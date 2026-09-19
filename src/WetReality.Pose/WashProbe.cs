using MelonLoader;
using UnityEngine;

namespace WetReality;

// Reads the game's aiming chain. Nothing is written here.
//
// The wash ray has now been attacked three times on inference and missed three
// times: written to EquipmentManager.WashRay from LateUpdate (too early -
// WashEquipment rebuilds in PlayerCameraPreRender, a render callback), then
// patched through ref Ray (cleaning stopped entirely, section 57). The probe
// cannot read method bodies, so the computation itself stays invisible.
//
// What it CAN do is read state, and every value the computation could plausibly
// use is exposed on WashEquipment as a property:
//
//   m_nozzleLocatorsRoot                   Transform  the VFX's parent
//   m_nozzleLocatorsRootRotationWorld      Vector3    cached, world
//   m_nozzleLocatorsRootRotationLocal      Vector3    cached, local
//   m_nozzleRaycasters[CENTER]             NozzleRaycaster
//       .RaySpawnPointRotation             Vector3    and it has a SETTER
//       .NozzleAnchorOffset                Vector3    likewise
//   m_lastCrosshairHitDistance             float      names the aiming model
//
// Put beside the nozzle's own forward and the camera's forward, those numbers
// say which transform the effective ray follows and by how much it diverges.
// That is what the next write has to be built on - measured, not assumed.
//
// Section 59 added the pair that matters most. WashingSource and
// WashingDirection are getters with NO backing field - no m_washingDirection
// exists on the type - so they are computed on every read, and whoever needs
// the value has to come through the getter. Reading them here settles in one
// run what three write attempts could not: whether the effective direction
// follows the gun or the gaze. The angle against each is logged directly, so
// the answer is a number in the log rather than a judgement from a video.
internal sealed class WashProbe
{
    private Il2CppFuturLab.PW2.WashEquipment? equipment;
    private float nextReport;
    private float nextSearch;

    // Read once per frame by the laser so it can point along the direction the
    // game actually washes in. Null until the first successful read, and reset
    // with the rest of the state, so a stale direction cannot outlive a job.
    internal Vector3? WashDirection { get; private set; }

    // ZWEI WERTE FUER DIE STRAHL-HAPTIK, und sie kommen absichtlich aus
    // DEMSELBEN Sample wie die Richtung: ein zweiter Abgriff an einer anderen
    // Stelle im Frame waere ein anderer Zwischenstand, und daran sind in
    // diesem Projekt schon Diagnosen gescheitert.
    //
    // IsWashing ist die Auskunft des SPIELS und nicht unsere Eingabe. Der
    // Unterschied ist nicht akademisch: der Strahl laeuft nicht, wenn der Tank
    // leer ist, wenn ein Menue offen ist oder wenn die Figur gerade klettert -
    // in allen drei Faellen wuerde GameInput.FireHeld weiter true melden und
    // der Controller grundlos brummen.
    internal bool IsWashing { get; private set; }

    // m_lastCrosshairHitDistance. Ob diese Zahl im Leerlauf einen
    // Kein-Treffer-Wert meldet oder den letzten Treffer festhaelt, ist NICHT
    // gemessen - deshalb entscheidet nicht diese Datei, was sie bedeutet: sie
    // gibt sie weiter, und die Schwellen sind Preferences.
    internal float CrosshairHitDistance { get; private set; }

    // DIE TURBO-DREHUNG, und sie ist der gemessene Turbo-Melder.
    //
    // NozzleInstance traegt TurboRotation. Dreht sich die Zahl, rotiert der
    // Strahl - das ist Turbo, unabhaengig davon, welcher Reiniger gerade
    // eingelegt ist. Die Alternative waere ein geratener Asset-Name gewesen:
    // beide gemessenen Reiniger heissen "...Light" und sind Klasse Light, aus
    // den Daten ist keiner als Turbo erkennbar.
    //
    // Das Element wird in eine lokale KOPIE gelesen. NozzleInstance ist ein
    // Struct, und ein Indexer gibt eine Kopie heraus - dieselbe Einsicht, die
    // GameInput.RaycastUpdatePostfix den Rueckschreib-Zwang gekostet hat. Hier
    // wird nur gelesen, also genuegt die Kopie.
    internal float TurboRotation { get; private set; }

