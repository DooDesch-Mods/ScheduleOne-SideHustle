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
        /// <summary>file name -> game-branch tag ("il2cpp"|"mono"|"universal"|"unknown"), classified at extract
        /// time. A UI convenience (install summary); the resolver classifies the resolved PATH authoritatively.
        /// Absent in manifests written by older builds - fall back to on-demand classification.</summary>
        [JsonPropertyName("runtime")] public Dictionary<string, string> Runtime { get; set; } = new Dictionary<string, string>();
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
            string file = Path.Combine(packageDir ?? "", ManifestName);
            try { return JsonSerializer.Deserialize<CacheManifest>(File.ReadAllText(file)); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (Exception e)
            {
                Shared.SharedLog.WarnOnce("manifest:" + file, $"the cache manifest '{file}' is unreadable: {e.Message}");
                return null;
            }
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
                DeleteTreeNoFollow(destDir);
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
                            Shared.SharedLog.Warning($"'{zipPath}' holds an entry that escapes its folder ('{entry.FullName}'); the package is rejected.");
                            DeleteTreeNoFollow(destDir);
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
                    if (p != null) manifest.Runtime[f] = Shared.RuntimeClassifier.ToTag(Shared.RuntimeClassifier.ClassifyFile(p));
                }

                File.WriteAllText(Path.Combine(destDir, ManifestName), JsonSerializer.Serialize(manifest, JsonOpts));
                return manifest;
            }
            catch (Exception e)
            {
                Shared.SharedLog.Warning($"extracting '{zipPath}' failed: {e.GetType().Name}: {e.Message}");
                DeleteTreeNoFollow(destDir);
                return null;
            }
        }

        /// <summary>Delete a directory tree without ever following a reparse point out of it. A junction or symlink
        /// at the root is removed as a link (its target is untouched); inside the tree, .NET removes a link as a link.
        /// Failure is logged, not thrown - a leftover folder is retried by the next extraction.</summary>
        internal static void DeleteTreeNoFollow(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                {
                    Shared.SharedLog.Warning($"'{dir}' is a link; removing the link only, not what it points to.");
                    Directory.Delete(dir, false);
                    return;
                }
                Directory.Delete(dir, true);
            }
            catch (Exception e) { Shared.SharedLog.Warning($"could not remove '{dir}': {e.Message}"); }
        }

        /// <summary>The on-disk path of a classified DLL inside a package dir (packages nest them differently).</summary>
        private static bool InMonoFolder(string path) =>
            path != null && ("/" + path.Replace('\\', '/').ToLowerInvariant()).Contains("/mono/");

        internal static string FindExtractedFile(string packageDir, string fileName)
        {
            try
            {
                return FindExtractedFileAll(packageDir, fileName).FirstOrDefault();
            }
            catch (Exception e)
            {
                Shared.SharedLog.WarnOnce("find:" + packageDir, $"could not search '{packageDir}' for {fileName}: {e.Message}");
                return null;
            }
        }

        /// <summary>All occurrences of a DLL name inside a package dir, copies outside a "Mono" folder first, then
        /// shallowest first. Dual-branch packages can ship the SAME file name under Mods/Mono/ and Mods/IL2CPP/, and
        /// "Mono" is the shorter path: ordering by length alone picked the copy this IL2CPP game cannot load, and a
        /// sync then reported the host's build as different from Thunderstore.</summary>
        internal static List<string> FindExtractedFileAll(string packageDir, string fileName)
        {
            try
            {
                return Directory.GetFiles(packageDir, fileName, SearchOption.AllDirectories)
                                .OrderBy(p => InMonoFolder(p) ? 1 : 0)
                                .ThenBy(p => p.Length)
                                .ToList();
            }
            catch (Exception e)
            {
                Shared.SharedLog.WarnOnce("findall:" + packageDir, $"could not search '{packageDir}' for {fileName}: {e.Message}");
                return new List<string>();
            }
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
