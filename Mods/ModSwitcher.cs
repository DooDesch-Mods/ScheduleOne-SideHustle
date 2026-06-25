using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SideHustle.Config;
using UnityEngine;

namespace SideHustle.Mods
{
    /// <summary>
    /// Applies a mod plan and restarts the game. MelonLoader cannot unload a mod at runtime, so the actual DLL
    /// renames are done by a tiny helper script in the brief window after the game has closed, which then relaunches
    /// via Steam. Side Hustle remembers the gamemode to continue into and the inverse ("restore") operations so it
    /// can put the player's mods back when the gamemode ends.
    /// </summary>
    internal static class ModSwitcher
    {
        private const ulong SteamAppId = 3164500;

        private struct Op { public string File; public bool Disable; }

        // Set once a restart is committed; a second click (the menu keeps ticking while the process quits) is ignored.
        private static bool _inFlight;

        /// <summary>True while a mod policy is in effect (there is something to restore).</summary>
        internal static bool HasRestorePending => !string.IsNullOrEmpty(Preferences.RestoreModOps);

        /// <summary>Apply a gamemode's plan (disable/enable), remember how to restore, and restart into the gamemode.</summary>
        internal static void ApplyPolicyAndRestart(GamemodeDescriptor desc, ModPlan plan)
        {
            if (_inFlight) { Core.Log?.Msg("[modpolicy] a restart is already in progress; ignoring."); return; }

            var apply = new List<Op>();
            foreach (var f in plan.ToDisable) apply.Add(new Op { File = f, Disable = true });
            foreach (var f in plan.ToEnable) apply.Add(new Op { File = f, Disable = false });

            // the inverse, so we can restore the original set when the gamemode ends
            var restore = new List<Op>();
            foreach (var f in plan.ToDisable) restore.Add(new Op { File = f, Disable = false });
            foreach (var f in plan.ToEnable) restore.Add(new Op { File = f, Disable = true });

            DoSwitch(apply,
                () => { Preferences.PendingContinue = desc.Id; Preferences.RestoreModOps = Serialize(restore); },
                $"applying policy for '{desc.DisplayName}' (disable {plan.ToDisable.Count}, enable {plan.ToEnable.Count})");
        }

        /// <summary>Put the player's mods back the way they were and restart to the normal menu.</summary>
        internal static void RestoreAndRestart()
        {
            if (_inFlight) { Core.Log?.Msg("[modpolicy] a restart is already in progress; ignoring."); return; }
            var ops = Deserialize(Preferences.RestoreModOps);
            if (ops.Count == 0) { Core.Log?.Warning("[modpolicy] nothing to restore."); return; }
            DoSwitch(ops,
                () => { Preferences.RestoreModOps = ""; Preferences.PendingContinue = ""; },
                $"restoring {ops.Count} mod(s)");
        }

        // Write the helper and launch it; ONLY after a confirmed launch do we persist the prefs and quit, so a failed
        // launch never leaves the player with stale Pending/Restore state and no restart.
        private static void DoSwitch(List<Op> ops, Action persist, string what)
        {
            _inFlight = true;
            try
            {
                string bat = WriteHelper(ops);
                if (bat == null) { _inFlight = false; Core.Log?.Error("[modpolicy] could not write the restart helper; aborting."); return; }

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = bat,
                    UseShellExecute = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) { _inFlight = false; Core.Log?.Error("[modpolicy] could not start the restart helper; aborting."); return; }

                persist();
                Core.Log?.Msg("[modpolicy] " + what + "; restarting.");
                Application.Quit();
            }
            catch (Exception e) { _inFlight = false; Core.Log?.Error("[modpolicy] restart failed: " + e); }
        }

        private static string WriteHelper(List<Op> ops)
        {
            try
            {
                string mods = ModInventory.ModsPath();
                string root = ModInventory.GameRoot();
                if (mods == null || root == null) return null;

                string dir = Path.Combine(root, "UserData", "SideHustle");
                Directory.CreateDirectory(dir);
                string bat = Path.Combine(dir, "modswitch.bat");

                var sb = new System.Text.StringBuilder();
                sb.Append("@echo off\r\n:wait\r\n");
                sb.Append("tasklist /FI \"IMAGENAME eq Schedule I.exe\" 2>NUL | find /I \"Schedule I.exe\" >NUL\r\n");
                sb.Append("if not errorlevel 1 ( timeout /t 1 /nobreak >NUL & goto wait )\r\n");
                foreach (var op in ops)
                {
                    string src = Path.Combine(mods, op.Disable ? op.File : op.File + ".disabled");
                    string dst = Path.Combine(mods, op.Disable ? op.File + ".disabled" : op.File);
                    // Idempotent: act only if the source exists; "move /Y" overwrites a stray destination left by a
                    // partial prior run, so the enable/disable state is never left ambiguous (both files present).
                    sb.Append("if exist \"" + src + "\" move /Y \"" + src + "\" \"" + dst + "\" >NUL 2>NUL\r\n");
                }
                sb.Append("start \"\" \"steam://rungameid/" + SteamAppId + "\"\r\n");
                File.WriteAllText(bat, sb.ToString());
                return bat;
            }
            catch (Exception e) { Core.Log?.Error("[modpolicy] write helper failed: " + e); return null; }
        }

        // Records use '|' and fields use ':' - both illegal in Windows file names, so a DLL name can never collide.
        private static string Serialize(List<Op> ops) => string.Join("|", ops.Select(o => o.File + ":" + (o.Disable ? "D" : "E")));

        private static List<Op> Deserialize(string s)
        {
            var list = new List<Op>();
            if (string.IsNullOrEmpty(s)) return list;
            foreach (var part in s.Split('|'))
            {
                int i = part.LastIndexOf(':');
                if (i > 0) list.Add(new Op { File = part.Substring(0, i), Disable = part.Substring(i + 1) == "D" });
            }
            return list;
        }
    }
}
