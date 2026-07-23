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
                    Core.Log?.Msg($"[selftest] vanillahost: plan auto={plan.AutoCount} gh={plan.GhCount} link={plan.LinkCount} dropped={plan.DroppedCount}");
                    SyncCoordinator.StartHostVanilla(save, new HostOptions { MaxPlayers = 4 },
                        plan.Manifest.ToCanonicalText(), "", $"{plan.AutoCount + plan.GhCount}/{plan.LinkCount}/{plan.DroppedCount}", false);
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
            void Add(string file, string name, string ver, DiffStatus status, string source, bool hashWarn = false, string sha = "deadbeef")
            {
                var mod = new ManifestMod { File = file, Name = name, Version = ver, Sha256 = sha, Source = source };
                manifest.Mods.Add(mod);
                diff.Entries.Add(new DiffEntry { Mod = mod, Status = status, HashWarn = hashWarn });
            }
            Add("Siesta.dll", "Siesta", "1.2.0", DiffStatus.Present, "ts:DooDesch-Siesta-1.2.0");
            Add("Litterally.dll", "Litterally", "1.0.0", DiffStatus.Download, "ts:DooDesch-Litterally-1.0.0");
            Add("Backrooms.dll", "Backrooms", "2.0.0", DiffStatus.Cached, "ts:DooDesch-Backrooms-2.0.0", hashWarn: true);
            // The manual entries carry REAL hashes of tiny deterministic payloads so the folder watcher can be
            // exercised live: a file whose ASCII content is "SIDEHUSTLE SYNTHETIC PAYLOAD <stem>" resolves the row.
            Add("SecretMod.dll", "Nexus Only Mod", "3.1.0", DiffStatus.Manual, "nx:https://www.nexusmods.com/schedule1/mods/123",
                sha: "13d4dbd989bf57ab12d072d5a0918cd37cfa8508680a0a13ba53a2dce3c4082f");
            Add("HomeBrew.dll", "Home-brewed Mod", "0.0.1", DiffStatus.Dropped, "",
                sha: "bf89a3abb406ca4ab7bf38a76d7a150a70235092a28781d2cfbf515f737dde9d");
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
                    Name = "Join a lobby",
                    Subtitle = "Browse public vanilla lobbies other Side Hustle hosts are running.",
                    OnClick = ShowVanillaBrowser
                },
                new Row { Name = "Back", Subtitle = "Back to the gamemode list.", OnClick = ShowGamemodeList }
            };
            ShowRows("Vanilla Co-op", rows, aliasForGamemodeId: "vanilla");
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

            // A fresh start, mirroring the main menu's "New Game": create a real save and host it directly. Offered
            // whenever a save slot is free (also the only action when the player has no saves yet, so "Host a save"
            // is never a dead end); hidden when all five slots are full, since a new save could not be created anyway.
            if (FirstFreeSaveSlot() >= 0)
                rows.Insert(0, new Row
                {
                    Name = "New game",
                    Subtitle = "Create a fresh save and host it right away.",
                    Corner = "New",
                    OnClick = PromptNewGame
                });
            rows.Add(new Row { Name = "Back", Subtitle = "Back to Vanilla Co-op.", OnClick = ShowVanillaChoice });
            ShowRows("Host a save", rows);
        }

        // Name the organisation (like the vanilla New Game setup screen), then create + host a fresh save.
        private static void PromptNewGame()
        {
            var root = DialogRoot();
            if (root == null) return;
            DooDesch.UI.Components.PromptDialog(root, "New game",
                "Name your organisation - a fresh save is created and hosted as a public lobby.",
                "Organisation name", "Create and host",
                name =>
                {
                    if (string.IsNullOrWhiteSpace(name)) return "Enter an organisation name.";
                    HostNewGame(name.Trim());
                    return null;
                });
        }

        private static void HostNewGame(string orgName)
        {
            if (FirstFreeSaveSlot() < 0)
            {
                ShowRows("Host a save", new List<Row>
                {
                    new Row { Name = "All save slots are full", Subtitle = "Delete a save in the main menu first, then create a new game.", Disabled = true },
                    new Row { Name = "Back", Subtitle = "Back to your saves.", OnClick = ShowVanillaSavePicker }
                });
                return;
            }
            // The fresh save is created only when the player presses Host (in ShowVanillaHostForm's onHost), so
            // abandoning the form never leaves an orphaned slot.
            ShowVanillaHostForm(null, orgName);
        }

        // The first empty save slot (0..4), or -1 when all five are occupied.
        private static int FirstFreeSaveSlot()
        {
            try
            {
                var saves = Il2CppScheduleOne.Persistence.LoadManager.SaveGames;
                for (int i = 0; i < (saves?.Length ?? 0); i++)
                {
                    var info = saves[i];
                    string org = null;
                    try { org = info?.OrganisationName; } catch { /* treat as empty */ }
                    if (info == null || string.IsNullOrEmpty(org)) return i;
                }
            }
            catch (Exception e) { Core.Log?.Warning("[sync] free-slot scan failed: " + e.Message); }
            return -1;
        }

        // Host an existing save, or - when newGameOrg is set - a fresh new game. For a new game the save is NOT
        // created here: it is materialized only when the player actually presses Host (in the onHost callback), so
        // backing out of the form or a failed plan build never leaves an orphaned, never-played save slot behind.
        private static void ShowVanillaHostForm(Il2CppScheduleOne.Persistence.SaveInfo save, string newGameOrg = null)
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
                    // Only rebuild if the host form is still the active view (the player may have navigated away
                    // during the async index fetch).
                    if (_cloneScreen == null || !_cloneScreen.IsOpen || _formHost == null || _formHost.name != "SH_VanillaHost") return;
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
                            var target = save;
                            if (target == null && newGameOrg != null)
                            {
                                // Create the fresh save now, at the moment of hosting, so an abandoned form leaves nothing.
                                int slot = FirstFreeSaveSlot();
                                target = slot >= 0 ? Multiplayer.WorldBoot.CreateNewSave(slot, newGameOrg) : null;
                                if (target == null)
                                {
                                    ProfilesViews.BuildInstalling(CreateFormHost("SH_VanillaHost", 560f),
                                        "Couldn't create the new save (no free slot or an error - see the log).", ShowVanillaSavePicker);
                                    return;
                                }
                            }
                            if (target == null) return;
                            string prefsText = PrefsCatalog.BuildOverlay(syncedCats);
                            CloseHubScreen();
                            SyncCoordinator.StartHostVanilla(target, opts,
                                plan.Manifest.ToCanonicalText(), prefsText,
                                $"{plan.AutoCount + plan.GhCount}/{plan.LinkCount}/{plan.DroppedCount}", enforce);
                        });
                });
            });
        }

        // The row behind each browser card, so a card's Join can recover the full vanilla row (manifest hash,
        // enforce flag, owner trust) that the generic LobbyRow card model does not carry.
        private static readonly Dictionary<ulong, VanillaLobbyRow> _vanillaRowsById = new Dictionary<ulong, VanillaLobbyRow>();

        // The vanilla lobby browser now uses the SAME card list as the gamemode Join browser (JoinBrowserView) -
        // scrollable lobby cards with a per-card Join button and a Refresh/Back footer - instead of the old plain
        // text rows. Both back onto the identical Steam lobby query; only the row DTO and post-select flow differ.
        private static void ShowVanillaBrowser()
        {
            if (_clone == null) return;
            _back = ShowVanillaChoice;
            ClearFormHost();
            SetTmp(_clone.transform, "Title", "Join a vanilla lobby");

            var host = CreateFormHost("SH_VanillaBrowser", 560f);
            var content = JoinBrowserView.Build(host, ShowVanillaChoice, ShowVanillaBrowser);
            JoinBrowserView.SetStatus(content, "Searching for public vanilla lobbies...");
            ServerBrowser.BeginQueryVanilla(rows => MainThread.Post(() =>
            {
                if (_cloneScreen == null || !_cloneScreen.IsOpen || _formHost == null || _formHost.name != "SH_VanillaBrowser") return;
                _vanillaRowsById.Clear();
                var mapped = new List<LobbyRow>();
                foreach (var r in rows ?? new List<VanillaLobbyRow>())
                {
                    _vanillaRowsById[r.LobbyId] = r;
                    mapped.Add(MapVanillaRow(r));
                }
                JoinBrowserView.Populate(content, mapped, lr =>
                {
                    if (_vanillaRowsById.TryGetValue(lr.LobbyId, out var vr)) StartVanillaJoin(vr);
                });
            }));
        }

        // Adapt a vanilla lobby summary to the generic browser-card model: the save name, published mod counts and
        // enforce flag ride in the card's secondary line (LobbyRow.Mode, shown after "Vanilla Co-op").
        private static LobbyRow MapVanillaRow(VanillaLobbyRow r)
        {
            // Keep the card's secondary line short (it already leads with "Vanilla Co-op" + the player count, and
            // BuildCard appends a "Locked" flag): just the save name plus a compact synced-only marker. The full
            // published mod breakdown is shown on the sync-consent screen after the player picks the lobby.
            string extra = $"save '{r.Org}'";
            if (r.Enforced) extra += "  ·  synced-only";
            return new LobbyRow
            {
                LobbyId = r.LobbyId,
                LobbyName = r.LobbyName,
                HostName = r.HostName,
                Members = r.Members,
                MaxPlayers = r.MaxPlayers,
                HasPassword = r.HasPassword,
                PwHash = r.PwHash,
                GamemodeName = "Vanilla Co-op",
                Mode = extra,
            };
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

            // The browse read the card off Steam's lobby-LIST snapshot, which delivers big values (the chunked
            // manifest) late and unreliably - so a first read can miss the manifest entirely, producing a false
            // "Sync unavailable". Request the full lobby data and retry a few times before declaring it unsyncable.
            ShowRows("Reading the host's mods...", new List<Row>
            {
                new Row { Name = "Reading the host's mod list...", Subtitle = "Fetching the details from Steam.", Disabled = true }
            });
            try { Il2CppSteamworks.SteamMatchmaking.RequestLobbyData(new Il2CppSteamworks.CSteamID(row.LobbyId)); } catch { }
            WaitForManifest(row, 0);
        }

        // Retry reading the host's chunked manifest (re-requesting lobby data each attempt) until it validates or the
        // ~3s window elapses. The read + UI run on the main thread; only the delay rides a background task.
        private static void WaitForManifest(VanillaLobbyRow row, int attempt)
        {
            if (_cloneScreen == null || !_cloneScreen.IsOpen) return;
            if (VanillaLobby.TryReadPayloads(row.LobbyId, out var manifest, out var hostPrefs, out var mhash))
            {
                BeginSyncCompare(row, manifest, hostPrefs, mhash);
                return;
            }
            if (attempt >= 7)   // ~5s of Steam (the primary path) before falling back to the backend directory
            {
                TryDirectoryFallback(row);
                return;
            }
            try { Il2CppSteamworks.SteamMatchmaking.RequestLobbyData(new Il2CppSteamworks.CSteamID(row.LobbyId)); } catch { }
            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(700);
                MainThread.Post(() => WaitForManifest(row, attempt + 1));
            });
        }

        // Steam couldn't produce the manifest (likely too large to propagate) - fall back to the backend directory.
        // The backend is untrusted, so its manifest is only accepted when it hashes to the mhash the host wrote to the
        // real Steam lobby (see VanillaLobby.TryReadFromDirectoryAsync).
        private static void TryDirectoryFallback(VanillaLobbyRow row)
        {
            Core.Log?.Msg("[sync] Steam manifest unavailable; trying the backend fallback...");
            ShowRows("Reading the host's mods...", new List<Row>
            {
                new Row { Name = "Reading the host's mod list (backend)...", Subtitle = "Steam couldn't share it - using the fallback.", Disabled = true }
            });
            System.Threading.Tasks.Task.Run(async () =>
            {
                var res = await VanillaLobby.TryReadFromDirectoryAsync(row.LobbyId);
                MainThread.Post(() =>
                {
                    if (_cloneScreen == null || !_cloneScreen.IsOpen) return;
                    if (res != null)
                    {
                        Core.Log?.Msg("[sync] backend fallback provided the manifest.");
                        BeginSyncCompare(row, res.Manifest, res.Prefs, res.Mhash);
                    }
                    else
                    {
                        Core.Log?.Warning("[sync] manifest unreadable via Steam AND backend: " + VanillaLobby.DescribeReadFailure(row.LobbyId));
                        ShowUnsyncableJoin(row, "Couldn't read the host's mod list from Steam or the backend. The host may be on a different build. Go Back and try again.");
                    }
                });
            });
        }

        private static void BeginSyncCompare(VanillaLobbyRow row, SyncManifest manifest, string hostPrefs, string mhash)
        {
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
                    if (!diff.NeedsRestart && !diff.AnyVersionWarn && string.IsNullOrEmpty(hostPrefs))
                    {
                        // Our mods already match the manifest and there is nothing to overlay: join in place, but
                        // through the coordinator so the synced handshake (sh_sync member data) is still set -
                        // otherwise an enforcing host's gate would kick us after the grace period for never syncing.
                        CloseHubScreen();
                        SyncCoordinator.StartInPlaceJoin(row.LobbyId, mhash);
                        return;
                    }
                    if (nothingToFetch && TrustStore.IsTrusted(row.OwnerSteamId, mhash))
                    {
                        // Consent already given for THIS manifest: rejoin seamlessly, no checklist even if dropped mods remain.
                        StartSyncAndJoin(row, manifest, diff, mhash, hostPrefs, offerChecklist: false);
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
        /// bytes, remember the trust, and restart with the rejoin token. Hand-fetched mods (an nx: link mod or a
        /// source-less Nexus-only one) are not fetchable here yet - they ride as absent this round (their checklist
        /// is the next build step). <paramref name="offerChecklist"/> is false for a trusted rejoin that already
        /// consented to this exact manifest, so it is not re-nagged with the checklist.</summary>
        private static void StartSyncAndJoin(VanillaLobbyRow row, SyncManifest manifest, SyncDiff diff, string mhash, string hostPrefs = null, bool offerChecklist = true)
        {
            // Anything the client must fetch by hand - an nx: link mod (Manual) or a source-less Nexus-only mod
            // (Dropped) - gets the checklist: show it, and only build+restart once the player continues (resolved
            // files land in the cache the resolver reads). The dev/trusted path skips straight to the build.
            if (offerChecklist && diff.Entries.Any(e => e.Status == DiffStatus.Manual || e.Status == DiffStatus.Dropped)
                && _clone != null && _cloneScreen != null && _cloneScreen.IsOpen)
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
                SetTmp(_clone.transform, "Title", "Joining the host");
                var host = CreateFormHost("SH_Syncing", 560f);
                ProfilesViews.BuildBigStatus(host, "RESTARTING WITH HOST MODS",
                    "The game restarts and rejoins the host on its own - hang tight, this can take a moment. Don't close the game.");
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
                    // Remember this lobby's mod set so the player can turn it into a permanent named profile later.
                    LastSync.Save(row.HostName, new SyncManifest { Mods = diff.Entries.Select(e => e.Mod).ToList() });
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
