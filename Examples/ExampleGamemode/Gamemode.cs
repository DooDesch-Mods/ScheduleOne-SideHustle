using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using SideHustle;
using UnityEngine;

namespace ExampleGamemode
{
    /// <summary>
    /// Everything that touches SideHustle types. Only reached through the SideHustleLink lambdas in Core.
    ///
    /// The gamemode itself is deliberately tiny: a MenuSpace session with no UI of its own that shows a notice,
    /// pings the other players, and returns to the hub when its timer runs out. Real gamemodes build their overlay
    /// (MenuSpace) or run in the world (World) where this one only waits.
    /// </summary>
    internal static class Gamemode
    {
        internal const string Id = "doodesch.example";

        internal static GamemodeDescriptor Build()
        {
            return new GamemodeDescriptor
            {
                Id = Id,
                DisplayName = "Example",
                Description = "The Side Hustle API example gamemode.",
                Author = "DooDesch",
                Support = GamemodeSupport.Hybrid,
                Surface = GamemodeSurface.MenuSpace,
                Advertise = false,   // an example should not show up in other players' discovery list

                OnLaunchSingleplayer = Start,
                OnHostMultiplayer = Start,
                OnJoinMultiplayer = Start,
                OnExitToHub = Stop,

                // One setting of each type. Keys are short: the chosen values ride on the Steam lobby.
                HostSettings = new[]
                {
                    new SettingDescriptor { Key = "len", Label = "Session length", Category = "Match", Type = SettingType.Slider,
                                            Min = 10, Max = 120, Step = 10, WholeNumbers = true, Default = "30", Unit = "s" },
                    new SettingDescriptor { Key = "ping", Label = "Ping on start", Category = "Match", Type = SettingType.Toggle, Default = "1" },
                    new SettingDescriptor { Key = "pace", Label = "Pace", Category = "Feel", Type = SettingType.Segmented,
                                            Options = new[] { "Slow", "Normal", "Fast" }, Values = new[] { "s", "n", "f" }, Default = "n" },
                    new SettingDescriptor { Key = "area", Label = "Area", Category = "Feel", Type = SettingType.Dropdown,
                                            Options = new[] { "Docks", "Downtown", "Suburbia" }, Values = new[] { "docks", "town", "burbs" }, Default = "town" },
                    new SettingDescriptor { Key = "motd", Label = "Welcome text", Category = "Feel", Type = SettingType.Text, Default = "Have fun" }
                },

                Presets = new[]
                {
                    new SettingPreset
                    {
                        Name = "Quick",
                        Hint = "Ten seconds, fast pace.",
                        Recommended = "Best for 2-4",
                        MinPlayers = 2, MaxPlayers = 4,
                        Values = new Dictionary<string, string> { { "len", "10" }, { "pace", "f" } }
                    }
                }
            };
        }

        internal static void Subscribe()
        {
            API.SessionStarted += OnSessionStarted;
            API.SessionEnded += id => { if (id == Id) Core.Log.Msg("session ended."); };
            API.PlayerJoined += p => { if (IsOurs()) API.ShowNotice(p.DisplayName + " joined."); };
            API.PlayerLeft += p => { if (IsOurs()) API.ShowNotice(p.DisplayName + " left."); };
            API.OnMessage(Id, OnMessage);
            Core.Log.Msg("lifecycle events and messages subscribed.");
        }

        private static bool IsOurs() => API.CurrentSession?.Descriptor?.Id == Id;

        // --- launch ------------------------------------------------------------------------------------------

        private static object _timer;

        private static void Start(LaunchContext ctx)
        {
            // Settings come back through MultiplayerInfo. Singleplayer has no MultiplayerInfo, so use the defaults.
            var mp = ctx.Multiplayer;
            int seconds = mp?.GetInt("len", 30) ?? 30;
            string pace = mp?.GetSetting("pace", "n") ?? "n";
            string area = mp?.GetSetting("area", "town") ?? "town";
            Core.Log.Msg($"start as {ctx.Role}: {seconds}s, pace {pace}, area {area}, welcome \"{mp?.GetSetting("motd", "") ?? ""}\".");

            _timer = MelonCoroutines.Start(EndAfter(ctx, seconds));
        }

        // Runs after Start returned, so CurrentSession and Players are set and messages can go out.
        private static void OnSessionStarted(LaunchContext ctx)
        {
            if (ctx.Descriptor?.Id != Id) return;   // another gamemode's session
            API.ShowNotice($"Example running with {API.Players.Count} player(s).");

            bool ping = ctx.Multiplayer?.GetBool("ping", true) ?? false;
            if (ctx.Role != SessionRole.Singleplayer && ping)
                API.SendToAll(Id, "ping", "hello from " + LocalName());
        }

        private static IEnumerator EndAfter(LaunchContext ctx, int seconds)
        {
            float end = Time.unscaledTime + seconds;
            while (Time.unscaledTime < end) yield return null;
            _timer = null;
            ctx.ReturnToHub();   // ignored if the session already ended another way
        }

        private static void Stop(LaunchContext ctx)
        {
            if (_timer != null) { MelonCoroutines.Stop(_timer); _timer = null; }
            Core.Log.Msg("cleaned up.");
        }

        // --- messages ----------------------------------------------------------------------------------------

        private static void OnMessage(SessionMessage m)
        {
            switch (m.Channel)
            {
                case "ping":
                    Core.Log.Msg($"ping from {m.SenderSteamId}: {m.Text}");
                    API.SendTo(m.SenderSteamId, Id, "pong", "pong from " + LocalName());
                    break;
                case "pong":
                    Core.Log.Msg($"pong from {m.SenderSteamId}: {m.Text}");
                    API.ShowNotice(m.Text);
                    break;
                default:
                    Core.Log.Warning($"unknown channel '{m.Channel}' from {m.SenderSteamId}; ignored.");
                    break;
            }
        }

        private static string LocalName()
        {
            foreach (var p in API.Players) if (p.IsLocal) return p.DisplayName;
            return "?";
        }
    }
}
