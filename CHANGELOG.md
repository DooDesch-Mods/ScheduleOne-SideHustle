# Changelog

All notable changes to Side Hustle are documented here. This project adheres to
[Semantic Versioning](https://semver.org/).

## [1.2.0] - 2026-06-25

### Added
- **Conflict-free mod sets.** A gamemode can declare which other mods it works with (a mod policy). When you
  launch it, Side Hustle shows exactly which mods it will disable and enable, then - on your confirmation -
  applies the change and restarts the game into the gamemode. When you leave the gamemode your normal mods are
  restored. A "Restore my mods" entry is there if you ever need to put everything back yourself.

## [1.1.0] - 2026-06-25

### Added
- **Multiplayer launch.** Multiplayer and hybrid gamemodes now show a Singleplayer / Host / Join
  choice in the menu. Hosting opens a public lobby (with a player-count picker); bigger lobbies are
  supported with BiggerLobbies.
- **Public server browser.** Browse and join open sessions for a gamemode, filtered so each gamemode
  only lists its own lobbies.
- **World gamemodes.** Gamemodes that need the actual game world get a throwaway session booted for
  them, outside your five save slots - your real saves are never created or touched.
- **Richer launch context.** Gamemodes receive the host/client role, lobby id, player count, host name
  and the host's settings when they launch.
- Play-mode badge (Singleplayer / Multiplayer / SP + MP) on each gamemode in the list, optional
  per-gamemode icons, and a recently-played ordering so your last gamemodes appear first.

## [1.0.1] - 2026-06-24

### Fixed
- The "Side Hustle" menu entry could be added more than once (showing duplicate entries) when the
  main menu re-initialised during loading. Injection is now idempotent, so exactly one entry appears.

## [1.0.0] - 2026-06-24

Initial release.

### Added
- A "Side Hustle" entry on the main menu that lists every installed gamemode mod (name,
  description, author) and launches the selected one without loading a savegame.
- Public, load-order-independent registration API (`SideHustle.API.Register` with
  `GamemodeDescriptor` / `LaunchContext`) for mods to appear as gamemodes.
- Singleplayer launch flow with a clean return to the menu (`LaunchContext.ReturnToHub`).
- Multiplayer host/join and a server browser are shown but disabled, ready for a later update.
- `Enabled` setting (MelonPreferences) to hide the menu entry without uninstalling.
