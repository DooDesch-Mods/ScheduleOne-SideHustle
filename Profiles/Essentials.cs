using System.Linq;
using SideHustle.Shared;

namespace SideHustle.Profiles
{
    /// <summary>
    /// What counts as an ESSENTIAL of every profile: Side Hustle itself and the mods it stands on (its API layer).
    /// Essentials are seeded into every profile, hidden from the removable list, refused by every removal path, and
    /// treated as always-present by the dependency graph (an edge into an essential is never a reason to warn or to
    /// flag anything as missing/orphaned).
    /// </summary>
    internal static class Essentials
    {
        internal static bool IsEssentialFile(string file)
        {
            string f = NormToken(file);
            return f.Contains("sidehustle") || f.Contains("s1api");
        }

        /// <summary>Whether a Thunderstore package name (e.g. "ifBars-S1API") is an essential.</summary>
        internal static bool IsEssentialPackageName(string fullName) => IsEssentialFile(fullName);

        internal static bool IsEssentialRef(ProfileModRef r)
        {
            if (r == null) return false;
            if (r.Source == "thunderstore")
            {
                if (r.Files != null && r.Files.Any(IsEssentialFile)) return true;
                return IsEssentialPackageName(r.FullName);
            }
            return IsEssentialFile(r.File);
        }

        internal static string NormToken(string s) =>
            s == null ? "" : new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
