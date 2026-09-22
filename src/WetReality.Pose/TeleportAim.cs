using MelonLoader;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace WetReality;

// Das Teleport-Ziel: Strahlgang, Gueltigkeit und der Marker - Abschnitt 147.
//
// ========================================================================
// DIE ENTFERNUNG OHNE RaycastHit, und das ist der Befund, der diese Datei
// ueberhaupt moeglich macht.
//
// Projektregel (HandSpray.cs Zeile 90): KEINE STRUKTUREN UEBER DIE
// INTEROP-GRENZE, Bounds, Ray und RaycastHit ausdruecklich genannt. Ein
// Teleport braucht aber die Trefferentfernung, und die steht genau in einem
// RaycastHit.
//
// Der Ausweg steht in der Signatur selbst:
//
//     Physics.Raycast(Vector3 origin, Vector3 dir, float maxDistance, int mask)
//
// gibt ein bool zurueck - und ist MONOTON in maxDistance: wer innerhalb t
// trifft, trifft auch innerhalb jedes groesseren t. Damit ist die Entfernung
// per BISEKTION ueber t zu holen. Zwoelf bool-Strahlen auf 12 m Reichweite
// ergeben 3 mm; das ist genauer als der Marker gross ist.
//
// Nichts an diesem Weg ist ein Trick um die Regel herum. Es ist dasselbe
// Muster, das HandSpray schon benutzt ("Kein RaycastHit, nur bool") - nur auf
// eine Frage angewandt, bei der es zunaechst nicht danach aussieht.
//
// DIE EINE GRENZE, DIE BLEIBT: ohne Trefferstruktur gibt es keine NORMALE.
// "Ist das ein Boden oder eine Wand" ist damit nicht direkt zu beantworten -
// die Antwort kommt aus der Kapselprobe unten, und die ist ohnehin die
// Frage, auf die es ankommt: passt der Spieler dort hin.
//
// ========================================================================
// DIE REICHWEITE IST DIE WURFPARABEL DES SPIELS, keine Wunschzahl.
//
// Auftrag war, die Sprungweite auf das zu begrenzen, was ein Spieler mit der
// Sprungtaste erreicht: auf ebenem Boden die Normalweite, von hoch nach tief
// weiter, von tief nach hoch kuerzer, und niemals hoeher als die Sprunghoehe.
//
// Das sind keine vier Sonderfaelle. Das ist EINE Formel, gefuettert aus
// m_jumpHeight und m_movementSpeed des Spiels - siehe Reach().
internal sealed class TeleportAim
{
    private const string HolderName = "WetRealityTeleportMarker";
    private const float CanvasWidth = 256f;

    // 3 cm ueber dem Boden. Ein Marker GENAU auf der Flaeche z-fightet, und
    // die Alternative waere ein ZTest auf Unitys geteiltem UI-Material - also
    // dieselbe Falle, die Vignette.cs im Kopf beschreibt. Drei Zentimeter
    // Geometrie sind billiger als ein geteiltes Material.
    private const float Lift = 0.03f;

    // Das Bogenende gehoert auf die MITTE DES MARKERS, nicht auf den Boden
    // darunter.
    //
    // Gemeldet mit Bild und Pfeil: "Das Ende der Parabel endet nicht im
    // Mittelpunkt des Targets." Die Ursache ist genau dieser Lift - der
    // Marker liegt 3 cm ueber dem Boden, der Bogen endete am Boden, und aus
    // schraeger Sicht liest sich der Hoehenunterschied als seitlicher Versatz.
    // Bei einem Ziel von 45 cm sind 3 cm rund ein Siebtel des Radius, und
    // genau so viel zeigt das Bild.
    private static readonly Vector3 MarkerLead = new(0f, Lift, 0f);

    // 256 statt 128, weil die Rasterlinien duenn und scharf sein sollen. Der
    // Preis ist ein EINMALIGER Aufbau von 65536 Bildpunkten; pro Frame kostet
    // die Textur nichts.
    private const int TextureSize = 256;

    // WIE WEIT DER BOGEN NACH UNTEN VERFOLGT WIRD, als Konstante und
    // ausdruecklich NICHT als Einstellung.
    //
    // Hier stand envelope.MaxDrop, und genau das war der Defekt: Abschnitt 154
    // hat den Quell-Default von 4 auf 12 gehoben, aber ein geaenderter
    // Quell-Default erreicht eine BESTEHENDE cfg nicht. Dort stand weiter 4,
    // der Bogen wurde 5 m unter dem Fuss abgeschnitten, und das Log meldete
    // "lands nowhere" nach 28 bis 30 Segmenten - nachgerechnet exakt der
    // Abbruch bei 31.
    //
    // Eine Suchtiefe ist keine Benutzereinstellung. Sie beantwortet nur, wie
    // weit nach unten noch nach Boden gesucht wird, und diese Antwort darf
    // nicht von einem Wert abhaengen, der in einer alten Datei steht. Als
    // Konstante kann sie nicht veralten.
    //
    // 25 m deckt jedes Dach und jede Grube in diesem Spiel. Die Grenze nach
    // unten bleibt die aus Abschnitt 154: der Bogen muss Boden TREFFEN.
    private const float TraceDrop = 25f;

    // DIE KREISE, von aussen nach innen. Anteile des Zielradius, wobei 1,0 die
    // Aussenkante des duennen Aussenrings ist.
    //
    //     1,000 .. 0,975   duenner Aussenring
    //     0,955 .. 0,845   dicker Flaechenring
    //     0,825 .. 0,805   duenner Innenring
    //     unter 0,795      Fuellung und Raster
    //
    // Die Luecken dazwischen sind Absicht: sie trennen die Ringe, und ohne sie
    // waere es ein breites Band statt mehrerer Kreise.
    private const float OuterRingOut = 1.00f;
    private const float OuterRingIn = 0.975f;
    private const float ThickRingOut = 0.955f;
    private const float ThickRingIn = 0.845f;
    private const float InnerRingOut = 0.825f;
    private const float InnerRingIn = 0.805f;
    private const float GridRadius = 0.795f;

    // Der Punkt im Zentrum - er sagt, wohin genau der Fuss kommt. 0,05 und
    // nicht 0,035: bei 256 Bildpunkten sind das 12,8 Punkte Durchmesser statt
    // 9, und in der Vorschau war der kleinere kaum zu sehen.
    private const float CentreDot = 0.05f;

    // Halbe Linienbreite in BILDPUNKTEN, nicht in Zellen: eine in Zellen
    // gemessene Linie wuerde bei anderer Zellzahl anders dick. 1,1 ergibt 2,2
    // Bildpunkte - schmaler als die 3,6 von vorher, wie gewuenscht, und bei
    // 256 Punkten noch ueber der Sichtbarkeitsgrenze.
    private const float GridLineHalfTexels = 1.1f;

