using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SideHustle.Mods;
using SideHustle.Shared;

namespace SideHustle.Profiles
{
    internal enum EnsureStatus { Ready, Failed, Blocked, Cancelled }

    /// <summary>Snapshot for progress UI (polled from the main thread; workers report via IProgress).</summary>
    internal sealed class ProfileProgress
    {
        public string Phase = "";        // Resolving | Downloading | Extracting | Building
        public string CurrentItem = "";
        public int Done; public int Total;
        public long BytesDone; public long BytesTotal;
    }

    /// <summary>
    /// The named-profile engine: registry access, package installs (dependency closure -> cache), profile
    /// builds (hardlinked Mods dir + fingerprint) and the switch flow. Switching uses variant A: the committed
    /// choice goes into profiles.json as pendingSwitch and the game relaunches PLAIN (no basedir) - the boot
    /// plugin consumes the pending switch and redirects the mod scan in-process. The real Mods folder is never
    /// managed here (decision: external mod managers stay authoritative over it); profiles only LINK to it.
    /// </summary>
    internal static class ProfileEngine
    {
        internal const string EnvActiveProfile = ProfileStore.EnvActiveProfile;
        internal const string EnvContinueToken = ProfileStore.EnvContinueToken;

        internal static string GameRoot => ModInventory.GameRoot();
        internal static string StorePath => ProfileStore.PathFor(GameRoot);
        internal static string CacheRoot => PackageCache.RootFor(GameRoot);

        internal static string ProfileModsDir(string profileId) =>
            ProfileResolver.ProfileModsDir(GameRoot, profileId);

        /// <summary>The profile the CURRENT process booted with ("" = full mod set). A named-profile session boots
        /// into an isolated base under profiles\&lt;sanitized-id&gt;\, so the active id is the registry profile whose
        /// sanitized id matches that folder. (The legacy env handoff still wins if a boot plugin set it.)</summary>
        internal static string ActiveProfileId
        {
            get
            {
                try
                {
                    string env = Environment.GetEnvironmentVariable(EnvActiveProfile);
                    if (!string.IsNullOrEmpty(env)) return env;
                    if (!AltBase.IsNamedProfileSession()) return "";
                    string folder = Path.GetFileName((AltBase.CurrentBase() ?? "").TrimEnd('\\', '/'));
                    if (string.IsNullOrEmpty(folder)) return "";
                    var doc = LoadStore(out _);
                    var p = doc.Profiles.FirstOrDefault(x => ProfileResolver.Sanitize(x.Id).Equals(folder, StringComparison.OrdinalIgnoreCase));
                    return p?.Id ?? "";
                }
                catch { return ""; }
            }
        }

        internal static bool IsProfileSession => !string.IsNullOrEmpty(ActiveProfileId);

        /// <summary>One-shot read of the continue token the boot plugin carried across a pending switch.</summary>
        internal static string ConsumeContinueToken()
        {
            try
            {
                string t = Environment.GetEnvironmentVariable(EnvContinueToken);
                if (!string.IsNullOrEmpty(t)) Environment.SetEnvironmentVariable(EnvContinueToken, null);
                return t ?? "";
            }
            catch { return ""; }
        }

        // --- registry ---

        // profiles.json has no cross-thread guard of its own (atomic PER write, but a Load -> mutate -> Save
        // sequence is not). Worker-thread builds/installs run concurrently with main-thread edits, so every such
        // transaction takes _storeLock to stay atomic (last-writer-wins would otherwise silently drop edits).
        // _rebuildGate serializes the slow Mods-dir rebuilds so two never relink the same folder at once.
        private static readonly object _storeLock = new object();
        private static readonly object _rebuildGate = new object();

        internal static ProfilesFile LoadStore(out bool writable) => ProfileStore.Load(StorePath, out writable);

        internal static bool SaveStore(ProfilesFile doc) => ProfileStore.Save(StorePath, doc);

