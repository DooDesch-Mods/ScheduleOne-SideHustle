using System;
using DooDesch.UI;
using S1API.UI;
using SideHustle.Config;
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
        /// <summary>The screen title and host button for a lobby that is, or is not, listed publicly.</summary>
        internal static string HostLabel(int visibility) => visibility == 1 ? "Host" : "Host publicly";

        internal static void Build(Transform formHost, PublishPlan plan, System.Collections.Generic.List<PrefsCategory> prefsCats,
            int maxClients, Action onBack, Action<HostOptions, bool, System.Collections.Generic.List<string>> onHost,
            Action<string> setTitle = null)
        {
            int cap = Mathf.Max(2, maxClients);
            // What the host chose last time (never the password).
            var saved = Preferences.GetHost(Preferences.VanillaHostKey);
            int players = Mathf.Clamp(saved != null && saved.MaxPlayers >= 2 ? saved.MaxPlayers : 4, 2, cap);
            int visibility = saved != null ? saved.Visibility : 0;   // 0 = Public, 1 = Private
            bool enforce = saved != null ? saved.Enforce : true;   // default ON: co-op is most stable when everyone runs the host's synced set
            Button hostButton = null;
            Text hostLabel = null;
            void RefreshHostLabel()
            {
                string label = HostLabel(visibility);
                if (hostLabel != null) hostLabel.text = label;
                setTitle?.Invoke(label);
            }
            var syncPrefs = new System.Collections.Generic.HashSet<string>(
                (prefsCats ?? new System.Collections.Generic.List<PrefsCategory>())
                    .Where(c => c.SyncByDefault).Select(c => c.Id), StringComparer.OrdinalIgnoreCase);

            var page = Scaffold.Create(formHost);
            var content = page.Content;

            Components.SectionHeader(content, "Lobby");

            Components.FormRow(content, "Lobby name", "Shown to players in the lobby browser.", out var lnSlot, stacked: true);
            string startName = saved != null && !string.IsNullOrWhiteSpace(saved.LobbyName) ? saved.LobbyName : LobbyCoordinator.LocalPersonaName() + " Lobby";
            var lobbyName = Components.TextInput(lnSlot, startName, null, "lobby name", 40);
            Components.FillSlot(lobbyName.gameObject, 6f);

            Components.FormRow(content, "Players", $"2 to {cap}.", out var pSlot);
            BuildSlider(pSlot, 2, cap, players, v => players = Mathf.RoundToInt(v));

            InputField password = null;
            GameObject passwordRow = null;
            Components.FormRow(content, "Visibility", "Private = friends only, not listed.", out var vSlot);
            Components.FillSlot(Components.Segmented(vSlot, new[] { "Public", "Private" }, visibility,
                i => { visibility = i; if (passwordRow != null) passwordRow.SetActive(i == 0); RefreshHostLabel(); }, out _));

            passwordRow = Components.FormRow(content, "Password", "Empty = open lobby.", out var pwSlot);
            password = Components.TextInput(pwSlot, "", null, "no password", 32);
            Components.FillSlot(password.gameObject, 6f);
            if (visibility == 1) passwordRow.SetActive(false);

            Components.FormRow(content, "Discord invite", "Optional. Shown with your lobby.", out var dcSlot, stacked: true);
            var discordField = Components.TextInput(dcSlot, saved != null && DiscordInvite.IsCode(saved.DiscordCode) ? DiscordInvite.LinkFor(saved.DiscordCode) : "", null, "discord.gg/invite", 80);
            Components.FillSlot(discordField.gameObject, 6f);

            Components.SectionHeader(content, "Mod sync");
            int auto = plan.AutoCount + plan.GhCount;
            int total = auto + plan.LinkCount + plan.DroppedCount;
            Scaffold.Note(content, $"Joiners get your mod set: {auto} of {total} install automatically"
                        + (plan.LinkCount > 0 ? $", {plan.LinkCount} need a download link" : "")
                        + (plan.DroppedCount > 0 ? $", {plan.DroppedCount} cannot sync" : "") + ".");

            Components.FormRow(content, "Synced clients only", "Kick joiners whose mods don't match (friends included).", out var eSlot);
            Components.Toggle(eSlot, enforce, v => enforce = v);

            if (prefsCats != null && prefsCats.Count > 0)
            {
                Components.SectionHeader(content, "Mod settings to sync");
                Scaffold.Note(content, "Applied to joiners for this lobby only.");
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

            Scaffold.FooterButton(page.Footer, "Back", Theme.Button, left: true, onClick: onBack);

            var (hostGO, hostBtn, hostLbl) = UIFactory.ButtonWithLabel("Host", HostLabel(visibility), page.Footer, Theme.Accent, Scaffold.PrimaryWidth, Scaffold.ButtonHeight);
            hostButton = hostBtn; hostLabel = hostLbl;
            Scaffold.PlaceFooter(hostGO, left: false);
            setTitle?.Invoke(HostLabel(visibility));
            hostBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
            {
                string discordCode = "";
                string typedInvite = (discordField != null ? discordField.text : "") ?? "";
                if (typedInvite.Trim().Length > 0 && !DiscordInvite.TryParse(typedInvite, out discordCode))
                {
                    Core.Log?.Msg("[hub] vanilla host form: the Discord invite field did not parse; not hosting.");
                    Hub.Notify(DiscordInvite.NotAnInvite, Severity.Warning);
                    return;
                }
                string typedName = lobbyName != null ? lobbyName.text : null;
                var opts = new HostOptions
                {
                    MaxPlayers = players,
                    Visibility = visibility == 1 ? LobbyVisibility.Private : LobbyVisibility.Public,
                    Password = password != null ? password.text : null,
                    LobbyName = typedName,
                    DiscordCode = string.IsNullOrEmpty(discordCode) ? null : discordCode,
                };
                Preferences.SetHost(Preferences.VanillaHostKey, new Preferences.RememberedHost
                {
                    MaxPlayers = players,
                    Visibility = visibility,
                    LobbyName = typedName ?? "",
                    Enforce = enforce,
                    DiscordCode = discordCode,
                });
                onHost?.Invoke(opts, enforce, syncPrefs.ToList());
            }));

            Interactions.PolishButtons(formHost);
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
    }
}
