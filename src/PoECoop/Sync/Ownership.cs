using System;
using System.Collections.Generic;
using PoECoop.Net;

namespace PoECoop.Sync
{
    /// <summary>
    /// Who controls which party member. The host decides and tells the guests.
    /// A character nobody was given belongs to the host. A summon or animal
    /// companion belongs to whoever controls its master.
    /// Outside a session every character is the local player's.
    /// </summary>
    public sealed class Ownership
    {
        private static Ownership s_instance;

        private readonly Session _session;
        private readonly Dictionary<Guid, int> _owners = new Dictionary<Guid, int>();
        private bool _selectionDirty;
        private float _nextRefusalNote;

        /// <summary>Goes up by one whenever an assignment changes.</summary>
        public int Version { get; private set; }

        public Ownership(Session session)
        {
            _session = session;
            s_instance = this;
            session.GameMessage += OnGameMessage;
            session.PlayerJoined += OnPlayerJoined;
            session.PlayerLeft += OnPlayerLeft;
            session.FullStateRequested += OnFullStateRequested;
            GameState.OnLevelLoaded += OnLevelLoaded;
        }

        public void Dispose()
        {
            GameState.OnLevelLoaded -= OnLevelLoaded;
            _session.FullStateRequested -= OnFullStateRequested;
            _session.PlayerLeft -= OnPlayerLeft;
            _session.PlayerJoined -= OnPlayerJoined;
            _session.GameMessage -= OnGameMessage;
            if (s_instance == this) s_instance = null;
        }

        /// <summary>Used by the selection patch, which has no reference to this object.</summary>
        public static bool MayLocalPlayerSelect(PartyMemberAI member)
        {
            Ownership self = s_instance;
            if (self == null || self.IsMine(member)) return true;
            // Hovering still draws the game's selection circle, so say why a click did nothing.
            if (GameCursor.CharacterUnderCursor == member.gameObject && GameInput.GetControlUp(MappedControl.SELECT)
                && UnityEngine.Time.unscaledTime >= self._nextRefusalNote)
            {
                self._nextRefusalNote = UnityEngine.Time.unscaledTime + 2f;
                self._session.Note(CharacterStats.Name(member) + " is controlled by " + self._session.NameOf(self.OwnerOf(member)));
            }
            return false;
        }

        public bool IsMine(PartyMemberAI member)
        {
            if (!_session.IsHost && !_session.IsGuest) return true;
            return OwnerOf(member) == _session.LocalId;
        }

        public int OwnerOf(PartyMemberAI member)
        {
            // Follow summons and animal companions up to the party member they belong to.
            for (int depth = 0; member != null && depth < 4; depth++)
            {
                PartyMemberAI master = MasterOf(member);
                if (master == null) break;
                member = master;
            }
            if (member == null) return Session.HostId;
            Guid id = IdOf(member);
            return id != Guid.Empty && _owners.TryGetValue(id, out int owner) ? owner : Session.HostId;
        }

        /// <summary>Host only.</summary>
        public void Assign(PartyMemberAI member, int playerId)
        {
            if (!_session.IsHost || member == null) return;
            Guid id = IdOf(member);
            if (id == Guid.Empty) return;
            if (playerId == Session.HostId) _owners.Remove(id);
            else _owners[id] = playerId;
            Changed();
            Broadcast();
            _session.Note(CharacterStats.Name(member) + " is now controlled by " + _session.NameOf(playerId));
        }

        /// <summary>Immediately sends the complete assignment map to every ready guest.</summary>
        public void RefreshNow()
        {
            if (_session.IsHost) Broadcast();
        }

        /// <summary>Immediately sends the complete assignment map to one ready guest.</summary>
        public void RefreshNow(int playerId)
        {
            if (_session.IsHost) Broadcast(playerId);
        }

        /// <summary>Call once per frame.</summary>
        public void Update()
        {
            if (!_session.IsHost && !_session.IsGuest)
            {
                if (_owners.Count > 0) { _owners.Clear(); Changed(); }
                return;
            }
            if (!_selectionDirty || !GameState.IsInGameSession || GameState.IsLoading) return;
            _selectionDirty = false;
            // Let go of characters that are no longer the local player's.
            foreach (PartyMemberAI member in PartyMemberAI.PartyMembers)
                if (member != null && member.Selected && !IsMine(member)) member.Selected = false;
        }

        public static Guid IdOf(PartyMemberAI member)
        {
            var instance = member != null ? member.GetComponent<InstanceID>() : null;
            return instance != null ? instance.Guid : Guid.Empty;
        }

        public static PartyMemberAI Find(Guid id)
        {
            if (id == Guid.Empty) return null;
            foreach (PartyMemberAI member in PartyMemberAI.PartyMembers)
                if (member != null && IdOf(member) == id) return member;
            return null;
        }

        private static PartyMemberAI MasterOf(PartyMemberAI member)
        {
            if (member.Summoner != null) return member.Summoner.GetComponent<PartyMemberAI>();
            // An animal companion sits six slots above its master.
            int slot = member.Slot;
            if (slot >= 6 && slot < 12) return PartyMemberAI.PartyMembers[slot - 6];
            return null;
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            if (_session.IsHost) Broadcast();
        }

        private void OnPlayerLeft(PlayerInfo player)
        {
            // Their characters go back to the host.
            var theirs = new List<Guid>();
            foreach (var pair in _owners) if (pair.Value == player.Id) theirs.Add(pair.Key);
            if (theirs.Count == 0) return;
            foreach (Guid id in theirs) _owners.Remove(id);
            Changed();
            Broadcast();
        }

        private void OnLevelLoaded(object sender, EventArgs e)
        {
            _selectionDirty = true;
            if (_session.IsHost) Broadcast();
        }

        private void OnFullStateRequested(int playerId, long worldEpoch)
        {
            if (playerId < 0) RefreshNow();
            else RefreshNow(playerId);
        }

        private void Broadcast()
        {
            _session.SendToGuests(StateMessage());
        }

        private void Broadcast(int playerId)
        {
            _session.SendToPlayer(playerId, StateMessage());
        }

        private byte[] StateMessage()
        {
            var message = new MessageWriter(MessageType.Ownership).Int(_owners.Count);
            foreach (var pair in _owners) message.Guid(pair.Key).Int(pair.Value);
            return message.ToArray();
        }

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            if (sender != null || reader.Type != MessageType.Ownership) return;
            int count = reader.Int();
            if (count < 0 || count > 64) throw new FormatException("ownership count " + count);
            _owners.Clear();
            for (int i = 0; i < count; i++)
            {
                Guid id = reader.Guid();
                _owners[id] = reader.Int();
            }
            Changed();
        }

        private void Changed()
        {
            Version++;
            _selectionDirty = true;
        }
    }
}
