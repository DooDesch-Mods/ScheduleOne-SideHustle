using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideHustle.Profiles
{
    /// <summary>What a cached package contributes, classified at extract time (persisted as .sh_manifest.json).</summary>
    internal sealed class CacheManifest
    {
        [JsonPropertyName("mods")] public List<string> Mods { get; set; } = new List<string>();
        [JsonPropertyName("plugins")] public List<string> Plugins { get; set; } = new List<string>();
        [JsonPropertyName("userlibs")] public List<string> UserLibs { get; set; } = new List<string>();
        [JsonPropertyName("ignored")] public List<string> Ignored { get; set; } = new List<string>();
        /// <summary>file name -> lowercase sha256 for every classified DLL (diffing against a lobby manifest).</summary>
        [JsonPropertyName("hashes")] public Dictionary<string, string> Hashes { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// The r2modman-style package cache: every Thunderstore package version is downloaded and extracted ONCE
    /// under &lt;gameRoot&gt;\SideHustle_Profiles\cache\&lt;Owner-Name&gt;\&lt;version&gt;\ and hardlinked into any number of
    /// profiles from there (same volume as Mods by construction). Thunderstore versions are immutable, so a
    /// cached version never invalidates. Manually-installed files (the Nexus/link flow) are promoted into the
    /// hash-keyed bucket cache\_manual\&lt;sha256&gt;\. Pure BCL so the console harness can test extraction and
    /// classification with fixture zips.
    /// </summary>
    internal static class PackageCache
    {
        internal const string ManifestName = ".sh_manifest.json";

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { WriteIndented = true };

        internal static string RootFor(string gameRoot) => Path.Combine(gameRoot, "SideHustle_Profiles", "cache");
        internal static string PathFor(string cacheRoot, string fullName, string version) =>
            Path.Combine(cacheRoot, Sanitize(fullName), Sanitize(version));
        internal static string ManualRoot(string cacheRoot) => Path.Combine(cacheRoot, "_manual");
        internal static string ManualPathFor(string cacheRoot, string sha256) => Path.Combine(ManualRoot(cacheRoot), sha256);

        internal static bool IsCached(string cacheRoot, string fullName, string version) =>
            File.Exists(Path.Combine(PathFor(cacheRoot, fullName, version), ManifestName));

        internal static CacheManifest ReadManifest(string packageDir)
        {
            try { return JsonSerializer.Deserialize<CacheManifest>(File.ReadAllText(Path.Combine(packageDir, ManifestName))); }
            catch { return null; }
        }

        /// <summary>
        /// Extract a downloaded package zip into <paramref name="destDir"/> and classify its files. Every entry
        /// is path-traversal-guarded; entries escaping the destination abort the whole extraction (a hostile
        /// package must not plant files elsewhere). Returns the manifest (also persisted) or null on failure.
        /// </summary>
        internal static CacheManifest ExtractAndClassify(string zipPath, string destDir)
        {
            try
            {
                if (Directory.Exists(destDir)) Directory.Delete(destDir, true);
                Directory.CreateDirectory(destDir);
                string destFull = Path.GetFullPath(destDir + Path.DirectorySeparatorChar);

                var manifest = new CacheManifest();
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;   // pure directory entry
                        string rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                        string target = Path.GetFullPath(Path.Combine(destDir, rel));
                        if (!target.StartsWith(destFull, StringComparison.OrdinalIgnoreCase))
                        {
                            Directory.Delete(destDir, true);
                            return null;   // traversal attempt - reject the whole package
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destDir);
                        entry.ExtractToFile(target, true);

                        Classify(manifest, entry.FullName);
                    }
                }

                foreach (var f in manifest.Mods.Concat(manifest.Plugins).Concat(manifest.UserLibs))
                {
                    string p = FindExtractedFile(destDir, f);
                    string h = p != null ? Shared.ProfileBuilder.Sha256OfFile(p) : null;
                    if (h != null) manifest.Hashes[f] = h;
                }

                File.WriteAllText(Path.Combine(destDir, ManifestName), JsonSerializer.Serialize(manifest, JsonOpts));
                return manifest;
            }
            catch
            {
                try { if (Directory.Exists(destDir)) Directory.Delete(destDir, true); } catch { /* ignore */ }
                return null;
            }
        }

        /// <summary>The on-disk path of a classified DLL inside a package dir (packages nest them differently).</summary>
        internal static string FindExtractedFile(string packageDir, string fileName)
        {
            try
            {
                return Directory.GetFiles(packageDir, fileName, SearchOption.AllDirectories)
                                .OrderBy(p => p.Length)   // prefer the shallowest occurrence
                                .FirstOrDefault();
            }
            catch { return null; }
        }

        // Thunderstore MelonLoader packages nest their payload under Mods/ (or MelonLoader/Mods/), Plugins/ and
        // UserLibs/ - or ship a bare DLL at the zip root. Everything else (manifest.json, icon.png, README, ...)
        // stays inert in the cache.
        private static void Classify(CacheManifest m, string entryPath)
        {
            string p = entryPath.Replace('\\', '/');
            string name = p.Substring(p.LastIndexOf('/') + 1);
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { m.Ignored.Add(name); return; }

            string lower = "/" + p.ToLowerInvariant();
            if (lower.Contains("/mods/")) m.Mods.Add(name);
            else if (lower.Contains("/plugins/")) m.Plugins.Add(name);
            else if (lower.Contains("/userlibs/")) m.UserLibs.Add(name);
            else if (!p.Contains('/')) m.Mods.Add(name);   // bare DLL at the zip root
            else m.Ignored.Add(name);
        }

        private static string Sanitize(string s) =>
            new string((s ?? "x").Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_').ToArray());
    }
}
