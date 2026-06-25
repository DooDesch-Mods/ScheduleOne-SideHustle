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

        internal static void RequestReturn(LaunchContext ctx)
        {
            try
            {
                // World gamemodes and any multiplayer session loaded a real world (scene reload), so they return
                // via the multiplayer coordinator (ExitToMenu + reopen the hub). MenuSpace singleplayer overlays
                // just restore the menu in place via the Hub's handler.
                bool worldOrMp = ctx != null && (ctx.IsHost != null
                                                 || (ctx.Descriptor != null && ctx.Descriptor.Surface == GamemodeSurface.World));
                if (worldOrMp) SideHustle.Multiplayer.MultiplayerCoordinator.ReturnFromSession(ctx);
                else ReturnHandler?.Invoke(ctx);
            }
            catch (Exception e) { Core.Log?.Warning("ReturnToHub failed: " + e.Message); }
        }
    }
}
