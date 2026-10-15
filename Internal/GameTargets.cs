using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2CppScheduleOne.Equipping;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.Platform;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.PlayerScripts.Health;
using Il2CppScheduleOne.Quests;
using Il2CppScheduleOne.UI.Multiplayer;

namespace SideHustle.Internal
{
    /// <summary>
    /// One game member Side Hustle reaches by NAME at runtime (a Harmony target, a non-public setter, a type looked up
    /// from a string). These compile cleanly against any game version and only break when the game runs, so each is
    /// resolved here, once, and a missing one is reported once as
    /// <c>[compat] Type.Member not found - feature disabled</c>. The feature that owns the target checks
    /// <see cref="Found"/> and turns itself off; nothing else is affected.
    /// </summary>
    internal sealed class GameTarget<T> where T : class
    {
        private readonly Func<T> _resolve;
        private T _value;
        private bool _resolved;

        internal string Name { get; }
        internal string Feature { get; }

        internal GameTarget(string name, string feature, Func<T> resolve)
        {
            Name = name;
            Feature = feature;
            _resolve = resolve;
        }

        /// <summary>The resolved member, or null when the game no longer has it.</summary>
        internal T Value
        {
            get
            {
                if (_resolved) return _value;
                _resolved = true;
                try { _value = _resolve(); }
                catch (Exception e) { Core.Log?.Warning($"[compat] {Name} lookup threw: {e.Message}"); _value = null; }
                if (_value == null) Core.Log?.Warning($"[compat] {Name} not found - {Feature} disabled");
                return _value;
            }
        }

        internal bool Found => Value != null;
    }

    /// <summary>
    /// The registry of every string-named game target. <see cref="ResolveAll"/> runs once at startup so a game update
    /// that renamed one shows up in the log right away, not the first time a player touches the feature.
    /// </summary>
    internal static class GameTargets
    {
        private static readonly List<Action> _probes = new List<Action>();
        private static readonly List<Func<bool>> _found = new List<Func<bool>>();

        private static GameTarget<MethodBase> Method(string name, string feature, Func<MethodBase> resolve) =>
            Register(new GameTarget<MethodBase>(name, feature, resolve));

        private static GameTarget<T> Register<T>(GameTarget<T> t) where T : class
        {
            _probes.Add(() => { var _ = t.Value; });
            _found.Add(() => t.Found);
            return t;
        }

        private static MethodBase Instance(Type t, string name, Type[] args = null) =>
            args == null ? AccessTools.Method(t, name) : AccessTools.Method(t, name, args);

        // --- client exit guard ---
        internal static readonly GameTarget<MethodBase> LoadManagerExitToMenu =
            Method("LoadManager.ExitToMenu", "client exit guard", () => Instance(typeof(LoadManager), "ExitToMenu"));

        // IsGameLoaded / IsLoading have protected setters; the interop wrapper exposes them as set_ methods.
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static readonly GameTarget<MethodBase> LoadManagerSetIsGameLoaded =
            Method("LoadManager.set_IsGameLoaded", "client exit recovery", () => typeof(LoadManager).GetMethod("set_IsGameLoaded", AnyInstance));
        internal static readonly GameTarget<MethodBase> LoadManagerSetIsLoading =
            Method("LoadManager.set_IsLoading", "stuck-load recovery", () => typeof(LoadManager).GetMethod("set_IsLoading", AnyInstance));

        // --- lobby panel buttons ---
        internal static readonly GameTarget<MethodBase> LobbyInterfaceUpdateButtons =
            Method("LobbyInterface.UpdateButtons", "invite button for clients", () => Instance(typeof(LobbyInterface), "UpdateButtons"));
        internal static readonly GameTarget<MethodBase> LobbyInterfaceLateUpdate =
            Method("LobbyInterface.LateUpdate", "invite button for clients and the live-publish button", () => Instance(typeof(LobbyInterface), "LateUpdate"));

        // --- player alias ---
        internal static readonly GameTarget<MethodBase> PlayerOnStartClient =
            Method("Player.OnStartClient", "custom display name", () => Instance(typeof(Player), "OnStartClient"));

