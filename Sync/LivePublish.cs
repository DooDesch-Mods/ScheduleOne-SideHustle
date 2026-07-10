using System;
using HarmonyLib;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Networking;
using Il2CppScheduleOne.UI.Multiplayer;
using Il2CppSteamworks;
using SideHustle.Multiplayer;
using SideHustle.Profiles;
using UnityEngine;
using UnityEngine.UI;

namespace SideHustle.Sync
{
    /// <summary>
    /// Live-publish a co-op session already in progress: a "Publish (Sync)" button injected into the pause-menu
    /// lobby panel (cloned from the invite button, like <see cref="LobbyInviteAccess"/>). Shown only to the
    /// HOST of a real co-op lobby that Side Hustle did not itself start. Clicking it flips the lobby public,
    /// tags it with a manifest of the currently-loaded mods and opens it to non-friends; clicking again
    /// unpublishes. The manifest's source resolution needs the Thunderstore index, fetched in the background -
    /// until it arrives, sources are unresolved (clients still see the mod list, they just can't auto-install).
    /// </summary>
    internal static class LivePublish
    {
        private static HarmonyLib.Harmony _harmony;
        private static bool _installed;
        private static Button _button;
        private static Text _label;
        private static bool _published;
        private static TsIndex _index;
        private static bool _indexRequested;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                _harmony = new HarmonyLib.Harmony("doodesch.sidehustle.livepublish");
                var lateUpdate = AccessTools.Method(typeof(LobbyInterface), "LateUpdate");
                if (lateUpdate != null)
                    _harmony.Patch(lateUpdate, postfix: new HarmonyMethod(
                        typeof(LivePublish).GetMethod(nameof(LateUpdatePostfix), AccessTools.all)));
                else Core.Log?.Warning("[sync] LobbyInterface.LateUpdate not found - no live-publish button.");
            }
            catch (Exception e) { Core.Log?.Warning("[sync] live-publish install failed: " + e.Message); }
        }

        private static void LateUpdatePostfix(LobbyInterface __instance)
        {
            try
            {
                if (__instance == null || __instance.Lobby == null || __instance.InviteButton == null) return;

                bool eligible = __instance.Canvas != null && __instance.Canvas.enabled
                                && __instance.Lobby.IsInLobby && __instance.Lobby.IsHost
                                && !SyncCoordinator.IsInSession;   // a Side Hustle-hosted session already publishes itself

                if (!eligible)
                {
                    if (_button != null) _button.gameObject.SetActive(false);
                    return;
                }
                EnsureButton(__instance);
                if (_button != null)
                {
                    _button.gameObject.SetActive(true);
                    if (_label != null) _label.text = _published ? "Unpublish" : "Publish (Sync)";
                }
            }
            catch { }
        }

        private static void EnsureButton(LobbyInterface panel)
        {
            if (_button != null) return;
            try
            {
                var clone = UnityEngine.Object.Instantiate(panel.InviteButton.gameObject, panel.InviteButton.transform.parent, false);
                clone.name = "SideHustle_PublishButton";
                clone.transform.localScale = Vector3.one;
                var rt = clone.GetComponent<RectTransform>();
                var srt = panel.InviteButton.GetComponent<RectTransform>();
                if (rt != null && srt != null) rt.anchoredPosition = srt.anchoredPosition + new Vector2(0f, -46f);

                _button = clone.GetComponent<Button>();
                _label = clone.GetComponentInChildren<Text>();
                if (_button != null)
                {
                    _button.onClick.RemoveAllListeners();
                    int n = _button.onClick.GetPersistentEventCount();
                    for (int i = 0; i < n; i++) _button.onClick.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
                    _button.onClick.AddListener((UnityEngine.Events.UnityAction)Toggle);
                }
                Core.Log?.Msg("[sync] live-publish button injected.");
            }
            catch (Exception e) { Core.Log?.Warning("[sync] publish button build failed: " + e.Message); }
        }

        private static void Toggle()
        {
            try
            {
                var lobby = PersistentSingleton<Lobby>.Instance;
                if (lobby == null || !lobby.IsInLobby || !lobby.IsHost) return;

                if (_published)
                {
                    VanillaLobby.Untag();
                    PublicLobbyAccess.Disable();
                    _published = false;
                    Core.Log?.Msg("[sync] lobby unpublished.");
                    return;
                }

                if (!_indexRequested)
                {
                    _indexRequested = true;
                    System.Threading.Tasks.Task.Run(async () =>
                    {
                        try { _index = await Profiles.ThunderstoreClient.GetIndexAsync(Profiles.ProfileEngine.GameRoot, false, System.Threading.CancellationToken.None); }
                        catch { }
                    });
                }

                var plan = SyncPublisher.BuildPlan(_index);
                int cap = LobbyCaps.MaxClients();
                var opts = new HostOptions { MaxPlayers = Mathf.Max(2, cap), Visibility = LobbyVisibility.Public };
                string org = "";
                try { org = SteamFriends.GetPersonaName(); } catch { }
                bool ok = VanillaLobby.Tag(opts, plan.Manifest.ToCanonicalText(), "", false, org + "'s game",
                    $"{plan.AutoCount}/{plan.LinkCount}/{plan.DroppedCount}");
                if (!ok) { Core.Log?.Warning("[sync] could not publish the lobby."); return; }
                PublicLobbyAccess.Enable();
                _published = true;
                Core.Log?.Msg($"[sync] lobby published live ({plan.AutoCount}/{plan.LinkCount}/{plan.DroppedCount} mods).");
            }
            catch (Exception e) { Core.Log?.Warning("[sync] publish toggle failed: " + e.Message); }
        }

        /// <summary>A session end / menu return resets the button state (the lobby is gone).</summary>
        internal static void Reset()
        {
            _published = false;
        }
    }
}
