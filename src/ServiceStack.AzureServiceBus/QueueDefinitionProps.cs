using System;

namespace ServiceStack.AzureServiceBus
{
    internal class QueueDefinitionProps
    {
        public QueueDefinitionProps() { }
        public TimeSpan AutoDeleteOnIdle { get; set; }
        public TimeSpan DefaultMessageTimeToLive { get; set; }
        public TimeSpan DuplicateDetectionHistoryTimeWindow { get; set; }
        public bool EnableBatchedOperations { get; set; }
        public bool EnableDeadLetteringOnMessageExpiration { get; set; }
        public bool EnableExpress { get; set; }
        public bool EnablePartitioning { get; set; }
        public string ForwardDeadLetteredMessagesTo { get; set; }
        public string ForwardTo { get; set; }
        public bool IsAnonymousAccessible { get; set; }
        public TimeSpan LockDuration { get; set; }
        public int MaxDeliveryCount { get; set; }
        public long MaxSizeInMegabytes { get; set; }
        public bool RequiresDuplicateDetection { get; set; }
        public bool RequiresSession { get; set; }
        public bool SupportOrdering { get; set; }
        public string UserMetadata { get; set; }

        public override bool Equals(object obj) => (obj is QueueDefinitionProps to) && this.ToJson() == to.ToJson();
    }
}
