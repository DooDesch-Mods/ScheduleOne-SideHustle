using MelonLoader;
using MelonLoader.Utils;

[assembly: MelonInfo(typeof(SideHustle.Boot.BootPlugin), "SideHustle.Boot", "2.0.0", "DooDesch")]
[assembly: MelonGame(null, null)]

namespace SideHustle.Boot
{
    /// <summary>
    /// Boots the full mod set on a plain launch and steps aside inside a base-dir session. A MelonPlugin loads
    /// before the game initializes, so it is the right place to confirm which kind of session this is. Named
    /// profiles are activated in-game: activating one relaunches into that profile's own isolated MelonLoader base
    /// (its Mods/Plugins/UserLibs, the runtime shared) via --melonloader.basedir, so the picker never has to touch
    /// the global mod folders a mod manager owns.
    /// </summary>
    public class BootPlugin : MelonPlugin
    {
        public override void OnPreInitialization()
        {
            try { Run(); }
            catch (Exception e) { LoggerInstance.Error("[boot] picker failed; booting the full mod set: " + e); }
        }

        private void Run()
        {
            string gameRoot = GameRootFromProcess();
            if (gameRoot == null) return;

            // A base-dir session (a named profile, a gamemode-policy base or a lobby-sync base) already loaded exactly
            // the right mods, plugins and libraries at bootstrap - never re-scan or redirect on top of it.
            if (!PathsEqual(MelonEnvironment.MelonBaseDirectory, gameRoot))
            {
                LoggerInstance.Msg("[boot] base-dir session detected; picker skipped.");
                return;
            }

            // A plain launch is always the full mod set. Named profiles are activated in-game now: activating one
            // relaunches the game into that profile's own isolated base dir (its Mods/Plugins/UserLibs), so there is
            // nothing to pick or redirect here and the global mod folders a mod manager owns are never touched.
            LoggerInstance.Msg("[boot] full mod set.");
        }

        // --- environment helpers ---

        private static string GameRootFromProcess()
        {
            try { return Path.GetDirectoryName(Environment.ProcessPath); } catch { return null; }
        }

        private static bool PathsEqual(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'),
                                     Path.GetFullPath(b).TrimEnd('\\', '/'),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }
    }
}
