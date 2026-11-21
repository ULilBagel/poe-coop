namespace PoECoop.Net
{
    /// <summary>Message and queue bounds shared by reliable ordered transports.</summary>
    public static class TransportLimits
    {
        /// <summary>Largest message accepted from or queued to a peer.</summary>
        public const int MaxMessageSize = 1 << 20;

        public const int MaxQueuedIncomingBytesPerLink = 16 << 20;
        public const int MaxQueuedIncomingBytes = 64 << 20;
        public const int MaxQueuedIncomingMessagesPerLink = 16 * 1024;
        public const int MaxQueuedIncomingMessages = 64 * 1024;
        public const int MaxQueuedOutgoingBytesPerLink = 288 << 20;
        public const int MaxQueuedOutgoingMessagesPerLink = 64 * 1024;
    }
}
