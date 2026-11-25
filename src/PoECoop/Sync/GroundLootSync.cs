using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>
    /// Replicates ground loot objects (dropped loot bags and lootable bodies) from
    /// the host to guests as presentation-only objects. See docs/PLAYTEST-ISSUES.md.
    /// The guest object carries the host's id, so the existing Use order, loot
    /// session and container inventory snapshots work against it unchanged.
    /// </summary>
    public sealed class GroundLootSync
    {
        private enum Kind : byte { Bag = 1, Body = 2 }

        private const int MaxRecords = 256;
        private const int MaxPrefabKeyLength = 96;
        private const int MaxLabelLength = 128;
        private const float MaxCoordinate = 1000000f;
        private const float ScanInterval = 0.5f;
        private const float IntegrityInterval = 6f;
        private const float RefreshPhaseOffset = 1.20f;
        private const float RetryInterval = 0.25f;
        private const float StandInDelay = 3f;
        private const float ColliderDelay = 3f;
        private const int HintScanFrames = 3;
        private const string DefaultBagPrefab = "DefaultDropItem";
        private const string BagTag = "DropItem";

        private const int FlagAreaLootable = 1;
        private const int FlagDeleteIfEmpty = 2;
        private const int FlagInteracted = 4;
        private const int FlagActive = 8;
        private const int AllFlags = 15;

        private const byte StateDelta = 0;
        private const byte StateFull = 1;
        /// <summary>Complete for what the host tracks, but the host has more ground loot than MaxRecords.</summary>
        private const byte StateFullTruncated = 2;

        private static readonly FieldInfo AllContainersField = AccessTools.Field(typeof(Container), "s_AllContainers");
        private static readonly MethodInfo CreateMeshForCollider = AccessTools.Method(typeof(Loot), "CreateMeshForCollider");
        private static readonly MethodInfo DeathAnimationComplete = AccessTools.Method(typeof(Loot), "DeathAnimationComplete");
        private static readonly FieldInfo ColliderRoutine = AccessTools.Field(typeof(Loot), "m_creatingMeshColliderCorouting");

        private sealed class Record
        {
            public Guid Id;
            public Kind Kind;
            public int Revision;
            public int Flags;
            public string PrefabKey = "";
            public string Label = "";
            public Vector3 Position;
            public Quaternion Rotation = Quaternion.identity;

            // Host: the tracked native container.
            public Container Container;

            // Guest presentation.
            public GameObject Object;
            public bool Applied;
            public bool CreatedObject;
            public bool AddedContainer;
            public bool FadeIn;
            public bool ColliderPending;
            public float FirstSeen;
            public float ColliderAt;
        }

        private static GroundLootSync s_instance;
        private static HashSet<Container> s_allContainers;
        private static bool s_bagTagMissing;

        private readonly Session _session;
        private readonly Ownership _ownership;
        private readonly Dictionary<Guid, Record> _records = new Dictionary<Guid, Record>();
        private readonly Dictionary<Container, Record> _hostByContainer = new Dictionary<Container, Record>();
        private readonly HashSet<Container> _hostIgnored = new HashSet<Container>();
        private readonly HashSet<Guid> _dirty = new HashSet<Guid>();
        private readonly List<Guid> _removed = new List<Guid>();
        private readonly List<Record> _scratch = new List<Record>();
        private HashSet<Guid> _orphanCandidates = new HashSet<Guid>();
        private HashSet<Guid> _orphanScratch = new HashSet<Guid>();

        private long _generation;
        private long _observedEpoch;
        private long _sequence;
        private float _nextScan;
        private float _nextIntegrity;
        private int _scanFrames;
        private bool _forceFull = true;
        private bool _truncated;

        private long _guestSequence;
        private float _nextRetry;
        private bool _guestWork;
        private bool _cullOrphans;
        private bool _warnedPrefab;

        public GroundLootSync(Session session, Ownership ownership)
        {
            _session = session;
            _ownership = ownership;
            _generation = session.Generation;
            _observedEpoch = FlowCoordinator.WorldEpoch;
            s_instance = this;
            session.GameMessage += OnGameMessage;
            session.PlayerJoined += OnPlayerJoined;
            session.WorldStateReady += OnWorldStateReady;
            session.FullStateRequested += OnFullStateRequested;
            session.SessionReset += OnSessionReset;
            GameState.OnLevelLoaded += OnLevelChanged;
            GameState.OnLevelUnload += OnLevelChanged;
        }

        public void Dispose()
        {
            _session.GameMessage -= OnGameMessage;
            _session.PlayerJoined -= OnPlayerJoined;
            _session.WorldStateReady -= OnWorldStateReady;
            _session.FullStateRequested -= OnFullStateRequested;
            _session.SessionReset -= OnSessionReset;
            GameState.OnLevelLoaded -= OnLevelChanged;
            GameState.OnLevelUnload -= OnLevelChanged;
            ResetWorld(true);
            if (s_instance == this) s_instance = null;
        }

        public void Update(float now)
        {
            SyncContext();
            if (_session.IsHost) UpdateHost(now);
            else if (_session.IsGuest) UpdateGuest(now);
        }

        /// <summary>Host: a native drop or container close just happened; look again on the next frames instead of waiting for the poll.</summary>
        internal static void NoteHostChange()
        {
            GroundLootSync self = s_instance;
            if (self != null && self._session.IsHost) self._scanFrames = HintScanFrames;
        }

        // ---- host capture ----

        private void UpdateHost(float now)
        {
            long epoch = FlowCoordinator.WorldEpoch;
            if (epoch <= 0 || _session.Players.Count < 2 || !GameState.IsInGameSession || GameState.IsLoading
                || GameState.IsInMainMenu)
                return;

            bool integrity = _forceFull || now >= _nextIntegrity;
            if (!integrity && _scanFrames <= 0 && now < _nextScan) return;

            long started = PerfStats.Begin();
            if (_scanFrames > 0) _scanFrames--;
            _nextScan = now + ScanInterval;
            // Containers that were not ground loot are looked at again only on the slow refresh.
            if (integrity) _hostIgnored.Clear();
            ScanHost();

            if (_removed.Count > 0) SendRemovals(epoch);
            if (integrity)
            {
                _nextIntegrity = now + IntegrityInterval + (_forceFull ? RefreshPhaseOffset : 0f);
                _forceFull = false;
                SendState(epoch, true);
            }
            else if (_dirty.Count > 0) SendState(epoch, false);
            PerfStats.End("groundloot.capture", started);
        }

        private void ScanHost()
        {
            _scratch.Clear();
            foreach (Record record in _records.Values)
                if (record.Container == null) _scratch.Add(record);
            foreach (Record record in _scratch)
            {
                _records.Remove(record.Id);
                if (!ReferenceEquals(record.Container, null)) _hostByContainer.Remove(record.Container);
                _dirty.Remove(record.Id);
                _removed.Add(record.Id);
                Plugin.Log.LogInfo("Ground loot removed: " + record.Id);
            }
            _scratch.Clear();

            HashSet<Container> all = AllContainers();
            if (all == null) return;
            _truncated = false;
            foreach (Container container in all)
            {
                if (container == null) continue;
                if (_hostByContainer.TryGetValue(container, out Record known))
                {
                    if (Capture(known, container)) _dirty.Add(known.Id);
                    continue;
                }
                if (_hostIgnored.Contains(container)) continue;
                if (!Classify(container, out Kind kind))
                {
                    _hostIgnored.Add(container);
                    continue;
                }
                if (_records.Count >= MaxRecords)
                {
                    _truncated = true;
                    continue;
                }
                Guid id = EntityRegistry.NetworkId(container.gameObject);
                if (id == Guid.Empty) continue;
                if (_records.TryGetValue(id, out Record previous))
                {
                    // The same object got a new Container component; keep one record per id.
                    if (!ReferenceEquals(previous.Container, null)) _hostByContainer.Remove(previous.Container);
                    _records.Remove(id);
                }
                _removed.Remove(id);
                var record = new Record { Id = id, Kind = kind, Container = container };
                Capture(record, container);
                if (!ValidPlacement(record.Position, record.Rotation)) continue;
                _records[id] = record;
                _hostByContainer[container] = record;
                _dirty.Add(id);
                Plugin.Log.LogInfo("Ground loot defined: " + id + " " + kind + " " + container.name
                    + " at " + record.Position.ToString("0.00"));
            }
        }

        /// <summary>
        /// A body is a container on something with Health (native CreateLootOnBody). A bag is the
        /// dropped container native CreateLootBag makes; its flags persist, its tag may not.
        /// </summary>
        private static bool Classify(Container container, out Kind kind)
        {
            GameObject obj = container.gameObject;
            if (obj.GetComponent<Health>() != null)
            {
                kind = Kind.Body;
                return true;
            }
            kind = Kind.Bag;
            return container.DeleteMeIfEmpty || HasBagTag(obj);
        }

        private static bool HasBagTag(GameObject obj)
        {
            if (s_bagTagMissing) return false;
            try { return obj.CompareTag(BagTag); }
            catch (UnityException)
            {
                s_bagTagMissing = true;
                return false;
            }
        }

        /// <summary>Reads the host object into the record. True when anything a guest presents changed.</summary>
        private static bool Capture(Record record, Container container)
        {
            GameObject obj = container.gameObject;
            Transform transform = obj.transform;
            int flags = (container.AreaLootable ? FlagAreaLootable : 0) | (container.DeleteMeIfEmpty ? FlagDeleteIfEmpty : 0)
                | (container.HasInteracted ? FlagInteracted : 0) | (obj.activeSelf ? FlagActive : 0);
            string label = Protocol.Clean(container.ManualLabelName, MaxLabelLength);
            string prefabKey = record.Kind == Kind.Bag ? PrefabKey(obj.name) : "";
            Vector3 position = transform.position;
            Quaternion rotation = transform.rotation;
            bool changed = record.Revision == 0 || flags != record.Flags || label != record.Label
                || prefabKey != record.PrefabKey || (position - record.Position).sqrMagnitude > 0.0001f
                || Quaternion.Angle(rotation, record.Rotation) > 0.5f;
            if (!changed) return false;
            if (!ValidPlacement(position, rotation)) return false;
            record.Flags = flags;
            record.Label = label;
            record.PrefabKey = prefabKey;
            record.Position = position;
            record.Rotation = rotation;
            record.Revision = record.Revision == int.MaxValue ? 1 : record.Revision + 1;
            return true;
        }

        private void SendState(long epoch, bool full)
        {
            _scratch.Clear();
            if (full) _scratch.AddRange(_records.Values);
            else
                foreach (Guid id in _dirty)
                    if (_records.TryGetValue(id, out Record record)) _scratch.Add(record);
            _dirty.Clear();

            byte mode = !full ? StateDelta : _truncated ? StateFullTruncated : StateFull;
            MessageWriter writer = new MessageWriter(MessageType.GroundLootState)
                .Long(epoch).Long(NextSequence()).Byte(mode).Int(_scratch.Count);
            foreach (Record record in _scratch)
            {
                writer.Byte((byte)record.Kind).Guid(record.Id).Int(record.Revision).Byte((byte)record.Flags)
                    .String(record.PrefabKey).String(record.Label)
                    .Float(record.Position.x).Float(record.Position.y).Float(record.Position.z)
                    .Float(record.Rotation.x).Float(record.Rotation.y).Float(record.Rotation.z).Float(record.Rotation.w);
            }
            _session.SendToGuests(writer.ToArray());
            PerfStats.Count(full ? "groundloot.full" : "groundloot.delta");
            PerfStats.Count("groundloot.records", _scratch.Count);
            _scratch.Clear();
        }

        private void SendRemovals(long epoch)
        {
            for (int offset = 0; offset < _removed.Count; offset += MaxRecords)
            {
                int count = Math.Min(MaxRecords, _removed.Count - offset);
                MessageWriter writer = new MessageWriter(MessageType.GroundLootRemove)
                    .Long(epoch).Long(NextSequence()).Int(count);
                for (int i = 0; i < count; i++) writer.Guid(_removed[offset + i]);
                _session.SendToGuests(writer.ToArray());
            }
            _removed.Clear();
        }

        private long NextSequence()
        {
            _sequence = _sequence <= 0 || _sequence == long.MaxValue ? 1 : _sequence + 1;
            return _sequence;
        }

        // ---- guest wire ----

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            if (sender != null || !_session.IsGuest) return;
            if (reader.Type == MessageType.GroundLootState) ReadState(reader);
            else if (reader.Type == MessageType.GroundLootRemove) ReadRemove(reader);
        }

        private void ReadState(MessageReader reader)
        {
            long started = PerfStats.Begin();
            long epoch = reader.Long();
            long sequence = reader.Long();
            byte mode = reader.Byte();
            int count = reader.Int();
            if (epoch <= 0 || sequence <= 0 || mode > StateFullTruncated || count < 0 || count > MaxRecords)
                throw new FormatException("ground loot state header");
            var incoming = new List<Record>(count);
            var seen = new HashSet<Guid>();
            for (int i = 0; i < count; i++)
            {
                var record = new Record { Kind = (Kind)reader.Byte(), Id = reader.Guid(), Revision = reader.Int(), Flags = reader.Byte() };
                record.PrefabKey = PrefabKey(reader.String());
                record.Label = Protocol.Clean(reader.String(), MaxLabelLength);
                record.Position = new Vector3(reader.Float(), reader.Float(), reader.Float());
                record.Rotation = new Quaternion(reader.Float(), reader.Float(), reader.Float(), reader.Float());
                if (record.Kind < Kind.Bag || record.Kind > Kind.Body || record.Id == Guid.Empty || record.Revision <= 0
                    || record.Flags > AllFlags || !ValidPlacement(record.Position, record.Rotation))
                    throw new FormatException("ground loot record");
                if (!seen.Add(record.Id)) throw new FormatException("duplicate ground loot record");
                incoming.Add(record);
            }
            reader.Finish();

            SyncContext();
            if (epoch != FlowCoordinator.WorldEpoch || sequence <= _guestSequence) return;
            _guestSequence = sequence;
            float now = Time.unscaledTime;
            foreach (Record update in incoming)
            {
                if (_records.TryGetValue(update.Id, out Record existing) && existing.Kind != update.Kind)
                {
                    RemoveGuest(existing);
                    existing = null;
                }
                if (existing == null)
                {
                    update.FirstSeen = now;
                    update.FadeIn = mode == StateDelta;
                    _records[update.Id] = update;
                    _guestWork = true;
                    continue;
                }
                // A listed record whose object vanished is rebuilt; an unchanged live one is left alone.
                if (existing.Revision == update.Revision && (!existing.Applied || existing.Object != null))
                {
                    PerfStats.Count("groundloot.skipped");
                    continue;
                }
                existing.Revision = update.Revision;
                existing.Flags = update.Flags;
                existing.PrefabKey = update.PrefabKey;
                existing.Label = update.Label;
                existing.Position = update.Position;
                existing.Rotation = update.Rotation;
                if (existing.Applied && existing.Object == null)
                {
                    existing.CreatedObject = false;
                    existing.AddedContainer = false;
                    existing.FirstSeen = now;
                }
                existing.Applied = false;
                _guestWork = true;
            }
            if (mode != StateDelta)
            {
                _scratch.Clear();
                foreach (Record record in _records.Values)
                    if (!seen.Contains(record.Id)) _scratch.Add(record);
                foreach (Record record in _scratch) RemoveGuest(record);
                _scratch.Clear();
                if (mode == StateFull)
                {
                    _cullOrphans = true;
                    _guestWork = true;
                }
            }
            PerfStats.End("groundloot.apply", started);
        }

        private void ReadRemove(MessageReader reader)
        {
            long epoch = reader.Long();
            long sequence = reader.Long();
            int count = reader.Int();
            if (epoch <= 0 || sequence <= 0 || count < 0 || count > MaxRecords)
                throw new FormatException("ground loot remove header");
            var ids = new List<Guid>(count);
            for (int i = 0; i < count; i++)
            {
                Guid id = reader.Guid();
                if (id == Guid.Empty) throw new FormatException("ground loot remove id");
                ids.Add(id);
            }
            reader.Finish();

            SyncContext();
            if (epoch != FlowCoordinator.WorldEpoch || sequence <= _guestSequence) return;
            _guestSequence = sequence;
            long started = PerfStats.Begin();
            foreach (Guid id in ids)
            {
                if (_records.TryGetValue(id, out Record record)) RemoveGuest(record);
                else RemoveUntrackedBag(id);
            }
            PerfStats.End("groundloot.apply", started);
        }

        // ---- guest presentation ----

        private void UpdateGuest(float now)
        {
            if (!_guestWork || now < _nextRetry || !WorldReady()) return;
            _nextRetry = now + RetryInterval;
            long started = PerfStats.Begin();
            bool pending = false;
            _scratch.Clear();
            _scratch.AddRange(_records.Values);
            foreach (Record record in _scratch)
            {
                if (!record.Applied)
                {
                    try { Apply(record, now); }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning("Could not present ground loot " + record.Id + ": " + ex.Message);
                    }
                }
                if (record.Applied && record.ColliderPending) BuildBodyCollider(record, now);
                pending |= !record.Applied || record.ColliderPending;
            }
            _scratch.Clear();
            if (_cullOrphans)
            {
                _cullOrphans = false;
                CullOrphans();
            }
            _guestWork = pending;
            PerfStats.End("groundloot.apply", started);
        }

        private void Apply(Record record, float now)
        {
            GameObject obj = EntityRegistry.ResolveObject(record.Id);
            if (obj == null)
            {
                // A body normally exists here as the mirrored creature. It does not after a
                // save handoff (dead creatures are destroyed on load), so a bag stands in for it.
                if (record.Kind == Kind.Body && now - record.FirstSeen < StandInDelay) return;
                obj = CreateBag(record);
                if (obj == null) return;
                record.CreatedObject = true;
                Plugin.Log.LogInfo("Ground loot created: " + record.Id + " " + record.Kind + " " + obj.name
                    + " at " + record.Position.ToString("0.00"));
            }

            Container container = obj.GetComponent<Container>();
            if (container == null)
            {
                container = AddBodyContainer(obj);
                if (container == null) return;
                record.AddedContainer = true;
                record.ColliderPending = true;
                record.ColliderAt = now + ColliderDelay;
                Plugin.Log.LogInfo("Ground loot body container added: " + record.Id + " " + obj.name);
            }

            if (record.Label.Length > 0) container.ManualLabelName = record.Label;
            container.AreaLootable = (record.Flags & FlagAreaLootable) != 0;
            container.DeleteMeIfEmpty = (record.Flags & FlagDeleteIfEmpty) != 0;
            container.HasInteracted = (record.Flags & FlagInteracted) != 0;
            if (record.CreatedObject)
            {
                obj.transform.position = record.Position;
                obj.transform.rotation = record.Rotation;
                bool active = (record.Flags & FlagActive) != 0;
                if (obj.activeSelf != active) obj.SetActive(active);
            }
            record.Object = obj;
            record.Applied = true;
        }

        /// <summary>
        /// Builds the same object native Loot.CreateLootBag makes, minus everything that decides
        /// contents: the inventory stays empty until the host's container snapshot fills it.
        /// </summary>
        private GameObject CreateBag(Record record)
        {
            Container prefab = null;
            if (record.PrefabKey.Length > 0 && !string.Equals(record.PrefabKey, DefaultBagPrefab, StringComparison.OrdinalIgnoreCase))
            {
                GameObject custom = GameResources.LoadPrefab<GameObject>(record.PrefabKey, false);
                prefab = custom != null ? custom.GetComponent<Container>() : null;
            }
            if (prefab == null) prefab = GameResources.LoadPrefab<Container>(DefaultBagPrefab, false);
            if (prefab == null)
            {
                if (!_warnedPrefab) Plugin.Log.LogWarning("Ground loot prefab " + DefaultBagPrefab + " is not available.");
                _warnedPrefab = true;
                return null;
            }

            GameObject obj = GameResources.Instantiate<GameObject>(prefab.gameObject, record.Position, record.Rotation);
            if (obj == null) return null;
            if (!s_bagTagMissing)
            {
                try { obj.tag = BagTag; }
                catch (UnityException) { s_bagTagMissing = true; }
            }
            obj.layer = LayerUtility.FindLayerValue("Dynamics");
            InstanceID instance = obj.GetComponent<InstanceID>();
            if (instance == null) instance = obj.AddComponent<InstanceID>();
            instance.Guid = record.Id;
            // Guests never save. Left in place, Persistence would look this id up in the handoff
            // save on Start and, for a stand-in carrying a dead creature's id, restore that
            // creature's "destroyed" state onto the bag.
            Persistence persistence = obj.GetComponent<Persistence>();
            if (persistence != null) UnityEngine.Object.DestroyImmediate(persistence);
            if (record.FadeIn)
            {
                AlphaControl alpha = obj.AddComponent<AlphaControl>();
                if (alpha != null)
                {
                    alpha.Alpha = 0f;
                    alpha.FadeIn(1f);
                }
            }
            return obj;
        }

        /// <summary>The presentation half of native Loot.CreateLootOnBody: the container and its click target, no loot roll.</summary>
        private Container AddBodyContainer(GameObject body)
        {
            Container prefab = GameResources.LoadPrefab<Container>(DefaultBagPrefab, false);
            if (prefab == null)
            {
                if (!_warnedPrefab) Plugin.Log.LogWarning("Ground loot prefab " + DefaultBagPrefab + " is not available.");
                _warnedPrefab = true;
                return null;
            }
            Container container = ComponentUtils.CopyComponent(prefab, body);
            if (container == null) return null;
            NPCDialogue dialogue = body.GetComponent<NPCDialogue>();
            if (dialogue != null) GameUtilities.Destroy(dialogue);
            body.layer = LayerUtility.FindLayerValue("Dynamics");
            return container;
        }

        /// <summary>Bakes the body's click collider once its death pose has settled, as the host did when it dropped the loot.</summary>
        private static void BuildBodyCollider(Record record, float now)
        {
            GameObject obj = record.Object;
            Loot loot = obj != null ? obj.GetComponent<Loot>() : null;
            if (loot == null || CreateMeshForCollider == null)
            {
                record.ColliderPending = false;
                return;
            }
            if (!obj.activeInHierarchy || !loot.enabled) return;
            try
            {
                bool settled = now >= record.ColliderAt
                    || DeathAnimationComplete != null && (bool)DeathAnimationComplete.Invoke(loot, null);
                if (!settled) return;
                record.ColliderPending = false;
                if (obj.GetComponentsInChildren<SkinnedMeshRenderer>().Length == 0) return;
                var routine = (IEnumerator)CreateMeshForCollider.Invoke(loot, null);
                if (ColliderRoutine != null) ColliderRoutine.SetValue(loot, routine);
                loot.StartCoroutine(routine);
            }
            catch (Exception ex)
            {
                record.ColliderPending = false;
                Plugin.Log.LogWarning("Could not build the loot collider for " + record.Id + ": " + ex.Message);
            }
        }

        private void RemoveGuest(Record record)
        {
            _records.Remove(record.Id);
            GameObject obj = record.Object;
            if (obj == null && !record.Applied) obj = InstanceID.GetObjectByID(record.Id);
            if (obj == null) return;
            Container container = obj.GetComponent<Container>();
            if (container == null) return;
            DestroyGuestContainer(container, "host removed it");
        }

        private static void RemoveUntrackedBag(Guid id)
        {
            GameObject obj = InstanceID.GetObjectByID(id);
            Container container = obj != null ? obj.GetComponent<Container>() : null;
            if (container != null && Classify(container, out Kind kind) && kind == Kind.Bag)
                DestroyGuestContainer(container, "host removed it");
        }

        /// <summary>Mirrors native Container.CloseInventoryCB: a character keeps its body and loses the container; a bag goes entirely.</summary>
        private static void DestroyGuestContainer(Container container, string reason)
        {
            GameObject obj = container.gameObject;
            Plugin.Log.LogInfo("Ground loot destroyed: " + obj.name + " (" + reason + ")");
            if (obj.GetComponent<CharacterStats>() != null || obj.GetComponent<Health>() != null)
                GameUtilities.Destroy(container);
            else GameUtilities.Destroy(obj);
        }

        /// <summary>
        /// A bag restored from the handoff save that the host no longer has would stay forever.
        /// It goes once two complete host lists in a row do not contain it.
        /// </summary>
        private void CullOrphans()
        {
            HashSet<Container> all = AllContainers();
            if (all == null) return;
            _orphanScratch.Clear();
            _scratch.Clear();
            foreach (Container container in all)
            {
                if (container == null || !Classify(container, out Kind kind) || kind != Kind.Bag) continue;
                InstanceID instance = container.GetComponent<InstanceID>();
                Guid id = instance != null ? instance.Guid : Guid.Empty;
                if (id == Guid.Empty || _records.ContainsKey(id)) continue;
                if (_orphanCandidates.Contains(id)) _scratch.Add(new Record { Id = id, Container = container });
                else _orphanScratch.Add(id);
            }
            foreach (Record orphan in _scratch) DestroyGuestContainer(orphan.Container, "not in the host's world");
            _scratch.Clear();
            HashSet<Guid> swap = _orphanCandidates;
            _orphanCandidates = _orphanScratch;
            _orphanScratch = swap;
        }

        // ---- reset and join ----

        private void OnPlayerJoined(PlayerInfo player)
        {
            SyncContext();
            if (_session.IsHost) _forceFull = true;
        }

        private void OnWorldStateReady(PlayerInfo player, long worldEpoch)
        {
            SyncContext();
            if (_session.IsHost && worldEpoch == FlowCoordinator.WorldEpoch) _forceFull = true;
        }

        private void OnFullStateRequested(int playerId, long worldEpoch)
        {
            SyncContext();
            if (_session.IsHost && worldEpoch == FlowCoordinator.WorldEpoch) _forceFull = true;
        }

        private void OnLevelChanged(object sender, EventArgs e)
        {
            ResetWorld(false);
            _observedEpoch = FlowCoordinator.WorldEpoch;
        }

        private void OnSessionReset()
        {
            _generation = _session.Generation;
            ResetWorld(true);
            _sequence = 0;
            _observedEpoch = FlowCoordinator.WorldEpoch;
        }

        private void SyncContext()
        {
            if (_generation != _session.Generation) OnSessionReset();
            long epoch = FlowCoordinator.WorldEpoch;
            if (_observedEpoch == epoch) return;
            _observedEpoch = epoch;
            ResetWorld(true);
        }

        /// <summary>
        /// Forgets everything. Objects this module built are destroyed unless the level itself is
        /// going away; objects it only bound (saved bags, mirrored bodies) belong to the world.
        /// </summary>
        private void ResetWorld(bool destroyCreated)
        {
            if (destroyCreated)
            {
                foreach (Record record in _records.Values)
                {
                    GameObject obj = record.Object;
                    if (obj == null) continue;
                    if (record.CreatedObject) GameUtilities.Destroy(obj);
                    else if (record.AddedContainer)
                    {
                        Container container = obj.GetComponent<Container>();
                        if (container != null) GameUtilities.Destroy(container);
                    }
                }
            }
            _records.Clear();
            _hostByContainer.Clear();
            _hostIgnored.Clear();
            _dirty.Clear();
            _removed.Clear();
            _scratch.Clear();
            _orphanCandidates.Clear();
            _orphanScratch.Clear();
            _forceFull = true;
            _truncated = false;
            _scanFrames = 0;
            _nextScan = 0f;
            _nextIntegrity = 0f;
            _guestSequence = 0;
            _nextRetry = 0f;
            _guestWork = false;
            _cullOrphans = false;
        }

        private bool WorldReady()
        {
            return _session.IsWorldReady && !FlowCoordinator.ApplyingWorldBarrier
                && GameState.IsInGameSession && !GameState.IsLoading && !GameState.IsInMainMenu;
        }

        // ---- helpers ----

        /// <summary>The game's own set of started containers; it is created once and never replaced.</summary>
        private static HashSet<Container> AllContainers()
        {
            if (s_allContainers == null && AllContainersField != null)
                s_allContainers = AllContainersField.GetValue(null) as HashSet<Container>;
            return s_allContainers;
        }

        private static string PrefabKey(string name)
        {
            return Protocol.Clean((name ?? "").Replace("(Clone)", ""), MaxPrefabKeyLength);
        }

        private static bool ValidPlacement(Vector3 position, Quaternion rotation)
        {
            return Finite(position.x) && Finite(position.y) && Finite(position.z)
                && Mathf.Abs(position.x) <= MaxCoordinate && Mathf.Abs(position.y) <= MaxCoordinate && Mathf.Abs(position.z) <= MaxCoordinate
                && Finite(rotation.x) && Finite(rotation.y) && Finite(rotation.z) && Finite(rotation.w)
                && Mathf.Abs(rotation.x) <= 1.001f && Mathf.Abs(rotation.y) <= 1.001f && Mathf.Abs(rotation.z) <= 1.001f && Mathf.Abs(rotation.w) <= 1.001f
                && rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w > 0.000001f;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
