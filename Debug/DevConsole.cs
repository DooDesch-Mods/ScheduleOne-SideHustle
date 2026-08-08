#if DEBUG
using System;
using System.Text;
using HarmonyLib;
using Il2CppSteamworks;
using SideHustle.Multiplayer;
using SideHustle.Phone;
using UnityEngine;

namespace SideHustle.Debugging
{
    /// <summary>
    /// DEBUG-only dev console for Side Hustle. Compiled out of Release.
    ///
    /// Everything here answers a question that cannot be answered from the outside: the lobby keys the game will
    /// not show, and whether a P2P message got anywhere. The chat especially - a message that never arrives looks
    /// exactly like a message nobody sent, and only the counters tell those apart.
    ///
    /// Both <c>Console.SubmitCommand</c> overloads are patched (string and List&lt;string&gt;): depending on the
    /// caller either one may be the prefix that actually fires. Dispatch dedupes per frame and signature so a
    /// command with side effects never runs twice for one submission.
    /// </summary>
    internal static class DevConsole
    {
        private static HarmonyLib.Harmony _harmony;
        private static int _lastFrame = -1;
        private static string _lastSig = "";

        internal static void Install()
        {
            if (_harmony != null) return;
            try
            {
                _harmony = new HarmonyLib.Harmony("doodesch.sidehustle.devconsole");
                var prefixString = new HarmonyMethod(typeof(DevConsole).GetMethod(nameof(SubmitString), AccessTools.all));
                var prefixList = new HarmonyMethod(typeof(DevConsole).GetMethod(nameof(SubmitList), AccessTools.all));

                var consoleType = typeof(Il2CppScheduleOne.Console);
                _harmony.Patch(AccessTools.Method(consoleType, "SubmitCommand", new[] { typeof(string) }), prefix: prefixString);
                _harmony.Patch(
                    AccessTools.Method(consoleType, "SubmitCommand", new[] { typeof(Il2CppSystem.Collections.Generic.List<string>) }),
                    prefix: prefixList);

                Core.Log?.Msg("[dev] console commands ready: shhelp");
            }
            catch (Exception e) { Core.Log?.Warning("[dev] could not patch the console: " + e.Message); }
        }

        private static bool SubmitString(string args)
        {
            try { return !Handle((args ?? "").Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)); }
            catch { return true; }
        }

        private static bool SubmitList(Il2CppSystem.Collections.Generic.List<string> args)
        {
            try
            {
                if (args == null || args.Count == 0) return true;
                var parts = new string[args.Count];
                for (int i = 0; i < args.Count; i++) parts[i] = args[i];
                return !Handle(parts);
            }
            catch { return true; }
        }

        private static readonly string[][] Listing =
        {
            new[] { "shhelp", "list these commands" },
            new[] { "shdiag", "lobby, publish and join state as the mod sees it" },
            new[] { "shroster", "everyone in the Steam lobby, with the host and friend flags" },
            new[] { "shchat", "shchat <steamid> <text> - send a P2P message to someone outside the lobby" },
            new[] { "shchatdiag", "what the P2P relay has actually seen: sessions, packets, threads" },
            new[] { "shchatpeer", "shchatpeer <steamid> - Steam's own view of that P2P session" },
            new[] { "shopenlobby", "open a lobby mid-session, the way the pause menu's Invite does" },
            new[] { "shpublish", "flip live publishing - the same switch as the button and the app" },
            new[] { "shphone", "shphone [up|down] - raise the phone on its home screen, with no app open" },
            new[] { "shloadmod", "shloadmod <file.dll> - load a mod NOW, from Mods/_late (the mod-gate spike)" },
            new[] { "shmods", "which mods are registered, and which sit unloaded in Mods/_late" },
        };

