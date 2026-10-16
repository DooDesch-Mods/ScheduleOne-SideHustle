using System;
using System.Collections.Generic;
using System.Linq;
using DooDesch.UI;
using S1API.UI;
using SideHustle.Profiles;
using SideHustle.Shared;
using UnityEngine;
using UnityEngine.UI;

namespace SideHustle.Menu
{
    /// <summary>
    /// The scrollable form-host views of the Mod Profiles screens (list, detail, add-installed, updates,
    /// installing). Same scaffold as the host config: a ScrollList body + a fixed footer, so any number of
    /// profiles/mods scrolls inside the panel instead of growing it. Pure view code - navigation, dialogs and
    /// engine calls stay in Hub (HubProfiles).
    /// </summary>
    internal static class ProfilesViews
    {
        private const float RowH = 54f;

        internal static void BuildList(Transform formHost, Action<string> onOpen, Action onSwitchFullSet,
            Action onNew, Action onBack, (string Host, int ModCount)? lastSync = null, Action onCreateFromLastSync = null,
            Action<string, string> onSwitch = null)
        {
            var page = Scaffold.Create(formHost);
            var content = page.Content;
            var footer = page.Footer;

            var doc = ProfileEngine.LoadStore(out bool writable);
            string active = ProfileEngine.ActiveProfileId;

            if (!writable)
                Scaffold.Note(content, "Profiles are read-only: the profile file is unreadable or newer than this Side Hustle.");

            // Quick-create from the last lobby you synced into (its mods are still cached), pinned at the very top.
            if (writable && lastSync.HasValue && onCreateFromLastSync != null)
                Row(content, "From last lobby: " + lastSync.Value.Host,
                    $"Save the {Words.Plural(lastSync.Value.ModCount, "mod", "mods")} you synced there as a profile.",
                    "Create", Theme.Accent, onCreateFromLastSync);

            if (!string.IsNullOrEmpty(active))
                Row(content, "Full mod set", "Switch back to everything in your Mods folder (restarts the game).",
                    "Switch", Theme.Button, onSwitchFullSet);

            if (doc.Profiles.Count == 0)
                Scaffold.Note(content, "No profiles yet.");

            foreach (var p in doc.Profiles)
            {
                ProfileDef def = p;
                bool isActive = def.Id.Equals(active, StringComparison.OrdinalIgnoreCase);
                bool isDefault = def.Id.Equals(doc.Settings.DefaultProfileId, StringComparison.OrdinalIgnoreCase);
                // Count only the mods the player actually added - the always-included essentials (Side Hustle + S1API)
                // are seeded into every profile and would otherwise make a brand-new, empty profile read "2 mods".
                int modCount = (def.Mods ?? new List<ProfileModRef>())
                    .Count(m => !(m.Source == "base" && Essentials.IsEssentialFile(m.File)));
                string sub = Words.Plural(modCount, "mod", "mods")
                             + (isActive ? " - active" : isDefault ? " - default" : "")
                             + (string.IsNullOrEmpty(def.Notes) ? "" : " - " + def.Notes);
                // The active profile is already running, so only the others get a direct Switch.
                Row(content, def.Name, sub, "Open", isActive ? Theme.Accent : Theme.Button, () => onOpen(def.Id),
                    extraLabel: !isActive && onSwitch != null ? "Switch" : null,
                    onExtra: !isActive && onSwitch != null ? () => onSwitch(def.Id, def.Name) : (Action)null);
            }

            Scaffold.FooterButton(footer, "Back", Theme.Button, left: true, onBack);
            if (writable) Scaffold.FooterButton(footer, "New profile", Theme.Accent, left: false, onNew);
            Interactions.PolishButtons(formHost);
        }

