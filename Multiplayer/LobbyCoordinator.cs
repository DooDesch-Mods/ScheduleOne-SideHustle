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
    ///   sh_gamemode (filter key) · sh_gamemode_name · sh_max · sh_pw · sh_host_name · sh_config · sh_build
    /// </summary>
    internal static class LobbyCoordinator
    {
        internal const string KeyGamemode = "sh_gamemode";
        internal const string KeyGamemodeName = "sh_gamemode_name";
        internal const string KeyMax = "sh_max";
        internal const string KeyPassword = "sh_pw";
        internal const string KeyHostName = "sh_host_name";
        internal const string KeyLobbyName = "sh_name";
        internal const string KeyMode = "sh_mode";
        internal const string KeyConfig = "sh_config";
        internal const string KeyVisibility = "sh_vis";
        internal const string KeyPwHash = "sh_pwhash";
        internal const string KeyBuild = "sh_build";

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

        /// <summary>Ask Steam to create a lobby (Public = browser-listed, Private = friends-only). The game's global
        /// LobbyCreated callback flips the singleton shortly after (poll <see cref="IsInLobby"/>). Leaves any existing
        /// lobby first.</summary>
        internal static bool CreateLobby(int maxPlayers, LobbyVisibility visibility)
        {
            var l = LobbyOrNull();
            if (l == null) { Core.Log?.Warning("[mp] Lobby singleton unavailable; cannot host."); return false; }
            try
            {
                if (l.IsInLobby) l.LeaveLobby();
                var type = visibility == LobbyVisibility.Private ? ELobbyType.k_ELobbyTypeFriendsOnly : ELobbyType.k_ELobbyTypePublic;
                SteamMatchmaking.CreateLobby(type, Math.Max(2, maxPlayers));
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
                bool priv = opts.Visibility == LobbyVisibility.Private;
                SteamMatchmaking.SetLobbyType(sid, priv ? ELobbyType.k_ELobbyTypeFriendsOnly : ELobbyType.k_ELobbyTypePublic);
                SteamMatchmaking.SetLobbyJoinable(sid, true);
                SteamMatchmaking.SetLobbyMemberLimit(sid, Math.Max(2, opts.MaxPlayers));
                SteamMatchmaking.SetLobbyData(sid, KeyGamemode, desc.Id ?? "");
                SteamMatchmaking.SetLobbyData(sid, KeyGamemodeName, desc.DisplayName ?? desc.Id ?? "");
                SteamMatchmaking.SetLobbyData(sid, KeyMax, opts.MaxPlayers.ToString());
                SteamMatchmaking.SetLobbyData(sid, KeyVisibility, priv ? "priv" : "pub");
                SteamMatchmaking.SetLobbyData(sid, KeyPassword, opts.HasPassword ? "1" : "0");
                SteamMatchmaking.SetLobbyData(sid, KeyPwHash, opts.HasPassword ? HashPassword(opts.Password) : "");
                SteamMatchmaking.SetLobbyData(sid, KeyHostName, LocalPersonaName());
                SteamMatchmaking.SetLobbyData(sid, KeyLobbyName, string.IsNullOrEmpty(opts.LobbyName) ? LocalPersonaName() : opts.LobbyName);
                if (!string.IsNullOrEmpty(opts.ModeLabel)) SteamMatchmaking.SetLobbyData(sid, KeyMode, opts.ModeLabel);
                if (!string.IsNullOrEmpty(opts.ConfigBlob))
                    SteamMatchmaking.SetLobbyData(sid, KeyConfig, opts.ConfigBlob);
                SteamMatchmaking.SetLobbyData(sid, KeyBuild, BuildIdOf(desc));
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
                info.LobbyName = SteamMatchmaking.GetLobbyData(sid, KeyLobbyName);
                info.Mode = SteamMatchmaking.GetLobbyData(sid, KeyMode);
                info.HostName = SteamMatchmaking.GetLobbyData(sid, KeyHostName);
                info.HasPassword = SteamMatchmaking.GetLobbyData(sid, KeyPassword) == "1";
                info.PwHash = SteamMatchmaking.GetLobbyData(sid, KeyPwHash);
                info.ConfigBlob = SteamMatchmaking.GetLobbyData(sid, KeyConfig);
                info.BuildId = SteamMatchmaking.GetLobbyData(sid, KeyBuild);
                int.TryParse(SteamMatchmaking.GetLobbyData(sid, KeyMax), out int max);
                info.MaxPlayers = max;
            }
            catch { /* ignore - returns a mostly-empty info */ }
            return info;
        }

        internal static string LocalPersonaName()
        {
            try
            {
                // While aliasing, use the active session alias so the server-browser host name matches the in-game
                // name others see; otherwise the real Steam persona name.
                var alias = PlayerAlias.CurrentAlias;
                return !string.IsNullOrEmpty(alias) ? alias : SteamFriends.GetPersonaName();
            }
            catch { return "Host"; }
        }

        /// <summary>A stable build fingerprint for a gamemode's DLL: the module's ModuleVersionId (MVID), which the
        /// compiler regenerates on every build. Written to the lobby (<c>sh_build</c>) by the host and compared by a
        /// joining client so a version mismatch ("everyone must run the same build") is caught at the join layer for
        /// ALL gamemodes. Empty string if the owner assembly is unknown.</summary>
        internal static string BuildIdOf(GamemodeDescriptor desc)
        {
            try { return desc?.OwnerAssembly?.ManifestModule?.ModuleVersionId.ToString("N") ?? ""; }
            catch { return ""; }
        }

        /// <summary>A stable salted hash of a join password, stored on the lobby so a joining client can verify the
        /// password it was given locally (a casual gate to keep randoms out, not strong cryptography).</summary>
        internal static string HashPassword(string pw)
        {
            if (string.IsNullOrEmpty(pw)) return "";
            try
            {
                var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("sidehustle:" + pw));
                var sb = new System.Text.StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
            catch { return "h" + (("sidehustle:" + pw).GetHashCode() & 0x7fffffff); }   // fallback gate
        }
    }
}
