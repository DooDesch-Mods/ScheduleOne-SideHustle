using System;
using HarmonyLib;
using Il2CppScheduleOne.Platform;

namespace SideHustle.Multiplayer
{
    /// <summary>
    /// Lets players who are NOT Steam friends of the host join a Side Hustle-hosted lobby.
    ///
    /// Vanilla Schedule I kicks every non-friend: when a joining client sends its name to the host, the server RPC
    /// <c>Player.RpcLogic___SendPlayerNameData</c> asks <c>PlatformFriends.IsLocalPlayerFriendsWith(id)</c> and, for
    /// anyone who is not a friend, calls <c>Owner.Kick("Not friends with host")</c> a few seconds after the connection
    /// is up. That makes public lobbies useless for anyone outside the host's friends list (the connection establishes
    /// over Steam relay, world data even starts streaming, then the host drops it).
    ///
    /// We patch the friend check itself rather than the RPC. That check has exactly one caller in the whole game - the
    /// kick - so forcing it to "yes" while a Side Hustle lobby hosts is precise and touches nothing else. It is also
    /// the durable target: the RPC's name carries a FishNet hash that changes whenever its signature does (it already
    /// broke once, going from <c>_586648380</c> to <c>_1988918489</c> when the id parameter turned into a string),
    /// while <c>IsLocalPlayerFriendsWith</c> is a plain static with a stable name.
    ///
    /// The RPC only executes on the server, so this is host-authoritative and completely inert on clients and outside
    /// a Side Hustle session (gated by <see cref="Active"/>, which only the host sets).
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
                var target = AccessTools.Method(typeof(PlatformFriends), nameof(PlatformFriends.IsLocalPlayerFriendsWith));
                if (target != null)
                {
                    _harmony.Patch(target, postfix: new HarmonyMethod(
                        typeof(PublicLobbyAccess).GetMethod(nameof(FriendCheckPostfix), AccessTools.all)));
                    Core.Log?.Msg("[mp] public-lobby access installed (non-friends may join a hosted lobby).");
                }
                else Core.Log?.Warning("[mp] PlatformFriends.IsLocalPlayerFriendsWith not found - the host will keep kicking non-friends.");
            }
            catch (Exception e) { Core.Log?.Warning("[mp] public-lobby patch install failed: " + e.Message); }
        }

        // While a Side Hustle lobby is hosting, everyone counts as a friend - so the kick branch is never taken and the
        // joiner stays connected. Outside a host session the vanilla answer stands untouched.
        private static void FriendCheckPostfix(ref bool __result)
        {
            if (Active) __result = true;
        }
    }
}
