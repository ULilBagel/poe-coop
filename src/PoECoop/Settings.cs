using BepInEx.Configuration;
using PoECoop.Net;
using UnityEngine;

namespace PoECoop
{
    public enum TransportPreference
    {
        Steam,
        DirectIP,
    }

    public enum HostVisibility
    {
        FriendsOnly,
        InviteOnly,
    }

    /// <summary>Saved in BepInEx/config/poecoop.cfg.</summary>
    public sealed class Settings
    {
        public readonly ConfigEntry<string> Name;
        public readonly ConfigEntry<string> Address;
        public readonly ConfigEntry<int> Port;
        public readonly ConfigEntry<TransportPreference> PreferredTransport;
        public readonly ConfigEntry<HostVisibility> Visibility;
        public readonly ConfigEntry<KeyCode> ToggleKey;

        public Settings(ConfigFile config)
        {
            Name = config.Bind("Player", "Name", "Player", "Name shown to the other players.");
            Address = config.Bind("Connection", "Address", "127.0.0.1", "Host address used last time.");
            Port = config.Bind("Connection", "Port", Protocol.DefaultPort, "Direct-IP TCP port to host on or connect to.");
            PreferredTransport = config.Bind("Connection", "PreferredTransport", TransportPreference.Steam,
                "Transport to use for hosting and joining. DirectIP remains available when Steam is unavailable.");
            Visibility = config.Bind("Connection", "Visibility", HostVisibility.FriendsOnly,
                "Steam lobby visibility: friends may join from their friends list, or membership is by invitation only.");
            ToggleKey = config.Bind("Interface", "ToggleKey", KeyCode.F10, "Shows and hides the co-op window.");
        }
    }
}
