using MelonLoader;
using UnityEngine;

namespace WetReality;

// DER STRAHL TRIFFT DIE EIGENE HAND - und der Aufprall ist nicht nachgebaut,
// sondern der des Spiels.
//
// Gewuenscht: richtet die dominante Hand den Strahl auf die Off-Hand, vibriert
// deren Controller kraeftig, das Spiel zeigt seinen normalen Spritzeffekt, und
// der Strahl geht nicht mehr durch die Hand hindurch. Die Kollision soll der
// Geometrie des Handmodells folgen, damit Bild und Gefuehl am selben Ort
// liegen.
//
// WAS DAS SPIEL DAFUER SCHON MITBRINGT, statisch gemessen mit
// WetReality.AssemblyProbe gegen Il2CppFuturLab.PW2.dll:
//
//     WashEquipment                  prop LayerMask m_rayMask
//                                    prop NozzleRaycaster[] m_nozzleRaycasters
//                                    prop float m_lastCrosshairHitDistance
//     WashEquipmentCreateRaycastJob  prop LayerMask LayerMask
//                                    prop float MaxRange / MinRange
//                                    prop NativeArray<RaycastCommand>
//     WashEquipmentRaycastHitAction  Invoke(WashEquipment, RaycastHit,
//                                           RaycastCommand)
//
// Der Waschstrahl IST ein Raycast-Faecher gegen eine EBENENMASKE. Damit ist die
// Aufgabe keine Effektprogrammierung, sondern eine Sichtbarkeitsfrage: hat die
// Hand einen Collider auf einer Ebene, die in dieser Maske steht, dann ist sie
// fuer das Spiel ein undurchdringliches Objekt wie jede Wand - mit dessen
// Spritzeffekt, dessen Trefferdistanz und dessen Strahlabbruch. Nichts davon
// muss dieser Mod zeichnen. Gemessen im Lauf 1.41.1: die Maske liest
// 0x20284101, also Default, Washable, Items, CharacterVisuals,
// PhysicsGrabbables und PhysicsWashable.
//
// ------------------------------------------------------------------------
// DIE FORM DER KOLLISION - DREI MELDUNGEN, ZWEI URSACHEN
//
// Gemeldet nach 1.41.1, mit Bildern: der Spritzeffekt liegt sichtbar UNTER der
// Hand; zielt man auf die Fingerspitzen, geht der Strahl ohne Vibration durch
// das ganze Modell; und kommt die Muendung der Hand nahe, hoert die Kollision
// auf, obwohl die Muendung das Mesh noch nicht erreicht hat.
//
//   URSACHE 1: DER QUADER. Die gemessene Box war 0,146 x 0,212 x 0,08 m - der
//   Huellquader ueber Handflaeche, abgespreiztem Daumen und Handgelenkstummel.
//   Eine Hand fuellt davon vielleicht die Haelfte. Der Rest ist Luft, in der
//   das Wasser aufschlaegt, und genau das zeigen die Bilder.
//
//   URSACHE 2: EIN STRAHL, DER IN EINEM COLLIDER BEGINNT, TRIFFT IHN NICHT.
//   Das ist dokumentiertes Unity-Verhalten und gilt fuer beide Seiten - fuer
//   den Faecher des Spiels wie fuer den Pruefstrahl hier. Mit einem zu grossen
//   Quader tritt der Fall ein, lange bevor die Muendung die Hand beruehrt, und
//   verschaerft wurde er durch HapticContactSkip: 0,1 m, die der Pruefstrahl
//   vorne uebersprungen hat. Die ersten zehn Zentimeter vor der Duese waren
//   damit blind.
//
// DIE ANTWORT AUF BEIDES
//
//   1. DIE ECHTE GEOMETRIE ALS COLLIDER. BakeMesh liefert die gestellte
//      Handgeometrie, und die wird einem MeshCollider gegeben - nicht konvex,
//      also mit Fingerzwischenraeumen und abgespreiztem Daumen. Fuer Raycasts
//      ist das erlaubt und billig; ein Rigidbody haengt nicht daran, und die
//      Ebene ist ueber IgnoreLayerCollision physikalisch stumm.
//
//      Der Quader bleibt als Rueckfall und als Schalter (HandHitMesh): er hat
//      die Mechanik belegt, und ein Vergleich im Headset kostet dann keinen
//      Build.
//
//   2. EIN EIGENER, KLEINER VORLAUF. Der Pruefstrahl fragt NUR die Handebene -
//      die Colliders des Waschers koennen dort gar nicht stoeren, wofuer
//      HapticContactSkip gedacht war. HandHitSkip steht darum auf 0,01 m.
//
//   3. EIN RUECKWAERTSSTRAHL FUER DEN INNENFALL, und er ist die Antwort auf die
//      Bitte, dass die Vibration auch dann bestehen bleibt, wenn die Muendung
//      in der Hand steckt. Trifft der Vorwaertsstrahl nicht, wird von einem
//      Punkt VOR der Hand zurueck zur Duese gestrahlt. Derselbe Strahlgang,
//      andere Richtung: verfehlt der Vorwaertsstrahl die Hand seitlich,
//      verfehlt der Rueckwaertsstrahl sie auch - trifft er, dann lag der
//      Ursprung im Inneren. Kein RaycastHit, nur bool.
//
// ------------------------------------------------------------------------
// ZWEI REGELN, DIE DIESES PROJEKT TEUER GELERNT HAT UND DIE HIER GELTEN
//
//   DIE EBENE EINES KNOTENS MIT RENDERER BLEIBT UNANGETASTET. Eine Ebene ist
//   auch die Culling-Maske der Kamera, und dieses Spiel culled nach Ebene
//   (GameLayer, PlayerCharacter.GetEquipmentLayer). In 1.41.0 lag der Collider
//   auf dem Renderer-Knoten, und die Off-Hand war unsichtbar. Der Collider
//   haengt deshalb an einem eigenen Kindknoten auf Identitaet.
//
//   KEINE STRUKTUREN UEBER DIE INTEROP-GRENZE. Bounds, Ray und RaycastHit
//   bleiben draussen; Box und Mesh entstehen aus Vector3-Eckpunkten, und die
//   Treffer sind bool. Dieselbe Regel wie in ProbeSprayContact.
internal sealed class HandSpray
{
    // Der gebackene Mesh MUSS leben, solange der MeshCollider ihn benutzt.
    // Zerstoert wird er in Reset, sonst bleibt je Levelwechsel einer liegen.
    private Mesh? baked;
    private Collider? shape;
    private bool meshShape;
    private int layer = -1;
    private int maskBefore;
    private bool maskRead;
    private bool maskWritten;
    private float nextBuild;
    private Vector3 boxSize;

