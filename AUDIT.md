# Gungeon Online — engineering audit and fixes (2026-11-10)

Reviewed commit: `bcb228428121e28721bce74d0b5f35fce03abcf7` (`main`, version 0.161.0, protocol 49).
Work branch: `audit/fable-review` (local only; nothing pushed); the commit list is at the end of this file.

This file is the audit ledger and the handoff report in one. Every claim below is labelled with how it was
checked. It has two parts: the first pass (10 November, read and fix, no multiplayer run) and a **second pass
(11 November)** that used a decompile of the game and real two-copy LAN sessions. The second pass is the next
section; where it changes something said later in this file, it says so.

**A successful build is not a multiplayer test.** What the two-copy sessions exercised is listed in the second
pass. Everything else is still *implemented and awaiting in-game verification*.

---

## Second pass (11 November): decompile, two-copy sessions, sync

### What was different this time

- **Game code with method bodies.** A full decompile of `Assembly-CSharp.dll` (ILSpy) was used to check every
  point the first pass had marked as an assumption, and to design the sync changes. The signature dumps in the
  author's decomp folder were useful as an index; they have no method bodies.
- **Real sessions.** Two copies of the game on one PC, isolated BepInEx folders, the mod's own test flags
  (`--gungeon-online-test host|join`, `--gungeon-online-test-run`, `--gungeon-online-bot`, `--gungeon-online-lag 35`),
  both copies played by the mod's bot. Five sessions of 2 to 8 minutes. Save folder backed up first.
- **An independent review of this branch's own diff**, which found four regressions (below).

### What the sessions showed

| Session | Build | Result |
|---|---|---|
| 1 (6 min) | first-pass branch | Connect, same floor (fingerprint match), six fights, Gatling Gull killed on both, 0 mod errors. **9 of 19 "partner broke X" messages found nothing.** One attack copy failed (Gunjurer teleport, NullReference) |
| deco (2 min) | diagnostic copy | Both copies listed every breakable decoration at floor load: **560 of 560 identical** (name and position). So decoration is in sync at load; it drifts later |
| 2 (7 min) | + break fix, review corrections | **0 missed breaks.** Team wipe, host Quick Restart, guest followed onto the new run, floor matched again. 0 mod errors |
| 3 (8 min) | + all second-pass changes | Invalid: my test helper had squeezed the windows to 464x261 and the death screen's Quick Restart threw at that size. Not the mod |
| 4 (8 min) | same build, clean 960x540 windows | Fights, Bullet Bros boss room, team wipe, Quick Restart, guest "Restarting onto the host's new run", floor matched. **0 mod errors, 0 exceptions.** 1 missed break on each side |
| 5 (12 min) | same build, Marine + Hunter | Bullet King room, wipe, restart, rejoin, the Hunter's dog shown on the other copy. 0 mod errors, 0 exceptions. 1 missed break on each side, again after the Quick Restart |
| dice (8 min) | + attack dice (GO-37), diagnostic log | **All 91 dice keys the following copy used (8 bullet patterns, 83 gun shots) were identical to keys the leading copy used; it had none of its own.** 0 mod errors, 0 failed attack copies |
| table-tops (9 + 4 min) | diagnostic copies | The earlier "560 of 560" list was taken during the loading screen, before tables are decorated: it held no table-top objects. Listed when the floor becomes playable instead: **53 of 53 table-top objects identical** on both copies (name and starting place) |
| final (10 min) | everything in this file | Two boss rooms (Bullet Bros, Gatling Gull), two team wipes, two Quick Restarts with rejoin, floors matched each time. **0 mod errors, 0 exceptions, 0 failed attack copies, 0 part failures, 0 messages the sender check would have refused.** But **5 missed breaks per side**, all table-top objects: GO-42 did not remove them (see the correction below). Follower drift 0.05 to 0.25 tiles average in most minutes, 0.94 in one |

