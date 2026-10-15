using System;
using System.Collections.Generic;

namespace SideHustle.Menu
{
    /// <summary>
    /// Released gamemodes Side Hustle knows by name, so the list can offer them before any lobby advertises one.
    /// Only gamemodes with a published release belong here; the id is the one the gamemode registers.
    /// </summary>
    internal static class GamemodeCatalogue
    {
        internal sealed class Entry
        {
            /// <summary>The id the gamemode registers with Side Hustle.</summary>
            internal string Id;
            internal string Name;
            internal string Subtitle;
            /// <summary>Thunderstore package, "Owner-Name".</summary>
            internal string Package;
            /// <summary>Page the Get it button opens (must pass <see cref="DownloadLink.IsAllowed"/>).</summary>
            internal string Url;
        }

        internal static readonly IReadOnlyList<Entry> All = new[]
        {
            new Entry
            {
                Id = "doodesch.prophunt",
                Name = "PropHunt",
                Subtitle = "Hiders disguise as props; hunters find them before the timer runs out.",
                Package = "DooDesch-PropHunt",
                Url = "https://thunderstore.io/c/schedule-i/p/DooDesch/PropHunt/",
            },
        };

        internal static Entry Find(string gamemodeId)
        {
            if (string.IsNullOrEmpty(gamemodeId)) return null;
            foreach (var e in All)
                if (string.Equals(e.Id, gamemodeId, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }
    }
}
