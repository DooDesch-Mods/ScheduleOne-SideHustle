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
    /// fetched automatically. The client opens the link and downloads the file in the browser; this watches the
    /// user's Downloads folder, Vortex's Schedule I download folder and the drop/staging folder, validates any
    /// candidate (.dll, or every .dll inside a .zip) by SHA256 against the host manifest, and promotes a match
    /// into the hash-keyed manual cache so the diff resolver picks it up. Only staging files are consumed
    /// (deleted) - the other folders belong to the user and are read-only. Polling-based (a FileSystemWatcher is
    /// flaky on some volumes); the UI calls Poll() each frame while its checklist is open, and the session dedupe
    /// keeps that cheap: each file is hashed at most once per (path, size, mtime).
    /// </summary>
    internal static class ManualInstall
    {
        private static readonly HashSet<string> _scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _watchStartUtc = DateTime.UtcNow;

        /// <summary>Bumped whenever a near-miss note changes, so the UI re-renders without a resolve.</summary>
        internal static int NotesVersion { get; private set; }

        internal static string StagingDir()
        {
            string root = Mods.ModInventory.GameRoot();
            string dir = Path.Combine(root ?? ".", "SideHustle_Profiles", "cache", "_staging");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Reset the watcher for a fresh checklist: downloads from now on (minus a grace window) count
        /// as fresh, and every file may be re-examined once against the new pending set.</summary>
        internal static void BeginSession()
        {
            _scanned.Clear();
            _watchStartUtc = DateTime.UtcNow;
        }

        /// <summary>Vortex's download folder for this game, when Vortex is around - anything the user already
        /// fetched through Vortex resolves with zero clicks. Read-only.</summary>
        internal static string VortexDownloadsDir()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(appData)) return null;
                string dir = Path.Combine(appData, "Vortex", "downloads", "schedule1");
                return Directory.Exists(dir) ? dir : null;
            }
            catch { return null; }
        }

        /// <summary>The folders the checklist tells the player it is watching.</summary>
        internal static string WatchedFoldersLabel()
        {
            var parts = new List<string> { "your Downloads folder" };
            if (VortexDownloadsDir() != null) parts.Add("Vortex downloads");
            parts.Add("the drop folder");
            return string.Join(", ", parts);
        }

        // The watched roots. Staging is ours (consumed on match); the Downloads folder catches the browser
        // download directly; the Vortex folder reuses earlier Vortex fetches.
        private static IEnumerable<(string Dir, ManualScan.RootKind Kind)> Roots()
        {
            yield return (StagingDir(), ManualScan.RootKind.Staging);
            string dl = KnownFolders.Downloads();
            if (dl != null && Directory.Exists(dl)) yield return (dl, ManualScan.RootKind.Downloads);
            string vortex = VortexDownloadsDir();
            if (vortex != null) yield return (vortex, ManualScan.RootKind.Vortex);
        }

        // A mod the client must fetch by hand: an nx: link mod (Manual) OR a source-less Nexus-only mod
        // (Dropped). Both carry the host's Sha256, so both verify the same way; the only difference is Manual
        // has a direct link and Dropped only a name search.
        private static bool IsAwaiting(DiffEntry e) => e.Status == DiffStatus.Manual || e.Status == DiffStatus.Dropped;

        /// <summary>Scan the watched folders and try to satisfy any still-pending manual entry. Returns the
        /// entries that flipped to resolved this call (so the UI can refresh and toast).</summary>
        internal static List<DiffEntry> Poll(IEnumerable<DiffEntry> manualEntries)
        {
            var resolved = new List<DiffEntry>();
            var pending = manualEntries.Where(e => IsAwaiting(e) && !string.IsNullOrEmpty(e.Mod.Sha256)).ToList();
            if (pending.Count == 0) return resolved;

            foreach (var (dir, kind) in Roots())
            {
                foreach (var path in SafeFiles(dir))
                {
                    string key = null;
                    try
                    {
                        var fi = new FileInfo(path);
                        if (!ManualScan.IsCandidate(kind, fi.Name, fi.Length, fi.LastWriteTimeUtc, _watchStartUtc)) continue;
                        key = ManualScan.ScanKey(path, fi.Length, fi.LastWriteTimeUtc);
                        if (_scanned.Contains(key)) continue;

                        if (ManualScan.IsUnreadableArchive(fi.Name)) NoteNearMiss(fi.Name, unreadableArchive: true, pending);
                        else
                        {
                            bool consume = kind == ManualScan.RootKind.Staging;
                            string ext = Path.GetExtension(path).ToLowerInvariant();
                            if (ext == ".dll") TryDll(path, consume, pending, resolved);
                            else if (ext == ".zip") TryZip(path, consume, pending, resolved);
                        }
                        // Only remember a file once it was fully examined - a locked/mid-copy file throws above
                        // and gets retried on a later poll (its key also changes while it grows).
                        _scanned.Add(key);
                        if (pending.All(e => !IsAwaiting(e))) return resolved;
                    }
                    catch (InvalidDataException) { if (key != null) _scanned.Add(key); }   // corrupt zip: done with it
                    catch { /* mid-copy or locked: next poll */ }
                }
            }
            return resolved;
        }

        private static void TryDll(string path, bool consume, List<DiffEntry> pending, List<DiffEntry> resolved)
        {
            string sha = ProfileBuilder.Sha256OfFile(path);
            if (sha == null) return;
            var match = pending.FirstOrDefault(e => IsAwaiting(e) &&
                string.Equals(e.Mod.Sha256, sha, StringComparison.OrdinalIgnoreCase));
            if (match == null) { NoteNearMiss(Path.GetFileName(path), unreadableArchive: false, pending); return; }
            string promoted = PromoteBytes(File.ReadAllBytes(path), match.Mod.File, sha);
            if (promoted == null) return;
            Resolve(match, promoted, resolved);
            if (consume) { try { File.Delete(path); } catch { /* leave it */ } }
        }

        private static void TryZip(string path, bool consume, List<DiffEntry> pending, List<DiffEntry> resolved)
        {
            bool consumed = false;
            using (var zip = ZipFile.OpenRead(path))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name) || !entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    byte[] bytes;
                    using (var s = entry.Open())
                    using (var ms = new MemoryStream()) { s.CopyTo(ms); bytes = ms.ToArray(); }
                    string sha = Sha256(bytes);
                    var match = pending.FirstOrDefault(e => IsAwaiting(e) &&
                        string.Equals(e.Mod.Sha256, sha, StringComparison.OrdinalIgnoreCase));
                    if (match == null) { NoteNearMiss(entry.Name, unreadableArchive: false, pending); continue; }
                    string promoted = PromoteBytes(bytes, match.Mod.File, sha);
                    if (promoted == null) continue;
                    Resolve(match, promoted, resolved);
                    consumed = true;
                }
            }
            if (!consumed) NoteNearMiss(Path.GetFileName(path), unreadableArchive: false, pending);
            if (consumed && consume) { try { File.Delete(path); } catch { } }
        }

        private static void Resolve(DiffEntry match, string promoted, List<DiffEntry> resolved)
        {
            match.Status = DiffStatus.Cached;
            match.SourcePath = promoted;
            if (match.ManualNote != null) { match.ManualNote = null; NotesVersion++; }
            resolved.Add(match);
        }

        // A file that LOOKS like a pending mod (by name) but cannot be used as-is: tell the player why on that
        // row instead of silently ignoring it. Wrong hash almost always means a different version than the
        // host's; 7z/rar we simply cannot open.
        private static void NoteNearMiss(string fileName, bool unreadableArchive, List<DiffEntry> pending)
        {
            string shortName = fileName != null && fileName.Length > 30 ? fileName.Substring(0, 27) + "..." : fileName;
            foreach (var e in pending)
            {
                if (!IsAwaiting(e) || !ManualScan.NameLooksLike(fileName, e.Mod.Name, e.Mod.File)) continue;
                string note = unreadableArchive
                    ? $"'{shortName}': can't read this archive - extract it and drop the .dll in."
                    : string.IsNullOrEmpty(e.Mod.Version)
                        ? $"'{shortName}' doesn't match the host's file."
                        : $"'{shortName}' is a different version - host needs {e.Mod.Version}.";
                if (e.ManualNote != note) { e.ManualNote = note; NotesVersion++; }
            }
        }

        /// <summary>Store validated bytes under cache\_manual\&lt;sha256&gt;\&lt;file&gt; so the resolver's hash
        /// index finds them (this join and every rejoin).</summary>
        internal static string PromoteBytes(byte[] bytes, string fileName, string sha)
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
