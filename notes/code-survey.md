# Code survey (milestone 1)

Surveyed 2026-11-18 against build 26435298 (Linux, Unity 2019.4.41f1), from the
ILSpy output in `decompiled/`. Line numbers refer to those decompiled files and
will shift with any game update; method names are what the patches bind to.

Method names and behaviour only: no game code is copied here.

Items marked **(unverified)** were inferred, not read in the code.

## Summary: the patch list

| System | Host: observe | Guest: block or redirect | Mod calls to apply remote input |
|---|---|---|---|
| Orders | — | `Player.IssueCommands`, `Player.ObjectClicked`, `Player.StartCasting`, `Player.CancelAction`, `PartyMemberAI.TriggerAbility` | Build an `AI.Player.*` state and `StateManager.PushState(state, true)` |
| Selection | `PartyMemberAI.OnAnySelectionChanged` | `PartyMemberAI.Selected` setter (refuse characters not owned) | — |
| Party AI | — | `AIController.GetAutoAttackAggression` → `Passive` | — |
| Pause and speed | `TimeController.UpdateTimeScale` postfix | `TimeController.UpdateTimeScale` prefix (write host value) | `TimeController.Paused` setter |
| Area change | `GameState.ChangeLevel(MapData)` prefix | same method (skip unless host-driven) | set `Player.StartPointLink`/`StartPointName`, call `GameState.ChangeLevel(MapData)` |
| Level ready | `GameState.OnLevelLoaded` event | same | — |
| Save handoff | `GameResources.SaveGame(name, "")` then `SaveGameInfo.WaitUntilSafeToSaveLoad()` | `GameState.Autosave` (skip) | `GameResources.LoadGame(name)` |

| Guest simulation | — | `AIStateManager.Update`, `ScriptEvent.RunScriptHelper`, `GameState.UpdateIsInCombatAndGameOver` (see section 8 for the full list) | same methods behind a bypass flag |
| Dialogue | `FlowChart.MoveToNode` postfix, `ConversationManager.StartConversation`/`EndConversation` | `UIConversationManager.PlayerInput` and `Continue` (send instead) | `ConversationManager.StartConversation`, `FlowChart.MoveToNode(nodeId, player)` |
| Combat results | `AttackBase.Launch`, `Health.DoDamage`, `CharacterStats.ApplyStatusEffectHelper`, `Console.AddMessage(ConsoleMessage)` | `AttackBase.OnImpact`, status-effect ticks | write health and stamina, `Health.HandleUnconscious`/`HandleDeath` |

## What the survey changes in the plan

1. **Turn-based mode is a full system, not a small extra.** `TacticalModeManager`
   runs an initiative queue with per-actor turn events, and it is a per-save
   setting (`GameState.Option.TacticalMode`). The initial milestone-1 recommendation
   was to refuse saves with it enabled; that recommendation is superseded. As of
   plugin 0.9.0/protocol 7, turn-based is the primary, most polished co-op mode,
   with RTwP retained as secondary. See the final milestone-6 through milestone-8
   findings below.
2. **There is no "apply this final damage number" call.** Every public damage
   method re-applies armour and difficulty multipliers. The guest writes health
   and stamina directly and drives incapacitation visuals separately; full damage,
   unconscious, death and revival handlers remain host-only. Milestone 6
   replicates gameplay *state* (health, stamina, effect list), not damage-event
   replays. Native tactical presentation events are reconstructed separately from
   ordered host snapshots and do not roll gameplay results.
3. **Freezing guest AI also freezes guest combat detection.** `GameState.InCombat`
   is derived from AI state stacks. The guest must be told by the host.
4. **Cutscenes are C# coroutines, about 25 hand-written classes.** They do not go
   through the script dispatcher. For first playable: block them on the guest,
   show a "host is in a cutscene" overlay, resync positions afterwards.
5. **Milestone order still holds.** Nothing found blocks milestones 2 to 5.

## 1. Party and selection

**Party list.** `PartyMemberAI.PartyMembers` is a static array of 30, indexed by
`Slot`. Slots 0 to 5 are the primary party; slot + 6 is that member's animal
companion; summons take later slots. Roster changes go through
`PartyMemberAI.AddToActiveParty`, `AddSummonToActiveParty` and
`RemoveFromActiveParty`, and raise the static `OnPartyMembersChanged`.

The main character's `Player` component is `GameState.s_playerCharacter`.

**Selection.** Stored per member in `PartyMemberAI.Selected`, mirrored into the
static `SelectedPartyMembers` array. The `Selected` setter is the only write
point: it refuses uncontrollable members and raises `OnSelectionChanged`, which
is relayed to the static `PartyMemberAI.OnAnySelectionChanged`.

About 30 call sites set `Selected` (clicks, drag box in `Player.PerformSelection`,
portrait bar, number-key groups, gamepad, cutscenes, tactical mode). Patch the
setter, not the callers.

**Ownership rule for split party.** Prefix the `Selected` setter: on each
machine, refuse `true` for a character that machine does not own. Everything
downstream (orders, formation, ability bar) works on the selected set, so this
one patch enforces most of ownership on the UI side. The host must still
validate ownership on every received command.

## 2. Orders

All player orders are built by `Player` and end as a pooled AI state pushed on
the character's state machine:

- allocate: `AIStateManager.StatePool.Allocate<T>()`
- push: `memberAI.StateManager.PushState(state, clearStack: true)`
- with the QUEUE key held: `StateManager.QueueState(state)` instead

The game sets the state's fields after the push. That is safe because `OnEnter`
only runs on the next `AIStateManager.Update`. A patch on `PushState` therefore
sees empty fields; patch the `Player` methods instead.

| Order | Built in | State | Fields that matter |
|---|---|---|---|
| Move | `Player.IssueCommands` (private) | `AI.Player.Move` | `Destination`, `Range` (0.05), `ShowDestinationCircle` |
| Attack-click | `Player.IssueCommands` | `AI.Player.Attack` | `Target`, `IsAutoAttack = false` |
| Use door, container, NPC, transition | `Player.ObjectClicked(Usable)` | `AI.Player.UseObject` | `UsableObject` |
| Targeted ability or spell | `Player.StartCasting(...)` then the confirm click inside `Player.Update` | `AI.Player.TargetedAttack` | `Ability`, `AttackToUse`, `WeaponAttack`, `Target`, `TargetPos`, `Forward`, `FullAttack`, `StatusEffects`, `AnimVariation` |
| Non-attack ability | `PartyMemberAI.TriggerAbility(GameEventArgs, GenericAbility)` | sets `QueuedAbility` or pushes `AI.Player.Ability` | `QueuedAbility` |
| Cancel | `Player.CancelAllActions` → `Player.CancelAction(GameObject)` | pops all states | — |

