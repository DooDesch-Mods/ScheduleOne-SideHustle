using System;
using System.Collections.Generic;
using System.Linq;
using DooDesch.UI;
using SideHustle.Profiles;
using SideHustle.Shared;
using UnityEngine;

namespace SideHustle.Menu
{
    /// <summary>
    /// The "Mod Profiles" screens (partial: shares the clone/form-host/_back machinery). Profiles get their own
    /// main-menu entry (they are not a gamemode, so they never appear in the gamemode list) and render as
    /// SCROLLABLE form-host views like the host config - the native row container is not built to scroll, so
    /// row-based screens would grow past the panel with a handful of mods. Side Hustle and its dependencies are
    /// part of every profile by construction and are neither listed as removable nor offered in the add picker.
    /// </summary>
    internal static partial class Hub
    {
        /// <summary>True while the profiles list was opened from the hub page's Profiles tab: its Back returns to
        /// the page instead of closing the screen into the vanilla menu.</summary>
        private static bool _profilesFromHub;

        /// <summary>Open the profiles screen (called by the injected "Mod Profiles" main-menu button).</summary>
        internal static void OpenProfilesScreen()
        {
            _profilesFromHub = false;
            OpenProfilesCore();
        }

        /// <summary>Open the profiles screen from the hub page's Profiles tab.</summary>
        internal static void OpenProfilesFromHub()
        {
            _profilesFromHub = true;
            OpenProfilesCore();
        }

        private static void OpenProfilesCore()
        {
            EnsureInit();
            EnsureClone();
            if (_cloneScreen == null) { Core.Log?.Warning("[hub] profiles screen unavailable."); return; }
            ShowProfilesList();
            if (!_cloneScreen.IsOpen) _cloneScreen.Open();
        }

        private static void ProfilesBack()
        {
            if (_profilesFromHub)
            {
                _profilesFromHub = false;
                Core.Log?.Msg("[hub] profiles: back to the hub page.");
                ShowGamemodeList();
                return;
            }
            _cloneScreen?.Close();
        }

#if DEBUG
        /// <summary>Dev.SelfTest only: same as the main-menu button (the MCP dev loop cannot click).</summary>
        internal static void OpenProfilesForTest() => OpenProfilesScreen();

        /// <summary>Dev.SelfTest only: open the FIRST profile's detail view for a screenshot.</summary>
        internal static void OpenFirstProfileDetailForTest()
        {
            OpenProfilesCore();
            var doc = ProfileEngine.LoadStore(out _);
            if (doc.Profiles.Count > 0) ShowProfileDetail(doc.Profiles[0].Id);
        }

        private static string FirstOrDemoProfileId()
        {
            var doc = ProfileEngine.LoadStore(out _);
            if (doc.Profiles.Count > 0) return doc.Profiles[0].Id;
            var def = ProfileEngine.CreateProfile("Demo");
            if (def != null) SeedEssentials(def.Id);
            return def?.Id;
        }

        /// <summary>Dev.SelfTest only: open the Thunderstore package browser (for a screenshot).</summary>
        internal static void OpenBrowserForTest()
        {
            OpenProfilesCore();
            string id = FirstOrDemoProfileId();
            if (id != null) ShowPackageBrowser(id);
        }

        /// <summary>Dev.SelfTest only: open the update-check results (for a screenshot).</summary>
        internal static void OpenUpdatesForTest()
        {
            OpenProfilesCore();
            string id = FirstOrDemoProfileId();
            if (id != null) RunUpdateCheck(id);
        }

        /// <summary>Dev.SelfTest only: open the "add an installed mod" picker (for a screenshot).</summary>
        internal static void OpenAddBaseForTest()
        {
            OpenProfilesCore();
            string id = FirstOrDemoProfileId();
            if (id != null) ShowAddBaseMod(id);
        }

        /// <summary>Dev.SelfTest only: drive the REAL UI install flow (browser Install click equivalent) for the
        /// latest version of a package into the first profile - the end-to-end the MCP loop cannot click.</summary>
        internal static void InstallForTest(string fullName)
        {
            OpenProfilesCore();
            var doc = ProfileEngine.LoadStore(out _);
            if (doc.Profiles.Count == 0) { Core.Log?.Error("[selftest] install: no profile."); return; }
            string id = doc.Profiles[0].Id;
            System.Threading.Tasks.Task.Run(async () =>
            {
                var idx = await ThunderstoreClient.GetIndexAsync(ProfileEngine.GameRoot, false, System.Threading.CancellationToken.None);
                string v = idx?.Find(fullName)?.Latest?.VersionNumber;
                MainThread.Post(() =>
                {
                    if (v == null) { Core.Log?.Error($"[selftest] install: '{fullName}' not in the index."); return; }
                    InstallIntoProfile(id, fullName, v);
                });
            });
        }

        /// <summary>Dev.SelfTest only: open the dependency-aware remove dialog for a thunderstore ref (screenshot).</summary>
        internal static void OpenRemoveDialogForTest(string fullName)
        {
            OpenProfilesCore();
            var doc = ProfileEngine.LoadStore(out _);
            if (doc.Profiles.Count == 0) { Core.Log?.Error("[selftest] removedlg: no profile."); return; }
            var p = doc.Profiles[0];
            ShowProfileDetail(p.Id);
            var mref = p.Mods.FirstOrDefault(m => m.Source == "thunderstore" && string.Equals(m.FullName, fullName, StringComparison.OrdinalIgnoreCase));
            if (mref == null) { Core.Log?.Error($"[selftest] removedlg: '{fullName}' is not in profile '{p.Name}'."); return; }
            ConfirmRemoveMod(p.Id, mref);
        }

