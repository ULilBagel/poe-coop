# Pillars of Eternity 1 co-op mod: plan

Written 2026-11-18. Self-contained so it can be picked up on another PC.
Current status (2026-11-21): milestones 0 to 7 are complete for their documented
acceptance scopes, and the current release is plugin 0.9.0 with protocol 7.
Turn-based combat is the primary, most polished mode; real-time with pause
(RTwP) is retained as the secondary mode. Milestone 7 covers correlated world
readiness and save-backed area transitions, shared time/pause with private local
UI, dialogue, rest, loot and inventory/equipment with persistent runtime item
identities. This is not a claim of exhaustive scripted-content, stronghold,
travel or exotic-item parity. Guests still wait for combat to end before save
handoff.

Milestone 8's Steam transport implementation is complete. Direct IP passed its
49-check regression, the real Steam API passed a one-account 19-check lobby
smoke test, and the deterministic fake Steam backend passed 14 checks. Proton
Experimental also passed direct host/guest installation and gameplay smoke
coverage. Real two-account friend invites, Steam P2P, NAT traversal and relay
acceptance remain unverified, so milestone 8 is not fully verified. Milestone 9
remains.

## What was verified about the game

Checked against the Steam install (app 291650, build 26435298, native Linux build):

| Fact | Value | Why it matters |
|---|---|---|
| Engine | Unity 2019.4.41f1 | BepInEx 5 supports it |
| Scripting backend | Mono (`MonoBleedingEdge`), not IL2CPP | Game code is plain .NET IL: decompilable and patchable with Harmony |
| Game code | `PillarsOfEternity_Data/Managed/Assembly-CSharp.dll` (4.4 MB) | The file to decompile |
| Steam library | Steamworks.NET 20.2.0, SDK 1.57 | Steam lobbies and P2P networking are already shipped with the game |
| Existing netcode | None (no multiplayer, lobby or network classes) | Everything network-side is ours to write |
| Classes surveyed and patched | `GameState`, `GameInput`, `TimeController`, `PersistenceManager`, `InstanceID`, `OEIRandom`, `TurnBasedCombat` | Their relevant input, save, identity and simulation paths are documented in `notes/code-survey.md` |

Re-check the Unity version and build id on another PC, since a Windows install or
a different build may differ.

## Decisions made

1. **Co-op model.** Split party: the host loads their save and assigns party
   members to each player. Dialogue choices, pause/time and shared-flow mutations
   are host-authoritative; local private UI does not pause the other player.
2. **Tooling.** BepInEx 5, the .NET SDK and ILSpy are installed; the game is
   decompiled under `decompiled/` and the plugin builds and deploys from
   `src/PoECoop/PoECoop.csproj`.
3. **Combat mode.** Turn-based (`RoundBased`/`TacticalMode`) is the primary and
   most polished mode. RTwP remains supported as the secondary mode, including
   switching modes out of combat, but new acceptance work should exercise
   turn-based first.

## Architecture

**Host-authoritative state replication.** Both players run the full game and load
the same save. Only the host simulates; the guest's game is a mirror.

- Guest to host: commands (move here, attack that, use ability, pick dialogue option).
- Host to guest: state (positions, animation, health, status effects, combat log,
  scene changes, dialogue state, pause), plus tactical settings, ordered
  initiative/presentation events and the current-turn token.
- On the guest, AI, damage rolls and scripted triggers are switched off with
  Harmony patches so nothing diverges from the host.

Lockstep (both simulate, only inputs sent) is ruled out: Unity pathfinding,
floating point and the game's RNG use are not deterministic across machines.

**Entity identity.** The game already tags persistent objects with `InstanceID`
GUIDs for saves. Use those as network ids; spawned objects (summons, projectiles)
get host-assigned ids.

**Transport.** One interface, two implementations:
- Direct TCP by IP address. This is the default and needs no co-op launch
  options. It also supports development with two game instances on one PC.
- Steam networking through the bundled Steamworks.NET, implemented in plugin
  0.9.0 for play without IP typing or port forwarding. Steam mode is selected
  with `-steam` through `scripts/launch-poecoop.sh` or the platform's Steam
  launch options. Real two-account P2P/NAT/relay acceptance is still pending.

**Joining.** Once the host is out of combat, it sends its save file to the guest;
the guest loads it, then switches to mirror mode. A guest connecting during
combat waits for combat to end. This avoids syncing the whole world from nothing.

## Milestones

Each one ends in something you can see working.

| # | Goal | Done when |
|---|---|---|
| 0 | Tooling | Game launches with BepInEx, a hello-world plugin logs a line; `Assembly-CSharp` is decompiled to a readable source tree |
| 1 | Code survey | Short notes on how party selection, movement orders, pause, scene loading, saves and dialogue are driven, with the exact methods to patch |
| 2 | Connect | Two instances connect by IP, show each other in an overlay, exchange chat |
| 3 | Save handoff | Guest receives the host's save and loads into the same scene |
| 4 | Movement mirror | Host moves the party; guest sees it. Guest simulation is frozen |
| 5 | Guest commands | Guest orders their assigned characters; host executes; ownership rules enforced |
| 6 | Combat (acceptance scope complete) | Turn-based primary: complete ordered snapshots/settings, native presentation-event reconstruction, current-turn tokens, Finish/Delay/previews, actions/movement, long casts and interrupts, projectiles, summons, death/revival, mode switching and combat-deferred late joining. RTwP secondary: attacks, abilities, health, status effects, death and combat logs replicate within the documented scope |
| 7 | Shared flow (acceptance scope complete) | Correlated world readiness and save-backed area transitions; shared time/pause with private local UI; one dialogue picker with host-only scripts; atomic rest transactions; shared loot with viewer-aware close; item registry, inventory and equipment with runtime-ID persistence |
| 8 | Steam transport (implementation complete; acceptance pending) | Invite through Steam friends with no IP typing, then pass real two-account friend-invite, P2P, NAT and relay acceptance |
| 9 | Hardening | Reconnect, desync detection with resync from save, version check |

