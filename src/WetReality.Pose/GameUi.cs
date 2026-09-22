using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;

namespace WetReality;

// The game's UI in stereo, and a way to switch it off.
//
// Measured cause, from the controlled pair of hierarchy dumps taken in the same
// job with only XR differing: WasherReticles - a zero-offset, centre-pivoted
// child of a full-rect chain, so its world position IS the canvas centre - sits
// at (1920, 1080) with XR off and at (1074, 1006) with XR on. Doubled, that is
// 2148x2012, which is exactly XRSettings.eyeTexture and matches neither the
// window (2560x1440) nor half of it.
//
// So Unity sizes every ScreenSpaceOverlay canvas to the EYE BUFFER and
// composites it into each MultiPass pass at the SAME pixel address. Nothing in
// that path applies a per-eye offset, so the UI carries zero screen disparity
// while the scene carries a real 62 mm one. Two copies, and an implied vergence
// that reads as pressed against the face. The doubling and the closeness are one
// defect, not two, and it is Unity's own overlay behaviour - XR Boot never
// touches a texture or a render pass.
//
// The fix is to stop the UI being a screen-space blit: ScreenSpaceCamera makes
// it scene geometry that each eye's own projection places, and planeDistance
// then sets a comfortable distance. One write on the ROOT canvas governs the
// whole nested tree.
//
// Both operations are gated on their own key and fully reversible, because two
// things about them are unverified: the layer of the UI subtree (PlayerCamera's
// cullingMask 0x002ae717 does NOT include bit 5, Unity's built-in UI layer, so
// the conversion could hide the UI outright - hence the cullingMask repair
// below), and whether PwsScreenManager rewrites renderMode on a viewport layout
// change.
internal sealed class GameUi
{
    private Canvas? root;
    private CanvasGroup? group;

    private RenderMode originalMode = RenderMode.ScreenSpaceOverlay;
    private Camera? originalCamera;
    private float originalPlane = 100f;
    private int originalMask;
    private bool converted;
    private bool captured;
    private bool hidden;
    private float appliedScale = float.NaN;
    private bool loggedDriven;

    // ========================================================================
    // DIE UI IM VORDERGRUND - Abschnitt 103.
    //
    // Der Kommentar am Kopf von ApplyScale nennt das Problem schon wortgleich
    // aus Unitys Dokumentation: was naeher an der Kamera steht als die
    // UI-Ebene, zeichnet davor. Das ist die Kehrseite von ScreenSpaceCamera und
    // kein Fehler der Mod - aber es macht Aufgabenliste und Hauptmenue vor
    // einer nahen Wand unbenutzbar.
    //
    // DREI NAMEN, UND SIE WERDEN OHNE HasProperty GESETZT. Das ist der
    // Unterschied zwischen dem ersten und dem zweiten Versuch, und er ist
    // gemessen:
    //
    //   ui depth: 402 graphic(s), 0 material(s) set, 402 without a candidate
    //     UI/Default                 -> KEIN KANDIDAT
    //     TextMeshPro/Distance Field -> KEIN KANDIDAT
    //
    // unity_GUIZTestMode ist in Unitys UI-Shader NICHT deklariert. Es steht
    // dort nur als ZTest [unity_GUIZTestMode], und Unity selbst setzt es per
    // SetInt - eine Eigenschaft, die in der Property-Tabelle des Shaders nie
    // auftaucht. HasProperty antwortet darum korrekt mit nein, waehrend SetInt
    // trotzdem wirkt.
    //
    // Das Muster von WashLaser.PaintMaterial war hier also das falsche: bei
    // Farbnamen schuetzt HasProperty vor einem Fehlgriff, hier sperrt es genau
    // den Weg, der funktioniert. Ein SetInt auf einen Namen, den ein Shader
    // nicht liest, ist dagegen wirkungslos und harmlos - es legt nur einen
    // Eintrag in die Property-Liste des Materials.
    //
    // _ZTestMode gehoert TextMeshPro, _ZTest den URP-Varianten. Alle drei
    // werden gesetzt, weil keiner der drei schadet und einer greifen muss.
    // Die Namen und das Schreiben liegen in UiDepth, weil der Zeiger-Laser
    // dasselbe braucht - siehe Abschnitt 104. Zwei Kopien derselben Liste
    // waeren zwei Orte, an denen ein vierter Name fehlen kann.

