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
       
        /// <summary>
        /// Queue filter called before a queue gets created or updated.
        /// The first parameter is the queue name for ServiceStack. 
        /// Azure queue name can be accessed in the queue description object.
        /// The queue description can be modified at this time.
        /// </summary>
        public Action<QueueDescription> CreateQueueFilter { get; set; }

        static AzureBusMessageFactory()
        {
            QueueNames.MqPrefix = "";
            QueueNames.TempMqPrefix = "tmp-";

            var originalQueueNameFn = QueueNames.ResolveQueueNameFn;
            QueueNames.ResolveQueueNameFn = (typeName, queueSuffix) =>
            {
                return originalQueueNameFn(
                    typeName, 
                    queueSuffix == ".dlq" ? ".inq/$deadletterqueue" : queueSuffix)
                    .ToLower();
            };
        }

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
            queues.Select(x => x).Each(q => MessagingFactory.CreateMessageReceiver(q).Purge());
        }

        public Task PurgeQueuesAsync(params string[] queues)
        {
            return Task.WhenAll(queues.Select(q => MessagingFactory.CreateMessageReceiver(q).PurgeAsync()));
        }

        public void DeleteQueue<T>() => DeleteQueues(QueueNames<T>.AllQueueNames);

        public Task DeleteQueueAsync<T>() => DeleteQueuesAsync(QueueNames<T>.AllQueueNames);

        public void DeleteQueues(params string[] queues)
        {
            queues.Select(x => x)
                .Where(q => !q.IsDeadLetterQueue())
                .Each(q => {
                    try {
                        NamespaceManager.DeleteQueue(q);
                    }
                    catch (MessagingEntityNotFoundException) { /* not present */ }
                  });
        }

        public Task DeleteQueuesAsync(params string[] queues)
        {
            return Task.WhenAll(queues.Select(x => x)
                .Where(q => !q.IsDeadLetterQueue())
                .Select(async q => {
                    try
                    {
                        await NamespaceManager.DeleteQueueAsync(q).ConfigureAwait(false);
                    }
                    catch (MessagingEntityNotFoundException) { /* not present */ }
                }));
        }

        public void Dispose()
        {
            MessagingFactory.Close();
        }
    }
}