    // Trigger werden ignoriert. Ein Auslosevolumen ist kein Boden, und eine
    // Aufzaehlung ueber die Interop-Grenze ist in diesem Projekt belegt
    // harmlos - BaseInput.InvokeItemInteraction(ItemInteraction) geht denselben
    // Weg.
    private const QueryTriggerInteraction NoTriggers =
        QueryTriggerInteraction.Ignore;

    // ====================================================================
    // EINE EIGENE KANTENFUNKTION, und sie ist die Behebung eines Fehlers, der
    // Ring und Fuellung unsichtbar gemacht hat.
    //
    // Mathf.SmoothStep(from, to, t) ist KEIN GLSL-smoothstep. Es ist ein
    // geglaettetes LERP: es gibt einen Wert ZWISCHEN from und to zurueck und
    // behandelt t als Interpolant, nicht als Position auf der Kante.
    //
    //     gemeint     Edge(0.75, 0.77, 0.85) = 1       Kante ueberschritten
    //     Unity       SmoothStep(0.75, 0.77, 0.85)
    //                 = 0.768                          Lerp mit t = 0.85
    //
    // Ring und Fuellung blieben darum unter Alpha 0,06 und waren unsichtbar,
    // waehrend das Raster mit seinem harten alpha = 1 als Einziges durchkam.
    //
    // Mathf.SmoothStep kommt in dieser Datei nicht mehr vor. EINE Funktion
    // statt vier Aufrufe mit geschobenen Vorzeichen - dieselbe Regel wie bei
    // UiDepth: zwei Kopien derselben Rechnung laufen auseinander.
    private static float Edge(float from, float to, float value)
    {
        if (Mathf.Approximately(from, to))
            return value < from ? 0f : 1f;

        var t = Mathf.Clamp01((value - from) / (to - from));
        return t * t * (3f - (2f * t));
    }

    // Ein Band zwischen innen und aussen, mit weichen Kanten und einem
    // PLATEAU voller Deckung dazwischen. Das ist, was ein Flaechenring ist.
    private static float Band(float inner, float outer, float edge, float radius)
        => Mathf.Min(
            Edge(inner - edge, inner + edge, radius),
            1f - Edge(outer - edge, outer + edge, radius));

    private GameObject? holder;

    // EINE Textur, EIN Knoten. Der Vorgaenger hatte drei Texturen, drei Knoten
    // und eine Stencil-Maske - die hat auf diesem World-Space-Canvas nicht
    // geklippt, und das Ergebnis war eine Quadratflaeche mit einem Kreisring
    // darauf. Statt daran nachzubessern traegt jetzt die Textur alles.
    private Texture2D? markerTexture;
    private RawImage? markerImage;

    // Mit welchen Werten die Textur erzeugt wurde. Aendert der Spieler sie,
    // gehoert sie neu erzeugt - ein Regler ohne Wirkung ist schlimmer als
    // keiner.
    private int bakedCells = -1;
    private float bakedFill = float.NaN;

    private bool buildFailed;
    private bool loggedOnce;
    private bool visible;

    // Alles, was eine Gueltigkeitspruefung braucht, in einem Griff.
    //
    // Eine Struktur, ja - aber eine, die NIE die Interop-Grenze sieht. Sie
    // wandert von Pose hierher und zurueck, beides verwalteter Code. Die Regel
    // aus dem Dateikopf gilt fuer Aufrufe ins Spiel, nicht fuer die eigene
    // Signatur.
    internal readonly struct Envelope
    {
        internal readonly float JumpHeight;

        // WIE HOCH EINE KANTE SEIN DARF, und das ist NICHT JumpHeight.
        //
        // JumpHeight ist der Scheitel des Sprungs; zum Landen muss nur der Fuss
        // darueber kommen, und die letzten Zentimeter uebernimmt der
        // Charaktercontroller mit seiner Stufentoleranz. Gemessen fehlten an
        // einer begehbaren Treppenstufe 8 cm.
        //
        // Getrennt gefuehrt und nicht in JumpHeight hineingerechnet: JumpHeight
        // bestimmt die WEITE ueber die Wurfparabel, und die soll die Toleranz
        // nicht mitbekommen.
        internal readonly float RiseCeiling;

        // DER KLETTERNDE GANG - Abschnitt 159. Nur benutzt, wenn RiseCeiling
        // schon abgelehnt hat.
        internal readonly bool SlopeWalk;
        // ABSTAND STATT ANZAHL, und das ist eine MESSUNG und kein Feinschliff.
        //
        // Mit fester Anzahl gegen sechs Bodenprofile geprueft: 12 Punkte auf
        // 4,5 m sind 0,375 m Abstand, und die OFFENE Treppe scheiterte mit 1
        // von 12 Treffern - genau der gemeldete Fall. Zwei aufeinanderfolgende
        // Treffer lagen dann mehrere Stufen auseinander und fielen aus dem
        // Band.
        //
        // Schlimmer: 0,25 m ergab 0 von 18 Treffern, also WENIGER als 0,375 -
        // es aliast gegen die Stufenteilung von 0,28. Eine feste Anzahl ist
        // damit die falsche Groesse; der Abstand muss deutlich unter der
        // Stufentiefe liegen. 0,18 m bekommt alle neun geprueften Profile
        // richtig.
        internal readonly float SlopeSpacing;
        internal readonly float SlopeStepRise;
        internal readonly float SlopeStepDrop;
        internal readonly float Speed;
        internal readonly float Gravity;
        internal readonly float CapsuleHeight;
        internal readonly float CapsuleRadius;

        // DIE PROBE IST SCHLANKER ALS DER SPIELER, und das ist Absicht.
        //
        // Mit der Kapsel des Controllers gefragt (gemessen r 0,4 h 1,8) lautet
        // die Frage "steht hier ein voll aufgerichteter Spieler frei?" -
        // gebraucht wird "ist hier Platz zum Stehen?". Eine Treppenstufe
        // zwischen zwei Gelaendern beantwortet die erste Frage mit nein und die
        // zweite mit ja, und begehbar ist sie.
        //
        // Die letzten Zentimeter loest der Charaktercontroller des Spiels: er
        // schiebt heraus und rastet auf den Boden.
        internal readonly float ProbeRadius;
        internal readonly float ProbeHeight;

        // WIE HOCH DIE PROBE UEBER DEM ZIEL BEGINNT - Abschnitt 160, und das
        // ist der Grund, warum keine Treppe die Probe bestehen konnte.
        //
        // Die untere Kugel sass bei 0,02 m ueber dem Ziel. Eine ansteigende
        // Treppe von 33 Grad liegt in der Hoehe h nur 1,56*h waagerecht
        // entfernt - bei h 0,02 m sind das 3 cm, und die Kugel hat Radius
        // 0,22 m. Unterhalb von 0,14 m ueberlappt JEDER Radius ueber 3 cm.
        // Kein Zielpunkt auf einer Treppe konnte bestehen.
        //
        // Was unterhalb der Schritthoehe liegt, ist kein Hindernis, sondern
        // eine Stufe - dasselbe Prinzip wie im kletternden Gang.
        internal readonly float ProbeLift;
        internal readonly float RayLength;
        internal readonly float ReachFactor;
        internal readonly int Mask;
        internal readonly int Steps;

        // Kommen die Werte aus dem Spiel oder aus der Rueckfallebene? Eine
        // geratene Huelle, die sich als gemessene ausgibt, waere der
        // schlimmere Fehler - darum wandert die Antwort mit und steht im Log.
        internal readonly bool Measured;

        internal Envelope(float jumpHeight, float speed, float gravity,
            float capsuleHeight, float capsuleRadius,
            float rayLength, float reachFactor, int mask, int steps,
            bool measured, float probeRadius, float probeHeight,
            float probeLift,
            float riseCeiling, bool slopeWalk, float slopeSpacing,
            float slopeStepRise, float slopeStepDrop)
        {
            SlopeWalk = slopeWalk;
            SlopeSpacing = slopeSpacing;
            SlopeStepRise = slopeStepRise;
            SlopeStepDrop = slopeStepDrop;
            ProbeRadius = probeRadius;
            ProbeHeight = probeHeight;
            ProbeLift = probeLift;
            RiseCeiling = riseCeiling;
            JumpHeight = jumpHeight;
            Speed = speed;
            Gravity = gravity;
            CapsuleHeight = capsuleHeight;
            CapsuleRadius = capsuleRadius;
            RayLength = rayLength;
            ReachFactor = reachFactor;
            Mask = mask;
            Steps = steps;
            Measured = measured;
        }

        // Die Flachreichweite, fuer die eine Zeile, die gegen einen echten
        // Tastensprung gehalten werden kann.
        internal float FlatReach => Reach(this, 0f);
    }

