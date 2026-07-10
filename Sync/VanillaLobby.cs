using System;
using System.Collections.Generic;
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

                WriteChunks(sid, ManifestChunkPrefix, KeyManifestChunks, SyncCodec.Pack(manifestText));
                WriteChunks(sid, PrefsChunkPrefix, KeyPrefsChunks, SyncCodec.Pack(prefsText));
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
