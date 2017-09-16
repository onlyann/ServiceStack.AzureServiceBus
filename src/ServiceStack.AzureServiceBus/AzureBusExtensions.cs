using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
using ServiceStack.Messaging;
using ServiceStack.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ServiceStack.AzureServiceBus
{
    public static class AzureBusExtensions
    {
        public static BrokeredMessage ToBrokeredMessage(this IMessage message)
        {
            var brokeredMessage = new BrokeredMessage(new MemoryStream(message.Body.ToJson().ToUtf8Bytes()), true);
            brokeredMessage.MessageId = message.Id.ToString();

            brokeredMessage.ContentType = MimeTypes.Json;

            if (message.Body != null)
            {
                brokeredMessage.Label = message.Body.GetType().Name;
            }

            if (message.ReplyTo != null)
            {
                brokeredMessage.ReplyTo = message.ReplyTo;
            }

            if (message.ReplyId != null)
            {
                brokeredMessage.CorrelationId = message.ReplyId.Value.ToString();
            }

            brokeredMessage.Properties["CreatedDate"] = message.CreatedDate;

            if (message.Priority != 0)
            {
                brokeredMessage.Properties["Priority"] = message.Priority;
            }

            if (message.Error != null)
            {
                brokeredMessage.Properties["Error"] = message.Error.ToJson();
            }

            if (message.Meta != null)
            {
                foreach (var key in message.Meta.Keys)
                {
                    if (key == "QueueName") continue;

                    brokeredMessage.Properties[key] = message.Meta[key];
                }
            }

            return brokeredMessage;
        }

        public static T GetOrDefault<T>(this IDictionary<string, object> dic, string key, T defaultVal = default(T))
        {
            return dic.TryGetValue(key, out object val) ? (T)val : defaultVal;
        }

        public static T GetOrDefault<T>(this IDictionary<string, object> dic, string key, Func<T> defaultValFn)
        {
            return dic.TryGetValue(key, out object val) ? (T)val : defaultValFn();
        }

        internal static void SetQueueName(this BrokeredMessage brokeredMessage, string queueName)
        {
            brokeredMessage.Properties["QueueName"] = queueName;
        }

        internal static void RemoveQueueName(this BrokeredMessage brokeredMessage)
        {
            brokeredMessage.Properties.Remove("QueueName");
        }

        internal static string QueueName(this IMessage message)
        {
            if (message?.Meta == null) return null;
            message.Meta.TryGetValue("QueueName", out string queueName);
            return queueName;
        }

        public static IMessage<T> ToMessage<T>(this BrokeredMessage brokeredMessage)
        {
            if (brokeredMessage == null)
                return null;

            var props = brokeredMessage.Properties;
            T body;

            using (var stream = brokeredMessage.GetBody<Stream>())
            {
                body = JsonSerializer.DeserializeFromReader<T>(new StreamReader(stream, Encoding.UTF8));
            }

            var message = new Message<T>(body)
            {
                Id = brokeredMessage.MessageId != null ? Guid.Parse(brokeredMessage.MessageId) : new Guid(),
                CreatedDate = props.GetOrDefault("CreatedDate", () => brokeredMessage.EnqueuedTimeUtc),
                Priority = props.GetOrDefault<long>("Priority"),
                ReplyTo = brokeredMessage.ReplyTo,
                Tag = brokeredMessage.LockToken.ToString(),
                RetryAttempts = brokeredMessage.DeliveryCount - 1,
                Meta = new Dictionary<string, string>()
            };

            if (brokeredMessage.CorrelationId != null)
            {
                message.ReplyId = Guid.Parse(brokeredMessage.CorrelationId);
            }

            foreach (var entry in props)
            {
                if (entry.Key == "CreatedDate" || entry.Key == "Priority") continue;

                if (entry.Key == "Error")
                {
                    var errors = entry.Value;
                    if (errors != null)
                    {
                        message.Error = ((string)errors).FromJson<ResponseStatus>();
                    }

                    continue;
                }

                message.Meta[entry.Key] = (string) entry.Value;
            }

            return message;
        }

        public static void RegisterQueueByName(
            this NamespaceManager namespaceMgr, 
            string queueName, 
            Action<QueueDescription> createQueueFilter = null)
        {
            if (queueName.IsDeadLetterQueue())
                // Dead letter Queues is created in Azure Service Bus
                return;

            namespaceMgr.RegisterQueue(queueName, createQueueFilter);
        }

        public static bool IsDeadLetterQueue(this string queueName)
        {
            return queueName != null && queueName.EndsWithInvariant("/$deadletterqueue");
        }

        private static QueueDescription TryGetQueue(this NamespaceManager namespaceMgr, string path)
        {
            // much faster than catch exception with GetQueue
            return namespaceMgr.GetQueues($"startswith(path, '{path}') eq true").FirstOrDefault();
        }

        private static async Task<QueueDescription> TryGetQueueAsync(this NamespaceManager namespaceMgr, string path)
        {
            // much faster than catch exception with GetQueue
            return (await namespaceMgr.GetQueuesAsync($"startswith(path, '{path}') eq true").ConfigureAwait(false)).FirstOrDefault();
        }

        public static void RegisterQueue(
            this NamespaceManager namespaceMgr, 
            string queueName, 
            Action<QueueDescription> createQueueFilter = null)
        {
            if (QueueNames.IsTempQueue(queueName)) return;

            var queueDesc = namespaceMgr.TryGetQueue(queueName);
            var queueExists = queueDesc != null;

            if (!queueExists)
                queueDesc = new QueueDescription(queueName);

            bool hasQueueDefChanged = false;

            if (createQueueFilter != null)
            {
                var sourceQueueDef = queueExists ? queueDesc.ConvertTo<QueueDefinitionProps>() : null;
                createQueueFilter.Invoke(queueDesc);
                hasQueueDefChanged = queueExists && sourceQueueDef.EqualsTo(queueDesc.ConvertTo<QueueDefinitionProps>());
            }

            if (!queueExists)
                namespaceMgr.CreateQueue(queueDesc);
            else if (hasQueueDefChanged)
                namespaceMgr.UpdateQueue(queueDesc);
        }

        public static async Task RegisterQueueAsync(
            this NamespaceManager namespaceMgr,
            string queueName,
            Action<QueueDescription> createQueueFilter = null)
        {
            if (QueueNames.IsTempQueue(queueName)) return;

            var queueDesc = await namespaceMgr.TryGetQueueAsync(queueName);
            var queueExists = queueDesc != null;

            if (!queueExists)
                queueDesc = new QueueDescription(queueName);

            bool hasQueueDefChanged = false;

            if (createQueueFilter != null)
            {
                var sourceQueueDef = queueExists ? queueDesc.ConvertTo<QueueDefinitionProps>() : null;
                createQueueFilter.Invoke(queueDesc);
                hasQueueDefChanged = queueExists && !sourceQueueDef.EqualsTo(queueDesc.ConvertTo<QueueDefinitionProps>());
            }

            if (!queueExists)
                await namespaceMgr.CreateQueueAsync(queueDesc);
            else if (hasQueueDefChanged)
                await namespaceMgr.UpdateQueueAsync(queueDesc);
        }

        public static async Task PurgeAsync(this MessageReceiver msgReceiver, int maxBatchSize = 10)
        {
            try
            {
                while ((await msgReceiver.PeekAsync().ConfigureAwait(false)) != null)
                {
                    var messages = await msgReceiver.ReceiveBatchAsync(maxBatchSize, TimeSpan.Zero).ConfigureAwait(false);
                    var lockTokens = messages?.Select(m => m.LockToken).ToList();

                    if (!lockTokens.IsEmpty())
                    {
                        await msgReceiver.CompleteBatchAsync(lockTokens).ConfigureAwait(false);
                    }
                        
                }
            }
            catch (MessagingEntityNotFoundException)
            {
                // ignore when queue/topic does not exist
            }
        }

        private static AzureBusMessageFactory ToAzureMessageFactoryOrThrow(this IMessageFactory msgFactory, string paramName)
        {
            var azureMgFactory = msgFactory as AzureBusMessageFactory;
            if (azureMgFactory == null) throw new ArgumentException(paramName, $"the object must be assignable to {typeof(AzureBusMessageFactory)}");
            return azureMgFactory;
        }

        public static Task PurgeQueuesAsync(this IMessageFactory msgFactory, params string[] queues)
        {
            return msgFactory
                .ToAzureMessageFactoryOrThrow(nameof(msgFactory))
                .PurgeQueuesAsync(queues);
        }

        public static Task PurgeQueueAsync<T>(this IMessageFactory msgFactory)
        {
            return msgFactory
                .ToAzureMessageFactoryOrThrow(nameof(msgFactory))
                .PurgeQueueAsync<T>();
        }

        public static Task DeleteQueueAsync<T>(this IMessageFactory msgFactory)
        {
            return msgFactory
                .ToAzureMessageFactoryOrThrow(nameof(msgFactory))
                .DeleteQueueAsync<T>();
        }

        public static Task RegisterQueuesAsync(
            this NamespaceManager namespaceMgr,
            QueueNames queueNames,
            Action<QueueDescription> createQueueFilter = null)
        {
            return RegisterQueuesAsync(namespaceMgr, new[] { queueNames.In, queueNames.Out, queueNames.Priority }, createQueueFilter);
            // queueNames.Dlq is created by Azure Service Bus
        }

        public static Task RegisterQueuesAsync(
            this NamespaceManager namespaceMgr,
            IEnumerable<string> queueNames,
            Action<QueueDescription> createQueueFilter = null)
        {
            return Task.WhenAll(queueNames.Select(x => namespaceMgr.RegisterQueueAsync(x, createQueueFilter)));
        }

        public static Task RegisterQueuesAsync<T>(
            this NamespaceManager namespaceMgr,
            Action<QueueDescription> createQueueFilter = null)
        {
            return namespaceMgr.RegisterQueuesAsync(new QueueNames(typeof(T)), createQueueFilter);
        }

        public static bool IsPriority(this IMessage message) => message.Priority > 0;
    }
}