    // ---------------------------------------------------------------- Mathe
    //
    // Reine Rechnung, keine Unity-Zustaende: dieselbe Funktion beantwortet
    // "wie weit" und "geht es ueberhaupt".
    //
    //     v0 = sqrt(2 g h)                     Absprung aus der Sprunghoehe
    //     t  = (v0 + sqrt(v0^2 - 2 g dy)) / g  Flugzeit bis zur Hoehe dy
    //     reach = s t                          waagerecht in dieser Zeit
    //
    // dy = 0   -> t = 2 v0 / g, die Normalsprungweite
    // dy < 0   -> laengere Flugzeit, GROESSERE Weite
    // dy > 0   -> kuerzere Flugzeit, KUERZERE Weite
    // dy > h   -> die Wurzel wird negativ, also UNMOEGLICH
    //
    // Die Hoehengrenze ist damit nicht aufgesetzt, sie ist die Definition.
    // Genau das war der Auftrag: hoeher als die Sprunghoehe waere das Spiel
    // kaputt.
    internal static float Reach(Envelope envelope, float dy)
    {
        var h = envelope.JumpHeight;
        var s = envelope.Speed;
        var g = envelope.Gravity;

        if (h <= 0f || s <= 0f || g <= 0.01f)
            return 0f;

        var v0 = Mathf.Sqrt(2f * g * h);
        var under = (v0 * v0) - (2f * g * dy);

        if (under < 0f)
            return 0f;

        var time = (v0 + Mathf.Sqrt(under)) / g;
        return s * time * Mathf.Max(0f, envelope.ReachFactor);
    }

    private static bool Cast(Vector3 origin, Vector3 direction, float length, int mask)
    {
        if (length <= 0.0001f)
            return false;

        try
        {
            return Physics.Raycast(origin, direction, length, mask, NoTriggers);
        }
        catch
        {
            // Ein Strahl, der wirft, ist kein Treffer. Stiller Fehlschlag ist
            // hier richtig: der Aufrufer sieht "kein Ziel" und der Marker
            // bleibt aus.
            return false;
        }
    }

    // Die Bisektion. steps Strahlen, jeder ein bool.
    //
    // Invariante: bei lo ist KEIN Treffer bekannt, bei hi einer. Trifft der
    // Strahl bis mid, liegt der erste Treffer in [lo, mid] - also hi = mid.
    // Trifft er nicht, liegt er in [mid, hi] - also lo = mid.
    //
    // Zurueckgegeben wird hi, die OBERE Schranke: lieber drei Millimeter zu
    // weit als drei zu kurz, denn zu kurz landet im Boden davor.
    internal static bool TryHitDistance(Vector3 origin, Vector3 direction,
        float maxLength, int mask, int steps, out float distance)
    {
        distance = 0f;

        if (!Cast(origin, direction, maxLength, mask))
            return false;

        var lo = 0f;
        var hi = maxLength;

        for (var index = 0; index < Mathf.Clamp(steps, 4, 24); index++)
        {
            var mid = (lo + hi) * 0.5f;

            if (Cast(origin, direction, mid, mask))
                hi = mid;
            else
                lo = mid;
        }

        distance = hi;
        return true;
    }

