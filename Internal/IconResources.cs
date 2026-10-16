using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SideHustle.Internal
{
    /// <summary>
    /// Turns <see cref="GamemodeDescriptor.IconResource"/> (a PNG embedded in the gamemode's own assembly) into
    /// <see cref="GamemodeDescriptor.IconTex"/>, which the hub already knows how to draw. Called by the hub right
    /// before it resolves a row icon; tried once per descriptor so a missing resource is logged once, not on every
    /// list rebuild.
    /// </summary>
    internal static class IconResources
    {
        private static readonly HashSet<GamemodeDescriptor> _tried = new HashSet<GamemodeDescriptor>();

        internal static void Fill(GamemodeDescriptor desc)
        {
            // A descriptor with its own sprite or texture, or without a resource name, has nothing to load.
            if (desc == null || desc.Icon != null || desc.IconTex != null || string.IsNullOrWhiteSpace(desc.IconResource)) return;
            if (!_tried.Add(desc)) return;

            string name = desc.IconResource.Trim();
            var asm = desc.OwnerAssembly;
            if (asm == null)
            {
                Core.Log?.Warning($"[api] gamemode '{desc.Id}' IconResource: the owner assembly is unknown, so \"{name}\" cannot be loaded.");
                return;
            }
            try
            {
                byte[] bytes;
                using (var s = asm.GetManifestResourceStream(name))
                {
                    if (s == null)
                    {
                        string have = string.Join(", ", asm.GetManifestResourceNames());
                        Core.Log?.Warning($"[api] gamemode '{desc.Id}' IconResource: \"{name}\" is not embedded in {asm.GetName().Name}. " +
                                          $"Embedded: {(have.Length > 0 ? have : "none")}.");
                        return;
                    }
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    bytes = ms.ToArray();
                }

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                if (!tex.LoadImage(bytes))
                {
                    Core.Log?.Warning($"[api] gamemode '{desc.Id}' IconResource: \"{name}\" ({bytes.Length} bytes) is not a readable PNG/JPG.");
                    return;
                }
                desc.IconTex = tex;
                Core.Log?.Msg($"[api] gamemode '{desc.Id}' icon loaded from \"{name}\" ({tex.width}x{tex.height}).");
            }
            catch (Exception e) { Core.Log?.Error($"[api] gamemode '{desc.Id}' IconResource \"{name}\" failed to load: {e}"); }
        }
    }
}
