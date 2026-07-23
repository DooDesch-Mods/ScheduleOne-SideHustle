using UnityEngine;

namespace SideHustle.Messenger
{
    /// <summary>
    /// WhatsDab's own app theme. The phone's native apps are LIGHT (white lists, dark text, hairline separators),
    /// so the chat app follows that language instead of the violet-dark menu design system - with a WhatsApp-like
    /// green accent to match the app's name: dark-green header, white contact list, paper-tone chat background,
    /// green own-bubbles. Local to the Messenger on purpose; menus keep using DooDesch.UI.Theme.
    /// </summary>
    internal static class WDTheme
    {
        internal static readonly Color Header = new Color(0.027f, 0.369f, 0.329f, 1f);       // #075E54 top bar
        internal static readonly Color HeaderLit = new Color(0.075f, 0.463f, 0.416f, 1f);    // #13766A back-button fill
        internal static readonly Color HeaderText = Color.white;

        internal static readonly Color Screen = new Color(0.988f, 0.988f, 0.988f, 1f);       // #FCFCFC list background
        internal static readonly Color ChatBg = new Color(0.925f, 0.898f, 0.867f, 1f);       // #ECE5DD thread paper
        internal static readonly Color TextDark = new Color(0.067f, 0.078f, 0.086f, 1f);     // #111416 primary
        internal static readonly Color TextGray = new Color(0.400f, 0.467f, 0.506f, 1f);     // #667781 secondary
        internal static readonly Color Hairline = new Color(0.914f, 0.929f, 0.937f, 1f);     // #E9EDEF row separators

        internal static readonly Color Green = new Color(0.145f, 0.827f, 0.400f, 1f);        // #25D366 send / badge
        internal static readonly Color GreenDark = new Color(0.000f, 0.659f, 0.420f, 1f);    // #00A86B caret / accents

        internal static readonly Color MineBubble = new Color(0.863f, 0.973f, 0.776f, 1f);   // #DCF8C6
        internal static readonly Color TheirBubble = Color.white;
        internal static readonly Color ComposeBar = new Color(0.941f, 0.949f, 0.961f, 1f);   // #F0F2F5
        internal static readonly Color InputBg = Color.white;

        // Sender/avatar tints: saturated enough for white initials, dark enough to read as names on white.
        internal static readonly Color[] People =
        {
            new Color(0.753f, 0.224f, 0.169f, 1f), // red
            new Color(0.161f, 0.502f, 0.725f, 1f), // blue
            new Color(0.153f, 0.682f, 0.376f, 1f), // green
            new Color(0.557f, 0.267f, 0.678f, 1f), // purple
            new Color(0.827f, 0.329f, 0.000f, 1f), // orange
            new Color(0.086f, 0.502f, 0.553f, 1f), // teal
        };

        internal static Color PersonColor(string name)
        {
            int h = 0;
            if (!string.IsNullOrEmpty(name)) foreach (char c in name) h = (h * 31 + c) & 0x7fffffff;
            return People[h % People.Length];
        }

        // A soft antialiased circle sprite for avatars, unread dots and badges. Generated once.
        private static Sprite _circle;
        internal static Sprite CircleSprite()
        {
            if (_circle != null) return _circle;
            const int S = 64;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
            float c = (S - 1) / 2f, r = c - 1f;
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    float a = Mathf.Clamp01(r - d + 1f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            tex.Apply();
            _circle = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f);
            if (_circle != null) _circle.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return _circle;
        }
    }
}
