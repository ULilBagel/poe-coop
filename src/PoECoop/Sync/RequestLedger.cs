using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using PoECoop.Net;

namespace PoECoop.Sync
{
    public enum RequestLookupStatus
    {
        New,
        Duplicate,
        Conflict,
    }

    public struct RequestLookup
    {
        public RequestLookupStatus Status;
        public bool HasResult;
        public FlowResultCode Result;
        public long StateSeq;

        public bool ShouldExecute => Status == RequestLookupStatus.New;
    }

    /// <summary>
    /// Host-side idempotency for shared-flow commands. Fingerprints cover the
    /// domain, kind and complete unread payload; request ids are scoped to a peer
    /// and world epoch. Completed results can be replayed without executing twice.
    /// </summary>
    public sealed class RequestLedger
    {
        public const int CapacityPerPlayer = 256;

        private sealed class Entry
        {
            public byte[] Fingerprint;
            public bool HasResult;
            public FlowResultCode Result;
            public long StateSeq;
        }

        private sealed class PlayerLedger
        {
            public long WorldEpoch;
            public readonly Queue<long> Order = new Queue<long>();
            public readonly Dictionary<long, Entry> Entries = new Dictionary<long, Entry>();
        }

        private readonly Dictionary<int, PlayerLedger> _players = new Dictionary<int, PlayerLedger>();

        public RequestLookup Begin(int playerId, FlowCommandEnvelope command, MessageReader payload)
        {
            if (command.RequestId <= 0) throw new ArgumentOutOfRangeException(nameof(command), "request id must be positive");
            byte[] fingerprint = Fingerprint(command, payload);
            if (!_players.TryGetValue(playerId, out PlayerLedger player))
            {
                player = new PlayerLedger { WorldEpoch = command.WorldEpoch };
                _players.Add(playerId, player);
            }
            else if (player.WorldEpoch != command.WorldEpoch)
            {
                player.Order.Clear();
                player.Entries.Clear();
                player.WorldEpoch = command.WorldEpoch;
            }

            if (player.Entries.TryGetValue(command.RequestId, out Entry existing))
            {
                if (!Equal(existing.Fingerprint, fingerprint))
                    return new RequestLookup { Status = RequestLookupStatus.Conflict, Result = FlowResultCode.Conflict };
                return new RequestLookup
                {
                    Status = RequestLookupStatus.Duplicate,
                    HasResult = existing.HasResult,
                    Result = existing.Result,
                    StateSeq = existing.StateSeq,
                };
            }

            while (player.Order.Count >= CapacityPerPlayer)
            {
                long oldest = player.Order.Dequeue();
                player.Entries.Remove(oldest);
            }
            player.Order.Enqueue(command.RequestId);
            player.Entries.Add(command.RequestId, new Entry { Fingerprint = fingerprint });
            return new RequestLookup { Status = RequestLookupStatus.New, Result = FlowResultCode.Ok };
        }

        public void Complete(int playerId, FlowCommandEnvelope command, FlowResultCode result, long stateSeq)
        {
            if (!_players.TryGetValue(playerId, out PlayerLedger player)
                || player.WorldEpoch != command.WorldEpoch
                || !player.Entries.TryGetValue(command.RequestId, out Entry entry)) return;
            if (entry.HasResult) return;
            entry.HasResult = true;
            entry.Result = result;
            entry.StateSeq = stateSeq;
        }

        public void RemovePlayer(int playerId) => _players.Remove(playerId);

        public void Clear() => _players.Clear();

        public int CountForPlayer(int playerId)
            => _players.TryGetValue(playerId, out PlayerLedger player) ? player.Entries.Count : 0;

        private static byte[] Fingerprint(FlowCommandEnvelope command, MessageReader payload)
        {
            byte[] data = payload != null ? payload.PeekRemainingBytes() : new byte[0];
            byte[] framed = new byte[data.Length + 5];
            framed[0] = (byte)command.Domain;
            framed[1] = (byte)command.Kind;
            framed[2] = (byte)(command.Kind >> 8);
            framed[3] = (byte)(command.Kind >> 16);
            framed[4] = (byte)(command.Kind >> 24);
            Buffer.BlockCopy(data, 0, framed, 5, data.Length);
            using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(framed);
        }

        private static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int difference = 0;
            for (int i = 0; i < a.Length; i++) difference |= a[i] ^ b[i];
            return difference == 0;
        }
    }
}