Enemy drift on the following copy (the mod's own once-a-minute figure), at about 43 ms one-way delay:
average 0.11 to 0.25 tiles before the following change, 0.14 to 0.39 after. That is **no measurable difference**
in these runs: the content differs every run (random seed, different rooms), and the bots don't produce the
case the change targets on purpose. The change is justified by the game code, not yet by a measurement.

What the sessions did **not** exercise: Steam, three or four players, a human player, beams, swords, the
stand-in bot (no guest dropped), the exit elevator with a bot, a ghost host at an exit, the Master Round, a
protected boss kill. Those fixes are implemented from the game code and still need a play-test.

A correction to earlier versions of this file, and an item I could not close. I first wrote that after a Quick
Restart one table-top object per session differed between the games. That was wrong: table-tops are identical
(53 of 53, see the table). I then guessed the leftover "no unbroken match" lines were reports of objects that had
already broken in the receiving game long before, and added GO-42 for that. The final session had 5 such lines
per side, so that guess does not explain them either. What the logs do show: every one is a table-top object;
the two games name different starting places for what looks like the same object (0.4 to 0.9 tiles apart); and
the game itself destroys flung debris without breaking it ("Destroying large debris for being outside valid
cell ranges" appears in the same logs), which leaves nothing to match. The effect is cosmetic: a book or an
apple that is broken on one screen and gone or lying about on the other. Matching by starting place still took
the miss rate from about half of all breaks to a few per session.

### Assumptions from the first pass, now checked in the game code

| Point | Result |
|---|---|
| GO-02: forcing `HandleDeath_CR` ends the run even with a living second local player | Confirmed: the coroutine has no co-op check |
| "Duplicate Revive with 3+ players" | **Rejected.** `ResurrectFromChest` runs `CoopResurrectInternal(isChest: true)`, which clears `IsGhost` before its first yield; a second Revive is ignored |
| GO-15: Turbo / Rainbow are saved | Confirmed: `[fsProperty]` fields of `GameStatsManager` |
| GO-08: a real unseal clears `IsSealed` at once | Confirmed: `RoomHandler.UnsealRoom` sets `m_isSealed = false` on every path except the NPC "seal all" early return |
| P-1: the game's breakables lists are complete | Confirmed: `MinorBreakable`/`MajorBreakable` add themselves in `Awake`, remove in `OnDestroy` |
| D-11: the stand-in's shots anger the shopkeeper | Confirmed and fixed (GO-36). Side note: the mod's existing patch on `BaseShopController.OnProjectileCreated` does nothing for this; that method only names the shopkeeper's own bullets |
| DuelSync: `LoadCharacterSelect` marks "loading" at once | Confirmed |

### Corrections to the first pass's own changes (found by the independent diff review)

| # | What was wrong | Fix |
|---|---|---|
| R-1 | P-5 removed the per-message target update from `TargetSync.Force`; `AttackSync` starts the copied attack right after, so it aimed at the old target | `Force` sets that one enemy's target at once (still no full walk) |
| R-2 | GO-02's `onlineGhost` mark was cleared on every floor load, and was never set for a host with a stand-in and no guest: both could leave a real online ghost without its game over | not cleared on load; a stand-in counts |
| R-3 | GO-10's `FollowMissedLoad` could follow a stale load after the host had quit to the Breach or started a new run | only followed if it is still where the host is; cleared on run end |
| R-4 | The stand-in shop rule required a connected guest, which is exactly when a stand-in usually does not have one | condition removed |
| R-5 | Joining the Steam lobby we are already in became a full leave | ignored |
| R-6 | Hello-less slot marks survived the session; a destroyed stand-in stayed on guests' rosters | cleared; roster re-sent |

### New findings and fixes

All Implemented; the "Checked" column says how far.

| ID | Sev | What | Fix | Checked |
|---|---|---|---|---|
| GO-23 | Medium | **Broken table-top objects were not found about half the time.** Books, bottles and plates are knocked about differently in each game, so "the green book at (x, y)" named a spot where the other game had something else | breakables are sent and matched by where they **started** (identical in every game) | two-copy: 9 of 19 missed before, 0 to 1 per session after |
| GO-24 | Medium | **Followed enemies were let go for every attack**, including shooting while walking (the commonest one). Their own movement then ran against a slow pull toward the leader's position: sliding and rubber-banding in fights. Also: frozen or falling enemies were still driven; a slowed enemy was slowed twice; some bosses' own scripts overwrote the drive velocity depending on script order | keep driving unless an attack owns the whole enemy; hands off frozen / falling / gone enemies; velocity written right before `AIActor.Update`; stand when the leader's copy stands | two-copy: no errors; drift unchanged within noise (see above) |
| GO-25 | Medium | **Beam damage was never shared (was D-1)**, and neither were sword swings, dodge rolls into enemies, Knife Shield, orbital shield, aura items, the Katana, Hero's Sword and companion melee. All of these damage enemies outside `Projectile.HandleDamage`, only in their owner's game | added up per enemy and sent 10 times a second, for enemies we don't lead (the leader's health reports already carry its own) | compiles, loads, no errors in sessions; not exercised (bots use guns) |
| GO-26 | Low-Med | A burn or poison that only landed in one game killed the enemy there and the death was deliberately not reported (was part of D-2) | every death is reported; a duplicate report does nothing | code path |
| GO-27 | Medium | **GO-07 completed.** A kill from another game now goes in as unstoppable boss damage; if the enemy is still alive after the 10 s retry, its protection (minimum health, armor, invulnerability) is removed and it is killed through the normal death path, the same recipe the game uses for pits and the boss kill camera. Scripted boss moments are waited out for up to 20 s | as described | not exercised: needs the Dragun / Kaliber test |
| GO-28 | Low | Bullet Bros enraging, a Shelleton coming back and similar heals happen locally; the leader's older, lower health then undid them for a moment | no clamp for 1 s after our copy's health rose | Bullet Bros room ran without errors |
| GO-29 | Low | D-3 step 1: a remembered dead enemy number killed whatever later got that number | remembered with its kind and matched on both | code path |
| GO-30 | **High with a bot** | **The host could not leave the floor until the stand-in stood on the elevator too.** With a stand-in the game is in two-player mode, where the exit needs every living local player on it; the bot only follows loosely, cannot open doors alone, and (new) could not move at all once off the host's screen | while the host stands on the elevator the stand-in doesn't have to; the stand-in is exempt from the camera fence | patches load; not exercised |
| GO-31 | **High, rare** | **Softlock: host a ghost, the living guest waiting at an exit.** The elevator only comes for a living player, so the ghost host cannot take it; the held guest's screen is already black and it cannot move. Reached on exits without a boss kill (trapdoors, pit levels) | the guest tells the host where its exit leads; a ghost host takes that exit for the team | not exercised |
| GO-32 | Medium | **Master Round sharing never worked.** The patch flips the room's "a player got hit" mark around the boss's loot drop, where it only doubles credits; the Master Round is decided later, in `RoomHandler.HandleBossClearReward`. Also D-9: the stand-in never reported its hits, and split money summed to 5/6 with host + guest + bot | same switch on the right method; the stand-in's hits are sent in its player's name; the host's game drops the stand-in's share | not exercised |
| GO-33 | Medium | D-7 and D-8. A host's new run arriving during our own load started a second load on top (the decompile shows both coroutines then share statics and the first throws). A guest held at an exit followed with a plain load and kept the old run's gear | the restart waits for our load to finish (90 s safety net); a held guest restarts too and its exit wait is cleared | wipe + restart + rejoin worked in sessions 2 and 4; the mid-load case not exercised |
| GO-34 | Medium | D-6. A chest was used up for a player who cannot be revived: still in the dying animation (the game makes the ghost only when it ends), or on the death screen after a wipe | "revivable ghost" = on our floor, not marked wiped, not in a death animation; the receiver also refuses a revive once its game over has begun | not exercised |
| GO-35 | Low | D-10. A guest's purse change during the host's loading screen was dropped. The obvious fix would have thrown: the key counter's setter needs the HUD, which doesn't exist during a load | kept and applied after the load | not exercised |
| GO-36 | Low | D-11: the stand-in's shots got the host banned from shops (confirmed in game code); key items didn't reach the stand-in; the Take a Break button stopped working after the first floor (the pause menu is rebuilt per scene); a pet stayed "being petted" if the petter left | fixed each | patches load; not exercised |

### Enemy attacks and bullets (last part of the second pass)

This is the README's "enemy bullets with random spread or homing can still differ". Checked in the game code:

- The seed sent with `AttackStart` wrapped exactly one call, the attack's `Update`. A scan of the decompile
  (a regex heuristic, so approximate): of 95 attack classes, 8 roll dice inside `Update` and 23 only later; of
  296 bullet scripts, 138 roll nothing, 47 only before their first wait, 111 after it or in child bullets.
  Patterns run as scripts over seconds, one step per 1/60 s of game time.
- A gun enemy fires a few frames after its attack starts, and **gun spread doesn't use the engine's dice at
  all**: `ProjectileModule.GetAngleVariance` uses one static counter shared by every gun in the game.
- Follower aim is at the smoothed, delayed body of a remote player, with no velocity, so "leading" shots don't
  lead on observers' screens. Each game alone decides whether its own player is hit, so this is cosmetic.
  Enemy homing rockets (`RobotechProjectile` and a few others) home on each game's own player, whoever the
  enemy targeted: a real one-sided effect that needs a design decision.

| ID | Sev | What | Fix | Checked |
|---|---|---|---|---|
| GO-37 | Medium | **Enemy attacks rolled different dice in every game** (above) | every step of a pattern started by a synced attack rolls from (attack seed, script, step number); a gun enemy's shot and a teleport's landing spot roll from the attack seed, with the spread counter set for that shot; the game's dice are restored after each | two-copy: 91 of 91 keys on the follower identical to the leader's. Whether the patterns then *look* the same still wants a side-by-side look at a boss |
| GO-38 | Low | S-4: a copied attack was run on an enemy copy the game itself would not run yet (not started or awake, no shadow) and threw inside `TeleportBehavior.Update`, after the copy had been moved | not replayed on such a copy | two-copy: 2 failures in earlier sessions, 0 since |
| GO-39 | Low-Med | Shot copies started at the muzzle one network delay late, so they flew behind and could visibly miss what the real bullet hit | a plain straight copy starts where the bullet is by now (at most 0.15 s / 3 tiles, never past a wall or an enemy) | ran clean; the visual effect not judged |
| GO-40 | Low-Med | D-2: copies rolled the bullet's own poison / fire / freeze / charm chances on top of what the shooter's game reported | switched off on copies (not for exploding bullets, whose explosion kind comes from the same marks) | ran clean |
| GO-41 | Low | A partner's blank was triggered with *our* player as its user, so the game applied our blank items to it (ammo back for us, our Ammolet's effects on the enemies) | no user | code path |

