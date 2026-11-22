# Playtest issue log

This file records user-reported problems from live co-op play. The symptoms are confirmed reports; the possible causes below are investigation leads, not claims that the root cause has already been proven.

## P0 — Ground loot is not visible to the guest

### Reported symptom

Loot dropped on the ground is visible to the host but not to the guest. This prevents the guest from seeing or interacting with dropped items.

### Current architectural suspicion

The current loot implementation is centered on **container inventories and loot-window sessions**, not a complete ground-item presentation path.

Relevant current components:

- `Sync/LootSync.cs` handles shared container opening, taking, TakeAll, and viewer closing.
- `Patches/LootPatches.cs` suppresses guest loot population/drop/regeneration paths.
- `Sync/EntityRegistry.cs` replicates saved and runtime **actors** such as encounters and summons.
- `Sync/ItemRegistry.cs` tracks items inside inventories/equipment and their custom state.
- `Sync/InventorySync.cs` replicates container and party inventory contents.

A ground drop may be an `Item`, a container/bag, a corpse-mounted inventory, or another runtime object. Those cases need to be distinguished. Simply calling native drop/populate on the guest would duplicate generation and must not be the fix.

### What to verify first

1. Capture the exact ground object on host and guest:
   - `GameObject.name`, `InstanceID.Guid`, prefab/source, active state, transform, renderers.
   - Whether it is an `Item`, `Container`, corpse, loot bag, or other component.
2. Check whether a host `EntityDefine` exists for it and whether the guest creates/binds an object.
3. Check whether guest spawn/drop guards are suppressing it without a replacement.
4. Check whether it exists but is hidden by fog, active-state, position, or renderer settings.
5. Verify whether a guest can interact with it despite it being visually absent.

### Implementation direction

Add a `GroundLootSync` or extend the entity/loot boundary to replicate:

- Host object ID and prefab/source identity.
- Spawn recipe, transform, active state, and removal/despawn revision.
- Underlying container inventory and item GUIDs when applicable.
- Visual-only guest presentation without guest RNG, drop scripts, or independent container opening.

Required tests:

- Host drops an item from inventory.
- Enemy dies and generates a corpse/bag.
- Guest sees the same ground object in the same place and can open/take it.
- Host-driven removal and area transitions remove it exactly once.
- Rejoining and A→B→A do not duplicate it or reroll its contents.

---

## P0 — Enemy is hostile to the host but not to the guest

### Reported symptom

An enemy appears hostile to the host but not to the guest. This can produce different cursor/targeting behavior and may prevent normal attack/ability use.

### Current architectural suspicion

There is no general faction/team-relationship synchronization. Existing combat snapshots replicate health/effects but not the complete faction state used by native targeting and cursor logic.

Relevant current components:

- `Orders.cs` validates and executes host orders but the guest also uses local native cursor/faction checks.
- `DialogueSync.cs` keeps scripts and quest/team effects host-only.
- No `FactionStateSync` or general quest/global relationship stream currently exists.

Our prior combat fixtures often called `SetIsHostile ... true` on **both peers**, which masked this missing replication. A new acceptance test must change hostility **only on the host**.

### What to verify first

1. Record the enemy's `Faction` and `RelationshipToPlayer` on host and guest.
2. Compare native `IsHostile`, `UnitHostileToPlayer`, cursor type, targetability, and selected-team relationship.
3. Determine whether hostility came from a script, encounter, dialogue, area transition, or direct console command.
4. Check whether guest targeting fails while force-attack still works.

### Implementation direction

Add `FactionStateSync` or a focused relationship stream in progression state:

- Replicate the authoritative team/relationship for every relevant entity.
- Include team ID, relationship-to-player, hostile/friendly state, and revision/epoch.
- Apply backing state on the guest without firing faction-change scripts or combat start locally.
- Update target/cursor validation from host snapshots while preserving ownership and current-turn rules.

Required tests:

- Set an NPC hostile only on the host and verify guest cursor, targeting, and combat membership match.
- Verify neutral→hostile and hostile→neutral transitions.
- Verify relationship changes caused by dialogue/scripts and area transitions.
- Ensure there is exactly one combat start and no guest-only faction event.

---

## P0 — Severe guest latency and stutter makes play miserable

### Reported symptom

Guest performance is extremely poor, with latency and stutter severe enough to make co-op unpleasant. This needs architectural optimization, not just a higher snapshot rate.

### Current architectural suspicion

There is no single proven cause yet. Likely contributors to measure include:

- Periodic full refreshes across many independent streams causing large bursts every 2–3 seconds.
- Expensive main-thread serialization/reflection and object discovery (`FindObjectsOfType`, LINQ, repeated property reflection).
- Large status, inventory, tactical, and equipment manifests being rebuilt or allocated even when semantically unchanged.
- Message/application work performed faster than the guest can apply it, including the previously fixed per-frame full-resync issue.
- Main-thread apply work, page reconciliation, effect proxies, and inactive object creation/destruction.
- Position updates at 20 Hz alongside many other 10–20 Hz streams, without enough frame-budgeting or interpolation.
- Native FPS throttling/focus/vsync and two instances sharing one machine, which can amplify every network and main-thread cost.

### Measure before optimizing

Add opt-in timing and traffic diagnostics per subsystem:

- Capture and apply milliseconds per module per frame.
- Snapshot count and bytes per stream per second.
- Full versus delta packet counts and worst 1% frame times.
- Guest FPS, frame-time distribution, main-thread time, and GC allocations.
- Native and application queue lengths and time from host capture to guest presentation.
- Probe/profiler overhead itself; report results both with and without `--coop-probe`.

Do not guess that the network is the bottleneck just because symptoms appear on the guest.

### Implementation directions to evaluate

1. **Budget and coalesce work:**
   - Cap messages and apply work per frame per subsystem without blocking readiness.
   - Coalesce same-entity changes before serialization and avoid sending unchanged records.
   - Replace periodic full rebuilds with dirty deltas plus slower integrity refreshes where appropriate.

2. **Reduce allocation and reflection:**
   - Cache actors, inventories, components, field accessors, and UI targets.
   - Avoid repeated `FindObjectsOfType`, repeated list creation, and per-frame large DTO graphs.
   - Pool/bound reusable network buffers and snapshot records.

3. **Smooth presentation:**
   - Use timestamped movement interpolation/extrapolation instead of snapping to the newest sample.
   - Preserve authoritative positions and `Mover.DesiredSpeed`; do not smooth into invalid tactical movement.
   - Decouple network receive frequency from visual frame rate where safe.

4. **Stagger refreshes:**
   - Spread full refreshes across frames rather than firing all streams on the same interval.
   - Prioritize positions/vitals/turn-critical messages ahead of inventory, logs, and cosmetic effects.

5. **Maintain correctness:**
   - Do not remove full repair, revision checks, readiness barriers, or authoritative ordering to gain speed.
   - Do not trade one smooth guest for frequent desyncs or invisible state.

### Required performance acceptance

Measure before and after the same scenario:

- Combat with tactical turns, status effects, projectiles, and multiple enemies.
- Large stash plus open container and inventory operations.
- Area transition and dynamic summons.
- Host/guest on the same machine and, when available, two machines.

Record median and 95th/99th percentile frame times, snapshot bandwidth, queue high-water marks, and presentation delay. The goal is smooth interactive play, not merely protocol convergence.

---

## Required regression coverage for all three issues

For each fix, include:

- Real host/guest mouse/keyboard reproduction, not injected states.
- Host and guest probes, logs, screenshots, and frame/timing evidence.
- Original-save hashes before/after.
- Tactical and RTwP regression checks on the same DLL.
- No duplicate items, entities, faction events, damage, effects, or callbacks.
- No readiness or message-flood regressions.

These issues are high priority because they directly block ordinary exploration and combat, rather than only exotic edge cases.
