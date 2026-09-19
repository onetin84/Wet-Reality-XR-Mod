using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;

namespace WetReality;

// Which washer is DRAWN, and the shader FOV lock that was long assumed to be why
// the drawn one sits somewhere else.
//
// READ THIS FIRST, because the original premise of this file was wrong and the
// correction cost most of a session.
//
// FOUR HYPOTHESES DIED HERE. All four are recorded because each was believed,
// each was acted on, and each cost a run:
//
//   PositionToFOV disabled          bit 16, ResetTransform verified, no change
//   FOVCorrectPosition as identity  bit 4096, posSkipped rose 0 -> 3247, no change
//   _ToggleFOV written to 0         bit 2048, reached every material that has
//                                   the property, no change
//   the third-person washer is      ApplyVisualSwap fired - "3 driven SHOWN,
//   what is drawn                   3 third-person hidden" - and no change
//
// The last one deserves its own warning, because it was a MEASUREMENT ARTEFACT
// and the same artefact appeared twice in one session. The subtree inventory
// runs ONCE, at resolve time, about 76 ms after F2 - and at that instant the
// first-person washer meshes read enabled False while the third-person ones read
// True. They are enabled a moment later: once the swap was in place, gunModel
// read enabled True in every block for the rest of the session and nothing
// looked different. So the drawn washer was ALWAYS the driven washer. A one-shot
// probe taken during equipment assembly describes a transient, not a state.
// (The same trap produced renderer positions scattering by a metre earlier -
// there, FindObjectOfType had picked the third-person assembler.)
//
// So the visible muzzle offset and the gun swinging with the head remain
// UNEXPLAINED, and the transform chain has been cleared four times over:
// gunToMuzzle is a constant 0.44 m across the whole head-pitch range, and the
// nozzle model sits 4 cm from the ray origin.
//
// What has never been checked is the SPACE CONVERSION itself, in Pose's
// world-space write: toWorld = camT.rotation * inverse(hmdRotation). If the head
// driving is self-consistent that can only be a pure yaw equal to bodyYaw, hence
// constant while the stick is still. If it picks up a head-dependent pitch or
// roll term, the 0.45 m lever arm (hand - head) is rotated by a wandering
// rotation and the washer swings while the hand is still. errUp cannot see this:
// it compares the job direction with raySpawn.forward, both BELOW
// assembly.rotation, so a toWorld error rotates gun and laser together. That is
// the measurement now in the per-second pose line.
//
// ApplyVisualSwap is kept regardless: it is harmless, it makes the drawn washer
// provably the driven one instead of merely probably, and it carries the hand
// hiding. The lock machinery below is kept for its cheap reads only.
//
// The property names were recovered from global-metadata.dat and the shipped
// bundles rather than guessed:
//
//   m_fovPropID              _FieldOfView          global
//   m_fovLockPropID          _LockFOV              global
//   m_fovTogglePropID        _ToggleFOV            PER MATERIAL, 666 occurrences
//   m_washerDepthMinDistPropID / m_washerDepthScalePropID   global
//
// That per-material fact closes one route for good: a material-local value
// always beats Shader.SetGlobalFloat in Unity, so writing _ToggleFOV globally
// would be inert. It has to be written on the materials.
//
// Exactly ten first-person materials carry it - five on the washer, four on the
// arms and gloves, one on equipment. This writes ONLY the washer's, so the arms
// keep their correction. That matters: the arm rig root sits exactly at the
// camera and L_UpperArm is 0.34 m out against a 0.1 m near plane, so removing
// their correction would plausibly push them through it. HideArms stays the
// answer for the arms.
//
// READ BEFORE WRITE, and not as a formality. A 70 to 79.65 degree lock at the
// measured 0.6 m camera-to-nozzle distance predicts about 11 cm of displacement
// - which matches the PositionToFOV figure already measured, and NOT the half
// metre reported on screen. So either _LockFOV arrives degenerate under XR or a
// depth term contributes a second displacement. Three reads distinguish those
// for the price of one log line, and this project has paid for enough writes
// made before the reads.
internal sealed class GunRender
{
    private Il2CppFuturLab.PW2.CharacterVisualsFirstPerson? visuals;
    private Il2CppFuturLab.PW2.PowerWasherAssembler? assembler;
    private float nextSearch;
    private bool loggedProbe;
    private bool applied;

    internal string Status { get; private set; } = "gun render: untouched";

    internal void Reset()
    {
        visuals = null;
        assembler = null;
        drivenMeshes = null;
        thirdPersonMeshes = null;
        bodyMeshes = null;
        assemblerKnown = false;
        drivenFromAssembler = false;
        thirdPersonFromAssembler = false;
        loggedForAssembly = 0;
        loggedForGun = 0;
        nextMeshRefresh = 0f;
        loggedSwap = false;
        SwapStatus = "visual: untouched";
        nextSearch = 0f;
        loggedProbe = false;
        loggedGlobals = false;
        wroteLock = float.NaN;
        FovStatus = "fov: untouched";
        applied = false;
        Status = "gun render: untouched";
    }

    // THE ASSEMBLER IS TAKEN FROM THE DRIVEN TRANSFORM, not searched for, and
    // that correction is the point of this revision.
    //
    // FindObjectOfType was wrong here for the same reason Pose.Resolve documents
    // for EquipmentAnchor: there are TWO washers, one under PlayerCamera and one
    // under Third Person Visuals, and FindObjectOfType returns whichever it
    // meets first. The evidence that it was returning the wrong one is
    // measured - WashProbe's renderer positions, resolved the same way, scattered
    // by a full metre relative to a gun whose own muzzle offset was stable at
    // (0, 0.04, 0.45), and no rigid model can do that.
    //
    // The consequence for this class is worse than a bad diagnostic: it means
    // _ToggleFOV was very likely being written to the THIRD-PERSON materials,
    // which do not carry the first-person FOV lock. That is consistent with the
    // probe reading _ToggleFOV 0 before this mod wrote anything, and with mask
    // bit 2048 changing nothing. So the shader FOV lock is not refuted - it was
    // never tested. Section 72's "2961: shader lock off" is an experiment that
    // never ran.
    //
    // CharacterVisualsFirstPerson keeps its search: the type name itself says
    // which of the two it is, so there is no ambiguity to resolve.
    private bool Resolve(Transform? assemblyRoot)
    {
        if (visuals is not null && visuals != null && assembler is not null && assembler != null)
            return true;

        if (Time.unscaledTime < nextSearch)
            return false;

        nextSearch = Time.unscaledTime + 0.5f;

        visuals = UnityEngine.Object
            .FindObjectOfType<Il2CppFuturLab.PW2.CharacterVisualsFirstPerson>();

        if (assemblyRoot is not null && assemblyRoot != null)
        {
            assembler = assemblyRoot.GetComponent<Il2CppFuturLab.PW2.PowerWasherAssembler>();

            if (assembler is null || assembler == null)
                assembler = assemblyRoot
                    .GetComponentInChildren<Il2CppFuturLab.PW2.PowerWasherAssembler>(true);
        }

        return visuals is not null && assembler is not null;
    }

