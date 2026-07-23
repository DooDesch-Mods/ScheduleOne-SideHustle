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

#if DEBUG
        // Dev.SelfTest only: build the profile + UserData clone via the real path but stop before the relaunch,
        // so the MCP dev loop can inspect the artifacts and drive the basedir relaunch itself.
        internal static bool DryRunForTests;
#endif

        /// <summary>True while a gamemode profile is live (the process was launched into an alt base) - leaving restores.</summary>
        internal static bool HasRestorePending => AltBase.IsAltSession();

        /// <summary>Build the gamemode's curated profile and relaunch into it, continuing into the gamemode. An optional
        /// <paramref name="pendingHostPayload"/> (encoded host options) makes the post-relaunch continue host directly.</summary>
        internal static void ApplyPolicyAndRestart(GamemodeDescriptor desc, ModPlan plan, string pendingHostPayload = null)
        {
            var tokens = new System.Collections.Generic.Dictionary<string, string>
            {
                ["PendingContinue"] = desc.Id,
                ["PendingHostOptions"] = pendingHostPayload ?? "",
                ["ActiveAltBase"] = "",   // set inside RelaunchIntoAltBase, where the path is final
                ["ActiveGamemodeId"] = desc.Id,
            };
            RelaunchIntoAltBase(desc.Id, plan.KeepFiles, tokens, prefsOverlay: null,
                $"launching '{desc.DisplayName}' with {plan.KeepFiles.Count} mod(s)");
        }

        /// <summary>
        /// The shared restart-into-a-profile engine (gamemode policy AND lobby sync): build the alt base, clone
        /// UserData with the session tokens - and optionally a host-synced prefs overlay - merged into the CLONED
        /// MelonPreferences.cfg, then relaunch. The live cfg never carries the tokens: a plain launch can therefore
        /// never see stale pending state, and nothing is persisted until every profile artifact exists on disk.
        /// </summary>
        internal static void RelaunchIntoAltBase(string profileId, System.Collections.Generic.IEnumerable<string> keepFiles,
            System.Collections.Generic.Dictionary<string, string> tokens, Func<string, string> prefsOverlay, string logLabel)
        {
            if (_inFlight) { Core.Log?.Msg("[modpolicy] a relaunch is already in progress; ignoring."); return; }
            _inFlight = true;
            try
            {
                string altPath = AltBase.ComputeAltPath(profileId);
                if (altPath == null) { _inFlight = false; Core.Log?.Error("[modpolicy] could not determine a profile path; aborting."); return; }

                if (!AltBase.Build(altPath, keepFiles))
                {
                    _inFlight = false;
                    Core.Log?.Error("[modpolicy] could not build the profile; aborting (your mods are untouched).");
                    return;
                }
                FinishRelaunch(altPath, tokens, prefsOverlay, logLabel);
            }
            catch (Exception e) { _inFlight = false; Core.Log?.Error("[modpolicy] apply failed: " + e); }
        }

        /// <summary>
        /// The lobby-sync variant: the profile's Mods come from EXPLICIT sources (hash-matched real files and
        /// package-cache files that may deliberately DIFFER from a same-named installed mod), not from names in
        /// the real Mods folder.
        /// </summary>
        internal static void RelaunchIntoSyncProfile(string profileKey,
            System.Collections.Generic.IReadOnlyList<Shared.BuildInput> inputs,
            System.Collections.Generic.Dictionary<string, string> tokens, Func<string, string> prefsOverlay, string logLabel)
        {
            if (_inFlight) { Core.Log?.Msg("[modpolicy] a relaunch is already in progress; ignoring."); return; }
            _inFlight = true;
            try
            {
                string altPath = AltBase.ComputeAltPath(profileKey);
                if (altPath == null || !AltBase.BuildSkeleton(altPath))
                {
                    _inFlight = false;
                    Core.Log?.Error("[modpolicy] could not build the sync profile; aborting (your mods are untouched).");
                    return;
                }
                if (!Shared.ProfileBuilder.BuildModsDir(Path.Combine(altPath, "Mods"), inputs,
                        s => Core.Log?.Warning("[modpolicy] " + s)))
                {
                    _inFlight = false;
                    AltBase.Teardown(altPath);
                    Core.Log?.Error("[modpolicy] sync profile is incomplete; aborting (your mods are untouched).");
                    return;
                }
                FinishRelaunch(altPath, tokens, prefsOverlay, logLabel);
            }
            catch (Exception e) { _inFlight = false; Core.Log?.Error("[modpolicy] sync apply failed: " + e); }
        }

        /// <summary>
        /// The NAMED-profile variant: build the profile's FULLY ISOLATED base (its own Mods/Plugins/UserLibs; only the
        /// MelonLoader runtime shared) at its persistent path, then relaunch into it. Unlike the gamemode/sync profiles
        /// this uses a stable per-profile folder (never swept) and carries no session tokens - it is just "boot with
        /// exactly this profile's mods and their plugins/libraries, isolated from the global folders".
        /// </summary>
        internal static void RelaunchIntoNamedProfile(string altPath,
            System.Collections.Generic.IReadOnlyList<Shared.BuildInput> modInputs,
            System.Collections.Generic.IReadOnlyList<Shared.BuildInput> pluginInputs,
            System.Collections.Generic.IReadOnlyList<Shared.BuildInput> userLibInputs, string logLabel)
        {
            if (_inFlight) { Core.Log?.Msg("[profiles] a relaunch is already in progress; ignoring."); return; }
            _inFlight = true;
            try
            {
                if (!AltBase.BuildIsolatedBase(altPath, modInputs, pluginInputs, userLibInputs))
                {
                    _inFlight = false;
                    Core.Log?.Error("[profiles] could not build the profile; aborting (your mods are untouched).");
                    return;
                }
                FinishRelaunch(altPath, new System.Collections.Generic.Dictionary<string, string>(), prefsOverlay: null, logLabel);
            }
            catch (Exception e) { _inFlight = false; Core.Log?.Error("[profiles] named-profile apply failed: " + e); }
        }

        // Shared tail: session tokens + host prefs into the CLONED cfg, then the bat relaunch. The live cfg
        // never carries the tokens, so a plain launch can never see stale pending state.
        private static void FinishRelaunch(string altPath, System.Collections.Generic.Dictionary<string, string> tokens,
            Func<string, string> prefsOverlay, string logLabel)
        {
            tokens ??= new System.Collections.Generic.Dictionary<string, string>();
            tokens["ActiveAltBase"] = altPath;
            string Transform(string cfg)
            {
                string t = prefsOverlay != null ? prefsOverlay(cfg) : cfg;
                return Sync.PrefsSync.MergeKeys(t, Preferences.CategoryId, tokens);
            }
            if (!AltBase.CloneUserData(altPath, Transform))
            {
                _inFlight = false;
                AltBase.Teardown(altPath);
                return;
            }

#if DEBUG
            if (DryRunForTests)
            {
                _inFlight = false;
                Core.Log?.Msg($"[modpolicy] DRY RUN: profile ready at '{altPath}'; skipping the relaunch.");
                return;
            }
#endif
            string bat = WriteRelaunchHelper(altPath);   // altPath != null => relaunch into the profile
            if (!StartHelper(bat)) { _inFlight = false; AltBase.Teardown(altPath); return; }

            Core.Log?.Msg($"[modpolicy] {logLabel}; relaunching.");
            Application.Quit();
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
                Preferences.ActiveGamemodeId = "";
                Core.Log?.Msg("[modpolicy] restoring your full mod set; relaunching.");
                Application.Quit();
            }
            catch (Exception e) { _inFlight = false; Core.Log?.Error("[modpolicy] restore failed: " + e); }
        }

        /// <summary>Plain relaunch (no basedir, no marker changes): used by the named-profile switch, where the
        /// BOOT PLUGIN decides what the next start loads (it consumes profiles.json's pendingSwitch).</summary>
        internal static void RelaunchPlain(string logLabel)
        {
            if (_inFlight) { Core.Log?.Msg("[modpolicy] a relaunch is already in progress; ignoring."); return; }
            _inFlight = true;
            try
            {
                string bat = WriteRelaunchHelper(null);
                if (!StartHelper(bat)) { _inFlight = false; return; }
                Core.Log?.Msg($"[modpolicy] {logLabel}; relaunching.");
                Application.Quit();
            }
            catch (Exception e) { _inFlight = false; Core.Log?.Error("[modpolicy] plain relaunch failed: " + e); }
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
        // EXCEPTION: a Steam-emulated copy (steam_appid.txt next to the exe, e.g. a Goldberg test install) is not
        // known to Steam at all - `-applaunch` would boot the REAL install instead. There the emulator initializes
        // Steamworks for a direct exe start anyway, so the helper relaunches this exe directly.
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
                string basedirArg = altBase == null ? "" : " --melonloader.basedir=\"" + altBase + "\"";
                bool steamEmu = File.Exists(Path.Combine(root, "steam_appid.txt"));

                // Wait for THIS process (by pid) to exit - matching by image name would also see a second running
                // instance (the other player in a local two-instance test) and wait forever.
                int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                var sb = new System.Text.StringBuilder();
                sb.Append("@echo off\r\n:wait\r\n");
                sb.Append("tasklist /FI \"PID eq " + pid + "\" 2>NUL | find \"" + pid + "\" >NUL\r\n");
                sb.Append("if not errorlevel 1 ( timeout /t 1 /nobreak >NUL & goto wait )\r\n");
                if (steamEmu)
                {
                    sb.Append("cd /d \"" + root + "\"\r\n");
                    sb.Append("start \"\" \"" + Path.Combine(root, "Schedule I.exe") + "\"" + basedirArg + "\r\n");
                }
                else
                {
                    string launchArgs = "-applaunch " + SteamAppId + basedirArg;
                    sb.Append("set \"STEAMEXE=\"\r\n");
                    sb.Append("for /f \"tokens=2,*\" %%a in ('reg query \"HKCU\\Software\\Valve\\Steam\" /v SteamExe 2^>NUL ^| findstr /i \"SteamExe\"') do set \"STEAMEXE=%%b\"\r\n");
                    sb.Append("if not exist \"%STEAMEXE%\" if exist \"%ProgramFiles(x86)%\\Steam\\steam.exe\" set \"STEAMEXE=%ProgramFiles(x86)%\\Steam\\steam.exe\"\r\n");
                    sb.Append("if not exist \"%STEAMEXE%\" if exist \"%ProgramFiles%\\Steam\\steam.exe\" set \"STEAMEXE=%ProgramFiles%\\Steam\\steam.exe\"\r\n");
                    sb.Append("if exist \"%STEAMEXE%\" ( start \"\" \"%STEAMEXE%\" " + launchArgs + " ) else ( start \"\" \"steam://rungameid/" + SteamAppId + "\" )\r\n");
                }
                File.WriteAllText(bat, sb.ToString());
                return bat;
            }
            catch (Exception e) { Core.Log?.Error("[modpolicy] write helper failed: " + e); return null; }
        }
    }
}
