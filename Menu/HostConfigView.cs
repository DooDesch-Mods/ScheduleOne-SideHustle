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
            int policyMode = 0;   // 0 = current installed, 1 = required only
            bool hasPolicy = plan != null && plan.HasChanges;
            bool blocked = plan != null && plan.Blocked;

            var values = new Dictionary<string, string>();
            var textInputs = new List<KeyValuePair<string, InputField>>();
            InputField passwordField = null;
            GameObject passwordRow = null;

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
            var content = Components.ScrollList(scrollArea.transform, out _, 6f);

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
                var seg = Components.Segmented(mSlot, new[] { "Current installed mods", "Required mods only" }, 0,
                    i => policyMode = i, out var mbtns);
                Fill(seg);
                if (blocked && mbtns.Length > 1 && mbtns[1] != null) mbtns[1].interactable = false;   // can't apply with a missing mod
            }

            // --- gamemode-declared settings ---
            if (desc.HostSettings != null)
            {
                foreach (var s in desc.HostSettings)
                {
                    if (s == null || string.IsNullOrEmpty(s.Key)) continue;
                    values[s.Key] = s.Default ?? "";
                    BuildSettingRow(content, s, values, textInputs);
                }
            }

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
                var opts = new HostOptions
                {
                    MaxPlayers = players,
                    Visibility = visibility == 1 ? LobbyVisibility.Private : LobbyVisibility.Public,
                    Password = passwordField != null ? passwordField.text : null,
                    ConfigBlob = values.Count > 0 ? ConfigCodec.Encode(values) : null
                };
                onStart?.Invoke(opts, mode);
            }));

            Interactions.PolishButtons(formHost);
        }

        // --- control builders ---

        private static void BuildSettingRow(Transform content, SettingDescriptor s, Dictionary<string, string> values, List<KeyValuePair<string, InputField>> textInputs)
        {
            switch (s.Type)
            {
                case SettingType.Slider:
                {
                    Components.FormRow(content, s.Label, s.Hint, out var slot);
                    float val = ParseFloat(s.Default, s.Min);
                    float step = s.Step > 0f ? s.Step : (s.WholeNumbers ? 1f : 0f);
                    BuildSlider(slot, s.Min, s.Max, val, s.WholeNumbers, step, s.Unit, v =>
                        values[s.Key] = s.WholeNumbers ? Mathf.RoundToInt(v).ToString(System.Globalization.CultureInfo.InvariantCulture)
                                                       : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                }
                case SettingType.Toggle:
                {
                    Components.FormRow(content, s.Label, s.Hint, out var slot);
                    bool on = s.Default == "1" || string.Equals(s.Default, "true", StringComparison.OrdinalIgnoreCase);
                    var tg = Components.Toggle(slot, on, v => values[s.Key] = v ? "1" : "0");
                    var trt = tg.GetComponent<RectTransform>(); trt.anchorMin = new Vector2(1, 0.5f); trt.anchorMax = new Vector2(1, 0.5f); trt.pivot = new Vector2(1, 0.5f); trt.anchoredPosition = new Vector2(-2, 0);
                    break;
                }
                case SettingType.Segmented:
                {
                    string[] opts = s.Options ?? new[] { "Off", "On" };
                    string[] vals = s.Values ?? opts;
                    int active = Math.Max(0, Array.IndexOf(vals, s.Default));
                    Components.FormRow(content, s.Label, s.Hint, out var slot, stacked: opts.Length > 2);
                    var seg = Components.Segmented(slot, opts, active, i => values[s.Key] = i >= 0 && i < vals.Length ? vals[i] : opts[i], out _);
                    Fill(seg);
                    break;
                }
                case SettingType.Text:
                {
                    Components.FormRow(content, s.Label, s.Hint, out var slot, stacked: true);
                    var input = Components.TextInput(slot, s.Default ?? "", null, null, 64);
                    Fill(input.gameObject, 6f);
                    textInputs.Add(new KeyValuePair<string, InputField>(s.Key, input));
                    break;
                }
            }
        }

        // A slider that fills the slot but reserves a value readout on the right. When step > 0 the underlying slider
        // is driven in whole "step units" so the handle snaps to each step (e.g. 5s round time); the readout and the
        // callback always get the real value.
        private static void BuildSlider(Transform slot, float min, float max, float value, bool whole, float step, string unit, Action<float> onChange)
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
