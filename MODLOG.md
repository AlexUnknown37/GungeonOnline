# Gungeon Online — MODLOG

Journal for the Enter the Gungeon online co-op mod. Record paths, versions, findings, failures, next steps.

## Setup facts
- Game: Enter the Gungeon (Steam app 311690), Unity 2017.4.27, Mono, .NET 3.5 profile, 64-bit.
  Install: `S:\_Games\Steamapps\steamapps\common\Enter the Gungeon`
- Loader: BepInEx 5.4.21 via **Gale** mod manager. Profile:
  `%APPDATA%\com.kesomannen.gale\enter-the-gungeon\profiles\Default\BepInEx`
  (log: `...\Default\BepInEx\LogOutput.log`)
- The game folder also has leftovers from old Mod the Gungeon (`Assembly-CSharp.Base.mm.dll`, `ETGModInstallLog.txt`). Harmless.
- Thunderstore dependency string for the loader: `BepInEx-BepInExPack_EtG-5.4.2101`.
- Reference copies in this folder: `BepInEx/` (copied profile: core DLLs + many plugins), `Alexandria-Alexandria/`, `MtG_API-Mod_the_Gungeon_API/`, `universal-modder-main/`.
- No .NET SDK installed. Building uses the Windows built-in `csc.exe` (v4.0.30319) with `/nostdlib` against the game's mscorlib, giving a v2.0.50727 assembly that the game loads. **Limit: C# 5 syntax only** (no `$""`, `?.`, `=>` members, `nameof`, `out var`).

## Route
BepInEx plugin (loader API) + Steamworks.NET that the game already ships.
- `Assembly-CSharp-firstpass.dll` contains Steamworks.NET (old version): legacy `SteamNetworking` P2P
  (`SendP2PPacket`/`ReadP2PPacket`/`AcceptP2PSessionWithUser`) and `SteamMatchmaking` lobbies.
  **No** `SteamNetworkingMessages`/`Sockets` (too old).
- Game's `SteamManager` (Assembly-CSharp) initializes Steam and runs callbacks; wait for `SteamManager.Initialized`.
- Callback API: `Callback<T>.Create(DispatchDelegate(T))`, `CallResult<T>.Create/Set(call, APIDispatchDelegate(T, bool ioFailure))`.
- Useful game members: `GameManager.Instance.PrimaryPlayer/SecondaryPlayer/AllPlayers`, `GameManager.HasInstance`,
  `CurrentRunSeed` (get/set), `IsFoyer`, `IsLoadingLevel`, `MainCameraController.Camera`;
  `PlayerController.CenterPosition` (from GameActor), `unadjustedAimPoint`, `PlayerIDX`, `characterIdentity`,
  `WarpToPoint`, `SetInputOverride`.

## Project layout (`GungeonOnline/`)
- `src/`
  - `Plugin` — entry, config (PlayerName, ToggleWindowKey, LAN Port, LastAddress), `DisplayName()`
  - `Transport` — abstract link: Send(reliable?), Poll, DropPeer, Close; events OnPacket/OnReady/OnLost
  - `SteamLink` + `SteamLobby` — Steam P2P + friends-only lobby; callbacks live for the whole game (invites)
  - `LanLink` — UDP; "GO" magic + kind byte; reliable = ushort seq + ack + 0.2 s resend + in-order delivery;
    host answers Discover broadcasts; SIO_UDP_CONNRESET disabled (Windows)
  - `LanDiscovery` — broadcast + loopback "any hosts?" on the LAN port
  - `NetSession` — link-agnostic: Hello/Ping/PlayerState/Bye, timeout, host keeps listening after a drop
  - `Protocol`, `DebugWindow` (F8: name box, Steam/LAN host/join, LAN finder)
- `package/` — manifest.json, icon.png (placeholder), README.md
- `build.ps1` — compiles and writes `dist/GungeonOnline-<version>.zip` (Thunderstore layout, forward-slash zip entries).
  `-Deploy` also copies it into the Gale **Bare** test profile as `BepInEx\plugins\Gungeon_Online-dev`.
- Version must match in `package/manifest.json` and `Plugin.ModVersion` (build checks).

## Status
- 0.1.0: Steam lobby + P2P handshake, ping, position exchange.
- 0.2.0: Transport abstraction, LAN (UDP) link with discovery, username setting. Builds, deployed to Bare. **Not yet tested in game.**
  Protocol (both links): joiner sends Hello (reliable) → host takes first sender as partner → Hello back → Connected.
  Ping 1/s, PlayerState 20/s unreliable, 10 s timeout.

## Testing notes
- Test profile: Gale profile **Bare** (`...\profiles\Bare`): BepInEx + Alexandria + MtG API only.
- Steam P2P needs **two different Steam accounts on two PCs**.
- LAN can be tested on **one PC with two game copies**: host in one, "Find LAN games" / join 127.0.0.1 in the other.
  **Finding (2026-10-10):** a second copy quits silently. `SteamManager.Awake` calls
  `SteamAPI.RestartAppIfNecessary(311690)` → `Application.Quit()`. No single-instance mutex (only Mutexes are in
  TextureScale/AkBankManager). Not a crash, no dump. Valve's standard dev fix: `steam_appid.txt` containing `311690`
  next to EtG.exe makes RestartAppIfNecessary return false. Second copy must be launched directly (not via Steam).
  Don't patch this inside the shipped mod (it's Steam's launch check).
  Both copies share the save folder `%USERPROFILE%\AppData\LocalLow\Dodge Roll\Enter the Gungeon` — back it up first.
  **Done 2026-10-10:** saves backed up to `save-backup\2026-10-10_0159` (45 files). `steam_appid.txt` created in the
  game folder (delete it to undo). `GungeonOnline\launch-test-copy.ps1` starts EtG.exe directly with the Bare
  profile's doorstop args (same args Gale uses); run it once per copy.
  Gale itself sometimes launches EtG.exe directly, sometimes via `steam.exe -applaunch 311690` (see Gale latest.log).
- Shareable install: Gale → Import → local mod → pick `dist/GungeonOnline-x.y.z.zip`.

## Findings: characters (0.3.0)
- `CharacterSelectController.GetCharacterPathFromIdentity(PlayableCharacters)` (static) → "PlayerRogue" (Pilot),
  "PlayerConvict", "PlayerRobot", "PlayerMarine", "PlayerGuide", "PlayerBullet", "PlayerEevee", "PlayerGunslinger",
  "PlayerCoopCultist". Load with `BraveResources.Load(path, ".prefab") as GameObject` (asset bundle "assets/ResourcesBundle/").
- `PlayerController.AlternateCostumeLibrary` / `IsUsingAlternateCostume`; animator: `spriteAnimator.CurrentClip.name`,
  `CurrentFrame`; clip `frames[i].spriteCollection/spriteId`; sprite `FlipX`, `HeightOffGround`, `IsPerpendicular`, `UpdateZDepth()`.
- Tool: `tools\il-dump.ps1 -Type X -Method regex` prints IL of game methods (no ILSpy needed).

## Findings: levels and seeds (0.4.0)
- `Dungeon.GetDungeonSeed()`: uses `GameManager.CurrentRunSeed` if non-zero, else `Dungeon.DungeonSeed`, else a
  random seed that is **not stored** → host must set a seed (`InitializeForRunWithSeed`) to share it.
- `m_currentRunSeed` is only written by the setter and QuickRestart: the game **never clears it**. Fresh seed per run,
  reset to 0 when the session ends.
- `IsSeeded` is only read by loot code (shops, chests, pedestals, RewardManager) — no achievement impact.
- All floor loads go through `GameManager.LoadNextLevel()` (private `nextLevelIndex`, public `SetNextLevelIndex`,
  `InjectedLevelName`) or `LoadCustomLevel(string custom)`. Breach elevator (`FoyerGungeonDoor.HandleLoading`):
  fade (`Pixelator.Instance.FadeToBlack`), `DelayedLoadNextLevel`, then `Foyer.Instance.OnDepartedFoyer()`.
- Every dungeon floor runs in scene `LoadingDungeon`, so the "which level" key adds `Dungeon.gameObject.name`.
- Sync design: Harmony prefixes on host (LoadNextLevel, LoadCustomLevel, ReturnToFoyer) send LevelLoad/ReturnToFoyer;
  joiner replays them. Known gaps: QuickRestart not synced; joiner can still take elevators alone; if the two saves
  have different unlocks, generated floors/loot may differ.

## Test results
- 0.3.0 and 0.4.0 never installed: deploy failed while the game ran, so two more tests still ran 0.2.0.
  Fix (0.5.0): `launch-test-copy.ps1` auto-installs the newest build when no copy is running (`deploy.ps1`);
  build.ps1 bumps the minor version each build; `-Name X` → `--gungeon-online-name X` gives each copy its own name
  (both copies share one BepInEx config file). 0.5.0 installed in Bare 2026-10-10 02:26.
- 0.5.0 test: puppet appears ("Showing partner as PlayerConvict" on both sides) but user reports "wrong sprite";
  puppet sat beside the name tag on opposite sides in the two views → suspected FlipX mirroring around the sprite
  anchor. 0.6.0: send `sprite.WorldBottomCenter` and move the puppet so its WorldBottomCenter matches (protocol 4);
  window shows partner character/anim/frame and puppet status. Still to confirm whether identity is right.
