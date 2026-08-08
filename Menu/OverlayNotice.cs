using System;
using DooDesch.UI;
using S1API.UI;   // UIFactory
using UnityEngine;
using UnityEngine.UI;

namespace SideHustle.Menu
{
    /// <summary>
    /// A headline plus one line of explanation, on its own canvas above everything else.
    ///
    /// The sync flow needs this exactly where it has no screen to trust: a relaunch tears the menu down, and a
    /// just-restarted process has no hub open yet. Anything anchored to a menu screen is therefore unreliable at the two
    /// moments the player most needs to be told what is happening - which is how the restart notice once ended up
    /// looking intermittent (it refused to draw unless a particular screen was open).
    ///
    /// <paramref name="dim"/> is the point of the scrim being a parameter: over a finished install list the screen
    /// behind is the evidence and stays readable; over a bare main menu there is nothing worth showing through.
    /// </summary>
    internal sealed class OverlayNotice
    {
        private readonly GameObject _go;
        private readonly Text _body;
        private readonly Transform _scrim;
        private GameObject _button;

        private OverlayNotice(GameObject go, Text body, Transform scrim) { _go = go; _body = body; _scrim = scrim; }

        internal static OverlayNotice Build(string name, string headline, string body, float dim)
        {
            var go = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(go);   // survives the menu scene reloading under it
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;   // above the menu and the phone; this is the last thing the player sees
            // A canvas that only ever showed text needed no raycaster. This one can carry a way out, and a button on a
            // canvas without one is drawn perfectly and never clickable.
            go.AddComponent<GraphicRaycaster>();

            var scrim = UIFactory.Panel("scrim", go.transform, Theme.WithAlpha(Theme.BgDeep, dim), fullAnchor: true);

            var head = UIFactory.Text("head", headline, scrim.transform, 34, TextAnchor.LowerCenter, FontStyle.Bold);
            head.color = Theme.TextPrimary;
            var hrt = head.rectTransform;
            hrt.anchorMin = new Vector2(0f, 0.5f); hrt.anchorMax = new Vector2(1f, 0.5f);
            hrt.offsetMin = new Vector2(40f, 4f); hrt.offsetMax = new Vector2(-40f, 60f);

            var line = UIFactory.Text("body", body ?? "", scrim.transform, Theme.Body, TextAnchor.UpperCenter);
            line.color = Theme.TextMuted;
            var brt = line.rectTransform;
            brt.anchorMin = new Vector2(0f, 0.5f); brt.anchorMax = new Vector2(1f, 0.5f);
            brt.offsetMin = new Vector2(60f, -70f); brt.offsetMax = new Vector2(-60f, -6f);

            return new OverlayNotice(go, line, scrim.transform);
        }

        internal void SetBody(string text)
        {
            if (_body != null) _body.text = text ?? "";
        }

        /// <summary>
        /// Take the whole overlay off screen without destroying it, so a step can hand the screen back and take it
        /// again. Needed because this canvas sits at sorting order 32000 and would otherwise cover the game's own
        /// popups - and the player has to be able to read those.
        /// </summary>
        internal void SetVisible(bool visible)
        {
            if (_go == null) return;
            try { if (_go.activeSelf != visible) _go.SetActive(visible); }
            catch (Exception e) { Core.Log?.Warning("[sync] could not toggle a notice overlay: " + e.Message); }
        }

        /// <summary>Give the notice a way out. Centred under the body line; a second call replaces the first.</summary>
        internal void AddButton(string label, Action onClick)
        {
            if (_scrim == null) return;
            try
            {
                if (_button != null) UnityEngine.Object.Destroy(_button);
                var (go, btn, _) = UIFactory.ButtonWithLabel("cancel", label, _scrim, Theme.Button, 220, 40);
                _button = go;
                var rt = go.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0.5f); rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0f, -90f);
                btn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onClick?.Invoke()));
                Interactions.ApplyStates(btn);
            }
            catch (Exception e) { Core.Log?.Warning("[sync] could not add a button to a notice overlay: " + e.Message); }
        }

        internal void Close()
        {
            if (_go == null) return;
            try { UnityEngine.Object.Destroy(_go); }
            catch (Exception e) { Core.Log?.Warning("[sync] could not close a notice overlay: " + e.Message); }
        }
    }
}
