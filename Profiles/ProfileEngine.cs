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
    internal enum EnsureStatus { Ready, Failed, Blocked }

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

        internal static ProfilesFile LoadStore(out bool writable) => ProfileStore.Load(StorePath, out writable);

        internal static bool SaveStore(ProfilesFile doc) => ProfileStore.Save(StorePath, doc);

        internal static ProfileDef CreateProfile(string name)
        {
            var doc = LoadStore(out bool writable);
            if (!writable) return null;
            string baseId = ProfileResolver.Sanitize(string.IsNullOrWhiteSpace(name) ? "profile" : name.Trim());
            string id = baseId;
            for (int i = 2; doc.Profiles.Any(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); i++)
                id = baseId + "-" + i;
            var def = new ProfileDef
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? id : name.Trim(),
                Created = DateTime.UtcNow.ToString("o"),
                Modified = DateTime.UtcNow.ToString("o"),
            };
            doc.Profiles.Add(def);
            return SaveStore(doc) ? def : null;
        }

        /// <summary>Change a profile's display name only. The Id (folder + registry key) stays stable, so nothing
        /// has to be rebuilt or re-linked. Returns false if the store is read-only or the profile is gone.</summary>
        internal static bool RenameProfile(string profileId, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) return false;
            var doc = LoadStore(out bool writable);
            if (!writable) return false;
            var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
            if (p == null) return false;
            p.Name = newName.Trim();
            p.Modified = DateTime.UtcNow.ToString("o");
            return SaveStore(doc);
        }

        /// <summary>Set a profile's free-text description (the notes shown in the list/detail). An empty value clears
        /// it. Returns false if the store is read-only or the profile is gone.</summary>
        internal static bool SetDescription(string profileId, string notes)
        {
            var doc = LoadStore(out bool writable);
            if (!writable) return false;
            var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
            if (p == null) return false;
            p.Notes = (notes ?? "").Trim();
            p.Modified = DateTime.UtcNow.ToString("o");
            return SaveStore(doc);
        }

        /// <summary>The on-disk folder that holds a profile's (hardlinked) mod set - what "Open folder" reveals.</summary>
        internal static string ProfileFolder(string profileId) => Path.GetDirectoryName(ProfileModsDir(profileId));

        internal static bool DeleteProfile(string profileId)
        {
            var doc = LoadStore(out bool writable);
            if (!writable) return false;
            doc.Profiles.RemoveAll(p => p.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
            if (doc.LastChoice == profileId) doc.LastChoice = "";
            if (doc.Settings.DefaultProfileId == profileId) doc.Settings.DefaultProfileId = "";
            bool ok = SaveStore(doc);
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

        /// <summary>(Re)link the profile's Mods dir and stamp the fingerprint. Runs plain IO - safe on a worker.</summary>
        internal static bool BuildProfile(string profileId)
        {
            var doc = LoadStore(out bool writable);
            var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
            if (p == null) return false;

            var inputs = ResolveInputs(p, out var missing);
            foreach (var m in missing) Core.Log?.Warning($"[profiles] '{p.Name}': no source for '{m}'.");
            string modsDir = ProfileModsDir(p.Id);
            if (!ProfileBuilder.BuildModsDir(modsDir, inputs, s => Core.Log?.Warning("[profiles] " + s))) return false;

            p.Build = new ProfileBuildInfo
            {
                Path = modsDir,
                Fingerprint = ProfileBuilder.ComputeFingerprint(inputs),
                BuiltAt = DateTime.UtcNow.ToString("o"),
            };
            p.Modified = DateTime.UtcNow.ToString("o");
            return !writable || SaveStore(doc);
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
            IProgress<ProfileProgress> progress, CancellationToken ct)
        {
            try
            {
                var index = await ThunderstoreClient.GetIndexAsync(GameRoot, false, ct).ConfigureAwait(false);
                if (index == null) return EnsureStatus.Failed;

                progress?.Report(new ProfileProgress { Phase = "Resolving", CurrentItem = fullName });
                var closure = index.ResolveClosure(new[] { (fullName, version) }, out var unresolved);
                if (unresolved.Count > 0)
                    Core.Log?.Warning("[profiles] unresolved dependencies: " + string.Join(", ", unresolved));
                if (closure.Count == 0) return EnsureStatus.Blocked;

                int done = 0;
                foreach (var (pkgName, pkgVer) in closure)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new ProfileProgress { Phase = "Downloading", CurrentItem = $"{pkgName} {pkgVer}", Done = done, Total = closure.Count });
                    string dir = await ThunderstoreClient.EnsurePackageAsync(GameRoot, index, pkgName, pkgVer,
                        new Progress<(string Label, long Done, long Total)>(t => progress?.Report(new ProfileProgress
                        { Phase = "Downloading", CurrentItem = t.Label, Done = done, Total = closure.Count, BytesDone = t.Done, BytesTotal = t.Total })), ct).ConfigureAwait(false);
                    if (dir == null) return EnsureStatus.Failed;
                    var mf = PackageCache.ReadManifest(dir);
                    if (mf != null && (mf.Plugins.Count > 0 || mf.UserLibs.Count > 0))
                        Core.Log?.Warning($"[profiles] '{pkgName}' ships Plugins/UserLibs payloads - those load globally, not per-profile (v1 limitation).");
                    done++;
                }

                var doc = LoadStore(out bool writable);
                if (!writable) return EnsureStatus.Failed;
                var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
                if (p == null) return EnsureStatus.Failed;

                foreach (var (pkgName, pkgVer) in closure)
                {
                    var mf = PackageCache.ReadManifest(PackageCache.PathFor(CacheRoot, pkgName, pkgVer));
                    var existing = p.Mods.FirstOrDefault(m => m.Source == "thunderstore" &&
                        string.Equals(m.FullName, pkgName, StringComparison.OrdinalIgnoreCase));
                    if (existing != null) { existing.Version = pkgVer; existing.Files = mf?.Mods; }
                    else p.Mods.Add(new ProfileModRef { Source = "thunderstore", FullName = pkgName, Version = pkgVer, Files = mf?.Mods });
                    if (mf != null)
                        foreach (var f in mf.Mods) ModMatcher.Confirm(f, pkgName, "install");
                }
                p.Modified = DateTime.UtcNow.ToString("o");
                return SaveStore(doc) ? EnsureStatus.Ready : EnsureStatus.Failed;
            }
            catch (OperationCanceledException) { return EnsureStatus.Failed; }
            catch (Exception e)
            {
                Core.Log?.Warning("[profiles] install failed: " + e.Message);
                return EnsureStatus.Failed;
            }
        }

        // --- switch ---

        /// <summary>Switch the running game into a profile - or back to the full mod set - by relaunching. A named
        /// profile boots into its OWN isolated MelonLoader base (its Mods/Plugins/UserLibs, the runtime shared); the
        /// full set is a plain relaunch with no base-dir. Persists the choice + fresh build fingerprint before the
        /// relaunch quits the game. An empty <paramref name="profileId"/> means the full mod set.</summary>
        internal static bool SwitchTo(string profileId, string continueToken = null)
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

            var modInputs = ProfileResolver.Resolve(GameRoot, p, out var missing);
            ProfileResolver.ResolveExtras(GameRoot, p, out var pluginInputs, out var userLibInputs);
            foreach (var m in missing) Core.Log?.Warning($"[profiles] '{p.Name}': no source for '{m}'.");

            string altPath = ProfileResolver.ProfileBaseDir(GameRoot, p.Id);
            var all = modInputs.Concat(pluginInputs).Concat(userLibInputs).ToList();
            p.Build = new ProfileBuildInfo
            {
                Path = altPath,
                Fingerprint = ProfileBuilder.ComputeFingerprint(all),
                BuiltAt = DateTime.UtcNow.ToString("o"),
            };
            doc.LastChoice = p.Id;
            SaveStore(doc);

            ModSwitcher.RelaunchIntoNamedProfile(altPath, modInputs, pluginInputs, userLibInputs,
                $"switching to profile '{p.Name}'");
            return true;
        }
    }
}
