# Wet Reality Discovery

Read-only diagnostic mod for PWS2. It answered the central reverse engineering question of the design document, section 17 — **which transform carries the relative washer aim offset, and where does PWS2 pull the camera along?** — in three keypresses. It modifies no game state.

Results are recorded in design document sections 34 and 35.

## Hotkeys

| Key | Action |
|---|---|
| `F9` | Record a baseline pose for every transform |
| `F10` | Compare against the baseline, write a report, and make the new snapshot the next baseline |
| `F11` | Schedule a hierarchy dump (default 3 s later, see below) |

Rebindable under `[WetRealityDiscovery]` in `UserData/MelonPreferences.cfg`. `F7` belongs to UnityExplorer.

Feedback appears **on screen**, top left, via `OnGUI` — MelonLoader forwards it to melons, so no injected behaviour is needed. The MelonLoader console is enabled but sits behind the game in fullscreen, which makes it useless mid-test. **If the status box is absent, the mod is not loaded** — check `MelonLoader/Latest.log` for the `N Mods loaded` line rather than assuming a hotkey failed.

## The aim test

Because `F10` re-baselines, the test walks in stages without touching the keyboard in between.

1. Stand still, look straight ahead. `F9`.
2. Swing the washer **inside** the free range. `F10`. → the transforms carrying the relative aim.
3. Keep aiming until the camera follows. `F10`. → what appears now is the camera follow path.
4. Tap `LT` for free aim mode and repeat, to separate that mode.

Reports land in `UserData/WetReality/comparison-NN.txt`. Each entry gives the rotation delta in degrees, the position delta, and local euler, local position and world position before and after.

### Watched transforms

These four are reported **whether they changed or not**:

```
HeadTurn[0]   PlayerCamera[1]   EquipmentAnchor[2]   Rig_PlayerArmsPivot[3]
```

That matters more than it sounds. `HeadTurn: UNCHANGED` is what proved the camera follows only at the aim limit rather than proportionally from the start — an unchanged transform simply did not appear in the earlier ranking.

### Focus filter

Changes whose path contains `FocusPath` (default `PlayerCharacter`) are reported first and are the only ones logged. Without it the ranking is worthless: in both aim measurements a garden sprinkler and two cat rigs outranked the entire player chain, and the twelve logged lines missed the relevant transforms entirely.

Noise floor: 0.01 degrees, 0.5 mm.

## The hierarchy dump

`F11` **schedules** the dump, by default 3 seconds out, with the status box counting down. A radial menu that stays open only while its button is held cannot be captured by a key pressed at the same moment; press `F11`, then open and hold the menu. Set `DumpDelaySeconds` to `0` for an immediate dump.

The dump covers much of design document section 22: active render pipeline via `currentRenderPipeline` (URP confirmed rather than assumed), graphics device, and for every camera its depth, FOV, clip planes, clear flags, culling mask, target texture, `stereoTargetEye`, HDR and MSAA, plus the full ancestor chain with each level's local pose and component list. Canvases are listed with `renderMode`, sorting order, plane distance and world camera.

It also reports:

- **UI components** — everything matching `UIDocument`, `PanelSettings`, `UIElements`, `Canvas`, `Graphic`, `Image`, `Text`, `Button`, `Selectable`, `EventSystem`, `InputModule`, `Raycaster`, `UIRenderer`, `Panel`. This exists to settle whether PWS2 builds its HUD with uGUI or UI Toolkit — the scene contains exactly one Canvas, a WorldSpace nameplate, so searching for Canvases alone cannot answer it.
- **Component census** — every component type in the scene with a count, descending. Useful well beyond the UI question: it is the shortest route to the component that rewrites `EquipmentAnchor` each frame.

Component names are read from the il2cpp class, not via `GetType`. Components coming out of an array are wrapped as their declared type and would all report `UnityEngine.Component`; reading the class also means detecting a type needs **no reference** to the assembly declaring it.

## Coverage limits

Snapshots are keyed by il2cpp object pointer, not by path. Paths are not unique even with a sibling index: an early version keyed on them and silently dropped 1946 of 6166 transforms. The report prints both the walked count and the distinct-path count so this stays visible.

Traversal starts at the roots of every loaded scene plus the root of every entry in `Camera.allCameras`, covering hierarchies parked in `DontDestroyOnLoad`. Objects in neither are not walked. `Camera.allCameras` reports only enabled cameras; disabled ones are still found by the scene walk.

Hotkeys use legacy `UnityEngine.Input`, which works in this build. Exceptions from `OnUpdate` are logged only when the message changes, since a per-frame failure would otherwise bury the log.

## Build and install

```
dotnet build -c Release
```

Copy `bin/Release/net6.0/WetReality.Discovery.dll` into the game's `Mods` folder. **PWS2 locks the DLL while running**, so close the game first, and confirm from the log that the intended version loaded.

## Version history

| | |
|---|---|
| 0.1.0 | Built, never loaded. |
| 0.2.0 | On-screen status via `OnGUI`. Confirmed at runtime: hotkeys respond, legacy input works. |
| 0.3.0 | Pointer-keyed snapshots, watched list, focus filter. Produced measurements 3 and 4. |
| 0.4.0 | Deferred dump and the UI probe plus component census. **Built, not yet run.** |
