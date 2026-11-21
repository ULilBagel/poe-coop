using System;

namespace PoECoop.Net
{
    public enum FlowDomain : byte
    {
        Pause = 1,
        Dialogue = 2,
        Area = 3,
        Rest = 4,
        Loot = 5,
        Inventory = 6,
    }

    public enum FlowResultCode
    {
        Ok = 0,
        Rejected = 1,
        StaleWorld = 2,
        NotReady = 3,
        Invalid = 4,
        Conflict = 5,
        Failed = 6,
    }

    public enum WorldStateKind : byte
    {
        Handoff = 0,
        Ready = 1,
    }

    public struct FlowCommandEnvelope
    {
        public long WorldEpoch;
        public long RequestId;
        public FlowDomain Domain;
        public int Kind;
    }

    public struct FlowResultEnvelope
    {
        public long WorldEpoch;
        public long RequestId;
        public FlowDomain Domain;
        public int Kind;
        public FlowResultCode Result;
        public int RawResult;
        public long StateSeq;
    }

    public struct WorldStateData
    {
        public long WorldEpoch;
        public int TransferId;
        public long StateSeq;
        public WorldStateKind Kind;
        public string Scene;
    }

    public struct WorldReadyData
    {
        public long WorldEpoch;
        public int TransferId;
        public FlowResultCode Result;
        public string Scene;
        public string Problem;
    }

    public struct FlowSnapshotHeader
    {
        public long WorldEpoch;
        public long StateSeq;
        public bool Full;
        public int Count;
    }

    /// <summary>
    /// Protocol 7 shared-flow layouts. Command and snapshot readers leave their
    /// domain-specific payload unread; all fixed-size messages consume the reader.
    /// </summary>
    public static class FlowWire
    {
        public const int MaxSceneLength = 100;
        public const int MaxProblemLength = 200;

        public static MessageWriter WriteCommand(long worldEpoch, long requestId, FlowDomain domain, int kind)
        {
            ValidateEpoch(worldEpoch);
            if (requestId <= 0) throw new ArgumentOutOfRangeException(nameof(requestId));
            ValidateDomain(domain);
            if (kind < 0) throw new ArgumentOutOfRangeException(nameof(kind));
            return new MessageWriter(MessageType.FlowCommand)
                .Long(worldEpoch).Long(requestId).Byte((byte)domain).Int(kind);
        }

        public static FlowCommandEnvelope ReadCommand(MessageReader reader)
        {
            RequireType(reader, MessageType.FlowCommand);
            var command = new FlowCommandEnvelope
            {
                WorldEpoch = reader.Long(),
                RequestId = reader.Long(),
                Domain = (FlowDomain)reader.Byte(),
                Kind = reader.Int(),
            };
            ValidateEpoch(command.WorldEpoch);
            if (command.RequestId <= 0) throw new FormatException("request id " + command.RequestId);
            ValidateDomain(command.Domain);
            if (command.Kind < 0) throw new FormatException("flow kind " + command.Kind);
            return command;
        }

        public static MessageWriter WriteResult(long worldEpoch, long requestId, FlowDomain domain, int kind,
            FlowResultCode result, long stateSeq)
            => WriteResult(worldEpoch, requestId, domain, kind, (int)result, stateSeq);

        public static MessageWriter WriteResult(long worldEpoch, long requestId, FlowDomain domain, int kind,
            int result, long stateSeq)
        {
            ValidateEpoch(worldEpoch);
            if (requestId <= 0) throw new ArgumentOutOfRangeException(nameof(requestId));
            ValidateDomain(domain);
            if (kind < 0) throw new ArgumentOutOfRangeException(nameof(kind));
            if (result < 0) throw new ArgumentOutOfRangeException(nameof(result));
            if (stateSeq < 0) throw new ArgumentOutOfRangeException(nameof(stateSeq));
            return new MessageWriter(MessageType.FlowResult)
                .Long(worldEpoch).Long(requestId).Byte((byte)domain).Int(kind).Int(result).Long(stateSeq);
        }

        public static FlowResultEnvelope ReadResult(MessageReader reader)
        {
            RequireType(reader, MessageType.FlowResult);
            long worldEpoch = reader.Long();
            long requestId = reader.Long();
            FlowDomain domain = (FlowDomain)reader.Byte();
            int kind = reader.Int();
            int rawResult = reader.Int();
            var result = new FlowResultEnvelope
            {
                WorldEpoch = worldEpoch,
                RequestId = requestId,
                Domain = domain,
                Kind = kind,
                Result = (FlowResultCode)rawResult,
                RawResult = rawResult,
                StateSeq = reader.Long(),
            };
            ValidateEpoch(result.WorldEpoch);
            if (result.RequestId <= 0) throw new FormatException("request id " + result.RequestId);
            ValidateDomain(result.Domain);
            if (result.Kind < 0) throw new FormatException("flow kind " + result.Kind);
            if (result.RawResult < 0) throw new FormatException("flow result " + result.RawResult);
            if (result.StateSeq < 0) throw new FormatException("state sequence " + result.StateSeq);
            reader.Finish();
            return result;
        }

        public static MessageWriter WriteWorldState(long worldEpoch, int transferId, long stateSeq,
            WorldStateKind kind, string scene)
        {
            ValidateEpoch(worldEpoch);
            ValidateTransfer(transferId);
            if (stateSeq < 0) throw new ArgumentOutOfRangeException(nameof(stateSeq));
            if (kind != WorldStateKind.Handoff && kind != WorldStateKind.Ready)
                throw new ArgumentOutOfRangeException(nameof(kind));
            scene = Protocol.Clean(scene, MaxSceneLength);
            if (scene.Length == 0) throw new ArgumentException("A world state needs a scene", nameof(scene));
            return new MessageWriter(MessageType.WorldState)
                .Long(worldEpoch).Int(transferId).Long(stateSeq).Byte((byte)kind).String(scene);
        }

