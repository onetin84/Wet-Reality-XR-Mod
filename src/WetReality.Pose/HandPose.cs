using MelonLoader;
using UnityEngine;

namespace WetReality;

// Welche Pose eine Hand zeigt. Zwei genuegen fuer alles, was gewuenscht war:
// die Pistolenhand greift dauerhaft, die freie Hand greift, wenn sie greift.
internal enum HandPoseKind
{
    Open,
    Grip,
}

// DIE HAENDE POSIEREN - MIT DEN POSEN, DIE DAS SPIEL MITBRINGT.
//
// Gewuenscht: die Pistolenhand umgreift den Griff, die freie Hand geht beim
// Greifen in eine Greifpose - beim Interaktions-Trigger, beim Schultergriff,
// beim Holstergriff und beim Griff an die Pistole fuer die Verlaengerung.
//
// DIE VORFRAGE WAR "STATISCH ODER RIG", UND SIE IST GEMESSEN. Log
// 26-9-19_2-20-18, Zeilen 609-613 und 627-631:
//
//     Rig_VRHand_L(Clone)   1 renderer   L_Hand   SkinnedMeshRenderer
//                           bones 23   rootBone L_Wrist   51 node(s)
//                           L_Wrist/L_Index1/L_Index2/L_Index3/L_IndexEnd
//                           dazu L_IndexTip_Marker, L_IndexPad_Marker,
//                           L_IndexKnuckle_Marker, L_IndexNail_Marker
//
// Voll artikulierte Rigs mit eigener Wurzel je Hand. Der Mod hat davon bisher
// NICHTS benutzt: VrHands.Place schreibt die Wurzel des Klons, kein Knochen
// wurde je angefasst.
//
// UND DIE POSEN LIEGEN SCHON IM SPIEL. Aus catalog.bin und
// DLC/HUT/catalog_HUT.bin:
//
//     Anim@L_Grip.fbx   Anim@R_Grip.fbx     Greifpose
//     Anim@L_Open.fbx   Anim@R_Open.fbx     offene Hand
//     Anim@MP_Grip.fbx  Anim@MP_Fist.fbx  Anim@MP_Point.fbx
//     Player_VRHand_L.controller / _R.controller
//     CHAR_MP_VR_Hands.mask, CHAR_MP_VR_L_Hand.mask, CHAR_MP_VR_R_Hand.mask
//
// Dass Controller und Rig zusammenpassen, ist NICHT geraten: die spieleigene
// debug_hand ist derselbe 23-Knochen-Bau und faehrt einen aktiven Animator mit
// Runtime-Controller Player_VRHand_L (dasselbe Log, Zeile 454-457). Sie
// zeichnet nur deshalb nichts, weil ihr Materialplatz leer ist.
//
// DER SCHLUESSEL IST GEMESSEN, NICHT GERATEN (Zeile 567-568):
//
//     Assets/PWS/Content/Core/Animation/Player/Controllers/Player_VRHand_L.controller
//     Assets/PWS/Content/Core/Animation/Player/Controllers/Player_VRHand_R.controller
//
// Der einzige Ladeversuch davor scheiterte NUR AM TYP, und die Ausnahme nennt
// den richtigen selbst: "Key exists as multiple Types=RuntimeAnimatorController,
// FuturLab.FuturStateMachineBehaviour, which is not assignable from the
// requested Type=UnityEngine.GameObject". Diesmal wird er typisiert geladen.
//
// ------------------------------------------------------------------------
// DREI WEGE, DER ERSTE DER TRAEGT - UND DAS LOG SAGT, WELCHER
//
//   CLIP     clip.SampleAnimation(hand, 0) setzt die Pose EINMAL und faellt
//            danach nicht mehr an. Fuer statische Posen ist das der sauberste
//            Weg: kein Zustandsautomat, dessen Uebergaenge niemand gemessen
//            hat, und kein Animator, der jeden Frame schreibt. Die Clips kommen
//            aus controller.animationClips - damit braucht es KEINEN Schluessel
//            je Clip, nur den des Controllers.
//
//            Grenze: humanoide Clips lassen sich nicht sampeln. clip.humanMotion
//            sagt es, und dann greift der naechste Weg.
//
//   ANIMATOR DER WEG, und zwar ueber PARAMETER. Gemessen in 1.44.0: der
//            FBX-Klon bringt seinen Animator samt Avatar mit
//            ("Rig_VRHand_RAvatar", isHuman, valid) und fuehrt sieben
//            Parameter auf sieben Layern:
//
//                Index, Middle, Ring, Pinky, Thumb   Float   0..1
//                Grip                                Bool
//                IsOffhand                           Bool
//
//            Das ist die fertige Schnittstelle dieses Assets. Zustandsnamen
//            sind der falsche Hebel - auf Layer 0 existiert nur "Open" -, und
//            die autorisierte Griffpose haengt an Grip.
//
//   BONES    Die Fingerketten von Hand beugen. Braucht kein Asset und ist die
//            Versicherung, falls ein Update die Clips wegnimmt. Die Namen sind
//            gemessen (L_Index1..3, L_Thumb0..), die Achse ist die uebliche
//            lokale X - und was angefasst wurde, steht im Log.
//
//            DIESER WEG MUSS DEN ANIMATOR ABSCHALTEN. In 1.44.0 hat er 15
//            Knochen geschrieben und nichts bewegt: der Animator ueberschreibt
//            sie in jedem Frame. Abschnitt 53 hat genau das fuer das Armrig
//            festgehalten; auf die eigene Hand uebertragen wurde es erst
//            hier.
//
// GEMESSEN IN 1.43.0, und es raeumt den ersten Weg ab: alle 26 Clips beider
// Controller lesen humanMotion TRUE (L_Open, L_Closed, L_Grip,
// L_LeftHandedGrip und ihre R-Gegenstuecke). Muskelraum-Clips lassen sich nicht
// sampeln, und sie brauchen am Animator einen AVATAR - unser Klon hatte keinen,
// also konnte auch ein gefundener Zustand nichts bewegen. Dazu: HasState liest
// fuer "R_Grip" false, die Zustandsnamen sind also NICHT die Clipnamen.
//
// Der Avatar kommt deshalb aus der funktionierenden Referenz im selben Prozess:
// die spieleigene debug_hand faehrt denselben Rig mit Player_VRHand_L. Ihr
// Animator wird ausgelesen und sein Avatar geborgt - dasselbe Objekt, kein
// Schluessel und kein Raten.
//
// Die Reihenfolge in "auto" ist damit CLIP, ANIMATOR, BONES. Der Plan hatte den
// Animator vorn; die Begruendung dreht sich am eigenen Argument: diese Posen
// sind STATISCH, und ein Zustandsautomat ist fuer einen festen Griff der
// teurere und der unbekanntere Weg. Erzwingen laesst sich jeder einzelne ueber
// HandPoseRoute.
//
// ------------------------------------------------------------------------
// ZWEI WAECHTER, BEIDE AUS TEUREN LEKTIONEN
//
//   DIE WURZEL GEHOERT DEM MOD. Ein Clip kann eine Wurzelkurve tragen, und die
//   Wurzel dieses Klons ist die 6DOF-Kette. Vor dem Sampeln wird die Weltpose
//   gemerkt und danach zurueckgeschrieben; der Animator bekommt zusaetzlich
//   applyRootMotion = false.
//
//   DER TREFFER-COLLIDER IST AUS DER POSE GEBACKEN. HandSpray backt die
//   aktuelle Handgeometrie in einen MeshCollider - aendert sich die Pose, ist
//   die alte Form im Collider stehen geblieben und der Wasserstrahl trifft
//   Luft. Jeder Posenwechsel meldet sich darum nach oben, und die Aufrufstelle
//   wirft den Collider weg.
internal sealed class HandPose
{
    private const string LeftKey =
        "Assets/PWS/Content/Core/Animation/Player/Controllers/Player_VRHand_L.controller";

