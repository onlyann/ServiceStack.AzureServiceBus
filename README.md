# ServiceStack.AzureServiceBus

[![Build status](https://ci.appveyor.com/api/projects/status/2c1ackhg6rloriok/branch/master?svg=true)](https://ci.appveyor.com/project/onlyann/servicestack-azureservicebus/branch/master)


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

As an alternative to the connection string, you can pass an instance of `AzureBusMessageFactory` to the `AzureBusServer` constructor allowing you to provide your own [NamespaceManager](https://docs.microsoft.com/en-us/dotnet/api/microsoft.servicebus.namespacemanager?redirectedfrom=MSDN&view=azureservicebus-4.1.1#microsoft_servicebus_namespacemanager) and [MessagingFactory](https://docs.microsoft.com/en-us/dotnet/api/microsoft.servicebus.messaging.messagingfactory?view=azureservicebus-4.1.1)

Starting the MQ Server will create up to 2 threads for each handler, one to listen to the Message Inbox `mq-{RequestDto}.inq` and another to listen on the Priority Queue located at `mq-{RequestDto}.priorityq`.

> Queue names are limited to alphanumeric, period, hyphen and underscore in Azure Service Bus. As such, names from the ServiceStack utility [QueueNames<T>](https://github.com/ServiceStack/ServiceStack/blob/master/src/ServiceStack.Interfaces/Messaging/QueueNames.cs) will differ from the actual queue name.

By default, only 1 thread is allocated to handle each message type, but like the other MQ Servers is easily configurable at registration:

```
mqServer.RegisterHandler<Hello>(m => { .. }, noOfThreads:4);
```

> Behind the scenes, it delegates the work to Azure Service Bus [event-driven message pump](https://docs.microsoft.com/en-us/dotnet/api/microsoft.servicebus.messaging.messagereceiver.onmessage?view=azureservicebus-4.1.1#Microsoft_ServiceBus_Messaging_MessageReceiver_OnMessage_System_Action_Microsoft_ServiceBus_Messaging_BrokeredMessage__Microsoft_ServiceBus_Messaging_OnMessageOptions_).

## Upcoming Features

- [ ] queue creation filter
- [ ] queue whitelisting
- [ ] request and response global filter
- [ ] error handler