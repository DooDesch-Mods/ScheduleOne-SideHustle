using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SideHustle.Mods;
using SideHustle.Profiles;
using SideHustle.Shared;

namespace SideHustle.Sync
{
    internal enum DiffStatus
    {
        Present,    // an installed file already has the exact bytes - hardlink it
        Cached,     // the exact bytes sit in the package cache (earlier sync/install) - link from there
        Download,   // ts: source, not cached yet - auto-download before the restart
        Manual,     // nx: source - the player fetches it via the link checklist
        Dropped,    // no source - the session runs without it on this client
    }

    internal sealed class DiffEntry
    {
        public ManifestMod Mod;
        public DiffStatus Status;
        public string SourcePath;   // set for Present/Cached
        /// <summary>Same file+version installed but different bytes (recompiled/self-built) - shown as a warning.</summary>
        public bool HashWarn;
        /// <summary>Satisfied from the client's OWN installed copy of a manual/nx: mod (not the host's exact bytes and
        /// not a download), so they don't have to re-fetch a mod they already have.</summary>
        public bool OwnCopyReuse;
        /// <summary>The reused own copy is a DIFFERENT (or unverifiable) version than the host published.</summary>
        public bool VersionWarn;
        /// <summary>Client-side hint for the manual checklist (near-miss guidance from the folder watcher);
        /// never part of the manifest.</summary>
        public string ManualNote;
    }

    internal sealed class SyncDiff
    {
        public List<DiffEntry> Entries = new List<DiffEntry>();
        public List<string> LocalOnly = new List<string>();   // loaded mods NOT in the manifest (unavailable in-session)
        public int Count(DiffStatus s) => Entries.Count(e => e.Status == s);

        /// <summary>A synced profile is only needed when something must be ASSEMBLED that the currently loaded
        /// set does not already provide: a mod to link from the cache/download, or a locally-loaded mod that
        /// must be dropped for the session. All-Present (+ any Dropped/Manual) means the live set already equals
        /// the syncable manifest, so the client can join in place with no restart.</summary>
        public bool NeedsRestart =>
            Entries.Any(e => e.Status == DiffStatus.Cached || e.Status == DiffStatus.Download) || LocalOnly.Count > 0;

        /// <summary>Any entry is satisfied from the client's own copy at a DIFFERENT/unverified version - the join
        /// must route through the consent screen so the player sees the version warning before joining.</summary>
        public bool AnyVersionWarn => Entries.Any(e => e.VersionWarn);
    }

