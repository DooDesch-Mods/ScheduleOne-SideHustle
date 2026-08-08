using System;
using Il2CppSteamworks;
using SideHustle.Multiplayer;
using SideHustle.Sync;

namespace SideHustle.Phone
{
    /// <summary>
    /// The live lobby settings, read and written from inside a running session.
    ///
    /// Everything here is a lobby-data write against a lobby we own, which is why it can happen mid-game at all:
    /// the host form's choices are only ever the STARTING values, and Steam is happy to be told new ones. What that
    /// does and does not reach is worth being exact about, because the app has to say so:
    ///
    /// - Name, password, visibility and the mod-set flag are advertisement. They change what a joiner sees and what
    ///   the browser lets them do. Nobody already in the session is affected.
    /// - The player limit is real, and it is Steam's. Lowering it below the current headcount removes nobody; it
    ///   stops the next person getting in. FullHouse raises the TRANSPORT limit when the lobby is created
    ///   (Stash/fullhouse/FullHouse.cs), so anything up to that cap works live and anything above it would be a
    ///   lobby seat the transport refuses to fill - hence the ceiling from LobbyCaps.
    /// - The password is a courtesy gate, not security. It is checked client-side in our own browser
    ///   (Menu/Hub.cs, Menu/HubVanilla.cs); a Steam invite walks straight past it. The app says so rather than
    ///   implying a lock.
    /// </summary>
    internal static class LobbyControls
    {
        /// <summary>The plaintext the host last set THIS session, so the app can show them what to pass on. The
        /// lobby itself only ever carries the hash, so after a restart this is empty and the app shows "set" without
        /// being able to show what - which is the honest answer rather than a guess.</summary>
        private static string _password = "";

        internal static bool IsHost
        {
            get { try { return LobbyCoordinator.IsInLobby && LobbyCoordinator.IsHost; } catch { return false; } }
        }

        /// <summary>In a lobby at all, host or not. The app's three states are "hosting", "in someone else's
        /// session" and "nothing running", and only this tells the last two apart.</summary>
        internal static bool InLobby
        {
            get { try { return LobbyCoordinator.IsInLobby; } catch { return false; } }
        }

        private static CSteamID Sid => new CSteamID(LobbyCoordinator.CurrentLobbyId);

        private static string Read(string key)
        {
            try { return SteamMatchmaking.GetLobbyData(Sid, key) ?? ""; }
            catch { return ""; }
        }

        private static bool Write(string key, string value)
        {
            if (!IsHost) return false;
            try { return SteamMatchmaking.SetLobbyData(Sid, key, value ?? ""); }
            catch (Exception e) { Core.Log?.Warning($"[lobby] could not write '{key}': {e.Message}"); return false; }
        }

        internal static string LobbyName => Read(LobbyCoordinator.KeyLobbyName);
        internal static bool HasPassword => Read(LobbyCoordinator.KeyPassword) == "1";
        internal static string KnownPassword => _password;
        internal static bool IsPublic => Read(LobbyCoordinator.KeyVisibility) != "priv";
        internal static bool Enforcing => Read(VanillaLobby.KeyEnforce) == "1";

        /// <summary>Whether this lobby advertises a mod list at all. Without one there is nothing a mod-set
        /// requirement could check a joiner against, so the switch that asks for one has to be able to say so.</summary>
        internal static bool PublishesModList => !string.IsNullOrEmpty(Read(VanillaLobby.KeyMHash));

        /// <summary>Seats Steam is actually handing out right now, which is not necessarily what the host asked
        /// for - Steam can refuse a limit.</summary>
        internal static int MaxPlayers
        {
            get
            {
                try
                {
                    int real = SteamMatchmaking.GetLobbyMemberLimit(Sid);
                    if (real >= 2) return real;
                }
                catch { /* fall through to the advertised value */ }
                int.TryParse(Read(LobbyCoordinator.KeyMax), out int advertised);
                return advertised >= 2 ? advertised : 4;
            }
        }

        internal static int Members
        {
            get { try { return Math.Max(1, LobbyCoordinator.MemberCount); } catch { return 1; } }
        }

        /// <summary>The most seats this session can hand out: whatever FullHouse raised the transport to when the
        /// lobby was created. Going past it advertises a seat that cannot be filled.</summary>
        internal static int SeatCeiling
        {
            get { try { return Math.Max(2, LobbyCaps.MaxClients()); } catch { return 4; } }
        }

