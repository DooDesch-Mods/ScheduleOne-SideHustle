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
        Text
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
}
