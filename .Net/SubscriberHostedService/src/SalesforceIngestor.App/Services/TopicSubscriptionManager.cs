using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Polly;
using PubSubApi;
using SalesforceIngestor.App.Config;
using SalesforceIngestor.App.Models;

namespace SalesforceIngestor.App.Services;

/// <summary>
/// Class to manage a subscription to a Salesforce Topic via the Pub/Sub API
/// </summary>
public class TopicSubscriptionManager
{
    private readonly string _topic;
    private readonly SubscriptionSettings _subscriptionSettings;
    private readonly ChannelWriter<EventMessage> _writer;
    private readonly ILogger<TopicSubscriptionManager> _logger;
    private readonly IReplayIdRepository _replayIdRepository;
    private readonly IMemoryCache _memoryCache;
    private AsyncDuplexStreamingCall<FetchRequest, FetchResponse>? _streamingCall;
    private readonly PubSub.PubSubClient _grpcClient;

    private const string ConsecutiveRetriesWithFailure = "consecutiveRetriesWithFailure";

    public TopicSubscriptionManager(string topic, SubscriptionSettings subscriptionSettings, ChannelWriter<EventMessage> writer, ILogger<TopicSubscriptionManager> logger, PubSub.PubSubClient grpcClient, IReplayIdRepository replayIdRepository, IMemoryCache memoryCache)
    {
        if (string.IsNullOrWhiteSpace(topic))
            throw new ArgumentNullException(nameof(topic));

        _topic = topic;
        _subscriptionSettings = subscriptionSettings ?? throw new ArgumentNullException(nameof(subscriptionSettings));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _grpcClient = grpcClient ?? throw new ArgumentNullException(nameof(grpcClient));
        _replayIdRepository = replayIdRepository ?? throw new ArgumentNullException(nameof(replayIdRepository));
        _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
    }
    
