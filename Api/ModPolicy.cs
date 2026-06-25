namespace SideHustle
{
    /// <summary>
    /// An optional per-gamemode policy that controls which OTHER mods may stay loaded while the gamemode runs.
    /// When a gamemode with a policy is launched, Side Hustle disables every loaded mod that is not allowed and
    /// enables the ones the gamemode requires, after a confirmation that lists exactly what changes. Applying it
    /// needs a game restart (MelonLoader cannot cleanly unload a mod at runtime); Side Hustle restarts for you and
    /// continues into the gamemode, then restores your original mods when you leave it.
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
