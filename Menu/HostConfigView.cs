using System;
using System.Collections.Generic;
using DooDesch.UI;
using S1API.UI;
using SideHustle.Multiplayer;
using UnityEngine;
using UnityEngine.UI;

namespace SideHustle.Menu
{
    /// <summary>
    /// Builds the Host-configuration form into a parent panel (rendered on the cloned native menu screen by the Hub):
    /// a player-count slider, lobby visibility + password, an optional mod-policy choice, and the gamemode's declared
    /// settings. On Start it assembles a <see cref="HostOptions"/> (with the settings encoded into the config blob)
    /// and the chosen policy mode, and hands them back to the Hub.
    /// </summary>
    internal static class HostConfigView
    {
        internal static void Build(Transform formHost, GamemodeDescriptor desc, Mods.ModPlan plan, int maxClients,
                                   Action onBack, Action<HostOptions, int> onStart)
        {
            int cap = Mathf.Max(2, maxClients);
            // What the host chose last time for this gamemode (never the password).
            var saved = SideHustle.Config.Preferences.GetHost(desc.Id);
            int players = Mathf.Clamp(saved != null && saved.MaxPlayers >= 2 ? saved.MaxPlayers : Mathf.Min(4, cap), 2, cap);   // multiplayer: never below 2
            int visibility = saved != null ? saved.Visibility : 0;   // 0 = Public, 1 = Private
            bool hasPolicy = plan != null && plan.HasChanges;
            bool blocked = plan != null && plan.Blocked;
            // The gamemode can default the mod-set choice (e.g. PropHunt wants a clean, everyone-identical set) - but
            // only when a policy is actually available and not blocked; otherwise "Current installed mods" (0).
            int policyMode = (hasPolicy && !blocked && desc.DefaultRequiredModsOnly) ? 1 : 0;   // 0 = current installed, 1 = required only
            if (hasPolicy && !blocked && saved != null && saved.PolicyMode >= 0) policyMode = saved.PolicyMode;   // the host's own last choice wins

            var values = new Dictionary<string, string>();
            var textInputs = new List<KeyValuePair<string, InputField>>();
            var setters = new Dictionary<string, Action<string>>();   // per-key control setters, for preset cascade
            Action applyPresetDefault = null;
            InputField passwordField = null;
            GameObject passwordRow = null;
            InputField lobbyNameField = null;
            InputField discordField = null;

            // Preset-cascade vs user-edit tracking: cascading a preset into the controls must NOT mark the form
            // "dirty" - only a direct user edit does. On Start the effective mode is the selected preset's name, or
            // "Custom - <mode>" when the host tweaked it. applyPresetValues runs a cascade with dirty suppressed.
            bool applyingPreset = false;
            bool settingsDirty = false;
            Action markDirty = () => { if (!applyingPreset) settingsDirty = true; };
            Action<Dictionary<string, string>> applyPresetValues = vals =>
            {
                applyingPreset = true;
                if (vals != null)
                    foreach (var kv in vals)
                        if (setters.TryGetValue(kv.Key, out var set)) { try { set(kv.Value); } catch (Exception e) { Core.Log?.Warning($"[hub] could not apply preset value {kv.Key}: {e.Message}"); } }
                applyingPreset = false;
                settingsDirty = false;   // selecting a preset is a clean baseline
            };
            Func<SettingPreset> selectedPreset = null;

            // A scrolling settings area above a fixed footer, both inset by Scaffold.Pad so the form lines up with the
            // native save-slot rows.
            var page = Scaffold.Create(formHost);
            var content = page.Content;

            // --- gamemode preset picker (cascades its values into the controls below; the host can still tweak
            //     every individual setting afterwards). Applied once the controls + setters exist (see end). ---
            if (desc.Presets != null && desc.Presets.Length > 0)
                BuildPresetPicker(content, desc.Presets, players, applyPresetValues, out applyPresetDefault, out selectedPreset);

            Components.SectionHeader(content, "Lobby");

            // --- Lobby name (shown to joiners in the lobby browser) ---
            Components.FormRow(content, "Lobby name", "Shown in the lobby browser.", out var lnSlot, stacked: true);
            // Default lobby title uses the host's chosen display name for THIS gamemode when set. At config time the
            // session has not started, so the alias is not yet "active" - read it straight from the stored preference;
            // fall back to the real Steam persona name when no alias is set.
            string startName = saved != null && !string.IsNullOrWhiteSpace(saved.LobbyName) ? saved.LobbyName : DefaultLobbyName(desc);
            lobbyNameField = Components.TextInput(lnSlot, startName, null, "lobby name", 40);
            Fill(lobbyNameField.gameObject, 6f);

            // --- Players ---
            Components.FormRow(content, "Players", $"2 to {cap}.", out var pSlot);
            BuildSlider(pSlot, 2, cap, players, true, 1f, "players", v => players = Mathf.RoundToInt(v));

            // --- Lobby visibility ---
            Components.FormRow(content, "Visibility", "Private = friends only, not listed.", out var vSlot);
            var vis = Components.Segmented(vSlot, new[] { "Public", "Private" }, visibility,
                i => { visibility = i; if (passwordRow != null) passwordRow.SetActive(i == 0); }, out _);
            Fill(vis);

            // --- Password (public only) ---
            passwordRow = Components.FormRow(content, "Password", "Empty = open lobby.", out var pwSlot);
            passwordField = Components.TextInput(pwSlot, "", null, "no password", 32);
            Fill(passwordField.gameObject, 6f);
            if (visibility == 1) passwordRow.SetActive(false);

            // --- Discord invite (optional; only the code is published) ---
            Components.FormRow(content, "Discord invite", "Optional. Shown with your lobby.", out var dcSlot, stacked: true);
            discordField = Components.TextInput(dcSlot, saved != null && DiscordInvite.IsCode(saved.DiscordCode) ? DiscordInvite.LinkFor(saved.DiscordCode) : "", null, "discord.gg/invite", 80);
            Fill(discordField.gameObject, 6f);

            // --- Mod policy (optional, only when it would change the mod set) ---
            if (hasPolicy)
            {
                string hint = blocked
                    ? "Missing required mods: " + string.Join(", ", plan.MissingRequired) + "."
                    : $"Required mods only pauses {Words.Plural(plan.ToDisable.Count, "mod", "mods")}, enables {plan.ToEnable.Count} and restarts the game.";
                Components.FormRow(content, "Mods", hint, out var mSlot, stacked: true);
                var seg = Components.Segmented(mSlot, new[] { "Current installed mods", "Required mods only" }, policyMode,
                    i => policyMode = i, out var mbtns);
                Fill(seg);
                if (blocked && mbtns.Length > 1 && mbtns[1] != null) mbtns[1].interactable = false;   // can't apply with a missing mod
            }

            // --- gamemode-declared settings ---
            if (desc.HostSettings != null)
            {
                string lastCategory = null;
                foreach (var s in desc.HostSettings)
                {
                    if (s == null || string.IsNullOrEmpty(s.Key)) continue;
                    if (!string.IsNullOrEmpty(s.Category) && s.Category != lastCategory)
                    {
                        Components.SectionHeader(content, s.Category);
                        lastCategory = s.Category;
                    }
                    values[s.Key] = s.Default ?? "";
                    BuildSettingRow(content, s, values, textInputs, setters, markDirty);
                }
            }

            // Now the controls + their setters exist: apply the default preset so the form opens configured to it.
            applyPresetDefault?.Invoke();

            // --- footer: Back / Start ---
            Scaffold.FooterButton(page.Footer, "Back", Theme.Button, left: true, onClick: onBack);

            Scaffold.FooterButton(page.Footer, "Start hosting", Theme.Accent, left: false, onClick: () =>
            {
                foreach (var kv in textInputs) values[kv.Key] = Sanitize(kv.Value != null ? kv.Value.text : "");
                int mode = hasPolicy ? policyMode : 0;
                string lobbyName = Sanitize(lobbyNameField != null ? lobbyNameField.text : "");
                string discordCode = "";
                string typedInvite = (discordField != null ? discordField.text : "") ?? "";
                if (typedInvite.Trim().Length > 0 && !DiscordInvite.TryParse(typedInvite, out discordCode))
                {
                    Core.Log?.Msg("[hub] host form: the Discord invite field did not parse; not starting.");
                    Hub.Notify(DiscordInvite.NotAnInvite, Severity.Warning);
                    return;
                }
                string modeLabel = null;
                var sel = selectedPreset != null ? selectedPreset() : null;
                if (sel != null) modeLabel = settingsDirty ? "Custom - " + (sel.Mode ?? sel.Name) : sel.Name;
                var opts = new HostOptions
                {
                    MaxPlayers = players,
                    Visibility = visibility == 1 ? LobbyVisibility.Private : LobbyVisibility.Public,
                    Password = passwordField != null ? passwordField.text : null,
                    ConfigBlob = values.Count > 0 ? ConfigCodec.Encode(values) : null,
                    LobbyName = lobbyName,
                    ModeLabel = modeLabel,
                    DiscordCode = string.IsNullOrEmpty(discordCode) ? null : discordCode
                };
                SideHustle.Config.Preferences.SetHost(desc.Id, new SideHustle.Config.Preferences.RememberedHost
                {
                    MaxPlayers = players,
                    Visibility = visibility,
                    LobbyName = lobbyName,
                    DiscordCode = discordCode,
                    PolicyMode = hasPolicy ? mode : -1,
                });
                onStart?.Invoke(opts, mode);
            });

            Interactions.PolishButtons(formHost);
        }

