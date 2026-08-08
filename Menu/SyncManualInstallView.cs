using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DooDesch.UI;
using S1API.UI;
using SideHustle.Sync;
using UnityEngine;
using UnityEngine.UI;

namespace SideHustle.Menu
{
    /// <summary>
    /// The manual-install checklist for a sync join: one row per nx: mod the host sourced from a download link.
    /// "Open next" walks the player link by link; each download is picked up automatically by the folder watcher
    /// (Downloads folder, Vortex downloads, the drop/staging folder), hash-verified against the manifest and
    /// toasted when it lands. Near-miss files (wrong version, unreadable archive) surface as per-row hints.
    /// Continue is enabled once nothing is pending (the player may also proceed, leaving unresolved mods to
    /// drop). Ticked from Core.OnUpdate while open, so the folder poll runs without a watcher.
    /// </summary>
    internal static class SyncManualInstallView
    {
        private static readonly List<DiffEntry> _pending = new List<DiffEntry>();
        private static Action _refresh;
        private static bool _active;
        private static int _notesSeen;
        private static int _lookupsSeen;

        internal static bool IsActive => _active;

        /// <summary>Start resolving the exact Nexus page for every mod this diff can only offer by name, so the
        /// checklist's links point at the mod itself instead of a search. Called from the consent screen too - the
        /// answers are usually in by the time the checklist opens, and a repeat call is free.</summary>
        internal static void PrefetchLinks(SyncDiff diff)
        {
            if (diff?.Entries == null) return;
            NexusLookup.Prefetch(diff.Entries.Where(NeedsLookup).Select(ManualQuery));
        }

        internal static void Tick()
        {
            if (!_active) return;
            var resolved = ManualInstall.Poll(_pending);
            // A new watcher hint or a landed Nexus lookup both change what a row shows, so either repaints.
            bool rowsChanged = _notesSeen != ManualInstall.NotesVersion || _lookupsSeen != NexusLookup.ResultsVersion;
            _notesSeen = ManualInstall.NotesVersion;
            _lookupsSeen = NexusLookup.ResultsVersion;
            if (resolved.Count > 0)
            {
                if (resolved.Count <= 3)
                    foreach (var e in resolved) ShowToast($"{Label(e)} - found and verified.", Severity.Success);
                else
                    ShowToast($"{resolved.Count} mods found and verified.", Severity.Success);
                if (!_pending.Any(Pending)) ShowToast("All mods are in - you're ready to continue.", Severity.Success);
            }
            if (resolved.Count > 0 || rowsChanged) _refresh?.Invoke();
        }

        /// <summary>The host of the lobby this checklist belongs to, so the player can ask them about a mod they
        /// cannot get. Zero when unknown (the gamemode path does not carry it), which hides the button rather than
        /// offering one that goes nowhere.</summary>
        private static ulong _hostSteamId;
        private static string _hostName = "";

        /// <summary>Name the host before building, so "Ask the host" appears. Separate from Build because the two
        /// callers know different amounts: the vanilla browser has the owner id, the gamemode path does not.</summary>
        internal static void SetHost(ulong steamId, string name)
        {
            _hostSteamId = steamId;
            _hostName = name ?? "";
        }

