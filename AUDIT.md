# Gungeon Online — engineering audit and fixes (2026-11-10)

Reviewed commit: `bcb228428121e28721bce74d0b5f35fce03abcf7` (`main`, version 0.161.0, protocol 49).
Work branch: `audit/fable-review` (local only; nothing pushed). Seven commits on top of the reviewed one.

This file is the audit ledger and the handoff report in one. Every claim below is labelled with how it was
checked. **A successful build is not a multiplayer test**: apart from one transport bug proven in a test harness
and a single-copy load test, the fixes here are *implemented and awaiting in-game verification*.

## Summary

The mod is in much better shape than "super duper alpha" suggests. The star topology is sound, the host stamps
the sender slot so guests cannot speak for each other, host-only orders are filtered in one place, every echo
guard is in a `try/finally`, save progress is borrowed in memory and never written to a guest's save, and the
diagnostics (session log, freeze watch, self-tests, NetStats) are better than in most hobby mods.

The most consequential problems found:

1. **LAN reliable stream could stop for good** (GO-01, High, proven). The receiver acknowledged packets it then
   threw away. Positions kept flowing, every game event stopped. Fixed, with a regression test.
2. **Offline local co-op was broken by having the mod installed** (GO-02, High for those players). Player 1
   dying got a forced game over three seconds later. Fixed.
3. **The host only tracked its run while a guest was connected** (GO-03). A friend joining late, or coming
   back after the host changed floor alone, got no floor or the wrong one. Fixed.
4. **A Steam host accepting an invite ended up connecting forever** (GO-04). Fixed.
5. A group of "state left behind" bugs: enemies frozen after a disconnect, a pause that nobody lifts, a
   stand-in's slot ignored by third players, stale level state in the next session (GO-05 … GO-11). Fixed.

Not fixed, and worth the author's attention next: beam damage is never shared (D-1), enemy IDs depend on each
game's spawn order (D-3), and receivers don't check that the sender actually leads the room (D-4).

---

## A. Scope and evidence

### Environment (verified on this machine)

