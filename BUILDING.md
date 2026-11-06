# Building ShreddedBots from source

ShreddedBots is a small launcher (`ShreddedBots.exe`) that starts Teenage Mutant Ninja Turtles: Shredder's Revenge
in-process and applies runtime patches with [Harmony](https://github.com/pardeike/Harmony). The game files are never
modified. This repository contains only the mod's own source code. It does **not** contain any game code, game
assemblies or assets.

## Requirements

- Windows 10/11 (x64). The resulting exe targets .NET Framework 4.5.2 (`net452`), which is part of Windows 10/11.
- [.NET SDK 8.0 or newer](https://dotnet.microsoft.com/download), or Visual Studio 2022 with the ".NET desktop development" workload.
  The .NET Framework reference assemblies come from NuGet (`Microsoft.NETFramework.ReferenceAssemblies`), so no
  targeting pack needs to be installed.
- Internet access for the first build (NuGet restore: `Lib.Harmony` 2.3.3 and the reference assemblies).
- **Your own legitimate copy of TMNT: Shredder's Revenge** (Steam). The mod compiles against the game's
  `TMNT.exe`, `ParisEngine.dll`, `FNA.dll`, `NBug.dll` and `NLog.dll`. These are only read at compile time and are
  never copied to the output. They are not included in this repository. The release was built against Steam
  build 1.0.0.349. Other builds may fail to compile or may produce a different exe.

## Build

Default Steam location:

```
git clone <this repository's URL>
cd ShreddedBots
dotnet build -c Release
```

The csproj looks for the game in `C:\Program Files (x86)\Steam\steamapps\common\TMNT`.
If your game is somewhere else, set `GameDir` to the folder that contains `TMNT.exe`:

```
dotnet build -c Release -p:GameDir="D:\Games\TMNT"
```

(In Visual Studio: open `ShreddedBots.csproj`, set the `GameDir` property in the csproj or pass it with
`/p:GameDir=...`, choose the Release configuration and build.)

If `TMNT.exe` cannot be found, the build stops with a message that says so.

## Output

`bin\Release\` contains exactly the three files that make up the mod:

| File | What it is |
| --- | --- |
| `ShreddedBots.exe` | the launcher and mod (built from this source) |
| `ShreddedBots.exe.config` | generated runtime config (`supportedRuntime` .NET Framework 4.5.2) |
| `0Harmony.dll` | Harmony 2.3.3 from NuGet (`Lib.Harmony`), unmodified |

The release zip also contains `README.txt` and `THIRD-PARTY-NOTICES.txt`; those are plain text files and are not built.

To install, copy those three files into the game folder (next to `TMNT.exe`) and start the game with
`ShreddedBots.exe`. See the README for details.

## Comparing with the release

The build is deterministic (`Deterministic`, `ContinuousIntegrationBuild`, `PathMap`, no pdb), so building from this
source gives a byte-identical `ShreddedBots.exe` regardless of where you checked it out, as long as the same game
build is used for the reference assemblies. In PowerShell:

```
Get-FileHash bin\Release\ShreddedBots.exe, bin\Release\ShreddedBots.exe.config, bin\Release\0Harmony.dll -Algorithm SHA256
```

Expected SHA-256 for release 0.8:

```
0c768c5be1708a84a12b445347f5aea4ac1435b08a437869c3a34d3784496a33  ShreddedBots.exe
cd01de75bd5626c11bf2c1d9983f87b9f24ccabfe5edf5657df0870c27106ab7  ShreddedBots.exe.config
4c06bb6d9d75785f9b64fd551fc75d54af9d5a5458a43cfb5bf52a5a22b24e6e  0Harmony.dll
```

If the exe hash differs, the usual cause is a different compiler version or a different game build used as
reference. The release `ShreddedBots.exe` was produced with the .NET SDK 8 compiler.

## Notes on what the code does (for reviewers)

- `Loader.cs` / `Mod.cs`: launcher, assembly resolution and starting the game's `Main` in-process.
- `Hooks.cs`, `SelfCheck.cs`, `Diag.cs`: Harmony patch helper (types and methods looked up by name) and a start-up
  check that every hook applied; the mod refuses to start the game if a critical block is missing.
- `Telemetry.cs`: **disables** the game's crash reporting and outbound reporting (NBug, Discord webhook, analytics).
- `OnlineBlock.cs`: blocks online play while the game is started through ShreddedBots. The mod itself makes no network connections.
- `Bot*.cs`, `Boss*.cs`, `Statue*.cs`, `SurvivalBrain.cs`, `TenderizerPizza.cs`, `Throws.cs`, `PitMap.cs`, `Difficulty.cs`:
  the bot AI.
- `Menu.cs`, `Config.cs`, `GamePatches.cs`: the in-game F7 menu and its settings (`ShreddedBots.ini`).
- `SteamFix.cs`: sets the Steam app id environment variables so the game starts when launched outside Steam.
- A few logging methods carry `[Conditional("SB_DEBUG")]`. That symbol is never defined in this project, so those
  calls are compiled out; they are kept so the code stays identical to the release build.

## License

The mod's source code is MIT licensed (see `LICENSE`). Harmony and its bundled components are covered by
`THIRD-PARTY-NOTICES.txt`. TMNT: Shredder's Revenge is not part of this project and is not covered by this license.
