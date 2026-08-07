using System;
using System.Collections.Generic;
using Il2CppSteamworks;   // SteamMatchmaking, CallResult, LobbyMatchList_t, CSteamID, enums
using Il2Cpp;             // SteamManager (global-namespace Steamworks.NET helper)

namespace SideHustle.Multiplayer
{
    /// <summary>
    /// Public server browser: async Steam lobby queries. Uses a CallResult (RequestLobbyList returns a
    /// SteamAPICall_t, not a Callback). Note: <c>LobbyMatchList_t.m_nLobbiesMatching</c> does NOT marshal across the
    /// Il2Cpp CallResult delegate boundary, so we iterate <c>GetLobbyByIndex</c> until an invalid id instead. Each
    /// CallResult handle is held in a static field (a GC'd CallResult silently stops firing).
    ///
    /// Two queries: <see cref="BeginQuery"/> lists lobbies for ONE gamemode (the Join browser), and
    /// <see cref="BeginQueryAdvertised"/> lists ALL advertised public lobbies across every gamemode (the menu's
    /// "not installed - live now" discovery entries). They keep independent CallResults so one cannot clobber the
    /// other's delegate.
    /// </summary>
    internal static class ServerBrowser
    {
        private static CallResult<LobbyMatchList_t> _callResult;
        private static Action<List<LobbyRow>> _onResults;
        private static bool _querying;

        private static CallResult<LobbyMatchList_t> _advCallResult;
        private static Action<List<LobbyRow>> _advOnResults;

        private static CallResult<LobbyMatchList_t> _vanillaCallResult;
        private static Action<List<Sync.VanillaLobbyRow>> _vanillaOnResults;

        internal static bool IsQuerying => _querying;

        private static bool _warnedNoSteam;

        /// <summary>False when Steam is not up, in which case no lobby query can be made.
        ///
        /// Since 0.4.6f11 the game runs happily without Steam - it falls back to a mock lobby service - so this is a
        /// normal state rather than a broken install, and the browser polls every 15 seconds. Without this guard every
        /// poll threw "Steamworks is not initialized" and buried the log in identical stack traces. Warn once, then
        /// stay quiet.</summary>
        private static bool SteamReady()
        {
            bool up;
            try { up = SteamManager.Initialized; } catch { up = false; }
            if (up) { _warnedNoSteam = false; return true; }
            if (!_warnedNoSteam)
            {
                _warnedNoSteam = true;
                Core.Log?.Msg("[mp] Steam is not up - the lobby browser stays empty until it is.");
            }
            return false;
        }

        /// <summary>Issue a lobby-list request filtered to one gamemode id. <paramref name="onResults"/> fires once on the main thread.</summary>
        internal static void BeginQuery(string gamemodeId, Action<List<LobbyRow>> onResults)
        {
            _onResults = onResults;
            if (!SteamReady()) { onResults?.Invoke(new List<LobbyRow>()); return; }
            try
            {
                if (_callResult == null)
                    _callResult = CallResult<LobbyMatchList_t>.Create(
                        (CallResult<LobbyMatchList_t>.APIDispatchDelegate)OnLobbyList);

                SteamMatchmaking.AddRequestLobbyListStringFilter(
                    LobbyCoordinator.KeyGamemode, gamemodeId, ELobbyComparison.k_ELobbyComparisonEqual);
                SteamMatchmaking.AddRequestLobbyListDistanceFilter(
                    ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);

                SteamAPICall_t call = SteamMatchmaking.RequestLobbyList();
                _callResult.Set(call, (CallResult<LobbyMatchList_t>.APIDispatchDelegate)OnLobbyList);
                _querying = true;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[mp] server-browser query failed: " + e.Message);
                _querying = false;
                onResults?.Invoke(new List<LobbyRow>());
            }
        }

