# Two-copy LAN test helpers (from the audit)

Optional. These are the scripts the audit used to test the mod with two copies of the game on one PC, both
played by the mod's own bot, and to run console commands in either copy. They are test tools, never shipped
with the mod. Delete the folder if you have your own way of doing this.

## What is here

| File | What it does |
|---|---|
| `lan-test.ps1` | Starts a host copy and a guest copy (auto host / auto join on 127.0.0.1, bots on), each with its own BepInEx folder, runs them for `-Minutes`, closes both and keeps the logs under a `-Tag` |
| `game-cmd.ps1` | Sends Mod the Gungeon API console commands to a running copy: `-To host`, `guest` or `both` |
| `AuditCommands.cs` | Test-only plugin: reads `BepInEx\audit-commands.txt` four times a second and runs each line through `ETGModConsole.Instance.ParseCommand` (found by reflection, so it loads without the console too) |
| `SmallWindow.cs` | Test-only plugin: keeps a copy in a 960x540 window and silent. 960x540 exactly: other sizes broke the death screen |

## Setup

1. Pick a work folder and set `GUNGEON_AUDIT_DIR` to it (default `C:\Projects\GungeonOnline-audit`). Set
   `GUNGEON_DIR` to the game folder if it is not `C:\Steam\steamapps\common\Enter the Gungeon`.
2. Put the build under test at `<work folder>\build\GungeonOnline.dll`. `GungeonOnline\check.ps1` compiles to
   `%TEMP%\GungeonOnline-check.dll` without bumping the version; copying that file is enough.
3. Compile the two helper plugins into `<work folder>\build\` as `AuditCommands.dll` and `AuditSmallWindow.dll`,
   with the same compiler and references as the mod:

   ```powershell
   . .\GungeonOnline\paths.ps1
   $refs = Get-References
   & $Csc /nologo /noconfig /nostdlib+ /target:library /optimize+ "/out:$env:GUNGEON_AUDIT_DIR\build\AuditCommands.dll" $refs tools\audit-lan\AuditCommands.cs
   & $Csc /nologo /noconfig /nostdlib+ /target:library /optimize+ "/out:$env:GUNGEON_AUDIT_DIR\build\AuditSmallWindow.dll" $refs tools\audit-lan\SmallWindow.cs
   ```
4. `lan-test.ps1` expects Gale: BepInExPack 5.4.2101 in Gale's cache, and the modpack in the Gale profile
   `Default`. Each copy gets its own copy of that profile's plugins, with the build under test in place of the
   installed Gungeon Online. The profile itself is only read. `-Bare` leaves the other mods out.

## Use

```powershell
powershell -ExecutionPolicy Bypass -File tools\audit-lan\lan-test.ps1 -Minutes 8 -Tag my-run
# while it runs, from another shell:
powershell -ExecutionPolicy Bypass -File tools\audit-lan\game-cmd.ps1 -To both "godmode true" "spawn blobulon" "wait 1" "spawn ammomancer"
powershell -ExecutionPolicy Bypass -File tools\audit-lan\game-cmd.ps1 -To guest "give witch_pistol"
```

Logs end up in `<work folder>\lan-logs\<tag>-host` and `<tag>-guest`. Each command and the console's answer are
in that copy's `LogOutput.log` (`[cmd] ...`, `ETG Console`).

## Things that cost time

- The saves folder is shared by both copies; the script backs it up once a day to `<work folder>\save-backup`.
- A second copy quits at once unless `steam_appid.txt` (containing `311690`) is next to `EtG.exe`. The script
  creates it when missing and removes it again afterwards; one that was already there is left alone.
- The console's `spawn` makes the enemy **in that copy only**, in the room that copy's player is in. Send it to
  `both` while the two players are in the same room (after a "Fight started" line) or the two games get
  different enemies: that is the test's doing, not a sync fault.
- Item names are the console's own: `give shotgun` is "Invalid item ID". `hexagun`, `witch_pistol`,
  `magic_lamp`, `blobulon`, `ammomancer`, `bullet_kin` work.
- Comparing the games: every enemy made in play logs `Enemy made in play: <name> in room <n> is number <id>`.
  Sort those lines from both logs and diff them.