    // ====================================================================
    // DER WURFBOGEN - Abschnitt 149.
    //
    // Ein Steinwurf aus dem Controller:
    //
    //     p(t) = p0 + dir * V * t - 0.5 * g * t^2 * up
    //
    // V IST NICHT FREI GEWAEHLT, und die ABWURFHOEHE gehoert hinein.
    //
    // Der erste Anlauf nahm V = sqrt(reachFlat * g). Das gilt fuer einen Wurf,
    // der auf SEINER EIGENEN HOEHE landet - der Abwurf sitzt aber in der Hand,
    // rund 1,4 m ueber dem Fuss, und ein Wurf von oben traegt weiter.
    // Nachgerechnet vor dem Testlauf: 5,46 m statt 3,96 m, also achtunddreissig
    // Prozent zu weit und damit genau die Vorgabe verletzt.
    //
    // Fuer einen Wurf aus der Hoehe y0 auf y = 0 ist die groesste Weite ueber
    // alle Winkel
    //
    //     R_max = (V^2/g) * sqrt(1 + 2 g y0 / V^2)
    //
    // Mit u = V^2/g wird daraus u^2 + 2 y0 u - R^2 = 0, und damit
    //
    //     V = sqrt( g * ( sqrt(y0^2 + R^2) - y0 ) )
    //
    // Nachgerechnet fuer y0 = 0; 0,8; 1,4 und 2,0 m ist das Maximum jedes Mal
    // genau R, und bei y0 = 0 faellt es auf sqrt(R g) zurueck. Der optimale
    // Winkel wandert dabei von 45 auf 35 Grad - richtig und weiter bequem.
    //
    // Die Huelle aus Abschnitt 147 ist damit nicht ersetzt, sondern in eine
    // Geste uebersetzt - dieselbe Zahl, anders erfahrbar. Und der Gewinn ist
    // der, den der Auftraggeber genannt hat: die Grenze ist kein VERBOT mehr,
    // an dem der Marker rot wird und der Spieler den gueltigen Punkt suchen
    // muss, sondern PHYSIK. Weiter als das Maximum kann der Stein nicht
    // fliegen.
    //
    // KEINE WEITENPRUEFUNG UND KEINE WANDPROBE mehr, und das ist kein
    // Weglassen:
    //
    //   Die Weite kann der Bogen nicht ueberschreiten - sie steckt in V.
    //   Die Wand beendet den Wurf, weil JEDES SEGMENT einzeln geprueft wird;
    //   der Stein landet an der Wand statt hinter ihr.
    //
    // Die HOEHENGRENZE bleibt als harte Regel: steil geworfen kann der Bogen
    // auf einem Vorsprung landen, der hoeher liegt als ein Sprung reicht.
    //
    // DER ABWURF SITZT AM CONTROLLER und nicht am Fuss, weil genau das die
    // Geste ist. Das verschiebt die Weite um die Handlaenge nach vorn, rund
    // 0,4 m - hingenommen: die Beschwerde war das Suchen des gueltigen Punkts,
    // und ein etwas grosszuegiger Bogen bringt es nicht zurueck.
    internal bool ResolveArc(MelonLogger.Instance log, Envelope envelope,
        Vector3 origin, Vector3 forward, Vector3 foot,
        bool ladderAllowed, float ladderOffset, List<Vector3> path,
        out Vector3 target, out bool hasSurface, out string why)
    {
        target = foot;
        hasSurface = false;
        path.Clear();

        if (forward.sqrMagnitude < 1e-6f)
        {
            why = "no aim direction";
            return false;
        }

        var g = Mathf.Max(0.01f, envelope.Gravity);
        var reach = envelope.FlatReach;

        if (reach <= 0.01f)
        {
            why = $"no envelope (h {envelope.JumpHeight:0.00}  s {envelope.Speed:0.00})";
            return false;
        }

        // DIE ABWURFHOEHE UEBER DEM FUSS, gemessen und nicht angenommen: so
        // stimmt die Weite auch fuer den gehockten Spieler und fuer eine tief
        // gehaltene Hand. Unter dem Fuss - Hand nach unten an einer Kante -
        // wird auf null geklemmt, sonst waere die Wurzel kleiner als y0 und die
        // Geschwindigkeit negativ.
        var launchHeight = Mathf.Max(0f, origin.y - foot.y);

        var dir = forward.normalized;

        // DER WAAGERECHTE HANDVERSATZ, PROJIZIERT AUF DIE WURFRICHTUNG.
        //
        // Die Huelle gilt ab dem FUSS, der Wurf beginnt an der HAND. Steht die
        // Hand 0,3 m vor dem Fuss, landet der Stein 0,3 m weiter als erlaubt.
        //
        // Projiziert und nicht abgezogen: wer ueber die Schulter nach hinten
        // zielt, hat die Hand HINTER dem Ziel, und ein blosser Abzug wuerde den
        // Wurf dort zu kurz machen. Das Skalarprodukt traegt das Vorzeichen.
        var lead = new Vector3(origin.x - foot.x, 0f, origin.z - foot.z);
        var flatDir = new Vector3(dir.x, 0f, dir.z);

        var leadOffset = flatDir.sqrMagnitude > 1e-6f
            ? Vector3.Dot(lead, flatDir.normalized)
            : 0f;

        // Gedeckelt, damit eine weit vorgestreckte Hand den Wurf nicht auf
        // nichts zusammenzieht - ein halber Meter bleibt immer.
        var effective = Mathf.Max(0.5f, reach - leadOffset);

        var speed = Mathf.Sqrt(g * (Mathf.Sqrt((launchHeight * launchHeight)
            + (effective * effective)) - launchHeight));

        path.Add(origin);

        var previous = origin;
        var hit = origin;
        var steps = 0;

        // Zeitschritt und Deckel. 0,03 s ergibt bei den hier ueblichen
        // Geschwindigkeiten Segmente von rund 20 cm - fein genug, dass der
        // Bogen als Bogen aussieht, und grob genug, dass ein Zielvorgang unter
        // hundert Strahlen bleibt.
        const float Step = 0.03f;
        const float MaxTime = 4f;

        for (var time = Step; time <= MaxTime; time += Step)
        {
            var point = origin + (dir * (speed * time))
                - new Vector3(0f, 0.5f * g * time * time, 0f);

            var segment = point - previous;
            var length = segment.magnitude;
            steps++;

            if (length > 0.0001f)
            {
                var direction = segment / length;

                if (Cast(previous, direction, length, envelope.Mask))
                {
                    // INNERHALB DES SEGMENTS bisektiert, mit demselben
                    // monotonen bool-Strahl wie die gerade Variante. Ohne das
                    // waere der Landepunkt auf 20 cm genau, und ein Marker, der
                    // 20 cm neben der Kante liegt, ist eine falsche Zusage.
                    TryHitDistance(previous, direction, length, envelope.Mask,
                        envelope.Steps, out var exact);

                    hit = previous + (direction * exact);
                    hasSurface = true;

                    // AUF DIE MARKERMITTE, nicht auf den Boden: der Marker
                    // liegt um Lift hoeher, und ein Bogen, der darunter endet,
                    // sieht aus schraeger Sicht versetzt aus.
                    path.Add(hit + MarkerLead);
                    break;
                }
            }

            path.Add(point);
            previous = point;

            // Unter der Suchtiefe ist der Wurf verloren. Ohne diesen Ausstieg
            // laeuft der Bogen bis MaxTime ins Bodenlose und zeichnet einen
            // Schweif nach unten.
            //
            // GEGEN EINE KONSTANTE, nicht gegen eine Einstellung - siehe
            // TraceDrop. Hier stand envelope.MaxDrop, und ein veralteter Wert
            // in einer bestehenden cfg hat den Bogen mitten in der Luft
            // abgeschnitten.
            if (point.y < foot.y - TraceDrop)
                break;
        }

        if (!hasSurface)
        {
            why = $"the throw lands nowhere ({steps} segment(s), "
                + $"v {speed:0.0} m/s from {launchHeight:0.00} m)";
            return false;
        }

        target = hit;

        // Die Leiter zuerst - die EINE gewollte Ausnahme von der Hoehengrenze.
        if (ladderAllowed
            && TryLadderTop(log, hit, envelope.Mask, ladderOffset, out var top))
        {
            target = top;

            // DER BOGEN FUEHRT BIS ZUM OBERPUNKT. Ohne diese Zeile endet er an
            // der Sprosse, auf die gezeigt wurde, waehrend der Marker oben am
            // Leiterkopf sitzt - zwei Orte, und keiner sagt, dass sie
            // zusammengehoeren.
            path.Add(top + MarkerLead);

            why = $"ladder top   throw {steps} segment(s)";
            return true;
        }

        var dy = hit.y - foot.y;

        // GEGEN DIE KANTENGRENZE, nicht gegen die Sprunghoehe - Abschnitt 156.
        //
        // 150 Ablehnungen im gemeldeten Lauf lauteten "too high: rise 1.58 >
        // jump 1.50". Die Stufe war begehbar; die Grenze war die falsche
        // Groesse. Siehe Envelope.RiseCeiling.
        if (dy > envelope.RiseCeiling)
        {
            // ERST JETZT DER KLETTERNDE GANG - Abschnitt 159.
            //
            // Eine Treppe ist kein Sprungziel, sondern ein WEG: der Test auf
            // der Treppe hat gezeigt, dass auch ein laufender Spieler dort
            // nicht springen kann, weil die Steigung dem Absprungwinkel
            // entspricht. Die Sprunghuelle ist fuer Treppen also das falsche
            // Modell.
            //
            // Der Gang laeuft NUR HIER, wo die Huelle schon nein gesagt hat:
            // er kostet rund 96 Strahlen, und ein Messweg, der im haeufigen
            // Fall bezahlt wird, ist der falsche.
            if (envelope.SlopeWalk
                && WalkableSlope(envelope, foot, hit, out var walk))
            {
                if (!Fits(log, envelope, hit, out var slopeRoom))
                {
                    why = $"no room at {hit.y:0.00} m: {slopeRoom}";
                    return false;
                }

                target = hit;
                why = $"ok via slope   rise {dy:0.00} m over ceiling "
                    + $"{envelope.RiseCeiling:0.00}   {walk}";
                return true;
            }

            why = $"too high: rise {dy:0.00} m > ceiling "
                + $"{envelope.RiseCeiling:0.00} m (jump {envelope.JumpHeight:0.00})"
                + (envelope.SlopeWalk ? $"   no walkable slope" : "");
            return false;
        }

        // NACH UNTEN WIRD NICHT MEHR ABGESAGT - Abschnitt 154.
        //
        // Hier stand eine Absage bei zu tiefem Fall. Gemeldet war die Folge:
        // von einer Leiter kam ein Teleport-Spieler nicht mehr herunter, wenn
        // der Boden zu weit weg war. Uebernommen wurde der bessere Vorschlag -
        // die Beschraenkung gilt nur NACH OBEN.
        //
        // Das ersetzt auch einen Sonderfall, der sonst noetig gewesen waere:
        // ein eigener Weg zum Leiterfuss. MaxDrop ist nur noch die Suchtiefe
        // des Bogens, oben in der Schleife.
        if (!Fits(log, envelope, hit, out var room))
        {
            // DER GRUND NENNT DIE MASSE, weil genau sie die Frage
            // entscheiden: ein zu dicker Pruefkoerper lehnt begehbare Stufen
            // ab, und ohne die Zahlen ist das von einem echten Hindernis nicht
            // zu unterscheiden.
            why = $"no room at {hit.y:0.00} m (dy {dy:0.00}): {room}";
            return false;
        }

        var flat = new Vector3(hit.x - foot.x, 0f, hit.z - foot.z).magnitude;

        // DIE ABWURFHOEHE STEHT MIT DRIN, weil sie die Geschwindigkeit
        // bestimmt: weicht die gemessene Weite vom Maximum ab, ist sie die
        // erste Spalte, in der man nachsieht.
        why = $"ok   throw {flat:0.00} m at dy {dy:0.00}   "
            + $"v {speed:0.0} m/s from {launchHeight:0.00} m"
            + $"   lead {leadOffset:0.00} m   max {reach:0.00} m";
        return true;
    }

