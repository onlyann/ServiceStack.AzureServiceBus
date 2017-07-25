﻿using ServiceStack.Logging;
using ServiceStack.Messaging;
using ServiceStack.Text;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ServiceStack.AzureServiceBus
{
    public class AzureBusServer : IMessageService
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(AzureBusServer));

        public const int DefaultRetryCount = 1; //Will be a total of 2 attempts

        public int RetryCount { get; set; }

        /// <summary>
        /// The Message Factory used by this MQ Server
        /// </summary>
        private AzureBusMessageFactory messageFactory;
        public IMessageFactory MessageFactory => messageFactory;

        private readonly Dictionary<Type, IMessageHandlerFactory> msgHandlerFactoryMap
            = new Dictionary<Type, IMessageHandlerFactory>();

        private readonly Dictionary<Type, int> handlerThreadCountMap
            = new Dictionary<Type, int>();

        private AzureMessageReceiverPump[] messagePumps;

        public List<Type> RegisteredTypes => msgHandlerFactoryMap.Keys.ToList();

        private int status;

        public AzureBusServer(string connectionString): this(new AzureBusMessageFactory(connectionString))
        {
        }

        public AzureBusServer(AzureBusMessageFactory messageFactory)
        {
            this.messageFactory = messageFactory;
            this.RetryCount = DefaultRetryCount;
        }

        public IMessageHandlerStats GetStats()
        {
            lock (messagePumps)
            {
                var total = new MessageHandlerStats("All Handlers");
                messagePumps.Each(x => total.Add(x.GetStats()));
                return total;
            }
        }

        public string GetStatsDescription()
        {
            lock (messagePumps)
            {
                var sb = StringBuilderCache.Allocate().Append("#MQ SERVER STATS:\n");
                sb.AppendLine("===============");
                sb.AppendLine("Current Status: " + GetStatus());
                sb.AppendLine("Listening On: " + string.Join(", ", messagePumps.ToList().ConvertAll(x => x.QueueName).ToArray()));
                sb.AppendLine("===============");
                foreach (var msgPump in messagePumps)
                {
                    sb.AppendLine(msgPump.GetStats().ToString());
                    sb.AppendLine("---------------\n");
                }
                return StringBuilderCache.ReturnAndFree(sb);
            }
        }

        public string GetStatus() => WorkerStatus.ToString(status);

        public virtual void RegisterHandler<T>(Func<IMessage<T>, object> processMessageFn)
        {
            RegisterHandler(processMessageFn, null, noOfThreads: 1);
        }

        public virtual void RegisterHandler<T>(Func<IMessage<T>, object> processMessageFn, int noOfThreads)
        {
            RegisterHandler(processMessageFn, null, noOfThreads);
        }

        public virtual void RegisterHandler<T>(Func<IMessage<T>, object> processMessageFn, Action<IMessageHandler, IMessage<T>, Exception> processExceptionEx)
        {
            RegisterHandler(processMessageFn, processExceptionEx, noOfThreads: 1);
        }

        public virtual void RegisterHandler<T>(Func<IMessage<T>, object> processMessageFn, Action<IMessageHandler, IMessage<T>, Exception> processExceptionEx, int noOfThreads)
        {
            if (msgHandlerFactoryMap.ContainsKey(typeof(T)))
                throw new ArgumentException("Message handler has already been registered for type: " + typeof(T).Name);

            msgHandlerFactoryMap[typeof(T)] = CreateMessageHandlerFactory(processMessageFn, processExceptionEx);
            handlerThreadCountMap[typeof(T)] = noOfThreads;
        }

        protected IMessageHandlerFactory CreateMessageHandlerFactory<T>(Func<IMessage<T>, object> processMessageFn, Action<IMessageHandler, IMessage<T>, Exception> processExceptionEx)
        {
            return new MessageHandlerFactory<T>(this, processMessageFn, processExceptionEx)
            {
                RetryCount = RetryCount,
            };
        }

        protected void ThrowsIfDisposed()
        {
            if (status == WorkerStatus.Disposed)
                throw new ObjectDisposedException("MQ Host has been disposed");
        }

        public void Start()
        {
            if (status == WorkerStatus.Started) return;

            ThrowsIfDisposed();

            status = WorkerStatus.Starting;

            Init();

            if (messagePumps == null || messagePumps.Length == 0)
            {
                Log.Warn("Cannot start a MQ Server with no Message Handlers registered, ignoring.");
                status = WorkerStatus.Stopped;
                return;
            }

            StartMessagePumps();

            status = WorkerStatus.Started;
        }

        public virtual void Init()
        {
            if (messagePumps != null) return;

            var msgPumpsBuilder = new List<AzureMessageReceiverPump>();

            foreach (var entry in msgHandlerFactoryMap)
            {
                var msgType = entry.Key;
                var handlerFactory = entry.Value;

                var queueNames = new QueueNames(msgType);
                var noOfThreads = handlerThreadCountMap[msgType];

                msgPumpsBuilder.Add(new AzureMessageReceiverPump(
                            messageFactory,
                            handlerFactory,
                            queueNames.Priority,
                            noOfThreads));

                msgPumpsBuilder.Add(new AzureMessageReceiverPump(
                             messageFactory,
                             handlerFactory,
                             queueNames.In,
                             noOfThreads));

                messageFactory.NamespaceManager.RegisterQueues(queueNames);
            }

            messagePumps = msgPumpsBuilder.ToArray();
        }

        public virtual void StartMessagePumps()
        {
            Log.Debug("Starting all Azure Bus message pumps...");

            foreach (var msgPump in messagePumps)
            {
                try
                {
                    msgPump.Start();
                }
                catch (Exception exception)
                {
                    Log.Warn($"Could not START Azure message pump {exception}");
                    throw;
                }
            }
        }

        public virtual void StopMessagePumps()
        {
            Log.Debug("Stopping all Azure Bus message pumps...");

            foreach (var msgPump in messagePumps)
            {
                try
                {
                    msgPump.Stop();
                }
                catch (Exception exception)
                {
                    Log.Warn($"Could not STOP Azure Bus message pump {exception}");
                    throw;
                }
            }
        }

        public void Stop()
        {
            ThrowsIfDisposed();
            if (status == WorkerStatus.Stopped) return;

            status = WorkerStatus.Stopping;
            StopMessagePumps();
            status = WorkerStatus.Stopped;
        }

        public void Dispose()
        {
            if (status == WorkerStatus.Disposed) return;

            Log.Debug("Disposing all Azure Bus Server message pumps...");

            Stop();
            status = WorkerStatus.Disposed;

            try
            {
                messagePumps?.Each(x => x.Dispose());
            }
            
            catch (Exception ex)
            {
                Log.Error("Error DisposeWorkerThreads(): ", ex);
            }
        }
    }
}
