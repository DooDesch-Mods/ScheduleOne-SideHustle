// SideHustleLink - copy this file into your mod's project. It is source, not a DLL: nothing to ship.
//
// It carries the weak-link pattern for an OPTIONAL dependency on Side Hustle, so your mod loads and runs whether
// or not SideHustle.dll is installed. Also put this once in your project (any file):
//
//     [assembly: MelonLoader.MelonOptionalDependencies("SideHustle")]
//
// and reference SideHustle.dll with <Private>false</Private> (it is never copied into your output).
//
// Why the pattern matters: the .NET JIT compiles a whole method before it runs. A method that mentions a
// SideHustle type anywhere in its body fails at JIT time when SideHustle.dll is missing - before the first line,
// so a try/catch INSIDE that method never runs. The try/catch has to sit one call further out, around a call to a
// method that is not inlined. TryRegister and TryCall below do exactly that; keep every SideHustle type inside
// the lambdas you pass them (or inside methods only those lambdas call).

using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using MelonLoader;

namespace SideHustleShim
{
    /// <summary>Optional-dependency helpers for Side Hustle. Copy-in source; see the file header.</summary>
    internal static class SideHustleLink
    {
        private static bool? _present;
        private static bool _absentLogged;

        /// <summary>True when SideHustle.dll is loaded in this game. Cached after the first true answer.</summary>
        internal static bool IsPresent
        {
            get
            {
                if (_present == true) return true;
                bool found = AppDomain.CurrentDomain.GetAssemblies()
                    .Any(a => string.Equals(a.GetName().Name, "SideHustle", StringComparison.OrdinalIgnoreCase));
                if (found) _present = true;
                return found;
            }
        }

        /// <summary>
        /// The running Side Hustle's API version (<c>SideHustle.API.ApiVersion</c>), read by reflection so it is the
        /// runtime value. 0 when Side Hustle is absent or older than 2.5.0 (which has no ApiVersion).
        /// </summary>
        internal static int ApiVersion
        {
            get
            {
                var prop = ApiType()?.GetProperty("ApiVersion", BindingFlags.Public | BindingFlags.Static);
                try { return prop != null ? (int)prop.GetValue(null) : 0; }
                catch (Exception e) { Log(null, true, "reading SideHustle.API.ApiVersion failed: " + e.Message); return 0; }
            }
        }

        /// <summary>
        /// <c>SideHustle.API.Has(feature)</c> by reflection: false when Side Hustle is absent or too old to have
        /// <c>Has</c>. Pass the names from <c>SideHustle.ApiFeatures</c> as string literals ("session-events"), or
        /// read those constants inside a TryCall lambda.
        /// </summary>
        internal static bool Has(string feature)
        {
            var has = ApiType()?.GetMethod("Has", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            try { return has != null && (bool)has.Invoke(null, new object[] { feature }); }
            catch (Exception e) { Log(null, true, $"SideHustle.API.Has(\"{feature}\") failed: {e.InnerException?.Message ?? e.Message}"); return false; }
        }

        /// <summary>
        /// Build and register a gamemode when Side Hustle is installed. <paramref name="build"/> returns your
        /// <c>SideHustle.GamemodeDescriptor</c> typed as object, so this method's own signature carries no SideHustle
        /// type. Returns true when it registered. An absent Side Hustle is logged once per game run at Msg; any
        /// other failure is logged as a warning with its full exception.
        /// </summary>
        internal static bool TryRegister(Func<object> build, MelonLogger.Instance log = null)
        {
            if (build == null) { Log(log, true, "TryRegister needs a builder; nothing registered."); return false; }
            if (!IsPresent) { LogAbsent(log, "the gamemode is not registered"); return false; }
            try
            {
                RegisterCore(build());
                return true;
            }
            catch (Exception e)
            {
                Log(log, true, "registering with Side Hustle failed: " + e);
                return false;
            }
        }

        /// <summary>
        /// Run any Side Hustle call behind the same guard, e.g.
        /// <c>SideHustleLink.TryCall(() =&gt; SideHustle.API.ShowNotice("Round over"), "notice")</c>. Returns false
        /// (logged) when Side Hustle is absent or the call threw.
        /// </summary>
        internal static bool TryCall(Action call, string what, MelonLogger.Instance log = null)
        {
            if (call == null) { Log(log, true, $"TryCall({what}) needs an action; nothing called."); return false; }
            if (!IsPresent) { LogAbsent(log, "skipped " + what); return false; }
            try
            {
                call();
                return true;
            }
            catch (Exception e)
            {
                Log(log, true, $"Side Hustle call '{what}' failed: " + e);
                return false;
            }
        }

        // Its own method, never inlined: the SideHustle types live here, so a missing SideHustle.dll fails while
        // this one method is JIT-compiled - inside TryRegister's try - instead of taking the caller down with it.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RegisterCore(object descriptor)
        {
            SideHustle.API.Register((SideHustle.GamemodeDescriptor)descriptor);
        }

        private static Type ApiType()
        {
            if (!IsPresent) return null;
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "SideHustle", StringComparison.OrdinalIgnoreCase));
                return asm?.GetType("SideHustle.API", false);
            }
            catch (Exception e) { Log(null, true, "finding SideHustle.API failed: " + e.Message); return null; }
        }

        // Absence is an expected state, and TryCall may run often; say it once per game run.
        private static void LogAbsent(MelonLogger.Instance log, string what)
        {
            if (_absentLogged) return;
            _absentLogged = true;
            Log(log, false, $"Side Hustle is not installed; {what} (further skips are not logged).");
        }

        private static void Log(MelonLogger.Instance log, bool warn, string line)
        {
            if (log != null) { if (warn) log.Warning(line); else log.Msg(line); return; }
            if (warn) MelonLogger.Warning("[SideHustleLink] " + line);
            else MelonLogger.Msg("[SideHustleLink] " + line);
        }
    }
}
