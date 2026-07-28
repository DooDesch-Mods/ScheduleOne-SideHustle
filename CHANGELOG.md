# Changelog

All notable changes to Side Hustle are documented here. This project adheres to
[Semantic Versioning](https://semver.org/).

## [2.1.2] - 2026-07-28

### Changed

- The Thunderstore build now downloads nothing at all. 2.1.1 stopped it fetching from GitHub and was rejected
  again, so this build also stops it downloading packages from Thunderstore itself. What that costs on
  Thunderstore, and only there: mod sync never installs anything on its own, every mod lands on the checklist
  with a direct link, and the "Add from Thunderstore" browser is gone from Mod Profiles - a browser that lists
  the whole community index and then cannot install is worse than no button, so the profile screen says to use
  a mod manager instead. Profiles built from mods you already have work unchanged.
- The package index and mod icons are still fetched. They are a JSON catalogue and PNGs, not executable code.
- The builds on GitHub and Nexus are unaffected and keep every download.

## [2.1.1] - 2026-07-27

### Changed

- The Thunderstore build no longer downloads mods from GitHub releases. Thunderstore does not allow a package
  to fetch executable code from outside Thunderstore, which is a fair rule and one this mod's sync was
  breaking, so the build published there is compiled without that fetch: no endpoint, no release reader, no
  archive scanner in the DLL at all. Mods hosted on GitHub take the same route Nexus mods already take - a
  checklist with a direct link, you download it, Side Hustle picks it up from your Downloads folder and
  verifies the hash. Thunderstore's own packages still download automatically.
- Nothing changes for the builds on GitHub and Nexus; those keep the automatic GitHub download.

## [2.1.0] - 2026-07-27

### Changed

- The messenger left home. Side Hustle carried its own phone app for lobby chat; that app is now
  [WhatsDab](https://github.com/DooDesch-Mods/ScheduleOne-WhatsDab) - a mod of its own, and a requirement of this
  one, so a mod manager installs it for you and nothing about the feature disappears. It got better on the way
  out: both phone orientations, a typing indicator, right-click to step back the way the vanilla apps do, and an
  interface written as three web files instead of 780 lines of panel code. Side Hustle is a gamemode hub again,
  and the chat is maintained where it belongs.

### Removed

- 1270 lines of chat code (transport, store, contacts, two screens and a notifier) and the embedded app icon.
  Nothing else in Side Hustle used them.

## [2.0.1] - 2026-07-26

### Fixed
- Downloads that land in their own folder are picked up now. If your browser saves a mod to
  `Downloads\SomeMod\SomeMod.dll`, the checklist finds it there too, not just directly in Downloads.
- A mod that could not be downloaded now says why. A checklist row that appears after the consent screen
  promised an automatic install tells you what happened: Thunderstore refused the file, or the host runs a
  different build than the one on Thunderstore (same version number, different file - ask them for theirs).
- A hiccup on Thunderstore's side no longer sends a mod to the checklist. A 5xx answer is retried once before
  giving up, which is what a "502" during a join used to cost you.

## [2.0.0] - 2026-07-26

Play modded multiplayer with anyone - mods sync themselves, join a gamemode you don't even have installed, and chat in-game.

### Added
- **Join a gamemode you don't have installed.** A public gamemode lobby now advertises the exact mod files a joiner
  needs (the gamemode's own mod, whatever its policy requires, with version and hash). In the Side Hustle menu such a
  lobby is no longer a dead "not installed" entry: pick "Join (installs the mods)", confirm, and Side Hustle fetches
  them from Thunderstore - anything it can't fetch gets the same checklist with a direct Nexus link - installs them
  into a session profile, restarts and drops you into that lobby. Your own mods stay exactly where they are.
- **Mod Profiles - an in-game mod manager.** A new "Mod Profiles" entry on the main menu lets you keep separate,
  isolated sets of mods and switch between them (the game restarts into the one you pick). Build a profile from
  your installed mods, or browse and install straight from Thunderstore - dependencies included. Your real Mods
  folder is never touched, so external managers like r2modman keep working exactly as before. Side Hustle picks
  which profile to load at startup (a short prompt you can skip); pick "Full mod set" any time to go back to
  everything.
- **Vanilla Co-op with mod sync.** Host your own normal savegame as a public lobby (from the menu, or when you
  click Continue), and anyone can find it and join. When they do, Side Hustle compares their mods to yours and, if
  they differ, offers to set up a matching profile automatically - download from Thunderstore, restart, and drop
  straight back into your game. Mods that can't be fetched automatically get a simple checklist with a download
  link and an "open folder" button. You choose per-session whether to require synced mods.
- **Sync your mod settings too (optional).** As a host you can pick which mods' settings to apply to everyone who
  joins - it only affects the session, never their real settings, and anything that looks like a secret is left
  off by default.
- **Side Hustle Messenger.** A new phone app for chatting with the other players in your lobby - a shared lobby
  chat and private one-to-one conversations, with unread badges and native message notifications.
- **GitHub mods download themselves too.** Mods whose download link points at a GitHub repository are fetched
  automatically from the repo's releases (hash-verified against the host's exact file), so fewer mods end up on
  the manual checklist.
- **Hands-free manual installs.** For mods that still need a browser download (e.g. Nexus), the checklist now
  watches your Downloads folder (and Vortex's download folder, if you use Vortex): click the link, download the
  file, done - Side Hustle spots it, verifies it and installs it on its own. An "Open next link" button walks you
  through the list, each mod ticks off live with a toast, and if you grab the wrong version the row tells you
  exactly which one the host runs. Files with the right content are also reused across rejoins.
- **See what's being played right now.** The Side Hustle menu now also lists gamemodes you do NOT have installed
  that have live public lobbies, so you can discover new things to play. Opening one shows a "Download Mod" button
  (Host and Join stay greyed out until you install the mod). You can turn this off under "Show gamemodes you don't
  have" in the settings.
- **For gamemode authors:** a gamemode can declare a `DownloadUrl` (shown as the "Download Mod" link, opened in the
  Steam overlay) and set `Advertise = false` to keep its public lobbies out of that discovery list - e.g. while a
  mode is still in development. Advertising is on by default and only ever lists public lobbies.

### Changed
- **Manual downloads land on the mod's own Nexus page.** For a mod the host could not give a download link for,
  Side Hustle now looks the name up on Nexus: when it identifies exactly one published Schedule I mod, the
  checklist button opens that mod's page directly ("Open Nexus") instead of a search results list. Ambiguous
  names ("Mod Manager") and mods that aren't on Nexus still open the search, exactly as before. The same lookup
  gives a gamemode you don't have installed a working "Find it on Nexus" button when its host advertised no link.

### Fixed
- **Gamemode sessions show up on the public lobby list.** A hosted gamemode lobby now also lists itself on the Side
  Hustle website (public lobbies only - a friends-only session stays off it), with its gamemode, player count and
  what mods a joiner would need. Only vanilla co-op lobbies did that before.
- **Your lobby name survives the "Required mods only" restart.** Hosting a gamemode that curates its mod set restarts
  the game first, and the name (and the chosen preset label) you typed on the host form was dropped in the process -
  the lobby then showed up as just your player name, in the browser and on the website.
- **Synced mods no longer arrive without their shared libraries.** A mod whose Thunderstore package ships (or depends
  on) a shared library - PropHunt needs SteamNetworkLib, for example - was installed as a lone DLL, so the joiner
  ended up with a mod that could not load and an error every frame. The sync now fetches those library packages too
  and puts them into the session profile's own `Plugins`/`UserLibs`, seeded from your global ones, so your real
  install is still never written to.

### Notes
- Side Hustle now ships a small startup helper (`SideHustle.Boot`) that it installs into your `Plugins` folder on
  first run. To fully remove Side Hustle, delete that file and the `SideHustle_Profiles` folder alongside the game.
- Downloads only ever come from Thunderstore's official CDN or a mod's own GitHub releases, and every downloaded
  file is verified against the host's manifest hash before it is used. Mods are never sent between players directly.
- Side Hustle only ever reads your Downloads and Vortex folders - nothing there is moved or deleted.

## [1.7.0] - 2026-07-10

Set a custom display name for multiplayer sessions.

### Added
- **Custom display name (privacy).** Each gamemode has its own "Your name" field on its Host / Join screen. Fill it
  in before opening or joining a lobby and other players see that name - the in-game nametag over your character,
  scoreboards, and the server browser - instead of your Steam name. Leave it empty to use your Steam name. Per
  gamemode, per session, and never saved.

## [1.6.0] - 2026-07-09

Native bigger lobbies - Side Hustle now raises the co-op player cap on its own.

### Added
- **Bigger co-op lobbies, built in.** Side Hustle now seats far more than the vanilla 4 players by itself
  (default up to 32), and the host player-count slider opens up to match. No separate lobby mod required.

### Changed
- Larger lobbies no longer rely on the external BiggerLobbies mod. If you still run BiggerLobbies, the two
  cooperate and the higher cap wins.

## [1.5.3] - 2026-07-08

A multiplayer-focused update - public lobbies work with anyone, plus host controls, friend invites and smoother
mod-set switching.

### Added
- **Play with anyone, not just friends.** Schedule I normally kicks a joining player who isn't on the host's Steam
  friends list a few seconds after they connect - so public sessions were effectively friends-only. While you host a
  Side Hustle gamemode that kick is lifted, so friends and non-friends alike can join your public (or
  password-protected) lobbies. Normal co-op outside Side Hustle is unaffected.
- **Kick players.** The host can remove a player from the session from the gamemode's UI (a reusable framework
  control, so any gamemode can offer it).
- **Clients can invite friends too.** The Steam friend-invite (+) button in the pause-menu lobby panel now shows for
  everyone in the lobby, not just the host, and works past four seats (with BiggerLobbies).
- **A heads-up before a "Required mods only" host restarts the game.** Instead of restarting instantly, a short
  countdown appears (Restart now / Cancel) and restarts on its own when the timer runs out - so the restart is never
  a surprise.
- **Gamemodes can default to "Required mods only".** A gamemode can ask the Host form to pre-select the isolated
  "required mods only" set (the host can still switch it), so a mode that wants everyone on an identical set gets it
  by default.

### Fixed
- **"Required mods only" re-hosting is now solid.** Hosting a gamemode with only its required mods, returning to the
  menu, then hosting again now works reliably: each launch builds a fresh isolated profile (a locked leftover can
  never block it), the gamemode list comes back on its own, and a quick re-host no longer times out.

### Changed
- **Better connection routing for public lobbies.** Public P2P connections now allow all connection routes and warm
  up Steam's relay, so a join picks the best available path instead of falling back to relay-only.

## [1.5.0] - 2026-07-03

### Added
- **Gamemode presets.** Gamemodes can now ship named presets (like "Classic Hunt") that show up as a picker at
  the top of the host setup form. Pick one and it fills in all the settings for you - you can still tweak anything
  afterwards. The form auto-selects whichever preset best fits your lobby size, a preset can suggest a player
  count, and one whose headline mechanic isn't finished yet is flagged EXPERIMENTAL so you know what you're getting.
- **Name your lobby.** The host form has a lobby name field now, and that name (plus the mode you picked) shows on
  the server-browser cards, so joiners can tell sessions apart at a glance.
- **Session-hygiene flags for gamemode authors.** A gamemode can opt into a handful of world-cleanup switches
  instead of reinventing them: skip the new-game intro and character creator, stop vanilla quests from
  auto-starting (with an allow-list for your own guide quest), block saving for the session so a throwaway world
  never overwrites a real save, keep NPCs from reacting to gunfire, and turn off vanilla player death when the
  gamemode runs its own elimination. All opt-in, all off by default.
- **A heads-up when versions don't match.** When you join a session, Side Hustle compares your build of the
  gamemode against the host's and warns you - in the log and right on the browser card - if they differ. It's the
  classic "we're all on different versions" bug, now easy to spot. It only warns; it never stops you joining.

### Changed
- **Host form polish.** Settings can be grouped under section headers, there's a new compact dropdown for
  one-of-many choices, and the settings list scrolls with a smooth mouse-wheel glide.

## [1.4.0] - 2026-06-26

### Added
- **Host configuration screen.** Hosting a multiplayer gamemode now opens a native-style setup form: pick the
  exact player count (up to the lobby cap), a public or private (friends-only) lobby with an optional password,
  and any settings the gamemode itself exposes - sliders, toggles, choices and text fields - all handed to the
  gamemode at launch.
- **Restyled server browser.** The Join screen lists open lobbies as cards (host, player count, gamemode, and a
  lock for password-protected lobbies); a locked lobby asks for the password before joining.

### Changed
- **The mod policy is now a per-session choice on the host form.** When you host a gamemode that declares which
  other mods it works with, the setup form offers "Current installed mods" (the default - keep your full set) or
  "Required mods only" (run just the gamemode's mods in an isolated profile, after a confirmation listing the
  changes). Nothing in your real Mods folder is ever renamed, moved or deleted (junctions/hardlinks, no admin
  needed), so your mod manager stays in sync. Joining a session never changes your mods - that is the host's choice.

## [1.3.0] - 2026-06-26

### Changed
- **Conflict-free mod sets are now fully non-destructive.** When a gamemode declares which other mods it works
  with, Side Hustle launches it in a temporary, isolated profile that loads only those mods - your installed mods
  are never disabled, renamed, moved or deleted, so your mod manager stays in sync and a normal launch always
  loads everything. Leaving the gamemode restarts back to your full set. The confirmation still lists exactly what
  changes before anything happens, and a "Restore my mods" entry returns you to your full set at any time.

## [1.2.0] - 2026-06-25

### Added
- **Conflict-free mod sets.** A gamemode can declare which other mods it works with (a mod policy). When you
  launch it, Side Hustle shows exactly which mods it will pause and enable, then - on your confirmation -
  launches the gamemode in its own mod set. When you leave the gamemode your normal mods are restored. A
  "Restore my mods" entry is there if you ever need to put everything back yourself.

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
