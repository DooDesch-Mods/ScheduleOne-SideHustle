using MelonLoader;
using MelonLoader.Melons;
using MelonLoader.Resolver;
using MelonLoader.Utils;

namespace SideHustle.Boot
{
    /// <summary>
    /// Hold the mod set back at startup so a lobby - not the launcher - decides what runs.
    /// </summary>
    /// <remarks>
    /// The problem it solves: every mod you own patches the game the moment you double-click, and changing that
    /// set costs a restart. Joining somebody's session therefore means quitting, and quitting means losing the
    /// menu you were standing in. If nothing loads until a lobby exists, the session can bring its own mods with
    /// it and the restart stops being part of joining.
    ///
    /// HOW, after two dead ends worth writing down.
    ///
    /// Moving the DLLs out of the way works - a subfolder of Mods without a manifest.json is invisible to the
    /// scanner - and it is still the wrong answer: those files belong to a mod manager, the move has to be undone,
    /// and a crash in between leaves somebody's mod folder gutted with nothing running that knows how to fix it.
    ///
    /// Patching <c>MelonAssembly.LoadMelonAssembly</c>, which every mod file passes through, does not work at all.
    /// MelonLoader carries an assembly-level <c>[PatchShield]</c> and refuses Harmony patches on its own methods -
    /// silently, with no error and no warning. The patch applies, reports success, and never runs.
    ///
    /// What is left is the API MelonLoader offers for exactly this: <c>MelonFolderHandler.AddFullPathExclusion</c>.
    /// It only works on DIRECTORIES, never on a single file, so the whole Mods folder comes out of the scan and
    /// this plugin loads the handful that must run anyway. Nothing moves, nothing is patched, and the mechanism is
    /// one MelonLoader publishes rather than one it tolerates.
    ///
    /// The window is exact: plugins load, then <c>OnPreModsLoaded</c> (here), then <c>LoadMelons(ScanType.Mods)</c>
    /// finds an empty list. Excluding the folder also drops it from the assembly RESOLVER, which would break
    /// dependency lookups for everything loaded later, so it goes straight back.
    /// </remarks>
    internal static class ModGate
    {
        /// <summary>Entry in the mod's own MelonPreferences category, so the switch sits with the rest of Side
        /// Hustle's settings instead of in a second place nobody finds.</summary>
        private const string CategoryId = "SideHustle_01_Main";
        private const string EntryId = "DeferModsUntilLobby";
        private const string KeepEntryId = "AlwaysLoadMods";

        /// <summary>What still loads at startup, whatever the switch says.
        ///
        /// Side Hustle has to: it is the thing that loads the others. Sideload has to as well - the menu's own
        /// columns are Sideload surfaces, and a hub that cannot draw is a hub nobody can host from. Everything
        /// else is the session's business.</summary>
        private static readonly string[] AlwaysLoad =
        {
            "SideHustle.dll",
            "Sideload.dll",
        };

        private static readonly List<string> _deferred = new List<string>();

        internal static bool Enabled { get; private set; }

        /// <summary>Take the Mods folder out of the scan and load the few that must run. Called from
        /// OnPreModsLoaded, the last moment before MelonLoader reads the folder.</summary>
        internal static void Arm(MelonLogger.Instance log)
        {
            Enabled = ReadPreference();
            _keepExtra = ReadKeepList();   // read even when off, so the entry exists to be edited
            if (!Enabled) return;

            string modsDir = MelonEnvironment.ModsDirectory;
            if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir)) { Enabled = false; return; }

            var found = Collect(modsDir);
            if (found.Count == 0) { Enabled = false; return; }

            try
            {
                // The exclusion is compared by exact string, so it has to be the same value ScanForFolders used.
                MelonFolderHandler.AddFullPathExclusion(modsDir);
                // ...and put the folder back where assemblies are LOOKED UP. The exclusion removes it from both,
                // and a late-loaded mod whose dependency sits beside it would otherwise fail to resolve.
                MelonAssemblyResolver.AddSearchDirectory(modsDir);
            }
            catch (Exception e)
            {
                log.Error("[gate] could not hold the mod folder back; loading your mods normally: " + e);
                Enabled = false;
                return;
            }

