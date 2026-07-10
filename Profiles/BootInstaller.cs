using System;
using System.IO;
using SideHustle.Mods;

namespace SideHustle.Profiles
{
    /// <summary>
    /// Self-installs the embedded boot plugin (the profile picker) to &lt;game&gt;\Plugins\SideHustle.Boot.dll, so a
    /// plain "drop SideHustle.dll into Mods" install gets the picker without a second file to manage. Updates
    /// use a rename-swap: NTFS allows renaming a loaded DLL and writing a fresh file under the original name;
    /// the renamed .old is deleted on a later run once it is no longer loaded. Uninstall story: delete
    /// Plugins\SideHustle.Boot.dll (documented in the README).
    /// </summary>
    internal static class BootInstaller
    {
        private const string ResourceName = "SideHustle.Boot.dll";

        internal static void EnsureInstalled()
        {
            try
            {
                string root = ModInventory.GameRoot();
                if (root == null) return;
                string pluginsDir = Path.Combine(root, "Plugins");
                string target = Path.Combine(pluginsDir, ResourceName);
                string old = target + ".old";

                try { if (File.Exists(old)) File.Delete(old); } catch { /* still loaded from a swap last session */ }

                byte[] embedded = ReadEmbedded();
                if (embedded == null || embedded.Length == 0)
                {
                    Core.Log?.Warning("[profiles] no embedded boot plugin in this build; the boot picker stays as installed.");
                    return;
                }

                if (File.Exists(target))
                {
                    byte[] existing = File.ReadAllBytes(target);
                    if (existing.AsSpan().SequenceEqual(embedded)) return;   // up to date

                    try
                    {
                        File.Delete(target);   // not loaded (e.g. was never picked up) - plain replace
                    }
                    catch
                    {
                        File.Move(target, old);   // loaded: rename-swap (NTFS allows renaming a loaded DLL)
                    }
                }

                Directory.CreateDirectory(pluginsDir);
                File.WriteAllBytes(target, embedded);
                Core.Log?.Msg("[profiles] boot plugin installed/updated (active from the next launch).");
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[profiles] boot-plugin install failed (the picker may be outdated): " + e.Message);
            }
        }

        private static byte[] ReadEmbedded()
        {
            try
            {
                using var s = typeof(BootInstaller).Assembly.GetManifestResourceStream(ResourceName);
                if (s == null) return null;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            catch { return null; }
        }
    }
}
