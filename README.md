# Gungeon Online

*Super Duper Alpha please dont look yuck yuck*

**Online co-op for Enter the Gungeon** — a BepInEx mod that lets two players play together over **Steam** or **LAN**.

> ## ⚠️ Very early alpha — side project
>
> This is a **hobby side project**, built in spare time, and it is **pre-alpha quality**.
> Expect bugs, desyncs, crashes, softlocks and features that only half work.
> Things change (and break) between versions, and both players must run the **exact same version**.
> There is no support, no schedule and no guarantee it will ever be finished.
> **Back up your save files before trying it.**

## What works (roughly)

- Steam lobby hosting/invites, or LAN by IP / LAN search; pick your display name
- Seeing your partner: character, animations, gun, shadow, pet, ghost glow, status effects (fire, poison...)
- Host-led runs: same seed and floors for both players, run start/end and death-screen restarts
- Player bullets, thrown items (Molotovs, grenades), blanks, floor goop
- Shared enemy health/deaths, enemies targeting either player, room clears
- Co-op deaths: ghost mode, revive from chests, game over when both are down
- Tables (flip/push), breakables, rolling barrels, petting the partner's dog, shared pause, optional PvP

All of it is experimental. See `MODLOG.md` for the detailed (and honest) development journal, including known gaps.

## Not done / known problems

- Enemy *attacks* are simulated separately in each game (only targeting, health and deaths are shared)
- Items dropped on death aren't synced
- Many items, guns, bosses and special rooms have never been tested
- Performance and network smoothness still need work

## Requirements

- Enter the Gungeon (Steam version for Steam play)
- [BepInExPack for EtG](https://thunderstore.io/c/enter-the-gungeon/) (a mod manager like Gale installs it)
- Both players on the same Gungeon Online version

## Building

The mod is plain C# 5 compiled with the C# compiler that ships with Windows (no SDK needed).
`GungeonOnline/build.ps1` builds `GungeonOnline.dll` and a Thunderstore-style zip in `GungeonOnline/dist/`.
It currently expects the game and a BepInEx copy at the paths at the top of the script — adjust them for your machine.

## Credits

- [BepInEx](https://github.com/BepInEx/BepInEx) and HarmonyX; the game's own Steamworks.NET
- Built with heavy help from Claude (Anthropic), an AI assistant

## License

MIT
