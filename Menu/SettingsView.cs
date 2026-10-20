using System;
using DooDesch.UI;
using S1API.UI;
using SideHustle.Config;
using SideHustle.Phone;
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
            var page = Scaffold.Create(formHost);
            var content = page.Content;

            Components.SectionHeader(content, "Menus");
            Switch(content, "Info column in the main menu", "Mod set, open lobbies and messages.",
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
            if (waiting > 0) Scaffold.Note(content, waiting == 1 ? "1 mod waits for a lobby." : $"{waiting} mods wait for a lobby.");

            Scaffold.FooterButton(page.Footer, "Back", Theme.Button, left: true, onClick: () =>
            {
                // The text field commits on blur or Enter; a click on Back can arrive first.
                if (keep != null) SaveKeepList(keep.text);
                onBack?.Invoke();
            });

            Interactions.PolishButtons(formHost);
        }

        /// <summary>The same settings on the hub page, in three cards.</summary>
        internal static void BuildPage(PageView pv, Action onBack)
        {
            pv.Frame = "main";
            pv.OnBack(onBack);
            int waiting = SafePending();
            pv.Add(pv.Scroll(new[]
            {
                pv.Card("MENUS", false,
                    PageSwitch(pv, "Info column in the main menu", "Mod set, open lobbies and messages.",
                        Preferences.MenuPanel, v => Preferences.MenuPanel = v),
                    PageSwitch(pv, "Chat column while joining", "Ask the host about a mod you cannot get.",
                        Preferences.JoinChatPanel, v => Preferences.JoinChatPanel = v),
                    PageSwitch(pv, "Ask to host on Continue", "Offers to open the save as a public lobby.",
                        Preferences.AskHostOnContinue, v => Preferences.AskHostOnContinue = v),
                    PageSwitch(pv, "Show gamemodes you don't have", "Lists gamemodes that are not installed.",
                        Preferences.ShowUninstalledGamemodes, v => Preferences.ShowUninstalledGamemodes = v)),
                pv.Card("MESSAGES", false,
                    PageSwitch(pv, "Let strangers message you", "Shown in the Lobby app on your phone.",
                        Preferences.AcceptStrangerMessages, v => Preferences.AcceptStrangerMessages = v)),
                pv.Card("MODS", false,
                    PageSwitch(pv, "Load mods when a lobby starts", "Takes effect after a restart.",
                        Preferences.DeferModsUntilLobby, v => Preferences.DeferModsUntilLobby = v,
                        waiting <= 0 ? null : waiting == 1 ? "1 mod waits for a lobby." : $"{waiting} mods wait for a lobby."),
                    pv.Set("Always load these mods", "File names, comma-separated. Takes effect after a restart.",
                        pv.Input("keep", _keepDraft ?? Preferences.AlwaysLoadMods, "SomeMod.dll, Other.dll",
                            v => _keepDraft = v, SaveKeepList))),
            }));
        }

        // The field commits on Enter and when it loses focus; a tab click can take the screen away first.
        private static string _keepDraft;

        internal static void LeavePage()
        {
            if (_keepDraft != null) SaveKeepList(_keepDraft);
            _keepDraft = null;
        }

        private static Json PageSwitch(PageView pv, string label, string hint, bool value, Action<bool> set, string note = null) =>
            pv.Set(label, hint, pv.Toggle(value, v =>
            {
                set(v);
                Core.Log?.Msg($"[settings] '{label}' is now {(v ? "on" : "off")}.");
            }), note);

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
    }
}
