using System;
using System.Collections.Generic;
using System.Linq;
using DooDesch.UI;
using S1API.UI;
using SideHustle.Config;
using SideHustle.Multiplayer;
using SideHustle.Phone;
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

        /// <summary>What the host form holds while the player fills it in, on the classic screen and on the hub page.</summary>
        internal sealed class HostForm
        {
            public PublishPlan Plan;
            public bool Failed;
            public string Error;
            public List<PrefsCategory> Cats = new List<PrefsCategory>();
            public int Cap = 2;
            public int Players = 4;
            public int Visibility;   // 0 = Public, 1 = Private
            public bool Enforce = true;
            public string Name = "";
            public string Password = "";
            public string Discord = "";
            public HashSet<string> SyncPrefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public string Mono = "";
            public string Title = "";
            public string Sub = "";
        }

        /// <summary>The form with what the host chose last time (never the password).</summary>
        internal static HostForm NewForm(List<PrefsCategory> prefsCats, int maxClients)
        {
            int cap = Mathf.Max(2, maxClients);
            var saved = Preferences.GetHost(Preferences.VanillaHostKey);
            var f = new HostForm
            {
                Cats = prefsCats ?? new List<PrefsCategory>(),
                Cap = cap,
                Players = Mathf.Clamp(saved != null && saved.MaxPlayers >= 2 ? saved.MaxPlayers : 4, 2, cap),
                Visibility = saved != null ? saved.Visibility : 0,
                // Default ON: co-op is most stable when everyone runs the host's synced set.
                Enforce = saved != null ? saved.Enforce : true,
                Name = saved != null && !string.IsNullOrWhiteSpace(saved.LobbyName) ? saved.LobbyName : LobbyCoordinator.LocalPersonaName() + " Lobby",
                Discord = saved != null && DiscordInvite.IsCode(saved.DiscordCode) ? DiscordInvite.LinkFor(saved.DiscordCode) : "",
            };
            foreach (var c in f.Cats.Where(c => c.SyncByDefault)) f.SyncPrefs.Add(c.Id);
            return f;
        }

        /// <summary>Check the invite, remember the choices and host. False when the form refused.</summary>
        internal static bool Submit(HostForm f, Action<HostOptions, bool, List<string>> onHost)
        {
            string discordCode = "";
            string typedInvite = f.Discord ?? "";
            if (typedInvite.Trim().Length > 0 && !DiscordInvite.TryParse(typedInvite, out discordCode))
            {
                Core.Log?.Msg("[hub] vanilla host form: the Discord invite field did not parse; not hosting.");
                Hub.Notify(DiscordInvite.NotAnInvite, Severity.Warning);
                return false;
            }
            var opts = new HostOptions
            {
                MaxPlayers = f.Players,
                Visibility = f.Visibility == 1 ? LobbyVisibility.Private : LobbyVisibility.Public,
                Password = f.Password,
                LobbyName = f.Name,
                DiscordCode = string.IsNullOrEmpty(discordCode) ? null : discordCode,
            };
            Preferences.SetHost(Preferences.VanillaHostKey, new Preferences.RememberedHost
            {
                MaxPlayers = f.Players,
                Visibility = f.Visibility,
                LobbyName = f.Name ?? "",
                Enforce = f.Enforce,
                DiscordCode = discordCode,
            });
            Core.Log?.Msg($"[hub] vanilla host form: hosting '{f.Name}' ({(f.Visibility == 1 ? "private" : "public")}, {f.Players} players).");
            onHost?.Invoke(opts, f.Enforce, f.SyncPrefs.ToList());
            return true;
        }

        private static string SyncSummary(PublishPlan plan)
        {
            int auto = plan.AutoCount + plan.GhCount;
            int total = auto + plan.LinkCount + plan.DroppedCount;
            return $"Joiners get your mod set: {auto} of {total} install automatically"
                   + (plan.LinkCount > 0 ? $", {plan.LinkCount} need a download link" : "")
                   + (plan.DroppedCount > 0 ? $", {plan.DroppedCount} cannot sync" : "") + ".";
        }

        private static string Clip(string s, int max) => s == null ? "" : s.Length <= max ? s : s.Substring(0, max);

        /// <summary>The host form on the hub page: the lobby on the left, mod sync on the right, Host below.</summary>
        internal static void BuildPage(PageView pv, HostForm f, Action onBack, Action onHost)
        {
            pv.Frame = "detail";
            pv.OnBack(onBack);
            pv.Add(pv.DHead(f.Mono, f.Title, f.Sub, pv.Btn("Saves", onBack, "sm", "back")));
            if (f.Plan == null)
            {
                pv.Add(f.Failed
                    ? pv.Say("bad", "bad", "Couldn't prepare your mod list.", null)
                    : pv.Say("now", "busy", "Preparing your mod list...", null));
                return;
            }
            if (!string.IsNullOrEmpty(f.Error)) pv.Add(pv.Say("bad", "bad", f.Error, null));

            var left = new List<Json>
            {
                pv.Field("Lobby name", pv.Input("lobby", f.Name, "lobby name", v => f.Name = Clip(v, 40))),
                pv.Field("Players", pv.Stepper(f.Players, 2, f.Cap, v => f.Players = v), $"2 to {f.Cap}."),
                pv.Field("Visibility", pv.Seg(new[] { "Public", "Private" }, f.Visibility, i => f.Visibility = i)),
            };
            if (f.Visibility == 0)
                left.Add(pv.Field("Password", pv.Input("password", f.Password, "no password", v => f.Password = Clip(v, 32)), "Empty = open lobby."));
            left.Add(pv.Field("Discord invite", pv.Input("discord", f.Discord, "discord.gg/invite", v => f.Discord = Clip(v, 80)), "Optional."));

            var right = new List<Json>
            {
                pv.Heading("MOD SYNC"),
                pv.FNote(SyncSummary(f.Plan)),
                pv.Set("Synced clients only", "Kick joiners whose mods don't match (friends included).", pv.Toggle(f.Enforce, v => f.Enforce = v)),
            };
            if (f.Cats.Count > 0)
            {
                right.Add(pv.Heading("MOD SETTINGS TO SYNC"));
                right.Add(pv.FNote("Applied to joiners for this lobby only."));
                foreach (var cat in f.Cats)
                {
                    var c = cat;
                    right.Add(pv.Set(c.DisplayName, CategoryHint(c), pv.Toggle(f.SyncPrefs.Contains(c.Id), on =>
                    {
                        if (on) f.SyncPrefs.Add(c.Id); else f.SyncPrefs.Remove(c.Id);
                    })));
                }
            }
            pv.Add(pv.Cols(left, right));
            pv.Add(pv.Facts(new[] { pv.Btn(HostLabel(f.Visibility), onHost, "go big") }));
        }

        private static string CategoryHint(PrefsCategory c) =>
            !string.IsNullOrEmpty(c.Description) ? c.Description
            : c.SecretRisk ? "Off by default - may contain secrets (lobby data is public)."
            : "Apply your values to joiners.";

        internal static void Build(Transform formHost, PublishPlan plan, System.Collections.Generic.List<PrefsCategory> prefsCats,
            int maxClients, Action onBack, Action<HostOptions, bool, System.Collections.Generic.List<string>> onHost,
            Action<string> setTitle = null)
        {
            var form = NewForm(prefsCats, maxClients);
            int cap = form.Cap;
            int players = form.Players;
            int visibility = form.Visibility;
            bool enforce = form.Enforce;
            Button hostButton = null;
            Text hostLabel = null;
            void RefreshHostLabel()
            {
                string label = HostLabel(visibility);
                if (hostLabel != null) hostLabel.text = label;
                setTitle?.Invoke(label);
            }
            var syncPrefs = form.SyncPrefs;

            var page = Scaffold.Create(formHost);
            var content = page.Content;

            Components.SectionHeader(content, "Lobby");

            Components.FormRow(content, "Lobby name", "Shown to players in the lobby browser.", out var lnSlot, stacked: true);
            var lobbyName = Components.TextInput(lnSlot, form.Name, null, "lobby name", 40);
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
            var discordField = Components.TextInput(dcSlot, form.Discord, null, "discord.gg/invite", 80);
            Components.FillSlot(discordField.gameObject, 6f);

            Components.SectionHeader(content, "Mod sync");
            Scaffold.Note(content, SyncSummary(plan));

            Components.FormRow(content, "Synced clients only", "Kick joiners whose mods don't match (friends included).", out var eSlot);
            Components.Toggle(eSlot, enforce, v => enforce = v);

            if (prefsCats != null && prefsCats.Count > 0)
            {
                Components.SectionHeader(content, "Mod settings to sync");
                Scaffold.Note(content, "Applied to joiners for this lobby only.");
                foreach (var cat in prefsCats)
                {
                    var c = cat;
                    Components.FormRow(content, c.DisplayName, CategoryHint(c), out var cSlot);
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
                form.Name = lobbyName != null ? lobbyName.text : null;
                form.Password = password != null ? password.text : null;
                form.Discord = discordField != null ? discordField.text : "";
                form.Players = players;
                form.Visibility = visibility;
                form.Enforce = enforce;
                Submit(form, onHost);
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
