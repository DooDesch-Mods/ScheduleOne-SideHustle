using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SideHustle.Mods
{
    /// <summary>
    /// Builds and tears down an alternate MelonLoader base directory so a gamemode can run with ONLY its allowed
    /// mods - without ever touching the player's real Mods folder. The alt base is a sibling directory on the same
    /// volume that holds directory junctions back to the real MelonLoader/Plugins/UserLibs/UserData and a real
    /// Mods/ with hardlinks to only the allowed mod DLLs. Relaunching the game with --melonloader.basedir=&lt;alt&gt;
    /// then loads exactly that curated set. Because nothing in the managed Mods/ is renamed, moved or deleted, mod
    /// managers (r2modman / Thunderstore) keep a truthful picture and clean install/uninstall keep working.
    ///
    /// Hardlinks need the same volume as the real Mods (no admin on NTFS); junctions can point anywhere (no admin).
    /// A plain Steam launch (no arg) always loads the real full set, so the player can never get stuck.
    /// </summary>
    internal static class AltBase
    {
        // Folders that --melonloader.basedir relocates and that must therefore exist under the alt base. They are
        // junctioned straight back to the real install so the loader runtime, plugins and user libraries are shared.
        // UserData is NOT junctioned anymore: a profile gets its own UserData via CloneUserData (subdirectories
        // junctioned - including the generated-assembly cache, whose absence is what hangs a FRESH UserData at
        // startup - and top-level files copied), so a profile can carry its own MelonPreferences.cfg. Callers of
        // Build MUST follow up with CloneUserData before relaunching.
        private static readonly string[] JunctionDirs = { "MelonLoader", "Plugins", "UserLibs" };

        /// <summary>The real game root (parent of the game's data folder); always the real install, never the alt base.</summary>
        internal static string GameRoot() => ModInventory.GameRoot();

        /// <summary>The real install's Mods folder (the hardlink source), independent of the current base directory.</summary>
        internal static string RealModsDir()
        {
            var r = GameRoot();
            return r == null ? null : Path.Combine(r, "Mods");
        }

        /// <summary>The directory that holds everything profile-related, inside the game folder so the player can
        /// find and delete it easily, e.g. "&lt;game&gt;\SideHustle_Profiles". (Same volume as Mods, so hardlinks work.)
        /// Subdirectories with distinct lifecycles: session\ (temporary per-gamemode/sync profiles, swept on normal
        /// launches), profiles\ (named persistent profiles, never swept) and cache\ (downloaded packages).</summary>
        internal static string BasesRoot()
        {
            var root = GameRoot();
            return root == null ? null : Path.Combine(root, "SideHustle_Profiles");
        }

        /// <summary>The parent of all TEMPORARY session profiles (gamemode policy + lobby sync) - the only
        /// subtree SweepStale touches.</summary>
        internal static string SessionRoot()
        {
            var bases = BasesRoot();
            return bases == null ? null : Path.Combine(bases, "session");
        }

        /// <summary>A FRESH alt base path for a gamemode id (sanitized folder name + a unique nonce). A new nonce
        /// every build guarantees a locked, un-deletable stale profile (a hardlink to a currently-loaded mod DLL
        /// cannot be removed by ANY of its names) can never block a new "Required only" host. Old profiles are cheap
        /// - hardlinks share the real file's bytes and junctions are reparse points - and get swept on a later normal
        /// launch when their mods are not loaded. The exact path is persisted (Preferences.ActiveAltBase + the
        /// relaunch .bat), so nothing relies on this being recomputable.</summary>
        internal static string ComputeAltPath(string gamemodeId)
        {
            var bases = SessionRoot();
            if (bases == null) return null;
            var safe = new string((gamemodeId ?? "gamemode")
                .Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_').ToArray());
            if (safe.Length == 0) safe = "gamemode";
            string nonce = Guid.NewGuid().ToString("N").Substring(0, 8);
            return Path.Combine(bases, safe + "-" + nonce);
        }

        /// <summary>The base directory the CURRENT process actually loaded from (alt or real).</summary>
        internal static string CurrentBase()
        {
            try { return MelonLoader.Utils.MelonEnvironment.MelonBaseDirectory; }
            catch { return GameRoot(); }
        }

        /// <summary>True when the running process booted into a NAMED profile's isolated base (under profiles\),
        /// as opposed to a temporary gamemode/sync policy base (under session\). Distinguished by path so the two
        /// kinds of alt-base session get the right handling at menu load.</summary>
        internal static bool IsNamedProfileSession()
        {
            try
            {
                if (!IsAltSession()) return false;
                string cur = CurrentBase();
                string profilesRoot = Shared.ProfileResolver.ProfilesRoot(GameRoot());
                if (string.IsNullOrEmpty(cur) || string.IsNullOrEmpty(profilesRoot)) return false;
                string p = Path.GetFullPath(cur).TrimEnd('\\', '/');
                string pre = Path.GetFullPath(profilesRoot).TrimEnd('\\', '/');
                return p.StartsWith(pre + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                       || p.Equals(pre, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>True when the running process was launched into an alt base (a policy session is live).</summary>
        internal static bool IsAltSession()
        {
            try
            {
                string cur = CurrentBase();
                string real = GameRoot();
                if (string.IsNullOrEmpty(cur) || string.IsNullOrEmpty(real)) return false;
                return !PathsEqual(cur, real);
            }
            catch { return false; }
        }

        /// <summary>
        /// Build the alt base for <paramref name="altPath"/> containing only <paramref name="keepFiles"/> (DLL names).
        /// Returns true on success. Safe to call while the game is running (hardlinks/junctions don't lock the targets).
        /// </summary>
        /// <summary>The junction-only shell of an alt base (clean slate + Mods dir + runtime junctions), shared
        /// by the name-based gamemode build below and the explicit-source sync build (ModSwitcher).</summary>
        internal static bool BuildSkeleton(string altPath)
        {
            try
            {
                string root = GameRoot();
                if (altPath == null || root == null) return false;

                Teardown(altPath);                       // clean slate (a stale tree is never in use in a normal session)
                Directory.CreateDirectory(altPath);
                Directory.CreateDirectory(Path.Combine(altPath, "Mods"));

                foreach (var d in JunctionDirs)
                {
                    string target = Path.Combine(root, d);
                    if (!Directory.Exists(target)) continue;   // Plugins/UserLibs may be absent on a minimal install
                    string link = Path.Combine(altPath, d);
                    if (!MakeJunction(link, target))
                    {
                        Core.Log?.Error($"[modpolicy] could not junction {d}; aborting alt-base build.");
                        Teardown(altPath);
                        return false;
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                Core.Log?.Error("[modpolicy] alt-base skeleton failed: " + e);
                try { Teardown(altPath); } catch { /* ignore */ }
                return false;
            }
        }

        internal static bool Build(string altPath, IEnumerable<string> keepFiles)
        {
            try
            {
                string realMods = RealModsDir();
                if (realMods == null || !BuildSkeleton(altPath)) return false;

                int linked = 0;
                foreach (var file in keepFiles.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string src = ResolveSource(realMods, file);
                    if (src == null) { Core.Log?.Warning($"[modpolicy] keep '{file}' has no source on disk; skipping."); continue; }
                    string dst = Path.Combine(altPath, "Mods", file);   // always the enabled .dll name (enables a .disabled source)
                    // Already present from a reused profile whose stale hardlink was locked (un-deletable): if it
                    // already matches the source, count it as done rather than failing to re-link/overwrite it.
                    if (File.Exists(dst) && SameSize(dst, src)) { linked++; continue; }
                    if (MakeHardLink(dst, src)) { linked++; continue; }
                    // Fallback to a copy (cross-volume, non-NTFS, or a locked source).
                    try { File.Copy(src, dst, true); linked++; }
                    catch (Exception e) { Core.Log?.Warning($"[modpolicy] could not link or copy '{file}': {e.Message}"); }
                }

                Core.Log?.Msg($"[modpolicy] alt base ready at '{altPath}' with {linked} mod(s).");
                return linked > 0;
            }
            catch (Exception e)
            {
                Core.Log?.Error("[modpolicy] alt-base build failed: " + e);
                try { Teardown(altPath); } catch { /* ignore */ }
                return false;
            }
        }

        /// <summary>The on-disk source for a wanted DLL: the enabled file, or the .disabled file (which we then enable).
        /// Falls back to the CURRENT session's Mods folder when that differs from the real one - a gamemode launched
        /// from inside another profile may want a DLL that exists only there (e.g. installed from the package cache).</summary>
        private static string ResolveSource(string realMods, string file)
        {
            string enabled = Path.Combine(realMods, file);
            if (File.Exists(enabled)) return enabled;
            string disabled = enabled + ".disabled";
            if (File.Exists(disabled)) return disabled;
            string curMods = ModInventory.ModsPath();
            if (curMods != null && !PathsEqual(curMods, realMods))
            {
                string cur = Path.Combine(curMods, file);
                if (File.Exists(cur)) return cur;
                if (File.Exists(cur + ".disabled")) return cur + ".disabled";
            }
            return null;
        }

        /// <summary>
        /// Build a NAMED profile's FULLY ISOLATED base directory: only the shared MelonLoader runtime is junctioned
        /// (never duplicated), while Mods/, Plugins/ and UserLibs/ are the profile's OWN real folders. Plugins/ and
        /// UserLibs/ are seeded from the global install (hardlinks, so everything that loaded globally still loads)
        /// and then the profile's Thunderstore packages add their own - all WITHOUT ever writing into the global
        /// folders a mod manager owns. Callers follow up with CloneUserData before relaunching.
        /// </summary>
        internal static bool BuildIsolatedBase(string altPath, IReadOnlyList<Shared.BuildInput> modInputs,
            IReadOnlyList<Shared.BuildInput> pluginInputs, IReadOnlyList<Shared.BuildInput> userLibInputs)
        {
            try
            {
                string root = GameRoot();
                if (altPath == null || root == null) return false;

                Teardown(altPath);                       // clean slate (a named profile is never in use in a normal session)
                Directory.CreateDirectory(altPath);

                string mlTarget = Path.Combine(root, "MelonLoader");
                if (Directory.Exists(mlTarget) && !MakeJunction(Path.Combine(altPath, "MelonLoader"), mlTarget))
                {
                    Core.Log?.Error("[profiles] could not junction the MelonLoader runtime; aborting isolated build.");
                    Teardown(altPath);
                    return false;
                }

                bool ok = Shared.ProfileBuilder.BuildModsDir(Path.Combine(altPath, "Mods"), modInputs,
                    s => Core.Log?.Warning("[profiles] " + s));
                ok &= BuildSeededFolder(Path.Combine(altPath, "Plugins"), Path.Combine(root, "Plugins"), pluginInputs);
                ok &= BuildSeededFolder(Path.Combine(altPath, "UserLibs"), Path.Combine(root, "UserLibs"), userLibInputs);
                if (ok) Core.Log?.Msg($"[profiles] isolated base ready at '{altPath}'.");
                return ok;
            }
            catch (Exception e)
            {
                Core.Log?.Error("[profiles] isolated base build failed: " + e);
                try { Teardown(altPath); } catch { /* ignore */ }
                return false;
            }
        }

        // A profile Plugins/UserLibs folder = every DLL from the matching global folder (seeded, so nothing that
        // loaded globally breaks) PLUS the profile's own package files, all hardlinked. The profile's own files win a
        // name clash over the global seed. Reuses the generic mods-dir builder.
        private static bool BuildSeededFolder(string destDir, string globalDir, IReadOnlyList<Shared.BuildInput> extras)
        {
            var inputs = new List<Shared.BuildInput>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in extras ?? (IReadOnlyList<Shared.BuildInput>)Array.Empty<Shared.BuildInput>())
                if (e?.FileName != null && seen.Add(e.FileName)) inputs.Add(e);
            if (Directory.Exists(globalDir))
                foreach (var f in Directory.GetFiles(globalDir, "*.dll"))
                {
                    string name = Path.GetFileName(f);
                    if (seen.Add(name)) inputs.Add(new Shared.BuildInput { FileName = name, SourcePath = f });
                }
            return Shared.ProfileBuilder.BuildModsDir(destDir, inputs, s => Core.Log?.Warning("[profiles] " + s));
        }

        /// <summary>
        /// Give the alt base its OWN UserData: every subdirectory of the real UserData is junctioned in (shared -
        /// including the generated-assembly cache, whose absence is what hangs a fresh UserData at startup), every
        /// top-level FILE is copied, and MelonPreferences.cfg is written through <paramref name="prefsTransform"/>
        /// (session tokens + host-synced categories). The real cfg is never touched; a missing real cfg still
        /// produces a transformed clone (first-run installs). Returns false when any junction or the transform
        /// fails - the caller must abort and tear the alt base down.
        /// </summary>
        internal static bool CloneUserData(string altPath, Func<string, string> prefsTransform)
        {
            const string CfgName = "MelonPreferences.cfg";
            try
            {
                string root = GameRoot();
                if (altPath == null || root == null) return false;
                string realUserData = Path.Combine(root, "UserData");
                string clone = Path.Combine(altPath, "UserData");
                Directory.CreateDirectory(clone);

                bool cfgSeen = false;
                if (Directory.Exists(realUserData))
                {
                    foreach (var sub in Directory.GetDirectories(realUserData))
                    {
                        if (IsReparsePoint(sub)) continue;   // never junction a junction (would chain reparse points)
                        string link = Path.Combine(clone, Path.GetFileName(sub));
                        if (!MakeJunction(link, sub))
                        {
                            Core.Log?.Error($"[modpolicy] could not junction UserData\\{Path.GetFileName(sub)}; aborting clone.");
                            return false;
                        }
                    }
                    foreach (var f in Directory.GetFiles(realUserData))
                    {
                        string name = Path.GetFileName(f);
                        string dst = Path.Combine(clone, name);
                        if (string.Equals(name, CfgName, StringComparison.OrdinalIgnoreCase) && prefsTransform != null)
                        {
                            File.WriteAllText(dst, prefsTransform(File.ReadAllText(f)));
                            cfgSeen = true;
                        }
                        else
                        {
                            File.Copy(f, dst, true);
                        }
                    }
                }
                if (!cfgSeen && prefsTransform != null)
                    File.WriteAllText(Path.Combine(clone, CfgName), prefsTransform(""));
                return true;
            }
            catch (Exception e)
            {
                Core.Log?.Error("[modpolicy] UserData clone failed: " + e);
                return false;
            }
        }

        /// <summary>
        /// Safely remove an alt base. Junctions are removed link-only via `rmdir` so the REAL targets are never
        /// touched; only once every reparse point is gone do we recursively delete the remaining real content
        /// (the Mods hardlinks - deleting a hardlink never removes the original).
        /// </summary>
        internal static void Teardown(string altPath)
        {
            try
            {
                if (string.IsNullOrEmpty(altPath) || !Directory.Exists(altPath)) return;

                foreach (var dir in Directory.GetDirectories(altPath, "*", SearchOption.AllDirectories)
                                             .Concat(new[] { altPath })
                                             .Where(IsReparsePoint)
                                             .OrderByDescending(p => p.Length))   // deepest first
                {
                    RunCmd($"rmdir \"{dir}\"");
                }

                // Refuse a recursive delete if any reparse point survived (would risk following it into a real target).
                if (Directory.GetDirectories(altPath, "*", SearchOption.AllDirectories).Any(IsReparsePoint))
                {
                    Core.Log?.Error($"[modpolicy] alt base '{altPath}' still has junctions; NOT deleting recursively.");
                    return;
                }

                // Delete the remaining real content (the Mods hardlinks) ONE BY ONE, tolerating locked files: a
                // hardlink to a currently-loaded mod DLL is un-deletable, and an all-or-nothing Directory.Delete(true)
                // would throw and leave the whole profile behind (the old "Access denied" failure). Deleting a
                // hardlink never removes the original file. Anything we cannot delete now is left in place (it costs
                // ~nothing - it shares bytes with the real mod) and is swept on a later launch when it is not loaded.
                bool allGone = true;
                foreach (var f in Directory.GetFiles(altPath, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
                    catch { allGone = false; }   // locked (loaded DLL) - leave it, sweep later
                }
                foreach (var dir in Directory.GetDirectories(altPath, "*", SearchOption.AllDirectories)
                                             .OrderByDescending(p => p.Length))   // deepest first
                { try { Directory.Delete(dir, false); } catch { allGone = false; } }
                if (allGone) { try { Directory.Delete(altPath, false); } catch { /* a race left something; the next sweep gets it */ } }
            }
            catch (Exception e) { Core.Log?.Warning("[modpolicy] alt-base teardown: " + e.Message); }
        }

        private static bool SameSize(string a, string b)
        {
            try { return new FileInfo(a).Length == new FileInfo(b).Length; }
            catch { return false; }
        }

        /// <summary>Remove every leftover SESSION profile (call from a normal session). Named profiles (profiles\)
        /// and the package cache (cache\) have their own lifecycles and are never swept. Pre-2.0 session profiles
        /// lived directly under the root as "&lt;id&gt;-&lt;8-hex-nonce&gt;" - those are swept too as migration; anything
        /// else at the top level is left alone. Optionally keep one path.</summary>
        internal static void SweepStale(string keep = null)
        {
            try
            {
                string bases = BasesRoot();
                if (bases == null || !Directory.Exists(bases)) return;
                foreach (var d in Directory.GetDirectories(bases))
                {
                    string name = Path.GetFileName(d);
                    if (string.Equals(name, "session", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var s in Directory.GetDirectories(d))
                        {
                            if (keep != null && PathsEqual(s, keep)) continue;
                            Teardown(s);
                        }
                        try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { /* ignore */ }
                        continue;
                    }
                    // Legacy top-level session profile (pre-2.0 layout): "<id>-<8-hex-nonce>".
                    if (!System.Text.RegularExpressions.Regex.IsMatch(name, "-[0-9a-fA-F]{8}$")) continue;
                    if (keep != null && PathsEqual(d, keep)) continue;
                    Teardown(d);
                }
                try { if (!Directory.EnumerateFileSystemEntries(bases).Any()) Directory.Delete(bases); } catch { /* ignore */ }
            }
            catch (Exception e) { Core.Log?.Warning("[modpolicy] sweep: " + e.Message); }
        }

        /// <summary>
        /// True when the CURRENT alt-profile session's mods no longer match the real install - e.g. the player updated
        /// a mod (a new beta) AFTER this profile was built, so its hardlinks/copies are out of date (a replaced DLL
        /// orphans the old hardlink). Compares every DLL in the profile's Mods to its real-install source by size, then
        /// content on a size tie; a source that is now gone also counts as stale. Cheap (a handful of DLLs) and only
        /// meaningful inside a profile session. When true, the caller should restore the full set so a fresh launch
        /// rebuilds the profile from the up-to-date mods.
        /// </summary>
        internal static bool ProfileIsStale()
        {
            try
            {
                if (!IsAltSession()) return false;
                string profMods = Path.Combine(CurrentBase() ?? "", "Mods");
                string realMods = RealModsDir();
                if (!Directory.Exists(profMods) || realMods == null || !Directory.Exists(realMods)) return false;

                foreach (var f in Directory.GetFiles(profMods, "*.dll"))
                {
                    string name = Path.GetFileName(f);
                    string src = ResolveSource(realMods, name);
                    if (src == null) return true;                                  // a mod removed from the real install
                    // Resolved to itself: no external source to be stale against (a cache-backed mod that only
                    // exists in this profile - its freshness is the profile registry's fingerprint job, not ours).
                    if (PathsEqual(src, f)) continue;
                    if (new FileInfo(f).Length != new FileInfo(src).Length) return true;   // a different build (updated mod)
                    if (!ContentEquals(f, src)) return true;                       // same size, different content
                }
                return false;
            }
            catch { return false; }
        }

        private static bool ContentEquals(string a, string b)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] ha, hb;
                    using (var s = File.OpenRead(a)) ha = sha.ComputeHash(s);
                    using (var s = File.OpenRead(b)) hb = sha.ComputeHash(s);
                    return ha.AsSpan().SequenceEqual(hb);
                }
            }
            catch { return false; }   // unreadable -> treat as changed (safer to rebuild)
        }

        // --- win32 link helpers (mklink is a cmd builtin; no admin needed for /J junctions or /H hardlinks) ---

        private static bool MakeJunction(string link, string target) => RunCmd($"mklink /J \"{link}\" \"{target}\"") == 0 && Directory.Exists(link);
        private static bool MakeHardLink(string link, string target) => RunCmd($"mklink /H \"{link}\" \"{target}\"") == 0 && File.Exists(link);

        private static bool IsReparsePoint(string path)
        {
            try { return (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0; }
            catch { return false; }
        }

        private static int RunCmd(string command)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c " + command)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return -1;
                    p.WaitForExit(15000);
                    return p.HasExited ? p.ExitCode : -1;
                }
            }
            catch { return -1; }
        }

        private static bool PathsEqual(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }
    }
}
