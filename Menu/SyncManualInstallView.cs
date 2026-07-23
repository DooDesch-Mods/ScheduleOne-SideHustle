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

        internal static bool IsActive => _active;

        internal static void Tick()
        {
            if (!_active) return;
            var resolved = ManualInstall.Poll(_pending);
            bool notesChanged = _notesSeen != ManualInstall.NotesVersion;
            _notesSeen = ManualInstall.NotesVersion;
            if (resolved.Count > 0)
            {
                if (resolved.Count <= 3)
                    foreach (var e in resolved) ShowToast($"{Label(e)} - found and verified.", Severity.Success);
                else
                    ShowToast($"{resolved.Count} mods found and verified.", Severity.Success);
                if (!_pending.Any(Pending)) ShowToast("All mods are in - you're ready to continue.", Severity.Success);
            }
            if (resolved.Count > 0 || notesChanged) _refresh?.Invoke();
        }

        internal static void Build(Transform formHost, SyncDiff diff, Action onContinue, Action onBack)
        {
            const float Pad = 30f;
            _pending.Clear();
            _pending.AddRange(diff.Entries.Where(Pending));
            ManualInstall.BeginSession();
            _notesSeen = ManualInstall.NotesVersion;
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
                Note(content, "These aren't on Thunderstore, so grab them in your browser: open each link (or \"Find online\" to search Nexus by name) and start the download - that's it. SideHustle watches " + ManualInstall.WatchedFoldersLabel() + " and installs a matching file automatically. Or skip and continue without them.");

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
                    Place(title, new Vector2(12, -6), new Vector2(0.6f, 1f));
                    string statusText = done2 ? "ready"
                        : entry.ManualNote ?? "waiting - download it and it installs automatically...";
                    var status = UIFactory.Text("status", statusText, row.transform, 13, TextAnchor.LowerLeft);
                    status.color = done2 ? Theme.Success : entry.ManualNote != null ? Theme.WarningText : Theme.TextMuted;
                    Place(status, new Vector2(12, 4), new Vector2(0.6f, 0.55f));

                    if (!done2)
                    {
                        string nxUrl = entry.Mod.Source.StartsWith("nx:", StringComparison.Ordinal) ? entry.Mod.Source.Substring(3) : null;
                        bool hasDirect = DownloadLink.IsAllowed(nxUrl);
                        // Never a dead button: use the host's exact link when it is a trusted URL, else search Nexus by name.
                        var (linkGO, linkBtn, _) = UIFactory.ButtonWithLabel("link", hasDirect ? "Open link" : "Find online", row.transform, Theme.Button, 110f, 34f);
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
            backBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => { _active = false; onBack?.Invoke(); }));

            var (contGO, cBtn, _c) = UIFactory.ButtonWithLabel("Continue", "Continue", footer.transform, Theme.Accent, 220, 40);
            Place2(contGO, left: false);
            continueBtn = cBtn;
            cBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => { _active = false; onContinue?.Invoke(); }));

            Render();
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
            string u = e.Mod.Source.StartsWith("nx:", StringComparison.Ordinal) ? e.Mod.Source.Substring(3) : null;
            DownloadLink.OpenExternal(DownloadLink.IsAllowed(u) ? u : DownloadLink.SearchUrl(ManualQuery(e)));
        }

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

        private static void Place(Text t, Vector2 offset, Vector2 anchorMax)
        {
            var rt = t.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 0); rt.anchorMax = anchorMax; rt.pivot = new Vector2(0, 1);
            rt.offsetMin = new Vector2(offset.x, 0); rt.offsetMax = new Vector2(0, offset.y);
        }

        private static void Place2(GameObject go, bool left)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(left ? 0 : 1, 0.5f); rt.anchorMax = new Vector2(left ? 0 : 1, 0.5f);
            rt.pivot = new Vector2(left ? 0 : 1, 0.5f); rt.anchoredPosition = Vector2.zero;
        }
    }
}
