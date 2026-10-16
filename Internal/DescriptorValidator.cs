using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SideHustle.Mods;
using SideHustle.Multiplayer;

namespace SideHustle.Internal
{
    /// <summary>
    /// Checks a <see cref="GamemodeDescriptor"/> when it is registered and logs every problem with the gamemode id
    /// and the field name. It never rejects: every descriptor that registered before keeps registering. Error
    /// means that part of the gamemode cannot work as declared; Warning means it works but probably not as meant.
    ///
    /// The mod policy check needs the full installed mod set, which only exists once every mod's OnInitializeMelon
    /// has run, so it waits for the first frame (<see cref="OnModsSettled"/>) when a gamemode registers earlier.
    /// </summary>
    internal static class DescriptorValidator
    {
        private static readonly Regex IdPattern = new Regex("^[a-z0-9._-]{3,48}$", RegexOptions.CultureInvariant);

        /// <summary>The config blob rides on Steam lobby metadata, which has an 8 KB budget for ALL keys together.</summary>
        internal const int ConfigBlobBudget = 1500;

        private static readonly List<GamemodeDescriptor> _pendingPolicy = new List<GamemodeDescriptor>();
        private static bool _modsSettled;

        /// <summary>Warn when an id is taken over by a different mod. The same mod registering again (hot reload,
        /// double init) is the supported replace case and stays silent.</summary>
        internal static void CheckOwner(GamemodeDescriptor incoming, GamemodeDescriptor existing)
        {
            if (existing == null || ReferenceEquals(existing, incoming)) return;
            var a = existing.OwnerAssembly;
            var b = incoming.OwnerAssembly;
            if (a == null || b == null || a == b) return;
            Warn(incoming.Id, "Id", $"already registered by {a.GetName().Name}; {b.GetName().Name} replaces it. Two mods are using the same id.");
        }

        internal static void Validate(GamemodeDescriptor d)
        {
            try
            {
                string id = d.Id;
                if (!IdPattern.IsMatch(id))
                    Warn(id, "Id", "should be 3-48 characters of a-z, 0-9, '.', '_' or '-' (e.g. \"you.yourmode\"). It still works.");

                CheckCallbacks(d);
                var keys = CheckSettings(d);
                CheckPresets(d, keys);
                CheckBlobSize(d);

                if (d.Policy != null)
                {
                    if (_modsSettled) CheckPolicy(d);
                    else _pendingPolicy.Add(d);
                }
            }
            catch (Exception e)
            {
                Core.Log?.Error($"[api] checking gamemode '{d?.Id}' failed: {e}. It is registered unchecked.");
            }
        }

        /// <summary>Run the mod policy checks that waited for the full mod set. Called once, on the first frame.</summary>
        internal static void OnModsSettled()
        {
            _modsSettled = true;
            var pending = _pendingPolicy.ToList();
            _pendingPolicy.Clear();
            foreach (var d in pending)
            {
                if (!Registry.Contains(d))
                {
                    Core.Log?.Msg($"[api] mod policy check for '{d.Id}' skipped: it was replaced or unregistered.");
                    continue;
                }
                try { CheckPolicy(d); }
                catch (Exception e) { Core.Log?.Error($"[api] mod policy check for '{d.Id}' failed: {e}"); }
            }
        }

        // --- callbacks ---------------------------------------------------------------------------------------

        private static void CheckCallbacks(GamemodeDescriptor d)
        {
            if (d.AllowsSingleplayer && d.OnLaunchSingleplayer == null)
                Error(d.Id, "OnLaunchSingleplayer", $"is null but Support is {d.Support}; singleplayer cannot launch.");
            if (d.AllowsMultiplayer && d.OnHostMultiplayer == null)
                Error(d.Id, "OnHostMultiplayer", $"is null but Support is {d.Support}; hosting cannot start.");
            if (d.AllowsMultiplayer && d.OnJoinMultiplayer == null)
                Error(d.Id, "OnJoinMultiplayer", $"is null but Support is {d.Support}; nobody can join.");
        }

        // --- host settings -----------------------------------------------------------------------------------

