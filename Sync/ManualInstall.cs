using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using SideHustle.Profiles;
using SideHustle.Shared;

namespace SideHustle.Sync
{
    /// <summary>
    /// The manual (nx:) install flow: mods the host sources from a download link (Nexus etc.) that cannot be
    /// fetched automatically. The client opens the link, drops the file (a .dll or the mod's .zip) into a
    /// staging folder, and this validates it by SHA256 against the host manifest, promoting a match into the
    /// hash-keyed manual cache so the diff resolver picks it up. Polling-based (a FileSystemWatcher is flaky on
    /// some volumes); the UI calls Poll() each frame while its checklist is open.
    /// </summary>
    internal static class ManualInstall
    {
        internal static string StagingDir()
        {
            string root = Mods.ModInventory.GameRoot();
            string dir = Path.Combine(root ?? ".", "SideHustle_Profiles", "cache", "_staging");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Scan the staging folder and try to satisfy any still-pending manual entry. Returns the entries
        /// that flipped to resolved this call (so the UI can refresh + delete the consumed file).</summary>
        internal static List<DiffEntry> Poll(IEnumerable<DiffEntry> manualEntries)
        {
            var resolved = new List<DiffEntry>();
            var pending = manualEntries.Where(e => e.Status == DiffStatus.Manual && !string.IsNullOrEmpty(e.Mod.Sha256)).ToList();
            if (pending.Count == 0) return resolved;

            string staging = StagingDir();
            foreach (var path in SafeFiles(staging))
            {
                try
                {
                    string ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext == ".dll") TryDll(path, pending, resolved);
                    else if (ext == ".zip") TryZip(path, pending, resolved);
                    if (pending.All(e => e.Status != DiffStatus.Manual)) break;
                }
                catch { /* a file mid-copy: next poll */ }
            }
            return resolved;
        }

        private static void TryDll(string path, List<DiffEntry> pending, List<DiffEntry> resolved)
        {
            string sha = ProfileBuilder.Sha256OfFile(path);
            if (sha == null) return;
            var match = pending.FirstOrDefault(e => e.Status == DiffStatus.Manual &&
                string.Equals(e.Mod.Sha256, sha, StringComparison.OrdinalIgnoreCase));
            if (match == null) return;
            string promoted = PromoteBytes(File.ReadAllBytes(path), match.Mod.File, sha);
            if (promoted == null) return;
            match.Status = DiffStatus.Cached;
            match.SourcePath = promoted;
            resolved.Add(match);
            try { File.Delete(path); } catch { /* leave it */ }
        }

        private static void TryZip(string path, List<DiffEntry> pending, List<DiffEntry> resolved)
        {
            using var zip = ZipFile.OpenRead(path);
            bool consumed = false;
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) || !entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                byte[] bytes;
                using (var s = entry.Open())
                using (var ms = new MemoryStream()) { s.CopyTo(ms); bytes = ms.ToArray(); }
                string sha = Sha256(bytes);
                var match = pending.FirstOrDefault(e => e.Status == DiffStatus.Manual &&
                    string.Equals(e.Mod.Sha256, sha, StringComparison.OrdinalIgnoreCase));
                if (match == null) continue;
                string promoted = PromoteBytes(bytes, match.Mod.File, sha);
                if (promoted == null) continue;
                match.Status = DiffStatus.Cached;
                match.SourcePath = promoted;
                resolved.Add(match);
                consumed = true;
            }
            if (consumed) { try { File.Delete(path); } catch { } }
        }

        // Store validated bytes under cache\_manual\<sha256>\<file> so the resolver's hash index finds them.
        private static string PromoteBytes(byte[] bytes, string fileName, string sha)
        {
            try
            {
                string cacheRoot = PackageCache.RootFor(ProfileEngine.GameRoot);
                string dir = PackageCache.ManualPathFor(cacheRoot, sha);
                Directory.CreateDirectory(dir);
                string dst = Path.Combine(dir, fileName);
                File.WriteAllBytes(dst, bytes);
                return dst;
            }
            catch (Exception e) { Core.Log?.Warning("[sync] manual promote failed: " + e.Message); return null; }
        }

        private static IEnumerable<string> SafeFiles(string dir)
        {
            try { return Directory.GetFiles(dir); } catch { return Array.Empty<string>(); }
        }

        private static string Sha256(byte[] bytes)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
        }
    }
}
