using System;
using System.Collections.Generic;
using System.Linq;
using DooDesch.UI;
using SideHustle.Phone;
using SideHustle.Profiles;
using SideHustle.Shared;

namespace SideHustle.Menu
{
    /// <summary>
    /// The Profiles tab on the hub page: the profiles on the left, the open one on the right, and its sub-screens
    /// (add an installed mod, updates, installing, the Thunderstore browser). The classic form-host views in
    /// <see cref="ProfilesViews"/> stay for when the page cannot draw.
    ///
    /// Every screen reads a snapshot taken when it opened; the store is not read again on every state push.
    /// Each edit reopens the screen through <see cref="ShowProfileDetail"/>, which takes a new snapshot.
    /// </summary>
    internal static partial class Hub
    {
        private sealed class ProfilesSnap
        {
            public ProfilesFile Doc;
            public bool Writable;
            public string Active = "";
            public ProfileDef Selected;
            public (string Host, int ModCount)? LastSync;
            public DependencyGraph Graph;
            public Dictionary<string, string> Notes;
        }

        private static ProfilesSnap TakeProfilesSnap(string selectId)
        {
            var doc = ProfileEngine.LoadStore(out bool writable);
            string active = ProfileEngine.ActiveProfileId ?? "";
            var s = new ProfilesSnap { Doc = doc, Writable = writable, Active = active };
            s.Selected = doc.Profiles.FirstOrDefault(x => x.Id.Equals(selectId ?? "", StringComparison.OrdinalIgnoreCase))
                         ?? doc.Profiles.FirstOrDefault(x => x.Id.Equals(active, StringComparison.OrdinalIgnoreCase))
                         ?? doc.Profiles.FirstOrDefault();
            var recent = Sync.LastSync.Load();
            if (recent != null) s.LastSync = (string.IsNullOrWhiteSpace(recent.Host) ? "a lobby" : recent.Host, recent.ModCount);
            if (s.Selected != null)
            {
                s.Graph = BuildGraph(s.Selected);
                s.Notes = BuildRuntimeNotes(s.Selected);
            }
            return s;
        }

        /// <summary>The list beside the profile <paramref name="selectId"/> (null: the active one, else the first).</summary>
        private static bool ShowProfilesPage(string selectId)
        {
            ProfilesSnap snap;
            try { snap = TakeProfilesSnap(selectId); }
            catch (Exception e)
            {
                Core.Log?.Error("[profiles] reading the profiles for the hub page failed, using the classic screen: " + e);
                return false;
            }
            return ShowOnPageKeep("profiles", "profiles", pv => BuildProfilesPage(pv, snap));
        }

        private static void BuildProfilesPage(PageView pv, ProfilesSnap s)
        {
            pv.Frame = "main";
            pv.OnBack(ProfilesBack);

            var left = new List<Json>();
            if (!s.Writable)
                left.Add(pv.Say("need", "alert", "Profiles are read-only: the profile file is unreadable or newer than this Side Hustle.", null));

            // Nothing to list and nothing to open: one empty state across the page instead of a narrow empty column
            // beside an empty right half.
            if (s.Doc.Profiles.Count == 0 && string.IsNullOrEmpty(s.Active))
            {
                var first = new List<Json>();
                if (s.Writable) first.Add(pv.Btn("New profile", PromptNewProfile, "go", "plus"));
                if (s.Writable && s.LastSync.HasValue) first.Add(pv.Btn("From " + s.LastSync.Value.Host, CreateFromLastSync, "", "restore"));
                left.Add(pv.Card(null, true, pv.Big("package", "No profiles yet",
                    "A profile is a mod set of its own. Switching to one restarts the game with only those mods.",
                    -1, null, null, first.ToArray())));
                pv.Add(left);
                return;
            }

            if (s.Writable && s.LastSync.HasValue)
                left.Add(pv.Say("now", "plus", "From last lobby: " + s.LastSync.Value.Host,
                    $"Save the {Words.Plural(s.LastSync.Value.ModCount, "mod", "mods")} you synced there as a profile.",
                    pv.Btn("Create", CreateFromLastSync, "sm go")));

            var list = new List<Json>();
            if (!string.IsNullOrEmpty(s.Active))
                list.Add(pv.Item("Full mod set", "Switch back to everything in your Mods folder (restarts the game).",
                    acts: new[] { pv.Btn("Switch", () => ConfirmSwitch("", "your full mod set"), "sm") }));
            foreach (var p in s.Doc.Profiles)
            {
                var def = p;
                bool isActive = def.Id.Equals(s.Active, StringComparison.OrdinalIgnoreCase);
                bool isDefault = def.Id.Equals(s.Doc.Settings.DefaultProfileId, StringComparison.OrdinalIgnoreCase);
                string sub = Words.Plural(ExtraModCount(def), "mod", "mods")
                             + (!isActive && isDefault ? " - default" : "")
                             + (string.IsNullOrEmpty(def.Notes) ? "" : " - " + def.Notes);
                bool sel = s.Selected != null && def.Id.Equals(s.Selected.Id, StringComparison.OrdinalIgnoreCase);
                list.Add(pv.Item(def.Name, sub, isActive ? "Active" : null, live: isActive, sel: sel,
                    onClick: () => ShowProfileDetail(def.Id),
                    acts: isActive ? null : new[] { pv.Btn("Switch", () => ConfirmSwitch(def.Id, def.Name), "sm") }));
            }
            if (s.Doc.Profiles.Count == 0) list.Add(pv.Quiet("No profiles yet."));

            // The button spans the card under the list: beside the heading it did not fit the narrow column.
            left.Add(pv.Card(null, true,
                pv.Heading("MOD PROFILES"),
                pv.Scroll(list),
                s.Writable ? pv.Btn("New profile", PromptNewProfile, "", "plus") : null));

            var right = new List<Json>();
            if (s.Selected != null) right.Add(ProfileDetailCard(pv, s));
            pv.Add(pv.Two(left, right));
        }

