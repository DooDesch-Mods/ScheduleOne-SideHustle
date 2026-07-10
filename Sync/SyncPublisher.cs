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
        public int LinkCount;     // nx: sources - clients get a manual install checklist
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

        // Side Hustle is the sync infrastructure itself: required to browse/join these lobbies at all, not on
        // Thunderstore, and version-checked via the manifest header instead.
        private static bool IsInfraFile(string file)
        {
            string f = Norm(file);
            return f.Contains("sidehustle");
        }

        private static string ResolveSource(LoadedMod m, TsIndex index)
        {
            try
            {
                var pkg = ModMatcher.ConfirmedFullName(m.File) is string full ? index?.Find(full) : ModMatcher.Suggest(m, index);
                // ts: only when the EXACT installed version exists on Thunderstore - the client verifies the
                // download by hash, and a self-built DLL with a store version's number would just fail there.
                if (pkg != null && !string.IsNullOrEmpty(m.Version) && pkg.Get(m.Version) != null)
                    return "ts:" + pkg.FullName + "-" + m.Version;
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