| Item | Value | How checked |
|---|---|---|
| Game | Enter the Gungeon, Steam, `EtG.exe` built with Unity 2017.4.27 | file version of `EtG.exe` |
| Runtime | Mono, .NET 3.5 profile (game's own `mscorlib`) | `build.ps1` references, MODLOG |
| Loader | BepInExPack_EtG 5.4.2101 (BepInEx 5.4.21 + HarmonyX) | `manifest.json`, Gale cache |
| Compiler | `csc.exe` 4.8.9363.0, banner "for C# 5" | ran `csc /?` |
| Language limit | C# 5 only, **confirmed**. All changes are C# 5 (anonymous delegates, no `?.`, `$""`, `nameof`, `=>` members) | compiles with that compiler |
| Dependencies | none beyond the game, BepInEx, Harmony; Steamworks.NET is the game's own | references in `build.ps1` |

First-party code: `GungeonOnline/src/*.cs` (73 files, about 22,000 lines), the PowerShell scripts, the docs.
No vendor or generated code is committed (`.gitignore` keeps reference copies out).

### What was run

| Check | Result |
|---|---|
| Baseline compile of `bcb2284` (same flags as `check.ps1`, `/warn:4`) | 0 errors, 0 warnings |
| Compile after every batch | 0 errors, 0 warnings |
| `LanLink.SelfTest` (the mod's own) before and after | passes both |
| New transport tests (`GungeonOnline/tests/run-tests.ps1`) on `bcb2284` | **2 of 4 checks fail** (GO-01 reproduced) |
| Same tests on the branch | 4 of 4 pass |
| Load test: one copy of the real game, isolated BepInEx folder, audited DLL, to the title screen and Breach setup | plugin loads, every Harmony patch applies, both self-tests log OK, no Gungeon Online error. Two HarmonyX warnings (`RobotPastController.Update`, `ExpandTheGungeon…` type lookup) are present on the original code path too and are handled by the mod |

### What was not run

- No two-copy session, no Steam session, no run was started. Nothing in sections B–D is verified in play
  unless it says so.
- No profiler measurements. Section C separates "the code does X per frame" (read) from "X costs Y ms" (not measured).
- `build.ps1` itself was not run (it bumps the version and writes a zip); `check.ps1` was.

### Depth of review

Read completely by me: `Protocol`, `Transport`, `LanLink`, `SteamLink`, `NetSession`, the build scripts and docs.
Read completely by four parallel reviewers, whose candidate findings I then re-read in the code before acting:
every other file in `src/`. Game internals were checked against a local decompile of `Assembly-CSharp.dll`
where a finding depended on them (noted per finding as "decompile").

---

## Architecture map

```
Plugin (BaseUnityPlugin)
  Awake : SessionLog, FreezeWatch (the only background thread), LanLink.SelfTest, config,
          4 RemotePuppet + 4 PetPuppet, NetSession, then 39 module Init() calls.
          Harmony: no global PatchAll; each module patches its own class.
  Update: NetSession.Update (poll links, dispatch) -> every module's Update -> puppets.
          Everything runs on the Unity main thread.
  Quit  : SaveSync.EndBorrow -> NetSession.Leave (Bye) -> LanDiscovery.Stop

Transport (abstract)                      NetSession
  SteamLink  legacy SteamNetworking P2P     slots: host 0, guests 1-3, star topology
             friends-only lobby             Hello(version, name, slot, key) -> Roster
             reliable = k_EP2PSendReliable  host: stamps sender slot, dispatches, relays
  LanLink    UDP, "GO" magic                guest: trusts host; HostAuthority types only from slot 0
             reliable = seq + epoch + ack   30 Hz PlayerState, 1 Hz Ping, 20 s timeout
             + 0.2 s resend, in order       Disconnect: host keeps session; StandIn bot may take the slot
```

Delivery guarantees (checked in code, not assumed):

- **Reliable** (everything not listed below): per peer, in order, exactly once. LAN implements this itself.
- **Unreliable** (`PlayerState`, `Shot`, `EnemyStates`, `Orbitals`, `Companions`, `Courses`, `Beams`, `Reload`):
  may be lost, reordered or duplicated on LAN. `PlayerState` carries the sender's clock and drops older ones;
  the others are "latest wins" or one-shot.
- Relayed messages keep their reliability class. Reliable order is per sender→host→receiver hop, so two guests'
  messages have no order relative to each other.

One action, end to end (a guest shoots an enemy):

```
guest B: Gun.ShootSingleProjectile postfix -> BulletSync queues -> Shot (unreliable) -> host
host   : HandlePacket stamps slot B, OnShot spawns an ownerless copy (no damage), relays to guest C
guest B: bullet hits -> HealthHaver.ApplyDamage postfix, LocalBulletHitting -> EnemyDamage(id, guid, amount) reliable
host, C: OnEnemyDamage -> Find(id, guid) -> ApplyDamage under applyingRemote (no echo)
death  : whoever's game kills it sends EnemyDied; others kill their copy and remember the ID for the floor
room   : the lowest-slot living player in the room "leads" it: sends EnemyStates 15 Hz, AttackStart, RoomCleared
```

Authority at a glance:

| Thing | Who decides | Checked by receiver |
|---|---|---|
| Level loads, run end, rules, save progress, team wipe, wallet total, duel result | host | yes: type filter + slot 0 |
| Enemy positions, attacks, room cleared | room leader (lowest living slot in the room, each game computes it) | only "ignore if I lead it myself" (see D-4) |
| Enemy damage / death | whoever hit it | level, ID and enemy kind |
| Money and keys | host total; guests send deltas | host clamps (new) |
| Player state | each player; host for its stand-in | slot stamped by host |

---

## B. Findings and outcomes

Severity: Critical / High / Medium / Low as defined in the brief. Confidence: **Verified** (reproduced, or a
complete code path), **Strong** (substantial code evidence, one stated assumption), **Open** (needs investigation).
Outcome: **Fixed+tested**, **Implemented** (awaiting in-game check), **Deferred**, **Rejected**.

Line numbers refer to the reviewed commit `bcb2284`.

### GO-01 — LAN: reliable stream stops permanently after a burst — High, Verified (reproduced) — Fixed+tested

- Where: `LanLink.HandleDatagram`, `case PacketKind.Reliable` (`src/LanLink.cs:253-281`).
- Cause: the ack was sent first, then a packet more than `MaxAhead` (1024) beyond the next expected one was
  discarded. An acknowledged packet is removed from the sender's `Unacked` list and never sent again, so the
  receiver waits for that sequence number forever. Unreliable traffic continues, so nothing times out: players
  keep moving but no shot damage, death, level load or chest arrives. MODLOG's "half-connected" symptom fits.
- Trigger: one reliable packet missing while more than 1024 later ones arrive. The realistic case is a peer
  frozen on a level load (it doesn't poll; Windows' UDP buffer overflows) while the others fight; the sender
  also re-sent its whole backlog every 0.2 s, which makes the overflow worse. I did not measure how often a real
  session reaches 1024; the mechanism itself is reproduced.
- Fix: acknowledge only what is stored; re-send at most the 128 oldest unacknowledged packets per round.
- Test: `tests/LanLinkTests.cs` `AckedPacketsAreNeverDiscarded` — 75 packets acked-and-lost before, 0 after;
  stream complete and in order after re-sends.
- Remaining: `Unacked` is still unbounded if a peer stops acknowledging but keeps sending (ends at the 20 s timeout
  only if it also goes silent).

### GO-02 — Offline local co-op: forced game over 3 s after player 1 dies — High (for local co-op), Verified (code path) — Implemented

- Where: `DeathSync.Update` (`src/DeathSync.cs:172-207`), called every frame from `Plugin.Update`.
- Cause: "a ghost with nobody connected ends its own run" had no check that the ghost came from an online
  session. In the game's own couch co-op, player 1 as a ghost with player 2 alive matched it.
  Assumption: `HandleDeath_CR` ends the run with a living second player (the mod relies on exactly that for its stand-in).
- Fix: `onlineGhost` is set only where the mod turns us into a ghost (`BeforeDie`) and cleared when alive again
  or on a new run. Also: a Steam guest whose connection is being retried gets 30 s instead of 3 s.
- Check in game: mod installed, offline, two local players, kill player 1. Expected: player 2 plays on.

### GO-03 — Host forgets its run while nobody is connected — Medium/High, Verified (code path) — Implemented

- Where: `LevelSync.BeforeLoadNextLevel`, `BeforeLoadCustomLevel`, `BeforeReturnToFoyer`, `BeforeLoadCharacterSelect`
  (`src/LevelSync.cs:391, 436, 519, 540`), all gated on `IsConnectedHost()` = `Status == Connected`.
- Effects: (a) host opens a lobby and starts the run before the friend arrives: no seed is rolled and
  `hostLevel` is null, so the friend is held with "Waiting for … to start the run" until the host's next floor.
  (b) the last guest drops, the host goes down a floor, the guest returns: it is sent the floor the host left.
  (c) host back in the Breach alone: `hostLevel` keeps the old run.
- Fix: gate on "is hosting" (`IsHosting()`), not "has a connected guest". Sending is already a no-op with nobody connected.
- Side effect to know about: a host with an open lobby and no guest now plays a seeded run (as it already did
  with a guest). MODLOG says the game reads `IsSeeded` only in loot code.
- Check: host starts a run alone, guest joins on floor 1 and takes the elevator; host alone changes floor, guest rejoins.

### GO-04 — Steam: accepting an invite while hosting on Steam never connects — Medium, Verified (code path) — Implemented

- Where: `SteamLobby.Join`/`OnJoinRequested`, `NetSession.InitSteam`/`Use`/`HandlePacket`.
- Cause: only the Steam lobby was left; `NetSession.Leave` was not called, `Use(Steam)` returned early, so
  `Protocol.LocalSlot` stayed 0 and every packet from the new host was dropped as "our own". `lastReceivedAt`
  was still refreshed, so no timeout either. If the join failed, the session stayed as a host with no lobby.
- Fix: `NetSession.JoinSteamLobby` ends the current session first; the invite callback goes through it.

### GO-05 — Third players ignore a slot after a stand-in takes over or hands back — Medium (3–4 players), Verified (code path) — Implemented

- Where: `NetSession.ReadPlayerState` gate `sentAt < Remote.SentAt` and `ReadRoster`.
- Cause: the stand-in's state is stamped with the host's clock, the player's with their own. Whichever is lower
  looks "older than the newest" on the other guests, who drop every update for that slot until the floor changes.
- Fix: a jump of more than 5 s in a slot's clock restarts the gate and the motion buffer. A NaN clock is rejected.

### GO-06 — Enemies stay frozen after a disconnect — Medium, Verified (code path) — Implemented

- Where: `EnemySync.Update` early return before `ApplyCorrections`; `Release` is the only undo.
- Fix: when not connected, release every driven enemy and clear corrections.

### GO-07 — A kill from another game is lost if the local copy can't take damage right then — Medium, Verified (decompile) — Implemented (partly)

- Where: `EnemySync.KillFromPartner`. The game's `ApplyDamageDirectional` still refuses on `PreventAllDamage`,
  `!vulnerable`, first frame, `minimumHealth`, armor, and deals 1 for counted-hits enemies.
- Fix: retry for 10 s (every 0.1 s, every frame in a sealed room), then log once.
- Not done: forcing `minimumHealth`/armor to zero for a boss that stays protected longer. That changes boss
  behaviour and needs an in-game test first.

### GO-08 — RoomCleared sent for rooms that did not open — Medium, Strong — Implemented

- Where: `EnemySync.AfterUnsealRoom`, `UnsealIfStuck`. `RoomHandler.UnsealRoom` returns early for NPC-sealed
  rooms (decompile) but the postfix still ran; the local "stuck for 2 s" guess was broadcast as a clear.
  Receivers then remove every later wave in that room for the rest of the floor.
- Fix: no message if the room is still sealed; the local guess is not broadcast.
- Assumption to check in game: which rooms actually hit this (challenge shrines, NPC rooms).

### GO-09 — Level state survives the session — Medium, Verified (code path) — Implemented

- `hostLevel`, `ourLevel`, `WaitingForHost`, `WaitingToJoin` and friends were never cleared on leave/disconnect.
  The next session started out believing its host was in the old run (auto-"Continue" from the title, no hold at
  the elevator, overlay left up). Fix: `LevelSync.OnSessionEnded()` from `NetSession.ResetAll`.
- Same family, also fixed: `DuelSync.toBreachAt`/`privateLoad` not cleared in the Breach (threw a guest out of the
  next run's first floor); `LoopSync.loopTarget`/`loopCountedFor` never reset (loop start skipped on a later run);
  `LevelSync.bypassWait` set but never consumed (let one later exit go ahead of the host).

### GO-10 — A host load that arrives during our own load is never followed — Medium, Verified (drop) — Implemented (part a)

- `OnLevelLoad` stored `missedLoad`, but only a player waiting at an exit ever consumed it. Fix: `FollowMissedLoad`
  follows it once our load is done (not if it is the floor we just loaded, not after 120 s).
- Deferred (D-7): the "host starts a new run while we're still loading" branch runs before the loading check and
  can start a second load.

### GO-11 — Pause left behind by a player who dropped — Medium (3+ players), Strong — Implemented

- With a stand-in in the slot, the other guests never saw the slot leave, so "A paused the game" stayed with time
  stopped. Fix: on a guest's disconnect the host sends `Paused(false)` in that slot's name.

### GO-12 — Pet puppet: a failed build is retried and logged every frame — Medium when it happens, Verified (code path) — Implemented

- A partner's companion that doesn't exist in our game (an item mod only they have) gave about 60 warnings a
  second, each written to two log files. Fix: remember the failed guid for 30 s, at most two builds a second.
  `RemotePuppet` got the same retry limit.

### GO-13 … GO-22 — smaller defects, all Implemented

| ID | Sev / Conf | What | Fix |
|---|---|---|---|
| GO-13 | Low-Med / Strong | Tables: an earlier pusher re-sent its old position every 3 s forever; two pushers could slide a table back and forth | taking a remote position ends our resync of that table |
| GO-14 | Low-Med / Verified | `Autopilot.Brain.waypoints` was `static`: a stand-in and Take a Break shared one path | per instance |
| GO-15 | Low-Med / Verified (write), Strong (persistence) | Host's Turbo/Rainbow written into the guest's stats and Challenge mode left on | own values saved on first host rules, restored at session end and on quit |
| GO-16 | Low-Med / Verified | Stand-in hand-back position had no floor or age: a returning player could be warped to old coordinates | stored with level and time; ignored on another floor or after 2 min |
| GO-17 | Low / Open | Stand-in destroyed by the game left its brain and two-player mode behind | detected and cleaned up |
| GO-18 | Low-Med / Verified (decompile) | A charmed enemy's bullet hitting an enemy was reported as "our hit" by every game: damage × players. Bullet copies rolled their own transmogrify | enemy-owned bullets are not reported; copies can't transmogrify |
| GO-19 | Med-Low / Strong | Host a ghost + living stand-in: all damage in the host's game was dropped, enemies in the bot's room were invulnerable | ghost block lifted while the stand-in lives |
| GO-20 | Low / Verified | Session: joiner's "no answer" timeout was pushed back by the game's uptime; a LAN address that sent one packet but never a valid hello held a guest slot forever; a hello from another version was parsed before the version check; neither side was told about a mismatch | idle clock kept current; slot freed after 10 s; version read and checked first; host answers once, both sides get a toast |
| GO-21 | Low / Verified | Untrusted values (needs a connected, modified client; the host relays them): NaN damage made an enemy unkillable for everyone; NaN positions/clock; orbital count 255, beam count 255; huge blank radius; wallet delta overflow; unbounded names and LAN search results | `Protocol.Finite`, caps and clamps at each site |
| GO-22 | Med / Strong | `EnemyStates` is 29 bytes an enemy; from 42 enemies it passes Steam's documented 1200-byte limit for unreliable sends and would not be sent (limit from Steam's docs, not tested here) | split into messages of 40 |

### Deferred (real, not changed here)

| ID | Sev / Conf | What | Why deferred |
|---|---|---|---|
| D-1 | Medium / Verified (decompile) | Beam damage is never shared: `BasicBeamController.FrameUpdate` calls `ApplyDamage` outside `Projectile.HandleDamage`, so `LocalBulletHitting` is false. A non-leader's beam only shows up as a sudden death | new feature (needs batching per enemy at 10–15 Hz), wants play-testing |
| D-2 | Low-Med / Verified | Bullet copies still roll the prefab's own poison/fire/freeze/charm dice; a DoT that lands in one non-leading game kills there and the death is not reported. Transmogrify is not synced at all | touching status flags on copies can change how items look and feel; needs a test plan |
| D-3 | Low / Verified | Enemy ID = room × 1000 + registration order in *that* game. A one-sided spawn shifts later IDs; a remembered dead ID kills whatever later gets it, without a kind check | needs a small data change (`deadIds` → id+kind) and testing with summoners/splitters |
| D-4 | Low (friends-only) / Verified | Receivers don't check that the sender of `EnemyStates`/`AttackStart`/`RoomCleared` is alive and in that room. Any connected player can clear any room | cheap check exists (`LowerPlayerInRoom`-style); risk of rejecting legitimate door-overlap traffic, so test first |
| D-5 | Low-Med / Verified | Remembered gear and the stand-in are keyed by a string the client sends. On LAN it is the display name, default "Player": two such guests share one entry | wants a design choice (Steam ID from the transport; a saved random ID on LAN) |
| D-6 | Medium / Verified | A normal chest is used up as a "revive" when the dead player is on their death screen, not a ghost (the state message has one "dead" bit, all 8 flag bits in use). Partly fixed: only a ghost on our floor counts | full fix needs a protocol change |
| D-7 | Medium / Strong | Host starts a new run while a guest is mid-load: `RestartOntoHostRun` clears the loading flag and starts a second load | the author's own notes say two loads hang the game; reorder needs testing |
| D-8 | Low / Verified | Host Quick Restart while a guest waits at an exit: the guest follows with a plain load and keeps its gear | interacts with the exit-wait state; small, but untested |
| D-9 | Low / Verified | Guests count the stand-in as a partner: Master Round given although everyone was hit; split money adds up to 5/6 with host + guest + bot | wants the divisor in `Rules` |
| D-10 | Low / Verified | Wallet change made while the host is on a loading screen is dropped | one-line change, but touches money; test with a purchase during a host load |
| D-11 | Low / various | Key items found while a stand-in plays aren't given to it; stand-in's shots can get the host banned from a shop (Open); Take a Break button may not be relabelled a second time (Open); `PetStart` not undone if the petter drops (Strong) | small, each needs an in-game look |
| D-12 | Low / Verified | `Plugin.Awake` has no per-module isolation: one Harmony target that fails to resolve stops every later module, half-loading the mod. `Plugin.Update` likewise: one exception per frame skips everything after it | 39 mechanical edits; low likelihood while the game version is frozen |
| D-13 | Low / Verified | `ModSettingsSync` rewrites the guest's Gungeon Go Vroom config on disk and never restores it (deliberate per MODLOG 0.90) | design decision; suggest saving the old values |
| D-14 | Low / Strong | `MotionBuffer` clock offset is pulled late by the receiver's own hitches (partner jumps back, then replays fast) | cosmetic; smoothing feel needs eyes on it |
| D-15 | Low | `FreezeWatch` reports a tabbed-out offline game as a freeze; LAN search only uses 255.255.255.255 (one adapter on multi-adapter PCs, Open) | diagnostics polish |

### Needs investigation (not findings)

- Duplicate `Revive` with 3+ players after a boss: every living player sends one. Depends on when the game clears `IsGhost`.
- Host is a ghost, a living guest is held at the exit: can the ghost host trigger the exit at all? Possible softlock.
- `MenuGuard.BeforeUnpause` blocks every `Unpause` while the Multiplayer menu is open, including the game's own.
- `SaveSync.OnProgress` deserializes host-sent JSON with no size cap (host is trusted; still worth a cap).

### Rejected after checking (selection)

- Guests forging host orders (`LevelLoad`, `EndRun`, `TeamWiped`, `WalletState`, `InventoryRestore`): blocked by the
  type filter and the slot stamp. `OnTeamWiped` also checks `From == 0`.
- Wallet echo or double-apply: deltas are polled, and both handlers re-baseline.
- `Protocol.SendAs` left set after an exception: all users reset it in `finally`.
- Host progress written into a guest's save file: `Save` is wrapped, the borrow ends on disconnect, timeout and quit.
- Time scale stuck after leaving: cleared every frame while not connected (GO-11 was the one exception).
- Out-of-range gun sprite, gun ID, clip name, identity crashing a puppet every frame: all clamped or null-checked (decompile).
- A guest making peers instantiate arbitrary enemy prefabs through the pet guid: the prefab is only loaded to read its animations.
- `FreezeWatch` data race / Unity API off the main thread: volatile fields, single writer, no Unity calls.
- Threading bugs in general: there is exactly one background thread and it touches nothing shared unsafely.

---

## C. Performance

Nothing here was profiled. "Read" means the code path is certain; the cost in milliseconds is not known.
Ranked by expected practical value.

| # | Status | Workload | What the code did (read) | Change | Risk |
|---|---|---|---|---|---|
| P-1 | Implemented | a partner's blank/explosion breaks many pots (one reliable `Break` each) | each message: `FindObjectsOfType` over the scene + 3 strings per candidate; a miss did a second search and a log line | game's own `AllMinorBreakables`/`AllMajorBreakables` lists first, scene search only as fallback; distance before name; one miss explanation per 2 s | an object not yet in the game's list falls back to the old search |
| P-2 | Implemented | every frame, **also offline** | `DuelSync.Update`: `FindObjectOfType<CoopPastController>()`; `PartnerMapMarker`: `Minimap.Instance` (searches when null); guests in the Breach: `FindObjectOfType` first in a condition; idle bot: chest + pickup scene searches every frame | cached with a 0.25 s re-look; asked only when connected; moved last; once a second | arena noticed up to 0.25 s later |
| P-3 | Implemented | long sessions | `remoteProjectiles` and `BulletMotion.moved` kept one entry per remote bullet for the life of the process (copies are destroyed, not pooled — decompile) | pruned every 2 s once above 256 | none expected |
| P-4 | Implemented | partner fires a modded item whose sprite sheet / chain effect we don't have | `Resources.FindObjectsOfTypeAll` per shot | negative result kept for 10 s | a late-loaded sheet shows up to 10 s later |
| P-5 | Implemented | busy room, follower side | `TargetSync.Force` walked all chased enemies once per enemy per `EnemyStates` (n² at 15 Hz) | `Update` already applies it the same frame | none expected |
| P-6 | Implemented | every frame, offline too | 4 status strings a frame in `RemotePuppet.Update`, 8 culture-sensitive `StartsWith` | constant strings, `IsShown` bool | debug window no longer shows the partner's level name in the hidden reason |
| P-7 | Implemented | LAN, peer frozen on a load | whole unacknowledged backlog re-sent every 0.2 s | 128 oldest per round | slower catch-up after very long freezes |
| P-8 | Not done | every bullet hit | `EnemyDamage` is 47 bytes of which 33 are the enemy guid string; `KindHash` (4 bytes) is already used in `EnemyStates` | protocol change (bump `Protocol.Version`) | — |
| P-9 | Not done | shotguns | one `Shot` datagram per pellet; per shot about 8 `GetComponent` + allocations in `BulletLook.Read`; `SpareTeammatesIn` reflection per received shot | batch per frame; cache per prefab | protocol change for batching |
| P-10 | Not done | host with a bot | `Autopilot.FillActions`: new dictionary, 3 reflection reads, boxed args, `MethodInfo.Invoke` per input tick; `OnlineBodies().ToArray()` up to 27 times a frame | cache per action set, delegates | — |
| P-11 | Not done | 15 Hz | `EnemySync.Update` allocates two lists + closure and walks every enemy of the floor (dead ones stay in `byId`); `EffectSync.Index()` rescans all items every 10 s for an effect it can't find; `BulletSync.FindPrefab` rescans per unknown code | reuse lists; negative caches | — |

Profiling plan (what I'd measure before touching P-8…P-11): the mod already has `--gungeon-online-allocprof`
and NetStats. Two copies on LAN with `-Lag 35`, a busy Forge room, 60 s: record GC allocations per frame from
`AllocProfiler`, bytes/s and messages/s per type from NetStats, and frame time spikes from the session log,
before and after. For P-1: one copy breaks 30 pots with a blank; compare the other copy's worst frame time.

Bandwidth (computed from the writers, not measured): `EnemyStates` = 8 + 29·n bytes at 15 Hz (10 enemies ≈ 4.5 KB/s,
20 ≈ 8.8 KB/s) per leader, relayed by the host to each other guest. `PlayerState` at 30 Hz is the other steady stream.

---

## D. Validation and test matrix

Commands actually run:

```
GungeonOnline\check.ps1                  -> exit 0 (after every batch)
GungeonOnline\tests\run-tests.ps1        -> 4/4 pass (2/4 on the reviewed commit)
one game copy, isolated BepInEx, audited DLL -> loads, patches apply, self-tests OK
```

Changes players or the author will notice:

- No packet format changed; `Protocol.Version` stays 49. Mixed old/new copies still talk, but everyone should
  update together as the README already says (a new host now sends `EnemyStates` in several messages and
  `Paused(false)` for a dropped guest; an old guest handles both).
- Version number untouched (`build.ps1` bumps it when the author builds).
- A host with an open lobby and no guest now gets a seeded run (GO-03).
- Build: `paths.ps1` finds the game and BepInEx; the hard-coded `S:\` path is still tried first.
- New: `GungeonOnline/tests/` (not compiled into the mod).

Manual matrix for the fixes (pass = the stated observation; all on two or more copies):

| # | Scenario | Players / link | Steps | Pass when |
|---|---|---|---|---|
| T1 | Offline couch co-op (GO-02) | 1 PC, 2 local players, mod installed, never online | player 1 dies | player 2 keeps playing; no death screen after 3 s |
| T2 | Late join (GO-03) | 2, LAN and Steam | host hosts, starts a run alone, guest joins and takes the elevator | guest lands on the host's floor with the same layout (FloorCheck logs a match) |
| T3 | Rejoin after floor change (GO-03) | 2 | guest quits mid-run, host goes down a floor, guest rejoins | guest arrives on the host's current floor |
| T4 | Load storm (GO-01) | 3–4, LAN, `-Lag 35` | one copy reloads the floor (or joins late) while the others fight a full room for 20 s | after the load, shots still damage, chests and level loads still sync; log has no "half-connected" signs |
| T5 | Invite while hosting (GO-04) | 2, Steam | A hosts, B hosts, A accepts B's invite | A connects to B within seconds |
| T6 | Stand-in handover (GO-05, GO-11) | 3 | B pauses, then kills its game; later rejoins | C is not frozen; C sees the bot animate and shoot, then B again after the rejoin |
| T7 | Disconnect in combat (GO-06) | 2 | guest loses the host mid-fight (kill the host) | enemies walk and attack normally on the guest |
| T8 | Boss kill across games (GO-07) | 2 | guest kills a boss while the host's copy is between phases (Dragun, Gatling Gull last stand) | boss dies on both within 10 s; rewards drop on both |
| T9 | Sealed NPC room (GO-08) | 2 | challenge shrine / NPC-sealed room with waves | later waves are not auto-killed on the other copy |
| T10 | Guest joins mid-load (GO-10) | 2 | guest takes the elevator, host leaves the floor during the guest's load | guest follows to the host's new floor right after its load |
| T11 | Session hygiene (GO-09, GO-15) | 2 | leave a session while the host is in a run, join another host in the Breach; host has Turbo on, guest off | no auto-Continue, held at the elevator as usual; guest's Turbo is off again after leaving |
| T12 | Tables (GO-13) | 2 | A pushes a table, later B pushes the same one | table stays where B left it on both for 10 s |
| T13 | Crowded room (GO-22) | 2, Steam | room with 45+ enemies (Loop mod) | followers still get position updates |
| T14 | Version mismatch (GO-20) | 2, LAN | join with a build of another protocol version | both sides get a toast at once; joiner is offline again, not "Connecting" |
| T15 | Long session (P-3) | 2 | 30 min of play | NetStats/AllocProfiler show no steady growth; no new hitch pattern |
| T16 | Malformed input (GO-21) | 2, test build | send `EnemyDamage` with NaN, `Orbitals` with count 255 | ignored; enemy still killable; no log flood |

Regression checks worth doing because the fix touches a hot path: breaking pots as a guest still breaks them
on the host (P-1); ghost host cannot kill with the ghost pulse when **no** stand-in exists (GO-19 unchanged path).

---

## E. Remaining work, in order

Fix before wider testing:

1. Play through T1–T7 (an evening with two copies and `launch-test-copy.ps1`). Estimate: 2–3 h.
2. D-7 (second load while loading) and D-6 (chest used up for a death-screen player). Each about an hour plus testing;
   D-6 needs a protocol bump.
3. D-1 beam damage. Half a day including batching and a boss test.

Fix next for reliability and smoothness:

4. D-3 kind check on remembered dead IDs (1–2 h), then D-4 sender-leads-room check (1–2 h, test at doors).
5. D-5 identity from the transport on Steam, saved random ID on LAN (2 h).
6. P-8 and P-9 together under one protocol bump (half a day); measure first with NetStats.
7. D-9, D-10, D-11 small logic fixes (an hour each).

Longer-term simplification:

8. One place for "session ended" clean-up. This audit added `LevelSync.OnSessionEnded`; a dozen modules still
   watch `session.Link == null` in their own `Update`. A single `SessionEnded` event would have prevented GO-06,
   GO-09 and GO-15.
9. One helper for "who leads this room" used by senders *and* receivers (EnemySync, AttackSync, RoomCleared).
10. Per-module try/catch in `Plugin.Awake` and `Plugin.UpdateParts` (D-12): mechanical, makes a single failing
    module visible instead of silently disabling everything after it.
11. More tests of the same kind as `tests/LanLinkTests.cs`: `Protocol`, `MotionBuffer` and the `NetSession`
    host filter compile outside the game with small stubs.

All estimates are rough.

---

## F. Coverage and open questions

Reviewed: transport, session, protocol, level/run lifecycle, death/revive, pause, save sync, loops, duel, enemy
sync and attacks, bullets/beams/blanks/throws, loot/wallet/inventory, world objects, stand-in and autopilot,
puppets and UI, diagnostics, build scripts.

Blind spots:

- Everything that needs the game running with two players (all of section D).
- Other mods' interaction (Loop The Gungeon, Expand, Custom Characters, Gungeon Go Vroom) was read, not run.
- Steam specifics: the 1200-byte unreliable limit, relay behaviour and lobby owner migration are from Steam's
  documentation and the code, not observed.
- Bosses and special rooms individually (the README already lists these as untested).
- Real message rates. The 1024-packet window in GO-01 and the bandwidth figures are computed, not measured;
  one NetStats capture of a 4-player fight would settle both.

## Ledger of changes

| Commit | Contents |
|---|---|
| `4f47d7a` | GO-01, GO-20 |
| `70abfc8` | build script portability (`paths.ps1`) |
| `ee9b353` | GO-13, GO-14, GO-16, GO-17, P-1, parts of P-2, wallet clamp |
| `6705825` | GO-04, GO-05, GO-12, P-2, P-6, caps in GO-21, quit ordering |
| `db9dae9` | GO-02, GO-03, GO-09, GO-10, GO-11, GO-15, D-6 partial |
| `f7b9489` | GO-06, GO-07, GO-08, GO-18, GO-19, GO-21, GO-22, P-3, P-4, P-5 |
| (this commit) | `tests/`, README build notes, this file |
