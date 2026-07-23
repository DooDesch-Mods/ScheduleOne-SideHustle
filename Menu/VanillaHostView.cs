using System;
using DooDesch.UI;
using S1API.UI;
using SideHustle.Multiplayer;
using SideHustle.Sync;
using UnityEngine;
using UnityEngine.UI;

namespace SideHustle.Menu
{
    /// <summary>
    /// The vanilla-hosting form (form-host view): lobby name / players / visibility / password, the enforce
    /// toggle, and the publish plan's summary so the host sees what joiners will get BEFORE opening the lobby.
    /// The manifest itself is prepared by the caller (it needs the Thunderstore index).
    /// </summary>
    internal static class VanillaHostView
    {
        internal static void Build(Transform formHost, PublishPlan plan, System.Collections.Generic.List<PrefsCategory> prefsCats,
            int maxClients, Action onBack, Action<HostOptions, bool, System.Collections.Generic.List<string>> onHost)
        {
            const float Pad = 30f;
            int cap = Mathf.Max(2, maxClients);
            int players = Mathf.Clamp(4, 2, cap);
            int visibility = 0;   // 0 = Public, 1 = Private
            bool enforce = true;   // default ON: co-op is most stable when everyone runs the host's synced set
            var syncPrefs = new System.Collections.Generic.HashSet<string>(
                (prefsCats ?? new System.Collections.Generic.List<PrefsCategory>())
                    .Where(c => c.SyncByDefault).Select(c => c.Id), StringComparer.OrdinalIgnoreCase);

            var footer = UIFactory.Panel("footer", formHost, Theme.Clear);
            var frt = footer.GetComponent<RectTransform>();
            frt.anchorMin = new Vector2(0, 0); frt.anchorMax = new Vector2(1, 0); frt.pivot = new Vector2(0.5f, 0);
            frt.offsetMin = new Vector2(Pad, 0); frt.offsetMax = new Vector2(-Pad, 56);

            var scrollArea = UIFactory.Panel("scrollArea", formHost, Theme.Clear);
            var srt = scrollArea.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(0, 0); srt.anchorMax = new Vector2(1, 1);
            srt.offsetMin = new Vector2(Pad, 64); srt.offsetMax = new Vector2(-Pad, 0);
            var content = Components.ScrollList(scrollArea.transform, out var scroll, 6f, Theme.ScrimPanel);
            SmoothScroll.Attach(scroll);

            Components.SectionHeader(content, "Lobby");

            Components.FormRow(content, "Lobby name", "Shown to players in the lobby browser.", out var lnSlot, stacked: true);
            var lobbyName = Components.TextInput(lnSlot, LobbyCoordinator.LocalPersonaName() + " Lobby", null, "lobby name", 40);
            Components.FillSlot(lobbyName.gameObject, 6f);

            Components.FormRow(content, "Players", $"2 to {cap}.", out var pSlot);
            BuildSlider(pSlot, 2, cap, players, v => players = Mathf.RoundToInt(v));

            InputField password = null;
            GameObject passwordRow = null;
            Components.FormRow(content, "Lobby", "Private = friends-only, not listed.", out var vSlot);
            Components.FillSlot(Components.Segmented(vSlot, new[] { "Public", "Private" }, 0,
                i => { visibility = i; if (passwordRow != null) passwordRow.SetActive(i == 0); }, out _));

            passwordRow = Components.FormRow(content, "Password", "Empty = open lobby.", out var pwSlot);
            password = Components.TextInput(pwSlot, "", null, "no password", 32);
            Components.FillSlot(password.gameObject, 6f);

            Components.SectionHeader(content, "Mod sync");
            int auto = plan.AutoCount + plan.GhCount;
            int total = auto + plan.LinkCount + plan.DroppedCount;
            Info(content, $"Joiners get your mod set: {auto} of {total} install automatically (Thunderstore{(plan.GhCount > 0 ? "/GitHub" : "")})"
                        + (plan.LinkCount > 0 ? $", {plan.LinkCount} via download link (picked up automatically once downloaded)" : "")
                        + (plan.DroppedCount > 0 ? $", {plan.DroppedCount} cannot sync (they join without those)" : "") + ".");

            Components.FormRow(content, "Synced clients only", "Kick joiners whose mods don't match (friends included).", out var eSlot);
            Components.Toggle(eSlot, true, v => enforce = v);

            if (prefsCats != null && prefsCats.Count > 0)
            {
                Components.SectionHeader(content, "Mod settings to sync");
                Info(content, "Only your session applies these - the client's real settings stay untouched.");
                foreach (var cat in prefsCats)
                {
                    var c = cat;
                    string hint = !string.IsNullOrEmpty(c.Description) ? c.Description
                                : c.SecretRisk ? "Off by default - may contain secrets (lobby data is public)."
                                : "Apply your values to joiners.";
                    Components.FormRow(content, c.DisplayName, hint, out var cSlot);
                    Components.Toggle(cSlot, c.SyncByDefault, on => { if (on) syncPrefs.Add(c.Id); else syncPrefs.Remove(c.Id); });
                }
            }

            var (backGO, backBtn, _b) = UIFactory.ButtonWithLabel("Back", "Back", footer.transform, Theme.Button, 140, 40);
            PlaceFooter(backGO, left: true);
            backBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => onBack?.Invoke()));