    // DER ZWEITE BACKTERMIN. Ein Posenwechsel blendet, und der Frame direkt
    // danach zeigt eine Zwischenstellung - gebacken wird darum zweimal: sofort,
    // damit nie ein Frame ohne Collider steht, und noch einmal, wenn die Blende
    // durch ist.
    private float rebuildAt;

    internal int LayerBit => layer < 0 ? 0 : 1 << layer;

    internal bool Ready => layer >= 0 && shape is not null && shape != null;

    internal bool InsideHand { get; private set; }

    internal string Status { get; private set; } = "hand hit: off";

    internal void Reset()
    {
        // Der Collider stirbt mit der Handinstanz, der Mesh nicht: er gehoert
        // diesem Mod und wird hier freigegeben.
        if (baked is not null && baked != null)
            UnityEngine.Object.Destroy(baked);

        baked = null;
        shape = null;
        meshShape = false;
        layer = -1;
        maskRead = false;
        maskWritten = false;
        nextBuild = 0f;
        rebuildAt = 0f;
        boxSize = Vector3.zero;
        InsideHand = false;
        Status = "hand hit: off";
    }

    // DIE POSE HAT DIE FORM GEAENDERT, also ist der gebackene Collider die
    // Geometrie von gestern. Nur vergessen, nicht abbauen: EnsureCollider
    // raeumt den alten Collider selbst weg (DropShape) und backt neu.
    //
    // Der Zeitgeber wird mitgenullt, damit der naechste Frame baut und nicht
    // erst der in einer halben Sekunde - eine halbe Sekunde falsche Hitbox
    // waere genau der Fehler, den Abschnitt 137 beseitigt hat.
    internal void Invalidate(float settle)
    {
        shape = null;
        nextBuild = 0f;

        // Der Nachtermin gilt ab JETZT, nicht ab dem ersten Backen: die Blende
        // laeuft parallel zum Bauen.
        rebuildAt = settle > 0f ? Time.unscaledTime + settle : 0f;
    }

