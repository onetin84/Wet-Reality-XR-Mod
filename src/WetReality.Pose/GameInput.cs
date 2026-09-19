using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using UnityEngine;

namespace WetReality;

// Routes a controller button into the game's OWN input surface rather than
// registering an input system of its own, which is the project rule from
// CLAUDE.md: emulate what the game already reads.
//
// BaseInput is that surface, and Fire is what makes the washer spray.
// PwsPlayerInput - the class actually in play - does not override it, so the
// auto-property on BaseInput is the single source and one postfix covers every
// caller. The alternative subclasses in the same assembly are worth recording
// here because they change the long-term picture: XRPlayerInputHand and
// XRPlayerInputManager, both deriving from BaseInput, alongside
// FuturLab.XR.XRInputBridge and XRInputManager. This build carries the code of
// the game's own VR edition. Section 55.
//
// A getter postfix rather than a setter call, deliberately. Writing a value and
// hoping it survives has already cost a full run: the game rewrites
// EquipmentAnchor.localPosition after MelonLoader's OnLateUpdate, which is why
// 0.23.0 had to move the position write one transform deeper. A getter patch
// cannot lose that race.
//
// bool __result keeps this clear of the struct-return family that hard crashed
// the process twice - sections 45 and 46.
internal static class GameInput
{
    // Only override while the mod is actually driving, so releasing F2 hands the
    // game its own input back with no trace.
    internal static bool Active;
    internal static bool FireHeld;
    internal static bool Installed;

    // Proves the detour is live. If this stays at zero the patch never took, and
    // that is a different problem from the button not reading - the two failure
    // modes were indistinguishable in every earlier input attempt.
    internal static long FireReads;

    // The wash ray, published by Pose from the live nozzle transform.
    //
    // Why the patches exist at all: WashEquipment rebuilds its raycast commands
    // in PlayerCameraPreRender(), which the URP render callback fires AFTER
    // OnLateUpdate. A value written from LateUpdate is therefore always stale by
    // the time it is used, which is why F5 changed nothing in 0.27.0. These act
    // at the moment of use instead.
    internal static bool RayActive;
    internal static bool RayReady;
    internal static Vector3 RayOrigin;
    internal static Vector3 RayDirection;

    // Counters, because three failure modes have to stay distinguishable: the
    // override works; WashRay is read and written but is not what the local
    // raycast uses; or nothing touches it at all while washing.
    internal static long RayReads;
    internal static long RayWrites;
    internal static Vector3 LastGameDirection;

    // patchWashRay defaults to FALSE, and that is a deliberate retreat.
    //
    // Cleaning stopped working entirely the moment these two patches landed, and
    // the visible jet came apart from the effective one. A Ray crossing the
    // il2cpp detour boundary by value is the same shape that hard crashed this
    // process twice, and a mis-marshalled Ray looks exactly like two rays
    // disagreeing. Until that is measured on its own, they stay out of the way.
    //
    // The Fire patch is unconditional: a bool __result has been proven here.
    // THE DECOUPLING EXPERIMENT. Section 60.
    //
    // Measured, not assumed: over 168 samples with the pose driving live,
    // RaySpawnPoint.forward sits at a median 0.0 deg from the CAMERA while the
    // Nozzle Locators Root - which hangs off the transform this mod drives -
    // diverges from the camera by up to 131 deg. So the gun follows the hand
    // and the game then re-aims RaySpawnPoint at the gaze underneath it. The
    // visible jet hangs below RaySpawnPoint and WashingDirection IS its forward
    // axis, so jet, laser and effect zone all inherit the gaze. That is the
    // whole bug, and section 58 had it right before section 59 mis-corrected it.
    //
    // WashEquipment carries four void, parameterless methods that can plausibly
    // perform that re-aim - the safest patch shape available here, no struct
    // crosses the boundary in either direction:
    //
    //   1  SetWashDirection
    //   2  SetScreenSpaceWashDirection
    //   3  SetScreenSpaceSurfaceHeadDirection
    //   4  TrySetTurboNozzleWashDirection
    //
    // All four are patched ALWAYS, but with AimSkip at 0 the prefixes only
    // COUNT and let the original run - no behaviour change whatsoever. That
    // alone answers, in a single run and without risk, which of the four the
    // game actually calls. Setting AimSkip to N then suppresses exactly one of
    // them, so four experiments fit in one session with only ever one change
    // active. A prefix returning false is what skips the original.
    internal static int AimSkip;
    internal static readonly long[] AimCalls = new long[5];
    internal static readonly long[] AimSkipped = new long[5];

