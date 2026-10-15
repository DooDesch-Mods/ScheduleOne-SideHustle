#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using SideHustle.Profiles;

namespace SideHustle.Menu
{
    /// <summary>
    /// Debug-only entry points for the dev console: each opens one hub screen directly so it can be looked at
    /// without playing through the menu. Compiled out of Release.
    /// </summary>
    internal static partial class Hub
    {
        private static bool OpenHubForTest()
        {
            EnsureInit();
            EnsureClone();
            if (_cloneScreen == null) { Core.Log?.Warning("[dev] the gamemode screen is unavailable."); return false; }
            if (!_cloneScreen.IsOpen) { ShowGamemodeList(); _cloneScreen.Open(); }
            return true;
        }

        private static GamemodeDescriptor FindGamemode(string id)
        {
            var found = API.Registered.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
            if (found == null)
                Core.Log?.Warning($"[dev] no gamemode '{id}'. Registered: {string.Join(", ", API.Registered.Select(d => d.Id))}");
            return found;
        }

        /// <summary>Click row <paramref name="n"/> (1-based) of the screen on show, its right-click with "alt", or the
        /// screen's Back with "back". No argument lists the rows.</summary>
        internal static void ClickRowForTest(string[] parts)
        {
            if (parts.Length > 1 && parts[1].Equals("dismiss", StringComparison.OrdinalIgnoreCase))
            {
                // An alert's outside-click is its dismiss; press it the way a player would.
                var root = DialogRootStatic();
                var scrim = root != null ? root.Find("DD_AlertScrim") : null;
                var btn = scrim != null ? scrim.Find("catcher")?.GetComponent<UnityEngine.UI.Button>() : null;
                if (btn == null) { Core.Log?.Warning("[dev] shrow dismiss: no alert on show."); return; }
                Core.Log?.Msg("[dev] shrow: dismissing the alert.");
                btn.onClick.Invoke();
                return;
            }
            if (_cloneScreen == null || !_cloneScreen.IsOpen) { Core.Log?.Warning("[dev] shrow: the hub is not open."); return; }
            if (parts.Length > 2 && parts[1].Equals("press", StringComparison.OrdinalIgnoreCase))
            {
                // A form's footer button, found by its label: forms have no rows for "shrow <n>" to press.
                string label = string.Join(" ", parts, 2, parts.Length - 2);
                if (_formHost == null) { Core.Log?.Warning("[dev] shrow press: no form on show."); return; }
                foreach (var b in _formHost.GetComponentsInChildren<UnityEngine.UI.Button>(true))
                {
                    var ui = b.GetComponentInChildren<UnityEngine.UI.Text>(true);
                    var tmp = ui == null ? b.GetComponentInChildren<Il2CppTMPro.TMP_Text>(true) : null;
                    string text = ui != null ? ui.text : tmp != null ? tmp.text : null;
                    if (text == null || !text.Trim().Equals(label, StringComparison.OrdinalIgnoreCase)) continue;
                    Core.Log?.Msg($"[dev] shrow: pressing '{text}'.");
                    b.onClick.Invoke();
                    return;
                }
                Core.Log?.Warning($"[dev] shrow press: no button labelled '{label}' in the form.");
                return;
            }
            if (parts.Length > 1 && parts[1].Equals("back", StringComparison.OrdinalIgnoreCase))
            {
                Core.Log?.Msg("[dev] shrow: Back.");
                _back?.Invoke();
                return;
            }
            if (_shownRows == null) { Core.Log?.Warning("[dev] shrow: a form is on show, not rows."); return; }
            if (parts.Length < 2 || !int.TryParse(parts[1], out int n))
            {
                for (int i = 0; i < _shownRows.Count; i++)
                    Core.Log?.Msg($"[dev] row {i + 1}: '{_shownRows[i].Name}' - {_shownRows[i].Subtitle} [{_shownRows[i].Corner}]"
                                  + (_shownRows[i].OnSecondary != null ? " (right-click)" : ""));
                return;
            }
            if (n < 1 || n > _shownRows.Count) { Core.Log?.Warning($"[dev] shrow: no row {n}; {_shownRows.Count} on show."); return; }
            var row = _shownRows[n - 1];
            bool alt = parts.Length > 2 && parts[2].Equals("alt", StringComparison.OrdinalIgnoreCase);
            var act = alt ? row.OnSecondary : row.OnClick;
            if (act == null) { Core.Log?.Warning($"[dev] shrow: '{row.Name}' has no {(alt ? "right-click" : "click")} action."); return; }
            Core.Log?.Msg($"[dev] shrow: {(alt ? "right-click" : "click")} '{row.Name}'.");
            act();
        }

