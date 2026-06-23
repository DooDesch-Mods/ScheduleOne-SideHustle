using System;
using UnityEngine;

namespace SideHustle
{
    /// <summary>
    /// Everything a gamemode mod tells Side Hustle about itself. Pass one of these to
    /// <see cref="API.Register"/> (usually from your mod's OnInitializeMelon). Only <see cref="Id"/>,
    /// <see cref="DisplayName"/> and the launch callback for your <see cref="Support"/> mode are required;
    /// every multiplayer field is optional so a singleplayer-only mod registers minimally.
    /// </summary>
    public sealed class GamemodeDescriptor
    {
        // --- Identity (required) ---

        /// <summary>Stable, unique key (e.g. "doodesch.inkubator"). Used for de-duplication and, later, lobby filtering.</summary>
        public string Id;

        /// <summary>Name shown in the gamemode list.</summary>
        public string DisplayName;

        /// <summary>Short one-line description shown under the name.</summary>
        public string Description;

        /// <summary>Author shown in the list (optional).</summary>
        public string Author;

        // --- Presentation (optional) ---

        /// <summary>Icon sprite for the list row. If null, Side Hustle builds one from <see cref="IconTex"/>.</summary>
        public Sprite Icon;

        /// <summary>Icon texture; Side Hustle wraps it in a Sprite when <see cref="Icon"/> is null.</summary>
        public Texture2D IconTex;

        // --- Behaviour ---

        /// <summary>Which play modes this gamemode supports.</summary>
        public GamemodeSupport Support = GamemodeSupport.Singleplayer;

        /// <summary>Whether the gamemode runs as a menu overlay or needs the game world.</summary>
        public GamemodeSurface Surface = GamemodeSurface.MenuSpace;

        // --- Launch callbacks ---

        /// <summary>Invoked to start singleplayer. Required for Singleplayer/Hybrid gamemodes.</summary>
        public Action<LaunchContext> OnLaunchSingleplayer;

        /// <summary>Invoked to start hosting a multiplayer session. Optional.</summary>
        public Action<LaunchContext> OnHostMultiplayer;

        /// <summary>Invoked to join a multiplayer session. Optional.</summary>
        public Action<LaunchContext> OnJoinMultiplayer;

        /// <summary>Invoked when Side Hustle tears the gamemode down (e.g. the player backs out). Optional.</summary>
        public Action<LaunchContext> OnExitToHub;

        /// <summary>True when this descriptor can be launched in singleplayer.</summary>
        public bool AllowsSingleplayer => Support == GamemodeSupport.Singleplayer || Support == GamemodeSupport.Hybrid;

        /// <summary>True when this descriptor exposes multiplayer (host/join).</summary>
        public bool AllowsMultiplayer => Support == GamemodeSupport.Multiplayer || Support == GamemodeSupport.Hybrid;
    }
}
