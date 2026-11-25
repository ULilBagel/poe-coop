using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace PoECoop.Net
{
    /// <summary>First byte of every message.</summary>
    public enum MessageType : byte
    {
        /// <summary>Guest to host, first message: protocol, mod version, game version, name.</summary>
        Hello = 1,
        /// <summary>Host to guest: your player id, then the full player list.</summary>
        Welcome = 2,
        /// <summary>Host to guest: refused, with a reason. The host then disconnects.</summary>
        Reject = 3,
        /// <summary>Host to guests: a player joined (id, name).</summary>
        PlayerJoined = 4,
        /// <summary>Host to guests: a player left (id).</summary>
        PlayerLeft = 5,
        /// <summary>Guest to host: text. Host to guests: sender id, text.</summary>
        Chat = 6,

        // Types from FirstGameMessage up pass through Session's world-readiness
        // gate before game-side handlers receive them (see Sync/).
        FirstGameMessage = 16,
        /// <summary>Host to guest: a save is coming (transfer id, total bytes, scene name).</summary>
        SaveBegin = 16,
        /// <summary>Host to guest: the next piece of the save (transfer id, bytes).</summary>
        SaveChunk = 17,
        /// <summary>Guest to host: finished loading (scene name), or failed (empty scene name, reason).</summary>
        SaveLoaded = 18,
        /// <summary>Host to guests: where characters are (count, then per character: id, position, facing, speeds).</summary>
        Positions = 19,
        /// <summary>Host to guests: who controls which party member (count, then per character: id, player id).</summary>
        Ownership = 20,
        /// <summary>Guest to host: an order for one character (kind, character id, queued, then data by kind).</summary>
        Order = 21,
        /// <summary>Host to guests: combat state snapshot.</summary>
        CombatState = 22,
        /// <summary>Host to guests: character vitals snapshot.</summary>
        Vitals = 23,
        /// <summary>Host to guests: status effects snapshot.</summary>
        StatusEffects = 24,
        /// <summary>Host to guests: attack visuals event.</summary>
        AttackVisuals = 25,
        /// <summary>Host to guests: combat log entries.</summary>
        CombatLogs = 26,
        /// <summary>Host to guests: ability states snapshot.</summary>
        AbilityStates = 27,
        /// <summary>Host to guests: definition of a networked entity.</summary>
        EntityDefine = 28,
        /// <summary>Host to guests: removal of a networked entity.</summary>
        EntityRemove = 29,
        /// <summary>Host to guests: projectile visuals event.</summary>
        ProjectileVisuals = 30,
        /// <summary>Host to guest: complete ordered tactical snapshot, including the current turn token.</summary>
        TacticalState = 31,
        /// <summary>Guest to host: semantic tactical command for the supplied turn token.</summary>
        TacticalOrder = 32,
        /// <summary>Host to guest: acceptance or rejection of a tactical command.</summary>
        OrderResult = 33,
        /// <summary>Host to guest: correlated world handoff or readiness marker.</summary>
        WorldState = 34,
        /// <summary>Guest to host: the correlated world load succeeded or failed.</summary>
        WorldReady = 35,
        /// <summary>Host to guest: complete pause, speed and world-clock state.</summary>
        TimeState = 36,
        /// <summary>Guest to host: a correlated shared-flow command.</summary>
        FlowCommand = 37,
        /// <summary>Host to guest: the durable result of a shared-flow command.</summary>
        FlowResult = 38,
        /// <summary>Host to guest: complete dialogue state.</summary>
        DialogueState = 39,
        /// <summary>Host to guest: complete rest state.</summary>
        RestState = 40,
        /// <summary>Host to guest: complete loot state.</summary>
        LootState = 41,
        /// <summary>Host to guest: complete inventory state.</summary>
        InventoryState = 42,
        /// <summary>Host to guest: complete equipment state.</summary>
        EquipmentState = 43,
        /// <summary>Host to guests: ground loot objects (dropped bags, lootable bodies): definitions and state.</summary>
        GroundLootState = 44,
        /// <summary>Host to guests: removal of ground loot objects.</summary>
        GroundLootRemove = 45,
        /// <summary>Host to guests: team relationships and per-entity faction state.</summary>
        FactionState = 46,
        /// <summary>Host to guests: reserved second faction stream (for example team relationship matrix deltas).</summary>
        FactionTeams = 47,
        /// <summary>Either direction: opt-in performance diagnostics (clock/latency samples). Never required for gameplay.</summary>
        PerfProbe = 48,
    }

    public static class Protocol
    {
        /// <summary>Bump on any change to the message layouts.</summary>
        public const int Version = 8;
        public const int DefaultPort = 14242;
        public const int MaxNameLength = 24;
        public const int MaxChatLength = 300;

        /// <summary>Where the current release and its updater live. Shown as text, so it has no scheme.</summary>
        public const string UpdateUrl = "github.com/ULilBagel/poe-coop/releases/latest";

        /// <summary>
        /// One short sentence saying who has to update after a mod version mismatch. It goes into
        /// the host's refusal, which older guests show as it is, so it stays well under 100 characters.
        /// </summary>
        public static string UpdateAdvice(string hostVersion, string guestVersion)
        {
            string who = "Both players need the same release";
            System.Version host, guest;
            if (TryVersion(hostVersion, out host) && TryVersion(guestVersion, out guest) && host != guest)
                who = host > guest ? "The guest's version is older" : "The host's version is older";
            return who + ". Run update-poecoop or get " + UpdateUrl;
        }

        private static bool TryVersion(string text, out System.Version version)
        {
            version = null;
            if (string.IsNullOrEmpty(text)) return false;
            try { version = new System.Version(text.Trim()); return true; }
            catch (System.Exception) { return false; }
        }

        public static string CleanName(string name)
        {
            name = Clean(name, MaxNameLength);
            return name.Length == 0 ? "Player" : name;
        }

        /// <summary>Trim, drop control characters, cap the length. Applied to everything received.</summary>
        public static string Clean(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (IsClean(text, maxLength)) return text;
            var sb = new StringBuilder(Math.Min(text.Length, maxLength));
            foreach (char c in text)
            {
                if (sb.Length >= maxLength) break;
                if (!char.IsControl(c)) sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        /// <summary>True when Clean would return the text unchanged, which is the usual case.</summary>
        private static bool IsClean(string text, int maxLength)
        {
            if (text.Length > maxLength || char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[text.Length - 1])) return false;
            for (int i = 0; i < text.Length; i++)
                if (char.IsControl(text[i])) return false;
            return true;
        }
    }

    /// <summary>Same bytes as BinaryWriter/BinaryReader on a little-endian machine, without their per-value arrays.</summary>
    internal static class WireBits
    {
        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float Value;
            [FieldOffset(0)] public uint Bits;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct GuidBits
        {
            [FieldOffset(0)] public Guid Value;
            [FieldOffset(0)] public ulong Low;
            [FieldOffset(8)] public ulong High;
        }

        /// <summary>False when the in-memory Guid layout is not its wire layout; then the array path is used.</summary>
        internal static readonly bool FastGuid = CheckGuidLayout();

        internal static uint FromFloat(float value)
        {
            var bits = default(FloatBits);
            bits.Value = value;
            return bits.Bits;
        }

        internal static float ToFloat(uint value)
        {
            var bits = default(FloatBits);
            bits.Bits = value;
            return bits.Value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void FromGuid(Guid value, out ulong low, out ulong high)
        {
            var bits = default(GuidBits);
            bits.Value = value;
            low = bits.Low;
            high = bits.High;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Guid ToGuid(ulong low, ulong high)
        {
            var bits = default(GuidBits);
            bits.Low = low;
            bits.High = high;
            return bits.Value;
        }

        internal static void WriteUInt64(byte[] buffer, int offset, ulong value)
        {
            uint low = (uint)value, high = (uint)(value >> 32);
            buffer[offset] = (byte)low;
            buffer[offset + 1] = (byte)(low >> 8);
            buffer[offset + 2] = (byte)(low >> 16);
            buffer[offset + 3] = (byte)(low >> 24);
            buffer[offset + 4] = (byte)high;
            buffer[offset + 5] = (byte)(high >> 8);
            buffer[offset + 6] = (byte)(high >> 16);
            buffer[offset + 7] = (byte)(high >> 24);
        }

        internal static ulong ReadUInt64(byte[] buffer, int offset)
        {
            uint low = (uint)(buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16 | buffer[offset + 3] << 24);
            uint high = (uint)(buffer[offset + 4] | buffer[offset + 5] << 8 | buffer[offset + 6] << 16 | buffer[offset + 7] << 24);
            return low | (ulong)high << 32;
        }

        private static bool CheckGuidLayout()
        {
            try
            {
                if (!BitConverter.IsLittleEndian) return false;
                var sample = new Guid("00112233-4455-6677-8899-aabbccddeeff");
                byte[] expected = sample.ToByteArray();
                byte[] actual = new byte[16];
                ulong low, high;
                FromGuid(sample, out low, out high);
                WriteUInt64(actual, 0, low);
                WriteUInt64(actual, 8, high);
                for (int i = 0; i < 16; i++)
                    if (actual[i] != expected[i]) return false;
                return ToGuid(ReadUInt64(expected, 0), ReadUInt64(expected, 8)) == sample
                    && BitConverter.ToSingle(BitConverter.GetBytes(FromFloat(1.5f)), 0) == 1.5f;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Builds one message. The bytes are those BinaryWriter would produce. The
    /// working buffer is reused by the next writer on the same thread once
    /// ToArray has copied the message out.
    /// </summary>
    public sealed class MessageWriter
    {
        private const int InitialCapacity = 256;
        private const int MaxSpareCapacity = 256 * 1024;

        [ThreadStatic] private static byte[] t_spare;

        private byte[] _buffer;
        private int _length;
        // The last ToArray result while the working buffer is handed back.
        private byte[] _sealed;

        public MessageWriter(MessageType type)
        {
            _buffer = t_spare ?? new byte[InitialCapacity];
            t_spare = null;
            _buffer[0] = (byte)type;
            _length = 1;
        }

        public MessageWriter Int(int value)
        {
            Reserve(4);
            byte[] buffer = _buffer;
            int at = _length;
            buffer[at] = (byte)value;
            buffer[at + 1] = (byte)(value >> 8);
            buffer[at + 2] = (byte)(value >> 16);
            buffer[at + 3] = (byte)(value >> 24);
            _length = at + 4;
            return this;
        }

        public MessageWriter Long(long value)
        {
            Reserve(8);
            WireBits.WriteUInt64(_buffer, _length, (ulong)value);
            _length += 8;
            return this;
        }

        public MessageWriter Byte(byte value)
        {
            Reserve(1);
            _buffer[_length++] = value;
            return this;
        }

        public MessageWriter Float(float value) => Int((int)WireBits.FromFloat(value));

        public MessageWriter Guid(Guid value)
        {
            Reserve(16);
            if (WireBits.FastGuid)
            {
                ulong low, high;
                WireBits.FromGuid(value, out low, out high);
                WireBits.WriteUInt64(_buffer, _length, low);
                WireBits.WriteUInt64(_buffer, _length + 8, high);
            }
            else
            {
                Buffer.BlockCopy(value.ToByteArray(), 0, _buffer, _length, 16);
            }
            _length += 16;
            return this;
        }

        public MessageWriter String(string value)
        {
            if (value == null) value = "";
            int count = Encoding.UTF8.GetByteCount(value);
            Reserve(5 + count);
            uint prefix = (uint)count;
            while (prefix >= 0x80)
            {
                _buffer[_length++] = (byte)(prefix | 0x80);
                prefix >>= 7;
            }
            _buffer[_length++] = (byte)prefix;
            if (count > 0) _length += Encoding.UTF8.GetBytes(value, 0, value.Length, _buffer, _length);
            return this;
        }

        public MessageWriter Bytes(byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || count > data.Length - offset) throw new ArgumentOutOfRangeException(nameof(count));
            Int(count);
            Reserve(count);
            Buffer.BlockCopy(data, offset, _buffer, _length, count);
            _length += count;
            return this;
        }

        public byte[] ToArray()
        {
            if (_buffer == null) return (byte[])_sealed.Clone();
            byte[] message = new byte[_length];
            Buffer.BlockCopy(_buffer, 0, message, 0, _length);
            byte[] spare = t_spare;
            if (_buffer.Length <= MaxSpareCapacity && (spare == null || spare.Length < _buffer.Length)) t_spare = _buffer;
            _buffer = null;
            _sealed = message;
            return message;
        }

        private void Reserve(int count)
        {
            if (_buffer == null || count > _buffer.Length - _length) Grow(count);
        }

        private void Grow(int count)
        {
            int needed = checked(_length + count);
            byte[] source = _buffer ?? _sealed;
            byte[] grown = null;
            if (_buffer == null)
            {
                // Written to again after ToArray: continue from a copy of that message.
                byte[] spare = t_spare;
                if (spare != null && spare.Length >= needed)
                {
                    grown = spare;
                    t_spare = null;
                }
            }
            if (grown == null)
                grown = new byte[(int)Math.Min(int.MaxValue, Math.Max((long)needed, Math.Max(InitialCapacity, source.Length * 2L)))];
            Buffer.BlockCopy(source, 0, grown, 0, _length);
            _buffer = grown;
            _sealed = null;
        }
    }

    /// <summary>Reads one received message. Read methods throw on truncated or malformed data.</summary>
    public sealed class MessageReader
    {
        private readonly byte[] _data;
        private int _position;

        public MessageType Type { get; }
        public int Remaining => _data.Length - _position;

        public MessageReader(byte[] data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            Type = (MessageType)data[Take(1)];
        }

        public int Int()
        {
            int at = Take(4);
            byte[] data = _data;
            return data[at] | data[at + 1] << 8 | data[at + 2] << 16 | data[at + 3] << 24;
        }

        public long Long() => (long)WireBits.ReadUInt64(_data, Take(8));
        public byte Byte() => _data[Take(1)];
        public float Float() => WireBits.ToFloat((uint)Int());

        public string String()
        {
            int length = Read7BitEncodedInt();
            if (length < 0 || length > TransportLimits.MaxMessageSize) throw new FormatException("string byte count " + length);
            int remaining = Remaining;
            if (length > remaining) throw new EndOfStreamException("string byte count " + length + " exceeds remaining " + remaining);
            return length == 0 ? "" : Encoding.UTF8.GetString(_data, Take(length), length);
        }

        private int Read7BitEncodedInt()
        {
            int value = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                byte next = Byte();
                if (shift == 28 && (next & 0xf0) != 0) throw new FormatException("malformed 7-bit string byte count");
                value |= (next & 0x7f) << shift;
                if ((next & 0x80) == 0) return value;
            }
            throw new FormatException("malformed 7-bit string byte count");
        }

        public Guid Guid()
        {
            int at = Take(16);
            if (WireBits.FastGuid) return WireBits.ToGuid(WireBits.ReadUInt64(_data, at), WireBits.ReadUInt64(_data, at + 8));
            byte[] raw = new byte[16];
            Buffer.BlockCopy(_data, at, raw, 0, 16);
            return new Guid(raw);
        }

        public byte[] Bytes()
        {
            int count = Int();
            if (count < 0 || count > TransportLimits.MaxMessageSize) throw new FormatException("byte count " + count);
            int at = Take(count);
            byte[] data = new byte[count];
            Buffer.BlockCopy(_data, at, data, 0, count);
            return data;
        }

        /// <summary>Returns the unread payload without advancing the reader.</summary>
        public byte[] PeekRemainingBytes()
        {
            byte[] data = new byte[Remaining];
            Buffer.BlockCopy(_data, _position, data, 0, data.Length);
            return data;
        }

        /// <summary>Rejects trailing data after the complete message payload has been read.</summary>
        public void Finish()
        {
            if (Remaining != 0) throw new FormatException("trailing data: " + Remaining + " bytes");
        }

        /// <summary>Advances past count bytes and returns where they start.</summary>
        private int Take(int count)
        {
            int at = _position;
            if (count > _data.Length - at) throw new EndOfStreamException();
            _position = at + count;
            return at;
        }
    }
}
