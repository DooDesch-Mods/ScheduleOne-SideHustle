using System;
using SideHustle.Profiles;   // MainThread

namespace SideHustle.Menu
{
    /// <summary>
    /// The one place a mod-sync (or a host mod-policy change) commits to relaunching the game.
    ///
    /// Both sync paths reached this point on their own and behaved differently: the gamemode join drew a notice, the
    /// vanilla sync relaunched with nothing on screen. Two runs over different routes are the whole reason the notice
    /// looked "intermittent" - it was never random, one route simply never had it.
    ///
    /// It also does NOT depend on which screen happens to be open. The old notice refused to draw unless the hub clone
    /// was open, and nothing owns that screen during a download - a right-click closes it while the worker keeps going
    /// and restarts anyway. This draws on its own overlay canvas, so the message cannot be taken away by navigation.
    ///
    /// The delay is load-bearing: RelaunchIntoSyncProfile quits within its own call, so a message built immediately
    /// before it never gets a frame to render in. It is also the window in which Cancel works - the relaunch cannot be
    /// taken back once it starts.
    /// </summary>
    internal static class CommittedRestart
    {
        // Long enough to read the finished list through the scrim and understand WHY the game is about to close.
        private const float ShowSeconds = 2.2f;

        private static OverlayNotice _notice;
        private static int _gen;
        private static Action _onCancel;

        /// <summary>True while the notice is up and the relaunch can still be called off.</summary>
        internal static bool Pending => _notice != null;

        /// <summary>Show "restarting to join", then run <paramref name="relaunch"/>. Always relaunches unless the player
        /// cancels, even if nothing could be drawn - a missing message must never strand the player short of the
        /// session they asked for. <paramref name="onCancel"/> puts the previous screen back.</summary>
        internal static void Then(string what, Action relaunch, Action onCancel = null)
        {
            // Put the screen behind into its finished state first: full bar, every row ticked off, no live Cancel. The
            // notice then reads as the next step of the same screen instead of contradicting it.
            SyncDownloadProgress.Complete();
            // Dim, not opaque. An opaque scrim leaves the player staring at a headline with a blank page under it and no
            // trace of the mods that were just fetched - the finished list IS the evidence that the restart is earned,
            // so it has to stay readable through the notice.
            //
            // No count in the line either. It used to print the PROFILE input count (5) beside a bar counting pending
            // DOWNLOADS (3), so the screen contradicted itself. The bar already says how many mods; this says what
            // happens next.
            Run("Restarting to join",
                $"Your mods are ready. The game restarts and rejoins {what} - don't close it.",
                0.62f, relaunch, onCancel);
        }

        /// <summary>The host's version: the game relaunches with the gamemode's required mods and opens the lobby.</summary>
        internal static void ForHost(Action relaunch, Action onCancel = null)
        {
            Run("Restarting with the required mods",
                "The game restarts and opens your lobby - don't close it.",
                0.9f, relaunch, onCancel);
        }

        private static void Run(string headline, string body, float dim, Action relaunch, Action onCancel)
        {
            if (relaunch == null)
            {
                Core.Log?.Warning("[sync] committed restart without a relaunch action - nothing to do.");
                return;
            }

            int gen = ++_gen;
            _onCancel = onCancel;
            CloseNotice();
            try
            {
                _notice = OverlayNotice.Build("SH_CommittedRestart", headline, body, dim);
                _notice.AddButton("Cancel", Cancel);
            }
            catch (Exception e)
            {
                _notice = null;
                Core.Log?.Warning("[sync] restart notice failed to draw: " + e.Message);
            }
            if (_notice == null) Core.Log?.Warning("[sync] restarting with no notice on screen - nothing was drawable.");

            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay((int)(ShowSeconds * 1000));
                MainThread.Post(() =>
                {
                    if (gen != _gen)
                    {
                        Core.Log?.Msg("[sync] the restart was cancelled before it started.");
                        return;
                    }
                    try { relaunch(); }
                    catch (Exception e) { Core.Log?.Error("[sync] relaunch failed: " + e); }
                });
            });
        }

        /// <summary>Call the restart off. A no-op once the relaunch has been handed to the process.</summary>
        internal static void Cancel()
        {
            if (_notice == null)
            {
                Core.Log?.Msg("[sync] cancel asked for, but no restart is pending.");
                return;
            }
            _gen++;
            var back = _onCancel;
            _onCancel = null;
            CloseNotice();
            Core.Log?.Msg("[sync] the player cancelled the restart.");
            try { back?.Invoke(); }
            catch (Exception e) { Core.Log?.Warning("[sync] the screen after a cancelled restart failed: " + e.Message); }
        }

        private static void CloseNotice()
        {
            var n = _notice;
            _notice = null;
            n?.Close();
        }
    }
}
