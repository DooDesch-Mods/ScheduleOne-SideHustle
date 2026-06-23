using MelonLoader;
using SideHustle.Config;
using SideHustle.Menu;

[assembly: MelonInfo(typeof(SideHustle.Core), "Side Hustle", "1.0.0", "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-SideHustle")]
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

        public override void OnInitializeMelon()
        {
            Instance = this;
            Log = LoggerInstance;

            Preferences.Initialize();
            API.IsReady = true;

#if DEBUG
            Dev.StubGamemode.Register();
#endif

            Log.Msg($"Side Hustle 1.0.0 ready - {API.Registered.Count} gamemode(s) registered so far.");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (sceneName == "Menu")
            {
                _inMenu = true;
                MenuInjector.Reset();
                MenuInjector.TryInject(); // one immediate attempt; OnUpdate retries if the UI isn't ready yet
            }
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            if (sceneName == "Menu")
            {
                _inMenu = false;
                Hub.Teardown();
                MenuInjector.Reset();
            }
        }

        public override void OnUpdate()
        {
            if (_inMenu) MenuInjector.TickRetry();
        }
    }
}
