using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace WetReality;

// Die Komfort-Vignette und die Teleport-Blende - Abschnitt 147.
//
// Beide sind kopfgefuehrte schwarze Flaechen und teilen darum EINEN Canvas.
// Getrennte Objekte waeren zwei Canvasse, zwei Nachfuehrungen pro Frame und
// zwei Stellen, an denen ein Aufraeumen fehlen kann.
//
// DER BAU FOLGT Splash.cs, und zwar bis zur Begruendung: ein World-Space-Canvas
// mit RawImage, weil ein Quad ein Material braucht und ein Material einen
// Shader - und Shader.Find ist in einem gestrippten IL2CPP-Build unzuverlaessig
// (WashLaser traegt genau dafuer eine Rueckfallebene). Ein CanvasRenderer holt
// sich das Standard-UI-Material bei Unity selbst.
//
// PRO FRAME NEU GESETZT, NICHT AN DIE KAMERA GEHAENGT. Dieselbe Begruendung wie
// in Splash.cs Zeile 122: die Kamera wird bei Szenenwechseln neu gebaut, und
// eine angehaengte Flaeche wuerde mit ihr zerstoert.
//
// ------------------------------------------------------------------------
// WARUM HIER KEIN ZTEST GESCHRIEBEN WIRD, und das ist eine Entscheidung gegen
// den naheliegenden Weg:
//
// UiDepth.ForceAlways gibt es, und GameUi benutzt es fuer die Spiel-UI. Hier
// waere es eine FALLE. Unitys Standard-UI-Material ist EINES fuer hunderte
// Graphics - GameUi.cs Zeile 88 sagt das ausdruecklich -, und ein ZTest Always
// darauf wuerde die UI des Spiels mitverstellen. Der Ausweg waere eine eigene
// Materialinstanz, also ein weiterer Interop-Aufruf und ein weiteres Objekt,
// das freigegeben werden muss.
//
// Der billigere Weg ist die GEOMETRIE: bei 0,32 m vor dem Auge liegt diese
// Flaeche naeher als praktisch alles in der Szene, und die Spielerkapsel
// verhindert, dass eine Wand so nah kommt. Damit zeichnet sie ohne jeden
// Eingriff vorn.
//
// DER EINE FALL, DER BLEIBT: die Waschpistole in der Hand kann naeher als
// 0,32 m ans Auge kommen und zeichnet dann ueber die Vignette. Das ist in der
// Bildmitte und die Vignette wirkt am Rand - hingenommen, nicht uebersehen.
internal sealed class Vignette
{
    private const string HolderName = "WetRealityVignette";

    // Canvas-Einheiten. Die Umrechnung in Meter macht localScale, das hier ist
    // nur die innere Aufloesung des Rects.
    private const float CanvasWidth = 1000f;

    // WIE WEIT DIE HALBE KANTE VOM BLICKSTRAHL WEGSTEHT, als WINKEL und nicht
    // als Breite.
    //
    // Eine feste Breite ist nur fuer einen festen Abstand richtig: 2,2 m bei
    // 0,32 m deckt 73,8 Grad, bei 1 m Abstand nur noch 47,7 - und damit
    // weniger als das Sichtfeld. Quest 3 zeigt je Auge rund 48 Grad horizontal
    // und 45 vertikal, also deckt 70 Grad das Feld bei JEDEM Abstand mit
    // Reserve.
    private const float HalfAngleDegrees = 70f;

    // Sicherheitsabstand hinter der Near-Plane. Eine Flaeche GENAU auf ihr
    // wird zur Haelfte weggeschnitten, was als flackernder Rand erscheint.
    private const float NearClipMargin = 0.06f;

    // Kantenlaenge der erzeugten Textur. 128 reicht fuer einen weichen
    // Radialverlauf - gezeigt wird sie unscharf am Rand des Blickfelds, nicht
    // als Bild.
    private const int TextureSize = 128;

    private GameObject? holder;
    private Texture2D? texture;
    private RectTransform? rect;
    private RawImage? ring;
    private RawImage? plate;

    private bool buildFailed;
    private bool loggedOnce;

    // Der geglaettete Stand, nicht das Ziel. Ohne ihn springt die Vignette mit
    // jedem Stickausschlag, und eine springende Vignette ist schlimmer als
    // keine.
    private float current;

    // Mit welchem Innenradius die Textur erzeugt wurde. Aendert der Spieler
    // ihn in der cfg, wird neu erzeugt - sonst haette der Regler keine Wirkung,
    // und das waere ein stummer Schalter.
    private float bakedInner = float.NaN;

    // Der sichtbare Radiusanteil, mit dem die Textur erzeugt wurde. Aendert er
    // sich - anderes Sichtfeld, andere Brille -, gehoert sie neu erzeugt.
    private float bakedVisible = float.NaN;

