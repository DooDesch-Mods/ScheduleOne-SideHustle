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
        internal const string KeyAdvertise = "sh_adv";   // "1" on a public lobby whose gamemode opted in to discovery
        internal const string KeyUrl = "sh_url";         // where to get the gamemode mod (for the "Download Mod" button)
        internal const string KeyGamemodeFile = "sh_gmfile";   // the gamemode's OWN dll in the published join mod set
        internal const string KeyRuntime = "sh_rt";            // which game branch the host is playing on

        /// <summary>
        /// The game branch this build runs on, written onto every lobby we open.
        ///
        /// Schedule I ships two incompatible branches from the same Steam app - IL2CPP (default) and Mono
        /// (alternate) - and their lobbies sit in the same Steam lobby list. Nothing the game itself publishes
        /// distinguishes them, so a player on the wrong branch can see a lobby, join it, and only find out when
        /// nothing works. One key fixes that, and it has to come from the compiler rather than from anything measured
        /// at runtime: a build knows its own branch for certain.
        ///
        /// A lobby without the key is simply unknown, never assumed - a host on an older Side Hustle writes nothing.
        /// </summary>
        internal const string ThisRuntime =
#if IL2CPP
            "il2cpp";
#else
            "mono";
#endif

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

        /// <summary>The Steam id of the lobby we are in, or 0. NOT Lobby.LobbyID - that property exists but the game
        /// never assigns it, so it always reads 0; the real id lives in SteamLobbyService, which FullHouse resolves.</summary>
        internal static ulong CurrentLobbyId
        {
            get { try { return DooDesch.FullHouse.Lobbies.CurrentLobbyId; } catch { return 0UL; } }
        }

        /// <summary>The current lobby as a Steam id. <see cref="CSteamID.Nil"/> when we are not in a lobby.</summary>
        private static CSteamID CurrentLobbySteamId => new CSteamID(CurrentLobbyId);

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
                CSteamID sid = CurrentLobbySteamId;
                if (sid.m_SteamID == 0UL) { Core.Log?.Warning("[mp] lobby id not resolved yet; skipping the tag pass."); return; }
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
                SteamMatchmaking.SetLobbyData(sid, KeyRuntime, ThisRuntime);
                // Advertise this gamemode's PUBLIC lobbies to players who do not have it installed (a discovery marker
                // + a download link the browser can open). Private lobbies are never advertised - you cannot join them
                // anyway - and a gamemode can opt out with Advertise = false (e.g. a WIP mod not ready to be found).
                if (desc.Advertise && !priv)
                {
                    SteamMatchmaking.SetLobbyData(sid, KeyAdvertise, "1");
                    SteamMatchmaking.SetLobbyData(sid, KeyUrl, desc.DownloadUrl ?? "");
                }
                PublishJoinMods(sid, desc, opts);
            }
            catch (Exception e) { Core.Log?.Warning("[mp] TagLobby failed: " + e.Message); }
        }

        /// <summary>
        /// Advertise the exact mod files a joiner needs for this gamemode (the gamemode's own DLL + its policy's
        /// required mods, each with version + SHA256 + where to get it). That is what lets a player WITHOUT the
        /// gamemode join at all: Side Hustle can fetch precisely the host's build instead of guessing, and a mod
        /// nobody can fetch automatically still shows up on the manual checklist.
        ///
        /// Best-effort and silent on failure: without it the lobby simply behaves as before (installed players only).
        /// </summary>
        private static void PublishJoinMods(CSteamID sid, GamemodeDescriptor desc, HostOptions opts)
        {
            try
            {
                var files = Mods.ModPolicyResolver.RequiredFilesForJoin(desc);
                if (files.Count == 0) return;
                // Name the gamemode's OWN dll so a joiner can tell "the gamemode itself arrived" from "some extra
                // mod did" - restarting without the gamemode would land them in the menu with nothing gained.
                SteamMatchmaking.SetLobbyData(sid, KeyGamemodeFile, Mods.ModPolicyResolver.OwnFileOf(desc) ?? "");
                var index = Profiles.ThunderstoreClient.GetCachedIndexOrNull(Profiles.ProfileEngine.GameRoot);
                var plan = Sync.SyncPublisher.BuildPlan(index, excludeFiles: null, includeFiles: files);
                if (plan.Manifest.Mods.Count == 0) return;
                string manifestText = plan.Manifest.ToCanonicalText();
                string mhash = Sync.VanillaLobby.PublishJoinManifest(sid, manifestText);
                if (string.IsNullOrEmpty(mhash)) return;
                Core.Log?.Msg($"[mp] join mod set published: {plan.Manifest.Mods.Count} file(s) " +
                              $"({plan.AutoCount} auto, {plan.GhCount} github, {plan.LinkCount} link, {plan.DroppedCount} unsourced).");

                // Also list this session in the public directory the website browses - PUBLIC lobbies only, a
                // friends-only session is nobody else's business.
                if (opts.Visibility != LobbyVisibility.Private)
                    PublishDirectoryEntry(sid, desc, opts, manifestText, mhash, plan);
            }
            catch (Exception e) { Core.Log?.Warning("[mp] could not publish the join mod set: " + e.Message); }
        }

        // The directory entry for a hosted gamemode session: same shape the vanilla co-op host publishes, tagged as a
        // gamemode so the website can show what is being played. Carries the join mod set, so the site (and a joiner
        // whose Steam read failed) can tell what the session needs.
        private static void PublishDirectoryEntry(CSteamID sid, GamemodeDescriptor desc, HostOptions opts,
            string manifestText, string mhash, Sync.PublishPlan plan)
        {
            try
            {
                int members = 1;
                try { var l = LobbyOrNull(); if (l != null) members = Math.Max(1, l.PlayerCount); } catch { }
                string summary = $"{plan.Manifest.Mods.Count} mod(s), {plan.AutoCount} auto-installable";
                Sync.VanillaLobby.PublishDirectory(sid.m_SteamID, secret => new Sync.DirPublish
                {
                    LobbyId = sid.m_SteamID.ToString(),
                    OwnerSteamId = SteamUser.GetSteamID().m_SteamID.ToString(),
                    Secret = secret,
                    HostName = LocalPersonaName(),
                    LobbyName = string.IsNullOrEmpty(opts.LobbyName) ? LocalPersonaName() : opts.LobbyName,
                    Kind = "gamemode",
                    Gamemode = desc.Id ?? "",
                    GamemodeName = desc.DisplayName ?? desc.Id ?? "",
                    Enforce = false,
                    MaxPlayers = Math.Max(2, opts.MaxPlayers),
                    Members = members,
                    HasPassword = opts.HasPassword,
                    PwHash = opts.HasPassword ? HashPassword(opts.Password) : "",
                    ModSummary = summary,
                    GameVersion = UnityEngine.Application.version,
                    AppBuild = "",
                    Mhash = mhash,
                    Manifest = manifestText,
                    Prefs = "",
                });
                Core.Log?.Msg($"[mp] lobby listed in the public directory as '{desc.DisplayName}'.");
            }
            catch (Exception e) { Core.Log?.Warning("[mp] directory publish failed: " + e.Message); }
        }

        /// <summary>Join a lobby by id. The game's OnLobbyEntered then drives the client world-load handshake.</summary>
        internal static void JoinLobby(ulong lobbyId)
        {
            try { SteamMatchmaking.JoinLobby(new CSteamID(lobbyId)); }
            catch (Exception e) { Core.Log?.Warning("[mp] JoinLobby failed: " + e.Message); }
        }

        /// <summary>Menu safety net: if we still OWN a Steam lobby but no Side Hustle session is live or starting, it
        /// is a leftover from a prior host (a MenuSpace gamemode, an aborted host, or a co-op host that did not leave
        /// cleanly). The owner leaving DESTROYS the lobby, so a stray one can never be discovered or joined before the
        /// player explicitly hosts again. Host-only (a client's membership is torn down elsewhere); no-op when not in a
        /// lobby. Returns true if it left one.</summary>
        internal static bool LeaveStrayHostLobby()
        {
            var l = LobbyOrNull();
            try
            {
                if (l == null || !l.IsInLobby || !l.IsHost) return false;
                l.LeaveLobby();
                Core.Log?.Msg("[mp] left a stray host lobby at the menu (no active session).");
                return true;
            }
            catch (Exception e) { Core.Log?.Warning("[mp] leaving a stray lobby failed: " + e.Message); return false; }
        }

        /// <summary>Leave whatever lobby we are in, host or client. Used when a join is abandoned before the world
        /// ever came up: the Steam lobby membership survives the failed load, so the host keeps counting a player
        /// who never arrived (and keeps their seat occupied) until this client drops it. Returns true if it left
        /// one.</summary>
        internal static bool LeaveCurrentLobby()
        {
            var l = LobbyOrNull();
            try
            {
                if (l == null || !l.IsInLobby) return false;
                l.LeaveLobby();
                Core.Log?.Msg("[mp] left the lobby after an abandoned join.");
                return true;
            }
            catch (Exception e) { Core.Log?.Warning("[mp] leaving the lobby failed: " + e.Message); return false; }
        }

        /// <summary>Best-effort: stop advertising the lobby before we leave (the host went back to the hub).</summary>
        internal static void Unlist()
        {
            Sync.VanillaLobby.UnpublishDirectory();   // drop the website listing too, even if the Steam lobby is gone
            var l = LobbyOrNull();
            if (l == null || !l.IsInLobby) return;
            try
            {
                CSteamID sid = CurrentLobbySteamId;
                if (sid.m_SteamID == 0UL) return;
                SteamMatchmaking.SetLobbyJoinable(sid, false);
                SteamMatchmaking.SetLobbyData(sid, KeyGamemode, "");
                SteamMatchmaking.SetLobbyData(sid, KeyAdvertise, "");
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
                info.GamemodeId = SteamMatchmaking.GetLobbyData(sid, KeyGamemode);
                info.DownloadUrl = SteamMatchmaking.GetLobbyData(sid, KeyUrl);
                info.Runtime = SteamMatchmaking.GetLobbyData(sid, KeyRuntime);
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