        internal static bool SetLobbyName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length > 48) name = name.Substring(0, 48);
            if (name.Length == 0) name = LobbyCoordinator.LocalPersonaName();
            return Write(LobbyCoordinator.KeyLobbyName, name);
        }

        /// <summary>Set or clear the join password. An empty string clears it, which is why this is one call and not
        /// two: "no password" is a value, not a separate operation.</summary>
        internal static bool SetPassword(string password)
        {
            password = (password ?? "").Trim();
            bool has = password.Length > 0;
            // Read the old flag BEFORE writing anything. The rollback below used to read it back off the lobby after
            // the write it was undoing, so it faithfully restored the new value and the lobby was left claiming a
            // password whose hash had never landed - every joiner refused, no way to get in.
            string hadFlag = Read(LobbyCoordinator.KeyPassword);
            if (!Write(LobbyCoordinator.KeyPassword, has ? "1" : "0")) return false;
            // The hash is what a joiner compares against, so it has to land or the gate silently opens.
            if (!Write(LobbyCoordinator.KeyPwHash, has ? LobbyCoordinator.HashPassword(password) : ""))
            {
                Write(LobbyCoordinator.KeyPassword, hadFlag);   // put the flag back as it was
                return false;
            }
            _password = password;
            Core.Log?.Msg(has ? "[lobby] join password set." : "[lobby] join password removed.");
            return true;
        }

        internal static bool SetPublic(bool pub)
        {
            if (!IsHost) return false;
            try
            {
                SteamMatchmaking.SetLobbyType(Sid, pub ? ELobbyType.k_ELobbyTypePublic : ELobbyType.k_ELobbyTypeFriendsOnly);
            }
            catch (Exception e) { Core.Log?.Warning("[lobby] could not change the lobby type: " + e.Message); return false; }
            bool ok = Write(LobbyCoordinator.KeyVisibility, pub ? "pub" : "priv");
            if (ok) Core.Log?.Msg($"[lobby] visibility is now {(pub ? "public" : "friends only")}.");
            return ok;
        }

        /// <summary>Change the seat count. Clamped to 2..ceiling rather than refused, so a slider that overshoots
        /// still does the sensible thing. Returns what Steam actually accepted.</summary>
        internal static int SetMaxPlayers(int seats)
        {
            if (!IsHost) return MaxPlayers;
            int wanted = Math.Max(2, Math.Min(SeatCeiling, seats));
            try { SteamMatchmaking.SetLobbyMemberLimit(Sid, wanted); }
            catch (Exception e) { Core.Log?.Warning("[lobby] could not change the member limit: " + e.Message); return MaxPlayers; }

            // Advertise what Steam gave us, never what we asked for: a refused limit that we still advertise lets
            // joiners queue for a seat that does not exist.
            int real = MaxPlayers;
            Write(LobbyCoordinator.KeyMax, real.ToString());
            if (real != wanted) Core.Log?.Warning($"[lobby] Steam kept the limit at {real} (asked for {wanted}).");
            else Core.Log?.Msg($"[lobby] seats are now {real}.");
            return real;
        }

        /// <summary>
        /// Require a matching mod set, or stop requiring it. Moves the advertisement and the kicking together.
        /// </summary>
        /// <remarks>
        /// One switch in the app, so one switch here. Writing the key alone was the whole of this method, and the
        /// half it left behind was the half that removes people: a host who turned the requirement off went on
        /// kicking every unsynced joiner for the rest of the session, with the app showing "off".
        ///
        /// The gate checks joiners against the lobby's own published manifest hash, which is what a synced client
        /// announces in its member data - so this arms a lobby published from the pause menu just as well as a
        /// session Side Hustle started. A lobby with no published manifest has nothing to check against; then the
        /// requirement is refused rather than advertised, because a rule that cannot be applied is a lie on a card
        /// somebody else is reading.
        /// </remarks>
        internal static bool SetEnforce(bool enforce)
        {
            string mhash = Read(VanillaLobby.KeyMHash);
            if (enforce && string.IsNullOrEmpty(mhash))
            {
                Core.Log?.Warning("[lobby] this lobby publishes no mod list, so a mod-set requirement would have "
                                  + "nothing to check joiners against - not switching it on.");
                return false;
            }
            if (!Write(VanillaLobby.KeyEnforce, enforce ? "1" : "0")) return false;
            SyncCoordinator.SetEnforce(enforce, mhash);
            Core.Log?.Msg($"[lobby] mod-set requirement is now {(enforce ? "on" : "off")}.");
            return true;
        }

        /// <summary>One person in the lobby, as the app lists them.</summary>
        internal sealed class Member
        {
            internal ulong SteamId;
            internal string Name = "";
            internal bool IsHost;
            internal bool IsSelf;
            internal bool IsFriend;
        }

        /// <summary>Steam friendship, cached for the process. The roster is rebuilt on every poll and a friendship
        /// does not change mid-session; an unavailable Steam call answers "not a friend", because a missing badge is
        /// a non-event and a wrong one is a claim about who somebody is. Same rule PropHunt's roster follows.</summary>
        private static readonly System.Collections.Generic.Dictionary<ulong, bool> _friends =
            new System.Collections.Generic.Dictionary<ulong, bool>();

        private static bool IsFriend(ulong id)
        {
            if (id == 0UL) return false;
            if (_friends.TryGetValue(id, out bool known)) return known;
            bool friend = false;
            try
            {
                friend = SteamFriends.GetFriendRelationship(new CSteamID(id))
                         == EFriendRelationship.k_EFriendRelationshipFriend;
            }
            catch (Exception e) { Core.Log?.Warning("[lobby] could not read a Steam friend relationship: " + e.Message); }
            _friends[id] = friend;
            return friend;
        }

        /// <summary>
        /// Everyone currently in the Steam lobby, host first, then friends, then the rest.
        ///
        /// Read straight from Steam rather than from the game's player list on purpose: this has to work for a
        /// client too, and it has to be right in the seconds after somebody joins but before their avatar exists.
        /// </summary>
        internal static System.Collections.Generic.List<Member> Roster()
        {
            var list = new System.Collections.Generic.List<Member>();
            try
            {
                CSteamID sid = Sid;
                if (sid.m_SteamID == 0UL) return list;
                ulong owner = 0UL, me = 0UL;
                try { owner = SteamMatchmaking.GetLobbyOwner(sid).m_SteamID; } catch { }
                try { me = SteamUser.GetSteamID().m_SteamID; } catch { }

                int n = SteamMatchmaking.GetNumLobbyMembers(sid);
                for (int i = 0; i < n; i++)
                {
                    CSteamID m = SteamMatchmaking.GetLobbyMemberByIndex(sid, i);
                    if (m.m_SteamID == 0UL) continue;
                    string name = "";
                    try { name = SteamFriends.GetFriendPersonaName(m); } catch { }
                    list.Add(new Member
                    {
                        SteamId = m.m_SteamID,
                        // A Steam name can be empty or unresolved for a member we have never seen; showing the id
                        // beats showing a blank row.
                        Name = string.IsNullOrWhiteSpace(name) ? m.m_SteamID.ToString() : name,
                        IsHost = m.m_SteamID == owner,
                        IsSelf = m.m_SteamID == me,
                        IsFriend = m.m_SteamID != me && IsFriend(m.m_SteamID),
                    });
                }
                list.Sort((a, b) =>
                {
                    if (a.IsHost != b.IsHost) return a.IsHost ? -1 : 1;
                    if (a.IsFriend != b.IsFriend) return a.IsFriend ? -1 : 1;
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
            }
            catch (Exception e) { Core.Log?.Warning("[lobby] could not read the member list: " + e.Message); }
            return list;
        }

        /// <summary>The host's Steam name, for a client who wants to know whose game this is.</summary>
        internal static string HostName
        {
            get
            {
                string advertised = Read(LobbyCoordinator.KeyHostName);
                if (!string.IsNullOrWhiteSpace(advertised)) return advertised;
                try { return SteamFriends.GetFriendPersonaName(SteamMatchmaking.GetLobbyOwner(Sid)); }
                catch { return ""; }
            }
        }

        /// <summary>Which game branch this lobby advertises (sh_rt): "il2cpp", "mono", or empty from an older host.</summary>
        internal static string Runtime => Read(LobbyCoordinator.KeyRuntime);

        /// <summary>
        /// Take strangers' messages, or stop. Writes the preference AND the lobby key in one call.
        ///
        /// Both, because they answer different questions: the preference is what the relay checks when a packet
        /// lands, the key is what a joiner reads to decide whether offering a Chat button is honest. Setting only
        /// the first leaves a button in someone else's browser that opens onto a host who will drop every line.
        /// </summary>
        internal static void SetAccepting(bool accepting)
        {
            Config.Preferences.AcceptStrangerMessages = accepting;
            if (IsHost) Write(VanillaLobby.KeyMessages, accepting ? "1" : "0");
            Core.Log?.Msg("[lobby] messages from strangers are now " + (accepting ? "on" : "off") + ".");
        }

        /// <summary>
        /// Whether the game will actually let anyone in. Vanilla starts a joiner's load only when the lobby's own
        /// "ready" (or "host_loading") key says so, and a lobby opened after the host was already playing does not
        /// get one on its own - which is why a published session could look perfect and admit nobody. The host sees
        /// the answer here instead of finding out from somebody else's silence.
        /// </summary>
        internal static bool JoinableNow => Read("ready") == "true" || Read("host_loading") == "true";

        /// <summary>Forget the remembered plaintext and the friendship cache. Called when a session ends - the next
        /// one is a different lobby, and showing the previous password there would be wrong as well as careless.</summary>
        internal static void Reset()
        {
            _password = "";
            _friends.Clear();
        }
    }
}