- 0.6.0 test: identity is correct (host Pilot, guest Convict; each window names the other right). Puppet status
  "shown" but nothing visible at the tag — the sprite next to each tag was the LOCAL player (checked against reported
  positions). WorldBottomCenter is live (transform.position + anchor offset), so placement is fine → rendering issue.
  0.7.0: copy layer/HeightOffGround/IsPerpendicular/rotation/sorting from the live local player sprite (prefab layer
  suspected wrong); window shows puppet layer, z, renderer.isVisible and shader name.
- 0.7.0 test: still invisible (host Soldier, guest Pilot). Puppet: layer FG_Reflection, z ~17-18, isVisible true,
  shader tk2d/CutoutVertexColorTintableTilted. PlayerController.Start: sprite lives on child "PlayerSprite",
  `usesOverrideMaterial = true`, material shader = ShaderCache.Acquire(LocalShaderName) → "Brave/PlayerShader"
  (or ...NoStencil / ...Eevee), copies "_Perpendicular", sets "_PlayerGhostAdjustFactor"; in the Breach sets layer
  FG_Reflection recursively. 0.8.0: puppet uses `new Material(local player material)` with mainTexture = partner's
  sprite-def material texture; window compares puppet vs. local (layer, z, on screen, shader).
- 0.8.0 test: still invisible although puppet and local now match: layer FG_Reflection, z ~16.6-16.8, on screen,
  shader Brave/LitTk2dCustomFalloffTiltedCutout (the Breach uses this lit shader, not PlayerShader).
  Remaining differences are tk2d fields set on live sprites: GenerateUV2, CachedPerpState, ApplyEmissivePropertyBlock,
  AdditionalFlat/PerpForwardPercentage, etc. 0.9.0: puppet = Instantiate(local player's "PlayerSprite" object),
  children destroyed, all non-sprite MonoBehaviours destroyed, attachedRenderers (private, via reflection) cleared,
  attachParent null; private material copy whose mainTexture follows the current frame's sheet.
  F9 dumps full details of both sprites to the log.
- 0.9.0 test: **puppet visible** at the right place (cloning the live PlayerSprite fixed rendering), but it shows
  the LOCAL character, frozen → ShowFrame never applied a frame. SetSprite/set_spriteId are normal (no off-screen
  deferral when called directly). Suspect: `prefab.GetComponentInChildren<tk2dSpriteAnimator>()` picked a non-body
  animator whose library lacks e.g. "idle_twohands". 0.10.0: search all animator libraries in the prefab (alt costume
  first), log missing clip names once, keep the puppet hidden until the first partner frame is applied.
- 0.10.0 test: **animations work** (incl. dodge roll), correct partner character, run-follow worked (both in
  Base_Castle). Issues: occasional "teleporting"; partner gun not shown; guest arrived with NO gun.
  Cause of missing gun: FoyerGungeonDoor calls `Foyer.OnDepartedFoyer()` (IsFoyer=false, re-enables guns) right after
  scheduling DelayedLoadNextLevel, so the host's LoadNextLevel prefix saw IsFoyer=false → startingRun=false → joiner
  skipped OnDepartedFoyer. 0.11.0: prefix on Foyer.OnDepartedFoyer sets a departedFoyer flag; joiner calls
  OnDepartedFoyer whenever it leaves the Breach. Also: snapshot interpolation (MotionBuffer: 0.1 s delay, ≤0.15 s
  extrapolation, smoothed clock offset, 4-unit teleport threshold), state at 30 Hz with sender timestamp, out-of-order
  guard; gun sync (PickupObjectId → PickupObjectDatabase.GetById → gun sprite collection; spriteId, offset from feet,
  angle, FlipY, z relative to body) rendered by cloning the stripped body puppet. Protocol 5.
- 0.11.0 test: guns "close but sometimes draw over player", shadow out of place, bullets not synced.
  Game facts: `PlayerController.HandleGunDepthInternal` only sets gun `sprite.HeightOffGround` (+/-0.075, 0.4 if
  preventRotation) then UpdateZDepth; tk2d `UpdateZDepth` on an attached sprite defers to `attachParent`, which sorts
  its `attachedRenderers`. Shadow = GameActor.ShadowObject (blob from ShadowPrefab, child of the player root, placed
  at SpriteBottomCenter x). Gun fire: `Gun.ShootSingleProjectile` → `SpawnManager.SpawnProjectile(GameObject prefab,
  Vector3, Quaternion, bool)` → `LastProjectile`, then sets Owner/Shooter and scales baseData.
  0.12.0: gun clone attached via `AttachRenderer` with the sender's HeightOffGround (no manual z); shadow = stripped
  clone of local ShadowObject at feet + sender's offset; BulletSync: postfix SpawnProjectile records prefab, postfix
  ShootSingleProjectile (owner == PrimaryPlayer) sends Shot (level, prefab name, pos, angle, speed, damage, range,
  scale); receiver indexes all gun projectile prefabs by name (PickupObjectDatabase.Instance.Objects; singleModule,
  RawSourceVolley, alternateVolley, rawOptionalReloadVolley, charge, CriticalReplacementProjectile), spawns it with
  owner/shooter null, collidesWithPlayer false, collidesWithEnemies true. Not covered: beams, item-spawned projectiles.
  Protocol 6.
- 0.12.0 test: **bullets work** with the guns tried; gun depth OK. Shadow drawn but offset down-left of the
  partner (object anchor != drawn area). 0.13.0: send shadow renderer.bounds.center relative to feet; receiver
  shifts the clone so its bounds center matches; clone sprites get attachParent = null. F9 also dumps both shadows
  (object pos, scale, parent, each renderer's pos/bounds).
- 0.13.0 test: black character-shaped silhouette below-left of the puppet (existed before the shadow code); no
  round shadow under the partner. F9: partner shadow clone renderer `enabled False` (local one True); shadow position
  matched. Reflections: Pixelator's reflection camera culls only FG_Reflection (cm_refl); objects set material
  "_ReflectionYOffset" in OnWillRenderObject while `Pixelator.IsRenderingReflectionMap` (AIActor/Gun: bounds.min.y*2
  + actorReflectionAdditionalOffset; PlayerController.LateUpdate: actorReflectionAdditionalOffset). Puppet kept our
  player's value → misplaced black reflection. 0.14.0: NoReflection component (offset 100000 during the reflection
  pass, restored otherwise) on body + gun; shadow renderer forced enabled. Partner has no floor reflection for now.
- 0.14.0 test: black silhouette still there and it CHANGES WHEN THE LOCAL PLAYER SWITCHES CHARACTER → made from
  the local player's art, not the reflection (NoReflection didn't help; kept for now). Host ending a run → guest
  stuck loading forever: GameManager.ReturnToFoyer internally calls LoadNextLevel, so the host sent ReturnToFoyer AND
  a stray LevelLoad; the joiner started two loads. After rebooting the guest the puppet stayed hidden (likely host
  still in the run while guest in the Breach).
  0.15.0: suppress LevelLoad inside ReturnToFoyer/LoadCharacterSelect; joiner follows with DelayedReturnToFoyer once
  not IsLoadingLevel and ignores LevelLoad while loading; host remembers its last floor and re-sends it (every 8 s)
  while the partner reports a tt_foyer level; window shows the reason the puppet is hidden; F9 lists every enabled
  renderer within 3 units of the puppet (path, layer, bounds, sprite/collection, all materials).
- 0.15.0 test: black sprite "half fixed"; no F9 dump in the logs. Host ending the run did NOT bring the guest back:
  run end goes through `LoadCharacterSelect(unloadGameData: true, doMainMenu: false)` — pause menu
  (PauseMenuController.HandleExitToMainMenu → DelayedLoadCharacterSelect(0.15, true, false)) and death screen
  (AmmonomiconDeathPageController.HandleMainMenu) — never ReturnToFoyer (only elevators/DelayedReturnToFoyer use that).
  0.16.0/0.17.0: host prefix on LoadCharacterSelect (when not IsFoyer) sends EndRun (protocol 7); joiner, once not
  loading, fades and calls DelayedLoadCharacterSelect(0.5, true, false). (0.16.0 built but never installed; 0.17.0 adds
  clearing pendingEndRun on LevelLoad.)
