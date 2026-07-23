using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using SideHustle.Mods;

namespace SideHustle.Sync
{
    /// <summary>
    /// Remembers the LAST vanilla lobby the player synced into, so they can turn that mod set into a permanent
    /// named profile later ("Create profile from this" at the top of the Mod Profiles list). Only the most recent
    /// is kept. Persisted to UserData\SideHustle\last_sync.json - which lives under a UserData subdirectory that is
    /// junctioned into every profile base, so the write (from the base session) and the read (from anywhere) agree.
    /// </summary>
    internal static class LastSync
    {
        internal sealed class Record
        {
            public string Host { get; set; } = "";
            public string Manifest { get; set; } = "";   // SyncManifest canonical text
            public int ModCount { get; set; }            // syncable (ts:/nx:) mods, for the row subtitle
        }

        private static string PathFor()
        {
            string root = ModInventory.GameRoot();
            return root == null ? null : Path.Combine(root, "UserData", "SideHustle", "last_sync.json");
        }

        internal static void Save(string host, SyncManifest manifest)
        {
            try
            {
                if (manifest == null) return;
                string p = PathFor();
                if (p == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                // Only ts: mods can be auto-recreated into a profile (nx: are Nexus links installed by hand), so the
                // row's count must reflect the ts: set CreateFromLastSync actually pins, not the full synced set.
                int count = manifest.Mods.Count(m => m.Source != null && m.Source.StartsWith("ts:", StringComparison.Ordinal));
                var rec = new Record { Host = host ?? "", Manifest = manifest.ToCanonicalText(), ModCount = count };
                File.WriteAllText(p, JsonSerializer.Serialize(rec));
            }
            catch (Exception e) { Core.Log?.Warning("[sync] last-sync save failed: " + e.Message); }
        }

        internal static Record Load()
        {
            try
            {
                string p = PathFor();
                if (p == null || !File.Exists(p)) return null;
                var rec = JsonSerializer.Deserialize<Record>(File.ReadAllText(p));
                return rec != null && !string.IsNullOrEmpty(rec.Manifest) ? rec : null;
            }
            catch { return null; }
        }

        internal static void Clear()
        {
            try { string p = PathFor(); if (p != null && File.Exists(p)) File.Delete(p); }
            catch { /* best-effort */ }
        }
    }
}