            var keep = KeepSet(found, log);
            int loaded = 0;
            var waiting = new List<string>();
            foreach (string file in found)
            {
                if (keep.Contains(file)) { if (LoadNow(file, log)) loaded++; }
                else waiting.Add(file);
            }
            _deferred.AddRange(InDependencyOrder(waiting));

            log.Msg($"[gate] {loaded} mod(s) loaded now, {_deferred.Count} held back until a lobby starts.");
        }

        /// <summary>The scan is done - write down what happened, for whoever asks why a mod is not running.</summary>
        internal static void Disarm(MelonLogger.Instance log)
        {
            if (!Enabled) return;
            try
            {
                string dir = Path.Combine(MelonEnvironment.UserDataDirectory, "SideHustle");
                Directory.CreateDirectory(dir);
                File.WriteAllLines(Path.Combine(dir, "deferred-mods.txt"), _deferred);
            }
            catch (Exception e) { log.Warning("[gate] could not write the deferred list: " + e.Message); }
        }

        /// <summary>
        /// Load one mod the way MelonLoader would have.
        ///
        /// Two calls, not one: LoadMelonAssembly reads the file and finds the melon inside it, then stops - no
        /// logger, no Harmony instance, no OnInitializeMelon. Register() is what makes it a running mod, and it
        /// puts the melon on the same OnApplicationStart it would have ridden anyway, so nothing about its
        /// lifetime is unusual from here on.
        /// </summary>
        private static bool LoadNow(string file, MelonLogger.Instance log)
        {
            try
            {
                var asm = MelonAssembly.LoadMelonAssembly(file);
                if (asm?.LoadedMelons == null) return false;
                bool any = false;
                foreach (var melon in asm.LoadedMelons)
                    if (melon.Register()) any = true;
                return any;
            }
            catch (Exception e)
            {
                log.Error("[gate] " + Path.GetFileName(file) + " could not be loaded: " + e);
                return false;
            }
        }

        /// <summary>Names the player added to the allowlist, from the preference. Read once at arm time.</summary>
        private static string[] _keepExtra = Array.Empty<string>();

        /// <summary>
        /// Everything that has to load now: the named mods, plus whatever they are built against.
        /// </summary>
        /// <remarks>
        /// The second half is not optional, and the first boot proved it. Side Hustle is compiled against S1API,
        /// which is itself a mod in this folder - held it back, and the hub threw FileNotFoundException the moment
        /// it drew a row. A library that another mod links is not deferrable at all: the mod that needs it is
        /// already running.
        ///
        /// So the list closes over assembly references rather than being written down. Reading them is what
        /// MelonLoader's own scanner does to every file anyway (Mono.Cecil, no loading, no side effects), and a
        /// closure means the next library nobody thought of is kept for the same reason the first one was.
        /// </remarks>
        private static HashSet<string> KeepSet(List<string> found, MelonLogger.Instance log)
        {
            var byAssemblyName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in found)
            {
                string asmName = AssemblyNameOf(file);
                if (asmName != null && !byAssemblyName.ContainsKey(asmName)) byAssemblyName[asmName] = file;
            }

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            foreach (string file in found)
                if (IsAlwaysLoad(file) && keep.Add(file)) queue.Enqueue(file);

            while (queue.Count > 0)
            {
                string file = queue.Dequeue();
                foreach (string reference in ReferencesOf(file))
                {
                    if (!byAssemblyName.TryGetValue(reference, out string dep)) continue;
                    if (!keep.Add(dep)) continue;
                    queue.Enqueue(dep);
                    log.Msg($"[gate] keeping {Path.GetFileName(dep)} - {Path.GetFileName(file)} is built against it.");
                }
            }
            return keep;
        }

        /// <summary>
        /// Order the held-back mods so a mod is loaded after everything it is built against.
        /// </summary>
        /// <remarks>
        /// Alphabetical was the first order, and it lasted one run: Backrooms came up before TightBeam, looked for
        /// TightBeam among the loaded mods, found nothing and told the player to install it and start the game
        /// again. MelonLoader never hits that because it loads the whole folder before any of them initializes;
        /// loading one at a time puts every mod that inspects its neighbours at risk.
        ///
        /// The same reference graph the allowlist closes over answers this too. Cycles cannot happen between
        /// assemblies, but a broken read can, so anything that does not sort simply keeps its place at the end.
        /// </remarks>
        private static List<string> InDependencyOrder(List<string> files)
        {
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                var facts = Facts(file);
                if (facts.AssemblyName != null && !byName.ContainsKey(facts.AssemblyName)) byName[facts.AssemblyName] = file;
                if (facts.MelonName != null && !byName.ContainsKey(facts.MelonName)) byName[facts.MelonName] = file;
            }

            var ordered = new List<string>();
            var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Place(string file)
            {
                if (placed.Contains(file) || !visiting.Add(file)) return;
                var facts = Facts(file);
                foreach (string need in facts.References)
                    if (byName.TryGetValue(need, out string dep) && dep != file) Place(dep);
                foreach (string need in facts.DeclaredDependencies)
                    if (byName.TryGetValue(need, out string dep) && dep != file) Place(dep);
                visiting.Remove(file);
                if (placed.Add(file)) ordered.Add(file);
            }

            foreach (string file in files) Place(file);
            foreach (string file in files) if (placed.Add(file)) ordered.Add(file);
            return ordered;
        }

        /// <summary>What one mod file says about itself, read once. Assembly identity, the name MelonLoader knows
        /// it by, and everything it says it needs.</summary>
        private sealed class ModFacts
        {
            internal string AssemblyName;
            internal string MelonName;
            internal readonly List<string> References = new List<string>();
            internal readonly List<string> DeclaredDependencies = new List<string>();
        }

        private static readonly Dictionary<string, ModFacts> _facts = new Dictionary<string, ModFacts>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Read a mod's identity and its dependencies without loading it.
        /// </summary>
        /// <remarks>
        /// Two kinds of dependency, and only reading both gets the order right.
        ///
        /// An assembly REFERENCE is what a mod is compiled against - that is how Side Hustle needs S1API, and
        /// missing it throws FileNotFoundException the moment a screen draws.
        ///
        /// A DECLARED dependency is a name in <c>MelonAdditionalDependencies</c> or
        /// <c>MelonOptionalDependencies</c>, and it leaves no reference at all: Backrooms needs TightBeam, ships a
        /// copy of its API as source, and asks <c>MelonBase.FindMelon("TightBeam", ...)</c> at startup. Sorted by
        /// references alone it loads first, finds nothing, and tells the player to install a mod they already have.
        /// </remarks>
        private static ModFacts Facts(string file)
        {
            if (_facts.TryGetValue(file, out var cached)) return cached;
            var facts = new ModFacts();
            try
            {
                using var def = Mono.Cecil.AssemblyDefinition.ReadAssembly(file);
                facts.AssemblyName = def.Name?.Name;
                foreach (var reference in def.MainModule.AssemblyReferences) facts.References.Add(reference.Name);

                foreach (var attribute in def.CustomAttributes)
                {
                    string type = attribute.AttributeType?.Name ?? "";
                    if (type == "MelonInfoAttribute" && attribute.ConstructorArguments.Count > 1)
                        facts.MelonName = attribute.ConstructorArguments[1].Value as string;
                    else if (type == "MelonAdditionalDependenciesAttribute" || type == "MelonOptionalDependenciesAttribute")
                        foreach (var argument in attribute.ConstructorArguments)
                            if (argument.Value is Mono.Cecil.CustomAttributeArgument[] names)
                                foreach (var one in names)
                                    if (one.Value is string s && s.Length > 0) facts.DeclaredDependencies.Add(s);
                }
            }
            catch { /* unreadable: no identity, no edges, and it keeps its place */ }
            _facts[file] = facts;
            return facts;
        }

        private static string AssemblyNameOf(string file) => Facts(file).AssemblyName;

        private static List<string> ReferencesOf(string file) => Facts(file).References;

        private static bool IsAlwaysLoad(string file)
        {
            string name = Path.GetFileName(file);
            foreach (string keep in AlwaysLoad)
                if (string.Equals(name, keep, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string keep in _keepExtra)
                if (string.Equals(name, keep, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// The player's own additions to the allowlist, as file names separated by commas.
        ///
        /// Not a nicety. The first thing the gate held back on a dev machine was the MCP bridge this project drives
        /// the game with - so the tooling that would have verified the gate was the thing the gate switched off.
        /// A mod that has to be there before a lobby exists is a real category: a bridge, an overlay, a profiler,
        /// anything a player looks at in the main menu.
        /// </summary>
        private static string[] ReadKeepList()
        {
            try
            {
                var category = MelonPreferences.GetCategory(CategoryId) ?? MelonPreferences.CreateCategory(CategoryId);
                var entry = category.GetEntry<string>(KeepEntryId)
                            ?? category.CreateEntry(KeepEntryId, "",
                                "Mods that always load, even when the rest waits",
                                "File names separated by commas, e.g. ScheduleMCP.dll, Hotline.dll. Side Hustle and "
                                + "Sideload are always on this list and do not need naming.");
                string raw = entry.Value ?? "";
                if (raw.Trim().Length == 0) return Array.Empty<string>();
                var names = new List<string>();
                foreach (string part in raw.Split(','))
                {
                    string name = part.Trim();
                    if (name.Length == 0) continue;
                    if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) name += ".dll";
                    names.Add(name);
                }
                return names.ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        /// <summary>
        /// Every DLL MelonLoader would have loaded: the folder itself, plus each immediate subfolder carrying a
        /// manifest.json - which is precisely the rule its own scanner uses. Mirroring it matters because anything
        /// missed here is a mod that neither loads nor appears in the held-back list, and that is invisible.
        /// </summary>
        private static List<string> Collect(string modsDir)
        {
            var files = new List<string>();
            try
            {
                files.AddRange(Directory.GetFiles(modsDir, "*.dll", SearchOption.TopDirectoryOnly));
                foreach (string sub in Directory.GetDirectories(modsDir, "*", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(sub);
                    // MelonLoader's own default exclusions, so a folder it ignores stays ignored here too.
                    if (name.StartsWith("~") || name.StartsWith(".")) continue;
                    if (name == "Broken" || name == "Retired" || name == "Disabled") continue;
                    if (!File.Exists(Path.Combine(sub, "manifest.json"))) continue;
                    files.AddRange(Directory.GetFiles(sub, "*.dll", SearchOption.TopDirectoryOnly));
                }
            }
            catch { /* an unreadable folder means the gate does not apply */ }
            return files;
        }

        /// <summary>
        /// Read the switch. MelonPreferences.Load() runs before plugins, so the saved value is already in memory
        /// and a category created here picks it up; a first run creates the entry switched OFF.
        ///
        /// Off by default on purpose. Loading a mod after the menu exists is not the order its author tested, and
        /// one that patches something the game already ran, or that needs MelonLoader's early hooks, will not say
        /// so - it will just quietly not work. That is a choice for the player to make deliberately, per install.
        /// </summary>
        private static bool ReadPreference()
        {
            try
            {
                var category = MelonPreferences.GetCategory(CategoryId) ?? MelonPreferences.CreateCategory(CategoryId);
                var entry = category.GetEntry<bool>(EntryId)
                            ?? category.CreateEntry(EntryId, false,
                                "Load mods only when a lobby starts",
                                "Nothing but Side Hustle and Sideload loads at startup; the rest loads when you host or join. "
                                + "Fewer restarts when joining, but a mod that needs to patch the game early may stop working.");
                return entry.Value;
            }
            catch { return false; }
        }
    }
}
