using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using UnityEngine;

namespace WetReality;

// DIE VR-HAENDE DES SPIELS: SCHLUESSEL AUFZAEHLEN, DANN LADEN UND BERICHTEN.
//
// Gewuenscht sind zwei unabhaengige Handmodelle an den Controllern: rechts die
// Hand an der Pistole, links eine Interaktionshand. Ueber das Armrig geht das
// nicht - fps_arms und fps_gloves sind je EIN Skinned Mesh ueber beide Seiten
// auf einem 56-Knochen-Rig mit Wurzel am Chest (Abschnitt 53, inzwischen auch
// fuer die Handschuhe belegt).
//
// Das Spiel bringt ein eigenes Paar mit. Der Katalog fuehrt Rig_VRHand_L.fbx,
// Rig_VRHand_R.fbx, Player_XRHand.prefab samt Animatoren und Masken - und
// DLC/HUT/catalog_HUT.bin fuehrt denselben Satz unter denselben Namen. Ein Paar
// fuer alle Inhalte, outfitunabhaengig.
//
// WARUM DIESE FASSUNG NICHT MEHR RAET
//
// Die Vorfassung hat die DATEINAMEN als Adressierungsschluessel benutzt und
// dafuer einen Lauf bezahlt:
//
//     InvalidKeyException: No Location found for Key=Rig_VRHand_L
//     ... dasselbe fuer Rig_VRHand_R und Player_XRHand
//
// Der Ladeweg selbst war damit belegt - Handle, Polling, Status und eine
// lesbare Fehlermeldung, alles kam an. Falsch war nur der Schluessel. Was im
// catalog.bin steht, ist eine alphabetisch sortierte DATEINAMEN-Tabelle
// ("ring.mat", "ring1.mat", "Rig_PlayerArms.fbx", "Rig_VRHand_L.fbx"), also
// nicht die Schluessel, unter denen Addressables die Assets adressiert.
//
// Also wird jetzt AUFGEZAEHLT statt geraten, und die gefundenen Schluessel
// werden im GLEICHEN Lauf geladen. Das ist der Unterschied zwischen einem
// Testlauf und zwei.
//
// Der Weg dorthin ist konkret und braucht keine Generika-Kunststuecke:
//
//     Addressables.Instance     -> AddressablesImpl
//     impl.m_ResourceLocators   -> List<ResourceLocatorInfo>   (konkret)
//     info.Locator              -> IResourceLocator
//     NativeTypeOf(locator)     -> der ECHTE Typ, ueber den Zeiger
//     TryCast auf konkret       -> und nur dessen eigene Felder lesen
//
// KEIN MEMBER-AUFRUF AUF EINEM INTERFACE-WRAPPER, und das ist die Lehre aus
// einem Absturz.
//
// 1.27.0 hat locator.LocatorId und locator.Keys gerufen - Eigenschaften des
// Interfaces IResourceLocator - und das Spiel starb nativ. Das Log endet exakt
// auf "8 locator(s)", ohne Schleifenzeile und ohne Ausnahme, in zwei Sitzungen
// an derselben Stelle; ein managed try/catch sieht einen Zugriffsfehler auf
// IL2CPP-Ebene nicht.
//
// Il2CppInterop kann Interface-Member nicht verlaesslich aufloesen: der Aufruf
// braucht den Interface-Offset der KONKRETEN Klasse, und den kennt ein
// Interface-Wrapper nicht. 1.26.0 hat im selben Schleifenrumpf nur TryCast und
// ToString gerufen - das lief. Der Unterschied sind genau diese zwei
// Eigenschaftszugriffe.
//
// Der Typname kommt deshalb ueber den ZEIGER, nach dem Muster aus
// WetReality.Discovery.TypeNameOf: il2cpp_object_get_class plus
// il2cpp_class_get_name fassen keinen Member an. Gelesen wird nur nach einem
// TryCast auf einen konkreten Typ, aus dessen eigenen Feldern.
//
// DER LADEAUFRUF bleibt der nicht-generische. Die Form steht im
// NativeMethodInfoPtr-Namen, nicht im Interop-public:
//
//     InstantiateAsync_Public_Static_AsyncOperationHandle_1_GameObject
//         _Object_Transform_Boolean_Boolean_0
//
// KEIN await - in einem MelonLoader-Mod gibt es keinen
// Synchronisationskontext, also wird gepollt.
internal sealed class HandAssets
{
    // Wonach in den Schluesseln gesucht wird. Bewusst zwei Stichworte und
    // bewusst ohne Dateiendung: der Schluessel kann ein Pfad, ein Label oder
    // eine GUID-nahe Zeichenkette sein, und welche Form es ist, sagt erst der
    // Lauf.
    private static readonly string[] Needles = { "VRHand", "XRHand" };

