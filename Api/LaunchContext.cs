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

        /// <summary>
        /// Call this from your gamemode when it is done (e.g. the player clicks "Back"). Side Hustle restores
        /// the main menu and re-shows the hub. Safe to call once; further calls are ignored.
        /// </summary>
        public void ReturnToHub() => HubBridge.RequestReturn(this);
    }
}
