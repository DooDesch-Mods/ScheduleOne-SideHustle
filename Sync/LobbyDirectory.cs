using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace SideHustle.Sync
{
    /// <summary>
    /// Client for the Side Hustle backend (SideHustle.doodesch.de) - a public lobby directory + manifest cache.
    ///
    /// This is a FALLBACK, never the primary path. Steam lobby data carries the manifest by default (no external
    /// dependency, lower latency, fine for normal mod lists). The backend only rescues the worst case - a mod list
    /// too large for Steam's lobby metadata to propagate - so a big host is still joinable. A host publishes to BOTH
    /// (Steam is the trust anchor + primary read; the backend is the safety copy + the filterable directory), and a
    /// joiner reads Steam first and only asks the backend when Steam can't produce a valid manifest.
    ///
    /// Trust: the backend is untrusted. The manifest it returns is only accepted after the caller verifies its hash
    /// equals the mhash the host wrote to the real Steam lobby (Steam-authenticated to the owner). Pure BCL +
    /// HttpClient, always driven off the main thread.
    /// </summary>
    internal static class LobbyDirectory
    {
        internal const string DefaultBase = "https://SideHustle.doodesch.de";

        /// <summary>Base URL (a MelonPreferences override lets a private deployment or "off" be set without a rebuild).</summary>
        internal static string BaseUrl = DefaultBase;
        internal static bool Enabled = true;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        private static readonly HttpClient Http = MakeClient();

        private static HttpClient MakeClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("SideHustle/2.0");
            return c;
        }

        // --- host side ---

        /// <summary>Publish (or refresh) this lobby's directory entry + manifest. Fire-and-forget; a failure just means
        /// the backend fallback won't be available for this lobby (Steam still works). Returns false on any error.</summary>
        internal static async Task<bool> PublishAsync(DirPublish p)
        {
            if (!Enabled) return false;
            try
            {
                using var body = new StringContent(JsonSerializer.Serialize(p, Json), Encoding.UTF8, "application/json");
                using var r = await Http.PostAsync(BaseUrl + "/api/lobbies", body).ConfigureAwait(false);
                return r.IsSuccessStatusCode;
            }
            catch (Exception e) { Log("publish failed: " + e.Message); return false; }
        }

        /// <summary>Keep the directory entry alive + update the live member count. Silent best-effort.</summary>
        internal static async Task HeartbeatAsync(string lobbyId, string secret, int members)
        {
            if (!Enabled || string.IsNullOrEmpty(lobbyId)) return;
            try
            {
                using var body = new StringContent(JsonSerializer.Serialize(new { secret, members }, Json), Encoding.UTF8, "application/json");
                using var _ = await Http.PostAsync($"{BaseUrl}/api/lobbies/{lobbyId}/heartbeat", body).ConfigureAwait(false);
            }
            catch { /* best-effort */ }
        }

        /// <summary>Stop advertising this lobby (host left / went private). Silent best-effort.</summary>
        internal static async Task RemoveAsync(string lobbyId, string secret)
        {
            if (!Enabled || string.IsNullOrEmpty(lobbyId)) return;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/api/lobbies/{lobbyId}")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { secret }, Json), Encoding.UTF8, "application/json"),
                };
                using var _ = await Http.SendAsync(req).ConfigureAwait(false);
            }
            catch { /* best-effort */ }
        }

        // --- joiner side ---

        /// <summary>List directory lobbies (optionally filtered). Empty list on any error - the caller merges these
        /// with the Steam lobby-list, so the backend being down just means fewer discovery sources.</summary>
        internal static async Task<List<DirRow>> BrowseAsync(string kind = null, string gamemode = null, string search = null)
        {
            if (!Enabled) return new List<DirRow>();
            try
            {
                var q = new List<string>();
                if (!string.IsNullOrEmpty(kind)) q.Add("kind=" + Uri.EscapeDataString(kind));
                if (!string.IsNullOrEmpty(gamemode)) q.Add("gamemode=" + Uri.EscapeDataString(gamemode));
                if (!string.IsNullOrEmpty(search)) q.Add("search=" + Uri.EscapeDataString(search));
                string url = BaseUrl + "/api/lobbies" + (q.Count > 0 ? "?" + string.Join("&", q) : "");
                string s = await Http.GetStringAsync(url).ConfigureAwait(false);
                var resp = JsonSerializer.Deserialize<DirListResponse>(s, Json);
                return resp?.Lobbies ?? new List<DirRow>();
            }
            catch (Exception e) { Log("browse failed: " + e.Message); return new List<DirRow>(); }
        }

        /// <summary>Fetch the full manifest + prefs for a lobby (the fallback when Steam couldn't produce it). The
        /// caller MUST verify the returned mhash against the Steam-lobby mhash before trusting it. Null on any error.</summary>
        internal static async Task<DirManifestResponse> FetchManifestAsync(string lobbyId)
        {
            if (!Enabled || string.IsNullOrEmpty(lobbyId)) return null;
            try
            {
                string s = await Http.GetStringAsync($"{BaseUrl}/api/lobbies/{lobbyId}/manifest").ConfigureAwait(false);
                return JsonSerializer.Deserialize<DirManifestResponse>(s, Json);
            }
            catch (Exception e) { Log("manifest fetch failed: " + e.Message); return null; }
        }

        private static void Log(string m) => Core.Log?.Warning("[dir] " + m);
    }

    // --- payloads (System.Text.Json, camelCase to match the backend) ---

    internal sealed class DirPublish
    {
        public string LobbyId { get; set; }
        public string OwnerSteamId { get; set; }
        public string Secret { get; set; }
        public string HostName { get; set; }
        public string LobbyName { get; set; }
        public string Kind { get; set; }          // "vanilla" | "gamemode"
        public string Gamemode { get; set; }
        public string GamemodeName { get; set; }
        public bool Enforce { get; set; }
        public int MaxPlayers { get; set; }
        public int Members { get; set; }
        public bool HasPassword { get; set; }
        public string PwHash { get; set; }
        public string ModSummary { get; set; }
        public string GameVersion { get; set; }
        public string AppBuild { get; set; }
        public string Mhash { get; set; }
        public string Manifest { get; set; }
        public string Prefs { get; set; }
    }

    internal sealed class DirRow
    {
        public string LobbyId { get; set; }
        public string OwnerSteamId { get; set; }
        public string HostName { get; set; }
        public string LobbyName { get; set; }
        public string Kind { get; set; }
        public string Gamemode { get; set; }
        public string GamemodeName { get; set; }
        public bool Enforce { get; set; }
        public int MaxPlayers { get; set; }
        public int Members { get; set; }
        public bool HasPassword { get; set; }
        public string ModSummary { get; set; }
        public string GameVersion { get; set; }
        public string Mhash { get; set; }
    }

    internal sealed class DirListResponse
    {
        public bool Ok { get; set; }
        public int Count { get; set; }
        public List<DirRow> Lobbies { get; set; }
    }

    internal sealed class DirManifestResponse
    {
        public bool Ok { get; set; }
        public string LobbyId { get; set; }
        public string OwnerSteamId { get; set; }
        public string Mhash { get; set; }
        public string Manifest { get; set; }
        public string Prefs { get; set; }
    }

    /// <summary>A backend manifest that has already been validated against the Steam-lobby mhash (safe to use).</summary>
    internal sealed class DirManifest
    {
        public SyncManifest Manifest;
        public string Prefs;
        public string Mhash;
    }
}
