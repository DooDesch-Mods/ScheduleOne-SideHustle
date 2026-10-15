using System;
using System.Linq;
using Il2CppScheduleOne.DevUtilities;   // PersistentSingleton<>
using Il2CppScheduleOne.Networking;     // Lobby

namespace SideHustle.Multiplayer
{
    /// <summary>Resolves the maximum players the current install can seat, to bound the host player-count slider.</summary>
    internal static class LobbyCaps
    {
        private const int Vanilla = 4;
        private const int BiggerLobbiesCap = 20;   // BiggerLobbies' fixed Constants.MAX_PLAYERS

        /// <summary>
        /// The seat cap for a new lobby. Ground truth is the seat array inside the game's SteamLobbyService, which
        /// FullHouse (and any other cap mod) resizes to its cap; before that array exists, our own embedded FullHouse
        /// engine already knows the effective cap. Falls back to detecting the BiggerLobbies melon (fixed 20), then
        /// the vanilla 4.
        /// </summary>
        internal static int MaxClients()
        {
            try
            {
                int seats = DooDesch.FullHouse.Lobbies.SeatCount;
                if (seats >= 2) return seats;
            }
            catch (Exception e) { Core.Log?.Msg("[mp] FullHouse seat count is not available yet: " + e.Message); }

            try
            {
                int cap = DooDesch.FullHouse.Lobbies.EffectiveCap;
                if (cap >= 2) return cap;
            }
            catch (Exception e) { Core.Log?.Msg("[mp] FullHouse effective cap is not available: " + e.Message); }

            try
            {
                if (Mods.ModInventory.Loaded().Any(m => Mods.ModInventory.MatchesAny(m, "BiggerLobbies")))
                    return BiggerLobbiesCap;
            }
            catch (Exception e) { Core.Log?.Warning("[mp] could not check for BiggerLobbies; using the vanilla seat count: " + e.Message); }

            return Vanilla;
        }
    }
}
