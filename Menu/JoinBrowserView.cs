using System;
using System.Collections.Generic;
using DooDesch.UI;
using S1API.UI;
using SideHustle.Multiplayer;
using UnityEngine;
using UnityEngine.UI;

namespace SideHustle.Menu
{
    /// <summary>
    /// The Join lobby browser rendered on the cloned native panel (the counterpart to the host form): a scrollable
    /// list of lobby cards (host, player count, gamemode, a lock when password-protected) each with a Join button,
    /// plus a Refresh / Back footer. The card list is rebuilt as the asynchronous lobby query returns results.
    /// </summary>
    internal static class JoinBrowserView
    {
        private const float Pad = 30f;

        /// <summary>Build the scroll area + footer; returns the scroll content the caller fills via SetStatus/Populate.</summary>
        internal static Transform Build(Transform formHost, Action onBack, Action onRefresh)
        {
            var footer = NewPanel("footer", formHost);
            var frt = footer.GetComponent<RectTransform>();
            frt.anchorMin = new Vector2(0, 0); frt.anchorMax = new Vector2(1, 0); frt.pivot = new Vector2(0.5f, 0);
            frt.offsetMin = new Vector2(Pad, 0); frt.offsetMax = new Vector2(-Pad, 56);

            var scrollArea = NewPanel("scrollArea", formHost);
            var srt = scrollArea.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(0, 0); srt.anchorMax = new Vector2(1, 1); srt.offsetMin = new Vector2(Pad, 64); srt.offsetMax = new Vector2(-Pad, 0);
            var content = Components.ScrollList(scrollArea.transform, out _, 8f);

            var (backGO, backBtn, _b) = UIFactory.ButtonWithLabel("Back", "Back", footer.transform, Theme.Button, 140, 40);
            PlaceFooter(backGO, left: true);
            backBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onBack?.Invoke()));

