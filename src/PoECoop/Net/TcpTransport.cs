using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PoECoop.Net
{
    /// <summary>
    /// Direct connection by IP address. Each message is sent as a 4-byte
    /// little-endian length followed by the payload.
    /// </summary>
    public sealed class TcpTransport : ITransport
    {
        public const int MaxMessageSize = TransportLimits.MaxMessageSize;

        private const int ConnectTimeoutMs = 5000;

        private readonly ConcurrentQueue<QueuedEvent> _events = new ConcurrentQueue<QueuedEvent>();
        private int _queuedIncomingBytes;
        private int _queuedIncomingMessages;
        private readonly Dictionary<int, Link> _links = new Dictionary<int, Link>();
        private readonly object _lock = new object();
        private TcpListener _listener;
        private int _nextId = 1;
        private volatile bool _disposed;

        public string DisplayName => "Direct IP";
        public string Status { get; private set; } = "Direct IP";

        public void Host(int port)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            _listener = listener;
            Status = "Hosting on port " + port;
            StartThread("PoECoop accept", () => AcceptLoop(listener));
        }

        public void Connect(string address, int port)
        {
            Status = "Connecting to " + address + ":" + port;
            StartThread("PoECoop connect", () =>
            {
                var client = new TcpClient(AddressFamily.InterNetwork);
                try
                {
                    var pending = client.BeginConnect(address, port, null, null);
                    if (!pending.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
                        throw new TimeoutException("no answer from " + address + ":" + port);
                    client.EndConnect(pending);
                }
                catch (Exception ex)
                {
                    client.Close();
                    Emit(TransportEventType.Disconnected, 0, null, Reason(ex));
                    return;
                }
                Adopt(0, client);
            });
        }

        public bool Poll(out TransportEvent e)
        {
            QueuedEvent queued;
            if (!_events.TryDequeue(out queued))
            {
                e = default(TransportEvent);
                return false;
            }
            e = queued.Event;
            if (queued.IncomingBytes != 0) ReleaseIncoming(queued.Source, queued.IncomingBytes);
            return true;
        }

        public void Send(int connection, byte[] message)
        {
            Link link;
            lock (_lock) _links.TryGetValue(connection, out link);
            if (link != null && !link.Enqueue(message)) link.Close("outgoing queue limit or invalid message");
        }

        public void Disconnect(int connection)
        {
            Link link;
            lock (_lock) _links.TryGetValue(connection, out link);
            link?.Close("closed locally");
        }

        public string Describe(int connection)
        {
            Link link;
            lock (_lock) _links.TryGetValue(connection, out link);
            return link?.Address ?? "?";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _listener?.Stop(); } catch { }
            List<Link> links;
            lock (_lock) links = new List<Link>(_links.Values);
            foreach (var link in links) link.Close("shut down");
        }

        private void AcceptLoop(TcpListener listener)
        {
            while (!_disposed)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { return; } // listener stopped
                int id;
                lock (_lock) id = _nextId++;
                Adopt(id, client);
            }
        }

        private void Adopt(int id, TcpClient client)
        {
            client.NoDelay = true;
            var link = new Link(this, id, client);
            lock (_lock)
            {
                if (_disposed) { client.Close(); return; }
                _links[id] = link;
            }
            if (id == 0) Status = "Connected to " + link.Address;
            Emit(TransportEventType.Connected, id, null, link.Address);
            StartThread("PoECoop recv " + id, link.ReceiveLoop);
            StartThread("PoECoop send " + id, link.SendLoop);
        }

        private void Remove(Link link, string reason)
        {
            bool removed;
            lock (_lock)
            {
                removed = _links.TryGetValue(link.Id, out var current) && current == link && _links.Remove(link.Id);
                Monitor.PulseAll(_lock);
            }
            if (removed && !_disposed) Emit(TransportEventType.Disconnected, link.Id, null, reason);
        }

        private bool ReserveIncoming(Link link, int count)
        {
            lock (_lock)
            {
                while (!_disposed && !link.IsClosed &&
                    (_queuedIncomingBytes > TransportLimits.MaxQueuedIncomingBytes - count ||
                     link.QueuedIncomingBytes > TransportLimits.MaxQueuedIncomingBytesPerLink - count ||
                     _queuedIncomingMessages >= TransportLimits.MaxQueuedIncomingMessages ||
                     link.QueuedIncomingMessages >= TransportLimits.MaxQueuedIncomingMessagesPerLink))
                {
                    Monitor.Wait(_lock);
                }
                if (_disposed || link.IsClosed) return false;
                _queuedIncomingBytes += count;
                _queuedIncomingMessages++;
                link.QueuedIncomingBytes += count;
                link.QueuedIncomingMessages++;
                return true;
            }
        }

        private void ReleaseIncoming(Link link, int count)
        {
            lock (_lock)
            {
                _queuedIncomingBytes -= count;
                _queuedIncomingMessages--;
                link.QueuedIncomingBytes -= count;
                link.QueuedIncomingMessages--;
                Monitor.PulseAll(_lock);
            }
        }

        private void EmitData(Link link, byte[] data)
        {
            _events.Enqueue(new QueuedEvent
            {
                Event = new TransportEvent { Type = TransportEventType.Data, Connection = link.Id, Data = data },
                Source = link,
                IncomingBytes = data.Length,
            });
        }

        private void Emit(TransportEventType type, int connection, byte[] data, string text)
        {
            _events.Enqueue(new QueuedEvent
            {
                Event = new TransportEvent { Type = type, Connection = connection, Data = data, Text = text },
            });
        }

        private static void StartThread(string name, ThreadStart body)
        {
            new Thread(body) { IsBackground = true, Name = name }.Start();
        }

        private static string Reason(Exception ex)
        {
            if (ex is SocketException se) return se.SocketErrorCode.ToString();
            if (ex.InnerException is SocketException inner) return inner.SocketErrorCode.ToString();
            return ex.Message;
        }

        private sealed class QueuedEvent
        {
            public TransportEvent Event;
            public Link Source;
            public int IncomingBytes;
        }

        private sealed class Link
        {
            public readonly int Id;
            public readonly string Address;
            public int QueuedIncomingBytes;
            public int QueuedIncomingMessages;

            private readonly BlockingCollection<byte[]> _outgoing = new BlockingCollection<byte[]>();
            private readonly object _outgoingLock = new object();
            private readonly TcpTransport _owner;
            private readonly TcpClient _client;
            private readonly NetworkStream _stream;
            private int _outgoingBytes;
            private int _outgoingMessages;
            private int _closed;

            public bool IsClosed => Volatile.Read(ref _closed) != 0;

            public Link(TcpTransport owner, int id, TcpClient client)
            {
                _owner = owner;
                Id = id;
                _client = client;
                _stream = client.GetStream();
                try { Address = client.Client.RemoteEndPoint.ToString(); }
                catch { Address = "?"; }
            }

            public bool Enqueue(byte[] message)
            {
                if (message == null || message.Length <= 0 || message.Length > TransportLimits.MaxMessageSize) return false;
                lock (_outgoingLock)
                {
                    if (IsClosed || _outgoingBytes > TransportLimits.MaxQueuedOutgoingBytesPerLink - message.Length ||
                        _outgoingMessages >= TransportLimits.MaxQueuedOutgoingMessagesPerLink) return false;
                    _outgoingBytes += message.Length;
                    _outgoingMessages++;
                    try { _outgoing.Add(message); }
                    catch
                    {
                        _outgoingBytes -= message.Length;
                        _outgoingMessages--;
                        return false;
                    }
                    return true;
                }
            }

            public void ReceiveLoop()
            {
                var header = new byte[4];
                try
                {
                    while (true)
                    {
                        if (!ReadExactly(header, 4)) { Close("connection closed"); return; }
                        int length = header[0] | header[1] << 8 | header[2] << 16 | header[3] << 24;
                        if (length <= 0 || length > TransportLimits.MaxMessageSize) { Close("bad message size " + length); return; }
                        if (!_owner.ReserveIncoming(this, length)) return;
                        byte[] body;
                        try
                        {
                            body = new byte[length];
                            if (!ReadExactly(body, length))
                            {
                                _owner.ReleaseIncoming(this, length);
                                Close("connection closed");
                                return;
                            }
                        }
                        catch
                        {
                            _owner.ReleaseIncoming(this, length);
                            throw;
                        }
                        _owner.EmitData(this, body);
                    }
                }
                catch (Exception ex)
                {
                    Close(Reason(ex));
                }
            }

            public void SendLoop()
            {
                var header = new byte[4];
                try
                {
                    foreach (var message in _outgoing.GetConsumingEnumerable())
                    {
                        int n = message.Length;
                        header[0] = (byte)n; header[1] = (byte)(n >> 8); header[2] = (byte)(n >> 16); header[3] = (byte)(n >> 24);
                        _stream.Write(header, 0, 4);
                        _stream.Write(message, 0, n);
                        lock (_outgoingLock)
                        {
                            _outgoingBytes -= n;
                            _outgoingMessages--;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Close(Reason(ex));
                }
            }

            public void Close(string reason)
            {
                if (Interlocked.Exchange(ref _closed, 1) != 0) return;
                try { _outgoing.CompleteAdding(); } catch { }
                try { _client.Close(); } catch { }
                _owner.Remove(this, reason);
            }

            private bool ReadExactly(byte[] buffer, int count)
            {
                int done = 0;
                while (done < count)
                {
                    int got = _stream.Read(buffer, done, count - done);
                    if (got <= 0) return false;
                    done += got;
                }
                return true;
            }
        }
    }
}
