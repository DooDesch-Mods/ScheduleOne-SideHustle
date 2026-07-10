using System;
using System.Collections.Generic;
using System.Linq;
using DooDesch.UI;
using S1API.UI;
using SideHustle.Profiles;
using UnityEngine;
using UnityEngine.UI;

namespace SideHustle.Menu
{
    /// <summary>
    /// The Thunderstore browser (a form-host view like the host config): a search field over the cached
    /// community index and a scrolling result list with one Install button per package. All index loading runs
    /// on a worker; the UI is only touched via MainThread. Search filters client-side - after one index fetch
    /// everything is local.
    /// </summary>
    internal static class PackageBrowserView
    {
        private const int MaxResults = 40;
        private const float IconSize = 40f;
        private const float InstallW = 96f;
        private const float InfoW = 68f;
        private const float TextLeft = 62f;    // clears the icon (10 + IconSize + gap)
        private const float TextRight = 194f;  // clears the Info + Install buttons on the right

        internal static void Build(Transform formHost, Action onBack, Action<string, string> onInstall)
        {
            const float Pad = 30f;

            var footer = UIFactory.Panel("footer", formHost, Theme.Clear);
            var frt = footer.GetComponent<RectTransform>();
            frt.anchorMin = new Vector2(0, 0); frt.anchorMax = new Vector2(1, 0); frt.pivot = new Vector2(0.5f, 0);
            frt.offsetMin = new Vector2(Pad, 0); frt.offsetMax = new Vector2(-Pad, 56);

            var searchArea = UIFactory.Panel("search", formHost, Theme.Clear);
            var sart = searchArea.GetComponent<RectTransform>();
            sart.anchorMin = new Vector2(0, 1); sart.anchorMax = new Vector2(1, 1); sart.pivot = new Vector2(0.5f, 1);
            sart.offsetMin = new Vector2(Pad, -50); sart.offsetMax = new Vector2(-Pad, 0);

            var listArea = UIFactory.Panel("list", formHost, Theme.Clear);
            var lrt = listArea.GetComponent<RectTransform>();
            lrt.anchorMin = new Vector2(0, 0); lrt.anchorMax = new Vector2(1, 1);
            lrt.offsetMin = new Vector2(Pad, 64); lrt.offsetMax = new Vector2(-Pad, -56);

            var content = Components.ScrollList(listArea.transform, out var scroll, 6f, Theme.ScrimPanel);
            SmoothScroll.Attach(scroll);

            TsIndex index = null;
            string query = "";

            void Render()
            {
                if (content == null) return;
                UIFactory.ClearChildren(content);

                if (index == null)
                {
                    UIFactory.Text("loading", "Loading the package index...", content, 18, TextAnchor.MiddleCenter);
                    return;
                }

                var q = Norm(query);
                var results = index.Packages
                    .Where(p => !p.IsDeprecated && p.Latest != null)
                    .Where(p => q.Length == 0 || Norm(p.Name).Contains(q) || Norm(p.Owner).Contains(q) || Norm(p.FullName).Contains(q))
                    .OrderByDescending(p => p.TotalDownloads)
                    .Take(MaxResults)
                    .ToList();

                if (results.Count == 0)
                {
                    UIFactory.Text("none", "No packages match.", content, 18, TextAnchor.MiddleCenter);
                    return;
                }

                foreach (var pkg in results)
                {
                    var p = pkg;
                    var row = UIFactory.Panel("pkg_" + p.FullName, content, Theme.BgElevated);
                    var rle = row.AddComponent<LayoutElement>();
                    rle.minHeight = 60f; rle.preferredHeight = 60f; rle.flexibleWidth = 1;

                    // Square icon on the left (a rounded placeholder until the real one arrives from Thunderstore).
                    var icon = UIFactory.Panel("icon", row.transform, Theme.BgBase);
                    var iconImg = icon.GetComponent<Image>();
                    if (iconImg != null) { iconImg.sprite = Theme.RoundedSprite(); iconImg.type = Image.Type.Sliced; }
                    var iconRT = icon.GetComponent<RectTransform>();
                    iconRT.anchorMin = new Vector2(0, 0.5f); iconRT.anchorMax = new Vector2(0, 0.5f); iconRT.pivot = new Vector2(0, 0.5f);
                    iconRT.sizeDelta = new Vector2(IconSize, IconSize); iconRT.anchoredPosition = new Vector2(10f, 0f);
                    IconCache.Apply(p.Latest.Icon, iconImg);

                    var title = UIFactory.Text("name", p.Name, row.transform, Theme.H3, TextAnchor.UpperLeft, FontStyle.Bold);
                    PlaceLine(title, topInset: 11f, height: 20f);
                    var meta = UIFactory.Text("meta", $"by {p.Owner} - v{p.Latest.VersionNumber} - {Downloads(p.TotalDownloads)} downloads",
                        row.transform, Theme.Body, TextAnchor.UpperLeft);
                    meta.color = Theme.TextMuted; meta.horizontalOverflow = HorizontalWrapMode.Overflow;
                    PlaceLine(meta, topInset: 32f, height: 16f);

                    var (btnGO, btn, _) = UIFactory.ButtonWithLabel("install", "Install", row.transform, Theme.Accent, InstallW, 36f);
                    var brt = btnGO.GetComponent<RectTransform>();
                    brt.anchorMin = new Vector2(1, 0.5f); brt.anchorMax = new Vector2(1, 0.5f); brt.pivot = new Vector2(1, 0.5f);
                    brt.anchoredPosition = new Vector2(-10f, 0f);
                    btn.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
                        onInstall?.Invoke(p.FullName, p.Latest.VersionNumber)));

                    // Info: open the package's Thunderstore page (Steam overlay browser, external as fallback).
                    var (infoGO, infoBtn, _i) = UIFactory.ButtonWithLabel("info", "Info", row.transform, Theme.Button, InfoW, 36f);
                    var xrt = infoGO.GetComponent<RectTransform>();
                    xrt.anchorMin = new Vector2(1, 0.5f); xrt.anchorMax = new Vector2(1, 0.5f); xrt.pivot = new Vector2(1, 0.5f);
                    xrt.anchoredPosition = new Vector2(-(10f + InstallW + 8f), 0f);
                    infoBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => DownloadLink.Open(p.PackageUrl)));
                }
            }

            var search = Components.TextInput(searchArea.transform, "", v => { query = v ?? ""; Render(); }, "Search packages", 60);
            var srt2 = search.GetComponent<RectTransform>();
            if (srt2 != null)
            {
                srt2.anchorMin = new Vector2(0, 0); srt2.anchorMax = new Vector2(1, 1);
                srt2.offsetMin = Vector2.zero; srt2.offsetMax = Vector2.zero;
            }

            var (backGO, backBtn, _b) = UIFactory.ButtonWithLabel("Back", "Back", footer.transform, Theme.Button, 140, 40);
            var bprt = backGO.GetComponent<RectTransform>();
            bprt.anchorMin = new Vector2(0, 0.5f); bprt.anchorMax = new Vector2(0, 0.5f); bprt.pivot = new Vector2(0, 0.5f);
            bprt.anchoredPosition = new Vector2(0f, 0f);
            backBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onBack?.Invoke()));

            Render();
            Interactions.PolishButtons(formHost);

            System.Threading.Tasks.Task.Run(async () =>
            {
                TsIndex idx = null;
                try { idx = await ThunderstoreClient.GetIndexAsync(ProfileEngine.GameRoot, false, System.Threading.CancellationToken.None); }
                catch { /* offline: idx stays null */ }
                MainThread.Post(() =>
                {
                    if (content == null) return;   // the view was torn down meanwhile
                    index = idx ?? new TsIndex(new List<TsPackage>());
                    Render();
                });
            });
        }

        // Two tight lines anchored from the row's top edge (name over meta), between the left icon and the right
        // buttons, instead of pinning one to the top and one to the bottom of the row with a gap between.
        private static void PlaceLine(Text t, float topInset, float height)
        {
            var rt = t.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1); rt.pivot = new Vector2(0, 1);
            rt.offsetMin = new Vector2(TextLeft, -(topInset + height));
            rt.offsetMax = new Vector2(-TextRight, -topInset);
        }

        private static string Downloads(long n) =>
            n >= 1_000_000 ? (n / 1_000_000.0).ToString("0.#") + "M" : n >= 1_000 ? (n / 1_000.0).ToString("0.#") + "k" : n.ToString();

        private static string Norm(string s) =>
            s == null ? "" : new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
