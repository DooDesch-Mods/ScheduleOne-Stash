# Stash - shared source for DooDesch's Schedule I mods

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de/stash](https://support.doodesch.de/stash).

The **Stash** is the public, MIT-licensed source that several DooDesch Schedule I mods share. It exists so the
actual mod logic lives in the open - some mods (like [FullHouse](https://github.com/DooDesch-Mods/ScheduleOne-FullHouse))
are basically just this shared engine plus packaging, so without the Stash their repo would look empty.

The private build workspace (the decompiled game, the game's managed DLLs, internal design docs) stays private -
none of that is needed to read or edit the source here.

## What's inside

| Path | What |
|------|------|
| `fullhouse/FullHouse.cs` | The FullHouse engine - raises the co-op lobby cap (Steam lobby member limit + the game's `Lobby`/`LobbyInterface`). Shared by the standalone FullHouse mod and embedded into Side Hustle. |
| `build/FullHouse.props` | One-line linked-source import so a host mod compiles the engine in. |
| `tools/copy-game-libs.ps1` | Helper to copy the game/MelonLoader DLLs you need to build, out of your own Schedule I install. |

More shared source will move here over time.

## Using it in a mod

A host mod adds one line to its `.csproj` (after its `<Compile Remove>` guards) and calls the engine once:

```xml
<Import Project="$(MSBuildThisFileDirectory)../Stash/build/FullHouse.props"
        Condition="Exists('$(MSBuildThisFileDirectory)../Stash/build/FullHouse.props')" />
```
```csharp
DooDesch.FullHouse.Lobbies.Install();   // early, e.g. OnInitializeMelon
```

The engine is `internal`, so several mods can each compile it in without a CS0436 clash, and a runtime
single-flight guard makes sure only one loaded copy patches.

## Building / contributing

You need two things to build a mod that uses the Stash:

1. **This repo + the mod's repo**, checked out as siblings (`Stash/` next to e.g. `FullHouse/`).
2. **The game's managed DLLs** (Assembly-CSharp, the Il2Cpp interop assemblies, MelonLoader, Steamworks,
   TextMeshPro). These are game binaries and are NOT redistributed here - grab them from your own Schedule I +
   MelonLoader install:

   ```powershell
   # from the Stash folder, point at your Schedule I install:
   ./tools/copy-game-libs.ps1 -GameRoot "D:\...\steamapps\common\Schedule I"
   # -> fills Stash/lib/il2cpp/{game,melonloader}
   ```

Then build the mod pointing at those libs, e.g.:

```
dotnet build ../FullHouse/FullHouse.csproj -c Release -p:WorkspaceLibPath="<abs path>/Stash/lib"
```

(The mod csprojs default `WorkspaceLibPath` to the maintainer's private lib folder and fall back to
`../Stash/lib`, so once you have run the helper the default just works.)

## License

MIT - see [LICENSE.md](LICENSE.md). Game assets and the game's own DLLs are not included and remain the
property of TVGS.
