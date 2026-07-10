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
        private const float Pad = 30f;
        private const float RowH = 54f;

        internal static void BuildList(Transform formHost, Action<string> onOpen, Action onSwitchFullSet,
            Action onNew, Action onBack)
        {
            var (content, footer) = Scaffold(formHost);

            var doc = ProfileEngine.LoadStore(out bool writable);
            string active = ProfileEngine.ActiveProfileId;

            if (!writable)
                Note(content, "Profiles are read-only: profiles.json is unreadable or from a newer Side Hustle.");

            if (!string.IsNullOrEmpty(active))
                Row(content, "Full mod set", "Switch back to everything in your Mods folder (restarts the game).",
                    "Switch", Theme.Button, onSwitchFullSet);

            if (doc.Profiles.Count == 0)
                Note(content, "No profiles yet. Create one to build an isolated mod set - your Mods folder stays untouched.");

            foreach (var p in doc.Profiles)
            {
                ProfileDef def = p;
                bool isActive = def.Id.Equals(active, StringComparison.OrdinalIgnoreCase);
                bool isDefault = def.Id.Equals(doc.Settings.DefaultProfileId, StringComparison.OrdinalIgnoreCase);
                string sub = $"{def.Mods.Count} mod(s)"
                             + (isActive ? " - ACTIVE" : isDefault ? " - default" : "")
                             + (string.IsNullOrEmpty(def.Notes) ? "" : " - " + def.Notes);
                Row(content, def.Name, sub, "Open", isActive ? Theme.Accent : Theme.Button, () => onOpen(def.Id));
            }

            FooterButton(footer, "Back", Theme.Button, left: true, onBack);
            if (writable) FooterButton(footer, "New profile", Theme.Accent, left: false, onNew);
            Interactions.PolishButtons(formHost);
        }

        internal static void BuildDetail(Transform formHost, ProfileDef p, bool writable, Func<string, bool> isEssential,
            Action onActivate, Action onAddThunderstore, Action onAddInstalled, Action onCheckUpdates,
            Action<ProfileModRef> onRemoveMod, Action onDelete, Action onBack,
            Action onRename = null, Action onOpenFolder = null, Action onEditDescription = null)
        {
            var (content, footer) = Scaffold(formHost);
            bool isActive = p.Id.Equals(ProfileEngine.ActiveProfileId, StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(p.Notes))
                Note(content, p.Notes);

            if (writable)
            {
                Components.SectionHeader(content, "Add mods to this profile");
                Row(content, "Thunderstore", "Browse the community index and install (dependencies included).",
                    "Browse", Theme.Button, onAddThunderstore);
                Row(content, "Pick from installed mods", "Choose a mod you already have in your Mods folder to add here.",
                    "Pick", Theme.Button, onAddInstalled);
            }

            Components.SectionHeader(content, "Mods in this profile");
            var essentials = (p.Mods ?? new List<ProfileModRef>())
                .Where(m => m.Source == "base" && isEssential(m.File))
                .Select(m => StripDllName(m.File))
                .ToList();
            if (essentials.Count > 0)
                Note(content, "Always included: " + string.Join(", ", essentials) + " (Side Hustle manages this profile).");

            bool any = false;
            foreach (var m in (p.Mods ?? new List<ProfileModRef>()))
            {
                if (m.Source == "base" && isEssential(m.File)) continue;
                any = true;
                ProfileModRef mref = m;
                string title, sub;
                switch (mref.Source)
                {
                    case "thunderstore":
                        title = mref.FullName;
                        sub = $"Thunderstore {mref.Version} - {(mref.Files == null || mref.Files.Count == 0 ? "?" : string.Join(", ", mref.Files))}";
                        break;
                    case "local":
                        title = StripDllName(mref.File);
                        sub = "Local file inside this profile (managed by you).";
                        break;
                    default:
                        title = StripDllName(mref.File);
                        sub = "Linked from your Mods folder.";
                        break;
                }
                if (writable) Row(content, title, sub, "Remove", Theme.Button, () => onRemoveMod(mref));
                else Note(content, title + " - " + sub);
            }
            if (!any)
                Note(content, "No extra mods yet - add some from Thunderstore or your Mods folder.");

            if (writable)
            {
                Components.SectionHeader(content, "Maintain");
                if (onRename != null)
                    Row(content, "Rename profile", "Change this profile's name (its mods and folder stay put).",
                        "Rename", Theme.Button, onRename);
                if (onEditDescription != null)
                    Row(content, "Edit description", "Add or change the note shown under this profile's name.",
                        "Edit", Theme.Button, onEditDescription);
                if (onOpenFolder != null)
                    Row(content, "Open folder", "Reveal this profile's mod folder in your file browser.",
                        "Open", Theme.Button, onOpenFolder);
                Row(content, "Check for updates", "Compare the pinned Thunderstore versions against the index.",
                    "Check", Theme.Button, onCheckUpdates);
                Row(content, "Delete profile", "Cached downloads and your Mods folder stay as they are.",
                    "Delete", Theme.Danger, onDelete);
            }

            FooterButton(footer, "Back", Theme.Button, left: true, onBack);
            var activate = FooterButton(footer, isActive ? "Active" : "Activate", Theme.Accent, left: false, onActivate);
            if (isActive && activate != null) activate.interactable = false;
            Interactions.PolishButtons(formHost);
        }

        internal static void BuildAddBase(Transform formHost, IReadOnlyList<string> candidates,
            Action<string> onAdd, Action onBack)
        {
            var (content, footer) = Scaffold(formHost);
            if (candidates.Count == 0)
                Note(content, "Every installed mod is already in this profile.");
            foreach (var file in candidates)
            {
                string f = file;
                Row(content, StripDllName(f), f, "Add", Theme.Accent, () => onAdd(f));
            }
            FooterButton(footer, "Back", Theme.Button, left: true, onBack);
            Interactions.PolishButtons(formHost);
        }

        internal static void BuildUpdatesPending(Transform formHost, Action onBack)
        {
            var (content, footer) = Scaffold(formHost);
            Note(content, "Refreshing the Thunderstore index...");
            FooterButton(footer, "Back", Theme.Button, left: true, onBack);
            Interactions.PolishButtons(formHost);
        }

        internal static void BuildUpdateResults(Transform formHost,
            List<(string FullName, string Pinned, string Latest)> updates, string error,
            Action<(string FullName, string Pinned, string Latest)> onUpdate, Action onBack)
        {
            var (content, footer) = Scaffold(formHost);
            if (error != null)
                Note(content, "Update check failed: " + error);
            else if (updates == null || updates.Count == 0)
                Note(content, "Everything is up to date.");
            else
                foreach (var u in updates)
                {
                    var upd = u;
                    Row(content, upd.FullName, $"{upd.Pinned} -> {upd.Latest}", "Update", Theme.Accent, () => onUpdate(upd));
                }
            FooterButton(footer, "Back", Theme.Button, left: true, onBack);
            Interactions.PolishButtons(formHost);
        }

        internal static void BuildInstalling(Transform formHost, string label, Action onBack = null)
        {
            var (content, footer) = Scaffold(formHost);
            Note(content, label);
            if (onBack != null) FooterButton(footer, "Back", Theme.Button, left: true, onBack);
        }

        // --- scaffold + primitives ---

        private static (RectTransform Content, Transform Footer) Scaffold(Transform formHost)
        {
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
            return (content, footer.transform);
        }

        private static void Row(RectTransform content, string title, string subtitle, string actionLabel,
            Color actionColor, Action onAction)
        {
            var row = UIFactory.Panel("row_" + title, content, Theme.BgElevated);
            var rle = row.AddComponent<LayoutElement>();
            rle.minHeight = RowH; rle.preferredHeight = RowH; rle.flexibleWidth = 1;

            var t = UIFactory.Text("name", title, row.transform, Theme.H3, TextAnchor.UpperLeft, FontStyle.Bold);
            PlaceLine(t, topInset: 9f, height: 20f);
            var s = UIFactory.Text("sub", subtitle ?? "", row.transform, Theme.Body, TextAnchor.UpperLeft);
            s.color = Theme.TextMuted; s.horizontalOverflow = HorizontalWrapMode.Overflow;
            PlaceLine(s, topInset: 30f, height: 16f);

            var (btnGO, btn, _) = UIFactory.ButtonWithLabel("action", actionLabel, row.transform, actionColor, 120f, 36f);
            var brt = btnGO.GetComponent<RectTransform>();
            brt.anchorMin = new Vector2(1, 0.5f); brt.anchorMax = new Vector2(1, 0.5f); brt.pivot = new Vector2(1, 0.5f);
            brt.anchoredPosition = new Vector2(-10f, 0f);
            btn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onAction?.Invoke()));
        }

        private static void Note(RectTransform content, string text)
        {
            var row = UIFactory.Panel("note", content, Theme.Clear);
            var rle = row.AddComponent<LayoutElement>();
            rle.minHeight = 40f; rle.preferredHeight = 40f; rle.flexibleWidth = 1;
            var t = UIFactory.Text("text", text, row.transform, Theme.Body, TextAnchor.MiddleLeft);
            t.color = Theme.TextMuted;
            var rt = t.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(12, 0); rt.offsetMax = new Vector2(-12, 0);
        }

        private static Button FooterButton(Transform footer, string label, Color color, bool left, Action onClick)
        {
            var (go, btn, _) = UIFactory.ButtonWithLabel(label, label, footer, color, left ? 140f : 200f, 40f);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(left ? 0 : 1, 0.5f); rt.anchorMax = new Vector2(left ? 0 : 1, 0.5f);
            rt.pivot = new Vector2(left ? 0 : 1, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            btn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onClick?.Invoke()));
            return btn;
        }

        // Stack the title and subtitle as two tight lines anchored from the row's top edge, instead of flinging
        // one to the top and the other to the bottom of the row (which left an ugly gap between them). Each line
        // gets a fixed-height band [rowTop - topInset - height .. rowTop - topInset], left-inset, capped at 72%
        // width so the action button on the right stays clear.
        private static void PlaceLine(Text t, float topInset, float height)
        {
            var rt = t.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(0.72f, 1); rt.pivot = new Vector2(0, 1);
            rt.offsetMin = new Vector2(12, -(topInset + height));
            rt.offsetMax = new Vector2(0, -topInset);
        }

        private static string StripDllName(string f) =>
            f != null && f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? f.Substring(0, f.Length - 4) : f ?? "?";
    }
}