    // ====================================================================
    // DER KLETTERNDE GANG - Abschnitt 159.
    //
    // Beantwortet "fuehrt von hier ein gehbarer Weg dorthin?" als Folge kleiner
    // Schritte entlang der Waagerechten. Bei jedem Schritt wird der Boden
    // gesucht, der hoechstens StepRise UEBER und hoechstens StepDrop UNTER der
    // bisherigen Bezugshoehe liegt; wird er gefunden, wandert die Bezugshoehe
    // mit.
    //
    //     Treppe   0,00 - 0,18 - 0,36 - ...   jeder Schritt klein   ->  JA
    //     Rampe    stetig steigend                                  ->  JA
    //     Wand     0,00 - 0,00 - 0,00 - Ziel 2,00                    ->  NEIN
    //
    // DIE WAND SCHEITERT AM SCHRITT, nicht am Mittelwert. Eine mittlere
    // Steigung haette sie durchgelassen - 2 m auf 4 m Lauf sind 26 Grad. Der
    // Schritt ist das Tor, das wirkt.
    //
    // LUECKEN SIND ERLAUBT, und das ist keine Nachlaessigkeit: das gemeldete
    // Treppenhaus ist OFFEN, zwischen den Stufen ist Luft. Ein Schritt ohne
    // Boden im Band laesst die Bezugshoehe stehen, statt den Gang abzubrechen -
    // sonst waere jede offene Treppe unpassierbar.
    private static bool WalkableSlope(Envelope envelope, Vector3 foot,
        Vector3 target, out string detail)
    {
        var rise = Mathf.Max(0.05f, envelope.SlopeStepRise);
        var drop = Mathf.Max(0.05f, envelope.SlopeStepDrop);

        var footFlat = new Vector3(foot.x, 0f, foot.z);
        var targetFlat = new Vector3(target.x, 0f, target.z);
        var span = (targetFlat - footFlat).magnitude;

        if (span < 0.05f)
        {
            detail = "slope: no horizontal distance";
            return false;
        }

        // AUS DEM ABSTAND, nicht aus einer Anzahl: eine feste Anzahl ergibt bei
        // langer Strecke grobe Schritte, und grobe Schritte liessen die offene
        // Treppe durchfallen. Gedeckelt, damit ein weiter Wurf nicht beliebig
        // viele Strahlen kostet.
        var spacing = Mathf.Max(0.05f, envelope.SlopeSpacing);
        var samples = Mathf.Clamp(Mathf.RoundToInt(span / spacing), 3, 60);

        var reference = foot.y;
        var found = 0;

        for (var index = 1; index <= samples; index++)
        {
            var flat = Vector3.Lerp(footFlat, targetFlat, (float)index / samples);

            // Acht Bisektionsschritte ueber ein Band von rund einem Meter sind
            // 4 mm - genauer, als eine Stufe hoch ist, und ein Drittel der
            // Strahlen, die das Zielen selbst braucht.
            if (GroundInBand(flat, reference + rise, reference - drop,
                envelope.Mask, 8, out var height))
            {
                reference = height;
                found++;
            }
        }

        var reachable = target.y <= reference + rise;

        detail = $"slope {found}/{samples} on ground (spacing {spacing:0.##} m), "
            + $"walked {foot.y:0.00} -> {reference:0.00} m, "
            + $"target {target.y:0.00} m, step {rise:0.##}/{drop:0.##}, "
            + $"span {span:0.00} m";

        return reachable;
    }

    // Die Bodenhoehe an einer Stelle, INNERHALB eines Bandes.
    //
    // Das Band ist der Kern: ein Strahl von oben nach unten trifft sonst den
    // tiefen Hallenboden unter einer offenen Treppe, und daraus wuerde ein
    // Absturz statt einer Stufe. Wer nichts im Band findet, findet nichts.
    private static bool GroundInBand(Vector3 flat, float from, float to,
        int mask, int steps, out float height)
    {
        height = 0f;

        var length = from - to;

        if (length <= 0.001f)
            return false;

        var start = new Vector3(flat.x, from, flat.z);

        if (!TryHitDistance(start, Vector3.down, length, mask, steps, out var distance))
            return false;

        height = from - distance;
        return true;
    }

