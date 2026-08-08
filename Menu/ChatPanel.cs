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

        /// <summary>The screen this column belonged to has gone. Deferred to the next tick rather than acted on at
        /// once, so a screen that re-shows the same host in the same frame keeps the live page - and with it the
        /// reply the player was half way through typing.</summary>
        private static bool _stale;

        /// <summary>The screen that owns this column is being torn down. See <see cref="_stale"/>.</summary>
        internal static void MarkStale() => _stale = _panel != null;

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
            // Which of the two columns owns the strip is decided once, in Core's menu pump - not here. Deciding it
            // from both ends is how the state column ended up dead for the rest of a session after one Esc.
            _stale = false;
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
                //
                // Scaled by widening the RECT and telling Sideload what the short side is worth, not with a
                // CanvasScaler: the engine derives its device-pixel ratio from the view's own scale, so a canvas
                // scaling underneath it would leave the painter believing one css pixel is one device pixel and
                // snapping every hairline to the wrong width. The menu is authored against 1920 and matches width.
                float ui = Mathf.Max(0.5f, Screen.width / 1920f);
                rect.sizeDelta = new Vector2(Width * ui, 0f);
                rect.anchoredPosition = Vector2.zero;

                _surface = Surfaces.Mount(rect, SurfaceId, "SideHustle.Assets.chat", designShortSide: Width)
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
        }

        internal static bool IsOpen => _panel != null;

        /// <summary>Which host the open column belongs to, or 0. The browser needs it to decide whether its Chat
        /// button closes this conversation or switches to another one.</summary>
        internal static ulong OpenPeer => _panel != null ? _peer : 0UL;

        /// <summary>
        /// What a Chat button on a lobby card does: close this host's column if it is the one showing, otherwise
        /// open theirs.
        ///
        /// Both halves matter and neither is obvious from the other. Somebody comparing three published sessions
        /// asks each host in turn, so the button has to SWITCH rather than refuse; and having asked, they want the
        /// column out of the way to read the list again, so the same button has to close it.
        /// </summary>
        /// <summary>
        /// Re-assert this host's column on a screen that is only passing through.
        /// </summary>
        /// <remarks>
        /// Every screen change marks the column stale, which is right for a screen that has nothing to do with the
        /// host - and wrong for the four wait screens between pressing Chat and reaching the decision, where the
        /// column would blink out for the five seconds a manifest read takes and take a half-typed question with
        /// it. Cheap and idempotent: Show returns at once for a peer already on screen.
        /// </remarks>
        internal static void Keep(ulong hostSteamId, string hostName, bool acceptsMessages)
        {
            if (Possible(hostSteamId, acceptsMessages)) Show(hostSteamId, hostName);
        }

        internal static void Toggle(ulong hostSteamId, string hostName)
        {
            if (OpenPeer == hostSteamId) { Hide(); return; }
            Show(hostSteamId, hostName);
        }

        /// <summary>Who the local player has actually written to. A reply is only a reply to a question that was
        /// asked; an unsolicited first line from a stranger must not be announced as one.</summary>
        private static readonly System.Collections.Generic.HashSet<ulong> _wroteTo = new System.Collections.Generic.HashSet<ulong>();

        /// <summary>The conversations are gone (a session ended, and ChatRelay dropped its threads). Forget who was
        /// written to as well, or the first line from a stranger in the NEXT session is announced as a reply to a
        /// question nobody asked.</summary>
        internal static void ForgetConversations()
        {
            _wroteTo.Clear();
            Hide();
        }

        /// <summary>
        /// Tell the player, in the menu, that the host they asked has answered.
        ///
        /// The host's side of this is a phone notification and an icon badge. A menu has neither - and worse,
        /// LobbyApp.Tick consumes the same arrival unconditionally, so without this the reply lands in complete
        /// silence unless that exact column happens to be open. Runs BEFORE LobbyApp.Tick for that reason.
        ///
        /// Nothing is said while the column showing that host is already up: they are watching the bubble arrive.
        /// </summary>
        internal static void AnnounceReply()
        {
            if (!Preferences.JoinChatPanel) return;
            ulong from = ChatRelay.PeekArrival();
            if (from == 0UL || !_wroteTo.Contains(from)) return;
            if (_panel != null && _peer == from) return;   // on screen, so the bubble is the notification

            ChatRelay.TakeArrival();
            try
            {
                DooDesch.UI.Toast.Show(ChatRelay.NameOf(from) + " answered.", DooDesch.UI.Severity.Info);
            }
            catch (Exception e) { Core.Log?.Warning("[chat] could not announce a reply: " + e.Message); }
        }

        /// <summary>Pumped from Core.OnUpdate while a menu is up. One event when the thread actually moved, which
        /// for a conversation nobody is typing in is almost never.</summary>
        internal static void Tick()
        {
            if (_stale) { _stale = false; Hide(); return; }
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
            _wroteTo.Add(_peer);
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
