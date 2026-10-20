# Side Hustle - Gamemode Hub for Schedule I

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de/sidehustle](https://support.doodesch.de/sidehustle).

> **One hub for every gamemode.** Side Hustle opens a hub beside the main menu that lists every installed
> gamemode mod and launches it straight from the menu - no savegame required. It is the shared hub that
> gamemode mods plug into.

> 🌐 **Is anyone playing right now?** Every public lobby is listed live at
> **[SideHustle.doodesch.de](https://sidehustle.doodesch.de)** - who is hosting, which gamemode, how full it is,
> and the exact mods that session runs. Check before you launch; you still join from inside the game.

![Version](https://img.shields.io/badge/version-3.0.0-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-purple)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3+-green)
![S1API](https://img.shields.io/badge/S1API-required-orange)
[![Polyfill](https://polyfill.doomods.com/badge/side-hustle.svg)](https://polyfill.doomods.com/mod/side-hustle)

[![MLVScan SideHustle.dll](https://mlvscan.com/attestations/att_7vo4_xhua2LZsV1SxmWekok_/badge.svg)](https://mlvscan.com/attestations/att_7vo4_xhua2LZsV1SxmWekok_)

![Side Hustle 3.0](https://raw.githubusercontent.com/DooDesch-Mods/ScheduleOne-SideHustle/main/docs/img/header.jpg)

## What it is

Side Hustle is a **hub**, not a gamemode by itself. It opens beside the vanilla main menu, so Continue stays
one click away. Install gamemode mods that build on it and they appear in the list; pick one and it
launches immediately in its own self-contained session - gamemodes never load or touch a normal save.

If you installed Side Hustle as a dependency of another mod, there is nothing to do: that mod registers
itself and shows up under the Side Hustle entry.

## New in 3.0: the hub

- **One page beside the vanilla menu.** Your last gamemode with Host again, public co-op for your own save, every
  gamemode with its actions, and a Lobbies tab, all on one screen over its own 3D scene.
- **Fewer clicks.** Host again and Host with defaults skip the form, a solo gamemode starts with one click, and
  after a session the hub opens on that gamemode for a rematch.
- **One lobby board** for every gamemode and vanilla co-op: search, filters, seats and what happens before you join.
- **A live status for every launch**: each step from lobby found to loading the world, with Cancel and the reason
  when something fails.

## New in 2.0

- **Mod Profiles** - an in-game mod manager. Separate sets of mods you switch between; the game restarts into the
  one you pick. Build one from the mods you already have installed. Your real Mods folder is never managed, so
  r2modman and friends keep working - and in this build they are also how mods get installed in the first place.
- **Vanilla co-op with mod sync** - host your normal savegame as a public lobby. A joiner's mods are compared to
  yours and turned into a checklist with a direct link for each one. You download them, Side Hustle picks each
  file up from your Downloads folder, verifies its hash and installs it into the profile. Mods are never sent
  between players. **This build downloads nothing itself** - Thunderstore does not allow a package to fetch
  executable code, so installing is your mod manager's job here and Side Hustle does the matching and the
  profile work around it.
- **Join a gamemode you don't have installed** - a public gamemode lobby lists what it needs. Pick "Join (installs
  the mods)", choose a session, and Side Hustle installs it, restarts and joins that lobby.
- **Lobby browser on the web** - see every open public lobby at [SideHustle.doodesch.de](https://sidehustle.doodesch.de) without starting the game.
- **Lobby chat on the phone** - through [WhatsDab](https://thunderstore.io/c/schedule-i/p/DooDesch/WhatsDab/), which comes with this mod: group and one-to-one threads, unread badges, notifications.

## Features

- **Every gamemode on one page** - name, description, author and a Singleplayer / Multiplayer badge, launched
  from the hub beside the main menu.
- **No savegame** - gamemodes run in their own session and never load or alter your saves.
- **Singleplayer, host or join** - solo gamemodes start with one click, multiplayer ones carry Host and Join.
- **Configure before you host** - a native-style setup form for the exact player count, a public or private
  (friends-only) lobby with an optional password, and any settings the gamemode exposes (sliders, toggles,
  choices, text), all passed to the gamemode at launch.
- **Public server browser** - browse open lobbies as cards (host, player count, a lock for password-protected
  ones), filtered per gamemode; bigger lobbies (past the vanilla 4) come built in, no extra lobby mod needed.
- **Play with anyone, not just friends** - Schedule I normally kicks joiners who aren't the host's Steam friends;
  while you host a Side Hustle gamemode that kick is lifted, so anyone can join your public or password lobbies.
- **Custom display name** - each gamemode has its own "Your name" field on its Host / Join screen; fill it in and
  others see that name (nametag, scoreboards, browser) instead of your Steam name. Per gamemode, per session.
- **Discover what's being played** - the menu also lists gamemodes you don't have installed that have live public
  lobbies right now, with a "Download Mod" button. Toggleable; gamemode authors can opt out.
- **World gamemodes** - gamemodes that need the loaded game world get a throwaway session, outside your
  save slots, so your real saves are never touched.
- **Conflict-free mod sets (optional, the host's choice)** - a gamemode can declare which mods it works with. When
  hosting, you keep your full set ("Current installed mods", default) or run only the gamemode's mods ("Required
  mods only") in a temporary, isolated profile (after a confirmation listing the changes), switching back when you
  leave. Nothing in your real Mods folder is ever disabled, renamed, moved or deleted (junctions/hardlinks, no
  admin), so your mod manager stays in sync.
- **Load-order independent API** so gamemode mods register whether they load before or after the hub.
- A single setting to hide the entry without uninstalling.

## Screenshots

![Play tab](https://raw.githubusercontent.com/DooDesch-Mods/ScheduleOne-SideHustle/main/docs/img/play.jpg)
![Lobby board](https://raw.githubusercontent.com/DooDesch-Mods/ScheduleOne-SideHustle/main/docs/img/lobbies.jpg)

## Requirements

- **Schedule I** (IL2CPP) with **MelonLoader 0.7.3+**.
- **S1API 3.1.15+** (pulled in as a dependency).
- **Sideload 1.33.1+** (pulled in as a dependency) - draws the hub.
- **[WhatsDab](https://thunderstore.io/c/schedule-i/p/DooDesch/WhatsDab/)** (pulled in as a dependency) - the lobby chat app that used to live in here.
- One or more **gamemode mods** that use Side Hustle.

## Settings

Open the **Settings** tab in the hub. Everything is stored in `UserData/MelonPreferences.cfg` under `SideHustle_01_Main`;
`Enabled` (default `true`) hides the hub without uninstalling and is only in the cfg.

## License

MIT. See the included LICENSE.md.
