using MelonLoader;
using UnityEngine;

namespace WetReality;

// DIE VR-HAENDE DES SPIELS, EINGEHAENGT.
//
// Rechts (bei Rechtshaendern) die Hand an der Pistole, links eine
// Interaktionshand, damit beim Bedienen von Pistole und animierten Objekten
// nicht ins Leere gegriffen wird. Nicht animiert - Sichtbarkeit genuegt.
//
// DIE SCHLUESSEL SIND GEMESSEN, nicht geraten. Die Aufzaehlung in HandAssets
// hat 292798 Schluessel durchlaufen und diese fuenf gefunden; von den fuenf
// taugen genau die zwei FBX:
//
//     Rig_VRHand_L   1 Renderer  L_Hand  materials 1 "Lit / URP/Lit"
//                    bones 23    rootBone L_Wrist    51 Knoten
//     Rig_VRHand_R   1 Renderer  R_Hand  materials 1 "Lit / URP/Lit"
//                    bones 23    rootBone R_Wrist    51 Knoten
//
// Zwei unabhaengige, voll artikulierte Haende MIT Material - kein Spiegeln,
// kein Material beistellen, keine invertierten Normalen. Player_XRHand.prefab
// ist dagegen ein Entwicklerrest: zwei Renderer, BEIDE L_Hand, Material none,
// und der Pfad nennt es selbst - HandOffset/Rig_VRHand_L_PermaGrip_DeleteMe.
//
// WARUM DIE PFADE JETZT FEST IM CODE STEHEN, obwohl Abschnitt 117 das Gegenteil
// gelehrt hat: dort war der Name GERATEN. Diese Pfade sind aus dem laufenden
// Spiel abgelesen. Und die Aufzaehlung kostet 480 ms, davon 135822 Schluessel
// des ersten Locators in EINEM Frame - als Dauerzustand ein Ruckler. Sie bleibt
// hinter ProbeHandAssets, falls ein DLC die Pfade aendert; dann sagt ein Lauf
// die neuen.
//
// KEINE AUFRUFE AUF INTERFACE-WRAPPERN. Die Lehre aus dem Absturz von 1.27.0
// gilt hier genauso: nur konkrete Typen, nur deren eigene Member.
internal sealed class VrHands
{
    private const string LeftKey =
        "Assets/PWS/Content/Core/FBX/Oculus/Rig_VRHand_L.fbx";

    private const string RightKey =
        "Assets/PWS/Content/Core/FBX/Oculus/Rig_VRHand_R.fbx";

    private GameObject? holder;
    private GameObject? washerHand;
    private GameObject? offHand;

    // 0 = noch nichts, 1 = Pistolenhand angefragt, 2 = Off-Hand angefragt,
    // 3 = fertig. Ein Schritt pro Aufruf, damit kein Frame am Laden haengt.
    private int step;
    private bool failed;
    private float deadline;

    // Nullable, weil Il2CppInterop generische Wertetypen als KLASSE herausgibt -
    // siehe HandAssets.
    private UnityEngine.ResourceManagement.AsyncOperations
        .AsyncOperationHandle<GameObject>? handle;

    internal string Status { get; private set; } = "hands: off";

    // Fuer GunRender: alles unterhalb dieses Knotens ist die eigene Hand des
    // Mods und darf nicht als Koerpergeometrie ausgeblendet werden.
    internal Transform? WasherHandRoot => washerHand is null || washerHand == null
        ? null
        : washerHand.transform;

    // Fuer HandSpray: unter diesem Knoten haengt die Geometrie, die der
    // Strahl treffen soll. Dieselbe Form wie oben - eine Referenz, kein Name.
    internal Transform? OffHandRoot => offHand is null || offHand == null
        ? null
        : offHand.transform;

    // DIE EBENE DER HAENDE, damit sie im selben Durchgang wie die Pistole
    // gezeichnet werden. Rekursiv, denn ein SkinnedMeshRenderer haengt tiefer
    // als die Wurzel.
    //
    // AUSGENOMMEN IST DER TREFFER-KNOTEN: er traegt die freie Ebene, die in der
    // Waschmaske des Spiels steht, und die darf ihm die Umlagerung nicht
    // nehmen. Er wird ueber seinen eigenen Namen erkannt - der Knoten gehoert
    // dem Mod, und das ist der eine Fall, in dem ein Name das richtige Mittel
    // ist.
    internal void ApplyLayer(MelonLogger.Instance log, int layer, string except)
    {
        if (layer < 0 || layer > 31)
            return;

        var changed = Move(washerHand, layer, except) + Move(offHand, layer, except);

        if (changed == 0 || layer == loggedLayer)
            return;

        loggedLayer = layer;
        log.Msg($"  vr hands: {changed} node(s) moved to layer {layer} "
            + $"(\"{LayerMask.LayerToName(layer)}\") so the washer's pass draws them");
    }

