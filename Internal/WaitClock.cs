using UnityEngine;

namespace SideHustle.Internal
{
    /// <summary>
    /// The time one frame adds to a "waiting for Steam" deadline.
    ///
    /// Steam callbacks only run between frames, so a single long frame (a scene load, a GC pause) is not Steam being
    /// slow. Counting it in full let a host started during the menu's own load hit its 10 s lobby deadline on the
    /// very next frame. Capping the step keeps the deadline about Steam's answer time, not about our hitches.
    /// </summary>
    internal static class WaitClock
    {
        /// <summary>The most one frame may add to a wait.</summary>
        internal const float MaxStep = 0.5f;

        internal static float Step => Mathf.Min(Time.unscaledDeltaTime, MaxStep);
    }
}
