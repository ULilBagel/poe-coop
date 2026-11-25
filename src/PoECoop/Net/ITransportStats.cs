namespace PoECoop.Net
{
    /// <summary>
    /// Optional queue diagnostics a transport may implement next to ITransport.
    /// Called from the main thread; must be cheap and must not block.
    /// </summary>
    public interface ITransportStats
    {
        int PendingIncomingMessages { get; }
        long PendingIncomingBytes { get; }
        int PendingOutgoingMessages { get; }
        long PendingOutgoingBytes { get; }
    }
}