    /// <summary>
    /// Client-side manifest resolution: what of the host's mod set is already here (by SHA256 - the hash wins
    /// over the version everywhere), what the cache can provide, what must be downloaded or fetched manually,
    /// and what is dropped. Also assembles the exact BuildInputs for the sync profile. Worker-thread safe
    /// (hashing every installed mod takes a moment).
    /// </summary>
    internal static class SyncResolver
    {
        internal static SyncDiff Compute(SyncManifest manifest)
        {
            var diff = new SyncDiff();
            string modsPath = ModInventory.ModsPath();
            var localByFile = new Dictionary<string, (string Path, string Sha)>(StringComparer.OrdinalIgnoreCase);
            if (modsPath != null)
            {
                foreach (var f in ModInventory.AvailableFiles())
                {
                    string p = Path.Combine(modsPath, f);
                    if (!File.Exists(p)) p += ".disabled";
                    if (!File.Exists(p)) continue;
                    localByFile[f] = (p, ModInventory.Sha256OfFile(p) ?? "");
                }
            }
            var cacheByHash = BuildCacheHashIndex();
            var loadedList = ModInventory.Loaded();

            foreach (var m in manifest.Mods)
            {
                var e = new DiffEntry { Mod = m };
                bool haveLocal = localByFile.TryGetValue(m.File, out var local);
                bool shaMatch = haveLocal && !string.IsNullOrEmpty(m.Sha256)
                                && string.Equals(local.Sha, m.Sha256, StringComparison.OrdinalIgnoreCase);

                // Version-based match: the Thunderstore version is the compatibility unit. If the client already
                // has the SAME package version the host published, accept the client's own copy even when the bytes
                // differ (a self-built or re-downloaded copy of the same release) instead of forcing a re-download.
                // A DIFFERENT version still falls through to Download below, so the session aligns to the host's
                // version. Only for ts: (Thunderstore) mods, where the version string is authoritative.
                var loaded = loadedList.FirstOrDefault(x => string.Equals(x.File, m.File, StringComparison.OrdinalIgnoreCase));
                bool versionMatch = haveLocal && !shaMatch && loaded != null && !string.IsNullOrEmpty(m.Version)
                                    && m.Source.StartsWith("ts:", StringComparison.Ordinal)
                                    && string.Equals(loaded.Version ?? "", m.Version, StringComparison.OrdinalIgnoreCase)
                                    && SamePackageIdentity(m);

                if (shaMatch)
                {
                    e.Status = DiffStatus.Present;
                    e.SourcePath = local.Path;
                }
                else if (versionMatch)
                {
                    e.Status = DiffStatus.Present;
                    e.SourcePath = local.Path;
                    e.HashWarn = true;   // same version, different bytes - kept your copy, but noted
                }
                else if (!string.IsNullOrEmpty(m.Sha256) && cacheByHash.TryGetValue(m.Sha256, out var cached))
                {
                    e.Status = DiffStatus.Cached;
                    e.SourcePath = cached;
                }
                else if (m.Source.StartsWith("ts:", StringComparison.Ordinal)) e.Status = DiffStatus.Download;
                // A GitHub-hosted link mod downloads like ts: - releases are an open CDN and the hash check gates
                // the result, so the session aligns to the host's exact bytes instead of reusing a local variant.
                else if (GhReleases.IsGitHubSource(m.Source)) e.Status = DiffStatus.Download;
                // nx: / unsourced: before forcing a hand-download or dropping it, reuse the client's OWN installed
                // copy of the same mod (its exact bytes aren't fetchable anyway) so they don't re-download what they have.
                else if (m.Source.StartsWith("nx:", StringComparison.Ordinal)) { if (!TryReuseOwnCopy(m, e, localByFile, loadedList)) e.Status = DiffStatus.Manual; }
                else { if (!TryReuseOwnCopy(m, e, localByFile, loadedList)) e.Status = DiffStatus.Dropped; }

                diff.Entries.Add(e);
            }

            var inManifest = new HashSet<string>(manifest.Mods.Select(m => m.File), StringComparer.OrdinalIgnoreCase);
            foreach (var m in loadedList)
            {
                if (m.File == null || inManifest.Contains(m.File)) continue;
                if (IsClientEssential(m.File)) continue;   // rides along anyway
                diff.LocalOnly.Add(m.Name ?? m.File);
            }
            return diff;
        }