        // The essentials are seeded into every profile; a new profile does not read "2 mods".
        private static int ExtraModCount(ProfileDef def) =>
            (def.Mods ?? new List<ProfileModRef>()).Count(m => !(m.Source == "base" && IsEssentialFile(m.File)));

        private static Json ProfileDetailCard(PageView pv, ProfilesSnap s)
        {
            var p = s.Selected;
            bool w = s.Writable;
            bool isActive = p.Id.Equals(s.Active, StringComparison.OrdinalIgnoreCase);
            var mods = p.Mods ?? new List<ProfileModRef>();
            var essentials = mods.Where(m => m.Source == "base" && IsEssentialFile(m.File)).Select(m => StripDll(m.File)).ToList();
            var removable = mods.Where(m => !(m.Source == "base" && IsEssentialFile(m.File))).ToList();
            var manual = removable.Where(m => !(m.Source == "thunderstore" && m.AsDependency)).ToList();
            var deps = removable.Where(m => m.Source == "thunderstore" && m.AsDependency).ToList();

            var kids = new List<Json>
            {
                pv.DHead(null, p.Name, Words.Plural(removable.Count, "mod", "mods") + (string.IsNullOrWhiteSpace(p.Notes) ? "" : " - " + p.Notes),
                    w ? pv.Btn("Check for updates", () => RunUpdateCheck(p.Id), "sm", "refresh") : null),
            };
            if (w)
            {
#if NO_EXTERNAL_FETCH
                kids.Add(pv.Row(new[] { pv.Quiet("Thunderstore installs are not part of this build."), pv.Sp(),
                    pv.Btn("Pick from installed mods", () => ShowAddBaseMod(p.Id), "sm", "plus") }));
#else
                kids.Add(pv.Row(new[] { pv.Btn("Browse Thunderstore", () => ShowPackageBrowser(p.Id), "sm go", "search"),
                    pv.Btn("Pick from installed mods", () => ShowAddBaseMod(p.Id), "sm", "plus") }));
#endif
            }

            var rows = new List<Json>();
            if (essentials.Count > 0) rows.Add(pv.Quiet("Always included: " + string.Join(", ", essentials) + "."));
            if (removable.Count == 0) rows.Add(pv.Quiet("No extra mods yet."));
            else
            {
                rows.Add(pv.Group("MODS IN THIS PROFILE", manual.Count.ToString()));
                foreach (var m in manual) rows.Add(ProfileModRow(pv, s, p.Id, m, false));
                if (deps.Count > 0)
                {
                    rows.Add(pv.Group("DEPENDENCIES (INSTALLED AUTOMATICALLY)", deps.Count.ToString()));
                    foreach (var m in deps) rows.Add(ProfileModRow(pv, s, p.Id, m, true));
                }
            }
            kids.Add(pv.Scroll(rows));

            var maintain = new List<Json>();
            if (w)
            {
                maintain.Add(pv.Btn("Rename", () => PromptRenameProfile(p.Id, p.Name), "sm"));
                maintain.Add(pv.Btn("Edit description", () => PromptEditDescription(p.Id, p.Notes), "sm"));
                maintain.Add(pv.Btn("Open folder", () => OpenProfileFolder(p.Id), "sm", "folder"));
                maintain.Add(pv.Btn("Delete", () => ConfirmDeleteProfile(p.Id, p.Name), "sm bad2"));
            }
            // An active profile restarts so an edit is applied; SwitchTo rebuilds it and relaunches.
            kids.Add(pv.Foot(null, maintain, new[] { pv.Btn(isActive ? "Restart" : "Activate", () => ConfirmSwitch(p.Id, p.Name), "go") }));
            return pv.Card(null, true, kids.ToArray());
        }