Orders that bypass the state machine: `Stealth.SetInStealthMode`,
`Equipment.SelectNextWeaponSet`, quick-item equip in `UIAbilityBarButton`.

**Consequences for the command protocol:**

- **Send resolved destinations.** Formation positions come from
  `PartyMemberAI.CalculateFormationPosition`, which depends on which characters
  are selected locally. The guest computes its own characters' destinations and
  sends one position per character; the host does not recompute.
- **Do not replay ability-button presses on the host.** The ability path is
  `GenericAbility.TriggerFromUI` → `GenericAbility.Update` → `TriggerAbility` →
  `Player.StartCasting`, which puts the *local* cursor into targeting mode and
  needs the caster selected locally. The guest does its targeting locally, then
  sends the final `TargetedAttack` field set; the host builds the state directly.
- **The confirm click is not its own method.** It is a block inside
  `Player.Update`, so there is no method to prefix for it.
- **Proposed capture point: harvest the stack.** The guest's AI state machines
  are frozen (section 8), so a pushed order just sits on the stack with its
  fields filled in. A postfix on `Player.Update` can scan the guest's owned
  characters, turn any new `AI.Player.Move`, `Attack`, `TargetedAttack` or
  `UseObject` into a command, and pop it. That covers every order type,
  including the confirm click, with one patch and no transpiler. This is a
  design idea from the survey, not something tried yet; settle it in milestone 5.
- **The game reads the local QUEUE key** inside `TriggerAbility` and the casting
  code. Send "queued" as a flag in the command.

## 3. Party AI that acts by itself

Party members auto-attack and auto-cast unless told not to. Sources:
`AI.Player.Wait.Update` and `AI.Player.Attack.Update` (both call
`PartyMemberAI.AutoPickNearbyEnemy`), `AIController.HandleDamagedEvent`,
`AIController.HandleMeleeEngagedEvent`, and instruction-set autocasting through
`CasterTargetScanner`.

All four auto-attack paths check `AIController.GetAutoAttackAggression()`.
Returning `Passive` from a prefix silences them. Autocasting is governed by
`PartyMemberAI.UseInstructionSet`.

For co-op this matters only on the guest (the host runs party AI as normal for
everyone, and the result replicates). On the guest all AI is off anyway, see
section 8.

Not covered by either switch: pathing nudges from `PathFindingManager`, modal
abilities relaunching from `GenericAbility.Update`, hit-react states.

## 4. Identity

- `InstanceID.Guid` is the persistent id. Look up with
  `InstanceID.GetObjectByID(Guid)`. Only enabled objects are in the table.
- Companions have fixed Guids (`CompanionInstanceID`).
- Party slots 0 to 5 have fixed alias Guids in `SpecialCharacterInstanceID`
  (`s_slotGuids`), registered by the `PartyMemberAI.Slot` setter. These resolve
  the same on both machines and are the simplest party-member id.
- Runtime spawns through `GameResources.Instantiate` keep the prefab's Guid for
  the first copy and get `Guid.NewGuid()` for later ones, so they differ per
  machine. **Summons need a host-assigned id**, as the plan assumed.
- Abilities are instantiated with a fresh Guid each (`CharacterStats.InstantiateAbility`).
  **Identify an ability by owner plus its index or name, never by Guid.**
- Projectiles have no id **(unverified)**. Treat them as visual only on the guest.

## 5. Pause and time

State lives in `TimeController`: a player pause flag, a UI pause flag (set by
windows whose `UIHudWindow.PausesGame` is true) and a speed (1, slow 0.2,
fast 1.8). `TimeController.UpdateTimeScale()` combines them and is the only
gameplay writer of `Time.timeScale`; it runs every frame.

Pause is requested from many places (hotkey and HUD via
`UIActionBarOnClick.HandlePause`, gamepad via `GameInput.HandlePause`,
`GameState.AutoPause`, window open and close in `UIWindowManager`, focus loss,
scene load, cutscene start). All of them go through the `Paused`, `UiPaused` or
private `TimeScale` setters.

**Plan:**

- Host: postfix `UpdateTimeScale`, send `Time.timeScale` when it changes.
- Guest: prefix `UpdateTimeScale`, write the host's value, skip the original.
- Guest pause requests: prefix `UIActionBarOnClick.HandlePause` and
  `GameInput.HandlePause` to send a command instead.
- Resolved in milestone 7: private local UI such as a guest inventory does not
  pause the host and does not consume or block the next shared pause command.
  Shared pause/time remains host-authoritative; see the final milestone-7
  findings below.

The world clock (`WorldTime.CurrentTime`) advances from `Time.deltaTime`, so it
follows pause, but it will drift between machines. Sync it from the host
periodically. Jumps go through `WorldTime.AdvanceTime*` (rest, travel).

## 6. Scenes and saves

**Area change.** Every map change funnels into
`GameState.ChangeLevel(MapData)`. It saves the current level to the temp
folder, then starts the `GameResources.LoadScene(name, false)` coroutine.
Callers: `SceneTransition` (after the whole party has walked to the exit),
the world map (`UIWorldMapManager.DoTransition`), and the script call
`Scripts.AreaTransition`, the last two via
`UIDifficultyScaling.PromptScalersAndChangeLevel`.

The destination is not a parameter. It is set on the player first:
`Player.StartPointLink` and `Player.StartPointName`. `StartPoint.SpawnPartyHere`
places the party after load. A same-map transition does not load anything; it
calls `PartyWaypoint.TeleportPartyToLocation`.

**Level ready.** `GameState.FinalizeLevelLoad` raises the static
`GameState.OnLevelLoaded`, then clears `GameState.IsLoading`. Persistent objects
are already restored by then. It also calls `GameState.Autosave()` after a
transition; skip that on the guest.

"In a level and playable" = `GameState.IsInGameSession && !IsLoading &&
!IsInMainMenu && !IsInCharacterCreation`.

