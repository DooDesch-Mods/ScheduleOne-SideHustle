using System;
using System.Collections.Generic;
using System.IO;

namespace SideHustle.Mods
{
    /// <summary>
    /// The gamemodes that are installed but not loaded, so the menu can list them anyway.
    /// </summary>
    /// <remarks>
    /// With the boot gate on, a gamemode mod has not run, has not called <c>API.Register</c>, and is therefore
    /// invisible to the hub - which would leave the gamemode list empty on a machine with four of them installed.
    /// That is the one thing that made the gate unusable rather than merely unfinished.
    ///
    /// Nothing needs to be declared for this to work. A gamemode mod is built against SideHustle.dll, because that
    /// reference is what <c>API.Register</c> comes from and nothing else in the mod needs it - so the assembly
    /// reference IS the marker. Measured on a 29-mod install: exactly PropHunt, Inkubator and Personify, which is
    /// exactly the set that registers a gamemode.
    ///
    /// The boot plugin already read every file to sort them, so it writes this list out rather than having the mod
    /// parse all of them a second time for the same answer.
    ///
    /// What a row cannot show before loading is the descriptor - description, icon, singleplayer or multiplayer.
    /// Those live in code that has not run. The row carries the mod's own name, version and author, and loading it
    /// on click replaces the row with the real thing.
    /// </remarks>
    internal static class GamemodeProbe
    {
        internal sealed class Candidate
        {
            internal string File;
            internal string Name;
            internal string Version;
            internal string Author;
            /// <summary>What this one needs, itself last, in load order. Computed by the boot plugin, which is the
            /// only place that read every file's references anyway.</summary>
            internal string[] Closure = Array.Empty<string>();
        }

        private static List<Candidate> _found;

        /// <summary>Installed gamemodes that are not running. Empty on a normal boot, and empty again once they
        /// have been loaded - a candidate whose file is no longer waiting is one that is already on the list for
        /// real.</summary>
        internal static List<Candidate> Waiting()
        {
            _found ??= Read();
            var still = new List<Candidate>();
            foreach (var candidate in _found)
                if (LateLoader.IsPending(candidate.File)) still.Add(candidate);
            return still;
        }

        /// <summary>Load one candidate and everything it needs. The mod registers its own descriptor as it
        /// initializes, so the caller's next render shows the real row instead of this stand-in.</summary>
        internal static bool Load(Candidate candidate)
        {
            if (candidate == null) return false;
            Core.Log?.Msg($"[gate] loading {candidate.Name} and {Math.Max(0, candidate.Closure.Length - 1)} "
                          + "mod(s) it needs, because the player asked for it.");
            return LateLoader.LoadClosure(candidate.Closure, candidate.File);
        }

        private static List<Candidate> Read()
        {
            var list = new List<Candidate>();
            try
            {
                string file = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory,
                    "SideHustle", "deferred-gamemodes.txt");
                if (!File.Exists(file)) return list;
                foreach (string line in File.ReadAllLines(file))
                {
                    var parts = line.Split('|');
                    if (parts.Length < 2 || parts[0].Length == 0) continue;
                    var candidate = new Candidate
                    {
                        File = parts[0],
                        Name = parts[1],
                        Version = parts.Length > 2 ? parts[2] : "",
                        Author = parts.Length > 3 ? parts[3] : "",
                    };
                    if (parts.Length > 4 && parts[4].Length > 0)
                        candidate.Closure = parts[4].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                    if (candidate.Closure.Length == 0) candidate.Closure = new[] { candidate.File };
                    list.Add(candidate);
                }
            }
            catch (Exception e) { Core.Log?.Warning("[gate] could not read the gamemode candidates: " + e.Message); }
            return list;
        }
    }
}