        private static Json ProfileModRow(PageView pv, ProfilesSnap s, string profileId, ProfileModRef mref, bool dependency)
        {
            string extra = s.Graph != null ? Annotate(s.Graph, s.Notes, mref) : null;
            string title, sub;
            switch (mref.Source)
            {
                case "thunderstore":
                    title = mref.FullName;
                    // The dependency hint first, so "required by ..." is never cut off behind the file list.
                    sub = !string.IsNullOrEmpty(extra)
                        ? $"Thunderstore {mref.Version}  -  {extra}"
                        : $"Thunderstore {mref.Version} - {(mref.Files == null || mref.Files.Count == 0 ? "?" : string.Join(", ", mref.Files))}";
                    break;
                case "local":
                    title = StripDll(mref.File);
                    sub = "Local file in this profile." + (string.IsNullOrEmpty(extra) ? "" : "  -  " + extra);
                    break;
                default:
                    title = StripDll(mref.File);
                    sub = "Linked from your Mods folder." + (string.IsNullOrEmpty(extra) ? "" : "  -  " + extra);
                    break;
            }
            var acts = s.Writable ? new[] { pv.Btn("Remove", () => ConfirmRemoveMod(profileId, mref), "sm") } : null;
            return pv.Item(title, sub, acts: acts, dep: dependency);
        }

        // --- add an installed mod ---

        private static void BuildAddBasePage(PageView pv, string profileId, string profileName, List<string> candidates,
            HashSet<string> added, Action<string> onAdd)
        {
            pv.Frame = "board";
            pv.Add(pv.Head("Add an installed mod", profileName, "Profile", () => ShowProfileDetail(profileId)));
            var rows = new List<Json>();
            if (candidates.Count == 0) rows.Add(pv.Quiet("Every installed mod is already in this profile."));
            foreach (var file in candidates)
            {
                string f = file;
                var act = added.Contains(f)
                    ? pv.Btn("Added", null, "sm ok2", "check", off: true)
                    : pv.Btn("Add", () => onAdd(f), "sm go", "plus");
                rows.Add(pv.Item(StripDll(f), f, acts: new[] { act }));
            }
            pv.Add(pv.Scroll(rows));
        }

        // --- updates ---

        private sealed class UpdatesPage
        {
            public string ProfileId;
            public string Name;
            public bool Done;
            public string Error;
            public List<(string FullName, string Pinned, string Latest)> Updates = new List<(string, string, string)>();
            public List<EssentialUpdate> Essentials = new List<EssentialUpdate>();
        }

