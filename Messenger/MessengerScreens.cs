using System;
using DooDesch.UI;
using Il2CppScheduleOne.Tools;
using S1API.UI;
using UnityEngine;
using UnityEngine.UI;

namespace SideHustle.Messenger
{
    /// <summary>
    /// WhatsDab's two screens (contact list, thread) drawn into a body panel. Styled like the phone's native apps
    /// (light, flat rows, hairline separators, round avatars) with the app's own green accent - see
    /// <see cref="WDTheme"/> - rather than the dark menu design system, so it reads as a real phone app. Pure view
    /// code; navigation and sending live in <see cref="MessengerApp"/>.
    /// </summary>
    internal static class MessengerScreens
    {
        // Type scale for the ~655px-wide portrait screen, matched to the NATIVE apps' proportions (the vanilla
        // Messages list renders its title at ~8% of the screen width and row names at ~5%) - phone type, not menu type.
        private const int FTitle = 44;
        private const int FBack = 44;
        private const int FRowName = 34;
        private const int FRowSub = 26;
        private const int FBubble = 32;
        private const int FSender = 24;
        private const int FBadge = 24;
        private const int FInitial = 32;
        private const int FCompose = 32;
        private const int FSend = 28;
        private const int FEmpty = 28;

        private const float HeaderH = 0.096f;   // header band, fraction of screen height (~115px)
        private const float ComposeH = 0.105f;  // compose band on the thread (~126px)
        private const float RowH = 144f;
        private const float Avatar = 68f;

        internal static void BuildEmpty(Transform parent)
        {
            Header(parent, "WhatsDab", null);
            var body = Band("empty_body", parent, WDTheme.Screen, 0f, 1f - HeaderH);
            var t = UIFactory.Text("empty", "Chat is available while you're in a lobby.\n\nHost or join one from the main menu (Side Hustle).",
                body.transform, FEmpty, TextAnchor.MiddleCenter);
            t.color = WDTheme.TextGray; t.raycastTarget = false; t.lineSpacing = 1.25f;
            Fill(t.rectTransform, 28f, 28f);
        }

        internal static void BuildContactList(Transform parent, Action<ulong> onOpen)
        {
            Header(parent, "WhatsDab", null);

            var listBand = Band("list", parent, WDTheme.Screen, 0f, 1f - HeaderH);
            var content = Components.ScrollList(listBand.transform, out var scroll, 0f);
            SmoothScroll.Attach(scroll);

            Row(content, "Lobby chat", Preview(ChatStore.GroupKey, "Everyone in the lobby"), ChatStore.Unread(ChatStore.GroupKey), true, () => onOpen(ChatStore.GroupKey));

            var contacts = Contacts.All;
            if (contacts.Count == 0)
                Note(content, "You're the only one here so far.");
            foreach (var c in contacts)
            {
                var contact = c;
                Row(content, contact.Name, Preview(contact.SteamId, "Private chat"), ChatStore.Unread(contact.SteamId), false, () => onOpen(contact.SteamId));
            }
        }