    // Der Collider an der Off-Hand. Auf 0,5 s nachgefasst, weil ein
    // Levelwechsel die Handinstanzen neu baut - shape liest dann Unity-null,
    // und "is null" allein wuerde das nicht sehen.
    internal bool EnsureCollider(MelonLogger.Instance log, Transform? offHandRoot,
        bool wantMesh, float padding, Vector3 handWorld, bool handReady)
    {
        if (offHandRoot is null || offHandRoot == null)
        {
            shape = null;
            Status = "hand hit: no hand";
            return false;
        }

        if (shape is not null && shape != null && meshShape == wantMesh)
        {
            // IST DIE BLENDE DURCH, wird einmal nachgebacken. Ohne das bliebe
            // die Zwischenstellung des Posenwechsels als Hitbox stehen - genau
            // die Meldung nach 1.45.0.
            if (rebuildAt <= 0f || Time.unscaledTime < rebuildAt)
                return true;

            rebuildAt = 0f;
            shape = null;
            nextBuild = 0f;
        }

        if (Time.unscaledTime < nextBuild)
            return false;

        nextBuild = Time.unscaledTime + 0.5f;

        try
        {
            var renderer = FirstGeometry(offHandRoot);

            if (renderer is null || renderer == null)
            {
                Status = "hand hit: no renderer";
                return false;
            }

            var host = CollisionNode(renderer.transform);

            if (host is null || host == null)
            {
                Status = "hand hit: no collision node";
                return false;
            }

            if (layer < 0 && !PickLayer(log, host))
                return false;

            // NUR der Kindknoten wechselt die Ebene. Der Renderer daneben
            // behaelt seine, sonst culled ihn die Kamera weg - der Defekt aus
            // 1.41.0.
            host.gameObject.layer = layer;

            DropShape(host);

            var built = wantMesh
                ? BuildMesh(log, renderer, host)
                : BuildBox(log, renderer, host, padding);

            if (!built)
                return false;

            meshShape = wantMesh;

            // DER EINE WERT, DER FORM VON RAUM TRENNT. Liegt der Collider
            // weit von der getrackten Hand weg, ist nicht die FORM falsch,
            // sondern der RAUM, in dem sie sitzt - und dann ist der naechste
            // Schritt ein anderer. Ohne diese Zahl war "starker Offset" nur
            // ein Eindruck.
            var gap = handReady
                ? (host.position - handWorld).magnitude
                : -1f;

            log.Msg($"  hand hit: {(wantMesh ? "MESH collider" : "box collider")}"
                + (wantMesh ? "" : $" {boxSize.x:0.###} x {boxSize.y:0.###} x {boxSize.z:0.###} m")
                + $"   on layer {layer} (\"{LayerMask.LayerToName(layer)}\")"
                + $"   node {host.name} under {renderer.name}"
                + $"   renderer stays on layer {renderer.gameObject.layer}"
                + (gap < 0f ? "   collider-to-controller unknown"
                    : $"   collider-to-controller {gap:0.###} m"));

            return true;
        }
        catch (Exception exception)
        {
            log.Warning($"  hand hit: collider build threw {exception.GetType().Name}: "
                + exception.Message);
            Status = "hand hit: build failed";
            return false;
        }
    }