**Mirroring a transition on the guest.** The original design was for a host
prefix on `ChangeLevel(MapData)` to send the scene name and two start-point
values, then have the guest call `ChangeLevel` itself. Milestone 7 supersedes
that direct mirror with a semantic host request, a save-backed transition and
correlated world-ready acknowledgements. The guest does not run native
`ChangeLevel` or autosave independently; see the final milestone-7 findings.

**Save format.** One `.savegame` zip per save in `GameResources.SaveGamePath`
(on Linux `~/.local/share/PillarsOfEternity/SavedGames` **(unverified)**).
It contains `saveinfo.xml`, one `.lvl` per visited level, `MobileObjects.save`,
fog files and images. Object state is keyed by `InstanceID` Guid.

**Save handoff (milestone 3).**

1. Host: `GameResources.SaveGame("<name>.savegame", "")`. Returns false in
   combat or while loading; those are the only checks inside it.
2. Host: `SaveGameInfo.WaitUntilSafeToSaveLoad()`. The zip is written on a
   background thread; this joins it. Then read the file.
3. Guest: write the bytes into `SaveGamePath`, call
   `GameResources.LoadGame("<name>.savegame")` (name relative to that folder,
   extension included).
4. Guest is ready on `GameState.OnLevelLoaded`.

Remove the handoff file afterwards with `GameResources.DeleteSavedGame` so it
does not clutter the save list.

Joining while the host is in combat is deferred: the guest waits for combat to
end, then receives the normal save handoff. Mid-combat save handoff remains
outside the implementation.

**New game** does not use `GameResources.LoadScene`; it loads the first scene
directly and runs character creation in the level. Co-op should start from a
loaded save only.

## 7. Dialogue

Conversations and scripted interactions (the illustrated text scenes) share one
system. `ConversationManager.Instance` holds the running `FlowChartPlayer`
objects; each has a `CurrentNodeID`. The node classes themselves are in a
separate assembly (`OEIFormats`), not in `decompiled/`.

| Step | Method |
|---|---|
| Start | `ConversationManager.StartConversation(string file, int startNode, GameObject owner, DisplayMode, bool disableVo)`; scripted interactions use `StartScriptedInteraction(file, owner)` |
| Every node change | `FlowChart.MoveToNode(int nodeID, FlowChartPlayer)`: runs exit scripts, sets the node, runs enter scripts |
| Response list | `Conversation.GetResponseNodes(player, qualifiedOnly)`, filtered by reflected conditionals |
| Player picks | private `UIConversationManager.PlayerInput(int index)` → private `MoveToNode(int nodeId)` |
| Continue an NPC line | private `UIConversationManager.Continue()` → `FlowChart.GetNextNode(player)` → `MoveToNode` |
| End | `ConversationManager.EndConversation(FlowChartPlayer)` |

There are no start, end or node-entered events, only the
`FlowChartPlayerAdded`/`FlowChartPlayerRemoved` delegates. Postfix
`FlowChart.MoveToNode` for node changes.

**Points that shape the sync:**

- **Send node ids, never response indexes.** The index depends on locally
  evaluated conditionals and a display option.
- **The guest must not pick the next NPC node itself.** `GetNextNode` uses
  random picks for some node types.
- **The UI follows state by polling.** `UIConversationManager.CheckRecreateContent`
  compares the manager's active conversation and node with its own copy every
  frame and redraws. So on the guest, calling `StartConversation` and
  `MoveToNode` is enough to show the dialogue; no UI calls needed.
- **Node scripts must not run on the guest.** They change globals, quests and XP
  through `ScriptEvent.RunScriptHelper` (blocked, section 8).
  `Conversation.TransitionToNode` also calls `QuestManager.TriggerTalkEvent`
  directly; block that too.
- **Response conditionals read local state** (stats, globals, quest state). They
  only show correctly on the guest if globals and quests are synced. For first
  playable the host can send the list of qualified response node ids with each
  node.
- **Who picks.** Resolved in milestone 7: the conversation starter is the sole
  picker. Picks and Continue go to the host as node/revision-checked commands;
  a host who is not the picker is rejected too. Both peers read the same
  conversation, while conversation flow scripts execute only on the host.
- **Pause.** The conversation window pauses through `UiPaused` like any other
  window **(unverified: the flag is set on the prefab, not in code)**.

## 8. Freezing the guest simulation

The guest prefixes these to skip the original. A static bypass flag lets the
mod call the same methods when applying host data.

| Method | Stops |
|---|---|
| `AIStateManager.Update()` | All AI thinking, party and NPC. One method covers every controller type |
| `ScriptEvent.RunScriptHelper(ScriptCall, Object)` | Every data-driven script action: area scripts, conversation node scripts, quest and global changes |
| `Trigger.NotifyTriggerEnter` / `NotifyTriggerExit` | Trigger volumes (and their persistent trigger counts) |
| `TriggerConversation.OnTriggerEnter`, `TriggerGlobalVar.OnTriggerEnter`, `TriggerQuestObjective.OnTriggerEnter` | Three trigger types that bypass the script dispatcher |
| `Cutscene.StartCutscene()` (and `RealtimeCutscene.StartCutscene`) | Cutscenes, which are C# coroutines in about 25 subclasses |
| `Encounter.Spawn()` (private) | Local enemy spawning |
| `QuestManager.TriggerTalkEvent` | Quest changes from dialogue |
| `AttackBase.OnImpact(GameObject, GameObject, bool)` | Hit rolls, damage and effect application |
| `GameState.UpdateIsInCombatAndGameOver()` (private) | Local combat detection and game-over |
| `GameState.Autosave()` | Autosaves of mirror state |

**Still to solve with AI frozen:**

- **Movement.** `Mover` is independent of AI and can be driven from outside:
  `Mover.PathToDestination(Vector3)` and `Stop()`, or write the transform.
- **Walk animation follows Mover speed, not AI.** Pathing the Mover gives walk
  animation for free; teleporting the transform gives none. Milestone 4 should
  path the Mover towards the host position and only snap on large error.
- **Action animations** (attack, cast) are set by AI states through
  `AnimationController.DesiredAction`. The guest must set that from host data.
- **Event-pushed states.** Damage and death events still push states
  (`HitReact`, `Dead`) on the frozen stack; they will not tick. Death and
  knockdown visuals need checking in milestone 6.
- **Status-effect ticks** run from `CharacterStats.Update`, not from AI, and
  apply damage over time through `Health.ApplyDamageDirectly`. They will
  double-apply on the guest unless blocked.
