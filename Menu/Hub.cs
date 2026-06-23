using System;
using Il2CppScheduleOne.UI.MainMenu;
using SideHustle.Internal;
using S1API.UI;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SideHustle.Menu
{
    /// <summary>
    /// Owns the gamemode-selection panel and the launch flow. The menu button (see <see cref="MenuInjector"/>)
    /// calls <see cref="ToggleHubPanel"/>. Selecting a singleplayer gamemode hides the menu and invokes its
    /// launch callback; the gamemode calls LaunchContext.ReturnToHub when done, which routes back to
    /// <see cref="OnReturn"/>. Multiplayer host/join are shown but disabled until Phase 2.
    /// </summary>
    internal static class Hub
    {
        private static bool _initialized;
        private static GameObject _canvasGO;
        private static GameObject _window;
        private static RectTransform _listContent;
        private static MainMenuScreen _home;
        private static LaunchContext _activeCtx;

        private static readonly Color DimColor = new Color(0f, 0f, 0f, 0.75f);
        private static readonly Color WindowColor = new Color(0.10f, 0.10f, 0.12f, 0.98f);
        private static readonly Color RowColor = new Color(0.18f, 0.18f, 0.22f, 1f);
        private static readonly Color BackColor = new Color(0.30f, 0.30f, 0.34f, 1f);
        private static readonly Color DisabledColor = new Color(0.16f, 0.16f, 0.18f, 1f);

        internal static void EnsureInit()
        {
            if (_initialized) return;
            _initialized = true;
            HubBridge.ReturnHandler = OnReturn;
        }

        internal static void RememberHome(MainMenuScreen home) => _home = home;

        /// <summary>Tear everything down when the Menu scene unloads, so we rebuild fresh next time.</summary>
        internal static void Teardown()
        {
            try { if (_canvasGO != null) UnityEngine.Object.Destroy(_canvasGO); }
            catch { /* ignore */ }
            _canvasGO = null;
            _window = null;
            _listContent = null;
            _activeCtx = null;
        }

        internal static void ToggleHubPanel()
        {
            EnsureInit();
            if (_canvasGO == null) { Build(); BuildList(); return; }
            bool show = !_canvasGO.activeSelf;
            _canvasGO.SetActive(show);
            if (show) BuildList();
        }

        private static void HidePanel() { if (_canvasGO != null) _canvasGO.SetActive(false); }
        private static void ShowPanel() { if (_canvasGO != null) _canvasGO.SetActive(true); }

        // --- panel construction ---

        private static void Build()
        {
            _canvasGO = new GameObject("SideHustle_HubCanvas");
            UnityEngine.Object.DontDestroyOnLoad(_canvasGO);
            var canvas = _canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            _canvasGO.AddComponent<GraphicRaycaster>();

            // Full-screen dim background that also blocks clicks to the menu behind.
            GameObject dim = UIFactory.Panel("Dim", _canvasGO.transform, DimColor, fullAnchor: true);

            // Centered window.
            _window = UIFactory.Panel("Window", dim.transform, WindowColor);
            var wrt = _window.GetComponent<RectTransform>();
            wrt.anchorMin = new Vector2(0.5f, 0.5f);
            wrt.anchorMax = new Vector2(0.5f, 0.5f);
            wrt.pivot = new Vector2(0.5f, 0.5f);
            wrt.sizeDelta = new Vector2(640f, 720f);
            wrt.anchoredPosition = Vector2.zero;

            var vlg = _window.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(20, 20, 20, 20);
            vlg.spacing = 14;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var title = UIFactory.Text("Title", "Side Hustle", _window.transform, 30, TextAnchor.MiddleCenter, FontStyle.Bold);
            AddHeight(title.gameObject, 46f);

            var subtitle = UIFactory.Text("Subtitle", "Choose a gamemode", _window.transform, 16, TextAnchor.MiddleCenter);
            subtitle.color = new Color(0.7f, 0.7f, 0.75f);
            AddHeight(subtitle.gameObject, 24f);

            var content = UIFactory.ScrollableVerticalList("List", _window.transform, out var scroll);
            var scrollLE = scroll.gameObject.AddComponent<LayoutElement>();
            scrollLE.flexibleHeight = 1f;
            scrollLE.minHeight = 460f;
            _listContent = content;

            var (closeGO, closeBtn, _) = UIFactory.ButtonWithLabel("Close", "Close", _window.transform, BackColor, 200f, 52f);
            AddHeight(closeGO, 52f);
            closeBtn.onClick.AddListener((UnityAction)HidePanel);
        }

        private static void BuildList()
        {
            if (_listContent == null) return;
            UIFactory.ClearChildren(_listContent);

            var gamemodes = API.Registered;
            if (gamemodes.Count == 0)
            {
                var empty = UIFactory.Text("Empty",
                    "No gamemodes installed.\nInstall a Side Hustle gamemode mod (e.g. Inkubator) and it will appear here.",
                    _listContent, 16, TextAnchor.MiddleCenter);
                empty.color = new Color(0.7f, 0.7f, 0.75f);
                AddHeight(empty.gameObject, 120f);
                return;
            }

            foreach (GamemodeDescriptor d in gamemodes)
            {
                GamemodeDescriptor desc = d; // capture per-iteration
                string badge = desc.Support == GamemodeSupport.Singleplayer ? "SP"
                    : desc.Support == GamemodeSupport.Multiplayer ? "MP" : "SP+MP";
                string label = $"{desc.DisplayName}   [{badge}]";

                var (rowGO, rowBtn, _) = UIFactory.ButtonWithLabel("gm_" + desc.Id, label, _listContent, RowColor, 600f, 56f);
                AddHeight(rowGO, 56f);
                rowBtn.onClick.AddListener((UnityAction)(() => OnSelectGamemode(desc)));
            }
        }

        private static void OnSelectGamemode(GamemodeDescriptor desc)
        {
            if (desc.AllowsMultiplayer && !(desc.Support == GamemodeSupport.Singleplayer))
                ShowModeChoice(desc);
            else
                LaunchSingleplayer(desc);
        }

        // Multiplayer / hybrid: Singleplayer (if allowed) / Host / Join. Host+Join are Phase 2, shown disabled.
        private static void ShowModeChoice(GamemodeDescriptor desc)
        {
            if (_listContent == null) return;
            UIFactory.ClearChildren(_listContent);

            var head = UIFactory.Text("ModeHead", desc.DisplayName, _listContent, 20, TextAnchor.MiddleCenter, FontStyle.Bold);
            AddHeight(head.gameObject, 40f);

            if (desc.AllowsSingleplayer)
            {
                var (spGO, spBtn, _) = UIFactory.ButtonWithLabel("mode_sp", "Singleplayer", _listContent, RowColor, 560f, 54f);
                AddHeight(spGO, 54f);
                spBtn.onClick.AddListener((UnityAction)(() => LaunchSingleplayer(desc)));
            }

            AddDisabled("Host Game  (coming soon)");
            AddDisabled("Join Game  (coming soon)");

            var (backGO, backBtn, _) = UIFactory.ButtonWithLabel("mode_back", "Back", _listContent, BackColor, 560f, 50f);
            AddHeight(backGO, 50f);
            backBtn.onClick.AddListener((UnityAction)BuildList);
        }

        private static void AddDisabled(string label)
        {
            var (go, btn, txt) = UIFactory.ButtonWithLabel("mode_disabled", label, _listContent, DisabledColor, 560f, 54f);
            AddHeight(go, 54f);
            btn.interactable = false;
            txt.color = new Color(0.55f, 0.55f, 0.6f);
        }

        // --- launch / return ---

        private static void LaunchSingleplayer(GamemodeDescriptor desc)
        {
            if (desc.OnLaunchSingleplayer == null)
            {
                Core.Log?.Warning($"Gamemode '{desc.Id}' has no singleplayer launch callback.");
                return;
            }

            _activeCtx = new LaunchContext { Descriptor = desc, IsHost = null, LobbyId = 0 };
            HidePanel();
            HideMenu();
            Core.Log?.Msg($"Launching gamemode '{desc.DisplayName}' (singleplayer).");
            try { desc.OnLaunchSingleplayer(_activeCtx); }
            catch (Exception e)
            {
                Core.Log?.Error($"Gamemode '{desc.Id}' launch threw: {e}");
                OnReturn(_activeCtx);
            }
        }

        private static void OnReturn(LaunchContext ctx)
        {
            try { ctx?.Descriptor?.OnExitToHub?.Invoke(ctx); }
            catch (Exception e) { Core.Log?.Warning("OnExitToHub threw: " + e.Message); }

            ShowMenu();
            ShowPanel();
            BuildList();
            _activeCtx = null;
        }

        // --- menu show/hide (so a menu-space gamemode overlay is unobstructed) ---

        private static void HideMenu()
        {
            if (_home == null || _home.Group == null) return;
            _home.Group.alpha = 0f;
            _home.Group.interactable = false;
            _home.Group.blocksRaycasts = false;
        }

        private static void ShowMenu()
        {
            if (_home == null || _home.Group == null) return;
            _home.Group.alpha = 1f;
            _home.Group.interactable = true;
            _home.Group.blocksRaycasts = true;
        }

        private static void AddHeight(GameObject go, float h)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.minHeight = h;
            le.preferredHeight = h;
            le.flexibleWidth = 1f;
        }
    }
}
