using System;
using UnityEngine;

namespace SideHustle.Multiplayer
{
    /// <summary>
    /// Drives a multiplayer (or World-singleplayer) gamemode session through its lifecycle. Tick() is pumped from
    /// Core.OnUpdate. The flow is a small state machine because lobby creation and world loading are asynchronous:
    ///
    ///   HOST:  create public lobby -> (singleton flips) tag it -> boot the world (World surface) -> OnHostMultiplayer
    ///   JOIN:  JoinLobby -> the game's OnLobbyEntered streams the host's world in -> OnJoinMultiplayer
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

        // --- world-load watchdog -------------------------------------------------------------------
        // A world load must not be policed by a single flat deadline. The load reports its progress only
        // as coarse phase changes, and one phase - LoadingScene, which wraps Unity's LoadSceneAsync -
        // reports nothing at all until it is done. A flat timeout therefore does not measure "stuck", it
        // measures "slower than my machine": weak PCs and slow connections got aborted mid-load, and
        // because the abort left the loading screen open that looked like an infinite loading screen.
        // So: give the blind scene-loading phase a generous budget of its own, and police every other
        // phase by how long it has sat WITHOUT changing. A genuinely dead load still ends, just not a
        // merely slow one.
        private const float StallTimeout = 90f;    // no phase change at all -> really stuck
        private const float SceneTimeout = 420f;   // LoadingScene is opaque; only a hard ceiling fits
        private const float HardCeiling = 900f;    // last resort, whatever the phase claims

        private static float _stallTimer;
        private static string _lastProgress;

        /// <summary>Reset the watchdog at the start of every wait (a new load is not the previous one's stall).</summary>
        private static void ResetWatchdog()
        {
            _timer = 0f;
            _stallTimer = 0f;
            _lastProgress = null;
        }

        /// <summary>
        /// Advance the watchdog and report whether this load should be given up on. Progress is any change to
        /// the scene, the load phase, or the phase's own detail text - the last one matters because the
        /// syncing phase names the task it is replicating, so a slow but healthy sync keeps resetting the stall
        /// timer instead of tripping it.
        /// </summary>
        private static bool LoadHasGivenUp(out string why)
        {
            string progress = WorldBoot.ProgressSignature();
            if (progress != _lastProgress) { _lastProgress = progress; _stallTimer = 0f; }
            else _stallTimer += Time.unscaledDeltaTime;

            bool blindScenePhase = WorldBoot.IsLoadingScene;
            float limit = blindScenePhase ? SceneTimeout : StallTimeout;

            if (_stallTimer > limit)
            {
                why = blindScenePhase
                    ? $"scene load exceeded {SceneTimeout:F0}s (scene={WorldBoot.CurrentScene}, status={WorldBoot.LoadStatus})"
                    : $"no progress for {StallTimeout:F0}s (scene={WorldBoot.CurrentScene}, status={WorldBoot.LoadStatus})";
                return true;
            }
            if (_timer > HardCeiling)
            {
                why = $"load exceeded {HardCeiling:F0}s (scene={WorldBoot.CurrentScene}, status={WorldBoot.LoadStatus})";
                return true;
            }
            why = null;
            return false;
        }

        /// <summary>Set when a session ended via a full scene reload; Core reopens the hub on the next Menu init.</summary>
        internal static bool PendingHubReopen;

        internal static bool IsBusy => _state != State.Idle;

        // --- entry points (called by the Hub UI) ---

        internal static void StartHost(GamemodeDescriptor desc, HostOptions opts)
        {
            if (desc == null) return;
            if (desc.OnHostMultiplayer == null)
            {
                Core.Log?.Warning($"Gamemode '{desc.Id}' has no host callback; cannot host.");
                return;
            }
            _desc = desc;
            _hostOpts = opts ?? new HostOptions();
            _ctx = null;
            ResetWatchdog();
            GamemodeHygiene.Apply(desc);   // skip-intro / block-quests must be active before the world loads
            NetworkTuning.EnsureIceEnabled();   // allow all P2P ICE candidate types so non-friend clients can reach this host
            PublicLobbyAccess.Enable();   // stop the vanilla host from kicking non-friends, so public lobbies actually work
            LobbyInviteAccess.Enable();   // let any lobby member (incl. clients) invite Steam friends from the pause panel
            PlayerAlias.Enable(Config.Preferences.GetAlias(desc.Id));   // show the player's chosen (per-gamemode) display name this session

            Core.Log?.Msg($"[mp] hosting '{desc.DisplayName}' (max {_hostOpts.MaxPlayers}, {_hostOpts.Visibility})...");
            if (!LobbyCoordinator.CreateLobby(_hostOpts.MaxPlayers, _hostOpts.Visibility)) { AbortToHub("could not create a lobby"); return; }
            _state = State.HostCreatingLobby;
        }

