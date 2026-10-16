using System;
using System.Collections.Generic;
using DooDesch.UI;
using S1API.UI;
using SideHustle.Config;
using SideHustle.Multiplayer;
using UnityEngine;
using UnityEngine.UI;

namespace SideHustle.Menu
{
    /// <summary>
    /// The lobby browser rendered on the cloned native panel (the counterpart to the host form): a toolbar with a
    /// search field, filter switches and a sort, a scrollable list of lobby cards, and a Back / Refresh footer.
    ///
    /// One controller serves the gamemode browser, the not-installed gamemode browser and the vanilla browser; they
    /// differ only in the query they start and what happens on Join. The controller owns everything about the list's
    /// life: the first search with its timeout, the quiet re-query every few seconds, and the rebuild that keeps the
    /// scroll position and the typed search text.
    /// </summary>
    internal static class JoinBrowserView
    {
        private const float ToolbarHeight = 92f;

        /// <summary>How long a search may take before the list says Steam did not answer.</summary>
        private const float SearchTimeoutSeconds = 10f;

        /// <summary>Gap between quiet re-queries while the browser is open.</summary>
        private const float AutoRefreshSeconds = 10f;

        // The public web lobby directory. Always the live site regardless of any LobbyDirectory API-base override.
        private const string WebDirectoryUrl = "https://sidehustle.doodesch.de";

        /// <summary>What a browser needs from the screen that opens it.</summary>
        internal sealed class Options
        {
            /// <summary>Start a search; the callback delivers the full lobby list on the main thread, once.</summary>
            public Action<Action<List<LobbyRow>>> Query;
            public Action<LobbyRow> OnJoin;
            public Action OnBack;
            /// <summary>The gamemode's build fingerprint; a host build that differs is flagged.</summary>
            public string LocalBuild;
            /// <summary>The empty state's "Host a lobby" button. Null hides it (a gamemode the player has not installed).</summary>
            public Action OnHost;
            /// <summary>The reason a lobby cannot be joined right now, or null. Shown on a disabled Join.</summary>
            public Func<LobbyRow, string> JoinBlock;
            /// <summary>Lobbies to show before the first search returns (already known, e.g. the advertised cache).</summary>
            public List<LobbyRow> Initial;
            /// <summary>Footer buttons in the centre instead of the "Browse online" link (the not-installed browser:
            /// download the mod, add it to a profile). Null or empty keeps the link.</summary>
            public List<KeyValuePair<string, Action>> Extras;
        }

        private static Browser _current;

        /// <summary>Pumped every frame by the hub: drives the search timeout, the quiet re-query and live search typing.</summary>
        internal static void Tick() => _current?.Update();

        /// <summary>Build the browser on <paramref name="formHost"/> and start its first search.</summary>
        internal static Browser Build(Transform formHost, Options o)
        {
            var b = new Browser(formHost, o);
            _current = b;
            b.Start();
            return b;
        }

        internal sealed class Browser
        {
            private readonly Options _o;
            private readonly string _tag;
            private Transform _content;
            private ScrollRect _scroll;
            private InputField _search;

            private List<LobbyRow> _rows;        // the last full result; null until a search or the initial set arrives
            private int _gen;                    // identifies the search whose answer is still wanted
            private bool _awaiting;
            private bool _silent;                // the running search is a quiet re-query: the list stays on screen
            private bool _timedOut;
            private float _queryAt;
            private float _activityAt;           // last result (or timeout): the quiet re-query counts from here
            private string _shownSig;            // what the cards on screen were built from; a rebuild with the same value is skipped
            private string _lastSearch = "";
            private bool _rendered;
            private readonly List<Toggle> _toggles = new List<Toggle>();

