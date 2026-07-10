using System;
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

        protected override string AppName => "SideHustleMessenger";
        protected override string AppTitle => "Messenger";
        protected override string IconLabel => "Messenger";
        protected override string IconFileName => "messenger.png";

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
        private Text _header;
        private bool _activated;
        private int _lastRevision = -1;
        private string _lastSig;

        // Navigation: null = the contact list; a value = the open thread key (GroupKey for the lobby chat).
        private ulong? _openThread;
        private InputField _composeField;

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

        private void BuildUI(GameObject container)
        {
            var root = UIFactory.Panel("msg_root", container.transform, Theme.BgBase, fullAnchor: true);
            root.GetComponent<RectTransform>().localScale = Vector3.one;
            _dialogRoot = root.transform;

            var safe = UIFactory.Panel("msg_safe", root.transform, Theme.Clear, fullAnchor: true);
            var safeRT = safe.GetComponent<RectTransform>();
            float cw = container.GetComponent<RectTransform>() != null ? container.GetComponent<RectTransform>().rect.width : 0f;
            if (cw > 1f) { float m = cw * 0.012f; safeRT.offsetMin = new Vector2(m, m); safeRT.offsetMax = new Vector2(-m, -m); }
            var host = safe.transform;

            var header = Band("msg_header", host, Theme.BgPanel, 0.9f, 1f);
            _header = UIFactory.Text("msg_title", "Messenger", header.transform, Theme.H3, TextAnchor.MiddleLeft, FontStyle.Bold);
            _header.color = Theme.Accent;
            var hrt = _header.rectTransform;
            hrt.anchorMin = new Vector2(0, 0); hrt.anchorMax = new Vector2(1, 1); hrt.offsetMin = new Vector2(12, 0); hrt.offsetMax = new Vector2(-12, 0);

            _contentRegion = Band("msg_content", host, Theme.BgBase, 0f, 0.9f);

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
                if (!IsOpen()) return;
                if (!_activated)
                {
                    _activated = true;
                    if (_container != null) { try { UIFactory.ClearChildren(_container.transform); BuildUI(_container); } catch { } }
                }
                ChatService.ActiveThread = _openThread ?? unchecked((ulong)0);   // group when on the list is harmless
                if (_openThread.HasValue) ChatStore.MarkRead(_openThread.Value);

                string sig = Signature();
                if (ChatStore.Revision != _lastRevision || sig != _lastSig)
                {
                    _lastRevision = ChatStore.Revision;
                    _lastSig = sig;
                    Rebuild();
                }
                SmoothScroll.Tick(PhoneCamera());
            }
            catch { }
        }

        private string Signature()
        {
            // Rebuild when the view target changes or the contact set changes (thread content is covered by Revision).
            return (_openThread?.ToString() ?? "list") + "|" + Contacts.All.Count + "|" + (ChatService.InLobby ? 1 : 0);
        }

        private void Rebuild()
        {
            if (_contentRegion == null) return;
            SmoothScroll.Clear();
            var prev = _body;
            _body = UIFactory.Panel("msg_body", _contentRegion.transform, Theme.BgBase, fullAnchor: true);

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
                _composeField = MessengerScreens.BuildThread(_body.transform, _openThread.Value,
                    onBack: () => { _openThread = null; _composeField = null; _lastSig = null; Rebuild(); },
                    onSend: text => { ChatService.Send(_openThread.Value, text); });
                if (_header != null) _header.text = _openThread.Value == ChatStore.GroupKey ? "Lobby chat" : Contacts.NameOf(_openThread.Value);
            }
            else
            {
                MessengerScreens.BuildContactList(_body.transform, key => { _openThread = key; _lastSig = null; Rebuild(); });
                if (_header != null) _header.text = "Messenger";
            }

            if (prev != null) UnityEngine.Object.Destroy(prev);
        }

        private static Camera PhoneCamera()
        {
            try { var gm = Singleton<GameplayMenu>.Instance; return gm != null ? gm.OverlayCamera : null; }
            catch { return null; }
        }
    }
}