    // DIE MASKE DES SPIELS, gelesen und um ein Bit erweitert. want false nimmt
    // die Erweiterung zurueck, damit der Effekt im Headset umschaltbar ist und
    // nicht erst im naechsten Build.
    internal void ApplyWashMask(MelonLogger.Instance log,
        Il2CppFuturLab.PW2.WashEquipment? equipment, bool want)
    {
        if (equipment is null || equipment == null)
            return;

        try
        {
            var current = equipment.m_rayMask.value;

            if (!maskRead)
            {
                maskRead = true;
                maskBefore = current;
                log.Msg($"  hand hit: wash mask reads 0x{current:X8}"
                    + $"   ({LayerNames(current)})");
            }

            if (want && LayerBit != 0 && (current & LayerBit) == 0)
            {
                equipment.m_rayMask = current | LayerBit;
                maskWritten = true;
                log.Msg($"  hand hit: wash mask 0x{current:X8} -> "
                    + $"0x{current | LayerBit:X8}   layer {layer} added");
            }
            else if (!want && maskWritten && (current & LayerBit) != 0)
            {
                equipment.m_rayMask = current & ~LayerBit;
                maskWritten = false;
                log.Msg($"  hand hit: wash mask 0x{current:X8} -> "
                    + $"0x{current & ~LayerBit:X8}   layer {layer} removed");
            }
        }
        catch (Exception exception)
        {
            // Die Vibration haengt NICHT daran: sie fragt den eigenen Strahl
            // gegen die eigene Ebene. Ohne Maskenschreibzugriff fehlt nur der
            // Spritzeffekt des Spiels, und das steht dann hier.
            log.Warning($"  hand hit: wash mask threw {exception.GetType().Name}: "
                + exception.Message);
        }
    }

    // Beim Abschalten: die Maske zurueck, wie sie war.
    internal void Release(MelonLogger.Instance log,
        Il2CppFuturLab.PW2.WashEquipment? equipment)
    {
        if (!maskWritten || equipment is null || equipment == null)
            return;

        try
        {
            equipment.m_rayMask = maskBefore;
            maskWritten = false;
            log.Msg($"  hand hit: wash mask restored to 0x{maskBefore:X8}");
        }
        catch (Exception exception)
        {
            log.Warning($"  hand hit: mask restore threw {exception.GetType().Name}");
        }
    }

    // TRIFFT DER STRAHL DIE HAND? Drei bool-Strahlen, kein RaycastHit.
    //
    //   1. VORWAERTS gegen NUR die Handebene: steht die Hand im Strahl.
    //   2. RUECKWAERTS von einem Punkt vor der Hand zurueck zur Duese, aber nur
    //      wenn 1 nichts fand: dann steckt der Ursprung im Inneren der Hand.
    //      Derselbe Strahlgang, andere Richtung - ein seitlicher Vorbeischuss
    //      verfehlt in beide Richtungen.
    //   3. Die Weltmaske bis KURZ VOR die Hand: steht etwas dazwischen, ist es
    //      nicht die Hand, die getroffen wird. Die Entfernung dafuer kommt aus
    //      der veroeffentlichten Handpose, nicht aus dem Treffer.
    internal bool Probe(Vector3 origin, Vector3 forward, Vector3 handWorld,
        float range, int blockMask, float skip, float insideDepth)
    {
        InsideHand = false;

        if (!Ready || forward.sqrMagnitude < 0.0001f)
            return false;

        var start = origin + (forward * skip);
        var hit = Physics.Raycast(start, forward, range, LayerBit,
            QueryTriggerInteraction.Ignore);

        if (!hit && insideDepth > 0.001f)
        {
            var ahead = start + (forward * insideDepth);

            if (Physics.Raycast(ahead, -forward, insideDepth, LayerBit,
                QueryTriggerInteraction.Ignore))
            {
                hit = true;
                InsideHand = true;
            }
        }

        if (!hit)
            return false;

        // Der Innenfall kennt kein "davor": die Hand liegt um die Duese.
        if (InsideHand)
            return true;

        var along = Vector3.Dot(handWorld - start, forward);

        if (along > 0.15f && blockMask != 0
            && Physics.Raycast(start, forward, along - 0.1f, blockMask,
                QueryTriggerInteraction.Ignore))
            return false;

        return true;
    }

    internal void Report(bool hitting, bool washing)
    {
        if (!Ready)
            return;

        var form = meshShape ? "mesh" : "box";

        Status = hitting
            ? InsideHand
                ? $"hand hit: INSIDE ({form})"
                : $"hand hit: ON ({form})"
            : washing ? $"hand hit: clear ({form})" : $"hand hit: idle ({form})";
    }

