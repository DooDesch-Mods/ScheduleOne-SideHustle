using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppScheduleOne.UI.MainMenu;
using SideHustle.Config;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SideHustle.Menu
{
    /// <summary>
    /// Injects the "Side Hustle" entry into the main-menu home screen. We do not Harmony-patch MainMenuScreen.Awake
    /// (it is the base of many screens); instead we run from the MelonMod scene lifecycle once the "Menu" scene is up,
    /// find the home screen (the MainMenuScreen with OpenOnStart), clone one of its nav buttons for styling and rewire
    /// its click to open the hub panel. The first frame after a scene load may not have the UI laid out yet, so Core
    /// retries us for a short window. The injector is also self-diagnosing: it logs the home screens and candidate
    /// buttons it sees, so the exact menu structure is visible in the MelonLoader log.
    /// </summary>
    internal static class MenuInjector
    {
        private const string MenuButtonName = "SideHustle_MenuButton";

        // The separate "Mod Profiles" entry, right below the Side Hustle one: profiles are not a gamemode, so
        // they get their own way in instead of being mixed into the gamemode list.
        private const string ProfilesButtonName = "SideHustle_ProfilesButton";

        // The direct main-menu entry for the gamemode a profile session is running (e.g. "PropHunt"), shown only
        // while that profile is live. Clicking it is identical to opening Side Hustle and picking the gamemode.
        private const string GamemodeButtonName = "SideHustle_GamemodeButton";

        // Vanilla home-screen entries that start/continue a normal campaign. They make no sense inside a curated
        // gamemode profile (a stripped mod set, no campaign save), so we hide them there and offer the gamemode instead.
        private static readonly string[] HiddenInProfileLabels = { "continue", "new game" };

        // Let the menu's own UIScreen/UISelectable navigation finish initializing before we clone + reparent a button
        // into it. Touching the nav while the game is still iterating its selectables can corrupt it and hard-crash
        // (more likely when another heavy mod loads alongside us and shifts the timing).
        private const int WarmupFrames = 20;

        private static bool _injectedThisScene;
        private static bool _loggedStructure;
        private static int _retries;

        // Nav-button labels we prefer to clone (Settings has the most side-effect-free click).
        private static readonly string[] PreferredLabels =
            { "settings", "options", "load", "continue", "new game", "quit", "exit" };

        internal static void Reset()
        {
            _injectedThisScene = false;
            _loggedStructure = false;
            _retries = 0;
        }

        /// <summary>Called every frame while in the Menu scene until we inject or give up.</summary>
        internal static void TickRetry()
        {
            if (_injectedThisScene) return;
            if (++_retries < WarmupFrames) return;       // wait out the menu's own init before touching its UI
            if (_retries > 120 + WarmupFrames) return;    // ~2s of attempts after the warmup, then stop trying
            TryInject();
        }

        internal static void TryInject()
        {
            try
            {
                if (_injectedThisScene) return;
                if (!Preferences.Enabled) { _injectedThisScene = true; return; }

                MainMenuScreen home = FindHomeScreen();
                if (home == null) return;

                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Button> buttons =
                    home.GetComponentsInChildren<Button>(true);
                if (buttons == null || buttons.Length == 0) return;

                // Idempotency guard: the "Menu" scene can re-initialise during a single menu load (the game fires
                // OnSceneWasInitialized -> Reset more than once), so injection may run again after our buttons
                // already exist. Adopt what is there and only add what is missing - never duplicate an entry.
                Button existingMain = null;
                bool haveProfiles = false;
                for (int i = 0; i < buttons.Length; i++)
                {
                    if (buttons[i] == null) continue;
                    string n = buttons[i].gameObject.name;
                    if (n == MenuButtonName) existingMain = buttons[i];
                    else if (n == ProfilesButtonName) haveProfiles = true;
                }
                if (existingMain != null)
                {
                    if (!haveProfiles)
                        CloneNavButton(existingMain, ProfilesButtonName, "Mod Profiles",
                            (UnityAction)Hub.OpenProfilesScreen, existingMain.transform.GetSiblingIndex() + 1);
                    _injectedThisScene = true;
                    Hub.RememberHome(home);
                    if (Mods.AltBase.IsAltSession()) ApplyProfileMenu(home);
                    return;
                }

                if (!_loggedStructure)
                {
                    _loggedStructure = true;
#if DEBUG
                    Core.Log?.Msg($"[menu] home screen '{home.name}' has {buttons.Length} button(s):");
                    for (int i = 0; i < buttons.Length; i++)
                    {
                        Button b = buttons[i];
                        if (b == null) continue;
                        Core.Log?.Msg($"[menu]   #{i} go='{b.gameObject.name}' parent='{(b.transform.parent != null ? b.transform.parent.name : "<none>")}' label='{GetLabel(b.gameObject)}'");
                    }
#endif
                }

                Button template = PickTemplate(buttons);
                if (template == null) return;

                if (BuildEntry(home, template))
                {
                    _injectedThisScene = true;
                    Hub.RememberHome(home);
                    if (Mods.AltBase.IsAltSession()) ApplyProfileMenu(home);
                    Core.Log?.Msg("[menu] Side Hustle entry injected.");
                }
            }
            catch (Exception ex)
            {
                Core.Log?.Warning("[menu] inject error: " + ex.Message);
                _injectedThisScene = true; // don't spam every frame on a hard failure
            }
        }

        private static MainMenuScreen FindHomeScreen()
        {
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<MainMenuScreen> screens =
                UnityEngine.Object.FindObjectsOfType<MainMenuScreen>(true);
            if (screens == null || screens.Length == 0) return null;

            MainMenuScreen openOnStart = null;
            for (int i = 0; i < screens.Length; i++)
            {
                MainMenuScreen s = screens[i];
                if (s == null) continue;
                if (s.OpenOnStart) { openOnStart = s; break; }
            }
            if (openOnStart == null)
                Core.Log?.Warning($"[menu] no MainMenuScreen with OpenOnStart among {screens.Length}; falling back to first.");
            return openOnStart ?? screens[0];
        }

        private static Button PickTemplate(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Button> buttons)
        {
            // Prefer a known nav button by label so the clone sits in the nav column and matches its styling.
            foreach (string want in PreferredLabels)
            {
                for (int i = 0; i < buttons.Length; i++)
                {
                    Button b = buttons[i];
                    if (b == null || !b.gameObject.activeInHierarchy) continue;
                    string lbl = GetLabel(b.gameObject);
                    if (!string.IsNullOrEmpty(lbl) && lbl.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                        return b;
                }
            }
            // Fallback: first active button.
            for (int i = 0; i < buttons.Length; i++)
                if (buttons[i] != null && buttons[i].gameObject.activeInHierarchy) return buttons[i];
            return buttons.Length > 0 ? buttons[0] : null;
        }

        private static bool BuildEntry(MainMenuScreen home, Button template)
        {
            // Place the Side Hustle entry just above the template button in the nav column, and the separate
            // "Mod Profiles" entry directly below it.
            int idx = template.transform.GetSiblingIndex();
            var main = CloneNavButton(template, MenuButtonName, "Side Hustle", (UnityAction)Hub.OpenScreen, idx);
            if (main == null) return false;
            CloneNavButton(template, ProfilesButtonName, "Mod Profiles",
                (UnityAction)Hub.OpenProfilesScreen, main.transform.GetSiblingIndex() + 1);
            return true;
        }

        /// <summary>Clone a nav button for styling, relabel it, rewire its click to <paramref name="onClick"/> (dropping
        /// every inherited listener so the template's original action does not also fire) and slot it at
        /// <paramref name="siblingIndex"/>. Returns the new GameObject, or null on failure.</summary>
        private static GameObject CloneNavButton(Button template, string name, string label, UnityAction onClick, int siblingIndex)
        {
            Transform parent = template.transform.parent;
            GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, parent, false).Cast<GameObject>();
            clone.transform.localScale = Vector3.one;
            clone.name = name;
            clone.transform.SetSiblingIndex(siblingIndex);

            SetLabel(clone, label);

            Button btn = clone.GetComponent<Button>();
            if (btn == null) { UnityEngine.Object.Destroy(clone); return null; }
            NeutralizeClick(btn);
            btn.onClick.AddListener(onClick);
            btn.interactable = true;

            if (!clone.activeSelf) clone.SetActive(true);
            return clone;
        }

        /// <summary>
        /// While a gamemode profile is running, reshape the home screen: hide the vanilla "Continue"/"New Game"
        /// entries and put a direct entry for the running gamemode in their place. Clicking it opens the same
        /// Singleplayer / Host / Join choice as picking the gamemode from the Side Hustle list. The Side Hustle
        /// entry stays, so the full list (and "Restore my mods") is still reachable. Idempotent: safe to re-run on
        /// every menu (re)initialisation - it adopts an existing gamemode entry and re-hides the campaign buttons.
        /// </summary>
        // The main-menu "Restore my mods" entry, shown in any alt session so the player can drop back to the full
        // installed mod set without digging into the Side Hustle gamemode panel.
        private const string RestoreButtonName = "SideHustle_RestoreButton";

        private static void ApplyProfileMenu(MainMenuScreen home)
        {
            try
            {
                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Button> buttons =
                    home.GetComponentsInChildren<Button>(true);
                if (buttons == null || buttons.Length == 0) return;

                Button template = PickTemplate(buttons);   // Settings, for styling (never one of the hidden entries)
                bool namedProfile = Mods.AltBase.IsNamedProfileSession();

                // Find the topmost campaign slot. Only a POLICY/gamemode profile (a stripped mod set with no campaign
                // save) hides the campaign entries; a NAMED profile keeps them - its Continue is rewired to open the
                // gamemode chooser (ContinueInterstitial), and New Game starts a normal save with the profile's mods.
                Button existing = null;
                int topIdx = int.MaxValue;
                for (int i = 0; i < buttons.Length; i++)
                {
                    Button b = buttons[i];
                    if (b == null) continue;
                    string n = b.gameObject.name;
                    if (n == GamemodeButtonName) { existing = b; continue; }
                    if (n == MenuButtonName || n == RestoreButtonName) continue;
                    string lbl = GetLabel(b.gameObject);
                    if (MatchesAny(lbl, HiddenInProfileLabels))
                    {
                        topIdx = Math.Min(topIdx, b.transform.GetSiblingIndex());
                        if (!namedProfile)
                        {
                            if (b.gameObject.activeSelf) b.gameObject.SetActive(false);   // policy/gamemode profile: no campaign
                        }
                        else if (lbl != null && lbl.IndexOf("continue", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // Named profile: Continue opens the gamemode chooser DIRECTLY (no vanilla save picker), so it
                            // never pops the picker with the chooser overlaid on top. Idempotent - NeutralizeClick drops
                            // any prior wiring first (including our own from an earlier re-init).
                            NeutralizeClick(b);
                            b.onClick.AddListener((UnityAction)Hub.OpenScreen);
                            b.interactable = true;
                        }
                    }
                }

                int slot = topIdx == int.MaxValue ? 0 : topIdx;
                InjectRestoreEntry(buttons, template, slot);

                if (namedProfile) return;   // no gamemode descriptor to inject for a named profile

                GamemodeDescriptor desc = ActiveProfileGamemode();
                if (desc == null) return;   // gamemode mod not registered (missing DLL); the Side Hustle entry recovers it

                if (existing == null)
                {
                    if (template == null) return;
                    if (CloneNavButton(template, GamemodeButtonName, desc.DisplayName,
                            (UnityAction)(() => Hub.OpenGamemode(desc)), slot) != null)
                        Core.Log?.Msg($"[menu] in-profile gamemode entry '{desc.DisplayName}' injected.");
                }
                else
                {
                    // Adopt the existing entry: refresh its label and click target (the registered descriptor instance
                    // can differ after a reload) and make sure it is visible.
                    SetLabel(existing.gameObject, desc.DisplayName);
                    NeutralizeClick(existing);
                    existing.onClick.AddListener((UnityAction)(() => Hub.OpenGamemode(desc)));
                    existing.interactable = true;
                    if (!existing.gameObject.activeSelf) existing.gameObject.SetActive(true);
                }
            }
            catch (Exception e) { Core.Log?.Warning("[menu] profile menu failed: " + e.Message); }
        }

        // Clone (or adopt, on a menu re-init) a home-screen "<- Restore my mods" entry pointing at the full-set
        // restore. Idempotent and gated on there actually being an alt session to restore from.
        private static void InjectRestoreEntry(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Button> buttons,
            Button template, int slot)
        {
            try
            {
                if (!Mods.ModSwitcher.HasRestorePending) return;
                const string label = "← Restore mods";
                for (int i = 0; i < buttons.Length; i++)
                {
                    Button b = buttons[i];
                    if (b == null || b.gameObject.name != RestoreButtonName) continue;
                    SetLabel(b.gameObject, label);
                    NeutralizeClick(b);
                    b.onClick.AddListener((UnityAction)Mods.ModSwitcher.RestoreAndRestart);
                    b.interactable = true;
                    if (!b.gameObject.activeSelf) b.gameObject.SetActive(true);
                    return;   // adopted the existing one
                }
                if (template != null &&
                    CloneNavButton(template, RestoreButtonName, label, (UnityAction)Mods.ModSwitcher.RestoreAndRestart, slot) != null)
                    Core.Log?.Msg("[menu] Restore-my-mods entry injected.");
            }
            catch (Exception e) { Core.Log?.Warning("[menu] restore entry failed: " + e.Message); }
        }

        /// <summary>The gamemode whose profile the current process is running, or null (no policy session, or its mod
        /// is not registered in this session).</summary>
        private static GamemodeDescriptor ActiveProfileGamemode()
        {
            if (!Mods.AltBase.IsAltSession()) return null;
            string id = Preferences.ActiveGamemodeId;
            if (string.IsNullOrEmpty(id)) return null;
            return API.Registered.FirstOrDefault(d => d != null && d.Id == id);
        }

        private static bool MatchesAny(string label, string[] wants)
        {
            if (string.IsNullOrEmpty(label)) return false;
            for (int i = 0; i < wants.Length; i++)
                if (label.IndexOf(wants[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static void NeutralizeClick(Button btn)
        {
            try
            {
                btn.onClick.RemoveAllListeners();
                int n = btn.onClick.GetPersistentEventCount();
                for (int i = 0; i < n; i++)
                    btn.onClick.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
            }
            catch (Exception e) { Core.Log?.Warning("[menu] neutralize click failed: " + e.Message); }
        }

        // --- label helpers (handle both TMP and legacy uGUI Text) ---

        private static string GetLabel(GameObject go)
        {
            var tmp = go.GetComponentInChildren<Il2CppTMPro.TextMeshProUGUI>(true);
            if (tmp != null && !string.IsNullOrEmpty(tmp.text)) return tmp.text;
            var txt = go.GetComponentInChildren<Text>(true);
            if (txt != null && !string.IsNullOrEmpty(txt.text)) return txt.text;
            return null;
        }

        private static void SetLabel(GameObject go, string label)
        {
            var tmp = go.GetComponentInChildren<Il2CppTMPro.TextMeshProUGUI>(true);
            if (tmp != null)
            {
                tmp.text = label;
                // Our labels ("Mod Profiles", "Restore my mods") can be wider than the cloned nav button; keep them on
                // one line (extend, not wrap) so a longer entry does not break onto a second row like the vanilla ones.
                try { tmp.enableWordWrapping = false; tmp.overflowMode = Il2CppTMPro.TextOverflowModes.Overflow; } catch { }
                return;
            }
            var txt = go.GetComponentInChildren<Text>(true);
            if (txt != null) { txt.text = label; try { txt.horizontalOverflow = HorizontalWrapMode.Overflow; } catch { } }
        }
    }
}