        /// <summary>Dev.SelfTest only: headless dependency-aware CASCADE removal (no dialogs) through the same
        /// engine path the dialog uses, then log the resulting orphan set.</summary>
        internal static void RemoveForTest(string fullName)
        {
            var doc = ProfileEngine.LoadStore(out _);
            if (doc.Profiles.Count == 0) { Core.Log?.Error("[selftest] remove: no profile."); return; }
            var p = doc.Profiles[0];
            var mref = p.Mods.FirstOrDefault(m => m.Source == "thunderstore" && string.Equals(m.FullName, fullName, StringComparison.OrdinalIgnoreCase));
            if (mref == null) { Core.Log?.Error($"[selftest] remove: '{fullName}' is not in profile '{p.Name}'."); return; }

            var dependants = BuildGraph(p).DependantsOf(mref);
            var all = new List<ProfileModRef>(dependants) { mref };
            bool ok = ProfileEngine.RemoveMods(p.Id, all);
            Core.Log?.Msg($"[selftest] remove: ok={ok} target={fullName} dependants={dependants.Count} ({string.Join(", ", dependants.Select(RefDisplayName))})");

            var doc2 = ProfileEngine.LoadStore(out _);
            var p2 = doc2.Profiles.FirstOrDefault(x => x.Id == p.Id);
            var orphans = p2 != null ? BuildGraph(p2).Orphans() : new List<ProfileModRef>();
            Core.Log?.Msg("[selftest] remove: orphans now: " + (orphans.Count == 0 ? "(none)" : string.Join(", ", orphans.Select(RefDisplayName))));
        }

        /// <summary>Dev.SelfTest only: drive the REAL "Remove all" cascade (ExecuteRemoval -> orphan offer) so the
        /// orphan-cleanup ChoiceDialog can be screenshotted.</summary>
        internal static void RemoveAllForTest(string fullName)
        {
            OpenProfilesCore();
            var doc = ProfileEngine.LoadStore(out _);
            if (doc.Profiles.Count == 0) { Core.Log?.Error("[selftest] removeall: no profile."); return; }
            var p = doc.Profiles[0];
            ShowProfileDetail(p.Id);
            var mref = p.Mods.FirstOrDefault(m => m.Source == "thunderstore" && string.Equals(m.FullName, fullName, StringComparison.OrdinalIgnoreCase));
            if (mref == null) { Core.Log?.Error($"[selftest] removeall: '{fullName}' is not in profile '{p.Name}'."); return; }
            var dependants = BuildGraph(p).DependantsOf(mref);
            var all = new List<ProfileModRef>(dependants) { mref };
            ExecuteRemoval(p.Id, all, RefDisplayName(mref) + (dependants.Count > 0 ? $" and {Words.Plural(dependants.Count, "dependent mod", "dependent mods")}" : ""));
        }

        /// <summary>Dev.SelfTest only: inject a fake wrong-runtime exclusion set into the first profile and show
        /// the REAL one-time notice dialog - a click-free screenshot path that needs no relaunch.</summary>
        internal static void ShowRuntimeNoticeForTest()
        {
            OpenProfilesCore();
            string id = FirstOrDemoProfileId();
            if (id == null) { Core.Log?.Error("[selftest] runtimedialog: no profile."); return; }
            ProfileEngine.InjectRuntimeExclusionsForTest(id, new List<string> { "FakeMod_Mono.dll", "OtherMod_Mono.dll" });
            ShowWrongRuntimeNotice(id);
        }

        /// <summary>Dev.SelfTest only: start a real install, then cancel it shortly after (the cancel path).</summary>
        internal static void InstallCancelForTest(string fullName)
        {
            InstallForTest(fullName);
            MelonLoader.MelonCoroutines.Start(CancelInstallSoon());
        }

        private static System.Collections.IEnumerator CancelInstallSoon()
        {
            // Wait until the install actually started (index fetch may take a moment), then cancel mid-download.
            float waited = 0f;
            while (_installCts == null && waited < 30f) { yield return new UnityEngine.WaitForSeconds(0.25f); waited += 0.25f; }
            yield return new UnityEngine.WaitForSeconds(1f);
            try { _installCts?.Cancel(); Core.Log?.Msg("[selftest] installcancel: cancel requested."); }
            catch (Exception e) { Core.Log?.Warning("[selftest] installcancel: cancel threw: " + e.Message); }
        }
#endif

        // Side Hustle and the mods it stands on are seeded into every profile and must survive every edit -
        // removing them would strip the manager (or its API layer) out of the profile it manages.
        private static bool IsEssentialFile(string file) => Profiles.Essentials.IsEssentialFile(file);

        private static void ShowProfilesList()
        {
            if (_clone == null) return;
            _mpDesc = null;
            // Right-click at the profiles root returns to the hub page when it came from there; otherwise TickInput's
            // default closes back to the main menu.
            _back = _profilesFromHub ? ProfilesBack : (Action)null;
            if (ShowProfilesPage(null)) return;

            ClearFormHost();
            SetTmp(_clone.transform, "Title", "Mod profiles");
            var host = CreateFormHost("SH_ProfilesList");
            var recent = Sync.LastSync.Load();
            ProfilesViews.BuildList(host,
                onOpen: ShowProfileDetail,
                onSwitchFullSet: () => ConfirmSwitch("", "your full mod set"),
                onNew: PromptNewProfile,
                onBack: ProfilesBack,
                lastSync: recent != null ? (string.IsNullOrWhiteSpace(recent.Host) ? "a lobby" : recent.Host, recent.ModCount) : ((string, int)?)null,
                onCreateFromLastSync: CreateFromLastSync,
                onSwitch: ConfirmSwitch);
        }

