using Microsoft.ServiceBus.Messaging;
using ServiceStack.Messaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceStack.AzureServiceBus
{
    public class AzureMessageReceiverPump : IDisposable
    {
        public string QueueName { get; private set; }

        protected readonly int noOfThreads;
        protected readonly AzureBusMessageFactory mqFactory;
        protected readonly IMessageHandlerFactory messageHandlerFactory;
        protected AzureBusMessageQueueClient mqClient;

        protected readonly IMessageHandler[] messageHandlers;

        protected int threadCount = 0;

        public AzureBusMessageQueueClient MqClient
        {
            get => mqClient ?? (mqClient = mqFactory.CreateMessageQueueClient() as AzureBusMessageQueueClient);
        }

        public AzureMessageReceiverPump(AzureBusMessageFactory mqFactory,
            IMessageHandlerFactory messageHandlerFactory, string queueName, int noOfThreads = 1)
        {
            QueueName = queueName;
            this.noOfThreads = noOfThreads;
            this.mqFactory = mqFactory;
            this.messageHandlerFactory = messageHandlerFactory;

            messageHandlers = new IMessageHandler[noOfThreads];
            noOfThreads.Times(i => { messageHandlers[i] = messageHandlerFactory.CreateMessageHandler(); });
        }

        public virtual void Start()
        {
            var options = new OnMessageOptions
            {
                AutoComplete = false,
                MaxConcurrentCalls = noOfThreads
            };

            options.ExceptionReceived += OnMessageExceptionReceived;

            var msgReceiver = MqClient.GetMessageReceiver(QueueName);
            msgReceiver.OnMessage(
                OnMessage,
                options);
        }

        private void OnMessageExceptionReceived(object sender, ExceptionReceivedEventArgs e)
        {
        }

        /// <summary>
        /// This gets called in a separate thread by the underlying Azure message pump
        /// </summary>
        /// <param name="msg"></param>
        protected virtual void OnMessage(BrokeredMessage msg)
        {
            var threadNumber = Interlocked.Increment(ref threadCount);
            try
            {
                messageHandlers[threadNumber - 1].ProcessMessage(MqClient, msg);
            }
            finally
            {
                Interlocked.Decrement(ref threadCount);
            }
        }



        public virtual IMessageHandlerStats GetStats()
        {
            var stats = new MessageHandlerStats($"{QueueName} pump stats");
            messageHandlers.Each(x => stats.Add(x.GetStats()));
            return stats;
        }

        public virtual void Stop()
        {
            DisposeMqClient();
        }

        protected virtual void DisposeMqClient()
        {
            if (mqClient != null)
            {
                mqClient.Dispose();
                mqClient = null;
            }
        }

        public void Dispose()
        {
            DisposeMqClient();
        }
    }
}
