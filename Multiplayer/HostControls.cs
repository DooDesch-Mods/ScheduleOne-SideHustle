using System;
using Il2CppFishNet;
using Il2CppFishNet.Managing.Server;
using Il2CppFishNet.Managing.Logging;
using Il2CppScheduleOne.PlayerScripts;

namespace SideHustle.Multiplayer
{
    /// <summary>
    /// Host-authoritative session controls that any Side Hustle gamemode can reuse. Currently: kick a player by their
    /// Steam id. The FishNet kick only takes effect on the machine that is the server (the host), so every call is a
    /// no-op unless <see cref="InstanceFinder.IsServer"/> - a client that somehow triggers a kick does nothing, which
    /// is the intended safety (only the host removes players).
    /// </summary>
    internal static class HostControls
    {
        /// <summary>Kick the connected player whose Steam id (their replicated <c>PlayerCode</c>) matches. Host-only,
        /// and never kicks the host itself. Returns true if a matching remote player was found and disconnected.</summary>
        internal static bool KickBySteamId(ulong steamId64, string reason)
        {
            try
            {
                bool isServer = false;
                try { isServer = InstanceFinder.IsServer; } catch { }
                if (!isServer) { Core.Log?.Warning("[mp] kick ignored - only the host can kick players."); return false; }

                string code = steamId64.ToString();
                var list = Player.PlayerList;
                if (list == null) return false;
                for (int i = 0; i < list.Count; i++)
                {
                    var p = list[i];
                    if (p == null) continue;
                    string pc = null;
                    try { pc = p.PlayerCode; } catch { }
                    if (pc != code) continue;

                    var conn = p.Connection;   // the owning NetworkConnection (server-side)
                    if (conn == null) { Core.Log?.Warning("[mp] kick: target has no connection."); return false; }
                    try { if (conn.IsLocalClient) { Core.Log?.Warning("[mp] kick: refusing to kick the host."); return false; } } catch { }

                    conn.Kick(KickReason.Unset, LoggingType.Warning, string.IsNullOrEmpty(reason) ? "Kicked by host" : reason);
                    Core.Log?.Msg($"[mp] kicked player {code} ({reason}).");
                    return true;
                }
                Core.Log?.Warning($"[mp] kick: no connected player with id {code}.");
                return false;
            }
            catch (Exception e) { Core.Log?.Warning("[mp] kick failed: " + e.Message); return false; }
        }
    }
}
