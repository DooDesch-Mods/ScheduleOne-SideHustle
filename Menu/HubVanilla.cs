using System;
using System.Collections.Generic;
using System.Linq;
using SideHustle.Multiplayer;
using SideHustle.Profiles;
using SideHustle.Sync;
using UnityEngine;

namespace SideHustle.Menu
{
    /// <summary>
    /// The "Vanilla Co-op" screens (partial Hub): host your own real savegame as a PUBLIC lobby with a mod
    /// manifest, or browse published vanilla lobbies. Entered via a pinned row at the top of the gamemode list -
    /// unlike Mod Profiles this IS a session type, so it belongs with the gamemodes.
    /// </summary>
    internal static partial class Hub
    {
#if DEBUG
        /// <summary>Dev.SelfTest only: host the first save publicly with defaults - the full host path
        /// (plan -> lobby -> tag -> world) without UI clicks, for the MCP dev loop.</summary>
        internal static void HostVanillaForTest()
        {
            Il2CppScheduleOne.Persistence.SaveInfo save = null;
            try
            {
                var saves = Il2CppScheduleOne.Persistence.LoadManager.SaveGames;
                if (saves != null)
                    for (int i = 0; i < saves.Length && save == null; i++)
                    {
                        try { if (saves[i] != null && !string.IsNullOrEmpty(saves[i].OrganisationName)) save = saves[i]; }
                        catch { /* next */ }
                    }
            }
            catch (Exception e) { Core.Log?.Error("[selftest] vanillahost: save enumeration failed: " + e.Message); }
            if (save == null) { Core.Log?.Error("[selftest] vanillahost: no save found."); return; }

            System.Threading.Tasks.Task.Run(async () =>
            {
                PublishPlan plan = null;
                try
                {
                    var index = await ThunderstoreClient.GetIndexAsync(ProfileEngine.GameRoot, false, System.Threading.CancellationToken.None);
                    plan = SyncPublisher.BuildPlan(index);
                }
                catch (Exception e) { Core.Log?.Error("[selftest] vanillahost: plan failed: " + e.Message); }
                MainThread.Post(() =>
                {
                    if (plan == null) return;
                    Core.Log?.Msg($"[selftest] vanillahost: plan auto={plan.AutoCount} link={plan.LinkCount} dropped={plan.DroppedCount}");
                    SyncCoordinator.StartHostVanilla(save, new HostOptions { MaxPlayers = 4 },
                        plan.Manifest.ToCanonicalText(), "", $"{plan.AutoCount}/{plan.LinkCount}/{plan.DroppedCount}", false);
                });
            });
        }

        /// <summary>Dev.SelfTest only: open the host FORM for the first save (async plan -> form) to screenshot it.</summary>
        internal static void OpenVanillaHostFormForTest()
        {
            EnsureInit();
            EnsureClone();
            if (_cloneScreen == null) return;
            if (!_cloneScreen.IsOpen) { ShowVanillaChoice(); _cloneScreen.Open(closePrevious: true); }
            Il2CppScheduleOne.Persistence.SaveInfo save = null;
            try
            {
                var saves = Il2CppScheduleOne.Persistence.LoadManager.SaveGames;
                if (saves != null)
                    for (int i = 0; i < saves.Length && save == null; i++)
                        try { if (saves[i] != null && !string.IsNullOrEmpty(saves[i].OrganisationName)) save = saves[i]; } catch { }
            }
            catch { }
            if (save != null) ShowVanillaHostForm(save);
        }

