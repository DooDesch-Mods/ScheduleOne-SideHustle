using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SideHustle.Profiles;

namespace SideHustle.Sync
{
    /// <summary>
    /// Turns a mod NAME into its exact Nexus mod page, so the manual-install checklist can send the player straight
    /// to the download instead of a search-results list. Asks the public Nexus GraphQL API
    /// (api.nexusmods.com/v2/graphql) anonymously: no API key, read-only, one small query per name, scoped to the
    /// Schedule I domain and published mods.
    ///
    /// A name only ever resolves when the match is UNAMBIGUOUS (see <see cref="PickUnique"/>) - the manifest carries
    /// no author, so a name is all we have to go on. Everything else (no hit, several plausible hits, API down,
    /// offline) resolves to nothing and the caller falls back to the Nexus search URL the flow used before, which is
    /// where the player would have landed anyway. Answers are cached per session, so a name is asked at most once.
    ///
    /// Pure BCL on <see cref="ThunderstoreClient"/>'s HTTP transport; worker-thread only, no Unity API - callers read
    /// the cache from the main thread via <see cref="CachedPageUrlOrNull"/> and repaint when
    /// <see cref="ResultsVersion"/> changes.
    /// </summary>
    internal static class NexusLookup
    {
        internal const string Endpoint = "https://api.nexusmods.com/v2/graphql";
        internal const string GameDomain = "schedule1";

        /// <summary>How many candidates one query asks for. Only used to decide ambiguity - a name that matches this
        /// many mods is never resolved anyway.</summary>
        internal const int MaxCandidates = 10;

        /// <summary>Shortest searchable term (letters+digits, spaces excluded). The API itself rejects wildcard values
        /// under 2 characters, and anything that short is far too generic to identify a mod.</summary>
        internal const int MinTermLength = 3;

        private const int LookupTimeoutSeconds = 12;

        /// <summary>Set to false to keep the flow fully offline (search links only).</summary>
        internal static bool Enabled = true;

        /// <summary>Host-provided diagnostics sink (Core.Log in the mod, Console in the test harness) - this type
        /// stays pure BCL, so it cannot log anywhere itself.</summary>
        internal static Action<string> Log;

        private static readonly object Gate = new object();
        // Normalized name -> mod id, 0 meaning "asked, no unique match". Failed lookups are NOT cached, so a later
        // screen retries them.
        private static readonly Dictionary<string, int> Answers = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly HashSet<string> InFlight = new HashSet<string>(StringComparer.Ordinal);
        private static int _version;

        /// <summary>Bumped every time a lookup lands. A UI that shows resolved links repaints when this changes.</summary>
        internal static int ResultsVersion => Volatile.Read(ref _version);

        /// <summary>A mod's Nexus page URL. Mod pages live under the bare game domain - the "/games/" prefix the
        /// site's SEARCH uses 404s here. Same host as the search fallback, so it passes the download-link allowlist
        /// unchanged.</summary>
        internal static string PageUrl(int modId) => $"https://www.nexusmods.com/{GameDomain}/mods/{modId}";

        // Compiled once: SearchTerm runs for every row of a mod list, on every repaint.
        private static readonly Regex NotWordChars = new Regex(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);
        private static readonly Regex BeforeCapital = new Regex(@"(?<=\p{Ll}|\p{N})(?=\p{Lu})", RegexOptions.Compiled);
        private static readonly Regex EndOfAcronym = new Regex(@"(?<=\p{Lu})(?=\p{Lu}\p{Ll})", RegexOptions.Compiled);
        private static readonly Regex Runs = new Regex(@"\s+", RegexOptions.Compiled);

