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
        public string Description;  // optional friendlier hint (overrides the generic one in the host form)
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
                    catch (Exception e) { Core.Log?.Warning($"[sync] could not scan the settings of '{id}' for secrets (its settings are treated as not secret): {e.Message}"); }

                    // FullHouse is Side Hustle's embedded bigger-lobby engine, not a third-party mod - name it clearly
                    // so hosts don't wonder what it is, and always sync it so everyone agrees on the max lobby size.
                    bool isFullHouse = id.Equals("FullHouse", StringComparison.OrdinalIgnoreCase);
                    cats.Add(new PrefsCategory
                    {
                        Id = id,
                        DisplayName = isFullHouse ? "Bigger lobbies" : (string.IsNullOrEmpty(c.DisplayName) ? id : c.DisplayName),
                        Description = isFullHouse ? "Part of Side Hustle - keeps everyone's max lobby size in sync." : null,
                        SyncByDefault = isFullHouse || !secret,
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
                // The cfg Save just wrote: the running base's, which is a mod manager profile or a Side Hustle
                // profile as often as the game folder.
                string cfg = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "MelonPreferences.cfg");
                if (!File.Exists(cfg))
                {
                    Core.Log?.Warning($"[sync] no settings file at '{cfg}'; the lobby goes without your mod settings.");
                    return "";
                }
                return PrefsSync.ExtractSections(File.ReadAllText(cfg), ids);
            }
            catch (Exception e) { Core.Log?.Warning("[sync] overlay build failed: " + e.Message); return ""; }
        }
    }
}
