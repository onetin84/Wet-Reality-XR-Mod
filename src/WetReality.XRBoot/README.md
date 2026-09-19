# Wet Reality XR Boot

Starts Unity's OpenXR provider inside a shipped PWS2 build that was never built with XR support.

**This mod does nothing until you press a key.** A failed or half-finished OpenXR session can hang a frame or wedge the renderer, and that must never happen to someone who merely launched the game.

| Key | Action |
|---|---|
| `F8` | Run the OpenXR initialize and start sequence |
| `F6` | End the session and unload the loader library |

Rebindable under `[WetRealityXRBoot]` in `UserData/MelonPreferences.cfg`. `F7` belongs to UnityExplorer, `F9`–`F11` to `WetReality.Discovery`.

Status appears on screen below Discovery's box, showing the phase, `XRSettings.enabled`, the loaded device name and the last native event. The full step-by-step trace goes to `MelonLoader/Latest.log`.

## Why this is possible

PWS2 ships FuturLab's complete XR game layer and its VR content, but no XR provider — see design document sections 37 and 39. The obvious conclusion was that a provider cannot be retrofitted, because `Unity.XR.Management` and `Unity.XR.OpenXR` are managed C# and would have to be inside `GameAssembly.dll`.

That conclusion was too pessimistic. It holds for the managed **orchestration**, not for the native **registration**:

1. Unity reads `Data/UnitySubsystems/<library>/UnitySubsystemsManifest.json` at startup and registers the declared subsystem descriptors. This is data driven and happens before any melon runs.
2. `UnityEngine.VRModule`, `UnityEngine.XRModule` and `UnityEngine.SubsystemsModule` all ship in this build, so `XRSettings`, `XRDisplaySubsystem` and the descriptor store are reachable.
3. Every call Unity's managed loader makes into the provider is a plain P/Invoke, and a melon is ordinary managed code with full P/Invoke.

So the mod takes over the loader role. Staging the files alone already produced both descriptors and made `XRSettings.supportedDevices` report them — design document section 40.

## Required staging

Not part of this mod. Three files from `com.unity.xr.openxr@1.18.0`, the stable version for Unity 6000.0:

```
PowerWash Simulator 2_Data/Plugins/x86_64/UnityOpenXR.dll
PowerWash Simulator 2_Data/Plugins/x86_64/openxr_loader.dll
PowerWash Simulator 2_Data/UnitySubsystems/UnityOpenXR/UnitySubsystemsManifest.json
```

Recorded in `tools/openxr-staged-files.json`; removal means deleting those three files. Without them `F8` fails at `main_LoadOpenXRLibrary`.

## The sequence

Transcribed from `OpenXRLoader.InitializeInternal` and `StartInternal` in the package source. Unity packages ship C#, not compiled assemblies, so the loader is readable.

```
INITIALIZE                                  START
  session_SetSuccessfullyInitialized(false)   session_CreateSessionIfNeeded
  main_LoadOpenXRLibrary(wide path)           wait for XrReady
  session_InitializeSession                   XRDisplaySubsystem.Start
  NativeConfig_SetCallbacks                   session_BeginSession
  NativeConfig_SetApplicationInfo             XRInputSubsystem.Start
  create "OpenXR Display"
  create "OpenXR Input"
```

Display starts before input, and that order is not cosmetic: input needs the session object the display creates.

**Deliberately skipped**, because they exist only to serve Unity's editor and feature infrastructure, which is absent here: `OpenXRFeature.Initialize`, `HookGetInstanceProcAddr`, `RequestOpenXRFeatures`, `OpenXRSettings.ApplySettings`, analytics, diagnostic report.

If initialization fails, add them back in that order. `HookGetInstanceProcAddr` is the likeliest culprit — it installs an `xrGetInstanceProcAddr` interceptor, and its absence would surface at `session_InitializeSession`.

One oddity is reproduced rather than fixed: Unity sets the application info **after** initializing the session, which looks wrong for OpenXR where the application name belongs to `xrCreateInstance`. It is the order that demonstrably works in shipped builds. If the runtime turns out to ignore the name, moving that call earlier is the first thing to try.

## Testing without a headset

The package ships a mock runtime. Point `LoaderName` in `MelonPreferences.cfg` at `Runtime/MockRuntime/windows/x64/openxr_loader.dll` from the extracted package to separate "the sequence is wrong" from "VDXR is not cooperating".

On this machine the active OpenXR runtime is already VDXR:

```
HKLM\SOFTWARE\Khronos\OpenXR\1\ActiveRuntime
  → C:\Program Files\Virtual Desktop Streamer\OpenXR\virtualdesktop-openxr.json
```

## What success and failure look like

Reaching phase `Running` with `XRSettings.enabled == true` proves the provider starts. It does **not** prove the game renders in stereo — the shipped URP must still contain its XR render passes, and IL2CPP managed stripping may have removed them. That is risk R1 in design document section 40 and the largest remaining unknown.

The log line worth watching most closely is the last one:

```
XRBackend.IsEnabled  <true|false>
```

`XRBackend.IsEnabled` is computed and read only, and `XRSDKConditional` exposes `OpenXRRunning` and `WarnIfXRNotInitialized`, which suggests the gate asks whether an XR runtime is actually running. If it flips to `true`, PWS2's own VR layer — input bindings, locomotion, VR hands, haptics — becomes reachable. If it stays `false` with XR running, the gate is build data and that hypothesis is dead.

## Build

```
dotnet build -c Release
```

SDK 6.0.424 pinned via `global.json`, NuGet off, all references into the game folder.