    internal static void Install(HarmonyLib.Harmony harmony, MelonLogger.Instance log,
        bool patchWashRay, bool carryProbe)
    {
        InstallCarryProbe(harmony, log, carryProbe);

        var fire = typeof(Il2CppFuturLab.PW2.BaseInput)
            .GetProperty("Fire", BindingFlags.Instance | BindingFlags.Public)
            ?.GetGetMethod();

        Installed = Patch(harmony, log, "BaseInput.get_Fire", fire, nameof(FirePostfix), false);

        // The candidate set after the first field measurement, which settled the
        // structure of the old one:
        //
        //   calls 1:12837 2:1487 3:0 4:1487   skipped 1:11350 2:0 3:0 4:0
        //
        // SetScreenSpaceWashDirection and TrySetTurboNozzleWashDirection froze
        // at exactly the count they had when the mod was switched on, so
        // SetWashDirection is their CALLER and bit 1 kills the whole subtree.
        // SetScreenSpaceSurfaceHeadDirection is never called at all. That is why
        // suppressing either 1 or 2 alone appeared to help: one cut the chain,
        // the other cut a branch of it.
        //
        // And the decisive part: the pitch-dependent offset SURVIVES with that
        // entire chain suppressed, so it does not originate there. Hence three
        // new candidates, all of which position or orient the nozzle anchor
        // itself rather than compute a direction.
        //
        // The parameter lists are why these were unreachable before -
        // SetNozzleAnchorPoint takes six arguments including two Vector3 and an
        // enum. A Harmony prefix that declares NO parameters can skip a method
        // of any signature, so nothing crosses the interop boundary at all.
        // That removes the struct-shape objection from sections 45 and 46
        // entirely.
        PatchAim(harmony, log, "SetWashDirection", nameof(SkipOne));
        PatchAim(harmony, log, "SetAnchorPoints", nameof(SkipTwo));
        PatchAim(harmony, log, "SetNozzleAnchorPoint", nameof(SkipThree));
        PatchAim(harmony, log, "RaycastUpdate", nameof(SkipFour));

        // The same method, patched a second time and as a POSTFIX. Prefix skips
        // it, postfix corrects what it produced - see RaycastUpdatePostfix.
        Patch(harmony, log, "WashEquipment.RaycastUpdate (postfix)",
            typeof(Il2CppFuturLab.PW2.WashEquipment)
                .GetMethod("RaycastUpdate", BindingFlags.Instance | BindingFlags.Public),
            nameof(RaycastUpdatePostfix), false);

        // The FOV reprojection, and the current prime suspect for the
        // pitch-dependent displacement of the effect zone.
        //
        // PlayerCameraController exposes FOVCorrectPosition(Vector3, bool) and
        // FOVCorrectDirection(Vector3) as PUBLIC while keeping the factor behind
        // a private getter - the shape of something other classes call. The
        // error of a screen-space remap is zero on the camera axis and grows
        // off it, which is precisely the reported symptom, and
        // m_reprojectionHitsBuffer says it raycasts from the camera for the
        // depth to reproject at, which supplies the pitch magnitude.
        //
        // Why the flat game needs it: the washer is drawn through a
        // vertex-shader FOV lock, so the gun the player SEES is not at its
        // geometric transform and the gameplay ray has to be corrected to agree
        // with the picture. In the headset the gun renders at its real
        // transform - beam, gun and laser all agree - but the correction is
        // still applied to the ray, so the effect zone is the only thing left
        // displaced.
        //
        // Two levers, because one is cheap and one is thorough:
        //
        //   bit 32  zero the factor. A float postfix on a private getter,
        //           the smallest possible intervention.
        //   bit 64  pass the corrections through unchanged. NOT a skip: these
        //           return Vector3, so a parameterless bool prefix would yield
        //           Vector3.zero and collapse the ray entirely. __0 and
        //           __result keep the struct parameters unnamed.
        var controller = typeof(Il2CppFuturLab.PW2.PlayerCameraController);

        Patch(harmony, log, "PlayerCameraController.get_FOVReprojectionFactor",
            controller.GetProperty("FOVReprojectionFactor",
                BindingFlags.Instance | BindingFlags.Public)?.GetGetMethod(),
            nameof(FovFactorPostfix), false);

        Patch(harmony, log, "PlayerCameraController.FOVCorrectDirection",
            controller.GetMethod("FOVCorrectDirection", BindingFlags.Instance | BindingFlags.Public),
            nameof(FovPassThrough), true);

        // SPLIT OUT of bit 64 onto its own bit 4096, and the split is the point.
        //
        // Bit 64 substitutes identity into BOTH corrections, and the direction
        // correction sits on the one thing in this project that measurably
        // works: errUp 0.1 deg at camPitch 16.6 deg in the 0.51.0 log. Testing a
        // positional hypothesis must not put that at risk.
        //
        // What is under test is purely positional. Measured from the same log,
        // the distance from the gun pivot to the ray spawn point runs 0.44 m
        // looking down and up to 0.90 m looking up - and both points hang in the
        // SAME rigid chain under PowerWasher_Assembly, where that distance can
        // only be a constant. Something stretches the chain per frame, the
        // reprojection factor reads -1.927, and FOVCorrectPosition is called
        // 22731 times in the session. Section 72 did test FOVCorrect* and filed
        // it as marginal - but it measured the DIRECTION, in degrees. The origin
        // was never measured against it.
        Patch(harmony, log, "PlayerCameraController.FOVCorrectPosition",
            controller.GetMethod("FOVCorrectPosition", BindingFlags.Instance | BindingFlags.Public),
            nameof(FovPositionPassThrough), true);

        if (!patchWashRay)
        {
            log.Msg("  patch  skipped   EquipmentManager.WashRay (PatchWashRay is off)");
            return;
        }

        var washRay = typeof(Il2CppFuturLab.PW2.EquipmentManager)
            .GetProperty("WashRay", BindingFlags.Instance | BindingFlags.Public);

        Patch(harmony, log, "EquipmentManager.get_WashRay",
            washRay?.GetGetMethod(), nameof(WashRayPostfix), false);

        Patch(harmony, log, "EquipmentManager.set_WashRay",
            washRay?.GetSetMethod(), nameof(WashRayPrefix), true);
    }

    // Resolved by name on WashEquipment. All four are natively private or
    // public but Il2CppInterop exposes them as public instance methods, so one
    // BindingFlags set covers them; a miss is logged rather than thrown, because
    // a renamed method must not stop the other three from installing.
    private static void PatchAim(HarmonyLib.Harmony harmony, MelonLogger.Instance log,
        string method, string handler)
    {
        // Resolved by NAME only, without a parameter-type filter, because the new
        // candidates take arguments - SetNozzleAnchorPoint has six. The handler
        // declares none of them, which is what makes skipping them safe.
        MethodInfo? target = null;

        try
        {
            target = typeof(Il2CppFuturLab.PW2.WashEquipment)
                .GetMethod(method, BindingFlags.Instance | BindingFlags.Public);
        }
        catch (AmbiguousMatchException)
        {
            log.Error($"  patch  AMBIGUOUS  WashEquipment.{method} - overloaded, needs a type filter");
            return;
        }

        Patch(harmony, log, $"WashEquipment.{method}", target, handler, true);
    }