        private static void BuildUpdatesPage(PageView pv, UpdatesPage u)
        {
            pv.Frame = "board";
            pv.Add(pv.Head("Updates", u.Name, "Profile", () => ShowProfileDetail(u.ProfileId)));
            if (!u.Done) { pv.Add(pv.Say("now", "busy", "Refreshing the Thunderstore index...", null)); return; }
            if (u.Updates.Count == 0 && u.Essentials.Count == 0)
            {
                pv.Add(u.Error != null
                    ? pv.Say("bad", "bad", "Update check failed: " + u.Error, null)
                    : pv.Say("ok", "ok", "Everything is up to date.", null));
                return;
            }

            var rows = new List<Json>();
            if (u.Essentials.Count > 0)
            {
                // Every profile runs the copy from the player's Mods folder, so these update there, not here.
                rows.Add(pv.Group("SIDE HUSTLE AND S1API", u.Essentials.Count.ToString()));
                foreach (var e in u.Essentials)
                {
                    var ess = e;
                    rows.Add(pv.Item(ess.Name, $"{ess.Installed} -> {ess.Latest} - update it in your mod manager",
                        acts: new[] { pv.Btn("Open page", () => DownloadLink.Open(ess.Url), "sm", "link") }));
                }
            }
            if (u.Error != null) rows.Add(pv.Say("bad", "bad", "Update check of the profile failed: " + u.Error, null));
            else if (u.Updates.Count == 0) rows.Add(pv.Quiet("The mods in this profile are up to date."));
            else
            {
                rows.Add(pv.Group("MODS IN THIS PROFILE", u.Updates.Count.ToString()));
                foreach (var x in u.Updates)
                {
                    var upd = x;
                    rows.Add(pv.Item(upd.FullName, $"{upd.Pinned} -> {upd.Latest}",
                        acts: new[] { pv.Btn("Update", () => InstallIntoProfile(u.ProfileId, upd.FullName, upd.Latest, fromUpdate: true), "sm go", "download") }));
                }
            }
            pv.Add(pv.Scroll(rows));
        }

        // --- Thunderstore ---

        private sealed class BrowsePage
        {
            public string ProfileId;
            public string Name;
            public HashSet<string> Installed;
            public TsIndex Index;
            public bool Failed;
            public string Query = "";
            public string ResultsFor;
            public List<TsPackage> Results = new List<TsPackage>();
            // Installs started from this browser, oldest first. The player stays in the list while they run.
            public List<BrowseJob> Jobs = new List<BrowseJob>();
        }

        private enum BrowseJobState { Queued, Running, Done, Warn, Failed }

        private sealed class BrowseJob
        {
            public string FullName;
            public string Version;
            /// <summary>The package name as the list shows it: "Owner-Name" without the owner.</summary>
            public string Name => FullName.IndexOf('-') is var i && i >= 0 ? FullName.Substring(i + 1) : FullName;
            public BrowseJobState State;
            public InstallProgressView.Controller Ui;
            public System.Threading.CancellationTokenSource Cts;
        }

        private static bool BrowseBusy(BrowsePage b) =>
            b.Jobs.Any(j => j.State == BrowseJobState.Queued || j.State == BrowseJobState.Running);

        /// <summary>Install click in the browser: queue the package and start it when nothing else runs.</summary>
        private static void QueueBrowseInstall(BrowsePage b, string fullName, string version)
        {
            if (b.Jobs.Any(j => (j.State == BrowseJobState.Queued || j.State == BrowseJobState.Running)
                                && j.FullName.Equals(fullName, StringComparison.OrdinalIgnoreCase)))
            {
                Core.Log?.Msg($"[profiles] browser: {fullName} is already queued.");
                return;
            }
            b.Jobs.RemoveAll(j => j.FullName.Equals(fullName, StringComparison.OrdinalIgnoreCase));
            b.Jobs.Add(new BrowseJob { FullName = fullName, Version = version, State = BrowseJobState.Queued });
            Core.Log?.Msg($"[profiles] browser: queued {fullName} {version} ({b.Jobs.Count(j => j.State == BrowseJobState.Queued)} waiting).");
            RunNextBrowseJob(b);
        }

        private static void RunNextBrowseJob(BrowsePage b)
        {
            if (b.Jobs.Any(j => j.State == BrowseJobState.Running)) return;
            var job = b.Jobs.FirstOrDefault(j => j.State == BrowseJobState.Queued);
            if (job == null) return;
            if (_installCts != null)
            {
                Core.Log?.Warning($"[profiles] browser: another install is running; {job.FullName} waits.");
                return;
            }

            var plan = ResolveInstallPlan(job.FullName, job.Version, out var unresolved);
            job.Cts = new System.Threading.CancellationTokenSource();
            var cts = job.Cts;
            Action cancel = () => { try { cts.Cancel(); } catch (Exception e) { Core.Log?.Warning("[profiles] install cancel threw: " + e.Message); } };
            job.Ui = InstallProgressView.Controller.ForPage($"Installing {job.Name} {job.Version}", plan, unresolved, cancel);
            job.State = BrowseJobState.Running;
            _hubSinceState = 999f;
            Core.Log?.Msg($"[profiles] browser: installing {job.FullName} {job.Version} ({plan.Count} package(s)).");
            RunInstallWorker(b.ProfileId, job.FullName, job.Version, false, job.Ui, cts,
                (status, built) => OnBrowseJobFinished(b, job, status, built));
        }

