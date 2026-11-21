# PoE Co-op installation

PoE Co-op supports the Windows version of Pillars of Eternity running through Proton, native Windows, and the native Linux version. All players should use the same PoE Co-op release.

The installer preserves saves, BepInEx configuration, and unrelated plugins. It backs up any file it replaces under `.poecoop-backups` in the game folder. Running it again is safe: unchanged files are left alone.

## Windows through Proton on Linux (recommended)

Proton requires one explicit Steam launch option so Wine loads BepInEx's `winhttp.dll`. The installer does not edit Proton's `user.reg` or any other registry file.

1. In Steam, install **Pillars of Eternity**. Under **Properties → Compatibility**, select **Proton Experimental**. Launch the game once and close it so Steam creates its Proton prefix.
2. Extract the PoE Co-op release zip. Open a terminal in the extracted `PoECoop` folder and run:

   ```sh
   ./install-poecoop.sh
   ```

   Standard Steam, Steam snap, and Flatpak locations are detected automatically. If necessary, supply the folder containing `PillarsOfEternity.exe`:

   ```sh
   ./install-poecoop.sh "/path/to/steamapps/common/Pillars of Eternity"
   ```

3. In Steam, open **Properties → General → Launch Options** and add:

   ```text
   WINEDLLOVERRIDES="winhttp=n,b" %command%
   ```

   For **Steam mode**, use this instead:

   ```text
   WINEDLLOVERRIDES="winhttp=n,b" %command% -steam
   ```

   You can also launch through `./launch-poecoop.sh`, or `./launch-poecoop.sh -steam` for Steam mode. The launcher supplies the same visible Wine override and uses Steam on `PATH`, or `flatpak run com.valvesoftware.Steam` when Steam is installed through Flatpak.

The installer only unpacks the official Windows x64 BepInEx 5.4.23.6 package and `PoECoop.dll` into the game folder. It does not edit `user.reg`, Steam settings, or launch options.

## Native Windows

1. Extract the release zip.
2. Double-click `install-poecoop.bat`. Standard Steam locations are detected automatically; otherwise enter the game folder when prompted. You can also drag the game folder onto the batch file.
3. For Direct IP, launch the game normally from Steam or its desktop shortcut. For Steam mode, add `-steam` as described below. Windows does not need the Proton `WINEDLLOVERRIDES` option.

## Native Linux

Run these commands from the extracted `PoECoop` folder:

```sh
./install-poecoop.sh
./launch-poecoop.sh
```

The launcher starts the native game through BepInEx. Direct IP needs no options; pass `-steam` to `./launch-poecoop.sh` only when using Steam mode.

To launch through Steam instead, set this **native Linux only** launch option for Direct IP:

```text
./run_bepinex.sh %command%
```

For Steam mode, use the same wrapper with `-steam`:

```text
./run_bepinex.sh %command% -steam
```

If both Windows and native Linux binaries are present, pass `--platform windows` or `--platform linux` to the installer and launcher.

## Play with Direct IP (default)

Direct IP is selected by default on a new installation. Existing settings are not changed.

1. Both players launch the game and press **F10** to open the co-op window.
2. The host chooses **Direct IP**, enters a player name, and clicks **Host a game**. The default port is **14242**.
3. The guest chooses **Direct IP**, enters the host's IP address and port, and clicks **Join**.
4. The host loads a save when needed and assigns party members in the co-op window.

On a LAN, use the host's LAN address. Over the internet, the host normally needs to forward **TCP port 14242** to their PC and the guest uses the host's public IP. Only the host needs to forward the port. Firewall prompts must also allow the game to accept connections.

## Optional Steam mode

Steam mode uses Steam lobbies and peer-to-peer networking, so no IP address or port forwarding is needed. It requires the `-steam` game argument; Direct IP does not require that argument. Proton still requires the `WINEDLLOVERRIDES` launch option shown above for both modes.

- **Proton:** launch with `./launch-poecoop.sh -steam`.
- **Native Linux:** launch with `./launch-poecoop.sh -steam`, or use `./run_bepinex.sh %command% -steam` as the Steam launch option.
- **Native Windows:** add `-steam` to the game's Steam launch options.

1. Both players sign in to Steam and launch the modded game with `-steam`.
2. Press **F10** and choose **Steam**.
3. The host clicks **Host Steam lobby**, then invites a friend through the co-op window or the native Steam invite dialog.
4. The guest accepts the invitation through Steam. The host can invite through the co-op window or the native Steam invite dialog.

## Updating or troubleshooting

Extract the new release and run its installer again. Existing `BepInEx/config`, saves, and unrelated plugins are retained; only replaced files receive a new timestamped backup.

To confirm the mod loaded, open `BepInEx/LogOutput.log` in the game folder and look for `PoE Co-op`. If Proton cannot find the game, pass its directory as the first argument to `./install-poecoop.sh`. Run `./install-poecoop.sh --help` for all installer options.

## Building a release

From the source repository, with a .NET SDK and `zip` installed:

```sh
./scripts/build-release.sh
```

This builds the Release configuration and creates `dist/PoECoop-VERSION.zip`. The package contains only the plugin, both official BepInEx archives, installers, launcher, and these instructions. The build's automatic plugin deployment is redirected to a temporary folder, so it does not install into the real game.
