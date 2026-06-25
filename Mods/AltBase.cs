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
        // junctioned straight back to the real install so the loader runtime, plugins, user libraries and user data
        // (preferences, generated-assembly cache hash, etc.) are shared - a FRESH UserData hangs the game at startup.
        private static readonly string[] JunctionDirs = { "MelonLoader", "Plugins", "UserLibs", "UserData" };

        /// <summary>The real game root (parent of the game's data folder); always the real install, never the alt base.</summary>
        internal static string GameRoot() => ModInventory.GameRoot();

        /// <summary>The real install's Mods folder (the hardlink source), independent of the current base directory.</summary>
        internal static string RealModsDir()
        {
            var r = GameRoot();
            return r == null ? null : Path.Combine(r, "Mods");
        }

        /// <summary>The directory that holds all per-gamemode profiles, inside the game folder so the player can
        /// find and delete it easily, e.g. "&lt;game&gt;\SideHustle_Profiles". (Same volume as Mods, so hardlinks work.)</summary>
        internal static string BasesRoot()
        {
            var root = GameRoot();
            return root == null ? null : Path.Combine(root, "SideHustle_Profiles");
        }

        /// <summary>The alt base path for a gamemode id (sanitized to a safe, no-space folder name).</summary>
        internal static string ComputeAltPath(string gamemodeId)
        {
            var bases = BasesRoot();
            if (bases == null) return null;
            var safe = new string((gamemodeId ?? "gamemode")
                .Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_').ToArray());
            if (safe.Length == 0) safe = "gamemode";
            return Path.Combine(bases, safe);
        }

        /// <summary>The base directory the CURRENT process actually loaded from (alt or real).</summary>
        internal static string CurrentBase()
        {
            try { return MelonLoader.Utils.MelonEnvironment.MelonBaseDirectory; }
            catch { return GameRoot(); }
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
        internal static bool Build(string altPath, IEnumerable<string> keepFiles)
        {
            try
            {
                string root = GameRoot();
                string realMods = RealModsDir();
                if (altPath == null || root == null || realMods == null) return false;

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

                int linked = 0;
                foreach (var file in keepFiles.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string src = ResolveSource(realMods, file);
                    if (src == null) { Core.Log?.Warning($"[modpolicy] keep '{file}' has no source on disk; skipping."); continue; }
                    string dst = Path.Combine(altPath, "Mods", file);   // always the enabled .dll name (enables a .disabled source)
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

        /// <summary>The on-disk source for a wanted DLL: the enabled file, or the .disabled file (which we then enable).</summary>
        private static string ResolveSource(string realMods, string file)
        {
            string enabled = Path.Combine(realMods, file);
            if (File.Exists(enabled)) return enabled;
            string disabled = enabled + ".disabled";
            if (File.Exists(disabled)) return disabled;
            return null;
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
                Directory.Delete(altPath, true);
            }
            catch (Exception e) { Core.Log?.Warning("[modpolicy] alt-base teardown: " + e.Message); }
        }

        /// <summary>Remove every leftover alt base (call from a normal session). Optionally keep one path.</summary>
        internal static void SweepStale(string keep = null)
        {
            try
            {
                string bases = BasesRoot();
                if (bases == null || !Directory.Exists(bases)) return;
                foreach (var d in Directory.GetDirectories(bases))
                {
                    if (keep != null && PathsEqual(d, keep)) continue;
                    Teardown(d);
                }
                try { if (!Directory.EnumerateFileSystemEntries(bases).Any()) Directory.Delete(bases); } catch { /* ignore */ }
            }
            catch (Exception e) { Core.Log?.Warning("[modpolicy] sweep: " + e.Message); }
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