        public static WorldStateData ReadWorldState(MessageReader reader)
        {
            RequireType(reader, MessageType.WorldState);
            var state = new WorldStateData
            {
                WorldEpoch = reader.Long(),
                TransferId = reader.Int(),
                StateSeq = reader.Long(),
                Kind = (WorldStateKind)reader.Byte(),
                Scene = Protocol.Clean(reader.String(), MaxSceneLength),
            };
            reader.Finish();
            ValidateEpoch(state.WorldEpoch);
            ValidateTransfer(state.TransferId);
            if (state.StateSeq < 0) throw new FormatException("state sequence " + state.StateSeq);
            if (state.Kind != WorldStateKind.Handoff && state.Kind != WorldStateKind.Ready)
                throw new FormatException("world state kind " + state.Kind);
            if (state.Scene.Length == 0) throw new FormatException("empty world scene");
            return state;
        }

        public static MessageWriter WriteWorldReady(long worldEpoch, int transferId, FlowResultCode result,
            string scene, string problem)
        {
            ValidateEpoch(worldEpoch);
            ValidateTransfer(transferId);
            ValidateResult(result);
            scene = Protocol.Clean(scene, MaxSceneLength);
            problem = Protocol.Clean(problem, MaxProblemLength);
            if (result == FlowResultCode.Ok && scene.Length == 0)
                throw new ArgumentException("A successful world load needs a scene", nameof(scene));
            if (result != FlowResultCode.Ok && problem.Length == 0)
                throw new ArgumentException("A failed world load needs a reason", nameof(problem));
            return new MessageWriter(MessageType.WorldReady)
                .Long(worldEpoch).Int(transferId).Int((int)result).String(scene).String(problem);
        }

        public static WorldReadyData ReadWorldReady(MessageReader reader)
        {
            RequireType(reader, MessageType.WorldReady);
            var ready = new WorldReadyData
            {
                WorldEpoch = reader.Long(),
                TransferId = reader.Int(),
                Result = (FlowResultCode)reader.Int(),
                Scene = Protocol.Clean(reader.String(), MaxSceneLength),
                Problem = Protocol.Clean(reader.String(), MaxProblemLength),
            };
            reader.Finish();
            ValidateEpoch(ready.WorldEpoch);
            ValidateTransfer(ready.TransferId);
            ValidateResult(ready.Result);
            if (ready.Result == FlowResultCode.Ok && ready.Scene.Length == 0)
                throw new FormatException("successful world load has no scene");
            if (ready.Result != FlowResultCode.Ok && ready.Problem.Length == 0)
                throw new FormatException("failed world load has no reason");
            return ready;
        }

        public static MessageWriter WriteSnapshot(MessageType type, long worldEpoch, long stateSeq,
            bool full, int count)
        {
            ValidateSnapshotType(type);
            ValidateEpoch(worldEpoch);
            if (stateSeq < 0) throw new ArgumentOutOfRangeException(nameof(stateSeq));
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            return new MessageWriter(type).Long(worldEpoch).Long(stateSeq).Byte(full ? (byte)1 : (byte)0).Int(count);
        }

        public static FlowSnapshotHeader ReadSnapshot(MessageReader reader, MessageType expectedType, int maxCount)
        {
            RequireType(reader, expectedType);
            ValidateSnapshotType(expectedType);
            if (maxCount < 0) throw new ArgumentOutOfRangeException(nameof(maxCount));
            long worldEpoch = reader.Long();
            long stateSeq = reader.Long();
            byte full = reader.Byte();
            var header = new FlowSnapshotHeader
            {
                WorldEpoch = worldEpoch,
                StateSeq = stateSeq,
                Full = full != 0,
                Count = reader.Int(),
            };
            if (full > 1) throw new FormatException("snapshot full flag " + full);
            ValidateEpoch(header.WorldEpoch);
            if (header.StateSeq < 0) throw new FormatException("state sequence " + header.StateSeq);
            if (header.Count < 0 || header.Count > maxCount)
                throw new FormatException("snapshot count " + header.Count);
            return header;
        }

        private static void RequireType(MessageReader reader, MessageType expected)
        {
            if (reader.Type != expected) throw new FormatException("expected " + expected + ", got " + reader.Type);
        }

        private static void ValidateEpoch(long epoch)
        {
            if (epoch <= 0) throw new FormatException("world epoch " + epoch);
        }

        private static void ValidateTransfer(int transferId)
        {
            if (transferId <= 0) throw new FormatException("transfer id " + transferId);
        }

        private static void ValidateDomain(FlowDomain domain)
        {
            if (domain < FlowDomain.Pause || domain > FlowDomain.Inventory)
                throw new FormatException("flow domain " + (byte)domain);
        }

        private static void ValidateResult(FlowResultCode result)
        {
            if (result < FlowResultCode.Ok || result > FlowResultCode.Failed)
                throw new FormatException("flow result " + (int)result);
        }

        private static void ValidateSnapshotType(MessageType type)
        {
            if (type != MessageType.TimeState && type != MessageType.DialogueState
                && type != MessageType.RestState && type != MessageType.LootState
                && type != MessageType.InventoryState && type != MessageType.EquipmentState)
                throw new ArgumentException("Not a shared-flow snapshot: " + type, nameof(type));
        }
    }
}
