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
        /// <summary>Added once after a forced refresh, so this stream's periodic refresh does not share a frame with the others.</summary>
        private const float FullRefreshPhase = 0.15f;
        /// <summary>Slightly longer than SendInterval, so a guest character is still moving when the next position arrives.</summary>
        private const float SlideSeconds = 0.07f;
        /// <summary>A walking character slides for this many times the widest of its last two gaps between positions, within SlideSeconds..MaxSlideSeconds.</summary>
        private const float SlideGapFactor = 1.3f;
        private const float MaxSlideSeconds = 0.2f;
        /// <summary>A longer gap than this says nothing about the pace positions arrive at; timing starts over.</summary>
        private const float StaleGap = 0.25f;
        /// <summary>At or below this the host's character counts as standing.</summary>
        private const float MovingSpeed = 0.01f;
        /// <summary>
        /// A walking character whose next position is late carries on at the pace of its last step, for
        /// no longer than this and no further than that step or MaxCoastDistance.
        /// </summary>
        private const float MaxCoastSeconds = 0.1f;
        private const float MaxCoastDistance = 0.5f;
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
        private readonly Dictionary<Guid, Sent> _sent = new Dictionary<Guid, Sent>();
        private float _nextSend, _nextFullRefresh;
        private bool _forceFullRefresh = true;
        private int _skipped;

        // Guest: where each character should be.
        private readonly Dictionary<Guid, Target> _targets = new Dictionary<Guid, Target>();
        private readonly List<Guid> _gone = new List<Guid>();
        private float _nextTargetPrune;

        private struct Sample
        {
            public Vector3 Position;
            public float Facing, Speed, Desired;
        }

        private struct Sent
        {
            public Sample Sample;
            /// <summary>The id as it goes on the wire, worked out once so that sending it allocates nothing.</summary>
            public long IdLow, IdHigh;
        }

        private sealed class Target
        {
            public Sample To;
            /// <summary>Seconds left to reach To. Reset when a new position arrives.</summary>
            public float Remaining;
            /// <summary>The character was placed exactly on To; HeldRotation is what its transform then read back.</summary>
            public bool Held;
            public Quaternion HeldRotation;
            /// <summary>The slide ended and nothing newer has arrived since.</summary>
            public bool Waiting;
            /// <summary>When To arrived (unscaled time), and the last two gaps between positions while walking; 0 when unknown.</summary>
            public float Arrived, Gap, PreviousGap;
            /// <summary>How long a slide to To lasts.</summary>
            public float Slide;
            /// <summary>If the slide ends first: carry on with CoastVelocity for CoastLeft seconds. CoastSeconds re-arms it.</summary>
            public Vector3 CoastVelocity;
            public float CoastSeconds, CoastLeft;
            /// <summary>The character was carried past To and has not been brought back yet.</summary>
            public bool Coasting;
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
            if (!_session.IsHost) return;
            _nextFullRefresh = 0f;
            _forceFullRefresh = true;
        }

        private void OnLevelLoaded(object sender, EventArgs e)
        {
            // Different level, different characters.
            _handled.Clear();
            _sent.Clear();
            _targets.Clear();
            _nextFullRefresh = 0f;
            _forceFullRefresh = true;
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
            // A fixed grid: a frame that runs late does not push the following sends back, so the
            // real period is SendInterval whenever the frame rate allows. After a hitch (or while
            // frames are longer than the interval) start over from now instead of catching up.
            _nextSend += SendInterval;
            if (_nextSend <= now) _nextSend = now + SendInterval;
            if (_session.Players.Count < 2 || !GameState.IsInGameSession || GameState.IsLoading) return;

            long started = PerfStats.Begin();
            Capture(now);
            PerfStats.End("position.capture", started);
        }

        private void Capture(float now)
        {
            bool full = _forceFullRefresh || now >= _nextFullRefresh;
            if (full)
            {
                _nextFullRefresh = now + FullRefreshInterval + (_forceFullRefresh ? FullRefreshPhase : 0f);
                _forceFullRefresh = false;
            }

            MessageWriter message = null;
            int count = 0;
            _skipped = 0;
            _handled.Clear();
            List<Mover> movers = Mover.Movers;
            for (int i = 0; i < movers.Count; i++) AddSample(movers[i], full, ref message, ref count);
            IEnumerable<GameObject> registered = EntityRegistry.RegisteredObjects;
            if (registered is Dictionary<GameObject, Guid>.KeyCollection keys)
            {
                // Same objects; walking the concrete collection does not allocate an enumerator every tick.
                foreach (GameObject obj in keys)
                {
                    if (obj != null) AddSample(obj.GetComponent<Mover>(), full, ref message, ref count);
                    if (count >= MaxMovers) break;
                }
            }
            else
            {
                foreach (GameObject obj in registered)
                {
                    if (obj != null) AddSample(obj.GetComponent<Mover>(), full, ref message, ref count);
                    if (count >= MaxMovers) break;
                }
            }
            if (full)
            {
                // The game's Mover list omits objects disabled in the hierarchy.
                long scanStarted = PerfStats.Begin();
                Mover[] all = Resources.FindObjectsOfTypeAll<Mover>();
                foreach (Mover mover in all)
                {
                    if (mover != null && mover.gameObject.scene.IsValid()) AddSample(mover, true, ref message, ref count);
                    if (count >= MaxMovers) break;
                }
                PerfStats.End("position.scan", scanStarted);
            }
            PerfStats.Count("position.skipped", _skipped);
            if (message == null) return;

            byte[] data = message.ToArray();
            // Fill in the count that was left as 0 after the type byte.
            data[1] = (byte)count; data[2] = (byte)(count >> 8); data[3] = (byte)(count >> 16); data[4] = (byte)(count >> 24);
            _session.SendToGuests(data);
            PerfStats.Count(full ? "position.full" : "position.delta");
            PerfStats.Count("position.records", count);
        }

        private void AddSample(Mover mover, bool full, ref MessageWriter message, ref int count)
        {
            if (mover == null || count >= MaxMovers || !_handled.Add(mover)) return;
            Guid id = EntityRegistry.NetworkId(mover.gameObject);
            if (id == Guid.Empty) return;

            Transform t = mover.transform;
            // A speed of zero is sent as zero either way, so only a moving character needs the checks that force it to zero.
            float speed = CurrentSpeed(mover);
            if (speed != 0f && IsStill(mover)) speed = 0f;
            var sample = new Sample
            {
                Position = t.position,
                Facing = t.eulerAngles.y,
                Speed = speed,
                Desired = DesiredSpeed(mover),
            };
            if (!IsFinite(sample.Position) || !IsFinite(sample.Facing) || !IsFinite(sample.Speed) || !IsFinite(sample.Desired)) return;
            bool known = _sent.TryGetValue(id, out Sent sent);
            if (!full && known && Same(sent.Sample, sample))
            {
                _skipped++;
                return;
            }
            if (!known)
            {
                byte[] raw = id.ToByteArray();
                sent.IdLow = LittleEndianLong(raw, 0);
                sent.IdHigh = LittleEndianLong(raw, 8);
            }
            sent.Sample = sample;
            _sent[id] = sent;

            if (message == null) message = new MessageWriter(MessageType.Positions).Int(0);
            // The same sixteen bytes MessageWriter.Guid writes, without a new array for every record.
            message.Long(sent.IdLow).Long(sent.IdHigh).Float(sample.Position.x).Float(sample.Position.y).Float(sample.Position.z)
                .Float(sample.Facing).Float(sample.Speed).Float(sample.Desired);
            count++;
        }

        private static bool IsStill(Mover mover)
        {
            if (!mover.isActiveAndEnabled) return true;
            Health health = mover.GetComponent<Health>();
            return health != null && health.IsDeadOrUnconscious;
        }

        private static long LittleEndianLong(byte[] data, int offset)
        {
            long value = 0;
            for (int i = 7; i >= 0; i--) value = (value << 8) | data[offset + i];
            return value;
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
            long started = PerfStats.Begin();
            int count = reader.Int();
            if (count < 0 || count > MaxMovers) throw new FormatException("position count " + count);
            float now = Time.unscaledTime;
            float movingSlide = 0f;
            for (int i = 0; i < count; i++)
            {
                Guid id = ReadId(reader);
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
                Pace(target, sample, now);
                target.To = sample;
                target.Remaining = target.Slide;
                target.Held = false;
                target.Waiting = false;
                target.Coasting = false;
                if (sample.Speed > MovingSpeed) movingSlide = target.Slide;
            }
            if (movingSlide > 0f) PerfStats.Gauge("position.slide_ms", movingSlide * 1000f);
            PerfStats.Count("position.records", count);
            PerfStats.End("position.apply", started);
        }

        /// <summary>
        /// Works out, before <paramref name="next"/> replaces target.To, how long the slide to it lasts and
        /// how the character may carry on if the position after it is late. Anything but a character that
        /// keeps walking in steps arriving at a steady pace gets the fixed slide and no carrying on.
        /// </summary>
        private static void Pace(Target target, Sample next, float now)
        {
            float gap = now - target.Arrived;
            bool walking = next.Speed > MovingSpeed && target.To.Speed > MovingSpeed;
            if (walking && gap <= 0f)
            {
                // A second position in the same frame replaces the first; the pace measured so far still holds.
                target.CoastLeft = target.CoastSeconds;
                return;
            }
            target.Arrived = now;
            Vector3 step = next.Position - target.To.Position;
            float length = step.magnitude;
            if (!walking || gap > StaleGap || length > SnapDistance)
            {
                Untimed(target);
                return;
            }

            target.PreviousGap = target.Gap;
            target.Gap = gap;
            float widest = Mathf.Max(target.Gap, target.PreviousGap);
            target.Slide = Mathf.Clamp(widest * SlideGapFactor, SlideSeconds, MaxSlideSeconds);

            // The host never sends faster than SendInterval; a shorter gap here is two positions bunched up on the way.
            target.CoastVelocity = step / Mathf.Max(gap, SendInterval);
            float seconds = Mathf.Min(widest, MaxCoastSeconds);
            float distance = target.CoastVelocity.magnitude * seconds;
            float limit = Mathf.Min(length, MaxCoastDistance);
            if (distance > limit) seconds *= limit / distance;
            target.CoastSeconds = target.CoastLeft = seconds;
        }

        /// <summary>Back to the fixed slide, with nothing known about the pace of arrival.</summary>
        private static void Untimed(Target target)
        {
            target.Gap = target.PreviousGap = 0f;
            target.Slide = SlideSeconds;
            target.CoastSeconds = target.CoastLeft = 0f;
        }

        /// <summary>
        /// The slide ended while the host's character was still walking. Carries it on for what is left of
        /// its allowance, then holds it there; once the gap is stale, sends it back to the last position
        /// the host gave. False when the caller should go on and slide to that position.
        /// </summary>
        private static bool Coast(Target target, Transform t, Vector3 from, float dt)
        {
            if (target.CoastLeft > 0f)
            {
                // Paused: the host's characters stand still mid-step and no positions are sent.
                if (Time.timeScale <= 0f) target.CoastLeft = 0f;
                else
                {
                    float step = Mathf.Min(dt, target.CoastLeft);
                    target.CoastLeft -= step;
                    t.position = from + target.CoastVelocity * step;
                    return true;
                }
            }
            if (Time.unscaledTime - target.Arrived < StaleGap) return true;
            target.Coasting = false;
            target.Remaining = SlideSeconds;
            PerfStats.Count("position.stalled");
            return false;
        }

        /// <summary>Reads the sixteen bytes of MessageWriter.Guid without the array MessageReader.Guid allocates.</summary>
        private static Guid ReadId(MessageReader reader)
        {
            int a = reader.Int();
            int bc = reader.Int();
            return new Guid(a, (short)bc, (short)(bc >> 16), reader.Byte(), reader.Byte(), reader.Byte(), reader.Byte(),
                reader.Byte(), reader.Byte(), reader.Byte(), reader.Byte());
        }

        private void PruneTargets(float now)
        {
            if (now < _nextTargetPrune) return;
            _nextTargetPrune = now + FullRefreshInterval;
            long started = PerfStats.Begin();
            _gone.Clear();
            foreach (Guid id in _targets.Keys)
                if (EntityRegistry.ResolveObject(id) == null) _gone.Add(id);
            for (int i = 0; i < _gone.Count; i++) _targets.Remove(_gone[i]);
            PerfStats.End("position.update", started);
        }

        /// <summary>Called for every character every frame on a guest, in place of the game's own movement.</summary>
        public static void Drive(Mover mover)
        {
            PositionSync self = s_instance;
            if (self == null) return;
            long started = PerfStats.Begin();
            self.Place(mover);
            PerfStats.End("position.update", started);
        }

        private void Place(Mover mover)
        {
            Guid id = EntityRegistry.NetworkId(mover.gameObject);
            if (id == Guid.Empty || !_targets.TryGetValue(id, out Target target))
            {
                // The host has not said anything about this one: it stands still.
                CurrentSpeed(mover) = 0f;
                DesiredSpeed(mover) = 0f;
                return;
            }

            CurrentSpeed(mover) = target.To.Speed;
            DesiredSpeed(mover) = target.To.Desired;

            Transform t = mover.transform;
            Vector3 from = t.position, to = target.To.Position;
            float dt = Time.unscaledDeltaTime;
            if (target.Remaining <= 0f)
            {
                if (!target.Waiting)
                {
                    target.Waiting = true;
                    // Still walking on the host, but the next position did not arrive in time.
                    if (target.To.Speed > MovingSpeed)
                    {
                        PerfStats.Count("position.starved");
                        if (target.CoastLeft > 0f && Time.timeScale > 0f)
                        {
                            target.Coasting = true;
                            target.Held = false;
                            PerfStats.Count("position.coasted");
                        }
                    }
                }
                if (target.Coasting)
                {
                    if (Coast(target, t, from, dt)) return;
                }
                // Already exactly where the host put it and nothing has moved it since: nothing to write.
                else if (target.Held && Identical(from, to) && Identical(t.rotation, target.HeldRotation)) return;
            }

            // Cover the remaining distance in the remaining time. Each frame advances by
            // its own share, so this also keeps up when frames are slower than the host
            // sends (then every frame simply lands on the newest position).
            float share = target.Remaining <= dt ? 1f : dt / target.Remaining;
            target.Remaining = Mathf.Max(0f, target.Remaining - dt);
            if ((to - from).sqrMagnitude > SnapDistance * SnapDistance)
            {
                // A jump, not a walk: nothing measured before it applies after it.
                share = 1f;
                Untimed(target);
            }

            if (share < 1f)
            {
                t.position = Vector3.Lerp(from, to, share);
                t.rotation = Quaternion.Euler(0f, Mathf.LerpAngle(t.eulerAngles.y, target.To.Facing, share), 0f);
                return;
            }
            t.position = to;
            t.rotation = Quaternion.Euler(0f, target.To.Facing, 0f);
            if (target.Remaining > 0f) return;
            target.HeldRotation = t.rotation;
            target.Held = true;
        }

        private static bool Identical(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
        private static bool Identical(Quaternion a, Quaternion b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;

        private static bool IsFinite(Vector3 v) => IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);
        private static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
    }
}
