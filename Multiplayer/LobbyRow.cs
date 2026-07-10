namespace SideHustle.Multiplayer
{
    /// <summary>One row in the server browser: a discoverable lobby for the selected gamemode.</summary>
    public sealed class LobbyRow
    {
        public ulong LobbyId;
        public string GamemodeName;
        public string LobbyName;   // host-chosen lobby name (sh_name); browser card title, falls back to HostName
        public string Mode;        // selected mode/preset label (sh_mode), e.g. "Custom - Infection"
        public string HostName;
        public int Members;
        public int MaxPlayers;
        public bool HasPassword;
        public string PwHash;   // hash of the host's password (sh_pwhash) - the client compares its entry before joining
        public string BuildId;  // host's gamemode build fingerprint (sh_build) - the browser flags a version mismatch
        public string GamemodeId;   // stable gamemode id (sh_gamemode) - matches a lobby to an installed gamemode
        public string DownloadUrl;  // where to get the mod (sh_url) - shown as "Download Mod" for uninstalled gamemodes
    }
}
