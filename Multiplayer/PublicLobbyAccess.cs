using System;
using HarmonyLib;
using Il2CppScheduleOne.Platform;

namespace SideHustle.Multiplayer
{
    /// <summary>
    /// Lets players who are NOT Steam friends of the host join a Side Hustle-hosted lobby.
    ///
    /// Vanilla Schedule I kicks every non-friend: when a joining client sends its name to the host, the server RPC
    /// <c>Player.RpcLogic___SendPlayerNameData</c> asks <c>PlatformFriends.IsLocalPlayerFriendsWith(id)</c> and, for
    /// anyone who is not a friend, calls <c>Owner.Kick("Not friends with host")</c> a few seconds after the connection
    /// is up. That makes public lobbies useless for anyone outside the host's friends list (the connection establishes
    /// over Steam relay, world data even starts streaming, then the host drops it).
    ///
    /// We patch the friend check itself rather than the RPC. That check has exactly one caller in the whole game - the
    /// kick - so forcing it to "yes" while a Side Hustle lobby hosts is precise and touches nothing else. It is also
    /// the durable target: the RPC's name carries a FishNet hash that changes whenever its signature does (it already
    /// broke once, going from <c>_586648380</c> to <c>_1988918489</c> when the id parameter turned into a string),
    /// while <c>IsLocalPlayerFriendsWith</c> is a plain static with a stable name.
    ///
    /// The RPC only executes on the server, so this is host-authoritative and completely inert on clients and outside
    /// a Side Hustle session (gated by <see cref="Active"/>, which only the host sets).
    /// </summary>
    internal static class PublicLobbyAccess
    {
        private static HarmonyLib.Harmony _harmony;
        private static bool _installed;

        /// <summary>True only while this process is hosting a Side Hustle lobby - the friend-check kick is bypassed then.</summary>
        internal static bool Active;

        /// <summary>Install the patch (once) and allow non-friends for the duration of the hosted session.</summary>
        internal static void Enable()
        {
            EnsureInstalled();
            Active = true;
            SetAuthModeAnyone(true);   // 0.4.6f12+: the game's own "anyone may join" mode
        }

        /// <summary>Restore vanilla behaviour (kick non-friends) once the hosted session ends. The patch stays installed but inert.</summary>
        internal static void Disable()
        {
            Active = false;
            SetAuthModeAnyone(false);   // hand the game's own default back
        }

        private static void EnsureInstalled()
        {
            if (_installed) return;
            _installed = true;
            try
            {
                _harmony = new HarmonyLib.Harmony("doodesch.sidehustle.publiclobby");
                var target = AccessTools.Method(typeof(PlatformFriends), nameof(PlatformFriends.IsLocalPlayerFriendsWith));
                if (target != null)
                {
                    _harmony.Patch(target, postfix: new HarmonyMethod(
                        typeof(PublicLobbyAccess).GetMethod(nameof(FriendCheckPostfix), AccessTools.all)));
                    Core.Log?.Msg("[mp] public-lobby access installed (non-friends may join a hosted lobby).");
                }
                else Core.Log?.Warning("[mp] PlatformFriends.IsLocalPlayerFriendsWith not found - the host will keep kicking non-friends.");
            }
            catch (Exception e) { Core.Log?.Warning("[mp] public-lobby patch install failed: " + e.Message); }
        }

        // While a Side Hustle lobby is hosting, everyone counts as a friend - so the kick branch is never taken and the
        // joiner stays connected. Outside a host session the vanilla answer stands untouched.
        private static void FriendCheckPostfix(ref bool __result)
        {
            if (Active) __result = true;
        }

        // ---- 0.4.6f12 and later: the game's own auth mode ----

