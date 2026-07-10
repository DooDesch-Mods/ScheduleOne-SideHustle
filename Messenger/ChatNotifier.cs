using System;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.UI;

namespace SideHustle.Messenger
{
    /// <summary>
    /// Shows a native phone notification (with the game's own notification sound) for an incoming message to a
    /// thread the Messenger app is not currently showing. Reuses <c>NotificationsManager.SendNotification</c> so
    /// it looks and sounds exactly like the game's own alerts. Best-effort: a missing manager is silently
    /// ignored (e.g. before the world is fully up).
    /// </summary>
    internal static class ChatNotifier
    {
        internal static void Notify(ChatMessage m)
        {
            try
            {
                var mgr = Singleton<NotificationsManager>.Instance;
                if (mgr == null) return;
                string who = Contacts.NameOf(m.SenderId);
                string title = m.RecipientId == ChatStore.GroupKey ? "Lobby chat" : who;
                string subtitle = m.RecipientId == ChatStore.GroupKey ? who + ": " + m.Text : m.Text;
                if (subtitle.Length > 80) subtitle = subtitle.Substring(0, 79) + "…";
                mgr.SendNotification(title, subtitle, null, 5f, true);
            }
            catch (Exception e) { Core.Log?.Warning("[messenger] notification failed: " + e.Message); }
        }
    }
}