    private Il2CppFuturLab.PW2.PositionToFOV? positionToFov;
    private float nextFovResolve;
    private bool loggedFovInventory;
    private bool fovWasDisabled;

    private static float ReadTurboRotation(Il2CppFuturLab.PW2.WashEquipment equipment)
    {
        try
        {
            var instances = equipment.m_nozzleInstances;

            if (instances is null || instances.Length == 0)
                return 0f;

            // Die ERSTE Duese genuegt: rotiert der Strahl, rotiert sie mit. Ein
            // Mittel ueber alle waere eine Zahl, die bei keiner stimmt.
            return instances[0].TurboRotation;
        }
        catch
        {
            return 0f;
        }
    }

    internal void Reset()
    {
        IsWashing = false;
        CrosshairHitDistance = 0f;
        TurboRotation = 0f;
        equipment = null;
        nextReport = 0f;
        nextSearch = 0f;
        WashDirection = null;
        positionToFov = null;
        nextFovResolve = 0f;
        loggedFovInventory = false;
        fovWasDisabled = false;
        assembler = null;
        nextAssemblerSearch = 0f;
    }

    // PositionToFOV holds the beam origin at a fixed SCREEN position as the
    // field of view changes, by rewriting NozzleAnchor.localPosition every
    // LateUpdate. The dumps prove it: the third-person anchor reads a clean
    // (0, 0, 0.04) in all five, while the first-person one reads
    // (0.057, -0.057, 0.041) at fov 70 and (0.084, -0.080, 0.039) at the 79.65
    // that XR raises it to. Off-axis that is 8.1 cm flat and 11.6 cm in VR.
    //
    // The growth is FOV-coupled but the formula is NOT understood: the measured
    // ratio is 0.116/0.081 = 1.43, while tan(39.82)/tan(35) is 1.19. An earlier
    // version of this comment claimed those matched. They do not, and the
    // correction in MuzzlePoint is built to strip whatever lateral value is
    // present each frame rather than to model it - which is why it does not
    // depend on the formula being known.
    //
    // This explains the beam visibly starting beside the muzzle. It does NOT
    // explain the pitch-dependent displacement of the effect zone: there is only
    // one PositionToFOV instance and everything under RaySpawnPoint moves with
    // it, so beam, gun and laser would stay in agreement - and they do. That
    // second defect is PlayerCameraController's FOV reprojection, measured
    // separately at 1.15 deg on the camera axis rising to 28.6 deg off it.
    //
    // Disabled through the component's own enabled flag: a bool on a class
    // reference, the safest shape there is. The inventory is logged first and
    // once, because m_targets is a List<Transform> that may hold more than the
    // nozzle anchor - the gun body or the arms would move too, and that has to
    // be readable in the log before the cause is believed.
    internal void ApplyPositionToFov(MelonLogger.Instance log, bool disable)
    {
        try
        {
            if (equipment is null || equipment == null)
                return;

            // RE-RESOLVED ON A TIMER, not cached once, and the difference is a
            // reported symptom.
            //
            // "??=" assigns only when the reference is PATTERN-null. A Unity
            // object that has been DESTROYED is not pattern-null, so after the
            // washer is re-assembled - which a nozzle or extension change does -
            // the dead component was kept forever. Reading .enabled on it then
            // yields false rather than throwing, "enabled == !disable" is
            // satisfied, and the method returns WITHOUT disabling the live
            // instance. That instance goes on rewriting NozzleAnchor.localPosition
            // every LateUpdate, which displaces the jet from the laser by the
            // 8 to 12 cm this component is known to write.
            //
            // It matches the report exactly: an offset that appears sporadically
            // and then "sorts itself out after a while or after some controller
            // command" - the next equipment change rebuilds everything and the
            // fresh resolve disables the new component again.
            //
            // The same is the Unity-null distinction WashLaser.Ensure already
            // makes correctly and this file did not.
            if (positionToFov is null || positionToFov == null
                || Time.unscaledTime >= nextFovResolve)
            {
                nextFovResolve = Time.unscaledTime + 0.5f;

                var live = equipment.m_positionToFOV;

                if (!ReferenceEquals(live, positionToFov))
                {
                    if (positionToFov is not null && live is not null)
                        log.Msg("  positionToFOV: component REPLACED - the washer was "
                            + "re-assembled. Re-disabling the new instance.");

                    positionToFov = live;
                }
            }

            if (positionToFov is null || positionToFov == null)
            {
                if (!loggedFovInventory)
                {
                    loggedFovInventory = true;
                    log.Msg("  positionToFOV: not present on WashEquipment");
                }

                return;
            }

            if (!loggedFovInventory)
            {
                loggedFovInventory = true;
                LogFovInventory(log, positionToFov);
            }

            if (positionToFov.enabled == !disable)
                return;

            positionToFov.enabled = !disable;
            fovWasDisabled = disable;

            // The game's own undo, called once on the way out. Merely disabling
            // the component stops it WRITING; it does not restore what it last
            // wrote, so NozzleAnchor.localPosition would freeze at whatever
            // displacement the last frame happened to have - and that
            // displacement was measured at up to 0.562 m. ResetTransform is
            // public, void and parameterless, and it is what puts the targets
            // back on their recorded parent and local offset.
            if (disable)
            {
                try
                {
                    positionToFov.ResetTransform();
                    log.Msg("  positionToFOV: DISABLED, ResetTransform called");
                }
                catch (Exception exception)
                {
                    log.Warning($"  positionToFOV.ResetTransform threw {exception.GetType().Name}; "
                        + "the anchor may be frozen at its last displaced position.");
                }
            }
            else
            {
                log.Msg("  positionToFOV: re-enabled");
            }
        }
        catch (Exception exception)
        {
            log.Warning($"  positionToFOV threw {exception.GetType().Name}: {exception.Message}");
            positionToFov = null;
        }
    }