        /// <summary>The lobby title a host gets without typing one: their display name for this gamemode (or Steam
        /// name) plus "Lobby". At config time the session has not started, so the alias is read from the preference.</summary>
        internal static string DefaultLobbyName(GamemodeDescriptor desc)
        {
            string hostDisplay = SideHustle.Config.Preferences.GetAlias(desc.Id);
            if (string.IsNullOrEmpty(hostDisplay)) hostDisplay = LobbyCoordinator.LocalPersonaName();
            return hostDisplay + " Lobby";
        }

        // --- control builders ---

        private static void BuildSettingRow(Transform content, SettingDescriptor s, Dictionary<string, string> values, List<KeyValuePair<string, InputField>> textInputs, Dictionary<string, Action<string>> setters, Action markDirty)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            switch (s.Type)
            {
                case SettingType.Slider:
                {
                    Components.FormRow(content, s.Label, s.Hint, out var slot);
                    float val = ParseFloat(s.Default, s.Min);
                    float step = s.Step > 0f ? s.Step : (s.WholeNumbers ? 1f : 0f);
                    var slider = BuildSlider(slot, s.Min, s.Max, val, s.WholeNumbers, step, s.Unit, v =>
                    {
                        values[s.Key] = s.WholeNumbers ? Mathf.RoundToInt(v).ToString(ci) : v.ToString("0.##", ci);
                        markDirty();
                    });
                    // preset cascade: set the slider value (its onValueChanged updates the readout + the blob).
                    setters[s.Key] = v => { if (float.TryParse(v, System.Globalization.NumberStyles.Float, ci, out var f)) slider.value = Mathf.Clamp(f, s.Min, s.Max); };
                    break;
                }
                case SettingType.Toggle:
                {
                    Components.FormRow(content, s.Label, s.Hint, out var slot);
                    bool on = s.Default == "1" || string.Equals(s.Default, "true", StringComparison.OrdinalIgnoreCase);
                    var tg = Components.Toggle(slot, on, v => { values[s.Key] = v ? "1" : "0"; markDirty(); });
                    var trt = tg.GetComponent<RectTransform>(); trt.anchorMin = new Vector2(1, 0.5f); trt.anchorMax = new Vector2(1, 0.5f); trt.pivot = new Vector2(1, 0.5f); trt.anchoredPosition = new Vector2(-2, 0);
                    setters[s.Key] = v => tg.isOn = v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
                    break;
                }
                case SettingType.Segmented:
                {
                    string[] opts = s.Options ?? new[] { "Off", "On" };
                    string[] vals = s.Values ?? opts;
                    int active = Math.Max(0, Array.IndexOf(vals, s.Default));
                    Components.FormRow(content, s.Label, s.Hint, out var slot, stacked: opts.Length > 2);
                    var seg = Components.Segmented(slot, opts, active, i => { values[s.Key] = i >= 0 && i < vals.Length ? vals[i] : opts[i]; markDirty(); }, out var segButtons);
                    Fill(seg);
                    setters[s.Key] = v =>
                    {
                        int idx = Array.IndexOf(vals, v); if (idx < 0) idx = 0;
                        Components.SetSegmentedActive(segButtons, idx);
                        values[s.Key] = idx < vals.Length ? vals[idx] : opts[idx];
                    };
                    break;
                }
                case SettingType.Dropdown:
                {
                    // A compact "< Name >" cycler: a dropdown selector that fits one row and styles like the rest of
                    // the form (a native uGUI popup is fragile to skin here). Stored value tracks the chosen option.
                    string[] opts = s.Options ?? new[] { "Off", "On" };
                    string[] vals = s.Values ?? opts;
                    int idx = Math.Max(0, Array.IndexOf(vals, s.Default));
                    Components.FormRow(content, s.Label, s.Hint, out var slot);

                    var holder = new GameObject("dropdown"); holder.transform.SetParent(slot, false); holder.AddComponent<RectTransform>();
                    Fill(holder);
                    var hl = holder.AddComponent<HorizontalLayoutGroup>();
                    hl.spacing = 6; hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = false; hl.childForceExpandHeight = true; hl.childAlignment = TextAnchor.MiddleRight;

                    var (prevGO, prevBtn, _dp) = UIFactory.ButtonWithLabel("prev", "<", holder.transform, Theme.Button, 36, 30);
                    var ple = prevGO.AddComponent<LayoutElement>(); ple.minWidth = 36; ple.preferredWidth = 36; ple.flexibleWidth = 0;
                    var lbl = UIFactory.Text("val", opts[idx], holder.transform, Theme.Label, TextAnchor.MiddleCenter, FontStyle.Bold);
                    lbl.color = Theme.TextPrimary; lbl.raycastTarget = false;
                    var lle = lbl.gameObject.AddComponent<LayoutElement>(); lle.minWidth = 110; lle.preferredWidth = 170; lle.flexibleWidth = 0;
                    var (nextGO, nextBtn, _dn) = UIFactory.ButtonWithLabel("next", ">", holder.transform, Theme.Button, 36, 30);
                    var nle = nextGO.AddComponent<LayoutElement>(); nle.minWidth = 36; nle.preferredWidth = 36; nle.flexibleWidth = 0;

                    Action<int> set = i =>
                    {
                        idx = ((i % opts.Length) + opts.Length) % opts.Length;
                        lbl.text = opts[idx];
                        values[s.Key] = idx < vals.Length ? vals[idx] : opts[idx];
                    };
                    set(idx);   // seed the stored value from the default
                    prevBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => { set(idx - 1); markDirty(); }));
                    nextBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => { set(idx + 1); markDirty(); }));
                    setters[s.Key] = v => { int i = Array.IndexOf(vals, v); set(i < 0 ? 0 : i); };
                    break;
                }
                case SettingType.Text:
                {
                    Components.FormRow(content, s.Label, s.Hint, out var slot, stacked: true);
                    var input = Components.TextInput(slot, s.Default ?? "", _t => markDirty(), null, 64);
                    Fill(input.gameObject, 6f);
                    textInputs.Add(new KeyValuePair<string, InputField>(s.Key, input));
                    setters[s.Key] = v => input.text = v ?? "";
                    break;
                }
            }
        }

        /// <summary>
        /// The gamemode preset picker: a "&lt; Name &gt;" cycler with a live description, shown above the settings.
        /// Selecting a preset cascades its values into the matching controls via <paramref name="setters"/> (the
        /// controls stay fully editable afterwards). <paramref name="applyInitial"/> is invoked by the caller AFTER
        /// the setting rows are built, so the form opens configured to the first preset.
        /// </summary>
        private static void BuildPresetPicker(Transform content, SettingPreset[] presets, int playerCount, Action<Dictionary<string, string>> applyValues, out Action applyInitial, out Func<SettingPreset> getSelected)
        {
            int index = 0;
            int n = presets.Length;

            var block = new GameObject("presetPicker"); block.transform.SetParent(content, false); block.AddComponent<RectTransform>();
            var ble = block.AddComponent<LayoutElement>(); ble.minHeight = 98; ble.preferredHeight = 98; ble.flexibleWidth = 1;
            var bv = block.AddComponent<VerticalLayoutGroup>(); bv.spacing = 4; bv.childControlWidth = true; bv.childControlHeight = true; bv.childForceExpandWidth = true; bv.childForceExpandHeight = false; bv.childAlignment = TextAnchor.UpperLeft;

            // The title reports the STATE, and it has to follow the selection: the form opens on the host's saved
            // settings, so it says so - but the moment they cycle to a named preset that sentence would be a lie, and
            // then the useful thing to say is what picking one does to the rows underneath. The old text,
            // "(new host? pick one, then Start)", said neither.
            var title = UIFactory.Text("title", "Preset", block.transform, Theme.Label, TextAnchor.MiddleLeft, FontStyle.Bold);
            title.color = Theme.TextPrimary; title.raycastTarget = false;
            var tle = title.gameObject.AddComponent<LayoutElement>(); tle.minHeight = 20; tle.preferredHeight = 20;

            var row = new GameObject("row"); row.transform.SetParent(block.transform, false); row.AddComponent<RectTransform>();
            var rle = row.AddComponent<LayoutElement>(); rle.minHeight = 36; rle.preferredHeight = 36; rle.flexibleWidth = 1;
            var rh = row.AddComponent<HorizontalLayoutGroup>(); rh.spacing = 8; rh.childControlWidth = true; rh.childControlHeight = true; rh.childForceExpandHeight = true; rh.childAlignment = TextAnchor.MiddleCenter;

            var (prevGO, prevBtn, _p) = UIFactory.ButtonWithLabel("prev", "<", row.transform, Theme.Button, 44, 36);
            var ple = prevGO.AddComponent<LayoutElement>(); ple.minWidth = 44; ple.preferredWidth = 44; ple.flexibleWidth = 0;

            var nameTxt = UIFactory.Text("name", presets[0].Name ?? "", row.transform, Theme.H3, TextAnchor.MiddleCenter, FontStyle.Bold);
            nameTxt.color = Theme.Accent; nameTxt.raycastTarget = false;
            var nle = nameTxt.gameObject.AddComponent<LayoutElement>(); nle.flexibleWidth = 1; nle.minWidth = 120;

            var (nextGO, nextBtn, _nx) = UIFactory.ButtonWithLabel("next", ">", row.transform, Theme.Button, 44, 36);
            var nxle = nextGO.AddComponent<LayoutElement>(); nxle.minWidth = 44; nxle.preferredWidth = 44; nxle.flexibleWidth = 0;

            var descTxt = UIFactory.Text("desc", presets[0].Hint ?? "", block.transform, Theme.Caption, TextAnchor.UpperLeft);
            descTxt.color = Theme.TextMuted; descTxt.raycastTarget = false;
            descTxt.horizontalOverflow = HorizontalWrapMode.Wrap; descTxt.verticalOverflow = VerticalWrapMode.Truncate;
            var dle = descTxt.gameObject.AddComponent<LayoutElement>(); dle.minHeight = 32; dle.preferredHeight = 32; dle.flexibleWidth = 1;

            void Apply(int i)
            {
                index = ((i % n) + n) % n;
                var p = presets[index];
                // DefaultSelected marks the entry a gamemode rebuilt from the host's last session - the only entry
                // that is not a fixed preset, and the only one the title can claim was "loaded".
                title.text = p.DefaultSelected
                    ? "Preset - your last settings are loaded"
                    : "Preset - fills in every setting below";
                nameTxt.text = p.Experimental ? (p.Name ?? "") + "   - experimental" : (p.Name ?? "");
                nameTxt.color = p.Experimental ? Theme.WarningText : Theme.Accent;   // amber badge for not-yet-built mechanics
                descTxt.text = string.IsNullOrEmpty(p.Recommended) ? (p.Hint ?? "") : (p.Recommended + "  -  " + (p.Hint ?? ""));
                applyValues?.Invoke(p.Values);   // cascade into the controls (dirty stays clean - this is a preset pick)
            }

            prevBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => Apply(index - 1)));
            nextBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => Apply(index + 1)));

            getSelected = () => presets[((index % n) + n) % n];

            // open on a DefaultSelected preset (e.g. a saved "Custom"); otherwise the best fit for the lobby size.
            applyInitial = () =>
            {
                int init = -1;
                for (int i = 0; i < presets.Length; i++) if (presets[i] != null && presets[i].DefaultSelected) { init = i; break; }
                if (init < 0) init = BestFitIndex(presets, playerCount);
                Apply(init);
            };
        }

        /// <summary>
        /// The settings blob and mode label the host form would start with for <paramref name="players"/> players and
        /// untouched controls: every declared default, then the default (or best-fitting) preset on top. "Host again"
        /// uses it to skip the form.
        /// </summary>
        internal static string DefaultConfig(GamemodeDescriptor desc, int players, out string modeLabel)
        {
            modeLabel = null;
            var values = new Dictionary<string, string>();
            if (desc.HostSettings != null)
                foreach (var s in desc.HostSettings)
                    if (s != null && !string.IsNullOrEmpty(s.Key)) values[s.Key] = s.Default ?? "";
            if (desc.Presets != null && desc.Presets.Length > 0)
            {
                int init = -1;
                for (int i = 0; i < desc.Presets.Length; i++) if (desc.Presets[i] != null && desc.Presets[i].DefaultSelected) { init = i; break; }
                if (init < 0) init = BestFitIndex(desc.Presets, players);
                var p = desc.Presets[init];
                if (p != null)
                {
                    modeLabel = p.Name;
                    if (p.Values != null) foreach (var kv in p.Values) values[kv.Key] = kv.Value;
                }
            }
            return values.Count > 0 ? ConfigCodec.Encode(values) : null;
        }

        /// <summary>Pick the non-experimental preset whose recommended [Min,Max] player range contains
        /// <paramref name="playerCount"/> with the narrowest (most specific) span; fall back to the first
        /// preset (the default) when nothing fits.</summary>
        private static int BestFitIndex(SettingPreset[] presets, int playerCount)
        {
            int best = -1, bestSpan = int.MaxValue;
            for (int i = 0; i < presets.Length; i++)
            {
                var p = presets[i];
                if (p == null || p.Experimental) continue;
                if (p.MinPlayers <= 0 || p.MaxPlayers <= 0) continue;
                if (playerCount < p.MinPlayers || playerCount > p.MaxPlayers) continue;
                int span = p.MaxPlayers - p.MinPlayers;
                if (span < bestSpan) { bestSpan = span; best = i; }
            }
            return best >= 0 ? best : 0;
        }

        // A slider that fills the slot but reserves a value readout on the right. When step > 0 the underlying slider
        // is driven in whole "step units" so the handle snaps to each step (e.g. 5s round time); the readout and the
        // callback always get the real value.
        private static Slider BuildSlider(Transform slot, float min, float max, float value, bool whole, float step, string unit, Action<float> onChange)
        {
            var val = UIFactory.Text("val", Fmt(value, whole, unit), slot, Theme.Label, TextAnchor.MiddleRight);
            val.color = Theme.TextPrimary; val.raycastTarget = false;
            var vrt = val.rectTransform; vrt.anchorMin = new Vector2(1, 0); vrt.anchorMax = new Vector2(1, 1); vrt.pivot = new Vector2(1, 0.5f); vrt.sizeDelta = new Vector2(54, 0); vrt.anchoredPosition = new Vector2(-2, 0);

            // Stepping lives in the DooDesch.UI Slider; the value arrives already snapped (real units).
            var slider = Components.Slider(slot, min, max, value, v => { val.text = Fmt(v, whole, unit); onChange?.Invoke(v); }, step);
            var srt = slider.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(0, 0.5f); srt.anchorMax = new Vector2(1, 0.5f); srt.pivot = new Vector2(0.5f, 0.5f);
            srt.offsetMin = new Vector2(0, -4); srt.offsetMax = new Vector2(-58, 4);

            val.text = Fmt(slider.value, whole, unit);   // reflect the snapped initial value
            return slider;
        }

        // --- small helpers ---

        private static void Anchor(GameObject go, Vector2 min, Vector2 max, Vector2 pivot)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = min; rt.anchorMax = max; rt.pivot = pivot; rt.anchoredPosition = Vector2.zero;
        }

        private static void Fill(GameObject go, float vInset = 0f)
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return;
            rt.anchorMin = new Vector2(0, 0); rt.anchorMax = new Vector2(1, 1);
            rt.offsetMin = new Vector2(0, vInset); rt.offsetMax = new Vector2(0, -vInset);
        }

        private static string Fmt(float v, bool whole, string unit)
        {
            string n = whole ? Mathf.RoundToInt(v).ToString() : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(unit) ? n : n + " " + unit;
        }

        private static float ParseFloat(string s, float fallback) =>
            float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

        private static string Sanitize(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.Replace(";", " ").Replace("=", " ").Replace("\n", " ").Replace("\r", " ").Trim();
    }
}
