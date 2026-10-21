// Copyright (c) 2026 DooDesch. DooDesch API License (see LICENSE.md): you may copy this file unchanged.
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
        /// <summary>A free-text field of up to 64 characters. On Start the form replaces ';', '=' and line breaks with
        /// spaces and trims the text.</summary>
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
    /// <remarks>
    /// Stored form per <see cref="Type"/>: a Slider stores an integer such as "30" with <see cref="WholeNumbers"/>,
    /// else a number with up to two decimals such as "0.25". Numbers use the invariant culture (a dot, no
    /// thousands separator). A Toggle stores "1" or "0". Segmented and Dropdown store the chosen entry of
    /// <see cref="Values"/>, or of <see cref="Options"/> when Values is null. Text stores the typed text.
    /// The whole blob of all settings should stay under about 1500 bytes; a larger worst case logs a warning at
    /// <see cref="API.Register"/>.
    /// </remarks>
    /// <example>
    /// One setting per type, read back in the launch callback:
    /// <code>
    /// HostSettings = new[]
    /// {
    ///     new SettingDescriptor { Key = "len", Label = "Session length", Type = SettingType.Slider,
    ///                             Min = 10, Max = 120, Step = 10, WholeNumbers = true, Default = "30", Unit = "s" },
    ///     new SettingDescriptor { Key = "ping", Label = "Ping on start", Type = SettingType.Toggle, Default = "1" },
    ///     new SettingDescriptor { Key = "pace", Label = "Pace", Type = SettingType.Segmented,
    ///                             Options = new[] { "Slow", "Normal", "Fast" }, Values = new[] { "s", "n", "f" }, Default = "n" },
    ///     new SettingDescriptor { Key = "motd", Label = "Welcome text", Type = SettingType.Text, Default = "Have fun" }
    /// };
    /// // in OnHostMultiplayer / OnJoinMultiplayer:
    /// int seconds = ctx.Multiplayer?.GetInt("len", 30) ?? 30;
    /// </code>
    /// </example>
    public sealed class SettingDescriptor
    {
        /// <summary>Stable, short id used to store/read the value (e.g. "round_time"). Required.</summary>
        /// <value>Unique within the gamemode, e.g. "len". Compared exactly, case included. Null or empty skips the
        /// setting on the form. Read it back with <see cref="Multiplayer.MultiplayerInfo.GetSetting"/>.</value>
        public string Key;

        /// <summary>Label shown to the host. Required.</summary>
        public string Label;

        /// <summary>Optional one-line help shown under the label.</summary>
        public string Hint;

        /// <summary>Optional category name. The host form renders a section header whenever the category changes
        /// between consecutive settings, so order settings by category. Null/empty = no header for that setting.</summary>
        public string Category;

        /// <summary>The control to render.</summary>
        /// <value>Default <see cref="SettingType.Toggle"/>.</value>
        public SettingType Type = SettingType.Toggle;

        /// <summary>Default stored value (string form, parsed per <see cref="Type"/>).</summary>
        /// <value>Slider: a number in the invariant culture, e.g. "30". Not a number starts at <see cref="Min"/>; a value
        /// outside Min..Max is clamped. Toggle: "1", "0", "true" or "false"; anything else starts off. Segmented and
        /// Dropdown: one of the stored values, e.g. "n"; anything else selects the first option. Text: the starting
        /// text. Null starts the slider at Min, the toggle off, the first option selected and the text empty.</value>
        public string Default;

        /// <summary>Optional unit suffix shown next to a slider value (e.g. "s", "m", "players").</summary>
        public string Unit;

        // --- Slider ---

        /// <summary>Slider minimum.</summary>
        /// <value>In the setting's own unit, e.g. 10. Default 0. Above <see cref="Max"/> logs an error.</value>
        public float Min = 0f;
        /// <summary>Slider maximum.</summary>
        /// <value>In the setting's own unit, e.g. 120. Default 1. Leaving the range at 0..1 logs a warning.</value>
        public float Max = 1f;
        /// <summary>Slider step/granularity (0 = continuous).</summary>
        /// <value>In the setting's own unit, e.g. 10. Default 1. 0 or less means continuous, or steps of 1 with
        /// <see cref="WholeNumbers"/>. A negative step logs a warning.</value>
        public float Step = 1f;
        /// <summary>When true the slider snaps to whole numbers (integer setting).</summary>
        public bool WholeNumbers;

        // --- Segmented ---

        /// <summary>Segmented option labels (what the host sees).</summary>
        /// <value>Used by Segmented and Dropdown, e.g. <c>new[] { "Slow", "Normal", "Fast" }</c>. Null shows "Off" and
        /// "On". An empty array logs an error and shows nothing.</value>
        public string[] Options;

        /// <summary>Stored value per option, parallel to <see cref="Options"/>. When null the option label is stored.</summary>
        /// <value>Same length as Options, e.g. <c>new[] { "s", "n", "f" }</c>. Fewer entries store the label for the rest
        /// and log a warning. More entries log an error.</value>
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
        /// <value>Each value in the control's stored form, e.g. <c>{ "len", "10" }, { "pace", "f" }</c>. Keys with no
        /// matching setting log a warning and are ignored. Null applies nothing.</value>
        public System.Collections.Generic.Dictionary<string, string> Values;

        /// <summary>When true the picker marks this preset as experimental (a badge + sorted to the back) -
        /// use it for presets whose headline mechanic is not fully built yet.</summary>
        public bool Experimental;

        /// <summary>Optional human-readable player-count recommendation shown in the picker (e.g. "Best for 2-6").</summary>
        public string Recommended;

        /// <summary>Player-count range this preset suits best. When the lobby size falls in
        /// [<see cref="MinPlayers"/>, <see cref="MaxPlayers"/>] the form may auto-select it on open. 0 = unspecified.</summary>
        /// <value>Inclusive player count, e.g. 2. The auto-select needs both ends above 0 and skips experimental presets.
        /// Among several fits the narrowest range wins.</value>
        public int MinPlayers;
        /// <summary>Upper end of the player-count range, inclusive, e.g. 4. 0 = unspecified. See <see cref="MinPlayers"/>.</summary>
        public int MaxPlayers;

        /// <summary>Canonical "mode" this preset represents (e.g. "Infection"). When the host tweaks a preset, the
        /// effective mode shown to joiners becomes "Custom - {Mode}". Defaults to <see cref="Name"/> when null.</summary>
        public string Mode;

        /// <summary>When true the form opens on this preset, overriding the player-count best-fit. Used for a saved
        /// "Custom" preset that should be pre-selected next time the host opens the form.</summary>
        public bool DefaultSelected;
    }
}
