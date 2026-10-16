using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SideHustle.Menu
{
    // Stages the vanilla menu scene (camera, the player's rig, extra lights) behind the hub page, and puts every
    // touched value back. The menu scene has no camera script and nothing per frame moves the camera, the Rig root or
    // the scene lights, so a value set once stays. AvatarLookController resets the Avatar's own local rotation every
    // frame, which is why the Rig parent is turned instead.
    internal static class HubScene
    {
        private struct Saved { public Vector3 Pos; public Quaternion Rot; public bool Active; }

        private static readonly Dictionary<IntPtr, (Transform t, Saved s)> _transforms = new Dictionary<IntPtr, (Transform, Saved)>();
        private static readonly Dictionary<IntPtr, (Light l, float intensity, Color color)> _lights = new Dictionary<IntPtr, (Light, float, Color)>();
        private static readonly Dictionary<string, GameObject> _added = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
        private static Camera _cam;
        private static float _camFov = -1f;

        private static Transform Remember(Transform t)
        {
            if (t == null) return null;
            IntPtr key = t.Pointer;
            if (!_transforms.ContainsKey(key))
                _transforms[key] = (t, new Saved { Pos = t.position, Rot = t.rotation, Active = t.gameObject.activeSelf });
            return t;
        }

        private static Camera MenuCamera()
        {
            if (_cam != null) return _cam;
            var go = GameObject.Find("Camera");   // tagged Untagged, so Camera.main is null in the menu
            _cam = go != null ? go.GetComponent<Camera>() : null;
            if (_cam == null) Core.Log?.Warning("[scene] the menu camera 'Camera' was not found.");
            return _cam;
        }

        internal static void SetCamera(Vector3 pos, Vector3 euler, float fov)
        {
            var cam = MenuCamera();
            if (cam == null) return;
            Remember(cam.transform);
            if (_camFov < 0f) _camFov = cam.fieldOfView;
            cam.transform.position = pos;
            cam.transform.rotation = Quaternion.Euler(euler);
            if (fov > 0f) cam.fieldOfView = fov;
        }

        internal static bool Place(string path, Vector3 pos, float? yaw)
        {
            var go = GameObject.Find(path);
            if (go == null) { Core.Log?.Warning($"[scene] '{path}' not found."); return false; }
            var t = Remember(go.transform);
            t.position = pos;
            if (yaw.HasValue) t.rotation = Quaternion.Euler(0f, yaw.Value, 0f);
            return true;
        }

        internal static bool SetActive(string path, bool on)
        {
            var t = FindAny(path);
            if (t == null) { Core.Log?.Warning($"[scene] '{path}' not found."); return false; }
            Remember(t).gameObject.SetActive(on);
            return true;
        }

        // Scales the intensity of every realtime light in the scene; baked lights live in the lightmaps and ignore it.
        internal static void ScaleLights(float factor)
        {
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (l == null || _added.ContainsKey(l.gameObject.name)) continue;
                IntPtr key = l.Pointer;
                if (!_lights.ContainsKey(key)) _lights[key] = (l, l.intensity, l.color);
                l.intensity = _lights[key].intensity * factor;
            }
        }

        internal static void AddLight(string name, LightType type, Vector3 pos, Vector3 euler, Color color, float intensity, float range, float spotAngle)
        {
            if (!_added.TryGetValue(name, out var go) || go == null)
            {
                go = new GameObject(name);
                go.AddComponent<Light>();
                _added[name] = go;
            }
            var l = go.GetComponent<Light>();
            l.type = type;
            l.color = color;
            l.intensity = intensity;
            l.range = range;
            if (type == LightType.Spot) l.spotAngle = spotAngle;
            l.shadows = LightShadows.None;
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(euler);
        }

        // The staged looks for the hub. Camera position, rotation (euler) and field of view in the menu scene's world
        // space; dim scales the realtime scene lights.
        private struct Preset { public Vector3 Pos, Euler; public float Fov, Dim; }

        private static readonly Dictionary<string, Preset> Presets = new Dictionary<string, Preset>(StringComparer.OrdinalIgnoreCase)
        {
            // the vanilla shot pulled back and darker: the RV's neon windows run along the top of the page
            ["neon"] = new Preset { Pos = new Vector3(-4.6f, 2.1f, 3.0f), Euler = new Vector3(8f, 138f, 0f), Fov = 50f, Dim = 0.5f },
            // low behind the cash stacks, the player and the RV beyond them
            ["cash"] = new Preset { Pos = new Vector3(5f, 0.9f, 3.5f), Euler = new Vector3(8f, 225f, 0f), Fov = 50f, Dim = 1f },
            // from above the open side: the player and the lit RV door stand upper left, above the vanilla menu buttons,
            // with the first cash stacks at the left edge; the hub page covers the right 60 percent
            ["haul"] = new Preset { Pos = new Vector3(-1f, 4f, 8f), Euler = new Vector3(30f, 206f, 0f), Fov = 50f, Dim = 1f },
        };

        // The look staged behind the hub page; null leaves the vanilla scene alone.
        internal static string Look = "haul";

        // Called when the hub page shows or hides: the staged look belongs to the page, the vanilla menu keeps its own.
        internal static void OnPageShown(bool shown)
        {
            if (string.IsNullOrEmpty(Look)) return;
            Reset();
            if (shown) Apply(Look);
        }

        internal static bool Apply(string key)
        {
            if (!Presets.TryGetValue(key ?? "", out var p)) { Core.Log?.Warning($"[scene] no preset '{key}'."); return false; }
            SetCamera(p.Pos, p.Euler, p.Fov);
            ScaleLights(p.Dim);
            // the vanilla menu's left-anchored shade and logo backdrop would sit as a dark band on the staged shot
            SetActive("MainMenu/Background", false);
            Core.Log?.Msg($"[scene] preset '{key}' applied.");
            return true;
        }

        internal static void Reset()
        {
            int n = 0;
            foreach (var kv in _transforms.Values)
            {
                if (kv.t == null) continue;
                kv.t.position = kv.s.Pos; kv.t.rotation = kv.s.Rot; kv.t.gameObject.SetActive(kv.s.Active); n++;
            }
            _transforms.Clear();
            foreach (var kv in _lights.Values)
                if (kv.l != null) { kv.l.intensity = kv.intensity; kv.l.color = kv.color; n++; }
            _lights.Clear();
            foreach (var go in _added.Values) if (go != null) { UnityEngine.Object.Destroy(go); n++; }
            _added.Clear();
            if (_cam != null && _camFov > 0f) { _cam.fieldOfView = _camFov; n++; }
            _camFov = -1f;
            _cam = null;   // the menu scene reloads after a session, and with it the camera
            if (n > 0) Core.Log?.Msg($"[scene] reset: {n} value(s) restored.");
        }

        // GameObject.Find skips inactive objects; this walks the scene roots so hidden ones can be found too.
        private static Transform FindAny(string path)
        {
            var active = GameObject.Find(path);
            if (active != null) return active.transform;
            string[] parts = path.Split('/');
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name != parts[0]) continue;
                Transform t = root.transform;
                for (int i = 1; i < parts.Length && t != null; i++) t = t.Find(parts[i]);
                if (t != null) return t;
            }
            return null;
        }

        private static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        private static string V(Vector3 v) => string.Format(CultureInfo.InvariantCulture, "({0:0.##} {1:0.##} {2:0.##})", v.x, v.y, v.z);

