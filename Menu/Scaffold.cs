using System;
using DooDesch.UI;
using S1API.UI;
using UnityEngine;
using UnityEngine.UI;

namespace SideHustle.Menu
{
    /// <summary>
    /// The page every form-host screen is built from: a scrolling body above a footer button row, plus the note and
    /// footer-button primitives that go in them. One place decides the spacing, the note height and the button
    /// sizes, so two screens cannot drift apart again.
    /// </summary>
    internal static class Scaffold
    {
        /// <summary>Horizontal inset of the body and the footer from the panel edge.</summary>
        internal const float Pad = 30f;
        internal const float FooterHeight = 56f;
        /// <summary>Space between the footer and the body above it.</summary>
        internal const float FooterGap = 8f;
        internal const float NoteHeight = 40f;
        internal const float NoteInset = 12f;
        internal const float ButtonHeight = 40f;
        internal const float SecondaryWidth = 140f;
        internal const float PrimaryWidth = 200f;
        internal const float ListSpacing = 6f;

        /// <summary>The scroll body's content root and the footer row of one page.</summary>
        internal readonly struct Page
        {
            internal readonly RectTransform Content;
            internal readonly Transform Footer;
            internal readonly ScrollRect Scroll;
            internal readonly RectTransform ScrollArea;

            internal Page(RectTransform content, Transform footer, ScrollRect scroll, RectTransform scrollArea)
            {
                Content = content; Footer = footer; Scroll = scroll; ScrollArea = scrollArea;
            }
        }

        /// <summary>Footer first, then the scroll area above it. <paramref name="topInset"/> leaves room above the
        /// body for a toolbar the caller places itself. A list that restores its own scroll position on rebuild passes
        /// <paramref name="smoothScroll"/> false, so the glide does not fight the jump.</summary>
        internal static Page Create(Transform formHost, float topInset = 0f, float spacing = ListSpacing, bool smoothScroll = true)
        {
            var footer = UIFactory.Panel("footer", formHost, Theme.Clear);
            var frt = footer.GetComponent<RectTransform>();
            frt.anchorMin = new Vector2(0, 0); frt.anchorMax = new Vector2(1, 0); frt.pivot = new Vector2(0.5f, 0);
            frt.offsetMin = new Vector2(Pad, 0); frt.offsetMax = new Vector2(-Pad, FooterHeight);

            var area = UIFactory.Panel("scrollArea", formHost, Theme.Clear);
            var art = area.GetComponent<RectTransform>();
            art.anchorMin = new Vector2(0, 0); art.anchorMax = new Vector2(1, 1);
            art.offsetMin = new Vector2(Pad, FooterHeight + FooterGap); art.offsetMax = new Vector2(-Pad, -topInset);

            var content = Components.ScrollList(area.transform, out var scroll, spacing, Theme.ScrimPanel);
            if (smoothScroll) SmoothScroll.Attach(scroll);
            return new Page(content, footer.transform, scroll, art);
        }

        /// <summary>A single wrapped line of body text in the page. Muted unless a state colour says more.</summary>
        internal static void Note(RectTransform content, string text, Color? color = null)
        {
            var row = UIFactory.Panel("note", content, Theme.Clear);
            var rowImg = row.GetComponent<Image>(); if (rowImg != null) rowImg.raycastTarget = false;
            var rle = row.AddComponent<LayoutElement>();
            rle.minHeight = NoteHeight; rle.preferredHeight = NoteHeight; rle.flexibleWidth = 1;
            var t = UIFactory.Text("text", text, row.transform, Theme.Body, TextAnchor.MiddleLeft);
            t.color = color ?? Theme.TextMuted;
            t.raycastTarget = false;
            var rt = t.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(NoteInset, 0); rt.offsetMax = new Vector2(-NoteInset, 0);
        }

        /// <summary>A footer button pinned to the left or right edge, <paramref name="offsetX"/> further inwards.
        /// The size follows the role: 140 for a secondary action, 200 for the primary one, unless given.</summary>
        internal static Button FooterButton(Transform footer, string label, Color color, bool left, Action onClick,
            float offsetX = 0f, float width = 0f)
        {
            if (width <= 0f) width = left ? SecondaryWidth : PrimaryWidth;
            var (go, btn, _) = UIFactory.ButtonWithLabel(label, label, footer, color, width, ButtonHeight);
            PlaceFooter(go, left, offsetX);
            btn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onClick?.Invoke()));
            return btn;
        }

        /// <summary>Pins an existing footer button, for callers that need the label component back.</summary>
        internal static void PlaceFooter(GameObject go, bool left, float offsetX = 0f)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(left ? 0 : 1, 0.5f); rt.anchorMax = new Vector2(left ? 0 : 1, 0.5f);
            rt.pivot = new Vector2(left ? 0 : 1, 0.5f);
            rt.anchoredPosition = new Vector2(left ? offsetX : -offsetX, 0);
        }
    }

    /// <summary>Wording helpers shared by every screen that prints a count.</summary>
    internal static class Words
    {
        /// <summary>"1 mod", "3 mods": the count with the matching noun form.</summary>
        internal static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
    }
}
