using System;
using System.Linq;
using MelonLoader;
using SideHustle.Config;
using SideHustle.Menu;

[assembly: MelonInfo(typeof(SideHustle.Core), "Side Hustle", "2.0.0", "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-SideHustle")]
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

        public override void OnInitializeMelon()
        {
            Instance = this;
            Log = LoggerInstance;

            Preferences.Initialize();
            API.IsReady = true;

            // Native bigger lobbies - raise the co-op cap ourselves (no external BiggerLobbies dependency).
            // Idempotent + single-flight guarded, so a standalone FullHouse.dll or BiggerLobbies alongside is fine.
            DooDesch.FullHouse.Lobbies.Install();

            // Keep the boot-time profile picker (a MelonPlugin, shipped embedded) installed and current.
            Profiles.BootInstaller.EnsureInstalled();
            Profiles.ThunderstoreClient.Log = s => Log?.Warning("[profiles] " + s);

            // The live-publish button (pause-menu lobby panel) patch - inert until a co-op host is eligible.
            Sync.LivePublish.Install();

            // Guarantee a co-op client can always quit back to the menu (vanilla ExitToMenu can silently no-op).
            Multiplayer.ClientExitGuard.Install();

            // Keep ticking when the window is unfocused, so a post-restart auto-continue still fires.
            try { UnityEngine.Application.runInBackground = true; } catch { /* ignore */ }

#if DEBUG
            Dev.StubGamemode.Register();
#endif

            Log.Msg($"Side Hustle 2.0.0 ready - {API.Registered.Count} gamemode(s) registered so far.");
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

                // A pending vanilla-sync rejoin means this session base is a SYNC profile, built from the HOST's bytes
                // on purpose. It must never be measured against the local install: a host running a different build of
                // a shared mod would always read as "stale" and bounce back, dropping the rejoin token and stranding
                // the player in the menu. Only real gamemode policy profiles get the staleness restore below.
                // Keyed on BOTH the not-yet-consumed token AND the already-latched payload: the "Menu" scene can
                // re-initialise within a single menu load (see MenuInjector), and the first pass consumes the token
                // into _vanillaJoinPayload below - without the second term, a re-init would see the cleared token,
                // treat the sync profile as a normal policy profile, and RestoreAndRestart() its host-built (always
                // "stale") mods, re-dropping the rejoin this exemption exists to protect.
                bool syncJoinPending = policySession
                    && (!string.IsNullOrEmpty(Preferences.PendingVanillaJoin) || !string.IsNullOrEmpty(_vanillaJoinPayload));

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
                _continueId = null;
                _continueHost = null;
                _runtimeNoticeFrames = 0;
                _runtimeNoticeProfileId = null;
                Hub.ResetAdvertised();
                Hub.Teardown();
                MenuInjector.Reset();
            }
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
            Multiplayer.ClientExitGuard.TickWatchdog();   // recover a kicked/dropped client stranded on a loading screen

            // The Messenger backend runs whenever we're in a lobby; its phone app refreshes only while open.
            Messenger.ChatService.Tick();
            Messenger.MessengerApp.Instance?.Tick();

#if DEBUG
            Dev.SelfTest.TickChatService();
#endif

#if DEBUG
            Dev.ChatSmoke.Tick();
#endif

            if (_inMenu)
            {
                MenuInjector.TickRetry();
                DooDesch.UI.SmoothScroll.Tick();   // smooth wheel glide for menu lists (host-config form, etc.)
                DooDesch.UI.Toast.Tick();          // profile-manager toasts (removals, install results)
                Hub.TickInput();   // right-click steps one view back (mod-check, host/join choice, browser, ...)
                Menu.SyncManualInstallView.Tick();   // poll the staging folder while the manual checklist is open
                if (_reopenHubFrames > 0 && --_reopenHubFrames == 0)
                {
                    if (!string.IsNullOrEmpty(_vanillaJoinPayload)) { var p = _vanillaJoinPayload; _vanillaJoinPayload = null; Sync.SyncCoordinator.ContinueJoin(p); }
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
