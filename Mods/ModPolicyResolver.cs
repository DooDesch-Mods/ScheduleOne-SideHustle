using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace SideHustle.Mods
{
    /// <summary>The computed effect of a gamemode's mod policy against the current mod set.</summary>
    internal sealed class ModPlan
    {
        public List<string> ToDisable = new List<string>();        // loaded .dll files that won't be active in the session
        public List<string> ToEnable = new List<string>();         // .dll.disabled files (required) that will be activated
        public List<string> MissingRequired = new List<string>();  // required tokens with no file installed at all
        public List<string> KeepNames = new List<string>();        // friendly names kept (for the dialog)
        public List<string> KeepFiles = new List<string>();        // the .dll files that should be ACTIVE = the alt Mods set

        public bool HasChanges => ToDisable.Count > 0 || ToEnable.Count > 0;
        public bool Blocked => MissingRequired.Count > 0;
    }

    /// <summary>
    /// Turns a gamemode's <see cref="ModPolicy"/> into a concrete plan: which loaded mods to disable, which required
    /// mods to enable, and which required mods are missing. Always keeps the essentials, the gamemode's own mod, and
    /// the dependency closure of every kept mod, so nothing a kept mod relies on is disabled.
    /// </summary>
    internal static class ModPolicyResolver
    {
        private static readonly string[] Essentials = { "S1API", "SideHustle", "ModManagerPhoneApp", "MelonLoader" };
        private static readonly string[] MpEssentials = { "BiggerLobbies", "SteamNetworkLib" };

        internal static ModPlan Resolve(GamemodeDescriptor desc)
        {
            var plan = new ModPlan();
            if (desc?.Policy == null) return plan;

            var loaded = ModInventory.Loaded();
            var available = ModInventory.AvailableFiles();
            var nameMap = ModInventory.LoadNameMap();

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // essentials (and MP libraries for multiplayer gamemodes)
            foreach (var m in loaded)
            {
                if (ModInventory.MatchesAny(m, Essentials)) keep.Add(m.File);
                if (desc.AllowsMultiplayer && ModInventory.MatchesAny(m, MpEssentials)) keep.Add(m.File);
            }

            // the gamemode's own DLL
            string ownFile = OwnFile(desc, loaded);
            if (ownFile != null) keep.Add(ownFile);

            // allowed + required tokens -> kept files
            foreach (var tok in (desc.Policy.AllowedMods ?? Array.Empty<string>())
                                 .Concat(desc.Policy.RequiredMods ?? Array.Empty<string>()))
            {
                string f = ModInventory.Resolve(tok, loaded, available, nameMap);
                if (f != null) keep.Add(f);
            }

            // keep anything a kept mod depends on
            ExpandDependencies(keep, loaded);

            // disable every loaded mod that is not kept
            foreach (var m in loaded)
                if (!keep.Contains(m.File)) plan.ToDisable.Add(m.File);

            // required: enable if currently disabled on disk; report missing if not installed at all
            foreach (var tok in desc.Policy.RequiredMods ?? Array.Empty<string>())
            {
                string f = ModInventory.Resolve(tok, loaded, available, nameMap);
                if (f == null) { plan.MissingRequired.Add(tok); continue; }
                if (ModInventory.IsDisabledOnDisk(f)) plan.ToEnable.Add(f);
            }

            plan.ToDisable = plan.ToDisable.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            plan.ToEnable = plan.ToEnable.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            plan.MissingRequired = plan.MissingRequired.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            plan.KeepNames = loaded.Where(m => keep.Contains(m.File)).Select(m => m.Name).ToList();

            // The mods that should be ACTIVE in the gamemode session = the kept loaded mods + any required mod that
            // is currently disabled on disk (it gets activated by linking its file into the curated set).
            var keepFiles = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
            foreach (var f in plan.ToEnable) keepFiles.Add(f);
            plan.KeepFiles = keepFiles.ToList();
            return plan;
        }

        private static string OwnFile(GamemodeDescriptor desc, List<LoadedMod> loaded)
        {
            if (desc.OwnerAssembly == null) return null;
            foreach (var m in loaded)
            {
                if (m.Assembly == null) continue;
                if (ReferenceEquals(m.Assembly, desc.OwnerAssembly) || m.Assembly.FullName == desc.OwnerAssembly.FullName)
                    return m.File;
            }
            try { var loc = desc.OwnerAssembly.Location; if (!string.IsNullOrEmpty(loc)) return Path.GetFileName(loc); } catch { /* ignore */ }
            return null;
        }

        // Keep anything a kept mod depends on - by static assembly reference AND by MelonLoader's declared
        // dependency attributes (which cover runtime/reflection deps that GetReferencedAssemblies misses). When in
        // doubt this errs toward keeping a mod loaded, which is always safe (over-keeping never breaks a gamemode).
        private static void ExpandDependencies(HashSet<string> keep, List<LoadedMod> loaded)
        {
            bool changed = true;
            int guard = 0;
            while (changed && guard++ < 16)
            {
                changed = false;
                foreach (var keeper in loaded.Where(m => keep.Contains(m.File)).ToList())
                {
                    var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try { foreach (var r in keeper.Assembly?.GetReferencedAssemblies() ?? Array.Empty<AssemblyName>()) refs.Add(r.Name); } catch { /* ignore */ }
                    foreach (var d in DeclaredDeps(keeper)) refs.Add(StripExt(d));

                    foreach (var cand in loaded)
                    {
                        if (keep.Contains(cand.File) || cand.Assembly == null) continue;
                        string candAsm = null;
                        try { candAsm = cand.Assembly.GetName().Name; } catch { /* ignore */ }
                        if ((candAsm != null && refs.Contains(candAsm)) || refs.Contains(StripExt(cand.File)))
                        {
                            keep.Add(cand.File);
                            changed = true;
                        }
                    }
                }
            }
        }

        private static string StripExt(string f)
        {
            if (string.IsNullOrEmpty(f)) return f;
            if (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return f.Substring(0, f.Length - 4);
            return f;
        }

        /// <summary>MelonLoader-declared dependency assembly names from a mod's [Melon(Additional|Optional)Dependencies].
        /// Read reflectively so it survives MelonLoader API drift.</summary>
        private static IEnumerable<string> DeclaredDeps(LoadedMod keeper)
        {
            var names = new List<string>();
            try
            {
                var attrs = keeper.Assembly?.GetCustomAttributes(false);
                if (attrs == null) return names;
                foreach (var a in attrs)
                {
                    var t = a.GetType();
                    if (t == null || t.Name.IndexOf("Dependencies", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    foreach (var pn in new[] { "AssemblyNames", "Dependencies", "Mods" })
                    {
                        try { if (t.GetProperty(pn)?.GetValue(a) is string[] arr) names.AddRange(arr); }
                        catch { /* ignore */ }
                    }
                }
            }
            catch { /* ignore */ }
            return names;
        }
    }
}