    // Die Kapselprobe, aus Resolve herausgezogen, weil beide Wege sie
    // brauchen. Sie tut zwei Dinge: sie fragt, ob der Spieler dort hinpasst,
    // und sie ersetzt die fehlende NORMALE - ein Treffer auf einer senkrechten
    // Flaeche liegt IN der Geometrie, eine Kapsel darum ueberlappt.
    // ZWEI BAENDER, EIN ERGEBNIS UND EINE DIAGNOSE - Abschnitt 160.
    //
    // Gefragt wird nur noch das OBERE Band, ab ProbeLift ueber dem Ziel. Das
    // untere wird trotzdem gemessen und weitergegeben, damit die Absage sagen
    // kann, welches Band ueberlappt hat.
    //
    // Warum das kein Nachlassen ist: eine Wand reicht von 0 bis 3 m und wird
    // oben genauso getroffen. Was wegfaellt, ist allein das Verbot, knietief
    // etwas neben sich zu haben - und genau dort schiebt der
    // Charaktercontroller des Spiels heraus, wie er es beim GEHEN auf derselben
    // Treppe tut.
    private static bool Fits(MelonLogger.Instance log, Envelope envelope,
        Vector3 point, out string detail)
    {
        // DIE SCHLANKE PROBE, nicht die Kapsel des Controllers - siehe
        // Envelope.ProbeRadius. Faellt einer der beiden Werte aus, gilt der
        // Controllerwert als Rueckfallebene.
        var radius = Mathf.Max(0.05f, envelope.ProbeRadius > 0.01f
            ? envelope.ProbeRadius
            : envelope.CapsuleRadius);

        var height = Mathf.Max(radius * 2f + 0.05f,
            envelope.ProbeHeight > 0.05f
                ? envelope.ProbeHeight
                : envelope.CapsuleHeight);

        // Der Lift ist nach unten auf die Kugel begrenzt: darunter gibt es
        // kein Band, sondern nur eine tiefer gelegte Kugel.
        var lift = Mathf.Max(radius + 0.02f, envelope.ProbeLift);

        // Das OBERE Band - Kopf und Rumpf. Nur dieses entscheidet.
        var upperFoot = point + new Vector3(0f, lift, 0f);
        var upperHead = point + new Vector3(0f,
            Mathf.Max(lift + 0.05f, lift + height - radius), 0f);

        // Das UNTERE Band - Schienbeinhoehe. Wird gemessen, nicht gewertet.
        var lowerFoot = point + new Vector3(0f, radius + 0.02f, 0f);
        var lowerHead = point + new Vector3(0f, lift, 0f);

        try
        {
            // OHNE die zusaetzliche Verkleinerung um 5 Prozent: der
            // Pruefradius ist jetzt ausdruecklich eingestellt, und eine
            // versteckte zweite Verkleinerung macht aus einer eingestellten
            // Zahl eine andere.
            var upperBlocked = Physics.CheckCapsule(upperFoot, upperHead,
                radius, envelope.Mask, NoTriggers);

            // Die untere Frage kostet eine Kapselprobe und ist der Grund,
            // warum die naechste Absage ihre Ursache selbst nennt. Eine
            // Ablehnung, die nur "no room" sagt, hat einen ganzen Lauf
            // gekostet.
            var lowerBlocked = lowerHead.y - lowerFoot.y > 0.01f
                && Physics.CheckCapsule(lowerFoot, lowerHead, radius,
                    envelope.Mask, NoTriggers);

            detail = $"probe r {radius:0.##} lift {lift:0.##} "
                + $"band {lift:0.##}-{upperHead.y - point.y + radius:0.##} m   "
                + (upperBlocked ? "UPPER blocked" : "upper clear")
                + ", " + (lowerBlocked ? "lower blocked (a step)" : "lower clear");

            return !upperBlocked;
        }
        catch (Exception exception)
        {
            // Die Probe nicht BESTANDEN ist eine Absage, die Probe nicht
            // DURCHGEFUEHRT ist keine. Ein Fehlschlag des Messwegs darf den
            // Teleport nicht verbieten.
            log.Warning("  teleport: capsule check threw "
                + exception.GetType().Name + "; taking the target as it is");
            detail = "probe threw " + exception.GetType().Name;
            return true;
        }
    }

    // ------------------------------------------------------------ Gueltigkeit
    //
    // HIER STAND DER GERADE STRAHL - Abschnitt 157, und er ist weg.
    //
    // Er war die Rueckfallebene fuer TeleportArc = false, und niemand hat das
    // je gesetzt. Dreimal in drei Abschnitten hat er darum eine Abweichung
    // gesammelt, die nur durch Zufall auffiel:
    //
    //     155   die Kapselprobe stand zweimal
    //     156   die Hoehenpruefung stand zweimal
    //     157   die Absage "too deep" stand nur noch hier
    //
    // Beim dritten Mal ist das keine Panne, sondern eine Eigenschaft: ein Pfad,
    // den niemand benutzt, wird geaendert, wenn man daran denkt, und vergessen,
    // wenn nicht. Als Sicherheit war er damit keine - er war eine zweite,
    // ungetestete Wahrheit.
    //
    // ResolveArc ist der einzige Weg, und TryHitDistance bleibt: die Bisektion
    // aus dem Dateikopf arbeitet jetzt innerhalb der Bogensegmente.

