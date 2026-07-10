using System;
using System.Collections.Generic;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppSteamworks;
using SideHustle.Multiplayer;

namespace SideHustle.Sync
{
    /// <summary>
    /// Host-side enforcement for "synced clients only" lobbies. Every few seconds it matches each remote player
    /// (by their replicated PlayerCode = SteamID) against the lobby MEMBER data key "sh_sync" the client sets
    /// after a synced join. A member whose hash is absent or wrong after a grace period (manual joiners and the
    /// FishNet spawn both take a moment) is kicked. With enforcement off it only logs. Pumped from Core.OnUpdate
    /// while a host session is live; a NON-enforcing host still does nothing here.
    /// </summary>
    internal static class SyncGate
    {
        private const float ScanInterval = 5f;
        private const float Grace = 20f;

        private static bool _active;
        private static string _expectedHash;
        private static float _nextScan;
        private static readonly Dictionary<string, float> _firstSeen = new Dictionary<string, float>();

        internal static void Enable(string expectedMHash)
        {
            _active = true;
            _expectedHash = expectedMHash ?? "";
            _firstSeen.Clear();
            _nextScan = 0f;
        }

        internal static void Disable()
        {
            _active = false;
            _firstSeen.Clear();
        }

        internal static void Tick(ulong lobbyId)
        {
            if (!_active || lobbyId == 0) return;
            float now = UnityEngine.Time.unscaledTime;
            if (now < _nextScan) return;
            _nextScan = now + ScanInterval;

            try
            {
                var lobby = new CSteamID(lobbyId);
                var list = Player.PlayerList;
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var p = list[i];
                    if (p == null) continue;
                    string code = null;
                    try { code = p.PlayerCode; } catch { }
                    if (string.IsNullOrEmpty(code)) continue;

                    // Skip the host itself (its own SteamID owns the lobby).
                    try { if (p.Connection != null && p.Connection.IsLocalClient) continue; } catch { }

                    string got = "";
                    try { got = SteamMatchmaking.GetLobbyMemberData(lobby, new CSteamID(ulong.Parse(code)), "sh_sync"); } catch { }
                    bool ok = !string.IsNullOrEmpty(got) && string.Equals(got, _expectedHash, StringComparison.Ordinal);
                    if (ok) { _firstSeen.Remove(code); continue; }

                    if (!_firstSeen.TryGetValue(code, out float since)) { _firstSeen[code] = now; continue; }
                    if (now - since < Grace) continue;

                    Core.Log?.Msg($"[sync] gate: player {code} is not synced after {Grace:n0}s - kicking.");
                    HostControls.KickBySteamId(ulong.Parse(code), "This lobby requires the host's synced mods (Side Hustle Sync).");
                    _firstSeen.Remove(code);
                }
            }
            catch (Exception e) { Core.Log?.Warning("[sync] gate scan failed: " + e.Message); }
        }
    }
}
