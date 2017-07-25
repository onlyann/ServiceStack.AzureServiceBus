using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
using ServiceStack.Messaging;
using ServiceStack.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
                RetryAttempts = brokeredMessage.DeliveryCount - 1
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

                if (message.Meta == null)
                    message.Meta = new Dictionary<string, string>();

                message.Meta[entry.Key] = (string) entry.Value;
            }

            return message;
        }

        public static void RegisterQueueByName(this NamespaceManager namespaceMgr, string queueName)
        {
            if (queueName.IsDeadLetterQueue())
                // Dead letter Queues is created in Azure Service Bus
                return;

            namespaceMgr.RegisterQueue(queueName);
        }

        public static bool IsDeadLetterQueue(this string queueName)
        {
            return queueName != null && queueName.EndsWith(".dlq");
        }

        public static void RegisterQueue(this NamespaceManager namespaceMgr, string queueName)
        {
            if (!QueueNames.IsTempQueue(queueName))
            {
                try
                {
                    namespaceMgr.CreateQueue(queueName.ToSafeAzureQueueName());
                }
                catch (MessagingEntityAlreadyExistsException)
                { 
                    // queue already created 
                }
            }
        }

        public static void Purge(this MessageReceiver msgReceiver, int maxBatchSize = 10)
        {
            try
            {
                while (msgReceiver.Peek() != null)
                {
                    var messages = msgReceiver.ReceiveBatch(maxBatchSize, TimeSpan.Zero);
                    if (messages != null)
                        msgReceiver.CompleteBatch(messages.Select(m => m.LockToken));
                }
            }
            catch (MessagingEntityNotFoundException)
            {
                // ignore when queue/topic does not exist
            }
        }

        public static async Task PurgeAsync(this MessageReceiver msgReceiver, int maxBatchSize = 10)
        {
            try
            {
                while ((await msgReceiver.PeekAsync().ConfigureAwait(false)) != null)
                {
                    var messages = await msgReceiver.ReceiveBatchAsync(maxBatchSize, TimeSpan.Zero).ConfigureAwait(false);
                    var lockTokens = messages != null ? messages.Select(m => m.LockToken).ToList() : null;

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

        public static void PurgeQueue<T>(this IMessageFactory msgFactory)
        {
            var azureMgFactory = msgFactory as AzureBusMessageFactory;
            if (azureMgFactory == null) throw new ArgumentException(nameof(msgFactory), $"the object must be assignable to {typeof(AzureBusMessageFactory)}");
            azureMgFactory.PurgeQueue<T>();
        }

        public static Task PurgeQueueAsync<T>(this IMessageFactory msgFactory)
        {
            var azureMgFactory = msgFactory as AzureBusMessageFactory;
            if (azureMgFactory == null) throw new ArgumentException(nameof(msgFactory), $"the object must be assignable to {typeof(AzureBusMessageFactory)}");
            return azureMgFactory.PurgeQueueAsync<T>();
        }

        public static void RegisterQueues(this NamespaceManager namespaceMgr, QueueNames queueNames)
        {
            namespaceMgr.RegisterQueue(queueNames.In);
            namespaceMgr.RegisterQueue(queueNames.Priority);
            namespaceMgr.RegisterQueue(queueNames.Out);
            // queueNames.Dlq is created by Azure Service Bus
        }

        public static string ToSafeAzureQueueName(this string queueName)
        {
            // valid characters are alpha numeric, period, hyphen and underscore
            return Regex.Replace(queueName, @"[^\w\._-]", "-", RegexOptions.None); // replace invalid chars with hyphen
        }

        public static bool IsPriority(this IMessage message) => message.Priority > 0;
    }
}
