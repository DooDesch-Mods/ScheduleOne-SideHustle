using System;
using HarmonyLib;
using Il2CppScheduleOne.UI.MainMenu;
using SideHustle.Config;
using UnityEngine;

namespace SideHustle.Menu
{
    /// <summary>
    /// A native-looking "host this publicly?" prompt when the player clicks Continue / a Load Game slot. A
    /// Harmony prefix on <c>ContinueScreen.LoadGame(int)</c> swallows the vanilla load once and shows a two-way
    /// dialog: "Host publicly" runs the Sync host flow on that save; "Just play" re-invokes the vanilla load
    /// (bypass flag). Guarded so it never fires inside a curated profile, when a lobby flow is already underway,
    /// or when the player disabled it. Installed once; inert until then. Every failure path falls back to the
    /// plain vanilla load, so it can never strand the player.
    /// </summary>
    internal static class ContinueInterstitial
    {
        private static HarmonyLib.Harmony _harmony;
        private static bool _installed;
        private static bool _bypass;   // set while we re-invoke the vanilla LoadGame ourselves

        internal static void EnsureInstalled()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                _harmony = new HarmonyLib.Harmony("doodesch.sidehustle.continuehost");
                var target = AccessTools.Method(typeof(ContinueScreen), nameof(ContinueScreen.LoadGame));
                if (target != null)
                {
                    _harmony.Patch(target, prefix: new HarmonyMethod(
                        typeof(ContinueInterstitial).GetMethod(nameof(LoadGamePrefix), AccessTools.all)));
                    Core.Log?.Msg("[sync] continue-host interstitial installed.");
                }
                else Core.Log?.Warning("[sync] ContinueScreen.LoadGame not found - no host-on-continue prompt.");
            }
            catch (Exception e) { Core.Log?.Warning("[sync] continue interstitial install failed: " + e.Message); }
        }

        // Returning false swallows the vanilla load; true lets it proceed.
        private static bool LoadGamePrefix(ContinueScreen __instance, int index)
        {
            try
            {
                if (_bypass) return true;                                   // our own resumed "Just play"
                if (!Preferences.AskHostOnContinue) return true;           // player opted out
                if (Mods.AltBase.IsAltSession()) return true;              // inside a curated profile
                if (Multiplayer.LobbyCoordinator.IsInLobby) return true;   // a co-op/friends flow already underway

                var save = Il2CppScheduleOne.Persistence.LoadManager.SaveGames[index];
                if (save == null) return true;

                var root = Hub.DialogRootStatic();
                if (root == null) return true;   // no canvas to host the dialog: load normally

                ShowDialog(root, __instance, index, save);
                return false;   // swallow this load; the dialog decides what happens next
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[sync] interstitial prefix failed (loading normally): " + e.Message);
                return true;
            }
        }

        private static void ShowDialog(Transform root, ContinueScreen screen, int index,
            Il2CppScheduleOne.Persistence.SaveInfo save)
        {
            GameObject scrim = DooDesch.UI.Components.CountdownDialog(root,
                "Host this save publicly?",
                $"Open '{SafeOrg(save)}' as a public Side Hustle lobby so others can join with matching mods - or just play it solo.",
                "Host publicly", "Just play",
                onConfirm: () =>
                {
                    DestroyScrim();
                    Hub.HostVanillaSave(save);
                },
                onCancel: () =>
                {
                    DestroyScrim();
                    try { _bypass = true; screen.LoadGame(index); }
                    finally { _bypass = false; }
                },
                out var countdown);

            _scrim = scrim;
            if (countdown != null) countdown.text = "";   // no timer: a deliberate choice, not a countdown
        }

#if DEBUG
        /// <summary>Dev.SelfTest only: show the host-on-continue dialog with sample copy for a screenshot
        /// (both actions just close it).</summary>
        internal static void ShowForTest()
        {
            var root = Hub.DialogRootStatic();
            if (root == null) return;
            _scrim = DooDesch.UI.Components.CountdownDialog(root,
                "Host this save publicly?",
                "Open 'Kings of Cul-de-Sac' as a public Side Hustle lobby so others can join with matching mods - or just play it solo.",
                "Host publicly", "Just play",
                onConfirm: DestroyScrim, onCancel: DestroyScrim, out var countdown);
            if (countdown != null) countdown.text = "";
        }
#endif

        private static GameObject _scrim;
        private static void DestroyScrim()
        {
            if (_scrim != null) { try { UnityEngine.Object.Destroy(_scrim); } catch { } _scrim = null; }
        }

        private static string SafeOrg(Il2CppScheduleOne.Persistence.SaveInfo save)
        {
            try { return string.IsNullOrEmpty(save.OrganisationName) ? "this save" : save.OrganisationName; }
            catch { return "this save"; }
        }
    }
}
