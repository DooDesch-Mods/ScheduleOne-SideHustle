using System;
using Il2CppScheduleOne.DevUtilities;   // PersistentSingleton<>
using Il2CppScheduleOne.Networking;     // Lobby
using Il2CppSteamworks;                   // SteamMatchmaking, ELobbyType, CSteamID, SteamFriends

namespace SideHustle.Multiplayer
{
    /// <summary>
    /// Steam lobby lifecycle for the gamemode hub. The game registers a GLOBAL
    /// Callback&lt;LobbyCreated_t&gt; in <c>Lobby.InitializeCallbacks</c>, so a lobby WE create via Steamworks still
    /// flips the game's <c>Lobby</c> singleton (IsInLobby/IsHost/LobbyID) - no reflection, and the game's own
    /// FishySteamworks host transport then binds because <c>Lobby.IsInLobby &amp;&amp; Lobby.IsHost</c> is true.
    ///
    /// Namespaced lobby metadata (so the browser can filter and clients can read host options):
    ///   sh_gamemode (filter key) · sh_gamemode_name · sh_max · sh_pw · sh_host_name · sh_config
    /// </summary>
    internal static class LobbyCoordinator
    {
        internal const string KeyGamemode = "sh_gamemode";
        internal const string KeyGamemodeName = "sh_gamemode_name";
        internal const string KeyMax = "sh_max";
        internal const string KeyPassword = "sh_pw";
        internal const string KeyHostName = "sh_host_name";
        internal const string KeyConfig = "sh_config";

        private static Lobby LobbyOrNull()
        {
            try { return PersistentSingleton<Lobby>.Instance; } catch { return null; }
        }

        internal static bool IsInLobby
        {
            get { var l = LobbyOrNull(); try { return l != null && l.IsInLobby; } catch { return false; } }
        }

        internal static bool IsHost
        {
            get { var l = LobbyOrNull(); try { return l != null && l.IsInLobby && l.IsHost; } catch { return false; } }
        }

        internal static ulong CurrentLobbyId
        {
            get { var l = LobbyOrNull(); try { return l != null ? l.LobbyID : 0UL; } catch { return 0UL; } }
        }

        internal static int MemberCount
        {
            get { var l = LobbyOrNull(); try { return l != null ? l.PlayerCount : 1; } catch { return 1; } }
        }

        /// <summary>The true Steam lobby member count (ground truth, independent of the game's fixed Players[] array;
        /// used to verify BiggerLobbies actually seats more than the vanilla 4).</summary>
        internal static int SteamMemberCount
        {
            get { try { return SteamMatchmaking.GetNumLobbyMembers(new CSteamID(CurrentLobbyId)); } catch { return -1; } }
        }

        /// <summary>Ask Steam to create a public lobby. The game's global LobbyCreated callback flips the singleton
        /// shortly after (poll <see cref="IsInLobby"/>). Leaves any existing lobby first.</summary>
        internal static bool CreatePublicLobby(int maxPlayers)
        {
            var l = LobbyOrNull();
            if (l == null) { Core.Log?.Warning("[mp] Lobby singleton unavailable; cannot host."); return false; }
            try
            {
                if (l.IsInLobby) l.LeaveLobby();
                SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, Math.Max(2, maxPlayers));
                return true;
            }
            catch (Exception e) { Core.Log?.Warning("[mp] CreateLobby failed: " + e.Message); return false; }
        }

        /// <summary>Re-affirm public/joinable + write the namespaced metadata so the lobby shows in the browser
        /// and clients can read the host's options. Call once the singleton has flipped (we are in the lobby).</summary>
        internal static void TagLobby(GamemodeDescriptor desc, HostOptions opts)
        {
            var l = LobbyOrNull();
            if (l == null || !l.IsInLobby) return;
            try
            {
                CSteamID sid = l.LobbySteamID;
                SteamMatchmaking.SetLobbyType(sid, ELobbyType.k_ELobbyTypePublic);
                SteamMatchmaking.SetLobbyJoinable(sid, true);
                SteamMatchmaking.SetLobbyMemberLimit(sid, Math.Max(2, opts.MaxPlayers));
                SteamMatchmaking.SetLobbyData(sid, KeyGamemode, desc.Id ?? "");
                SteamMatchmaking.SetLobbyData(sid, KeyGamemodeName, desc.DisplayName ?? desc.Id ?? "");
                SteamMatchmaking.SetLobbyData(sid, KeyMax, opts.MaxPlayers.ToString());
                SteamMatchmaking.SetLobbyData(sid, KeyPassword, opts.HasPassword ? "1" : "0");
                SteamMatchmaking.SetLobbyData(sid, KeyHostName, LocalPersonaName());
                if (!string.IsNullOrEmpty(opts.ConfigBlob))
                    SteamMatchmaking.SetLobbyData(sid, KeyConfig, opts.ConfigBlob);
            }
            catch (Exception e) { Core.Log?.Warning("[mp] TagLobby failed: " + e.Message); }
        }

        /// <summary>Join a lobby by id. The game's OnLobbyEntered then drives the client world-load handshake.</summary>
        internal static void JoinLobby(ulong lobbyId)
        {
            try { SteamMatchmaking.JoinLobby(new CSteamID(lobbyId)); }
            catch (Exception e) { Core.Log?.Warning("[mp] JoinLobby failed: " + e.Message); }
        }

        /// <summary>Best-effort: stop advertising the lobby before we leave (the host went back to the hub).</summary>
        internal static void Unlist()
        {
            var l = LobbyOrNull();
            if (l == null || !l.IsInLobby) return;
            try
            {
                CSteamID sid = l.LobbySteamID;
                SteamMatchmaking.SetLobbyJoinable(sid, false);
                SteamMatchmaking.SetLobbyData(sid, KeyGamemode, "");
            }
            catch { /* ignore */ }
        }

        /// <summary>Read the namespaced metadata for a lobby (used to populate browser rows + the join context).</summary>
        internal static MultiplayerInfo ReadInfo(ulong lobbyId)
        {
            var info = new MultiplayerInfo();
            try
            {
                CSteamID sid = new CSteamID(lobbyId);
                info.GamemodeName = SteamMatchmaking.GetLobbyData(sid, KeyGamemodeName);
                info.HostName = SteamMatchmaking.GetLobbyData(sid, KeyHostName);
                info.HasPassword = SteamMatchmaking.GetLobbyData(sid, KeyPassword) == "1";
                info.ConfigBlob = SteamMatchmaking.GetLobbyData(sid, KeyConfig);
                int.TryParse(SteamMatchmaking.GetLobbyData(sid, KeyMax), out int max);
                info.MaxPlayers = max;
            }
            catch { /* ignore - returns a mostly-empty info */ }
            return info;
        }

        internal static string LocalPersonaName()
        {
            try { return SteamFriends.GetPersonaName(); } catch { return "Host"; }
        }
    }
}