- **Black silhouette solved (F9 nearby-renderer dump, guest side):** `GungeonOnline Partner Gun/BraveOutlineSprite`
  (Brave/Internal/SinglePassOutline, sprite from the LOCAL character's collection). Object.Destroy of the clone's
  children is deferred to end of frame; the gun clone (Instantiate(go)) was made in the same frame and copied the
  local player's outline child. 0.18.0: DestroyImmediate for the body clone's children, and the gun clone's children too.
- 0.18.0: black sprite confirmed fixed.
- Co-op deaths (0.19.0, untested). Game facts: `PlayerController.Die(Vector2)` (private) → if COOP and
  GetOtherPlayer alive → `HandleCoopDeath(bool ignoreCorpse)` (public IEnumerator; only touches own items +
  IsPrimaryPlayer, ends in BecomeGhost) else `HandleDeath_CR` (private) → DoGameOver. Chest: `Chest.Initialize`
  resets static m_IsCoopMode unless GameType COOP; `Chest.Open` coop branch needs COOP + NumberOfLivingPlayers == 1 +
  ChestIdentifier NORMAL → plays open anim, m_isMimic=false, GiveCoopPartnerBack → `ResurrectFromChest(Vector2
  chestBottomCenter)` (public, self-contained). Online design (DeathSync): Die prefix → if partner alive on our
  floor: StartCoroutine(HandleCoopDeath(m_isFalling)) + PlayerDied, skip Die; if partner already a ghost: normal
  death + TeamWiped (ghost then runs HandleDeath_CR via reflection). Chest.Open prefix while partner is a ghost
  (NORMAL chest, opener alive): IsOpen=true, play openAnimName, send Revive(chest WorldBottomCenter), no loot.
  PlayerState carries IsDead (IsGhost || healthHaver.IsDead). Protocol 8. Not covered: boss-kill revive, coop chest
  look, ghost animations on the puppet.
- 0.20.0: PvP toggle (config Rules/PvP, host checkbox in F8 window; Rules message on connect + on change,
  protocol 9). Remote-spawned partner bullets get collidesWithPlayer = session.PvpEnabled. Untested.
- Enemy sync step 1, "shared health" (0.21.0, untested). Both games simulate enemies; ID = index of room in
  `Dungeon.data.rooms` * 1000 + order of `RoomHandler.RegisterEnemy` in that room (postfix); messages carry level key
  + EnemyGuid to reject mismatches. `HealthHaver.ApplyDamage` pre/post health diff → EnemyDamage / EnemyDied (reliable);
  receiver applies via ApplyDamage(ignore invuln+caps) with applyingRemote guard; dead IDs remembered per floor and
  killed when they register later. Host EnemyStates 10 Hz (id, specRigidbody.UnitCenter, health): joiner lowers health
  to host's (ForceSetCurrentHealth) and snaps enemies drifted > 1.5 tiles (transform + specRigidbody.Reinitialize).
  Partner bullet copies tagged; `Projectile.HandleDamage` prefix/postfix sets RemoteBulletHitting so ApplyDamage on
  enemies is skipped (no double damage). Protocol 10. Not synced yet: enemy targeting/attacks (each game's enemies
  attack their own player), room clear/doors, boss phases, enemies spawned by timing/randomness may mismatch.
- 0.21.0 test: respawn works. Issues: revive chest stays closed in the ghost's game; ghosts should be pulled into
  fights; with host as ghost enemies froze (host) / rubber-banded (guest); room stayed sealed for the host after the
  guest cleared it; enemies shudder on guest; ghost pulse kills enemies. Facts: DoGhostBlank → SilencerInstance.
  TriggerSilencer (push/knockback only, no enemy damage) — kills likely knockback into pits / blank-vulnerable enemies.
  0.22.0: room authority = whoever is alive and in the room (both send EnemyStates; the host's copy wins if both are);
  per-frame glide correction (settle 0.25, pull speed 6/s, snap > 4 tiles); ghost's game never applies enemy damage
  (ApplyDamage skipped while local is ghost, unless from network); dead-on-arrival enemies killed once their room is
  sealed or after 1.5 s; sealed + no RoomClear enemies for 2 s → UnsealRoom; RoomHandler.SealRoom postfix sends
  PullGhost (ghost WarpToPoint); Revive also opens the nearest closed chest (≤3 tiles) in the ghost's game.
  Protocol 11. Requested but deferred: host enemies targeting the guest, dog sync + petting, death item-drop sync.
- 0.22.0 test (F9 on host): partner was the Guide/Hunter; her dog isn't drawn on the host (pets weren't synced).
  0.23.0: PlayerController.companions (public List<AIActor>) → first visible companion's EnemyGuid, clip, frame,
  FlipX, sprite.WorldBottomCenter added to PlayerState (protocol 12). PetPuppet: libraries from
  EnemyDatabase.GetOrLoadByGuid(guid) animators, sprite = RemotePuppet.CloneStrippedSprite(local player sprite)
  (now a shared helper), own MotionBuffer for smoothing. Visual only. Petting needs CompanionController.DoPet
  (public) on a real companion — not done yet.
- Enemy targeting (0.24.0 built/never installed, 0.25.0): `AIActor.TargetRigidbody` returns `OverrideTarget`
  when set, else PlayerTarget.specRigidbody; `AIBulletBank.PlayerPosition()` (bullet-script aiming) uses
  aiActor.TargetRigidbody. TargetSync: invisible SpeculativeRigidbody (12x16 px manual PlayerHitBox collider,
  CollideWithOthers/TileMap false) follows the partner's smoothed center; 4x/s each active enemy in our room picks
  the closer player (switch only if 20% closer; ghosts are never targets); chasing-partner enemies get
  OverrideTarget = that body (re-applied every frame), others have it cleared. Untested: whether behaviours that use
  PlayerTarget directly ignore it; partner's picture doesn't block bullets.
- 0.25.0 test: the ghost got stuck after the fight-start warp. Cause: `PlayerController.WarpToPoint(point, poof,
  followers)` sets transform.position = point; we passed the partner's CenterPosition (half a body off → in walls).
  0.26.0: PlayerState carries RootOffset (transform.position - sprite feet); PlayerWarp warps to partner feet +
  RootOffset; ghost pull delayed 1 s and uses the partner's position at that moment; F8 "Teleport to partner"
  button (disabled with a reason when not connected/other floor/loading/you're alive in a sealed room).
  Protocol 13. Other game warps worth knowing: ReuniteWithOtherPlayer(PlayerController, bool), TeleportToPoint.
- 0.27.0: death-screen choices. Host LoadNextLevel prefix also treats QuickRestart as a run start (fresh seed).
  Joiner: QuickRestart prefix → if host is in the Breach, LoadCharacterSelect(true,false) instead; any joiner run
  start (Breach elevator / QuickRestart) while the host is in a run adopts the host's seed + floor (index or
  injected custom level) in the LoadNextLevel prefix. LevelLoad gained a `follow` flag; joiner always stores the
  host floor; joiner on its own death screen doesn't auto-follow LevelLoad/EndRun/ReturnToFoyer. Host sends its
  floor (follow=false) on connect. Removed the 8 s auto catch-up. Protocol 14.
- 0.28.0: ThrowSync. `SpawnObjectPlayerItem.DoSpawn(user, angleFromAim)` has three paths: prefab with Projectile →
  Instantiate rotated at user center; tossForce 0 → placed at center (anchor 4), quantized 1/16; else Instantiate at
  center (+0.25 up if aiming up) and `LootEngine.DropItemWithoutInstantiating(obj, pos, Euler(angle)*aim, tossForce,
  false,false,true,false)`, then DebrisObject flags (BlackHoleDoer → no pits/absorption, aim up → heightBoost -1,
  IsAccurateDebris, Priority 0, bounceCount). Postfix sends Throw(level, item id, angle, center, aim, synergy); the
  partner repeats it (skip enemyGuid items, Kage Bunshin, cigarettes). Goop/explosions then happen natively in both
  games (players get burned by fire on their own floor). EnemyDamage now only for LocalBulletHitting (our projectile
  HandleDamage); other damage happens in both games natively; EnemyDied always sent. Protocol 15.
- 0.29.0: petting the partner's dog. Game petting (PlayerController.HandlePlayerInput): InteractAction.WasPressed,
  not IsPetting/IsInCombat/IsDodgeRolling/queued anim, scans StaticReferenceManager.AllEnemies for companions with
  CompanionController.CanBePet within 2.5 → DoPet(player) (aiAnimator LockFacing + PlayUntilCancelled("pet")),
  player spriteAnimator "pet", ToggleGun/HandRenderers(false,"petting"); Update ends it when not playing "pet", > 3
  tiles or rolling. PetSync re-creates that against the PetPuppet (IsShown/Center added), using m_activeActions via
  reflection; PetStart(petterOnLeft)/PetStop make the owner's real dog play/end "pet". Protocol 16.
- 0.30.0: blanks + player look. Every blank = `SilencerInstance.TriggerSilencer(center, expandSpeed, maxRadius,
  vfx, distIntensity, distRadius, pushForce, pushRadius, knockbackForce, knockbackRadius, extraTime, user,
  breaksWalls, skipBreakables)`; VFX "Global VFX/BlankVFX" / "Global VFX/BlankVFX_Ghost" (BraveResources). BlankSync
  postfix (user == PrimaryPlayer) sends all params; receiver runs a new SilencerInstance with user = its own player
  (guarded against echo). Ghost look = PlayerController.ChangeSpecialShaderFlag / ChangeFlatColorOverride; status
  tints = GameActor.CurrentOverrideColor; shader IDs from private GameActor fields m_specialFlagsID /
  m_overrideFlatColorID / m_overrideColorID. PlayerLook reads them from the player's sharedMaterial each state and
  the puppet material gets the same values. On fire: PlayerController.IsOnFire; puppet gets 3 flame sprites from our
  own stats.OnFireSourceEffect.FlameVfx. Protocol 17.
- 0.30.0 pet test: "Petting the partner's dog" logged on every press and the owner got PetStart, but the petting
  stopped the next frame: without PlayerController.m_pettingTarget the player's animation code replaced "pet" with
  idle, and our IsPlaying("pet") check ended it. 0.31.0: hold `m_handlingQueuedAnimation = true` while petting (the
  flag QueueSpecificAnimation uses), re-play "pet" if it ends, stop on moving (velocity, after 0.25 s), rolling,
  combat or > 3 tiles; clear the flag on stop.
- 0.31.0 test: fire visuals work. Host's enemies aim at the guest but never fire. AIActor.HasLineOfSightToTarget →
  HasLineOfSightToRigidbody / HasLineOfSightToTargetFromPosition raycast with the enemy visibility mask and require
  the hit body == TargetRigidbody; our partner body has no collisions so it never got hit. 0.32.0: postfixes on both
  return "no wall cell between enemy and partner" (quarter-tile steps over DungeonData.isWall /
  CheckInBoundsAndValid) when the target is our partner body.
- 0.32.0 test: dog petting works. Guest stuck loading forever after host quick-restart: guest was a GHOST (not
  "dead"), TeamWiped was ignored (no log), so it followed the new run as a ghost. Also NullReferenceException in
  SpeculativeRigidbody.ForceRegenerate from Start / our Reinitialize right after level loads (PhysicsEngine not
  ready) → partner target body broken for the floor → enemies followed the partner (pathing) but aimed at us and never
  fired. AIShooter.AimAtTarget/Update also use aiActor.TargetRigidbody.
  0.33.0: target body created disabled, only enabled/initialized when PhysicsEngine.HasInstance && !IsLoadingLevel,
  Reinitialize in try/catch (destroy + rebuild on failure). DeathSync.ForceGameOver (ghost or dead, once per run;
  reset after each LoadNextLevel); TeamWiped uses it; joiner receiving a startingRun LevelLoad while a ghost shows its
  death screen instead of following.
- 0.33.0 test: simultaneous death → both saw the partner alive (state ~33 ms old), both became ghosts, guest never got
  a death screen. 0.34.0: DeathSync.Update every frame: local ghost + partner IsDead for 1 s (or partner disconnected
  for 3 s) → ForceGameOver, independent of the TeamWiped message. Host restart no longer pulls a joiner who is in a
  run (alive/ghost/death screen): startingRun LevelLoad while !IsFoyer only stores the host floor and sets
  WaitingToJoin (F8 hint); the joiner's own Quick Restart / elevator adopts it. A joiner in the Breach still follows.
- 0.35.0 (user rules): host new run pulls the joiner unless the joiner is on its death screen; joiner Quick Restart
  before the host restarts is held (WaitingForHost + saved QuickRestartOptions, centered "Waiting for the host..."),
  then run when the host's startingRun LevelLoad arrives; if the host goes to the Breach instead the joiner goes there.
  Pulling = RestartOntoHostRun → GameManager.DelayedQuickRestart(0.5, options) (fresh player, fixes ghost carry-over);
  BeforeLoadNextLevel adopts the host floor (dropped the Remote.HasPlayer requirement: host has no player mid-load).
- 0.35.0 test (F9 guest): a BulletMan stayed alive in Room_Castle_RP_Dodgems on the guest after the host cleared it
  (ID mismatch from different spawn order). Dog flashed when a player got hurt; red tint sometimes lingered.
  HealthHaver.FlashOnHit/EndFlashOnHit set "_OverrideColor" on materialsToFlash; CloneStrippedSprite copies shared our
  player's material until ApplySheet swapped it, and ApplySheet bailed when def.material was null (dog uses
  materialInst) → the pet kept sharing our player's material. 0.36.0/0.37.0: clones get `new Material(...)`
  immediately (gun clone too); ApplySheet uses def.materialInst ?? def.material. RoomCleared: RoomHandler.UnsealRoom
  postfix (no RoomClear enemies left, sender alive) → partner marks all IDs in that room dead, kills what's left,
  unseals. PauseSync: living player's GameManager.IsPaused (not loading, not dead) → Paused msg → partner
  BraveTime.RegisterTimeScaleMultiplier(0, key) + "X paused the game"; cleared on unpause/disconnect/partner dead or
  loading. Protocol 18. (0.36.0 never installed.)
