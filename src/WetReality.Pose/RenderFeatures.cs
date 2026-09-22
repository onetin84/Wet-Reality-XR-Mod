using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppFuturLab.PW2;
using Il2CppVLB;
using Il2CppOccaSoftware.Buto.Runtime;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace WetReality;

// DIE EIN-AUGEN-EFFEKTE - Abschnitt 161.
//
// Gemeldet: Nebel und ein Flecken-/Streifeneffekt auf dem Rasen werden nur auf
// dem RECHTEN Auge gezeichnet und irritieren in VR. Der Befund stand lange als
// Verdacht im Dokument (offener Punkt zu Abschnitt 108); die Ursache ist jetzt
// aus den Interop-Assemblies BELEGT.
//
// ============================== WER ZEICHNET
//
// Zwei Fremd-Assets als URP-Renderer-Features:
//
//     Nebel           Buto  RenderFogPass            OccaSoftware
//     Lichtstreuung   LSPP  LightScatteringRenderPass
//
// ============================== UND WARUM GENAU EIN AUGE
//
// stereoRenderingMode ist MULTIPASS (gemessen): die Kamera rendert zweimal pro
// Frame, einmal je Auge. RenderFogPass haelt
//
//     Dictionary<Camera, RTData>  m_keyValuePairs   Puffer pro KAMERA
//     Matrix4x4                   toPreviousView    EINE Vorframe-Matrix
//     ComputeShader               media/lighting/integrator
//
// In beiden Augenpaessen ist die Kamera DASSELBE OBJEKT, also derselbe
// Froxel-Puffer und dieselbe einzige Reprojektionsmatrix - die zwangsläufig zum
// zuletzt gerenderten Auge gehoert. LightScatteringRenderPass hat dieselbe Form
// (ein einziger RTHandle-Satz, kein Augen-Index).
//
// Die Effekte sind nicht "nicht stereo". Sie sind ARCHITEKTONISCH EINAEUGIG.
//
// ============================== WARUM HIER NICHT REPARIERT WIRD
//
// Buto stereo-korrekt zu machen heisst Buto neu zu schreiben: die
// Methodenkoerper sind native IL2CPP, der Fix braeuchte Verdopplung des
// Zustands pro Auge INNERHALB von Execute, und die drei ComputeShader liegen
// als kompilierte Varianten im Build.
//
// Auch geprueft und verworfen: ein Harmony-Detour auf Execute, der den Cache
// zwischen den Augenpaessen leert. Execute nimmt ref RenderingData - eine
// STRUKTUR ueber die Interop-Grenze, was die Projektregel ausschliesst. Er
// liefe zudem auf dasselbe hinaus wie FogTemporal unten, nur mit doppelter
// Allokation pro Frame.
//
// ============================== WAS DIESE KLASSE STATTDESSEN TUT
//
// ScriptableRendererFeature bringt genau die zwei Bausteine mit, die gebraucht
// werden, und beide sind bool - keine Struktur ueberquert die Grenze:
//
//     public void SetActive(bool)     schalten
//     public bool get_isActive()      ZURUECKLESEN
//
// Das Zuruecklesen ist der Punkt. Eine Erfolgsmeldung zaehlt die WIRKUNG, nie
// den Aufruf - in Abschnitt 151 hat genau diese Verwechslung ("mask live"
// zaehlte die Erstellung, nicht die Wirkung) einen ganzen Lauf gekostet.
//
// Der Schalter ist GENERISCH und nicht auf zwei Effekte verdrahtet: fuenf
// Renderer-Features sind im Build, drei davon unzugeordnet. Eine Liste von
// Typnamen heisst, dass der naechste Test keinen neuen Build kostet - die
// Antwort auf "Testlaeufe sind teuer".
//
// Zeilenenden: LF ohne BOM.
internal sealed class RenderFeatures
{
    // Die Feature-Referenz und ihr Typname zusammen. Der Name wird EINMAL
    // gelesen und mitgetragen: er kommt aus dem Il2CPP-Typsystem und ist
    // teurer als ein Stringvergleich, und der Abgleich laeuft pro Frame.
    private readonly List<ScriptableRendererFeature> features = new();
    private readonly List<string> names = new();

    private bool reported;
    private float nextScan;

    // Der letzte angewandte Wunsch. Der Abgleich loggt nur bei AENDERUNG -
    // pro Frame zu melden wuerde das Log fluten und die eine interessante
    // Zeile darin begraben.
    private readonly Dictionary<string, bool> applied = new();

    private float fogTemporal = float.NaN;

    // Die Grasklingen - Abschnitt 162. Eigener Zwischenspeicher, eigener
    // Zeitgeber: ShellTextureGeometry haengt an LEVELGEOMETRIE und wechselt
    // darum mit der Szene, waehrend Renderer-Features Assets der Pipeline sind
    // und bleiben. Zwei Lebensdauern, zwei Speicher.
    private readonly List<ShellTextureGeometry> grass = new();
    private float nextGrassScan;
    private bool grassReported;
    private int wantedFins = -1;
    private int wantedShells = -1;

    // ZWEITER KANDIDAT - Abschnitt 162: Unity-Terrain-Detailgras. Es erklaert
    // die Verteilung im gemeldeten Bild BESSER als die Fins, weil die Klingen
    // auch ueber Mulch und Kies liegen, wo es kein Rasenmesh gibt -
    // Terrain-Detail sitzt unabhaengig von der Splat-Textur auf dem ganzen
    // Terrain.
    //
    // Gegenindiz, offen: der Build enthaelt TerrainToMesh und LinkedTerrain,
    // also moeglicherweise GEBACKENES Terrain ohne Terrain-Instanzen zur
    // Laufzeit. Dann findet dieser Weg nichts - und sagt das.
    private int wantedFoliage = -1;
    private int wantedInstanced = -1;
    private float appliedSeparation = float.NaN;

    // Die Kameraliste - Abschnitt 169. Kameras entstehen und vergehen mit der
    // Szene, darum eine Anzahl als Wechselerkennung statt eines Einmal-Flags.
    private int wantedBeams = -1;
    private int cameraCount;
    private float nextCameraScan;