    // DIE ECHTE GEOMETRIE. Nicht konvex, damit Fingerzwischenraeume und der
    // abgespreizte Daumen erhalten bleiben - fuer Raycasts ist das erlaubt,
    // und ein Rigidbody haengt hier nicht daran.
    private bool BuildMesh(MelonLogger.Instance log, Renderer renderer, Transform host)
    {
        var skinned = renderer.TryCast<SkinnedMeshRenderer>();

        if (skinned is null || skinned == null)
        {
            log.Msg("  hand hit: the hand renderer is not skinned, taking the box");
            return BuildBox(log, renderer, host, 0f);
        }

        if (baked is not null && baked != null)
            UnityEngine.Object.Destroy(baked);

        baked = new Mesh();
        skinned.BakeMesh(baked);

        var points = baked.vertices;
        var count = points is null ? 0 : points.Length;

        if (count == 0)
        {
            log.Warning("  hand hit: the baked hand mesh reports no vertices");
            return false;
        }

        var collider = host.gameObject.AddComponent<MeshCollider>();

        if (collider is null || collider == null)
        {
            log.Warning("  hand hit: MeshCollider could not be added, taking the box");
            return BuildBox(log, renderer, host, 0f);
        }

        collider.convex = false;
        collider.isTrigger = false;
        collider.sharedMesh = baked;
        shape = collider;

        log.Msg($"  hand hit: baked {count} vertices into a mesh collider");
        return true;
    }

    // Der Rueckfall, und gleichzeitig der Vergleichswert: derselbe Quader, den
    // 1.41 benutzt hat. Er hat die Mechanik belegt - Maske, Ebene, Puls - und
    // ist nur der Form nach zu grob.
    private bool BuildBox(MelonLogger.Instance log, Renderer renderer, Transform host,
        float padding)
    {
        if (!MeasureBox(log, renderer, padding, out var center, out var size))
            return false;

        var collider = host.gameObject.AddComponent<BoxCollider>();

        if (collider is null || collider == null)
        {
            log.Warning("  hand hit: BoxCollider could not be added");
            return false;
        }

        collider.isTrigger = false;
        collider.center = center;
        collider.size = size;
        boxSize = size;
        shape = collider;
        return true;
    }

    // Beim Formwechsel muss der alte Collider weg, sonst stehen zwei
    // uebereinander und der groebere gewinnt jeden Treffer.
    private void DropShape(Transform host)
    {
        var mesh = host.GetComponent<MeshCollider>();

        if (mesh is not null && mesh != null)
            UnityEngine.Object.Destroy(mesh);

        var box = host.GetComponent<BoxCollider>();

        if (box is not null && box != null)
            UnityEngine.Object.Destroy(box);

        shape = null;
    }

    private bool PickLayer(MelonLogger.Instance log, Transform host)
    {
        layer = FreeLayer();

        if (layer < 0)
        {
            // Keine freie Ebene: dann die des Knotens nehmen und im Log sagen,
            // dass die Maskenerweiterung damit mehr trifft als die Hand.
            layer = host.gameObject.layer;
            log.Warning("  hand hit: no free layer, using the node's own "
                + $"({layer}, \"{LayerMask.LayerToName(layer)}\") - the wash mask "
                + "then also covers everything else on it");
            return true;
        }

        IsolateLayer(layer);
        return true;
    }

    // DER EIGENE KNOTEN FUER DEN COLLIDER. Auf Identitaet unter dem Renderer,
    // damit sein Raum dessen Raum ist; ein eigener Name ist hier richtig, denn
    // der Knoten gehoert dem Mod und keinem Asset.
    private const string NodeName = "WetRealityHandHit";

    private static Transform? CollisionNode(Transform renderer)
    {
        var existing = renderer.Find(NodeName);

        if (existing is not null && existing != null)
            return existing;

        var created = new GameObject(NodeName);
        var node = created.transform;

        node.SetParent(renderer, false);
        node.localPosition = Vector3.zero;
        node.localRotation = Quaternion.identity;
        node.localScale = Vector3.one;

        return node;
    }

