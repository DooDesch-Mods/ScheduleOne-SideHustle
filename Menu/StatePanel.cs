using System;
using System.Globalization;
using Sideload.Api;
using SideHustle.Config;
using SideHustle.Multiplayer;
using SideHustle.Phone;
using UnityEngine;

namespace SideHustle.Menu
{
    /// <summary>
    /// A column down the right of the main menu that answers one question: what happens if I press something now.
    ///
    /// The menu already knows all of this and shows none of it. Which mod set the game booted with, whether any
    /// published session is actually taking players, whether somebody wrote while the game was closed - each of
    /// those currently costs a click into a screen, and two of them cost a restart to undo if the answer was no.
    ///
    /// Built on Sideload Surfaces rather than hand-assembled uGUI, which is the first real use of that door: the
    /// panel is HTML, so changing it is a stylesheet edit and not a pile of RectTransform arithmetic. When Sideload
    /// is older than 1.13.0 the mount answers false and nothing appears - the menu is exactly as it was, which is
    /// the correct fallback for a panel that only ever informs.
    /// </summary>
    internal static class StatePanel
    {
        internal const string SurfaceId = "sidehustle-menu";

        /// <summary>Reference pixels against the menu's own 1920x1080 basis, so the column keeps its proportion to
        /// the controls beside it on every screen. Wide enough for a sentence at 13px, narrow enough to leave the
        /// menu's art alone on 16:9.</summary>
        private const float Width = 340f;

        private const float Margin = 24f;

        /// <summary>The free band down the right of the vanilla menu, as fractions of the screen height: above the
        /// social-link strip, below the community-vote card.</summary>
        private const float BandBottom = 0.07f;
        private const float BandTop = 0.44f;

        private static SurfaceHandle _surface;
        private static GameObject _panel;

        /// <summary>What the page last rendered, so the push happens when something moved and not every frame.</summary>
        private static string _pushed = "";
        private static bool _warnedNoSurfaces;

        /// <summary>Lobby counts, refreshed on a timer rather than per frame: each refresh is a Steam query.</summary>
        private static int _lobbies = -1, _joinable = -1;
        private static float _sinceQuery = 999f;
        private static bool _querying;

        /// <summary>
        /// How long to wait before asking again, by attempt.
        ///
        /// Not one flat interval: the case that matters is a host who opens their session while the joiner is
        /// already sitting in the menu, and a twenty-second clock makes that look like nothing is there. The first
        /// few checks come quickly and then it settles down, so the number is right within a couple of seconds of
        /// arriving and still costs one Steam query every twenty after that.
        /// </summary>
        private static readonly float[] Cadence = { 3f, 5f, 8f, 12f, 20f };
        private static int _attempt;

        /// <summary>How far up the ramp an EMPTY answer may settle. "Nobody is hosting" is the reading most likely
        /// to be out of date and the one that costs the player the most to believe, so it keeps checking often;
        /// once something is listed the column has nothing urgent left to learn and drops to the slow step.</summary>
        private const int EmptyMaxStep = 2;

        /// <summary>
        /// The menu scene is being rebuilt. The canvas is ours, so it does not go on its own - and leaving it would
        /// stack a second panel on top of the first every time the menu re-initialises, which it does more than once
        /// per load.
        /// </summary>
        internal static void Reset()
        {
            try { ServerBrowser.VanillaResultsTap = null; } catch { }
            try { Surfaces.Unmount(SurfaceId); } catch { }
            try { if (_panel != null) UnityEngine.Object.Destroy(_panel); } catch { }
            _panel = null;
            _surface = null;
            _pushed = "";
            _sinceQuery = 999f;
            _attempt = 0;
        }

        /// <summary>
        /// Put the panel up, or adopt the one already there. Called from the same retry loop that injects the menu
        /// button, so it inherits that loop's warmup - the menu's own navigation has finished initialising by then.
        /// </summary>
        /// <summary>
        /// Step aside while a Side Hustle screen is up.
        ///
        /// The two right-hand columns live in the same strip by design - it is the one part of the menu vanilla
        /// leaves free - so they take turns instead of stacking. A plain latch with exactly one caller
        /// (Core's menu pump), because two callers is how it drifted: nothing took this column down on a subscreen
        /// and nothing brought it back after the chat column closed.
        /// </summary>
        internal static bool Suspended { get; private set; }

        internal static void Suspend()
        {
            if (Suspended) return;
            Suspended = true;
            Reset();
        }

        internal static void Resume()
        {
            if (!Suspended) return;
            Suspended = false;
            Ensure();
        }

