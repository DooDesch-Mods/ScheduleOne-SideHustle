using MelonLoader;
using SideHustle.Config;
using SideHustle.Menu;

[assembly: MelonInfo(typeof(SideHustle.Core), "Side Hustle", "1.1.0", "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-SideHustle")]
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

        public override void OnInitializeMelon()
        {
            Instance = this;
            Log = LoggerInstance;

            Preferences.Initialize();
            API.IsReady = true;

#if DEBUG
            Dev.StubGamemode.Register();
#endif

            Log.Msg($"Side Hustle 1.1.0 ready - {API.Registered.Count} gamemode(s) registered so far.");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (sceneName == "Menu")
            {
                _inMenu = true;
                MenuInjector.Reset();
                MenuInjector.TryInject(); // one immediate attempt; OnUpdate retries if the UI isn't ready yet

                // A World/multiplayer session that just ended reloaded the menu scene: reopen the gamemode list
                // once the menu has laid out (a short delay so the cloned NewGameScreen is available).
                if (Multiplayer.MultiplayerCoordinator.PendingHubReopen)
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
                if (_reopenHubFrames > 0 && --_reopenHubFrames == 0) Hub.OpenScreen();
            }
        }
    }
}
