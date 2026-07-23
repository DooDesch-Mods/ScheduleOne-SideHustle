using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Il2CppScheduleOne.DevUtilities;   // PersistentSingleton<>
using Il2CppScheduleOne.Networking;     // Lobby
using Il2CppSteamworks;
using SideHustle.Multiplayer;

namespace SideHustle.Sync
{
    /// <summary>Browser-card summary of a published vanilla lobby (the cheap keys, never the chunks).</summary>
    internal sealed class VanillaLobbyRow
    {
        public ulong LobbyId;
        public string LobbyName;
        public string HostName;
        public string Org;
        public string ModSummary;   // "synced/auto/manual" counts as published by the host
        public int Members;
        public int MaxPlayers;
        public bool HasPassword;
        public string PwHash;
        public string MHash;
        public bool Enforced;
        /// <summary>Host SteamID from the game's own "owner" lobby key (readable without joining) - the trust key.</summary>
        public ulong OwnerSteamId;
    }

    /// <summary>
    /// The vanilla-lobby key family on the Steam lobby: discovery (sh_vanilla), the chunked manifest/prefs
    /// payloads and their summary keys. Deliberately does NOT set sh_gamemode/sh_adv, so vanilla lobbies never
    /// leak into the gamemode browsers (and vice versa). Composes LobbyCoordinator's helpers; the game's own
    /// global callbacks make a lobby we create here a fully valid vanilla lobby.
    /// </summary>
    internal static class VanillaLobby
    {
        internal const string KeyVanilla = "sh_vanilla";
        internal const string KeyMHash = "sh_mhash";
        internal const string KeyManifestChunks = "sh_mct";
        internal const string KeyPrefsChunks = "sh_pct";
        internal const string KeyModSummary = "sh_msum";
        internal const string KeyOrg = "sh_org";
        internal const string KeyEnforce = "sh_enf";
        internal const string ManifestChunkPrefix = "sh_m";
        internal const string PrefsChunkPrefix = "sh_p";

        // Backend directory (fallback) publish state for the current host session.
        private static string _dirSecret;
        private static ulong _dirLobbyId;

        private static string AppBuildId()
        {
            try { return typeof(Core).Assembly.ManifestModule.ModuleVersionId.ToString("N"); } catch { return ""; }
        }

        private static Lobby LobbyOrNull()
        {
            try { return PersistentSingleton<Lobby>.Instance; } catch { return null; }
        }

