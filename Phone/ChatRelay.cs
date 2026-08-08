using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;   // Il2CppStructArray<byte> - what the Steam interop takes
using Il2CppSteamworks;

namespace SideHustle.Phone
{
    /// <summary>
    /// Short messages between someone who cannot join a lobby and the host of it.
    ///
    /// The case this exists for: a published lobby is right there in the browser, and the joiner is stuck - a mod
    /// the host built themselves, a full session, the wrong branch. Today they close the game and the host never
    /// learns anyone tried. Everything else about that screen tells them WHY; this is the only thing that lets them
    /// say anything back.
    ///
    /// **Steam P2P, not the lobby chat.** Lobby chat would mean joining the Steam lobby, and joining costs one of
    /// the host's seats - in a four-seat session, a quarter of it, held by someone who is not playing. A P2P packet
    /// goes to a SteamID and needs no membership at all. The host's id is already readable from the lobby's own
    /// "owner" key without entering (Sync/VanillaLobby.cs).
    ///
    /// Deliberately NOT a general chat. Side Hustle already requires WhatsDab, which is the in-lobby messenger; a
    /// second one would be the same feature twice. This carries one thread per stranger, in memory, for as long as
    /// the game runs.
    /// </summary>
    internal static class ChatRelay
    {
        /// <summary>Our own P2P channel. The game uses low channels for its own traffic; this sits well clear of
        /// them so a stray read can never take a packet FishNet was waiting for.</summary>
        private const int Channel = 41;

        private const string Magic = "sh1";
        private const int MaxTextLength = 240;

        /// <summary>How often one sender may be heard from. A stranger with a keyboard is the whole abuse surface
        /// here, and a rate limit costs nothing to the honest case: nobody types two sentences a second.</summary>
        private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(2);

        /// <summary>Threads per peer, oldest first. In memory on purpose - this is a conversation about getting into
        /// a session that is happening right now, not correspondence.</summary>
        internal sealed class Message
        {
            internal bool Mine;
            internal string Text = "";
            internal DateTime At;
        }

        private static readonly Dictionary<ulong, List<Message>> _threads = new Dictionary<ulong, List<Message>>();
        private static readonly Dictionary<ulong, string> _names = new Dictionary<ulong, string>();
        private static readonly Dictionary<ulong, DateTime> _lastHeard = new Dictionary<ulong, DateTime>();
        private static readonly HashSet<ulong> _muted = new HashSet<ulong>();
        private static readonly HashSet<ulong> _unread = new HashSet<ulong>();

        private static Callback<P2PSessionRequest_t> _sessionRequest;
        private static bool _installed;

        /// <summary>Counters for the one question this system cannot answer from a thread list: did anything at all
        /// come off the wire. A message that never arrives looks identical to a message nobody sent, and these tell
        /// the two apart - see the shchatdiag console command.</summary>
        internal static int SessionRequests { get; private set; }
        internal static int PacketsRead { get; private set; }
        internal static int PacketsRejected { get; private set; }
        internal static int PacketsSent { get; private set; }
        internal static bool Installed => _installed;

        /// <summary>Bumped whenever anything a page renders changed, so the app can push one event instead of the
        /// page polling a conversation that is silent nearly all the time.</summary>
        internal static int Revision { get; private set; }

