using System;
using System.IO;
using System.Text.Json;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Networking;
using Il2CppScheduleOne.Persistence;
using SideHustle.Phone;
using UnityEngine;

namespace SideHustle.Menu
{
    // The "Continue" card at the top of the hub: the last vanilla save, loaded with one click. Side Hustle must never
    // stand between a player and normal play, so this is the shortest path on the page.
    internal static partial class Hub
    {
        private static float _resumeAge = 999f;
        private static int _resumeIndex = -1;
        private static string _resumeName = "";
        private static string _resumeDetail = "";

        // The last played save (LoadManager.LastPlayedGame), re-read at most every couple of seconds.
        private static Json ResumeJson()
        {
            _resumeAge += Time.unscaledDeltaTime;
            if (_resumeAge > 2f)
            {
                _resumeAge = 0f;
                ReadLastPlayed();
            }
            if (_resumeIndex < 0) return null;
            return Json.Object()
                .Add("slot", _resumeIndex)
                .Add("name", _resumeName)
                .Add("detail", _resumeDetail);
        }

        private static void ReadLastPlayed()
        {
            _resumeIndex = -1;
            try
            {
                var last = LoadManager.LastPlayedGame;
                if (last == null) return;   // no save yet: the card stays away
                var saves = LoadManager.SaveGames;
                if (saves == null) { Core.Log?.Warning("[hub3] continue: the save list is not loaded."); return; }
                for (int i = 0; i < saves.Length; i++)
                {
                    var info = saves[i];
                    if (info == null || info.SavePath != last.SavePath) continue;
                    _resumeIndex = i;
                    _resumeName = string.IsNullOrEmpty(info.OrganisationName) ? $"Save {i + 1}" : info.OrganisationName;
                    int day = ElapsedDays(info.SavePath);
                    string worth = $"${info.Networth:n0}";
                    _resumeDetail = day >= 0 ? $"Day {day + 1} - {worth}" : worth;
                    return;
                }
                Core.Log?.Warning($"[hub3] continue: the last played save '{last.SavePath}' is not in the save list.");
            }
            catch (Exception e) { Core.Log?.Error("[hub3] continue: reading the last played save failed: " + e.Message); }
        }

        // The in-game day lives in <save>/Time.json (TimeData.ElapsedDays), not in SaveInfo. -1 when it cannot be read.
        private static int ElapsedDays(string savePath)
        {
            try
            {
                string file = Path.Combine(savePath ?? "", "Time.json");
                if (!File.Exists(file)) { Core.Log?.Msg($"[hub3] continue: no Time.json in '{savePath}', the card shows no day."); return -1; }
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("ElapsedDays", out var d) && d.TryGetInt32(out int days)) return days;
                Core.Log?.Warning($"[hub3] continue: '{file}' has no ElapsedDays.");
            }
            catch (Exception e) { Core.Log?.Warning("[hub3] continue: Time.json could not be read: " + e.Message); }
            return -1;
        }

        // Loads the save the way vanilla ContinueScreen.LoadGame does, without Side Hustle's "host it publicly?" prompt:
        // that prompt sits on ContinueScreen.LoadGame, and this goes to LoadManager.StartGame directly.
        private static string HubCallResume(string arg)
        {
            if (HubBusy()) { Core.Log?.Msg("[hub3] continue refused: busy."); return RefuseBusy; }
            if (!int.TryParse(arg, out int index)) { Core.Log?.Error($"[hub3] continue: unreadable slot '{arg}'."); return "Something went wrong."; }
            try
            {
                var lobby = PersistentSingleton<Lobby>.Instance;
                if (lobby != null && !lobby.IsHost)
                {
                    Core.Log?.Msg("[hub3] continue refused: in someone else's lobby.");
                    return "Only the host can start the game.";
                }
                var saves = LoadManager.SaveGames;
                var info = saves != null && index >= 0 && index < saves.Length ? saves[index] : null;
                if (info == null) { Core.Log?.Warning($"[hub3] continue: save {index + 1} is gone."); _resumeAge = 999f; return "That save is gone."; }
                Core.Log?.Msg($"[hub3] continue: loading save {index + 1} ('{info.OrganisationName}').");
                CloseHubScreen();   // the page would otherwise stay drawn over the loading screen
                Singleton<LoadManager>.Instance.StartGame(info);
                return "";
            }
            catch (Exception e)
            {
                Core.Log?.Error("[hub3] continue: loading the save failed: " + e);
                return "Something went wrong.";
            }
        }
    }
}
