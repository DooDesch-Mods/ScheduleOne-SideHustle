using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SideHustle.Sync
{
    /// <summary>One mod entry of a published lobby manifest.</summary>
    internal sealed class ManifestMod
    {
        public string File;     // enabled DLL name, e.g. "Siesta.dll"
        public string Name;     // MelonInfo display name
        public string Version;  // exact MelonInfo version (never a range; "" when the mod declares none)
        public string Sha256;   // lowercase hex of the DLL bytes - wins over Version everywhere
        public string Source;   // "ts:Owner-Name-1.2.3" | "nx:<https url>" | "" (unsourced: dropped from sync)
    }

    /// <summary>
    /// The host's published mod set: what a client must load to match the session. Serializes to a line-based
    /// canonical text (mods deterministically sorted by file name, each field percent-escaped) so the same mod
    /// set always produces the same bytes - the manifest hash (SyncCodec.Hash) relies on that. Parsing is
    /// strict: a malformed text, a mod-count mismatch, or a NEWER schema version yields null, because silently
    /// ignoring unknown future semantics could make a client join with the wrong set. This type is pure BCL on
    /// purpose (no Unity/MelonLoader) so the console test harness can compile it standalone.
    /// </summary>
    internal sealed class SyncManifest
    {
        internal const int SchemaVersion = 1;

        public string GameVersion = "";
        public string MelonLoaderVersion = "";
        public string SideHustleVersion = "";
        public List<ManifestMod> Mods = new List<ManifestMod>();

        public string ToCanonicalText()
        {
            var sb = new StringBuilder();
            sb.Append("v=").Append(SchemaVersion).Append('\n');
            sb.Append("game=").Append(Esc(GameVersion)).Append('\n');
            sb.Append("ml=").Append(Esc(MelonLoaderVersion)).Append('\n');
            sb.Append("sh=").Append(Esc(SideHustleVersion)).Append('\n');
            var mods = (Mods ?? new List<ManifestMod>())
                .Where(m => m != null && !string.IsNullOrEmpty(m.File))
                .OrderBy(m => m.File, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.Sha256, StringComparer.Ordinal)
                .ToList();
            sb.Append("mods=").Append(mods.Count).Append('\n');
            foreach (var m in mods)
            {
                sb.Append("m=")
                  .Append(Esc(m.File)).Append('|')
                  .Append(Esc(m.Name)).Append('|')
                  .Append(Esc(m.Version)).Append('|')
                  .Append(Esc(m.Sha256)).Append('|')
                  .Append(Esc(m.Source)).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Parse a canonical manifest text; null when malformed, truncated, or from a newer schema.</summary>
        public static SyncManifest Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var res = new SyncManifest();
            int declaredCount = -1;
            bool sawVersion = false;
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length == 0) continue;
                int i = line.IndexOf('=');
                if (i <= 0) return null;
                string key = line.Substring(0, i);
                string val = line.Substring(i + 1);
                switch (key)
                {
                    case "v":
                        if (!int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out int v)) return null;
                        if (v > SchemaVersion) return null;
                        sawVersion = true;
                        break;
                    case "game": res.GameVersion = Unesc(val); break;
                    case "ml": res.MelonLoaderVersion = Unesc(val); break;
                    case "sh": res.SideHustleVersion = Unesc(val); break;
                    case "mods":
                        if (!int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out declaredCount)) return null;
                        break;
                    case "m":
                        var f = val.Split('|');
                        if (f.Length != 5) return null;
                        res.Mods.Add(new ManifestMod
                        {
                            File = Unesc(f[0]),
                            Name = Unesc(f[1]),
                            Version = Unesc(f[2]),
                            Sha256 = Unesc(f[3]),
                            Source = Unesc(f[4]),
                        });
                        break;
                    default:
                        // Unknown keys within the SAME schema version are tolerated (additive extensions);
                        // anything semantically breaking must bump "v" instead.
                        break;
                }
            }
            if (!sawVersion) return null;
            if (declaredCount < 0 || declaredCount != res.Mods.Count) return null;
            if (res.Mods.Any(m => string.IsNullOrEmpty(m.File))) return null;
            return res;
        }

        // Same escaping idea as ConfigCodec, plus '|' because it separates the mod-line fields.
        // Escape '%' first so inserted escapes are not re-escaped; unescape '%25' last for the inverse.
        private static string Esc(string s) => string.IsNullOrEmpty(s) ? "" :
            s.Replace("%", "%25").Replace("|", "%7C").Replace("\r", "%0D").Replace("\n", "%0A");

        private static string Unesc(string s) => string.IsNullOrEmpty(s) ? "" :
            s.Replace("%0A", "\n").Replace("%0D", "\r").Replace("%7C", "|").Replace("%25", "%");
    }
}
