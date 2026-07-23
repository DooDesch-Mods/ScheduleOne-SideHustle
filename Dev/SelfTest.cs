#if DEBUG
using System;
using System.Linq;
using SideHustle.Config;
using SideHustle.Mods;

namespace SideHustle.Dev
{
    /// <summary>
    /// DEBUG-only, env-driven regression check for the mod-policy profile machinery - no UI clicks needed, so
    /// the MCP dev loop can drive it headlessly. On the Menu scene:
    ///  - SIDEHUSTLE_SELFTEST_POLICY=dry (normal session): resolve a restricted plan (allowed: TightBeam) for a
    ///    synthetic stub descriptor and run the REAL ApplyPolicyAndRestart path in dry-run mode - the session
    ///    profile and its cloned UserData (with the injected tokens) land on disk, the relaunch is skipped and
    ///    the artifacts can be inspected. The test rig then relaunches with --melonloader.basedir itself.
    ///  - any value (profile session): log what PendingContinue the clone carried, proving the token round-trip.
    /// </summary>
    internal static class SelfTest
    {
        private const string EnvVar = "SIDEHUSTLE_SELFTEST_POLICY";
        private const string TsEnvVar = "SIDEHUSTLE_SELFTEST_TS";
        private static bool _ran, _tsRan;

        internal static void TickMenu(bool policySession)
        {
            TickThunderstore();
            TickUi();
            RunPolicy(policySession);
        }

        // Pumped from Core.OnUpdate (in-world too): drive the Messenger backend without the phone UI. Once in a
        // lobby it sends a group message every few seconds and logs the stored group thread + contacts, proving
        // ChatService + ChatStore + Contacts + transport integrate. Enabled via SIDEHUSTLE_SELFTEST_CHATSVC=1.
        private static bool _chatInit;
        private static bool _chatOn;
        private static float _chatNext;
        private static int _chatSeq;
        internal static void TickChatService()
        {
            if (!_chatInit) { _chatInit = true; _chatOn = Environment.GetEnvironmentVariable("SIDEHUSTLE_SELFTEST_CHATSVC") == "1"; }
            if (!_chatOn || !Messenger.ChatService.InLobby) return;
            if (UnityEngine.Time.unscaledTime < _chatNext) return;
            _chatNext = UnityEngine.Time.unscaledTime + 5f;

            Messenger.ChatService.Send(0UL, "hi #" + _chatSeq++ + " from " + Messenger.ChatTransport.SelfId());
            var thread = Messenger.ChatStore.Thread(0UL);
            int mine = 0, theirs = 0;
            foreach (var m in thread) { if (m.Mine) mine++; else theirs++; }
            string contacts = "";
            foreach (var c in Messenger.Contacts.All) contacts += c.Name + "(" + c.SteamId + ") ";
            Core.Log?.Msg($"[selftest] chatsvc: group thread mine={mine} theirs={theirs} contacts=[{contacts.Trim()}]");
        }

        private static void RunPolicy(bool policySession)
        {
            if (_ran) return;
            string mode = Environment.GetEnvironmentVariable(EnvVar);
            if (string.IsNullOrEmpty(mode)) return;
            _ran = true;

            if (policySession)
            {
                Core.Log?.Msg($"[selftest] policy session active; PendingContinue='{Preferences.PendingContinue}' " +
                              $"ActiveAltBase='{Preferences.ActiveAltBase}' ActiveGamemodeId='{Preferences.ActiveGamemodeId}'");
                return;
            }

            var desc = new GamemodeDescriptor
            {
                Id = "sidehustle.selftest",
                DisplayName = "Policy SelfTest",
                Support = GamemodeSupport.Singleplayer,
                Policy = new ModPolicy { AllowedMods = new[] { "TightBeam" } },
            };
            var plan = ModPolicyResolver.Resolve(desc);
            Core.Log?.Msg($"[selftest] plan: keep={plan.KeepFiles.Count} disable={plan.ToDisable.Count} " +
                          $"enable={plan.ToEnable.Count} missing={plan.MissingRequired.Count} blocked={plan.Blocked}");
            if (plan.Blocked) { Core.Log?.Error("[selftest] plan blocked; aborting."); return; }

            ModSwitcher.DryRunForTests = string.Equals(mode, "dry", StringComparison.OrdinalIgnoreCase);
            ModSwitcher.ApplyPolicyAndRestart(desc, plan);
        }

