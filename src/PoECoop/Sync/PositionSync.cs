using System;
using System.Collections.Generic;
using HarmonyLib;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>
    /// Keeps characters in the same place on every machine. The host sends where
    /// each character is twenty times a second; a guest slides its copy there. Sent
    /// along are the two speeds the game's walk animation is driven by, so the
    /// guest's characters also animate like the host's. EntityRegistry supplies
    /// ids for both saved and runtime characters.
    /// </summary>
    public sealed class PositionSync
    {
        private const float SendInterval = 0.05f;
        private const float FullRefreshInterval = 3f;
        /// <summary>Slightly longer than SendInterval, so a guest character is still moving when the next position arrives.</summary>
        private const float SlideSeconds = 0.07f;
        /// <summary>Further away than this, jump instead of sliding.</summary>
        private const float SnapDistance = 4f;
        private const int MaxMovers = 10000;

        // The Mover keeps these private. Animation reads current speed; tactical input also reads desired speed.
        private static readonly AccessTools.FieldRef<Mover, float> CurrentSpeed =
            AccessTools.FieldRefAccess<Mover, float>("m_currentSpeed");
        private static readonly AccessTools.FieldRef<Mover, float> DesiredSpeed =
            AccessTools.FieldRefAccess<Mover, float>("m_desiredSpeed");

        private static PositionSync s_instance;

        private readonly Session _session;
        private readonly HashSet<Mover> _handled = new HashSet<Mover>();

        // Host: what was last sent for each character.
        private readonly Dictionary<Guid, Sample> _sent = new Dictionary<Guid, Sample>();
        private float _nextSend, _nextFullRefresh;

        // Guest: where each character should be.
        private readonly Dictionary<Guid, Target> _targets = new Dictionary<Guid, Target>();
        private float _nextTargetPrune;

        private struct Sample
        {
            public Vector3 Position;
            public float Facing, Speed, Desired;
        }

        private sealed class Target
        {
            public Sample To;
            /// <summary>Seconds left to reach To. Reset when a new position arrives.</summary>
            public float Remaining;
        }

        public PositionSync(Session session)
        {
            _session = session;
            s_instance = this;
            session.GameMessage += OnGameMessage;
            session.PlayerJoined += OnPlayerJoined;
            GameState.OnLevelLoaded += OnLevelLoaded;
            GameState.OnLevelUnload += OnLevelLoaded;
        }

        public void Dispose()
        {
            GameState.OnLevelLoaded -= OnLevelLoaded;
            GameState.OnLevelUnload -= OnLevelLoaded;
            _session.GameMessage -= OnGameMessage;
            _session.PlayerJoined -= OnPlayerJoined;
            if (s_instance == this) s_instance = null;
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            if (_session.IsHost) _nextFullRefresh = 0f;
        }

        private void OnLevelLoaded(object sender, EventArgs e)
        {
            // Different level, different characters.
            _handled.Clear();
            _sent.Clear();
            _targets.Clear();
            _nextFullRefresh = 0f;
            _nextTargetPrune = 0f;
        }

        // ---- host ----

        public void Update(float now)
        {
            if (_session.IsGuest)
            {
                PruneTargets(now);
                return;
            }
            if (!_session.IsHost || now < _nextSend) return;
            _nextSend = now + SendInterval;
            if (_session.Players.Count < 2 || !GameState.IsInGameSession || GameState.IsLoading) return;

            bool full = now >= _nextFullRefresh;
            if (full) _nextFullRefresh = now + FullRefreshInterval;

            MessageWriter message = null;
            int count = 0;
            _handled.Clear();
            List<Mover> movers = Mover.Movers;
            for (int i = 0; i < movers.Count; i++) AddSample(movers[i], full, ref message, ref count);
            foreach (GameObject obj in EntityRegistry.RegisteredObjects)
            {
                if (obj != null) AddSample(obj.GetComponent<Mover>(), full, ref message, ref count);
                if (count >= MaxMovers) break;
            }
            if (full)
            {
                // The game's Mover list omits objects disabled in the hierarchy.
                Mover[] all = Resources.FindObjectsOfTypeAll<Mover>();
                foreach (Mover mover in all)
                {
                    if (mover != null && mover.gameObject.scene.IsValid()) AddSample(mover, true, ref message, ref count);
                    if (count >= MaxMovers) break;
                }
            }
            if (message == null) return;

            byte[] data = message.ToArray();
            // Fill in the count that was left as 0 after the type byte.
            data[1] = (byte)count; data[2] = (byte)(count >> 8); data[3] = (byte)(count >> 16); data[4] = (byte)(count >> 24);
            _session.SendToGuests(data);
        }

        private void AddSample(Mover mover, bool full, ref MessageWriter message, ref int count)
        {
            if (mover == null || count >= MaxMovers || !_handled.Add(mover)) return;
            Guid id = EntityRegistry.NetworkId(mover.gameObject);
            if (id == Guid.Empty) return;

            Transform t = mover.transform;
            bool still = !mover.isActiveAndEnabled;
            Health health = mover.GetComponent<Health>();
            if (health != null && health.IsDeadOrUnconscious) still = true;
            var sample = new Sample
            {
                Position = t.position,
                Facing = t.eulerAngles.y,
                Speed = still ? 0f : CurrentSpeed(mover),
                Desired = DesiredSpeed(mover),
            };
            if (!IsFinite(sample.Position) || !IsFinite(sample.Facing) || !IsFinite(sample.Speed) || !IsFinite(sample.Desired)) return;
            if (!full && _sent.TryGetValue(id, out Sample last) && Same(last, sample)) return;
            _sent[id] = sample;

            if (message == null) message = new MessageWriter(MessageType.Positions).Int(0);
            message.Guid(id).Float(sample.Position.x).Float(sample.Position.y).Float(sample.Position.z)
                .Float(sample.Facing).Float(sample.Speed).Float(sample.Desired);
            count++;
        }

        private static bool Same(Sample a, Sample b)
        {
            return (a.Position - b.Position).sqrMagnitude < 0.0001f
                && Mathf.Abs(Mathf.DeltaAngle(a.Facing, b.Facing)) < 1f
                && Mathf.Abs(a.Speed - b.Speed) < 0.01f
                && Mathf.Abs(a.Desired - b.Desired) < 0.01f;
        }

        // ---- guest ----

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            if (sender != null || reader.Type != MessageType.Positions) return;
            int count = reader.Int();
            if (count < 0 || count > MaxMovers) throw new FormatException("position count " + count);
            for (int i = 0; i < count; i++)
            {
                Guid id = reader.Guid();
                var sample = new Sample
                {
                    Position = new Vector3(reader.Float(), reader.Float(), reader.Float()),
                    Facing = reader.Float(),
                    Speed = reader.Float(),
                    Desired = reader.Float(),
                };
                if (!IsFinite(sample.Position) || !IsFinite(sample.Facing) || !IsFinite(sample.Speed) || !IsFinite(sample.Desired))
                    throw new FormatException("position is not a number");
                if (!_targets.TryGetValue(id, out Target target)) _targets[id] = target = new Target();
                target.To = sample;
                target.Remaining = SlideSeconds;
            }
        }

        private void PruneTargets(float now)
        {
            if (now < _nextTargetPrune) return;
            _nextTargetPrune = now + FullRefreshInterval;
            List<Guid> gone = null;
            foreach (Guid id in _targets.Keys)
                if (EntityRegistry.ResolveObject(id) == null) (gone ?? (gone = new List<Guid>())).Add(id);
            if (gone != null) foreach (Guid id in gone) _targets.Remove(id);
        }

        /// <summary>Called for every character every frame on a guest, in place of the game's own movement.</summary>
        public static void Drive(Mover mover)
        {
            PositionSync self = s_instance;
            if (self == null) return;
            Guid id = EntityRegistry.NetworkId(mover.gameObject);
            if (id == Guid.Empty || !self._targets.TryGetValue(id, out Target target))
            {
                // The host has not said anything about this one: it stands still.
                CurrentSpeed(mover) = 0f;
                DesiredSpeed(mover) = 0f;
                return;
            }

            // Cover the remaining distance in the remaining time. Each frame advances by
            // its own share, so this also keeps up when frames are slower than the host
            // sends (then every frame simply lands on the newest position).
            Transform t = mover.transform;
            float dt = Time.unscaledDeltaTime;
            float share = target.Remaining <= dt ? 1f : dt / target.Remaining;
            target.Remaining = Mathf.Max(0f, target.Remaining - dt);

            Vector3 from = t.position, to = target.To.Position;
            if ((to - from).sqrMagnitude > SnapDistance * SnapDistance) share = 1f;

            t.position = Vector3.Lerp(from, to, share);
            t.rotation = Quaternion.Euler(0f, Mathf.LerpAngle(t.eulerAngles.y, target.To.Facing, share), 0f);
            CurrentSpeed(mover) = target.To.Speed;
            DesiredSpeed(mover) = target.To.Desired;
        }

        private static bool IsFinite(Vector3 v) => IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);
        private static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    }
}
