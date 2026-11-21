using System;
using System.Collections.Generic;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>Semantic inventory intents and host-authoritative, model-native transactions.</summary>
    public sealed class InventoryCommands : IDisposable
    {
        internal enum Kind : byte
        {
            Move = 1,
            Swap = 2,
            Split = 3,
            Merge = 4,
            Equip = 5,
            Unequip = 6,
            SelectWeapon = 7,
            QuickItem = 8,
            Grimoire = 9,
            Destroy = 10,
            Resync = 11,
        }

        private enum Result : byte
        {
            Accepted = 0,
            Stale = 1,
            NotOwner = 2,
            Invalid = 3,
            Unavailable = 4,
            Tactical = 5,
            Failed = 6,
            Conflict = 7,
        }

        private sealed class Location
        {
            internal Guid Inventory;
            internal Guid Item;
            internal int Slot = -1;
            internal int Revision;
            internal int WeaponSet = -1;
            internal int EquipmentSlot = -1;
            internal Guid Owner;

            internal bool Same(Location other)
            {
                return Inventory == other.Inventory && Item == other.Item && Slot == other.Slot
                    && Revision == other.Revision && WeaponSet == other.WeaponSet && EquipmentSlot == other.EquipmentSlot;
            }
        }

        private sealed class Command
        {
            internal long Epoch;
            internal long RequestId;
            internal Kind Kind;
            internal Location Source = new Location();
            internal Location Target = new Location();
            internal int Quantity;
            internal Guid Actor;
            internal string Key = "";
            internal int Index;
            internal byte Flags;

            internal bool Same(Command other)
            {
                return Kind == other.Kind && Source.Same(other.Source) && Target.Same(other.Target)
                    && Quantity == other.Quantity && Actor == other.Actor && Key == other.Key
                    && Index == other.Index && Flags == other.Flags;
            }
        }

        private sealed class Receipt
        {
            internal Command Command;
            internal Result Result;
        }

        private sealed class PlayerRequests
        {
            internal readonly Dictionary<long, Receipt> Receipts = new Dictionary<long, Receipt>();
            internal readonly Queue<long> Order = new Queue<long>();
        }

        private sealed class PendingDrag
        {
            internal UIInventoryGridItem Source;
            internal Location Location;
            internal int Quantity;
            internal long Generation;
        }

        private sealed class EntryBackup
        {
            internal InventoryItem Entry;
            internal Item Item;
            internal ItemRegistry.Recipe Recipe;
            internal int Stack;
            internal int Slot;
            internal bool Original;
            internal bool AreaSource;
        }

        private sealed class InventoryBackup
        {
            internal BaseInventory Inventory;
            internal readonly List<EntryBackup> Entries = new List<EntryBackup>();

            internal InventoryBackup(BaseInventory inventory)
            {
                Inventory = inventory;
                if (inventory == null) return;
                foreach (InventoryItem entry in inventory.ItemList)
                {
                    if (entry == null || entry.baseItem == null) continue;
                    Entries.Add(new EntryBackup
                    {
                        Entry = entry,
                        Item = entry.baseItem,
                        Recipe = ItemRegistry.Capture(entry.baseItem),
                        Stack = entry.stackSize,
                        Slot = entry.uiSlot,
                        Original = entry.Original,
                        AreaSource = entry.AreaLootSource,
                    });
                }
            }

            internal void Restore()
            {
                if (Inventory == null) return;
                var originalEntries = new HashSet<InventoryItem>();
                var originalItems = new HashSet<Item>();
                foreach (EntryBackup backup in Entries)
                {
                    originalEntries.Add(backup.Entry);
                    if (backup.Item != null) originalItems.Add(backup.Item);
                }
                var current = new List<InventoryItem>(Inventory.ItemList);
                foreach (InventoryItem entry in current)
                    if (entry != null && !originalEntries.Contains(entry)) Inventory.ItemList.Remove(entry);
                Inventory.ItemList.Clear();
                foreach (EntryBackup backup in Entries)
                {
                    Item item = backup.Item;
                    if (item == null && backup.Recipe != null) item = ItemRegistry.RestoreHost(backup.Recipe);
                    if (item == null) continue;
                    backup.Entry.baseItem = item;
                    backup.Entry.stackSize = backup.Stack;
                    backup.Entry.uiSlot = backup.Slot;
                    backup.Entry.Original = backup.Original;
                    backup.Entry.AreaLootSource = backup.AreaSource;
                    PlaceInInventory(Inventory, backup.Entry, backup.Slot, false);
                    Inventory.ItemList.Add(backup.Entry);
                    originalItems.Add(item);
                }
                foreach (InventoryItem entry in current)
                {
                    if (entry == null || entry.baseItem == null || originalItems.Contains(entry.baseItem)) continue;
                    Persistence persistence = entry.baseItem.GetComponent<Persistence>();
                    if (persistence != null) PersistenceManager.RemoveObject(persistence);
                    GameUtilities.Destroy(entry.baseItem.gameObject);
                }
                InventorySync.MarkChanged(Inventory);
            }
        }

        private const FlowDomain Domain = FlowDomain.Inventory;
        private const int MaxRememberedRequests = 256;
        private const int MaxKeyLength = 96;
        private const int MaxQuantity = 1000000000;

        private static InventoryCommands s_instance;
        internal static InventoryCommands Instance => s_instance;

        private readonly Session _session;
        private readonly Ownership _ownership;
        private readonly Dictionary<int, PlayerRequests> _requests = new Dictionary<int, PlayerRequests>();
        private PendingDrag _pendingDrag;
        private long _generation;
        private long _nextRequestId;
        private long _receiptEpoch = -1;

        public InventoryCommands(Session session, Ownership ownership)
        {
            _session = session;
            _ownership = ownership;
            _generation = session.Generation;
            s_instance = this;
            session.PlayerLeft += OnPlayerLeft;
        }

        public void Dispose()
        {
            _session.PlayerLeft -= OnPlayerLeft;
            _requests.Clear();
            _pendingDrag = null;
            if (s_instance == this) s_instance = null;
        }

        public void Update(float now)
        {
            CheckGeneration();
            if (_receiptEpoch != InventorySync.CurrentWorldEpoch)
            {
                _receiptEpoch = InventorySync.CurrentWorldEpoch;
                _requests.Clear();
                _pendingDrag = null;
            }
            if (_pendingDrag != null && _pendingDrag.Generation != _generation)
                _pendingDrag = null;
        }

        // ---- guest UI capture ----

        internal static bool CaptureBeginDrag(UIInventoryGridItem source, int quantity)
        {
            InventoryCommands self = GuestInstance();
            if (self == null) return false;
            if (!UsableGrid(source)) return true;
            Location location = self.LocationOf(source);
            if (location.Inventory == Guid.Empty && location.EquipmentSlot < 0) return true;
            self._pendingDrag = new PendingDrag
            {
                Source = source,
                Location = location,
                Quantity = quantity > 0 ? Math.Min(quantity, source.InvItem.stackSize) : source.InvItem.stackSize,
                Generation = self._generation,
            };
            return true;
        }

        internal static bool CaptureSwap(UIInventoryGridItem destination, UIInventoryGridItem source, int quantity)
        {
            InventoryCommands self = GuestInstance();
            if (self == null) return false;
            if (destination == null || !UsableGrid(source)) return true;
            if (quantity <= 0 || quantity > source.InvItem.stackSize) quantity = source.InvItem.stackSize;
            self.SendTransfer(self.LocationOf(source), self.LocationOf(destination), quantity);
            self._pendingDrag = null;
            return true;
        }

        internal static bool CaptureDrop(UIInventoryGridItem destination, int quantity = -1)
        {
            InventoryCommands self = GuestInstance();
            if (self == null) return false;
            PendingDrag pending = self._pendingDrag;
            UIInventoryGridItem source = pending != null ? pending.Source : UIGlobalInventory.Instance.DraggedSource;
            if (pending == null && (source == null || !UsableGrid(source)) || destination == null) return true;
            if (quantity <= 0) quantity = pending != null ? pending.Quantity : source.InvItem.stackSize;
            self.SendTransfer(pending != null ? pending.Location : self.LocationOf(source), self.LocationOf(destination), quantity);
            self._pendingDrag = null;
            return true;
        }

        internal static bool CaptureQuantity(UIInventoryGridItem source, int quantity, BaseInventory explicitTarget)
        {
            InventoryCommands self = GuestInstance();
            if (self == null) return false;
            if (!UsableGrid(source)) return true;
            BaseInventory target = explicitTarget;
            if (target == null && source.GetClickSendsTo() != null) target = source.GetClickSendsTo().Inventory;
            if (target == null && source.ClickSendsToPlayer && GameState.s_playerCharacter != null)
                target = GameState.s_playerCharacter.Inventory;
            if (target == null) return true;
            self.SendTransfer(self.LocationOf(source), self.LocationOf(target), quantity);
            return true;
        }

        internal static bool CaptureReceiver(UIInventoryItemReciever receiver)
        {
            InventoryCommands self = GuestInstance();
            if (self == null) return false;
            PendingDrag pending = self._pendingDrag;
            UIInventoryGridItem source = pending != null ? pending.Source : UIGlobalInventory.Instance.DraggedSource;
            if (pending == null && (source == null || !UsableGrid(source)) || receiver == null || receiver.Reciever == null) return true;
            int quantity = pending != null ? pending.Quantity : source.InvItem.stackSize;
            self.SendTransfer(pending != null ? pending.Location : self.LocationOf(source), self.LocationOf(receiver.Reciever), quantity);
            self._pendingDrag = null;
            return true;
        }

        internal static bool CaptureDestroy(UIInventoryGridItem source)
        {
            InventoryCommands self = GuestInstance();
            if (self == null) return false;
            if (!UsableGrid(source)) return true;
            var command = self.NewCommand(Kind.Destroy);
            command.Source = self.LocationOf(source);
            command.Quantity = source.InvItem.stackSize;
            self.Send(command);
            return true;
        }

        internal static bool CaptureSelectWeapon(Equipment equipment, int index)
        {
            InventoryCommands self = GuestInstance();
            if (self == null || equipment == null) return false;
            PartyMemberAI member = equipment.GetComponent<PartyMemberAI>();
            if (member == null) return true;
            var command = self.NewCommand(Kind.SelectWeapon);
            command.Actor = ActorId(member);
            command.Target.Revision = InventorySync.RevisionOf(equipment);
            command.Target.EquipmentSlot = (int)Equippable.EquipmentSlot.PrimaryWeapon;
            command.Target.WeaponSet = index;
            command.Index = index;
            self.Send(command);
            return true;
        }

        internal static bool CaptureNextWeapon(Equipment equipment)
        {
            InventoryCommands self = GuestInstance();
            if (self == null || equipment == null) return false;
            CharacterStats stats = equipment.GetComponent<CharacterStats>();
            EquipmentSet set = equipment.CurrentItems;
            if (stats == null || set == null || stats.MaxWeaponSets <= 0) return true;
            int index = set.SelectedWeaponSet;
            for (int i = 0; i < stats.MaxWeaponSets; i++)
            {
                index = (index + 1) % stats.MaxWeaponSets;
                if (equipment.IsWeaponSetValid(index) || !WeaponSet.IsNullOrEmpty(set.GetWeaponSet(index))) break;
            }
            return CaptureSelectWeapon(equipment, index);
        }

        internal static bool CaptureQuickItem(Consumable consumable, GameObject owner, bool immediate)
        {
            InventoryCommands self = GuestInstance();
            if (self == null || consumable == null || owner == null) return false;
            PartyMemberAI member = owner.GetComponent<PartyMemberAI>();
            if (member == null) return true;
            var command = self.NewCommand(Kind.QuickItem);
            command.Actor = ActorId(member);
            command.Source = self.LocationOf(consumable.StoredInventory, consumable);
            command.Flags = (byte)(immediate ? 1 : 0);
            command.Quantity = 1;
            self.Send(command);
            return true;
        }

        internal static bool CaptureGrimoire(Grimoire grimoire, GenericSpell spell, int operation)
        {
            InventoryCommands self = GuestInstance();
            if (self == null || grimoire == null || spell == null || UIGrimoireManager.Instance == null) return false;
            Item item = grimoire.GetComponent<Item>();
            PartyMemberAI member = UIGrimoireManager.Instance.SelectedCharacter != null
                ? UIGrimoireManager.Instance.SelectedCharacter.GetComponent<PartyMemberAI>() : null;
            if (item == null || member == null) return true;
            var command = self.NewCommand(Kind.Grimoire);
            command.Actor = ActorId(member);
            command.Source = self.LocationOf(item.StoredInventory, item);
            command.Source.Item = ItemRegistry.NetworkId(item);
            if (command.Source.Inventory == Guid.Empty)
            {
                Equipment equipment = member.GetComponent<Equipment>();
                command.Source.Revision = InventorySync.RevisionOf(equipment);
            }
            command.Key = ItemRegistry.NormalizeKey(spell.name);
            command.Index = spell.SpellLevel;
            command.Flags = (byte)operation;
            command.Quantity = 1;
            self.Send(command);
            return true;
        }

        internal static bool CaptureActionEquip(UIInventoryGridItem source)
        {
            InventoryCommands self = GuestInstance();
            if (self == null) return false;
            if (!UsableGrid(source) || UIInventoryManager.Instance == null) return true;
            PartyMemberAI member = UIInventoryManager.Instance.SelectedCharacter != null
                ? UIInventoryManager.Instance.SelectedCharacter.GetComponent<PartyMemberAI>() : null;
            if (member == null) return true;
            if (source.IsEquipped || source.EquipmentSlot != Equippable.EquipmentSlot.None)
            {
                self.SendTransfer(self.LocationOf(source), self.LocationOf(member.Inventory), 1);
                return true;
            }
            if (source.InvItem.baseItem is Consumable)
            {
                self.SendTransfer(self.LocationOf(source), self.LocationOf(member.QuickbarInventory), source.InvItem.stackSize);
                return true;
            }
            Equippable equippable = source.InvItem.baseItem as Equippable;
            if (equippable == null) return true;
            if (!(equippable is Weapon) && equippable.GetComponent<Shield>() == null)
            {
                UIInventoryGridItem destination = UIInventoryManager.Instance.Equipment.GetGridItemForSlot(equippable.GetPreferredSlot());
                if (destination != null) self.SendTransfer(self.LocationOf(source), self.LocationOf(destination), 1);
                return true;
            }
            UIInventoryWeaponSet[] sets = UIInventoryManager.Instance.WeaponSets.SubSets;
            if (sets == null) return true;
            foreach (UIInventoryWeaponSet set in sets)
            {
                if (set == null || set.PrimarySlot.Blocked || set.SecondarySlot.Blocked) continue;
                if (set.PrimarySlot.Empty && equippable.PrimaryWeaponSlot)
                {
                    self.SendTransfer(self.LocationOf(source), self.LocationOf(set.PrimarySlot), 1);
                    return true;
                }
                if (set.SecondarySlot.Empty && equippable.SecondaryWeaponSlot && !equippable.BothPrimaryAndSecondarySlot)
                {
                    self.SendTransfer(self.LocationOf(source), self.LocationOf(set.SecondarySlot), 1);
                    return true;
                }
            }
            Equipment equipment = member.GetComponent<Equipment>();
            int selected = equipment != null ? equipment.CurrentItems.SelectedWeaponSet : 0;
            UIInventoryWeaponSet selectedSet = sets != null && selected >= 0 && selected < sets.Length ? sets[selected] : null;
            if (selectedSet != null)
            {
                UIInventoryGridItem destination = !selectedSet.SecondarySlot.Empty && equippable.SecondaryWeaponSlot
                    ? selectedSet.SecondarySlot : selectedSet.PrimarySlot;
                self.SendTransfer(self.LocationOf(source), self.LocationOf(destination), 1);
            }
            return true;
        }

        internal static bool PendingDragActive => s_instance != null && s_instance._pendingDrag != null;

        internal static UIInventoryGridItem PendingSource()
        {
            InventoryCommands self = s_instance;
            if (self == null) return null;
            return self._pendingDrag != null ? self._pendingDrag.Source : UIGlobalInventory.Instance.DraggedSource;
        }

        internal static bool CaptureResync()
        {
            InventoryCommands self = GuestInstance();
            if (self == null) return false;
            self.Send(self.NewCommand(Kind.Resync));
            return true;
        }

        private static InventoryCommands GuestInstance()
        {
            InventoryCommands self = s_instance;
            return self != null && Plugin.IsMirroring && !InventorySync.IsApplyingHostState ? self : null;
        }

        private static bool UsableGrid(UIInventoryGridItem grid)
        {
            return grid != null && grid.InvItem != null && grid.InvItem.baseItem != null;
        }

        private Command NewCommand(Kind kind)
        {
            CheckGeneration();
            return new Command
            {
                Epoch = InventorySync.CurrentWorldEpoch,
                RequestId = NextRequestId(),
                Kind = kind,
            };
        }

        private void SendTransfer(Location source, Location target, int quantity)
        {
            Kind kind = Kind.Move;
            if (target.EquipmentSlot >= 0) kind = Kind.Equip;
            else if (source.EquipmentSlot >= 0) kind = Kind.Unequip;
            else if (target.Item != Guid.Empty)
            {
                Item sourceItem = ItemRegistry.Resolve(source.Item);
                Item targetItem = ItemRegistry.Resolve(target.Item);
                kind = sourceItem != null && targetItem != null && InventoryItem.ItemsCanStack(sourceItem, targetItem)
                    ? Kind.Merge : Kind.Swap;
            }
            if (quantity > 0 && source.Item != Guid.Empty)
            {
                Item item = ItemRegistry.Resolve(source.Item);
                if (item != null && quantity < StackAt(source, item)) kind = Kind.Split;
            }
            Send(new Command
            {
                Epoch = InventorySync.CurrentWorldEpoch,
                RequestId = NextRequestId(),
                Kind = kind,
                Source = source,
                Target = target,
                Quantity = quantity,
                Actor = source.Owner != Guid.Empty ? source.Owner : target.Owner,
            });
        }

        private int StackAt(Location location, Item item)
        {
            BaseInventory inventory = InventorySync.ResolveInventory(location.Inventory);
            if (inventory == null) return 1;
            InventoryItem entry = FindEntry(inventory, item, location.Slot);
            return entry != null ? entry.stackSize : 1;
        }

        private Location LocationOf(UIInventoryGridItem grid)
        {
            Item item = grid.InvItem != null ? grid.InvItem.baseItem : null;
            if (grid.EquipmentSlot != Equippable.EquipmentSlot.None)
            {
                GameObject owner = grid.Owner != null ? grid.Owner.OwnerGameObject : null;
                PartyMemberAI member = owner != null ? owner.GetComponent<PartyMemberAI>() : null;
                Equipment equipment = member != null ? member.GetComponent<Equipment>() : null;
                int weaponSet = grid.Owner is UIInventoryWeaponSet weapons ? weapons.BackingSetIndex
                    : equipment != null ? equipment.CurrentItems.SelectedWeaponSet : -1;
                return new Location
                {
                    Item = ItemRegistry.NetworkId(item),
                    Owner = ActorId(member),
                    Slot = grid.Slot,
                    Revision = InventorySync.RevisionOf(equipment),
                    EquipmentSlot = (int)grid.EquipmentSlot,
                    WeaponSet = weaponSet,
                };
            }
            return LocationOf(grid.Owner != null ? grid.Owner.Inventory : null, item, grid.Slot);
        }

        private Location LocationOf(BaseInventory inventory, Item item = null, int slot = -1)
        {
            return new Location
            {
                Inventory = InventorySync.IdOf(inventory),
                Item = ItemRegistry.NetworkId(item),
                Owner = InventorySync.ActorOf(inventory),
                Slot = slot,
                Revision = InventorySync.RevisionOf(inventory),
            };
        }

        private void Send(Command command)
        {
            if (!_session.IsGuest || command.Epoch <= 0 || !InventorySync.WorldReady || command.RequestId <= 0) return;
            MessageWriter writer = FlowWire.WriteCommand(command.Epoch, command.RequestId, Domain, (int)command.Kind);
            WriteLocation(writer, command.Source);
            WriteLocation(writer, command.Target);
            writer.Int(command.Quantity).Guid(command.Actor).String(command.Key).Int(command.Index).Byte(command.Flags);
            _session.SendToHost(writer.ToArray());
        }

        private static void WriteLocation(MessageWriter writer, Location location)
        {
            writer.Guid(location.Inventory).Guid(location.Item).Int(location.Slot).Int(location.Revision)
                .Int(location.WeaponSet).Int(location.EquipmentSlot);
        }

        private static Location ReadLocation(MessageReader reader)
        {
            var location = new Location
            {
                Inventory = reader.Guid(),
                Item = reader.Guid(),
                Slot = reader.Int(),
                Revision = reader.Int(),
                WeaponSet = reader.Int(),
                EquipmentSlot = reader.Int(),
            };
            if (location.Slot < -1 || location.Slot > 1000000 || location.Revision < 0
                || location.WeaponSet < -1 || location.WeaponSet >= EquipmentSet.MAX_WEAPON_SETS
                || location.EquipmentSlot < -1 || location.EquipmentSlot > (int)Equippable.EquipmentSlot.SecondaryWeapon2)
                throw new FormatException("inventory command location");
            return location;
        }

        private long NextRequestId()
        {
            _nextRequestId = _nextRequestId == long.MaxValue ? 1 : _nextRequestId + 1;
            return _nextRequestId;
        }

        private void CheckGeneration()
        {
            if (_generation == _session.Generation) return;
            _generation = _session.Generation;
            _nextRequestId = 0;
            _pendingDrag = null;
            _requests.Clear();
        }

        // ---- FlowCoordinator dispatch surface ----

        /// <summary>Handles the payload after FlowCoordinator has read worldEpoch/requestId/domain/kind.</summary>
        public void HandleHostCommand(PlayerInfo sender, long worldEpoch, long requestId, int kind, MessageReader reader)
        {
            if (sender == null || !_session.IsHost) return;
            CheckGeneration();
            if (_receiptEpoch != InventorySync.CurrentWorldEpoch)
            {
                _receiptEpoch = InventorySync.CurrentWorldEpoch;
                _requests.Clear();
            }
            var command = new Command
            {
                Epoch = worldEpoch,
                RequestId = requestId,
                Kind = (Kind)kind,
                Source = ReadLocation(reader),
                Target = ReadLocation(reader),
                Quantity = reader.Int(),
                Actor = reader.Guid(),
                Key = ItemRegistry.NormalizeKey(Protocol.Clean(reader.String(), MaxKeyLength)),
                Index = reader.Int(),
                Flags = reader.Byte(),
            };
            reader.Finish();
            if (command.Kind < Kind.Move || command.Kind > Kind.Resync || requestId <= 0
                || command.Quantity < 0 || command.Quantity > MaxQuantity || command.Index < -1 || command.Index > 1000000)
            {
                Complete(sender, command, Result.Invalid);
                return;
            }

            PlayerRequests requests = RequestsFor(sender.Id);
            if (requests.Receipts.TryGetValue(requestId, out Receipt previous))
            {
                Result result = previous.Command.Same(command) && previous.Command.Epoch == command.Epoch
                    ? previous.Result : Result.Conflict;
                SendResult(sender, previous.Command.Epoch, requestId, kind, result);
                return;
            }
            requests.Receipts[requestId] = new Receipt { Command = command, Result = Result.Failed };
            requests.Order.Enqueue(requestId);
            while (requests.Order.Count > MaxRememberedRequests)
                requests.Receipts.Remove(requests.Order.Dequeue());

            Result resultCode = Execute(command, sender.Id);
            requests.Receipts[requestId].Result = resultCode;
            SendResult(sender, command.Epoch, requestId, kind, resultCode);
        }

        public void HandleFlowResult(long worldEpoch, long requestId, int kind, FlowResultCode result, long stateSeq)
        {
            CheckGeneration();
            if (kind < (int)Kind.Move || kind > (int)Kind.Resync || stateSeq < 0)
                throw new FormatException("invalid inventory flow result");
            if (!_session.IsGuest || worldEpoch != InventorySync.CurrentWorldEpoch) return;
            if (result == FlowResultCode.Ok) return;
            if (result == FlowResultCode.StaleWorld) Send(NewCommand(Kind.Resync));

            string message;
            switch (result)
            {
                case FlowResultCode.StaleWorld: message = "The inventory changed; requesting a fresh snapshot."; break;
                case FlowResultCode.NotReady: message = "That inventory action is not currently available."; break;
                case FlowResultCode.Rejected: message = "The host refused that inventory action."; break;
                case FlowResultCode.Invalid: message = "That inventory action is invalid."; break;
                case FlowResultCode.Conflict: message = "The inventory request conflicted with an earlier request."; break;
                default: message = "The host could not complete that inventory action."; break;
            }
            _session.Note(message);
        }

        private Result Execute(Command command, int playerId)
        {
            if (command.Epoch != InventorySync.CurrentWorldEpoch || command.Epoch <= 0) return Result.Stale;
            if (!InventorySync.WorldReady || !GameState.IsInGameSession || GameState.IsLoading) return Result.Unavailable;
            if (command.Kind == Kind.Resync)
            {
                InventorySync.RequestResync(playerId);
                return Result.Accepted;
            }
            try
            {
                InventorySync.BeginMutation();
                bool changed = false;
                try
                {
                    Result result = ExecuteValidated(command, playerId, out changed);
                    return result;
                }
                finally
                {
                    InventorySync.EndMutation(changed);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Inventory command " + command.Kind + " failed: " + ex.Message);
                return Result.Failed;
            }
        }

        private Result ExecuteValidated(Command command, int playerId, out bool changed)
        {
            changed = false;
            switch (command.Kind)
            {
                case Kind.SelectWeapon: return SelectWeapon(command, playerId, out changed);
                case Kind.QuickItem: return UseQuickItem(command, playerId, out changed);
                case Kind.Grimoire: return EditGrimoire(command, playerId, out changed);
                case Kind.Destroy: return Destroy(command, playerId, out changed);
                case Kind.Move:
                case Kind.Swap:
                case Kind.Split:
                case Kind.Merge:
                case Kind.Equip:
                case Kind.Unequip:
                    return Transfer(command, playerId, out changed);
                default: return Result.Invalid;
            }
        }

        private Result Transfer(Command command, int playerId, out bool changed)
        {
            changed = false;
            if (command.Quantity <= 0 || command.Source.Item == Guid.Empty) return Result.Invalid;
            Item sourceItem = ItemRegistry.Resolve(command.Source.Item);
            if (sourceItem == null || sourceItem.IsQuestItem) return Result.Invalid;

            BaseInventory sourceInventory = InventorySync.ResolveInventory(command.Source.Inventory);
            Equipment sourceEquipment = command.Source.Inventory == Guid.Empty ? ResolveCommandEquipment(command.Source, command.Actor) : null;
            InventoryItem sourceEntry = null;
            if (sourceInventory != null)
            {
                if (!CanAccess(sourceInventory, playerId) || command.Source.EquipmentSlot >= 0) return Result.NotOwner;
                if (InventorySync.RevisionOf(sourceInventory) != command.Source.Revision) return Result.Stale;
                sourceEntry = FindEntry(sourceInventory, sourceItem, command.Source.Slot);
                if (sourceEntry == null || sourceEntry.stackSize < command.Quantity) return Result.Invalid;
            }
            else if (sourceEquipment != null)
            {
                PartyMemberAI member = sourceEquipment.GetComponent<PartyMemberAI>();
                if (member == null || _ownership.OwnerOf(member) != playerId) return Result.NotOwner;
                if (InventorySync.RevisionOf(sourceEquipment) != command.Source.Revision) return Result.Stale;
                Equippable equipped = EquipmentItem(sourceEquipment, command.Source.EquipmentSlot, command.Source.WeaponSet);
                if (equipped != sourceItem || !equipped.CanUnequip() || command.Quantity != 1) return Result.Invalid;
                Result sourceTactical = CanChangeWeapon(sourceEquipment, command.Source.EquipmentSlot);
                if (sourceTactical != Result.Accepted) return sourceTactical;
            }
            else return Result.Invalid;

            BaseInventory targetInventory = InventorySync.ResolveInventory(command.Target.Inventory);
            Equipment targetEquipment = command.Target.Inventory == Guid.Empty && command.Target.EquipmentSlot >= 0
                ? ResolveCommandEquipment(command.Target, command.Actor) : null;
            if (targetInventory == null && targetEquipment == null) return Result.Invalid;
            if (targetInventory != null)
            {
                if (!CanDeposit(targetInventory)) return Result.NotOwner;
                if (InventorySync.RevisionOf(targetInventory) != command.Target.Revision) return Result.Stale;
                if (targetInventory is QuestInventory || targetInventory is CraftingInventory || sourceInventory is QuestInventory
                    || sourceInventory is CraftingInventory) return Result.Invalid;
                if (targetInventory is QuickbarInventory && !(sourceItem is Consumable)) return Result.Invalid;
                if (command.Target.Slot >= 0)
                {
                    InventoryItem occupied = EntryAtSlot(targetInventory, command.Target.Slot);
                    if (occupied != null && ItemRegistry.NetworkId(occupied.baseItem) != command.Target.Item) return Result.Invalid;
                    if (occupied == null && command.Target.Item != Guid.Empty) return Result.Invalid;
                }
            }
            if (targetEquipment != null)
            {
                PartyMemberAI member = targetEquipment.GetComponent<PartyMemberAI>();
                if (member == null || _ownership.OwnerOf(member) != playerId) return Result.NotOwner;
                if (InventorySync.RevisionOf(targetEquipment) != command.Target.Revision) return Result.Stale;
                Equippable equippable = sourceItem as Equippable;
                if (equippable == null || !equippable.CanUseSlot((Equippable.EquipmentSlot)command.Target.EquipmentSlot)
                    || equippable.WhyCantEquip(targetEquipment.gameObject) != Equippable.CantEquipReason.None
                    || !targetEquipment.HasEquipmentSlot((Equippable.EquipmentSlot)command.Target.EquipmentSlot)
                    || targetEquipment.IsSlotLocked((Equippable.EquipmentSlot)command.Target.EquipmentSlot)) return Result.Invalid;
                CharacterStats targetStats = targetEquipment.GetComponent<CharacterStats>();
                if (Equippable.IsWeapon((Equippable.EquipmentSlot)command.Target.EquipmentSlot)
                    && (targetStats == null || command.Target.WeaponSet < 0
                        || command.Target.WeaponSet >= targetStats.MaxWeaponSets)) return Result.Invalid;
                Result tactical = CanChangeWeapon(targetEquipment, command.Target.EquipmentSlot);
                if (tactical != Result.Accepted) return tactical;
            }

            var sourceBackup = new InventoryBackup(sourceInventory);
            var targetBackup = new InventoryBackup(targetInventory);
            var rollback = TransactionBackups(sourceBackup, targetBackup);
            try
            {
                if (targetInventory != null && sourceInventory != null)
                    return InventoryToInventory(command, sourceInventory, sourceEntry, targetInventory, out changed);
                if (targetInventory != null && sourceEquipment != null)
                    return EquipmentToInventory(command, sourceEquipment, sourceItem as Equippable, targetInventory, targetBackup, out changed);
                if (targetEquipment != null && sourceInventory != null)
                    return InventoryToEquipment(command, sourceInventory, sourceEntry, targetEquipment, out changed);
                if (targetEquipment != null && sourceEquipment != null)
                    return EquipmentToEquipment(command, sourceEquipment, targetEquipment, sourceItem as Equippable, out changed);
                return Result.Invalid;
            }
            catch
            {
                foreach (InventoryBackup backup in rollback) backup.Restore();
                throw;
            }
        }

        private static List<InventoryBackup> TransactionBackups(InventoryBackup source, InventoryBackup target)
        {
            var result = new List<InventoryBackup>();
            var seen = new HashSet<BaseInventory>();
            foreach (InventoryBackup backup in new[] { source, target })
                if (backup.Inventory != null && seen.Add(backup.Inventory)) result.Add(backup);
            foreach (PartyMemberAI member in PartyMemberAI.OnlyPrimaryPartyMembers)
            {
                if (member == null) continue;
                if (member.Inventory != null && seen.Add(member.Inventory)) result.Add(new InventoryBackup(member.Inventory));
                if (member.QuickbarInventory != null && seen.Add(member.QuickbarInventory))
                    result.Add(new InventoryBackup(member.QuickbarInventory));
            }
            PlayerInventory player = GameState.PlayerInventory;
            if (player != null)
            {
                if (player.StashInventory != null && seen.Add(player.StashInventory))
                    result.Add(new InventoryBackup(player.StashInventory));
                if (player.QuestInventory != null && seen.Add(player.QuestInventory))
                    result.Add(new InventoryBackup(player.QuestInventory));
                if (player.CraftingInventory != null && seen.Add(player.CraftingInventory))
                    result.Add(new InventoryBackup(player.CraftingInventory));
            }
            return result;
        }

        private Result InventoryToInventory(Command command, BaseInventory source, InventoryItem sourceEntry,
            BaseInventory target, out bool changed)
        {
            changed = false;
            Item sourceItem = sourceEntry.baseItem;
            InventoryItem targetEntry = command.Target.Item != Guid.Empty
                ? FindEntry(target, ItemRegistry.Resolve(command.Target.Item), command.Target.Slot) : null;
            if (command.Target.Item != Guid.Empty && targetEntry == null) return Result.Invalid;
            if (targetEntry == sourceEntry) return Result.Accepted;

            if (targetEntry != null && InventoryItem.ItemsCanStack(sourceItem, targetEntry.baseItem)
                && command.Kind != Kind.Swap)
            {
                int maximum = target.InfiniteStacking ? int.MaxValue : targetEntry.baseItem.MaxStackSize;
                int amount = Math.Min(command.Quantity, maximum - targetEntry.stackSize);
                if (amount <= 0) return Result.Unavailable;
                bool original = targetEntry.Original;
                targetEntry.SetStackSize(targetEntry.stackSize + amount);
                targetEntry.Original = original || sourceEntry.Original;
                targetEntry.AreaLootSource |= sourceEntry.AreaLootSource;
                RemoveFromSource(source, sourceEntry, amount);
                changed = true;
                InventorySync.MarkChanged(source);
                InventorySync.MarkChanged(target);
                return Result.Accepted;
            }

            if (targetEntry != null)
            {
                if (command.Quantity != sourceEntry.stackSize || command.Kind == Kind.Split || command.Kind == Kind.Merge
                    || !CanSwap(target, sourceEntry, targetEntry) || !CanSwap(source, targetEntry, sourceEntry)) return Result.Invalid;
                SwapEntries(source, sourceEntry, target, targetEntry);
                changed = true;
                InventorySync.MarkChanged(source);
                InventorySync.MarkChanged(target);
                return Result.Accepted;
            }

            bool partial = command.Quantity < sourceEntry.stackSize;
            if (command.Kind == Kind.Merge) return Result.Invalid;
            Item addedItem = partial ? sourceItem.Prefab : sourceItem;
            var previousStacks = new Dictionary<InventoryItem, int>();
            foreach (InventoryItem entry in target.ItemList)
                if (entry != null) previousStacks[entry] = entry.stackSize;
            int remainder = target.AddItem(addedItem, command.Quantity, command.Target.Slot, sourceEntry.Original);
            foreach (InventoryItem entry in target.ItemList)
            {
                if (entry == null || entry.baseItem == null) continue;
                if (!previousStacks.TryGetValue(entry, out int oldStack)) entry.AreaLootSource = sourceEntry.AreaLootSource;
                else if (entry.stackSize > oldStack) entry.AreaLootSource |= sourceEntry.AreaLootSource;
            }
            int moved = command.Quantity - remainder;
            if (moved <= 0) return Result.Unavailable;
            RemoveFromSource(source, sourceEntry, moved, sourceItem);
            changed = true;
            InventorySync.MarkChanged(source);
            InventorySync.MarkChanged(target);
            return Result.Accepted;
        }

        private Result EquipmentToInventory(Command command, Equipment equipment, Equippable item,
            BaseInventory target, InventoryBackup targetBackup, out bool changed)
        {
            changed = false;
            InventoryItem targetEntry = command.Target.Item != Guid.Empty
                ? FindEntry(target, ItemRegistry.Resolve(command.Target.Item), command.Target.Slot) : null;
            if (command.Target.Item != Guid.Empty && targetEntry == null) return Result.Invalid;
            if (targetEntry != null)
            {
                Equippable incoming = targetEntry.baseItem as Equippable;
                if (incoming == null || !incoming.CanUseSlot((Equippable.EquipmentSlot)command.Source.EquipmentSlot)
                    || incoming.WhyCantEquip(equipment.gameObject) != Equippable.CantEquipReason.None) return Result.Invalid;
                try
                {
                    EquipNative(equipment, incoming, command.Source.EquipmentSlot, command.Source.WeaponSet);
                }
                catch
                {
                    if (EquipmentItem(equipment, command.Source.EquipmentSlot, command.Source.WeaponSet) != item)
                        EquipNative(equipment, item, command.Source.EquipmentSlot, command.Source.WeaponSet);
                    RestoreEquipmentVisual(equipment, item, command.Source.EquipmentSlot);
                    throw;
                }
                if (EquipmentItem(equipment, command.Source.EquipmentSlot, command.Source.WeaponSet) != incoming)
                {
                    if (EquipmentItem(equipment, command.Source.EquipmentSlot, command.Source.WeaponSet) != item)
                        EquipNative(equipment, item, command.Source.EquipmentSlot, command.Source.WeaponSet);
                    RestoreEquipmentVisual(equipment, item, command.Source.EquipmentSlot);
                    return Result.Failed;
                }
                target.ItemList.Remove(targetEntry);
                var replacement = new InventoryItem(item, 1)
                {
                    uiSlot = targetEntry.uiSlot,
                    Original = targetEntry.Original,
                    AreaLootSource = targetEntry.AreaLootSource,
                };
                PlaceInInventory(target, replacement, replacement.uiSlot, true);
            }
            else
            {
                int remainder = target.AddItem(item, 1, command.Target.Slot, false);
                if (remainder != 0) return Result.Unavailable;
                Equippable removed;
                try
                {
                    removed = UnequipNative(equipment, item, command.Source.EquipmentSlot, command.Source.WeaponSet);
                }
                catch
                {
                    targetBackup.Restore();
                    RestoreEquipmentVisual(equipment, item, command.Source.EquipmentSlot);
                    throw;
                }
                if (removed != item || EquipmentItem(equipment, command.Source.EquipmentSlot, command.Source.WeaponSet) == item)
                {
                    targetBackup.Restore();
                    RestoreEquipmentVisual(equipment, item, command.Source.EquipmentSlot);
                    return Result.Failed;
                }
            }
            MarkWeaponChanged(equipment, command.Source.EquipmentSlot);
            changed = true;
            InventorySync.MarkChanged(equipment);
            InventorySync.MarkChanged(target);
            return Result.Accepted;
        }

        private Result InventoryToEquipment(Command command, BaseInventory source, InventoryItem sourceEntry,
            Equipment equipment, out bool changed)
        {
            changed = false;
            if (command.Quantity != 1 || sourceEntry.stackSize != 1) return Result.Invalid;
            Equippable item = sourceEntry.baseItem as Equippable;
            Equippable previous = EquipmentItem(equipment, command.Target.EquipmentSlot, command.Target.WeaponSet);
            if (command.Target.Item != ItemRegistry.NetworkId(previous)) return Result.Invalid;
            try
            {
                EquipNative(equipment, item, command.Target.EquipmentSlot, command.Target.WeaponSet);
            }
            catch
            {
                if (EquipmentItem(equipment, command.Target.EquipmentSlot, command.Target.WeaponSet) == item)
                    UnequipNative(equipment, item, command.Target.EquipmentSlot, command.Target.WeaponSet);
                if (previous != null && EquipmentItem(equipment, command.Target.EquipmentSlot, command.Target.WeaponSet) != previous)
                    EquipNative(equipment, previous, command.Target.EquipmentSlot, command.Target.WeaponSet);
                throw;
            }
            if (EquipmentItem(equipment, command.Target.EquipmentSlot, command.Target.WeaponSet) != item)
            {
                if (previous != null) EquipNative(equipment, previous, command.Target.EquipmentSlot, command.Target.WeaponSet);
                return Result.Failed;
            }
            source.ItemList.Remove(sourceEntry);
            source.NotifyItemRemoved(sourceEntry);
            if (previous != null && previous != item)
            {
                var oldEntry = new InventoryItem(previous, 1)
                {
                    uiSlot = sourceEntry.uiSlot,
                    Original = sourceEntry.Original,
                    AreaLootSource = sourceEntry.AreaLootSource,
                };
                PlaceInInventory(source, oldEntry, sourceEntry.uiSlot, true);
            }
            MarkWeaponChanged(equipment, command.Target.EquipmentSlot);
            changed = true;
            InventorySync.MarkChanged(source);
            InventorySync.MarkChanged(equipment);
            return Result.Accepted;
        }

        private Result EquipmentToEquipment(Command command, Equipment source, Equipment target, Equippable item, out bool changed)
        {
            changed = false;
            Equippable targetItem = EquipmentItem(target, command.Target.EquipmentSlot, command.Target.WeaponSet);
            if (command.Target.Item != ItemRegistry.NetworkId(targetItem) || targetItem == item) return Result.Invalid;
            if (targetItem != null && (!targetItem.CanUnequip()
                || targetItem.WhyCantEquip(source.gameObject) != Equippable.CantEquipReason.None)) return Result.Invalid;
            try
            {
                UnequipNative(source, item, command.Source.EquipmentSlot, command.Source.WeaponSet);
                if (targetItem != null) UnequipNative(target, targetItem, command.Target.EquipmentSlot, command.Target.WeaponSet);
                EquipNative(target, item, command.Target.EquipmentSlot, command.Target.WeaponSet);
                if (targetItem != null) EquipNative(source, targetItem, command.Source.EquipmentSlot, command.Source.WeaponSet);
            }
            catch
            {
                RestoreEquipmentPair(source, item, command.Source.EquipmentSlot, command.Source.WeaponSet,
                    target, targetItem, command.Target.EquipmentSlot, command.Target.WeaponSet);
                throw;
            }
            bool valid = EquipmentItem(target, command.Target.EquipmentSlot, command.Target.WeaponSet) == item
                && (targetItem == null || EquipmentItem(source, command.Source.EquipmentSlot, command.Source.WeaponSet) == targetItem);
            if (!valid)
            {
                RestoreEquipmentPair(source, item, command.Source.EquipmentSlot, command.Source.WeaponSet,
                    target, targetItem, command.Target.EquipmentSlot, command.Target.WeaponSet);
                return Result.Failed;
            }
            MarkWeaponChanged(source, command.Source.EquipmentSlot);
            MarkWeaponChanged(target, command.Target.EquipmentSlot);
            changed = true;
            InventorySync.MarkChanged(source);
            InventorySync.MarkChanged(target);
            return Result.Accepted;
        }

        private static void RestoreEquipmentPair(Equipment source, Equippable sourceItem, int sourceSlot, int sourceSet,
            Equipment target, Equippable targetItem, int targetSlot, int targetSet)
        {
            if (EquipmentItem(target, targetSlot, targetSet) == sourceItem)
                UnequipNative(target, sourceItem, targetSlot, targetSet);
            if (targetItem != null && EquipmentItem(source, sourceSlot, sourceSet) == targetItem)
                UnequipNative(source, targetItem, sourceSlot, sourceSet);
            if (sourceItem != null && EquipmentItem(source, sourceSlot, sourceSet) != sourceItem)
                EquipNative(source, sourceItem, sourceSlot, sourceSet);
            if (targetItem != null && EquipmentItem(target, targetSlot, targetSet) != targetItem)
                EquipNative(target, targetItem, targetSlot, targetSet);
        }

        private Result SelectWeapon(Command command, int playerId, out bool changed)
        {
            changed = false;
            PartyMemberAI member = InventorySync.ResolveMember(command.Actor);
            Equipment equipment = member != null ? member.GetComponent<Equipment>() : null;
            if (member == null || equipment == null) return Result.Invalid;
            if (_ownership.OwnerOf(member) != playerId) return Result.NotOwner;
            if (InventorySync.RevisionOf(equipment) != command.Target.Revision) return Result.Stale;
            if (command.Index < 0 || command.Index >= EquipmentSet.MAX_WEAPON_SETS
                || command.Target.WeaponSet != command.Index) return Result.Invalid;
            Result tactical = CanChangeWeapon(equipment, (int)Equippable.EquipmentSlot.PrimaryWeapon);
            if (tactical != Result.Accepted) return tactical;
            if (!equipment.CanChangeWeaponSets() || !equipment.IsWeaponSetValid(command.Index)) return Result.Unavailable;
            if (equipment.CurrentItems.SelectedWeaponSet == command.Index) return Result.Accepted;
            equipment.SelectWeaponSet(command.Index, true);
            if (equipment.CurrentItems.SelectedWeaponSet != command.Index) return Result.Failed;
            changed = true;
            InventorySync.MarkChanged(equipment);
            return Result.Accepted;
        }

        private Result UseQuickItem(Command command, int playerId, out bool changed)
        {
            changed = false;
            PartyMemberAI member = InventorySync.ResolveMember(command.Actor);
            if (member == null) return Result.Invalid;
            if (_ownership.OwnerOf(member) != playerId) return Result.NotOwner;
            Consumable item = ItemRegistry.Resolve(command.Source.Item) as Consumable;
            BaseInventory inventory = InventorySync.ResolveInventory(command.Source.Inventory);
            if (item == null || inventory == null || (inventory != member.Inventory && inventory != member.QuickbarInventory))
                return Result.Invalid;
            if (InventorySync.RevisionOf(inventory) != command.Source.Revision
                || FindEntry(inventory, item, command.Source.Slot) == null || command.Quantity != 1 || command.Flags > 1)
                return Result.Invalid;
            CharacterStats stats = member.GetComponent<CharacterStats>();
            AIController controller = member.GetComponent<AIController>();
            bool freePotion = false;
            if (stats == null || !item.CanUse(stats)) return Result.Unavailable;
            if (controller != null && TacticalModeManager.IsInTacticalCombat(controller))
            {
                TacticalModeManager manager = TacticalModeManager.Instance;
                freePotion = item.Type == Consumable.ConsumableType.Potion && !controller.WasFreePotionUsed();
                if (manager == null || manager.WhoseTurn != controller || !controller.AvailableAction && !freePotion
                    || controller.StateManager == null || controller.StateManager.CurrentState == null
                    || controller.StateManager.CurrentState.IsPerforming()) return Result.Tactical;
            }
            if (command.Flags != 0 || item.Type == Consumable.ConsumableType.Potion) item.UseImmediately(member.gameObject);
            else item.StartUse(member.gameObject);
            if (freePotion) controller.UseFreePotionAction();
            changed = true;
            InventorySync.MarkChanged(inventory);
            return Result.Accepted;
        }

        private Result EditGrimoire(Command command, int playerId, out bool changed)
        {
            changed = false;
            PartyMemberAI member = InventorySync.ResolveMember(command.Actor);
            CharacterStats stats = member != null ? member.GetComponent<CharacterStats>() : null;
            if (member == null || stats == null) return Result.Invalid;
            if (_ownership.OwnerOf(member) != playerId) return Result.NotOwner;
            if (GameState.InCombat || command.Key.Length == 0 || command.Index < 1 || command.Index > Grimoire.MaxSpellLevel
                || command.Flags > 2) return Result.Unavailable;
            Item item = ItemRegistry.Resolve(command.Source.Item);
            Grimoire grimoire = item != null ? item.GetComponent<Grimoire>() : null;
            if (grimoire == null) return Result.Invalid;
            BaseInventory inventory = InventorySync.ResolveInventory(command.Source.Inventory);
            Equipment equipment = member.GetComponent<Equipment>();
            bool held = inventory != null && (inventory == member.Inventory || inventory == member.QuickbarInventory)
                && FindEntry(inventory, item, command.Source.Slot) != null;
            bool equipped = equipment != null && equipment.CurrentItems.Grimoire == item;
            if (!held && !equipped) return Result.Invalid;
            if (held && InventorySync.RevisionOf(inventory) != command.Source.Revision
                || equipped && InventorySync.RevisionOf(equipment) != command.Source.Revision) return Result.Stale;

            GenericSpell spell = null;
            foreach (GenericAbility ability in stats.ActiveAbilities)
            {
                GenericSpell candidate = ability as GenericSpell;
                if (candidate != null && ItemRegistry.NormalizeKey(candidate.name) == command.Key
                    && candidate.SpellLevel == command.Index && candidate.SpellClass == CharacterStats.Class.Wizard
                    && !candidate.ProhibitFromGrimoire && candidate.MasteryLevel <= 0)
                {
                    spell = candidate;
                    break;
                }
            }
            if (spell == null) return Result.Invalid;
            Grimoire.SpellChapter chapter = grimoire.Spells != null && command.Index <= grimoire.Spells.Length
                ? grimoire.Spells[command.Index - 1] : null;
            if (chapter == null || chapter.SpellData == null || chapter.SpellData.Length != Grimoire.MaxSpellsPerLevel)
                return Result.Invalid;
            int existing = FindSpell(chapter.SpellData, command.Key);
            int operation = command.Flags == 0 ? (existing >= 0 ? 2 : 1) : command.Flags;
            if (operation == 1)
            {
                if (existing >= 0) return Result.Accepted;
                int free = FindSpell(chapter.SpellData, "");
                if (free < 0) return Result.Unavailable;
                GenericSpell prefab = GameResources.LoadPrefab<GenericSpell>(command.Key, false);
                chapter.SpellData[free] = prefab != null ? prefab : spell;
            }
            else
            {
                if (existing < 0) return Result.Accepted;
                chapter.SpellData[existing] = null;
            }
            stats.CoolDownGrimoire();
            changed = true;
            if (held) InventorySync.MarkChanged(inventory);
            if (equipped) InventorySync.MarkChanged(equipment);
            return Result.Accepted;
        }

        private Result Destroy(Command command, int playerId, out bool changed)
        {
            changed = false;
            BaseInventory inventory = InventorySync.ResolveInventory(command.Source.Inventory);
            Item item = ItemRegistry.Resolve(command.Source.Item);
            if (inventory == null || item == null || item.IsQuestItem || command.Quantity <= 0) return Result.Invalid;
            if (!CanAccess(inventory, playerId)) return Result.NotOwner;
            if (inventory is QuestInventory || inventory is CraftingInventory) return Result.Invalid;
            if (InventorySync.RevisionOf(inventory) != command.Source.Revision) return Result.Stale;
            InventoryItem entry = FindEntry(inventory, item, command.Source.Slot);
            if (entry == null || entry.stackSize < command.Quantity) return Result.Invalid;
            if (entry.stackSize > command.Quantity) entry.SetStackSize(entry.stackSize - command.Quantity);
            else
            {
                inventory.ItemList.Remove(entry);
                inventory.NotifyItemRemoved(entry);
                Persistence persistence = item.GetComponent<Persistence>();
                if (persistence != null) PersistenceManager.RemoveObject(persistence);
                GameUtilities.Destroy(item.gameObject);
            }
            changed = true;
            InventorySync.MarkChanged(inventory);
            return Result.Accepted;
        }

        private bool CanAccess(BaseInventory inventory, int playerId)
        {
            if (inventory is StashInventory || inventory is QuestInventory || inventory is CraftingInventory) return true;
            PartyMemberAI member = inventory.GetComponent<PartyMemberAI>();
            return member != null && _ownership.OwnerOf(member) == playerId;
        }

        private static bool CanDeposit(BaseInventory inventory)
        {
            if (inventory is StashInventory || inventory is QuestInventory || inventory is CraftingInventory) return true;
            PartyMemberAI member = inventory.GetComponent<PartyMemberAI>();
            return member != null && System.Linq.Enumerable.Contains(PartyMemberAI.OnlyPrimaryPartyMembers, member);
        }

        private Equipment ResolveCommandEquipment(Location location, Guid commandActor)
        {
            Guid actor = commandActor != Guid.Empty ? commandActor : InventorySync.ActorOf(InventorySync.ResolveInventory(location.Inventory));
            return InventorySync.ResolveEquipment(actor);
        }

        private static void MarkWeaponChanged(Equipment equipment, int slot)
        {
            if (!Equippable.IsWeapon((Equippable.EquipmentSlot)slot)) return;
            AIController controller = equipment.GetComponent<AIController>();
            if (controller != null && TacticalModeManager.IsInTacticalCombat(controller)) controller.SwitchedWeaponsThisTurn = true;
        }

        private static Result CanChangeWeapon(Equipment equipment, int slot)
        {
            if (!Equippable.IsWeapon((Equippable.EquipmentSlot)slot)) return Result.Accepted;
            AIController controller = equipment.GetComponent<AIController>();
            if (controller == null || !TacticalModeManager.IsInTacticalCombat(controller)) return Result.Accepted;
            TacticalModeManager manager = TacticalModeManager.Instance;
            if (manager == null || manager.WhoseTurn != controller || controller.SwitchedWeaponsThisTurn
                || controller.StateManager == null || controller.StateManager.CurrentState == null
                || controller.StateManager.CurrentState.IsPerforming()) return Result.Tactical;
            return Result.Accepted;
        }

        private static Equippable EquipmentItem(Equipment equipment, int slot, int weaponSet)
        {
            if (equipment == null || equipment.CurrentItems == null || slot < 0) return null;
            Equippable.EquipmentSlot equipmentSlot = (Equippable.EquipmentSlot)slot;
            if (equipmentSlot == Equippable.EquipmentSlot.Cape_DEPRECATED) return equipment.CurrentItems.Cape;
            if (!Equippable.IsWeapon(equipmentSlot)) return equipment.CurrentItems.GetItemInSlot(equipmentSlot);
            if (weaponSet < 0 || weaponSet >= equipment.CurrentItems.AlternateWeaponSets.Length) return null;
            Equippable.EquipmentSlot normalized = Equippable.IsPrimaryWeapon(equipmentSlot)
                ? Equippable.EquipmentSlot.PrimaryWeapon : Equippable.EquipmentSlot.SecondaryWeapon;
            return equipment.CurrentItems.GetWeaponInSlot(normalized, weaponSet);
        }

        private static Equippable EquipNative(Equipment equipment, Equippable item, int slot, int weaponSet)
        {
            Equippable.EquipmentSlot equipmentSlot = (Equippable.EquipmentSlot)slot;
            if (Equippable.IsWeapon(equipmentSlot))
            {
                Equippable.EquipmentSlot normalized = Equippable.IsPrimaryWeapon(equipmentSlot)
                    ? Equippable.EquipmentSlot.PrimaryWeapon : Equippable.EquipmentSlot.SecondaryWeapon;
                return equipment.EquipWeapon(item, normalized, weaponSet);
            }
            return equipment.Equip(item, equipmentSlot, true);
        }

        private static Equippable UnequipNative(Equipment equipment, Equippable item, int slot, int weaponSet)
        {
            Equippable.EquipmentSlot equipmentSlot = (Equippable.EquipmentSlot)slot;
            if (Equippable.IsWeapon(equipmentSlot))
            {
                Equippable.EquipmentSlot normalized = Equippable.IsPrimaryWeapon(equipmentSlot)
                    ? Equippable.EquipmentSlot.PrimaryWeapon : Equippable.EquipmentSlot.SecondaryWeapon;
                return equipment.UnEquipWeapon(item, normalized, weaponSet);
            }
            return equipment.UnEquip(item, equipmentSlot);
        }

        private static void RestoreEquipmentVisual(Equipment equipment, Equippable item, int slot)
        {
            item.Location = Item.ItemLocation.Equipped;
            item.AttachToSlot(equipment.gameObject, (Equippable.EquipmentSlot)slot);
        }

        private static InventoryItem FindEntry(BaseInventory inventory, Item item, int slot)
        {
            if (inventory == null || item == null) return null;
            foreach (InventoryItem entry in inventory.ItemList)
                if (entry != null && entry.baseItem == item && (slot < 0 || inventory.InfiniteStacking || entry.uiSlot == slot)) return entry;
            return null;
        }

        private static InventoryItem EntryAtSlot(BaseInventory inventory, int slot)
        {
            foreach (InventoryItem entry in inventory.ItemList)
                if (entry != null && entry.baseItem != null && entry.uiSlot == slot) return entry;
            return null;
        }

        private static void RemoveFromSource(BaseInventory source, InventoryItem entry, int quantity, Item originalItem = null)
        {
            Item item = entry.baseItem;
            Item notificationItem = item != null ? item.Prefab : originalItem;
            bool original = entry.Original;
            if (entry.stackSize > quantity) entry.SetStackSize(entry.stackSize - quantity);
            else
            {
                source.ItemList.Remove(entry);
                source.NotifyItemRemoved(notificationItem, quantity, original);
            }
        }

        private static bool CanSwap(BaseInventory inventory, InventoryItem incoming, InventoryItem outgoing)
        {
            if (inventory == null || incoming == null) return false;
            if (inventory.InfiniteStacking || incoming.stackSize <= incoming.baseItem.MaxStackSize)
                return inventory.MaxItems < 0 || inventory.ItemList.Count <= inventory.MaxItems;
            return false;
        }

        private static void SwapEntries(BaseInventory source, InventoryItem sourceEntry, BaseInventory target, InventoryItem targetEntry)
        {
            int sourceIndex = source.ItemList.IndexOf(sourceEntry);
            int targetIndex = target.ItemList.IndexOf(targetEntry);
            if (sourceIndex < 0 || targetIndex < 0) throw new InvalidOperationException("inventory swap entries missing");
            if (source == target)
            {
                source.ItemList[sourceIndex] = targetEntry;
                source.ItemList[targetIndex] = sourceEntry;
            }
            else
            {
                source.ItemList[sourceIndex] = targetEntry;
                target.ItemList[targetIndex] = sourceEntry;
            }
            int sourceSlot = sourceEntry.uiSlot;
            sourceEntry.uiSlot = targetEntry.uiSlot;
            targetEntry.uiSlot = sourceSlot;
            PlaceInInventory(source, targetEntry, targetEntry.uiSlot, false);
            PlaceInInventory(target, sourceEntry, sourceEntry.uiSlot, false);
            source.NotifyItemRemoved(sourceEntry.baseItem, sourceEntry.stackSize, sourceEntry.Original);
            target.NotifyItemRemoved(targetEntry.baseItem, targetEntry.stackSize, targetEntry.Original);
            source.NotifyItemAdded(targetEntry);
            target.NotifyItemAdded(sourceEntry);
        }

        private static void PlaceInInventory(BaseInventory inventory, InventoryItem entry, int slot, bool notify)
        {
            Item item = entry.baseItem;
            entry.uiSlot = slot;
            item.StoredInventory = inventory;
            item.Location = Item.ItemLocation.Stored;
            item.transform.parent = inventory.transform;
            item.transform.localPosition = Vector3.zero;
            item.transform.localRotation = Quaternion.identity;
            if (item is Equippable equippable) equippable.RecacheTransforms();
            inventory.persistItem(item);
            if (notify)
            {
                if (!inventory.ItemList.Contains(entry)) inventory.ItemList.Add(entry);
                inventory.NotifyItemAdded(entry);
            }
        }

        private static int FindSpell(GenericSpell[] spells, string key)
        {
            for (int i = 0; i < spells.Length; i++)
            {
                if (key.Length == 0 && spells[i] == null) return i;
                if (spells[i] != null && ItemRegistry.NormalizeKey(spells[i].name) == key) return i;
            }
            return -1;
        }

        private PlayerRequests RequestsFor(int playerId)
        {
            if (!_requests.TryGetValue(playerId, out PlayerRequests requests))
                _requests[playerId] = requests = new PlayerRequests();
            return requests;
        }

        private void Complete(PlayerInfo sender, Command command, Result result)
        {
            if (command.RequestId <= 0) return;
            PlayerRequests requests = RequestsFor(sender.Id);
            requests.Receipts[command.RequestId] = new Receipt { Command = command, Result = result };
            requests.Order.Enqueue(command.RequestId);
            while (requests.Order.Count > MaxRememberedRequests)
                requests.Receipts.Remove(requests.Order.Dequeue());
            SendResult(sender, command.Epoch, command.RequestId, (int)command.Kind, result);
        }

        private void SendResult(PlayerInfo sender, long worldEpoch, long requestId, int kind, Result result)
        {
            FlowResultCode wireResult;
            switch (result)
            {
                case Result.Accepted: wireResult = FlowResultCode.Ok; break;
                case Result.Stale: wireResult = FlowResultCode.StaleWorld; break;
                case Result.NotOwner: wireResult = FlowResultCode.Rejected; break;
                case Result.Invalid: wireResult = FlowResultCode.Invalid; break;
                case Result.Unavailable: wireResult = FlowResultCode.NotReady; break;
                case Result.Tactical: wireResult = FlowResultCode.Rejected; break;
                case Result.Conflict: wireResult = FlowResultCode.Conflict; break;
                default: wireResult = FlowResultCode.Failed; break;
            }
            _session.SendToPlayer(sender.Id, FlowWire.WriteResult(worldEpoch,
                requestId, Domain, kind, wireResult, InventorySync.CurrentStateSequence).ToArray());
        }

        private void OnPlayerLeft(PlayerInfo player)
        {
            _requests.Remove(player.Id);
        }

        private static Guid ActorId(PartyMemberAI member)
        {
            if (member == null) return Guid.Empty;
            Guid id = Ownership.IdOf(member);
            return id != Guid.Empty ? id : EntityRegistry.NetworkId(member.gameObject);
        }
    }
}
