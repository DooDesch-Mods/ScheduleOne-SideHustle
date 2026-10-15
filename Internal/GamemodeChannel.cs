using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;   // Il2CppStructArray<byte> - what the Steam interop takes
using Il2CppSteamworks;

namespace SideHustle.Internal
{
    /// <summary>
    /// Gamemode messages (<see cref="API.SendToAll(string, string, byte[])"/>, <see cref="API.SendTo(ulong, string, string, byte[])"/>,
    /// <see cref="API.OnMessage"/>) over Steam P2P.
    ///
    /// Its own channel, next to the chat relay's 41 and well above the low channels the game uses, so neither side
    /// can read a packet meant for the other. Only active while a Side Hustle gamemode session with a lobby is
    /// running, and only between members of that lobby: the sender id is the one Steam reports for the packet, a
    /// packet from anyone outside the lobby is dropped, and nothing in the payload can claim to be someone else.
    ///
    /// Wire format, version 1: "SHGM" | version byte | kind byte (0 bytes, 1 text) | id length byte | gamemode id
    /// (UTF-8) | channel length byte | channel (UTF-8) | payload.
    /// </summary>
    internal static class GamemodeChannel
    {
        internal const int Channel = 42;
        internal const int MaxPayloadBytes = 8 * 1024;
        private const int MaxNameBytes = 64;
        private const int MaxPacketBytes = MaxPayloadBytes + 8 + 2 * MaxNameBytes;
        private const int MaxPacketsPerFrame = 64;
        private const int SteamMaxPacket = 1024 * 1024;
        private const byte WireVersion = 1;
        private static readonly byte[] Magic = { (byte)'S', (byte)'H', (byte)'G', (byte)'M' };

        private static readonly Dictionary<string, Subscribers<Action<SessionMessage>>> _handlers =
            new Dictionary<string, Subscribers<Action<SessionMessage>>>(StringComparer.Ordinal);
        private static readonly object _handlersLock = new object();

        // Once-only log keys, so a busy channel logs its first success and each distinct refusal once.
        private static readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);

        private static Callback<P2PSessionRequest_t> _sessionRequest;
        private static bool _installed;
        private static bool _readFailureLogged;
        private static int _derefOffset = -1;

