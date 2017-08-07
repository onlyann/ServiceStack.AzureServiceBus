using NUnit.Framework;
using ServiceStack.AzureServiceBus;
using ServiceStack.Messaging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ServiceStack.AzureServiceBus.Tests
{
    public class Sample
    {
        public string Value { get; set; }
    }

    [TestFixture(Category = "Integration")]
    public class MessagingFactoryTests
    {
        [Test]
        public async Task Send_receive_from_queue_dto()
        {
            IMessageFactory messagingFactory = new AzureBusMessageFactory(Config.AzureBusConnectionString);

            await messagingFactory.PurgeQueueAsync<Sample>();

            var messageClient = messagingFactory.CreateMessageQueueClient();

            messageClient.Publish(new Sample { Value = "hello" });

            var message = messageClient.Get<Sample>(QueueNames<Sample>.In, TimeSpan.FromSeconds(10));
            messageClient.Ack(message);
        }

        [Test]
        public async Task Send_receive_from_queue_message()
        {
            IMessageFactory messagingFactory = new AzureBusMessageFactory(Config.AzureBusConnectionString);

            await messagingFactory.PurgeQueueAsync<Sample>();

            var messageClient = messagingFactory.CreateMessageQueueClient();

            var sentMsg = new Message<Sample>(new Sample { Value = "hello" })
            {
                Id = Guid.NewGuid(),
                CreatedDate = DateTime.Now,
                Meta = new Dictionary<string, string>()
                {
                    ["Meta1"] = "Foo",
                    ["Meta2"] = "Bar"
                },
                Priority = 1,
                ReplyId = Guid.NewGuid()
            };

            messageClient.Publish(sentMsg);

            var msg = messageClient.Get<Sample>(QueueNames<Sample>.Priority, TimeSpan.FromSeconds(10));

            Assert.NotNull(msg);

            messageClient.Ack(msg);

            Assert.That(msg.CreatedDate, Is.EqualTo(sentMsg.CreatedDate));
            Assert.That(msg.Priority, Is.EqualTo(sentMsg.Priority));
            Assert.That(msg.ReplyId, Is.EqualTo(sentMsg.ReplyId));
            Assert.That(msg.Tag, Is.Not.Null);
            Assert.That(msg.Meta["Meta1"], Is.EqualTo("Foo"));
            Assert.That(msg.Meta["Meta2"], Is.EqualTo("Bar"));
        }
    }
}