        private static void PromptNewProfile()
        {
            AskText("New profile", "Name the new mod profile.", "profile name", "", "Create", name =>
            {
                if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
                var def = ProfileEngine.CreateProfile(name.Trim());
                if (def == null) return "Couldn't create the profile. The profile file isn't writable.";
                SeedEssentials(def.Id);
                ShowProfileDetail(def.Id);
                return null;
            });
        }

        // Turn the last vanilla lobby the player synced into a permanent named profile (its Thunderstore mods are
        // still in the cache from the sync). One tap: create, seed essentials, pin the host's ts: mods, open it.
        private static void CreateFromLastSync()
        {
            var rec = Sync.LastSync.Load();
            if (rec == null) { ShowToast("No recent lobby to create a profile from.", Severity.Info); return; }
            var manifest = Sync.SyncManifest.Parse(rec.Manifest);
            if (manifest == null) { ShowToast("The saved lobby data was unreadable.", Severity.Warning); return; }

            string name = string.IsNullOrWhiteSpace(rec.Host) ? "Synced lobby" : rec.Host.Trim() + "'s mods";
            var def = ProfileEngine.CreateProfile(name);
            if (def == null) { ShowToast("Couldn't create the profile. The profile file isn't writable.", Severity.Warning); return; }
            SeedEssentials(def.Id);

            var pins = new List<(string, string)>();
            foreach (var m in manifest.Mods)
            {
                if (m.Source == null || !m.Source.StartsWith("ts:", StringComparison.Ordinal)) continue;
                if (TsIndex.SplitDependency(m.Source.Substring(3), out var full, out var ver)) pins.Add((full, ver));
            }
            ProfileEngine.PinThunderstoreMods(def.Id, pins);
            Sync.LastSync.Clear();   // one-shot: the quick-create row disappears and cannot spawn duplicate profiles
            ShowToast($"Created '{name}' from the last lobby.", Severity.Success);
            ShowProfileDetail(def.Id);
        }

        private static void PromptRenameProfile(string profileId, string currentName)
        {
            AskText("Rename profile", "Give this profile a new name.", "profile name", currentName, "Rename", name =>
            {
                if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
                if (!ProfileEngine.RenameProfile(profileId, name.Trim())) return "Couldn't rename the profile. The profile file isn't writable.";
                ShowToast("Profile renamed.", Severity.Success);
                ShowProfileDetail(profileId);
                return null;
            });
        }

        private static void PromptEditDescription(string profileId, string currentNotes)
        {
            AskText("Edit description", "A short note about this profile. Empty clears it.",
                "e.g. lightweight co-op set", currentNotes, "Save", notes =>
            {
                if (!ProfileEngine.SetDescription(profileId, notes)) return "Couldn't save the description. The profile file isn't writable.";
                ShowToast("Description saved.", Severity.Success);
                ShowProfileDetail(profileId);
                return null;
            });
        }