    private const string RightKey =
        "Assets/PWS/Content/Core/Animation/Player/Controllers/Player_VRHand_R.controller";

    // Woran eine Pose in den Clipnamen zu erkennen ist. Gemessen sind L_Grip,
    // R_Grip, L_Open, R_Open, MP_Grip, MP_Fist, MP_Point - "Grip" und "Open"
    // reichen also, und "Fist" ist der Ersatz, falls ein Controller nur den
    // fuehrt.
    private static readonly string[] GripNeedles = { "Grip", "Fist" };
    private static readonly string[] OpenNeedles = { "Open", "Idle", "Default" };

    // DIE SCHNITTSTELLE DES ASSETS, gemessen in 1.44.0. Die fuenf Finger sind
    // Floats von 0 (offen) bis 1 (geschlossen), Grip schaltet die autorisierte
    // Griffpose, IsOffhand die Variante der freien Hand.
    private static readonly string[] FingerParameters =
        { "Index", "Middle", "Ring", "Pinky", "Thumb" };

    private const string GripParameter = "Grip";
    private const string OffhandParameter = "IsOffhand";

    private sealed class Side
    {
        internal bool Requested;
        internal bool Done;
        internal float Deadline;
        internal AnimationClip? Grip;
        internal AnimationClip? Open;
        internal RuntimeAnimatorController? Controller;
        internal string Route = "unresolved";
        internal HandPoseKind Applied = HandPoseKind.Open;
        internal bool Valid;
        internal int Instance;

