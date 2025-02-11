# Introduction 
This is a hosted service designed for handling multiple subscriptions to events for Salesforce via the Pub/Sub API.  It is meant to be long-running and contains some components for resiliency via Polly.

# Operation
The application manages subscriptions to 1 or more Salesforce Topics.  The gPRC interactions are managed via .Net's gRPC client factory.  The application provides resiliency via Polly and the application is meant to provide stable, long-lived connection to Salesforce Topics for data synchronization.  The application has a library for managing access tokens with Salesforce and a simple service for handling replay id storage to the file system.  All messages received via th response stream are put on a .Net channel and are handled by another background service for further processing.

# Getting it running
Assuming this will be run against a test org, there are two config vals that need to be specified.  The topics that need to be subscribed to the parameters necessary for authentication need to be set.  This app assumes they will be set in both `appsettings.json` and in `secrets.json` as they can be set without changing code in the repository.

This code assumes two things:
1. The user has configured OAuth 2.0 authentication and authorization on their Salesforce environment (sandbox or non-Sandbox) following either of the two URLs at the end of this file.
1. The configuration of one of two different authentication methods with this application - either with certificates & using the JWT Bearer Flow **OR** using the Authorization Code & Credentials Flow for Private Clients authentication to Salesforce.  

Refer to the `SalesforceIngestor.SalesforceAuthentication` project for more details. 

The application uses the user secret `sf-ingestor-poc`.  The secret can be initialized and values for authentication can be set within secrets.json file.  With the below set the library will manage getting/caching tokens for Salesforce.  By default tokens will be cached for 1hr.

# Set Up Secrets for Sensitive Data, App Configuration & Env Configuration
Follow these steps to set up the Secrets configuration for your local development environment.  These steps are based on this article: https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets?view=aspnetcore-7.0&tabs=windows#register-the-user-secrets-configuration-source

1. Create a directory and secrets.json file on your local machine
   - On MacOS/Linux: `~/.microsoft/usersecrets/sf-ingestor-poc/secrets.json`
   - On windows `%APPDATA%\Microsoft\UserSecrets\sf-ingestor-poc\secrets.json`
2. Edit secrets.json and copy/paste with this data:

```
{
  "oAuthSecrets": {
    "privateKey": "<put private key here if using the Certificate JWT Bearer authentication flow.  Leave this as an empty string if using Auth Code & Credentials flow.  It is best to base64 encode.  If it is base 64 encoded make sure privateKeyIsBase64Encoded is true>",
    "privateKeyPassword": "<put private key password here if using the Certificate JWT Bearer authentication flow. Leave this as an empty string if using Auth Code & Credentials flow. If the PK is base 64 encoded then make sure privateKeyIsPasswordProtected is true>",
    "clientId": "<The client id for the connected app in Salesforce>",
    "userName": "<The salesforce user to login as>",
    "password": "<The salesforce password for the respective user>",
    "clientSecret": "<The salesforce client secret if using the Auth Code & Credentials flow.  If using the Certificate JWT Bearer flow, leave this as an empty string>",
    "tenantId": "<Your salesforce tenant ID, sandbox or non-sandbox IDs can be used.>"
  }
}

```

3. Edit appsettings.json and alter this section according to which authentication flow has been selected


```
{
    "salesforceAuthenticationSettings": {
        "privateKeyIsPasswordProtected": false,
        "privateKeyIsBase64Encoded": false,     
        "oAuthGrantType": "<specify either "bearer" if using the Certificate JWT Bearer flow or "password" if using the Auth Code & Credentials flow>"        
      }
}

```

*Storing the private key for certificate authentication **OR** any oAuth secrets in the appsettings.json file should be avoided*

4.  Edit appsettings.json and alter this section to specify which topics the client should subscribe 
    - The application can manage connections to more than 1 subscription at a time.  
    - Edit the array and each entry will be subscribed to, for example "/data/AccountChangeEvent"

```

{
    "subscriptions": {
        "topics": ["/event/<put the Salesforce schema name here>"]        
    }
}

```

5. A note about the loginUrl in the appsettings.json file
   - If you are using the Auth Code & Credentials flow and Salesforce sandbox environment and this environment has been configured to provide authentication as a sole authentication source (e.g. not integrated with your company/org's authentication provider(s)) you must use the `https://<your sandbox name>.sandbox.my.salesforce.com` URL when requesting an authorization token.
   - The `https://test.salesforce.com` URL may be sufficient for other authentication flows for **NON PRODUCTION** salesforce environments.

6. Set up an ENV variable either in your IDE or on your local machine
   - The variable name should be `DOTNET_ENVIRONMENT` and the value should be `Development`
   - If you are using Visual Studio for MacOS or Linux you will want to launch the IDE from your shell.  This will expose/publish your environment variables to the IDE for debug runtime.  

*This sample was specifically tested with Platform Events.  CDC events have been tested using the /data/AccountChangeEvent topic*

# Articles of Interest Relative to Salesforce OAuth Flows & Support

Salesforce OAuth 2.0 JWT Bearer Flow: https://help.salesforce.com/s/articleView?id=sf.remoteaccess_oauth_jwt_flow.htm&type=5

For more information about the Authorization Code & Credentials Flow for Private Clients, please reference this article: https://help.salesforce.com/s/articleView?id=sf.remoteaccess_authorization_code_credentials_flow.htm&type=5