        /// <summary>The thread view: a header with a back button and the contact's avatar, a scrolling bubble list
        /// on the paper-tone background, and a compose bar pinned at the bottom. Returns the compose InputField.</summary>
        internal static InputField BuildThread(Transform parent, ulong threadKey, Action onBack, Action<string> onSend,
            out RectTransform bubbleContent, out ScrollRect bubbleScroll)
        {
            bool group = threadKey == ChatStore.GroupKey;
            string title = group ? "Lobby chat" : Contacts.NameOf(threadKey);
            Header(parent, title, onBack, group);

            var listBand = Band("thread_list", parent, WDTheme.ChatBg, ComposeH, 1f - HeaderH);
            bubbleContent = Components.ScrollList(listBand.transform, out bubbleScroll, 6f);
            SmoothScroll.Attach(bubbleScroll);
            FillBubbles(bubbleContent, threadKey);

            // Compose bar: a tall white rounded input (with the game's typing lock) plus a green Send button. The
            // input's own RectMask2D is dropped: its clipping is axis-aligned and blanks the text inside the rotated
            // portrait panel.
            var compose = Band("compose", parent, WDTheme.ComposeBar, 0f, ComposeH);
            const float SendW = 136f;
            var input = Components.TextInput(compose.transform, "", null, "Message", ChatEnvelope.MaxTextChars);
            var mask = input.GetComponent<RectMask2D>(); if (mask != null) UnityEngine.Object.Destroy(mask);
            var ibg = input.GetComponent<Image>(); if (ibg != null) ibg.color = WDTheme.InputBg;
            var irt = input.GetComponent<RectTransform>();
            irt.anchorMin = new Vector2(0, 0.5f); irt.anchorMax = new Vector2(1f, 0.5f); irt.pivot = new Vector2(0, 0.5f);
            irt.offsetMin = new Vector2(18, -46); irt.offsetMax = new Vector2(-(SendW + 26f), 46);   // 92px tall
            var itxt = input.textComponent; if (itxt != null) { itxt.fontSize = FCompose; itxt.color = WDTheme.TextDark; }
            var iph = input.placeholder as Text; if (iph != null) { iph.fontSize = FCompose; iph.color = WDTheme.TextGray; }
            input.caretColor = WDTheme.GreenDark; input.customCaretColor = true; input.caretWidth = 3;
            try { input.gameObject.AddComponent<InputFieldAttachment>(); } catch { }

            var (sendGO, sendBtn, sendLbl) = UIFactory.ButtonWithLabel("send", "Send", compose.transform, WDTheme.Green, SendW, 80f);
            if (sendLbl != null) { sendLbl.fontSize = FSend; sendLbl.fontStyle = FontStyle.Bold; sendLbl.color = Color.white; }
            var srt = sendGO.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(1f, 0.5f); srt.anchorMax = new Vector2(1f, 0.5f); srt.pivot = new Vector2(1f, 0.5f);
            srt.anchoredPosition = new Vector2(-18f, 0f);
            Round(sendGO.GetComponent<Image>());
            sendBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
            {
                string text = input.text;
                if (!string.IsNullOrWhiteSpace(text)) { onSend?.Invoke(text); input.text = ""; }
            }));

