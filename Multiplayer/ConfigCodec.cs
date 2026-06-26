using System;
using System.Collections.Generic;
using System.Text;

namespace SideHustle.Multiplayer
{
    /// <summary>
    /// A tiny <c>key=value;key=value</c> codec for the per-gamemode host config carried on the Steam lobby (the
    /// <c>sh_config</c> blob). Keys and values are percent-escaped (<c>% ; =</c> and newlines) so a free-text
    /// setting can never corrupt the grammar. Decode is tolerant: malformed records are skipped.
    /// </summary>
    internal static class ConfigCodec
    {
        internal static string Encode(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            var sb = new StringBuilder();
            foreach (var kv in pairs)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(Esc(kv.Key)).Append('=').Append(Esc(kv.Value ?? ""));
            }
            return sb.ToString();
        }

        internal static Dictionary<string, string> Decode(string blob)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(blob)) return map;
            foreach (var rec in blob.Split(';'))
            {
                if (rec.Length == 0) continue;
                int i = rec.IndexOf('=');
                if (i <= 0) continue;
                map[Unesc(rec.Substring(0, i))] = Unesc(rec.Substring(i + 1));
            }
            return map;
        }

        // Escape '%' first so the escapes we insert are not re-escaped; unescape '%25' last for the inverse.
        private static string Esc(string s) => string.IsNullOrEmpty(s) ? "" :
            s.Replace("%", "%25").Replace(";", "%3B").Replace("=", "%3D").Replace("\r", "%0D").Replace("\n", "%0A");

        private static string Unesc(string s) => string.IsNullOrEmpty(s) ? "" :
            s.Replace("%0A", "\n").Replace("%0D", "\r").Replace("%3D", "=").Replace("%3B", ";").Replace("%25", "%");
    }
}