    // Material-Pointer -> was geaendert wurde, damit die Ruecknahme moeglich
    // ist. Der POINTER ist der Schluessel, nicht das Material: Il2CppInterop
    // gibt bei jedem Zugriff einen frischen Wrapper heraus, und Unitys
    // Standard-UI-Material ist EINES fuer hunderte Graphics - ueber den Wrapper
    // verglichen wuerde es hundertfach beschrieben.
    private readonly Dictionary<IntPtr, (Material Material, string Property, int Previous)>
        depthTouched = new();

    private float nextDepthWalk = float.MaxValue;
    private bool loggedDepth;

    internal string Status { get; private set; } = "ui: untouched";

    // Exposed so the reticle diagnostic can walk the UI subtree without a second
    // scene-wide search. Null until the root resolves.
    internal Transform? Root => root is null || root == null ? null : root.transform;

    // Die Materialien werden beim Reset ZURUECKGESETZT, nicht bloss vergessen.
    // Unitys Standard-UI-Material ueberlebt einen Auftragswechsel, und ein
    // vergessener ZTest waere eine Aenderung, die niemand mehr zuruecknehmen
    // kann - genau die Sorte Rest, die dieses Projekt sonst als Defekt
    // protokolliert.
    internal void ResetDepth(MelonLogger.Instance log)
    {
        RestoreDepth(log);
        nextDepthWalk = float.MaxValue;
        loggedDepth = false;
    }

    // Deliberately does NOT clear the captured originals.
    //
    // It used to set captured = false, which threw away originalMode,
    // originalCamera, originalPlane and originalMask. After a Reset the
    // conversion could never be undone: ToggleStereoFix would re-capture the
    // ALREADY CONVERTED state as the original, and the F12 toggle would then
    // "restore" ScreenSpaceCamera forever. The values cost sixteen bytes and
    // are the only way back.
    internal void Reset()
    {
        root = null;
        group = null;
        converted = false;
        hidden = false;
        appliedScale = float.NaN;
        loggedDriven = false;
        Status = "ui: untouched";
    }

    // Resolved by shape rather than by a hard-coded path: the root is a parentless
    // Canvas in DontDestroyOnLoad whose name starts with UIRoot. FindObjectsOfTypeAll
    // is used because FindObjectOfType skips inactive objects, and it returns an
    // Il2CppReferenceArray of class references - the safe interop shape.
    private bool Resolve(MelonLogger.Instance log)
    {
        if (root is not null && root != null)
            return true;

        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Canvas>());

            for (var index = 0; index < all.Length; index++)
            {
                var canvas = all[index]?.TryCast<Canvas>();

                if (canvas is null || canvas.transform.parent is not null)
                    continue;

                if (!canvas.name.StartsWith("UIRoot", StringComparison.Ordinal))
                    continue;

                root = canvas;
                group = canvas.GetComponent<CanvasGroup>();

                log.Msg($"  ui root: {canvas.name}   mode {canvas.renderMode}   "
                    + $"layer {canvas.gameObject.layer}   "
                    + $"canvasGroup {(group is null ? "MISSING" : "found")}");

                return true;
            }