- 0.37.0 test: host felt laggy/shuddering while walking; no error spam in logs. Suspect: ApplySheet made a new
  Material and destroyed the old one on every sprite-sheet switch (walk cycles span several atlases) → GC hitches.
  0.38.0: SheetMaterials component caches one material per sheet per puppet sprite.
  WorldSync: MinorBreakable.Break(Vector2) / MajorBreakable.Break(Vector2) (not tables) / FlippableCover.DestroyCover
  (bool, Vector2?) → Break(kind, base name, position, direction); receiver breaks the nearest same-name unbroken one
  within 0.75. FlippableCover.Flip(DungeonData.Direction) → TableFlip(name, home, dir). Tables known by "home"
  position (first seen, scanned each second; same in both games); moved tables (> 0.02, not being corrected) sent
  ~7/s as TablePositions; receiver glides its copy (transform + specRigidbody.Reinitialize). Protocol 19.
  Not done: player-vs-partner body collision; rolling (kickable) barrels' movement.
- 0.39.0: rolling barrels = KickableObject (Interact → private Kick(SpeculativeRigidbody kicker); direction from
  the kicker's position). Kick postfix sends (name, home, kicker pos); the partner invokes Kick with
  TargetSync.PartnerBody (the invisible body at the partner's position) as kicker. Barrels tracked by home like tables;
  the kicking game reports the roll positions (TablePositions entries now carry a kind byte: 3 table, 4 barrel), the
  other copy (RemotelyKicked) only glides. Protocol 20.
- 0.38.0/0.39.0 never loaded: Harmony "Undefined target method" for WorldSync.AfterDestroyCover — the overload is
  DestroyCover(bool, IntVector2?) not Vector2?. A failed patch throws in Plugin.Awake and kills the WHOLE mod.
  0.40.0 fixed; verified by launching a copy myself: "Gungeon Online 0.40.0 loaded", no patch errors.
  Lesson: after adding patches, launch once and grep LogOutput.log for "Patching exception" before handing over.
- 0.40.0 test: host revived via chest → frozen and (seemingly) extra health; laggy. CoopResurrectInternal ends with
  GameManager.GetOtherPlayer(this).stats.RecalculateStats → NRE online, skipping CurrentInputState = AllInput and
  healthHaver.IsVulnerable = true (frozen + invulnerable). The revive also FullHeal()s (+6 armor for Robot) — native
  co-op rule. ReuniteWithOtherPlayer(null) NRE from LateUpdate (ghost leash). WorldSync did FindObjectsOfType every
  second (+ on every lookup) → hitches.
  0.41.0/0.42.0: DeathSync.FinishRevive coroutine (after ≥1.5 s / revive done, ≤4 s) restores input, vulnerability,
  clears "revivepause"; ReuniteWithOtherPlayer(null) → PlayerWarp.TeleportToPartner; WorldSync scans only on floor/
  room change or every 15 s, lookups don't scan. 0.42.0 built while the user's game was open: NOT yet load-verified.
- 0.42.0 test: guest walking into barrels didn't break them on the host. Walking → MinorBreakable.OnPreCollision →
  Break(Vector2) (already patched); there's also a separate Break() with its own body. 0.43.0: also patch Break();
  log every break sent ("Broke ... telling partner") and every miss on receipt (closest candidate name + distance).
  Load-verified.
- 2026-10-15 FIRST REAL STEAM TEST (two PCs, 0.43.0): Steam lobby + P2P connect works; pet, pause, breakables,
  ghosts, room clears, game over, EndRun all seen working in logs. Breakable misses were only already-broken copies.
  Problems: (1) friend had no Keep Running → Unity stops on tab-out → "timed out"; (2) every host new run →
  joiner "partner left the lobby": both games freeze while generating the floor (>10 s silence) → joiner timed out
  and LEFT the Steam lobby. 0.44.0: timeout 20 s and time spent loading / frame gaps > 1 s don't count; a Steam
  joiner that times out stays in the lobby (Status Connecting, re-Hello every 2 s, seed kept); host answers every
  Hello; Application.runInBackground = true while connected/connecting. Load-verified.
  Reference: user's metadata dump at D:\The_Work_Space\OpenCode\enter the Gundeon Decomp (Assembly-CSharp_dump.txt =
  every type/method signature; MULTIPLAYER_NOTES.md). Entangled Worlds (Noita) docs don't describe its netcode.
- 0.45.0–0.47.0: in-game Multiplayer menu. MenuButtons clones the menu's Options button (PauseMenuController.Start
  postfix → OptionsButton; MainMenuFoyerController.InitializeMainMenu postfix → ControlsButton = "OPTIONS" on the
  title), strips non-dfControl components, Text "MULTIPLAYER" (IsLocalized false), Click → MultiplayerMenu.Open.
  Layout: new button takes the anchor's spot; the anchor and dfButtons above move UP by the measured row spacing
  (pushing down cut QUIT off screen). Screenshot-verified on the title: PLAY/OPTIONS/MULTIPLAYER/QUIT.
  MultiplayerMenu = IMGUI panel (name, Host Steam/Invite, Host LAN, Find LAN, join by address, PvP, teleport, leave;
  Esc/Back). F8 DebugWindow now hidden by default (backup). Hosting/joining (incl. accepting a Steam invite) on the
  title screen presses the title's NewGameButton (PLAY) via DoClick → straight into the Breach.
  tools\screenshot-game.ps1 saves a screenshot of the game window. Not yet: controller navigation onto the new button.