        internal static void Build(Transform formHost, SyncDiff diff, Action onContinue, Action onBack)
        {
            const float Pad = 30f;
            _pending.Clear();
            _pending.AddRange(diff.Entries.Where(Pending));
            ManualInstall.BeginSession();
            _notesSeen = ManualInstall.NotesVersion;
            _lookupsSeen = NexusLookup.ResultsVersion;
            PrefetchLinks(diff);
            _active = true;

            var footer = UIFactory.Panel("footer", formHost, Theme.Clear);
            var frt = footer.GetComponent<RectTransform>();
            frt.anchorMin = new Vector2(0, 0); frt.anchorMax = new Vector2(1, 0); frt.pivot = new Vector2(0.5f, 0);
            frt.offsetMin = new Vector2(Pad, 0); frt.offsetMax = new Vector2(-Pad, 56);

            var listArea = UIFactory.Panel("scrollArea", formHost, Theme.Clear);
            var lrt = listArea.GetComponent<RectTransform>();
            lrt.anchorMin = new Vector2(0, 0); lrt.anchorMax = new Vector2(1, 1);
            lrt.offsetMin = new Vector2(Pad, 64); lrt.offsetMax = new Vector2(-Pad, 0);
            var content = Components.ScrollList(listArea.transform, out var scroll, 6f, Theme.ScrimPanel);
            SmoothScroll.Attach(scroll);

            Button continueBtn = null;

            void Render()
            {
                if (content == null) return;
                UIFactory.ClearChildren(content);
                int total = _pending.Count;
                int done = _pending.Count(e => !Pending(e));
                Components.SectionHeader(content, total > 0 ? $"Install these manually - {done} of {total} ready" : "Install these manually");
                Note(content, "Open a link and download it - SideHustle finds the file in " + ManualInstall.WatchedFoldersLabel()
                              + " and installs it. Or skip these.");

                if (_pending.Any(Pending))
                {
                    var allRow = UIFactory.Panel("openAll", content, Theme.Clear);
                    var arle = allRow.AddComponent<LayoutElement>();
                    arle.minHeight = 44f; arle.preferredHeight = 44f; arle.flexibleWidth = 1;
                    var (nextGO, nextBtn, _n) = UIFactory.ButtonWithLabel("openNextBtn", "Open next link", allRow.transform, Theme.Accent, 170f, 36f);
                    var nrt = nextGO.GetComponent<RectTransform>();
                    nrt.anchorMin = new Vector2(0, 0.5f); nrt.anchorMax = new Vector2(0, 0.5f); nrt.pivot = new Vector2(0, 0.5f);
                    nrt.anchoredPosition = new Vector2(12f, 0f);
                    nextBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(OpenNext));
                    var (allGO, allBtn, _a) = UIFactory.ButtonWithLabel("openAllBtn", "Open all in browser", allRow.transform, Theme.Button, 200f, 36f);
                    var art = allGO.GetComponent<RectTransform>();
                    art.anchorMin = new Vector2(0, 0.5f); art.anchorMax = new Vector2(0, 0.5f); art.pivot = new Vector2(0, 0.5f);
                    art.anchoredPosition = new Vector2(194f, 0f);
                    allBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(OpenAll));
                }

                foreach (var e in _pending)
                {
                    var entry = e;
                    bool done2 = !Pending(entry);
                    var row = UIFactory.Panel("m_" + entry.Mod.File, content, Theme.BgElevated);
                    var rle = row.AddComponent<LayoutElement>();
                    rle.minHeight = 54f; rle.preferredHeight = 54f; rle.flexibleWidth = 1;

                    var title = UIFactory.Text("name", (done2 ? "✓ " : "") + Label(entry), row.transform, 16, TextAnchor.UpperLeft, FontStyle.Bold);
                    Place(title, new Vector2(12, -RowPad), new Vector2(0.6f, 1f));
                    string statusText = done2 ? "ready" : entry.ManualNote ?? "waiting for the download...";
                    var status = UIFactory.Text("status", statusText, row.transform, 13, TextAnchor.LowerLeft);
                    status.color = done2 ? Theme.Success : entry.ManualNote != null ? Theme.WarningText : Theme.TextMuted;
                    Place(status, new Vector2(12, 4), new Vector2(0.6f, 0.55f), bottom: RowPad);

                    if (!done2)
                    {
                        // Never a dead button: the host's exact link when it is a trusted URL, else the mod's own Nexus
                        // page once the name lookup has identified it, else a Nexus search for the name.
                        bool hasDirect = DownloadLink.IsAllowed(LinkUrl(entry));
                        string label = hasDirect ? "Open link"
                            : DownloadLink.HasNexusPage(ManualQuery(entry)) ? "Open Nexus" : "Find online";
                        var (linkGO, linkBtn, _) = UIFactory.ButtonWithLabel("link", label, row.transform, Theme.Button, 110f, 34f);
                        var lrt2 = linkGO.GetComponent<RectTransform>();
                        lrt2.anchorMin = new Vector2(1, 0.5f); lrt2.anchorMax = new Vector2(1, 0.5f); lrt2.pivot = new Vector2(1, 0.5f);
                        lrt2.anchoredPosition = new Vector2(-134f, 0f);
                        linkBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => OpenFor(entry)));