        private static bool Handle(string[] parts)
        {
            if (parts.Length == 0) return false;
            string cmd = parts[0].ToLowerInvariant();

            bool ours = false;
            foreach (string[] one in Listing) if (one[0] == cmd) { ours = true; break; }
            if (!ours) return false;

            string sig = string.Join(" ", parts);
            int frame = Time.frameCount;
            if (frame == _lastFrame && sig == _lastSig) return true;   // the other overload already ran it
            _lastFrame = frame; _lastSig = sig;

            try
            {
                switch (cmd)
                {
                    case "shhelp": Help(); break;
                    case "shdiag": Diag(); break;
                    case "shroster": RosterDump(); break;
                    case "shchat": Chat(parts); break;
                    case "shchatdiag": ChatDiag(); break;
                    case "shchatpeer": PeerState(parts); break;
                    case "shopenlobby": OpenLobby(); break;
                    case "shpublish": Publish(); break;
                    case "shphone": Phone(parts); break;
                    case "shloadmod": LoadModLate(parts); break;
                    case "shmods": ListMods(); break;
                }
            }
            catch (Exception e) { Core.Log?.Warning("[dev] " + cmd + " failed: " + e); }
            return true;
        }

        private static void Help()
        {
            Core.Log?.Msg("[dev] Side Hustle console commands:");
            foreach (string[] one in Listing) Core.Log?.Msg("  " + one[0].PadRight(12) + one[1]);
        }

        private static void Diag()
        {
            Core.Log?.Msg("[dev] shdiag:");
            Core.Log?.Msg("  inLobby=" + LobbyControls.InLobby + " host=" + LobbyControls.IsHost
                + " members=" + LobbyControls.Members + "/" + LobbyControls.MaxPlayers
                + " ceiling=" + LobbyControls.SeatCeiling);
            Core.Log?.Msg("  name='" + LobbyControls.LobbyName + "' public=" + LobbyControls.IsPublic
                + " password=" + LobbyControls.HasPassword + " enforce=" + LobbyControls.Enforcing);
            Core.Log?.Msg("  runtime=" + LobbyControls.Runtime + " hostName='" + LobbyControls.HostName + "'");
            // The one that explains a lobby nobody can enter: vanilla only starts a joiner's load once this is set.
            Core.Log?.Msg("  joinable=" + LobbyControls.JoinableNow
                + " canPublish=" + Sync.LivePublish.CanPublish + " published=" + Sync.LivePublish.IsPublished);
            Core.Log?.Msg("  lobbyId=" + LobbyCoordinator.CurrentLobbyId + " me=" + LocalId());
        }

        private static void RosterDump()
        {
            var list = LobbyControls.Roster();
            Core.Log?.Msg("[dev] shroster: " + list.Count + " member(s)");
            foreach (var m in list)
                Core.Log?.Msg("  " + m.SteamId + "  " + m.Name
                    + (m.IsHost ? "  [host]" : "") + (m.IsSelf ? "  [me]" : "") + (m.IsFriend ? "  [friend]" : ""));
        }

        private static void Chat(string[] parts)
        {
            if (parts.Length < 3)
            {
                Core.Log?.Warning("[dev] shchat <steamid> <text>");
                return;
            }
            if (!ulong.TryParse(parts[1], out ulong peer))
            {
                Core.Log?.Warning("[dev] shchat: '" + parts[1] + "' is not a Steam id.");
                return;
            }
            var text = new StringBuilder();
            for (int i = 2; i < parts.Length; i++) { if (text.Length > 0) text.Append(' '); text.Append(parts[i]); }
            bool ok = ChatRelay.Send(peer, text.ToString());
            Core.Log?.Msg("[dev] shchat -> " + peer + ": " + (ok ? "Steam took the packet" : "REFUSED"));
        }