- The Bare profile also has a Gale-imported Gungeon Online 0.43.0 that BepInEx skips (harmless).
- The user removed the Gale-imported duplicate (only Gungeon_Online-dev remains as of 0.8.0).
- The user also imported the zip via Gale (`plugins\Gungeon_Online`), duplicating the dev copy; asked them to uninstall it.
- 2026-10-10, 0.2.0, LAN on one PC (two copies): **Connected**, ping 7 ms, name tags placed correctly over the partner
  (WorldToScreenPoint on MainCameraController.Camera is right). LocalAddress() showed a 26.x (Radmin VPN) address
  instead of the real LAN IP — pick better / list all addresses.

## Next steps
1. In-game test: plugin loads (log line), F8 window, host lobby, friend joins, ping shows.
2. Check the name-tag marker lines up with the partner (EtG's pixel-perfect camera may offset WorldToScreenPoint).
3. Milestone 3: show the partner as a real in-game character (likely by spawning/driving the co-op player).

- 0.48.0: our IMGUI panels covered the game's cursor (GameCursorController draws in OnGUI). Plugin.OnGUI sets GUI.depth = -1000 and, while the Multiplayer menu or F8 window is open, calls GameCursorController.DrawCursor() after drawing them (Repaint only). Screenshot-verified with the F8 window.

- 0.49.0/0.50.0: SteamFriendsList: immediate friends with GetFriendGamePlayed AppID 311690 (m_steamIDLobby = joinable lobby → Join button); rich presence 'gungeononline' = our version while online and 'connect' = '+connect_lobby <id>' while in a lobby (Steam friends-list Join Game). Menu section 'Friends playing Enter the Gungeon' (refresh 3 s; hosts first). Screenshot-verified the menu opens from the title button (needs a ~250 ms press when clicked synthetically) and shows the section. tools\click-game.ps1 added.

## 0.51.0 / 0.52.0 (2026-10-15) — save sync ("host's world, your own save") + bullet fix
- Cause of guest black screen (guest save further along): floor generation reads save progress
  (log: guest injected "gunber muncher" metadata, host "nothing"); guest's F1 generation retried forever.
- New SaveSync.cs: host serializes GameStatsManager with the game's own
  SerializationHelpers.SerializeToContent<GameStatsManager, FullSerializerSerializer>, gzips, sends as
  MsgType.Progress (28) chunks of 12 KB (reliable) on connect (throttled 10 s) and before each run start's LevelLoad.
  Joiner deserializes into an in-memory copy (never saved). While a floor loads (LevelSync LoadNextLevel /
  LoadCustomLevel prefixes -> BeginBorrow; ends when !IsLoadingLevel + new Dungeon + player, +1.5 s, cap 90 s)
  a generic Harmony prefix answers 24 GameStatsManager read methods from the host copy. Writes untouched,
  so Hegemony Credit/unlocks go to each player's own save. 1-arg "current character" overloads not patched.
  Save folder backed up once per game session to BepInEx/config/GungeonOnline-save-backups (keeps 10).
  Protocol 21. Self-test on start: round-trip OK, 11 KB (3 KB packed), 434/434 flags match.
- 0.51 load bug: AccessTools.Method ambiguous on SerializeToContent overloads aborted Plugin.Awake → fixed (0.52).
- Shots now unreliable: reliable ordering made one lost packet hold back later bullets → late burst fire.
- Known gap: prerequisites that check the *player's character* still use each player's own character.
- 0.53–0.55: SaveSlotLabel.cs — "SAVE SLOT X ▼" top-right on the title screen; click opens Slot A–D list
  (disabled while online). Switch = SaveManager.TargetSaveSlot + GameManager.LoadMainMenu (same as Options'
  ChangeSaveSlot). Hidden once DisableMainMenu turns the title GUIManager off (Breach). Verified in game.
- 0.56: Boss stayed alive on a ghost host after the guest killed it: HealthHaver.BossHealthSanityCheck refuses a
  killing blow on a boss when PrimaryPlayer is dead and the game isn't local co-op. EnemySync prefix returns true
  while applyingRemote. Boss-kill revive: RoomHandler.HandleBossClearReward postfix on the living player sends
  Revive(pos, fromBoss=true) when the partner is a ghost (no chest opened on the receiver). Protocol 22. Load-verified.
- 0.57: LootSync.cs + PartnerMapMarker.cs (protocol 23, msgs 29–34).
  Chests: Chest.Open postfix -> ChestOpened(pos); receiver nulls contents (re-roll, not for mimics), ForceUnlock,
  ForceOpen; teleports to partner if not in that room. DeathSync.ReviveChest excludes the revive chest.
  Master Round: drop is AIActor.HandleLootPinata reading room.PlayerHasTakenDamageInThisRoom. PlayerController.Damaged
  postfix sends HitInRoom once per room; pinata prefix clears the flag (restored after) when partner is alive, in the
  boss room, and not hit there.
  Guns: Gun.DropGun pre/postfix -> GunDropped(id host-even/joiner-odd, pickupId, ammo, pos); receiver
  LootEngine.SpawnItem. Gun.Pickup prefix on a tracked gun -> GunTaken; receiver destroys its copy if still on floor.
  Shop: PlayerController.HandleItemPurchased/HandleItemStolen postfix -> ShopItemGone(pos) -> ForceOutOfStock.
  Wallet: per-frame delta of Currency/KeyBullets while both in a run -> Wallet(dm, dk) applied on partner.
  Note: both games drop money for the same kills, so shared income is ~2x solo.
  Rat: ShouldBeTakenByRat returns false while primary is dead; prefix sets currentHealth 0.5 for the call.
  Map: clone of our minimapIconPrefab, tinted, at Minimap.transform + world/8 (z -1) like UpdatePlayerPositionExact.
  Load-verified only; needs a two-player test.
- 0.58: Partner map marker uses their character prefab's minimapIconPrefab (CharacterSelectController path), falls
  back to our tinted icon. HalfMoney host rule (config Rules/HalfMoney, default on; menu toggle; sent in Rules,
  protocol 24): CurrencyPickup.Pickup prefix halves currencyValue (non-meta) with a carried remainder while in a
  shared run. Load-verified.
- 0.59/0.60: Custom Characters mod support (CustomCharacterSupport.cs, protocol 25). PlayerState carries the
  custom character name (CustomCharacter component -> data.nameInternal, e.g. "PlayerThe_Lamb"); receiver looks it
  up in CustomCharacters.CharacterBuilder.storedCharacters (name -> Tuple<data, prefab>) via reflection and uses
  that prefab for the puppet and map icon; falls back to the base identity's prefab. Test launch options:
  launch-test-copy.ps1 -Test host|join (auto LAN host / join 127.0.0.1 from the title screen). Tools take
  -Index N (copy by start order) via find-game-window.ps1; new key-game.ps1 (SendKeys).
  Verified with two copies: guest log "Showing partner as custom character PlayerThe_Lamb ... GuideAnimation(Clone)"
  and screenshot shows the partner as The Lamb.
- 0.61: Partner puppet gets the game's black outline (SpriteOutlineManager.AddOutlineToSprite once the partner's
  first frame is set; ApplySheet calls ForceUpdateOutlineMaterial on each outline when the sheet changes). The
  missing outline made the Lamb look flat/clipped. Verified on two copies (host Marine sees guest Lamb outlined).
- 0.62–0.66: Loop The Gungeon (Milz 1.1.2) support, LoopSync.cs (protocol 26, LoopState = 35). Loop count is
  LoopTheGungeon.Plugin.GlobalLoopOffset; TriggerLoop(delay) = ++loop, casing/shrine resets, QuickRestartLite,
  PerformLoopLoad (SetNextLevelIndex + DelayedLoadNextLevel, so host's LevelSync sends a normal follow LevelLoad).
  Host sends loop + 7 GunfigManager.Get*PerLoop values on connect, before each LevelLoad, and on change (1 s poll).
  Joiner: on host loop == ours+1 in a run, calls TriggerLoop with PerformLoopLoad suppressed (prefix false); sets
  GlobalLoopOffset; Get*PerLoop prefixes return the host's values (ref object __result — verified with a planted
  value: "redirect check OK"). Set-up is deferred to the first frame because Loop loads after us.
  Not synced: DPS cap / LOTJ / tax toggles; extra enemies per loop may spawn differently (room-clear backstop).
  Loop transition itself not yet tested in game.
- il-dump.ps1: -Dll accepts a full path to another mod's DLL; resolves references from the Bare profile.
- 0.67/0.68 (protocol 27): Loop The Gungeon, part 2.
  Options: Gunfig.Value(key) prefix on the joiner answers with the host's value for any Gunfig belonging to
  Loop (GunfigManager.gunfig or a submenu via _parentGunfig); host sends all _options of those menus (11) in
  LoopState. Verified with a planted value ("Options redirect check OK").
  Extra enemies: SpawnCountScaler.SpawnEnemiesWithVariety prefix sets a seed base from run seed + room index +
  wave (per-room call count) + loop; PickWeightedCandidate pre/postfix InitState(base + pick) and restores
  Random.state after. Positions still differ (GetRandomAvailableCell, player-relative) -> EnemySync glides.
  Joiner keeps the host's loop count (Update enforces it; Loop's resume can restore another).
  Continue: GameManager.DelayedLoadMidgameSave prefix marks the next level load as a run start, so a joiner
  continuing a saved run goes to the host's floor/seed with their saved character and items.
  Joining from the title (MultiplayerMenu): joiner waits until connected (+1 s, max 8 s); host in a run and a
  valid mid-game save -> CONTINUE, else PLAY. Untested in game: loop transition, continue onto host's run.
