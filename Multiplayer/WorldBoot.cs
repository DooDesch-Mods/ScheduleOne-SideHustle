using System;
using System.IO;
using Il2CppFishNet;                         // InstanceFinder (dropping a dangling client connection)
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

        /// <summary>
        /// Whether the game has begun going somewhere at all - any load status, any scene but the menu, or an
        /// already-loaded world.
        ///
        /// The distinction this draws is the one a join watchdog needs. Vanilla only starts a client's load when the
        /// host's lobby says so on entry ("ready" / "host_loading" / "load_tutorial"); with none of them set the
        /// client simply stays in the menu and nothing ever happens - no error, no screen, no progress. That is not a
        /// slow load, it is a load that will never begin, and it must not be given the same patience: a real world
        /// load can take minutes on a slow disk, while this state is already final after seconds.
        /// </summary>
        internal static bool LoadStarted
        {
            get
            {
                var lm = LoadOrNull();
                try
                {
                    if (SceneManager.GetActiveScene().name != "Menu") return true;
                    if (lm == null) return false;
                    return lm.IsGameLoaded || lm.IsLoading || lm.LoadStatus != LoadManager.ELoadStatus.None;
                }
                catch { return true; }   // unreadable: assume it is moving, so a watchdog never cuts a live load short
            }
        }

        /// <summary>True while the game is inside Unity's LoadSceneAsync. This phase is opaque - it publishes no
        /// progress at all until the scene is swapped in - so a watchdog has to treat it differently from the
        /// phases that do report movement.</summary>
        internal static bool IsLoadingScene
        {
            get
            {
                var lm = LoadOrNull();
                try { return lm != null && lm.LoadStatus == LoadManager.ELoadStatus.LoadingScene; }
                catch { return false; }
            }
        }

        /// <summary>A cheap fingerprint of "where the load currently is". Any change means the load is alive.
        /// The status TEXT is part of it on purpose: during syncing it names the task being replicated, so a slow
        /// but healthy sync keeps changing this string instead of looking frozen.</summary>
        internal static string ProgressSignature()
        {
            var lm = LoadOrNull();
            try
            {
                string scene = SceneManager.GetActiveScene().name;
                if (lm == null) return scene + "|?";
                return scene + "|" + lm.LoadStatus + "|" + lm.GetLoadStatusText();
            }
            catch { return "?"; }
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

        /// <summary>
        /// Recover from a load that never finished (a join or world boot we gave up on).
        /// <para>
        /// The vanilla <c>ExitToMenu</c> refuses to do anything while <c>IsGameLoaded</c> is false
        /// ("Game not loaded, cannot exit to menu") - and that is exactly the state a stalled join leaves
        /// behind: the scene is still Menu, but <c>IsLoading</c> is true and the LoadingScreen is open on
        /// top of it. Without this the player is stuck staring at "Loading world..." forever and has to
        /// kill the process, which is what made a failed join look like a hard freeze.
        /// </para>
        /// So tear the half-started load down by hand: stop the load coroutine that is still sitting in
        /// <c>while (!asyncLoad.isDone)</c>, clear the manager's flags, and close the loading screen. The
        /// menu scene underneath is untouched and interactive, so the player lands back in the hub.
        /// </summary>
        /// <returns>true when a stalled load was actually torn down.</returns>
        internal static bool AbortLoadToMenu()
        {
            var lm = LoadOrNull();
            if (lm == null) return false;
            try
            {
                // The world did come up after all - the normal path handles it (and leaves the lobby).
                if (lm.IsGameLoaded) { lm.ExitToMenu(); return true; }
                if (!lm.IsLoading && lm.LoadStatus == LoadManager.ELoadStatus.None) return false;   // nothing to undo

                // The load coroutine is parked on an await that may never complete; leaving it running
                // would drop us into the world minutes later, on top of the menu.
                try { lm.StopAllCoroutines(); }
                catch (Exception e) { Core.Log?.Warning("[mp] could not stop the load coroutine: " + e.Message); }

                // Drop the half-built FishNet connection. LoadAsClient calls ClientManager.StartConnection
                // and then waits for the host to hand over the scene; when that never happens the connection
                // is left dangling, and this client can NEVER join again - not even a session with free
                // seats. Measured: after one failed join a client stays broken for every further attempt,
                // while a freshly started client joins the same session fine. Without this, the abort only
                // fixes the visible symptom and leaves the player permanently unable to get back in.
                try { InstanceFinder.ClientManager?.StopConnection(); }
                catch (Exception e) { Core.Log?.Warning("[mp] could not stop the client connection: " + e.Message); }

                lm.IsLoading = false;
                lm.LoadStatus = LoadManager.ELoadStatus.None;

                CloseLoadingScreenHard();

                Core.Log?.Msg("[mp] stalled load torn down; back at the menu.");
                return true;
            }
            catch (Exception e) { Core.Log?.Warning("[mp] AbortLoadToMenu failed: " + e.Message); return false; }
        }

        /// <summary>
        /// Take the loading screen down for good, without relying on its own Close().
        /// <para>
        /// Close() does three things before it hides anything: it clears IsOpen, stops the loading music, and
        /// calls <c>SceneState.Current.Remove(State)</c> - and only THEN starts the fade that actually disables
        /// the canvas. That Remove undoes a Push that happens in the scene-change callback, so after a load that
        /// never changed scene there is nothing to remove and the call throws. The fade never starts, and the
        /// canvas stays on screen over the menu with a frozen "Loading world..." on it - visible to the player
        /// even though every internal flag already says the load is over.
        /// </para>
        /// So: let Close() run for its side effects (music, IsOpen) but treat it as best-effort, then switch the
        /// canvas off directly. Canvas and Group are public fields on the component.
        /// </summary>
        private static void CloseLoadingScreenHard()
        {
            Il2CppScheduleOne.UI.LoadingScreen ls = null;
            try { ls = Singleton<Il2CppScheduleOne.UI.LoadingScreen>.Instance; } catch { /* not spawned yet */ }
            if (ls == null) return;

            try { ls.Close(); }
            catch (Exception e) { Core.Log?.Warning("[mp] LoadingScreen.Close threw (expected after a scene-less load): " + e.Message); }

            try
            {
                if (ls.Group != null) ls.Group.alpha = 0f;
                if (ls.Canvas != null) ls.Canvas.enabled = false;
            }
            catch (Exception e) { Core.Log?.Warning("[mp] could not force the loading screen off: " + e.Message); }
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