        internal static int UnreadCount => _unread.Count;

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                // Held in a static: a Callback that gets collected stops firing, silently.
                _sessionRequest = Callback<P2PSessionRequest_t>.Create(
                    (Callback<P2PSessionRequest_t>.DispatchDelegate)OnSessionRequest);
                Core.Log?.Msg("[chat] relay ready (P2P channel " + Channel + ").");
            }
            catch (Exception e) { Core.Log?.Warning("[chat] could not install the relay: " + e.Message); }
        }

        /// <summary>
        /// Someone wants to open a P2P session with us. Accepting is what lets their first packet arrive at all.
        ///
        /// Accepted even when the host has messages switched off, and then dropped on read: refusing the session
        /// itself tells the sender nothing useful and leaves Steam retrying. The switch is enforced where the
        /// content is, not at the handshake.
        /// </summary>
        private static void OnSessionRequest(P2PSessionRequest_t req)
        {
            try
            {
                SessionRequests++;
                ulong raw = 0UL;
                try { raw = req.m_steamIDRemote.m_SteamID; } catch { }

                ulong id = ResolveSteamId(raw);
                if (id == 0UL)
                {
                    Core.Log?.Warning("[chat] a P2P session request arrived with an unreadable sender (" + raw + ").");
                    return;
                }
                if (_muted.Contains(id)) return;
                SteamNetworking.AcceptP2PSessionWithUser(new CSteamID(id));
                Core.Log?.Msg("[chat] accepted a P2P session from " + id);
            }
            catch (Exception e) { Core.Log?.Warning("[chat] could not accept a P2P session: " + e.Message); }
        }

        /// <summary>
        /// The sender of a P2P session request, whatever the interop handed us.
        ///
        /// IL2CPP does not deliver the callback's struct by value: what arrives in <c>m_steamIDRemote</c> is the
        /// ADDRESS of the native <c>P2PSessionRequest_t</c>, not the id inside it. Accepting that number accepts a
        /// session with nobody, so the real sender's first packet is dropped and the whole relay looks dead while
        /// every call reports success. Reading the id back out of that address is what makes the handshake work.
        ///
        /// Both shapes are handled rather than one: an interop that starts passing the struct properly must not
        /// turn a working chat into a crash, and the two are told apart by the id's own bits - a Steam id carries
        /// universe 1 and an account type in its top byte, which no heap address does.
        /// </summary>
        private static ulong ResolveSteamId(ulong raw)
        {
            if (LooksLikeSteamId(raw)) return raw;
            if (raw < 0x10000UL || raw > 0x7FFFFFFFFFFFUL) return 0UL;   // not a user-space address either

            // Offset 0 is the bare struct; 0x10 is where the payload sits if it ever arrives boxed (il2cpp object
            // header). Trying both costs one read and saves a silent regression on the next interop change.
            foreach (int offset in new[] { 0, 0x10 })
            {
                try
                {
                    ulong deref = unchecked((ulong)System.Runtime.InteropServices.Marshal.ReadInt64((IntPtr)(long)raw, offset));
                    if (!LooksLikeSteamId(deref)) continue;
                    if (_derefOffset != offset)
                    {
                        _derefOffset = offset;
                        Core.Log?.Msg("[chat] P2P session requests arrive by reference; reading the sender at +" + offset + ".");
                    }
                    return deref;
                }
                catch { /* try the next offset */ }
            }
            return 0UL;
        }

        private static int _derefOffset = -1;

        /// <summary>Universe 1 (public) and an account type in 1..10 - the bits every real CSteamID carries and no
        /// pointer does.</summary>
        private static bool LooksLikeSteamId(ulong v)
        {
            if (v == 0UL) return false;
            ulong universe = v >> 56;
            ulong type = (v >> 52) & 0xF;
            return universe == 1UL && type >= 1UL && type <= 10UL;
        }

        /// <summary>Pumped from Core.OnUpdate. Reads whatever arrived since the last frame.</summary>
        internal static void Tick()
        {
            if (!_installed) return;
            try
            {
                while (SteamNetworking.IsP2PPacketAvailable(out uint size, Channel))
                {
                    if (size == 0 || size > 4096) { DrainOne(size); continue; }
                    var buffer = new Il2CppStructArray<byte>((int)size);
                    if (!SteamNetworking.ReadP2PPacket(buffer, size, out uint read, out CSteamID from, Channel)) break;
                    PacketsRead++;
                    Receive(from.m_SteamID, Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(read, size)));
                }
            }
            catch (Exception e) { Core.Log?.Warning("[chat] read failed: " + e.Message); }
        }

        /// <summary>Take a packet off the queue and throw it away - a malformed size must not wedge the loop.</summary>
        private static void DrainOne(uint size)
        {
            try
            {
                var junk = new Il2CppStructArray<byte>((int)Math.Max(1, Math.Min(size, 4096)));
                SteamNetworking.ReadP2PPacket(junk, (uint)junk.Length, out _, out _, Channel);
            }
            catch { /* nothing to salvage */ }
        }

        private static void Receive(ulong from, string payload)
        {
            if (from == 0UL || _muted.Contains(from)) { PacketsRejected++; return; }
            if (!Config.Preferences.AcceptStrangerMessages)
            {
                PacketsRejected++;
                Core.Log?.Msg("[chat] dropped a message from " + from + " - stranger messages are switched off.");
                return;
            }

            // "sh1|<display name>|<text>" - the name travels with the message because we may never have shared a
            // lobby with this person, so Steam has no persona for them cached.
            string[] parts = (payload ?? "").Split(new[] { '|' }, 3);
            if (parts.Length != 3 || parts[0] != Magic)
            {
                // Not ours. Worth a line rather than silence: it means something else is on this channel.
                PacketsRejected++;
                Core.Log?.Warning("[chat] ignored a packet on channel " + Channel + " that is not a Side Hustle message.");
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (_lastHeard.TryGetValue(from, out DateTime last) && now - last < MinGap) { PacketsRejected++; return; }
            _lastHeard[from] = now;

            string text = parts[2].Trim();
            if (text.Length == 0) return;
            if (text.Length > MaxTextLength) text = text.Substring(0, MaxTextLength);

            string name = parts[1].Trim();
            if (name.Length > 0) _names[from] = name.Length > 48 ? name.Substring(0, 48) : name;

            Add(from, new Message { Mine = false, Text = text, At = now });
            _unread.Add(from);
            Core.Log?.Msg($"[chat] message from {NameOf(from)}: {text}");
        }

        /// <summary>Send to a SteamID we are not in a lobby with. Reliable, because a message the sender believes
        /// went out and did not is worse than a visible failure.</summary>
        internal static bool Send(ulong to, string text)
        {
            if (to == 0UL) return false;
            text = (text ?? "").Trim();
            if (text.Length == 0) return false;
            if (text.Length > MaxTextLength) text = text.Substring(0, MaxTextLength);

            string me = "";
            try { me = SteamFriends.GetPersonaName() ?? ""; } catch { }
            byte[] bytes = Encoding.UTF8.GetBytes(Magic + "|" + me + "|" + text);

            try
            {
                // Open the way back at the same time. Whoever we just wrote to is exactly who may answer, and an
                // answer that arrives before their session request has been handled would otherwise be dropped.
                try { SteamNetworking.AcceptP2PSessionWithUser(new CSteamID(to)); } catch { /* not fatal */ }

                var payload = new Il2CppStructArray<byte>(bytes.Length);
                for (int i = 0; i < bytes.Length; i++) payload[i] = bytes[i];
                bool ok = SteamNetworking.SendP2PPacket(new CSteamID(to), payload, (uint)bytes.Length,
                    EP2PSend.k_EP2PSendReliable, Channel);
                if (ok) { PacketsSent++; Add(to, new Message { Mine = true, Text = text, At = DateTime.UtcNow }); }
                else Core.Log?.Warning("[chat] Steam refused the message to " + to);
                return ok;
            }
            catch (Exception e) { Core.Log?.Warning("[chat] send failed: " + e.Message); return false; }
        }

        private static void Add(ulong peer, Message m)
        {
            if (!_threads.TryGetValue(peer, out List<Message> list))
            {
                list = new List<Message>();
                _threads[peer] = list;
            }
            list.Add(m);
            // Twenty is a conversation about getting into a game. Anything longer is a different app.
            if (list.Count > 20) list.RemoveAt(0);
            Revision++;
        }

        internal static IReadOnlyList<ulong> Peers()
        {
            var ids = new List<ulong>(_threads.Keys);
            // Most recent first: the person still waiting is the one worth answering.
            ids.Sort((a, b) => Last(b).CompareTo(Last(a)));
            return ids;
        }

        private static DateTime Last(ulong peer) =>
            _threads.TryGetValue(peer, out List<Message> l) && l.Count > 0 ? l[l.Count - 1].At : DateTime.MinValue;

        internal static IReadOnlyList<Message> Thread(ulong peer) =>
            _threads.TryGetValue(peer, out List<Message> l) ? l : (IReadOnlyList<Message>)Array.Empty<Message>();

        internal static bool IsUnread(ulong peer) => _unread.Contains(peer);

        internal static void MarkRead(ulong peer)
        {
            if (_unread.Remove(peer)) Revision++;
        }

        internal static string NameOf(ulong peer)
        {
            if (_names.TryGetValue(peer, out string n) && !string.IsNullOrWhiteSpace(n)) return n;
            try
            {
                string persona = SteamFriends.GetFriendPersonaName(new CSteamID(peer));
                if (!string.IsNullOrWhiteSpace(persona)) return persona;
            }
            catch { }
            return peer.ToString();
        }

        /// <summary>Stop hearing from one person, and forget what they said. Per-sender, because switching the
        /// whole relay off because of one person is a heavier answer than the problem.</summary>
        internal static void Mute(ulong peer)
        {
            _muted.Add(peer);
            _threads.Remove(peer);
            _unread.Remove(peer);
            Revision++;
            Core.Log?.Msg("[chat] muted " + peer);
        }

        /// <summary>The session ended - the conversation was about getting into THAT session.</summary>
        internal static void Clear()
        {
            if (_threads.Count == 0 && _unread.Count == 0) return;
            _threads.Clear();
            _unread.Clear();
            _lastHeard.Clear();
            Revision++;
        }
    }
}
