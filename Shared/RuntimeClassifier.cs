using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace SideHustle.Shared
{
    /// <summary>Which game branch a mod DLL is built for. Schedule I ships two incompatible branches: IL2CPP
    /// (default/beta, net6, Il2CppScheduleOne.*) and Mono (alternate, net472, ScheduleOne.*) - a DLL built for one
    /// does not load on the other. Universal = no branch-specific references (S1API-only or pure library), loads on
    /// both. Unknown = could not be determined, treated as loadable (never a false positive).</summary>
    internal enum ModRuntime { Unknown, Il2Cpp, Mono, Universal }

    /// <summary>
    /// Classifies mod DLLs by game branch WITHOUT loading them, via System.Reflection.Metadata (in-box on net6).
    /// Metadata evidence is primary: assembly references first (Il2Cpp*/UnhollowerBaseLib vs Assembly-CSharp), then
    /// type-reference namespaces for ILMerged mods - always testing IL2CPP BEFORE Mono, because "Il2CppScheduleOne"
    /// contains "ScheduleOne" (this precedence is load-bearing in every reference implementation). Filename tokens
    /// ("_Mono"/"_IL2CPP") are only a FALLBACK for unreadable files and a heuristic for package names - a shipped
    /// library like Mono.Cecil.dll must never be excluded on its name when its metadata says it is branch-neutral.
    /// Pure BCL; compiled into the mod, the boot plugin and the test harness.
    /// </summary>
    internal static class RuntimeClassifier
    {
        /// <summary>This build of SideHustle targets the IL2CPP branch (compile-time constant), so only a PROVEN
        /// Mono build is ever the wrong runtime. Unknown/Universal always load.</summary>
        internal static bool IsWrongForThisGame(ModRuntime r) => r == ModRuntime.Mono;

        // Metadata results cached per (path, size, mtime) for the process lifetime - resolves run on every
        // detail-open/build and must not re-parse fifty PE files each time.
        private static readonly ConcurrentDictionary<string, ModRuntime> Cache =
            new ConcurrentDictionary<string, ModRuntime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Branch hint from name tokens alone: "mono"/"il2cpp" as a WHOLE token, split on '.', '_', '-'
        /// and spaces (so "Monolith" or "MonoBehaviourPatch" never match). Works on file names including the
        /// ".dll.disabled" suffix and on Thunderstore package names like "Owner-CashDrops_MONO".</summary>
        internal static ModRuntime FromNameTokens(string name)
        {
            if (string.IsNullOrEmpty(name)) return ModRuntime.Unknown;
            foreach (var token in name.Split('.', '_', '-', ' '))
            {
                if (token.Equals("il2cpp", StringComparison.OrdinalIgnoreCase)) return ModRuntime.Il2Cpp;
                if (token.Equals("mono", StringComparison.OrdinalIgnoreCase)) return ModRuntime.Mono;
            }
            return ModRuntime.Unknown;
        }

        /// <summary>Classify a DLL on disk. Metadata first (authoritative); the name-token heuristic only decides
        /// when the file cannot be read at all. Every failure path yields Unknown - which always loads.</summary>
        internal static ModRuntime ClassifyFile(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return FromNameTokens(Path.GetFileName(path));

                string key = path.ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
                if (Cache.TryGetValue(key, out var cached)) return cached;

                var result = ClassifyMetadata(path);
                if (result == ModRuntime.Unknown)
                {
                    // Unreadable/native file: the name suffix is the only evidence left.
                    var byName = FromNameTokens(Path.GetFileName(path));
                    if (byName != ModRuntime.Unknown) result = byName;
                }
                Cache[key] = result;
                return result;
            }
            catch { return ModRuntime.Unknown; }
        }

        private static ModRuntime ClassifyMetadata(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var pe = new PEReader(fs);
                if (!pe.HasMetadata) return ModRuntime.Unknown;   // native / resource-only image
                var md = pe.GetMetadataReader();

                bool il2cpp = false, mono = false;

                // Assembly references give a POSITIVE IL2CPP signal only. Crucially, "Assembly-CSharp" is NOT a
                // Mono signal: Il2CppInterop proxy assemblies keep the ORIGINAL assembly name - an IL2CPP mod that
                // touches game types references an assembly literally named Assembly-CSharp. Only the NAMESPACES
                // carry the branch (Il2CppScheduleOne.* vs ScheduleOne.*), which is why the reference
                // implementations classify by namespace and never by assembly name. The one exception below is
                // S1API: both its builds share the assembly name "S1API", so only the target runtime tells them apart.
                bool refsS1API = false, refsMscorlib = false, refsNetCore = false;
                foreach (var handle in md.AssemblyReferences)
                {
                    string name = md.GetString(md.GetAssemblyReference(handle).Name);
                    if (name.StartsWith("Il2Cpp", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("UnhollowerBaseLib", StringComparison.OrdinalIgnoreCase))
                        il2cpp = true;
                    else if (name.StartsWith("S1API", StringComparison.OrdinalIgnoreCase)) refsS1API = true;
                    else if (name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)) refsMscorlib = true;
                    else if (name.Equals("System.Runtime", StringComparison.OrdinalIgnoreCase) ||
                             name.Equals("System.Private.CoreLib", StringComparison.OrdinalIgnoreCase) ||
                             name.Equals("netstandard", StringComparison.OrdinalIgnoreCase))
                        refsNetCore = true;
                }
                if (il2cpp) return ModRuntime.Il2Cpp;

                // Type-reference namespaces decide: Il2Cpp* first ("Il2CppScheduleOne" contains "ScheduleOne").
                foreach (var handle in md.TypeReferences)
                {
                    string ns = md.GetString(md.GetTypeReference(handle).Namespace);
                    if (ns.StartsWith("Il2Cpp", StringComparison.Ordinal)) { il2cpp = true; break; }
                    if (ns.Equals("ScheduleOne", StringComparison.Ordinal) ||
                        ns.StartsWith("ScheduleOne.", StringComparison.Ordinal))
                        mono = true;
                }
                if (il2cpp) return ModRuntime.Il2Cpp;
                if (mono) return ModRuntime.Mono;

                // An S1API mod carries no game-branch namespace (it only touches S1API's own types), so the namespace
                // check above cannot see the branch - but the two S1API builds target different runtimes: the Mono
                // one is .NET Framework (references mscorlib), the IL2CPP one is .NET (references System.Runtime). A
                // .NET-Framework S1API mod is a Mono build that cannot load on this IL2CPP game. Gated on the S1API
                // reference so a plain .NET-Framework utility library (Mono.Cecil, an old Newtonsoft) still stays
                // Universal below.
                if (refsS1API && refsMscorlib && !refsNetCore) return ModRuntime.Mono;

                // A readable managed assembly with NO branch signal is branch-neutral: a pure library (Mono.Cecil,
                // Harmony, Newtonsoft...) that loads on both branches. It is deliberately Universal, NOT Unknown -
                // only a PE we could not read at all stays Unknown, so the name-token fallback in ClassifyFile never
                // fires on a readable-but-neutral file and cannot mislabel e.g. "Mono.Cecil.dll" as a Mono build.
                return ModRuntime.Universal;
            }
            catch { return ModRuntime.Unknown; }
        }

        internal static string ToTag(ModRuntime r) => r switch
        {
            ModRuntime.Il2Cpp => "il2cpp",
            ModRuntime.Mono => "mono",
            ModRuntime.Universal => "universal",
            _ => "unknown",
        };

        internal static ModRuntime FromTag(string tag) => (tag ?? "").ToLowerInvariant() switch
        {
            "il2cpp" => ModRuntime.Il2Cpp,
            "mono" => ModRuntime.Mono,
            "universal" => ModRuntime.Universal,
            _ => ModRuntime.Unknown,
        };
    }
}