    // Die Nachbearbeitung - Abschnitt 163. Volume-Komponenten sind
    // ScriptableObjects in Profilen und bleiben wie die Renderer-Features;
    // die Kameradaten haengen an der Kamera und wechseln mit der Szene.
    private readonly List<VolumeComponent> volumes = new();
    private readonly List<string> volumeNames = new();
    private bool volumesReported;
    private float nextVolumeScan;
    private readonly Dictionary<string, bool> volumeApplied = new();
    private int wantedPost = -1;

    // Renderer nach SHADERNAME abschalten - Abschnitt 164. Der grobe, aber
    // entscheidende Gegentest zur Sonde.
    private readonly List<Renderer> shaderHidden = new();
    private string shaderWanted = string.Empty;
    private float nextShaderScan;

    // Materialien und Keywords - Abschnitt 165. Was abgeschaltet wurde, muss
    // zurueckgegeben werden koennen: Material und Keyword als Paar, weil
    // dasselbe Keyword auf vielen Materialien sitzt und dasselbe Material
    // mehrere tragen kann.
    private readonly List<Material> keywordMaterials = new();
    private readonly List<string> keywordNames = new();
    private string keywordWanted = string.Empty;
    private float nextKeywordScan;
    private int inventoryCount;
    private float nextInventoryScan;

    // ====================================================================
    // FINDEN UND ZWISCHENSPEICHERN
    //
    // FindObjectsOfTypeAll und nicht FindObjectsOfType: Letzteres ueberspringt
    // inaktive Objekte, und ein schon abgeschaltetes Feature ist genau das.
    // Es liefert einen Il2CppReferenceArray von Klassenreferenzen - die
    // sichere Interop-Form, dieselbe Wahl und dieselbe Begruendung wie in
    // GameUi.Resolve.
    //
    // Der Sweep ist teuer und laeuft darum NICHT pro Frame, sondern nur wenn
    // eine gemerkte Referenz gestorben ist oder das Intervall abgelaufen ist.
    private bool Resolve(MelonLogger.Instance log, float rescanSeconds)
    {
        if (Alive() && Time.unscaledTime < nextScan)
            return features.Count > 0;

        nextScan = Time.unscaledTime + Mathf.Max(0.5f, rescanSeconds);

        try
        {
            var all = Resources.FindObjectsOfTypeAll(
                Il2CppType.Of<ScriptableRendererFeature>());

            features.Clear();
            names.Clear();

            for (var index = 0; index < all.Length; index++)
            {
                var feature = all[index]?.TryCast<ScriptableRendererFeature>();

                if (feature is null || feature == null)
                    continue;

                features.Add(feature);

                // Der TYPNAME, nicht der Objektname: der Objektname eines
                // Renderer-Features ist der Asset-Name und damit frei
                // vergeben. Auf Typen zu ankern statt auf Asset-Namen ist die
                // Lehre aus "gepinnter Pfad ist ein Namenstest".
                names.Add(feature.GetIl2CppType()?.Name ?? "?");
            }

            return features.Count > 0;
        }
        catch (Exception exception)
        {
            log.Warning("  render features: the sweep threw "
                + exception.GetType().Name + "; leaving them alone");
            features.Clear();
            names.Clear();
            return false;
        }
    }

    // Unity-null sieht kein zerstoertes Objekt, darum BEIDE Pruefungen. Stirbt
    // eine Referenz - etwa weil die Pipeline neu geladen wurde -, ist der
    // ganze Zwischenspeicher wertlos und nicht nur dieser Eintrag.
    private bool Alive()
    {
        if (features.Count == 0)
            return false;

        for (var index = 0; index < features.Count; index++)
        {
            var feature = features[index];

            if (feature is null || feature == null)
                return false;
        }

        return true;
    }

    // ====================================================================
    // DIE ZUORDNUNGSMESSUNG
    //
    // Einmal je Sitzung, und sie ist der eigentliche Zweck des ersten Laufs:
    // drei der fuenf Features sind unzugeordnet, und diese Liste nennt sie mit
    // Namen statt sie zu erraten.
    private void Report(MelonLogger.Instance log)
    {
        if (reported)
            return;

        reported = true;

        log.Msg($"render features: {features.Count} found");

        for (var index = 0; index < features.Count; index++)
        {
            var active = "unreadable";

            try
            {
                active = features[index].isActive ? "YES" : "no";
            }
            catch
            {
                // Unlesbar ist eine eigene Auskunft und kein "nein". Ein
                // fehlender Wert ist kein Lesefehler und umgekehrt.
            }

            log.Msg($"  {names[index],-32} active {active}");
        }
    }

    // ====================================================================
    // DER ABGLEICH
    //
    // Er muss WIEDERHOLT laufen, und das ist gemessen begruendet:
    // FogManager.OnSceneChange und VolumetricFogSettingStrategy
    // .HandleSettingChanged schalten die Features wieder ein. Dasselbe Problem
    // und dieselbe Loesung wie bei GameUi.Reapply - idempotent, pro Frame
    // billig, weil er nur fuenf bool-Eigenschaften liest.
    internal void Apply(MelonLogger.Instance log, string disabled,
        float rescanSeconds)
    {
        if (!Resolve(log, rescanSeconds))
            return;

        Report(log);

        for (var index = 0; index < features.Count; index++)
        {
            var name = names[index];
            var wanted = !Matches(disabled, name);

            try
            {
                var feature = features[index];

                // ERST LESEN. Ein unbedingtes SetActive waere ein Schreibvorgang
                // pro Frame auf ein ScriptableObject und wuerde ausserdem
                // verdecken, ob das Spiel zurueckschaltet.
                if (feature.isActive == wanted)
                {
                    applied[name] = wanted;
                    continue;
                }

                feature.SetActive(wanted);

                // UND DANACH WIEDER LESEN. Gemeldet wird dieser Wert, nicht
                // der Wunsch: bleibt er stehen, hat das Spiel zurueckgeschaltet,
                // und genau das soll im Log unterscheidbar sein.
                var now = feature.isActive;

                if (!applied.TryGetValue(name, out var last) || last != wanted)
                {
                    applied[name] = wanted;

                    log.Msg($"  render feature {name}: isActive now "
                        + (now ? "YES" : "no")
                        + (now == wanted ? "" : "   THE GAME REFUSED"));
                }
            }
            catch (Exception exception)
            {
                log.Warning($"  render feature {name}: switching threw "
                    + exception.GetType().Name);
            }
        }
    }

