using System;
using System.Collections.Generic;
using System.Linq;
using DooDesch.UI;
using SideHustle.Multiplayer;
using SideHustle.Profiles;
using SideHustle.Shared;

namespace SideHustle.Menu
{
    /// <summary>
    /// The not-installed side of the gamemode list: catalogue rows for released gamemodes nobody is hosting right
    /// now, and the "install into a profile" step on the not-installed screen.
    /// </summary>
    internal static partial class Hub
    {
        // Catalogue gamemodes that are neither installed nor already shown as a live-lobby row. A gamemode with a
        // live lobby takes the lobby path (BuildGhostRows) instead, which can also join and install in one go.
        private static List<Row> BuildCatalogueRows(List<LobbyRow> advertised)
        {
            var rows = new List<Row>();
            var installed = new HashSet<string>(API.Registered.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
            var waitingNames = new HashSet<string>(
                Mods.GamemodeProbe.Waiting().Select(w => w.Name ?? ""), StringComparer.OrdinalIgnoreCase);
            var live = new HashSet<string>(
                (advertised ?? new List<LobbyRow>()).Where(l => !string.IsNullOrEmpty(l.GamemodeId)).Select(l => l.GamemodeId),
                StringComparer.OrdinalIgnoreCase);

            foreach (var entry in GamemodeCatalogue.All)
            {
                if (installed.Contains(entry.Id) || live.Contains(entry.Id) || waitingNames.Contains(entry.Name)) continue;
                var e = entry;
                rows.Add(new Row
                {
                    Name = e.Name,
                    Subtitle = e.Subtitle,
                    Corner = "Not installed",
                    OnClick = () => ShowUninstalledChoice(new Ghost { Name = e.Name, Url = e.Url, GamemodeId = e.Id })
                });
            }
            return rows;
        }

        // "Install into a profile": the package goes into the one profile there is, or the player picks.
        private static void StartCatalogueInstall(GamemodeCatalogue.Entry entry, Action back)
        {
            var profiles = ProfileEngine.LoadStore(out _).Profiles;
            if (profiles.Count == 0)
            {
                Core.Log?.Msg("[catalogue] install requested but there is no profile yet.");
                Notify("No profile yet. Make one under Profiles.", Severity.Warning);
                return;
            }
            if (profiles.Count == 1)
            {
                InstallCatalogueEntry(profiles[0].Id, entry);
                return;
            }

            var rows = profiles.Select(p =>
            {
                string id = p.Id;
                return new Row
                {
                    Name = p.Name,
                    Subtitle = p.Mods.Count == 1 ? "1 mod" : $"{p.Mods.Count} mods",
                    OnClick = () => InstallCatalogueEntry(id, entry)
                };
            }).ToList();
            rows.Add(new Row { Name = "Back", Subtitle = "Back to " + entry.Name + ".", OnClick = back });
            _back = back;
            ShowRows("Install into a profile", rows);
        }

        private static void InstallCatalogueEntry(string profileId, GamemodeCatalogue.Entry entry)
        {
            Core.Log?.Msg($"[catalogue] looking up the latest {entry.Package}.");
            Notify("Looking up " + entry.Name + "...", Severity.Info);
            System.Threading.Tasks.Task.Run(async () =>
            {
                string version = null;
                try
                {
                    var index = await ThunderstoreClient.GetIndexAsync(ProfileEngine.GameRoot, false, System.Threading.CancellationToken.None);
                    version = index?.Find(entry.Package)?.Latest?.VersionNumber;
                }
                catch (Exception e)
                {
                    Core.Log?.Warning($"[catalogue] Thunderstore lookup for {entry.Package} failed: {e.Message}");
                }
                MainThread.Post(() =>
                {
                    if (version == null)
                    {
                        Core.Log?.Warning($"[catalogue] no version found for {entry.Package}.");
                        Notify("Couldn't reach Thunderstore.", Severity.Danger);
                        return;
                    }
                    InstallIntoProfile(profileId, entry.Package, version);
                });
            });
        }
    }
}
