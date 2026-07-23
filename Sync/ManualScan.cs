using System;
using System.IO;
using System.Linq;

namespace SideHustle.Sync
{
    /// <summary>
    /// Pure rules for the manual-install folder watcher: which files in a watched folder are worth hashing,
    /// the per-session dedupe key that keeps the per-frame poll cheap, and the name matching behind the
    /// near-miss hints (a found file that LOOKS like a pending mod but cannot be used as-is). No IO - the
    /// scanner feeds it file facts, which keeps every rule testable in the harness.
    /// </summary>
    internal static class ManualScan
    {
        /// <summary>Watched-folder kinds: staging is ours (a consumed file is deleted), the others belong to
        /// the user or another tool and are only ever read.</summary>
        internal enum RootKind { Staging, Downloads, Vortex }

        /// <summary>No Schedule I mod archive is anywhere near this - larger files are unrelated downloads.</summary>
        internal const long MaxBytes = 100L * 1024 * 1024;

        /// <summary>Files downloaded shortly BEFORE the checklist opened still count as fresh.</summary>
        internal static readonly TimeSpan DownloadsGrace = TimeSpan.FromMinutes(10);

        /// <summary>Whether a file in a watched folder is worth examining at all (extension, size, freshness).
        /// The Downloads folder only counts files newer than the watch start minus a grace window - it is full
        /// of old, unrelated files; staging and the Vortex folder keep their history.</summary>
        internal static bool IsCandidate(RootKind kind, string fileName, long sizeBytes, DateTime mtimeUtc, DateTime watchStartUtc)
        {
            string ext = ExtOf(fileName);
            if (ext != ".zip" && ext != ".dll" && ext != ".7z" && ext != ".rar") return false;
            if (sizeBytes <= 0 || sizeBytes > MaxBytes) return false;
            if (kind == RootKind.Downloads && mtimeUtc < watchStartUtc - DownloadsGrace) return false;
            return true;
        }

        /// <summary>An archive format we cannot open (no 7z/rar support in the BCL) - only good for a hint.</summary>
        internal static bool IsUnreadableArchive(string fileName)
        {
            string ext = ExtOf(fileName);
            return ext == ".7z" || ext == ".rar";
        }

        /// <summary>Session dedupe key: a candidate is hashed at most once per (path, size, mtime). A file still
        /// being written changes size/mtime and is naturally re-examined once the copy settles.</summary>
        internal static string ScanKey(string path, long sizeBytes, DateTime mtimeUtc) =>
            path + "|" + sizeBytes + "|" + mtimeUtc.Ticks;

        /// <summary>Whether a downloaded file's name plausibly belongs to a mod: the normalized mod name or DLL
        /// stem is contained in the normalized file name (Nexus names downloads like
        /// "Litterally-208-1-0-0-1717....zip"). Short stems are skipped - they match everything.</summary>
        internal static bool NameLooksLike(string fileName, string modName, string modFile)
        {
            string hay = Norm(StripExt(fileName));
            if (hay.Length == 0) return false;
            string byName = Norm(modName);
            if (byName.Length >= 4 && hay.Contains(byName)) return true;
            string stem = modFile != null && modFile.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? modFile.Substring(0, modFile.Length - 4) : modFile;
            string byStem = Norm(stem);
            return byStem.Length >= 4 && hay.Contains(byStem);
        }

        private static string ExtOf(string fileName)
        {
            try { return (Path.GetExtension(fileName) ?? "").ToLowerInvariant(); }
            catch { return ""; }
        }

        private static string StripExt(string fileName)
        {
            try { return Path.GetFileNameWithoutExtension(fileName) ?? ""; }
            catch { return fileName ?? ""; }
        }

        private static string Norm(string s) =>
            s == null ? "" : new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