- 0.69/0.70: Real Steam test (guest = long save): seed + borrow worked but the floor still differed (guest's
  F1 needed 4 generation attempts; boss pool BulletKing/GatlingGull). Some code reads progress data directly.
  SaveSync now SWAPS GameStatsManager.m_instance to the host copy while a floor builds (our session fields lent
  to it); 22 write methods redirect to our real instance only (MirrorWrite/MirrorWriteVoid; void methods can't
  take __result — 0.69 failed to patch them AND broke Loop's patching, never ship 0.69); GameStatsManager.Save
  swaps ours back in for the save; quitting ends the borrow. RoomCleared sent once per room per floor (log
  showed 35+ repeats for one room). Breach freeze: log was overwritten, cause unknown.
- 0.71 (protocol 28): Chest re-roll was a no-op: DetermineContents falls back to RewardManager's seeded floor
  manifest (PregeneratedChestContents) -> partner got the same item. Receiver now sets contents from
  Chest.GenerateContents(player, 0, new System.Random(own)). OrbitalSync.cs: player.orbitals + trailOrbitals
  sprites (collection name, sprite id, pos, height, flip, angle) at 10 Hz unreliable (Orbitals = 36); receiver
  draws CloneStrippedSprite copies, collections found via Resources.FindObjectsOfTypeAll. InventorySync.cs:
  joiner sends its inventory snapshot (guns+ammo, passives, actives, health/max/armor, blanks, run seed) on
  change every 2 s (37); host remembers per PeerName; joiner starting onto the host's run (not via Continue)
  sends InventoryRequest (38); host replies InventoryRestore (39) if same run seed; joiner adds missing guns
  (AddGunToInventory) + items (GivePrefabToPlayer), sets health/armor/blanks. Load-verified only.
- 0.72: BreachCultist.cs. Online (session.Link != null): FoyerCharacterSelectFlag.Update prefix keeps the coop
  character (Cultist) shown (normally hidden with no controller); ChangeCoopMode.OnEnter prefix: IsTestCoopValid
  -> fire IfCoopValidEvent; TargetCoopMode -> run a ChangeToNewCharacter (PlayerPrefabPath copied, Init(State),
  OnEnter) so WE become the Cultist; leaving coop -> Finish. build.ps1 now references PlayMaker.dll.
  Verified: Cultist visible in the Breach while hosting. Selecting him not verified (mouse automation in the
  Breach character select didn't land on him).
- 0.73–0.76: Cultist in the character-select screen. Foyer.SetUpCharacterCallbacks prefix (online): coop flag ->
  IsCoopCharacter=false, CharacterPrefabPath=PlayerCoopCultist, shown; it lacked an OverheadElement (borrowed
  from another flag; CreateOverheadElement postfix relabels "The Cultist", hides other labels/sprites) and a
  CharacterSelectIdleDoer (the mouse hover reads its sprite -> NullReferenceException in HandleCharacterSelect;
  added one, disabled). Verified: selected the Cultist and spawned as PlayerCoopCultist(Clone).
- 0.77 (protocol 29). Real test feedback round (guest Slayer rejoining Darj on Hollow).
  FloorCheck.cs: after each floor settles (1 s), fingerprint = every room's PrototypeRoomName@basePosition/
  dimensions (FNV hash); host sends FloorPrint (41) on new floor and on connect; joiner compares (same level key +
  seed), logs the first 8 differing rooms, reloads the host floor via LevelSync.ReloadHostFloor (max 2 per floor),
  else a persistent on-screen warning. Toast.cs: on-screen messages.
  LevelSync: joiner remembers the floor it's on/loading (ourLevel); a host LevelLoad for that same floor is
  skipped (joiner took the exit first) instead of reloading it.
  InventorySync: snapshot includes the character prefab name; joiner sends no snapshots while waiting for the
  host's answer (6 s), host always answers (bool hasData) - a fresh character could overwrite the remembered one.
  Host on connect sends RejoinOffer (40) if it remembers the peer in its current run; joiner auto-picks that
  character on the select screen (Foyer.OnSelectedCharacter with the matching flag) and starts the run
  (OnDepartedFoyer + DelayedLoadNextLevel -> adoption -> restore). Title join prefers this over Continue.
  EnemySync: partner-caused deaths (EnemyDied, kill queue, RoomCleared) die without loot (HandleLootPinata
  skipped; bosses still drop). Boss room SealRoom pulls a living partner too (PlayerWarp pull for alive).
  BulletSync: Shot carries gunId; receiver plays the gun's shot sound (SetSwitch WPN_Guns + Play_WPN_gun_shot_01
  or OverrideNormalFireAudioEvent, max one per 60 ms) and spawns its shellCasing; Gun.Reload postfix -> Reload (42)
  -> Play_WPN_gun_reload_01. Load-verified only (two copies connect, Breach).
  Not done yet: shopkeeper anger/ban, controllable projectiles (Slayer rocket), beams (wood beam).
- 0.78 (protocol 30): ShopBan.cs - online, BaseShopController.set_State to Hostile/RefuseService/PreTeleportAway/
  TeleportAway/Gone is replaced (via __args, the ShopState enum is protected) with Default and the local player is
  banned from that shop: ShopItemController.Interact blocked + ban lines (Toast), TalkDoerLite.Interact on that
  shop's m_shopkeep shows ban lines; OnProjectileCreated ignores the partner's copied bullets.
  BulletSync: Shot carries a netId; our bullets whose direction turned >6 deg from launch send Courses (43:
  pos/angle/speed, 10 Hz, max 24) -> partner's copy snaps + SendInDirection. BeamSync.cs: our gun's active beams
  (Gun.m_moduleData[].beam) sent as prefab name/origin/direction at 15 Hz (Beams = 44); partner spawns the prefab
  like BeamController.FreeFireBeam with no damage/hits, updates Origin/Direction/LateUpdatePosition each frame,
  CeaseAttack after 0.3 s silence. BreachCultist: TalkDoerLite.Interact on NPC_FoyerCharacter_Cultist (online,
  Breach) skips the co-op conversation and runs ChangeToNewCharacter(cultist path) with the NPC's FSM state.
  Load-verified only.
- 0.79: Table positions: movement was only sent unreliably while moving, so a lost last update left tables apart.
  WorldSync now sends a table's resting place reliably once it's still for 0.4 s, and every 3 s re-sends every
  table we moved (reliable, max 40); receiver skips positions it already has and doesn't override a table we're
  pushing right now. Load-verified.
- 0.80: Version shown in game: 'Gungeon Online vX' bottom-left of the title screen (SaveSlotLabel) and at the top of the Multiplayer menu (Plugin.ModVersion). Screenshot-verified.
- 0.81–0.88: Controller support for the menus. Causes found in the game's code:
  MainMenuFoyerController.Update refocuses Continue/NewGame whenever none of ITS buttons (NewGame, Controls,
  XboxLive, Quit, Continue) has focus -> our button goes in the unused XboxLiveButton slot. The copied button
  keeps its UIKeyControls (up/down links), linked between the anchor and the anchor's old "down"; pause-menu
  links refreshed by visible position every 0.5 s (falls back to the original down link). MenuGuard.cs: while our
  menu is open (+0.3 s after): InControlInputAdapter.ProcessPrimary/SecondaryPlayerInput (controller -> dfGUI
  menus), MainMenuFoyerController.Update and GameManager.Unpause are skipped. MultiplayerMenu: Btn/Tog helpers
  number every control; D-pad/stick/arrows move, A/Enter press (applied in the Layout pass), B/Esc back, scrolls
  to the selection, ignores input for 0.25 s after opening. Verified on the title screen with arrow keys:
  select MULTIPLAYER, open, move, Back, then down to QUIT. Pause menu not re-tested.
- 0.89 (protocol 31): Guest waits at the exit. LevelSync LoadNextLevel/LoadCustomLevel prefixes are now bool: a
  joiner's own mid-run load (not a run start, not a follow) is held - DelayedLoadNextLevel_CR already set
  GameManager.m_loadingLevel, so it's reset to false - joiner sends AtExit (45, host shows a toast) and shows
  "Waiting for <host> to take the exit..." until the host's LevelLoad arrives (OnLevelLoad marks followLoad and
  loads the host floor). Host disconnects while waiting -> our own load goes ahead (bypassWait). Load-verified.