### Last additions (the "can do without a decision" group)

| ID | What | Checked |
|---|---|---|
| GO-42 | Breaks are remembered for the whole floor (by name and starting place), not just for 10 seconds, so a late report of something long broken here is recognised | **did not reduce the misses** in the final session (5 per side); harmless, kept |
| GO-43 | D-12: each part of the mod is started and run on its own (40 start-up calls, 35 per-frame calls). One part failing no longer silently leaves out every part after it at start-up, or skips the rest of each frame; the log names the part, at most once every 5 s | all parts start and run in the sessions |
| GO-44 | D-4, **counting only**: for every enemy-position, attack and room-cleared message the mod now checks whether the sender could be leading that room (alive, on our floor, in the room now or within the last 2 s, or within 3 tiles) and logs once a minute how many it *would* have refused. Nothing is refused. If that stays at zero in normal play, `EnemySync.MayLead` can be switched to refuse | see the final session row |
| GO-45 | P-11: the 15-times-a-second enemy report reuses its lists and the per-frame "do we lead this room" answer; an unknown status effect triggers one new search of all items, not one every 10 s for as long as it keeps coming | ran clean |

Not done from this part: sending a teleport's destination (it differs when it depends on each game's camera),
bullets some items and companions fire that bypass the shot hooks (the damage still arrives, the picture is
missing: thrown guns, the Shadow Clone, bank-fired companion shots, beams from anything but the gun in hand),
and batching several pellets into one packet (needs a version bump; about 75% less for a shotgun).

