using System;
using System.Collections.Generic;

namespace SideHustle
{
    /// <summary>
    /// Feature names for <see cref="API.Has"/>. Ask for a feature instead of comparing version numbers: it says
    /// exactly what you need, and it stays true in every later release. Every name here is true on the Side Hustle
    /// that ships it. The constants are compiled into your mod, so pass them to <c>Has</c>; do not read them to
    /// learn what is running.
    /// </summary>
    public static class ApiFeatures
    {
        /// <summary>Host-configurable settings on the Host form (<see cref="GamemodeDescriptor.HostSettings"/>,
        /// <see cref="Multiplayer.MultiplayerInfo"/> getters).</summary>
        public const string HostSettings = "host-settings";

        /// <summary>One-click presets for the host settings (<see cref="GamemodeDescriptor.Presets"/>).</summary>
        public const string Presets = "presets";

        /// <summary>Per-gamemode mod policy (<see cref="GamemodeDescriptor.Policy"/>).</summary>
        public const string ModPolicy = "mod-policy";

        /// <summary>The world hygiene flags on <see cref="GamemodeDescriptor"/> (quests, intro, saving, NPC combat,
        /// player death).</summary>
        public const string WorldHygieneFlags = "world-hygiene-flags";

        /// <summary><see cref="API.KickPlayer"/>.</summary>
        public const string KickPlayer = "kick-player";

        /// <summary>Descriptor checks at <see cref="API.Register"/>, logged per field. Since 3.0.0.</summary>
        public const string Validation = "validation";

        /// <summary><see cref="API.Ready"/>, <see cref="API.SessionStarted"/>, <see cref="API.SessionEnded"/> and
        /// <see cref="API.CurrentSession"/>. Since 3.0.0.</summary>
        public const string SessionEvents = "session-events";

        /// <summary><see cref="LaunchContext.Role"/> and <see cref="SessionRole"/>. Since 3.0.0.</summary>
        public const string SessionRole = "session-role";

        /// <summary><see cref="API.PlayerJoined"/>, <see cref="API.PlayerLeft"/> and <see cref="API.Players"/>.
        /// Since 3.0.0.</summary>
        public const string Players = "players";

        /// <summary><see cref="API.ShowNotice"/>. Since 3.0.0.</summary>
        public const string Notices = "notices";

        /// <summary><see cref="API.SendToAll(string, string, byte[])"/>, <see cref="API.SendTo(ulong, string, string, byte[])"/>
        /// and <see cref="API.OnMessage"/>. Since 3.0.0.</summary>
        public const string Messages = "messages";

        /// <summary><see cref="GamemodeDescriptor.IconResource"/>. Since 3.0.0.</summary>
        public const string IconResource = "icon-resource";

        /// <summary>The <see cref="SideHustle.WorldHygiene"/> presets. Since 3.0.0.</summary>
        public const string WorldHygienePresets = "world-hygiene-presets";

        private static readonly HashSet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            HostSettings, Presets, ModPolicy, WorldHygieneFlags, KickPlayer,
            Validation, SessionEvents, SessionRole, Players, Notices, Messages, IconResource, WorldHygienePresets
        };

        internal static bool IsKnown(string feature) => !string.IsNullOrWhiteSpace(feature) && Known.Contains(feature.Trim());
    }
}
