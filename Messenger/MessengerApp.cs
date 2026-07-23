using System;
using System.Collections.Generic;
using DooDesch.UI;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;
using Il2CppScheduleOne.UI.Phone;
using S1API.PhoneApp;
using S1API.UI;
using UnityEngine;
using UnityEngine.UI;

namespace SideHustle.Messenger
{
    /// <summary>
    /// The Side Hustle Messenger phone app: chat with the other players in your lobby. S1API auto-discovers and
    /// instantiates PhoneApp subclasses on the gameplay-scene home screen, so this needs only a parameterless
    /// ctor. It is a thin frontend over <see cref="ChatService"/>: a contact list (group + each member, with
    /// unread badges) and a thread view (bubbles + compose). Modelled on PropHunt's phone app, incl. the
    /// build-while-inactive rebuild-once workaround and the worldspace SmoothScroll camera.
    /// </summary>
    public class MessengerApp : PhoneApp
    {
        internal static MessengerApp Instance { get; private set; }

        protected override string AppName => "SideHustleMessenger";   // stable S1API app id - not user-facing
        protected override string AppTitle => "WhatsDab";
        protected override string IconLabel => "WhatsDab";
        protected override string IconFileName => "messenger.png";

        // A chat reads far better tall than wide, so the messenger runs portrait.
        protected override EOrientation Orientation => EOrientation.Vertical;

        private static Sprite _icon;
        private static bool _iconAbsent;
        protected override Sprite IconSprite
        {
            get
            {
                if (_icon != null) return _icon;
                if (_iconAbsent) return null;
                _icon = LoadEmbeddedIcon();
                if (_icon == null) _iconAbsent = true;
                return _icon;
            }
        }

        private static Sprite LoadEmbeddedIcon()
        {
            try
            {
                using var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("SideHustle.Assets.messenger_icon.png");
                if (s == null) return null;
                var bytes = new byte[s.Length];
                int read = 0;
                while (read < bytes.Length) { int n = s.Read(bytes, read, bytes.Length - read); if (n <= 0) break; read += n; }
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
                tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                if (!tex.LoadImage(bytes)) return null;
                var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                if (sp != null) sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
                return sp;
            }
            catch { return null; }
        }

        private GameObject _container;
        private GameObject _contentRegion;
        private GameObject _body;
        private Transform _dialogRoot;
        private bool _activated;
        private int _lastRevision = -1;
        private string _lastSig;

        // Navigation: null = the contact list; a value = the open thread key (GroupKey for the lobby chat).
        private ulong? _openThread;
        private InputField _composeField;

        // The open thread's bubble list + its scroll, kept so an incoming message can refresh ONLY the bubbles
        // (leaving the compose draft/focus and scroll position intact) instead of rebuilding the whole thread.
        private RectTransform _bubbleContent;
        private ScrollRect _bubbleScroll;
        private ulong? _shownThread;                                       // the thread whose bubbles are rendered now
        private readonly Dictionary<ulong, float> _scrollPos = new Dictionary<ulong, float>();   // per-thread, for reopen

        // The unread-count badge on the home-screen app icon (the red bubble the vanilla Messages app shows). Found
        // and cached lazily; updated every frame regardless of whether the app is open.
        private Transform _iconNotif;
        private Text _iconNotifText;
        private int _lastBadge = -1;

        // The Messenger icon sprite, so the incoming-message toast can carry it like the vanilla app's alerts.
        internal static Sprite NotifIcon => _icon;

        protected override void OnCreatedUI(GameObject container)
        {
            Instance = this;
            _container = container;
            try { BuildUI(container); }
            catch (Exception e) { Core.Log?.Warning("[messenger] app build failed: " + e.Message); }
        }

#if DEBUG
        // Render seeded contacts/threads even without a real lobby (solo scratch world used for screenshots).
        internal static bool ForceRenderForTest;
        internal void OpenListForTest() { ForceRenderForTest = true; _openThread = null; _lastSig = null; OpenApp(); Rebuild(); }
        internal void OpenThreadForTest(ulong key) { ForceRenderForTest = true; _openThread = key; _lastSig = null; OpenApp(); Rebuild(); }
#endif

        private static GameObject Band(string name, Transform parent, Color color, float y0, float y1)
        {
            var p = UIFactory.Panel(name, parent, color, new Vector2(0f, y0), new Vector2(1f, y1));
            var rt = p.GetComponent<RectTransform>();
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            return p;
        }