        // DIE RUHEPOSE, je Instanz einmal gemerkt. Die Bindepose eines
        // Fingerglieds ist nicht die Identitaet - eine gesetzte Eulerdrehung
        // wuerde die Hand verbiegen statt beugen. Gebeugt wird darum RELATIV.
        internal readonly List<Transform> Bones = new();
        internal readonly List<Quaternion> Rest = new();
        internal int BoundInstance;
        internal bool AvatarBorrowed;
        internal bool Reported;

        // Welche Parameter dieser Animator wirklich fuehrt. Einmal gelesen,
        // damit ein Set auf einen fehlenden Namen nicht jeden Frame eine
        // Ausnahme kostet - Animator.SetBool auf einen unbekannten Parameter
        // ist in Unity eine Fehlermeldung, kein stiller No-Op.
        internal readonly List<string> Parameters = new();

        internal UnityEngine.ResourceManagement.AsyncOperations
            .AsyncOperationHandle<RuntimeAnimatorController>? Handle;
    }

    private readonly Side left = new();
    private readonly Side right = new();
    private bool loggedRoutes;

    internal string Status { get; private set; } = "poses: not loaded";

    internal void Reset()
    {
        Clear(left);
        Clear(right);
        loggedRoutes = false;
        Status = "poses: not loaded";
    }

    private static void Clear(Side side)
    {
        side.Requested = false;
        side.Done = false;
        side.Deadline = 0f;
        side.Grip = null;
        side.Open = null;
        side.Controller = null;
        side.Route = "unresolved";
        side.Applied = HandPoseKind.Open;
        side.Valid = false;
        side.Instance = 0;
        side.Handle = null;
        side.Bones.Clear();
        side.Rest.Clear();
        side.BoundInstance = 0;
        side.AvatarBorrowed = false;
        side.Reported = false;
        side.Parameters.Clear();
    }

    // Einmal pro Sitzung, ein Schritt pro Frame - dieselbe Form wie HandAssets,
    // damit kein Frame am Laden haengt. KEIN await: in einem MelonLoader-Mod
    // gibt es keinen Synchronisationskontext.
    internal void Probe(MelonLogger.Instance log)
    {
        Step(log, left, LeftKey, "L");
        Step(log, right, RightKey, "R");

        if (loggedRoutes || !left.Done || !right.Done)
            return;

        loggedRoutes = true;
        log.Msg($"  hand poses: left [{Describe(left)}]   right [{Describe(right)}]");
    }

    private static string Describe(Side side) =>
        $"grip {(side.Grip is null || side.Grip == null ? "-" : side.Grip.name)}"
        + $", open {(side.Open is null || side.Open == null ? "-" : side.Open.name)}"
        + $", controller {(side.Controller is null || side.Controller == null ? "-" : "yes")}";