        // Reveal a profile's mod folder in the OS file browser. Opening a folder is inert, so no confirm - and it
        // is guarded so a missing folder (a profile that was never built/activated) just logs instead of throwing.
        private static void OpenProfileFolder(string profileId)
        {
            try
            {
                string dir = ProfileEngine.ProfileFolder(profileId);
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
                {
                    Core.Log?.Warning("[profiles] no folder yet for '" + profileId + "' - activate the profile once to build it.");
                    ShowToast("This profile has no folder yet. Switch to it once to build it.", Severity.Info);
                    return;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[profiles] could not open the profile folder: " + e.Message);
                ShowToast("Couldn't open the profile folder.", Severity.Warning);
            }
        }

        // A profile without Side Hustle would boot without this menu (and without the switch-back entry) - seed
        // every new profile with the hub + its API layer so the player can always manage/switch in-profile.
        private static void SeedEssentials(string profileId)
        {
            try
            {
                var files = Mods.ModInventory.Loaded()
                    .Where(m => IsEssentialFile(m.File))
                    .Select(m => m.File)
                    .ToList();
                ProfileEngine.SeedBaseMods(profileId, files);
            }
            catch (Exception e) { Core.Log?.Warning("[profiles] essentials seed failed: " + e.Message); }
        }

        private static void ShowProfileDetail(string profileId)
        {
            if (_clone == null) return;
            _mpDesc = null;
            _back = ShowProfilesList;
            if (ShowProfilesPage(profileId)) return;

            var doc = ProfileEngine.LoadStore(out bool writable);
            var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
            if (p == null) { ShowProfilesList(); return; }

            ClearFormHost();
            SetTmp(_clone.transform, "Title", p.Name);
            var host = CreateFormHost("SH_ProfileDetail");
            var graph = BuildGraph(p);
            var runtimeNotes = BuildRuntimeNotes(p);
            ProfilesViews.BuildDetail(host, p, writable, IsEssentialFile,
                onActivate: () => ConfirmSwitch(p.Id, p.Name),
                onAddThunderstore: () => ShowPackageBrowser(p.Id),
                onAddInstalled: () => ShowAddBaseMod(p.Id),
                onCheckUpdates: () => RunUpdateCheck(p.Id),
                onRemoveMod: mref => ConfirmRemoveMod(p.Id, mref),
                onDelete: () => ConfirmDeleteProfile(p.Id, p.Name),
                onBack: ShowProfilesList,
                onRename: writable ? () => PromptRenameProfile(p.Id, p.Name) : (Action)null,
                onOpenFolder: () => OpenProfileFolder(p.Id),
                onEditDescription: writable ? () => PromptEditDescription(p.Id, p.Notes) : (Action)null,
                annotate: r => Annotate(graph, runtimeNotes, r));
        }

        private static void ShowAddBaseMod(string profileId)
        {
            if (_clone == null) return;
            _back = () => ShowProfileDetail(profileId);

            var doc = ProfileEngine.LoadStore(out _);
            var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
            if (p == null) { ShowProfilesList(); return; }

            var inProfile = new HashSet<string>(p.Mods.Where(m => m.Source == "base").Select(m => m.File ?? ""), StringComparer.OrdinalIgnoreCase);
            var candidates = Mods.ModInventory.AvailableFiles()
                .Where(f => !inProfile.Contains(f) && !IsEssentialFile(f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // On the page the list stays open, so several mods go in one after another; the row says it is added.
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> onPageAdd = f =>
            {
                if (added.Contains(f)) return;
                if (AddBaseMod(profileId, f))
                {
                    added.Add(f);
                    Core.Log?.Msg($"[profiles] added {f} to '{profileId}', the list stays open.");
                    _hubSinceState = 999f;
                }
                else ShowToast("Couldn't add " + f + ". The profile file isn't writable.", Severity.Warning);
            };
            string profileName = p.Name;
            if (ShowOnPage("profiles.add", "profiles", pv => BuildAddBasePage(pv, profileId, profileName, candidates, added, onPageAdd))) return;

            // The classic screen cannot redraw one row, so it goes back to the profile as before.
            Action<string> onAdd = f =>
            {
                if (AddBaseMod(profileId, f)) ShowToast("Added " + f + ".", Severity.Success);
                else ShowToast("Couldn't add " + f + ". The profile file isn't writable.", Severity.Warning);
                ShowProfileDetail(profileId);
            };

            ClearFormHost();
            SetTmp(_clone.transform, "Title", "Add an installed mod");
            var host = CreateFormHost("SH_AddBaseMod");
            ProfilesViews.BuildAddBase(host, candidates, onAdd, onBack: () => ShowProfileDetail(profileId));
        }

        private static bool AddBaseMod(string profileId, string file) => ProfileEngine.AddBaseMod(profileId, file);

        // Removal is dependency-aware (the r2modman/Gale pattern): a mod nothing depends on gets the plain confirm;
        // a mod with dependants gets a choice dialog listing everything that would break, with "remove all" as the
        // recommended path. Never hard-blocked, never a silent cascade. After any removal, dependencies that were
        // only ever auto-installed and are no longer needed are offered for cleanup (apt-autoremove semantics).
        private static void ConfirmRemoveMod(string profileId, ProfileModRef mref)
        {
            var doc = ProfileEngine.LoadStore(out _);
            var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
            if (p == null) { ShowProfilesList(); return; }

            string what = RefDisplayName(mref);
            var dependants = BuildGraph(p).DependantsOf(mref);

            if (dependants.Count == 0)
            {
                AskConfirm("Remove from profile",
                    $"Remove {what} from this profile? The files themselves are not deleted.", "Remove",
                    () => ExecuteRemoval(profileId, new List<ProfileModRef> { mref }, what), danger: true);
                return;
            }

            var items = dependants.Select(RefDisplayName).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            string message = (dependants.Count == 1 ? "1 mod in this profile depends" : dependants.Count + " mods in this profile depend")
                             + $" on {what} and may break without it.";
            AskChoice("Other mods need " + what, message, items,
                ("Remove all (recommended)", false, true, () =>
                {
                    var all = new List<ProfileModRef>(dependants) { mref };
                    ExecuteRemoval(profileId, all, $"{what} and {Words.Plural(dependants.Count, "dependent mod", "dependent mods")}");
                }),
                ($"Remove only {what}", false, false, () =>
                    ExecuteRemoval(profileId, new List<ProfileModRef> { mref }, what)));
        }

        // Remove, refresh the detail underneath, then (with the up-to-date store) offer orphan cleanup on top.
        private static void ExecuteRemoval(string profileId, List<ProfileModRef> refs, string what)
        {
            if (!ProfileEngine.RemoveMods(profileId, refs))
            {
                ShowToast("Nothing was removed - the profile may be read-only.", Severity.Warning);
                ShowProfileDetail(profileId);
                return;
            }
            RebuildInBackground(profileId);
            ShowToast("Removed " + what + ".", Severity.Success);
            ShowProfileDetail(profileId);
            OfferOrphanCleanup(profileId);
        }

        private static void OfferOrphanCleanup(string profileId)
        {
            var doc = ProfileEngine.LoadStore(out bool writable);
            if (!writable) return;
            var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
            if (p == null) return;
            var orphans = BuildGraph(p).Orphans();
            if (orphans.Count == 0) return;

            var items = orphans.Select(RefDisplayName).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            string message = (orphans.Count == 1 ? "1 dependency was installed automatically and is" : orphans.Count + " dependencies were installed automatically and are")
                             + " no longer needed by any mod in this profile.";
            AskChoice("Unused dependencies", message, items,
                ("Remove them (recommended)", true, false, () =>
                {
                    if (ProfileEngine.RemoveMods(profileId, orphans))
                    {
                        RebuildInBackground(profileId);
                        ShowToast("Removed " + (orphans.Count == 1 ? "1 unused dependency." : orphans.Count + " unused dependencies."), Severity.Success);
                    }
                    ShowProfileDetail(profileId);
                }),
                ("Keep them", false, false, () =>
                {
                    // Kept dependencies count as deliberately chosen from now on - they are never offered again.
                    ProfileEngine.PromoteToManual(profileId, orphans);
                    ShowProfileDetail(profileId);
                }));
            // Cancel = decide later; the flags stay, so the offer returns after the next removal.
        }

        // Keep the profile's on-disk Mods dir truthful after an edit (activation would rebuild anyway via SwitchTo,
        // but "Open folder" should not show freshly removed mods). Plain IO, safe on a worker.
        private static void RebuildInBackground(string profileId)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try { ProfileEngine.BuildProfile(profileId); }
                catch (Exception e) { Core.Log?.Warning("[profiles] rebuild after edit failed: " + e.Message); }
            });
        }

