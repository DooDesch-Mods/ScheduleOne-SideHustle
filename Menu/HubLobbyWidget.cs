using System;
using HarmonyLib;
using Il2CppScheduleOne.UI.Multiplayer;

namespace SideHustle.Menu
{
    /// <summary>
    /// Hides the vanilla "Lobby (n/m)" box while the hub page is shown.
    ///
    /// <c>LobbyInterface.LateUpdate</c> sets <c>Container</c> active every frame on the main menu, so the box would
    /// sit on top of the page's top bar. A postfix on the same method turns it off again while the page is up; once
    /// the page hides, vanilla's own LateUpdate shows the box on the next frame.
    /// </summary>
    internal static class HubLobbyWidget
    {
        private static HarmonyLib.Harmony _harmony;
        private static bool _installed;
        private static bool _hiddenLogged;
        private static bool _failed;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                var lateUpdate = Internal.GameTargets.LobbyInterfaceLateUpdate.Value;
                if (lateUpdate == null)
                {
                    Core.Log?.Warning("[hub3] LobbyInterface.LateUpdate not found; the vanilla lobby box stays over the page.");
                    return;
                }
                _harmony = new HarmonyLib.Harmony("doodesch.sidehustle.hublobbywidget");
                _harmony.Patch(lateUpdate, postfix: new HarmonyMethod(
                    typeof(HubLobbyWidget).GetMethod(nameof(LateUpdatePostfix), AccessTools.all)));
                Core.Log?.Msg("[hub3] vanilla lobby box is hidden while the page is shown.");
            }
            catch (Exception e) { Core.Log?.Error("[hub3] lobby box patch install failed; the box stays over the page: " + e.Message); }
        }

        private static void LateUpdatePostfix(LobbyInterface __instance)
        {
            if (_failed) return;
            try
            {
                if (!Hub.HubShown || __instance == null || __instance.Container == null) return;
                var go = __instance.Container.gameObject;
                if (!go.activeSelf) return;
                go.SetActive(false);
                if (!_hiddenLogged) { _hiddenLogged = true; Core.Log?.Msg("[hub3] vanilla lobby box hidden under the page."); }
            }
            catch (Exception e)
            {
                // Runs per frame; one failure is logged and the patch stops trying.
                _failed = true;
                Core.Log?.Error("[hub3] hiding the vanilla lobby box failed; it stays over the page: " + e.Message);
            }
        }
    }
}