    private static void LogFovInventory(MelonLogger.Instance log,
        Il2CppFuturLab.PW2.PositionToFOV fov)
    {
        try
        {
            var targets = fov.m_targets;
            log.Msg($"  positionToFOV inventory: enabled {fov.enabled}   "
                + $"screenPoint {V(fov.m_screenPoint)}   "
                + $"targets {(targets is null ? "null" : targets.Count.ToString())}");

            if (targets is null)
                return;

            for (var index = 0; index < targets.Count && index < 8; index++)
            {
                var target = targets[index];
                log.Msg($"    target[{index}] {(target is null ? "null" : target.name)}"
                    + (target is null ? "" : $"  local {V(target.localPosition)}"));
            }
        }
        catch (Exception exception)
        {
            log.Warning($"  positionToFOV inventory threw {exception.GetType().Name}");
        }
    }

    // Split from Report so the laser gets a fresh direction every frame while
    // the log keeps its one-second cadence. Both are reads, neither writes.
    //
    // On any throw the cached component is dropped rather than kept: the player
    // is rebuilt per job, and a stale WashEquipment would otherwise make the
    // laser freeze silently, which is exactly the kind of quiet failure that
    // costs a run.
    // Sample runs every frame, so the scene scan behind it is on a timer of its
    // own. FindObjectOfType walks the scene, and in a menu - where there is no
    // WashEquipment at all - an unthrottled search would do that every single
    // frame. Performance is a deferred topic in this project, which is a reason
    // not to add a new cost to it, not a licence to.
    internal void Sample()
    {
        try
        {
            // Unity-null as well as pattern-null: a destroyed WashEquipment is
            // not pattern-null, so the old test never re-resolved it and the
            // probe went quiet for the rest of the job.
            if (equipment is null || equipment == null)
            {
                if (Time.unscaledTime < nextSearch)
                {
                    WashDirection = null;
                    return;
                }

                nextSearch = Time.unscaledTime + 0.5f;
                equipment = UnityEngine.Object
                    .FindObjectOfType<Il2CppFuturLab.PW2.WashEquipment>();
            }

            if (equipment is null || equipment == null)
            {
                WashDirection = null;
                return;
            }

            var direction = equipment.WashingDirection;

            WashDirection = direction.magnitude < 0.001f ? null : direction.normalized;
            IsWashing = equipment.IsWashing;
            CrosshairHitDistance = equipment.m_lastCrosshairHitDistance;
            TurboRotation = ReadTurboRotation(equipment);
        }
        catch
        {
            equipment = null;
            WashDirection = null;
            IsWashing = false;
            CrosshairHitDistance = 0f;
            TurboRotation = 0f;
        }
    }