- **Stamina regeneration** runs in `Health.Update` on the guest too. Harmless if
  health and stamina are overwritten from host snapshots.
- **Encounter spawns need ids.** How spawned creatures get their `InstanceID`
  was not traced in the original survey. Milestone 6 resolved this with
  host-assigned ids for the tested dynamic encounters and summons; see below.

## 9. Combat

**One attack, end to end:**

1. `AttackBase.Launch(GameObject enemy, int variation)` (or the position
   overload) starts the animation. `AttackRanged` overrides it and spawns a
   `Projectile`.
2. Impact arrives from the animation hit event (melee) or
   `Projectile.HandleImpactHelper` (ranged). Both converge on
   `AttackBase.OnImpact(self, enemy, isMainTarget)`.
3. `AttackBase.CalcDamage` rolls damage, then
   `CharacterStats.AdjustDamageDealt(enemy, DamageInfo, testing)` does the d100
   hit roll and sets miss, graze, hit or crit on the `DamageInfo`.
4. `Health.DoDamage(DamageInfo, attacker)` applies armour, subtracts stamina and
   health, and calls `HandleUnconscious` or `HandleDeath`. Returns the final number.
5. `AttackBase.ApplyStatusEffects` / `ApplyAfflictions` →
   `CharacterStats.ApplyStatusEffectHelper` (private).
6. `AttackBase.PostAttackMessages` writes the combat log through
   `Console.AddBatchedMessage`; every log line ends in
   `Console.AddMessage(ConsoleMessage)`.

**Host capture (postfixes):** `AttackBase.Launch` (who attacks whom, for the
animation), `Health.DoDamage` and `Health.ApplyDamageDirectly` (then read
`CurrentHealth`/`CurrentStamina`), `CharacterStats.ApplyStatusEffectHelper`,
`CharacterStats.ClearEffect(StatusEffect)`, `Console.AddMessage(ConsoleMessage)`.

**Guest apply:** write the private `m_currentHealth` and `m_currentStamina`,
call `Health.HandleUnconscious(GameObject)` or `HandleDeath(GameObject)` when the
host says so, append log lines with `Console.AddMessage`. Afflictions can be
applied with `CharacterStats.ApplyAffliction`, but there is no id registry for
them **(unverified)** and effect ids differ per machine, so a name-to-prefab
lookup is ours to build.

`CharacterStats.SetAttackerToHitRollOverride(int)` exists and forces the next hit
roll against a defender. Not needed if the guest never rolls; noted in case
replaying events turns out better than replicating state.

**Combat state.** `GameState.InCombat` is recomputed each frame from AI state
stacks in `UpdateIsInCombatAndGameOver`, and raises `GameState.OnCombatStart` /
`OnCombatEnd`. On the guest it must be set from the host (private static
`s_isInCombat`). Many systems read it: saving, fast mode, area transitions,
stamina regeneration.

**Stealth.** `Stealth.SetInStealthMode(GameObject, bool)` per character;
detection runs in `Stealth.Update` and `AIController.HandleSuspicionDetection`.

**Turn-based mode.** This is now the primary co-op mode, superseding the initial
recommendation to refuse tactical saves. The implementation uses the surveyed
`TacticalModeManager.EnterCombat`, `FinishTurn`, `DelayTurn`, `CurrentActor`, and
static `OnTurnStart`/`OnTurnEnd` entry points, together with ordered host
snapshots and native guest presentation-event reconstruction. The guest does not
run an independent initiative scheduler. See the final milestone-6 findings for
acceptance coverage and limits.

## RNG

`OEIRandom` wraps one static, time-seeded `System.Random` with no seeding API
(about 114 call sites; the attack roll is `DieRoll(100)` in
`CharacterStats.AdjustDamageDealt`). `UnityEngine.Random` is also used directly
for loot (seeded per container, position and day in `Loot.SetSeed`), AI
instruction choice, random dialogue branches (`Conversation.GetNextNode`) and
shuffles. This confirms the plan: no lockstep, the guest never rolls.

## Findings from milestones 2 and 3

- **Two copies on one PC.** Unity refuses a second copy while `boot.config` has a
  `single-instance` line, and the game's `GameInstanceLocker.Awake` holds a lock
  file. `scripts/run-instance.sh` and `Patches/DevPatches.cs` bypass both for testing.
- **Save folder depends on how the game is launched.** It follows `XDG_DATA_HOME`.
  Started from a terminal it is `~/.local/share/PillarsOfEternity/SavedGames`;
  started through the Steam snap it may be a folder inside the snap **(unverified)**.
- **`GameState.IsLoading` is not set during a new game's first level load**, because
  that path does not go through `GameResources.LoadScene`. Do not rely on it alone
  to mean "the level is ready"; `GameState.OnLevelLoaded` is the reliable signal.
- **Closing windows runs scripts.** `GameResources.LoadGame` closes all windows on
  level unload, and closing the intro text window fired a script that failed on the
  guest. Seen only with a save forced mid-intro; watch for it with normal saves.
- **A new game cannot be saved until character creation is done** (the game's
  `InGameHUD.QuicksaveAllowed` is false). The handoff waits for it.
- **Edge scrolling moves the camera in unattended tests.** If the mouse pointer
  rests on a window edge, the game scrolls the camera to the level boundary, and
  screenshots then show black. Test runs pass `--coop-no-edge-scroll`.
- **Handoff verified with a normal save** (2026-11-19): same scene, same party,
  same positions and camera on host and guest, no errors in either log. The
  earlier script error did not recur, so it came from the save forced mid-intro.
- **After the handoff the guest still simulates.** It is not paused when the host
  is, and its NPCs wander. That is milestone 4 (freeze) and 7 (pause).

## Findings from milestone 4

- **Guest freeze in place:** `AIStateManager.Update`, `ScriptEvent.RunScriptHelper`,
  `Trigger.NotifyTriggerEnter`/`Exit`, `Mover.Update` and `Player.IssueCommands` are
  skipped on a guest inside a level (`Plugin.IsMirroring`). Not while loading, so a
  level's own load-time scripts still run. Cutscenes, encounters, combat detection
  and status-effect ticks from section 8 are still to do.
- **Walk animation follows from two private Mover fields**, `m_currentSpeed` and
  `m_desiredSpeed`. Copying them from the host is enough; no animator calls needed.
