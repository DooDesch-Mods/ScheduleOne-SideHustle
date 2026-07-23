using System;
using System.Linq;
using DooDesch.UI;
using S1API.UI;
using SideHustle.Sync;
using UnityEngine;
using UnityEngine.UI;

namespace SideHustle.Menu
{
    /// <summary>
    /// The consent screen a joining client sees before any mod is installed: the full diff (what gets set up,
    /// what already matches, what cannot sync, which own mods pause for the session) plus version warnings.
    /// Scrollable form-host view; the confirm restarts the game into the synced profile.
    /// </summary>
    internal static class SyncConsentView
    {
        internal static void Build(Transform formHost, SyncManifest manifest, SyncDiff diff, bool enforced, bool hasPrefs,
            Action onSyncJoin, Action onPlainJoin, Action onBack)
        {
            const float Pad = 30f;

            var footer = UIFactory.Panel("footer", formHost, Theme.Clear);
            var frt = footer.GetComponent<RectTransform>();
            frt.anchorMin = new Vector2(0, 0); frt.anchorMax = new Vector2(1, 0); frt.pivot = new Vector2(0.5f, 0);
            frt.offsetMin = new Vector2(Pad, 0); frt.offsetMax = new Vector2(-Pad, 56);

            var scrollArea = UIFactory.Panel("scrollArea", formHost, Theme.Clear);
            var srt = scrollArea.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(0, 0); srt.anchorMax = new Vector2(1, 1);
            srt.offsetMin = new Vector2(Pad, 64); srt.offsetMax = new Vector2(-Pad, 0);
            var content = Components.ScrollList(scrollArea.transform, out var scroll, 6f, Theme.ScrimPanel);
            SmoothScroll.Attach(scroll);

            // --- warnings first ---
            string localGame = "";
            try { localGame = Application.version; } catch { /* ignore */ }
            if (!string.IsNullOrEmpty(manifest.GameVersion) && !string.IsNullOrEmpty(localGame)
                && !string.Equals(manifest.GameVersion, localGame, StringComparison.OrdinalIgnoreCase))
                Note(content, $"! Game version differs: host {manifest.GameVersion}, you {localGame}.", warn: true);
            if (enforced)
                Note(content, "This host only keeps synced clients - joining without syncing will bounce you.", warn: true);

            int download = diff.Count(DiffStatus.Download) + diff.Count(DiffStatus.Cached);
            int present = diff.Count(DiffStatus.Present);
            int manual = diff.Count(DiffStatus.Manual);
            int dropped = diff.Count(DiffStatus.Dropped);

            int reuse = diff.Entries.Count(x => x.OwnCopyReuse);

            Components.SectionHeader(content, "What syncing sets up");
            bool allExact = diff.Entries.Where(x => x.Status == DiffStatus.Present).All(x => !x.HashWarn && !x.OwnCopyReuse);
            if (download == 0 && present > 0 && allExact)
                Note(content, $"All {Plural(present, "synced mod")} already match yours byte-for-byte.", Theme.TextPrimary);
            foreach (var e in diff.Entries.Where(x => (x.Status == DiffStatus.Download || x.Status == DiffStatus.Cached) && !x.OwnCopyReuse))
                Note(content, $"+  {Label(e)}   ({(e.Status == DiffStatus.Cached ? "already downloaded" : SourceLabel(e))})"
                              + (e.HashWarn ? "  - replaces your same-version copy (different build)" : ""), Theme.SuccessText);
            if (present > 0 && download > 0 && allExact)
                Note(content, $"=  {Plural(present, "mod")} already match and stay as they are.", Theme.TextMuted);

            if (reuse > 0)
            {
                Components.SectionHeader(content, "Using mods you already have");
                foreach (var e in diff.Entries.Where(x => x.OwnCopyReuse))
                    Note(content, $"=  {Label(e)} - using your own installed copy"
                                  + (e.VersionWarn ? "  (version differs / unverified)" : ""), Theme.TextPrimary);
            }

            if (manual > 0)
            {
                Components.SectionHeader(content, "Only via download link (picked up automatically)");
                foreach (var e in diff.Entries.Where(x => x.Status == DiffStatus.Manual))
                    Note(content, $"~  {Label(e)}", Theme.WarningText);
                Note(content, "The next step opens each link - download in your browser and SideHustle spots the file in your Downloads folder and installs it on its own.", Theme.TextMuted);
            }

            if (dropped > 0)
            {
                Components.SectionHeader(content, "Only on Nexus (search link on the next step)");
                foreach (var e in diff.Entries.Where(x => x.Status == DiffStatus.Dropped))
                    Note(content, $"~  {Label(e)}", Theme.WarningText);
                Note(content, "Not on Thunderstore - the next step gives you a Nexus search for each (downloads are picked up automatically). If you can't find one, the session just runs without it.", Theme.TextMuted);
            }

            if (diff.LocalOnly.Count > 0)
            {
                int n = diff.LocalOnly.Count;
                Components.SectionHeader(content, "Your mods pause for this session");
                Note(content, $"{n} of your mod{(n == 1 ? "" : "s")} pause for this session - they stay installed, only the session profile runs without them.", Theme.TextMuted);
            }

            if (hasPrefs)
            {
                Components.SectionHeader(content, "Host settings");
                Note(content, "The host also applies some mod settings for this session - only inside the session profile; your own settings stay as they are.", Theme.TextMuted);
            }

            Components.SectionHeader(content, "How it works");
            Note(content, "Syncing builds a separate session profile (your Mods folder is never touched), restarts the game and rejoins this lobby on its own.", Theme.TextMuted);

            var (backGO, backBtn, _b) = UIFactory.ButtonWithLabel("Back", "Back", footer.transform, Theme.Button, 140, 40);
            Place(backGO, left: true, xOffset: 0);
            backBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onBack?.Invoke()));

