using System;
using ServiceStack.Messaging;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
using System.Linq;
using System.Threading.Tasks;

namespace ServiceStack.AzureServiceBus
{
    public class AzureBusMessageFactory : IMessageFactory
    {
        public NamespaceManager NamespaceManager { get; private set; }

        public MessagingFactory MessagingFactory { get; private set; }

        public AzureBusMessageFactory(string connectionString)
        {
            NamespaceManager = NamespaceManager.CreateFromConnectionString(connectionString);
            MessagingFactory = MessagingFactory.CreateFromConnectionString(connectionString);
        }

        public AzureBusMessageFactory(NamespaceManager namespaceManager, MessagingFactory messagingFactory)
        {
            NamespaceManager = namespaceManager;
            MessagingFactory = messagingFactory;
        }

        public virtual IMessageProducer CreateMessageProducer() => new AzureBusMessageProducer(this);

        public virtual IMessageQueueClient CreateMessageQueueClient() => new AzureBusMessageQueueClient(this);

        public void PurgeQueue<T>() => PurgeQueues(QueueNames<T>.AllQueueNames);

        public Task PurgeQueueAsync<T>() => PurgeQueuesAsync(QueueNames<T>.AllQueueNames);

        public void PurgeQueues(params string[] queues)
        {
            queues.Select(x => x.ToSafeAzureQueueName())
                .Where(q => !q.IsDeadLetterQueue())
                .Each(q => MessagingFactory.CreateMessageReceiver(q).Purge());
        }

        public Task PurgeQueuesAsync(params string[] queues)
        {
            return Task.WhenAll(queues.Select(x => x.ToSafeAzureQueueName())
                .Where(q => !q.IsDeadLetterQueue())
                .Select(q => MessagingFactory.CreateMessageReceiver(q).PurgeAsync()));
        }

        public void Dispose()
        {
            MessagingFactory.Close();
        }
    }
}