    private static bool Patch(HarmonyLib.Harmony harmony, MelonLogger.Instance log,
        string label, MethodInfo? target, string handler, bool asPrefix)
    {
        if (target is null)
        {
            log.Error($"  patch  NOT FOUND  {label}");
            return false;
        }

        try
        {
            var method = new HarmonyMethod(typeof(GameInput).GetMethod(handler,
                BindingFlags.Static | BindingFlags.NonPublic));

            if (asPrefix)
                harmony.Patch(target, prefix: method);
            else
                harmony.Patch(target, postfix: method);

            log.Msg($"  patch  installed  {label}");
            return true;
        }
        catch (Exception exception)
        {
            log.Error($"  patch  FAILED  {label}: "
                + $"{exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    // Every read of the ray is answered with the nozzle's, so whoever consumes
    // it - local raycast or network sync - gets the hand's aim.
    private static void WashRayPostfix(ref UnityEngine.Ray __result)
    {
        RayReads++;

        if (!RayActive || !RayReady)
            return;

        __result.origin = RayOrigin;
        __result.direction = RayDirection;
    }

    // And every write is replaced. Recording the incoming direction first: that
    // is the game's own aim, and comparing it against the nozzle's says plainly
    // whether the gaze is what it was using.
    private static void WashRayPrefix(ref UnityEngine.Ray value)
    {
        RayWrites++;
        LastGameDirection = value.direction;

        if (!RayActive || !RayReady)
            return;

        value.origin = RayOrigin;
        value.direction = RayDirection;
    }

    // The continuous-spray latch, toggled by the right grip.
    //
    // The flat game has this as a separate action - Use Washer (Continuous) on
    // the right mouse button, beside Use Washer (Hold) on the left - and its
    // game-side routes run through StaticWashing, FireOverride, m_toggleFire and
    // the proximity state machine. None of that is needed: the mod already owns
    // this getter, so the latch is one bool and touches nothing.
    internal static bool FireLatched;

    private static void FirePostfix(ref bool __result)
    {
        FireReads++;

        if (Active && (FireHeld || FireLatched))
            __result = true;
    }

    // Returning false SKIPS the original. Returning true runs it unchanged, so
    // with AimSkip at 0 these are pure instrumentation: the game behaves exactly
    // as before and the counters still record which of the four it calls.
    //
    // Gated on Active as well, so releasing F2 restores the game's own aiming
    // even if a skip index is still selected - a bad experiment must never
    // outlive the mod being switched off.
    // AimSkip is a BITMASK, not a selector, and that change came straight out of
    // the first field result: suppressing SetWashDirection alone helped, and
    // suppressing SetScreenSpaceWashDirection alone helped, each leaving a
    // reduced but real gaze pull. Two partial fixes mean the remaining term sits
    // in the other method, so they have to be testable TOGETHER. A
    // one-at-a-time selector cannot express that and was the bottleneck.
    //
    //   bit 0 (1)  SetWashDirection
    //   bit 1 (2)  SetScreenSpaceWashDirection
    //   bit 2 (4)  SetScreenSpaceSurfaceHeadDirection
    //   bit 3 (8)  TrySetTurboNozzleWashDirection
    private static bool Allow(int index)
    {
        AimCalls[index]++;

        var bit = 1 << (index - 1);

        if (!Active || (AimSkip & bit) == 0)
            return true;

        AimSkipped[index]++;
        return false;
    }

    internal static long FovFactorReads;
    internal static long FovCorrectCalls;
    internal static float LastFovFactor;

    // Bit 32. Zeroing the factor is the cheapest possible test of the
    // reprojection hypothesis: if the factor multiplies the correction, one run
    // either removes the pitch offset or clears the hypothesis outright.
    private static void FovFactorPostfix(ref float __result)
    {
        FovFactorReads++;
        LastFovFactor = __result;

        if (Active && (AimSkip & 32) != 0)
            __result = 0f;
    }

    // Bit 64. An identity substitution, not a suppression - the return value is
    // the input. Returning false skips the original, so nothing is reprojected.
    //
    // Now DIRECTION ONLY. The position half moved to FovPositionPassThrough on
    // bit 4096 so the two can be tested apart.
    private static bool FovPassThrough(Vector3 __0, ref Vector3 __result)
    {
        FovCorrectCalls++;

        if (!Active || (AimSkip & 64) == 0)
            return true;

        __result = __0;
        return false;
    }

    internal static long FovPositionCalls;
    internal static long FovPositionSkipped;

    // Bit 4096, the positional half. Same identity shape as above: __0 and
    // __result keep the Vector3 parameters unnamed, and returning false skips
    // the original so nothing is reprojected. FOVCorrectPosition takes a second
    // bool parameter, which Harmony simply leaves unbound.
    //
    // The two counters are the whole diagnostic value. "calls rising, skipped
    // zero" means the bit is clear; "both rising" means the substitution is live.
    // Without that pair, a run that changed nothing would be indistinguishable
    // from a patch that never fired - and this project has already spent runs on
    // exactly that ambiguity.
    private static bool FovPositionPassThrough(Vector3 __0, ref Vector3 __result)
    {
        FovCorrectCalls++;
        FovPositionCalls++;

        if (!Active || (AimSkip & 4096) == 0)
            return true;

        __result = __0;
        FovPositionSkipped++;
        return false;
    }

    // THE DIRECTION ITSELF. Bit 256, and it is where the evidence finally points.
    //
    // Everything before this attacked a transform or a method that writes one,
    // and the measurement that settled it was RaySpawnPoint.localRotation forced
    // to identity AND VERIFIED at identity in the log - spawnLocal euler (-0,0,0)
    // - while the effective direction stayed wrong by the camera's pitch:
    //
    //     errRight  -0.6 deg   errUp  -5.5 deg   camPitch  -8.2 deg
    //     errRight  -5.8 deg   errUp -22.0 deg   camPitch -22.5 deg
    //
    // errUp tracks camPitch roughly one to one; errRight is near zero. So the
    // nozzle transform is clean and the direction is computed with a camera-pitch
    // term added somewhere no transform lever can reach.
    //
    // m_nozzleInstances is what becomes WashParams and then the raycast job,
    // field for field. Origin and Direction have public setters, and
    // Il2CppReferenceArray is value-type aware - it carries ourElementIsValueType
    // and GetElementPointer(i), so indexing addresses the element's own memory
    // rather than handing back a copy. The write therefore lands in the array.
    //
    // A POSTFIX on RaycastUpdate, so it runs after the game has filled the
    // instances. The open question is whether RaycastUpdate also schedules the
    // job from them, in which case this is one step too late and the counter
    // below will show writes with no change in behaviour. That is a readable
    // failure, which is the point.
    internal static Vector3 NozzleForward;
    internal static Vector3 NozzleOrigin;
    internal static bool NozzleReady;
    internal static long JobWrites;

    private static void RaycastUpdatePostfix(Il2CppFuturLab.PW2.WashEquipment __instance)
    {
        if (!Active || (AimSkip & 256) == 0 || !NozzleReady)
            return;

        try
        {
            var instances = __instance.m_nozzleInstances;

            if (instances is null)
                return;

            for (var index = 0; index < instances.Length; index++)
            {
                var instance = instances[index];

                if (instance is null)
                    continue;

                instance.Direction = NozzleForward;
                instance.Origin = NozzleOrigin;

                // WRITTEN BACK, and this line is the whole fix.
                //
                // NozzleInstance derives from Il2CppSystem.ValueType, so it is a
                // struct. The advice this was built on said Il2CppReferenceArray
                // is value-type aware and that indexing therefore addresses the
                // element's own memory, making a write-back unnecessary - and
                // flagged that as inference rather than knowledge.
                //
                // The measurement refuted it. jobWrites climbed to 24093 while
                // errUp stayed locked to camPitch one to one across 131 samples.
                // The counter proves the postfix ran; the unchanged error proves
                // the write went nowhere. A struct returned by an indexer is a
                // copy, the two property setters wrote into that copy, and it was
                // discarded at the end of the iteration.
                //
                // set_Item is what stores it back, over StoreValue.
                instances[index] = instance;
                JobWrites++;
            }
        }
        catch
        {
            // Swallowed deliberately: this runs inside the game's own call, and
            // a throw here would surface as the game misbehaving rather than as
            // the mod failing. The JobWrites counter says whether it ran.
        }
    }

    // ========================================================================
    // WER DEM TRAGE-HALTER DIE RICHTUNG GIBT - Abschnitt 102, Messung 1.
    //
    // Gemeldet: ein mit X aufgenommenes Objekt laesst sich nur mit dem KOPF an
    // seinen Platz schieben. Gewuenscht ist der Zeigestrahl der Pistolenhand,
    // mit der Bodenklemme, die das Spiel schon hat.
    //
    // DIE MECHANIK IST GEFUNDEN, DIE QUELLE NICHT. PlayerRaycastItemHolder
    // traegt Move() samt m_useHeightLimit, m_placementHeightLimiter,
    // m_idleObjectElevation und m_startingHeight - also genau das Tragen mit
    // Bodenklemme - und PlayerCameraController reicht TargetDirection,
    // TargetPosition und CameraTransform heraus. WELCHEN dieser drei Move()
    // liest, steht in keiner Signatur: IL2CPP hat die Methodenkoerper in
    // nativen Code uebersetzt, die Interop-Assembly nennt nur Namen.
    //
    // DESHALB EINE KLAMMER UND ZAEHLER, KEIN UMBAU. Prefix und Postfix auf
    // Move() setzen eine Tiefe, und die drei Getter-Postfixes zaehlen
    // getrennt, ob sie INNERHALB dieser Klammer gelesen wurden oder
    // ausserhalb. Was innen zaehlt, ist der Hebel fuer den naechsten Lauf.
    // Zaehlt nichts innen, liest Move() etwas anderes - und ein Umbau haette
    // geraten.
    //
    // NICHTS WIRD GESCHRIEBEN. Die Getter geben ihr __result unveraendert
    // zurueck; dieser Lauf aendert am Tragen kein Bit.
    //
    // BEIDE HALTER, weil unbekannt ist, welcher eine Leiter fuehrt:
    // PlayerRaycastItemHolder und PlayerPhysicalItemHolder ueberschreiben
    // Move() jeweils selbst, und der Zustandsautomat waehlt den Halter beim
    // Aufnehmen pro Objektart.
    //
    // DIE INSTALLATION HAENGT AN EINER PREFERENCE, nicht die Auswertung.
    // get_TargetDirection ist ein heisser Pfad - Abschnitt 87 fuehrt die
    // Kosten pro Frame als offenen Posten - und ein Lauf ohne Messung soll
    // keinen Zaehler bezahlen.
    internal static bool CarryProbeInstalled;
    internal static long HolderMoves;
    internal static long HolderMovesPhysical;
    internal static long HolderMovesBase;
    internal static string HolderKind = "none";
    internal static long TargetDirIn;
    internal static long TargetDirOut;
    internal static long TargetPosIn;
    internal static long TargetPosOut;
    internal static long CamTransformIn;
    internal static long CamTransformOut;
    internal static Vector3 LastTargetDirection;
    internal static Vector3 LastTargetPosition;
    internal static Vector3 ItemBeforeMove;
    internal static Vector3 ItemAfterMove;
    internal static bool ItemSeen;

    // ====================================================================
    // DIE PLATZIERUNG AN DER HAND - Abschnitt 102, Lauf 1.
    //
    // Gesetzt von Pose in DriveRay, an derselben Stelle, an der auch der
    // Wasch-Strahl veroeffentlicht wird: EINEN Frame alt, weil DriveRay in
    // OnLateUpdate laeuft und Move() im Update des Zustandsautomaten. Bei 90
    // Grad pro Sekunde ist das etwa ein Grad - dieselbe Toleranz, die die
    // Zielsuche seit Abschnitt 99 ausdruecklich akzeptiert.
    internal static bool CarryFollowsHand;
    internal static bool CarrySwapOrigin;
    internal static bool CarryAimReady;
    internal static Vector3 CarryAimForward;
    internal static Vector3 CarryAimOrigin;

    // Zwei Zaehler, weil "nichts hat sich geaendert" sonst zwei Ursachen
    // haette: der Tausch lief nicht, oder er lief und die Rotation ist nicht
    // die Quelle. Das ist genau die Zweideutigkeit, an der diese Sitzung
    // schon Laeufe verloren hat.
    internal static long CarrySwaps;
    internal static long CarrySwapSkips;

    // Die Kamera, wie sie OHNE Tausch stand - fuer den Bericht, damit der
    // Strahl des Spiels auch dann bestimmbar ist, wenn der Tausch nichts
    // bewirkt.
    internal static Vector3 CamPosAtMove;
    internal static Vector3 CamFwdAtMove;

    private static Transform? carrySwapTarget;
    private static Quaternion carrySavedRotation;
    private static Vector3 carrySavedPosition;

    // DER TAUSCH, und er ist bewusst eine ROTATION.
    //
    // Nur die Richtung, nicht der Ursprung: die Rotation ist, was das Objekt
    // STEUERT, und der Ursprung verschiebt es lediglich um den Abstand
    // Auge-zu-Muendung. Den Ursprung mitzutauschen aendert zusaetzlich, von wo
    // die Bodenpruefung und das Hoehenlimit rechnen, also steht er auf einem
    // eigenen Schalter und ist per cfg zuschaltbar statt per Build.
    //
    // LookRotation mit Vector3.up nimmt die Rolle der Hand heraus. Eine
    // gekippte Kamera waere fuer den Halter ein schiefer Horizont, und die
    // Rolle traegt fuer einen Strahl keine Information.
    private static void SwapCameraForCarry()
    {
        if (!Active || !CarryFollowsHand || !CarryAimReady)
        {
            CarrySwapSkips++;
            return;
        }

        try
        {
            var camera = Camera.main;

            if (camera is null || camera == null)
            {
                CarrySwapSkips++;
                return;
            }

            var target = camera.transform;

            carrySavedRotation = target.rotation;
            carrySavedPosition = target.position;

            CamPosAtMove = carrySavedPosition;
            CamFwdAtMove = carrySavedRotation * Vector3.forward;

            target.rotation = Quaternion.LookRotation(CarryAimForward, Vector3.up);

            if (CarrySwapOrigin)
                target.position = CarryAimOrigin;

            // ZULETZT gesetzt, damit ein Wurf oberhalb dieser Zeile keinen
            // Tausch behauptet, den es nicht gibt - die Wiederherstellung
            // wuerde sonst eine fremde Rotation schreiben.
            carrySwapTarget = target;
            CarrySwaps++;
        }
        catch
        {
            carrySwapTarget = null;
            CarrySwapSkips++;
        }
    }

    private static void RestoreCameraAfterCarry()
    {
        var target = carrySwapTarget;

        // BEIDE null-Formen, und beide Felder werden geraeumt: ein zerstoertes
        // Transform liest fuer "is null" nicht null, und ein Flag ohne sein
        // Objekt ist die Falle, die dieses Projekt schon kennt.
        if (target is null || target == null)
        {
            carrySwapTarget = null;
            return;
        }

        try
        {
            target.rotation = carrySavedRotation;

            if (CarrySwapOrigin)
                target.position = carrySavedPosition;
        }
        catch
        {
            // Verschluckt wie jeder Wurf in einem Detour. Ein nicht
            // zurueckgegebener Kopf waere sichtbar, also sagt es der
            // Zaehler-Vergleich im Bericht.
        }

        carrySwapTarget = null;
    }

    // Eine TIEFE statt eines bool: sollte ein Halter-Move je ein zweites
    // schachteln, wuerde ein bool beim inneren Postfix die Klammer des
    // aeusseren schliessen und alles Weitere als "ausserhalb" zaehlen - ein
    // stiller Messfehler genau der Art, die dieses Projekt schon vier
    // Diagnosen gekostet hat.
    private static int moveDepth;

    // GESETZT NUR AUF DER AEUSSERSTEN EBENE. Ruft eine Ableitung base.Move(),
    // feuern zwei Prefixes hintereinander, und ohne diese Bedingung wuerde der
    // Bericht "PlayerItemHolderBase" nennen, wo die Ableitung gearbeitet hat.
    private static void EnterMove(string kind)
    {
        moveDepth++;

        if (moveDepth == 1)
            HolderKind = kind;
    }

    internal static bool MoveActive => moveDepth > 0;

    private static void InstallCarryProbe(HarmonyLib.Harmony harmony,
        MelonLogger.Instance log, bool enabled)
    {
        var raycastHolder = typeof(Il2CppFuturLab.PW2.PlayerRaycastItemHolder);
        var physicalHolder = typeof(Il2CppFuturLab.PW2.PlayerPhysicalItemHolder);
        var controller = typeof(Il2CppFuturLab.PW2.PlayerCameraController);

        var move = BindingFlags.Instance | BindingFlags.Public;

        var ok = Patch(harmony, log, "PlayerRaycastItemHolder.Move (prefix)",
            raycastHolder.GetMethod("Move", move), nameof(RaycastMovePrefix), true);

        ok &= Patch(harmony, log, "PlayerRaycastItemHolder.Move (postfix)",
            raycastHolder.GetMethod("Move", move), nameof(RaycastMovePostfix), false);

        ok &= Patch(harmony, log, "PlayerPhysicalItemHolder.Move (prefix)",
            physicalHolder.GetMethod("Move", move), nameof(PhysicalMovePrefix), true);

        ok &= Patch(harmony, log, "PlayerPhysicalItemHolder.Move (postfix)",
            physicalHolder.GetMethod("Move", move), nameof(PhysicalMovePostfix), false);

        // DIE BASIS AUCH, und sie ist nicht die Vollstaendigkeitshalber-Zeile:
        // LadderItemHolder und AbseilingItemHolder ueberschreiben Move() nicht,
        // und der Zustandsautomat haelt seinen Halter als PlayerItemHolderBase.
        // Ist die Ableitung ein 'new' statt eines 'override', bedient die Basis
        // den Aufruf - dann ist DIESER Detour der einzige, der ihn sieht.
        var baseHolder = typeof(Il2CppFuturLab.PW2.PlayerItemHolderBase);

        ok &= Patch(harmony, log, "PlayerItemHolderBase.Move (prefix)",
            baseHolder.GetMethod("Move", move), nameof(BaseMovePrefix), true);

        ok &= Patch(harmony, log, "PlayerItemHolderBase.Move (postfix)",
            baseHolder.GetMethod("Move", move), nameof(BaseMovePostfix), false);

        // DIE DREI GETTER-ZAEHLER HABEN GEANTWORTET, und die Antwort war
        // null in beiden Spalten ueber 2759 Move()-Aufrufe. Sie bleiben als
        // Beleg stehen und werden nur noch auf Verlangen installiert - drei
        // Postfixes auf einem heissen Pfad zahlt niemand fuer eine Frage, die
        // beantwortet ist.
        if (enabled)
        {
            ok &= Patch(harmony, log, "PlayerCameraController.get_TargetDirection",
                controller.GetProperty("TargetDirection", move)?.GetGetMethod(),
                nameof(TargetDirectionPostfix), false);

            ok &= Patch(harmony, log, "PlayerCameraController.get_TargetPosition",
                controller.GetProperty("TargetPosition", move)?.GetGetMethod(),
                nameof(TargetPositionPostfix), false);

            ok &= Patch(harmony, log, "PlayerCameraController.get_CameraTransform",
                controller.GetProperty("CameraTransform", move)?.GetGetMethod(),
                nameof(CameraTransformPostfix), false);
        }

        CarryProbeInstalled = ok;

        log.Msg(ok
            ? $"  carry hooks armed: Move() is bracketed, camera getters "
                + $"{(enabled ? "counted" : "not counted (CarryProbe is off)")}."
            : "  carry hooks INCOMPLETE - a missing patch above takes the hand-driven "
                + "placement with it, so the head keeps steering.");
    }

    // __instance ist eine Klassenreferenz und Item.transform.position ein
    // Vector3-Getter darauf - die Form, die diese Mod ueberall benutzt. Kein
    // Bounds, kein Struct als Wert.
    private static void RaycastMovePrefix(
        Il2CppFuturLab.PW2.PlayerRaycastItemHolder __instance)
    {
        EnterMove("PlayerRaycastItemHolder");
        HolderMoves++;
        ItemBeforeMove = ReadItemPosition(__instance);

        // NUR HIER, nicht an den beiden anderen Haltern: gemessen sind 2759
        // Aufrufe an diesem und null an den anderen zwei. Ein Tausch an einem
        // Halter, der nicht arbeitet, waere ein Eingriff ohne Gegenleistung.
        SwapCameraForCarry();
    }

    // __instance ist hier der BASISTYP, und der Typname des tatsaechlichen
    // Objekts ist die interessante Spalte: er sagt, welcher Halter eine Leiter
    // fuehrt, ohne dass irgendwer es raten muss.
    private static void BaseMovePrefix(
        Il2CppFuturLab.PW2.PlayerItemHolderBase __instance)
    {
        var kind = "PlayerItemHolderBase";

        try
        {
            var name = __instance.GetIl2CppType()?.Name;

            if (!string.IsNullOrEmpty(name))
                kind = name!;
        }
        catch
        {
            // Der Name ist Beschriftung, nicht Messung. Ein Fehlschlag hier
            // darf die Klammer nicht kosten.
        }

        EnterMove(kind);
        HolderMovesBase++;
        ItemBeforeMove = ReadItemPosition(__instance);
    }

    private static void BaseMovePostfix(
        Il2CppFuturLab.PW2.PlayerItemHolderBase __instance)
    {
        ItemAfterMove = ReadItemPosition(__instance);

        if (moveDepth > 0)
            moveDepth--;
    }

    private static void RaycastMovePostfix(
        Il2CppFuturLab.PW2.PlayerRaycastItemHolder __instance)
    {
        // ZUERST zurueckgeben. Alles danach darf werfen, ohne dem Spieler
        // einen verdrehten Kopf zu hinterlassen.
        RestoreCameraAfterCarry();

        ItemAfterMove = ReadItemPosition(__instance);

        if (moveDepth > 0)
            moveDepth--;
    }

    private static void PhysicalMovePrefix(
        Il2CppFuturLab.PW2.PlayerPhysicalItemHolder __instance)
    {
        EnterMove("PlayerPhysicalItemHolder");
        HolderMovesPhysical++;
        ItemBeforeMove = ReadItemPosition(__instance);
    }

    private static void PhysicalMovePostfix(
        Il2CppFuturLab.PW2.PlayerPhysicalItemHolder __instance)
    {
        ItemAfterMove = ReadItemPosition(__instance);

        if (moveDepth > 0)
            moveDepth--;
    }

    private static Vector3 ReadItemPosition(Il2CppFuturLab.PW2.PlayerItemHolderBase holder)
    {
        try
        {
            var item = holder.Item;

            if (item is null || item == null)
            {
                ItemSeen = false;
                return Vector3.zero;
            }

            ItemSeen = true;
            return item.transform.position;
        }
        catch
        {
            // Verschluckt: das laeuft im Aufruf des Spiels, und ein Wurf hier
            // saehe wie ein Spielfehler aus statt wie ein Messfehler. Die
            // Zaehler sagen, ob es lief.
            ItemSeen = false;
            return Vector3.zero;
        }
    }

    private static void TargetDirectionPostfix(ref Vector3 __result)
    {
        if (moveDepth > 0)
            TargetDirIn++;
        else
            TargetDirOut++;

        LastTargetDirection = __result;
    }

    private static void TargetPositionPostfix(ref Vector3 __result)
    {
        if (moveDepth > 0)
            TargetPosIn++;
        else
            TargetPosOut++;

        LastTargetPosition = __result;
    }

    // Kein ref-Result und kein Zugriff auf den Transform: eine Position hier
    // abzugreifen waere eine Messung an einer anderen Stelle im Frame als die
    // beiden oben und damit nicht vergleichbar. Gezaehlt wird, ob gelesen
    // wurde - das ist die Frage.
    private static void CameraTransformPostfix()
    {
        if (moveDepth > 0)
            CamTransformIn++;
        else
            CamTransformOut++;
    }

    private static bool SkipOne() => Allow(1);

    private static bool SkipTwo() => Allow(2);

    private static bool SkipThree() => Allow(3);

    private static bool SkipFour() => Allow(4);

    // ====================================================================
    // DIE MENUEAUSWAHL FESTHALTEN - Abschnitt 125.
    //
    // Gemessen: die Auswahl wird 20 bis 30 mal pro Sekunde GELEERT, und die
    // Mod schreibt sie jedes Mal zurueck. Das Highlight haelt das aus, die
    // Vorschau nicht - sie laedt ihr Bild beim Auswaehlen und faengt bei
    // jedem Paar von vorn an.
    //
    // Also wird nicht schneller geschrieben, sondern das LEEREN abgewiesen.
    // Ein Prefix, der false zurueckgibt, ueberspringt das Original - dasselbe
    // Mittel wie bei SkipOne bis SkipFour, nur mit einer engen Bedingung.
    //
    // ABGEWIESEN WIRD NUR, WENN ALLE DREI ZUTREFFEN:
    //   1. das Ziel ist null (ein Setzen laeuft IMMER durch, sonst koennte
    //      keine Seite mehr ihre eigene Vorgabe waehlen),
    //   2. die Mod hat innerhalb des Fensters eine Auswahl gesetzt,
    //   3. das aktuell Ausgewaehlte IST unsere Kachel, am Zeiger verglichen.
    //
    // Damit ist der Eingriff ausserhalb des Menues wirkungslos, und jedes
    // Abwaehlen, das nicht unsere Kachel trifft, laeuft unberuehrt durch.
    internal static bool KeepMenuSelection;
    internal static bool SelectionGuardInstalled;

    // Das Fenster, von Pose bei jedem Setzen erneuert. 0,3 s ist die Form aus
    // zoneSuppressUntil: lang genug, um die Reaktion des Spiels auf unseren
    // Schreibvorgang zu ueberdecken, kurz genug, um nach dem Verlassen einer
    // Seite von selbst zu verfallen.
    internal const float KeepSelectionWindow = 0.3f;

    internal static float KeepSelectionUntil;
    internal static IntPtr KeepSelectionTarget;

    // Wahr, waehrend die Mod selbst schreibt. Es sagt NICHT "dieser Aufruf
    // kam von der Mod" - ein Handler des Spiels kann innerhalb unseres
    // Schreibvorgangs laufen. Es sagt "waehrend eines Mod-Schreibvorgangs",
    // und genau so steht es in der Logzeile.
    internal static bool ModWriting;

    // ZAEHLER VOR DEM HEBEL, und das ist hier keine Formsache: IL2CPP
    // schmilzt kleine Methoden ein, und die einstellige Ueberladung leitet
    // nur weiter. Bleibt SelectCallsTwo bei 0, hat der Detour nie gefeuert -
    // das ist dann der Befund und nicht ein Grund, weiterzuraten.
    internal static long SelectCallsOne;
    internal static long SelectCallsTwo;
    internal static long SelectBlocked;
    internal static long SelectPassed;
    internal static long SelectGuardThrew;

    private static MelonLogger.Instance? selectionLog;
    private static readonly List<string> SelectionWriters = new();

    internal static void InstallSelectionGuard(HarmonyLib.Harmony harmony,
        MelonLogger.Instance log, bool keep)
    {
        selectionLog = log;
        KeepMenuSelection = keep;

        // AUS HEISST NICHT INSTALLIERT - und die Vorfassung hat hier gelogen:
        // sie schrieb "patch skipped" und patchte danach trotzdem. Eine
        // Meldung, die das Gegenteil dessen behauptet, was geschieht, ist
        // schlimmer als keine; sie haette den naechsten Lauf in die falsche
        // Richtung geschickt.
        if (!keep)
        {
            log.Msg("  patch  not installed  EventSystem.SetSelectedGameObject "
                + "(MenuKeepSelection is off, the default). Measured in a clean "
                + "run: nothing clears the selection out from under the pointer, "
                + "selectBlocked was 0. This guard existed only to fight "
                + "UniverseLib's own prefix on the same method.");
            return;
        }

        // MIT TYPFILTER AUFGELOEST, weil es ZWEI Ueberladungen gibt und
        // GetMethod sonst AmbiguousMatchException wirft. Beide werden
        // gepatcht: die einstellige leitet nur weiter und kann eingeschmolzen
        // sein, die zweistellige ist die eigentliche.
        var system = typeof(UnityEngine.EventSystems.EventSystem);

        var one = system.GetMethod("SetSelectedGameObject",
            BindingFlags.Instance | BindingFlags.Public,
            null, new[] { typeof(GameObject) }, null);

        var two = system.GetMethod("SetSelectedGameObject",
            BindingFlags.Instance | BindingFlags.Public,
            null,
            new[]
            {
                typeof(GameObject),
                typeof(UnityEngine.EventSystems.BaseEventData),
            },
            null);

        var okOne = Patch(harmony, log, "EventSystem.SetSelectedGameObject(GameObject)",
            one, nameof(SelectOnePrefix), true);

        var okTwo = Patch(harmony, log,
            "EventSystem.SetSelectedGameObject(GameObject, BaseEventData)",
            two, nameof(SelectTwoPrefix), true);

        SelectionGuardInstalled = okOne || okTwo;
    }

    private static bool SelectOnePrefix(
        UnityEngine.EventSystems.EventSystem __instance, GameObject __0)
    {
        SelectCallsOne++;

        return Judge(__instance, __0);
    }

    private static bool SelectTwoPrefix(
        UnityEngine.EventSystems.EventSystem __instance, GameObject __0)
    {
        SelectCallsTwo++;

        return Judge(__instance, __0);
    }

    // Gibt true zurueck, wenn das Original laufen darf.
    private static bool Judge(UnityEngine.EventSystems.EventSystem system,
        GameObject? target)
    {
        try
        {
            Describe(system, target);

            // EIN SETZEN LAEUFT IMMER DURCH. Nur das Leeren ist der Gegner,
            // und eine Seite, die ihre eigene Vorgabe waehlt, muss das
            // koennen - sonst haengt das Leuchten nach einem Seitenwechsel.
            if (target is not null && target != null)
            {
                SelectPassed++;
                return true;
            }

            if (!KeepMenuSelection
                || KeepSelectionTarget == IntPtr.Zero
                || Time.unscaledTime > KeepSelectionUntil)
            {
                SelectPassed++;
                return true;
            }

            // AM ZEIGER VERGLICHEN, nicht am Namen: Il2CppInterop gibt bei
            // jedem Zugriff einen frischen Wrapper heraus - Abschnitt 92.
            var held = system is null || system == null
                ? null
                : system.currentSelectedGameObject;

            if (held is null || held == null || held.Pointer != KeepSelectionTarget)
            {
                SelectPassed++;
                return true;
            }

            SelectBlocked++;
            return false;
        }
        catch
        {
            // EIN WURF IM DETOUR DARF DAS MENUE NICHT ANHALTEN. Durchlassen
            // ist der sichere Ausgang, und der Zaehler sagt, dass es passiert
            // ist.
            SelectGuardThrew++;
            return true;
        }
    }

    // EINMAL JE KOMBINATION, gedeckelt auf acht. Wer die Auswahl leert, ist
    // die offene Frage; ein Name beantwortet sie, eine Zahl nicht - genau wie
    // "Button_Close" der Schluessel zur vorigen Diagnose war.
    private static void Describe(UnityEngine.EventSystems.EventSystem system,
        GameObject? target)
    {
        if (selectionLog is null || SelectionWriters.Count >= 8)
            return;

        // ZUGEWIESEN STATT BEDINGT GELESEN. Ein bool "alive" ueber den drei
        // Lesestellen sieht richtig aus, aber die Nullable-Analyse kann ihm
        // nicht folgen und meldet drei Warnungen - und dieses Projekt baut mit
        // null. Der Zweig traegt die Lesestellen, dann weiss der Compiler es
        // auch.
        var type = "-";
        var owner = "no system";
        var module = "none";

        if (system is not null && system != null)
        {
            type = NativeTypeOf(system);
            owner = system.gameObject.name;

            var input = system.currentInputModule;

            if (input is not null && input != null)
                module = input.name;
        }

        var set = target is not null && target != null;
        var key = $"{type}|{owner}|{module}|{(set ? "set" : "clear")}"
            + $"|{(ModWriting ? "inWrite" : "outside")}";

        if (SelectionWriters.Contains(key))
            return;

        SelectionWriters.Add(key);

        selectionLog.Msg($"  menu selection writer: {type}"
            + $"   object \"{owner}\""
            + $"   module {module}"
            + $"   target {(set ? $"\"{target!.name}\"" : "NULL (a clear)")}"
            + $"   {(ModWriting ? "during a mod write" : "outside any mod write")}");
    }

    // Das Muster aus WetReality.Discovery.TypeNameOf: nur der Zeiger, kein
    // Member - deshalb auch auf einem Interface-Wrapper sicher. Internal,
    // damit Pose denselben Namen im EventSystem-Zensus benutzt statt einer
    // dritten Kopie.
    internal static string NativeTypeOf(Il2CppObjectBase instance)
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
}
