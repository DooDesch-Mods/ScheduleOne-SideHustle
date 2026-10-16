using System.Text;

namespace SideHustle
{
    /// <summary>Who the local player is in a session. Read it from <see cref="LaunchContext.Role"/>. Since 3.0.0.</summary>
    public enum SessionRole
    {
        /// <summary>A singleplayer launch; no lobby.</summary>
        Singleplayer,
        /// <summary>The local player opened the lobby and owns the session.</summary>
        Host,
        /// <summary>The local player joined someone else's lobby.</summary>
        Client
    }

    /// <summary>
    /// One player in the current session (see <see cref="API.Players"/>, <see cref="API.PlayerJoined"/>,
    /// <see cref="API.PlayerLeft"/>). The same object stays in <see cref="API.Players"/> for as long as the player
    /// is in the session; <see cref="DisplayName"/> and <see cref="IsHost"/> are refreshed about twice a second.
    /// Since 3.0.0.
    /// </summary>
    public sealed class SessionPlayer
    {
        internal SessionPlayer(ulong steamId, bool isLocal)
        {
            SteamId = steamId;
            IsLocal = isLocal;
        }

        /// <summary>The player's Steam id (id64). 0 for the local player when Steam could not be read.</summary>
        public ulong SteamId { get; }

        /// <summary>
        /// The name other players see. For the local player this is the session alias when one is set, else the
        /// Steam persona. For a remote player in a World session it is the replicated in-game name (which carries
        /// their alias) once their character has spawned; before that, and in a MenuSpace session, it is their
        /// Steam persona.
        /// </summary>
        public string DisplayName { get; internal set; }

        /// <summary>True for the lobby owner. In singleplayer the local player is not the host.</summary>
        public bool IsHost { get; internal set; }

        /// <summary>True for the player on this machine.</summary>
        public bool IsLocal { get; }

        /// <summary>"Name (steamId)", for logs.</summary>
        public override string ToString() => $"{DisplayName} ({SteamId})";
    }

    /// <summary>
    /// A gamemode message received over Side Hustle's P2P channel (see <see cref="API.OnMessage"/>). Since 3.0.0.
    /// </summary>
    public sealed class SessionMessage
    {
        internal SessionMessage(string gamemodeId, string channel, ulong senderSteamId, byte[] payload, bool isText)
        {
            GamemodeId = gamemodeId;
            Channel = channel;
            SenderSteamId = senderSteamId;
            Payload = payload ?? new byte[0];
            IsText = isText;
        }

        /// <summary>The gamemode id the sender addressed.</summary>
        public string GamemodeId { get; }

        /// <summary>The sender's channel name, free-form per gamemode (e.g. "ping", "state").</summary>
        public string Channel { get; }

        /// <summary>The sender's Steam id as Steam reported it for the packet. Never read from the payload.</summary>
        public ulong SenderSteamId { get; }

        /// <summary>The raw payload bytes.</summary>
        public byte[] Payload { get; }

        /// <summary>True when the sender used a text overload.</summary>
        public bool IsText { get; }

        /// <summary>The payload decoded as UTF-8.</summary>
        public string Text => Encoding.UTF8.GetString(Payload);
    }
}
