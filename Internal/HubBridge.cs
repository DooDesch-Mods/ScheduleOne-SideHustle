using System;

namespace SideHustle.Internal
{
    /// <summary>
    /// Thin seam between the public API surface (which a consumer mod holds onto) and the menu-side Hub logic
    /// (built in Phase 1). The Hub installs <see cref="ReturnHandler"/> when the menu scene initializes; until
    /// then <see cref="RequestReturn"/> is a safe no-op. This lets LaunchContext.ReturnToHub compile and work
    /// without the API layer depending on the menu UI.
    /// </summary>
    internal static class HubBridge
    {
        /// <summary>Installed by the Hub. Restores the menu and re-shows the gamemode list.</summary>
        internal static Action<LaunchContext> ReturnHandler;

        internal static void RequestReturn(LaunchContext ctx)
        {
            try { ReturnHandler?.Invoke(ctx); }
            catch (Exception e) { Core.Log?.Warning("ReturnToHub failed: " + e.Message); }
        }
    }
}