            internal Browser(Transform formHost, Options o)
            {
                _o = o;
                _tag = "[browser]";

                // No SmoothScroll: Render restores the scroll offset itself after every rebuild.
                var page = Scaffold.Create(formHost, topInset: ToolbarHeight + 8f, spacing: 8f, smoothScroll: false);
                _content = page.Content;
                _scroll = page.Scroll;
                BuildToolbar(formHost);

                Scaffold.FooterButton(page.Footer, "Back", Theme.Button, left: true, onClick: _o.OnBack);
                Scaffold.FooterButton(page.Footer, "Refresh", Theme.Accent, left: false, onClick: () => StartQuery(silent: false), width: 160f);

                // Centre: the screen's own extras, or the link to the public web directory (SideHustle.doodesch.de) -
                // every open lobby, filterable, in a browser. Opens via the Steam overlay; joining still happens in-game.
                var centre = _o.Extras != null && _o.Extras.Count > 0
                    ? _o.Extras
                    : new List<KeyValuePair<string, Action>> { new KeyValuePair<string, Action>("Browse online", () => DownloadLink.Open(WebDirectoryUrl)) };
                const float centreW = 190f, centreGap = 10f;
                float x0 = -(centre.Count * centreW + (centre.Count - 1) * centreGap) / 2f + centreW / 2f;
                for (int i = 0; i < centre.Count; i++)
                {
                    var act = centre[i].Value;
                    var (cGO, cBtn, _c) = UIFactory.ButtonWithLabel("Centre" + i, centre[i].Key, page.Footer, Theme.Button, centreW, Scaffold.ButtonHeight);
                    var crt = cGO.GetComponent<RectTransform>();
                    crt.anchorMin = crt.anchorMax = new Vector2(0.5f, 0.5f); crt.pivot = new Vector2(0.5f, 0.5f);
                    crt.anchoredPosition = new Vector2(x0 + i * (centreW + centreGap), 0); crt.sizeDelta = new Vector2(centreW, 40);
                    cBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => act?.Invoke()));
                }

                Interactions.PolishButtons(formHost);
            }

            private void BuildToolbar(Transform formHost)
            {
                var bar = NewPanel("toolbar", formHost);
                var brt = bar.GetComponent<RectTransform>();
                brt.anchorMin = new Vector2(0, 1); brt.anchorMax = new Vector2(1, 1); brt.pivot = new Vector2(0.5f, 1);
                brt.offsetMin = new Vector2(Scaffold.Pad, -ToolbarHeight); brt.offsetMax = new Vector2(-Scaffold.Pad, -4);

                _search = Components.TextInput(bar.transform, "", _ => { }, "Search by lobby or host name", 40);
                var irt = _search.GetComponent<RectTransform>();
                irt.anchorMin = new Vector2(0, 1); irt.anchorMax = new Vector2(1, 1); irt.pivot = new Vector2(0.5f, 1);
                irt.offsetMin = new Vector2(0, -40); irt.offsetMax = new Vector2(-250, 0);

                var sort = Components.Segmented(bar.transform, new[] { "Most players", "Name" },
                    Preferences.BrowserSortByName ? 1 : 0,
                    i => { Preferences.BrowserSortByName = i == 1; Render(force: true); }, out _);
                var sgrt = sort.GetComponent<RectTransform>();
                sgrt.anchorMin = sgrt.anchorMax = new Vector2(1, 1); sgrt.pivot = new Vector2(1, 1);
                sgrt.sizeDelta = new Vector2(236, 40); sgrt.anchoredPosition = Vector2.zero;

                AddSwitch(bar.transform, "Hide full", 0f, Preferences.BrowserHideFull, on => Preferences.BrowserHideFull = on);
                AddSwitch(bar.transform, "Hide locked", 170f, Preferences.BrowserHideLocked, on => Preferences.BrowserHideLocked = on);
                AddSwitch(bar.transform, "Hide other game version", 360f, Preferences.BrowserHideOtherVersion, on => Preferences.BrowserHideOtherVersion = on);
            }