        internal static void OpenSettingsForTest()
        {
            if (OpenHubForTest()) ShowSettings();
        }

        /// <summary>The gamemode list with the catalogue rows, and what the catalogue decided for each entry.</summary>
        internal static void OpenCatalogueForTest()
        {
            if (!OpenHubForTest()) return;
            ShowGamemodeList();
            var installed = new HashSet<string>(API.Registered.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var e in GamemodeCatalogue.All)
                Core.Log?.Msg($"[dev] catalogue '{e.Id}' ({e.Package}): {(installed.Contains(e.Id) ? "installed, no row" : "not installed")}; "
                              + $"ShowUninstalledGamemodes={Config.Preferences.ShowUninstalledGamemodes}");
        }

        internal static void OpenHostFormForTest(string target)
        {
            if (!OpenHubForTest()) return;
            if (string.Equals(target, "vanilla", StringComparison.OrdinalIgnoreCase))
            {
                Core.Log?.Warning("[dev] opening the vanilla host form on a new game; its Host button really hosts.");
                ShowVanillaHostForm(null, "Test org");
                return;
            }
            var desc = FindGamemode(target);
            if (desc != null) ShowHostConfig(desc);
        }

        internal static void OpenBrowserForTest(string target)
        {
            if (!OpenHubForTest()) return;
            if (string.Equals(target, "vanilla", StringComparison.OrdinalIgnoreCase)) { ShowVanillaBrowser(); return; }
            var desc = FindGamemode(target);
            if (desc != null) ShowBrowser(desc);
        }

        /// <summary>A three-entry download that takes about ten seconds and can be cancelled, through the same
        /// run token, ESC guard and Stop question as a real sync. Nothing is fetched or installed.</summary>
        internal static void OpenFakeSyncForTest()
        {
            if (!OpenHubForTest()) return;
            var ct = SyncRun.Begin();
            _syncBack = ShowGamemodeList;
            _back = null;
            ClearFormHost();
            SetTmp(_clone.transform, "Title", "Joining the host");
            var host = CreateFormHost("SH_Syncing");
            var names = new[] { "Fake-ModA 1.0.0", "Fake-ModB 2.1.0", "Fake-ModC 0.3.2" };
            const long size = 3_000_000;
            var plan = names.Select(n => new InstallPlanRow { FullName = n.Split(' ')[0], Version = n.Split(' ')[1], Key = n, Size = size }).ToList();
            var ui = InstallProgressView.Build(host, "Matching the host's mods", plan, null,
                onCancel: () => StopSyncDownload("cancel button"));
            _syncUi = ui;

            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < names.Length; i++)
                    {
                        int index = i;
                        for (int step = 1; step <= 10; step++)
                        {
                            await System.Threading.Tasks.Task.Delay(330, ct);
                            long done = size * step / 10;
                            MainThread.Post(() =>
                            {
                                if (ct.IsCancellationRequested) return;
                                ui.Report(new ProfileProgress
                                {
                                    Phase = "Downloading", CurrentItem = names[index], Done = index, Total = names.Length,
                                    BytesDone = done, BytesTotal = size
                                });
                            });
                        }
                    }
                }
                catch (OperationCanceledException) { Core.Log?.Msg("[dev] the fake download was cancelled."); return; }

                MainThread.Post(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    SyncRun.End(ct);
                    _syncBack = null;
                    ui.ShowSuccess("All 3 mods are ready. (Fake download, nothing installed.)");
                    Core.Log?.Msg("[dev] the fake download finished.");
                });
            });
        }

        /// <summary>The "restarting to join" notice with its Cancel. The relaunch action only logs.</summary>
        internal static void OpenFakeRestartForTest()
        {
            if (!OpenHubForTest()) return;
            CommittedRestart.Then("a test lobby",
                () => Core.Log?.Msg("[dev] the fake restart would relaunch the game now."),
                onCancel: () => { Core.Log?.Msg("[dev] the fake restart was cancelled."); ShowGamemodeList(); });
        }
    }
}
#endif
