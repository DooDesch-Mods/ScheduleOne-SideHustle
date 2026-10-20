using System;
using System.Collections.Generic;
using System.Linq;
using DooDesch.UI;
using S1API.UI;
using SideHustle.Phone;
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
        /// <param name="enforcedNote">What the "no joining as you are" line says. The vanilla default is about the
        /// host's sync gate; the gamemode-install flow overrides it, where the reason is simply that the player does
        /// not have the gamemode yet.</param>
        internal static void Build(Transform formHost, SyncManifest manifest, SyncDiff diff, bool enforced, bool hasPrefs,
            ulong hostSteamId, string hostName, bool hostAcceptsMessages,
            Action onSyncJoin, Action onPlainJoin, Action onBack, string enforcedNote = null)
        {
            // Warm the name -> Nexus page lookups now: the manual checklist is one click away and its links should
            // already point at the exact mod page when it opens.
            SyncManualInstallView.PrefetchLinks(diff);

            var page = Scaffold.Create(formHost);
            var content = page.Content;

            // --- warnings first ---
            string localGame = "";
            try { localGame = Application.version; } catch (Exception e) { Safe.Report("reading the game version", e); }
            if (!string.IsNullOrEmpty(manifest.GameVersion) && !string.IsNullOrEmpty(localGame)
                && !string.Equals(manifest.GameVersion, localGame, StringComparison.OrdinalIgnoreCase))
                Scaffold.Note(content, $"! Game version differs: host {manifest.GameVersion}, you {localGame}.", Theme.WarningText);
            if (enforced)
                Scaffold.Note(content, enforcedNote ?? "The host removes players who are not synced.", Theme.WarningText);

            int download = diff.Count(DiffStatus.Download) + diff.Count(DiffStatus.Cached);
            int present = diff.Count(DiffStatus.Present);
            int manual = diff.Count(DiffStatus.Manual);
            int dropped = diff.Count(DiffStatus.Dropped);

            int reuse = diff.Entries.Count(x => x.OwnCopyReuse);

            if (manual > 0)
            {
                Components.SectionHeader(content, "Only via download link");
                foreach (var e in diff.Entries.Where(x => x.Status == DiffStatus.Manual))
                    Scaffold.Note(content, $"~  {Label(e)}", Theme.WarningText);
                Scaffold.Note(content, "Download these in your browser on the next step.");
            }

            if (dropped > 0)
            {
                Components.SectionHeader(content, "Only on Nexus (link on the next step)");
                foreach (var e in diff.Entries.Where(x => x.Status == DiffStatus.Dropped))
                    Scaffold.Note(content, $"~  {Label(e)}", Theme.WarningText);
                Scaffold.Note(content, "You can join without them.");
            }

            Components.SectionHeader(content, "What syncing sets up");
            bool allExact = diff.Entries.Where(x => x.Status == DiffStatus.Present).All(x => !x.HashWarn && !x.OwnCopyReuse);
            if (download == 0 && present > 0 && allExact)
                Scaffold.Note(content, $"All {Words.Plural(present, "mod", "mods")} already match yours.", Theme.TextPrimary);
            foreach (var e in diff.Entries.Where(x => (x.Status == DiffStatus.Download || x.Status == DiffStatus.Cached) && !x.OwnCopyReuse))
                Scaffold.Note(content, $"+  {Label(e)}   ({(e.Status == DiffStatus.Cached ? "already downloaded" : SourceLabel(e))})"
                              + (e.HashWarn ? "  - replaces your same-version copy (different build)" : ""), Theme.SuccessText);
            if (present > 0 && download > 0 && allExact)
                Scaffold.Note(content, $"=  {Words.Plural(present, "mod", "mods")} already match.");

            if (reuse > 0)
            {
                Components.SectionHeader(content, "Using mods you already have");
                foreach (var e in diff.Entries.Where(x => x.OwnCopyReuse))
                    Scaffold.Note(content, $"=  {Label(e)} - using your own installed copy"
                                  + (e.VersionWarn ? "  (version may differ)" : ""), Theme.TextPrimary);
            }

            if (diff.LocalOnly.Count > 0)
            {
                int n = diff.LocalOnly.Count;
                Components.SectionHeader(content, "Your mods pause for this session");
                Scaffold.Note(content, $"{Words.Plural(n, "mod", "mods")} of yours pause for this session. They stay installed.");
            }

            if (hasPrefs)
            {
                Components.SectionHeader(content, "Host settings");
                Scaffold.Note(content, "The host also applies some mod settings for this session.");
            }

            Components.SectionHeader(content, "How it works");
            Scaffold.Note(content, "Syncing restarts the game and rejoins this lobby.");

            Scaffold.FooterButton(page.Footer, "Back", Theme.Button, left: true, onClick: onBack);

            // The way back to a column the player closed. Without it the X in its header is a one-way door: the
            // lobby card that opened it is two screens behind, and this is the screen where the question occurs to
            // them. Toggles, exactly like the card's button, so the same control both opens and puts it away.
            if (ChatPanel.Possible(hostSteamId, hostAcceptsMessages))
            {
                Scaffold.FooterButton(page.Footer, "Chat", Theme.Button, left: true, onClick: () => ChatPanel.Toggle(hostSteamId, hostName),
                    offsetX: Scaffold.SecondaryWidth + 8f, width: 120f);
            }

            Scaffold.FooterButton(page.Footer, "Sync and join (restart)", Theme.Accent, left: false, onClick: onSyncJoin, width: 220f);

            // "Join without syncing" is a real option ONLY when the host does not enforce sync - otherwise it is a
            // guaranteed bounce, so it is not offered. When shown, it sits in the footer as the secondary action.
            if (!enforced)
            {
                Scaffold.FooterButton(page.Footer, "Join without syncing", Theme.Button, left: false, onClick: onPlainJoin,
                    offsetX: 228f, width: 200f);
            }

            Interactions.PolishButtons(formHost);
        }

        /// <summary>The sync check on the hub page: warnings, then what syncing sets up and what it cannot, then the joins.</summary>
        internal static void BuildPage(PageView pv, string title, SyncManifest manifest, SyncDiff diff, bool enforced, bool hasPrefs,
            ulong hostSteamId, string hostName, bool hostAcceptsMessages, Action onSyncJoin, Action onPlainJoin, Action onBack,
            string backLabel, string enforcedNote = null)
        {
            pv.Frame = "board";
            var chat = ChatPanel.Possible(hostSteamId, hostAcceptsMessages)
                ? pv.Btn("Chat", () => ChatPanel.Toggle(hostSteamId, hostName), "sm", "chat")
                : null;
            pv.Add(pv.Head(title, "Sync check", backLabel, onBack, chat));

            string localGame = "";
            try { localGame = Application.version; } catch (Exception e) { Safe.Report("reading the game version", e); }
            bool versionDiffers = !string.IsNullOrEmpty(manifest.GameVersion) && !string.IsNullOrEmpty(localGame)
                                  && !string.Equals(manifest.GameVersion, localGame, StringComparison.OrdinalIgnoreCase);
            string enforcedLine = enforced ? enforcedNote ?? "The host removes players who are not synced." : null;
            if (versionDiffers)
                pv.Add(pv.Say("need", "alert", $"Game version differs: host {manifest.GameVersion}, you {localGame}.", enforcedLine));
            else if (enforcedLine != null)
                pv.Add(enforcedNote != null
                    ? pv.Say("now", "package", enforcedLine, null)
                    : pv.Say("need", "alert", enforcedLine, null));

            int download = diff.Count(DiffStatus.Download) + diff.Count(DiffStatus.Cached);
            int present = diff.Count(DiffStatus.Present);
            bool allExact = diff.Entries.Where(x => x.Status == DiffStatus.Present).All(x => !x.HashWarn && !x.OwnCopyReuse);
            var sets = diff.Entries.Where(x => (x.Status == DiffStatus.Download || x.Status == DiffStatus.Cached) && !x.OwnCopyReuse).ToList();

            var rows = new List<Json>();
            var setRows = new List<Json>();
            if (download == 0 && present > 0 && allExact)
                setRows.Add(pv.Item($"All {Words.Plural(present, "mod", "mods")} already match yours.", icon: "check", iconTone: "ok"));
            foreach (var e in sets)
                setRows.Add(pv.Item(Label(e), e.HashWarn ? "replaces your same-version copy (different build)" : null,
                    e.Status == DiffStatus.Cached ? "already downloaded" : SourceLabel(e), "ok", icon: "download", iconTone: "ok"));
            if (present > 0 && download > 0 && allExact)
                setRows.Add(pv.Item($"{Words.Plural(present, "mod", "mods")} already match.", icon: "check", iconTone: "ok"));
            if (setRows.Count > 0)
            {
                string count = sets.Count > 0 && present > 0 && allExact ? $"{sets.Count} + {present} match"
                             : sets.Count > 0 ? sets.Count.ToString() : "";
                rows.Add(pv.Group("WHAT SYNCING SETS UP", count, "download", "ok"));
                rows.AddRange(setRows);
            }

            void Section(string label, string icon, string tone, List<DiffEntry> entries, string lastSub)
            {
                if (entries.Count == 0) return;
                rows.Add(pv.Group(label, entries.Count.ToString(), icon, tone));
                for (int i = 0; i < entries.Count; i++)
                    rows.Add(pv.Item(Label(entries[i]), i == entries.Count - 1 ? lastSub : null, icon: icon, iconTone: tone));
            }
            Section("ONLY VIA DOWNLOAD LINK", "hand", "need", diff.Entries.Where(x => x.Status == DiffStatus.Manual).ToList(),
                "Download these in your browser on the next step.");
            Section("ONLY ON NEXUS (LINK ON THE NEXT STEP)", "circle", "dim", diff.Entries.Where(x => x.Status == DiffStatus.Dropped).ToList(),
                "You can join without them.");

            var reuse = diff.Entries.Where(x => x.OwnCopyReuse).ToList();
            if (reuse.Count > 0)
            {
                rows.Add(pv.Group("USING MODS YOU ALREADY HAVE", reuse.Count.ToString(), "restore"));
                foreach (var e in reuse)
                    rows.Add(pv.Item(Label(e), "using your own installed copy" + (e.VersionWarn ? "  (version may differ)" : ""), icon: "restore"));
            }

            if (diff.LocalOnly.Count > 0)
            {
                rows.Add(pv.Group("YOUR MODS PAUSE FOR THIS SESSION", null, "pause", "dim"));
                rows.Add(pv.Item($"{Words.Plural(diff.LocalOnly.Count, "mod", "mods")} of yours pause for this session. They stay installed.",
                    icon: "pause", iconTone: "dim"));
            }

            if (hasPrefs)
            {
                rows.Add(pv.Group("HOST SETTINGS"));
                rows.Add(pv.Quiet("The host also applies some mod settings for this session."));
            }
            pv.Add(pv.Scroll(rows));

            // "Join without syncing" is a real option ONLY when the host does not enforce sync - otherwise it is a
            // guaranteed bounce, so it is not offered.
            pv.Add(pv.Foot("Syncing restarts the game and rejoins this lobby.", null, new[]
            {
                !enforced && onPlainJoin != null ? pv.Btn("Join without syncing", onPlainJoin) : null,
                pv.Btn("Sync and join (restart)", onSyncJoin, "go big"),
            }));
        }

        private static string Label(DiffEntry e) =>
            string.IsNullOrEmpty(e.Mod.Name) ? e.Mod.File : $"{e.Mod.Name} {e.Mod.Version}";

        private static string SourceLabel(DiffEntry e) =>
            GhReleases.IsGitHubSource(e.EffectiveSource) ? "GitHub" : "Thunderstore";
    }
}
