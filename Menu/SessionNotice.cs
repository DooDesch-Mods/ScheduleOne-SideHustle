using System;
using UnityEngine;
using DooDesch.UI;
using SideHustle.Config;

namespace SideHustle.Menu
{
    /// <summary>
    /// Tells the player why a session ended badly, and keeps telling them until they acknowledge it.
    ///
    /// Every abort path already knew the reason - <c>MultiplayerCoordinator.AbortToHub("join did not complete...")</c>,
    /// the sync abort, the kicked-client recovery - and every one of them wrote it to the log and nowhere else. To the
    /// player that reads as the game giving up: a loading screen, a long wait, then the main menu with no explanation.
    ///
    /// Two things make it actually arrive:
    ///
    ///  - It is PERSISTED, not held in a field. Half the ways a session dies end in a restart (the mod-policy
    ///    relaunch, a sync profile switch), and anything kept in memory dies with the process - which is precisely
    ///    the case where the player is most confused about what just happened.
    ///  - It is a DIALOG the player dismisses, not a toast that fades. A message about a failure that vanishes on
    ///    its own is only marginally better than no message, and it is trivially missed while a loading screen is
    ///    still fading out.
    /// </summary>
    internal static class SessionNotice
    {
        private static bool _showing;

        /// <summary>Remember why the session ended. The newest reason wins: an abort chain can report a symptom
        /// after its cause, and the last thing to go wrong is the one worth showing.</summary>
        internal static void Set(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return;
            try { Preferences.LastSessionError = reason; } catch { }
        }

        /// <summary>
        /// Pumped every frame from Core.OnUpdate - deliberately NOT gated on "are we in the menu".
        ///
        /// That gate is what swallowed this the first time round: a stalled join is aborted by closing the loading
        /// screen, without ever re-initialising the Menu scene, so the in-menu flag stays false and nothing that
        /// depends on it ever runs again. The dialog canvas is DontDestroyOnLoad and survives on its own.
        /// </summary>
        internal static void Tick()
        {
            if (_showing) return;
            string reason;
            try { reason = Preferences.LastSessionError; } catch { return; }
            if (string.IsNullOrEmpty(reason)) return;

            // Only surface it once there is somewhere to draw AND no world running - mid-session is the wrong
            // moment for a modal about a session that already ended.
            try { if (Multiplayer.WorldBoot.IsInGame) return; } catch { }

            Transform root;
            try { root = Hub.DialogRootStatic(); } catch { return; }
            if (root == null) return;

            _showing = true;
            string text = Sentence(reason);
            string title = Title(reason);
            try
            {
                // An alert, not a choice: there is nothing here to cancel, and a second button would only make the
                // reader hunt for a difference between it and OK.
                Components.AlertDialog(root, title, text, "OK", () => Dismiss());
                Core.Log?.Msg($"[notice] shown to the player: {title} - {text}");
            }
            catch (Exception e)
            {
                // Never let a UI failure strand the message forever - drop it rather than block every future one.
                Core.Log?.Warning("[notice] could not show \"" + text + "\": " + e.Message);
                Dismiss();
            }
        }

        private static void Dismiss()
        {
            _showing = false;
            try { Preferences.LastSessionError = ""; } catch { }
        }

        /// <summary>Forget a pending notice without showing it (a fresh, deliberate session start).</summary>
        internal static void Clear() => Dismiss();

        /// <summary>Name what actually happened. "Session ended" on a join that never started is not just vague,
        /// it is wrong - nothing had begun to end.</summary>
        private static string Title(string reason)
        {
            string lower = (reason ?? "").ToLowerInvariant();
            if (lower.Contains("no progress") || lower.Contains("did not complete")
                || lower.Contains("timed out") || lower.Contains("timeout")) return "Could not join";
            if (lower.Contains("kick")) return "Removed from the session";
            if (lower.Contains("lost") || lower.Contains("disconnect")) return "Disconnected";
            if (lower.Contains("could not create a lobby")) return "Could not host";
            if (lower.Contains("world boot")) return "World failed to load";
            return "Session ended";
        }

        /// <summary>
        /// The one line the player reads. Kept short and free of guesswork on purpose.
        ///
        /// Shipped games write these as a named failure plus at most one concrete fact - "Failed to join the
        /// session. No response from host.", "Unable to connect to the other player." Comprehension research puts
        /// the useful ceiling around fourteen words. The earlier draft here ran to forty and spent most of them
        /// speculating about a cause this code cannot actually determine, which tells the player nothing and
        /// invites them to chase the wrong fix.
        ///
        /// So: say what failed, add an action when there is one, and stop.
        /// </summary>
        private static string Sentence(string reason)
        {
            string r = (reason ?? "").Trim();
            if (r.Length == 0) return "Try again.";
            string lower = r.ToLowerInvariant();

            if (lower.Contains("no progress") || lower.Contains("did not complete")
                || lower.Contains("timed out") || lower.Contains("timeout"))
                return "No response from the host. Try again.";
            if (lower.Contains("kick")) return "The host removed you from their session.";
            if (lower.Contains("lost") || lower.Contains("disconnect")) return "Try rejoining from the session list.";
            if (lower.Contains("could not create a lobby")) return "Steam did not open a lobby. Try again.";
            if (lower.Contains("world boot")) return "The world could not be loaded.";
            // Deliberately states the OBSERVATION, not a cause. All this path knows is that we believed we were
            // connected and are now back in the menu without the clean exit having run. Whether the host closed the
            // session, kicked us, or the connection died is not distinguishable from here - and claiming one of them
            // would send the player after the wrong fix.
            if (lower.Contains("stopped unexpectedly")) return "Try rejoining from the session list.";

            // Unknown reasons are still shown rather than swallowed - just tidied into a sentence.
            return char.ToUpperInvariant(r[0]) + r.Substring(1) + (r.EndsWith(".") ? "" : ".");
        }
    }
}
