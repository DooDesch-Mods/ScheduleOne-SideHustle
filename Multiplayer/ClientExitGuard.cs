using System;
using System.Reflection;
using HarmonyLib;
using Il2CppFishNet;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Networking;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.PlayerScripts;
using UnityEngine.SceneManagement;

namespace SideHustle.Multiplayer
{
    /// <summary>
    /// Guarantees a co-op CLIENT can always leave a live game back to the main menu. Vanilla
    /// <c>LoadManager.ExitToMenu</c> has two ways to silently no-op before it ever starts its scene-load coroutine:
    /// an early <c>if (!IsGameLoaded) return;</c> guard, and any exception thrown in its synchronous pre-coroutine
    /// block (it runs <c>Lobby.LeaveLobby()</c> - which fires <c>onLobbyChange</c> subscribers - and touches several
    /// singletons there, none wrapped). When either happens, <c>ConfirmExitScreen.ConfirmExit</c> still runs its
    /// second line, <c>Close(openPrevious:true)</c>, which re-opens the pause menu - so the player clicks Quit, the
    /// confirm closes, and they are dumped back into the in-game pause menu, still loaded and unable to leave.
    ///
    /// This is not caused by any Side Hustle Harmony patch (none touch the exit path), which is why it is easy to
    /// miss. The guard is client-only and host-safe: a host's exit and plain menu navigation are left untouched.
    /// It also records the live state at the click so a per-profile log pins the exact cause on the next report.
    /// </summary>
    internal static class ClientExitGuard
    {
        private static HarmonyLib.Harmony _harmony;
        private static bool _installed;
        private static MethodInfo _setGameLoaded;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                _harmony = new HarmonyLib.Harmony("doodesch.sidehustle.clientexit");
                var target = AccessTools.Method(typeof(LoadManager), nameof(LoadManager.ExitToMenu));
                if (target == null) { Core.Log?.Warning("[exitguard] LoadManager.ExitToMenu not found - client-exit guard inactive."); return; }