    // Was die Aufzaehlung gefunden hat. Diese werden geladen - nicht geratene
    // Namen.
    private readonly List<string> keys = new();

    private GameObject? holder;
    private int index;
    private bool enumerated;
    private bool finished;
    private bool live;
    private float deadline;

    // NULLABLE, und das ist eine Messung: der Compiler hat CS8618 hierauf
    // gemeldet. AsyncOperationHandle<T> ist in Unitys Quelltext eine STRUKTUR -
    // eine Null-Warnung ist nur bei einem Referenztyp moeglich, also gibt
    // Il2CppInterop generische Wertetypen als KLASSE heraus. Ein Zugriff ohne
    // Pruefung waere ein stiller Absturz im falschen Frame.
    private UnityEngine.ResourceManagement.AsyncOperations
        .AsyncOperationHandle<GameObject>? handle;

    internal string Status { get; private set; } = "hands: not probed";

    internal void Reset()
    {
        Destroy();
        keys.Clear();
        index = 0;
        enumerated = false;
        finished = false;
        live = false;
        deadline = 0f;
        Status = "hands: not probed";
    }

    // Einmal pro Sitzung. Jeder Aufruf treibt HOECHSTENS EINEN Schritt voran,
    // damit kein Frame am Laden haengt.
    internal void Probe(MelonLogger.Instance log)
    {
        if (finished)
            return;

        try
        {
            if (!enumerated)
            {
                enumerated = true;
                Enumerate(log);
                return;
            }

            if (!live)
            {
                Request(log);
                return;
            }

            Poll(log);
        }
        catch (Exception exception)
        {
            log.Warning($"  hand assets: threw {exception.GetType().Name}: "
                + exception.Message);
            Finish(log, "threw");
        }
    }

    // DIE SCHLUESSEL, wie sie wirklich heissen. Jeder Schritt wird einzeln
    // gemeldet, damit ein Fehlschlag sagt, WO die Kette reisst, statt nur dass
    // sie reisst.
    private void Enumerate(MelonLogger.Instance log)
    {
        var impl = UnityEngine.AddressableAssets.Addressables.Instance;

        if (impl is null)
        {
            log.Warning("  hand assets: Addressables.Instance is null - "
                + "not initialised yet?");
            return;
        }

        var locators = impl.m_ResourceLocators;

        if (locators is null)
        {
            log.Warning("  hand assets: m_ResourceLocators is null");
            return;
        }

        log.Msg($"  hand assets: {locators.Count} locator(s)");

        var scanned = 0;

        for (var i = 0; i < locators.Count; i++)
        {
            var info = locators[i];
            var locator = info is null ? null : info.Locator;

            if (locator is null)
            {
                log.Msg($"    locator[{i}] has no Locator");
                continue;
            }

            // DER ECHTE TYP, ueber den Zeiger. Kein Member-Aufruf, also kann
            // diese Zeile nicht abstuerzen - und sie sagt endlich, WAS die acht
            // sind. In 1.26.0 stand hier ueberall derselbe Wrapper-Name, weil
            // ToString den Wrapper nennt und nicht die Laufzeitklasse.
            var type = NativeTypeOf(locator);

            scanned += ReadKeys(log, i, type, locator);
        }

        log.Msg($"  hand assets: {scanned} key(s) scanned, {keys.Count} hand key(s)");

        // ZWEI VERSCHIEDENE BEFUNDE, und sie duerfen nicht dieselbe Zeile
        // teilen. Wurden Schluessel gelesen und keiner passt, ist das eine
        // Aussage ueber die ASSETS. Wurde KEIN Schluessel gelesen, ist es eine
        // Aussage ueber DIESE PROBE - und genau das hat die Vorfassung
        // verwechselt: sie schrieb "not addressable", nachdem sie null
        // Schluessel gelesen hatte. Ein Zaehler, der nie gelaufen ist, ist kein
        // Befund.
        if (scanned == 0)
            log.Warning("  hand assets: NO key was read at all - this says nothing "
                + "about the assets, only that the walk failed. See the locator lines.");
        else if (keys.Count == 0)
            log.Warning($"  hand assets: {scanned} key(s) read and none matches "
                + "VRHand or XRHand - the assets ship but are not addressable under "
                + "such a name. The in-scene debug_hand is then the way.");
    }

