using System;
using System.Collections.Generic;
using Il2CppSteamworks;
using SideHustle.Multiplayer;

namespace SideHustle.Sync
{
    /// <summary>
    /// Host-side enforcement for "synced clients only" lobbies. Every few seconds it matches each remote lobby member
    /// against the lobby MEMBER data key "sh_sync" the client sets after a synced join. A member whose hash is wrong
    /// or still absent after a grace period is kicked. Pumped from Core.OnUpdate while a host session is live.
    ///
    /// Members are read from the Steam lobby rather than from the spawned players: a client is in the lobby long
    /// before its player exists in the world, and its grace has to start when it arrives, not when it has finished
    /// loading. Applies to VANILLA co-op lobbies only - a gamemode lobby carries no mod-set hash and its clients never
    /// write the handshake, so arming the gate there would remove every joiner (see <see cref="CanEnforce"/>).
    /// </summary>
    internal static class SyncGate
    {
        private const float ScanInterval = 5f;

        /// <summary>A member that has declared a DIFFERENT mod set is not going to fix it by waiting.</summary>
        private const float MismatchGrace = 20f;

        /// <summary>A member that has declared nothing yet is most likely still loading the world; the client writes
        /// the handshake once the load is through, and its own load allowance is 120 s, so the host waits longer.</summary>
        private const float LoadingGrace = 150f;

        private sealed class MemberState
        {
            public float FirstSeen;
            public bool KickFailedLogged;
            public bool ReadFailedLogged;
        }

        private static bool _active;
        private static string _expectedHash;
        private static float _nextScan;
        private static readonly Dictionary<ulong, MemberState> _members = new Dictionary<ulong, MemberState>();
        private static bool _scanFailedLogged;

        /// <summary>Whether the gate is armed right now. The single answer to "is this lobby enforcing", so the
        /// advertised key and the kicking cannot drift apart - they used to be two flags, and turning the
        /// requirement off in the phone app moved only the advertisement.</summary>
        internal static bool IsActive => _active;

        /// <summary>Whether the synced-clients-only requirement can apply to this lobby at all. False for a gamemode
        /// lobby and for no lobby; the switch that arms the gate should be hidden or disabled then.</summary>
        internal static bool CanEnforce(ulong lobbyId) => lobbyId != 0 && !LobbyCoordinator.IsGamemodeLobby(lobbyId);

        /// <summary>Arm the gate against <paramref name="expectedMHash"/>. Refused (false, with a warning) on a
        /// gamemode lobby or when there is no lobby.</summary>
        internal static bool Enable(string expectedMHash)
        {
            ulong lobbyId = LobbyCoordinator.CurrentLobbyId;
            if (!CanEnforce(lobbyId))
            {
                Core.Log?.Warning(lobbyId == 0
                    ? "[sync] gate not armed: no lobby."
                    : "[sync] gate not armed: this is a gamemode lobby, whose clients never send the synced-mods handshake.");
                return false;
            }
            _active = true;
            _expectedHash = expectedMHash ?? "";
            _members.Clear();
            _nextScan = 0f;
            _scanFailedLogged = false;
            Core.Log?.Msg($"[sync] gate armed for lobby {lobbyId} (hash {_expectedHash}).");
            return true;
        }

        internal static void Disable()
        {
            if (_active) Core.Log?.Msg("[sync] gate disarmed.");
            _active = false;
            _members.Clear();
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
                ulong self = SteamUser.GetSteamID().m_SteamID;
                int count = SteamMatchmaking.GetNumLobbyMembers(lobby);
                var present = new HashSet<ulong>();
                for (int i = 0; i < count; i++)
                {
                    ulong id = 0;
                    try
                    {
                        id = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i).m_SteamID;
                        if (id == 0 || id == self) continue;   // the host itself is never gated
                        present.Add(id);
                        EvaluateMember(lobby, id, now);
                    }
                    catch (Exception e)
                    {
                        // One member that cannot be read must not end the scan for everyone else, and is not evidence
                        // that the member is unsynced.
                        Core.Log?.Warning($"[sync] gate: could not evaluate lobby member {id}: {e.Message}");
                    }
                }

                if (_members.Count > present.Count)
                {
                    var gone = new List<ulong>();
                    foreach (var k in _members.Keys) if (!present.Contains(k)) gone.Add(k);
                    foreach (var k in gone) _members.Remove(k);
                }
            }
            catch (Exception e)
            {
                if (!_scanFailedLogged) { _scanFailedLogged = true; Core.Log?.Warning("[sync] gate scan failed: " + e.Message); }
            }
        }

        private static void EvaluateMember(CSteamID lobby, ulong id, float now)
        {
            if (!_members.TryGetValue(id, out var state))
            {
                state = new MemberState { FirstSeen = now };
                _members[id] = state;
                Core.Log?.Msg($"[sync] gate: member {id} entered the lobby; waiting for the synced-mods handshake.");
            }

            string got;
            try { got = SteamMatchmaking.GetLobbyMemberData(lobby, new CSteamID(id), "sh_sync"); }
            catch (Exception e)
            {
                // Unknown, not unsynced: keep waiting and try again next scan.
                if (!state.ReadFailedLogged)
                {
                    state.ReadFailedLogged = true;
                    Core.Log?.Warning($"[sync] gate: could not read the handshake of member {id}, still waiting: {e.Message}");
                }
                return;
            }

            if (!string.IsNullOrEmpty(got) && string.Equals(got, _expectedHash, StringComparison.Ordinal))
            {
                Core.Log?.Msg($"[sync] gate: member {id} is synced.");
                _members.Remove(id);
                return;
            }

            bool mismatch = !string.IsNullOrEmpty(got);
            float grace = mismatch ? MismatchGrace : LoadingGrace;
            if (now - state.FirstSeen < grace) return;

            Core.Log?.Msg($"[sync] gate: member {id} {(mismatch ? "runs a different mod set" : "sent no handshake")} after {grace:n0}s - kicking.");
            if (HostControls.KickBySteamId(id, "This lobby requires the host's synced mods (Side Hustle Sync).", logMissing: !state.KickFailedLogged))
            {
                NotifyHost("Side Hustle Sync", "A player was removed - they couldn't sync your mods.");
                _members.Remove(id);
            }
            else if (!state.KickFailedLogged)
            {
                // They are in the lobby but have no player in the world yet; the next scan tries again.
                state.KickFailedLogged = true;
                Core.Log?.Warning($"[sync] gate: member {id} could not be kicked yet (no connected player); retrying every scan.");
            }
        }

        /// <summary>Surface a host-facing message through the game's own in-world notification popup (the host is in
        /// the world, not the menu, when the gate runs - a menu toast would never show).</summary>
        private static void NotifyHost(string title, string subtitle)
        {
            try
            {
                var mgr = Il2CppScheduleOne.DevUtilities.Singleton<Il2CppScheduleOne.UI.NotificationsManager>.Instance;
                if (mgr != null) mgr.SendNotification(title, subtitle, null, 6f, true);
                else Core.Log?.Warning("[sync] gate: the notification manager is not available; the host sees no popup.");
            }
            catch (Exception e) { Core.Log?.Warning("[sync] gate: the host popup failed: " + e.Message); }
        }
    }
}
