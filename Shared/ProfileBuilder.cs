using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SideHustle.Shared
{
    /// <summary>One file a profile build links in: the DLL name it appears under and where its bytes live.</summary>
    internal sealed class BuildInput
    {
        public string FileName;     // e.g. "Siesta.dll" (always the enabled name)
        public string SourcePath;   // real Mods file, package-cache file, or the profile's own local file
    }

    /// <summary>
    /// Builds a NAMED profile's Mods directory from resolved inputs (hardlinks, copy fallback) and computes the
    /// fingerprint that ties a build to its exact input bytes. Compiled into BOTH assemblies - the mod (manager,
    /// engine) and the boot plugin (rebuild-if-stale before the game starts) - so it is pure BCL + cmd builtins
    /// (mklink needs no admin on NTFS). Unlike AltBase this deals only in a Mods dir: named profiles need no
    /// junctions (the boot plugin redirects the scan in-process; the real base dir stays active).
    /// </summary>
    internal static class ProfileBuilder
    {
        /// <summary>SHA256 (hex) over the sorted "name|size|sha256" line of every input. Any changed, added or
        /// removed input changes the fingerprint; unreadable inputs hash as "?" (counts as different).</summary>
        internal static string ComputeFingerprint(IEnumerable<BuildInput> inputs)
        {
            var lines = new List<string>();
            foreach (var i in inputs ?? Enumerable.Empty<BuildInput>())
            {
                long size = -1; string hash = "?";
                try
                {
                    size = new FileInfo(i.SourcePath).Length;
                    hash = Sha256OfFile(i.SourcePath) ?? "?";
                }
                catch (Exception e) { SharedLog.WarnOnce("fp:" + i.SourcePath, $"could not read '{i.SourcePath}' for the profile fingerprint (counted as changed): {e.Message}"); }
                lines.Add(i.FileName.ToLowerInvariant() + "|" + size + "|" + hash);
            }
            lines.Sort(StringComparer.Ordinal);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)))).ToLowerInvariant();
        }

        /// <summary>
        /// Make <paramref name="modsDir"/> contain exactly the inputs (as hardlinks, copies cross-volume).
        /// Existing files not in the set are deleted (locked ones tolerated and reported via the return); files
        /// already matching their source (size) are kept. "local" files the caller passes with SourcePath ==
        /// their own path inside modsDir are left untouched. Returns true when every input is present afterwards.
        /// </summary>
        internal static bool BuildModsDir(string modsDir, IReadOnlyList<BuildInput> inputs, Action<string> log = null)
        {
            try
            {
                Directory.CreateDirectory(modsDir);
                var wanted = new HashSet<string>(inputs.Select(i => i.FileName), StringComparer.OrdinalIgnoreCase);

                foreach (var f in Directory.GetFiles(modsDir))
                {
                    if (wanted.Contains(Path.GetFileName(f))) continue;
                    try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
                    catch (Exception e) { log?.Invoke($"stale '{Path.GetFileName(f)}' could not be removed ({e.Message}); left in place."); }
                }

                bool allOk = true;
                foreach (var i in inputs)
                {
                    string dst = Path.Combine(modsDir, i.FileName);
                    try
                    {
                        if (PathsEqual(dst, i.SourcePath)) continue;   // a "local" file that lives here already
                        if (File.Exists(dst))
                        {
                            if (SameBytes(dst, i.SourcePath)) continue; // already holds the same bytes (a locked leftover of a rebuild)
                            File.SetAttributes(dst, FileAttributes.Normal);
                            File.Delete(dst);
                        }
                        if (!MakeHardLink(dst, i.SourcePath))
                            File.Copy(i.SourcePath, dst, true);        // cross-volume / non-NTFS fallback
                    }
                    catch (Exception e)
                    {
                        allOk = false;
                        log?.Invoke($"could not provide '{i.FileName}': {e.Message}");
                    }
                }
                return allOk;
            }
            catch (Exception e)
            {
                log?.Invoke("profile build failed: " + e.Message);
                return false;
            }
        }

        internal static string Sha256OfFile(string path)
        {
            try
            {
                using var sha = SHA256.Create();
                using var s = File.OpenRead(path);
                return Convert.ToHexString(sha.ComputeHash(s)).ToLowerInvariant();
            }
            catch (Exception e)
            {
                SharedLog.WarnOnce("sha:" + path, $"could not hash '{path}': {e.Message}");
                return null;
            }
        }

        /// <summary>Same size and same SHA256. A size match alone keeps a stale build that happens to be as long.</summary>
        private static bool SameBytes(string a, string b)
        {
            if (!SameSize(a, b)) return false;
            string ha = Sha256OfFile(a), hb = Sha256OfFile(b);
            return ha != null && ha == hb;
        }

        private static bool SameSize(string a, string b)
        {
            try { return new FileInfo(a).Length == new FileInfo(b).Length; }
            catch (Exception e)
            {
                SharedLog.WarnOnce("size:" + a, $"could not compare the sizes of '{a}' and '{b}' (treated as different): {e.Message}");
                return false;
            }
        }

        private static bool PathsEqual(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch (Exception e)
            {
                SharedLog.WarnOnce("path:" + a, $"could not normalise '{a}' / '{b}'; comparing the text: {e.Message}");
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool MakeHardLink(string link, string target)
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /H \"{link}\" \"{target}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null)
                {
                    SharedLog.WarnOnce("mklink-start", "cmd.exe did not start for mklink; copying files instead of linking them.");
                    return false;
                }
                // Read both streams before waiting, or a chatty failure fills the pipe and the wait runs into its timeout.
                var errTask = p.StandardError.ReadToEndAsync();
                var outTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(15000))
                {
                    SharedLog.WarnOnce("mklink-timeout", $"mklink for '{Path.GetFileName(link)}' did not finish in 15 s; copying instead.");
                    return false;
                }
                bool ok = p.ExitCode == 0 && File.Exists(link);
                if (!ok)
                {
                    string detail = (errTask.Wait(1000) ? errTask.Result : "") + (outTask.Wait(1000) ? outTask.Result : "");
                    // Expected across volumes and on non-NTFS: the caller falls back to a copy.
                    SharedLog.WarnOnce("mklink-fail", $"mklink /H failed for '{Path.GetFileName(link)}' (exit {p.ExitCode}: {detail.Trim()}); copying instead of linking.");
                }
                return ok;
            }
            catch (Exception e)
            {
                SharedLog.WarnOnce("mklink-throw", $"mklink threw ({e.Message}); copying files instead of linking them.");
                return false;
            }
        }
    }
}
