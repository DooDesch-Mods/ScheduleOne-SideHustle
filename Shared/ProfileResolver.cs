using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SideHustle.Profiles;

namespace SideHustle.Shared
{
    /// <summary>A profile input dropped at resolve time because it is a Mono-branch build (this game is IL2CPP).</summary>
    internal sealed class ExcludedInput
    {
        public string FileName;
        /// <summary>"base" | "thunderstore" | "local" | "plugin"</summary>
        public string Source;
        /// <summary>The owning Thunderstore package ("Owner-Name"), for thunderstore/plugin exclusions.</summary>
        public string PackageFullName;
        /// <summary>True when the SAME ref still contributed at least one loading Mods DLL (dual-variant package:
        /// skipping the Mono flavor is normal). False = the whole ref is disabled and needs the user's attention.</summary>
        public bool HasSurvivingSibling;
    }

    /// <summary>
    /// Resolves a profile's mod refs to concrete files - shared by the mod (engine/UI) and the boot plugin
    /// (rebuild-if-stale before the game starts), so both always agree on what a profile contains. Pure BCL.
    /// Sources: "base" = the real Mods folder (enabled or .disabled), "thunderstore" = the package cache,
    /// "local" = a file living in the profile's own Mods dir. Mono-branch builds never enter a profile (see
    /// <see cref="RuntimeClassifier"/>); what got dropped is reported so the UI can tell the player.
    /// </summary>
    internal static class ProfileResolver
    {
        internal static string ProfilesRoot(string gameRoot) =>
            Path.Combine(gameRoot, "SideHustle_Profiles", "profiles");

        /// <summary>The root of a named profile's isolated MelonLoader base directory (the folder we point
        /// --melonloader.basedir at). Holds this profile's own Mods/, Plugins/, UserLibs/ and a cloned UserData/,
        /// plus a junction to the shared MelonLoader runtime.</summary>
        internal static string ProfileBaseDir(string gameRoot, string profileId) =>
            Path.Combine(ProfilesRoot(gameRoot), Sanitize(profileId));

        internal static string ProfileModsDir(string gameRoot, string profileId) =>
            Path.Combine(ProfileBaseDir(gameRoot, profileId), "Mods");

        internal static string Sanitize(string s) =>
            new string((s ?? "profile").Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '-').ToArray()).ToLowerInvariant();

        // A Mono-branch DLL would only produce an "incompatible" load error on this IL2CPP game, so it never
        // enters a profile. Essentials are exempt: S1API self-manages its dual variants via its own loader.
        private static bool ExcludeAsWrongRuntime(string fileName, string sourcePath) =>
            !Essentials.IsEssentialFile(fileName) &&
            RuntimeClassifier.IsWrongForThisGame(RuntimeClassifier.ClassifyFile(sourcePath));

        // Choose which extracted copy of a same-named DLL to link. Skips proven wrong-runtime copies, then among
        // the rest prefers one NOT living in a "Mono" folder: a same-name dual package ships Mods/Mono/X.dll
        // (net472) next to Mods/IL2CPP/X.dll and both look branch-neutral by namespace, so the folder breaks the
        // tie toward the copy that actually loads on this IL2CPP build. Null if every copy is wrong-runtime.
        private static string PickLoadable(List<string> candidates)
        {
            string fallback = null;
            foreach (var c in candidates)
            {
                if (RuntimeClassifier.IsWrongForThisGame(RuntimeClassifier.ClassifyFile(c))) continue;
                if (!InMonoFolder(c)) return c;   // definitely-not-Mono path wins
                fallback ??= c;                    // remember a loadable-but-Mono-folder copy as a last resort
            }
            return fallback;
        }

        private static bool InMonoFolder(string path) =>
            path != null && ("/" + path.Replace('\\', '/').ToLowerInvariant()).Contains("/mono/");

        internal static List<BuildInput> Resolve(string gameRoot, ProfileDef p, out List<string> missing) =>
            Resolve(gameRoot, p, out missing, out _);