            var (hostGO, hostBtn, _h) = UIFactory.ButtonWithLabel("Host", "Host publicly", footer.transform, Theme.Accent, 200, 40);
            PlaceFooter(hostGO, left: false);
            hostBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
            {
                var opts = new HostOptions
                {
                    MaxPlayers = players,
                    Visibility = visibility == 1 ? LobbyVisibility.Private : LobbyVisibility.Public,
                    Password = password != null ? password.text : null,
                    LobbyName = lobbyName != null ? lobbyName.text : null,
                };
                onHost?.Invoke(opts, enforce, syncPrefs.ToList());
            }));

            Interactions.PolishButtons(formHost);
        }

        private static void Info(RectTransform content, string text)
        {
            var row = UIFactory.Panel("info", content, Theme.Clear);
            var rle = row.AddComponent<LayoutElement>();
            rle.minHeight = 44f; rle.preferredHeight = 44f; rle.flexibleWidth = 1;
            var t = UIFactory.Text("text", text, row.transform, 14, TextAnchor.MiddleLeft);
            t.color = Theme.TextMuted;
            var rt = t.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(12, 0); rt.offsetMax = new Vector2(-12, 0);
        }

        // A slider that fills its FormRow slot with a live value readout on the right - the same layout the
        // gamemode host form (HostConfigView) uses. A raw Components.Slider dropped into the slot has no anchors
        // and collapses to the centre, which is why the earlier version overlapped.
        private static void BuildSlider(Transform slot, int min, int max, int value, Action<float> onChange)
        {
            var readout = UIFactory.Text("val", value.ToString(), slot, Theme.Label, TextAnchor.MiddleRight);
            readout.color = Theme.TextPrimary; readout.raycastTarget = false;
            var vrt = readout.rectTransform;
            vrt.anchorMin = new Vector2(1, 0); vrt.anchorMax = new Vector2(1, 1); vrt.pivot = new Vector2(1, 0.5f);
            vrt.sizeDelta = new Vector2(44, 0); vrt.anchoredPosition = new Vector2(-2, 0);

            var slider = Components.Slider(slot, min, max, value, v => { readout.text = Mathf.RoundToInt(v).ToString(); onChange?.Invoke(v); }, 1f);
            var srt = slider.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(0, 0.5f); srt.anchorMax = new Vector2(1, 0.5f); srt.pivot = new Vector2(0.5f, 0.5f);
            srt.offsetMin = new Vector2(0, -4); srt.offsetMax = new Vector2(-52, 4);
            readout.text = Mathf.RoundToInt(slider.value).ToString();
        }

        private static void PlaceFooter(GameObject go, bool left)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(left ? 0 : 1, 0.5f); rt.anchorMax = new Vector2(left ? 0 : 1, 0.5f);
            rt.pivot = new Vector2(left ? 0 : 1, 0.5f);
            rt.anchoredPosition = Vector2.zero;
        }
    }
}
