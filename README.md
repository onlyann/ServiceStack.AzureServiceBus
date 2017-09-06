# ServiceStack.AzureServiceBus

[![Build status](https://ci.appveyor.com/api/projects/status/2c1ackhg6rloriok/branch/master?svg=true)](https://ci.appveyor.com/project/onlyann/servicestack-azureservicebus/branch/master)
[![Nuget](https://img.shields.io/nuget/v/ServiceStack.AzureServiceBus.svg)](https://www.nuget.org/packages/ServiceStack.AzureServiceBus/)

This adds Azure Service Bus [MQ Server option](http://docs.servicestack.net/messaging) for the excellent [ServiceStack](https://github.com/serviceStack/serviceStack) framework.

As it relies on [WindowsAzure.ServiceBus](https://www.nuget.org/packages/WindowsAzure.ServiceBus/) Nuget package, it requires .Net Framework 4.5 Full Profile.

This is inspired from the [Rabbit MQ implementation](http://docs.servicestack.net/rabbit-mq) and supports similar features (most of them are out-of-the-box ServiceStack features irrespective of the MQ Server option):
- Messages with no responses are sent to `.outq` queue
- Messages with responses are sent to the response `.inq` queue
- Responses from messages with ReplyTo are published to that address
- Messages with exceptions are re-tried then published to the respective [dead-letter queue](https://docs.microsoft.com/en-us/azure/service-bus-messaging/service-bus-dead-letter-queues)
- OneWay HTTP requests are published to MQ then executed
- OneWay MQ and HTTP Service Clients are Substitutable

## Adding Azure Service Bus MQ support to ServiceStack

[Create a Service Bus namespace](https://docs.microsoft.com/en-us/azure/service-bus-messaging/service-bus-create-namespace-portal) in Azure and obtain the connection string from the Shared Access policies. 

Install the package from Nuget.org
```
Install-Package ServiceStack.AzureServiceBus
```

Register Azure Service Bus MQ Server in your ServiceStack AppHost:

```
public override void Configure(Container container) 
{
    // ...

    // register the Azure MQ Server
    container.Register<IMessageService>(c => new AzureBusServer("Endpoint=sb://YOUR_SB_NAMESPACE.servicebus.windows.net/;SharedAccessKeyName=YOUR_ACCESS_KEY;SharedAccessKey=****"));

    var mqServer = container.Resolve<IMessageService>();

    mqServer.RegisterHandler<Hello>(ExecuteMessage);
    mqServer.Start();
}
```

## Azure Service Bus MQ Features

The [AzureBusServer](src\ServiceStack.AzureServiceBus\AzureBusServer.cs) has the configuration options:
- `int` **RetryCount** - How many times a message should be retried before sending to the DLQ.
- `string` **connectionString** - The connection string to the Azure Service Bus namespace
- `IMessageFactory` **MessageFactory** - the MQ Message Factory used by this MQ Server
- `Action<QueueDescription>` **CreateQueueFilter** - A filter to customize the options Azure Queues are created/updated with.
- `Action<string, BrokeredMessage>` **GetMessageFilter** - Called every time a message is received.
- `Action<string, BrokeredMessage, IMessage>` **PublishMessageFilter** - Called every time a message gets published.

As an alternative to a connection string, you can pass an instance of `AzureBusMessageFactory` to the `AzureBusServer` constructor and provide your own [NamespaceManager](https://docs.microsoft.com/en-us/dotnet/api/microsoft.servicebus.namespacemanager?redirectedfrom=MSDN&view=azureservicebus-4.1.1#microsoft_servicebus_namespacemanager) and [MessagingFactory](https://docs.microsoft.com/en-us/dotnet/api/microsoft.servicebus.messaging.messagingfactory?view=azureservicebus-4.1.1).

Starting the MQ Server will create up to 2 threads for each handler, one to listen to the Message Inbox `{RequestDto}.inq` and another to listen on the Priority Queue located at `{RequestDto}.priorityq`.

> Queue names are limited to alphanumeric, period, hyphen and underscore in Azure Service Bus. As such, the default prefixes from [QueueNames](https://github.com/ServiceStack/ServiceStack/blob/master/src/ServiceStack.Interfaces/Messaging/QueueNames.cs) and the queue name resolver get overriden by `AzureBusMessageFactory`. The deadletter queue name resolves to `RequestDto.inq/$deadletterqueue`. 
> Finally, note that queue names are case-insensitive in Azure Service Bus.

By default, only 1 thread is allocated to handle each message type, but like the other MQ Servers is easily configurable at registration:

```
mqServer.RegisterHandler<Hello>(m => { .. }, noOfThreads:4);
```

> Behind the scenes, it delegates the work to Azure Service Bus [event-driven message pump](https://docs.microsoft.com/en-us/dotnet/api/microsoft.servicebus.messaging.messagereceiver.onmessage?view=azureservicebus-4.1.1#Microsoft_ServiceBus_Messaging_MessageReceiver_OnMessage_System_Action_Microsoft_ServiceBus_Messaging_BrokeredMessage__Microsoft_ServiceBus_Messaging_OnMessageOptions_).

### Filters

#### Create Queue Filter

To modify the options a queue gets created with, provide a `CreateQueueFilter` filter and modify the `QueueDescription`.

For instance, to change the default TTL and have messages expire automatically after 2 minutes:
```
 container.Register<IMessageService>(c => new AzureBusServer(ConnectionString) {
     CreateQueueFilter = (description) => {
                    description.DefaultMessageTimeToLive = TimeSpan.FromMinutes(2);
                }
 });
```

If the queue already exists, any change to the queue options will result in an update.

#### Message Filters

There are optional `PublishMessageFilter` and `GetMessageFilter` callbacks which can be used to intercept outgoing and incoming messages. The Type name of the message body that was published is available in the `Label` property, e.g:

```
mqServer.PublishMessageFilter = (queueName, brokeredMsg, message) => {
    brokeredMsg.Properties["AppId"] = "app:{0}".Fmt(queueName);
};

mqServer.GetMessageFilter = (queueName, brokeredMsg) => {
    receivedMsgType = brokeredMsg.Label; //automatically added by AzureBusMessageProducer
    receivedMsgApp = brokeredMsg.Properties["AppId"] as string;

    receivedMsgType.Print(); // Hello
    receivedMsgApp.Print(); // app:Hello.In
};

using (var mqClient = mqServer.CreateMessageQueueClient())
{
    mqClient.Publish(new Hello { Name = "Bright blue sky" });
}
```

Note that `brokeredMsg` parameter of `GetMessageFilter` when explicitly retrieving a message results in a timeout.

## Upcoming Features

- [ ] queue whitelisting
- [ ] request and response global filter
- [ ] error handler