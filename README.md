# Wet Reality XR Mod

PCVR-Mod für **PowerWash Simulator 2**. Sie macht aus dem Flachspiel ein
VR-Spiel: die Waschpistole folgt dem Controller in sechs Freiheitsgraden,
Blickrichtung und Schussrichtung sind entkoppelt, und die Menüs werden mit
einem Zeigestrahl bedient.

**Stand:** Beta. Ein Tester-Paket liegt unter [Releases](../../releases).

| | |
|---|---|
| Ziel | Meta Quest 3 über OpenXR + Virtual Desktop (VDXR) |
| Grundlage | MelonLoader, IL2CPP, x64 |
| Spiel | PowerWash Simulator 2 (Steam, AppID 2968420) |

## Was drin ist

- **6DOF-Pistole.** Die Pose des dominanten Controllers überschreibt die
  Transformation der Waschpistole. Keine Mausemulation.
- **Zwei VR-Hände.** Die Handmodelle des Spiels hängen an den Controllern —
  eine am Pistolengriff, eine frei zum Greifen. Die Vorgabegeometrie des Spiels
  (ein Mesh über beide Arme, am Kopf verankert) bleibt aus.
- **Körperzonen-Gesten.** Griff an die Schulter wechselt das Gerät, an die
  Hüfte die Düsengruppe, die freie Hand an die Pistole die Verlängerung. Alle
  drei lösen den vorhandenen Spielpfad aus, keine nachgebaute Logik.
- **Haptik.** Dauerpuls beim Sprühen, abhängig von Düse und Reiniger, dazu
  Ereignispulse; bei der zweihändigen Geste pulsen beide Hände.
- **6DOF-Kopf.** Aufstehen und Lehnen bewegen den Blickpunkt. Kalibriert die
  Laufzeitumgebung neu (META-Taste), zieht die Mod die Augenhöhe nach.
- **Konfigurationswerkzeug.** WPF über PowerShell 5.1, nichts kompiliert,
  zweisprachig, mit Kurzanleitung und Startknopf für das Spiel.

## Aufbau

    src/WetReality.Pose            die Mod: Pose, Eingabe, Gesten, Haptik,
                                   VR-Hände, Menüzeiger
    src/WetReality.XRBoot          OpenXR-Session, Eingabe-Actions,
                                   haptischer Ausgang
    src/WetReality.Discovery       Sonden zur Laufzeit-Erkundung
    src/WetReality.ExplorerCompat  Brücke für UnityExplorer unter Unity 6
    tools/frontend                 Konfigurationswerkzeug und Kurzanleitungen
    tools/package                  Paketbau für die Auslieferung

**Nicht im Repository**, bewusst: die fertigen Pakete (gehören in Releases),
fremde Binärpakete und fremder Quellcode mit eigener Lizenz, das interne
Arbeitsprotokoll, die Messdaten und die Patchskripte.

## Bauen

Die Projekte pinnen ihr SDK in `global.json` auf **6.0.424** mit
`rollForward: disable`. `global.json` wird vom **aktuellen Verzeichnis** aus
gesucht, nicht vom Projektpfad — deshalb muss der Build **aus dem
Projektverzeichnis** laufen:

```
cd src/WetReality.Pose
dotnet build -c Release
```

Vom Wurzelverzeichnis aus zieht `dotnet` ein neueres SDK und verlangt
Targeting-Packs, die nicht installiert sind (`NU1100`).

Die Projekte verweisen auf die Assemblies des installierten Spiels. Der Pfad
steht als `GamePath` in der `.csproj` und ist überschreibbar:

```
dotnet build -c Release -p:GamePath="D:\SteamLibrary\steamapps\common\PowerWash Simulator 2"
```

Ein Paket baut `tools/package/Make-Package.ps1`. Die Version wird aus der
gebauten `WetReality.Pose.dll` gelesen, damit Ordnername, Readme und Anleitung
nicht mit der DLL auseinanderlaufen können.

## Zeilenenden sind hier Messwerte

`.gitattributes` setzt `* -text`. Das ist kein Stil, sondern Notwendigkeit: die
Dateien dieses Projekts haben absichtlich unterschiedliche Kodierungen —
`Pose.cs` ist CRLF mit BOM, `GameInput.cs` ist LF ohne BOM, `Strings.ps1` ist
CRLF mit BOM, `WetReality-Config.ps1` ist LF ohne BOM. Jedes Patchskript prüft
das und bricht ab, wenn es nicht stimmt. Würde Git beim Auschecken
normalisieren, trüge ein Klon andere Bytes als das Original — lautlos, denn der
Code kompiliert weiter.

## Bekannte Stolperstelle: UnityExplorer

Liegt **UnityExplorer** im `Mods`-Ordner, ist die Menü-Auswahl des Spiels
kaputt: Highlights wandern, Vorschaubilder bleiben leer oder stehen still, der
Ring fehlt. Ursache ist nicht die Mod, sondern UniverseLib — es patcht
`EventSystem.SetSelectedGameObject` und den Setter von `EventSystem.current`
und weist jede Auswahländerung ab, auch die des Spiels. Zum Spielen
UnityExplorer aus dem Ordner nehmen.

## Lizenz

Noch keine gewählt. Ohne Lizenzdatei gilt „alle Rechte vorbehalten"; für ein
privates Repository ist das in Ordnung, vor einer Veröffentlichung wäre es eine
Entscheidung.
