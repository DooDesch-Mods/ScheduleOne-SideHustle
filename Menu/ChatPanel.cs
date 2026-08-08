using System;
using Sideload.Api;
using SideHustle.Config;
using SideHustle.Phone;
using UnityEngine;

namespace SideHustle.Menu
{
    /// <summary>
    /// The conversation with one host, as a column down the right of whatever menu screen is open.
    ///
    /// It replaces a button that opened a one-shot prompt. That button could send a line and nothing else: no
    /// history, no answer, no sign the host had read it - which is the wrong shape for the moment it exists for.
    /// Someone stuck on "Still missing" is not filing a report, they are having a short conversation about whether
    /// they can get in at all, and that needs both directions on screen at once.
    ///
    /// The right side of a menu SUBSCREEN is free - vanilla's community-vote card only sits on the home screen -
    /// so the panel gets the full column height there rather than the narrow band the state panel uses.
    ///
    /// Mounted only when the host advertises that they take messages (<c>sh_msg</c>). A panel onto a host who
    /// drops every line would be worse than none.
    /// </summary>
    internal static class ChatPanel
    {
        internal const string SurfaceId = "sidehustle-chat";

        private const float Width = 360f;

        private static SurfaceHandle _surface;
        private static GameObject _panel;
        private static ulong _peer;
        private static string _peerName = "";
        private static int _revisionSeen = -1;

        /// <summary>Whether a panel could be shown for this host at all: we know who they are, they said they take
        /// messages, and the engine can render outside the phone.</summary>
        internal static bool Possible(ulong hostSteamId, bool hostAcceptsMessages) =>
            hostSteamId != 0UL && hostAcceptsMessages && Surfaces.Available && Preferences.JoinChatPanel;

        /// <summary>
        /// Put the panel up for one host. Idempotent for the same peer, so a screen that rebuilds itself does not
        /// stack a second copy; a different peer replaces the first.
        /// </summary>
        internal static void Show(ulong hostSteamId, string hostName)
        {
            if (hostSteamId == 0UL) { Hide(); return; }
            if (_panel != null && _peer == hostSteamId && Surfaces.IsMounted(SurfaceId)) return;

            Hide();
            // One column, one owner. The state panel sits in the same strip, so it steps aside rather than
            // showing through from underneath.
            StatePanel.Suspend();
            _peer = hostSteamId;
            _peerName = string.IsNullOrWhiteSpace(hostName) ? ChatRelay.NameOf(hostSteamId) : hostName;

            try
            {
                // Its own screen-space-overlay canvas, for the reason Sideload documents: a canvas drawn by the
                // menu camera goes through that camera's tone mapping and every surface colour comes out flattened.
                _panel = new GameObject("SideHustle_ChatPanel");
                var canvas = _panel.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 120;   // over the menu, far under OverlayNotice's 32000

                // Unlike the state panel this one IS interactive - it has a field and two buttons - so it needs a
                // raycaster, and therefore it must not cover anything the player still has to click. The sync
                // screens keep their own content left of this column.
                _panel.AddComponent<UnityEngine.UI.GraphicRaycaster>();

                var holder = new GameObject("panel");
                var rect = holder.AddComponent<RectTransform>();
                rect.SetParent(_panel.transform, false);
                rect.anchorMin = new Vector2(1f, 0f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(1f, 0.5f);
                // Flush to the screen on three sides. At this size a floating card reads as a window that came
                // loose; docked, it reads as part of the screen - which is what it is for as long as it is open.
                rect.sizeDelta = new Vector2(Width, 0f);
                rect.anchoredPosition = Vector2.zero;

                _surface = Surfaces.Mount(rect, SurfaceId, "SideHustle.Assets.chat")
                    .OnCall("chat.state", _ => State())
                    .OnCall("chat.send", text => Send(text))
                    .OnCall("chat.close", _ => { Hide(); return "ok"; });

                _revisionSeen = ChatRelay.Revision;
                ChatRelay.MarkRead(_peer);
                Core.Log?.Msg($"[chat] panel open for {_peerName} ({_peer}).");
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[chat] could not open the panel: " + e.Message);
                _panel = null;
            }
        }

        internal static void Hide()
        {
            if (_panel == null && _surface == null) return;
            try { Surfaces.Unmount(SurfaceId); } catch { }
            try { if (_panel != null) UnityEngine.Object.Destroy(_panel); } catch { }
            _panel = null;
            _surface = null;
            _peer = 0UL;
            StatePanel.Resume();
        }

        internal static bool IsOpen => _panel != null;

        /// <summary>Pumped from Core.OnUpdate while a menu is up. One event when the thread actually moved, which
        /// for a conversation nobody is typing in is almost never.</summary>
        internal static void Tick()
        {
            if (_panel == null) return;
            if (ChatRelay.Revision == _revisionSeen) return;
            _revisionSeen = ChatRelay.Revision;
            ChatRelay.MarkRead(_peer);
            try { _surface?.Emit("chat.changed", ""); } catch { /* the page reads state on its next build */ }
        }

        private static string Send(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return "empty";
            if (!ChatRelay.Send(_peer, text)) return "error";
            _revisionSeen = ChatRelay.Revision;
            return "ok";
        }

        private static string State()
        {
            var msgs = Json.Array();
            foreach (var m in ChatRelay.Thread(_peer))
                msgs.Item(Json.Object().Add("mine", m.Mine).Add("text", m.Text));

            return Json.Object()
                .Add("host", _peerName)
                .Add("messages", msgs)
                .ToString();
        }
    }
}