    // NUR KONKRETE TYPEN. Jeder Zweig liest ein eigenes Feld der jeweiligen
    // Klasse - nie ein Interface-Member.
    private int ReadKeys(MelonLogger.Instance log, int i, string type,
        UnityEngine.AddressableAssets.ResourceLocators.IResourceLocator locator)
    {
        try
        {
            // Der Binaerkatalog-Locator: keyData ist ein konkretes
            // Dictionary<Object, uint> auf einer konkreten Klasse.
            var catalog = locator.TryCast<UnityEngine.AddressableAssets
                .ResourceLocators.ContentCatalogData.ResourceLocator>();

            if (catalog is not null)
            {
                var data = catalog.keyData;

                if (data is null)
                {
                    log.Msg($"    locator[{i}] {type} - ResourceLocator, keyData null");
                    return 0;
                }

                var walked = 0;

                foreach (var entry in data)
                {
                    walked++;

                    var text = entry.Key is null ? "" : entry.Key.ToString();

                    if (string.IsNullOrEmpty(text) || !Matches(text))
                        continue;

                    if (keys.Contains(text) || keys.Count >= 12)
                        continue;

                    keys.Add(text);
                    log.Msg($"      HAND KEY: \"{text}\"");
                }

                log.Msg($"    locator[{i}] {type} - {walked} key(s) via keyData");
                return walked;
            }

            // Der Sprite-Atlas-Locator fuehrt keine eigene Schluesselliste,
            // seine Keys sind abgeleitet. Nur melden, nichts anfassen.
            var dynamic = locator.TryCast<UnityEngine.AddressableAssets
                .DynamicResourceLocator>();

            if (dynamic is not null)
            {
                log.Msg($"    locator[{i}] {type} - dynamic, no key table");
                return 0;
            }

            log.Msg($"    locator[{i}] {type} - no concrete reader for this type");
            return 0;
        }
        catch (Exception exception)
        {
            log.Warning($"    locator[{i}] {type} threw "
                + $"{exception.GetType().Name}: {exception.Message}");
            return 0;
        }
    }

    // Das Muster aus WetReality.Discovery.TypeNameOf: nur der Zeiger, kein
    // Member - deshalb auch auf einem Interface-Wrapper sicher.
    private static string NativeTypeOf(Il2CppObjectBase instance)
    {
        try
        {
            var klass = IL2CPP.il2cpp_object_get_class(instance.Pointer);

            if (klass == IntPtr.Zero)
                return "<unknown>";

            var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass))
                ?? "<unnamed>";
            var space = Marshal.PtrToStringAnsi(
                IL2CPP.il2cpp_class_get_namespace(klass));