- **The game pauses itself every frame while its window is unfocused and
  fullscreen** (`GameState.Update`). On one PC this stops whichever copy is in the
  background, so multi-instance mode tells the game it always has focus. In real
  play it means a host who alt-tabs pauses everyone: decide in milestone 7.
- **Two copies on one PC run at a low frame rate**, lower than the 10 per second
  the host sends positions at. Anything on the guest that restarts when a message
  arrives will starve; advance by elapsed time instead.
- **Measured:** guest party about 0.5 units behind the host on average while
  walking at 1.2 units per second. Positions go over TCP for now.
- **Not matched yet:** characters created after the save was taken get a different
  id on each machine (section 4), so the guest leaves them where they are.

## Findings from milestone 5

- **"Harvest the stack" works.** On a guest, order states pushed by the game sit
  on the frozen AI stack. `Sync/Orders.cs` reads them each frame, sends them and
  pops them, then calls `AIStateManager.PostUpdate()` so the pooled states are
  returned (nothing else frees them while `Update` is skipped).
- **Host execution** builds the same state with `StatePool.Allocate<T>()` and
  `PushState(state, true)`, fields set after the push, as the game does.
- **Ownership** is keyed by the character's `InstanceID`; summons and animal
  companions follow their master. Enforced locally by refusing `Selected = true`
  for other players' characters, and on the host for every order.
- **Not covered yet:** abilities and spells (`TargetedAttack`), stealth, weapon-set
  switch, quick items. A usable object without an `InstanceID` cannot be ordered.
- **Initial order coverage used injected states**, not real mouse clicks; the
  later real-input coverage is recorded below.

## Findings from the first hand test (2026-11-19)

- **A hidden game window runs at 1 frame per second** on this desktop when
  vertical sync is on (measured: host 1 fps behind the guest window, guest 75).
  The game clamps its frame step to 0.1 s, so at 1 fps characters also move at
  about a tenth of normal speed. This was the "latency" and most likely the
  "hit or miss" orders. Multi-instance mode now turns sync off and caps at 60 fps
  (`DevTools.KeepRunningWhenHidden`). Two PCs do not have this problem; a host who
  alt-tabs away in real play might, so it is worth rechecking in milestone 7.
- **Positions are sent 20 times a second** now. Measured guest lag about 0.2 units
  (walking speed 1.2 to 3 units per second).
- **Redirecting a walking character** on the host reuses the running `Move` state
  (set the fields, `ClearQueuedStates`, `OnEnter`), as `Player.IssueCommands` does.
- **Opening a container originally showed the loot window on whichever machine
  ran `Container.Open`**, which was the host (`ShowSingleContainerLootUI`).
  Milestone 7 replaced that limitation with shared loot sessions, host-authoritative
  transfers and viewer-aware close behavior; see below.
- **Host selecting a given-away character: not reproduced.** Select-all and
  click-select through the game's own methods are both refused. Every selection
  write goes through the patched setter. The game draws its "selected" circle on
  hover (`SelectionCircle`), which may be what was seen. A refused click now says
  who controls the character.

## Findings from real-input tests (2026-11-19)

- **Real X11 input passed** for selection and ownership enforcement, Move, X Stop,
  Shift A→B queued movement and queue cancellation, force Attack on `NPC_Odema`,
  and NPC Use. These results cover order capture, transport and host execution,
  not combat or damage replication.
- **Focused-chat F10 bug fixed** on IMGUI `KeyUp`, with immediate key unblock and
  draft preservation. Overlay/chat input isolation also passed. The game's single
  global `GameInput.BlockAllKeys` flag is not reference-counted, so overlapping
  key blocks owned by other UI remain an open edge case.
- **Adra's Use order was delivered:** the host approached but stopped about 10
  units short, consistent with the game's partial-path cancellation rather than
  a transport rejection.
- **Reusable test infrastructure:** `scripts/x11_input.py` and the opt-in
  `--coop-probe` mode support repeatable live-input checks. When instances share a
  BepInEx log, concurrent writes can overwrite the other instance's log; account
  for this when collecting test evidence.

## Findings from milestone 6 (2026-11-20)

**Final acceptance scope.** Plugin **0.7.0**, wire **protocol 6**. Turn-based
combat (`RoundBased`/`TacticalMode`) is the primary, most polished mode. RTwP is
retained as the secondary mode and has its own final regression coverage. The
host remains authoritative for gameplay and tactical scheduling; guests
reconstruct native presentation from host state rather than running independent
schedulers or impact rolls.

- **Complete tactical snapshots and native presentation.** Mode, enabled and
  combat state, epoch, complete settings and the ordered initiative/event queue
  converge on both peers, including event identity, order, initiative fields,
  preview flags and the current event/actor. The guest reconstructs native
  tactical presentation events from these ordered host snapshots.
- **Current-turn commands, Finish/Delay and previews.** Commands carry
  current-turn tokens and are checked for ownership and the active actor;
  wrong-actor and stale-token requests do not mutate host state. Real guest
  Finish and Delay input converges, duplicate requests are cached, Delay
  previews match without consuming resources, invalid Delay-after-Move is
  rejected, and Finish works from the native `WaitForTurn` gate over multiple
  rounds.
- **Actions and movement.** Real guest attacks, Knock Down, Fireball, Defender,
  movement and Stop follow host execution. Standard-action and per-encounter/
  spell resources converge, repeat standard orders are rejected, movement
  allowance is consumed on the host and mirrored, and out-of-turn summon
  movement is rejected while own-turn movement succeeds.
- **Long casts, previews and interruption.** Chill Fog hover and committed
  previews preserve attack identity, initiative, position and direction. Its
  long-cast resolution, resources and pulses converge; a native combat interrupt
  cancels the pending cast without impact or a later impact after two rounds.
- **Projectiles and tactical visuals.** Crossbow projectiles and Chill Fog pulse
  visuals reconstruct on the guest without scripts, rigidbodies, colliders,
  independent damage or ammunition use. Tactical projectile turn events match,
  natural host removal is mirrored, and stale/full-refresh and projectile-lock
  cleanup are covered.
- **Summons.** Fixed Essential Phantom and random Conjure Lesser Blight summons
  receive host-assigned ids, appear exactly once on the guest, follow ownership
  and turn rules, converge in vitals/position, and naturally despawn with their
  tactical events removed on both peers.
- **Death and revival.** Natural hostile lethal damage produces matching vitals,
  finalized logs and dead reactions without guest-independent death state,
  party removal or false game-over. Dead actors leave initiative; native
  HealParty revival restores the stand-up visual and exactly one converged
  initiative re-entry.
