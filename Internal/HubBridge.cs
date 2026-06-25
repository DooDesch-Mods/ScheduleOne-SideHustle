using System;

namespace SideHustle.Internal
{
    /// <summary>
    /// Thin seam between the public API surface (which a consumer mod holds onto) and the menu-side Hub logic.
    /// The Hub installs <see cref="ReturnHandler"/> when the menu scene initializes; until
    /// then <see cref="RequestReturn"/> is a safe no-op. This lets LaunchContext.ReturnToHub compile and work
    /// without the API layer depending on the menu UI.
    /// </summary>
    internal static class HubBridge
    {
        /// <summary>Installed by the Hub. Restores the menu and re-shows the gamemode list (MenuSpace singleplayer).</summary>
        internal static Action<LaunchContext> ReturnHandler;

        private static bool _returning;   // makes ReturnToHub idempotent (a double call must not double-trigger)

        internal static void RequestReturn(LaunchContext ctx)
        {
            if (_returning) return;
            _returning = true;
            bool restarting = false;
            try
            {
                bool worldOrMp = ctx != null && (ctx.IsHost != null
                                                 || (ctx.Descriptor != null && ctx.Descriptor.Surface == GamemodeSurface.World));

                // If a mod policy is in effect, leaving the gamemode restores the player's original mods. That is a
                // full game restart, so it supersedes the normal return. Run the gamemode's cleanup, and delist/clean
                // any live world or lobby first (the restart itself tears the live session down).
                if (SideHustle.Mods.ModSwitcher.HasRestorePending)
                {
                    restarting = true;
                    try { ctx?.Descriptor?.OnExitToHub?.Invoke(ctx); }
                    catch (Exception e) { Core.Log?.Warning("OnExitToHub threw: " + e.Message); }
                    try
                    {
                        if (worldOrMp)
                        {
                            if (ctx.IsHost == true) SideHustle.Multiplayer.LobbyCoordinator.Unlist();
                            SideHustle.Multiplayer.WorldBoot.CleanupScratch();
                        }
                    }
                    catch (Exception e) { Core.Log?.Warning("policy-return cleanup threw: " + e.Message); }
                    SideHustle.Mods.ModSwitcher.RestoreAndRestart();
                    return;
                }

                // World gamemodes and any multiplayer session loaded a real world (scene reload), so they return
                // via the multiplayer coordinator (ExitToMenu + reopen the hub). MenuSpace singleplayer overlays
                // just restore the menu in place via the Hub's handler.
                if (worldOrMp) SideHustle.Multiplayer.MultiplayerCoordinator.ReturnFromSession(ctx);
                else ReturnHandler?.Invoke(ctx);
            }
            catch (Exception e) { Core.Log?.Warning("ReturnToHub failed: " + e.Message); }
            finally { if (!restarting) _returning = false; }   // keep the guard set across a committed restart
        }
    }
}
