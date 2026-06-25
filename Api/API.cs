using System;
using System.Collections.Generic;
using SideHustle.Internal;

namespace SideHustle
{
    /// <summary>
    /// Public, stable entry point for mods that want to appear as a gamemode in Side Hustle's main-menu list.
    /// Register early (e.g. in your mod's OnInitializeMelon). Registration is load-order independent, so it
    /// does not matter whether Side Hustle or your mod loads first. Reference SideHustle.dll and declare
    /// <c>[assembly: MelonOptionalDependencies("SideHustle")]</c> so the hub loads first and your mod still
    /// loads cleanly if it is absent.
    /// </summary>
    public static class API
    {
        /// <summary>
        /// Register (or replace, by <see cref="GamemodeDescriptor.Id"/>) a gamemode. Replacing an existing id
        /// is allowed and makes hot-reload / double-registration safe.
        /// </summary>
        public static void Register(GamemodeDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (string.IsNullOrWhiteSpace(descriptor.Id)) throw new ArgumentException("GamemodeDescriptor.Id is required.", nameof(descriptor));
            if (string.IsNullOrWhiteSpace(descriptor.DisplayName)) descriptor.DisplayName = descriptor.Id;

            // Remember the registering mod so a mod policy never disables the gamemode's own DLL. The launch
            // callbacks live in the gamemode's own assembly, which is reliable even when registration is routed
            // through a shared helper assembly (where GetCallingAssembly would point at the wrong DLL).
            if (descriptor.OwnerAssembly == null)
            {
                try
                {
                    var cb = descriptor.OnLaunchSingleplayer ?? descriptor.OnHostMultiplayer
                             ?? descriptor.OnJoinMultiplayer ?? descriptor.OnExitToHub;
                    descriptor.OwnerAssembly = cb?.Method?.DeclaringType?.Assembly
                                               ?? System.Reflection.Assembly.GetCallingAssembly();
                }
                catch { try { descriptor.OwnerAssembly = System.Reflection.Assembly.GetCallingAssembly(); } catch { /* ignore */ } }
            }

            bool replaced = Registry.Register(descriptor);
            Core.Log?.Msg(replaced
                ? $"Gamemode re-registered: '{descriptor.DisplayName}' ({descriptor.Id})."
                : $"Gamemode registered: '{descriptor.DisplayName}' ({descriptor.Id}).");
        }

        /// <summary>Remove a previously registered gamemode. Returns true if one was removed.</summary>
        public static bool Unregister(string id) => !string.IsNullOrWhiteSpace(id) && Registry.Unregister(id);

        /// <summary>True once Side Hustle's own OnInitializeMelon has run.</summary>
        public static bool IsReady { get; internal set; }

        /// <summary>The currently registered gamemodes.</summary>
        public static IReadOnlyList<GamemodeDescriptor> Registered => Registry.All;
    }
}