        private static DependencyGraph BuildGraph(ProfileDef p) =>
            DependencyGraph.Build(p, ThunderstoreClient.GetCachedIndexOrNull(ProfileEngine.GameRoot), ModMatcher.ConfirmedFullName);

        private static string RefDisplayName(ProfileModRef r)
        {
            if (r == null) return "?";
            if (r.Source == "thunderstore") return r.FullName ?? "?";
            string f = r.File ?? "?";
            return f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? f.Substring(0, f.Length - 4) : f;
        }

        // Per-ref runtime hints, derived from the SAME resolve the engine builds with (no duplicated logic):
        // a ref whose Mods were dropped entirely is disabled; one that only lost its Mono flavor is routine.
        private static Dictionary<string, string> BuildRuntimeNotes(ProfileDef p)
        {
            var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                ProfileEngine.ResolveInputs(p, out _, out var excluded);
                foreach (var group in excluded.GroupBy(e => e.Source == "thunderstore" || e.Source == "plugin"
                             ? "thunderstore|" + (e.PackageFullName ?? "")
                             : e.Source + "|" + e.FileName, StringComparer.OrdinalIgnoreCase))
                {
                    bool wholeRef = group.Any(e => !e.HasSurvivingSibling);
                    notes[group.Key] = wholeRef ? "Other game version - disabled" : "Other game version skipped";
                }
            }
            catch (Exception e) { Safe.Report("reading the per-mod runtime notes", e); }
            return notes;
        }

        private static string RuntimeNoteKey(ProfileModRef r) =>
            r.Source == "thunderstore" ? "thunderstore|" + (r.FullName ?? "") : r.Source + "|" + (r.File ?? "");

