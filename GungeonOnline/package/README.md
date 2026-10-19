# Gungeon Online

Online co-op for **up to 4 players** in Enter the Gungeon over **Steam** (play with friends anywhere, no port forwarding) or **LAN** (same home network).

> **Very early alpha — side project.** Built as a hobby in spare time. Expect bugs, desyncs and crashes; every player needs the exact same version; back up your saves first. No support or guarantees.

It can do this so far (all experimental):

**Playing together**
- Host a friends-only Steam lobby and invite up to 3 friends through the Steam overlay, or accept a friend's invite
- Host or join a LAN game by IP address, or find LAN games automatically
- See the other players: character, animations, gun, pet, ghost glow, status effects and name tags
- Party list on the right side of the screen: every other player's character, hearts and armor
- Host-led runs on the same seed: same floors, run start and end, Quick Restart from the death screen

**Shared world**
- Enemies: shared health and deaths; the game in charge of a room decides each enemy's attacks and movement, so everyone sees the same fight
- Status effects on enemies (freeze, charm, fire, poison, slow, bleed) happen in every game
- Bullets look the same for everyone, items and synergies included: tints, size, bounces, piercing, chain lightning, orbiting and spiral bullets, gun muzzle and reload effects
- Companions (all of them) and orbitals (Guon stones...) show for everyone
- Same room decorations in every game; bullets, thrown items, blanks, floor effects, tables, breakables and rolling barrels
- Shared money, keys and pause; optional PvP and split money
- Teleport to fights: when someone starts a fight, everyone else is pulled in (on by default)
- Tougher enemies with more players (+50% health per extra player by default; stacks with Loop The Gungeon)

**Deaths and drop-outs**
- Co-op deaths: become a ghost, get revived from a chest or with the Cultist's Friendship Cookie
- A player who drops out keeps playing as a bot with their gear; when they rejoin, their character is handed back. Only **one** bot at a time: the host's game has room for one extra character, so if several players drop out, only the first gets a bot
- Take a Break: a bot plays for you until you touch the controls again

**Other mods**
- Loop The Gungeon: loops, the loop's time tube and its difficulty settings are shared with everyone
- Custom Characters, Expand The Gungeon and Gungeon Go Vroom settings are matched where needed

## Requirements

- Enter the Gungeon (Steam play needs the Steam version of the game)
- BepInExPack for EtG (your mod manager installs it automatically)
- Every player needs the **same version** of this mod

## How to use

Use the **Multiplayer** button on the main menu, or press **F8** in game to show or hide the Gungeon Online window.

**Your name:** type it in the Name box and click **Save**. Leave it empty to use your Steam name.

**Steam:**
1. Host: click **Host on Steam**, then **Invite friend** and pick a friend in the Steam overlay.
2. Friends: accept the invite in Steam.

**LAN:**
1. Host: click **Host on LAN**. The window shows your LAN address (like `192.168.1.20:27460`).
2. Friends: click **Find LAN games** and pick the host, or type the address and click **Join**.
3. The first time you host, Windows may ask whether to let Enter the Gungeon through the firewall. Allow it on **private networks**.

The status changes to **Connected** and shows your ping.

**Keys:**
- **F8**: show or hide the Gungeon Online window
- **F6**, or **Take a Break** in the pause menu: let a bot play for you until you move again
- **F7**: accept a guest's request to restart the run

## Settings

In `BepInEx/config/gungeononline.multiplayer.cfg` (or your mod manager's config editor). The **host's** rules apply to everyone:

- `PlayerName`: the name others see (empty = Steam name)
- `ToggleWindowKey`, `TakeABreakKey`, `AcceptRestartKey`: the keys above
- `Port` (LAN): the UDP port for LAN games (default 27460; host and friends must match)
- `PvP`: players' bullets hurt each other (default off)
- `HalfMoney`: split boss money; a boss's money counts 1/players, since it drops in every game (default on). Enemies' money always counts in full
- `TeleportToFights`: pull everyone into a fight someone starts (default on)
- `ScaleEnemyHealth`, `HealthPerExtraPlayer`: tougher enemies with more players (default on, +50% each)
- `ShowPartnerName`, `ShowPartyList`: name tags and the party list (default on)

## Troubleshooting

- Each game session is saved to `BepInEx/GungeonOnline-logs` in your mod profile (the last 10 are kept). After a problem, send the newest file there; it isn't erased when you open the game again. Gungeon Online's lines start with `[Gungeon Online]`.
- If the game freezes, the same file says where (look for `[FreezeWatch]`). The host's file matters most.
- LAN game not found? Make sure all PCs are on the same network and the firewall allows the game, then try typing the address instead.
- Things look different for different players? Make sure everyone runs the same version of this mod and of any other gameplay mods.

## Credits

- Built on [BepInEx](https://github.com/BepInEx/BepInEx) and the game's own Steamworks.NET
- Built with help from Claude (Anthropic), an AI assistant

## License

MIT

## Support

If you enjoy this side project, you can support me or follow my other projects:

- Ko-fi: https://ko-fi.com/alexjustalex
- Patreon: https://patreon.com/AlexanderVazquez
- GitHub: https://github.com/AlexUnknown37