    // Die Leiter am Trefferpunkt.
    //
    // OHNE RaycastHit gibt es keinen Collider zum Treffer, also wird um den
    // Punkt herum gesucht. OverlapSphere gibt einen Il2CppReferenceArray von
    // Collider zurueck - Klassenreferenzen, die sichere Form.
    //
    // ========================================================================
    // DIES IST EINE HYPOTHESE MIT EINGEBAUTER MESSUNG, KEINE ZUSICHERUNG.
    //
    // get_ClimbableTop ist nativ Public_Virtual_Final_New, also lesbar. Was
    // KEINE Signatur sagt: ob der Wert gefuellt ist, ob er in Weltkoordinaten
    // steht, und ob der Punkt begehbar ist oder in der Wand haengt.
    //
    // Darum nennt die Zeile unten alle vier Groessen, aus denen der Offset
    // abzulesen ist. EIN Lauf entscheidet ihn; geraten wird er nicht. Traegt
    // der Treffer keine Leiter, bleibt alles beim normalen Pfad - der Ausfall
    // kostet die Leiter, nicht den Teleport.
    private bool TryLadderTop(MelonLogger.Instance log, Vector3 hit, int mask,
        float offset, out Vector3 top)
    {
        top = hit;

        try
        {
            var found = Physics.OverlapSphere(hit, 0.35f, mask, NoTriggers);

            if (found is null)
                return false;

            for (var index = 0; index < found.Length; index++)
            {
                var collider = found[index];

                if (collider is null || collider == null)
                    continue;

                var ladder = collider
                    .GetComponentInParent<Il2CppFuturLab.PW2.Ladder>();

                if (ladder is null || ladder == null)
                    continue;

                var climbTop = ladder.ClimbableTop;
                var climbForward = ladder.ClimbableForward;
                var climbBottom = ladder.ClimbableBottom;

                // Weg von der Wand: ClimbableForward zeigt bei einer
                // angelehnten Leiter in die Flaeche, an der geklettert wird.
                // Der Spieler soll AUF dem Dach stehen, nicht an der Sprosse
                // haengen.
                var flat = new Vector3(climbForward.x, 0f, climbForward.z);

                if (flat.sqrMagnitude > 1e-6f)
                    flat = flat.normalized;
                else
                    flat = Vector3.zero;

                top = climbTop - (flat * offset);

                log.Msg($"teleport: ladder \"{ladder.name}\"   "
                    + $"top ({climbTop.x:0.00}, {climbTop.y:0.00}, {climbTop.z:0.00})   "
                    + $"bottom ({climbBottom.x:0.00}, {climbBottom.y:0.00}, "
                    + $"{climbBottom.z:0.00})   "
                    + $"forward ({climbForward.x:0.00}, {climbForward.y:0.00}, "
                    + $"{climbForward.z:0.00})   "
                    + $"offset {offset:0.##} m   "
                    + $"target ({top.x:0.00}, {top.y:0.00}, {top.z:0.00})");

                return true;
            }
        }
        catch (Exception exception)
        {
            log.Warning($"  teleport: ladder lookup threw "
                + $"{exception.GetType().Name}: {exception.Message}");
        }

        return false;
    }

    // ---------------------------------------------------------------- Marker

    internal void Show(MelonLogger.Instance log, Vector3 target, bool valid,
        float size, Color tint, int gridCells, float fillAlpha)
    {
        if (!Ensure(log, gridCells, fillAlpha))
            return;

        try
        {
            holder!.transform.position = target + new Vector3(0f, Lift, 0f);

            // Flach auf den Boden gelegt: ein World-Space-Canvas zeigt lokal
            // nach +Z, und -90 Grad um X dreht das nach oben. Mit +90 zeigte
            // die Flaeche nach unten und waere unsichtbar - einmal
            // nachgerechnet statt einmal geraten.
            holder.transform.rotation = Quaternion.Euler(-90f, 0f, 0f);

            var scale = Mathf.Max(0.05f, size) / CanvasWidth;
            holder.transform.localScale = new Vector3(scale, scale, scale);

            if (markerImage is not null && markerImage != null)
            {
                // ROT SCHLAEGT DIE AUSWAHL, und nur dafuer gibt es hier eine
                // zweite Farbe: eine Absage muss eindeutig bleiben.
                //
                // ALPHA EINS, und das ist der ganze Trick: die Textur
                // entscheidet, was halbtransparent ist (Fuellung) und was opak
                // (Linien und Ring). Eine Farbe mit Alpha unter eins wuerde
                // BEIDES abdunkeln und die Linien ihre Deckung nehmen.
                markerImage.color = valid ? tint : new Color(1f, 0.3f, 0.25f, 1f);
            }

            if (!holder.activeSelf)
                holder.SetActive(true);

            visible = true;
        }
        catch (Exception exception)
        {
            log.Warning($"  teleport marker threw {exception.GetType().Name}: "
                + exception.Message);
            Drop();
        }
    }

    internal void Hide()
    {
        if (!visible)
            return;

        visible = false;

        try
        {
            if (holder is not null && holder != null && holder.activeSelf)
                holder.SetActive(false);
        }
        catch
        {
            Drop();
        }
    }

    private void Drop()
    {
        // ALLE Felder auf dasselbe Objekt, nicht nur eines: eine halb
        // geraeumte Huelle baut beim naechsten Versuch einen zweiten Marker
        // neben den ueberlebenden ersten.
        holder = null;
        markerImage = null;
        visible = false;
    }

    private bool Ensure(MelonLogger.Instance log, int gridCells, float fillAlpha)
    {
        if (buildFailed)
            return false;

        // DIE TEXTUR HAENGT AN ZWEI EINSTELLUNGEN, also muss eine Aenderung sie
        // neu erzeugen - sonst waeren beide Regler stumme Schalter. Dieselbe
        // Vorkehrung, die die Vignette fuer ihren Innenradius traegt.
        var stale = markerTexture is null || markerTexture == null
            || bakedCells != gridCells
            || !Mathf.Approximately(bakedFill, fillAlpha);

        if (holder is not null && holder != null && !stale)
            return true;

        if (stale && markerTexture is not null && markerTexture != null)
        {
            UnityEngine.Object.Destroy(markerTexture);
            markerTexture = null;
        }

        if (!Bake(log, gridCells, fillAlpha))
            return false;

        if (holder is not null && holder != null)
        {
            if (markerImage is not null && markerImage != null)
                markerImage.texture = markerTexture;

            return true;
        }

        // Unity-null: eine zerstoerte Huelle ist kein Nullzeiger.
        Drop();

        try
        {
            holder = new GameObject(HolderName);
            holder.layer = 0;

            var canvas = holder.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            // Unter der Vignette und der Blende (32500), ueber der Spiel-UI:
            // der Marker liegt in der Welt und soll von einer Blende verdeckt
            // werden, nicht umgekehrt.
            canvas.sortingOrder = 32100;

            var rect = holder.GetComponent<RectTransform>();

            if (rect is not null && rect != null)
                rect.sizeDelta = new Vector2(CanvasWidth, CanvasWidth);

            var group = holder.AddComponent<CanvasGroup>();
            group.alpha = 1f;
            group.interactable = false;
            group.blocksRaycasts = false;

            var child = new GameObject("Marker");
            child.layer = 0;
            child.transform.SetParent(holder.transform, false);

            markerImage = child.AddComponent<RawImage>();
            markerImage.raycastTarget = false;
            markerImage.texture = markerTexture;

            var childRect = child.GetComponent<RectTransform>();

            if (childRect is not null && childRect != null)
            {
                childRect.anchorMin = Vector2.zero;
                childRect.anchorMax = Vector2.one;
                childRect.offsetMin = Vector2.zero;
                childRect.offsetMax = Vector2.zero;
            }

            if (!loggedOnce)
            {
                loggedOnce = true;
                log.Msg("  teleport marker: canvas built, world space, layer 0, "
                    + $"{Lift * 100f:0.#} cm above the ground   "
                    + "grid is MARKER-anchored, not world-anchored - a world "
                    + "grid needs a shader, see the file header");
            }

            return true;
        }
        catch (Exception exception)
        {
            log.Warning("  teleport marker build threw "
                + $"{exception.GetType().Name}: {exception.Message}");
            buildFailed = true;
            return false;
        }
    }

