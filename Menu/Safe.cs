using System;
using System.Collections.Generic;

namespace SideHustle.Menu
{
    /// <summary>
    /// Calls that are allowed to fail without stopping the screen (a destroyed UI object, a Steam call before Steam is
    /// up, a toast with no canvas) but never without a trace. Each failure is logged as a warning; a given step logs
    /// only its first failure, so a call made every frame does not flood the log.
    /// </summary>
    internal static class Safe
    {
        private static readonly HashSet<string> _logged = new HashSet<string>();

        /// <summary>Run <paramref name="action"/>; a throw is logged as a warning naming <paramref name="what"/>.</summary>
        internal static bool Try(string what, Action action)
        {
            try { action(); return true; }
            catch (Exception e) { Report(what, e); return false; }
        }

        /// <summary>Run <paramref name="func"/>; a throw is logged and <paramref name="fallback"/> is returned.</summary>
        internal static T Try<T>(string what, Func<T> func, T fallback)
        {
            try { return func(); }
            catch (Exception e) { Report(what, e); return fallback; }
        }

        /// <summary>Log a tolerated failure the way <see cref="Try(string, Action)"/> does, for code that has its own
        /// try block.</summary>
        internal static void Report(string what, Exception e)
        {
            if (!_logged.Add(what)) return;
            Core.Log?.Warning($"[hub] {what} failed (further failures of this step are not logged): {e.Message}");
        }
    }
}
