using System;
using UnityEngine;

namespace SideHustle.Multiplayer
{
    /// <summary>
    /// Drives a multiplayer (or World-singleplayer) gamemode session through its lifecycle. Tick() is pumped from
    /// Core.OnUpdate. The flow is a small state machine because lobby creation and world loading are asynchronous:
    ///
    ///   HOST:  create public lobby -> (singleton flips) tag it -> boot the world (World surface) -> OnHostMultiplayer
    ///   JOIN:  JoinLobby -> lobby entered -> (World) the game's OnLobbyEntered streams the host's world in -> OnJoinMultiplayer
    ///   RETURN: OnExitToHub -> leave lobby + ExitToMenu -> reopen the hub on the next Menu init
    ///
    /// The lobby must exist + be owned by us before the world boots, or StartGame binds the localhost-only
    /// transport and the session is unjoinable.
    /// </summary>
    internal static class MultiplayerCoordinator
    {
        private enum State { Idle, HostCreatingLobby, HostBootingWorld, Joining, SpBootingWorld, InSession }

        private static State _state = State.Idle;
        private static GamemodeDescriptor _desc;
        private static HostOptions _hostOpts;
        private static LaunchContext _ctx;
        private static ulong _joinLobbyId;
        private static float _timer;
        private static bool _lobbyEntered;    // Joining: Steam admitted us to the lobby we asked for
        private static bool _loadStarted;     // Joining a World gamemode: the game has begun loading since we entered
        private static bool _exitNotified;    // the gamemode's OnExitToHub has run for this session

        /// <summary>How long a join may take to be admitted to the lobby at all.</summary>
        private const float LobbyEntrySeconds = 20f;

        /// <summary>How long an admitted client of a World gamemode waits for the game to start loading. Vanilla only
        /// starts a client's load from the host's lobby keys on entry, so past this the load will never begin (see
        /// <see cref="WorldBoot.LoadStarted"/>).</summary>
        private const float NeverStartedSeconds = 15f;

        private static readonly LoadWatchdog _watchdog = new LoadWatchdog();

        /// <summary>Set when a session ended via a full scene reload; Core reopens the hub on the next Menu init.</summary>
        internal static bool PendingHubReopen;

        internal static bool IsBusy => _state != State.Idle;

        /// <summary>Why the last Start call returned false, in words a player can read. Empty after a successful start.</summary>
        internal static string LastRefusal { get; private set; } = "";

        // --- entry points (called by the Hub UI) ---

        /// <summary>Whether a new session may start now. Double clicks and a start while a vanilla co-op session is
        /// being set up would each open a second lobby on top of the first.</summary>
        private static bool CanStart(string what, GamemodeDescriptor desc)
        {
            if (_state != State.Idle)
            {
                LastRefusal = "A session is already starting.";
                Core.Log?.Warning($"[mp] {what} of '{desc?.Id}' refused: a gamemode session is already {_state}.");
                return false;
            }
            if (Sync.SyncCoordinator.IsBusy)
            {
                LastRefusal = "A vanilla co-op session is already starting.";
                Core.Log?.Warning($"[mp] {what} of '{desc?.Id}' refused: a vanilla co-op session is in progress.");
                return false;
            }
            LastRefusal = "";
            return true;
        }

        internal static bool StartHost(GamemodeDescriptor desc, HostOptions opts)
        {
            if (desc == null) { Core.Log?.Warning("[mp] host refused: no gamemode given."); return false; }
            if (desc.OnHostMultiplayer == null)
            {
                Core.Log?.Warning($"Gamemode '{desc.Id}' has no host callback; cannot host.");
                LastRefusal = "This gamemode cannot be hosted.";
                return false;
            }
            if (!CanStart("host", desc)) return false;

            _state = State.HostCreatingLobby;   // claimed before anything else, so a second click is refused
            _desc = desc;
            _hostOpts = opts ?? new HostOptions();
            _ctx = null;
            _exitNotified = false;
            ResetPhase();
            GamemodeHygiene.Apply(desc);   // skip-intro / block-quests must be active before the world loads
            NetworkTuning.EnsureIceEnabled();   // allow all P2P ICE candidate types so non-friend clients can reach this host
            PublicLobbyAccess.Enable();   // stop the vanilla host from kicking non-friends, so public lobbies actually work
            LobbyInviteAccess.Enable();   // let any lobby member (incl. clients) invite Steam friends from the pause panel
            PlayerAlias.Enable(Config.Preferences.GetAlias(desc.Id));   // show the player's chosen (per-gamemode) display name this session
            Mods.LateLoader.LoadAll("hosting a gamemode lobby");   // before the lobby: a mod that hooks the lobby has to exist by then

            Core.Log?.Msg($"[mp] hosting '{desc.DisplayName}' (max {_hostOpts.MaxPlayers}, {_hostOpts.Visibility})...");
            if (!LobbyCoordinator.CreateLobby(_hostOpts.MaxPlayers, _hostOpts.Visibility)) { AbortToHub("could not create a lobby"); return false; }
            return true;
        }