    private static int Move(GameObject? hand, int layer, string except)
    {
        if (hand is null || hand == null)
            return 0;

        var nodes = hand.GetComponentsInChildren<Transform>(true);

        if (nodes is null)
            return 0;

        var changed = 0;

        for (var index = 0; index < nodes.Length; index++)
        {
            var node = nodes[index];

            if (node is null || node == null)
                continue;

            if (string.Equals(node.name, except, StringComparison.Ordinal))
                continue;

            // Ein Kind des Treffer-Knotens gibt es nicht, aber ein Elternteil
            // mit diesem Namen darf seine Kinder mitnehmen - darum wird der
            // Knoten selbst geprueft und nicht seine Kette.
            var target = node.gameObject;

            if (target is null || target == null || target.layer == layer)
                continue;

            target.layer = layer;
            changed++;
        }

        return changed;
    }

    private bool lastWasherIsRight = true;
    private int loggedLayer = -1;
    private int shadowState = -1;
    private int shadowWasherId;
    private int shadowOffId;
    private bool reportedSkin;
    private int flippedWasherId;
    private int flippedOffId;
    private int normalsWasherId;
    private int normalsOffId;
    private int culledWasherId;
    private int culledOffId;
    private readonly List<Material> ownMaterials = new();

    // DIE HANDSCHUHFARBE DES SPIELS - Nutzerwunsch vom 24.09.2026.
    //
    // Flach traegt der Spieler orangene Handschuhe; die VR-Haende aus dem
    // Oculus-Rig haben ein eigenes "Lit"-Material. Getoent wird _BaseColor
    // auf derselben EIGENEN Instanz, die auch die Cull-Richtung traegt - das
    // geteilte Material gehoert dem Spiel, und die debug_hand haengt daran.
    //
    // _BaseColor MULTIPLIZIERT die Grundtextur. Ob es eine gibt, sagt die
    // Logzeile; mit einer hautfarbenen Textur wird das Orange dunkler, und
    // dann ist der Farbwert nachzustellen, nicht der Weg.
    //
    // Der Ausgangswert wird je Material einmal gemerkt, damit Abschalten
    // zurueckfuehrt statt auf Weiss zu setzen.
    private int tintedWasherId;
    private int tintedOffId;
    private string tintedWith = "";
    private readonly Dictionary<IntPtr, Color> untinted = new();
    private readonly List<Mesh> ownMeshes = new();

    // DIE MESHKORREKTUR, auf einer EIGENEN KOPIE - und sie fasst NUR die
    // Normalen an.
    //
    // GEMESSEN: das R-Rig ist eine Punktspiegelung, rootBone R_Wrist liest
    // lossyScale (-1, -1, -1). Daraus folgen zwei Dinge, die Unity
    // VERSCHIEDEN behandelt:
    //
    //   DIE WICKLUNG kompensiert Unity selbst - bei ungerader Zahl negativer
    //   Skalierungskomponenten dreht die Pipeline die Cull-Richtung mit.
    //
    //   DIE NORMALEN kompensiert Unity NICHT: die inverse Transponierte ist
    //   hier -1, sie zeigen also nach innen, und die Flaeche liest dunkel.
    //
    // 1.48.0 hat darum das Falsche angefasst. Der Dreiecksflip hat ueber
    // RecalculateNormals die Farbe richtiggestellt UND die Wicklung gedreht,
    // die vorher stimmte - gemeldet als "Farbe jetzt natuerlich, Innenseiten
    // weiter sichtbar". Genau die Trennung, die dieser Abschnitt nachholt:
    // Dreiecke bleiben, Normalen werden negiert.
    //
    // Das geteilte Mesh gehoert dem SPIEL - die spieleigene debug_hand haengt
    // am L-Mesh, und eine Aenderung daran wuerde sie mitnehmen. Gearbeitet wird
    // darum an einer Instanz, und die haelt diese Klasse, bis sie sie selbst
    // freigibt.
    //
    // side ist die ASSET-Seite ("r"/"l"), nicht die Rolle: bei einem
    // Linkshaender haelt die linke Hand die Pistole, aber R bleibt R.
    // DIE CULL-RICHTUNG AM MATERIAL, und das ist der Eingriff, der von der
    // Lesbarkeit der Geometrie unabhaengig ist.
    //
    // GEMESSEN: cull 2 (einseitig, Rueckseiten weg), zwrite 1, und der
    // Wurzelknochen der rechten Hand liest (-1, -1, -1). Eine Punktspiegelung
    // dreht die Flaechenorientierung, und Unitys eigene Kompensation haengt an
    // der RENDERER-Transformation - die liest scale 1. Die Spiegelung steckt in
    // den KNOCHEN, wo diese Kompensation nicht hinsieht. Mit cull 2 wird also
    // die zugewandte Seite weggeschnitten: "man sieht die Innenseiten".
    //
    // Geschrieben wird auf renderer.material - eine EIGENE Instanz. Das
    // geteilte Material gehoert dem Spiel, und die spieleigene Hand haengt am
    // selben Asset.
    internal void ApplyCull(MelonLogger.Instance log, string mode)
    {
        var washerId = washerHand is null || washerHand == null
            ? 0
            : washerHand.GetInstanceID();
        var offId = offHand is null || offHand == null ? 0 : offHand.GetInstanceID();

        if (washerId != 0 && washerId != culledWasherId)
        {
            culledWasherId = washerId;
            Cull(log, washerHand, lastWasherIsRight ? "R" : "L", mode);
        }

        if (offId != 0 && offId != culledOffId)
        {
            culledOffId = offId;
            Cull(log, offHand, lastWasherIsRight ? "L" : "R", mode);
        }
    }