        internal static void StartJoin(GamemodeDescriptor desc, LobbyRow row)
        {
            if (desc == null || row == null) return;
            if (desc.OnJoinMultiplayer == null)
            {
                Core.Log?.Warning($"Gamemode '{desc.Id}' has no join callback; cannot join.");
                return;
            }
            _desc = desc;
            _joinLobbyId = row.LobbyId;
            _ctx = null;
            ResetWatchdog();
            GamemodeHygiene.Apply(desc);   // active before the host's world streams in (the client also runs PlayerLoaded)
            NetworkTuning.EnsureIceEnabled();   // allow all P2P ICE candidate types so this join can hold to a non-friend host
            LobbyInviteAccess.Enable();   // let this client invite Steam friends from the pause-menu lobby panel
            PlayerAlias.Enable(Config.Preferences.GetAlias(desc.Id));   // show the player's chosen (per-gamemode) display name this session

            Core.Log?.Msg($"[mp] joining '{desc.DisplayName}' lobby {row.LobbyId}...");
            LobbyCoordinator.JoinLobby(row.LobbyId);
            _state = State.Joining;
        }

        /// <summary>Boot a throwaway world for a Surface=World singleplayer gamemode (no lobby).</summary>
        internal static void StartWorldSingleplayer(GamemodeDescriptor desc)
        {
            if (desc == null) return;
            if (desc.OnLaunchSingleplayer == null)
            {
                Core.Log?.Warning($"Gamemode '{desc.Id}' has no singleplayer callback.");
                return;
            }
            _desc = desc;
            _ctx = null;
            ResetWatchdog();
            GamemodeHygiene.Apply(desc);
            Core.Log?.Msg($"[mp] booting singleplayer world for '{desc.DisplayName}'...");
            if (!WorldBoot.BootHostWorld(SessionOrgName())) { AbortToHub("world boot failed"); return; }
            _state = State.SpBootingWorld;
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
                        LobbyCoordinator.TagLobby(_desc, _hostOpts);
                        if (_desc.Surface == GamemodeSurface.World)
                        {
                            if (!WorldBoot.BootHostWorld(SessionOrgName())) { AbortToHub("world boot failed"); break; }
                            ResetWatchdog();
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
                    else if (LoadHasGivenUp(out string hostWhy)) AbortToHub("world not ready - " + hostWhy);
                    break;

                case State.Joining:
                    if (WorldBoot.IsWorldReady()) FireJoin();
                    else if (LoadHasGivenUp(out string joinWhy)) AbortToHub("join did not complete - " + joinWhy);
                    break;

                case State.SpBootingWorld:
                    if (WorldBoot.IsWorldReady()) FireSpWorld();
                    else if (LoadHasGivenUp(out string spWhy)) AbortToHub("world not ready - " + spWhy);
                    break;
            }
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
        }

        private static void FireSpWorld()
        {
            _ctx = new LaunchContext { Descriptor = _desc, IsHost = null, LobbyId = 0, PlayerCount = 1 };
            _state = State.InSession;
            Core.Log?.Msg($"[mp] singleplayer world ready: '{_desc.DisplayName}'.");
            SafeInvoke(_desc.OnLaunchSingleplayer, _ctx);
        }

        // --- return / teardown (called via HubBridge when ctx.ReturnToHub fires for a World/MP session) ---

        internal static void ReturnFromSession(LaunchContext ctx)
        {
            try { ctx?.Descriptor?.OnExitToHub?.Invoke(ctx); }
            catch (Exception e) { Core.Log?.Warning("OnExitToHub threw: " + e.Message); }

            bool host = ctx != null && ctx.IsHost == true;
            if (host) LobbyCoordinator.Unlist();

            bool wasInGame = WorldBoot.IsInGame;
            if (wasInGame)
            {
                WorldBoot.ExitToMenu();        // also leaves the Steam lobby
                PendingHubReopen = true;       // reopen the hub when the Menu scene re-initializes
            }

            GamemodeHygiene.Clear();
            PublicLobbyAccess.Disable();   // restore the vanilla non-friend kick outside a Side Hustle session
            PlayerAlias.Disable();         // stop aliasing; the next session uses the real Steam name again
            LobbyInviteAccess.Disable();   // restore the vanilla host-only invite button outside a Side Hustle session
            WorldBoot.CleanupScratch();
            _state = State.Idle;
            _ctx = null;

            if (!wasInGame)
            {
                // MenuSpace MP (no scene reload): the menu is still here - reopen the hub directly.
                Menu.Hub.ReopenAfterSession();
            }
        }

        private static float _backAtMenuFor;

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
            catch { return; }

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
            if (_desc != null && _ctx == null)
            {
                try { _desc.OnExitToHub?.Invoke(new LaunchContext { Descriptor = _desc }); } catch { /* ignore */ }
            }
            LobbyCoordinator.Unlist();
            bool wasInGame = WorldBoot.IsInGame;
            if (wasInGame) { WorldBoot.ExitToMenu(); PendingHubReopen = true; }
            // We may be aborting DURING a load that never finished (the classic stalled join: scene is still
            // Menu, so IsInGame is false and the branch above does nothing). The game's loading screen is open
            // on top of the menu and nothing else will ever close it - leaving the player frozen at
            // "Loading world..." with no way back. Take it down and drop the lobby membership by hand.
            else if (WorldBoot.AbortLoadToMenu()) LobbyCoordinator.LeaveCurrentLobby();
            GamemodeHygiene.Clear();
            PublicLobbyAccess.Disable();   // restore the vanilla non-friend kick outside a Side Hustle session
            PlayerAlias.Disable();         // stop aliasing; the next session uses the real Steam name again
            LobbyInviteAccess.Disable();   // restore the vanilla host-only invite button outside a Side Hustle session
            WorldBoot.CleanupScratch();
            _state = State.Idle;
            _ctx = null;
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
