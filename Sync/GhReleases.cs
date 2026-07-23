using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SideHustle.Profiles;

namespace SideHustle.Sync
{
    /// <summary>
    /// Auto-download for manifest mods whose download link points at a GitHub repository: resolves the repo's
    /// releases via the public API (anonymous - the per-IP limit is ample for a handful of mods per join), picks
    /// the release matching the host's version (tolerantly; newest as fallback) and hash-scans its .dll/.zip
    /// assets for the exact manifest bytes. Every candidate is verified by SHA256 before use, so a wrong pick can
    /// never ship a wrong DLL - it just falls back to the manual flow. Pure BCL on ThunderstoreClient's
    /// TLS-ladder HTTP; worker-thread only, no Unity API.
    /// </summary>
    internal static class GhReleases
    {
        internal const int MaxAssets = 3;
        internal const long MaxAssetBytes = 50L * 1024 * 1024;

        /// <summary>Whether a manifest source ("nx:&lt;url&gt;") points at a GitHub repo whose releases we can
        /// resolve - such a mod downloads automatically instead of via the manual checklist.</summary>
        internal static bool IsGitHubSource(string source) =>
            source != null && source.StartsWith("nx:", StringComparison.Ordinal)
            && TryParseRepo(source.Substring(3), out _, out _);

        /// <summary>Extract owner/repo from a GitHub URL (repo page or any subpath like /releases). Only the
        /// canonical hosts count - gists/raw/pages are not release sources.</summary>
        internal static bool TryParseRepo(string url, out string owner, out string repo)
        {
            owner = repo = null;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps) return false;
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && !uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)) return false;
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) return false;
            owner = parts[0];
            repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                ? parts[1].Substring(0, parts[1].Length - 4) : parts[1];
            return repo.Length > 0;
        }

        /// <summary>Tolerant version equality: a leading 'v' and trailing ".0" segments are cosmetic
        /// ("v1.2.3" == "1.2.3" == "1.2.3.0"). Non-numeric segments never match (fallback picks by recency).</summary>
        internal static bool VersionsMatch(string a, string b)
        {
            var x = Segments(a);
            var y = Segments(b);
            if (x == null || y == null) return false;
            int n = Math.Max(x.Count, y.Count);
            for (int i = 0; i < n; i++)
                if ((i < x.Count ? x[i] : 0) != (i < y.Count ? y[i] : 0)) return false;
            return true;
        }

        private static List<int> Segments(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Trim();
            if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase)) v = v.Substring(1);
            var result = new List<int>();
            foreach (var part in v.Split('.'))
            {
                if (!int.TryParse(part, out int seg) || seg < 0) return null;
                result.Add(seg);
            }
            return result.Count > 0 ? result : null;
        }

        internal sealed class GhAsset { public string Name; public string Url; public long Size; }
        internal sealed class GhRelease { public string Tag; public bool Prerelease; public List<GhAsset> Assets = new List<GhAsset>(); }

        /// <summary>Parse the releases-list JSON. Defensive: an API error body is an object, not an array,
        /// and parses to an empty list.</summary>
        internal static List<GhRelease> ParseReleases(string json)
        {
            var list = new List<GhRelease>();
            if (string.IsNullOrEmpty(json)) return list;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    var r = new GhRelease
                    {
                        Tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null,
                        Prerelease = rel.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True,
                    };
                    if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                        foreach (var a in assets.EnumerateArray())
                            r.Assets.Add(new GhAsset
                            {
                                Name = a.TryGetProperty("name", out var n) ? n.GetString() : null,
                                Url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null,
                                Size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0,
                            });
                    list.Add(r);
                }
            }
            catch { /* malformed body: empty list */ }
            return list;
        }

        /// <summary>The assets worth hash-testing: from the release whose tag matches the host's version, else
        /// the newest stable (else newest) release; .dll/.zip only, size-capped, at most MaxAssets.</summary>
        internal static List<GhAsset> PickAssets(List<GhRelease> releases, string version)
        {
            if (releases == null || releases.Count == 0) return new List<GhAsset>();
            var pick = releases.FirstOrDefault(r => VersionsMatch(r.Tag, version))
                       ?? releases.FirstOrDefault(r => !r.Prerelease)
                       ?? releases[0];
            return pick.Assets
                .Where(a => !string.IsNullOrEmpty(a.Url) && !string.IsNullOrEmpty(a.Name)
                            && (a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                                || a.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                            && a.Size > 0 && a.Size <= MaxAssetBytes)
                .Take(MaxAssets)
                .ToList();
        }

        /// <summary>Fetch the repo's releases and return the exact manifest bytes (the DLL whose SHA256 equals
        /// <paramref name="sha256"/>), or null - the caller falls back to the manual flow.</summary>
        internal static async Task<byte[]> TryFetchAsync(string url, string version, string sha256, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(sha256) || !TryParseRepo(url, out var owner, out var repo)) return null;
            string api = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases?per_page=20";
            var body = await ThunderstoreClient.DownloadBytesAsync(api, ct).ConfigureAwait(false);
            if (body == null) return null;
            var assets = PickAssets(ParseReleases(System.Text.Encoding.UTF8.GetString(body)), version);
            foreach (var a in assets)
            {
                ct.ThrowIfCancellationRequested();
                var bytes = await ThunderstoreClient.DownloadBytesAsync(a.Url, ct).ConfigureAwait(false);
                if (bytes == null) continue;
                var hit = a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    ? ScanZip(bytes, sha256)
                    : Sha256(bytes).Equals(sha256, StringComparison.OrdinalIgnoreCase) ? bytes : null;
                if (hit != null) return hit;
            }
            return null;
        }

        // Every .dll inside the archive, tested against the wanted hash.
        private static byte[] ScanZip(byte[] zipBytes, string sha256)
        {
            try
            {
                using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name) || !entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    using var s = entry.Open();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    var bytes = ms.ToArray();
                    if (Sha256(bytes).Equals(sha256, StringComparison.OrdinalIgnoreCase)) return bytes;
                }
            }
            catch { /* corrupt archive: no match */ }
            return null;
        }

        private static string Sha256(byte[] bytes)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
        }
    }
}
