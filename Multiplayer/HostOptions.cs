namespace SideHustle.Multiplayer
{
    /// <summary>How a hosted lobby is exposed.</summary>
    public enum LobbyVisibility
    {
        /// <summary>Listed in the in-hub server browser; anyone can join (optionally gated by a password).</summary>
        Public,
        /// <summary>Friends-only: not listed in the browser, joined via a Steam invite.</summary>
        Private
    }

    /// <summary>
    /// Choices the host makes on the Host-config screen before opening a lobby. <see cref="ConfigBlob"/> carries the
    /// gamemode's chosen settings (encoded key=value) to the gamemode and to joining clients.
    /// </summary>
    public sealed class HostOptions
    {
        /// <summary>Maximum players for the lobby. Defaults to the game's native cap; BiggerLobbies allows more.</summary>
        public int MaxPlayers = 4;

        /// <summary>Public (browser-listed) or Private (friends-only).</summary>
        public LobbyVisibility Visibility = LobbyVisibility.Public;

        /// <summary>Optional join password for a Public lobby (empty = open). Ignored for Private lobbies.</summary>
        public string Password;

        /// <summary>Free-form per-gamemode config the host publishes to clients via the lobby (key sh_config).</summary>
        public string ConfigBlob = null;

        /// <summary>Optional display name for the lobby (key sh_name); shown as the title of the browser card.
        /// Empty falls back to the host's Steam persona.</summary>
        public string LobbyName = null;

        /// <summary>Optional mode label the host is running (key sh_mode), e.g. "Infection" or "Custom - Infection";
        /// shown on the browser card so joiners see the selected mode. Null when the gamemode has no presets.</summary>
        public string ModeLabel = null;

        /// <summary>True when a Public lobby is password-gated (shows a lock in the browser).</summary>
        public bool HasPassword => Visibility == LobbyVisibility.Public && !string.IsNullOrEmpty(Password);
    }
}