            var (refGO, refBtn, _r) = UIFactory.ButtonWithLabel("Refresh", "Refresh", footer.transform, Theme.Accent, 160, 40);
            PlaceFooter(refGO, left: false);
            refBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onRefresh?.Invoke()));

            Interactions.PolishButtons(formHost);
            return content;
        }

        /// <summary>Show a single centered status message (searching / empty), clearing any cards.</summary>
        internal static void SetStatus(Transform content, string message)
        {
            if (content == null) return;   // the browser was torn down (navigated away) before this fired
            Clear(content);
            var card = NewCard(content, 64f);
            var t = UIFactory.Text("msg", message, card.transform, Theme.Body, TextAnchor.MiddleCenter);
            t.color = Theme.TextMuted; t.raycastTarget = false;
            Fill(t.rectTransform);
        }

        /// <summary>Rebuild the card list from the lobby results. <paramref name="localBuild"/> is the local gamemode's
        /// build fingerprint; a row whose host build differs is flagged as a version mismatch (still joinable).</summary>
        internal static void Populate(Transform content, List<LobbyRow> lobbies, Action<LobbyRow> onJoin, string localBuild = null)
        {
            if (content == null) return;   // navigated away before the query returned
            if (lobbies == null || lobbies.Count == 0) { SetStatus(content, "No open sessions. Host one, or refresh to look again."); return; }
            Clear(content);
            foreach (var l in lobbies) BuildCard(content, l, onJoin, localBuild);
        }

        private static void BuildCard(Transform content, LobbyRow row, Action<LobbyRow> onJoin, string localBuild)
        {
            var card = NewCard(content, 68f);
            string title = !string.IsNullOrEmpty(row.LobbyName) ? row.LobbyName
                         : string.IsNullOrEmpty(row.HostName) ? "Session" : row.HostName;
            string gm = row.GamemodeName ?? "";
            if (!string.IsNullOrEmpty(row.Mode)) gm = string.IsNullOrEmpty(gm) ? row.Mode : gm + " - " + row.Mode;
            string cap = row.MaxPlayers > 0 ? $"{row.Members} / {row.MaxPlayers} players" : $"{row.Members} player(s)";
            bool versionMismatch = !string.IsNullOrEmpty(localBuild) && !string.IsNullOrEmpty(row.BuildId)
                                   && !string.Equals(localBuild, row.BuildId, StringComparison.Ordinal);
            string sub = cap
                       + (string.IsNullOrEmpty(gm) ? "" : "   ·   " + gm)
                       + (!string.IsNullOrEmpty(row.HostName) && !string.Equals(row.LobbyName, row.HostName, StringComparison.Ordinal) ? "   ·   by " + row.HostName : "")
                       + (row.HasPassword ? "   ·   Locked" : "")
                       + (versionMismatch ? "   ·   Different version - update to match host" : "");

            var name = UIFactory.Text("name", title, card.transform, Theme.Body, TextAnchor.LowerLeft, FontStyle.Bold);
            name.color = Theme.TextPrimary; name.raycastTarget = false; name.horizontalOverflow = HorizontalWrapMode.Overflow;
            var nrt = name.rectTransform; nrt.anchorMin = new Vector2(0, 0.5f); nrt.anchorMax = new Vector2(1, 1); nrt.offsetMin = new Vector2(16, 0); nrt.offsetMax = new Vector2(-124, -4);

            var subT = UIFactory.Text("sub", sub, card.transform, Theme.Caption, TextAnchor.UpperLeft);
            subT.color = versionMismatch ? Theme.WarningText : Theme.TextMuted; subT.raycastTarget = false; subT.horizontalOverflow = HorizontalWrapMode.Overflow;
            var srt = subT.rectTransform; srt.anchorMin = new Vector2(0, 0); srt.anchorMax = new Vector2(1, 0.5f); srt.offsetMin = new Vector2(16, 4); srt.offsetMax = new Vector2(-124, 0);

            var (joinGO, joinBtn, _j) = UIFactory.ButtonWithLabel("Join", "Join", card.transform, Theme.Accent, 96, 40);
            var jrt = joinGO.GetComponent<RectTransform>();
            jrt.anchorMin = new Vector2(1, 0.5f); jrt.anchorMax = new Vector2(1, 0.5f); jrt.pivot = new Vector2(1, 0.5f);
            jrt.anchoredPosition = new Vector2(-12, 0); jrt.sizeDelta = new Vector2(96, 40);
            LobbyRow captured = row;
            joinBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onJoin?.Invoke(captured)));

            Interactions.PolishButtons(card.transform);
        }

        // --- helpers ---

        private static GameObject NewCard(Transform content, float height)
        {
            var go = UIFactory.Panel("card", content, Theme.BgElevated);
            var img = go.GetComponent<Image>(); if (img != null) { img.sprite = Theme.RoundedSprite(); img.type = Image.Type.Sliced; img.raycastTarget = false; }
            var le = go.AddComponent<LayoutElement>(); le.minHeight = height; le.preferredHeight = height; le.flexibleWidth = 1;
            return go;
        }

        private static GameObject NewPanel(string name, Transform parent)
        {
            var go = UIFactory.Panel(name, parent, Theme.Clear);
            var img = go.GetComponent<Image>(); if (img != null) img.raycastTarget = false;
            return go;
        }

        private static void PlaceFooter(GameObject go, bool left)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(left ? 0 : 1, 0.5f);
            rt.pivot = new Vector2(left ? 0 : 1, 0.5f);
            rt.anchoredPosition = new Vector2(left ? 16 : -16, 0);
        }

        private static void Fill(RectTransform rt)
        {
            if (rt == null) return;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = new Vector2(12, 0); rt.offsetMax = new Vector2(-12, 0);
        }

        // Detach immediately (so the layout doesn't show stale cards for a frame) and destroy.
        private static void Clear(Transform content)
        {
            for (int i = content.childCount - 1; i >= 0; i--)
            {
                var c = content.GetChild(i);
                c.SetParent(null, false);
                UnityEngine.Object.Destroy(c.gameObject);
            }
        }
    }
}