    // The laser state comes in as a parameter so it lands HERE rather than in the
    // per-second pose line: that line sits past the world-space branch's early
    // return and therefore never runs in the default configuration. An
    // on-screen-only status has already cost one run in this project.
    internal void Report(MelonLogger.Instance log, Transform? nozzle, Vector3 muzzle,
        Transform? gun, string laser)
    {
        if (Time.unscaledTime < nextReport)
            return;

        nextReport = Time.unscaledTime + 1f;

        try
        {
            if (equipment is null || equipment == null)
                equipment = UnityEngine.Object
                    .FindObjectOfType<Il2CppFuturLab.PW2.WashEquipment>();

            if (equipment is null || equipment == null)
            {
                log.Msg("  wash probe: no WashEquipment in the scene");
                return;
            }

            log.Msg($"  wash probe:   {laser}");

            // Which of the four aim methods the game actually calls, and how
            // often each was suppressed. This is the half of the experiment that
            // works even at skip 0, where nothing is changed: a candidate with
            // zero calls cannot be the one re-aiming RaySpawnPoint, so the four
            // narrow to however many are live in one risk-free run.
            // Candidate 1 is SetWashDirection, 2 SetAnchorPoints,
            // 3 SetNozzleAnchorPoint, 4 RaycastUpdate. A candidate whose call
            // count FREEZES when another is skipped is that other one's callee -
            // which is how SetScreenSpaceWashDirection was identified as a child
            // of SetWashDirection rather than an independent lever.
            log.Msg($"    aim skip {GameInput.AimSkip}   calls "
                + $"1:{GameInput.AimCalls[1]} 2:{GameInput.AimCalls[2]} "
                + $"3:{GameInput.AimCalls[3]} 4:{GameInput.AimCalls[4]}   skipped "
                + $"1:{GameInput.AimSkipped[1]} 2:{GameInput.AimSkipped[2]} "
                + $"3:{GameInput.AimSkipped[3]} 4:{GameInput.AimSkipped[4]}   jobWrites {GameInput.JobWrites}");

            // Whether the FOV reprojection is even on the path. Zero reads would
            // refute the whole hypothesis before any lever is pulled, and that
            // is worth knowing from the measuring run rather than from a run
            // spent wondering why a patch changed nothing.
            log.Msg($"    fov reads {GameInput.FovFactorReads}  "
                + $"factor {GameInput.LastFovFactor:0.###}  "
                + $"correct calls {GameInput.FovCorrectCalls}  "
                + $"posCalls {GameInput.FovPositionCalls}  "
                + $"posSkipped {GameInput.FovPositionSkipped}");

            // THE MEASUREMENT OF THIS VERSION, and it is logged as ONE NUMBER in
            // ONE block on purpose.
            //
            // gunToMuzzle is the distance from the transform this mod drives to
            // the point the ray and the laser start from. Both hang in the same
            // rigid chain under PowerWasher_Assembly, so this is a CONSTANT for a
            // given nozzle and extension - it can only change when the player
            // swaps one. Anything else moving it means the chain is being pulled
            // apart per frame, which is the visible offset between the muzzle and
            // the start of the jet.
            //
            // Derived from the 0.51.0 log by pairing this against camPitch across
            // two separate lines, which gave 0.44 m looking down against 0.90 m
            // looking up. That pairing spanned up to a second of gun motion, so
            // it is logged HERE, beside camPitch in the same block and from the
            // same transform snapshot, where the two cannot be a second apart.
            if (gun is not null && nozzle is not null)
                log.Msg($"    gunToMuzzle      {Vector3.Distance(gun.position, muzzle):0.###} m"
                    + $"   pivot {V(gun.position)}   muzzle {V(muzzle)}");

            ReportModelVsGeometry(log, gun, muzzle);
            log.Msg($"    cachedRotation   world {V(equipment.m_nozzleLocatorsRootRotationWorld)}  "
                + $"local {V(equipment.m_nozzleLocatorsRootRotationLocal)}");

            // The angle against the nozzle is logged rather than left to be
            // derived later from two vectors. Section 59 claimed these two were
            // "identical in every frame" by reading one frame where they were;
            // recomputed over all 86 samples of that run the median is 27.6 deg
            // and only 2 frames are within 2. A number in the log cannot be
            // eyeballed into agreement.
            var root = equipment.m_nozzleLocatorsRoot;
            if (root is not null)
                log.Msg($"    locatorsRoot     euler {V(root.rotation.eulerAngles)}  "
                    + $"forward {V(root.forward)}"
                    + (nozzle is null
                        ? ""
                        : $"  vs nozzle {Vector3.Angle(root.forward, nozzle.forward):0.#} deg"));

            var raycasters = equipment.m_nozzleRaycasters;
            if (raycasters is not null && raycasters.Length > 0)
            {
                // Reported per entry rather than only the centre one: left and
                // right exist for the trident nozzle, and if they carry different
                // rotations that alone shows how the game builds its spread.
                for (var i = 0; i < raycasters.Length && i < 3; i++)
                {
                    var caster = raycasters[i];
                    if (caster is null)
                        continue;

                    log.Msg($"    raycaster[{i}]     spawnRotation {V(caster.RaySpawnPointRotation)}  "
                        + $"anchorOffset {V(caster.NozzleAnchorOffset)}");
                }
            }
            else
            {
                log.Msg("    raycasters       none");
            }

            // The value the hierarchy dumps never showed, because the dumper
            // collapses single-child chains and RaySpawnPoint never appears as
            // its own entry. Every other node in the chain reads localEuler
            // (0,0,0), so this is where the gun-to-nozzle discrepancy lives. The
            // aim-test files recorded it swinging by up to 16.1 deg while
            // localPos stayed exactly (0,0,0).
            //
            // Read beside raycaster[0].RaySpawnPointRotation above: if the field
            // is (0,0,0) while this is not, the rotation is applied INSIDE
            // SetAnchorPoint and only clamping the transform can reach it.
            if (nozzle is not null)
                log.Msg($"    spawnLocal       euler {V(nozzle.localEulerAngles)}  "
                    + $"pos {V(nozzle.localPosition)}");

            log.Msg($"    crosshairDist    {equipment.m_lastCrosshairHitDistance:0.##}   "
                + $"washing {(equipment.IsWashing ? "YES" : "no")}   "
                + $"vanishingPoint {V(equipment.CrosshairVanishingPoint)}");

            // The two computed getters, and the whole point of this version.
            var source = equipment.WashingSource;
            var direction = equipment.WashingDirection;
            var length = direction.magnitude;

            // Distances against the MUZZLE, angles against the transform. The
            // muzzle arrives as a position rather than replacing the transform
            // because five columns here are built from nozzle.forward and must
            // stay transform-based, and there is no transform sitting at the
            // corrected place to hand in instead. fovFudge is the displacement
            // between the two - the one number that says out loud whether the
            // correction is live this frame, and it reads 0.000 exactly if the
            // parent chain ever falls back, which makes the guard audible.
            log.Msg($"    washSource       {V(source)}"
                + (nozzle is null
                    ? ""
                    : $"   muzzle {V(muzzle)}   "
                        + $"apart {Vector3.Distance(source, muzzle):0.###} m   "
                        + $"fovFudge {Vector3.Distance(nozzle.position, muzzle):0.###} m"));

            var camera = Camera.main;

            // Guarded rather than trusted: Vector3.Angle on a zero vector gives
            // a meaningless number, and a zero direction is itself the finding -
            // it would mean the getter only returns something while washing.
            if (length < 0.001f)
            {
                log.Msg($"    washDirection    {V(direction)}  len {length:0.###}  "
                    + "ZERO - no usable direction this frame");
            }
            else
            {
                var vsCamera = camera is null
                    ? -1f
                    : Vector3.Angle(direction, camera.transform.forward);
                var vsNozzle = nozzle is null
                    ? -1f
                    : Vector3.Angle(direction, nozzle.forward);

                log.Msg($"    washDirection    {V(direction)}  len {length:0.###}");
                log.Msg($"      vs camera      {vsCamera:0.#} deg");
                log.Msg($"      vs nozzle      {vsNozzle:0.#} deg");
            }

            if (camera is not null && nozzle is not null)
                log.Msg($"    nozzle forward   {V(nozzle.forward)}   "
                    + $"camera forward {V(camera.transform.forward)}   "
                    + $"apart {Vector3.Angle(nozzle.forward, camera.transform.forward):0.#} deg");

            // The column the 0.30.0 run did not have, and without which its
            // central question was undecidable. That log recorded only nozzle
            // versus camera, so "the nozzle follows the HAND" was never
            // measured - merely "it is not the gaze", which does not
            // distinguish the hand from anything else.
            //
            // PowerWasher_Assembly is the transform this mod drives from the
            // controller, so its forward IS the hand direction after offsets.
            // Small angle here means the ray origin travels with the gun we
            // drive; a large one means something re-aims RaySpawnPoint on its
            // own, which is what section 35 measured in the flat game as a
            // 9.68 deg change of its own local rotation.
            // THE DECISIVE MEASUREMENT for the pitch-dependent offset.
            //
            // m_nozzleInstances is what RaycastUpdate fills and what becomes
            // WashParams and then the raycast job - field for field identical to
            // WashEquipmentCreateRaycastJob. So Origin and Direction here ARE
            // the effect zone, one step before it is committed.
            //
            // The number that settles it is the angle between that Direction and
            // RaySpawnPoint.forward, logged beside the off-axis angle of the
            // nozzle against the camera. If the first grows with the second and
            // nulls when the aim is centred in view, the carrier is
            // PlayerCameraController's FOV reprojection - whose error is by
            // construction zero on the camera axis and grows off it. If it stays
            // flat, the correction happens further downstream and the next target
            // is PlayerCameraPreRender.
            //
            // Requires RaycastUpdate to RUN, so this measurement is worthless
            // with mask bit 8 set.
            try
            {
                var instances = equipment.m_nozzleInstances;

                if (instances is not null && instances.Length > 0 && instances[0] is not null)
                {
                    var instance = instances[0];
                    var jobDirection = instance.Direction;
                    var jobOrigin = instance.Origin;

                    var vsNozzle = nozzle is null
                        ? -1f
                        : Vector3.Angle(jobDirection, nozzle.forward);
                    var offAxis = camera is null || nozzle is null
                        ? -1f
                        : Vector3.Angle(nozzle.forward, camera.transform.forward);

                    log.Msg($"    job origin       {V(jobOrigin)}"
                        + (nozzle is null ? "" : $"   vs nozzlePos "
                            + $"{Vector3.Distance(jobOrigin, muzzle):0.###} m"));
                    log.Msg($"    job direction    {V(jobDirection)}   "
                        + $"vs nozzle {vsNozzle:0.#} deg   offAxis {offAxis:0.#} deg");

                    // DECOMPOSED, and the previous version's failure to do this
                    // is why two runs appeared to contradict each other.
                    //
                    // offAxis is the TOTAL angle between nozzle and camera, so it
                    // mixes yaw and pitch. The reported symptom is explicitly
                    // vertical - horizontal head movement leaves the effect zone
                    // in the beam, looking up or down displaces it - and a pure
                    // pitch term averages away inside a total-angle bin. One run
                    // then binned as 1.15 deg rising to 28.6, the next as a flat
                    // 10 deg with n=100 in the centre bin, from the same build.
                    // Neither reading was wrong; the regressor was.
                    //
                    // So: the error split into the camera's own vertical and
                    // horizontal components, beside the camera's pitch. If errUp
                    // tracks camPitch while errRight stays flat, the carrier is a
                    // pitch term and the sign says which way to invert it.
                    if (camera is not null && nozzle is not null)
                    {
                        var camT = camera.transform;
                        var localJob = camT.InverseTransformDirection(jobDirection);
                        var localNozzle = camT.InverseTransformDirection(nozzle.forward);

                        var errRight = Mathf.Atan2(localJob.x, localJob.z) * Mathf.Rad2Deg
                            - Mathf.Atan2(localNozzle.x, localNozzle.z) * Mathf.Rad2Deg;
                        var errUp = Mathf.Asin(Mathf.Clamp(localJob.y, -1f, 1f)) * Mathf.Rad2Deg
                            - Mathf.Asin(Mathf.Clamp(localNozzle.y, -1f, 1f)) * Mathf.Rad2Deg;

                        // Signed and wrapped to +-180, so looking down reads
                        // negative rather than 340.
                        var camPitch = camT.eulerAngles.x;
                        if (camPitch > 180f)
                            camPitch -= 360f;

                        log.Msg($"      errRight     {errRight:0.#} deg   "
                            + $"errUp {errUp:0.#} deg   camPitch {camPitch:0.#} deg");
                    }
                }
                else
                {
                    log.Msg("    job              no nozzle instances");
                }
            }
            catch (Exception exception)
            {
                log.Warning($"    job read threw {exception.GetType().Name}: {exception.Message}");
            }

            if (gun is not null && nozzle is not null)
                log.Msg($"    gun forward      {V(gun.forward)}   "
                    + $"vs nozzle {Vector3.Angle(gun.forward, nozzle.forward):0.#} deg"
                    + (camera is null
                        ? ""
                        : $"   vs camera {Vector3.Angle(gun.forward, camera.transform.forward):0.#} deg"));
        }
        catch (Exception exception)
        {
            log.Warning($"  wash probe threw {exception.GetType().Name}: {exception.Message}");
            nextReport = Time.unscaledTime + 10f;
        }
    }

