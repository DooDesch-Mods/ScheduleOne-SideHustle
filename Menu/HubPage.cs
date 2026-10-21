using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DooDesch.UI;
using SideHustle.Phone;

namespace SideHustle.Menu
{
    /// <summary>
    /// One sub-screen of the hub page that used to be a classic uGUI view (settings, profiles, the join checks):
    /// a list of parts the page draws in the hub look. The screen's builder runs again for every state push, so
    /// live values (a download's bytes, a count) follow without the screen keeping its own copy.
    ///
    /// Every clickable part gets an id for the closure behind it. A click comes back as <c>hub.view</c> with that id
    /// and runs the closure of the build the page drew; ids are numbered in build order, so a screen whose shape
    /// is stable keeps its ids from one build to the next.
    /// </summary>
    internal sealed class PageView
    {
        internal readonly string Key;
        internal readonly string Tab;

        /// <summary>The id the page runs on right-click and for the header's back button; "" when there is none.</summary>
        internal string Back = "";

        /// <summary>How the page frames the parts: "board" (one glass panel), "main" (loose cards) or "detail".</summary>
        internal string Frame = "board";

        /// <summary>The screen holds the hub until it is done or cancelled (a download, a restart): the tabs do nothing.</summary>
        internal bool Lock;

        private readonly Json _parts = Json.Array();
        private Json _dialog;
        private readonly Dictionary<string, Action<string>> _acts = new Dictionary<string, Action<string>>(StringComparer.Ordinal);
        private int _next;

        internal PageView(string key, string tab)
        {
            Key = key;
            Tab = tab;
        }

        internal string Act(Action<string> run)
        {
            if (run == null) return "";
            string id = "a" + _next++;
            _acts[id] = run;
            return id;
        }

        internal string Act(Action run) => run == null ? "" : Act(_ => run());

        internal bool TryGet(string id, out Action<string> run) => _acts.TryGetValue(id ?? "", out run);

        internal PageView Add(Json part)
        {
            if (part != null) _parts.Item(part);
            return this;
        }

        internal PageView Add(IEnumerable<Json> parts)
        {
            foreach (var p in parts) Add(p);
            return this;
        }

        internal string ToJson(int seq)
        {
            var j = Json.Object()
                .Add("seq", seq)
                .Add("key", Key ?? "")
                .Add("tab", Tab ?? "play")
                .Add("frame", Frame ?? "board")
                .Add("back", Back ?? "")
                .Add("lock", Lock)
                // The chat column sits over the right edge of the page; the page makes room for it.
                .Add("chat", ChatPanel.IsOpen)
                .Add("parts", _parts);
            if (_dialog != null) j.Add("dialog", _dialog); else j.AddNull("dialog");
            return j.ToString();
        }

        // --------------------------------------------------------------------------------------------------
        // parts
        // --------------------------------------------------------------------------------------------------

        internal static Json Arr(IEnumerable<Json> items)
        {
            var a = Json.Array();
            if (items != null) foreach (var i in items) if (i != null) a.Item(i);
            return a;
        }

        private static Json Part(string type) => Json.Object().Add("t", type);

        /// <summary>The right-click step of a screen without a header.</summary>
        internal void OnBack(Action back)
        {
            string id = Act(back);
            if (id.Length > 0) Back = id;
        }

        /// <summary>A button. <paramref name="cls"/> takes the hub classes: "go", "sm", "big", "bad2", "ok2".</summary>
        internal Json Btn(string label, Action onClick, string cls = "", string icon = "", bool off = false) =>
            Json.Object().Add("label", label ?? "").Add("cls", cls ?? "").Add("icon", icon ?? "")
                .Add("off", off || onClick == null).Add("act", off ? "" : Act(onClick));

        /// <summary>A dialog button: its closure gets the text of the dialog's field.</summary>
        internal Json BtnValue(string label, Action<string> onClick, string cls = "") =>
            Json.Object().Add("label", label ?? "").Add("cls", cls ?? "").Add("icon", "")
                .Add("off", onClick == null).Add("act", Act(onClick));

        /// <summary>Back, title and one line of context, actions on the right. Sets the screen's back step.</summary>
        internal Json Head(string name, string sub, string backLabel, Action onBack, params Json[] acts)
        {
            string back = Act(onBack);
            if (back.Length > 0) Back = back;
            return Part("head").Add("name", name ?? "").Add("sub", sub ?? "").Add("backLabel", backLabel ?? "")
                .Add("back", back).Add("acts", Arr(acts));
        }