        /// <summary>
        /// A clean search term for a mod name: letters/digits/spaces only, with run-together words split apart.
        ///
        /// Two things make Nexus miss a mod. Special characters (the "&amp;" in "Mod Manager &amp; Phone App") throw off
        /// both the site search and the API, so every run of them collapses to a single space. And a mod whose file
        /// is named in CamelCase is usually LISTED with the spaces in: searching "BigPimpin" finds nothing while
        /// "Big Pimpin" finds it. Splitting at the humps is the direction that works, because Nexus matches a
        /// spaced query against a run-together title ("Net Eye" finds "NetEye") but not the other way round.
        ///
        /// An acronym stays whole: the second pattern only splits before a capital that starts a word, so
        /// "SIAKImperium" becomes "SIAK Imperium" rather than five single letters.
        /// </summary>
        internal static string SearchTerm(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string term = NotWordChars.Replace(name, " ");
            term = BeforeCapital.Replace(term, " ");
            term = EndOfAcronym.Replace(term, " ");
            return Runs.Replace(term, " ").Trim();
        }

        /// <summary>The cache/compare form of a name: <see cref="SearchTerm"/> lowercased.</summary>
        internal static string Normalize(string name) => SearchTerm(name).ToLowerInvariant();

        /// <summary>Whether a normalized term is specific enough to be worth asking about at all.</summary>
        internal static bool IsSearchable(string term) =>
            !string.IsNullOrEmpty(term) && term.Replace(" ", "").Length >= MinTermLength;

        /// <summary>Whether a raw mod name can be looked up (or searched for) at all - a blank or punctuation-only
        /// name gives neither an API match nor a usable search.</summary>
        internal static bool CanLookUp(string modName) => IsSearchable(Normalize(modName));

        /// <summary>
        /// The exact Nexus page for a mod name if it has already been resolved, else null (not asked yet, still in
        /// flight, or no unique match). Never touches the network and never blocks - safe on the main thread; the
        /// caller uses the search URL whenever this returns null.
        /// </summary>
        internal static string CachedPageUrlOrNull(string modName)
        {
            string q = Normalize(modName);
            if (q.Length == 0) return null;
            int id;
            lock (Gate) { if (!Answers.TryGetValue(q, out id)) return null; }
            return id > 0 ? PageUrl(id) : null;
        }

        /// <summary>
        /// Resolve these mod names in the background (one query each, sequentially - a join with many manual mods must
        /// not burst the API). Already-answered and in-flight names are skipped, so calling this again when a screen
        /// reopens is free. Fire-and-forget: on any failure the names simply stay unresolved.
        /// </summary>
        internal static void Prefetch(IEnumerable<string> modNames)
        {
            if (!Enabled || modNames == null) return;
            var todo = new List<string>();
            lock (Gate)
            {
                foreach (var name in modNames)
                {
                    string q = Normalize(name);
                    if (!IsSearchable(q) || Answers.ContainsKey(q) || InFlight.Contains(q)) continue;
                    InFlight.Add(q);
                    todo.Add(q);
                }
            }
            if (todo.Count == 0) return;

            Task.Run(async () =>
            {
                foreach (var q in todo)
                {
                    int? id = null;
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(LookupTimeoutSeconds));
                        id = await ResolveAsync(q, cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception e) { Log?.Invoke($"lookup of \"{q}\" failed: {e.GetType().Name}: {e.Message}"); }

                    lock (Gate)
                    {
                        InFlight.Remove(q);
                        if (id.HasValue) Answers[q] = id.Value;
                    }
                    if (id.HasValue) Interlocked.Increment(ref _version);
                }
            });
        }

        /// <summary>
        /// One live lookup: the mod id for an unambiguous match, 0 when the name stays ambiguous or unknown, and null
        /// when the API could not be reached (not cached - retried later). Worker-thread only.
        /// </summary>
        internal static async Task<int?> ResolveAsync(string modName, CancellationToken ct)
        {
            string term = Normalize(modName);
            if (!Enabled || !IsSearchable(term)) return 0;
            string body = await ThunderstoreClient.PostJsonAsync(Endpoint, BuildRequestJson(term), ct).ConfigureAwait(false);
            if (body == null) return null;
            var mods = ParseMods(body, out int totalCount);
            if (mods == null) return null;
            int id = PickUnique(term, mods, totalCount);
            Log?.Invoke(id > 0
                ? $"\"{term}\" resolved to nexus mod {id} ({mods.Count} candidate(s), {totalCount} total)"
                : $"\"{term}\" not resolved ({totalCount} match(es)) - using the search link");
            return id;
        }