    // Der erste Mesh- oder SkinnedMesh-Renderer unter der Hand. Strukturell,
    // kein Name: der Klon heisst je Asset anders, und ein Name waere die
    // Bruchstelle, die Abschnitt 113 fuer die Koerpergeometrie beseitigt hat.
    private static Renderer? FirstGeometry(Transform root)
    {
        var all = root.GetComponentsInChildren<Renderer>(true);

        if (all is null)
            return null;

        for (var index = 0; index < all.Length; index++)
        {
            var renderer = all[index];

            if (renderer is null || renderer == null)
                continue;

            var type = TypeName(renderer);

            if (string.Equals(type, "SkinnedMeshRenderer", StringComparison.Ordinal)
                || string.Equals(type, "MeshRenderer", StringComparison.Ordinal))
                return renderer;
        }

        return null;
    }

    // Die Box aus den Eckpunkten. Fuer einen SkinnedMeshRenderer aus der
    // GEBACKENEN Geometrie, damit die Bindepose nicht gegen die gestellte
    // steht; fuer einen MeshRenderer aus dem geteilten Mesh.
    private static bool MeasureBox(MelonLogger.Instance log, Renderer renderer,
        float padding, out Vector3 center, out Vector3 size)
    {
        center = Vector3.zero;
        size = Vector3.zero;

        Mesh? mesh = null;
        var temporary = false;

        try
        {
            var skinned = renderer.TryCast<SkinnedMeshRenderer>();

            if (skinned is not null && skinned != null)
            {
                mesh = new Mesh();
                skinned.BakeMesh(mesh);
                temporary = true;
            }
            else
            {
                var filter = renderer.GetComponent<MeshFilter>();
                mesh = filter is null || filter == null ? null : filter.sharedMesh;
            }

            if (mesh is null || mesh == null)
            {
                log.Warning("  hand hit: the hand renderer carries no readable mesh");
                return false;
            }

            var points = mesh.vertices;

            if (points is null || points.Length == 0)
            {
                log.Warning("  hand hit: the hand mesh reports no vertices");
                return false;
            }

            var min = points[0];
            var max = points[0];

            for (var index = 1; index < points.Length; index++)
            {
                var point = points[index];

                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }

            center = (min + max) * 0.5f;
            size = max - min;

            if (padding != 0f)
                size += new Vector3(padding, padding, padding) * 2f;

            log.Msg($"  hand hit: measured {points.Length} vertices"
                + $"   {(temporary ? "baked pose" : "shared mesh")}"
                + $"   box {size.x:0.###} x {size.y:0.###} x {size.z:0.###} m");

            return size.x > 0.0001f && size.y > 0.0001f && size.z > 0.0001f;
        }
        catch (Exception exception)
        {
            log.Warning($"  hand hit: mesh read threw {exception.GetType().Name}: "
                + exception.Message);
            return false;
        }
        finally
        {
            if (temporary && mesh is not null && mesh != null)
                UnityEngine.Object.Destroy(mesh);
        }
    }

    // Die erste unbenannte Ebene von oben. Unbenannt heisst in Unity frei, und
    // von oben, weil die niedrigen Indizes die eingebauten sind.
    private static int FreeLayer()
    {
        for (var index = 31; index >= 8; index--)
            if (string.IsNullOrEmpty(LayerMask.LayerToName(index)))
                return index;

        return -1;
    }

    // Diese Ebene stoesst nichts an. Raycasts fragen die Maske, nicht die
    // Kollisionsmatrix - der Collider bleibt also trefferbar.
    private static void IsolateLayer(int layer)
    {
        for (var other = 0; other < 32; other++)
            Physics.IgnoreLayerCollision(layer, other, true);
    }

    private static string LayerNames(int mask)
    {
        var names = "";

        for (var index = 0; index < 32; index++)
        {
            if ((mask & (1 << index)) == 0)
                continue;

            var name = LayerMask.LayerToName(index);

            names += (names.Length == 0 ? "" : ", ")
                + (string.IsNullOrEmpty(name) ? index.ToString() : name);
        }

        return names.Length == 0 ? "none" : names;
    }

    // Der ECHTE IL2CPP-Typ, nicht GetType().Name: GetComponentsInChildren gibt
    // jeden Eintrag als Renderer-Huelle zurueck. Dieselbe Stelle wie in
    // GunRender.PlainGeometry.
    private static string TypeName(Renderer renderer)
    {
        try
        {
            return renderer.GetIl2CppType()?.Name ?? "";
        }
        catch
        {
            return "";
        }
    }
}