        /// <summary>Write the vanilla-lobby metadata onto the CURRENT lobby (we must be in/owning it).</summary>
        internal static bool Tag(HostOptions opts, string manifestText, string prefsText, bool enforce,
            string orgName, string modSummary)
        {
            var l = LobbyOrNull();
            if (l == null || !l.IsInLobby) return false;
            try
            {
                CSteamID sid = l.LobbySteamID;
                bool priv = opts.Visibility == LobbyVisibility.Private;
                SteamMatchmaking.SetLobbyType(sid, priv ? ELobbyType.k_ELobbyTypeFriendsOnly : ELobbyType.k_ELobbyTypePublic);
                SteamMatchmaking.SetLobbyJoinable(sid, true);
                SteamMatchmaking.SetLobbyMemberLimit(sid, Math.Max(2, opts.MaxPlayers));

                // Vanilla gates lobby ENTRY on the "version" key: Lobby.OnLobbyEntered bounces any joiner whose
                // Application.version differs from the lobby's "version" value. Vanilla only writes that key from its
                // own global LobbyCreated_t callback, which does not reliably fire for a lobby the mod created itself
                // (a joiner then reads an empty version and "Lobby version mismatch, cannot join"). Write it here so
                // the lobby is always a valid, joinable vanilla lobby regardless of the callback's timing.
                SteamMatchmaking.SetLobbyData(sid, "version", UnityEngine.Application.version);

                SteamMatchmaking.SetLobbyData(sid, KeyVanilla, "1");
                SteamMatchmaking.SetLobbyData(sid, LobbyCoordinator.KeyMax, opts.MaxPlayers.ToString());
                SteamMatchmaking.SetLobbyData(sid, LobbyCoordinator.KeyVisibility, priv ? "priv" : "pub");
                SteamMatchmaking.SetLobbyData(sid, LobbyCoordinator.KeyPassword, opts.HasPassword ? "1" : "0");
                SteamMatchmaking.SetLobbyData(sid, LobbyCoordinator.KeyPwHash, opts.HasPassword ? LobbyCoordinator.HashPassword(opts.Password) : "");
                SteamMatchmaking.SetLobbyData(sid, LobbyCoordinator.KeyHostName, LobbyCoordinator.LocalPersonaName());
                SteamMatchmaking.SetLobbyData(sid, LobbyCoordinator.KeyLobbyName,
                    string.IsNullOrEmpty(opts.LobbyName) ? LobbyCoordinator.LocalPersonaName() : opts.LobbyName);
                SteamMatchmaking.SetLobbyData(sid, KeyOrg, orgName ?? "");
                SteamMatchmaking.SetLobbyData(sid, KeyEnforce, enforce ? "1" : "0");
                SteamMatchmaking.SetLobbyData(sid, KeyModSummary, modSummary ?? "");
                SteamMatchmaking.SetLobbyData(sid, KeyMHash, SyncCodec.Hash(manifestText, prefsText));

                var mChunks = SyncCodec.Pack(manifestText);
                var pChunks = SyncCodec.Pack(prefsText);
                WriteChunks(sid, ManifestChunkPrefix, KeyManifestChunks, mChunks);
                WriteChunks(sid, PrefsChunkPrefix, KeyPrefsChunks, pChunks);
                int maxChunk = 0; foreach (var c in mChunks) if (c.Length > maxChunk) maxChunk = c.Length;
                Core.Log?.Msg($"[sync] vanilla lobby published (version={UnityEngine.Application.version}, enforce={enforce}, " +
                              $"manifest {manifestText.Length} chars -> {mChunks.Length} chunk(s), biggest {maxChunk}b, prefs {pChunks.Length} chunk(s)).");

                // Also publish to the backend directory as a FALLBACK (a joiner reads Steam first; the backend only
                // rescues a too-large-for-Steam manifest). Off-main-thread, best-effort - Steam is the source of truth.
                try
                {
                    if (string.IsNullOrEmpty(_dirSecret)) _dirSecret = Guid.NewGuid().ToString("N");
                    _dirLobbyId = sid.m_SteamID;
                    var pub = new DirPublish
                    {
                        LobbyId = sid.m_SteamID.ToString(),
                        OwnerSteamId = SteamUser.GetSteamID().m_SteamID.ToString(),
                        Secret = _dirSecret,
                        HostName = LobbyCoordinator.LocalPersonaName(),
                        LobbyName = string.IsNullOrEmpty(opts.LobbyName) ? LobbyCoordinator.LocalPersonaName() : opts.LobbyName,
                        Kind = "vanilla",
                        Gamemode = "", GamemodeName = "",
                        Enforce = enforce,
                        MaxPlayers = Math.Max(2, opts.MaxPlayers),
                        Members = Math.Max(1, l.PlayerCount),
                        HasPassword = opts.HasPassword,
                        PwHash = opts.HasPassword ? LobbyCoordinator.HashPassword(opts.Password) : "",
                        ModSummary = modSummary ?? "",
                        GameVersion = UnityEngine.Application.version,
                        AppBuild = AppBuildId(),
                        Mhash = SyncCodec.Hash(manifestText, prefsText),
                        Manifest = manifestText,
                        Prefs = prefsText ?? "",
                    };
                    Task.Run(() => LobbyDirectory.PublishAsync(pub));
                }
                catch (Exception e) { Core.Log?.Warning("[dir] publish build failed: " + e.Message); }
                return true;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[sync] tagging the vanilla lobby failed: " + e.Message);
                return false;
            }
        }

