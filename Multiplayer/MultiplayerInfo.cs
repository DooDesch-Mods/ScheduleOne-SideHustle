namespace SideHustle.Multiplayer
{
    /// <summary>
    /// The multiplayer payload carried on <see cref="LaunchContext.Multiplayer"/>. Populated by Side Hustle from
    /// the Steam lobby's namespaced metadata (the <c>sh_*</c> keys). The host fills it from its host options;
    /// a joining client reads it back from the lobby. <see cref="ConfigBlob"/> is a free-form string the host
    /// writes (lobby key <c>sh_config</c>) so a gamemode can ship its own settings to clients with no extra netcode.
    /// </summary>
    public sealed class MultiplayerInfo
    {
        /// <summary>Maximum players the host opened the lobby for.</summary>
        public int MaxPlayers { get; internal set; }

        /// <summary>Display name of the gamemode (lobby key <c>sh_gamemode_name</c>).</summary>
        public string GamemodeName { get; internal set; }

        /// <summary>Steam persona of the host (lobby key <c>sh_host_name</c>).</summary>
        public string HostName { get; internal set; }

        /// <summary>True if the lobby is password-protected (lobby key <c>sh_pw</c> = "1").</summary>
        public bool HasPassword { get; internal set; }

        /// <summary>Free-form per-gamemode payload the host published (lobby key <c>sh_config</c>); may be null.</summary>
        public string ConfigBlob { get; internal set; }

        /// <summary>Salted hash of the join password (lobby key <c>sh_pwhash</c>); used by the client-side join gate.</summary>
        internal string PwHash { get; set; }

        // Lazily decoded view of ConfigBlob for the typed getters below (the host's chosen settings,
        // round-tripped to clients with no extra netcode). Decoded once on first read.
        private System.Collections.Generic.Dictionary<string, string> _config;
        private System.Collections.Generic.Dictionary<string, string> Config =>
            _config ?? (_config = ConfigCodec.Decode(ConfigBlob));

        /// <summary>A host setting value by key (the gamemode's <see cref="SettingDescriptor.Key"/>), or <paramref name="fallback"/>.</summary>
        public string GetSetting(string key, string fallback = null) =>
            Config.TryGetValue(key, out var v) ? v : fallback;

        /// <summary>A host setting parsed as an integer (invariant culture), or <paramref name="fallback"/>.</summary>
        public int GetInt(string key, int fallback = 0) =>
            int.TryParse(GetSetting(key), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

        /// <summary>A host setting parsed as a float (invariant culture), or <paramref name="fallback"/>.</summary>
        public float GetFloat(string key, float fallback = 0f) =>
            float.TryParse(GetSetting(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

        /// <summary>A host setting parsed as a bool ("1"/"true" = true), or <paramref name="fallback"/>.</summary>
        public bool GetBool(string key, bool fallback = false)
        {
            var s = GetSetting(key);
            if (string.IsNullOrEmpty(s)) return fallback;
            return s == "1" || string.Equals(s, "true", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
