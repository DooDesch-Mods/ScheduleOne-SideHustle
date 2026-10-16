using MelonLoader;
using SideHustleShim;

[assembly: MelonInfo(typeof(ExampleGamemode.Core), "Side Hustle Example Gamemode", "1.0.0", "DooDesch")]
[assembly: MelonGame("TVGS", "Schedule I")]
// Optional: the mod loads without Side Hustle. With it, MelonLoader loads Side Hustle first.
[assembly: MelonOptionalDependencies("SideHustle")]

namespace ExampleGamemode
{
    /// <summary>
    /// Entry point. Touches no SideHustle type itself: everything that does lives in <see cref="Gamemode"/> and is
    /// reached through lambdas handed to SideHustleLink, so a missing SideHustle.dll fails inside the shim's
    /// try/catch instead of at the JIT of this method.
    /// </summary>
    public sealed class Core : MelonMod
    {
        internal static MelonLogger.Instance Log { get; private set; }

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;

            if (!SideHustleLink.TryRegister(() => Gamemode.Build(), Log)) return;

            // Feature detection by name, at runtime. "session-events" and "messages" exist since Side Hustle 3.0.0.
            if (SideHustleLink.Has("session-events") && SideHustleLink.Has("messages"))
                SideHustleLink.TryCall(() => Gamemode.Subscribe(), "lifecycle events", Log);
            else
                Log.Msg($"Side Hustle API version {SideHustleLink.ApiVersion} has no lifecycle events; running without them.");
        }
    }
}