        internal static List<BuildInput> Resolve(string gameRoot, ProfileDef p, out List<string> missing,
            out List<ExcludedInput> excluded)
        {
            var inputs = new List<BuildInput>();
            missing = new List<string>();
            excluded = new List<ExcludedInput>();
            string realMods = Path.Combine(gameRoot, "Mods");
            string cacheRoot = PackageCache.RootFor(gameRoot);
            string profileMods = ProfileModsDir(gameRoot, p.Id);

            // File-name collisions are real (e.g. a package's dependency ships a DLL the profile also links
            // from the real Mods folder): the FIRST claim wins, and "base" refs are resolved first so the
            // player's own installed file always beats a cache copy of the same name.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = (p.Mods ?? new List<ProfileModRef>()).OrderBy(m => m.Source == "base" ? 0 : 1).ToList();
            void Claim(string fileName, string sourcePath)
            {
                if (seen.Add(fileName)) inputs.Add(new BuildInput { FileName = fileName, SourcePath = sourcePath });
            }

            foreach (var m in ordered)
            {
                switch (m.Source)
                {
                    case "base":
                    {
                        string src = Path.Combine(realMods, m.File ?? "");
                        string found = File.Exists(src) ? src : File.Exists(src + ".disabled") ? src + ".disabled" : null;
                        if (found == null) { missing.Add(m.File ?? "?"); break; }
                        if (ExcludeAsWrongRuntime(m.File, found))
                            excluded.Add(new ExcludedInput { FileName = m.File, Source = "base" });
                        else Claim(m.File, found);
                        break;
                    }
                    case "thunderstore":
                    {
                        string pkgDir = PackageCache.PathFor(cacheRoot, m.FullName, m.Version);
                        var manifest = PackageCache.ReadManifest(pkgDir);
                        if (manifest == null) { missing.Add(m.FullName + " " + m.Version); break; }
                        // Dual-branch packages can even ship the SAME dll name in Mods/Mono/ and Mods/IL2CPP/
                        // subfolders - classify every occurrence and take one that loads here.
                        bool claimedAny = false;
                        var dropped = new List<ExcludedInput>();
                        foreach (var f in m.Files ?? manifest.Mods)
                        {
                            var candidates = PackageCache.FindExtractedFileAll(pkgDir, f);
                            if (candidates.Count == 0) { missing.Add(f); continue; }
                            string src = Essentials.IsEssentialFile(f) ? candidates[0] : PickLoadable(candidates);
                            if (src == null)
                            {
                                // A real exclusion only if no other ref already provides this file name (a same-name
                                // base/local copy that loaded means nothing was actually disabled).
                                if (!seen.Contains(f))
                                    dropped.Add(new ExcludedInput { FileName = f, Source = "thunderstore", PackageFullName = m.FullName });
                                continue;
                            }
                            Claim(f, src);
                            claimedAny = true;
                        }
                        foreach (var d in dropped) { d.HasSurvivingSibling = claimedAny; excluded.Add(d); }
                        break;
                    }
                    case "local":
                    {
                        string src = Path.Combine(profileMods, m.File ?? "");
                        if (!File.Exists(src)) { missing.Add(m.File ?? "?"); break; }
                        if (ExcludeAsWrongRuntime(m.File, src))
                            excluded.Add(new ExcludedInput { FileName = m.File, Source = "local" });
                        else Claim(m.File, src);
                        break;
                    }
                }
            }
            return inputs;
        }

        /// <summary>
        /// The Plugins and UserLibs a profile's Thunderstore packages contribute (classified in each package's cache
        /// manifest). These go into the profile's OWN isolated Plugins/ and UserLibs/ folders - never the global
        /// ones - so a package that ships a plugin or a shared library works per-profile without touching the folders
        /// a mod manager owns. "base"/"local" refs are single Mods DLLs and contribute nothing here.
        /// </summary>
        internal static void ResolveExtras(string gameRoot, ProfileDef p,
            out List<BuildInput> plugins, out List<BuildInput> userLibs, List<ExcludedInput> excluded = null)
        {
            plugins = new List<BuildInput>();
            userLibs = new List<BuildInput>();
            string cacheRoot = PackageCache.RootFor(gameRoot);
            var seenP = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenU = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var m in (p.Mods ?? new List<ProfileModRef>()).Where(m => m.Source == "thunderstore"))
            {
                string pkgDir = PackageCache.PathFor(cacheRoot, m.FullName, m.Version);
                var manifest = PackageCache.ReadManifest(pkgDir);
                if (manifest == null) continue;
                foreach (var f in manifest.Plugins ?? new List<string>())
                {
                    var candidates = PackageCache.FindExtractedFileAll(pkgDir, f);
                    if (candidates.Count == 0) continue;
                    string src = Essentials.IsEssentialFile(f)
                        ? candidates[0]
                        : candidates.FirstOrDefault(c => !RuntimeClassifier.IsWrongForThisGame(RuntimeClassifier.ClassifyFile(c)));
                    if (src == null)
                    {
                        // Plugins never gate the whole ref - a skipped Mono plugin flavor is routine.
                        excluded?.Add(new ExcludedInput { FileName = f, Source = "plugin", PackageFullName = m.FullName, HasSurvivingSibling = true });
                        continue;
                    }
                    if (seenP.Add(f)) plugins.Add(new BuildInput { FileName = f, SourcePath = src });
                }
                foreach (var f in manifest.UserLibs ?? new List<string>())
                {
                    string src = PackageCache.FindExtractedFile(pkgDir, f);
                    if (src != null && seenU.Add(f)) userLibs.Add(new BuildInput { FileName = f, SourcePath = src });
                }
            }
        }
    }
}
