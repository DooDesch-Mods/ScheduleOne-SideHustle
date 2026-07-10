using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MelonLoader;
using SideHustle.Mods;

namespace SideHustle.Sync
{
    /// <summary>One MelonPreferences category the host can choose to sync to clients.</summary>
    internal sealed class PrefsCategory
    {
        public string Id;           // section id, e.g. "PropHunt_01_Main"
        public string DisplayName;
        public bool SyncByDefault = true;
        public bool SecretRisk;     // contains a key that looks like a token/secret (world-readable lobby data!)
    }

    /// <summary>
    /// Enumerates the host's syncable MelonPreferences categories for the host form. Side Hustle's own internal
    /// category is never offered (it carries the session tokens); a category whose keys look like secrets
    /// (token/key/secret/password/webhook/api) defaults OFF with a warning, because lobby data is world-readable.
    /// </summary>
    internal static class PrefsCatalog
    {
        private static readonly string[] SecretNeedles = { "token", "secret", "password", "webhook", "apikey", "api_key" };

        internal static List<PrefsCategory> Enumerate()
        {
            var cats = new List<PrefsCategory>();
            try
            {
                var categories = MelonPreferences.Categories;
                if (categories == null) return cats;
                foreach (var c in categories)
                {
                    if (c == null) continue;
                    string id = c.Identifier;
                    if (string.IsNullOrEmpty(id)) continue;
                    if (id.Equals(Config.Preferences.CategoryId, StringComparison.OrdinalIgnoreCase)) continue;   // our tokens

                    bool secret = false;
                    try
                    {
                        foreach (var e in c.Entries)
                        {
                            string k = (e?.Identifier ?? "").ToLowerInvariant();
                            if (SecretNeedles.Any(n => k.Contains(n))) { secret = true; break; }
                        }
                    }
                    catch { /* ignore */ }

                    cats.Add(new PrefsCategory
                    {
                        Id = id,
                        DisplayName = string.IsNullOrEmpty(c.DisplayName) ? id : c.DisplayName,
                        SyncByDefault = !secret,
                        SecretRisk = secret,
                    });
                }
            }
            catch (Exception e) { Core.Log?.Warning("[sync] prefs enumeration failed: " + e.Message); }
            return cats.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Save the live prefs, then extract the chosen categories' TOML section text from the on-disk
        /// cfg (exact value syntax, zero serializer risk). Returns "" when nothing is selected.</summary>
        internal static string BuildOverlay(IEnumerable<string> sectionIds)
        {
            var ids = sectionIds?.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            if (ids == null || ids.Count == 0) return "";
            try
            {
                MelonPreferences.Save();
                string root = ModInventory.GameRoot();
                string cfg = root != null ? Path.Combine(root, "UserData", "MelonPreferences.cfg") : null;
                if (cfg == null || !File.Exists(cfg)) return "";
                return PrefsSync.ExtractSections(File.ReadAllText(cfg), ids);
            }
            catch (Exception e) { Core.Log?.Warning("[sync] overlay build failed: " + e.Message); return ""; }
        }
    }
}
