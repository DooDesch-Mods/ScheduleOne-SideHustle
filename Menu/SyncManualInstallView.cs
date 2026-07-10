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
    /// The manual-install checklist for a sync join: one row per nx: mod the host sourced from a download link,
    /// with "Open link" (allowlisted host), "Open folder" (the staging dir), and a live status that flips to
    /// done when the dropped file's hash matches the manifest. Continue is enabled once nothing is pending (the
    /// player may also proceed, leaving unresolved mods to drop). Ticked from Core.OnUpdate while open, so the
    /// folder poll runs without a watcher.
    /// </summary>
    internal static class SyncManualInstallView
    {
        private static readonly List<DiffEntry> _pending = new List<DiffEntry>();
        private static Action _refresh;
        private static bool _active;

        internal static bool IsActive => _active;

        internal static void Tick()
        {
            if (!_active) return;
            var resolved = ManualInstall.Poll(_pending);
            if (resolved.Count > 0) _refresh?.Invoke();
        }

        internal static void Build(Transform formHost, SyncDiff diff, Action onContinue, Action onBack)
        {
            const float Pad = 30f;
            _pending.Clear();
            _pending.AddRange(diff.Entries.Where(e => e.Status == DiffStatus.Manual));
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
                Components.SectionHeader(content, "Install these manually");
                Note(content, "Open each link, download the mod, then click \"Open folder\" and drop the file in. It verifies automatically.");

                foreach (var e in _pending)
                {
                    var entry = e;
                    bool done = entry.Status != DiffStatus.Manual;
                    var row = UIFactory.Panel("m_" + entry.Mod.File, content, Theme.BgElevated);
                    var rle = row.AddComponent<LayoutElement>();
                    rle.minHeight = 54f; rle.preferredHeight = 54f; rle.flexibleWidth = 1;

                    var title = UIFactory.Text("name", (done ? "✓ " : "") + Label(entry), row.transform, 16, TextAnchor.UpperLeft, FontStyle.Bold);
                    Place(title, new Vector2(12, -6), new Vector2(0.6f, 1f));
                    var status = UIFactory.Text("status", done ? "ready" : "waiting for the file...", row.transform, 13, TextAnchor.LowerLeft);
                    status.color = done ? Theme.Success : Theme.TextMuted;
                    Place(status, new Vector2(12, 4), new Vector2(0.6f, 0.55f));

                    if (!done)
                    {
                        string url = entry.Mod.Source.StartsWith("nx:", StringComparison.Ordinal) ? entry.Mod.Source.Substring(3) : null;
                        var (linkGO, linkBtn, _) = UIFactory.ButtonWithLabel("link", "Open link", row.transform, Theme.Button, 110f, 34f);
                        var lrt2 = linkGO.GetComponent<RectTransform>();
                        lrt2.anchorMin = new Vector2(1, 0.5f); lrt2.anchorMax = new Vector2(1, 0.5f); lrt2.pivot = new Vector2(1, 0.5f);
                        lrt2.anchoredPosition = new Vector2(-134f, 0f);
                        linkBtn.interactable = DownloadLink.IsAllowed(url);
                        linkBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => { if (DownloadLink.IsAllowed(url)) DownloadLink.Open(url); }));

                        var (folderGO, folderBtn, _2) = UIFactory.ButtonWithLabel("folder", "Open folder", row.transform, Theme.Button, 120f, 34f);
                        var frt2 = folderGO.GetComponent<RectTransform>();
                        frt2.anchorMin = new Vector2(1, 0.5f); frt2.anchorMax = new Vector2(1, 0.5f); frt2.pivot = new Vector2(1, 0.5f);
                        frt2.anchoredPosition = new Vector2(-8f, 0f);
                        folderBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(OpenStaging));
                    }
                }

                bool anyPending = _pending.Any(e => e.Status == DiffStatus.Manual);
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