        /// <summary>Dev.SelfTest only: a synthetic manifest + diff covering every status, for screenshotting the
        /// consent and manual-install views without a live host.</summary>
        private static (VanillaLobbyRow Row, SyncManifest Manifest, SyncDiff Diff, string MHash, string Prefs) SyntheticSync()
        {
            var manifest = new SyncManifest { GameVersion = "0.4.5f2", MelonLoaderVersion = "0.7.3", SideHustleVersion = "2.0.0" };
            var diff = new SyncDiff();
            void Add(string file, string name, string ver, DiffStatus status, string source, bool hashWarn = false)
            {
                var mod = new ManifestMod { File = file, Name = name, Version = ver, Sha256 = "deadbeef", Source = source };
                manifest.Mods.Add(mod);
                diff.Entries.Add(new DiffEntry { Mod = mod, Status = status, HashWarn = hashWarn });
            }
            Add("Siesta.dll", "Siesta", "1.2.0", DiffStatus.Present, "ts:DooDesch-Siesta-1.2.0");
            Add("Litterally.dll", "Litterally", "1.0.0", DiffStatus.Download, "ts:DooDesch-Litterally-1.0.0");
            Add("Backrooms.dll", "Backrooms", "2.0.0", DiffStatus.Cached, "ts:DooDesch-Backrooms-2.0.0", hashWarn: true);
            Add("SecretMod.dll", "Nexus Only Mod", "3.1.0", DiffStatus.Manual, "nx:https://www.nexusmods.com/schedule1/mods/123");
            Add("HomeBrew.dll", "Home-brewed Mod", "0.0.1", DiffStatus.Dropped, "");
            diff.LocalOnly.Add("Your Private HUD");
            diff.LocalOnly.Add("Cheat Menu");
            var row = new VanillaLobbyRow
            {
                LobbyId = 1234567, LobbyName = "Sam's modded run", HostName = "Sam", Org = "Kings of Cul-de-Sac",
                Enforced = true, MHash = "1a2b3c4d5e6f7a8b", Members = 2, MaxPlayers = 4, OwnerSteamId = 76561190000000000UL,
            };
            return (row, manifest, diff, "1a2b3c4d5e6f7a8b", "[SomeMod_01_Main]\nDifficulty = \"hard\"\nSpawnRate = 2.5\n");
        }

        /// <summary>Dev.SelfTest only: open the Sync consent screen (synthetic diff) for a screenshot.</summary>
        internal static void OpenConsentForTest()
        {
            EnsureInit();
            EnsureClone();
            if (_cloneScreen == null) return;
            if (!_cloneScreen.IsOpen) { ShowVanillaChoice(); _cloneScreen.Open(closePrevious: true); }
            var s = SyntheticSync();
            ShowVanillaConsent(s.Row, s.Manifest, s.Diff, s.MHash, s.Prefs);
        }

        /// <summary>Dev.SelfTest only: open the manual-install checklist (synthetic diff) for a screenshot.</summary>
        internal static void OpenManualForTest()
        {
            EnsureInit();
            EnsureClone();
            if (_clone == null) return;
            if (!_cloneScreen.IsOpen) { ShowVanillaChoice(); _cloneScreen.Open(closePrevious: true); }
            var s = SyntheticSync();
            ClearFormHost();
            SetTmp(_clone.transform, "Title", "Manual installs");
            var host = CreateFormHost("SH_ManualInstall", 560f);
            SyncManualInstallView.Build(host, s.Diff, onContinue: ShowVanillaChoice, onBack: ShowVanillaChoice);
        }

        /// <summary>Dev.SelfTest only: open the hub straight on the vanilla lobby browser for a screenshot.</summary>
        internal static void OpenVanillaBrowserForTest()
        {
            EnsureInit();
            EnsureClone();
            if (_cloneScreen == null) return;
            if (!_cloneScreen.IsOpen) { ShowGamemodeList(); _cloneScreen.Open(closePrevious: true); }
            ShowVanillaChoice();
        }

        /// <summary>Dev.SelfTest only: find the first published vanilla lobby, read its manifest, diff and run
        /// the REAL sync-and-join path with auto-consent - the headless client half of the 2-instance rig test
        /// (paired with SIDEHUSTLE_SELFTEST_DRYRUN so the rig controls the relaunch itself).</summary>
        internal static void JoinVanillaForTest()
        {
            ServerBrowser.BeginQueryVanilla(rows => MainThread.Post(() =>
            {
                var row = rows?.FirstOrDefault(r => !string.IsNullOrEmpty(r.MHash));
                if (row == null) { Core.Log?.Error("[selftest] vanillajoin: no published lobby found."); return; }
                if (!VanillaLobby.TryReadPayloads(row.LobbyId, out var manifest, out _, out var mhash))
                { Core.Log?.Error("[selftest] vanillajoin: payloads unreadable."); return; }
                Core.Log?.Msg($"[selftest] vanillajoin: lobby={row.LobbyId} mods={manifest.Mods.Count} mhash={mhash}");
                System.Threading.Tasks.Task.Run(() =>
                {
                    SyncDiff diff = null;
                    try { diff = SyncResolver.Compute(manifest); }
                    catch (Exception e) { Core.Log?.Error("[selftest] vanillajoin: diff failed: " + e.Message); }
                    MainThread.Post(() =>
                    {
                        if (diff == null) return;
                        Core.Log?.Msg($"[selftest] vanillajoin: diff present={diff.Count(DiffStatus.Present)} " +
                                      $"cached={diff.Count(DiffStatus.Cached)} download={diff.Count(DiffStatus.Download)} " +
                                      $"manual={diff.Count(DiffStatus.Manual)} dropped={diff.Count(DiffStatus.Dropped)} localOnly={diff.LocalOnly.Count}");
                        StartSyncAndJoin(row, manifest, diff, mhash);
                    });
                });
            }));
        }

