using MelonLoader;

namespace SideHustle.Config
{
    /// <summary>
    /// MelonPreferences wrapper. The category id is prefixed with the mod name ("SideHustle_...") so it is
    /// auto-detected by the "Mod Manager &amp; Phone App" settings UI. Side Hustle is a hub, so it only needs
    /// a couple of toggles; more arrive with the multiplayer phases.
    /// </summary>
    internal static class Preferences
    {
        private const string CategoryId = "SideHustle_01_Main";

        private static MelonPreferences_Category _category;
        private static MelonPreferences_Entry<bool> _enabled;

        internal static void Initialize()
        {
            if (_category != null) return;

            _category = MelonPreferences.CreateCategory(CategoryId, "Side Hustle (Gamemode Hub)");

            _enabled = _category.CreateEntry("Enabled", true, "Show the Side Hustle menu entry",
                "When ON, Side Hustle adds its entry to the main menu and lists installed gamemodes. " +
                "Turn OFF to hide it without uninstalling. Requires returning to the main menu to take effect.");
        }

        internal static bool Enabled => _enabled?.Value ?? true;
    }
}