    // Das gemessene vertikale Sichtfeld, nur fuer die Logzeile. Es ist die
    // Zahl, die "die Rampe sitzt richtig" von "sie sitzt wieder daneben"
    // trennt.
    private float fieldOfView;

    private float blinkStart = -1f;
    private float blinkSpan;

    // Der Abstand, mit dem zuletzt gezeichnet wurde, und die Near-Plane, gegen
    // die er gedeckelt wurde. Nur fuer die eine Logzeile - sie ist das, was
    // einen zu nahen Abstand von einem unsichtbaren Canvas unterscheidet.
    private float usedDistance;
    private float nearClip;

    // Von der Teleport-Route gerufen. Die Blende laeuft AUCH ohne
    // eingeschaltete Vignette - sie gehoert zum Teleport, nicht zum Komfort.
    internal void Blink(float seconds)
    {
        if (seconds <= 0.001f)
            return;

        blinkStart = Time.unscaledTime;
        blinkSpan = seconds;
    }

    // target ist 0 bis 1 und kommt von der Bewegung; strength deckelt, wie
    // schwarz es maximal wird.
    //
    // OHNE STATUS-ZEICHENKETTE, und das ist Absicht. Die erste Fassung gab
    // eine zurueck und setzte eine Eigenschaft - pro Frame formatiert,
    // solange die Vignette sichtbar ist, und von niemandem gelesen. Der
    // offene Posten "Die acht Status-Strings pro Frame" nennt genau diese
    // Kosten; eine neunte waere die falsche Richtung. Was zu sagen ist, sagt
    // eine Zeile beim Bau.
    internal void Tick(MelonLogger.Instance log, bool enabled, float target,
        float strength, float inner, float fadeIn, float fadeOut, float distance)
    {
        var blinking = blinkStart >= 0f
            && Time.unscaledTime - blinkStart < blinkSpan;

        if (!blinking)
            blinkStart = -1f;

        // Ziel zuerst, Bau danach: solange nichts zu zeigen ist und auch nichts
        // mehr nachklingt, wird kein Canvas gebaut. Eine ausgeschaltete
        // Vignette kostet damit genau diesen Vergleich.
        var want = enabled ? Mathf.Clamp01(target) * Mathf.Clamp01(strength) : 0f;

        var rate = want > current ? fadeIn : fadeOut;

        current = rate <= 0.001f
            ? want
            : Mathf.MoveTowards(current, want, Time.unscaledDeltaTime / rate);

        if (current <= 0.001f && !blinking)
        {
            current = 0f;

            if (holder is not null && holder != null && holder.activeSelf)
                holder.SetActive(false);

            return;
        }

        var camera = Camera.main;

        // Keine Kamera ist kein Fehler, sondern der normale Zustand einen Frame
        // nach einem Szenenwechsel.
        if (camera is null || camera == null)
            return;

        // DIE NEAR-PLANE WIRD GELESEN, NICHT ANGENOMMEN.
        //
        // Das war die Ursache, nach der gesucht wurde: ein Canvas naeher als
        // die Near-Plane ist gebaut, positioniert, korrekt eingefaerbt - und
        // vollstaendig weggeschnitten. Kein Fehler, keine Warnung, nichts zu
        // sehen.
        nearClip = 0.01f;

        try
        {
            nearClip = camera.nearClipPlane;
        }
        catch
        {
            // Unlesbar ist kein Grund, nichts zu zeichnen - dann gilt der
            // gewuenschte Abstand unveraendert.
        }

        usedDistance = Mathf.Max(distance, nearClip + NearClipMargin);

        // ================================================================
        // WIE WEIT DAS BLICKFELD IN DIESE FLAECHE HINEINREICHT.
        //
        // Das war der Defekt: die Flaeche deckt HalfAngleDegrees, die Rampe
        // stand aber in Flaecheneinheiten. Bei 70 Grad Flaeche und 90 Grad
        // Sichtfeld reicht das Sichtbare nur bis r = 0,36 - und die Rampe
        // begann bei 0,55. Die ganze Vignette lag ausserhalb.
        //
        // Gerechnet statt angenommen: r = tan(FOV/2) / tan(HalfAngle).
        fieldOfView = 90f;

        try
        {
            var read = camera.fieldOfView;

            // Ein absurder Wert ist kein Messwert. Unter 20 oder ueber 170
            // Grad ist die Kamera nicht die, die hier gemeint ist.
            if (read > 20f && read < 170f)
                fieldOfView = read;
        }
        catch
        {
            // Unlesbar heisst: mit 90 Grad weiterrechnen, nicht aufgeben.
        }

        var visible = Mathf.Clamp(
            Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad)
                / Mathf.Tan(HalfAngleDegrees * Mathf.Deg2Rad),
            0.1f, 1f);

