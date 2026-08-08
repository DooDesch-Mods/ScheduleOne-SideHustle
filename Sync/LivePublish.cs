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

                // The panel no longer owns a Canvas - LateUpdate drives Container.gameObject instead. The rest of the
                // test lives in CanPublish, shared with the phone app's row: two surfaces offering the same switch
                // must not disagree about whether it applies.
                bool eligible = __instance.Container != null && __instance.Container.gameObject.activeInHierarchy
                                && CanPublish;

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

        /// <summary>Whether this session's lobby is currently advertised by the live-publish path. The pause-menu
        /// button and the phone app are two views of the same switch, so both read it from here.</summary>
        internal static bool IsPublished => _published;

        /// <summary>
        /// Whether live publishing applies at all right now: we host a real co-op lobby that Side Hustle did not
        /// start itself (one it started publishes itself). Same test the button's visibility uses.
        /// </summary>
        /// <remarks>
        /// Both coordinators have to be idle, not just the sync one. A GAMEMODE host is also "in a lobby, hosting",
        /// and that lobby already carries its own name, seats, visibility and join manifest - publishing over it
        /// rewrites every one of them with vanilla co-op values, and unpublishing then calls Untag, which sets the
        /// lobby unjoinable. A running PropHunt round would lose anyone still trying to get in, and nothing on
        /// screen would connect that to the button that did it.
        /// </remarks>
        internal static bool CanPublish
        {
            get
            {
                try
                {
                    if (SyncCoordinator.IsBusy || Multiplayer.MultiplayerCoordinator.IsBusy) return false;
                    var lobby = PersistentSingleton<Lobby>.Instance;
                    return lobby != null && lobby.IsInLobby && lobby.IsHost;
                }
                catch { return false; }
            }
        }

        /// <summary>Flip the switch from somewhere other than the button (the phone app). Same code path, so the two
        /// surfaces cannot drift apart.</summary>
        internal static void TogglePublished() => Toggle();

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

                // Re-checked at the moment of the click, not only when the surface was drawn: a gamemode session can
                // start between the two, and publishing over its lobby rewrites metadata the round is using.
                if (!CanPublish) { Core.Log?.Warning("[sync] this lobby is not one that can be published."); return; }

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
                    $"{plan.AutoCount + plan.GhCount}/{plan.LinkCount}/{plan.DroppedCount}");
                if (!ok) { Core.Log?.Warning("[sync] could not publish the lobby."); return; }

                // Tell the lobby the host's world is up, or nobody can ever join it.
                //
                // A joiner only starts loading when SteamLobbyService.OnLobbyEntered finds "ready", "load_tutorial"
                // or "host_loading" set; none of them matching means the client just sits in the menu with no error.
                // And "ready" is written in exactly one place in the game - at the END of the host's world load, and
                // only while already in a lobby (LoadManager). A lobby opened from the pause menu, which is the only
                // kind this button is offered on, was created long after that ran, so OnLobbyCreated's "false" is
                // the last word on it forever.
                //
                // Here the claim is simply true: the host is standing in their loaded world. The chat message covers
                // anyone already waiting in the lobby, since they read it instead of the key. Neither is undone on
                // unpublish - the world stays loaded, so the keys stay honest and a Steam invite keeps working.
                try
                {
                    lobby.SetLobbyData("host_loading", "false");
                    lobby.SetLobbyData("ready", "true");
                    lobby.SendLobbyMessage("ready");
                }
                catch (Exception e) { Core.Log?.Warning("[sync] could not mark the lobby ready to join: " + e.Message); }

                PublicLobbyAccess.Enable();
                _published = true;
                Core.Log?.Msg($"[sync] lobby published live ({plan.AutoCount + plan.GhCount}/{plan.LinkCount}/{plan.DroppedCount} mods).");
            }
            catch (Exception e) { Core.Log?.Warning("[sync] publish toggle failed: " + e.Message); }
        }

        /// <summary>A session end / menu return tears the listing down (the lobby is gone).
        ///
        /// This used to only clear the button state. The backend entry stayed, and the heartbeat - which runs
        /// independently of the sync session so a live-published lobby keeps its listing - refreshed it forever, so
        /// the website advertised a dead lobby (frozen at 1 player) until the game process ended. Untag is idempotent,
        /// so calling this when nothing was published, or twice, costs nothing.</summary>
        internal static void Reset()
        {
            if (!_published) return;
            _published = false;
            try
            {
                VanillaLobby.Untag();
                PublicLobbyAccess.Disable();
                Core.Log?.Msg("[sync] live-published lobby withdrawn.");
            }
            catch (Exception e) { Core.Log?.Warning("[sync] live publish teardown failed: " + e.Message); }
        }
    }
}
