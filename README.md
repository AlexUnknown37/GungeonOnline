# Gungeon Online

*Super Duper Alpha please dont look yuck yuck*

**Online co-op for Enter the Gungeon** — a BepInEx mod that lets **up to 4 players** play together over **Steam** or **LAN**.

> ## ⚠️ Very early alpha — side project
>
> This is a **hobby side project**, built in spare time, and it is **pre-alpha quality**.
> Expect bugs, desyncs, crashes, softlocks and features that only half work.
> Things change (and break) between versions, and every player must run the **exact same version**.
> There is no support, no schedule and no guarantee it will ever be finished.
> **Back up your save files before trying it.**

## What works (roughly)

- Steam lobby hosting/invites (not tested), or LAN by IP / LAN search; pick your display name
- Seeing the other players: character, animations, gun, shadow, pet, ghost glow, status effects, name tags
- Party list on the right side of the screen with every player's character, hearts and armor
- Host-led runs: same seed and floors for everyone, run start/end and death-screen restarts
- Player bullets, thrown items (Molotovs, grenades), blanks, floor goop
- Enemies: shared health and deaths; the game in charge of a room decides enemy attacks, targets and movement
- Teleport to fights, tougher enemies with more players, shared money and keys, optional split money
- Co-op deaths: ghost mode, revive from chests or the Cultist's Friendship Cookie, game over when everyone is down
- A player who drops out keeps playing as a bot with their gear until they rejoin (one bot at a time); Take a Break lets a bot play for you
- Status effects on enemies (freeze, charm, fire, poison...) happen in every game
- Bullets look the same for everyone, items and synergies included (tints, size, bounces, piercing, chain lightning, orbiting/spiral bullets, gun effects); all companions and orbitals show
- Same room decorations in every game
- Tables (flip/push), breakables, rolling barrels, petting other players' dogs, shared pause, optional PvP
- Loop The Gungeon: loops (with the time tube) and loop difficulty shared with everyone

All of it is experimental. See `MODLOG.md` for the detailed (and honest) development journal, including known gaps.

## Not done / known problems

- Enemy bullets with random spread or homing can still differ a little between games
- Items dropped on death aren't synced
- Pushed barrels and crates can end up in different spots in different games
- Only one bot can be hosted at a time: if several players drop out (even everyone), only the first gets a bot (the host's game has room for one extra character)
- Many items, guns, bosses and special rooms have never been tested
- Performance and network smoothness still need work

## Requirements

- Enter the Gungeon (Steam version for Steam play)
- [BepInExPack for EtG](https://thunderstore.io/c/enter-the-gungeon/) (a mod manager like Gale installs it)
- Every player on the same Gungeon Online version

## Building

The mod is plain C# 5 compiled with the C# compiler that ships with Windows (no SDK needed).
`GungeonOnline/build.ps1` builds `GungeonOnline.dll` and a Thunderstore-style zip in `GungeonOnline/dist/`.
It currently expects the game and a BepInEx copy at the paths at the top of the script — adjust them for your machine.

## Credits

- [BepInEx](https://github.com/BepInEx/BepInEx) and HarmonyX; the game's own Steamworks.NET
- Built with heavy help from Claude (Anthropic), an AI assistant

## License

MIT

## Support

If you enjoy this side project, you can support me or follow my other projects:

- Ko-fi: https://ko-fi.com/alexjustalex
- Patreon: https://patreon.com/AlexanderVazquez
- GitHub: https://github.com/AlexUnknown37
