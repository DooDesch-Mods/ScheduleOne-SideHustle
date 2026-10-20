using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SideHustle.Internal;

namespace SideHustle
{
    /// <summary>
    /// Public, stable entry point for mods that want to appear as a gamemode in Side Hustle's main-menu list.
    /// Register early (e.g. in your mod's OnInitializeMelon). Registration is load-order independent, so it
    /// does not matter whether Side Hustle or your mod loads first. Reference SideHustle.dll and declare
    /// <c>[assembly: MelonOptionalDependencies("SideHustle")]</c> so the hub loads first and your mod still
    /// loads cleanly if it is absent.
    /// </summary>
    /// <remarks>
    /// A gamemode id is the <see cref="GamemodeDescriptor.Id"/> you registered, e.g. "doodesch.example". Ids are
    /// compared exactly, case included. A Steam id is the id64 number, the same value as
    /// <see cref="SessionPlayer.SteamId"/>.
    /// </remarks>
    /// <example>
    /// A singleplayer menu gamemode that shows a notice and returns to the hub:
    /// <code>
    /// API.Register(new GamemodeDescriptor
    /// {
    ///     Id = "you.yourmode",
    ///     DisplayName = "Your Mode",
    ///     Description = "One line under the name.",
    ///     Support = GamemodeSupport.Singleplayer,
    ///     Surface = GamemodeSurface.MenuSpace,
    ///     OnLaunchSingleplayer = ctx =&gt; { API.ShowNotice("Started."); ctx.ReturnToHub(); }
    /// });
    /// </code>
    /// </example>
    public static class API
    {
        // --- Version and feature detection -------------------------------------------------------------------

        /// <summary>
        /// The version of this API surface. Rule: it goes up by one with every release that ADDS public API, and
        /// never for a fix. 1 = the 1.x hub (Register, descriptor basics), 2 = the 2.x additions (host settings,
        /// presets, mod policy, world hygiene flags, KickPlayer), 3 = Side Hustle 3.0.0 (validation, lifecycle
        /// events, players, notices, gamemode messages, <see cref="Has"/>).
        ///
        /// A property, not a const: a const is copied into the consumer at ITS compile time and would report the
        /// version the consumer was built against instead of the one running. Reading this on a Side Hustle older
        /// than 3.0.0 throws <see cref="MissingMethodException"/>; the copy-in <c>SideHustleLink</c> shim reads it by
        /// reflection and returns 0 there.
        /// </summary>
        public static int ApiVersion => CurrentApiVersion;

        private const int CurrentApiVersion = 3;

        /// <summary>
        /// True when this Side Hustle supports the named feature. Use the constants on <see cref="ApiFeatures"/>;
        /// matching is case-insensitive and an unknown name is false. Exists since 3.0.0 (API version 3): on an older
        /// Side Hustle the method itself is missing, so call it through the <c>SideHustleLink</c> shim or guard it.
        /// </summary>
        /// <param name="feature">A constant from <see cref="ApiFeatures"/>, e.g. <c>ApiFeatures.Messages</c> ("messages").
        /// Leading and trailing spaces are ignored. Null or empty is false.</param>
        public static bool Has(string feature) => ApiFeatures.IsKnown(feature);

        // --- Registration ------------------------------------------------------------------------------------

        /// <summary>
        /// Register (or replace, by <see cref="GamemodeDescriptor.Id"/>) a gamemode. Replacing an existing id
        /// is allowed and makes hot-reload / double-registration safe. Since 3.0.0 the descriptor is checked here
        /// and every problem is logged with the gamemode id and the field name; nothing is rejected that worked
        /// before. A second registration of the same id from a DIFFERENT mod is logged as a warning and still
        /// replaces the first.
        /// </summary>
        /// <param name="descriptor">The gamemode to list. Null throws <see cref="ArgumentNullException"/>. An empty
        /// <see cref="GamemodeDescriptor.Id"/> throws <see cref="ArgumentException"/>. An empty
        /// <see cref="GamemodeDescriptor.DisplayName"/> is set to the id.</param>
        // NoInlining keeps GetCallingAssembly below pointing at the consumer instead of whatever inlined this.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Register(GamemodeDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (string.IsNullOrWhiteSpace(descriptor.Id)) throw new ArgumentException("GamemodeDescriptor.Id is required.", nameof(descriptor));
            if (string.IsNullOrWhiteSpace(descriptor.DisplayName))
            {
                descriptor.DisplayName = descriptor.Id;
                Core.Log?.Msg($"[api] gamemode '{descriptor.Id}' DisplayName: empty - the list shows the id instead.");
            }

