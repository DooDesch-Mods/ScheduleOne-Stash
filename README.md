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
| `nudge/Nudge.cs` | Update check - tells the player in the console which of their mods have a newer GitHub release. |
| `build/Nudge.props` | One-line linked-source import for Nudge (also switches the check off for Thunderstore builds). |
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

## Nudge - "your mods are out of date"

Same two steps:

```xml
<Import Project="$(MSBuildThisFileDirectory)../Stash/build/Nudge.props"
        Condition="Exists('$(MSBuildThisFileDirectory)../Stash/build/Nudge.props')" />
```
```csharp
DooDesch.Nudge.Nudge.Watch();   // once, in OnInitializeMelon
```

It checks **every loaded mod**, not just yours. MelonLoader already knows each mod's version and download
link, so any mod that names its repo in `MelonInfo` is covered whether or not it has heard of Nudge:

```csharp
[assembly: MelonInfo(typeof(Core), "MyMod", "1.0.0", "Me", "https://github.com/me/MyMod")]
```

The newest version is the repo's latest GitHub release tag. Outdated mods get one yellow block in the
console; when everything is current it says nothing at all.

```
[Nudge] 2 mods are out of date
[Nudge]   Snitch       1.6.0 -> 1.7.0   https://github.com/DooDesch-Mods/ScheduleOne-Snitch/releases
[Nudge]   Side Hustle  2.2.3 -> 2.3.0   https://github.com/DooDesch-Mods/ScheduleOne-SideHustle/releases
```

One consumer covers a whole install - further copies detect each other and stand down. Answers are cached
in `UserData/DooDesch/Nudge.txt` for six hours, so a big mod list stays inside GitHub's anonymous rate
limit. Players switch it off with `UpdateCheck = false` under `[DooDesch]` in `MelonPreferences.cfg`.

Nudge never downloads or installs anything - it only tells you. Building with `-p:StoreBuild=thunderstore`
compiles the endpoint, the HTTP client and the parser out of the DLL entirely and leaves `Watch()` a no-op.

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