    private void Cull(MelonLogger.Instance log, GameObject? hand, string label,
        string mode)
    {
        if (hand is null || hand == null || string.Equals(mode, "none",
            StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            var renderers = hand.GetComponentsInChildren<Renderer>(true);

            if (renderers is null)
                return;

            for (var index = 0; index < renderers.Length; index++)
            {
                var renderer = renderers[index];

                if (renderer is null || renderer == null)
                    continue;

                var skinned = renderer.TryCast<SkinnedMeshRenderer>();
                var root = skinned is null || skinned == null ? null : skinned.rootBone;
                var scale = root is null || root == null
                    ? Vector3.one
                    : root.lossyScale;
                var mirrored = scale.x < 0f || scale.y < 0f || scale.z < 0f;

                // "auto" ist eine REGEL und keine Vorliebe: eine negative
                // Determinante in der Knochenkette dreht die
                // Flaechenorientierung, also wird die Cull-Richtung gedreht.
                // Eine Hand ohne Spiegelung bleibt unberuehrt.
                var wanted = mode switch
                {
                    "front" or "Front" => 1f,
                    "back" or "Back" => 2f,
                    "off" or "Off" => 0f,
                    _ => mirrored ? 1f : -1f,
                };

                if (wanted < 0f)
                    continue;

                var material = renderer.material;

                if (material is null || material == null)
                    continue;

                var before = material.GetFloat("_Cull");

                material.SetFloat("_Cull", wanted);
                Own(material);

                log.Msg($"  vr hands: {label} cull {before:0.#} -> {wanted:0.#}"
                    + $"   ({(mirrored ? "mirrored rig" : "not mirrored")}, mode \"{mode}\")");
            }
        }
        catch (Exception exception)
        {
            log.Warning($"  vr hands: setting the {label} cull mode threw "
                + $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    // WAS AN DIESEM ASSET UEBERHAUPT OPERIERBAR IST. Die Normalenkorrektur aus
    // 1.49.0 hat "carries no normals" gemeldet - eine nicht CPU-lesbare
    // Geometrie liefert leere Kanaele. Hier stehen die Zahlen, statt dass es
    // beim naechsten Versuch wieder eine Vermutung ist.
    private static void Channels(MelonLogger.Instance log, string label,
        SkinnedMeshRenderer skinned)
    {
        try
        {
            var mesh = skinned.sharedMesh;

            if (mesh is null || mesh == null)
                return;

            var normals = mesh.normals;
            var triangles = mesh.triangles;
            var tangents = mesh.tangents;

            log.Msg($"    mesh {label}: readable {mesh.isReadable}"
                + $"   vertices {mesh.vertexCount}"
                + $"   normals {(normals is null ? 0 : normals.Length)}"
                + $"   triangles {(triangles is null ? 0 : triangles.Length)}"
                + $"   tangents {(tangents is null ? 0 : tangents.Length)}"
                + $"   bindposes {(mesh.bindposes is null ? 0 : mesh.bindposes.Length)}");
        }
        catch (Exception exception)
        {
            log.Warning($"    mesh {label} read threw {exception.GetType().Name}: "
                + exception.Message);
        }
    }

    // NUR DIE NORMALEN, die Dreiecke bleiben. Nach der -1-Transformation
    // zeigen negierte Normalen nach aussen, und die Cull-Richtung bleibt
    // Unitys Sache - die kompensiert sie bereits.
    internal bool ApplyNormals(MelonLogger.Instance log, string mode)
    {
        var washerId = washerHand is null || washerHand == null
            ? 0
            : washerHand.GetInstanceID();
        var offId = offHand is null || offHand == null ? 0 : offHand.GetInstanceID();
        var fixedAny = false;

        if (washerId != 0 && washerId != normalsWasherId)
        {
            normalsWasherId = washerId;

            if (Wanted(mode, lastWasherIsRight))
                fixedAny |= Negate(log, washerHand, lastWasherIsRight ? "R" : "L");
        }

        if (offId != 0 && offId != normalsOffId)
        {
            normalsOffId = offId;

            if (Wanted(mode, !lastWasherIsRight))
                fixedAny |= Negate(log, offHand, lastWasherIsRight ? "L" : "R");
        }

        return fixedAny;
    }

    private bool Negate(MelonLogger.Instance log, GameObject? hand, string label)
    {
        if (hand is null || hand == null)
            return false;

        try
        {
            var renderers = hand.GetComponentsInChildren<Renderer>(true);

            if (renderers is null)
                return false;

            var done = 0;

            for (var index = 0; index < renderers.Length; index++)
            {
                var renderer = renderers[index];

                if (renderer is null || renderer == null)
                    continue;

                var skinned = renderer.TryCast<SkinnedMeshRenderer>();

                if (skinned is null || skinned == null)
                    continue;

                var source = skinned.sharedMesh;

                if (source is null || source == null)
                    continue;

                var clone = UnityEngine.Object.Instantiate(source);

                if (clone is null || clone == null)
                    continue;

                var normals = clone.normals;
                var count = normals is null ? 0 : normals.Length;

                // KEINE GESPEICHERTEN NORMALEN IST EIN BEFUND, KEIN ABBRUCH.
                //
                // Gemessen in 1.49.0: clone.normals liest leer. Ein Mesh, das
                // beim Import "Calculate" bekommen hat, speichert keine
                // Normalen - der Shader arbeitet dann mit dem, was die GPU
                // liefert, und das war die dunkle Hand. RecalculateNormals
                // rechnet sie aus der Geometrie und SPEICHERT sie; genau das
                // hat in 1.48.0 die Farbe gerettet, dort aber im Paket mit
                // einem Dreiecksflip, der nicht hingehoerte.
                if (count == 0)
                {
                    clone.RecalculateNormals();
                    skinned.sharedMesh = clone;
                    ownMeshes.Add(clone);
                    done++;

                    log.Msg($"  vr hands: {label} mesh stores no normals - "
                        + "recalculated them on an own copy (triangles untouched)");
                    continue;
                }

                for (var n = 0; n < count; n++)
                    normals![n] = -normals[n];

                clone.normals = normals;

                // Die Tangenten bleiben: ihr w traegt die Haendigkeit fuer
                // Normalmaps, und dieses Material hat keine - eine Aenderung
                // waere hier ein Eingriff ohne Anlass.
                skinned.sharedMesh = clone;
                ownMeshes.Add(clone);
                done++;
            }

            if (done > 0)
                log.Msg($"  vr hands: {label} normals negated on {done} mesh(es) "
                    + "(own copy, triangles untouched)");

            return done > 0;
        }
        catch (Exception exception)
        {
            log.Warning($"  vr hands: negating the {label} normals threw "
                + $"{exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    internal bool ApplyWinding(MelonLogger.Instance log, string mode)
    {
        var washerId = washerHand is null || washerHand == null
            ? 0
            : washerHand.GetInstanceID();
        var offId = offHand is null || offHand == null ? 0 : offHand.GetInstanceID();

        var flipped = false;

        // washerIsRight steht in der Instanz, nicht im Aufruf: welche
        // Asset-Seite die Pistolenhand traegt, weiss diese Klasse selbst.
        if (washerId != 0 && washerId != flippedWasherId)
        {
            flippedWasherId = washerId;

            if (Wanted(mode, lastWasherIsRight))
                flipped |= Flip(log, washerHand, lastWasherIsRight ? "R" : "L");
        }

        if (offId != 0 && offId != flippedOffId)
        {
            flippedOffId = offId;

            if (Wanted(mode, !lastWasherIsRight))
                flipped |= Flip(log, offHand, lastWasherIsRight ? "L" : "R");
        }

        return flipped;
    }

    private static bool Wanted(string mode, bool right) =>
        mode switch
        {
            "both" => true,
            "r" or "R" => right,
            "l" or "L" => !right,
            _ => false,
        };

    private bool Flip(MelonLogger.Instance log, GameObject? hand, string label)
    {
        if (hand is null || hand == null)
            return false;

        try
        {
            var renderers = hand.GetComponentsInChildren<Renderer>(true);

            if (renderers is null)
                return false;

            var done = 0;

            for (var index = 0; index < renderers.Length; index++)
            {
                var renderer = renderers[index];

                if (renderer is null || renderer == null)
                    continue;

                var skinned = renderer.TryCast<SkinnedMeshRenderer>();

                if (skinned is null || skinned == null)
                    continue;

                var source = skinned.sharedMesh;

                if (source is null || source == null)
                    continue;

                // MEHRERE SUBMESHES WAEREN EIN ANDERER FALL: mesh.triangles
                // flacht sie ein, und das Zurueckschreiben legt alles in
                // Submesh 0. Gemessen ist ein Material je Hand, also ein
                // Submesh - trifft das einmal nicht zu, bleibt die Hand
                // unberuehrt und sagt es.
                if (source.subMeshCount != 1)
                {
                    log.Warning($"  vr hands: {label} mesh has "
                        + $"{source.subMeshCount} submeshes - winding left alone");
                    continue;
                }

                var clone = UnityEngine.Object.Instantiate(source);

                if (clone is null || clone == null)
                    continue;

                var triangles = clone.triangles;
                var count = triangles is null ? 0 : triangles.Length;

                for (var t = 0; t + 2 < count; t += 3)
                {
                    var swap = triangles![t];
                    triangles[t] = triangles[t + 2];
                    triangles[t + 2] = swap;
                }

                clone.triangles = triangles;

                // OHNE DIES BLEIBT ES DUNKEL: die Wicklung entscheidet, welche
                // Seite gezeichnet wird, die Normalen, wie sie beleuchtet wird.
                clone.RecalculateNormals();
                clone.RecalculateTangents();

                skinned.sharedMesh = clone;
                ownMeshes.Add(clone);
                done++;
            }

            if (done > 0)
                log.Msg($"  vr hands: {label} winding reversed on {done} mesh(es) "
                    + "(own copy, normals and tangents recalculated)");

            return done > 0;
        }
        catch (Exception exception)
        {
            log.Warning($"  vr hands: flipping the {label} winding threw "
                + $"{exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    // DIE SCHATTEN DER EGO-GEOMETRIE, als Schalter und ohne Vorgabewechsel:
    // eine Hand am Griff liegt unter dem Pistolenkoerper und damit in dessen
    // Schatten. Ob das stoert, entscheidet das Bild nach dem Wicklungsflip.
    internal void ApplyShadows(MelonLogger.Instance log, bool wantShadows)
    {
        var washerId = washerHand is null || washerHand == null
            ? 0
            : washerHand.GetInstanceID();
        var offId = offHand is null || offHand == null ? 0 : offHand.GetInstanceID();
        var wanted = wantShadows ? 1 : 0;

        if (shadowState == wanted && shadowWasherId == washerId
            && shadowOffId == offId)
            return;

        shadowState = wanted;
        shadowWasherId = washerId;
        shadowOffId = offId;

        var touched = Shadows(washerHand, wantShadows) + Shadows(offHand, wantShadows);

        if (touched == 0)
            return;

        log.Msg($"  vr hands: shadows {(wantShadows ? "ON" : "off")} on "
            + $"{touched} renderer(s)");
    }

    private static int Shadows(GameObject? hand, bool wantShadows)
    {
        if (hand is null || hand == null)
            return 0;

        var renderers = hand.GetComponentsInChildren<Renderer>(true);

        if (renderers is null)
            return 0;

        var touched = 0;

        for (var index = 0; index < renderers.Length; index++)
        {
            var renderer = renderers[index];

            if (renderer is null || renderer == null)
                continue;

            renderer.shadowCastingMode = wantShadows
                ? UnityEngine.Rendering.ShadowCastingMode.On
                : UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = wantShadows;
            touched++;
        }

        return touched;
    }

    // WAS DIE HAENDE UNTERSCHEIDET, einmal je Sitzung und fuer beide dieselben
    // Groessen - dieselbe Frage, dieselben Filter.
    //
    // Der Wurzelknochen ist der Punkt: eine negative Komponente dort dreht die
    // Wicklung eines Skinned Mesh, und auf Objektebene ist die Spiegelung schon
    // ausgeschieden (beide scale 1).
    internal void ReportSkin(MelonLogger.Instance log)
    {
        if (reportedSkin || washerHand is null || washerHand == null
            || offHand is null || offHand == null)
            return;

        reportedSkin = true;

        Skin(log, "washer     ", washerHand);
        Skin(log, "interaction", offHand);
    }

    private static void Skin(MelonLogger.Instance log, string label, GameObject hand)
    {
        try
        {
            var renderers = hand.GetComponentsInChildren<Renderer>(true);
            var count = renderers is null ? 0 : renderers.Length;

            for (var index = 0; index < count; index++)
            {
                var renderer = renderers![index];

                if (renderer is null || renderer == null)
                    continue;

                var material = renderer.sharedMaterial;
                var shader = material is null || material == null
                        || material.shader is null || material.shader == null
                    ? "none"
                    : material.shader.name;

                var skinned = renderer.TryCast<SkinnedMeshRenderer>();
                var root = skinned is null || skinned == null ? null : skinned.rootBone;
                var scale = root is null || root == null
                    ? Vector3.one
                    : root.lossyScale;
                var mirrored = scale.x < 0f || scale.y < 0f || scale.z < 0f;
                var mesh = skinned is null || skinned == null
                    ? null
                    : skinned.sharedMesh;

                // _Cull und _ZWrite DIREKT gelesen, nicht ueber HasProperty:
                // das verschweigt undeklarierte Uniformen (Abschnitt 103) und
                // waere hier ein Tor, das einen Lauf kostet. Liest _Cull 0,
                // waeren beide Seiten gezeichnet - dann ist die Frage eine
                // andere.
                var cull = "?";
                var zwrite = "?";

                try
                {
                    if (material is not null && material != null)
                    {
                        cull = material.GetFloat("_Cull").ToString("0.#");
                        zwrite = material.GetFloat("_ZWrite").ToString("0.#");
                    }
                }
                catch
                {
                    // Ein nicht lesbares Materialfeld ist ein Befund, keine
                    // Ausnahme - das Fragezeichen steht dann im Log.
                }

                if (skinned is not null && skinned != null)
                    Channels(log, label, skinned);

                log.Msg($"    skin {label}: {renderer.name,-10} shader \"{shader}\""
                    + $"   submeshes {(mesh is null || mesh == null ? 0 : mesh.subMeshCount)}"
                    + $"   cull {cull}   zwrite {zwrite}"
                    + $"   cast {renderer.shadowCastingMode}"
                    + $"   receive {renderer.receiveShadows}"
                    + $"   rootBone {(root is null || root == null ? "-" : root.name)}"
                    + $"   bone scale ({scale.x:0.###}, {scale.y:0.###}, {scale.z:0.###})"
                    + $"{(mirrored ? "   MIRRORED" : "")}");
            }
        }
        catch (Exception exception)
        {
            log.Warning($"    skin {label} threw {exception.GetType().Name}: "
                + exception.Message);
        }
    }


    // Pro Frame zwei Ganzzahl- und ein Referenzvergleich; geschrieben wird nur
    // bei einer neuen Hand oder einem geaenderten Wunsch.
    internal void ApplyTint(MelonLogger.Instance log, bool enable, string colour)
    {
        var washerId = washerHand is null || washerHand == null
            ? 0
            : washerHand.GetInstanceID();
        var offId = offHand is null || offHand == null ? 0 : offHand.GetInstanceID();
        var wanted = enable ? colour : "";

        if (washerId == tintedWasherId && offId == tintedOffId
            && string.Equals(wanted, tintedWith, StringComparison.Ordinal))
            return;

        tintedWasherId = washerId;
        tintedOffId = offId;
        tintedWith = wanted;

        Color? tint = null;

        if (enable)
        {
            if (!TryParseColour(colour, out var parsed))
            {
                log.Warning($"  vr hands: HandTintColor \"{colour}\" is not #RRGGBB, "
                    + "hands stay untinted");
                return;
            }

            tint = parsed;
        }

        Tint(log, washerHand, lastWasherIsRight ? "R" : "L", tint);
        Tint(log, offHand, lastWasherIsRight ? "L" : "R", tint);
    }

    private void Tint(MelonLogger.Instance log, GameObject? hand, string label,
        Color? tint)
    {
        if (hand is null || hand == null)
            return;

        try
        {
            var renderers = hand.GetComponentsInChildren<Renderer>(true);

            if (renderers is null)
                return;

            for (var index = 0; index < renderers.Length; index++)
            {
                var renderer = renderers[index];

                if (renderer is null || renderer == null)
                    continue;

                // BEIM ABSCHALTEN KEINE NEUE INSTANZ: sharedMaterial ist nach
                // einem renderer.material schon die eigene, und eine Hand, die
                // nie getoent wurde, hat nichts zurueckzufuehren.
                var material = tint is null ? renderer.sharedMaterial : renderer.material;

                if (material is null || material == null)
                    continue;

                var key = material.Pointer;
                var before = material.GetColor("_BaseColor");

                if (tint is null && !untinted.ContainsKey(key))
                    continue;

                if (!untinted.ContainsKey(key))
                    untinted[key] = before;

                if (tint is not null)
                    Own(material);

                var after = tint ?? untinted[key];
                material.SetColor("_BaseColor", after);

                var map = material.GetTexture("_BaseMap");

                log.Msg($"  vr hands: {label} tint {Rgb(before)} -> {Rgb(after)}"
                    + $"   base map {(map is null || map == null ? "none" : "\"" + map.name + "\"")}"
                    + $"{(tint is null ? "   (restored)" : "")}");
            }
        }
        catch (Exception exception)
        {
            log.Warning($"  vr hands: tinting the {label} hand threw "
                + $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    // ApplyCull und ApplyTint schreiben auf DIESELBE Instanz - sie darf nur
    // einmal in der Freigabeliste stehen.
    private void Own(Material material)
    {
        for (var index = 0; index < ownMaterials.Count; index++)
        {
            var known = ownMaterials[index];

            if (known is not null && known != null && known.Pointer == material.Pointer)
                return;
        }

        ownMaterials.Add(material);
    }

    // #RRGGBB, wie es im Farbwaehler steht. SetColor nimmt den Wert als sRGB
    // und rechnet ihn im linearen Projekt selbst um.
    private static bool TryParseColour(string text, out Color colour)
    {
        colour = Color.white;
        var hex = (text ?? "").Trim().TrimStart('#');

        if (hex.Length != 6 || !int.TryParse(hex,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var value))
            return false;

        colour = new Color(((value >> 16) & 255) / 255f, ((value >> 8) & 255) / 255f,
            (value & 255) / 255f, 1f);
        return true;
    }

    private static string Rgb(Color colour) =>
        $"#{Mathf.RoundToInt(Mathf.Clamp01(colour.r) * 255f):X2}"
        + $"{Mathf.RoundToInt(Mathf.Clamp01(colour.g) * 255f):X2}"
        + $"{Mathf.RoundToInt(Mathf.Clamp01(colour.b) * 255f):X2}";

    internal void Reset()
    {
        // DIE EIGENEN MESHKOPIEN FREIGEBEN. Sie haengen an keinem GameObject,
        // das der Levelwechsel raeumt - ohne diese Schleife bliebe je Hand und
        // Level eine Kopie liegen.
        for (var index = 0; index < ownMeshes.Count; index++)
        {
            var mesh = ownMeshes[index];

            if (mesh is not null && mesh != null)
                UnityEngine.Object.Destroy(mesh);
        }

        ownMeshes.Clear();
        flippedWasherId = 0;
        flippedOffId = 0;
        normalsWasherId = 0;
        normalsOffId = 0;
        culledWasherId = 0;
        culledOffId = 0;

        // renderer.material legt eine Instanz an, und die raeumt niemand sonst
        // weg - dieselbe Pflicht wie bei den Meshkopien darueber.
        for (var index = 0; index < ownMaterials.Count; index++)
        {
            var material = ownMaterials[index];

            if (material is not null && material != null)
                UnityEngine.Object.Destroy(material);
        }

        ownMaterials.Clear();
        untinted.Clear();
        tintedWasherId = 0;
        tintedOffId = 0;
        tintedWith = "";
        loggedLayer = -1;
        shadowState = -1;
        shadowWasherId = 0;
        shadowOffId = 0;
        reportedSkin = false;
        Discard(ref washerHand);
        Discard(ref offHand);
        Discard(ref holder);
        step = 0;
        failed = false;
        deadline = 0f;
        handle = null;
        Status = "hands: off";
    }

    // BEIDE HAENDE HAENGEN AM EIGENEN HALTER, nicht in der Spielerhierarchie.
    //
    // Die Vorfassung hat die Pistolenhand unter debug_hand eingehaengt, weil
    // dessen Transform sie an den Griff legt - geschenkte Kalibrierung, und sie
    // blieb trotzdem unsichtbar. Sichtbar wurde nur die Off-Hand, die im
    // Weltraum gefahren wird. Also wird jetzt auch die Pistolenhand so
    // gefahren: dieselbe Mechanik, die nachweislich traegt.
    //
    // washerAnchor wird nicht mehr gebraucht, bleibt aber im Aufruf - es kostet
    // nichts, und der Knoten ist die richtige Antwort, falls doch wieder etwas
    // unter der Assembly einzuhaengen ist.
    internal void Apply(MelonLogger.Instance log, bool enable,
        Transform? washerAnchor, bool washerIsRight)
    {
        try
        {
            // WELCHE ASSET-SEITE DIE PISTOLENHAND TRAEGT. ApplyWinding braucht
            // das, und es steht nur hier: der Aufruf kennt die Rolle, nicht das
            // Asset.
            lastWasherIsRight = washerIsRight;

            if (!enable)
            {
                if (washerHand is not null || offHand is not null)
                {
                    Reset();
                    log.Msg("  vr hands: off, instances removed");
                }

                return;
            }

            if (failed)
                return;

            // Kein Neu-Anhaengen mehr: beide Haende haengen am eigenen
            // DontDestroyOnLoad-Halter und ueberleben einen Levelwechsel. Der
            // Zweig der Vorfassung existierte nur, weil debug_hand starb.
            if (step >= 3)
                return;

            if (handle is not null)
            {
                Poll(log, washerIsRight);
                return;
            }

            Request(log, washerAnchor, washerIsRight);
        }
        catch (Exception exception)
        {
            log.Warning("  vr hands: threw "
                + $"{exception.GetType().Name}: {exception.Message}");
            failed = true;
            Status = "hands: failed";
        }
    }

    private void Request(MelonLogger.Instance log, Transform? washerAnchor,
        bool washerIsRight)
    {
        if (holder is null || holder == null)
        {
            holder = new GameObject("WetRealityHands");
            UnityEngine.Object.DontDestroyOnLoad(holder);
        }

        var washerKey = washerIsRight ? RightKey : LeftKey;
        var offKey = washerIsRight ? LeftKey : RightKey;
        var key = step == 0 ? washerKey : offKey;

        log.Msg($"  vr hands: requesting the "
            + $"{(step == 0 ? "washer" : "interaction")} hand, {Short(key)}");

        handle = Load(key, holder.transform);
        deadline = Time.unscaledTime + 15f;
    }

    private static UnityEngine.ResourceManagement.AsyncOperations
        .AsyncOperationHandle<GameObject> Load(string key, Transform parent)
    {
        Il2CppSystem.Object keyObject = (Il2CppSystem.String)key;

        return UnityEngine.AddressableAssets.Addressables.InstantiateAsync(
            keyObject, parent, false, true);
    }

    private void Poll(MelonLogger.Instance log, bool washerIsRight)
    {
        var current = handle;

        if (current is null)
        {
            log.Warning("  vr hands: handle came back null");
            failed = true;
            return;
        }

        if (!current.IsDone)
        {
            if (Time.unscaledTime < deadline)
                return;

            log.Warning($"  vr hands: still not done after 15 s, "
                + $"status {current.Status}");
            failed = true;
            Status = "hands: timed out";
            return;
        }

        if (current.Status != UnityEngine.ResourceManagement.AsyncOperations
            .AsyncOperationStatus.Succeeded)
        {
            var reason = current.OperationException is null
                ? "no exception given"
                : current.OperationException.Message;

            log.Warning($"  vr hands: load FAILED, status {current.Status} - {reason}");
            failed = true;
            Status = "hands: load failed";
            return;
        }

        var instance = current.Result;
        handle = null;

        if (instance is null || instance == null)
        {
            log.Warning("  vr hands: succeeded but the result is null");
            failed = true;
            return;
        }

        if (step == 0)
        {
            washerHand = instance;
            step = 1;
            log.Msg($"  vr hands: washer hand \"{instance.name}\" ready, "
                + $"{(washerIsRight ? "right" : "left")} side");
            return;
        }

        offHand = instance;
        step = 3;
        Status = "hands: attached";
        log.Msg($"  vr hands: interaction hand \"{instance.name}\" ready, "
            + $"{(washerIsRight ? "left" : "right")} side");

        Compare(log);
    }

    // Die Off-Hand folgt dem Controller. Weltraum, weil der Halter bewusst
    // ausserhalb der Spielerhierarchie liegt und einen Levelwechsel ueberlebt.
    //
    // Die Versatzwerte sind zum Nachtrimmen im Headset gedacht - die rohe
    // Controller-Pose zeigt nicht dorthin, wo eine Hand sitzen soll, und wie
    // weit daneben sie liegt, sagt am Ende nur das Bild.
    // DIESELBE FORM WIE DriveOffHand, und das ist der Punkt: die Mechanik, die
    // bei der Interaktionshand nachweislich traegt, fuehrt jetzt auch die
    // Pistolenhand.
    internal void DriveWasherHand(Vector3 point, Quaternion rotation,
        Vector3 positionOffset, Vector3 rotationOffset)
    {
        Place(washerHand, point, rotation, positionOffset, rotationOffset);
    }

    internal void DriveOffHand(Vector3 point, Quaternion rotation,
        Vector3 positionOffset, Vector3 rotationOffset)
    {
        Place(offHand, point, rotation, positionOffset, rotationOffset);
    }

    // Der Versatz wird IN der Handdrehung gerechnet, damit er sich mit ihr
    // dreht - "5 cm vorn" bleibt vorn, egal wie die Hand gehalten wird. Die
    // Off-Hand ist damit getrimmt, also bleibt die Rechnung unveraendert.
    private static void Place(GameObject? hand, Vector3 point,
        Quaternion rotation, Vector3 positionOffset, Vector3 rotationOffset)
    {
        if (hand is null || hand == null)
            return;

        var withOffset = rotation * Quaternion.Euler(rotationOffset);

        hand.transform.SetPositionAndRotation(
            point + withOffset * positionOffset, withOffset);
    }

    // DER VERGLEICH, einmal wenn beide stehen.
    //
    // Die Off-Hand ist der Kontrollwert: sie IST sichtbar, ihre Werte
    // definieren, wie "funktioniert" aussieht. Bleibt die Pistolenhand
    // unsichtbar, sagt die Gegenueberstellung, WELCHE Groesse abweicht - Layer,
    // Skalierung, Frustum oder Platzierung - statt eines weiteren Versuchs.
    private void Compare(MelonLogger.Instance log)
    {
        try
        {
            log.Msg("  vr hands: side by side (the interaction hand is the control)");
            Describe(log, "washer     ", washerHand);
            Describe(log, "interaction", offHand);
        }
        catch (Exception exception)
        {
            log.Warning("  vr hands: compare threw "
                + $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void Describe(MelonLogger.Instance log, string label,
        GameObject? hand)
    {
        if (hand is null || hand == null)
        {
            log.Msg($"    {label}: missing");
            return;
        }

        var renderers = hand.GetComponentsInChildren<Renderer>(true);
        var renderer = renderers is null || renderers.Length == 0
            ? null
            : renderers[0];

        var camera = Camera.main;
        var distance = camera is null || camera == null
            ? -1f
            : Vector3.Distance(camera.transform.position, hand.transform.position);

        log.Msg($"    {label}: active {hand.activeInHierarchy}"
            + $"   layer {hand.layer}"
            + $"   scale {hand.transform.lossyScale.x:0.###}"
            + $"   pos {hand.transform.position}"
            + $"   camera {distance:0.##} m");

        if (renderer is null || renderer == null)
        {
            log.Msg($"    {label}: no renderer");
            return;
        }

        log.Msg($"    {label}: enabled {renderer.enabled}"
            + $"   isVisible {renderer.isVisible}"
            + $"   bounds {renderer.bounds.size}");
    }

    private static string Short(string key)
    {
        var slash = key.LastIndexOf('/');

        return slash < 0 || slash + 1 >= key.Length ? key : key.Substring(slash + 1);
    }

    private static void Discard(ref GameObject? target)
    {
        if (target is null || target == null)
        {
            target = null;
            return;
        }

        UnityEngine.Object.Destroy(target);
        target = null;
    }
}