        /// <summary>Download every auto-fetchable entry (ts: Thunderstore, GitHub releases) that is not yet
        /// available locally. Returns false when one failed (the caller re-computes the diff and shows what is
        /// still missing).</summary>
        internal static async System.Threading.Tasks.Task<bool> DownloadMissingAsync(SyncDiff diff,
            IProgress<(string Label, long Done, long Total)> progress, System.Threading.CancellationToken ct)
        {
            TsIndex index = null;   // fetched lazily - a gh-only diff never needs the Thunderstore index
            bool allOk = true;
            foreach (var e in diff.Entries.Where(x => x.Status == DiffStatus.Download))
            {
                if (GhReleases.IsGitHubSource(e.Mod.Source))
                {
                    progress?.Report((e.Mod.File, 0, 0));
                    byte[] bytes = null;
                    try { bytes = await GhReleases.TryFetchAsync(e.Mod.Source.Substring(3), e.Mod.Version, e.Mod.Sha256, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Core.Log?.Warning($"[sync] '{e.Mod.File}': GitHub fetch failed: {ex.Message}"); }
                    string ghPromoted = bytes != null
                        ? ManualInstall.PromoteBytes(bytes, e.Mod.File, e.Mod.Sha256.ToLowerInvariant()) : null;
                    if (ghPromoted != null)
                    {
                        e.Status = DiffStatus.Cached;
                        e.SourcePath = ghPromoted;
                    }
                    else
                    {
                        Core.Log?.Warning($"[sync] '{e.Mod.File}': no GitHub release asset matched the host's hash; falling back to the manual link.");
                        e.Status = DiffStatus.Manual;
                        allOk = false;
                    }
                    continue;
                }

                index ??= await ThunderstoreClient.GetIndexAsync(ProfileEngine.GameRoot, false, ct).ConfigureAwait(false);
                if (!TsIndex.SplitDependency(e.Mod.Source.Substring(3), out var fullName, out var version)) { allOk = false; continue; }
                string dir = await ThunderstoreClient.EnsurePackageAsync(ProfileEngine.GameRoot, index, fullName, version, progress, ct).ConfigureAwait(false);
                if (dir == null) { allOk = false; continue; }
                string src = PackageCache.FindExtractedFile(dir, e.Mod.File);
                string sha = src != null ? ProfileBuilder.Sha256OfFile(src) : null;
                if (src != null && string.Equals(sha, e.Mod.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    e.Status = DiffStatus.Cached;
                    e.SourcePath = src;
                }
                else
                {
                    // The store version does not carry the host's exact bytes (or the file is missing): treat as
                    // manual/dropped rather than shipping a mismatched DLL into the session.
                    Core.Log?.Warning($"[sync] '{e.Mod.File}': downloaded {fullName} {version} does not match the host's hash; skipping.");
                    e.Status = string.IsNullOrEmpty(e.Mod.Source) ? DiffStatus.Dropped : DiffStatus.Manual;
                    allOk = false;
                }
            }
            return allOk;
        }

        /// <summary>The sync profile's exact inputs: every resolved manifest file + the client-side essentials
        /// (Side Hustle itself and, when the manifest does not carry one, the local S1API).</summary>
        internal static List<BuildInput> ToInputs(SyncDiff diff)
        {
            var inputs = new List<BuildInput>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in diff.Entries)
            {
                if (e.SourcePath == null) continue;
                string name = e.Mod.File;
                if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 9);
                if (seen.Add(name)) inputs.Add(new BuildInput { FileName = name, SourcePath = e.SourcePath });
            }

            string modsPath = ModInventory.ModsPath();
            if (modsPath != null)
            {
                foreach (var m in ModInventory.Loaded())
                {
                    if (m.File == null || !IsClientEssential(m.File) || seen.Contains(m.File)) continue;
                    string p = Path.Combine(modsPath, m.File);
                    if (File.Exists(p) && seen.Add(m.File)) inputs.Add(new BuildInput { FileName = m.File, SourcePath = p });
                }
            }
            return inputs;
        }

