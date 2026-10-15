using UnityEngine;

namespace SideHustle.Multiplayer
{
    /// <summary>
    /// Decides when a world load should be given up on. A load must not be policed by a single flat deadline. The
    /// load reports its progress only as coarse phase changes, and one phase - LoadingScene, which wraps Unity's
    /// LoadSceneAsync - reports nothing at all until it is done. A flat timeout therefore does not measure "stuck", it
    /// measures "slower than my machine": weak PCs and slow connections got aborted mid-load, and because the abort
    /// left the loading screen open that looked like an infinite loading screen.
    ///
    /// So: give the blind scene-loading phase a generous budget of its own, and police every other phase by how long
    /// it has sat WITHOUT changing. A genuinely dead load still ends, just not a merely slow one. Both the gamemode
    /// and the vanilla co-op coordinators use one instance each.
    /// </summary>
    internal sealed class LoadWatchdog
    {
        private const float StallTimeout = 90f;    // no phase change at all -> really stuck
        private const float SceneTimeout = 420f;   // LoadingScene is opaque; only a hard ceiling fits
        private const float HardCeiling = 900f;    // last resort, whatever the phase claims

        private float _total;
        private float _stall;
        private string _lastProgress;

        /// <summary>Start a new wait (a new load is not the previous one's stall).</summary>
        internal void Reset()
        {
            _total = 0f;
            _stall = 0f;
            _lastProgress = null;
        }

        /// <summary>
        /// Advance by one frame and report whether this load should be given up on. Progress is any change to the
        /// scene, the load phase, or the phase's own detail text - the last one matters because the syncing phase
        /// names the task it is replicating, so a slow but healthy sync keeps resetting the stall timer.
        /// </summary>
        internal bool HasGivenUp(out string why)
        {
            float dt = Time.unscaledDeltaTime;
            _total += dt;
            string progress = WorldBoot.ProgressSignature();
            if (progress != _lastProgress) { _lastProgress = progress; _stall = 0f; }
            else _stall += dt;

            bool blindScenePhase = WorldBoot.IsLoadingScene;
            float limit = blindScenePhase ? SceneTimeout : StallTimeout;

            if (_stall > limit)
            {
                why = blindScenePhase
                    ? $"scene load exceeded {SceneTimeout:F0}s (scene={WorldBoot.CurrentScene}, status={WorldBoot.LoadStatus})"
                    : $"no progress for {StallTimeout:F0}s (scene={WorldBoot.CurrentScene}, status={WorldBoot.LoadStatus})";
                return true;
            }
            if (_total > HardCeiling)
            {
                why = $"load exceeded {HardCeiling:F0}s (scene={WorldBoot.CurrentScene}, status={WorldBoot.LoadStatus})";
                return true;
            }
            why = null;
            return false;
        }
    }
}
