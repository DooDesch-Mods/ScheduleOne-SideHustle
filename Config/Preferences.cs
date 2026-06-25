using System.Collections.Generic;
using System.Linq;
using MelonLoader;

namespace SideHustle.Config
{
    /// <summary>
    /// MelonPreferences wrapper. The category id is prefixed with the mod name ("SideHustle_...") so it is
    /// auto-detected by the "Mod Manager &amp; Phone App" settings UI. Besides the visibility toggle it
    /// remembers the recently launched gamemodes so the hub can surface them first.
    /// </summary>
    internal static class Preferences
    {
        private const string CategoryId = "SideHustle_01_Main";
        private const int RecentMax = 10;

        private static MelonPreferences_Category _category;
        private static MelonPreferences_Entry<bool> _enabled;
        private static MelonPreferences_Entry<string> _recent;

        internal static void Initialize()
        {
            if (_category != null) return;

            _category = MelonPreferences.CreateCategory(CategoryId, "Side Hustle (Gamemode Hub)");

            _enabled = _category.CreateEntry("Enabled", true, "Show the Side Hustle menu entry",
                "When ON, Side Hustle adds its entry to the main menu and lists installed gamemodes. " +
                "Turn OFF to hide it without uninstalling. Requires returning to the main menu to take effect.");

            _recent = _category.CreateEntry("RecentlyPlayed", "", "Recently played gamemodes",
                "Internal: a list of recently launched gamemode ids so the hub can list them first. Managed automatically.");
        }

        internal static bool Enabled => _enabled?.Value ?? true;

        /// <summary>The ids of recently launched gamemodes, most recent first.</summary>
        internal static List<string> RecentlyPlayed
        {
            get
            {
                string raw = _recent?.Value;
                if (string.IsNullOrEmpty(raw)) return new List<string>();
                return raw.Split(',').Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            }
        }

        /// <summary>Record that a gamemode was just launched (moves it to the front of the recent list).</summary>
        internal static void RecordLaunch(string id)
        {
            if (_recent == null || string.IsNullOrWhiteSpace(id)) return;
            var list = RecentlyPlayed;
            list.RemoveAll(s => s == id);
            list.Insert(0, id);
            if (list.Count > RecentMax) list = list.Take(RecentMax).ToList();
            _recent.Value = string.Join(",", list);
            try { MelonPreferences.Save(); } catch { /* best-effort */ }
        }
    }
}