        /// <summary>A detail header: initials tile, name and line, actions on the right.</summary>
        internal Json DHead(string mono, string name, string sub, params Json[] acts) =>
            Part("dhead").Add("mono", mono ?? "").Add("name", name ?? "").Add("sub", sub ?? "").Add("acts", Arr(acts));

        internal Json Card(string heading, bool grow, params Json[] kids) =>
            Part("card").Add("h", heading ?? "").Add("grow", grow).Add("kids", Arr(kids));

        internal Json Two(IEnumerable<Json> left, IEnumerable<Json> right) =>
            Part("two").Add("l", Arr(left)).Add("r", Arr(right));

        internal Json Cols(IEnumerable<Json> left, IEnumerable<Json> right) =>
            Part("cols").Add("l", Arr(left)).Add("r", Arr(right));

        internal Json Scroll(IEnumerable<Json> kids) => Part("scroll").Add("kids", Arr(kids));

        internal Json Row(IEnumerable<Json> kids) => Part("line").Add("kids", Arr(kids));

        internal Json Set(string name, string sub, Json control, string note = null) =>
            Part("set").Add("name", name ?? "").Add("sub", sub ?? "").Add("note", note ?? "").Add("ctl", control);

        internal Json Toggle(bool on, Action<bool> set) =>
            Part("tog").Add("opts", Arr(new[] { Text("On"), Text("Off") })).Add("cur", on ? 0 : 1)
                .Add("act", Act(v => set?.Invoke(v == "0")));

        internal Json Seg(IReadOnlyList<string> options, int current, Action<int> pick) =>
            Part("tog").Add("opts", Arr(options.Select(Text))).Add("cur", current)
                .Add("act", Act(v => { if (int.TryParse(v, out int i)) pick?.Invoke(i); }));

        /// <summary>A text field. <paramref name="onInput"/> gets every edit, <paramref name="onCommit"/> Enter and
        /// leaving the field. <paramref name="key"/> keeps the field across rebuilds of the same screen.</summary>
        internal Json Input(string key, string value, string placeholder, Action<string> onInput, Action<string> onCommit = null,
            bool password = false, string cls = "inp") =>
            Part("input").Add("key", key ?? "").Add("value", value ?? "").Add("ph", placeholder ?? "")
                .Add("pw", password).Add("cls", cls ?? "inp")
                .Add("act", Act(onInput)).Add("commit", Act(onCommit));

        internal Json Field(string label, Json control, string cap = null) =>
            Part("field").Add("label", label ?? "").Add("ctl", control).Add("cap", cap ?? "");

        internal Json Stepper(int value, int min, int max, Action<int> set) =>
            Part("step").Add("v", value).Add("min", min).Add("max", max)
                .Add("act", Act(v => { if (int.TryParse(v, out int n)) set?.Invoke(Math.Max(min, Math.Min(max, n))); }));

        /// <summary>A group header inside a list: icon, label, count.</summary>
        internal Json Group(string label, string count = null, string icon = null, string tone = null) =>
            Part("gh").Add("label", label ?? "").Add("count", count ?? "").Add("icon", icon ?? "").Add("tone", tone ?? "");

        /// <summary>A row: name and line on the left, a state or a bar in the middle, actions on the right.</summary>
        internal Json Item(string name, string sub = null, string state = null, string tone = null, IEnumerable<Json> acts = null,
            string icon = null, string iconTone = null, bool dep = false, bool sel = false, string tag = null,
            string mono = null, Action onClick = null, int pct = -1, bool live = false, string pic = null) =>
            Part("row").Add("name", name ?? "").Add("sub", sub ?? "").Add("st", state ?? "").Add("tone", tone ?? "")
                .Add("acts", Arr(acts)).Add("icon", icon ?? "").Add("itone", iconTone ?? "").Add("dep", dep).Add("sel", sel)
                .Add("tag", tag ?? "").Add("mono", mono ?? "").Add("pic", pic ?? "").Add("act", Act(onClick)).Add("pct", pct).Add("live", live);

        /// <summary>A strip that says what will happen, in the colour of its meaning ("now", "need", "bad", "ok").</summary>
        internal Json Say(string tone, string icon, string name, string sub, params Json[] acts) =>
            Part("say").Add("tone", tone ?? "").Add("icon", icon ?? "").Add("name", name ?? "").Add("sub", sub ?? "")
                .Add("acts", Arr(acts));

        /// <summary>A wide bar with its label; <paramref name="tone"/> "done", "bad" or "need" recolours it.</summary>
        internal Json Progress(int pct, string label, string tone = null) =>
            Part("prog").Add("pct", pct).Add("label", label ?? "").Add("tone", tone ?? "");