        internal static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text ?? "");

        // --- subscribe ------------------------------------------------------------------------------------

        internal static void Subscribe(string gamemodeId, Action<SessionMessage> handler)
        {
            if (string.IsNullOrWhiteSpace(gamemodeId)) { Core.Log?.Warning("[api] OnMessage needs a gamemode id; handler ignored."); return; }
            if (handler == null) { Core.Log?.Warning($"[api] OnMessage for '{gamemodeId}' got a null handler; ignored."); return; }
            Subscribers<Action<SessionMessage>> subs;
            lock (_handlersLock)
            {
                if (!_handlers.TryGetValue(gamemodeId, out subs))
                {
                    subs = new Subscribers<Action<SessionMessage>>($"OnMessage('{gamemodeId}')");
                    _handlers[gamemodeId] = subs;
                }
            }
            subs.Add(handler);
            Core.Log?.Msg($"[api] message handler added for '{gamemodeId}' by {Subscribers<Action<SessionMessage>>.OwnerOf(handler)}.");
        }

        internal static bool Unsubscribe(string gamemodeId, Action<SessionMessage> handler)
        {
            if (string.IsNullOrWhiteSpace(gamemodeId) || handler == null) return false;
            lock (_handlersLock)
                return _handlers.TryGetValue(gamemodeId, out var subs) && subs.Remove(handler);
        }

        // --- send -----------------------------------------------------------------------------------------

        /// <summary>Send to one member (<paramref name="to"/> != 0) or every other member (0). See the API docs.</summary>
        internal static bool Send(ulong to, string gamemodeId, string channel, byte[] payload, bool isText)
        {
            if (!ApiPump.OnMainThread)
            {
                byte[] copy = payload == null ? null : (byte[])payload.Clone();
                ApiPump.Post(() => Send(to, gamemodeId, channel, copy, isText));
                return true;
            }

            string what = $"'{gamemodeId}'/'{channel}'";
            if (string.IsNullOrWhiteSpace(gamemodeId)) { Core.Log?.Warning("[api] message refused: no gamemode id."); return false; }
            if (string.IsNullOrEmpty(channel)) { Core.Log?.Warning($"[api] message for '{gamemodeId}' refused: no channel name."); return false; }
            byte[] id = Encoding.UTF8.GetBytes(gamemodeId);
            byte[] ch = Encoding.UTF8.GetBytes(channel);
            if (id.Length > MaxNameBytes || ch.Length > MaxNameBytes)
            {
                Core.Log?.Warning($"[api] message {what} refused: gamemode id and channel are limited to {MaxNameBytes} bytes each.");
                return false;
            }
            payload ??= new byte[0];
            if (payload.Length > MaxPayloadBytes)
            {
                Core.Log?.Warning($"[api] message {what} refused: {payload.Length} bytes is over the {MaxPayloadBytes}-byte limit.");
                return false;
            }

            var session = SessionEvents.Current;
            if (session == null || session.LobbyId == 0UL)
            {
                Core.Log?.Warning($"[api] message {what} not sent: no Side Hustle session lobby is running.");
                return false;
            }
            if (!LobbyMembers.TryRead(session.LobbyId, out var members, out _, out string error))
            {
                Core.Log?.Error($"[api] message {what} not sent: lobby members unreadable ({error}).");
                return false;
            }

            ulong self = PlayerTracker.LocalSteamId();
            var targets = new List<ulong>();
            if (to == 0UL)
            {
                foreach (ulong m in members) if (m != self) targets.Add(m);
                if (targets.Count == 0)
                {
                    LogOnce("alone|" + what, $"[api] message {what} has no recipient: you are the only player in the session.");
                    return false;
                }
            }
            else if (to == self)
            {
                Core.Log?.Warning($"[api] message {what} refused: sending to yourself is not supported.");
                return false;
            }
            else if (!members.Contains(to))
            {
                Core.Log?.Warning($"[api] message {what} refused: {to} is not in the session lobby.");
                return false;
            }
            else targets.Add(to);

            Il2CppStructArray<byte> packet;
            try { packet = Encode(id, ch, payload, isText); }
            catch (Exception e) { Core.Log?.Error($"[api] message {what} could not be encoded: {e}"); return false; }

            EnsureInstalled();
            int sent = 0;
            foreach (ulong t in targets)
            {
                try
                {
                    // Open the way back too: whoever we write to may answer before their session request was handled.
                    SteamNetworking.AcceptP2PSessionWithUser(new CSteamID(t));
                    if (SteamNetworking.SendP2PPacket(new CSteamID(t), packet, (uint)packet.Length, EP2PSend.k_EP2PSendReliable, Channel)) sent++;
                    else Core.Log?.Warning($"[api] Steam refused message {what} to {t}.");
                }
                catch (Exception e) { Core.Log?.Error($"[api] message {what} to {t} failed: {e.Message}"); }
            }
            if (sent > 0) LogOnce("sent|" + what, $"[api] first message on {what} sent to {sent} player(s) (later sends are not logged).");
            return sent == targets.Count;
        }

        private static Il2CppStructArray<byte> Encode(byte[] id, byte[] ch, byte[] payload, bool isText)
        {
            int len = Magic.Length + 3 + id.Length + 1 + ch.Length + payload.Length;
            var buf = new byte[len];
            int o = 0;
            Buffer.BlockCopy(Magic, 0, buf, o, Magic.Length); o += Magic.Length;
            buf[o++] = WireVersion;
            buf[o++] = (byte)(isText ? 1 : 0);
            buf[o++] = (byte)id.Length;
            Buffer.BlockCopy(id, 0, buf, o, id.Length); o += id.Length;
            buf[o++] = (byte)ch.Length;
            Buffer.BlockCopy(ch, 0, buf, o, ch.Length); o += ch.Length;
            Buffer.BlockCopy(payload, 0, buf, o, payload.Length);

            var arr = new Il2CppStructArray<byte>(len);
            for (int i = 0; i < len; i++) arr[i] = buf[i];
            return arr;
        }

        // --- receive --------------------------------------------------------------------------------------

        /// <summary>Pumped every frame by ApiPump. Reads at most <see cref="MaxPacketsPerFrame"/> packets, so a flood
        /// costs a few frames of latency instead of a frozen frame.</summary>
        internal static void Tick()
        {
            EnsureInstalled();
            if (!_installed) return;
            try
            {
                for (int n = 0; n < MaxPacketsPerFrame; n++)
                {
                    if (!SteamNetworking.IsP2PPacketAvailable(out uint size, Channel)) break;
                    if (size == 0 || size > MaxPacketBytes)
                    {
                        Drain(size);
                        LogOnce("size|" + size, $"[api] dropped a {size}-byte packet on channel {Channel}: over the {MaxPacketBytes}-byte packet limit.", warn: true);
                        continue;
                    }
                    var buffer = new Il2CppStructArray<byte>((int)size);
                    if (!SteamNetworking.ReadP2PPacket(buffer, size, out uint read, out CSteamID from, Channel))
                    {
                        LogOnce("readfalse", $"[api] Steam reported a packet on channel {Channel} but would not hand it over.", warn: true);
                        break;
                    }
                    byte[] data = buffer;   // one copy out of the IL2CPP array; indexing it per byte is a native call each
                    Receive(from.m_SteamID, data, (int)Math.Min(read, size));
                }
            }
            catch (Exception e)
            {
                if (!_readFailureLogged) { _readFailureLogged = true; Core.Log?.Error($"[api] reading channel {Channel} failed: {e}"); }
            }
        }

        private static void Drain(uint size)
        {
            try
            {
                var junk = new Il2CppStructArray<byte>((int)Math.Max(1u, Math.Min(size, (uint)SteamMaxPacket)));
                SteamNetworking.ReadP2PPacket(junk, (uint)junk.Length, out _, out _, Channel);
            }
            catch (Exception e) { LogOnce("drain", $"[api] could not drop an oversized packet on channel {Channel}: {e.Message}", warn: true); }
        }

        private static void Receive(ulong from, byte[] data, int length)
        {
            var session = SessionEvents.Current;
            if (session == null || session.LobbyId == 0UL)
            {
                LogOnce("nosession|" + from, $"[api] dropped a gamemode message from {from}: no Side Hustle session lobby is running.");
                return;
            }
            if (!IsMember(session.LobbyId, from))
            {
                LogOnce("stranger|" + from, $"[api] dropped a gamemode message from {from}: not a member of the session lobby.", warn: true);
                return;
            }
            if (!TryDecode(data, length, out string gamemodeId, out string channel, out byte[] payload, out bool isText, out string why))
            {
                LogOnce("bad|" + from + "|" + why, $"[api] dropped a malformed gamemode message from {from}: {why}.", warn: true);
                return;
            }

            Subscribers<Action<SessionMessage>> subs;
            lock (_handlersLock) _handlers.TryGetValue(gamemodeId, out subs);
            if (subs == null || subs.Count == 0)
            {
                LogOnce("nohandler|" + gamemodeId, $"[api] a message for '{gamemodeId}' arrived but no OnMessage handler is registered for it.", warn: true);
                return;
            }
            var msg = new SessionMessage(gamemodeId, channel, from, payload, isText);
            LogOnce("recv|" + gamemodeId + "|" + channel, $"[api] first message on '{gamemodeId}'/'{channel}' received from {from} (later ones are not logged).");
            subs.Raise(h => h(msg));
        }

        private static bool IsMember(ulong lobbyId, ulong id)
        {
            if (id == 0UL) return false;
            if (PlayerTracker.Contains(id)) return true;
            // A joiner can write before the next roster poll has seen them; ask Steam directly.
            return LobbyMembers.TryRead(lobbyId, out var members, out _, out _) && members.Contains(id);
        }

        private static bool TryDecode(byte[] d, int len, out string gamemodeId, out string channel, out byte[] payload, out bool isText, out string why)
        {
            gamemodeId = channel = null; payload = null; isText = false; why = null;
            int o = 0;
            if (len < Magic.Length + 4) { why = "too short"; return false; }
            for (int i = 0; i < Magic.Length; i++) if (d[i] != Magic[i]) { why = "not a Side Hustle gamemode message"; return false; }
            o += Magic.Length;
            byte version = d[o++];
            if (version != WireVersion) { why = "wire version " + version + " is not supported"; return false; }
            isText = d[o++] == 1;
            int idLen = d[o++];
            if (idLen == 0 || o + idLen + 1 > len) { why = "bad gamemode id length"; return false; }
            gamemodeId = Encoding.UTF8.GetString(d, o, idLen); o += idLen;
            int chLen = d[o++];
            if (chLen == 0 || o + chLen > len) { why = "bad channel length"; return false; }
            channel = Encoding.UTF8.GetString(d, o, chLen); o += chLen;
            payload = new byte[len - o];
            Buffer.BlockCopy(d, o, payload, 0, payload.Length);
            return true;
        }

        // --- P2P session acceptance -----------------------------------------------------------------------

        /// <summary>
        /// Accept P2P sessions from members of the current session lobby, so their first packet is not dropped.
        /// The chat relay accepts strangers on its own terms; this only ever says yes to someone already playing
        /// with us.
        /// </summary>
        private static void EnsureInstalled()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                // Held in a static: a Callback that gets collected stops firing, silently.
                _sessionRequest = Callback<P2PSessionRequest_t>.Create(
                    (Callback<P2PSessionRequest_t>.DispatchDelegate)OnSessionRequest);
                Core.Log?.Msg($"[api] gamemode message channel ready (P2P channel {Channel}).");
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"[api] could not watch P2P session requests ({e.Message}); members are still accepted when we write to them first.");
            }
        }

        private static void OnSessionRequest(P2PSessionRequest_t req)
        {
            try
            {
                var session = SessionEvents.Current;
                if (session == null || session.LobbyId == 0UL) return;   // outside a session the chat relay decides alone

                ulong raw = 0UL;
                try { raw = req.m_steamIDRemote.m_SteamID; }
                catch (Exception e) { Core.Log?.Warning("[api] P2P session request unreadable: " + e.Message); return; }
                ulong id = ResolveSteamId(raw);
                if (id == 0UL)
                {
                    Core.Log?.Msg($"[api] P2P session request with an unreadable sender ({raw}); left to the chat relay.");
                    return;
                }
                if (!IsMember(session.LobbyId, id))
                {
                    Core.Log?.Msg($"[api] P2P session request from {id}, who is not in the session lobby; left to the chat relay.");
                    return;
                }
                bool ok = SteamNetworking.AcceptP2PSessionWithUser(new CSteamID(id));
                Core.Log?.Msg($"[api] P2P session from session member {id} {(ok ? "accepted" : "not accepted by Steam")}.");
            }
            catch (Exception e) { Core.Log?.Warning("[api] handling a P2P session request failed: " + e.Message); }
        }

        /// <summary>
        /// IL2CPP hands the callback's struct over by reference: <c>m_steamIDRemote</c> can hold the ADDRESS of the
        /// native struct instead of the id. A Steam id carries universe 1 and an account type in its top bits, which
        /// no heap address does, so the two shapes are told apart and the address is read back when needed.
        /// </summary>
        private static ulong ResolveSteamId(ulong raw)
        {
            if (LooksLikeSteamId(raw)) return raw;
            if (raw < 0x10000UL || raw > 0x7FFFFFFFFFFFUL) return 0UL;
            foreach (int offset in new[] { 0, 0x10 })
            {
                try
                {
                    ulong deref = unchecked((ulong)System.Runtime.InteropServices.Marshal.ReadInt64((IntPtr)(long)raw, offset));
                    if (!LooksLikeSteamId(deref)) continue;
                    if (_derefOffset != offset)
                    {
                        _derefOffset = offset;
                        Core.Log?.Msg("[api] P2P session requests arrive by reference; reading the sender at +" + offset + ".");
                    }
                    return deref;
                }
                catch (Exception e) { LogOnce("deref|" + offset, $"[api] reading a P2P sender at +{offset} failed: {e.Message}", warn: true); }
            }
            return 0UL;
        }

        private static bool LooksLikeSteamId(ulong v)
        {
            if (v == 0UL) return false;
            ulong universe = v >> 56;
            ulong type = (v >> 52) & 0xF;
            return universe == 1UL && type >= 1UL && type <= 10UL;
        }

        /// <summary>Log a line the first time its key comes up. <paramref name="warn"/> marks a dropped or refused
        /// packet (tolerated on purpose); the rest are expected states and successes.</summary>
        private static void LogOnce(string key, string line, bool warn = false)
        {
            lock (_logged)
            {
                if (_logged.Count > 512) _logged.Clear();   // bounded: a long session with many peers must not grow this forever
                if (!_logged.Add(key)) return;
            }
            if (warn) Core.Log?.Warning(line);
            else Core.Log?.Msg(line);
        }
    }
}
