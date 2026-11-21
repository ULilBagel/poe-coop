# Downloaded dependencies

Official BepInEx 5.4.23.6 Windows and Linux x64 archives are downloaded into this directory by:

```sh
./scripts/fetch-dependencies.sh
```

The archives are intentionally not committed. The release build packages these official archives with `PoECoop.dll` so end users do not need a .NET SDK or an internet connection during installation.

`bepinex-unpacked/`, `bepinex-releases.json`, and `dotnet-install.sh` are local development files and are also excluded by `.gitignore`.
