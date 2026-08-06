using System;
using SideHustle.Profiles;   // MainThread

namespace SideHustle.Menu
{
    /// <summary>
    /// The one place a mod-sync commits to relaunching the game.
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
    /// before it never gets a frame to render in.
    /// </summary>
    internal static class CommittedRestart
    {
        // Long enough to read the finished list through the scrim and understand WHY the game is about to close.
        private const float ShowSeconds = 2.2f;

        /// <summary>Show "restarting to join", then run <paramref name="relaunch"/>. Always relaunches, even if nothing
        /// could be drawn - a missing message must never strand the player short of the session they asked for.</summary>
        internal static void Then(string what, Action relaunch)
        {
            if (relaunch == null) return;
            // Put the screen behind into its finished state first: full bar, every row ticked off, no live Cancel. The
            // notice then reads as the next step of the same screen instead of contradicting it.
            SyncDownloadProgress.Complete();
            bool drawn = false;
            try { drawn = Draw(what); }
            catch (Exception e) { Core.Log?.Warning("[sync] restart notice failed to draw: " + e.Message); }

            if (!drawn) Core.Log?.Warning("[sync] restarting with no notice on screen - nothing was drawable.");

            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay((int)(ShowSeconds * 1000));
                MainThread.Post(() => { try { relaunch(); } catch (Exception e) { Core.Log?.Error("[sync] relaunch failed: " + e); } });
            });
        }

        private static bool Draw(string what)
        {
            // Dim, not opaque. An opaque scrim leaves the player staring at a headline with a blank page under it and no
            // trace of the mods that were just fetched - the finished list IS the evidence that the restart is earned,
            // so it has to stay readable through the notice.
            //
            // No count in the line either. It used to print the PROFILE input count (5) beside a bar counting pending
            // DOWNLOADS (3), so the screen contradicted itself. The bar already says how many mods; this says what
            // happens next.
            OverlayNotice.Build("SH_CommittedRestart", "RESTARTING TO JOIN",
                $"Your mods are ready. The game restarts and rejoins {what} on its own - don't close it.", 0.62f);
            return true;
        }
    }
}