        /// <summary>
        /// From 0.4.6f12 the friend rule is enforced by a FishNet AUTHENTICATOR instead of a post-connection kick, and
        /// the patch above cannot reach it. The authenticator refuses the connection before a player object exists, and
        /// its strictest mode does not even ask this machine - it broadcasts "is this person a friend of yours?" to the
        /// other clients and accepts their answer, so nothing the host alone returns can satisfy it.
        ///
        /// It also ships the answer: <c>SetAuthMode(ESteamAuthMode.Anyone)</c>. That is a supported mode the game
        /// defines for itself, which is a far better thing to depend on than a bypass - it is what a public lobby means.
        ///
        /// Reached by REFLECTION on purpose. The type does not exist in 0.4.6f11, which is what this mod is compiled
        /// against, so naming it directly would not build today and would hard-fail on f11 tomorrow. Everything here is
        /// therefore best-effort and silent when the type is absent: on f11 the Harmony patch above is the whole story.
        /// </summary>
        // TODO (once 0.4.6f12 is out of beta): replace this reflection with a direct typed call.
        //
        // The type, the public SetAuthMode(ESteamAuthMode) and the Anyone / FriendOfAnyExistingPlayer members were all
        // read off a regenerated IL2CPP f12 interop assembly, so the names below are checked rather than guessed. What
        // reflection buys is only that this still compiles and runs against f11, which is what ships today.
        //
        // When f12 is the released version and the references are updated:
        //   1. reference the type directly and delete ResolveAuthenticator's lookup
        //   2. keep the Harmony patch only if f11 must still be supported; otherwise drop it - the auth mode replaces it
        //   3. re-test with two accounts that are NOT Steam friends, both on f12. That is the one thing neither the
        //      reflection check nor a local emulator run can establish: the strict mode asks the OTHER clients, so a
        //      single machine cannot show that a stranger gets in.
        private const string AuthTypeName = "Il2CppScheduleOne.Networking.FishNetSteamAuthenticator";

        private static object _authInstance;      // cached authenticator, resolved once per session
        private static object _modeAnyone, _modeDefault;
        private static System.Reflection.MethodInfo _setAuthMode;
        private static bool _typeAbsent;     // this build has no authenticator at all - final
        private static bool _shapeChanged;   // it exists but not as we know it - final

        /// <summary>Ask the game to let anyone in (host session) or restore its own default (session over). No-op on any
        /// build that has no authenticator.</summary>
        internal static void SetAuthModeAnyone(bool anyone)
        {
            try
            {
                if (!ResolveAuthenticator()) return;
                object mode = anyone ? _modeAnyone : _modeDefault;
                if (mode == null) return;
                _setAuthMode.Invoke(_authInstance, new[] { mode });
                Core.Log?.Msg($"[mp] steam auth mode -> {(anyone ? "Anyone" : "FriendOfAnyExistingPlayer")} (public-lobby access).");
            }
            catch (Exception e) { Core.Log?.Warning("[mp] could not set the Steam auth mode: " + e.Message); }
        }

        private static bool ResolveAuthenticator()
        {
            if (_authInstance != null && _setAuthMode != null) return true;
            // Only a MISSING TYPE is cached as final. A type that exists but had no instance yet is retried, because
            // the authenticator is a scene component and the first host can easily run before it is there.
            if (_typeAbsent || _shapeChanged) return false;

            Type t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType(AuthTypeName, false); } catch { t = null; }
                if (t != null) break;
            }
            if (t == null)
            {
                if (!_typeAbsent) Core.Log?.Msg("[mp] no Steam authenticator in this build - the friend-check patch covers it.");
                _typeAbsent = true;
                return false;
            }

            _setAuthMode = t.GetMethod("SetAuthMode", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            var enumType = t.GetNestedType("ESteamAuthMode");
            if (_setAuthMode == null || enumType == null)
            {
                Core.Log?.Warning("[mp] the Steam authenticator changed shape - public lobbies may reject non-friends.");
                _shapeChanged = true;
                return false;
            }
            try
            {
                _modeAnyone = Enum.Parse(enumType, "Anyone");
                _modeDefault = Enum.Parse(enumType, "FriendOfAnyExistingPlayer");
            }
            catch { }

            // The authenticator is a component in the scene, so find it the same way anything else would.
            try
            {
                var objs = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.From(t));
                if (objs != null && objs.Length > 0) _authInstance = objs[0];
            }
            catch (Exception e) { Core.Log?.Warning("[mp] authenticator lookup failed: " + e.Message); }

            if (_authInstance == null) Core.Log?.Warning("[mp] the Steam authenticator was not in the scene yet - retrying on the next host.");
            return _authInstance != null && _setAuthMode != null;
        }
    }
}
