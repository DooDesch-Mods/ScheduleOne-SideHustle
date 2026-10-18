using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SideHustle.Profiles;

namespace SideHustle.Sync
{
    /// <summary>
    /// Finds where a mod the host could not source comes from, by name, version and file hash.
    ///
    /// The Side Hustle backend (<see cref="Endpoint"/>) is asked first. It hashes the DLLs of the Thunderstore
    /// packages that fit the name and version, so it can say "this is exactly UncleTyrone-More_Guns_Forked_IL2CPP
    /// 1.6.6" - an answer the game cannot get on its own without downloading every candidate. Only such a file-hash
    /// match comes back as <c>confidence: verified</c> with a <c>ts:</c> source, and only that answer may turn a
    /// dropped mod into a download (<see cref="IsAutoInstallable"/>); the downloaded file is hash-checked again
    /// against the host's manifest before it is used.
    ///
    /// When the backend cannot be reached, the Nexus API is asked directly (<see cref="NexusLookup.ResolveVersionedAsync"/>),
    /// which can only ever produce a page link, never an install.
    ///
    /// Answers are cached per session, keyed by name, version and hash. A failed or still-pending lookup is not
    /// cached, so a later screen asks again. Pure BCL on <see cref="ThunderstoreClient"/>'s HTTP transport.
    /// </summary>
    internal static class SourceLookup
    {
        internal const string Endpoint = "https://sidehustle.doodesch.de/api/resolve";

        /// <summary>The backend reads at most this many rows per request.</summary>
        internal const int MaxRows = 64;

        /// <summary>Total wait for one join's lookup. The backend answers within ~12 s and keeps resolving after
        /// that, so the budget leaves room for exactly one follow-up request for rows it reported as pending.</summary>
        internal static readonly TimeSpan JoinBudget = TimeSpan.FromSeconds(20);

        private static readonly TimeSpan PendingRetryDelay = TimeSpan.FromSeconds(1.5);
        private const int MaxBackendRounds = 3;

        /// <summary>Set to false to keep the join flow fully offline.</summary>
        internal static bool Enabled = true;

        /// <summary>Host-provided diagnostics sink (Core.Log in the mod, Console in the test harness).</summary>
        internal static Action<string> Log;

        /// <summary>Test seam: replaces the HTTP POST (url, json) -> body or null.</summary>
        internal static Func<string, string, CancellationToken, Task<string>> PostJson = ThunderstoreClient.PostJsonAsync;

        internal sealed class Query
        {
            public string Name;
            public string Version;
            public string File;
            public string Sha256;
        }

        internal sealed class Answer
        {
            /// <summary>thunderstore | nexus | search | pending</summary>
            public string Resolution = "";
            public string Url = "";
            /// <summary>"ts:Owner-Name-1.2.3" for a file-hash match, else empty.</summary>
            public string Source = "";
            /// <summary>verified | version | name | none</summary>
            public string Confidence = "none";
            /// <summary>Who answered: "backend" or "nexus" (the direct fallback). For the log.</summary>
            public string Via = "";
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Answer> Answers = new Dictionary<string, Answer>(StringComparer.Ordinal);
        private static readonly HashSet<string> InFlight = new HashSet<string>(StringComparer.Ordinal);
        private static int _version;
        // A Nexus answer stands in for the backend while it is down. It is a page link at best, while the backend can
        // match the host's file hash to a Thunderstore package and make the mod an automatic download, so the backend
        // gets asked again once this much time has passed.
        private static DateTime _backendDownAt = DateTime.MinValue;
        private static readonly TimeSpan BackendRetry = TimeSpan.FromMinutes(2);

        /// <summary>Bumped every time a lookup lands, so a screen that shows resolved links repaints.</summary>
        internal static int ResultsVersion => Volatile.Read(ref _version);

        /// <summary>The query for a manifest row. The name falls back to the DLL file name without ".dll", the same
        /// term the manual checklist searches for.</summary>
        internal static Query For(ManifestMod m) => new Query
        {
            Name = DisplayName(m),
            Version = m?.Version ?? "",
            File = m?.File ?? "",
            Sha256 = (m?.Sha256 ?? "").ToLowerInvariant(),
        };

        internal static string DisplayName(ManifestMod m)
        {
            if (m == null) return "";
            if (!string.IsNullOrEmpty(m.Name)) return m.Name;
            var f = m.File ?? "";
            return f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? f.Substring(0, f.Length - 4) : f;
        }

        internal static string Key(Query q) =>
            NexusLookup.Normalize(q?.Name) + "|" + NexusLookup.NormVersion(q?.Version) + "|" + (q?.Sha256 ?? "").ToLowerInvariant();