        if (!Ensure(log, inner, visible))
            return;


        var eye = camera.transform;
        holder!.transform.position = eye.position + (eye.forward * usedDistance);
        holder.transform.rotation = eye.rotation;

        // Die Breite folgt dem Abstand, damit die Winkelabdeckung gleich
        // bleibt. Ohne das waere ein groesserer Abstand ein Fleck in der Mitte
        // statt einer Vignette am Rand.
        var width = 2f * usedDistance * Mathf.Tan(HalfAngleDegrees * Mathf.Deg2Rad);
        var scale = width / CanvasWidth;
        holder.transform.localScale = new Vector3(scale, scale, scale);

        if (ring is not null && ring != null)
            ring.color = new Color(0f, 0f, 0f, current);

        // DREIECK, nicht Rampe: die Blende geht schnell nach schwarz und
        // genauso schnell zurueck. Eine Blende, die langsam aufblendet, wuerde
        // den Sprung zeigen, den sie verdecken soll.
        var blinkAlpha = 0f;

        if (blinking && blinkSpan > 0.001f)
        {
            var phase = (Time.unscaledTime - blinkStart) / blinkSpan;
            blinkAlpha = 1f - Mathf.Abs((phase * 2f) - 1f);
        }

        if (plate is not null && plate != null)
            plate.color = new Color(0f, 0f, 0f, Mathf.Clamp01(blinkAlpha));

