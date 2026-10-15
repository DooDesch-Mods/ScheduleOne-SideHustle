using System;
using HarmonyLib;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.PlayerScripts.Health;
using Il2CppScheduleOne.Quests;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Noise;
using Il2CppScheduleOne.Vision;
using Il2CppScheduleOne.Equipping;

namespace SideHustle.Multiplayer
{
    /// <summary>
    /// Applies a gamemode's opt-in world-hygiene flags for the duration of a session. Each flag maps to one or more
    /// Harmony prefixes, installed LAZILY on first use and gated by a static "active" flag, so they only affect a
    /// session that opted in and are completely inert otherwise:
    ///  - SkipIntro: prefix on Player.PlayerLoaded sets HasCompletedIntro=true BEFORE the intro gate runs, so the
    ///    new-game intro cutscene + character creator never start (Player.cs gate on HasCompletedIntro).
    ///  - BlockVanillaQuests: prefix on Quest.Begin returns false, so no vanilla quest activates (on a fresh world
    ///    quests auto-start AFTER load, so blocking Begin catches them all; host-authoritative). Allow-list exempts
    ///    a gamemode's own guide quest.
    ///  - BlockSaveDuringSession: prefixes on both SaveManager.Save overloads skip saving (scratch world; persisting
    ///    transient gamemode state would corrupt the save).
    ///  - SuppressNpcCombatReactions: prefixes on NPCAwareness NoiseEvent/VisionEvent (gunshot/discharge cases only),
    ///    NPC.SendImpact, and Equippable_RangedWeapon.CheckAimingAtNPC - NPCs ignore player gunfire while active.
    ///    Purely subtractive; every other awareness (footsteps, deals, vandalism) is untouched.
    ///  - DisableVanillaPlayerDeath: prefixes on PlayerHealth.TakeDamage + SendDie cancel all player death (the
    ///    gamemode owns elimination; vanilla death would medical-centre-respawn the player out of the play area).
    /// ForceNewGame is a no-op (WorldBoot already boots a fresh scratch world every launch). Patch failures are
    /// caught + logged (the feature just no-ops).
    /// </summary>
    internal static class GamemodeHygiene
    {
        private static HarmonyLib.Harmony _harmony;
        private static bool _installed;

        internal static bool SkipIntroActive;
        internal static bool BlockQuestsActive;
        internal static bool BlockSaveActive;
        internal static bool SuppressNpcCombatActive;
        internal static bool DisablePlayerDeathActive;
        private static readonly System.Collections.Generic.HashSet<string> _allowedQuestTitles = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);

        /// <summary>Activate the descriptor's hygiene flags. Call at session start (before the world loads).</summary>
        internal static void Apply(GamemodeDescriptor desc)
        {
            if (desc == null) return;
            if (!desc.SkipIntro && !desc.BlockVanillaQuests && !desc.BlockSaveDuringSession
                && !desc.SuppressNpcCombatReactions && !desc.DisableVanillaPlayerDeath) return;
            EnsureInstalled();
            SkipIntroActive = desc.SkipIntro;
            BlockQuestsActive = desc.BlockVanillaQuests;
            BlockSaveActive = desc.BlockSaveDuringSession;
            SuppressNpcCombatActive = desc.SuppressNpcCombatReactions;
            DisablePlayerDeathActive = desc.DisableVanillaPlayerDeath;
            _allowedQuestTitles.Clear();
            if (desc.AllowedQuestTitles != null)
                foreach (var t in desc.AllowedQuestTitles) if (!string.IsNullOrEmpty(t)) _allowedQuestTitles.Add(t);
            Core.Log?.Msg($"[hygiene] applied for '{desc.DisplayName}' (skipIntro={SkipIntroActive}, blockQuests={BlockQuestsActive}, " +
                          $"blockSave={BlockSaveActive}, npcCombatOff={SuppressNpcCombatActive}, noDeath={DisablePlayerDeathActive}, allowed={_allowedQuestTitles.Count}).");
        }

        /// <summary>Deactivate (call on session teardown). The patches stay installed but become inert.</summary>
        internal static void Clear()
        {
            SkipIntroActive = false;
            BlockQuestsActive = false;
            BlockSaveActive = false;
            SuppressNpcCombatActive = false;
            DisablePlayerDeathActive = false;
            _allowedQuestTitles.Clear();
        }

        private static void EnsureInstalled()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                _harmony = new HarmonyLib.Harmony("doodesch.sidehustle.hygiene");

