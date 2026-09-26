# Hull Forge for From the Depths

Hull Forge is a standalone Windows utility for designing a hollow *From the Depths* ship hull and exporting a native `.blueprint`. It reads the block catalog from the player's game installation and never attaches to the running game.

## Features

- Start from a historical hull or set dimensions and shape controls directly. Bow, body, stern, deck rise, keel rise, and bulb controls remain editable after choosing a starting shape.
- Build layered side, bottom, and deck armor with independent materials and construction styles.
- Add centerline barbettes, position them on a one-metre ruler, and review placement diagnostics.
- Choose physical slope filling or decorative vertical and horizontal smoothing.
- Preview the resolved hull in Grid or Ocean and export that same result as a `.blueprint`.
- Enable experimental Internal Structures when needed.

Project files, faction/style libraries, modular superstructures, and several research-stage smoothing modes are not part of the normal interface.

## Build and run

Use Windows with the .NET 8 SDK and WPF support. Open `FtdHullGenerator.sln` in Visual Studio 2022 or run:

```powershell
dotnet build .\FtdHullGenerator.sln -c Release
dotnet run --project .\FtdHullGenerator\FtdHullGenerator.csproj -c Release
```

Hull Forge finds a typical Steam installation automatically. If it cannot find one, choose the game folder in the app. The game is required for its block catalog but does not need to be running.

## Test

The self-tests require an installed copy of *From the Depths* on this Windows machine:

```powershell
dotnet run --project .\FtdHullGenerator.SelfTest\FtdHullGenerator.SelfTest.csproj -c Release
dotnet run --project .\FtdHullGenerator.SelfTest\FtdHullGenerator.SelfTest.csproj -c Release -- --profile full
```

The first command runs the fast suite; `--profile full` runs the exhaustive product suite. Hand-corrected fixtures in `FtdHullGenerator.SelfTest/Fixtures` are independent test evidence and should not be regenerated to make a test pass.

## Package

For a local framework-dependent package and launcher shortcut:

```powershell
.\scripts\Publish-Local.ps1 -Launch
```

For a self-contained Windows x64 ZIP and SHA-256 receipt:

```powershell
.\scripts\Publish-Portable.ps1
```

The portable publisher requires a clean committed worktree. Its ZIP includes usage instructions and does not include the game or personal files. The application is unsigned, so Windows security settings may warn about or block its first run.

After a game update, export a small test hull, load it in the vehicle designer, and save it once there before relying on it for a campaign craft.
