using ServiceStack.Messaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ServiceStack.AzureServiceBus
{
    public interface IMessageProducerExtended : IMessageProducer
    {
        void PublishAll<T>(IEnumerable<IMessage<T>> messages);

        void PublishAll<T>(IEnumerable<T> messageBodyList);
    }
}
