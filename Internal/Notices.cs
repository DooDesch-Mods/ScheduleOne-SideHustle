using System;
using SideHustle.Multiplayer;

namespace SideHustle.Internal
{
    /// <summary>
    /// <see cref="API.ShowNotice"/>: one short line for the local player, drawn by whatever already shows notices
    /// where the player is. In the world that is the game's notification popup; in the menu it is the hub's toast
    /// stack, which sits on the hub's own overlay canvas and so also shows while no hub screen is open.
    /// </summary>
    internal static class Notices
    {
        internal static void Show(string text, float seconds)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Core.Log?.Warning("[api] ShowNotice was called with empty text; nothing shown.");
                return;
            }
            if (!ApiPump.OnMainThread)
            {
                ApiPump.Post(() => Show(text, seconds));
                return;
            }
            if (float.IsNaN(seconds) || seconds <= 0f) seconds = 4f;
            text = text.Trim();

            try
            {
                if (WorldBoot.IsInGame) ShowInWorld(text, seconds);
                else ShowInMenu(text);
            }
            catch (Exception e) { Core.Log?.Error($"[api] notice \"{text}\" could not be shown: {e}"); }
        }

        private static void ShowInWorld(string text, float seconds)
        {
            var mgr = Il2CppScheduleOne.DevUtilities.Singleton<Il2CppScheduleOne.UI.NotificationsManager>.Instance;
            if (mgr == null)
            {
                Core.Log?.Warning($"[api] notice \"{text}\" dropped: the world has no notification manager yet.");
                return;
            }
            string title = SessionEvents.Current?.Descriptor?.DisplayName;
            if (string.IsNullOrWhiteSpace(title)) title = "Side Hustle";
            mgr.SendNotification(title, text, null, seconds, true);
            Core.Log?.Msg($"[api] notice shown in the world: \"{text}\" ({seconds:0.#}s).");
        }

        private static void ShowInMenu(string text)
        {
            var root = Menu.Hub.DialogRootStatic();
            if (root == null)
            {
                Core.Log?.Warning($"[api] notice \"{text}\" dropped: the menu overlay canvas is not available.");
                return;
            }
            DooDesch.UI.Toast.Init(root);
            DooDesch.UI.Toast.Show(text, DooDesch.UI.Severity.Info);
            Core.Log?.Msg($"[api] notice shown in the menu: \"{text}\".");
        }
    }
}
