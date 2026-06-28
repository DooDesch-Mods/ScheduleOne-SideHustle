using System;
using HarmonyLib;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Quests;

namespace SideHustle.Multiplayer
{
    /// <summary>
    /// Applies a gamemode's opt-in world-hygiene flags for the duration of a session:
    ///  - SkipIntro: a Harmony prefix on Player.PlayerLoaded sets HasCompletedIntro=true BEFORE the intro gate
    ///    runs, so the new-game intro cutscene + character creator never start (Player.cs gate on HasCompletedIntro).
    ///  - BlockVanillaQuests: a Harmony prefix on Quest.Begin returns false, so no vanilla quest activates (on a
    ///    fresh world quests auto-start AFTER load, so blocking Begin catches them all; host-authoritative).
    /// Both patches are installed LAZILY on first use and gated by a static "active" flag, so they only affect a
    /// session that opted in and are completely inert otherwise. ForceNewGame is a no-op (WorldBoot already boots a
    /// fresh scratch world every launch). Patch failures are caught + logged (the feature just no-ops).
    /// </summary>
    internal static class GamemodeHygiene
    {
        private static HarmonyLib.Harmony _harmony;
        private static bool _installed;

        internal static bool SkipIntroActive;
        internal static bool BlockQuestsActive;

        /// <summary>Activate the descriptor's hygiene flags. Call at session start (before the world loads).</summary>
        internal static void Apply(GamemodeDescriptor desc)
        {
            if (desc == null) return;
            if (!desc.SkipIntro && !desc.BlockVanillaQuests) return;
            EnsureInstalled();
            SkipIntroActive = desc.SkipIntro;
            BlockQuestsActive = desc.BlockVanillaQuests;
            Core.Log?.Msg($"[hygiene] applied for '{desc.DisplayName}' (skipIntro={SkipIntroActive}, blockQuests={BlockQuestsActive}).");
        }

        /// <summary>Deactivate (call on session teardown). The patches stay installed but become inert.</summary>
        internal static void Clear()
        {
            SkipIntroActive = false;
            BlockQuestsActive = false;
        }

        private static void EnsureInstalled()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                _harmony = new HarmonyLib.Harmony("doodesch.sidehustle.hygiene");

                var playerLoaded = AccessTools.Method(typeof(Player), "PlayerLoaded");
                if (playerLoaded != null)
                    _harmony.Patch(playerLoaded, prefix: new HarmonyMethod(typeof(GamemodeHygiene).GetMethod(nameof(PlayerLoadedPrefix), AccessTools.all)));
                else Core.Log?.Warning("[hygiene] Player.PlayerLoaded not found - SkipIntro will not work.");

                var questBegin = AccessTools.Method(typeof(Quest), "Begin");
                if (questBegin != null)
                    _harmony.Patch(questBegin, prefix: new HarmonyMethod(typeof(GamemodeHygiene).GetMethod(nameof(QuestBeginPrefix), AccessTools.all)));
                else Core.Log?.Warning("[hygiene] Quest.Begin not found - BlockVanillaQuests will not work.");

                Core.Log?.Msg("[hygiene] patches installed.");
            }
            catch (Exception e) { Core.Log?.Warning("[hygiene] patch install failed: " + e.Message); }
        }

        // Set HasCompletedIntro=true before PlayerLoaded's `if (!HasCompletedIntro ...)` gate -> intro never starts.
        private static void PlayerLoadedPrefix(Player __instance)
        {
            try { if (SkipIntroActive && __instance != null) __instance.HasCompletedIntro = true; } catch { }
        }

        // Return false = skip Quest.Begin, so no vanilla quest activates while a quest-blocking session is active.
        private static bool QuestBeginPrefix() => !BlockQuestsActive;
    }
}
