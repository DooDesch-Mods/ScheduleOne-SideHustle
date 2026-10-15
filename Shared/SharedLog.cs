using System;
using System.Collections.Generic;

namespace SideHustle.Shared
{
    /// <summary>
    /// The log sink for code that is compiled into BOTH the mod and the boot plugin (the files under Shared/ and
    /// Profiles/PackageCache.cs). Those files are pure BCL and cannot reach <c>Core.Log</c> or the plugin's
    /// logger, so each host assigns its own logger here once at startup; until then a message is dropped.
    /// </summary>
    internal static class SharedLog
    {
        /// <summary>Level "warning" sink. Set by the host (mod: Core.Log, plugin: its LoggerInstance).</summary>
        internal static Action<string> Warn;

        /// <summary>Level "info" sink, for expected fallbacks.</summary>
        internal static Action<string> Info;

        private static readonly HashSet<string> _once = new HashSet<string>(StringComparer.Ordinal);
        private const int MaxOnceKeys = 256;

        internal static void Warning(string message)
        {
            try { Warn?.Invoke(message); }
            catch (Exception e) { System.Diagnostics.Debug.WriteLine("SharedLog sink failed: " + e.Message); }
        }

        internal static void Msg(string message)
        {
            try { Info?.Invoke(message); }
            catch (Exception e) { System.Diagnostics.Debug.WriteLine("SharedLog sink failed: " + e.Message); }
        }

        /// <summary>A warning for a condition that repeats on every call (a probe over many files): the first
        /// occurrence per <paramref name="key"/> is logged, the rest are not.</summary>
        internal static void WarnOnce(string key, string message)
        {
            lock (_once)
            {
                if (_once.Count >= MaxOnceKeys || !_once.Add(key ?? message)) return;
            }
            Warning(message);
        }
    }
}