        // The muted per-row hint in the detail list: the runtime note FIRST (truncation must never hide that a
        // mod is disabled), then who needs this mod, then whether the profile is missing a dependency it needs.
        // ("Installed as a dependency" is conveyed by the Dependencies section + styling, not repeated here.)
        private static string Annotate(DependencyGraph graph, Dictionary<string, string> runtimeNotes, ProfileModRef r)
        {
            try
            {
                var parts = new List<string>();
                if (runtimeNotes != null && runtimeNotes.TryGetValue(RuntimeNoteKey(r), out var note))
                    parts.Add(note);
                var deps = graph.DirectDependantsOf(r);
                if (deps.Count > 0)
                {
                    var names = deps.Select(RefDisplayName).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).Take(2).ToList();
                    parts.Add("required by " + string.Join(", ", names) + (deps.Count > 2 ? $" (+{deps.Count - 2})" : ""));
                }
                var missing = graph.MissingDepsOf(r);
                if (missing.Count > 0)
                    parts.Add("missing: " + string.Join(", ", missing.Take(2)) + (missing.Count > 2 ? $" (+{missing.Count - 2})" : ""));
                return parts.Count > 0 ? string.Join(" - ", parts) : null;
            }
            catch (Exception e) { Safe.Report("annotating a profile row", e); return null; }
        }

        /// <summary>Named-profile session, menu loaded: if this profile's build dropped wrong-runtime mods, show
        /// the one-time dialog (first start with this exact set) or a small warning toast (repeat starts). Runs on
        /// the plain menu canvas - the hub screen does not need to be open.</summary>
        internal static void ShowWrongRuntimeNotice(string profileId)
        {
            try
            {
                var doc = ProfileEngine.LoadStore(out bool writable);
                var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
                var list = p?.Build?.ExcludedWrongRuntime;
                if (list == null || list.Count == 0) return;

                EnsureInit();
                EnsureClone();
                var root = DialogRoot();
                if (root == null)
                {
                    Core.Log?.Warning("[profiles] runtime notice: no canvas available; logged only: " + string.Join(", ", list));
                    return;
                }

                string key = ProfileEngine.RuntimeNoticeKeyFor(list);
                if (writable && p.RuntimeNoticeKey != key)
                {
                    // Mark FIRST so "once" holds no matter how the dialog is dismissed (Cancel has no callback).
                    ProfileEngine.MarkRuntimeNoticeShown(profileId, key);
                    var items = list.Select(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? f.Substring(0, f.Length - 4) : f)
                                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
                    Components.ChoiceDialog(root, "Some mods were disabled",
                        (list.Count == 1 ? "1 mod in this profile is" : list.Count + " mods in this profile are")
                        + " made for another game version and cannot load. They were left out of this profile.",
                        items,
                        ("Got it", Theme.Accent, () => { }));
                }
                else
                {
                    ShowToast(list.Count == 1
                        ? "1 mod stays disabled: other game version."
                        : list.Count + " mods stay disabled: other game version.", Severity.Warning);
                }
            }
            catch (Exception e) { Core.Log?.Warning("[profiles] runtime notice failed: " + e.Message); }
        }

        /// <summary>A toast from outside the hub (the continue prompt, the settings form).</summary>
        internal static void Notify(string message, Severity sev) => ShowToast(message, sev);

        private static void ShowToast(string message, Severity sev)
        {
            if (PageToast(message, sev)) return;
            try
            {
                // The hub's own canvas while it exists, the overlay canvas otherwise (the screen can close under a toast).
                Toast.Init(DialogRoot() ?? DialogRootStatic());
                Toast.Show(message, sev);
            }
            catch (Exception e) { Core.Log?.Warning("[hub] could not show a toast: " + e.Message + " - " + message); }
        }

        private static void ConfirmDeleteProfile(string profileId, string name)
        {
            AskConfirm("Delete profile", $"Delete '{name}'? Your Mods folder stays as it is.", "Delete", () =>
            {
                if (ProfileEngine.DeleteProfile(profileId)) ShowToast($"Deleted '{name}'.", Severity.Success);
                else ShowToast("Couldn't delete the profile. The profile file isn't writable.", Severity.Warning);
                ShowProfilesList();
            }, danger: true);
        }

        private static void ConfirmSwitch(string profileId, string label)
        {
            AskConfirm("Restart into " + label,
                "The game restarts with this profile's mods.",
                "Restart now", () => ProfileEngine.SwitchTo(profileId));
        }

        private static void RunUpdateCheck(string profileId)
        {
            if (_clone == null) return;
            _back = () => ShowProfileDetail(profileId);

            var page = new UpdatesPage { ProfileId = profileId, Name = ProfileName(profileId) };
            bool onPage = ShowOnPage("profiles.updates", "profiles", pv => BuildUpdatesPage(pv, page));
            if (!onPage)
            {
                ClearFormHost();
                SetTmp(_clone.transform, "Title", "Updates");
                var host = CreateFormHost("SH_Updates");
                ProfilesViews.BuildUpdatesPending(host, () => ShowProfileDetail(profileId));
            }

            System.Threading.Tasks.Task.Run(async () =>
            {
                List<(string FullName, string Pinned, string Latest)> updates = new();
                List<EssentialUpdate> essentials = new();
                string error = null;
                TsIndex index = null;
                try
                {
                    index = await ThunderstoreClient.GetIndexAsync(ProfileEngine.GameRoot, true, System.Threading.CancellationToken.None);
                    if (index == null) { error = "Mod index unavailable. Are you offline?"; }
                    else
                    {
                        var doc = ProfileEngine.LoadStore(out _);
                        var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
                        foreach (var m in p?.Mods.Where(m => m.Source == "thunderstore") ?? Enumerable.Empty<ProfileModRef>())
                        {
                            var latest = index.Find(m.FullName)?.Latest?.VersionNumber;
                            if (latest != null && TsIndex.CompareVersions(latest, m.Version) > 0)
                                updates.Add((m.FullName, m.Version, latest));
                        }
                    }
                }
                catch (Exception e) { error = e.Message; }

                // Side Hustle and S1API come from the player's own Mods folder, not from the profile; asked apart so an
                // index failure does not hide them.
                try { essentials = await EssentialUpdates.CheckAsync(index, System.Threading.CancellationToken.None); }
                catch (Exception e) { Core.Log?.Warning("[profiles] update check of Side Hustle and S1API failed: " + e.Message); }

                MainThread.Post(() =>
                {
                    Core.Log?.Msg($"[profiles] update check: {(error != null ? "failed: " + error : Words.Plural(updates.Count, "update", "updates"))}, "
                        + $"{Words.Plural(essentials.Count, "essential update", "essential updates")}.");
                    if (onPage)
                    {
                        page.Essentials = essentials;
                        page.Updates = updates;
                        page.Error = error;
                        page.Done = true;
                        _hubSinceState = 999f;
                        return;
                    }
                    if (_cloneScreen == null || !_cloneScreen.IsOpen) return;
                    ClearFormHost();
                    var h = CreateFormHost("SH_UpdateResults");
                    ProfilesViews.BuildUpdateResults(h, essentials, updates, error,
                        onUpdate: u => InstallIntoProfile(profileId, u.FullName, u.Latest, fromUpdate: true),
                        onBack: () => ShowProfileDetail(profileId));
                });
            });
        }

        // One install runs at a time; the CTS doubles as the "an install is running" marker.
        private static System.Threading.CancellationTokenSource _installCts;

        /// <summary>Shared install runner (browser + updates): the resolved dependency closure as a live progress
        /// screen (per-package status + byte-weighted bar), the download on a worker with cancel support, then the
        /// profile rebuild and back to the detail.</summary>
        private static void InstallIntoProfile(string profileId, string fullName, string version, bool fromUpdate = false)
        {
            if (_clone == null) return;
            if (_installCts != null)
            {
                ShowToast("An install is already running.", Severity.Warning);
                return;
            }
            _back = null;   // no backing out mid-install; Cancel on the screen is the only escape

            // Resolve the plan up front from the cached index so the screen shows every closure member immediately.
            var plan = new List<InstallPlanRow>();
            List<string> unresolved = null;
            var index = ThunderstoreClient.GetCachedIndexOrNull(ProfileEngine.GameRoot);
            if (index != null)
            {
                var closure = index.ResolveClosure(new[] { (fullName, version) }, out unresolved);
                // Mirror the engine: essentials are never pinned/downloaded, so they must not appear on the plan.
                closure.RemoveAll(c => Profiles.Essentials.IsEssentialPackageName(c.FullName));
                foreach (var (pkgName, pkgVer) in closure)
                {
                    plan.Add(new InstallPlanRow
                    {
                        FullName = pkgName,
                        Version = pkgVer,
                        Size = index.Find(pkgName)?.Get(pkgVer)?.FileSize ?? 0,
                        Cached = PackageCache.IsCached(ProfileEngine.CacheRoot, pkgName, pkgVer),
                        Key = $"{pkgName} {pkgVer}",
                        // Only the package NAME (after "Owner-") is a runtime hint - an owner named "Mono" is not.
                        NameLooksMono = RuntimeClassifier.FromNameTokens(
                            pkgName.Contains('-') ? pkgName.Substring(pkgName.IndexOf('-') + 1) : pkgName) == ModRuntime.Mono,
                    });
                }
            }
            if (plan.Count == 0)   // no cached index: the engine still resolves on its own, show the root at least
                plan.Add(new InstallPlanRow { FullName = fullName, Version = version, Key = $"{fullName} {version}" });

            var cts = new System.Threading.CancellationTokenSource();
            Action cancel = () => { try { cts.Cancel(); } catch (Exception e) { Core.Log?.Warning("[profiles] install cancel threw: " + e.Message); } };
            string headline = $"Installing {fullName} {version}";
            InstallProgressView.Controller ui = InstallProgressView.Controller.ForPage(headline, plan, unresolved, cancel);
            var pageUi = ui;
            if (!ShowOnPage("profiles.install", "profiles", pv => pageUi.BuildPage(pv)))
            {
                ClearFormHost();
                SetTmp(_clone.transform, "Title", "Installing");
                var host = CreateFormHost("SH_InstallProgress");
                ui = InstallProgressView.Build(host, headline, plan, unresolved, onCancel: cancel);
            }
            _installCts = cts;

            var sink = new UiInstallProgress(ui);
            System.Threading.Tasks.Task.Run(async () =>
            {
                var status = EnsureStatus.Failed;
                bool built = false;
                try
                {
                    status = await ProfileEngine.InstallPackageAsync(profileId, fullName, version, sink, cts.Token,
                        promoteRootToManual: !fromUpdate);
                    if (status == EnsureStatus.Ready)
                    {
                        MainThread.Post(() => Safe.Try("showing the applying step", () => ui.SetApplying()));
                        built = ProfileEngine.BuildProfile(profileId);
                    }
                }
                catch (Exception e) { Core.Log?.Warning("[profiles] install worker failed: " + e.Message); status = EnsureStatus.Failed; }

                // The marker reset MUST run whatever happened above, or InstallActive stays true forever and the
                // whole screen's right-click-back is swallowed until a restart.
                var finalStatus = status; var finalBuilt = built;
                MainThread.Post(() =>
                {
                    _installCts = null;
                    if (_cloneScreen == null || !_cloneScreen.IsOpen) return;
                    OnInstallFinished(profileId, fullName, version, finalStatus, finalBuilt, ui, fromUpdate);
                });
            });
        }

        /// <summary>Post-extract runtime verdict for a cached package: are ALL its Mods DLLs Mono builds (the
        /// package cannot work here), and did it ship a Mono flavor next to a loading one (routine dual package)?
        /// Reads the manifest's runtime map, classifying on demand for manifests from older builds.</summary>
        private static (bool AllModsWrong, bool DualSkipped) PackageRuntimeSummary(string fullName, string version)
        {
            try
            {
                string pkgDir = PackageCache.PathFor(ProfileEngine.CacheRoot, fullName, version);
                var mf = PackageCache.ReadManifest(pkgDir);
                if (mf == null || mf.Mods.Count == 0) return (false, false);

                int wrong = 0, loading = 0;
                foreach (var f in mf.Mods.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (Essentials.IsEssentialFile(f)) { loading++; continue; }
                    // Mirror the resolver: a file loads if ANY extracted copy of that name is not wrong-runtime
                    // (a same-name dual package has a Mono AND an IL2CPP copy - the IL2CPP one loads).
                    var copies = PackageCache.FindExtractedFileAll(pkgDir, f);
                    bool loads = copies.Count > 0
                        ? copies.Any(c => !RuntimeClassifier.IsWrongForThisGame(RuntimeClassifier.ClassifyFile(c)))
                        : !(mf.Runtime != null && mf.Runtime.TryGetValue(f, out var tag) && RuntimeClassifier.IsWrongForThisGame(RuntimeClassifier.FromTag(tag)));
                    if (loads) loading++;
                    else wrong++;
                }
                return (wrong > 0 && loading == 0, wrong > 0 && loading > 0);
            }
            catch (Exception e) { Safe.Report("reading a package's runtime summary", e); return (false, false); }
        }

        private static void OnInstallFinished(string profileId, string fullName, string version,
            EnsureStatus status, bool built, InstallProgressView.Controller ui, bool fromUpdate)
        {
            if (status == EnsureStatus.Ready)
            {
                if (!built)
                    Core.Log?.Warning($"[profiles] installed {fullName} {version} but the rebuild reported problems .");

                // Authoritative runtime verdict now that the DLLs are extracted: a package whose Mods are ALL
                // Mono builds installed fine but cannot load here - the player must see that, so no auto-return.
                var (allWrong, dualSkipped) = PackageRuntimeSummary(fullName, version);
                if (allWrong)
                {
                    ui.ShowWarning($"Installed, but {fullName} is made for another game version and cannot load.",
                        onBack: () => ShowProfileDetail(profileId));
                    ShowToast($"{fullName} is for another game version and stays disabled.", Severity.Warning);
                }
                else
                {
                    ui.ShowSuccess(fromUpdate ? "Updated." : "Installed.");
                    ShowToast($"{(fromUpdate ? "Updated" : "Installed")} {fullName} {version}.", Severity.Success);
                    MelonLoader.MelonCoroutines.Start(ReturnToDetailAfter(profileId, 1.2f, offerOrphans: fromUpdate));
                }
                if (dualSkipped)
                    ui.SetRuntimeNote($"{fullName} {version}", "Other game version skipped", warn: false);

                // Dependencies that turned out to be Mono-only get flagged on their rows too.
                foreach (var key in ui.Plan.Keys.ToList())
                {
                    var row = ui.Plan[key];
                    if (string.Equals(row.FullName, fullName, StringComparison.OrdinalIgnoreCase)) continue;
                    var (depAllWrong, depDual) = PackageRuntimeSummary(row.FullName, row.Version);
                    if (depAllWrong) ui.SetRuntimeNote(key, "Other game version - will not load", warn: true);
                    else if (depDual) ui.SetRuntimeNote(key, "Other game version skipped", warn: false);
                }
                return;
            }
            if (status == EnsureStatus.Cancelled)
            {
                ShowToast("Install cancelled.", Severity.Info);
                ShowProfileDetail(profileId);
                return;
            }
            Core.Log?.Error($"[profiles] install of {fullName} {version} failed ({status}).");
            ui.ShowError(
                status == EnsureStatus.Blocked
                    ? "Nothing to install: the package was not found in the index."
                    : "The install failed. Check your connection and try again.",
                onRetry: () => InstallIntoProfile(profileId, fullName, version, fromUpdate),
                onBack: () => ShowProfileDetail(profileId));
        }

        // Let the success state read for a moment, then return - unless the player already navigated elsewhere.
        private static System.Collections.IEnumerator ReturnToDetailAfter(string profileId, float seconds, bool offerOrphans = false)
        {
            yield return new UnityEngine.WaitForSeconds(seconds);
            if (_cloneScreen == null || !_cloneScreen.IsOpen) yield break;
            bool stillThere = _pageKey == "profiles.install" || (_formHost != null && _formHost.name == "SH_InstallProgress");
            if (!stillThere) yield break;
            ShowProfileDetail(profileId);
            // After an update the dependency closure can shrink (v2 drops a dependency v1 pulled in); offer to remove
            // the now-unused auto-installed dependencies, the same apt-autoremove flow a manual removal uses.
            if (offerOrphans) OfferOrphanCleanup(profileId);
        }

        /// <summary>Marshals engine progress to the UI, coalescing the ~80KB-granular byte reports into at most one
        /// pending main-thread update at a time so a fast download can never flood the per-frame queue budget.</summary>
        private sealed class UiInstallProgress : IProgress<ProfileProgress>
        {
            private readonly InstallProgressView.Controller _ui;
            private readonly object _lock = new object();
            private ProfileProgress _latest;
            private bool _posted;

            internal UiInstallProgress(InstallProgressView.Controller ui) { _ui = ui; }

            public void Report(ProfileProgress value)
            {
                lock (_lock)
                {
                    _latest = value;
                    if (_posted) return;
                    _posted = true;
                }
                MainThread.Post(() =>
                {
                    ProfileProgress pp;
                    lock (_lock) { pp = _latest; _posted = false; }
                    Safe.Try("updating the install progress", () => _ui.Report(pp));
                });
            }
        }

        private static void ShowPackageBrowser(string profileId)
        {
            if (_clone == null) return;
            _mpDesc = null;
            _back = () => ShowProfileDetail(profileId);

            // Snapshot the profile's Thunderstore packages so the browser can mark them as already installed.
            var doc = ProfileEngine.LoadStore(out _);
            var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
            var installed = new HashSet<string>(
                (p?.Mods ?? new List<ProfileModRef>())
                    .Where(m => m.Source == "thunderstore" && !string.IsNullOrEmpty(m.FullName))
                    .Select(m => m.FullName),
                StringComparer.OrdinalIgnoreCase);

            var browse = new BrowsePage { ProfileId = profileId, Name = p?.Name ?? "this profile", Installed = installed };
            if (ShowOnPage("profiles.browse", "profiles", pv => BuildBrowsePage(pv, browse)))
            {
                LoadBrowseIndex(browse);
                return;
            }

            ClearFormHost();
            SetTmp(_clone.transform, "Title", "Thunderstore");
            var host = CreateFormHost("SH_PackageBrowser");
            PackageBrowserView.Build(host,
                () => ShowProfileDetail(profileId),
                (fullName, version) => InstallIntoProfile(profileId, fullName, version),
                isInstalled: installed.Contains);
        }

        private static string ProfileName(string profileId)
        {
            var doc = ProfileEngine.LoadStore(out _);
            return doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase))?.Name ?? "this profile";
        }

        private static Transform DialogRoot()
        {
            var canvas = _clone != null ? _clone.GetComponentInParent<Canvas>() : null;
            return canvas != null ? canvas.transform : (_clone != null ? _clone.transform : null);
        }

        /// <summary>True while a package install is running - the input handler swallows right-click-back then, so a
        /// habitual right-click cannot close the whole menu out from under a headless download.</summary>
        internal static bool InstallActive => _installCts != null;

        /// <summary>If a modal remove/orphan/confirm/prompt dialog is open, dismiss the topmost one and report true,
        /// so a right-click closes the dialog instead of falling through to close the whole screen (which would
        /// leave the scrim - it lives on the canvas, not the clone - floating over the home menu). The restart
        /// countdown is intentionally excluded: it owns its own cancel handling and is gated separately.</summary>
        internal static bool CloseTopDialogIfAny()
        {
            var root = DialogRoot();
            if (root == null) return false;
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                var child = root.GetChild(i);
                string n = child.name ?? "";
                if (n == "DD_ConfirmScrim" || n == "DD_ChoiceScrim" || n == "DD_PromptScrim")
                {
                    UnityEngine.Object.Destroy(child.gameObject);
                    return true;
                }
            }
            return false;
        }
    }
}