- **Mode switching and late-join waiting.** Peers converge through tactical and
  RTwP mode changes, with a fresh queue and epoch for the next combat. A second
  guest joining during combat waits without a save or partial mirror, receives
  the normal handoff only after combat ends, and converges without a stale
  queue even if the host re-enters tactical combat while that guest is loading.
- **Key fixes.** Final acceptance includes the desired-speed tactical movement
  fix, pass capture from the native `WaitForTurn` state, projectile stale/full-
  refresh and lock cleanup, and a guard preventing dead actors from entering
  initiative.

**Final verification.** The standard build succeeded with **0 warnings and 0
errors**. Final live-suite evidence:

| Suite | Result |
|---|---|
| `.dev/test-tactical-final/result.json` | `PASS`, **65/65** checks |
| `.dev/test-tactical-schedule/result.json` | `PASS`, **41/41** checks |
| `.dev/test-tactical-abilities/core/result.json` | `PASS_SCOPED` |
| `.dev/test-tactical-abilities/interrupt/result.json` | `PASS` |
| `.dev/test-tactical-visuals/result.json` | `PASS`; also records 0 build warnings/errors |
| `.dev/test-tactical-mode-switch/result.json` | `PASS`, **31/31** checks |
| `.dev/test-rtwp-final/result.json` | `PASS`; core **56/56** and crossbow **26/26** |

The RTwP core run covers real guest Move/Stop and Knock Down, host-authoritative
vitals/effects/logs, attack animation and death/reaction fixtures. The crossbow
run covers real projectile lifecycle, inert guest visuals, ammunition authority
and natural full-refresh stale cleanup. Child results are
`.dev/test-rtwp-final/core/result.json` and
`.dev/test-rtwp-final/crossbow/result.json`.

**Current remaining limits and observations.**

- Acceptance is not exhaustive parity for every exotic effect, projectile or
  class-resource path. Newly granted abilities and untested class resources and
  effect origins remain outside the claim.
- Mid-combat save handoff is not implemented: joining guests wait until combat
  ends, as verified by the mode-switch suite.
- Native exceptions without plugin frames were observed, including
  `AIController.HandleRequestHelpEvent`, `TacticalProjectileTurnEvent.OnUpdate`
  and guest `SmartCamera` paths. They remain native/non-plugin observations,
  separate from the passing plugin/session/Harmony error checks.

## Findings from milestone 7 (2026-11-21)

**Final acceptance scope.** Plugin **0.8.0**, wire **protocol 7**. Milestone 7 is
complete for the documented shared-flow scope. Turn-based combat remains the
primary, most polished mode; RTwP remains supported as the secondary mode. The
host authorizes shared-flow commands and transactions, while guests render the
resulting authoritative state rather than independently mutating the world.

- **Correlated world readiness and save-backed transitions.** Handoff readiness
  is correlated between peers, including a guest that becomes ready only after
  the initial exchange, without a readiness deadlock. Tested area transitions
  use a semantic host request and save-backed same-scene readiness on both peers.
  A-to-B and B-to-A round trips preserve scene, ownership, start positions,
  tactical settings, mutated inventory and mutated world globals/state. The
  guest does not independently run native `ChangeLevel` or autosave, and
  transient fixture/handoff saves are cleaned up.
- **Shared time/pause and private UI policy.** Real guest pause and unpause input
  drives the shared host-authoritative state: both peers reach effective zero
  while paused and positive time scale after unpausing. Private local UI, including
  a guest inventory, does not pause the host. Opening that UI also does not swallow
  or block the next shared pause command, and pause/network input remains
  responsive during dialogue.
- **Dialogue picker, commands and host-only scripts.** Both peers see the same
  conversation file, node, text and qualified responses. The conversation starter
  is the sole picker; real Continue and qualified-response input become
  host-checked node/revision commands. A host who is not the picker is rejected,
  duplicate commands return their cached result without a second advance, and
  stale dialogue revisions fail with a conflict. Conversations end cleanly on
  both peers and restore native pause state. Native conversation flow remains
  enabled, but dialogue and flow-chart scripts execute only on the host.
- **Rest transactions.** Rest is a host-side transaction shared by both combat
  modes. No-supply and in-combat requests are rejected without queueing rest,
  consuming supplies or advancing/mutating time; guest open/cancel alone has no
  rest mutation. A real guest can select a valid Survival bonus for an owned
  character. Accepted rest produces exactly one rest movie, time advance and set
  of party effects, spell restoration and logs in both RoundBased and RTwP.
- **Loot sessions and viewer-aware close.** A container has one authoritative
  shared session: both viewers receive matching item IDs, prefabs and stacks,
  with host loot generation performed once. Concurrent attempts to take one stack
  conserve it without duplication or loss. Partial native splits preserve the
  source ID, assign a distinct destination ID and conserve quantity; duplicate
  partial takes are cached without another mutation. Take All converges inventory,
  logs and cleanup, while combat open/take requests are rejected without inventory
  or turn mutation. Closing one viewer leaves the session open for the other;
  the last viewer close performs cleanup.
- **Item registry, inventory and equipment.** The item registry preserves exact
  runtime identities across host and guest manifests and mutations. Covered real
  commands include move, occupied-slot swap, gift, stash transfer, split and merge,
  with ownership and stale-request validation and cached duplicate results.
  Equipment coverage includes occupied-slot equip swap, unequip, the second weapon
  set, equipment-origin effects and quick consumables. Native turn/resource rules
  still reject invalid weapon switches. Post-equipment attack and projectile
  identity converge, and a fresh handoff preserves IDs, stacks, item mods,
  consumables, equipment and effects.

**Important fixes in the final scope.**

- Inventory update flooding no longer causes frame starvation during large item
  transfers/snapshots.
- Pause input is isolated from private UI: private windows neither pause the host
  nor consume the next shared pause request.
- Dialogue authority rejects a host non-picker instead of allowing the host's
  local role to bypass the designated-picker rule.
- Guest rest uses the host transaction path, including guest Survival selection
  and survival-affliction registration, rather than a local partial rest.
- Loot split and close/reopen handling preserve authoritative stacks, identities
  and viewer/session state.
- Runtime items use native `Persistence.SaveObject` registration so their
  host-assigned runtime IDs and item state persist through saves and fresh
  handoffs, rather than existing only in the in-memory network registry.