    private Il2CppFuturLab.PW2.PowerWasherAssembler? assembler;
    private float nextAssemblerSearch;
    private bool loggedAssemblerInventory;

    // Proves the two-instance hypothesis instead of resting on it, and it is
    // worth one block of log because a WRONG assembler has been silently in use:
    // GunRender resolves the same way and has therefore been writing _ToggleFOV
    // to whichever washer FindObjectOfType happened to return. If that was the
    // third-person one, mask bit 2048 was a no-op and the shader FOV lock was
    // never tested at all - which would make section 72's "2961: shader lock
    // off" entry an experiment that never ran.
    //
    // Printed once. The path of each instance is what distinguishes the
    // first-person washer under PlayerCamera from the third-person one.
    private static void LogAssemblerInventory(MelonLogger.Instance log, Transform gun,
        Il2CppFuturLab.PW2.PowerWasherAssembler? picked)
    {
        try
        {
            var pickedPath = picked is null || picked == null
                ? "NONE"
                : PathOf(picked.transform);

            log.Msg($"  assembler: driven gun is {PathOf(gun)}");
            log.Msg($"    picked from subtree: {pickedPath}");

            var all = UnityEngine.Object
                .FindObjectsOfType<Il2CppFuturLab.PW2.PowerWasherAssembler>();

            log.Msg($"    instances in scene: {(all is null ? 0 : all.Length)}");

            if (all is null)
                return;

            for (var index = 0; index < all.Length && index < 6; index++)
            {
                var one = all[index];
                if (one is null || one == null)
                    continue;

                var underGun = one.transform == gun || one.transform.IsChildOf(gun);
                log.Msg($"      [{index}] {PathOf(one.transform)}"
                    + $"   underDrivenGun {underGun}");
            }
        }
        catch (Exception exception)
        {
            log.Warning($"  assembler inventory threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string PathOf(Transform node)
    {
        var path = node.name;

        for (var parent = node.parent; parent is not null && parent != null; parent = parent.parent)
            path = parent.name + "/" + path;

        return path;
    }

    // THE DISCRIMINATING MEASUREMENT, and it settles a question two hypotheses
    // died on today without ever being asked.
    //
    // What is established: the geometric chain is sound. gunToMuzzle reads 0.442 m
    // and stays there across the whole head-pitch range - 0.442 / 0.442 / 0.442 /
    // 0.441 - and substituting identity into FOVCorrectPosition moves it by one
    // millimetre. So nothing stretches the chain, and the effect zone is right.
    // What is left is that the gun the player SEES does not sit where the gun IS,
    // by an amount the user measured empirically at about 0.27 m when they
    // trimmed GripOffsetZ to +0.265 to make the two meet.
    //
    // The shader-lock explanation is ALSO now doubtful: _ToggleFOV already reads
    // 0 on all three washer materials before this mod writes anything, so mask
    // bit 2048 is a no-op and preset 2961 never tested what it claimed to.
    //
    // So this asks the one question nobody has asked: where are the washer's
    // RENDERER TRANSFORMS relative to the ray origin? A renderer's transform is
    // where the model is placed before any vertex-shader displacement. Three
    // outcomes, all decisive:
    //
    //   nozzle renderer sits AT the muzzle          -> the model is placed
    //       correctly and the displacement is purely in the vertex shader, so
    //       the lever is the shader globals (_FieldOfView, _LockFOV) and NOT
    //       _ToggleFOV, whose value is already what this mod wants.
    //   nozzle renderer sits ~0.27 m BEHIND it      -> the authored prefab puts
    //       RaySpawnPoint ahead of the model, because the flat game's shader
    //       pushes the model forward to meet it. Then the correct fix is to move
    //       the RAY ORIGIN back to the model, which is a mod-side computation
    //       and needs no shader work at all.
    //   the renderers are missing                   -> the assembler reference is
    //       the problem and nothing above applies.
    //
    // Deliberately NOT reading renderer.bounds. It returns a 24-byte struct by
    // value, the exact shape that hard-killed this process through
    // InputDevices.GetDeviceAtXRNode - no managed exception, the log simply
    // ended mid-line. Transform.position is a Vector3 getter on a class
    // reference, the shape this mod already relies on everywhere.
    private void ReportModelVsGeometry(MelonLogger.Instance log, Transform? gun, Vector3 muzzle)
    {
        try
        {
            if (gun is null || gun == null)
                return;

            if (assembler is null || assembler == null)
            {
                if (Time.unscaledTime < nextAssemblerSearch)
                    return;

                nextAssemblerSearch = Time.unscaledTime + 1f;

                // RESOLVED FROM THE DRIVEN TRANSFORM, never searched for.
                //
                // The first version of this used FindObjectOfType and the
                // measurement came back nonsense: nozzleModel[0] read 0.076 m
                // from the muzzle in one block and 1.001 m two blocks later,
                // which no rigid model can do. The cause is the same trap
                // Pose.Resolve already documents for EquipmentAnchor - there are
                // TWO, one under PlayerCamera and one under Third Person
                // Visuals - so there are two assemblers too, and
                // FindObjectOfType returns whichever it meets first. The
                // third-person washer follows the body animation independently
                // of the hand, which is exactly the scatter observed.
                //
                // gun IS Pose's `assembly`, i.e. PlayerCamera/EquipmentAnchor/
                // PowerWasher_Assembly. Taking the component from that subtree
                // is unambiguous by construction.
                assembler = gun.GetComponent<Il2CppFuturLab.PW2.PowerWasherAssembler>();

                if (assembler is null || assembler == null)
                    assembler = gun
                        .GetComponentInChildren<Il2CppFuturLab.PW2.PowerWasherAssembler>(true);

                if (!loggedAssemblerInventory)
                {
                    loggedAssemblerInventory = true;
                    LogAssemblerInventory(log, gun, assembler);
                }

                if (assembler is null || assembler == null)
                {
                    log.Msg("    model            no PowerWasherAssembler under the driven gun");
                    return;
                }
            }

            // In the gun's own frame, so the number is readable as "so far along
            // the barrel" rather than as a world triple that means nothing
            // without the gun's orientation.
            if (gun is not null)
                log.Msg($"    muzzleLocal      {V(gun.InverseTransformPoint(muzzle))}"
                    + "   (in the driven gun's frame, +z is along the barrel)");

            LogRenderer(log, "gunModel", assembler.m_pressureGunRenderer, muzzle, gun);
            LogRenderer(log, "extModel", assembler.m_extensionRenderer, muzzle, gun);

            var nozzles = assembler.m_nozzleRenderers;
            if (nozzles is null)
            {
                log.Msg("    nozzleModel      null list");
                return;
            }

            for (var index = 0; index < nozzles.Count && index < 4; index++)
                LogRenderer(log, $"nozzleModel[{index}]", nozzles[index], muzzle, gun);
        }
        catch (Exception exception)
        {
            log.Warning($"    model read threw {exception.GetType().Name}: {exception.Message}");
            assembler = null;
        }
    }

    private static void LogRenderer(MelonLogger.Instance log, string label,
        Renderer? renderer, Vector3 muzzle, Transform? gun)
    {
        if (renderer is null || renderer == null)
        {
            log.Msg($"    {label,-16} no renderer");
            return;
        }

        try
        {
            // Reported whether or not it is drawn: a disabled renderer that still
            // carries the right transform would otherwise look like a missing one.
            var t = renderer.transform;

            log.Msg($"    {label,-16} enabled {renderer.enabled}"
                + $"   toMuzzle {Vector3.Distance(t.position, muzzle):0.###} m"
                + (gun is null ? "" : $"   local {V(gun.InverseTransformPoint(t.position))}"));
        }
        catch (Exception exception)
        {
            log.Warning($"    {label,-16} read threw {exception.GetType().Name}");
        }
    }

    private static string V(Vector3 value) =>
        $"({value.x:0.##}, {value.y:0.##}, {value.z:0.##})";
}