#if DEBUG
        // shscene - drive the staging from the dev console. Spaces in object names are written as '_'.
        internal static void Dev(string[] parts)
        {
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "dump";
            float F(int i, float d = 0f) => parts.Length > i && float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : d;
            string P(int i) => parts.Length > i ? parts[i].Replace('_', ' ') : "";
            try
            {
                switch (sub)
                {
                    case "dump": Dump(); break;
                    case "find": FindLog(P(2)); break;
                    case "cam":
                        SetCamera(new Vector3(F(2), F(3), F(4)), new Vector3(F(5), F(6), F(7)), F(8, -1f));
                        Core.Log?.Msg($"[scene] camera at {V(new Vector3(F(2), F(3), F(4)))} looking {V(new Vector3(F(5), F(6), F(7)))}.");
                        break;
                    case "move":
                        if (Place(P(2), new Vector3(F(3), F(4), F(5)), parts.Length > 6 ? F(6) : (float?)null))
                            Core.Log?.Msg($"[scene] moved '{P(2)}'.");
                        break;
                    case "show": if (SetActive(P(2), true)) Core.Log?.Msg($"[scene] '{P(2)}' shown."); break;
                    case "hide": if (SetActive(P(2), false)) Core.Log?.Msg($"[scene] '{P(2)}' hidden."); break;
                    case "dim": ScaleLights(F(2, 1f)); Core.Log?.Msg($"[scene] realtime lights at x{F(2, 1f)}."); break;
                    case "light":
                        // shscene light <name> point|spot x y z r g b intensity range [rx ry angle]
                        AddLight(P(2), parts.Length > 3 && parts[3] == "spot" ? LightType.Spot : LightType.Point,
                            new Vector3(F(4), F(5), F(6)), new Vector3(F(13), F(14), 0f), new Color(F(7), F(8), F(9)), F(10, 1f), F(11, 5f), F(15, 60f));
                        Core.Log?.Msg($"[scene] light '{P(2)}' set.");
                        break;
                    case "page": Hub.ShowPageCanvasForTest(parts.Length < 3 || parts[2] != "off"); break;
                    case "reset": Reset(); break;
                    case "preset": Reset(); Apply(P(2)); break;
                    case "res":
                        // shscene res <w> <h> - windowed, to check the framing at another size
                        Screen.SetResolution((int)F(2, 1920f), (int)F(3, 1080f), false);
                        Core.Log?.Msg($"[scene] resolution set to {(int)F(2, 1920f)}x{(int)F(3, 1080f)} windowed.");
                        break;
                    case "where": Where(P(2)); break;
                    case "size": Sizes(); break;
                    case "hit": Hit(F(2, 0.5f), F(3, 0.5f), false); break;
                    case "click": Hit(F(2, 0.5f), F(3, 0.5f), true); break;
                    case "esc":
                        // what Esc does on a menu screen with a previous screen: close the one on top
                        var top = Il2CppScheduleOne.UI.MainMenu.MenuScreen.Current;
                        if (top == null) { Core.Log?.Warning("[scene] no menu screen is open."); break; }
                        if (top.PreviousScreen == null) { Core.Log?.Warning($"[scene] '{top.name}' has no previous screen; Esc keeps it open."); break; }
                        Core.Log?.Msg($"[scene] closing '{top.name}' as Esc does.");
                        top.Close();
                        break;
                    default: Core.Log?.Warning($"[scene] unknown subcommand '{sub}'."); break;
                }
            }
            catch (Exception e) { Core.Log?.Error("[scene] " + sub + " failed: " + e); }
        }

        // Where an object lands on screen, as viewport fractions: x 0 is the left edge, 1 the right edge.
        private static void Where(string path)
        {
            var cam = MenuCamera();
            var t = FindAny(path);
            if (cam == null || t == null) { Core.Log?.Warning($"[scene] where: '{path}' or the camera not found."); return; }
            Vector3 v = cam.WorldToViewportPoint(t.position);
            Core.Log?.Msg(string.Format(CultureInfo.InvariantCulture, "[scene] '{0}' at {1} is on screen at x {2:0.00} y {3:0.00} depth {4:0.0}.",
                path, V(t.position), v.x, v.y, v.z));
        }

        // shscene hit|click <x> <y> - what a click at that viewport point reaches, topmost first; click also sends it
        private static void Hit(float x, float y, bool click)
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null) { Core.Log?.Warning("[scene] no event system."); return; }
            var data = new UnityEngine.EventSystems.PointerEventData(es) { position = new Vector2(x * Screen.width, y * Screen.height) };
            var hits = new Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
            es.RaycastAll(data, hits);
            Core.Log?.Msg(string.Format(CultureInfo.InvariantCulture, "[scene] hit at ({0:0.##} {1:0.##}): {2} target(s).", x, y, hits.Count));
            for (int i = 0; i < hits.Count && i < 4; i++)
            {
                var go = hits[i].gameObject;
                Core.Log?.Msg($"[scene]   {i}: {(go != null ? PathOf(go.transform) : "<none>")}");
            }
            if (!click) return;
            if (hits.Count == 0) { Core.Log?.Warning("[scene] nothing to click there."); return; }
            var target = UnityEngine.EventSystems.ExecuteEvents.ExecuteHierarchy(hits[0].gameObject, data, UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
            Core.Log?.Msg(target != null ? $"[scene] clicked {PathOf(target.transform)}." : "[scene] no click handler above the hit.");
        }

        // The screen, and the size every overlay canvas and the hub page holder report for it.
        private static void Sizes()
        {
            Core.Log?.Msg($"[scene] screen {Screen.width}x{Screen.height}, current resolution {Screen.currentResolution.width}x{Screen.currentResolution.height}, fullscreen {Screen.fullScreen}.");
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (c == null || !c.isRootCanvas) continue;
                var rt = c.GetComponent<RectTransform>();
                Core.Log?.Msg(string.Format(CultureInfo.InvariantCulture, "[scene] canvas '{0}' {1} rect {2:0}x{3:0} scale {4:0.###} pixelRect {5:0}x{6:0}",
                    c.name, c.renderMode, rt.rect.width, rt.rect.height, c.scaleFactor, c.pixelRect.width, c.pixelRect.height));
            }
            var page = FindAny("SideHustle_HubSurface/page");
            if (page == null) { Core.Log?.Warning("[scene] size: the hub page holder was not found."); return; }
            var r = page.GetComponent<RectTransform>().rect;
            Core.Log?.Msg(string.Format(CultureInfo.InvariantCulture, "[scene] hub page holder {0:0}x{1:0}.", r.width, r.height));
        }

        private static void Dump()
        {
            var cam = MenuCamera();
            if (cam != null) Core.Log?.Msg($"[scene] camera {V(cam.transform.position)} euler {V(cam.transform.eulerAngles)} fov {cam.fieldOfView:0.#}");
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                Core.Log?.Msg($"[scene] root '{root.name}' {(root.activeSelf ? "on" : "off")} at {V(root.transform.position)} euler {V(root.transform.eulerAngles)}");
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (l == null) continue;
                Core.Log?.Msg($"[scene] light '{PathOf(l.transform)}' {l.type} {(l.enabled ? "on" : "off")} i={l.intensity:0.##} r={l.range:0.#} " +
                              $"rgb={l.color.r:0.##},{l.color.g:0.##},{l.color.b:0.##} bake={l.bakingOutput.lightmapBakeType} at {V(l.transform.position)}");
            }
        }

        private static void FindLog(string needle)
        {
            int n = 0;
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t == null || t.name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Core.Log?.Msg($"[scene] '{PathOf(t)}' at {V(t.position)} euler {V(t.eulerAngles)} scale {V(t.lossyScale)}");
                if (++n >= 40) { Core.Log?.Msg("[scene] stopped at 40 matches."); break; }
            }
            if (n == 0) Core.Log?.Msg($"[scene] nothing named like '{needle}'.");
        }
#endif
    }
}
