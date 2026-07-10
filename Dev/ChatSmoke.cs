#if DEBUG
using System;
using System.IO;
using System.Text;
using Il2CppSteamworks;

namespace SideHustle.Dev
{
    /// <summary>
    /// DEBUG-only transport smoke test for the Messenger module: proves Steam lobby chat round-trips in this
    /// environment (including the Goldberg test rig) BEFORE any chat UI exists. Enabled via
    /// SIDEHUSTLE_SELFTEST_CHAT=1; once the game is in a lobby it sends an SH1|MSG envelope every few seconds
    /// and logs every received lobby-chat entry to UserData\SideHustle\chatsmoke_&lt;steamid&gt;.log - its OWN file,
    /// because two rig instances share the game root and MelonLoader's Latest.log cannot be told apart. The
    /// envelope is ASCII by construction and can never collide with the vanilla control strings ("ready",
    /// "load_tutorial", "host_loading") the game's own lobby-chat handler reacts to.
    /// </summary>
    internal static class ChatSmoke
    {
        private const string EnvVar = "SIDEHUSTLE_SELFTEST_CHAT";

        private static bool _enabled, _init;
        private static Callback<LobbyChatMsg_t> _callback;   // held in a static: a GC'd Callback silently stops firing
        private static float _nextSend;
        private static int _seq, _rx;
        private static string _logPath;

        internal static void Tick()
        {
            if (!_init)
            {
                _init = true;
                _enabled = Environment.GetEnvironmentVariable(EnvVar) == "1";
                if (_enabled) Core.Log?.Msg("[chatsmoke] enabled; waiting for a lobby.");
            }
            if (!_enabled) return;

            if (_callback == null)
            {
                try
                {
                    _callback = Callback<LobbyChatMsg_t>.Create((Callback<LobbyChatMsg_t>.DispatchDelegate)OnChatMsg);
                    Log("callback registered; self=" + SelfId());
                }
                catch (Exception e)
                {
                    _enabled = false;
                    Core.Log?.Error("[chatsmoke] Callback<LobbyChatMsg_t>.Create failed: " + e);
                    return;
                }
            }

            if (!Multiplayer.LobbyCoordinator.IsInLobby) return;
            if (UnityEngine.Time.unscaledTime < _nextSend) return;
            _nextSend = UnityEngine.Time.unscaledTime + 5f;
            Send();
        }

        private static void Send()
        {
            try
            {
                ulong lobby = Multiplayer.LobbyCoordinator.CurrentLobbyId;
                if (lobby == 0UL) return;
                string text = $"hello #{_seq} from {SelfId()}";
                string envelope = "SH1|MSG|*|" + _seq + "|" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "|" +
                                  Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
                byte[] bytes = Encoding.ASCII.GetBytes(envelope + "\0");
                bool ok = SteamMatchmaking.SendLobbyChatMsg(new CSteamID(lobby), bytes, bytes.Length);
                Log($"tx seq={_seq} ok={ok} len={bytes.Length} lobby={lobby}");
                _seq++;
            }
            catch (Exception e) { Log("tx failed: " + e.Message); }
        }

        private static void OnChatMsg(LobbyChatMsg_t msg)
        {
            try
            {
                var lobby = new CSteamID(msg.m_ulSteamIDLobby);
                // The buffer must be an Il2CppStructArray: a managed byte[] gets implicitly CONVERTED (copied)
                // at the interop boundary, so Steam would fill a throwaway copy and the managed array stays empty.
                var buf = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(4096);
                int len = SteamMatchmaking.GetLobbyChatEntry(lobby, (int)msg.m_iChatID,
                    out CSteamID sender, buf, (int)buf.Length, out EChatEntryType type);
                int n = Math.Max(0, Math.Min(len, (int)buf.Length));
                var managed = new byte[n];
                for (int i = 0; i < n; i++) managed[i] = buf[i];
                string raw = n > 0 ? Encoding.ASCII.GetString(managed).TrimEnd('\0') : "";
                bool ours = raw.StartsWith("SH1|", StringComparison.Ordinal);
                _rx++;
                string detail = "";
                if (ours)
                {
                    var parts = raw.Split('|');
                    if (parts.Length == 6)
                        try { detail = " text='" + Encoding.UTF8.GetString(Convert.FromBase64String(parts[5])) + "'"; }
                        catch { detail = " text=<bad base64>"; }
                }
                Log($"rx #{_rx} from={sender.m_SteamID} type={type} len={len} ours={ours} raw='{Truncate(raw)}'{detail}");
            }
            catch (Exception e) { Log("rx handler failed: " + e.Message); }
        }

        private static string SelfId()
        {
            try { return SteamUser.GetSteamID().m_SteamID.ToString(); } catch { return "unknown"; }
        }

        private static void Log(string line)
        {
            Core.Log?.Msg("[chatsmoke] " + line);
            try
            {
                if (_logPath == null)
                {
                    string dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "SideHustle");
                    Directory.CreateDirectory(dir);
                    _logPath = Path.Combine(dir, $"chatsmoke_{SelfId()}.log");
                }
                File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch { /* best-effort */ }
        }

        private static string Truncate(string s) => s.Length <= 120 ? s : s.Substring(0, 120) + "...";
    }
}
#endif