        internal static bool StartJoin(GamemodeDescriptor desc, LobbyRow row)
        {
            if (desc == null || row == null) { Core.Log?.Warning("[mp] join refused: no gamemode or lobby given."); return false; }
            if (desc.OnJoinMultiplayer == null)
            {
                Core.Log?.Warning($"Gamemode '{desc.Id}' has no join callback; cannot join.");
                LastRefusal = "This gamemode cannot be joined.";
                return false;
            }
            if (!CanStart("join", desc)) return false;

            _state = State.Joining;   // claimed before anything else, so a second click is refused
            _desc = desc;
            _joinLobbyId = row.LobbyId;
            _ctx = null;
            _exitNotified = false;
            _lobbyEntered = false;
            _loadStarted = false;
            ResetPhase();
            GamemodeHygiene.Apply(desc);   // active before the host's world streams in (the client also runs PlayerLoaded)
            NetworkTuning.EnsureIceEnabled();   // allow all P2P ICE candidate types so this join can hold to a non-friend host
            LobbyInviteAccess.Enable();   // let this client invite Steam friends from the pause-menu lobby panel
            PlayerAlias.Enable(Config.Preferences.GetAlias(desc.Id));   // show the player's chosen (per-gamemode) display name this session
            Mods.LateLoader.LoadAll("joining a gamemode lobby");

            Core.Log?.Msg($"[mp] joining '{desc.DisplayName}' lobby {row.LobbyId}...");
            if (!LobbyCoordinator.JoinLobby(row.LobbyId)) { AbortToHub("could not ask Steam to join the lobby"); return false; }
            return true;
        }

        /// <summary>Boot a throwaway world for a Surface=World singleplayer gamemode (no lobby).</summary>
        internal static bool StartWorldSingleplayer(GamemodeDescriptor desc)
        {
            if (desc == null) { Core.Log?.Warning("[mp] singleplayer refused: no gamemode given."); return false; }
            if (desc.OnLaunchSingleplayer == null)
            {
                Core.Log?.Warning($"Gamemode '{desc.Id}' has no singleplayer callback.");
                LastRefusal = "This gamemode has no singleplayer mode.";
                return false;
            }
            if (!CanStart("singleplayer start", desc)) return false;

            _state = State.SpBootingWorld;   // claimed before anything else, so a second click is refused
            _desc = desc;
            _ctx = null;
            _exitNotified = false;
            ResetPhase();
            GamemodeHygiene.Apply(desc);
            Core.Log?.Msg($"[mp] booting singleplayer world for '{desc.DisplayName}'...");
            if (!WorldBoot.BootHostWorld(SessionOrgName())) { AbortToHub("world boot failed"); return false; }
            return true;
        }

        /// <summary>A new wait starts: the phase timer and the load watchdog both restart.</summary>
        private static void ResetPhase()
        {
            _timer = 0f;
            _watchdog.Reset();
        }

        // --- per-frame state machine ---

