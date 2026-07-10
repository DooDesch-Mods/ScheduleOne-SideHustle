using MelonLoader;
using SideHustle.Config;
using SideHustle.Menu;

[assembly: MelonInfo(typeof(SideHustle.Core), "Side Hustle", "1.8.0", "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-SideHustle")]
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
        private string _continueId;     // a gamemode to continue into after a mod-policy restart
        private string _continueHost;   // encoded host options to host directly after a Host-triggered policy restart

        public override void OnInitializeMelon()
        {
            Instance = this;
            Log = LoggerInstance;

            Preferences.Initialize();
            API.IsReady = true;

            // Native bigger lobbies - raise the co-op cap ourselves (no external BiggerLobbies dependency).
            // Idempotent + single-flight guarded, so a standalone FullHouse.dll or BiggerLobbies alongside is fine.
            DooDesch.FullHouse.Lobbies.Install();

            // Keep ticking when the window is unfocused, so a post-restart auto-continue still fires.
            try { UnityEngine.Application.runInBackground = true; } catch { /* ignore */ }

#if DEBUG
            Dev.StubGamemode.Register();
#endif

            Log.Msg($"Side Hustle 1.8.0 ready - {API.Registered.Count} gamemode(s) registered so far.");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (sceneName == "Menu")
            {
                _inMenu = true;
                MenuInjector.Reset();   // OnUpdate injects after a short warmup, once the menu's own UI has settled

                bool policySession = Mods.AltBase.IsAltSession();

                // If this gamemode profile no longer matches your installed mods (you updated a mod - a new beta - after
                // the profile was built), it would run STALE DLLs. Bounce back to your full, current mod set: the next
                // normal launch sweeps the outdated profile, and relaunching the gamemode rebuilds it from the up-to-date
                // mods. This guarantees a profile is never silently out of date with what's installed.
                if (policySession && Mods.AltBase.ProfileIsStale())
                {
                    Log.Warning("[modpolicy] this gamemode profile is out of date with your installed mods - restoring your full set so it rebuilds fresh.");
                    Mods.ModSwitcher.RestoreAndRestart();
                    return;
                }

                if (!policySession)
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
                Hub.Teardown();
                MenuInjector.Reset();
            }
        }

        public override void OnUpdate()
        {
            // The multiplayer coordinator's state machine must advance every frame (its host/join transitions
            // happen during scene loads, not only in the menu).
            Multiplayer.MultiplayerCoordinator.Tick();

            if (_inMenu)
            {
                MenuInjector.TickRetry();
                DooDesch.UI.SmoothScroll.Tick();   // smooth wheel glide for menu lists (host-config form, etc.)
                Hub.TickInput();   // right-click steps one view back (mod-check, host/join choice, browser, ...)
                if (_reopenHubFrames > 0 && --_reopenHubFrames == 0)
                {
                    if (!string.IsNullOrEmpty(_continueId)) { var id = _continueId; var host = _continueHost; _continueId = null; _continueHost = null; Hub.ContinueGamemode(id, host); }
                    else Hub.OpenScreen();
                }
            }
        }
    }
}