        /// <summary>Stop advertising (host went back to the menu). The lobby itself dies with the session.</summary>
        internal static void Untag()
        {
            // Drop the backend directory entry first, independent of whether the Steam lobby is still around.
            if (_dirLobbyId != 0)
            {
                string id = _dirLobbyId.ToString(); string sec = _dirSecret;
                Task.Run(() => LobbyDirectory.RemoveAsync(id, sec));
                _dirLobbyId = 0; _dirSecret = null;
            }
            var l = LobbyOrNull();
            if (l == null || !l.IsInLobby) return;
            try
            {
                CSteamID sid = l.LobbySteamID;
                SteamMatchmaking.SetLobbyData(sid, KeyVanilla, "");
                SteamMatchmaking.SetLobbyJoinable(sid, false);
            }
            catch { /* ignore */ }
        }

        /// <summary>Refresh the backend directory entry for the live host session (keeps it from expiring + updates the
        /// member count). No-op unless we published one. Pumped from the host session tick.</summary>
        internal static void HeartbeatDirectory()
        {
            if (_dirLobbyId == 0 || string.IsNullOrEmpty(_dirSecret)) return;
            int members = 1;
            try { var l = LobbyOrNull(); if (l != null) members = Math.Max(1, l.PlayerCount); } catch { }
            string id = _dirLobbyId.ToString(); string sec = _dirSecret;
            Task.Run(() => LobbyDirectory.HeartbeatAsync(id, sec, members));
        }

        private static float _hbTimer;

        /// <summary>Pumped every frame from Core.OnUpdate. Refreshes the backend directory entry on a 30s cadence for
        /// as long as this host has one published - deliberately independent of the sync session state, so a
        /// live-published lobby (LivePublish, which is NOT a Side Hustle-hosted session) keeps its listing alive
        /// instead of silently dropping off the web directory after the ~90s TTL.</summary>
        internal static void HeartbeatTick(float dt)
        {
            if (_dirLobbyId == 0 || string.IsNullOrEmpty(_dirSecret)) { _hbTimer = 0f; return; }
            _hbTimer += dt;
            if (_hbTimer >= 30f) { _hbTimer = 0f; HeartbeatDirectory(); }
        }

        /// <summary>Fallback manifest read: fetch from the backend directory and accept it ONLY if it hashes to the
        /// mhash the host wrote to the real Steam lobby (Steam-authenticated to the owner) - an untrusted cache can
        /// never feed a forged mod list. Returns null when unavailable or the hash does not match.</summary>
        internal static async Task<DirManifest> TryReadFromDirectoryAsync(ulong lobbyId)
        {
            try
            {
                var resp = await LobbyDirectory.FetchManifestAsync(lobbyId.ToString()).ConfigureAwait(false);
                if (resp == null || string.IsNullOrEmpty(resp.Manifest)) return null;
                string steamMhash = SteamMatchmaking.GetLobbyData(new CSteamID(lobbyId), KeyMHash);
                string prefs = resp.Prefs ?? "";
                string computed = SyncCodec.Hash(resp.Manifest, prefs);
                if (string.IsNullOrEmpty(steamMhash) || !string.Equals(computed, steamMhash, StringComparison.Ordinal))
                {
                    Core.Log?.Warning($"[dir] backend manifest hash '{computed}' != Steam mhash '{steamMhash}' - rejecting.");
                    return null;
                }
                var manifest = SyncManifest.Parse(resp.Manifest);
                if (manifest == null) return null;
                return new DirManifest { Manifest = manifest, Prefs = prefs, Mhash = steamMhash };
            }
            catch (Exception e) { Core.Log?.Warning("[dir] directory read failed: " + e.Message); return null; }
        }

        private static void WriteChunks(CSteamID sid, string prefix, string countKey, string[] chunks)
        {
            SteamMatchmaking.SetLobbyData(sid, countKey, chunks.Length.ToString());
            for (int i = 0; i < chunks.Length; i++)
                SteamMatchmaking.SetLobbyData(sid, prefix + i, chunks[i]);
        }