        internal static void Tick()
        {
            if (_state == State.Idle) return;
            if (_state == State.InSession) { TickSessionAlive(); return; }
            _timer += Time.unscaledDeltaTime;

            switch (_state)
            {
                case State.HostCreatingLobby:
                    if (LobbyCoordinator.IsHost)
                    {
                        if (!LobbyCoordinator.TagLobby(_desc, _hostOpts)) { AbortToHub("could not set up the lobby"); break; }
                        if (_desc.Surface == GamemodeSurface.World)
                        {
                            if (!WorldBoot.BootHostWorld(SessionOrgName())) { AbortToHub("world boot failed"); break; }
                            ResetPhase();
                            _state = State.HostBootingWorld;
                        }
                        else
                        {
                            FireHost();
                        }
                    }
                    else if (_timer > 10f) AbortToHub("lobby did not open within 10s");   // re-host: allow Steam to finish tearing down the previous lobby first
                    break;

                case State.HostBootingWorld:
                    if (WorldBoot.IsWorldReady()) FireHost();
                    else if (_watchdog.HasGivenUp(out string hostWhy)) AbortToHub("world not ready - " + hostWhy);
                    break;

                case State.Joining:
                    TickJoining();
                    break;

                case State.SpBootingWorld:
                    if (WorldBoot.IsWorldReady()) FireSpWorld();
                    else if (_watchdog.HasGivenUp(out string spWhy)) AbortToHub("world not ready - " + spWhy);
                    break;
            }
        }

        private static void TickJoining()
        {
            // Phase 1: Steam has to admit us to the lobby we asked for. JoinLobby only queues the request.
            if (!_lobbyEntered)
            {
                if (LobbyCoordinator.IsInLobby && LobbyCoordinator.CurrentLobbyId == _joinLobbyId)
                {
                    _lobbyEntered = true;
                    ResetPhase();
                    Core.Log?.Msg($"[mp] entered lobby {_joinLobbyId}.");
                }
                else if (_timer > LobbyEntrySeconds)
                    AbortToHub($"could not enter the lobby within {LobbyEntrySeconds:F0}s (it may be full, closed or unreachable)");
                return;
            }

            // A MenuSpace gamemode builds its overlay on the menu and the host never loads a world, so the lobby is
            // the whole join.
            if (_desc.Surface == GamemodeSurface.MenuSpace) { FireJoin(); return; }

            // Phase 2 (World): the host's world streams in. Two different waits that must not share a deadline: until
            // the game is going somewhere it is not slow, it is never going to start.
            if (WorldBoot.IsWorldReady()) { FireJoin(); return; }
            if (WorldBoot.LoadStarted) _loadStarted = true;
            if (!_loadStarted)
            {
                if (_timer > NeverStartedSeconds)
                    AbortToHub("the host's lobby never started the game - it is not marked ready for players");
            }
            else if (_watchdog.HasGivenUp(out string joinWhy)) AbortToHub("join did not complete - " + joinWhy);
        }

        // --- fire the gamemode callbacks ---

        private static void FireHost()
        {
            _ctx = new LaunchContext
            {
                Descriptor = _desc,
                IsHost = true,
                LobbyId = LobbyCoordinator.CurrentLobbyId,
                PlayerCount = LobbyCoordinator.MemberCount,
                HostName = LobbyCoordinator.LocalPersonaName(),
                HasPassword = _hostOpts.HasPassword,
                Multiplayer = new MultiplayerInfo
                {
                    MaxPlayers = _hostOpts.MaxPlayers,
                    GamemodeName = _desc.DisplayName,
                    LobbyName = string.IsNullOrEmpty(_hostOpts.LobbyName) ? LobbyCoordinator.LocalPersonaName() : _hostOpts.LobbyName,
                    Mode = _hostOpts.ModeLabel,
                    HostName = LobbyCoordinator.LocalPersonaName(),
                    HasPassword = _hostOpts.HasPassword,
                    ConfigBlob = _hostOpts.ConfigBlob
                }
            };
            _state = State.InSession;
            Core.Log?.Msg($"[mp] HOST ready: '{_desc.DisplayName}' lobby {_ctx.LobbyId}, {_ctx.PlayerCount} player(s).");
            SafeInvoke(_desc.OnHostMultiplayer, _ctx);
            Internal.SessionEvents.RaiseStarted(_ctx);
        }