        if (!loggedOnce)
        {
            loggedOnce = true;
            // DIE NEAR-PLANE STEHT MIT DRIN, und sie ist der Grund fuer
            // diese Zeile: sie trennt "zu nah, also weggeschnitten" von
            // "gezeichnet, aber unsichtbar". Ohne sie hat der erste Lauf
            // genau diese Frage offen gelassen.
            // DAS SICHTFELD UND DER SICHTBARE ANTEIL STEHEN MIT DRIN, und
            // sie sind der Kern: in Abschnitt 149 fehlten genau diese zwei
            // Zahlen, und darum hat der Lauf die Near-Plane bestaetigt statt
            // die Ursache zu nennen. Sitzt die Rampe wieder daneben, ist es
            // hier ablesbar und nicht zu erraten.
            log.Msg($"  vignette: {TextureSize}x{TextureSize} texture, "
                + $"{width:0.##} m wide at {usedDistance:0.##} m "
                + $"(asked {distance:0.##}, nearClip {nearClip:0.###}), "
                + $"fov {fieldOfView:0.#} deg -> visible to r {visible:0.000}, "
                + $"ramp {inner * visible:0.000} to {visible:0.000}, "
                + $"camera {camera.name}, layer {holder.layer}");
        }
    }

    // ------------------------------------------------------------------ build

    private bool Ensure(MelonLogger.Instance log, float inner, float visible)
    {
        if (buildFailed)
            return false;

        // Der Innenradius sitzt IN der Textur, also muss eine Aenderung sie neu
        // erzeugen. Unity-null mit geprueft: eine zerstoerte Textur ist kein
        // Nullzeiger.
        if (texture is null || texture == null
            || !Mathf.Approximately(bakedInner, inner)
            || !Mathf.Approximately(bakedVisible, visible))
        {
            if (!Bake(log, inner, visible))
                return false;

            if (ring is not null && ring != null)
                ring.texture = texture;
        }

        if (holder is not null && holder != null)
        {
            if (!holder.activeSelf)
                holder.SetActive(true);

            return true;
        }

        // Eine zerstoerte Huelle ist nicht null. Derselbe Unity-null-Fall, der
        // dieses Projekt schon vier Diagnosen gekostet hat - und ALLE Felder
        // werden geraeumt, nicht nur das eine.
        holder = null;
        rect = null;
        ring = null;
        plate = null;

        try
        {
            // Ebene 0. PlayerCamera traegt die Default-Ebene in ihrer
            // cullingMask, also ist kein Ebenen-Jonglieren noetig - dieselbe
            // Begruendung wie in Splash und WashLaser.
            holder = new GameObject(HolderName);
            holder.layer = 0;

            var canvas = holder.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            // Ueber dem Startbild (32000) und damit ueber allem, was diese Mod
            // sonst zeichnet: eine Blende, die hinter etwas liegt, blendet
            // nicht.
            canvas.sortingOrder = 32500;

            rect = holder.GetComponent<RectTransform>();

            if (rect is not null && rect != null)
                rect.sizeDelta = new Vector2(CanvasWidth, CanvasWidth);

            var groupComponent = holder.AddComponent<CanvasGroup>();
            groupComponent.alpha = 1f;
            groupComponent.interactable = false;

            // blocksRaycasts false, und das ist hier keine Kosmetik: eine
            // Flaeche 32 cm vor dem Auge wuerde sonst jeden Menuezeiger
            // abfangen, bevor er irgendwohin kommt.
            groupComponent.blocksRaycasts = false;

            ring = AddPlane("Vignette", holder.transform, texture);
            plate = AddPlane("Blink", holder.transform, null);

            log.Msg("  vignette: canvas built, world space, layer 0, "
                + "two planes (vignette + blink).");

            return true;
        }
        catch (Exception error)
        {
            log.Warning("  vignette: building the canvas threw "
                + error.GetType().Name + " - " + error.Message);
            buildFailed = true;
            return false;
        }
    }

    private static RawImage AddPlane(string name, Transform parent, Texture2D? source)
    {
        var child = new GameObject(name);
        child.layer = 0;
        child.transform.SetParent(parent, false);

        var image = child.AddComponent<RawImage>();
        image.raycastTarget = false;
        image.color = new Color(0f, 0f, 0f, 0f);

        if (source is not null && source != null)
            image.texture = source;

        var childRect = child.GetComponent<RectTransform>();

        if (childRect is not null && childRect != null)
        {
            childRect.anchorMin = Vector2.zero;
            childRect.anchorMax = Vector2.one;
            childRect.offsetMin = Vector2.zero;
            childRect.offsetMax = Vector2.zero;
        }

        return image;
    }

    // Die Textur entsteht HIER und wird nicht mitgeliefert: ein Radialverlauf
    // ist eine Zeile Rechnung, und eine eingebettete PNG waere eine Datei, ein
    // LogicalName und ein stiller Fehlschlagweg mehr - Splash.cs traegt genau
    // den.
    private bool Bake(MelonLogger.Instance log, float inner, float visible)
    {
        try
        {
            if (texture is not null && texture != null)
                UnityEngine.Object.Destroy(texture);

            texture = null;

            // mipChain false: die Flaeche steht in einer Entfernung und einer
            // Groesse, Mips wuerden nur Speicher kosten.
            var baked = new Texture2D(TextureSize, TextureSize,
                TextureFormat.RGBA32, false);

            baked.wrapMode = TextureWrapMode.Clamp;
            baked.filterMode = FilterMode.Bilinear;

            // DIE RAMPE LIEGT IM SICHTBAREN TEIL DER FLAECHE, nicht in der
            // ganzen. inner bleibt ein Anteil des BLICKFELDS - so ist der
            // Regler gemeint und so stand er auch immer in der Beschreibung -,
            // und visible rechnet ihn in Flaecheneinheiten um.
            var edge = Mathf.Clamp(inner, 0f, 0.95f) * visible;
            var span = Mathf.Max(0.02f, visible - edge);
            var half = (TextureSize - 1) * 0.5f;

            for (var y = 0; y < TextureSize; y++)
            {
                for (var x = 0; x < TextureSize; x++)
                {
                    var dx = (x - half) / half;
                    var dy = (y - half) / half;

                    // Radius eins an der halben Kante. In den Ecken laeuft er
                    // auf 1,41 und wird gedeckelt - die Ecken liegen ohnehin
                    // weit ausserhalb des Sichtfelds.
                    var radius = Mathf.Sqrt((dx * dx) + (dy * dy));
                    var ramp = Mathf.Clamp01((radius - edge) / span);

                    // SmoothStep, damit die Kante nicht als Ring sichtbar
                    // wird: eine harte Grenze liest sich als Fehler im Bild.
                    var alpha = ramp * ramp * (3f - (2f * ramp));

                    baked.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            baked.Apply(false, false);

            texture = baked;
            bakedInner = inner;
            bakedVisible = visible;
            return true;
        }
        catch (Exception error)
        {
            log.Warning("  vignette: baking the texture threw "
                + error.GetType().Name + " - " + error.Message);
            buildFailed = true;
            return false;
        }
    }

    // ---------------------------------------------------------------- teardown

    internal void Dispose(MelonLogger.Instance log)
    {
        current = 0f;
        blinkStart = -1f;
        loggedOnce = false;

        try
        {
            if (holder is not null && holder != null)
                UnityEngine.Object.Destroy(holder);

            // Die Textur geht NICHT mit dem Objekt: sie ist hier entstanden und
            // nicht aus einem Asset geladen, also gehoert ihre Freigabe
            // hierher. Splash und WashLaser haben dieselbe Lehre.
            if (texture is not null && texture != null)
                UnityEngine.Object.Destroy(texture);
        }
        catch (Exception error)
        {
            log.Warning("  vignette: teardown threw " + error.GetType().Name);
        }

        holder = null;
        texture = null;
        rect = null;
        ring = null;
        plate = null;
        bakedInner = float.NaN;
        bakedVisible = float.NaN;
    }
}
