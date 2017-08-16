using Microsoft.ServiceBus.Messaging;
using NUnit.Framework;
using ServiceStack.Logging;
using ServiceStack.Messaging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceStack.AzureServiceBus.Tests
{
    public class Reverse
    {
        public string Value { get; set; }
    }

    public class Rot13
    {
        public string Value { get; set; }
    }

    public class AlwaysThrows
    {
        public string Value { get; set; }
    }

    [TestFixture, Category("Integration")]
    [NonParallelizable]
    public class AzureBusServerTests
    {
        static readonly string ConnectionString = Config.AzureBusConnectionString;

        [OneTimeSetUp]
        public void TestFixtureSetUp()
        {
            LogManager.LogFactory = new ConsoleLogFactory();
        }

        internal static AzureBusServer CreateMqServer(int noOfRetries = 2)
        {
            var mqHost = new AzureBusServer(ConnectionString);
            return mqHost;
        }

        internal static void Publish_4_messages(IMessageQueueClient mqClient)
        {
            mqClient.Publish(new Reverse { Value = "Hello" });
            mqClient.Publish(new Reverse { Value = "World" });
            mqClient.Publish(new Reverse { Value = "ServiceStack" });
            mqClient.Publish(new Reverse { Value = "Redis" });
        }

        private static void Publish_4_Rot13_messages(IMessageQueueClient mqClient)
        {
            mqClient.Publish(new Rot13 { Value = "Hello" });
            mqClient.Publish(new Rot13 { Value = "World" });
            mqClient.Publish(new Rot13 { Value = "ServiceStack" });
            mqClient.Publish(new Rot13 { Value = "Redis" });
        }

        [Test]
        public void Utils_publish_Reverse_messages()
        {
            using (var mqHost = new AzureBusServer(ConnectionString))
            using (var mqClient = mqHost.CreateMessageQueueClient())
            {
                Publish_4_messages(mqClient);
            }
        }

        [Test]
        public void Utils_publish_Rot13_messages()
        {
            using (var mqHost = new AzureBusServer(ConnectionString))
            using (var mqClient = mqHost.CreateMessageQueueClient())
            {
                Publish_4_Rot13_messages(mqClient);
            }
        }

        [Test]
        public void Cannot_Start_a_Disposed_MqHost()
        {
            var mqHost = CreateMqServer();

            mqHost.RegisterHandler<Reverse>(x => x.GetBody().Value.Reverse());
            mqHost.Dispose();

            try
            {
                mqHost.Start();
                Assert.Fail("Should throw ObjectDisposedException");
            }
            catch (ObjectDisposedException) { }
        }

        [Test]
        public void Cannot_Stop_a_Disposed_MqHost()
        {
            var mqHost = CreateMqServer();

            mqHost.RegisterHandler<Reverse>(x => x.GetBody().Value.Reverse());
            mqHost.Start();
            Thread.Sleep(100);

            mqHost.Dispose();

            try
            {
                mqHost.Stop();
                Assert.Fail("Should throw ObjectDisposedException");
            }
            catch (ObjectDisposedException) { }
        }

        public class Incr
        {
            public int Value { get; set; }
        }

        [Test]
        public async Task Can_receive_and_process_same_reply_responses()
        {
            var called = 0;
            using (var mqHost = CreateMqServer())
            {
                await mqHost.MessageFactory.PurgeQueueAsync<Incr>();

                mqHost.RegisterHandler<Incr>(m =>
                {
                    Debug.WriteLine("In Incr #" + m.GetBody().Value);
                    Interlocked.Increment(ref called);
                    return m.GetBody().Value > 0 ? new Incr { Value = m.GetBody().Value - 1 } : null;
                });

                mqHost.Start();

                var incr = new Incr { Value = 5 };
                using (var mqClient = mqHost.CreateMessageQueueClient())
                {
                    mqClient.Publish(incr);
                }

                ExecUtils.RetryOnException(() =>
                {
                    Assert.That(called, Is.EqualTo(1 + incr.Value));
                    Thread.Sleep(100);
                }, TimeSpan.FromSeconds(10));
            }
        }

        public class Hello : IReturn<HelloResponse>
        {
            public string Name { get; set; }
        }
        public class HelloNull : IReturn<HelloResponse>
        {
            public string Name { get; set; }
        }
        public class HelloResponse
        {
            public string Result { get; set; }
        }

        [Test]
        public async Task Can_receive_and_process_standard_request_reply_combo()
        {
            using (var mqHost = CreateMqServer())
            {
                await Task.WhenAll(
                mqHost.MessageFactory.PurgeQueueAsync<Hello>(),
                mqHost.MessageFactory.PurgeQueueAsync<HelloResponse>());

                string messageReceived = null;

                mqHost.RegisterHandler<Hello>(m =>
                    new HelloResponse { Result = "Hello, " + m.GetBody().Name });

                mqHost.RegisterHandler<HelloResponse>(m =>
                {
                    messageReceived = m.GetBody().Result; return null;
                });

                mqHost.Start();

                using (var mqClient = mqHost.CreateMessageQueueClient())
                {
                    var dto = new Hello { Name = "ServiceStack" };
                    mqClient.Publish(dto);

                    ExecUtils.RetryOnException(() =>
                    {
                        Assert.That(messageReceived, Is.EqualTo("Hello, ServiceStack"));
                        Thread.Sleep(100);
                    }, TimeSpan.FromSeconds(10));
                }
            }
        }

        public class Wait
        {
            public int ForMs { get; set; }
        }

        [Test]
        public Task Can_handle_requests_concurrently_in_4_threads()
        {
            return RunHandlerOnMultipleThreads(noOfThreads: 4, msgCount: 10);
        }

        private static async Task RunHandlerOnMultipleThreads(int noOfThreads, int msgCount)
        {
            using (var mqHost = CreateMqServer())
            {
                var timesCalled = 0;
                await mqHost.MessageFactory.PurgeQueueAsync<Wait>();

                mqHost.RegisterHandler<Wait>(m => {
                    Interlocked.Increment(ref timesCalled);
                    Thread.Sleep(m.GetBody().ForMs);
                    return null;
                }, noOfThreads);

                mqHost.Start();

                using (var mqClient = mqHost.CreateMessageQueueClient() as IMessageProducerExtended)
                {
                    mqClient.PublishAll(msgCount.Times(() => new Wait { ForMs = 50 }));

                    ExecUtils.RetryOnException(() =>
                    {
                        Assert.That(timesCalled, Is.EqualTo(msgCount));
                        Thread.Sleep(200);
                    }, TimeSpan.FromSeconds(10));
                }
            }
        }

        [Test]
        public void Can_publish_and_receive_messages_with_MessageFactory()
        {
            using (var mqFactory = new AzureBusMessageFactory(Config.AzureBusConnectionString))
            using (var mqClient = mqFactory.CreateMessageQueueClient())
            {
                mqClient.Publish(new Hello { Name = "Foo" });
                var msg = mqClient.Get<Hello>(QueueNames<Hello>.In);

                Assert.That(msg.GetBody().Name, Is.EqualTo("Foo"));
            }
        }

        [Test]
        public async Task Messages_with_null_Response_is_published_to_OutMQ()
        {
            int msgsReceived = 0;
            using (var mqServer = CreateMqServer())
            {
                await mqServer.MessageFactory.PurgeQueueAsync<HelloNull>();
                mqServer.RegisterHandler<HelloNull>(m =>
                {
                    Interlocked.Increment(ref msgsReceived);
                    return null;
                });

                mqServer.Start();

                using (var mqClient = mqServer.CreateMessageQueueClient())
                {
                    mqClient.Publish(new HelloNull { Name = "Into the Void" });

                    var msg = mqClient.Get<HelloNull>(QueueNames<HelloNull>.Out, TimeSpan.FromSeconds(10));
                    Assert.That(msg, Is.Not.Null);

                    HelloNull response = msg.GetBody();

                    Thread.Sleep(100);

                    Assert.That(response.Name, Is.EqualTo("Into the Void"));
                    Assert.That(msgsReceived, Is.EqualTo(1));
                }
            }
        }

        [Test]
        public async Task Messages_with_null_Response_is_published_to_ReplyMQ()
        {
            int msgsReceived = 0;
            using (var mqServer = CreateMqServer())
            {
                await mqServer.MessageFactory.PurgeQueuesAsync(QueueNames<HelloNull>.In);
                mqServer.RegisterHandler<HelloNull>(m =>
                {
                    Interlocked.Increment(ref msgsReceived);
                    return null;
                });

                mqServer.Start();

                using (var mqClient = mqServer.CreateMessageQueueClient())
                {
                    var replyMq = mqClient.GetTempQueueName();
                    mqClient.Publish(new Message<HelloNull>(new HelloNull { Name = "Into the Void" })
                    {
                        ReplyTo = replyMq
                    });

                    var msg = mqClient.Get<HelloNull>(replyMq, TimeSpan.FromSeconds(10));

                    HelloNull response = msg.GetBody();
                    Assert.That(response.Name, Is.EqualTo("Into the Void"));
                    Assert.That(msgsReceived, Is.EqualTo(1));
                }
            }
        }

        [Test]
        public async Task Can_change_queue_settings_before_creation()
        {
            var queueDescriptions = new List<QueueDescription>();

            using (var mqHost = CreateMqServer())
            {
                await mqHost.MessageFactory.DeleteQueueAsync<Hello>();

                mqHost.CreateQueueFilter = (description) => {
                    queueDescriptions.Add(description);
                    description.MaxSizeInMegabytes = 3072;
                };

                mqHost.RegisterHandler<Hello>(m =>
                    new HelloResponse { Result = "Hello, " + m.GetBody().Name });

                mqHost.Start();

                // priority, normal and out queues
                Assert.That(queueDescriptions.Count, Is.EqualTo(3));
                foreach (var desc in queueDescriptions)
                {
                    Assert.That(desc.MaxSizeInMegabytes, Is.EqualTo(3072));
                }
            }
        }

        [Test]
        public async Task Can_update_queue_settings_when_already_present()
        {
            var queueDescriptions = new List<QueueDescription>();

            using (var mqHost = CreateMqServer())
            {
                var nsMgr = (mqHost.MessageFactory as AzureBusMessageFactory).NamespaceManager;
                await nsMgr.RegisterQueuesAsync<Hello>(desc =>
                {
                    desc.MaxDeliveryCount = 3;
                });

                mqHost.CreateQueueFilter = (description) => {
                    queueDescriptions.Add(description);
                    description.MaxSizeInMegabytes = 3072;
                };

                mqHost.RegisterHandler<Hello>(m =>
                    new HelloResponse { Result = "Hello, " + m.GetBody().Name });

                mqHost.Start();

                // priority, normal and out queues
                Assert.That(queueDescriptions.Count, Is.EqualTo(3));
                foreach (var desc in queueDescriptions)
                {
                    Assert.That(desc.MaxSizeInMegabytes, Is.EqualTo(3072));
                    Assert.That(desc.MaxDeliveryCount, Is.EqualTo(3));
                }
            }
        }
    }
}
