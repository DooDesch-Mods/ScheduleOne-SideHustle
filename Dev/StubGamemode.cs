#if DEBUG
using System;
using S1API.UI;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SideHustle.Dev
{
    /// <summary>
    /// DEBUG-only sample gamemode so the menu list is non-empty before a real consumer is loaded. It registers
    /// as a menu-space MULTIPLAYER gamemode exposing one of every host-setting control (slider/toggle/segmented/
    /// text), so the Host-config form can be exercised end to end - register -> list -> Host -> form -> launch ->
    /// return - without depending on a heavier gamemode mod. Excluded from Release builds (csproj Compile Remove Dev/**).
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
                Description = "Sample multiplayer gamemode for testing the hub + host form.",
                Author = "DooDesch",
                Support = GamemodeSupport.Multiplayer,
                Surface = GamemodeSurface.MenuSpace,
                OnHostMultiplayer = Launch,
                OnJoinMultiplayer = Launch,
                OnExitToHub = _ => Close(),
                HostSettings = new[]
                {
                    new SettingDescriptor { Key = "round", Label = "Round time", Hint = "Length of one round.", Type = SettingType.Slider, Min = 60, Max = 600, Step = 5, WholeNumbers = true, Unit = "s", Default = "180" },
                    new SettingDescriptor { Key = "ff", Label = "Friendly fire", Hint = "Players can damage teammates.", Type = SettingType.Toggle, Default = "0" },
                    new SettingDescriptor { Key = "end", Label = "End on", Hint = "What finishes the round.", Type = SettingType.Segmented, Options = new[] { "Spectator", "Infection", "Timer" }, Default = "Spectator" },
                    new SettingDescriptor { Key = "map", Label = "Map name", Hint = "Free-text map label.", Type = SettingType.Text, Default = "Downtown" }
                }
            });
        }

        private static void Launch(LaunchContext ctx)
        {
            var mp = ctx?.Multiplayer;
            Core.Log?.Msg($"[stub] launched: host={ctx?.IsHost} players={ctx?.PlayerCount} pw={(mp != null && mp.HasPassword)} " +
                          $"blob='{mp?.ConfigBlob}' -> round={mp?.GetInt("round", -1)} ff={mp?.GetBool("ff", false)} " +
                          $"end='{mp?.GetSetting("end", "?")}' map='{mp?.GetSetting("map", "?")}'");
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
