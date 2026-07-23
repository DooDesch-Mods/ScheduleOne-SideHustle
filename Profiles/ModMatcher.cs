using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SideHustle.Mods;

namespace SideHustle.Profiles
{
    /// <summary>A persisted, confirmed DLL -> package mapping ("install" = we installed it, "user" = confirmed in UI).</summary>
    internal sealed class ModMapEntry
    {
        public string FullName { get; set; } = "";
        public string ConfirmedBy { get; set; } = "";
    }

    /// <summary>
    /// Maps an installed DLL to its Thunderstore package. Precedence: the persisted confirmed map
    /// (UserData\SideHustle\modmap.json) wins; otherwise a HEURISTIC suggestion against the index (normalized
    /// name equality, then containment with a version tie-breaker) that the UI offers for one-tap confirmation.
    /// Updates only ever run over confirmed mappings - a wrong fuzzy match must never silently "update" an
    /// unrelated mod.
    /// </summary>
    internal static class ModMatcher
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { WriteIndented = true };
        private static Dictionary<string, ModMapEntry> _map;   // key: dll file name (matched case-insensitively)
        // Confirm() runs on the install worker while the profile UI reads ConfirmedFullName on the main thread;
        // a plain Dictionary would race (torn read / resize). All _map access goes through this lock.
        private static readonly object _lock = new object();

        private static string MapPath()
        {
            var root = ModInventory.GameRoot();
            return root == null ? null : Path.Combine(root, "UserData", "SideHustle", "modmap.json");
        }

        private static Dictionary<string, ModMapEntry> Map()
        {
            if (_map != null) return _map;
            try
            {
                string p = MapPath();
                if (p != null && File.Exists(p))
                {
                    // JsonSerializer builds a case-SENSITIVE dictionary; rebuild it with the same OrdinalIgnoreCase
                    // comparer the fallback uses, so a confirmed mapping is found regardless of DLL-name casing (a
                    // package's zip entry name and the on-disk file name can differ in case). Iterate rather than the
                    // copy-constructor, which would throw on a case-variant duplicate key.
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, ModMapEntry>>(File.ReadAllText(p), JsonOpts);
                    if (loaded != null)
                    {
                        _map = new Dictionary<string, ModMapEntry>(StringComparer.OrdinalIgnoreCase);
                        foreach (var kv in loaded) _map[kv.Key] = kv.Value;
                    }
                }
            }
            catch { _map = null; }
            return _map ??= new Dictionary<string, ModMapEntry>(StringComparer.OrdinalIgnoreCase);
        }

        private static void SaveMap()
        {
            try
            {
                string p = MapPath();
                if (p == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, JsonSerializer.Serialize(Map(), JsonOpts));
            }
            catch { /* best-effort */ }
        }

        /// <summary>The CONFIRMED package for a DLL, or null (suggestions are never returned here).</summary>
        internal static string ConfirmedFullName(string dllFile)
        {
            if (string.IsNullOrEmpty(dllFile)) return null;
            lock (_lock) return Map().TryGetValue(dllFile, out var e) ? e.FullName : null;
        }

        internal static void Confirm(string dllFile, string fullName, string confirmedBy)
        {
            if (string.IsNullOrEmpty(dllFile) || string.IsNullOrEmpty(fullName)) return;
            lock (_lock)
            {
                Map()[dllFile] = new ModMapEntry { FullName = fullName, ConfirmedBy = confirmedBy };
                SaveMap();
            }
        }

        internal static void Forget(string dllFile)
        {
            lock (_lock)
            {
                if (Map().Remove(dllFile ?? "")) SaveMap();
            }
        }

        /// <summary>
        /// A heuristic suggestion for an unmapped mod, or null when nothing is confident enough. Exact
        /// normalized-name equality (dll name, then MelonInfo name) is decisive; containment needs length >= 5
        /// and is only offered when it is the SINGLE candidate; a version match against any package version is
        /// the tie-breaker between multiple containment hits.
        /// </summary>
        internal static TsPackage Suggest(LoadedMod mod, TsIndex index)
        {
            if (mod == null || index == null) return null;
            string dllNorm = Norm(Path.GetFileNameWithoutExtension(mod.File ?? ""));
            string nameNorm = Norm(mod.Name);

            foreach (var p in index.Packages)
            {
                string pn = Norm(p.Name);
                string fn = Norm(p.FullName);   // Owner-Name, for a DLL named after the full package
                if (pn.Length > 0 && (pn == dllNorm || pn == nameNorm || fn == dllNorm || fn == nameNorm)) return p;
            }

            var candidates = new List<TsPackage>();
            foreach (var p in index.Packages)
            {
                string pn = Norm(p.Name);
                if (pn.Length < 5 || dllNorm.Length < 5) continue;
                if (pn.Contains(dllNorm) || dllNorm.Contains(pn) ||
                    (nameNorm.Length >= 5 && (pn.Contains(nameNorm) || nameNorm.Contains(pn))))
                    candidates.Add(p);
            }
            if (candidates.Count == 1) return candidates[0];
            if (candidates.Count > 1 && !string.IsNullOrEmpty(mod.Version))
            {
                var byVersion = candidates.Where(c => c.Get(mod.Version) != null).ToList();
                if (byVersion.Count == 1) return byVersion[0];
            }
            return null;
        }

        private static string Norm(string s) =>
            s == null ? "" : new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