**Final verification.** The standard build succeeded with **0 warnings and 0
errors**. Final live-suite evidence:

| Suite | Result |
|---|---|
| `.dev/test-m7-flow-final/result.json` | `PASS`, **133/133** checks; combined readiness, pause/private UI, dialogue, area, rest, loot and inventory/equipment flow |
| `.dev/test-m7-tactical-final/result.json` | `PASS`, **65/65** checks; final integrated tactical regression on protocol 7 |
| `.dev/test-m7-rtwp-final/result.json` | `PASS`; core **56/56** and crossbow **26/26** checks |

The final flow run also records matching built/deployed plugin DLLs, unchanged
production sources, preserved original host/guest save sets and closed test ports.
The RTwP child results are `.dev/test-m7-rtwp-final/core/result.json` and
`.dev/test-m7-rtwp-final/crossbow/result.json`.

**Remaining limits and observations.**

- Optional same-map and world-map travel are not fully accepted. The accepted
  area result is the tested save-backed A-to-B/B-to-A path, not every travel UI
  or transition variant.
- Scripted content, general stronghold parity and exotic item cases are not
  exhaustive. Host-only dialogue scripts and the tested item/registry paths do
  not establish parity for every quest, cutscene, stronghold interaction or item
  type.
- Mid-combat save handoff remains deferred: a joining guest waits until combat
  ends before receiving the normal handoff.
- Native exceptions without plugin frames were observed, including
  `AIController.HandleRequestHelpEvent`, `TacticalProjectileTurnEvent.OnUpdate`
  and guest `SmartCamera` paths. They remain native/non-plugin observations,
  separate from the passing plugin/session/Harmony error checks.
- Milestone **8 (Steam transport)** now has the implementation and limited
  validation documented below; its real two-account acceptance remains open.
  Milestone **9 (hardening)** remains.

## Findings from milestone 8 (2026-11-21)

**Status and scope.** Plugin **0.9.0**, wire **protocol 7**. The Steam transport
implementation is complete, but milestone 8 is **not fully verified**. Direct IP
has a passing full regression, Steamworks has deterministic fake-backend and
real one-account lobby coverage, and Proton Experimental has a passing direct
host/guest smoke test. No real two-account Steam co-op session has been accepted.

### Release, installation and launch modes

- `scripts/build-release.sh` builds the Release configuration and creates
  `dist/PoECoop-0.9.0.zip` plus `dist/PoECoop-0.9.0.zip.sha256`. The zip contains
  the 0.9.0 plugin, official Windows and Linux x64 BepInEx 5.4.23.6 archives,
  installers, launcher and `INSTALL.md`; build deployment is redirected away
  from the real game. The final build reported **0 warnings and 0 errors**.
- `scripts/install-poecoop.sh` and `scripts/install-poecoop.bat` provide the
  player installation path. They detect standard Steam locations, preserve
  saves, BepInEx configuration and unrelated plugins, back up files they replace
  under `.poecoop-backups`, and leave unchanged files alone on rerun. The Proton
  installer does not edit `user.reg` or Steam settings. Instead, the user adds
  `WINEDLLOVERRIDES="winhttp=n,b" %command%` explicitly to the game's Steam
  launch options. Installer and repository-rerun evidence is under
  `.dev/test-proton/logs/`.
- **Direct IP is the default.** For Proton, add the visible `WINEDLLOVERRIDES`
  option before launching. **Steam mode additionally requires `-steam`**, supplied
  through `./launch-poecoop.sh -steam` or appended to that option. Native Linux
  still uses the BepInEx wrapper when launched through Steam; `INSTALL.md` gives
  the exact per-platform forms.

### Proton Experimental and Direct IP validation

The real Windows game was tested with **Proton Experimental**
(`experimental-11.0-20261112-x86_64`). BepInEx 5.4.23.6 loaded through the
configured `winhttp` bootstrap, and its log records chainloader startup followed
by `PoE Co-op 0.9.0`. This validates the installation/loading path; it is not a
claim that every Proton version or prefix has been tested.

`.dev/test-proton/coop/result.json` is `PASS` with **20/20** checks. Using the
exact release DLL and protocol 7, two Windows-game instances completed a Direct
IP host/guest save handoff and ownership assignment. Real input covered F10
hide/show on both peers, guest selection, Move and X Stop, plus shared pause to
effective zero and resume to positive time scale on both peers. The run reported
no plugin, Harmony, assembly or session errors and preserved the save set.

`.dev/test-m8-direct-regression/result.json` is `PASS` with **49/49** phase
checks on plugin 0.9.0/protocol 7: **29** core checks, **12** camp/rest checks
and **8** partial-loot checks. Coverage includes handoff/readiness, real tactical
Move/Finish/Knock Down, shared pause and private UI, rest, and native partial
stack splitting with conserved items and cached duplicates. Production sources,
original save sets and the expected deployed DLL were preserved, and test ports
were closed. This was explicitly a **Direct IP** regression, not a Steam lobby
or P2P session.

### Steam transport evidence

| Suite | Result and scope |
|---|---|
| `.dev/test-proton-steam/result.json` | `success: true`, **19/19** checks with the real Steam client and native Steamworks API, one account, Proton-hosted game |
| `.dev/test-steam-harness/` deterministic fake-backend harness | **14/14** checks, including a lossless and ordered **256 MiB** save-sized burst |

The one-account real Steam smoke test verified the game-bundled `steam_api64`,
Steam manager initialization and valid local user, creation of a private
two-player lobby with the expected metadata and owner, attachment of
`SteamTransport`, rejection of a self-join, native invite-overlay open/cancel,
and clean lobby leave. It sent **zero real friend invites** and had only the
local user in the lobby. It establishes Steam API/lobby integration, not native
Linux Steam validation and not two-player P2P connectivity.

The deterministic fake-backend suite covers startup failure, lobby admission,
nonce/session handshake order, message boundaries, exact/oversized limits, send
retry ordering, per-link and global backpressure, queue limits, stale failure
handling, owner migration and callback disposal. Its 256 MiB case checks ordered,
lossless save-sized transfer without using a real Steam network. These are
transport implementation tests, not evidence of Internet connectivity.

### Remaining milestone-8 acceptance

