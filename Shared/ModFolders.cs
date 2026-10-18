using System;
using System.Collections.Generic;
using System.IO;

namespace SideHustle.Shared
{
    /// <summary>
    /// Where mod files sit in a Mods folder, by MelonLoader 0.7's own rule: the folder itself plus every immediate
    /// subfolder that carries a manifest.json. r2modman and Gale install each Thunderstore package that way, so a
    /// lookup of Mods/&lt;file&gt; alone misses every mod a manager installed. Pure BCL, shared with the boot plugin.
    /// </summary>
    internal static class ModFolders
    {
        /// <summary>The package subfolders MelonLoader scans, skipping its default exclusions ("~" and "." prefixes,
        /// Broken, Retired, Disabled).</summary>
        internal static List<string> PackageFolders(string modsDir)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir)) return res;
            try
            {
                foreach (var sub in Directory.GetDirectories(modsDir))
                {
                    string name = Path.GetFileName(sub);
                    if (name.StartsWith("~") || name.StartsWith(".")) continue;
                    if (name == "Broken" || name == "Retired" || name == "Disabled") continue;
                    if (File.Exists(Path.Combine(sub, "manifest.json"))) res.Add(sub);
                }
            }
            catch (Exception e)
            {
                SharedLog.WarnOnce("pkgfolders:" + modsDir, $"could not list the package folders in '{modsDir}' (the list may be incomplete): {e.Message}");
            }
            return res;
        }

        /// <summary>Where a mod file sits: Mods/&lt;file&gt;, then Mods/&lt;file&gt;.disabled when
        /// <paramref name="allowDisabled"/>, then a package subfolder. Null when it is in none of them.</summary>
        internal static string Find(string modsDir, string file, bool allowDisabled = true)
        {
            if (string.IsNullOrEmpty(modsDir) || string.IsNullOrEmpty(file)) return null;
            try
            {
                string flat = Path.Combine(modsDir, file);
                if (File.Exists(flat)) return flat;
                if (allowDisabled && File.Exists(flat + ".disabled")) return flat + ".disabled";
                foreach (var sub in PackageFolders(modsDir))
                {
                    string p = Path.Combine(sub, file);
                    if (File.Exists(p)) return p;
                }
            }
            catch (Exception e)
            {
                SharedLog.WarnOnce("find:" + file, $"could not look for {file} in '{modsDir}': {e.Message}");
            }
            return null;
        }
    }
}