        internal static void BuildDetail(Transform formHost, ProfileDef p, bool writable, Func<string, bool> isEssential,
            Action onActivate, Action onAddThunderstore, Action onAddInstalled, Action onCheckUpdates,
            Action<ProfileModRef> onRemoveMod, Action onDelete, Action onBack,
            Action onRename = null, Action onOpenFolder = null, Action onEditDescription = null,
            Func<ProfileModRef, string> annotate = null)
        {
            var page = Scaffold.Create(formHost);
            var content = page.Content;
            var footer = page.Footer;
            bool isActive = p.Id.Equals(ProfileEngine.ActiveProfileId, StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(p.Notes))
                Scaffold.Note(content, p.Notes);

            if (writable)
            {
                Components.SectionHeader(content, "Add mods to this profile");
#if NO_EXTERNAL_FETCH
                // No downloads in this build, so there is no install to offer. Saying why beats a Browse button
                // that lists the whole community index and then fails on the one thing it exists for.
                Scaffold.Note(content, "Thunderstore installs are not part of this build.");
#else
                Row(content, "Thunderstore", "Browse Thunderstore and install, dependencies included.",
                    "Browse", Theme.Button, onAddThunderstore);
#endif
                Row(content, "Pick from installed mods", "Add a mod from your Mods folder.",
                    "Pick", Theme.Button, onAddInstalled);
            }

            Components.SectionHeader(content, "Mods in this profile");
            var essentials = (p.Mods ?? new List<ProfileModRef>())
                .Where(m => m.Source == "base" && isEssential(m.File))
                .Select(m => StripDllName(m.File))
                .ToList();
            if (essentials.Count > 0)
                Scaffold.Note(content, "Always included: " + string.Join(", ", essentials) + ".");

            // Split the removable mods into what the player chose ("manual" - base/local links and directly
            // installed Thunderstore packages) and what only came along as a dependency. The chosen mods sit on top
            // where the player primarily manages them; the dependencies drop to the bottom under their own header,
            // visually recessed so they are not removed by accident.
            var removable = (p.Mods ?? new List<ProfileModRef>())
                .Where(m => !(m.Source == "base" && isEssential(m.File)))
                .ToList();
            var manual = removable.Where(m => !(m.Source == "thunderstore" && m.AsDependency)).ToList();
            var deps = removable.Where(m => m.Source == "thunderstore" && m.AsDependency).ToList();

            void EmitRow(ProfileModRef mref, bool dependency)
            {
                string extra = annotate?.Invoke(mref);
                string title, sub;
                switch (mref.Source)
                {
                    case "thunderstore":
                        title = mref.FullName;
                        // Lead with the dependency hint (the useful part) and fall back to the DLL list only when
                        // there is no hint, so "required by ..." is never pushed past the row's truncation.
                        sub = !string.IsNullOrEmpty(extra)
                            ? $"Thunderstore {mref.Version}  -  {extra}"
                            : $"Thunderstore {mref.Version} - {(mref.Files == null || mref.Files.Count == 0 ? "?" : string.Join(", ", mref.Files))}";
                        break;
                    case "local":
                        title = StripDllName(mref.File);
                        sub = "Local file in this profile.";
                        if (!string.IsNullOrEmpty(extra)) sub += "  -  " + extra;
                        break;
                    default:
                        title = StripDllName(mref.File);
                        sub = "Linked from your Mods folder.";
                        if (!string.IsNullOrEmpty(extra)) sub += "  -  " + extra;
                        break;
                }
                if (writable) Row(content, title, sub, "Remove", Theme.Button, () => onRemoveMod(mref), dependency);
                else Scaffold.Note(content, title + " - " + sub);
            }

            foreach (var m in manual) EmitRow(m, dependency: false);

            if (deps.Count > 0)
            {
                Components.SectionHeader(content, "Dependencies (installed automatically)");
                foreach (var m in deps) EmitRow(m, dependency: true);
            }

            if (removable.Count == 0)
                Scaffold.Note(content, "No extra mods yet.");

            if (writable)
            {
                Components.SectionHeader(content, "Maintain");
                if (onRename != null)
                    Row(content, "Rename profile", "Change the profile's name.",
                        "Rename", Theme.Button, onRename);
                if (onEditDescription != null)
                    Row(content, "Edit description", "Change the note under the profile's name.",
                        "Edit", Theme.Button, onEditDescription);
                if (onOpenFolder != null)
                    Row(content, "Open folder", "Show the profile's folder in your file browser.",
                        "Open", Theme.Button, onOpenFolder);
                Row(content, "Check for updates", "Look for newer Thunderstore versions.",
                    "Check", Theme.Button, onCheckUpdates);
                Row(content, "Delete profile", "Cached downloads and your Mods folder stay as they are.",
                    "Delete", Theme.Danger, onDelete);
            }

            Scaffold.FooterButton(footer, "Back", Theme.Button, left: true, onBack);
            // When this profile is already active, Activate becomes "Restart" so an edit (a removed mod) can actually
            // be applied - SwitchTo rebuilds the profile and relaunches into it. Never a dead disabled button.
            Scaffold.FooterButton(footer, isActive ? "Restart" : "Activate", Theme.Accent, left: false, onActivate);
            Interactions.PolishButtons(formHost);
        }

