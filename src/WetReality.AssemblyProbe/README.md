# Wet Reality Assembly Probe

Offline structural inspection of PWS2. It reads type and member signatures out of the Il2CppInterop assemblies that MelonLoader generates under `MelonLoader/Il2CppAssemblies`, so questions about *structure* no longer need a running game.

It found the single most consequential fact of the project so far: **PWS2 ships FuturLab's complete XR game layer and its VR content bundles, but not the Unity XR runtime provider.** Results are recorded in design document section 37.

## What it can and cannot answer

IL2CPP compiled the game logic to native code. Il2CppInterop regenerates only managed stubs that forward to it, so the assemblies carry **no method bodies**.

| Answerable here | Needs the running game |
|---|---|
| Which component declares which field, and of what type | What a field actually contains |
| Whether a class has `Update` / `LateUpdate` | Which code path writes a transform per frame |
| Method names, parameters, visibility | What a method does |
| Which types exist at all, and in which assembly | Whether an instance exists in the scene |

Use it to pick the Harmony patch target, then confirm behaviour with `WetReality.Discovery` or UnityExplorer.

## Usage

```
dotnet bin/Release/net6.0/WetReality.AssemblyProbe.dll <assembly-or-directory> [options]

  --type <substring>     only types whose name or namespace matches
  --members              list fields, properties and methods
  --member <substring>   only members matching, implies --members
  --base <substring>     only types whose base type matches
```

Matching is case-insensitive. Passing the directory sweeps all 180 assemblies and skips anything unreadable rather than aborting. Without `--type` every type is listed, which is far too much — always filter.

The interop properties mirror the native fields, so `--member "prop "` is the compact way to read a component's field layout, and `--member "method "` its call surface.

### Examples

```bash
PROBE="dotnet bin/Release/net6.0/WetReality.AssemblyProbe.dll"
ASM="F:/SteamLibrary/steamapps/common/PowerWash Simulator 2/MelonLoader/Il2CppAssemblies"

# Field layout of the component that drives look, aim offset and camera follow
$PROBE "$ASM" --type PlayerCameraController --member "prop "

# Every XR type in the build
$PROBE "$ASM" --type XR

# Enum values
$PROBE "$ASM/Il2CppFuturLab.PW2.Core.dll" --type XRLocomotionMode --member field
```

Note that `--member field` on a large class also prints the `NativeFieldInfoPtr_*` / `NativeMethodInfoPtr_*` statics that Il2CppInterop adds. They are noise for most questions, but their mangled names do encode visibility and parameter types, which is occasionally useful when a signature is ambiguous.

## Build

```
dotnet build -c Release
```

SDK 6.0.424 is pinned via `global.json` and NuGet is switched off, same as the other projects here. `System.Reflection.Metadata` ships inside the .NET 6 shared framework, which is what makes this buildable with no package feed and no external decompiler. The language level is therefore C# 10 — raw string literals are not available.