        /// <summary>Cancel in the browser's install strip: stops the running download and drops the queue.</summary>
        private static void CancelBrowseInstalls(BrowsePage b)
        {
            int dropped = b.Jobs.RemoveAll(j => j.State == BrowseJobState.Queued);
            var run = b.Jobs.FirstOrDefault(j => j.State == BrowseJobState.Running);
            Core.Log?.Msg($"[profiles] browser: cancel ({(run != null ? run.FullName : "nothing running")}, {dropped} queued dropped).");
            run?.Ui?.OnCancel?.Invoke();
            _hubSinceState = 999f;
        }

        private static void OnBrowseJobFinished(BrowsePage b, BrowseJob job, EnsureStatus status, bool built)
        {
            switch (status)
            {
                case EnsureStatus.Ready:
                {
                    if (!built) Core.Log?.Warning($"[profiles] installed {job.FullName} {job.Version} but the rebuild reported problems.");
                    var (allWrong, _) = PackageRuntimeSummary(job.FullName, job.Version);
                    job.Ui.ShowSuccess("Installed.");
                    // The whole closure is in the profile now, so its members read "Installed" in the list too.
                    foreach (var row in job.Ui.Plan.Values) b.Installed.Add(row.FullName);
                    b.Installed.Add(job.FullName);
                    if (allWrong)
                    {
                        job.State = BrowseJobState.Warn;
                        ShowToast($"{job.Name} is for another game version and stays disabled.", Severity.Warning);
                    }
                    else
                    {
                        job.State = BrowseJobState.Done;
                        ShowToast($"Installed {job.Name} {job.Version}.", Severity.Success);
                    }
                    Core.Log?.Msg($"[profiles] browser: {job.FullName} {job.Version} {(allWrong ? "installed for another game version" : "installed")}.");
                    break;
                }
                case EnsureStatus.Cancelled:
                    b.Jobs.Remove(job);
                    ShowToast("Install cancelled.", Severity.Info);
                    Core.Log?.Msg($"[profiles] browser: {job.FullName} {job.Version} cancelled.");
                    break;
                default:
                    job.State = BrowseJobState.Failed;
                    job.Ui.ShowError(status == EnsureStatus.Blocked
                        ? "The package was not found in the index."
                        : "The install failed. Check your connection.", null, null);
                    ShowToast(status == EnsureStatus.Blocked
                        ? $"{job.Name} was not found in the index."
                        : $"Couldn't install {job.Name}. Check your connection.", Severity.Danger);
                    Core.Log?.Error($"[profiles] browser: install of {job.FullName} {job.Version} failed ({status}).");
                    break;
            }
            _hubSinceState = 999f;

            if (_cloneScreen == null || !_cloneScreen.IsOpen)
            {
                int dropped = b.Jobs.RemoveAll(j => j.State == BrowseJobState.Queued);
                if (dropped > 0) Core.Log?.Warning($"[profiles] browser: the menu closed; {dropped} queued install(s) dropped.");
                return;
            }
            RunNextBrowseJob(b);
        }

        private static void LoadBrowseIndex(BrowsePage b)
        {
            b.Failed = false;
            _hubSinceState = 999f;
            System.Threading.Tasks.Task.Run(async () =>
            {
                TsIndex idx = null;
                try { idx = await ThunderstoreClient.GetIndexAsync(ProfileEngine.GameRoot, false, System.Threading.CancellationToken.None); }
                catch (Exception e) { Core.Log?.Warning("[profiles] could not load the Thunderstore index (offline?): " + e.Message); }
                MainThread.Post(() =>
                {
                    if (idx == null)
                    {
                        Core.Log?.Warning("[profiles] the Thunderstore index did not load; offering a retry.");
                        b.Failed = true;
                    }
                    else
                    {
                        b.Index = idx;
                        Core.Log?.Msg($"[profiles] browser: index loaded ({idx.Packages.Count} packages).");
                    }
                    _hubSinceState = 999f;
                });
            });
        }

