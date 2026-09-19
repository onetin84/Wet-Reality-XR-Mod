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

    internal void Reset()
    {
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