        // Portrait, modelled on the WeatherApp reference mod. That mod parents a fresh panel to the AppsCanvas and
        // gives it localRotation Euler(0,0,90) - a 90-degree roll RELATIVE TO the canvas - plus a portrait rect. A
        // canvas-relative roll tracks the phone screen as the player turns (a fixed world rotation does not, which is
        // why an identity rotation mirrored). S1API hands us a container nested inside a cloned LANDSCAPE panel, so we
        // solve for the local rotation that lands our root at 90 degrees relative to the canvas, cancelling the
        // template's baked roll through the parent chain, and give it the portrait rect (the swap of the landscape
        // container). Result matches WeatherApp: upright portrait content that stays correct from any facing.
        private void OrientPortrait(GameObject container, RectTransform rootRT)
        {
            try
            {
                if (Phone.InstanceExists && Phone.Instance.isHorizontal) Phone.Instance.SetIsHorizontal(false);
                Canvas.ForceUpdateCanvases();

                rootRT.anchorMin = rootRT.anchorMax = new Vector2(0.5f, 0.5f);
                rootRT.pivot = new Vector2(0.5f, 0.5f);
                rootRT.anchoredPosition = Vector2.zero;
                rootRT.localScale = Vector3.one;

                var crt = container.GetComponent<RectTransform>();
                var r = crt != null ? crt.rect : new Rect(0f, 0f, 1201f, 655f);
                rootRT.sizeDelta = (r.width > 1f && r.height > 1f) ? new Vector2(r.height, r.width) : new Vector2(655f, 1201f);

                if (AppsCanvas.InstanceExists)
                {
                    var canvas = AppsCanvas.Instance.transform;
                    rootRT.localRotation = Quaternion.Inverse(container.transform.rotation) * canvas.rotation * Quaternion.Euler(0f, 0f, 90f);
                }
            }
            catch (Exception e) { Core.Log?.Warning("[messenger] orient failed: " + e.Message); }
        }

        private void BuildUI(GameObject container)
        {
            var root = UIFactory.Panel("msg_root", container.transform, WDTheme.Screen, fullAnchor: true);
            var rootRT = root.GetComponent<RectTransform>();
            rootRT.localScale = Vector3.one;
            OrientPortrait(container, rootRT);
            _dialogRoot = root.transform;

            // The full screen belongs to the active view: each screen (list / thread) builds its own header,
            // content and compose bar, so the layout reads like a real portrait phone app rather than a menu panel.
            _contentRegion = UIFactory.Panel("msg_content", root.transform, WDTheme.Screen, fullAnchor: true);

            Rebuild();

            try
            {
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(root.GetComponent<RectTransform>());
                var texts = root.GetComponentsInChildren<Text>(true);
                if (texts != null) foreach (var tx in texts) if (tx != null) tx.SetAllDirty();
            }
            catch { }
        }

        internal void Tick()
        {
            try
            {
                // Only the thread you are actually viewing counts as read. Closed, or on the contact list, means no
                // active thread - so every incoming message, the lobby chat included, counts as unread and alerts.
                ChatService.ActiveThread = (IsOpen() && _openThread.HasValue) ? _openThread.Value : ChatStore.NoThread;
                UpdateBadge();                 // the icon badge must track unread even while the app is closed
                if (!IsOpen()) return;
                if (!_activated)
                {
                    _activated = true;
                    if (_container != null) { try { UIFactory.ClearChildren(_container.transform); BuildUI(_container); } catch { } }
                }
                if (_openThread.HasValue) ChatStore.MarkRead(_openThread.Value);

                string sig = Signature();
                bool viewChanged = sig != _lastSig;
                bool contentChanged = ChatStore.Revision != _lastRevision;
                if (viewChanged)
                {
                    _lastSig = sig;
                    _lastRevision = ChatStore.Revision;
                    Rebuild();
                }
                else if (contentChanged)
                {
                    _lastRevision = ChatStore.Revision;
                    // Same view, new messages: refresh ONLY the bubbles so the compose draft/focus + scroll survive.
                    if (_openThread.HasValue && _shownThread == _openThread && _bubbleContent != null) UpdateBubbles();
                    else Rebuild();
                }

                // Track the open thread's live scroll position so reopening it returns to the same place.
                try { if (_shownThread.HasValue && _bubbleScroll != null) _scrollPos[_shownThread.Value] = _bubbleScroll.verticalNormalizedPosition; } catch { }

                SmoothScroll.Tick(PhoneCamera());
            }
            catch { }
        }

        private string Signature()
        {
            // Rebuild when the view target changes or the contact set changes (thread content is covered by Revision).
            return (_openThread?.ToString() ?? "list") + "|" + Contacts.All.Count + "|" + (ChatService.InLobby ? 1 : 0);
        }

