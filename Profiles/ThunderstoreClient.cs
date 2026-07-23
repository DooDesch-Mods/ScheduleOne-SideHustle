using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SideHustle.Profiles
{
    /// <summary>
    /// Networking for the manager: the community package index (ETag-cached on disk under UserData, refreshed
    /// lazily at most once per session) and package-zip downloads into the package cache. Pure BCL + HttpClient,
    /// always driven from Task.Run - no Unity API is ever touched here; callers marshal results back to the main
    /// thread themselves. Offline behavior: a failed refresh falls back to the last on-disk index (the UI shows
    /// its age); a failed download returns null and the caller reports the mod as unavailable.
    /// </summary>
    internal static class ThunderstoreClient
    {
        internal const string Community = "schedule-i";

        /// <summary>Host-provided diagnostics sink (Core.Log in the mod, Console in the test harness) - this
        /// type stays pure BCL, so it cannot log anywhere itself.</summary>
        internal static Action<string> Log;

        private static readonly SemaphoreSlim IndexGate = new SemaphoreSlim(1, 1);
        private static TsIndex _session;   // the index is multi-MB JSON; parse it once per session

        // The download CDN (gcdn.thunderstore.io) INTERMITTENTLY rejects TLS handshakes depending on the exact
        // ClientHello (fingerprint filtering that varies across its edges - observed empirically 2026-07-10:
        // the combined TLS1.2+1.3 default hello is rejected most, single-protocol hellos usually pass, and the
        // same pin can succeed on one connection and fail on the next). So every request walks a ladder of
        // differently-configured clients until one connects.
        private static readonly HttpClient[] Clients =
        {
            CreateClient(System.Security.Authentication.SslProtocols.Tls12),
            CreateClient(System.Security.Authentication.SslProtocols.Tls13),
            CreateClient(System.Security.Authentication.SslProtocols.None),   // system default
        };

        private static HttpClient CreateClient(System.Security.Authentication.SslProtocols protocols)
        {
            var handler = new System.Net.Http.SocketsHttpHandler
            {
                SslOptions = { EnabledSslProtocols = protocols },
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            };
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("SideHustle/2.0");
            return c;
        }

        private static volatile int _preferredClient;   // the config that connected last - tried first next time

        /// <summary>Send a GET through the TLS-config ladder (starting at the last config that worked): each
        /// client gets <paramref name="attemptsEach"/> tries before the next config; only transport failures
        /// advance the ladder.</summary>
        private static async Task<HttpResponseMessage> GetWithLadderAsync(Func<HttpRequestMessage> makeRequest,
            HttpCompletionOption completion, int attemptsEach, CancellationToken ct)
        {
            Exception last = null;
            int start = _preferredClient;
            for (int i = 0; i < Clients.Length; i++)
            {
                int idx = (start + i) % Clients.Length;
                for (int attempt = 0; attempt < attemptsEach; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var resp = await Clients[idx].SendAsync(makeRequest(), completion, ct).ConfigureAwait(false);
                        _preferredClient = idx;
                        return resp;
                    }
                    catch (HttpRequestException e) { last = e; }
                }
            }
            throw last ?? new HttpRequestException("no TLS configuration connected");
        }

        /// <summary>Download raw bytes (e.g. a package icon) through the same TLS ladder, or null on failure. Small
        /// assets only - buffers the whole body. Worker-thread only; no Unity API touched.</summary>
        internal static async Task<byte[]> DownloadBytesAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                using var resp = await GetWithLadderAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseContentRead, 1, ct)
                    .ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            }
            catch { return null; }
        }

        internal static string IndexCachePath(string gameRoot) =>
            Path.Combine(gameRoot, "UserData", "SideHustle", "Cache", "ts_index.json");

        /// <summary>When the on-disk index cache was last refreshed (UI "last updated" note), or null.</summary>
        internal static DateTime? IndexCacheTime(string gameRoot)
        {
            try
            {
                string p = IndexCachePath(gameRoot);
                return File.Exists(p) ? File.GetLastWriteTime(p) : (DateTime?)null;
            }
            catch { return null; }
        }

        internal static async Task<TsIndex> GetIndexAsync(string gameRoot, bool forceRefresh, CancellationToken ct)
        {
            if (_session != null && !forceRefresh) return _session;
            await IndexGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_session != null && !forceRefresh) return _session;

                string cachePath = IndexCachePath(gameRoot);
                string etagPath = cachePath + ".etag";
                try
                {
                    HttpRequestMessage MakeRequest()
                    {
                        var req = new HttpRequestMessage(HttpMethod.Get, $"https://thunderstore.io/c/{Community}/api/v1/package/");
                        if (File.Exists(cachePath) && File.Exists(etagPath))
                            req.Headers.TryAddWithoutValidation("If-None-Match", File.ReadAllText(etagPath).Trim());
                        return req;
                    }

                    using var resp = await GetWithLadderAsync(MakeRequest, HttpCompletionOption.ResponseHeadersRead, 1, ct).ConfigureAwait(false);
                    if (resp.StatusCode != System.Net.HttpStatusCode.NotModified && resp.IsSuccessStatusCode)
                    {
                        string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        var fresh = TsIndex.Parse(json);
                        if (fresh != null)
                        {
                            try
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                                File.WriteAllText(cachePath, json);
                                string tag = resp.Headers.ETag?.Tag;
                                if (!string.IsNullOrEmpty(tag)) File.WriteAllText(etagPath, tag);
                            }
                            catch { /* cache write is best-effort */ }
                            _session = fresh;
                            return fresh;
                        }
                    }
                    // 304 or unparseable body: fall through to the disk cache.
                }
                catch { /* offline / DNS / timeout: fall through to the disk cache */ }

                if (File.Exists(cachePath))
                {
                    _session = TsIndex.Parse(File.ReadAllText(cachePath));
                    return _session;
                }
                return null;
            }
            finally { IndexGate.Release(); }
        }

        /// <summary>
        /// The index WITHOUT any network round-trip: the session copy if one is loaded, else a synchronous parse of
        /// the on-disk cache, else null. Safe on the main thread (a few MB of JSON, no gate). Used where the UI
        /// needs dependency/sort data NOW and must degrade gracefully offline instead of blocking. Deliberately does
        /// NOT populate <see cref="_session"/> - that would suppress GetIndexAsync's once-per-session network refresh
        /// and freeze the browser/installs on a stale disk cache.
        /// </summary>
        internal static TsIndex GetCachedIndexOrNull(string gameRoot)
        {
            if (_session != null) return _session;
            try
            {
                string cachePath = IndexCachePath(gameRoot);
                if (!File.Exists(cachePath)) return null;
                return TsIndex.Parse(File.ReadAllText(cachePath));
            }
            catch { return null; }
        }

        /// <summary>
        /// Make sure a package version sits extracted in the cache; download + extract when missing. Returns the
        /// package directory, or null when the package is unknown, the download fails, or the zip is rejected
        /// (traversal guard). <paramref name="progress"/> reports (label, bytesDone, bytesTotal).
        /// </summary>
        internal static async Task<string> EnsurePackageAsync(string gameRoot, TsIndex index, string fullName,
            string version, IProgress<(string Label, long Done, long Total)> progress, CancellationToken ct)
        {
            string cacheRoot = PackageCache.RootFor(gameRoot);
            string dir = PackageCache.PathFor(cacheRoot, fullName, version);
            if (PackageCache.IsCached(cacheRoot, fullName, version)) return dir;

            var pkg = index?.Find(fullName);
            var ver = pkg?.Get(version);
            if (ver == null || string.IsNullOrEmpty(ver.DownloadUrl)) return null;

            string tmp = Path.Combine(cacheRoot, "_tmp", Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(tmp));
                using (var resp = await GetWithLadderAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, ver.DownloadUrl),
                    HttpCompletionOption.ResponseHeadersRead, 2, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        Log?.Invoke($"download of {fullName} {version} failed: HTTP {(int)resp.StatusCode}");
                        return null;
                    }
                    long total = resp.Content.Headers.ContentLength ?? ver.FileSize;
                    await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var dst = File.Create(tmp);
                    var buf = new byte[81920];
                    long done = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        done += n;
                        progress?.Report(($"{fullName} {version}", done, total));
                    }
                }

                bool extracted = PackageCache.ExtractAndClassify(tmp, dir) != null;
                if (!extracted) Log?.Invoke($"extract of {fullName} {version} failed (bad or hostile zip).");
                return extracted ? dir : null;
            }
            catch (Exception e)
            {
                Log?.Invoke($"download of {fullName} {version} failed: {e.GetType().Name}: {e.Message}");
                return null;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            }
        }
    }
}