            // Remember the registering mod so a mod policy never disables the gamemode's own DLL. The launch
            // callbacks live in the gamemode's own assembly, which is reliable even when registration is routed
            // through a shared helper assembly (where GetCallingAssembly would point at the wrong DLL).
            if (descriptor.OwnerAssembly == null)
            {
                try
                {
                    var cb = descriptor.OnLaunchSingleplayer ?? descriptor.OnHostMultiplayer
                             ?? descriptor.OnJoinMultiplayer ?? descriptor.OnExitToHub;
                    descriptor.OwnerAssembly = cb?.Method?.DeclaringType?.Assembly
                                               ?? System.Reflection.Assembly.GetCallingAssembly();
                }
                catch (Exception e)
                {
                    Core.Log?.Warning($"[api] gamemode '{descriptor.Id}': could not read the owner from its callbacks ({e.Message}); using the calling assembly.");
                    try { descriptor.OwnerAssembly = System.Reflection.Assembly.GetCallingAssembly(); }
                    catch (Exception e2)
                    {
                        Core.Log?.Error($"[api] gamemode '{descriptor.Id}': owner assembly unknown ({e2.Message}). A mod policy cannot protect its DLL and the build id stays empty.");
                    }
                }
            }

            DescriptorValidator.CheckOwner(descriptor, Registry.Find(descriptor.Id));
            DescriptorValidator.Validate(descriptor);

            bool replaced = Registry.Register(descriptor);
            Core.Log?.Msg(replaced
                ? $"Gamemode re-registered: '{descriptor.DisplayName}' ({descriptor.Id})."
                : $"Gamemode registered: '{descriptor.DisplayName}' ({descriptor.Id}).");
        }

        /// <summary>Remove a previously registered gamemode. Returns true if one was removed.</summary>
        /// <param name="id">The <see cref="GamemodeDescriptor.Id"/> it registered with, e.g. "doodesch.example". Matched
        /// exactly, case included. Null or empty returns false.</param>
        public static bool Unregister(string id) => !string.IsNullOrWhiteSpace(id) && Registry.Unregister(id);

        /// <summary>Host-only: kick the connected player with this Steam id (id64) from the current session. A no-op on
        /// a client or if no such player is connected, and never kicks the host itself. Returns true if a player was
        /// kicked. Reusable by any gamemode that manages a Side Hustle lobby.</summary>
        /// <param name="steamId64">The player's Steam id64, the same value as <see cref="SessionPlayer.SteamId"/>. It is
        /// matched against the player's <c>Player.PlayerCode</c>.</param>
        /// <param name="reason">The reason passed to the kick and written to the log. Null or empty uses
        /// "Kicked by host".</param>
        public static bool KickPlayer(ulong steamId64, string reason = "Kicked by host")
            => Multiplayer.HostControls.KickBySteamId(steamId64, reason);

        /// <summary>True once Side Hustle's own OnInitializeMelon has run. <see cref="Ready"/> fires at the same
        /// moment.</summary>
        public static bool IsReady
        {
            get => _ready;
            internal set
            {
                if (!value || _ready) { _ready = value; return; }
                _ready = true;
                ApiPump.CaptureMainThread();
                SessionEvents.RaiseReady();
            }
        }

        private static bool _ready;

        /// <summary>A snapshot of the registered gamemodes. Since 3.0.0 this is a copy: registering or removing a
        /// gamemode later does not change a list you already hold.</summary>
        public static IReadOnlyList<GamemodeDescriptor> Registered => Registry.Snapshot();

        // --- Lifecycle (since 3.0.0) -------------------------------------------------------------------------

        /// <summary>
        /// Fires once, right after Side Hustle's own OnInitializeMelon. A handler added after that does NOT run:
        /// check <see cref="IsReady"/> first and act directly when it is already true. Since 3.0.0.
        /// </summary>
        public static event Action Ready
        {
            add => SessionEvents.Ready.Add(value);
            remove => SessionEvents.Ready.Remove(value);
        }

