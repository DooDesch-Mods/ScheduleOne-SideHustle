# Side Hustle - A Main-Menu Hub for Schedule I Gamemodes

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de/sidehustle](https://support.doodesch.de/sidehustle).

> Side Hustle adds a single entry to the Schedule I main menu that lists every installed
> "gamemode" mod and launches it straight from the menu - no savegame required. It is the
> shared entry point that gamemode mods (like an in-game tattoo editor) plug into. Built on
> [S1API](https://github.com/ifBars/S1API).

> 🌐 **Is anyone playing right now?** Every public lobby is listed live at
> **[SideHustle.doodesch.de](https://sidehustle.doodesch.de)** - who is hosting, which gamemode, how full it is,
> and the exact mods that session runs. Check before you launch; you still join from inside the game.

![Version](https://img.shields.io/badge/version-3.0.0-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-purple)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3+-green)
![S1API](https://img.shields.io/badge/S1API-required-orange)
![Status](https://img.shields.io/badge/status-working-brightgreen)
[![Polyfill](https://polyfill.doomods.com/badge/side-hustle.svg)](https://polyfill.doomods.com/mod/side-hustle)

[![MLVScan SideHustle.dll](https://mlvscan.com/attestations/att_yzj7-1r9wxWqOThCf2Zq6wMZ/badge.svg)](https://mlvscan.com/attestations/att_yzj7-1r9wxWqOThCf2Zq6wMZ)

![Side Hustle 3.0](https://raw.githubusercontent.com/DooDesch-Mods/ScheduleOne-SideHustle/main/docs/img/header.jpg)

## What it is

Side Hustle is a **hub**, not a gamemode by itself. It opens beside the vanilla main menu, so Continue stays
one click away. Install gamemode mods that build on it and they appear in the list;
pick one and it launches immediately in its own self-contained session. Gamemodes never load or
touch a normal savegame, so they stay cleanly separated from your real playthrough.

If you installed Side Hustle as a dependency of another mod, you do not have to do anything - that
mod registers itself and shows up under the Side Hustle entry.

## New in 3.0: the hub

- **One page beside the vanilla menu.** Your last gamemode with Host again, public co-op for your own save, every
  gamemode with its actions, and a Lobbies tab, all on one screen over its own 3D scene.
- **Fewer clicks.** Host again and Host with defaults skip the form, a solo gamemode starts with one click, and
  after a session the hub opens on that gamemode for a rematch.
- **One lobby board** for every gamemode and vanilla co-op: search, filters, seats and what happens before you join.
- **A live status for every launch**: each step from lobby found to loading the world, with Cancel and the reason
  when something fails.

## Features

- **Every gamemode on one page.** The hub beside the main menu lists all installed gamemode mods with their
  name, description, author and a Singleplayer / Multiplayer badge.
- **No savegame.** Gamemodes launch in their own session and never load or alter your saves.
- **Singleplayer, host or join.** Singleplayer gamemodes launch with one click; multiplayer gamemodes carry
  Host and Join right on their row.
- **Configure before you host.** Hosting opens a native-style setup form: set the exact player count (up to the
  lobby cap), choose a public or private (friends-only) lobby with an optional password, and adjust any settings
  the gamemode exposes (sliders, toggles, choices, text) - all handed to the gamemode when it starts.
- **Lobby browser.** Find and join open lobbies for a gamemode, shown as cards with the host, player count and a
  lock for password-protected lobbies. Search by name, sort, and hide full, locked or other-version lobbies; the
  list refreshes itself every few seconds. Bigger lobbies (well past the vanilla 4 players) come built in - no extra
  lobby mod needed.
- **The host form remembers.** Player count, visibility, lobby name and Discord invite come back the next time you
  host, per gamemode and for vanilla co-op. "Host again" skips the form. Passwords are never stored.
- **Discord invite.** A host can put a Discord invite on the lobby; guests see a Discord button on the lobby card
  and in the Lobby app on the phone.
- **Play with anyone, not just friends.** Schedule I normally kicks joiners who aren't on the host's Steam friends
  list. While you host a Side Hustle gamemode that kick is lifted, so anyone can join your public or
  password-protected lobbies. Normal co-op outside Side Hustle is unaffected.
- **Custom display name.** Each gamemode has its own "Your name" field on its Host / Join screen. Fill it in and
  other players see that name - the in-game nametag over your character, scoreboards and the server browser -
  instead of your Steam name. A privacy option for public matches, remembered per gamemode.
- **Discover gamemodes.** The menu also lists released gamemodes you don't have installed, and any gamemode with a
  live public lobby right now, with a "Download Mod" button or a one-click install into a mod profile. Toggle it off
  in the settings; gamemode authors can opt their gamemode out of being advertised.
- **No dead ends.** Every download, search and wait has a Cancel or a Try again. If a session cannot start, the hub
  says why.
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

## New in 2.0: Profiles and Sync

- **Join a gamemode you don't have installed.** A public gamemode lobby lists the mod files a joiner needs. Pick
  "Join (installs the mods)", choose a session, and Side Hustle installs the gamemode (Thunderstore automatically,
  anything else via the checklist with a direct Nexus link), restarts and joins that lobby. Your own mods stay put.
- **Mod Profiles (in-game mod manager).** A separate "Mod Profiles" main-menu entry keeps isolated sets of mods you
  can switch between (the game restarts into the one you pick). Build a profile from your installed mods or browse
  and install straight from Thunderstore with dependencies resolved. Your real Mods folder is never managed, so
  external managers keep working. A short startup prompt lets you pick the profile to load (or the full set).
- **Vanilla Co-op with mod sync.** Host your own savegame as a public lobby (from the menu, or when you click
  Continue). When someone joins, Side Hustle compares mods and offers to set up a matching profile automatically -
  download from Thunderstore or a mod's GitHub releases, restart, rejoin - so you can play modded co-op with anyone
  without hand-matching mod lists. Mods that still need a browser download (e.g. Nexus) get a guided checklist:
  open the link, download the file, and Side Hustle picks it up from your Downloads folder (or Vortex's), verifies
  it and installs it on its own. Optionally require synced clients. Downloads only ever come from Thunderstore's
  CDN or GitHub releases, every file is hash-verified, and mods are never sent between players directly.
  **The build published on Thunderstore downloads nothing** (`-p:StoreBuild=thunderstore`), because Thunderstore
  does not allow a package to fetch executable code - not from GitHub and not from Thunderstore either. There,
  every mod takes the checklist route, and "Add from Thunderstore" is absent from Mod Profiles; the package
  index and mod icons are still fetched, being JSON and PNGs. The GitHub and Nexus builds are unchanged.
- **Lobby browser on the web** - see every open public lobby at [SideHustle.doodesch.de](https://sidehustle.doodesch.de) without starting the game.
- **Sync mod settings (optional).** A host can apply chosen mods' settings to everyone in the session only, never
  touching their real settings; anything that looks like a secret is off by default.
- **Lobby chat on the phone.** Side Hustle carried its own messenger until 2.1; that app is now
  [WhatsDab](https://github.com/DooDesch-Mods/ScheduleOne-WhatsDab), a mod of its own and a requirement of this
  one. Same idea, more of it: group and one-to-one threads, unread badges, notifications, and both orientations.
  Nothing to set up - install and it is on the phone.

## Screenshots

| | |
|---|---|
| ![Play tab](https://raw.githubusercontent.com/DooDesch-Mods/ScheduleOne-SideHustle/main/docs/img/play.jpg) | ![Lobby board](https://raw.githubusercontent.com/DooDesch-Mods/ScheduleOne-SideHustle/main/docs/img/lobbies.jpg) |
| ![Host form](https://raw.githubusercontent.com/DooDesch-Mods/ScheduleOne-SideHustle/main/docs/img/host.jpg) | ![Joining](https://raw.githubusercontent.com/DooDesch-Mods/ScheduleOne-SideHustle/main/docs/img/join.jpg) |

## Requirements

| Component | Version / Source |
|-----------|------------------|
| Schedule I | IL2CPP (current Steam public build) |
| MelonLoader | `0.7.3+` |
| S1API | [ifBars/S1API_Forked](https://thunderstore.io/c/schedule-i/p/ifBars/S1API_Forked/) `3.1.15+` |
| [Sideload](https://github.com/DooDesch-Mods/ScheduleOne-Sideload) | `1.33.1+` - draws the hub |
| [WhatsDab](https://github.com/DooDesch-Mods/ScheduleOne-WhatsDab) | `1.0.2+` - the lobby chat app, pulled in as a dependency |

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

Open the **Settings** tab in the hub to change these in game. They are stored in
`UserData/MelonPreferences.cfg` under `SideHustle_01_Main`.

| Setting | Default | What it does |
|---|---|---|
| `Enabled` | `true` | Show the Side Hustle menu entry. Off hides it without uninstalling (only in the cfg). |
| `ShowUninstalledGamemodes` | `true` | Also list gamemodes you don't have installed. |
| `AskHostOnContinue` | `true` | Clicking Continue or a save slot offers to open it as a public lobby. |
| `AcceptStrangerMessages` | `true` | Anyone looking at your public lobby can send you a short message. |
| `MenuStatePanel` | `true` | Info column on the right of the main menu (needs Sideload 1.13.0+). |
| `JoinChatPanel` | `true` | Chat column on the screens you pass through while joining. |
| `DeferModsUntilLobby` | `false` | Load only Side Hustle and Sideload at startup; the rest loads when you host or join. Takes effect after a restart. |
| `AlwaysLoadMods` | `""` | File names that load at startup even with `DeferModsUntilLobby` on, comma-separated. |

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
session, then hands you a `LaunchContext` with the role (`Host`, `Client` or `Singleplayer`), lobby id,
player count and the host's settings. Re-registering your own `Id` is safe; `Register` logs every problem
it finds in a descriptor. Call `ctx.ReturnToHub()` from your gamemode when it finishes to return to the menu.

Since API version 3 (`API.ApiVersion`, `API.Has(...)`) a gamemode can also:

- listen to `SessionStarted` / `SessionEnded` and `PlayerJoined` / `PlayerLeft`, and read `API.Players`;
- show a short message on screen with `API.ShowNotice`;
- send its own messages to the other players with `API.SendToAll` / `API.SendTo` and receive them with
  `API.OnMessage`, without its own networking code.

`OnExitToHub` runs exactly once per session, also when Side Hustle aborts a session that failed to start.
`Examples/SideHustleLink.cs` is a copy-in file that keeps Side Hustle optional, and `Examples/ExampleGamemode`
is a complete gamemode to start from. The full reference is on the
[docs site](https://docs.doodesch.de/mods/sidehustle/).

## Compatibility

- IL2CPP build only (current Steam public branch).
- Works alongside any other MelonLoader/S1API mod. Side Hustle only adds a main-menu entry and the
  registration API; it does not touch gameplay or saves.

## Credits

- **DooDesch** - mod author.
- **[ifBars/S1API](https://github.com/ifBars/S1API)** - the modding API this is built on.

## License

Provided as-is under the [MIT License](LICENSE.md).
