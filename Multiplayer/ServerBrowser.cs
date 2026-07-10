using System;
using System.Collections.Generic;
using Il2CppSteamworks;   // SteamMatchmaking, CallResult, LobbyMatchList_t, CSteamID, enums

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

        internal static bool IsQuerying => _querying;

        /// <summary>Issue a lobby-list request filtered to one gamemode id. <paramref name="onResults"/> fires once on the main thread.</summary>
        internal static void BeginQuery(string gamemodeId, Action<List<LobbyRow>> onResults)
        {
            _onResults = onResults;
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

        private static void OnLobbyList(LobbyMatchList_t result, bool ioFailure)
        {
            _querying = false;
            var rows = ReadRows();
            Core.Log?.Msg($"[mp] server browser: {rows.Count} lobby(ies) found.");
            try { _onResults?.Invoke(rows); }
            catch (Exception e) { Core.Log?.Warning("[mp] server-browser callback threw: " + e.Message); }
        }

        private static void OnAdvertisedLobbyList(LobbyMatchList_t result, bool ioFailure)
        {
            var rows = ReadRows();
            Core.Log?.Msg($"[mp] advertised lobbies: {rows.Count} found.");
            try { _advOnResults?.Invoke(rows); }
            catch (Exception e) { Core.Log?.Warning("[mp] advertised-lobby callback threw: " + e.Message); }
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
