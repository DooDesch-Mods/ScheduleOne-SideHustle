using System;
using Il2CppSteamworks;
using SideHustle.Multiplayer;
using UnityEngine;

namespace SideHustle.Sync
{
    /// <summary>
    /// Drives a published VANILLA co-op session (the "Sync" module's session lifecycle). Unlike a gamemode
    /// session there is no descriptor, no hygiene/alias layer and no scratch save: the host loads their own real
    /// savegame; Side Hustle only adds public discoverability, the mod manifest and (host-side) the sync gate.
    /// The Steam lobby MUST exist and be owned before StartGame runs, or the game binds its localhost-only
    /// transport and nobody can join (same rule the gamemode coordinator enforces). Tick() is pumped from
    /// Core.OnUpdate; OnMenuScene() cleans a session up when the player exits back to the menu.
    /// </summary>
    internal static class SyncCoordinator
    {
        private enum State { Idle, HostCreatingLobby, HostBootingWorld, ClientCheckingLobby, ClientJoining, InSession }

        private static State _state = State.Idle;
        private static HostOptions _opts;
        private static Il2CppScheduleOne.Persistence.SaveInfo _save;
        private static string _manifestText, _prefsText, _modSummary, _org;
        private static bool _enforce;
        private static float _timer;

        // client-side rejoin (after the mod-sync restart)
        private static ulong _joinLobbyId;
        private static string _joinMHash;
        private static bool _isClient;
        private static Callback<LobbyDataUpdate_t> _dataCallback;   // static-held: a GC'd Callback dies silently
        private static bool _dataArrived;
        private static float _lobbyRetry;   // paces the re-request/re-discover while waiting for the lobby data

        internal static bool IsBusy => _state != State.Idle;
        internal static bool IsInSession => _state == State.InSession;

        internal static void StartHostVanilla(Il2CppScheduleOne.Persistence.SaveInfo save, HostOptions opts,
            string manifestText, string prefsText, string modSummary, bool enforce)
        {
            if (_state != State.Idle) { Core.Log?.Warning("[sync] a session is already starting."); return; }
            if (save == null) { Core.Log?.Warning("[sync] no save selected."); return; }

            _save = save;
            _opts = opts ?? new HostOptions();
            _manifestText = manifestText ?? "";
            _prefsText = prefsText ?? "";
            _modSummary = modSummary ?? "";
            _enforce = enforce;
            try { _org = save.OrganisationName; } catch { _org = ""; }

            NetworkTuning.EnsureIceEnabled();   // non-friend clients need all ICE candidate types
            PublicLobbyAccess.Enable();         // stop the vanilla host from kicking non-friends
            LobbyInviteAccess.Enable();         // every member may invite from the pause panel
            PlayerAlias.Enable(Config.Preferences.GetAlias("vanilla"));   // the host's chosen "Your name" for vanilla lobbies

            Core.Log?.Msg($"[sync] hosting vanilla save '{_org}' publicly (max {_opts.MaxPlayers})...");
            if (!LobbyCoordinator.CreateLobby(_opts.MaxPlayers, _opts.Visibility)) { Abort("could not create a lobby"); return; }
            _timer = 0f;
            _state = State.HostCreatingLobby;
        }

        /// <summary>
        /// Post-restart rejoin (payload = ConfigCodec {lobby, mhash} from the profile's continue token): request
        /// fresh lobby data, verify the lobby still exists with the SAME manifest, then join - entering the
        /// lobby immediately triggers the vanilla world pull, so every check happens before that. A dead lobby
        /// or a changed manifest leaves the player safely in the menu (the profile session still offers
        /// "Restore my mods").
        /// </summary>
        internal static void ContinueJoin(string payload)
        {
            var map = ConfigCodec.Decode(payload);
            if (!map.TryGetValue("lobby", out var ls) || !ulong.TryParse(ls, out _joinLobbyId) || _joinLobbyId == 0)
            {
                Core.Log?.Warning("[sync] rejoin token unreadable; staying in the menu.");
                return;
            }
            map.TryGetValue("mhash", out _joinMHash);

            NetworkTuning.EnsureIceEnabled();
            LobbyInviteAccess.Enable();
            PlayerAlias.Enable(Config.Preferences.GetAlias("vanilla"));   // carry the joiner's chosen name into the session
            _isClient = true;
            _dataArrived = false;
            try
            {
                if (_dataCallback == null)
                    _dataCallback = Callback<LobbyDataUpdate_t>.Create((Callback<LobbyDataUpdate_t>.DispatchDelegate)OnLobbyData);
                if (!SteamMatchmaking.RequestLobbyData(new CSteamID(_joinLobbyId)))
                {
                    RejoinFailed("the lobby no longer exists");
                    return;
                }
            }
            catch (Exception e) { RejoinFailed("lobby lookup failed: " + e.Message); return; }

            Core.Log?.Msg($"[sync] rejoining lobby {_joinLobbyId} after the mod-sync restart...");
            _timer = 0f;
            _lobbyRetry = 0f;
            // Prime lobby discovery: a fresh (just-restarted) client must re-learn the host's lobby exists before
            // RequestLobbyData will resolve it. A vanilla lobby-list query repopulates that registry.
            try { Multiplayer.ServerBrowser.BeginQueryVanilla(_ => { }); } catch { }
            _state = State.ClientCheckingLobby;
        }

