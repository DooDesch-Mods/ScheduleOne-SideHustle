using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SideHustle.Profiles
{
    /// <summary>A newer Thunderstore version of an essential (Side Hustle or S1API) than the one running.</summary>
    internal sealed class EssentialUpdate
    {
        public string Name;       // display name, e.g. "Side Hustle"
        public string FullName;   // Thunderstore package, e.g. "DooDesch-SideHustle"
        public string Installed;  // the running version
        public string Latest;
        public string Url;        // the package page in the Schedule I community
    }

    /// <summary>
    /// The update check for the essentials. They are never pinned in a profile (every profile links the copy from the
    /// player's own Mods folder), so the per-profile check never saw them. The running version is compared with the
    /// package's latest version. The per-package endpoint is asked first because a package whose community listing is
    /// unlisted or rejected is missing from the community index; the index is the fallback when that request fails.
    /// The result only informs: the running DLL is never replaced, the player updates through their mod manager.
    /// </summary>
    internal static class EssentialUpdates
    {
        private static readonly (string Name, string Owner, string Package)[] Packages =
        {
            ("Side Hustle", "DooDesch", "SideHustle"),
            ("S1API", "ifBars", "S1API_Forked"),
        };

        /// <summary>Worker thread only. One entry per essential with a newer version; essentials whose running or
        /// latest version is unknown are logged and left out.</summary>
        internal static async Task<List<EssentialUpdate>> CheckAsync(TsIndex index, CancellationToken ct)
        {
            var result = new List<EssentialUpdate>();
            foreach (var (name, owner, package) in Packages)
            {
                string fullName = owner + "-" + package;
                string installed = InstalledVersion(name);
                if (string.IsNullOrEmpty(installed))
                {
                    Core.Log?.Warning($"[profiles] update check: the running version of {name} is unknown; skipped.");
                    continue;
                }

                string latest = await LatestAsync(owner, package, ct).ConfigureAwait(false);
                if (latest == null)
                {
                    latest = index?.Find(fullName)?.Latest?.VersionNumber;
                    if (latest == null)
                    {
                        Core.Log?.Warning($"[profiles] update check: no Thunderstore version found for {fullName}; skipped.");
                        continue;
                    }
                    Core.Log?.Msg($"[profiles] update check: {fullName} {latest} taken from the package index.");
                }

                bool newer = TsIndex.CompareVersions(latest, installed) > 0;
                Core.Log?.Msg($"[profiles] update check: {name} running {installed}, Thunderstore {latest}{(newer ? " - update available" : "")}.");
                if (newer)
                    result.Add(new EssentialUpdate { Name = name, FullName = fullName, Installed = installed, Latest = latest, Url = PageUrl(owner, package) });
            }
            return result;
        }

        private static string InstalledVersion(string name)
        {
            if (name == "Side Hustle") return DooDesch.ModVersion.Current;
            // S1API registers as "S1API (Forked by Bars)"; its loader is a plugin and not in this list.
            var melon = Mods.ModInventory.Melons().FirstOrDefault(m =>
                Mods.ModInventory.NameOf(m).StartsWith("S1API", StringComparison.OrdinalIgnoreCase));
            if (melon == null)
            {
                Core.Log?.Warning("[profiles] update check: no loaded mod named S1API.");
                return null;
            }
            return Mods.ModInventory.VersionOf(melon);
        }

        // The endpoint's own package_url has no community and lands on another game's page, so the link is built here.
        private static string PageUrl(string owner, string package) =>
            $"https://thunderstore.io/c/{ThunderstoreClient.Community}/p/{owner}/{package}/";

        // The per-package endpoint answers for every package, listed or not.
        private static async Task<string> LatestAsync(string owner, string package, CancellationToken ct)
        {
            string url = $"https://thunderstore.io/api/experimental/package/{owner}/{package}/";
            var fetch = await ThunderstoreClient.FetchAsync(url, ct, 4 * 1024 * 1024).ConfigureAwait(false);
            if (fetch.Bytes == null)
            {
                Core.Log?.Warning($"[profiles] update check: GET {url} failed ({fetch.Error}); trying the package index.");
                return null;
            }
            try
            {
                using var doc = JsonDocument.Parse(fetch.Bytes);
                var root = doc.RootElement;
                string latest = root.TryGetProperty("latest", out var l) && l.TryGetProperty("version_number", out var v)
                    ? v.GetString() : null;
                if (latest == null) Core.Log?.Warning($"[profiles] update check: {url} named no latest version; trying the package index.");
                return latest;
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"[profiles] update check: the answer of {url} is unreadable ({e.Message}); trying the package index.");
                return null;
            }
        }
    }
}