        /// <summary>A row of buttons: a quiet note or the left buttons, then the right ones at the end.</summary>
        internal Json Foot(string note, IEnumerable<Json> left, IEnumerable<Json> right) =>
            Part("foot").Add("note", note ?? "").Add("l", Arr(left)).Add("r", Arr(right));

        internal Json Quiet(string text, string tone = null) => Part("quiet").Add("text", text ?? "").Add("tone", tone ?? "");

        /// <summary>Pushes what follows it in a line to the right end.</summary>
        internal Json Sp() => Part("sp");

        internal Json Title(string text) => Part("title").Add("text", text ?? "");

        internal Json Heading(string text) => Part("h").Add("text", text ?? "");

        internal Json FNote(string text) => Part("fnote").Add("text", text ?? "");

        /// <summary>The one centred moment of a screen: icon, headline, line, bar and actions.</summary>
        internal Json Big(string icon, string name, string sub, int pct, string label, string barTone, params Json[] acts) =>
            Part("big").Add("icon", icon ?? "").Add("name", name ?? "").Add("sub", sub ?? "").Add("pct", pct)
                .Add("label", label ?? "").Add("tone", barTone ?? "").Add("acts", Arr(acts));

        internal Json Save(string no, string name, string sub, string cash, bool sel, Action onClick, bool empty = false) =>
            Part("save").Add("no", no ?? "").Add("name", name ?? "").Add("sub", sub ?? "").Add("cash", cash ?? "")
                .Add("sel", sel).Add("empty", empty).Add("act", Act(onClick));

        internal Json Saves(IEnumerable<Json> saves) => Part("saves").Add("kids", Arr(saves));

        internal Json Facts(IEnumerable<Json> buttons) => Part("facts").Add("kids", Arr(buttons));

        private static Json Text(string s) => Json.Object().Add("s", s ?? "");

        /// <summary>A dialog over the screen. <paramref name="cancel"/> runs on right-click and on the close button.</summary>
        internal void Dialog(string name, string sub, IEnumerable<string> items, Json input, string error, Action cancel,
            params Json[] acts)
        {
            _dialog = Json.Object().Add("name", name ?? "").Add("sub", sub ?? "")
                .Add("items", Arr((items ?? Enumerable.Empty<string>()).Select(Text)))
                .Add("error", error ?? "").Add("cancel", Act(cancel)).Add("acts", Arr(acts));
            if (input != null) _dialog.Add("input", input); else _dialog.AddNull("input");
        }
    }

    internal static partial class Hub
    {
        // --- the classic view the page draws right now ---

        private static Action<PageView> _pageBuild;
        private static Action<PageView> _pageDialogBuild;
        private static Action _pageLeave;
        private static string _pageKey;
        private static string _pageTab;
        private static PageView _pageLast;
        private static int _pageSeq;

        /// <summary>
        /// Called at the top of a classic view that has a page version. Takes the view when the page can draw it and
        /// returns true, so the caller skips building the uGUI rows. False leaves everything to the classic screen.
        /// </summary>
        private static bool ShowOnPage(string key, string tab, Action<PageView> build, Action onLeave = null)
        {
            if (build == null) return false;
            if (!RouteToHub(tab, null, false)) return false;
            _pageBuild = build;
            _pageLeave = onLeave;
            _pageKey = key;
            _pageTab = tab;
            _pageDialogBuild = null;
            _pageLast = null;
            _pageSeq++;
            _hubSinceState = 999f;
            Core.Log?.Msg($"[hub3] page screen: {key}.");
            return true;
        }

        /// <summary>Like <see cref="ShowOnPage"/>, but when the page already shows <paramref name="key"/> it only swaps
        /// the builder: the tab, an open dialog and the page's own state (scroll, fields) stay.</summary>
        private static bool ShowOnPageKeep(string key, string tab, Action<PageView> build, Action onLeave = null)
        {
            if (build != null && PageScreenUp && string.Equals(_pageKey, key, StringComparison.Ordinal))
            {
                _pageBuild = build;
                _pageLeave = onLeave;
                _hubSinceState = 999f;
                Core.Log?.Msg($"[hub3] page screen: {key} (redrawn).");
                return true;
            }
            return ShowOnPage(key, tab, build, onLeave);
        }

        /// <summary>The page draws one of the classic screens right now.</summary>
        private static bool PageScreenUp => _pageBuild != null && _hubView && HubReady;

        private static void DropPageScreen()
        {
            if (_pageBuild != null) Core.Log?.Msg($"[hub3] page screen '{_pageKey}' closed.");
            var leave = _pageLeave;
            _pageLeave = null;
            if (leave != null) Safe.Try($"closing the page screen '{_pageKey}'", leave);
            _pageBuild = null;
            _pageDialogBuild = null;
            _pageLast = null;
            _pageKey = null;
            _pageByRev.Clear();
            _pageBody = "";
        }

