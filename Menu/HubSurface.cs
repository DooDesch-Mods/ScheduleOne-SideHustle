using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sideload.Api;
using SideHustle.Config;
using SideHustle.Internal;
using SideHustle.Multiplayer;
using SideHustle.Phone;
using SideHustle.Profiles;
using SideHustle.Sync;
using UnityEngine;

namespace SideHustle.Menu
{
    /// <summary>
    /// The hub drawn as one Sideload page in the right 60 percent of the screen instead of rows in the cloned New Game
    /// panel. The vanilla main menu stays live in the left 40 percent beside it.
    ///
    /// The cloned screen stays the engine underneath. It still opens and closes, owns Esc and the way back to the
    /// plain main menu, and every flow behind a click (host, join, mod check, consent, restart) is the same code as before.
    /// What changes is who draws: while the clone is on a view the page has a version of - the gamemode list, a
    /// gamemode's choice, the host form, a lobby browser - the clone is hidden behind the page. Any other view
    /// (consent, checklists, profiles, settings) shows the clone as it always looked, and its Back lands on the page
    /// again. Without a Sideload that can mount surfaces nothing here engages and the hub is exactly the old one.
    ///
    /// The page reads one JSON state (<c>hub.state</c>) and is told when it moved (<c>hub.changed</c>). A launch the
    /// page started is followed through its stations - mods, lobby, world - until the game's own loading screen
    /// takes over, so a click never ends in a screen that just closes.
    /// </summary>
    internal static partial class Hub
    {
        internal const string HubSurfaceId = "sidehustle-hub";

        /// <summary>The page is written for a 900px short side. It covers the right 60 percent, so a 16:9 screen
        /// gives it 960x900 and a 4:3 one 720x900.</summary>
        private const float HubDesignShortSide = 900f;

        /// <summary>The share of the screen width left to the vanilla main menu.</summary>
        private const float HubMenuShare = 0.4f;

        /// <summary>The sizes the settings page offers, in percent. Index 0 is the default.</summary>
        internal static readonly int[] HubSizes = { 100, 90, 80 };

        private static bool _hubSizeWarned;

        private static GameObject _hubRoot;
        private static RectTransform _hubRect;

        /// <summary>How far the page sits left of its place, as a share of the screen width. It follows the chat
        /// column's width while that column is open, so the page keeps its full width beside it instead of losing
        /// a third of it under the column.</summary>
        private static float _hubShift;
        private static Canvas _hubCanvas;
        private static UnityEngine.UI.GraphicRaycaster _hubRaycaster;
        private static SurfaceHandle _hub;
        private static bool _hubWarnedNoSurfaces;

        /// <summary>The clone is on a view the page draws. Cleared by every legacy view build (ClearFormHost) and set
        /// again by <see cref="RouteToHub"/>, so whichever ran last in a frame decides.</summary>
        private static bool _hubView;

        /// <summary>Set around a deliberate trip to the old screen ("More settings"), which would otherwise route
        /// straight back to the page.</summary>
        private static bool _hubForceLegacy;

        private static bool _hubShown;

        // --- fading with the vanilla main menu ---

        /// <summary>On the page's root canvas: its alpha is <see cref="_hubFade"/> times what the vanilla covers
        /// leave visible.</summary>
        private static CanvasGroup _hubGroup;

        /// <summary>The page's own open lerp, 0 to 1 at the speed of the vanilla menu screens.</summary>
        private static float _hubFade;

        /// <summary>The vanilla full-screen layer over the menu right now, for the log ("" when none).</summary>
        private static string _hubCover = "";

        private static Il2CppScheduleOne.UI.MainMenu.Disclaimer _disclaimer;
        private static bool _disclaimerLooked;
        private static Il2CppScheduleOne.UI.LoadingScreen _loading;
        private static bool _hubFadeWarned;

        /// <summary>A nested canvas on the clone, disabled while the page draws: the clone keeps running as a screen
        /// but paints nothing and takes no clicks.</summary>
        private static Canvas _cloneCanvas;
        private static UnityEngine.UI.GraphicRaycaster _cloneRaycaster;

        private static string _hubPushed = "";
        private static float _hubSinceState = 999f;

        // The last place the mod asked the page to show. The sequence number lets the page apply it exactly once.
        private static int _openSeq;
        private static string _openTab = "play";
        private static string _openMode;
        private static bool _openForm;

        // --- lobbies, queried on a ramp while the page is up ---

        private static List<LobbyRow> _hubGameLobbies = new List<LobbyRow>();
        private static List<VanillaLobbyRow> _hubVanillaLobbies = new List<VanillaLobbyRow>();
        private static string _hubSteam = "loading";
        private static float _hubSinceQuery = 999f;
        private static float _hubQueryAge;
        private static int _hubQueryGen;
        private static bool _hubQueryAdv, _hubQueryVan;
        private static int _hubCadenceStep;

        /// <summary>Fast first, then settling: a host who opens a lobby while the player looks at the board should
        /// show up within seconds, and a board left open should not cost a Steam query every few seconds forever.</summary>
        private static readonly float[] HubCadence = { 3f, 6f, 10f, 15f };

        private const float HubQueryTimeout = 15f;

        private static readonly HashSet<string> _hubIcons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _hubIconsFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The remembered vanilla save, read off the save list at most every couple of seconds.
        private static float _vanillaInfoAge = 999f;
        private static bool _vanillaCanHost;
        private static string _vanillaDetail = "";

        // --- the launch the page is following ---

        private sealed class HubFlow
        {
            public string Title;
            public string Coordinator;   // "mp" (gamemodes) or "sync" (vanilla co-op)
            public readonly List<string> Labels = new List<string>();
            public readonly Dictionary<int, string> Notes = new Dictionary<int, string>();
            public int Step;
            public bool Launched;
            public string Error;
            public int AbortsAtStart;
        }

        private static HubFlow _flow;

        /// <summary>The hub is drawn by the page right now.</summary>
        internal static bool HubShown => _hubShown;

        private static bool HubReady => _hub != null && Surfaces.IsMounted(HubSurfaceId);

        // ------------------------------------------------------------------------------------------------------
        // mount / reset
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Put the page up, hidden, or adopt the one already there. Returns whether it is usable.</summary>
        private static bool EnsureHubSurface()
        {
            if (_hubRoot != null && HubReady) return true;
            if (!Surfaces.Available)
            {
                if (!_hubWarnedNoSurfaces)
                {
                    _hubWarnedNoSurfaces = true;
                    Core.Log?.Msg("[hub3] the installed Sideload cannot mount surfaces - the hub uses the classic screen.");
                }
                return false;
            }

            try
            {
                HubReset();   // a canvas that died with its scene still holds the surface id

                // Its own overlay canvas: the menu canvas is drawn by the lit menu camera and its tone mapping would
                // flatten every colour on the page. Above the state column (100), below the dialog overlay (30000) and
                // the rejoin notice (32000), which both have to cover the hub.
                _hubRoot = new GameObject("SideHustle_HubSurface");
                _hubCanvas = _hubRoot.AddComponent<Canvas>();
                _hubCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _hubCanvas.sortingOrder = 1000;
                _hubRaycaster = _hubRoot.AddComponent<UnityEngine.UI.GraphicRaycaster>();
                _hubGroup = _hubRoot.AddComponent<CanvasGroup>();
                _hubGroup.alpha = 0f;
                _hubFade = 0f;

                var holder = new GameObject("page");
                var rect = holder.AddComponent<RectTransform>();
                rect.SetParent(_hubRoot.transform, false);
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                _hubRect = rect;
                _hubShift = 0f;
                PlaceHubRect();

                _hub = Surfaces.Mount(rect, HubSurfaceId, "SideHustle.Assets.hub", HubDesignShortSide)
                    .OnCall("hub.state", _ => HubStateJson())
                    .OnCall("hub.hostAgain", HubCallHostAgain)
                    .OnCall("hub.play", HubCallPlay)
                    .OnCall("hub.add", HubCallAdd)
                    .OnCall("hub.info", HubCallInfo)
                    .OnCall("hub.load", HubCallLoad)
                    .OnCall("hub.vanillaHost", _ => HubCallVanillaHost())
                    .OnCall("hub.hostForm", HubCallHostForm)
                    .OnCall("hub.host", HubCallHost)
                    .OnCall("hub.join", HubCallJoin)
                    .OnCall("hub.chat", HubCallChat)
                    .OnCall("hub.status", HubCallStatus)
                    .OnCall("hub.legacy", HubCallLegacy)
                    .OnCall("hub.refresh", _ => { HubQueryNow("the player asked"); return ""; })
                    .OnCall("hub.pref", HubCallPref)
                    .OnCall("hub.view", HubCallView)
                    .OnCall("hub.open", HubCallOpen);

                _hubShown = true;   // so the first SetHubShown(false) below really hides it
                SetHubShown(false);
                _hubIcons.Clear();
                _hubIconsFailed.Clear();
                Core.Log?.Msg("[hub3] hub surface mounted.");
                return true;
            }
            catch (Exception e)
            {
                Core.Log?.Error("[hub3] could not mount the hub surface - using the classic screen: " + e);
                HubReset();
                return false;
            }
        }