        /// <summary>List ALL advertised public lobbies (any gamemode) - lobbies whose gamemode opted in to discovery
        /// (<c>sh_adv == "1"</c>), used to surface gamemodes the player does not have installed. Fires once on the
        /// main thread. Runs on its own CallResult so it is independent of the per-gamemode Join browser.</summary>
        internal static void BeginQueryAdvertised(Action<List<LobbyRow>> onResults)
        {
            _advOnResults = onResults;
            if (!SteamReady()) { onResults?.Invoke(new List<LobbyRow>()); return; }
            try
            {
                if (_advCallResult == null)
                    _advCallResult = CallResult<LobbyMatchList_t>.Create(
                        (CallResult<LobbyMatchList_t>.APIDispatchDelegate)OnAdvertisedLobbyList);

                SteamMatchmaking.AddRequestLobbyListStringFilter(
                    LobbyCoordinator.KeyAdvertise, "1", ELobbyComparison.k_ELobbyComparisonEqual);
                SteamMatchmaking.AddRequestLobbyListDistanceFilter(
                    ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);

                SteamAPICall_t call = SteamMatchmaking.RequestLobbyList();
                _advCallResult.Set(call, (CallResult<LobbyMatchList_t>.APIDispatchDelegate)OnAdvertisedLobbyList);
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[mp] advertised-lobby query failed: " + e.Message);
                onResults?.Invoke(new List<LobbyRow>());
            }
        }

        /// <summary>List published VANILLA lobbies (sh_vanilla == "1") - the Sync module's browser. Independent
        /// CallResult, same rules as the others (static-held, GetLobbyByIndex iteration).</summary>
        internal static void BeginQueryVanilla(Action<List<Sync.VanillaLobbyRow>> onResults)
        {
            _vanillaOnResults = onResults;
            if (!SteamReady()) { onResults?.Invoke(new List<Sync.VanillaLobbyRow>()); return; }
            try
            {
                if (_vanillaCallResult == null)
                    _vanillaCallResult = CallResult<LobbyMatchList_t>.Create(
                        (CallResult<LobbyMatchList_t>.APIDispatchDelegate)OnVanillaLobbyList);

                SteamMatchmaking.AddRequestLobbyListStringFilter(
                    Sync.VanillaLobby.KeyVanilla, "1", ELobbyComparison.k_ELobbyComparisonEqual);
                SteamMatchmaking.AddRequestLobbyListDistanceFilter(
                    ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);

                SteamAPICall_t call = SteamMatchmaking.RequestLobbyList();
                _vanillaCallResult.Set(call, (CallResult<LobbyMatchList_t>.APIDispatchDelegate)OnVanillaLobbyList);
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[sync] vanilla-lobby query failed: " + e.Message);
                onResults?.Invoke(new List<Sync.VanillaLobbyRow>());
            }
        }

        private static void OnVanillaLobbyList(LobbyMatchList_t result, bool ioFailure)
        {
            var rows = new List<Sync.VanillaLobbyRow>();
            try
            {
                for (int i = 0; i < 50; i++)
                {
                    CSteamID id = SteamMatchmaking.GetLobbyByIndex(i);
                    if (id.m_SteamID == 0UL) break;
                    rows.Add(Sync.VanillaLobby.ReadSummary(id.m_SteamID));
                    // Warm the FULL lobby data (incl. the big chunked manifest) in our Steam cache while the player
                    // browses, so a later sync-check reads complete data on the first try instead of racing propagation.
                    try { SteamMatchmaking.RequestLobbyData(id); } catch { }
                }
            }
            catch (Exception e) { Core.Log?.Warning("[sync] vanilla-lobby parse error: " + e.Message); }
            Core.Log?.Msg($"[sync] vanilla browser: {rows.Count} lobby(ies) found.");
            try { _vanillaOnResults?.Invoke(rows); }
            catch (Exception e) { Core.Log?.Warning("[sync] vanilla-browser callback threw: " + e.Message); }
        }

        private static void OnLobbyList(LobbyMatchList_t result, bool ioFailure)
        {
            _querying = false;
            var rows = ReadRows();
            Core.Log?.Msg($"[mp] server browser: {rows.Count} lobby(ies) found.");
            try { _onResults?.Invoke(rows); }
            catch (Exception e) { Core.Log?.Warning("[mp] server-browser callback threw: " + e.Message); }
        }

        /// <summary>Last advertised-lobby count that was logged, so a poll that keeps finding the same thing stays
        /// quiet. The list is re-queried on a timer while the hub is open, and "0 found" every few seconds buried
        /// everything else in the log.</summary>
        private static int _lastAdvertisedLogged = -1;