        /// <summary>Dev.SelfTest only: once the session is live, read our OWN lobby's published data back.</summary>
        internal static void VerifyOwnVanillaLobbyForTest()
        {
            ulong id = LobbyCoordinator.CurrentLobbyId;
            var summary = VanillaLobby.ReadSummary(id);
            bool readable = VanillaLobby.TryReadPayloads(id, out var manifest, out var prefs, out var mhash);
            Core.Log?.Msg($"[selftest] vanillahost: lobby={id} org='{summary.Org}' summary='{summary.ModSummary}' " +
                          $"payloadReadable={readable} mods={manifest?.Mods.Count ?? -1} mhash={mhash} prefsLen={prefs?.Length ?? -1}");
        }
#endif

        /// <summary>Canvas root for a modal dialog, usable even when the hub screen is not the active view (the
        /// continue interstitial fires from the vanilla Continue screen). Falls back to any menu canvas.</summary>
        internal static UnityEngine.Transform DialogRootStatic()
        {
            var r = DialogRoot();
            if (r != null) return r;
            try
            {
                var canvases = UnityEngine.Object.FindObjectsOfType<UnityEngine.Canvas>(false);
                UnityEngine.Canvas top = null;
                if (canvases != null)
                    for (int i = 0; i < canvases.Length; i++)
                        if (canvases[i] != null && canvases[i].isActiveAndEnabled &&
                            (top == null || canvases[i].sortingOrder >= top.sortingOrder)) top = canvases[i];
                return top != null ? top.transform : null;
            }
            catch { return null; }
        }

        /// <summary>Host an arbitrary save publicly (used by the Continue interstitial): open the host form for it,
        /// building the clone if the hub screen is not up.</summary>
        internal static void HostVanillaSave(Il2CppScheduleOne.Persistence.SaveInfo save)
        {
            EnsureInit();
            EnsureClone();
            if (_cloneScreen == null) { Core.Log?.Warning("[sync] host screen unavailable."); return; }
            if (!_cloneScreen.IsOpen) { ShowVanillaChoice(); _cloneScreen.Open(closePrevious: true); }
            ShowVanillaHostForm(save);
        }

        private static Row BuildVanillaRow() => new Row
        {
            Name = "Vanilla Co-op",
            Subtitle = "Host your own save publicly or browse open vanilla lobbies - mods sync on join.",
            Corner = "Sync",
            OnClick = ShowVanillaChoice
        };

        private static void ShowVanillaChoice()
        {
            _mpDesc = null;
            _back = ShowGamemodeList;
            var rows = new List<Row>
            {
                new Row
                {
                    Name = "Host a save",
                    Subtitle = "Load one of your savegames and open it as a public lobby.",
                    OnClick = ShowVanillaSavePicker
                },
                new Row
                {
                    Name = "Browse lobbies",
                    Subtitle = "Public vanilla lobbies published by other Side Hustle hosts.",
                    OnClick = ShowVanillaBrowser
                },
                new Row { Name = "Back", Subtitle = "Back to the gamemode list.", OnClick = ShowGamemodeList }
            };
            ShowRows("Vanilla Co-op", rows);
        }

        private static void ShowVanillaSavePicker()
        {
            _back = ShowVanillaChoice;
            var rows = new List<Row>();
            try
            {
                var saves = Il2CppScheduleOne.Persistence.LoadManager.SaveGames;
                if (saves != null)
                {
                    for (int i = 0; i < saves.Length; i++)
                    {
                        var info = saves[i];
                        if (info == null) continue;
                        string org;
                        float worth = 0f;
                        int slot = i;
                        try { org = info.OrganisationName; } catch { org = null; }
                        try { worth = info.Networth; } catch { /* ignore */ }
                        if (string.IsNullOrEmpty(org)) continue;   // empty slot
                        var save = info;
                        rows.Add(new Row
                        {
                            Name = org,
                            Subtitle = $"Slot {slot + 1} - net worth ${worth:n0}",
                            Corner = "Save",
                            OnClick = () => ShowVanillaHostForm(save)
                        });
                    }
                }
            }
            catch (Exception e) { Core.Log?.Warning("[sync] save enumeration failed: " + e.Message); }

            if (rows.Count == 0)
                rows.Add(new Row { Name = "No saves found", Subtitle = "Start a normal game first - the host loads a real save.", Disabled = true });
            rows.Add(new Row { Name = "Back", Subtitle = "Back to Vanilla Co-op.", OnClick = ShowVanillaChoice });
            ShowRows("Host a save", rows);
        }