        /// <summary>
        /// Fires after a gamemode's launch callback (OnLaunchSingleplayer / OnHostMultiplayer / OnJoinMultiplayer)
        /// returned without ending the session. <see cref="CurrentSession"/> and <see cref="Players"/> are already
        /// set when it runs. Fires for every gamemode, not only yours - compare <c>ctx.Descriptor.Id</c>. Since 3.0.0.
        /// </summary>
        public static event Action<LaunchContext> SessionStarted
        {
            add => SessionEvents.Started.Add(value);
            remove => SessionEvents.Started.Remove(value);
        }

        /// <summary>
        /// Fires once per started session, right after the gamemode's OnExitToHub, with the gamemode id. Covers a
        /// normal ReturnToHub, a mod-policy restart and an aborted session (lost world, kicked client). Since 3.0.0.
        /// </summary>
        public static event Action<string> SessionEnded
        {
            add => SessionEvents.Ended.Add(value);
            remove => SessionEvents.Ended.Remove(value);
        }

        /// <summary>
        /// A player entered the current session's Steam lobby. Polled about twice a second from the lobby member
        /// list, so it works for World and MenuSpace sessions alike; in a World session it fires before the
        /// joiner's character has spawned. Players already present at <see cref="SessionStarted"/> are in
        /// <see cref="Players"/> and do not raise it. Since 3.0.0.
        /// </summary>
        public static event Action<SessionPlayer> PlayerJoined
        {
            add => SessionEvents.Joined.Add(value);
            remove => SessionEvents.Joined.Remove(value);
        }

        /// <summary>
        /// A player left the current session's Steam lobby (quit, dropped or kicked). Not raised for the remaining
        /// players when the session itself ends - <see cref="SessionEnded"/> covers that. Since 3.0.0.
        /// </summary>
        public static event Action<SessionPlayer> PlayerLeft
        {
            add => SessionEvents.Left.Add(value);
            remove => SessionEvents.Left.Remove(value);
        }

        /// <summary>The running gamemode session, or null. Set just before <see cref="SessionStarted"/> and cleared
        /// just before <see cref="SessionEnded"/>. Since 3.0.0.</summary>
        public static LaunchContext CurrentSession => SessionEvents.Current;

        /// <summary>
        /// The players in the current session as a snapshot copy (empty outside a session). A singleplayer session
        /// holds only the local player. Display names follow the session alias where one is known. Since 3.0.0.
        /// </summary>
        public static IReadOnlyList<SessionPlayer> Players => PlayerTracker.Snapshot();

        // --- Notices (since 3.0.0) ---------------------------------------------------------------------------

        /// <summary>
        /// Show a short notice to the local player. In the world it is the game's own notification popup (titled
        /// with the running gamemode's name) and stays <paramref name="seconds"/>; in the menu it is the hub's toast,
        /// whose duration is fixed. Safe from any thread: calls off the main thread are queued to the next frame.
        /// Since 3.0.0.
        /// </summary>
        /// <param name="text">The notice text, e.g. "Round starts in 10 seconds".</param>
        /// <param name="seconds">How long the world popup stays, in seconds. 0, a negative value or NaN uses 4. The menu
        /// toast ignores it.</param>
        public static void ShowNotice(string text, float seconds = 4f) => Notices.Show(text, seconds);

        // --- Gamemode messages (since 3.0.0) -----------------------------------------------------------------

        /// <summary>
        /// Send bytes to every other player in the current session (not to yourself). Reliable, over Steam P2P on
        /// Side Hustle's own channel; at most <see cref="MaxMessageBytes"/> bytes. Returns false, with a log line,
        /// when there is no session lobby or the message is refused. Off the main thread it is queued and returns
        /// true. Since 3.0.0.
        /// </summary>
        /// <param name="gamemodeId">Your <see cref="GamemodeDescriptor.Id"/>, e.g. "doodesch.example". Required. At most
        /// 64 bytes as UTF-8.</param>
        /// <param name="channel">Your own message name, e.g. "ping" or "state". Required. At most 64 bytes as UTF-8.</param>
        /// <param name="payload">The bytes to send, at most <see cref="MaxMessageBytes"/> (8192). Null sends an empty
        /// payload.</param>
        /// <returns>True when the message went out or was queued from another thread. False when it was refused; the
        /// log line says why.</returns>
        public static bool SendToAll(string gamemodeId, string channel, byte[] payload)
            => GamemodeChannel.Send(0UL, gamemodeId, channel, payload, false);

