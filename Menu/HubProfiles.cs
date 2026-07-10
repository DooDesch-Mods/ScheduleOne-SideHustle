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
        /// <summary>Open the profiles screen (called by the injected "Mod Profiles" main-menu button).</summary>
        internal static void OpenProfilesScreen()
        {
            EnsureInit();
            EnsureClone();
            if (_cloneScreen == null) { Core.Log?.Warning("[hub] profiles screen unavailable."); return; }
            ShowProfilesList();
            if (!_cloneScreen.IsOpen) _cloneScreen.Open(closePrevious: true);
        }

#if DEBUG
        /// <summary>Dev.SelfTest only: same as the main-menu button (the MCP dev loop cannot click).</summary>
        internal static void OpenProfilesForTest() => OpenProfilesScreen();

        /// <summary>Dev.SelfTest only: open the FIRST profile's detail view for a screenshot.</summary>
        internal static void OpenFirstProfileDetailForTest()
        {
            OpenProfilesScreen();
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
            OpenProfilesScreen();
            string id = FirstOrDemoProfileId();
            if (id != null) ShowPackageBrowser(id);
        }

        /// <summary>Dev.SelfTest only: open the update-check results (for a screenshot).</summary>
        internal static void OpenUpdatesForTest()
        {
            OpenProfilesScreen();
            string id = FirstOrDemoProfileId();
            if (id != null) RunUpdateCheck(id);
        }

        /// <summary>Dev.SelfTest only: open the "add an installed mod" picker (for a screenshot).</summary>
        internal static void OpenAddBaseForTest()
        {
            OpenProfilesScreen();
            string id = FirstOrDemoProfileId();
            if (id != null) ShowAddBaseMod(id);
        }

        /// <summary>Dev.SelfTest only: drive the REAL UI install flow (browser Install click equivalent) for the
        /// latest version of a package into the first profile - the end-to-end the MCP loop cannot click.</summary>
        internal static void InstallForTest(string fullName)
        {
            OpenProfilesScreen();
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
#endif

        // Side Hustle and the mods it stands on are seeded into every profile and must survive every edit -
        // removing them would strip the manager (or its API layer) out of the profile it manages.
        private static bool IsEssentialFile(string file)
        {
            string f = NormToken(file);
            return f.Contains("sidehustle") || f.Contains("s1api");
        }

        private static string NormToken(string s) =>
            s == null ? "" : new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        private static void ShowProfilesList()
        {
            if (_clone == null) return;
            _mpDesc = null;
            _back = null;   // right-click at the profiles root closes back to the main menu (TickInput default)

            ClearFormHost();
            SetTmp(_clone.transform, "Title", "Mod Profiles");
            var host = CreateFormHost("SH_ProfilesList", 560f);
            ProfilesViews.BuildList(host,
                onOpen: ShowProfileDetail,
                onSwitchFullSet: () => ConfirmSwitch("", "your full mod set"),
                onNew: PromptNewProfile,
                onBack: () => _cloneScreen?.Close(openPrevious: true));
        }

        private static void PromptNewProfile()
        {
            var root = DialogRoot();
            if (root == null) return;
            Components.PromptDialog(root, "New profile", "Name the new mod profile.", "profile name", "Create", name =>
            {
                if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
                var def = ProfileEngine.CreateProfile(name.Trim());
                if (def == null) return "Could not create the profile (see log).";
                SeedEssentials(def.Id);
                ShowProfileDetail(def.Id);
                return null;
            });
        }

        private static void PromptRenameProfile(string profileId, string currentName)
        {
            var root = DialogRoot();
            if (root == null) return;
            Components.PromptDialog(root, "Rename profile", "Give this profile a new name.", currentName ?? "profile name", "Rename", name =>
            {
                if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
                if (!ProfileEngine.RenameProfile(profileId, name.Trim())) return "Could not rename the profile (see log).";
                ShowProfileDetail(profileId);
                return null;
            });
        }

        private static void PromptEditDescription(string profileId, string currentNotes)
        {
            var root = DialogRoot();
            if (root == null) return;
            Components.PromptDialog(root, "Edit description", "A short note about what this profile is for (leave empty to clear).",
                string.IsNullOrEmpty(currentNotes) ? "e.g. lightweight co-op set" : currentNotes, "Save", notes =>
            {
                if (!ProfileEngine.SetDescription(profileId, notes)) return "Could not save the description (see log).";
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
                    return;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch (Exception e) { Core.Log?.Warning("[profiles] could not open the profile folder: " + e.Message); }
        }

        // A profile without Side Hustle would boot without this menu (and without the switch-back entry) - seed
        // every new profile with the hub + its API layer so the player can always manage/switch in-profile.
        private static void SeedEssentials(string profileId)
        {
            try
            {
                var doc = ProfileEngine.LoadStore(out bool writable);
                if (!writable) return;
                var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
                if (p == null) return;
                foreach (var m in Mods.ModInventory.Loaded())
                {
                    if (!IsEssentialFile(m.File)) continue;
                    if (p.Mods.Any(r => string.Equals(r.File, m.File, StringComparison.OrdinalIgnoreCase))) continue;
                    p.Mods.Add(new ProfileModRef { Source = "base", File = m.File });
                }
                ProfileEngine.SaveStore(doc);
            }
            catch (Exception e) { Core.Log?.Warning("[profiles] essentials seed failed: " + e.Message); }
        }

        private static void ShowProfileDetail(string profileId)
        {
            if (_clone == null) return;
            _mpDesc = null;
            _back = ShowProfilesList;

            var doc = ProfileEngine.LoadStore(out bool writable);
            var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
            if (p == null) { ShowProfilesList(); return; }

            ClearFormHost();
            SetTmp(_clone.transform, "Title", p.Name);
            var host = CreateFormHost("SH_ProfileDetail", 560f);
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
                onEditDescription: writable ? () => PromptEditDescription(p.Id, p.Notes) : (Action)null);
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

            ClearFormHost();
            SetTmp(_clone.transform, "Title", "Add an installed mod");
            var host = CreateFormHost("SH_AddBaseMod", 560f);
            ProfilesViews.BuildAddBase(host, candidates,
                onAdd: f => { AddBaseMod(profileId, f); ShowProfileDetail(profileId); },
                onBack: () => ShowProfileDetail(profileId));
        }

        private static void AddBaseMod(string profileId, string file)
        {
            var doc = ProfileEngine.LoadStore(out bool writable);
            if (!writable) return;
            var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
            if (p == null) return;
            p.Mods.Add(new ProfileModRef { Source = "base", File = file });
            p.Modified = DateTime.UtcNow.ToString("o");
            ProfileEngine.SaveStore(doc);
        }

        private static void ConfirmRemoveMod(string profileId, ProfileModRef mref)
        {
            var root = DialogRoot();
            if (root == null) return;
            string label = mref.Source == "thunderstore" ? mref.FullName : mref.File;
            Components.ConfirmDialog(root, "Remove from profile",
                $"Remove {label} from this profile? The files themselves are not deleted.", "Remove", () =>
            {
                var doc = ProfileEngine.LoadStore(out bool writable);
                if (!writable) return;
                var p = doc.Profiles.FirstOrDefault(x => x.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
                var hit = p?.Mods.FirstOrDefault(m =>
                    m.Source == mref.Source &&
                    string.Equals(m.File, mref.File, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(m.FullName, mref.FullName, StringComparison.OrdinalIgnoreCase));
                // Defense in depth: essentials are not listed as removable, but never remove one either way.
                if (hit != null && !(hit.Source == "base" && IsEssentialFile(hit.File)))
                {
                    p.Mods.Remove(hit);
                    ProfileEngine.SaveStore(doc);
                }
                ShowProfileDetail(profileId);
            });
        }

        private static void ConfirmDeleteProfile(string profileId, string name)
        {
            var root = DialogRoot();
            if (root == null) return;
            Components.ConfirmDialog(root, "Delete profile", $"Delete '{name}'? Cached downloads and your Mods folder stay as they are.", "Delete", () =>
            {
                ProfileEngine.DeleteProfile(profileId);
                ShowProfilesList();
            });
        }

        private static void ConfirmSwitch(string profileId, string label)
        {
            var root = DialogRoot();
            if (root == null) return;
            Components.ConfirmDialog(root, "Restart into " + label,
                "The game restarts and loads exactly this profile's mods. You can always get back from the main menu.",
                "Restart now", () => ProfileEngine.SwitchTo(profileId));
        }

        private static void RunUpdateCheck(string profileId)
        {
            if (_clone == null) return;
            _back = () => ShowProfileDetail(profileId);

            ClearFormHost();
            SetTmp(_clone.transform, "Title", "Updates");
            var host = CreateFormHost("SH_Updates", 560f);
            ProfilesViews.BuildUpdatesPending(host, () => ShowProfileDetail(profileId));

            System.Threading.Tasks.Task.Run(async () =>
            {
                List<(string FullName, string Pinned, string Latest)> updates = new();
                string error = null;
                try
                {
                    var index = await ThunderstoreClient.GetIndexAsync(ProfileEngine.GameRoot, true, System.Threading.CancellationToken.None);
                    if (index == null) { error = "Index unavailable (offline?)."; }
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

                MainThread.Post(() =>
                {
                    if (_cloneScreen == null || !_cloneScreen.IsOpen) return;
                    ClearFormHost();
                    var h = CreateFormHost("SH_UpdateResults", 560f);
                    ProfilesViews.BuildUpdateResults(h, updates, error,
                        onUpdate: u => InstallIntoProfile(profileId, u.FullName, u.Latest),
                        onBack: () => ShowProfileDetail(profileId));
                });
            });
        }

        /// <summary>Shared install runner (browser + updates): download on a worker, rebuild, back to the detail.</summary>
        private static void InstallIntoProfile(string profileId, string fullName, string version)
        {
            if (_clone == null) return;
            _back = null;   // no backing out mid-install; the flow returns on its own

            ClearFormHost();
            SetTmp(_clone.transform, "Title", "Installing...");
            var host = CreateFormHost("SH_Installing", 560f);
            ProfilesViews.BuildInstalling(host, $"Installing {fullName} {version} - this returns on its own...");

            System.Threading.Tasks.Task.Run(async () =>
            {
                var status = await ProfileEngine.InstallPackageAsync(profileId, fullName, version, null, System.Threading.CancellationToken.None);
                bool built = status == EnsureStatus.Ready && ProfileEngine.BuildProfile(profileId);
                MainThread.Post(() =>
                {
                    if (_cloneScreen == null || !_cloneScreen.IsOpen) return;
                    if (status != EnsureStatus.Ready)
                        Core.Log?.Error($"[profiles] install of {fullName} {version} failed ({status}).");
                    else if (!built)
                        Core.Log?.Warning($"[profiles] installed {fullName} {version} but the rebuild reported problems (see log).");
                    ShowProfileDetail(profileId);
                });
            });
        }

        private static void ShowPackageBrowser(string profileId)
        {
            if (_clone == null) return;
            _mpDesc = null;
            _back = () => ShowProfileDetail(profileId);
            ClearFormHost();
            SetTmp(_clone.transform, "Title", "Thunderstore");

            var host = CreateFormHost("SH_PackageBrowser", 560f);
            PackageBrowserView.Build(host,
                () => ShowProfileDetail(profileId),
                (fullName, version) => InstallIntoProfile(profileId, fullName, version));
        }

        private static Transform DialogRoot()
        {
            var canvas = _clone != null ? _clone.GetComponentInParent<Canvas>() : null;
            return canvas != null ? canvas.transform : (_clone != null ? _clone.transform : null);
        }
    }
}
