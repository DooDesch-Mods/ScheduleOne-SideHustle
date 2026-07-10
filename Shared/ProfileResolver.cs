using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SideHustle.Profiles;

namespace SideHustle.Shared
{
    /// <summary>
    /// Resolves a profile's mod refs to concrete files - shared by the mod (engine/UI) and the boot plugin
    /// (rebuild-if-stale before the game starts), so both always agree on what a profile contains. Pure BCL.
    /// Sources: "base" = the real Mods folder (enabled or .disabled), "thunderstore" = the package cache,
    /// "local" = a file living in the profile's own Mods dir.
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

        // Community packages often ship BOTH loader backends (e.g. S1API.Mono.MelonLoader.dll next to
        // S1API.Il2Cpp.MelonLoader.dll). This game is IL2CPP - the Mono flavor would only produce an
        // "incompatible" load warning, so it never enters a profile.
        private static bool IsWrongBackend(string fileName) =>
            fileName != null && fileName.IndexOf(".Mono.", StringComparison.OrdinalIgnoreCase) >= 0;

        internal static List<BuildInput> Resolve(string gameRoot, ProfileDef p, out List<string> missing)
        {
            var inputs = new List<BuildInput>();
            missing = new List<string>();
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
                        if (File.Exists(src)) Claim(m.File, src);
                        else if (File.Exists(src + ".disabled")) Claim(m.File, src + ".disabled");
                        else missing.Add(m.File ?? "?");
                        break;
                    }
                    case "thunderstore":
                    {
                        string pkgDir = PackageCache.PathFor(cacheRoot, m.FullName, m.Version);
                        var manifest = PackageCache.ReadManifest(pkgDir);
                        if (manifest == null) { missing.Add(m.FullName + " " + m.Version); break; }
                        foreach (var f in m.Files ?? manifest.Mods)
                        {
                            if (IsWrongBackend(f)) continue;   // dual-backend packages: skip the Mono flavor
                            string src = PackageCache.FindExtractedFile(pkgDir, f);
                            if (src != null) Claim(f, src);
                            else missing.Add(f);
                        }
                        break;
                    }
                    case "local":
                    {
                        string src = Path.Combine(profileMods, m.File ?? "");
                        if (File.Exists(src)) Claim(m.File, src);
                        else missing.Add(m.File ?? "?");
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
            out List<BuildInput> plugins, out List<BuildInput> userLibs)
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
                    if (IsWrongBackend(f)) continue;
                    string src = PackageCache.FindExtractedFile(pkgDir, f);
                    if (src != null && seenP.Add(f)) plugins.Add(new BuildInput { FileName = f, SourcePath = src });
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
