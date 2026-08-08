using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;

namespace SideHustle.Mods
{
    /// <summary>
    /// Load the mods the boot gate held back, at the moment a session decides it needs them.
    /// </summary>
    /// <remarks>
    /// The other half of <c>SideHustle.Boot.ModGate</c>. Nothing here runs unless that gate was armed, and when it
    /// was not, every call is a no-op - so the host and join paths can ask unconditionally.
    ///
    /// Two calls, not one, and that is the whole lesson of the spike this grew out of.
    /// <c>MelonAssembly.LoadMelonAssembly</c> reads the file and finds the melon inside it, then stops: no logger,
    /// no Harmony instance, no <c>OnInitializeMelon</c>. It reports success, and the mod sits there doing nothing.
    /// <c>MelonBase.Register()</c> is what turns it into a running mod, and it knows that
    /// <c>OnApplicationStart</c> is long gone - it calls the late hooks itself.
    /// </remarks>
    internal static class LateLoader
    {
        private static List<string> _pending;
        private static bool _read;

        /// <summary>Whether anything is still waiting. False on a normal boot, where the gate never armed.</summary>
        internal static bool Any => Pending.Count > 0;

        internal static int PendingCount => Pending.Count;

        internal static IReadOnlyList<string> PendingFiles => Pending;

        private static List<string> Pending
        {
            get
            {
                if (!_read) { _read = true; _pending = ReadList(); }
                return _pending;
            }
        }

        /// <summary>
        /// Load everything the gate held back. Safe to call from anywhere and any number of times: a file that has
        /// already been loaded is off the list, and an empty list returns before touching anything.
        /// </summary>
        /// <param name="why">One phrase for the log - which session made this happen.</param>
        /// <returns>How many mods actually registered.</returns>
        internal static int LoadAll(string why)
        {
            var waiting = Pending;
            if (waiting.Count == 0) return 0;

            Core.Log?.Msg($"[gate] loading {waiting.Count} held-back mod(s) for {why}...");
            int registered = 0;
            // Copied first: Load can take a while and a failure has to leave the list shorter, not unchanged.
            foreach (string file in waiting.ToArray())
            {
                waiting.Remove(file);
                if (LoadOne(file)) registered++;
            }
            Core.Log?.Msg($"[gate] {registered} mod(s) now running ({MelonMod.RegisteredMelons.Count} total).");
            return registered;
        }

        /// <summary>One file, both steps, and a line saying which of them failed. A mod that loads but does not
        /// register is the failure mode worth naming: nothing throws and nothing runs.</summary>
        internal static bool LoadOne(string file)
        {
            string name = Path.GetFileName(file);
            if (!File.Exists(file)) { Core.Log?.Warning("[gate] gone before it could load: " + name); return false; }

            MelonAssembly asm;
            try { asm = MelonAssembly.LoadMelonAssembly(file); }
            catch (Exception e) { Core.Log?.Error("[gate] " + name + " threw while loading: " + e); return false; }
            if (asm?.LoadedMelons == null) { Core.Log?.Warning("[gate] " + name + " carries no melon."); return false; }

            bool any = false;
            foreach (var melon in asm.LoadedMelons)
            {
                bool ok = false;
                try { ok = melon.Register(); }
                catch (Exception e) { Core.Log?.Error("[gate] " + name + " threw while registering: " + e); }
                if (ok) { any = true; Core.Log?.Msg($"[gate]   {melon.Info?.Name} {melon.Info?.Version}"); }
                else Core.Log?.Warning($"[gate]   {melon.Info?.Name}: loaded but did not register.");
            }
            return any;
        }

        /// <summary>
        /// What the gate wrote, or - when that file is missing - what is in Mods and not running.
        ///
        /// The fallback matters more than it looks: the file is written at the END of the scan, so a boot that died
        /// in between leaves it stale or absent, and the difference between "the gate held these back" and "these
        /// are simply not loaded" is invisible from here anyway. Both answers are the same list.
        /// </summary>
        private static List<string> ReadList()
        {
            var list = new List<string>();
            try
            {
                string file = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "SideHustle", "deferred-mods.txt");
                if (File.Exists(file))
                {
                    foreach (string line in File.ReadAllLines(file))
                    {
                        string path = line.Trim();
                        if (path.Length > 0 && File.Exists(path)) list.Add(path);
                    }
                    return list;
                }
            }
            catch (Exception e) { Core.Log?.Warning("[gate] could not read the deferred list: " + e.Message); }

            try
            {
                var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in MelonMod.RegisteredMelons)
                {
                    string loc = null;
                    try { loc = m.MelonAssembly?.Location; } catch { }
                    if (!string.IsNullOrEmpty(loc)) loaded.Add(Path.GetFileName(loc));
                }
                string dir = MelonLoader.Utils.MelonEnvironment.ModsDirectory;
                if (Directory.Exists(dir))
                    foreach (string f in Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
                        if (!loaded.Contains(Path.GetFileName(f))) list.Add(f);
            }
            catch (Exception e) { Core.Log?.Warning("[gate] could not scan for unloaded mods: " + e.Message); }
            return list;
        }
    }
}
