using Microsoft.ServiceBus.Messaging;
using ServiceStack.Messaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace ServiceStack.AzureServiceBus
{
    public class AzureBusMessageProducer : IMessageProducerExtended, IOneWayClient
    {
        protected delegate void PublishAllDelegate(IEnumerable<IMessage> messages);

        static readonly Dictionary<Type, PublishAllDelegate> CacheFn
            = new Dictionary<Type, PublishAllDelegate>();

        protected ConcurrentDictionary<string, MessageSender> MessageSenders = new ConcurrentDictionary<string, MessageSender>();
        protected ConcurrentDictionary<string, MessageReceiver> MessageReceivers = new ConcurrentDictionary<string, MessageReceiver>();

        public virtual MessageReceiver GetMessageReceiver(string queueName) => 
            MessageReceivers.GetOrAdd(queueName, key => MessagingFactory.CreateMessageReceiver(key.ToSafeAzureQueueName()));

        public virtual MessageSender GetMessageSender(string queueName) =>
           MessageSenders.GetOrAdd(queueName, key => MessagingFactory.CreateMessageSender(key.ToSafeAzureQueueName()));

        protected static HashSet<string> Queues = new HashSet<string>();

        protected readonly AzureBusMessageFactory msgFactory;

        public MessagingFactory MessagingFactory { get => msgFactory.MessagingFactory; }

        public AzureBusMessageProducer(AzureBusMessageFactory msgFactory)
        {
            msgFactory.ThrowIfNull(nameof(msgFactory));
            this.msgFactory = msgFactory;
        }

        public virtual void Publish<T>(T messageBody)
        {
            if (messageBody is IMessage message)
            {
                Publish(message.ToInQueueName(), message);
            }
            else
            {
                Publish(new Message<T>(messageBody));
            }
        }

        public virtual void Publish<T>(IMessage<T> message) => Publish(message.ToInQueueName(), message);

        public virtual void PublishAll<T>(IEnumerable<T> messageBodyList)
        {
            if (typeof(IMessage).IsAssignableFrom(typeof(T)))
            {
                SendAllOneWay(messageBodyList.Cast<object>());
            }
            else
            {
                PublishAll(messageBodyList.Select(body => new Message<T>(body) as IMessage<T>));
            }
        }

        public virtual void PublishAll<T>(IEnumerable<IMessage<T>> messages)
        {
            var byPriorityMessages = messages.GroupBy(m => m.IsPriority());
            foreach (var group in byPriorityMessages)
            {
                PublishAll(group.Key ? QueueNames<T>.Priority : QueueNames<T>.In, group);
            }
        }

        public virtual void Publish(string queueName, IMessage message)
        {
            var brokeredMessage = message.ToBrokeredMessage();
            Publish(queueName, brokeredMessage);
        }

        public virtual void PublishAll(string queueName, IEnumerable<IMessage> messages)
        {
            var brokeredMessages = messages.Select(x => x.ToBrokeredMessage());
            PublishAll(queueName, brokeredMessages);
        }

        public virtual void PublishAll(string queueName, IEnumerable<BrokeredMessage> messages)
        {
            EnsureQueueRegistered(queueName);

            var queueClient = GetMessageSender(queueName);
            queueClient.SendBatch(messages);
        }

        public virtual void EnsureQueueRegistered(string queueName)
        {
            if (!Queues.Contains(queueName))
            {
                msgFactory.NamespaceManager.RegisterQueueByName(queueName);
                Queues.Add(queueName);
            }
        }

        public virtual void Publish(string queueName, BrokeredMessage message)
        {
            EnsureQueueRegistered(queueName);

            var queueClient = GetMessageSender(queueName);
            queueClient.Send(message);
        }

        public void SendOneWay(object requestDto) => 
            Publish(MessageFactory.Create(requestDto));

        public void SendOneWay(string queueName, object requestDto) => 
            Publish(queueName, MessageFactory.Create(requestDto));

        public void SendAllOneWay(IEnumerable<object> requests)
        {
            if (requests == null) return;

            var messages = requests.Select(MessageFactory.Create).ToList();
            var reqType = messages.FirstOrDefault()?.GetType().GenericTypeArguments.FirstOrDefault();
            if (reqType == null)
            {
                foreach (var m in messages)
                {
                    Publish(m);
                }
            }
            else
            {
                GetOrCreateDelegate(reqType)(messages);
            }
        }

        protected virtual PublishAllDelegate GetOrCreateDelegate(Type reqType)
        {
            PublishAllDelegate publishAllDelegate;
            lock (CacheFn)
            {
                if (CacheFn.TryGetValue(reqType, out publishAllDelegate))
                {
                    return publishAllDelegate;
                }
            }

            var mi = typeof(AzureBusMessageProducer)
                .GetMethods()
                .First(m => m.Name == nameof(PublishAll) && m.IsGenericMethod);

            publishAllDelegate = (PublishAllDelegate)mi.MakeGenericMethod(reqType).CreateDelegate(
                typeof(PublishAllDelegate));

            lock (CacheFn) CacheFn[reqType] = publishAllDelegate;

            return publishAllDelegate;
        }

        public virtual BrokeredMessage GetMessage(string queueName, TimeSpan? serverWaitTime = null)
        {
            EnsureQueueRegistered(queueName);

            var queueClient = GetMessageReceiver(queueName);
            return queueClient.Receive(serverWaitTime ?? TimeSpan.MaxValue);
        }

        public void Dispose()
        {
            foreach (var msgSender in MessageSenders.Values)
            {
                msgSender.Close();
            }

            foreach(var msgReceiver in MessageReceivers.Values)
            {
                msgReceiver.Close();
            }

            MessageSenders.Clear();
            MessageReceivers.Clear();
        }
    }
}