        private static void FireJoin()
        {
            var info = LobbyCoordinator.ReadInfo(_joinLobbyId);
            _ctx = new LaunchContext
            {
                Descriptor = _desc,
                IsHost = false,
                LobbyId = _joinLobbyId,
                PlayerCount = LobbyCoordinator.MemberCount,
                HostName = info.HostName,
                HasPassword = info.HasPassword,
                Multiplayer = info
            };
            _state = State.InSession;
            Core.Log?.Msg($"[mp] JOINED: '{_desc.DisplayName}' lobby {_ctx.LobbyId}, {_ctx.PlayerCount} player(s).");

            // Version parity: warn (don't block) when the host runs a different build of this gamemode. A build
            // mismatch is the classic "everyone must be on the same version" bug; catching it here surfaces it for
            // ALL gamemodes without each one rolling its own check.
            string localBuild = LobbyCoordinator.BuildIdOf(_desc);
            if (!string.IsNullOrEmpty(info.BuildId) && !string.IsNullOrEmpty(localBuild)
                && !string.Equals(info.BuildId, localBuild, StringComparison.Ordinal))
                Core.Log?.Warning($"[mp] VERSION MISMATCH: host runs a different build of '{_desc.DisplayName}' " +
                                  $"(host {Short(info.BuildId)} vs local {Short(localBuild)}). Everyone must use the same version - expect bugs.");

            SafeInvoke(_desc.OnJoinMultiplayer, _ctx);
            Internal.SessionEvents.RaiseStarted(_ctx);
        }

        private static void FireSpWorld()
        {
            _ctx = new LaunchContext { Descriptor = _desc, IsHost = null, LobbyId = 0, PlayerCount = 1 };
            _state = State.InSession;
            Core.Log?.Msg($"[mp] singleplayer world ready: '{_desc.DisplayName}'.");
            SafeInvoke(_desc.OnLaunchSingleplayer, _ctx);
            Internal.SessionEvents.RaiseStarted(_ctx);
        }

        // --- return / teardown (called via HubBridge when ctx.ReturnToHub fires for a World/MP session) ---

        internal static void ReturnFromSession(LaunchContext ctx)
        {
            Core.Log?.Msg($"[mp] returning from '{ctx?.Descriptor?.Id ?? _desc?.Id}'.");
            bool wasInGame = false;
            try
            {
                NotifyExit(ctx ?? _ctx);

                bool host = ctx != null && ctx.IsHost == true;
                if (host) Safe("unlist the lobby", LobbyCoordinator.Unlist);

                Safe("leave the session", () =>
                {
                    wasInGame = WorldBoot.IsInGame;
                    if (wasInGame) WorldBoot.ExitToMenu();   // also leaves the Steam lobby
                    else LobbyCoordinator.LeaveCurrentLobby();   // a MenuSpace session has no world to exit, but a lobby to leave
                });
            }
            finally
            {
                if (wasInGame) PendingHubReopen = true;   // reopen the hub when the Menu scene re-initializes
                Teardown();
            }

            // MenuSpace MP (no scene reload): the menu is still here - reopen the hub directly.
            if (!wasInGame) Menu.Hub.ReopenAfterSession();
        }

        /// <summary>Tells the gamemode the session is over - at most once per session, even when a callback threw or
        /// the exit is reached from two paths.</summary>
        private static void NotifyExit(LaunchContext ctx)
        {
            if (ctx == null) { Core.Log?.Msg("[mp] no gamemode session to end."); return; }
            if (_exitNotified) { Core.Log?.Msg($"[mp] '{ctx.Descriptor?.Id}' was already told the session ended."); return; }
            _exitNotified = true;
            // SessionEvents runs OnExitToHub once per context and raises SessionEnded for a started session; the flag
            // above covers the abort path, which builds a fresh context when the session never started.
            Internal.SessionEvents.Exit(ctx);
        }

        /// <summary>The session-wide switches that every way out of a session must undo. Each step stands alone, so
        /// one that throws cannot leave the rest on and the state machine stuck.</summary>
        private static void Teardown()
        {
            Safe("clear hygiene", GamemodeHygiene.Clear);
            Safe("restore the friend kick", PublicLobbyAccess.Disable);   // the vanilla non-friend kick outside a Side Hustle session
            Safe("stop aliasing", PlayerAlias.Disable);                   // the next session uses the real Steam name again
            Safe("restore the host-only invite button", LobbyInviteAccess.Disable);
            Safe("clean the scratch save", WorldBoot.CleanupScratch);
            _state = State.Idle;
            _ctx = null;
        }