    /// <summary>
    /// Runs the service.  The salesforce fetch response stream tends to stop returning data after running for a while.
    /// Since this is meant to be a long running task to keep data in other data sources in sync, the work is done in
    /// a Polly retry policy to add resiliency.  A cancellation token source is maintained to cancel after an interval
    /// if a fetch response has need been returned via the response stream.  I've found that the API usually sends an
    /// empty fetch response at 60seconds though the documentation states a return will occur at 270 seconds.  The
    /// token timeout is configurable and can be set according to needs.  Once he timeout has been reached, Polly will
    /// retry, reestablishing the subscription.
    /// </summary>
    /// <param name="stoppingToken"></param>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        // Cache the schema for this topic once.
await CacheSchemaAsync().ConfigureAwait(false);
_logger.LogDebug("{Method} called for topic {TopicName}", nameof(RunAsync), _topic);

// Continue running until cancellation is requested.
while (!stoppingToken.IsCancellationRequested)
{
    // Create a fresh retry policy for each subscription cycle.
    // This policy’s internal retry count will start at zero every time.
    var retryPolicy = Policy
        .Handle<Exception>()
        .WaitAndRetryForeverAsync(
            (retryCount, context) =>
            {
                // Calculate delay based on configuration.
                return !_subscriptionSettings.UseAttemptMultiplier
                    ? _subscriptionSettings.SubscriptionRetryWait
                    : _subscriptionSettings.SubscriptionRetryWait * Math.Max(retryCount, 1);
            },
            (exception, retryCount, timeSpan, context) =>
            {
                _logger.LogDebug("Retry attempt {RetryCount} for topic {Topic}", retryCount, _topic);
                _logger.LogError(
                    exception,
                    "Error on topic {Topic}: {Message}. Will retry in {Delay}ms.",
                    _topic,
                    exception.Message,
                    timeSpan.TotalMilliseconds);
            });

    // Use a cancellation token source to enforce a fetch response timeout for this cycle.
    using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    fetchCts.CancelAfter(_subscriptionSettings.FetchResponseTimeout);

    try
    {
        // Execute the subscription operation. Note that each execution here
        // is independent and gets a fresh Polly retry count.
        await retryPolicy.ExecuteAsync(async ct =>
        {
            // SubscribeImplAsync will send a fetch request and process messages.
            // On success (i.e. when messages are received), this execution completes,
            // naturally resetting Polly’s internal retry counter.
            await SubscribeImplAsync(new Context("RetryContext"), ct, fetchCts).ConfigureAwait(false);
        }, stoppingToken).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
        // Graceful shutdown.
        break;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Subscription cycle for topic {Topic} failed", _topic);
        // Optionally add a short delay here before restarting the loop.
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
    }
}

_logger.LogDebug("{Method} finished for topic {TopicName}", nameof(RunAsync), _topic);
    }

    /// <summary>
    /// setup the subscription, send fetch request and read the response stream
    /// </summary>
    /// <param name="policyContext"></param>
    /// <param name="stoppingToken"></param>
    /// <param name="timeoutTokenSource"></param>
    private async Task SubscribeImplAsync(Context policyContext, CancellationToken stoppingToken, CancellationTokenSource timeoutTokenSource)
    {
        //initialize the subscription
        InitializeSubscription(stoppingToken);
        
        //get the last replay id
        var replayId = await _replayIdRepository.GetReplayIdAsync(_topic).ConfigureAwait(false);
        
        //send a new fetch request so messages will start streaming on the response stream
        await SendNewFetchRequestAsync(replayId).ConfigureAwait(false);

        //used for getting time between requests
        var sw = new Stopwatch();
        sw.Start();
        int numReceived = 0;

        //await messages on the response stream, call will cancel after the timeout has been hit
        await foreach (var message in _streamingCall!.ResponseStream.ReadAllAsync(cancellationToken: timeoutTokenSource.Token))
        {
            //reset the timeout since something was returned
            timeoutTokenSource.CancelAfter(_subscriptionSettings.FetchResponseTimeout);
            //there was a successful return so reset the consecutive failures counter 
            policyContext[ConsecutiveRetriesWithFailure] = 0;

            sw.Stop();
            _logger.LogDebug(
                "{Topic} returned with {Count} fetch responses. Time since last return {Ellapsed}ms",
                _topic, message.Events.Count, sw.ElapsedMilliseconds);
            sw.Restart();
            var lastReplayId = BinaryPrimitives.ReadInt64BigEndian(message.LatestReplayId.ToByteArray());

            //loop the events that were returned
            foreach (var consumerEvent in message.Events)
            {
                //write them to a channel for further processing
                await _writer.WriteAsync(new EventMessage(_topic, consumerEvent), stoppingToken).ConfigureAwait(false);
                numReceived++;
            }

            await _replayIdRepository.UpdateReplayIdAsync(_topic, lastReplayId).ConfigureAwait(false);
            _logger.LogDebug("Last replay id: {ReplayId} reported for topic {Topic}", lastReplayId, _topic);

            //if the numer requested has been received then send another fetch request
            if (_subscriptionSettings.FetchRequestCount == numReceived)
            {
                numReceived = 0;
                await SendNewFetchRequestAsync(lastReplayId)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// calls grpc service to set up the subscription
    /// </summary>
    /// <param name="stoppingToken"></param>
    private void InitializeSubscription(CancellationToken stoppingToken = default)
    {
        _streamingCall = _grpcClient.Subscribe(cancellationToken: stoppingToken);
    }
    
    /// <summary>
    /// sends the fetch request.  The will start messages being pushed to the client via the response stream
    /// A default value of -1 can be specified that will indicate to retreive only new events
    /// </summary>
    /// <param name="replayId"></param>
    /// <exception cref="InvalidOperationException"></exception>
    private async Task SendNewFetchRequestAsync(long replayId = Resources.DefaultReplayId)
    {
        if (_streamingCall?.RequestStream == null)
            throw new InvalidOperationException("The request stream has not been initialized");
        
        var req = new FetchRequest()
        {
            TopicName = _topic,
            NumRequested = _subscriptionSettings.FetchRequestCount
        };
        
        if (replayId == -1)
        {
            req.ReplayPreset = ReplayPreset.Latest;
        }
        else
        {
            //issues with endianess of the replay id.  Not sure why it isn't a long instead of a byte array, but
            //the replay id needs to be represented as BigEndian
            req.ReplayPreset = ReplayPreset.Custom;
            var converted = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(replayId) : replayId;
            req.ReplayId = ByteString.CopyFrom(BitConverter.GetBytes(converted));
        }

        _logger.LogDebug("Sending FetchRequest for {Name} with replay id: {ReplayId}", _topic, replayId);
        await _streamingCall.RequestStream.WriteAsync(req).ConfigureAwait(false);
    }

    /// <summary>
    /// retrieves the schema for the topic and stores in a memcache.  This is used later for deserialization.
    /// </summary>
    private async Task CacheSchemaAsync()
    {
        var topicInfo = await _grpcClient.GetTopicAsync(new TopicRequest() { TopicName = _topic })
            .ConfigureAwait(false);

        await _memoryCache.GetOrCreateAsync<SchemaInfo>(topicInfo.SchemaId, async entry => await _grpcClient.GetSchemaAsync(new SchemaRequest() { SchemaId = topicInfo.SchemaId })
            .ConfigureAwait(false)).ConfigureAwait(false);

    }
}