        internal static void Ensure()
        {
            if (!Preferences.MenuPanel || Suspended) return;
            if (_panel != null && Surfaces.IsMounted(SurfaceId)) return;

            if (!Surfaces.Available)
            {
                // Once per session, NOT written to the config. Writing it there outlived its cause: the player
                // installed a newer Sideload, the column never came back, and the settings row read OFF for a
                // switch they had never touched.
                if (!_warnedNoSurfaces)
                {
                    _warnedNoSurfaces = true;
                    Core.Log?.Msg("[menu] the installed Sideload cannot render outside the phone - no state panel.");
                }
                return;
            }

            try
            {
                // Its OWN screen-space-overlay canvas, not the menu's.
                //
                // The main menu is a lit 3D scene and its canvas is drawn by that camera, so everything on it goes
                // through the same tone mapping the scene does: a measured #808080 arrives on screen as #383838, and
                // every dark surface collapses to black. An overlay canvas is composited after the camera, so a
                // colour lands as the colour that was written - which is the whole point of styling this in CSS.
                //
                // No GraphicRaycaster on purpose. The panel only ever tells you things, and a raycaster over a
                // quarter of the screen would swallow clicks meant for the menu behind it.
                _panel = new GameObject("SideHustle_StatePanel");
                var canvas = _panel.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                // Above the menu, far below OverlayNotice's 32000 - a rejoin notice has to cover this, not the
                // other way round.
                canvas.sortingOrder = 100;

                // The menu scales to a 1920x1080 reference, so a column measured in raw device pixels covers the
                // buttons underneath it on a small screen - and its raycaster swallows the clicks - while rendering
                // at half the surrounding text size at 4K. Fixed reference, deliberately NOT the player's UI Scale
                // slider: following that re-creates the overlap at 1.5.
                var scaler = _panel.AddComponent<UnityEngine.UI.CanvasScaler>();
                scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0f;   // the game matches width

                var holder = new GameObject("panel");
                var rect = holder.AddComponent<RectTransform>();
                rect.SetParent(_panel.transform, false);

                // The right edge is not empty: vanilla puts its community-vote card across the top of it and its
                // social links along the very bottom. The band between them is what is actually free, so the panel
                // takes that and nothing else. Anchored in fractions rather than pixels so it keeps its place when
                // the resolution changes, with nobody watching for that.
                rect.anchorMin = new Vector2(1f, BandBottom);
                rect.anchorMax = new Vector2(1f, BandTop);
                rect.pivot = new Vector2(1f, 0.5f);
                rect.sizeDelta = new Vector2(Width, 0f);
                rect.anchoredPosition = new Vector2(-Margin, 0f);

                _surface = Surfaces.Mount(rect, SurfaceId, "SideHustle.Assets.menu")
                    .OnCall("menu.state", _ => State());

                // Anything else that queries the lobby list - opening the browser, the hub's prewarm - updates this
                // column too. Otherwise the two run on separate clocks and the panel reads stale next to a browser
                // that just refreshed, which is exactly how it looked.
                ServerBrowser.VanillaResultsTap = Adopt;

                Core.Log?.Msg("[menu] state panel mounted.");
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[menu] could not mount the state panel: " + e.Message);
                _panel = null;
            }
        }

        /// <summary>Pumped from Core.OnUpdate while the menu is up: refreshes the lobby counts on a timer and pushes
        /// one event when anything the page shows has actually changed.</summary>
        internal static void Tick(float dt)
        {
            if (_panel == null) return;

            _sinceQuery += dt;
            if (_sinceQuery >= Cadence[Math.Min(_attempt, Cadence.Length - 1)] && !_querying)
            {
                _sinceQuery = 0f;
                int ceiling = _lobbies == 0 ? EmptyMaxStep : Cadence.Length - 1;
                if (_attempt < ceiling) _attempt++; else _attempt = ceiling;
                Query();
            }

            string now = State();
            if (now == _pushed) return;
            _pushed = now;
            try { _surface?.Emit("menu.changed", ""); } catch { /* the page reads state on its next build */ }
        }

        /// <summary>
        /// How many sessions are advertised and how many of them would actually let someone in.
        ///
        /// The second number is the one worth having: a lobby whose host opened it from the pause menu without
        /// publishing never marks itself ready, and joining it does nothing at all. Counting them here means the
        /// menu can say "3 sessions, 1 taking players" before anyone spends a mod download finding out.
        /// </summary>
        private static void Query()
        {
            _querying = true;
            try { ServerBrowser.BeginQueryVanilla(rows => { _querying = false; Adopt(rows); }); }
            catch (Exception e)
            {
                _querying = false;
                Core.Log?.Warning("[menu] lobby count failed: " + e.Message);
            }
        }

        /// <summary>Take a lobby list as the current truth, whoever asked for it. Resets the cadence so a result
        /// that just landed is not immediately chased by another query.</summary>
        private static void Adopt(System.Collections.Generic.List<Sync.VanillaLobbyRow> rows)
        {
            // A completed query is a completed query, whoever asked for it - so nothing of ours is still outstanding.
            // Cheap insurance on the one latch in this class that, left stuck, stops the column updating for good.
            _querying = false;
            _sinceQuery = 0f;
            int wasLobbies = _lobbies;
            if (rows == null) { _lobbies = 0; _joinable = 0; }
            else
            {
                int open = 0;
                foreach (var row in rows) if (Sync.VanillaLobby.AcceptsJoiners(row)) open++;
                _lobbies = rows.Count;
                _joinable = open;
            }
            // Something moved, so something else may be about to - start the ramp over rather than waiting out the
            // slow step next to a list that is visibly changing.
            if (_lobbies != wasLobbies) _attempt = 0;
        }

        private static string State()
        {
            string profile = "";
            bool alt = false;
            try
            {
                alt = Mods.AltBase.IsAltSession();
                if (alt) profile = System.IO.Path.GetFileName(Mods.AltBase.CurrentBase() ?? "");
            }
            catch { }

            string lastError = "";
            try { lastError = Preferences.LastSessionError ?? ""; } catch { }

            return Phone.Json.Object()
                // Which mod set this process booted with. A player who restarted into a gamemode profile and then
                // wonders where their campaign went is the whole reason this line exists.
                .Add("profile", profile)
                .Add("isProfile", alt)
                .Add("lobbies", _lobbies)
                .Add("joinable", _joinable)
                .Add("counted", _lobbies >= 0)
                .Add("unread", ChatRelay.UnreadCount)
                .Add("lastError", lastError)
                .Add("version", Version())
                .ToString();
        }

        private static string Version()
        {
            try { return typeof(Core).Assembly.GetName().Version?.ToString(3) ?? ""; }
            catch { return ""; }
        }

        /// <summary>Numbers the page prints, formatted where they are read rather than in JavaScript - the engine's
        /// Jint has no locale and the mod already runs on the invariant culture.</summary>
        internal static string Number(int n) => n.ToString(CultureInfo.InvariantCulture);
    }
}