        /// <summary>Send UTF-8 text to every other player in the current session. Same rules as the byte overload.
        /// Since 3.0.0.</summary>
        /// <param name="gamemodeId">Your <see cref="GamemodeDescriptor.Id"/>. Required. At most 64 bytes as UTF-8.</param>
        /// <param name="channel">Your own message name, e.g. "ping". Required. At most 64 bytes as UTF-8.</param>
        /// <param name="text">The text, sent as UTF-8. The encoded bytes count against <see cref="MaxMessageBytes"/>.
        /// Null sends an empty text.</param>
        public static bool SendToAll(string gamemodeId, string channel, string text)
            => GamemodeChannel.Send(0UL, gamemodeId, channel, GamemodeChannel.Utf8(text), true);

        /// <summary>Send bytes to one player of the current session by Steam id. The target must be in the session's
        /// lobby; sending to yourself is refused. Same rules as <see cref="SendToAll(string, string, byte[])"/>.
        /// Since 3.0.0.</summary>
        /// <param name="steamId">The target's <see cref="SessionPlayer.SteamId"/>, e.g. <see cref="SessionMessage.SenderSteamId"/>
        /// to answer a message.</param>
        /// <param name="gamemodeId">Your <see cref="GamemodeDescriptor.Id"/>. Required. At most 64 bytes as UTF-8.</param>
        /// <param name="channel">Your own message name, e.g. "pong". Required. At most 64 bytes as UTF-8.</param>
        /// <param name="payload">The bytes to send, at most <see cref="MaxMessageBytes"/> (8192). Null sends an empty
        /// payload.</param>
        public static bool SendTo(ulong steamId, string gamemodeId, string channel, byte[] payload)
            => GamemodeChannel.Send(steamId, gamemodeId, channel, payload, false);

        /// <summary>Send UTF-8 text to one player of the current session. Same rules as the byte overload.
        /// Since 3.0.0.</summary>
        /// <param name="steamId">The target's <see cref="SessionPlayer.SteamId"/>.</param>
        /// <param name="gamemodeId">Your <see cref="GamemodeDescriptor.Id"/>. Required. At most 64 bytes as UTF-8.</param>
        /// <param name="channel">Your own message name, e.g. "pong". Required. At most 64 bytes as UTF-8.</param>
        /// <param name="text">The text, sent as UTF-8. The encoded bytes count against <see cref="MaxMessageBytes"/>.
        /// Null sends an empty text.</param>
        public static bool SendTo(ulong steamId, string gamemodeId, string channel, string text)
            => GamemodeChannel.Send(steamId, gamemodeId, channel, GamemodeChannel.Utf8(text), true);

        /// <summary>
        /// Receive the messages sent for <paramref name="gamemodeId"/> (any channel - read
        /// <see cref="SessionMessage.Channel"/>). Handlers run on the main thread. Only packets from members of the
        /// current session's lobby are delivered; the sender id comes from Steam, never from the payload.
        /// Since 3.0.0.
        /// </summary>
        /// <param name="gamemodeId">Your <see cref="GamemodeDescriptor.Id"/>, the same id the senders pass. Matched exactly,
        /// case included. Null or empty is ignored with a warning.</param>
        /// <param name="handler">Called once per received message. Null is ignored with a warning.</param>
        /// <example>
        /// Answer a "ping" with a "pong" to the sender:
        /// <code>
        /// API.OnMessage("you.yourmode", m =&gt;
        /// {
        ///     if (m.Channel == "ping") API.SendTo(m.SenderSteamId, "you.yourmode", "pong", "hi " + m.Text);
        /// });
        /// API.SendToAll("you.yourmode", "ping", "hello");
        /// </code>
        /// </example>
        public static void OnMessage(string gamemodeId, Action<SessionMessage> handler) => GamemodeChannel.Subscribe(gamemodeId, handler);

        /// <summary>Remove a handler added with <see cref="OnMessage"/>. Returns true if it was registered.
        /// Since 3.0.0.</summary>
        /// <param name="gamemodeId">The id the handler was added with.</param>
        /// <param name="handler">The same delegate instance passed to <see cref="OnMessage"/>.</param>
        public static bool OffMessage(string gamemodeId, Action<SessionMessage> handler) => GamemodeChannel.Unsubscribe(gamemodeId, handler);

        /// <summary>The largest payload <see cref="SendToAll(string, string, byte[])"/> and
        /// <see cref="SendTo(ulong, string, string, byte[])"/> accept, in bytes. Since 3.0.0.</summary>
        /// <value>8192 (8 KB).</value>
        public static int MaxMessageBytes => GamemodeChannel.MaxPayloadBytes;
    }
}
