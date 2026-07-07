using System;
using HarmonyLib;
using Il2CppScheduleOne.PlayerScripts;

namespace SideHustle.Multiplayer
{
    /// <summary>
    /// Lets players who are NOT Steam friends of the host join a Side Hustle-hosted lobby.
    ///
    /// Vanilla Schedule I kicks every non-friend: when a joining client sends its name to the host, the server RPC
    /// <c>Player.RpcLogic___SendPlayerNameData</c> checks <c>SteamFriends.GetFriendRelationship</c> and, for anyone
    /// who is not a friend, calls <c>Owner.Kick("Not friends with host")</c> a few seconds after the connection is up.
    /// That makes public lobbies useless for anyone outside the host's friends list (the connection establishes over
    /// Steam relay, world data even starts streaming, then the host drops it).
    ///
    /// While a Side Hustle lobby is hosting, we replace that server RPC body with only its harmless half - the vanilla
    /// method first calls <c>ReceivePlayerNameData</c> (which just broadcasts the joiner's name to all observers, no
    /// gatekeeping) and then does the friend-check kick. Our prefix runs the name broadcast and returns false, so the
    /// kick never happens. The RPC only executes on the server, so this is host-authoritative and completely inert on
    /// clients and outside a Side Hustle session (gated by <see cref="Active"/>, which only the host sets).
    /// </summary>
    internal static class PublicLobbyAccess
    {
        private static HarmonyLib.Harmony _harmony;
        private static bool _installed;

        /// <summary>True only while this process is hosting a Side Hustle lobby - the friend-check kick is bypassed then.</summary>
        internal static bool Active;

        /// <summary>Install the patch (once) and allow non-friends for the duration of the hosted session.</summary>
        internal static void Enable()
        {
            EnsureInstalled();
            Active = true;
        }

        /// <summary>Restore vanilla behaviour (kick non-friends) once the hosted session ends. The patch stays installed but inert.</summary>
        internal static void Disable() => Active = false;

        private static void EnsureInstalled()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                _harmony = new HarmonyLib.Harmony("doodesch.sidehustle.publiclobby");
                var target = AccessTools.Method(typeof(Player), "RpcLogic___SendPlayerNameData_586648380");
                if (target != null)
                {
                    _harmony.Patch(target, prefix: new HarmonyMethod(
                        typeof(PublicLobbyAccess).GetMethod(nameof(SendPlayerNameDataPrefix), AccessTools.all)));
                    Core.Log?.Msg("[mp] public-lobby access installed (non-friends may join a hosted lobby).");
                }
                else Core.Log?.Warning("[mp] Player.RpcLogic___SendPlayerNameData not found - the host will keep kicking non-friends.");
            }
            catch (Exception e) { Core.Log?.Warning("[mp] public-lobby patch install failed: " + e.Message); }
        }

        // Vanilla body = ReceivePlayerNameData(name broadcast) THEN a Steam-friend check that kicks non-friends. While a
        // Side Hustle lobby is hosting, run only the name broadcast and skip the kick, so a non-friend stays connected.
        private static bool SendPlayerNameDataPrefix(Player __instance, string playerName, ulong id)
        {
            if (!Active) return true;   // outside a Side Hustle host session: the vanilla friend-check stands
            try { __instance?.ReceivePlayerNameData(null, playerName, id.ToString()); }
            catch (Exception e) { Core.Log?.Warning("[mp] player-name passthrough failed: " + e.Message); }
            return false;               // skip the vanilla non-friend kick
        }
    }
}