        internal static VanillaLobbyRow ReadSummary(ulong lobbyId)
        {
            var row = new VanillaLobbyRow { LobbyId = lobbyId };
            try
            {
                CSteamID sid = new CSteamID(lobbyId);
                row.LobbyName = SteamMatchmaking.GetLobbyData(sid, LobbyCoordinator.KeyLobbyName);
                row.HostName = SteamMatchmaking.GetLobbyData(sid, LobbyCoordinator.KeyHostName);
                row.Org = SteamMatchmaking.GetLobbyData(sid, KeyOrg);
                row.ModSummary = SteamMatchmaking.GetLobbyData(sid, KeyModSummary);
                row.HasPassword = SteamMatchmaking.GetLobbyData(sid, LobbyCoordinator.KeyPassword) == "1";
                row.PwHash = SteamMatchmaking.GetLobbyData(sid, LobbyCoordinator.KeyPwHash);
                row.MHash = SteamMatchmaking.GetLobbyData(sid, KeyMHash);
                row.Enforced = SteamMatchmaking.GetLobbyData(sid, KeyEnforce) == "1";
                ulong.TryParse(SteamMatchmaking.GetLobbyData(sid, "owner"), out row.OwnerSteamId);   // the game's own key
                int.TryParse(SteamMatchmaking.GetLobbyData(sid, LobbyCoordinator.KeyMax), out row.MaxPlayers);
                try { row.Members = SteamMatchmaking.GetNumLobbyMembers(sid); } catch { row.Members = 1; }
            }
            catch { /* mostly-empty row */ }
            return row;
        }

        /// <summary>
        /// Read + validate the full payloads of a lobby. Returns false when any chunk is missing/corrupt or the
        /// hash does not match (truncation/tamper) - the caller must then treat the lobby as "manifest unreadable"
        /// (join without sync only), never as an empty mod set.
        /// </summary>
        internal static bool TryReadPayloads(ulong lobbyId, out SyncManifest manifest, out string prefsText, out string mhash)
        {
            manifest = null; prefsText = null; mhash = null;
            try
            {
                CSteamID sid = new CSteamID(lobbyId);
                mhash = SteamMatchmaking.GetLobbyData(sid, KeyMHash);
                string manifestText = ReadChunks(sid, ManifestChunkPrefix, KeyManifestChunks);
                prefsText = ReadChunks(sid, PrefsChunkPrefix, KeyPrefsChunks);
                if (manifestText == null || prefsText == null) return false;
                if (string.IsNullOrEmpty(mhash) || SyncCodec.Hash(manifestText, prefsText) != mhash) return false;
                manifest = SyncManifest.Parse(manifestText);
                return manifest != null;
            }
            catch { return false; }
        }

        /// <summary>Diagnostic: describe exactly why a lobby's manifest read is failing (missing/short chunk vs hash
        /// mismatch vs parse), so a stuck "Sync unavailable" tells us whether it is propagation, size, or content.</summary>
        internal static string DescribeReadFailure(ulong lobbyId)
        {
            try
            {
                CSteamID sid = new CSteamID(lobbyId);
                string mct = SteamMatchmaking.GetLobbyData(sid, KeyManifestChunks);
                string mhash = SteamMatchmaking.GetLobbyData(sid, KeyMHash);
                string m0 = SteamMatchmaking.GetLobbyData(sid, ManifestChunkPrefix + "0");
                string manifestText = ReadChunks(sid, ManifestChunkPrefix, KeyManifestChunks);
                string prefsText = ReadChunks(sid, PrefsChunkPrefix, KeyPrefsChunks);
                string computed = (manifestText != null && prefsText != null) ? SyncCodec.Hash(manifestText, prefsText) : "?";
                return $"mct='{mct}', mhash='{mhash}', m0len={(m0 == null ? -1 : m0.Length)}, " +
                       $"manifest={(manifestText == null ? "null" : manifestText.Length.ToString())}, " +
                       $"prefs={(prefsText == null ? "null" : prefsText.Length.ToString())}, computed='{computed}'";
            }
            catch (Exception e) { return "describe failed: " + e.Message; }
        }

        private static string ReadChunks(CSteamID sid, string prefix, string countKey)
        {
            if (!int.TryParse(SteamMatchmaking.GetLobbyData(sid, countKey), out int count) || count < 0 || count > 64)
                return null;
            var chunks = new List<string>(count);
            for (int i = 0; i < count; i++)
                chunks.Add(SteamMatchmaking.GetLobbyData(sid, prefix + i));
            return SyncCodec.Unpack(chunks);
        }
    }
}
