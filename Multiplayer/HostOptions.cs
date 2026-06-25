namespace SideHustle.Multiplayer
{
    /// <summary>
    /// Choices the host makes before opening a lobby (from the Host dialog). Kept small; passwords are only a
    /// visible lock flag for now (no enforcement) per the current scope.
    /// </summary>
    public sealed class HostOptions
    {
        /// <summary>Maximum players for the lobby. Defaults to the game's native cap; BiggerLobbies allows more.</summary>
        public int MaxPlayers = 4;

        /// <summary>Whether to advertise the lobby as password-protected (shows a lock in the browser).</summary>
        public bool HasPassword = false;

        /// <summary>Free-form per-gamemode config the host publishes to clients via the lobby (key sh_config).</summary>
        public string ConfigBlob = null;
    }
}