        private static void ChatDiag()
        {
            Core.Log?.Msg("[dev] shchatdiag: installed=" + ChatRelay.Installed
                + " accepting=" + Config.Preferences.AcceptStrangerMessages + " me=" + LocalId());
            Core.Log?.Msg("  sessionRequests=" + ChatRelay.SessionRequests
                + " packetsRead=" + ChatRelay.PacketsRead
                + " rejected=" + ChatRelay.PacketsRejected
                + " sent=" + ChatRelay.PacketsSent);
            var peers = ChatRelay.Peers();
            Core.Log?.Msg("  threads=" + peers.Count);
            foreach (ulong p in peers)
            {
                var thread = ChatRelay.Thread(p);
                Core.Log?.Msg("  " + p + " (" + ChatRelay.NameOf(p) + ") " + thread.Count + " message(s)"
                    + (ChatRelay.IsUnread(p) ? " UNREAD" : ""));
                foreach (var m in thread) Core.Log?.Msg("    " + (m.Mine ? "> " : "< ") + m.Text);
            }
        }

        /// <summary>Steam's own answer about a P2P session, which is the only thing that separates "the packet was
        /// never sent" from "there is no route to that peer".</summary>
        private static void PeerState(string[] parts)
        {
            if (parts.Length < 2 || !ulong.TryParse(parts[1], out ulong peer))
            {
                Core.Log?.Warning("[dev] shchatpeer <steamid>");
                return;
            }
            try
            {
                bool ok = SteamNetworking.GetP2PSessionState(new CSteamID(peer), out P2PSessionState_t s);
                if (!ok) { Core.Log?.Msg("[dev] shchatpeer " + peer + ": Steam knows no session with them."); return; }
                Core.Log?.Msg("[dev] shchatpeer " + peer + ": connecting=" + s.m_bConnectionActive
                    + " connecting=" + s.m_bConnecting + " error=" + s.m_eP2PSessionError
                    + " relay=" + s.m_bUsingRelay + " queuedBytes=" + s.m_nBytesQueuedForSend
                    + " queuedPackets=" + s.m_nPacketsQueuedForSend);
            }
            catch (Exception e) { Core.Log?.Warning("[dev] shchatpeer failed: " + e.Message); }
        }

        /// <summary>
        /// Open a lobby from inside a running session, which is the one order that reproduces the join bug.
        ///
        /// <c>Lobby.CreateLobby</c> is exactly what the pause menu's Invite reaches
        /// (<c>ScheduleOne.Networking/Lobby.cs:110-122</c>, TryOpenInviteInterface), and that button is a mouse
        /// click no automation can supply. A lobby made here has never seen the end of a world load, so vanilla's
        /// <c>OnLobbyCreated</c> leaves "ready" on "false" for good - run shdiag before and after to watch it.
        /// </summary>
        private static void OpenLobby()
        {
            try
            {
                var lobby = Il2CppScheduleOne.DevUtilities.PersistentSingleton<Il2CppScheduleOne.Networking.Lobby>.Instance;
                if (lobby == null) { Core.Log?.Warning("[dev] shopenlobby: no Lobby singleton."); return; }
                if (lobby.IsInLobby) { Core.Log?.Msg("[dev] shopenlobby: already in a lobby."); return; }
                lobby.CreateLobby();
                Core.Log?.Msg("[dev] shopenlobby: asked Steam for a lobby - run shdiag in a second.");
            }
            catch (Exception e) { Core.Log?.Warning("[dev] shopenlobby failed: " + e.Message); }
        }

        private static void Publish()
        {
            if (!Sync.LivePublish.CanPublish)
            {
                Core.Log?.Warning("[dev] shpublish: not eligible (need to host a lobby Side Hustle did not start).");
                return;
            }
            Sync.LivePublish.TogglePublished();
            Core.Log?.Msg("[dev] shpublish: published=" + Sync.LivePublish.IsPublished
                + " joinable=" + LobbyControls.JoinableNow);
        }

        /// <summary>
        /// Take the phone out without opening anything, which is the only way to look at the home screen from a
        /// script. Opening an app raises the phone too, but then the app is covering the icons - and the icons are
        /// exactly what a check of a new app picture needs to see.
        /// </summary>
        private static void Phone(string[] parts)
        {
            bool up = parts.Length < 2 || !parts[1].Equals("down", StringComparison.OrdinalIgnoreCase);
            bool ok = up ? Sideload.Api.PhoneScreen.Raise() : Sideload.Api.PhoneScreen.Lower();
            Core.Log?.Msg("[dev] shphone " + (up ? "up" : "down") + ": " + (ok ? "done" : "the game refused"));
        }

