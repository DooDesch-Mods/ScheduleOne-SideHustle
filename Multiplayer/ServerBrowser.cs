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

        // Diagnostic only: a second, UNFILTERED probe fired when the filtered query finds nothing.
        private static CallResult<LobbyMatchList_t> _diagResult;
        private static bool _diagInFlight;

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

            if (rows.Count == 0) DiagnoseUnfiltered();   // distinguish a Goldberg sharing gap from a filter/tag bug
        }

        /// <summary>Diagnostic: when the gamemode-filtered query found nothing, fire ONE unfiltered query and log
        /// every lobby Steam/Goldberg returns plus its sh_gamemode/sh_name/sh_vis. If this also returns 0 the lobby
        /// isn't being shared at all (Goldberg/env); if it returns the host lobby with a matching sh_gamemode then the
        /// string filter is the culprit. Temporary - remove before shipping.</summary>
        private static void DiagnoseUnfiltered()
        {
            if (_diagInFlight) return;
            try
            {
                if (_diagResult == null)
                    _diagResult = CallResult<LobbyMatchList_t>.Create((CallResult<LobbyMatchList_t>.APIDispatchDelegate)OnDiag);
                SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
                SteamAPICall_t call = SteamMatchmaking.RequestLobbyList();
                _diagResult.Set(call, (CallResult<LobbyMatchList_t>.APIDispatchDelegate)OnDiag);
                _diagInFlight = true;
            }
            catch (Exception e) { Core.Log?.Warning("[mp][diag] unfiltered probe failed: " + e.Message); }
        }

        private static void OnDiag(LobbyMatchList_t result, bool ioFailure)
        {
            _diagInFlight = false;
            try
            {
                int n = 0;
                for (int i = 0; i < 50; i++)
                {
                    CSteamID id = SteamMatchmaking.GetLobbyByIndex(i);
                    if (id.m_SteamID == 0UL) break;
                    n++;
                    string gm = SteamMatchmaking.GetLobbyData(id, LobbyCoordinator.KeyGamemode);
                    string nm = SteamMatchmaking.GetLobbyData(id, LobbyCoordinator.KeyLobbyName);
                    string vis = SteamMatchmaking.GetLobbyData(id, LobbyCoordinator.KeyVisibility);
                    Core.Log?.Msg($"[mp][diag] raw lobby {id.m_SteamID}: sh_gamemode='{gm}' sh_name='{nm}' sh_vis='{vis}'");
                }
                Core.Log?.Msg($"[mp][diag] UNFILTERED lobby count = {n}  (filtered found 0; if this is also 0 Goldberg isn't sharing the lobby, if >0 with a matching sh_gamemode the string filter is at fault).");
            }
            catch (Exception e) { Core.Log?.Warning("[mp][diag] parse error: " + e.Message); }
        }
    }
}
