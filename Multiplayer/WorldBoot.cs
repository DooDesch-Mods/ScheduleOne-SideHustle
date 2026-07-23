using System;
using System.IO;
using Il2CppScheduleOne.DevUtilities;        // Singleton<>, GameSettings
using Il2CppScheduleOne.Persistence;         // LoadManager, SaveManager, SaveInfo
using Il2CppScheduleOne.Persistence.Datas;   // GameData, MetaData, DateTimeData
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SideHustle.Multiplayer
{
    /// <summary>
    /// Boots a throwaway game world for <see cref="GamemodeSurface.World"/> gamemodes, in a scratch folder OUTSIDE
    /// the five real save slots (so a real save is never created or touched). Replicates the vanilla
    /// SetupScreen.StartGame recipe; the scratch folder name is not "SaveGame_N" so RefreshSaveInfo never lists it.
    /// </summary>
    internal static class WorldBoot
    {
        internal const string ScratchName = "SideHustleScratch";

        private static LoadManager LoadOrNull()
        {
            try { return Singleton<LoadManager>.Instance; } catch { return null; }
        }

        internal static string ScratchPath()
        {
            try { return Path.Combine(Singleton<SaveManager>.Instance.IndividualSavesContainerPath, ScratchName); }
            catch { return null; }
        }

        internal static bool IsInGame
        {
            get { var lm = LoadOrNull(); try { return lm != null && lm.IsGameLoaded; } catch { return false; } }
        }

        /// <summary>The world is fully loaded and interactive (host or client end-state).</summary>
        internal static bool IsWorldReady()
        {
            var lm = LoadOrNull();
            try
            {
                return lm != null && lm.IsGameLoaded && !lm.IsLoading
                       && lm.LoadStatus == LoadManager.ELoadStatus.None
                       && SceneManager.GetActiveScene().name == "Main";
            }
            catch { return false; }
        }

        internal static string CurrentScene
        {
            get { try { return SceneManager.GetActiveScene().name; } catch { return "?"; } }
        }

        internal static string LoadStatus
        {
            get { var lm = LoadOrNull(); try { return lm != null ? lm.LoadStatus.ToString() : "?"; } catch { return "?"; } }
        }

        /// <summary>Build a fresh scratch save and start it. For a host, the lobby must already exist + be owned by
        /// us BEFORE this call (StartGame binds the joinable FishySteamworks transport only then).</summary>
        internal static bool BootHostWorld(string orgName)
        {
            try
            {
                string folder = ScratchPath();
                if (folder == null) { Core.Log?.Warning("[mp] scratch path unavailable."); return false; }
                BuildScratchSave(folder, orgName);

                var nowDt = Il2CppSystem.DateTime.Now;
                var md = new MetaData(new DateTimeData(nowDt), new DateTimeData(nowDt),
                                      Application.version, Application.version, false);
                var info = new SaveInfo(folder, -1, orgName, nowDt, nowDt, 0f, Application.version, md);
                Core.Log?.Msg($"[mp] booting world at {folder} (slot -1, tutorial off)...");
                Singleton<LoadManager>.Instance.StartGame(info, false, false);
                return true;
            }
            catch (Exception e) { Core.Log?.Error("[mp] BootHostWorld failed: " + e); return false; }
        }

        /// <summary>Materialize a fresh REAL save in slot N (0..4) from the DefaultSave template - the same recipe
        /// the vanilla New Game screen uses - and refresh the save registry. Returns the new SaveInfo (for the
        /// normal vanilla host flow to publish), or null on failure. Unlike the scratch world this creates a real
        /// "SaveGame_N" folder, so RefreshSaveInfo lists it and the player keeps the save.</summary>
        internal static Il2CppScheduleOne.Persistence.SaveInfo CreateNewSave(int slot, string orgName)
        {
            try
            {
                var sm = Singleton<SaveManager>.Instance;
                if (sm == null) { Core.Log?.Warning("[mp] SaveManager unavailable for new game."); return null; }
                string folder = Path.Combine(sm.IndividualSavesContainerPath, "SaveGame_" + (slot + 1));
                BuildScratchSave(folder, orgName);   // the SetupScreen.StartGame recipe (copy + Game/Metadata json)
                Singleton<LoadManager>.Instance.RefreshSaveInfo();
                var saves = LoadManager.SaveGames;
                return saves != null && slot >= 0 && slot < saves.Length ? saves[slot] : null;
            }
            catch (Exception e) { Core.Log?.Error("[mp] CreateNewSave failed: " + e); return null; }
        }

        /// <summary>Leave the world back to the menu. The game's ExitToMenu also leaves the Steam lobby.</summary>
        internal static void ExitToMenu()
        {
            try
            {
                var lm = LoadOrNull();
                if (lm != null && lm.IsGameLoaded) lm.ExitToMenu();
            }
            catch (Exception e) { Core.Log?.Warning("[mp] ExitToMenu failed: " + e.Message); }
        }

        internal static void CleanupScratch()
        {
            try
            {
                string folder = ScratchPath();
                if (folder != null && Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (Exception e) { Core.Log?.Warning("[mp] scratch cleanup failed: " + e.Message); }
        }

        // --- the SetupScreen.StartGame recipe ---

        private static void BuildScratchSave(string folder, string orgName)
        {
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            ClearFolderContents(folder);
            CopyFilesRecursively(Path.Combine(Application.streamingAssetsPath, "DefaultSave"), folder);

            string gameJson = new GameData(orgName, UnityEngine.Random.Range(0, int.MaxValue), new GameSettings()).GetJson();
            File.WriteAllText(Path.Combine(folder, "Game.json"), gameJson);
            var nowDt = Il2CppSystem.DateTime.Now;
            string metaJson = new MetaData(new DateTimeData(nowDt), new DateTimeData(nowDt),
                                           Application.version, Application.version, false).GetJson();
            File.WriteAllText(Path.Combine(folder, "Metadata.json"), metaJson);
        }

        private static void ClearFolderContents(string folderPath)
        {
            var dir = new DirectoryInfo(folderPath);
            foreach (var f in dir.GetFiles()) f.Delete();
            foreach (var d in dir.GetDirectories()) d.Delete(true);
        }

        private static void CopyFilesRecursively(string sourcePath, string targetPath)
        {
            if (!Directory.Exists(sourcePath)) { Core.Log?.Warning("[mp] DefaultSave missing at " + sourcePath); return; }
            foreach (string d in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(d.Replace(sourcePath, targetPath));
            foreach (string f in Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories))
                if (!f.EndsWith(".meta")) File.Copy(f, f.Replace(sourcePath, targetPath), true);
        }
    }
}
