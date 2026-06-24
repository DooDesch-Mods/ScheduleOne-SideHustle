using System;
using Il2CppInterop.Runtime;
using Il2CppScheduleOne.UI.MainMenu;
using SideHustle.Internal;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SideHustle.Menu
{
    /// <summary>
    /// The gamemode-selection menu. It is a clone of the vanilla "New Game" (Select Save Slot) screen, driven as a
    /// real <see cref="MainMenuScreen"/>, so it looks 1:1 like the game's own menu and gets ESC back for free (the
    /// home screen is its PreviousScreen). RMB back is polled (the menu's MainMenuScreen path ignores RightClick).
    /// Each save slot is repurposed into one registered gamemode. Selecting one hides the menu and invokes its launch
    /// callback; the gamemode calls LaunchContext.ReturnToHub when done, which routes back to <see cref="OnReturn"/>.
    /// </summary>
    internal static class Hub
    {
        private static bool _initialized;
        private static MainMenuScreen _home;
        private static LaunchContext _activeCtx;

        // The clone of the vanilla New Game screen (our gamemode menu) + its screen component.
        private static GameObject _clone;
        private static MainMenuScreen _cloneScreen;

        // Gamemode rows are taller than the vanilla 70px save slots so each gamemode gets room to breathe; the name
        // and description are centred as a group inside the taller row. The panel is sized to the row count so it is
        // always nicely filled (never empty rows / dead space), and rendered at native scale (no zoom) for crisp text.
        private const float SlotHeight = 96f;
        private const float SlotSpacing = 8f;
        private const float NameOffsetY = 12f;    // name centre, above the row centre
        private const float DescOffsetY = -13f;   // description centre, below the row centre
        private const float PanelChrome = 92f;    // title band + bottom padding around the row list
        private const float PanelWidth = 800f;

        internal static void EnsureInit()
        {
            if (_initialized) return;
            _initialized = true;
            HubBridge.ReturnHandler = OnReturn;
        }

        internal static void RememberHome(MainMenuScreen home) => _home = home;

        /// <summary>Tear everything down when the Menu scene unloads, so we rebuild fresh next time.</summary>
        internal static void Teardown()
        {
            try { if (_clone != null) UnityEngine.Object.Destroy(_clone); }
            catch { /* ignore */ }
            _clone = null;
            _cloneScreen = null;
            _activeCtx = null;
        }

        /// <summary>Per-frame (menu scene): RMB closes our screen like the game's own back input. ESC is native.</summary>
        internal static void TickInput()
        {
            if (_cloneScreen == null || !_cloneScreen.IsOpen) return;
            if (Input.GetMouseButtonDown(1)) _cloneScreen.Close(openPrevious: true);
        }

        // --- open / close ---

        /// <summary>Open the gamemode menu (called by the injected main-menu button). Closes the home screen.</summary>
        internal static void OpenScreen()
        {
            EnsureInit();
            EnsureClone();
            if (_cloneScreen == null) { Core.Log?.Warning("[hub] gamemode screen unavailable."); return; }
            RebuildRows();
            _cloneScreen.Open(closePrevious: true);
        }

        /// <summary>
        /// Clone the vanilla New Game screen once and drive it as our gamemode menu. Instantiate remaps references
        /// inside the cloned subtree (its CanvasGroup, slot rows), so the clone is a self-contained MainMenuScreen.
        /// </summary>
        internal static void EnsureClone()
        {
            if (_clone != null && _cloneScreen != null) return;
            try
            {
                var screens = UnityEngine.Object.FindObjectsOfType<NewGameScreen>(true);
                if (screens == null || screens.Length == 0) { Core.Log?.Warning("[hub] NewGameScreen not found; cannot build the gamemode screen."); return; }
                NewGameScreen ng = screens[0];

                _clone = UnityEngine.Object.Instantiate(ng.gameObject, ng.transform.parent, false).Cast<GameObject>();
                _clone.name = "SideHustle_GamemodeScreen";
                _cloneScreen = _clone.GetComponent<MainMenuScreen>();
                if (_cloneScreen != null && _home != null) _cloneScreen.PreviousScreen = _home;

                // The vanilla SaveDisplay component fills each slot's Organisation/net-worth/etc. from the real saves
                // (in Awake + on every onSaveInfoLoaded). Remove it before Awake so our gamemode labels are not
                // overwritten and the slots are not re-bound to save files.
                try
                {
                    var displays = _clone.GetComponentsInChildren<SaveDisplay>(true);
                    for (int i = 0; i < displays.Length; i++)
                        if (displays[i] != null) UnityEngine.Object.DestroyImmediate(displays[i]);
                }
                catch (Exception e) { Core.Log?.Warning("[hub] remove SaveDisplay: " + e.Message); }

                // The original is inactive, so the clone's Awake has not run. Awake (OpenOnStart=false) would
                // deactivate the object and reset IsOpen on first activation, undoing the first Open(). Run it once
                // now (it self-deactivates and registers the ESC exit listener) so later Open() behaves.
                _clone.SetActive(true);
                Core.Log?.Msg("[hub] cloned NewGameScreen -> gamemode screen.");
            }
            catch (Exception e) { Core.Log?.Warning("[hub] EnsureClone failed: " + e); }
        }

        // --- repurpose the save slots into gamemode rows ---

        private static void RebuildRows()
        {
            if (_clone == null) return;
            try
            {
                SetTmp(_clone.transform, "Title", "Choose a gamemode");

                Transform container = _clone.transform.Find("Container");
                if (container == null) { Core.Log?.Warning("[hub] slot container not found."); return; }

                // A touch more air between the taller rows than the vanilla 5px.
                var vlg = container.GetComponent<VerticalLayoutGroup>();
                if (vlg != null) vlg.spacing = SlotSpacing;

                var modes = API.Registered;

                // Make sure there are at least as many slot rows as gamemodes (clone the first slot as a template).
                if (container.childCount > 0)
                {
                    GameObject template = container.GetChild(0).gameObject;
                    int guard = 0;
                    while (container.childCount < modes.Count && guard++ < 64)
                        UnityEngine.Object.Instantiate(template, container, false);
                }

                int gi = 0;
                for (int i = 0; i < container.childCount; i++)
                {
                    Transform slot = container.GetChild(i);
                    if (gi < modes.Count)
                    {
                        RepurposeSlot(slot, modes[gi]);
                        slot.gameObject.SetActive(true);
                        gi++;
                    }
                    else
                    {
                        slot.gameObject.SetActive(false);
                    }
                }

                // Size the panel to the number of gamemodes so the slot list fills it (no empty rows / dead space),
                // exactly like the vanilla screen is full of save slots. The scaler renders it bigger on top of this.
                int n = Mathf.Max(1, modes.Count);
                float height = PanelChrome + n * SlotHeight + (n - 1) * SlotSpacing;
                var rootRt = _clone.GetComponent<RectTransform>();
                if (rootRt != null) rootRt.sizeDelta = new Vector2(PanelWidth, height);
            }
            catch (Exception e) { Core.Log?.Warning("[hub] RebuildRows failed: " + e); }
        }

        // One save-slot card -> one gamemode, reusing the SAME native widgets in their native positions so the row
        // is laid out 1:1 like a save slot: the gamemode name where the save's organisation name is (top), the
        // description where the save's stat row is (bottom, same grey sub-text), and the author in the bottom-right
        // "Version" corner. Save-only fields are hidden; the slot's click launches the gamemode.
        private static void RepurposeSlot(Transform slot, GamemodeDescriptor desc)
        {
            // SaveDisplay hides the Info block for empty save slots; force it visible so the gamemode info shows.
            Transform info = slot.Find("Container/Info");
            if (info != null) info.gameObject.SetActive(true);

            // Make the row taller than a vanilla save slot so the gamemode has room to breathe (native scale, no zoom).
            var slotRt = slot.GetComponent<RectTransform>();
            if (slotRt != null) slotRt.sizeDelta = new Vector2(slotRt.sizeDelta.x, SlotHeight);
            var le = slot.GetComponent<LayoutElement>();
            if (le == null) le = slot.gameObject.AddComponent<LayoutElement>();
            le.minHeight = SlotHeight;
            le.preferredHeight = SlotHeight;

            // Name -> the native "Organisation" widget (18px white). Centre it as a group with the description (the
            // taller row would otherwise leave the name stranded at the very top), keeping the native left alignment.
            Transform orgT = slot.Find("Container/Info/Organisation");
            if (orgT != null)
            {
                CentreRow(orgT, NameOffsetY);
                var tmp = orgT.GetComponent<Il2CppTMPro.TextMeshProUGUI>();
                if (tmp != null) tmp.text = desc.DisplayName;
            }

            // Description -> the native "Net worth" stat widget (same 12px grey sub-text the save uses). Sit it just
            // below the name (mirroring its horizontal span) and recolour it to the native grey. Hide its value child.
            Transform nwT = slot.Find("Container/Info/NetWorth");
            if (nwT != null)
            {
                CentreRow(nwT, DescOffsetY);
                var tmp = nwT.GetComponent<Il2CppTMPro.TextMeshProUGUI>();
                if (tmp != null)
                {
                    tmp.text = string.IsNullOrWhiteSpace(desc.Description) ? "" : desc.Description.Trim();
                    tmp.color = new Color(0.627f, 0.627f, 0.627f, 1f);
                    tmp.enableWordWrapping = false;
                    tmp.overflowMode = Il2CppTMPro.TextOverflowModes.Ellipsis;
                }
                HideChild(slot, "Container/Info/NetWorth/Text");   // the green money value
            }

            // Author -> the native bottom-right "Version" corner (grey, small). Hidden if no author was given.
            Transform verT = slot.Find("Container/Info/Version");
            if (verT != null)
            {
                var vtmp = verT.GetComponent<Il2CppTMPro.TextMeshProUGUI>();
                if (vtmp != null && !string.IsNullOrWhiteSpace(desc.Author))
                {
                    vtmp.text = "by " + desc.Author.Trim();
                    verT.gameObject.SetActive(true);
                }
                else verT.gameObject.SetActive(false);
            }

            // The remaining save-specific fields make no sense for a gamemode - hide them.
            HideChild(slot, "Container/Info/Created");
            HideChild(slot, "Container/Info/LastPlayed");
            HideChild(slot, "Container/Info/Export");
            HideChild(slot, "Container/Import");

            // Both slot buttons call SlotSelected by default; clear them and wire our launch.
            WireSlotButton(slot, "Button", desc);
            WireSlotButton(slot, "Container/Button", desc);
        }

        private static void WireSlotButton(Transform slot, string path, GamemodeDescriptor desc)
        {
            Transform t = slot.Find(path);
            if (t == null) return;
            Button b = t.GetComponent<Button>();
            if (b == null) return;
            NeutralizeClick(b);
            GamemodeDescriptor d = desc;
            b.onClick.AddListener((UnityAction)(() => OnSelectGamemode(d)));
            b.interactable = true;
        }

        private static void OnSelectGamemode(GamemodeDescriptor desc)
        {
            // Multiplayer launch (Host/Join) is not available yet; only singleplayer/hybrid launch.
            if (desc.AllowsSingleplayer) LaunchSingleplayer(desc);
            else Core.Log?.Msg($"Gamemode '{desc.Id}' is multiplayer-only; multiplayer launch is not available yet.");
        }

        // --- launch / return ---

        private static void LaunchSingleplayer(GamemodeDescriptor desc)
        {
            if (desc.OnLaunchSingleplayer == null)
            {
                Core.Log?.Warning($"Gamemode '{desc.Id}' has no singleplayer launch callback.");
                return;
            }

            _activeCtx = new LaunchContext { Descriptor = desc, IsHost = null, LobbyId = 0 };
            // Close our screen without reopening home (home is already closed), so the gamemode overlay is unobstructed.
            if (_cloneScreen != null) _cloneScreen.Close(openPrevious: false);
            Core.Log?.Msg($"Launching gamemode '{desc.DisplayName}' (singleplayer).");
            try { desc.OnLaunchSingleplayer(_activeCtx); }
            catch (Exception e)
            {
                Core.Log?.Error($"Gamemode '{desc.Id}' launch threw: {e}");
                OnReturn(_activeCtx);
            }
        }

        private static void OnReturn(LaunchContext ctx)
        {
            try { ctx?.Descriptor?.OnExitToHub?.Invoke(ctx); }
            catch (Exception e) { Core.Log?.Warning("OnExitToHub threw: " + e.Message); }

            EnsureClone();
            RebuildRows();
            if (_cloneScreen != null) _cloneScreen.Open(closePrevious: true);
            _activeCtx = null;
        }

        // --- helpers ---

        private static void SetTmp(Transform root, string path, string text)
        {
            Transform t = root.Find(path);
            if (t == null) return;
            var tmp = t.GetComponent<Il2CppTMPro.TextMeshProUGUI>();
            if (tmp != null) tmp.text = text;
        }

        private static void HideChild(Transform root, string path)
        {
            Transform t = root.Find(path);
            if (t != null) t.gameObject.SetActive(false);
        }

        // Re-anchor a slot text widget to span the row at its vertical centre, offset by <code>dy</code> px (so the
        // name + description form a centred group), keeping the native 30px left inset and 30px right inset.
        private static void CentreRow(Transform t, float dy)
        {
            var rt = t.GetComponent<RectTransform>();
            if (rt == null) return;
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(1f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(30f, dy);
            rt.sizeDelta = new Vector2(-60f, 25f);
        }

        private static void NeutralizeClick(Button btn)
        {
            try
            {
                btn.onClick.RemoveAllListeners();
                int n = btn.onClick.GetPersistentEventCount();
                for (int i = 0; i < n; i++)
                    btn.onClick.SetPersistentListenerState(i, UnityEventCallState.Off);
            }
            catch (Exception e) { Core.Log?.Warning("[hub] neutralize click failed: " + e.Message); }
        }
    }
}
