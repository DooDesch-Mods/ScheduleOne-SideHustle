using System;
using System.Collections.Generic;
using SideHustle.Sync;

namespace SideHustle.Menu
{
    /// <summary>
    /// The join path on the hub page: the sync check, the manual installs, the download and the restart. Each
    /// helper answers false when the page cannot take the screen, and the caller builds the classic view instead.
    /// </summary>
    internal static partial class Hub
    {
        private static bool ShowConsentOnPage(string tab, string title, SyncManifest manifest, SyncDiff diff, bool enforced, bool hasPrefs,
            ulong hostSteamId, string hostName, bool hostAcceptsMessages, Action onSyncJoin, Action onPlainJoin, Action onBack,
            string backLabel, string enforcedNote = null)
        {
            if (!ShowOnPage("join.consent", tab, pv => SyncConsentView.BuildPage(pv, title, manifest, diff, enforced, hasPrefs,
                    hostSteamId, hostName, hostAcceptsMessages, onSyncJoin, onPlainJoin, onBack, backLabel, enforcedNote)))
                return false;
            SyncManualInstallView.PrefetchLinks(diff);
            return true;
        }

        private static bool ShowManualOnPage(string tab, string title, SyncDiff diff, ulong hostSteamId, string hostName,
            bool hostAcceptsMessages, string backLabel, Action onContinue, Action onBack)
        {
            if (!ShowOnPage("join.manual", tab, pv => SyncManualInstallView.BuildPage(pv, title, hostSteamId, hostName,
                    hostAcceptsMessages, backLabel, onContinue, onBack)))
                return false;
            // After ShowOnPage: its screen swap closes any checklist that was open before.
            SyncManualInstallView.Begin(diff, hostSteamId, hostName, hostAcceptsMessages);
            return true;
        }

        /// <summary>The download as a page screen. Null when the page cannot take it.</summary>
        private static InstallProgressView.Controller ShowDownloadOnPage(string tab, string title, string sub, string headline,
            IReadOnlyList<InstallPlanRow> plan, IReadOnlyList<string> unresolved, Action onCancel)
        {
            var c = InstallProgressView.Controller.ForPage(headline, plan, unresolved, onCancel);
            c.PageTitle = title;
            c.PageSub = sub;
            return ShowOnPage("join.download", tab, pv => c.BuildPage(pv)) ? c : null;
        }

        /// <summary>"Restarting to join" on the page, with the finished download's bar. False: use the overlay.</summary>
        internal static bool ShowRestartOnPage(string headline, string body, Action cancel)
        {
            if (!HubReady || !ScreenOpen)
            {
                Core.Log?.Msg("[hub3] restart notice: the hub page is not up; using the overlay.");
                return false;
            }
            var done = _syncUi ?? _installUi;
            string label = done?.StatusText;
            int pct = done != null ? done.BarPercent : -1;
            string tab = _pageTab ?? "lobbies";
            return ShowOnPage("join.restart", tab, pv =>
            {
                pv.Frame = "board";
                pv.Lock = true;
                pv.Add(pv.Big("refresh", headline, body, pct, label, "done", pv.Btn("Cancel", cancel)));
            });
        }
    }
}
