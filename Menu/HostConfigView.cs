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
            int players = Mathf.Clamp(Mathf.Min(4, cap), 2, cap);   // multiplayer: never below 2
            int visibility = 0;   // 0 = Public, 1 = Private
            bool hasPolicy = plan != null && plan.HasChanges;
            bool blocked = plan != null && plan.Blocked;
            // The gamemode can default the mod-set choice (e.g. PropHunt wants a clean, everyone-identical set) - but
            // only when a policy is actually available and not blocked; otherwise "Current installed mods" (0).
            int policyMode = (hasPolicy && !blocked && desc.DefaultRequiredModsOnly) ? 1 : 0;   // 0 = current installed, 1 = required only

            var values = new Dictionary<string, string>();
            var textInputs = new List<KeyValuePair<string, InputField>>();
            var setters = new Dictionary<string, Action<string>>();   // per-key control setters, for preset cascade
            Action applyPresetDefault = null;
            InputField passwordField = null;
            GameObject passwordRow = null;
            InputField lobbyNameField = null;

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
                        if (setters.TryGetValue(kv.Key, out var set)) { try { set(kv.Value); } catch { } }
                applyingPreset = false;
                settingsDirty = false;   // selecting a preset is a clean baseline
            };
            Func<SettingPreset> selectedPreset = null;

            // Split the panel into a scrolling settings area (top) and a fixed footer (bottom). Both are inset
            // horizontally by Pad so the form content lines up with the native save-slot rows (which inset their
            // text by the same amount) instead of overflowing the visible panel and clipping on the left/right.
            const float Pad = 30f;
            var footer = NewPanel("footer", formHost);
            var frt = footer.GetComponent<RectTransform>();
            frt.anchorMin = new Vector2(0, 0); frt.anchorMax = new Vector2(1, 0); frt.pivot = new Vector2(0.5f, 0);
            frt.offsetMin = new Vector2(Pad, 0); frt.offsetMax = new Vector2(-Pad, 56);

            var scrollArea = NewPanel("scrollArea", formHost);
            var srt = scrollArea.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(0, 0); srt.anchorMax = new Vector2(1, 1); srt.offsetMin = new Vector2(Pad, 64); srt.offsetMax = new Vector2(-Pad, 0);
            var content = Components.ScrollList(scrollArea.transform, out var scroll, 6f);
            SmoothScroll.Attach(scroll);   // smooth wheel glide for the host-config list (driven by Core's menu update)

            // --- gamemode preset picker (cascades its values into the controls below; the host can still tweak
            //     every individual setting afterwards). Applied once the controls + setters exist (see end). ---
            if (desc.Presets != null && desc.Presets.Length > 0)
                BuildPresetPicker(content, desc.Presets, players, applyPresetValues, out applyPresetDefault, out selectedPreset);

            Components.SectionHeader(content, "Lobby");

            // --- Lobby name (shown to joiners in the server browser) ---
            Components.FormRow(content, "Lobby name", "Shown to players in the server browser.", out var lnSlot, stacked: true);
            lobbyNameField = Components.TextInput(lnSlot, LobbyCoordinator.LocalPersonaName() + " Lobby", null, "lobby name", 40);
            Fill(lobbyNameField.gameObject, 6f);

            // --- Players ---
            Components.FormRow(content, "Players", $"2 to {cap}.", out var pSlot);
            BuildSlider(pSlot, 2, cap, players, true, 1f, "players", v => players = Mathf.RoundToInt(v));

            // --- Lobby visibility ---
            Components.FormRow(content, "Lobby", "Private = friends-only, not listed.", out var vSlot);
            var vis = Components.Segmented(vSlot, new[] { "Public", "Private" }, 0,
                i => { visibility = i; if (passwordRow != null) passwordRow.SetActive(i == 0); }, out _);
            Fill(vis);

            // --- Password (public only) ---
            passwordRow = Components.FormRow(content, "Password", "Empty = open lobby.", out var pwSlot);
            passwordField = Components.TextInput(pwSlot, "", null, "no password", 32);
            Fill(passwordField.gameObject, 6f);

            // --- Mod policy (optional, only when it would change the mod set) ---
            if (hasPolicy)
            {
                string hint = blocked
                    ? "Missing required mod(s): " + string.Join(", ", plan.MissingRequired) + ". Pick \"Current installed mods\"."
                    : $"Required: pauses {plan.ToDisable.Count} mod(s), enables {plan.ToEnable.Count}; restarts the game. Current keeps your full set.";
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
            var (backGO, backBtn, _b) = UIFactory.ButtonWithLabel("Back", "Back", footer.transform, Theme.Button, 140, 40);
            PlaceFooterButton(backGO, left: true);
            backBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onBack?.Invoke()));

            var (startGO, startBtn, _s) = UIFactory.ButtonWithLabel("Start", "Start hosting", footer.transform, Theme.Accent, 200, 40);
            PlaceFooterButton(startGO, left: false);
            startBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
            {
                foreach (var kv in textInputs) values[kv.Key] = Sanitize(kv.Value != null ? kv.Value.text : "");
                int mode = hasPolicy ? policyMode : 0;
                string lobbyName = Sanitize(lobbyNameField != null ? lobbyNameField.text : "");
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
                    ModeLabel = modeLabel
                };
                onStart?.Invoke(opts, mode);
            }));

            Interactions.PolishButtons(formHost);
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

            var title = UIFactory.Text("title", "Game mode preset  (new host? pick one, then Start)", block.transform, Theme.Label, TextAnchor.MiddleLeft, FontStyle.Bold);
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
                nameTxt.text = p.Experimental ? (p.Name ?? "") + "   - EXPERIMENTAL" : (p.Name ?? "");
                nameTxt.color = p.Experimental ? new Color(1f, 0.72f, 0.2f) : Theme.Accent;   // amber badge for not-yet-built mechanics
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

        private static GameObject NewPanel(string name, Transform parent)
        {
            var go = UIFactory.Panel(name, parent, Theme.Clear);
            var img = go.GetComponent<Image>(); if (img != null) img.raycastTarget = false;
            return go;
        }

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

        private static void PlaceFooterButton(GameObject go, bool left)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(left ? 0 : 1, 0.5f);
            rt.pivot = new Vector2(left ? 0 : 1, 0.5f);
            rt.anchoredPosition = new Vector2(left ? 16 : -16, 0);
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
