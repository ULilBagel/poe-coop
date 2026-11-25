using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>Item identity is deliberately independent of actor EntityRegistry identity.</summary>
    public sealed class ItemRegistry : IDisposable
    {
        internal const int MaxItems = 8192;
        internal const int MaxMods = 32;
        internal const int MaxGrimoireSpells = Grimoire.MaxSpellLevel * Grimoire.MaxSpellsPerLevel;
        private const int MaxKeyLength = 96;
        private const int MaxNameLength = 256;
        private const int MaxCounter = 1000000;
        private const float MaxProgress = 1000000000f;
        private const float PruneInterval = 0.5f;
        private const int MaxCachedKeys = 4096;

        private static readonly AccessTools.FieldRef<InstanceID, Guid> StoredId =
            AccessTools.FieldRefAccess<InstanceID, Guid>("m_Guid");
        private static readonly AccessTools.FieldRef<Consumable, int> UsageCounter =
            AccessTools.FieldRefAccess<Consumable, int>("m_usageCounter");
        private static readonly AccessTools.FieldRef<GenericAbility, int> AbilityCooldownCounter =
            AccessTools.FieldRefAccess<GenericAbility, int>("m_cooldownCounter");
        private static readonly AccessTools.FieldRef<Equippable, GameObject> EquippedOwner =
            AccessTools.FieldRefAccess<Equippable, GameObject>("m_owner");
        private static readonly AccessTools.FieldRef<EquipmentSoulbind, float> SoulbindUnlockProgress =
            AccessTools.FieldRefAccess<EquipmentSoulbind, float>("<UnlockProgress>k__BackingField");
        private static readonly AccessTools.FieldRef<EquipmentSoulbind, float> SoulbindDegenerateProgress =
            AccessTools.FieldRefAccess<EquipmentSoulbind, float>("<DegenerateUnlockProgress>k__BackingField");
        private static readonly AccessTools.FieldRef<EquipmentSoulbind, bool> SoulbindLastDegenerate =
            AccessTools.FieldRefAccess<EquipmentSoulbind, bool>("<WasLastUnlockDegenerate>k__BackingField");
        private static readonly AccessTools.FieldRef<EquipmentSoulbind, CharacterStats.Class> SoulbindClass =
            AccessTools.FieldRefAccess<EquipmentSoulbind, CharacterStats.Class>("<BoundClass>k__BackingField");
        private static readonly AccessTools.FieldRef<EquipmentSoulbind, DatabaseString> SoulbindCachedName =
            AccessTools.FieldRefAccess<EquipmentSoulbind, DatabaseString>("<CachedBoundOwnerName>k__BackingField");
        private static readonly AccessTools.FieldRef<EquipmentSoulbind, string> SoulbindCachedOverride =
            AccessTools.FieldRefAccess<EquipmentSoulbind, string>("<CachedBoundOwnerOverrideName>k__BackingField");
        private static readonly AccessTools.FieldRef<Item, bool> Initted =
            AccessTools.FieldRefAccess<Item, bool>("m_Initted");

        private static ItemRegistry s_instance;
        // Asset names never change, and reading UnityEngine.Object.name allocates a string every time.
        private static readonly Dictionary<UnityEngine.Object, string> s_keys = new Dictionary<UnityEngine.Object, string>();
        private static readonly Dictionary<Guid, Item> s_savedIndex = new Dictionary<Guid, Item>();
        private static int s_savedIndexFrame = -1;

        private readonly Session _session;
        private readonly Dictionary<Guid, Item> _byId = new Dictionary<Guid, Item>();
        private readonly Dictionary<Item, Guid> _byItem = new Dictionary<Item, Guid>();
        private readonly HashSet<Guid> _remoteCreated = new HashSet<Guid>();
        private long _generation;
        private float _nextPrune;

        internal sealed class Recipe
        {
            internal Guid Id;
            internal string PrefabKey = "";
            internal bool HasConsumable;
            internal int ConsumableUsage;
            internal int ConsumableLevel;
            internal bool HasEquippable;
            internal bool HasAddedPrefabMods;
            internal string[] Mods = Array.Empty<string>();
            internal int[] ModCounters = Array.Empty<int>();
            internal bool HasGrimoire;
            internal string GrimoireOwner = "";
            internal string[] Spells = Array.Empty<string>();
            internal bool HasSoulbind;
            internal Guid BoundActor;
            internal int UnlockLevel;
            internal float UnlockProgress;
            internal float DegenerateProgress;
            internal bool LastUnlockDegenerate;
            internal int BoundClass;
            internal int CachedNameTable = -1;
            internal int CachedNameId = -1;
            internal string CachedOwnerOverride = "";
        }

        public ItemRegistry(Session session)
        {
            _session = session;
            _generation = session.Generation;
            s_instance = this;
            GameState.OnLevelLoaded += OnLevelChanged;
            GameState.OnLevelUnload += OnLevelChanged;
        }

        public void Dispose()
        {
            GameState.OnLevelLoaded -= OnLevelChanged;
            GameState.OnLevelUnload -= OnLevelChanged;
            if (s_instance == this) s_instance = null;
            _byId.Clear();
            _byItem.Clear();
            _remoteCreated.Clear();
            ClearCaches();
        }

        public void Update(float now)
        {
            if (_generation != _session.Generation)
            {
                _generation = _session.Generation;
                _byId.Clear();
                _byItem.Clear();
                _remoteCreated.Clear();
                ClearCaches();
            }
            // Every reader of these maps tolerates a destroyed item, so pruning does not need to run each frame.
            if (now < _nextPrune) return;
            _nextPrune = now + PruneInterval;
            List<Item> destroyed = null;
            foreach (Item item in _byItem.Keys)
                if (item == null) (destroyed ?? (destroyed = new List<Item>())).Add(item);
            if (destroyed == null) return;
            foreach (Item item in destroyed)
            {
                if (!_byItem.TryGetValue(item, out Guid id)) continue;
                _byItem.Remove(item);
                if (_byId.TryGetValue(id, out Item mapped) && mapped == item) _byId.Remove(id);
                _remoteCreated.Remove(id);
            }
        }

        /// <summary>Returns the saved item id, or assigns a unique host id to a runtime item.</summary>
        public static Guid NetworkId(Item item)
        {
            if (item == null || item.IsPrefab) return Guid.Empty;
            ItemRegistry self = s_instance;
            if (self == null) return ReadSavedId(item);
            if (self._byItem.TryGetValue(item, out Guid known)) return known;
            return self.Register(item);
        }

        public static Item Resolve(Guid id)
        {
            if (id == Guid.Empty) return null;
            ItemRegistry self = s_instance;
            if (self != null && self._byId.TryGetValue(id, out Item item))
            {
                if (item != null) return item;
                self._byId.Remove(id);
            }
            GameObject obj = InstanceID.GetObjectByID(id);
            item = obj != null ? obj.GetComponent<Item>() : null;
            if (item == null) item = FindInactiveSaved(id);
            if (item != null && self != null) self.Bind(id, item, false);
            return item;
        }

        /// <summary>The live item already bound to this id, without the saved-object fallbacks of Resolve.</summary>
        internal static Item Known(Guid id)
        {
            ItemRegistry self = s_instance;
            return id != Guid.Empty && self != null && self._byId.TryGetValue(id, out Item item) && item != null ? item : null;
        }

        /// <summary>NormalizeKey(asset.name), cached per asset.</summary>
        internal static string KeyOf(UnityEngine.Object asset)
        {
            if (asset == null) return "";
            if (s_keys.TryGetValue(asset, out string key)) return key;
            key = NormalizeKey(asset.name);
            if (s_keys.Count >= MaxCachedKeys) s_keys.Clear();
            s_keys[asset] = key;
            return key;
        }

        private static string PrefabKeyOf(Item item)
        {
            // Item.Prefab answers the item itself when it has no prefab; only real assets are cached.
            Item prefab = item.Prefab;
            return prefab != null && !ReferenceEquals(prefab, item) ? KeyOf(prefab) : NormalizeKey(item.name);
        }

        internal static ulong Mix(ulong hash, ulong value)
        {
            hash = (hash ^ value) * 0x9E3779B97F4A7C15UL;
            return hash ^ (hash >> 32);
        }

        internal static ulong Mix(ulong hash, Guid value)
        {
            var bits = new GuidBits { Value = value };
            return Mix(Mix(hash, bits.Low), bits.High);
        }

        internal static ulong Mix(ulong hash, float value)
        {
            var bits = new FloatBits { Value = value };
            return Mix(hash, bits.Bits);
        }

        internal static ulong Mix(ulong hash, string value)
        {
            return Mix(hash, value != null ? (ulong)(uint)value.GetHashCode() + ((ulong)value.Length << 32) : ulong.MaxValue);
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct GuidBits
        {
            [System.Runtime.InteropServices.FieldOffset(0)] internal Guid Value;
            [System.Runtime.InteropServices.FieldOffset(0)] internal ulong Low;
            [System.Runtime.InteropServices.FieldOffset(8)] internal ulong High;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatBits
        {
            [System.Runtime.InteropServices.FieldOffset(0)] internal float Value;
            [System.Runtime.InteropServices.FieldOffset(0)] internal uint Bits;
        }

        /// <summary>
        /// Folds everything Capture would put into a recipe into the hash without allocating. Returns false,
        /// leaving the hash untouched, exactly when Capture would return null.
        /// </summary>
        internal static bool MixState(Item item, ref ulong hash)
        {
            Guid id = NetworkId(item);
            if (id == Guid.Empty || item == null) return false;
            string prefabKey = PrefabKeyOf(item);
            if (prefabKey.Length == 0) return false;
            ulong h = Mix(Mix(hash, id), prefabKey);

            Consumable consumable = item as Consumable;
            if (consumable != null) h = Mix(Mix(Mix(h, 1UL), (ulong)UsageCounter(consumable)), (ulong)consumable.Level);

            Equippable equippable = item as Equippable;
            if (equippable != null)
            {
                h = Mix(h, equippable.HasAddedPrefabMods ? 3UL : 2UL);
                List<ItemModComponent> attached = equippable.AttachedItemMods;
                int mods = 0;
                for (int i = 0; attached != null && i < attached.Count; i++)
                {
                    ItemModComponent component = attached[i];
                    if (component == null || component.Mod == null || mods >= MaxMods) continue;
                    mods++;
                    GenericAbility ability = component.ChargeAbility;
                    h = Mix(Mix(h, KeyOf(component.Mod)), (ulong)(ability != null ? AbilityCooldownCounter(ability) : 0));
                }
                h = Mix(h, (ulong)mods);
            }

            Grimoire grimoire = item.GetComponent<Grimoire>();
            if (grimoire != null)
            {
                h = Mix(Mix(h, 4UL), grimoire.PrimaryOwnerName);
                if (grimoire.Spells != null)
                {
                    for (int chapter = 0; chapter < grimoire.Spells.Length && chapter < Grimoire.MaxSpellLevel; chapter++)
                    {
                        Grimoire.SpellChapter spellChapter = grimoire.Spells[chapter];
                        for (int slot = 0; slot < Grimoire.MaxSpellsPerLevel; slot++)
                        {
                            GenericSpell spell = spellChapter != null && spellChapter.SpellData != null
                                && slot < spellChapter.SpellData.Length ? spellChapter.SpellData[slot] : null;
                            h = Mix(h, spell != null ? KeyOf(spell) : "");
                        }
                    }
                }
            }

            EquipmentSoulbind soulbind = item.GetComponent<EquipmentSoulbind>();
            if (soulbind != null)
            {
                h = Mix(Mix(Mix(h, 5UL), soulbind.BoundGuid), (ulong)soulbind.UnlockLevel);
                h = Mix(Mix(h, soulbind.UnlockProgress), soulbind.DegenerateUnlockProgress);
                h = Mix(Mix(h, soulbind.WasLastUnlockDegenerate ? 1UL : 0UL), (ulong)(int)soulbind.BoundClass);
                DatabaseString name = soulbind.CachedBoundOwnerName;
                h = Mix(Mix(h, (ulong)(name != null ? name.StringTableID : -1)), (ulong)(name != null ? name.StringID : -1));
                h = Mix(h, soulbind.CachedBoundOwnerOverrideName);
            }
            hash = h;
            return true;
        }

        /// <summary>
        /// True when applying the recipe to this live item would change nothing, so the caller may skip it.
        /// Mirrors ApplyRecipe field by field; any doubt answers false.
        /// </summary>
        internal static bool Matches(Item item, Recipe recipe)
        {
            if (item == null || recipe == null) return false;
            if (recipe.HasConsumable && item is Consumable consumable
                && (UsageCounter(consumable) != recipe.ConsumableUsage || consumable.Level != recipe.ConsumableLevel)) return false;
            if (recipe.HasEquippable && item is Equippable equippable)
            {
                List<ItemModComponent> attached = equippable.AttachedItemMods;
                if (!Initted(equippable) || attached == null
                    || equippable.HasAddedPrefabMods != (recipe.HasAddedPrefabMods || recipe.Mods.Length > 0)) return false;
                for (int i = 0; i < attached.Count; i++)
                {
                    ItemModComponent component = attached[i];
                    if (component == null || component.Mod == null) continue;
                    if (Array.IndexOf(recipe.Mods, KeyOf(component.Mod)) < 0) return false;
                }
                for (int i = 0; i < recipe.Mods.Length; i++)
                {
                    ItemModComponent component = FindMod(equippable, recipe.Mods[i]);
                    if (component == null) return false;
                    GenericAbility ability = component.ChargeAbility;
                    if (ability != null && AbilityCooldownCounter(ability) != recipe.ModCounters[i]) return false;
                }
            }
            if (recipe.HasGrimoire)
            {
                Grimoire grimoire = item.GetComponent<Grimoire>();
                if (grimoire != null)
                {
                    if (!string.Equals(grimoire.PrimaryOwnerName, recipe.GrimoireOwner, StringComparison.Ordinal)
                        || grimoire.Spells == null || grimoire.Spells.Length != Grimoire.MaxSpellLevel
                        || recipe.Spells.Length != MaxGrimoireSpells) return false;
                    for (int chapter = 0; chapter < Grimoire.MaxSpellLevel; chapter++)
                    {
                        Grimoire.SpellChapter spellChapter = grimoire.Spells[chapter];
                        if (spellChapter == null || spellChapter.SpellData == null
                            || spellChapter.SpellData.Length != Grimoire.MaxSpellsPerLevel) return false;
                        for (int slot = 0; slot < Grimoire.MaxSpellsPerLevel; slot++)
                        {
                            GenericSpell spell = spellChapter.SpellData[slot];
                            string key = recipe.Spells[chapter * Grimoire.MaxSpellsPerLevel + slot];
                            if (key.Length == 0 ? !ReferenceEquals(spell, null) : spell == null || KeyOf(spell) != key) return false;
                        }
                    }
                }
            }
            if (recipe.HasSoulbind)
            {
                EquipmentSoulbind soulbind = item.GetComponent<EquipmentSoulbind>();
                if (soulbind != null)
                {
                    DatabaseString name = soulbind.CachedBoundOwnerName;
                    if (soulbind.BoundGuid != recipe.BoundActor || soulbind.UnlockLevel != recipe.UnlockLevel
                        || soulbind.UnlockProgress != recipe.UnlockProgress
                        || soulbind.DegenerateUnlockProgress != recipe.DegenerateProgress
                        || soulbind.WasLastUnlockDegenerate != recipe.LastUnlockDegenerate
                        || (int)soulbind.BoundClass != recipe.BoundClass
                        || (recipe.CachedNameTable < 0 ? name != null : name == null
                            || name.StringTableID != recipe.CachedNameTable || name.StringID != recipe.CachedNameId)
                        || !string.Equals(soulbind.CachedBoundOwnerOverrideName, recipe.CachedOwnerOverride, StringComparison.Ordinal))
                        return false;
                }
            }
            return true;
        }

        internal static Recipe Capture(Item item)
        {
            Guid id = NetworkId(item);
            if (id == Guid.Empty || item == null) return null;
            var recipe = new Recipe
            {
                Id = id,
                PrefabKey = PrefabKeyOf(item),
            };

            Consumable consumable = item as Consumable;
            if (consumable != null)
            {
                recipe.HasConsumable = true;
                recipe.ConsumableUsage = UsageCounter(consumable);
                recipe.ConsumableLevel = consumable.Level;
            }

            Equippable equippable = item as Equippable;
            if (equippable != null)
            {
                recipe.HasEquippable = true;
                recipe.HasAddedPrefabMods = equippable.HasAddedPrefabMods;
                var mods = new List<string>();
                var counters = new List<int>();
                foreach (ItemModComponent component in equippable.AttachedItemMods)
                {
                    if (component == null || component.Mod == null || mods.Count >= MaxMods) continue;
                    mods.Add(KeyOf(component.Mod));
                    GenericAbility ability = component.ChargeAbility;
                    counters.Add(ability != null ? AbilityCooldownCounter(ability) : 0);
                }
                recipe.Mods = mods.ToArray();
                recipe.ModCounters = counters.ToArray();
            }

            Grimoire grimoire = item.GetComponent<Grimoire>();
            if (grimoire != null)
            {
                recipe.HasGrimoire = true;
                recipe.GrimoireOwner = Protocol.Clean(grimoire.PrimaryOwnerName, MaxNameLength);
                var spells = new List<string>(MaxGrimoireSpells);
                if (grimoire.Spells != null)
                {
                    for (int chapter = 0; chapter < grimoire.Spells.Length && chapter < Grimoire.MaxSpellLevel; chapter++)
                    {
                        Grimoire.SpellChapter spellChapter = grimoire.Spells[chapter];
                        for (int slot = 0; slot < Grimoire.MaxSpellsPerLevel; slot++)
                        {
                            GenericSpell spell = spellChapter != null && spellChapter.SpellData != null
                                && slot < spellChapter.SpellData.Length ? spellChapter.SpellData[slot] : null;
                            spells.Add(spell != null ? KeyOf(spell) : "");
                        }
                    }
                }
                while (spells.Count < MaxGrimoireSpells) spells.Add("");
                recipe.Spells = spells.ToArray();
            }

            EquipmentSoulbind soulbind = item.GetComponent<EquipmentSoulbind>();
            if (soulbind != null)
            {
                recipe.HasSoulbind = true;
                recipe.BoundActor = soulbind.BoundGuid;
                recipe.UnlockLevel = soulbind.UnlockLevel;
                recipe.UnlockProgress = soulbind.UnlockProgress;
                recipe.DegenerateProgress = soulbind.DegenerateUnlockProgress;
                recipe.LastUnlockDegenerate = soulbind.WasLastUnlockDegenerate;
                recipe.BoundClass = (int)soulbind.BoundClass;
                if (soulbind.CachedBoundOwnerName != null)
                {
                    recipe.CachedNameTable = soulbind.CachedBoundOwnerName.StringTableID;
                    recipe.CachedNameId = soulbind.CachedBoundOwnerName.StringID;
                }
                recipe.CachedOwnerOverride = Protocol.Clean(soulbind.CachedBoundOwnerOverrideName, MaxNameLength);
            }
            return recipe.PrefabKey.Length == 0 ? null : recipe;
        }

        internal static void WriteRecipe(MessageWriter writer, Recipe recipe)
        {
            writer.Guid(recipe.Id).String(recipe.PrefabKey);
            writer.Byte((byte)(recipe.HasConsumable ? 1 : 0));
            if (recipe.HasConsumable) writer.Int(recipe.ConsumableUsage).Int(recipe.ConsumableLevel);
            writer.Byte((byte)(recipe.HasEquippable ? 1 : 0));
            if (recipe.HasEquippable)
            {
                writer.Byte((byte)(recipe.HasAddedPrefabMods ? 1 : 0)).Int(recipe.Mods.Length);
                for (int i = 0; i < recipe.Mods.Length; i++) writer.String(recipe.Mods[i]).Int(recipe.ModCounters[i]);
            }
            writer.Byte((byte)(recipe.HasGrimoire ? 1 : 0));
            if (recipe.HasGrimoire)
            {
                writer.String(recipe.GrimoireOwner).Int(recipe.Spells.Length);
                foreach (string spell in recipe.Spells) writer.String(spell);
            }
            writer.Byte((byte)(recipe.HasSoulbind ? 1 : 0));
            if (recipe.HasSoulbind)
            {
                writer.Guid(recipe.BoundActor).Int(recipe.UnlockLevel).Float(recipe.UnlockProgress)
                    .Float(recipe.DegenerateProgress).Byte((byte)(recipe.LastUnlockDegenerate ? 1 : 0))
                    .Int(recipe.BoundClass).Int(recipe.CachedNameTable).Int(recipe.CachedNameId)
                    .String(recipe.CachedOwnerOverride);
            }
        }

        internal static Recipe ReadRecipe(MessageReader reader)
        {
            var recipe = new Recipe
            {
                Id = reader.Guid(),
                PrefabKey = ReadKey(reader),
            };
            if (recipe.Id == Guid.Empty || recipe.PrefabKey.Length == 0) throw new FormatException("item recipe identity");

            int flag = reader.Byte();
            if (flag > 1) throw new FormatException("item consumable flag");
            recipe.HasConsumable = flag != 0;
            if (recipe.HasConsumable)
            {
                recipe.ConsumableUsage = BoundedCounter(reader.Int(), "consumable usage");
                recipe.ConsumableLevel = BoundedCounter(reader.Int(), "consumable level");
            }

            flag = reader.Byte();
            if (flag > 1) throw new FormatException("item equippable flag");
            recipe.HasEquippable = flag != 0;
            if (recipe.HasEquippable)
            {
                int added = reader.Byte();
                if (added > 1) throw new FormatException("item prefab-mod flag");
                recipe.HasAddedPrefabMods = added != 0;
                int count = reader.Int();
                if (count < 0 || count > MaxMods) throw new FormatException("item mod count " + count);
                recipe.Mods = new string[count];
                recipe.ModCounters = new int[count];
                for (int i = 0; i < count; i++)
                {
                    recipe.Mods[i] = ReadKey(reader);
                    if (recipe.Mods[i].Length == 0) throw new FormatException("empty item mod");
                    recipe.ModCounters[i] = BoundedCounter(reader.Int(), "item mod counter");
                }
            }

            flag = reader.Byte();
            if (flag > 1) throw new FormatException("item grimoire flag");
            recipe.HasGrimoire = flag != 0;
            if (recipe.HasGrimoire)
            {
                recipe.GrimoireOwner = Protocol.Clean(reader.String(), MaxNameLength);
                int count = reader.Int();
                if (count != MaxGrimoireSpells) throw new FormatException("grimoire spell count " + count);
                recipe.Spells = new string[count];
                for (int i = 0; i < count; i++) recipe.Spells[i] = ReadKey(reader);
            }

            flag = reader.Byte();
            if (flag > 1) throw new FormatException("item soulbind flag");
            recipe.HasSoulbind = flag != 0;
            if (recipe.HasSoulbind)
            {
                recipe.BoundActor = reader.Guid();
                recipe.UnlockLevel = reader.Int();
                if (recipe.UnlockLevel < -1 || recipe.UnlockLevel > 64) throw new FormatException("soulbind level");
                recipe.UnlockProgress = BoundedFloat(reader.Float());
                recipe.DegenerateProgress = BoundedFloat(reader.Float());
                int degenerate = reader.Byte();
                if (degenerate > 1) throw new FormatException("soulbind degenerate flag");
                recipe.LastUnlockDegenerate = degenerate != 0;
                recipe.BoundClass = reader.Int();
                if (recipe.BoundClass < 0 || recipe.BoundClass > 64) throw new FormatException("soulbind class");
                recipe.CachedNameTable = reader.Int();
                recipe.CachedNameId = reader.Int();
                if (recipe.CachedNameTable < -1 || recipe.CachedNameTable > 10000 || recipe.CachedNameId < -1)
                    throw new FormatException("soulbind cached name");
                recipe.CachedOwnerOverride = Protocol.Clean(reader.String(), MaxNameLength);
            }
            return recipe;
        }

        internal static Item ResolveOrCreate(Recipe recipe)
        {
            return ResolveOrCreate(recipe, false);
        }

        internal static Item RestoreHost(Recipe recipe)
        {
            return ResolveOrCreate(recipe, true);
        }

        private static Item ResolveOrCreate(Recipe recipe, bool hostRestore)
        {
            if (recipe == null) return null;
            Item item = Resolve(recipe.Id);
            if (item != null)
            {
                if (!Matches(item, recipe)) ApplyRecipe(item, recipe);
                return item;
            }
            ItemRegistry self = s_instance;
            if (self == null || (!hostRestore && !self._session.IsGuest) || (hostRestore && !self._session.IsHost)
                || GameState.IsLoading || !GameState.IsInGameSession) return null;
            if (self._byId.Count >= MaxItems) return null;

            Item prefab = GameResources.LoadPrefab<Item>(recipe.PrefabKey, false);
            if (prefab == null) return null;
            item = GameResources.Instantiate<Item>(prefab);
            if (item == null) return null;
            item.Prefab = prefab;
            self.Bind(recipe.Id, item, !hostRestore);
            ApplyRecipe(item, recipe);
            return item;
        }

        private static void ApplyRecipe(Item item, Recipe recipe)
        {
            if (recipe.HasConsumable && item is Consumable consumable)
            {
                UsageCounter(consumable) = recipe.ConsumableUsage;
                consumable.Level = recipe.ConsumableLevel;
            }
            if (recipe.HasEquippable && item is Equippable equippable)
            {
                GameObject owner = EquippedOwner(equippable);
                EquippedOwner(equippable) = null;
                try
                {
                    equippable.Init();
                    var wanted = new HashSet<string>(recipe.Mods, StringComparer.Ordinal);
                    for (int i = equippable.AttachedItemMods.Count - 1; i >= 0; i--)
                    {
                        ItemModComponent attached = equippable.AttachedItemMods[i];
                        if (attached == null || attached.Mod == null) continue;
                        if (!wanted.Contains(KeyOf(attached.Mod))) equippable.DestroyItemMod(attached);
                    }
                    for (int i = 0; i < recipe.Mods.Length; i++)
                    {
                        ItemModComponent attached = FindMod(equippable, recipe.Mods[i]);
                        if (attached == null)
                        {
                            ItemMod mod = GameResources.LoadPrefab<ItemMod>(recipe.Mods[i], false);
                            if (mod != null)
                            {
                                equippable.AttachItemMod(mod);
                                attached = FindMod(equippable, recipe.Mods[i]);
                            }
                        }
                        GenericAbility ability = attached != null ? attached.ChargeAbility : null;
                        if (ability != null) AbilityCooldownCounter(ability) = recipe.ModCounters[i];
                    }
                    equippable.HasAddedPrefabMods = recipe.HasAddedPrefabMods || recipe.Mods.Length > 0;
                }
                finally
                {
                    EquippedOwner(equippable) = owner;
                }
            }
            if (recipe.HasGrimoire)
            {
                Grimoire grimoire = item.GetComponent<Grimoire>();
                if (grimoire != null)
                {
                    grimoire.PrimaryOwnerName = recipe.GrimoireOwner;
                    if (grimoire.Spells == null || grimoire.Spells.Length != Grimoire.MaxSpellLevel)
                        grimoire.Spells = new Grimoire.SpellChapter[Grimoire.MaxSpellLevel];
                    for (int chapter = 0; chapter < Grimoire.MaxSpellLevel; chapter++)
                    {
                        if (grimoire.Spells[chapter] == null) grimoire.Spells[chapter] = new Grimoire.SpellChapter();
                        if (grimoire.Spells[chapter].SpellData == null
                            || grimoire.Spells[chapter].SpellData.Length != Grimoire.MaxSpellsPerLevel)
                            grimoire.Spells[chapter].SpellData = new GenericSpell[Grimoire.MaxSpellsPerLevel];
                        for (int slot = 0; slot < Grimoire.MaxSpellsPerLevel; slot++)
                        {
                            string key = recipe.Spells[chapter * Grimoire.MaxSpellsPerLevel + slot];
                            grimoire.Spells[chapter].SpellData[slot] = key.Length == 0
                                ? null : GameResources.LoadPrefab<GenericSpell>(key, false);
                        }
                    }
                }
            }
            if (recipe.HasSoulbind)
            {
                EquipmentSoulbind soulbind = item.GetComponent<EquipmentSoulbind>();
                if (soulbind != null)
                {
                    soulbind.BoundGuid = recipe.BoundActor;
                    soulbind.UnlockLevel = recipe.UnlockLevel;
                    SoulbindUnlockProgress(soulbind) = recipe.UnlockProgress;
                    SoulbindDegenerateProgress(soulbind) = recipe.DegenerateProgress;
                    SoulbindLastDegenerate(soulbind) = recipe.LastUnlockDegenerate;
                    SoulbindClass(soulbind) = (CharacterStats.Class)recipe.BoundClass;
                    SoulbindCachedName(soulbind) = recipe.CachedNameTable < 0 ? null : new DatabaseString(
                        (DatabaseString.StringTableType)recipe.CachedNameTable, recipe.CachedNameId);
                    SoulbindCachedOverride(soulbind) = recipe.CachedOwnerOverride;
                    soulbind.Restored();
                }
            }
        }

        private static ItemModComponent FindMod(Equippable item, string key)
        {
            foreach (ItemModComponent component in item.AttachedItemMods)
                if (component != null && component.Mod != null && KeyOf(component.Mod) == key) return component;
            return null;
        }

        private Guid Register(Item item)
        {
            if (!_session.IsHost) return ReadSavedId(item);
            Guid id = ReadSavedId(item);
            if (id != Guid.Empty && _byId.TryGetValue(id, out Item collision) && collision != null && collision != item)
                id = Guid.Empty;
            if (id == Guid.Empty) id = Guid.NewGuid();
            if (_byId.Count >= MaxItems && !_byId.ContainsKey(id)) return Guid.Empty;
            InstanceID instance = item.GetComponent<InstanceID>();
            if (instance == null) instance = item.gameObject.AddComponent<InstanceID>();
            if (ReadSavedId(item) != id) instance.Guid = id;
            Bind(id, item, false);
            Persistence persistence = item.GetComponent<Persistence>();
            if (persistence != null && !GameState.IsLoading)
            {
                persistence.UnloadsBetweenLevels = false;
                persistence.SaveObject();
            }
            return id;
        }

        private void Bind(Guid id, Item item, bool remoteCreated)
        {
            if (id == Guid.Empty || item == null) return;
            if (_byItem.TryGetValue(item, out Guid previous) && previous != id)
            {
                _byItem.Remove(item);
                if (_byId.TryGetValue(previous, out Item old) && old == item) _byId.Remove(previous);
            }
            if (_byId.TryGetValue(id, out Item collision) && collision != null && collision != item) return;
            _byId[id] = item;
            _byItem[item] = id;
            if (remoteCreated) _remoteCreated.Add(id);
            InstanceID instance = item.GetComponent<InstanceID>();
            if (instance == null) instance = item.gameObject.AddComponent<InstanceID>();
            if (ReadSavedId(item) != id) instance.Guid = id;
        }

        /// <summary>
        /// Finds a saved item that is not in the game's active id table. The scan of every loaded item is
        /// shared by all lookups of one frame; a manifest of unknown items used to pay for it once per item.
        /// </summary>
        private static Item FindInactiveSaved(Guid id)
        {
            int frame = Time.frameCount;
            if (s_savedIndexFrame != frame)
            {
                long started = PerfStats.Begin();
                s_savedIndexFrame = frame;
                s_savedIndex.Clear();
                Item[] all = Resources.FindObjectsOfTypeAll<Item>();
                foreach (Item item in all)
                {
                    if (item == null || item.IsPrefab || !item.gameObject.scene.IsValid()) continue;
                    Guid saved = ReadSavedId(item);
                    if (saved != Guid.Empty && !s_savedIndex.ContainsKey(saved)) s_savedIndex[saved] = item;
                }
                PerfStats.End("inventory.scan", started);
            }
            return s_savedIndex.TryGetValue(id, out Item found) && found != null ? found : null;
        }

        private static void ClearCaches()
        {
            s_keys.Clear();
            s_savedIndex.Clear();
            s_savedIndexFrame = -1;
        }

        private static Guid ReadSavedId(Item item)
        {
            InstanceID instance = item != null ? item.GetComponent<InstanceID>() : null;
            if (instance == null) return Guid.Empty;
            Guid id = StoredId(instance);
            if (id != Guid.Empty) return id;
            return !string.IsNullOrEmpty(instance.UniqueID) && Guid.TryParse(instance.UniqueID, out id) ? id : Guid.Empty;
        }

        private void OnLevelChanged(object sender, EventArgs e)
        {
            ClearCaches();
            List<Guid> missing = null;
            foreach (var pair in _byId)
                if (pair.Value == null) (missing ?? (missing = new List<Guid>())).Add(pair.Key);
            if (missing == null) return;
            foreach (Guid id in missing)
            {
                if (_byId.TryGetValue(id, out Item item) && item != null) _byItem.Remove(item);
                _byId.Remove(id);
                _remoteCreated.Remove(id);
            }
        }

        private static string ReadKey(MessageReader reader)
        {
            return NormalizeKey(Protocol.Clean(reader.String(), MaxKeyLength));
        }

        internal static string NormalizeKey(string name)
        {
            name = Protocol.Clean((name ?? "").Replace("(Clone)", ""), MaxKeyLength);
            return Path.GetFileNameWithoutExtension(name).Trim().ToLowerInvariant();
        }

        private static int BoundedCounter(int value, string name)
        {
            if (value < 0 || value > MaxCounter) throw new FormatException(name + " " + value);
            return value;
        }

        private static float BoundedFloat(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > MaxProgress)
                throw new FormatException("item progress out of range");
            return value;
        }
    }
}
