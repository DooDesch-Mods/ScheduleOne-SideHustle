using System;
using System.Globalization;
using Sideload.Api;

namespace SideHustle.Phone
{
    /// <summary>
    /// The in-game half of the host's lobby controls: a phone app that changes the things the host form could only
    /// ask about before the world loaded.
    ///
    /// It exists because those choices go stale. A password gets passed around, four seats turn out to be three too
    /// few, a session that started as friends-only is the one people want to join. Every one of those used to mean
    /// ending the session, going back to the menu and hosting again.
    ///
    /// Sideload is not a new dependency here - Side Hustle already requires WhatsDab, which requires Sideload - and
    /// the registration is a no-op when it is missing, so nothing about this can stop the mod loading.
    /// </summary>
    internal static class LobbyApp
    {
        internal const string AppId = "sidehustle";

        private static AppHandle _app;

        // Pushed rather than polled: the page re-renders when something it did not do itself changed - somebody
        // joined, somebody left, the session ended. Compared as a string so any change at all counts.
        private static string _pushed = "";

        internal static void Register()
        {
            try
            {
                _app = Apps.Register(
                    id: AppId,
                    bundlePrefix: "SideHustle.Assets.lobby",
                    title: "Lobby",
                    iconLabel: "Lobby");

                _app.Orientation("landscape", "portrait")
                    .OnCall("lobby.state", _ => State())
                    .OnCall("lobby.setName", name => LobbyControls.SetLobbyName(name) ? "ok" : "error")
                    .OnCall("lobby.setPassword", pw => LobbyControls.SetPassword(pw) ? "ok" : "error")
                    .OnCall("lobby.setVisibility", v => LobbyControls.SetPublic(v == "pub") ? "ok" : "error")
                    .OnCall("lobby.setMax", v => SetMax(v))
                    .OnCall("lobby.setEnforce", v => LobbyControls.SetEnforce(v == "1") ? "ok" : "error")
                    .OnCall("lobby.togglePublish", _ => TogglePublish())
                    .OnCall("lobby.players", _ => Players())
                    .OnCall("chat.threads", _ => Threads())
                    .OnCall("chat.messages", peer => Messages(peer))
                    .OnCall("chat.send", arg => SendChat(arg))
                    .OnCall("chat.mute", peer => { if (ulong.TryParse(peer, out ulong id)) ChatRelay.Mute(id); return "ok"; })
                    .OnCall("chat.accepting", v =>
                    {
                        if (v == "1" || v == "0") LobbyControls.SetAccepting(v == "1");
                        return Config.Preferences.AcceptStrangerMessages ? "1" : "0";
                    });

                Core.Log?.Msg(Apps.Available
                    ? "[lobby] phone app registered."
                    : "[lobby] Sideload is not loaded yet - the app registration is queued.");
            }
            catch (Exception e) { Core.Log?.Warning("[lobby] could not register the phone app: " + e.Message); }
        }

        private static string SetMax(string raw)
        {
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seats)) return "error";
            // Answer with what Steam accepted, not with "ok": the page has to show the real number, and clamping
            // silently would leave a slider claiming a seat count nobody can take.
            return LobbyControls.SetMaxPlayers(seats).ToString(CultureInfo.InvariantCulture);
        }

        private static string TogglePublish()
        {
            if (!Sync.LivePublish.CanPublish) return "error";
            Sync.LivePublish.TogglePublished();
            return Sync.LivePublish.IsPublished ? "1" : "0";
        }

        private static string Players()
        {
            var arr = Json.Array();
            foreach (var m in LobbyControls.Roster())
                arr.Item(Json.Object()
                    .Add("name", m.Name)
                    .Add("host", m.IsHost)
                    .Add("self", m.IsSelf)
                    .Add("friend", m.IsFriend));
            return arr.ToString();
        }

        private static string Threads()
        {
            var arr = Json.Array();
            foreach (ulong peer in ChatRelay.Peers())
            {
                var thread = ChatRelay.Thread(peer);
                string last = thread.Count > 0 ? thread[thread.Count - 1].Text : "";
                arr.Item(Json.Object()
                    .Add("id", peer.ToString())
                    .Add("name", ChatRelay.NameOf(peer))
                    .Add("last", last)
                    .Add("unread", ChatRelay.IsUnread(peer)));
            }
            return arr.ToString();
        }

        private static string Messages(string peerId)
        {
            if (!ulong.TryParse(peerId, out ulong peer)) return "[]";
            ChatRelay.MarkRead(peer);
            var arr = Json.Array();
            foreach (var m in ChatRelay.Thread(peer))
                arr.Item(Json.Object().Add("mine", m.Mine).Add("text", m.Text));
            return arr.ToString();
        }

        /// <summary>
        /// Steam id and text, separated by a newline. Flat rather than JSON because the payload is two flat fields -
        /// and a SECOND newline is refused rather than folded in, so a pasted block cannot arrive as one unreadable
        /// line in somebody else's app.
        /// </summary>
        private static string SendChat(string arg)
        {
            var parts = (arg ?? "").Split('\n');
            if (parts.Length != 2) return "error";
            if (!ulong.TryParse(parts[0], out ulong peer)) return "error";
            return ChatRelay.Send(peer, parts[1]) ? "ok" : "error";
        }

        private static string State()
        {
            bool host = LobbyControls.IsHost;
            var j = Json.Object()
                .Add("host", host)
                // Whose game this is and what it runs on: a client's whole first tab, and useless to a host.
                .Add("hostName", LobbyControls.HostName)
                .Add("runtime", LobbyControls.Runtime)
                .Add("joinable", LobbyControls.JoinableNow)
                .Add("inLobby", LobbyControls.InLobby)
                .Add("name", LobbyControls.LobbyName)
                .Add("hasPassword", LobbyControls.HasPassword)
                // Only ever the plaintext this session set. After a restart the lobby carries a hash and nothing
                // else, so the page shows "set" and cannot show what - which beats inventing something.
                .Add("password", LobbyControls.KnownPassword)
                .Add("public", LobbyControls.IsPublic)
                .Add("members", LobbyControls.Members)
                .Add("max", LobbyControls.MaxPlayers)
                .Add("ceiling", LobbyControls.SeatCeiling)
                .Add("enforce", LobbyControls.Enforcing)
                .Add("canPublish", Sync.LivePublish.CanPublish)
                .Add("published", Sync.LivePublish.IsPublished)
                .Add("unread", ChatRelay.UnreadCount)
                .Add("accepting", Config.Preferences.AcceptStrangerMessages);
            return j.ToString();
        }

        /// <summary>Pumped from Core.OnUpdate. One event when the session state actually moved, so the page is not
        /// asking every frame and not stale either.</summary>
        internal static void Tick()
        {
            if (_app == null) return;
            // Also for a client: their Players tab is the whole reason they opened this, and somebody arriving or
            // leaving is exactly what they want to see without poking at it.
            // Auch ausserhalb einer Lobby: eine Nachricht kann eintreffen, waehrend gar keine Sitzung laeuft.
            string now = LobbyControls.InLobby || ChatRelay.Revision > 0
                ? $"{LobbyControls.IsHost}/{LobbyControls.Members}/{LobbyControls.MaxPlayers}/{LobbyControls.IsPublic}/{LobbyControls.HasPassword}/{LobbyControls.Enforcing}/{Sync.LivePublish.IsPublished}/{ChatRelay.Revision}"
                : "-/" + ChatRelay.Revision;
            if (now == _pushed) return;
            _pushed = now;
            try { _app.Emit("lobby.changed", now); } catch { /* the page will catch up on its next open */ }
        }
    }
}
