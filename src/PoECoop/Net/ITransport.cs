using System;

namespace PoECoop.Net
{
    public enum TransportEventType
    {
        /// <summary>A connection is up. On a guest, connection id 0 is the host.</summary>
        Connected,
        /// <summary>
        /// A connection ended or a connect attempt failed. Text holds the reason.
        /// On a host, connection 0 reports listener startup failure.
        /// </summary>
        Disconnected,
        /// <summary>One whole message arrived.</summary>
        Data,
    }

    public struct TransportEvent
    {
        public TransportEventType Type;
        public int Connection;
        public byte[] Data;
        public string Text;
    }

    /// <summary>
    /// Reliable, ordered, message-framed connections. Everything here is called
    /// from the Unity main thread and never blocks. Implementations may do I/O
    /// on background threads or service it from these main-thread calls.
    /// </summary>
    public interface ITransport : IDisposable
    {
        /// <summary>Transport name for the UI, such as Direct IP.</summary>
        string DisplayName { get; }

        /// <summary>Transport-specific listener or connection description for the UI.</summary>
        string Status { get; }

        /// <summary>
        /// Start accepting connections. Throws on synchronous startup failure;
        /// asynchronous failure is a Disconnected event for connection 0.
        /// </summary>
        void Host(int port);

        /// <summary>Start connecting. The result arrives as a Connected or Disconnected event for connection 0.</summary>
        void Connect(string address, int port);

        /// <summary>Next pending event, if any. Call until it returns false, every frame.</summary>
        bool Poll(out TransportEvent e);

        void Send(int connection, byte[] message);

        void Disconnect(int connection);

        /// <summary>Human-readable peer description for the overlay and session log.</summary>
        string Describe(int connection);
    }
}