    // EINE Textur: drei Kreise, Fuellung, Raster und der Mittelpunkt, mit der
    // Alpha als Traeger des Unterschieds. Erzeugt statt mitgeliefert - aus
    // demselben Grund wie bei der Vignette: eine eingebettete PNG waere eine
    // Datei, ein LogicalName und ein stiller Fehlschlagweg mehr.
    //
    // WEISS MIT VARIABLER ALPHA, und darum traegt EINE Textur alle Farben:
    // RawImage.color multipliziert sie. Eine fuenfte Farbe waere ohne Aufwand.
    private bool Bake(MelonLogger.Instance log, int gridCells, float fillAlpha)
    {
        try
        {
            var baked = new Texture2D(TextureSize, TextureSize,
                TextureFormat.RGBA32, false);

            // Clamp, nicht Repeat: diese Textur kachelt nicht, und Repeat
            // wuerde die weiche Aussenkante gegenueber wieder einblenden.
            baked.wrapMode = TextureWrapMode.Clamp;
            baked.filterMode = FilterMode.Bilinear;

            var half = (TextureSize - 1) * 0.5f;
            var cells = Mathf.Clamp(gridCells, 2, 32);
            var fill = Mathf.Clamp01(fillAlpha);

            // Halbe Linienbreite, aus Bildpunkten in Zellen umgerechnet.
            var lineHalfCells = GridLineHalfTexels / ((float)TextureSize / cells);

            // Kantenweichheit in RADIUSANTEILEN: der Radius laeuft von 0 bis 1
            // ueber die halbe Texturbreite, ein Bildpunkt ist also
            // 2/TextureSize. 1,2 Bildpunkte sind genug gegen Treppenstufen und
            // zu wenig, um als Verlauf zu wirken.
            var edge = 1.2f * 2f / TextureSize;

            for (var y = 0; y < TextureSize; y++)
            {
                for (var x = 0; x < TextureSize; x++)
                {
                    var dx = (x - half) / half;
                    var dy = (y - half) / half;
                    var radius = Mathf.Sqrt((dx * dx) + (dy * dy));

                    // Ausserhalb: nichts. DAS macht die Form zu einem Kreis -
                    // nicht eine Maske, die es nicht tat.
                    if (radius > OuterRingOut + edge)
                    {
                        baked.SetPixel(x, y, new Color(1f, 1f, 1f, 0f));
                        continue;
                    }

                    var alpha = 0f;

                    // Die Fuellung, halbtransparent, bis zum Innenring.
                    if (fill > 0f)
                        alpha = fill * (1f - Edge(GridRadius - edge,
                            GridRadius + edge, radius));

                    // DAS RASTER, OPAK. Nur innerhalb des Innenrings, damit es
                    // seine Kante nicht aufweicht.
                    if (radius < GridRadius - edge)
                    {
                        // AB DER MITTE GERECHNET, nicht ab der Kante - und
                        // das ist der Unterschied zwischen zentriert und
                        // zufaellig zentriert.
                        //
                        // Vorher lief u von 0 an der linken Kante bis cells an
                        // der rechten, Zellgrenzen bei ganzen Zahlen. Ob dann
                        // eine Linie durch die MITTE geht, haengt an der
                        // PARITAET: bei 8 Zellen ja, bei 13 nicht. Das Vorbild
                        // zeigt eine Linie durch den Mittelpunkt, und die soll
                        // nicht davon abhaengen, welche Zellzahl eingestellt
                        // ist.
                        //
                        // u = dx * cells / 2 legt eine Grenze auf u = 0, also
                        // genau in die Mitte. Die Dichte bleibt dieselbe: von
                        // -cells/2 bis +cells/2 sind es weiter cells Zellen.
                        var u = dx * 0.5f * cells;
                        var v = dy * 0.5f * cells;

                        var du = Mathf.Abs(u - Mathf.Round(u));
                        var dv = Mathf.Abs(v - Mathf.Round(v));

                        // WEICHE KANTE UNTER EINEM BILDPUNKT, statt eines
                        // harten Schwellentests - und das behebt einen
                        // sichtbaren Fehler.
                        //
                        // In der Vorschau war EINE Linie heller als die
                        // anderen: an der Texturmitte liegen zwei Texel gleich
                        // weit von der Zellgrenze, also erfuellten beide die
                        // Schwelle, waehrend andere Linien nur einen Texel
                        // trafen. Doppelte Breite an genau einer Stelle liest
                        // sich als Fehler im Bild.
                        //
                        // Der KERN bleibt voll deckend - die Linie ist weiter
                        // opak, wie gefordert -, nur die aeusseren 30 Prozent
                        // laufen aus. Damit haben alle Linien dasselbe
                        // Gewicht, unabhaengig davon, wo sie im Texelraster
                        // liegen.
                        var line = 1f - Edge(lineHalfCells * 0.7f,
                            lineHalfCells * 1.3f, Mathf.Min(du, dv));

                        alpha = Mathf.Max(alpha, line);
                    }

                    // DREI KREISE, von innen nach aussen. Jeder ein Band mit
                    // Plateau; die Luecken dazwischen bleiben frei und machen
                    // aus einem breiten Band mehrere Kreise.
                    alpha = Mathf.Max(alpha,
                        Band(InnerRingIn, InnerRingOut, edge, radius));
                    alpha = Mathf.Max(alpha,
                        Band(ThickRingIn, ThickRingOut, edge, radius));
                    alpha = Mathf.Max(alpha,
                        Band(OuterRingIn, OuterRingOut, edge, radius));

                    // Der Mittelpunkt, zuletzt: er sagt, wohin genau der Fuss
                    // kommt, und darf von nichts uebermalt werden.
                    alpha = Mathf.Max(alpha,
                        1f - Edge(CentreDot - edge, CentreDot + edge, radius));

                    baked.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(alpha)));
                }
            }

            baked.Apply(false, false);

            markerTexture = baked;
            bakedCells = cells;
            bakedFill = fill;

            log.Msg($"  teleport marker texture: {TextureSize}x{TextureSize}, "
                + $"{cells} grid cells, line {GridLineHalfTexels * 2f:0.#} texels, "
                + $"fill {fill:0.##}, three rings");

            return true;
        }
        catch (Exception exception)
        {
            log.Warning("  teleport marker texture threw "
                + $"{exception.GetType().Name}: {exception.Message}");
            buildFailed = true;
            return false;
        }
    }

    internal void Dispose(MelonLogger.Instance log)
    {
        visible = false;
        loggedOnce = false;

        try
        {
            if (holder is not null && holder != null)
                UnityEngine.Object.Destroy(holder);

            // Die Textur ist hier entstanden, also gehoert ihre Freigabe
            // hierher - Destroy(holder) nimmt sie nicht mit.
            if (markerTexture is not null && markerTexture != null)
                UnityEngine.Object.Destroy(markerTexture);
        }
        catch (Exception exception)
        {
            log.Warning("  teleport marker teardown threw "
                + exception.GetType().Name);
        }

        holder = null;
        markerTexture = null;
        markerImage = null;
        bakedCells = -1;
        bakedFill = float.NaN;
    }
}
