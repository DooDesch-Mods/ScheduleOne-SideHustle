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
        private static bool _handshakeWritten;   // the sh_sync member data is on the lobby (read back)
        private static float _handshakeRetry;
        private static readonly LoadWatchdog _watchdog = new LoadWatchdog();   // host world boot and client world load
        private static string _lastRefusal = "";

        /// <summary>Why the last start call returned false, in words a player can read. Empty after a successful start.</summary>
        internal static string LastRefusal => _lastRefusal;

        /// <summary>How long a joined-but-idle client waits before calling it. Deliberately short: this is the state
        /// where the game will never start loading at all (see <see cref="Multiplayer.WorldBoot.LoadStarted"/>), so
        /// waiting longer only wastes the player's evening. The two-minute ceiling below still covers a load that has
        /// actually begun and is merely slow.</summary>
        private const float NeverStartedSeconds = 15f;

        internal static bool IsBusy => _state != State.Idle;
        internal static bool IsInSession => _state == State.InSession;

        /// <summary>Whether this process is holding a mod list it could advertise. False in someone else's session and
        /// in a lobby opened with the pause-menu Publish button, where "no mod list on the lobby" is simply how that
        /// lobby is and not something the host can fix.</summary>
        internal static bool HasModList => !string.IsNullOrEmpty(_manifestText);

        /// <summary>Whether a vanilla co-op start/join may begin: neither coordinator may be mid-session, or a double
        /// click (or a start from the other flow) would open a second lobby on top of the first.</summary>
        private static bool CanStart(string what)
        {
            if (_state != State.Idle)
            {
                _lastRefusal = "A session is already starting.";
                Core.Log?.Warning($"[sync] {what} refused: a vanilla co-op session is already {_state}.");
                return false;
            }
            if (MultiplayerCoordinator.IsBusy)
            {
                _lastRefusal = "A gamemode session is already starting.";
                Core.Log?.Warning($"[sync] {what} refused: a gamemode session is in progress.");
                return false;
            }
            _lastRefusal = "";
            return true;
        }

        internal static bool StartHostVanilla(Il2CppScheduleOne.Persistence.SaveInfo save, HostOptions opts,
            string manifestText, string prefsText, string modSummary, bool enforce)
        {
            if (!CanStart("hosting")) return false;
            if (save == null) { Core.Log?.Warning("[sync] no save selected."); _lastRefusal = "No save selected."; return false; }

            _save = save;
            _opts = opts ?? new HostOptions();
            _manifestText = manifestText ?? "";
            _prefsText = prefsText ?? "";
            _modSummary = modSummary ?? "";
            _enforce = enforce;
            try { _org = save.OrganisationName; }
            catch (Exception e) { _org = ""; Core.Log?.Warning("[sync] the save's organisation name is unreadable: " + e.Message); }
            _state = State.HostCreatingLobby;   // claimed before anything else, so a second click is refused

            // The session is what the mods are for, so this is where they load. On a normal boot the gate never
            // armed and this returns before touching anything. Before the lobby, not after: a mod that hooks the
            // lobby has to exist by the time one is created.
            Mods.LateLoader.LoadAll("hosting a session");

            NetworkTuning.EnsureIceEnabled();   // non-friend clients need all ICE candidate types
            PublicLobbyAccess.Enable();         // stop the vanilla host from kicking non-friends
            LobbyInviteAccess.Enable();         // every member may invite from the pause panel
            PlayerAlias.Enable(Config.Preferences.GetAlias("vanilla"));   // the host's chosen "Your name" for vanilla lobbies

            Core.Log?.Msg($"[sync] hosting vanilla save '{_org}' publicly (max {_opts.MaxPlayers})...");
            _timer = 0f;
            if (!LobbyCoordinator.CreateLobby(_opts.MaxPlayers, _opts.Visibility)) { Abort("could not create a lobby"); return false; }
            return true;
        }

        /// <summary>
        /// Post-restart rejoin (payload = ConfigCodec {lobby, mhash} from the profile's continue token): request
        /// fresh lobby data, verify the lobby still exists with the SAME manifest, then join - entering the
        /// lobby immediately triggers the vanilla world pull, so every check happens before that. A dead lobby
        /// or a changed manifest leaves the player safely in the menu (the profile session still offers
        /// "Restore my mods").
        /// </summary>
        internal static bool ContinueJoin(string payload)
        {
            if (!CanStart("rejoin"))
            {
                Menu.RejoinNotice.Hide();
                return false;
            }
            var map = ConfigCodec.Decode(payload);
            if (!map.TryGetValue("lobby", out var ls) || !ulong.TryParse(ls, out ulong parsedLobby) || parsedLobby == 0)
            {
                Core.Log?.Warning("[sync] rejoin token unreadable; staying in the menu.");
                Menu.RejoinNotice.Hide();
                return false;
            }
            _joinLobbyId = parsedLobby;
            map.TryGetValue("mhash", out _joinMHash);

            // This process was relaunched into the host's mod set; the gate held it back on the way up, so load it
            // before the lobby rather than arriving in their session with nothing but the hub running.
            Mods.LateLoader.LoadAll("rejoining a session");

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
                    return false;
                }
            }
            catch (Exception e) { RejoinFailed("lobby lookup failed: " + e.Message); return false; }

            Core.Log?.Msg($"[sync] rejoining lobby {_joinLobbyId} after the mod-sync restart...");
            Menu.RejoinNotice.Show("Looking for the lobby and checking its mod list has not changed...");
            Menu.RejoinNotice.SetCancel(CancelRejoin);
            _timer = 0f;
            _lobbyRetry = 0f;
            _loadStarted = false;
            _handshakeWritten = false;
            // Prime lobby discovery: a fresh (just-restarted) client must re-learn the host's lobby exists before
            // RequestLobbyData will resolve it. A vanilla lobby-list query repopulates that registry.
            try { Multiplayer.ServerBrowser.BeginQueryVanilla(_ => { }); }
            catch (Exception e) { Core.Log?.Warning("[sync] lobby discovery could not be primed: " + e.Message); }
            _state = State.ClientCheckingLobby;
            return true;
        }

        /// <summary>Join a lobby whose mods already match ours in place (no restart needed) but still announce the
        /// synced handshake: an enforcing host's gate reads the sh_sync member data, so a client that skipped the
        /// restart path would otherwise be kicked as "unsynced". Reuses the ClientJoining wait-for-world + sh_sync.</summary>
        internal static bool StartInPlaceJoin(ulong lobbyId, string mhash)
        {
            if (!CanStart("in-place join")) return false;
            if (lobbyId == 0) { Core.Log?.Warning("[sync] in-place join refused: no lobby id."); _lastRefusal = "No lobby selected."; return false; }
            _state = State.ClientJoining;   // claimed before anything else, so a second click is refused
            // The one path where the gate pays off immediately: our mods already match the host's, so loading them
            // here and joining is the whole join - no profile, no relaunch.
            Mods.LateLoader.LoadAll("joining a session");
            _isClient = true;
            _joinLobbyId = lobbyId;
            _joinMHash = mhash ?? "";
            NetworkTuning.EnsureIceEnabled();
            LobbyInviteAccess.Enable();
            PlayerAlias.Enable(Config.Preferences.GetAlias("vanilla"));
            _timer = 0f;
            _loadStarted = false;
            _handshakeWritten = false;
            _watchdog.Reset();
            Menu.RejoinNotice.SetCancel(CancelRejoin);   // no notice up on this path yet, but the step owns the way out
            Core.Log?.Msg($"[sync] in-place synced join to lobby {lobbyId}.");
            if (!LobbyCoordinator.JoinLobby(lobbyId)) { RejoinFailed("Steam did not accept the join request"); return false; }
            return true;   // the state machine waits for the world, sets sh_sync, then InSession
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
            catch (Exception e) { Core.Log?.Warning("[sync] could not show a notice: " + e.Message); }
        }

        internal static void Tick()
        {
            if (_state == State.Idle || _state == State.InSession) return;
            _timer += Internal.WaitClock.Step;

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
                        _timer = 0f;
                        _loadStarted = false;
                        _handshakeWritten = false;
                        _watchdog.Reset();
                        _state = State.ClientJoining;
                        if (!LobbyCoordinator.JoinLobby(_joinLobbyId)) RejoinFailed("Steam did not accept the join request");
                    }
                    else if (_timer > 20f) RejoinFailed("the lobby could not be reached (host may have left, or a network issue)");
                    else
                    {
                        // Keep re-requesting the lobby data and re-running discovery while we wait: a restarted
                        // client can take a couple of seconds to see the still-open host lobby.
                        _lobbyRetry += Internal.WaitClock.Step;
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
                    // The handshake goes onto the lobby as soon as we are in it, not when the world is up: the host's
                    // gate starts each member's grace at lobby entry, and a slow load must not read as "unsynced".
                    if (!_handshakeWritten && LobbyCoordinator.IsInLobby && LobbyCoordinator.CurrentLobbyId == _joinLobbyId)
                    {
                        _handshakeRetry -= Time.unscaledDeltaTime;
                        if (_handshakeRetry <= 0f)
                        {
                            _handshakeWritten = WriteSyncHandshake("lobby entered");
                            _handshakeRetry = 2f;
                        }
                    }
                    if (WorldBoot.IsWorldReady())
                    {
                        WriteSyncHandshake("world ready");   // re-asserted once the world is up; also logs if it did not stick
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
                    else if (_watchdog.HasGivenUp(out string loadWhy))
                        RejoinFailed("world never arrived - " + loadWhy);
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
                        _watchdog.Reset();
                        _state = State.HostBootingWorld;
                    }
                    else if (_timer > 10f) Abort("lobby did not open within 10s");
                    break;

                case State.HostBootingWorld:
                    if (WorldBoot.IsWorldReady())
                    {
                        _state = State.InSession;
                        // Arm from the hash the LOBBY carries, not from the one we computed. They are the same hash
                        // whenever the write landed, and when it did not there is nothing for a joiner to sync
                        // against - arming anyway is how a session came to remove every player who tried to join a
                        // mod list they could not read.
                        if (_enforce)
                        {
                            string published = VanillaLobby.PublishedMHash();
                            if (!string.IsNullOrEmpty(published)) SyncGate.Enable(published);
                            else
                            {
                                _enforce = false;
                                VanillaLobby.AdvertiseEnforce(false);
                                Core.Log?.Warning("[sync] this lobby carries no mod-set hash, so the requirement is "
                                                  + "off - nobody is removed for a list they cannot read. Publish "
                                                  + "again from the Lobby app to turn it back on.");
                            }
                        }
                        Core.Log?.Msg($"[sync] vanilla session live: '{_org}' lobby {LobbyCoordinator.CurrentLobbyId}, " +
                                      $"{LobbyCoordinator.MemberCount} player(s), enforce={_enforce}.");
                    }
                    else if (_watchdog.HasGivenUp(out string hostWhy)) Abort("world not ready - " + hostWhy);
                    break;
            }
        }

        /// <summary>Pumped from Core.OnUpdate: an enforcing host scans for unsynced members and kicks them. The backend
        /// directory heartbeat runs separately in VanillaLobby (every frame), so a live-published lobby that is not a
        /// Side Hustle session is kept alive on the web directory too.</summary>
        internal static void TickGate()
        {
            // The GATE's own armed flag decides whether to scan, not this coordinator's state. Enforcement can be
            // switched mid-session from the phone app, and a lobby published with the in-game button is not a
            // coordinator session at all - asking two flags is how the switch came to move the advertisement and
            // leave the kicking exactly as it was.
            if (!SyncGate.IsActive || _isClient) return;
            if (!LobbyCoordinator.IsInLobby || !LobbyCoordinator.IsHost) return;
            SyncGate.Tick(LobbyCoordinator.CurrentLobbyId);
        }

        /// <summary>
        /// Move the mod-set requirement of the session running right now - the kick with it.
        /// </summary>
        /// <remarks>
        /// The host toggles this in the phone app, where it reads as one switch, so it has to be one. Turning it OFF
        /// used to change the advertisement only: the gate kept scanning and kept removing people for a rule the host
        /// had already dropped.
        ///
        /// <paramref name="expectedMHash"/> is the lobby's own published <c>sh_mhash</c>, which is exactly what a
        /// synced client writes into its <c>sh_sync</c> member data - so this arms correctly for a live-published
        /// lobby too, not just a session this coordinator started. Returns false when there is nothing to check
        /// joiners against, and then arms nothing rather than kicking everybody.
        /// </remarks>
        internal static bool SetEnforce(bool enforce, string expectedMHash)
        {
            _enforce = enforce;
            if (!enforce) { SyncGate.Disable(); return true; }

            // Nothing published to check against - but this session may still HOLD its mod list, in which case the
            // right answer is to advertise it again rather than refuse. Without this the switch was one-way: turning
            // the requirement off and back on left the host stuck on "this session publishes no mod list", with no way
            // back short of re-hosting.
            if (string.IsNullOrEmpty(expectedMHash)) expectedMHash = RepublishModList();

            if (string.IsNullOrEmpty(expectedMHash)) { SyncGate.Disable(); return false; }
            return SyncGate.Enable(expectedMHash);   // false on a gamemode lobby: the gate refuses and says why
        }

        /// <summary>
        /// Write this session's mod list onto the current lobby again and answer its hash, or "" when there is nothing
        /// to write.
        ///
        /// "Nothing to write" is a real case and not a failure: the lobby may be one this coordinator never hosted -
        /// someone else's session, or one published with the pause-menu button - and then this process holds no
        /// manifest to advertise.
        /// </summary>
        internal static string RepublishModList()
        {
            try
            {
                if (string.IsNullOrEmpty(_manifestText) || !LobbyCoordinator.IsHost) return "";
                if (!VanillaLobby.Tag(_opts, _manifestText, _prefsText, _enforce, _org, _modSummary)) return "";

                string mhash = SyncCodec.Hash(_manifestText, _prefsText);
                Core.Log?.Msg("[sync] re-advertised this session's mod set (" + _manifestText.Length + " chars).");
                return mhash;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[sync] could not re-advertise the mod set: " + e.Message);
                return "";
            }
        }

        /// <summary>
        /// Put the synced-client handshake (<c>sh_sync</c> = the host's mod-set hash) on our member data in the joined
        /// lobby, and read it back: Steam gives no result for the write, and an unverified handshake is how a synced
        /// client got kicked as "unsynced".
        /// </summary>
        private static bool WriteSyncHandshake(string when)
        {
            string value = _joinMHash ?? "";
            if (value.Length == 0)
            {
                Core.Log?.Msg($"[sync] no mod-set hash to announce ({when}); the lobby has no requirement to meet.");
                return true;
            }
            try
            {
                var lobby = new CSteamID(_joinLobbyId);
                SteamMatchmaking.SetLobbyMemberData(lobby, "sh_sync", value);
                string back = SteamMatchmaking.GetLobbyMemberData(lobby, SteamUser.GetSteamID(), "sh_sync");
                if (string.Equals(back, value, StringComparison.Ordinal))
                {
                    Core.Log?.Msg($"[sync] synced-mods handshake on the lobby ({when}).");
                    return true;
                }
                Core.Log?.Warning($"[sync] the synced-mods handshake did not stick ({when}): wrote {value}, read back '{back}'.");
                return false;
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"[sync] could not write the synced-mods handshake ({when}): {e.Message}");
                return false;
            }
        }

        /// <summary>The menu scene unloaded, which happens for exactly one reason: a world is loading. What the next
        /// menu needs to know is that a session happened in between.</summary>
        private static bool _sawWorld;

        internal static void OnLeftMenu() => _sawWorld = true;

        /// <summary>Menu scene (re)initialized: a live session ended via the vanilla save+quit flow - clean up.</summary>
        internal static void OnMenuScene()
        {
            // FIRST, before the Idle bail-out below: a lobby published with the in-game "Publish" button is NOT a
            // Side Hustle-hosted session, so _state is Idle and everything after that early return was skipped -
            // the listing survived on the website for the rest of the process. Reset is a no-op when nothing was
            // published, so it is safe to run on every menu return.
            LivePublish.Reset();

            // Same reason, and now load-bearing: the gate can be armed on a lobby this coordinator never started
            // (the phone app's switch), so an armed gate that survived into the menu would meet the NEXT session
            // still scanning - and kick everyone out of a lobby whose host never asked for a mod requirement.
            SyncGate.Disable();

            // The conversations were about getting into the session that just ended, and the remembered join
            // password belongs to the lobby that just closed. Showing either of them in the next one would be
            // wrong as well as careless.
            //
            // Only after a world, never on a bare menu init. The menu initialises at boot and again while it
            // settles, and by then a player may be halfway through asking a host whether they can get in - so
            // "the menu came up" is the wrong trigger and "we came back from a session" is the right one.
            if (_sawWorld)
            {
                _sawWorld = false;
                Phone.ChatRelay.Clear();
                Phone.LobbyControls.Reset();
                Menu.ChatPanel.ForgetConversations();
            }

            // Only a session that was actually live ends here. The Menu scene initializes several times while a
            // world is loading (and again around a rejoin), so resetting any in-flight state on those would kill the
            // very start this method is not part of; the load watchdogs own those states.
            if (_state != State.InSession) return;
            Core.Log?.Msg("[sync] vanilla session ended; cleaning up.");
            if (!_isClient) VanillaLobby.Untag();
            PublicLobbyAccess.Disable();
            LobbyInviteAccess.Disable();
            PlayerAlias.Disable();
            ResetSession();
        }

        /// <summary>Forget everything about the session that just ended, so nothing stale (above all the manifest text)
        /// can be republished by a later lobby.</summary>
        private static void ResetSession()
        {
            _state = State.Idle;
            _save = null;
            _manifestText = null;
            _prefsText = null;
            _modSummary = null;
            _org = null;
            _enforce = false;
            _isClient = false;
            _joinLobbyId = 0;
            _joinMHash = null;
            _loadStarted = false;
            _handshakeWritten = false;
        }

        private static void Abort(string reason)
        {
            Core.Log?.Warning("[sync] aborting: " + reason);
            Menu.SessionNotice.Set(reason);   // the player gets the reason on the menu, not just the log

            // A half-started load has to come down with the session, exactly as in the gamemode flow: the stalled
            // load, its loading screen and the Steam lobby membership are all still there after the state flips.
            bool wasInGame = false;
            try { VanillaLobby.Untag(); }
            catch (Exception e) { Core.Log?.Error("[sync] abort: unpublishing the lobby failed: " + e); }
            try { wasInGame = WorldBoot.TakeDownSession(); }
            catch (Exception e) { Core.Log?.Error("[sync] abort: taking the session down failed: " + e); }
            try { SyncGate.Disable(); PublicLobbyAccess.Disable(); LobbyInviteAccess.Disable(); PlayerAlias.Disable(); }
            catch (Exception e) { Core.Log?.Error("[sync] abort: restoring the session switches failed: " + e); }
            ResetSession();

            if (wasInGame) MultiplayerCoordinator.PendingHubReopen = true;   // the menu reloads; the hub reopens there
            else Menu.Hub.ReopenAfterSession();
        }
    }
}