        /// <summary>A lookup answer that was already fetched, else null. Never touches the network.</summary>
        internal static Answer CachedOrNull(Query q)
        {
            if (q == null) return null;
            lock (Gate) return Answers.TryGetValue(Key(q), out var a) ? a : null;
        }

        /// <summary>
        /// Whether an answer may turn a dropped mod into an automatic download: the backend matched the host's DLL
        /// hash inside a Thunderstore package. A name or version match never qualifies, and neither does a row
        /// without a full SHA-256 to check the download against.
        /// </summary>
        internal static bool IsAutoInstallable(Answer a, string expectedSha256)
        {
            if (a == null || a.Confidence != "verified") return false;
            if (string.IsNullOrEmpty(a.Source) || !a.Source.StartsWith("ts:", StringComparison.Ordinal)) return false;
            if (!TsIndex.SplitDependency(a.Source.Substring(3), out _, out _)) return false;
            return IsSha256(expectedSha256);
        }

        internal static bool IsSha256(string s) =>
            !string.IsNullOrEmpty(s) && s.Length == 64 && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));

        /// <summary>
        /// Look these rows up and wait for the answers, at most <paramref name="budget"/>. For a worker thread (the
        /// join's diff runs on one). Returns the answers by <see cref="Key"/>, cached ones included; rows that are
        /// still unanswered when the budget runs out are simply missing.
        /// </summary>
        internal static Dictionary<string, Answer> ResolveNow(IEnumerable<Query> rows, TimeSpan budget)
        {
            var list = rows?.Where(q => q != null).ToList() ?? new List<Query>();
            if (!Enabled || list.Count == 0) return new Dictionary<string, Answer>(StringComparer.Ordinal);
            try
            {
                using var cts = new CancellationTokenSource(budget);
                // Task.Run: the awaits inside must never try to resume on a caller's synchronization context.
                return Task.Run(() => ResolveAsync(list, cts.Token)).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Log?.Invoke($"lookup of {list.Count} row(s) failed: {e.GetType().Name}: {e.Message}");
                return CachedFor(list);
            }
        }

