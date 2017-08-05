using NUnit.Framework;
using ServiceStack;

namespace ServiceStack.AzureServiceBus.Tests
{
    [TestFixture]
    public class AzureBusExtensionsTests
    {
        [Test]
        public void ToSafeAzureQueueName()
        {
            Assert.AreEqual("mq-reverse.in", "mq:Reverse.In".ToSafeAzureQueueName());
        }
    }
}
