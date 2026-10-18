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

        /// <summary>When we last wrote to each peer. The receiving side already refuses a second line inside
        /// MinGap and says nothing - and the sender had by then posted its own bubble, so "hey" and "can I join?"
        /// at normal typing speed both looked delivered while the host saw one. Refused here instead, where the
        /// page can say so.</summary>
        private static readonly Dictionary<ulong, DateTime> _lastWritten = new Dictionary<ulong, DateTime>();
        private static readonly HashSet<ulong> _muted = new HashSet<ulong>();
        private static readonly HashSet<ulong> _unread = new HashSet<ulong>();

        /// <summary>The peer whose message just landed, waiting to be turned into a notification. Handed over once
        /// and cleared, the same way WhatsDab does it: a notification is an EVENT, and re-deriving it from the
        /// thread list would fire again on every unrelated change.</summary>
        private static ulong _arrived;

        /// <summary>Peers we accepted a P2P session with and have not closed again. Steam keeps a session (and a relay
        /// connection) open for each until it is closed, so each is closed when its conversation ends.</summary>
        private static readonly Dictionary<ulong, DateTime> _sessions = new Dictionary<ulong, DateTime>();

        /// <summary>The per-sender tables are keyed by whatever SteamID a stranger sends from, so each is bounded: an
        /// attacker cycling accounts must not grow them without limit.</summary>
        private const int MaxPeers = 64;
        private const int MaxMuted = 512;

        /// <summary>A packet read loop must end even when Steam keeps reporting packets (or a bad size) every call.</summary>
        private const int MaxPacketsPerFrame = 32;

        private static readonly TimeSpan IdleSessionAge = TimeSpan.FromMinutes(15);
        private static DateTime _nextPrune;

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
                try { raw = req.m_steamIDRemote.m_SteamID; }
                catch (Exception e) { Core.Log?.Warning("[chat] could not read the sender of a P2P session request: " + e.Message); }

                ulong id = ResolveSteamId(raw);
                if (id == 0UL)
                {
                    Core.Log?.Warning("[chat] a P2P session request arrived with an unreadable sender (" + raw + ").");
                    return;
                }
                if (_muted.Contains(id))
                {
                    Core.Log?.Msg("[chat] ignored a P2P session request from muted " + id);
                    return;
                }
                if (!_sessions.ContainsKey(id) && _sessions.Count >= MaxPeers)
                {
                    CloseOldestSession();
                }
                SteamNetworking.AcceptP2PSessionWithUser(new CSteamID(id));
                _sessions[id] = DateTime.UtcNow;
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
            // The value itself is the id: the pointer is never touched.
            if (LooksLikeSteamId(raw))
            {
                if (!_byValueLogged)
                {
                    _byValueLogged = true;
                    Core.Log?.Msg("[chat] P2P session requests arrive by value.");
                }
                return raw;
            }
            // Only now is the value treated as an address.
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
                catch (Exception e)
                {
                    Core.Log?.Warning($"[chat] could not read the session request at +{offset}: {e.Message}");
                }
            }
            return 0UL;
        }

        private static int _derefOffset = -1;
        private static bool _byValueLogged;

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
                int handled = 0;
                while (handled < MaxPacketsPerFrame && SteamNetworking.IsP2PPacketAvailable(out uint size, Channel))
                {
                    handled++;
                    if (size == 0 || size > 4096)
                    {
                        if (!DrainOne(size)) break;
                        continue;
                    }
                    var buffer = new Il2CppStructArray<byte>((int)size);
                    if (!SteamNetworking.ReadP2PPacket(buffer, size, out uint read, out CSteamID from, Channel))
                    {
                        // Steam said a packet was there and then would not hand it over; asking again this frame
                        // would only get the same answer.
                        WarnOnce(ref _readFailLogged, "Steam reported a P2P packet but would not read it; retrying next frame.");
                        break;
                    }
                    PacketsRead++;
                    Receive(from.m_SteamID, Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(read, size)));
                }
                if (handled >= MaxPacketsPerFrame)
                    WarnOnce(ref _floodLogged, $"more than {MaxPacketsPerFrame} P2P packets waiting in one frame; the rest is read on the next frames.");

                if (DateTime.UtcNow >= _nextPrune)
                {
                    _nextPrune = DateTime.UtcNow.AddSeconds(60);
                    PruneIdle(DateTime.UtcNow);
                }
            }
            // Runs every frame: without Steam this would repeat the same line sixty times a second.
            catch (Exception e) { WarnOnce(ref _pumpFailLogged, "reading chat packets failed (logged once): " + e.Message); }
        }

        private static bool _pumpFailLogged;
        private static bool _readFailLogged, _floodLogged, _drainFailLogged, _personaLogged, _acceptBackLogged, _nameLogged, _dropLogged;

        private static void WarnOnce(ref bool flag, string text)
        {
            if (flag) return;
            flag = true;
            Core.Log?.Warning("[chat] " + text);
        }

        /// <summary>Take a packet off the queue and throw it away - a malformed size must not wedge the loop.</summary>
        private static bool DrainOne(uint size)
        {
            try
            {
                var junk = new Il2CppStructArray<byte>((int)Math.Max(1, Math.Min(size, 4096)));
                PacketsRejected++;
                if (SteamNetworking.ReadP2PPacket(junk, (uint)junk.Length, out _, out _, Channel)) return true;
                WarnOnce(ref _drainFailLogged, $"could not discard a P2P packet of {size} bytes.");
                return false;
            }
            catch (Exception e)
            {
                WarnOnce(ref _drainFailLogged, $"discarding a P2P packet of {size} bytes failed: {e.Message}");
                return false;
            }
        }

        private static void Receive(ulong from, string payload)
        {
            if (from == 0UL || _muted.Contains(from)) { PacketsRejected++; return; }
            if (!Config.Preferences.AcceptStrangerMessages)
            {
                PacketsRejected++;
                if (!_dropLogged)
                {
                    _dropLogged = true;
                    Core.Log?.Msg("[chat] dropping messages from strangers - they are switched off (logged once; see shchatdiag for the count).");
                }
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
            if (!_lastHeard.ContainsKey(from)) MakeRoomForPeer(now);
            _lastHeard[from] = now;

            string text = parts[2].Trim();
            if (text.Length == 0) return;
            if (text.Length > MaxTextLength) text = text.Substring(0, MaxTextLength);

            string name = parts[1].Trim();
            if (name.Length > 0) _names[from] = name.Length > 48 ? name.Substring(0, 48) : name;
            if (_names.Count > MaxPeers + MaxMuted) TrimNames();

            Add(from, new Message { Mine = false, Text = text, At = now });
            _unread.Add(from);
            _arrived = from;
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

            DateTime now = DateTime.UtcNow;
            if (_lastWritten.TryGetValue(to, out DateTime wrote) && now - wrote < MinGap) return false;

            string me = "";
            try { me = SteamFriends.GetPersonaName() ?? ""; }
            catch (Exception e) { WarnOnce(ref _personaLogged, "could not read our own display name; sending without one: " + e.Message); }
            // '|' separates the fields, so one inside a Steam name would move the rest of the name into the text.
            byte[] bytes = Encoding.UTF8.GetBytes(Magic + "|" + me.Replace('|', '/') + "|" + text);

            try
            {
                // Open the way back at the same time. Whoever we just wrote to is exactly who may answer, and an
                // answer that arrives before their session request has been handled would otherwise be dropped.
                try
                {
                    SteamNetworking.AcceptP2PSessionWithUser(new CSteamID(to));
                    _sessions[to] = now;
                }
                catch (Exception e) { WarnOnce(ref _acceptBackLogged, "could not open the way back to " + to + " (sending anyway): " + e.Message); }

                var payload = new Il2CppStructArray<byte>(bytes.Length);
                for (int i = 0; i < bytes.Length; i++) payload[i] = bytes[i];
                bool ok = SteamNetworking.SendP2PPacket(new CSteamID(to), payload, (uint)bytes.Length,
                    EP2PSend.k_EP2PSendReliable, Channel);
                if (ok)
                {
                    PacketsSent++;
                    if (!_lastWritten.ContainsKey(to)) MakeRoomForPeer(now);
                    _lastWritten[to] = now;
                    Add(to, new Message { Mine = true, Text = text, At = now });
                    Core.Log?.Msg($"[chat] sent a message to {to} ({text.Length} characters).");
                }
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

        /// <summary>Who has a message waiting to be announced, without consuming it. Two things want the same
        /// arrival - the phone app's notification and the menu's toast - and only one of them should get it.</summary>
        internal static ulong PeekArrival() => _arrived;

        /// <summary>The peer whose message arrived since the last call, or 0. Consumed by reading it.</summary>
        internal static ulong TakeArrival()
        {
            ulong who = _arrived;
            _arrived = 0UL;
            return who;
        }

        /// <summary>The newest line in a thread, for a notification body.</summary>
        internal static string LastText(ulong peer)
        {
            var list = Thread(peer);
            return list.Count > 0 ? list[list.Count - 1].Text : "";
        }

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
            catch (Exception e) { WarnOnce(ref _nameLogged, "could not read a display name from Steam: " + e.Message); }
            return peer.ToString();
        }

        /// <summary>
        /// Stop hearing from one person, and forget what they said. Per-sender, because switching the whole relay
        /// off because of one person is a heavier answer than the problem.
        ///
        /// The conversation goes, the NAME stays. That is what makes this undoable: a mute is one click next to a
        /// thread and is going to be hit by accident, and without the name there is nothing left to point an
        /// unmute at.
        /// </summary>
        internal static void Mute(ulong peer)
        {
            if (peer == 0UL) return;
            if (_muted.Count >= MaxMuted && !_muted.Contains(peer))
            {
                Core.Log?.Warning($"[chat] the mute list is full ({MaxMuted}); not muting {peer}.");
                return;
            }
            _muted.Add(peer);
            _threads.Remove(peer);
            _unread.Remove(peer);
            _lastHeard.Remove(peer);
            _lastWritten.Remove(peer);
            CloseSession(peer);
            Revision++;
            Core.Log?.Msg("[chat] muted " + NameOf(peer) + " (" + peer + ")");
        }

        /// <summary>Hear from them again. The conversation muting threw away does not come back - only the next
        /// thing they say.</summary>
        internal static void Unmute(ulong peer)
        {
            if (!_muted.Remove(peer)) return;
            Revision++;
            Core.Log?.Msg("[chat] unmuted " + NameOf(peer) + " (" + peer + ")");
        }

        internal static bool IsMuted(ulong peer) => _muted.Contains(peer);

        /// <summary>Everyone currently muted, newest first, so the app can offer the way back.</summary>
        internal static IReadOnlyList<ulong> Muted()
        {
            var ids = new List<ulong>(_muted);
            ids.Sort();
            return ids;
        }

        /// <summary>Close the P2P session with one peer, so Steam stops holding a connection for a conversation that
        /// is over.</summary>
        private static void CloseSession(ulong peer)
        {
            if (!_sessions.Remove(peer)) return;
            try
            {
                SteamNetworking.CloseP2PSessionWithUser(new CSteamID(peer));
                Core.Log?.Msg("[chat] closed the P2P session with " + peer);
            }
            catch (Exception e) { Core.Log?.Warning($"[chat] could not close the P2P session with {peer}: {e.Message}"); }
        }

        private static void CloseAllSessions()
        {
            if (_sessions.Count == 0) return;
            foreach (var peer in new List<ulong>(_sessions.Keys)) CloseSession(peer);
        }

        private static void CloseOldestSession()
        {
            ulong oldest = 0UL;
            DateTime at = DateTime.MaxValue;
            foreach (var kv in _sessions)
                if (kv.Value < at) { at = kv.Value; oldest = kv.Key; }
            if (oldest != 0UL) CloseSession(oldest);
        }

        /// <summary>Drop what has gone quiet: sessions idle for a while are closed and the rate-limit stamps that can
        /// no longer refuse anything are forgotten.</summary>
        private static void PruneIdle(DateTime now)
        {
            foreach (var kv in new List<KeyValuePair<ulong, DateTime>>(_sessions))
                if (now - kv.Value > IdleSessionAge && !_threads.ContainsKey(kv.Key)) CloseSession(kv.Key);
            RemoveOlderThan(_lastHeard, now - MinGap);
            RemoveOlderThan(_lastWritten, now - MinGap);
        }

        private static void RemoveOlderThan(Dictionary<ulong, DateTime> map, DateTime cutoff)
        {
            if (map.Count == 0) return;
            foreach (var kv in new List<KeyValuePair<ulong, DateTime>>(map))
                if (kv.Value < cutoff) map.Remove(kv.Key);
        }

        /// <summary>Before a NEW sender is added to the tables: when they are full, forget the quietest conversation
        /// (never a muted one - that is a decision of the player's) and close its session.</summary>
        private static void MakeRoomForPeer(DateTime now)
        {
            if (_threads.Count < MaxPeers && _lastHeard.Count < MaxPeers * 4 && _lastWritten.Count < MaxPeers * 4) return;
            PruneIdle(now);
            if (_threads.Count < MaxPeers) return;

            ulong quietest = 0UL;
            DateTime at = DateTime.MaxValue;
            foreach (var kv in _threads)
            {
                DateTime t = Last(kv.Key);
                if (t < at) { at = t; quietest = kv.Key; }
            }
            if (quietest == 0UL) return;
            Core.Log?.Warning($"[chat] {MaxPeers} conversations are open; forgetting the quietest ({quietest}) to make room.");
            _threads.Remove(quietest);
            _unread.Remove(quietest);
            _lastHeard.Remove(quietest);
            _lastWritten.Remove(quietest);
            CloseSession(quietest);
            Revision++;
        }

        private static void TrimNames()
        {
            foreach (var key in new List<ulong>(_names.Keys))
            {
                if (_names.Count <= MaxPeers + MaxMuted) break;
                if (!_muted.Contains(key) && !_threads.ContainsKey(key)) _names.Remove(key);
            }
        }

        /// <summary>The session ended - the conversation was about getting into THAT session.</summary>
        internal static void Clear()
        {
            CloseAllSessions();
            if (_threads.Count == 0 && _unread.Count == 0) return;
            _threads.Clear();
            _unread.Clear();
            // _muted deliberately survives: it is a decision about a person, not about a session.
            _lastHeard.Clear();
            _lastWritten.Clear();
            _arrived = 0UL;
            Revision++;
        }
    }
}
