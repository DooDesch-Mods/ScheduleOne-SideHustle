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

        /// <summary>How many times a failed file has been tried. A file that fails stays pending so a later session
        /// retries it, but not for ever: after <see cref="MaxAttempts"/> it is given up on with a line saying so.</summary>
        private static readonly Dictionary<string, int> _attempts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private const int MaxAttempts = 2;
        private static bool _loading;
        private static bool _probeLogged;

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
            if (_loading)
            {
                Core.Log?.Warning($"[gate] a load of held-back mods is already running; ignoring the request for {why}.");
                return 0;
            }

            _loading = true;
            try
            {
                Core.Log?.Msg($"[gate] loading {waiting.Count} held-back mod(s) for {why}...");
                int registered = 0;
                // Copied first: Load can take a while and the list changes as files settle.
                foreach (string file in waiting.ToArray())
                    if (Settle(file, LoadOne(file, out bool retry), retry)) registered++;
                Core.Log?.Msg($"[gate] {registered} mod(s) now running ({MelonMod.RegisteredMelons.Count} total, {waiting.Count} still held back).");
                return registered;
            }
            finally { _loading = false; }
        }

        /// <summary>Take a file off the pending list once it has loaded, or once there is no point trying again;
        /// otherwise leave it for the next session that asks. Returns <paramref name="loaded"/>.</summary>
        private static bool Settle(string file, bool loaded, bool retry)
        {
            string name = Path.GetFileName(file);
            if (loaded) { Pending.Remove(file); _attempts.Remove(file); return true; }
            if (!retry)
            {
                Core.Log?.Warning($"[gate] {name} cannot be loaded and will not be retried.");
                Pending.Remove(file);
                return false;
            }
            _attempts.TryGetValue(file, out int tries);
            _attempts[file] = ++tries;
            if (tries >= MaxAttempts)
            {
                Core.Log?.Warning($"[gate] {name} failed {tries} times; giving up on it for this run.");
                Pending.Remove(file);
            }
            else Core.Log?.Warning($"[gate] {name} failed to load; it stays held back and is tried again next time.");
            return false;
        }

        internal static bool IsPending(string file)
        {
            foreach (string p in Pending)
                if (string.Equals(p, file, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Load one mod and exactly what it needs, in the order the boot plugin worked out.
        /// </summary>
        /// <remarks>
        /// The first version took every held-back mod ahead of this one in the list, on the grounds that the list
        /// was already in dependency order. It was, and it was still wrong: picking PropHunt loaded twenty mods,
        /// froze the menu for as long as that took, and stalled outright on one of them that had no business being
        /// started from a menu at all. A gamemode needs its own dependencies. Everything else can keep waiting.
        /// </remarks>
        internal static bool LoadClosure(IReadOnlyList<string> closure, string target)
        {
            if (closure == null || closure.Count == 0) return false;
            bool ok = false;
            foreach (string file in closure)
            {
                if (!IsPending(file)) continue;   // already running: nothing to do, and never twice
                bool loaded = Settle(file, LoadOne(file, out bool retry), retry);
                if (string.Equals(file, target, StringComparison.OrdinalIgnoreCase)) ok = loaded;
            }
            return ok;
        }

        /// <summary>One file, both steps, and a line saying which of them failed. A mod that loads but does not
        /// register is the failure mode worth naming: nothing throws and nothing runs.</summary>
        internal static bool LoadOne(string file) => LoadOne(file, out _);

        /// <param name="retry">Set when a later attempt could succeed (a throw, a refused registration); false for a
        /// file that is gone or holds no melon.</param>
        private static bool LoadOne(string file, out bool retry)
        {
            retry = false;
            string name = Path.GetFileName(file);
            if (!File.Exists(file)) { Core.Log?.Warning("[gate] gone before it could load: " + name); return false; }

            MelonAssembly asm;
            try { asm = MelonAssembly.LoadMelonAssembly(file); }
            catch (Exception e) { retry = true; Core.Log?.Error("[gate] " + name + " threw while loading: " + e); return false; }
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
            retry = !any;
            return any;
        }

        /// <summary>Why a session cannot be entered without restarting, or null when it can.</summary>
        internal sealed class Collision
        {
            internal string ModName;
            internal string Loaded;
            internal string Wanted;
            public override string ToString() => $"{ModName} {Loaded} is running, the session wants {Wanted}";
        }

        /// <summary>
        /// Whether this session's mod set can be reached from here without restarting the game.
        /// </summary>
        /// <remarks>
        /// The whole point of the gate. Nothing loaded at startup, so a session that needs mods can simply load
        /// them - no profile directory, no relaunch, no losing the menu. What cannot be done in-process is
        /// REPLACING an assembly that is already loaded, so a version that differs from the one running is the one
        /// case that still costs a restart.
        ///
        /// A mod the session does not want but that is already loaded is NOT a collision. It stays out of the
        /// session's way by not being asked to do anything, and unloading it (MelonBase.UnregisterInstance does
        /// unpatch Harmony) would still leave its spawned objects and static state behind - a worse answer than
        /// leaving it idle.
        /// </remarks>
        internal static Collision FirstCollision(IEnumerable<KeyValuePair<string, string>> wanted)
        {
            var running = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var melon in MelonMod.RegisteredMelons)
            {
                string file = null;
                try { file = Path.GetFileName(melon.MelonAssembly?.Location); }
                catch (Exception e) { WarnProbe("a running mod's file name", e); }
                if (string.IsNullOrEmpty(file)) continue;
                string version = null;
                try { version = melon.Info?.Version; }
                catch (Exception e) { WarnProbe("a running mod's version", e); }
                running[file] = version ?? "";
            }

            foreach (var entry in wanted)
            {
                if (!running.TryGetValue(entry.Key, out string have)) continue;   // not loaded: load it, no problem
                string want = VersionOf(entry.Value);
                if (want == null || have.Length == 0) continue;                   // unreadable: not a claim worth making
                if (string.Equals(have, want, StringComparison.OrdinalIgnoreCase)) continue;
                return new Collision { ModName = entry.Key, Loaded = have, Wanted = want };
            }
            return null;
        }

        /// <summary>The version a file declares, without loading it. Reflection would load the assembly, which is
        /// the exact thing this check exists to decide about.</summary>
        private static string VersionOf(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                string v = info.FileVersion;
                if (string.IsNullOrEmpty(v)) return null;
                // MelonInfo versions are three-part; a file version carries a fourth that is always 0 here.
                var parts = v.Split('.');
                return parts.Length >= 3 ? string.Join(".", parts[0], parts[1], parts[2]) : v;
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"[gate] could not read the version of '{Path.GetFileName(path)}': {e.Message}");
                return null;
            }
        }

        /// <summary>A probe of the running mods that throws on one melon is skipped for that melon; logged once,
        /// since the same melon fails the same way on every call.</summary>
        private static void WarnProbe(string what, Exception e)
        {
            if (_probeLogged) return;
            _probeLogged = true;
            Core.Log?.Warning($"[gate] could not read {what}; skipping it: {e.Message}");
        }

        /// <summary>
        /// Load an exact set of files - the host's own bytes, straight out of the package cache.
        /// </summary>
        /// <remarks>
        /// The replacement for building a profile directory and relaunching into it. The bytes are the same ones
        /// that build would have hardlinked; the difference is that nothing has to be arranged on disk first and
        /// the player keeps the screen they were standing on.
        /// </remarks>
        internal static int LoadSet(IEnumerable<KeyValuePair<string, string>> wanted, string why)
        {
            var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var melon in MelonMod.RegisteredMelons)
            {
                try { running.Add(Path.GetFileName(melon.MelonAssembly?.Location) ?? ""); }
                catch (Exception e) { WarnProbe("a running mod's file name", e); }
            }

            int loaded = 0;
            foreach (var entry in wanted)
            {
                if (running.Contains(entry.Key)) continue;
                bool ok = LoadOne(entry.Value, out bool retry);
                if (ok) loaded++;
                // Off the list on success or when it can never load; a retryable failure stays for the next session.
                if (ok || !retry)
                    Pending.RemoveAll(p => string.Equals(Path.GetFileName(p), entry.Key, StringComparison.OrdinalIgnoreCase));
                else
                    Core.Log?.Warning($"[gate] {entry.Key} failed to load for {why}; it stays held back.");
            }
            if (loaded > 0) Core.Log?.Msg($"[gate] {loaded} mod(s) loaded for {why} without restarting.");
            return loaded;
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
                    try { loc = m.MelonAssembly?.Location; }
                    catch (Exception e) { WarnProbe("a running mod's location", e); }
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