        private static HashSet<string> CheckSettings(GamemodeDescriptor d)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (d.HostSettings == null) return keys;
            for (int i = 0; i < d.HostSettings.Length; i++)
            {
                var s = d.HostSettings[i];
                string where = $"HostSettings[{i}]";
                if (s == null) { Warn(d.Id, where, "is null and is skipped."); continue; }
                if (string.IsNullOrWhiteSpace(s.Key)) { Error(d.Id, where + ".Key", "is empty; the value cannot be stored or read back."); continue; }
                where = $"HostSettings[\"{s.Key}\"]";
                if (!keys.Add(s.Key)) Warn(d.Id, where + ".Key", "is used twice; both controls write the same value.");
                if (string.IsNullOrWhiteSpace(s.Label)) Warn(d.Id, where + ".Label", "is empty; the host sees an unlabelled control.");

                switch (s.Type)
                {
                    case SettingType.Slider: CheckSlider(d.Id, where, s); break;
                    case SettingType.Toggle:
                        if (!string.IsNullOrEmpty(s.Default) && !IsBoolText(s.Default))
                            Warn(d.Id, where + ".Default", $"\"{s.Default}\" is not \"1\", \"0\", \"true\" or \"false\"; the toggle starts off.");
                        break;
                    case SettingType.Segmented:
                    case SettingType.Dropdown: CheckChoice(d.Id, where, s); break;
                }
            }
            return keys;
        }

