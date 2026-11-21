using System;
using System.Collections.Generic;
using System.Linq;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    public sealed class LootSync : IDisposable
    {
        private enum CommandKind : byte
        {
            Close = 1,
            Take = 2,
            TakeAll = 3,
            Resync = 4,
        }

        private sealed class SharedLoot
        {
            public Guid RootId;
            public Container Root;
            public readonly List<Container> Containers = new List<Container>();
            public readonly HashSet<int> SessionIds = new HashSet<int>();
            public bool HostViewer;
            public bool Area;
        }

        private sealed class GuestSession
        {
            public int Id;
            public int PlayerId;
            public Guid OpenerId;
            public int Revision;
            public SharedLoot Group;
        }

        public const FlowDomain Domain = FlowDomain.Loot;
        private const int MaxContainers = 64;

        private static readonly HarmonyLib.AccessTools.FieldRef<Container, GameObject> ContainerUser =
            HarmonyLib.AccessTools.FieldRefAccess<Container, GameObject>("m_user");

        private static LootSync s_instance;
        internal static LootSync Instance => s_instance;

        private readonly Session _session;
        private readonly Ownership _ownership;
        private readonly Dictionary<Guid, SharedLoot> _groups = new Dictionary<Guid, SharedLoot>();
        private readonly Dictionary<int, GuestSession> _sessions = new Dictionary<int, GuestSession>();
        private readonly Dictionary<int, GuestSession> _sessionByPlayer = new Dictionary<int, GuestSession>();
        private readonly RequestLedger _requests = new RequestLedger();
        private readonly Dictionary<int, long> _sequences = new Dictionary<int, long>();
        private readonly List<Guid> _guestContainerIds = new List<Guid>();

        private long _generation;
        private long _observedEpoch;
        private int _nextSessionId;
        private long _nextRequestId;
        private bool _remoteOpening;
        private int _nativeCloseDepth;

        private long _guestSequence;
        private int _guestSession;
        private int _guestRevision;
        private Guid _guestRootId;
        private Guid _guestOpenerId;
        private bool _guestOpen;
        private bool _guestUiDirty;
        private bool _guestClosePending;
        private bool _guestApplying;
        private int _guestShownSession;
        private int _guestShownRevision;
        private CombinedInventory _guestCombined;
        private UIInventoryGridItem _guestClickItem;
        private float _guestClickDue;

        public LootSync(Session session, Ownership ownership)
        {
            _session = session;
            _ownership = ownership;
            _generation = session.Generation;
            _observedEpoch = FlowCoordinator.WorldEpoch;
            s_instance = this;
            session.GameMessage += OnGameMessage;
            session.PlayerJoined += OnPlayerJoined;
            session.PlayerLeft += OnPlayerLeft;
            session.WorldStateReady += OnWorldStateReady;
            session.FullStateRequested += OnFullStateRequested;
            session.SessionReset += OnSessionReset;
            GameState.OnLevelLoaded += OnLevelChanged;
            GameState.OnLevelUnload += OnLevelChanged;
        }

        public long WorldEpoch => FlowCoordinator.WorldEpoch;

        public static void NotifyInventorySnapshotsResolved()
        {
            LootSync self = s_instance;
            if (self != null && self._session.IsGuest) self._guestUiDirty = true;
        }

        public void Dispose()
        {
            _session.GameMessage -= OnGameMessage;
            _session.PlayerJoined -= OnPlayerJoined;
            _session.PlayerLeft -= OnPlayerLeft;
            _session.WorldStateReady -= OnWorldStateReady;
            _session.FullStateRequested -= OnFullStateRequested;
            _session.SessionReset -= OnSessionReset;
            GameState.OnLevelLoaded -= OnLevelChanged;
            GameState.OnLevelUnload -= OnLevelChanged;
            ResetWorld();
            if (s_instance == this) s_instance = null;
        }

        public void Update(float now)
        {
            SyncContext();
            if (_session.IsHost) UpdateHost();
            else if (_session.IsGuest) UpdateGuest();
        }

        // The FlowCoordinator removes the common envelope and calls this only for domain Loot.
        public void HandleFlowCommand(PlayerInfo sender, long worldEpoch, long requestId, int kindValue, MessageReader reader)
        {
            if (!_session.IsHost || sender == null) throw new FormatException("loot command sender");
            SyncContext();
            CommandKind kind = (CommandKind)kindValue;
            if (kind < CommandKind.Close || kind > CommandKind.Resync)
                throw new FormatException("loot command kind " + kindValue);
            var command = new FlowCommandEnvelope
            {
                WorldEpoch = worldEpoch,
                RequestId = requestId,
                Domain = Domain,
                Kind = kindValue,
            };
            if (worldEpoch <= 0 || requestId <= 0) throw new FormatException("loot command envelope");

            RequestLookup lookup = _requests.Begin(sender.Id, command, reader);
            if (lookup.Status == RequestLookupStatus.Conflict)
            {
                SendCurrentState(sender.Id);
                SendResult(sender, command, FlowResultCode.Conflict, CurrentSequence(sender.Id));
                return;
            }
            if (lookup.Status == RequestLookupStatus.Duplicate)
            {
                SendCurrentState(sender.Id);
                if (lookup.HasResult) SendResult(sender, command, lookup.Result, lookup.StateSeq);
                return;
            }

            int sessionId = 0;
            int expectedRevision = 0;
            Guid sourceContainerId = Guid.Empty;
            Guid itemId = Guid.Empty;
            Guid destinationId = Guid.Empty;
            int quantity = 0;
            switch (kind)
            {
                case CommandKind.Close:
                    sessionId = reader.Int();
                    expectedRevision = reader.Int();
                    break;
                case CommandKind.Take:
                    sessionId = reader.Int();
                    expectedRevision = reader.Int();
                    sourceContainerId = reader.Guid();
                    itemId = reader.Guid();
                    quantity = reader.Int();
                    destinationId = reader.Guid();
                    break;
                case CommandKind.TakeAll:
                    sessionId = reader.Int();
                    expectedRevision = reader.Int();
                    destinationId = reader.Guid();
                    break;
                case CommandKind.Resync:
                    break;
            }
            reader.Finish();

            if (worldEpoch != WorldEpoch)
            {
                Complete(sender, command, FlowResultCode.StaleWorld, true);
                return;
            }
            if (kind == CommandKind.Resync)
            {
                Complete(sender, command, FlowResultCode.Ok, true);
                return;
            }
            if (!WorldReady() || !_session.IsPeerReady(sender.Id))
            {
                Complete(sender, command, FlowResultCode.NotReady, false);
                return;
            }
            if (!_sessions.TryGetValue(sessionId, out GuestSession session)
                || session.PlayerId != sender.Id || !_sessionByPlayer.TryGetValue(sender.Id, out GuestSession current)
                || current != session || session.Group == null)
            {
                Complete(sender, command, FlowResultCode.Conflict, true);
                return;
            }
            PartyMemberAI opener = Ownership.Find(session.OpenerId);
            if (opener == null || _ownership.OwnerOf(opener) != sender.Id)
            {
                Complete(sender, command, FlowResultCode.Rejected, true);
                return;
            }
            if (expectedRevision != session.Revision)
            {
                Complete(sender, command, FlowResultCode.Conflict, true);
                return;
            }

            switch (kind)
            {
                case CommandKind.Close:
                    CloseSession(session, true);
                    Complete(sender, command, FlowResultCode.Ok, false);
                    break;
                case CommandKind.Take:
                    Take(sender, command, session, opener, sourceContainerId, itemId, quantity, destinationId);
                    break;
                case CommandKind.TakeAll:
                    TakeAll(sender, command, session, opener, destinationId);
                    break;
            }
        }

        public void HandleFlowResult(long worldEpoch, long requestId, int kindValue, FlowResultCode result, long stateSeq)
        {
            SyncContext();
            if (kindValue < (int)CommandKind.Close || kindValue > (int)CommandKind.Resync || stateSeq < 0)
                throw new FormatException("invalid loot flow result");
            if (!_session.IsGuest || worldEpoch != WorldEpoch) return;
            CommandKind kind = (CommandKind)kindValue;
            if (kind == CommandKind.Close && result != FlowResultCode.Ok) _guestClosePending = false;
            if (result == FlowResultCode.Ok) return;
            if (result == FlowResultCode.StaleWorld || result == FlowResultCode.Conflict) _guestUiDirty = true;
            _session.Note(result == FlowResultCode.StaleWorld
                ? "The shared world changed; the loot request was not applied."
                : result == FlowResultCode.Conflict
                    ? "The container changed; applying the host's latest state."
                    : "The host did not accept that loot request.");
        }

        private void Take(PlayerInfo sender, FlowCommandEnvelope command,
            GuestSession session, PartyMemberAI opener, Guid sourceContainerId, Guid itemId,
            int quantity, Guid destinationId)
        {
            BaseInventory destination;
            if (!ResolveDestination(sender.Id, destinationId, out destination))
            {
                Complete(sender, command, FlowResultCode.Rejected, true);
                return;
            }
            if (GameState.InCombat || !HasNativeAccess())
            {
                Complete(sender, command, FlowResultCode.Invalid, true);
                return;
            }
            Container source = ResolveGroupContainer(session.Group, sourceContainerId);
            Item item = ItemRegistry.Resolve(itemId);
            if (source == null || item == null || quantity <= 0)
            {
                Complete(sender, command, FlowResultCode.Invalid, true);
                return;
            }
            InventoryItem inventoryItem = FindSourceItem(source, item);
            if (inventoryItem == null || quantity > inventoryItem.stackSize)
            {
                Complete(sender, command, FlowResultCode.Invalid, true);
                return;
            }

            int moved = 0;
            InventorySync.BeginMutation();
            try { moved = TransferAddFirst(source, inventoryItem, quantity, destination, opener); }
            finally { InventorySync.EndMutation(moved > 0); }
            if (moved > 0)
            {
                InventorySync.MarkChanged(source.GetComponent<Inventory>());
                InventorySync.MarkChanged(destination);
            }
            if (moved <= 0)
            {
                Complete(sender, command, FlowResultCode.Failed, true);
                return;
            }
            MarkGroupChanged(session.Group);
            CloseEmptyGuestSessions(session.Group);
            Complete(sender, command, FlowResultCode.Ok, false);
        }

        private void TakeAll(PlayerInfo sender, FlowCommandEnvelope command,
            GuestSession session, PartyMemberAI opener, Guid destinationId)
        {
            BaseInventory destination;
            if (!ResolveDestination(sender.Id, destinationId, out destination))
            {
                Complete(sender, command, FlowResultCode.Rejected, true);
                return;
            }
            if (GameState.InCombat || !HasNativeAccess())
            {
                Complete(sender, command, FlowResultCode.Invalid, true);
                return;
            }

            int moved = 0;
            InventorySync.BeginMutation();
            try
            {
                foreach (Container container in session.Group.Containers.ToArray())
                {
                    if (container == null) continue;
                    Inventory inventory = container.GetComponent<Inventory>();
                    if (inventory == null) continue;
                    foreach (InventoryItem item in inventory.ItemList.ToArray())
                    {
                        if (item == null || item.baseItem == null || item.stackSize <= 0) continue;
                        moved += TransferAddFirst(container, item, item.stackSize, destination, opener);
                    }
                }
            }
            finally { InventorySync.EndMutation(moved > 0); }
            if (moved > 0)
            {
                foreach (Container container in session.Group.Containers)
                    if (container != null) InventorySync.MarkChanged(container.GetComponent<Inventory>());
                InventorySync.MarkChanged(destination);
            }
            if (moved <= 0)
            {
                if (GroupEmpty(session.Group))
                {
                    CloseEmptyGuestSessions(session.Group);
                    Complete(sender, command, FlowResultCode.Ok, false);
                }
                else Complete(sender, command, FlowResultCode.Failed, true);
                return;
            }
            MarkGroupChanged(session.Group);
            CloseEmptyGuestSessions(session.Group);
            Complete(sender, command, FlowResultCode.Ok, false);
        }

        private static int TransferAddFirst(Container source, InventoryItem inventoryItem,
            int quantity, BaseInventory destination, PartyMemberAI opener)
        {
            Item sourceItem = inventoryItem.baseItem;
            if (sourceItem == null || quantity <= 0 || quantity > inventoryItem.stackSize) return 0;
            ContainerUser(source) = opener != null ? opener.gameObject : null;

            Item itemToAdd = sourceItem.MaxStackSize > 1 ? sourceItem.Prefab : sourceItem;
            int remaining = destination.AddItem(itemToAdd, quantity);
            if (remaining > 0 && destination.IsPartyMember && GameState.PlayerInventory != null)
                remaining = GameState.PlayerInventory.AddItem(sourceItem, remaining, -1);
            int moved = quantity - remaining;
            if (moved <= 0) return 0;

            if (moved < inventoryItem.stackSize)
            {
                inventoryItem.SetStackSize(inventoryItem.stackSize - moved);
                return moved;
            }

            Inventory inventory = source.GetComponent<Inventory>();
            if (inventory != null && inventory.ItemList.Contains(inventoryItem)) inventory.TakeItem(inventoryItem);
            if (sourceItem.MaxStackSize > 1 && sourceItem != null)
            {
                Persistence persistence = sourceItem.GetComponent<Persistence>();
                if (persistence != null) PersistenceManager.RemoveObject(persistence);
                GameUtilities.Destroy(sourceItem.gameObject);
            }
            return moved;
        }

        private static InventoryItem FindSourceItem(Container source, Item item)
        {
            Inventory inventory = source.GetComponent<Inventory>();
            if (inventory == null) return null;
            foreach (InventoryItem candidate in inventory.ItemList)
                if (candidate != null && candidate.baseItem == item) return candidate;
            return null;
        }

        private bool ResolveDestination(int playerId, Guid destinationId, out BaseInventory destination)
        {
            if (destinationId == Guid.Empty)
            {
                destination = GameState.PlayerStashInventory;
                return destination != null;
            }
            PartyMemberAI member = Ownership.Find(destinationId);
            if (member == null || _ownership.OwnerOf(member) != playerId || !member.IsActiveInParty)
            {
                destination = null;
                return false;
            }
            destination = member.Inventory;
            return destination != null;
        }

        private static bool HasNativeAccess()
        {
            UILootManager manager = UILootManager.Instance;
            if (manager == null || manager.ItemGrid == null) return true;
            return UIInventoryItemZone.GetCurrentAccessLevel() >= manager.ItemGrid.RestrictExternalTake;
        }

        // ---- host capture and viewer lifetime ----

        internal static bool TryOpenExisting(Container container, GameObject user)
        {
            LootSync self = s_instance;
            if (self == null || !self._session.IsHost || container == null || user == null
                || container.CurrentState != OCL.State.Open && container.CurrentState != OCL.State.SealedOpen)
                return false;
            PartyMemberAI member = user.GetComponent<PartyMemberAI>();
            if (member == null) return false;
            Guid rootId = EntityRegistry.NetworkId(container.gameObject);
            if (rootId == Guid.Empty || !self._groups.TryGetValue(rootId, out SharedLoot group)) return false;
            int playerId = self._ownership.OwnerOf(member);
            if (playerId == Session.HostId)
            {
                UILootManager manager = UILootManager.Instance;
                if (manager == null || manager.ItemGrid == null) return false;
                Inventory display = container.GetComponent<Inventory>();
                if (display == null) return false;
                if (group.Containers.Count > 1)
                {
                    CombinedInventory combined = manager.gameObject.AddComponent<CombinedInventory>();
                    combined.Initialize(container.ManualLabelName ?? "",
                        group.Containers.Where(c => c != null).Select(c => c.GetComponent<Inventory>())
                            .Where(inventory => inventory != null).ToArray());
                    combined.CloseInventoryCB = container.CloseInventoryAreaLoot;
                    display = combined;
                }
                else display.CloseInventoryCB = container.CloseInventoryCB;
                manager.SetData(member, display, container.gameObject);
                manager.ShowWindow();
                group.HostViewer = true;
                return true;
            }
            if (!self._session.Players.Exists(player => player.Id == playerId)) return false;
            self._remoteOpening = true;
            self.CaptureRemoteOpen(container, member, group.Containers.ToList());
            return true;
        }

        internal static bool BeginContainerOpen(Container container, GameObject user)
        {
            LootSync self = s_instance;
            return self != null && self.BeginRemoteOpen(container, user);
        }

        internal static void EndContainerOpen(Container container, GameObject user, bool opened)
        {
            LootSync self = s_instance;
            if (self == null) return;
            bool remote = self._remoteOpening;
            self._remoteOpening = false;
            if (opened && self._session.IsHost && !remote) self.CaptureHostOpen(container, user);
        }

        internal static bool RemoteContainerOpening => s_instance != null && s_instance._remoteOpening;

        private bool BeginRemoteOpen(Container container, GameObject user)
        {
            if (!_session.IsHost || container == null || user == null) return false;
            PartyMemberAI member = user.GetComponent<PartyMemberAI>();
            if (member == null || _ownership.OwnerOf(member) == Session.HostId) return false;
            _remoteOpening = true;
            return true;
        }

        internal static void CaptureRemoteSingle(Container container, PartyMemberAI opener)
        {
            LootSync self = s_instance;
            if (self != null && self._remoteOpening)
                self.CaptureRemoteOpen(container, opener, new List<Container> { container });
        }

        internal static void CaptureRemoteArea(Container root, PartyMemberAI opener, List<Container> containers)
        {
            LootSync self = s_instance;
            if (self != null && self._remoteOpening) self.CaptureRemoteOpen(root, opener, containers);
        }

        private void CaptureRemoteOpen(Container root, PartyMemberAI opener, List<Container> containers)
        {
            if (!_session.IsHost || root == null || opener == null) return;
            int playerId = _ownership.OwnerOf(opener);
            if (playerId == Session.HostId || !_session.Players.Exists(p => p.Id == playerId)) return;
            Guid openerId = Ownership.IdOf(opener);
            SharedLoot group = GetOrCreateGroup(root, containers);
            if (group == null || openerId == Guid.Empty) return;

            if (_sessionByPlayer.TryGetValue(playerId, out GuestSession old)) CloseSession(old, true);
            GuestSession session = new GuestSession
            {
                Id = NextSessionId(),
                PlayerId = playerId,
                OpenerId = openerId,
                Revision = 1,
                Group = group,
            };
            _sessions.Add(session.Id, session);
            _sessionByPlayer[playerId] = session;
            group.SessionIds.Add(session.Id);
            InventorySync.RequestResync();
            SendState(session, true);
        }

        private void CaptureHostOpen(Container root, GameObject user)
        {
            if (root == null || user == null || UILootManager.Instance == null
                || UILootManager.Instance.ItemGrid == null) return;
            var containers = new List<Container>();
            CombinedInventory combined = UILootManager.Instance.ItemGrid.Inventory as CombinedInventory;
            if (combined != null)
            {
                foreach (Inventory inventory in combined.CombinedInventories)
                {
                    Container container = inventory != null ? inventory.GetComponent<Container>() : null;
                    if (container != null && !containers.Contains(container)) containers.Add(container);
                }
            }
            if (!containers.Contains(root)) containers.Insert(0, root);
            SharedLoot group = GetOrCreateGroup(root, containers);
            if (group == null) return;
            group.HostViewer = true;
        }

        private SharedLoot GetOrCreateGroup(Container root, List<Container> containers)
        {
            Guid rootId = EntityRegistry.NetworkId(root.gameObject);
            if (rootId == Guid.Empty) return null;
            if (!_groups.TryGetValue(rootId, out SharedLoot group))
            {
                group = new SharedLoot { RootId = rootId, Root = root };
                _groups.Add(rootId, group);
            }
            group.Root = root;
            int total = group.Containers.Count;
            foreach (Container container in containers)
                if (container != null && !group.Containers.Contains(container)) total++;
            if (!group.Containers.Contains(root)) total++;
            if (total > MaxContainers) return null;

            bool changed = false;
            foreach (Container container in containers)
            {
                if (container == null || group.Containers.Contains(container)) continue;
                if (EntityRegistry.NetworkId(container.gameObject) == Guid.Empty) continue;
                group.Containers.Add(container);
                changed = true;
            }
            group.Area |= group.Containers.Count > 1;
            if (!group.Containers.Contains(root)) group.Containers.Insert(0, root);
            if (changed && group.SessionIds.Count > 0) MarkGroupChanged(group);
            return group.Containers.Count > 0 ? group : null;
        }

        internal static bool HostContainerClose(Container container)
        {
            LootSync self = s_instance;
            return self == null || self.CloseHostViewer(container);
        }

        internal static bool HostAreaClose(Container container)
        {
            LootSync self = s_instance;
            return self == null || self.CloseHostViewer(container);
        }

        private bool CloseHostViewer(Container container)
        {
            if (!_session.IsHost || _nativeCloseDepth > 0 || container == null) return true;
            Guid rootId = EntityRegistry.NetworkId(container.gameObject);
            if (!_groups.TryGetValue(rootId, out SharedLoot group)) return true;
            if (group.HostViewer) group.HostViewer = false;
            else if (group.SessionIds.Count > 0) return false;
            if (group.HostViewer || group.SessionIds.Count > 0) return false;
            CloseGroupNative(group);
            return false;
        }

        private void CloseSession(GuestSession session, bool send)
        {
            if (session == null) return;
            _sessions.Remove(session.Id);
            if (_sessionByPlayer.TryGetValue(session.PlayerId, out GuestSession current) && current == session)
                _sessionByPlayer.Remove(session.PlayerId);
            SharedLoot group = session.Group;
            group?.SessionIds.Remove(session.Id);
            if (send) SendState(session, false);
            if (group != null && !group.HostViewer && group.SessionIds.Count == 0) CloseGroupNative(group);
        }

        private void CloseGroupNative(SharedLoot group)
        {
            if (group == null || !_groups.ContainsKey(group.RootId)) return;
            _groups.Remove(group.RootId);
            if (!WorldReady()) return;

            var combinedToDestroy = new List<CombinedInventory>();
            if (UILootManager.Instance != null)
            {
                foreach (CombinedInventory combined in UILootManager.Instance.GetComponents<CombinedInventory>())
                {
                    if (combined == null) continue;
                    foreach (Inventory inventory in combined.CombinedInventories)
                    {
                        Container container = inventory != null ? inventory.GetComponent<Container>() : null;
                        if (container != null && group.Containers.Contains(container))
                        {
                            combinedToDestroy.Add(combined);
                            break;
                        }
                    }
                }
            }

            _nativeCloseDepth++;
            try
            {
                foreach (Container container in group.Containers)
                {
                    if (container == null || HasOtherViewers(group, container)) continue;
                    Inventory inventory = container.GetComponent<Inventory>();
                    if (inventory != null)
                        foreach (InventoryItem item in inventory.ItemList) item.AreaLootSource = false;
                }
                foreach (Container container in group.Containers)
                    if (container != null && !HasOtherViewers(group, container)) container.CloseInventoryCB();
                foreach (CombinedInventory combined in combinedToDestroy)
                    if (combined != null) GameUtilities.Destroy(combined);
            }
            finally { _nativeCloseDepth--; }
        }

        private bool HasOtherViewers(SharedLoot closing, Container container)
        {
            foreach (SharedLoot other in _groups.Values)
            {
                if (other == closing || !other.HostViewer && other.SessionIds.Count == 0
                    || !other.Containers.Contains(container)) continue;
                return true;
            }
            return false;
        }

        private void CloseEmptyGuestSessions(SharedLoot group)
        {
            foreach (SharedLoot affected in AffectedGroups(group))
            {
                if (!GroupEmpty(affected)) continue;
                foreach (int id in affected.SessionIds.ToArray())
                    if (_sessions.TryGetValue(id, out GuestSession session)) CloseSession(session, true);
            }
        }

        private static bool GroupEmpty(SharedLoot group)
        {
            foreach (Container container in group.Containers)
            {
                Inventory inventory = container != null ? container.GetComponent<Inventory>() : null;
                if (inventory != null && inventory.ItemList.Count > 0) return false;
            }
            return true;
        }

        private void MarkGroupChanged(SharedLoot group)
        {
            InventorySync.RequestResync();
            foreach (SharedLoot affected in AffectedGroups(group))
                foreach (int id in affected.SessionIds)
                    if (_sessions.TryGetValue(id, out GuestSession session))
                    {
                        session.Revision = NextPositive(session.Revision);
                        SendState(session, true);
                    }
        }

        private List<SharedLoot> AffectedGroups(SharedLoot group)
        {
            var affected = new List<SharedLoot>();
            if (group == null) return affected;
            foreach (SharedLoot candidate in _groups.Values)
            {
                if (candidate != group && !candidate.Containers.Any(c => group.Containers.Contains(c))) continue;
                affected.Add(candidate);
            }
            if (!affected.Contains(group) && _groups.ContainsKey(group.RootId)) affected.Add(group);
            return affected;
        }

        private void UpdateHost()
        {
            foreach (SharedLoot group in _groups.Values.ToArray())
            {
                if (group.Root == null)
                {
                    AbandonGroup(group);
                    continue;
                }
                bool changed = group.Containers.RemoveAll(c => c == null) > 0;
                if (!group.Containers.Contains(group.Root))
                {
                    AbandonGroup(group);
                    continue;
                }
                if (changed) MarkGroupChanged(group);
                foreach (int id in group.SessionIds.ToArray())
                {
                    if (!_sessions.TryGetValue(id, out GuestSession session)) continue;
                    PartyMemberAI opener = Ownership.Find(session.OpenerId);
                    if (opener == null || _ownership.OwnerOf(opener) != session.PlayerId
                        || !_session.Players.Exists(p => p.Id == session.PlayerId)) CloseSession(session, true);
                }
            }
        }

        private void AbandonGroup(SharedLoot group)
        {
            foreach (int id in group.SessionIds.ToArray())
                if (_sessions.TryGetValue(id, out GuestSession session))
                {
                    _sessions.Remove(id);
                    _sessionByPlayer.Remove(session.PlayerId);
                    SendState(session, false);
                }
            group.SessionIds.Clear();
            group.HostViewer = false;
            _groups.Remove(group.RootId);
        }

        private static Container ResolveGroupContainer(SharedLoot group, Guid id)
        {
            foreach (Container container in group.Containers)
                if (container != null && EntityRegistry.NetworkId(container.gameObject) == id) return container;
            return null;
        }

        // ---- guest native UI ----

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            if (sender != null || !_session.IsGuest || reader.Type != MessageType.LootState) return;
            FlowSnapshotHeader header = FlowWire.ReadSnapshot(reader, MessageType.LootState, MaxContainers);
            long epoch = header.WorldEpoch;
            long sequence = header.StateSeq;
            int count = header.Count;
            if (!header.Full) throw new FormatException("loot state must be full");
            int sessionId = reader.Int();
            int openFlag = reader.Byte();
            Guid rootId = reader.Guid();
            Guid openerId = reader.Guid();
            if (sequence <= 0 || openFlag > 1) throw new FormatException("loot state header");
            var containerIds = new List<Guid>(count);
            var unique = new HashSet<Guid>();
            for (int i = 0; i < count; i++)
            {
                Guid id = reader.Guid();
                if (id == Guid.Empty || !unique.Add(id)) throw new FormatException("loot container id");
                containerIds.Add(id);
            }
            int revision = reader.Int();
            reader.Finish();
            if (revision < 0 || openFlag != 0 && (sessionId <= 0 || rootId == Guid.Empty
                || openerId == Guid.Empty || count == 0 || revision <= 0))
                throw new FormatException("open loot state identity");

            SyncContext();
            if (epoch != WorldEpoch) return;
            if (sequence <= _guestSequence) return;
            _guestSequence = sequence;
            if (openFlag == 0)
            {
                if (_guestOpen && sessionId != _guestSession) return;
                HideRemoteWindow();
                ClearGuestState();
                return;
            }

            _guestOpen = true;
            _guestSession = sessionId;
            _guestRevision = revision;
            _guestRootId = rootId;
            _guestOpenerId = openerId;
            _guestContainerIds.Clear();
            _guestContainerIds.AddRange(containerIds);
            _guestClosePending = false;
            _guestUiDirty = true;
        }

        private void UpdateGuest()
        {
            UpdateGuestClick();
            if (!_guestOpen || _guestClosePending || !WorldReady()) return;
            if (!_guestUiDirty && _guestShownSession == _guestSession && _guestShownRevision == _guestRevision) return;
            TryReconstructLootWindow();
        }

        private void TryReconstructLootWindow()
        {
            UILootManager manager = UILootManager.Instance;
            if (manager == null || manager.ItemGrid == null) return;
            Container root = ResolveContainer(_guestRootId);
            PartyMemberAI opener = Ownership.Find(_guestOpenerId);
            if (root == null || opener == null || _ownership.OwnerOf(opener) != _session.LocalId) return;
            var containers = new List<Container>(_guestContainerIds.Count);
            foreach (Guid id in _guestContainerIds)
            {
                Container container = ResolveContainer(id);
                if (container == null || container.GetComponent<Inventory>() == null) return;
                containers.Add(container);
            }
            foreach (Container container in containers)
            {
                Inventory inventory = container.GetComponent<Inventory>();
                if (inventory.ItemList == null) return;
                foreach (InventoryItem item in inventory.ItemList)
                    if (item == null || item.baseItem == null || ItemRegistry.NetworkId(item.baseItem) == Guid.Empty) return;
            }

            _guestApplying = true;
            try
            {
                Inventory display = root.GetComponent<Inventory>();
                if (containers.Count > 1)
                {
                    if (_guestCombined != null) GameUtilities.Destroy(_guestCombined);
                    CombinedInventory combined = manager.gameObject.AddComponent<CombinedInventory>();
                    combined.Initialize(root.ManualLabelName ?? "", containers.ConvertAll(c => c.GetComponent<Inventory>()).ToArray());
                    combined.CloseInventoryCB = delegate { };
                    _guestCombined = combined;
                    display = combined;
                }
                else if (_guestCombined != null)
                {
                    GameUtilities.Destroy(_guestCombined);
                    _guestCombined = null;
                }
                foreach (Container container in containers)
                {
                    Inventory inventory = container.GetComponent<Inventory>();
                    inventory.CloseInventoryCB = delegate { };
                    foreach (InventoryItem item in inventory.ItemList) item.AreaLootSource = container == root;
                }
                manager.SetData(opener, display, root.gameObject);
                if (!manager.WindowActive()) manager.ShowWindow();
                _guestShownSession = _guestSession;
                _guestShownRevision = _guestRevision;
                _guestUiDirty = false;
            }
            finally { _guestApplying = false; }
        }

        private static Container ResolveContainer(Guid id)
        {
            GameObject obj = EntityRegistry.ResolveObject(id);
            return obj != null ? obj.GetComponent<Container>() : null;
        }

        internal static bool GuestSendQuantity(UIInventoryGridItem item, int quantity, BaseInventory target)
        {
            LootSync self = s_instance;
            return self != null && self.TrySendTake(item, quantity, target);
        }

        internal static bool GuestClick(UIInventoryGridItem item)
        {
            LootSync self = s_instance;
            if (self == null || !self.IsRemoteLootItem(item)) return false;
            if (GameInput.GetShiftkey())
            {
                self._guestClickItem = null;
                GuestShiftTake(item);
                return true;
            }
            self._guestClickItem = item;
            self._guestClickDue = Time.unscaledTime + 0.3f;
            return true;
        }

        internal static bool GuestDoubleClick(UIInventoryGridItem item)
        {
            LootSync self = s_instance;
            if (self == null || !self.IsRemoteLootItem(item)) return false;
            self._guestClickItem = null;
            if (item.InvItem.stackSize <= 1)
            {
                self.TrySendTake(item, item.InvItem.stackSize, null);
                return true;
            }
            UIMessageBox dialog = UIInventoryGridItem.ShowSplitDialog(item.InvItem);
            if (dialog == null) return true;
            dialog.OnDialogEnd = (UIMessageBox.OnEndDialog)System.Delegate.Combine(dialog.OnDialogEnd,
                (UIMessageBox.OnEndDialog)((result, owner) =>
                {
                    if (result == UIMessageBox.Result.AFFIRMATIVE && self._guestOpen && !self._guestClosePending)
                        self.TrySendTake(item, owner.NumericValue, null);
                }));
            return true;
        }

        private void UpdateGuestClick()
        {
            UIInventoryGridItem item = _guestClickItem;
            if (item == null || Time.unscaledTime < _guestClickDue) return;
            _guestClickItem = null;
            if (item.InvItem != null) TrySendTake(item, item.InvItem.stackSize, null);
        }

        internal static bool GuestBeginDrag(UIInventoryGridItem item, int quantity)
        {
            LootSync self = s_instance;
            if (self == null || !self.IsRemoteLootItem(item)) return false;
            self._guestClickItem = null;
            if (quantity < 0) quantity = item.InvItem.stackSize;
            self.TrySendTake(item, quantity, null);
            return true;
        }

        internal static bool GuestShiftTake(UIInventoryGridItem item)
        {
            LootSync self = s_instance;
            return self != null && self.TrySendTake(item, item != null && item.InvItem != null ? item.InvItem.stackSize : 0,
                GameState.PlayerStashInventory);
        }

        internal static bool GuestTakeAll(BaseInventory target)
        {
            LootSync self = s_instance;
            if (self == null || !self._session.IsGuest || !self._guestOpen || self._guestClosePending
                || self._guestApplying || UILootManager.Instance == null) return false;
            if (!self.TryDestination(target, out Guid destinationId)) return true;
            self.SendCommand(CommandKind.TakeAll, writer => writer
                .Int(self._guestSession).Int(self._guestRevision).Guid(destinationId));
            return true;
        }

        private bool TrySendTake(UIInventoryGridItem gridItem, int quantity, BaseInventory target)
        {
            if (!IsRemoteLootItem(gridItem) || quantity <= 0 || quantity > gridItem.InvItem.stackSize) return false;
            Container source = gridItem.InvItem.baseItem.StoredInventory != null
                ? gridItem.InvItem.baseItem.StoredInventory.GetComponent<Container>() : null;
            if (source == null || !_guestContainerIds.Contains(EntityRegistry.NetworkId(source.gameObject)))
                source = FindGuestSource(gridItem.InvItem);
            Guid sourceId = source != null ? EntityRegistry.NetworkId(source.gameObject) : Guid.Empty;
            Guid itemId = ItemRegistry.NetworkId(gridItem.InvItem.baseItem);
            if (sourceId == Guid.Empty || itemId == Guid.Empty || !TryDestination(target, out Guid destinationId)) return true;
            SendCommand(CommandKind.Take, writer => writer.Int(_guestSession).Int(_guestRevision)
                .Guid(sourceId).Guid(itemId).Int(quantity).Guid(destinationId));
            return true;
        }

        private bool IsRemoteLootItem(UIInventoryGridItem item)
        {
            return _session.IsGuest && _guestOpen && !_guestClosePending && !_guestApplying
                && item != null && item.InvItem != null && item.InvItem.baseItem != null
                && UILootManager.Instance != null && item.Owner == UILootManager.Instance.ItemGrid
                && _guestShownSession == _guestSession;
        }

        private Container FindGuestSource(InventoryItem inventoryItem)
        {
            foreach (Guid id in _guestContainerIds)
            {
                Container container = ResolveContainer(id);
                Inventory inventory = container != null ? container.GetComponent<Inventory>() : null;
                if (inventory != null && inventory.ItemList.Contains(inventoryItem)) return container;
            }
            return null;
        }

        private bool TryDestination(BaseInventory target, out Guid destinationId)
        {
            UILootManager manager = UILootManager.Instance;
            if (target == null && manager != null)
                target = manager.SelectedStash ? GameState.PlayerStashInventory
                    : manager.SelectedCharacter != null ? manager.SelectedCharacter.GetComponent<Inventory>() : null;
            if (target == null)
            {
                destinationId = Guid.Empty;
                return false;
            }
            if (target is StashInventory || target == GameState.PlayerStashInventory)
            {
                destinationId = Guid.Empty;
                return true;
            }
            PartyMemberAI member = target.GetComponent<PartyMemberAI>();
            if (member == null || !_ownership.IsMine(member))
            {
                destinationId = Guid.Empty;
                return false;
            }
            destinationId = Ownership.IdOf(member);
            return destinationId != Guid.Empty;
        }

        internal static void GuestWindowClosing()
        {
            LootSync self = s_instance;
            if (self == null || !self._session.IsGuest || !self._guestOpen || self._guestApplying
                || self._guestClosePending || self._guestShownSession != self._guestSession || !self.WorldReady()) return;
            self._guestClickItem = null;
            self._guestClosePending = true;
            self.SendCommand(CommandKind.Close, writer => writer.Int(self._guestSession).Int(self._guestRevision));
        }

        private void SendCommand(CommandKind kind, Func<MessageWriter, MessageWriter> payload)
        {
            if (WorldEpoch <= 0 || !_session.IsWorldReady) return;
            long requestId = NextRequestId();
            MessageWriter writer = FlowWire.WriteCommand(WorldEpoch, requestId, Domain, (int)kind);
            _session.SendToHost(payload(writer).ToArray());
        }

        private long NextRequestId()
        {
            _nextRequestId = NextPositive(_nextRequestId);
            return _nextRequestId;
        }

        private void HideRemoteWindow()
        {
            UILootManager manager = UILootManager.Instance;
            _guestClickItem = null;
            _guestApplying = true;
            try
            {
                if (manager != null && _guestShownSession != 0 && manager.IsVisible) manager.HideWindow(true);
                if (_guestCombined != null) GameUtilities.Destroy(_guestCombined);
            }
            finally
            {
                _guestCombined = null;
                _guestApplying = false;
                _guestShownSession = 0;
                _guestShownRevision = 0;
            }
        }

        private void ClearGuestState()
        {
            _guestOpen = false;
            _guestSession = 0;
            _guestRevision = 0;
            _guestRootId = Guid.Empty;
            _guestOpenerId = Guid.Empty;
            _guestContainerIds.Clear();
            _guestClosePending = false;
            _guestUiDirty = false;
        }

        // ---- wire results and state ----

        private void SendState(GuestSession session, bool open)
        {
            if (session == null || !_session.IsHost || WorldEpoch <= 0) return;
            long sequence = NextSequence(session.PlayerId);
            int count = open && session.Group != null ? session.Group.Containers.Count : 0;
            MessageWriter writer = FlowWire.WriteSnapshot(MessageType.LootState, WorldEpoch, sequence, true, count)
                .Int(session.Id).Byte((byte)(open ? 1 : 0))
                .Guid(session.Group != null ? session.Group.RootId : Guid.Empty).Guid(session.OpenerId);
            for (int i = 0; i < count; i++)
                writer.Guid(EntityRegistry.NetworkId(session.Group.Containers[i].gameObject));
            writer.Int(session.Revision);
            _session.SendToPlayer(session.PlayerId, writer.ToArray());
        }

        private void SendCurrentState(int playerId)
        {
            if (!_session.IsHost || WorldEpoch <= 0) return;
            if (_sessionByPlayer.TryGetValue(playerId, out GuestSession session)) SendState(session, true);
            else
            {
                long sequence = NextSequence(playerId);
                _session.SendToPlayer(playerId, FlowWire.WriteSnapshot(MessageType.LootState, WorldEpoch, sequence, true, 0)
                    .Int(0).Byte(0).Guid(Guid.Empty).Guid(Guid.Empty).Int(0).ToArray());
            }
        }

        private void Complete(PlayerInfo sender, FlowCommandEnvelope command, FlowResultCode result, bool sendState)
        {
            if (sendState) SendCurrentState(sender.Id);
            long stateSeq = CurrentSequence(sender.Id);
            _requests.Complete(sender.Id, command, result, stateSeq);
            SendResult(sender, command, result, stateSeq);
        }

        private void SendResult(PlayerInfo sender, FlowCommandEnvelope command, FlowResultCode result, long stateSeq)
        {
            _session.SendToPlayer(sender.Id, FlowWire.WriteResult(command.WorldEpoch, command.RequestId,
                Domain, command.Kind, result, stateSeq).ToArray());
        }

        private long CurrentSequence(int playerId)
            => _sequences.TryGetValue(playerId, out long sequence) ? sequence : 0;

        private int NextSessionId()
        {
            for (int attempts = 0; attempts < int.MaxValue; attempts++)
            {
                _nextSessionId = NextPositive(_nextSessionId);
                if (!_sessions.ContainsKey(_nextSessionId)) return _nextSessionId;
            }
            throw new InvalidOperationException("no loot session ids available");
        }

        private long NextSequence(int playerId)
        {
            _sequences.TryGetValue(playerId, out long sequence);
            sequence = NextPositive(sequence);
            _sequences[playerId] = sequence;
            return sequence;
        }

        private static int NextPositive(int value) => value <= 0 || value == int.MaxValue ? 1 : value + 1;
        private static long NextPositive(long value) => value <= 0 || value == long.MaxValue ? 1 : value + 1;

        // ---- reset and join ----

        private void OnPlayerJoined(PlayerInfo player)
        {
            SyncContext();
        }

        private void OnWorldStateReady(PlayerInfo player, long worldEpoch)
        {
            SyncContext();
            if (worldEpoch != WorldEpoch) return;
            if (_session.IsHost && player != null) SendCurrentState(player.Id);
            if (_session.IsGuest) _guestUiDirty = true;
        }

        private void OnFullStateRequested(int playerId, long worldEpoch)
        {
            SyncContext();
            if (!_session.IsHost || worldEpoch != WorldEpoch) return;
            if (playerId >= 0) SendCurrentState(playerId);
            else foreach (PlayerInfo player in _session.Players)
                if (!player.IsHost) SendCurrentState(player.Id);
        }

        private void OnPlayerLeft(PlayerInfo player)
        {
            if (!_session.IsHost || player == null) return;
            if (_sessionByPlayer.TryGetValue(player.Id, out GuestSession session)) CloseSession(session, false);
            _requests.RemovePlayer(player.Id);
            _sequences.Remove(player.Id);
        }

        private void OnLevelChanged(object sender, EventArgs e)
        {
            ResetWorld();
            _observedEpoch = WorldEpoch;
        }

        private void OnSessionReset()
        {
            _generation = _session.Generation;
            ResetWorld();
            _requests.Clear();
            _nextRequestId = 0;
            _nextSessionId = 0;
            _observedEpoch = WorldEpoch;
        }

        private void SyncContext()
        {
            if (_generation != _session.Generation) OnSessionReset();
            long epoch = WorldEpoch;
            if (_observedEpoch == epoch) return;
            _observedEpoch = epoch;
            ResetWorld();
            _requests.Clear();
        }

        private void ResetWorld()
        {
            _groups.Clear();
            _sessions.Clear();
            _sessionByPlayer.Clear();
            _remoteOpening = false;
            _nativeCloseDepth = 0;
            _sequences.Clear();
            if (_session.IsGuest || _guestOpen || _guestShownSession != 0 || _guestCombined != null)
            {
                HideRemoteWindow();
                ClearGuestState();
                _guestSequence = 0;
            }
        }

        private bool WorldReady()
        {
            return _session.IsWorldReady && !FlowCoordinator.ApplyingWorldBarrier
                && GameState.IsInGameSession && !GameState.IsLoading && !GameState.IsInMainMenu;
        }
    }
}
