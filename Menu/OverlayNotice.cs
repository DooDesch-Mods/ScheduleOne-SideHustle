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

        private OverlayNotice(GameObject go, Text body) { _go = go; _body = body; }

        internal static OverlayNotice Build(string name, string headline, string body, float dim)
        {
            var go = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(go);   // survives the menu scene reloading under it
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;   // above the menu and the phone; this is the last thing the player sees

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

            return new OverlayNotice(go, line);
        }

        internal void SetBody(string text)
        {
            if (_body != null) _body.text = text ?? "";
        }

        internal void Close()
        {
            if (_go == null) return;
            try { UnityEngine.Object.Destroy(_go); }
            catch (Exception e) { Core.Log?.Warning("[sync] could not close a notice overlay: " + e.Message); }
        }
    }
}