        internal static void BuildAddBase(Transform formHost, IReadOnlyList<string> candidates,
            Action<string> onAdd, Action onBack)
        {
            var page = Scaffold.Create(formHost);
            var content = page.Content;
            var footer = page.Footer;
            if (candidates.Count == 0)
                Scaffold.Note(content, "Every installed mod is already in this profile.");
            foreach (var file in candidates)
            {
                string f = file;
                Row(content, StripDllName(f), f, "Add", Theme.Accent, () => onAdd(f));
            }
            Scaffold.FooterButton(footer, "Back", Theme.Button, left: true, onBack);
            Interactions.PolishButtons(formHost);
        }

        internal static void BuildUpdatesPending(Transform formHost, Action onBack)
        {
            var page = Scaffold.Create(formHost);
            var content = page.Content;
            var footer = page.Footer;
            Scaffold.Note(content, "Refreshing the Thunderstore index...");
            Scaffold.FooterButton(footer, "Back", Theme.Button, left: true, onBack);
            Interactions.PolishButtons(formHost);
        }

        internal static void BuildUpdateResults(Transform formHost,
            List<(string FullName, string Pinned, string Latest)> updates, string error,
            Action<(string FullName, string Pinned, string Latest)> onUpdate, Action onBack)
        {
            var page = Scaffold.Create(formHost);
            var content = page.Content;
            var footer = page.Footer;
            if (error != null)
                Scaffold.Note(content, "Update check failed: " + error);
            else if (updates == null || updates.Count == 0)
                Scaffold.Note(content, "Everything is up to date.");
            else
                foreach (var u in updates)
                {
                    var upd = u;
                    Row(content, upd.FullName, $"{upd.Pinned} -> {upd.Latest}", "Update", Theme.Accent, () => onUpdate(upd));
                }
            Scaffold.FooterButton(footer, "Back", Theme.Button, left: true, onBack);
            Interactions.PolishButtons(formHost);
        }

        internal static void BuildInstalling(Transform formHost, string label, Action onBack = null)
        {
            var page = Scaffold.Create(formHost);
            var content = page.Content;
            var footer = page.Footer;
            Scaffold.Note(content, label);
            if (onBack != null) Scaffold.FooterButton(footer, "Back", Theme.Button, left: true, onBack);
        }

        /// <summary>A prominent, centred "please wait" status - a large headline over a smaller subtitle - so the
        /// player clearly sees what is happening (e.g. the mod-sync restart) instead of a small grey note.</summary>
        internal static void BuildBigStatus(Transform formHost, string headline, string subtitle)
        {
            var panel = UIFactory.Panel("bigStatus", formHost, Theme.Clear);
            var prt = panel.GetComponent<RectTransform>();
            prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
            prt.offsetMin = new Vector2(Scaffold.Pad, Scaffold.Pad); prt.offsetMax = new Vector2(-Scaffold.Pad, -Scaffold.Pad);

            var head = UIFactory.Text("headline", headline, panel.transform, Theme.H1, TextAnchor.LowerCenter, FontStyle.Bold);
            head.color = Theme.TextPrimary; head.horizontalOverflow = HorizontalWrapMode.Wrap;
            var hrt = head.rectTransform;
            hrt.anchorMin = new Vector2(0, 0.5f); hrt.anchorMax = new Vector2(1, 1f);
            hrt.offsetMin = new Vector2(10, 0); hrt.offsetMax = new Vector2(-10, -20);

            if (!string.IsNullOrEmpty(subtitle))
            {
                var sub = UIFactory.Text("subtitle", subtitle, panel.transform, Theme.H2, TextAnchor.UpperCenter);
                sub.color = Theme.TextMuted; sub.horizontalOverflow = HorizontalWrapMode.Wrap;
                var srt = sub.rectTransform;
                srt.anchorMin = new Vector2(0, 0f); srt.anchorMax = new Vector2(1, 0.5f);
                srt.offsetMin = new Vector2(20, 10); srt.offsetMax = new Vector2(-20, -14);
            }
        }

