# PoE Co-op: in-depth bug-search handoff

## 1. Purpose and reading guide

This document is a complete handoff for a bug-search pass over the PoE Co-op mod for **Pillars of Eternity 1**. It is intended for **Fable 5.2** or another engineer who needs to understand the system before changing it.

Read these sections first:

1. **Current facts and claim boundaries** — what is implemented and what is not verified.
2. **Install and build** — how to run the mod and produce a release.
3. **Architecture and invariants** — rules that must remain true.
4. **Source map** — where each subsystem lives.
5. **Prioritized bug-search plan** — concrete leads and how to validate them.
6. **Test harness and evidence** — how to prove or reject a finding.

This is a living engineering document. Update it when behavior, protocol, installation, or acceptance status changes.

---

## 2. Current facts and claim boundaries

### 2.1 Product and release

- Mod: **PoE Co-op**
- Plugin version: **0.9.0** (`src/PoECoop/Plugin.cs:17`)
- Wire protocol: **7** (`src/PoECoop/Net/Messages.cs:87`)
- GitHub repository: `https://github.com/ULilBagel/poe-coop`
- Current release: `v0.9.0`
- Release page: `https://github.com/ULilBagel/poe-coop/releases/tag/v0.9.0`
- Release archive: `PoECoop-0.9.0.zip`
- Release archive SHA-256: `d3f7e727bf82c35bfa9202ea4b8a0c6c9a31685cf3c0b74d7d9a007fe89356f0`
- Source commit at release: `cb663c7` (`Default to Steam without client-side modifications`)
- Primary mode: **turn-based / `RoundBased` / `TacticalMode`**
- Secondary supported mode: **real-time with pause (RTwP)**
- Default network mode: **Steam lobby/P2P**
- Fallback network mode: **Direct IP over TCP**

### 2.2 Supported installation targets

- Windows game through **Proton Experimental**.
- Native Windows.
- Native Linux.

The main acceptance path is the **Windows version running through Proton**. Proton support was validated on `experimental-11.0-20261112-x86_64`, game `v3.10.0.90434`, Unity `2019.4.41f1`, using Mono and BepInEx 5.4.23.6.

### 2.3 Verified implementation scope

The following are implemented and covered by live tests:

- Save handoff and correlated world readiness.
- Split-party ownership and selection restrictions.
- Host-authoritative movement, attacks, abilities, health, stamina, effects, death, revival, and combat logs.
- Turn-based initiative/event snapshots, current actor, action and movement allowances, Finish, Delay, previews, stale/duplicate order protection, long casts, and interruptions.
- Visual-only projectiles, dynamic encounters, fixed and random summons.
- Shared pause, speed, and world clock; private UI remains local.
- Shared dialogue with one picker and host-only conversation scripts.
- Rest transactions with Survival bonuses and movie presentation.
- Shared loot with multiple viewers, partial stacks, Take All, and viewer-aware close.
- Inventory, equipment, weapon sets, consumables, and persistent runtime item identities.
- Direct TCP and Steam lobby/P2P transport.
- Idempotent installation for Proton, Windows, and Linux.

### 2.4 Explicitly not fully verified

Do not report these as completed capabilities without new evidence:

- Real two-account friend invitations, P2P gameplay, NAT traversal, relay fallback, and disconnect behavior.
- Exhaustive parity for every class resource, ability, status effect, projectile, quest, cutscene, stronghold interaction, or item type.
- Every same-map and world-map travel variant.
- Mid-combat save handoff. Joining guests currently wait for combat to end.
- Windows interoperability with the native Linux build beyond the tested Proton path.
- Every Proton version and every prefix configuration.

### 2.5 Evidence discipline

Use these categories in bug reports:

- **Confirmed bug:** reproduced with logs/probes/screenshots and a failing expected invariant.
- **Suspected bug:** code-derived hypothesis not yet reproduced.
- **Fixture limitation:** the scenario could not be constructed reliably.
- **Base-game observation:** exception or oddity without mod/Harmony frames.
- **Out of scope:** behavior outside the documented acceptance scope.

Do not treat a passing unit-style harness as proof of live multiplayer behavior. Do not treat one-account Steam tests as proof of two-account behavior.

---

## 3. Repository contents and excluded material

### 3.1 Tracked content

- `src/PoECoop/` — plugin and all networking, gameplay, patch, and UI code.
- `scripts/` — installers, launchers, release builder, dependency fetcher, and X11 input harness.
- `PLAN.md` — milestones and current acceptance status.
- `notes/code-survey.md` — native-game API findings and verification results.
- `README.md` — project overview and build/install quick start.
- `INSTALL.md` — user installation and launch instructions.
- `tools/README.md` — explanation of downloaded BepInEx dependencies.
- `docs/BUG-SEARCH-CONTEXT.md` — this handoff.

### 3.2 Intentionally excluded content

- `decompiled/` — Obsidian's decompiled game code. Do not commit it.
- `.dev/` — local saves, Proton prefixes, logs, screenshots, probes, and test harnesses.
- `dist/` — generated release archives.
- `tools/*` except `tools/README.md` — downloaded BepInEx archives and local tooling.
- `bin/` and `obj/` — build output.

A fresh clone does not need the decompiled game to build. It needs the actual game installation for assembly references.

---

## 4. Installation and launch behavior

### 4.1 What the installer does

`./install-poecoop.sh`:

1. Finds the game in standard Steam/Snap/Flatpak locations or accepts a path:
   ```sh
   ./install-poecoop.sh "/path/to/steamapps/common/Pillars of Eternity"
   ```
   Equivalent named option:
   ```sh
   ./install-poecoop.sh --game-dir "/path/to/steamapps/common/Pillars of Eternity"
   ```
2. Verifies `PillarsOfEternity_Data/Managed/Assembly-CSharp.dll`.
3. Detects Windows/Proton versus native Linux and selects the matching official BepInEx archive.
4. Extracts BepInEx into a temporary directory.
5. Backs up only changed files under `.poecoop-backups/<timestamp>-<pid>` in the game folder.
6. Installs BepInEx and `BepInEx/plugins/PoECoop/PoECoop.dll`.
7. Preserves existing `doorstop_config.ini`, saves, unrelated plugins, and configuration.
8. If no `BepInEx/config/poecoop.cfg` exists, creates one with `PreferredTransport = Steam`. Existing settings are not replaced.

The installer is idempotent: identical files are skipped and no new backup is created unless a file changes.

### 4.2 What the installer does not do

It does **not**:

- Install a second copy of the game or include decompiled game files.
- Modify or delete saves.
- Edit `user.reg`, Steam settings, Steam launch options, or rich presence.
- Modify the game executable.
- Compile the mod or require a .NET SDK.
- Replace existing co-op configuration.

These restrictions are deliberate. Do not reintroduce hidden registry or Steam-client changes.

### 4.3 Proton launch arguments

For the default Steam mode, the user must add this visible Steam launch option:

```text
WINEDLLOVERRIDES="winhttp=n,b" %command% -steam
```

For Direct IP fallback:

```text
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

`./launch-poecoop.sh` supplies the Wine override itself. With `-steam`, it uses `steam -applaunch 291650 -steam`; without it, Direct IP is used. Flatpak has a matching `flatpak run com.valvesoftware.Steam` fallback.

Native Windows needs `-steam` for Steam mode but not the Proton `WINEDLLOVERRIDES` option. Native Linux uses the BepInEx wrapper; `INSTALL.md` shows the full form.

### 4.4 Default transport behavior

- `src/PoECoop/Settings.cs:34` binds `PreferredTransport` to **Steam** by default.
- `UI/Overlay.cs` selects Steam when available and leaves Direct IP available as a fallback.
- The fresh installer config also defaults to Steam.
- Existing configs are not migrated or overwritten.

### 4.5 BepInEx bootstrap

- Proton/Windows uses `winhttp.dll` and the explicit Wine override above.
- Native Linux uses `run_bepinex.sh`.
- A successful log shows chainloader startup and `PoE Co-op 0.9.0 loaded`.
- F10 opens the overlay. Its key-up handler preserves chat drafts and releases game key blocking.

---

## 5. Build and release workflow

### 5.1 Fresh clone build

Requirements: .NET SDK 8+, `curl`, `unzip`, and `zip`.

```sh
./scripts/fetch-dependencies.sh
./scripts/build-release.sh
```

`fetch-dependencies.sh` downloads the two official BepInEx 5.4.23.6 archives into ignored `tools/`.

`build-release.sh`:

- Builds the `Release` configuration.
- Redirects the automatic deployment to a temporary directory rather than installing into the game.
- Produces `dist/PoECoop-VERSION.zip` and a SHA-256 file.
- Includes only the plugin, both BepInEx archives, installers, launcher, and `INSTALL.md`.

### 5.2 Build against another game directory

```sh
dotnet build src/PoECoop/PoECoop.csproj -c Release -p:GameDir="/path/to/Pillars of Eternity"
```

The normal project deployment copies the DLL into `<game>/BepInEx/plugins/PoECoop/`.

### 5.3 Release creation

The current GitHub release was created with `gh release create v0.9.0 dist/PoECoop-0.9.0.zip` and its notes. Before another release:

1. Commit and push all source changes.
2. Run `./scripts/build-release.sh`.
3. Verify `unzip -t dist/PoECoop-VERSION.zip`.
4. Verify the SHA-256 file.
5. Test the actual packaged installer, not just a DLL built from the working tree.
6. Upload the archive to the GitHub release and download it again for comparison.

---

## 6. Core architecture

### 6.1 Authority model

The mod is **host-authoritative**, not lockstep:

- Host simulates the world, AI, combat, turn scheduler, scripts, loot generation, item transfers, dialogue choices, rest, and persistence.
- Guests render authoritative snapshots and presentation events.
- Guests send semantic commands: movement, attacks, abilities, Finish/Delay, dialogue choices, loot/inventory actions, pause, and travel.
- The host validates ownership, current turn, revision, request identity, and native access rules before applying commands.

Unity pathfinding, floating point, and game RNG are not deterministic across processes, so the guest must not simulate independently.

### 6.2 Startup and update order

`Plugin.Awake` (`src/PoECoop/Plugin.cs:62`) constructs:

1. Session and Steam lobby service.
2. `FlowCoordinator` and save handoff.
3. Ownership and entity/item registries.
4. Shared-flow modules: time, dialogue, area, rest, inventory, loot.
5. Combat modules: position, vitals, combat state, tactical state, effects, attack/projectile visuals, logs, ability states.
6. Orders, tactical orders, and overlay.
7. DevTools, then `Harmony.PatchAll()`.

`Plugin.Update` uses unscaled time and follows this general order:

1. Session polling.
2. Flow/readiness and save handoff.
3. Entity/item updates.
4. Shared-flow updates.
5. Position/vitals/combat/tactical/effect/visual/log/ability updates.
6. Ownership and order collection.
7. Steam lobby service and overlay.

Disposal occurs in reverse-safe order. Every module with `Update` and `Dispose` should remain main-thread-only.

### 6.3 Important state predicates

- `Plugin.IsGuest`: connected to another session.
- `Plugin.IsHost`: hosting.
- `Plugin.IsMirroring`: guest, in a game session, and not loading.
- Some guards must apply while `IsGuest`, not only `IsMirroring`, because level-load scripts can otherwise run locally.

### 6.4 Session generation

`Session.Generation` changes whenever the session is replaced/reset. It protects against stale events, old handoffs, and acknowledgements sent to a new host.

Do not weaken generation checks when changing connection logic. A late callback must not revive an old session.

### 6.5 World epochs and readiness

`FlowCoordinator` correlates:

1. `WorldState(Handoff)` before save transfer.
2. Guest loads the save and sends `WorldReady`.
3. Host validates epoch, transfer, scene, then sends `WorldState(Ready)`.
4. Only then does the peer receive normal gameplay traffic and execute commands.

A real host level load gets a new world epoch. A late join reuses the existing epoch but gets a different transfer ID. Existing guests retain their world when another guest joins.

Readiness barriers block input, AI, movers, tactical updates, and effective time during required handoffs. The host barrier is not merely pause: the game still accepts some orders while paused.

### 6.6 Save handoff

`Sync/SaveHandoff.cs`:

- Saves to a temporary host file.
- Waits for native background save completion.
- Sends up to 256 MiB in 256 KiB chunks.
- Tracks generation, world epoch, and transfer ID.
- Cancels pending loads when the session changes.
- Suppresses stale completion/acknowledgement if the native load became uncancellable.
- Deletes temporary handoff files.

Joining during combat or a blocking conversation/cutscene/menu waits. Do not force-save those states.

---

## 7. Protocol and networking

### 7.1 Message types

`src/PoECoop/Net/Messages.cs:8` defines the first byte of every application message.

| IDs | Purpose |
|---|---|
| 1–6 | Hello, Welcome, Reject, PlayerJoined, PlayerLeft, Chat |
| 16–18 | SaveBegin, SaveChunk, SaveLoaded |
| 19 | Positions |
| 20 | Ownership |
| 21 | Move/Attack/Use/Stop/targeted and non-targeted ability orders |
| 22 | Global combat state |
| 23 | Health/stamina/death/unconscious vitals |
| 24 | Status-effect lists |
| 25 | Attack animation events |
| 26 | Finalized combat-log entries |
| 27 | Ability cooldown/resource state |
| 28–29 | Dynamic entity definition/removal |
| 30 | Projectile visuals and pulse events |
| 31 | Complete ordered tactical snapshots and current-turn token |
| 32 | Tactical commands |
| 33 | Tactical command result |
| 34 | World handoff/readiness marker |
| 35 | Guest world load result |
| 36 | Pause/speed/world clock |
| 37 | Shared-flow command |
| 38 | Shared-flow result |
| 39 | Dialogue state |
| 40 | Rest state |
| 41 | Loot session state |
| 42 | Inventory snapshots |
| 43 | Equipment snapshots |

Do not change a layout without bumping the protocol. Existing version checks reject mismatched mod, game, and protocol versions.

### 7.2 Shared-flow envelope

`FlowCommand` and `FlowResult` use 64-bit world epochs and request IDs:

```text
worldEpoch: Int64
requestId: Int64
domain: Byte
kind: Int32
payload: domain-specific
```

Results add `result:Int32` and `stateSeq:Int64`.

Domains:

1. Pause
2. Dialogue
3. Area
4. Rest
5. Loot
6. Inventory

`RequestLedger` keeps 256 fingerprinted requests per player. Exact duplicates replay their cached result without executing again. Reusing a request ID with different content is rejected.

### 7.3 Direct TCP

- Default port: **14242**.
- Four-byte little-endian length followed by message body.
- Maximum message: **1 MiB**.
- Reliable ordered frames with `NoDelay`.

Queue limits in `TransportLimits`:

- Incoming: 16 MiB/link, 64 MiB total.
- Incoming messages: 16K/link, 64K total.
- Outgoing: 288 MiB/link, 64K messages.

The outgoing size accommodates a 256 MiB save plus framing overhead. Sustained overload disconnects rather than growing indefinitely.

### 7.4 Steam lobby and P2P

- Steam is the default transport.
- `SteamLobbyService` owns create/join/invite/leave operations.
- `SteamTransport` attaches to an existing validated lobby; it does not create a second one.
- Channel 0 carries a transport nonce handshake and keepalives.
- Channel 1 carries unchanged application messages with `k_EP2PSendReliable`.
- Application `Hello` is sent only after the transport handshake completes, preventing loss of the first message.
- Hosts accept only current lobby members, reject self/strangers/duplicates, and map `CSteamID` to local integer connection IDs.
- Guests accept only the original lobby owner. Lobby-owner migration disconnects rather than migrating authority.
- Steam callbacks are rooted until disposal and execute through the game's main-thread callback pump.
- The mod does not call `SteamAPI.Init`, `RunCallbacks`, or `Shutdown`; the game owns those lifetimes.
- No rich-presence writes are made.

Steam behavior uses false-send retry: the queue head is retained if `SendP2PPacket` returns false, avoiding dropped/reordered chunks. Incoming accounting is bounded with at most one 1 MiB lookahead packet.

### 7.5 String and packet hardening

`MessageReader.String()` decodes the 7-bit length explicitly and rejects:

- Malformed/negative lengths.
- Over-limit strings.
- Declared lengths larger than remaining packet bytes.
- Truncated UTF-8 payloads.

`MessageReader.Finish()` rejects trailing data where used. New message handlers should use it consistently.

A 1 MiB frame cap alone is not enough: attacker-declared string lengths and receive queues must also be bounded.

---

## 8. Source map by subsystem

### 8.1 Networking and lifecycle

| File | Responsibility |
|---|---|
| `Net/Messages.cs` | Message IDs, protocol version, reader/writer and bounds |
| `Net/Session.cs` | Session state, player list, readiness gates, chat, factory selection, reset/generation |
| `Net/TcpTransport.cs` | Direct TCP, framing, backpressure |
| `Net/TransportLimits.cs` | Shared message/queue limits |
| `Net/SteamLobbyService.cs` | Steam availability, lobby lifecycle, invites, launch args, friend cache |
| `Net/SteamTransport.cs` | P2P handshake, packet transport, disconnects, queues |
| `Net/ISteamBackend.cs` | Real Steam adapter and fake-test seam |
| `Net/FlowWire.cs` | Shared-flow envelopes and result codes |
| `Sync/FlowCoordinator.cs` | World epochs, readiness, barriers, domain routing |
| `Sync/RequestLedger.cs` | Bounded duplicate detection |
| `Sync/SaveHandoff.cs` | Save transfer, cancellation, stale-ack prevention |

### 8.2 Combat and turn-based

| File | Responsibility |
|---|---|
| `Sync/PositionSync.cs` | Position/facing/current and desired movement speeds |
| `Sync/VitalsSync.cs` | Health, stamina, down/death/revival presentation |
| `Sync/CombatStateSync.cs` | Global combat/game-over state |
| `Sync/StatusEffectSync.cs` | Host-keyed effect snapshots and origin reconstruction |
| `Sync/AbilityStateSync.cs` | Cooldowns, spell casts, class resources, modal state |
| `Sync/AttackVisualSync.cs` | Animation actions and cleanup |
| `Sync/ProjectileVisualSync.cs` | Inert projectile/pulse visuals |
| `Sync/CombatLogSync.cs` | Finalized console batches, no object callbacks |
| `Sync/TacticalStateSync.cs` | Complete ordered tactical state and settings |
| `Sync/TacticalEventPresentation.cs` | Native event reconstruction without gameplay lifecycle |
| `Sync/TacticalOrders.cs` | Finish/Delay/previews, turn validation and dedup |
| `Sync/EntityRegistry.cs` | Saved/runtime actor identity and spawn replication |

### 8.3 Shared flow and inventory

| File | Responsibility |
|---|---|
| `Sync/TimeSync.cs` | Pause/speed/world clock and private UI policy |
| `Sync/DialogueSync.cs` | Settled conversations, picker, response commands |
| `Sync/DialoguePresentation.cs` | Native conversation reconstruction |
| `Sync/AreaTransitionSync.cs` | Semantic exits and save-backed transitions |
| `Sync/RestSync.cs` | Camp/inn/rest transactions and presentation |
| `Sync/ItemRegistry.cs` | Persistent/runtime item IDs and custom item state |
| `Sync/InventorySync.cs` | Atomic inventory/equipment snapshots |
| `Sync/InventoryCommands.cs` | Native model transactions and rollback |
| `Sync/LootSync.cs` | Shared loot sessions, viewers, take/close behavior |

### 8.4 Patches and UI

- `Patches/GuestFreezePatches.cs`: AI, scripts, triggers, Mover, cutscenes.
- `Patches/CombatSimulationPatches.cs`: AI combat events and traps.
- `Patches/TacticalModePatches.cs`: tactical scheduler and mutation guards.
- `Patches/TacticalCommandPatches.cs`: Finish/Delay and preview input capture.
- `Patches/TimePatches.cs`: pause input, stale pause restoration, private UI handling.
- `Patches/DialoguePatches.cs`: conversation start/end/input and script suppression.
- `Patches/InventoryPatches.cs`: intent capture before native item mutation.
- `Patches/LootPatches.cs`: loot open/UI and generation guards.
- `UI/Overlay.cs`: Steam/direct connection UX, friend list, invites, party assignment, chat.
- `Settings.cs`: preferred transport, visibility, name, address, port, F10.
- `DevTools.cs`: opt-in probes and test fixtures; keep disabled in normal play.

---

## 9. Turn-based behavior and invariants

### 9.1 Host tactical snapshots

`TacticalStateSync` sends complete ordered snapshots at up to 20 Hz with full repair every two seconds. They include:

- Tactical mode and gameplay-affecting settings.
- Epoch and sequence.
- Ordered event IDs in host order.
- Current event, including a detached event during spin-down.
- Current actor and `WhoseTurn`, which are not always the same.
- Current phase/timers/locks.
- Actor initiative, available action/movement, turn-event counts, free-action state.
- Turn, cast-finished, round-start, delay, turn-lock, and projectile-turn events.

Never locally sort this queue. Native tie-breaking can use process-local Unity instance IDs.

### 9.2 Presentation without simulation

`TacticalEventPresentation` reconstructs native event objects for the UI while preserving the existing `m_events` list instance. It must not call native:

- `NotifyEventOrderChanged`
- `PopEvent`
- `OnPreStart`, `OnStart`, `OnEnd`, or `OnUpdate`
- `FinishSpinUp` / `FinishSpinDown`
- Actor/environment gameplay callbacks

Turn-event constructors increment counts, and round events subscribe to round notifications. Every construction needs balanced teardown. Repeated snapshots must not leak turn counts, subscriptions, or UI transition events.

### 9.3 Current-turn commands

- Ordinary orders carry expected epoch/current-event ID, zero in RTwP.
- Tactical commands include request ID and the same turn token.
- Host verifies ownership, active `WhoseTurn`, controllability, and native Finish/Delay rules.
- Delayed packets from old turns must not execute in a later turn of the same character.
- Duplicate request IDs return cached results without ending/delaying twice.
- Standard attacks/abilities need an available action; movement needs allowance; native free-action rules remain intact.
- Native queue is disabled in tactical mode.

### 9.4 Important fixes to preserve

- Stationary `Mover.DesiredSpeed` must replicate even while current speed is zero. Otherwise `AvailableMoveDistance` becomes zero and native input refuses Move.
- `UIPassTurnButton.CanEndTurn` needs the guest `WaitForTurn` accommodation so a click reaches semantic Finish capture.
- Dead/unconscious actors must not enter initiative until revived.
- Full-refresh projectile cleanup must also destroy associated pulse visuals.
- Direct health/death handlers must not run on guests; presentation uses raw vitals and narrow reaction maintenance.

---

## 10. Shared-flow invariants

### 10.1 Pause and private UI

- Explicit/auto/shared pause is host-authoritative.
- Private inventory/loot/options windows do not pause the other peer.
- Closing old UI must not restore a stale pause over a newer remote pause.
- Turn locks and pause are different concepts.
- Network input uses unscaled time and continues while paused.
- Host private UI must not force a shared zero timescale.
- Real guest pause from private UI must still work, including Space where appropriate.

### 10.2 Dialogue

- Host chooses conversation, node, random branch, scripts, and quest/global/faction effects.
- Guests see settled host-rendered state and qualified responses.
- Response identity is the response **node ID**, never its displayed index.
- The initiating player is the sole picker; scripts/chained starts default to host or inherit the previous picker.
- Non-picker and stale commands do not advance dialogue.
- Duplicate commands replay receipts only.
- Host-only: conversation scripts and `QuestManager.TriggerTalkEvent`.
- Picker disconnect falls back to host.

### 10.3 Area transitions

- Ordinary cross-scene transitions remain save-backed for correctness across revisits.
- Guest `ChangeLevel` is blocked, including during loading.
- Guest exits become semantic requests; host executes native whole-party flow with temporary internal selection restoration.
- Readiness, start positions, ownership, settings, inventory, and world changes must converge.
- Same-map waypoint transitions do not create a new world epoch.
- Combat requests must not transition or mutate the tactical queue.

### 10.4 Rest

- Host alone calls native Camp/Rest/PurchaseRoom.
- A real camp consumes exactly one supply and advances exactly 28,800 seconds.
- Open/cancel and no-supply/in-combat requests have no mutation.
- Survival selection requires owned character and valid index.
- Duplicate confirms cannot rest twice.
- Guest gets prompt/dropdown/movie/results only; no local rest, time jump, or rest-finished scripts.

### 10.5 Loot and inventory

- Loot generation and item mutations happen only on the host.
- Runtime items have a separate identity domain from actors.
- Saved and runtime item GUIDs persist across transfers and fresh handoffs.
- Runtime registration calls native `Persistence.SaveObject()` after assigning the authoritative ID.
- Splits create a new shared item ID; merging retires the absorbed ID.
- Commands operate on model objects, not host UI tiles or selected host character.
- Multi-viewer close happens only after the last viewer leaves.
- Partial clicks allow native double-click split dialogs; duplicates do not transfer twice.
- Guest drag/drop is intent capture, never optimistic mutation.
- Tactical weapon and consumable restrictions recheck on the host.

---

## 11. Prioritized bug-search plan

The items below are **investigation leads, not claims of existing bugs**. Each includes what to inspect and how to prove or reject it.

### P0: crash, deadlock, data loss, or authority violation

#### P0.1 Unbounded receive loop and frame starvation

- Inspect `Session.Update` and per-module send cadence.
- Earlier defect: inventory timers reset every frame and caused thousands of full snapshots, starving Unity before the probe coroutine could run.
- Verify every snapshot timer changes only when state actually changes, and incoming processing cannot keep the game inside an infinite poll loop.
- Test large stash/container manifests and slow guest FPS. Require frames and input to continue progressing and queue accounting to drain.

#### P0.2 Readiness deadlock or failed-handoff limbo

- Exercise initial join, late join, multi-guest join, wrong scene, failed load, disconnect during load, and same-process join to a different host.
- Require failed peers not to hold the barrier forever and failed transfers to be retried or fail explicitly.
- Check duplicate/stale `WorldReady` cannot mark a peer ready.
- Verify ownership is resent after readiness.

#### P0.3 Remote allocation or packet flood

- Fuzz declared string lengths, counts, pages, enums, NaN/infinite values, duplicate IDs, and trailing bytes.
- Include pre-Hello packets and tactical/inventory payloads.
- Require rejection without large allocation or whole-session instability.
- Verify only the offending connection is dropped on the host.

#### P0.4 Guest applies authoritative gameplay locally

- Audit all new Harmony patches for both `Plugin.IsGuest` and `Plugin.IsMirroring` coverage.
- Look for damage, effect ticks, loot generation, rest, scripts, local spawn, death handlers, and inventory operations during loading as well as normal mirroring.
- Require host snapshots to be the only source of those results.

#### P0.5 Stale save or acknowledgement after session replacement

- Disconnect during the 450 ms received-save fade and during actual native loading.
- Confirm no stale save loads into offline/new-host state and no old `SaveLoaded` reaches the new host.
- Test rapid Leave/Join and abrupt host kill.

#### P0.6 Tactical event constructor/destructor leaks

- Repeatedly apply full snapshots, mode switches, scene loads, long casts, death, and revival.
- Check `TacticalTurnEventCount`, round subscriptions, detached current events, and duplicate UI transition callbacks.
- Ensure `m_eventsReadOnly` still wraps the original list.

#### P0.7 Item duplication or identity loss

- Test runtime-created items through split, merge, equipment, quick use, fresh handoff, and A→B→A transitions.
- Require exact ID conservation and no host/guest duplicates.
- Check native persistence registration happens before the host saves, not only in the in-memory registry.

#### P0.8 Protocol/layout mismatch

- Compare every reader and writer, including 64-bit epoch/request fields and projectile pulse counts.
- Test old/duplicate sequence, trailing data, invalid enum, and large payloads.
- Bump protocol for every incompatible layout change and confirm rejection UX.

#### P0.9 Steam listener/start failure and callback races

- Verify asynchronous startup failure emits exactly one `Disconnected(0)` rather than leaving `Session.Hosting` stuck.
- Test stale create/join callbacks after cancel/dispose, owner migration, duplicate sessions, and Steam shutdown.
- Require no second lobby, no second `Session.Join`, and no callback into a replacement session.

### P1: common gameplay and UX defects

#### P1.1 Tactical movement refuses valid clicks

- Inspect desired/current speed, available movement, and available move distance.
- Require valid stationary speed even while the character is idle.
- Test ordinary, short, long, and beyond-allowance movement.

#### P1.2 Pass button never reaches network capture

- Test native `WaitForTurn`, performing attack, long cast, wrong actor, and key/button activation.
- A native UI gate must not hide the request from Harmony capture.
- One click must advance exactly once.

#### P1.3 Stale or duplicate action executes in a later turn

- Replay old Move/Attack/ability/Finish/Delay tokens after the same actor becomes current again.
- Verify request-ID payload conflicts and stale world/turn tokens do not mutate state.

#### P1.4 Effect-origin reconstruction mismatch

- Test Prone, modal Defender, ConstantRecovery duplicates, equipment mods, aura/suppression, DoT/HoT, and newly granted abilities.
- Match host effect ID, origin, duration/time-left, applied/suppressed/suspended state, and removal.
- Unsupported recipes must log explicitly rather than silently applying a wrong effect.

#### P1.5 Fast projectile lifecycle or stale visuals

- Test fast Crossbow shots, final spawn+remove in one batch, full-refresh stale removal, Chill Fog pulses, and scene/disconnect cleanup.
- Guest visuals must remain inert: no scripts, rigidbodies, colliders, ammo use, or independent damage.

#### P1.6 Death and initiative re-entry

- Let a party member die naturally, revive with native HealParty, and compare reaction, collision, turn-event counts, and initiative.
- Preview events must not be counted as real re-entry.

#### P1.7 Private UI pause and input leakage

- Open/close private inventory while issuing pause, switching speed, opening dialogue, and changing focus.
- Ensure private windows do not pause the peer or swallow the next pause command.
- Overlay text fields must block game hotkeys and release on hide/close.

#### P1.8 Dialogue authority and response identity

- Test host non-picker input, stale response node, duplicate request, scripted chain, random/cycle nodes, and picker disconnect.
- Use response node IDs, not indexes. Require exactly one script execution.

#### P1.9 Loot races and partial stack input

- Two viewers open one container, take the same item, split a stack, TakeAll, close independently, and disconnect.
- Verify one mutation, correct source/destination IDs, no reroll, and last-viewer cleanup.

#### P1.10 Runtime item persistence after native save

- Save natively after runtime item creation and hand off to a fresh guest.
- Verify the original IDs, placements, mods, consumables, equipment, and effects.
- Do not accept convergence achieved only by replacing IDs on both peers.

#### P1.11 Transition epochs and old-world commands

- A→B→A after inventory/rest/dialogue changes.
- Reject commands from a previous world even after loading finishes.
- Ensure no old tactical queue/current token survives.

#### P1.12 Hidden-window and focus throttling

- Compare foreground, background, and hidden windows for FPS, Steam callback cadence, orders, and snapshots.
- Do not misdiagnose normal focus throttling as network latency.
- Multi-instance test mode already disables vsync/caps 60 FPS; production focus policy needs deliberate review.

### P2: broader parity and long-tail cases

- Same-map and world-map variants, scaler prompts, and cutscene-forced transitions.
- Stronghold upgrades, hirelings, visitors, taxes, adventures, random events, and UI/log state.
- Focus, wounds, chanter phrases, spiritshift, aura/suppression/DoT/equipment-origin effects.
- Beams, bouncing/pulsed exotic projectiles, custom VFX/scripts, and summoned weapons.
- Soulbind, grimoire, charged item abilities, quick-grimoire swaps, two-handed aliases, and multi-select inventory.
- Multi-guest UI ownership, preview policy, disconnect cleanup, and lobby-capacity handling.

---

## 12. Test infrastructure

### 12.1 General safety rules

- Use isolated host/guest folders and never overwrite real saves.
- Hash original saves before and after each scenario.
- Do not quicksave unless the scenario explicitly tests persistence; then isolate and remove the new file after hashing it.
- Terminate only processes started by the test driver.
- Use display and build locks to prevent agents/tests from stealing windows or racing builds:
  - `.dev/live-input-display.lock`
  - `.dev/combat-build.lock`
- Keep `.dev` out of commits.
- Distinguish normal handoff temporary saves from unwanted fixture saves and disclose both.

### 12.2 Real X11 input harness

`scripts/x11_input.py` uses X11 `XWarpPointer` plus targeted `XSendEvent`; XTest is only a fallback because this desktop's rootless Xwayland ignores it.

Useful commands:

```sh
python3 scripts/x11_input.py windows --match 'Pillars'
python3 scripts/x11_input.py find --pid 12345
python3 scripts/x11_input.py geometry --window 0x1600004
python3 scripts/x11_input.py focus --window 0x1600004
python3 scripts/x11_input.py click 640 400 --window 0x1600004 --local --focus
python3 scripts/x11_input.py key ctrl+s --window 0x1600004
python3 scripts/x11_input.py key Escape --window 0x1600004
python3 scripts/x11_input.py type 'message from guest' --window 0x1600004
python3 scripts/x11_input.py screenshot .dev/guest-view.png --window 0x1600004
python3 scripts/x11_input.py pointer
```

Use `--local` for game-client coordinates and scale coordinates when the probe surface differs from the X11 client. Synthetic modifiers persist only inside one harness process; use one Python driver for Shift-held sequences.

### 12.3 Launching local test instances

`scripts/run-instance.sh` is for native Linux direct-IP tests:

```sh
COOP_ISOLATE_HOST=1 scripts/run-instance.sh host \
  --coop-continue --coop-port 14242 --coop-give-slot 1 \
  --coop-no-edge-scroll --coop-probe "$PWD/.dev/host-probe.jsonl"