        /// <summary>The menu scene is being rebuilt or left: the canvas is ours and does not go on its own.</summary>
        internal static void HubReset()
        {
            Safe.Try("unmounting the hub surface", () => Surfaces.Unmount(HubSurfaceId));
            Safe.Try("destroying the hub canvas", () => { if (_hubRoot != null) UnityEngine.Object.Destroy(_hubRoot); });
            DestroyGrip();
            _hubRoot = null;
            _hubRect = null;
            _hubShift = 0f;
            _hubCanvas = null;
            _hubRaycaster = null;
            _hubGroup = null;
            _hubFade = 0f;
            _hubCover = "";
            _disclaimer = null;
            _disclaimerLooked = false;
            // A wish left from the last menu visit would make the next menu skip reopening its home screen.
            _homeWant = null;
            _hub = null;
            _hubShown = false;
            _hubView = false;
            _cloneCanvas = null;
            _cloneRaycaster = null;
            _hubPushed = "";
            _hubSinceQuery = 999f;
            _hubCadenceStep = 0;
            _hubQueryAdv = _hubQueryVan = false;
            _hubQueryGen++;
            _vanillaInfoAge = 999f;
            _flow = null;
            DropPageScreen();
        }

        private static void SetHubShown(bool on)
        {
            if (_hubShown == on) return;
            _hubShown = on;
            // The page's canvas and raycaster follow the fade in TickHubFade: it fades out with the vanilla menu
            // instead of vanishing, and takes no clicks from the moment it is meant to be gone.
            if (_hubRaycaster != null && !on) _hubRaycaster.enabled = false;
            EnsureCloneCanvas();
            if (_cloneCanvas != null) _cloneCanvas.enabled = !on;
            if (_cloneRaycaster != null) _cloneRaycaster.enabled = !on;
            Core.Log?.Msg(on ? "[hub3] page shown." : "[hub3] page hidden.");
            HubScene.OnPageShown(on);
            MenuInjector.ShowHubEntries(!on);
            if (on)
            {
                _hubPushed = "";
                _hubSinceState = 999f;
                if (_hubSinceQuery > HubCadence[0]) _hubCadenceStep = 0;
            }
        }

#if DEBUG
        // shscene page off: hides only the page's canvas, so a screenshot shows the staged scene alone.
        internal static void ShowPageCanvasForTest(bool on)
        {
            if (_hubCanvas == null) { Core.Log?.Warning("[scene] the hub page has no canvas yet."); return; }
            _pageOffForTest = !on;
            Core.Log?.Msg(on ? "[scene] page canvas on." : "[scene] page canvas off.");
        }

        private static bool _pageOffForTest;
        private static float _fadeTraceLeft;

        // shscene fade <seconds>: one log line per frame with the page's alpha beside the vanilla menu's.
        internal static void TraceFadeForTest(float seconds)
        {
            _fadeTraceLeft = Mathf.Max(0.1f, seconds);
            Core.Log?.Msg($"[fade] tracing for {_fadeTraceLeft:0.##}s.");
        }