            private void AddSwitch(Transform bar, string label, float x, bool value, Action<bool> save)
            {
                var t = Components.Toggle(bar, value, on => { save(on); Render(force: true); });
                _toggles.Add(t);
                var trt = t.GetComponent<RectTransform>();
                trt.anchorMin = trt.anchorMax = new Vector2(0, 1); trt.pivot = new Vector2(0, 0.5f);
                trt.anchoredPosition = new Vector2(x, -64f);

                var text = UIFactory.Text("label", label, bar, Theme.Label, TextAnchor.MiddleLeft);
                text.color = Theme.TextPrimary; text.raycastTarget = false;
                var lrt = text.rectTransform;
                lrt.anchorMin = lrt.anchorMax = new Vector2(0, 1); lrt.pivot = new Vector2(0, 0.5f);
                lrt.sizeDelta = new Vector2(190, 26); lrt.anchoredPosition = new Vector2(x + 54f, -64f);
            }

            private bool Alive => _content != null;

            /// <summary>Show what is already known, then search. A browser opened with a known set (the advertised
            /// cache) paints it at once and freshens it with a quiet search right away: the cache holds only advertised
            /// lobbies, the browser's own query can find more.</summary>
            internal void Start()
            {
                if (_o.Initial != null)
                {
                    _rows = _o.Initial;
                    _activityAt = Time.unscaledTime;
                    Render(force: true);
                    StartQuery(silent: true);
                    return;
                }
                StartQuery(silent: false);
            }

            private void StartQuery(bool silent)
            {
                if (!Alive) return;
                int gen = ++_gen;
                _awaiting = true;
                _silent = silent && _rows != null;
                _queryAt = Time.unscaledTime;
                if (!_silent) ShowStatus("Searching for lobbies...");
                try { _o.Query(rows => OnResults(gen, rows)); }
                catch (Exception e)
                {
                    _awaiting = false;
                    _activityAt = Time.unscaledTime;
                    Core.Log?.Error($"{_tag} the lobby search could not start: {e}");
                    ShowStatus("Couldn't search for lobbies.", "Refresh", () => StartQuery(silent: false));
                }
            }

            private void OnResults(int gen, List<LobbyRow> rows)
            {
                if (gen != _gen || !Alive) return;   // a newer search owns the list, or the screen is gone
                _awaiting = false;
                if (_timedOut) Core.Log?.Msg($"{_tag} the lobby search answered after the timeout.");
                _timedOut = false;
                _rows = rows ?? new List<LobbyRow>();
                _activityAt = Time.unscaledTime;
                Render(force: false);
            }

            internal void Update()
            {
                if (!Alive) { if (_current == this) _current = null; return; }
                if (!_content.gameObject.activeInHierarchy) return;   // the hub is hidden: no timers, no queries
                float now = Time.unscaledTime;

                // Live search: onEndEdit only fires on blur, so the text is read every frame.
                string typed = _search != null ? (_search.text ?? "") : "";
                if (typed != _lastSearch) { _lastSearch = typed; Render(force: true); }

                if (_awaiting)
                {
                    if (now - _queryAt < SearchTimeoutSeconds) return;
                    _awaiting = false;
                    _activityAt = now;
                    Core.Log?.Warning($"{_tag} Steam gave no lobby answer within {SearchTimeoutSeconds:0} s.");
                    if (_silent && _rows != null) return;   // the list on screen is still the best answer there is
                    _timedOut = true;
                    ShowStatus("Steam did not answer.", "Refresh", () => StartQuery(silent: false));
                    return;
                }
                if (now - _activityAt >= AutoRefreshSeconds) StartQuery(silent: true);
            }

            // --- rendering ---

            private void Render(bool force)
            {
                if (!Alive || _rows == null) return;

                var shown = Filtered(out int hidden);
                string sig = Signature(shown, hidden);
                if (!force && _rendered && sig == _shownSig) return;   // nothing the player can see changed
                _shownSig = sig;
                _rendered = true;

                float scrollY = _content.GetComponent<RectTransform>().anchoredPosition.y;
                Clear(_content);

                if (_rows.Count == 0)
                {
                    ShowStatus("No lobbies open.", _o.OnHost != null ? "Host a lobby" : null, _o.OnHost);
                    return;
                }
                if (shown.Count == 0)
                {
                    ShowStatus("No lobbies match.", "Clear filters", ClearFilters);
                    return;
                }
                foreach (var l in shown) BuildCard(_content, l, _o);
                if (hidden > 0) Caption(_content, $"{hidden} hidden by filters.");

                RestoreScroll(scrollY);
            }