No packet format changed in the second pass either. One message gained a trailing field (`AtExit` now carries where
the exit leads); old hosts ignore it and a new host accepts its absence. `Protocol.Version` stays 49.

### Still open after the second pass

| ID | What | Why it waits |
|---|---|---|
| D-3 step 2 | Enemy numbers still depend on each game's spawn order. A design exists: enemies made by `AIActor.Spawn` (summons, split blobs, spawn-on-death) get a number from room + parent + kind in a separate range, so a one-sided spawn shifts nothing | changes what a number means: needs a `Protocol.Version` bump and a test with summoners |
| D-4 | Receivers don't refuse messages from a player who doesn't lead the room. The check exists and counts (GO-44) | switch it on once the counts from real play stay at zero |
| D-5 | Remembered gear keyed by a name the client sends | design choice |
| D-2 rest | Bullet copies still roll their own status-effect dice; transmogrify is not synced | touches how items feel |
| S-1 | *(done: see "Enemy attacks and bullets" above.)* What remains there: aimed patterns still aim at each game's own view of the target; enemy homing rockets home on each game's own player | cosmetic / design decision |
| S-2 | Knockback is counted twice on followed enemies (the leader's reported velocity includes it, the follower adds its own) | fixing it changes how hits feel; wants eyes on it |
| S-3 | The Dragun's near-death attack group is private to its behaviour and not synced after the first pattern; the Advanced Dragun path spawns a separate enemy from local state | boss-specific, needs the fight |
| S-4 | *(fixed: GO-38.)* Large "worst drift" readings (10 to 20 tiles) still occur: a teleport's destination is picked per game. GO-37 seeds it, which only helps when both games have the same candidate cells | needs the destination sent (protocol change) |
| S-5 | With a stand-in the host's game is in two-player mode: enemies there have co-op health and bullet speed, shop prices and drop chances differ slightly from the guests'. A flying host can't use a trapdoor exit while the bot lives. The stand-in's beams are not shown to guests. A mid-game save taken then is marked co-op | each small; the trapdoor one is the same fix as GO-30 on another method |
| P-8 … P-11 | unchanged from the first pass | protocol bump / profiling first |

---

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

### What was not run (first pass; see the second pass above for the sessions run since)

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

*Second pass: D-1, D-6, D-7, D-8, D-9, D-10 and D-11 are now fixed, and D-2 and D-3 partly; see the table of
new fixes above. The rows are kept as the record of what was found.*

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

- Duplicate `Revive` with 3+ players after a boss: **rejected in the second pass** (game code).
- Host is a ghost, a living guest is held at the exit: **confirmed as a softlock and fixed in the second pass (GO-31).**
- `MenuGuard.BeforeUnpause`: **not a softlock** (second pass): only player-driven callers use `Unpause`; loads use `ForceUnpause`.
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

1. Play through T1–T7 (an evening with two copies and `launch-test-copy.ps1`). Estimate: 2–3 h. *Second pass:
   T2-like flows (join, floor match, wipe, restart, rejoin) ran clean with bots; the rest still needs people.*
2. *(done in the second pass: D-7, D-6 without a protocol bump, D-1.)* Play-test them instead: a beam or sword as
   the non-leading player; a chest opened while the partner is still dying; a host restart during a guest's load.
3. With a third copy or a dropped guest: the stand-in on the exit elevator (GO-30), a ghost host with the guest
   at a trapdoor (GO-31), the Master Round with one player hit (GO-32), the Dragun killed by the guest (GO-27).

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
| `92b5876` | `tests/`, README build notes, this file |
| `b005f2a` | stand-in shots and the shopkeeper (GO-36, first part) |
| `d2977b6` | R-1 … R-6, GO-23, GO-24 |
| `625756b` | GO-25 … GO-36 |
| `b98eb5e` | second-pass section of this file |
| `4dc124d` | GO-37 … GO-41 |
| `c180045` | this file: enemy attacks and bullets |
| (this commit) | GO-42 … GO-45 |
