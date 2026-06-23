#if DEBUG
using System;
using S1API.UI;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SideHustle.Dev
{
    /// <summary>
    /// DEBUG-only sample gamemode so the menu list is non-empty before a real consumer (Inkubator) exists.
    /// It registers itself as a menu-space singleplayer gamemode and, on launch, shows a tiny overlay with a
    /// Back button that returns to the hub - exercising the full register -> list -> launch -> return loop.
    /// Excluded from Release builds (csproj Compile Remove Dev/**).
    /// </summary>
    internal static class StubGamemode
    {
        private static GameObject _overlay;

        internal static void Register()
        {
            API.Register(new GamemodeDescriptor
            {
                Id = "sidehustle.debugstub",
                DisplayName = "Debug Stub",
                Description = "Sample gamemode for testing the hub.",
                Author = "DooDesch",
                Support = GamemodeSupport.Singleplayer,
                Surface = GamemodeSurface.MenuSpace,
                OnLaunchSingleplayer = Launch,
                OnExitToHub = _ => Close()
            });
        }

        private static void Launch(LaunchContext ctx)
        {
            Close();
            _overlay = new GameObject("SideHustle_StubOverlay");
            UnityEngine.Object.DontDestroyOnLoad(_overlay);
            var canvas = _overlay.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30100;
            _overlay.AddComponent<GraphicRaycaster>();

            var panel = UIFactory.Panel("Panel", _overlay.transform, new Color(0.05f, 0.05f, 0.08f, 1f), fullAnchor: true);

            UIFactory.Text("Msg", "Debug Stub gamemode is running.\nThis proves register -> launch works.",
                panel.transform, 26, TextAnchor.MiddleCenter, FontStyle.Bold);

            var (backGO, backBtn, _) = UIFactory.ButtonWithLabel("Back", "Back to hub", panel.transform,
                new Color(0.30f, 0.30f, 0.34f, 1f), 220f, 56f);
            var rt = backGO.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 80f);
            backBtn.onClick.AddListener((UnityAction)(() => ctx.ReturnToHub()));

            Core.Log?.Msg("[stub] overlay shown.");
        }

        private static void Close()
        {
            if (_overlay != null) { UnityEngine.Object.Destroy(_overlay); _overlay = null; }
        }
    }
}
#endif
