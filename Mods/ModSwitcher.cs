using System;
using System.IO;
using SideHustle.Config;
using UnityEngine;

namespace SideHustle.Mods
{
    /// <summary>
    /// Runs a gamemode with only its allowed mods by relaunching the game into a temporary, isolated MelonLoader
    /// profile (an alternate base directory) that contains hardlinks to just those mods - the player's real Mods
    /// folder is never renamed, moved or deleted, so mod managers stay in sync. Leaving the gamemode relaunches
    /// normally (the full mod set) and the temporary profile is cleaned up. A plain Steam launch always loads the
    /// full set, so the player can never get stuck in the restricted set.
    /// </summary>
    internal static class ModSwitcher
    {
        private const ulong SteamAppId = 3164500;

        // Set once a relaunch is committed; a second click (the menu keeps ticking while the process quits) is ignored.
        private static bool _inFlight;

        /// <summary>True while a gamemode profile is live (the process was launched into an alt base) - leaving restores.</summary>
        internal static bool HasRestorePending => AltBase.IsAltSession();

        /// <summary>Build the gamemode's curated profile and relaunch into it, continuing into the gamemode. An optional
        /// <paramref name="pendingHostPayload"/> (encoded host options) makes the post-relaunch continue host directly.</summary>
        internal static void ApplyPolicyAndRestart(GamemodeDescriptor desc, ModPlan plan, string pendingHostPayload = null)
        {
            if (_inFlight) { Core.Log?.Msg("[modpolicy] a relaunch is already in progress; ignoring."); return; }
            _inFlight = true;
            try
            {
                string altPath = AltBase.ComputeAltPath(desc.Id);
                if (altPath == null) { _inFlight = false; Core.Log?.Error("[modpolicy] could not determine a profile path; aborting."); return; }

                if (!AltBase.Build(altPath, plan.KeepFiles))
                {
                    _inFlight = false;
                    Core.Log?.Error("[modpolicy] could not build the gamemode profile; aborting (your mods are untouched).");
                    return;
                }

                string bat = WriteRelaunchHelper(altPath);   // altPath != null => relaunch into the profile
                if (!StartHelper(bat)) { _inFlight = false; AltBase.Teardown(altPath); return; }

                // Persist only after a confirmed relaunch, so a failure never leaves stale state with no restart.
                Preferences.PendingContinue = desc.Id;
                Preferences.PendingHostOptions = pendingHostPayload ?? "";
                Preferences.ActiveAltBase = altPath;
                Core.Log?.Msg($"[modpolicy] launching '{desc.DisplayName}' with {plan.KeepFiles.Count} mod(s); relaunching.");
                Application.Quit();
            }
            catch (Exception e) { _inFlight = false; Core.Log?.Error("[modpolicy] apply failed: " + e); }
        }

        /// <summary>Relaunch normally (the player's full mod set) and leave the temporary profile for cleanup.</summary>
        internal static void RestoreAndRestart()
        {
            if (_inFlight) { Core.Log?.Msg("[modpolicy] a relaunch is already in progress; ignoring."); return; }
            _inFlight = true;
            try
            {
                string bat = WriteRelaunchHelper(null);      // null => plain relaunch, no basedir arg
                if (!StartHelper(bat)) { _inFlight = false; return; }

                // Clear the session markers; the leftover profile folder is swept by the next normal launch.
                Preferences.PendingContinue = "";
                Preferences.ActiveAltBase = "";
                Core.Log?.Msg("[modpolicy] restoring your full mod set; relaunching.");
                Application.Quit();
            }
            catch (Exception e) { _inFlight = false; Core.Log?.Error("[modpolicy] restore failed: " + e); }
        }

        private static bool StartHelper(string bat)
        {
            if (bat == null) { Core.Log?.Error("[modpolicy] could not write the relaunch helper; aborting."); return false; }
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = bat,
                    UseShellExecute = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                if (System.Diagnostics.Process.Start(psi) != null) return true;
                Core.Log?.Error("[modpolicy] could not start the relaunch helper; aborting.");
                return false;
            }
            catch (Exception e) { Core.Log?.Error("[modpolicy] start helper failed: " + e); return false; }
        }

        // A tiny batch helper: wait for this game process to exit, then relaunch through Steam (`steam -applaunch`).
        // Going through Steam is required so the game initializes Steamworks; a direct exe launch leaves Steamworks
        // uninitialized and breaks multiplayer lobby creation. The steam:// URL drops appended arguments, so
        // -applaunch carries --melonloader.basedir. A profile loads only its mods; no profile loads the full set.
        private static string WriteRelaunchHelper(string altBase)
        {
            try
            {
                string root = ModInventory.GameRoot();
                if (root == null) return null;
                string dir = Path.Combine(root, "UserData", "SideHustle");
                Directory.CreateDirectory(dir);
                string bat = Path.Combine(dir, "sh_relaunch.bat");

                // The profile path lives in the game folder and can contain spaces, so the value is quoted.
                string launchArgs = "-applaunch " + SteamAppId + (altBase == null ? "" : " --melonloader.basedir=\"" + altBase + "\"");

                var sb = new System.Text.StringBuilder();
                sb.Append("@echo off\r\n:wait\r\n");
                sb.Append("tasklist /FI \"IMAGENAME eq Schedule I.exe\" 2>NUL | find /I \"Schedule I.exe\" >NUL\r\n");
                sb.Append("if not errorlevel 1 ( timeout /t 1 /nobreak >NUL & goto wait )\r\n");
                sb.Append("set \"STEAMEXE=\"\r\n");
                sb.Append("for /f \"tokens=2,*\" %%a in ('reg query \"HKCU\\Software\\Valve\\Steam\" /v SteamExe 2^>NUL ^| findstr /i \"SteamExe\"') do set \"STEAMEXE=%%b\"\r\n");
                sb.Append("if not exist \"%STEAMEXE%\" if exist \"%ProgramFiles(x86)%\\Steam\\steam.exe\" set \"STEAMEXE=%ProgramFiles(x86)%\\Steam\\steam.exe\"\r\n");
                sb.Append("if not exist \"%STEAMEXE%\" if exist \"%ProgramFiles%\\Steam\\steam.exe\" set \"STEAMEXE=%ProgramFiles%\\Steam\\steam.exe\"\r\n");
                sb.Append("if exist \"%STEAMEXE%\" ( start \"\" \"%STEAMEXE%\" " + launchArgs + " ) else ( start \"\" \"steam://rungameid/" + SteamAppId + "\" )\r\n");
                File.WriteAllText(bat, sb.ToString());
                return bat;
            }
            catch (Exception e) { Core.Log?.Error("[modpolicy] write helper failed: " + e); return null; }
        }
    }
}