        private static void ShowVanillaHostForm(Il2CppScheduleOne.Persistence.SaveInfo save)
        {
            if (_clone == null) return;
            _back = ShowVanillaSavePicker;
            ClearFormHost();
            SetTmp(_clone.transform, "Title", "Host publicly");
            var host = CreateFormHost("SH_VanillaHost", 560f);

            // The mod list needs the Thunderstore index (source resolution) + a hash of every mod DLL - both off
            // the main thread; the form appears once it is ready.
            ProfilesViews.BuildInstalling(host, "Preparing your mod list...");
            var prefsCats = PrefsCatalog.Enumerate();
            System.Threading.Tasks.Task.Run(async () =>
            {
                PublishPlan plan = null;
                try
                {
                    var index = await ThunderstoreClient.GetIndexAsync(ProfileEngine.GameRoot, false, System.Threading.CancellationToken.None);
                    plan = SyncPublisher.BuildPlan(index);
                }
                catch (Exception e) { Core.Log?.Warning("[sync] publish plan failed: " + e.Message); }

                MainThread.Post(() =>
                {
                    if (_cloneScreen == null || !_cloneScreen.IsOpen) return;
                    ClearFormHost();
                    var h = CreateFormHost("SH_VanillaHost", 560f);
                    if (plan == null)
                    {
                        ProfilesViews.BuildInstalling(h, "Couldn't prepare your mod list. Go back and try again.", ShowVanillaSavePicker);
                        return;
                    }
                    VanillaHostView.Build(h, plan, prefsCats, LobbyCaps.MaxClients(),
                        onBack: ShowVanillaSavePicker,
                        onHost: (opts, enforce, syncedCats) =>
                        {
                            string prefsText = PrefsCatalog.BuildOverlay(syncedCats);
                            CloseHubScreen();
                            SyncCoordinator.StartHostVanilla(save, opts,
                                plan.Manifest.ToCanonicalText(), prefsText,
                                $"{plan.AutoCount}/{plan.LinkCount}/{plan.DroppedCount}", enforce);
                        });
                });
            });
        }

        private static void ShowVanillaBrowser()
        {
            _back = ShowVanillaChoice;
            ShowRows("Browsing...", new List<Row>
            {
                new Row { Name = "Searching for public vanilla lobbies...", Subtitle = "Steam lobby query in flight.", Disabled = true }
            });
            ServerBrowser.BeginQueryVanilla(rows => MainThread.Post(() => ShowVanillaBrowserResults(rows)));
        }

        private static void ShowVanillaBrowserResults(List<VanillaLobbyRow> lobbies)
        {
            if (_cloneScreen == null || !_cloneScreen.IsOpen) return;
            _back = ShowVanillaChoice;
            var rows = new List<Row>();
            foreach (var l in lobbies ?? new List<VanillaLobbyRow>())
            {
                var row = l;
                string counts = row.ModSummary;   // "auto/manual/dropped" as published
                string sub = $"{row.Members}/{Math.Max(row.Members, row.MaxPlayers)} players - save '{row.Org}'"
                             + (string.IsNullOrEmpty(counts) ? "" : $" - mods {counts}")
                             + (row.Enforced ? " - synced clients only" : "");
                rows.Add(new Row
                {
                    Name = string.IsNullOrEmpty(row.LobbyName) ? row.HostName : row.LobbyName,
                    Subtitle = sub,
                    Corner = row.HasPassword ? "Locked" : "Open",
                    OnClick = () => StartVanillaJoin(row)
                });
            }
            if (rows.Count == 0)
                rows.Add(new Row { Name = "No public vanilla lobbies right now", Subtitle = "Hosts publish theirs via Vanilla Co-op -> Host a save.", Disabled = true });
            rows.Add(new Row { Name = "Back", Subtitle = "Back to Vanilla Co-op.", OnClick = ShowVanillaChoice });
            ShowRows("Vanilla lobbies", rows);
        }

