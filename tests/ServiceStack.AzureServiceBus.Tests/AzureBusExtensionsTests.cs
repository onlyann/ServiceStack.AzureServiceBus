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
            Assert.AreEqual("mq-Reverse.In", "mq:Reverse.In".ToSafeAzureQueueName());
        }
    }
}