        internal sealed class NexusMod
        {
            public int ModId;
            public string Name;
        }

        /// <summary>The GraphQL request body: published Schedule I mods whose name matches every word of the term.</summary>
        internal static string BuildRequestJson(string term) =>
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["query"] = QueryText,
                ["variables"] = new Dictionary<string, object> { ["game"] = GameDomain, ["name"] = term },
            });

        // The name filter is a WILDCARD: the API matches every word of the term against the mod title independently
        // (so "Mod Manager Phone App" still finds "Mod Manager - Phone App"), which is exactly what the normalized
        // term needs. Adult mods are included - Nexus gates those on its own page, and hiding them would send the
        // player to a search that cannot find their mod either.
        private static readonly string QueryText =
            "query SideHustleModByName($game: String!, $name: String!) {" +
            " mods(filter: {" +
            " gameDomainName: [{ value: $game, op: EQUALS }]," +
            " status: [{ value: \"published\", op: EQUALS }]," +
            " name: [{ value: $name, op: WILDCARD }]" +
            " }, count: " + MaxCandidates + ") {" +
            " totalCount nodes { modId name }" +
            " } }";

        /// <summary>Parse the response. Null when the body is not a usable GraphQL result (transport error page,
        /// GraphQL "errors", schema drift) - the caller then treats the lookup as failed rather than as "no match".</summary>
        internal static List<NexusMod> ParseMods(string json, out int totalCount)
        {
            totalCount = 0;
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                {
                    Log?.Invoke("api reported an error: " + errors[0].ToString());
                    return null;
                }
                if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("mods", out var mods)
                    || mods.ValueKind != JsonValueKind.Object) return null;
                if (mods.TryGetProperty("totalCount", out var tc) && tc.TryGetInt32(out int n)) totalCount = n;

                var list = new List<NexusMod>();
                if (mods.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
                    foreach (var m in nodes.EnumerateArray())
                    {
                        int id = m.TryGetProperty("modId", out var mi) && mi.TryGetInt32(out int v) ? v : 0;
                        string name = m.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                        if (id > 0 && !string.IsNullOrEmpty(name)) list.Add(new NexusMod { ModId = id, Name = name });
                    }
                return list;
            }
            catch { return null; }
        }

        /// <summary>
        /// Pick the ONE mod a name can only mean, or 0 when the player should see the search results instead. In
        /// order: a single candidate whose title equals the name; a single candidate whose title STARTS with the name
        /// at a word boundary (Nexus titles are usually "Name - tagline", e.g. "Siesta - NPC Performance LOD"); or a
        /// name that matches exactly one published mod in the whole game. Two candidates that qualify equally are
        /// never guessed between - a wrong page costs the player a wasted download, the search costs one click.
        /// </summary>
        internal static int PickUnique(string term, List<NexusMod> candidates, int totalCount)
        {
            if (string.IsNullOrEmpty(term) || candidates == null || candidates.Count == 0) return 0;

            var exact = candidates.Where(m => Normalize(m.Name) == term).ToList();
            if (exact.Count > 0) return exact.Count == 1 ? exact[0].ModId : 0;

            var titled = candidates.Where(m => Normalize(m.Name).StartsWith(term + " ", StringComparison.Ordinal)).ToList();
            if (titled.Count > 0) return titled.Count == 1 ? titled[0].ModId : 0;

            return totalCount == 1 && candidates.Count == 1 ? candidates[0].ModId : 0;
        }

        /// <summary>Test seam: forget every answer so a lookup runs again.</summary>
        internal static void ResetCache()
        {
            lock (Gate) { Answers.Clear(); InFlight.Clear(); }
        }
    }
}
