using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>
    /// Sends the host's finalized console messages to guests. Capture happens in
    /// Console.FetchConsoleMessages' postfix, after the game has combined batched
    /// attacks. Guests only add the resulting text to their own console; no damage
    /// data, callbacks or other game object references cross the network.
    /// </summary>
    public sealed class CombatLogSync : IDisposable
    {
        private const float SendInterval = 0.2f;
        private const int MaxTopLevelEntries = 64;
        private const int MaxNodes = 256;
        private const int MaxDepth = 2;
        private const int MaxTextBytes = 4 * 1024;
        private const int MaxVerboseBytes = 16 * 1024;
        private const int MaxBatchBytes = 32 * 1024;
        private const int MaxPendingEntries = 512;
        private const int HeaderSize = 13; // type, epoch, sequence, top-level count
        private const int FixedNodeSize = 24; // mode, RGBA, child count

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static CombatLogSync s_instance;

        private readonly Session _session;
        private readonly List<Entry> _pending = new List<Entry>();
        private readonly bool _probe;

        private bool _hosting, _joined;
        private int _epoch = 1, _sequence;
        private float _nextSend;
        private int _droppedEntries;
        private string _dropReason;

        private bool _hasEpoch;
        private int _guestEpoch, _guestSequence;

        /// <summary>True only while a guest is inserting a received host entry.</summary>
        public static bool ApplyingHostLog { get; private set; }

        private sealed class Entry
        {
            public Console.ConsoleState Mode;
            public Color Color;
            public string Text, Verbose;
            public List<Entry> Children;
            public int NodeCount, WireSize;
        }

        public CombatLogSync(Session session)
        {
            _session = session;
            _probe = Array.IndexOf(Environment.GetCommandLineArgs(), "--coop-probe") >= 0;
            s_instance = this;
            session.GameMessage += OnGameMessage;
            GameState.OnLevelLoaded += OnLevelChanged;
            GameState.OnLevelUnload += OnLevelChanged;
        }

        public void Dispose()
        {
            _session.GameMessage -= OnGameMessage;
            GameState.OnLevelLoaded -= OnLevelChanged;
            GameState.OnLevelUnload -= OnLevelChanged;
            _pending.Clear();
            if (s_instance == this)
            {
                s_instance = null;
                ApplyingHostLog = false;
            }
        }

        /// <summary>Called only by the FetchConsoleMessages postfix, on the host.</summary>
        public static void Capture(List<Console.ConsoleMessage> messages)
        {
            CombatLogSync self = s_instance;
            if (self == null || messages == null || ApplyingHostLog || !self._session.IsHost) return;
            self.SyncRole();
            if (self._session.Players.Count < 2) return;

            for (int i = 0; i < messages.Count; i++)
            {
                if (self._pending.Count >= MaxPendingEntries)
                {
                    self.ReportDrop(messages.Count - i, "pending queue is full");
                    break;
                }

                try
                {
                    if (TryCopyNode(messages[i], 0, out Entry entry, out string reason)
                        && entry.NodeCount <= MaxNodes && HeaderSize + entry.WireSize <= MaxBatchBytes)
                        self._pending.Add(entry);
                    else
                        self.ReportDrop(1, reason ?? "entry exceeds the node or batch limit");
                }
                catch (Exception ex) when (ex is EncoderFallbackException || ex is ArgumentException)
                {
                    self.ReportDrop(1, "entry contains invalid text");
                }
            }
        }

        /// <summary>Records the exact finalized batches delivered to each UI in probe runs.</summary>
        public static void ProbeFetched(List<Console.ConsoleMessage> messages)
        {
            CombatLogSync self = s_instance;
            if (self == null || !self._probe || messages == null || messages.Count == 0) return;
            string role = self._session.IsHost ? "host" : self._session.IsGuest ? "guest" : "offline";
            for (int i = 0; i < messages.Count; i++) self.ProbeNode(messages[i], role, i.ToString(CultureInfo.InvariantCulture));
        }

        private void ProbeNode(Console.ConsoleMessage message, string role, string path)
        {
            if (message == null) return;
            Color color = message.m_color;
            string data = "{\"utc\":\"" + DateTime.UtcNow.ToString("o") + "\",\"role\":\"" + role
                + "\",\"path\":\"" + path + "\",\"mode\":\"" + message.m_mode
                + "\",\"color\":[" + color.r.ToString("R", CultureInfo.InvariantCulture) + ","
                + color.g.ToString("R", CultureInfo.InvariantCulture) + "," + color.b.ToString("R", CultureInfo.InvariantCulture)
                + "," + color.a.ToString("R", CultureInfo.InvariantCulture) + "],\"text64\":\""
                + Convert.ToBase64String(Encoding.UTF8.GetBytes(message.m_message ?? "")) + "\",\"verbose64\":\""
                + Convert.ToBase64String(Encoding.UTF8.GetBytes(message.m_verbosemessage ?? "")) + "\"}";
            _session.Note("TEST console " + data);
            if (message.Children == null) return;
            for (int i = 0; i < message.Children.Count; i++)
                ProbeNode(message.Children[i], role, path + "." + i.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Call once per frame from the main thread.</summary>
        public void Update(float now)
        {
            SyncRole();
            if (!_session.IsHost) return;
            ReportDrops();

            if (_session.Players.Count < 2)
            {
                _pending.Clear();
                return;
            }
            if (_pending.Count == 0 || now < _nextSend) return;
            _nextSend = now + SendInterval;
            SendBatch();
        }

        private void SyncRole()
        {
            bool host = _session.IsHost;
            if (host && !_hosting)
            {
                _hosting = true;
                _epoch = 1;
                _sequence = 0;
                _nextSend = 0f;
                _pending.Clear();
            }
            else if (!host && _hosting)
            {
                _hosting = false;
                _pending.Clear();
            }

            bool guest = _session.IsGuest;
            if (guest && !_joined)
            {
                _joined = true;
                _hasEpoch = false;
                _guestEpoch = 0;
                _guestSequence = 0;
            }
            else if (!guest)
            {
                _joined = false;
                _hasEpoch = false;
            }
        }

        private void OnLevelChanged(object sender, EventArgs e)
        {
            if (!_session.IsHost) return;
            SyncRole();
            _epoch = NextPositive(_epoch);
            _sequence = 0;
            _nextSend = 0f;
            _pending.Clear();
        }

        // ---- host ----

        private static bool TryCopyNode(Console.ConsoleMessage source, int depth, out Entry entry, out string reason)
        {
            entry = null;
            reason = null;
            if (source == null) { reason = "entry is null"; return false; }
            if (depth > MaxDepth) { reason = "entry nests deeper than two levels"; return false; }
            if (!ValidMode(source.m_mode)) { reason = "entry has an invalid console mode"; return false; }

            string text = source.m_message ?? "";
            string verbose = source.m_verbosemessage ?? "";
            int textBytes = StrictUtf8.GetByteCount(text);
            int verboseBytes = StrictUtf8.GetByteCount(verbose);
            if (textBytes > MaxTextBytes) { reason = "main text exceeds 4 KiB"; return false; }
            if (verboseBytes > MaxVerboseBytes) { reason = "verbose text exceeds 16 KiB"; return false; }

            Color color = source.m_color;
            if (!IsFinite(color.r) || !IsFinite(color.g) || !IsFinite(color.b) || !IsFinite(color.a))
            {
                reason = "entry color is not finite";
                return false;
            }
            color = new Color(Mathf.Clamp01(color.r), Mathf.Clamp01(color.g), Mathf.Clamp01(color.b), Mathf.Clamp01(color.a));

            var result = new Entry
            {
                Mode = source.m_mode,
                Color = color,
                Text = text,
                Verbose = verbose,
                Children = new List<Entry>(),
                NodeCount = 1,
                WireSize = FixedNodeSize + StringSize(textBytes) + StringSize(verboseBytes),
            };

            if (source.Children == null) { entry = result; return true; }
            if (source.Children.Count > MaxNodes) { reason = "entry has too many children"; return false; }
            if (depth == MaxDepth && source.Children.Count > 0)
            {
                reason = "entry nests deeper than two levels";
                return false;
            }

            for (int i = 0; i < source.Children.Count; i++)
            {
                if (!TryCopyNode(source.Children[i], depth + 1, out Entry child, out reason)) return false;
                result.NodeCount += child.NodeCount;
                result.WireSize += child.WireSize;
                if (result.NodeCount > MaxNodes || HeaderSize + result.WireSize > MaxBatchBytes)
                {
                    reason = "entry exceeds the node or batch limit";
                    return false;
                }
                result.Children.Add(child);
            }
            entry = result;
            return true;
        }

        private void SendBatch()
        {
            int count = 0, nodes = 0, bytes = HeaderSize;
            while (count < _pending.Count && count < MaxTopLevelEntries)
            {
                Entry entry = _pending[count];
                if (nodes + entry.NodeCount > MaxNodes || bytes + entry.WireSize > MaxBatchBytes) break;
                nodes += entry.NodeCount;
                bytes += entry.WireSize;
                count++;
            }
            if (count == 0)
            {
                // TryCopyNode already enforces this. Drop defensively rather than
                // letting one bad entry permanently block every entry behind it.
                _pending.RemoveAt(0);
                ReportDrop(1, "entry cannot fit in a batch");
                ReportDrops();
                return;
            }

            if (_sequence == int.MaxValue)
            {
                _epoch = NextPositive(_epoch);
                _sequence = 0;
            }
            int sequence = ++_sequence;
            var message = new MessageWriter(MessageType.CombatLogs).Int(_epoch).Int(sequence).Int(count);
            for (int i = 0; i < count; i++) WriteNode(message, _pending[i]);
            byte[] data = message.ToArray();
            if (data.Length > MaxBatchBytes) throw new InvalidOperationException("combat log batch size " + data.Length);
            _session.SendToGuests(data);
            _pending.RemoveRange(0, count);
        }

        private static void WriteNode(MessageWriter writer, Entry entry)
        {
            writer.Int((int)entry.Mode)
                .Float(entry.Color.r).Float(entry.Color.g).Float(entry.Color.b).Float(entry.Color.a)
                .String(entry.Text).String(entry.Verbose).Int(entry.Children.Count);
            for (int i = 0; i < entry.Children.Count; i++) WriteNode(writer, entry.Children[i]);
        }

        private void ReportDrop(int count, string reason)
        {
            _droppedEntries = _droppedEntries > int.MaxValue - count ? int.MaxValue : _droppedEntries + count;
            if (_dropReason == null) _dropReason = reason;
        }

        private void ReportDrops()
        {
            if (_droppedEntries == 0) return;
            _session.Note("Dropped " + _droppedEntries + " combat log entr" + (_droppedEntries == 1 ? "y" : "ies")
                + ": " + _dropReason + ".");
            _droppedEntries = 0;
            _dropReason = null;
        }

        // ---- guest ----

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            if (sender != null || reader.Type != MessageType.CombatLogs || !_session.IsGuest) return;
            SyncRole();

            int epoch = reader.Int();
            int sequence = reader.Int();
            int count = reader.Int();
            if (epoch <= 0) throw new FormatException("combat log epoch " + epoch);
            if (sequence <= 0) throw new FormatException("combat log sequence " + sequence);
            if (count < 0 || count > MaxTopLevelEntries) throw new FormatException("combat log entry count " + count);

            int nodes = 0, bytes = HeaderSize;
            var entries = new List<Entry>(count);
            for (int i = 0; i < count; i++)
            {
                entries.Add(ReadNode(reader, 0, ref nodes, ref bytes));
                if (bytes > MaxBatchBytes) throw new FormatException("combat log batch exceeds 32 KiB");
            }

            // TCP cannot reorder a live stream. Ignoring an older sequence also
            // protects the UI if a caller ever retries a batch after reconnecting.
            if (_hasEpoch && epoch == _guestEpoch && sequence <= _guestSequence) return;
            if (!_hasEpoch || epoch != _guestEpoch)
            {
                _hasEpoch = true;
                _guestEpoch = epoch;
                _guestSequence = 0;
            }
            else if (sequence != _guestSequence + 1)
            {
                // Text already displayed cannot be reconstructed from an event
                // stream. Keep subsequent lines ordered and make the gap visible.
                _session.Note("Combat log sequence gap: expected " + (_guestSequence + 1) + ", got " + sequence + ".");
            }
            _guestSequence = sequence;
            Apply(entries);
        }

        private static Entry ReadNode(MessageReader reader, int depth, ref int nodes, ref int bytes)
        {
            if (depth > MaxDepth) throw new FormatException("combat log nesting exceeds two levels");
            nodes++;
            if (nodes > MaxNodes) throw new FormatException("combat log node count exceeds 256");

            int mode = reader.Int();
            if (!ValidMode((Console.ConsoleState)mode)) throw new FormatException("combat log mode " + mode);
            var color = new Color(reader.Float(), reader.Float(), reader.Float(), reader.Float());
            if (!IsFinite(color.r) || !IsFinite(color.g) || !IsFinite(color.b) || !IsFinite(color.a)
                || color.r < 0f || color.r > 1f || color.g < 0f || color.g > 1f
                || color.b < 0f || color.b > 1f || color.a < 0f || color.a > 1f)
                throw new FormatException("combat log color is outside RGBA bounds");

            string text = ReadString(reader, MaxTextBytes, "main", out int textBytes);
            string verbose = ReadString(reader, MaxVerboseBytes, "verbose", out int verboseBytes);
            int childCount = reader.Int();
            bytes += FixedNodeSize + StringSize(textBytes) + StringSize(verboseBytes);
            if (bytes > MaxBatchBytes) throw new FormatException("combat log batch exceeds 32 KiB");
            if (childCount < 0 || childCount > MaxNodes || nodes + childCount > MaxNodes)
                throw new FormatException("combat log child count " + childCount);
            if (depth == MaxDepth && childCount != 0) throw new FormatException("combat log nesting exceeds two levels");

            var entry = new Entry
            {
                Mode = (Console.ConsoleState)mode,
                Color = color,
                Text = text,
                Verbose = verbose,
                Children = new List<Entry>(childCount),
                NodeCount = 1,
                WireSize = FixedNodeSize + StringSize(textBytes) + StringSize(verboseBytes),
            };
            for (int i = 0; i < childCount; i++)
            {
                Entry child = ReadNode(reader, depth + 1, ref nodes, ref bytes);
                entry.Children.Add(child);
                entry.NodeCount += child.NodeCount;
                entry.WireSize += child.WireSize;
            }
            return entry;
        }

        private static string ReadString(MessageReader reader, int maxBytes, string which, out int byteCount)
        {
            string value = reader.String();
            byteCount = StrictUtf8.GetByteCount(value);
            if (byteCount > maxBytes) throw new FormatException("combat log " + which + " string is too long");
            return value;
        }

        private static void Apply(List<Entry> entries)
        {
            if (entries.Count == 0 || Console.Instance == null) return;
            bool wasApplying = ApplyingHostLog;
            ApplyingHostLog = true;
            try
            {
                for (int i = 0; i < entries.Count; i++) Console.Instance.AddMessage(ToConsoleMessage(entries[i]));
            }
            finally
            {
                ApplyingHostLog = wasApplying;
            }
        }

        private static Console.ConsoleMessage ToConsoleMessage(Entry entry)
        {
            var message = new Console.ConsoleMessage(entry.Text, entry.Verbose, entry.Mode, entry.Color);
            if (entry.Children.Count > 0)
            {
                var children = new List<Console.ConsoleMessage>(entry.Children.Count);
                for (int i = 0; i < entry.Children.Count; i++) children.Add(ToConsoleMessage(entry.Children[i]));
                message.Children = children;
            }
            return message;
        }

        // ---- shared ----

        private static bool ValidMode(Console.ConsoleState mode)
        {
            return mode == Console.ConsoleState.Combat || mode == Console.ConsoleState.Dialogue
                || mode == Console.ConsoleState.DialogueBig || mode == Console.ConsoleState.Both;
        }

        private static int StringSize(int byteCount)
        {
            int prefix = 1;
            for (int value = byteCount; value >= 128; value >>= 7) prefix++;
            return prefix + byteCount;
        }

        private static int NextPositive(int value) => value <= 0 || value == int.MaxValue ? 1 : value + 1;
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
