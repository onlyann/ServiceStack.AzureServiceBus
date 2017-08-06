using System;
using ServiceStack.Messaging;
using Microsoft.ServiceBus.Messaging;

namespace ServiceStack.AzureServiceBus
{
    public class AzureBusMessageQueueClient : AzureBusMessageProducer, IMessageQueueClient
    {
        public AzureBusMessageQueueClient(AzureBusMessageFactory msgFactory) : base(msgFactory)
        {
        }

        public IMessage<T> CreateMessage<T>(object mqResponse)
        {
            if (mqResponse is BrokeredMessage brokeredMessage)
            {
                return brokeredMessage.ToMessage<T>();
            }

            return (IMessage<T>)mqResponse;
        }

        public IMessage<T> Get<T>(string queueName, TimeSpan? timeOut = null)
        {
            var brokeredMsg = GetMessage(queueName, timeOut);

            // need to keep track of queue name for Ack
            brokeredMsg.SetQueueName(queueName);

            return brokeredMsg.ToMessage<T>();
        }

        public IMessage<T> GetAsync<T>(string queueName) => Get<T>(queueName, TimeSpan.Zero);

        public string GetTempQueueName()
        {
            var queueDesc = msgFactory.NamespaceManager.CreateQueue(new QueueDescription(QueueNames.GetTempQueueName().ToSafeAzureQueueName())
            {
                AutoDeleteOnIdle = TimeSpan.FromMinutes(10),
                EnableExpress = true
            });

            return queueDesc.Path;
        }
        public void Ack(IMessage message)
        {
            var lockToken = Guid.Parse(message.Tag);
            var queueClient = GetMessageReceiver(message.QueueName() ?? message.ToInQueueName());
            queueClient.Complete(lockToken);
        }

        public void Nak(IMessage message, bool requeue, Exception exception = null)
        {
            var lockToken = Guid.Parse(message.Tag);
            var queueClient = GetMessageReceiver(message.QueueName() ?? message.ToInQueueName());

            if (requeue)
                queueClient.Abandon(lockToken);
            else
            {
                queueClient.DeadLetter(lockToken);
            }
        }

        public void Notify(string queueName, IMessage message)
        {
            Publish(queueName, message);
        }
    }
}
