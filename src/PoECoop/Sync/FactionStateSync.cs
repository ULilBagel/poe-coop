using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>
    /// Replicates the host's team relationships and per-entity faction state to
    /// guests without running faction-change scripts there. See docs/PLAYTEST-ISSUES.md.
    ///
    /// Native hostility is derived from three things only: the entity's team
    /// (Faction.m_myTeam), its personal "hostile to the player" flag
    /// (Faction.m_unitHostileToPlayer, which the game does not save) and the three
    /// relationship sets on each registered Team. The host sends all three; the
    /// guest writes them as backing state, never through the native setters.
    /// </summary>
    public sealed class FactionStateSync
    {
        /// <summary>Host: how often live factions are compared with what guests were told.</summary>
        private const float ScanInterval = 0.5f;
        /// <summary>Host: shortest gap between two scans started by a native mutator.</summary>
        private const float DirtyScanInterval = 0.1f;
        /// <summary>
        /// Both streams have a complete delta path on a reliable ordered transport and
        /// the guest re-asserts its stored host state itself, so this is integrity only.
        /// </summary>
        private const float FullRefreshInterval = 10f;
        /// <summary>Keeps the first periodic refresh after a forced one off the other streams' frames.</summary>
        private const float ForcedRefreshPhase = 1.5f;
        /// <summary>Guest: how often stored host state is re-asserted on local objects.</summary>
        private const float VerifyInterval = 0.5f;
        private const float TeamVerifyInterval = 3f;
        private const float ProbeInterval = 0.5f;

        private const int MaxRecords = 4096;
        private const int MaxTeams = 1024;
        private const int MaxTeamNames = 4096;
        private const int MaxTagLength = 128;
        private const int EntityRecordBytes = 22;

        private const byte FlagUnitHostile = 1;
        private const byte FlagHasTeam = 2;
        private const byte KnownFlags = FlagUnitHostile | FlagHasTeam;
        private const byte NoRelationship = 255;

        private static readonly AccessTools.FieldRef<Faction, Team> MyTeam =
            AccessTools.FieldRefAccess<Faction, Team>("m_myTeam");
        private static readonly AccessTools.FieldRef<Faction, bool> UnitHostile =
            AccessTools.FieldRefAccess<Faction, bool>("m_unitHostileToPlayer");

        private static FactionStateSync s_instance;
        private static bool s_dirty;
        private static readonly string s_probeFile = ProbeArgument();

        private readonly Session _session;
        private bool _active;

        // ---- host ----

        private sealed class Sent
        {
            public Guid Id;
            public byte Flags, Relationship;
            public int TeamHash, Stamp;
        }

        private readonly Dictionary<Faction, Sent> _sent = new Dictionary<Faction, Sent>();
        private readonly List<Sent> _changed = new List<Sent>();
        private readonly List<Faction> _gone = new List<Faction>();
        private readonly HashSet<Guid> _fullIds = new HashSet<Guid>();
        private readonly Dictionary<int, int> _nameIndex = new Dictionary<int, int>();
        private readonly List<string> _names = new List<string>();
        private long _hostEpoch;
        private int _entityRevision, _teamRevision, _stamp;
        private long _sentTeamSignature;
        private bool _hasSentTeams, _forceFull, _warnedOverflow;
        private float _nextScan, _nextDirtyScan, _nextFull;

        // ---- guest ----

        private sealed class Wanted
        {
            public Guid Id;
            public byte Flags, Relationship;
            public int TeamHash;
            public Faction Faction;
            public bool Applied, Reported;
        }

        private sealed class TeamRow
        {
            public string Tag;
            public int Hash;
            public byte Default;
            public int[][] Members; // by Faction.Relationship: tag hashes
        }

        private readonly Dictionary<Guid, Wanted> _wanted = new Dictionary<Guid, Wanted>();
        private readonly List<TeamRow> _teamRows = new List<TeamRow>();
        private readonly Dictionary<int, string> _tagByHash = new Dictionary<int, string>();
        private readonly Dictionary<int, Team> _teamByHash = new Dictionary<int, Team>();
        private readonly Dictionary<int, Team> _placeholders = new Dictionary<int, Team>();
        private readonly Dictionary<int, Team> _assetByHash = new Dictionary<int, Team>();
        private readonly List<Team> _memberBuffer = new List<Team>();
        private long _guestEpoch;
        private int _guestEntityRevision, _guestTeamRevision;
        private bool _hasTeamRows, _assetsSearched;
        private int _pending, _mismatched;
        private float _nextVerify, _nextTeamVerify, _nextProbe;

        public FactionStateSync(Session session)
        {
            _session = session;
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
            Clear();
            if (s_instance == this) s_instance = null;
        }

        /// <summary>A native faction or team mutator ran (Patches/FactionPatches.cs): look again soon.</summary>
        internal static void MarkDirty() => s_dirty = true;

        private void OnPlayerJoined(PlayerInfo player)
        {
            if (_session.IsHost) _forceFull = true;
        }

        private void OnWorldStateReady(PlayerInfo player, long worldEpoch)
        {
            if (_session.IsHost) _forceFull = true;
            else s_dirty = true;
        }

        private void OnFullStateRequested(int playerId, long worldEpoch)
        {
            if (_session.IsHost) _forceFull = true;
        }

        private void OnSessionReset() => Clear();

        private void OnLevelChanged(object sender, EventArgs e)
        {
            // Teams and factions of the old level are gone or about to be rebuilt. The
            // host's next epoch starts from a full state; the guest waits for it.
            ClearHost();
            ClearGuest();
        }

        private void Clear()
        {
            _active = false;
            ClearHost();
            ClearGuest();
        }

        private void ClearHost()
        {
            _sent.Clear();
            _changed.Clear();
            _gone.Clear();
            _hostEpoch = 0;
            // Revisions only ever grow, so a reset here can never make a guest in the
            // same world discard what follows as stale.
            _hasSentTeams = false;
            _forceFull = true;
            _nextScan = 0f;
            _nextDirtyScan = 0f;
            _nextFull = 0f;
        }

        private void ClearGuest()
        {
            _wanted.Clear();
            _teamRows.Clear();
            _tagByHash.Clear();
            _teamByHash.Clear();
            // Stand-in teams may still be referenced by the game's team table; they are
            // dropped, not destroyed. The game clears that table itself on the next load.
            _placeholders.Clear();
            _assetByHash.Clear();
            _hasTeamRows = false;
            _assetsSearched = false;
            _guestEpoch = 0;
            _guestEntityRevision = 0;
            _guestTeamRevision = 0;
            _pending = 0;
            _mismatched = 0;
        }

        public void Update(float now)
        {
            if (_session.IsHost)
            {
                _active = true;
                UpdateHost(now);
            }
            else if (_session.IsGuest)
            {
                _active = true;
                UpdateGuest(now);
            }
            else
            {
                if (_active) Clear();
                return;
            }
            if (s_probeFile != null && now >= _nextProbe)
            {
                _nextProbe = now + ProbeInterval;
                WriteProbe();
            }
        }

        // ---- host: capture ----

        private void UpdateHost(float now)
        {
            if (_session.Players.Count < 2 || !GameState.IsInGameSession || GameState.IsLoading) return;
            long epoch = FlowCoordinator.WorldEpoch;
            if (epoch <= 0) return;
            if (epoch != _hostEpoch)
            {
                ClearHost();
                _hostEpoch = epoch;
            }

            bool forced = _forceFull;
            bool full = forced || now >= _nextFull;
            if (!full)
            {
                if (s_dirty ? now < _nextDirtyScan : now < _nextScan) return;
            }
            else
            {
                _nextFull = now + FullRefreshInterval + (forced ? ForcedRefreshPhase : 0f);
                _forceFull = false;
            }
            s_dirty = false;
            _nextScan = now + ScanInterval;
            _nextDirtyScan = now + DirtyScanInterval;

            long started = PerfStats.Begin();
            // Teams first: an entity record names its team by tag hash, and the guest
            // learns the tag from the team table.
            CaptureTeams(full);
            CaptureEntities(full);
            PerfStats.End("faction.capture", started);
        }

        private void CaptureTeams(bool full)
        {
            Dictionary<string, Team> teams = Team.LoadedTeams;
            long signature = teams.Count;
            foreach (Team team in teams.Values)
            {
                if (team == null) continue;
                long row = Mix(team.ScriptTagHash, 3 + (int)team.DefaultRelationship);
                row += SetSignature(team.HostileTeamSet, Faction.Relationship.Hostile);
                row += SetSignature(team.NeutralTeamSet, Faction.Relationship.Neutral);
                row += SetSignature(team.FriendlyTeamSet, Faction.Relationship.Friendly);
                signature += Mix((int)row, (int)(row >> 32));
            }
            if (!full && _hasSentTeams && signature == _sentTeamSignature) return;

            _names.Clear();
            _nameIndex.Clear();
            int teamCount = 0;
            foreach (Team team in teams.Values)
            {
                if (!Sendable(team)) continue;
                if (teamCount >= MaxTeams) { WarnOverflow("teams"); break; }
                teamCount++;
                NameIndex(team);
            }
            // Row bodies are written after the name table, so they go to their own writer first.
            var rows = new MessageWriter(MessageType.FactionTeams);
            int written = 0;
            foreach (Team team in teams.Values)
            {
                if (!Sendable(team)) continue;
                if (written >= teamCount) break;
                written++;
                rows.Int(NameIndex(team)).Byte((byte)team.DefaultRelationship);
                WriteMembers(rows, team.HostileTeamSet);
                WriteMembers(rows, team.NeutralTeamSet);
                WriteMembers(rows, team.FriendlyTeamSet);
            }

            var message = new MessageWriter(MessageType.FactionTeams).Long(_hostEpoch).Int(_teamRevision + 1)
                .Int(_names.Count);
            foreach (string name in _names) message.String(name);
            message.Int(teamCount);
            byte[] head = message.ToArray();
            byte[] body = rows.ToArray();
            byte[] data = new byte[head.Length + body.Length - 1];
            Buffer.BlockCopy(head, 0, data, 0, head.Length);
            Buffer.BlockCopy(body, 1, data, head.Length, body.Length - 1); // skip the second type byte
            _session.SendToGuests(data);

            if (_hasSentTeams && signature != _sentTeamSignature)
                Plugin.Log.LogInfo("Faction: team relationships changed, sent revision " + (_teamRevision + 1)
                    + " (" + teamCount + " teams)");
            _teamRevision++;
            _sentTeamSignature = signature;
            _hasSentTeams = true;
            PerfStats.Count(full ? "faction.full" : "faction.delta");
        }

        private static bool Sendable(Team team)
        {
            if (team == null || team.ScriptTagHash == 0) return false;
            string tag = team.ScriptTag;
            return ValidTag(tag) && Animator.StringToHash(tag) == team.ScriptTagHash;
        }

        /// <summary>The form both sides accept; the guest recomputes the hash from it.</summary>
        private static bool ValidTag(string tag)
        {
            if (string.IsNullOrEmpty(tag) || tag.Length > MaxTagLength) return false;
            foreach (char c in tag)
                if (char.IsControl(c) || char.IsUpper(c)) return false;
            return tag == tag.ToLowerInvariant();
        }

        private int NameIndex(Team team)
        {
            int hash = team.ScriptTagHash;
            if (_nameIndex.TryGetValue(hash, out int index)) return index;
            if (_names.Count >= MaxTeamNames) { WarnOverflow("team names"); return -1; }
            index = _names.Count;
            _names.Add(team.ScriptTag);
            _nameIndex[hash] = index;
            return index;
        }

        private void WriteMembers(MessageWriter writer, HashSet<Team> set)
        {
            _memberBuffer.Clear();
            foreach (Team member in set)
                if (Sendable(member) && NameIndex(member) >= 0) _memberBuffer.Add(member);
            writer.Int(_memberBuffer.Count);
            foreach (Team member in _memberBuffer) writer.Int(NameIndex(member));
            _memberBuffer.Clear();
        }

        private static long SetSignature(HashSet<Team> set, Faction.Relationship relationship)
        {
            long sum = 0;
            foreach (Team member in set)
                if (member != null) sum += Mix(member.ScriptTagHash, (int)relationship);
            return sum;
        }

        /// <summary>Order-independent building block: rows and members are summed.</summary>
        private static long Mix(int a, int b)
        {
            unchecked
            {
                ulong x = ((ulong)(uint)a << 32) | (uint)b;
                x ^= x >> 33; x *= 0xff51afd7ed558ccdUL;
                x ^= x >> 33; x *= 0xc4ceb9fe1a85ec53UL;
                x ^= x >> 33;
                return (long)x;
            }
        }

        private void CaptureEntities(bool full)
        {
            _stamp++;
            _changed.Clear();
            List<Faction> active = Faction.ActiveFactionComponents;
            int live = 0;
            for (int i = 0; i < active.Count; i++)
            {
                Faction faction = active[i];
                if (faction == null) continue;
                if (!_sent.TryGetValue(faction, out Sent sent))
                {
                    sent = new Sent { Id = HostId(faction), Relationship = NoRelationship };
                    _sent[faction] = sent;
                }
                sent.Stamp = _stamp;
                live++;
                if (sent.Id == Guid.Empty) continue;

                Team team = faction.CurrentTeamInstance;
                bool hasTeam = team != null && team.ScriptTagHash != 0;
                byte flags = (byte)((UnitHostile(faction) ? FlagUnitHostile : 0) | (hasTeam ? FlagHasTeam : 0));
                int teamHash = hasTeam ? team.ScriptTagHash : 0;
                byte relationship = (byte)faction.RelationshipToPlayer;
                bool differs = sent.Flags != flags || sent.TeamHash != teamHash || sent.Relationship != relationship;
                if (differs && sent.Relationship != NoRelationship)
                    Plugin.Log.LogInfo("Faction: " + faction.name + " is now " + (Faction.Relationship)relationship
                        + " to the player (team " + (hasTeam ? team.ScriptTag : "<none>")
                        + ", unit hostile " + ((flags & FlagUnitHostile) != 0) + ")");
                sent.Flags = flags;
                sent.TeamHash = teamHash;
                sent.Relationship = relationship;
                if (differs || full) _changed.Add(sent);
            }

            if (_sent.Count > live)
            {
                _gone.Clear();
                foreach (var pair in _sent)
                    if (pair.Value.Stamp != _stamp) _gone.Add(pair.Key);
                foreach (Faction faction in _gone) _sent.Remove(faction);
                _gone.Clear();
            }
            if (_changed.Count == 0 && !full) return;

            int count = _changed.Count;
            if (count > MaxRecords)
            {
                WarnOverflow("entities");
                PerfStats.Count("faction.skipped", count - MaxRecords);
                count = MaxRecords;
            }
            _fullIds.Clear();
            var message = new MessageWriter(MessageType.FactionState).Long(_hostEpoch).Int(_entityRevision + 1)
                .Byte((byte)(full ? 1 : 0));
            // Two host objects must never share an id; if they do, the first one wins.
            int unique = 0;
            for (int i = 0; i < count; i++)
                if (_fullIds.Add(_changed[i].Id)) unique++;
            message.Int(unique);
            _fullIds.Clear();
            for (int i = 0; i < count; i++)
            {
                Sent sent = _changed[i];
                if (!_fullIds.Add(sent.Id)) continue;
                message.Guid(sent.Id).Byte(sent.Flags).Int(sent.TeamHash).Byte(sent.Relationship);
            }
            _fullIds.Clear();
            _changed.Clear();
            _session.SendToGuests(message.ToArray());
            _entityRevision++;
            PerfStats.Count(full ? "faction.full" : "faction.delta");
            PerfStats.Count("faction.records", unique);
        }

        /// <summary>
        /// Characters use the shared registry id. Anything else with a faction (traps,
        /// containers) is only sent when it has a saved id, which both sides share.
        /// </summary>
        private static Guid HostId(Faction faction)
        {
            GameObject obj = faction.gameObject;
            if (obj.GetComponent<Health>() != null) return EntityRegistry.NetworkId(obj);
            if (obj.GetComponent<Persistence>() == null) return Guid.Empty;
            InstanceID instance = obj.GetComponent<InstanceID>();
            return instance != null && !string.IsNullOrEmpty(instance.UniqueID) ? instance.Guid : Guid.Empty;
        }

        private void WarnOverflow(string what)
        {
            if (_warnedOverflow) return;
            _warnedOverflow = true;
            Plugin.Log.LogWarning("Faction: too many " + what + " to replicate; the rest keep their local state");
        }

        // ---- guest: receive ----

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            if (sender != null || !_session.IsGuest) return;
            if (reader.Type == MessageType.FactionTeams) ReadTeams(reader);
            else if (reader.Type == MessageType.FactionState) ReadEntities(reader);
        }

        private void ReadTeams(MessageReader reader)
        {
            long epoch = reader.Long();
            int revision = reader.Int();
            int nameCount = reader.Int();
            if (epoch <= 0 || revision <= 0) throw new FormatException("faction team header");
            if (nameCount < 0 || nameCount > MaxTeamNames || nameCount > reader.Remaining)
                throw new FormatException("faction team name count " + nameCount);
            var tags = new string[nameCount];
            var hashes = new int[nameCount];
            var seen = new HashSet<int>();
            for (int i = 0; i < nameCount; i++)
            {
                string tag = reader.String();
                if (!ValidTag(tag)) throw new FormatException("faction team tag");
                int hash = Animator.StringToHash(tag);
                if (hash == 0 || !seen.Add(hash)) throw new FormatException("duplicate faction team tag");
                tags[i] = tag;
                hashes[i] = hash;
            }

            int teamCount = reader.Int();
            if (teamCount < 0 || teamCount > MaxTeams || teamCount > nameCount)
                throw new FormatException("faction team count " + teamCount);
            var rows = new List<TeamRow>(teamCount);
            seen.Clear();
            for (int i = 0; i < teamCount; i++)
            {
                int index = reader.Int();
                byte defaultRelationship = reader.Byte();
                if (index < 0 || index >= nameCount || !seen.Add(index))
                    throw new FormatException("faction team index " + index);
                if (defaultRelationship > (byte)Faction.Relationship.Friendly)
                    throw new FormatException("faction default relationship " + defaultRelationship);
                var row = new TeamRow
                {
                    Tag = tags[index],
                    Hash = hashes[index],
                    Default = defaultRelationship,
                    Members = new int[3][],
                };
                // Wire order is hostile, neutral, friendly; the array is indexed by the enum.
                row.Members[(int)Faction.Relationship.Hostile] = ReadMembers(reader, hashes);
                row.Members[(int)Faction.Relationship.Neutral] = ReadMembers(reader, hashes);
                row.Members[(int)Faction.Relationship.Friendly] = ReadMembers(reader, hashes);
                rows.Add(row);
            }
            reader.Finish();

            if (!AcceptEpoch(epoch)) return;
            if (revision <= _guestTeamRevision) return;
            _guestTeamRevision = revision;

            _teamRows.Clear();
            _teamRows.AddRange(rows);
            _tagByHash.Clear();
            for (int i = 0; i < nameCount; i++) _tagByHash[hashes[i]] = tags[i];
            _hasTeamRows = true;
            _assetsSearched = false;

            _nextTeamVerify = 0f;
            if (GameState.IsLoading) { s_dirty = true; return; }
            long started = PerfStats.Begin();
            ApplyTeams();
            // A team that was unknown until now may unblock waiting entities.
            ApplyEntities();
            PerfStats.End("faction.apply", started);
        }

        private static int[] ReadMembers(MessageReader reader, int[] hashes)
        {
            int count = reader.Int();
            if (count < 0 || count > hashes.Length || count > reader.Remaining / 4)
                throw new FormatException("faction team member count " + count);
            var members = new int[count];
            for (int i = 0; i < count; i++)
            {
                int index = reader.Int();
                if (index < 0 || index >= hashes.Length) throw new FormatException("faction team member index " + index);
                members[i] = hashes[index];
            }
            return members;
        }

        private void ReadEntities(MessageReader reader)
        {
            long epoch = reader.Long();
            int revision = reader.Int();
            byte kind = reader.Byte();
            int count = reader.Int();
            if (epoch <= 0 || revision <= 0 || kind > 1) throw new FormatException("faction state header");
            if (count < 0 || count > MaxRecords || count != reader.Remaining / EntityRecordBytes
                || reader.Remaining % EntityRecordBytes != 0)
                throw new FormatException("faction record count " + count);
            var records = new Wanted[count];
            var seen = new HashSet<Guid>();
            for (int i = 0; i < count; i++)
            {
                var record = new Wanted
                {
                    Id = reader.Guid(),
                    Flags = reader.Byte(),
                    TeamHash = reader.Int(),
                    Relationship = reader.Byte(),
                };
                if (record.Id == Guid.Empty || !seen.Add(record.Id)) throw new FormatException("faction record id");
                if ((record.Flags & ~KnownFlags) != 0) throw new FormatException("faction record flags " + record.Flags);
                if (record.Relationship > (byte)Faction.Relationship.Friendly)
                    throw new FormatException("faction record relationship " + record.Relationship);
                if (((record.Flags & FlagHasTeam) != 0) != (record.TeamHash != 0))
                    throw new FormatException("faction record team");
                records[i] = record;
            }
            reader.Finish();

            if (!AcceptEpoch(epoch)) return;
            if (revision <= _guestEntityRevision) return;
            _guestEntityRevision = revision;

            // Keep the resolved object of an entity that is merely updated.
            foreach (Wanted record in records)
                if (_wanted.TryGetValue(record.Id, out Wanted known)) record.Faction = known.Faction;
            if (kind == 1) _wanted.Clear();
            foreach (Wanted record in records) _wanted[record.Id] = record;
            PerfStats.Count(kind == 1 ? "faction.full" : "faction.delta");

            if (GameState.IsLoading) { s_dirty = true; return; }
            long started = PerfStats.Begin();
            ApplyEntities();
            PerfStats.End("faction.apply", started);
        }

        /// <summary>Only the world this guest is in may change its factions.</summary>
        private bool AcceptEpoch(long epoch)
        {
            if (epoch != FlowCoordinator.WorldEpoch)
            {
                PerfStats.Count("faction.skipped");
                return false;
            }
            if (epoch != _guestEpoch)
            {
                ClearGuest();
                _guestEpoch = epoch;
            }
            return true;
        }

        // ---- guest: apply ----

        private void UpdateGuest(float now)
        {
            if (_guestEpoch == 0) return;
            if (!GameState.IsInGameSession || GameState.IsLoading || _guestEpoch != FlowCoordinator.WorldEpoch) return;
            bool teams = now >= _nextTeamVerify;
            if (!teams && !s_dirty && now < _nextVerify) return;
            s_dirty = false;
            _nextVerify = now + VerifyInterval;

            long started = PerfStats.Begin();
            if (teams)
            {
                _nextTeamVerify = now + TeamVerifyInterval;
                ApplyTeams();
            }
            ApplyEntities();
            PerfStats.End("faction.apply", started);
        }

        /// <summary>
        /// Makes every team the host listed have exactly the host's relationship sets.
        /// Writes the sets directly: Team.SetRelationship is refused on a guest.
        /// </summary>
        private void ApplyTeams()
        {
            if (!_hasTeamRows) return;
            _teamByHash.Clear();
            foreach (Team team in Team.LoadedTeams.Values)
                if (team != null && team.ScriptTagHash != 0) _teamByHash[team.ScriptTagHash] = team;
            // Unregistered teams the guest already references (asset lists) are reused as set members.
            foreach (Team team in Team.LoadedTeams.Values)
            {
                if (team == null) continue;
                Index(team.HostileTeamSet);
                Index(team.NeutralTeamSet);
                Index(team.FriendlyTeamSet);
            }

            foreach (TeamRow row in _teamRows)
            {
                Team team = Team.GetTeamByTag(row.Tag);
                if (team == null) team = RegisterMissingTeam(row);
                if (team == null) continue;
                if ((byte)team.DefaultRelationship != row.Default)
                    team.DefaultRelationship = (Faction.Relationship)row.Default;
                ApplySet(team.HostileTeamSet, row.Members[(int)Faction.Relationship.Hostile]);
                ApplySet(team.NeutralTeamSet, row.Members[(int)Faction.Relationship.Neutral]);
                ApplySet(team.FriendlyTeamSet, row.Members[(int)Faction.Relationship.Friendly]);
            }
            _teamByHash.Clear();
        }

        private void Index(HashSet<Team> set)
        {
            foreach (Team member in set)
            {
                if (member == null) continue;
                int hash = member.ScriptTagHash;
                if (hash != 0 && !_teamByHash.ContainsKey(hash)) _teamByHash[hash] = member;
            }
        }

        private void ApplySet(HashSet<Team> set, int[] wanted)
        {
            _memberBuffer.Clear();
            bool same = true;
            int live = 0;
            foreach (Team member in set)
            {
                if (member == null) { same = false; break; }
                live++;
            }
            for (int i = 0; i < wanted.Length; i++)
            {
                Team member = MemberTeam(wanted[i]);
                if (member == null) continue;
                _memberBuffer.Add(member);
                // Team equality is by tag hash, so this is true for any object of that team.
                if (same && !set.Contains(member)) same = false;
            }
            if (same && live == _memberBuffer.Count) { _memberBuffer.Clear(); return; }
            set.Clear();
            foreach (Team member in _memberBuffer) set.Add(member);
            _memberBuffer.Clear();
            PerfStats.Count("faction.records");
        }

        private Team MemberTeam(int hash)
        {
            if (_teamByHash.TryGetValue(hash, out Team team) && team != null) return team;
            if (!_tagByHash.TryGetValue(hash, out string tag)) return null;
            team = FindTeamAsset(hash);
            if (team == null) team = Placeholder(hash, tag);
            _teamByHash[hash] = team;
            return team;
        }

        /// <summary>
        /// The host has a registered team this guest never loaded. Registering only adds
        /// it to the game's team table; the relationship sets then come from the host.
        /// </summary>
        private Team RegisterMissingTeam(TeamRow row)
        {
            Team source;
            if (!_teamByHash.TryGetValue(row.Hash, out source) || source == null) source = FindTeamAsset(row.Hash);
            if (source != null)
            {
                source.Register();
                Team registered = Team.GetTeamByTag(row.Tag);
                if (registered != null)
                {
                    _teamByHash[row.Hash] = registered;
                    return registered;
                }
            }
            // No asset in memory: a stand-in with the host's relationships. A guest never
            // saves, and the game rebuilds its team table on every load.
            Team placeholder = Placeholder(row.Hash, row.Tag);
            placeholder.DefaultRelationship = (Faction.Relationship)row.Default;
            Team.LoadedTeams[row.Tag] = placeholder;
            Plugin.Log.LogInfo("Faction: team '" + row.Tag + "' is not loaded here; using the host's relationships for it");
            _teamByHash[row.Hash] = placeholder;
            return placeholder;
        }

        private Team FindTeamAsset(int hash)
        {
            if (_assetByHash.TryGetValue(hash, out Team known) && known != null) return known;
            if (_assetsSearched) return null;
            // One sweep per received table at most: only for a team nothing here references.
            _assetsSearched = true;
            foreach (Team asset in Resources.FindObjectsOfTypeAll<Team>())
            {
                if (asset == null) continue;
                int assetHash = asset.ScriptTagHash;
                if (assetHash == 0 || _assetByHash.ContainsKey(assetHash)) continue;
                if (_placeholders.TryGetValue(assetHash, out Team placeholder) && (object)placeholder == asset) continue;
                _assetByHash[assetHash] = asset;
            }
            return _assetByHash.TryGetValue(hash, out known) ? known : null;
        }

        private Team Placeholder(int hash, string tag)
        {
            if (_placeholders.TryGetValue(hash, out Team team) && team != null) return team;
            team = ScriptableObject.CreateInstance<Team>();
            team.name = tag;
            team.ScriptTag = tag;
            _placeholders[hash] = team;
            return team;
        }

        private void ApplyEntities()
        {
            int pending = 0, mismatched = 0;
            foreach (Wanted wanted in _wanted.Values)
            {
                if (!ApplyEntity(wanted)) { pending++; continue; }
                if ((byte)wanted.Faction.RelationshipToPlayer == wanted.Relationship) { wanted.Reported = false; continue; }
                mismatched++;
                if (wanted.Reported) continue;
                wanted.Reported = true;
                Plugin.Log.LogWarning("Faction: " + wanted.Faction.name + " is " + wanted.Faction.RelationshipToPlayer
                    + " here but " + (Faction.Relationship)wanted.Relationship + " on the host");
            }
            _pending = pending;
            _mismatched = mismatched;
            PerfStats.Gauge("faction.pending", pending);
        }

        /// <summary>False while the object or its team is not available on this guest yet.</summary>
        private bool ApplyEntity(Wanted wanted)
        {
            Faction faction = wanted.Faction;
            if (faction == null)
            {
                GameObject obj = EntityRegistry.ResolveObject(wanted.Id);
                faction = obj != null ? obj.GetComponent<Faction>() : null;
                if (faction == null) return false;
                wanted.Faction = faction;
                wanted.Applied = false;
            }

            Team team = null;
            if ((wanted.Flags & FlagHasTeam) != 0)
            {
                if (!_tagByHash.TryGetValue(wanted.TeamHash, out string tag)) return false;
                team = Team.GetTeamByTag(tag);
                if (team == null) return false;
            }

            bool changed = false;
            if ((object)MyTeam(faction) != (object)team || (object)faction.CurrentTeam != (object)team)
            {
                // Not the CurrentTeamInstance setter: no registration, no native event order.
                MyTeam(faction) = team;
                faction.CurrentTeam = team;
                changed = true;
            }
            bool unitHostile = (wanted.Flags & FlagUnitHostile) != 0;
            if (UnitHostile(faction) != unitHostile)
            {
                UnitHostile(faction) = unitHostile;
                changed = true;
            }
            if (changed)
            {
                PerfStats.Count("faction.records");
                if (wanted.Applied)
                    Plugin.Log.LogInfo("Faction: restored host state on " + faction.name);
                // Presentation only: the tactical turn marker recolours from this event.
                try { faction.OnFactionChanged.Trigger(faction.gameObject, faction.CurrentTeam); }
                catch (Exception ex) { Plugin.Log.LogWarning("Faction: change notification failed: " + ex.Message); }
            }
            wanted.Applied = true;
            return true;
        }

        // ---- diagnostics (--coop-faction-probe FILE) ----

        private static string ProbeArgument()
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "--coop-faction-probe");
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private void WriteProbe()
        {
            if (!GameState.IsInGameSession || GameState.IsLoading) return;
            try
            {
                var sb = new StringBuilder(4096);
                bool host = _session.IsHost;
                sb.Append("{\"utc\":\"").Append(DateTime.UtcNow.ToString("o")).Append("\",\"role\":\"")
                    .Append(host ? "host" : "guest").Append("\",\"epoch\":").Append(host ? _hostEpoch : _guestEpoch)
                    .Append(",\"entityRevision\":").Append(host ? _entityRevision : _guestEntityRevision)
                    .Append(",\"teamRevision\":").Append(host ? _teamRevision : _guestTeamRevision)
                    .Append(",\"pending\":").Append(_pending).Append(",\"mismatched\":").Append(_mismatched)
                    .Append(",\"inCombat\":").Append(GameState.InCombat ? "true" : "false")
                    .Append(",\"cursor\":\"").Append(GameCursor.ActiveCursor).Append("\",\"desiredCursor\":\"")
                    .Append(GameCursor.DesiredCursor).Append("\",\"underCursor\":\"")
                    .Append(Escape(GameCursor.CharacterUnderCursor != null ? GameCursor.CharacterUnderCursor.name : ""))
                    .Append("\",\"entities\":[");
                bool first = true;
                Camera camera = Camera.main;
                foreach (Faction faction in Faction.ActiveFactionComponents)
                {
                    if (faction == null || faction.GetComponent<Health>() == null) continue;
                    InstanceID instance = faction.GetComponent<InstanceID>();
                    if (instance == null || string.IsNullOrEmpty(instance.UniqueID)) continue;
                    Team team = MyTeam(faction);
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"id\":\"").Append(instance.UniqueID).Append("\",\"name\":\"").Append(Escape(faction.name))
                        .Append("\",\"team\":\"").Append(Escape(team != null ? team.ScriptTag : ""))
                        .Append("\",\"unitHostile\":").Append(UnitHostile(faction) ? "true" : "false")
                        .Append(",\"relationship\":\"").Append(faction.RelationshipToPlayer).Append('"');
                    if (camera != null)
                    {
                        Vector3 screen = camera.WorldToScreenPoint(faction.transform.position);
                        sb.Append(",\"sx\":").Append(Mathf.RoundToInt(screen.x)).Append(",\"sy\":")
                            .Append(Mathf.RoundToInt(Screen.height - screen.y)).Append(",\"onScreen\":")
                            .Append(screen.z > 0f && screen.x >= 0f && screen.y >= 0f
                                && screen.x < Screen.width && screen.y < Screen.height ? "true" : "false");
                    }
                    sb.Append('}');
                }
                sb.Append("],\"screen\":[").Append(Screen.width).Append(',').Append(Screen.height).Append("]}\n");
                File.AppendAllText(s_probeFile, sb.ToString());
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("Faction probe failed: " + ex.Message);
            }
        }

        private static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (!char.IsControl(c)) sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
