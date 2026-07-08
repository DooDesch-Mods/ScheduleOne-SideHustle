using System;
using HarmonyLib;
using Il2CppScheduleOne.Networking;
using Il2CppScheduleOne.UI.Multiplayer;
using Il2CppSteamworks;

namespace SideHustle.Multiplayer
{
    /// <summary>
    /// Lets a NON-host lobby member open the Steam friend-invite dialog from the pause-menu lobby panel.
    ///
    /// Vanilla shows the [+] invite button to the host only: <c>LobbyInterface.UpdateButtons</c> does
    /// <c>InviteButton.SetActive(Lobby.IsHost &amp;&amp; Lobby.PlayerCount &lt; 4)</c>, and <c>Lobby.TryOpenInviteInterface</c>
    /// refuses once the lobby has &gt;= 4 members. The lobby panel itself is already shown to clients (its Canvas is
    /// enabled for anyone paused while in a lobby), and Steam lets ANY lobby member invite friends - so we just
    /// re-show the button for clients and bypass the hardcoded 4-seat cap (which BiggerLobbies raises anyway).
    ///
    /// Active only while a Side Hustle session is live (both host and client set it), so vanilla co-op outside Side
    /// Hustle is untouched.
    /// </summary>
    internal static class LobbyInviteAccess
    {
        private static HarmonyLib.Harmony _harmony;
        private static bool _installed;

        /// <summary>True while a Side Hustle multiplayer session is live - the invite button is opened up only then.</summary>
        internal static bool Active;

        internal static void Enable()
        {
            EnsureInstalled();
            Active = true;
        }

        internal static void Disable() => Active = false;

        private static void EnsureInstalled()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                _harmony = new HarmonyLib.Harmony("doodesch.sidehustle.lobbyinvite");

                var updateButtons = AccessTools.Method(typeof(LobbyInterface), "UpdateButtons");
                if (updateButtons != null)
                    _harmony.Patch(updateButtons, postfix: Hook(nameof(UpdateButtonsPostfix)));
                else Core.Log?.Warning("[mp] LobbyInterface.UpdateButtons not found - clients may not see the invite button.");

                var lateUpdate = AccessTools.Method(typeof(LobbyInterface), "LateUpdate");
                if (lateUpdate != null)
                    _harmony.Patch(lateUpdate, postfix: Hook(nameof(LateUpdatePostfix)));

                var tryInvite = AccessTools.Method(typeof(Lobby), "TryOpenInviteInterface");
                if (tryInvite != null)
                    _harmony.Patch(tryInvite, prefix: Hook(nameof(TryOpenInvitePrefix)));

                Core.Log?.Msg("[mp] lobby invite access installed (clients can invite friends).");
            }
            catch (Exception e) { Core.Log?.Warning("[mp] lobby-invite patch install failed: " + e.Message); }
        }

        private static HarmonyMethod Hook(string name) =>
            new HarmonyMethod(typeof(LobbyInviteAccess).GetMethod(name, AccessTools.all));

        // Vanilla hides the invite button for non-hosts (and past 4 seats). While a Side Hustle session is live, show
        // it to any lobby member so clients can invite Steam friends too.
        private static void UpdateButtonsPostfix(LobbyInterface __instance)
        {
            try
            {
                if (!Active || __instance == null || __instance.InviteButton == null || __instance.Lobby == null) return;
                __instance.InviteButton.gameObject.SetActive(__instance.Lobby.IsInLobby);
            }
            catch { }
        }

        // UpdateButtons only runs on Start / onLobbyChange, so a client that just opened the paused lobby panel may
        // have a stale (hidden) button. Re-assert it whenever the panel is visible.
        private static void LateUpdatePostfix(LobbyInterface __instance)
        {
            try
            {
                if (!Active || __instance == null || __instance.InviteButton == null || __instance.Lobby == null) return;
                if (__instance.Canvas != null && __instance.Canvas.enabled && __instance.Lobby.IsInLobby
                    && !__instance.InviteButton.gameObject.activeSelf)
                    __instance.InviteButton.gameObject.SetActive(true);
            }
            catch { }
        }

        // Bypass the hardcoded ">= 4 members" refusal so invites work in BiggerLobbies-sized lobbies. Open the Steam
        // invite overlay for the current lobby directly and skip the vanilla body.
        private static bool TryOpenInvitePrefix(Lobby __instance)
        {
            if (!Active || __instance == null) return true;   // outside a Side Hustle session: vanilla behaviour
            try
            {
                if (__instance.IsInLobby)
                {
                    SteamFriends.ActivateGameOverlayInviteDialog(__instance.LobbySteamID);
                    return false;   // handled - skip the capacity guard
                }
            }
            catch (Exception e) { Core.Log?.Warning("[mp] open invite failed: " + e.Message); }
            return true;   // not in a lobby - let vanilla handle it
        }
    }
}
