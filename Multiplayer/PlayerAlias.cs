using System;
using HarmonyLib;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppSteamworks;

namespace SideHustle.Multiplayer
{
    /// <summary>
    /// Lets a player show a custom display name (alias) to everyone else for the duration of a Side Hustle
    /// session, instead of their real Steam persona name - a privacy option for public co-op.
    ///
    /// The vanilla client names itself in <c>Player.OnStartClient</c>: it sets <c>PlayerName</c> from
    /// <c>SteamFriends.GetPersonaName()</c> and pushes it via the owner-only ServerRpc
    /// <c>Player.SetPlayerNameAndId_Server(name, steamId)</c> (declared <c>RunLocally</c>), whose relay
    /// (<c>SetPlayerNameAndId_Client</c>) sets the replicated <c>PlayerName</c>, the overhead nametag and the map POI
    /// on every peer including the caller. We simply let that run, then in a POSTFIX on <c>OnStartClient</c>
    /// re-push the chosen alias with a second <c>SetPlayerNameAndId_Server(alias, steamId)</c> call. The second call wins,
    /// updating the local owner's own <c>PlayerName</c> (what mods like PropHunt read) and every remote peer.
    ///
    /// We re-send rather than rewrite the outgoing argument in a prefix because a Harmony <c>ref string</c> write
    /// does not reliably propagate through IL2CPP string marshalling; passing the alias as a plain argument to a
    /// fresh call sidesteps that entirely. The REAL Steam id is passed (read back from the already-replicated
    /// <c>PlayerCode</c>), so the host's friend-check keys off the true id and <see cref="PublicLobbyAccess"/>'s
    /// non-friend bypass keeps working. PublicLobbyAccess patches the friend check itself
    /// (<c>PlatformFriends.IsLocalPlayerFriendsWith</c>) rather than this RPC, so the two compose cleanly.
    ///
    /// The name is never written to the save (PlayerData has no name field), so this is non-destructive and resets
    /// to the Steam name on the next spawn once <see cref="Active"/> goes false. Gated by Active, set only while a
    /// Side Hustle session is live and the player chose a non-empty alias.
    /// </summary>
    internal static class PlayerAlias
    {
        private static HarmonyLib.Harmony _harmony;
        private static bool _installed;
        private static string _alias = "";

        /// <summary>True while a Side Hustle session is live AND the local player set a non-empty alias.</summary>
        internal static bool Active;

        /// <summary>The alias currently applied to the local player ("" when not aliasing), so other systems - e.g. the
        /// server-browser host name - can show the same name the in-game nametag uses.</summary>
        internal static string CurrentAlias => Active ? _alias : "";

        /// <summary>Apply the given alias for the coming session. Empty/whitespace keeps the real Steam name.</summary>
        internal static void Enable(string alias)
        {
            _alias = (alias ?? "").Trim();
            EnsureInstalled();
            Active = !string.IsNullOrEmpty(_alias);
            if (Active) Core.Log?.Msg("[mp] custom display name active: \"" + _alias + "\".");
        }

        /// <summary>Stop aliasing when the session ends; the next spawn uses the real Steam name again.</summary>
        internal static void Disable() => Active = false;

        private static void EnsureInstalled()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                _harmony = new HarmonyLib.Harmony("doodesch.sidehustle.playeralias");
                var target = Internal.GameTargets.PlayerOnStartClient.Value;
                if (target == null) return;   // GameTargets logged the missing target
                _harmony.Patch(target, postfix: new HarmonyMethod(
                    typeof(PlayerAlias).GetMethod(nameof(OnStartClientPostfix), AccessTools.all)));
                Core.Log?.Msg("[mp] player alias installed (custom display name in Side Hustle sessions).");
            }
            catch (Exception e) { Core.Log?.Warning("[mp] player-alias patch install failed: " + e.Message); }
        }

        // After the owner named itself with its Steam name, re-push the chosen alias so the replicated PlayerName
        // (local + all peers), overhead nametag and map POI all show it. Owner-only, and only while a session set a
        // non-empty alias. The id is the REAL Steam id (read back from the just-replicated PlayerCode, with a
        // Steamworks fallback) so the host's friend-check still keys off the true id.
        private static void OnStartClientPostfix(Player __instance)
        {
            try
            {
                if (!Active || string.IsNullOrEmpty(_alias) || __instance == null || !__instance.IsOwner) return;

                // SetPlayerNameAndId_Server (SendPlayerNameData before 0.4.7) takes the id as a STRING since 0.4.6f11. It must stay the numeric Steam id:
                // the host's friend-check parses it (PlatformFriends.IsLocalPlayerFriendsWith -> ulong.Parse).
                string id = null;
                try { id = __instance.PlayerCode; }
                catch (Exception e) { Core.Log?.Warning("[mp] alias: PlayerCode unreadable, trying Steam: " + e.Message); }
                if (string.IsNullOrEmpty(id) || id == "0")
                {
                    try { id = SteamUser.GetSteamID().ToString(); }
                    catch (Exception e) { Core.Log?.Warning("[mp] alias: Steam id unreadable: " + e.Message); }
                }
                if (string.IsNullOrEmpty(id) || id == "0")
                {
                    Core.Log?.Warning("[mp] alias not applied: the local player has no Steam id yet.");   // don't push a bogus id
                    return;
                }

                __instance.SetPlayerNameAndId_Server(_alias, id);
                Core.Log?.Msg("[mp] alias pushed to the session: \"" + _alias + "\".");
            }
            catch (Exception e) { Core.Log?.Warning("[mp] alias apply failed: " + e.Message); }
        }
    }
}