        // For a manual/nx: (or unsourced) manifest mod whose exact bytes we cannot fetch, reuse the client's OWN
        // installed copy of the same mod instead of forcing a hand-download. Byte-exact hash/cache matches already
        // won in Compute, so this is the identity fallback: tiered, first hit wins, and never masquerades a different
        // mod as the wanted one. Sets OwnCopyReuse (+ HashWarn since bytes aren't exact) and VersionWarn when the
        // reused copy's version differs from or can't be verified against the host's. Returns true when it resolved.
        private static bool TryReuseOwnCopy(ManifestMod m, DiffEntry e,
            Dictionary<string, (string Path, string Sha)> localByFile, List<LoadedMod> loadedList)
        {
            try
            {
                // TIER 0: byte-identical copy under ANY file name (exact bytes, just renamed) - fully safe, no warn.
                if (!string.IsNullOrEmpty(m.Sha256))
                    foreach (var kv in localByFile)
                        if (string.Equals(kv.Value.Sha, m.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            bool live = loadedList.Any(x => string.Equals(x.File, kv.Key, StringComparison.OrdinalIgnoreCase));
                            e.Status = live ? DiffStatus.Present : DiffStatus.Cached;
                            e.SourcePath = kv.Value.Path; e.OwnCopyReuse = true;
                            return true;
                        }

                // Same DLL file name in the client's install?
                if (localByFile.TryGetValue(m.File, out var same))
                {
                    var loadedSame = loadedList.FirstOrDefault(x => string.Equals(x.File, m.File, StringComparison.OrdinalIgnoreCase));
                    if (loadedSame != null)
                    {
                        // TIER 1: same file name, currently loaded. Abort if BOTH mod names are known and differ (a
                        // different mod sharing a generic DLL name) - never masquerade.
                        if (!string.IsNullOrEmpty(m.Name) && !string.IsNullOrEmpty(loadedSame.Name)
                            && Norm(m.Name) != Norm(loadedSame.Name)) return false;
                        e.Status = DiffStatus.Present; e.SourcePath = same.Path;
                        e.OwnCopyReuse = true; e.HashWarn = true;
                        e.VersionWarn = !(!string.IsNullOrEmpty(m.Version)
                            && string.Equals(loadedSame.Version ?? "", m.Version, StringComparison.OrdinalIgnoreCase));
                        return true;
                    }
                    // TIER 3: same file present on disk but NOT loaded (disabled/failed). Version unreadable while
                    // unloaded -> flag it and load it via the restart (Cached).
                    e.Status = DiffStatus.Cached; e.SourcePath = same.Path;
                    e.OwnCopyReuse = true; e.HashWarn = true; e.VersionWarn = true;
                    return true;
                }

                // TIER 2: exactly one loaded mod with the same NAME under a different file that exists on disk.
                if (!string.IsNullOrEmpty(m.Name))
                {
                    var byName = loadedList.Where(x => !string.IsNullOrEmpty(x.Name) && Norm(x.Name) == Norm(m.Name)
                                                       && x.File != null && localByFile.ContainsKey(x.File)).ToList();
                    if (byName.Count == 1)
                    {
                        var only = byName[0];
                        e.Status = DiffStatus.Present; e.SourcePath = localByFile[only.File].Path;
                        e.OwnCopyReuse = true; e.HashWarn = true;
                        e.VersionWarn = !(!string.IsNullOrEmpty(m.Version)
                            && string.Equals(only.Version ?? "", m.Version, StringComparison.OrdinalIgnoreCase));
                        return true;
                    }
                }
                return false;
            }
            catch { return false; }
        }

        // Same-version acceptance is only safe when the client's local copy is the SAME Thunderstore package the
        // host published - otherwise a coincidental same-name+same-version DLL from a DIFFERENT package would
        // masquerade as it and the session would run the wrong mod. Requires a confirmed local mapping; an unmapped
        // (hand-dropped) copy falls through to the hash/download path, which fetches the host's exact package.
        private static bool SamePackageIdentity(ManifestMod m)
        {
            try
            {
                if (!TsIndex.SplitDependency(m.Source.Substring(3), out var full, out _)) return false;
                string local = ModMatcher.ConfirmedFullName(m.File);
                return local != null && string.Equals(local, full, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // Side Hustle must ride into every sync profile (it drives the rejoin + the switch-back UI); S1API is
        // its API layer and comes from the manifest when the host runs it, from the local install otherwise.
        internal static bool IsClientEssential(string file)
        {
            string f = Norm(file);
            return f.Contains("sidehustle") || f.Contains("s1api");
        }

        // Every cached DLL by hash: package-cache manifests record theirs, manual promotions are keyed by it.
        private static Dictionary<string, string> BuildCacheHashIndex()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string cacheRoot = PackageCache.RootFor(ProfileEngine.GameRoot);
                if (!Directory.Exists(cacheRoot)) return map;
                foreach (var mf in Directory.GetFiles(cacheRoot, PackageCache.ManifestName, SearchOption.AllDirectories))
                {
                    var manifest = PackageCache.ReadManifest(Path.GetDirectoryName(mf));
                    if (manifest?.Hashes == null) continue;
                    foreach (var kv in manifest.Hashes)
                    {
                        string src = PackageCache.FindExtractedFile(Path.GetDirectoryName(mf), kv.Key);
                        if (src != null && !map.ContainsKey(kv.Value)) map[kv.Value] = src;
                    }
                }
                string manualRoot = PackageCache.ManualRoot(cacheRoot);
                if (Directory.Exists(manualRoot))
                    foreach (var dir in Directory.GetDirectories(manualRoot))
                    {
                        string sha = Path.GetFileName(dir);
                        var file = Directory.GetFiles(dir).FirstOrDefault();
                        if (file != null && !map.ContainsKey(sha)) map[sha] = file;
                    }
            }
            catch (Exception e) { Core.Log?.Warning("[sync] cache hash index failed: " + e.Message); }
            return map;
        }

        private static string Norm(string s) =>
            s == null ? "" : new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
