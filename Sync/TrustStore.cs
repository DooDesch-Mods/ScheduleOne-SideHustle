using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SideHustle.Sync
{
    /// <summary>
    /// Trust-on-rejoin: once a player consented to a host's manifest, rejoining the SAME host (SteamID) with an
    /// UNCHANGED manifest hash skips the consent screen (the downloads are cached anyway). Any manifest change
    /// re-prompts. Stored as JSON under the REAL game root's UserData - never MelonPreferences, whose cfg is
    /// per-profile after the UserData-clone change and rewritten wholesale on save.
    /// </summary>
    internal static class TrustStore
    {
        private sealed class Entry
        {
            public string MHash { get; set; } = "";
            public string HostName { get; set; } = "";
            public string When { get; set; } = "";
        }

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { WriteIndented = true };
        private static Dictionary<string, Entry> _map;   // key: host SteamID as string

        private static string PathFor()
        {
            var root = Mods.ModInventory.GameRoot();
            return root == null ? null : Path.Combine(root, "UserData", "SideHustle", "trust.json");
        }

        private static Dictionary<string, Entry> Map()
        {
            if (_map != null) return _map;
            try
            {
                string p = PathFor();
                _map = p != null && File.Exists(p)
                    ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(p), JsonOpts)
                    : null;
            }
            catch { _map = null; }
            return _map ??= new Dictionary<string, Entry>();
        }

        internal static bool IsTrusted(ulong hostSteamId, string mhash) =>
            !string.IsNullOrEmpty(mhash) && Map().TryGetValue(hostSteamId.ToString(), out var e)
            && string.Equals(e.MHash, mhash, StringComparison.Ordinal);

        internal static void Trust(ulong hostSteamId, string mhash, string hostName)
        {
            if (hostSteamId == 0 || string.IsNullOrEmpty(mhash)) return;
            Map()[hostSteamId.ToString()] = new Entry
            {
                MHash = mhash,
                HostName = hostName ?? "",
                When = DateTime.UtcNow.ToString("o"),
            };
            Save();
        }

        internal static void ForgetAll()
        {
            Map().Clear();
            Save();
        }

        private static void Save()
        {
            try
            {
                string p = PathFor();
                if (p == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, JsonSerializer.Serialize(Map(), JsonOpts));
            }
            catch { /* best-effort */ }
        }
    }
}
