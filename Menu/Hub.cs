using System;
using System.Collections.Generic;
using System.Linq;
using DooDesch.UI;
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
        private static GameObject _formHost;   // the Host-config form overlay (shown instead of the native row list)

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
        private const float FormPanelHeight = 720f;   // fixed height for the scrollable Host-config form
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
            _formHost = null;
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

        /// <summary>Open the gamemode menu and jump straight to one gamemode's selection - identical to opening
        /// Side Hustle and picking it. Used by the direct main-menu entry shown while that gamemode's profile is
        /// running (so the player lands on the same Singleplayer / Host / Join choice).</summary>
        internal static void OpenGamemode(GamemodeDescriptor desc)
        {
            if (desc == null) { OpenScreen(); return; }
            EnsureInit();
            EnsureClone();
            if (_cloneScreen == null) { Core.Log?.Warning("[hub] gamemode screen unavailable."); return; }
            if (!_cloneScreen.IsOpen) { ShowGamemodeList(); _cloneScreen.Open(closePrevious: true); }
            OnSelectGamemode(desc);
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

            // While a gamemode profile is running, offer to switch back to the player's full mod set.
            if (Mods.ModSwitcher.HasRestorePending)
                rows.Add(new Row
                {
                    Name = "Restore my mods",
                    Subtitle = "Switch back to your full set of mods (restarts the game).",
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
            rows.Add(new Row { Name = "Host", Subtitle = "Configure and open a session.", OnClick = () => ShowHostConfig(desc) });
            // Join goes straight to the browser: the mod policy is the host's decision (made on the host form), and a
            // client runs the gamemode host-authoritatively, so it keeps its own mods - no client-side mod gate here.
            rows.Add(new Row { Name = "Join", Subtitle = "Browse and join an open session.", OnClick = () => ShowBrowser(desc) });
            rows.Add(new Row { Name = "Back", Subtitle = "Back to the gamemode list.", OnClick = ShowGamemodeList });
            ShowRows(desc.DisplayName, rows);
        }

        // The Host-config screen: a custom form (player count, visibility/password, optional mod policy, and the
        // gamemode's declared settings) rendered on the native panel background instead of the row list.
        private static void ShowHostConfig(GamemodeDescriptor desc)
        {
            if (_clone == null) return;
            _mpDesc = desc;
            _back = () => ShowModeChoice(desc);
            ClearFormHost();
            SetTmp(_clone.transform, "Title", "Host: " + (desc.DisplayName ?? desc.Id));

            var host = CreateFormHost("SH_HostForm", 560f);
            var plan = desc.Policy != null ? Mods.ModPolicyResolver.Resolve(desc) : null;
            HostConfigView.Build(host, desc, plan, LobbyCaps.MaxClients(),
                () => ShowModeChoice(desc),
                (opts, policyMode) => StartHostConfigured(desc, opts, policyMode, plan));
        }

        // Build a custom view-host on the cloned panel (used by the host form + the join browser): size the panel to
        // formH, hide the native row list, and add a full-width form-host of that height as a layout child. The
        // container's VerticalLayoutGroup does not control child heights, so the rect is set explicitly (the native
        // rows do the same); returns its transform for the view to fill.
        private static Transform CreateFormHost(string name, float formH)
        {
            var rootRt = _clone.GetComponent<RectTransform>();
            if (rootRt != null) rootRt.sizeDelta = new Vector2(PanelWidth, PanelChrome + formH + 24f);

            var container = _clone.transform.Find("Container");
            if (container != null)
                for (int i = 0; i < container.childCount; i++) container.GetChild(i).gameObject.SetActive(false);

            _formHost = new GameObject(name);
            _formHost.transform.SetParent(container != null ? container : _clone.transform, false);
            var fhrt = _formHost.AddComponent<RectTransform>();
            fhrt.anchorMin = new Vector2(0, 1); fhrt.anchorMax = new Vector2(1, 1); fhrt.pivot = new Vector2(0.5f, 1);
            fhrt.sizeDelta = new Vector2(0, formH);
            var fle = _formHost.AddComponent<LayoutElement>();
            fle.minHeight = formH; fle.preferredHeight = formH; fle.flexibleWidth = 1;
            return _formHost.transform;
        }

        private static void ShowBrowser(GamemodeDescriptor desc)
        {
            if (_clone == null) return;
            _mpDesc = desc;
            _back = () => ShowModeChoice(desc);
            ClearFormHost();
            SetTmp(_clone.transform, "Title", "Join: " + (desc.DisplayName ?? desc.Id));

            var host = CreateFormHost("SH_JoinBrowser", 560f);
            var content = JoinBrowserView.Build(host, () => ShowModeChoice(desc), () => ShowBrowser(desc));
            JoinBrowserView.SetStatus(content, "Searching for sessions...");
            ServerBrowser.BeginQuery(desc.Id, results =>
            {
                // Only apply if this gamemode's browser is still the active view.
                if (_cloneScreen != null && _cloneScreen.IsOpen && _mpDesc == desc && _formHost != null)
                    JoinBrowserView.Populate(content, results, row => StartJoin(desc, row));
            });
        }

        // --- render a row set into the cloned screen's slots ---

        // Destroy the Host-config form overlay (if any) and re-show the native row container before a row view renders.
        private static void ClearFormHost()
        {
            if (_formHost != null) { try { UnityEngine.Object.Destroy(_formHost); } catch { /* ignore */ } _formHost = null; }
            var container = _clone != null ? _clone.transform.Find("Container") : null;
            if (container != null) container.gameObject.SetActive(true);
        }

        private static void ShowRows(string title, List<Row> rows)
        {
            if (_clone == null) return;
            ClearFormHost();
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
            // Multiplayer gamemodes choose whether to apply the policy on the Host form (and gate it on Join), so the
            // policy is NOT forced here - go straight to the mode choice.
            if (desc.AllowsMultiplayer) { ShowModeChoice(desc); return; }

            // Singleplayer with a mod policy: confirm + restart first; after the restart the resolver reports no changes.
            if (desc.Policy != null)
            {
                var plan = Mods.ModPolicyResolver.Resolve(desc);
                if (plan.Blocked || plan.HasChanges) { ShowModCheck(desc, plan); return; }
            }
            LaunchSelected(desc);
        }

        /// <summary>Continue into a gamemode after a mod-policy restart (mods are already in the right state). When a
        /// host payload is present (the restart was triggered from the Host form), host directly with those options.</summary>
        internal static void ContinueGamemode(string id, string hostPayload = "")
        {
            EnsureInit();
            EnsureClone();
            if (_cloneScreen == null) return;
            if (!_cloneScreen.IsOpen) { ShowGamemodeList(); _cloneScreen.Open(closePrevious: true); }
            var desc = API.Registered.FirstOrDefault(d => d.Id == id);
            if (desc == null)
            {
                Core.Log?.Warning($"[hub] could not continue into '{id}' after restart (gamemode not registered). " +
                                  "The gamemode list (with 'Restore my mods') is shown so you are not stuck.");
                return;
            }
            if (!string.IsNullOrEmpty(hostPayload))
            {
                CloseHubScreen();
                MultiplayerCoordinator.StartHost(desc, DecodeHostIntent(hostPayload));
                return;
            }
            OnSelectGamemode(desc);
        }

        private static void ShowModCheck(GamemodeDescriptor desc, Mods.ModPlan plan)
        {
            _mpDesc = desc;
            _back = ShowGamemodeList;
            var rows = new List<Row>();

            if (plan.MissingRequired.Count > 0)
                rows.Add(new Row { Name = "Missing - install first", Subtitle = string.Join(", ", plan.MissingRequired) });
            if (plan.ToDisable.Count > 0)
                rows.Add(new Row { Name = $"Paused for this session ({plan.ToDisable.Count})", Subtitle = FriendlyNames(plan.ToDisable) });
            if (plan.ToEnable.Count > 0)
                rows.Add(new Row { Name = $"Enabled for this session ({plan.ToEnable.Count})", Subtitle = string.Join(", ", plan.ToEnable.Select(StripDll)) });

            if (plan.Blocked)
            {
                rows.Add(new Row { Name = "Cannot start yet", Subtitle = "Install the missing mod(s), then try again." });
            }
            else
            {
                rows.Add(new Row
                {
                    Name = "Confirm and launch",
                    Subtitle = "Restarts into a temporary profile with just these mods, then opens the gamemode. Your installed mods stay untouched.",
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

        private static void StartHostConfigured(GamemodeDescriptor desc, HostOptions opts, int policyMode, Mods.ModPlan plan)
        {
            // "Required mods only" + the plan actually changes the set -> build the curated profile and relaunch,
            // carrying the chosen host options across so the post-relaunch continue hosts directly. Else host in place.
            if (policyMode == 1 && plan != null && plan.HasChanges && !plan.Blocked)
            {
                Preferences.RecordLaunch(desc.Id);
                Mods.ModSwitcher.ApplyPolicyAndRestart(desc, plan, EncodeHostIntent(opts));
                return;
            }
            Preferences.RecordLaunch(desc.Id);
            CloseHubScreen();
            MultiplayerCoordinator.StartHost(desc, opts);
        }

        // Encode/decode the host's chosen lobby options so they survive a mod-policy relaunch (the relaunch is local,
        // so the raw password is fine here). Nesting is safe: ConfigCodec escapes the already-encoded ConfigBlob.
        private static string EncodeHostIntent(HostOptions o) => ConfigCodec.Encode(new[]
        {
            new KeyValuePair<string, string>("max", o.MaxPlayers.ToString()),
            new KeyValuePair<string, string>("vis", o.Visibility == LobbyVisibility.Private ? "1" : "0"),
            new KeyValuePair<string, string>("pw", o.Password ?? ""),
            new KeyValuePair<string, string>("cfg", o.ConfigBlob ?? "")
        });

        private static HostOptions DecodeHostIntent(string s)
        {
            var m = ConfigCodec.Decode(s);
            int.TryParse(m.TryGetValue("max", out var mx) ? mx : "4", out int max);
            return new HostOptions
            {
                MaxPlayers = Math.Max(2, max),
                Visibility = (m.TryGetValue("vis", out var v) && v == "1") ? LobbyVisibility.Private : LobbyVisibility.Public,
                Password = m.TryGetValue("pw", out var pw) && pw.Length > 0 ? pw : null,
                ConfigBlob = m.TryGetValue("cfg", out var c) && c.Length > 0 ? c : null
            };
        }

        private static void StartJoin(GamemodeDescriptor desc, LobbyRow row)
        {
            // A locked lobby: prompt for the password and verify it (client-side hash compare against the lobby's
            // advertised hash - a casual gate, not strong) before joining. Open lobbies join straight away.
            if (row != null && row.HasPassword && !string.IsNullOrEmpty(row.PwHash))
            {
                var canvas = _clone != null ? _clone.GetComponentInParent<Canvas>() : null;
                Transform root = canvas != null ? canvas.transform : (_clone != null ? _clone.transform : null);
                if (root != null)
                {
                    Components.PromptDialog(root, "Password required",
                        $"Enter the password for {(string.IsNullOrEmpty(row.HostName) ? "this" : row.HostName + "'s")} lobby.",
                        "password", "Join",
                        entered => string.Equals(LobbyCoordinator.HashPassword(entered ?? ""), row.PwHash, StringComparison.Ordinal)
                                   ? JoinAndAccept(desc, row)
                                   : "Incorrect password.");
                    return;
                }
            }
            JoinNow(desc, row);
        }

        // PromptDialog success path: start the join and return null so the dialog closes.
        private static string JoinAndAccept(GamemodeDescriptor desc, LobbyRow row) { JoinNow(desc, row); return null; }

        private static void JoinNow(GamemodeDescriptor desc, LobbyRow row)
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
