using System;
using System.IO;
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
    }

    public static class Protocol
    {
        /// <summary>Bump on any change to the message layouts.</summary>
        public const int Version = 7;
        public const int DefaultPort = 14242;
        public const int MaxNameLength = 24;
        public const int MaxChatLength = 300;

        public static string CleanName(string name)
        {
            name = Clean(name, MaxNameLength);
            return name.Length == 0 ? "Player" : name;
        }

        /// <summary>Trim, drop control characters, cap the length. Applied to everything received.</summary>
        public static string Clean(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(Math.Min(text.Length, maxLength));
            foreach (char c in text)
            {
                if (sb.Length >= maxLength) break;
                if (!char.IsControl(c)) sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }

    public sealed class MessageWriter
    {
        private readonly MemoryStream _stream = new MemoryStream();
        private readonly BinaryWriter _writer;

        public MessageWriter(MessageType type)
        {
            _writer = new BinaryWriter(_stream, Encoding.UTF8);
            _writer.Write((byte)type);
        }

        public MessageWriter Int(int value) { _writer.Write(value); return this; }
        public MessageWriter Long(long value) { _writer.Write(value); return this; }
        public MessageWriter Byte(byte value) { _writer.Write(value); return this; }
        public MessageWriter Float(float value) { _writer.Write(value); return this; }
        public MessageWriter Guid(Guid value) { _writer.Write(value.ToByteArray()); return this; }
        public MessageWriter String(string value) { _writer.Write(value ?? ""); return this; }
        public MessageWriter Bytes(byte[] data, int offset, int count) { _writer.Write(count); _writer.Write(data, offset, count); return this; }

        public byte[] ToArray()
        {
            _writer.Flush();
            return _stream.ToArray();
        }
    }

    /// <summary>Reads one received message. Read methods throw on truncated or malformed data.</summary>
    public sealed class MessageReader
    {
        private readonly BinaryReader _reader;

        public MessageType Type { get; }
        public int Remaining => (int)(_reader.BaseStream.Length - _reader.BaseStream.Position);

        public MessageReader(byte[] data)
        {
            _reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            Type = (MessageType)_reader.ReadByte();
        }

        public int Int() => _reader.ReadInt32();
        public long Long() => _reader.ReadInt64();
        public byte Byte() => _reader.ReadByte();
        public float Float() => _reader.ReadSingle();

        public string String()
        {
            int length = Read7BitEncodedInt();
            if (length < 0 || length > TransportLimits.MaxMessageSize) throw new FormatException("string byte count " + length);
            long remaining = _reader.BaseStream.Length - _reader.BaseStream.Position;
            if (length > remaining) throw new EndOfStreamException("string byte count " + length + " exceeds remaining " + remaining);
            byte[] data = _reader.ReadBytes(length);
            if (data.Length != length) throw new EndOfStreamException();
            return Encoding.UTF8.GetString(data);
        }

        private int Read7BitEncodedInt()
        {
            int value = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                byte next = _reader.ReadByte();
                if (shift == 28 && (next & 0xf0) != 0) throw new FormatException("malformed 7-bit string byte count");
                value |= (next & 0x7f) << shift;
                if ((next & 0x80) == 0) return value;
            }
            throw new FormatException("malformed 7-bit string byte count");
        }

        public Guid Guid()
        {
            byte[] raw = _reader.ReadBytes(16);
            if (raw.Length != 16) throw new EndOfStreamException();
            return new Guid(raw);
        }

        public byte[] Bytes()
        {
            int count = _reader.ReadInt32();
            if (count < 0 || count > TransportLimits.MaxMessageSize) throw new FormatException("byte count " + count);
            byte[] data = _reader.ReadBytes(count);
            if (data.Length != count) throw new EndOfStreamException();
            return data;
        }

        /// <summary>Returns the unread payload without advancing the reader.</summary>
        public byte[] PeekRemainingBytes()
        {
            long position = _reader.BaseStream.Position;
            byte[] data = _reader.ReadBytes(Remaining);
            _reader.BaseStream.Position = position;
            return data;
        }

        /// <summary>Rejects trailing data after the complete message payload has been read.</summary>
        public void Finish()
        {
            if (Remaining != 0) throw new FormatException("trailing data: " + Remaining + " bytes");
        }
    }
}
