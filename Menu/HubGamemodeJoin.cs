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
        private static void ShowGhostMissingMod(Ghost g)
        {
            _back = ShowGamemodeList;
            bool canLookUp = DownloadLink.IsAllowed(g.Url) || Sync.NexusLookup.CanLookUp(g.Name);
            ShowRows(g.Name, new List<Row>
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
            });
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
                ProfilesViews.BuildBigStatus(host, "INSTALLING AND RESTARTING",
                    "The game restarts with the gamemode installed and joins the lobby on its own - hang tight. Don't close the game.");
            }

            System.Threading.Tasks.Task.Run(async () =>
            {
                bool allFetched = false;
                try { allFetched = await SyncResolver.DownloadMissingAsync(diff, null, System.Threading.CancellationToken.None); }
                catch (Exception e) { Core.Log?.Warning("[mp] gamemode join: downloads failed: " + e.Message); }

                var inputs = SyncResolver.ToInputs(diff);
                SyncResolver.ResolveExtras(diff, out var pluginInputs, out var userLibInputs);
                MainThread.Post(() =>
                {
                    // The gamemode's own mod is the point of the whole flow: without it a restart lands the player in
                    // the menu with nothing gained, so stop here and say why instead.
                    string ownFile = "";
                    try { ownFile = Il2CppSteamworks.SteamMatchmaking.GetLobbyData(new Il2CppSteamworks.CSteamID(row.LobbyId), LobbyCoordinator.KeyGamemodeFile); } catch { }
                    bool Resolved(DiffEntry e) => e.Status == DiffStatus.Present || e.Status == DiffStatus.Cached;
                    bool ok = string.IsNullOrEmpty(ownFile)
                        ? diff.Entries.Any(Resolved)
                        : diff.Entries.Any(e => Resolved(e) && string.Equals(e.Mod.File, ownFile, StringComparison.OrdinalIgnoreCase));
                    if (!ok)
                    {
                        Core.Log?.Error("[mp] gamemode join: the gamemode's own mod could not be installed; staying in the menu.");
                        ShowGhostMissingMod(g);
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
                    Mods.ModSwitcher.RelaunchIntoSyncProfile("gmjoin-" + g.GamemodeId, inputs, tokens, prefsOverlay: null,
                        logLabel: $"installing {inputs.Count} mod(s) to join '{g.Name}'", pluginInputs, userLibInputs);
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
                return;
            }

            var desc = API.Registered.FirstOrDefault(d => string.Equals(d.Id, gmId, StringComparison.OrdinalIgnoreCase));
            if (desc == null)
            {
                Core.Log?.Warning($"[mp] '{gmId}' is still not registered after the install; staying in the menu.");
                if (_cloneScreen != null && !_cloneScreen.IsOpen) { ShowGamemodeList(); _cloneScreen.Open(); }
                return;
            }

            // This is a FRESH process: it has no cached lobby data yet, so reading metadata right away returns blanks
            // and every check would pass vacuously. Ask Steam and retry until the lobby actually answers - then verify
            // it is still the same gamemode lobby before entering it.
            try { Il2CppSteamworks.SteamMatchmaking.RequestLobbyData(new Il2CppSteamworks.CSteamID(lobbyId)); } catch { }
            VerifyThenJoin(desc, lobbyId, wantHash, 0);
        }

        /// <summary>How long the post-install join waits for the lobby to answer before giving up (7 x 700ms).</summary>
        private const int RejoinDataAttempts = 7;

        private static void VerifyThenJoin(GamemodeDescriptor desc, ulong lobbyId, string wantHash, int attempt)
        {
            var info = LobbyCoordinator.ReadInfo(lobbyId);
            string liveHash = "";
            try { liveHash = Il2CppSteamworks.SteamMatchmaking.GetLobbyData(new Il2CppSteamworks.CSteamID(lobbyId), VanillaLobby.KeyMHash); } catch { }
            // "Answered" means the keys we verify against are actually there. A half-propagated lobby (id present,
            // hash still empty) must keep waiting, otherwise the mod-set check passes vacuously.
            bool answered = !string.IsNullOrEmpty(info.GamemodeId)
                            && (string.IsNullOrEmpty(wantHash) || !string.IsNullOrEmpty(liveHash));
            if (!answered)
            {
                if (attempt < RejoinDataAttempts)
                {
                    try { Il2CppSteamworks.SteamMatchmaking.RequestLobbyData(new Il2CppSteamworks.CSteamID(lobbyId)); } catch { }
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
