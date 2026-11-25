using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>
    /// Gives runtime objects the same identity on both machines. Saved objects keep
    /// their InstanceID; the host assigns ids to other actors and sends a bounded
    /// recipe when a guest needs to create its presentation copy.
    /// </summary>
    public sealed class EntityRegistry
    {
        private enum Origin : byte { Saved = 1, Encounter = 2, Summon = 3, Prefab = 4 }

        private const int MaxEntities = 1024;
        private const int MaxPrefabKeyLength = 96;
        private const int MaxAbilityKeyLength = 96;
        private const float FullRefreshInterval = 3f;
        private const float RetryInterval = 0.25f;
        // An entity that stays unresolved repeats a search of the whole scene on every attempt, so
        // the gap keeps doubling up to this. A new binding still retries at once, and the host's
        // full manifest (every 12 s) attempts every pending entity regardless.
        private const float MaxRetryInterval = 32f;
        private const int MaxRetryDoublings = 7;
        /// <summary>Failed attempts (about two seconds) before an unresolved entity is named in the log, once.</summary>
        private const int UnresolvedLogAttempts = 4;
        /// <summary>An unchanged manifest is only re-sent this often; changes still go out as deltas at once.</summary>
        private const float IntegrityInterval = 12f;
        private const float ForcedRefreshPhase = 0.45f;
        private const int MaxCachedPrefabKeys = 1024;
        private const int MaxCachedPrefabKeyLength = 128;
        private const float MaxCoordinate = 1000000f;
        private const float MaxScale = 100f;

        private static readonly HarmonyLib.AccessTools.FieldRef<InstanceID, Guid> StoredId =
            HarmonyLib.AccessTools.FieldRefAccess<InstanceID, Guid>("m_Guid");
        private static readonly HarmonyLib.AccessTools.FieldRef<Encounter, List<Guid>> EncounterIds =
            HarmonyLib.AccessTools.FieldRefAccess<Encounter, List<Guid>>("guidList");
        private static readonly HarmonyLib.AccessTools.FieldRef<Encounter, List<GameObject>> EncounterSpawned =
            HarmonyLib.AccessTools.FieldRefAccess<Encounter, List<GameObject>>("spawnedList");
        private static readonly HarmonyLib.AccessTools.FieldRef<Encounter, List<GameObject>> EncounterDisabled =
            HarmonyLib.AccessTools.FieldRefAccess<Encounter, List<GameObject>>("disabledInstanceList");
        private static readonly HarmonyLib.AccessTools.FieldRef<Summon, List<Health>> SummonedHealth =
            HarmonyLib.AccessTools.FieldRefAccess<Summon, List<Health>>("m_summons");
        private static readonly HarmonyLib.AccessTools.FieldRef<Summon, GameObject> Summoner =
            HarmonyLib.AccessTools.FieldRefAccess<Summon, GameObject>("m_summoner");

        private static EntityRegistry s_instance;
        private static int s_applyDepth;

        private readonly Session _session;
        private readonly Dictionary<Guid, Entity> _byId = new Dictionary<Guid, Entity>();
        private readonly Dictionary<GameObject, Guid> _byObject = new Dictionary<GameObject, Guid>();
        private readonly HashSet<Guid> _dirty = new HashSet<Guid>();
        private readonly HashSet<Guid> _removed = new HashSet<Guid>();
        private readonly HashSet<Guid> _pending = new HashSet<Guid>();
        private readonly HashSet<Guid> _unresolvedLogged = new HashSet<Guid>();
        private readonly HashSet<GameObject> _projectiles = new HashSet<GameObject>();
        private int _epoch = 1;
        private int _revision;
        private float _nextFullRefresh;
        private float _nextRetry;
        private bool _wasGuest;

        // Host: a full manifest is sent when forced, when anything changed since the last one, or at the integrity interval.
        private bool _forceFull = true;
        private bool _changedSinceFull;
        private float _nextIntegrity;

        // Guest: local repair between host manifests, and retry pacing.
        private float _nextGuestRepair;
        private int _bindVersion;
        private int _retryBindVersion;
        private readonly List<Guid> _scratchIds = new List<Guid>();
        private readonly List<GameObject> _scratchObjects = new List<GameObject>();
        private readonly HashSet<Guid> _seen = new HashSet<Guid>();

        // Guest: object scans shared by every definition applied in one message or retry pass.
        private int _scanDepth;
        private bool _savedIndexValid;
        private readonly Dictionary<Guid, GameObject> _savedIndex = new Dictionary<Guid, GameObject>();
        private Transform[] _sceneTransforms;
        private Vector3[] _sceneTransformPositions;

        private static readonly Dictionary<GameObject, Guid> NoObjects = new Dictionary<GameObject, Guid>();
        private static readonly Dictionary<string, string> PrefabKeys = new Dictionary<string, string>(StringComparer.Ordinal);

        private StreamWriter _probe;
        private DataContractJsonSerializer _probeFrameSerializer;
        private DataContractJsonSerializer _probeEventSerializer;
        private float _nextProbe;
        private readonly Dictionary<string, float> _blockedProbeTimes = new Dictionary<string, float>();

        [Serializable]
        public sealed class EntityProbeVector { public float x, y, z; }

        [Serializable]
        public sealed class EntityProbeEntity
        {
            public string id, parent, prefab, name, ability;
            public int origin, slot, abilityDisplayId, abilityOccurrence;
            public bool resolved, active, dead, unconscious;
            public EntityProbeVector world;
            public float health, stamina, maxHealth, maxStamina;
        }

        [Serializable]
        public sealed class EntityProbeEncounter
        {
            public string id, name;
            public bool active, spawnOnStart;
            public string[] guids, prefabs, spawned;
            public EntityProbeVector world;
        }

        [Serializable]
        public sealed class EntityProbeSummon
        {
            public string ability, ownerId, summonType;
            public string[] prefabs, summons;
            public int activeSummons, maxSummons;
            public bool random;
        }

        [Serializable]
        public sealed class EntityProbeFrame
        {
            public string kind = "frame", utc, role, scene;
            public float gameTime;
            public int epoch, revision, pending;
            public bool inCombat;
            public List<EntityProbeEntity> entities = new List<EntityProbeEntity>();
            public List<EntityProbeEncounter> encounters = new List<EntityProbeEncounter>();
            public List<EntityProbeSummon> summonAbilities = new List<EntityProbeSummon>();
        }

        [Serializable]
        public sealed class EntityProbeEvent
        {
            public string kind = "event", utc, role, scene, action, id, parent, prefab, detail, ability;
            public float gameTime;
            public int origin, slot, epoch, revision, abilityDisplayId, abilityOccurrence;
        }

        private sealed class Definition
        {
            public Origin Origin;
            public Guid Id;
            public Guid Parent;
            public int Slot;
            public string PrefabKey;
            public string AbilityKey;
            public int AbilityDisplayId = -1;
            public int AbilityOccurrence;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
            public bool Active;

            public bool SameRecipe(Definition other)
            {
                return Origin == other.Origin && Parent == other.Parent && Slot == other.Slot && PrefabKey == other.PrefabKey
                    && AbilityKey == other.AbilityKey && AbilityDisplayId == other.AbilityDisplayId && AbilityOccurrence == other.AbilityOccurrence;
            }
        }

        private sealed class Entity
        {
            public GameObject Object;
            public Definition Definition;
            /// <summary>Host: the entity and this active flag have been sent since the last forced manifest.</summary>
            public bool Sent, SentActive;
            /// <summary>Guest: the definition came from the host rather than from a local saved-id lookup.</summary>
            public bool Remote;
            /// <summary>Guest: failed creation attempts since the definition last changed, and the earliest next one.</summary>
            public int Attempts;
            public float NextAttempt;
        }

        internal static bool IsApplying => s_applyDepth > 0;
        /// <summary>Lets per-frame patches skip building ProbeBlocked arguments while the probe is off.</summary>
        internal static bool Probing => s_instance != null && s_instance._probe != null;
        internal static Dictionary<GameObject, Guid>.KeyCollection RegisteredObjects =>
            s_instance != null ? s_instance._byObject.Keys : NoObjects.Keys;

        public EntityRegistry(Session session)
        {
            _session = session;
            s_instance = this;
            session.GameMessage += OnGameMessage;
            session.PlayerJoined += OnPlayerJoined;
            session.FullStateRequested += OnFullStateRequested;
            GameState.OnLevelLoaded += OnLevelChanged;
            GameState.OnLevelUnload += OnLevelChanged;
            InitializeProbe();
        }

        public void Dispose()
        {
            GameState.OnLevelLoaded -= OnLevelChanged;
            GameState.OnLevelUnload -= OnLevelChanged;
            _session.GameMessage -= OnGameMessage;
            _session.PlayerJoined -= OnPlayerJoined;
            _session.FullStateRequested -= OnFullStateRequested;
            if (s_instance == this)
            {
                s_instance = null;
                s_applyDepth = 0;
            }
            _byId.Clear();
            _byObject.Clear();
            _projectiles.Clear();
            _dirty.Clear();
            _removed.Clear();
            _pending.Clear();
            ReleaseScans();
            _probe?.Dispose();
        }

        /// <summary>The saved id, or the host-assigned id for a runtime actor. Empty if it cannot be networked.</summary>
        public static Guid NetworkId(GameObject obj)
        {
            if (obj == null) return Guid.Empty;
            EntityRegistry self = s_instance;
            // Mapped objects were checked for a Projectile when they were mapped.
            if (self != null && self._byObject.TryGetValue(obj, out Guid known))
                return self._projectiles.Count > 0 && self._projectiles.Contains(obj) ? Guid.Empty : known;
            if (obj.GetComponent<Projectile>() != null) return Guid.Empty;
            if (self == null) return ReadSavedId(obj);
            // The inventory and level-up screens show a copy of the character. It exists only in
            // that player's interface: announced as a world entity, every guest would search its
            // scene again and again for something it can never have.
            if (obj.layer == PaperdollLayer) return Guid.Empty;
            return self.Register(obj, Origin.Prefab, Guid.Empty, 0, (string)null, Guid.Empty);
        }

        private static int s_paperdollLayer = int.MinValue;
        private static int PaperdollLayer
        {
            get
            {
                if (s_paperdollLayer == int.MinValue) s_paperdollLayer = LayerMask.NameToLayer("Paperdoll");
                return s_paperdollLayer;
            }
        }

        /// <summary>Resolves active and inactive registered objects, then falls back to the game's saved-id table.</summary>
        public static GameObject ResolveObject(Guid id)
        {
            if (id == Guid.Empty) return null;
            EntityRegistry self = s_instance;
            if (self != null && self._byId.TryGetValue(id, out Entity entity))
            {
                if (entity.Object != null) return entity.Object;
                if (self._session.IsHost)
                {
                    // The object key stays for RemoveDestroyed: callers may be enumerating RegisteredObjects.
                    self._byId.Remove(id);
                    self._pending.Remove(id);
                }
            }
            GameObject obj = InstanceID.GetObjectByID(id);
            if (obj != null && self != null && !self._byObject.ContainsKey(obj))
                self.BindResolved(id, obj);
            return obj;
        }

        public void Update(float now)
        {
            WriteProbe(now);
            SyncGuestSession();
            if (_session.IsHost) UpdateHost(now);
            else if (_session.IsGuest) UpdateGuest(now);
        }

        private void UpdateHost(float now)
        {
            long started = PerfStats.Begin();
            RemoveDestroyed();
            bool connected = _session.Players.Count >= 2 && GameState.IsInGameSession && !GameState.IsLoading;
            bool periodic = connected && (_forceFull || now >= _nextFullRefresh);
            bool full = periodic && (_forceFull || _changedSinceFull || _removed.Count > 0 || _dirty.Count > 0
                || now >= _nextIntegrity || ActiveStateChanged());
            PerfStats.End("entity.update", started);
            if (!connected) return;

            started = PerfStats.Begin();
            if (_removed.Count > 0) SendRemovals();
            if (periodic)
            {
                // A forced manifest is followed by a later periodic one so the streams do not all refresh in one frame.
                _nextFullRefresh = now + FullRefreshInterval + (_forceFull ? ForcedRefreshPhase : 0f);
                if (full)
                {
                    _forceFull = false;
                    _nextIntegrity = now + IntegrityInterval;
                    SendDefinitions(null, true);
                }
                else PerfStats.Count("entity.skipped");
            }
            else if (_dirty.Count > 0) SendDefinitions(_dirty, false);
            PerfStats.End("entity.capture", started);
        }

        /// <summary>Active flags have no dirty path; they are sampled at the refresh cadence the guest was already given.</summary>
        private bool ActiveStateChanged()
        {
            foreach (Entity entity in _byId.Values)
            {
                if (entity.Object == null) continue;
                if (!entity.Sent || entity.SentActive != entity.Object.activeSelf) return true;
            }
            return false;
        }

        private void UpdateGuest(float now)
        {
            bool repair = now >= _nextGuestRepair && _session.IsWorldReady;
            if (!repair && (_pending.Count == 0 || now < _nextRetry)) return;
            long started = PerfStats.Begin();
            if (repair)
            {
                _nextGuestRepair = now + FullRefreshInterval;
                RepairGuest();
            }
            if (_pending.Count > 0 && now >= _nextRetry)
            {
                _nextRetry = now + RetryInterval;
                RetryPending(now);
            }
            PerfStats.End("entity.update", started);
        }

        /// <summary>
        /// What an unchanged host manifest used to do for bound entities every refresh:
        /// restore the host's active flag and queue entities whose object went away.
        /// </summary>
        private void RepairGuest()
        {
            _scratchObjects.Clear();
            foreach (var pair in _byId)
            {
                Entity entity = pair.Value;
                if (!entity.Remote || entity.Definition == null) continue;
                if (entity.Object == null)
                {
                    if (_pending.Add(pair.Key))
                    {
                        entity.Attempts = 0;
                        entity.NextAttempt = 0f;
                    }
                }
                else if (entity.Object.activeSelf != entity.Definition.Active) _scratchObjects.Add(entity.Object);
            }
            // Activation can run game code that resolves ids, so it happens after the walk.
            for (int i = 0; i < _scratchObjects.Count; i++)
            {
                GameObject obj = _scratchObjects[i];
                if (obj == null || !_byObject.TryGetValue(obj, out Guid id) || !_byId.TryGetValue(id, out Entity entity)) continue;
                if (entity.Object == obj && obj.activeSelf != entity.Definition.Active) obj.SetActive(entity.Definition.Active);
            }
            _scratchObjects.Clear();
        }

        private void RetryPending(float now)
        {
            // A new binding can be the parent or owner another pending entity was waiting for.
            bool kick = _retryBindVersion != _bindVersion;
            _retryBindVersion = _bindVersion;
            bool settled = !GameState.IsLoading && GameState.IsInGameSession;
            _scratchIds.Clear();
            foreach (Guid id in _pending) _scratchIds.Add(id);
            BeginScans();
            try
            {
                for (int i = 0; i < _scratchIds.Count; i++)
                {
                    Guid id = _scratchIds[i];
                    if (!_byId.TryGetValue(id, out Entity entity) || entity.Object != null)
                    {
                        _pending.Remove(id);
                        continue;
                    }
                    if (!kick && now < entity.NextAttempt)
                    {
                        PerfStats.Count("entity.skipped");
                        continue;
                    }
                    PerfStats.Count("entity.retry");
                    if (ApplyDefinition(entity.Definition)) continue;
                    // Nothing can be created while loading, so waiting there is not a failure.
                    if (settled && entity.Attempts < MaxRetryDoublings && ++entity.Attempts == UnresolvedLogAttempts
                        && _unresolvedLogged.Add(id))
                    {
                        Definition d = entity.Definition;
                        Plugin.Log?.LogWarning("Entity " + id + " (" + d.Origin + ", " + d.PrefabKey + ", active " + d.Active
                            + ") from the host cannot be found or created here; retrying less and less often.");
                    }
                    entity.NextAttempt = now + Mathf.Min(RetryInterval * (1 << entity.Attempts), MaxRetryInterval);
                }
            }
            finally
            {
                EndScans();
                _scratchIds.Clear();
            }
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            if (_session.IsHost)
            {
                _nextFullRefresh = 0f;
                _forceFull = true;
                return;
            }
            if (_session.IsGuest && player.IsHost)
            {
                ResetGuestSession();
                _wasGuest = true;
            }
        }

        /// <summary>A guest that has just become ready received nothing while it loaded.</summary>
        private void OnFullStateRequested(int playerId, long worldEpoch)
        {
            if (!_session.IsHost) return;
            _nextFullRefresh = 0f;
            _forceFull = true;
        }

        private void SyncGuestSession()
        {
            if (!_session.IsGuest)
            {
                _wasGuest = false;
                return;
            }
            if (_wasGuest) return;
            ResetGuestSession();
            _wasGuest = true;
        }

        private void ResetGuestSession()
        {
            ResetRemote();
            _epoch = 1;
            _revision = 0;
            _nextFullRefresh = 0f;
            _forceFull = true;
            _nextRetry = 0f;
            _dirty.Clear();
            _removed.Clear();
        }

        private void OnLevelChanged(object sender, EventArgs e)
        {
            _epoch = _epoch == int.MaxValue ? 1 : _epoch + 1;
            _revision = 0;
            _nextFullRefresh = 0f;
            _forceFull = true;
            _changedSinceFull = false;
            _nextRetry = 0f;
            _byId.Clear();
            _byObject.Clear();
            _projectiles.Clear();
            _dirty.Clear();
            _removed.Clear();
            _pending.Clear();
            ReleaseScans();
        }

        // ---- host registration and capture ----

        private static Guid ReadSavedId(GameObject obj)
        {
            var instance = obj.GetComponent<InstanceID>();
            return instance != null ? ReadSavedId(instance) : Guid.Empty;
        }

        private static Guid ReadSavedId(InstanceID instance)
        {
            Guid id = StoredId(instance);
            if (id != Guid.Empty) return id;
            return !string.IsNullOrEmpty(instance.UniqueID) && Guid.TryParse(instance.UniqueID, out id) ? id : Guid.Empty;
        }

        private Guid Register(GameObject obj, Origin origin, Guid parent, int slot, string prefabKey, Guid requiredId)
        {
            InstanceID instance = obj.GetComponent<InstanceID>();
            bool saved = obj.GetComponent<Persistence>() != null;
            Guid id = requiredId != Guid.Empty ? requiredId : ReadSavedId(obj);
            if (origin == Origin.Prefab && prefabKey == null && (saved || !_session.IsHost)) origin = Origin.Saved;
            if (id == Guid.Empty && (!_session.IsHost || saved || origin == Origin.Saved)) return Guid.Empty;
            if (id == Guid.Empty) id = Guid.NewGuid();

            if (instance == null)
            {
                if (obj.GetComponent<Mover>() == null && obj.GetComponent<Health>() == null
                    && obj.GetComponent<AIController>() == null && obj.GetComponent<Usable>() == null
                    && obj.GetComponent<Encounter>() == null)
                    return Guid.Empty;
                instance = obj.AddComponent<InstanceID>();
            }
            if (StoredId(instance) != id && (_session.IsHost && !saved || _session.IsGuest && requiredId != Guid.Empty))
                instance.Guid = id;

            if (_session.IsHost && _byId.TryGetValue(id, out Entity stale) && stale.Object == null) RemoveMapping(id);
            if (_byId.TryGetValue(id, out Entity collision) && collision.Object != null && collision.Object != obj)
            {
                if (!_session.IsHost || saved || origin == Origin.Saved) return Guid.Empty;
                id = Guid.NewGuid();
                instance.Guid = id;
            }

            string key = NormalizePrefabKey(prefabKey ?? obj.name);
            var definition = new Definition
            {
                Origin = origin,
                Id = id,
                Parent = parent,
                Slot = slot,
                PrefabKey = origin == Origin.Saved ? "" : key,
                Position = obj.transform.position,
                Rotation = obj.transform.rotation,
                Scale = obj.transform.localScale,
                Active = obj.activeSelf,
            };
            if (_byObject.TryGetValue(obj, out Guid previous) && previous != id) RemoveMapping(previous);
            if (!_byId.ContainsKey(id) && _byId.Count >= MaxEntities) return Guid.Empty;
            if (_byId.TryGetValue(id, out Entity existing) && existing.Object == obj)
            {
                if (!existing.Definition.SameRecipe(definition)) _dirty.Add(id);
                if (origin != Origin.Prefab || existing.Definition.Origin == Origin.Prefab)
                    existing.Definition = definition;
            }
            else
            {
                _byId[id] = new Entity { Object = obj, Definition = definition };
                _dirty.Add(id);
            }
            MapObject(obj, id);
            _removed.Remove(id);
            return id;
        }

        private void BindResolved(Guid id, GameObject obj)
        {
            if (_byId.TryGetValue(id, out Entity existing))
            {
                if (existing.Object == null || existing.Object == obj)
                {
                    existing.Object = obj;
                    MapObject(obj, id);
                    _pending.Remove(id);
                }
                return;
            }
            var definition = new Definition
            {
                Origin = Origin.Saved,
                Id = id,
                Position = obj.transform.position,
                Rotation = obj.transform.rotation,
                Scale = obj.transform.localScale,
                PrefabKey = "",
                Active = obj.activeSelf,
            };
            _byId[id] = new Entity { Object = obj, Definition = definition };
            MapObject(obj, id);
        }

        private void MapObject(GameObject obj, Guid id)
        {
            if (!_byObject.ContainsKey(obj))
            {
                _bindVersion++;
                if (obj.GetComponent<Projectile>() != null) _projectiles.Add(obj);
            }
            _byObject[obj] = id;
        }

        private void UnmapObject(GameObject obj)
        {
            _byObject.Remove(obj);
            if (_projectiles.Count > 0) _projectiles.Remove(obj);
        }

        internal static void CaptureEncounter(Encounter encounter)
        {
            EntityRegistry self = s_instance;
            if (self == null || !self._session.IsHost || encounter == null) return;
            Guid parent = NetworkId(encounter.gameObject);
            if (parent == Guid.Empty) return;
            List<Guid> ids = EncounterIds(encounter);
            List<GameObject> spawned = encounter.GetSpawnedList();
            for (int i = 0; i < spawned.Count; i++)
            {
                GameObject obj = spawned[i];
                if (obj == null) continue;
                Guid id = ReadSavedId(obj);
                int slot = -1;
                for (int n = 0; n < encounter.encounterList.Count && n < ids.Count; n++)
                {
                    if (encounter.encounterList[n].Creature == obj || id != Guid.Empty && ids[n] == id)
                    {
                        slot = n;
                        break;
                    }
                }
                if (slot < 0) continue;
                Guid registered = self.Register(obj, Origin.Encounter, parent, slot, encounter.encounterList[slot].Creature, id);
                if (registered != Guid.Empty && self._byId.TryGetValue(registered, out Entity entity))
                    self.ProbeEvent("host-encounter-spawn", entity.Definition, obj.name);
            }
        }

        private Guid Register(GameObject obj, Origin origin, Guid parent, int slot, GameObject prefab, Guid requiredId)
        {
            return Register(obj, origin, parent, slot, prefab != null ? prefab.name : null, requiredId);
        }

        internal static List<Health> Summons(Summon summon) => SummonedHealth(summon);

        internal static void CaptureSummon(Summon summon, CharacterStats prefab, List<Health> before)
        {
            EntityRegistry self = s_instance;
            if (self == null || !self._session.IsHost || summon == null) return;
            GameObject owner = Summoner(summon) != null ? Summoner(summon) : summon.Owner;
            if (owner == null) return;
            Guid parent = NetworkId(owner);
            if (parent == Guid.Empty) return;
            string abilityKey;
            int abilityDisplayId, abilityOccurrence;
            SummonAbilityIdentity(summon, owner, out abilityKey, out abilityDisplayId, out abilityOccurrence);
            List<Health> current = SummonedHealth(summon);
            for (int i = 0; i < current.Count; i++)
            {
                Health health = current[i];
                if (health == null || before != null && before.Contains(health)) continue;
                Guid registered = self.Register(health.gameObject, Origin.Summon, parent, i, prefab != null ? prefab.name : health.name, Guid.Empty);
                if (registered != Guid.Empty && self._byId.TryGetValue(registered, out Entity entity))
                {
                    entity.Definition.AbilityKey = abilityKey;
                    entity.Definition.AbilityDisplayId = abilityDisplayId;
                    entity.Definition.AbilityOccurrence = abilityOccurrence;
                    self._dirty.Add(registered);
                    self.ProbeEvent("host-summon-create", entity.Definition, health.name);
                }
            }
        }

        private static void SummonAbilityIdentity(Summon summon, GameObject owner, out string key, out int displayId, out int occurrence)
        {
            key = NormalizePrefabKey(summon.name);
            GenericAbility ability = summon.GetComponent<GenericAbility>();
            displayId = ability != null && ability.DisplayName != null ? ability.DisplayName.StringID : -1;
            occurrence = 0;
            CharacterStats stats = owner != null ? owner.GetComponent<CharacterStats>() : null;
            if (stats == null || stats.ActiveAbilities == null) return;
            foreach (GenericAbility candidate in stats.ActiveAbilities)
            {
                if (candidate == null || NormalizePrefabKey(candidate.name) != key) continue;
                int candidateDisplay = candidate.DisplayName != null ? candidate.DisplayName.StringID : -1;
                if (candidateDisplay != displayId) continue;
                if (candidate.gameObject == summon.gameObject) return;
                occurrence++;
            }
        }

        internal static void CaptureDespawn(List<GameObject> before)
        {
            EntityRegistry self = s_instance;
            if (self == null || !self._session.IsHost || before == null) return;
            foreach (GameObject obj in before)
            {
                if (obj == null) continue;
                PartyMemberAI member = obj.GetComponent<PartyMemberAI>();
                if (member != null && member.enabled) continue;
                if (self._byObject.TryGetValue(obj, out Guid id) && self._byId.TryGetValue(id, out Entity entity))
                    self.ProbeEvent("host-encounter-despawn", entity.Definition, obj.name);
                self.RemoveHostObject(obj);
            }
        }

        private void RemoveDestroyed()
        {
            _scratchObjects.Clear();
            foreach (GameObject obj in _byObject.Keys)
                if (obj == null) _scratchObjects.Add(obj);
            if (_scratchObjects.Count == 0) return;
            for (int i = 0; i < _scratchObjects.Count; i++)
            {
                GameObject obj = _scratchObjects[i];
                if (!_byObject.TryGetValue(obj, out Guid id)) continue;
                if (_byId.TryGetValue(id, out Entity entity))
                {
                    if (ReferenceEquals(entity.Object, obj)) RemoveHost(id);
                    else UnmapObject(obj);
                }
                else
                {
                    // ResolveObject already dropped the entity. Without removing the key here the
                    // removal was queued and sent again on every later frame.
                    UnmapObject(obj);
                    _dirty.Remove(id);
                    _removed.Add(id);
                }
            }
            _scratchObjects.Clear();
        }

        private void RemoveHostObject(GameObject obj)
        {
            if (_byObject.TryGetValue(obj, out Guid id)) RemoveHost(id);
        }

        private void RemoveHost(Guid id)
        {
            if (_byId.TryGetValue(id, out Entity entity)) ProbeEvent("host-entity-remove", entity.Definition, "destroyed");
            RemoveMapping(id);
            _dirty.Remove(id);
            _removed.Add(id);
        }

        private void RemoveMapping(Guid id)
        {
            if (!_byId.TryGetValue(id, out Entity entity)) return;
            if (!ReferenceEquals(entity.Object, null)) UnmapObject(entity.Object);
            _byId.Remove(id);
        }

        // ---- wire ----

        private void SendDefinitions(ICollection<Guid> selected, bool full)
        {
            var message = new MessageWriter(MessageType.EntityDefine)
                .Int(_epoch).Int(NextRevision()).Byte((byte)(full ? 1 : 0)).Int(0);
            int count = 0;
            if (selected == null)
            {
                foreach (Entity entity in _byId.Values)
                    if (WriteEntity(message, entity, count)) count++;
            }
            else
            {
                foreach (Guid id in selected)
                    if (_byId.TryGetValue(id, out Entity entity) && WriteEntity(message, entity, count)) count++;
            }
            byte[] data = message.ToArray();
            WriteCount(data, 1 + 4 + 4 + 1, count);
            _session.SendToGuests(data);
            _dirty.Clear();
            _changedSinceFull = !full;
            PerfStats.Count(full ? "entity.full" : "entity.delta");
            PerfStats.Count("entity.records", count);
        }

        private static bool WriteEntity(MessageWriter message, Entity entity, int count)
        {
            if (entity.Object == null || count >= MaxEntities) return false;
            Definition d = entity.Definition;
            d.Active = entity.Object.activeSelf;
            if (!ValidTransform(d.Position, d.Rotation, d.Scale)) return false;
            WriteDefinition(message, d);
            entity.Sent = true;
            entity.SentActive = d.Active;
            return true;
        }

        private void SendRemovals()
        {
            var message = new MessageWriter(MessageType.EntityRemove).Int(_epoch).Int(NextRevision()).Int(0);
            int count = 0;
            foreach (Guid id in _removed)
            {
                if (count >= MaxEntities) break;
                message.Guid(id);
                count++;
            }
            byte[] data = message.ToArray();
            WriteCount(data, 1 + 4 + 4, count);
            _session.SendToGuests(data);
            _removed.Clear();
            _changedSinceFull = true;
        }

        private int NextRevision()
        {
            if (_revision == int.MaxValue)
            {
                _epoch = _epoch == int.MaxValue ? 1 : _epoch + 1;
                _revision = 1;
            }
            else _revision++;
            return _revision;
        }

        private static void WriteCount(byte[] data, int offset, int count)
        {
            data[offset] = (byte)count;
            data[offset + 1] = (byte)(count >> 8);
            data[offset + 2] = (byte)(count >> 16);
            data[offset + 3] = (byte)(count >> 24);
        }

        private static void WriteDefinition(MessageWriter message, Definition d)
        {
            message.Byte((byte)d.Origin).Guid(d.Id).Guid(d.Parent).Int(d.Slot).String(d.PrefabKey ?? "")
                .String(d.AbilityKey ?? "").Int(d.AbilityDisplayId).Int(d.AbilityOccurrence);
            WriteVector(message, d.Position);
            message.Float(d.Rotation.x).Float(d.Rotation.y).Float(d.Rotation.z).Float(d.Rotation.w);
            WriteVector(message, d.Scale);
            message.Byte((byte)(d.Active ? 1 : 0));
        }

        private static void WriteVector(MessageWriter message, Vector3 value)
        {
            message.Float(value.x).Float(value.y).Float(value.z);
        }

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            if (sender != null || !_session.IsGuest) return;
            if (reader.Type != MessageType.EntityDefine && reader.Type != MessageType.EntityRemove) return;
            long started = PerfStats.Begin();
            BeginScans();
            try
            {
                ApplyMessage(reader);
            }
            finally
            {
                EndScans();
                PerfStats.End("entity.apply", started);
            }
        }

        private void ApplyMessage(MessageReader reader)
        {
            SyncGuestSession();
            int epoch = reader.Int();
            int revision = reader.Int();
            if (epoch <= 0 || revision < 0) throw new FormatException("entity epoch/revision");
            if (epoch != _epoch) ResetRemote();
            else if (revision <= _revision) return;
            _epoch = epoch;
            _revision = revision;

            if (reader.Type == MessageType.EntityRemove)
            {
                int count = reader.Int();
                CheckCount(count);
                for (int i = 0; i < count; i++) RemoveRemote(reader.Guid(), true);
                return;
            }

            int fullFlag = reader.Byte();
            if (fullFlag > 1) throw new FormatException("entity full flag");
            int definitions = reader.Int();
            CheckCount(definitions);
            PerfStats.Count(fullFlag != 0 ? "entity.full" : "entity.delta");
            PerfStats.Count("entity.records", definitions);
            HashSet<Guid> seen = _seen;
            seen.Clear();
            for (int i = 0; i < definitions; i++)
            {
                Definition definition = ReadDefinition(reader);
                if (!seen.Add(definition.Id)) throw new FormatException("duplicate entity definition");
                Entity entity;
                if (!_byId.TryGetValue(definition.Id, out entity))
                    _byId[definition.Id] = entity = new Entity();
                if (!entity.Remote || entity.Definition == null || !entity.Definition.SameRecipe(definition))
                {
                    entity.Attempts = 0;
                    entity.NextAttempt = 0f;
                }
                entity.Remote = true;
                entity.Definition = definition;
                if (entity.Object == null) _pending.Add(definition.Id);
                // A repeated definition of an entity that is still unresolved waits for its retry:
                // attempting it here would search the whole scene at every manifest.
                if (entity.Object == null && Time.unscaledTime < entity.NextAttempt) PerfStats.Count("entity.skipped");
                else ApplyDefinition(definition);
            }
            if (fullFlag != 0)
            {
                // The manifest just repaired everything the local pass would.
                _nextGuestRepair = Time.unscaledTime + FullRefreshInterval;
                List<Guid> absent = null;
                foreach (var pair in _byId)
                    if (pair.Value.Definition.Origin != Origin.Saved && !seen.Contains(pair.Key))
                        (absent ?? (absent = new List<Guid>())).Add(pair.Key);
                if (absent != null) foreach (Guid id in absent) RemoveRemote(id, true);
            }
            seen.Clear();
        }

        private static void CheckCount(int count)
        {
            if (count < 0 || count > MaxEntities) throw new FormatException("entity count " + count);
        }

        private static Definition ReadDefinition(MessageReader reader)
        {
            var origin = (Origin)reader.Byte();
            if (origin < Origin.Saved || origin > Origin.Prefab) throw new FormatException("entity origin " + (byte)origin);
            var d = new Definition
            {
                Origin = origin,
                Id = reader.Guid(),
                Parent = reader.Guid(),
                Slot = reader.Int(),
                PrefabKey = NormalizePrefabKey(Protocol.Clean(reader.String(), MaxPrefabKeyLength)),
                AbilityKey = NormalizePrefabKey(Protocol.Clean(reader.String(), MaxAbilityKeyLength)),
                AbilityDisplayId = reader.Int(),
                AbilityOccurrence = reader.Int(),
                Position = ReadVector(reader),
            };
            d.Rotation = new Quaternion(reader.Float(), reader.Float(), reader.Float(), reader.Float());
            d.Scale = ReadVector(reader);
            int active = reader.Byte();
            if (active > 1) throw new FormatException("entity active flag");
            d.Active = active != 0;
            if (d.Id == Guid.Empty || d.Slot < 0 || d.Slot > 4095) throw new FormatException("entity id/slot");
            if ((origin == Origin.Encounter || origin == Origin.Summon) && d.Parent == Guid.Empty)
                throw new FormatException("entity parent");
            if (origin != Origin.Saved && d.PrefabKey.Length == 0) throw new FormatException("entity prefab");
            if (origin == Origin.Summon && d.AbilityKey.Length == 0) throw new FormatException("summon ability");
            if (d.AbilityDisplayId < -1 || d.AbilityDisplayId > 1000000 || d.AbilityOccurrence < 0 || d.AbilityOccurrence > 4095)
                throw new FormatException("summon ability identity");
            if (!ValidTransform(d.Position, d.Rotation, d.Scale)) throw new FormatException("entity transform");
            return d;
        }

        private static Vector3 ReadVector(MessageReader reader)
        {
            return new Vector3(reader.Float(), reader.Float(), reader.Float());
        }

        // ---- guest creation ----

        private void ResetRemote()
        {
            List<Guid> old = null;
            foreach (var pair in _byId)
                if (pair.Value.Definition != null && pair.Value.Definition.Origin != Origin.Saved)
                    (old ?? (old = new List<Guid>())).Add(pair.Key);
            if (old != null) foreach (Guid id in old) RemoveRemote(id, true);
            _byId.Clear();
            _byObject.Clear();
            _projectiles.Clear();
            _pending.Clear();
        }

        private void RemoveRemote(Guid id, bool destroy)
        {
            if (id == Guid.Empty || !_byId.TryGetValue(id, out Entity entity)) return;
            GameObject obj = entity.Object;
            ProbeEvent("guest-entity-remove", entity.Definition, obj != null ? obj.name : "already-destroyed");
            if (!ReferenceEquals(obj, null)) UnmapObject(obj);
            _byId.Remove(id);
            _pending.Remove(id);
            if (destroy && obj != null)
            {
                s_applyDepth++;
                try { GameUtilities.Destroy(obj); }
                finally { s_applyDepth--; }
            }
        }

        private bool ApplyDefinition(Definition d)
        {
            GameObject existing = ResolveObject(d.Id);
            if (existing == null) existing = FindInactiveSaved(d.Id);
            if (existing == null && d.Origin == Origin.Prefab) existing = FindRecipeDuplicate(d);
            if (existing != null)
            {
                BindRemote(d, existing);
                if (existing.activeSelf != d.Active) existing.SetActive(d.Active);
                return true;
            }
            if (!_session.IsGuest || GameState.IsLoading || !GameState.IsInGameSession) return false;

            GameObject created = null;
            s_applyDepth++;
            try
            {
                switch (d.Origin)
                {
                    case Origin.Encounter: created = CreateEncounterEntity(d); break;
                    case Origin.Summon: created = CreateSummon(d); break;
                    case Origin.Prefab: created = CreatePrefab(d); break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Could not create entity " + d.Id + " from " + d.PrefabKey + ": " + ex.Message);
            }
            finally { s_applyDepth--; }
            if (created == null) return false;
            BindRemote(d, created);
            created.transform.position = d.Position;
            created.transform.rotation = d.Rotation;
            created.transform.localScale = d.Scale;
            if (created.activeSelf != d.Active) created.SetActive(d.Active);
            ProbeEvent("guest-entity-create", d, created.name);
            return true;
        }

        private void BindRemote(Definition d, GameObject obj)
        {
            if (obj == null) return;
            InstanceID instance = obj.GetComponent<InstanceID>();
            if (instance == null) instance = obj.AddComponent<InstanceID>();
            if (ReadSavedId(obj) != d.Id) instance.Guid = d.Id;
            if (_byObject.TryGetValue(obj, out Guid old) && old != d.Id && _byId.TryGetValue(old, out Entity oldEntity))
            {
                if (oldEntity.Object == obj) oldEntity.Object = null;
                _byId.Remove(old);
            }
            if (!_byId.TryGetValue(d.Id, out Entity entity))
                _byId[d.Id] = entity = new Entity();
            if (!ReferenceEquals(entity.Object, null) && !ReferenceEquals(entity.Object, obj)) UnmapObject(entity.Object);
            entity.Object = obj;
            entity.Definition = d;
            entity.Remote = true;
            MapObject(obj, d.Id);
            _pending.Remove(d.Id);
        }

        private void BeginScans()
        {
            _scanDepth++;
        }

        private void EndScans()
        {
            if (_scanDepth > 0 && --_scanDepth > 0) return;
            _savedIndex.Clear();
            _savedIndexValid = false;
            _sceneTransforms = null;
        }

        private void ReleaseScans()
        {
            _savedIndex.Clear();
            _savedIndexValid = false;
            _sceneTransforms = null;
            _sceneTransformPositions = null;
        }

        /// <summary>Finds a saved object the game's active-id table does not list, using one scene scan per message or retry pass.</summary>
        private GameObject FindInactiveSaved(Guid id)
        {
            if (_scanDepth == 0) _savedIndexValid = false;
            for (int pass = 0; pass < 2; pass++)
            {
                if (!_savedIndexValid) BuildSavedIndex();
                if (!_savedIndex.TryGetValue(id, out GameObject obj)) return null;
                if (obj != null && ReadSavedId(obj) == id)
                {
                    BindResolved(id, obj);
                    return obj;
                }
                // The object was rebound or went away after the scan; look once more.
                _savedIndexValid = false;
            }
            return null;
        }

        private void BuildSavedIndex()
        {
            long started = PerfStats.Begin();
            _savedIndex.Clear();
            InstanceID[] all = Resources.FindObjectsOfTypeAll<InstanceID>();
            foreach (InstanceID instance in all)
            {
                if (instance == null) continue;
                GameObject obj = instance.gameObject;
                if (!obj.scene.IsValid()) continue;
                Guid saved = ReadSavedId(obj);
                if (saved != Guid.Empty && !_savedIndex.ContainsKey(saved)) _savedIndex.Add(saved, obj);
            }
            _savedIndexValid = true;
            PerfStats.End("entity.scan", started);
        }

        private GameObject FindRecipeDuplicate(Definition d)
        {
            if (_scanDepth == 0) _sceneTransforms = null;
            Transform[] all = _sceneTransforms;
            Vector3[] positions = _sceneTransformPositions;
            if (all == null)
            {
                long started = PerfStats.Begin();
                all = _sceneTransforms = Resources.FindObjectsOfTypeAll<Transform>();
                if (positions == null || positions.Length < all.Length)
                    positions = _sceneTransformPositions = new Vector3[all.Length];
                for (int i = 0; i < all.Length; i++)
                    positions[i] = all[i] != null ? all[i].position : new Vector3(float.NaN, float.NaN, float.NaN);
                PerfStats.End("entity.scan", started);
            }
            // Position first: it rejects nearly every candidate without reading (and allocating) its name.
            float x = d.Position.x, y = d.Position.y, z = d.Position.z;
            for (int i = 0; i < all.Length; i++)
            {
                float dx = positions[i].x - x, dy = positions[i].y - y, dz = positions[i].z - z;
                if (!(dx * dx + dy * dy + dz * dz < 0.0001f)) continue;
                Transform candidate = all[i];
                if (candidate == null || !candidate.gameObject.scene.IsValid()) continue;
                if (NormalizePrefabKey(candidate.name) != d.PrefabKey) continue;
                if ((candidate.position - d.Position).sqrMagnitude < 0.0001f) return candidate.gameObject;
            }
            return null;
        }

        private static GameObject CreateEncounterEntity(Definition d)
        {
            GameObject parent = ResolveObject(d.Parent);
            Encounter encounter = parent != null ? parent.GetComponent<Encounter>() : null;
            if (encounter == null || encounter.encounterList == null || d.Slot >= encounter.encounterList.Count) return null;
            EncounterData selected = encounter.encounterList[d.Slot];
            if (selected == null || selected.Creature == null) return null;

            List<EncounterData> originalData = encounter.encounterList;
            List<Guid> originalIds = EncounterIds(encounter);
            List<GameObject> originalSpawned = EncounterSpawned(encounter);
            List<GameObject> originalDisabled = EncounterDisabled(encounter);
            var temporarySpawned = new List<GameObject>();
            try
            {
                encounter.encounterList = new List<EncounterData> { selected };
                EncounterIds(encounter) = new List<Guid> { d.Id };
                EncounterSpawned(encounter) = temporarySpawned;
                EncounterDisabled(encounter) = new List<GameObject>();
                encounter.ForceSpawn();
            }
            finally
            {
                encounter.encounterList = originalData;
                while (originalIds.Count <= d.Slot) originalIds.Add(Guid.Empty);
                originalIds[d.Slot] = d.Id;
                EncounterIds(encounter) = originalIds;
                EncounterSpawned(encounter) = originalSpawned;
                originalDisabled.Remove(selected.Creature);
                EncounterDisabled(encounter) = originalDisabled;
            }
            foreach (GameObject obj in temporarySpawned)
            {
                if (obj == null) continue;
                if (!originalSpawned.Contains(obj)) originalSpawned.Add(obj);
                if (ReadSavedId(obj) == d.Id) return obj;
            }
            return ResolveObject(d.Id);
        }

        private static GameObject CreateSummon(Definition d)
        {
            GameObject owner = ResolveObject(d.Parent);
            if (owner == null) return null;
            Summon selected = null;
            CharacterStats prefab = null;
            var examined = new HashSet<Summon>();
            CharacterStats stats = owner.GetComponent<CharacterStats>();
            if (stats != null && stats.ActiveAbilities != null)
            {
                foreach (GenericAbility ability in stats.ActiveAbilities)
                {
                    if (ability == null) continue;
                    foreach (Summon summon in ability.GetComponents<Summon>())
                        if (examined.Add(summon) && SummonMatches(summon, owner, d, out prefab)) { selected = summon; break; }
                    if (selected != null) break;
                }
            }
            if (selected == null)
            {
                foreach (Summon summon in owner.GetComponentsInChildren<Summon>(true))
                    if (examined.Add(summon) && SummonMatches(summon, owner, d, out prefab)) { selected = summon; break; }
            }
            if (selected == null && stats != null)
            {
                Summon abilityPrefab = FindSummonAbilityPrefab(d);
                GenericAbility generic = abilityPrefab != null ? abilityPrefab.GetComponent<GenericAbility>() : null;
                if (generic != null)
                {
                    GenericAbility instance = stats.InstantiateAbility(generic, generic.EffectType);
                    selected = instance != null ? instance.GetComponent<Summon>() : null;
                    if (selected != null && s_instance != null) s_instance.ProbeEvent("guest-summon-ability-create", d, generic.name);
                }
            }
            if (selected == null) return null;
            if (prefab == null) prefab = GameResources.LoadPrefab<CharacterStats>(d.PrefabKey, false);
            if (prefab == null) return null;

            Summoner(selected) = owner;
            List<Health> before = new List<Health>(SummonedHealth(selected));
            var method = HarmonyLib.AccessTools.Method(typeof(Summon), "SummonCreature");
            try
            {
                method.Invoke(selected, new object[] { prefab, d.Position, owner.GetComponent<Faction>() });
            }
            catch
            {
                foreach (Health health in SummonedHealth(selected))
                    if (health != null && !before.Contains(health)) return health.gameObject;
                throw;
            }
            foreach (Health health in SummonedHealth(selected))
                if (health != null && !before.Contains(health)) return health.gameObject;
            foreach (Health health in SummonedHealth(selected))
                if (health != null && NormalizePrefabKey(health.name) == d.PrefabKey) return health.gameObject;
            return null;
        }

        private static Summon FindSummonAbilityPrefab(Definition d)
        {
            if (d.AbilityKey.Length == 0) return null;
            GameObject ability = GameResources.LoadPrefab<GameObject>(d.AbilityKey, false);
            return ability != null ? ability.GetComponent<Summon>() : null;
        }

        private static bool SummonMatches(Summon summon, GameObject owner, Definition d, out CharacterStats prefab)
        {
            prefab = null;
            if (summon.Owner != owner && Summoner(summon) != owner) return false;
            if (d.AbilityKey.Length > 0)
            {
                string key;
                int displayId, occurrence;
                SummonAbilityIdentity(summon, owner, out key, out displayId, out occurrence);
                if (key != d.AbilityKey || displayId != d.AbilityDisplayId || occurrence != d.AbilityOccurrence) return false;
            }
            return SummonProvides(summon, d.PrefabKey, out prefab);
        }

        private static bool SummonProvides(Summon summon, string key, out CharacterStats prefab)
        {
            prefab = null;
            foreach (CharacterStats candidate in summon.SummonList)
            {
                if (candidate != null && NormalizePrefabKey(candidate.name) == key)
                {
                    prefab = candidate;
                    return true;
                }
            }
            foreach (string file in summon.SummonFileList)
                if (NormalizePrefabKey(Path.GetFileNameWithoutExtension(file ?? "")) == key) return true;
            return false;
        }

        private static GameObject CreatePrefab(Definition d)
        {
            CharacterStats character = GameResources.LoadPrefab<CharacterStats>(d.PrefabKey, false);
            if (character != null) return GameResources.Instantiate<CharacterStats>(character, d.Position, d.Rotation).gameObject;
            GameObject prefab = GameResources.LoadPrefab<GameObject>(d.PrefabKey, false);
            return prefab != null ? UnityEngine.Object.Instantiate(prefab, d.Position, d.Rotation) : null;
        }

        // ---- opt-in diagnostics ----

        private void InitializeProbe()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--coop-entity-probe");
            if (index < 0 || index + 1 >= args.Length) return;
            try
            {
                string file = Path.GetFullPath(args[index + 1]);
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                _probe = new StreamWriter(file, false) { AutoFlush = true };
                _probeFrameSerializer = new DataContractJsonSerializer(typeof(EntityProbeFrame));
                _probeEventSerializer = new DataContractJsonSerializer(typeof(EntityProbeEvent));
                ProbeEvent("probe-start", null, "opt-in registry diagnostics");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Entity probe could not start: " + ex.Message);
                _probe?.Dispose();
                _probe = null;
            }
        }

        private void WriteProbe(float now)
        {
            if (_probe == null || now < _nextProbe) return;
            _nextProbe = now + 0.5f;
            try
            {
                var frame = new EntityProbeFrame
                {
                    utc = DateTime.UtcNow.ToString("o"),
                    role = _session.IsHost ? "host" : _session.IsGuest ? "guest" : "offline",
                    scene = SceneName(),
                    gameTime = now,
                    epoch = _epoch,
                    revision = _revision,
                    pending = _pending.Count,
                    inCombat = GameState.InCombat,
                };
                foreach (var pair in _byId)
                {
                    Entity entity = pair.Value;
                    Definition d = entity.Definition;
                    GameObject obj = entity.Object;
                    Health health = obj != null ? obj.GetComponent<Health>() : null;
                    frame.entities.Add(new EntityProbeEntity
                    {
                        id = pair.Key.ToString(),
                        parent = d.Parent.ToString(),
                        prefab = d.PrefabKey,
                        name = obj != null ? obj.name : "",
                        ability = d.AbilityKey,
                        origin = (int)d.Origin,
                        slot = d.Slot,
                        abilityDisplayId = d.AbilityDisplayId,
                        abilityOccurrence = d.AbilityOccurrence,
                        resolved = obj != null,
                        active = obj != null && obj.activeInHierarchy,
                        dead = health != null && health.Dead,
                        unconscious = health != null && health.Unconscious,
                        world = obj != null ? VectorProbe(obj.transform.position) : null,
                        health = health != null ? health.CurrentHealth : 0f,
                        stamina = health != null ? health.CurrentStamina : 0f,
                        maxHealth = health != null ? health.MaxHealth : 0f,
                        maxStamina = health != null ? health.MaxStamina : 0f,
                    });
                }
                foreach (Encounter encounter in Resources.FindObjectsOfTypeAll<Encounter>())
                {
                    if (encounter == null || !encounter.gameObject.scene.IsValid()) continue;
                    List<Guid> ids = EncounterIds(encounter);
                    var prefabs = new List<string>();
                    foreach (EncounterData entry in encounter.encounterList)
                        prefabs.Add(entry != null && entry.Creature != null ? NormalizePrefabKey(entry.Creature.name) + ":" + entry.IsPrefab : "");
                    var spawned = new List<string>();
                    foreach (GameObject obj in encounter.GetSpawnedList())
                        if (obj != null) spawned.Add(DiagnosticId(obj).ToString() + ":" + obj.name);
                    frame.encounters.Add(new EntityProbeEncounter
                    {
                        id = DiagnosticId(encounter.gameObject).ToString(),
                        name = encounter.name,
                        active = encounter.gameObject.activeInHierarchy,
                        spawnOnStart = encounter.SpawnOnStart,
                        guids = ids != null ? ids.ConvertAll(id => id.ToString()).ToArray() : new string[0],
                        prefabs = prefabs.ToArray(),
                        spawned = spawned.ToArray(),
                        world = VectorProbe(encounter.transform.position),
                    });
                }
                foreach (Summon summon in UnityEngine.Object.FindObjectsOfType<Summon>())
                {
                    if (summon == null) continue;
                    GameObject owner = Summoner(summon) != null ? Summoner(summon) : summon.Owner;
                    var prefabs = new List<string>();
                    foreach (CharacterStats prefab in summon.SummonList)
                        if (prefab != null) prefabs.Add(NormalizePrefabKey(prefab.name));
                    foreach (string prefab in summon.SummonFileList)
                        prefabs.Add(NormalizePrefabKey(Path.GetFileNameWithoutExtension(prefab ?? "")));
                    var summoned = new List<string>();
                    foreach (Health health in SummonedHealth(summon))
                        if (health != null) summoned.Add(DiagnosticId(health.gameObject).ToString() + ":" + health.name);
                    frame.summonAbilities.Add(new EntityProbeSummon
                    {
                        ability = summon.name.Replace("(Clone)", ""),
                        ownerId = DiagnosticId(owner).ToString(),
                        summonType = summon.SummonType.ToString(),
                        prefabs = prefabs.ToArray(),
                        summons = summoned.ToArray(),
                        activeSummons = summon.NumActiveSummons,
                        maxSummons = summon is SummonRandom ? ((SummonRandom)summon).MaxSummons : 1,
                        random = summon is SummonRandom,
                    });
                }
                WriteProbeJson(_probeFrameSerializer, frame);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Entity probe stopped: " + ex.Message);
                _probe.Dispose();
                _probe = null;
            }
        }

        private void ProbeEvent(string action, Definition d, string detail)
        {
            if (_probe == null) return;
            try
            {
                WriteProbeJson(_probeEventSerializer, new EntityProbeEvent
                {
                    utc = DateTime.UtcNow.ToString("o"),
                    role = _session.IsHost ? "host" : _session.IsGuest ? "guest" : "offline",
                    scene = SceneName(),
                    action = action,
                    id = d != null ? d.Id.ToString() : "",
                    parent = d != null ? d.Parent.ToString() : "",
                    prefab = d != null ? d.PrefabKey : "",
                    detail = detail,
                    ability = d != null ? d.AbilityKey : "",
                    gameTime = Time.unscaledTime,
                    origin = d != null ? (int)d.Origin : 0,
                    slot = d != null ? d.Slot : 0,
                    abilityDisplayId = d != null ? d.AbilityDisplayId : -1,
                    abilityOccurrence = d != null ? d.AbilityOccurrence : 0,
                    epoch = _epoch,
                    revision = _revision,
                });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Entity probe event failed: " + ex.Message);
            }
        }

        internal static void ProbeBlocked(string action, GameObject obj)
        {
            EntityRegistry self = s_instance;
            if (self == null || self._probe == null) return;
            float now = Time.unscaledTime;
            if (self._blockedProbeTimes.TryGetValue(action, out float next) && now < next) return;
            self._blockedProbeTimes[action] = now + 2f;
            self.ProbeEvent(action, null, "blocked-autonomous:" + (obj != null ? obj.name : ""));
        }

        private void WriteProbeJson(DataContractJsonSerializer serializer, object value)
        {
            using (var json = new MemoryStream())
            {
                serializer.WriteObject(json, value);
                _probe.WriteLine(Encoding.UTF8.GetString(json.ToArray()));
            }
        }

        private Guid DiagnosticId(GameObject obj)
        {
            if (obj == null) return Guid.Empty;
            return _byObject.TryGetValue(obj, out Guid id) ? id : ReadSavedId(obj);
        }

        private string SceneName()
        {
            return GameState.Instance != null && GameState.Instance.CurrentMap != null ? GameState.Instance.CurrentMap.SceneName : "";
        }

        private static EntityProbeVector VectorProbe(Vector3 value)
        {
            return new EntityProbeVector { x = value.x, y = value.y, z = value.z };
        }

        // ---- validation ----

        internal static string NormalizePrefabKey(string name)
        {
            if (name == null) name = "";
            if (PrefabKeys.TryGetValue(name, out string cached)) return cached;
            string key = Protocol.Clean(name.Replace("(Clone)", ""), MaxPrefabKeyLength);
            key = Path.GetFileNameWithoutExtension(key).Trim().ToLowerInvariant();
            if (name.Length <= MaxCachedPrefabKeyLength)
            {
                if (PrefabKeys.Count >= MaxCachedPrefabKeys) PrefabKeys.Clear();
                PrefabKeys[name] = key;
            }
            return key;
        }

        private static bool ValidTransform(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            return Finite(position) && Finite(rotation.x) && Finite(rotation.y) && Finite(rotation.z) && Finite(rotation.w)
                && Mathf.Abs(position.x) <= MaxCoordinate && Mathf.Abs(position.y) <= MaxCoordinate && Mathf.Abs(position.z) <= MaxCoordinate
                && Mathf.Abs(rotation.x) <= 1f && Mathf.Abs(rotation.y) <= 1f && Mathf.Abs(rotation.z) <= 1f && Mathf.Abs(rotation.w) <= 1f
                && rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w > 0.000001f
                && Finite(scale) && Mathf.Abs(scale.x) <= MaxScale && Mathf.Abs(scale.y) <= MaxScale && Mathf.Abs(scale.z) <= MaxScale;
        }

        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
