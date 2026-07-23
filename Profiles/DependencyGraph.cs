using System;
using System.Collections.Generic;
using System.Linq;
using SideHustle.Shared;

namespace SideHustle.Profiles
{
    /// <summary>
    /// The dependency relationships INSIDE one profile, built from the Thunderstore index: who requires whom, who
    /// would be left broken by a removal, and which auto-installed dependencies nothing needs anymore. Nodes are
    /// the profile's refs resolved to package full names - thunderstore refs directly, base/local refs only through
    /// the CONFIRMED DLL-to-package map (a fuzzy guess must never drive a cascade). Edges are the pinned version's
    /// dependency list, filtered to packages present in the profile; edges into the MelonLoader pseudo-package or
    /// an essential (Side Hustle / S1API - always present in every profile) are skipped entirely. Pure BCL and
    /// side-effect free; with no index available the graph is edge-free and every query degrades to "no findings".
    /// </summary>
    internal sealed class DependencyGraph
    {
        private sealed class Node
        {
            public ProfileModRef Ref;
            public string PkgName;                            // resolved package full name, or null (unknown identity)
            public readonly List<string> DepNames = new List<string>();   // in-profile deps only (post skip rules)
            public readonly List<string> MissingDeps = new List<string>();
        }

        private readonly List<Node> _nodes = new List<Node>();
        private readonly Dictionary<string, List<Node>> _byPkg = new Dictionary<string, List<Node>>(StringComparer.OrdinalIgnoreCase);

        private DependencyGraph() { }

        internal static DependencyGraph Build(ProfileDef p, TsIndex index, Func<string, string> confirmedPackageOf = null)
        {
            var g = new DependencyGraph();
            if (p?.Mods == null) return g;

            foreach (var r in p.Mods)
            {
                string pkg = r.Source == "thunderstore" ? r.FullName : confirmedPackageOf?.Invoke(r.File);
                var node = new Node { Ref = r, PkgName = string.IsNullOrEmpty(pkg) ? null : pkg };
                g._nodes.Add(node);
                if (node.PkgName != null)
                {
                    if (!g._byPkg.TryGetValue(node.PkgName, out var list)) g._byPkg[node.PkgName] = list = new List<Node>();
                    list.Add(node);
                }
            }

            if (index == null) return g;   // offline, no cache: edge-free graph

            foreach (var node in g._nodes)
            {
                if (node.PkgName == null) continue;
                var pkg = index.Find(node.PkgName);
                if (pkg == null) continue;   // removed from the index: no dependency knowledge, degrade
                var ver = (node.Ref.Source == "thunderstore" ? pkg.Get(node.Ref.Version) : null) ?? pkg.Latest;
                if (ver?.Dependencies == null) continue;

                foreach (var dep in ver.Dependencies)
                {
                    if (!TsIndex.SplitDependency(dep, out var depName, out _)) continue;
                    if (depName.Equals("LavaGang-MelonLoader", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Essentials.IsEssentialPackageName(depName)) continue;   // always present, never a finding
                    if (g._byPkg.ContainsKey(depName)) node.DepNames.Add(depName);
                    else node.MissingDeps.Add(depName);
                }
            }
            return g;
        }

        /// <summary>Every ref that DIRECTLY requires <paramref name="target"/> (for "required by ..." hints).</summary>
        internal List<ProfileModRef> DirectDependantsOf(ProfileModRef target)
        {
            var names = PkgNamesOf(target);
            if (names.Count == 0) return new List<ProfileModRef>();
            return _nodes
                .Where(n => !SameRef(n.Ref, target) && n.DepNames.Any(d => names.Contains(d)))
                .Select(n => n.Ref)
                .ToList();
        }

        /// <summary>Every ref that requires <paramref name="target"/> directly or transitively - the set that would
        /// break if it were removed. Essentials never appear (they are roots, not dependants).</summary>
        internal List<ProfileModRef> DependantsOf(ProfileModRef target)
        {
            var affected = new HashSet<string>(PkgNamesOf(target), StringComparer.OrdinalIgnoreCase);
            if (affected.Count == 0) return new List<ProfileModRef>();

            var result = new List<ProfileModRef>();
            var resultNodes = new HashSet<Node>();
            bool grew = true;
            int guard = 0;
            while (grew && guard++ < 1000)
            {
                grew = false;
                foreach (var n in _nodes)
                {
                    if (n.PkgName == null || resultNodes.Contains(n) || SameRef(n.Ref, target)) continue;
                    if (!n.DepNames.Any(d => affected.Contains(d))) continue;
                    resultNodes.Add(n);
                    result.Add(n.Ref);
                    if (affected.Add(n.PkgName)) grew = true;
                }
            }
            return result.Where(r => !Essentials.IsEssentialRef(r)).ToList();
        }

        /// <summary>Index-known dependencies of <paramref name="r"/> that are NOT in the profile (post skip rules) -
        /// the "missing: X" hint after a "remove only this one".</summary>
        internal List<string> MissingDepsOf(ProfileModRef r)
        {
            var node = _nodes.FirstOrDefault(n => SameRef(n.Ref, r));
            return node != null ? new List<string>(node.MissingDeps) : new List<string>();
        }

        /// <summary>
        /// Auto-installed thunderstore refs (AsDependency) that no root still needs: mark-and-sweep from every
        /// manual/base/local/essential ref along dependency edges; whatever auto-installed ref stays unmarked is an
        /// orphan (apt-autoremove semantics).
        /// </summary>
        internal List<ProfileModRef> Orphans()
        {
            var marked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();

            foreach (var n in _nodes)
            {
                bool isRoot = n.Ref.Source != "thunderstore" || !n.Ref.AsDependency || Essentials.IsEssentialRef(n.Ref);
                if (!isRoot) continue;
                foreach (var d in n.DepNames)
                    if (marked.Add(d)) queue.Enqueue(d);
            }

            int guard = 0;
            while (queue.Count > 0 && guard++ < 10_000)
            {
                string pkg = queue.Dequeue();
                if (!_byPkg.TryGetValue(pkg, out var providers)) continue;
                foreach (var provider in providers)
                    foreach (var d in provider.DepNames)
                        if (marked.Add(d)) queue.Enqueue(d);
            }

            return _nodes
                .Where(n => n.Ref.Source == "thunderstore" && n.Ref.AsDependency
                            && !Essentials.IsEssentialRef(n.Ref)
                            && n.PkgName != null && !marked.Contains(n.PkgName))
                .Select(n => n.Ref)
                .ToList();
        }

        private List<string> PkgNamesOf(ProfileModRef target)
        {
            return _nodes.Where(n => SameRef(n.Ref, target) && n.PkgName != null)
                         .Select(n => n.PkgName)
                         .ToList();
        }

        /// <summary>The identity triple every removal path matches on (same as the remove dialog's lookup).</summary>
        internal static bool SameRef(ProfileModRef a, ProfileModRef b)
        {
            if (a == null || b == null) return false;
            return a.Source == b.Source
                   && string.Equals(a.File, b.File, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