        private static void StartVanillaJoin(VanillaLobbyRow row)
        {
            // Password gate first (client-side hash compare, the casual gate the gamemode browser also uses).
            if (row.HasPassword && !string.IsNullOrEmpty(row.PwHash))
            {
                var root = DialogRoot();
                if (root != null)
                {
                    DooDesch.UI.Components.PromptDialog(root, "Password required",
                        $"Enter the password for {(string.IsNullOrEmpty(row.HostName) ? "this" : row.HostName + "'s")} lobby.",
                        "password", "Continue",
                        entered => string.Equals(LobbyCoordinator.HashPassword(entered ?? ""), row.PwHash, StringComparison.Ordinal)
                                   ? VanillaJoinAccepted(row)
                                   : "Incorrect password.");
                    return;
                }
            }
            ShowVanillaSyncCheck(row);
        }

        private static string VanillaJoinAccepted(VanillaLobbyRow row) { ShowVanillaSyncCheck(row); return null; }

        /// <summary>
        /// The pre-join sync check: read the host's manifest, diff it against the local install + cache (on a
        /// worker - it hashes every installed mod) and show the consent screen. Entering the lobby immediately
        /// triggers the vanilla world pull, so EVERYTHING (consent, downloads, restart) happens before JoinLobby.
        /// Trust-on-rejoin: a known host with an unchanged manifest and nothing left to fetch skips the screen.
        /// </summary>
        private static void ShowVanillaSyncCheck(VanillaLobbyRow row)
        {
            _back = ShowVanillaBrowser;

            if (!VanillaLobby.TryReadPayloads(row.LobbyId, out var manifest, out var hostPrefs, out var mhash))
            {
                ShowUnsyncableJoin(row, "Couldn't read the host's mod list - nothing can be compared or synced.");
                return;
            }

            ShowRows("Comparing mods...", new List<Row>
            {
                new Row { Name = "Comparing the host's mods with yours...", Subtitle = "Hashing your installed mods.", Disabled = true }
            });

            System.Threading.Tasks.Task.Run(() =>
            {
                SyncDiff diff = null;
                try { diff = SyncResolver.Compute(manifest); }
                catch (Exception e) { Core.Log?.Warning("[sync] diff failed: " + e.Message); }
                MainThread.Post(() =>
                {
                    if (_cloneScreen == null || !_cloneScreen.IsOpen) return;
                    if (diff == null) { ShowUnsyncableJoin(row, "Comparing failed (see log)."); return; }

                    bool nothingToFetch = diff.Count(DiffStatus.Download) == 0 && diff.Count(DiffStatus.Manual) == 0;
                    if (!diff.NeedsRestart && string.IsNullOrEmpty(hostPrefs))
                    {
                        // Everything already matches byte-for-byte and there is nothing to overlay: join in place.
                        CloseHubScreen();
                        LobbyCoordinator.JoinLobby(row.LobbyId);
                        return;
                    }
                    if (nothingToFetch && TrustStore.IsTrusted(row.OwnerSteamId, mhash))
                    {
                        StartSyncAndJoin(row, manifest, diff, mhash, hostPrefs);   // consent already given for THIS manifest
                        return;
                    }
                    ShowVanillaConsent(row, manifest, diff, mhash, hostPrefs);
                });
            });
        }

        private static void ShowVanillaConsent(VanillaLobbyRow row, SyncManifest manifest, SyncDiff diff, string mhash, string hostPrefs)
        {
            if (_clone == null) return;
            _back = ShowVanillaBrowser;
            ClearFormHost();
            SetTmp(_clone.transform, "Title", string.IsNullOrEmpty(row.LobbyName) ? "Sync check" : row.LobbyName);
            var host = CreateFormHost("SH_SyncConsent", 560f);
            SyncConsentView.Build(host, manifest, diff, row.Enforced, hasPrefs: !string.IsNullOrEmpty(hostPrefs),
                onSyncJoin: () => StartSyncAndJoin(row, manifest, diff, mhash, hostPrefs),
                onPlainJoin: () => { CloseHubScreen(); LobbyCoordinator.JoinLobby(row.LobbyId); },
                onBack: ShowVanillaBrowser);
        }

