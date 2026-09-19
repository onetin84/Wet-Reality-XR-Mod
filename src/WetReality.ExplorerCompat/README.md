# Wet Reality Explorer Compatibility

Diagnostic mod that makes UnityExplorer 4.13.6 (UniverseLib 1.6.2) usable in PWS2 1.3.0 on Unity 6000.0.73f1 with MelonLoader 0.7.3.

## The crash

`UniverseLib.AssetBundle.LoadFromMemory` resolves the icall `UnityEngine.AssetBundle::LoadFromMemory_Internal` and invokes it as `(IntPtr binary, uint crc)`. On Unity 6 that native function takes `(ref ManagedSpanWrapper, uint)`, so the Il2Cpp array pointer is reinterpreted as a span: `begin` is read from the object's class pointer and `length` from its monitor field. Dereferencing that is the `0xc0000005` access violation recorded in `diagnostics/2026-09-13-first-crash`. The faulting module named in the Windows event, `coreclr.dll`, is not itself defective.

`ICallManager.GetICallUnreliable` cannot recover from this. It builds its `_Injected` candidate list with `loopSig.Concat(...)` and discards the result, so only the two literal signatures are ever tried. The single-signature `GetICall` appends the suffix correctly. UnityExplorer 4.13.6 (2026-04-30) is the newest release and its "Fixed AssetBundle injected" change covers `LoadFromFile` and `LoadAsset`, not `LoadFromMemory`, so there is no upstream fix.

## The workaround

PWS2 does not strip the `AssetBundle` type, so MelonLoader generated a full interop wrapper for it. A Harmony prefix on `UniverseLib.AssetBundle.LoadFromMemory(byte[], uint)` forwards to those generated bindings, which marshal the span and unmarshal the returned handle using code generated from this build of `GameAssembly.dll`. No struct layout, calling convention or GC handle representation is assumed here. The mod loads before UnityExplorer via `MelonPriority(-1000)` and replaces no game or tool binary.

IL2CPP strips unused methods individually — the loader already reports no native pointer for `AssetBundle.UnloadAllAssetBundles` — so the memory loader is not assumed to have survived. Version 0.3.0 probes which `LoadFrom*` bindings this build kept, then tries the generated `LoadFromMemory` followed by the generated `LoadFromFile` against a copy of the bundle in `UserData/WetReality`. Every outcome is logged, because `UniverseLib.UI.UniversalUI.TryLoadBundle` swallows all exceptions with a bare `catch` and only reports that the bundle is missing.

`ExplorerCompat.cs.v0.1.bak` holds the discarded first attempt, which called the injected function itself through a reconstructed `ManagedSpan` and a guessed GC handle resolution.

## Status

| | |
|---|---|
| 0.2.0, run of 02:12 | Crash resolved. `2 Mods loaded`, prefix installed, `UnityExplorer 4.13.6 (IL2CPP) initialized`, process stable. UI bundle still not loaded — the generated `LoadFromMemory` threw and UniverseLib swallowed it. |
| 0.3.0 | Built, not yet run. Adds the binding probe and the `LoadFromFile` fallback. |

Without the UI bundle UniverseLib falls back to Arial and a substitute shader; whether the explorer interface is adequately usable that way has not been checked.

## Build and install

```
dotnet build -c Release
```

The pinned .NET 6 SDK and every referenced assembly already exist on this machine, so no NuGet packages are needed. Override `GamePath` via MSBuild if the game moves.

Copy only `bin/Release/net6.0/WetReality.ExplorerCompat.dll` into the game's `Mods` folder alongside the enabled UnityExplorer. PWS2 locks the DLL while running, so close the game before copying.

To return to the loader-only comparison, close PWS2 and rename **both** mod DLLs with a `.disabled` suffix. Disabling only this mod leaves the original UnityExplorer crash path active.

Upstream source examined: <https://github.com/yukieiji/UniverseLib/blob/0a84d9eedcd6e497e4b20b1e14716de9314f0712/src/Runtime/Il2Cpp/AssetBundle.cs>