        /// <summary>Run a read-modify-write against the registry atomically w.r.t. other in-process mutations.
        /// <paramref name="mutate"/> returns false to abort without saving (e.g. profile gone, nothing changed).</summary>
        private static bool Transact(Func<ProfilesFile, bool> mutate)
        {
            lock (_storeLock)
            {
                var doc = LoadStore(out bool writable);
                if (!writable) return false;
                if (!mutate(doc)) return false;
                return SaveStore(doc);
            }
        }

        /// <summary>An IProgress that forwards synchronously on the reporting thread, unlike System.Progress&lt;T&gt;
        /// which hops to the thread pool (no SynchronizationContext here) and can deliver reports out of order.</summary>
        private sealed class SyncProgress<T> : IProgress<T>
        {
            private readonly Action<T> _on;
            internal SyncProgress(Action<T> on) { _on = on; }
            public void Report(T value) { try { _on?.Invoke(value); } catch { } }
        }

        internal static ProfileDef CreateProfile(string name)
        {
            ProfileDef def = null;
            bool ok = Transact(doc =>
            {
                string baseId = ProfileResolver.Sanitize(string.IsNullOrWhiteSpace(name) ? "profile" : name.Trim());
                string id = baseId;
                for (int i = 2; doc.Profiles.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); i++)
                    id = baseId + "-" + i;
                def = new ProfileDef
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(name) ? id : name.Trim(),
                    Created = DateTime.UtcNow.ToString("o"),
                    Modified = DateTime.UtcNow.ToString("o"),
                };
                doc.Profiles.Add(def);
                return true;
            });
            return ok ? def : null;
        }

        /// <summary>Pin a set of Thunderstore packages (name+version) into a profile as manual refs - used when
        /// creating a profile from a synced lobby's manifest. Files link from the package cache on the next build
        /// (they are present from the just-finished sync); nothing is downloaded here. Essentials and
        /// already-pinned packages are skipped.</summary>
        internal static void PinThunderstoreMods(string profileId, IEnumerable<(string FullName, string Version)> mods)
        {
            var list = mods?.ToList() ?? new List<(string FullName, string Version)>();
            Transact(doc =>
            {
                var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
                if (p == null) return false;
                foreach (var (full, ver) in list)
                {
                    if (string.IsNullOrEmpty(full) || Essentials.IsEssentialPackageName(full)) continue;
                    if (p.Mods.Any(m => m.Source == "thunderstore" && string.Equals(m.FullName, full, StringComparison.OrdinalIgnoreCase))) continue;
                    var mf = PackageCache.ReadManifest(PackageCache.PathFor(CacheRoot, full, ver));
                    p.Mods.Add(new ProfileModRef { Source = "thunderstore", FullName = full, Version = ver, Files = mf?.Mods, AsDependency = false });
                }
                p.Modified = DateTime.UtcNow.ToString("o");
                return true;
            });
        }

        /// <summary>Change a profile's display name only. The Id (folder + registry key) stays stable, so nothing
        /// has to be rebuilt or re-linked. Returns false if the store is read-only or the profile is gone.</summary>
        internal static bool RenameProfile(string profileId, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) return false;
            return Transact(doc =>
            {
                var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
                if (p == null) return false;
                p.Name = newName.Trim();
                p.Modified = DateTime.UtcNow.ToString("o");
                return true;
            });
        }

        /// <summary>Set a profile's free-text description (the notes shown in the list/detail). An empty value clears
        /// it. Returns false if the store is read-only or the profile is gone.</summary>
        internal static bool SetDescription(string profileId, string notes)
        {
            return Transact(doc =>
            {
                var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
                if (p == null) return false;
                p.Notes = (notes ?? "").Trim();
                p.Modified = DateTime.UtcNow.ToString("o");
                return true;
            });
        }

        /// <summary>The on-disk folder that holds a profile's (hardlinked) mod set - what "Open folder" reveals.</summary>
        internal static string ProfileFolder(string profileId) => Path.GetDirectoryName(ProfileModsDir(profileId));

        internal static bool DeleteProfile(string profileId)
        {
            bool ok = Transact(doc =>
            {
                doc.Profiles.RemoveAll(p => p.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
                if (doc.LastChoice == profileId) doc.LastChoice = "";
                if (doc.Settings.DefaultProfileId == profileId) doc.Settings.DefaultProfileId = "";
                return true;
            });
            try
            {
                string dir = Path.GetDirectoryName(ProfileModsDir(profileId));
                if (ok && dir != null && Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { /* locked files: the folder holds only hardlinks; harmless leftovers */ }
            return ok;
        }

        // --- resolve + build ---

        /// <summary>Resolve every mod ref of a profile to a concrete file, reporting refs with no source.</summary>
        internal static List<BuildInput> ResolveInputs(ProfileDef p, out List<string> missing) =>
            ProfileResolver.Resolve(GameRoot, p, out missing);

        /// <summary>As above, additionally reporting inputs dropped as wrong-runtime (Mono) builds.</summary>
        internal static List<BuildInput> ResolveInputs(ProfileDef p, out List<string> missing, out List<ExcludedInput> excluded) =>
            ProfileResolver.Resolve(GameRoot, p, out missing, out excluded);

        /// <summary>The exclusions worth the player's ATTENTION: whole refs that were disabled entirely (a skipped
        /// Mono variant of a dual package is routine and stays out of this list). Null when empty - the store
        /// serializer omits null fields.</summary>
        private static List<string> AttentionExclusions(List<ExcludedInput> excluded)
        {
            if (excluded == null || excluded.Count == 0) return null;
            var list = excluded.Where(e => !e.HasSurvivingSibling)
                               .Select(e => e.FileName)
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                               .ToList();
            return list.Count > 0 ? list : null;
        }

        private static void LogExclusions(string profileName, List<ExcludedInput> excluded)
        {
            if (excluded == null) return;
            foreach (var e in excluded)
                Core.Log?.Warning(e.HasSurvivingSibling
                    ? $"[profiles] '{profileName}': '{e.FileName}' is the Mono variant - skipped (the IL2CPP one loads)."
                    : $"[profiles] '{profileName}': '{e.FileName}' is a Mono build - left out (this game is IL2CPP).");
        }

        /// <summary>(Re)link the profile's Mods dir and stamp the fingerprint. Runs plain IO - safe on a worker.
        /// Serialized against other rebuilds; reads the profile fresh at the start and persists ONLY the build
        /// fingerprint back onto the current document, so it never clobbers a concurrent edit (removal/install).</summary>
        internal static bool BuildProfile(string profileId)
        {
            lock (_rebuildGate)
            {
                ProfileDef snapshot;
                lock (_storeLock)
                {
                    var doc0 = LoadStore(out _);
                    snapshot = doc0.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
                }
                if (snapshot == null) return false;

                var inputs = ResolveInputs(snapshot, out var missing, out var excluded);
                foreach (var m in missing) Core.Log?.Warning($"[profiles] '{snapshot.Name}': no source for '{m}'.");
                LogExclusions(snapshot.Name, excluded);
                string modsDir = ProfileModsDir(profileId);
                if (!ProfileBuilder.BuildModsDir(modsDir, inputs, s => Core.Log?.Warning("[profiles] " + s))) return false;

                string fingerprint = ProfileBuilder.ComputeFingerprint(inputs);
                lock (_storeLock)
                {
                    var doc = LoadStore(out bool writable);
                    if (!writable) return true;   // built on disk; a read-only store just cannot record the fingerprint
                    var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
                    if (p == null) return false;
                    p.Build = new ProfileBuildInfo
                    {
                        Path = modsDir,
                        Fingerprint = fingerprint,
                        BuiltAt = DateTime.UtcNow.ToString("o"),
                        ExcludedWrongRuntime = AttentionExclusions(excluded),
                    };
                    return SaveStore(doc);
                }
            }
        }

        internal static bool IsBuildCurrent(ProfileDef p)
        {
            if (p?.Build == null || string.IsNullOrEmpty(p.Build.Fingerprint)) return false;
            var inputs = ResolveInputs(p, out var missing);
            if (missing.Count > 0) return false;
            return ProfileBuilder.ComputeFingerprint(inputs) == p.Build.Fingerprint;
        }

        // --- install ---

        /// <summary>
        /// Install a package (plus its dependency closure) into a profile: ensure every closure member sits in
        /// the cache, then pin the refs in the registry. Worker-thread safe; the caller rebuilds/switches after.
        /// Packages that ship Plugins/UserLibs payloads are accepted but those payloads are NOT deployed in v1 -
        /// the limitation is logged and surfaced by the UI (plugins load before the boot picker by design).
        /// </summary>
        internal static async Task<EnsureStatus> InstallPackageAsync(string profileId, string fullName, string version,
            IProgress<ProfileProgress> progress, CancellationToken ct, bool promoteRootToManual = true)
        {
            try
            {
                var index = await ThunderstoreClient.GetIndexAsync(GameRoot, false, ct).ConfigureAwait(false);
                if (index == null) return EnsureStatus.Failed;

                progress?.Report(new ProfileProgress { Phase = "Resolving", CurrentItem = fullName });
                var closure = index.ResolveClosure(new[] { (fullName, version) }, out var unresolved);
                // Essentials (Side Hustle / S1API) are already present in every profile as base refs; a package's
                // dependency on them must never be pinned as a separate thunderstore ref (it would show as a phantom
                // removable row and duplicate the DLL). Drop them from the closure entirely.
                closure.RemoveAll(c => Essentials.IsEssentialPackageName(c.FullName));
                if (unresolved.Count > 0)
                    Core.Log?.Warning("[profiles] unresolved dependencies: " + string.Join(", ", unresolved));
                if (closure.Count == 0) return EnsureStatus.Blocked;

                int done = 0;
                foreach (var (pkgName, pkgVer) in closure)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new ProfileProgress { Phase = "Downloading", CurrentItem = $"{pkgName} {pkgVer}", Done = done, Total = closure.Count });
                    int localDone = done;
                    string dir = await ThunderstoreClient.EnsurePackageAsync(GameRoot, index, pkgName, pkgVer,
                        new SyncProgress<(string Label, long Done, long Total)>(t => progress?.Report(new ProfileProgress
                        { Phase = "Downloading", CurrentItem = t.Label, Done = localDone, Total = closure.Count, BytesDone = t.Done, BytesTotal = t.Total })), ct).ConfigureAwait(false);
                    // EnsurePackageAsync swallows a cancellation into a null return - distinguish it from a failure.
                    if (dir == null) return ct.IsCancellationRequested ? EnsureStatus.Cancelled : EnsureStatus.Failed;
                    var mf = PackageCache.ReadManifest(dir);
                    if (mf != null && (mf.Plugins.Count > 0 || mf.UserLibs.Count > 0))
                        Core.Log?.Warning($"[profiles] '{pkgName}' ships Plugins/UserLibs payloads - those load globally, not per-profile (v1 limitation).");
                    done++;
                }

                bool ok = Transact(doc =>
                {
                    var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
                    if (p == null) return false;

                    foreach (var (pkgName, pkgVer) in closure)
                    {
                        // The requested package is a deliberate choice; everything else in the closure came along
                        // as a dependency (apt-style auto-installed marker). Explicitly installing (from the browser)
                        // a package that was pulled in earlier as a dependency promotes it to a deliberate choice -
                        // but re-pinning it via an update must NOT, or its orphan cleanup is lost forever.
                        bool isRoot = string.Equals(pkgName, fullName, StringComparison.OrdinalIgnoreCase);
                        var mf = PackageCache.ReadManifest(PackageCache.PathFor(CacheRoot, pkgName, pkgVer));
                        var existing = p.Mods.FirstOrDefault(m => m.Source == "thunderstore" &&
                            string.Equals(m.FullName, pkgName, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            // The root install pins exactly what the user picked; a dependency only ever UPGRADES an
                            // existing pin (a dep string pins the version current at the dependant's publish time and
                            // must not silently downgrade a newer pin another mod needs).
                            bool takeThis = isRoot || TsIndex.CompareVersions(pkgVer, existing.Version) > 0;
                            if (takeThis) { existing.Version = pkgVer; existing.Files = mf?.Mods; }
                            if (isRoot && promoteRootToManual) existing.AsDependency = false;
                        }
                        else p.Mods.Add(new ProfileModRef { Source = "thunderstore", FullName = pkgName, Version = pkgVer, Files = mf?.Mods, AsDependency = !isRoot });
                        if (mf != null)
                            foreach (var f in mf.Mods) ModMatcher.Confirm(f, pkgName, "install");
                    }
                    p.Modified = DateTime.UtcNow.ToString("o");
                    return true;
                });
                return ok ? EnsureStatus.Ready : EnsureStatus.Failed;
            }
            catch (OperationCanceledException) { return EnsureStatus.Cancelled; }
            catch (Exception e)
            {
                Core.Log?.Warning("[profiles] install failed: " + e.Message);
                return EnsureStatus.Failed;
            }
        }

        /// <summary>
        /// Remove a set of refs from a profile in ONE store transaction (one Modified stamp, one save) - the shape
        /// every removal path (single, cascade, orphan cleanup) goes through. Essentials are skipped defensively.
        /// The files themselves are never deleted; the caller rebuilds the profile dir afterwards.
        /// </summary>
        internal static bool RemoveMods(string profileId, IReadOnlyList<ProfileModRef> refs)
        {
            if (refs == null || refs.Count == 0) return false;
            return Transact(doc =>
            {
                var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
                if (p == null) return false;

                bool changed = false;
                foreach (var r in refs)
                {
                    if (Essentials.IsEssentialRef(r)) continue;
                    var hit = p.Mods.FirstOrDefault(m => DependencyGraph.SameRef(m, r));
                    if (hit != null) { p.Mods.Remove(hit); changed = true; }
                }
                if (!changed) return false;
                p.Modified = DateTime.UtcNow.ToString("o");
                return true;
            });
        }

        /// <summary>Add one base ref (a DLL linked from the real Mods folder) to a profile. Atomic w.r.t. concurrent
        /// worker builds so a background rebuild cannot drop the freshly added mod.</summary>
        internal static bool AddBaseMod(string profileId, string file)
        {
            if (string.IsNullOrEmpty(file)) return false;
            return Transact(doc =>
            {
                var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
                if (p == null) return false;
                p.Mods.Add(new ProfileModRef { Source = "base", File = file });
                p.Modified = DateTime.UtcNow.ToString("o");
                return true;
            });
        }

        /// <summary>Seed a profile with base refs it does not already carry (the essentials on profile creation).
        /// Atomic w.r.t. concurrent worker builds.</summary>
        internal static bool SeedBaseMods(string profileId, IEnumerable<string> files)
        {
            var list = files?.Where(f => !string.IsNullOrEmpty(f)).ToList();
            if (list == null || list.Count == 0) return false;
            return Transact(doc =>
            {
                var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
                if (p == null) return false;
                bool changed = false;
                foreach (var f in list)
                {
                    if (p.Mods.Any(r => string.Equals(r.File, f, StringComparison.OrdinalIgnoreCase))) continue;
                    p.Mods.Add(new ProfileModRef { Source = "base", File = f });
                    changed = true;
                }
                return changed;
            });
        }

#if DEBUG
        /// <summary>Dev.SelfTest only: plant a fake wrong-runtime exclusion set + re-arm the one-time dialog.</summary>
        internal static bool InjectRuntimeExclusionsForTest(string profileId, List<string> files)
        {
            return Transact(doc =>
            {
                var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
                if (p == null) return false;
                p.Build ??= new ProfileBuildInfo();
                p.Build.ExcludedWrongRuntime = files;
                p.RuntimeNoticeKey = null;
                return true;
            });
        }
#endif

        /// <summary>Stable key for a disabled-mods list: 16 hex chars of SHA256 over the sorted lowercase names.
        /// The one-time dialog re-arms exactly when this key changes (a different set of mods got disabled).</summary>
        internal static string RuntimeNoticeKeyFor(IReadOnlyList<string> files)
        {
            if (files == null || files.Count == 0) return "";
            string joined = string.Join("\n", files.Select(f => (f ?? "").ToLowerInvariant()).OrderBy(s => s, StringComparer.Ordinal));
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(joined));
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        /// <summary>Record that the one-time wrong-runtime dialog was shown for this exclusion set.</summary>
        internal static bool MarkRuntimeNoticeShown(string profileId, string key)
        {
            return Transact(doc =>
            {
                var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
                if (p == null || p.RuntimeNoticeKey == key) return false;
                p.RuntimeNoticeKey = key;
                return true;
            });
        }

        /// <summary>Clear the auto-installed marker on the given refs (the "keep them" answer in the orphan-cleanup
        /// offer): a kept dependency counts as deliberately chosen and is never offered again.</summary>
        internal static bool PromoteToManual(string profileId, IReadOnlyList<ProfileModRef> refs)
        {
            if (refs == null || refs.Count == 0) return false;
            return Transact(doc =>
            {
                var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
                if (p == null) return false;

                bool changed = false;
                foreach (var r in refs)
                {
                    var hit = p.Mods.FirstOrDefault(m => DependencyGraph.SameRef(m, r));
                    if (hit != null && hit.AsDependency) { hit.AsDependency = false; changed = true; }
                }
                return changed;
            });
        }

        // --- switch ---

        /// <summary>Switch the running game into a profile - or back to the full mod set - by relaunching. A named
        /// profile boots into its OWN isolated MelonLoader base (its Mods/Plugins/UserLibs, the runtime shared); the
        /// full set is a plain relaunch with no base-dir. Persists the choice + fresh build fingerprint before the
        /// relaunch quits the game. An empty <paramref name="profileId"/> means the full mod set.</summary>
        internal static bool SwitchTo(string profileId, string continueToken = null)
        {
            lock (_storeLock)
            {
                var doc = LoadStore(out bool writable);
                if (!writable) { Core.Log?.Error("[profiles] profiles.json is not writable; cannot switch."); return false; }
                doc.PendingSwitch = null;

                if (string.IsNullOrEmpty(profileId))
                {
                    doc.LastChoice = "";
                    SaveStore(doc);
                    ModSwitcher.RelaunchPlain("switching to the full mod set");
                    return true;
                }

                var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
                if (p == null) { Core.Log?.Error("[profiles] switch: profile '" + profileId + "' not found."); return false; }

                var modInputs = ProfileResolver.Resolve(GameRoot, p, out var missing, out var excluded);
                ProfileResolver.ResolveExtras(GameRoot, p, out var pluginInputs, out var userLibInputs, excluded);
                foreach (var m in missing) Core.Log?.Warning($"[profiles] '{p.Name}': no source for '{m}'.");
                LogExclusions(p.Name, excluded);

                string altPath = ProfileResolver.ProfileBaseDir(GameRoot, p.Id);
                var all = modInputs.Concat(pluginInputs).Concat(userLibInputs).ToList();
                p.Build = new ProfileBuildInfo
                {
                    Path = altPath,
                    Fingerprint = ProfileBuilder.ComputeFingerprint(all),
                    BuiltAt = DateTime.UtcNow.ToString("o"),
                    ExcludedWrongRuntime = AttentionExclusions(excluded),
                };
                doc.LastChoice = p.Id;
                SaveStore(doc);

                ModSwitcher.RelaunchIntoNamedProfile(altPath, modInputs, pluginInputs, userLibInputs,
                    $"switching to profile '{p.Name}'");
                return true;
            }
        }
    }
}
