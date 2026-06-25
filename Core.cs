using MelonLoader;
using SideHustle.Config;
using SideHustle.Menu;

[assembly: MelonInfo(typeof(SideHustle.Core), "Side Hustle", "1.2.0", "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-SideHustle")]
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

        public override void OnInitializeMelon()
        {
            Instance = this;
            Log = LoggerInstance;

            Preferences.Initialize();
            API.IsReady = true;

            // Keep ticking when the window is unfocused, so a post-restart auto-continue still fires.
            try { UnityEngine.Application.runInBackground = true; } catch { /* ignore */ }

#if DEBUG
            Dev.StubGamemode.Register();
#endif

            Log.Msg($"Side Hustle 1.2.0 ready - {API.Registered.Count} gamemode(s) registered so far.");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (sceneName == "Menu")
            {
                _inMenu = true;
                MenuInjector.Reset();
                MenuInjector.TryInject(); // one immediate attempt; OnUpdate retries if the UI isn't ready yet

                // All mods are loaded by the time the menu shows: refresh the mod name->file map for the policy.
                Mods.ModInventory.RefreshNameMap();

                // After a mod-policy restart, continue straight into the gamemode (mods are now in the right state).
                string cont = Preferences.PendingContinue;
                if (!string.IsNullOrEmpty(cont))
                {
                    Preferences.PendingContinue = "";
                    _continueId = cont;
                    _reopenHubFrames = 90;
                }
                // A World/multiplayer session that just ended reloaded the menu scene: reopen the gamemode list
                // once the menu has laid out (a short delay so the cloned NewGameScreen is available).
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
                Hub.TickInput();   // right-click steps one view back (mod-check, host/join choice, browser, ...)
                if (_reopenHubFrames > 0 && --_reopenHubFrames == 0)
                {
                    if (!string.IsNullOrEmpty(_continueId)) { var id = _continueId; _continueId = null; Hub.ContinueGamemode(id); }
                    else Hub.OpenScreen();
                }
            }
        }
    }
}