        private static void TraceFade(float dt, float cover)
        {
            if (_fadeTraceLeft <= 0f) return;
            _fadeTraceLeft -= dt;
            float home = -1f;
            try { if (_home != null && _home.Group != null) home = _home.gameObject.activeInHierarchy ? _home.Group.alpha : 0f; }
            catch (Exception e) { Core.Log?.Warning("[fade] could not read the vanilla menu's alpha: " + e.Message); }
            Core.Log?.Msg(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[fade] f{0} page {1:0.000} (own {2:0.000}, cover {3:0.000} {4}) home {5:0.000} shown {6} canvas {7} rays {8}",
                Time.frameCount, _hubGroup != null ? _hubGroup.alpha : -1f, _hubFade, cover, string.IsNullOrEmpty(_hubCover) ? "-" : _hubCover,
                home, _hubShown, _hubCanvas != null && _hubCanvas.enabled, _hubRaycaster != null && _hubRaycaster.enabled));
            if (_fadeTraceLeft <= 0f) Core.Log?.Msg("[fade] trace done.");
        }

#endif
        private static void EnsureCloneCanvas()
        {
            if (_clone == null || _cloneCanvas != null) return;
            try
            {
                _cloneCanvas = _clone.GetComponent<Canvas>() ?? _clone.AddComponent<Canvas>();
                // A nested canvas takes its graphics away from the parent's raycaster, so it needs its own.
                _cloneRaycaster = _clone.GetComponent<UnityEngine.UI.GraphicRaycaster>() ?? _clone.AddComponent<UnityEngine.UI.GraphicRaycaster>();
                _cloneCanvas.enabled = !_hubShown;
                _cloneRaycaster.enabled = !_hubShown;
            }
            catch (Exception e)
            {
                Core.Log?.Error("[hub3] could not give the classic screen its own canvas; it stays visible under the page: " + e.Message);
                _cloneCanvas = null;
                _cloneRaycaster = null;
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // routing: which views the page draws
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Called at the top of every view the page has a version of. Takes the view when the page can draw it:
        /// drops whatever the clone was showing, records where the page should go, and returns true so the caller
        /// skips building the old rows. False leaves everything to the classic screen.
        /// </summary>
        private static bool RouteToHub(string tab, string mode, bool form)
        {
            if (_hubForceLegacy) return false;
            if (_clone == null || _cloneScreen == null) return false;
            if (!EnsureHubSurface()) return false;

            ClearFormHost();
            _hubView = true;
            _gamemodeListShowing = false;
            _shownRows = null;
            _secondaryTargets.Clear();
            SetTmp(_clone.transform, "Title", "Side Hustle");

            // A launch that had not started yet was left behind by this navigation. A running or failed one stays:
            // its status is what the player needs to see.
            if (_flow != null && !_flow.Launched && _flow.Error == null)
            {
                Core.Log?.Msg($"[hub3] '{_flow.Title}' left before it launched.");
                _flow = null;
            }

            _openSeq++;
            _openTab = tab;
            _openMode = mode;
            _openForm = form;
            Core.Log?.Msg($"[hub3] page view: {tab}{(string.IsNullOrEmpty(mode) ? "" : " / " + mode)}{(form ? " (host form)" : "")}.");
            return true;
        }

        /// <summary>A wait screen of a flow the page started: keep the page up and show the step instead.</summary>
        private static void HubFlowWait(string station, string note)
        {
            var f = _flow;
            if (f == null || f.Error != null || !HubReady) return;
            int i = f.Labels.IndexOf(station);
            if (i >= 0) { f.Step = i; f.Notes[i] = note ?? ""; }
            _hubView = true;
        }

        /// <summary>A start call refused: the page shows why on the flow it was following. False when there is no
        /// such flow and the caller has to tell the player itself.</summary>
        private static bool HubFlowRefused(string why)
        {
            if (_flow == null || !HubReady) return false;
            HubFlowFail(string.IsNullOrEmpty(why) ? "The session could not start." : why);
            return true;
        }

        /// <summary>The required-mods restart counts down as a station when the page started the host.</summary>
        private static bool HubCountdownAsStation()
        {
            var f = _flow;
            if (f == null || f.Error != null || !_hubShown) return false;
            if (!f.Labels.Contains("Restart"))
            {
                f.Labels.Insert(1, "Restart");
                if (f.Step >= 1) f.Step++;
            }
            f.Step = f.Labels.IndexOf("Restart");
            f.Notes[f.Step] = "";
            return true;
        }

        /// <summary>The page reports a failure of the launch it follows itself, so the separate notice dialog waits
        /// until the page has taken the reason (and cleared it) or let go of the launch.</summary>
        internal static bool HubHoldsSessionNotice => _flow != null && _flow.Error == null && HubReady;

        private static void HubFlowFail(string why)
        {
            var f = _flow;
            if (f == null) return;
            f.Error = why;
            Core.Log?.Warning($"[hub3] '{f.Title}' failed at '{(f.Step < f.Labels.Count ? f.Labels[f.Step] : "?")}': {why}");
            // The page carries the reason now; the separate notice dialog would say it a second time.
            Safe.Try("dismissing the session notice the hub already shows", SessionNotice.Clear);
        }

        private static HubFlow NewFlow(string title, string coordinator)
        {
            _flow = new HubFlow
            {
                Title = title,
                Coordinator = coordinator,
                AbortsAtStart = coordinator == "sync" ? SyncCoordinator.Aborts : MultiplayerCoordinator.Aborts,
            };
            Core.Log?.Msg($"[hub3] following '{title}'.");
            return _flow;
        }

        private static void FlowHost(GamemodeDescriptor desc, string modsNote)
        {
            var f = NewFlow("HOSTING " + Upper(desc.DisplayName ?? desc.Id), "mp");
            f.Labels.Add("Mods checked");
            f.Notes[0] = modsNote;
            f.Labels.Add("Creating lobby");
            if (desc.Surface == GamemodeSurface.World) f.Labels.Add("Loading world");
            f.Step = 1;
        }

        private static void FlowVanillaHost(string org)
        {
            var f = NewFlow("HOSTING " + Upper(org), "sync");
            f.Labels.Add("Mod list");
            f.Labels.Add("Creating lobby");
            f.Labels.Add("Loading save");
            f.Notes[0] = "Preparing";
            f.Step = 0;
        }

        private static void FlowJoin(string what, string host, string coordinator, bool world)
        {
            var f = NewFlow("JOINING " + Upper(what), coordinator);
            f.Labels.Add("Lobby found");
            f.Notes[0] = host ?? "";
            f.Labels.Add("Mods compared");
            f.Labels.Add("Entering lobby");
            if (world) f.Labels.Add("Loading world");
            f.Step = 1;
        }

        private static string Upper(string s) => (s ?? "").ToUpperInvariant();

        // ------------------------------------------------------------------------------------------------------
        // per frame
        // ------------------------------------------------------------------------------------------------------

        /// <summary>Pumped from Core.OnUpdate while the menu is up.</summary>
        internal static void HubTick(float dt)
        {
            TickFlow();
            if (_hub == null) return;

            bool want = HubWanted();
            if (want != _hubShown) SetHubShown(want);
            TickFold();
            SyncHomeBesidePage();
            TickHubFade(dt);
            TickHubShift(dt);
            if (!_hubShown) return;

            TickHubQueries(dt);

            _hubSinceState += dt;
            if (_hubSinceState < 0.2f) return;
            _hubSinceState = 0f;
            string now = HubStateJson();
            if (now == _hubPushed) return;
            _hubPushed = now;
            Safe.Try("telling the hub page its state moved", () => _hub.Emit("hub.changed", ""));
        }

        /// <summary>
        /// The page fades like a vanilla menu screen: in and out over <c>MenuScreen.LerpTime</c> when it shows or
        /// hides, so it goes with the vanilla menu beside it when a sub-screen (Continue, Settings) opens or closes.
        /// On top of that, whatever vanilla lays over the whole menu hides the page too: the disclaimer at the first
        /// menu load and the loading screen on the way into a save and back. The page's canvas sorts above both, so
        /// without this it showed before the menu and stayed after it.
        /// </summary>
        private static void TickHubFade(float dt)
        {
            if (_hubCanvas == null || _hubGroup == null) return;
            float lerp = MenuLerpTime();
            float step = lerp > 0f ? dt / lerp : 1f;
            _hubFade = _hubShown ? Mathf.Min(1f, _hubFade + step) : Mathf.Max(0f, _hubFade - step);

            float cover = MenuCover();
            float alpha = _hubFade * (1f - cover);
#if DEBUG
            if (_pageOffForTest) alpha = 0f;
#endif
            _hubGroup.alpha = alpha;
            bool draw = alpha > 0.001f;
            if (_hubCanvas.enabled != draw) _hubCanvas.enabled = draw;
            // Clicks only while the page is meant to be up and no cover sits over most of it, the way the vanilla
            // menu takes no clicks until the disclaimer lets go.
            bool rays = _hubShown && cover < 0.5f;
            if (_hubRaycaster != null && _hubRaycaster.enabled != rays) _hubRaycaster.enabled = rays;
#if DEBUG
            TraceFade(dt, cover);
#endif
        }

        /// <summary>
        /// Slides the page left by the chat column's width while the column is open, and back when it closes, at
        /// the speed of the vanilla menu screens. Only the anchors move: the page keeps its size, so the engine
        /// does not lay it out again. Its left edge then lands right of the vanilla menu buttons.
        /// Sideload bakes every clip rectangle in canvas space when it paints, so a page that moved without a
        /// rebuild still clips to where it stood before. Once the slide arrives the page is told to rebuild.
        /// </summary>
        private static void TickHubShift(float dt)
        {
            if (_hubRect == null) return;
            float target = ChatPanel.ScreenShare;
            if (Mathf.Approximately(_hubShift, target)) return;
            float lerp = MenuLerpTime();
            float step = lerp > 0f ? dt / lerp * Mathf.Max(target, _hubShift) : 1f;
            _hubShift = Mathf.MoveTowards(_hubShift, target, step);
            PlaceHubRect();
            if (!Mathf.Approximately(_hubShift, target)) return;
            Core.Log?.Msg($"[hub3] page moved {(target > 0f ? "left of the chat column" : "back")}; repainting its clip areas.");
            Safe.Try("telling the hub page it moved", () => _hub?.Emit("hub.moved", ""));
        }

        /// <summary>
        /// Puts the page where the chat shift and the size setting say. The size is the rect's scale around its
        /// centre rather than a smaller rect: the engine skips a rect that shrinks with the same proportions, since
        /// its CSS size stays the same, and would keep drawing at the old scale. The layout stays the same either way.
        /// </summary>
        private static void PlaceHubRect()
        {
            if (_hubRect == null) return;
            float size = HubSizes[HubSizeIndex] / 100f;
            _hubRect.anchorMin = new Vector2(HubMenuShare - _hubShift, 0f);
            _hubRect.anchorMax = new Vector2(1f - _hubShift, 1f);
            _hubRect.localScale = new Vector3(size, size, 1f);
        }

        /// <summary>Which of <see cref="HubSizes"/> the preference names. A value edited to something else by hand
        /// counts as the default.</summary>
        internal static int HubSizeIndex
        {
            get
            {
                int percent = Preferences.HubSizePercent;
                int index = Array.IndexOf(HubSizes, percent);
                if (index >= 0) return index;
                if (!_hubSizeWarned)
                {
                    _hubSizeWarned = true;
                    Core.Log?.Warning($"[hub3] HubSizePercent {percent} is not one of 100, 90, 80; using 100.");
                }
                return 0;
            }
        }

        internal static void SetHubSize(int index)
        {
            if (index < 0 || index >= HubSizes.Length)
            {
                Core.Log?.Warning($"[hub3] no hub size at index {index}.");
                return;
            }
            Preferences.HubSizePercent = HubSizes[index];
            PlaceHubRect();
            // Clip rectangles are baked when the page paints, so a page that changed scale has to paint again.
            Safe.Try("telling the hub page it changed size", () => _hub?.Emit("hub.moved", ""));
            Core.Log?.Msg($"[hub3] hub size is now {HubSizes[index]} percent.");
        }

        private static float _menuLerpTime = -1f;

        private static float MenuLerpTime()
        {
            if (_menuLerpTime > 0f) return _menuLerpTime;
            try { _menuLerpTime = Il2CppScheduleOne.UI.MainMenu.MenuScreen.LerpTime; }
            catch (Exception e) { Core.Log?.Warning("[hub3] could not read the vanilla menu fade time, using 0.075s: " + e.Message); }
            if (_menuLerpTime <= 0f) _menuLerpTime = 0.075f;
            return _menuLerpTime;
        }

        /// <summary>Whether a vanilla layer draws below the page, so the page has to fade for it. One drawn above
        /// already hides the page the way it hides the menu.</summary>
        private static bool UnderPage(Canvas c)
        {
            if (c == null || _hubCanvas == null) return true;
            var root = c.rootCanvas != null ? c.rootCanvas : c;
            if (root.renderMode != RenderMode.ScreenSpaceOverlay) return true;   // every overlay canvas draws over it
            return root.sortingOrder < _hubCanvas.sortingOrder;
        }

        /// <summary>How much of the menu a vanilla full-screen layer below the page hides right now, 0 to 1.</summary>
        private static float MenuCover()
        {
            float cover = 0f;
            string by = "";
            try
            {
                if (!_disclaimerLooked)
                {
                    _disclaimerLooked = true;
                    _disclaimer = UnityEngine.Object.FindObjectOfType<Il2CppScheduleOne.UI.MainMenu.Disclaimer>(true);
                    Core.Log?.Msg(_disclaimer != null ? "[hub3] the menu disclaimer is in the scene; the page waits for it."
                                                      : "[hub3] no menu disclaimer in this scene.");
                }
                if (_disclaimer != null && _disclaimer.gameObject.activeInHierarchy && _disclaimer.Group != null
                    && _disclaimer.Group.alpha > cover && UnderPage(_disclaimer.GetComponentInParent<Canvas>()))
                { cover = _disclaimer.Group.alpha; by = "disclaimer"; }

                // Persistent across scenes, so one lookup holds until the game drops it.
                if (_loading == null) _loading = UnityEngine.Object.FindObjectOfType<Il2CppScheduleOne.UI.LoadingScreen>(true);
                if (_loading != null && _loading.Canvas != null && _loading.Canvas.enabled && _loading.Group != null
                    && _loading.Group.alpha > cover && UnderPage(_loading.Canvas))
                { cover = _loading.Group.alpha; by = "loading screen"; }
            }
            catch (Exception e)
            {
                if (!_hubFadeWarned)
                {
                    _hubFadeWarned = true;
                    Core.Log?.Warning("[hub3] could not read the vanilla disclaimer or loading screen; the page ignores them: " + e.Message);
                }
                return 0f;
            }

            string now = cover >= 0.5f ? by : "";
            if (now != _hubCover)
            {
                Core.Log?.Msg(now.Length > 0 ? $"[hub3] page under the vanilla {now}." : $"[hub3] page clear of the vanilla {_hubCover}.");
                _hubCover = now;
            }
            return Mathf.Clamp01(cover);
        }

        // What the page last asked of the vanilla home screen; null while vanilla decides alone.
        private static bool? _homeWant;

        /// <summary>
        /// Keeps the vanilla home screen live beside the page. The clone is the menu screen on top while the page is
        /// shown, so vanilla has faded the home screen out; it comes back with its own open fade. It goes again when
        /// the page goes because another screen opened (Continue, Load, Settings), and while a launch holds the page
        /// with the clone closed, so nothing else starts in the middle of it.
        /// </summary>
        private static void SyncHomeBesidePage()
        {
            var home = _home;
            if (home == null) { _homeWant = null; return; }
            bool vanilla = home.IsOpen;
            bool want = _hubShown ? ScreenOpen : vanilla;
            if (_homeWant == null) { if (want == vanilla) return; }
            else if (_homeWant == want) { if (want == vanilla) _homeWant = null; return; }
            try
            {
                home.Lerp(want);
                _homeWant = want == vanilla ? (bool?)null : want;
                Core.Log?.Msg(want ? "[hub3] vanilla menu shown beside the page." : "[hub3] vanilla menu hidden.");
            }
            catch (Exception e)
            {
                _homeWant = null;
                Core.Log?.Error("[hub3] could not " + (want ? "show" : "hide") + " the vanilla menu beside the page: " + e.Message);
            }
        }

        private static bool HubWanted()
        {
            if (!HubReady) return false;
            // A launch the page started keeps it up after the screen closed, until the game's own loading screen
            // or a restart notice has something better to show.
            if (_flow != null && _flow.Launched && _flow.Error == null)
                return !WorldBoot.LoadStarted && !CommittedRestart.Pending;
            return ScreenOpen && _hubView;
        }

        private static void TickFlow()
        {
            var f = _flow;
            if (f == null || f.Error != null) return;

            bool sync = f.Coordinator == "sync";
            bool busy = sync ? SyncCoordinator.IsBusy : MultiplayerCoordinator.IsBusy;
            if (!f.Launched)
            {
                if (!busy) return;
                f.Launched = true;
                Core.Log?.Msg($"[hub3] '{f.Title}' launched.");
            }

            string phase = sync ? SyncCoordinator.Phase : MultiplayerCoordinator.Phase;
            if (phase == "InSession")
            {
                Core.Log?.Msg($"[hub3] '{f.Title}' is in session.");
                _flow = null;
                return;
            }
            if (phase == "Idle")
            {
                int aborts = sync ? SyncCoordinator.Aborts : MultiplayerCoordinator.Aborts;
                if (aborts != f.AbortsAtStart)
                {
                    string why = sync ? SyncCoordinator.LastFailure : MultiplayerCoordinator.LastFailure;
                    HubFlowFail(string.IsNullOrEmpty(why) ? "The session stopped." : Sentence(why));
                }
                else
                {
                    Core.Log?.Msg($"[hub3] '{f.Title}' ended without a failure (cancelled).");
                    _flow = null;
                }
                return;
            }

            switch (phase)
            {
                case "HostCreatingLobby": StepTo(f, "Creating lobby"); break;
                case "HostBootingWorld": StepTo(f, sync ? "Loading save" : "Loading world"); break;
                case "Joining": StepTo(f, MultiplayerCoordinator.LobbyEntered ? "Loading world" : "Entering lobby"); break;
                case "ClientCheckingLobby": StepTo(f, "Entering lobby"); break;
                case "ClientJoining": StepTo(f, LobbyCoordinator.IsInLobby ? "Loading world" : "Entering lobby"); break;
            }
        }

        private static void StepTo(HubFlow f, string label)
        {
            int i = f.Labels.IndexOf(label);
            if (i >= 0 && i != f.Step) f.Step = i;
        }

        /// <summary>Coordinator reasons are log phrases ("lobby did not open within 10s"); the page shows sentences.</summary>
        private static string Sentence(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return s;
            s = char.ToUpperInvariant(s[0]) + s.Substring(1);
            return s.EndsWith(".") ? s : s + ".";
        }

        private static void TickHubQueries(float dt)
        {
            if (_hubQueryAdv || _hubQueryVan)
            {
                _hubQueryAge += dt;
                if (_hubQueryAge < HubQueryTimeout) return;
                Core.Log?.Warning($"[hub3] a lobby query gave no answer in {HubQueryTimeout:F0}s (advertised {(_hubQueryAdv ? "open" : "done")}, vanilla {(_hubQueryVan ? "open" : "done")}); asking again.");
                _hubQueryAdv = _hubQueryVan = false;
                _hubQueryGen++;
                if (_hubGameLobbies.Count == 0 && _hubVanillaLobbies.Count == 0) _hubSteam = "error";
            }
            _hubSinceQuery += dt;
            if (_hubSinceQuery < HubCadence[Math.Min(_hubCadenceStep, HubCadence.Length - 1)]) return;
            if (_hubCadenceStep < HubCadence.Length - 1) _hubCadenceStep++;
            HubQuery();
        }

        private static void HubQueryNow(string why)
        {
            Core.Log?.Msg($"[hub3] lobby refresh: {why}.");
            _hubCadenceStep = 0;
            _hubQueryAdv = _hubQueryVan = false;
            _hubSteam = "loading";
            HubQuery();
        }

        /// <summary>One Steam lobby-list request at a time: a second RequestLobbyList cancels the one still open, so
        /// vanilla goes first (it leads the page) and the gamemode lobbies follow from its answer.</summary>
        private static void HubQuery()
        {
            _hubSinceQuery = 0f;
            _hubQueryAge = 0f;
            int gen = ++_hubQueryGen;
            _hubQueryAdv = true;
            _hubQueryVan = true;

            try
            {
                ServerBrowser.BeginQueryVanilla(rows => MainThread.Post(() =>
                {
                    if (gen != _hubQueryGen) return;
                    _hubQueryVan = false;
                    if (rows == null) Core.Log?.Warning("[hub3] the vanilla lobby query answered nothing.");
                    else { _hubVanillaLobbies = rows; if (_hubSteam != "error") _hubSteam = "ok"; }
                    HubQueryAdvertised(gen);
                }));
            }
            catch (Exception e)
            {
                _hubQueryVan = false;
                Core.Log?.Warning("[hub3] the vanilla lobby query could not start: " + e.Message);
                HubQueryAdvertised(gen);
            }
        }

        private static void HubQueryAdvertised(int gen)
        {
            _hubQueryAge = 0f;
            try
            {
                ServerBrowser.BeginQueryAdvertised(rows => MainThread.Post(() =>
                {
                    if (gen != _hubQueryGen) return;
                    _hubQueryAdv = false;
#if DEBUG
                    rows = DebugSeedAdvertised(rows);
#endif
                    if (rows == null) { _hubSteam = "error"; Core.Log?.Warning("[hub3] the advertised lobby query answered nothing."); return; }
                    _hubGameLobbies = rows;
                    // The classic screens read the same cache, so a trip to them shows what the page showed.
                    _advertisedCache = rows;
                    _advertisedQueried = true;
                    _advertisedAt = Time.unscaledTime;
                    _hubSteam = "ok";
                }));
            }
            catch (Exception e)
            {
                _hubQueryAdv = false;
                _hubSteam = "error";
                Core.Log?.Warning("[hub3] the advertised lobby query could not start: " + e.Message);
            }
        }

        /// <summary>Right-click while the page is up: the page steps back itself (dialog, form, lobby, tab).</summary>
        private static bool HubTakeRightClick()
        {
            if (!_hubShown || !Input.GetMouseButtonDown(1)) return false;
            Safe.Try("telling the hub page to step back", () => _hub?.Emit("hub.back", ""));
            return true;
        }

        // ------------------------------------------------------------------------------------------------------
        // the state the page reads
        // ------------------------------------------------------------------------------------------------------

        private static bool HubBusy() =>
            (_flow != null && _flow.Error == null)
            || MultiplayerCoordinator.IsBusy || SyncCoordinator.IsBusy
            || RestartCountdownActive || CommittedRestart.Pending;

        private static string HubStateJson()
        {
            try
            {
                var installed = new HashSet<string>(API.Registered.Select(d => d.Id), StringComparer.OrdinalIgnoreCase);
                var lobbies = Json.Array();
                int live = 0;
                var liveByMode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var r in _hubVanillaLobbies)
                {
                    lobbies.Item(VanillaLobbyJson(r));
                    live++;
                }
                foreach (var r in _hubGameLobbies)
                {
                    if (string.IsNullOrEmpty(r.GamemodeId)) continue;
                    lobbies.Item(GameLobbyJson(r, installed));
                    live++;
                    liveByMode[r.GamemodeId] = (liveByMode.TryGetValue(r.GamemodeId, out int n) ? n : 0) + 1;
                }

                var j = Json.Object()
                    .Add("version", HubVersion())
                    .Add("busy", HubBusy())
                    .Add("steam", _hubSteam)
                    .Add("live", live)
                    .Add("vanilla", VanillaJson());
                var cont = ContinueJson();
                if (cont != null) j.Add("cont", cont); else j.AddNull("cont");
                j.Add("modes", ModesJson(installed, liveByMode))
                 .Add("lobbies", lobbies)
                 .Add("chat", ChatPanel.IsOpen);
                var st = StatusJson();
                if (st != null) j.Add("status", st); else j.AddNull("status");
                j.Add("prefs", Json.Object()
                        .Add("hideFull", Preferences.BrowserHideFull)
                        .Add("hideLocked", Preferences.BrowserHideLocked))
                 .Add("open", Json.Object()
                        .Add("seq", _openSeq)
                        .Add("tab", _openTab ?? "play")
                        .Add("mode", _openMode ?? "")
                        .Add("form", _openForm));
                string view = _hubView ? PageJson() : null;
                string all = j.ToString();
                return view == null ? all : all.Substring(0, all.Length - 1) + ",\"view\":" + view + "}";
            }
            catch (Exception e)
            {
                Core.Log?.Error("[hub3] building the page state failed: " + e);
                return "";
            }
        }

        private static string HubVersion()
        {
            try { return typeof(Core).Assembly.GetName().Version?.ToString(3) ?? ""; }
            catch (Exception e) { Safe.Report("reading the mod version for the hub", e); return ""; }
        }

        private static Json VanillaJson()
        {
            _vanillaInfoAge += Time.unscaledDeltaTime;
            if (_vanillaInfoAge > 2f)
            {
                _vanillaInfoAge = 0f;
                var again = Preferences.GetHost(Preferences.VanillaHostKey);
                var save = RememberedVanillaSave(again);
                _vanillaCanHost = save != null;
                if (save != null)
                {
                    string org;
                    try { org = save.OrganisationName; }
                    catch (Exception e) { org = "Your save"; Core.Log?.Warning("[hub3] could not read the organisation name of the remembered save: " + e.Message); }
                    _vanillaDetail = $"{org} - {HostAgainSummary(again)}";
                }
                else _vanillaDetail = "Host one of your saves as an open lobby.";
            }
            return Json.Object()
                .Add("icon", "")
                .Add("detail", _vanillaDetail ?? "")
                .Add("hostLabel", _vanillaCanHost ? "Host again" : "")
                .Add("canHost", _vanillaCanHost);
        }

        // The last gamemode played, one click from running again.
        private static Json ContinueJson()
        {
            foreach (var id in Preferences.RecentlyPlayed)
            {
                var desc = API.Registered.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
                if (desc == null) continue;
                var again = desc.AllowsMultiplayer ? Preferences.GetHost(desc.Id) : null;
                bool lastSolo = desc.AllowsSingleplayer && (!desc.AllowsMultiplayer || Preferences.GetLastMode(desc.Id) == Preferences.ModeSolo);
                if (again == null && !lastSolo) continue;
                return Json.Object()
                    .Add("id", desc.Id)
                    .Add("name", desc.DisplayName ?? desc.Id)
                    .Add("icon", HubIcon(desc))
                    .Add("summary", lastSolo ? "Solo" : HostAgainSummary(again))
                    .Add("label", lastSolo ? "Play" : "Host again")
                    .Add("action", lastSolo ? "play" : "hostAgain")
                    .Add("settings", !lastSolo);
            }
            return null;
        }

        private static Json ModesJson(HashSet<string> installed, Dictionary<string, int> liveByMode)
        {
            var recent = Preferences.RecentlyPlayed;
            var arr = Json.Array();
            foreach (var desc in API.Registered.OrderBy(m => { int i = recent.IndexOf(m.Id); return i < 0 ? int.MaxValue : i; }))
            {
                var acts = Json.Array();
                if (desc.AllowsMultiplayer)
                {
                    acts.Item(Json.Object().Add("id", "host").Add("label", "Host").Add("go", true));
                    acts.Item(Json.Object().Add("id", "join").Add("label", "Join").Add("go", false));
                }
                if (desc.AllowsSingleplayer)
                    acts.Item(Json.Object().Add("id", "play").Add("label", desc.AllowsMultiplayer ? "Solo" : "Play").Add("go", !desc.AllowsMultiplayer));
                string author = string.IsNullOrWhiteSpace(desc.Author) ? "" : "by " + desc.Author.Trim();
                arr.Item(Json.Object()
                    .Add("id", desc.Id)
                    .Add("name", desc.DisplayName ?? desc.Id)
                    .Add("icon", HubIcon(desc))
                    .Add("sub", SupportBadge(desc.Support) + (author.Length > 0 ? "  " + author : ""))
                    .Add("desc", desc.Description ?? "")
                    .Add("kind", "mode")
                    .Add("join", desc.AllowsMultiplayer)
                    .Add("live", liveByMode.TryGetValue(desc.Id, out int n) ? n : 0)
                    .Add("actions", acts));
            }

            // Gamemodes the boot gate held back: one click loads the mod, then its own choice follows.
            foreach (var c in Mods.GamemodeProbe.Waiting())
            {
                arr.Item(Json.Object()
                    .Add("id", "waiting:" + c.File)
                    .Add("name", c.Name ?? c.File)
                    .Add("icon", HubIconFile(c.IconFile))
                    .Add("sub", string.IsNullOrWhiteSpace(c.Version) ? "Ready" : c.Version)
                    .Add("desc", "Click to start.")
                    .Add("kind", "waiting")
                    .Add("live", 0)
                    .Add("actions", Json.Array().Item(Json.Object().Add("id", "load").Add("label", "Start").Add("go", true))));
            }

            if (!Preferences.ShowUninstalledGamemodes) return arr;
            PlayCounts.RefreshIfStale();

            // Gamemodes somebody is hosting that this player does not have, busiest first.
            var ghosts = _hubGameLobbies.Where(l => !string.IsNullOrEmpty(l.GamemodeId) && !installed.Contains(l.GamemodeId))
                                        .GroupBy(l => l.GamemodeId, StringComparer.OrdinalIgnoreCase)
                                        .OrderByDescending(g => g.Count()).ThenByDescending(g => PlayCounts.Week(g.Key))
                                        .ToList();
            foreach (var g in ghosts)
            {
                var first = g.First();
                int plays = PlayCounts.Week(g.Key);
                arr.Item(Json.Object()
                    .Add("id", g.Key)
                    .Add("name", string.IsNullOrEmpty(first.GamemodeName) ? g.Key : first.GamemodeName)
                    .Add("icon", CatalogueIcon(GamemodeCatalogue.Find(g.Key)))
                    .Add("sub", NotInstalledSub(plays))
                    .Add("desc", GamemodeCatalogue.Find(g.Key)?.Subtitle ?? "")
                    .Add("kind", "ghost")
                    .Add("live", g.Count())
                    .Add("actions", Json.Array().Item(Json.Object().Add("id", "join").Add("label", "Join, installs the mod").Add("go", false))));
            }

            // Released gamemodes nobody is hosting right now, most played this week first.
            var waitingNames = new HashSet<string>(Mods.GamemodeProbe.Waiting().Select(w => w.Name ?? ""), StringComparer.OrdinalIgnoreCase);
            var live = new HashSet<string>(ghosts.Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
            var catalogue = GamemodeCatalogue.All
                .Where(e => !installed.Contains(e.Id) && !live.Contains(e.Id) && !waitingNames.Contains(e.Name))
                .Select((e, i) => (Entry: e, Plays: PlayCounts.Week(e.Id), Order: i))
                .OrderByDescending(x => x.Plays).ThenBy(x => x.Order);
            foreach (var c in catalogue)
            {
                arr.Item(Json.Object()
                    .Add("id", c.Entry.Id)
                    .Add("name", c.Entry.Name)
                    .Add("icon", CatalogueIcon(c.Entry))
                    .Add("sub", NotInstalledSub(c.Plays))
                    .Add("desc", c.Entry.Subtitle ?? "")
                    .Add("kind", "ghost")
                    .Add("live", 0)
                    .Add("actions", Json.Array()
                        .Item(Json.Object().Add("id", "info").Add("label", "Info").Add("go", false))
                        .Item(Json.Object().Add("id", "add").Add("label", "Add to a profile").Add("go", true))));
            }
            return arr;
        }

        private static string NotInstalledSub(int plays) =>
            plays <= 0 ? "Not installed" : "Not installed  -  " + (plays == 1 ? "1 play this week" : plays + " plays this week");

        /// <summary>The package icon from the cached Thunderstore index, or "" (the page draws initials then). No
        /// request of its own: the index is cached once the Profiles tab or an install has fetched it.</summary>
        private static string CatalogueIcon(GamemodeCatalogue.Entry entry)
        {
            if (entry == null) return "";
            try
            {
                var index = ThunderstoreClient.GetCachedIndexOrNull(ProfileEngine.GameRoot);
                return HubPackageIcon(index?.Find(entry.Package)?.Latest?.Icon);
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"[hub3] icon of {entry.Package} not read from the cached index: {e.Message}");
                return "";
            }
        }

        private static string HubCallAdd(string id)
        {
            var entry = GamemodeCatalogue.Find(id);
            if (entry == null) { Core.Log?.Warning($"[hub3] add: '{id}' is not in the catalogue."); return RefuseGone; }
            if (HubBusy()) { Core.Log?.Msg("[hub3] add refused: busy."); return RefuseBusy; }
            Core.Log?.Msg($"[hub3] add '{entry.Id}' to a profile.");
            StartCatalogueInstall(entry, ShowGamemodeList);
            return "";
        }

        private static string HubCallInfo(string id)
        {
            var entry = GamemodeCatalogue.Find(id);
            if (entry == null) { Core.Log?.Warning($"[hub3] info: '{id}' is not in the catalogue."); return RefuseGone; }
            Core.Log?.Msg($"[hub3] info: opening the page of {entry.Package}.");
            DownloadLink.Open(entry.Url);
            return "";
        }

        private static Json GameLobbyJson(LobbyRow r, HashSet<string> installed)
        {
            var desc = API.Registered.FirstOrDefault(d => string.Equals(d.Id, r.GamemodeId, StringComparison.OrdinalIgnoreCase));
            string state, text, before;
            bool full = r.MaxPlayers > 0 && r.Members >= r.MaxPlayers;
            bool otherBranch = !string.IsNullOrEmpty(r.Runtime) && !string.Equals(r.Runtime, LobbyCoordinator.ThisRuntime, StringComparison.OrdinalIgnoreCase);
            string localBuild = desc != null ? LobbyCoordinator.BuildIdOf(desc) : null;
            bool otherBuild = desc != null && !string.IsNullOrEmpty(r.BuildId) && !string.IsNullOrEmpty(localBuild)
                              && !string.Equals(r.BuildId, localBuild, StringComparison.Ordinal);
            if (full) { state = "full"; text = "Full"; before = "Every seat is taken."; }
            else if (desc == null) { state = "install"; text = "Not installed"; before = "Installs the gamemode in a separate profile, then restarts."; }
            else if (otherBranch) { state = "version"; text = "Other game branch"; before = "The host plays on the " + r.Runtime + " branch. Joining may fail."; }
            else if (otherBuild) { state = "version"; text = "Other version"; before = "The host runs another " + (desc.DisplayName ?? desc.Id) + " build. Joining may fail."; }
            else { state = "ready"; text = "Ready"; before = "Checks your mods against the host's, then joins."; }

            return Json.Object()
                .Add("id", r.LobbyId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Add("mode", r.GamemodeId)
                .Add("modeName", desc?.DisplayName ?? (string.IsNullOrEmpty(r.GamemodeName) ? r.GamemodeId : r.GamemodeName))
                .Add("icon", desc != null ? HubIcon(desc) : "")
                .Add("host", r.HostName ?? "")
                .Add("name", string.IsNullOrEmpty(r.LobbyName) ? (r.HostName ?? "") : r.LobbyName)
                .Add("members", r.Members)
                .Add("max", r.MaxPlayers)
                .Add("locked", r.HasPassword)
                .Add("askPassword", r.HasPassword && !string.IsNullOrEmpty(r.PwHash))
                .Add("state", state)
                .Add("stateText", text)
                .Add("before", before)
                .Add("warn", "")
                .Add("canJoin", !full)
                .Add("chat", ChatPanel.Possible(r.OwnerSteamId, r.AcceptsMessages))
                .Add("chatOpen", r.OwnerSteamId != 0UL && ChatPanel.OpenPeer == r.OwnerSteamId);
        }

        private static Json VanillaLobbyJson(VanillaLobbyRow r)
        {
            bool full = r.MaxPlayers > 0 && r.Members >= r.MaxPlayers;
            bool ready = VanillaLobby.AcceptsJoiners(r);
            bool otherBranch = !string.IsNullOrEmpty(r.Runtime) && !string.Equals(r.Runtime, LobbyCoordinator.ThisRuntime, StringComparison.OrdinalIgnoreCase);
            string state, text, before, warn = "";
            if (full) { state = "full"; text = "Full"; before = "Every seat is taken."; }
            else if (!ready)
            {
                state = "notready"; text = "Not accepting joiners";
                before = "The host's save never told the game it is ready for players.";
                warn = $"{(string.IsNullOrEmpty(r.HostName) ? "This host" : r.HostName)}'s lobby never told the game it is ready for players. "
                       + "Joining would install their mods, restart your game and leave you in the menu. Try anyway if they just finished loading.";
            }
            else if (otherBranch) { state = "version"; text = "Other game branch"; before = "The host plays on the " + r.Runtime + " branch. Joining may fail."; }
            else { state = "ready"; text = r.Enforced ? "Synced players only" : "Mods sync on join"; before = "Compares your mods with the host's before joining."; }

            return Json.Object()
                .Add("id", r.LobbyId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Add("mode", "vanilla")
                .Add("modeName", "Vanilla co-op")
                .Add("icon", "")
                .Add("host", r.HostName ?? "")
                .Add("name", !string.IsNullOrEmpty(r.LobbyName) ? r.LobbyName : !string.IsNullOrEmpty(r.Org) ? r.Org : (r.HostName ?? ""))
                .Add("members", r.Members)
                .Add("max", r.MaxPlayers)
                .Add("locked", r.HasPassword)
                .Add("askPassword", r.HasPassword && !string.IsNullOrEmpty(r.PwHash))
                .Add("state", state)
                .Add("stateText", text)
                .Add("before", before)
                .Add("warn", warn)
                .Add("canJoin", !full)
                .Add("chat", ChatPanel.Possible(r.OwnerSteamId, r.AcceptsMessages))
                .Add("chatOpen", r.OwnerSteamId != 0UL && ChatPanel.OpenPeer == r.OwnerSteamId);
        }

        private static Json StatusJson()
        {
            var f = _flow;
            if (f == null) return null;
            var stns = Json.Array();
            for (int i = 0; i < f.Labels.Count; i++)
            {
                string state = i < f.Step ? "done" : i > f.Step ? "todo" : f.Error != null ? "fail" : "now";
                string note = f.Notes.TryGetValue(i, out var n) ? n : "";
                if (i == f.Step && f.Error == null && f.Labels[i] == "Restart" && RestartCountdownActive)
                    note = _rcFire != null || _rcRemaining <= 0f ? "Restarting..." : $"in {Mathf.Max(0, Mathf.CeilToInt(_rcRemaining))}s";
                stns.Item(Json.Object().Add("label", f.Labels[i]).Add("state", state).Add("note", note ?? ""));
            }

            var acts = Json.Array();
            if (f.Error != null)
                acts.Item(Json.Object().Add("id", "close").Add("label", "Close").Add("go", false));
            else if (RestartCountdownActive && _rcFire == null)
            {
                acts.Item(Json.Object().Add("id", "restartNow").Add("label", "Restart now").Add("go", true));
                acts.Item(Json.Object().Add("id", "cancel").Add("label", "Cancel").Add("go", false));
            }
            else if (_rcFire == null)
                acts.Item(Json.Object().Add("id", "cancel").Add("label", "Cancel").Add("go", false));

            return Json.Object()
                .Add("title", f.Title)
                .Add("stations", stns)
                .Add("progress", -1)
                .Add("error", f.Error ?? "")
                .Add("actions", acts);
        }

        // ------------------------------------------------------------------------------------------------------
        // icons
        // ------------------------------------------------------------------------------------------------------

        /// <summary>The picture name a gamemode is drawn with on the page, published once per mount. "" when it has
        /// none or it could not be read - the page then shows the empty tile.</summary>
        private static string HubIcon(GamemodeDescriptor desc)
        {
            if (desc == null || _hub == null) return "";
            string name = "icon/" + desc.Id;
            if (_hubIcons.Contains(name)) return name;
            if (_hubIconsFailed.Contains(name)) return "";

            byte[] png = null;
            try
            {
                png = IconResourceBytes(desc);
                if (png == null)
                {
                    IconResources.Fill(desc);
                    Texture tex = desc.IconTex != null ? desc.IconTex : desc.Icon != null ? desc.Icon.texture : null;
                    if (tex != null) png = PngOf(tex);
                }
            }
            catch (Exception e) { Core.Log?.Warning($"[hub3] icon of '{desc.Id}' could not be read: {e.Message}"); }

            if (png == null || png.Length == 0)
            {
                _hubIconsFailed.Add(name);
                Core.Log?.Msg($"[hub3] '{desc.Id}' has no icon the page can draw.");
                return "";
            }
            _hub.Image(name, png);
            _hubIcons.Add(name);
            Core.Log?.Msg($"[hub3] published '{name}' ({png.Length} bytes).");
            return name;
        }

        // The icon of a gamemode the boot gate held back: a PNG the gate copied out of the unloaded DLL.
        private static string HubIconFile(string path)
        {
            if (string.IsNullOrEmpty(path) || _hub == null) return "";
            string name = "icon/file/" + System.IO.Path.GetFileNameWithoutExtension(path);
            if (_hubIcons.Contains(name)) return name;
            if (_hubIconsFailed.Contains(name)) return "";
            byte[] png = null;
            try { png = System.IO.File.ReadAllBytes(path); }
            catch (Exception e) { Core.Log?.Warning($"[hub3] icon file '{path}' could not be read: {e.Message}"); }
            if (png == null || png.Length == 0)
            {
                _hubIconsFailed.Add(name);
                return "";
            }
            _hub.Image(name, png);
            _hubIcons.Add(name);
            Core.Log?.Msg($"[hub3] published '{name}' ({png.Length} bytes).");
            return name;
        }

        // Thunderstore package icons for the browser page. Each URL is downloaded once per session and re-encoded at
        // PkgIconPx, so the page gets a small PNG written by Unity whatever Thunderstore stored. The bytes outlive a
        // remount of the page; only the publishing is repeated.
        private const int PkgIconPx = 96;
        private static readonly Dictionary<string, byte[]> _pkgIconPng = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> _pkgIconName = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly HashSet<string> _pkgIconPending = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The picture name of a package icon, or "" while it downloads or when it has none. A finished
        /// download redraws the page, which then asks again.</summary>
        private static string HubPackageIcon(string url)
        {
            if (string.IsNullOrEmpty(url) || _hub == null) return "";
            if (!_pkgIconName.TryGetValue(url, out string name))
            {
                name = "pkg/" + (_pkgIconName.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                _pkgIconName[url] = name;
            }
            if (_hubIcons.Contains(name)) return name;
            if (_pkgIconPng.TryGetValue(url, out var png))
            {
                if (png == null) return "";
                _hub.Image(name, png);
                _hubIcons.Add(name);
                return name;
            }
            if (_pkgIconPending.Add(url)) FetchPackageIcon(url);
            return "";
        }

        private static void FetchPackageIcon(string url)
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                byte[] bytes = null;
                try { bytes = await ThunderstoreClient.DownloadBytesAsync(url, System.Threading.CancellationToken.None).ConfigureAwait(false); }
                catch (Exception e) { Core.Log?.Warning($"[hub3] package icon {url} could not be fetched: {e.Message}"); }
                MainThread.Post(() =>
                {
                    _pkgIconPending.Remove(url);
                    byte[] png = null;
                    if (bytes != null && bytes.Length > 0) png = ShrinkPng(bytes, url);
                    else Core.Log?.Msg($"[hub3] package icon {url}: no bytes, the row keeps its initials.");
                    _pkgIconPng[url] = png;
                    _hubSinceState = 999f;
                });
            });
        }

        private static byte[] ShrinkPng(byte[] bytes, string url)
        {
            Texture2D tex = null;
            try
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(bytes))
                {
                    Core.Log?.Warning($"[hub3] package icon {url} is not a readable picture ({bytes.Length} bytes).");
                    return null;
                }
                return PngOf(tex, PkgIconPx);
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"[hub3] package icon {url} could not be decoded: {e.Message}");
                return null;
            }
            finally { if (tex != null) UnityEngine.Object.Destroy(tex); }
        }

        // An embedded PNG is already what the page wants; no round trip through a texture. Without IconResource the
        // conventional "<AssemblyName>.hub-icon.png" is tried, the same name the boot gate reads from unloaded mods.
        private static byte[] IconResourceBytes(GamemodeDescriptor desc)
        {
            if (desc.OwnerAssembly == null) return null;
            string resource = string.IsNullOrWhiteSpace(desc.IconResource)
                ? desc.OwnerAssembly.GetName().Name + ".hub-icon.png"
                : desc.IconResource.Trim();
            using (var s = desc.OwnerAssembly.GetManifestResourceStream(resource))
            {
                if (s == null) return null;
                using (var ms = new System.IO.MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            }
        }

        // A gamemode's own texture is usually not readable from script, so it is drawn into a temporary target and
        // read back from there.
        private static byte[] PngOf(Texture tex, int max = 512)
        {
            float scale = Mathf.Min(1f, (float)max / Mathf.Max(1, Mathf.Max(tex.width, tex.height)));
            int w = Mathf.Clamp(Mathf.RoundToInt(tex.width * scale), 1, max), h = Mathf.Clamp(Mathf.RoundToInt(tex.height * scale), 1, max);
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            Texture2D copy = null;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                copy.Apply();
                var data = ImageConversion.EncodeToPNG(copy);
                return data != null ? data.ToArray() : null;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (copy != null) UnityEngine.Object.Destroy(copy);
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // calls from the page
        // ------------------------------------------------------------------------------------------------------

        private const string RefuseBusy = "A session is already starting.";
        private const string RefuseGone = "That gamemode is not installed.";

        private static GamemodeDescriptor FindMode(string id) =>
            string.IsNullOrEmpty(id) ? null : API.Registered.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

        private static string HubCallHostAgain(string id)
        {
            var desc = FindMode(id);
            if (desc == null) { Core.Log?.Warning($"[hub3] host again: '{id}' is not registered."); return RefuseGone; }
            if (HubBusy()) { Core.Log?.Msg("[hub3] host again refused: busy."); return RefuseBusy; }
            var saved = Preferences.GetHost(desc.Id);
            if (saved == null)
            {
                Core.Log?.Msg($"[hub3] host again for '{desc.Id}' without remembered settings; opening the form.");
                RouteToHub("play", desc.Id, true);
                return "";
            }
            FlowHost(desc, PolicyNote(desc, saved.PolicyMode));
            HostWith(desc, saved, "Host again");
            return "";
        }

        private static string PolicyNote(GamemodeDescriptor desc, int savedMode)
        {
            if (desc.Policy == null) return "";
            try
            {
                var plan = Mods.ModPolicyResolver.Resolve(desc);
                if (plan == null || !plan.HasChanges) return "Ready";
                int mode = (!plan.Blocked && desc.DefaultRequiredModsOnly) ? 1 : 0;
                if (!plan.Blocked && savedMode >= 0) mode = savedMode;
                return mode == 1 ? "Required only" : "All installed";
            }
            catch (Exception e) { Core.Log?.Warning($"[hub3] mod check of '{desc.Id}' failed for the status note: {e.Message}"); return ""; }
        }

        private static string HubCallPlay(string id)
        {
            var desc = FindMode(id);
            if (desc == null) { Core.Log?.Warning($"[hub3] play: '{id}' is not registered."); return RefuseGone; }
            if (!desc.AllowsSingleplayer) return "This gamemode has no solo mode.";
            if (HubBusy()) { Core.Log?.Msg("[hub3] play refused: busy."); return RefuseBusy; }
            Core.Log?.Msg($"[hub3] play '{desc.Id}'.");
            // A mod policy shows the classic mod check first; it needs the player's yes and a restart.
            if (desc.Policy != null)
            {
                Mods.ModPlan plan;
                try { plan = Mods.ModPolicyResolver.Resolve(desc); }
                catch (Exception e) { Core.Log?.Error($"[hub3] play: mod check of '{desc.Id}' failed: {e}"); return "Something went wrong."; }
                if (plan.Blocked || plan.HasChanges) { ShowModCheck(desc, plan); return ""; }
            }
            LaunchSelected(desc);
            return "";
        }

        private static string HubCallLoad(string id)
        {
            const string prefix = "waiting:";
            string file = id != null && id.StartsWith(prefix, StringComparison.Ordinal) ? id.Substring(prefix.Length) : id;
            var c = Mods.GamemodeProbe.Waiting().FirstOrDefault(w => string.Equals(w.File, file, StringComparison.OrdinalIgnoreCase));
            if (c == null) { Core.Log?.Warning($"[hub3] start: no held-back gamemode '{file}'."); return "That gamemode is no longer waiting."; }
            Core.Log?.Msg($"[hub3] starting held-back gamemode '{c.Name}'.");
            StartWaitingGamemode(c);
            return "";
        }

        private static string HubCallVanillaHost()
        {
            if (HubBusy()) { Core.Log?.Msg("[hub3] vanilla host refused: busy."); return RefuseBusy; }
            var again = Preferences.GetHost(Preferences.VanillaHostKey);
            var save = RememberedVanillaSave(again);
            if (save == null)
            {
                Core.Log?.Msg("[hub3] vanilla host: no remembered save; opening the save picker.");
                ShowVanillaSavePicker();
                return "";
            }
            string org;
            try { org = save.OrganisationName; }
            catch (Exception e) { org = "your save"; Core.Log?.Warning("[hub3] could not read the organisation name: " + e.Message); }
            FlowVanillaHost(org);
            HostVanillaDirect(save, again);
            _hubView = true;   // the "Preparing your mod list" screen is the flow's first station
            return "";
        }

        private static string HubCallHostForm(string id)
        {
            var desc = FindMode(id);
            if (desc == null) return Json.Object().Add("error", RefuseGone).ToString();
            if (!desc.AllowsMultiplayer) return Json.Object().Add("error", "This gamemode cannot be hosted.").ToString();
            int cap = Mathf.Max(2, LobbyCaps.MaxClients());
            var saved = Preferences.GetHost(desc.Id);
            Mods.ModPlan plan = null;
            try { plan = desc.Policy != null ? Mods.ModPolicyResolver.Resolve(desc) : null; }
            catch (Exception e) { Core.Log?.Warning($"[hub3] host form: mod check of '{desc.Id}' failed: {e.Message}"); }
            bool hasPolicy = plan != null && plan.HasChanges && !plan.Blocked;
            int policyMode = (hasPolicy && desc.DefaultRequiredModsOnly) ? 1 : 0;
            if (hasPolicy && saved != null && saved.PolicyMode >= 0) policyMode = saved.PolicyMode;
            return Json.Object()
                .Add("id", desc.Id)
                .Add("name", desc.DisplayName ?? desc.Id)
                .Add("icon", HubIcon(desc))
                .Add("author", desc.Author ?? "")
                .Add("players", Mathf.Clamp(saved != null && saved.MaxPlayers >= 2 ? saved.MaxPlayers : Mathf.Min(4, cap), 2, cap))
                .Add("min", 2)
                .Add("max", cap)
                .Add("visibility", saved != null ? saved.Visibility : 0)
                .Add("lobbyName", saved?.LobbyName ?? "")
                .Add("defaultName", HostConfigView.DefaultLobbyName(desc))
                .Add("policy", hasPolicy)
                .Add("policyMode", policyMode)
                .Add("settings", desc.HostSettings != null && desc.HostSettings.Length > 0)
                .Add("solo", desc.AllowsSingleplayer)
                .ToString();
        }

        private static string HubCallHost(string arg)
        {
            string id, lobbyName, password;
            int players, visibility, policyMode;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrEmpty(arg) ? "{}" : arg);
                var root = doc.RootElement;
                id = Str(root, "id");
                players = Int(root, "players", 4);
                visibility = Int(root, "visibility", 0);
                lobbyName = Str(root, "lobbyName");
                password = Str(root, "password");
                policyMode = Int(root, "policyMode", 0);
            }
            catch (Exception e) { Core.Log?.Error("[hub3] host: unreadable form: " + e.Message); return "Something went wrong."; }

            var desc = FindMode(id);
            if (desc == null) { Core.Log?.Warning($"[hub3] host: '{id}' is not registered."); return RefuseGone; }
            if (HubBusy()) { Core.Log?.Msg("[hub3] host refused: busy."); return RefuseBusy; }

            int cap = Mathf.Max(2, LobbyCaps.MaxClients());
            players = Mathf.Clamp(players, 2, cap);
            Mods.ModPlan plan;
            try { plan = desc.Policy != null ? Mods.ModPolicyResolver.Resolve(desc) : null; }
            catch (Exception e) { Core.Log?.Error($"[hub3] host: mod check of '{desc.Id}' failed: {e}"); return "Something went wrong."; }
            // Required mods are missing: hosting would start the gamemode without them. The mod check names them,
            // the same screen Play shows.
            if (plan != null && plan.Blocked)
            {
                Core.Log?.Msg($"[hub3] host '{desc.Id}' refused: {plan.MissingRequired.Count} required mod(s) missing.");
                ShowModCheck(desc, plan);
                return "";
            }
            bool hasPolicy = plan != null && plan.HasChanges && !plan.Blocked;
            int mode = hasPolicy ? (policyMode == 1 ? 1 : 0) : 0;
            string name = HostConfigView.Sanitize(lobbyName ?? "");
            var saved = Preferences.GetHost(desc.Id);
            var opts = new HostOptions
            {
                MaxPlayers = players,
                Visibility = visibility == 1 ? LobbyVisibility.Private : LobbyVisibility.Public,
                Password = string.IsNullOrEmpty(password) ? null : password,
                ConfigBlob = HostConfigView.DefaultConfig(desc, players, out var modeLabel),
                // The form shows the default name in an empty field, so an empty field means that name.
                LobbyName = name.Length > 0 ? name : HostConfigView.DefaultLobbyName(desc),
                ModeLabel = modeLabel,
                DiscordCode = saved != null && DiscordInvite.IsCode(saved.DiscordCode) ? saved.DiscordCode : null,
            };
            Preferences.SetHost(desc.Id, new Preferences.RememberedHost
            {
                MaxPlayers = players,
                Visibility = visibility == 1 ? 1 : 0,
                LobbyName = name,
                DiscordCode = opts.DiscordCode ?? "",
                PolicyMode = hasPolicy ? mode : -1,
            });
            Core.Log?.Msg($"[hub3] host '{desc.Id}' from the page: {players} players, {(visibility == 1 ? "friends only" : "public")}, " +
                          $"password {(opts.Password != null ? "set" : "none")}, mod mode {mode}.");
            FlowHost(desc, !hasPolicy ? (desc.Policy != null ? "Ready" : "") : mode == 1 ? "Required only" : "All installed");
            StartHostConfigured(desc, opts, mode, plan);
            return "";
        }

        private static string HubCallJoin(string arg)
        {
            string id, password;
            bool anyway;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrEmpty(arg) ? "{}" : arg);
                id = Str(doc.RootElement, "id");
                password = Str(doc.RootElement, "password");
                anyway = doc.RootElement.TryGetProperty("anyway", out var a) && a.ValueKind == JsonValueKind.True;
            }
            catch (Exception e) { Core.Log?.Error("[hub3] join: unreadable request: " + e.Message); return "Something went wrong."; }

            if (!ulong.TryParse(id, out ulong lobbyId)) { Core.Log?.Warning($"[hub3] join: '{id}' is not a lobby id."); return "That lobby is gone."; }
            if (HubBusy()) { Core.Log?.Msg("[hub3] join refused: busy."); return RefuseBusy; }

            var vr = _hubVanillaLobbies.FirstOrDefault(r => r.LobbyId == lobbyId);
            if (vr != null) return HubJoinVanilla(vr, password, anyway);

            var row = _hubGameLobbies.FirstOrDefault(r => r.LobbyId == lobbyId);
            if (row == null) { Core.Log?.Warning($"[hub3] join: lobby {lobbyId} is not in the last result."); return "That lobby is gone. Refresh the list."; }
            if (row.MaxPlayers > 0 && row.Members >= row.MaxPlayers) return "That lobby is full.";
            if (!PasswordOk(row.HasPassword, row.PwHash, password)) return "Incorrect password.";

            var desc = FindMode(row.GamemodeId);
            if (desc == null)
            {
                // Not installed: the classic install-and-join chain, which shows its consent on the old screen.
                var g = new Ghost
                {
                    Name = string.IsNullOrEmpty(row.GamemodeName) ? row.GamemodeId : row.GamemodeName,
                    GamemodeId = row.GamemodeId,
                    Url = row.DownloadUrl,
                };
                g.Rows.Add(row);
                g.Players = Mathf.Max(1, row.Members);
                Core.Log?.Msg($"[hub3] join lobby {lobbyId} of not-installed '{row.GamemodeId}'.");
                FlowJoin(g.Name, row.HostName, "mp", true);
                _ghostRetried = false;
                ReadGhostManifest(g, row);
                return "";
            }

            Core.Log?.Msg($"[hub3] join lobby {lobbyId} of '{desc.Id}' (host {row.HostName}).");
            FlowJoin(desc.DisplayName ?? desc.Id, row.HostName, "mp", desc.Surface == GamemodeSurface.World);
            JoinNow(desc, row);
            return "";
        }

        // The Chat button of a lobby: opens the column for its host, or closes it when it is already theirs. An
        // empty id closes whatever column is open; the page sends that when it leaves the Lobbies tab.
        private static string HubCallChat(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                if (ChatPanel.IsOpen) Core.Log?.Msg("[hub3] chat: closed, the page left the lobby list.");
                ChatPanel.Hide();
                _hubSinceState = 999f;
                return "";
            }
            if (!ulong.TryParse(id, out ulong lobbyId)) { Core.Log?.Warning($"[hub3] chat: '{id}' is not a lobby id."); return "That lobby is gone."; }

            ulong owner;
            string host;
            bool accepts;
            var vr = _hubVanillaLobbies.FirstOrDefault(r => r.LobbyId == lobbyId);
            var gr = vr == null ? _hubGameLobbies.FirstOrDefault(r => r.LobbyId == lobbyId) : null;
            if (vr != null) { owner = vr.OwnerSteamId; host = vr.HostName; accepts = vr.AcceptsMessages; }
            else if (gr != null) { owner = gr.OwnerSteamId; host = gr.HostName; accepts = gr.AcceptsMessages; }
            else { Core.Log?.Warning($"[hub3] chat: lobby {lobbyId} is not in the last result."); return "That lobby is gone. Refresh the list."; }

            if (!ChatPanel.Possible(owner, accepts))
            {
                Core.Log?.Warning($"[hub3] chat: refused for lobby {lobbyId} (owner {owner}, takes messages {accepts}, JoinChatPanel {Preferences.JoinChatPanel}).");
                return "This host does not take messages.";
            }
            ChatPanel.Toggle(owner, host);
            Core.Log?.Msg($"[hub3] chat: {(ChatPanel.OpenPeer == owner ? "opened" : "closed")} for {host} ({owner}), lobby {lobbyId}.");
            _hubSinceState = 999f;
            return "";
        }

        private static string HubJoinVanilla(VanillaLobbyRow row, string password, bool anyway)
        {
            if (row.MaxPlayers > 0 && row.Members >= row.MaxPlayers) return "That lobby is full.";
            if (!anyway && !VanillaLobby.AcceptsJoiners(row)) return "That lobby is not accepting joiners.";
            if (!PasswordOk(row.HasPassword, row.PwHash, password)) return "Incorrect password.";
            Core.Log?.Msg($"[hub3] join vanilla lobby {row.LobbyId} (host {row.HostName}{(anyway ? ", not marked ready" : "")}).");
            InvalidateJoinChecks();
            _vanillaRetried = false;
            FlowJoin("Vanilla co-op", row.HostName, "sync", true);
            ShowVanillaSyncCheck(row);
            return "";
        }

        // The casual client-side gate the classic browser uses: the lobby advertises a hash of its password.
        private static bool PasswordOk(bool hasPassword, string pwHash, string entered)
        {
            if (!hasPassword || string.IsNullOrEmpty(pwHash)) return true;
            bool ok = string.Equals(LobbyCoordinator.HashPassword(entered ?? ""), pwHash, StringComparison.Ordinal);
            if (!ok) Core.Log?.Msg("[hub3] join: the password did not match.");
            return ok;
        }

        private static string HubCallStatus(string action)
        {
            var f = _flow;
            switch (action)
            {
                case "restartNow":
                    if (!RestartCountdownActive) { Core.Log?.Warning("[hub3] restart now without a countdown."); return ""; }
                    Core.Log?.Msg("[hub3] restart now.");
                    RestartCountdownFire();
                    return "";

                case "close":
                    Core.Log?.Msg("[hub3] status closed.");
                    _flow = null;
                    return "";

                case "cancel":
                    if (f == null) return "";
                    Core.Log?.Msg($"[hub3] '{f.Title}' cancelled by the player at '{(f.Step < f.Labels.Count ? f.Labels[f.Step] : "?")}'.");
                    if (RestartCountdownActive) RestartCountdownCancel();
                    if (f.Launched)
                    {
                        if (f.Coordinator == "sync") SyncCoordinator.Cancel();
                        else MultiplayerCoordinator.Cancel();
                    }
                    else
                    {
                        // Not launched: a mod list being read or compared. The join checks drop their answers and
                        // the vanilla host's "Preparing" screen going away is its cancel.
                        InvalidateJoinChecks();
                        ClearFormHost();
                        if (ScreenOpen) _hubView = true;
                    }
                    _flow = null;
                    return "";
            }
            Core.Log?.Warning($"[hub3] unknown status action '{action}'.");
            return "";
        }

        private static string HubCallLegacy(string what)
        {
            what = what ?? "";
            Core.Log?.Msg($"[hub3] classic screen: {what}.");
            if (what == "settings") { ShowSettings(); return ""; }
            if (what == "profiles") { OpenProfilesFromHub(); return ""; }
            if (what == "vanillasaves") { ShowVanillaSavePicker(); return ""; }
            const string hostForm = "hostform:";
            if (what.StartsWith(hostForm, StringComparison.Ordinal))
            {
                var desc = FindMode(what.Substring(hostForm.Length));
                if (desc == null) return RefuseGone;
                _hubForceLegacy = true;
                try { ShowHostConfig(desc); }
                finally { _hubForceLegacy = false; }
                return "";
            }
            Core.Log?.Warning($"[hub3] no classic screen called '{what}'.");
            return "";
        }

        private static string HubCallPref(string arg)
        {
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrEmpty(arg) ? "{}" : arg);
                var root = doc.RootElement;
                if (root.TryGetProperty("hideFull", out var hf)) Preferences.BrowserHideFull = hf.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("hideLocked", out var hl)) Preferences.BrowserHideLocked = hl.ValueKind == JsonValueKind.True;
                Core.Log?.Msg($"[hub3] board filters: hide full {Preferences.BrowserHideFull}, hide locked {Preferences.BrowserHideLocked}.");
            }
            catch (Exception e) { Core.Log?.Error("[hub3] pref: unreadable request: " + e.Message); return "Something went wrong."; }
            return "";
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        private static int Int(JsonElement e, string name, int fallback) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : fallback;

        // ------------------------------------------------------------------------------------------------------
        // the main menu opens in the hub
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Called once per menu load after the main-menu entries are in place. Opens the hub unless something else
        /// owns the screen: a restart notice, a session being continued, or a hub that is already open.
        /// </summary>
        internal static void OpenOnMenuLoad()
        {
            if (!Preferences.Enabled) { Core.Log?.Msg("[hub3] menu load: Side Hustle is disabled."); return; }
            if (ScreenOpen) { Core.Log?.Msg("[hub3] menu load: the hub is already open."); return; }
            if (CommittedRestart.Pending || MultiplayerCoordinator.IsBusy || SyncCoordinator.IsBusy)
            {
                Core.Log?.Msg("[hub3] menu load: a session or restart owns the screen; not opening the hub.");
                return;
            }
            if (!EnsureHubSurfaceAfterClone()) { Core.Log?.Msg("[hub3] menu load: no surface, the main menu stays."); return; }
            if (Preferences.HubFolded) { Core.Log?.Msg("[hub3] menu load: the hub stays folded away."); return; }
            Core.Log?.Msg("[hub3] menu load: opening the hub.");
            OpenScreen();
        }

        private static bool EnsureHubSurfaceAfterClone()
        {
            EnsureInit();
            EnsureClone();
            return _cloneScreen != null && EnsureHubSurface();
        }
    }
}