        // Step through every menu-scene view in one launch (no reload needed between menu screens), pausing on
        // each so the MCP loop can screenshot after the per-screen log marker.
        private static System.Collections.IEnumerator MenuTour()
        {
            var steps = new (string Name, Action Open)[]
            {
                ("gamemodelist", () => Menu.Hub.OpenScreen()),
                ("profiles-list", () => Menu.Hub.OpenProfilesForTest()),
                ("profile-detail", () => Menu.Hub.OpenFirstProfileDetailForTest()),
                ("thunderstore-browser", () => Menu.Hub.OpenBrowserForTest()),
                ("updates", () => Menu.Hub.OpenUpdatesForTest()),
                ("add-installed", () => Menu.Hub.OpenAddBaseForTest()),
                ("vanilla-choice", () => Menu.Hub.OpenVanillaBrowserForTest()),
                ("vanilla-hostform", () => Menu.Hub.OpenVanillaHostFormForTest()),
                ("sync-consent", () => Menu.Hub.OpenConsentForTest()),
                ("manual-install", () => Menu.Hub.OpenManualForTest()),
                ("continue-interstitial", () => Menu.ContinueInterstitial.ShowForTest()),
            };
            string signal = null;
            try { signal = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "SideHustle", "sh_tour_next"); } catch { }
            try { if (signal != null && System.IO.File.Exists(signal)) System.IO.File.Delete(signal); } catch { }

            foreach (var s in steps)
            {
                try { s.Open(); } catch (Exception e) { Core.Log?.Warning($"[selftest] tour: '{s.Name}' failed: {e.Message}"); }
                yield return new UnityEngine.WaitForSeconds(1.5f);
                Core.Log?.Msg($"[selftest] tour: SHOWING '{s.Name}' (touch sh_tour_next to advance)");
                // Wait for the MCP loop to screenshot + drop the signal file (fallback: auto-advance after 90s).
                float waited = 0f;
                while (waited < 90f)
                {
                    if (signal != null && System.IO.File.Exists(signal)) { try { System.IO.File.Delete(signal); } catch { } break; }
                    yield return new UnityEngine.WaitForSeconds(0.5f);
                    waited += 0.5f;
                }
            }
            Core.Log?.Msg("[selftest] tour: DONE");
        }

        // Boot a throwaway world so the phone (and the Messenger app) exists, seed sample chat data, then open
        // the app on the contact list or a thread for a screenshot.
        private static System.Collections.IEnumerator MessengerScreenshot(bool thread)
        {
            Core.Log?.Msg("[selftest] messenger: booting a scratch world for the phone...");
            if (!Multiplayer.WorldBoot.BootHostWorld("Messenger Demo")) { Core.Log?.Error("[selftest] messenger: world boot failed."); yield break; }
            float waited = 0f;
            while (!Multiplayer.WorldBoot.IsWorldReady() && waited < 120f) { yield return new UnityEngine.WaitForSeconds(2f); waited += 2f; }
            if (!Multiplayer.WorldBoot.IsWorldReady()) { Core.Log?.Error("[selftest] messenger: world not ready."); yield break; }

            yield return new UnityEngine.WaitForSeconds(3f);
            const ulong peer = 76561199485712034UL;
            Messenger.Contacts.SeedForTest((peer, "Sam"), (76561190000000001UL, "Riley"));
            Messenger.ChatStore.SeedForTest(peer);

            var app = Messenger.MessengerApp.Instance;
            if (app == null) { Core.Log?.Error("[selftest] messenger: app instance not found (phone not up?)."); yield break; }

            string signal = null;
            try { signal = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "SideHustle", "sh_tour_next"); } catch { }
            try { if (signal != null && System.IO.File.Exists(signal)) System.IO.File.Delete(signal); } catch { }

            app.OpenListForTest();
            Core.Log?.Msg("[selftest] messenger: SHOWING 'messenger-list' (touch sh_tour_next to advance)");
            yield return WaitForSignal(signal);