        /// <summary>Join a lobby whose mods already match ours in place (no restart needed) but still announce the
        /// synced handshake: an enforcing host's gate reads the sh_sync member data, so a client that skipped the
        /// restart path would otherwise be kicked as "unsynced". Reuses the ClientJoining wait-for-world + sh_sync.</summary>
        internal static void StartInPlaceJoin(ulong lobbyId, string mhash)
        {
            if (_state != State.Idle) { Core.Log?.Warning("[sync] a session is already active; ignoring in-place join."); return; }
            if (lobbyId == 0) return;
            _isClient = true;
            _joinLobbyId = lobbyId;
            _joinMHash = mhash ?? "";
            NetworkTuning.EnsureIceEnabled();
            LobbyInviteAccess.Enable();
            PlayerAlias.Enable(Config.Preferences.GetAlias("vanilla"));
            LobbyCoordinator.JoinLobby(lobbyId);
            _timer = 0f;
            _state = State.ClientJoining;   // waits for the world, sets sh_sync, then InSession
            Core.Log?.Msg($"[sync] in-place synced join to lobby {lobbyId}.");
        }

        private static void OnLobbyData(LobbyDataUpdate_t data)
        {
            if (_state != State.ClientCheckingLobby || data.m_ulSteamIDLobby != _joinLobbyId) return;
            // A just-restarted client often has not re-discovered the still-open host lobby yet, so the very first
            // request can report failure even though the lobby is alive. Don't give up here - Tick keeps
            // re-requesting (and re-running a lobby-list discovery pass) until the data arrives or the window elapses.
            if (data.m_bSuccess == 0) return;
            _dataArrived = true;
        }

        private static void RejoinFailed(string reason)
        {
            Core.Log?.Warning("[sync] rejoin failed: " + reason);
            LobbyInviteAccess.Disable();
            PlayerAlias.Disable();   // OnMenuScene early-returns once Idle, so the alias must be cleared here too
            _state = State.Idle;
            _isClient = false;
            _joinLobbyId = 0;
            _joinMHash = null;
            Menu.Hub.OpenScreen();   // land the player on the hub (with "Restore my mods") instead of a dead menu
            // Tell the player why, instead of a silent bounce back to the menu - their mods are already restored.
            try
            {
                DooDesch.UI.Toast.Init(Menu.Hub.DialogRootStatic());
                DooDesch.UI.Toast.Show("Couldn't join: " + reason + ". Your mods are restored - try again.", DooDesch.UI.Severity.Warning);
            }
            catch { /* purely cosmetic */ }
        }

