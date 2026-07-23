using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SideHustle.Shared
{
    /// <summary>
    /// Resolves the user's real Downloads folder. Environment.SpecialFolder has no entry for it, so this asks
    /// the shell (SHGetKnownFolderPath) and falls back to %USERPROFILE%\Downloads when the shell API is
    /// unavailable (non-Windows runtime, relocated profile).
    /// </summary>
    internal static class KnownFolders
    {
        private static readonly Guid DownloadsFolderId = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        internal static string Downloads()
        {
            try
            {
                if (SHGetKnownFolderPath(DownloadsFolderId, 0, IntPtr.Zero, out var ptr) == 0 && ptr != IntPtr.Zero)
                {
                    try { return Marshal.PtrToStringUni(ptr); }
                    finally { Marshal.FreeCoTaskMem(ptr); }
                }
            }
            catch { /* shell unavailable - fall back below */ }
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home)) return Path.Combine(home, "Downloads");
            }
            catch { /* no profile dir either */ }
            return null;
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
            uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
    }
}
