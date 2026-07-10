using System;
using DooDesch.UI;
using Il2CppScheduleOne.Tools;
using S1API.UI;
using UnityEngine;
using UnityEngine.UI;

namespace SideHustle.Messenger
{
    /// <summary>Builds the Messenger's two screens (contact list, thread) into a body panel. Pure view code;
    /// navigation + sending stay in <see cref="MessengerApp"/>.</summary>
    internal static class MessengerScreens
    {
        internal static void BuildEmpty(Transform parent)
        {
            var t = UIFactory.Text("empty", "Chat is available while you're in a lobby.\n\nHost or join one from the\nmain menu (Side Hustle).",
                parent, Theme.Body, TextAnchor.MiddleCenter);
            t.color = Theme.TextMuted; t.raycastTarget = false;
            t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
            t.rectTransform.offsetMin = new Vector2(16, 16); t.rectTransform.offsetMax = new Vector2(-16, -16);
        }

        internal static void BuildContactList(Transform parent, Action<ulong> onOpen)
        {
            var content = Components.ScrollList(parent, out var scroll, 6f);
            SmoothScroll.Attach(scroll);

            Row(content, "Lobby chat", "Everyone in the lobby", ChatStore.Unread(ChatStore.GroupKey), () => onOpen(ChatStore.GroupKey));

            var contacts = Contacts.All;
            if (contacts.Count == 0)
                Note(content, "You're the only one here so far.");
            foreach (var c in contacts)
            {
                var contact = c;
                Row(content, contact.Name, "Private chat", ChatStore.Unread(contact.SteamId), () => onOpen(contact.SteamId));
            }
        }

        /// <summary>The thread view: a scrolling bubble list on top, a compose row (input + send) pinned at the
        /// bottom, and a back button in a slim top bar. Returns the compose InputField.</summary>
        internal static InputField BuildThread(Transform parent, ulong threadKey, Action onBack, Action<string> onSend)
        {
            const float BarH = 0.09f, ComposeH = 0.12f;

            var topBar = Band("thread_bar", parent, Theme.BgPanel, 1f - BarH, 1f);
            var (backGO, backBtn, _) = UIFactory.ButtonWithLabel("back", "< Back", topBar.transform, Theme.Button, 100f, 34f);
            var brt = backGO.GetComponent<RectTransform>();
            brt.anchorMin = new Vector2(0, 0.5f); brt.anchorMax = new Vector2(0, 0.5f); brt.pivot = new Vector2(0, 0.5f);
            brt.anchoredPosition = new Vector2(8f, 0f);
            backBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onBack?.Invoke()));

            var listBand = Band("thread_list", parent, Theme.BgBase, ComposeH, 1f - BarH);
            var content = Components.ScrollList(listBand.transform, out var scroll, 4f);
            SmoothScroll.Attach(scroll);

            var thread = ChatStore.Thread(threadKey);
            if (thread.Count == 0)
                Note(content, "No messages yet - say hi.");
            foreach (var m in thread)
                Bubble(content, m);

            // Compose row: an input field (with the game's typing lock) + a fixed-width Send button. The send
            // button is a fixed width (not a fraction) so it stays a comfortable size on the narrow portrait phone.
            const float SendW = 84f;
            var composeBand = Band("thread_compose", parent, Theme.BgPanel, 0f, ComposeH);
            var input = Components.TextInput(composeBand.transform, "", null, "message", ChatEnvelope.MaxTextChars);
            var irt = input.GetComponent<RectTransform>();
            irt.anchorMin = new Vector2(0, 0.5f); irt.anchorMax = new Vector2(1f, 0.5f); irt.pivot = new Vector2(0, 0.5f);
            irt.offsetMin = new Vector2(8, -18); irt.offsetMax = new Vector2(-(SendW + 16f), 18);
            try { input.gameObject.AddComponent<InputFieldAttachment>(); } catch { }   // sets GameInput.IsTyping while focused