        internal static void Tick()
        {
            if (_state == State.Idle || _state == State.InSession) return;
            _timer += Time.unscaledDeltaTime;

            switch (_state)
            {
                case State.ClientCheckingLobby:
                    if (_dataArrived)
                    {
                        var summary = VanillaLobby.ReadSummary(_joinLobbyId);
                        if (string.IsNullOrEmpty(summary.MHash)) { RejoinFailed("the lobby is no longer published"); break; }
                        if (!string.IsNullOrEmpty(_joinMHash) && !string.Equals(summary.MHash, _joinMHash, StringComparison.Ordinal))
                        {
                            RejoinFailed("the host changed their mods while you restarted - check the lobby again");
                            break;
                        }
                        Core.Log?.Msg("[sync] lobby verified; joining...");
                        LobbyCoordinator.JoinLobby(_joinLobbyId);
                        _timer = 0f;
                        _state = State.ClientJoining;
                    }
                    else if (_timer > 20f) RejoinFailed("the lobby could not be reached (host may have left, or a network issue)");
                    else
                    {
                        // Keep re-requesting the lobby data and re-running discovery while we wait: a restarted
                        // client can take a couple of seconds to see the still-open host lobby.
                        _lobbyRetry += Time.unscaledDeltaTime;
                        if (_lobbyRetry >= 2f)
                        {
                            _lobbyRetry = 0f;
                            try { SteamMatchmaking.RequestLobbyData(new CSteamID(_joinLobbyId)); } catch { }
                            try { Multiplayer.ServerBrowser.BeginQueryVanilla(_ => { }); } catch { }
                        }
                    }
                    break;

                case State.ClientJoining:
                    if (WorldBoot.IsWorldReady())
                    {
                        try
                        {
                            // The synced-client handshake the host's gate reads (and, later, friends see).
                            SteamMatchmaking.SetLobbyMemberData(new CSteamID(_joinLobbyId), "sh_sync", _joinMHash ?? "");
                        }
                        catch { /* member data is best-effort */ }
                        _state = State.InSession;
                        Core.Log?.Msg($"[sync] SYNCED JOIN complete: lobby {_joinLobbyId}, {LobbyCoordinator.MemberCount} player(s).");
                    }
                    else if (_timer > 120f) RejoinFailed($"world never arrived (scene={WorldBoot.CurrentScene}, status={WorldBoot.LoadStatus})");
                    break;

                case State.HostCreatingLobby:
                    if (LobbyCoordinator.IsHost)
                    {
                        VanillaLobby.Tag(_opts, _manifestText, _prefsText, _enforce, _org, _modSummary);
                        try
                        {
                            // The player's REAL save: keep the game's save backup on (unlike gamemode scratch saves).
                            Il2CppScheduleOne.DevUtilities.Singleton<Il2CppScheduleOne.Persistence.LoadManager>
                                .Instance.StartGame(_save, false, true);
                        }
                        catch (Exception e) { Abort("could not start the save: " + e.Message); break; }
                        _timer = 0f;
                        _state = State.HostBootingWorld;
                    }
                    else if (_timer > 10f) Abort("lobby did not open within 10s");
                    break;

                case State.HostBootingWorld:
                    if (WorldBoot.IsWorldReady())
                    {
                        _state = State.InSession;
                        if (_enforce) SyncGate.Enable(SyncCodec.Hash(_manifestText, _prefsText));
                        Core.Log?.Msg($"[sync] vanilla session live: '{_org}' lobby {LobbyCoordinator.CurrentLobbyId}, " +
                                      $"{LobbyCoordinator.MemberCount} player(s), enforce={_enforce}.");
                    }
                    else if (_timer > 95f) Abort($"world not ready (scene={WorldBoot.CurrentScene}, status={WorldBoot.LoadStatus})");
                    break;
            }
        }

        /// <summary>Pumped from Core.OnUpdate: an enforcing host scans for unsynced members and kicks them. The backend
        /// directory heartbeat runs separately in VanillaLobby (every frame), so a live-published lobby that is not a
        /// Side Hustle session is kept alive on the web directory too.</summary>
        internal static void TickGate()
        {
            if (_state != State.InSession || _isClient) return;
            if (_enforce) SyncGate.Tick(LobbyCoordinator.CurrentLobbyId);
        }

        /// <summary>Menu scene (re)initialized: a live session ended via the vanilla save+quit flow - clean up.</summary>
        internal static void OnMenuScene()
        {
            if (_state == State.Idle) return;
            if (_state == State.InSession) Core.Log?.Msg("[sync] vanilla session ended; cleaning up.");
            if (!_isClient) VanillaLobby.Untag();
            SyncGate.Disable();
            LivePublish.Reset();
            PublicLobbyAccess.Disable();
            LobbyInviteAccess.Disable();
            PlayerAlias.Disable();
            _state = State.Idle;
            _save = null;
            _isClient = false;
            _joinLobbyId = 0;
        }

        private static void Abort(string reason)
        {
            Core.Log?.Warning("[sync] aborting: " + reason);
            VanillaLobby.Untag();
            PublicLobbyAccess.Disable();
            LobbyInviteAccess.Disable();
            PlayerAlias.Disable();
            _state = State.Idle;
            _save = null;
            Menu.Hub.ReopenAfterSession();
        }
    }
}
