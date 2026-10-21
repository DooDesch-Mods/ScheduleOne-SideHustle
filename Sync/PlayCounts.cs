using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SideHustle.Config;

namespace SideHustle.Sync
{
    /// <summary>
    /// How often each gamemode was started this week, across everyone who shares it, and this player's share of it.
    ///
    /// A start reports the gamemode id and whether it was hosted or played solo - no Steam id, no name. The backend
    /// counts per day and serves the last seven days, which orders the gamemodes a player does not have yet. A
    /// joiner does not report: the host's start already counted that round.
    /// </summary>
    internal static class PlayCounts
    {
        /// <summary>How long a fetched ranking is used before the next request.</summary>
        private const float CacheSeconds = 600f;

        private static readonly HttpClient Http = MakeClient();

        private static HttpClient MakeClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("SideHustle/2.0");
            return c;
        }

        private static readonly object Lock = new object();
        private static Dictionary<string, int> _week = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _fetchedUtc = DateTime.MinValue;
        private static bool _fetching;

        /// <summary>Starts of this gamemode over the last seven days, 0 when unknown.</summary>
        internal static int Week(string gamemodeId)
        {
            if (string.IsNullOrEmpty(gamemodeId)) return 0;
            lock (Lock) return _week.TryGetValue(gamemodeId, out int n) ? n : 0;
        }

        /// <summary>Fetches the ranking off the main thread when the cached one is older than ten minutes. A failed
        /// fetch also waits ten minutes, so an unreachable backend is asked once per visit rather than every frame.</summary>
        internal static void RefreshIfStale()
        {
            lock (Lock)
            {
                if (_fetching || (DateTime.UtcNow - _fetchedUtc).TotalSeconds < CacheSeconds) return;
                _fetching = true;
                _fetchedUtc = DateTime.UtcNow;
            }
            if (!LobbyDirectory.Enabled)
            {
                Core.Log?.Msg("[plays] ranking not fetched: the backend is switched off.");
                lock (Lock) _fetching = false;
                return;
            }

            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    using var r = await Http.GetAsync(LobbyDirectory.BaseUrl + "/api/plays?days=7").ConfigureAwait(false);
                    if (!r.IsSuccessStatusCode)
                    {
                        Core.Log?.Warning($"[plays] ranking request answered HTTP {(int)r.StatusCode}.");
                        return;
                    }
                    string body = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var week = Parse(body);
                    lock (Lock) _week = week;
                    Core.Log?.Msg($"[plays] ranking fetched: {week.Count} gamemode(s) played this week.");
                }
                catch (Exception e) { Core.Log?.Warning("[plays] ranking request failed: " + e.Message); }
                finally { lock (Lock) _fetching = false; }
            });
        }

        private static Dictionary<string, int> Parse(string body)
        {
            var week = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("gamemodes", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                Core.Log?.Warning("[plays] ranking answer has no gamemodes list.");
                return week;
            }
            foreach (var g in list.EnumerateArray())
            {
                if (!g.TryGetProperty("gamemode", out var id) || id.ValueKind != JsonValueKind.String) continue;
                if (!g.TryGetProperty("plays", out var n) || !n.TryGetInt32(out int plays)) continue;
                week[id.GetString()] = plays;
            }
            return week;
        }

        /// <summary>Reports one started round, when the player shares play counts. Runs off the main thread.</summary>
        internal static void Report(string gamemodeId, string mode)
        {
            if (string.IsNullOrEmpty(gamemodeId)) { Core.Log?.Warning("[plays] start not reported: no gamemode id."); return; }
            if (!Preferences.SharePlayCounts) { Core.Log?.Msg($"[plays] start of '{gamemodeId}' not reported: sharing is off."); return; }
            if (!LobbyDirectory.Enabled) { Core.Log?.Msg($"[plays] start of '{gamemodeId}' not reported: the backend is switched off."); return; }

            string json = "{\"gamemode\":" + JsonSerializer.Serialize(gamemodeId) + ",\"mode\":" + JsonSerializer.Serialize(mode) + "}";
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var r = await Http.PostAsync(LobbyDirectory.BaseUrl + "/api/plays", content).ConfigureAwait(false);
                    if (r.IsSuccessStatusCode) Core.Log?.Msg($"[plays] start of '{gamemodeId}' ({mode}) reported.");
                    else Core.Log?.Warning($"[plays] start of '{gamemodeId}' rejected with HTTP {(int)r.StatusCode}.");
                }
                catch (Exception e) { Core.Log?.Warning($"[plays] start of '{gamemodeId}' not reported: {e.Message}"); }
            });
        }
    }
}