            app.OpenThreadForTest(0UL);
            Core.Log?.Msg("[selftest] messenger: SHOWING 'messenger-thread' (touch sh_tour_next to advance)");
            yield return WaitForSignal(signal);
            Core.Log?.Msg("[selftest] messenger: DONE");
        }

        private static System.Collections.IEnumerator WaitForSignal(string signal)
        {
            float waited = 0f;
            while (waited < 90f)
            {
                if (signal != null && System.IO.File.Exists(signal)) { try { System.IO.File.Delete(signal); } catch { } yield break; }
                yield return new UnityEngine.WaitForSeconds(0.5f);
                waited += 0.5f;
            }
        }

        /// <summary>SIDEHUSTLE_SELFTEST_UI=profiles: auto-open the hub on the profiles list once the menu has
        /// settled, so the MCP dev loop can screenshot the new views without clicking.</summary>
        private static bool _uiRan;
        private static void TickUi()
        {
            if (_uiRan) return;
            string mode = Environment.GetEnvironmentVariable("SIDEHUSTLE_SELFTEST_UI");
            _uiRan = true;
            if (Environment.GetEnvironmentVariable("SIDEHUSTLE_SELFTEST_DRYRUN") == "1")
            {
                ModSwitcher.DryRunForTests = true;   // build profiles, skip the relaunch (rig drives it itself)
                Core.Log?.Msg("[selftest] dry-run relaunches enabled.");
            }
            if (string.IsNullOrEmpty(mode)) return;
            MelonLoader.MelonCoroutines.Start(OpenUiSoon(mode));
        }

        private static System.Collections.IEnumerator OpenUiSoon(string mode)
        {
            yield return new UnityEngine.WaitForSeconds(4f);
            Core.Log?.Msg($"[selftest] ui: opening '{mode}'.");
            if (mode == "detail") Menu.Hub.OpenFirstProfileDetailForTest();
            else if (mode.StartsWith("install:", StringComparison.Ordinal)) Menu.Hub.InstallForTest(mode.Substring(8));
            else if (mode == "browser") Menu.Hub.OpenBrowserForTest();
            else if (mode.StartsWith("browsersort:", StringComparison.Ordinal))
            {
                if (int.TryParse(mode.Substring(12), out var sm)) Menu.PackageBrowserView.SetSortForTest(sm);
                Menu.Hub.OpenBrowserForTest();
            }
            else if (mode.StartsWith("removedlg:", StringComparison.Ordinal)) Menu.Hub.OpenRemoveDialogForTest(mode.Substring(10));
            else if (mode.StartsWith("removeall:", StringComparison.Ordinal)) Menu.Hub.RemoveAllForTest(mode.Substring(10));
            else if (mode.StartsWith("remove:", StringComparison.Ordinal)) Menu.Hub.RemoveForTest(mode.Substring(7));
            else if (mode.StartsWith("installcancel:", StringComparison.Ordinal)) Menu.Hub.InstallCancelForTest(mode.Substring(14));
            else if (mode == "runtimecheck") RuntimeCheck();
            else if (mode == "runtimedialog") Menu.Hub.ShowRuntimeNoticeForTest();
            else if (mode == "updates") Menu.Hub.OpenUpdatesForTest();
            else if (mode == "addbase") Menu.Hub.OpenAddBaseForTest();
            else if (mode == "consent") Menu.Hub.OpenConsentForTest();
            else if (mode == "manual") Menu.Hub.OpenManualForTest();
            else if (mode == "ghdl") GhDownloadCheck();
            else if (mode == "interstitial") Menu.ContinueInterstitial.ShowForTest();
            else if (mode == "messenger" || mode == "messengerthread") MelonLoader.MelonCoroutines.Start(MessengerScreenshot(mode == "messengerthread"));
            else if (mode == "switchbuild")
            {
                // Build the FIRST profile's isolated base dir via the real switch path but stop before the relaunch,
                // so the rig can inspect Mods/Plugins/UserLibs on disk and drive the --melonloader.basedir launch.
                Mods.ModSwitcher.DryRunForTests = true;
                var doc = Profiles.ProfileEngine.LoadStore(out _);
                if (doc.Profiles.Count > 0) Profiles.ProfileEngine.SwitchTo(doc.Profiles[0].Id);
                else Core.Log?.Error("[selftest] switchbuild: no profile to build.");
            }
            else if (mode == "vanillabrowse") Menu.Hub.OpenVanillaBrowserForTest();
            else if (mode == "vanillahostform") Menu.Hub.OpenVanillaHostFormForTest();
            else if (mode == "vanillajoin") Menu.Hub.JoinVanillaForTest();
            else if (mode == "vanillahost")
            {
                Menu.Hub.HostVanillaForTest();
                float waited = 0f;
                while (!Sync.SyncCoordinator.IsInSession && waited < 150f)
                {
                    yield return new UnityEngine.WaitForSeconds(2f);
                    waited += 2f;
                }
                if (Sync.SyncCoordinator.IsInSession) Menu.Hub.VerifyOwnVanillaLobbyForTest();
                else Core.Log?.Error("[selftest] vanillahost: session did not go live within 150s.");
            }
            else Menu.Hub.OpenProfilesForTest();
        }

        /// <summary>SIDEHUSTLE_SELFTEST_UI=ghdl: run the real GitHub-releases download path in-game against a
        /// live repo and log the outcome. Target via SIDEHUSTLE_SELFTEST_GH="owner/repo|version|sha256"
        /// (default: the released LooseEnds 1.1.0).</summary>
        private static void GhDownloadCheck()
        {
            string spec = Environment.GetEnvironmentVariable("SIDEHUSTLE_SELFTEST_GH")
                          ?? "DooDesch/ScheduleOne-LooseEnds|1.1.0|4b433ea1009bd4ab5d722c8b2cc66e9a8f382d75227df85510de779936b91bbf";
            var parts = spec.Split('|');
            if (parts.Length != 3) { Core.Log?.Error("[selftest] ghdl: bad SIDEHUSTLE_SELFTEST_GH spec."); return; }
            var mod = new Sync.ManifestMod
            {
                File = parts[0].Split('/').Last() + ".dll",
                Name = parts[0],
                Version = parts[1],
                Sha256 = parts[2],
                Source = "nx:https://github.com/" + parts[0],
            };
            var diff = new Sync.SyncDiff();
            diff.Entries.Add(new Sync.DiffEntry { Mod = mod, Status = Sync.DiffStatus.Download });
            System.Threading.Tasks.Task.Run(async () =>
            {
                bool ok = false;
                try { ok = await Sync.SyncResolver.DownloadMissingAsync(diff, null, System.Threading.CancellationToken.None); }
                catch (Exception e) { Core.Log?.Error("[selftest] ghdl failed: " + e.Message); }
                var e0 = diff.Entries[0];
                Core.Log?.Msg($"[selftest] ghdl: ok={ok} status={e0.Status} path={e0.SourcePath ?? "(none)"}");
            });
        }

        /// <summary>Classify every mod DLL in the real Mods folder and log a table (read-only) - the offline
        /// false-positive sweep for the runtime classifier.</summary>
        private static void RuntimeCheck()
        {
            try
            {
                string mods = System.IO.Path.Combine(Profiles.ProfileEngine.GameRoot ?? ".", "Mods");
                var files = System.IO.Directory.GetFiles(mods, "*.dll", System.IO.SearchOption.TopDirectoryOnly)
                    .Concat(System.IO.Directory.GetFiles(mods, "*.dll.disabled", System.IO.SearchOption.TopDirectoryOnly))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                Core.Log?.Msg($"[selftest] runtimecheck: {files.Count} file(s) in {mods}");
                var sw = new System.Diagnostics.Stopwatch();
                foreach (var f in files)
                {
                    string name = System.IO.Path.GetFileName(f);
                    var byName = Shared.RuntimeClassifier.FromNameTokens(name);
                    sw.Restart();
                    var r = Shared.RuntimeClassifier.ClassifyFile(f);
                    sw.Stop();
                    Core.Log?.Msg($"[selftest] runtimecheck: {name,-42} {Shared.RuntimeClassifier.ToTag(r),-9} name-token={Shared.RuntimeClassifier.ToTag(byName),-9} {sw.ElapsedMilliseconds}ms");
                }
            }
            catch (Exception e) { Core.Log?.Error("[selftest] runtimecheck failed: " + e); }
        }

        /// <summary>SIDEHUSTLE_SELFTEST_TS=Owner-Name: run the live Thunderstore index+download path INSIDE the
        /// game. Proves (a) the game's managed runtime negotiates TLS with the download CDN (the plain net6
        /// runtime is rejected there - the harness had to move to net9) and (b) HttpClient+zip on Task.Run works
        /// under Il2CppInterop. Logs the runtime version and every step.</summary>
        private static void TickThunderstore()
        {
            if (_tsRan) return;
            string fullName = Environment.GetEnvironmentVariable(TsEnvVar);
            if (string.IsNullOrEmpty(fullName)) return;
            _tsRan = true;

            Core.Log?.Msg($"[selftest] ts: runtime={Environment.Version}, fetching index...");
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var index = await Profiles.ThunderstoreClient.GetIndexAsync(
                        Mods.ModInventory.GameRoot(), false, System.Threading.CancellationToken.None);
                    Core.Log?.Msg($"[selftest] ts: index={(index == null ? "NULL" : index.Packages.Count + " packages")}");
                    var pkg = index?.Find(fullName);
                    if (pkg == null) { Core.Log?.Error($"[selftest] ts: package '{fullName}' not found."); return; }
                    string dir = await Profiles.ThunderstoreClient.EnsurePackageAsync(
                        Mods.ModInventory.GameRoot(), index, fullName, pkg.Latest.VersionNumber, null,
                        System.Threading.CancellationToken.None);
                    var mf = dir != null ? Profiles.PackageCache.ReadManifest(dir) : null;
                    Core.Log?.Msg($"[selftest] ts: download={(dir ?? "FAILED")} mods=[{(mf == null ? "" : string.Join(", ", mf.Mods))}]");
                }
                catch (Exception e) { Core.Log?.Error("[selftest] ts failed: " + e); }
            });
        }
    }
}
#endif
