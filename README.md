# Belmont FastLoad

BepInEx plugin for Castlevania: Belmont's Curse that shortens loading screens and save-mirror travel.

| Setting | Default | Effect |
|---|---|---|
| `AnimationSpeed` | 4 | Loading-screen animation speed (1 = vanilla) |
| `MirrorHoldSpeed` | 4 | Speed of the hold-to-enter-mirror animation (1 = vanilla) |
| `MirrorEnterDelayFrames` | 8 | Crouch frames before mirror entry starts (vanilla 30) |
| `KeepGameMenuLoaded` | false | Experimental: keep the pause menu loaded between levels (~1 s per load) |

If a game update breaks a hook, the affected feature turns itself off and logs why. The game is not modified.

## Requirements

- BepInEx 6 Unity IL2CPP x64 (tested with 6.0.0-be.793)
- Optional: [Belmont UI](https://www.nexusmods.com/castlevaniabelmontscurse/mods/89) for an in-game settings page

## Install

1. Extract into the game folder so you get `<game>\BepInEx\plugins\BelmontFastLoad\BelmontFastLoad.dll`.
2. Start the game once to create `<game>\BepInEx\config\belmontfastload.cfg`.

Change settings in that file, or in the pause menu under **MODS SETTINGS** if Belmont UI is installed.
Check `<game>\BepInEx\LogOutput.log` for the `FastLoad ready` line, which lists the enabled features.

## Build

Requires the .NET SDK and the game with BepInEx installed and run once (to generate `BepInEx\interop`).

```
dotnet build -c Release
```

The DLL is copied to `<game>\BepInEx\plugins\BelmontFastLoad\`. If the game isn't in the default Steam folder, add `-p:GameDir="<game folder>"`.

## Release

Bump the version in `[BepInPlugin]` in `Plugin.cs`, commit and push, then:

```
dotnet build -c Release -t:GitHubRelease
```

This builds `bin\BelmontFastLoad-<version>.zip` (extracts into the game folder) and creates GitHub release `v<version>` with it, using the [GitHub CLI](https://cli.github.com/) (`gh auth login` once). Use `-t:Package` to only build the zip.

`ext\BelmontUI\` holds Belmont UI 0.4.0 (MIT, see its `LICENSE.txt`) for compiling only; it is not shipped with the plugin.

## License

MIT, see [LICENSE](LICENSE).
