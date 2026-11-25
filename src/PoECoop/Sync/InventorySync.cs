using System;
using System.Collections.Generic;
using HarmonyLib;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>Atomic, paged inventory snapshots and complete equipment snapshots.</summary>
    public sealed class InventorySync : IDisposable
    {
        internal enum InventoryKind : byte { Party = 1, Quickbar = 2, Stash = 3, Quest = 4, Crafting = 5, Container = 6 }

        private const int ItemsPerPage = 32;
        private const int MaxItemsPerInventory = 4096;
        private const int MaxPages = 128;
        private const int MaxEquipmentSlots = 13;
        private const int MaxWeaponSets = EquipmentSet.MAX_WEAPON_SETS;
        private const int MaxCurrency = 1000000000;
        private const ulong HashSeed = 14695981039346656037UL;
        private const float SendInterval = 0.1f;
        private const float FullRefreshInterval = 3f;
        private const float RetryInterval = 0.25f;
        private const float MaxRetryInterval = 2f;
        private const float DiscoverInterval = 0.1f;
        private const float FallbackScanInterval = 0.5f;
        // Added once after a forced refresh so this stream's next integrity pass misses the other streams'.
        private const float ForcedRefreshPhase = 2.25f;
        private static readonly MessageType InventoryStateMessage = (MessageType)42;
        private static readonly MessageType EquipmentStateMessage = (MessageType)43;

        private static readonly AccessTools.FieldRef<BaseInventory, List<InventoryItem>> InventoryItems =
            AccessTools.FieldRefAccess<BaseInventory, List<InventoryItem>>("m_items");
        private static readonly AccessTools.FieldRef<Equipment, EquipmentSet> CurrentSet =
            AccessTools.FieldRefAccess<Equipment, EquipmentSet>("m_currentItems");
        private static readonly AccessTools.FieldRef<Equipment, bool[]> SlotLocks =
            AccessTools.FieldRefAccess<Equipment, bool[]>("m_SlotLocked");
        private static readonly AccessTools.FieldRef<Equipment, Weapon> CachedPrimary =
            AccessTools.FieldRefAccess<Equipment, Weapon>("m_primaryWeapon");
        private static readonly AccessTools.FieldRef<Equipment, Weapon> CachedSecondary =
            AccessTools.FieldRefAccess<Equipment, Weapon>("m_secondaryWeapon");
        private static readonly AccessTools.FieldRef<UIInventoryWeaponSetManager, Equipment> SelectedWeaponEquipment =
            AccessTools.FieldRefAccess<UIInventoryWeaponSetManager, Equipment>("m_SelectedEquipment");
        private static readonly AccessTools.FieldRef<Equippable, GameObject> ItemOwner =
            AccessTools.FieldRefAccess<Equippable, GameObject>("m_owner");
        private static readonly AccessTools.FieldRef<Equippable, Equipment> ItemEquipment =
            AccessTools.FieldRefAccess<Equippable, Equipment>("m_ownerEquipment");
        private static readonly AccessTools.FieldRef<Equippable, Equippable.EquipmentSlot> ItemSlot =
            AccessTools.FieldRefAccess<Equippable, Equippable.EquipmentSlot>("m_equipped_slot");

        // The game's own registry of started containers; reading it replaces a scene scan.
        private static readonly System.Reflection.FieldInfo AllContainers =
            AccessTools.Field(typeof(Container), "s_AllContainers");

        private static InventorySync s_instance;
        private static int s_applyDepth;

        private sealed class TrackedInventory
        {
            internal BaseInventory Inventory;
            internal InventoryKind Kind;
            internal Guid Id;
            internal Guid Owner;
            internal int Revision;
            internal Guid MutationGroup;
            internal bool Dirty;
            internal bool HasHash;
            internal ulong Hash;
            internal float RefreshAt;
        }

        private sealed class TrackedEquipment
        {
            internal Equipment Equipment;
            internal Guid Actor;
            internal int Revision;
            internal Guid MutationGroup;
            internal bool Dirty;
            internal bool HasHash;
            internal ulong Hash;
            internal float RefreshAt;
        }

        private sealed class ItemRecord
        {
            internal ItemRegistry.Recipe Recipe;
            internal int Stack;
            internal int Slot;
            internal bool Original;
            internal bool AreaLootSource;
        }

        private sealed class InventoryPage
        {
            internal long Epoch;
            internal Guid InventoryId;
            internal InventoryKind Kind;
            internal Guid Owner;
            internal int Revision;
            internal Guid MutationGroup;
            internal int Page;
            internal int PageCount;
            internal int TotalItems;
            internal int MaxItems;
            internal float Currency;
            internal int Supplies;
            internal int Flags;
            internal List<ItemRecord> Items;
        }

        private sealed class InventoryAssembly
        {
            internal long Epoch;
            internal int Revision;
            internal Guid MutationGroup;
            internal readonly Dictionary<int, InventoryPage> Pages = new Dictionary<int, InventoryPage>();
            internal List<ItemRecord> Records;
            internal bool Costly;
            internal float Delay;
            internal float NextAttempt;
        }

        private sealed class EquipmentRecord
        {
            internal long Epoch;
            internal Guid Actor;
            internal int Revision;
            internal Guid MutationGroup;
            internal int Selected;
            internal int Locks;
            internal Guid[] Slots;
            internal Guid[] Weapons;
            internal List<ItemRegistry.Recipe> Recipes;
            internal bool Costly;
            internal float Delay;
            internal float NextAttempt;
        }

        private readonly Session _session;
        private readonly Ownership _ownership;
        private readonly Dictionary<Guid, TrackedInventory> _inventories = new Dictionary<Guid, TrackedInventory>();
        private readonly Dictionary<Guid, TrackedEquipment> _equipment = new Dictionary<Guid, TrackedEquipment>();
        private readonly Dictionary<Guid, InventoryAssembly> _assemblies = new Dictionary<Guid, InventoryAssembly>();
        private readonly Dictionary<Guid, int> _appliedInventories = new Dictionary<Guid, int>();
        private readonly Dictionary<Guid, EquipmentRecord> _pendingEquipment = new Dictionary<Guid, EquipmentRecord>();
        private readonly Dictionary<Guid, int> _appliedEquipment = new Dictionary<Guid, int>();
        private readonly Dictionary<NPCAppearance, float> _appearanceAt = new Dictionary<NPCAppearance, float>();
        private readonly HashSet<Guid> _possiblyRemoved = new HashSet<Guid>();
        private readonly HashSet<BaseInventory> _uiInventories = new HashSet<BaseInventory>();
        private readonly HashSet<Guid> _scratchIds = new HashSet<Guid>();
        private readonly List<ItemRecord> _scratchRecords = new List<ItemRecord>();
        private readonly List<Guid> _scratchSlots = new List<Guid>();
        private readonly List<Guid> _scratchWeapons = new List<Guid>();
        private readonly List<ItemRegistry.Recipe> _scratchRecipes = new List<ItemRegistry.Recipe>();
        private readonly List<KeyValuePair<Guid, InventoryAssembly>> _scratchAssemblies = new List<KeyValuePair<Guid, InventoryAssembly>>();
        private readonly List<EquipmentRecord> _scratchEquipment = new List<EquipmentRecord>();
        private readonly List<Guid> _scratchDead = new List<Guid>();
        private readonly Dictionary<Equippable, Equippable.EquipmentSlot> _expectedSlots =
            new Dictionary<Equippable, Equippable.EquipmentSlot>();
        private long _generation;
        private long _worldEpoch;
        private bool _worldReady;
        private long _stateSequence;
        private int _mutationDepth;
        private Guid _activeMutation;
        private float _nextSend;
        private bool _forceFull = true;
        private float _nextRetry;
        private float _nextCleanup;
        private float _nextDiscover;
        private float _nextFallbackScan;
        private int _discoverFrame = -1;

        public InventorySync(Session session, Ownership ownership)
        {
            _session = session;
            _ownership = ownership;
            _generation = session.Generation;
            s_instance = this;
            session.GameMessage += OnGameMessage;
            session.PlayerJoined += OnPlayerJoined;
            session.FullStateRequested += OnFullStateRequested;
            GameState.OnLevelLoaded += OnLevelChanged;
            GameState.OnLevelUnload += OnLevelChanged;
        }

        public static long CurrentWorldEpoch => s_instance != null ? s_instance._worldEpoch : 0;
        public static bool WorldReady => s_instance != null && s_instance._worldReady;
        public static long CurrentStateSequence => s_instance != null ? s_instance._stateSequence : 0;
        public static bool IsApplyingHostState => s_applyDepth > 0;

        public void Dispose()
        {
            _session.GameMessage -= OnGameMessage;
            _session.PlayerJoined -= OnPlayerJoined;
            _session.FullStateRequested -= OnFullStateRequested;
            GameState.OnLevelLoaded -= OnLevelChanged;
            GameState.OnLevelUnload -= OnLevelChanged;
            foreach (TrackedInventory tracked in _inventories.Values)
                if (tracked.Inventory != null) tracked.Inventory.OnChanged -= OnInventoryChanged;
            foreach (TrackedEquipment tracked in _equipment.Values)
                if (tracked.Equipment != null) tracked.Equipment.OnEquipmentChanged -= OnEquipmentChanged;
            if (s_instance == this)
            {
                s_instance = null;
                s_applyDepth = 0;
            }
            _inventories.Clear();
            _equipment.Clear();
            _assemblies.Clear();
            _pendingEquipment.Clear();
            _appliedInventories.Clear();
            _appliedEquipment.Clear();
            _appearanceAt.Clear();
            _possiblyRemoved.Clear();
            _uiInventories.Clear();
        }

        /// <summary>Called by FlowCoordinator for WorldReady, world reset, and readiness barriers.</summary>
        public void SetWorldState(long worldEpoch, bool ready)
        {
            if (worldEpoch < 0) throw new ArgumentOutOfRangeException(nameof(worldEpoch));
            bool epochChanged = _worldEpoch != worldEpoch;
            if (epochChanged)
            {
                _worldEpoch = worldEpoch;
                _stateSequence = 0;
                _assemblies.Clear();
                _pendingEquipment.Clear();
                _appliedInventories.Clear();
                _appliedEquipment.Clear();
                _possiblyRemoved.Clear();
                foreach (TrackedInventory tracked in _inventories.Values)
                {
                    tracked.Revision = 0;
                    tracked.MutationGroup = Guid.Empty;
                    tracked.Dirty = true;
                    tracked.HasHash = false;
                }
                foreach (TrackedEquipment tracked in _equipment.Values)
                {
                    tracked.Revision = 0;
                    tracked.MutationGroup = Guid.Empty;
                    tracked.Dirty = true;
                    tracked.HasHash = false;
                }
            }
            if (!epochChanged && _worldReady == ready) return;
            _worldReady = ready;
            _nextSend = 0f;
            _forceFull = true;
            Plugin.Log.LogInfo("Inventory world state epoch=" + _worldEpoch + " ready=" + _worldReady);
        }

        public void Update(float now)
        {
            if (_generation != _session.Generation)
            {
                _generation = _session.Generation;
                _assemblies.Clear();
                _pendingEquipment.Clear();
                _appliedInventories.Clear();
                _appliedEquipment.Clear();
                _possiblyRemoved.Clear();
                _uiInventories.Clear();
                _worldReady = false;
                _worldEpoch = 0;
                _nextSend = 0f;
                _nextDiscover = 0f;
            }
            if (!_session.IsHost && !_session.IsGuest) return;

            // New inventories only matter to the next send or apply; ResolveInventory and RequestResync
            // still discover on demand.
            if (now >= _nextDiscover)
            {
                _nextDiscover = now + DiscoverInterval;
                DiscoverModel();
            }
            if (_session.IsGuest)
            {
                long started = PerfStats.Begin();
                if (now >= _nextRetry)
                {
                    _nextRetry = now + RetryInterval;
                    RetryPending(now);
                    UpdateAppearance(now);
                }
                if (_possiblyRemoved.Count > 0 && now >= _nextCleanup) CleanupRemoved();
                FlushUi();
                PerfStats.End("inventory.update", started);
                return;
            }
            if (!_worldReady || _worldEpoch <= 0 || _session.Players.Count < 2
                || !GameState.IsInGameSession || GameState.IsLoading) return;
            if (now < _nextSend) return;
            _nextSend = now + SendInterval;
            bool forced = _forceFull;
            _forceFull = false;
            SendChanged(forced, now, -1, true);
        }

        internal static IDisposable ApplyScope()
        {
            s_applyDepth++;
            return new ApplyScopeDisposable();
        }

        private struct ApplyScopeDisposable : IDisposable
        {
            public void Dispose() => s_applyDepth--;
        }

        internal static Guid IdOf(BaseInventory inventory)
        {
            if (inventory == null) return Guid.Empty;
            Container container = inventory.GetComponent<Container>();
            if (container != null) return EntityRegistry.NetworkId(container.gameObject);
            PartyMemberAI member = inventory.GetComponent<PartyMemberAI>();
            if (member == null && inventory is StashInventory && GameState.s_playerCharacter != null)
                member = GameState.s_playerCharacter.GetComponent<PartyMemberAI>();
            if (member == null) return Guid.Empty;
            Guid actor = Ownership.IdOf(member);
            if (actor == Guid.Empty) actor = EntityRegistry.NetworkId(member.gameObject);
            return actor == Guid.Empty ? Guid.Empty : DeriveInventoryId(actor, KindOf(inventory));
        }

        internal static Guid ActorOf(BaseInventory inventory)
        {
            PartyMemberAI member = inventory != null ? inventory.GetComponent<PartyMemberAI>() : null;
            if (member == null && inventory is StashInventory && GameState.s_playerCharacter != null)
                member = GameState.s_playerCharacter.GetComponent<PartyMemberAI>();
            return member != null ? Ownership.IdOf(member) : Guid.Empty;
        }

        internal static BaseInventory ResolveInventory(Guid id)
        {
            InventorySync self = s_instance;
            if (self == null || id == Guid.Empty) return null;
            // Several lookups in one frame (a paged join, a command's source and target) share one pass.
            if (self._discoverFrame != Time.frameCount) self.DiscoverModel();
            if (self._inventories.TryGetValue(id, out TrackedInventory tracked) && tracked.Inventory != null)
                return tracked.Inventory;
            GameObject obj = EntityRegistry.ResolveObject(id);
            Container container = obj != null ? obj.GetComponent<Container>() : null;
            Inventory inventory = container != null ? container.GetComponent<Inventory>() : null;
            if (inventory == null) return null;
            self.TrackInventory(inventory, InventoryKind.Container, id);
            return inventory;
        }

        internal static Equipment ResolveEquipment(Guid actor)
        {
            GameObject obj = EntityRegistry.ResolveObject(actor);
            if (obj == null) obj = InstanceID.GetObjectByID(actor);
            return obj != null ? obj.GetComponent<Equipment>() : null;
        }

        internal static PartyMemberAI ResolveMember(Guid actor)
        {
            GameObject obj = EntityRegistry.ResolveObject(actor);
            if (obj == null) obj = InstanceID.GetObjectByID(actor);
            return obj != null ? obj.GetComponent<PartyMemberAI>() : null;
        }

        internal static int RevisionOf(BaseInventory inventory)
        {
            InventorySync self = s_instance;
            Guid id = IdOf(inventory);
            return self != null && self._inventories.TryGetValue(id, out TrackedInventory tracked) ? tracked.Revision : 0;
        }

        internal static int RevisionOf(Equipment equipment)
        {
            InventorySync self = s_instance;
            if (self == null || equipment == null) return 0;
            PartyMemberAI member = equipment.GetComponent<PartyMemberAI>();
            Guid actor = member != null ? Ownership.IdOf(member) : Guid.Empty;
            return self._equipment.TryGetValue(actor, out TrackedEquipment tracked) ? tracked.Revision : 0;
        }

        internal static Guid MutationOf(BaseInventory inventory)
        {
            InventorySync self = s_instance;
            Guid id = IdOf(inventory);
            return self != null && self._inventories.TryGetValue(id, out TrackedInventory tracked) ? tracked.MutationGroup : Guid.Empty;
        }

        internal static void MarkChanged(BaseInventory inventory)
        {
            InventorySync self = s_instance;
            if (self == null || inventory == null) return;
            Guid id = IdOf(inventory);
            if (self._inventories.TryGetValue(id, out TrackedInventory tracked)) self.MarkDirty(tracked);
        }

        internal static void MarkChanged(Equipment equipment)
        {
            InventorySync self = s_instance;
            if (self == null || equipment == null) return;
            PartyMemberAI member = equipment.GetComponent<PartyMemberAI>();
            Guid actor = member != null ? Ownership.IdOf(member) : Guid.Empty;
            if (self._equipment.TryGetValue(actor, out TrackedEquipment tracked)) self.MarkDirty(tracked);
        }

        internal static void BeginMutation()
        {
            InventorySync self = s_instance;
            if (self == null) return;
            if (self._mutationDepth++ == 0) self._activeMutation = Guid.NewGuid();
        }

        internal static void EndMutation(bool changed)
        {
            InventorySync self = s_instance;
            if (self == null || self._mutationDepth <= 0) return;
            if (--self._mutationDepth == 0)
            {
                if (changed) self._stateSequence = Next(self._stateSequence);
                self._activeMutation = Guid.Empty;
                self._nextSend = 0f;
            }
        }

        internal static void RequestResync() => RequestResync(-1);

        internal static void RequestResync(int playerId)
        {
            InventorySync self = s_instance;
            if (self == null || !self._session.IsHost || self._worldEpoch <= 0
                || playerId >= 0 && !self._session.IsPeerReady(playerId)) return;
            self.DiscoverModel();
            foreach (TrackedInventory tracked in self._inventories.Values) self.Touch(tracked);
            foreach (TrackedEquipment tracked in self._equipment.Values) self.Touch(tracked);
            self._nextSend = 0f;
            // With a single guest this is the whole audience, so it restarts the integrity schedule like any
            // other forced refresh. With more guests the others keep their schedule: they did not get this one.
            self.SendChanged(true, Time.unscaledTime, playerId, playerId < 0 || self._session.Players.Count <= 2);
        }

        private void OnFullStateRequested(int playerId, long worldEpoch)
        {
            if (worldEpoch == _worldEpoch) RequestResync(playerId);
        }

        private void DiscoverModel()
        {
            long started = PerfStats.Begin();
            _discoverFrame = Time.frameCount;
            foreach (PartyMemberAI member in PartyMemberAI.OnlyPrimaryPartyMembers)
            {
                if (member == null) continue;
                Guid actor = Ownership.IdOf(member);
                if (actor == Guid.Empty) actor = EntityRegistry.NetworkId(member.gameObject);
                if (actor == Guid.Empty) continue;
                TrackInventory(member.Inventory, InventoryKind.Party, actor);
                TrackInventory(member.QuickbarInventory, InventoryKind.Quickbar, actor);
                TrackEquipment(member.GetComponent<Equipment>(), actor);
            }
            PlayerInventory player = GameState.PlayerInventory;
            if (player != null)
            {
                Guid actor = ActorOf(player);
                TrackInventory(player.StashInventory, InventoryKind.Stash, actor);
                TrackInventory(player.QuestInventory, InventoryKind.Quest, actor);
                TrackInventory(player.CraftingInventory, InventoryKind.Crafting, actor);
            }
            var containers = AllContainers != null ? AllContainers.GetValue(null) as HashSet<Container> : null;
            if (containers != null)
            {
                // Same population a scene scan returns (active objects), minus containers created this frame
                // that have not started yet; those appear on the next pass.
                foreach (Container container in containers)
                    if (container != null && container.gameObject.activeInHierarchy) TrackContainer(container);
            }
            else if (Time.unscaledTime >= _nextFallbackScan)
            {
                _nextFallbackScan = Time.unscaledTime + FallbackScanInterval;
                foreach (Container container in UnityEngine.Object.FindObjectsOfType<Container>())
                    if (container != null) TrackContainer(container);
            }
            PerfStats.End("inventory.scan", started);
        }

        private void TrackContainer(Container container)
        {
            Guid id = EntityRegistry.NetworkId(container.gameObject);
            if (id != Guid.Empty) TrackInventory(container.GetComponent<Inventory>(), InventoryKind.Container, id);
        }

        private void TrackInventory(BaseInventory inventory, InventoryKind kind, Guid actor)
        {
            if (inventory == null || actor == Guid.Empty) return;
            Guid id = kind == InventoryKind.Container ? actor : DeriveInventoryId(actor, kind);
            if (_inventories.TryGetValue(id, out TrackedInventory existing))
            {
                if (existing.Inventory == inventory) return;
                if (existing.Inventory != null) existing.Inventory.OnChanged -= OnInventoryChanged;
            }
            var tracked = new TrackedInventory
            {
                Inventory = inventory,
                Kind = kind,
                Id = id,
                Owner = actor,
                Dirty = true,
            };
            _inventories[id] = tracked;
            inventory.OnChanged += OnInventoryChanged;
        }

        private void TrackEquipment(Equipment equipment, Guid actor)
        {
            if (equipment == null || actor == Guid.Empty) return;
            if (_equipment.TryGetValue(actor, out TrackedEquipment existing))
            {
                if (existing.Equipment == equipment) return;
                if (existing.Equipment != null) existing.Equipment.OnEquipmentChanged -= OnEquipmentChanged;
            }
            var tracked = new TrackedEquipment { Equipment = equipment, Actor = actor, Dirty = true };
            _equipment[actor] = tracked;
            equipment.OnEquipmentChanged += OnEquipmentChanged;
        }

        private void OnInventoryChanged(BaseInventory inventory)
        {
            if (IsApplyingHostState || !_session.IsHost) return;
            MarkChanged(inventory);
        }

        private void OnEquipmentChanged(Equippable.EquipmentSlot slot, Equippable oldItem, Equippable newItem,
            bool swappingSummonedWeapon, bool enforceRecoveryPenalty)
        {
            if (IsApplyingHostState || !_session.IsHost) return;
            Equipment equipment = newItem != null ? newItem.m_ownerEquipment : oldItem != null ? oldItem.m_ownerEquipment : null;
            if (equipment == null && oldItem != null) equipment = oldItem.GetComponentInParent<Equipment>();
            if (equipment == null && newItem != null) equipment = newItem.GetComponentInParent<Equipment>();
            MarkChanged(equipment);
            PartyMemberAI member = equipment != null ? equipment.GetComponent<PartyMemberAI>() : null;
            if (member != null)
            {
                MarkChanged(member.Inventory);
                MarkChanged(member.QuickbarInventory);
            }
        }

        /// <summary>
        /// Sends every changed inventory and equipment set, plus the ones whose integrity refresh is due.
        /// A forced pass refreshes everything now; afterwards each one keeps its own slot in the refresh
        /// interval, so the periodic refreshes no longer leave as one burst.
        /// </summary>
        private void SendChanged(bool forced, float now, int targetPlayerId, bool reschedule)
        {
            long started = PerfStats.Begin();
            int total = _inventories.Count + _equipment.Count;
            int index = 0;
            int skipped = 0;
            foreach (TrackedInventory tracked in _inventories.Values)
            {
                index++;
                if (tracked.Inventory == null) continue;
                ulong hash = InventoryHash(tracked.Inventory);
                if (!tracked.HasHash || hash != tracked.Hash)
                {
                    tracked.HasHash = true;
                    tracked.Hash = hash;
                    MarkDirty(tracked);
                }
                bool refresh = !tracked.Dirty && (forced || now >= tracked.RefreshAt);
                if (refresh) Touch(tracked);
                if (!tracked.Dirty)
                {
                    skipped++;
                    continue;
                }
                SendInventory(tracked, targetPlayerId);
                PerfStats.Count(refresh ? "inventory.full" : "inventory.delta");
                if (reschedule) tracked.RefreshAt = NextRefresh(forced, now, index, total);
            }
            foreach (TrackedEquipment tracked in _equipment.Values)
            {
                index++;
                if (tracked.Equipment == null || CurrentSet(tracked.Equipment) == null) continue;
                ulong hash = EquipmentHash(tracked.Equipment);
                if (!tracked.HasHash || hash != tracked.Hash)
                {
                    tracked.HasHash = true;
                    tracked.Hash = hash;
                    MarkDirty(tracked);
                }
                bool refresh = !tracked.Dirty && (forced || now >= tracked.RefreshAt);
                if (refresh) Touch(tracked);
                if (!tracked.Dirty)
                {
                    skipped++;
                    continue;
                }
                SendEquipment(tracked, targetPlayerId);
                PerfStats.Count(refresh ? "inventory.full" : "inventory.delta");
                if (reschedule) tracked.RefreshAt = NextRefresh(forced, now, index, total);
            }
            if (skipped > 0) PerfStats.Count("inventory.skipped", skipped);
            PerfStats.End("inventory.capture", started);
        }

        private static float NextRefresh(bool forced, float now, int index, int total)
        {
            if (!forced) return now + FullRefreshInterval;
            return now + FullRefreshInterval + ForcedRefreshPhase + FullRefreshInterval * (index - 1) / Math.Max(1, total);
        }

        private void SendInventory(TrackedInventory tracked, int targetPlayerId)
        {
            List<ItemRecord> records = _scratchRecords;
            CaptureItems(tracked.Inventory, records);
            int pages = Math.Max(1, (records.Count + ItemsPerPage - 1) / ItemsPerPage);
            PlayerInventory player = tracked.Inventory as PlayerInventory;
            int flags = tracked.Inventory is QuestInventory quest && quest.HasNew ? 1 : 0;
            for (int page = 0; page < pages; page++)
            {
                var writer = new MessageWriter(InventoryStateMessage)
                    .Long(_worldEpoch).Guid(tracked.Id).Byte((byte)tracked.Kind).Guid(tracked.Owner)
                    .Int(tracked.Revision).Guid(tracked.MutationGroup)
                    .Int(page).Int(pages).Int(records.Count).Int(BoundedMaxItems(tracked.Inventory.MaxItems))
                    .Float(player != null ? player.currencyTotalValue.v : 0f)
                    .Int(player != null ? player.CampingSuppliesTotal : 0).Int(flags).Int(0);
                int count = 0;
                for (int i = page * ItemsPerPage; i < records.Count && i < (page + 1) * ItemsPerPage; i++)
                {
                    WriteItemRecord(writer, records[i]);
                    count++;
                }
                byte[] data = writer.ToArray();
                WriteCount(data, CountOffsetInventory(), count);
                if (targetPlayerId < 0) _session.SendToGuests(data);
                else _session.SendToPlayer(targetPlayerId, data);
            }
            PerfStats.Count("inventory.records", records.Count);
            records.Clear();
            tracked.Dirty = false;
        }

        private void SendEquipment(TrackedEquipment tracked, int targetPlayerId)
        {
            EquipmentSet set = CurrentSet(tracked.Equipment);
            List<Guid> ids = _scratchSlots;
            List<Guid> weapons = _scratchWeapons;
            List<ItemRegistry.Recipe> recipes = _scratchRecipes;
            HashSet<Guid> seen = _scratchIds;
            ids.Clear();
            weapons.Clear();
            recipes.Clear();
            seen.Clear();
            for (int slot = 0; slot < MaxEquipmentSlots; slot++)
                ids.Add(ItemId(slot == (int)Equippable.EquipmentSlot.Cape_DEPRECATED
                    ? set.Cape : set.GetItemInSlot((Equippable.EquipmentSlot)slot)));
            int setCount = Math.Min(MaxWeaponSets, set.AlternateWeaponSets != null ? set.AlternateWeaponSets.Length : 0);
            for (int i = 0; i < setCount; i++)
            {
                WeaponSet weaponSet = set.AlternateWeaponSets[i];
                weapons.Add(ItemId(weaponSet != null ? weaponSet.PrimaryWeapon : null));
                weapons.Add(ItemId(weaponSet != null ? weaponSet.SecondaryWeapon : null));
            }
            for (int i = 0; i < ids.Count; i++) AddRecipe(ids[i], seen, recipes);
            for (int i = 0; i < weapons.Count; i++) AddRecipe(weapons[i], seen, recipes);
            bool[] locks = SlotLocks(tracked.Equipment);
            int lockBits = 0;
            for (int i = 0; i < MaxEquipmentSlots && locks != null && i < locks.Length; i++) if (locks[i]) lockBits |= 1 << i;

            var writer = new MessageWriter(EquipmentStateMessage)
                .Long(_worldEpoch).Guid(tracked.Actor).Int(tracked.Revision).Guid(tracked.MutationGroup)
                .Int(set.SelectedWeaponSet).Int(lockBits).Int(setCount).Int(ids.Count);
            for (int i = 0; i < ids.Count; i++) writer.Guid(ids[i]);
            writer.Int(weapons.Count);
            for (int i = 0; i < weapons.Count; i++) writer.Guid(weapons[i]);
            writer.Int(recipes.Count);
            for (int i = 0; i < recipes.Count; i++) ItemRegistry.WriteRecipe(writer, recipes[i]);
            byte[] data = writer.ToArray();
            if (targetPlayerId < 0) _session.SendToGuests(data);
            else _session.SendToPlayer(targetPlayerId, data);
            PerfStats.Count("inventory.records", recipes.Count);
            recipes.Clear();
            tracked.Dirty = false;
        }

        private static int CountOffsetInventory()
        {
            return 1 + 8 + 16 + 1 + 16 + 4 + 16 + 4 * 7;
        }

        private static void WriteCount(byte[] data, int offset, int count)
        {
            data[offset] = (byte)count;
            data[offset + 1] = (byte)(count >> 8);
            data[offset + 2] = (byte)(count >> 16);
            data[offset + 3] = (byte)(count >> 24);
        }

        private static void WriteItemRecord(MessageWriter writer, ItemRecord record)
        {
            ItemRegistry.WriteRecipe(writer, record.Recipe);
            writer.Int(record.Stack).Int(record.Slot)
                .Byte((byte)(record.Original ? 1 : 0)).Byte((byte)(record.AreaLootSource ? 1 : 0));
        }

        private static ItemRecord ReadItemRecord(MessageReader reader)
        {
            var record = new ItemRecord
            {
                Recipe = ItemRegistry.ReadRecipe(reader),
                Stack = reader.Int(),
                Slot = reader.Int(),
            };
            int original = reader.Byte();
            int area = reader.Byte();
            if (original > 1 || area > 1) throw new FormatException("inventory item flags");
            record.Original = original != 0;
            record.AreaLootSource = area != 0;
            if (record.Stack <= 0 || record.Stack > 1000000000 || record.Slot < -1 || record.Slot > 1000000)
                throw new FormatException("inventory stack/slot");
            return record;
        }

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            if (sender != null || !_session.IsGuest) return;
            if (reader.Type != InventoryStateMessage && reader.Type != EquipmentStateMessage) return;
            long started = PerfStats.Begin();
            try
            {
                if (reader.Type == InventoryStateMessage) ReadInventoryPage(reader);
                else ReadEquipment(reader);
            }
            finally
            {
                PerfStats.End("inventory.apply", started);
            }
        }

        private void ReadInventoryPage(MessageReader reader)
        {
            var page = new InventoryPage
            {
                Epoch = reader.Long(),
                InventoryId = reader.Guid(),
                Kind = (InventoryKind)reader.Byte(),
                Owner = reader.Guid(),
                Revision = reader.Int(),
                MutationGroup = reader.Guid(),
                Page = reader.Int(),
                PageCount = reader.Int(),
                TotalItems = reader.Int(),
                MaxItems = reader.Int(),
                Currency = reader.Float(),
                Supplies = reader.Int(),
                Flags = reader.Int(),
            };
            int count = reader.Int();
            if (page.Epoch <= 0 || page.InventoryId == Guid.Empty || page.Owner == Guid.Empty || page.Revision < 0
                || page.MutationGroup == Guid.Empty || page.Kind < InventoryKind.Party || page.Kind > InventoryKind.Container
                || page.Page < 0 || page.PageCount <= 0 || page.PageCount > MaxPages || page.Page >= page.PageCount
                || page.TotalItems < 0 || page.TotalItems > MaxItemsPerInventory || page.MaxItems < -1 || page.MaxItems > 1000000
                || float.IsNaN(page.Currency) || float.IsInfinity(page.Currency) || page.Currency < 0f || page.Currency > MaxCurrency
                || page.Supplies < 0 || page.Supplies > 1000000 || (page.Flags & ~1) != 0
                || count < 0 || count > ItemsPerPage) throw new FormatException("inventory page bounds");
            page.Items = new List<ItemRecord>(count);
            HashSet<Guid> ids = _scratchIds;
            ids.Clear();
            for (int i = 0; i < count; i++)
            {
                ItemRecord record = ReadItemRecord(reader);
                if (!ids.Add(record.Recipe.Id)) throw new FormatException("duplicate item on inventory page");
                page.Items.Add(record);
            }
            reader.Finish();
            if (page.Epoch != _worldEpoch)
            {
                _worldEpoch = page.Epoch;
                _assemblies.Clear();
                _pendingEquipment.Clear();
                _appliedInventories.Clear();
                _appliedEquipment.Clear();
                _worldReady = false;
            }
            if (_appliedInventories.TryGetValue(page.InventoryId, out int applied) && page.Revision < applied) return;
            if (!_assemblies.TryGetValue(page.InventoryId, out InventoryAssembly assembly)
                || assembly.Epoch != page.Epoch || assembly.Revision != page.Revision
                || assembly.MutationGroup != page.MutationGroup)
            {
                // A snapshot that replaces one still waiting for its items inherits the wait already earned.
                float delay = assembly != null ? assembly.Delay : 0f;
                assembly = new InventoryAssembly
                {
                    Epoch = page.Epoch,
                    Revision = page.Revision,
                    MutationGroup = page.MutationGroup,
                    Delay = delay,
                };
                _assemblies[page.InventoryId] = assembly;
            }
            if (assembly.Pages.ContainsKey(page.Page)) return;
            InventoryPage first = assembly.Pages.Count > 0 ? assembly.Pages[0] : page;
            if (page.PageCount != first.PageCount || page.TotalItems != first.TotalItems || page.Kind != first.Kind
                || page.Owner != first.Owner || page.MaxItems != first.MaxItems || page.Currency != first.Currency
                || page.Supplies != first.Supplies || page.Flags != first.Flags)
                throw new FormatException("inconsistent inventory mutation group");
            assembly.Pages[page.Page] = page;
            if (assembly.Pages.Count == page.PageCount && !TryApplyInventory(page.InventoryId, assembly))
                Backoff(ref assembly.Delay, ref assembly.NextAttempt, assembly.Costly, Time.unscaledTime);
        }

        /// <summary>
        /// A target that does not exist yet is cheap to look for again, so it keeps the base retry rate.
        /// Items that cannot be created cost a prefab load and a scan of every loaded item per attempt,
        /// so those retries slow down, up to MaxRetryInterval.
        /// </summary>
        private static void Backoff(ref float delay, ref float nextAttempt, bool costly, float now)
        {
            if (!costly)
            {
                nextAttempt = 0f;
                return;
            }
            delay = delay <= 0f ? RetryInterval : Math.Min(delay * 2f, MaxRetryInterval);
            nextAttempt = now + delay;
        }

        /// <summary>False when the snapshot could not be applied yet and stays pending.</summary>
        private bool TryApplyInventory(Guid id, InventoryAssembly assembly)
        {
            assembly.Costly = false;
            InventoryPage first = assembly.Pages[0];
            List<ItemRecord> records = assembly.Records;
            HashSet<Guid> seen = _scratchIds;
            if (records == null)
            {
                records = new List<ItemRecord>(first.TotalItems);
                for (int page = 0; page < first.PageCount; page++)
                {
                    if (!assembly.Pages.TryGetValue(page, out InventoryPage part)) return false;
                    records.AddRange(part.Items);
                }
                if (records.Count != first.TotalItems) throw new FormatException("inventory page item total");
                seen.Clear();
                foreach (ItemRecord record in records)
                    if (!seen.Add(record.Recipe.Id)) throw new FormatException("duplicate item in full inventory");
                assembly.Records = records;
            }

            BaseInventory inventory = ResolveInventory(id);
            if (inventory == null) return false;
            DevTools.UpdateTrace("inventory-resolved");
            // The host re-sends unchanged inventories as an integrity refresh. When the live inventory already
            // is what the snapshot describes there is nothing to repair, and rebuilding it would re-save every
            // item and reload the UI for no change.
            if (_appliedInventories.ContainsKey(id) && InventoryMatches(inventory, first, records))
            {
                PerfStats.Count("inventory.skipped");
                FinishInventory(id, first, false);
                return true;
            }
            var items = new List<KeyValuePair<Item, ItemRecord>>(records.Count);
            foreach (ItemRecord record in records)
            {
                Item item = ItemRegistry.ResolveOrCreate(record.Recipe);
                if (item == null)
                {
                    assembly.Costly = true;
                    return false;
                }
                items.Add(new KeyValuePair<Item, ItemRecord>(item, record));
            }

            DevTools.UpdateTrace("inventory-items");
            seen.Clear();
            foreach (ItemRecord record in records) seen.Add(record.Recipe.Id);
            using (ApplyScope())
            {
                foreach (InventoryItem old in inventory.ItemList)
                {
                    if (old == null || old.baseItem == null) continue;
                    Guid oldId = ItemRegistry.NetworkId(old.baseItem);
                    if (!seen.Contains(oldId)) _possiblyRemoved.Add(oldId);
                    if (old.baseItem.StoredInventory == inventory) old.baseItem.StoredInventory = null;
                }
                var replacement = new List<InventoryItem>(items.Count);
                foreach (KeyValuePair<Item, ItemRecord> pair in items)
                {
                    Item item = pair.Key;
                    ItemRecord record = pair.Value;
                    item.StoredInventory = inventory;
                    item.Location = Item.ItemLocation.Stored;
                    item.transform.parent = inventory.transform;
                    item.transform.localPosition = Vector3.zero;
                    item.transform.localRotation = Quaternion.identity;
                    if (item is Equippable equippable) equippable.RecacheTransforms();
                    inventory.persistItem(item);
                    replacement.Add(new InventoryItem(item, record.Stack)
                    {
                        uiSlot = record.Slot,
                        Original = record.Original,
                        AreaLootSource = record.AreaLootSource,
                    });
                }
                InventoryItems(inventory) = replacement;
                DevTools.UpdateTrace("inventory-replaced");
                if (inventory is PlayerInventory player)
                {
                    player.currencyTotalValue.v = first.Currency;
                    player.CampingSuppliesTotal = first.Supplies;
                }
                RefreshUi(inventory);
                DevTools.UpdateTrace("inventory-ui");
            }
            PerfStats.Count("inventory.records", records.Count);
            FinishInventory(id, first, true);
            return true;
        }

        /// <summary>Records the snapshot as applied. Nothing is announced when it changed nothing.</summary>
        private void FinishInventory(Guid id, InventoryPage first, bool changed)
        {
            _appliedInventories[id] = first.Revision;
            if (_inventories.TryGetValue(id, out TrackedInventory appliedInventory))
            {
                appliedInventory.Revision = first.Revision;
                appliedInventory.MutationGroup = first.MutationGroup;
            }
            _assemblies.Remove(id);
            if (changed) LootSync.NotifyInventorySnapshotsResolved();
        }

        /// <summary>
        /// True when the live inventory holds exactly the snapshot's entries, in order, with every field the
        /// apply path writes already in place. Any doubt answers false and the snapshot is applied in full.
        /// </summary>
        private static bool InventoryMatches(BaseInventory inventory, InventoryPage first, List<ItemRecord> records)
        {
            List<InventoryItem> live = InventoryItems(inventory);
            if (live == null || live.Count != records.Count) return false;
            if (inventory is PlayerInventory player
                && (player.currencyTotalValue.v != first.Currency || player.CampingSuppliesTotal != first.Supplies)) return false;
            Transform parent = inventory.transform;
            for (int i = 0; i < live.Count; i++)
            {
                InventoryItem entry = live[i];
                ItemRecord record = records[i];
                if (entry == null) return false;
                Item item = entry.baseItem;
                if (ReferenceEquals(item, null) || !ReferenceEquals(ItemRegistry.Known(record.Recipe.Id), item)) return false;
                if (entry.stackSize != record.Stack || entry.uiSlot != record.Slot || entry.Original != record.Original
                    || entry.AreaLootSource != record.AreaLootSource) return false;
                if (item.StoredInventory != inventory || item.Location != Item.ItemLocation.Stored
                    || item.transform.parent != parent) return false;
                if (!ItemRegistry.Matches(item, record.Recipe)) return false;
            }
            return true;
        }

        private void ReadEquipment(MessageReader reader)
        {
            var record = new EquipmentRecord
            {
                Epoch = reader.Long(),
                Actor = reader.Guid(),
                Revision = reader.Int(),
                MutationGroup = reader.Guid(),
                Selected = reader.Int(),
                Locks = reader.Int(),
            };
            int setCount = reader.Int();
            int slotCount = reader.Int();
            if (record.Epoch <= 0 || record.Actor == Guid.Empty || record.Revision < 0 || record.MutationGroup == Guid.Empty
                || record.Selected < 0 || record.Selected >= MaxWeaponSets || record.Locks < 0 || record.Locks >= (1 << MaxEquipmentSlots)
                || setCount < 0 || setCount > MaxWeaponSets || slotCount != MaxEquipmentSlots)
                throw new FormatException("equipment header");
            record.Slots = new Guid[slotCount];
            var unique = new HashSet<Guid>();
            for (int i = 0; i < slotCount; i++) record.Slots[i] = reader.Guid();
            int weaponCount = reader.Int();
            if (weaponCount != setCount * 2) throw new FormatException("equipment weapon count");
            record.Weapons = new Guid[weaponCount];
            for (int i = 0; i < weaponCount; i++) record.Weapons[i] = reader.Guid();
            foreach (Guid id in record.Slots) if (id != Guid.Empty) unique.Add(id);
            foreach (Guid id in record.Weapons) if (id != Guid.Empty) unique.Add(id);
            int recipeCount = reader.Int();
            if (recipeCount < 0 || recipeCount > MaxEquipmentSlots + MaxWeaponSets * 2) throw new FormatException("equipment recipes");
            record.Recipes = new List<ItemRegistry.Recipe>(recipeCount);
            var recipeIds = new HashSet<Guid>();
            for (int i = 0; i < recipeCount; i++)
            {
                ItemRegistry.Recipe recipe = ItemRegistry.ReadRecipe(reader);
                if (!recipeIds.Add(recipe.Id) || !unique.Contains(recipe.Id)) throw new FormatException("equipment recipe identity");
                record.Recipes.Add(recipe);
            }
            reader.Finish();
            foreach (Guid id in unique) if (!recipeIds.Contains(id)) throw new FormatException("missing equipment recipe");
            if (record.Epoch != _worldEpoch)
            {
                _worldEpoch = record.Epoch;
                _assemblies.Clear();
                _pendingEquipment.Clear();
                _appliedInventories.Clear();
                _appliedEquipment.Clear();
                _worldReady = false;
            }
            if (_appliedEquipment.TryGetValue(record.Actor, out int applied) && record.Revision <= applied) return;
            if (_pendingEquipment.TryGetValue(record.Actor, out EquipmentRecord waiting)) record.Delay = waiting.Delay;
            _pendingEquipment[record.Actor] = record;
            if (!TryApplyEquipment(record))
                Backoff(ref record.Delay, ref record.NextAttempt, record.Costly, Time.unscaledTime);
        }

        /// <summary>False when the snapshot could not be applied yet and stays pending.</summary>
        private bool TryApplyEquipment(EquipmentRecord record)
        {
            record.Costly = false;
            Equipment equipment = ResolveEquipment(record.Actor);
            if (equipment == null || CurrentSet(equipment) == null) return false;
            EquipmentSet set = CurrentSet(equipment);
            // Same reasoning as for inventories: an unchanged refresh must not detach and re-attach every
            // item, reload the UI and regenerate the character's appearance.
            if (_appliedEquipment.ContainsKey(record.Actor) && EquipmentMatches(equipment, set, record))
            {
                PerfStats.Count("inventory.skipped");
                FinishEquipment(record, false);
                return true;
            }
            var resolved = new Dictionary<Guid, Equippable>();
            foreach (ItemRegistry.Recipe recipe in record.Recipes)
            {
                Item item = ItemRegistry.ResolveOrCreate(recipe);
                Equippable equippable = item as Equippable;
                if (equippable == null)
                {
                    record.Costly = true;
                    return false;
                }
                resolved[recipe.Id] = equippable;
            }

            DevTools.UpdateTrace("equipment-resolved");
            var oldItems = new HashSet<Equippable>();
            foreach (Equippable old in set) if (old != null) oldItems.Add(old);
            if (set.Cape != null) oldItems.Add(set.Cape);
            for (int i = 0; i < MaxEquipmentSlots && i < set.Slots.Length; i++)
                if (set.Slots[i].Val != null) oldItems.Add(set.Slots[i].Val);

            using (ApplyScope())
            {
                var weapons = new WeaponSet[record.Weapons.Length / 2];
                for (int i = 0; i < weapons.Length; i++)
                {
                    weapons[i] = new WeaponSet(
                        ResolveEquipmentItem(resolved, record.Weapons[i * 2]),
                        ResolveEquipmentItem(resolved, record.Weapons[i * 2 + 1]));
                }
                set.AlternateWeaponSets = weapons;
                for (int i = 0; i < 11 && i < set.Slots.Length; i++)
                    set.Slots[i].Val = ResolveEquipmentItem(resolved, record.Slots[i]);
                set.Cape = ResolveEquipmentItem(resolved, record.Slots[(int)Equippable.EquipmentSlot.Cape_DEPRECATED]);
                set.SelectedWeaponSet = record.Selected;
                WeaponSet selected = set.GetSelectedWeaponSet();
                set.PrimaryWeapon = selected != null ? selected.PrimaryWeapon : null;
                set.SecondaryWeapon = selected != null ? selected.SecondaryWeapon : null;
                CachedPrimary(equipment) = null;
                CachedSecondary(equipment) = null;
                bool[] locks = SlotLocks(equipment);
                if (locks == null || locks.Length < MaxEquipmentSlots)
                {
                    locks = new bool[MaxEquipmentSlots];
                    SlotLocks(equipment) = locks;
                }
                for (int i = 0; i < MaxEquipmentSlots; i++) locks[i] = (record.Locks & (1 << i)) != 0;

                DevTools.UpdateTrace("equipment-set");
                var newItems = new HashSet<Equippable>(resolved.Values);
                foreach (Equippable old in oldItems)
                    if (old != null && !newItems.Contains(old)) ClearVisual(equipment, old);
                for (int i = 0; i < 11 && i < set.Slots.Length; i++)
                    if (set.Slots[i].Val != null) PlaceVisual(equipment, set.Slots[i].Val, (Equippable.EquipmentSlot)i);
                if (set.Cape != null) PlaceVisual(equipment, set.Cape, Equippable.EquipmentSlot.Cape_DEPRECATED);
                for (int i = 0; i < weapons.Length; i++)
                {
                    if (weapons[i].PrimaryWeapon != null)
                        PlaceVisual(equipment, weapons[i].PrimaryWeapon,
                            i == record.Selected ? Equippable.EquipmentSlot.PrimaryWeapon : Equippable.EquipmentSlot.PrimaryWeapon2);
                    if (weapons[i].SecondaryWeapon != null)
                        PlaceVisual(equipment, weapons[i].SecondaryWeapon,
                            i == record.Selected ? Equippable.EquipmentSlot.SecondaryWeapon : Equippable.EquipmentSlot.SecondaryWeapon2);
                }
                DevTools.UpdateTrace("equipment-visuals");
                NPCAppearance appearance = equipment.GetComponent<NPCAppearance>();
                if (appearance != null) _appearanceAt[appearance] = Time.unscaledTime + 1f;
                DevTools.UpdateTrace("equipment-appearance");
                RefreshUi(equipment);
                DevTools.UpdateTrace("equipment-ui");
            }
            PerfStats.Count("inventory.records", record.Recipes.Count);
            FinishEquipment(record, true);
            return true;
        }

        private void FinishEquipment(EquipmentRecord record, bool changed)
        {
            _appliedEquipment[record.Actor] = record.Revision;
            if (_equipment.TryGetValue(record.Actor, out TrackedEquipment appliedEquipment))
            {
                appliedEquipment.Revision = record.Revision;
                appliedEquipment.MutationGroup = record.MutationGroup;
            }
            // A newer snapshot may already have replaced this one while it waited.
            if (_pendingEquipment.TryGetValue(record.Actor, out EquipmentRecord pending) && pending == record)
                _pendingEquipment.Remove(record.Actor);
            if (changed) LootSync.NotifyInventorySnapshotsResolved();
        }

        /// <summary>
        /// True when the live equipment set, its weapon sets, slot locks, item state and the visual
        /// bookkeeping PlaceVisual writes all equal what applying this snapshot would leave behind.
        /// </summary>
        private bool EquipmentMatches(Equipment equipment, EquipmentSet set, EquipmentRecord record)
        {
            if (set.SelectedWeaponSet != record.Selected || set.Slots == null) return false;
            bool[] locks = SlotLocks(equipment);
            if (locks == null || locks.Length < MaxEquipmentSlots) return false;
            for (int i = 0; i < MaxEquipmentSlots; i++)
                if (locks[i] != ((record.Locks & (1 << i)) != 0)) return false;
            WeaponSet[] weapons = set.AlternateWeaponSets;
            if (weapons == null || weapons.Length != record.Weapons.Length / 2) return false;
            foreach (ItemRegistry.Recipe recipe in record.Recipes)
            {
                Item item = ItemRegistry.Known(recipe.Id);
                if (!(item is Equippable) || !ItemRegistry.Matches(item, recipe)) return false;
            }

            // Expected slots are collected in the order the apply path places visuals, so the last one wins.
            Dictionary<Equippable, Equippable.EquipmentSlot> expected = _expectedSlots;
            expected.Clear();
            for (int i = 0; i < 11 && i < set.Slots.Length; i++)
            {
                Equippable item = KnownEquippable(record.Slots[i]);
                if (!ReferenceEquals(set.Slots[i].Val, item)) return false;
                if (item != null) expected[item] = (Equippable.EquipmentSlot)i;
            }
            Equippable cape = KnownEquippable(record.Slots[(int)Equippable.EquipmentSlot.Cape_DEPRECATED]);
            if (!ReferenceEquals(set.Cape, cape)) return false;
            if (cape != null) expected[cape] = Equippable.EquipmentSlot.Cape_DEPRECATED;
            for (int i = 0; i < weapons.Length; i++)
            {
                if (weapons[i] == null) return false;
                Equippable primary = KnownEquippable(record.Weapons[i * 2]);
                Equippable secondary = KnownEquippable(record.Weapons[i * 2 + 1]);
                if (!ReferenceEquals(weapons[i].PrimaryWeapon, primary)
                    || !ReferenceEquals(weapons[i].SecondaryWeapon, secondary)) return false;
                if (primary != null)
                    expected[primary] = i == record.Selected
                        ? Equippable.EquipmentSlot.PrimaryWeapon : Equippable.EquipmentSlot.PrimaryWeapon2;
                if (secondary != null)
                    expected[secondary] = i == record.Selected
                        ? Equippable.EquipmentSlot.SecondaryWeapon : Equippable.EquipmentSlot.SecondaryWeapon2;
            }
            WeaponSet selected = set.GetSelectedWeaponSet();
            if (!ReferenceEquals(set.PrimaryWeapon, selected != null ? selected.PrimaryWeapon : null)
                || !ReferenceEquals(set.SecondaryWeapon, selected != null ? selected.SecondaryWeapon : null)) return false;

            GameObject owner = equipment.gameObject;
            bool placed = true;
            foreach (KeyValuePair<Equippable, Equippable.EquipmentSlot> pair in expected)
            {
                Equippable item = pair.Key;
                if (ItemOwner(item) == owner && ItemEquipment(item) == equipment && ItemSlot(item) == pair.Value
                    && item.Location == Item.ItemLocation.Equipped && item.gameObject.activeSelf) continue;
                placed = false;
                break;
            }
            expected.Clear();
            return placed;
        }

        private static Equippable KnownEquippable(Guid id)
        {
            return ItemRegistry.Known(id) as Equippable;
        }

        private static Equippable ResolveEquipmentItem(Dictionary<Guid, Equippable> items, Guid id)
        {
            return id != Guid.Empty && items.TryGetValue(id, out Equippable item) ? item : null;
        }

        private static void PlaceVisual(Equipment equipment, Equippable item, Equippable.EquipmentSlot slot)
        {
            GameObject owner = equipment.gameObject;
            ItemOwner(item) = owner;
            ItemEquipment(item) = equipment;
            ItemSlot(item) = slot;
            item.Location = Item.ItemLocation.Equipped;
            item.AttachToSlot(owner, slot);
            item.gameObject.SetActive(true);
        }

        private static void ClearVisual(Equipment equipment, Equippable item)
        {
            AnimationBoneMapper mapper = equipment.GetComponent<AnimationBoneMapper>();
            if (mapper != null) mapper.ClearMapping(item.gameObject);
            if (item.GrimoireSlot)
            {
                AnimationController animation = equipment.GetComponent<AnimationController>();
                Animator animator = item.GetComponent<Animator>();
                if (animation != null && animator != null) animation.SyncList.Remove(animator);
            }
            ItemOwner(item) = null;
            ItemEquipment(item) = null;
            ItemSlot(item) = Equippable.EquipmentSlot.None;
            item.Renders = false;
            BaseInventory inventory = item.StoredInventory;
            item.transform.parent = inventory != null ? inventory.transform : equipment.transform;
            item.transform.localPosition = Vector3.zero;
            item.transform.localRotation = Quaternion.identity;
            if (inventory != null) item.Location = Item.ItemLocation.Stored;
        }

        private void RetryPending(float now)
        {
            if (_assemblies.Count > 0)
            {
                List<KeyValuePair<Guid, InventoryAssembly>> assemblies = _scratchAssemblies;
                assemblies.Clear();
                foreach (KeyValuePair<Guid, InventoryAssembly> pair in _assemblies) assemblies.Add(pair);
                foreach (KeyValuePair<Guid, InventoryAssembly> pair in assemblies)
                {
                    InventoryAssembly assembly = pair.Value;
                    if (now < assembly.NextAttempt || !assembly.Pages.TryGetValue(0, out InventoryPage first)
                        || assembly.Pages.Count != first.PageCount) continue;
                    if (!TryApplyInventory(pair.Key, assembly))
                        Backoff(ref assembly.Delay, ref assembly.NextAttempt, assembly.Costly, now);
                }
                assemblies.Clear();
            }
            if (_pendingEquipment.Count > 0)
            {
                List<EquipmentRecord> pending = _scratchEquipment;
                pending.Clear();
                foreach (EquipmentRecord record in _pendingEquipment.Values) pending.Add(record);
                foreach (EquipmentRecord record in pending)
                {
                    if (now < record.NextAttempt) continue;
                    if (!TryApplyEquipment(record))
                        Backoff(ref record.Delay, ref record.NextAttempt, record.Costly, now);
                }
                pending.Clear();
            }
        }

        private void UpdateAppearance(float now)
        {
            if (_appearanceAt.Count == 0) return;
            List<NPCAppearance> due = null;
            foreach (var pair in _appearanceAt)
                if (pair.Value <= now) (due ?? (due = new List<NPCAppearance>())).Add(pair.Key);
            if (due == null) return;
            foreach (NPCAppearance appearance in due)
            {
                _appearanceAt.Remove(appearance);
                if (appearance != null && !appearance.IsCreatingAppearance) appearance.Generate();
            }
        }

        private void CleanupRemoved()
        {
            _nextCleanup = Time.unscaledTime + 0.5f;
            var ids = new List<Guid>(_possiblyRemoved);
            _possiblyRemoved.Clear();
            foreach (Guid id in ids)
            {
                Item item = ItemRegistry.Resolve(id);
                if (item == null || IsReferenced(item)) continue;
                using (ApplyScope())
                {
                    Persistence persistence = item.GetComponent<Persistence>();
                    if (persistence != null) PersistenceManager.RemoveObject(persistence);
                    GameUtilities.Destroy(item.gameObject);
                }
            }
        }

        private static bool IsReferenced(Item item)
        {
            foreach (PartyMemberAI member in PartyMemberAI.OnlyPrimaryPartyMembers)
            {
                if (member == null) continue;
                if (Contains(member.Inventory, item) || Contains(member.QuickbarInventory, item)) return true;
                Equipment equipment = member.GetComponent<Equipment>();
                if (equipment != null && CurrentSet(equipment) != null)
                    foreach (Equippable equippable in CurrentSet(equipment)) if (equippable == item) return true;
            }
            PlayerInventory player = GameState.PlayerInventory;
            if (player != null && (Contains(player, item) || Contains(player.StashInventory, item)
                || Contains(player.QuestInventory, item) || Contains(player.CraftingInventory, item))) return true;
            InventorySync self = s_instance;
            if (self != null)
                foreach (TrackedInventory tracked in self._inventories.Values)
                    if (tracked.Kind == InventoryKind.Container && Contains(tracked.Inventory, item)) return true;
            return false;
        }

        private static bool Contains(BaseInventory inventory, Item item)
        {
            if (inventory == null) return false;
            foreach (InventoryItem entry in inventory.ItemList) if (entry != null && entry.baseItem == item) return true;
            return false;
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            if (!_session.IsHost) return;
            _nextSend = 0f;
            _forceFull = true;
            foreach (TrackedInventory tracked in _inventories.Values) tracked.Dirty = true;
            foreach (TrackedEquipment tracked in _equipment.Values) tracked.Dirty = true;
        }

        private void OnLevelChanged(object sender, EventArgs e)
        {
            _assemblies.Clear();
            _pendingEquipment.Clear();
            _appliedInventories.Clear();
            _appliedEquipment.Clear();
            _appearanceAt.Clear();
            _possiblyRemoved.Clear();
            _uiInventories.Clear();
            _nextSend = 0f;
            _forceFull = true;
            _nextRetry = 0f;
            _nextDiscover = 0f;
            if (_session.IsGuest) _worldReady = false;
            // Inventories destroyed with the previous level would otherwise be walked on every send forever.
            List<Guid> dead = _scratchDead;
            dead.Clear();
            foreach (KeyValuePair<Guid, TrackedInventory> pair in _inventories)
                if (pair.Value.Inventory == null) dead.Add(pair.Key);
            foreach (Guid id in dead) _inventories.Remove(id);
            dead.Clear();
            foreach (KeyValuePair<Guid, TrackedEquipment> pair in _equipment)
                if (pair.Value.Equipment == null) dead.Add(pair.Key);
            foreach (Guid id in dead) _equipment.Remove(id);
            dead.Clear();
        }

        private void MarkDirty(TrackedInventory tracked)
        {
            if (tracked == null) return;
            if (!tracked.Dirty) tracked.Revision = Next(tracked.Revision);
            else if (tracked.MutationGroup == Guid.Empty) tracked.Revision = Next(tracked.Revision);
            tracked.MutationGroup = _mutationDepth > 0 ? _activeMutation : Guid.NewGuid();
            if (_mutationDepth == 0) _stateSequence = Next(_stateSequence);
            tracked.Dirty = true;
            _nextSend = 0f;
        }

        private void MarkDirty(TrackedEquipment tracked)
        {
            if (tracked == null) return;
            if (!tracked.Dirty) tracked.Revision = Next(tracked.Revision);
            else if (tracked.MutationGroup == Guid.Empty) tracked.Revision = Next(tracked.Revision);
            tracked.MutationGroup = _mutationDepth > 0 ? _activeMutation : Guid.NewGuid();
            if (_mutationDepth == 0) _stateSequence = Next(_stateSequence);
            tracked.Dirty = true;
            _nextSend = 0f;
        }

        private void Touch(TrackedInventory tracked)
        {
            tracked.Revision = Next(tracked.Revision);
            tracked.MutationGroup = Guid.NewGuid();
            tracked.Dirty = true;
        }

        private void Touch(TrackedEquipment tracked)
        {
            tracked.Revision = Next(tracked.Revision);
            tracked.MutationGroup = Guid.NewGuid();
            tracked.Dirty = true;
        }

        private static int Next(int value) => value == int.MaxValue ? 1 : value + 1;
        private static long Next(long value) => value == long.MaxValue ? 1 : value + 1;

        private static Guid DeriveInventoryId(Guid actor, InventoryKind kind)
        {
            byte[] bytes = actor.ToByteArray();
            bytes[15] ^= (byte)kind;
            return new Guid(bytes);
        }

        private static InventoryKind KindOf(BaseInventory inventory)
        {
            if (inventory.GetComponent<Container>() != null) return InventoryKind.Container;
            if (inventory is QuickbarInventory) return InventoryKind.Quickbar;
            if (inventory is StashInventory) return InventoryKind.Stash;
            if (inventory is QuestInventory) return InventoryKind.Quest;
            if (inventory is CraftingInventory) return InventoryKind.Crafting;
            return InventoryKind.Party;
        }

        private static int BoundedMaxItems(int maxItems) => maxItems < 0 || maxItems == int.MaxValue ? -1 : Math.Min(maxItems, 1000000);

        private static void CaptureItems(BaseInventory inventory, List<ItemRecord> records)
        {
            records.Clear();
            foreach (InventoryItem item in inventory.ItemList)
            {
                if (item == null || item.baseItem == null || item.stackSize <= 0 || records.Count >= MaxItemsPerInventory) continue;
                ItemRegistry.Recipe recipe = ItemRegistry.Capture(item.baseItem);
                if (recipe == null) continue;
                records.Add(new ItemRecord
                {
                    Recipe = recipe,
                    Stack = item.stackSize,
                    Slot = item.uiSlot,
                    Original = item.Original,
                    AreaLootSource = item.AreaLootSource,
                });
            }
        }

        private static Guid ItemId(Item item) => item != null ? ItemRegistry.NetworkId(item) : Guid.Empty;

        private static void AddRecipe(Guid id, HashSet<Guid> seen, List<ItemRegistry.Recipe> recipes)
        {
            if (id == Guid.Empty || !seen.Add(id)) return;
            Item item = ItemRegistry.Resolve(id);
            ItemRegistry.Recipe recipe = item != null ? ItemRegistry.Capture(item) : null;
            if (recipe != null) recipes.Add(recipe);
        }

        /// <summary>
        /// Change detector for state that raises no inventory event (charges, slots, stacks, soulbind
        /// progress). Covers what a snapshot carries, in order, without building one.
        /// </summary>
        private static ulong InventoryHash(BaseInventory inventory)
        {
            ulong hash = HashSeed;
            PlayerInventory player = inventory as PlayerInventory;
            if (player != null)
                hash = ItemRegistry.Mix(ItemRegistry.Mix(hash, player.currencyTotalValue.v), (ulong)player.CampingSuppliesTotal);
            if (inventory is QuestInventory quest) hash = ItemRegistry.Mix(hash, quest.HasNew ? 1UL : 2UL);
            List<InventoryItem> items = inventory.ItemList;
            int count = 0;
            for (int i = 0; i < items.Count; i++)
            {
                InventoryItem item = items[i];
                if (item == null || item.baseItem == null || item.stackSize <= 0 || count >= MaxItemsPerInventory) continue;
                if (!ItemRegistry.MixState(item.baseItem, ref hash)) continue;
                count++;
                hash = ItemRegistry.Mix(ItemRegistry.Mix(hash, (ulong)item.stackSize), (ulong)item.uiSlot);
                hash = ItemRegistry.Mix(hash, (item.Original ? 1UL : 0UL) | (item.AreaLootSource ? 2UL : 0UL));
            }
            return ItemRegistry.Mix(hash, (ulong)count);
        }

        private static ulong EquipmentHash(Equipment equipment)
        {
            EquipmentSet set = CurrentSet(equipment);
            if (set == null) return 0UL;
            ulong hash = ItemRegistry.Mix(HashSeed, (ulong)set.SelectedWeaponSet);
            bool[] locks = SlotLocks(equipment);
            if (locks != null)
                for (int i = 0; i < locks.Length; i++) hash = ItemRegistry.Mix(hash, locks[i] ? 1UL : 0UL);
            for (int i = 0; i < MaxEquipmentSlots; i++)
                hash = MixEquipped(hash, i == (int)Equippable.EquipmentSlot.Cape_DEPRECATED
                    ? set.Cape : set.GetItemInSlot((Equippable.EquipmentSlot)i));
            WeaponSet[] weapons = set.AlternateWeaponSets;
            if (weapons != null)
                for (int i = 0; i < weapons.Length; i++)
                {
                    hash = MixEquipped(hash, weapons[i] != null ? weapons[i].PrimaryWeapon : null);
                    hash = MixEquipped(hash, weapons[i] != null ? weapons[i].SecondaryWeapon : null);
                }
            return hash;
        }

        private static ulong MixEquipped(ulong hash, Equippable item)
        {
            if (item == null) return ItemRegistry.Mix(hash, 0UL);
            return ItemRegistry.MixState(item, ref hash) ? ItemRegistry.Mix(hash, 1UL) : ItemRegistry.Mix(hash, 2UL);
        }

        private void RefreshUi(BaseInventory inventory)
        {
            // Reloaded once per frame by FlushUi, however many inventories that frame applied.
            _uiInventories.Add(inventory);
        }

        private void FlushUi()
        {
            if (_uiInventories.Count == 0) return;
            using (ApplyScope())
            {
                foreach (UIInventoryItemGrid grid in UnityEngine.Object.FindObjectsOfType<UIInventoryItemGrid>())
                {
                    if (grid == null) continue;
                    BaseInventory inventory = grid.Inventory;
                    if (inventory != null && _uiInventories.Contains(inventory)) grid.Reload();
                }
                _uiInventories.Clear();
                if (UIAbilityBar.Instance != null)
                {
                    UIAbilityBar.Instance.RefreshQuickItems();
                    UIAbilityBar.Instance.RefreshAbilities();
                }
            }
        }

        private static void RefreshUi(Equipment equipment)
        {
            UIInventoryManager manager = UIInventoryManager.Instance;
            if (manager != null)
            {
                if (manager.Equipment != null && manager.Equipment.SelectedEquipment == equipment) manager.Equipment.Reload();
                if (manager.WindowActive() && manager.WeaponSets != null && manager.WeaponSets.SubSets != null
                    && SelectedWeaponEquipment(manager.WeaponSets) == equipment)
                    manager.WeaponSets.ReloadInventory();
                manager.ReloadStats();
                manager.ReloadPaperdoll();
            }
            if (UIAbilityBar.Instance != null)
            {
                UIAbilityBar.Instance.RefreshWeaponSets();
                UIAbilityBar.Instance.RefreshAbilities();
            }
        }
    }
}
