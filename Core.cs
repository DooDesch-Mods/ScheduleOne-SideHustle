using System;
using System.Linq;
using MelonLoader;
using SideHustle.Config;
using SideHustle.Menu;

[assembly: MelonInfo(typeof(SideHustle.Core), "Side Hustle", DooDesch.ModVersion.Current, "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-SideHustle")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace SideHustle
{
    /// <summary>
    /// MelonLoader entry point for Side Hustle. On init it loads preferences and marks the API ready. On the
    /// "Menu" scene it injects the Side Hustle entry (retried for a short window in case the UI is not laid out
    /// on the first frame) and tears its panel down when the menu unloads. Gamemodes register themselves
    /// through <see cref="API"/> from their own OnInitializeMelon.
    /// </summary>
    public sealed class Core : MelonMod
    {
        public static Core Instance { get; private set; }
        public static MelonLogger.Instance Log { get; private set; }

        private bool _inMenu;
        private int _reopenHubFrames;   // >0 = reopen the hub list this many frames after a session returns to Menu
        private int _runtimeNoticeFrames;         // >0 = show the wrong-runtime notice this many frames after Menu
        private string _runtimeNoticeProfileId;   // the named profile the notice belongs to
        private string _continueId;     // a gamemode to continue into after a mod-policy restart
        private string _continueHost;   // encoded host options to host directly after a Host-triggered policy restart
        private string _vanillaJoinPayload;   // encoded lobby+mhash to auto-rejoin after a mod-sync restart
        private string _gamemodeJoinPayload;  // encoded lobby+gamemode+mhash to join after installing that gamemode

        public override void OnInitializeMelon()
        {
            Instance = this;
            Log = LoggerInstance;

            Preferences.Initialize();
            API.IsReady = true;

            // Native bigger lobbies - raise the co-op cap ourselves (no external BiggerLobbies dependency).
            // Idempotent + single-flight guarded, so a standalone FullHouse.dll or BiggerLobbies alongside is fine.
            DooDesch.FullHouse.Lobbies.Install();

            // Tell the player in the console which of their mods have a newer release. Covers every loaded
            // mod that declares a GitHub repo in its MelonInfo, not just this one. Off-thread and silent on
            // failure; players can switch it off under [DooDesch] in MelonPreferences.cfg.
            DooDesch.Nudge.Nudge.Watch();

            // Keep the boot-time profile picker (a MelonPlugin, shipped embedded) installed and current.
            Profiles.BootInstaller.EnsureInstalled();
            Profiles.ThunderstoreClient.Log = s => Log?.Warning("[profiles] " + s);
            Sync.NexusLookup.Log = s => Log?.Msg("[nexus] " + s);

            // The live-publish button (pause-menu lobby panel) patch - inert until a co-op host is eligible.
            Sync.LivePublish.Install();

            // The host's in-game lobby controls, as a phone app. Registering is load-order proof and a no-op when
            // Sideload is absent, so it goes here unconditionally rather than behind a check.
            Phone.LobbyApp.Register();
            Phone.ChatRelay.Install();   // P2P-Empfang fuer Leute, die nicht beitreten koennen

            // Guarantee a co-op client can always quit back to the menu (vanilla ExitToMenu can silently no-op).
            Multiplayer.ClientExitGuard.Install();

            // Keep ticking when the window is unfocused, so a post-restart auto-continue still fires.
            try { UnityEngine.Application.runInBackground = true; } catch { /* ignore */ }

#if DEBUG
            Dev.StubGamemode.Register();
            Debugging.DevConsole.Install();
#endif

            // Version read from the assembly, never typed twice: a hardcoded string here silently lies about which
            // build a player is running the moment a release forgets to update it.
            string version = "";
            try { version = typeof(Core).Assembly.GetName().Version?.ToString(3) ?? ""; } catch { /* ignore */ }
            Log.Msg($"Side Hustle {version} ready - {API.Registered.Count} gamemode(s) registered so far.");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (sceneName == "Menu")
            {
                _inMenu = true;
                MenuInjector.Reset();   // OnUpdate injects after a short warmup, once the menu's own UI has settled
                Menu.ContinueInterstitial.EnsureInstalled();   // the "host publicly?" prompt on Continue/Load
                Sync.SyncCoordinator.OnMenuScene();   // a vanilla session that ended via save+quit cleans up here

                // Safety net: a prior host (a MenuSpace gamemode, an aborted host, or a co-op host that didn't leave
                // cleanly) can leave a stray Steam lobby alive - still public, joinable and advertised - so the player
                // shows up as joinable the moment the menu loads, before any "Host publicly". This fires only on a
                // scene transition INTO the menu (never while a live menu-lobby is open), so it can only catch a
                // leftover: if no Side Hustle session is live or starting, make sure we are not still hosting one.
                if (!Sync.SyncCoordinator.IsBusy && !Multiplayer.MultiplayerCoordinator.IsBusy)
                    Multiplayer.LobbyCoordinator.LeaveStrayHostLobby();

                Menu.Hub.PrewarmAdvertised();   // warm the not-installed-gamemode lobby cache so the list never jumps on open

                // Three kinds of session: a plain full-set launch, a TEMPORARY gamemode/sync policy base (session\),
                // and a NAMED profile's own isolated base (profiles\). Only the gamemode/sync kind gets the policy
                // handling below; a named-profile session must neither restore-to-full on staleness nor have its
                // isolated Mods captured as "the full set".
                bool namedProfileSession = Mods.AltBase.IsNamedProfileSession();
                bool policySession = Mods.AltBase.IsAltSession() && !namedProfileSession;

                // Recover the session tokens from the file beside the profile when the cfg did not carry them. Both
                // session checks above are PATH based and therefore survive a broken MelonPreferences.cfg - the tokens
                // did not, and losing them means the restart happens and the player is left in the menu with no lobby.
                // Restored into Preferences so every read below (and the staleness exemption) works unchanged.
                RestorePendingTokens(policySession);

                // A pending vanilla-sync rejoin means this session base is a SYNC profile, built from the HOST's bytes
                // on purpose. It must never be measured against the local install: a host running a different build of
                // a shared mod would always read as "stale" and bounce back, dropping the rejoin token and stranding
                // the player in the menu. Only real gamemode policy profiles get the staleness restore below.
                // Keyed on BOTH the not-yet-consumed token AND the already-latched payload: the "Menu" scene can
                // re-initialise within a single menu load (see MenuInjector), and the first pass consumes the token
                // into _vanillaJoinPayload below - without the second term, a re-init would see the cleared token,
                // treat the sync profile as a normal policy profile, and RestoreAndRestart() its host-built (always
                // "stale") mods, re-dropping the rejoin this exemption exists to protect.
                // The gamemode-install profile (join a gamemode you don't own) is built from the host's bytes exactly
                // like a sync profile, so it needs the same exemption: a client that already owns a DIFFERENT build
                // of a mod the gamemode requires would read as "stale" against its own install, bounce, and lose the
                // join token that only exists in this profile's cloned config.
                bool syncJoinPending = policySession
                    && (!string.IsNullOrEmpty(Preferences.PendingVanillaJoin) || !string.IsNullOrEmpty(_vanillaJoinPayload)
                        || !string.IsNullOrEmpty(Preferences.PendingGamemodeJoin) || !string.IsNullOrEmpty(_gamemodeJoinPayload));

                // If this gamemode profile no longer matches your installed mods (you updated a mod - a new beta - after
                // the profile was built), it would run STALE DLLs. Bounce back to your full, current mod set: the next
                // normal launch sweeps the outdated profile, and relaunching the gamemode rebuilds it from the up-to-date
                // mods. This guarantees a profile is never silently out of date with what's installed.
                if (policySession && !syncJoinPending && Mods.AltBase.ProfileIsStale())
                {
                    Log.Warning("[modpolicy] this gamemode profile is out of date with your installed mods - restoring your full set so it rebuilds fresh.");
                    Mods.ModSwitcher.RestoreAndRestart();
                    return;
                }

                if (!policySession && !namedProfileSession)
                {
                    // Normal launch: capture the full installed mod set for the policy resolver, drop any stale
                    // policy markers left by a crashed profile session (a plain launch already loads the full set,
                    // so the player is never stuck), and clean up leftover temporary profile folders.
                    Mods.ModInventory.RefreshNameMap();
                    Preferences.ActiveAltBase = "";
                    Preferences.ActiveGamemodeId = "";
                    Preferences.PendingContinue = "";
                    Preferences.PendingHostOptions = "";
                    Preferences.RestoreModOps = "";   // retire the legacy rename-based field
                    Mods.AltBase.SweepStale();
                }

#if DEBUG
                // After the sweep (a dry-run-built profile must survive it) and before the continue-token is
                // consumed below (a profile session logs the token the clone carried).
                Dev.SelfTest.TickMenu(policySession);
#endif

                // A named profile whose build dropped wrong-runtime (Mono) mods tells the player once the menu
                // has laid out - deferred past the hub auto-reopen so the notice lands on top of everything.
                if (namedProfileSession)
                {
                    string pid = Profiles.ProfileEngine.ActiveProfileId;
                    var doc = Profiles.ProfileEngine.LoadStore(out _);
                    var prof = doc.Profiles.FirstOrDefault(x => x.Id.Equals(pid, StringComparison.OrdinalIgnoreCase));
                    if (prof?.Build?.ExcludedWrongRuntime is { Count: > 0 })
                    {
                        _runtimeNoticeProfileId = pid;
                        _runtimeNoticeFrames = 150;
                    }
                }

                // After a mod-sync restart, rejoin the published vanilla lobby the player consented to.
                string vanillaJoin = policySession ? Preferences.PendingVanillaJoin : "";
                if (!string.IsNullOrEmpty(vanillaJoin))
                {
                    Preferences.PendingVanillaJoin = "";
                    _vanillaJoinPayload = vanillaJoin;
                    _reopenHubFrames = 90;
                    // Say so NOW, not in 90 frames. The game just closed and reopened itself; an idle main menu is
                    // exactly what a failed restart looks like, so the wait cannot be the first thing with no message.
                    Menu.RejoinNotice.Show("Your mods are installed. Finding the session again - don't close the game.");
                    // Nothing has been joined yet, so backing out here is just dropping the payload before it fires.
                    // The coordinator replaces this with its own way out the moment it takes the step over.
                    Menu.RejoinNotice.SetCancel(DropPendingJoin);
                }
                // After installing a gamemode we did not have, join the lobby that install was for.
                string gmJoin = policySession ? Preferences.PendingGamemodeJoin : "";
                if (!string.IsNullOrEmpty(gmJoin))
                {
                    Preferences.PendingGamemodeJoin = "";
                    _gamemodeJoinPayload = gmJoin;
                    _reopenHubFrames = 90;
                    Menu.RejoinNotice.Show("Your mods are installed. Finding the session again - don't close the game.");
                    Menu.RejoinNotice.SetCancel(DropPendingJoin);
                }
                // After relaunching into a gamemode profile, continue straight into the gamemode (mods are curated).
                string cont = policySession ? Preferences.PendingContinue : "";
                if (!string.IsNullOrEmpty(cont))
                {
                    string host = Preferences.PendingHostOptions;
                    Preferences.PendingContinue = "";
                    Preferences.PendingHostOptions = "";
                    _continueId = cont;
                    _continueHost = host;
                    _reopenHubFrames = 90;
                }
                // A World/multiplayer session that just ended reloaded the menu scene: reopen the gamemode list
                // once the menu has laid out (a short delay so the cloned NewGameScreen is available). This must run
                // in a profile session too - otherwise, after hosting a "Required only" gamemode and returning to the
                // menu, the hub never comes back and re-hosting looks broken (the flag would also leak true).
                else if (Multiplayer.MultiplayerCoordinator.PendingHubReopen)
                {
                    Multiplayer.MultiplayerCoordinator.PendingHubReopen = false;
                    _reopenHubFrames = 90;
                }
            }
        }

        /// <summary>
        /// Take the tokens a relaunch left beside the profile and fill in whatever the cfg lost.
        ///
        /// MelonPreferences is all-or-nothing: one malformed line anywhere in the file and every category falls back
        /// to its defaults, so a pending join written into the cfg simply vanishes - silently, and only for players
        /// whose config some other mod had already broken. The file this reads is ours alone and is deleted as it is
        /// read, so a token can never fire twice.
        /// </summary>
        private static void RestorePendingTokens(bool policySession)
        {
            if (!policySession) return;
            try
            {
                var tokens = Sync.PendingHandoff.TakeAll();
                if (tokens.Count == 0) return;

                int restored = 0;
                if (string.IsNullOrEmpty(Preferences.PendingVanillaJoin) && tokens.TryGetValue("PendingVanillaJoin", out var vj) && vj.Length > 0)
                { Preferences.PendingVanillaJoin = vj; restored++; }
                if (string.IsNullOrEmpty(Preferences.PendingGamemodeJoin) && tokens.TryGetValue("PendingGamemodeJoin", out var gj) && gj.Length > 0)
                { Preferences.PendingGamemodeJoin = gj; restored++; }
                if (string.IsNullOrEmpty(Preferences.PendingContinue) && tokens.TryGetValue("PendingContinue", out var pc) && pc.Length > 0)
                { Preferences.PendingContinue = pc; restored++; }
                if (string.IsNullOrEmpty(Preferences.PendingHostOptions) && tokens.TryGetValue("PendingHostOptions", out var ho) && ho.Length > 0)
                { Preferences.PendingHostOptions = ho; restored++; }
                if (string.IsNullOrEmpty(Preferences.ActiveGamemodeId) && tokens.TryGetValue("ActiveGamemodeId", out var gm) && gm.Length > 0)
                { Preferences.ActiveGamemodeId = gm; restored++; }
                if (string.IsNullOrEmpty(Preferences.ActiveAltBase) && tokens.TryGetValue("ActiveAltBase", out var ab) && ab.Length > 0)
                { Preferences.ActiveAltBase = ab; restored++; }

                if (restored > 0)
                    Log.Warning($"[sync] MelonPreferences did not carry {restored} session token(s) - recovered them from " +
                                "the profile. Something in MelonPreferences.cfg does not parse; the rest of your settings " +
                                "are on their defaults this session.");
            }
            catch (Exception e) { Log.Warning("[sync] could not restore the pending tokens: " + e.Message); }
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            if (sceneName == "Menu")
            {
                _inMenu = false;
                _reopenHubFrames = 0;
                // Clear the deferred-reopen payloads too: they are only consumed while the frame counter counts down,
                // so zeroing the counter alone would strand a payload that a later menu entry then fires as a stale
                // rejoin/continue.
                _vanillaJoinPayload = null;
                _gamemodeJoinPayload = null;
                _continueId = null;
                _continueHost = null;
                Menu.RejoinNotice.Hide();   // a rejoin that never fired must not leave its notice on the world
                _runtimeNoticeFrames = 0;
                _runtimeNoticeProfileId = null;
                Hub.ResetAdvertised();
                Hub.Teardown();
                MenuInjector.Reset();
            }
        }

        /// <summary>Quitting the game withdraws this host's web listing right away. Without it the entry lingered
        /// until the backend's ~90s TTL swept it, so the lobby browser showed a lobby nobody could join.</summary>
        public override void OnApplicationQuit()
        {
            try { Sync.VanillaLobby.UnpublishDirectoryBlocking(); } catch { /* shutting down */ }
        }

        /// <summary>
        /// The player backed out during the short window between the restart and the coordinator taking over: drop
        /// the queued rejoin so the countdown cannot fire it a moment later, and put them on the hub. Their mods stay
        /// as the sync built them - "Restore my mods" is right there in the menu.
        /// </summary>
        private void DropPendingJoin()
        {
            _vanillaJoinPayload = null;
            _gamemodeJoinPayload = null;
            _reopenHubFrames = 0;
            Hub.OpenScreen();
        }

        public override void OnUpdate()
        {
            // The multiplayer coordinator's state machine must advance every frame (its host/join transitions
            // happen during scene loads, not only in the menu).
            Multiplayer.MultiplayerCoordinator.Tick();

            // Worker-thread completions from the Profiles module (downloads, builds) land on the main thread here.
            Profiles.MainThread.Tick();

            // The vanilla-session state machine (lobby create -> tag -> save load) advances every frame too.
            Sync.SyncCoordinator.Tick();
            Sync.SyncCoordinator.TickGate();   // an enforcing host kicks unsynced members
            Sync.VanillaLobby.HeartbeatTick(UnityEngine.Time.unscaledDeltaTime);   // keep a published lobby on the web directory
            Menu.SessionNotice.Tick();   // why the last session ended - NOT gated on the menu flag, see SessionNotice
            Menu.RejoinNotice.Tick(UnityEngine.Time.unscaledDeltaTime);   // offers the way out, yields to vanilla popups
            Phone.ChatRelay.Tick();  // liest eingegangene P2P-Nachrichten
            Menu.ChatPanel.AnnounceReply();   // in the menu there is no phone to notify on - see the method
            Phone.LobbyApp.Tick();   // pushes one event to the Lobby app when the session state actually moved
            Multiplayer.ClientExitGuard.TickWatchdog();   // recover a kicked/dropped client stranded on a loading screen

            if (_inMenu)
            {
                MenuInjector.TickRetry();

                // One column at a time, and ONE owner. The state column answers a question about the main menu, so
                // it only belongs up while the main menu is what is on screen; the chat column belongs to a hub
                // screen and goes with it. Decided here rather than in either panel because the exits that matter
                // never touch a button - Esc is native, and right-click goes through Hub.TickInput.
                if (Hub.ScreenOpen) Menu.StatePanel.Suspend();
                else { Menu.ChatPanel.Hide(); Menu.StatePanel.Resume(); }

                Menu.StatePanel.Tick(UnityEngine.Time.unscaledDeltaTime);   // the right-hand state column
                Menu.ChatPanel.Tick();   // the ask-the-host column, while a join screen carries one
                DooDesch.UI.SmoothScroll.Tick();   // smooth wheel glide for menu lists (host-config form, etc.)
                DooDesch.UI.Toast.Tick();          // profile-manager toasts (removals, install results)
                Hub.TickInput();   // right-click steps one view back (mod-check, host/join choice, browser, ...)
                Hub.TickAdvertised();   // keep discovered "not installed" lobbies current while the list is open
                Menu.SyncManualInstallView.Tick();   // poll the staging folder while the manual checklist is open
                if (_reopenHubFrames > 0 && --_reopenHubFrames == 0)
                {
                    if (!string.IsNullOrEmpty(_vanillaJoinPayload)) { var p = _vanillaJoinPayload; _vanillaJoinPayload = null; Sync.SyncCoordinator.ContinueJoin(p); }
                    else if (!string.IsNullOrEmpty(_gamemodeJoinPayload)) { var p = _gamemodeJoinPayload; _gamemodeJoinPayload = null; Hub.ContinueGamemodeJoin(p); }
                    else if (!string.IsNullOrEmpty(_continueId)) { var id = _continueId; var host = _continueHost; _continueId = null; _continueHost = null; Hub.ContinueGamemode(id, host); }
                    else Hub.OpenScreen();
                }
                if (_runtimeNoticeFrames > 0 && --_runtimeNoticeFrames == 0)
                {
                    var pid = _runtimeNoticeProfileId; _runtimeNoticeProfileId = null;
                    if (!string.IsNullOrEmpty(pid)) Hub.ShowWrongRuntimeNotice(pid);
                }
            }
        }
    }
}