scripts/run-instance.sh guest \
  --coop-port 14242 --coop-no-edge-scroll \
  --coop-probe "$PWD/.dev/guest-probe.jsonl"
```

- Host defaults to the normal isolated Encampment save with `COOP_ISOLATE_HOST=1`.
- Slot 1, Calisca, is assigned to Guest by `--coop-give-slot 1`.
- Avoid `--coop-walk`, `--coop-forge-order`, `--coop-click-container`, and `--coop-log-positions` for real-input acceptance.
- Do not use `--coop-log-positions` for selection assertions because it mutates selection.

For Proton, use the packaged installer and an isolated compatdata prefix. Proton supports the same game/protocol but its windows and save paths differ. Native-Linux launch scripts are not evidence of Proton support.

### 12.4 Probe data

`--coop-probe FILE` writes JSONL every 0.5 seconds while in a level. It uses `DataContractJsonSerializer`, not `JsonUtility`, because nested custom types were otherwise omitted.

Probe sections include:

- Party/world actors, ownership, selection, world/screen positions, states, targets, move destinations.
- Vitals, effects, abilities, cooldowns, modal state, animation/reaction.
- Tactical settings, current event/actor, ordered events, action/movement, pass/delay controls.
- Flow readiness, time, dialogue, area, rest, loot, inventory/equipment.
- Steam availability, lobby/member counts, transport queues and relay diagnostics (count-only, no account IDs).

Do not enable probes in normal play.

### 12.5 Common fixtures

- Scene: `AR_0701_Encampment`.
- Host main: `ddddd`.
- Guest-controlled: Calisca, party slot 1.
- NPC dialogue target: `NPC_Odema` (`f1c1c2eb-0cbe-42af-9d46-129581861d0c`).
- Naturally hostile target: Young Wolf (`85a4ad37-2977-4e3e-8a7c-e0660e6c59d9`).
- Targeted ability: Calisca's Knock Down.
- Non-targeted: Second Wind.
- Position-targeted damage: Fireball.
- Modal: Defender.
- Long cast: Chill Fog; Fireball is Standard in this build and does not produce a long-cast event.
- Ranged projectile: Crossbow/BowArrow.
- Fixed summon: Essential Phantom.
- Random summon: Conjure Lesser Blight.
- Multi-copy encounter: `Enc_Object_Amb_Grp_2` (`2541a5f1-86ed-4654-af32-ab91c75ed076`) with three copies of one prefab.
- Container: lockpick stack, where taking 2 of 5 must conserve source 3 + destination 2.
- Round trip: `AR_0722_Home_03` → `AR_0705_Gilded_Vale` → `AR_0722_Home_03`.

Console activation uses Backquote, Enter, ASCII command, Enter. `IRoll20s` enables cheats only in disposable fixtures. The built-in `CSharp` command is unreliable on this runtime because dynamic assembly enumeration throws `NotSupportedException`; use built-in commands instead.

### 12.6 Evidence directories

Historical evidence is local under `.dev`, not committed. Important examples:

- `.dev/test-m7-flow-final/result.json`
- `.dev/test-m7-tactical-final/result.json`
- `.dev/test-m7-rtwp-final/result.json`
- `.dev/test-proton/coop/result.json`
- `.dev/test-proton-steam/result.json`
- `.dev/test-steam-harness/`
- `.dev/test-handoff-cancel/result.json`

A result file named `FAIL` may reflect an obsolete assertion rather than a product defect. Read its checks and subsequent finalizer instead of relying on its status alone. The natural-party-death artifact expected a separate kill message that the base game intentionally suppresses on the first normal-difficulty death.

---

## 13. Verified evidence summary

| Suite | Result |
|---|---|
| Shared-flow final | **133/133 checks passed** |
| Final tactical regression | **65/65 checks passed** |
| Final RTwP core | **56/56 checks passed** |
| RTwP Crossbow | **26/26 checks passed** |
| Proton Direct IP | **20/20 checks passed** |
| Direct-IP regression after Steam work | **49/49 checks passed** |
| One-account Steam API/lobby smoke | **19/19 checks passed** |
| Deterministic fake Steam backend | **14/14 tests passed** |
| Disconnect-safe handoff | **17/17 checks passed** |
| Release build | **0 warnings, 0 errors** |

The 256 MiB fake-backend burst transferred 1,024 ordered 256 KiB messages with payload and queue-accounting verification. This is strong transport evidence, not two-account live evidence.

---

## 14. Known observations and limitations

### 14.1 Native exceptions

The following have appeared without PoECoop/Harmony frames and are documented as native/base-game observations, not automatically mod defects:

- `AIController.HandleRequestHelpEvent`
- `TacticalProjectileTurnEvent.OnUpdate`
- Guest `SmartCamera` paths

Preserve stack traces in reports and explicitly check whether plugin frames exist before assigning ownership.

### 14.2 Steam acceptance

- One-account lobby creation, metadata, invite-overlay open/cancel, self-join rejection, and cleanup are verified.
- Self-P2P is not valid two-peer evidence and is rejected.
- Real two-account invitation acceptance, guest join, Steam P2P handshake/save handoff/gameplay, NAT traversal, relay fallback, and disconnect/leave remain open.

### 14.3 Gameplay acceptance

- Coverage is fixture-based, not exhaustive parity.
- Focus, wounds, chanter phrases, spiritshift timers, aura/suppression/DoT/equipment origins, and exotic projectiles need focused validation.
- Same-map and world-map variants are not fully accepted.
- Stronghold-wide state/log parity is not claimed.
- Joining during combat still waits for combat to end.

### 14.4 Save caveats

Some transition fixtures created native autosaves. They were removed during cleanup and the original save hashes remained unchanged. Normal handoff also creates temporary `coop-*` files that the plugin deletes automatically. Disclose rather than hiding either category.

---

## 15. Recommended bug-report format

For each finding include:

1. **Title and severity** (P0/P1/P2).
2. **Mode and transport** (RoundBased or RTwP; Steam or Direct IP; Proton or native).
3. **Build identity** (commit, plugin, protocol, game build, BepInEx, Proton).
4. **Exact reproduction steps**, including real mouse/keyboard input.
5. **Expected invariant**.
6. **Actual result**.
7. **First divergent probe frame or native call**, not only the eventual symptom.
8. Host and guest:
   - `host-probe.jsonl`, `guest-probe.jsonl`
   - session logs
   - Unity `Player.log`
   - finalized console JSON
   - screenshots
   - action/network traces where relevant
9. Save hashes before/after and any transient save disclosure.
10. Whether the issue is confirmed, suspected, fixture-limited, base-game, or out of scope.
11. Minimal fix and regression scenario.
12. Evidence that unrelated existing suites still pass.

A good bug fix includes a new real-input reproduction, not only a code comment or a successful build.

---

## 16. Suggested first investigation sequence

1. Build the current source and verify the release archive.
2. Run readiness and private-UI smoke: handoff, ownership, Move, Pass, pause.
3. Stress large inventory/container refreshes while watching frame time and queue counts.
4. Run three tactical rounds with exact event/current-actor comparison and a Finish/Delay pair.
5. Run Knock Down and Chill Fog, checking action use, effect IDs, cast identity, pulses, and cleanup.
6. Kill and revive Calisca, checking turn counts and non-preview initiative re-entry.
7. Open one container with two viewers, split a stack, race a take, and close independently.
8. Create runtime items, transition A→B→A, then hand off to a fresh guest and compare IDs exactly.
9. Disconnect during save loading and rejoin a different host in the same guest process.
10. Create/cancel a Steam lobby, test startup failure, and verify no `user.reg`, rich-presence, or second-lobby side effects.
11. Rerun the full tactical and RTwP regression suites on the same DLL.
12. Only then schedule real two-account Steam acceptance.

---

## 17. Change discipline

- Keep turn-based as the primary acceptance path and rerun RTwP after shared changes.
- Do not replace host authority with guest simulation or replay damage/impact events.
- Preserve world/session/turn correlation and idempotent command handling.
- Preserve bounded queues and bounded per-frame work.
- Keep Unity and Steam calls on the main thread.
- Do not introduce registry edits, Steam launch-option writes, or rich-presence writes.
- Do not add decompiled game files or test artifacts to Git.
- Keep test-only probes disabled by default.
- Update comments and this document when behavior changes.
- Build and test the actual packaged installer after meaningful install changes.
- Commit and push source before publishing a release.

---

## 18. Quick reference: most important invariants

1. **Only the host mutates authoritative world state.**
2. **Guests send semantic commands; host validates ownership, turn, revision, and native access.**
3. **Every session replacement/world change invalidates stale state and acknowledgements.**
4. **Guest input cannot bypass private ownership or the active turn.**
5. **Every command executes at most once per request ID.**
6. **All network strings, counts, messages, queues, and frame work are bounded.**
7. **Native tactical presentation never runs the native guest tactical scheduler.**
8. **Guest damage/effect/death/loot/rest/inventory mutations never run locally.**
9. **Runtime actor/item IDs are stable across handoff and native saves.**
10. **Proton setup uses explicit launch arguments, never hidden registry or Steam-client edits.**
11. **Every bug fix includes a relevant real-input regression and preserved build/test evidence.**