    private void Step(MelonLogger.Instance log, Side side, string key, string label)
    {
        if (side.Done)
            return;

        try
        {
            if (!side.Requested)
            {
                side.Requested = true;
                side.Deadline = Time.unscaledTime + 15f;

                Il2CppSystem.Object keyObject = (Il2CppSystem.String)key;

                // TYPISIERT, und das ist der ganze Unterschied zum Fehlversuch
                // aus Abschnitt 117: dort lief derselbe Schluessel gegen
                // Type=GameObject und wurde folgerichtig abgewiesen.
                side.Handle = UnityEngine.AddressableAssets.Addressables
                    .LoadAssetAsync<RuntimeAnimatorController>(keyObject);

                log.Msg($"  hand poses: requesting the {label} controller");
                return;
            }

            var handle = side.Handle;

            if (handle is null)
            {
                log.Warning($"  hand poses: the {label} handle came back null");
                side.Done = true;
                return;
            }

            if (!handle.IsDone)
            {
                if (Time.unscaledTime < side.Deadline)
                    return;

                log.Warning($"  hand poses: the {label} controller is still not done "
                    + $"after 15 s, status {handle.Status}");
                side.Done = true;
                return;
            }

            side.Done = true;

            if (handle.Status != UnityEngine.ResourceManagement.AsyncOperations
                .AsyncOperationStatus.Succeeded)
            {
                var reason = handle.OperationException is null
                    ? "no exception given"
                    : handle.OperationException.Message;

                log.Warning($"  hand poses: the {label} controller FAILED, "
                    + $"status {handle.Status} - {reason}");
                return;
            }

            var controller = handle.Result;

            if (controller is null || controller == null)
            {
                log.Warning($"  hand poses: the {label} controller loaded as null");
                return;
            }

            side.Controller = controller;
            ReadClips(log, side, controller, label);
        }
        catch (Exception exception)
        {
            side.Done = true;
            log.Warning($"  hand poses: the {label} controller threw "
                + $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    // WAS DER CONTROLLER FUEHRT, als Liste statt als Vermutung. Die Clipnamen
    // entscheiden die Zuordnung, und humanMotion entscheidet den Weg.
    private static void ReadClips(MelonLogger.Instance log, Side side,
        RuntimeAnimatorController controller, string label)
    {
        var clips = controller.animationClips;
        var count = clips is null ? 0 : clips.Length;

        log.Msg($"  hand poses: the {label} controller carries {count} clip(s)");

        for (var index = 0; index < count; index++)
        {
            var clip = clips![index];

            if (clip is null || clip == null)
                continue;

            var human = false;

            try
            {
                human = clip.humanMotion;
            }
            catch
            {
                // Ein nicht lesbares Flag ist kein Grund, den Clip zu
                // verwerfen - es entscheidet nur, welcher Weg zuerst probiert
                // wird, und der Rueckfall steht daneben.
            }

            log.Msg($"    clip[{index}] {clip.name,-24} length {clip.length:0.##} s"
                + $"   humanMotion {human}   legacy {clip.legacy}");

            if (side.Grip is null && Matches(clip.name, GripNeedles))
                side.Grip = clip;
            else if (side.Open is null && Matches(clip.name, OpenNeedles))
                side.Open = clip;
        }
    }

    private static bool Matches(string name, string[] needles)
    {
        for (var index = 0; index < needles.Length; index++)
            if (name.IndexOf(needles[index], StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

        return false;
    }

    // Setzt die Pose, wenn sie sich geaendert hat, und meldet true GENAU DANN.
    // Die Aufrufstelle wirft darauf den Treffer-Collider weg - er ist aus der
    // alten Pose gebacken.
    internal bool Apply(MelonLogger.Instance log, Transform? hand, bool rightHand,
        HandPoseKind pose, string route, Transform? reference, float curl,
        string axis, bool offHand, float grabCurl)
    {
        if (hand is null || hand == null)
            return false;

        var side = rightHand ? right : left;
        var id = hand.GetInstanceID();

        // Ein neuer Klon ist eine neue Hand: die Pose muss erneut gesetzt
        // werden, auch wenn sie dieselbe heisst. Unity-null sieht das nicht,
        // die Kennung schon.
        if (side.Valid && side.Instance == id && side.Applied == pose)
            return false;

        try
        {
            var applied = Write(log, side, hand, pose, route, rightHand ? "R" : "L",
                reference, curl, axis, offHand, grabCurl);

            side.Instance = id;
            side.Applied = pose;
            side.Valid = applied;

            Status = $"poses: {(left.Valid || right.Valid ? side.Route : "none")}";
            return applied;
        }
        catch (Exception exception)
        {
            log.Warning($"  hand poses: writing {pose} threw "
                + $"{exception.GetType().Name}: {exception.Message}");
            Status = "poses: failed";
            return false;
        }
    }

    private bool Write(MelonLogger.Instance log, Side side, Transform hand,
        HandPoseKind pose, string route, string label, Transform? reference,
        float curl, string axis, bool offHand, float grabCurl)
    {
        var wantClip = route is "auto" or "clip";
        var wantAnimator = route is "auto" or "animator";

        // DER RUECKFALL DARF DIE ASSETS NICHT UEBERHOLEN, und das waere er in
        // den ersten Frames: die Controller laden asynchron, also sind Clip und
        // Controller anfangs null - und "auto" waere sofort bei den Knochen
        // gelandet und haette sich als erledigt gemerkt. In "auto" kommen die
        // Knochen darum erst, wenn der Ladeversuch dieser Seite ABGESCHLOSSEN
        // ist; erzwungen ("bones") laufen sie sofort.
        var wantBones = route is "bones" || (route is "auto" && side.Done);

        var clip = pose == HandPoseKind.Grip ? side.Grip : side.Open;

        // humanMotion ist hier keine Vorsicht mehr, sondern ein Messwert: alle
        // 26 Clips lesen true, also faellt dieser Weg im Spiel immer durch. Er
        // bleibt stehen, weil ein Update generische Clips nachliefern kann -
        // und weil er dann der billigste ist.
        if (wantClip && clip is not null && clip != null && !clip.humanMotion)
        {
            // DIE WURZEL VOR UND NACH DEM SAMPELN. Eine Wurzelkurve im Clip
            // wuerde die Hand vom Controller loesen, und das ist die eine
            // Bewegung, die dieser Mod nicht abgeben darf.
            var position = hand.position;
            var rotation = hand.rotation;

            clip.SampleAnimation(hand.gameObject, 0f);
            hand.SetPositionAndRotation(position, rotation);

            side.Route = "clip";
            log.Msg($"  hand poses: {label} set to {pose} by sampling "
                + $"\"{clip.name}\"");
            return true;
        }

        if (wantAnimator && side.Controller is not null && side.Controller != null)
        {
            var animator = hand.GetComponent<Animator>()
                ?? hand.gameObject.AddComponent<Animator>();

            if (animator is not null && animator != null)
            {
                // NUR WENN KEINER DA IST. Ein Controllerwechsel setzt den
                // Zustandsautomaten zurueck, und der Klon bringt seinen
                // eigenen mit - gemessen in 1.44.0.
                if (animator.runtimeAnimatorController is null
                    && side.Controller is not null && side.Controller != null)
                    animator.runtimeAnimatorController = side.Controller;

                // Die Wurzel bleibt unsere, auch wenn ein Zustand eine
                // Wurzelbewegung traegt.
                animator.applyRootMotion = false;

                // Der Knochen-Weg schaltet den Animator ab; ohne diese Zeile
                // bliebe er fuer den Rest der Sitzung aus und die bessere
                // Route waere blockiert.
                if (!animator.enabled)
                    animator.enabled = true;

                // Bleibt als Rueckfall: der Klon hat seinen Avatar zwar
                // mitgebracht, aber ein kuenftiges Asset muss das nicht.
                BorrowAvatar(log, side, animator, reference, label);
                ReportAnimator(log, side, animator, label);

                if (SetParameters(log, side, animator, pose, label, offHand, grabCurl))
                {
                    side.Route = "animator";
                    return true;
                }

                // KEIN ERFOLG OHNE WIRKUNG - die Korrektur aus 1.44.0 gilt
                // weiter: wurde kein Parameter gesetzt, faellt die Route durch.
                log.Msg($"  hand poses: {label} carries none of the pose parameters "
                    + "- falling through");
            }
        }

        if (wantBones)
        {
            // OHNE DIES BEWEGT SICH NICHTS, und das ist der Befund aus 1.44.0:
            // 15 geschriebene Knochen, kein Bild. Der Animator schreibt sie in
            // jedem Frame zurueck - dieselbe Mechanik, die Abschnitt 53 fuer
            // das Armrig festgehalten hat.
            var running = hand.GetComponent<Animator>();

            if (running is not null && running != null && running.enabled)
            {
                running.enabled = false;
                log.Msg($"  hand poses: {label} animator switched OFF - the bones "
                    + "route owns the fingers now");
            }

            var touched = Curl(side, hand, pose == HandPoseKind.Grip, curl, axis);

            side.Route = "bones";
            log.Msg($"  hand poses: {label} set to {pose} by bending "
                + $"{touched} finger bone(s)   curl {curl:0.#} deg around {axis}");
            return touched > 0;
        }

        side.Route = "none";
        return false;
    }

    // DER RUECKFALL OHNE ASSET. Gebeugt werden die Fingerglieder, erkannt an
    // ihrem Namen samt Gliednummer - die Kette ist gemessen (L_Index1, L_Index2,
    // L_Index3, dazu Thumb, Middle, Ring, Pinky/Little). Marker und Endknoten
    // bleiben aussen: sie tragen keine Geometrie und ihre Drehung waere nur
    // Rauschen.
    //
    // Die Achse ist die uebliche lokale X eines Fingerrigs. Ob sie fuer DIESES
    // Rig stimmt, sagt das Bild - deshalb ist dies der letzte Weg und nicht der
    // erste.
    private static readonly string[] Fingers =
        { "Thumb", "Index", "Middle", "Ring", "Pinky", "Little" };

    // RELATIV ZUR RUHEPOSE, und das ist die Korrektur eines Denkfehlers: die
    // Bindepose eines Fingerglieds ist nicht die Identitaet. Ein gesetztes
    // Euler(angle,0,0) haette die Finger verdreht, nicht gebeugt.
    //
    // Die Ruhepose wird je Handinstanz EINMAL gemerkt. Ein neuer Klon bringt
    // neue Transforms mit, also haengt der Bestand an der Kennung.
    private static int Curl(Side side, Transform hand, bool grip, float curl,
        string axis)
    {
        var id = hand.GetInstanceID();

        if (side.BoundInstance != id || side.Bones.Count == 0)
        {
            side.Bones.Clear();
            side.Rest.Clear();
            side.BoundInstance = id;

            var nodes = hand.GetComponentsInChildren<Transform>(true);

            if (nodes is null)
                return 0;

            for (var index = 0; index < nodes.Length; index++)
            {
                var node = nodes[index];

                if (node is null || node == null)
                    continue;

                var name = node.name;

                // Marker und Endknoten tragen keine Geometrie; ihre Drehung
                // waere nur Rauschen.
                if (name.IndexOf("Marker", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.EndsWith("End", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (SegmentOf(name) <= 0)
                    continue;

                side.Bones.Add(node);
                side.Rest.Add(node.localRotation);
            }
        }

        var touched = 0;

        for (var index = 0; index < side.Bones.Count; index++)
        {
            var node = side.Bones[index];

            if (node is null || node == null)
                continue;

            var thumb = node.name.IndexOf("Thumb",
                StringComparison.OrdinalIgnoreCase) >= 0;
            var angle = !grip ? 0f : thumb ? curl * 0.55f : curl;

            node.localRotation = side.Rest[index] * Bend(angle, axis);
            touched++;
        }

        return touched;
    }

    // DIE POSE ALS PARAMETERSATZ. Genau die Schnittstelle, die das Asset
    // mitbringt - und damit die autorisierten Posen statt selbst gedrehter
    // Finger.
    //
    //     Pistolenhand   Grip = true    IsOffhand = false   Finger 0
    //     freie Hand     Grip = false   IsOffhand = true    Finger 0 oder grabCurl
    //
    // Die Pistolenhand laesst die Finger auf 0: die Griffpose haengt am Bool
    // und soll nicht zusaetzlich zugeschnuert werden. Ist das im Bild zu offen,
    // ist HandPoseGrabCurl der erste Schalter und nicht ein neuer Build.
    private static bool SetParameters(MelonLogger.Instance log, Side side,
        Animator animator, HandPoseKind pose, string label, bool offHand,
        float grabCurl)
    {
        if (side.Parameters.Count == 0)
            return false;

        var grip = pose == HandPoseKind.Grip;
        var closed = offHand && grip ? Mathf.Clamp01(grabCurl) : 0f;
        var wrote = 0;

        // GRIP HEISST "DIESE HAND HAELT DIE PISTOLE", nicht "diese Hand
        // greift". Die erste Fassung hat es fuer die freie Hand beim Greifen
        // mitgesetzt - das waere die Waschgriff-Pose an einer Hand, die keine
        // Pistole haelt. Die freie Hand schliesst ueber die Finger, und das
        // ist auch die Bedeutung, die IsOffhand daneben nahelegt.
        if (Has(side, GripParameter))
        {
            animator.SetBool(GripParameter, !offHand);
            wrote++;
        }

        if (Has(side, OffhandParameter))
        {
            animator.SetBool(OffhandParameter, offHand);
            wrote++;
        }

        for (var index = 0; index < FingerParameters.Length; index++)
        {
            var name = FingerParameters[index];

            if (!Has(side, name))
                continue;

            animator.SetFloat(name, closed);
            wrote++;
        }

        if (wrote == 0)
            return false;

        log.Msg($"  hand poses: {label} set to {pose} by animator parameters"
            + $"   grip {!offHand}   offhand {offHand}"
            + $"   fingers {closed:0.##}   ({wrote} parameter(s) written)");

        return true;
    }

    private static bool Has(Side side, string name)
    {
        for (var index = 0; index < side.Parameters.Count; index++)
            if (string.Equals(side.Parameters[index], name, StringComparison.Ordinal))
                return true;

        return false;
    }

    // Um welche lokale Achse ein Fingerglied beugt, ist rigabhaengig und im
    // Bild in zehn Sekunden entschieden - deshalb ein Schalter und keine
    // Annahme.
    private static Quaternion Bend(float angle, string axis) =>
        axis switch
        {
            "y" or "Y" => Quaternion.Euler(0f, angle, 0f),
            "z" or "Z" => Quaternion.Euler(0f, 0f, angle),
            "-x" or "-X" => Quaternion.Euler(-angle, 0f, 0f),
            "-y" or "-Y" => Quaternion.Euler(0f, -angle, 0f),
            "-z" or "-Z" => Quaternion.Euler(0f, 0f, -angle),
            _ => Quaternion.Euler(angle, 0f, 0f),
        };

    // DER AVATAR AUS DER REFERENZ. debug_hand faehrt denselben Rig mit
    // Player_VRHand_L, und ihr Animator ist die einzige funktionierende
    // Konfiguration, die dieser Prozess sicher enthaelt. Geborgt wird das
    // OBJEKT, nicht ein Name.
    private static void BorrowAvatar(MelonLogger.Instance log, Side side,
        Animator animator, Transform? reference, string label)
    {
        if (side.AvatarBorrowed)
            return;

        side.AvatarBorrowed = true;

        try
        {
            var own = animator.avatar;

            if (own is not null && own != null)
            {
                log.Msg($"  hand poses: {label} animator already has avatar "
                    + $"\"{own.name}\"   isHuman {own.isHuman}   valid {own.isValid}");
                return;
            }

            if (reference is null || reference == null)
            {
                log.Warning($"  hand poses: {label} has no avatar and no reference "
                    + "hand was given - humanoid clips cannot play without one");
                return;
            }

            var source = reference.GetComponent<Animator>()
                ?? reference.GetComponentInParent<Animator>();

            if (source is null || source == null)
            {
                log.Warning($"  hand poses: {label} found no Animator on the "
                    + $"reference hand \"{reference.name}\"");
                return;
            }

            var avatar = source.avatar;
            var controller = source.runtimeAnimatorController;

            log.Msg($"  hand poses: reference animator on \"{reference.name}\""
                + $"   avatar {(avatar is null || avatar == null ? "NONE" : avatar.name)}"
                + $"   controller {(controller is null || controller == null ? "none" : controller.name)}"
                + $"   enabled {source.enabled}");

            if (avatar is null || avatar == null)
                return;

            animator.avatar = avatar;
            log.Msg($"  hand poses: {label} borrowed avatar \"{avatar.name}\""
                + $"   isHuman {avatar.isHuman}   valid {avatar.isValid}");
        }
        catch (Exception exception)
        {
            log.Warning($"  hand poses: borrowing the avatar threw "
                + $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    // WAS DER CONTROLLER WIRKLICH FUEHRT. Zustandsnamen sind zur Laufzeit nicht
    // aufzaehlbar, also wird eine Kandidatenliste GEFRAGT - HasState kostet
    // nichts und die Antwort ist eindeutig. Dazu die Parameter, denn ein
    // Blendbaum haengt an ihnen und nicht an einem Zustandsnamen.
    private static readonly string[] StateCandidates =
    {
        "Open", "Closed", "Grip", "LeftHandedGrip", "Idle", "Default",
        "L_Open", "L_Closed", "L_Grip", "R_Open", "R_Closed", "R_Grip",
        "MP_Grip", "MP_Fist", "MP_Point", "Blend", "New State", "Hand",
    };

    private static void ReportAnimator(MelonLogger.Instance log, Side side,
        Animator animator, string label)
    {
        if (side.Reported)
            return;

        side.Reported = true;

        try
        {
            var parameters = animator.parameters;
            var count = parameters is null ? 0 : parameters.Length;

            var controller = animator.runtimeAnimatorController;

            log.Msg($"  hand poses: {label} animator has {animator.layerCount} "
                + $"layer(s) and {count} parameter(s)"
                + $"   enabled {animator.enabled}"
                + $"   controller {(controller is null || controller == null ? "NONE" : controller.name)}");

            for (var index = 0; index < count; index++)
            {
                var parameter = parameters![index];

                if (parameter is null)
                    continue;

                // GEMERKT, nicht nur gemeldet: SetBool auf einen Parameter,
                // den der Controller nicht fuehrt, ist in Unity eine
                // Fehlermeldung je Frame.
                side.Parameters.Add(parameter.name);

                log.Msg($"    parameter[{index}] {parameter.name,-20} {parameter.type}"
                    + $"   defaults f {parameter.defaultFloat:0.##}"
                    + $" i {parameter.defaultInt} b {parameter.defaultBool}");
            }

            var found = "";

            for (var index = 0; index < StateCandidates.Length; index++)
            {
                var name = StateCandidates[index];

                if (!animator.HasState(0, Animator.StringToHash(name)))
                    continue;

                found += (found.Length == 0 ? "" : ", ") + name;
            }

            log.Msg($"  hand poses: {label} states that exist on layer 0: "
                + (found.Length == 0 ? "NONE of the candidates" : found));
        }
        catch (Exception exception)
        {
            log.Warning($"  hand poses: reading the {label} animator threw "
                + $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    // Die Gliednummer am Ende eines Fingerknochennamens, oder 0. Nur Knoten,
    // deren Name einen Finger UND eine Nummer traegt, sind Fingerglieder.
    private static int SegmentOf(string name)
    {
        var finger = false;

        for (var index = 0; index < Fingers.Length; index++)
        {
            if (name.IndexOf(Fingers[index], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                finger = true;
                break;
            }
        }

        if (!finger || name.Length == 0)
            return 0;

        var last = name[name.Length - 1];

        return last >= '1' && last <= '9' ? last - '0' : 0;
    }
}
