using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using Il2CppScheduleOne.UI.MainMenu;
using SideHustle.Config;
using SideHustle.Internal;
using SideHustle.Multiplayer;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace SideHustle.Menu
{
    /// <summary>
    /// The gamemode menu. It is a clone of the vanilla "New Game" (Select Save Slot) screen, driven as a real
    /// <see cref="MainMenuScreen"/>, so it looks 1:1 like the game's own menu and gets ESC back for free. Every view
    /// - the gamemode list, the Singleplayer / Host / Join choice, the host size picker, and the server browser -
    /// renders as native save-slot rows in this one screen, swapping the rows in place. A "Back" row steps one level
    /// up; right-click does the same; ESC closes the whole menu (its PreviousScreen is the home screen).
    /// </summary>
    internal static class Hub
    {
        private static bool _initialized;
        private static MainMenuScreen _home;
        private static LaunchContext _activeCtx;

        // The clone of the vanilla New Game screen + its screen component.
        private static GameObject _clone;
        private static MainMenuScreen _cloneScreen;

        // Current view's "back" step (null on the root gamemode list, where back closes the menu) + the gamemode the
        // multiplayer sub-views belong to (for the server-browser refresh callback).
        private static Action _back;
        private static GamemodeDescriptor _mpDesc;

        // Rows are taller than the vanilla 70px save slots so each entry gets room to breathe; the name and subtitle
        // are centred as a group. The panel is sized to the row count so it is always nicely filled, at native scale.
        private const float SlotHeight = 96f;
        private const float SlotSpacing = 8f;
        private const float NameOffsetY = 12f;
        private const float DescOffsetY = -13f;
        private const float PanelChrome = 92f;
        private const float PanelWidth = 800f;
        private const float TextInset = 30f;
        private const float TextInsetWithIcon = 96f;

        internal static void EnsureInit()
        {
            if (_initialized) return;
            _initialized = true;
            HubBridge.ReturnHandler = OnReturn;
        }

        internal static void RememberHome(MainMenuScreen home) => _home = home;

        internal static void Teardown()
        {
            try { if (_clone != null) UnityEngine.Object.Destroy(_clone); }
            catch { /* ignore */ }
            _clone = null;
            _cloneScreen = null;
            _activeCtx = null;
            _back = null;
            _mpDesc = null;
        }

        /// <summary>Per-frame (menu scene): right-click steps one view back, or closes the menu at the root. ESC is native.</summary>
        internal static void TickInput()
        {
            if (_cloneScreen == null || !_cloneScreen.IsOpen) return;
            if (Input.GetMouseButtonDown(1))
            {
                if (_back != null) _back();
                else _cloneScreen.Close(openPrevious: true);
            }
        }

        // --- open / clone ---

        /// <summary>Open the gamemode menu (called by the injected main-menu button). Closes the home screen.</summary>
        internal static void OpenScreen()
        {
            EnsureInit();
            EnsureClone();
            if (_cloneScreen == null) { Core.Log?.Warning("[hub] gamemode screen unavailable."); return; }
            ShowGamemodeList();
            _cloneScreen.Open(closePrevious: true);
        }

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

                // Remove SaveDisplay before Awake so our labels are not overwritten / slots not re-bound to save files.
                try
                {
                    var displays = _clone.GetComponentsInChildren<SaveDisplay>(true);
                    for (int i = 0; i < displays.Length; i++)
                        if (displays[i] != null) UnityEngine.Object.DestroyImmediate(displays[i]);
                }
                catch (Exception e) { Core.Log?.Warning("[hub] remove SaveDisplay: " + e.Message); }

                // Run Awake once now (it self-deactivates and registers the ESC exit listener) so later Open() behaves.
                _clone.SetActive(true);
                Core.Log?.Msg("[hub] cloned NewGameScreen -> gamemode screen.");
            }
            catch (Exception e) { Core.Log?.Warning("[hub] EnsureClone failed: " + e); }
        }

        // --- the views (each one swaps the screen's rows in place) ---

        private sealed class Row
        {
            public string Name;
            public string Subtitle;
            public string Corner;
            public Sprite Icon;
            public Action OnClick;
        }

        private static void ShowGamemodeList()
        {
            _back = null;
            _mpDesc = null;

            // Recently played first, then the rest in registration order.
            var recent = Preferences.RecentlyPlayed;
            var modes = API.Registered
                .OrderBy(m => { int i = recent.IndexOf(m.Id); return i < 0 ? int.MaxValue : i; })
                .ToList();

            var rows = new List<Row>();

            // If a gamemode policy turned some of your mods off, offer to put them back (and restart).
            if (Mods.ModSwitcher.HasRestorePending)
                rows.Add(new Row
                {
                    Name = "Restore my mods",
                    Subtitle = "Re-enable the mods a gamemode turned off, and restart.",
                    Corner = "Mods",
                    OnClick = () => Mods.ModSwitcher.RestoreAndRestart()
                });

            foreach (var d in modes)
            {
                GamemodeDescriptor desc = d;
                string author = string.IsNullOrWhiteSpace(desc.Author) ? "" : "by " + desc.Author.Trim() + "   ";
                rows.Add(new Row
                {
                    Name = desc.DisplayName,
                    Subtitle = desc.Description,
                    Corner = author + SupportBadge(desc.Support),
                    Icon = IconOf(desc),
                    OnClick = () => OnSelectGamemode(desc)
                });
            }
            ShowRows("Choose a gamemode", rows);
        }

        private static void ShowModeChoice(GamemodeDescriptor desc)
        {
            _mpDesc = desc;
            _back = ShowGamemodeList;
            var rows = new List<Row>();
            if (desc.AllowsSingleplayer)
                rows.Add(new Row { Name = "Singleplayer", Subtitle = "Play on your own.", OnClick = () => LaunchSelected(desc) });
            rows.Add(new Row { Name = "Host", Subtitle = "Open a session others can join.", OnClick = () => ShowHostSizes(desc) });
            rows.Add(new Row { Name = "Join", Subtitle = "Browse and join an open session.", OnClick = () => ShowBrowser(desc) });
            rows.Add(new Row { Name = "Back", Subtitle = "Back to the gamemode list.", OnClick = ShowGamemodeList });
            ShowRows(desc.DisplayName, rows);
        }

        private static void ShowHostSizes(GamemodeDescriptor desc)
        {
            _mpDesc = desc;
            _back = () => ShowModeChoice(desc);
            var rows = new List<Row>();
            foreach (int max in new[] { 4, 8, 16 })
            {
                int m = max;
                rows.Add(new Row
                {
                    Name = $"{m} players",
                    Subtitle = m > 4 ? "Bigger lobby (needs BiggerLobbies)." : "Standard lobby size.",
                    OnClick = () => StartHost(desc, m)
                });
            }
            rows.Add(new Row { Name = "Back", Subtitle = "Back to the options.", OnClick = () => ShowModeChoice(desc) });
            ShowRows("Host: " + desc.DisplayName, rows);
        }

        private static void ShowBrowser(GamemodeDescriptor desc)
        {
            _mpDesc = desc;
            _back = () => ShowModeChoice(desc);
            ShowRows("Join: " + desc.DisplayName, BrowserRows(desc, null, "Searching for sessions..."));
            ServerBrowser.BeginQuery(desc.Id, results =>
            {
                // Only apply if the browser for this gamemode is still the active view.
                if (_cloneScreen != null && _cloneScreen.IsOpen && _mpDesc == desc)
                    ShowRows("Join: " + desc.DisplayName, BrowserRows(desc, results, null));
            });
        }

        private static List<Row> BrowserRows(GamemodeDescriptor desc, List<LobbyRow> lobbies, string status)
        {
            var rows = new List<Row>();
            if (status != null)
            {
                rows.Add(new Row { Name = status, Subtitle = "" });
            }
            else if (lobbies == null || lobbies.Count == 0)
            {
                rows.Add(new Row { Name = "No open sessions", Subtitle = "Host one, or refresh to look again." });
            }
            else
            {
                foreach (var l in lobbies.Take(8))
                {
                    LobbyRow row = l;
                    string host = string.IsNullOrEmpty(row.HostName) ? "Session" : row.HostName;
                    string cap = row.MaxPlayers > 0 ? $"{row.Members} / {row.MaxPlayers} players" : $"{row.Members} player(s)";
                    rows.Add(new Row
                    {
                        Name = host + (row.HasPassword ? "  (locked)" : ""),
                        Subtitle = cap,
                        Corner = "Join",
                        OnClick = () => StartJoin(desc, row)
                    });
                }
            }
            rows.Add(new Row { Name = "Refresh", Subtitle = "Look for sessions again.", OnClick = () => ShowBrowser(desc) });
            rows.Add(new Row { Name = "Back", Subtitle = "Back to the options.", OnClick = () => ShowModeChoice(desc) });
            return rows;
        }

        // --- render a row set into the cloned screen's slots ---

        private static void ShowRows(string title, List<Row> rows)
        {
            if (_clone == null) return;
            try
            {
                SetTmp(_clone.transform, "Title", title);

                Transform container = _clone.transform.Find("Container");
                if (container == null) { Core.Log?.Warning("[hub] slot container not found."); return; }

                var vlg = container.GetComponent<VerticalLayoutGroup>();
                if (vlg != null) vlg.spacing = SlotSpacing;

                // Make sure there are at least as many slot rows as entries (clone the first slot as a template).
                if (container.childCount > 0)
                {
                    GameObject template = container.GetChild(0).gameObject;
                    int guard = 0;
                    while (container.childCount < rows.Count && guard++ < 64)
                        UnityEngine.Object.Instantiate(template, container, false);
                }

                for (int i = 0; i < container.childCount; i++)
                {
                    Transform slot = container.GetChild(i);
                    if (i < rows.Count)
                    {
                        RepurposeRow(slot, rows[i]);
                        slot.gameObject.SetActive(true);
                    }
                    else
                    {
                        slot.gameObject.SetActive(false);
                    }
                }

                int n = Mathf.Max(1, rows.Count);
                float height = PanelChrome + n * SlotHeight + (n - 1) * SlotSpacing;
                var rootRt = _clone.GetComponent<RectTransform>();
                if (rootRt != null) rootRt.sizeDelta = new Vector2(PanelWidth, height);
            }
            catch (Exception e) { Core.Log?.Warning("[hub] ShowRows failed: " + e); }
        }

        // One native save-slot card -> one row, reusing the SAME native widgets in their native positions: the name
        // where the save's organisation name is (top), the subtitle where the save's stat row is (bottom grey
        // sub-text), an optional corner badge in the bottom-right "Version" corner, and an optional left icon.
        private static void RepurposeRow(Transform slot, Row row)
        {
            Transform info = slot.Find("Container/Info");
            if (info != null) info.gameObject.SetActive(true);

            var slotRt = slot.GetComponent<RectTransform>();
            if (slotRt != null) slotRt.sizeDelta = new Vector2(slotRt.sizeDelta.x, SlotHeight);
            var le = slot.GetComponent<LayoutElement>();
            if (le == null) le = slot.gameObject.AddComponent<LayoutElement>();
            le.minHeight = SlotHeight;
            le.preferredHeight = SlotHeight;

            float inset = SetIcon(slot, row.Icon) ? TextInsetWithIcon : TextInset;

            Transform orgT = slot.Find("Container/Info/Organisation");
            if (orgT != null)
            {
                CentreRow(orgT, NameOffsetY, inset);
                var tmp = orgT.GetComponent<Il2CppTMPro.TextMeshProUGUI>();
                if (tmp != null) tmp.text = row.Name ?? "";
            }

            Transform nwT = slot.Find("Container/Info/NetWorth");
            if (nwT != null)
            {
                CentreRow(nwT, DescOffsetY, inset);
                var tmp = nwT.GetComponent<Il2CppTMPro.TextMeshProUGUI>();
                if (tmp != null)
                {
                    tmp.text = string.IsNullOrWhiteSpace(row.Subtitle) ? "" : row.Subtitle.Trim();
                    tmp.color = new Color(0.627f, 0.627f, 0.627f, 1f);
                    tmp.enableWordWrapping = false;
                    tmp.overflowMode = Il2CppTMPro.TextOverflowModes.Ellipsis;
                }
                HideChild(slot, "Container/Info/NetWorth/Text");
            }

            Transform verT = slot.Find("Container/Info/Version");
            if (verT != null)
            {
                var vtmp = verT.GetComponent<Il2CppTMPro.TextMeshProUGUI>();
                if (vtmp != null)
                {
                    bool has = !string.IsNullOrWhiteSpace(row.Corner);
                    vtmp.text = has ? row.Corner : "";
                    verT.gameObject.SetActive(has);
                }
            }

            HideChild(slot, "Container/Info/Created");
            HideChild(slot, "Container/Info/LastPlayed");
            HideChild(slot, "Container/Info/Export");
            HideChild(slot, "Container/Import");

            WireRowButton(slot, "Button", row.OnClick);
            WireRowButton(slot, "Container/Button", row.OnClick);
        }

        private static void WireRowButton(Transform slot, string path, Action onClick)
        {
            Transform t = slot.Find(path);
            if (t == null) return;
            Button b = t.GetComponent<Button>();
            if (b == null) return;
            NeutralizeClick(b);
            Action cb = onClick;
            b.onClick.AddListener((UnityAction)(() => { try { cb?.Invoke(); } catch (Exception e) { Core.Log?.Warning("[hub] row click failed: " + e.Message); } }));
            b.interactable = cb != null;
        }

        // --- selection / launch ---

        private static void OnSelectGamemode(GamemodeDescriptor desc)
        {
            // A gamemode with a mod policy: if it would change which mods are loaded, confirm first (and restart).
            // After the restart the mods are already correct, so the resolver reports no changes and we fall through.
            if (desc.Policy != null)
            {
                var plan = Mods.ModPolicyResolver.Resolve(desc);
                if (plan.Blocked || plan.HasChanges) { ShowModCheck(desc, plan); return; }
            }
            if (desc.AllowsMultiplayer) ShowModeChoice(desc);
            else LaunchSelected(desc);
        }

        /// <summary>Continue into a gamemode after a mod-policy restart (mods are already in the right state).</summary>
        internal static void ContinueGamemode(string id)
        {
            EnsureInit();
            EnsureClone();
            if (_cloneScreen == null) return;
            if (!_cloneScreen.IsOpen) { ShowGamemodeList(); _cloneScreen.Open(closePrevious: true); }
            var desc = API.Registered.FirstOrDefault(d => d.Id == id);
            if (desc != null) OnSelectGamemode(desc);
            else Core.Log?.Warning($"[hub] could not continue into '{id}' after restart (gamemode not registered). " +
                                   "The gamemode list (with 'Restore my mods') is shown so you are not stuck.");
        }

        private static void ShowModCheck(GamemodeDescriptor desc, Mods.ModPlan plan)
        {
            _mpDesc = desc;
            _back = ShowGamemodeList;
            var rows = new List<Row>();

            if (plan.MissingRequired.Count > 0)
                rows.Add(new Row { Name = "Missing - install first", Subtitle = string.Join(", ", plan.MissingRequired) });
            if (plan.ToDisable.Count > 0)
                rows.Add(new Row { Name = $"Will disable ({plan.ToDisable.Count})", Subtitle = FriendlyNames(plan.ToDisable) });
            if (plan.ToEnable.Count > 0)
                rows.Add(new Row { Name = $"Will enable ({plan.ToEnable.Count})", Subtitle = string.Join(", ", plan.ToEnable.Select(StripDll)) });

            if (plan.Blocked)
            {
                rows.Add(new Row { Name = "Cannot start yet", Subtitle = "Install the missing mod(s), then try again." });
            }
            else
            {
                rows.Add(new Row
                {
                    Name = "Confirm and restart",
                    Subtitle = "The game restarts to apply these mod changes, then opens this gamemode.",
                    OnClick = () => { Preferences.RecordLaunch(desc.Id); Mods.ModSwitcher.ApplyPolicyAndRestart(desc, plan); }
                });
            }
            rows.Add(new Row { Name = "Cancel", Subtitle = "Back to the gamemode list.", OnClick = ShowGamemodeList });
            ShowRows(desc.DisplayName + " - mod check", rows);
        }

        private static string StripDll(string f) =>
            f != null && f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? f.Substring(0, f.Length - 4) : f;

        private static string FriendlyNames(List<string> files)
        {
            var loaded = Mods.ModInventory.Loaded();
            return string.Join(", ", files.Select(f =>
                loaded.FirstOrDefault(m => string.Equals(m.File, f, StringComparison.OrdinalIgnoreCase))?.Name ?? StripDll(f)));
        }

        /// <summary>Launch a gamemode in singleplayer: a MenuSpace overlay, or a World boot for World gamemodes.</summary>
        internal static void LaunchSelected(GamemodeDescriptor desc)
        {
            Preferences.RecordLaunch(desc.Id);
            if (desc.Surface == GamemodeSurface.World)
            {
                CloseHubScreen();
                MultiplayerCoordinator.StartWorldSingleplayer(desc);
            }
            else
            {
                LaunchSingleplayer(desc);
            }
        }

        private static void StartHost(GamemodeDescriptor desc, int maxPlayers)
        {
            Preferences.RecordLaunch(desc.Id);
            CloseHubScreen();
            MultiplayerCoordinator.StartHost(desc, new HostOptions { MaxPlayers = maxPlayers });
        }

        private static void StartJoin(GamemodeDescriptor desc, LobbyRow row)
        {
            Preferences.RecordLaunch(desc.Id);
            CloseHubScreen();
            MultiplayerCoordinator.StartJoin(desc, row);
        }

        /// <summary>Close the gamemode screen without reopening the home menu (used when a session is launching).</summary>
        internal static void CloseHubScreen()
        {
            if (_cloneScreen != null) _cloneScreen.Close(openPrevious: false);
        }

        /// <summary>Reopen the gamemode list after a session that did NOT reload the scene (MenuSpace multiplayer).</summary>
        internal static void ReopenAfterSession()
        {
            try
            {
                EnsureClone();
                ShowGamemodeList();
                if (_cloneScreen != null) _cloneScreen.Open(closePrevious: true);
            }
            catch (Exception e) { Core.Log?.Warning("[hub] reopen failed: " + e.Message); }
        }

        private static void LaunchSingleplayer(GamemodeDescriptor desc)
        {
            if (desc.OnLaunchSingleplayer == null)
            {
                Core.Log?.Warning($"Gamemode '{desc.Id}' has no singleplayer launch callback.");
                return;
            }
            _activeCtx = new LaunchContext { Descriptor = desc, IsHost = null, LobbyId = 0 };
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
            ShowGamemodeList();
            if (_cloneScreen != null) _cloneScreen.Open(closePrevious: true);
            _activeCtx = null;
        }

        // --- helpers ---

        private static string SupportBadge(GamemodeSupport s)
        {
            switch (s)
            {
                case GamemodeSupport.Multiplayer: return "Multiplayer";
                case GamemodeSupport.Hybrid: return "SP + MP";
                default: return "Singleplayer";
            }
        }

        /// <summary>Resolve a row icon from the descriptor (wrapping IconTex into a Sprite once, cached on the descriptor).</summary>
        private static Sprite IconOf(GamemodeDescriptor desc)
        {
            if (desc.Icon != null) return desc.Icon;
            if (desc.IconTex != null)
            {
                try
                {
                    desc.Icon = Sprite.Create(desc.IconTex, new Rect(0, 0, desc.IconTex.width, desc.IconTex.height), new Vector2(0.5f, 0.5f));
                    return desc.Icon;
                }
                catch { /* ignore */ }
            }
            return null;
        }

        /// <summary>Show/hide a left-aligned icon image on the row. Returns true when an icon is shown.</summary>
        private static bool SetIcon(Transform slot, Sprite sprite)
        {
            Transform info = slot.Find("Container/Info");
            Transform parent = info != null ? info : slot;
            Transform iconT = parent.Find("SH_Icon");
            if (sprite == null)
            {
                if (iconT != null) iconT.gameObject.SetActive(false);
                return false;
            }
            Image img;
            if (iconT == null)
            {
                var go = new GameObject("SH_Icon");
                go.transform.SetParent(parent, false);
                img = go.AddComponent<Image>();
                var rt = go.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0f, 0.5f);
                rt.anchorMax = new Vector2(0f, 0.5f);
                rt.pivot = new Vector2(0f, 0.5f);
                rt.sizeDelta = new Vector2(56f, 56f);
                rt.anchoredPosition = new Vector2(28f, 0f);
            }
            else
            {
                img = iconT.GetComponent<Image>();
                iconT.gameObject.SetActive(true);
            }
            if (img != null) { img.sprite = sprite; img.preserveAspect = true; }
            return true;
        }

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

        private static void CentreRow(Transform t, float dy, float inset)
        {
            var rt = t.GetComponent<RectTransform>();
            if (rt == null) return;
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(1f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(inset, dy);
            rt.sizeDelta = new Vector2(-(inset + 30f), 25f);
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
