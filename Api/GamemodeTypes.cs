namespace SideHustle
{
    /// <summary>
    /// Which multiplayer modes a gamemode supports. Side Hustle uses this to decide whether selecting the
    /// gamemode launches straight into singleplayer or first shows the Singleplayer / Host / Join choice.
    /// </summary>
    public enum GamemodeSupport
    {
        /// <summary>Singleplayer only. Selecting it launches immediately.</summary>
        Singleplayer,
        /// <summary>Multiplayer only. Selecting it shows Host / Join.</summary>
        Multiplayer,
        /// <summary>Both. Selecting it shows Singleplayer / Host / Join.</summary>
        Hybrid
    }

    /// <summary>
    /// Where a gamemode runs. <see cref="MenuSpace"/> gamemodes build their own overlay on top of the main
    /// menu and never load a save (e.g. the Inkubator tattoo editor). <see cref="World"/> gamemodes need the
    /// actual game world, so Side Hustle boots a throwaway save before handing control over.
    /// </summary>
    public enum GamemodeSurface
    {
        /// <summary>Runs as an overlay in the menu scene; no save is loaded.</summary>
        MenuSpace,
        /// <summary>Needs the loaded game world; Side Hustle boots a throwaway save first.</summary>
        World
    }
}
