using System;
using System.Collections.Generic;
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
                // OnSceneWasInitialized -> Reset more than once), so injection may run again after our button already
                // exists. If it does, adopt the existing entry and stop - never add a second "Side Hustle" button.
                for (int i = 0; i < buttons.Length; i++)
                {
                    if (buttons[i] != null && buttons[i].gameObject.name == MenuButtonName)
                    {
                        _injectedThisScene = true;
                        Hub.RememberHome(home);
                        return;
                    }
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
            Transform parent = template.transform.parent;
            GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, parent, false).Cast<GameObject>();
            clone.transform.localScale = Vector3.one;
            clone.name = MenuButtonName;
            // Place it just above the template button in the nav column.
            int idx = template.transform.GetSiblingIndex();
            clone.transform.SetSiblingIndex(idx);

            // Relabel (TMP first, then legacy Text).
            SetLabel(clone, "Side Hustle");

            // Rewire the click: drop every inherited listener (runtime + inspector-wired persistent) so the
            // template's original action (e.g. open Settings) does not also fire, then add ours.
            Button btn = clone.GetComponent<Button>();
            if (btn == null) { UnityEngine.Object.Destroy(clone); return false; }
            NeutralizeClick(btn);
            btn.onClick.AddListener((UnityAction)Hub.OpenScreen);
            btn.interactable = true;

            if (!clone.activeSelf) clone.SetActive(true);
            return true;
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
            if (tmp != null) { tmp.text = label; return; }
            var txt = go.GetComponentInChildren<Text>(true);
            if (txt != null) txt.text = label;
        }
    }
}