    internal void Apply(MelonLogger.Instance log, bool disableLock, Transform? assemblyRoot)
    {
        try
        {
            if (!Resolve(assemblyRoot))
            {
                Status = "gun render: no visuals/assembler";
                return;
            }

            var toggleId = visuals!.m_fovTogglePropID;

            // DIE INVENTARZEILE WIRD PRO WASCHER NEU SCHARFGESTELLT, und das ist
            // der Grund, warum der gemeldete DLC-Defekt nicht messbar war.
            //
            // loggedProbe lief einmal pro PROZESS und wurde nur von Reset()
            // zurueckgestellt - und Reset() laeuft beim Levelwechsel nicht. Im
            // Log der gemeldeten Sitzung steht daher genau EIN Inventar, vom
            // Basis-Washer aus dem Splash; ueber das DLC-Arsenal sagt es nichts.
            //
            // Zwei Kennungen: der Assembly-Knoten wechselt beim Levelwechsel,
            // der Pistolen-Renderer beim Neuzusammenbau des Waschers - genau
            // das, was das Log als "component REPLACED" meldet.
            var assemblyId = assemblyRoot is null || assemblyRoot == null
                ? 0
                : assemblyRoot.GetInstanceID();
            var gunId = 0;

            try
            {
                var pressureGun = assembler!.m_pressureGunRenderer;

                if (pressureGun is not null && pressureGun != null)
                    gunId = pressureGun.GetInstanceID();
            }
            catch
            {
                gunId = 0;
            }

            if (assemblyId != loggedForAssembly || gunId != loggedForGun)
            {
                loggedForAssembly = assemblyId;
                loggedForGun = gunId;
                loggedProbe = false;
                loggedSwap = false;
            }

            if (!loggedProbe)
            {
                loggedProbe = true;
                LogProbe(log, toggleId);
                LogSubtree(log, toggleId, assemblyRoot, assembler);
                LogEveryGunMesh(log, assemblyRoot);
                LogShaderProperties(log, assembler!.m_pressureGunRenderer);
            }

            // Re-asserted rather than done once. SetProperties(Renderer) plus the
            // WasherRendererModified event is an event-shaped pair, so the game
            // very likely rewrites this on a nozzle swap or a re-assembly. Whether
            // Update also does it per frame is unverified, so the cheap answer is
            // to keep writing while the bit is set.
            if (!disableLock)
            {
                if (applied)
                {
                    applied = false;
                    Status = "gun render: lock left alone";
                }

                return;
            }

            var written = 0;
            written += WriteRenderer(assembler!.m_pressureGunRenderer, toggleId);
            written += WriteRenderer(assembler.m_extensionRenderer, toggleId);

            var nozzles = assembler.m_nozzleRenderers;
            if (nozzles is not null)
            {
                for (var index = 0; index < nozzles.Count; index++)
                    written += WriteRenderer(nozzles[index], toggleId);
            }

            // THE WHOLE SUBTREE AS WELL, and there is a measured reason.
            //
            // All three renderers the assembler references report enabled False,
            // while the washer is plainly visible. Their transforms are right -
            // local (0,0,0), (0,0.04,0.12) and (0,0.04,0.40) against a muzzle at
            // (0,0.04,0.45) - so they are the correct nodes, but if the visible
            // mesh is drawn by some OTHER renderer under the same assembly then
            // writing only these three would change nothing visible, and the run
            // would read as "the lock is not the cause" when it was never
            // reached. One sweep of the subtree removes that ambiguity.
            //
            // Scoped to assemblyRoot, which is
            // PlayerCamera/EquipmentAnchor/PowerWasher_Assembly. The arms hang
            // off Rig_PlayerArmsPivot, a different subtree, so they keep their
            // own correction - which matters, because the arm rig root sits at
            // the camera and L_UpperArm is 0.34 m out against a 0.1 m near
            // plane. HideArms stays the answer for the arms.
            var extra = 0;
            var swept = 0;

            if (assemblyRoot is not null && assemblyRoot != null)
            {
                var all = assemblyRoot.GetComponentsInChildren<Renderer>(true);

                if (all is not null)
                {
                    swept = all.Length;

                    for (var index = 0; index < all.Length; index++)
                        extra += WriteRenderer(all[index], toggleId);
                }
            }

            if (!applied)
            {
                applied = true;

                // The counts are the diagnostic. "written 3, swept 5, extra 5"
                // says the subtree holds two renderers the assembler never named;
                // "extra 0" would say the sweep found nothing the three did not
                // already cover, and then the three ARE the drawn washer.
                log.Msg($"  gun render: _ToggleFOV 0 on {written} assembler material(s), "
                    + $"{extra} across {swept} renderer(s) in the driven subtree");
            }

            Status = $"gun lock off ({written}+{extra} mats)";
        }
        catch (Exception exception)
        {
            log.Warning($"  gun render threw {exception.GetType().Name}: {exception.Message}");
            Reset();
            Status = "gun render: failed";
        }
    }

