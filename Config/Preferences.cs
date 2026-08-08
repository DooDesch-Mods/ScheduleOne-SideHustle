using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        // Internal: ModSwitcher injects the pending-session tokens into a profile's CLONED cfg under this
        // section id (text-level merge), so the live cfg never carries them.
        internal const string CategoryId = "SideHustle_01_Main";
        private const int RecentMax = 10;

        // Delimiters for the per-gamemode alias map (see GetAlias/SetAlias). Both are stripped from aliases on set,
        // and neither occurs in a gamemode id, so a single-line "id=alias|id2=alias2" value is unambiguous.
        private const char AliasRecordSep = '|';
        private const char AliasPairSep = '=';

        private static MelonPreferences_Category _category;
        private static MelonPreferences_Entry<bool> _enabled;
        private static MelonPreferences_Entry<bool> _showUninstalled;
        private static MelonPreferences_Entry<bool> _askHostOnContinue;
        private static MelonPreferences_Entry<string> _recent;
        private static MelonPreferences_Entry<string> _modNameMap;
        private static MelonPreferences_Entry<string> _pendingContinue;
        private static MelonPreferences_Entry<string> _restoreOps;
        private static MelonPreferences_Entry<string> _activeAltBase;
        private static MelonPreferences_Entry<string> _activeGamemodeId;
        private static MelonPreferences_Entry<string> _pendingHostOptions;
        private static MelonPreferences_Entry<string> _pendingVanillaJoin;
        private static MelonPreferences_Entry<string> _pendingGamemodeJoin;
        private static MelonPreferences_Entry<string> _aliases;
        private static MelonPreferences_Entry<string> _lastSessionError;
        private static MelonPreferences_Entry<bool> _acceptStrangerMessages;
        private static MelonPreferences_Entry<bool> _menuPanel;
        private static MelonPreferences_Entry<bool> _joinChatPanel;

        internal static void Initialize()
        {
            if (_category != null) return;

            _category = MelonPreferences.CreateCategory(CategoryId, "Side Hustle (Gamemode Hub)");

            _enabled = _category.CreateEntry("Enabled", true, "Show the Side Hustle menu entry",
                "When ON, Side Hustle adds its entry to the main menu and lists installed gamemodes. " +
                "Turn OFF to hide it without uninstalling. Requires returning to the main menu to take effect.");

            _showUninstalled = _category.CreateEntry("ShowUninstalledGamemodes", true, "Show gamemodes you don't have",
                "When ON, the Side Hustle menu also lists gamemodes you have NOT installed that currently have live " +
                "public lobbies, so you can discover them (with a Download link). Turn OFF to show only installed gamemodes.");

            _askHostOnContinue = _category.CreateEntry("AskHostOnContinue", true, "Ask to host publicly on Continue",
                "When ON, clicking Continue or a save slot asks whether to open it as a public Side Hustle lobby. " +
                "Turn OFF to always just play the save.");

            _acceptStrangerMessages = _category.CreateEntry("AcceptStrangerMessages", true,
                "Let people who cannot join message you",
                "When ON, someone who found your published lobby but cannot get in - a mod they cannot download, a " +
                "full session - can send you a short message, and it appears in the Lobby app on your phone. " +
                "Turn OFF to refuse them silently. Only ever from people looking at YOUR published lobby.");

            _menuPanel = _category.CreateEntry("MenuStatePanel", true, "Show the state panel in the main menu",
                "When ON, a column down the right of the main menu says which mod set the game booted with, how " +
                "many published sessions are taking players right now, and whether anyone messaged you. Needs " +
                "Sideload 1.13.0 or newer; older versions simply show nothing.");

            _joinChatPanel = _category.CreateEntry("JoinChatPanel", true, "Show the ask-the-host column while joining",
                "When ON, the screens you pass through while joining a session carry a chat column on the right, so " +
                "you can ask the host about a mod you cannot download and read their answer without leaving. Only " +
                "appears when that host allows messages. Needs Sideload 1.13.0 or newer.");

            _recent = _category.CreateEntry("RecentlyPlayed", "", "Recently played gamemodes",
                "Internal: a list of recently launched gamemode ids so the hub can list them first. Managed automatically.");

            _modNameMap = _category.CreateEntry("ModNameMap", "", "Mod name map (internal)",
                "Internal: a mod name to DLL file map so the mod-policy feature can resolve disabled mods. Managed automatically.");
            _pendingContinue = _category.CreateEntry("PendingContinue", "", "Pending gamemode (internal)",
                "Internal: a gamemode id to auto-continue into after a mod-policy restart. Managed automatically.");
            _restoreOps = _category.CreateEntry("RestoreModOps", "", "Restore mod ops (legacy, internal)",
                "Internal: legacy field from an older mod-policy mechanism. No longer used; cleared automatically.");
            _activeAltBase = _category.CreateEntry("ActiveAltBase", "", "Active gamemode profile (internal)",
                "Internal: the temporary profile folder a mod-policy gamemode is running from, so it can be cleaned up. Managed automatically.");
            _activeGamemodeId = _category.CreateEntry("ActiveGamemodeId", "", "Active gamemode (internal)",
                "Internal: the id of the gamemode whose profile is currently running, so the main menu can offer it directly. Managed automatically.");
            _pendingHostOptions = _category.CreateEntry("PendingHostOptions", "", "Pending host options (internal)",
                "Internal: the host's chosen lobby options to apply after a mod-policy restart, so it hosts directly. Managed automatically.");
            _pendingVanillaJoin = _category.CreateEntry("PendingVanillaJoin", "", "Pending vanilla join (internal)",
                "Internal: the lobby to auto-rejoin after a mod-sync restart (only ever present in a profile's cloned config). Managed automatically.");
            _pendingGamemodeJoin = _category.CreateEntry("PendingGamemodeJoin", "", "Pending gamemode join (internal)",
                "Internal: the gamemode lobby to join after installing that gamemode's mods (only ever present in a profile's cloned config). Managed automatically.");

            _lastSessionError = _category.CreateEntry("LastSessionError", "", "Last session error (internal)",
                "Internal: why the last session ended badly, shown once in the menu and cleared when acknowledged. Persisted because half the ways a session dies end in a restart. Managed automatically.");

            _aliases = _category.CreateEntry("Aliases", "", "Display names (internal)",
                "Internal: your chosen display name for each gamemode, shown to other players instead of your Steam " +
                "name during that gamemode's session. Set it on the gamemode's Host/Join screen. Managed automatically.");
        }

        internal static bool Enabled => _enabled?.Value ?? true;

        /// <summary>Whether the menu also lists not-installed gamemodes that currently have live public lobbies.</summary>
        internal static bool ShowUninstalledGamemodes => _showUninstalled?.Value ?? true;

        /// <summary>Whether clicking Continue / a save slot prompts to host it as a public Side Hustle lobby.</summary>
        internal static bool AskHostOnContinue => _askHostOnContinue?.Value ?? true;

        /// <summary>Whether a stranger who cannot join this host's lobby may send them a short message. The one
        /// switch that turns the whole relay off - a host who does not want to be reached should not have to mute
        /// people one at a time.</summary>
        internal static bool AcceptStrangerMessages
        {
            get => _acceptStrangerMessages?.Value ?? true;
            set { if (_acceptStrangerMessages != null) { _acceptStrangerMessages.Value = value; Save(); } }
        }

        /// <summary>Whether the main menu carries the Side Hustle state column. Settable, because the panel turns
        /// itself off once when the installed Sideload is too old to render outside the phone - asking that question
        /// again on every menu load would be a log line per load and the same answer every time.</summary>
        internal static bool MenuPanel
        {
            get => _menuPanel?.Value ?? true;
            set { if (_menuPanel != null) { _menuPanel.Value = value; Save(); } }
        }

        /// <summary>Whether the join screens carry the ask-the-host chat column.</summary>
        internal static bool JoinChatPanel
        {
            get => _joinChatPanel?.Value ?? true;
            set { if (_joinChatPanel != null) { _joinChatPanel.Value = value; Save(); } }
        }

        /// <summary>Your chosen display name for a given gamemode ("" = use the real Steam persona name). Stored
        /// per-gamemode so you can appear under a different name in each. Applied to the in-game player name for the
        /// duration of that gamemode's session.</summary>
        internal static string GetAlias(string gamemodeId)
        {
            if (string.IsNullOrEmpty(gamemodeId)) return "";
            return DecodeAliases().TryGetValue(gamemodeId, out var v) ? v : "";
        }

        /// <summary>Set (or clear, when blank) the display name for one gamemode. Trimmed, capped at 24 chars, and
        /// stripped of the map delimiters and newlines; persisted immediately.</summary>
        internal static void SetAlias(string gamemodeId, string name)
        {
            if (_aliases == null || string.IsNullOrEmpty(gamemodeId)) return;
            var v = (name ?? "").Trim();
            if (v.Length > 24) v = v.Substring(0, 24);
            v = v.Replace(AliasRecordSep, ' ').Replace(AliasPairSep, ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
            var map = DecodeAliases();
            if (string.IsNullOrEmpty(v)) map.Remove(gamemodeId); else map[gamemodeId] = v;
            var sb = new StringBuilder();
            foreach (var kv in map)
            {
                if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value)) continue;
                if (sb.Length > 0) sb.Append(AliasRecordSep);
                sb.Append(kv.Key).Append(AliasPairSep).Append(kv.Value);
            }
            _aliases.Value = sb.ToString();
            Save();
        }

        // gamemodeId -> alias, encoded on one line as "id=alias|id2=alias2". Both delimiters are stripped from
        // aliases on set and neither occurs in a gamemode id, so the split back is unambiguous and stays single-line.
        private static Dictionary<string, string> DecodeAliases()
        {
            var map = new Dictionary<string, string>();
            var raw = _aliases?.Value;
            if (string.IsNullOrEmpty(raw)) return map;
            foreach (var rec in raw.Split(AliasRecordSep))
            {
                if (string.IsNullOrEmpty(rec)) continue;
                int i = rec.IndexOf(AliasPairSep);
                if (i <= 0) continue;
                map[rec.Substring(0, i)] = rec.Substring(i + 1);
            }
            return map;
        }

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

        /// <summary>Legacy field from the older rename-based mechanism; retained only so it can be cleared on upgrade.</summary>
        internal static string RestoreModOps
        {
            get => _restoreOps?.Value ?? "";
            set { if (_restoreOps != null) { _restoreOps.Value = value ?? ""; Save(); } }
        }

        /// <summary>The temporary profile folder a mod-policy gamemode is running from ("" = no policy session active).</summary>
        internal static string ActiveAltBase
        {
            get => _activeAltBase?.Value ?? "";
            set { if (_activeAltBase != null) { _activeAltBase.Value = value ?? ""; Save(); } }
        }

        /// <summary>The id of the gamemode whose profile is currently running ("" = no policy session active). Unlike
        /// <see cref="PendingContinue"/> this is durable for the whole session, so the main menu can offer the gamemode
        /// directly even after the auto-continue has already fired (or the player has returned from a session).</summary>
        internal static string ActiveGamemodeId
        {
            get => _activeGamemodeId?.Value ?? "";
            set { if (_activeGamemodeId != null) { _activeGamemodeId.Value = value ?? ""; Save(); } }
        }

        /// <summary>Encoded host options to apply after a mod-policy restart so the gamemode hosts directly ("" = none).</summary>
        internal static string PendingHostOptions
        {
            get => _pendingHostOptions?.Value ?? "";
            set { if (_pendingHostOptions != null) { _pendingHostOptions.Value = value ?? ""; Save(); } }
        }

        internal static string PendingVanillaJoin
        {
            get => _pendingVanillaJoin?.Value ?? "";
            set { if (_pendingVanillaJoin != null) { _pendingVanillaJoin.Value = value ?? ""; Save(); } }
        }

        /// <summary>The gamemode lobby to join once its mods are installed ("" = none): encoded lobby + gamemode id +
        /// manifest hash, written into the session profile's cloned config before the restart.</summary>
        internal static string PendingGamemodeJoin
        {
            get => _pendingGamemodeJoin?.Value ?? "";
            set { if (_pendingGamemodeJoin != null) { _pendingGamemodeJoin.Value = value ?? ""; Save(); } }
        }

        /// <summary>Why the last session ended badly ("" = nothing to report). Survives the restarts that several
        /// abort paths trigger, so the explanation still reaches the player on the other side of one.</summary>
        internal static string LastSessionError
        {
            get => _lastSessionError?.Value ?? "";
            set { if (_lastSessionError != null) { _lastSessionError.Value = value ?? ""; Save(); } }
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