        /// <summary>Where a mod waits when it is meant NOT to load at boot. MelonLoader only scans Mods/ itself, so
        /// a subfolder is enough to hold one back without renaming anything.</summary>
        private static string LateDir()
        {
            try { return System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.ModsDirectory, "_late"); }
            catch { return ""; }
        }

        /// <summary>
        /// The mod-gate spike, as a command rather than a branch: load one mod at the moment the real feature would.
        ///
        /// This is what has to be true before Side Hustle could ever hold mods back until a lobby is picked, and it
        /// is a question no amount of reading answers - a mod that misses OnInitializeMelon may still be fine, or may
        /// silently do nothing, and only running it says which.
        /// </summary>
        private static void LoadModLate(string[] parts)
        {
            if (parts.Length < 2) { Core.Log?.Warning("[dev] shloadmod <file.dll>  (from Mods/_late)"); return; }

            string dir = LateDir();
            string file = System.IO.Path.Combine(dir, parts[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? parts[1] : parts[1] + ".dll");
            if (!System.IO.File.Exists(file)) { Core.Log?.Warning("[dev] shloadmod: no such file - " + file); return; }

            int before = MelonLoader.MelonMod.RegisteredMelons.Count;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            MelonLoader.MelonAssembly asm = null;
            try { asm = MelonLoader.MelonAssembly.LoadMelonAssembly(file); }
            catch (Exception e) { Core.Log?.Error("[dev] shloadmod threw: " + e); return; }

            int after = MelonLoader.MelonMod.RegisteredMelons.Count;
            Core.Log?.Msg($"[dev] shloadmod {System.IO.Path.GetFileName(file)}: "
                + (asm == null ? "LoadMelonAssembly returned null" : "loaded")
                + $", melons {before} -> {after}, {watch.ElapsedMilliseconds} ms");

            // LoadMelonAssembly stops at "found the melon". Register() is the separate step that hands out the
            // logger and Harmony instance and fires OnEarlyInitializeMelon / OnInitializeMelon - and it handles a
            // loader whose OnApplicationStart is long gone by calling the late hooks directly, which is exactly the
            // case here. Without it a "loaded" mod sits there doing nothing at all, which is the trap this spike
            // exists to find.
            if (asm?.LoadedMelons != null)
            {
                foreach (var m in asm.LoadedMelons)
                {
                    bool ok = false;
                    try { ok = m.Register(); }
                    catch (Exception e) { Core.Log?.Error("[dev] register threw: " + e); }
                    Core.Log?.Msg($"  {m.Info?.Name} {m.Info?.Version}: Register()={ok} registered={m.Registered}");
                }
                Core.Log?.Msg("[dev] melons now " + MelonLoader.MelonMod.RegisteredMelons.Count);
            }
        }

        private static void ListMods()
        {
            var mods = MelonLoader.MelonMod.RegisteredMelons;
            Core.Log?.Msg("[dev] shmods: " + mods.Count + " registered");
            foreach (var m in mods) Core.Log?.Msg("  " + m.Info?.Name + " " + m.Info?.Version);

            string dir = LateDir();
            if (!System.IO.Directory.Exists(dir)) { Core.Log?.Msg("  (no Mods/_late folder)"); return; }
            string[] waiting = System.IO.Directory.GetFiles(dir, "*.dll");
            Core.Log?.Msg("  waiting in Mods/_late: " + waiting.Length);
            foreach (string f in waiting) Core.Log?.Msg("    " + System.IO.Path.GetFileName(f));
        }

        private static ulong LocalId()
        {
            try { return SteamUser.GetSteamID().m_SteamID; } catch { return 0UL; }
        }
    }
}
#endif