        // --- scaffold + primitives ---

        private static void Row(RectTransform content, string title, string subtitle, string actionLabel,
            Color actionColor, Action onAction, bool dependency = false, string extraLabel = null, Action onExtra = null)
        {
            // A second button sits left of the main one, so the text lines stop earlier to stay clear of both.
            float textMax = extraLabel != null ? 0.58f : 0.72f;
            // Dependency rows sit below the mods the player actually chose and read as secondary: a recessed panel
            // with a coloured left accent bar and a muted title, so "installed by you" stays visually primary.
            var row = UIFactory.Panel("row_" + title, content, dependency ? Theme.BgPanel : Theme.BgElevated);
            var rle = row.AddComponent<LayoutElement>();
            rle.minHeight = RowH; rle.preferredHeight = RowH; rle.flexibleWidth = 1;

            float textLeft = 12f;
            if (dependency)
            {
                var accent = UIFactory.Panel("depaccent", row.transform, Theme.Info);
                var art = accent.GetComponent<RectTransform>();
                art.anchorMin = new Vector2(0, 0); art.anchorMax = new Vector2(0, 1); art.pivot = new Vector2(0, 0.5f);
                art.offsetMin = new Vector2(0, 8f); art.offsetMax = new Vector2(4f, -8f);
                var aimg = accent.GetComponent<Image>(); if (aimg != null) aimg.raycastTarget = false;
                textLeft = 18f;
            }

            var t = UIFactory.Text("name", title, row.transform, Theme.H3, TextAnchor.UpperLeft, FontStyle.Bold);
            t.color = dependency ? Theme.TextMuted : Theme.TextPrimary;
            PlaceLine(t, topInset: 9f, height: 20f, left: textLeft, maxX: textMax);
            var s = UIFactory.Text("sub", subtitle ?? "", row.transform, Theme.Body, TextAnchor.UpperLeft);
            // Clip within the 72%-wide line instead of overflowing: the annotate suffix ("required by ..., missing ...")
            // makes subtitles long, and Overflow would draw them straight across the Remove button on the right.
            s.color = Theme.TextMuted; s.horizontalOverflow = HorizontalWrapMode.Wrap; s.verticalOverflow = VerticalWrapMode.Truncate;
            PlaceLine(s, topInset: 30f, height: 16f, left: textLeft, maxX: textMax);

            var (btnGO, btn, _) = UIFactory.ButtonWithLabel("action", actionLabel, row.transform, actionColor, 120f, 36f);
            var brt = btnGO.GetComponent<RectTransform>();
            brt.anchorMin = new Vector2(1, 0.5f); brt.anchorMax = new Vector2(1, 0.5f); brt.pivot = new Vector2(1, 0.5f);
            brt.anchoredPosition = new Vector2(-10f, 0f);
            btn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onAction?.Invoke()));

            if (extraLabel != null)
            {
                var (xGO, xBtn, _) = UIFactory.ButtonWithLabel("extra", extraLabel, row.transform, Theme.Accent, 120f, 36f);
                var xrt = xGO.GetComponent<RectTransform>();
                xrt.anchorMin = new Vector2(1, 0.5f); xrt.anchorMax = new Vector2(1, 0.5f); xrt.pivot = new Vector2(1, 0.5f);
                xrt.anchoredPosition = new Vector2(-140f, 0f);
                xBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onExtra?.Invoke()));
            }
        }

        // Stack the title and subtitle as two tight lines anchored from the row's top edge, instead of flinging
        // one to the top and the other to the bottom of the row (which left an ugly gap between them). Each line
        // gets a fixed-height band [rowTop - topInset - height .. rowTop - topInset], left-inset, capped at 72%
        // width (maxX) so the action button on the right stays clear.
        private static void PlaceLine(Text t, float topInset, float height, float left = 12f, float maxX = 0.72f)
        {
            var rt = t.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(maxX, 1); rt.pivot = new Vector2(0, 1);
            rt.offsetMin = new Vector2(left, -(topInset + height));
            rt.offsetMax = new Vector2(0, -topInset);
        }

        private static string StripDllName(string f) =>
            f != null && f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? f.Substring(0, f.Length - 4) : f ?? "?";
    }
}
