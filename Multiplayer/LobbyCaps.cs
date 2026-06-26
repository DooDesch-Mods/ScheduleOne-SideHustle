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
        /// The seat cap for a new lobby. Ground truth is the game's <c>Lobby.Players</c> array, which BiggerLobbies
        /// resizes to its cap in the Menu scene; falls back to detecting the BiggerLobbies melon (fixed 20), then the
        /// vanilla 4.
        /// </summary>
        internal static int MaxClients()
        {
            try
            {
                var l = PersistentSingleton<Lobby>.Instance;
                if (l != null && l.Players != null && l.Players.Length >= 2) return l.Players.Length;
            }
            catch { /* fall through */ }

            try
            {
                if (Mods.ModInventory.Loaded().Any(m => Mods.ModInventory.MatchesAny(m, "BiggerLobbies")))
                    return BiggerLobbiesCap;
            }
            catch { /* ignore */ }

            return Vanilla;
        }
    }
}
