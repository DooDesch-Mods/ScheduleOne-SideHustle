using Il2CppSteamworks;

namespace SideHustle.Multiplayer
{
    /// <summary>
    /// Writes keys onto one Steam lobby while tracking what they cost.
    ///
    /// Steam carries a lobby's WHOLE key/value set in one 8 KB metadata blob, so every key spends from the same
    /// budget - vanilla's own writes included. Past it SetLobbyData simply answers false, and what gets refused is
    /// whatever happened to be written last. Every host path (gamemode and vanilla) writes through this, so a
    /// refusal is always logged with the key, its size and what was already spent.
    /// </summary>
    internal sealed class LobbyDataWriter
    {
        internal const int Budget = 8192;

        /// <summary>Left untouched for vanilla's keys (owner, version, host_loading, ready, load_tutorial), Steam's
        /// per-key overhead, and the renames, seat changes and passwords the host sets later from the phone app. A
        /// lobby that spends its last byte on the mod list is a lobby that cannot be renamed.</summary>
        internal const int Reserve = 1500;

        /// <summary>What this writer may spend before it starts refusing optional keys.</summary>
        internal const int Limit = Budget - Reserve;

        private readonly CSteamID _sid;
        private readonly string _area;

        internal LobbyDataWriter(CSteamID sid, string area)
        {
            _sid = sid;
            _area = area;
        }

        /// <summary>Characters (keys plus values) written through this writer so far.</summary>
        internal int Spent { get; private set; }

        /// <summary>Whether <paramref name="cost"/> more characters still fit under <see cref="Limit"/>.</summary>
        internal bool Fits(int cost) => Spent + cost <= Limit;

        /// <summary>
        /// Write one key. An <paramref name="essential"/> key (the version gate, the mod-set hash) goes to Steam
        /// even when the soft limit is spent, since refusing it breaks joining; any other key past the limit is
        /// refused here. Returns false, with a warning, when the key was refused by us or by Steam.
        /// </summary>
        internal bool Put(string key, string value, bool essential = false)
        {
            value = value ?? "";
            int cost = key.Length + value.Length;
            if (!essential && !Fits(cost))
            {
                Core.Log?.Warning($"[{_area}] lobby key '{key}' ({value.Length} chars) not written: "
                                  + $"{Spent} of {Limit} chars already spent.");
                return false;
            }
            if (SteamMatchmaking.SetLobbyData(_sid, key, value)) { Spent += cost; return true; }
            Core.Log?.Warning($"[{_area}] Steam refused lobby key '{key}' ({value.Length} chars) after {Spent} chars of lobby data.");
            return false;
        }
    }
}
