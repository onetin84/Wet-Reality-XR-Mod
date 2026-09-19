# Wet Reality XR Mod

A PCVR mod for **PowerWash Simulator 2**. It turns the flat game into a VR
game: the washer follows the controller in six degrees of freedom, gaze and aim
are fully decoupled, and the menus are driven by a pointer ray.

**Status:** beta. A tester package is under [Releases](../../releases).
*Deutsche Kurzfassung [weiter unten](#deutsch).*

| | |
|---|---|
| Target | Meta Quest 3 over OpenXR + Virtual Desktop (VDXR) |
| Base | MelonLoader, IL2CPP, x64 |
| Game | PowerWash Simulator 2 (Steam, AppID 2968420) |

## What it does

- **6DOF washer.** The dominant controller's pose overwrites the washer's
  transform. No mouse emulation — the game's own aim chain is redirected at the
  point of use.
- **Two VR hands.** The game's own hand models are attached to the controllers,
  one at the washer grip and one free for reaching. The game's default geometry
  stays hidden: it is a single mesh spanning both arms, anchored to the head,
  and unusable in VR.
- **Body-zone gestures.** Grip at the shoulder switches the tool, at the hip
  the nozzle group, and the free hand at the washer switches the extension. All
  three raise the game's existing code path — no reimplemented tool logic.
- **Haptics.** A sustained pulse while spraying, shaped by nozzle and detergent,
  plus event pulses. The two-handed gesture pulses both hands.
- **6DOF head.** Standing up and leaning move the viewpoint. When the runtime
  recentres (META button), the mod follows and restores neutral eye height.
- **Configurator.** WPF through PowerShell 5.1, nothing compiled, bilingual,
  with a quick guide and a button that launches the game through Steam.

## Layout

    src/WetReality.Pose            the mod: pose, input, gestures, haptics,
                                   VR hands, menu pointer
    src/WetReality.XRBoot          OpenXR session, input actions, haptic output
    src/WetReality.Discovery       runtime exploration probes
    src/WetReality.ExplorerCompat  bridge for UnityExplorer under Unity 6
    tools/frontend                 configurator and quick guides
    tools/package                  package builder for releases

**Deliberately not in this repository:** the built packages (they belong in
Releases), third-party binaries and third-party source under their own
licences, the internal engineering log, the measurement data and the patch
scripts.

## Building

The projects pin their SDK in `global.json` to **6.0.424** with
`rollForward: disable`. MSBuild looks for `global.json` relative to the
**current directory**, not the project file — so the build has to run **from
the project directory**:

```
cd src/WetReality.Pose
dotnet build -c Release
```

From the repository root, `dotnet` picks a newer SDK and asks for targeting
packs that are not installed (`NU1100`).

The projects compile against the assemblies of the **installed game**. The
default in the `.csproj` is Steam's standard location. If the game lives
elsewhere, there are two ways — once per machine:

```
cp Directory.Build.props.example Directory.Build.props
```

Put the path in there. That copy is **not tracked**; MSBuild finds it by
searching upward from the project directory and imports it before the project
body, so the `GamePath` set there wins. Or per invocation:

```
dotnet build -c Release -p:GamePath="D:\SteamLibrary\steamapps\common\PowerWash Simulator 2"
```

If the path is wrong, the build stops with **one** line instead of hundreds of
compiler errors about missing types. `Directory.Build.targets` checks first and
names the cause: `WR0001` no game folder, `WR0002` MelonLoader missing inside
it, `WR0003` the Il2Cpp assemblies missing — those are generated the first time
the game starts with MelonLoader.

A package is built by `tools/package/Make-Package.ps1`. The version is read out
of the built `WetReality.Pose.dll`, so the folder name, the readme and the
guide cannot disagree with the DLL a tester actually receives.

## Line endings here are measurements

`.gitattributes` sets `* -text`. That is not style, it is necessity: files in
this project deliberately carry different encodings — `Pose.cs` is CRLF with
BOM, `GameInput.cs` is LF without, `Strings.ps1` is CRLF with BOM,
`WetReality-Config.ps1` is LF without. Every patch script in the project reads
that encoding and **aborts** if it does not match, which has caught real
mistakes before they shipped. If Git normalised on checkout, a clone would
carry different bytes than the original — silently, because the code still
compiles.

## Known pitfall: UnityExplorer

With **UnityExplorer** in the `Mods` folder, the game's menu selection is
broken: highlights drift, preview images stay empty or frozen, the ring is
missing. The cause is not this mod but UniverseLib — it patches
`EventSystem.SetSelectedGameObject` and the setter of `EventSystem.current`,
and rejects every selection change, the game's own included. Take UnityExplorer
out of the folder to play.

## License

[MIT](LICENSE), for this project's own code.

MelonLoader, UnityExplorer and OpenXR keep their own licences. They are not in
this repository but fetched; `tools/downloads` and `tools/source-inspection`
are excluded for that reason.

---

## Deutsch

PCVR-Mod für **PowerWash Simulator 2**. Sie macht aus dem Flachspiel ein
VR-Spiel: die Waschpistole folgt dem Controller in sechs Freiheitsgraden,
Blickrichtung und Schussrichtung sind entkoppelt, und die Menüs werden mit
einem Zeigestrahl bedient. Ziel ist Meta Quest 3 über OpenXR und Virtual
Desktop, Grundlage ist MelonLoader unter IL2CPP.

**Spielen:** das Paket unter [Releases](../../releases) entpacken,
`Install.cmd` doppelklicken, danach `Configurator.cmd` für die Einstellungen.
Dort steht auch die deutsche Kurzanleitung, und der Knopf unten startet das
Spiel. Entfernen: `Uninstall.cmd`.

**Enthalten:** 6DOF-Pistole ohne Mausemulation, zwei VR-Hände an den
Controllern, Körperzonen-Gesten für Gerät, Düsengruppe und Verlängerung,
Haptik beim Sprühen, 6DOF-Kopfhaltung samt Nachziehen der Augenhöhe nach einer
Neukalibrierung, und ein zweisprachiges Konfigurationswerkzeug.

**Bauen:** aus dem **Projektverzeichnis** heraus, nicht aus der Wurzel — die
`global.json` pinnt das SDK auf 6.0.424 und wird vom aktuellen Verzeichnis aus
gesucht. Liegt das Spiel nicht am Steam-Standardort, den Pfad in
`Directory.Build.props` eintragen (Vorlage liegt daneben, die Kopie wird nicht
versioniert) oder mit `-p:GamePath` übergeben. Stimmt er nicht, bricht der
Build mit einer Zeile ab — `WR0001` bis `WR0003` — statt mit hunderten
Compilerfehlern.

**Wichtig:** liegt **UnityExplorer** im `Mods`-Ordner, ist die Menü-Auswahl des
Spiels kaputt — Highlights wandern, Vorschaubilder bleiben leer. Das ist nicht
die Mod, sondern UniverseLib: es patcht `EventSystem.SetSelectedGameObject` und
weist jede Auswahländerung ab, auch die des Spiels. Zum Spielen UnityExplorer
aus dem Ordner nehmen.

**Zeilenenden sind hier Messwerte.** `.gitattributes` setzt `* -text`, weil die
Dateien absichtlich unterschiedliche Kodierungen tragen und jedes Patchskript
sie prüft. Eine Normalisierung durch Git wäre lautlos und würde jede
Ankerprüfung entwerten.

**Lizenz:** [MIT](LICENSE) für den Code dieses Projekts. MelonLoader,
UnityExplorer und OpenXR behalten ihre eigenen Lizenzen.