        // Every build whose parts differ from the last one gets the next revision. The page sends back the revision
        // it drew, so a click lands on the closures of that build even when a newer one has moved the ids since.
        private static int _pageRev;
        private static string _pageBody = "";
        private static readonly Dictionary<int, PageView> _pageByRev = new Dictionary<int, PageView>();
        private static string _pageFailed;

        private static string PageJson()
        {
            if (_pageBuild == null) return null;
            var pv = new PageView(_pageKey, _pageTab);
            try
            {
                _pageBuild(pv);
                _pageDialogBuild?.Invoke(pv);
                _pageFailed = null;
            }
            catch (Exception e)
            {
                if (_pageFailed != _pageKey) Core.Log?.Error($"[hub3] drawing the page screen '{_pageKey}' failed: {e}");
                _pageFailed = _pageKey;
                pv = new PageView(_pageKey, _pageTab);
                pv.Add(pv.Head("Something went wrong", null, "Back", ShowGamemodeList));
                pv.Add(pv.Say("bad", "bad", "This screen could not be drawn.", e.Message));
            }

            string body = pv.ToJson(_pageSeq);
            if (body != _pageBody)
            {
                _pageBody = body;
                _pageRev++;
                _pageByRev.Remove(_pageRev - 8);
            }
            _pageByRev[_pageRev] = pv;
            _pageLast = pv;
            return "{\"rev\":" + _pageRev + "," + body.Substring(1);
        }

        /// <summary>A click on a part of the page screen. Answers "" or the sentence the page shows.</summary>
        private static string HubCallView(string arg)
        {
            string key, id, value;
            int rev;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrEmpty(arg) ? "{}" : arg);
                key = Str(doc.RootElement, "key");
                id = Str(doc.RootElement, "id");
                value = Str(doc.RootElement, "value");
                rev = Int(doc.RootElement, "rev", -1);
            }
            catch (Exception e) { Core.Log?.Error("[hub3] view: unreadable request: " + e.Message); return "Something went wrong."; }