    // Teilstueck-Vergleich ohne Gross-/Kleinschreibung. Teilstuecke und nicht
    // Gleichheit, damit "buto" genuegt und der Benutzer nicht
    // "ButoRenderFeature" fehlerfrei tippen muss.
    private static bool Matches(string disabled, string name)
    {
        if (string.IsNullOrWhiteSpace(disabled))
            return false;

        var parts = disabled.Split(',');

        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index].Trim();

            if (part.Length == 0)
                continue;

            if (name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    // ====================================================================
    // DER EINE VERSUCH, DEN NEBEL ZU BEHALTEN
    //
    // temporalAALighting und temporalAAMedia treiben die temporale
    // Reprojektion - also den Teil, der die EINE toPreviousView-Matrix
    // benutzt. Auf 0 nimmt er die nachweislich pro-Pass-zustandsbehaftete
    // Komponente heraus, OHNE den Nebel abzuschalten.
    //
    // Ehrlich zur Grenze: der geteilte Dictionary<Camera, RTData> bleibt. Es
    // ist ein Versuch, und er bekommt GENAU EINEN LAUF - die Lehre aus den
    // Abschnitten 155 bis 159, wo vier Abschnitte an derselben Stelle gedreht
    // haben, weil nie festgelegt war, wann eine Hypothese erledigt ist.
    //
    // Erkennbar am Symptom: Geisterbild oder Flimmern in einem Auge spricht
    // fuer Reprojektionsartefakte. Nebel links GANZ ABWESEND spricht dagegen,
    // dann ist der geteilte Puffer die Ursache und nicht die Matrix.
    internal void ApplyFogTemporal(MelonLogger.Instance log, float value)
    {
        // Negativ heisst NICHT ANFASSEN, und darum ist der Schluessel eine Zahl
        // und kein bool: ein bool hat keinen dritten Zustand und muesste beim
        // Ausschalten raten, was vorher dort stand.
        if (value < 0f)
            return;

        // Gleicher Wunsch wie beim letzten Mal: nichts zu tun. Volume-Parameter
        // haelt das Spiel nicht zurueck, anders als die Renderer-Features.
        if (!float.IsNaN(fogTemporal) && Mathf.Approximately(fogTemporal, value))
            return;

        try
        {
            var all = Resources.FindObjectsOfTypeAll(
                Il2CppType.Of<ButoVolumetricFog>());

            var touched = 0;

            for (var index = 0; index < all.Length; index++)
            {
                var fog = all[index]?.TryCast<ButoVolumetricFog>();

                if (fog is null || fog == null)
                    continue;

                // overrideState MUSS mit: ohne ihn liest das Volume-System den
                // Profilwert und der geschriebene bleibt ohne Wirkung. Ein
                // gesetzter Wert, der nicht wirkt, waere die schlimmere Art
                // von Erfolgsmeldung.
                fog.temporalAALighting.value = value;
                fog.temporalAALighting.overrideState = true;
                fog.temporalAAMedia.value = value;
                fog.temporalAAMedia.overrideState = true;

                touched++;
            }

            fogTemporal = value;

            // GEZAEHLT, nicht gemeldet. Null beruehrte Volumen bei gesetztem
            // Schluessel ist die interessante Auskunft: dann gibt es keinen
            // ButoVolumetricFog in der Szene und der Versuch hat nichts
            // gepruefen.
            log.Msg($"  buto temporal AA set to {value:0.##} on {touched} "
                + $"volume(s) of {all.Length} found");
        }
        catch (Exception exception)
        {
            log.Warning("  buto temporal AA threw "
                + exception.GetType().Name + "; leaving the fog alone");
        }
    }

    // ====================================================================
    // DIE GRASKLINGEN SIND KAMERAZUGEWANDT - Abschnitt 162.
    //
    // Hier und nicht in einer eigenen Klasse, weil es dieselbe Aufgabe ist:
    // etwas abschalten, das unter MultiPass nur in einem Auge landet. Der
    // Dateikopf beschreibt genau diese Aufgabe.
    //
    // Aber NICHT dieselbe Bauform: ShellTextureGeometry ist ein MonoBehaviour
    // auf Levelgeometrie, kein Pipeline-Asset. Es wechselt mit der Szene, und
    // ein Level kann hunderte Instanzen tragen.
    //
    // ZWEI SCHALTER und nicht einer: die SCHALEN liegen parallel zum Boden und
    // sind stereo-korrekt, nur die FINS sind kamerazugewandt. Wer beide
    // zusammenlegt, kann den billigen Teil nicht ohne den teuren haben - und
    // "Rasen weg" waere ein hoher Preis fuer ein Problem, das an den Klingen
    // haengt.
    internal void ApplyGrass(MelonLogger.Instance log, bool fins, bool shells,
        float rescanSeconds)
    {
        var wantFins = fins ? 1 : 0;
        var wantShells = shells ? 1 : 0;

        // Nichts gewollt und nichts gemerkt: gar nicht suchen. Im
        // Auslieferungszustand (beide an) kostet dieser Weg damit keinen
        // einzigen Sweep.
        if (wantFins == 1 && wantShells == 1 && wantedFins < 0 && wantedShells < 0)
            return;

        var changed = wantFins != wantedFins || wantShells != wantedShells;

        if (!ResolveGrass(log, rescanSeconds) && !changed)
            return;

        var finsOff = 0;
        var shellsOff = 0;
        var touched = 0;

        for (var index = 0; index < grass.Count; index++)
        {
            try
            {
                var piece = grass[index];

                if (piece is null || piece == null)
                    continue;

                // LESEN, schreiben, LESEN - dieselbe Ordnung wie bei den
                // Renderer-Features und aus demselben Grund: gezaehlt wird die
                // Wirkung, nicht der Aufruf.
                if (piece.DrawFins != fins)
                    piece.DrawFins = fins;

                if (piece.DrawShells != shells)
                    piece.DrawShells = shells;

                if (!piece.DrawFins)
                    finsOff++;

                if (!piece.DrawShells)
                    shellsOff++;

                touched++;
            }
            catch
            {
                // Eine einzelne Grasflaeche, die sich nicht schalten laesst,
                // darf die anderen nicht mitnehmen.
            }
        }

        if (changed || !grassReported)
        {
            grassReported = true;
            wantedFins = wantFins;
            wantedShells = wantShells;

            log.Msg($"  grass: {touched} of {grass.Count} piece(s) set"
                + $"   fins off on {finsOff}"
                + $"   shells off on {shellsOff}"
                + (grass.Count == 0
                    ? "   NO ShellTextureGeometry IN THIS SCENE"
                    : string.Empty));
        }
    }

    // Eigener Sweep, weil die Lebensdauer eine andere ist. Die Meldung nennt
    // die ANZAHL: null gefundene Flaechen bei gesetztem Schalter ist die
    // interessante Auskunft, denn dann hat der Versuch nichts geprueft.
    private bool ResolveGrass(MelonLogger.Instance log, float rescanSeconds)
    {
        var alive = grass.Count > 0;

        for (var index = 0; alive && index < grass.Count; index++)
        {
            var piece = grass[index];

            if (piece is null || piece == null)
                alive = false;
        }

        if (alive && Time.unscaledTime < nextGrassScan)
            return true;

        nextGrassScan = Time.unscaledTime + Mathf.Max(0.5f, rescanSeconds);

        try
        {
            var all = Resources.FindObjectsOfTypeAll(
                Il2CppType.Of<ShellTextureGeometry>());

            grass.Clear();

            for (var index = 0; index < all.Length; index++)
            {
                var piece = all[index]?.TryCast<ShellTextureGeometry>();

                if (piece is not null && piece != null)
                    grass.Add(piece);
            }

            return grass.Count > 0;
        }
        catch (Exception exception)
        {
            log.Warning("  grass: the sweep threw "
                + exception.GetType().Name + "; leaving the grass alone");
            grass.Clear();
            return false;
        }
    }

    // ====================================================================
    // DER ZWEITE KANDIDAT: UNITY-TERRAIN-DETAILGRAS - Abschnitt 162.
    //
    // Kein Zwischenspeicher und kein Zeitgeber: Terrains sind pro Szene eine
    // Handvoll, der Sweep laeuft nur bei ZUSTANDSWECHSEL. Wer hier einen
    // Speicher fuehrt, fuehrt ihn fuer drei Objekte.
    //
    // detailObjectDensity BLEIBT UNGENUTZT, obwohl es dieselbe Wirkung haette.
    // Zwei Hebel auf dasselbe Ziel sind eine zweite Wahrheit, und dann steht
    // irgendwann einer auf einem Wert, den niemand gesetzt hat.
    internal void ApplyTerrainFoliage(MelonLogger.Instance log, bool foliage,
        bool instanced)
    {
        var want = foliage ? 1 : 0;
        var wantInst = instanced ? 1 : 0;

        if (want == wantedFoliage && wantInst == wantedInstanced)
            return;

        // Wie beim Gras: im Auslieferungszustand nie suchen.
        if (foliage && instanced && wantedFoliage < 0 && wantedInstanced < 0)
        {
            wantedFoliage = want;
            wantedInstanced = wantInst;
            return;
        }

        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Terrain>());

            var off = 0;
            var plain = 0;
            var found = 0;

            for (var index = 0; index < all.Length; index++)
            {
                var terrain = all[index]?.TryCast<Terrain>();

                if (terrain is null || terrain == null)
                    continue;

                found++;

                if (terrain.drawTreesAndFoliage != foliage)
                    terrain.drawTreesAndFoliage = foliage;

                // ZURUECKGELESEN, nicht angenommen.
                if (!terrain.drawTreesAndFoliage)
                    off++;

                // DIE INSTANZIERUNG - Abschnitt 166, und das ist der
                // eigentliche Verdaechtige.
                //
                // Instanziertes Terrain baut seine Patch-Daten pro KAMERA und
                // Frame auf. Wird der Puffer im zweiten Augendurchgang
                // wiederverwendet, sitzen die Patches falsch - und weil der
                // Shader die Normalen PRO PIXEL aus einer Terrain-Normalmap
                // liest, werden daraus falsche Normalen und damit falsche
                // BELEUCHTUNG. Genau das wurde gemeldet.
                //
                // Abgeschaltet zeichnet das Terrain als gewoehnliche
                // Patch-Meshes: gleiche Optik, nur ohne Instanzierungspfad.
                if (terrain.drawInstanced != instanced)
                    terrain.drawInstanced = instanced;

                if (!terrain.drawInstanced)
                    plain++;
            }

            wantedFoliage = want;
            wantedInstanced = wantInst;

            // Der Nullfall AUSDRUECKLICH: ohne ihn waere "kein Effekt" nicht von
            // "nichts gefunden" zu trennen, und genau das ist hier der
            // Unterscheider gegen die Fins.
            log.Msg(found == 0
                ? "  terrain: NO Terrain IN THIS SCENE - both candidates are out"
                : $"  terrain: {found} found   foliage off on {off}"
                    + $"   instancing off on {plain}");
        }
        catch (Exception exception)
        {
            log.Warning("  terrain foliage threw "
                + exception.GetType().Name + "; leaving the terrain alone");
        }
    }

    // ====================================================================
    // DIE GANZE EBENE ABSCHALTEN - Abschnitt 163.
    //
    // Beantwortet "ist es ueberhaupt die Nachbearbeitung?" in EINEM Lauf,
    // statt sechs Komponenten einzeln zu raten. Erst wenn die Antwort ja ist,
    // lohnt das Trennen.
    internal void ApplyPostProcessing(MelonLogger.Instance log, bool wanted)
    {
        var want = wanted ? 1 : 0;

        if (want == wantedPost)
            return;

        // Im Auslieferungszustand nie suchen.
        if (wanted && wantedPost < 0)
        {
            wantedPost = want;
            return;
        }

        try
        {
            var all = Resources.FindObjectsOfTypeAll(
                Il2CppType.Of<UniversalAdditionalCameraData>());

            var off = 0;
            var found = 0;

            for (var index = 0; index < all.Length; index++)
            {
                var data = all[index]?.TryCast<UniversalAdditionalCameraData>();

                if (data is null || data == null)
                    continue;

                found++;

                if (data.renderPostProcessing != wanted)
                    data.renderPostProcessing = wanted;

                // ZURUECKGELESEN.
                if (!data.renderPostProcessing)
                    off++;
            }

            wantedPost = want;

            log.Msg(found == 0
                ? "  post processing: NO UniversalAdditionalCameraData FOUND"
                : $"  post processing: off on {off} of {found} camera(s)");
        }
        catch (Exception exception)
        {
            log.Warning("  post processing threw "
                + exception.GetType().Name + "; leaving it alone");
        }
    }

    // ====================================================================
    // UND DAS EINZELNE TEIL - Abschnitt 163.
    //
    // Dasselbe Muster wie DisableRenderFeatures, und aus demselben Grund: es
    // hat sich gerade bezahlt. Sechs Kandidaten einzeln zu bauen haette sechs
    // Builds gekostet; als Typnamen-Liste kostet jeder null.
    //
    // MotionBlur ist der Hauptverdaechtige - URP-Bewegungsunschaerfe rechnet
    // mit der VORHERIGEN View-Projection-Matrix, also derselben Bauform, die
    // beim Nebel schon der Befund war.
    internal void ApplyVolumes(MelonLogger.Instance log, string disabled,
        float rescanSeconds)
    {
        // Nichts gewuenscht und nichts gemerkt: gar nicht suchen.
        if (string.IsNullOrWhiteSpace(disabled) && volumeApplied.Count == 0
            && volumesReported)
            return;

        if (!ResolveVolumes(log, rescanSeconds))
            return;

        ReportVolumes(log);

        for (var index = 0; index < volumes.Count; index++)
        {
            var name = volumeNames[index];
            var wanted = !Matches(disabled, name);

            try
            {
                var volume = volumes[index];

                if (volume.active == wanted)
                {
                    volumeApplied[name] = wanted;
                    continue;
                }

                volume.active = wanted;

                var now = volume.active;

                if (!volumeApplied.TryGetValue(name, out var last) || last != wanted)
                {
                    volumeApplied[name] = wanted;

                    log.Msg($"  volume {name}: active now "
                        + (now ? "YES" : "no")
                        + (now == wanted ? "" : "   THE GAME REFUSED"));
                }
            }
            catch (Exception exception)
            {
                log.Warning($"  volume {name}: switching threw "
                    + exception.GetType().Name);
            }
        }
    }

    private bool ResolveVolumes(MelonLogger.Instance log, float rescanSeconds)
    {
        var alive = volumes.Count > 0;

        for (var index = 0; alive && index < volumes.Count; index++)
        {
            var volume = volumes[index];

            if (volume is null || volume == null)
                alive = false;
        }

        if (alive && Time.unscaledTime < nextVolumeScan)
            return true;

        nextVolumeScan = Time.unscaledTime + Mathf.Max(0.5f, rescanSeconds);

        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<VolumeComponent>());

            volumes.Clear();
            volumeNames.Clear();

            for (var index = 0; index < all.Length; index++)
            {
                var volume = all[index]?.TryCast<VolumeComponent>();

                if (volume is null || volume == null)
                    continue;

                volumes.Add(volume);
                volumeNames.Add(volume.GetIl2CppType()?.Name ?? "?");
            }

            return volumes.Count > 0;
        }
        catch (Exception exception)
        {
            log.Warning("  volumes: the sweep threw "
                + exception.GetType().Name + "; leaving them alone");
            volumes.Clear();
            volumeNames.Clear();
            return false;
        }
    }

    // Einmal je Sitzung, und das ist wieder die Zuordnungsmessung: sie nennt,
    // welche Komponenten ueberhaupt geladen sind und welche aktiv. Eine
    // Vermutung ueber MotionBlur ist wertlos, wenn MotionBlur nicht existiert.
    private void ReportVolumes(MelonLogger.Instance log)
    {
        if (volumesReported)
            return;

        volumesReported = true;

        log.Msg($"volume components: {volumes.Count} found");

        for (var index = 0; index < volumes.Count; index++)
        {
            var state = "unreadable";

            try
            {
                state = volumes[index].active ? "YES" : "no";
            }
            catch
            {
                // Unlesbar ist eine eigene Auskunft, kein "nein".
            }

            log.Msg($"  {volumeNames[index],-28} active {state}");
        }
    }

    // ====================================================================
    // RENDERER NACH SHADERNAME ABSCHALTEN - Abschnitt 164.
    //
    // Der Gegentest zur Sonde, und er ist absichtlich grob: wer den Namen aus
    // dem Log hier eintraegt, sieht den Boden verschwinden. Entscheidend ist
    // aber nicht die Schoenheit, sondern die Antwort - BLEIBT das Artefakt
    // ohne den Boden, ist es nicht sein Material.
    //
    // Was abgeschaltet wurde, wird GEMERKT und beim Leeren des Schluessels
    // zurueckgegeben: ein Diagnosewerkzeug, das seinen Eingriff nicht
    // aufraeumen kann, kostet einen Neustart pro Versuch.
    internal void ApplyRenderersByShader(MelonLogger.Instance log, string disabled,
        float rescanSeconds)
    {
        var wanted = disabled ?? string.Empty;
        var changed = wanted != shaderWanted;

        if (!changed && string.IsNullOrWhiteSpace(wanted))
            return;

        if (changed)
        {
            // ERST ZURUECKGEBEN, dann neu suchen. Sonst bleibt beim Wechsel
            // von einem Namen auf den naechsten der erste Boden unsichtbar.
            Restore(log);
            shaderWanted = wanted;
            nextShaderScan = 0f;
        }

        if (string.IsNullOrWhiteSpace(shaderWanted))
            return;

        if (Time.unscaledTime < nextShaderScan)
            return;

        nextShaderScan = Time.unscaledTime + Mathf.Max(0.5f, rescanSeconds);

        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Renderer>());
            var hit = 0;

            for (var index = 0; index < all.Length; index++)
            {
                var renderer = all[index]?.TryCast<Renderer>();

                if (renderer is null || renderer == null || !renderer.enabled)
                    continue;

                if (!UsesShader(renderer, shaderWanted))
                    continue;

                renderer.enabled = false;

                // ZURUECKGELESEN, und nur dann gemerkt: was nicht wirklich
                // ausgegangen ist, darf nicht als zurueckzugebend gelten.
                if (!renderer.enabled)
                {
                    shaderHidden.Add(renderer);
                    hit++;
                }
            }

            if (hit > 0 || changed)
                log.Msg($"  renderers by shader \"{shaderWanted}\": {hit} switched off "
                    + $"this pass, {shaderHidden.Count} held");
        }
        catch (Exception exception)
        {
            log.Warning("  renderers by shader threw "
                + exception.GetType().Name);
        }
    }

    private static bool UsesShader(Renderer renderer, string fragments)
    {
        try
        {
            var materials = renderer.sharedMaterials;

            for (var index = 0; index < materials.Length; index++)
            {
                var material = materials[index];

                if (material is null || material == null)
                    continue;

                var name = material.shader?.name;

                if (name is not null && Matches(fragments, name))
                    return true;
            }
        }
        catch
        {
            // Ein unlesbares Material ist kein Treffer.
        }

        return false;
    }

    // Zurueckgeben, was abgeschaltet wurde. Ohne das kostet jeder Versuch
    // einen Neustart.
    private void Restore(MelonLogger.Instance log)
    {
        var back = 0;

        for (var index = 0; index < shaderHidden.Count; index++)
        {
            try
            {
                var renderer = shaderHidden[index];

                if (renderer is null || renderer == null)
                    continue;

                renderer.enabled = true;

                if (renderer.enabled)
                    back++;
            }
            catch
            {
                // Ein zerstoerter Renderer braucht nichts zurueck.
            }
        }

        if (shaderHidden.Count > 0)
            log.Msg($"  renderers by shader: {back} of {shaderHidden.Count} given back");

        shaderHidden.Clear();
    }

    // ====================================================================
    // DIE MATERIALLISTE - Abschnitt 165, und sie braucht KEIN Zielen.
    //
    // Die Sonde aus Abschnitt 164 verlangte eine Zielhandlung UND ein
    // Zeitfenster; zwei Laeufe mit 61 Berichten lieferten null Zeilen vom
    // gesuchten Boden, weil das Laden eines Levels Zeit kostet. Ein Messgeraet
    // mit zwei Bedingungen an den Benutzer ist falsch entworfen.
    //
    // Hier laedt der Benutzer nur das Level. GRUPPIERT nach Shader und
    // Keyword-Satz, weil ein Level tausende Materialien traegt und die Gruppe
    // die Auskunft ist: welcher Satz kommt vor, und wie oft.
    internal void ReportMaterials(MelonLogger.Instance log, bool wanted,
        float rescanSeconds)
    {
        if (!wanted)
            return;

        // NICHT auf absolute Zeit gattern - Abschnitt 166.
        //
        // Die erste Fassung wartete auf Time.unscaledTime > 5 s, also fuenf
        // Sekunden nach SPIELSTART. Das ist mitten im Ladebildschirm: drei
        // Laeufe lang zaehlte die Liste die Lobby, nie ein Level. Dritter
        // Fehler derselben Familie in dieser Sitzung.
        //
        // Jetzt entscheidet die MATERIALZAHL: aendert sie sich deutlich, wurde
        // eine Szene geladen, und die Liste laeuft neu. Das haengt an nichts,
        // was der Benutzer tun oder treffen muss.
        if (Time.unscaledTime < nextInventoryScan)
            return;

        nextInventoryScan = Time.unscaledTime + Mathf.Max(2f, rescanSeconds);

        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Material>());

            var groups = new Dictionary<string, int>();
            var examples = new Dictionary<string, string>();
            var noise = 0;

            for (var index = 0; index < all.Length; index++)
            {
                var material = all[index]?.TryCast<Material>();

                if (material is null || material == null)
                    continue;

                string key;
                var name = "?";

                try
                {
                    name = material.name;
                    var shader = material.shader?.name ?? "null";

                    // RAUSCHEN AUSFILTERN statt Nutzdaten abschneiden -
                    // Abschnitt 167. Blit-, Schrift- und
                    // Oberflaechenmaterialien koennen ein Artefakt auf einer
                    // Bodenflaeche nicht zeichnen, belegen aber ueber 40 der
                    // Gruppen und draengen das gesuchte Einzelstueck aus dem
                    // Deckel.
                    if (Noise(shader))
                    {
                        noise++;
                        continue;
                    }
                    var keywords = material.shaderKeywords;

                    var set = string.Empty;

                    for (var k = 0; k < keywords.Length; k++)
                        set += (k == 0 ? "" : " ") + keywords[k];

                    key = shader + "  [" + set + "]";
                }
                catch
                {
                    // Ein unlesbares Material ist eine eigene Gruppe und kein
                    // Grund, die Liste abzubrechen.
                    key = "<unreadable>";
                }

                groups.TryGetValue(key, out var count);
                groups[key] = count + 1;

                if (!examples.ContainsKey(key))
                    examples[key] = name;
            }

            // Zehn Prozent Abweichung ist die Schwelle: ein Levelwechsel
            // vervielfacht die Zahl, ein normaler Frame aendert sie kaum.
            var moved = inventoryCount == 0
                || Mathf.Abs(all.Length - inventoryCount)
                    > Mathf.Max(20, inventoryCount / 10);

            if (!moved)
                return;

            inventoryCount = all.Length;

            // Das Weggelassene wird GENANNT. Ein Filter, der verschweigt,
            // was er unterdrueckt, ist die naechste blinde Stelle.
            log.Msg($"material inventory: {all.Length} material(s), "
                + $"{groups.Count} group(s) after filtering out {noise} "
                + "blit/font/UI material(s)");

            // Absteigend, damit die haeufigen Flaechen oben stehen. Gedeckelt,
            // weil ein Level auch hundert Gruppen haben kann.
            var shown = 0;

            foreach (var pair in Sorted(groups))
            {
                if (shown++ >= 150)
                {
                    log.Msg($"  ... and {groups.Count - 150} more group(s)");
                    break;
                }

                log.Msg($"  {pair.Value,5}x  {pair.Key}"
                    + $"   e.g. {examples[pair.Key]}");
            }
        }
        catch (Exception exception)
        {
            log.Warning("  material inventory threw "
                + exception.GetType().Name);
        }
    }

    // Was ein Artefakt auf einer BODENFLAECHE nicht zeichnen kann. Nicht
    // "unwichtig", sondern strukturell ausgeschlossen: Blits laufen im
    // Bildraum ueber das fertige Bild, Schrift und UI liegen darueber.
    private static bool Noise(string shader)
        => shader.StartsWith("Hidden/Universal", StringComparison.Ordinal)
            || shader.StartsWith("Hidden/InternalError", StringComparison.Ordinal)
            || shader.StartsWith("TextMeshPro/", StringComparison.Ordinal)
            || shader.StartsWith("UI/", StringComparison.Ordinal)
            || shader.StartsWith("Sprites/", StringComparison.Ordinal)
            || shader.StartsWith("GUI/", StringComparison.Ordinal);

    // Eigene Sortierung, weil LINQ over Il2Cpp-Sammlungen hier nicht gebraucht
    // wird und eine Liste von 60 Gruppen jede Ordnung vertraegt.
    private static List<KeyValuePair<string, int>> Sorted(Dictionary<string, int> groups)
    {
        var list = new List<KeyValuePair<string, int>>();

        foreach (var pair in groups)
            list.Add(pair);

        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        return list;
    }

    // ====================================================================
    // KEYWORDS ABSCHALTEN - Abschnitt 165.
    //
    // Der Gegentest IST der Fix: trifft es _DECAL_ENABLE und das Artefakt
    // verschwindet, ist das bewiesen und behoben in einem Schritt - ohne
    // Nebel, Gras, Schatten oder Nachbearbeitung anzutasten.
    internal void ApplyKeywords(MelonLogger.Instance log, string disabled,
        float rescanSeconds)
    {
        var wanted = disabled ?? string.Empty;
        var changed = wanted != keywordWanted;

        if (!changed && string.IsNullOrWhiteSpace(wanted))
            return;

        if (changed)
        {
            // Erst zurueckgeben, dann neu suchen - sonst bleibt beim Wechsel
            // von einem Keyword auf das naechste das erste abgeschaltet.
            RestoreKeywords(log);
            keywordWanted = wanted;
            nextKeywordScan = 0f;
        }

        if (string.IsNullOrWhiteSpace(keywordWanted))
            return;

        if (Time.unscaledTime < nextKeywordScan)
            return;

        nextKeywordScan = Time.unscaledTime + Mathf.Max(1f, rescanSeconds);

        var parts = keywordWanted.Split(',');

        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Material>());
            var hit = 0;
            var refused = 0;

            for (var index = 0; index < all.Length; index++)
            {
                var material = all[index]?.TryCast<Material>();

                if (material is null || material == null)
                    continue;

                for (var p = 0; p < parts.Length; p++)
                {
                    var keyword = parts[p].Trim();

                    if (keyword.Length == 0)
                        continue;

                    try
                    {
                        if (!material.IsKeywordEnabled(keyword))
                            continue;

                        material.DisableKeyword(keyword);

                        // ZURUECKGELESEN. Ein Keyword, das sich nicht
                        // abschalten laesst, darf nicht als abgeschaltet
                        // gezaehlt und nicht zurueckgegeben werden.
                        if (material.IsKeywordEnabled(keyword))
                        {
                            refused++;
                            continue;
                        }

                        keywordMaterials.Add(material);
                        keywordNames.Add(keyword);
                        hit++;
                    }
                    catch
                    {
                        // Ein Material, das seine Keywords nicht preisgibt,
                        // nimmt die anderen nicht mit.
                    }
                }
            }

            if (hit > 0 || refused > 0 || changed)
                log.Msg($"  keywords \"{keywordWanted}\": {hit} disabled this pass, "
                    + $"{keywordMaterials.Count} held"
                    + (refused > 0 ? $"   {refused} REFUSED" : string.Empty));
        }
        catch (Exception exception)
        {
            log.Warning("  keywords threw " + exception.GetType().Name);
        }
    }

    private void RestoreKeywords(MelonLogger.Instance log)
    {
        var back = 0;

        for (var index = 0; index < keywordMaterials.Count; index++)
        {
            try
            {
                var material = keywordMaterials[index];

                if (material is null || material == null)
                    continue;

                material.EnableKeyword(keywordNames[index]);

                if (material.IsKeywordEnabled(keywordNames[index]))
                    back++;
            }
            catch
            {
                // Ein zerstoertes Material braucht nichts zurueck.
            }
        }

        if (keywordMaterials.Count > 0)
            log.Msg($"  keywords: {back} of {keywordMaterials.Count} given back");

        keywordMaterials.Clear();
        keywordNames.Clear();
    }

    // ====================================================================
    // DIE KLASSE TRENNEN - Abschnitt 168.
    //
    // Elf Kandidaten sind gemessen abgeschaltet und das Artefakt blieb jedes
    // Mal. Damit ist nicht der zwoelfte Kandidat faellig, sondern die
    // Annahme, die alle elf getragen hat: dass eine kameraabhaengige Groesse
    // einmal berechnet und fuer zwei Augen benutzt wird.
    //
    // Beim Nebel war das BELEGT. Fuer alles danach war es eine Analogie, und
    // eine Analogie ist keine Messung.
    //
    // stereoSeparation = 0 laesst beide Augen aus DEMSELBEN Punkt rendern.
    // Verschwindet das Artefakt, ist es eine Sichtinkonsistenz; bleibt es
    // einseitig, schreibt etwas in genau ein Augenziel und die ganze
    // Suchfamilie war falsch.
    //
    // KEIN FIX: ohne Augenabstand gibt es keine Tiefe. Reines Messgeraet.
    internal void ApplyStereoSeparation(MelonLogger.Instance log, float metres)
    {
        // Negativ heisst NICHT ANFASSEN - dieselbe Form wie FogTemporal, damit
        // "unberuehrt" von "auf 0 gesetzt" unterscheidbar bleibt.
        if (metres < 0f)
            return;

        if (!float.IsNaN(appliedSeparation)
            && Mathf.Approximately(appliedSeparation, metres))
            return;

        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Camera>());

            var touched = 0;
            var found = 0;
            var before = float.NaN;

            for (var index = 0; index < all.Length; index++)
            {
                var camera = all[index]?.TryCast<Camera>();

                if (camera is null || camera == null)
                    continue;

                found++;

                if (float.IsNaN(before))
                    before = camera.stereoSeparation;

                camera.stereoSeparation = metres;

                // ZURUECKGELESEN. Ein Wert, der nicht steht, darf nicht als
                // gesetzt gemeldet werden - die Lehre, die in dieser Sitzung
                // schon zweimal zu spaet kam.
                if (Mathf.Approximately(camera.stereoSeparation, metres))
                    touched++;
            }

            appliedSeparation = metres;

            log.Msg($"  stereo separation: {metres:0.###} m on {touched} of "
                + $"{found} camera(s)   was {before:0.###} m");
        }
        catch (Exception exception)
        {
            log.Warning("  stereo separation threw "
                + exception.GetType().Name + "; cameras untouched");
        }
    }

    // ====================================================================
    // ACHT KAMERAS, UND ICH HABE NIE EINE ANGESEHEN - Abschnitt 169.
    //
    // "off on 8 of 8 camera(s)" stand zweimal im Log und ich habe es zweimal
    // ueberlesen. Ein Artefakt, das in einem Auge vorhanden und im anderen
    // ABWESEND ist, entsteht unmittelbar, wenn eine Kamera auf ein einzelnes
    // Auge gebunden ist - stereoTargetEye Left oder Right.
    //
    // Das ist keine Analogie zum Nebel, sondern eine direkte messbare Ursache
    // fuer genau die beobachtete Form, und die erste Erklaerung, die nicht
    // verlangt, dass irgendetwas pro Kamera puffert.
    //
    // Gemeldet wird bei ANZAHLWECHSEL, nicht einmalig: Kameras kommen mit der
    // Szene, und eine Liste, die zu frueh laeuft, zaehlt die falsche Szene -
    // der Fehler aus Abschnitt 166.
    internal void ReportCameras(MelonLogger.Instance log, bool wanted,
        float rescanSeconds)
    {
        if (!wanted)
            return;

        if (Time.unscaledTime < nextCameraScan)
            return;

        nextCameraScan = Time.unscaledTime + Mathf.Max(2f, rescanSeconds);

        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Camera>());

            var live = new List<Camera>();

            for (var index = 0; index < all.Length; index++)
            {
                var camera = all[index]?.TryCast<Camera>();

                if (camera is not null && camera != null)
                    live.Add(camera);
            }

            if (live.Count == cameraCount)
                return;

            cameraCount = live.Count;

            log.Msg($"camera inventory: {live.Count} camera(s)");

            for (var index = 0; index < live.Count; index++)
            {
                var camera = live[index];

                try
                {
                    var target = camera.targetTexture;

                    // stereoTargetEye IST DIE FRAGE dieses Abschnitts. Alles
                    // andere steht daneben, weil eine Kamera auch ueber
                    // Culling-Maske oder Zieltextur einseitig wirken kann.
                    log.Msg($"  {camera.name,-28}"
                        + $" eye {camera.stereoTargetEye}"
                        + $"   stereo {(camera.stereoEnabled ? "YES" : "no")}"
                        + $"   enabled {(camera.enabled ? "YES" : "no")}"
                        + $"   inScene {(camera.gameObject.activeInHierarchy ? "YES" : "no")}"
                        + $"   depth {camera.depth:0.#}"
                        + $"   clear {camera.clearFlags}"
                        + $"   mask 0x{camera.cullingMask:x8}"
                        + (target is null || target == null
                            ? "   no target texture"
                            : $"   target {target.name} {target.width}x{target.height}"));
                }
                catch (Exception inner)
                {
                    log.Msg($"  camera {index}: reading threw {inner.GetType().Name}");
                }
            }
        }
        catch (Exception exception)
        {
            log.Warning("  camera inventory threw " + exception.GetType().Name);
        }
    }

    // ====================================================================
    // VOLUMETRIC LIGHT BEAM, AUF SINGLEPASS KONFIGURIERT - Abschnitt 170.
    //
    // Der Shadername aus der Materialliste des Spielers sagt es selbst:
    //
    //     Hidden/VLB_URP_SinglePass
    //
    // Single-Pass-Stereo, waehrend dieses Spiel unter MULTIPASS laeuft. Und
    // VLB verdeckt seine Strahlen ueber DynamicOcclusionDepthBuffer, also
    // ueber den Tiefenpuffer im Bildraum - die Bauform, die pro Auge
    // schiefgehen kann.
    //
    // Ein Lichtstrahl, der eine Flaeche streift, hellt sie auf und legt sich
    // ueber den Schlagschatten. Genau das wurde gemeldet.
    //
    // Abgeschaltet wird die KOMPONENTE, nicht das Objekt: ein Strahl haengt an
    // einer Lampe, und das Objekt zu deaktivieren naehme die Lampe mit.
    internal void ApplyLightBeams(MelonLogger.Instance log, bool wanted)
    {
        var want = wanted ? 1 : 0;

        if (want == wantedBeams)
            return;

        if (wanted && wantedBeams < 0)
        {
            wantedBeams = want;
            return;
        }

        try
        {
            var all = Resources.FindObjectsOfTypeAll(
                Il2CppType.Of<VolumetricLightBeam>());

            var off = 0;
            var found = 0;

            for (var index = 0; index < all.Length; index++)
            {
                var beam = all[index]?.TryCast<VolumetricLightBeam>();

                if (beam is null || beam == null)
                    continue;

                found++;

                if (beam.enabled != wanted)
                    beam.enabled = wanted;

                // ZURUECKGELESEN. In dieser Sitzung hat genau das einmal eine
                // falsche Schlussfolgerung verhindert (0 von 8 Kameras beim
                // Augenabstand) - die Regel bleibt.
                if (!beam.enabled)
                    off++;
            }

            wantedBeams = want;

            log.Msg(found == 0
                ? "  light beams: NO VolumetricLightBeam IN THIS SCENE - candidate is out"
                : $"  light beams: off on {off} of {found} beam(s)");
        }
        catch (Exception exception)
        {
            log.Warning("  light beams threw "
                + exception.GetType().Name + "; beams untouched");
        }
    }

    // Kein Unity-Objekt gehoert dieser Klasse - sie haelt nur Referenzen auf
    // Assets des Spiels. Aufgeraeumt wird darum der Zwischenspeicher und
    // sonst nichts; ein Destroy hier wuerde die Pipeline des Spiels zerlegen.
    internal void Dispose()
    {
        features.Clear();
        names.Clear();
        applied.Clear();
        reported = false;
        nextScan = 0f;
        fogTemporal = float.NaN;

        grass.Clear();
        nextGrassScan = 0f;
        grassReported = false;
        wantedFins = -1;
        wantedShells = -1;
        wantedFoliage = -1;

        volumes.Clear();
        volumeNames.Clear();
        volumeApplied.Clear();
        volumesReported = false;
        nextVolumeScan = 0f;
        wantedPost = -1;

        shaderHidden.Clear();
        shaderWanted = string.Empty;
        nextShaderScan = 0f;

        keywordMaterials.Clear();
        keywordNames.Clear();
        keywordWanted = string.Empty;
        nextKeywordScan = 0f;
        inventoryCount = 0;
        nextInventoryScan = 0f;
        wantedInstanced = -1;
        appliedSeparation = float.NaN;
        wantedBeams = -1;
        cameraCount = 0;
        nextCameraScan = 0f;
    }
}
