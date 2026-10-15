using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MelonLoader;
using SideHustle.Config;
using UnityEngine;

namespace SideHustle.Mods
{
    /// <summary>One loaded MelonLoader mod: its display name, version, DLL file name, and managed assembly.</summary>
    internal sealed class LoadedMod
    {
        public string Name;
        public string Version;      // exact MelonInfo version ("" when the mod declares none)
        public string DownloadLink; // MelonInfo download URL ("" when the mod declares none)
        public string File;         // e.g. "Litterally.dll"
        public Assembly Assembly;   // for dependency lookups
    }

    /// <summary>
    /// Reads the mod environment: the loaded MelonLoader mods (name + DLL file), the mod files present on disk
    /// (enabled or .disabled), and resolves a policy token (a mod name OR a DLL file name) to a DLL file. A
    /// persisted name -> file map lets a token resolve a mod even while it is disabled (and thus not loaded).
    /// </summary>
    internal static class ModInventory
    {
        private static readonly HashSet<string> _warned = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>These probes run on every menu load and per mod; a given problem is logged once.</summary>
        private static void WarnOnce(string key, string message)
        {
            if (_warned.Count >= 200 || !_warned.Add(key)) return;
            Core.Log?.Warning("[mods] " + message);
        }

        internal static string GameRoot()
        {
            try { return Directory.GetParent(Application.dataPath).FullName; }
            catch (Exception e)
            {
                WarnOnce("gameroot", "could not find the game folder: " + e.Message);
                return null;
            }
        }

        /// <summary>The Mods folder the running process actually loaded from (= the alt Mods during a policy session,
        /// the real Mods otherwise), so the policy resolver's "available" set always matches the loaded set.</summary>
        internal static string ModsPath()
        {
            try { var d = MelonLoader.Utils.MelonEnvironment.ModsDirectory; if (!string.IsNullOrEmpty(d)) return d; }
            catch (Exception e) { WarnOnce("modspath", "MelonLoader would not name its Mods folder; using <game>/Mods: " + e.Message); }
            var r = GameRoot(); return r == null ? null : Path.Combine(r, "Mods");
        }

        internal static IEnumerable<MelonMod> Melons()
        {
            try { return MelonMod.RegisteredMelons; }
            catch (Exception e)
            {
                WarnOnce("melons", "could not list the loaded mods (treated as none): " + e.Message);
                return new List<MelonMod>();
            }
        }

        internal static string NameOf(MelonMod m)
        {
            try { return m.Info?.Name ?? m.GetType().Name; }
            catch (Exception e)
            {
                WarnOnce("name:" + m.GetType().Name, $"could not read the name of {m.GetType().Name}: {e.Message}");
                return m.GetType().Name;
            }
        }

        internal static string VersionOf(MelonMod m)
        {
            try { return m.Info?.Version ?? ""; }
            catch (Exception e)
            {
                WarnOnce("ver:" + m.GetType().Name, $"could not read the version of {m.GetType().Name}: {e.Message}");
                return "";
            }
        }

        internal static string DownloadLinkOf(MelonMod m)
        {
            try { return m.Info?.DownloadLink ?? ""; }
            catch (Exception e)
            {
                WarnOnce("link:" + m.GetType().Name, $"could not read the download link of {m.GetType().Name}: {e.Message}");
                return "";
            }
        }

        internal static string FileOf(MelonMod m)
        {
            try { var l = m.MelonAssembly?.Location; if (!string.IsNullOrEmpty(l)) return Path.GetFileName(l); }
            catch (Exception e) { WarnOnce("file1:" + m.GetType().Name, $"could not read the file of {m.GetType().Name} from MelonLoader (trying the assembly): {e.Message}"); }
            try { var l = m.GetType().Assembly.Location; if (!string.IsNullOrEmpty(l)) return Path.GetFileName(l); }
            catch (Exception e) { WarnOnce("file2:" + m.GetType().Name, $"could not read the file of {m.GetType().Name} from its assembly (the mod is left out of the list): {e.Message}"); }
            return null;
        }

        internal static List<LoadedMod> Loaded()
        {
            var list = new List<LoadedMod>();
            foreach (var m in Melons())
            {
                try
                {
                    string f = FileOf(m);
                    if (f == null) continue;
                    list.Add(new LoadedMod
                    {
                        Name = NameOf(m),
                        Version = VersionOf(m),
                        DownloadLink = DownloadLinkOf(m),
                        File = f,
                        Assembly = m.GetType().Assembly
                    });
                }
                catch (Exception e) { WarnOnce("loaded:" + m.GetType().Name, $"{m.GetType().Name} could not be listed: {e.Message}"); }
            }
            return list;
        }

