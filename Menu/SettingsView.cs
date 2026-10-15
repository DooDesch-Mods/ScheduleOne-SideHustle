using System;
using DooDesch.UI;
using S1API.UI;
using SideHustle.Config;
using UnityEngine;
using UnityEngine.UI;

namespace SideHustle.Menu
{
    /// <summary>
    /// The in-game form for the settings players otherwise had to find in MelonPreferences.cfg: the menu columns,
    /// the Continue prompt, the stranger-message switch, the uninstalled-gamemode rows and the mod gate. Every
    /// control writes its preference as soon as it changes.
    /// </summary>
    internal static class SettingsView
    {
        internal static void Build(Transform formHost, Action onBack)
        {
            const float Pad = 30f;

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

            Components.SectionHeader(content, "Menus");
            Switch(content, "Info column in the main menu", "Mod set, open sessions and messages.",
                Preferences.MenuPanel, v => Preferences.MenuPanel = v);
            Switch(content, "Chat column while joining", "Ask the host about a mod you cannot get.",
                Preferences.JoinChatPanel, v => Preferences.JoinChatPanel = v);
            Switch(content, "Ask to host on Continue", "Offers to open the save as a public lobby.",
                Preferences.AskHostOnContinue, v => Preferences.AskHostOnContinue = v);
            Switch(content, "Show gamemodes you don't have", "Lists gamemodes that are not installed.",
                Preferences.ShowUninstalledGamemodes, v => Preferences.ShowUninstalledGamemodes = v);

            Components.SectionHeader(content, "Messages");
            Switch(content, "Let strangers message you", "Shown in the Lobby app on your phone.",
                Preferences.AcceptStrangerMessages, v => Preferences.AcceptStrangerMessages = v);

            Components.SectionHeader(content, "Mods");
            Switch(content, "Load mods when a lobby starts", "Takes effect after a restart.",
                Preferences.DeferModsUntilLobby, v => Preferences.DeferModsUntilLobby = v);

            Components.FormRow(content, "Always load these mods", "File names, comma-separated. Takes effect after a restart.", out var keepSlot, stacked: true);
            var keep = Components.TextInput(keepSlot, Preferences.AlwaysLoadMods, SaveKeepList, "SomeMod.dll, Other.dll", 400);
            Components.FillSlot(keep.gameObject, 6f);

            int waiting = SafePending();
            if (waiting > 0) Info(content, waiting == 1 ? "1 mod waits for a lobby." : $"{waiting} mods wait for a lobby.");

            var (backGO, backBtn, _b) = UIFactory.ButtonWithLabel("Back", "Back", footer.transform, Theme.Button, 140, 40);
            var rt = backGO.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 0.5f); rt.anchorMax = new Vector2(0, 0.5f); rt.pivot = new Vector2(0, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            backBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
            {
                // The text field commits on blur or Enter; a click on Back can arrive first.
                if (keep != null) SaveKeepList(keep.text);
                onBack?.Invoke();
            }));

            Interactions.PolishButtons(formHost);
        }

        private static void Switch(RectTransform content, string label, string hint, bool value, Action<bool> set)
        {
            Components.FormRow(content, label, hint, out var slot);
            Components.Toggle(slot, value, v =>
            {
                set(v);
                Core.Log?.Msg($"[settings] '{label}' is now {(v ? "on" : "off")}.");
            });
        }

        private static string _lastSavedKeep;

        private static void SaveKeepList(string text)
        {
            string value = (text ?? "").Trim();
            if (value == (_lastSavedKeep ?? Preferences.AlwaysLoadMods)) return;
            Preferences.AlwaysLoadMods = value;
            _lastSavedKeep = value;
            Core.Log?.Msg($"[settings] always-load list saved ({(value.Length == 0 ? "empty" : value)}).");
            Hub.Notify("Saved. Takes effect after a restart.", Severity.Success);
        }

        private static int SafePending()
        {
            try { return Mods.LateLoader.PendingCount; }
            catch (Exception e)
            {
                Safe.Report("counting the mods that wait for a lobby", e);
                return 0;
            }
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
    }
}