            if (!_pageByRev.TryGetValue(rev, out var pv)) pv = _pageLast;
            if (pv == null || _pageBuild == null || !string.Equals(key, _pageKey, StringComparison.Ordinal)
                || !string.Equals(pv.Key, _pageKey, StringComparison.Ordinal))
            {
                Core.Log?.Warning($"[hub3] view: '{id}' on '{key}', but the page screen is '{_pageKey ?? "none"}'.");
                return "";
            }
            if (!pv.TryGet(id, out var run))
            {
                Core.Log?.Warning($"[hub3] view: '{_pageKey}' has no part '{id}'.");
                return "";
            }
            try { run(value ?? ""); }
            catch (Exception e)
            {
                Core.Log?.Error($"[hub3] view: '{id}' on '{_pageKey}' threw: {e}");
                return "Something went wrong.";
            }
            _hubSinceState = 999f;
            return "";
        }

        /// <summary>A tab clicked while the page draws one of the mod's screens: leave it for that tab.</summary>
        private static string HubCallOpen(string tab)
        {
            tab = tab ?? "";
            Core.Log?.Msg($"[hub3] tab '{tab}' leaves the page screen '{_pageKey ?? "none"}'.");
            switch (tab)
            {
                case "play":
                    ShowGamemodeList();
                    return "";
                case "lobbies":
                    InvalidateJoinChecks();
                    _mpDesc = null;
                    _back = ShowGamemodeList;
                    if (!RouteToHub("lobbies", null, false)) Core.Log?.Warning("[hub3] the lobby board could not take the screen.");
                    return "";
                case "settings":
                case "profiles":
                    return HubCallLegacy(tab);
            }
            Core.Log?.Warning($"[hub3] no tab called '{tab}'.");
            return "";
        }

        // --- toasts and dialogs that follow the page ---

        /// <summary>A toast on the page when the page is what the player sees. False: the caller shows its own.</summary>
        internal static bool PageToast(string message, Severity sev)
        {
            if (!_hubShown || _hub == null) return false;
            string tone = sev == Severity.Danger ? "bad" : sev == Severity.Warning ? "need" : sev == Severity.Success ? "ok" : "";
            try
            {
                _hub.Emit("hub.toast", Json.Object().Add("text", message ?? "").Add("tone", tone).ToString());
                Core.Log?.Msg($"[hub3] page toast ({sev}): {message}");
                return true;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[hub3] the page toast failed, using the classic one: " + e.Message);
                return false;
            }
        }

        private static void ClosePageDialog(string why)
        {
            if (_pageDialogBuild == null) return;
            _pageDialogBuild = null;
            _hubSinceState = 999f;
            Core.Log?.Msg($"[hub3] page dialog closed ({why}).");
        }

        /// <summary>Ask before something that cannot be taken back. On the page when it is up, else the classic dialog.</summary>
        private static void AskConfirm(string title, string message, string okLabel, Action onOk, bool danger = false)
        {
            if (PageScreenUp)
            {
                Action<PageView> mine = null;
                mine = pv => pv.Dialog(title, message, null, null, null, () => ClosePageDialog("cancelled"),
                    pv.BtnValue("Cancel", _ => ClosePageDialog("cancelled")),
                    pv.BtnValue(okLabel, _ =>
                    {
                        if (_pageDialogBuild == mine) _pageDialogBuild = null;
                        onOk?.Invoke();
                    }, danger ? "bad" : "go"));
                _pageDialogBuild = mine;
                _hubSinceState = 999f;
                Core.Log?.Msg($"[hub3] page dialog: {title}.");
                return;
            }
            var root = DialogRoot();
            if (root == null) { Core.Log?.Warning($"[hub] no canvas to ask '{title}' on."); return; }
            Components.ConfirmDialog(root, title, message, okLabel, onOk);
        }

        /// <summary>A question with a list of what it concerns and two or more answers.</summary>
        private static void AskChoice(string title, string message, List<string> items, params (string Label, bool Go, bool Danger, Action Run)[] answers)
        {
            if (PageScreenUp)
            {
                Action<PageView> mine = null;
                mine = pv => pv.Dialog(title, message, items, null, null, () => ClosePageDialog("cancelled"),
                    answers.Select(a => pv.BtnValue(a.Label, _ =>
                    {
                        if (_pageDialogBuild == mine) _pageDialogBuild = null;
                        a.Run?.Invoke();
                    }, a.Danger ? "bad" : a.Go ? "go" : "")).ToArray());
                _pageDialogBuild = mine;
                _hubSinceState = 999f;
                Core.Log?.Msg($"[hub3] page dialog: {title}.");
                return;
            }
            var root = DialogRoot();
            if (root == null) { Core.Log?.Warning($"[hub] no canvas to ask '{title}' on."); return; }
            Components.ChoiceDialog(root, title, message, items,
                answers.Select(a => (a.Label, a.Danger ? Theme.Danger : a.Go ? Theme.Accent : Theme.Button, a.Run)).ToArray());
        }

        /// <summary>A question that wants a line of text. <paramref name="onOk"/> answers null to close, or the reason
        /// it refused, which the dialog shows under the field.</summary>
        private static void AskText(string title, string message, string placeholder, string initial, string okLabel, Func<string, string> onOk)
        {
            if (PageScreenUp)
            {
                string draft = initial ?? "";
                string error = null;
                Action<PageView> mine = null;
                mine = pv => pv.Dialog(title, message, null,
                    pv.Input("dialog", draft, placeholder, v => { draft = v; error = null; }, v => Submit(v)),
                    error, () => ClosePageDialog("cancelled"),
                    pv.BtnValue("Cancel", _ => ClosePageDialog("cancelled")),
                    pv.BtnValue(okLabel, v => Submit(v), "go"));
                void Submit(string v)
                {
                    if (v != null) draft = v;
                    string why = onOk?.Invoke(draft);
                    if (why == null) { if (_pageDialogBuild == mine) _pageDialogBuild = null; }
                    else { error = why; Core.Log?.Msg($"[hub3] '{title}' refused: {why}"); }
                    _hubSinceState = 999f;
                }
                _pageDialogBuild = mine;
                _hubSinceState = 999f;
                Core.Log?.Msg($"[hub3] page dialog: {title}.");
                return;
            }
            var root = DialogRoot();
            if (root == null) { Core.Log?.Warning($"[hub] no canvas to ask '{title}' on."); return; }
            Components.PromptDialog(root, title, message, string.IsNullOrEmpty(initial) ? placeholder : initial, okLabel, onOk);
        }

        /// <summary>Two letters for a tile without a picture.</summary>
        private static string Initials(string name)
        {
            var words = (name ?? "").Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => char.IsLetterOrDigit(w[0])).ToList();
            if (words.Count == 0) return "?";
            string s = words.Count > 1 ? $"{words[0][0]}{words[1][0]}" : words[0].Substring(0, Math.Min(2, words[0].Length));
            return s.ToUpperInvariant();
        }
    }
}
