using System;
using System.Runtime.InteropServices;

namespace SideHustle.Multiplayer
{
    /// <summary>
    /// Steam P2P networking tuning, applied once before hosting/joining so PUBLIC lobbies work between players who are
    /// NOT Steam friends. Schedule I leaves Steam's ICE candidate policy restricted (the log shows "gathered candidate
    /// type 0x400/0x4, but 0x202 is allowed") - the public-reflexive (STUN) candidates are disallowed, so two
    /// non-friends behind NAT can't punch a direct route: the FishNet world sync never holds and the join times out at
    /// "LoadingData". We widen the GLOBAL policy to allow ALL candidate types and warm up Steam's relay as a fallback
    /// for strict NAT. Friends were unaffected (they already use Steam's authenticated relay).
    ///
    /// The game's managed Steamworks binding has the entire SetGlobalConfigValue* API stripped by IL2CPP dead-code
    /// elimination, so we call the native Steamworks flat API in steam_api64.dll directly (it IS exported there,
    /// verified). steam_api64.dll is already loaded in-process by the game, so the DllImport resolves to that module.
    /// Setting ICE_Enable = All is strictly MORE permissive (it only adds connectivity paths), so it is safe.
    /// </summary>
    internal static class NetworkTuning
    {
        // ESteamNetworkingConfigValue: STUN_ServerList=103, P2P_Transport_ICE_Enable=104, ICE_Penalty=105 (stable across the SDK).
        private const int P2P_Transport_ICE_Enable = 104;
        // k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_All - every candidate type (private + public/STUN + relay).
        private const int ICE_Enable_All = 0x7fffffff;

        private const string Steam = "steam_api64.dll";

        [DllImport(Steam, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SteamAPI_SteamNetworkingUtils_SteamAPI_v004();

        [DllImport(Steam, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(IntPtr self, int eValue, int val);

        [DllImport(Steam, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SteamNetDebugOutput(int nType, IntPtr pszMsg);

        [DllImport(Steam, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SteamAPI_ISteamNetworkingUtils_SetDebugOutputFunction(IntPtr self, int eDetailLevel, IntPtr pfnFunc);

        private static bool _applied;
        private static SteamNetDebugOutput _debugSink;   // kept alive so the native side keeps a valid function pointer

        /// <summary>Globally allow all P2P ICE candidate types + warm up relay so non-friend public joins can connect.
        /// Idempotent; defers (retries on the next host/join) until Steam's networking interface is up.</summary>
        internal static void EnsureIceEnabled()
        {
            if (_applied) return;
            try
            {
                IntPtr utils = SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
                if (utils == IntPtr.Zero)
                {
                    Core.Log?.Warning("[mp] SteamNetworkingUtils not ready yet; P2P ICE tuning deferred.");
                    return;   // not applied -> retried on the next host/join
                }
                bool ok = SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(utils, P2P_Transport_ICE_Enable, ICE_Enable_All);
                try { SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(utils); } catch { }   // relay fallback for strict NAT
                EnableConnectionDebugLog(utils);   // surface WHY a P2P connection drops (otherwise silent)
                _applied = ok;
                Core.Log?.Msg($"[mp] P2P ICE candidates -> allow-all ({(ok ? "ok" : "rejected")}) + relay warmup; enables non-friend NAT traversal.");
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[mp] native P2P ICE tuning failed: " + e.Message);
            }
        }

        /// <summary>Diagnostic: route Steam's networking debug output (connection lifecycle + CLOSE REASONS, which are
        /// otherwise silent) into the log, so a dropped P2P connection tells us WHY. Level Msg = important events + msgs,
        /// not per-packet spam. The delegate is held in a static field so the native function pointer stays valid.</summary>
        private static void EnableConnectionDebugLog(IntPtr utils)
        {
            if (_debugSink != null || utils == IntPtr.Zero) return;
            try
            {
                _debugSink = OnSteamNetDebug;
                const int k_ESteamNetworkingSocketsDebugOutputType_Msg = 5;
                SteamAPI_ISteamNetworkingUtils_SetDebugOutputFunction(
                    utils, k_ESteamNetworkingSocketsDebugOutputType_Msg, Marshal.GetFunctionPointerForDelegate(_debugSink));
                Core.Log?.Msg("[mp] Steam networking debug output enabled (captures connection close reasons).");
            }
            catch (Exception e) { Core.Log?.Warning("[mp] could not enable Steam net debug: " + e.Message); }
        }

        private static void OnSteamNetDebug(int nType, IntPtr pszMsg)
        {
            try { var s = Marshal.PtrToStringAnsi(pszMsg); if (!string.IsNullOrEmpty(s)) Core.Log?.Msg("[steamnet] " + s); }
            catch { }
        }
    }
}