            log.Warning($"  ui root: no parentless Canvas named UIRoot* among {all.Length} canvases");
        }
        catch (Exception exception)
        {
            log.Warning($"  ui root lookup threw {exception.GetType().Name}: {exception.Message}");
        }

        return false;
    }

    // The kill switch. CanvasGroup.alpha rather than SetActive or Canvas.enabled:
    // alpha multiplies down through every nested canvas and Graphic in the tree,
    // and it fires no Awake/OnEnable/OnDisable. That matters here -
    // ScreenManagerViewportBase overrides OnDisable and owns the cached-screen
    // list, so deactivating the tree risks unloading screens and re-running
    // initialisation on the way back.
    // EIN GEWUENSCHTER STAND, NICHT DAS GEGENTEIL VON JETZT.
    //
    // Der Immersion Mode gleicht pro Frame ab (Pose ruft mit
    // immersion && !menuMode), und ein Toggle kann das nicht bedienen: er
    // wuesste bei jedem Aufruf nur, dass er umschalten soll. Darum haelt Pose
    // den WUNSCH und diese Klasse den ZUSTAND, und genau eine Stelle
    // vergleicht die beiden.
    //
    // IDEMPOTENT UND DARUM PRO FRAME BEZAHLBAR: stimmt der Stand schon,
    // passiert nichts - kein Schreibvorgang auf die CanvasGroup, keine
    // Logzeile. Geloggt wird nur der Wechsel.
    //
    // Resolve() steht bewusst HINTER dem Kurzschluss. Der Normalfall ist
    // "nicht versteckt und soll nicht versteckt sein", und dafuer muss keine
    // szeneweite Suche nach dem Wurzel-Canvas laufen.
    internal void ApplyHidden(MelonLogger.Instance log, bool wanted)
    {
        if (wanted == hidden)
            return;

        if (!Resolve(log))
            return;

        try
        {
            if (group is null)
            {
                // Second choice, and only because the first is absent.
                root!.enabled = !wanted;
                hidden = wanted;
                Status = hidden ? "ui hidden (canvas)" : "ui shown (canvas)";
                log.Msg($"  {Status}");
                return;
            }

            hidden = wanted;
            group.alpha = hidden ? 0f : 1f;
            group.blocksRaycasts = !hidden;
            Status = hidden ? "ui hidden" : "ui shown";
            log.Msg($"  {Status}");
        }
        catch (Exception exception)
        {
            log.Warning($"  hiding the ui threw {exception.GetType().Name}: {exception.Message}");
            Reset();
        }
    }

    internal void ToggleStereoFix(MelonLogger.Instance log, float distance)
    {
        if (!Resolve(log))
            return;

        try
        {
            var canvas = root!;
            var camera = Camera.main;

            if (camera is null)
            {
                Status = "ui: no main camera";
                log.Warning("  ui stereo fix: no main camera");
                return;
            }

            if (!captured)
            {
                captured = true;
                originalMode = canvas.renderMode;
                originalCamera = canvas.worldCamera;
                originalPlane = canvas.planeDistance;
                originalMask = camera.cullingMask;
                log.Msg($"  ui original: mode {originalMode}  plane {originalPlane:0.##}  "
                    + $"cullingMask 0x{originalMask:x8}");
            }

            if (converted)
            {
                canvas.renderMode = originalMode;
                canvas.worldCamera = originalCamera;
                canvas.planeDistance = originalPlane;
                camera.cullingMask = originalMask;
                converted = false;
                Status = "ui overlay (original)";
                log.Msg($"  {Status}");
                return;
            }

            // The layer repair, and the one thing most likely to decide whether
            // this works at all. In overlay mode the culling mask is irrelevant;
            // the moment the canvas becomes geometry it is not. Unity's built-in
            // UI layer is bit 5, and PlayerCamera's mask does not include it.
            var layer = canvas.gameObject.layer;
            var bit = 1 << layer;

            if ((camera.cullingMask & bit) == 0)
            {
                camera.cullingMask |= bit;
                log.Msg($"  ui stereo fix: added layer {layer} to cullingMask, now "
                    + $"0x{camera.cullingMask:x8}");
            }

            canvas.worldCamera = camera;
            canvas.planeDistance = distance;
            canvas.renderMode = RenderMode.ScreenSpaceCamera;

            converted = true;
            Status = $"ui screen-space-camera at {distance:0.##} m";
            log.Msg($"  {Status}   layer {layer}");
        }
        catch (Exception exception)
        {
            log.Warning($"  ui stereo fix threw {exception.GetType().Name}: {exception.Message}");
            Reset();
        }
    }

    // AUTOMATIC, so the headset does not start with unreadable UI and a keypress
    // nobody new would know to make. F12 stays as the manual override.
    //
    // Converting is idempotent - ToggleStereoFix is only called while `converted`
    // is false - so this can be driven every frame from OnLateUpdate.
    // Von Pose gerufen, wenn ein Menue auf- oder zugeht: der naechste Frame
    // laeuft dann einmal ueber den Baum, weil ein geoeffnetes Menue neue
    // Graphics mitbringt. Kein Durchlauf im Moment des Aufrufs - der gehoert
    // an die Stelle im Frame, an der die UI ohnehin behandelt wird.
    internal void TouchDepth() => nextDepthWalk = 0f;

    internal void EnsureStereo(MelonLogger.Instance log, float distance, float scale,
        bool alwaysOnTop, float depthRefresh)
    {
        if (!Resolve(log))
            return;

        if (!converted)
        {
            ToggleStereoFix(log, distance);
            ApplyScale(log, scale);

            if (converted && alwaysOnTop)
            {
                ApplyDepth(log);

                // NOCH EINMAL ZWEI SEKUNDEN SPAETER. Die HUD-Elemente stehen
                // beim Auftragsstart noch nicht alle; ein einziger Durchlauf
                // erwischt die spaeteren nicht, und pro Frame zu laufen waere
                // der falsche Preis dafuer.
                nextDepthWalk = Time.unscaledTime + 2f;
            }

            return;
        }

        // DER SCHALTER WIRKT IN BEIDE RICHTUNGEN, und zwar ohne Neustart: aus
        // heisst zuruecknehmen, an heisst beim naechsten Takt anwenden.
        if (!alwaysOnTop)
        {
            RestoreDepth(log);
            nextDepthWalk = float.MaxValue;
        }
        else if (Time.unscaledTime >= nextDepthWalk)
        {
            ApplyDepth(log);

            // Ohne Intervall genau ein Nachschlag; mit Intervall der naechste
            // Takt. Default ist ohne.
            nextDepthWalk = depthRefresh > 0.01f
                ? Time.unscaledTime + depthRefresh
                : float.MaxValue;
        }

        // RE-ASSERTED, not merely applied once - and the bug this fixes was
        // introduced by the first version of this method.
        //
        // Converting once was wrong because the game lays its viewports out
        // again on a menu transition, and that reaches renderMode on the root
        // above them. Reapply had always carried this check; making the
        // conversion automatic routed around it, because EnsureStereo only acted
        // while `converted` was false and `converted` stays true forever.
        //
        // The symptom was NOT the UI. It was the CROSSHAIR, reported as
        // "completely rotated" after opening and closing the task list, and
        // correct again only when the player happened to face one particular
        // world direction. The measurement explains why: all six reticle nodes
        // read localEuler (0, 0, 0) and inherit their world rotation from this
        // canvas. A ScreenSpaceCamera canvas is oriented to face its camera every
        // frame; one that has lost the mode or the camera reference simply keeps
        // its last orientation, which is world-fixed. The reticle then points
        // wherever the player was looking at the moment the menu opened.
        //
        // worldCamera is checked as well as renderMode: the mode surviving while
        // the camera reference is cleared produces exactly the same frozen
        // orientation, and costs nothing to test.
        var canvas = root!;
        var camera = Camera.main;

        var modeLost = canvas.renderMode != RenderMode.ScreenSpaceCamera;
        var cameraLost = camera is not null && camera != null
            && (canvas.worldCamera is null || canvas.worldCamera != camera);

        if (modeLost || cameraLost)
        {
            log.Msg($"  ui: conversion lapsed ({(modeLost ? "renderMode" : "worldCamera")}), "
                + "re-applying - the crosshair would otherwise stay world-fixed");

            converted = false;
            ToggleStereoFix(log, distance);

            // The scale is re-asserted too. A viewport re-layout that resets the
            // render mode plausibly rebuilds children, and appliedScale would
            // then describe nodes that no longer exist.
            appliedScale = float.NaN;
        }

        ApplyScale(log, scale);
    }

    // ZTest Always auf jedem UI-Material unter dem Wurzel-Canvas.
    //
    // IDEMPOTENT UND BILLIG BEI WIEDERHOLUNG: schon behandelte Materialien
    // stehen im Verzeichnis und werden uebersprungen. Ein zweiter Aufruf
    // kostet damit den Baumdurchlauf, aber keine Materialschreibung - und der
    // Durchlauf laeuft NICHT pro Frame, sondern nach der Umstellung, zwei
    // Sekunden spaeter noch einmal fuer die HUD-Elemente, und wenn ein Menue
    // aufgeht. Alles andere waere Arbeit fuer eine Frage, die beantwortet ist.
    private void ApplyDepth(MelonLogger.Instance log)
    {
        if (root is null || root == null)
            return;

        try
        {
            var graphics = root.GetComponentsInChildren<UnityEngine.UI.Graphic>();

            if (graphics is null)
                return;

            var added = 0;
            var withoutProperty = 0;
            var shaders = loggedDepth ? null : new List<string>();

            for (var index = 0; index < graphics.Length; index++)
            {
                var graphic = graphics[index];

                if (graphic is null || graphic == null)
                    continue;

                Material? material;

                try
                {
                    // materialForRendering ist das Material, mit dem
                    // tatsaechlich gezeichnet wird - inklusive der Instanz, die
                    // eine Maske erzeugt. graphic.material waere das
                    // zugewiesene und bei maskierten Elementen das falsche.
                    material = graphic.materialForRendering;
                }
                catch
                {
                    continue;
                }

                if (material is null || material == null)
                    continue;

                var pointer = material.Pointer;

                if (depthTouched.ContainsKey(pointer))
                    continue;

                // DER VORWERT, und er ist die Beschriftung fuer die
                // Ruecknahme. Fuer eine nicht deklarierte Eigenschaft liest
                // GetInt 0, und 0 waere als ZTest "Disabled" - zurueck-
                // geschrieben also gerade NICHT der Zustand von vorher. Darum
                // wird 0 als 4 gemerkt, LEqual, was Unity fuer einen Canvas
                // ausserhalb des Overlay-Modus setzt.
                var previous = UiDepth.ReadPrevious(material);
                var written = UiDepth.ForceAlways(material);

                if (shaders is not null)
                {
                    var shader = material.shader;
                    var name = shader is null || shader == null ? "no shader" : shader.name;

                    // DIE RUECKLESUNG STEHT DANEBEN, weil "gesetzt" und
                    // "angekommen" zwei Dinge sind: GetInt liest die
                    // Property-Liste des Materials, nicht das, was der Shader
                    // daraus macht. Ein 8 hier beweist nur, dass der
                    // Schreibvorgang gelandet ist - das Urteil faellt das Bild.
                    var back = UiDepth.ReadBack(material);

                    var line = $"{name} -> wrote {written} name(s), "
                        + $"{UiDepth.ZTestProperties[0]} reads back {back}, was {previous}";

                    if (!shaders.Contains(line))
                        shaders.Add(line);
                }

                if (written == 0)
                {
                    withoutProperty++;
                    continue;
                }

                depthTouched[pointer] = (material, UiDepth.ZTestProperties[0], previous);
                added++;
            }

            if (!loggedDepth)
            {
                loggedDepth = true;

                // EIN BLOCK, EINMAL, und er nennt genau das, was die naechste
                // Entscheidung braucht: welcher Shader welchen Kandidaten hat.
                // Steht ueberall KEIN KANDIDAT, ist der Materialweg erledigt
                // und eine eigene UI-Kamera der naechste Schritt - das soll im
                // Log stehen und nicht im Headset erraten werden.
                log.Msg($"  ui depth: {graphics.Length} graphic(s), "
                    + $"{depthTouched.Count} material(s) set to ZTest Always, "
                    + $"{withoutProperty} that refused every name");

                if (shaders is not null)
                {
                    for (var index = 0; index < shaders.Count; index++)
                        log.Msg($"    ui depth shader: {shaders[index]}");
                }
            }
            else if (added > 0)
            {
                log.Msg($"  ui depth: {added} new material(s) set to ZTest Always "
                    + $"({depthTouched.Count} total)");
            }
        }
        catch (Exception exception)
        {
            log.Warning($"  ui depth threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    // Die Ruecknahme, und sie ist in dieser Datei Pflicht: ToggleStereoFix
    // stellt renderMode, worldCamera, planeDistance und cullingMask ebenso
    // zurueck. Ein Schalter, der nicht zurueckkann, ist in einer Mod kein
    // Schalter.
    private void RestoreDepth(MelonLogger.Instance log)
    {
        if (depthTouched.Count == 0)
            return;

        var restored = 0;

        foreach (var entry in depthTouched)
        {
            var material = entry.Value.Material;

            // Unity-null MIT geprueft: nach einem Auftragswechsel koennen die
            // Materialien zerstoert sein, und "is null" sieht das nicht.
            if (material is null || material == null)
                continue;

            try
            {
                UiDepth.Restore(material, entry.Value.Previous);
                restored++;
            }
            catch
            {
                // Ein Material, das sich nicht mehr schreiben laesst, ist weg.
            }
        }

        depthTouched.Clear();
        log.Msg($"  ui depth: restored {restored} material(s) to their own ZTest");
    }

    // THE CONTENT SCALE, and the node it is written to is the whole point.
    //
    // Unity's documentation answers the original question literally: the UI's
    // screen size does NOT change with distance, because it is always rescaled
    // to fit the camera frustum exactly. Turning planeDistance up therefore does
    // not make the surface smaller - which is why the 2 m plane reads fine and
    // the UI is still too big. The same passage names the clipping: anything
    // nearer the camera than the UI plane draws in front of it.
    //
    // So the effective lever is a scale on the CHILD nodes under the root canvas,
    // never on the root canvas itself: a Canvas DRIVES its own RectTransform,
    // and a scale written there is overwritten on the next layout.
    // RectTransform.drivenByObject is a public getter returning a class - a safe
    // shape - and it says at runtime who owns the transform, so it is logged once
    // rather than assumed.
    //
    // CanvasScaler is deliberately not touched: it writes only scaleFactor and
    // referencePixelsPerUnit, never a transform, and disabling it is NOT neutral
    // because its OnDisable resets scaleFactor to 1 and referencePixelsPerUnit
    // to 100.
    private void ApplyScale(MelonLogger.Instance log, float scale)
    {
        if (root is null || root == null)
            return;

        // Written only on change. Every child touched per frame would be pointless
        // work on a tree this size, and the layout system reacts to scale writes.
        if (Mathf.Approximately(appliedScale, scale))
            return;

        try
        {
            var parent = root.transform;

            if (!loggedDriven)
            {
                loggedDriven = true;

                var rect = parent.TryCast<RectTransform>();
                var driven = "not a RectTransform";

                if (rect is not null)
                {
                    var owner = rect.drivenByObject;
                    driven = owner is null || owner == null ? "nobody" : owner.name;
                }

                log.Msg($"  ui scale: root rect driven by {driven}   "
                    + $"{parent.childCount} child node(s) - scaling the children, not the root");
            }

            var written = 0;

            for (var index = 0; index < parent.childCount; index++)
            {
                var child = parent.GetChild(index);

                if (child is null || child == null)
                    continue;

                child.localScale = new Vector3(scale, scale, 1f);
                written++;
            }

            appliedScale = scale;
            log.Msg($"  ui scale: {scale:0.##} on {written} child node(s)");
        }
        catch (Exception exception)
        {
            log.Warning($"  ui scale threw {exception.GetType().Name}: {exception.Message}");
            appliedScale = float.NaN;
        }
    }

    // Re-applied on request rather than assumed to stick: PwsScreenManager lays
    // the viewports out again on ViewportLayoutChanged, and whether that reaches
    // renderMode on the root above them is unverified.
    internal void Reapply(MelonLogger.Instance log, float distance)
    {
        if (!converted || root is null || root == null)
            return;

        try
        {
            if (root.renderMode != RenderMode.ScreenSpaceCamera)
            {
                log.Msg("  ui: renderMode was reset by the game, re-applying");
                converted = false;
                ToggleStereoFix(log, distance);
            }
        }
        catch
        {
            Reset();
        }
    }
}
