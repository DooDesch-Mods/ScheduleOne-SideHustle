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
        private static bool _loadStarted;   // the game has begun going somewhere since we entered the lobby

        /// <summary>How long a joined-but-idle client waits before calling it. Deliberately short: this is the state
        /// where the game will never start loading at all (see <see cref="Multiplayer.WorldBoot.LoadStarted"/>), so
        /// waiting longer only wastes the player's evening. The two-minute ceiling below still covers a load that has
        /// actually begun and is merely slow.</summary>
        private const float NeverStartedSeconds = 15f;

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
                Menu.RejoinNotice.Hide();
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
            Menu.RejoinNotice.Show("Looking for the lobby and checking its mod list has not changed...");
            Menu.RejoinNotice.SetCancel(CancelRejoin);
            _timer = 0f;
            _lobbyRetry = 0f;
            _loadStarted = false;
            // Prime lobby discovery: a fresh (just-restarted) client must re-learn the host's lobby exists before
            // RequestLobbyData will resolve it. A vanilla lobby-list query repopulates that registry.
            try { Multiplayer.ServerBrowser.BeginQueryVanilla(_ => { }); }
            catch (Exception e) { Core.Log?.Warning("[sync] lobby discovery could not be primed: " + e.Message); }
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
            _loadStarted = false;
            Menu.RejoinNotice.SetCancel(CancelRejoin);   // no notice up on this path yet, but the step owns the way out
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
            AbandonJoin();
            // Tell the player why, instead of a silent bounce back to the menu - their mods are already restored.
            Toast("Couldn't join: " + reason + ". Your mods are restored - try again.", DooDesch.UI.Severity.Warning);
        }

        /// <summary>The player took the way out of the rejoin notice. Same teardown as a failure, different story:
        /// nothing went wrong, they simply stopped waiting.</summary>
        private static void CancelRejoin()
        {
            Core.Log?.Msg("[sync] rejoin cancelled; leaving the lobby.");
            AbandonJoin();
            Toast("Stopped looking for that lobby. Your mods are still set up for it - use \"Restore my mods\" when you're done.",
                  DooDesch.UI.Severity.Info);
        }

        /// <summary>
        /// Give up on a join in progress and hand the player back a working menu.
        ///
        /// Leaving the Steam lobby is the part that is easy to forget and rude to skip: entering it took a seat, and a
        /// membership that outlives the attempt keeps the host counting a player who never arrived - in a four-seat
        /// lobby that is a quarter of their session spent on a ghost.
        /// </summary>
        private static void AbandonJoin()
        {
            Menu.RejoinNotice.Hide();
            LobbyCoordinator.LeaveCurrentLobby();
            LobbyInviteAccess.Disable();
            PlayerAlias.Disable();   // OnMenuScene early-returns once Idle, so the alias must be cleared here too
            _state = State.Idle;
            _isClient = false;
            _loadStarted = false;
            _joinLobbyId = 0;
            _joinMHash = null;
            Menu.Hub.OpenScreen();   // land the player on the hub (with "Restore my mods") instead of a dead menu
        }

        private static void Toast(string line, DooDesch.UI.Severity severity)
        {
            try
            {
                DooDesch.UI.Toast.Init(Menu.Hub.DialogRootStatic());
                DooDesch.UI.Toast.Show(line, severity);
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
                        // The notice comes down on its own when the menu scene unloads for the world - from there the
                        // game's own loading screen is the better signal.
                        Menu.RejoinNotice.Update("Lobby verified - loading the world...");
                        LobbyCoordinator.JoinLobby(_joinLobbyId);
                        _timer = 0f;
                        _loadStarted = false;
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
                            Menu.RejoinNotice.Update($"Looking for the lobby... {(int)_timer}s of 20");
                            try { SteamMatchmaking.RequestLobbyData(new CSteamID(_joinLobbyId)); }
                            catch (Exception e) { Core.Log?.Warning("[sync] lobby data re-request failed: " + e.Message); }
                            try { Multiplayer.ServerBrowser.BeginQueryVanilla(_ => { }); }
                            catch (Exception e) { Core.Log?.Warning("[sync] lobby discovery pass failed: " + e.Message); }
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
                        break;
                    }
                    // Two different waits, and they must not share a deadline. Once the game is going somewhere it may
                    // legitimately take minutes; until then it is not slow, it is never going to start - vanilla only
                    // kicks a client's load off from the host's lobby keys on entry, so a lobby that admits us without
                    // any of them set leaves us in the menu for as long as we are willing to sit there.
                    if (WorldBoot.LoadStarted) _loadStarted = true;
                    if (!_loadStarted)
                    {
                        if (_timer > NeverStartedSeconds)
                            RejoinFailed("the host let us in but their lobby never started the game - it is not marked "
                                         + "ready for players. Ask them to unpublish and publish again");
                    }
                    else if (_timer > 120f)
                        RejoinFailed($"world never arrived (scene={WorldBoot.CurrentScene}, status={WorldBoot.LoadStatus})");
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
            // FIRST, before the Idle bail-out below: a lobby published with the in-game "Publish" button is NOT a
            // Side Hustle-hosted session, so _state is Idle and everything after that early return was skipped -
            // the listing survived on the website for the rest of the process. Reset is a no-op when nothing was
            // published, so it is safe to run on every menu return.
            LivePublish.Reset();

            if (_state == State.Idle) return;
            if (_state == State.InSession) Core.Log?.Msg("[sync] vanilla session ended; cleaning up.");
            if (!_isClient) VanillaLobby.Untag();
            SyncGate.Disable();
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
            Menu.SessionNotice.Set(reason);   // the player gets the reason on the menu, not just the log
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
