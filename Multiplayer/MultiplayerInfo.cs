namespace SideHustle.Multiplayer
{
    /// <summary>
    /// The multiplayer payload carried on <see cref="LaunchContext.Multiplayer"/>. Populated by Side Hustle from
    /// the Steam lobby's namespaced metadata (the <c>sh_*</c> keys). The host fills it from its host options;
    /// a joining client reads it back from the lobby. <see cref="ConfigBlob"/> is a free-form string the host
    /// writes (lobby key <c>sh_config</c>) so a gamemode can ship its own settings to clients with no extra netcode.
    /// </summary>
    public sealed class MultiplayerInfo
    {
        /// <summary>Maximum players the host opened the lobby for.</summary>
        public int MaxPlayers { get; internal set; }

        /// <summary>Display name of the gamemode (lobby key <c>sh_gamemode_name</c>).</summary>
        public string GamemodeName { get; internal set; }

        /// <summary>Steam persona of the host (lobby key <c>sh_host_name</c>).</summary>
        public string HostName { get; internal set; }

        /// <summary>True if the lobby is password-protected (lobby key <c>sh_pw</c> = "1").</summary>
        public bool HasPassword { get; internal set; }

        /// <summary>Free-form per-gamemode payload the host published (lobby key <c>sh_config</c>); may be null.</summary>
        public string ConfigBlob { get; internal set; }
    }
}
