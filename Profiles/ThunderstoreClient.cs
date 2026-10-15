using System;
using System.IO;
using System.Linq;
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

        /// <summary>Send a request through the TLS-config ladder (starting at the last config that worked): each
        /// client gets <paramref name="attemptsEach"/> tries before the next config; only transport failures
        /// advance the ladder. <paramref name="makeRequest"/> is called once per attempt - a retried request needs
        /// its own message (and its own content).</summary>
        private static async Task<HttpResponseMessage> SendWithLadderAsync(Func<HttpRequestMessage> makeRequest,
            HttpCompletionOption completion, int attemptsEach, CancellationToken ct)
        {
            Exception last = null;
            int start = _preferredClient;
            int tried = 0;
            for (int i = 0; i < Clients.Length; i++)
            {
                int idx = (start + i) % Clients.Length;
                for (int attempt = 0; attempt < attemptsEach; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    // A short, growing pause between tries: an edge that just refused a handshake is usually still
                    // refusing a few milliseconds later, and hammering the ladder only burns the same failures.
                    if (tried > 0) await Task.Delay(Math.Min(250 * tried, 1500), ct).ConfigureAwait(false);
                    tried++;
                    try
                    {
                        var resp = await Clients[idx].SendAsync(makeRequest(), completion, ct).ConfigureAwait(false);
                        _preferredClient = idx;
                        return resp;
                    }
                    catch (HttpRequestException e) { last = e; }
                    // HttpClient.Timeout surfaces as a cancellation that is not the caller's: this configuration did
                    // not answer in time, so move on to the next one instead of ending the request.
                    catch (OperationCanceledException e) when (!ct.IsCancellationRequested) { last = e; }
                }
            }
            if (last is OperationCanceledException)
                throw new HttpRequestException($"no TLS configuration answered within {Clients[0].Timeout.TotalSeconds:0} s", last);
            throw last ?? new HttpRequestException("no TLS configuration connected");
        }

        /// <summary>How long a body may deliver no bytes at all before the download is given up. The client timeout
        /// only covers the response headers, so without this a connection that goes quiet mid-file never ends.</summary>
        internal static readonly TimeSpan BodyIdleTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Copy <paramref name="src"/> to <paramref name="dst"/>, failing with an <see cref="IOException"/> that
        /// names the stall when no byte arrives for <see cref="BodyIdleTimeout"/>. The caller's own cancellation still
        /// surfaces as an OperationCanceledException. <paramref name="maxBytes"/> of 0 means no cap.</summary>
        internal static async Task<long> CopyWithIdleTimeoutAsync(Stream src, Stream dst, long maxBytes,
            Action<long> onBytes, CancellationToken ct)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var buf = new byte[81920];
            long done = 0;
            while (true)
            {
                idle.CancelAfter(BodyIdleTimeout);
                int n;
                try { n = await src.ReadAsync(buf.AsMemory(0, buf.Length), idle.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException($"the download stalled - no data for {BodyIdleTimeout.TotalSeconds:0} s after {done} bytes");
                }
                if (n == 0) break;
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (maxBytes > 0 && done > maxBytes)
                    throw new IOException($"the file is larger than the allowed {maxBytes} bytes");
                onBytes?.Invoke(done);
            }
            return done;
        }

        /// <summary>Outcome of <see cref="FetchAsync"/>. Exactly one of <see cref="Bytes"/> / <see cref="Error"/> is set.</summary>
        internal sealed class HttpFetch
        {
            public byte[] Bytes;
            public int Status;          // 0 when no response arrived
            public string Error;        // human-readable reason when Bytes is null
            public bool RateLimited;    // the server said "too many requests" (429, or 403 with the quota used up)
            public DateTime? RateLimitReset;   // local time the quota returns, when the server said
        }

        /// <summary>GET a URL through the TLS ladder into memory, with the body idle timeout and an optional size cap.
        /// Never throws for a network or HTTP failure (the reason is in the result); the caller's cancellation does
        /// throw. Worker-thread only.</summary>
        internal static async Task<HttpFetch> FetchAsync(string url, CancellationToken ct, long maxBytes = 0,
            Action<long, long> onBytes = null)
        {
            var result = new HttpFetch();
            if (string.IsNullOrEmpty(url)) { result.Error = "no URL"; return result; }
            try
            {
                using var resp = await SendWithLadderAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, 1, ct)
                    .ConfigureAwait(false);
                result.Status = (int)resp.StatusCode;
                if (!resp.IsSuccessStatusCode)
                {
                    result.Error = $"HTTP {result.Status}";
                    bool quotaGone = resp.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) && rem.FirstOrDefault()?.Trim() == "0";
                    if (result.Status == 429 || (result.Status == 403 && quotaGone))
                    {
                        result.RateLimited = true;
                        if (resp.Headers.TryGetValues("X-RateLimit-Reset", out var rst)
                            && long.TryParse(rst.FirstOrDefault(), out long epoch))
                            result.RateLimitReset = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;
                        else if (resp.Headers.RetryAfter?.Delta is TimeSpan wait)
                            result.RateLimitReset = DateTime.Now + wait;
                    }
                    return result;
                }
                long total = resp.Content.Headers.ContentLength ?? 0;
                if (maxBytes > 0 && total > maxBytes) { result.Error = $"the file is larger than the allowed {maxBytes} bytes"; return result; }
                using var ms = new MemoryStream(total > 0 && total < int.MaxValue ? (int)total : 0);
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await CopyWithIdleTimeoutAsync(src, ms, maxBytes, d => onBytes?.Invoke(d, total), ct).ConfigureAwait(false);
                result.Bytes = ms.ToArray();
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                result.Error = $"{e.GetType().Name}: {e.Message}";
                return result;
            }
        }

        /// <summary>Download raw bytes (e.g. a package icon) through the same TLS ladder, or null on failure (logged).
        /// Small assets only - buffers the whole body. Worker-thread only; no Unity API touched.</summary>
        internal static async Task<byte[]> DownloadBytesAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                var fetch = await FetchAsync(url, ct).ConfigureAwait(false);
                if (fetch.Bytes == null) Log?.Invoke($"GET {url} failed: {fetch.Error}");
                return fetch.Bytes;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Log?.Invoke($"GET {url} cancelled.");
                return null;
            }
        }

        /// <summary>POST a JSON body and return the response text, or null on any failure (transport, timeout, or a
        /// non-success status). Same TLS ladder as every other request here - this type is the mod's shared HTTP
        /// transport, not only the Thunderstore one (GhReleases and NexusLookup ride it too). Worker-thread only.</summary>
        internal static async Task<string> PostJsonAsync(string url, string json, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(url) || json == null) return null;
            try
            {
                using var resp = await SendWithLadderAsync(
                    () => new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        // Fresh content per attempt: a consumed HttpContent cannot be re-sent up the ladder.
                        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                    },
                    HttpCompletionOption.ResponseContentRead, 1, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Log?.Invoke($"POST {url} failed: HTTP {(int)resp.StatusCode}");
                    return null;
                }
                return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) { Log?.Invoke($"POST {url} failed: {e.GetType().Name}: {e.Message}"); return null; }
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
            catch (Exception e) { Log?.Invoke($"reading the package index age failed: {e.Message}"); return null; }
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

                    using var resp = await SendWithLadderAsync(MakeRequest, HttpCompletionOption.ResponseHeadersRead, 1, ct).ConfigureAwait(false);
                    if (resp.StatusCode != System.Net.HttpStatusCode.NotModified && resp.IsSuccessStatusCode)
                    {
                        string json;
                        using (var ms = new MemoryStream())
                        {
                            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                            await CopyWithIdleTimeoutAsync(body, ms, 0, null, ct).ConfigureAwait(false);
                            json = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                        }
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
                            catch (Exception e) { Log?.Invoke($"the package index could not be cached on disk: {e.Message}"); }
                            _session = fresh;
                            return fresh;
                        }
                        Log?.Invoke("the package index answer could not be parsed; using the cached copy.");
                    }
                    else if (resp.StatusCode != System.Net.HttpStatusCode.NotModified)
                        Log?.Invoke($"the package index request got HTTP {(int)resp.StatusCode}; using the cached copy.");
                    // 304: the disk cache is current.
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) { Log?.Invoke($"the package index could not be fetched ({e.GetType().Name}: {e.Message}); using the cached copy."); }

                if (File.Exists(cachePath))
                {
                    try
                    {
                        _session = TsIndex.Parse(File.ReadAllText(cachePath));
                        return _session;
                    }
                    catch (Exception e) { Log?.Invoke($"the cached package index is unreadable: {e.Message}"); return null; }
                }
                Log?.Invoke("no package index available (offline and nothing cached).");
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
            catch (Exception e) { Log?.Invoke($"reading the cached package index failed: {e.Message}"); return null; }
        }

        /// <summary>
        /// Make sure a package version sits extracted in the cache; download + extract when missing. Returns the
        /// package directory, or null when the package is unknown, the download fails, or the zip is rejected
        /// (traversal guard). <paramref name="progress"/> reports (label, bytesDone, bytesTotal).
        /// </summary>
        /// <param name="attempt">Internal retry counter - a 5xx from the CDN gets exactly one second chance.</param>
        /// <param name="failed">Receives the reason when the result is null for any cause other than the caller's
        /// own cancellation (which leaves it unset - check the token).</param>
        internal static async Task<string> EnsurePackageAsync(string gameRoot, TsIndex index, string fullName,
            string version, IProgress<(string Label, long Done, long Total)> progress, CancellationToken ct,
            int attempt = 0, Action<string> failed = null)
        {
            string cacheRoot = PackageCache.RootFor(gameRoot);
            string dir = PackageCache.PathFor(cacheRoot, fullName, version);
            if (PackageCache.IsCached(cacheRoot, fullName, version)) return dir;

#if NO_EXTERNAL_FETCH
            // This build downloads no mod packages at all - a package is a zip of executable code, and the store
            // it ships on does not allow a package to fetch that. Anything already in the cache is still used
            // (it is on disk, nothing is fetched); anything else takes the manual route the callers already have.
            Log?.Invoke($"this build does not download packages - '{fullName} {version}' has to be installed by hand.");
            failed?.Invoke("this build does not download packages");
            return null;
#else

            var pkg = index?.Find(fullName);
            var ver = pkg?.Get(version);
            if (ver == null || string.IsNullOrEmpty(ver.DownloadUrl))
            {
                string why = index == null ? "the Thunderstore package list is not available"
                    : pkg == null ? "the package is not on Thunderstore" : $"version {version} is not on Thunderstore";
                Log?.Invoke($"download of {fullName} {version} skipped: {why}.");
                failed?.Invoke(why);
                return null;
            }

            string tmp = Path.Combine(cacheRoot, "_tmp", Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(tmp));
                using (var resp = await SendWithLadderAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, ver.DownloadUrl),
                    HttpCompletionOption.ResponseHeadersRead, 2, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        // A 5xx is the CDN having a moment, not a missing package - one short retry turns a failed
                        // join-sync ("install it by hand") back into the automatic install it was supposed to be.
                        // A 4xx is an answer (gone, renamed), so that one is final.
                        int code = (int)resp.StatusCode;
                        if (code >= 500 && attempt == 0)
                        {
                            Log?.Invoke($"download of {fullName} {version} got HTTP {code}; retrying once.");
                            await Task.Delay(1500, ct).ConfigureAwait(false);
                            return await EnsurePackageAsync(gameRoot, index, fullName, version, progress, ct, attempt + 1, failed)
                                .ConfigureAwait(false);
                        }
                        Log?.Invoke($"download of {fullName} {version} failed: HTTP {code}");
                        failed?.Invoke($"Thunderstore answered HTTP {code}");
                        return null;
                    }
                    long total = resp.Content.Headers.ContentLength ?? ver.FileSize;
                    await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var dst = File.Create(tmp);
                    string label = $"{fullName} {version}";
                    await CopyWithIdleTimeoutAsync(src, dst, 0, done => progress?.Report((label, done, total)), ct)
                        .ConfigureAwait(false);
                }

                bool extracted = PackageCache.ExtractAndClassify(tmp, dir) != null;
                if (!extracted)
                {
                    Log?.Invoke($"extract of {fullName} {version} failed (bad or hostile zip).");
                    failed?.Invoke("the downloaded archive was rejected");
                }
                return extracted ? dir : null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Log?.Invoke($"download of {fullName} {version} cancelled.");
                return null;
            }
            catch (Exception e)
            {
                Log?.Invoke($"download of {fullName} {version} failed: {e.GetType().Name}: {e.Message}");
                failed?.Invoke(e is IOException || e is HttpRequestException ? e.Message : $"{e.GetType().Name}: {e.Message}");
                return null;
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); }
                catch (Exception e) { Log?.Invoke($"could not delete the partial download '{tmp}': {e.Message}"); }
            }
#endif
        }
    }
}