        private static void Safe(string what, Action step)
        {
            try { step(); }
            catch (Exception e) { Core.Log?.Error($"[mp] session teardown step '{what}' failed: {e}"); }
        }

        private static float _backAtMenuFor;
        private static bool _aliveCheckFailedLogged;

        /// <summary>
        /// Notice when a live session has quietly ended underneath us.
        ///
        /// Reaching "in session" used to be the end of this state machine's job, so a client whose world dropped it
        /// back to the menu was never cleaned up: still a member of the Steam lobby, still receiving the host's
        /// state, still advertised as being in the game, and never told why. The player sees the main menu and
        /// assumes they left; the host still counts them.
        ///
        /// The other recovery path (ClientExitGuard) cannot cover this one - it only looks at gameplay scenes,
        /// and this failure ends up on the menu by definition.
        /// </summary>
        private static void TickSessionAlive()
        {
            // WORLD sessions only. A MenuSpace gamemode builds its overlay ON the menu and never boots a world, so
            // "no world + menu scene" is its NORMAL, healthy state - this watchdog would have killed every such session
            // three seconds in. The signal is only meaningful where a world is supposed to be running.
            if (_desc == null || _desc.Surface != GamemodeSurface.World) { _backAtMenuFor = 0f; return; }

            bool onMenu;
            try { onMenu = !WorldBoot.IsInGame && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Menu"; }
            catch (Exception e)
            {
                if (!_aliveCheckFailedLogged) { _aliveCheckFailedLogged = true; Core.Log?.Warning("[mp] session liveness check failed: " + e.Message); }
                return;
            }

            if (!onMenu) { _backAtMenuFor = 0f; return; }

            // A short dwell keeps a normal, deliberate exit (which reaches Idle on its own within a frame or two)
            // from being reported as a failure.
            _backAtMenuFor += Time.unscaledDeltaTime;
            if (_backAtMenuFor < 3f) return;
            _backAtMenuFor = 0f;

            AbortToHub("the session stopped unexpectedly");
        }

        private static void AbortToHub(string reason)
        {
            _backAtMenuFor = 0f;
            Core.Log?.Warning("[mp] aborting session: " + reason);
            // Tell the player too, not just the log. Shown once they are back in the menu - right here there is
            // nothing to draw on, and the scene reload below would destroy it anyway.
            Menu.SessionNotice.Set(reason);

            // The gamemode hears about the end of its session whether or not its own callback is what failed.
            NotifyExit(_ctx ?? (_desc != null ? new LaunchContext { Descriptor = _desc } : null));

            Safe("unlist the lobby", LobbyCoordinator.Unlist);
            // We may be aborting DURING a load that never finished (the classic stalled join: scene is still Menu, so
            // IsInGame is false). The game's loading screen is open on top of the menu and nothing else will ever
            // close it - leaving the player frozen at "Loading world..." with no way back. TakeDownSession closes it
            // and drops the lobby membership by hand.
            bool wasInGame = false;
            Safe("take the session down", () => wasInGame = WorldBoot.TakeDownSession());
            if (wasInGame) PendingHubReopen = true;
            Teardown();
            if (!wasInGame) Menu.Hub.ReopenAfterSession();
        }

        private static string SessionOrgName()
        {
            return _desc != null ? (_desc.DisplayName ?? "Side Hustle") + " Session" : "Side Hustle Session";
        }

        // First 8 hex of a build fingerprint - enough to read in a log without dumping the full 32-char MVID.
        private static string Short(string buildId) =>
            string.IsNullOrEmpty(buildId) ? "?" : (buildId.Length > 8 ? buildId.Substring(0, 8) : buildId);

        private static void SafeInvoke(Action<LaunchContext> cb, LaunchContext ctx)
        {
            try { cb?.Invoke(ctx); }
            catch (Exception e)
            {
                Core.Log?.Error($"Gamemode '{_desc?.Id}' multiplayer callback threw: {e}");
                AbortToHub("gamemode callback threw");
            }
        }
    }
}
