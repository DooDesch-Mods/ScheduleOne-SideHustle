using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SideHustle.Sync
{
    /// <summary>
    /// Text-level TOML helpers for MelonPreferences.cfg. A profile clone gets its cfg by TEXT transform -
    /// never through the live MelonPreferences API - so the running game's own cfg is never touched and the
    /// value syntax the game wrote survives untouched outside the merged keys (line endings are normalized to
    /// the input's dominant style). Only whole [Section] headers and simple `Key = value` lines are understood,
    /// which is exactly what MelonPreferences emits. Pure BCL on purpose for the console test harness.
    /// </summary>
    internal static class PrefsSync
    {
        /// <summary>
        /// Merge string values into one section: each existing `Key = ...` line is replaced, missing keys are
        /// appended at the section's end (after its last non-blank line), and the whole section is created at
        /// EOF when absent. Values are written as TOML basic strings (quoted + escaped).
        /// </summary>
        internal static string MergeKeys(string cfgText, string section, IReadOnlyDictionary<string, string> values)
        {
            if (string.IsNullOrEmpty(section) || values == null || values.Count == 0) return cfgText ?? "";

            string eol = (cfgText ?? "").Contains("\r\n") ? "\r\n" : "\n";
            var lines = (cfgText ?? "").Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1); // no phantom line from a trailing newline

            string header = "[" + section + "]";
            int start = lines.FindIndex(l => l.Trim() == header);
            var pending = new Dictionary<string, string>(values);

            if (start >= 0)
            {
                int end = lines.Count;
                for (int i = start + 1; i < lines.Count; i++)
                    if (lines[i].TrimStart().StartsWith("[")) { end = i; break; }

                for (int i = start + 1; i < end; i++)
                {
                    string key = KeyOfLine(lines[i]);
                    if (key != null && pending.TryGetValue(key, out var v))
                    {
                        lines[i] = key + " = " + Quote(v);
                        pending.Remove(key);
                    }
                }

                if (pending.Count > 0)
                {
                    int insert = start + 1;
                    for (int i = end - 1; i > start; i--)
                        if (lines[i].Trim().Length > 0) { insert = i + 1; break; }
                    foreach (var kv in pending)
                        lines.Insert(insert++, kv.Key + " = " + Quote(kv.Value));
                }
            }
            else
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                lines.Add(header);
                foreach (var kv in pending)
                    lines.Add(kv.Key + " = " + Quote(kv.Value));
            }

            return string.Join(eol, lines) + eol;
        }

        /// <summary>The bare key of a `Key = ...` line, or null for blanks, comments, headers and non-assignments.</summary>
        private static string KeyOfLine(string line)
        {
            string t = line.TrimStart();
            if (t.Length == 0 || t[0] == '#' || t[0] == '[') return null;
            int eq = t.IndexOf('=');
            if (eq <= 0) return null;
            string key = t.Substring(0, eq).Trim();
            return key.Length == 0 ? null : key;
        }

        /// <summary>
        /// Extract whole [Section] blocks (header + body up to the next header) for the given section ids from a
        /// cfg text, concatenated in the order requested. Used host-side to publish exactly the MelonPreferences
        /// categories the host chose to sync. Unknown sections are skipped silently.
        /// </summary>
        internal static string ExtractSections(string cfgText, IEnumerable<string> sectionIds)
        {
            if (string.IsNullOrEmpty(cfgText) || sectionIds == null) return "";
            var lines = cfgText.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            foreach (var id in sectionIds)
            {
                string header = "[" + id + "]";
                int start = Array.FindIndex(lines, l => l.Trim() == header);
                if (start < 0) continue;
                sb.Append(lines[start]).Append('\n');
                for (int i = start + 1; i < lines.Length; i++)
                {
                    if (lines[i].TrimStart().StartsWith("[")) break;
                    sb.Append(lines[i]).Append('\n');
                }
            }
            return sb.ToString();
        }

        /// <summary>Overlay published section text onto a client cfg: split the payload into its [Section] blocks
        /// and MergeKeys each one wholesale (whole-category sync, so replacing the block is exact). Sections the
        /// client does not have are appended; sections it has keep every key the host did not send.</summary>
        internal static string ApplyOverlay(string clientCfg, string overlayText)
        {
            if (string.IsNullOrEmpty(overlayText)) return clientCfg ?? "";
            string cfg = clientCfg ?? "";
            var lines = overlayText.Replace("\r\n", "\n").Split('\n');
            string section = null;
            var values = new Dictionary<string, string>();

            void Flush()
            {
                if (section != null && values.Count > 0) cfg = MergeKeys(cfg, section, new Dictionary<string, string>(values));
                values.Clear();
            }

            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    Flush();
                    section = line.Substring(1, line.Length - 2);
                    continue;
                }
                if (line.StartsWith("#") || section == null) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = Unquote(line.Substring(eq + 1).Trim());
                if (key.Length > 0) values[key] = val;
            }
            Flush();
            return cfg;
        }

        // TOML basic string: backslashes and quotes escaped; the merged values (ids, encoded blobs, Windows
        // paths) are single-line by construction, so no multi-line handling is needed.
        private static string Quote(string value) =>
            "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static string Unquote(string value)
        {
            if (value != null && value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                return value.Substring(1, value.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
            return value ?? "";
        }
    }
}
