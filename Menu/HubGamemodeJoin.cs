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
    /// Joining a gamemode lobby you do NOT have the mod for. The host advertises the exact files a joiner needs
    /// (<see cref="LobbyCoordinator"/> publishes them onto the lobby); this reads that list, shows the same consent
    /// and manual-install screens the vanilla co-op sync uses, installs everything into a session profile and
    /// restarts - and once the gamemode is loaded, the pending token drops the player straight into that lobby.
    ///
    /// Deliberately narrower than the vanilla sync: no prefs overlay (a gamemode carries its settings in its own
    /// config blob), no backend fallback (a gamemode's mod set is a handful of entries, well inside what Steam
    /// propagates) and no trust store (installing a gamemode always asks).
    /// </summary>
    internal static partial class Hub
    {
        private const int JoinManifestAttempts = 7;

        // One retry through the checklist when a download failed; a second pass installs whatever is there.
        private static bool _ghostRetried;
        /// <summary>The live download progress view, kept so the async download can report into it.</summary>
        private static InstallProgressView.Controller _installUi;

        /// <summary>
        /// The lobby browser for a gamemode the player does NOT have - deliberately the same view an installed
        /// player gets, so "join a session" works the same way; only the install runs in between. No build-mismatch
        /// badge here: there is no local build to compare against yet.
        /// </summary>
        private static void ShowGhostBrowser(Ghost g)
        {
            if (_clone == null || g == null) return;
            _back = () => ShowUninstalledChoice(g);
            ClearFormHost();
            SetTmp(_clone.transform, "Title", "Join: " + g.Name);
            var host = CreateFormHost("SH_GhostBrowser", 560f);
            var content = JoinBrowserView.Build(host, () => ShowUninstalledChoice(g), () => RefreshGhostBrowser(g));
            JoinBrowserView.Populate(content, g.Rows, row => StartGhostJoin(g, row));
        }

        // Refresh: re-query the advertised lobbies and rebuild this gamemode's browser from the fresh set.
        private static void RefreshGhostBrowser(Ghost g)
        {
            RefreshAdvertised(0f);
            var fresh = _advertisedCache;
            if (fresh != null)
            {
                g.Rows = fresh.Where(l => string.Equals(l.GamemodeId, g.GamemodeId, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            ShowGhostBrowser(g);
        }

        /// <summary>A session was picked: password gate, fetch that host's mod list, consent.</summary>
        private static void StartGhostJoin(Ghost g, LobbyRow row)
        {
            if (g == null || row == null || row.LobbyId == 0) return;
            _ghostRetried = false;   // a fresh attempt gets its own "show me what is missing" pass
            // Ask for the password BEFORE installing anything - nobody wants to download a gamemode for a lobby they
            // cannot enter (same client-side hash check the normal join browser uses).
            if (row.HasPassword && !string.IsNullOrEmpty(row.PwHash))
            {
                var canvas = _clone != null ? _clone.GetComponentInParent<UnityEngine.Canvas>() : null;
                Transform root = canvas != null ? canvas.transform : (_clone != null ? _clone.transform : null);
                if (root != null)
                {
                    DooDesch.UI.Components.PromptDialog(root, "Password required",
                        $"Enter the password for {(string.IsNullOrEmpty(row.HostName) ? "this" : row.HostName + "'s")} lobby.",
                        "password", "Continue",
                        entered => string.Equals(LobbyCoordinator.HashPassword(entered ?? ""), row.PwHash, StringComparison.Ordinal)
                                   ? GhostPasswordAccepted(g, row)
                                   : "Incorrect password.");
                    return;
                }
            }
            ReadGhostManifest(g, row);
        }

        private static string GhostPasswordAccepted(Ghost g, LobbyRow row) { ReadGhostManifest(g, row); return null; }

        private static void ReadGhostManifest(Ghost g, LobbyRow row)
        {
            _back = () => ShowGhostBrowser(g);
            ShowRows(g.Name, new List<Row>
            {
                new Row { Name = "Reading the host's mod list...", Subtitle = "Fetching the details from Steam.", Disabled = true }
            });
            try { Il2CppSteamworks.SteamMatchmaking.RequestLobbyData(new Il2CppSteamworks.CSteamID(row.LobbyId)); } catch { }
            WaitForGhostManifest(g, row, 0);
        }

        // Steam delivers the big chunked values late on a lobby-LIST snapshot, so re-request and retry before giving up.
        private static void WaitForGhostManifest(Ghost g, LobbyRow row, int attempt)
        {
            if (_cloneScreen == null || !_cloneScreen.IsOpen) return;
            if (VanillaLobby.TryReadPayloads(row.LobbyId, out var manifest, out _, out var mhash) && manifest.Mods.Count > 0)
            {
                BeginGhostCompare(g, row, manifest, mhash);
                return;
            }
            if (attempt >= JoinManifestAttempts)
            {
                Core.Log?.Warning("[mp] gamemode join: no mod list on the lobby - " + VanillaLobby.DescribeReadFailure(row.LobbyId));
                ShowGhostNoManifest(g);
                return;
            }
            try { Il2CppSteamworks.SteamMatchmaking.RequestLobbyData(new Il2CppSteamworks.CSteamID(row.LobbyId)); } catch { }
            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(700);
                MainThread.Post(() => WaitForGhostManifest(g, row, attempt + 1));
            });
        }

        // An older host (or one whose gamemode could not resolve its own files) advertises no mod list: the player
        // can still get the mod the old way, by hand.
        private static void ShowGhostNoManifest(Ghost g)
        {
            _back = ShowGamemodeList;
            bool canLookUp = DownloadLink.IsAllowed(g.Url) || Sync.NexusLookup.CanLookUp(g.Name);
            ShowRows(g.Name, new List<Row>
            {
                new Row
                {
                    Name = "This host can't install it for you",
                    Subtitle = "The lobby doesn't advertise its mod files (older Side Hustle on the host).",
                    Disabled = true
                },
                new Row
                {
                    Name = DownloadLink.IsAllowed(g.Url) ? "Download Mod" : "Find it on Nexus",
                    Subtitle = canLookUp ? "Install it yourself, then join from the gamemode list." : "No trusted download link provided.",
                    OnClick = canLookUp
                        ? (Action)(() => DownloadLink.Open(DownloadLink.IsAllowed(g.Url) ? g.Url : DownloadLink.NexusUrl(g.Name)))
                        : null,
                    Disabled = !canLookUp
                },
                new Row { Name = "Back", Subtitle = "Back to the gamemode list.", OnClick = ShowGamemodeList }
            });
        }

        // The gamemode's own mod did not arrive (download failed, or it is a hand-install nobody grabbed): offer the
        // download link so the player can finish it themselves rather than restarting for nothing.
        /// <summary>
        /// Nothing installable arrived. <paramref name="reasons"/> is what the resolver could not fetch and why - the
        /// screen used to say only that something was missing, which left the player with no idea whether to retry, wait
        /// or install by hand. The reasons are the difference between a dead end and a next step.
        /// </summary>
        private static void ShowGhostMissingMod(Ghost g, IReadOnlyList<string> reasons = null)
        {
            _back = ShowGamemodeList;
            bool canLookUp = DownloadLink.IsAllowed(g.Url) || Sync.NexusLookup.CanLookUp(g.Name);
            var rows = new List<Row>
            {
                new Row { Name = "The gamemode itself is still missing", Subtitle = "Nothing was installed, so joining would not work.", Disabled = true },
                new Row
                {
                    Name = DownloadLink.IsAllowed(g.Url) ? "Download Mod" : "Find it on Nexus",
                    Subtitle = canLookUp ? "Install it by hand, then join from the gamemode list." : "No trusted download link provided.",
                    OnClick = canLookUp
                        ? (Action)(() => DownloadLink.Open(DownloadLink.IsAllowed(g.Url) ? g.Url : DownloadLink.NexusUrl(g.Name)))
                        : null,
                    Disabled = !canLookUp
                },
                new Row { Name = "Back", Subtitle = "Back to the gamemode list.", OnClick = ShowGamemodeList }
            };

            // Insert the WHY between the headline and the manual link, so it is read before the button is pressed.
            if (reasons != null && reasons.Count > 0)
            {
                int at = 1;
                foreach (var r in reasons)
                {
                    if (string.IsNullOrWhiteSpace(r)) continue;
                    rows.Insert(at++, new Row { Name = r, Subtitle = "Could not be fetched automatically.", Disabled = true });
                    if (at > 4) break;   // a wall of rows stops being read; the log carries the full list
                }
            }

            ShowRows(g.Name, rows);
        }

        private static void BeginGhostCompare(Ghost g, LobbyRow row, SyncManifest manifest, string mhash)
        {
            ShowRows(g.Name, new List<Row>
            {
                new Row { Name = "Checking what you need...", Subtitle = "Comparing the gamemode's mods with yours.", Disabled = true }
            });
            System.Threading.Tasks.Task.Run(() =>
            {
                SyncDiff diff = null;
                try { diff = SyncResolver.Compute(manifest); }
                catch (Exception e) { Core.Log?.Warning("[mp] gamemode join: compare failed: " + e.Message); }
                MainThread.Post(() =>
                {
                    if (_cloneScreen == null || !_cloneScreen.IsOpen) return;
                    if (diff == null) { ShowGhostNoManifest(g); return; }
                    ShowGhostConsent(g, row, manifest, diff, mhash);
                });
            });
        }

        private static void ShowGhostConsent(Ghost g, LobbyRow row, SyncManifest manifest, SyncDiff diff, string mhash)
        {
            if (_clone == null) return;
            _back = ShowGamemodeList;
            SyncManualInstallView.PrefetchLinks(diff);
            ClearFormHost();
            SetTmp(_clone.transform, "Title", g.Name);
            var host = CreateFormHost("SH_GhostConsent", 560f);
            // enforced: a gamemode session always runs the host's set - joining "as you are" is not on offer here,
            // because without the gamemode's own mod there is nothing to join with.
            SyncConsentView.Build(host, manifest, diff, enforced: true, hasPrefs: false,
                onSyncJoin: () => GhostSyncAndJoin(g, row, manifest, diff, mhash),
                onPlainJoin: null,
                onBack: () => ShowGhostBrowser(g),
                enforcedNote: $"You don't have {g.Name} yet - Side Hustle installs it into a separate session profile, "
                              + "restarts, and joins the lobby. Your own mods stay untouched.");
        }

        /// <summary>
        /// Joining a gamemode you DO have installed, but with a different mod set than the host.
        ///
        /// Until now this case skipped every check: only players who lacked the gamemode entirely went through the
        /// install flow, so anyone who owned it joined carrying whatever else they had loaded. The host curates its
        /// set with "required mods only" precisely to avoid that, and the joiner then quietly undid it - a mod the
        /// host does not run can patch the same game methods the gamemode does and break the round for everyone,
        /// which is exactly the class of problem the curation exists to prevent.
        ///
        /// The comparison, the consent screen, the profile build and the auto-rejoin already existed for the
        /// not-installed case. This routes the installed case into the same machinery; the only difference is that
        /// joining as you are stays on offer, because unlike a missing gamemode a mismatched set is a risk rather
        /// than a hard blocker.
        ///
        /// Returns true when it has taken over the screen - the caller must not join.
        /// </summary>
        /// <summary>
        /// Rising counter identifying the join attempt in flight. Any navigation or a new attempt invalidates the old
        /// one, so a comparison that finishes after the player moved on cannot act.
        ///
        /// Without it the completion only knew that SOME hub screen was still open: going back and starting a second
        /// join left the first comparison free to finish and join the FIRST lobby, or to draw its consent over the new
        /// screen - with the coordinator then out of step with the Steam join actually being attempted.
        /// </summary>
        private static int _joinGen;

        internal static void InvalidateJoinChecks() => _joinGen++;

        internal static bool BeginModCheckedJoin(GamemodeDescriptor desc, LobbyRow row, Action joinAnyway)
        {
            if (desc == null || row == null || row.LobbyId == 0 || joinAnyway == null) return false;
            if (_clone == null || _cloneScreen == null || !_cloneScreen.IsOpen) return false;

            var g = new Ghost { Name = desc.DisplayName ?? desc.Id, GamemodeId = desc.Id };
            _ghostRetried = false;
            int gen = ++_joinGen;

            ShowRows(g.Name, new List<Row>
            {
                new Row { Name = "Checking your mods...", Subtitle = "Reading the host's list.", Disabled = true }
            });
            try { Il2CppSteamworks.SteamMatchmaking.RequestLobbyData(new Il2CppSteamworks.CSteamID(row.LobbyId)); } catch { }
            WaitForInstalledManifest(g, row, joinAnyway, gen, 0);
            return true;
        }

        /// <summary>
        /// Wait for the host's mod list before comparing, retrying like the not-installed flow already does.
        ///
        /// Steam delivers the big chunked lobby values LATE on a list snapshot, which the ghost path documents and
        /// retries seven times for. Reading once and joining unchecked on a miss - which is what this did - meant the
        /// safeguard silently did not apply during ordinary metadata propagation. A safeguard that fails open exactly
        /// when it is slow to answer is worse than none, because nobody can tell the difference.
        /// </summary>
        private static void WaitForInstalledManifest(Ghost g, LobbyRow row, Action joinAnyway, int gen, int attempt)
        {
            if (gen != _joinGen) return;                                  // the player moved on
            if (_cloneScreen == null || !_cloneScreen.IsOpen) return;

            SyncManifest manifest = null; string mhash = null; bool got = false;
            try { got = VanillaLobby.TryReadPayloads(row.LobbyId, out manifest, out _, out mhash) && manifest.Mods.Count > 0; }
            catch { got = false; }

            if (got) { BeginInstalledCompare(g, row, manifest, mhash, joinAnyway, gen); return; }

            if (attempt >= JoinManifestAttempts)
            {
                // Genuinely absent, as far as we can tell: an older host, or one whose gamemode could not resolve its
                // own files. Joining beats refusing - the comparison is a safeguard, not something the session needs.
                Core.Log?.Msg("[mp] join: no mod list on the lobby after " + JoinManifestAttempts +
                              " tries - joining without a comparison. " + VanillaLobby.DescribeReadFailure(row.LobbyId));
                joinAnyway();
                return;
            }
            try { Il2CppSteamworks.SteamMatchmaking.RequestLobbyData(new Il2CppSteamworks.CSteamID(row.LobbyId)); } catch { }
            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(700);
                MainThread.Post(() => WaitForInstalledManifest(g, row, joinAnyway, gen, attempt + 1));
            });
        }

        private static void BeginInstalledCompare(Ghost g, LobbyRow row, SyncManifest manifest, string mhash,
                                                 Action joinAnyway, int gen)
        {
            ShowRows(g.Name, new List<Row>
            {
                new Row { Name = "Checking your mods...", Subtitle = "Comparing your set with the host's.", Disabled = true }
            });
            System.Threading.Tasks.Task.Run(() =>
            {
                SyncDiff diff = null;
                try { diff = SyncResolver.Compute(manifest); }
                catch (Exception e) { Core.Log?.Warning("[mp] join: mod compare failed: " + e.Message); }
                MainThread.Post(() =>
                {
                    if (gen != _joinGen) return;                          // a newer attempt owns the screen now
                    if (_cloneScreen == null || !_cloneScreen.IsOpen) return;
                    // Could not compare, or nothing to reconcile - join exactly as before.
                    if (diff == null || (!diff.NeedsRestart && !diff.AnyVersionWarn)) { joinAnyway(); return; }

                    Core.Log?.Msg($"[mp] join: mod set differs from the host " +
                                  $"(link/download {diff.Count(DiffStatus.Cached) + diff.Count(DiffStatus.Download)}, " +
                                  $"ours-only {diff.LocalOnly.Count}) - asking the player.");
                    ShowInstalledConsent(g, row, manifest, diff, mhash, joinAnyway);
                });
            });
        }

        private static void ShowInstalledConsent(Ghost g, LobbyRow row, SyncManifest manifest, SyncDiff diff,
                                                 string mhash, Action joinAnyway)
        {
            if (_clone == null) return;
            _back = ShowGamemodeList;
            SyncManualInstallView.PrefetchLinks(diff);
            ClearFormHost();
            SetTmp(_clone.transform, "Title", g.Name);
            var host = CreateFormHost("SH_JoinModCheck", 560f);
            SyncConsentView.Build(host, manifest, diff, enforced: false, hasPrefs: false,
                onSyncJoin: () => GhostSyncAndJoin(g, row, manifest, diff, mhash),
                onPlainJoin: joinAnyway,
                onBack: () => ShowGamemodeList(),
                enforcedNote: null);
        }

        private static void GhostSyncAndJoin(Ghost g, LobbyRow row, SyncManifest manifest, SyncDiff diff, string mhash)
        {
            // Mods nobody can fetch automatically (a Nexus link, or one with no source at all) get the checklist -
            // the same one the co-op sync uses, including the "Open Nexus" lookup.
            if (diff.Entries.Any(e => e.Status == DiffStatus.Manual || e.Status == DiffStatus.Dropped)
                && _clone != null && _cloneScreen != null && _cloneScreen.IsOpen)
            {
                _back = () => ShowGhostConsent(g, row, manifest, diff, mhash);
                ClearFormHost();
                SetTmp(_clone.transform, "Title", "Manual installs");
                var mh = CreateFormHost("SH_GhostManual", 560f);
                SyncManualInstallView.Build(mh, diff,
                    row?.OwnerSteamId ?? 0UL, row?.HostName, row?.AcceptsMessages ?? false,
                    onContinue: () => GhostBuildAndRestart(g, row, diff, mhash),
                    onBack: () => ShowGhostConsent(g, row, manifest, diff, mhash));
                return;
            }
            GhostBuildAndRestart(g, row, diff, mhash);
        }

        private static void GhostBuildAndRestart(Ghost g, LobbyRow row, SyncDiff diff, string mhash)
        {
            if (_clone != null && _cloneScreen != null && _cloneScreen.IsOpen)
            {
                _back = null;
                ClearFormHost();
                SetTmp(_clone.transform, "Title", "Installing " + g.Name);
                var host = CreateFormHost("SH_GhostInstalling", 560f);
                // The real progress view, not a static card: this is the part of the flow that takes time, so it is the
                // part that has to show what is happening. The card said nothing while several megabytes came down.
                _installUi = InstallProgressView.Build(host, "Installing " + g.Name,
                    SyncDownloadProgress.PlanFrom(diff), diff.Unresolved, onCancel: null);
            }

            var sink = _installUi != null ? new SyncDownloadProgress(_installUi, diff) : null;
            System.Threading.Tasks.Task.Run(async () =>
            {
                bool allFetched = false;
                try { allFetched = await SyncResolver.DownloadMissingAsync(diff, sink, System.Threading.CancellationToken.None); }
                catch (Exception e) { Core.Log?.Warning("[mp] gamemode join: downloads failed: " + e.Message); }

                var inputs = SyncResolver.ToInputs(diff);
                SyncResolver.ResolveExtras(diff, out var pluginInputs, out var userLibInputs);
                MainThread.Post(() =>
                {
                    // The gamemode's own mod is the point of the whole flow: without it a restart lands the player in
                    // the menu with nothing gained, so stop here and say why instead.
                    string ownFile = "";
                    try { ownFile = Il2CppSteamworks.SteamMatchmaking.GetLobbyData(new Il2CppSteamworks.CSteamID(row.LobbyId), LobbyCoordinator.KeyGamemodeFile); }
                    catch (Exception e) { Core.Log?.Warning("[mp] could not read the lobby's gamemode file: " + e.Message); }
                    bool Resolved(DiffEntry e) => e.Status == DiffStatus.Present || e.Status == DiffStatus.Cached;
                    bool ok = string.IsNullOrEmpty(ownFile)
                        ? diff.Entries.Any(Resolved)
                        : diff.Entries.Any(e => Resolved(e) && string.Equals(e.Mod.File, ownFile, StringComparison.OrdinalIgnoreCase));
                    if (!ok)
                    {
                        Core.Log?.Error("[mp] gamemode join: the gamemode's own mod could not be installed; staying in the menu.");
                        ShowGhostMissingMod(g, diff.Unresolved);
                        return;
                    }

                    // A download that failed leaves a mod the host does run out of the session. The gamemode itself is
                    // there (checked above), so this is not fatal - but the player gets the checklist ONCE with what is
                    // still missing instead of being restarted into a quietly incomplete set. Continuing from the
                    // checklist a second time proceeds regardless (that is what "skip missing" means).
                    if (!allFetched && !_ghostRetried
                        && diff.Entries.Any(e => e.Status == DiffStatus.Manual || e.Status == DiffStatus.Dropped)
                        && _clone != null && _cloneScreen != null && _cloneScreen.IsOpen)
                    {
                        _ghostRetried = true;
                        Core.Log?.Warning("[mp] gamemode join: not every mod could be fetched - showing what is still missing.");
                        SyncManualInstallView.PrefetchLinks(diff);
                        _back = null;
                        ClearFormHost();
                        SetTmp(_clone.transform, "Title", "Still missing");
                        var mh2 = CreateFormHost("SH_GhostManualRetry", 560f);
                        SyncManualInstallView.Build(mh2, diff,
                            row?.OwnerSteamId ?? 0UL, row?.HostName, row?.AcceptsMessages ?? false,
                            onContinue: () => GhostBuildAndRestart(g, row, diff, mhash),
                            onBack: () => ShowGhostBrowser(g));
                        return;
                    }
                    var token = ConfigCodec.Encode(new[]
                    {
                        new KeyValuePair<string, string>("lobby", row.LobbyId.ToString()),
                        new KeyValuePair<string, string>("gm", g.GamemodeId ?? ""),
                        new KeyValuePair<string, string>("mhash", mhash ?? ""),
                    });
                    var tokens = new Dictionary<string, string>
                    {
                        ["PendingGamemodeJoin"] = token,
                        ["PendingVanillaJoin"] = "",
                        ["PendingContinue"] = "",
                        ["PendingHostOptions"] = "",
                        ["ActiveGamemodeId"] = g.GamemodeId ?? "",
                    };
                    // FINISH the progress view, then restart a moment later.
                    //
                    // With everything already in the package cache the download reports nothing at all, so the bar sat
                    // at zero and the game then vanished into a relaunch with no warning - indistinguishable from a
                    // crash. Completing the bar and saying what happens next costs half a second and is the difference
                    // between "it restarted itself" and "it broke".
                    // One shared commit point for both sync paths - see CommittedRestart.
                    CommittedRestart.Then(g.Name, () =>
                        Mods.ModSwitcher.RelaunchIntoSyncProfile("gmjoin-" + g.GamemodeId, inputs, tokens, prefsOverlay: null,
                            logLabel: $"installing {inputs.Count} mod(s) to join '{g.Name}'", pluginInputs, userLibInputs));
                });
            });
        }

        /// <summary>
        /// After the restart: the gamemode is loaded now, so join the lobby it was installed for. A lobby that is
        /// gone (or whose mod set changed while we installed) leaves the player in the hub instead of joining
        /// something else than they agreed to.
        /// </summary>
        internal static void ContinueGamemodeJoin(string payload)
        {
            EnsureInit();
            EnsureClone();
            var map = ConfigCodec.Decode(payload);
            map.TryGetValue("gm", out var gmId);
            map.TryGetValue("mhash", out var wantHash);
            if (!map.TryGetValue("lobby", out var ls) || !ulong.TryParse(ls, out var lobbyId) || lobbyId == 0)
            {
                Core.Log?.Warning("[mp] gamemode-join token unreadable; staying in the menu.");
                RejoinNotice.Hide();
                return;
            }

            var desc = API.Registered.FirstOrDefault(d => string.Equals(d.Id, gmId, StringComparison.OrdinalIgnoreCase));
            if (desc == null)
            {
                Core.Log?.Warning($"[mp] '{gmId}' is still not registered after the install; staying in the menu.");
                RejoinNotice.Hide();
                if (_cloneScreen != null && !_cloneScreen.IsOpen) { ShowGamemodeList(); _cloneScreen.Open(); }
                return;
            }

            // This is a FRESH process: it has no cached lobby data yet, so reading metadata right away returns blanks
            // and every check would pass vacuously. Ask Steam and retry until the lobby actually answers - then verify
            // it is still the same gamemode lobby before entering it.
            try { Il2CppSteamworks.SteamMatchmaking.RequestLobbyData(new Il2CppSteamworks.CSteamID(lobbyId)); }
            catch (Exception e) { Core.Log?.Warning("[mp] could not ask Steam for the lobby data: " + e.Message); }
            VerifyThenJoin(desc, lobbyId, wantHash, 0);
        }

        /// <summary>How long the post-install join waits for the lobby to answer before giving up (7 x 700ms).</summary>
        private const int RejoinDataAttempts = 7;

        private static void VerifyThenJoin(GamemodeDescriptor desc, ulong lobbyId, string wantHash, int attempt)
        {
            var info = LobbyCoordinator.ReadInfo(lobbyId);
            string liveHash = "";
            // Swallowing this would be worse than failing: an empty hash reads as "the lobby has not answered yet",
            // so a broken read turns into a silent retry loop and then a "lobby is gone" the host never caused.
            try { liveHash = Il2CppSteamworks.SteamMatchmaking.GetLobbyData(new Il2CppSteamworks.CSteamID(lobbyId), VanillaLobby.KeyMHash); }
            catch (Exception e) { Core.Log?.Warning("[mp] could not read the lobby's mod hash: " + e.Message); }
            // "Answered" means the keys we verify against are actually there. A half-propagated lobby (id present,
            // hash still empty) must keep waiting, otherwise the mod-set check passes vacuously.
            bool answered = !string.IsNullOrEmpty(info.GamemodeId)
                            && (string.IsNullOrEmpty(wantHash) || !string.IsNullOrEmpty(liveHash));
            if (!answered)
            {
                if (attempt < RejoinDataAttempts)
                {
                    RejoinNotice.Update($"Looking for the session... attempt {attempt + 1} of {RejoinDataAttempts}");
                    try { Il2CppSteamworks.SteamMatchmaking.RequestLobbyData(new Il2CppSteamworks.CSteamID(lobbyId)); }
                    catch (Exception e) { Core.Log?.Warning("[mp] lobby data re-request failed: " + e.Message); }
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(700);
                        MainThread.Post(() => VerifyThenJoin(desc, lobbyId, wantHash, attempt + 1));
                    });
                    return;
                }
                // The lobby never answered - it is most likely gone. Joining would strand the player on a loading
                // screen until the coordinator's timeout, so stay in the hub where the gamemode is now installed.
                Core.Log?.Warning($"[mp] lobby {lobbyId} did not answer after the install - it is probably closed. " +
                                  $"'{desc.DisplayName}' is installed; pick a session from the browser.");
                ShowInstalledButLobbyGone(desc);
                return;
            }

            if (!string.Equals(info.GamemodeId, desc.Id, StringComparison.OrdinalIgnoreCase))
            {
                Core.Log?.Warning($"[mp] lobby {lobbyId} is no longer a '{desc.Id}' lobby (now '{info.GamemodeId}') - not joining.");
                ShowInstalledButLobbyGone(desc);
                return;
            }

            // The host changed their mods while we were installing: what we just built is not what this lobby needs
            // anymore, so entering it would be exactly the "join something else than agreed" case. Back to the
            // gamemode's browser - one click re-reads the new list and installs the difference.
            if (!string.IsNullOrEmpty(wantHash) && liveHash != wantHash)
            {
                Core.Log?.Warning($"[mp] lobby {lobbyId} now advertises a different mod set - not joining with the old one.");
                ShowInstalledButLobbyGone(desc, $"{desc.DisplayName} is installed - the host changed their mods, pick the session again.");
                return;
            }

            Core.Log?.Msg($"[mp] installed '{desc.DisplayName}'; joining lobby {lobbyId} now.");
            // Left up on purpose: it comes down when the menu scene unloads for the world, so the gap between the last
            // check and the game's loading screen is covered too.
            RejoinNotice.Update($"Joining {desc.DisplayName} - loading the world...");
            CloseHubScreen();
            MultiplayerCoordinator.StartJoin(desc, new LobbyRow
            {
                LobbyId = lobbyId,
                GamemodeId = desc.Id,
                GamemodeName = info.GamemodeName,
                LobbyName = info.LobbyName,
                HostName = info.HostName,
                MaxPlayers = info.MaxPlayers,
                BuildId = info.BuildId,
            });
        }

        // The install worked but the session is not there anymore: land on this gamemode's own screen, so the player
        // is one click from another lobby instead of staring at a menu wondering what happened.
        private static void ShowInstalledButLobbyGone(GamemodeDescriptor desc, string message = null)
        {
            RejoinNotice.Hide();   // this screen IS the answer now; a "rejoining" overlay on top of it would contradict it
            try
            {
                EnsureClone();
                if (_cloneScreen == null) return;
                if (!_cloneScreen.IsOpen) { ShowGamemodeList(); _cloneScreen.Open(); }
                OpenGamemode(desc);
                ShowToastSafe(message ?? $"{desc.DisplayName} is installed - that session has closed, pick another one.");
            }
            catch (Exception e) { Core.Log?.Warning("[mp] could not show the post-install screen: " + e.Message); }
        }

        private static void ShowToastSafe(string message)
        {
            try { DooDesch.UI.Toast.Init(DialogRootStatic()); DooDesch.UI.Toast.Show(message, DooDesch.UI.Severity.Info); }
            catch { /* menu mid-transition */ }
        }
    }
}
