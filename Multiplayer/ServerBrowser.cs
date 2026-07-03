using System;
using System.Collections.Generic;
using Il2CppSteamworks;   // SteamMatchmaking, CallResult, LobbyMatchList_t, CSteamID, enums

namespace SideHustle.Multiplayer
{
    /// <summary>
    /// Public server browser: an async Steam lobby query filtered by gamemode id. Uses a CallResult (RequestLobbyList
    /// returns a SteamAPICall_t, not a Callback). Note: <c>LobbyMatchList_t.m_nLobbiesMatching</c> does NOT marshal
    /// across the Il2Cpp CallResult delegate boundary, so we iterate <c>GetLobbyByIndex</c> until an invalid id
    /// instead. The CallResult handle is held in a static field (a GC'd CallResult silently stops firing).
    /// </summary>
    internal static class ServerBrowser
    {
        private static CallResult<LobbyMatchList_t> _callResult;
        private static Action<List<LobbyRow>> _onResults;
        private static bool _querying;

        internal static bool IsQuerying => _querying;

        /// <summary>Issue a filtered lobby-list request. <paramref name="onResults"/> fires once on the main thread.</summary>
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

        private static void OnLobbyList(LobbyMatchList_t result, bool ioFailure)
        {
            _querying = false;
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
                        BuildId = info.BuildId
                    });
                }
                Core.Log?.Msg($"[mp] server browser: {rows.Count} lobby(ies) found.");
            }
            catch (Exception e) { Core.Log?.Warning("[mp] server-browser parse error: " + e.Message); }

            try { _onResults?.Invoke(rows); }
            catch (Exception e) { Core.Log?.Warning("[mp] server-browser callback threw: " + e.Message); }
        }
    }
}
