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
        /// <summary>Package-cache directories of DEPENDENCY packages fetched only for their Plugins/UserLibs payload
        /// (e.g. SteamNetworkLib for PropHunt) - shared libraries a synced mod cannot load without.</summary>
        public List<string> LibPackageDirs = new List<string>();
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
                // A failed auto-download must not stay "Download": that status means "will be fetched", so the entry
                // would silently vanish from the profile inputs while the checklist (which lists manual/dropped rows)
                // never shows it. Demote it to what it actually is - a mod the player has to fetch by hand.
                if (!TsIndex.SplitDependency(e.Mod.Source.Substring(3), out var fullName, out var version))
                { Downgrade(e, "the host's download reference is unreadable"); allOk = false; continue; }
                string dir = await ThunderstoreClient.EnsurePackageAsync(ProfileEngine.GameRoot, index, fullName, version, progress, ct).ConfigureAwait(false);
                if (dir == null)
                {
                    Core.Log?.Warning($"[sync] '{e.Mod.File}': {fullName} {version} could not be downloaded; falling back to the manual link.");
                    Downgrade(e, "Thunderstore didn't hand it over - grab it here instead");
                    allOk = false; continue;
                }
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
                    // manual/dropped rather than shipping a mismatched DLL into the session. This is the common
                    // "host runs their own build with a released version number" case, so SAY that - otherwise the
                    // player is told "Thunderstore" on the consent screen and then handed a manual checklist.
                    Core.Log?.Warning($"[sync] '{e.Mod.File}': downloaded {fullName} {version} does not match the host's hash; skipping.");
                    Downgrade(e, $"the host runs a different build than Thunderstore's {version} - ask them for their file");
                    allOk = false;
                }
            }

            // The shared libraries the synced mods need, for EVERY Thunderstore entry - not only the ones this run
            // downloaded. A rejoin (or a second sync against the same host) resolves the mods straight from the
            // package cache, and the libraries must still be there.
            var tsEntries = diff.Entries
                .Where(x => x.Mod?.Source != null && x.Mod.Source.StartsWith("ts:", StringComparison.Ordinal)
                            && x.Status != DiffStatus.Manual && x.Status != DiffStatus.Dropped)
                .ToList();
            if (tsEntries.Count > 0)
            {
                try
                {
                    index ??= await ThunderstoreClient.GetIndexAsync(ProfileEngine.GameRoot, false, ct).ConfigureAwait(false);
                    foreach (var e in tsEntries)
                        if (TsIndex.SplitDependency(e.Mod.Source.Substring(3), out var fn, out var ver))
                            await EnsureLibDependenciesAsync(diff, index, fn, ver, progress, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { Core.Log?.Warning("[sync] library dependencies could not be resolved: " + e.Message); }
            }
            return allOk;
        }

        /// <summary>An entry that could not be auto-fetched becomes a hand-install (or a drop when nothing points at
        /// it), so every "not in the session" mod is visible in exactly one place: the manual checklist. The reason
        /// rides along as the row's note - a checklist row that appears out of nowhere after the consent screen
        /// promised an automatic download needs to explain itself.</summary>
        private static void Downgrade(DiffEntry e, string why = null)
        {
            e.Status = string.IsNullOrEmpty(e.Mod.Source) ? DiffStatus.Dropped : DiffStatus.Manual;
            if (!string.IsNullOrEmpty(why)) e.ManualNote = why;
        }

        /// <summary>How deep the dependency walk goes, and how many library packages it may fetch - a runaway
        /// closure must never turn one join into dozens of downloads.</summary>
        private const int MaxLibDepth = 2;
        private const int MaxLibPackages = 8;

        /// <summary>
        /// Fetch the SHARED LIBRARIES a synced Thunderstore mod declares as dependencies (its Plugins/UserLibs
        /// payload, e.g. ifBars-SteamNetworkLib_Il2Cpp for PropHunt). The manifest only carries mod DLLs, so without
        /// this a joiner installs the mod and it then throws "Could not load file or assembly" every frame.
        ///
        /// Only the library payload is taken: a dependency's own MOD dlls are deliberately ignored - the host's
        /// manifest is the authority on which mods run, and anything the host really loads is already an entry.
        /// Essentials and the MelonLoader pseudo-package are skipped; a failure is never fatal (the mod may still
        /// work, and the session runs either way).
        /// </summary>
        private static async System.Threading.Tasks.Task EnsureLibDependenciesAsync(SyncDiff diff, TsIndex index,
            string fullName, string version, IProgress<(string Label, long Done, long Total)> progress,
            System.Threading.CancellationToken ct, int depth = 0)
        {
            if (index == null || depth >= MaxLibDepth || diff.LibPackageDirs.Count >= MaxLibPackages) return;
            var deps = index.Find(fullName)?.Get(version)?.Dependencies;
            if (deps == null) return;

            foreach (var dep in deps)
            {
                if (diff.LibPackageDirs.Count >= MaxLibPackages) return;
                if (!TsIndex.SplitDependency(dep, out var depName, out var depVersion)) continue;
                if (Essentials.IsEssentialPackageName(depName)) continue;
                if (depName.IndexOf("melonloader", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                string dir = PackageCache.PathFor(PackageCache.RootFor(ProfileEngine.GameRoot), depName, depVersion);
                if (diff.LibPackageDirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) continue;

                try
                {
                    dir = await ThunderstoreClient.EnsurePackageAsync(ProfileEngine.GameRoot, index, depName, depVersion, progress, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { Core.Log?.Warning($"[sync] dependency '{dep}' could not be fetched: {e.Message}"); continue; }
                if (dir == null) continue;

                var mf = PackageCache.ReadManifest(dir);
                bool hasLibs = mf != null && ((mf.Plugins?.Count ?? 0) > 0 || (mf.UserLibs?.Count ?? 0) > 0);
                if (hasLibs)
                {
                    diff.LibPackageDirs.Add(dir);
                    Core.Log?.Msg($"[sync] library dependency '{dep}' fetched for the session profile.");
                }
                await EnsureLibDependenciesAsync(diff, index, depName, depVersion, progress, ct, depth + 1).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// The Plugins and UserLibs the session profile needs on top of its mods: everything the synced Thunderstore
        /// packages and their library dependencies ship. These are hardlinked into the profile's OWN Plugins/UserLibs
        /// (seeded from the client's global folders), so the client's real install is never written to. Wrong-runtime
        /// flavors (a Mono dll in an Il2Cpp game) are skipped.
        /// </summary>
        internal static void ResolveExtras(SyncDiff diff, out List<BuildInput> plugins, out List<BuildInput> userLibs)
        {
            plugins = new List<BuildInput>();
            userLibs = new List<BuildInput>();
            if (diff == null) return;

            // Derived from the SOURCE, not from what this run happened to download: on a rejoin (or a second sync
            // against the same host) every package is already cached, nothing downloads, and the libraries would
            // otherwise silently go missing. Only directories that really sit in the cache are used.
            string cacheRoot = PackageCache.RootFor(ProfileEngine.GameRoot);
            var index = ThunderstoreClient.GetCachedIndexOrNull(ProfileEngine.GameRoot);
            var dirs = new List<string>();
            void AddDir(string d)
            {
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d) && !dirs.Contains(d, StringComparer.OrdinalIgnoreCase))
                    dirs.Add(d);
            }
            void AddPackage(string fullName, string version, int depth)
            {
                if (fullName == null || version == null) return;
                AddDir(PackageCache.PathFor(cacheRoot, fullName, version));
                if (depth >= MaxLibDepth) return;
                var deps = index?.Find(fullName)?.Get(version)?.Dependencies;
                foreach (var dep in deps ?? new List<string>())
                    if (TsIndex.SplitDependency(dep, out var dn, out var dv)
                        && !Essentials.IsEssentialPackageName(dn)
                        && dn.IndexOf("melonloader", StringComparison.OrdinalIgnoreCase) < 0)
                        AddPackage(dn, dv, depth + 1);
            }
            foreach (var e in diff.Entries)
                if (e.Mod?.Source != null && e.Mod.Source.StartsWith("ts:", StringComparison.Ordinal)
                    && TsIndex.SplitDependency(e.Mod.Source.Substring(3), out var fn, out var ver))
                    AddPackage(fn, ver, 0);
            foreach (var d in diff.LibPackageDirs) AddDir(d);

            var seenP = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenU = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs)
            {
                var mf = PackageCache.ReadManifest(dir);
                if (mf == null) continue;
                Collect(dir, mf.Plugins, seenP, plugins);
                Collect(dir, mf.UserLibs, seenU, userLibs);
            }

            void Collect(string dir, List<string> files, HashSet<string> seen, List<BuildInput> into)
            {
                foreach (var f in files ?? new List<string>())
                {
                    if (string.IsNullOrEmpty(f) || seen.Contains(f)) continue;
                    var candidates = PackageCache.FindExtractedFileAll(dir, f);
                    string src = candidates.FirstOrDefault(c =>
                        !Shared.RuntimeClassifier.IsWrongForThisGame(Shared.RuntimeClassifier.ClassifyFile(c)));
                    if (src == null) continue;
                    seen.Add(f);
                    into.Add(new BuildInput { FileName = f, SourcePath = src });
                }
            }
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
