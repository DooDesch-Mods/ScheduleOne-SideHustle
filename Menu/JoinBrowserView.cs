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

        // The public web lobby directory. Always the live site regardless of any LobbyDirectory API-base override.
        private const string WebDirectoryUrl = "https://sidehustle.doodesch.de";

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
            var content = Components.ScrollList(scrollArea.transform, out _, 8f, Theme.ScrimPanel);

            var (backGO, backBtn, _b) = UIFactory.ButtonWithLabel("Back", "Back", footer.transform, Theme.Button, 140, 40);
            PlaceFooter(backGO, left: true);
            backBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onBack?.Invoke()));

            var (refGO, refBtn, _r) = UIFactory.ButtonWithLabel("Refresh", "Refresh", footer.transform, Theme.Accent, 160, 40);
            PlaceFooter(refGO, left: false);
            refBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onRefresh?.Invoke()));

            // Centre link to the public web directory (SideHustle.doodesch.de) - every open lobby, filterable, in a
            // browser. Opens via the Steam overlay; joining still happens here in-game.
            var (webGO, webBtn, _w) = UIFactory.ButtonWithLabel("Web", "Browse online", footer.transform, Theme.Button, 190, 40);
            var wrt = webGO.GetComponent<RectTransform>();
            wrt.anchorMin = wrt.anchorMax = new Vector2(0.5f, 0.5f); wrt.pivot = new Vector2(0.5f, 0.5f);
            wrt.anchoredPosition = Vector2.zero; wrt.sizeDelta = new Vector2(190, 40);
            webBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => DownloadLink.Open(WebDirectoryUrl)));

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

            // The host's game branch, as a colour rather than another clause in the subtitle. IL2CPP and Mono are the
            // same Steam app and share this lobby list, but a player on one cannot join the other at all - so this is
            // not a detail among the others, it is whether the Join button can work. Green for the branch this build
            // runs on, red for the one it does not, nothing at all for a host too old to say (never a guess).
            string branch = BranchLabel(row.Runtime);
            var name = UIFactory.Text("name", title, card.transform, Theme.Body, TextAnchor.LowerLeft, FontStyle.Bold);
            name.color = Theme.TextPrimary; name.raycastTarget = false;
            // Clip, do not overflow. A uGUI Text in Overflow mode ignores its rect entirely, so the offsets below
            // constrained nothing and a 40-character lobby name printed straight across the badge and both buttons.
            // One cut line beats a name drawn over Join.
            name.horizontalOverflow = HorizontalWrapMode.Wrap;
            name.verticalOverflow = VerticalWrapMode.Truncate;
            var nrt = name.rectTransform; nrt.anchorMin = new Vector2(0, 0.5f); nrt.anchorMax = new Vector2(1, 1); nrt.offsetMin = new Vector2(16, 0);
            nrt.offsetMax = new Vector2(branch == null ? -124 : -196, -4);

            // Held outside the block so the Chat button below can move it. Without that it sat at [-190, -128],
            // entirely inside the Chat button's [-200, -116], and the button - built later, with an opaque fill -
            // painted the red MONO warning out on exactly the cards that offer Chat.
            RectTransform brt = null;
            if (branch != null)
            {
                bool joinable = string.Equals(row.Runtime, LobbyCoordinator.ThisRuntime, StringComparison.OrdinalIgnoreCase);
                var badge = UIFactory.Text("branch", branch, card.transform, Theme.Caption, TextAnchor.LowerRight, FontStyle.Bold);
                badge.color = joinable ? Theme.Success : Theme.DangerText;
                badge.raycastTarget = false; badge.horizontalOverflow = HorizontalWrapMode.Overflow;
                brt = badge.rectTransform;
                brt.anchorMin = new Vector2(1, 0.5f); brt.anchorMax = new Vector2(1, 1);
                brt.offsetMin = new Vector2(-190, 0); brt.offsetMax = new Vector2(-128, -4);
            }

            var subT = UIFactory.Text("sub", sub, card.transform, Theme.Caption, TextAnchor.UpperLeft);
            // Clip within the card (never draw under the Join button on the right): a long subtitle - e.g. a vanilla
            // lobby's "... · save 'Long Org Name' · synced-only · Locked" - would otherwise overflow across the button.
            subT.color = versionMismatch ? Theme.WarningText : Theme.TextMuted; subT.raycastTarget = false;
            subT.horizontalOverflow = HorizontalWrapMode.Wrap; subT.verticalOverflow = VerticalWrapMode.Truncate;
            var srt = subT.rectTransform; srt.anchorMin = new Vector2(0, 0); srt.anchorMax = new Vector2(1, 0.5f); srt.offsetMin = new Vector2(16, 4); srt.offsetMax = new Vector2(-124, 0);

            var (joinGO, joinBtn, _j) = UIFactory.ButtonWithLabel("Join", "Join", card.transform, Theme.Accent, 96, 40);
            var jrt = joinGO.GetComponent<RectTransform>();
            jrt.anchorMin = new Vector2(1, 0.5f); jrt.anchorMax = new Vector2(1, 0.5f); jrt.pivot = new Vector2(1, 0.5f);
            jrt.anchoredPosition = new Vector2(-12, 0); jrt.sizeDelta = new Vector2(96, 40);
            LobbyRow captured = row;
            joinBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onJoin?.Invoke(captured)));

            // Ask before committing. Shown only when this host advertises that they take messages (sh_msg) - a
            // button onto someone who drops every line would be worse than none - and it opens the same column the
            // sync screens carry, so the conversation survives walking into the join.
            if (ChatPanel.Possible(row.OwnerSteamId, row.AcceptsMessages))
            {
                var (chatGO, chatBtn, _ch) = UIFactory.ButtonWithLabel("Chat", "Chat", card.transform, Theme.Button, 84, 40);
                var crt = chatGO.GetComponent<RectTransform>();
                crt.anchorMin = new Vector2(1, 0.5f); crt.anchorMax = new Vector2(1, 0.5f); crt.pivot = new Vector2(1, 0.5f);
                crt.anchoredPosition = new Vector2(-116, 0); crt.sizeDelta = new Vector2(84, 40);
                chatBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
                    ChatPanel.Toggle(captured.OwnerSteamId, captured.HostName)));

                // Everything to the left of the two buttons moves with them: the two texts so a long lobby name
                // does not draw through, and the branch badge so the warning that decides whether Join can work
                // at all is not painted over by the button next to it.
                nrt.offsetMax = new Vector2(branch == null ? -208 : -280, -4);
                srt.offsetMax = new Vector2(-208, 0);
                if (brt != null) { brt.offsetMin = new Vector2(-274, 0); brt.offsetMax = new Vector2(-212, -4); }
            }

            Interactions.PolishButtons(card.transform);
        }

        // --- helpers ---

        /// <summary>The badge caption for a host's branch key, or null when the host never published one - an
        /// unlabelled card says "unknown", which is the truth, where a grey "?" badge would just add noise to every
        /// row hosted by an older build.</summary>
        private static string BranchLabel(string runtime) => (runtime ?? "").ToLowerInvariant() switch
        {
            "il2cpp" => "IL2CPP",
            "mono" => "MONO",
            _ => null,
        };

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
