using System;
using System.IO;
using SideHustle.Mods;

namespace SideHustle.Profiles
{
    /// <summary>
    /// Self-installs the embedded boot plugin (the profile picker) to &lt;home&gt;\Plugins\SideHustle.Boot.dll, so a
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
                // The Plugins folder of the base the player starts from: the game folder, or the mod manager profile
                // when r2modman or Gale launched with --melonloader.basedir. A copy in <game>\Plugins would never
                // load in a manager session. Side Hustle's own profiles reach the home Plugins through a junction.
                string root = AltBase.HomeBase();
                if (root == null) { Core.Log?.Warning("[profiles] no home folder found; the boot plugin is not installed."); return; }
                string pluginsDir = Path.Combine(root, "Plugins");
                string target = Path.Combine(pluginsDir, ResourceName);
                string old = target + ".old";

                try { if (File.Exists(old)) File.Delete(old); }
                catch (Exception e) { Core.Log?.Msg("[profiles] the previous boot plugin copy is still loaded and stays until next launch: " + e.Message); }

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
                }

                // The new copy is written beside the old one FIRST, so a failed or interrupted write never leaves the
                // plugin folder without a working picker; only the final move replaces it.
                Directory.CreateDirectory(pluginsDir);
                string fresh = target + ".new";
                try
                {
                    File.WriteAllBytes(fresh, embedded);
                    if (File.Exists(target))
                    {
                        try
                        {
                            File.Move(fresh, target, true);   // not loaded (e.g. never picked up) - plain replace
                        }
                        catch (Exception e)
                        {
                            Core.Log?.Msg("[profiles] the boot plugin is loaded; swapping it by rename: " + e.Message);
                            File.Move(target, old, true);     // loaded: rename-swap (NTFS allows renaming a loaded DLL)
                            File.Move(fresh, target);
                        }
                    }
                    else File.Move(fresh, target);
                }
                catch
                {
                    try { if (File.Exists(fresh)) File.Delete(fresh); }
                    catch (Exception cleanup) { Core.Log?.Warning("[profiles] could not remove the unfinished boot plugin copy: " + cleanup.Message); }
                    throw;   // reported once by the handler below
                }
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
            catch (Exception e)
            {
                Core.Log?.Warning("[profiles] could not read the embedded boot plugin: " + e.Message);
                return null;
            }
        }
    }
}