        private static void ShowUnsyncableJoin(VanillaLobbyRow row, string why)
        {
            _back = ShowVanillaBrowser;
            var rows = new List<Row>
            {
                new Row { Name = "Sync unavailable", Subtitle = why, Disabled = true },
                new Row
                {
                    Name = "Join without syncing",
                    Subtitle = row.Enforced
                        ? "This host kicks unsynced clients - joining anyway will likely bounce you."
                        : "Join with your current mods (gameplay may desync when mods differ).",
                    Corner = "Join",
                    OnClick = () => { CloseHubScreen(); LobbyCoordinator.JoinLobby(row.LobbyId); }
                },
                new Row { Name = "Back", Subtitle = "Back to the lobby list.", OnClick = ShowVanillaBrowser }
            };
            ShowRows(string.IsNullOrEmpty(row.LobbyName) ? "Vanilla lobby" : row.LobbyName, rows);
        }

        /// <summary>Consent given: download what's missing, build the session profile from the EXACT resolved
        /// bytes, remember the trust, and restart with the rejoin token. Manual (nx:) mods are not fetchable
        /// here yet - they ride as absent this round (their checklist is the next build step).</summary>
        private static void StartSyncAndJoin(VanillaLobbyRow row, SyncManifest manifest, SyncDiff diff, string mhash, string hostPrefs = null)
        {
            // Manual (nx:) mods first: if any need fetching by hand, show the checklist and only build+restart
            // once the player continues (resolved manual files land in the cache the resolver reads). The dev
            // test path has no open screen, so it skips straight to the build.
            if (diff.Entries.Any(e => e.Status == DiffStatus.Manual) && _clone != null && _cloneScreen != null && _cloneScreen.IsOpen)
            {
                _back = () => ShowVanillaConsent(row, manifest, diff, mhash, hostPrefs);
                ClearFormHost();
                SetTmp(_clone.transform, "Title", "Manual installs");
                var mh = CreateFormHost("SH_ManualInstall", 560f);
                SyncManualInstallView.Build(mh,
                    diff,
                    onContinue: () => BuildAndRestart(row, diff, mhash, hostPrefs),
                    onBack: () => ShowVanillaConsent(row, manifest, diff, mhash, hostPrefs));
                return;
            }
            BuildAndRestart(row, diff, mhash, hostPrefs);
        }

        private static void BuildAndRestart(VanillaLobbyRow row, SyncDiff diff, string mhash, string hostPrefs)
        {
            // The UI update is optional (the dev-loop test drives this without an open hub); the download +
            // build + restart below never depend on _clone.
            if (_clone != null && _cloneScreen != null && _cloneScreen.IsOpen)
            {
                _back = null;
                ClearFormHost();
                SetTmp(_clone.transform, "Title", "Syncing...");
                var host = CreateFormHost("SH_Syncing", 560f);
                ProfilesViews.BuildInstalling(host, "Syncing the host's mods - the game restarts and rejoins on its own...");
            }

            System.Threading.Tasks.Task.Run(async () =>
            {
                bool downloadsOk = false;
                try
                {
                    downloadsOk = await SyncResolver.DownloadMissingAsync(diff, null, System.Threading.CancellationToken.None);
                }
                catch (Exception e) { Core.Log?.Warning("[sync] downloads failed: " + e.Message); }

                var inputs = SyncResolver.ToInputs(diff);
                MainThread.Post(() =>
                {
                    if (!downloadsOk)
                        Core.Log?.Warning("[sync] not every mod could be fetched - the session runs without the missing ones.");
                    if (inputs.Count == 0)
                    {
                        Core.Log?.Error("[sync] nothing resolvable to build a profile from; not restarting.");
                        ShowVanillaBrowser();
                        return;
                    }
                    TrustStore.Trust(row.OwnerSteamId, mhash, row.HostName);
                    var token = ConfigCodec.Encode(new[]
                    {
                        new KeyValuePair<string, string>("lobby", row.LobbyId.ToString()),
                        new KeyValuePair<string, string>("mhash", mhash ?? ""),
                    });
                    var tokens = new Dictionary<string, string>
                    {
                        ["PendingVanillaJoin"] = token,
                        ["PendingContinue"] = "",
                        ["PendingHostOptions"] = "",
                        ["ActiveGamemodeId"] = "",
                    };
                    // The host's synced MelonPreferences categories apply ONLY inside this session profile (its
                    // cloned cfg); the client's real settings never change.
                    Func<string, string> overlay = string.IsNullOrEmpty(hostPrefs)
                        ? null
                        : cfg => PrefsSync.ApplyOverlay(cfg, hostPrefs);
                    Mods.ModSwitcher.RelaunchIntoSyncProfile("sync-" + row.OwnerSteamId, inputs, tokens,
                        overlay, $"syncing {inputs.Count} mod(s) for '{row.LobbyName}'");
                });
            });
        }
    }
}
