using System;
using System.Text.RegularExpressions;
using Il2CppSteamworks;

namespace SideHustle.Menu
{
    /// <summary>
    /// A host's Discord invite on a lobby.
    ///
    /// Only the invite CODE travels: it is checked against a strict pattern when the host types it, stored under
    /// <see cref="Key"/>, checked again when a guest reads it, and the link a guest opens is rebuilt from the code. The
    /// lobby never carries a URL, so a hostile lobby cannot make a guest open anything but discord.gg/&lt;code&gt;.
    /// The link host is not on the <see cref="DownloadLink"/> allowlist and does not need to be: the rebuilt link has
    /// exactly one possible shape.
    /// </summary>
    internal static class DiscordInvite
    {
        /// <summary>The lobby key that carries the code.</summary>
        internal const string Key = "sh_discord";

        /// <summary>What a host sees when a typed invite does not parse.</summary>
        internal const string NotAnInvite = "Not a Discord invite.";

        private static readonly Regex CodePattern = new Regex("^[A-Za-z0-9-]{2,32}$", RegexOptions.Compiled);

        // discord.gg/<code> or discord.com/invite/<code>, with or without a scheme and "www.", optional trailing slash.
        private static readonly Regex LinkPattern = new Regex(
            @"^(?:https?://)?(?:www\.)?(?:discord\.gg/|discord\.com/invite/)(?<code>[^/?#\s]+)/?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Whether <paramref name="code"/> is a bare invite code.</summary>
        internal static bool IsCode(string code) => !string.IsNullOrEmpty(code) && CodePattern.IsMatch(code);

        /// <summary>Read an invite link out of what the host typed. An empty input is not an invite (the caller
        /// treats the field as unset before asking).</summary>
        internal static bool TryParse(string input, out string code)
        {
            code = "";
            var m = LinkPattern.Match((input ?? "").Trim());
            if (!m.Success) return false;
            string c = m.Groups["code"].Value;
            if (!IsCode(c)) return false;
            code = c;
            return true;
        }

        /// <summary>The link a code stands for.</summary>
        internal static string LinkFor(string code) => "https://discord.gg/" + code;

        /// <summary>The invite a lobby carries, or "" when it has none or the stored value is not a code.</summary>
        internal static string ReadFor(ulong lobbyId)
        {
            if (lobbyId == 0UL) return "";
            try
            {
                string raw = SteamMatchmaking.GetLobbyData(new CSteamID(lobbyId), Key);
                if (string.IsNullOrEmpty(raw)) return "";
                if (IsCode(raw)) return raw;
                Core.Log?.Warning($"[discord] lobby {lobbyId} carries a value under {Key} that is not an invite code; ignoring it.");
                return "";
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"[discord] could not read the invite of lobby {lobbyId}: {e.Message}");
                return "";
            }
        }

        /// <summary>Write the invite onto a lobby we host. An empty code clears the key. The result of the Steam call is
        /// checked: a lobby whose data is full refuses the write without raising anything.</summary>
        internal static bool Publish(ulong lobbyId, string code)
        {
            code = code ?? "";
            if (code.Length > 0 && !IsCode(code))
            {
                Core.Log?.Warning("[discord] refusing to publish a value that is not an invite code.");
                return false;
            }
            if (lobbyId == 0UL)
            {
                Core.Log?.Warning("[discord] no lobby to publish the invite on.");
                return false;
            }
            try
            {
                bool ok = SteamMatchmaking.SetLobbyData(new CSteamID(lobbyId), Key, code);
                if (ok) Core.Log?.Msg(code.Length > 0 ? $"[discord] invite published on lobby {lobbyId}." : $"[discord] invite cleared on lobby {lobbyId}.");
                else Core.Log?.Warning($"[discord] Steam refused the invite key on lobby {lobbyId} (its data is probably full).");
                return ok;
            }
            catch (Exception e)
            {
                Core.Log?.Error($"[discord] could not write the invite to lobby {lobbyId}: {e}");
                return false;
            }
        }

        /// <summary>Open the invite for <paramref name="code"/>. Called from a click only.</summary>
        internal static void Open(string code)
        {
            if (!IsCode(code))
            {
                Core.Log?.Warning("[discord] not opening a value that is not an invite code.");
                return;
            }
            DownloadLink.Launch(LinkFor(code));
        }
    }
}
