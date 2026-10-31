using System;
using System.IO;
using SideHustle.Config;
using SideHustle.Sync;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SideHustle.Menu
{
    /// <summary>
    /// Folding the hub away: a grip on the right screen edge closes the page and opens it again, and the choice
    /// survives a game start. Closing runs through the cloned screen like Esc does, so the vanilla menu and its
    /// "Side Hustle" entry come back on their own and the page's own state is untouched.
    /// </summary>
    internal static partial class Hub
    {
        private const float GripWidth = 26f;
        private const float GripHeight = 84f;
        private const float GripIconSize = 20f;
        private const string GripIconResource = "SideHustle.Assets.hub.icons.chevron-right.png";

        // The grip has its own canvas: the page's canvas switches off while the page is folded away.
        private static GameObject _gripRoot;
        private static Canvas _gripCanvas;
        private static CanvasGroup _gripGroup;
        private static RectTransform _gripIcon;

        private static bool _hubWasOpen;

        /// <summary>Set for one tick by a close that starts a session, so it does not count as folding away.</summary>
        private static bool _closedForLaunch;

        private static bool _gripFailed;

        /// <summary>Called by the "Side Hustle" menu entry and the grip: the player asked for the hub.</summary>
        internal static void OpenScreenByPlayer()
        {
            if (Preferences.HubFolded) Core.Log?.Msg("[hub3] unfolded by the player.");
            Preferences.HubFolded = false;
            OpenScreen();
        }

        private static void OnGripClicked()
        {
            if (!ScreenOpen) { OpenScreenByPlayer(); return; }
            // TickFold sees the screen go and records it, the same as for Esc.
            Core.Log?.Msg("[hub3] the grip closes the hub.");
            _cloneScreen.Close();
        }

        /// <summary>Per frame: records a close by the player, and keeps the grip where it belongs.</summary>
        private static void TickFold()
        {
            bool open = ScreenOpen;
            // Esc is native, so the close shows up here rather than in a handler. The vanilla menu being on top
            // tells it apart from a sub-screen such as Settings, which only covers the hub for a while.
            if (_hubWasOpen && !open && !_closedForLaunch && !HubBusy() && _home != null && _home.IsOpen)
            {
                Preferences.HubFolded = true;
                Core.Log?.Msg("[hub3] folded away by the player.");
            }
            _hubWasOpen = open;
            _closedForLaunch = false;
            TickGrip();
        }

        private static void TickGrip()
        {
            EnsureGrip();
            if (_gripCanvas == null || _gripGroup == null) return;

            bool onMenu = _hubShown || (!ScreenOpen && _home != null && _home.IsOpen);
            // The chat column sits on the right edge, and a running launch or download keeps its screen up.
            bool wanted = onMenu && !HubBusy() && !SyncRun.Active && ChatPanel.ScreenShare <= 0f;
            float cover = MenuCover();
            bool show = wanted && cover < 0.5f;
            if (_gripCanvas.enabled != show) _gripCanvas.enabled = show;
            if (!show) return;

            _gripGroup.alpha = 1f - cover;
            // The chevron points where the page will go: out to the edge while open, back in while folded.
            float x = _hubShown ? 1f : -1f;
            if (_gripIcon != null && !Mathf.Approximately(_gripIcon.localScale.x, x)) _gripIcon.localScale = new Vector3(x, 1f, 1f);
        }

        private static void EnsureGrip()
        {
            if (_gripRoot != null || _gripFailed) return;
            try
            {
                _gripRoot = new GameObject("SideHustle_HubGrip");
                _gripCanvas = _gripRoot.AddComponent<Canvas>();
                _gripCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _gripCanvas.sortingOrder = 1001;   // right above the page, below the dialog overlay and the notices
                var scaler = _gripRoot.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;
                _gripRoot.AddComponent<GraphicRaycaster>();
                _gripGroup = _gripRoot.AddComponent<CanvasGroup>();

                var body = new GameObject("grip");
                var rect = body.AddComponent<RectTransform>();
                rect.SetParent(_gripRoot.transform, false);
                rect.anchorMin = rect.anchorMax = new Vector2(1f, 0.5f);
                rect.pivot = new Vector2(1f, 0.5f);
                // Pushed past the edge by the corner radius, so only the left corners show as rounded.
                rect.anchoredPosition = new Vector2(10f, 0f);
                rect.sizeDelta = new Vector2(GripWidth + 10f, GripHeight);

                var image = body.AddComponent<Image>();
                image.sprite = DooDesch.UI.Theme.RoundedSprite();
                image.type = Image.Type.Sliced;

                var button = body.AddComponent<Button>();
                var colors = button.colors;
                colors.normalColor = new Color(0.051f, 0.059f, 0.078f, 0.92f);
                colors.highlightedColor = new Color(0.137f, 0.149f, 0.176f, 1f);
                colors.pressedColor = new Color(0.180f, 0.196f, 0.227f, 1f);
                colors.selectedColor = colors.normalColor;
                colors.fadeDuration = 0.08f;
                button.colors = colors;
                button.targetGraphic = image;
                button.onClick.AddListener((UnityAction)OnGripClicked);

                var sprite = LoadGripIcon();
                if (sprite != null)
                {
                    var iconObject = new GameObject("icon");
                    _gripIcon = iconObject.AddComponent<RectTransform>();
                    _gripIcon.SetParent(rect, false);
                    _gripIcon.anchorMin = _gripIcon.anchorMax = new Vector2(0.5f, 0.5f);
                    _gripIcon.pivot = new Vector2(0.5f, 0.5f);
                    _gripIcon.anchoredPosition = new Vector2(-5f, 0f);
                    _gripIcon.sizeDelta = new Vector2(GripIconSize, GripIconSize);
                    var icon = iconObject.AddComponent<Image>();
                    icon.sprite = sprite;
                    icon.color = new Color(0.957f, 0.957f, 0.949f, 0.85f);
                    icon.raycastTarget = false;
                }
                Core.Log?.Msg("[hub3] grip on the right screen edge created.");
            }
            catch (Exception e)
            {
                Core.Log?.Error("[hub3] could not create the grip; Esc and the menu entry still close and open the hub: " + e);
                DestroyGrip();
                _gripFailed = true;
            }
        }

        private static Sprite LoadGripIcon()
        {
            using var stream = typeof(Hub).Assembly.GetManifestResourceStream(GripIconResource);
            if (stream == null)
            {
                Core.Log?.Error($"[hub3] the grip icon '{GripIconResource}' is not embedded; the grip has no chevron.");
                return null;
            }
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            if (!ImageConversion.LoadImage(tex, bytes.ToArray()))
            {
                Core.Log?.Error("[hub3] the grip icon is not a readable PNG; the grip has no chevron.");
                return null;
            }
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
        }

        private static void DestroyGrip()
        {
            Safe.Try("destroying the hub grip", () => { if (_gripRoot != null) UnityEngine.Object.Destroy(_gripRoot); });
            _gripRoot = null;
            _gripCanvas = null;
            _gripGroup = null;
            _gripIcon = null;
            _hubWasOpen = false;
            _closedForLaunch = false;
        }
    }
}
