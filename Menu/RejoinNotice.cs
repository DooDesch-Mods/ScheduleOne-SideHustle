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
    ///
    /// It also owns two things no single step can: the way out, and staying out of the game's way. The overlay draws at
    /// sorting order 32000 over a 0.9 scrim, so a vanilla popup underneath it is invisible - and a rejoin that never
    /// resolves used to leave the player with a headline, no explanation and no button.
    /// </summary>
    internal static class RejoinNotice
    {
        /// <summary>How long the player watches before a way out appears. Short enough not to feel trapped, long
        /// enough that a rejoin which is simply working never grows a button nobody needed.</summary>
        private const float CancelAfter = 8f;

        private static OverlayNotice _notice;
        private static Action _cancel;
        private static float _shown;
        private static bool _cancelOffered;

        internal static bool Visible => _notice != null;

        /// <summary>Show the notice, or update its line if it is already up.</summary>
        internal static void Show(string line)
        {
            try
            {
                if (_notice == null)
                {
                    _notice = OverlayNotice.Build("SH_RejoinNotice", "REJOINING", line, 0.9f);
                    _shown = 0f;
                    _cancelOffered = false;
                }
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

        /// <summary>
        /// Hand the notice the way out for whatever step currently owns it. Set by each step as it takes over, so the
        /// button always does the right thing for where the rejoin actually is: dropping a payload that has not fired
        /// yet is not the same as leaving a lobby we already joined.
        /// </summary>
        internal static void SetCancel(Action onCancel) => _cancel = onCancel;

        /// <summary>
        /// Pumped every frame. Two jobs: reveal the way out once the wait stops looking normal, and get out of the
        /// way of the game's own popup. Vanilla answers a version mismatch with a MainMenuPopup and then leaves the
        /// lobby - underneath this overlay that popup is invisible, which is how a refused join could look exactly
        /// like a silent one.
        /// </summary>
        internal static void Tick(float dt)
        {
            var notice = _notice;
            if (notice == null) { _shown = 0f; _cancelOffered = false; return; }

            notice.SetVisible(!VanillaPopupOpen());

            _shown += dt;
            if (_cancelOffered || _shown < CancelAfter || _cancel == null) return;
            _cancelOffered = true;
            notice.AddButton("Cancel and go back", () =>
            {
                var cancel = _cancel;
                Core.Log?.Msg("[sync] the player cancelled the rejoin.");
                Hide();
                cancel?.Invoke();
            });
        }

        private static bool VanillaPopupOpen()
        {
            try
            {
                var popup = Il2CppScheduleOne.DevUtilities.Singleton<Il2CppScheduleOne.UI.MainMenu.MainMenuPopup>.Instance;
                return popup != null && popup.Screen != null && popup.Screen.IsOpen;
            }
            catch { return false; }   // not spawned yet, or the type moved - never let this hide the notice
        }

        internal static void Hide()
        {
            var n = _notice;
            _notice = null;
            _cancel = null;
            _shown = 0f;
            _cancelOffered = false;
            n?.Close();
        }
    }
}
