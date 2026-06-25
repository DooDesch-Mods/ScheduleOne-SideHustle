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
        private static MelonPreferences_Entry<string> _modNameMap;
        private static MelonPreferences_Entry<string> _pendingContinue;
        private static MelonPreferences_Entry<string> _restoreOps;

        internal static void Initialize()
        {
            if (_category != null) return;

            _category = MelonPreferences.CreateCategory(CategoryId, "Side Hustle (Gamemode Hub)");

            _enabled = _category.CreateEntry("Enabled", true, "Show the Side Hustle menu entry",
                "When ON, Side Hustle adds its entry to the main menu and lists installed gamemodes. " +
                "Turn OFF to hide it without uninstalling. Requires returning to the main menu to take effect.");

            _recent = _category.CreateEntry("RecentlyPlayed", "", "Recently played gamemodes",
                "Internal: a list of recently launched gamemode ids so the hub can list them first. Managed automatically.");

            _modNameMap = _category.CreateEntry("ModNameMap", "", "Mod name map (internal)",
                "Internal: a mod name to DLL file map so the mod-policy feature can resolve disabled mods. Managed automatically.");
            _pendingContinue = _category.CreateEntry("PendingContinue", "", "Pending gamemode (internal)",
                "Internal: a gamemode id to auto-continue into after a mod-policy restart. Managed automatically.");
            _restoreOps = _category.CreateEntry("RestoreModOps", "", "Restore mod ops (internal)",
                "Internal: how to put your mods back when a mod-policy gamemode ends. Managed automatically.");
        }

        internal static bool Enabled => _enabled?.Value ?? true;

        private static void Save() { try { MelonPreferences.Save(); } catch { /* best-effort */ } }

        /// <summary>Mod name -> DLL file map (delimited), used by the mod policy to resolve disabled mods.</summary>
        internal static string ModNameMap
        {
            get => _modNameMap?.Value ?? "";
            set { if (_modNameMap != null) { _modNameMap.Value = value ?? ""; Save(); } }
        }

        /// <summary>A gamemode id to auto-continue into after a mod-policy restart ("" = none).</summary>
        internal static string PendingContinue
        {
            get => _pendingContinue?.Value ?? "";
            set { if (_pendingContinue != null) { _pendingContinue.Value = value ?? ""; Save(); } }
        }

        /// <summary>How to restore the player's mods when a mod-policy gamemode ends ("" = nothing to restore).</summary>
        internal static string RestoreModOps
        {
            get => _restoreOps?.Value ?? "";
            set { if (_restoreOps != null) { _restoreOps.Value = value ?? ""; Save(); } }
        }

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
