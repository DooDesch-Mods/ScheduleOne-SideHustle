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
    /// <c>SyncResolver.DownloadMissingAsync</c> reports explicit <see cref="Sync.SyncProgressEvent"/>s keyed by
    /// <see cref="Sync.DiffEntry.Key"/>: Started, Bytes, then Ready (hash verified) or Failed(reason). A row is
    /// finished only on Ready; nothing is inferred from the next file starting, so a failed or skipped download can no
    /// longer show up as done.
    ///
    /// Reports arrive on worker threads. Started / Ready / Failed are queued and applied in order on the main thread;
    /// Bytes are coalesced (hundreds a second on a fast line) so only the latest sample per key is painted, and only
    /// ONE main-thread post is in flight at a time.
    /// </summary>
    internal sealed class SyncDownloadProgress : IProgress<Sync.SyncProgressEvent>
    {
        private readonly InstallProgressView.Controller _ui;
        private readonly object _lock = new object();
        private readonly Queue<Sync.SyncProgressEvent> _steps = new Queue<Sync.SyncProgressEvent>();
        private Sync.SyncProgressEvent? _latestBytes;
        private bool _posted;

        private readonly HashSet<string> _pending;                 // keys that were Download when the sink was built
        private readonly HashSet<string> _ready = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly int _total;
        private static SyncDownloadProgress _active;

        internal SyncDownloadProgress(InstallProgressView.Controller ui, Sync.SyncDiff diff)
        {
            _ui = ui;
            // The DENOMINATOR is fixed now, before anything is fetched. Recomputing it later would shrink it as entries
            // complete, and a bar whose scale moves under it can go backwards.
            _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (diff != null)
                foreach (var e in diff.Entries)
                    if (e.Status == Sync.DiffStatus.Download) _pending.Add(e.Key);
            _total = _pending.Count;
            _active = this;
        }

        /// <summary>Per-frame hook from the hub. Painting is driven by events now; this only re-paints the bar so a
        /// frame that skipped a post still shows the current count.</summary>
        internal static void Tick()
        {
            var a = _active;
            if (a == null || a._ui == null || a._ui.Terminal || a._total <= 0) return;
            try { a.Paint(); }
            catch (Exception e) { Fault("painting the progress", e); }
        }

        /// <summary>
        /// Put the screen into its finished state, then retire the sink. Deterministic on purpose: whether a queued
        /// event lands before the restart notice is a race, and it lost once (the bar read "2 of 3" while the restart
        /// notice was already up). Every row has to read finished, not just the bar:
        /// <see cref="InstallProgressView.Controller.ShowSuccess"/> marks the plan rows off its own keys.
        /// </summary>
        internal static void Complete()
        {
            var a = _active;
            _active = null;
            if (a == null || a._ui == null) return;
            try
            {
                a._ready.UnionWith(a._pending);
                a.Paint();
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

        private void Paint()
        {
            int done = _ready.Count;
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
                string bad = _failed.Count > 0 ? $", {_failed.Count} failed" : "";
                _ui.Status.text = $"{done} of {_total} mods ready{bad}{cur}";
            }
        }

        public void Report(Sync.SyncProgressEvent value)
        {
            lock (_lock)
            {
                if (value.Kind == Sync.SyncProgressKind.Bytes) _latestBytes = value;
                else
                {
                    // A finished entry supersedes any pending byte sample of its own.
                    if (_latestBytes.HasValue && string.Equals(_latestBytes.Value.Key, value.Key, StringComparison.OrdinalIgnoreCase))
                        _latestBytes = null;
                    _steps.Enqueue(value);
                }
                if (_posted) return;
                _posted = true;
            }
            MainThread.Post(Drain);
        }

        private void Drain()
        {
            List<Sync.SyncProgressEvent> batch;
            Sync.SyncProgressEvent? bytes;
            lock (_lock)
            {
                batch = new List<Sync.SyncProgressEvent>(_steps);
                _steps.Clear();
                bytes = _latestBytes;
                _latestBytes = null;
                _posted = false;
            }
            try
            {
                foreach (var ev in batch) Apply(ev);
                if (bytes.HasValue) Apply(bytes.Value);
                if (_ui != null && !_ui.Terminal) Paint();
            }
            catch (Exception e) { Fault("applying a download report", e); }
        }

        private void Apply(Sync.SyncProgressEvent ev)
        {
            if (_ui == null || _ui.Terminal) return;
            if (string.IsNullOrEmpty(ev.Key)) return;
            switch (ev.Kind)
            {
                case Sync.SyncProgressKind.Started:
                    _ui.SyncStarted(ev.Key);
                    break;
                case Sync.SyncProgressKind.Bytes:
                    if (_ready.Contains(ev.Key) || _failed.Contains(ev.Key)) break;   // a late sample after the end
                    _ui.SyncBytes(ev.Key, ev.Done, ev.Total);
                    break;
                case Sync.SyncProgressKind.Ready:
                    if (_pending.Contains(ev.Key)) _ready.Add(ev.Key);
                    _failed.Remove(ev.Key);
                    _ui.SyncReady(ev.Key);
                    break;
                case Sync.SyncProgressKind.Failed:
                    _ready.Remove(ev.Key);
                    if (_pending.Contains(ev.Key)) _failed.Add(ev.Key);
                    _ui.SyncFailed(ev.Key, ev.Reason);
                    break;
            }
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
                string name = e.Key;
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