        // Mirror the vanilla App.SetNotificationCount: write the total unread count into the icon's Notifications/Text
        // bubble and show/hide it. The badge child comes free with S1API's cloned app icon (every home-screen icon
        // carries a "Notifications/Text" object). Only touches the UI when the count actually changes.
        private void UpdateBadge()
        {
            try
            {
                int total = ChatStore.TotalUnread();
                if (total == _lastBadge) return;
                if (_iconNotif == null && !FindIconBadge()) return;   // icon not spawned yet - retry next frame
                _lastBadge = total;
                if (_iconNotifText != null) _iconNotifText.text = total > 99 ? "99+" : total.ToString();
                if (_iconNotif != null) _iconNotif.gameObject.SetActive(total > 0);
            }
            catch { }
        }

        private bool FindIconBadge()
        {
            try
            {
                if (!HomeScreen.InstanceExists) return false;
                var icon = FindDescendant(HomeScreen.Instance.transform, AppName);   // S1API renamed our icon to AppName
                if (icon == null) return false;
                var notif = icon.Find("Notifications");
                if (notif == null) return false;
                _iconNotif = notif;
                var txt = notif.Find("Text");
                _iconNotifText = txt != null ? txt.GetComponent<Text>() : null;
                return true;
            }
            catch { return false; }
        }

        private static Transform FindDescendant(Transform root, string name)
        {
            if (root == null) return null;
            var all = root.GetComponentsInChildren<Transform>(true);
            if (all != null) foreach (var t in all) if (t != null && t.name == name) return t;
            return null;
        }

        private void Rebuild()
        {
            if (_contentRegion == null) return;
            // A rebuild (a new incoming message bumps the Revision) recreates the compose input, which would wipe the
            // message the player is mid-typing. Carry the draft (and, if it was focused, the caret + focus) across.
            string draft = _composeField != null ? _composeField.text : null;
            bool wasFocused = false;
            try { wasFocused = _composeField != null && _composeField.isFocused; } catch { }
            SmoothScroll.Clear();
            var prev = _body;
            _body = UIFactory.Panel("msg_body", _contentRegion.transform, WDTheme.Screen, fullAnchor: true);
            _bubbleContent = null; _bubbleScroll = null; _shownThread = null;

            bool render = ChatService.InLobby;
#if DEBUG
            render = render || ForceRenderForTest;
#endif
            if (!render)
            {
                _openThread = null;
                MessengerScreens.BuildEmpty(_body.transform);
            }
            else if (_openThread.HasValue)
            {
                ulong thread = _openThread.Value;
                _composeField = MessengerScreens.BuildThread(_body.transform, thread,
                    onBack: () => { _openThread = null; _composeField = null; _bubbleContent = null; _bubbleScroll = null; _shownThread = null; _lastSig = null; Rebuild(); },
                    onSend: text => { ChatService.Send(thread, text); },
                    out _bubbleContent, out _bubbleScroll);
                _shownThread = thread;
                if (_composeField != null && !string.IsNullOrEmpty(draft))
                {
                    _composeField.text = draft;
                    try { _composeField.caretPosition = draft.Length; } catch { }
                    if (wasFocused) try { _composeField.ActivateInputField(); } catch { }
                }
                RestoreScroll(thread);   // reopen returns to the remembered spot; a fresh thread starts at the bottom
            }
            else
            {
                MessengerScreens.BuildContactList(_body.transform, key => { _openThread = key; _lastSig = null; Rebuild(); });
            }

            if (prev != null) UnityEngine.Object.Destroy(prev);
        }

        // Same thread, new message(s): refresh the bubble list only (compose field + scroll untouched). Follow to the
        // newest message only if the player was already at the bottom; otherwise leave their position (reading history).
        private void UpdateBubbles()
        {
            try
            {
                bool atBottom = _bubbleScroll == null || _bubbleScroll.verticalNormalizedPosition <= 0.05f;
                MessengerScreens.FillBubbles(_bubbleContent, _openThread.Value);
                if (atBottom) ScrollToBottom();
            }
            catch { }
        }

        private void ScrollToBottom()
        {
            if (_bubbleScroll == null || _bubbleContent == null) return;
            try
            {
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(_bubbleContent);
                _bubbleScroll.verticalNormalizedPosition = 0f;   // 0 = bottom (newest) for the top-anchored list
            }
            catch { }
        }

        private void RestoreScroll(ulong thread)
        {
            if (_bubbleScroll == null || _bubbleContent == null) return;
            try
            {
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(_bubbleContent);
                _bubbleScroll.verticalNormalizedPosition = _scrollPos.TryGetValue(thread, out var p) ? p : 0f;
            }
            catch { }
        }

        private static Camera PhoneCamera()
        {
            try { var gm = Singleton<GameplayMenu>.Instance; return gm != null ? gm.OverlayCamera : null; }
            catch { return null; }
        }
    }
}