            return string.IsNullOrEmpty(space) ? name : space + "." + name;
        }
        catch
        {
            return "<threw>";
        }
    }

    private static bool Matches(string text)
    {
        for (var i = 0; i < Needles.Length; i++)
        {
            if (text.IndexOf(Needles[i], StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    private void Request(MelonLogger.Instance log)
    {
        if (index >= keys.Count)
        {
            Finish(log, keys.Count == 0 ? "no keys" : "done");
            return;
        }

        // DER HALTER IST VOR DEM INSTANZIEREN DEAKTIVIERT, und das ist der
        // ganze Grund, warum dieser Lauf nichts sichtbar macht. Ein aktiver
        // Halter wuerde die Hand fuer einen Frame ins Bild setzen.
        if (holder is null || holder == null)
        {
            holder = new GameObject("WetRealityHandProbe");
            holder.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(holder);
        }

        var key = keys[index];

        log.Msg($"  hand assets: requesting \"{key}\" ({index + 1} of {keys.Count})");

        Il2CppSystem.Object keyObject = (Il2CppSystem.String)key;

        handle = UnityEngine.AddressableAssets.Addressables.InstantiateAsync(
            keyObject, holder.transform, false, true);

        live = true;

        // Ein Handle, der nie fertig wird, darf nicht bis zum Sitzungsende
        // weitergepollt werden.
        deadline = Time.unscaledTime + 15f;
        Status = $"hands: loading {key}";
    }

    private void Poll(MelonLogger.Instance log)
    {
        var key = keys[index];
        var current = handle;

        if (current is null)
        {
            log.Warning($"  hand assets: \"{key}\" handle came back null");
            Advance();
            return;
        }

        if (!current.IsDone)
        {
            if (Time.unscaledTime < deadline)
                return;

            log.Warning($"  hand assets: \"{key}\" still not done after 15 s, "
                + $"status {current.Status}, {current.PercentComplete:0.##} complete");
            Advance();
            return;
        }

        var status = current.Status;

        if (status != UnityEngine.ResourceManagement.AsyncOperations
            .AsyncOperationStatus.Succeeded)
        {
            var reason = current.OperationException is null
                ? "no exception given"
                : current.OperationException.Message;

            log.Warning($"  hand assets: \"{key}\" FAILED, status {status} - {reason}");
            Advance();
            return;
        }

        var instance = current.Result;

        if (instance is null || instance == null)
        {
            log.Warning($"  hand assets: \"{key}\" succeeded but the result is null");
            Advance();
            return;
        }

        Describe(log, key, instance);
        Advance();
    }

    // WAS GENAU ANKOMMT. Die offenen Fragen stehen danach als Zahlen da:
    // Renderer und ihr Typ, die Materialplaetze SAMT Shadername - damit ist
    // "zeichnet nichts" belegt oder widerlegt -, Knochenzahl, rootBone und die
    // Knotenpfade, an denen der naechste Lauf einhaengt.
    private static void Describe(MelonLogger.Instance log, string key,
        GameObject instance)
    {
        log.Msg($"  hand assets: \"{key}\" loaded as \"{instance.name}\"");

        var renderers = instance.GetComponentsInChildren<Renderer>(true);
        var count = renderers is null ? 0 : renderers.Length;

        log.Msg($"    {count} renderer(s)");

        for (var i = 0; i < count && i < 8; i++)
        {
            var renderer = renderers![i];

            if (renderer is null || renderer == null)
                continue;

            var type = "?";

            try
            {
                type = renderer.GetIl2CppType()?.Name ?? "?";
            }
            catch
            {
                type = "threw";
            }

            var materials = renderer.sharedMaterials;
            var slots = materials is null ? 0 : materials.Length;
            var material = "none";

            if (materials is not null && slots > 0 && materials[0] is not null
                && materials[0] != null)
                material = $"{materials[0].name} / {materials[0].shader?.name ?? "null shader"}";

            log.Msg($"      [{i}] {renderer.name,-24} {type,-20}"
                + $" materials {slots}  \"{material}\"");

            var skinned = renderer.TryCast<SkinnedMeshRenderer>();

            if (skinned is null || skinned == null)
                continue;

            var bones = skinned.bones;
            var root = skinned.rootBone;

            log.Msg($"           bones {(bones is null ? 0 : bones.Length)}"
                + $"   rootBone {(root is null || root == null ? "NONE" : root.name)}");
        }

        var nodes = instance.GetComponentsInChildren<Transform>(true);

        if (nodes is null)
            return;

        log.Msg($"    {nodes.Length} node(s), the first ones by path:");

        for (var i = 0; i < nodes.Length && i < 12; i++)
        {
            var node = nodes[i];

            if (node is null || node == null)
                continue;

            log.Msg($"      {PathWithin(instance.transform, node)}");
        }
    }

    // Relativ zur Instanz: der Halter ist ein Hilfsknoten und wuerde jede Zeile
    // nur verlaengern.
    private static string PathWithin(Transform root, Transform node)
    {
        var path = node.name;
        var walk = node.parent;
        var guard = 0;

        while (walk is not null && walk != null && guard++ < 12)
        {
            if (walk.GetInstanceID() == root.GetInstanceID())
                break;

            path = walk.name + "/" + path;
            walk = walk.parent;
        }

        return path;
    }

    private void Advance()
    {
        live = false;
        index++;

        // Jede Instanz sofort wieder weg. Der Halter bleibt, bis alle
        // Schluessel durch sind.
        if (holder is null || holder == null)
            return;

        var children = holder.GetComponentsInChildren<Transform>(true);

        if (children is null)
            return;

        var holderId = holder.transform.GetInstanceID();

        for (var i = 0; i < children.Length; i++)
        {
            var child = children[i];

            if (child is null || child == null)
                continue;

            if (child.GetInstanceID() == holderId)
                continue;

            if (child.parent is not null && child.parent != null
                && child.parent.GetInstanceID() == holderId)
                UnityEngine.Object.Destroy(child.gameObject);
        }
    }

    private void Finish(MelonLogger.Instance log, string how)
    {
        Destroy();
        finished = true;
        Status = $"hands: probed ({how})";
        log.Msg($"  hand assets: holder destroyed, probe {how}");
    }

    private void Destroy()
    {
        if (holder is null || holder == null)
        {
            holder = null;
            return;
        }

        UnityEngine.Object.Destroy(holder);
        holder = null;
    }
}
