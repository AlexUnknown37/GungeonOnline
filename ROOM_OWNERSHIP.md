# Room Ownership (design idea, not built)

Status: proposal, 2026-10-24. Nothing here is implemented yet.
Based on how Noita Entangled Worlds syncs entities
(https://github.com/IntQuant/noita_entangled_worlds: `noita_proxy/src/net/des.rs`,
`ewext/src/modules/entity_sync.rs`, `ewext/src/modules/entity_sync/diff_model.rs`).

## The problem today

Room authority (since 0.22) is "whoever is alive and in the room". Several players can send
EnemyStates for the same room, and "host's copy wins" only settles it some of the time. Backstop
timers paper over the gaps (dead-on-arrival kill after 1.5 s, unseal after 2 s with no RoomClear
enemies).

Symptoms seen in tests (MODLOG): rubber-banding and shuddering enemies, a room staying sealed for
the host after the guest cleared it, enemies freezing when the authority died or turned ghost.

## How Entangled Worlds does it (short version)

- A proxy keeps a pool of unowned entities. Every 7 frames each game asks "give me every free
  entity near my camera" and gets them all at once.
- The proxy stores a full copy of each entity's data. On release the owner uploads it, deletes its
  copy, and the next owner rebuilds the entity from it.
- On disconnect, all of that player's entities go back to the free pool.
- Each game announces "I'm here" every 5 frames. Owners within range send a full copy once, then
  only changes. `ExitedInterest` removes the copies when the player moves away.
- No ownership checks: it trusts players.
- Even there, a lot is hand-written per entity type (bosses, lasers, centipedes, charm, limbs).

## The idea for Gungeon Online

Do the same thing per **room**, not per entity. Gungeon is already split into rooms, and both
games already run every enemy from the shared seed, so there's no need to delete and rebuild
enemies. Only the right to send changes hands.

1. **Room-owner table, decided by the host.** Host keeps `room index -> owner slot`. A player
   entering an unowned room asks the host; the host picks one and broadcasts it. Only the owner
   sends EnemyStates / RoomCleared for that room.
2. **Snapshot on enter.** When a player enters a room someone already owns, the owner sends one
   full snapshot (alive enemy IDs, health, positions, sealed/cleared). Normal corrections after that.
3. **Handoff with state.** When the owner leaves the room, dies or turns ghost, the host gives the
   room to the next living player inside. That player gets the latest state and starts sending.
4. **Disconnect frees rooms.** Host reassigns every room the leaver owned to whoever is still in it.
5. **Late join.** Host sends the room-owner table with the join data (next to Rules).

## What it would fix

- Rubber-banding and shaking from more than one sender per room.
- Rooms stuck sealed after a clear: the owner says cleared, everyone follows.
- Freezes when the owner dies or turns ghost: the next player takes over at once.
- Disconnects mid-fight: rooms get a new owner instead of drifting.
- Walking into a fight already going: the snapshot shows the real enemy state right away.
- 3-4 players in one room: one sender however many people are there.
- The backstop timers could probably go, or would rarely fire.

## What it would NOT fix

- Enemies doing different attacks on each game (per-enemy behavior sync, still by hand).
- Boss phases, traps, chests, pickups: each still needs its own "here's my state" code. Ownership
  only tells it who sends.
- Extra enemies spawning in different places (randomness).
- The host leaving still ends the session. Host migration is skipped on purpose.

## Not copied from Entangled Worlds

- The separate proxy program.
- Delete-and-rebuild on handoff.
- Per-entity ownership (rooms are the natural unit in Gungeon).

## Before building

Read the current EnemySync / room authority code; some symptoms above may already be fixed in
later versions. Needs a protocol bump.

## Audit of EnemySync (2026-10-24, version 0.132)

Most of the proposal already exists in another form: one sender per room, chosen by a fixed rule
instead of a host-kept table.

- **One sender per room:** a game sends EnemyStates (and leads attacks via AttackSync, and sends
  RoomCleared) only if it is alive in the room AND no lower-numbered living player is in it
  (`InCharge && !LowerPlayerInRoom`). Every game applies the same rule, so in steady state exactly one
  game sends. Receivers also drop states from a higher slot when they lead the room themselves.
- **Handoff on death / ghost / leaving:** automatic. The next living player in the room becomes the
  sender the next frame, with no message needed.
- **Disconnect:** the leaver stops being "in the room", so the next player takes over (same as above).
- **Snapshot on enter:** deaths are already remembered per enemy ID for the whole floor (`deadIds`,
  killed as they spawn), cleared rooms are remembered (`clearedByOthers`, and the host sends the list to
  late arrivals), and health is clamped down to the sender's at 15 Hz. A new arrival has the right
  state within one update.
- **RoomCleared** is only sent by the room's sender (0.115), and backstops (`UnsealIfStuck`,
  `KeepClearedRoomOpen`) cover ID mismatches.

### Real gaps found

1. **Bot + guest in the same room: two senders.** The host leads any room its stand-in bot is in
   (`InCharge` bot path), and `LowerPlayerInRoom` on the host never finds a lower slot (it is slot 0). A
   guest in that room also leads (the host's own player is elsewhere). Both send states and both pick
   attacks. The host ignores the guest's states; the guest follows the host's while also sending;
   everyone else gets two streams -> rubber-banding and different attacks.
   Fix: the bot only makes the host lead a room no living real player is in.
2. **Short freeze when we become the sender.** Enemies we were following stay "driven" toward the old
   sender's last positions until their correction times out (0.5 s). Fix: drop the correction and
   release the enemy as soon as we lead it.
3. **Doorway overlap (minor, not fixed).** Each game decides from the others' positions, which arrive
   ~ping late, so for a moment at a door two games can both think they send. A host-kept table would fix
   it at the cost of a round trip on every room entry. Not worth it unless it shows up in tests.

### Verdict

Don't build the room-owner table. Fix gaps 1 and 2 (a few lines each in EnemySync). Revisit if
doorway overlap shows up in logs.
