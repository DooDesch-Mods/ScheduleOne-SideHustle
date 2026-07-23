using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SideHustle.Mods;
using SideHustle.Profiles;

namespace SideHustle.Sync
{
    /// <summary>What publishing the current mod set would give joining clients (shown on the host form).</summary>
    internal sealed class PublishPlan
    {
        public SyncManifest Manifest;
        public int AutoCount;     // ts: sources - clients install these automatically
        public int GhCount;       // nx: GitHub sources - clients auto-download these from GitHub releases
        public int LinkCount;     // other nx: sources - clients get the manual install checklist
        public int DroppedCount;  // no source - ignored by the sync (clients join without them)
    }

    /// <summary>
    /// Host-side manifest building: every loaded mod becomes a manifest entry with its exact version + SHA256
    /// and a RESOLVED source (decided here, at publish time, because only the host knows its mods' provenance):
    /// a Thunderstore ref when the installed version exists in the index, else the mod's own DownloadLink when
    /// it points at a trusted host, else no source (dropped from sync - clients can still join). Side Hustle
    /// itself is never in the manifest: a client browses these lobbies WITH Side Hustle, and its version rides
    /// the manifest header as a warn-only field.
    /// </summary>
    internal static class SyncPublisher
    {
        internal static PublishPlan BuildPlan(TsIndex index, ISet<string> excludeFiles = null)
        {
            var plan = new PublishPlan { Manifest = NewManifestHeader() };
            string modsPath = ModInventory.ModsPath();

            foreach (var m in ModInventory.Loaded())
            {
                if (m.File == null) continue;
                if (IsInfraFile(m.File)) continue;
                if (excludeFiles != null && excludeFiles.Contains(m.File)) continue;

                string sha = modsPath != null ? ModInventory.Sha256OfFile(Path.Combine(modsPath, m.File)) : null;
                string source = ResolveSource(m, index);
                if (source.StartsWith("ts:", StringComparison.Ordinal)) plan.AutoCount++;
                else if (GhReleases.IsGitHubSource(source)) plan.GhCount++;
                else if (source.StartsWith("nx:", StringComparison.Ordinal)) plan.LinkCount++;
                else plan.DroppedCount++;

                plan.Manifest.Mods.Add(new ManifestMod
                {
                    File = m.File,
                    Name = m.Name ?? "",
                    Version = m.Version ?? "",
                    Sha256 = sha ?? "",
                    Source = source,
                });
            }
            return plan;
        }

        private static SyncManifest NewManifestHeader()
        {
            string game = "", sh = "";
            try { game = UnityEngine.Application.version; } catch { /* ignore */ }
            try { sh = typeof(Core).Assembly.GetName().Version?.ToString(3) ?? ""; } catch { /* ignore */ }
            return new SyncManifest
            {
                GameVersion = game,
                MelonLoaderVersion = SafeMelonVersion(),
                SideHustleVersion = sh,
            };
        }

        private static string SafeMelonVersion()
        {
            try { return MelonLoader.Properties.BuildInfo.Version ?? ""; } catch { return ""; }
        }

        // Side Hustle AND its S1API layer are the sync infrastructure itself: a joiner must already have them to
        // browse/join a Side Hustle lobby at all (the client treats them as essentials that ride along - see
        // SyncResolver.IsClientEssential), so counting them in the host's syncable set is pointless and misleading
        // ("12 of 15" when 3 were really SideHustle + S1API). They are never part of the sync plan.
        private static bool IsInfraFile(string file) => Profiles.Essentials.IsEssentialFile(file);

        private static string ResolveSource(LoadedMod m, TsIndex index)
        {
            try
            {
                var pkg = ModMatcher.ConfirmedFullName(m.File) is string full ? index?.Find(full) : ModMatcher.Suggest(m, index);
                // ts: when the installed version exists on Thunderstore. The exact version string wins; failing that
                // a tolerant semver match (so "1.2.3" and "1.2.3.0" agree) picks the store's own version string so
                // the client downloads the right file. The client still verifies the download by hash, so a
                // self-built DLL sharing a store version number harmlessly falls back to manual/dropped there.
                if (pkg != null && !string.IsNullOrEmpty(m.Version))
                {
                    string storeVer = pkg.Get(m.Version) != null
                        ? m.Version
                        : pkg.Versions.FirstOrDefault(v => TsIndex.CompareVersions(v.VersionNumber, m.Version) == 0)?.VersionNumber;
                    if (storeVer != null) return "ts:" + pkg.FullName + "-" + storeVer;
                }
                if (Menu.DownloadLink.IsAllowed(m.DownloadLink))
                    return "nx:" + m.DownloadLink;
                return "";
            }
            catch { return ""; }
        }

        private static string Norm(string s) =>
            s == null ? "" : new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
