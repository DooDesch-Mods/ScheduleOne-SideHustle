namespace SideHustle
{
    /// <summary>
    /// An optional per-gamemode policy that controls which OTHER mods are active while the gamemode runs.
    /// When a gamemode with a policy is launched, Side Hustle restarts the game into a temporary, isolated profile
    /// that loads ONLY the allowed mods, after a confirmation that lists exactly what changes, then continues into
    /// the gamemode; leaving the gamemode restarts back to your full mod set. Your installed mods are never renamed,
    /// moved or deleted - the profile is a throwaway sandbox - so mod managers stay in sync and a normal launch
    /// always loads everything.
    ///
    /// Mods are matched by their MelonLoader display name OR their DLL file name (case-insensitive, ".dll" optional).
    /// You never need to list the essentials - they are always kept: MelonLoader, S1API, Side Hustle, this gamemode's
    /// own mod, the Mod Manager &amp; Phone App, and (for multiplayer gamemodes) the multiplayer libraries. Mods that a
    /// kept mod depends on are kept too.
    /// </summary>
    public sealed class ModPolicy
    {
        /// <summary>
        /// Extra mods that are allowed to remain loaded alongside this gamemode. Everything loaded that is neither
        /// allowed, required, nor an essential (or a dependency of a kept mod) gets disabled.
        /// </summary>
        public string[] AllowedMods;

        /// <summary>
        /// Mods this gamemode needs. If present but disabled they are enabled; if a required mod is not installed at
        /// all the player is told to install it first. Required mods are implicitly allowed.
        /// </summary>
        public string[] RequiredMods;
    }
}