Real two-account friend invite delivery and acceptance, guest lobby join, Steam
P2P save handoff and live gameplay traffic, NAT traversal, and relay fallback
remain **unverified**. Do not treat the one-account lobby or fake-backend pass as
completion of those scenarios. Milestone 8 therefore remains open only for that
real acceptance scope; milestone **9 (hardening)** also remains.

### Historical milestone-6 real-time scope (plugin 0.6.0, protocol 5)

The findings below record the earlier host-authoritative RTwP acceptance scope.
At that stage turn-based had not been implemented; the former statement that it
was unsupported is superseded by the final tactical acceptance above. These
results remain useful historical RTwP scope, not the current plugin version or
mode priority.

- **Host authority and core combat.** The host supplies combat state, health,
  stamina, dead/unconscious flags and gameplay results; the guest does not roll
  its own impacts or independently change those values. The final real-X11 core
  regression passed exact vitals replication, combat-state convergence, guest
  Move/Stop, real attacks, attack animation state, lethal death and reaction
  fixtures, with no independent guest incoming damage or duplicate logs.
  Evidence: `.dev/test-combat-final-core/result.json` (`PASS`; both peers report
  plugin 0.6.0 and accept protocol 5).
- **Abilities and their covered resources.** Real guest input produced targeted
  Knock Down, non-targeted Second Wind, position-targeted damaging Fireball and
  modal Defender activation/deactivation. The host executed the orders and the
  guest converged on per-encounter uses, readiness/cooldown and modal state;
  Fireball also consumed exactly one real third-level spell cast and replicated
  its AoE vitals, effects and logs. These tests cover those resource paths, not
  all class resources. Evidence: `.dev/test-combat-ability/result.json` and
  `.dev/test-combat-aoe-modal/result.json` (both `PASS`), and the final targeted
  Knock Down rerun in `.dev/test-transport-live/result.json` (`PASS`).
- **Status snapshots.** Host effect IDs key guest application, update and
  removal, including origin, duration, timing and applied/UI state. A real Knock
  Down applied Prone and converged on both peers; expiry/removal was also tested.
  Three same-origin ConstantRecovery effects retained host order and distinct
  IDs through real healing ticks and removal, with no guest-only vitals drift.
  Evidence: `.dev/test-combat-status/result.json`,
  `.dev/test-combat-effects-extended/result.json` and the final core result
  (all `PASS`). The base game left all three ConstantRecovery effects
  unsuppressed, so this is stacking coverage, not suppression coverage.
- **Attack visuals, projectiles and finalized logs.** Guest attack desired and
  current animation state followed the host. Multiple real Crossbow/BowArrow
  launches replicated, including a roughly 36 ms spawn/remove lifecycle; guest
  visuals had no scripts, rigidbodies or colliders, did not consume guest ammo,
  and followed host removal/destruction while damage came only from host
  snapshots. Finalized host/guest console rows matched text, verbose text, mode
  and colour without duplicates for the covered melee, death and Fireball cases.
  Evidence: `.dev/test-combat-final-entities/projectile/result.json`,
  `.dev/test-combat-final-core/result.json` and
  `.dev/test-combat-aoe-modal/result.json` (all `PASS`).
- **Death, unconsciousness and revival.** Covered cases include NPC lethal
  death, unconscious/dead and stand-up reaction fixtures, natural hostile-NPC
  killing of a party member, collision removal/restoration, party retention and
  no false game-over, plus native HealParty revival. On the first
  normal-difficulty party death, the base game has MaimAvailable and emits no
  separate "killed" line; the single lethal combat row is the death-causing log,
  and its text/verbose/mode/colour matched on host and guest. The top-level
  `.dev/test-combat-party-death/result.json` remains `FAIL` because two outdated
  assertions required that separate line; its recorded base-game log behaviour
  and the other death/revival checks passed, so it is not a clean regression
  `PASS` artifact.
- **Dynamic entities.** In the final real-X11 dynamic suite, three copies of the
  same encounter prefab received identical host-assigned GUIDs on both peers,
  converged in position/vitals and
  despawned with the host; the guest made no independent or duplicate spawns.
  Real ability input covered fixed Essential Phantom and random Conjure Lesser
  Blight (which produced a Wind Blight), including identity, position, vitals,
  damage and despawn. Evidence: `.dev/test-combat-final-entities/result.json`
  (`gameplayStatus: PASS`, overall `PASS_WITH_SAVE_CAVEAT`) and its `encounter`,
  `summons` and `projectile` child results. The encounter fixture required a
  same-scene reload to capture PathOfTheDamned in `Encounter.Awake`, which
  created one transient autosave that was removed during cleanup; the original
  save hash stayed unchanged, and a no-transition variant produced no spawns.
- **Transport hardening and final live regression.** String tests passed UTF-8
  round trips and strict rejection of malformed, oversized and truncated
  lengths. Incoming tests passed pre-Hello per-link/global bounds, ordered drain
  and accounting returning to zero; outgoing tests accepted a 256 MiB save-sized
  burst and disconnected sustained overload as intended. The protocol-5 live
  rerun then passed real X11 Move/Stop and one targeted Knock Down with matching
  finalized logs, no backpressure disconnects or malformed-string errors, and
  no save changes. Evidence:
  `.dev/test-transport-hardening/verification.log` and
  `.dev/test-transport-live/result.json` (`PASS`).
- **Disconnect-safe handoff.** Session generations cancel a pending received-save
  load when the connection changes and suppress completion/acknowledgement if the
  game load already became uncancellable. The live abrupt-disconnect scenario
  passed all 17 checks, including delayed-load cancellation, no stale ack, and a
  same-process guest joining a different host with exactly one new ack. Evidence:
  `.dev/test-handoff-cancel/result.json` (`PASS`).

**Historical real-time limits and observations.**

- Focus, wounds, chanter phrases and spiritshift timers were not live-tested.
  Ability-state fixtures used abilities already present on both peers; generic
  replication of newly granted abilities is not claimed.
- Aura-origin, suppression, periodic-damage/DoT and equipment-origin effects
  were not live-tested. ConstantRecovery covers periodic healing only.
- Exotic projectile behaviour such as beams and bounces is not fully covered;
  the inert-visual result is for the tested Crossbow/BowArrow path.
- Base-game `AIController.HandleRequestHelpEvent` null-reference exceptions
  appeared in host Unity logs during combat fixtures without plugin, Harmony,
  BepInEx or session frames. They remain an observed base-game issue, separate
  from the plugin/session error checks, and were not fixed in milestone 6.
