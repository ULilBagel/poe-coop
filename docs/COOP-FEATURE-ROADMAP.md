# Co-op feature catalogue and implementation roadmap

## 1. Purpose

This document compares common features in **Divinity: Original Sin 2 (DOS2)** and **Baldur's Gate 3 (BG3)** with the current PoE Co-op implementation, then proposes a practical way to add them to this mod.

It is a design and scaffolding guide, not a promise that every feature should be implemented immediately. The main constraints remain:

- Host-authoritative simulation; guests present state and send semantic commands.
- **Turn-based combat is the primary, most polished mode.** RTwP remains supported.
- Steam is the default transport; Direct IP is the fallback.
- Proton, Windows, and Linux installation must remain simple and free of hidden registry/Steam-client changes.
- No decompiled game or test artifacts belong in the repository.
- Every feature needs bounded messages, idempotent commands, reset-safe lifecycle, and a real-input acceptance scenario.

For the existing bug-search handoff, see `docs/BUG-SEARCH-CONTEXT.md`.

---

## 2. Reference co-op features in DOS2 and BG3

The important patterns are:

- **Drop-in/drop-out multiplayer** and reassignment of party characters when players join or leave.
- Shared campaign state and the ability for one player to continue while others are away.
- Multiple players controlling different party members in exploration and turn-based combat.
- Dialogue participation, including votes in BG3, with the conversation initiator retaining a special role.
- Shared exploration, quests, combat, inventories, loot, equipment, and difficulty settings.
- Different loot policies, including shared inventories or more individualized approaches.
- Online and local multiplayer, with cross-platform play and mod verification added to BG3 later.
- Photo/social presentation and lobby customization.

Sources:

- [Larian: Can I play the game with friends?](https://larian.com/support/faqs/can-i-play-the-game-with-friends_33) — drop-in/drop-out multiplayer.
- [Divinity: Original Sin 2 on Steam](https://store.steampowered.com/app/435150/Divinity_Original_Sin_2__Definitive_Edition/) — up to four players, online/local co-op, shared adventure, turn-based combat.
- [BG3: The Final Patch](https://baldursgate3.game/news/the-final-patch-new-subclasses-photo-mode-and-cross-play_138) — cross-play, cross-platform progression, mod verification, and Photo Mode.

These games are not architectural templates for PoE 1. They are feature references. We should keep the current host-authoritative model rather than adopting a second simulation or split-screen architecture.

---

## 3. Current implementation baseline

### 3.1 Already present

- Correlated save handoff and world readiness.
- Split-party ownership and selection rules.
- Host-authoritative movement, combat, tactical turns, health, effects, death, revival, and combat logs.
- Complete tactical queue/current-turn snapshots, Finish/Delay/previews, stale/duplicate order protection.
- Shared dialogue with a single authorized picker and host-only scripts.
- Shared pause, speed, world clock, rest, area transitions, loot, and inventory.
- Saved and runtime actor/item identities and dynamic encounter/summon replication.
- Direct TCP and Steam lobbies/P2P.
- Proton/Windows/Linux installers and release packaging.
- Deterministic transport/fault harnesses and extensive real-X11 combat/shared-flow tests.

### 3.2 Reusable extension points

| Extension | Existing component to reuse |
|---|---|
| Session/lobby lifecycle | `Net/Session.cs`, `Net/SteamLobbyService.cs`, `Net/ITransport.cs` |
| Direct and Steam transport | `Net/TcpTransport.cs`, `Net/SteamTransport.cs`, `Net/TransportLimits.cs` |
| World/readiness epochs | `Sync/FlowCoordinator.cs`, `Sync/SaveHandoff.cs` |
| Command deduplication | `Sync/RequestLedger.cs`, `Net/FlowWire.cs` |
| Actor identity | `Sync/EntityRegistry.cs` |
| Item identity | `Sync/ItemRegistry.cs` |
| Combat state | `Sync/VitalsSync.cs`, `CombatStateSync.cs`, `StatusEffectSync.cs`, `AbilityStateSync.cs` |
| Tactical state | `Sync/TacticalStateSync.cs`, `TacticalEventPresentation.cs`, `TacticalOrders.cs` |
| Dialogue | `Sync/DialogueSync.cs`, `DialoguePresentation.cs` |
| Loot/inventory | `Sync/LootSync.cs`, `InventorySync.cs`, `InventoryCommands.cs` |
| Shared flow | `Sync/TimeSync.cs`, `AreaTransitionSync.cs`, `RestSync.cs` |
| UI and social | `UI/Overlay.cs`, `Settings.cs`, `DevTools.cs` |

### 3.3 Current gaps to close before broadening scope

1. Real two-account friend invitation and Steam P2P acceptance.
2. Automatic reconnect and resync after dropped sessions.
3. Mid-combat guest joining without an unsafe forced combat save.
4. Desync detection and repair beyond periodic snapshots.
5. Quest/global/faction/map presentation outside the host's current UI.
6. Richer social and lobby UX.
7. More comprehensive scripted-content and class/effect parity.

Features below are ordered to address these gaps before adding complexity.

---

## 4. Target architecture for new features

### 4.1 Preserve the current authority model

New features should be implemented as one of four categories:

1. **Host snapshot:** host publishes complete state or bounded deltas with periodic full repair.
2. **Guest semantic command:** guest asks the host to perform a native operation.
3. **Presentation event:** host tells guests what to display, with no gameplay callback.
4. **Local-only preference:** keybindings, camera, accessibility, and private UI state that never changes the shared simulation.

Avoid optimistic guest mutation. Guests can predict visual highlighting, but not health, items, quest state, turn order, or resource use.

### 4.2 Suggested protocol-8 evolution

Protocol 7 currently ends at `EquipmentState = 43`. Do not add all proposed messages immediately. Introduce protocol 8 only when a real feature needs a new layout, and group related fields into small streams rather than one large world packet.

Suggested message IDs:

| ID | Proposed message | Direction and purpose |
|---|---|---|
| 44 | `SessionResume` | Guest reconnect request and resume token |
| 45 | `ResumeState` | Host resume acceptance, current world/transfer and checkpoint |
| 46 | `DesyncReport` | Guest state hash mismatch or missing entity/report |
| 47 | `FullStateRequest` | Targeted full-state request by stream/reason |
| 48 | `ModManifest` | Game/mod version and ordered content hash manifest |
| 49 | `PlayerPresence` | Connection, loading, ready, idle, camera and ownership status |
| 50 | `QuestState` | Quest/journal/global/faction state needed by UI |
| 51 | `MapState` | Fog reveal, map visibility, current map, waypoint availability |
| 52 | `TradeState` | Trade session, offer revision, items/currency, accept/cancel |
| 53 | `VoteState` | Dialogue or game-over vote proposal, tally, resolution |
| 54 | `LootPolicy` | Host-selected shared/personal/round-robin policy |
| 55 | `CoopNotification` | Structured notices, pings, quick-chat and rejection reasons |
| 56 | `PlayerProfile` | Player color, icon, display name, controller/accessibility metadata |
| 57 | `SessionCheckpoint` | Safe checkpoint creation, integrity, transfer, invalidation |
| 58 | `PartyProgression` | XP, level-ups, attributes, skills, abilities and spell selections |
| 59 | `InputProfile` | Optional host-suggested default controls, not forced local keybindings |
| 60 | `SpectatorState` | Read-only follow camera and spectator permissions |

Suggested new `FlowDomain` values after `Inventory = 6`:

- `Progression = 7`
- `Trade = 8`
- `Map = 9`
- `Social = 10`
- `Session = 11`

Keep turn-token validation in `TacticalOrders`, item transactions in inventory/loot, and session lifecycle in `Session` rather than mixing them into a generic feature manager.

### 4.3 Suggested new implementation files

These are proposals, not current files:

- `Sync/ReconnectSync.cs` — reconnect tokens, resume, stale-session rejection.
- `Sync/CheckpointSync.cs` — safe checkpoint capture and resync.
- `Sync/QuestStateSync.cs` — quest/global/journal presentation.
- `Sync/MapStateSync.cs` — fog/map/waypoint presentation.
- `Sync/TradeSync.cs` — revisioned trade sessions.
- `Sync/DialogueVoteSync.cs` — vote/tally/resolution around existing dialogue.
- `Sync/LootPolicySync.cs` — configurable loot behavior.
- `Sync/PlayerProfileSync.cs` — colors, icons, names, presence and camera follow.
- `Sync/ModManifestSync.cs` — mod/game compatibility verification.
- `UI/CoopHud.cs` — pings, player markers, notices and lightweight HUD.
- `UI/TradeWindow.cs` — trade UI integrated with existing inventory presentation.
- `UI/LobbyWindow.cs` — reconnect, mod verification, lobby policy and readiness UX.
- `Net/SessionCheckpoint.cs` — checkpoint identity and safe persistence boundary.

New protocols and modules must follow the existing constructor/`Update`/`Dispose` lifecycle and apply on the Unity main thread.

---

## 5. Detailed feature proposals

## A. Lobby, connection, and lifecycle

### A1. True drop-in/drop-out and seamless character reassignment

**Reference pattern:** DOS2/BG3 allow players to join or leave while the campaign continues.

**Current state:** Guests can join out of combat and receive ownership. A departing player's characters revert to the host.

**Proposal:** Make reassignment visible and reversible. When a guest leaves, their characters return to the host without resetting the world. When they rejoin, allow the host to restore the previous assignment or explicitly choose another player. Do not reset the scene merely because a player joined or left.

**Implementation scaffold:**

1. Add a persistent `PlayerAssignmentRecord` in `Ownership`: stable player/session GUID, character ID, assignment revision, online/offline status.
2. Store records in the host's current session checkpoint, not the user's real save by default.
3. Add `Ownership.AssignmentsSnapshot` full refresh on ready and assignment changes.
4. Extend `Session.PlayerLeft` to mark the assignment offline rather than deleting its history.
5. Add overlay actions for `Reclaim`, `Take over`, and `Reassign` with host-only authority and request deduplication.
6. On rejoin, match by `PlayerAssignmentRecord` and offer the old characters without changing world epoch.

**Acceptance:** Player leaves during exploration and tactical combat; characters remain stationary/host-controlled, rejoin restores them once, no duplicate entities or lost turn state.

**Priority:** P0, because it is the basis of drop-in/drop-out play and reconnect.

### A2. Mid-combat joining with a cached safe checkpoint

**Current state:** Joining during combat waits because a live combat save is unsafe and native tactical queues are not saved.

**Proposal:** Cache the last safe out-of-combat save as a baseline, then send live combat snapshots after readiness. Do not force-save combat.

**Implementation scaffold:**

1. Add `SessionCheckpoint` containing baseline bytes, scene, epoch, transfer ID, hash, and timestamp.
2. Refresh after initial join, area load, and safe out-of-combat checkpoints.
3. On combat-time join, send the cached baseline, never a newly forced combat save.
4. After `WorldReady`, host sends current entity, position, faction, vitals, effects, inventory, combat, tactical, and recent log snapshots.
5. Stage guest presentation until all required actors/effects/attacks resolve.
6. Prevent duplicate attacks/damage/effects/resources during catch-up.

**Important limitation:** Baseline plus live state is not enough for arbitrary quest/faction/inventory changes. Those streams must be implemented or the join must wait. Do not silently restore a pre-hostility baseline and call it synchronized.

**Acceptance:** Join during a normal turn and a long cast; no duplicate cast, damage, log, spawn, or resource use. Next transition matches exactly.

**Priority:** P0 after A1 and baseline Steam acceptance.

### A3. Automatic reconnect with session resume tokens

**Current state:** Session generation prevents stale events, but reconnection is manual.

**Proposal:** Detect dropped transport, retain the host session/world, and let the same guest resume without creating a second player or losing ownership.

**Implementation scaffold:**

1. Host issues a cryptographically random `resumeToken` bound to Steam ID and session nonce.
2. Store token, player ID, current epoch, assignment revision, and a short expiry.
3. On disconnect, host keeps player record `Disconnected` for a grace period and continues simulating.
4. Guest reconnects to the lobby and presents `SessionResume` with token and last accepted stream revisions.
5. Host validates token, rejects reuse after expiry/replacement, and responds with `ResumeState` plus targeted full state.
6. Reset only guest-local presentation, not the host world or other guests.
7. Ensure `Session.Generation` still increments and stale old callbacks cannot complete resume.

**Acceptance:** Disconnect during movement, combat, handoff, and area load. Rejoin produces exactly one player, correct ownership, current scene, and no duplicate orders or entities.

**Priority:** P0.

### A4. Desync detection with hashes and targeted resync

**Current state:** Periodic full refreshes repair many streams, but there is no explicit divergence report.

**Proposal:** Add canonical state hashes and targeted resync instead of assuming periodic convergence always works.

**Implementation scaffold:**

1. Define canonical serialization for each stream: entities, positions, vitals, effects, tactical queue, inventory, quest/map, and global combat state.
2. Sort records by stable ID and include revision, count, and relevant state only—not transient receive timers.
3. Host sends `(stream, epoch, revision, hash)` periodically.
4. Guest computes the same hash and sends `DesyncReport` with stream/revision/hash and optional missing entity ID.
5. Host replies with `FullStateRequest` response or current correct snapshot.
6. Apply full snapshots atomically and recompute hash. Keep report rate-limited and avoid false positives during loading/allowed interpolation.
7. Keep state hashes as diagnostic metadata, not authority; host state remains authoritative.

**Acceptance:** Inject one altered effect, item, actor position, and tactical event in a test guest. Report identifies the correct stream, full resync repairs it, and no manual reload is needed.

**Priority:** P0 for hardening.

### A5. Safe host migration

**Reference pattern:** Drop-in/drop-out ideally allows a guest to continue after the host leaves.

**Current state:** Host departure ends authority; Steam lobby-owner migration is explicitly rejected.

**Proposal:** Add host migration only after checkpoints, reconnect, and full live-state replication are reliable. Do not treat Steam lobby-owner migration as gameplay host migration by itself.

**Implementation scaffold:**

1. Choose a migration candidate by lobby membership and stable player order.
2. Require a recent checkpoint and a complete current-state hash manifest.
3. Freeze new commands during migration and display a migration barrier.
4. Transfer authoritative ownership records, tactical queue, current entities/effects, inventory, quests, rest/dialogue/loot sessions, and resume tokens.
5. Candidate becomes `Session.HostId`; former host becomes guest or disconnects.
6. New host starts a new world/session generation, not a fake continuation of the old epoch.
7. Validate all streams, then release the barrier.

**Acceptance:** Host leaves during exploration and tactical combat. Candidate continues with the same scene, items, quests, effects, turn, and no duplicate combat results.

**Priority:** P3/high risk. Do not implement before A1–A4 and quest/map support are proven.

### A6. Lobby privacy, capacity, and mod verification

**Reference pattern:** BG3 cross-play includes mod verification and prompts to install/update matching mods.

**Current state:** Private/friends-only lobbies, member limit, exact mod/game/protocol `Hello` check.

**Proposal:** Add clearer lobby policy and a manifest verification step before P2P gameplay.

**Implementation scaffold:**

1. Keep Private and FriendsOnly; do not add public matchmaking by default.
2. Add `ModManifest` with mod version, game build, protocol, ordered mod GUID list, and a canonical content hash.
3. Host publishes it in lobby metadata for early UX; the existing `Hello` remains authoritative.
4. Guest shows exact mismatch and offers install/update instructions rather than a generic refusal.
5. Validate lobby capacity and display full/locked/rate-limited states.
6. If other mods are present later, define deterministic ordering and compatibility rules; do not pretend only PoECoop is modded.

**Acceptance:** Mismatched mod/game/protocol receives actionable reason and no session/handoff. Matching manifests connect normally. No auto-download or arbitrary remote code execution.

**Priority:** P1.

### A7. Cross-platform session identity and resume

**Reference pattern:** BG3 cross-play supports different platforms and cross-platform progression.

**Current state:** Proton, Windows, and Linux share protocol 7, but no cross-platform acceptance matrix exists.

**Proposal:** Treat platform as a compatibility dimension, not a network identity. Use `platformId`, game build, mod version, and protocol in diagnostics while keeping identical layouts and behavior.

**Implementation scaffold:**

1. Add bounded platform/runtime fields to `ModManifest` (Proton, Windows, Linux, architecture).
2. Never use OS-specific paths or native library names in the protocol.
3. Keep all state serialization endian-stable and UTF-8.
4. Run a Proton↔Windows and Proton↔Linux matrix for handoff, tactical combat, inventory, and reconnect.

**Acceptance:** Cross-platform peers connect, complete handoff, play combat, trade items, and resume without ID or serialization divergence.

**Priority:** P1 after two-account baseline.

## B. Party, exploration, and combat

### B1. Multiple characters per player, companions, and summons

**Current state:** Ownership is per party member; summons/companions follow their master.

**Proposal:** Let each player control multiple assigned characters with clear active-character behavior, including animal companions and summons, without breaking native single-active-turn rules.

**Implementation scaffold:**

1. Keep `Ownership.OwnerOf` as the authority for every order.
2. Extend overlay with per-player groups and current-active indicator instead of a single implied avatar.
3. Preserve `Selected` restrictions and one-active-tactical-actor rule.
4. On summon creation, bind `Summoner` ownership and network ID before first snapshot.
5. On summoner death/despawn/reassignment, update dependent ownership exactly once.

**Acceptance:** Each player controls two characters plus a summon. Only the current tactical actor can act, and reassignment never creates duplicate pets or orders.

**Priority:** P1.

### B2. Independent exploration and simultaneous encounters

**Reference pattern:** Players can split up in co-op RPGs.

**Current state:** Shared scene, party, combat state, and one tactical scheduler.

**Proposal:** Allow separate formations and exploration, then support independent encounters only if the game can represent them safely. Do not fake this with two global combat toggles.

**Implementation scaffold:**

1. Support independent party movement and camera first; do not force a single formation.
2. Add encounter-scoped combat IDs in `CombatStateSync`, `TacticalStateSync`, `CombatLogSync`, and orders.
3. Partition tactical queues by encounter, with independent current actor/turn tokens.
4. Keep host AI authoritative and tag every order with actor/encounter ID.
5. Separate combat log filtering and current-encounter HUD per player.
6. If native global `InCombat` prevents independent encounters, explicitly block them with a clear message rather than merging turns accidentally.

**Acceptance:** Two players explore separately and fight two encounters with correct independent turns, vitals, logs, and no cross-encounter orders. If native restrictions prevent this, the limitation is documented and tested.

**Priority:** P3/high complexity.

### B3. Dialogue voting and shared dialogue presence

**Reference pattern:** BG3 allows other players to vote while the initiator has a special role.

**Current state:** One picker; everyone sees the same conversation; non-picker input is rejected.

**Proposal:** Add an optional vote mode where non-picker players recommend a response without advancing until the picker confirms or a host-defined rule resolves.

**Implementation scaffold:**

1. Add `DialogueVoteSync` keyed by world epoch, conversation ID, revision, and vote ID.
2. Extend `FlowCommand Dialogue` with `ProposeResponse`, `WithdrawVote`, `ConfirmResponse`.
3. Non-picker `ProposeResponse` records only a recommendation; it never calls native `PlayerInput`.
4. Picker retains final authority by default. Optional host policies: initiator-decides, majority, or unanimous.
5. Display votes beside response node IDs in a small `DialogueVoteWindow`.
6. Resolve once, validate current node and qualification, then execute through existing `DialogueSync`.
7. Handle picker disconnect by falling back to host.

**Acceptance:** Two guests recommend different responses; the picker sees them and confirms once. Duplicate votes, stale nodes, and non-qualified responses have no effect.

**Priority:** P2.

### B4. Shared quest, journal, globals, and faction state

**Current state:** Dialogue is host-rendered, so conversation works without general quest replication. Journal/map UI and later responses can diverge.

**Proposal:** Replicate the state needed for journal, conditionals, faction presentation, and future scripted interactions without replaying scripts.

**Implementation scaffold:**

1. Inventory native quest/global/faction APIs and choose read-only canonical records.
2. Add `QuestStateSync` for quest IDs/stages/objectives, globals, relationship/team, and quest-global event counters.
3. Host publishes settled changes after script execution and full snapshots on readiness/transition.
4. Guest applies backing fields directly, without `ScriptEvent` or quest callbacks.
5. Use this state for response qualification, journal UI, cursor hostility, and transition validation.
6. Include a quest-state hash in `DesyncReport`.

**Acceptance:** A dialogue choice updates a quest/global/faction once on the host. Journal and subsequent response availability match on both peers, with no duplicate XP, reputation, or scripts.

**Priority:** P1, required for broad scripted-content support and host migration.

### B5. Shared fog of war, map visibility, and waypoints

**Current state:** Save handoff carries map state, but live map/fog updates are not a dedicated stream.

**Proposal:** Replicate fog reveal and map unlock/travel state while keeping local camera preferences independent.

**Implementation scaffold:**

1. Add `MapStateSync` for fog tiles/chunks, map visited/visible/unlocked waypoints, current map tag, and travel availability.
2. Host captures reveal and unlock changes and sends changed chunks plus full refresh.
3. Guest applies presentation data without local reveal scripts.
4. Add per-player follow-camera options: own character, current tactical actor, host/peer camera, or free camera.
5. Keep map clicks semantic for ping/travel commands; no arbitrary teleport.

**Acceptance:** One player reveals a new area or unlocks a waypoint; all peers see it. Camera preferences do not change world fog or trigger travel.

**Priority:** P1.

### B6. Co-op difficulty, auto-pause, and combat-speed policy

**Current state:** Host pause/speed/world clock are shared; tactical settings replicate.

**Proposal:** Expose a deliberate host policy for difficulty, auto-pause, auto-pass, speed, and focus-loss behavior rather than letting local options silently differ.

**Implementation scaffold:**

1. Extend `TimeSync` with difficulty and auto-pause flags, auto-pass, speed, and focus policy.
2. Only the host changes shared gameplay settings; guest gets a read-only display or request.
3. Keep accessibility preferences local where they do not affect simulation.
4. Validate changes out of combat unless native rules allow them.

**Acceptance:** Host changes difficulty/speed/auto-pause once and all peers agree. Guest focus or private UI cannot change the shared setting.

**Priority:** P1.

### B7. Shared game-over and reload voting

**Reference pattern:** Co-op games often let the group decide whether to reload after a party wipe.

**Current state:** Combat/game-over state is mirrored, but there is no group decision flow.

**Proposal:** Add a host-controlled game-over/reload vote that does not automatically load or delete a save, especially for Trial of Iron.

**Implementation scaffold:**

1. Add `VoteState` kind `ReloadCheckpoint` with vote ID, eligible players, tally, timeout, and resolution.
2. Host chooses the checkpoint; guests receive read-only details.
3. Validate votes, deduplicate, and resolve only on host policy.
4. Use existing save-handoff path for the selected checkpoint and block input until ready.
5. Honour Trial of Iron rules; never bypass native save deletion or create an alternate hidden save.

**Acceptance:** Party wipe produces one vote, one reload, and one new readiness cycle. Duplicate/abstention/timeout behavior is deterministic.

**Priority:** P2.

## C. Inventory, loot, and social play

### C1. Revisioned player-to-player trading

**Reference pattern:** Co-op RPGs let players exchange equipment and currency.

**Current state:** Guests can move/gift items through shared inventories but there is no offer/accept trade session.

**Proposal:** Add an atomic trade UI with per-player offers, revisions, and acceptance, using existing item transactions rather than a new item path.

**Implementation scaffold:**

1. Add `TradeSync` with trade ID, participants, revision, offered item IDs/quantities/currency, and state.
2. Host validates ownership, item identity, revision, native access, and tactical restrictions.
3. Both players can add/remove offers before acceptance; every change increments revision.
4. On both accepts, commit one host transaction using `InventoryCommands` with rollback on any failure.
5. Apply affected inventories/equipment atomically and publish the resulting manifest.
6. Cancel/stale revision/disconnect restores no state because no item moved until commit.

**Acceptance:** Item+currency trade executes exactly once. Stale accept, double accept, wrong owner, full destination, and disconnect cannot duplicate or lose items.

**Priority:** P1.

### C2. Configurable loot policies

**Reference pattern:** Co-op games vary between shared inventories and personal/distributed loot.

**Current state:** Shared containers and host-generated contents are synchronized.

**Proposal:** Support host-selected policies without changing item identity:

- Shared world loot (current behavior).
- Personal view/take reservation.
- Round-robin reservation.
- Host-assigned recipient.

**Implementation scaffold:**

1. Add `LootPolicy` in lobby settings and `LootPolicySync`.
2. For personal/round-robin, generate loot once on host, then reserve each item/stack for an eligible player.
3. Only the assigned player can take reserved items; shared items follow current rules.
4. Publish reservation and revision in `LootState`.
5. Keep native special routing for currency, quest items, and ingredients.
6. Do not reroll per player; that would diverge persistence and exploit RNG.

**Acceptance:** Each policy produces the same host-generated manifest. Unauthorized takes are rejected, quantities conserve, and save handoff preserves remaining loot.

**Priority:** P2.

### C3. Shared stash, crafting, and quest-item handling

**Current state:** Inventory snapshots include stash/quest/crafting and special routing.

**Proposal:** Make shared stash/crafting behavior explicit and robust for quest items, ingredients, currency, and supplies.

**Implementation scaffold:**

1. Expose stash/crafting inventory access rules in overlay and command validation.
2. Replicate `HasNew`, currency, supplies, and quest/crafting flags with inventory revisions.
3. Verify quest items cannot be duplicated or moved to invalid stores.
4. Test currency/supply caps and quest/ingredient redirects.
5. Add crafting transaction result only if native crafting can be called safely on host; do not simulate recipes on guests.

**Acceptance:** Currency, supplies, quest items, ingredients, and crafted results land in the same authoritative inventories on both peers with one log and no duplication.

**Priority:** P1.

## D. Presentation, accessibility, and social UX

### D1. Player colors, names, portraits, and camera-follow modes

**Reference pattern:** Co-op UI helps players identify who controls whom and where they are.

**Current state:** Overlay lists owners and current tactical actor; world markers are limited.

**Proposal:** Add local display profiles and clear world/HUD indicators.

**Implementation scaffold:**

1. Add `PlayerProfileSync` for player ID, color, icon, display name, and online/ready state.
2. Show a colored ring/marker above owned characters and current actor.
3. Add camera-follow options: owned actor, current tactical actor, specific peer, or free camera.
4. Keep profiles cosmetic and editable locally where possible; do not modify native character appearance without need.
5. Ensure colorblind-safe icon/shape alternatives and not color alone.

**Acceptance:** Each player can identify owned characters, partner turn, and peer camera. Profiles do not affect selection, ownership, fog, or orders.

**Priority:** P2.

### D2. Map pings and quick-chat commands

**Reference pattern:** Co-op teams need lightweight coordination without voice.

**Proposal:** Add world pings and a small quick-chat set: move here, attack, defend, wait, need help, loot here, and ready.

**Implementation scaffold:**

1. Add `CoopNotification` kinds with ping ID, sender, world position/entity, text key, expiry.
2. Host validates bounded rate/position and broadcasts.
3. Draw ping in world and on the minimap/HUD; no gameplay effect.
4. Keep pings distinct from `Session.Chat` and order messages so they cannot accidentally issue Move/Attack.

**Acceptance:** Pings reach all peers once, expire correctly, do not pause, order, or reveal hidden map state.

**Priority:** P2.

### D3. Controller support and remappable co-op controls

**Current state:** Keyboard/mouse input is primary; native gamepad paths exist but are not a co-op acceptance target.

**Proposal:** Make co-op actions accessible through remappable controls and gamepad navigation while keeping input authority unchanged.

**Implementation scaffold:**

1. Add co-op bindings in `Settings` for overlay, accept, cancel, ping, next character, quick chat.
2. Route through the existing `GameInput` mapping and overlay focus model.
3. Prevent gamepad `Confirm` from also advancing dialogue/issuing world orders.
4. Test tactical Finish/Delay and private UI navigation with controller only.

**Acceptance:** Every shared-flow action is possible with keyboard/mouse and controller, with no double activation or hotkey leakage.

**Priority:** P2.

### D4. Accessibility and readability options

**Proposal:** Add UI scale, high-contrast mode, colorblind-safe markers, text-size options, subtitle/log preferences, and reduced camera motion.

**Implementation scaffold:**

1. Extend `Settings` with local-only accessibility preferences.
2. Apply them in `Overlay`, `CoopHud`, `TradeWindow`, and `DialogueVoteWindow`.
3. Keep game difficulty, turn speed, and combat rules shared and separate.
4. Test common resolutions and Proton DPI behavior.

**Acceptance:** UI remains readable and usable at 1280×800 and larger resolutions, without changing simulation or network messages.

**Priority:** P2.

### D5. Photo/spectator mode

**Reference pattern:** BG3 includes Photo Mode and spectators can follow the party.

**Proposal:** Add a read-only spectator/follow mode and optional screenshot helper, not a second gameplay mode.

**Implementation scaffold:**

1. Add `SpectatorState` with followed player/actor and read-only flag.
2. Spectator cannot send orders, vote, take items, or mutate anything.
3. Camera can follow current actor, peer, or free camera without altering shared selection.
4. Optionally provide a `PhotoMode` UI for camera angle/position, player markers, and screenshot capture.
5. Keep screenshots local; do not send images over the network.

**Acceptance:** Spectator sees live combat and exploration but cannot mutate state. Screenshots do not alter saves or orders.

**Priority:** P3.

### D6. In-game co-op guide and diagnostics

**Proposal:** Add a small help panel explaining joining, ownership, turn control, shared rules, and common errors, plus an opt-in diagnostics view.

**Implementation scaffold:**

1. Add `UI/HelpWindow` with install, invite, ownership, turn, dialogue, loot, and reconnect guidance.
2. Show transport, readiness, epoch, and pending handoff reasons in a user-safe diagnostics panel.
3. Keep account IDs and native addresses out of the display.
4. Include links to `INSTALL.md` and the bug-report format rather than flooding chat.

**Acceptance:** New users can host/join and understand why a command is rejected or a guest is waiting without reading source code.

**Priority:** P1 for usability.

## E. Progression, mods, and advanced features

### E1. Shared XP, level-up, attributes, skills, and spell selection

**Current state:** Ability/resource snapshots exist, but general level-up UI and progression choices are not synchronized.

**Proposal:** Host-authoritative progression with guests reviewing options and submitting choices for owned characters.

**Implementation scaffold:**

1. Add `PartyProgression` snapshots for XP, level, attributes, skills, available talents/spells, and selection revision.
2. Host validates character ownership and available point pools.
3. Guest level-up UI is a presentation of host data; selection commands reference stable option IDs, not screen indexes.
4. Host applies the choice once and publishes resulting stats/abilities/effects.
5. Preserve per-rest and per-encounter resource behavior and newly granted ability resolution.

**Acceptance:** Guest levels an owned character and chooses attributes/skills/spells once. Host and guest stats, ability lists, and effect origins match with no double-spend.

**Priority:** P1 for a complete campaign.

### E2. Guest-created character during a campaign

**Reference pattern:** Some co-op games let a new player create or import a character.

**Current state:** Guests take over existing party members; the architecture assumes a shared save.

**Proposal:** Support guest character creation only after shared party/progression is stable. Do not make it a prerequisite for co-op.

**Implementation scaffold:**

1. Host chooses an allowed creation point, not arbitrary combat/dialogue.
2. Guest submits appearance/class/race/stats through host-validated creation steps.
3. Host creates the character and assigns a persistent actor ID and owner.
4. Replicate party roster, inventory, equipment, effects, and save checkpoint.
5. Preserve existing companion stories and do not duplicate the main character unless that mode is explicitly chosen.

**Acceptance:** New guest creates one valid character, joins the party once, survives handoff, and can level/equip/fight normally.

**Priority:** P3/high complexity.

### E3. Mod compatibility and update assistance

**Reference pattern:** BG3 verifies mods and prompts for matching versions.

**Current state:** `ModManifest` is only proposed; exact mod/game/protocol checks exist.

**Proposal:** Provide a signed/checksummed release manifest and clear update flow, without remote code execution.

**Implementation scaffold:**

1. Publish a small release manifest with version, protocol, game build, DLL hash, and minimum compatible version.
2. Compare manifest during lobby join and show exact upgrade/downgrade action.
3. Direct users to the GitHub release asset; do not auto-download or execute code.
4. Optionally detect other BepInEx plugins and warn that their behavior is untested rather than claiming compatibility.

**Acceptance:** Outdated guest sees a useful update message and cannot enter a broken session. Updated guest joins normally.

**Priority:** P1.

### E4. Game Master/custom campaign tools

**Reference pattern:** DOS2 includes a Game Master mode and custom campaigns.

**Proposal:** Treat this as a separate advanced feature, not part of the core co-op milestone. If added, provide host-only debug permissions for spawn, teleport, god mode, and state inspection, with guest-visible audit logs.

**Implementation scaffold:**

1. Add a host-only `DebugPermission` in session settings, disabled by default.
2. Route every debug action through host native APIs and structured audit messages.
3. Never expose debug commands to unverified guests or lobby metadata alone.
4. Test save integrity and clear labeling of modified campaigns.

**Acceptance:** Host can perform supported debug actions without desync or hidden guest privileges. Guests see the action and cannot invoke it.

**Priority:** P4/optional and not recommended before core hardening.

---

## 6. Recommended delivery sequence

### Phase 9A — Steam acceptance and drop-in/drop-out foundation

1. Two distinct Steam accounts: invite/accept, lobby join, handshake, save handoff, chat, movement, and clean leave.
2. A1 seamless assignment and reclaim.
3. A3 reconnect with resume tokens.
4. A6 lobby privacy/capacity/mod manifest baseline.
5. A7 cross-platform identity checks.

**Gate:** Real Steam gameplay works without IP entry and no duplicate player/session.

### Phase 9B — Hardening and current-world resync

1. A4 state hashes and targeted resync.
2. A2 cached safe checkpoint and mid-combat join.
3. Reconnect during handoff and area transition.
4. Multi-guest join/leave and ownership stability.

**Gate:** No manual reload is required after a dropped session or a small injected divergence.

### Phase 10 — Campaign coherence

1. B4 quest/global/faction state.
2. B5 fog/map/waypoints.
3. E1 shared progression/level-up.
4. B6 difficulty and pause policy.
5. C3 shared stash/crafting/quest-item behavior.

**Gate:** Journal, conditionals, progression, map, and inventory remain converged after A→B→A and fresh handoff.

### Phase 11 — Social and inventory UX

1. C1 revisioned trading.
2. C2 loot policies.
3. B3 dialogue voting.
4. D2 pings/quick chat.
5. D1 profiles and camera-follow modes.
6. D6 help/diagnostics.

**Gate:** Trades/votes/pings cannot mutate state incorrectly or duplicate items/actions.

### Phase 12 — Polish and advanced modes

1. D3 controller and D4 accessibility.
2. D5 spectator/photo mode.
3. B7 game-over/reload voting.
4. A5 host migration.
5. B2 independent exploration/simultaneous encounters.
6. E2 guest-created characters.
7. E4 optional Game Master tools.

**Gate:** Each advanced feature gets an independent acceptance pass; no blanket “co-op complete” claim without campaign-length testing.

---

## 7. Implementation rules for all proposed features

1. **Host remains authoritative.** Never solve a feature by letting both peers mutate the same object.
2. **Use stable IDs.** Actor/item/conversation/quest/trade/vote/session identifiers must not be screen indexes or per-process object IDs.
3. **Use revisioned commands and full repair.** Every mutation has a revision and a way to recover after loss or reconnect.
4. **Bound everything.** Strings, counts, queue bytes, pages, pings, offers, and per-frame work need limits.
5. **Deduplicate by payload.** Request IDs need fingerprinting and cached receipts, not just a Boolean “handled.”
6. **Keep local preferences local.** Camera, accessibility, keybindings, and private UI do not need host authority unless they affect gameplay.
7. **Do not rerun native gameplay on guests.** Apply backing fields or presentation-only adapters instead.
8. **Preserve world/session/turn correlation.** New commands need epoch/revision and stale rejection.
9. **No hidden installation changes.** No `user.reg`, Steam launch-option, or rich-presence writes.
10. **Verify real input.** A compile or protocol test alone is not enough for a UI feature.
11. **Preserve direct IP.** Steam is default but must not make development/testing or fallback impossible.
12. **Document scope honestly.** Do not call a feature complete with only one-account or injected-state evidence.

---

## 8. Acceptance-test matrix for new features

| Feature area | Required live evidence |
|---|---|
| Drop-in/out | Leave/rejoin during exploration and combat; ownership restored once |
| Mid-combat join | Join during turn and long cast; no duplicate attack/damage/effect/log |
| Reconnect | Disconnect during movement, combat, handoff, and scene load; resume once |
| Desync repair | Inject altered entity/effect/item/tactical event; targeted resync repairs hash |
| Host migration | Continue with same world, quests, items, effects, and turn without duplicate results |
| Mod verification | Exact mismatch reason; no join until corrected; no remote code execution |
| Dialogue vote | Multiple recommendations, one final transition, host scripts once |
| Quest/map | Quest/global/faction and fog converge after transitions and reconnect |
| Trade | Offer revision and double-accept/stale/disconnect cannot duplicate items |
| Loot policies | One generated manifest; policy controls access without rerolling or loss |
| Progression | Level-up choices spend once and replicate stats/abilities/effects |
| Controller/accessibility | Complete flow without mouse and at target resolutions |
| Spectator/photo | Read-only follow and screenshots cannot mutate game state |
| Cross-platform | Proton↔Windows/Linux handoff, combat, inventory, reconnect |

Every new feature also needs:

- Original-save hash checks and transient-save disclosure.
- Host/guest probe and Unity logs.
- No plugin/Harmony/session errors.
- No new unbounded allocations or per-frame floods.
- Existing tactical and RTwP regression suites on the same DLL.

---

## 9. Features deliberately not recommended now

- **Deterministic lockstep:** would be fragile across Unity pathfinding, floating point, and RNG.
- **Split-screen support:** unrelated to the networked host-authoritative architecture and a large separate input/UI project.
- **Public matchmaking:** raises privacy, moderation, and security scope; private/friends-only lobbies are sufficient.
- **Automatic remote mod download/execution:** unnecessary security risk; show manifest and link to the release instead.
- **Host migration before full state parity:** would lose or duplicate quests, effects, items, or combat results.
- **Independent simultaneous combat before encounter-scoped state exists:** a single global `InCombat`/turn scheduler cannot safely represent it.
- **Guest-created main characters before progression/party roster replication:** would produce unstable identity and campaign behavior.
- **Game Master/debug powers for guests by default:** would bypass host authority and create unsafe state changes.
- **Voice chat as a core dependency:** useful socially but orthogonal; quick chat and pings provide a smaller, safer first step.
- **Mandatory shared camera or shared pause on every UI window:** harms independent inventory/loot/accessibility workflows.

---

## 10. Summary recommendation

The most valuable next work is **not** a large pile of social features. It is the foundation that makes them reliable:

1. Real two-account Steam acceptance.
2. Drop-in/drop-out ownership and resume tokens.
3. Checkpoint-based mid-combat join and targeted resync.
4. Quest/map/progression coherence.
5. Trading, loot policies, dialogue voting, and player profiles.

Only after those are proven should we attempt host migration, independent simultaneous combat, guest-created characters, or Game Master tools. This preserves the stable host-authoritative architecture while approaching the drop-in/drop-out experience offered by DOS2 and BG3.
