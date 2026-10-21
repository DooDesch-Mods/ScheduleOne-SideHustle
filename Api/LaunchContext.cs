// Copyright (c) 2026 DooDesch. DooDesch API License (see LICENSE.md): you may copy this file unchanged.
using SideHustle.Internal;

namespace SideHustle
{
    /// <summary>
    /// Passed to a gamemode's launch callback. Carries who is launching (host / client / singleplayer) and,
    /// for multiplayer, the lobby id. The gamemode calls <see cref="ReturnToHub"/> when it is finished so
    /// Side Hustle can restore the menu.
    /// </summary>
    /// <example>
    /// One callback for every role, reading a host setting:
    /// <code>
    /// static void Start(LaunchContext ctx)
    /// {
    ///     int seconds = ctx.Multiplayer?.GetInt("len", 30) ?? 30;   // null in singleplayer
    ///     if (ctx.Role == SessionRole.Host) { /* start the round */ }
    ///     // later, when the gamemode is done:
    ///     ctx.ReturnToHub();
    /// }
    /// </code>
    /// </example>
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

        /// <summary>Who the local player is in this session; the same information as <see cref="IsHost"/> as an
        /// enum. Since 3.0.0.</summary>
        public SessionRole Role => IsHost == null ? SessionRole.Singleplayer : IsHost.Value ? SessionRole.Host : SessionRole.Client;

        // Lifecycle bookkeeping for SessionEvents: SessionStarted and OnExitToHub each run at most once per context.
        internal bool StartRaised;
        internal bool ExitRaised;

        // --- Richer multiplayer context (all optional; 0/null for singleplayer) ---

        /// <summary>Players currently in the lobby (1 for singleplayer).</summary>
        public int PlayerCount { get; internal set; } = 1;

        /// <summary>The host's display name: their session alias when they set one on the Host form, else their Steam
        /// persona. On a client it is read from the lobby (<c>sh_host_name</c>), which the host writes the same way.
        /// Null for singleplayer.</summary>
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
        /// the main menu and re-shows the hub. Only the first call counts: once the session was ended - by this
        /// call or by Side Hustle aborting it - later calls are ignored and logged.
        /// </summary>
        public void ReturnToHub() => HubBridge.RequestReturn(this);
    }
}
