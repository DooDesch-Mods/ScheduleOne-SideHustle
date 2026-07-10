using System;
using UnityEngine;

namespace SideHustle.Messenger
{
    /// <summary>
    /// The Messenger backend: owns the transport, store and contacts, and is live whenever the local player is
    /// in ANY lobby (gamemode, published vanilla, plain co-op). Pumped from Core.OnUpdate. The phone app is a
    /// thin frontend over this; a notification fires for an incoming message while the app is not showing that
    /// thread. All Unity/Steam access is on the main thread (the transport callback runs there via Steamworks'
    /// RunCallbacks pump).
    /// </summary>
    internal static class ChatService
    {
        private static bool _started;
        private static int _seq;
        private static float _nextContactRefresh;
        private static ulong _lobby;

        /// <summary>The thread the app is currently showing (so incoming messages there are not counted unread).
        /// GroupKey by default; the app sets it as the user navigates. -1 sentinel via ulong not needed.</summary>
        internal static ulong ActiveThread = ChatStore.GroupKey;

        /// <summary>Raised on the main thread for an incoming message to a thread that is NOT active (for a toast).</summary>
        internal static Action<ChatMessage> OnBackgroundMessage;

        internal static bool InLobby => ChatTransport.InLobby;

        internal static void Tick()
        {
            if (!_started)
            {
                _started = true;
                if (OnBackgroundMessage == null) OnBackgroundMessage = ChatNotifier.Notify;
                ChatTransport.Start(HandleIncoming);
            }

            ulong lobby = InLobby ? CurrentLobbyId() : 0UL;
            if (lobby != _lobby)
            {
                _lobby = lobby;
                if (lobby == 0UL) { ChatStore.Clear(); }   // left the lobby: session chat is over
                else Contacts.Refresh(lobby);
                _nextContactRefresh = Time.unscaledTime + 5f;
            }
            else if (lobby != 0UL && Time.unscaledTime >= _nextContactRefresh)
            {
                _nextContactRefresh = Time.unscaledTime + 5f;
                Contacts.Refresh(lobby);
            }
        }

        /// <summary>Send a message to the group (recipientKey 0) or a specific member. No-op outside a lobby.</summary>
        internal static void Send(ulong recipientKey, string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || !InLobby) return;
            int seq = _seq++;
            ChatStore.AddLocal(recipientKey, seq, text);
            ChatTransport.Send(recipientKey, seq, text);
        }

        private static void HandleIncoming(ChatMessage m)
        {
            try
            {
                ChatStore.Receive(m, ActiveThread);
                if (m.SenderId != ChatTransport.SelfId())
                {
                    ulong key = m.RecipientId == ChatStore.GroupKey ? ChatStore.GroupKey : m.SenderId;
                    if (key != ActiveThread) OnBackgroundMessage?.Invoke(m);
                }
            }
            catch (Exception e) { Core.Log?.Warning("[messenger] incoming handling failed: " + e.Message); }
        }

        private static ulong CurrentLobbyId()
        {
            try { return Multiplayer.LobbyCoordinator.CurrentLobbyId; } catch { return 0UL; }
        }
    }
}
