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
        /// <summary>The game's own "ready" key: the host's world is up and clients are told to load on entry.</summary>
        /// <summary>The host advertises that strangers may write to them. Absent on an older host, which reads as
        /// false: offering to message someone whose build cannot receive it is the one wrong answer.</summary>
        public bool AcceptsMessages;

        public bool HostReady;
        /// <summary>The game's own "host_loading" key: the host is still loading; a joiner waits on their screen.</summary>
        public bool HostLoading;
        /// <summary>Host's game branch (sh_rt): "il2cpp", "mono", or empty from a host on an older build.</summary>
        public string Runtime;
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

        /// <summary>Whether this host takes messages from people who cannot join. A local preference on their side,
        /// advertised here because the only person who needs it is the joiner deciding whether asking is even an
        /// option - a Chat button that opens onto silence is worse than no button.</summary>
        internal const string KeyMessages = "sh_msg";
        internal const string ManifestChunkPrefix = "sh_m";
        internal const string PrefsChunkPrefix = "sh_p";

        // Backend directory (fallback) publish state for the current host session.
        private static string _dirSecret;
        private static ulong _dirLobbyId;
        private static Func<string, DirPublish> _dirBuild;   // how to rebuild this entry if the backend loses it

        private static string AppBuildId()
        {
            try { return typeof(Core).Assembly.ManifestModule.ModuleVersionId.ToString("N"); } catch { return ""; }
        }

        private static Lobby LobbyOrNull()
        {
            try { return PersistentSingleton<Lobby>.Instance; } catch { return null; }
        }

        /// <summary>
        /// Steam carries a lobby's WHOLE key/value set in one 8 KB metadata blob, so every key spends from the same
        /// budget - vanilla's own five writes included. Past it SetLobbyData simply answers false, and what gets
        /// refused is whatever happened to be written last.
        /// </summary>
        private const int LobbyDataBudget = 8192;

        /// <summary>Left untouched for vanilla's keys (owner, version, host_loading, ready, load_tutorial), Steam's
        /// per-key overhead, and the renames, seat changes and passwords the host sets later from the phone app. A
        /// lobby that spends its last byte on the mod list is a lobby that cannot be renamed.</summary>
        private const int BudgetReserve = 1500;

        /// <summary>
        /// Write the vanilla-lobby metadata onto the CURRENT lobby (we must be in/owning it). True when the mod-set
        /// hash is on the lobby and read back correctly - which is to say, when a joiner can sync with this session
        /// at all. Everything else is advertisement and is written best-effort.
        /// </summary>
        internal static bool Tag(HostOptions opts, string manifestText, string prefsText, bool enforce,
            string orgName, string modSummary)
        {
            var l = LobbyOrNull();
            if (l == null || !l.IsInLobby) return false;
            try
            {
                CSteamID sid = new CSteamID(LobbyCoordinator.CurrentLobbyId);
                if (sid.m_SteamID == 0UL) return false;   // IsInLobby can be true a beat before the id resolves
                bool priv = opts.Visibility == LobbyVisibility.Private;
                SteamMatchmaking.SetLobbyType(sid, priv ? ELobbyType.k_ELobbyTypeFriendsOnly : ELobbyType.k_ELobbyTypePublic);
                SteamMatchmaking.SetLobbyJoinable(sid, true);
                SteamMatchmaking.SetLobbyMemberLimit(sid, Math.Max(2, opts.MaxPlayers));

                int spent = 0;
                bool Put(string key, string value)
                {
                    value = value ?? "";
                    if (SteamMatchmaking.SetLobbyData(sid, key, value)) { spent += key.Length + value.Length; return true; }
                    Core.Log?.Warning($"[sync] Steam refused lobby key '{key}' ({value.Length} chars) "
                                      + $"after {spent} chars of lobby data.");
                    return false;
                }

                // Free whatever payload is already on this lobby before measuring anything. Those bytes still count,
                // so a re-tag that skipped this would size the new payload against a budget the old one is holding.
                ClearPayload(sid);

                string mhash = SyncCodec.Hash(manifestText, prefsText);

                // Vanilla gates lobby ENTRY on the "version" key: SteamLobbyService.OnLobbyEntered bounces any joiner
                // whose Application.version differs from the lobby's "version" value. Vanilla writes that key from a
                // global LobbyCreated_t callback, so for a lobby the mod created itself the timing is not guaranteed -
                // and when Steam was down at Lobby.Start the game runs MockLobbyService, which registers no callbacks
                // at all and never writes it. Writing the same value here is idempotent and keeps the lobby joinable
                // either way; without it a joiner reads an empty version and gets "Lobby version mismatch".
                Put("version", UnityEngine.Application.version);

                Put(KeyVanilla, "1");
                Put(LobbyCoordinator.KeyMax, opts.MaxPlayers.ToString());
                Put(LobbyCoordinator.KeyVisibility, priv ? "priv" : "pub");
                Put(LobbyCoordinator.KeyPassword, opts.HasPassword ? "1" : "0");
                Put(LobbyCoordinator.KeyPwHash, opts.HasPassword ? LobbyCoordinator.HashPassword(opts.Password) : "");
                Put(LobbyCoordinator.KeyHostName, LobbyCoordinator.LocalPersonaName());
                Put(LobbyCoordinator.KeyLobbyName,
                    string.IsNullOrEmpty(opts.LobbyName) ? LobbyCoordinator.LocalPersonaName() : opts.LobbyName);
                Put(LobbyCoordinator.KeyRuntime, LobbyCoordinator.ThisRuntime);
                Put(KeyEnforce, enforce ? "1" : "0");

                // The hash goes in before the browser-card text and long before anything bulky. It is sixteen
                // characters and it is the only thing that lets a joiner accept the backend copy of the mod list, so
                // it must never be the write that runs out of room - which is exactly what it was: a host syncing
                // 54 KB of preferences filled the lobby with chunks, and the hash after them was refused. The
                // session then advertised a mod requirement no joiner could read, let alone satisfy.
                bool hashOk = Put(KeyMHash, mhash);

                Put(KeyOrg, orgName);
                Put(KeyModSummary, modSummary);
                Put(KeyMessages, Config.Preferences.AcceptStrangerMessages ? "1" : "0");

                var mChunks = SyncCodec.Pack(manifestText);
                var pChunks = SyncCodec.Pack(prefsText);
                int payloadCost = ChunkCost(ManifestChunkPrefix, KeyManifestChunks, mChunks)
                                  + ChunkCost(PrefsChunkPrefix, KeyPrefsChunks, pChunks);
                bool payloadFits = spent + payloadCost <= LobbyDataBudget - BudgetReserve;
                bool payloadOk = false;
                if (payloadFits)
                {
                    // All or nothing: the hash covers manifest AND prefs together, so half a payload validates
                    // against nothing and only spends budget a rename will want later.
                    payloadOk = WriteChunks(sid, ManifestChunkPrefix, KeyManifestChunks, mChunks)
                                & WriteChunks(sid, PrefsChunkPrefix, KeyPrefsChunks, pChunks);
                    if (!payloadOk) ClearPayload(sid);
                }

                // Read the hash back instead of trusting the write. This is the one key a joiner cannot do without,
                // and a payload that was just cleared may have freed the room it needs.
                if (!hashOk || SteamMatchmaking.GetLobbyData(sid, KeyMHash) != mhash)
                    hashOk = Put(KeyMHash, mhash) && SteamMatchmaking.GetLobbyData(sid, KeyMHash) == mhash;

                int maxChunk = 0; foreach (var c in mChunks) if (c.Length > maxChunk) maxChunk = c.Length;
                Core.Log?.Msg($"[sync] vanilla lobby published (version={UnityEngine.Application.version}, enforce={enforce}, "
                              + $"manifest {manifestText.Length} chars -> {mChunks.Length} chunk(s), biggest {maxChunk}b, "
                              + $"prefs {pChunks.Length} chunk(s), {spent} of {LobbyDataBudget - BudgetReserve} chars used).");
                if (!payloadOk)
                    Core.Log?.Msg($"[sync] the mod list needs {payloadCost} chars and Steam's whole lobby carries "
                                  + $"{LobbyDataBudget - BudgetReserve} - joiners read the backend copy and check it "
                                  + "against the published hash instead.");
                if (!hashOk)
                    Core.Log?.Warning("[sync] Steam refused the mod-set hash - joiners cannot sync with this lobby. "
                                      + "Re-publishing from the Lobby app is the way back.");

                // Also publish to the backend directory as a FALLBACK (a joiner reads Steam first; the backend only
                // rescues a too-large-for-Steam manifest). Off-main-thread, best-effort - Steam is the source of truth.
                try
                {
                    PublishDirectory(sid.m_SteamID, secret => new DirPublish
                    {
                        LobbyId = sid.m_SteamID.ToString(),
                        OwnerSteamId = SteamUser.GetSteamID().m_SteamID.ToString(),
                        Secret = secret,
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
                        Mhash = mhash,
                        Manifest = manifestText,
                        Prefs = prefsText ?? "",
                    });
                }
                catch (Exception e) { Core.Log?.Warning("[dir] publish build failed: " + e.Message); }
                return hashOk;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[sync] tagging the vanilla lobby failed: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Publish a mod set onto ANY lobby we own (used by gamemode lobbies, which advertise just the files a joiner
        /// needs for that gamemode). Writes the same chunked keys the vanilla path uses, so a client reads it back
        /// with <see cref="TryReadPayloads"/> unchanged. Prefs are empty here - a gamemode carries its settings in its
        /// own config blob. Returns the manifest hash, or "" when nothing was published.
        /// </summary>
        internal static string PublishJoinManifest(CSteamID sid, string manifestText)
        {
            if (string.IsNullOrEmpty(manifestText)) return "";
            try
            {
                string mhash = SyncCodec.Hash(manifestText, "");
                var mChunks = SyncCodec.Pack(manifestText);
                var pChunks = SyncCodec.Pack("");
                // Free the previous payload first. Its bytes count against the lobby's 8 KB whether or not any count
                // key still points at them, so re-publishing over it is how a lobby runs out of room for its own
                // mod set (see LobbyDataBudget).
                ClearPayload(sid);
                // Write the payload FIRST and only claim the hash when every chunk landed: Steam can reject a lobby
                // data write (size limits, transient failure), and a half-written manifest that still advertises a
                // hash is worse than none - a joiner would keep retrying a payload that can never validate.
                if (!WriteChunks(sid, ManifestChunkPrefix, KeyManifestChunks, mChunks)
                    || !WriteChunks(sid, PrefsChunkPrefix, KeyPrefsChunks, pChunks))
                {
                    SteamMatchmaking.SetLobbyData(sid, KeyMHash, "");
                    SteamMatchmaking.SetLobbyData(sid, KeyManifestChunks, "");
                    Core.Log?.Warning("[sync] the lobby refused part of the mod set - not advertising it.");
                    return "";
                }
                if (!SteamMatchmaking.SetLobbyData(sid, KeyMHash, mhash))
                {
                    Core.Log?.Warning("[sync] the lobby refused the mod-set hash - not advertising it.");
                    SteamMatchmaking.SetLobbyData(sid, KeyManifestChunks, "");
                    return "";
                }
                return mhash;
            }
            catch (Exception e) { Core.Log?.Warning("[sync] publishing the gamemode mod set failed: " + e.Message); return ""; }
        }

        /// <summary>
        /// Publish ANY lobby we own to the backend directory (what the website's lobby browser lists) and take over
        /// the heartbeat for it. Shared by the vanilla co-op host and the gamemode host - the directory has always
        /// modelled both kinds, only nothing published the gamemode ones.
        /// </summary>
        internal static void PublishDirectory(ulong lobbyId, Func<string, DirPublish> build)
        {
            if (build == null) return;
            if (string.IsNullOrEmpty(_dirSecret)) _dirSecret = Guid.NewGuid().ToString("N");
            _dirLobbyId = lobbyId;
            _dirBuild = build;
            _dirGen++;   // invalidates any heartbeat still in flight for the previous listing   // kept so a heartbeat that finds the entry gone can rebuild it
            var pub = build(_dirSecret);
            Task.Run(() => LobbyDirectory.PublishAsync(pub));
        }

        /// <summary>Drop this host's directory entry (session over). Safe to call when nothing was published.</summary>
        internal static void UnpublishDirectory()
        {
            if (_dirLobbyId == 0) return;
            string id = _dirLobbyId.ToString(); string sec = _dirSecret;
            Task.Run(() => LobbyDirectory.RemoveAsync(id, sec));
            _dirLobbyId = 0; _dirSecret = null; _dirBuild = null; _dirGen++;
        }

        /// <summary>Drop the directory entry and WAIT briefly for the request to land. Only for application quit: the
        /// normal fire-and-forget removal never completes when the process is tearing down, which is why a host who
        /// alt-F4s used to sit on the website until the 90s TTL swept them.</summary>
        internal static void UnpublishDirectoryBlocking()
        {
            if (_dirLobbyId == 0) return;
            string id = _dirLobbyId.ToString(); string sec = _dirSecret;
            _dirLobbyId = 0; _dirSecret = null; _dirBuild = null; _dirGen++;
            try { LobbyDirectory.RemoveAsync(id, sec).Wait(TimeSpan.FromSeconds(2)); } catch { /* quitting anyway */ }
        }

        /// <summary>Stop advertising (host went back to the menu). The lobby itself dies with the session.</summary>
        internal static void Untag()
        {
            // Drop the backend directory entry first, independent of whether the Steam lobby is still around.
            UnpublishDirectory();
            var l = LobbyOrNull();
            if (l == null || !l.IsInLobby) return;
            try
            {
                CSteamID sid = new CSteamID(LobbyCoordinator.CurrentLobbyId);
                if (sid.m_SteamID == 0UL) return;
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
            var build = _dirBuild;
            ulong lobby = _dirLobbyId;
            int gen = _dirGen;
            Task.Run(async () =>
            {
                bool known = await LobbyDirectory.HeartbeatAsync(id, sec, members).ConfigureAwait(false);
                // The backend keeps its directory in memory, so a redeploy drops every entry while the hosts are
                // still playing. A 404 means "you are no longer listed" - re-publish instead of heartbeating into
                // the void until the host re-hosts.
                if (known || build == null) return;
                // ...but only if this listing is still OURS. Teardown can run while the request is in flight, and
                // re-publishing then resurrects a lobby that has ended - with the local state already cleared, nothing
                // is left that could withdraw it again, so it stays advertised until the backend expires it.
                if (gen != _dirGen || _dirLobbyId != lobby || _dirSecret != sec) return;
                try { await LobbyDirectory.PublishAsync(build(sec)).ConfigureAwait(false); } catch { }
            });
        }

        /// <summary>Bumped whenever the published listing changes or is withdrawn, so an in-flight heartbeat can
        /// tell whether the entry it is about to re-publish is still the current one.</summary>
        private static int _dirGen;

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

        /// <summary>Write a chunked payload. False when Steam rejected ANY chunk - a partial payload can never be
        /// reassembled by a joiner, so the caller must not advertise it as published.</summary>
        private static bool WriteChunks(CSteamID sid, string prefix, string countKey, string[] chunks)
        {
            bool ok = SteamMatchmaking.SetLobbyData(sid, countKey, chunks.Length.ToString());
            for (int i = 0; i < chunks.Length; i++)
                ok &= SteamMatchmaking.SetLobbyData(sid, prefix + i, chunks[i]);
            return ok;
        }

        /// <summary>
        /// Whether this lobby has told us the payload is NOT on Steam: it carries the mod-set hash but no chunk count.
        /// A host whose mod list is too big for Steam's 8 KB publishes the hash alone, and re-reading Steam for five
        /// seconds only delays the backend read that was always going to be the one that answers.
        ///
        /// Both halves matter. Before the lobby data arrives, everything reads empty - including the hash - so the
        /// missing chunk count on its own means "not known yet", not "not published".
        /// </summary>
        internal static bool PayloadOnBackendOnly(ulong lobbyId)
        {
            try
            {
                var sid = new CSteamID(lobbyId);
                if (string.IsNullOrEmpty(SteamMatchmaking.GetLobbyData(sid, KeyMHash))) return false;
                int.TryParse(SteamMatchmaking.GetLobbyData(sid, KeyManifestChunks), out int count);
                return count <= 0;
            }
            catch { return false; }
        }

        /// <summary>The mod-set hash the CURRENT lobby actually advertises, or "" when it carries none. What a joiner
        /// reads, so it is also the only honest thing to arm the sync gate with.</summary>
        internal static string PublishedMHash()
        {
            try
            {
                CSteamID sid = new CSteamID(LobbyCoordinator.CurrentLobbyId);
                if (sid.m_SteamID == 0UL) return "";
                return SteamMatchmaking.GetLobbyData(sid, KeyMHash) ?? "";
            }
            catch { return ""; }
        }

        /// <summary>Move the mod-set requirement on the card without touching the gate. The pair belongs together
        /// (SyncCoordinator.SetEnforce owns that), but a session that has just discovered it cannot enforce needs the
        /// advertisement corrected on its own.</summary>
        internal static void AdvertiseEnforce(bool enforce)
        {
            try
            {
                CSteamID sid = new CSteamID(LobbyCoordinator.CurrentLobbyId);
                if (sid.m_SteamID != 0UL) SteamMatchmaking.SetLobbyData(sid, KeyEnforce, enforce ? "1" : "0");
            }
            catch (Exception e) { Core.Log?.Warning("[sync] could not update the mod-set flag: " + e.Message); }
        }

        /// <summary>What a chunked payload costs against <see cref="LobbyDataBudget"/>, keys included.</summary>
        private static int ChunkCost(string prefix, string countKey, string[] chunks)
        {
            int cost = countKey.Length + 2;
            for (int i = 0; i < chunks.Length; i++) cost += prefix.Length + 2 + chunks[i].Length;
            return cost;
        }

        /// <summary>
        /// Erase the chunked manifest and prefs from a lobby, values included.
        ///
        /// Clearing the two count keys alone would be enough for a reader - it takes the count as authoritative - but
        /// not for the budget: the chunk values keep their bytes, and those bytes are what the next write runs out of.
        /// </summary>
        private static void ClearPayload(CSteamID sid)
        {
            ClearChunkFamily(sid, ManifestChunkPrefix, KeyManifestChunks);
            ClearChunkFamily(sid, PrefsChunkPrefix, KeyPrefsChunks);
        }

        private static void ClearChunkFamily(CSteamID sid, string prefix, string countKey)
        {
            try
            {
                int.TryParse(SteamMatchmaking.GetLobbyData(sid, countKey), out int count);
                SteamMatchmaking.SetLobbyData(sid, countKey, "");
                // One past the advertised count, because a shorter payload than last time leaves a stray chunk that
                // no count points at and that nothing would ever clear.
                for (int i = 0; i <= count; i++) SteamMatchmaking.SetLobbyData(sid, prefix + i, "");
            }
            catch (Exception e) { Core.Log?.Warning($"[sync] could not clear '{countKey}': {e.Message}"); }
        }

        /// <summary>
        /// Whether entering this lobby will actually get the player into a game.
        ///
        /// Vanilla starts a joining client's load from SteamLobbyService.OnLobbyEntered, and only when one of the
        /// host's own lobby keys says so: "ready", "host_loading" or "load_tutorial". None of them set means the
        /// client enters the lobby, takes a seat, and then sits in the menu forever with nothing on screen.
        ///
        /// That is not a rare edge. "ready" is written in exactly one place in the whole game - at the END of the
        /// host's world load, and only while they are already in a lobby - so any lobby opened after the host was
        /// already playing keeps the "false" that OnLobbyCreated wrote, permanently. Side Hustle's own live-publish
        /// button now sets the key itself, but a host on an older build never will, which is what this check is for:
        /// their lobby is real, discoverable and unjoinable, and the player deserves to learn that from the card
        /// instead of from a two-minute restart that ends in a dead menu.
        /// </summary>
        internal static bool AcceptsJoiners(VanillaLobbyRow row) => row != null && (row.HostReady || row.HostLoading);

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
                row.HostReady = SteamMatchmaking.GetLobbyData(sid, "ready") == "true";               // ditto
                row.HostLoading = SteamMatchmaking.GetLobbyData(sid, "host_loading") == "true";
                row.Runtime = SteamMatchmaking.GetLobbyData(sid, LobbyCoordinator.KeyRuntime);
                row.AcceptsMessages = SteamMatchmaking.GetLobbyData(sid, KeyMessages) == "1";
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
