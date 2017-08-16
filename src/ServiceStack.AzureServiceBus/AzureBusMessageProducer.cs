using Microsoft.ServiceBus.Messaging;
using ServiceStack.Messaging;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace ServiceStack.AzureServiceBus
{
    public class AzureBusMessageProducer : IMessageProducerExtended, IOneWayClient
    {
        static readonly MethodInfo publishAllOfIMessageGeneric = typeof(AzureBusMessageProducer)
                .GetMethods()
                .First(m =>
                {
                    var args = m.GetParameters();
                    return m.Name == nameof(PublishAll) &&
                    m.IsGenericMethod && args.Length == 1 &&
                    args[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
                    args[0].ParameterType.GetGenericArguments()[0].IsGeneric() &&
                    args[0].ParameterType.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(IMessage<>);
                });

        static readonly Dictionary<Type, Action<IEnumerable<IMessage>>> CacheFn
            = new Dictionary<Type, Action<IEnumerable<IMessage>>>();

        protected ConcurrentDictionary<string, MessageSender> MessageSenders = new ConcurrentDictionary<string, MessageSender>();
        protected ConcurrentDictionary<string, MessageReceiver> MessageReceivers = new ConcurrentDictionary<string, MessageReceiver>();

        public virtual MessageReceiver GetMessageReceiver(string queueName) => 
            MessageReceivers.GetOrAdd(queueName, key => MessagingFactory.CreateMessageReceiver(key));

        public virtual MessageSender GetMessageSender(string queueName) =>
           MessageSenders.GetOrAdd(queueName, key => MessagingFactory.CreateMessageSender(key));

        protected static HashSet<string> Queues = new HashSet<string>();

        protected readonly AzureBusMessageFactory msgFactory;

        public MessagingFactory MessagingFactory { get => msgFactory.MessagingFactory; }

        /// <summary>
        /// Filter called every time a message is received.
        /// The filter can also be called with a null message when the get message
        /// results in a timeout.
        /// </summary>
        public Action<string, BrokeredMessage> GetMessageFilter { get; set; }

        /// <summary>
        /// Filter called every time before a message gets published.
        /// </summary>
        public Action<string, BrokeredMessage, IMessage> PublishMessageFilter { get; set; }

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
            brokeredMessage.RemoveQueueName();

            PublishMessageFilter?.Invoke(queueName, brokeredMessage, message);

            Publish(queueName, brokeredMessage);
        }

        public virtual void PublishAll(string queueName, IEnumerable<IMessage> messages)
        {
            var brokeredMessages = new List<BrokeredMessage>();
            messages.Each(msg =>
            {
                var brokeredMessage = msg.ToBrokeredMessage();
                brokeredMessage.RemoveQueueName();

                PublishMessageFilter?.Invoke(queueName, brokeredMessage, msg);

                brokeredMessages.Add(brokeredMessage);
            });

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
                msgFactory.NamespaceManager.RegisterQueueByName(queueName, msgFactory.CreateQueueFilter);
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

            if (messages.IsEmpty()) return;

            var messageType = messages.First().GetType();
            var reqType = messageType.GenericTypeArguments.FirstOrDefault();
            
            if (reqType == null)
            {
                // IEnumerable<IMessage>
                messages.ForEach(x => Publish(x));
            }
            else
            {
                // IEnumerable<IMessage<T>>
                GetOrCreatePublishAllDelegate(reqType)(messages);
            }
        }

        protected virtual Action<IEnumerable<IMessage>> GetOrCreatePublishAllDelegate(Type reqType)
        {
            Action<IEnumerable<IMessage>> action = null;

            lock (CacheFn)
            {
                if (CacheFn.TryGetValue(reqType, out action))
                {
                    return action;
                }
            }

            var mi = publishAllOfIMessageGeneric.MakeGenericMethod(reqType);
            var genericMsgListType = typeof(List<>).MakeGenericType(typeof(IMessage<>).MakeGenericType(reqType));

            action = msgs =>
            {
                var param = (IList) Activator.CreateInstance(genericMsgListType);
                foreach (var msg in msgs)
                {
                    param.Add(msg);
                }

                mi.Invoke(this, new object[] { param });
            };

            lock (CacheFn) CacheFn[reqType] = action;

            return action;
        }

        public virtual BrokeredMessage GetMessage(string queueName, TimeSpan? serverWaitTime = null)
        {
            EnsureQueueRegistered(queueName);

            var queueClient = GetMessageReceiver(queueName);
            var msg = queueClient.Receive(serverWaitTime ?? TimeSpan.MaxValue);

            GetMessageFilter?.Invoke(queueName, msg);

            return msg;
        }

        public async Task CloseAsync()
        {
            var tasks = MessageSenders.Values.Select(x => x.CloseAsync())
                .Concat(MessageReceivers.Values.Select(x => x.CloseAsync()));
            await Task.WhenAll(tasks).ConfigureAwait(false);
           
            MessageSenders.Clear();
            MessageReceivers.Clear();
        }

        public void Dispose()
        {
        }
    }
}
