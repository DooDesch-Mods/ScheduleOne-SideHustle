using System;
using System.Collections.Generic;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppSteamworks;
using SideHustle.Multiplayer;

namespace SideHustle.Internal
{
    /// <summary>
    /// Who is in the current gamemode session, for <see cref="API.Players"/> and the PlayerJoined/PlayerLeft events.
    ///
    /// The source is the Steam lobby member list, polled twice a second. The game's own
    /// <c>Player.onPlayerSpawned</c>/<c>onPlayerDespawned</c> only exist once a world is loaded, so they would leave
    /// MenuSpace sessions (an overlay on the menu with a lobby behind it) with no roster at all; the lobby list is
    /// the same in both kinds of session. The cost is timing: a joiner appears here when Steam seats them, before
    /// their character spawns in a World session. The world is still used for names: a remote player's replicated
    /// <c>Player.PlayerName</c> carries their session alias, which Steam does not know.
    /// </summary>
    internal static class PlayerTracker
    {
        private const float PollInterval = 0.5f;

        private static readonly List<SessionPlayer> _players = new List<SessionPlayer>();
        private static readonly object _lock = new object();
        private static LaunchContext _ctx;
        private static float _nextPoll;
        private static bool _readFailedLogged;
        private static bool _emptyLogged;
        private static bool _steamIdWarned;
        private static bool _nameWarned;

        internal static int Count { get { lock (_lock) return _players.Count; } }

        internal static IReadOnlyList<SessionPlayer> Snapshot()
        {
            lock (_lock) return new List<SessionPlayer>(_players).AsReadOnly();
        }

        /// <summary>Seed the roster when a session starts. Nobody present now raises PlayerJoined.</summary>
        internal static void Begin(LaunchContext ctx)
        {
            _ctx = ctx;
            _readFailedLogged = false;
            _emptyLogged = false;
            lock (_lock) _players.Clear();
            Poll(raise: false);
            _nextPoll = UnityEngine.Time.unscaledTime + PollInterval;
        }

        /// <summary>Drop the roster when the session ends. SessionEnded covers the remaining players.</summary>
        internal static void End()
        {
            _ctx = null;
            lock (_lock) _players.Clear();
        }

        /// <summary>True when this Steam id is in the current roster.</summary>
        internal static bool Contains(ulong steamId)
        {
            lock (_lock)
                foreach (var p in _players) if (p.SteamId == steamId) return true;
            return false;
        }

        /// <summary>Pumped every frame by ApiPump; polls at <see cref="PollInterval"/>.</summary>
        internal static void Tick()
        {
            if (_ctx == null) return;
            float now = UnityEngine.Time.unscaledTime;
            if (now < _nextPoll) return;
            _nextPoll = now + PollInterval;
            Poll(raise: true);
        }

        private static void Poll(bool raise)
        {
            var ctx = _ctx;
            if (ctx == null) return;
            ulong local = LocalSteamId();

            // Singleplayer: the roster is the local player and never changes.
            if (ctx.LobbyId == 0UL)
            {
                lock (_lock)
                {
                    if (_players.Count == 0) _players.Add(new SessionPlayer(local, true));
                    _players[0].DisplayName = LobbyCoordinator.LocalPersonaName();
                    _players[0].IsHost = false;
                }
                return;
            }

            if (!LobbyMembers.TryRead(ctx.LobbyId, out var members, out ulong owner, out string error))
            {
                if (!_readFailedLogged)
                {
                    _readFailedLogged = true;
                    Core.Log?.Warning($"[api] could not read the lobby members of {ctx.LobbyId}: {error}. The player list keeps its last state.");
                }
                return;
            }
            // An empty member list means we are no longer in the lobby - the session is about to end. Reporting
            // everyone as having left would be wrong; SessionEnded follows.
            if (members.Count == 0)
            {
                if (!_emptyLogged)
                {
                    _emptyLogged = true;
                    Core.Log?.Msg($"[api] lobby {ctx.LobbyId} reports no members; waiting for the session to end.");
                }
                return;
            }

            var joined = new List<SessionPlayer>();
            var left = new List<SessionPlayer>();
            lock (_lock)
            {
                for (int i = _players.Count - 1; i >= 0; i--)
                {
                    if (members.Contains(_players[i].SteamId)) continue;
                    left.Add(_players[i]);
                    _players.RemoveAt(i);
                }
                foreach (ulong id in members)
                {
                    SessionPlayer p = null;
                    foreach (var q in _players) if (q.SteamId == id) { p = q; break; }
                    if (p == null)
                    {
                        p = new SessionPlayer(id, id == local);
                        _players.Add(p);
                        joined.Add(p);
                    }
                    p.IsHost = id == owner;
                    p.DisplayName = NameOf(id, p.IsLocal);
                }
            }

            if (!raise) return;
            foreach (var p in left)
            {
                Core.Log?.Msg($"[api] player left: {p}.");
                SessionEvents.Left.Raise(h => h(p));
            }
            foreach (var p in joined)
            {
                Core.Log?.Msg($"[api] player joined: {p}.");
                SessionEvents.Joined.Raise(h => h(p));
            }
        }

        internal static ulong LocalSteamId()
        {
            try { return SteamUser.GetSteamID().m_SteamID; }
            catch (Exception e)
            {
                if (!_steamIdWarned) { _steamIdWarned = true; Core.Log?.Warning("[api] could not read the local Steam id: " + e.Message); }
                return 0UL;
            }
        }

        private static string NameOf(ulong id, bool local)
        {
            if (local) return LobbyCoordinator.LocalPersonaName();
            string world = WorldName(id);
            if (!string.IsNullOrWhiteSpace(world)) return world;
            try
            {
                string persona = SteamFriends.GetFriendPersonaName(new CSteamID(id));
                if (!string.IsNullOrWhiteSpace(persona)) return persona;
            }
            catch (Exception e) { WarnNameOnce($"Steam persona of {id} unreadable: {e.Message}"); }
            return id.ToString();
        }

        /// <summary>The replicated in-game name of a spawned remote player, or null outside a world.</summary>
        private static string WorldName(ulong id)
        {
            if (!WorldBoot.IsInGame) return null;
            try
            {
                var list = Player.PlayerList;
                if (list == null) return null;
                string code = id.ToString();
                for (int i = 0; i < list.Count; i++)
                {
                    var p = list[i];
                    if (p != null && p.PlayerCode == code) return p.PlayerName;
                }
            }
            catch (Exception e) { WarnNameOnce($"in-world name of {id} unreadable: {e.Message}"); }
            return null;
        }

        // Names are refreshed every poll; a failure that repeats is logged once per game run, not twice a second.
        private static void WarnNameOnce(string what)
        {
            if (_nameWarned) return;
            _nameWarned = true;
            Core.Log?.Warning("[api] " + what + " (further name lookup failures are not logged).");
        }
    }

    /// <summary>Reads the member list of a Steam lobby we are in.</summary>
    internal static class LobbyMembers
    {
        internal static bool TryRead(ulong lobbyId, out HashSet<ulong> members, out ulong owner, out string error)
        {
            members = new HashSet<ulong>();
            owner = 0UL;
            error = null;
            try
            {
                var sid = new CSteamID(lobbyId);
                int n = SteamMatchmaking.GetNumLobbyMembers(sid);
                for (int i = 0; i < n; i++)
                {
                    ulong m = SteamMatchmaking.GetLobbyMemberByIndex(sid, i).m_SteamID;
                    if (m != 0UL) members.Add(m);
                }
                owner = SteamMatchmaking.GetLobbyOwner(sid).m_SteamID;
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }
    }
}