            var (sendGO, sendBtn, _s) = UIFactory.ButtonWithLabel("send", "Send", composeBand.transform, Theme.Accent, 0, 0);
            var srt = sendGO.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(1f, 0.5f); srt.anchorMax = new Vector2(1f, 0.5f); srt.pivot = new Vector2(1f, 0.5f);
            srt.sizeDelta = new Vector2(SendW, 36f); srt.anchoredPosition = new Vector2(-8f, 0f);
            sendBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
            {
                string text = input.text;
                if (!string.IsNullOrWhiteSpace(text)) { onSend?.Invoke(text); input.text = ""; }
            }));

            // Enter in the field also sends.
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

        // --- primitives ---

        private static GameObject Band(string name, Transform parent, Color color, float y0, float y1)
        {
            var p = UIFactory.Panel(name, parent, color, new Vector2(0f, y0), new Vector2(1f, y1));
            var rt = p.GetComponent<RectTransform>();
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            return p;
        }

        private static void Row(RectTransform content, string title, string subtitle, int unread, Action onClick)
        {
            var row = UIFactory.Panel("row_" + title, content, Theme.BgElevated);
            var rle = row.AddComponent<LayoutElement>();
            rle.minHeight = 46f; rle.preferredHeight = 46f; rle.flexibleWidth = 1;

            var btn = row.AddComponent<Button>(); btn.targetGraphic = row.GetComponent<Image>();
            btn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onClick?.Invoke()));

            var t = UIFactory.Text("name", title, row.transform, Theme.H3, TextAnchor.MiddleLeft, FontStyle.Bold);
            t.raycastTarget = false;
            var trt = t.rectTransform; trt.anchorMin = new Vector2(0, 0); trt.anchorMax = new Vector2(0.62f, 1); trt.offsetMin = new Vector2(12, 0); trt.offsetMax = new Vector2(0, 0);

            var s = UIFactory.Text("sub", subtitle, row.transform, Theme.Body, TextAnchor.MiddleRight);
            s.color = Theme.TextMuted; s.raycastTarget = false;
            var srt = s.rectTransform; srt.anchorMin = new Vector2(0.55f, 0); srt.anchorMax = new Vector2(1, 1); srt.offsetMin = new Vector2(0, 0); srt.offsetMax = new Vector2(unread > 0 ? -46 : -12, 0);

            if (unread > 0)
            {
                var badge = UIFactory.Text("unread", unread.ToString(), row.transform, 13, TextAnchor.MiddleCenter, FontStyle.Bold);
                badge.color = Theme.TextPrimary; badge.raycastTarget = false;
                var pill = UIFactory.Panel("pill", row.transform, Theme.Accent);
                var pimg = pill.GetComponent<Image>(); if (pimg != null) { pimg.sprite = Theme.RoundedSprite(); pimg.type = Image.Type.Sliced; }
                var prt = pill.GetComponent<RectTransform>();
                prt.anchorMin = new Vector2(1, 0.5f); prt.anchorMax = new Vector2(1, 0.5f); prt.pivot = new Vector2(1, 0.5f);
                prt.anchoredPosition = new Vector2(-12, 0); prt.sizeDelta = new Vector2(26, 22);
                badge.transform.SetParent(pill.transform, false);
                var bmrt = badge.rectTransform; bmrt.anchorMin = Vector2.zero; bmrt.anchorMax = Vector2.one; bmrt.offsetMin = Vector2.zero; bmrt.offsetMax = Vector2.zero;
            }
        }

        // A chat bubble that HUGS its text (short replies stay small) up to a max width, then wraps. The card
        // is anchored to its side of the row (mine right, theirs left) with a width estimated from the text, so
        // the left/right cue stays strong. Height follows the estimated wrapped line count. Mine uses the darker
        // AccentPressed so white text keeps enough contrast.
        private const float BubbleMaxTextW = 230f;
        private const float BubbleCharW = 7.0f;

        private static void Bubble(RectTransform content, ChatMessage m)
        {
            string sender = (m.RecipientId == ChatStore.GroupKey && !m.Mine) ? Contacts.NameOf(m.SenderId) : null;
            string body = m.Text + (m.Pending ? "  ·" : "");

            var row = UIFactory.Panel("bubble", content, Theme.Clear);

            float oneLine = Mathf.Max(1, body.Length) * BubbleCharW;
            float textW = Mathf.Min(BubbleMaxTextW, oneLine);
            int lines = Mathf.Max(1, Mathf.CeilToInt(oneLine / Mathf.Max(1f, textW)));
            float cardH = (sender != null ? 15f : 0f) + 12f + lines * 17f;
            var rle = row.AddComponent<LayoutElement>();
            rle.minHeight = cardH + 4f; rle.preferredHeight = cardH + 4f; rle.flexibleWidth = 1;

            var card = UIFactory.Panel("card", row.transform, m.Mine ? Theme.AccentPressed : Theme.BgElevated);
            var cimg = card.GetComponent<Image>(); if (cimg != null) { cimg.sprite = Theme.RoundedSprite(); cimg.type = Image.Type.Sliced; }
            var crt = card.GetComponent<RectTransform>();
            crt.anchorMin = crt.anchorMax = new Vector2(m.Mine ? 1f : 0f, 0.5f);
            crt.pivot = new Vector2(m.Mine ? 1f : 0f, 0.5f);
            crt.sizeDelta = new Vector2(textW + 20f, cardH);
            crt.anchoredPosition = new Vector2(m.Mine ? -6f : 6f, 0f);

            float top = -6f;
            if (sender != null)
            {
                var name = UIFactory.Text("sender", sender, card.transform, Theme.Caption, TextAnchor.UpperLeft, FontStyle.Bold);
                name.color = m.Mine ? Theme.TextPrimary : Theme.AccentBorder; name.raycastTarget = false;
                var nrt = name.rectTransform; nrt.anchorMin = new Vector2(0, 1); nrt.anchorMax = new Vector2(1, 1); nrt.pivot = new Vector2(0.5f, 1);
                nrt.offsetMin = new Vector2(10, -21); nrt.offsetMax = new Vector2(-10, top);
                top = -21f;
            }
            var t = UIFactory.Text("text", body, card.transform, Theme.Body, TextAnchor.UpperLeft);
            t.color = Theme.TextPrimary; t.raycastTarget = false; t.horizontalOverflow = HorizontalWrapMode.Wrap;
            var trt = t.rectTransform; trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = new Vector2(10, 6); trt.offsetMax = new Vector2(-10, top);
        }

        private static void Note(RectTransform content, string text)
        {
            var row = UIFactory.Panel("note", content, Theme.Clear);
            var rle = row.AddComponent<LayoutElement>();
            rle.minHeight = 40f; rle.preferredHeight = 40f; rle.flexibleWidth = 1;
            var t = UIFactory.Text("text", text, row.transform, 13, TextAnchor.MiddleCenter);
            t.color = Theme.TextMuted; t.raycastTarget = false;
            var rt = t.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = new Vector2(12, 0); rt.offsetMax = new Vector2(-12, 0);
        }
    }
}
