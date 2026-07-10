using System;
using System.Reflection;
using Il2CppSteamworks;   // SteamFriends (Steam overlay web page)
using UnityEngine;         // Application.OpenURL

namespace SideHustle.Menu
{
    /// <summary>
    /// Opens a gamemode's download page for the "not installed" advertisement. The URL is published by whoever
    /// hosts the lobby (untrusted), so it is only ever opened when it points at a known mod host (Thunderstore,
    /// Nexus, GitHub, or DooDesch support); anything else is shown to the player but never auto-opened. Opens in the
    /// Steam in-game overlay browser, falling back to the external browser when the overlay is unavailable.
    /// </summary>
    internal static class DownloadLink
    {
        // Trusted hosts a "Download Mod" link may point at. Matched as an exact host or a subdomain suffix, so
        // e.g. www.nexusmods.com and thunderstore.io both pass.
        private static readonly string[] AllowedHosts =
        {
            "thunderstore.io",
            "nexusmods.com",
            "github.com",
            "doodesch.de",
        };

        /// <summary>True if <paramref name="url"/> is an https link to one of the trusted mod hosts.</summary>
        internal static bool IsAllowed(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps) return false;
            string host = uri.Host;
            foreach (var h in AllowedHosts)
                if (string.Equals(host, h, StringComparison.OrdinalIgnoreCase) ||
                    host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>Open a trusted download URL - Steam overlay first, external browser as a fallback. No-op (with a
        /// warning) when the url is not on the trusted-host allowlist.</summary>
        internal static void Open(string url)
        {
            if (!IsAllowed(url))
            {
                Core.Log?.Warning("[hub] refusing to open a download link that is not on a trusted host: " + url);
                return;
            }
            if (TryOverlay(url)) return;
            try { Application.OpenURL(url); }
            catch (Exception e) { Core.Log?.Warning("[hub] could not open download link: " + e.Message); }
        }

        // Open the URL in the Steam overlay browser. Invoked via reflection because the Il2Cpp binding of
        // SteamFriends.ActivateGameOverlayToWebPage may or may not keep the optional overlay-mode parameter; this
        // binds to whichever arity exists and returns false (falling back to the external browser) on any failure.
        private static bool TryOverlay(string url)
        {
            try
            {
                foreach (var m in typeof(SteamFriends).GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "ActivateGameOverlayToWebPage") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 0 || ps[0].ParameterType != typeof(string)) continue;
                    if (ps.Length == 1) { m.Invoke(null, new object[] { url }); return true; }
                    if (ps.Length == 2)
                    {
                        object mode = Enum.ToObject(ps[1].ParameterType, 0);   // 0 = default overlay mode
                        m.Invoke(null, new object[] { url, mode });
                        return true;
                    }
                }
            }
            catch (Exception e) { Core.Log?.Warning("[hub] Steam overlay web page failed (" + e.Message + "); using external browser."); }
            return false;
        }
    }
}
