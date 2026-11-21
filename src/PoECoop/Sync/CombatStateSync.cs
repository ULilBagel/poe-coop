using System;
using System.Reflection;
using HarmonyLib;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop.Sync
{
    /// <summary>
    /// Mirrors the game's global combat and party-defeat state. Every packet is a
    /// complete snapshot, so the periodic resend also repairs a late joiner.
    /// </summary>
    public sealed class CombatStateSync
    {
        private const float SendInterval = 0.1f;
        private const float FullRefreshInterval = 3f;
        private const float MaxCombatDuration = 31536000f;
        private const float MaxGameOverDelay = 3600f;

        private static readonly FieldInfo InCombatField = AccessTools.Field(typeof(GameState), "s_isInCombat");
        private static readonly FieldInfo TrapCombatField = AccessTools.Field(typeof(GameState), "s_isInTrapTriggeredCombat");
        private static readonly FieldInfo CombatDurationField = AccessTools.Field(typeof(GameState), "s_inCombatTimer");
        private static readonly FieldInfo GameOverField = AccessTools.Field(typeof(GameState), "s_gameOver");
        private static readonly FieldInfo GameOverDelayField = AccessTools.Field(typeof(GameState), "s_deadTimer");

        private readonly Session _session;
        private Sample _sent;
        private bool _hasSent;
        private float _nextSend, _nextFullRefresh;
        private int _epoch, _revision;
        private bool _active;

        private struct Sample
        {
            public int Flags;
            public float CombatDuration, GameOverDelay;
        }

        public CombatStateSync(Session session)
        {
            _session = session;
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
        }

        private void OnPlayerJoined(PlayerInfo player)
        {
            if (!_session.IsHost) return;
            _nextSend = 0f;
            _nextFullRefresh = 0f;
        }

        private void OnLevelLoaded(object sender, EventArgs e)
        {
            _hasSent = false;
            _revision = 0;
            _nextSend = 0f;
            _nextFullRefresh = 0f;
            if (_session.IsHost) _epoch++;
            else _epoch = -1;
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

            bool full = now >= _nextFullRefresh;
            if (full) _nextFullRefresh = now + FullRefreshInterval;
            var sample = Capture();
            if (!full && _hasSent && Same(_sent, sample)) return;

            byte[] data = new MessageWriter(MessageType.CombatState).Int(_epoch).Int(_revision + 1)
                .Int(sample.Flags).Float(sample.CombatDuration).Float(sample.GameOverDelay).ToArray();
            _session.SendToGuests(data);
            _sent = sample;
            _hasSent = true;
            _revision++;
        }

        private static Sample Capture()
        {
            int flags = 0;
            if (GameState.InCombat) flags |= 1;
            if (GameState.IsInTrapTriggeredCombat) flags |= 2;
            if (GameState.GameOver) flags |= 4;
            if (GameState.PartyDead) flags |= 8;
            return new Sample
            {
                Flags = flags,
                CombatDuration = Bounded(GameState.InCombatDuration, MaxCombatDuration),
                GameOverDelay = Bounded((float)GameOverDelayField.GetValue(null), MaxGameOverDelay),
            };
        }

        private static bool Same(Sample a, Sample b)
        {
            return a.Flags == b.Flags
                && Mathf.Abs(a.CombatDuration - b.CombatDuration) < 0.05f
                && Mathf.Abs(a.GameOverDelay - b.GameOverDelay) < 0.02f;
        }

        private void OnGameMessage(PlayerInfo sender, MessageReader reader)
        {
            if (sender != null || reader.Type != MessageType.CombatState || !_session.IsGuest) return;
            _active = true;
            int epoch = reader.Int();
            int revision = reader.Int();
            int flags = reader.Int();
            float combatDuration = ReadBounded(reader.Float(), MaxCombatDuration, "combat duration");
            float gameOverDelay = ReadBounded(reader.Float(), MaxGameOverDelay, "game-over delay");
            if ((flags & ~15) != 0) throw new FormatException("combat state flags " + flags);
            if (epoch == _epoch && revision <= _revision) return;

            _epoch = epoch;
            _revision = revision;
            InCombatField.SetValue(null, (flags & 1) != 0);
            TrapCombatField.SetValue(null, (flags & 2) != 0);
            GameOverField.SetValue(null, (flags & 4) != 0);
            GameState.PartyDead = (flags & 8) != 0;
            CombatDurationField.SetValue(null, combatDuration);
            GameOverDelayField.SetValue(null, gameOverDelay);
        }

        private void Clear()
        {
            _active = false;
            _hasSent = false;
            _epoch = 0;
            _revision = 0;
            _nextSend = 0f;
            _nextFullRefresh = 0f;
        }

        private static float Bounded(float value, float max)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
            return Mathf.Clamp(value, 0f, max);
        }

        private static float ReadBounded(float value, float max, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > max)
                throw new FormatException(name + " out of range");
            return value;
        }
    }
}