    // WHAT ACTUALLY DRAWS THE WASHER, and the run that made this necessary is
    // worth stating: with the assembler finally resolved to the first-person
    // washer, _ToggleFOV read 1, the write reached it - "0 on 3 assembler
    // material(s), 3 across 14 renderer(s)" - and NOTHING changed on screen.
    //
    // Three renderers out of fourteen carry the property, and all three report
    // enabled False while the washer is plainly visible. So the most likely
    // explanation left is that the drawn mesh comes from renderers that do not
    // carry _ToggleFOV at all, which would mean the shader lock is not the
    // carrier of the displacement either.
    //
    // This settles it by listing every renderer in the subtree with the three
    // facts that distinguish them: whether it is enabled, what shader its
    // materials use, and whether _ToggleFOV is present. Printed ONCE. A shader
    // name is the strongest single clue available - the FOV lock lives in a
    // specific shader family, so an enabled renderer whose shader differs from
    // the three disabled ones is the answer.
    //
    // Reading material.shader.name is a string across interop and could throw,
    // so every read is individually guarded rather than the loop as a whole: one
    // bad entry must skip, not abandon the inventory.
    private static void LogSubtree(MelonLogger.Instance log, int toggleId,
        Transform? assemblyRoot, Il2CppFuturLab.PW2.PowerWasherAssembler? assembler)
    {
        if (assemblyRoot is null || assemblyRoot == null)
        {
            log.Msg("  subtree: no assembly root");
            return;
        }

        try
        {
            var all = assemblyRoot.GetComponentsInChildren<Renderer>(true);

            if (all is null)
            {
                log.Msg("  subtree: GetComponentsInChildren returned null");
                return;
            }

            // Die Positivliste EINMAL, nicht pro Renderer.
            var gun = assembler is null || assembler == null
                ? new List<int>()
                : GunRendererIds(assembler);

            log.Msg($"  subtree: {all.Length} renderer(s) under {assemblyRoot.name}"
                + $"   assembler names {gun.Count}"
                + "   (enabled / type / verdict / shader)");

            for (var index = 0; index < all.Length && index < 20; index++)
            {
                var renderer = all[index];

                if (renderer is null || renderer == null)
                    continue;

                var shader = "?";
                var toggle = "ABSENT";

                try
                {
                    var materials = renderer.sharedMaterials;

                    if (materials is not null && materials.Length > 0 && materials[0] is not null)
                    {
                        shader = materials[0].shader?.name ?? "null";

                        if (materials[0].HasProperty(toggleId))
                            toggle = materials[0].GetFloat(toggleId).ToString("0.###");
                    }
                }
                catch (Exception exception)
                {
                    shader = $"threw {exception.GetType().Name}";
                }

                // DIE EINORDNUNG, DIE DIE REGEL GETROFFEN HAT, pro Renderer
                // und nachvollziehbar. Ein DLC-Pistolenteil, das der Assembler
                // nicht auffuehrt, stuende hier als "BODY" - und genau das ist
                // das benannte Restrisiko dieser Fassung.
                var type = "?";

                try
                {
                    type = renderer.GetIl2CppType()?.Name ?? "?";
                }
                catch
                {
                    type = "threw";
                }

                var isGun = gun.Contains(renderer.GetInstanceID());
                var verdict = isGun
                    ? "gun"
                    : !PlainGeometry(renderer)
                        ? "effect/type"
                        : UnderNozzleAnchor(renderer.transform, assemblyRoot)
                            ? "effect/nozzle"
                            : "BODY -> hidden";

                log.Msg($"    [{index,2}] {renderer.name,-28} enabled {renderer.enabled,-5}"
                    + $" {type,-20} {verdict,-14} toggle {toggle,-8} \"{shader}\"");

                // Der Pfad nur bei den ausgeblendeten. Der Name allein sagt beim
                // naechsten DLC nicht, WO die Geometrie haengt.
                if (!isGun && verdict.StartsWith("BODY", StringComparison.Ordinal))
                    log.Msg($"         {PathOf(renderer.transform)}");
            }
        }
        catch (Exception exception)
        {
            log.Warning($"  subtree inventory threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    private Renderer[]? drivenMeshes;
    private Renderer[]? thirdPersonMeshes;
    private Renderer[]? bodyMeshes;
    private bool assemblerKnown;
    private bool drivenFromAssembler;
    private bool thirdPersonFromAssembler;

    // Womit die Inventarzeile zuletzt scharfgestellt wurde. Zwei billige
    // Feldlesevorgaenge, kein GetComponentsInChildren pro Frame.
    private int loggedForAssembly;
    private int loggedForGun;
    private float nextMeshRefresh;
    private bool loggedSwap;

    internal string SwapStatus { get; private set; } = "visual: untouched";

    // DER KNOTEN, UNTER DEM DIE KOERPERGEOMETRIE DER PISTOLE HAENGT.
    //
    // Im Hauptspiel und im DLC ist das debug_hand, und sein Transform legt eine
    // Hand schon korrekt an den Griff - genau der Anker, den die VR-Hand
    // braucht.
    //
    // Gefunden wird er NAMENSFREI: er ist der Elternknoten des ersten
    // Renderers, den BodyMeshes als Koerpergeometrie eingeordnet hat. Damit
    // haengt er an derselben gemessenen Regel wie das Ausblenden selbst und
    // nicht an einem Namen, der im naechsten DLC anders lautet.
    internal Transform? BodyAnchor
    {
        get
        {
            if (bodyMeshes is null)
                return null;

            for (var index = 0; index < bodyMeshes.Length; index++)
            {
                var renderer = bodyMeshes[index];

                if (renderer is null || renderer == null)
                    continue;

                var parent = renderer.transform.parent;

                if (parent is not null && parent != null)
                    return parent;
            }

            return null;
        }
    }

    // THE FIX, and it is a mapping rather than an offset.
    //
    // Measured, not inferred. Every washer mesh in the player hierarchy was
    // listed with its path and its enabled flag, and the answer was unambiguous:
    //
    //   hidden   .../HeadTurn/PlayerCamera/EquipmentAnchor/PowerWasher_Assembly/...
    //   DRAWN    .../Third Person Visuals/EquipmentAnchor/PowerWasher_Assembly/...
    //
    // The player sees the THIRD-PERSON washer. The first-person one - the one
    // this mod drives in world space, whose ray origin provably stands still in
    // the room while the head turns - is not rendered at all. The two sat 0.33 m
    // apart vertically, which is exactly the reported "too close to the body".
    //
    // That one fact explains the entire session: the constant muzzle offset, the
    // gun wobbling with the head while laser and jet hold still, and three
    // levers - PositionToFOV, FOVCorrectPosition, _ToggleFOV - that each
    // provably fired and changed nothing, because all three act on geometry
    // nobody can see.
    //
    // So the washer meshes under the driven assembly are switched ON and the
    // third-person ones OFF. No calibration is involved and none should be: once
    // the drawn washer IS the driven washer, the muzzle and the ray origin are
    // the same 4 cm apart the geometry always said they were.
    //
    // Re-asserted per frame from cached arrays, refreshed on a timer. Switching a
    // nozzle destroys and rebuilds these clones, and the game may re-enable its
    // own visuals on an equipment change, so a one-shot write would survive only
    // until the first swap.
    //
    // Only renderers whose name carries the washer prefix are touched. L_Hand,
    // WashJet and the water and mist effects share the same subtree and must
    // keep their own visibility - the jet in particular is the thing being
    // aligned to.
    internal void ApplyVisualSwap(MelonLogger.Instance log, bool enable, bool hideHands,
        Transform? assemblyRoot, Transform? ownHand)
    {
        try
        {
            if (assemblyRoot is null || assemblyRoot == null)
            {
                SwapStatus = "visual: no assembly";
                return;
            }

            if (drivenMeshes is null || thirdPersonMeshes is null
                || Time.unscaledTime >= nextMeshRefresh)
            {
                nextMeshRefresh = Time.unscaledTime + 0.5f;
                // DIE LISTE DES SPIELS ZUERST, der Namenstest als RUECKFALL.
                //
                // Gemessen im DLC-Lauf, und der Mod hat es selbst gemeldet:
                //   gun meshes: NONE matched. The name filter is wrong
                //   visual swap: 0 driven SHOWN, 0 third-person hidden
                // WasherMeshes sucht "PWG_PW2" und "_PW_UX"; der DLC-Washer
                // traegt keines von beiden. Damit wurde die gefahrene Pistole
                // nicht eingeblendet und die Third-Person-Kopie nicht
                // ausgeblendet - das Bild aus Abschnitt 88, 0,33 m neben der
                // Hand.
                //
                // Der Rueckfall ist Absicht und keine Bequemlichkeit: so kann
                // kein Fall, der heute funktioniert, schlechter werden, und die
                // Logzeile sagt, welcher Weg gegriffen hat.
                //
                // Die Third-Person-Seite bekommt ihren EIGENEN Assembler. Der
                // Assembler der ersten Hand nennt nur Renderer der ersten Hand -
                // ihn fuer beide Seiten zu nehmen, waere genau der Fehler, den
                // Abschnitt 108 dokumentiert.
                var thirdPersonRoot = ThirdPersonAssembly(assemblyRoot);
                var drivenNamed = AssemblerMeshes(assembler);
                var thirdPersonNamed = AssemblerMeshes(AssemblerOf(thirdPersonRoot));

                drivenFromAssembler = drivenNamed is not null;
                thirdPersonFromAssembler = thirdPersonNamed is not null;

                drivenMeshes = drivenNamed ?? WasherMeshes(assemblyRoot);
                thirdPersonMeshes = thirdPersonNamed ?? WasherMeshes(thirdPersonRoot);
                bodyMeshes = BodyMeshes(assemblyRoot, assembler, ownHand);
                assemblerKnown = assembler is not null && assembler != null;
            }

            // enable false restores the game's own arrangement, so the A/B is
            // reversible in the headset rather than across two launches.
            var shown = Write(drivenMeshes, enable);
            var hidden = Write(thirdPersonMeshes, !enable);

            // THE HAND MESH UNDER THE WASHER, which HideArms never reached.
            //
            // ApplyArmMode hides ArmGeo under Rig_PlayerArmsPivot. L_Hand is a
            // SEPARATE renderer parented under PowerWasher_Assembly - it showed
            // up enabled in the subtree inventory beside the three washer meshes
            // - so it survived every arm setting and was drawn the whole time.
            //
            // Hidden permanently at the user request, and the reason is
            // structural rather than cosmetic: section 53 established that both
            // arms are ONE skinned mesh on a shared rig, so a single hand cannot
            // be retargeted without the other. Using it in VR would need a
            // separate left/right hand mesh, which is explicitly out of scope.
            // Re-asserted per frame like the rest, because a nozzle swap rebuilds
            // these clones.
            var hands = hideHands ? Write(bodyMeshes, false) : 0;

            if (!loggedSwap)
            {
                loggedSwap = true;
                log.Msg($"  visual swap: {shown} driven washer mesh(es) "
                    + $"{(enable ? "SHOWN" : "hidden")}, {hidden} third-person "
                    + $"{(enable ? "hidden" : "shown")}, {hands} body mesh(es) hidden"
                    + $"   [driven via {(drivenFromAssembler ? "assembler" : "NAME FALLBACK")}"
                    + $", tp via {(thirdPersonFromAssembler ? "assembler" : "NAME FALLBACK")}]"
                    + $"{(assemblerKnown ? "" : "   ASSEMBLER UNRESOLVED - nothing hidden")}");

                // DIE PFADE, nicht nur die Zahl. Abschnitt 112 hat das an den
                // gesperrten Menuezeilen gelernt: eine Zahl kann nicht sagen,
                // WELCHE. Beim naechsten DLC steht hier ohne Ratespiel, wo die
                // Geometrie diesmal haengt.
                if (bodyMeshes is not null)
                {
                    for (var index = 0; index < bodyMeshes.Length && index < 5; index++)
                    {
                        var renderer = bodyMeshes[index];

                        if (renderer is null || renderer == null)
                            continue;

                        log.Msg($"    body[{index}] {PathOf(renderer.transform)}");
                    }
                }
            }

            SwapStatus = enable
                ? $"visual: driven {shown}, tp off {hidden}"
                : "visual: game default";
        }
        catch (Exception exception)
        {
            log.Warning($"  visual swap threw {exception.GetType().Name}: {exception.Message}");
            drivenMeshes = null;
            thirdPersonMeshes = null;
            SwapStatus = "visual: failed";
        }
    }

    private static int Write(Renderer[]? meshes, bool enabled)
    {
        if (meshes is null)
            return 0;

        var count = 0;

        for (var index = 0; index < meshes.Length; index++)
        {
            var renderer = meshes[index];

            if (renderer is null || renderer == null)
                continue;

            if (renderer.enabled != enabled)
                renderer.enabled = enabled;

            count++;
        }

        return count;
    }

    // Reached from the driven assembly's own root rather than by a scene search,
    // the same discipline the assembler resolution now follows: the path is
    // taken verbatim from the measured hierarchy, so there is nothing to pick
    // wrongly.
    private static Transform? ThirdPersonAssembly(Transform drivenAssembly)
    {
        var root = drivenAssembly.root;

        return root is null || root == null
            ? null
            : root.Find("Third Person Visuals/EquipmentAnchor/PowerWasher_Assembly");
    }

    // KEIN NAMENSTEST MEHR, und das ist der Kern dieser Fassung.
    //
    // Die Vorfassung suchte "Hand", "Glove" und "fps_arms". Das traf im
    // Hauptspiel genau debug_hand/L_Hand - und im DLC nichts: dort liefert das
    // Spiel dieselbe Geometrie unter eigenen Namen, und ein Tester sah die Hand,
    // obwohl die Checkbox sie abwaehlt. Weitere DLC sind angekuendigt, also
    // entscheidet ab hier STRUKTUR statt Kunst-Asset-Name.
    //
    // Ausgeblendet wird ein Renderer unter der gefahrenen Assembly, wenn ALLE
    // DREI Bedingungen zutreffen:
    //
    //   1. DER ASSEMBLER NENNT IHN NICHT. m_pressureGunRenderer,
    //      m_extensionRenderer und m_nozzleRenderers sind die Liste, die das
    //      Spiel SELBST ueber seine Pistole fuehrt - verglichen wird ueber
    //      GetInstanceID, nicht ueber Zeichenketten. Was darin steht, IST die
    //      Pistole und bleibt sichtbar, unter welchem Namen auch immer.
    //
    //   2. SEIN TYP IST MeshRenderer ODER SkinnedMeshRenderer. Koerpergeometrie
    //      ist immer eines von beiden; VFXRenderer, ParticleSystemRenderer,
    //      LineRenderer und TrailRenderer sind nie Koerper. Das deckt die
    //      Kontaktgrafiken UND den Nebel unabhaengig davon, wo sie haengen - und
    //      der Nebelknoten fehlt in jeder vorhandenen Hierarchie-Aufnahme, seine
    //      Elternkette ist also unbelegt.
    //
    //      Geprueft wird der ECHTE IL2CPP-Typ ueber GetIl2CppType, nicht
    //      GetType().Name: GetComponentsInChildren<Renderer> gibt jeden Eintrag
    //      als Renderer-Huelle zurueck, GetType().Name liest darum immer
    //      "Renderer". Dasselbe Mittel wie in GameInput bei
    //      PlayerItemHolderBase.
    //
    //   3. KEIN VORFAHRE TRAEGT NozzleAnchor. Die gesamte Wassertechnik -
    //      WashJet, beide Strahlmeshes, die Kontaktgrafiken - haengt unter
    //      NozzleAnchor(Clone), und dieser Knoten traegt die Komponente
    //      FuturLab.PW2.NozzleAnchor. Gemessen an
    //      diagnostics/2026-09-13-runtime-hierarchy: 9 von 9 Effekt-Renderern
    //      liegen darunter, die Hand und die Pistolengeometrie nicht.
    //
    //      Bewusst NICHT ueber das aufgeloeste raySpawn aus Pose: dort haengt nur
    //      die AKTIVE Duese. Die Effekt-Renderer der uebrigen Duesen waeren sonst
    //      ausgeblendet und fehlten nach dem naechsten Duesenwechsel.
    //
    // Gegenprobe am gemessenen Inventar (14 Renderer, Log 26-9-18_22-15-37):
    // 3 Pistole + 10 Wassertechnik + 1 Hand. Die Regel blendet genau EINEN aus
    // und reproduziert damit das bisherige "1 hand mesh(es) hidden" bitgenau.
    //
    // Ist der Assembler nicht aufgeloest, wird NICHTS ausgeblendet: ohne seine
    // Liste ist die Pistole nicht von Koerpergeometrie zu trennen, und eine
    // unsichtbare Waschpistole ist der teurere Fehler. Die Logzeile sagt dann,
    // dass das der Grund war.
    private static Renderer[]? BodyMeshes(Transform? root,
        Il2CppFuturLab.PW2.PowerWasherAssembler? assembler, Transform? ownHand)
    {
        if (root is null || root == null)
            return null;

        if (assembler is null || assembler == null)
            return new Renderer[0];

        var all = root.GetComponentsInChildren<Renderer>(true);

        if (all is null)
            return null;

        var gun = GunRendererIds(assembler);
        var kept = new List<Renderer>();

        for (var index = 0; index < all.Length; index++)
        {
            var renderer = all[index];

            if (renderer is null || renderer == null)
                continue;

            if (gun.Contains(renderer.GetInstanceID()))
                continue;

            if (!PlainGeometry(renderer))
                continue;

            if (UnderNozzleAnchor(renderer.transform, root))
                continue;

            // DIE EIGENE HAND DES MODS, und das ist die Korrektur eines
            // Eigentors.
            //
            // Die VR-Hand wird unter debug_hand eingehaengt und erfuellt danach
            // alle drei Kriterien oben: ein SkinnedMeshRenderer unter der
            // Assembly, vom Assembler nicht genannt, nicht in einer
            // Duesenkette. Die Regel hat sie folgerichtig ausgeblendet - im Log
            // sprang die Zahl von 1 auf 2 und body[1] nannte den Pfad
            // .../debug_hand/Rig_VRHand_R(Clone)/R_Hand.
            //
            // Ausgenommen wird sie STRUKTURELL, nicht ueber ihren Namen: alles
            // unterhalb dieses Knotens gehoert dem Mod.
            if (ownHand is not null && ownHand != null
                && Underneath(renderer.transform, ownHand))
                continue;

            kept.Add(renderer);
        }

        return kept.ToArray();
    }

    // Die Positivliste des Spiels ueber seine eigene Pistole. Referenzen, keine
    // Namen - ein DLC kann die Meshes umbenennen, aber nicht aus dieser Liste
    // nehmen, ohne seinen eigenen Zusammenbau zu brechen.
    //
    // null bei leerer Liste, damit die Aufrufstelle mit ?? auf den Namenstest
    // zurueckfallen kann. Leer und "nicht vorhanden" muessen unterscheidbar
    // bleiben.
    private static Renderer[]? AssemblerMeshes(
        Il2CppFuturLab.PW2.PowerWasherAssembler? assembler)
    {
        if (assembler is null || assembler == null)
            return null;

        var kept = new List<Renderer>();

        try
        {
            var pressureGun = assembler.m_pressureGunRenderer;

            if (pressureGun is not null && pressureGun != null)
                kept.Add(pressureGun);

            var extension = assembler.m_extensionRenderer;

            if (extension is not null && extension != null)
                kept.Add(extension);

            var nozzles = assembler.m_nozzleRenderers;

            if (nozzles is not null)
            {
                for (var index = 0; index < nozzles.Count; index++)
                {
                    var nozzle = nozzles[index];

                    if (nozzle is not null && nozzle != null)
                        kept.Add(nozzle);
                }
            }
        }
        catch
        {
            // Eine unvollstaendige Liste ist schlimmer als keine: sie liesse
            // Pistolenteile als Koerpergeometrie gelten. Dann lieber der
            // Namenstest, der wenigstens bekannt ist.
            return null;
        }

        return kept.Count == 0 ? null : kept.ToArray();
    }

    // Der Assembler eines Assembly-Knotens. Dieselbe Form wie in Resolve, damit
    // die Third-Person-Seite ihren eigenen bekommt statt den der ersten Hand.
    private static Il2CppFuturLab.PW2.PowerWasherAssembler? AssemblerOf(Transform? root)
    {
        if (root is null || root == null)
            return null;

        var own = root.GetComponent<Il2CppFuturLab.PW2.PowerWasherAssembler>();

        if (own is not null && own != null)
            return own;

        var child = root
            .GetComponentInChildren<Il2CppFuturLab.PW2.PowerWasherAssembler>(true);

        return child is not null && child != null ? child : null;
    }

    // Dieselbe Liste, als Kennungen. Baut auf AssemblerMeshes auf, damit es
    // genau EINE Stelle gibt, die weiss, was die Pistole ist.
    private static List<int> GunRendererIds(
        Il2CppFuturLab.PW2.PowerWasherAssembler assembler)
    {
        var ids = new List<int>();
        var meshes = AssemblerMeshes(assembler);

        if (meshes is null)
            return ids;

        for (var index = 0; index < meshes.Length; index++)
        {
            var renderer = meshes[index];

            if (renderer is not null && renderer != null)
                ids.Add(renderer.GetInstanceID());
        }

        return ids;
    }

    // Koerpergeometrie ist Mesh oder SkinnedMesh. Wirft der Typlesevorgang, gilt
    // der Renderer als NICHT-Koerper und bleibt sichtbar - die Unsicherheit
    // faellt zugunsten des Sichtbaren aus.
    private static bool PlainGeometry(Renderer renderer)
    {
        try
        {
            var type = renderer.GetIl2CppType()?.Name;

            return string.Equals(type, "MeshRenderer", StringComparison.Ordinal)
                || string.Equals(type, "SkinnedMeshRenderer", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    // Liegt der Knoten unterhalb von root? Dieselbe Aufwaertsform wie
    // UnderNozzleAnchor, nur gegen einen bekannten Knoten statt gegen eine
    // Komponente.
    private static bool Underneath(Transform? node, Transform root)
    {
        var walk = node;
        var rootId = root.GetInstanceID();
        var guard = 0;

        while (walk is not null && walk != null && guard++ < 24)
        {
            if (walk.GetInstanceID() == rootId)
                return true;

            walk = walk.parent;
        }

        return false;
    }

    // Aufwaerts bis zur Assembly, nicht weiter. Die Komponente NozzleAnchor
    // markiert die Wurzel der Wassertechnik einer Duese - jeder Duese, nicht nur
    // der aktiven.
    private static bool UnderNozzleAnchor(Transform? node, Transform root)
    {
        var walk = node;
        var rootId = root.GetInstanceID();

        while (walk is not null && walk != null)
        {
            // Unity-null UND Muster-null: GetComponent liefert bei einem
            // zerstoerten Objekt eine Huelle, die "is null" nicht sieht.
            var anchor = walk.GetComponent<Il2CppFuturLab.PW2.NozzleAnchor>();

            if (anchor is not null && anchor != null)
                return true;

            if (walk.GetInstanceID() == rootId)
                return false;

            walk = walk.parent;
        }

        return false;
    }

    private static Renderer[]? WasherMeshes(Transform? root)
    {
        if (root is null || root == null)
            return null;

        var all = root.GetComponentsInChildren<Renderer>(true);

        if (all is null)
            return null;

        var kept = new List<Renderer>();

        for (var index = 0; index < all.Length; index++)
        {
            var renderer = all[index];

            if (renderer is null || renderer == null)
                continue;

            var name = renderer.name;

            // The gun body, the extension and the nozzle, all three named
            // PWG_PW2_PW_UX*. Everything else in the subtree keeps its own
            // visibility.
            if (name.IndexOf("PWG_PW2", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("_PW_UX", StringComparison.OrdinalIgnoreCase) >= 0)
                kept.Add(renderer);
        }

        return kept.ToArray();
    }

    // WHICH WASHER IS ACTUALLY DRAWN, and this is the measurement that the rest
    // of the session should have started with.
    //
    // The subtree inventory settled that the three washer MESHES under the
    // driven PowerWasher_Assembly - body, extension and nozzle - are all
    // enabled False. They are not rendered. Only L_Hand, WashJet and the water
    // and mist effects are. So the washer the player SEES is a different
    // instance, and every lever pulled at the driven chain necessarily did
    // nothing to it: PositionToFOV, FOVCorrectPosition and _ToggleFOV all act on
    // geometry that is invisible.
    //
    // That single fact accounts for the whole symptom cluster at once - the
    // constant muzzle offset, the gun wobbling with the head while laser and jet
    // stand still in the room, and three levers that each provably fired and
    // changed nothing.
    //
    // So: every gun mesh in the player hierarchy, wherever it lives, with the
    // three facts that identify the drawn one - its full path, whether it is
    // enabled, and where it is in the world. The enabled one is the one to
    // drive, and its path says what it is parented to and therefore why it
    // follows the head.
    //
    // Resources.FindObjectsOfTypeAll is used rather than FindObjectsOfType
    // because the candidates are precisely the ones that may be disabled, and it
    // is the pattern already proven in WashLaser.FromExistingLine. It also
    // returns prefab assets, so entries are kept only when they sit under a
    // PlayerCharacter root - a Transform and string test, no struct crosses the
    // boundary (Renderer.bounds and GameObject.scene both would).
    private static void LogEveryGunMesh(MelonLogger.Instance log, Transform? assemblyRoot)
    {
        try
        {
            var found = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Renderer>());

            log.Msg($"  gun meshes: scanning {found.Length} renderer(s) for washer geometry");

            var listed = 0;

            for (var index = 0; index < found.Length && listed < 24; index++)
            {
                Renderer? renderer;
                string name;

                try
                {
                    renderer = found[index]?.TryCast<Renderer>();

                    if (renderer is null || renderer == null)
                        continue;

                    name = renderer.name;
                }
                catch
                {
                    continue;
                }

                // The washer meshes are named PWG_PW2_PW_UX*; the nozzle and
                // extension variants share the PW_UX infix. Kept deliberately
                // loose, because a name filter that is too tight would hide the
                // very instance being hunted.
                if (name.IndexOf("PWG_PW2", StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("_PW_UX", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                try
                {
                    var t = renderer.transform;
                    var path = PathOf(t);

                    // Prefab assets have no PlayerCharacter ancestor, so this
                    // drops them without touching GameObject.scene.
                    if (path.IndexOf("PlayerCharacter", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    listed++;

                    var underDriven = assemblyRoot is not null && assemblyRoot != null
                        && (t == assemblyRoot || t.IsChildOf(assemblyRoot));

                    log.Msg($"    {(renderer.enabled ? "DRAWN    " : "hidden   ")}"
                        + $"underDriven {underDriven,-5}"
                        + $" pos ({t.position.x:0.##}, {t.position.y:0.##}, {t.position.z:0.##})");
                    log.Msg($"        {path}");
                }
                catch (Exception exception)
                {
                    log.Warning($"    gun mesh read threw {exception.GetType().Name}");
                }
            }

            if (listed == 0)
                log.Warning("  gun meshes: NONE matched. The name filter is wrong and the "
                    + "drawn washer cannot be identified from this block.");
        }
        catch (Exception exception)
        {
            log.Warning($"  gun mesh scan threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string PathOf(Transform node)
    {
        var path = node.name;

        for (var parent = node.parent; parent is not null && parent != null; parent = parent.parent)
            path = parent.name + "/" + path;

        return path;
    }

    // WHAT _LockFOV ACTUALLY IS, asked of the shader instead of inferred from
    // its name - and the reason is a measurement that turned out to prove
    // nothing.
    //
    // _LockFOV read exactly 1 and _ToggleFOV read exactly 1. Bit 16384 set the
    // lock to the live camera field of view, 79.646, on the theory that the
    // shader scales by a ratio of two angles. But if _LockFOV is a SWITCH rather
    // than an angle, then 1 was "on" and 79.646 is also "on", and the run could
    // not have tested anything. Two values that are both exactly 1 look far more
    // like booleans than like a field of view.
    //
    // Unity answers this directly. A Shader exposes its property table at
    // runtime: name, type, default value and range. A Range [0,1] settles it as
    // a toggle; a Range [0,180] or a plain Float settles it as an angle. Either
    // way the guessing stops.
    //
    // GetPropertyType returns an int-based enum and GetPropertyName a string,
    // both safe shapes. GetPropertyRangeLimits returns a Vector2 by value, which
    // is the same shape as the Vector3 getters this mod relies on everywhere
    // (transform.position and the rest), so it is no new risk - but it is
    // individually guarded, because a property that is not a Range throws.
    private void LogShaderProperties(MelonLogger.Instance log, Renderer? renderer)
    {
        if (renderer is null || renderer == null)
        {
            log.Msg("  shader props: no renderer");
            return;
        }

        try
        {
            var material = renderer.sharedMaterial;

            if (material is null || material == null)
            {
                log.Msg("  shader props: no shared material");
                return;
            }

            var shader = material.shader;

            if (shader is null || shader == null)
            {
                log.Msg("  shader props: no shader");
                return;
            }

            var count = shader.GetPropertyCount();
            log.Msg($"  shader props: \"{shader.name}\" has {count} propertie(s); "
                + "listing the FOV and depth ones");

            for (var index = 0; index < count; index++)
            {
                string name;

                try
                {
                    name = shader.GetPropertyName(index);
                }
                catch
                {
                    continue;
                }

                if (name.IndexOf("FOV", StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("Depth", StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("Field", StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("Lock", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var type = "?";
                var dflt = "?";
                var range = "";

                try { type = shader.GetPropertyType(index).ToString(); } catch { }
                try { dflt = shader.GetPropertyDefaultFloatValue(index).ToString("0.###"); } catch { }

                try
                {
                    var limits = shader.GetPropertyRangeLimits(index);
                    range = $"   range [{limits.x:0.###}, {limits.y:0.###}]";
                }
                catch
                {
                    // Not a Range property. Absence of a range is itself
                    // informative, so it is not an error.
                }

                var live = "?";
                try { live = material.GetFloat(name).ToString("0.###"); } catch { }

                log.Msg($"    {name,-24} type {type,-8} default {dflt,-8} live {live,-8}{range}");
            }
        }
        catch (Exception exception)
        {
            log.Warning($"  shader props threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    private bool loggedGlobals;
    private float wroteLock = float.NaN;

    internal string FovStatus { get; private set; } = "fov: untouched";

    // THE GLOBALS, and the value that has been sitting in the log since the
    // first probe without anyone acting on it:
    //
    //     gun render probe: _ToggleFOV id 1491  _LockFOV id 1492
    //                       global _LockFOV 1
    //
    // _LockFOV is a FIELD OF VIEW. One degree is degenerate. GunRender's own
    // header raised exactly this as a possibility - "either _LockFOV arrives
    // degenerate under XR or a depth term contributes a second displacement" -
    // and it was never followed up.
    //
    // Why this is now the only candidate left. The gun's TRANSFORM has been
    // exonerated to a millimetre: the residual wrote - toWorld*hand is constant
    // to 0.001 m across a whole run, so the head cancels out of the position
    // expression completely and the washer cannot move when the head turns. Yet
    // it visibly does, while the laser and the jet - drawn through
    // Sprites/Default and PWS/VFX Wash Beam - hold still. The washer meshes are
    // drawn through PWS/Wash Target Uber with _ToggleFOV 1. A vertex shader
    // keyed to the field of view displaces the DRAWN mesh as the camera changes
    // while its transform stands still, which is the one mechanism that produces
    // every symptom at once: a constant muzzle offset AND a head-coupled wobble
    // AND laser and jet unaffected.
    //
    // Bit 16384 sets the global lock to the LIVE camera field of view. If the
    // shader scales by a ratio of the two, making them equal makes the lock a
    // no-op and the washer draws at its true transform. A global float is the
    // cheapest write in the whole project - no material instances, no per-frame
    // allocation - and it is reversible by clearing the bit.
    //
    // Written every frame from OnLateUpdate, after the game's own Update, so a
    // game that also sets it per frame still loses the last word before
    // rendering.
    internal void ApplyFovGlobals(MelonLogger.Instance log, bool overrideLock, bool zeroLock,
        Camera? camera)
    {
        try
        {
            if (visuals is null || visuals == null)
                return;

            // There is NO m_fovPropID on this type - probed, not assumed. The
            // file header claimed one mapping to _FieldOfView and it does not
            // exist; only m_fovLockPropID and m_fovTogglePropID do. The two
            // depth property ids are real and are read alongside, because
            // m_washerDepthMinDist and m_washerDepthScale both sit at 0.5 and a
            // depth term is the other way a vertex shader displaces geometry
            // with the view.
            var lockId = visuals.m_fovLockPropID;

            var liveLock = Shader.GetGlobalFloat(lockId);
            var cameraFov = camera is null || camera == null ? -1f : camera.fieldOfView;

            if (!loggedGlobals)
            {
                loggedGlobals = true;
                var minDistId = visuals.m_washerDepthMinDistPropID;
                var scaleId = visuals.m_washerDepthScalePropID;

                log.Msg($"  fov globals: _LockFOV {liveLock:0.###}   "
                    + $"camera.fieldOfView {cameraFov:0.###}");
                log.Msg($"    depth globals: minDist {Shader.GetGlobalFloat(minDistId):0.###}   "
                    + $"scale {Shader.GetGlobalFloat(scaleId):0.###}   "
                    + $"fields {visuals.m_washerDepthMinDist:0.###} / "
                    + $"{visuals.m_washerDepthScale:0.###}");
            }

            overrideLock = overrideLock || zeroLock;

            if (!overrideLock)
            {
                wroteLock = float.NaN;
                FovStatus = $"fov: lock {liveLock:0.#} cam {cameraFov:0.#}";
                return;
            }

            if (cameraFov <= 0f && !zeroLock)
            {
                FovStatus = "fov: no camera";
                return;
            }

            // Bit 32768 writes ZERO instead of the camera angle, which is the
            // other half of the same question: if _LockFOV is a switch, zero is
            // the only value that turns it off, and 79.646 was just another
            // non-zero "on".
            var target = zeroLock ? 0f : cameraFov;
            Shader.SetGlobalFloat(lockId, target);

            if (float.IsNaN(wroteLock))
                log.Msg($"  fov globals: _LockFOV overridden {liveLock:0.###} -> {target:0.###}");

            wroteLock = target;
            FovStatus = $"fov: lock->{target:0.#} (was {liveLock:0.#})";
        }
        catch (Exception exception)
        {
            log.Warning($"  fov globals threw {exception.GetType().Name}: {exception.Message}");
            FovStatus = "fov: failed";
        }
    }

    private static int WriteRenderer(Renderer? renderer, int toggleId)
    {
        if (renderer is null || renderer == null)
            return 0;

        var written = 0;

        // renderer.materials yields INSTANCES, so this does not touch the shared
        // asset and reverts by itself on a level reload.
        var materials = renderer.materials;

        if (materials is null)
            return 0;

        for (var index = 0; index < materials.Length; index++)
        {
            var material = materials[index];

            if (material is null || !material.HasProperty(toggleId))
                continue;

            material.SetFloat(toggleId, 0f);
            written++;
        }

        return written;
    }

    // The three reads the agent insisted on, and the reason is arithmetic: the
    // lock ratio predicts 11 cm at the measured distance, the screen shows about
    // half a metre. One of these values is not what it is assumed to be.
    private void LogProbe(MelonLogger.Instance log, int toggleId)
    {
        try
        {
            var lockId = visuals!.m_fovLockPropID;

            log.Msg($"  gun render probe: _ToggleFOV id {toggleId}   _LockFOV id {lockId}   "
                + $"global _LockFOV {Shader.GetGlobalFloat(lockId):0.###}");
            log.Msg($"    washerDepthMinDist {visuals.m_washerDepthMinDist:0.###}   "
                + $"washerDepthScale {visuals.m_washerDepthScale:0.###}");

            LogMaterials(log, "pressureGun", assembler!.m_pressureGunRenderer, toggleId);
            LogMaterials(log, "extension", assembler.m_extensionRenderer, toggleId);

            var nozzles = assembler.m_nozzleRenderers;
            if (nozzles is not null)
            {
                for (var index = 0; index < nozzles.Count && index < 4; index++)
                    LogMaterials(log, $"nozzle[{index}]", nozzles[index], toggleId);
            }
        }
        catch (Exception exception)
        {
            log.Warning($"  gun render probe threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void LogMaterials(MelonLogger.Instance log, string label,
        Renderer? renderer, int toggleId)
    {
        if (renderer is null || renderer == null)
        {
            log.Msg($"    {label}: no renderer");
            return;
        }

        try
        {
            var materials = renderer.materials;

            if (materials is null || materials.Length == 0)
            {
                log.Msg($"    {label}: no materials");
                return;
            }

            for (var index = 0; index < materials.Length && index < 3; index++)
            {
                var material = materials[index];

                if (material is null)
                    continue;

                var has = material.HasProperty(toggleId);
                log.Msg($"    {label}[{index}] \"{material.name}\"  "
                    + $"_ToggleFOV {(has ? material.GetFloat(toggleId).ToString("0.###") : "ABSENT")}");
            }
        }
        catch (Exception exception)
        {
            log.Warning($"    {label}: read threw {exception.GetType().Name}");
        }
    }
}
