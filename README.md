# Side Hustle - A Main-Menu Hub for Schedule I Gamemodes

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de/sidehustle](https://support.doodesch.de/sidehustle).

> Side Hustle adds a single entry to the Schedule I main menu that lists every installed
> "gamemode" mod and launches it straight from the menu - no savegame required. It is the
> shared entry point that gamemode mods (like an in-game tattoo editor) plug into. Built on
> [S1API](https://github.com/ifBars/S1API).

![Version](https://img.shields.io/badge/version-2.0.0-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-purple)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3+-green)
![S1API](https://img.shields.io/badge/S1API-required-orange)
![Status](https://img.shields.io/badge/status-working-brightgreen)

## What it is

Side Hustle is a **hub**, not a gamemode by itself. On its own it just adds a **Side Hustle**
button to the main menu. Install gamemode mods that build on it and they appear in the list;
pick one and it launches immediately in its own self-contained session. Gamemodes never load or
touch a normal savegame, so they stay cleanly separated from your real playthrough.

If you installed Side Hustle as a dependency of another mod, you do not have to do anything - that
mod registers itself and shows up under the Side Hustle entry.

## Features

- **One menu entry for every gamemode.** A single "Side Hustle" button on the main menu lists all
  installed gamemode mods with their name, description, author and a Singleplayer / Multiplayer badge.
- **No savegame.** Gamemodes launch in their own session and never load or alter your saves.
- **Singleplayer, host or join.** Singleplayer gamemodes launch instantly; multiplayer gamemodes show
  a Singleplayer / Host / Join choice, all in the native menu style.
- **Configure before you host.** Hosting opens a native-style setup form: set the exact player count (up to the
  lobby cap), choose a public or private (friends-only) lobby with an optional password, and adjust any settings
  the gamemode exposes (sliders, toggles, choices, text) - all handed to the gamemode when it starts.
- **Public server browser.** Find and join open sessions for a gamemode, shown as cards with the host, player
  count and a lock for password-protected lobbies; filtered so each gamemode only lists its own lobbies. Bigger
  lobbies (well past the vanilla 4 players) come built in - no extra lobby mod needed.
- **Play with anyone, not just friends.** Schedule I normally kicks joiners who aren't on the host's Steam friends
  list. While you host a Side Hustle gamemode that kick is lifted, so anyone can join your public or
  password-protected lobbies. Normal co-op outside Side Hustle is unaffected.
- **Custom display name.** Each gamemode has its own "Your name" field on its Host / Join screen. Fill it in and
  other players see that name - the in-game nametag over your character, scoreboards and the server browser -
  instead of your Steam name. A privacy option for public matches; per gamemode, per session, and never saved.
- **Discover what's being played.** The menu also lists gamemodes you don't have installed that have live public
  lobbies right now, with a "Download Mod" button so you can go get them. Toggle it off in the settings; gamemode
  authors can opt their gamemode out of being advertised.
- **Host controls + friend invites.** The host can kick a player from the session, and every lobby member - not just
  the host - can invite Steam friends from the pause-menu panel.
- **World gamemodes.** Gamemodes that need the actual game world get a throwaway session booted for them,
  outside your save slots, so your real saves are never touched.
- **Conflict-free mod sets (optional, the host's choice).** A gamemode can declare which other mods it works with.
  When you host it, the setup form lets you keep your full set ("Current installed mods", the default) or run only
  the gamemode's mods ("Required mods only") in a temporary, isolated profile - after a confirmation that lists
  exactly what changes. Nothing in your real Mods folder is ever disabled, renamed, moved or deleted (it uses
  junctions/hardlinks, no admin needed), so your mod manager stays in sync; leaving the gamemode restarts back to
  your full set, and a normal launch always loads everything.
- **Load-order independent API.** Gamemode mods register themselves whether they load before or after
  Side Hustle.
- **Stays out of the way.** A single toggle hides the entry without uninstalling.

## New in 2.0: Profiles, Sync and Messenger

- **Mod Profiles (in-game mod manager).** A separate "Mod Profiles" main-menu entry keeps isolated sets of mods you
  can switch between (the game restarts into the one you pick). Build a profile from your installed mods or browse
  and install straight from Thunderstore with dependencies resolved. Your real Mods folder is never managed, so
  external managers keep working. A short startup prompt lets you pick the profile to load (or the full set).
- **Vanilla Co-op with mod sync.** Host your own savegame as a public lobby (from the menu, or when you click
  Continue). When someone joins, Side Hustle compares mods and offers to set up a matching profile automatically -
  download from Thunderstore, restart, rejoin - so you can play modded co-op with anyone without hand-matching mod
  lists. Mods that can't be fetched automatically get a link + "open folder" checklist. Optionally require synced
  clients. Downloads only ever come from Thunderstore's CDN; mods are never sent between players directly.
- **Sync mod settings (optional).** A host can apply chosen mods' settings to everyone in the session only, never
  touching their real settings; anything that looks like a secret is off by default.
- **Side Hustle Messenger.** A phone app to chat with the other players in your lobby - shared lobby chat and
  private one-to-one threads, unread badges and native notifications.

## Requirements

| Component | Version / Source |
|-----------|------------------|
| Schedule I | IL2CPP (current Steam public build) |
| MelonLoader | `0.7.3+` |
| S1API | [ifBars/S1API_Forked](https://thunderstore.io/c/schedule-i/p/ifBars/S1API_Forked/) |
| Mod Manager & Phone App | [Prowiler, Nexus mods/397](https://www.nexusmods.com/schedule1/mods/397) - optional, for the in-game settings UI |

## Installation

### Recommended: a Thunderstore mod manager

Install with a mod manager (r2modman / Gale) from the Schedule I community; the dependencies
(MelonLoader, S1API) are pulled in automatically.

### Manual

1. Install **MelonLoader 0.7.3** for Schedule I.
2. Install **S1API** (its DLLs go in `Mods/` and `Plugins/` per its own instructions).
3. Drop **`SideHustle.dll`** into your Schedule I `Mods/` folder.
4. Install one or more gamemode mods that use Side Hustle.

## Configuration

Settings live in the **Mod Manager & Phone App** UI in-game, or in `UserData/MelonPreferences.cfg`
under `SideHustle_01_Main`.

| Setting | Default | What it does |
|---|---|---|
| `Enabled` | `true` | Show the Side Hustle menu entry. Off hides it without uninstalling (return to the main menu to apply). |
| `ShowUninstalledGamemodes` | `true` | Also list gamemodes you don't have installed that currently have live public lobbies, with a Download link. Off shows only your installed gamemodes. |

## For mod authors

Make your mod show up as a gamemode in a few lines. Reference `SideHustle.dll`, declare it as an
optional dependency so your mod still loads if Side Hustle is absent, and register from your
`OnInitializeMelon`:

```csharp
[assembly: MelonOptionalDependencies("SideHustle")]

SideHustle.API.Register(new GamemodeDescriptor
{
    Id = "you.yourmode",                 // stable, unique
    DisplayName = "Your Mode",
    Description = "What your gamemode does.",
    Author = "You",
    Support = GamemodeSupport.Singleplayer,   // or Multiplayer / Hybrid
    Surface = GamemodeSurface.MenuSpace,      // overlay on the menu (no save), or World
    OnLaunchSingleplayer = ctx => { /* start your gamemode */ },
    OnExitToHub = ctx => { /* clean up when the player backs out */ }
});
```

For a multiplayer gamemode set `Support` to `Multiplayer` or `Hybrid` and add `OnHostMultiplayer` /
`OnJoinMultiplayer`. Side Hustle creates and tracks the lobby and (for `World` gamemodes) boots the
session, then hands you a `LaunchContext` with the host/client role, lobby id, player count and the
host's settings. Registration replaces by `Id`, so re-registering is safe. Call `ctx.ReturnToHub()`
from your gamemode when it finishes to return to the menu.

## Roadmap

Side Hustle 1.1.0 adds multiplayer (Host / Join), the public server browser, and world gamemodes - so
both singleplayer and multiplayer gamemodes launch straight from the menu. See the
[wiki](https://github.com/DooDesch-Mods/ScheduleOne-SideHustle/wiki) for the full roadmap and API reference.

## Compatibility

- IL2CPP build only (current Steam public branch).
- Works alongside any other MelonLoader/S1API mod. Side Hustle only adds a main-menu entry and the
  registration API; it does not touch gameplay or saves.

## Credits

- **DooDesch** - mod author.
- **[ifBars/S1API](https://github.com/ifBars/S1API)** - the modding API this is built on.
- **Prowiler** - Mod Manager & Phone App (in-game settings UI).

## License

Provided as-is under the [MIT License](LICENSE.md).