            input.onEndEdit.AddListener((UnityEngine.Events.UnityAction<string>)(v =>
            {
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    if (!string.IsNullOrWhiteSpace(v)) { onSend?.Invoke(v); input.text = ""; }
                }
            }));

            Interactions.PolishButtons(parent);
            return input;
        }

        /// <summary>(Re)fill a thread's bubble list. Separate from BuildThread so an incoming message can refresh ONLY
        /// the bubbles, leaving the compose field (and the player's draft + focus) and the scroll rect untouched.</summary>
        internal static void FillBubbles(RectTransform content, ulong threadKey)
        {
            if (content == null) return;
            bool group = threadKey == ChatStore.GroupKey;
            UIFactory.ClearChildren(content);
            var thread = ChatStore.Thread(threadKey);
            if (thread.Count == 0) Note(content, "No messages yet - say hi.");
            foreach (var m in thread) Bubble(content, m, group);
        }

        // --- header ---

        // The dark-green top bar: an optional back button, the contact's round avatar on threads, then the title.
        private static void Header(Transform parent, string title, Action onBack, bool group = false)
        {
            var bar = Band("header", parent, WDTheme.Header, 1f - HeaderH, 1f);
            float left = 24f;

            if (onBack != null)
            {
                var (backGO, backBtn, backLbl) = UIFactory.ButtonWithLabel("back", "<", bar.transform, WDTheme.HeaderLit, 64f, 64f);
                if (backLbl != null) { backLbl.fontSize = FBack; backLbl.fontStyle = FontStyle.Bold; backLbl.color = WDTheme.HeaderText; }
                Round(backGO.GetComponent<Image>());
                var brt = backGO.GetComponent<RectTransform>();
                brt.anchorMin = new Vector2(0, 0.5f); brt.anchorMax = new Vector2(0, 0.5f); brt.pivot = new Vector2(0, 0.5f);
                brt.anchoredPosition = new Vector2(16f, 0f);
                backBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onBack.Invoke()));
                left = 96f;

                // The contact's avatar next to the name, like a real chat app's thread header.
                var av = Circle("avatar", bar.transform, group ? WDTheme.GreenDark : WDTheme.PersonColor(title), 54f);
                var art = av.GetComponent<RectTransform>();
                art.anchorMin = new Vector2(0, 0.5f); art.anchorMax = new Vector2(0, 0.5f); art.pivot = new Vector2(0, 0.5f);
                art.anchoredPosition = new Vector2(left, 0f);
                var init = UIFactory.Text("init", Initial(title), av.transform, 24, TextAnchor.MiddleCenter, FontStyle.Bold);
                init.color = Color.white; init.raycastTarget = false; Fill(init.rectTransform, 0, 0);
                left += 54f + 14f;

                Interactions.PolishButtons(bar.transform);
            }

            var t = UIFactory.Text("title", title, bar.transform, FTitle, TextAnchor.MiddleLeft, FontStyle.Bold);
            t.color = WDTheme.HeaderText; t.raycastTarget = false;
            var trt = t.rectTransform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = new Vector2(left, 0); trt.offsetMax = new Vector2(-20, 0);
        }

        // --- contact row ---

        // A flat native-style row: round avatar, bold name, a two-line gray preview underneath, a hairline separator,
        // and a green unread badge on the right (name and preview darken/bold while unread).
        private static void Row(RectTransform content, string title, string subtitle, int unread, bool isGroup, Action onClick)
        {
            var row = UIFactory.Panel("row_" + title, content, WDTheme.Screen);
            var rle = row.AddComponent<LayoutElement>();
            rle.minHeight = RowH; rle.preferredHeight = RowH; rle.flexibleWidth = 1;

            var btn = row.AddComponent<Button>(); btn.targetGraphic = row.GetComponent<Image>();
            btn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onClick?.Invoke()));

            var av = Circle("avatar", row.transform, isGroup ? WDTheme.GreenDark : WDTheme.PersonColor(title), Avatar);
            var art = av.GetComponent<RectTransform>();
            art.anchorMin = new Vector2(0, 0.5f); art.anchorMax = new Vector2(0, 0.5f); art.pivot = new Vector2(0, 0.5f);
            art.anchoredPosition = new Vector2(18f, 0f);
            var init = UIFactory.Text("init", Initial(title), av.transform, FInitial, TextAnchor.MiddleCenter, FontStyle.Bold);
            init.color = Color.white; init.raycastTarget = false; Fill(init.rectTransform, 0, 0);

            float textLeft = 18f + Avatar + 16f;
            float textRight = unread > 0 ? 84f : 24f;

            // The name sits bottom-aligned just above the row's centre line and the preview top-aligned just below
            // it, so the two-line block reads vertically centred in the row (instead of hugging the top edge).
            var name = UIFactory.Text("name", title, row.transform, FRowName, TextAnchor.LowerLeft, FontStyle.Bold);
            name.color = WDTheme.TextDark; name.raycastTarget = false; name.horizontalOverflow = HorizontalWrapMode.Overflow;
            var nrt = name.rectTransform; nrt.anchorMin = new Vector2(0, 0.5f); nrt.anchorMax = new Vector2(1, 1); nrt.pivot = new Vector2(0, 0.5f);
            nrt.offsetMin = new Vector2(textLeft, 2f); nrt.offsetMax = new Vector2(-textRight, -10f);

            var sub = UIFactory.Text("sub", subtitle, row.transform, FRowSub, TextAnchor.UpperLeft,
                unread > 0 ? FontStyle.Bold : FontStyle.Normal);
            sub.color = unread > 0 ? WDTheme.TextDark : WDTheme.TextGray; sub.raycastTarget = false;
            sub.horizontalOverflow = HorizontalWrapMode.Wrap; sub.verticalOverflow = VerticalWrapMode.Truncate;
            var srt = sub.rectTransform; srt.anchorMin = new Vector2(0, 0); srt.anchorMax = new Vector2(1, 0.5f); srt.pivot = new Vector2(0, 1);
            srt.offsetMin = new Vector2(textLeft, 10f); srt.offsetMax = new Vector2(-textRight, -2f);

            if (unread > 0)
            {
                var pill = Circle("badge", row.transform, WDTheme.Green, 44f);
                var prt = pill.GetComponent<RectTransform>();
                prt.anchorMin = new Vector2(1, 0.5f); prt.anchorMax = new Vector2(1, 0.5f); prt.pivot = new Vector2(1, 0.5f);
                prt.anchoredPosition = new Vector2(-20, 0);
                if (unread > 9) prt.sizeDelta = new Vector2(58f, 44f);
                var badge = UIFactory.Text("unread", unread > 99 ? "99+" : unread.ToString(), pill.transform, FBadge, TextAnchor.MiddleCenter, FontStyle.Bold);
                badge.color = Color.white; badge.raycastTarget = false; Fill(badge.rectTransform, 0, 0);
            }

            // Hairline separator starting after the avatar, like the native list.
            var line = UIFactory.Panel("hairline", row.transform, WDTheme.Hairline);
            var lrt = line.GetComponent<RectTransform>();
            lrt.anchorMin = new Vector2(0, 0); lrt.anchorMax = new Vector2(1, 0); lrt.pivot = new Vector2(0.5f, 0);
            lrt.offsetMin = new Vector2(textLeft, 0); lrt.offsetMax = new Vector2(0, 2f);
        }

        // --- chat bubble ---

        // A bubble that hugs its text up to a max width, then wraps. The width comes from the text's MEASURED
        // preferred width, not a length*char-width guess - the guess sized the bubble too narrow, so the text
        // wrapped while the height still counted one line and the second line was clipped.
        private const float BubbleMaxW = 520f;
        private const float BubblePadX = 22f;
        private const float BubblePadY = 16f;
        private const float BubbleCharW = 18f;    // fallback estimate at FBubble if the text can't be measured yet
        private const float BubbleLineH = 42f;
        private const float SenderH = 32f;

        private static void Bubble(RectTransform content, ChatMessage m, bool group)
        {
            string sender = (group && !m.Mine) ? Contacts.NameOf(m.SenderId) : null;
            string body = m.Text + (m.Pending ? "  ·" : "");

            var row = UIFactory.Panel("bubble", content, DooDesch.UI.Theme.Clear);
            var rle = row.AddComponent<LayoutElement>();

            var card = UIFactory.Panel("card", row.transform, m.Mine ? WDTheme.MineBubble : WDTheme.TheirBubble);
            Round(card.GetComponent<Image>());
            var crt = card.GetComponent<RectTransform>();

            var t = UIFactory.Text("text", body, card.transform, FBubble, TextAnchor.UpperLeft);
            t.color = WDTheme.TextDark; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Overflow;

            // The sender name (group chats) is created first so we can measure it: the bubble must be at least as
            // wide as the name, or a message SHORTER than the name would size the bubble too narrow and wrap the NAME.
            Text nameT = null;
            float senderW = 0f;
            if (sender != null)
            {
                nameT = UIFactory.Text("sender", ClampSender(sender), card.transform, FSender, TextAnchor.UpperLeft, FontStyle.Bold);
                nameT.color = WDTheme.PersonColor(sender); nameT.raycastTarget = false;   // colour from the full name (stable)
                nameT.horizontalOverflow = HorizontalWrapMode.Overflow;   // one line; the card is sized to fit it
                senderW = nameT.preferredWidth;
            }

            float maxTextW = BubbleMaxW - 2f * BubblePadX;
            float msgPref = t.preferredWidth;
            if (msgPref < 1f) msgPref = Mathf.Max(1, body.Length) * BubbleCharW;   // fallback if not measured yet
            float contentW = Mathf.Max(msgPref, senderW);                          // never narrower than the sender name
            float textAreaW = Mathf.Clamp(contentW + 4f, 40f, maxTextW);           // +4 so single-line text never re-wraps
            int lines = Mathf.Max(1, Mathf.CeilToInt(msgPref / Mathf.Max(1f, textAreaW)));   // line count from the MESSAGE only
            float senderH = sender != null ? SenderH : 0f;
            float cardH = senderH + 2f * BubblePadY + lines * BubbleLineH;

            rle.minHeight = cardH + 8f; rle.preferredHeight = cardH + 8f; rle.flexibleWidth = 1;

            crt.anchorMin = crt.anchorMax = new Vector2(m.Mine ? 1f : 0f, 0.5f);
            crt.pivot = new Vector2(m.Mine ? 1f : 0f, 0.5f);
            crt.sizeDelta = new Vector2(textAreaW + 2f * BubblePadX, cardH);
            crt.anchoredPosition = new Vector2(m.Mine ? -10f : 10f, 0f);

            var trt = t.rectTransform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(BubblePadX, BubblePadY);
            trt.offsetMax = new Vector2(-BubblePadX, -(BubblePadY + senderH));

            if (nameT != null)
            {
                var nrt = nameT.rectTransform; nrt.anchorMin = new Vector2(0, 1); nrt.anchorMax = new Vector2(1, 1); nrt.pivot = new Vector2(0.5f, 1);
                nrt.offsetMin = new Vector2(BubblePadX, -(senderH + BubblePadY)); nrt.offsetMax = new Vector2(-BubblePadX, -BubblePadY);
            }
        }

        // Cap an over-long sender name so it can't outrun the bubble's max width. Aliases already cap at 24 chars, so
        // this only bites an unusually long or wide (e.g. CJK) Steam persona name that reaches the bubble unclamped.
        private static string ClampSender(string s) =>
            string.IsNullOrEmpty(s) || s.Length <= 24 ? s : s.Substring(0, 23).TrimEnd() + "…";

        // --- primitives ---

        private static GameObject Band(string name, Transform parent, Color color, float y0, float y1)
        {
            var p = UIFactory.Panel(name, parent, color, new Vector2(0f, y0), new Vector2(1f, y1));
            var rt = p.GetComponent<RectTransform>();
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            return p;
        }

        private static GameObject Circle(string name, Transform parent, Color color, float size)
        {
            var go = UIFactory.Panel(name, parent, color);
            var img = go.GetComponent<Image>();
            if (img != null) { img.sprite = WDTheme.CircleSprite(); img.type = Image.Type.Simple; img.preserveAspect = true; }
            go.GetComponent<RectTransform>().sizeDelta = new Vector2(size, size);
            return go;
        }

        private static void Note(RectTransform content, string text)
        {
            var row = UIFactory.Panel("note", content, DooDesch.UI.Theme.Clear);
            var rle = row.AddComponent<LayoutElement>();
            rle.minHeight = 56f; rle.preferredHeight = 56f; rle.flexibleWidth = 1;
            var t = UIFactory.Text("text", text, row.transform, FRowSub, TextAnchor.MiddleCenter);
            t.color = WDTheme.TextGray; t.raycastTarget = false;
            Fill(t.GetComponent<RectTransform>(), 16, 16);
        }

        private static void Fill(RectTransform rt, float xInset, float yInset)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(xInset, yInset); rt.offsetMax = new Vector2(-xInset, -yInset);
        }

        private static void Round(Image img)
        {
            if (img == null) return;
            var sp = DooDesch.UI.Theme.RoundedSprite();
            if (sp != null) { img.sprite = sp; img.type = Image.Type.Sliced; }
        }

        // The last message as a one-line preview (like the native Messages app), or a fallback when the chat is empty.
        private static string Preview(ulong key, string fallback)
        {
            var thread = ChatStore.Thread(key);
            if (thread != null && thread.Count > 0)
            {
                var last = thread[thread.Count - 1];
                return (last.Mine ? "You: " : "") + (last.Text ?? "");
            }
            return fallback;
        }

        private static string Initial(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "?";
            name = name.Trim();
            return name.Substring(0, 1).ToUpperInvariant();
        }
    }
}
