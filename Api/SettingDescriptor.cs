namespace SideHustle
{
    /// <summary>How a host setting is presented on the Host-config form and stored in the config blob.</summary>
    public enum SettingType
    {
        /// <summary>A numeric range. Use <see cref="SettingDescriptor.WholeNumbers"/> for an integer spinner-style slider.</summary>
        Slider,
        /// <summary>An on/off switch; stored as "1"/"0".</summary>
        Toggle,
        /// <summary>A small set of choices shown as a button group; stored as the chosen <see cref="SettingDescriptor.Values"/>.</summary>
        Segmented,
        /// <summary>A free-text field; stored verbatim (escaped in the blob).</summary>
        Text,
        /// <summary>A single choice from <see cref="SettingDescriptor.Options"/>/<see cref="SettingDescriptor.Values"/>,
        /// rendered as a compact "&lt; Name &gt;" cycler (a dropdown selector). Stored as the chosen value.</summary>
        Dropdown
    }

    /// <summary>
    /// One host-configurable setting a gamemode exposes. Side Hustle renders these as a form on the Host screen,
    /// then ships the chosen values to the gamemode (host and clients) via the launch context's config blob -
    /// see <see cref="GamemodeDescriptor.HostSettings"/> and <see cref="Multiplayer.MultiplayerInfo"/>'s getters.
    ///
    /// Every value is stored as a string under <see cref="Key"/>. Keep keys short (the whole blob rides on Steam
    /// lobby metadata) and stable (they are the wire format your gamemode reads back).
    /// </summary>
    public sealed class SettingDescriptor
    {
        /// <summary>Stable, short id used to store/read the value (e.g. "round_time"). Required.</summary>
        public string Key;

        /// <summary>Label shown to the host. Required.</summary>
        public string Label;

        /// <summary>Optional one-line help shown under the label.</summary>
        public string Hint;

        /// <summary>Optional category name. The host form renders a section header whenever the category changes
        /// between consecutive settings, so order settings by category. Null/empty = no header for that setting.</summary>
        public string Category;

        /// <summary>The control to render.</summary>
        public SettingType Type = SettingType.Toggle;

        /// <summary>Default stored value (string form, parsed per <see cref="Type"/>).</summary>
        public string Default;

        /// <summary>Optional unit suffix shown next to a slider value (e.g. "s", "m", "players").</summary>
        public string Unit;

        // --- Slider ---

        /// <summary>Slider minimum.</summary>
        public float Min = 0f;
        /// <summary>Slider maximum.</summary>
        public float Max = 1f;
        /// <summary>Slider step/granularity (0 = continuous).</summary>
        public float Step = 1f;
        /// <summary>When true the slider snaps to whole numbers (integer setting).</summary>
        public bool WholeNumbers;

        // --- Segmented ---

        /// <summary>Segmented option labels (what the host sees).</summary>
        public string[] Options;

        /// <summary>Stored value per option, parallel to <see cref="Options"/>. When null the option label is stored.</summary>
        public string[] Values;
    }

    /// <summary>
    /// A named bundle of host-setting values a gamemode can offer as a one-click preset (e.g. "Classic Hunt").
    /// Side Hustle shows a preset picker above the settings form; selecting one CASCADES its <see cref="Values"/>
    /// into the matching controls (matched by <see cref="SettingDescriptor.Key"/>), which the host can then still
    /// tweak. Values use the same wire format as the controls: a Slider's number, a Toggle's "1"/"0", a Segmented's
    /// stored value. Keys with no matching control are ignored (forward-compatible with not-yet-built settings).
    /// </summary>
    public sealed class SettingPreset
    {
        /// <summary>Display name shown in the picker (e.g. "Classic Hunt"). Required.</summary>
        public string Name;

        /// <summary>Optional one-line description shown under the picker while this preset is selected.</summary>
        public string Hint;

        /// <summary>The setting values this preset applies, keyed by <see cref="SettingDescriptor.Key"/>.</summary>
        public System.Collections.Generic.Dictionary<string, string> Values;

        /// <summary>When true the picker marks this preset as experimental (a badge + sorted to the back) -
        /// use it for presets whose headline mechanic is not fully built yet.</summary>
        public bool Experimental;

        /// <summary>Optional human-readable player-count recommendation shown in the picker (e.g. "Best for 2-6").</summary>
        public string Recommended;

        /// <summary>Player-count range this preset suits best. When the lobby size falls in
        /// [<see cref="MinPlayers"/>, <see cref="MaxPlayers"/>] the form may auto-select it on open. 0 = unspecified.</summary>
        public int MinPlayers;
        public int MaxPlayers;

        /// <summary>Canonical "mode" this preset represents (e.g. "Infection"). When the host tweaks a preset, the
        /// effective mode shown to joiners becomes "Custom - {Mode}". Defaults to <see cref="Name"/> when null.</summary>
        public string Mode;

        /// <summary>When true the form opens on this preset, overriding the player-count best-fit. Used for a saved
        /// "Custom" preset that should be pre-selected next time the host opens the form.</summary>
        public bool DefaultSelected;
    }
}