- Compat: Gungeon Go Vroom 1.7.2 (CaptainPretzel, incl. OptimizeIMGUI) installed manually in Bare plugins/CaptainPretzel-Gungeon_Go_Vroom. Two-copy LAN test: both load, connect (10 ms), F8 IMGUI window draws, partner puppet (Robot) shown in the Breach. Both players need it with identical settings (Shuffle Fix / random cell lookup change floor generation).
- 0.90 (protocol 32): ModSettingsSync.cs - host sends Gungeon Go Vroom's Gunfig options (GGV.ConfigMenu._Gunfig + submenus) on connect and on change (2 s poll); joiner Gunfig.Set()s differing values, SaveActiveConfigsToDisk, toast to restart once (GGV applies patches at startup); joiner without the mod gets an install hint. Verified: host Shuffle Fix=Enabled, guest file set to Disabled before launch -> on join 'changed 1 settings', file saved back to Enabled.
- 0.91–0.97: TheGarbageCollector 1.4.5 (ApacheThunder) installed in Bare (manual). Memory test (private MB, Breach):
  game alone ~33 MB/min; two connected copies ~190 MB/min. AllocProfiler.cs (--gungeon-online-allocprof /
  launch-test-copy -AllocProf) logs per-subsystem managed allocations every 30 s: session.Update = 60 MB/30 s.
  Cause: Mono UdpClient.Receive allocates a 64 KB buffer per packet -> LanLink/LanDiscovery now ReceiveFrom into a
  reused 64 KB buffer and copy n bytes (Steam path already reused a buffer). Also: cached CurrentLevelKey,
  puppet Status no per-frame text (Details() for F8), Steam presence compared by value, Protocol.Build and the
  receive BinaryReader reuse one stream, StringBuilder for settings signatures. After: host session.Update
  ~0.5-2 MB/30 s, memory flat while connected.
  Cultist stand-in CharacterSelectIdleDoer: OnEnable/Update skipped (its idle coroutine NRE'd). Select-screen
  card: CHR_CultistPanel (exists as an asset, not wired to the coop flag) used as the flag's OverheadElement -
  screenshot shows the real card.
  Guest at the Breach elevator while the host is still in the Breach: held ("Waiting for <host> to start the
  run...") until the host's run-start LevelLoad, then follows it directly (no quick restart).
- 0.98: Partner name tag drawn above the drawn (smoothed) puppet's renderer bounds (RemotePuppet.TryGetHeadTop) instead of the newest network position; centered, bold, shadowed, text cached. New config Display/ShowPartnerName (default on), toggle in the Multiplayer menu (connected), client-side only. Screenshot-verified.
- 0.99: 'Show partner's name' toggle now in the Multiplayer menu for both states (connected and not) right under the name row, and in the F8 window under the name row. Screenshot-verified in F8.
- 0.100: Test-run mode for automated floor tests: `--gungeon-online-test-run <PrefabSuffix>` (launch-test-copy
  -TestRun PlayerRogue) picks that character's flag on the Breach select screen; the host starts the run 8 s after
  both have a player. launch-test-copy -Profile <name> (only Bare is auto-deployed). OnlineTest Gale profile = copy
  of Default (30 mods) + Gungeon_Online-dev; Default untouched.
- 0.101: Default-modpack test (OnlineTest). Found: (1) Expand the Gungeon's ExpandJungleTreeController.Start
  adds Keep_JungleElevatorRoom or ...Room2 after generation via BraveUtility.Shuffle/RandomElement (unseeded
  UnityEngine.Random) -> host/guest differ. ExpandSync.cs patches its <Start> MoveNext (found by name on the first
  frames, Expand loads after us): online, Random.InitState(runSeed*31 + tree x*7919 + y) around each step, state
  restored after. (2) FloorCheck reloads produced a totally different layout: GameManager.set_CurrentRunSeed only
  redoes "STARTUP SEED DATA" (MetaInjectionData.ClearBlueprint + PreprocessRun, RewardManifest) when the seed
  changes or m_forceSeedUpdate; generation uses up the blueprint, so a same-seed reload diverged. ReloadHostFloor
  now sets m_forceSeedUpdate first. Retest: 23-room floor matched (CD698E77); no jungle tree and no reload on that
  floor, so both fixes are untested in play. Pre-existing, not ours: Emulate the Gungeon MissingMethodException
  (ETGMod Chest DOnPreOpen), AudioAnimatorListener.Start NRE.
- 0.102-0.104 (protocol 33): Up to 4 players (Protocol.MaxPlayers). Slots: host 0, guests 1-3. Every message is
  [type][sender slot][body] (Protocol.LocalSlot). Transport is multi-peer by slot (Send(slot|Everyone), OnPacket(slot,..),
  OnLost(slot,..), DropPeer(slot)); SteamLink keeps a CSteamID per slot, LanLink a Peer (endpoint, seqs, unacked) per
  slot; the host puts newcomers in the lowest free slot. NetSession: Players[slot] (Name, Connected, PingMs, LastHeard);
  host stamps the sender slot, handles the message, then relays it to the other connected guests (not Hello/Ping/Pong/Bye/
  Inventory/InventoryRequest/AtExit; unreliable types relayed unreliably). Hello carries the guest's slot back; Roster (47)
  lists everyone; per-guest timeouts. While a message is handled, From/Remote/PeerName = its sender; during a newcomer's
  hello every Send goes only to them (onlyTo); SendReliableTo for InventoryRestore. Per-player: puppets/pets
  (Plugin.Puppets/Pets), name tags, map markers, enemy target bodies (closest player, TargetSync.BodyOf), orbitals, beams,
  bullet ids (netId*4+slot), dropped-gun ids, Master Round hits, pause (anyone), deaths (game over when everyone is down,
  a chest revives all ghosts), petting (PetStart/Stop carry the owner slot), teleport (sender or closest). Enemy states:
  the lowest slot alive in a room reports. Money pickups count 1/players-in-run. Test: three LAN copies (host Rogue,
  Guest1 Robot, Guest2 Guide joining mid-run via the elevator; test-run guests now depart once the host is in a run):
  slots 1/2/3, all floors match (CFC40EB9), every game shows the other two characters + map icons, 0 exceptions.
  tools/hold-key.ps1 holds a key in a copy (walking). Not yet tested: Steam with 3+, combat/deaths/chests with 3.
- 0.105-0.106: Rules/TeleportToFights (default on, host rule sent in Rules as a 3rd bool): when a player's room seals
  (RoomHandler.SealRoom) they send PullGhost; receivers that are alive warp to the sender (PlayerWarp, 1 s delay) unless
  already in that room, dead, or in a boss room. Toggle under the name toggle in the Multiplayer menu and the F8 window
  (guests see the host's choice). Multiplayer menu toggles now show [ON]/[OFF] (the label style draws no checkbox);
  "Half money" shown as "Split money". Verified: guest log "teleport to fights on", F8 shows it. Fight warp itself not
  tested in a real fight yet.
- 0.107: Tables pushed client-side: TablePush.cs gives every other player a "feet" SpeculativeRigidbody (copy of our
  PlayerCollider box, 14x4 px; CanPush, CollideWithOthers, no tilemap) that follows their smoothed transform by
  Velocity (jumps when >2 tiles off), so the physics engine pushes flipped tables (FlippableCover sets CanBePushed)
  in every game. OnPreRigidbodyCollision skips everything that isn't a FlippableCover (PhysicsEngine calls both
  bodies' callbacks, so enemies/bullets/us pass through). WorldSync: a table that moved while another player's feet
  were closer than ours is their push (RemotePushAt) and isn't reported by us; positions from the pusher arriving
  then are kept as Final and glided to 0.4 s after our copy stops. Seen before: 'Partner broke Table ... closest 1.99
  away' (push not synced). Fight teleport fixed: SealRoom check also uses the room we stand in (CurrentRoom may lag);
  logs 'Fight started in X' / 'X started a fight'. Verified: host walked into Castle_Joe_Square_011, guest got
  'Fight! Bringing you to Host.' and warped in.
- 0.108: Friendship Cookie (HealPlayerItem.DoesRevive): online CanBeUsed only while another player on our floor is a
  ghost; DoEffect sends Revive (now with a target slot byte, NoSlot = all ghosts) to the closest ghost, who comes back
  at the user (ResurrectFromChest path, no chest). Untested in game.
- 0.109 (not yet rebuilt with the next three): 4-copy LAN test (Host Rogue, Guest1 Robot, Guest2 Guide, Guest3 Convict):
  all connected (slots 1-4), all floors matched, fight teleport pulled all 3 others in 4 fights. Found:
  (1) Guest3 timed out on the host but kept "playing": the host dropped its slot, then ignored its non-hello packets,
  while the host's pings (sent to every link peer) kept Guest3 from timing out itself; on LAN the new Peer also
  restarted reliable numbering at 0 so nothing got through. Fix: host answers packets from a not-hello'd slot with
  Hello(askBack=true) (1/s); the guest then says hello again. LanLink reliable packets/acks now carry a per-peer
  random epoch; a new epoch resets the receive numbering. (2) A cleared room locked again in some games
  (reinforcement waves after the leftovers were killed), so those players couldn't follow to the next fight (warp
  refuses while in a locked fight). Fix: rooms another player cleared are remembered per floor and kept open/emptied
  (KeepClearedRoomOpen, every 0.5 s). (3) Tables: no "touched table"/"moved by us" lines at all; still unexplained.
  Noise: "no unbroken match" warnings flood with 4 players (same object broken in every game).
- 0.110-0.114: TABLES FIXED. A FlippableCover's SpeculativeRigidbody (and sprite) is on a CHILD ("Table_Vertical"
  under "Table_Vertical(Clone)"); pushing moves the child, the root never moves. WorldSync watched/moved the root, so
  pushes were never seen; TablePush skipped tables because GetComponent<FlippableCover>() on the child is null.
  Now: tables are identified by the root ("home") but tracked/sent/glided by the body's transform (Mover/Pos/MoveBy,
  MoveBy also does OnPostMovement's shadow + z-depth); TablePush uses GetComponentInParent. Walking into a flipped
  table pushes it (slides, ~8 tiles in 2.5 s). Verified 2 copies: host flipped + pushed, both games' body at
  (156.6, 88.5), guest log "Player 1's body touched table". Open: guest's game logged flipping an unrelated table
  (Table_Horizontal, home 166,97) on its own once - watch for it.
  Test keys (test copies only): K = warp 2 tiles below the nearest unflipped table (Resources.FindObjectsOfTypeAll:
  unvisited rooms are inactive), L = log health + that table's state. tools/hold-key.ps1, tools/roll.ps1.
  Skip Intro bug (garbled guest heart bar, Robot turned into Marine): the intro lives in tt_foyer; a test guest
  reached the Breach before the intro ever ran, the intro then started at run start and our skip made the game
  quick-start with the last character. Skip Intro 0.4: only while no player exists, flags reset after. Verified.
- 0.115-0.116: Rejoin from the Breach. A guest that went back to the Breach while still connected picked any
  character, got its old items added (the new character's active item dropped: one slot) - the old-character
  pick only ran on reconnect (RejoinOffer). Now the guest remembers its own run character + seed while in a run;
  back in the Breach while the host is still in that run, it sets rejoinCharacter itself, so UpdateRejoin picks the
  old character on the select screen and departs into the host's run; items come back via InventoryRequest.
  Floor state for late arrivals: the host sends a player who arrives on its floor (slot's Scene becomes ours) every
  room already cleared (clearedSent + clearedByOthers) as RoomCleared (SendReliableTo) - KeepClearedRoomOpen empties
  them on entry. Cookie revive verified in play (guest used Friendship Cookie, host revived).
  Test key B = back to the Breach. Verified: guest B -> "rejoin as PlayerRobot" -> picked -> rejoined, host
  "sending back their items" + "1 rooms already cleared", host shows Robot again, floor matches.
- 0.117-0.124 (protocol 34): Autopilot.cs = "Take a Break". Drives our own player by answering the player-0
  GungeonActions in a PlayerActionSet.Update prefix (CommitWithValue per action, then the internal one/two-axis
  Update by reflection); aims with PlayerController.forceAimPoint. Brain: closest visible enemy (lead 0.15 s, keep
  3.5-8 tiles, strafe, flip side every 1.2-2.5 s), sidestep/roll from bullets whose line passes within 1.1 tiles
  (roll if < 0.3 s, 1.2 s cooldown), reload empty clip, follow the closest teammate (> 3 tiles) via
  Pathfinder.GetPath(..., CellTypes.FLOOR, canPassOccupied: true - false failed through closed doors), KeepSafe turns
  away from wall/pit cells, Unstick sidesteps after 0.5 s of no movement (door frames). Off on any key/mouse button
  or pad input after a 1 s grace (menus/pause extend the grace). F6 (TakeABreakKey) or the toggle in both menus.
  PlayerState carries OnBreak -> name tag "Name (bot)". Test copies: launch-test-copy -Bot (--gungeon-online-bot)
  keeps it on; the host's bot explores (nearest unvisited room; the room we're in counts as seen; gives up on a
  room after 6 s without progress). Verified 2 bot copies: 7 fights, guest died -> ghost followed the host's bot into
  the next fight, floor matched, no exceptions. Next: rejoin in bot mode, host stand-in for dropped players.
- 0.125-0.130: Autopilot steering by the feet (PrimaryPixelCollider.UnitCenter), path look-ahead (farthest waypoint
  with a clear 0.8-wide line), eased direction changes, pit-only cell check + physics feel-ahead
  (PhysicsEngine.RigidbodyCastWithIgnores 8 px; doors and our helper bodies ignored; walls: turns <= 40 deg then keep
  pushing so the game slides us, objects: up to 90 deg). Map-based exploring: unseen rooms connected to seen ones,
  never SECRET, nearest by path length. Loot only when EveryoneIsABot (all remotes OnBreak): nearest chest (locked
  only with keys) / ownerless gun in a seen, cleared room -> walk there, press interact (0.7 s), give up after 5
  presses or 15 s. Split money toggle moved next to the other host rules in both menus. BreakButton.cs: online the
  pause menu's "Exit Coop" (Breach) becomes "TAKE A BREAK" (enables, Autopilot.Start, unpause); offline restored.
  Black screen fix: a guest's held Quick Restart (death screen) left GameManager.m_loadingLevel set by the button, so
  DelayedQuickRestart_CR quit at once when the host restarted. Now cleared when holding and before restarting.
  Verified: both die (test key X), guest Space (Quick Restart) then host Space -> guest joined the new run, floors
  matched. Not yet seen in play: bot looting, Take a Break pause button.
- 0.131-0.133: STAND-IN (StandIn.cs). Autopilot split into Brain (per player index; 0 = us, 1 = co-op player 2) +
  Autopilot facade (Self, list of brains, one PlayerActionSet.Update prefix asks each brain). Host: a guest who leaves
  (timeout or Bye) mid-run with remembered gear (InventorySync, same seed) gets a stand-in: game switched to
  COOP_2_PLAYER, their character prefab (CustomCharacterSupport.FindPrefab or BraveResources.Load) spawned as
  SecondaryPlayer (PlayerIDX 1), next frame ConvertCoreUIToCoopMode / ClearPlayerCache / ghost collision exceptions /
  ReassignAllControllers, 0.6 s later the remembered gear (InventorySync.Apply). A Brain(1) drives it (follows the
  host or closest player, fights). Host sends its PlayerState (onBreak) and shots as the slot (Protocol.SendAs);
  roster keeps the slot with the player's name. Camera ignores it, co-op doors don't wait for it. Its gear is
  re-remembered every 2 s; every chest opened in the host's game also rolls the stand-in its own contents
  (Chest.GenerateContents for it, LootEngine.GivePrefabToPlayer). Hello from the same name: position kept, gear
  remembered, removed (single player again); InventoryRestore now carries hadStandIn + position -> the returning
  guest warps there and starts in bot mode (Autopilot.Start, "move to take over"). Run end (ClearActiveGameData,
  LoadCharacterSelect) removes it. Mid-run the host no longer resets the seed when the last guest leaves.
  One stand-in at a time (the game has one extra player). Verified: guest process killed -> host "keeps playing as a
  bot", Robot stand-in with co-op HUD; guest relaunched -> "stand-in hands over", rejoined as Robot, "Your bot is
  still playing - move to take over", floor matched. Chest share not yet seen in play.
- 0.134: Stand-in fixes. Lost guns on rejoin: the stand-in's first update remembered its STARTING kit as the
  player's gear before ApplyRemembered ran (0.6 s after spawn), so the real gear was overwritten. Now nothing is
  remembered until the gear is on (geared flag). Player 2's HUD (hearts, blanks, ammo/gun, item, name labels) is
  hidden on the host after ConvertCoreUIToCoopMode and re-hidden every 0.5 s. Test key G = give an AK-47.
- 0.135 (protocol message 48 RestartRequest, guest -> host): a guest's held Quick Restart now asks the host; a host
  on the death screen presses its own death page Quick Restart (AmmonomiconDeathPageController.DoQuickRestart),
  otherwise gets a toast "X wants to restart the run - press F7" (AcceptRestartKey, 20 s). The guest's waiting
  screen says "Asked <host> to restart the run... (Esc: go to the Breach instead)"; Esc clears the hold and loads
  the Breach. 0.134/0.135 not yet tested in game (the user's own Bare copies were open on the LAN port).
- 0.137: Money desync fix (4-player test showed 7/7/5/0). The host's purse is authoritative: guests send Wallet deltas
  to the host only (HostOnly), the host applies them and broadcasts WalletState (49: level, money, keys) on every
  change and every 2 s; guests set their purse to it. Before: deltas every-to-every, dropped whenever the sender or
  receiver wasn't "in a shared run" (others loading), and starting amounts never reconciled. Verified: 26 and 42 in
  all four games.
- 0.138: Bots followed the closest teammate, so three guest bots followed each other and froze while the host's bot
  explored. Leader now: a real (non-OnBreak) player first, else the host, living before ghosts. "We're in a boss
  room" skipped fight pulls in the boss foyer; now only while the boss room is sealed. Verified: 4 bots stayed
  together, ~9 fights, 0 errors. Noise: "Couldn't join the fight: you're in a locked fight" when already in the same
  fight (harmless).
- 0.139: DifficultySync.cs - enemy health x (1 + HealthPerExtraPlayer (0.5) x (players - 1)), stand-in counts. Host
  computes, sent as a 4th field of Rules (re-sent with every roster change); AIActor.Start postfix at
  Priority.Last, after Loop The Gungeon's AIActorStartPatch.TryApplyHpMultiplierNonStacking, so they multiply. Toggle
  in both menus (host), guests see "Enemy health: xN". Not yet tested.