        /// <summary>Look these rows up in the background. Rows already answered or in flight are skipped, so a
        /// screen may call this every time it opens.</summary>
        internal static void Prefetch(IEnumerable<Query> rows)
        {
            if (!Enabled || rows == null) return;
            var list = rows.Where(q => q != null).ToList();
            if (list.Count == 0) return;
            Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(JoinBudget);
                    await ResolveAsync(list, cts.Token).ConfigureAwait(false);
                }
                catch (Exception e) { Log?.Invoke($"background lookup failed: {e.GetType().Name}: {e.Message}"); }
            });
        }

        /// <summary>
        /// The lookup itself: backend first, in batches of <see cref="MaxRows"/>; rows it reports as pending are asked
        /// again while the token allows; when the backend cannot be reached, every open row goes to the Nexus API
        /// directly. Returns every answer known for these rows when it ends.
        /// </summary>
        internal static async Task<Dictionary<string, Answer>> ResolveAsync(IList<Query> rows, CancellationToken ct)
        {
            var open = new List<Query>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            lock (Gate)
            {
                foreach (var q in rows)
                {
                    string k = Key(q);
                    if (!NexusLookup.IsSearchable(NexusLookup.Normalize(q.Name)) && !IsSha256(q.Sha256))
                    {
                        Log?.Invoke($"\"{q.Name}\" skipped: no searchable name and no file hash");
                        continue;
                    }
                    if (!seen.Add(k) || InFlight.Contains(k)) continue;
                    if (Answers.TryGetValue(k, out var have)
                        && !(have.Via == "nexus" && DateTime.UtcNow - _backendDownAt >= BackendRetry)) continue;
                    InFlight.Add(k);
                    open.Add(q);
                }
            }
            if (open.Count == 0) return CachedFor(rows);

            try
            {
                var unanswered = await AskBackendAsync(open, ct).ConfigureAwait(false);
                if (unanswered == null)
                {
                    _backendDownAt = DateTime.UtcNow;
                    Log?.Invoke($"backend unreachable - asking Nexus directly for {open.Count} mod(s)");
                    await AskNexusAsync(open, ct).ConfigureAwait(false);
                }
                else if (unanswered.Count > 0)
                {
                    Log?.Invoke($"backend still working on {unanswered.Count} mod(s): "
                                + string.Join(", ", unanswered.Select(Label)) + " - asked again on the next screen");
                }
            }
            catch (OperationCanceledException)
            {
                Log?.Invoke("lookup stopped at its time limit; unanswered mods are asked again on the next screen");
            }
            finally
            {
                lock (Gate) foreach (var q in open) InFlight.Remove(Key(q));
            }
            return CachedFor(rows);
        }

        /// <summary>Ask the backend. Returns the rows it is still resolving (empty when all are answered), or null
        /// when it could not be reached or its answer was unreadable - the caller then falls back to Nexus.</summary>
        private static async Task<List<Query>> AskBackendAsync(List<Query> rows, CancellationToken ct)
        {
            var todo = rows;
            for (int round = 0; round < MaxBackendRounds && todo.Count > 0; round++)
            {
                if (round > 0) await Task.Delay(PendingRetryDelay, ct).ConfigureAwait(false);
                var pending = new List<Query>();
                for (int i = 0; i < todo.Count; i += MaxRows)
                {
                    var batch = todo.Skip(i).Take(MaxRows).ToList();
                    string body = await PostJson(Endpoint, BuildRequestJson(batch), ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    var answers = ParseAnswers(body, batch.Count);
                    if (answers == null)
                    {
                        Log?.Invoke(body == null ? "backend did not answer" : "backend answer could not be read");
                        return round == 0 && i == 0 ? null : pending.Concat(todo.Skip(i)).ToList();
                    }
                    for (int j = 0; j < batch.Count; j++)
                    {
                        var a = answers[j];
                        if (a.Resolution == "pending") { pending.Add(batch[j]); continue; }
                        a.Via = "backend";
                        Store(batch[j], a);
                    }
                }
                todo = pending;
            }
            return todo;
        }

        /// <summary>The fallback: one Nexus API query per row, version-aware. Produces page links only.</summary>
        private static async Task AskNexusAsync(List<Query> rows, CancellationToken ct)
        {
            foreach (var q in rows)
            {
                ct.ThrowIfCancellationRequested();
                var hit = await NexusLookup.ResolveVersionedAsync(q.Name, q.Version, ct).ConfigureAwait(false);
                if (hit == null) { Log?.Invoke($"{Label(q)}: Nexus did not answer either - stays a search link"); continue; }
                var (modId, by) = hit.Value;
                Store(q, modId > 0
                    ? new Answer { Resolution = "nexus", Url = NexusLookup.PageUrl(modId), Confidence = by, Via = "nexus" }
                    : new Answer { Resolution = "search", Confidence = "none", Via = "nexus" });
            }
        }

        private static void Store(Query q, Answer a)
        {
            lock (Gate) Answers[Key(q)] = a;
            Interlocked.Increment(ref _version);
            Log?.Invoke($"{Label(q)} -> {a.Resolution} [{a.Confidence}] via {a.Via}"
                        + (string.IsNullOrEmpty(a.Source) ? "" : " " + a.Source)
                        + (string.IsNullOrEmpty(a.Url) ? "" : " " + a.Url));
        }

        private static Dictionary<string, Answer> CachedFor(IEnumerable<Query> rows)
        {
            var map = new Dictionary<string, Answer>(StringComparer.Ordinal);
            lock (Gate)
                foreach (var q in rows)
                {
                    string k = Key(q);
                    if (Answers.TryGetValue(k, out var a)) map[k] = a;
                }
            return map;
        }

        private static string Label(Query q) => $"{q.Name} {q.Version}".Trim();

        /// <summary>The request body: {"mods":[{name,version,file,sha256}]}.</summary>
        internal static string BuildRequestJson(IList<Query> rows) =>
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["mods"] = rows.Take(MaxRows).Select(q => new Dictionary<string, string>
                {
                    ["name"] = q.Name ?? "",
                    ["version"] = q.Version ?? "",
                    ["file"] = q.File ?? "",
                    ["sha256"] = (q.Sha256 ?? "").ToLowerInvariant(),
                }).ToList(),
            });

        /// <summary>Parse {"ok":true,"results":[...]}, one result per request row in the same order. Null when the
        /// body is not that shape or the count differs - the caller treats it as the backend being unavailable.</summary>
        internal static List<Answer> ParseAnswers(string json, int expected)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                {
                    Log?.Invoke("backend answer has no results list");
                    return null;
                }
                if (results.GetArrayLength() != expected)
                {
                    Log?.Invoke($"backend answered {results.GetArrayLength()} row(s) for {expected}");
                    return null;
                }
                var list = new List<Answer>();
                foreach (var r in results.EnumerateArray())
                    list.Add(new Answer
                    {
                        Resolution = Str(r, "resolution"),
                        Url = Str(r, "url"),
                        Source = Str(r, "source"),
                        Confidence = Str(r, "confidence") is { Length: > 0 } c ? c : "none",
                    });
                return list;
            }
            catch (Exception e)
            {
                Log?.Invoke("backend answer could not be read: " + e.Message);
                return null;
            }
        }

        private static string Str(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";

        /// <summary>Test seam: forget every answer so a lookup runs again.</summary>
        internal static void ResetCache()
        {
            lock (Gate) { Answers.Clear(); InFlight.Clear(); }
        }
    }
}
