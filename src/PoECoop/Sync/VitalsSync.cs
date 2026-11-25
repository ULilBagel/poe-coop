using System;
using System.Collections.Generic;
using HarmonyLib;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>
    /// The host owns health and stamina. Guests receive raw values and only the
    /// animation and collision changes needed to show someone down or revived.
    /// Saved objects keep their InstanceID; dynamic objects use EntityRegistry.
    /// </summary>
    public sealed class VitalsSync
    {
        private const float SendInterval = 0.1f;
        private const float FullRefreshInterval = 3f;
        // Keeps the periodic refresh out of the frame the other streams use after a forced one.
        private const float FullRefreshPhase = 0.75f;
        private const float TrackedScanInterval = 30f;
        private float _nextTrackedScan;
        private const int MaxAnimationOwners = 4096;
        private const int MaxRecords = 16384;
        private const float MaxVitalValue = 1000000f;
        private const float MaxUnconsciousDelay = 3600f;
        private const int CountOffset = 10; // type, epoch, revision and full flag precede it

        private static readonly AccessTools.FieldRef<Health, float> CurrentHealth =
            AccessTools.FieldRefAccess<Health, float>("m_currentHealth");
        private static readonly AccessTools.FieldRef<Health, float> CurrentStamina =
            AccessTools.FieldRefAccess<Health, float>("m_currentStamina");
        private static readonly AccessTools.FieldRef<Health, float> MaxHealthOverride =
            AccessTools.FieldRefAccess<Health, float>("m_maxHealthOverride");
        private static readonly AccessTools.FieldRef<Health, float> MaxStaminaOverride =
            AccessTools.FieldRefAccess<Health, float>("m_maxStaminaOverride");
        private static readonly AccessTools.FieldRef<Health, float> UnconsciousDelay =
            AccessTools.FieldRefAccess<Health, float>("m_unconsciousDelayTimer");
        private static readonly AccessTools.FieldRef<Health, bool> DecaySuspended =
            AccessTools.FieldRefAccess<Health, bool>("m_DecaySuspended");

        private static VitalsSync s_instance;

        private readonly Session _session;
        private readonly Dictionary<Guid, Sample> _sent = new Dictionary<Guid, Sample>();
        private readonly Dictionary<Guid, Presentation> _presentations = new Dictionary<Guid, Presentation>();
        private readonly Dictionary<Health, Sample> _latest = new Dictionary<Health, Sample>();
        private readonly HashSet<Health> _pendingStandups = new HashSet<Health>();

        // Host: every Health that has started or been found by a scan; destroyed ones are pruned.
        private readonly List<Health> _tracked = new List<Health>();
        private readonly HashSet<Health> _trackedSet = new HashSet<Health>(ReferenceComparer.Instance);
        private readonly HashSet<Guid> _seen = new HashSet<Guid>();
        private readonly List<Guid> _gone = new List<Guid>();

        // Guest: lookups that would otherwise repeat for every record and every animated actor each frame.
        private readonly Dictionary<Guid, Resolved> _resolved = new Dictionary<Guid, Resolved>();
        private readonly Dictionary<AnimationController, Health> _animationOwners =
            new Dictionary<AnimationController, Health>(AnimationComparer.Instance);
        private readonly List<KeyValuePair<Guid, Sample>> _records = new List<KeyValuePair<Guid, Sample>>();

        private float _nextSend, _nextFullRefresh;
        private int _epoch, _revision;
        private bool _active;

        private struct Sample
        {
            public float CurrentHealth, MaxHealth, CurrentStamina, MaxStamina, UnconsciousDelay;
            public byte Flags;
        }

        private sealed class Presentation
        {
            public int State = -1; // -1 unknown, 0 alive, 1 unconscious, 2 dead
            public bool MoverCollision;
        }

        private struct Resolved
        {
            public GameObject Object;
            public Health Health;
        }

        // Identity only: a destroyed component must still be removable from the tracked set.
        private sealed class ReferenceComparer : IEqualityComparer<Health>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(Health a, Health b) => ReferenceEquals(a, b);
            public int GetHashCode(Health health) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(health);
        }

        private sealed class AnimationComparer : IEqualityComparer<AnimationController>
        {
            public static readonly AnimationComparer Instance = new AnimationComparer();
            public bool Equals(AnimationController a, AnimationController b) => ReferenceEquals(a, b);
            public int GetHashCode(AnimationController controller) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(controller);
        }

        public VitalsSync(Session session)
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
            _session.GameMessage -= OnGameMessage;
            _session.PlayerJoined -= OnPlayerJoined;
            GameState.OnLevelLoaded -= OnLevelLoaded;
            GameState.OnLevelUnload -= OnLevelLoaded;
            if (s_instance == this) s_instance = null;
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            if (!_session.IsHost) return;
            _nextSend = 0f;
            _nextFullRefresh = 0f;
        }

        private void OnLevelLoaded(object sender, EventArgs e)
        {
            _sent.Clear();
            _presentations.Clear();
            _latest.Clear();
            _pendingStandups.Clear();
            _resolved.Clear();
            _animationOwners.Clear();
            PruneTracked();
            _revision = 0;
            _nextSend = 0f;
            _nextFullRefresh = 0f;
            if (_session.IsHost) _epoch++;
            else _epoch = -1;
        }

        /// <summary>Called as a Health starts, so new actors are sampled without a scene scan every tick.</summary>
        internal static void Track(Health health)
        {
            VitalsSync self = s_instance;
            if (self != null && self._trackedSet.Add(health)) self._tracked.Add(health);
        }

        private void PruneTracked()
        {
            for (int i = _tracked.Count - 1; i >= 0; i--)
            {
                Health health = _tracked[i];
                if (health != null) continue;
                _trackedSet.Remove(health);
                _tracked[i] = _tracked[_tracked.Count - 1];
                _tracked.RemoveAt(_tracked.Count - 1);
            }
        }

        /// <summary>Repairs the tracked set: actors that started before this module, or were missed.</summary>
        private void ScanTracked()
        {
            PruneTracked();
            Health[] healths = UnityEngine.Object.FindObjectsOfType<Health>();
            int missed = 0;
            for (int i = 0; i < healths.Length; i++)
            {
                if (healths[i] == null || !_trackedSet.Add(healths[i])) continue;
                _tracked.Add(healths[i]);
                missed++;
            }
            // Shows whether tracking at start ever misses an actor between searches.
            if (missed > 0) PerfStats.Count("vitals.scan_found", missed);
        }

        public void Update(float now)
        {
            if (!_session.IsHost)
            {
                if (!_session.IsGuest && _active) Clear();
                return;
            }
            _active = true;
            if (now < _nextSend) return;
            _nextSend = now + SendInterval;
            if (_session.Players.Count < 2 || !GameState.IsInGameSession || GameState.IsLoading) return;

            long started = PerfStats.Begin();
            bool full = now >= _nextFullRefresh;
            if (full)
            {
                // A forced refresh (join, level load) is immediate; later ones keep their own phase.
                bool forced = _nextFullRefresh <= 0f;
                _nextFullRefresh = now + FullRefreshInterval + (forced ? FullRefreshPhase : 0f);
                // Actors are tracked as they start, so the scene search only has to repair the set:
                // at once after a join or level change, then every TrackedScanInterval.
                if (forced || now >= _nextTrackedScan)
                {
                    _nextTrackedScan = now + TrackedScanInterval;
                    long scanStarted = PerfStats.Begin();
                    ScanTracked();
                    PerfStats.End("vitals.scan", scanStarted);
                }
                else PruneTracked();
                _seen.Clear();
            }

            MessageWriter message = null;
            if (full) message = NewMessage(full: true);
            int count = 0, skipped = 0;
            for (int i = 0; i < _tracked.Count && count < MaxRecords; i++)
            {
                Health health = _tracked[i];
                if (health == null) continue;
                GameObject obj = health.gameObject;
                // Matches what a scene scan returns: inactive actors are not sampled.
                if (!obj.activeInHierarchy || !TryGetId(obj, out Guid id)) continue;
                if (full) _seen.Add(id);
                var sample = Capture(health);
                if (!full && _sent.TryGetValue(id, out Sample last) && Same(last, sample))
                {
                    skipped++;
                    continue;
                }
                _sent[id] = sample;
                if ((sample.Flags & 2) != 0) AttackVisualSync.OnEntityDeath(id);
                if (message == null) message = NewMessage(full: false);
                Write(message, id, sample);
                count++;
            }

            if (full && _sent.Count > _seen.Count)
            {
                _gone.Clear();
                foreach (Guid id in _sent.Keys) if (!_seen.Contains(id)) _gone.Add(id);
                for (int i = 0; i < _gone.Count; i++) _sent.Remove(_gone[i]);
                _gone.Clear();
            }
            if (message != null)
            {
                byte[] data = message.ToArray();
                data[CountOffset] = (byte)count;
                data[CountOffset + 1] = (byte)(count >> 8);
                data[CountOffset + 2] = (byte)(count >> 16);
                data[CountOffset + 3] = (byte)(count >> 24);
                _session.SendToGuests(data);
                _revision++;
                PerfStats.Count(full ? "vitals.full" : "vitals.delta");
                PerfStats.Count("vitals.records", count);
            }
            PerfStats.Count("vitals.skipped", skipped);
            PerfStats.End("vitals.capture", started);
        }

        private MessageWriter NewMessage(bool full)
        {
            return new MessageWriter(MessageType.Vitals).Int(_epoch).Int(_revision + 1)
                .Byte((byte)(full ? 1 : 0)).Int(0);
        }

        private static Sample Capture(Health health)
        {
            byte flags = 0;
            if (health.Unconscious) flags |= 1;
            if (health.Dead) flags |= 2;
            if (health.ShouldDecay) flags |= 4;
            if (health.IsDecaySuspended) flags |= 8;
            return new Sample
            {
                CurrentHealth = BoundedVital(health.CurrentHealth),
                MaxHealth = BoundedVital(health.MaxHealth),
                CurrentStamina = BoundedVital(health.CurrentStamina),
                MaxStamina = BoundedVital(health.MaxStamina),
                UnconsciousDelay = Bounded(UnconsciousDelay(health), MaxUnconsciousDelay),
                Flags = flags,
            };
        }

        private static void Write(MessageWriter message, Guid id, Sample sample)
        {
            message.Guid(id).Float(sample.CurrentHealth).Float(sample.MaxHealth)
                .Float(sample.CurrentStamina).Float(sample.MaxStamina)
                .Float(sample.UnconsciousDelay).Byte(sample.Flags);
        }

        private static bool Same(Sample a, Sample b)
        {
            return a.Flags == b.Flags
                && Mathf.Abs(a.CurrentHealth - b.CurrentHealth) < 0.01f
                && Mathf.Abs(a.MaxHealth - b.MaxHealth) < 0.01f
                && Mathf.Abs(a.CurrentStamina - b.CurrentStamina) < 0.01f
                && Mathf.Abs(a.MaxStamina - b.MaxStamina) < 0.01f
                && Mathf.Abs(a.UnconsciousDelay - b.UnconsciousDelay) < 0.02f;
        }

        // ---- guest ----

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            if (sender != null || reader.Type != MessageType.Vitals || !_session.IsGuest) return;
            _active = true;
            int epoch = reader.Int();
            int revision = reader.Int();
            byte full = reader.Byte();
            int count = reader.Int();
            if (full > 1) throw new FormatException("vitals full flag " + full);
            if (count < 0 || count > MaxRecords) throw new FormatException("vitals count " + count);
            if (epoch == _epoch && revision <= _revision)
            {
                PerfStats.Count("vitals.skipped", count);
                return;
            }

            long started = PerfStats.Begin();
            List<KeyValuePair<Guid, Sample>> records = _records;
            records.Clear();
            for (int i = 0; i < count; i++)
            {
                Guid id = reader.Guid();
                if (id == Guid.Empty) throw new FormatException("empty vitals id");
                var sample = new Sample
                {
                    CurrentHealth = ReadVital(reader.Float()),
                    MaxHealth = ReadVital(reader.Float()),
                    CurrentStamina = ReadVital(reader.Float()),
                    MaxStamina = ReadVital(reader.Float()),
                    UnconsciousDelay = ReadBounded(reader.Float(), MaxUnconsciousDelay, "unconscious delay"),
                    Flags = reader.Byte(),
                };
                if ((sample.Flags & ~15) != 0) throw new FormatException("vitals flags " + sample.Flags);
                records.Add(new KeyValuePair<Guid, Sample>(id, sample));
            }

            if (epoch != _epoch)
            {
                _presentations.Clear();
                _latest.Clear();
                _pendingStandups.Clear();
                _resolved.Clear();
                _animationOwners.Clear();
            }
            _epoch = epoch;
            _revision = revision;
            for (int i = 0; i < records.Count; i++) Apply(records[i].Key, records[i].Value);
            PerfStats.Count(full == 1 ? "vitals.full" : "vitals.delta");
            PerfStats.Count("vitals.records", records.Count);
            records.Clear();
            PerfStats.End("vitals.apply", started);
        }

        /// <summary>The registry is still asked every time, so a rebound id is noticed; only the component lookup is kept.</summary>
        private Health ResolveHealth(Guid id)
        {
            GameObject obj = EntityRegistry.ResolveObject(id);
            if (obj == null) obj = InstanceID.GetObjectByID(id);
            if (obj == null) return null;
            if (_resolved.TryGetValue(id, out Resolved known) && ReferenceEquals(known.Object, obj) && known.Health != null)
                return known.Health;
            Health health = obj.GetComponent<Health>();
            if (health != null) _resolved[id] = new Resolved { Object = obj, Health = health };
            return health;
        }

        private void Apply(Guid id, Sample sample)
        {
            Health health = ResolveHealth(id);
            if (health == null)
            {
                PerfStats.Count("vitals.skipped");
                return;
            }

            bool wasDown = health.Dead || health.Unconscious;
            CurrentHealth(health) = sample.CurrentHealth;
            CurrentStamina(health) = sample.CurrentStamina;
            MaxHealthOverride(health) = sample.MaxHealth;
            MaxStaminaOverride(health) = sample.MaxStamina;
            UnconsciousDelay(health) = sample.UnconsciousDelay;
            health.ShouldDecay = (sample.Flags & 4) != 0;
            DecaySuspended(health) = (sample.Flags & 8) != 0;
            _latest[health] = sample;

            if (!Plugin.IsMirroring) return;
            int state = (sample.Flags & 2) != 0 ? 2 : (sample.Flags & 1) != 0 ? 1 : 0;
            if (!_presentations.TryGetValue(id, out Presentation presentation))
                _presentations[id] = presentation = new Presentation();
            if (presentation.State == state) return;
            if (state == 2 && presentation.State != 2) AttackVisualSync.OnEntityDeath(id);

            if (state != 0)
            {
                if (presentation.State <= 0 || !wasDown)
                {
                    Mover mover = health.GetComponent<Mover>();
                    presentation.MoverCollision = mover != null && mover.Collision;
                    ShowDown(health);
                }
            }
            else if (wasDown || presentation.State > 0)
            {
                ShowRevived(health, presentation);
            }
            presentation.State = state;
        }

        private void ShowDown(Health health)
        {
            _pendingStandups.Remove(health);
            AnimationController animation = health.GetComponent<AnimationController>();
            if (animation != null)
            {
                animation.ClearReactions();
                animation.DesiredAction.Reset();
                animation.SetReaction(AnimationController.ReactionType.Dead);
            }
            SetDownCollision(health, down: true);
        }

        private void ShowRevived(Health health, Presentation presentation)
        {
            _pendingStandups.Add(health);
            AnimationController animation = health.GetComponent<AnimationController>();
            if (animation != null)
            {
                animation.ClearReactions();
                animation.SetReaction(AnimationController.ReactionType.Standup);
            }
            SetDownCollision(health, down: false);
            Mover mover = health.GetComponent<Mover>();
            if (mover != null) mover.Collision = presentation.MoverCollision;
        }

        private static void SetDownCollision(Health health, bool down)
        {
            Collider collider = health.GetComponent<Collider>();
            if (collider != null) collider.enabled = !down;
            Rigidbody body = health.GetComponent<Rigidbody>();
            if (body != null) body.detectCollisions = !down;
            Mover mover = health.GetComponent<Mover>();
            if (mover != null && down) mover.Collision = false;
        }

        internal static void MaintainReaction(AnimationController controller)
        {
            VitalsSync self = s_instance;
            if (self == null || self._latest.Count == 0 || !Plugin.IsMirroring || controller == null) return;
            long started = PerfStats.Begin();
            self.MaintainReactionFor(controller);
            PerfStats.End("vitals.update", started);
        }

        private void MaintainReactionFor(AnimationController controller)
        {
            VitalsSync self = this;
            if (!self._animationOwners.TryGetValue(controller, out Health health))
            {
                // Remembered even when there is none, so actors without Health are not searched every frame.
                if (self._animationOwners.Count >= MaxAnimationOwners) self._animationOwners.Clear();
                health = controller.GetComponent<Health>();
                self._animationOwners[controller] = health;
            }
            if (health == null || !self._latest.TryGetValue(health, out Sample sample)) return;
            if (controller.CurrentAvatar == null) controller.BindComponents();
            if (controller.CurrentAvatar == null) return;

            if ((sample.Flags & 3) != 0)
            {
                self._pendingStandups.Remove(health);
                if (controller.CurrentReaction != AnimationController.ReactionType.Dead
                    && controller.CurrentReaction != AnimationController.ReactionType.DeadProne)
                    controller.SetReaction(AnimationController.ReactionType.Dead);
            }
            else if (self._pendingStandups.Contains(health))
            {
                if (controller.CurrentReaction == AnimationController.ReactionType.Standup
                    && controller.IsPerformingReaction(AnimationController.ReactionType.Standup))
                    self._pendingStandups.Remove(health);
                else if (controller.CurrentReaction != AnimationController.ReactionType.Standup)
                    controller.SetReaction(AnimationController.ReactionType.Standup);
            }
        }

        internal static bool TryGetMaxHealth(Health health, out float value)
        {
            return TryGetMax(health, stamina: false, out value);
        }

        internal static bool TryGetMaxStamina(Health health, out float value)
        {
            return TryGetMax(health, stamina: true, out value);
        }

        private static bool TryGetMax(Health health, bool stamina, out float value)
        {
            value = 0f;
            VitalsSync self = s_instance;
            if (self == null || self._latest.Count == 0 || !Plugin.IsMirroring || health == null
                || !self._latest.TryGetValue(health, out Sample sample)) return false;
            value = stamina ? sample.MaxStamina : sample.MaxHealth;
            return true;
        }

        private void Clear()
        {
            _active = false;
            _sent.Clear();
            _presentations.Clear();
            _latest.Clear();
            _pendingStandups.Clear();
            _resolved.Clear();
            _animationOwners.Clear();
            _epoch = 0;
            _revision = 0;
            _nextSend = 0f;
            _nextFullRefresh = 0f;
        }

        private static bool TryGetId(GameObject obj, out Guid id)
        {
            id = EntityRegistry.NetworkId(obj);
            if (id == Guid.Empty)
            {
                InstanceID instance = obj.GetComponent<InstanceID>();
                if (instance != null) id = instance.Guid;
            }
            return id != Guid.Empty;
        }

        private static float BoundedVital(float value) => Bounded(value, MaxVitalValue);

        private static float Bounded(float value, float max)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
            return Mathf.Clamp(value, 0f, max);
        }

        private static float ReadVital(float value) => ReadBounded(value, MaxVitalValue, "vital");

        private static float ReadBounded(float value, float max, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > max)
                throw new FormatException(name + " out of range");
            return value;
        }
    }
}
