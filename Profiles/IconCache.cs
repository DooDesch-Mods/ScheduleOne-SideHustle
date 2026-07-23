using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;

namespace SideHustle.Profiles
{
    /// <summary>
    /// Lazily fetches Thunderstore package icons and hands them to row <see cref="Image"/>s. Each icon URL is
    /// downloaded once per session (through the same TLS ladder as the package downloads) and kept as a Sprite in
    /// memory, so re-opening or re-filtering the browser is instant. Downloads run on a worker; the Sprite is built
    /// and applied on the main thread. A row that was destroyed before its icon arrives is skipped (Unity's null
    /// check catches the dead Image), so late completions never touch a torn-down view.
    /// </summary>
    internal static class IconCache
    {
        private static readonly Dictionary<string, Sprite> Sprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        // url -> every Image still waiting for this download. A re-render (search/sort) destroys the row that kicked
        // off the fetch and builds new ones for the same url; without tracking all waiters, the completion would only
        // fill the first (now-dead) Image and the visible rows would stay iconless until the next render.
        private static readonly Dictionary<string, List<Image>> Waiting = new Dictionary<string, List<Image>>(StringComparer.Ordinal);

        /// <summary>Show the icon at <paramref name="url"/> in <paramref name="target"/> - immediately if cached,
        /// otherwise once the download finishes. No-op for a missing url/target.</summary>
        internal static void Apply(string url, Image target)
        {
            if (target == null || string.IsNullOrEmpty(url)) return;

            if (Sprites.TryGetValue(url, out var cached))
            {
                if (cached != null) Set(target, cached);
                return;
            }
            if (Waiting.TryGetValue(url, out var waiters)) { waiters.Add(target); return; }   // ride the in-flight download
            Waiting[url] = new List<Image> { target };

            System.Threading.Tasks.Task.Run(async () =>
            {
                byte[] bytes = null;
                try { bytes = await ThunderstoreClient.DownloadBytesAsync(url, CancellationToken.None).ConfigureAwait(false); }
                catch { /* offline / rejected: leave uncached, no icon */ }

                MainThread.Post(() =>
                {
                    var sp = bytes != null ? Build(bytes) : null;
                    Sprites[url] = sp;   // cache even a null so we don't hammer a broken url all session
                    if (Waiting.TryGetValue(url, out var targets))
                    {
                        if (sp != null) foreach (var img in targets) Set(img, sp);
                        Waiting.Remove(url);   // dead Images among them are skipped by Set's null check
                    }
                });
            });
        }

        private static void Set(Image target, Sprite sprite)
        {
            try
            {
                if (target == null) return;
                target.type = Image.Type.Simple;   // the placeholder is a sliced rounded square; the icon is not
                target.sprite = sprite;
                target.color = Color.white;
                target.preserveAspect = true;
                target.enabled = true;
            }
            catch { /* the row was torn down between the null check and here */ }
        }

        private static Sprite Build(byte[] bytes)
        {
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                if (!tex.LoadImage(bytes)) return null;
                var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                if (sp != null) sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
                return sp;
            }
            catch { return null; }
        }
    }
}
