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

        // The same four orderings as thunderstore.io, in its order, with its default (Last updated). Session-sticky.
        private static readonly string[] SortLabels = { "Last updated", "Newest", "Downloads", "Top rated" };
        private static int _sortMode;

        internal static void Build(Transform formHost, Action onBack, Action<string, string> onInstall,
            Func<string, bool> isInstalled = null)
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

            var sortArea = UIFactory.Panel("sort", formHost, Theme.Clear);
            var sortRt = sortArea.GetComponent<RectTransform>();
            sortRt.anchorMin = new Vector2(0, 1); sortRt.anchorMax = new Vector2(1, 1); sortRt.pivot = new Vector2(0.5f, 1);
            sortRt.offsetMin = new Vector2(Pad, -90); sortRt.offsetMax = new Vector2(-Pad, -56);

            var listArea = UIFactory.Panel("list", formHost, Theme.Clear);
            var lrt = listArea.GetComponent<RectTransform>();
            lrt.anchorMin = new Vector2(0, 0); lrt.anchorMax = new Vector2(1, 1);
            lrt.offsetMin = new Vector2(Pad, 64); lrt.offsetMax = new Vector2(-Pad, -96);

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
                var filtered = index.Packages
                    .Where(p => !p.IsDeprecated && p.Latest != null)
                    // Essentials (Side Hustle / S1API) are in every profile already - never offer them for install.
                    .Where(p => !Profiles.Essentials.IsEssentialPackageName(p.FullName))
                    .Where(p => q.Length == 0 || Norm(p.Name).Contains(q) || Norm(p.Owner).Contains(q) || Norm(p.FullName).Contains(q));
                var results = Sorted(filtered).Take(MaxResults).ToList();
#if DEBUG
                Core.Log?.Msg($"[browser] sort={SortLabels[_sortMode]} top3: " + string.Join(" | ",
                    results.Take(3).Select(p => $"{p.FullName} (upd {p.DateUpdated}, dl {p.TotalDownloads}, rt {p.RatingScore})")));
#endif

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

                    // A curated modpack (bundle of dependencies) reads very differently from a single mod - flag it
                    // with a violet pill leading the meta line so the player knows Install pulls in a whole set.
                    float metaLeft = TextLeft;
                    if (p.IsModpack)
                    {
                        const float PillW = 74f;
                        var pill = UIFactory.Panel("modpack", row.transform, Theme.Accent);
                        var pimg = pill.GetComponent<Image>(); if (pimg != null) { pimg.sprite = Theme.RoundedSprite(); pimg.type = Image.Type.Sliced; }
                        var prt = pill.GetComponent<RectTransform>();
                        prt.anchorMin = new Vector2(0, 1); prt.anchorMax = new Vector2(0, 1); prt.pivot = new Vector2(0, 1);
                        prt.sizeDelta = new Vector2(PillW, 18f); prt.anchoredPosition = new Vector2(TextLeft, -33f);
                        var pl = UIFactory.Text("t", "Modpack", pill.transform, Theme.Caption, TextAnchor.MiddleCenter, FontStyle.Bold);
                        pl.color = Color.white; pl.raycastTarget = false;
                        var plrt = pl.rectTransform; plrt.anchorMin = Vector2.zero; plrt.anchorMax = Vector2.one; plrt.offsetMin = Vector2.zero; plrt.offsetMax = Vector2.zero;
                        metaLeft = TextLeft + PillW + 8f;
                    }

                    var meta = UIFactory.Text("meta", $"by {p.Owner} - v{p.Latest.VersionNumber} - {Downloads(p.TotalDownloads)} downloads",
                        row.transform, Theme.Body, TextAnchor.UpperLeft);
                    meta.color = Theme.TextMuted; meta.horizontalOverflow = HorizontalWrapMode.Overflow;
                    var metaRt = meta.rectTransform;
                    metaRt.anchorMin = new Vector2(0, 1); metaRt.anchorMax = new Vector2(1, 1); metaRt.pivot = new Vector2(0, 1);
                    metaRt.offsetMin = new Vector2(metaLeft, -48f); metaRt.offsetMax = new Vector2(-TextRight, -32f);

                    // Already in this profile: a green, non-actionable "Installed" chip instead of the Install button.
                    bool installed = isInstalled != null && isInstalled(p.FullName);
                    var (btnGO, btn, btnLbl) = UIFactory.ButtonWithLabel(installed ? "installed" : "install",
                        installed ? "Installed" : "Install", row.transform, installed ? Theme.Success : Theme.Accent, InstallW, 36f);
                    var brt = btnGO.GetComponent<RectTransform>();
                    brt.anchorMin = new Vector2(1, 0.5f); brt.anchorMax = new Vector2(1, 0.5f); brt.pivot = new Vector2(1, 0.5f);
                    brt.anchoredPosition = new Vector2(-10f, 0f);
                    if (installed)
                    {
                        btn.interactable = false;
                        if (btnLbl != null) btnLbl.color = Theme.SuccessText;
                    }
                    else
                    {
                        btn.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
                            onInstall?.Invoke(p.FullName, p.Latest.VersionNumber)));
                    }

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

            var seg = Components.Segmented(sortArea.transform, SortLabels, _sortMode, i => { _sortMode = i; Render(); }, out _);
            Components.FillSlot(seg);

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
        // ISO-8601 timestamps compare correctly as ordinal strings, so the date sorts stay allocation-free.
        private static IEnumerable<TsPackage> Sorted(IEnumerable<TsPackage> src)
        {
            switch (_sortMode)
            {
                case 1: return src.OrderByDescending(p => p.DateCreated, StringComparer.Ordinal);
                case 2: return src.OrderByDescending(p => p.TotalDownloads);
                case 3: return src.OrderByDescending(p => p.RatingScore).ThenByDescending(p => p.TotalDownloads);
                default: return src.OrderByDescending(p => p.DateUpdated, StringComparer.Ordinal);
            }
        }

#if DEBUG
        /// <summary>Dev.SelfTest only: pick a sort mode before the browser opens (the rig cannot click).</summary>
        internal static void SetSortForTest(int mode) => _sortMode = Math.Max(0, Math.Min(SortLabels.Length - 1, mode));
#endif

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