            var (syncGO, syncBtn, _s) = UIFactory.ButtonWithLabel("Sync", "Sync and join (restart)", footer.transform, Theme.Accent, 220, 40);
            Place(syncGO, left: false, xOffset: 0);
            syncBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onSyncJoin?.Invoke()));

            // "Join without syncing" is a real option ONLY when the host does not enforce sync - otherwise it is a
            // guaranteed bounce, so it is not offered. When shown, it sits in the footer as the secondary action.
            if (!enforced)
            {
                var (pjGO, pjBtn, _p) = UIFactory.ButtonWithLabel("plain", "Join without syncing", footer.transform, Theme.Button, 200, 40);
                Place(pjGO, left: false, xOffset: -228);
                pjBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onPlainJoin?.Invoke()));
            }

            Interactions.PolishButtons(formHost);
        }

        private static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

        private static string Label(DiffEntry e) =>
            string.IsNullOrEmpty(e.Mod.Name) ? e.Mod.File : $"{e.Mod.Name} {e.Mod.Version}";

        private static string SourceLabel(DiffEntry e) =>
            GhReleases.IsGitHubSource(e.Mod.Source) ? "GitHub" : "Thunderstore";

        private static void Note(RectTransform content, string text, bool warn) =>
            Note(content, text, warn ? Theme.WarningText : Theme.TextMuted);

        private static void Note(RectTransform content, string text, Color color)
        {
            var row = UIFactory.Panel("note", content, Theme.Clear);
            var rle = row.AddComponent<LayoutElement>();
            rle.minHeight = 30f; rle.preferredHeight = 30f; rle.flexibleWidth = 1;
            var t = UIFactory.Text("text", text, row.transform, Theme.Body, TextAnchor.MiddleLeft);
            t.color = color;
            var rt = t.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(12, 0); rt.offsetMax = new Vector2(-12, 0);
        }

        private static void Place(GameObject go, bool left, float xOffset)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(left ? 0 : 1, 0.5f); rt.anchorMax = new Vector2(left ? 0 : 1, 0.5f);
            rt.pivot = new Vector2(left ? 0 : 1, 0.5f);
            rt.anchoredPosition = new Vector2(xOffset, 0);
        }
    }
}
