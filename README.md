# PoE Co-op

Host-authoritative co-op for **Pillars of Eternity**, with **turn-based combat as the primary, most polished mode** and real-time-with-pause supported as a secondary mode.

The mod runs on native Windows, native Linux, and the Windows game through Proton. It includes split-party ownership, combat and tactical turns, shared dialogue and rest, save handoff, loot, inventory, direct-IP networking, and Steam lobby/P2P transport.

## Install from a release

1. Download and extract the latest `PoECoop-VERSION.zip` release.
2. Install Pillars of Eternity through Steam. For Proton, select **Proton Experimental** in the game's **Properties → Compatibility** and launch once.
3. Run the installer:
   - Proton or Linux: `./install-poecoop.sh`
   - Windows: double-click `install-poecoop.bat`
4. For Proton, add `WINEDLLOVERRIDES="winhttp=n,b" %command%` under **Properties → General → Launch Options**. Then start the game through Steam and press **F10**.

The installer preserves saves, configuration, and unrelated plugins. Replaced files are backed up under `.poecoop-backups` in the game folder. Reinstalling is safe. It does not edit `user.reg` or Steam settings.

For **Steam mode**, add `-steam` to the Proton launch option, or use `./launch-poecoop.sh -steam`.

See [INSTALL.md](INSTALL.md) for complete installation and launch instructions.

## Build from source

Requirements: .NET SDK 8 or newer, `curl`, `unzip`, and `zip`.

```sh
./scripts/fetch-dependencies.sh
./scripts/build-release.sh
```

The release archive is created at `dist/PoECoop-VERSION.zip`. No decompiled game files are required to build the mod; the game assemblies are referenced from the existing Steam installation.

To use a nonstandard game folder:

```sh
dotnet build src/PoECoop/PoECoop.csproj -c Release -p:GameDir="/path/to/Pillars of Eternity"
```

## Steam acceptance status

Direct IP and Proton installation are live-tested. Steam lobby creation and transport behavior are covered by one-account API tests and deterministic transport tests. Real two-account friend invitations, P2P gameplay, NAT traversal, and relay behavior still require acceptance testing with two distinct Steam accounts.

## Repository contents

Only mod source, installers, launcher, build scripts, and documentation belong here. The decompiled game, local save files, game instances, logs, test output, and downloaded dependency archives are intentionally excluded.

Milestones and verification evidence are documented in [PLAN.md](PLAN.md) and [notes/code-survey.md](notes/code-survey.md).