            private void RestoreScroll(float y)
            {
                try
                {
                    var rt = _content.GetComponent<RectTransform>();
                    LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
                    float view = _scroll != null && _scroll.viewport != null ? _scroll.viewport.rect.height : 0f;
                    float max = Mathf.Max(0f, rt.rect.height - view);
                    rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, Mathf.Clamp(y, 0f, max));
                }
                catch (Exception e) { Core.Log?.Warning($"{_tag} could not restore the scroll position: {e.Message}"); }
            }

            private void ClearFilters()
            {
                // Setting a switch runs its handler, which saves the preference and re-renders.
                foreach (var t in _toggles) if (t != null && t.isOn) t.isOn = false;
                if (_search != null) _search.text = "";
                _lastSearch = "";
                Core.Log?.Msg($"{_tag} filters cleared.");
                Render(force: true);
            }

            private List<LobbyRow> Filtered(out int hidden)
            {
                string q = (_lastSearch ?? "").Trim();
                bool hideFull = Preferences.BrowserHideFull, hideLocked = Preferences.BrowserHideLocked, hideVersion = Preferences.BrowserHideOtherVersion;
                var list = new List<LobbyRow>();
                hidden = 0;
                foreach (var l in _rows)
                {
                    if (q.Length > 0
                        && (l.LobbyName ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
                        && (l.HostName ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;   // a search narrows, it is not a "hidden" filter
                    if ((hideFull && l.MaxPlayers > 0 && l.Members >= l.MaxPlayers)
                        || (hideLocked && l.HasPassword)
                        || (hideVersion && OtherVersion(l))) { hidden++; continue; }
                    list.Add(l);
                }
                if (Preferences.BrowserSortByName)
                    list.Sort((a, b) => string.Compare(TitleOf(a), TitleOf(b), StringComparison.OrdinalIgnoreCase));
                else
                    list.Sort((a, b) =>
                    {
                        int c = b.Members.CompareTo(a.Members);
                        return c != 0 ? c : string.Compare(TitleOf(a), TitleOf(b), StringComparison.OrdinalIgnoreCase);
                    });
                return list;
            }

            private static string Signature(List<LobbyRow> shown, int hidden)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(hidden).Append('|');
                foreach (var l in shown)
                    sb.Append(l.LobbyId).Append(':').Append(l.Members).Append('/').Append(l.MaxPlayers).Append(l.HasPassword ? 'L' : 'o')
                      .Append(l.LobbyName).Append('~').Append(l.HostName).Append('~').Append(l.Mode).Append('~').Append(l.Runtime).Append(';');
                return sb.ToString();
            }

            // --- status / caption cards ---

            private void ShowStatus(string message, string buttonLabel = null, Action onButton = null)
            {
                if (!Alive) return;
                _shownSig = null;
                Clear(_content);
                bool button = !string.IsNullOrEmpty(buttonLabel) && onButton != null;
                var card = NewCard(_content, button ? 116f : 64f);
                var t = UIFactory.Text("msg", message, card.transform, Theme.Body, button ? TextAnchor.UpperCenter : TextAnchor.MiddleCenter);
                t.color = Theme.TextMuted; t.raycastTarget = false;
                var trt = t.rectTransform;
                trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
                trt.offsetMin = new Vector2(12, button ? 56 : 0); trt.offsetMax = new Vector2(-12, button ? -14 : 0);
                if (!button) return;

                var (go, btn, _) = UIFactory.ButtonWithLabel("action", buttonLabel, card.transform, Theme.Accent, 200, 40);
                var rt = go.GetComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0);
                rt.pivot = new Vector2(0.5f, 0); rt.anchoredPosition = new Vector2(0, 14); rt.sizeDelta = new Vector2(200, 40);
                btn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onButton()));
                Interactions.PolishButtons(card.transform);
            }
        }

        // --- cards ---

        private static string TitleOf(LobbyRow r) =>
            !string.IsNullOrEmpty(r.LobbyName) ? r.LobbyName : string.IsNullOrEmpty(r.HostName) ? "Lobby" : r.HostName;

        /// <summary>The host runs the other game build (IL2CPP against Mono). The two share one Steam lobby list and
        /// cannot play together, so this is whether Join can work at all. A host too old to publish a branch is not
        /// treated as a mismatch - that would be a guess.</summary>
        internal static bool OtherVersion(LobbyRow r) =>
            !string.IsNullOrEmpty(r.Runtime)
            && !string.Equals(r.Runtime, LobbyCoordinator.ThisRuntime, StringComparison.OrdinalIgnoreCase);

        private static void BuildCard(Transform content, LobbyRow row, Options o)
        {
            var card = NewCard(content, 68f);
            string title = TitleOf(row);
            string gm = row.GamemodeName ?? "";
            if (!string.IsNullOrEmpty(row.Mode)) gm = string.IsNullOrEmpty(gm) ? row.Mode : gm + " - " + row.Mode;
            string cap = row.MaxPlayers > 0 ? $"{row.Members} / {row.MaxPlayers} players"
                       : Words.Plural(row.Members, "player", "players");
            string localBuild = o.LocalBuild;
            bool versionMismatch = !string.IsNullOrEmpty(localBuild) && !string.IsNullOrEmpty(row.BuildId)
                                   && !string.Equals(localBuild, row.BuildId, StringComparison.Ordinal);
            bool otherGame = OtherVersion(row);
            string blocked = o.JoinBlock?.Invoke(row);
            string discord = DiscordInvite.ReadFor(row.LobbyId);

            string sub = cap
                       + (string.IsNullOrEmpty(gm) ? "" : "   ·   " + gm)
                       + (!string.IsNullOrEmpty(row.HostName) && !string.Equals(row.LobbyName, row.HostName, StringComparison.Ordinal) ? "   ·   by " + row.HostName : "")
                       + (row.HasPassword ? "   ·   Locked" : "")
                       + (versionMismatch ? "   ·   Different version" : "")
                       + (blocked != null ? "   ·   " + blocked : "");

            // Buttons stack from the right edge; everything to their left clips against the leftmost one.
            float right = 12f;
            Button AddButton(string label, Color color, float width)
            {
                var (go, btn, _) = UIFactory.ButtonWithLabel(label, label, card.transform, color, width, 40);
                var rt = go.GetComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = new Vector2(1, 0.5f); rt.pivot = new Vector2(1, 0.5f);
                rt.anchoredPosition = new Vector2(-right, 0); rt.sizeDelta = new Vector2(width, 40);
                right += width + 8f;
                return btn;
            }

            LobbyRow captured = row;
            var joinBtn = AddButton("Join", Theme.Accent, 96);
            if (otherGame || blocked != null) joinBtn.interactable = false;
            else joinBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => o.OnJoin?.Invoke(captured)));

            // Ask before committing. Shown only when this host advertises that they take messages (sh_msg) - a
            // button onto someone who drops every line would be worse than none - and it opens the same column the
            // sync screens carry, so the conversation survives walking into the join.
            if (ChatPanel.Possible(row.OwnerSteamId, row.AcceptsMessages))
            {
                var chatBtn = AddButton("Chat", Theme.Button, 84);
                chatBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
                    ChatPanel.Toggle(captured.OwnerSteamId, captured.HostName)));
            }

            // Opens only on this click, and only the link rebuilt from the validated code.
            if (discord.Length > 0)
            {
                var dcBtn = AddButton("Discord", Theme.Button, 96);
                dcBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => DiscordInvite.Open(discord)));
            }

            // The other-version warning is a colour and a short phrase rather than another clause in the subtitle: it
            // decides whether Join can work at all.
            float badgeWidth = otherGame ? 138f : 0f;
            var name = UIFactory.Text("name", title, card.transform, Theme.Body, TextAnchor.LowerLeft, FontStyle.Bold);
            name.color = Theme.TextPrimary; name.raycastTarget = false;
            // Clip, do not overflow. A uGUI Text in Overflow mode ignores its rect entirely, so a 40-character lobby
            // name printed straight across the badge and the buttons. One cut line beats a name drawn over Join.
            name.horizontalOverflow = HorizontalWrapMode.Wrap;
            name.verticalOverflow = VerticalWrapMode.Truncate;
            var nrt = name.rectTransform; nrt.anchorMin = new Vector2(0, 0.5f); nrt.anchorMax = new Vector2(1, 1); nrt.offsetMin = new Vector2(16, 0);
            nrt.offsetMax = new Vector2(-(right + badgeWidth), -4);

            if (otherGame)
            {
                var badge = UIFactory.Text("branch", "Other game version", card.transform, Theme.Caption, TextAnchor.LowerRight, FontStyle.Bold);
                badge.color = Theme.DangerText; badge.raycastTarget = false; badge.horizontalOverflow = HorizontalWrapMode.Overflow;
                var brt = badge.rectTransform;
                brt.anchorMin = new Vector2(1, 0.5f); brt.anchorMax = new Vector2(1, 1);
                brt.offsetMin = new Vector2(-(right + badgeWidth), 0); brt.offsetMax = new Vector2(-right, -4);
            }

            var subT = UIFactory.Text("sub", sub, card.transform, Theme.Caption, TextAnchor.UpperLeft);
            // Clip within the card (never draw under the buttons on the right): a long subtitle - e.g. a vanilla
            // lobby's "... · save 'Long Org Name' · synced-only · Locked" - would otherwise overflow across them.
            subT.color = versionMismatch || blocked != null ? Theme.WarningText : Theme.TextMuted; subT.raycastTarget = false;
            subT.horizontalOverflow = HorizontalWrapMode.Wrap; subT.verticalOverflow = VerticalWrapMode.Truncate;
            var srt = subT.rectTransform; srt.anchorMin = new Vector2(0, 0); srt.anchorMax = new Vector2(1, 0.5f); srt.offsetMin = new Vector2(16, 4);
            srt.offsetMax = new Vector2(-right, 0);

            Interactions.PolishButtons(card.transform);
        }

        // --- helpers ---

        private static void Caption(Transform content, string text)
        {
            var row = UIFactory.Panel("hidden", content, Theme.Clear);
            var le = row.AddComponent<LayoutElement>(); le.minHeight = 28f; le.preferredHeight = 28f; le.flexibleWidth = 1;
            var t = UIFactory.Text("text", text, row.transform, Theme.Caption, TextAnchor.MiddleCenter);
            t.color = Theme.TextMuted; t.raycastTarget = false;
            var rt = t.rectTransform; rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        private static GameObject NewCard(Transform content, float height)
        {
            var go = UIFactory.Panel("card", content, Theme.BgElevated);
            var img = go.GetComponent<Image>(); if (img != null) { img.sprite = Theme.RoundedSprite(); img.type = Image.Type.Sliced; img.raycastTarget = false; }
            var le = go.AddComponent<LayoutElement>(); le.minHeight = height; le.preferredHeight = height; le.flexibleWidth = 1;
            return go;
        }

        private static GameObject NewPanel(string name, Transform parent)
        {
            var go = UIFactory.Panel(name, parent, Theme.Clear);
            var img = go.GetComponent<Image>(); if (img != null) img.raycastTarget = false;
            return go;
        }

        // Detach immediately (so the layout doesn't show stale cards for a frame) and destroy.
        private static void Clear(Transform content)
        {
            for (int i = content.childCount - 1; i >= 0; i--)
            {
                var c = content.GetChild(i);
                c.SetParent(null, false);
                UnityEngine.Object.Destroy(c.gameObject);
            }
        }
    }
}