        private static void BuildBrowsePage(PageView pv, BrowsePage b)
        {
            pv.Frame = "board";
            // While installs run the browser holds the tabs and the way back, like the install screen did: the
            // profile must not be switched, edited or deleted under a running download.
            bool busy = BrowseBusy(b);
            pv.Lock = busy;
            pv.Add(pv.Head("Thunderstore", $"Installs into {b.Name}, dependencies included.",
                busy ? null : "Profile", busy ? null : (Action)(() => ShowProfileDetail(b.ProfileId))));
            pv.Add(pv.Row(new[]
            {
                pv.Input("search", b.Query, "Search packages", v => b.Query = v ?? "", cls: "search"),
                pv.Sp(),
                pv.Seg(PackageBrowserView.SortNames, PackageBrowserView.SortMode, i => PackageBrowserView.SortMode = i),
            }));

            if (b.Index == null)
            {
                pv.Add(b.Failed
                    ? pv.Say("bad", "bad", "Couldn't load the mod list.", null, pv.Btn("Retry", () => LoadBrowseIndex(b), "sm go", "refresh"))
                    : pv.Say("now", "busy", "Loading the package index...", null));
                return;
            }

            string resultsFor = PackageBrowserView.SortMode + "|" + b.Query;
            if (b.ResultsFor != resultsFor)
            {
                b.Results = PackageBrowserView.Search(b.Index, b.Query);
                b.ResultsFor = resultsFor;
            }

            var rows = new List<Json>();
            foreach (var pkg in b.Results)
            {
                var p = pkg;
                string ver = p.Latest.VersionNumber;
                var job = b.Jobs.LastOrDefault(j => j.FullName.Equals(p.FullName, StringComparison.OrdinalIgnoreCase));
                var info = pv.Btn("Info", () => DownloadLink.Open(p.PackageUrl), "sm", "link");
                Json main;
                string state = null, tone = null;
                int pct = -1;
                switch (job?.State)
                {
                    case BrowseJobState.Running:
                        pct = job.Ui.BarPercent;
                        state = pct + "%";
                        main = pv.Btn("Installing", null, "sm", "download", off: true);
                        break;
                    case BrowseJobState.Queued:
                        main = pv.Btn("Queued", null, "sm", "clock", off: true);
                        break;
                    case BrowseJobState.Failed:
                        state = "Failed";
                        tone = "bad";
                        main = pv.Btn("Retry", () => QueueBrowseInstall(b, p.FullName, ver), "sm", "refresh");
                        break;
                    case BrowseJobState.Warn:
                        state = "Other game version";
                        tone = "need";
                        main = pv.Btn("Installed", null, "sm ok2", "check", off: true);
                        break;
                    default:
                        main = b.Installed.Contains(p.FullName)
                            ? pv.Btn("Installed", null, "sm ok2", "check", off: true)
                            : pv.Btn("Install", () => QueueBrowseInstall(b, p.FullName, ver), "sm go", "download");
                        break;
                }
                rows.Add(pv.Item(p.Name,
                    $"by {p.Owner} - v{ver} - {PackageBrowserView.Downloads(p.TotalDownloads)} {(p.TotalDownloads == 1 ? "download" : "downloads")}",
                    state, tone, mono: Initials(p.Name), pic: HubPackageIcon(p.Latest.Icon), tag: p.IsModpack ? "Modpack" : null,
                    pct: pct, acts: new[] { info, main }));
            }
            if (rows.Count == 0) rows.Add(pv.Quiet("No packages match."));
            pv.Add(pv.Scroll(rows));

            var run = b.Jobs.FirstOrDefault(j => j.State == BrowseJobState.Running);
            if (run != null) AddBrowseInstallStrip(pv, b, run);
        }

        /// <summary>The strip under the list while an install runs: what downloads, how far, what waits, Cancel.</summary>
        private static void AddBrowseInstallStrip(PageView pv, BrowsePage b, BrowseJob run)
        {
            var ui = run.Ui;
            int waiting = b.Jobs.Count(j => j.State == BrowseJobState.Queued);
            var sub = new List<string> { ui.StatusText };
            if (waiting > 0) sub.Add($"{waiting} queued");
            Json cancel = ui.Cancelling
                ? pv.Btn("Cancelling...", null, "sm")
                : ui.CanCancel ? pv.Btn("Cancel", () => CancelBrowseInstalls(b), "sm", "close") : null;
            pv.Add(pv.Say("now", "busy", $"Installing {run.Name} {run.Version}", string.Join(" - ", sub), cancel));
            pv.Add(pv.Progress(ui.BarPercent, null, ui.BarTone));
        }
    }
}