        /// <summary>All mod files in Mods/ as their enabled `.dll` name (whether currently `.dll` or `.dll.disabled`).</summary>
        internal static List<string> AvailableFiles()
        {
            var res = new List<string>();
            try
            {
                var dir = ModsPath(); if (dir == null) return res;
                foreach (var f in Directory.GetFiles(dir, "*.dll")) res.Add(Path.GetFileName(f));
                foreach (var f in Directory.GetFiles(dir, "*.dll.disabled"))
                {
                    string n = Path.GetFileName(f);
                    res.Add(n.Substring(0, n.Length - ".disabled".Length));
                }
            }
            catch (Exception e) { WarnOnce("available", "could not list the files in the Mods folder (the list may be incomplete): " + e.Message); }
            return res.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Lowercase SHA256 hex of a file's bytes, or null when unreadable. The sync manifest identifies
        /// a mod by this hash - it wins over the declared version (a recompiled DLL with an unchanged version
        /// must still count as different).</summary>
        internal static string Sha256OfFile(string path)
        {
            try
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var s = File.OpenRead(path);
                return Convert.ToHexString(sha.ComputeHash(s)).ToLowerInvariant();
            }
            catch (Exception e)
            {
                WarnOnce("sha:" + path, $"could not hash '{path}': {e.Message}");
                return null;
            }
        }

        internal static bool IsDisabledOnDisk(string dllFile)
        {
            try
            {
                var dir = ModsPath(); if (dir == null) return false;
                return File.Exists(Path.Combine(dir, dllFile + ".disabled")) && !File.Exists(Path.Combine(dir, dllFile));
            }
            catch (Exception e)
            {
                WarnOnce("disabled:" + dllFile, $"could not check whether {dllFile} is disabled (treated as enabled): {e.Message}");
                return false;
            }
        }

        // --- token resolution ---

        private static string Norm(string s) => s == null ? "" : new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        /// <summary>Resolve a policy token (mod name or DLL file name) to a DLL file name, or null if not found anywhere.</summary>
        internal static string Resolve(string token, List<LoadedMod> loaded, List<string> available, Dictionary<string, string> nameMap)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            string t = token.Trim();
            string tFile = t.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? t : t + ".dll";

            // 1. a currently loaded mod, by file or by (normalized) name
            foreach (var m in loaded)
            {
                if (m.File != null && string.Equals(m.File, tFile, StringComparison.OrdinalIgnoreCase)) return m.File;
                if (m.Name != null && Norm(m.Name) == Norm(t)) return m.File;
            }
            // 2. the persisted name -> file map (covers a mod that is currently disabled), but only if the mapped
            // file still exists on disk (the map could be stale after a mod was renamed/removed).
            if (nameMap != null && nameMap.TryGetValue(Norm(t), out var mapped) && !string.IsNullOrEmpty(mapped)
                && available.Any(a => string.Equals(a, mapped, StringComparison.OrdinalIgnoreCase)))
                return mapped;
            // 3. a file present on disk, by file name
            foreach (var a in available)
                if (string.Equals(a, tFile, StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }

        /// <summary>True if the loaded mod's file or name loosely matches any of the given substrings (for essentials).</summary>
        internal static bool MatchesAny(LoadedMod m, params string[] needles)
        {
            string nf = Norm(m.File);
            string nn = Norm(m.Name);
            foreach (var needle in needles)
            {
                string x = Norm(needle);
                if (x.Length == 0) continue;
                if (nf.Contains(x) || nn.Contains(x)) return true;
            }
            return false;
        }

        // --- persisted name -> file map ---

        /// <summary>Refresh the persisted map from the currently loaded mods (so disabled mods stay resolvable by name).</summary>
        internal static void RefreshNameMap()
        {
            try
            {
                var map = LoadNameMap();
                foreach (var m in Loaded())
                    if (!string.IsNullOrEmpty(m.Name) && !string.IsNullOrEmpty(m.File)) map[Norm(m.Name)] = m.File;
                // Drop entries whose file no longer exists on disk, so the map cannot drift and resolve to a dead file.
                var available = new HashSet<string>(AvailableFiles(), StringComparer.OrdinalIgnoreCase);
                foreach (var key in map.Keys.ToList())
                    if (!available.Contains(map[key])) map.Remove(key);
                // Records use '|' and fields use ':' - both illegal in Windows file names, so a DLL name cannot collide.
                Preferences.ModNameMap = string.Join("|", map.Select(kv => kv.Key + ":" + kv.Value));
            }
            catch (Exception e) { WarnOnce("namemap-refresh", "could not refresh the saved mod-name map (disabled mods may not resolve by name): " + e.Message); }
        }

        internal static Dictionary<string, string> LoadNameMap()
        {
            var map = new Dictionary<string, string>();
            try
            {
                string raw = Preferences.ModNameMap;
                if (string.IsNullOrEmpty(raw)) return map;
                foreach (var part in raw.Split('|'))
                {
                    int i = part.IndexOf(':');
                    if (i > 0) map[part.Substring(0, i)] = part.Substring(i + 1);
                }
            }
            catch (Exception e) { WarnOnce("namemap-load", "the saved mod-name map is unreadable and is ignored: " + e.Message); }
            return map;
        }
    }
}
