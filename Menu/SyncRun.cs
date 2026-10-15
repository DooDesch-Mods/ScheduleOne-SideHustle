using System;
using System.Threading;

namespace SideHustle.Menu
{
    /// <summary>
    /// The cancellation handle of the mod-sync download that is running right now.
    ///
    /// A sync download used to run with <see cref="CancellationToken.None"/>: once it started, the only ways out were
    /// waiting or killing the game. Each run now gets its own source. Cancelling it makes the download stop and the
    /// continuation drop out without building a profile or restarting.
    ///
    /// A run is identified by its token, not by a flag. A cancelled run can still be unwinding on its worker thread when
    /// the player starts a new one, and the old continuation must not clear or act on the new run's state.
    /// </summary>
    internal static class SyncRun
    {
        private static CancellationTokenSource _cts;

        /// <summary>True while a sync download is in flight. The hub uses it to ask before leaving the screen.</summary>
        internal static bool Active => _cts != null;

        /// <summary>Start a run. Any run still registered is cancelled first so two downloads never share the screen.</summary>
        internal static CancellationToken Begin()
        {
            if (_cts != null)
            {
                Core.Log?.Warning("[sync] a new download started while another was registered - cancelling the old one.");
                CancelQuietly(_cts);
            }
            _cts = new CancellationTokenSource();
            return _cts.Token;
        }

        /// <summary>Stop the current run. Returns false when none was running.</summary>
        internal static bool Cancel(string reason)
        {
            var cts = _cts;
            if (cts == null)
            {
                Core.Log?.Msg("[sync] cancel asked for, but no download is running.");
                return false;
            }
            _cts = null;
            Core.Log?.Msg("[sync] download cancelled: " + reason);
            CancelQuietly(cts);
            return true;
        }

        /// <summary>The run behind <paramref name="token"/> is over (finished, failed or cancelled). A newer run is left alone.</summary>
        internal static void End(CancellationToken token)
        {
            var cts = _cts;
            if (cts == null || cts.Token != token) return;
            _cts = null;
            try { cts.Dispose(); }
            catch (Exception e) { Core.Log?.Warning("[sync] could not release the download token: " + e.Message); }
        }

        private static void CancelQuietly(CancellationTokenSource cts)
        {
            try { cts.Cancel(); }
            catch (Exception e) { Core.Log?.Warning("[sync] a download cancel handler threw: " + e.Message); }
        }
    }
}
