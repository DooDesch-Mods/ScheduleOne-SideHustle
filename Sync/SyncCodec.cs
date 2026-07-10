using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace SideHustle.Sync
{
    /// <summary>
    /// Carries the manifest/prefs payloads across Steam lobby metadata: deflate + base64, split into chunks
    /// that stay safely under Steam's ~8KB-per-key limit (sh_manifest0..n / sh_prefs0..n). Also computes the
    /// short manifest hash (sh_mhash) that trust store, member handshake and rejoin validation key on. A failed
    /// unpack returns null - the caller treats the lobby as "manifest unreadable" (join-without-sync only),
    /// never as an empty mod set. Pure BCL on purpose (no Unity/MelonLoader) for the console test harness.
    /// </summary>
    internal static class SyncCodec
    {
        internal const int MaxChunkChars = 7000;

        /// <summary>Compress and split a payload text; an empty/null text packs to zero chunks.</summary>
        internal static string[] Pack(string text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
            byte[] raw = Encoding.UTF8.GetBytes(text);
            using var buf = new MemoryStream();
            using (var deflate = new DeflateStream(buf, CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(raw, 0, raw.Length);
            string b64 = Convert.ToBase64String(buf.ToArray());
            var chunks = new string[(b64.Length + MaxChunkChars - 1) / MaxChunkChars];
            for (int i = 0; i < chunks.Length; i++)
                chunks[i] = b64.Substring(i * MaxChunkChars, Math.Min(MaxChunkChars, b64.Length - i * MaxChunkChars));
            return chunks;
        }

        /// <summary>Inverse of Pack. Null when any chunk is missing/corrupt (Steam data can arrive truncated);
        /// zero chunks decode to the empty string (a host with nothing to sync).</summary>
        internal static string Unpack(IReadOnlyList<string> chunks)
        {
            if (chunks == null) return null;
            if (chunks.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var c in chunks)
            {
                if (string.IsNullOrEmpty(c)) return null;
                sb.Append(c);
            }
            try
            {
                using var input = new MemoryStream(Convert.FromBase64String(sb.ToString()));
                using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                using var reader = new StreamReader(deflate, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch { return null; }
        }

        /// <summary>The short manifest hash (16 hex chars of SHA256) over BOTH payload texts. Computed on the
        /// uncompressed canonical texts so host and client never depend on identical compressor output.</summary>
        internal static string Hash(string manifestText, string prefsText)
        {
            using var sha = SHA256.Create();
            byte[] bytes = Encoding.UTF8.GetBytes((manifestText ?? "") + "\n--\n" + (prefsText ?? ""));
            return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant().Substring(0, 16);
        }
    }
}