Milestones 0 to 5 are the first playable. Milestone 6 is the largest single piece:
the ability and status-effect system is where most of the game's complexity lives.

Milestone 7 final verification used the standard build (**0 warnings, 0 errors**)
and passed `.dev/test-m7-flow-final/result.json` (**133/133** checks),
`.dev/test-m7-tactical-final/result.json` (**65/65**) and
`.dev/test-m7-rtwp-final/result.json` (core **56/56**, crossbow **26/26**).
The detailed acceptance scope, fixes and remaining limits are in
`notes/code-survey.md`.

Milestone 8 implementation and release verification used plugin **0.9.0**,
wire **protocol 7** and a build with **0 warnings, 0 errors**. The release is
`dist/PoECoop-0.9.0.zip`, with its SHA-256 file. Verification passed **49/49**
Direct IP regression checks, **19/19** real Steam API one-account lobby checks
and **14/14** deterministic fake-backend checks, including a lossless, ordered
**256 MiB** save-sized burst. Proton Experimental validated the BepInEx
chainloader and a direct host/guest handoff with real F10, Move, Stop and shared
pause/resume input. None of these results is a real two-account Steam session;
friend invites, P2P transport, NAT traversal and relay behavior still require
acceptance before milestone 8 can be called fully verified.

## Risks

- **Combat parity and late joins.** Turn-based is now the primary mode and RTwP
  is secondary, but neither mode claims exhaustive exotic-effect or
  class-resource parity. Save handoff still waits for combat to end; joining
  guests cannot yet take over a live mid-combat mirror.
- **Steam acceptance.** The fake backend and one-account lobby smoke test do not
  exercise a second Steam account. Real friend delivery/acceptance, P2P data,
  NAT traversal and relay fallback can still reveal transport or lifecycle bugs.
- **Game updates.** The game is still being patched (build id is recent). Harmony
  patches target method names, so an update can break them. Keep patches few and
  listed in one place.
- **Shared-flow coverage.** Optional same-map and world-map travel are not fully
  accepted. General stronghold parity and exotic item cases are not exhaustive;
  milestone 7 covers the tested transactions and persistence paths.
- **Scripted content.** Cutscenes, scripted interactions and quest triggers each
  fire game scripts on the host. Mirroring them on the guest is a long tail of
  special cases; expect per-area fixes. Dialogue flow scripts are host-only, but
  scripted content as a whole is not exhaustively covered.
- **UI.** Inventory, equipment, dialogue, rest and loot now use shared-flow
  commands and host state. Other local screens such as level-up and stores still
  need individual policies so they cannot mutate only the guest.

## Setup on the other PC

1. Install Pillars of Eternity from Steam and note the OS and build. For the
   recommended Proton path, select **Proton Experimental** and launch the game
   once so Steam creates its prefix.
2. To play, extract `PoECoop-0.9.0.zip` and follow `INSTALL.md`. Run
   `install-poecoop.sh` on Linux/Proton or `install-poecoop.bat` on Windows.
   The installers detect standard Steam locations, preserve saves, BepInEx
   configuration and unrelated plugins, back up replaced files, and are safe to
   rerun. On Proton they configure BepInEx's `winhttp` override in the existing
   prefix; no Wine configuration tool or manual DLL override is needed.
3. **Direct IP** needs no Steam launch options on Proton or Windows: launch
   normally from Steam, or use `./launch-poecoop.sh`. **Steam mode** requires
   `-steam`, either through `./launch-poecoop.sh -steam` or the platform's Steam
   launch options. Native Linux launching through Steam also needs the BepInEx
   wrapper documented in `INSTALL.md`.
4. To build a release, install a .NET SDK (8 or newer) and `zip`, then run
   `./scripts/build-release.sh`. This creates `dist/PoECoop-VERSION.zip` and its
   SHA-256 file without deploying into the real game.
5. For code surveying, install ILSpy's command-line tool:
   `dotnet tool install -g ilspycmd`
6. Decompile for reading (keep the output out of any public repository, it is
   Obsidian's code):
   `ilspycmd -p -o decompiled "<game>/PillarsOfEternity_Data/Managed/Assembly-CSharp.dll"`
7. Mod project: a C# class library targeting `net472`, referencing
   `Assembly-CSharp.dll`, `UnityEngine*.dll` and the BepInEx and Harmony DLLs from
   the game folder. Output goes to `<game>/BepInEx/plugins/PoECoop/`.

## Suggested repository layout

```
poe-coop/
  PLAN.md
  src/PoECoop/
    Plugin.cs            BepInEx entry point
    Net/                 transport interface, direct and Steam implementations, messages
    Sync/                entity ids, state snapshots, command handling
    Patches/             all Harmony patches, one file per game system
    UI/                  connect overlay, player list, ownership assignment
  notes/                 code survey findings (method names, not decompiled code)
```
