namespace SideHustle.Multiplayer
{
    /// <summary>One row in the server browser: a discoverable lobby for the selected gamemode.</summary>
    public sealed class LobbyRow
    {
        public ulong LobbyId;
        public string GamemodeName;
        public string HostName;
        public int Members;
        public int MaxPlayers;
        public bool HasPassword;
        public string PwHash;   // hash of the host's password (sh_pwhash) - the client compares its entry before joining
    }
}
