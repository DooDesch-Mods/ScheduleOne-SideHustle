using System.Collections.Generic;

namespace SideHustle.Internal
{
    /// <summary>
    /// The live list of registered gamemodes. It is a plain static, so registration is load-order independent:
    /// MelonLoader runs every mod's OnInitializeMelon before any scene loads, so by the time the menu builds
    /// its list this is fully populated, whether the consumer mod loaded before or after Side Hustle.
    /// </summary>
    internal static class Registry
    {
        private static readonly List<GamemodeDescriptor> _gamemodes = new List<GamemodeDescriptor>();

        internal static IReadOnlyList<GamemodeDescriptor> All => _gamemodes;

        /// <summary>A copy of the list, for callers outside Side Hustle that must not see later changes.</summary>
        internal static IReadOnlyList<GamemodeDescriptor> Snapshot() => new List<GamemodeDescriptor>(_gamemodes).AsReadOnly();

        /// <summary>The registered descriptor with this id, or null.</summary>
        internal static GamemodeDescriptor Find(string id)
        {
            int idx = IndexOf(id);
            return idx >= 0 ? _gamemodes[idx] : null;
        }

        /// <summary>True when exactly this descriptor instance is still registered.</summary>
        internal static bool Contains(GamemodeDescriptor descriptor) => descriptor != null && _gamemodes.Contains(descriptor);

        /// <summary>Add or replace by <see cref="GamemodeDescriptor.Id"/>. Returns true if it replaced an existing entry.</summary>
        internal static bool Register(GamemodeDescriptor descriptor)
        {
            int idx = IndexOf(descriptor.Id);
            if (idx >= 0)
            {
                _gamemodes[idx] = descriptor;
                return true;
            }
            _gamemodes.Add(descriptor);
            return false;
        }

        internal static bool Unregister(string id)
        {
            int idx = IndexOf(id);
            if (idx < 0) return false;
            _gamemodes.RemoveAt(idx);
            return true;
        }

        private static int IndexOf(string id)
        {
            for (int i = 0; i < _gamemodes.Count; i++)
                if (string.Equals(_gamemodes[i].Id, id, System.StringComparison.Ordinal)) return i;
            return -1;
        }
    }
}
