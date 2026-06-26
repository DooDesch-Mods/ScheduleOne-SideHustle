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
            _timer = 0f;

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
            _timer = 0f;

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
            _timer = 0f;
            Core.Log?.Msg($"[mp] booting singleplayer world for '{desc.DisplayName}'...");
            if (!WorldBoot.BootHostWorld(SessionOrgName())) { AbortToHub("world boot failed"); return; }
            _state = State.SpBootingWorld;
        }

        // --- per-frame state machine ---

        internal static void Tick()
        {
            if (_state == State.Idle || _state == State.InSession) return;
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
                            _timer = 0f;
                            _state = State.HostBootingWorld;
                        }
                        else
                        {
                            FireHost();
                        }
                    }
                    else if (_timer > 5f) AbortToHub("lobby did not open within 5s");
                    break;

                case State.HostBootingWorld:
                    if (WorldBoot.IsWorldReady()) FireHost();
                    else if (_timer > 95f) AbortToHub($"world not ready (scene={WorldBoot.CurrentScene}, status={WorldBoot.LoadStatus})");
                    break;

                case State.Joining:
                    if (WorldBoot.IsWorldReady()) FireJoin();
                    else if (_timer > 95f) AbortToHub($"join did not complete (scene={WorldBoot.CurrentScene}, status={WorldBoot.LoadStatus})");
                    break;

                case State.SpBootingWorld:
                    if (WorldBoot.IsWorldReady()) FireSpWorld();
                    else if (_timer > 95f) AbortToHub($"world not ready (scene={WorldBoot.CurrentScene}, status={WorldBoot.LoadStatus})");
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

            WorldBoot.CleanupScratch();
            _state = State.Idle;
            _ctx = null;

            if (!wasInGame)
            {
                // MenuSpace MP (no scene reload): the menu is still here - reopen the hub directly.
                Menu.Hub.ReopenAfterSession();
            }
        }

        private static void AbortToHub(string reason)
        {
            Core.Log?.Warning("[mp] aborting session: " + reason);
            if (_desc != null && _ctx == null)
            {
                try { _desc.OnExitToHub?.Invoke(new LaunchContext { Descriptor = _desc }); } catch { /* ignore */ }
            }
            LobbyCoordinator.Unlist();
            bool wasInGame = WorldBoot.IsInGame;
            if (wasInGame) { WorldBoot.ExitToMenu(); PendingHubReopen = true; }
            WorldBoot.CleanupScratch();
            _state = State.Idle;
            _ctx = null;
            if (!wasInGame) Menu.Hub.ReopenAfterSession();
        }

        private static string SessionOrgName()
        {
            return _desc != null ? (_desc.DisplayName ?? "Side Hustle") + " Session" : "Side Hustle Session";
        }

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