                // Each patch is independent: a target the game no longer has (logged by GameTargets) disables only
                // the flag that depends on it.
                PatchPrefix(Internal.GameTargets.PlayerPlayerLoaded, nameof(PlayerLoadedPrefix));
                PatchPrefix(Internal.GameTargets.QuestBegin, nameof(QuestBeginPrefix));
                PatchPrefix(Internal.GameTargets.SaveManagerSave, nameof(SaveGatePrefix));
                PatchPrefix(Internal.GameTargets.SaveManagerSavePath, nameof(SaveGatePrefix));
                PatchPrefix(Internal.GameTargets.NpcAwarenessNoiseEvent, nameof(NoiseEventPrefix));
                PatchPrefix(Internal.GameTargets.NpcAwarenessVisionEvent, nameof(VisionEventPrefix));
                PatchPrefix(Internal.GameTargets.NpcSendImpact, nameof(NpcImpactPrefix));
                PatchPrefix(Internal.GameTargets.RangedWeaponCheckAimingAtNpc, nameof(WeaponAimPrefix));
                PatchPrefix(Internal.GameTargets.PlayerHealthTakeDamage, nameof(TakeDamagePrefix));
                PatchPrefix(Internal.GameTargets.PlayerHealthSendDie, nameof(SendDiePrefix));

                Core.Log?.Msg("[hygiene] patches installed.");
            }
            catch (Exception e) { Core.Log?.Warning("[hygiene] patch install failed: " + e.Message); }
        }

        private static HarmonyMethod Hook(string name) =>
            new HarmonyMethod(typeof(GamemodeHygiene).GetMethod(name, AccessTools.all));

        private static void PatchPrefix(Internal.GameTarget<System.Reflection.MethodBase> target, string prefix)
        {
            var method = target.Value;
            if (method == null) return;   // GameTargets logged the missing target
            try { _harmony.Patch(method, prefix: Hook(prefix)); }
            catch (Exception e) { Core.Log?.Warning($"[hygiene] patching {target.Name} failed - {target.Feature} disabled: {e.Message}"); }
        }

        // The prefixes run on hot paths (every noise, vision and damage event); a repeating failure is logged once per site.
        private static readonly System.Collections.Generic.HashSet<string> _warned = new System.Collections.Generic.HashSet<string>();
        private static void WarnOnce(string site, Exception e)
        {
            if (_warned.Add(site)) Core.Log?.Warning($"[hygiene] {site} failed: {e.Message}");
        }

        // Set the intro flag before PlayerLoaded's `if (!_hasCompletedIntro ...)` gate -> intro never starts.
        // The flag is a private auto-property since 0.4.6f11; the interop wrapper exposes it with a setter.
        private static void PlayerLoadedPrefix(Player __instance)
        {
            try { if (SkipIntroActive && __instance != null) __instance._hasCompletedIntro = true; }
            catch (Exception e) { WarnOnce("PlayerLoaded prefix", e); }
        }

        // Return false = skip Quest.Begin, so no vanilla quest activates while a quest-blocking session is active -
        // EXCEPT quests whose title is on the allow-list (e.g. the gamemode's own guide quest), which begin normally.
        private static bool QuestBeginPrefix(Quest __instance)
        {
            if (!BlockQuestsActive) return true;
            try { if (__instance != null && _allowedQuestTitles.Count > 0 && _allowedQuestTitles.Contains(__instance.title)) return true; }
            catch (Exception e) { WarnOnce("Quest.Begin allow-list check", e); }
            return false;
        }

        // Return false = skip the save while a save-blocking session is active.
        private static bool SaveGatePrefix() => !BlockSaveActive;

        // Skip ONLY the gunfire cases (Gunshot/Explosion noise, DischargingWeapon/Brandishing vision); every other
        // NPC awareness passes through. Matches the source method's parameter names (nEvent / vEvent) for injection.
        private static bool NoiseEventPrefix(NoiseEvent nEvent)
        {
            try
            {
                if (!SuppressNpcCombatActive || nEvent == null) return true;
                if (nEvent.type == ENoiseType.Gunshot || nEvent.type == ENoiseType.Explosion) return false;
            }
            catch (Exception e) { WarnOnce("NoiseEvent prefix", e); }
            return true;
        }

        private static bool VisionEventPrefix(VisionEventReceipt vEvent)
        {
            try
            {
                if (!SuppressNpcCombatActive || vEvent == null) return true;
                if (vEvent.State == EVisualState.DischargingWeapon || vEvent.State == EVisualState.Brandishing) return false;
            }
            catch (Exception e) { WarnOnce("VisionEvent prefix", e); }
            return true;
        }

        // A weapon hit on a bystander NPC (independent of NPCAwareness) - skip the impact entirely while active.
        private static bool NpcImpactPrefix() => !SuppressNpcCombatActive;

        // Merely AIMING at an NPC triggers a reaction scan - skip it while active.
        private static bool WeaponAimPrefix() => !SuppressNpcCombatActive;

        // Cancel all vanilla player damage (and death) while a no-death session is active - the gamemode owns elimination.
        private static bool TakeDamagePrefix() => !DisablePlayerDeathActive;
        private static bool SendDiePrefix() => !DisablePlayerDeathActive;
    }
}