        // --- gamemode hygiene ---
        internal static readonly GameTarget<MethodBase> PlayerPlayerLoaded =
            Method("Player.PlayerLoaded", "skip intro", () => Instance(typeof(Player), "PlayerLoaded"));
        internal static readonly GameTarget<MethodBase> QuestBegin =
            Method("Quest.Begin", "vanilla quest blocking", () => Instance(typeof(Quest), "Begin"));
        internal static readonly GameTarget<MethodBase> SaveManagerSave =
            Method("SaveManager.Save()", "save blocking", () => Instance(typeof(SaveManager), nameof(SaveManager.Save), Type.EmptyTypes));
        internal static readonly GameTarget<MethodBase> SaveManagerSavePath =
            Method("SaveManager.Save(string)", "save blocking", () => Instance(typeof(SaveManager), nameof(SaveManager.Save), new[] { typeof(string) }));
        internal static readonly GameTarget<MethodBase> NpcAwarenessNoiseEvent =
            Method("NPCAwareness.NoiseEvent", "NPC gunfire reactions off", () => Instance(typeof(NPCAwareness), "NoiseEvent"));
        internal static readonly GameTarget<MethodBase> NpcAwarenessVisionEvent =
            Method("NPCAwareness.VisionEvent", "NPC gunfire reactions off", () => Instance(typeof(NPCAwareness), "VisionEvent"));
        internal static readonly GameTarget<MethodBase> NpcSendImpact =
            Method("NPC.SendImpact", "NPC gunfire reactions off", () => Instance(typeof(NPC), "SendImpact"));
        internal static readonly GameTarget<MethodBase> RangedWeaponCheckAimingAtNpc =
            Method("Equippable_RangedWeapon.CheckAimingAtNPC", "NPC gunfire reactions off", () => Instance(typeof(Equippable_RangedWeapon), "CheckAimingAtNPC"));
        internal static readonly GameTarget<MethodBase> PlayerHealthTakeDamage =
            Method("PlayerHealth.TakeDamage", "player death off", () => Instance(typeof(PlayerHealth), nameof(PlayerHealth.TakeDamage), new[] { typeof(float), typeof(bool), typeof(bool) }));
        internal static readonly GameTarget<MethodBase> PlayerHealthSendDie =
            Method("PlayerHealth.SendDie", "player death off", () => Instance(typeof(PlayerHealth), nameof(PlayerHealth.SendDie)));

        // --- public lobbies ---
        internal static readonly GameTarget<MethodBase> PlatformFriendsIsLocalPlayerFriendsWith =
            Method("PlatformFriends.IsLocalPlayerFriendsWith", "joining a hosted lobby without being a Steam friend", () => Instance(typeof(PlatformFriends), "IsLocalPlayerFriendsWith"));

        private const string AuthTypeName = "Il2CppScheduleOne.Networking.FishNetSteamAuthenticator";
        internal static readonly GameTarget<Type> AuthenticatorType = Register(new GameTarget<Type>(
            "FishNetSteamAuthenticator", "the game's own anyone-may-join mode", () =>
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = null;
                    try { t = asm.GetType(AuthTypeName, false); }
                    catch (Exception e) { Core.Log?.Warning($"[compat] {asm.GetName().Name}: type lookup failed: {e.Message}"); }
                    if (t != null) return t;
                }
                return null;
            }));
        internal static readonly GameTarget<MethodInfo> AuthenticatorSetAuthMode = Register(new GameTarget<MethodInfo>(
            "FishNetSteamAuthenticator.SetAuthMode", "the game's own anyone-may-join mode", () =>
                AuthenticatorType.Value?.GetMethod("SetAuthMode", BindingFlags.Public | BindingFlags.Instance)));
        internal static readonly GameTarget<Type> AuthenticatorModeEnum = Register(new GameTarget<Type>(
            "FishNetSteamAuthenticator.ESteamAuthMode", "the game's own anyone-may-join mode", () =>
                AuthenticatorType.Value?.GetNestedType("ESteamAuthMode")));

        // --- MelonLoader (not the game, but the same failure shape: a member reached by name) ---
        internal static readonly GameTarget<PropertyInfo> AdditionalDependencyNames = Register(new GameTarget<PropertyInfo>(
            "MelonAdditionalDependenciesAttribute.AssemblyNames", "declared mod dependencies", () =>
                typeof(MelonLoader.MelonAdditionalDependenciesAttribute).GetProperty("AssemblyNames")));
        internal static readonly GameTarget<PropertyInfo> OptionalDependencyNames = Register(new GameTarget<PropertyInfo>(
            "MelonOptionalDependenciesAttribute.AssemblyNames", "declared mod dependencies", () =>
                typeof(MelonLoader.MelonOptionalDependenciesAttribute).GetProperty("AssemblyNames")));

        /// <summary>The assembly names a MelonLoader dependency attribute declares, or none for any other attribute.</summary>
        internal static string[] DeclaredDependencyNames(object attribute)
        {
            try
            {
                if (attribute is MelonLoader.MelonAdditionalDependenciesAttribute)
                    return AdditionalDependencyNames.Value?.GetValue(attribute) as string[] ?? Array.Empty<string>();
                if (attribute is MelonLoader.MelonOptionalDependenciesAttribute)
                    return OptionalDependencyNames.Value?.GetValue(attribute) as string[] ?? Array.Empty<string>();
            }
            catch (Exception e) { Core.Log?.Warning("[compat] reading a dependency attribute failed: " + e.Message); }
            return Array.Empty<string>();
        }

        /// <summary>Resolve every target now and log one summary line. Call once at startup.</summary>
        internal static void ResolveAll()
        {
            foreach (var p in _probes) p();
            int ok = 0;
            foreach (var f in _found) if (f()) ok++;
            string msg = $"[compat] {ok} of {_found.Count} game targets resolved.";
            if (ok == _found.Count) Core.Log?.Msg(msg); else Core.Log?.Warning(msg);
        }
    }
}
