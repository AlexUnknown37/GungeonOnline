# Gungeon Online

Online co-op for Enter the Gungeon over **Steam** (play with friends anywhere, no port forwarding) or **LAN** (same home network).

> **Very early alpha — side project.** Built as a hobby in spare time. Expect bugs, desyncs and crashes; both players need the exact same version; back up your saves first. No support or guarantees.

It can do this so far (all experimental):

- Host a friends-only Steam lobby and invite a friend through the Steam overlay, or accept a friend's invite
- Host or join a LAN game by IP address, or find LAN games automatically
- Pick the name other players see
- See your partner in game: character, animations, gun, pet, ghost glow and status effects
- Host-led runs on the same seed; bullets, thrown items, blanks and floor effects are shared
- Shared enemy health and deaths; enemies can target either player
- Co-op deaths: become a ghost, get revived from a chest
- Tables, breakables and rolling barrels; shared pause; optional PvP

Not synced yet: enemy attacks themselves (each game simulates them), items dropped on death, and much more.

## Requirements

- Enter the Gungeon (Steam play needs the Steam version of the game)
- BepInExPack for EtG (your mod manager installs it automatically)
- Both players need the **same version** of this mod

## How to use

Press **F8** in game to show or hide the Gungeon Online window.

**Your name:** type it in the Name box and click **Save**. Leave it empty to use your Steam name.

**Steam:**
1. Host: click **Host on Steam**, then **Invite friend** and pick a friend in the Steam overlay.
2. Friend: accept the invite in Steam.

**LAN:**
1. Host: click **Host on LAN**. The window shows your LAN address (like `192.168.1.20:27460`).
2. Friend: click **Find LAN games** and pick the host, or type the address and click **Join**.
3. The first time you host, Windows may ask whether to let Enter the Gungeon through the firewall. Allow it on **private networks**.

The status changes to **Connected** and shows your ping.

## Settings

In `BepInEx/config/gungeononline.multiplayer.cfg` (or your mod manager's config editor):

- `PlayerName`: the name others see (empty = Steam name)
- `ToggleWindowKey`: the key that shows/hides the window (default F8)
- `Port` (LAN section): the UDP port for LAN games (default 27460; host and friends must match)

## Compatibility

Gungeon Online doesn't change any game code yet, so it should work alongside other mods.

## Troubleshooting

- Check `BepInEx/LogOutput.log` in your mod profile. Gungeon Online's lines start with `[Gungeon Online]`.
- LAN game not found? Make sure both PCs are on the same network and the firewall allows the game, then try typing the address instead.

## Credits

- Built on [BepInEx](https://github.com/BepInEx/BepInEx) and the game's own Steamworks.NET
- Built with help from Claude (Anthropic), an AI assistant

## License

MIT
