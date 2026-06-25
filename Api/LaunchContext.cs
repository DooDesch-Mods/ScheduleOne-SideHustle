using SideHustle.Internal;

namespace SideHustle
{
    /// <summary>
    /// Passed to a gamemode's launch callback. Carries who is launching (host / client / singleplayer) and,
    /// for multiplayer, the lobby id. The gamemode calls <see cref="ReturnToHub"/> when it is finished so
    /// Side Hustle can restore the menu.
    /// </summary>
    public sealed class LaunchContext
    {
        /// <summary>The descriptor being launched.</summary>
        public GamemodeDescriptor Descriptor { get; internal set; }

        /// <summary>true = host, false = client, null = singleplayer.</summary>
        public bool? IsHost { get; internal set; }

        /// <summary>Steam lobby id for multiplayer; 0 for singleplayer.</summary>
        public ulong LobbyId { get; internal set; }

        /// <summary>True when this is a singleplayer launch.</summary>
        public bool IsSingleplayer => IsHost == null;

        // --- Richer multiplayer context (all optional; 0/null for singleplayer) ---

        /// <summary>Players currently in the lobby (1 for singleplayer).</summary>
        public int PlayerCount { get; internal set; } = 1;

        /// <summary>Steam persona of the lobby host (null for singleplayer).</summary>
        public string HostName { get; internal set; }

        /// <summary>True if the lobby was created with a password.</summary>
        public bool HasPassword { get; internal set; }

        /// <summary>
        /// The full multiplayer payload (max players, gamemode name, the host's free-form config blob, ...).
        /// Null for singleplayer. Use this to pass gamemode-specific settings from host to clients.
        /// </summary>
        public Multiplayer.MultiplayerInfo Multiplayer { get; internal set; }

        /// <summary>
        /// Call this from your gamemode when it is done (e.g. the player clicks "Back"). Side Hustle restores
        /// the main menu and re-shows the hub. Safe to call once; further calls are ignored.
        /// </summary>
        public void ReturnToHub() => HubBridge.RequestReturn(this);
    }
}
