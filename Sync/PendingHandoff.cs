using System;
using System.Collections.Generic;
using System.IO;

namespace SideHustle.Sync
{
    /// <summary>
    /// The session tokens a relaunch hands to the process it starts, written beside the profile instead of inside
    /// MelonPreferences.cfg.
    ///
    /// The cfg was the only carrier, and that made the whole rejoin depend on the WHOLE file parsing: one malformed
    /// line anywhere in it - written by any mod, in any category - and MelonLoader falls back to defaults for
    /// everything, including our pending join. The restart still happened, the mods still loaded, and the player was
    /// left standing in the menu with no lobby and nothing in the log to explain it.
    ///
    /// This file is ours alone, one `key=value` per line, no escaping to get wrong (the values are our own encoded
    /// tokens: digits, letters and separators). It is read once and deleted, so a token can never fire twice.
    ///
    /// The cfg entries stay as they were: this is a second, independent copy, and whichever answers first wins.
    /// </summary>
    internal static class PendingHandoff
    {
        private const string FileName = "sidehustle-pending.txt";

        private static string PathFor(string baseDir) =>
            string.IsNullOrEmpty(baseDir) ? null : Path.Combine(baseDir, FileName);

        /// <summary>Write the tokens next to a profile that is about to be launched. Failure is not fatal - the cfg
        /// copy is still there - so it warns rather than aborting a relaunch that would otherwise work.</summary>
        internal static void Write(string baseDir, IReadOnlyDictionary<string, string> tokens)
        {
            string path = PathFor(baseDir);
            if (path == null || tokens == null || tokens.Count == 0) return;
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var kv in tokens)
                {
                    if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value)) continue;
                    // A newline would split one token into two lines and quietly corrupt the next read.
                    if (kv.Value.IndexOf('\n') >= 0 || kv.Value.IndexOf('\r') >= 0) continue;
                    sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
                }
                if (sb.Length == 0) return;
                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception e) { Core.Log?.Warning("[sync] could not write the pending-join file: " + e.Message); }
        }

        /// <summary>Read and DELETE the tokens this process was started with. Empty when there are none, which is the
        /// normal case for a plain launch.</summary>
        internal static Dictionary<string, string> TakeAll()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            string path = PathFor(Mods.AltBase.CurrentBase());
            if (path == null) return map;
            try
            {
                if (!File.Exists(path)) return map;
                foreach (var line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                // Consumed: a token that survived its own launch would rejoin a lobby the player already left.
                try { File.Delete(path); }
                catch (Exception e) { Core.Log?.Warning("[sync] could not clear the pending-join file: " + e.Message); }
                if (map.Count > 0) Core.Log?.Msg($"[sync] picked up {map.Count} pending token(s) from the profile.");
            }
            catch (Exception e) { Core.Log?.Warning("[sync] could not read the pending-join file: " + e.Message); }
            return map;
        }
    }
}
