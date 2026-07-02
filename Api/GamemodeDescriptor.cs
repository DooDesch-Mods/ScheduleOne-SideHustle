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

        // --- World hygiene flags (opt-in; only meaningful for Surface == World) ---

        /// <summary>When true, Side Hustle blocks every vanilla quest from starting for the duration of the session
        /// (a Harmony prefix on Quest.Begin). Keeps a clean slate - no tutorial/dealer quest chains, phone prompts,
        /// or NPC waypoints fighting the gamemode. Quests are host-authoritative so this matters on the host. Default false.</summary>
        public bool BlockVanillaQuests = false;

        /// <summary>Quest titles EXEMPT from <see cref="BlockVanillaQuests"/> - e.g. a gamemode's own guide quest.
        /// Their <c>Quest.Begin</c> is allowed through even while vanilla quests are blocked. Default none.</summary>
        public string[] AllowedQuestTitles;

        /// <summary>When true, Side Hustle skips the new-game intro cutscene + character-creator (sets the player's
        /// HasCompletedIntro before the intro gate runs), so players drop straight into the world. Default false.</summary>
        public bool SkipIntro = false;

        /// <summary>When true, the gamemode always runs as a fresh new game. Side Hustle ALREADY boots a clean scratch
        /// world (clears the scratch folder + copies DefaultSave) on every launch, so this is currently a declarative
        /// no-op documenting that intent; it never overwrites a real save slot. Default false.</summary>
        public bool ForceNewGame = false;

        /// <summary>When true, Side Hustle blocks every vanilla save (both SaveManager.Save overloads) for the duration
        /// of the session. A gamemode runs on a throwaway scratch world and mutates transient state (teleports, locked
        /// doors, swapped appearances); persisting that to disk would corrupt the save. The Save button still appears
        /// but does nothing. Saving works normally again once the session ends. Default false.</summary>
        public bool BlockSaveDuringSession = false;

        /// <summary>When true, Side Hustle stops NPCs from reacting to hunter/player GUNFIRE for the session (no
        /// panic/flee/cover, no civilian police-call, no WANTED pursuit, no bystander weapon-impact) - so a gamemode
        /// can fire weapons without derailing the NPC schedule. Purely subtractive (prefixes on NPCAwareness
        /// NoiseEvent/VisionEvent for the gunshot/discharge cases, NPC.SendImpact, and the ranged-weapon aim scan);
        /// every other NPC awareness (footsteps, deals, vandalism) is untouched. Default false.</summary>
        public bool SuppressNpcCombatReactions = false;

        /// <summary>When true, Side Hustle cancels all vanilla PLAYER death for the session (prefix on
        /// PlayerHealth.TakeDamage + SendDie) - players cannot die to real weapon damage while the session runs.
        /// Use this when elimination is the gamemode's own concern (e.g. a host-validated catch) and vanilla death +
        /// medical-centre respawn would eject a player from the play area. Damage to NPCs/props is untouched. Default false.</summary>
        public bool DisableVanillaPlayerDeath = false;

        // --- Launch callbacks ---

        /// <summary>Invoked to start singleplayer. Required for Singleplayer/Hybrid gamemodes.</summary>
        public Action<LaunchContext> OnLaunchSingleplayer;

        /// <summary>Invoked to start hosting a multiplayer session. Optional.</summary>
        public Action<LaunchContext> OnHostMultiplayer;

        /// <summary>Invoked to join a multiplayer session. Optional.</summary>
        public Action<LaunchContext> OnJoinMultiplayer;

        /// <summary>Invoked when Side Hustle tears the gamemode down (e.g. the player backs out). Optional.</summary>
        public Action<LaunchContext> OnExitToHub;

        // --- Mod policy (optional) ---

        /// <summary>
        /// Optional policy controlling which other mods may stay loaded while this gamemode runs. Null = no
        /// restriction (default). See <see cref="ModPolicy"/>.
        /// </summary>
        public ModPolicy Policy;

        /// <summary>
        /// Optional host-configurable settings. Side Hustle renders these as a form on the Host screen and ships the
        /// chosen values to your launch callbacks via <see cref="LaunchContext.Multiplayer"/>'s config blob. Null = none.
        /// See <see cref="SettingDescriptor"/>.
        /// </summary>
        public SettingDescriptor[] HostSettings;

        /// <summary>
        /// Optional one-click presets for <see cref="HostSettings"/>. Side Hustle shows a picker above the form;
        /// selecting a preset cascades its values into the matching controls (the host can still tweak after).
        /// Null/empty = no picker. See <see cref="SettingPreset"/>.
        /// </summary>
        public SettingPreset[] Presets;

        /// <summary>The assembly that registered this descriptor; its DLL is always kept by a mod policy. Set by
        /// <see cref="API.Register"/>.</summary>
        internal System.Reflection.Assembly OwnerAssembly;

        /// <summary>True when this descriptor can be launched in singleplayer.</summary>
        public bool AllowsSingleplayer => Support == GamemodeSupport.Singleplayer || Support == GamemodeSupport.Hybrid;

        /// <summary>True when this descriptor exposes multiplayer (host/join).</summary>
        public bool AllowsMultiplayer => Support == GamemodeSupport.Multiplayer || Support == GamemodeSupport.Hybrid;
    }
}
