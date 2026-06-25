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
    /// <summary>One loaded MelonLoader mod: its display name, DLL file name, and managed assembly.</summary>
    internal sealed class LoadedMod
    {
        public string Name;
        public string File;        // e.g. "Litterally.dll"
        public Assembly Assembly;  // for dependency lookups
    }

    /// <summary>
    /// Reads the mod environment: the loaded MelonLoader mods (name + DLL file), the mod files present on disk
    /// (enabled or .disabled), and resolves a policy token (a mod name OR a DLL file name) to a DLL file. A
    /// persisted name -> file map lets a token resolve a mod even while it is disabled (and thus not loaded).
    /// </summary>
    internal static class ModInventory
    {
        internal static string GameRoot() { try { return Directory.GetParent(Application.dataPath).FullName; } catch { return null; } }
        internal static string ModsPath() { var r = GameRoot(); return r == null ? null : Path.Combine(r, "Mods"); }

        internal static IEnumerable<MelonMod> Melons()
        {
            try { return MelonMod.RegisteredMelons; } catch { return new List<MelonMod>(); }
        }

        internal static string NameOf(MelonMod m) { try { return m.Info?.Name ?? m.GetType().Name; } catch { return m.GetType().Name; } }

        internal static string FileOf(MelonMod m)
        {
            try { var l = m.MelonAssembly?.Location; if (!string.IsNullOrEmpty(l)) return Path.GetFileName(l); } catch { /* ignore */ }
            try { var l = m.GetType().Assembly.Location; if (!string.IsNullOrEmpty(l)) return Path.GetFileName(l); } catch { /* ignore */ }
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
                    list.Add(new LoadedMod { Name = NameOf(m), File = f, Assembly = m.GetType().Assembly });
                }
                catch { /* ignore */ }
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
            catch { /* ignore */ }
            return res.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static bool IsDisabledOnDisk(string dllFile)
        {
            try
            {
                var dir = ModsPath(); if (dir == null) return false;
                return File.Exists(Path.Combine(dir, dllFile + ".disabled")) && !File.Exists(Path.Combine(dir, dllFile));
            }
            catch { return false; }
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
            catch { /* best-effort */ }
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
            catch { /* ignore */ }
            return map;
        }
    }
}
