# Side Hustle - Gamemode Hub for Schedule I

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de](https://support.doodesch.de).

> **One menu entry for every gamemode.** Side Hustle adds a single button to the main menu that
> lists every installed gamemode mod and launches it straight from the menu - no savegame required.
> It is the shared hub that gamemode mods plug into.

![Version](https://img.shields.io/badge/version-1.6.0-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-purple)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3+-green)
![S1API](https://img.shields.io/badge/S1API-required-orange)

## What it is

Side Hustle is a **hub**, not a gamemode by itself. On its own it just adds a **Side Hustle** button
to the main menu. Install gamemode mods that build on it and they appear in the list; pick one and it
launches immediately in its own self-contained session - gamemodes never load or touch a normal save.

If you installed Side Hustle as a dependency of another mod, there is nothing to do: that mod registers
itself and shows up under the Side Hustle entry.

## Features

- **One menu entry for every gamemode** - name, description, author and a Singleplayer / Multiplayer
  badge, launched from the main menu.
- **No savegame** - gamemodes run in their own session and never load or alter your saves.
- **Singleplayer, host or join** - multiplayer gamemodes show a Singleplayer / Host / Join choice.
- **Configure before you host** - a native-style setup form for the exact player count, a public or private
  (friends-only) lobby with an optional password, and any settings the gamemode exposes (sliders, toggles,
  choices, text), all passed to the gamemode at launch.
- **Public server browser** - browse open lobbies as cards (host, player count, a lock for password-protected
  ones), filtered per gamemode; bigger lobbies (past the vanilla 4) come built in, no extra lobby mod needed.
- **Play with anyone, not just friends** - Schedule I normally kicks joiners who aren't the host's Steam friends;
  while you host a Side Hustle gamemode that kick is lifted, so anyone can join your public or password lobbies.
- **World gamemodes** - gamemodes that need the loaded game world get a throwaway session, outside your
  save slots, so your real saves are never touched.
- **Conflict-free mod sets (optional, the host's choice)** - a gamemode can declare which mods it works with. When
  hosting, you keep your full set ("Current installed mods", default) or run only the gamemode's mods ("Required
  mods only") in a temporary, isolated profile (after a confirmation listing the changes), switching back when you
  leave. Nothing in your real Mods folder is ever disabled, renamed, moved or deleted (junctions/hardlinks, no
  admin), so your mod manager stays in sync.
- **Load-order independent API** so gamemode mods register whether they load before or after the hub.
- A single setting to hide the entry without uninstalling.

## Requirements

- **Schedule I** (IL2CPP) with **MelonLoader 0.7.3+**.
- **S1API** (pulled in as a dependency).
- Optional: **Mod Manager & Phone App** for the in-game settings UI.
- One or more **gamemode mods** that use Side Hustle.

## Settings

`Enabled` (default `true`) - show the Side Hustle menu entry. Editable in the Mod Manager & Phone
App UI or `UserData/MelonPreferences.cfg` under `SideHustle_01_Main`.

## License

MIT. See the included LICENSE.md.
