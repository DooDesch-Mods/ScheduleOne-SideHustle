using System;
using System.Collections.Generic;
using System.Linq;
using SideHustle.Profiles;   // MainThread

namespace SideHustle.Menu
{
    /// <summary>
    /// Drives the shared <see cref="InstallProgressView"/> from a mod-SYNC download, so a player waiting for a
    /// gamemode's mods sees which file is coming down and how far along it is.
    ///
    /// Both sync paths used to pass <c>null</c> for progress and show a static "INSTALLING AND RESTARTING" card, so the
    /// screen said nothing during the one part of the flow that actually takes time - the exact moment a player starts
    /// wondering whether it has hung. The mod-profile installer already had the whole progress UI; only the sink was
    /// missing here.
    ///
    /// A separate class from the profile installer's own sink because the two report DIFFERENT shapes:
    /// <c>SyncResolver.DownloadMissingAsync</c> reports <c>(Label, Done, Total)</c> bytes per file, while the profile
    /// engine reports its own ProfileProgress. Sharing one adapter would mean inventing a common type for two things
    /// that genuinely differ.
    ///
    /// Coalescing matters: a download reports on every buffer, which is hundreds of callbacks a second on a fast line.
    /// Only ONE main-thread post is in flight at a time and it always renders the LATEST sample, so the bar keeps up
    /// without queueing a frame's worth of stale updates behind it.
    /// </summary>
    internal sealed class SyncDownloadProgress : IProgress<(string Label, long Done, long Total)>
    {
        private readonly InstallProgressView.Controller _ui;
        private readonly object _lock = new object();
        private (string Label, long Done, long Total) _latest;
        private bool _posted;

        private readonly Sync.SyncDiff _diff;
        private readonly int _total;
        private static SyncDownloadProgress _active;

        internal SyncDownloadProgress(InstallProgressView.Controller ui, Sync.SyncDiff diff)
        {
            _ui = ui;
            _diff = diff;
            // The DENOMINATOR is fixed now, before anything is fetched. Recomputing it later would shrink it as entries
            // complete, and a bar whose scale moves under it can go backwards.
            _total = diff == null ? 0 : diff.Entries.Count(e => e.Status == Sync.DiffStatus.Download);
            _active = this;
        }

        /// <summary>
        /// Pumped per frame from the hub. Counts how many mods have actually LANDED - an entry leaves
        /// DiffStatus.Download only after its bytes passed hash verification and were promoted into the cache.
        ///
        /// Bytes are not usable as the source here: GitHub reports (file, 0, 0) and never reports a size or a
        /// completion, so a byte-driven bar sits at zero for exactly the mods this flow fetches most. Counting verified
        /// mods needs no sizes, cannot run backwards, and cannot claim progress that did not happen.
        /// </summary>
        internal static void Tick()
        {
            var a = _active;
            if (a == null || a._ui == null || a._ui.Terminal || a._total <= 0 || a._diff == null) return;
            try
            {
                int done = a._diff.Entries.Count(e => e.Status != Sync.DiffStatus.Download);
                done -= (a._diff.Entries.Count - a._total);   // entries that were never pending do not count as progress
                if (done < 0) done = 0;
                if (done > a._total) done = a._total;
                a.Paint(done);
            }
            catch (Exception e) { Fault("counting verified mods", e); }
        }

        /// <summary>
        /// Put the screen into its finished state, then retire the sink. Deterministic on purpose: the last entry's
        /// status flips after the download loop returns, so whether a polled tick lands before the restart is a race -
        /// and it lost, which is why the bar read "2 of 3" while the restart notice was already up. An artificial delay
        /// would hide that race rather than remove it.
        ///
        /// This is also the state the player keeps LOOKING at while the restart notice sits over it, so every row has
        /// to read finished, not just the bar: <see cref="InstallProgressView.Controller.ShowSuccess"/> marks the plan
        /// rows off its own keys, which is what the byte-driven path could never do (the resolver reports a DLL file
        /// name, the rows are keyed by display name).
        /// </summary>
        internal static void Complete()
        {
            var a = _active;
            _active = null;
            if (a == null || a._ui == null) return;
            try
            {
                a.Paint(a._total);
                a._ui.ShowSuccess(a._total > 0
                    ? $"{a._total} of {a._total} mods ready"
                    : "Mods ready");
            }
            catch (Exception e) { Fault("finishing the progress view", e); }
        }

        internal static void Clear() => _active = null;

        /// <summary>
        /// Report a fault in the progress plumbing ONCE.
        ///
        /// These used to be empty catches, and that is precisely why five rounds of fixes to this bar each looked like
        /// they had worked: a throw inside the painter shows the same thing as a bar with nothing to say. Once, not per
        /// frame - Tick runs every frame and a broken sink would otherwise bury the log it is supposed to explain.
        /// </summary>
        private static bool _faulted;
        private static void Fault(string what, Exception e)
        {
            if (_faulted) return;
            _faulted = true;
            Core.Log?.Warning($"[sync] progress view fault while {what}: {e.Message}");
        }

        private void Paint(int done)
        {
            if (_ui.BarFill != null)
            {
                float f = _total > 0 ? (float)done / _total : 0f;
                var rt = _ui.BarFill.rectTransform;
                rt.anchorMin = new UnityEngine.Vector2(0f, 0f);
                rt.anchorMax = new UnityEngine.Vector2(UnityEngine.Mathf.Clamp01(f), 1f);
                rt.offsetMin = UnityEngine.Vector2.zero;
                rt.offsetMax = UnityEngine.Vector2.zero;
            }
            if (_ui.Status != null)
            {
                string cur = done >= _total || string.IsNullOrEmpty(_ui.ActiveKey) ? "" : "  -  " + _ui.ActiveKey;
                _ui.Status.text = $"{done} of {_total} mods ready{cur}";
            }
        }

        public void Report((string Label, long Done, long Total) value)
        {
            lock (_lock)
            {
                _latest = value;
                if (_posted) return;
                _posted = true;
            }
            MainThread.Post(() =>
            {
                (string Label, long Done, long Total) s;
                lock (_lock) { s = _latest; _posted = false; }
                try { Apply(s); } catch (Exception e) { Fault("applying a download report", e); }
            });
        }

        /// <summary>A report only says which file is being fetched now. What counts as DONE is decided by the tick
        /// above, from verified status - a start is not a completion, and the coalescer above may drop any single
        /// report, so nothing that matters may be inferred from one arriving.</summary>
        private void Apply((string Label, long Done, long Total) s)
        {
            if (_ui == null || _ui.Terminal) return;
            _ui.ActiveKey = s.Label ?? "";
        }

        /// <summary>Turn a diff into the plan rows the view lists, so the player sees every file up front instead of
        /// discovering them one at a time.</summary>
        internal static List<InstallPlanRow> PlanFrom(Sync.SyncDiff diff)
        {
            var rows = new List<InstallPlanRow>();
            if (diff == null) return rows;
            foreach (var e in diff.Entries)
            {
                if (e == null || e.Mod == null) continue;
                if (e.Status != Sync.DiffStatus.Download && e.Status != Sync.DiffStatus.Cached) continue;
                string name = e.Mod.Name ?? e.Mod.File ?? "mod";
                rows.Add(new InstallPlanRow
                {
                    FullName = name,
                    Version = e.Mod.Version ?? "",
                    Size = 0,                                    // the sync resolver reports totals as it goes
                    Cached = e.Status == Sync.DiffStatus.Cached,
                    Key = name,
                });
            }
            return rows;
        }
    }
}