        private static void CheckSlider(string id, string where, SettingDescriptor s)
        {
            if (s.Min > s.Max) { Error(id, where + ".Min", $"{F(s.Min)} is above Max {F(s.Max)}; the slider cannot move."); return; }
            if (s.Min == 0f && s.Max == 1f)
                Warn(id, where, "uses the default 0..1 range; set Min and Max unless a fraction is meant.");
            // Step 0 is documented as "continuous", so only a negative step is wrong.
            if (s.Step < 0f) Warn(id, where + ".Step", $"{F(s.Step)} is negative; use a positive step, or 0 for continuous.");
            if (!string.IsNullOrEmpty(s.Default))
            {
                if (!float.TryParse(s.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                    Warn(id, where + ".Default", $"\"{s.Default}\" is not a number (invariant culture); the slider starts at Min.");
                else if (v < s.Min || v > s.Max)
                    Warn(id, where + ".Default", $"{F(v)} is outside {F(s.Min)}..{F(s.Max)}; the slider clamps it.");
            }
        }

        private static void CheckChoice(string id, string where, SettingDescriptor s)
        {
            if (s.Options == null)
            {
                Warn(id, where + ".Options", $"is null; the {s.Type} falls back to \"Off\"/\"On\".");
            }
            else if (s.Options.Length == 0)
            {
                Error(id, where + ".Options", "is empty; the control has nothing to show.");
                return;
            }
            if (s.Options != null && s.Values != null && s.Values.Length != s.Options.Length)
            {
                if (s.Values.Length > s.Options.Length)
                    Error(id, where + ".Values", $"has {s.Values.Length} entries for {s.Options.Length} options; a default past the last option breaks the control.");
                else
                    Warn(id, where + ".Values", $"has {s.Values.Length} entries for {s.Options.Length} options; the rest store their label.");
            }
            string[] stored = s.Values ?? s.Options ?? new[] { "Off", "On" };
            if (!string.IsNullOrEmpty(s.Default) && Array.IndexOf(stored, s.Default) < 0)
                Warn(id, where + ".Default", $"\"{s.Default}\" is not one of the stored values; the first option is selected.");
        }

        // --- presets -----------------------------------------------------------------------------------------

        private static void CheckPresets(GamemodeDescriptor d, HashSet<string> keys)
        {
            if (d.Presets == null) return;
            for (int i = 0; i < d.Presets.Length; i++)
            {
                var p = d.Presets[i];
                if (p == null) { Warn(d.Id, $"Presets[{i}]", "is null and is skipped."); continue; }
                string where = string.IsNullOrWhiteSpace(p.Name) ? $"Presets[{i}]" : $"Presets[\"{p.Name}\"]";
                if (string.IsNullOrWhiteSpace(p.Name)) Warn(d.Id, where + ".Name", "is empty; the picker shows a blank entry.");
                if (p.MinPlayers > 0 && p.MaxPlayers > 0 && p.MinPlayers > p.MaxPlayers)
                    Warn(d.Id, where + ".MinPlayers", $"{p.MinPlayers} is above MaxPlayers {p.MaxPlayers}; it never auto-selects.");
                if (p.Values == null) continue;
                var unknown = p.Values.Keys.Where(k => !keys.Contains(k)).ToList();
                if (unknown.Count > 0)
                    Warn(d.Id, where + ".Values", $"key(s) {string.Join(", ", unknown.Select(k => "\"" + k + "\""))} match no host setting and are ignored " +
                                                  "(fine if they are meant for a setting a later version adds).");
            }
        }

        // --- config blob size --------------------------------------------------------------------------------

        /// <summary>Estimate the largest blob the host form can produce: every setting at its longest value. Text
        /// settings count at their default, because free text has no upper bound to measure.</summary>
        private static void CheckBlobSize(GamemodeDescriptor d)
        {
            if (d.HostSettings == null || d.HostSettings.Length == 0) return;
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (var s in d.HostSettings)
            {
                if (s == null || string.IsNullOrWhiteSpace(s.Key)) continue;
                pairs.Add(new KeyValuePair<string, string>(s.Key, LongestValue(s)));
            }
            int bytes = Encoding.UTF8.GetByteCount(ConfigCodec.Encode(pairs));
            if (bytes > ConfigBlobBudget)
                Warn(d.Id, "HostSettings", $"can produce a config blob of about {bytes} bytes (limit {ConfigBlobBudget}). It rides on the " +
                                           "8 KB Steam lobby metadata budget; shorten keys or stored values.");
        }

        private static string LongestValue(SettingDescriptor s)
        {
            var ci = CultureInfo.InvariantCulture;
            switch (s.Type)
            {
                case SettingType.Slider:
                {
                    string fmt(float v) => s.WholeNumbers ? Math.Round(v).ToString(ci) : v.ToString("0.##", ci);
                    // Two decimals on top of the larger end covers the widest value a continuous slider can store.
                    string a = fmt(s.Min), b = fmt(s.Max);
                    string w = a.Length >= b.Length ? a : b;
                    return s.WholeNumbers ? w : w + ".00";
                }
                case SettingType.Toggle: return "1";
                case SettingType.Segmented:
                case SettingType.Dropdown:
                {
                    string[] stored = s.Values ?? s.Options ?? new[] { "Off", "On" };
                    return stored.Where(v => v != null).OrderByDescending(v => v.Length).FirstOrDefault() ?? "";
                }
                default: return s.Default ?? "";
            }
        }

        // --- mod policy --------------------------------------------------------------------------------------

        private static void CheckPolicy(GamemodeDescriptor d)
        {
            var policy = d.Policy;
            if (policy == null) return;
            // Inside an isolated profile only that profile's mods are on disk; a token of another gamemode missing
            // there says nothing about the player's install.
            if (AltBase.IsAltSession())
            {
                Core.Log?.Msg($"[api] mod policy check for '{d.Id}' skipped: this game runs an isolated mod profile.");
                return;
            }

            var loaded = ModInventory.Loaded();
            var available = ModInventory.AvailableFiles();
            var nameMap = ModInventory.LoadNameMap();

            CheckTokens(d.Id, "Policy.RequiredMods", policy.RequiredMods, loaded, available, nameMap, required: true);
            CheckTokens(d.Id, "Policy.AllowedMods", policy.AllowedMods, loaded, available, nameMap, required: false);
        }

        private static void CheckTokens(string id, string field, string[] tokens, List<LoadedMod> loaded, List<string> available,
                                        Dictionary<string, string> nameMap, bool required)
        {
            if (tokens == null) return;
            foreach (var tok in tokens)
            {
                if (string.IsNullOrWhiteSpace(tok)) { Warn(id, field, "contains an empty entry; it is ignored."); continue; }
                if (ModInventory.Resolve(tok, loaded, available, nameMap) != null) continue;
                if (required)
                    Warn(id, field, $"\"{tok}\" matches no loaded mod name and no DLL in Mods/. Until it is installed a singleplayer launch " +
                                    "is blocked and hosting offers only the current mods. Match the MelonLoader name or the file name.");
                else
                    // An allowed mod is optional by definition - most players will not have every one installed.
                    Core.Log?.Msg($"[api] gamemode '{id}' {field}: \"{tok}\" is not installed here; nothing to keep.");
            }
        }

        // --- helpers -----------------------------------------------------------------------------------------

        private static bool IsBoolText(string v) =>
            v == "1" || v == "0" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);

        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static void Warn(string id, string field, string message) =>
            Core.Log?.Warning($"[api] gamemode '{id}' {field}: {message}");

        private static void Error(string id, string field, string message) =>
            Core.Log?.Error($"[api] gamemode '{id}' {field}: {message}");
    }
}
