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
                // One line per click, naming every reason this can bow out. Without it a click that
                // loads nothing is indistinguishable from a click that never reached this method,
                // and those two have completely different causes.
                Core.Log?.Msg($"[sync] LoadGame({index}) prefix: bypass={_bypass} ask={Preferences.AskHostOnContinue} " +
                              $"alt={Mods.AltBase.IsAltSession()} lobby={Multiplayer.LobbyCoordinator.IsInLobby}");

                if (_bypass) return true;                                   // our own resumed "Just play"
                if (!Preferences.AskHostOnContinue) return true;           // player opted out
                if (Mods.AltBase.IsAltSession()) return true;              // inside a curated profile (named Continue is rewired in MenuInjector)
                if (Multiplayer.LobbyCoordinator.IsInLobby) return true;   // a co-op/friends flow already underway

                var save = Il2CppScheduleOne.Persistence.LoadManager.SaveGames[index];
                if (save == null) return true;

                // Swallow the load ONLY once the dialog is provably on screen. Returning false with
                // no visible dialog strands the player on a menu where clicking a save does nothing
                // at all - no error, no load - which is exactly what 0.4.6f11 produced.
                return !TryShowDialog(__instance, index, save);
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[sync] interstitial prefix failed (loading normally): " + e.Message);
                return true;
            }
        }

        /// <summary>
        /// Builds and shows the dialog. Returns false when it could not be put on screen, in which
        /// case the caller must let the vanilla load run - swallowing a load without showing
        /// anything leaves the player clicking a save slot that does nothing at all.
        ///
        /// The canvas comes from Hub.DialogRootStatic(), which owns an overlay canvas of its own.
        /// It used to borrow the game canvas with the highest sortingOrder, and under 0.4.6f11 that
        /// picked DisclaimerCanvas (50) over MainMenu (0) - the dialog was built, was active, and
        /// was never visible.
        /// </summary>
        private static bool TryShowDialog(ContinueScreen screen, int index,
            Il2CppScheduleOne.Persistence.SaveInfo save)
        {
            // Hide the vanilla save picker so it cannot bleed through the dialog scrim. The
            // reference is kept so Just-play still loads and dismiss can reopen it.
            try { screen.Close(); }
            catch (Exception e) { Core.Log?.Warning("[sync] could not hide the save picker behind the host prompt: " + e.Message); }

            var root = Hub.DialogRootStatic();
            if (root == null)
            {
                Core.Log?.Warning("[sync] no canvas for the host prompt - loading normally.");
                return false;
            }

            GameObject scrim = DooDesch.UI.Components.CountdownDialog(root,
                "Host this save publicly?",
                $"Open '{SafeOrg(save)}' as a public Side Hustle lobby so others can join with matching mods - or just play it solo.",
                "Host publicly", "Just play",
                onConfirm: () =>
                {
                    DestroyScrim();
                    Hub.HostVanillaSave(save);
                },
                onCancel: () => JustPlay(screen, index),
                out var countdown,
                onDismiss: () =>
                {
                    DestroyScrim();
                    // Back to the save picker; nothing hosted or loaded.
                    try { screen.Open(); }
                    catch (Exception e) { Core.Log?.Warning("[sync] could not reopen the save picker: " + e.Message); }
                });

            // A dialog that was built but is not actually in a live, active hierarchy is worse than
            // no dialog: the player sees the menu do nothing. Throw it away and load normally.
            if (scrim == null || !scrim.activeInHierarchy)
            {
                Core.Log?.Warning("[sync] host prompt did not reach the screen - loading normally.");
                if (scrim != null) UnityEngine.Object.Destroy(scrim);
                _scrim = null;
                return false;
            }

            _scrim = scrim;
            if (countdown != null) countdown.text = "";   // no timer: a deliberate choice, not a countdown
            AddDontAskButton(scrim, () =>
            {
                Preferences.AskHostOnContinue = false;
                Core.Log?.Msg("[sync] the host prompt was turned off from the prompt itself.");
                Hub.Notify("Won't ask again. Turn it back on in the Side Hustle settings.", DooDesch.UI.Severity.Info);
                JustPlay(screen, index);
            });
            return true;
        }

        /// <summary>Load the save the way the game would have, without asking again for this click.</summary>
        private static void JustPlay(ContinueScreen screen, int index)
        {
            DestroyScrim();
            try { _bypass = true; screen.LoadGame(index); }
            finally { _bypass = false; }
        }

        /// <summary>A third choice under the dialog's two: the shared dialog only has Cancel and Confirm, so the card
        /// grows by one button row and the new button sits above them.</summary>
        private static void AddDontAskButton(GameObject scrim, Action onClick)
        {
            var card = scrim.transform.Find("card");
            if (card == null) { Core.Log?.Warning("[sync] the host prompt has no card to add the opt-out button to."); return; }
            var crt = card.GetComponent<RectTransform>();
            crt.sizeDelta = new Vector2(crt.sizeDelta.x, crt.sizeDelta.y + 48f);

            var (go, btn, _) = S1API.UI.UIFactory.ButtonWithLabel("DontAsk", "Don't ask again", card, DooDesch.UI.Theme.Button, 420, 40);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0); rt.pivot = new Vector2(0.5f, 0);
            rt.anchoredPosition = new Vector2(0, 66); rt.sizeDelta = new Vector2(420, 40);
            btn.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
            {
                try { onClick(); }
                catch (Exception e) { Core.Log?.Error("[sync] the opt-out button failed: " + e); }
            }));
            DooDesch.UI.Interactions.PolishButtons(card);
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
            if (_scrim != null) { Safe.Try("closing the host prompt", () => UnityEngine.Object.Destroy(_scrim)); _scrim = null; }
        }

        private static string SafeOrg(Il2CppScheduleOne.Persistence.SaveInfo save)
        {
            try { return string.IsNullOrEmpty(save.OrganisationName) ? "this save" : save.OrganisationName; }
            catch (Exception e) { Safe.Report("reading the organisation name for the host prompt", e); return "this save"; }
        }
    }
}
