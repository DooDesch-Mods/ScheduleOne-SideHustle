using System;

namespace SideHustle.Menu
{
    /// <summary>
    /// What the player looks at between the mod-sync restart and the session.
    ///
    /// That gap was silent and it is long: 90 frames before the rejoin even starts, then up to 20s of lobby discovery
    /// (vanilla) or seven 700ms lobby-data attempts (gamemode), all of it logged and none of it on screen. The game had
    /// just closed and reopened itself, so an idle main menu is exactly what a failed restart would look like too.
    ///
    /// One surface for both paths, driven by whoever owns the step: the resolver latches it, the coordinators narrate it,
    /// and it comes down the moment the world load takes over (the game's own loading screen is the better signal from
    /// there) or a failure surfaces its own screen.
    /// </summary>
    internal static class RejoinNotice
    {
        private static OverlayNotice _notice;

        internal static bool Visible => _notice != null;

        /// <summary>Show the notice, or update its line if it is already up.</summary>
        internal static void Show(string line)
        {
            try
            {
                if (_notice == null) _notice = OverlayNotice.Build("SH_RejoinNotice", "REJOINING", line, 0.9f);
                else _notice.SetBody(line);
            }
            catch (Exception e) { Core.Log?.Warning("[sync] rejoin notice failed to draw: " + e.Message); }
        }

        /// <summary>Update the line only if the notice is already up - for progress narration that must not conjure a
        /// notice on a path that never started a rejoin.</summary>
        internal static void Update(string line)
        {
            if (_notice != null) _notice.SetBody(line);
        }

        internal static void Hide()
        {
            var n = _notice;
            _notice = null;
            n?.Close();
        }
    }
}