                        var (folderGO, folderBtn, _2) = UIFactory.ButtonWithLabel("folder", "Open folder", row.transform, Theme.Button, 120f, 34f);
                        var frt2 = folderGO.GetComponent<RectTransform>();
                        frt2.anchorMin = new Vector2(1, 0.5f); frt2.anchorMax = new Vector2(1, 0.5f); frt2.pivot = new Vector2(1, 0.5f);
                        frt2.anchoredPosition = new Vector2(-8f, 0f);
                        folderBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(OpenStaging));
                    }
                }

                bool anyPending = _pending.Any(Pending);
                if (continueBtn != null)
                {
                    var lbl = continueBtn.GetComponentInChildren<Text>();
                    if (lbl != null) lbl.text = anyPending ? "Skip missing & continue" : "Continue";
                }
                Interactions.PolishButtons(formHost);
            }
            _refresh = Render;

            var (backGO, backBtn, _b) = UIFactory.ButtonWithLabel("Back", "Back", footer.transform, Theme.Button, 140, 40);
            Place2(backGO, left: true);

            // The one thing this screen could never do: say something back. A mod the host built themselves is not
            // on Nexus and never will be - the only way forward is to ask them. Goes straight to their SteamID over
            // Steam P2P, so it needs no lobby seat and no friendship.
            if (_hostSteamId != 0UL)
            {
                var (askGO, askBtn, _a) = UIFactory.ButtonWithLabel("AskHost", "Ask the host", footer.transform, Theme.Button, 170, 40);
                var art2 = askGO.GetComponent<RectTransform>();
                art2.anchorMin = art2.anchorMax = new Vector2(0, 0.5f);
                art2.pivot = new Vector2(0, 0.5f);
                art2.anchoredPosition = new Vector2(156f, 0f);
                askBtn.onClick.AddListener((UnityEngine.Events.UnityAction)AskHost);
            }
            backBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => { _active = false; onBack?.Invoke(); }));

            var (contGO, cBtn, _c) = UIFactory.ButtonWithLabel("Continue", "Continue", footer.transform, Theme.Accent, 220, 40);
            Place2(contGO, left: false);
            continueBtn = cBtn;
            cBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => { _active = false; onContinue?.Invoke(); }));

            Render();
        }

        /// <summary>Ask the host about what is missing. One line, sent and forgotten - there is no inbox on this
        /// screen, and building one for a menu that exists for thirty seconds would be the wrong shape. The answer
        /// arrives in the host's phone; if they let you in, you see it because the lobby changes.</summary>
        private static void AskHost()
        {
            var root = Hub.DialogRootStatic();
            if (root == null) return;
            string who = string.IsNullOrEmpty(_hostName) ? "the host" : _hostName;
            Components.PromptDialog(root, "Ask " + who,
                "They see this on their phone, in the Side Hustle app. Say which mod you cannot get - they are the "
                + "only one who can send it to you or let you in without it.",
                "for example: where do I get Sideload?", "Send",
                text =>
                {
                    text = (text ?? "").Trim();
                    if (text.Length == 0) return "Write something first.";
                    if (!Phone.ChatRelay.Send(_hostSteamId, text)) return "Steam would not send that. Try again.";
                    ShowToast("Sent to " + who + ".", Severity.Success);
                    return null;
                });
        }

        private static void OpenStaging()
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + ManualInstall.StagingDir() + "\"") { UseShellExecute = true }); }
            catch (Exception e) { Core.Log?.Warning("[sync] open folder failed: " + e.Message); }
        }

        private static string Label(DiffEntry e) => string.IsNullOrEmpty(e.Mod.Name) ? e.Mod.File : $"{e.Mod.Name} {e.Mod.Version}";

        // Downloads need the player's real browser (login/cookies + an actual file download the Steam overlay
        // can't do), so every link here opens externally.
        private static void OpenFor(DiffEntry e)
        {
            string u = LinkUrl(e);
            DownloadLink.OpenExternal(DownloadLink.IsAllowed(u) ? u : DownloadLink.NexusUrl(ManualQuery(e)));
        }

        // The URL the host published for this mod ("nx:<url>"), or null when the mod came without a source.
        private static string LinkUrl(DiffEntry e) =>
            e.Mod.Source != null && e.Mod.Source.StartsWith("nx:", StringComparison.Ordinal) ? e.Mod.Source.Substring(3) : null;

        // A row that has no usable link of its own, so it needs the name -> Nexus page lookup.
        private static bool NeedsLookup(DiffEntry e) => Pending(e) && !DownloadLink.IsAllowed(LinkUrl(e));

        // The guided flow: one click opens the first still-pending mod's page; once its file lands, the next
        // click opens the next one.
        private static void OpenNext()
        {
            var e = _pending.FirstOrDefault(Pending);
            if (e != null) OpenFor(e);
        }

        private static void OpenAll()
        {
            foreach (var e in _pending.Where(Pending)) OpenFor(e);
        }

        // A row still to satisfy: an nx: link mod (Manual) or a source-less Nexus-only mod (Dropped). Both are
        // fetched by hand and verified by hash; a resolved one flips to Cached and no longer counts as pending.
        private static bool Pending(DiffEntry e) => e.Status == DiffStatus.Manual || e.Status == DiffStatus.Dropped;

        // The search term for a manual mod: its name, or the DLL file name without extension when the name is blank.
        private static string ManualQuery(DiffEntry e)
        {
            if (!string.IsNullOrEmpty(e.Mod.Name)) return e.Mod.Name;
            var f = e.Mod.File ?? "";
            return f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? f.Substring(0, f.Length - 4) : f;
        }

        private static void ShowToast(string message, Severity sev)
        {
            try
            {
                Toast.Init(Hub.DialogRootStatic());
                Toast.Show(message, sev);
            }
            catch { /* menu scene mid-transition */ }
        }

        private static void Note(RectTransform content, string text)
        {
            var row = UIFactory.Panel("note", content, Theme.Clear);
            var rle = row.AddComponent<LayoutElement>();
            rle.minHeight = 40f; rle.preferredHeight = 40f; rle.flexibleWidth = 1;
            var t = UIFactory.Text("text", text, row.transform, 14, TextAnchor.MiddleLeft);
            t.color = Theme.TextMuted;
            var rt = t.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = new Vector2(12, 0); rt.offsetMax = new Vector2(-12, 0);
        }

        // Both texts in a row are anchored to a row EDGE (title top-left, status bottom-left), so each needs its own
        // inset from that edge - without them the title sticks to the row's top border and the status to its bottom.
        private const float RowPad = 8f;

        private static void Place(Text t, Vector2 offset, Vector2 anchorMax, float bottom = 0f)
        {
            var rt = t.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 0); rt.anchorMax = anchorMax; rt.pivot = new Vector2(0, 1);
            rt.offsetMin = new Vector2(offset.x, bottom); rt.offsetMax = new Vector2(0, offset.y);
        }

        private static void Place2(GameObject go, bool left)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(left ? 0 : 1, 0.5f); rt.anchorMax = new Vector2(left ? 0 : 1, 0.5f);
            rt.pivot = new Vector2(left ? 0 : 1, 0.5f); rt.anchoredPosition = Vector2.zero;
        }
    }
}