                _harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(ClientExitGuard).GetMethod(nameof(ExitPrefix), AccessTools.all)),
                    finalizer: new HarmonyMethod(typeof(ClientExitGuard).GetMethod(nameof(ExitFinalizer), AccessTools.all)));
                Core.Log?.Msg("[exitguard] client-exit guard installed.");
            }
            catch (Exception e) { Core.Log?.Warning("[exitguard] install failed: " + e.Message); }
        }

        // True only for a machine that is a pure FishNet client (not the host) sitting in a gameplay scene - the
        // one situation the vanilla no-op can strand. Everything else (host, menu) passes through untouched.
        private static bool IsClientInGame()
        {
            try
            {
                if (!InstanceFinder.IsClient || InstanceFinder.IsServer) return false;
                string scene = SceneManager.GetActiveScene().name;
                return scene == "Main" || scene == "Tutorial";
            }
            catch { return false; }
        }

        private static void ExitPrefix()
        {
            try
            {
                if (!IsClientInGame()) return;
                var lm = Singleton<LoadManager>.Instance;
                if (lm == null) return;

                bool loaded = false, loading = false;
                try { loaded = lm.IsGameLoaded; } catch { }
                try { loading = lm.IsLoading; } catch { }
                Core.Log?.Msg($"[exitguard] client exit-to-menu: IsGameLoaded={loaded}, IsLoading={loading}, scene={SceneManager.GetActiveScene().name}.");

                // Cause A: the vanilla guard would early-return because IsGameLoaded is false even though we are
                // plainly in a loaded game. Restore it so vanilla runs its own full, correct exit (scene load +
                // client save handshake + FishNet teardown). ExitToMenu sets it back to false immediately after.
                if (!loaded && TrySetGameLoaded(lm, true))
                    Core.Log?.Msg("[exitguard] restored IsGameLoaded so the vanilla exit can proceed (guard cause).");
            }
            catch (Exception e) { Core.Log?.Warning("[exitguard] prefix failed: " + e.Message); }
        }

        // A HarmonyX finalizer decides an exception's fate by its RETURN value: return null to swallow it, return it
        // unchanged to let it propagate. A void finalizer can only observe - it would leave the original throw to be
        // rethrown out of ExitToMenu, skipping ConfirmExit's Close() and surfacing an unhandled exception in the log.
        private static Exception ExitFinalizer(Exception __exception)
        {
            if (__exception == null) return null;   // vanilla exit ran fine
            try
            {
                if (!IsClientInGame()) return __exception;   // not our case - let it propagate untouched
                Core.Log?.Warning("[exitguard] vanilla ExitToMenu threw before it could leave (" + __exception.Message + ") - forcing a clean client exit.");
                ForceClientExit();
                return null;   // handled here: swallow it so ConfirmExit's Close() runs and Unity logs no stray throw
            }
            catch (Exception e) { Core.Log?.Warning("[exitguard] finalizer recovery failed: " + e.Message); return __exception; }
        }

        private static bool _recovering;

        // A last-resort teardown that mirrors the vanilla client exit without depending on the throw site. It also
        // recovers a client kicked MID-load: it stops the hung LoadAsClient coroutine and clears the loading state
        // FIRST (otherwise the client sits forever on a loading screen), then leaves the lobby (clears LobbyID so no
        // vanilla auto-rejoin), drops the FishNet connection, and loads the menu.
        private static void ForceClientExit()
        {
            if (_recovering) return;   // never re-enter within one recovery
            _recovering = true;
            try
            {
                var lm = Singleton<LoadManager>.Instance;
                if (lm != null)
                {
                    try { lm.StopAllCoroutines(); } catch { }   // stop a hung LoadAsClient WaitUntil that never returns
                    TrySetLoading(lm, false);
                }
                try { if (Player.Local != null) Player.Local.RequestSavePlayer(); } catch { }
                try { var lob = Singleton<Lobby>.Instance; if (lob != null && lob.IsInLobby) lob.LeaveLobby(); } catch { }
                try { InstanceFinder.ClientManager?.StopConnection(); } catch { }
                if (lm != null) TrySetGameLoaded(lm, false);
                try { UnityEngine.Time.timeScale = 1f; } catch { }
                try { SceneManager.LoadScene("Menu"); } catch (Exception e) { Core.Log?.Warning("[exitguard] menu load failed: " + e.Message); }
            }
            finally { _recovering = false; }
        }

        private static float _stuckTimer;

        /// <summary>Pumped from Core.OnUpdate. Recovers a client that was kicked/dropped and left on a gameplay scene
        /// (or a hung loading screen) instead of the menu: vanilla's kick recovery only fires from a fully loaded
        /// game, so a mid-load kick strands the client. The dwell time distinguishes this from the brief
        /// disconnected-but-in-gameplay sliver of a NORMAL exit (which reaches the menu within ~1s).</summary>
        internal static void TickWatchdog()
        {
            try
            {
                bool isClient = false, isServer = false;
                try { isClient = InstanceFinder.IsClient; isServer = InstanceFinder.IsServer; } catch { }
                var lm = Singleton<LoadManager>.Instance;
                bool loaded = false;
                if (lm != null) { try { loaded = lm.IsGameLoaded; } catch { } }
                string scene = SceneManager.GetActiveScene().name;
                bool inGameplay = scene == "Main" || scene == "Tutorial";

                // Stuck = on a gameplay scene, not a live loaded game, and connected as neither client nor host.
                bool stuck = inGameplay && !isClient && !isServer && !loaded;
                if (!stuck) { _stuckTimer = 0f; return; }
                _stuckTimer += UnityEngine.Time.unscaledDeltaTime;
                if (_stuckTimer > 4f)
                {
                    _stuckTimer = 0f;
                    Core.Log?.Warning("[exitguard] client stranded disconnected on a gameplay scene (likely kicked) - forcing exit to menu.");
                    ForceClientExit();
                }
            }
            catch { }
        }

        // IsGameLoaded has a protected setter; reach it once through the Il2CppInterop-generated set_ method.
        private static bool TrySetGameLoaded(LoadManager lm, bool value)
        {
            try
            {
                _setGameLoaded ??= typeof(LoadManager).GetMethod("set_IsGameLoaded",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_setGameLoaded == null) return false;
                _setGameLoaded.Invoke(lm, new object[] { value });
                return true;
            }
            catch (Exception e) { Core.Log?.Warning("[exitguard] set IsGameLoaded failed: " + e.Message); return false; }
        }

        // IsLoading also has a non-public setter; reach it the same way to clear a hung load state.
        private static MethodInfo _setLoading;
        private static bool TrySetLoading(LoadManager lm, bool value)
        {
            try
            {
                _setLoading ??= typeof(LoadManager).GetMethod("set_IsLoading",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_setLoading == null) return false;
                _setLoading.Invoke(lm, new object[] { value });
                return true;
            }
            catch { return false; }
        }
    }
}
