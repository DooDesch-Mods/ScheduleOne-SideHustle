using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SideHustle.Profiles
{
    /// <summary>One version of a Thunderstore package (the fields the manager needs, nothing more).</summary>
    internal sealed class TsVersion
    {
        [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
        [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
        [JsonPropertyName("icon")] public string Icon { get; set; } = "";
        [JsonPropertyName("dependencies")] public List<string> Dependencies { get; set; } = new List<string>();
        [JsonPropertyName("file_size")] public long FileSize { get; set; }
        [JsonPropertyName("downloads")] public long Downloads { get; set; }
    }

    /// <summary>One Thunderstore package from the community index. full_name = "Owner-Name" (no version).</summary>
    internal sealed class TsPackage
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("full_name")] public string FullName { get; set; } = "";
        [JsonPropertyName("owner")] public string Owner { get; set; } = "";
        [JsonPropertyName("package_url")] public string PackageUrl { get; set; } = "";
        [JsonPropertyName("is_deprecated")] public bool IsDeprecated { get; set; }
        // ISO-8601 UTC timestamps - kept as strings on purpose: they sort correctly ordinally and skip DateTime
        // parsing over thousands of index entries. rating_score backs the "Top rated" sort.
        [JsonPropertyName("date_created")] public string DateCreated { get; set; } = "";
        [JsonPropertyName("date_updated")] public string DateUpdated { get; set; } = "";
        [JsonPropertyName("rating_score")] public long RatingScore { get; set; }
        [JsonPropertyName("is_pinned")] public bool IsPinned { get; set; }
        [JsonPropertyName("categories")] public List<string> Categories { get; set; } = new List<string>();
        [JsonPropertyName("versions")] public List<TsVersion> Versions { get; set; } = new List<TsVersion>();

        /// <summary>A curated bundle (mostly dependencies, no standalone mod of its own) rather than a single mod -
        /// Thunderstore's own "Modpacks" category. Worth flagging so the player knows installing it pulls in a set.</summary>
        public bool IsModpack => Categories != null &&
            Categories.Any(c => c != null && c.Equals("Modpacks", StringComparison.OrdinalIgnoreCase));

        public TsVersion Latest => Versions.Count > 0 ? Versions[0] : null;   // the index lists newest first
        public TsVersion Get(string version) =>
            Versions.FirstOrDefault(v => string.Equals(v.VersionNumber, version, StringComparison.OrdinalIgnoreCase));
        public long TotalDownloads { get { long t = 0; foreach (var v in Versions) t += v.Downloads; return t; } }
    }

    /// <summary>
    /// The parsed community package index plus the pure logic on top of it: lookups, the dependency closure
    /// (BFS over "Owner-Name-1.2.3" strings) and a tolerant semver comparison. Pure BCL so the console test
    /// harness can exercise it against fixture JSON; all networking lives in ThunderstoreClient.
    /// </summary>
    internal sealed class TsIndex
    {
        private readonly Dictionary<string, TsPackage> _byFullName;
        internal IReadOnlyList<TsPackage> Packages { get; }

        internal TsIndex(List<TsPackage> packages)
        {
            Packages = packages ?? new List<TsPackage>();
            _byFullName = new Dictionary<string, TsPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Packages)
                if (!string.IsNullOrEmpty(p.FullName)) _byFullName[p.FullName] = p;
        }

        internal static TsIndex Parse(string json)
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<TsPackage>>(json);
                return list == null ? null : new TsIndex(list);
            }
            catch { return null; }
        }

        internal TsPackage Find(string fullName) =>
            !string.IsNullOrEmpty(fullName) && _byFullName.TryGetValue(fullName, out var p) ? p : null;

        /// <summary>Split a dependency string "Owner-Name-1.2.3" into full name and version. Package names may
        /// contain '-', versions never do, so the version is everything after the LAST dash that looks like one.</summary>
        internal static bool SplitDependency(string dep, out string fullName, out string version)
        {
            fullName = null; version = null;
            if (string.IsNullOrEmpty(dep)) return false;
            int i = dep.LastIndexOf('-');
            if (i <= 0 || i >= dep.Length - 1) return false;
            string ver = dep.Substring(i + 1);
            if (!ver.Contains('.') || !char.IsDigit(ver[0])) return false;
            fullName = dep.Substring(0, i);
            version = ver;
            return true;
        }

        /// <summary>
        /// The full dependency closure of the given package versions: every (fullName, version) that must be in
        /// the profile, requested roots included. A dependency pinned to a version that no longer exists in the
        /// index falls back to the package's latest; the MelonLoader pseudo-package is skipped (the loader is the
        /// runtime, never a profile mod); unknown packages are reported via <paramref name="unresolved"/>.
        /// </summary>
        internal List<(string FullName, string Version)> ResolveClosure(
            IEnumerable<(string FullName, string Version)> roots, out List<string> unresolved)
        {
            unresolved = new List<string>();
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(string FullName, string Version)>(roots);
            int guard = 0;

            while (queue.Count > 0 && guard++ < 10_000)
            {
                var (fullName, version) = queue.Dequeue();
                if (string.IsNullOrEmpty(fullName)) continue;
                if (fullName.Equals("LavaGang-MelonLoader", StringComparison.OrdinalIgnoreCase)) continue;

                var pkg = Find(fullName);
                if (pkg == null) { if (!unresolved.Contains(fullName)) unresolved.Add(fullName); continue; }

                var ver = pkg.Get(version) ?? pkg.Latest;
                if (ver == null) { if (!unresolved.Contains(fullName)) unresolved.Add(fullName); continue; }

                // Already claimed: keep the higher version (two dependents may pin different versions).
                if (result.TryGetValue(pkg.FullName, out var existing))
                {
                    if (CompareVersions(ver.VersionNumber, existing) <= 0) continue;
                }
                result[pkg.FullName] = ver.VersionNumber;

                foreach (var dep in ver.Dependencies ?? Enumerable.Empty<string>())
                    if (SplitDependency(dep, out var depName, out var depVer))
                        queue.Enqueue((depName, depVer));
            }

            return result.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        /// <summary>Tolerant semver-ish compare: numeric segment-by-segment, string fallback for tags.</summary>
        internal static int CompareVersions(string a, string b)
        {
            var pa = (a ?? "").Split('.', '-', '+');
            var pb = (b ?? "").Split('.', '-', '+');
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                string sa = i < pa.Length ? pa[i] : "0";
                string sb = i < pb.Length ? pb[i] : "0";
                bool na = int.TryParse(sa, out int ia);
                bool nb = int.TryParse(sb, out int ib);
                int c = na && nb ? ia.CompareTo(ib) : string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
            }
            return 0;
        }
    }
}