        private static void OnAdvertisedLobbyList(LobbyMatchList_t result, bool ioFailure)
        {
            var rows = ReadRows();
            if (rows.Count != _lastAdvertisedLogged)
            {
                _lastAdvertisedLogged = rows.Count;
                Core.Log?.Msg($"[mp] advertised lobbies: {rows.Count} found.");
            }
            try { _advOnResults?.Invoke(rows); }
            catch (Exception e) { Core.Log?.Warning("[mp] advertised-lobby callback threw: " + e.Message); }
            if (rows.Count == 0) ProbeUnfiltered();
        }

        // Nothing matched the advertise filter: ONCE per game session, ask again without any filter and log what
        // this client can see at all. That separates the two very different failures - "we discover no lobbies"
        // (Steam/network) from "we discover lobbies but none is flagged for discovery" (the host did not advertise) -
        // which otherwise look identical in the log. Once per session: an empty list is the normal case when nobody
        // is hosting, and that must not cost a second query every time the menu opens.
        private static bool _probing, _probed;
        private static void ProbeUnfiltered()
        {
            if (_probing || _probed) return;
            _probing = true;
            _probed = true;
            try
            {
                if (_probeCallResult == null)
                    _probeCallResult = CallResult<LobbyMatchList_t>.Create(
                        (CallResult<LobbyMatchList_t>.APIDispatchDelegate)OnProbeLobbyList);
                SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
                SteamAPICall_t call = SteamMatchmaking.RequestLobbyList();
                _probeCallResult.Set(call, (CallResult<LobbyMatchList_t>.APIDispatchDelegate)OnProbeLobbyList);
            }
            catch (Exception e) { _probing = false; Core.Log?.Warning("[mp] lobby probe failed: " + e.Message); }
        }

        private static CallResult<LobbyMatchList_t> _probeCallResult;

        private static void OnProbeLobbyList(LobbyMatchList_t result, bool ioFailure)
        {
            _probing = false;
            try
            {
                int n = 0;
                for (int i = 0; i < 50; i++)
                {
                    CSteamID id = SteamMatchmaking.GetLobbyByIndex(i);
                    if (id.m_SteamID == 0UL) break;
                    n++;
                    Core.Log?.Msg($"[mp] probe: lobby {id.m_SteamID} adv='{SteamMatchmaking.GetLobbyData(id, LobbyCoordinator.KeyAdvertise)}' " +
                                  $"gm='{SteamMatchmaking.GetLobbyData(id, LobbyCoordinator.KeyGamemode)}' " +
                                  $"vanilla='{SteamMatchmaking.GetLobbyData(id, Sync.VanillaLobby.KeyVanilla)}' " +
                                  $"members={SteamMatchmaking.GetNumLobbyMembers(id)}");
                }
                Core.Log?.Msg($"[mp] probe: {n} lobby(ies) visible without any filter.");
            }
            catch (Exception e) { Core.Log?.Warning("[mp] lobby probe read failed: " + e.Message); }
        }

        // Read the lobby list Steam just returned into rows. Shared by both queries; each callback reads the list of
        // the request that just completed (the queries fire at different times, so they do not interleave in practice).
        private static List<LobbyRow> ReadRows()
        {
            var rows = new List<LobbyRow>();
            try
            {
                for (int i = 0; i < 50; i++)
                {
                    CSteamID id = SteamMatchmaking.GetLobbyByIndex(i);
                    if (id.m_SteamID == 0UL) break;

                    var info = LobbyCoordinator.ReadInfo(id.m_SteamID);
                    int members = 1;
                    try { members = SteamMatchmaking.GetNumLobbyMembers(id); } catch { /* ignore */ }
                    rows.Add(new LobbyRow
                    {
                        LobbyId = id.m_SteamID,
                        GamemodeName = info.GamemodeName,
                        LobbyName = info.LobbyName,
                        Mode = info.Mode,
                        HostName = info.HostName,
                        Members = members,
                        MaxPlayers = info.MaxPlayers,
                        HasPassword = info.HasPassword,
                        PwHash = info.PwHash,
                        BuildId = info.BuildId,
                        GamemodeId = info.GamemodeId,
                        DownloadUrl = info.DownloadUrl
                    });
                }
            }
            catch (Exception e) { Core.Log?.Warning("[mp] server-browser parse error: " + e.Message); }
            return rows;
        }
    }
}
