using System.Globalization;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SubsystemsImplementation;
using UnityEngine.XR;

[assembly: MelonInfo(typeof(WetReality.Pose), "Wet Reality Pose", "1.38.0", "Wet Reality")]
[assembly: MelonGame("FuturLab", "PowerWash Simulator 2")]

namespace WetReality;

// Drives the power washer from the right motion controller.
//
// This is the core VR interaction of the whole project, design document
// sections 4 to 6: head and washer must be fully decoupled. The flat game
// cannot do that, because EquipmentAnchor is a child of PlayerCamera and
// therefore inherits every head rotation.
//
// What it writes, and what it deliberately does not, from section 44:
//
//   EquipmentAnchor       <- controller pose. Owned by EquipmentManager, which
//                            writes it every Update.
//   Rig_PlayerArmsPivot   <- follows the anchor, so the arms do not hang beside
//                            the washer. Owned by PlayerCameraController.
//   the camera            <- nothing yet, and the reason changed. Section 42
//                            claimed the HMD pose was already in the view
//                            matrix, so writing it would double-apply. Section
//                            45 retracts that: head motion does not let you
//                            look around at all, so the pose reaches nothing.
//                            The mapping from section 35 - HeadTurn takes yaw,
//                            PlayerCamera takes pitch - is valid again, but
//                            before writing anything the camera probe below has
//                            to establish whether Unity even receives the pose.
//
// Two writers exist for these transforms and both run in Update. Unity
// evaluates Update, then animation including Animation Rigging, then
// LateUpdate - so writing from OnLateUpdate lands behind both of them without
// needing Harmony at all. If something still overwrites the anchor, the
// fallback is a Harmony postfix on EquipmentManager.Update.
//
// Bound to a key and off by default. With the pose active the flat game is hard
// to use, and nobody who merely launched the game should end up there.
public sealed class Pose : MelonMod
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;


    private MelonPreferences_Entry<string> toggleKey = null!;
    private MelonPreferences_Entry<string> armsKey = null!;
    private MelonPreferences_Entry<string> sourceKey = null!;
    private MelonPreferences_Entry<bool> hideArms = null!;
    private MelonPreferences_Entry<bool> useAimPose = null!;
    private MelonPreferences_Entry<bool> drivePosition = null!;
    private MelonPreferences_Entry<string> positionTarget = null!;
    private MelonPreferences_Entry<bool> worldSpace = null!;
    private MelonPreferences_Entry<string> rayKey = null!;
    private MelonPreferences_Entry<bool> overrideRay = null!;
    private MelonPreferences_Entry<bool> patchWashRay = null!;
    private MelonPreferences_Entry<bool> carryProbe = null!;
    private MelonPreferences_Entry<bool> interactProbe = null!;
    private MelonPreferences_Entry<bool> carryFollowsHand = null!;
    private MelonPreferences_Entry<bool> carrySwapOrigin = null!;
    private MelonPreferences_Entry<bool> interactGrab = null!;
    private MelonPreferences_Entry<float> interactZoneRadius = null!;
    private MelonPreferences_Entry<float> crouchedMoveCap = null!;
    private MelonPreferences_Entry<bool> probeHandAssets = null!;
    private MelonPreferences_Entry<bool> showVrHands = null!;
    private MelonPreferences_Entry<float> offHandPosX = null!;
    private MelonPreferences_Entry<float> offHandPosY = null!;
    private MelonPreferences_Entry<float> offHandPosZ = null!;
    private MelonPreferences_Entry<float> offHandRotX = null!;
    private MelonPreferences_Entry<float> offHandRotY = null!;
    private MelonPreferences_Entry<float> offHandRotZ = null!;
    private MelonPreferences_Entry<float> washerHandPosX = null!;
    private MelonPreferences_Entry<float> washerHandPosY = null!;
    private MelonPreferences_Entry<float> washerHandPosZ = null!;
    private MelonPreferences_Entry<float> washerHandRotX = null!;
    private MelonPreferences_Entry<float> washerHandRotY = null!;
    private MelonPreferences_Entry<float> washerHandRotZ = null!;
    private MelonPreferences_Entry<bool> haptics = null!;
    private MelonPreferences_Entry<float> hapticSeconds = null!;
    private MelonPreferences_Entry<float> hapticAmplitude = null!;
    private MelonPreferences_Entry<float> hapticFrequency = null!;
    private MelonPreferences_Entry<bool> hapticUnfiltered = null!;

    // DIE STRAHL-HAPTIK - Abschnitt 102, Lauf 4. Alle Zahlen als Regler, weil
    // der Nutzer sie im Spiel beurteilen und nachziehen will; ein Wert, der
    // nur im Code steht, kostet pro Korrektur einen Neustart.
    private MelonPreferences_Entry<bool> sprayHapticsOn = null!;
    private MelonPreferences_Entry<float> hapticIntensity = null!;
    private MelonPreferences_Entry<float> hapticJet0 = null!;
    private MelonPreferences_Entry<float> hapticJet15 = null!;
    private MelonPreferences_Entry<float> hapticJet25 = null!;
    private MelonPreferences_Entry<float> hapticJet40 = null!;
    private MelonPreferences_Entry<float> hapticJetSoap = null!;
    private MelonPreferences_Entry<float> hapticJetDefault = null!;
    private MelonPreferences_Entry<float> hapticTurboFactor = null!;
    private MelonPreferences_Entry<float> hapticTurboHz = null!;
    private MelonPreferences_Entry<float> hapticTurboDepth = null!;
    private MelonPreferences_Entry<string> hapticTurboWasher = null!;
    private MelonPreferences_Entry<float> hapticContactFactor = null!;
    private MelonPreferences_Entry<float> hapticContactSmooth = null!;
    private MelonPreferences_Entry<float> hapticContactRange = null!;
    private MelonPreferences_Entry<float> hapticContactSkip = null!;
    private MelonPreferences_Entry<int> hapticContactMask = null!;
    private MelonPreferences_Entry<bool> hapticTurboAuto = null!;
    private MelonPreferences_Entry<float> hapticRefresh = null!;
    private MelonPreferences_Entry<float> hapticBreathe = null!;
    private MelonPreferences_Entry<bool> sprayHapticReport = null!;

    private readonly SprayHaptics sprayHaptics = new();
    private SprayHapticSettings? sprayHapticSettings;

    // Der Begruessungspuls, der auf die Action wartet.
    private bool hapticGreetPending;
    private float hapticGreetUntil;
    private float nextHapticGreet;

    // Der Traegezustand, wie ihn der letzte Puls gesehen hat. Ein eigenes Feld
    // statt eines Vergleichs gegen heldItemState: dieser Puls soll auf
    // AUFNEHMEN und ABLEGEN feuern, nicht auf jede Aenderung des
    // Zustandsnamens.
    private bool hapticCarrying;

    // DIE LINKE HAND IN WELTKOORDINATEN, veroeffentlicht von ApplyPoseSource.
    //
    // Sie wird dort und nur dort gerechnet, weil toWorld dort ohnehin steht:
    // toWorld = camT.rotation * Inverse(hmdRotation), und ein zweiter
    // Rechenweg an einer anderen Stelle im Frame waere eine neue Fehlerquelle
    // statt einer Messung - dieselbe Begruendung, aus der die Zielsuche den
    // veroeffentlichten Strahl liest statt den Transform abzugreifen.
    private Vector3 publishedOffHandWorld;
    private Quaternion publishedOffHandRotation = Quaternion.identity;
    private bool offHandWorldPublished;

    private Vector3 publishedWasherHandWorld;
    private Quaternion publishedWasherHandRotation = Quaternion.identity;
    private bool washerHandWorldPublished;

    // Die VR-Haende des Spiels, eingehaengt. Siehe VrHands.cs.
    private readonly VrHands vrHands = new();
    private MelonPreferences_Entry<string> laserKey = null!;
    private MelonPreferences_Entry<string> laserDirectionKey = null!;
    private MelonPreferences_Entry<bool> showLaser = null!;
    private MelonPreferences_Entry<string> laserDirection = null!;
    private MelonPreferences_Entry<float> laserLength = null!;
    private MelonPreferences_Entry<float> laserWidth = null!;
    private MelonPreferences_Entry<bool> laserAlwaysOnTop = null!;
    private MelonPreferences_Entry<bool> surfaceProbe = null!;
    private MelonPreferences_Entry<bool> menuHighlightHold = null!;
    private MelonPreferences_Entry<float> menuHighlightHoldSeconds = null!;
    private MelonPreferences_Entry<bool> menuHoverEvents = null!;
    private MelonPreferences_Entry<bool> menuSetEventSelection = null!;
    private MelonPreferences_Entry<bool> menuKeepSelection = null!;
    private MelonPreferences_Entry<bool> menuGameHover = null!;

    private bool loggedHoverProbe;

    // Nur fuer diese Sitzung, nicht fuer die cfg. Siehe SendHover.
    private bool hoverDropped;
    private bool loggedSelectionSame;
    private bool loggedSelectionDiffer;
    private bool loggedSelectionThief;
    private int selectionReasserts;
    private bool loggedEventSystems;
    private bool loggedReadBackTook;
    private bool loggedReadBackNull;
    private bool loggedReadBackOther;
    private int lastReassertMark;
    private float lastReassertTime;

    // Das EventSystem des SPIELS, zwischengespeichert. Beide null-Formen
    // werden beim Lesen geprueft: nach einem Auftragswechsel ist die Instanz
    // zerstoert, und "is null" sieht das nicht.
    private UnityEngine.EventSystems.EventSystem? gameEventSystem;
    private bool loggedGameEventSystem;
    private bool loggedEventSystemFallback;
    private IntPtr lastAssertedTarget;
    private int readBackLines;
    private bool selectionRouteDropped;

    // DAS HOVER DES SPIELS - Abschnitt 106.
    //
    // Das Modul wird gesucht wie der Cursor selbst: ueber
    // FindObjectsOfTypeAll und mit dem ersten aktiven. Vier Instanzen sind
    // Split-Screen-Vorhaltung, und die erste von vier zu nehmen war der Fehler,
    // der die erste Cursor-Sondierung bedeutungslos gemacht hat.
    private Il2CppFuturLab.PW2.ControllerCursorInputModule? cursorModule;

    // EINE Instanz, wiederverwendet: die Hover-Kette lebt in diesem Objekt.
    private UnityEngine.EventSystems.PointerEventData? hoverData;

    private float nextCursorModuleSearch;
    private bool loggedGameHover;
    private bool gameHoverDropped;
    private IntPtr gameHoverTarget;

    private float nextHighlightHold;
    private bool loggedHoldRevert;
    private bool loggedHoldHeld;
    private long highlightReasserts;

    private float surfaceProbeAt = float.MaxValue;
    private bool surfaceProbeDone;
    private MelonPreferences_Entry<int> aimSkip = null!;
    private MelonPreferences_Entry<string> aimSkipKey = null!;
    private MelonPreferences_Entry<bool> driveHeadPosition = null!;
    private MelonPreferences_Entry<float> headRecenterJump = null!;
    private MelonPreferences_Entry<bool> snapTurn = null!;
    private MelonPreferences_Entry<float> turnSpeed = null!;
    private MelonPreferences_Entry<float> snapAngle = null!;
    private MelonPreferences_Entry<float> turnDeadzone = null!;
    private MelonPreferences_Entry<string> uiHideKey = null!;
    private MelonPreferences_Entry<string> uiStereoKey = null!;
    private MelonPreferences_Entry<string> placeVerb = null!;
    private MelonPreferences_Entry<float> itemRotateSpeed = null!;
    private MelonPreferences_Entry<bool> autoEnableWithXr = null!;
    private MelonPreferences_Entry<bool> showSplash = null!;
    private MelonPreferences_Entry<float> splashSeconds = null!;
    private MelonPreferences_Entry<float> splashFadeSeconds = null!;
    private MelonPreferences_Entry<float> splashDistance = null!;
    private MelonPreferences_Entry<float> splashWidth = null!;
    private MelonPreferences_Entry<string> splashKey = null!;
    private MelonPreferences_Entry<bool> devMode = null!;
    private MelonPreferences_Entry<bool> verboseDiagnostics = null!;
    private MelonPreferences_Entry<bool> devHotkeys = null!;
    private MelonPreferences_Entry<string> calibrateKey = null!;
    private MelonPreferences_Entry<bool> gestureZones = null!;
    private MelonPreferences_Entry<bool> zoneDebug = null!;
    private MelonPreferences_Entry<float> shoulderZoneX = null!;
    private MelonPreferences_Entry<float> shoulderZoneY = null!;
    private MelonPreferences_Entry<float> shoulderZoneZ = null!;
    private MelonPreferences_Entry<float> shoulderZoneRadius = null!;
    private MelonPreferences_Entry<float> hipZoneX = null!;
    private MelonPreferences_Entry<float> hipZoneY = null!;
    private MelonPreferences_Entry<float> hipZoneZ = null!;
    private MelonPreferences_Entry<float> hipZoneRadius = null!;
    private MelonPreferences_Entry<float> washerZoneForward = null!;
    private MelonPreferences_Entry<float> washerZoneRadius = null!;
    private MelonPreferences_Entry<float> gestureCooldown = null!;
    private MelonPreferences_Entry<float> gestureEchoFactor = null!;
    private MelonPreferences_Entry<bool> menuMissReport = null!;
    private MelonPreferences_Entry<bool> aimMissReport = null!;
    private MelonPreferences_Entry<bool> aimChainReport = null!;

    // Diese Sitzung: die Kopfdrehung dieses Frames, festgehalten von DriveHead.
    // Der Pose-Schreiber braucht sie, um sie herausrechnen zu koennen, und der
    // Kettenbericht, um sie neben Controller und Gun zu stellen.
    private Quaternion headRotation = Quaternion.identity;
    private Vector3 headEuler;
    private float nextChainReport;
    private float chainLastHeadPitch = 999f;

    // DER STRAHL, DEN DAS SPIEL BEKOMMT, festgehalten von DriveRay an der
    // Stelle, an der er auch an GameInput geht. Die Zielsuche liest ihn von
    // hier statt den Transform selbst abzugreifen - Begruendung bei
    // FindAimTarget.
    private Vector3 publishedAimOrigin;
    private Vector3 publishedAimForward;
    private bool aimPublished;
    private MelonPreferences_Entry<float> pickupDistanceMin = null!;
    private MelonPreferences_Entry<bool> aimInteraction = null!;
    private MelonPreferences_Entry<float> aimInteractionRadius = null!;
    private MelonPreferences_Entry<float> aimInteractionRange = null!;
    private MelonPreferences_Entry<bool> uiNavigation = null!;
    private MelonPreferences_Entry<bool> menuPointer = null!;
    private MelonPreferences_Entry<bool> menuRectCandidates = null!;
    private MelonPreferences_Entry<bool> menuSettingControls = null!;
    private MelonPreferences_Entry<bool> menuScroll = null!;
    private MelonPreferences_Entry<float> menuScrollSpeed = null!;
    private MelonPreferences_Entry<float> menuAdjustSpeed = null!;
    private MelonPreferences_Entry<bool> sprayLatchLockout = null!;
    private MelonPreferences_Entry<bool> sprayLatchGripClears = null!;
    private MelonPreferences_Entry<bool> sprayLatchZoneGuard = null!;
    private MelonPreferences_Entry<float> sprayLatchZoneMargin = null!;
    private MelonPreferences_Entry<float> sprayLatchZoneGrace = null!;
    private MelonPreferences_Entry<bool> sprayLatchBuzz = null!;
    private MelonPreferences_Entry<bool> autoStereoUi = null!;
    private MelonPreferences_Entry<float> uiScale = null!;
    private MelonPreferences_Entry<float> uiDistance = null!;
    private MelonPreferences_Entry<bool> uiAlwaysOnTop = null!;
    private MelonPreferences_Entry<float> uiDepthRefresh = null!;

    // Die vorige Menuelage, nur fuer den UI-Tiefendurchlauf. menuMode selbst
    // wird jeden Frame neu bestimmt und traegt keine Flanke.
    private bool uiDepthMenuLast;
    private MelonPreferences_Entry<string> hand = null!;
    private MelonPreferences_Entry<bool> driveArms = null!;
    private MelonPreferences_Entry<float> offsetX = null!;
    private MelonPreferences_Entry<float> offsetY = null!;
    private MelonPreferences_Entry<float> offsetZ = null!;
    private MelonPreferences_Entry<float> pitchOffset = null!;
    private MelonPreferences_Entry<float> yawOffset = null!;
    private MelonPreferences_Entry<float> rollOffset = null!;
    private MelonPreferences_Entry<float> gripX = null!;
    private MelonPreferences_Entry<float> gripY = null!;
    private MelonPreferences_Entry<float> gripZ = null!;
    private MelonPreferences_Entry<bool> showDrivenWasher = null!;
    private MelonPreferences_Entry<string> positionSource = null!;
    private MelonPreferences_Entry<float> rayOriginX = null!;
    private MelonPreferences_Entry<float> rayOriginY = null!;
    private MelonPreferences_Entry<float> rayOriginZ = null!;

    private bool active;
    private string status = "Off. Press the toggle key once OpenXR is running.";

    private Transform? anchor;
    private Transform? armsPivot;

    // The arms are ONE skinned mesh on a shared bone rig - Rig -> Chest ->
    // L_Shoulder / R_Shoulder, animated by CharacterVisualsFirstPerson. There is
    // no left or right parent transform, so one hand cannot be retargeted
    // without the other, and writing bone rotations would be overwritten by the
    // animator every frame. The only cheap lever is whether they are drawn.
    //
    // 0 follow   rotate the shared pivot, so both arms swing with the washer
    // 1 hidden   draw no arms; the washer hangs off EquipmentAnchor, not off a
    //            hand bone, so it stays
    // 2 free     leave the arms to their animation and move the washer alone
    // ARM- UND HANDGEOMETRIE, NAMENSFREI UND AUF EINEM ZEITGEBER.
    //
    // Die Vorfassung hielt EINE Transform-Referenz, gefunden ueber einen
    // sechsgliedrigen Pfad:
    //
    //   Rig_PlayerArmsPivot/Rig_PlayerArms/CHAR_FPS_Default_Visual/
    //   CameraBoneInverse/CHAR_FPS_Default/ArmGeo
    //
    // ZWEI Glieder darin tragen den Outfit-Namen. Ein DLC-Outfit benennt sie um,
    // Transform.Find liefert null, und ApplyArmMode blendet NICHTS aus, obwohl
    // die Checkbox es abwaehlt. Gemessen im Log der gemeldeten Sitzung
    // (26-9-18_22-15-37): beim Splash "arm geo found", nach dem Laden des
    // DLC-Jobs "arm geo MISSING".
    //
    // Verschaerfend war, dass der live-Waechter in Resolve armGeo NICHT prueft:
    // einmal null aufgeloest, fasste nichts nach, und die Arme blieben das ganze
    // Level sichtbar. Dieser Fassung fehlt die Referenz, die still null bleiben
    // koennte - sie liest die Renderer unterhalb des Armpivots nach, auf dem
    // gleichen 0,5-s-Zeitgeber, den GunRender fuer die Waschermeshes fuehrt.
    //
    // Der Pivot selbst haelt: "Rig_PlayerArmsPivot" ist ein EINZIGES Glied unter
    // der Kamera und wurde auch im DLC gefunden. Unterhalb davon gehoert jeder
    // Renderer zum Koerper - gemessen in
    // diagnostics/2026-09-13-runtime-hierarchy: genau zwei, ArmGeo und
    // ArmGeo/fps_arms, alles andere sind Knochen ohne Renderer. Die Pistole
    // haengt unter EquipmentAnchor, einem GESCHWISTERZWEIG, und kann nicht
    // mitgetroffen werden.
    private Renderer[]? armMeshes;
    private float nextArmRefresh;

    // Nur die SELBST ausgeblendeten zuruecknehmen. Ein Renderer, den das Spiel
    // aus eigenem Grund verborgen haelt, darf beim Wiederherstellen nicht
    // aufgehen.
    // GameObjects, nicht Renderer - siehe ApplyArmMode. Vorgemerkt wird nur,
    // was VORHER aktiv war, damit RestoreArms nichts aufgehen laesst, das das
    // Spiel aus eigenem Grund verborgen hielt.
    private readonly List<GameObject> hiddenArmObjects = new();
    private readonly List<int> hiddenArmIds = new();
    private int loggedArmCount = -1;
    private int armReturnedCount = -1;

    // PowerWasher_Assembly, the anchor's only child and the washer itself.
    // Untouched by whatever rewrites the anchor's position, as far as the 0.22.0
    // readback could tell, and it parents the nozzle locators and RaySpawnPoint
    // so the wash ray travels with it.
    private Transform? assembly;

    // The game's own input surface. Resolved lazily and re-resolved when it goes
    // stale, like the anchor: the player is rebuilt per job.
    private Il2CppFuturLab.PW2.BaseInput? playerInput;

    // Der Haltungsgeber. Geholt ueber die Elternkette des Ankers, NICHT per
    // FindObjectOfType: BaseCharacterController ist eine NetworkBehaviour, im
    // Mehrspieler gibt es sie mehrfach, und FindObjectOfType liefert die erste,
    // die ihr begegnet - genau der Fehler, den GunRender fuer die zwei Washer
    // und Abschnitt 108 fuer die zwei Assembler dokumentieren.
    private Il2CppFuturLab.PW2.BaseCharacterController? characterController;

    // -1 heisst "noch nichts gemeldet". Gemeldet wird pro Haltung EINMAL, und
    // nur dann, wenn wirklich ein Rennen gesperrt wurde - ein Logger pro Frame
    // kostet Frames, und dieser Build liefert DevMode = false aus.
    private int loggedBlockedStance = -1;
    private bool loggedNoController;

    // Owns its own canvas and texture, so it is disposed with the mod.
    private readonly Splash splash = new();
    private bool splashArmed;
    private string splashStatus = "splash: idle";

    // The wash ray's real origin. Sits under the ACTIVE nozzle, so its path
    // changes whenever the nozzle does and it has to be searched for rather
    // than addressed.
    private Transform? raySpawn;
    private Il2CppFuturLab.PW2.EquipmentManager? equipment;
    private float nextRaySearch;
    private string rayStatus = "";
    private readonly WashProbe washProbe = new();
    private readonly WashLaser washLaser = new();
    private readonly GameUi gameUi = new();
    private readonly GunRender gunRender = new();
    private string laserStatus = "";
    // Reference pose, captured on the first frame that reads a value.
    //
    // Raw controller position cannot go into a local transform. The tracking
    // origin sits wherever the runtime put it, EquipmentAnchor's parent space is
    // PlayerCamera, and the two have no relation. Only the CHANGE since
    // activation is meaningful, so that is what gets added to the anchor's own
    // resting position.
    private Vector3 anchorBase;
    private Vector3 assemblyBase;
    private Vector3? assemblyRest;
    private Vector3 poseBase;
    private bool baseCaptured;
    private bool recenterRequested;

    // What was last written, and when it was last reported. Read back at the
    // start of the next frame: if the transform no longer holds it, something
    // else owns it and no amount of writing will show.
    private Vector3 wroteAnchorPosition;
    private Vector2 wroteMovement;
    private string statusPrefix = "local";
    private string moveStatus = "";
    private bool anchorPositionWritten;
    private float nextPoseReport;
    private Vector3 poseMin;
    private Vector3 poseMax;
    private Transform? headTurn;
    private XRNode node = XRNode.RightHand;

    // Logged once per activation rather than per frame. A pose that reads as
    // zero every frame would otherwise bury the log in seconds.
    private bool warnedUntracked;

    private InputAction? positionAction;
    private InputAction? rotationAction;
    private InputAction? headRotationAction;
    private InputAction? headPositionAction;
    private InputAction? trackedAction;
    private InputAction? triggerAction;
    private InputAction? moveAction;
    private InputAction? turnAction;

    // The artificial body yaw from design document sections 20 and 21. Held here
    // and written NOWHERE except inside DriveHead: HeadTurn gets exactly one
    // writer per frame, because two writers on one transform is what section 57
    // regression 1 was.
    //
    // Why not BaseInput.LookRaw, which exists and would accept a Vector2 the
    // same way MovementRaw does: DriveHead writes HeadTurn.localRotation as an
    // ABSOLUTE value every frame from OnLateUpdate, which is the last word in
    // the frame. Any yaw the game integrated from LookRaw during Update would be
    // destroyed in the same frame, and the symptom would look exactly like the
    // setter not taking. Body yaw and head yaw are rotations about the same
    // axis, so they commute and simply add - folding it into the existing write
    // is both correct and keeps the single-writer rule.
    private float bodyYaw;

    // Snap turn fires once per deflection, not once per frame. Re-armed when the
    // stick returns inside the deadzone.
    private bool snapArmed = true;
    private bool nozzleArmed = true;
    private InputAction? offHandTrigger;
    private InputAction? offHandPosition;

    // NEU GEBUNDEN, und in diesem Lauf nur gemeldet. Die Gestenzone braucht nur
    // den PUNKT der Off-Hand, deshalb gab es die Rotation bisher nicht - eine
    // Hand an diesem Controller braucht sie, und deswegen wird sie jetzt
    // erhoben, bevor der naechste Lauf sie benutzt.
    private InputAction? offHandRotation;

    // Die VR-Haende des Spiels. Laedt und berichtet, haengt nichts ein.
    private readonly HandAssets handAssets = new();
    private bool refillArmed = true;

    // The rest of the basic controls. All of these bind to actions the input
    // layer ALREADY registers - primary_button, secondary_button, menu_button
    // and squeeze, per hand - so none of this needs a new OpenXR binding and
    // therefore none of it needs a game restart. thumbstick_click would have
    // needed both, so Sprint is derived from stick deflection instead.
    //
    // The letters are the flat game's Modern letters: A jump, B stance, X
    // interact, left menu pause. That is the whole onboarding.
    private InputAction? rightPrimary;
    private InputAction? rightSecondary;
    private InputAction? rightSqueeze;
    private InputAction? leftPrimary;
    private InputAction? leftSecondary;
    private InputAction? leftMenu;
    private InputAction? leftSqueeze;
    private InputAction? leftStickClick;
    private InputAction? rightStickClick;

    private readonly ButtonEdge jumpButton = new();
    private readonly ButtonEdge stanceButton = new();
    private readonly ButtonEdge sprayLatchButton = new();
    private readonly ButtonEdge interactButton = new();
    private readonly ButtonEdge taskButton = new();
    private readonly ButtonEdge menuButton = new();
    private readonly ButtonEdge dirtButton = new();
    private readonly ButtonEdge groupButton = new();
    private readonly ButtonEdge extensionButton = new();
    private bool sprintHeld;
    private string buttonStatus = "buttons: idle";
    private string turnStatus = "turn: idle";

    // 6DOF head position state.
    private Vector3 headPoseBase;
    private Vector3 headTurnBase;
    private Vector3 wroteHeadPosition;
    private bool headBaseCaptured;
    private bool headPositionEverWritten;
    private bool recenterHeadRequested;

    // DIE UNBERUEHRTE RUHELAGE DES SPIELS, getrennt von headTurnBase.
    //
    // Nur die erste Erfassung von headTurn.localPosition ist sauber; ab dem
    // ersten Schreibvorgang steht dort Ruhelage PLUS Versatz. Wer daraus neu
    // erfasst, friert den alten Versatz ein - siehe Abschnitt 128.
    private Vector3 headTurnRest;
    private bool headTurnRestCaptured;

    // Die Kopfpose des Vorframes, fuer die Sprungerkennung.
    private Vector3 lastHeadPose;
    private bool lastHeadPoseValid;
    private int headRecenters;
    private string headPositionStatus = "head pos: off";

    // Both OpenXR pose sources are captured up front and a key picks between
    // them, because the difference is not a calibration matter but a change of
    // definition:
    //
    //   devicepose  /input/grip/pose. Per the OpenXR specification its -Z runs
    //               through the tube the curled fingers form, from little finger
    //               towards thumb. On a pistol grip that points UP THE HANDLE,
    //               which is exactly the tilt reported in section 53.
    //
    //   pointer     /input/aim/pose. Its -Z points along the aiming ray by
    //               definition. This is the pose a washer gun wants, and it is
    //               why the pointer action was registered in the first place.
    private InputAction? gripRotation;
    private InputAction? gripPosition;
    private InputAction? aimRotation;
    private InputAction? aimPosition;

    // Why no list of candidate head binding paths lives here any more:
    //
    // Five were tried and all five missed, because every one of them named
    // <XRHMD>. The device's generated layout is XRInputV1::OpenXR::HeadTrackingOpenXR,
    // so that prefix never matched. The controls were there the whole time,
    // under lower-case names - the capabilities JSON listed DeviceRotation,
    // CenterEyeRotation and the rest, and TryGetChildControl found them at
    // /HeadTrackingOpenXR/centereyerotation.
    //
    // So the binding path is now taken from the control itself, in ListControls.
    // Nothing about it is guessed.

    // Set by ProbeFeatures as soon as a rotation feature reads back, and used by
    // DriveHead from then on. Zero means nothing readable was found.
    private ulong headDeviceId;
    private string headFeature = "";

    private float probeUntil = -1f;
    private float nextProbe;

    private XRDisplaySubsystem? displaySubsystem;

    // Shown on screen so the answer needs no log reading: if these two differ
    // while the head turns, the display subsystem has the pose Unity is not
    // applying.
    private string headStatus = "head: idle";

    public override void OnInitializeMelon()
    {
        var settings = MelonPreferences.CreateCategory("WetRealityPose");
        toggleKey = settings.CreateEntry("ToggleKey", "F2",
            description: "Turns controller-driven washer aiming on and off.");
        hand = settings.CreateEntry("Hand", "RightHand",
            description: "XRNode to read. RightHand or LeftHand.");
        driveArms = settings.CreateEntry("DriveArms", true,
            description: "Also rotate Rig_PlayerArmsPivot to follow the anchor, so the arms stay on the washer.");
        armsKey = settings.CreateEntry("ArmsKey", "F3",
            description: "Cycles the arm mode: follow, hidden, free.");
        // ON by default, and not merely a preference: the game's first-person
        // mesh is ONE object holding both hands. There is no way to hide just the
        // empty hand, so in VR the washer would be held by a floating pair of
        // hands with the left one attached to nothing. Splitting the mesh is out
        // of scope for this mod, so the arms stay off.
        hideArms = settings.CreateEntry("HideArms", true,
            description: "Hide the first-person arms and show only the washer.");

        // DIE RUECKFALLEBENE, und sie ist absichtlich AUSGESCHALTET.
        //
        // Der eigentliche Eingriff sitzt in DriveSprint: der Mod schreibt
        // Sprint nicht mehr, solange die Haltung nicht Standing ist. Damit
        // braucht die Stickstaerke gar nicht beschnitten zu werden, und
        // gehocktes Gehen behaelt sein volles Tempo.
        //
        // Richtet das Spiel zusaetzlich aus der STAERKE heraus auf - unbelegt,
        // aber nicht ausgeschlossen -, dann begrenzt dieser Wert die
        // weitergegebene Auslenkung im Hocken. Multiplikativ, nicht
        // abschneidend: voll ausgeschlagen kommt als cap an, und der ganze
        // Stickweg bleibt gleichmaessig dosierbar.
        //
        // Bei 1.0 wird die Haltung in DriveMovement nicht einmal gelesen, die
        // Rueckfallebene kostet also nichts, solange sie nicht gebraucht wird.
        // EINMAL PRO SITZUNG, und bewusst nicht hinter DevMode: dieser Build
        // liefert DevMode = false aus, und dann waere die Messung beim Tester
        // unsichtbar - die Lehre aus Abschnitt 112.
        //
        // Der Schalter existiert, damit ein Ladevorgang, der sich als teuer
        // oder stoerend erweist, ohne neuen Build abgestellt werden kann.
        // DIE HAENDE. Sichtbar nur, wenn das Armrig ausgeblendet ist - sonst
        // stuenden Arme und Haende gleichzeitig im Bild.
        showVrHands = settings.CreateEntry("ShowVrHands", true,
            description: "Attach the game's own VR hand models to the controllers: "
                + "one at the washer grip, one as a free interaction hand. The "
                + "game's arm rig stays hidden either way - it is one mesh across "
                + "both arms and unusable in VR.");

        // ZUM NACHTRIMMEN IM HEADSET. Die rohe Controller-Pose zeigt nicht
        // dorthin, wo eine Hand sitzen soll, und wie weit daneben sie liegt,
        // sagt am Ende nur das Bild. Der Versatz ist in der Hand-Drehung
        // gerechnet, damit er sich mit ihr dreht.
        // DIE ZWOELF WERTE SIND IM HEADSET GETRIMMT, nicht gerechnet. Sie
        // stammen aus dem Lauf, in dem beide Haende zum ersten Mal standen, und
        // sind vom Nutzer als Grundeinstellung freigegeben.
        //
        // Eine geaenderte Vorgabe greift nur, wo der Schluessel in der cfg noch
        // FEHLT. In einer vorhandenen cfg bleiben die dort eingetragenen Werte
        // stehen - richtig so, es sind die Werte des Spielers.
        offHandPosX = settings.CreateEntry("OffHandPosX", -0.04f,
            description: "Interaction hand offset, metres right in the hand's own frame.");
        offHandPosY = settings.CreateEntry("OffHandPosY", 0f,
            description: "Interaction hand offset, metres up in the hand's own frame.");
        offHandPosZ = settings.CreateEntry("OffHandPosZ", -0.08f,
            description: "Interaction hand offset, metres forward in the hand's own frame.");
        offHandRotX = settings.CreateEntry("OffHandRotX", 70f,
            description: "Interaction hand rotation offset in degrees, pitch.");
        offHandRotY = settings.CreateEntry("OffHandRotY", 20f,
            description: "Interaction hand rotation offset in degrees, yaw.");
        // AUS DEM HEADSET, und die Richtung war andersherum als aus dem
        // Bericht geschlossen: -90 war die Ableitung, +90 ist das Ergebnis am
        // Kopf. Eine aus einer Beschreibung geschlossene Achse ist ein
        // Anfangswert, kein Messwert - getrimmt wird im Bild.
        offHandRotZ = settings.CreateEntry("OffHandRotZ", 90f,
            description: "Interaction hand rotation offset in degrees, roll.");

        // DIE PISTOLENHAND, zum Trimmen sobald sie sichtbar ist. Der Klon sitzt
        // auf Identitaet unter debug_hand, waehrend die spiel-eigene L_Hand dort
        // laut Hierarchie-Aufnahme mit localEuler (270, 0, 0) liegt. Ob genau
        // das der Unterschied ist, sagt erst das Bild - deshalb Knoepfe statt
        // einer geratenen Vorgabe.
        washerHandPosX = settings.CreateEntry("WasherHandPosX", 0.03f,
            description: "Washer hand offset in metres, right in its own frame.");
        washerHandPosY = settings.CreateEntry("WasherHandPosY", 0.03f,
            description: "Washer hand offset in metres, up in its own frame.");
        washerHandPosZ = settings.CreateEntry("WasherHandPosZ", -0.14f,
            description: "Washer hand offset in metres, forward in its own frame.");
        washerHandRotX = settings.CreateEntry("WasherHandRotX", -15f,
            description: "Washer hand rotation offset in degrees, pitch.");
        washerHandRotY = settings.CreateEntry("WasherHandRotY", 0f,
            description: "Washer hand rotation offset in degrees, yaw.");
        washerHandRotZ = settings.CreateEntry("WasherHandRotZ", -80f,
            description: "Washer hand rotation offset in degrees, roll.");

        probeHandAssets = settings.CreateEntry("ProbeHandAssets", false,
            description: "Load the game's own VR hand assets once per session and "
                + "write what they contain to the log. Nothing is shown or attached.");

        crouchedMoveCap = settings.CreateEntry("CrouchedMoveCap", 1.0f,
            description: "Fallback only. 1.0 = off. Below 1.0 it scales the stick "
                + "deflection passed on while crouching or lying down, for the case "
                + "that the game stands the character up from deflection alone.");
        sourceKey = settings.CreateEntry("SourceKey", "F4",
            description: "Switches the pose source between the aim pose and the grip pose.");
        useAimPose = settings.CreateEntry("UseAimPose", true,
            description: "Read the aim pose instead of the grip pose. The grip pose points up the handle.");
        drivePosition = settings.CreateEntry("DrivePosition", true,
            description: "Also write the controller position, not only its rotation.");
        worldSpace = settings.CreateEntry("WorldSpace", true,
            description: "Write the washer pose in world space, decoupled from the head. "
                + "Turn off to fall back to the local delta of 0.23.0.");
        rayKey = settings.CreateEntry("RayKey", "F5",
            description: "Toggles the wash ray override.");
        overrideRay = settings.CreateEntry("AimRayAtNozzle", false,
            description: "Aim the wash ray down the nozzle instead of along the gaze.");
        positionTarget = settings.CreateEntry("PositionTarget", "assembly",
            description: "Which transform receives the position: assembly or anchor. "
                + "The anchor is rewritten by the game every frame.");

        // Six scalars rather than one parsed vector. A string like \"0,0,0\" is
        // ambiguous where the decimal separator is a comma, and this file is
        // edited by hand on a German system.
        offsetX = settings.CreateEntry("PositionOffsetX", 0f, description: "Metres, added to the controller position.");
        offsetY = settings.CreateEntry("PositionOffsetY", 0f, description: "Metres, added to the controller position.");
        offsetZ = settings.CreateEntry("PositionOffsetZ", 0f, description: "Metres, added to the controller position.");
        pitchOffset = settings.CreateEntry("RotationOffsetPitch", 0f, description: "Degrees, applied before the controller rotation.");
        yawOffset = settings.CreateEntry("RotationOffsetYaw", 0f, description: "Degrees, applied before the controller rotation.");
        rollOffset = settings.CreateEntry("RotationOffsetRoll", 0f, description: "Degrees, applied before the controller rotation.");

        // THE GRIP OFFSET, and it is a different KIND of quantity from the three
        // above even though it is also three metre-valued scalars.
        //
        // PositionOffset* is added to the hand-minus-head vector and rotated by
        // toWorld alone, so it calibrates the tracking origin against the game's
        // head. It must not swing when the wrist turns, and it does not.
        //
        // GripOffset* is a constant in the WASHER'S OWN frame, rotated by the
        // full washer rotation. +Z pushes the gun forward along its own barrel,
        // +Y up, +X right - all relative to the gun, not to the room. That is
        // what pins a point of the washer in the hand and lets the body pivot
        // about it rather than swing around it.
        //
        // NEW entries, so MelonLoader writes them into the existing cfg with
        // these defaults. A CHANGED default would never reach this machine.
        gripX = settings.CreateEntry("GripOffsetX", 0f,
            description: "Metres in the washer's own frame. Positive is right, relative to the gun.");
        gripY = settings.CreateEntry("GripOffsetY", 0f,
            description: "Metres in the washer's own frame. Positive is up, relative to the gun.");
        gripZ = settings.CreateEntry("GripOffsetZ", 0f,
            description: "Metres in the washer's own frame. Positive is forward along the barrel.");

        // Split from UseAimPose, which now governs ROTATION only.
        //
        // OpenXR defines the grip pose's origin at the palm, and the aim pose's
        // origin as runtime-chosen - on Touch controllers ahead of the device
        // along the aiming ray. So taking POSITION from the aim pose places the
        // washer away from the hand as a matter of definition, and no trim value
        // repairs that: the two poses have different ORIGINS, not a constant
        // offset in a shared frame.
        //
        // Section 53 did reject the grip pose - but it rejected its ROTATION,
        // "the grip pose points up the handle". Its position was never measured.
        // "follow" is the old coupled behaviour, kept so one keypress restores
        // it inside the headset instead of costing a relaunch.
        // THE VISUAL SWAP, and it is the fix the whole session was looking for.
        //
        // Measured: the washer the player sees is the THIRD-PERSON one, under
        // Third Person Visuals/EquipmentAnchor, while the first-person washer
        // this mod drives has every mesh disabled. The two stood 0.33 m apart
        // vertically. So the drawn gun was never the driven gun, and no offset
        // could have joined them - which is why three levers on the driven chain
        // each fired and changed nothing.
        //
        // On by default: without it the washer does not follow the hand at all,
        // which is the entire point of this mod.
        showDrivenWasher = settings.CreateEntry("ShowDrivenWasher", true,
            description: "Render the FIRST-PERSON washer this mod drives and hide the "
                + "third-person one. Off restores the game's own arrangement, in which "
                + "the visible washer is the third-person model and does not follow the hand.");

        positionSource = settings.CreateEntry("PositionSource", "grip",
            description: "Which pose gives the washer its POSITION: grip (the palm, per "
                + "the OpenXR spec), aim, or follow (whatever UseAimPose says - the "
                + "pre-0.51.0 behaviour). Rotation always follows UseAimPose.");

        // THE RAY ORIGIN, and this one is a CALIBRATION, not a cause fix. Said
        // plainly because the distinction matters for what comes next.
        //
        // Measured state: the nozzle MODEL transform sits at local (0, 0.04,
        // 0.40) and the ray origin at (0, 0.04, 0.45) - 4.5 cm apart, so the
        // geometry agrees with itself. The user sees about 27 cm between the
        // drawn muzzle and where the jet starts, and reaching for the shader FOV
        // lock did not close it: _ToggleFOV was written to 0 on every material
        // in the subtree that carries it and nothing moved.
        //
        // So the drawn washer is displaced by something not yet identified. Until
        // it is, the usable answer is to move the RAY ORIGIN to where the gun is
        // DRAWN, rather than move the gun to the ray. Both close the visible gap;
        // only this one leaves the washer in the hand, which is the thing the
        // grip work of 0.51.0 just established. Trimming GripOffsetZ to +0.265
        // closed the same gap by pushing the whole washer 26.5 cm forward out of
        // the hand.
        //
        // Applied to RaySpawnPoint.localPosition, so the game's own jet VFX, the
        // mod's laser and the effect origin all move TOGETHER - the VFX hangs
        // under that transform and MuzzlePoint reads from it. Clamped per frame,
        // the same shape bit 128 already uses for its rotation and which was
        // verified to survive the game's own rewrites.
        //
        // Gated behind bit 8192 so it can be switched off in the headset, and
        // zero by default: a calibration nobody has dialled in must change
        // nothing.
        rayOriginX = settings.CreateEntry("RayOriginOffsetX", 0f,
            description: "Metres on RaySpawnPoint, in its own frame. Moves jet, laser and effect origin together. Needs mask bit 8192.");
        rayOriginY = settings.CreateEntry("RayOriginOffsetY", 0f,
            description: "Metres on RaySpawnPoint, in its own frame. Needs mask bit 8192.");
        rayOriginZ = settings.CreateEntry("RayOriginOffsetZ", 0f,
            description: "Metres on RaySpawnPoint. Negative pulls the jet BACK towards the drawn muzzle. Needs mask bit 8192.");

        // Off by default. See the header of patch-revert.pl and section 57: the
        // two ref-Ray patches coincided exactly with cleaning breaking, so they
        // are opt-in until measured on their own.
        patchWashRay = settings.CreateEntry("PatchWashRay", false,
            description: "Install the EquipmentManager.WashRay patches. "
                + "Suspected of tearing the visible jet away from the effective one.");

        // MESSUNG, KEIN UMBAU - Abschnitt 102, Lauf 0.
        //
        // Sie klammert PlayerRaycastItemHolder.Move() ein und zaehlt, welchen
        // der drei Kamerawerte das Spiel INNERHALB dieser Klammer liest. Das
        // ist die Frage, an der die Hand-Platzierung haengt, und sie steht in
        // keiner Signatur.
        //
        // Default TRUE fuer diesen Lauf und danach auf false zu setzen: die
        // drei Getter-Postfixes liegen auf einem heissen Pfad, und Abschnitt
        // 87 fuehrt die Kosten pro Frame als offenen Posten. Die Patches
        // werden nur installiert, wenn das hier true ist - ein Lauf ohne
        // Messung zahlt also keinen Zaehler.
        // DIE PLATZIERUNG AN DER HAND - Abschnitt 102, Lauf 1.
        //
        // Der Halter bekommt fuer die Dauer seines Move()-Aufrufs die
        // Handrichtung als Kamera-Rotation und danach seine eigene zurueck.
        // Bodenklemme, Hoehenlimit und Gueltigkeitspruefung bleiben damit beim
        // Spiel - "wie schon immer" war die Bedingung.
        carryFollowsHand = settings.CreateEntry("CarryFollowsHand", true,
            description: "A carried object hangs on the washer hand's pointing ray instead of "
                + "following the head. The game keeps doing the ground clamp and the "
                + "placement check.");

        // Der Ursprung, getrennt schaltbar. Nur die Rotation zu tauschen
        // versetzt das Objekt um den Abstand Auge-zu-Muendung, steuert sich
        // aber vollstaendig ueber die Hand; den Ursprung mitzutauschen aendert
        // zusaetzlich, von wo die Bodenpruefung rechnet. Erst messen, dann
        // zuschalten.
        carrySwapOrigin = settings.CreateEntry("CarrySwapOrigin", false,
            description: "Also move the ray origin to the muzzle while a carried object is "
                + "positioned. Off by default: it changes where the game's ground check "
                + "starts from.");

        // PHYSISCHES GREIFEN ANIMIERTER OBJEKTE - Abschnitt 102, Lauf 2.
        //
        // Auf dem linken TRIGGER, nicht auf dem linken Griff. Damit entsteht
        // die Dreifachbelegung, fuer die Abschnitt 101 eine Vorrangfolge
        // verlangt hat, gar nicht: der Griff behaelt die Verlaengerungs-Geste
        // und den Dreh-Modifikator.
        interactGrab = settings.CreateEntry("InteractGrab", true,
            description: "Left trigger activates an animated interactable when the left hand "
                + "overlaps it. When nothing is in reach the trigger keeps rotating the nozzle "
                + "and recalling soap.");

        // ABSTAND ZUR COLLIDER-OBERFLAECHE, und innen ist 0.
        //
        // Gemessen in Lauf 0: die Klobrille liest 0,44 bis 0,57 m bei einer
        // Handhoehe von 1,15 m gegen einen Deckel bei y 0,76 - das sind die
        // 40 cm, die man beim Hinunterfassen ueberwindet. Der Wickeltisch
        // liest 0 m, weil sein Collider die STANDZONE ist und nicht das Mesh.
        //
        // Deshalb ist diese Zahl der Regler und nicht eine Konstante: auf 0
        // gestellt muss die Hand IN den Collider, was den Wickeltisch
        // verschaerft, ohne einen neuen Build zu kosten.
        interactZoneRadius = settings.CreateEntry("InteractZoneRadius", 0.15f,
            description: "Metres from the interactable's collider surface; inside counts as 0. "
                + "Set it to 0 to require the hand to be inside the collider.");

        // HAPTISCHES FEEDBACK - Abschnitt 102, Lauf 3.
        //
        // Gepulst wird die handelnde Hand. Der Puls ist die einzige
        // Rueckmeldung, die eine Geste ohne Tastendruck haben kann: bei einem
        // Griff an die Huefte sieht man nichts und hoert nichts, und ohne
        // Rueckmeldung ist "die Geste sass nicht" von "das Spiel hat nichts
        // umgeschaltet" nicht zu unterscheiden.
        haptics = settings.CreateEntry("Haptics", true,
            description: "Vibrate the acting hand on a gesture, a grab, and on picking an "
                + "object up or putting it down. Needs XR Boot 0.21.0 or newer.");

        // 0,25 s STATT DER GEWUENSCHTEN 1 s, und das ist eine Empfehlung, kein
        // Widerspruch: ueblich sind 0,05 bis 0,3 s, und bei jeder
        // Duesenkategorie fuehlt sich eine Sekunde wie ein Motorbrummen an. Die
        // Zahl ist ein Regler, 1.0 ist also einen cfg-Eintrag entfernt und
        // kostet keinen Build.
        hapticSeconds = settings.CreateEntry("HapticSeconds", 0.25f,
            description: "Seconds per pulse. Usual haptics are 0.05 to 0.3; 1.0 feels like a "
                + "motor rather than a tap.");

        hapticAmplitude = settings.CreateEntry("HapticAmplitude", 1f,
            description: "Pulse strength, 0 to 1.");

        // DIE ZWEI SCHALTER FUER EINEN STUMMEN CONTROLLER - Abschnitt 102,
        // Lauf 3b. Beide kosten eine cfg-Zeile statt eines Laufs.
        //
        // 0 Hz ist XR_FREQUENCY_UNSPECIFIED und das, was die bequeme Fassung
        // des OpenXR-Pakets sendet. 160 ist ein ueblicher Wert, falls der
        // Runtime eine Zahl braucht.
        hapticFrequency = settings.CreateEntry("HapticFrequency", 0f,
            description: "Hertz. 0 lets the runtime choose, which is what Unity sends. Try 160 "
                + "if nothing is felt and XR Boot reports the binding as resolved.");

        // Unity ruft SendHapticImpulse mit deviceId 0, wenn kein Geraet
        // genannt ist. 0 heisst also "ungefiltert" und ist der zweite Weg,
        // falls die Geraeteliste der falsche ist.
        hapticUnfiltered = settings.CreateEntry("HapticUnfiltered", false,
            description: "Send the impulse with device id 0 instead of to each registered hand "
                + "device. The other route, for when the per-device one stays silent.");

        // ================================================================
        // DIE STRAHL-HAPTIK - Abschnitt 102, Lauf 4.
        //
        // Ziel: ohne Blick auf die Oberflaeche erfuehlen, welcher Strahl aktiv
        // ist, ob Turbo laeuft, ob Seife an ist und ob der Strahl etwas trifft.
        sprayHapticsOn = settings.CreateEntry("SprayHaptics", true,
            description: "Vibrate while spraying. Strength follows the nozzle, the washer "
                + "(turbo) and whether the jet hits a surface.");

        // Die EINE Zahl, die auch im Konfigurationswerkzeug steht. 0 ist aus,
        // 0.65 niedrig, 1.0 Standard, 1.25 stark - die Endamplitude wird
        // trotzdem auf 1 geklemmt, also kann "stark" nichts sprengen.
        hapticIntensity = settings.CreateEntry("HapticIntensity", 1f,
            description: "Overall spray haptic strength. 0 off, 0.65 low, 1.0 default, "
                + "1.25 strong. The final amplitude is clamped to 1 either way.");

        // GEMESSEN, NICHT GERATEN: NozzleType.ShortName liest "0", "15", "25",
        // "40" und "Soap" - der Spruehwinkel als Zahl. Je konzentrierter, desto
        // kraeftiger, genau wie vom Nutzer vorgegeben.
        hapticJet0 = settings.CreateEntry("HapticJet0", 0.42f,
            description: "Amplitude for the 0 degree nozzle, the most concentrated jet.");
        hapticJet15 = settings.CreateEntry("HapticJet15", 0.34f,
            description: "Amplitude for the 15 degree nozzle.");
        hapticJet25 = settings.CreateEntry("HapticJet25", 0.26f,
            description: "Amplitude for the 25 degree nozzle.");
        hapticJet40 = settings.CreateEntry("HapticJet40", 0.18f,
            description: "Amplitude for the 40 degree nozzle, the widest jet.");

        // SEIFE IST EINE DUESE, keine Betriebsart - gemessen als "Soap" in
        // Gruppe 2. Ein Faktor 0,65 haette nichts zu multiplizieren, weil bei
        // aktiver Seife kein Winkelstrahl existiert. Darum eine eigene Basis,
        // die 0,42 x 0,65 entspricht.
        hapticJetSoap = settings.CreateEntry("HapticJetSoap", 0.27f,
            description: "Amplitude for the soap nozzle. Soap is a nozzle in PWS2, not a mode, "
                + "so it carries its own base instead of a factor.");

        hapticJetDefault = settings.CreateEntry("HapticJetDefault", 0.30f,
            description: "Amplitude for a nozzle this mod does not know by name.");

        // TURBO IST EIN REINIGER, nicht eine Duese: die Schultergeste ruft
        // InvokeSwitchGun. Damit ist er orthogonal zur Strahlart, und der
        // Entwurf des Nutzers gilt hier unveraendert.
        hapticTurboFactor = settings.CreateEntry("HapticTurboFactor", 1.15f,
            description: "Multiplies the nozzle base while the turbo washer is equipped.");
        hapticTurboHz = settings.CreateEntry("HapticTurboHz", 15f,
            description: "Hertz of the turbo rattle. 12 to 18 reads as a fast rotating jet; "
                + "below 8 it becomes single knocks.");
        hapticTurboDepth = settings.CreateEntry("HapticTurboDepth", 0.6f,
            description: "The quiet half of the turbo rhythm, as a share of the loud half.");

        // WELCHER Reiniger der Turbo ist, steht in keiner Messung: beide
        // heissen "...Light" und beide sind Klasse Light. Der Log nennt in
        // jedem "washer config"-Block den aktuellen Namen - ein Teilstring
        // daraus hier eingetragen, und das Muster ist scharf, ohne Build.
        hapticTurboWasher = settings.CreateEntry("TurboWasherName", "",
            description: "Part of the turbo washer's asset name, e.g. PVLight. Empty means no "
                + "turbo pattern. The log line \"washer config\" names the current washer.");

        // 0.45 STATT 0.78, und der Wert kommt aus dem Urteil des Nutzers: der
        // Unterschied war nicht zu spueren. Er war es aber nicht, weil 0,78 zu
        // hoch lag - der Faktor wirkte dauerhaft, weil das Kontaktsignal
        // konstant "kein Treffer" meldete. Mit einem echten Signal traegt der
        // Wandkontakt jetzt die volle Amplitude, die als gut gemeldet ist, und
        // nur das Spruehen in die Luft wird leiser. Genau so war die Bitte.
        hapticContactFactor = settings.CreateEntry("HapticContactFactor", 0.45f,
            description: "Amplitude share while the jet hits nothing. Contact itself always "
                + "carries the full amplitude, so this number only weakens free spraying.");
        hapticContactSmooth = settings.CreateEntry("HapticContactSmooth", 0.15f,
            description: "Seconds to cross between no contact and contact. Without it, "
                + "sweeping past an edge would click.");

        // GEMESSEN UND VERWORFEN: m_lastCrosshairHitDistance liest konstant
        // 100, auch am Wandkontakt, ueber jede Stichprobe eines ganzen Laufs.
        // Es ist kein Kontaktmelder, und die zwei Schwellen, die daran hingen,
        // sind darum weg.
        //
        // DER ERSATZ ist ein eigener Strahl entlang der veroeffentlichten
        // Waschrichtung, mit der Ueberladung ohne RaycastHit: zwei Vector3
        // hinein, ein bool heraus.
        hapticContactRange = settings.CreateEntry("HapticContactRange", 8f,
            description: "Metres. How far the contact ray looks along the wash direction. The "
                + "washers measure 6.98 m of range, so a little more covers them.");

        // Der Strahl beginnt ein Stueck VOR der Muendung. Ohne diesen Versatz
        // koennte ein Kollider der Pistole oder der Spielfigur den Strahl
        // sofort stoppen, und der Kontakt laege dauerhaft an - im Log als
        // "contact 1" auch beim Spruehen in die Luft zu sehen.
        hapticContactSkip = settings.CreateEntry("HapticContactSkip", 0.1f,
            description: "Metres skipped at the muzzle, so the washer's own colliders cannot "
                + "count as a hit.");

        // -5 ist Unitys DefaultRaycastLayers, also alles ausser der
        // Ignore-Raycast-Ebene. Als Regler, damit eine Ebene, die falsche
        // Treffer liefert, ohne Build ausgeschlossen werden kann.
        hapticContactMask = settings.CreateEntry("HapticContactMask", -5,
            description: "Layer mask for the contact ray. -5 is Unity's DefaultRaycastLayers.");

        // TURBO GEMESSEN STATT BENANNT: NozzleInstance.TurboRotation dreht
        // sich, wenn der Strahl rotiert. Damit braucht niemand den Asset-Namen
        // des Turbo-Reinigers zu erraten - beide gemessenen heissen "...Light"
        // und sind Klasse Light.
        hapticTurboAuto = settings.CreateEntry("HapticTurboAuto", true,
            description: "Detect turbo from the nozzle's own rotation instead of the washer "
                + "name. TurboWasherName still overrides it when set.");

        // Ein Dauerpuls entsteht durch Nachsenden; das Intervall ist der
        // Kompromiss zwischen Glaette und Last. Das Turbomuster verkuerzt es
        // selbst auf seine Halbwelle.
        hapticRefresh = settings.CreateEntry("HapticRefresh", 0.06f,
            description: "Seconds between re-sends of the steady pulse. Each send lasts longer "
                + "than the interval, so a late frame leaves no gap.");

        hapticBreathe = settings.CreateEntry("HapticBreathe", 0.05f,
            description: "Slow natural variation, as a share. Must stay under about 0.05 or it "
                + "reads as deliberate pulsing. 0 switches it off.");

        sprayHapticReport = settings.CreateEntry("SprayHapticReport", true,
            description: "Log one spray-haptic line per second while the driver is on. It "
                + "carries the numbers that decide the tuning; turn it off once they fit.");

        carryProbe = settings.CreateEntry("CarryProbe", false,
            description: "Measurement only: count which camera value the game reads inside the "
                + "item holder's Move(). Measured as none of the three, so this is off - the "
                + "bracket itself stays, it carries the hand-driven placement.");

        // MESSUNG, KEIN UMBAU - Abschnitt 102, Lauf 0.
        //
        // Auf dem linken Trigger, ZUSAETZLICH zu dem, was er schon tut: der
        // Druck loest weiterhin Duese drehen und Seife aus. Dieser Lauf
        // aendert am Verhalten nichts, er schreibt einen Block pro Druck.
        interactProbe = settings.CreateEntry("InteractProbe", true,
            description: "Measurement only: on every left-trigger press, dump where the child "
                + "renderers and colliders of the animated interactables sit and how far the "
                + "left hand is from them. The trigger keeps doing what it did.");

        // The control instrument for the aiming chain. Purely visual and purely
        // read-only - it draws a line, it writes nothing to the game - so unlike
        // the ray patches it defaults ON: it is what makes the deviation between
        // the visible jet and the effective one filmable instead of a judgement
        // call. Section 58 was written from frames where the numbers happened to
        // agree; a line in the picture cannot be misread that way.
        // Keypad, NOT a function key. The full survey of this game folder found
        // every F-key from F2 to F11 already taken - F4 by XR Boot's GateKey AND
        // Pose's SourceKey at the same time, F6 by XR Boot's ShutdownKey, F7 by
        // UnityExplorer, F8 by XR Boot's BootKey, F9 to F11 by Discovery. Two of
        // those collisions were mine, made by surveying only this mod's source
        // instead of the other mods and the game folder's own config. The keypad
        // divide/multiply/minus cluster is free, findable blind, and well clear
        // of the digits the trim controls use.
        laserKey = settings.CreateEntry("LaserKey", "KeypadDivide",
            description: "Toggles the wash-direction laser. Keypad /");
        // OFF by default. It is a measuring instrument from the decoupling work, not
        // a feature, and a green line out of the nozzle is the first thing a new
        // user reports as a bug.
        showLaser = settings.CreateEntry("ShowWashLaser", false,
            description: "Draw a red line from the nozzle along the measured wash direction.");
        // F8, NOT F7. F7 is UnityExplorer's own toggle - "UnityExplorer Toggle"
        // = "F7" in the game's MelonPreferences.cfg - so the first version of
        // this collided with it: one press cycled the laser AND opened
        // UnityExplorer, whose overlay collapses the stereo view to flat 2D.
        // The key survey that picked F7 read only this project's sources; the
        // other mods' keys live in the game folder's config and have to be
        // checked there too.
        laserDirectionKey = settings.CreateEntry("LaserDirectionKey", "KeypadMultiply",
            description: "Cycles which direction the laser follows. Keypad *");

        // Defaults to nozzle now, and that is the whole instrument. Measured:
        // WashingDirection IS RaySpawnPoint.forward, so wash and nozzle draw the
        // SAME line - there is nothing to compare between them. What matters is
        // where that one line points: at the gaze means the game still owns the
        // aim, along the gun means the decoupling took.
        laserDirection = settings.CreateEntry("LaserDirection", "nozzle",
            description: "nozzle = RaySpawnPoint.forward, the EFFECTIVE wash direction "
                + "(identical to wash, measured). camera = gaze, for reference.");

        // A BITMASK, because the first field result showed 1 and 2 each helping
        // partially - which means the rest of the gaze term lives in the other
        // one and they have to be suppressible together. Starts at 3, both of
        // the two that worked, since that is the state worth testing first.
        //
        //   1 SetWashDirection   2 SetScreenSpaceWashDirection
        //   4 SetScreenSpaceSurfaceHeadDirection   8 TrySetTurboNozzleWashDirection
        aimSkip = settings.CreateEntry("WashAimSkip", 230289,
            description: "Bitmask of suppressed aim methods. 1 SetWashDirection, "
                + "2 SetScreenSpaceWashDirection, 4 SetScreenSpaceSurfaceHeadDirection, "
                + "8 TrySetTurboNozzleWashDirection. 0 changes nothing and only counts.");
        aimSkipKey = settings.CreateEntry("WashAimSkipKey", "KeypadMinus",
            description: "Cycles the suppressed aim method. Keypad -");

        // Comfort turning, design document sections 20 and 21. Smooth by
        // default because that is what was asked for; snap is a config switch
        // rather than a key, since the whole keypad and F2-F11 are taken and
        // this is a preference, not an experiment that needs switching in the
        // headset.
        // 6DOF head tracking. On by default: without it standing up does not
        // change the eye height, which is one of the things that most breaks the
        // sense of being in the room. Written on HeadTurn, with a readback, so a
        // game that owns that transform reports REVERTED instead of being
        // silently fought.
        driveHeadPosition = settings.CreateEntry("DriveHeadPosition", true,
            description: "Applies the HMD position to HeadTurn, so standing up "
                + "and leaning move the viewpoint. Num3 recentres it.");

        // DIE META-TASTE IST NICHT LESBAR, ihre WIRKUNG schon.
        //
        // /input/system/click behaelt die Laufzeitumgebung fuer sich
        // (XRInput.cs:649). Haelt der Spieler META und kalibriert neu,
        // verschiebt sie den Tracking-Ursprung: die Kopfpose springt in EINEM
        // Frame um einen Betrag, den kein Kopf zustande bringt. Das ist das
        // Signal.
        //
        // 0,25 m IST EIN ANFANGSWERT, KEIN MESSWERT - bei 90 Hz liegt es weit
        // ueber jeder echten Kopfbewegung pro Frame und unter jedem
        // Recentering-Sprung. Getrimmt wird im Headset, dafuer ist es ein
        // Regler; 0 schaltet die Erkennung ab.
        headRecenterJump = settings.CreateEntry("HeadRecenterJump", 0.25f,
            description: "Metres. A head pose that moves further than this in a single "
                + "frame is the runtime recentring, not a person - the eye-height "
                + "reference is then re-captured so holding the META button works. "
                + "0 turns the detection off.");

        snapTurn = settings.CreateEntry("SnapTurn", false,
            description: "Right stick X snaps by SnapAngle instead of turning smoothly.");
        turnSpeed = settings.CreateEntry("TurnSpeed", 90f,
            description: "Degrees per second at full deflection, smooth turn.");
        snapAngle = settings.CreateEntry("SnapAngle", 30f,
            description: "Degrees per flick, snap turn.");

        // The movement stick needs no deadzone because it hands its raw Vector2
        // to the game, which applies the player's own sensitivity and deadzone.
        // An angle integrated in this mod has no such owner and the control is
        // the raw generated Stick, so the deadzone has to live here.
        turnDeadzone = settings.CreateEntry("TurnDeadzone", 0.2f,
            description: "Deflection below this does not turn.");

        // F1 and F12 are the only function keys the full audit of this game
        // folder found unclaimed.
        uiHideKey = settings.CreateEntry("UiHideKey", "F1",
            description: "Hides and shows the game UI, via CanvasGroup alpha on UIRoot.");
        uiStereoKey = settings.CreateEntry("UiStereoKey", "F12",
            description: "Converts the UI root canvas to ScreenSpaceCamera so it "
                + "renders per eye instead of being blitted twice. Reversible.");
        // ON BY DEFAULT. The UI is unreadable in the headset until the stereo
        // conversion is applied, and expecting a new user to find F12 first is
        // not a shipping state. F12 remains as the manual override.
        // WHICH VERB PUTS AN OBJECT DOWN, and it is a config entry rather than a
        // constant because the answer is not derivable and every test run is
        // expensive.
        //
        // Section 74 flagged the hypothesis and this session broke it: the mod
        // called InvokeItemInteraction with a FIXED PickUp verb on the
        // assumption that the game resolves the verb per target object. It does
        // not - a second press repeats PickUp and nothing is placed, which is
        // what was reported twice.
        //
        // ItemInteraction holds None, PickUp, Use, Rotate and Remove, and
        // BaseInput carries InvokeCancelInteraction(ItemInteraction) beside
        // InvokeItemInteraction. That is three plausible ways to say "put it
        // down" and no way to tell from a signature which one the game means.
        // Cycled with Ctrl+Num1 so one run settles it instead of three.
        placeVerb = settings.CreateEntry("PlaceVerb", "request-place",
            description: "What the interact button does while carrying: cancel "
                + "(InvokeCancelInteraction(PickUp)), remove (InvokeItemInteraction(Remove)), "
                + "or use (InvokeItemInteraction(Use)). Ctrl+Num1 cycles it in the headset.");

        // Degrees per second at full stick deflection. The game's own
        // ItemRotationSpeed reads 1, and the unit of InvokeItemRotated(float) is
        // an open question from section 73 - so this is scaled here and the log
        // records what was sent against what the object did.
        itemRotateSpeed = settings.CreateEntry("ItemRotateSpeed", 90f,
            description: "Degrees per second at full deflection when rotating a carried object.");

        // ON by default, and switchable because it is the one feature here that
        // takes over inputs the player otherwise walks with. The gate is the
        // game's own AllowsPlayerMovement, so it should never engage during play -
        // but a gate that misreads would be unpleasant, and a config entry is
        // cheaper than a relaunch.
        // ONE KEY INSTEAD OF TWO, and this closes a genuine usability defect.
        //
        // Until now the player had to press F8 to bring up XR and then F2 to hand
        // the washer to the controller. Two keys for one intention, in a headset,
        // with no on-screen hint once the overlay is suppressed - the user called
        // it what it is. Section 73 had already named this entry and the reason:
        // "AutoEnableWithXR true - without it stereo appears but nothing tracks".
        //
        // Safe to do here: OnLateUpdate already returns early whenever
        // XRSettings.enabled is false, so activating in lockstep with XR cannot
        // make the mod write anything while the flat game is running. The manual
        // toggle stays on F2 for anyone who wants the flat game back without
        // shutting XR down.
        // OFF for release, and the reason is measured rather than assumed.
        //
        // A 211-second run wrote 6769 lines and 823 KB - about 31 lines every
        // second, arriving as ONE burst per second. MelonLoader writes those to
        // the console and the file synchronously on the main thread, and the
        // block that produces them also walks the washer's renderer list, reads
        // shader properties, enumerates the raycasters and recurses through the
        // reticle hierarchy looking for rotated nodes. A once-per-second burst
        // of work on the render thread is exactly the shape of a periodic
        // micro-stutter, which is what prompted this.
        //
        // The gates sit at the TOP of each report, before anything is gathered,
        // so switching this off removes the work and not just the writing.
        //
        // What stays on regardless: everything that happens once (patches, the
        // OpenXR boot sequence, resolving the player), every state change
        // (menu mode, re-binds, waiting for a controller), and every warning.
        // Those are what a bug report needs, and they cost nothing.
        // WHY THE POINTER MISSED, and it is a measurement rather than a feature.
        //
        // Reported: the item lists under Anpassung are not operable. The log
        // already said that this is NOT the scrolling - that works on this very
        // screen - and not the candidate scan either: the entries are in it,
        // CustomisationItemButton(Clone) appears as the first element of 19
        // scans. They were simply never SELECTED across two visits, and pointing
        // at the list yields "off target". So their rectangles are not where
        // they are drawn, and this report prints the numbers the containment
        // test actually decides against.
        //
        // Default ON for this measuring build: it only fires when the pointer
        // hits nothing at all, and then at most every two seconds. It belongs on
        // false once the question is answered - the item from section 87.
        menuMissReport = settings.CreateEntry("MenuMissReport", true,
            description: "When the pointer hits no element, log the three nearest "
                + "candidates with their projected rectangles. For finding out why an "
                + "element cannot be hit. Throttled to once every two seconds.");

        // HOW FAR THE GAME LOOKS FOR SOMETHING TO PICK UP, raised only when the
        // game's own value is smaller.
        //
        // Reported: a low step ladder cannot be picked up standing, nor
        // crouching - only lying down. PlayerCameraInteractionSelector raycasts
        // from the camera up to m_pickupDistance, and in VR the camera sits at
        // the player's REAL height because DriveHeadPosition writes the HMD
        // position onto HeadTurn. The straight line from there to something on
        // the floor is longer than the flat game ever has to deal with; lying
        // down shortens it, crouching barely. The taller the player, the worse -
        // which is why this is a preference and not a literal.
        //
        // 2.5 m is arithmetic, not a guess: at 1.72 m eye height and an object
        // 1.5 m away on the ground the line is about 2.3 m.
        pickupDistanceMin = settings.CreateEntry("PickupDistanceMin", 2.5f,
            description: "Metres. Raises the game's own pickup reach to at least this, so "
                + "objects on the floor stay reachable from VR standing height. The game's "
                + "value is left alone when it is already larger.");

        // AIMING FOR AN INTERACTION WITH THE WASHER instead of with the head.
        //
        // Measured: Camera.main's forward has y == 0 always, so the game's own
        // interaction ray is permanently level and never reaches the floor. The
        // gaze is unusable as an aiming aid in this mod - which fits it, since
        // gaze and aim have been decoupled since section 72.
        aimInteraction = settings.CreateEntry("AimInteraction", true,
            description: "Point the washer at an object and press X to pick it up. The "
                + "game's own gaze targeting is untouched - this only takes over while the "
                + "washer is actually pointing at something interactable.");

        aimInteractionRadius = settings.CreateEntry("AimInteractionRadius", 0.5f,
            description: "Metres of sideways distance from the beam that still counts as "
                + "pointing at an object.");

        aimInteractionRange = settings.CreateEntry("AimInteractionRange", 5f,
            description: "Metres ahead of the muzzle that are searched.");

        // WHY NO OBJECT WAS AIMED AT, printed at the moment of the press.
        //
        // The aim log alone cannot answer it, and both of its blind spots are
        // known failures in this project. It fires on a CHANGE of target, so the
        // perp values it prints are the ones that just crossed the threshold -
        // its own trigger biases the sample. And it is written only on success,
        // so a CanInteract that says false every other frame is invisible.
        //
        // Default ON for this measuring build, and deliberately WITHOUT a time
        // throttle, unlike MenuMissReport: the trigger is a button press and
        // therefore already rare, while a two-second lock would hide exactly the
        // frame-to-frame variation this is looking for. It belongs on false once
        // the question is answered - the item from section 87.
        aimMissReport = settings.CreateEntry("AimMissReport", true,
            description: "When a press of X finds no object under the washer, log the "
                + "five nearest interactables with the gate that rejected each one. For "
                + "finding out why an object cannot be aimed at. One block per press.");

        // OB DER KOPF DOPPELT GEZAEHLT WIRD, und das ist keine rhetorische
        // Frage: Pose.cs schreibt die ROHE Controller-Rotation als LOKALE
        // Rotation auf die Assembly, waehrend DriveHead dem Elternknoten die
        // volle HMD-Neigung gibt. Der Kommentar an der Schreibstelle hat die
        // fehlende Raumkonvertierung selbst als offene Hypothese notiert.
        //
        // Der Test am Laser war nicht durchfuehrbar, weil ShowWashLaser auf
        // false steht - es gab keinen Punkt zu sehen. Also misst die Mod, was
        // der Punkt gezeigt haette.
        aimChainReport = settings.CreateEntry("AimChainReport", true,
            description: "Log the head, controller and gun pitch together whenever the "
                + "head tilts by two degrees. For measuring whether the head rotation is "
                + "counted twice in the aim chain. Belongs on false once answered.");

        // THE BODY-ZONE GESTURES.
        //
        // A spatial condition plus a grip edge, nothing more: controller inside
        // the zone and grip newly pressed raises the SAME PWS2 action the stick
        // click already raises. No second input layer, no hand tracking, no
        // colliders, no inventory.
        //
        // ZONES AS A CENTRE PLUS A RADIUS. A sphere is the most tolerant shape,
        // it is four numbers per zone and it has no corner cases - "generous and
        // configurable to begin with" without six box limits to tune. The
        // defaults are a first guess for an average build and are MEANT to be
        // re-measured in the headset, which is what ZoneDebug is for.
        //
        // Coordinates are head-relative and YAW-ALIGNED: x right, y up, z
        // forward. Head pitch and roll deliberately do not enter, so looking up
        // while reaching over the shoulder cannot tilt the zone away.
        gestureZones = settings.CreateEntry("GestureZones", true,
            description: "Grab gestures: right controller behind the right shoulder plus "
                + "right grip switches the washer, at the right hip plus right grip switches "
                + "the nozzle category, and the left hand at the washer plus left grip "
                + "switches the extension. The stick clicks keep working either way.");

        zoneDebug = settings.CreateEntry("ZoneDebug", false,
            description: "Log the zone-local hand coordinates about twice a second, for "
                + "setting the zone centres and radii. Zone enter/exit and fired gestures "
                + "are logged either way. Turn off once the zones fit.");

        shoulderZoneX = settings.CreateEntry("ShoulderZoneX", 0.20f,
            description: "Metres right of the head, yaw-aligned. Centre of the shoulder zone.");
        shoulderZoneY = settings.CreateEntry("ShoulderZoneY", -0.15f,
            description: "Metres above the head. Negative is below, where a shoulder is.");
        shoulderZoneZ = settings.CreateEntry("ShoulderZoneZ", -0.20f,
            description: "Metres forward of the head. Negative is behind.");
        // 0.20 SEIT ABSCHNITT 110, herauf von 0.15: gemeldet als "es fiel mir
        // unheimlich schwer, den Schultergriff auszufuehren". 0.15 war eine
        // Handgroesse und damit knapp fuer eine Bewegung, die man blind macht -
        // hinter die eigene Schulter sieht niemand.
        //
        // DIE UEBERLAPPUNG BLEIBT AUS, und das ist der Grund, warum 0.20 die
        // Obergrenze ist: Schulter- und Hueftmittelpunkt liegen in y 0,45 m
        // auseinander, die Radien summieren sich auf 0,40. Bei 0.30 waren es
        // 0,60 gegen 0,45 - die Kugeln ueberlappten um 0,15 m, und der
        // Gleichstandsbrecher unten war tragend statt Guertel. Ab 0,225 je Zone
        // waere er das wieder.
        shoulderZoneRadius = settings.CreateEntry("ShoulderZoneRadius", 0.2f,
            description: "Metres. Reaching behind your own shoulder is a blind move, so this "
                + "is generous. Above 0.225 it starts to overlap the hip zone.");

        hipZoneX = settings.CreateEntry("HipZoneX", 0.22f,
            description: "Metres right of the head, yaw-aligned. Centre of the hip zone.");
        hipZoneY = settings.CreateEntry("HipZoneY", -0.60f,
            description: "Metres above the head. Negative is below.");
        hipZoneZ = settings.CreateEntry("HipZoneZ", 0f,
            description: "Metres forward of the head.");
        hipZoneRadius = settings.CreateEntry("HipZoneRadius", 0.2f,
            description: "Metres. A large radius makes the continuous-spray latch unreliable "
                + "near the hip, because the gesture wins the grip press.");

        // RELATIVE TO THE WASHER HAND, not to the body, because the gesture is
        // "the left hand reaches for the gun". Offset along the washer's own
        // forward axis so the zone can sit at the muzzle, where an extension is.
        washerZoneForward = settings.CreateEntry("WasherZoneForward", 0.15f,
            description: "Metres along the washer's forward axis. Shifts the zone from the "
                + "hand towards the muzzle.");
        washerZoneRadius = settings.CreateEntry("WasherZoneRadius", 0.15f,
            description: "Metres around that point.");

        gestureCooldown = settings.CreateEntry("GestureCooldown", 0.4f,
            description: "Seconds before the same gesture can fire again. Guards against a "
                + "grip that chatters; a deliberate second grab still cycles onwards.");

        // BEIDE HAENDE, WENN BEIDE AN DERSELBEN SACHE SIND - so gewuenscht,
        // fuer die Immersion.
        //
        // Die Verlaengerungs-Geste ist die einzige ZWEIHAENDIGE: die freie
        // Hand greift an die Pistole, die die andere Hand haelt. Schulter und
        // Huefte bleiben einhaendig, dort faehrt die Pistolenhand allein an
        // den Koerper - ein Puls in der freien Hand waere dort eine
        // Rueckmeldung ohne Ursache.
        //
        // EIN REGLER, DER AUCH DER SCHALTER IST: 0 laesst den zweiten Puls
        // weg. Ein bool daneben waere ein zweiter Zustand fuer dieselbe
        // Frage, und zwei Schalter fuer eine Sache haben in diesem Projekt
        // schon eine Fehldiagnose gekostet.
        //
        // 0,6 IST EIN ANFANGSWERT, KEIN MESSWERT. Der Ruck laeuft durch das
        // Geraet, duerfte also schwaecher ankommen - wie stark, sagt nur das
        // Headset. Genau das war die Lehre aus OffHandRotZ: aus einer
        // Beschreibung geschlossen -90, am Kopf gemessen +90.
        gestureEchoFactor = settings.CreateEntry("GestureEchoFactor", 0.6f,
            description: "Share of the pulse strength the OTHER hand gets when a two-handed "
                + "gesture fires - the extension grab, where the free hand reaches for the "
                + "gun that the washer hand holds. 0 leaves that second pulse out. Shoulder "
                + "and hip stay one-handed.");

        // THE DEVELOPMENT HOTKEYS, off by default - and this is the entry that
        // takes them away from the player.
        //
        // Reported: a player does not need most of these keys and can hit them
        // by accident, and some of them are expensive. The measured case is F4:
        // SourceKey had no modifier check, Unity's GetKeyDown sees the F4 in
        // Alt+F4 regardless, so EVERY Alt+F4 used to flip the pose source and
        // save it. The log of 2026-09-17 carries "Pose source: aim" 1.6 seconds
        // before the shutdown save, which is exactly how UseAimPose came to
        // alternate on every close of the game.
        //
        // A NEW ENTRY rather than ten changed defaults, and that choice is the
        // MelonPreferences lesson: a changed default does NOT reach a cfg that
        // already exists, so setting SourceKey's default to "None" would have
        // left every existing installation - this machine and every tester -
        // with its F-keys live. A new entry MelonLoader writes into every cfg
        // with the source default, so this reaches all of them at once.
        //
        // The keys stay in the code, compiled and testable, instead of being
        // commented out: a commented-out block rots silently. One entry brings
        // the whole set back.
        //
        // WHAT STAYS LIVE regardless, because it is not a development tool:
        // F2 (the way back to the flat game - AutoEnableWithXR turns aiming on,
        // this turns it off), F8 in XR Boot (the documented fallback when
        // auto-boot stands down), keypad 3 (recenter, a real VR function) and
        // keypad plus/period/enter (the nozzle).
        calibrateKey = settings.CreateEntry("CalibrateKey", "Home",
            description: "Held down, the washer stops following the hand and stands still. "
                + "Move your washer hand to where the washer should sit, then let go - the "
                + "grip offset and rotation are solved from the difference and saved. "
                + "On the controller: hold the left grip together with X and Y. Set to None "
                + "to disable the key and keep only the controller chord.");

        // DER EINE SCHALTER, DER ALLE ENTWICKLUNGSHILFEN DECKELT.
        //
        // Eine Beta soll nichts mitschleppen, was nur zum Messen da war: die
        // Live-Logs, die Dev-Tasten und die Messberichte kosten Bild fuer
        // Bild Arbeit und fuellen das Log.
        //
        // GEDECKELT, NICHT UEBERSCHRIEBEN. Naheliegend waere, die einzelnen
        // Schalter beim Start auf false zu setzen - aber MelonPreferences
        // speichert beim Spielende zurueck, und damit waeren die
        // Entwicklungswerte dauerhaft weg. Der Deckel laesst sie stehen und
        // macht sie nur unwirksam, siehe Dev().
        devMode = settings.CreateEntry("DevMode", false,
            description: "Master switch for everything that only exists for development: "
                + "the verbose per-frame logs, the F-key and keypad hotkeys, and the miss "
                + "and chain reports. While this is false those switches have no effect, "
                + "whatever they are set to, and the mod does no measuring work. Set it "
                + "to true to develop; the individual switches then apply as before. The "
                + "MelonLoader console window is separate - hide_console under [console] "
                + "in UserData/Loader.cfg.");

        devHotkeys = settings.CreateEntry("DevHotkeys", false,
            description: "Development keys: F1 UI, F3 arms, F4 pose source, F5 ray override, "
                + "F9 splash, F12 stereo UI, keypad divide/multiply/minus for the laser and "
                + "the aim mask, and the whole keypad trim set. Off by default so they cannot "
                + "be hit by accident. Keypad 3 recenter, the nozzle keys and F2 stay on "
                + "either way.");

        verboseDiagnostics = settings.CreateEntry("VerboseDiagnostics", false,
            description: "Per-second diagnostic blocks: the wash probe, the reticle series and "
                + "the pose line. Off by default because both the work and the log writes land "
                + "on the main thread once a second. Turn on when reporting a problem.");

        showSplash = settings.CreateEntry("ShowSplash", true,
            description: "Show the Wet Reality plate in the headset when VR comes up. It cannot "
                + "cover the loader's own start-up, which happens before any XR session exists - "
                + "only the black between OpenXR starting and the game's first frame.");

        splashSeconds = settings.CreateEntry("SplashSeconds", 2.5f,
            description: "Seconds the plate is held at full opacity before it fades.");

        splashFadeSeconds = settings.CreateEntry("SplashFadeSeconds", 0.75f,
            description: "Seconds the plate takes to fade out after the hold.");

        splashDistance = settings.CreateEntry("SplashDistance", 2.2f,
            description: "Metres in front of the headset.");

        splashWidth = settings.CreateEntry("SplashWidth", 1.9f,
            description: "Width of the plate in metres at that distance.");

        splashKey = settings.CreateEntry("SplashKey", "F9",
            description: "Shows the plate again, so its size and timing can be judged without "
                + "restarting the game.");

        autoEnableWithXr = settings.CreateEntry("AutoEnableWithXR", true,
            description: "Hand the washer to the controller as soon as XR is running, "
                + "so only one key is needed to get into VR.");

        uiNavigation = settings.CreateEntry("UiNavigation", true,
            description: "Left stick navigates menus and X confirms, while the game "
                + "reports that player movement is blocked. Cancel stays on the menu button.");

        // DEFAULT ON, and that is the whole point of the entry. The pointer
        // shipped in 1.3.0 behind alt+Keypad9, which section 90 recorded as the
        // reason it was delivered but unreachable: a tester in a headset does
        // not find that key and does not know of it.
        //
        // A NEW entry, so the MelonPreferences trap does not apply - MelonLoader
        // writes it into an existing cfg with this default on the next run.
        //
        // It gates only the AUTOMATIC activation. alt+Keypad9 keeps working as
        // the manual toggle either way, which is also the way back if the
        // forced input device ever misbehaves.
        menuPointer = settings.CreateEntry("MenuPointer", true,
            description: "Point at menus with the washer hand. Switches the game's own "
                + "controller cursor on the first time a menu opens, which also makes the "
                + "game show gamepad button prompts. Needs UiNavigation on. "
                + "Alt + keypad 9 toggles it by hand.");

        // SIX NEW ENTRIES, so the MelonPreferences trap does not apply to any of
        // them: MelonLoader adds a missing entry to an existing cfg with the
        // source default. The trap is about CHANGED defaults on entries that are
        // already written, so if one of these values ever needs a different
        // default the working cfg has to be changed with it.
        //
        // One switch per part of the menu work, so a tester can turn any single
        // part off without a rebuild and the log stays readable either way.
        menuRectCandidates = settings.CreateEntry("MenuRectCandidates", true,
            description: "Decide whether a menu element is on screen from its RECTANGLE "
                + "instead of its pivot. Off restores the pivot test, which drops large "
                + "tiles whose pivot sits outside the viewport.");

        menuSettingControls = settings.CreateEntry("MenuSettingControls", true,
            description: "Operate the settings screen: trigger activates buttons and "
                + "checkboxes, right stick left/right changes dropdowns and sliders.");

        menuScroll = settings.CreateEntry("MenuScroll", true,
            description: "Scroll the list under the pointer with the right stick, up and down.");

        menuScrollSpeed = settings.CreateEntry("MenuScrollSpeed", 0.8f,
            description: "Fraction of the whole list per second at full stick deflection.");

        menuAdjustSpeed = settings.CreateEntry("MenuAdjustSpeed", 0.6f,
            description: "Fraction of a slider's range per second at full stick deflection.");

        sprayLatchLockout = settings.CreateEntry("SprayLatchLockout", true,
            description: "Ignore the right grip while the trigger is held, so pulling the "
                + "trigger hard cannot latch continuous spray by squeezing the grip with it.");

        // ====================================================================
        // DER KLEBENDE DAUERSTRAHL - Abschnitt 111, und er ist GEMESSEN.
        //
        // Gemeldet: der Strahl wird ungewollt zum Dauerstrahl und laesst sich
        // dann nicht mehr abstellen; es hoere nach unbestimmter Zeit von selbst
        // auf oder nach einem Duesen- oder Pistolenwechsel.
        //
        // Das Log vom 18.09. 17:24-17:38 beantwortet alle drei Teile, und die
        // Vermutung "es liegt am lange gehaltenen Trigger" war falsch:
        //
        //   trigger down 101   trigger up 101      FireHeld ist sauber
        //   grip ignored, trigger is held   2      die Sperre oben arbeitet
        //   grip taken by a body-zone gesture 58   HIER liegt es
        //
        // DAS EINRASTEN IST EIN FEHLGRIFF AN DER HUEFTE. Sechs der sieben
        // "continuous spray ON" fallen 0,07 bis 0,47 s nach einem hipR EXIT.
        // Die Abstaende zum Hueftzentrum bei genau diesen Druecken, gegen
        // Radius 0,20:
        //
        //   0,211   0,235   0,237   0,204   0,204 m
        //
        // Zweimal VIER MILLIMETER ausserhalb. Die Hand greift zur Huefte, der
        // Griffdruck selbst zieht sie aus der Kugel, und der Druck fiel
        // stillschweigend auf die Rastung durch. Dazu flattert die Grenze:
        // hipR ENTER/EXIT wechselt alle 0,1 bis 0,3 s, waehrend die Hand
        // einfach an der Huefte haengt.
        //
        // DAS AUSRASTEN WAR UNERREICHBAR. Liegt die Rastung, trifft der
        // naechste Griffdruck die Zone, die Geste feuert, ZoneGestureSuppressed
        // frisst den Druck - und die Rastung bleibt. Der Block ab 17:38:07
        // zeigt zehn Hueftgesten in Folge, Duesengruppe klappt Soap/25 hin und
        // her, der Strahl laeuft weiter. Zwei Fenster mit festsitzendem Strahl:
        // 50,8 s und 87,9 s. Die Sitzung endete eingerastet, 7 ON gegen 6 off.
        //
        // Das "hoert von selbst auf" ist derselbe Zufall wie das Einschalten -
        // ein Griffdruck, der wieder ausserhalb der Zone landete. Deshalb war
        // es fuer den Spieler nicht nachvollziehbar.
        //
        // UND DER ZUSTAND WAR UNSICHTBAR: "SPRAY LATCHED" steht nur im
        // DevMode-Overlay, nicht im Headset. Kein Puls, kein Ton.
        //
        // Drei Teile, drei Schalter, damit ein Lauf drei Ablesungen liefert.
        sprayLatchGripClears = settings.CreateEntry("SprayLatchGripClears", true,
            description: "While continuous spray is latched, ANY grip press clears it and "
                + "nothing else happens on that press - no nozzle or washer change. That makes "
                + "the latched state always escapable, which is what it was not: a grip press "
                + "at the hip fired the gesture and the gesture ate the press.");

        sprayLatchZoneMargin = settings.CreateEntry("SprayLatchZoneMargin", 0.1f,
            description: "Metres added to the shoulder and hip radius for the latch guard only. "
                + "The gesture zones themselves are unchanged. Measured: the accidental latches "
                + "landed 4 to 36 mm outside the 0.20 m hip sphere, so this covers them with room.");

        sprayLatchZoneGrace = settings.CreateEntry("SprayLatchZoneGrace", 0.6f,
            description: "Seconds after leaving the shoulder or hip zone during which the grip "
                + "cannot latch. Measured: all six accidental latches fell within 0.47 s of a "
                + "zone exit, so 0.6 covers every one of them.");

        sprayLatchZoneGuard = settings.CreateEntry("SprayLatchZoneGuard", true,
            description: "Latch ON only when the hand is clear of the gesture zones - outside "
                + "radius plus SprayLatchZoneMargin, and SprayLatchZoneGrace seconds since the "
                + "last zone exit. Unlatching is never guarded, so a guard can never trap the jet.");

        sprayLatchBuzz = settings.CreateEntry("SprayLatchBuzz", true,
            description: "Pulse the washer hand on every latch change, so the latch can never "
                + "go on silently again. Needs Haptics.");

        autoStereoUi = settings.CreateEntry("AutoStereoUi", true,
            description: "Apply the stereo UI conversion automatically instead of waiting for F12.");

        // 0.5 as the starting point, the value to test first. Unity rescales the
        // canvas into the camera frustum regardless of distance, so planeDistance
        // cannot shrink the surface - only a scale on the child nodes can.
        // 0.45 rather than 0.5: measured in the headset. At the game's own
        // size the canvas fills well past the comfortable reading cone.
        uiScale = settings.CreateEntry("UiScale", 0.45f,
            description: "Scale applied to the UI root's CHILD nodes. 1 is the game's own size. "
                + "Alt + keypad 8/2 adjusts it, Alt + 4/6 the distance, Alt + 5 resets.");

        uiDistance = settings.CreateEntry("UiDistance", 2f,
            description: "Metres. Canvas plane distance once converted.");

        // DIE UI IM VORDERGRUND - Abschnitt 103.
        //
        // Gemeldet: nahe Geometrie verdeckt Aufgabenliste, Hauptmenue und die
        // Einblendungen. Das ist die dokumentierte Kehrseite von
        // ScreenSpaceCamera, und der Hebel ist der ZTest der UI-Materialien.
        // Default an, weil es einen gemeldeten Defekt behebt - und in beide
        // Richtungen schaltbar, ohne Neustart.
        uiAlwaysOnTop = settings.CreateEntry("UiAlwaysOnTop", true,
            description: "Draw the UI over everything, including near geometry and the washer "
                + "itself. Without it, anything closer than the UI plane covers the task list "
                + "and the menu.");

        // Der Baumdurchlauf laeuft nach der Umstellung, zwei Sekunden spaeter
        // noch einmal, und wenn ein Menue aufgeht. Dieses Intervall ist der
        // Rueckfall, falls sich zeigt, dass Elemente noch spaeter entstehen -
        // aus bleibt aus, weil ein Durchlauf ueber den UI-Baum Leistung kostet.
        uiDepthRefresh = settings.CreateEntry("UiDepthRefresh", 0f,
            description: "Seconds between UI depth passes. 0 is off and the right value: the "
                + "pass already runs on conversion, two seconds later, and whenever a menu "
                + "opens.");
        laserLength = settings.CreateEntry("LaserLength", 4f,
            description: "Metres. The line is drawn from the nozzle, not from the eye.");
        laserWidth = settings.CreateEntry("LaserWidth", 0.006f,
            description: "Metres. Thin enough to read an angle off, thick enough to film.");

        // DER ZEIGER VOR DIE GEOMETRIE - Abschnitt 104, und es behebt ein
        // Versaeumnis von 103: dort ist die UI nach vorn geholt worden, die
        // Linie, mit der man sie bedient, nicht. Sie clippte durch nahe
        // Objekte, und ein Menue ohne sichtbaren Zeiger ist schwerer bedienbar
        // als ein verdecktes Panel.
        laserAlwaysOnTop = settings.CreateEntry("LaserAlwaysOnTop", true,
            description: "Draw the pointer line over geometry, like the UI it points at. "
                + "The line has its own material, so this touches nothing of the game's.");

        // WER BEMALT DAS FENSTER - Abschnitt 104, und es ist eine Messung, kein
        // Umbau.
        //
        // Gemeldet: auf dem Monitor erscheint das Titelbild verschachtelt. Der
        // Mirror ist als Ursache WIDERLEGT - gameViewRenderMode stand schon
        // vorher auf None und die Augentextur liest 0x0, es gibt also keinen
        // Blit, den man abschalten koennte. Drei verschachtelte Bilder brauchen
        // drei Zeichner oder einen, der sich selbst als Textur liest, und
        // beides steht in den Kamera-Eigenschaften.
        //
        // Einmalig, drei Sekunden nach dem Start des Treibers, und danach auf
        // false - eine Diagnose, deren Frage beantwortet ist, zahlt keine
        // Frames mehr.
        // DAS HIGHLIGHT HALTEN - Abschnitt 105.
        //
        // Gemeldet: die grossen Kacheln blitzen beim Zeigen kurz auf und werden
        // wieder dunkel; waehlbar bleiben sie, aber niemand sieht, was gewaehlt
        // ist. Einstellungs-Elemente sind nicht betroffen.
        //
        // Der Zeiger waehlt heute NUR BEI WECHSEL - "if (best != menuIndex)" -,
        // und das ist mit Absicht so: ein Select pro Frame wuerde eine
        // Stick-Auswahl sofort ueberschreiben, der Zwei-Cursor-Konflikt aus
        // Abschnitt 81. Nimmt aber das Spiel die Auswahl von selbst zurueck,
        // dann sieht diese Sparsamkeit wie ein Flackern aus.
        //
        // Also erneuern statt dauernd waehlen: nur wenn der Zeiger auf
        // DERSELBEN Kachel steht und das Spiel IsSelected false meldet.
        // AUS, UND DAS IST DAS MESSERGEBNIS. Der Versuch, die Auswahl zu
        // erneuern, hat aus einem einmaligen Aufblitzen ein BLINKEN gemacht -
        // jeder Takt startet den Zyklus neu, weil das Spiel die Auswahl sofort
        // wieder wegnimmt. Der Schalter bleibt als Weg zurueck stehen, aber
        // niemand soll ihn brauchen.
        // AUS, UND ZWAR ENDGUELTIG - der Weg ist gemessen und geschlossen.
        //
        // Drei Anlaeufe, drei Ergebnisse: alle 150 ms hinter dem Tor -> langsames
        // Blinken; jeden Frame vor dem Tor -> SCHNELLERES Blinken. Genau das
        // schliesst die Sache: ForceSelect ist NICHT IDEMPOTENT. Jeder Aufruf
        // startet den Uebergang der Kachel von vorn, also erreicht sie den
        // hellen Zustand nie, und haeufiger aufzurufen macht es prinzipiell
        // schlimmer statt besser.
        //
        // Was daraus folgt, steht in Abschnitt 105: das Leuchten haengt an der
        // eigenen Cursor-Kette des Spiels, nicht an der Auswahl. Das EventSystem
        // haelt bereits die richtige Kachel ("SAME OBJECT"), und das dauerhaft
        // leuchtende Element in jedem Tab ist das, welches die Cursor-Kette
        // zuletzt beruehrt hat - sie bewegt sich nicht, weil niemand ihre
        // Eingabe fuellt (Abschnitt 90). Der naechste Hebel ist damit CursorInput
        // und nicht dieser Schalter.
        menuHighlightHold = settings.CreateEntry("MenuHighlightHold", false,
            description: "Re-assert the selection while the pointer rests. MEASURED DEAD END: "
                + "ForceSelect restarts the tile's transition, so re-asserting flickers - the "
                + "faster the worse. See section 105.");

        // ZEIGEN STATT WAEHLEN - Abschnitt 105.
        //
        // Was eine Maus beim Ueberfahren einer Kachel tut, ist ein
        // Pointer-Enter und kein Select. Die Einstellungs-Elemente, die laut
        // Meldung normal leuchten, bekommen genau das von der Cursor-Kette des
        // Spiels; die grossen Kacheln bekommen bisher nur unser ForceSelect,
        // das ihr Zustandsautomat zurueckzieht.
        // AUS, GEMESSEN: das Ereignis geht hinaus - die Kachel traegt
        // IPointerEnterHandler und der Aufruf wirft nicht mehr -, und das Bild
        // aendert sich trotzdem nicht. Bleibt als Weg stehen, nicht als Mittel.
        menuHoverEvents = settings.CreateEntry("MenuHoverEvents", false,
            description: "Send the game's own pointer-enter and pointer-exit to the tile under "
                + "the pointer, the way a mouse does. The selection for the trigger stays as "
                + "it is.");

        // DIE AUSWAHL DES SPIELS BEWEGEN, statt eine zweite daneben zu setzen.
        //
        // Gemessen, und es erklaert die Meldung "in jedem Tab ist eine Kachel
        // dauerhaft highlighted":
        //
        //     menu hover: ZUR BASIS ...   eventSystem holds "ImageButton_HomeBase"
        //
        // Das EventSystem haelt EIN Objekt, und dieses eine leuchtet. Unser
        // ForceSelect setzt daneben eine zweite Auswahl, die der
        // Zustandsautomat sofort zuruecknimmt - daher das Aufblitzen.
        // SetSelectedGameObject benutzt dieselbe Mechanik, die sichtbar
        // durchhaelt, statt gegen sie zu arbeiten.
        // AUS, GEMESSEN UND ZWAR EINDEUTIG: "SAME OBJECT". Das EventSystem
        // haelt bereits die Kachel unter dem Zeiger - diese Auswahl zu setzen
        // ist ein Nullaufruf, und das dauerhaft leuchtende Element in jedem Tab
        // ist etwas anderes.
        // DAS HOVER DES SPIELS BEWEGEN - Abschnitt 106, und es ist der Weg, den
        // Abschnitt 105 als "CursorInput nachbauen" benannt hat, nur ohne den
        // Nachbau.
        //
        // HandlePointerExitAndEnter ist Unitys eigene Hover-Buchfuehrung auf
        // dem Eingabemodul des Spiels. Sie laeuft die Kette ab, schickt Exit an
        // die alte und Enter an die neue Kachel - dasselbe, was eine Maus
        // ausloest. Kein Setzen eines Zustands, den ein Automat zurueckzieht,
        // sondern die Bewegung, aus der der Automat seinen Zustand selbst
        // bildet. Das ist der Unterschied zu allen drei Versuchen aus 105.
        menuGameHover = settings.CreateEntry("MenuGameHover", true,
            description: "Tell the game's own input module where the pointer is, so its hover "
                + "moves with it. This is what lights a tile; selection does not.");

        // VORGABE AN, und das ist kein Experiment mehr.
        //
        // Die Auswahl des EventSystem ist der Gamepad-Cursor des Spiels und
        // treibt Vorschaubild, Beschriftungszustand und Ring. Gemessen wurde,
        // dass das Spiel sie ~70 ms nach jedem Wechsel auf das Standardelement
        // der Seite zurueckholt - Button_Close, Button_Back. Ohne Nachsetzen
        // zeigt die Vorschau darum immer das erste Item.
        //
        // Der Schalter bleibt als Ausstieg. Steht er auf false, sagt das eine
        // Logzeile: ein alter false-Wert in einer vorhandenen cfg soll nicht
        // still wirken, denn eine geaenderte Vorgabe greift dort nicht.
        menuSetEventSelection = settings.CreateEntry("MenuSetEventSelection", true,
            description: "Keep the EventSystem's own selection on the tile under the "
                + "pointer. That selection drives the preview pane, the label state "
                + "and the ring, and the game pulls it back to the page default "
                + "about 70 ms after every change.");

        if (!menuSetEventSelection.Value)
            LoggerInstance.Msg("menu selection route: MenuSetEventSelection is OFF in the "
                + "config - preview pane, label and ring will not follow the pointer. "
                + "Set it to true in UserData/MelonPreferences.cfg.");

        // DER WAECHTER, VORGABE AUS - Abschnitt 127, und das ist ein
        // Messergebnis.
        //
        // Gebaut wurde er in 125 gegen ein Leeren der Auswahl, das 20 bis 30
        // mal pro Sekunde zuschlug. Der Kontrolllauf OHNE UnityExplorer hat
        // gezeigt, wem dieses Leeren gehoerte: UniverseLib traegt einen
        // eigenen Prefix auf genau dieser Methode
        // (EventSystemHelper.Prefix_EventSystem_SetSelectedGameObject) und
        // weist JEDE Auswahlaenderung ab - die der Mod wie die des Spiels.
        //
        // Ohne UnityExplorer leert niemand etwas: selectBlocked 0,
        // selectCalls 262/263 statt 788/13250, und der Schreibvorgang landet.
        // Ein Detour auf eine Unity-UI-Methode, der nachweislich nie feuert,
        // ist Risikoflaeche ohne Gegenwert.
        //
        // Der Schalter bleibt, weil er der Beleg ist: er laesst sich ohne
        // Build wieder anschalten, und die Beschreibung sagt, was dabei
        // gemessen wurde.
        menuKeepSelection = settings.CreateEntry("MenuKeepSelection", false,
            description: "Refuse attempts to CLEAR the menu selection while the pointer "
                + "rests on a tile. MEASURED UNNECESSARY: in a clean run the game never "
                + "clears it and this blocked nothing. It was built against UnityExplorer, "
                + "which patches this very method and rejects every selection change. See "
                + "section 127.");

        // 0 HEISST JEDEN FRAME, und das ist hier die Absicht: die Mod schreibt
        // in OnLateUpdate, das Spiel in Update - wer zuletzt schreibt, gewinnt
        // vor dem Zeichnen. Mit 150 ms verliert man dieses Rennen fuenf von
        // sechs Frames, und genau so sah es aus. Geprueft wird ohnehin nur ein
        // bool, und gesetzt nur, wenn das Spiel die Auswahl verloren hat.
        menuHighlightHoldSeconds = settings.CreateEntry("MenuHighlightHoldSeconds", 0f,
            description: "Seconds between those checks. 0 means every frame, which is what "
                + "wins the race against the game's own update.");

        surfaceProbe = settings.CreateEntry("SurfaceProbe", true,
            description: "Measurement only: dump every active camera and canvas once, three "
                + "seconds after the driver starts. For the nested title image on the monitor; "
                + "turn it off once that is understood.");

        // EINMAL SCHREIBEN, damit neue Schluessel in der Datei landen.
        //
        // Gemeldet war, dass OffHandRotX und die anderen nicht in der cfg
        // stehen. Sie existierten - im Speicher, mit ihren Vorgaben. Die Datei
        // wird aber nur bei einem Save() geschrieben, und in der gemeldeten
        // Sitzung kam "Preferences Saved" 0x vor: das Spiel wurde ohne
        // Speichern beendet. Ohne diese Zeile ist jeder neue Einstellwert erst
        // nach einer Sitzung sichtbar, die zufaellig sauber endet.
        MelonPreferences.Save();

        // Ein Buendel statt neunzehn Parameter, und die Ausgabe als zwei
        // Delegates: SprayHaptics weiss damit nichts von MelonPreferences und
        // nichts von der Bruecke zu XR Boot.
        sprayHapticSettings = new SprayHapticSettings(
            sprayHapticsOn, hapticIntensity,
            hapticJet0, hapticJet15, hapticJet25, hapticJet40,
            hapticJetSoap, hapticJetDefault,
            hapticTurboFactor, hapticTurboHz, hapticTurboDepth, hapticTurboWasher,

            hapticTurboAuto,
            hapticContactFactor, hapticContactSmooth,
            hapticRefresh, hapticBreathe, () => Dev(sprayHapticReport),
            SendSprayPulse, StopSprayPulse, ProbeSprayContact);

        GameInput.Install(HarmonyInstance, LoggerInstance, patchWashRay.Value,
            carryProbe.Value);
        GameInput.InstallSelectionGuard(HarmonyInstance, LoggerInstance,
            menuKeepSelection.Value);
        GameInput.AimSkip = aimSkip.Value;

        NativeXR.InstallResolver(typeof(Pose).Assembly,
            Path.Combine(Application.dataPath, "Plugins", "x86_64"));

        LoggerInstance.Msg($"Ready. {toggleKey.Value} toggles controller aiming. Start OpenXR first, with XR Boot.");

        // ADVERTISED ONLY WHEN THEY WORK. A startup banner naming F4 and the
        // keypad while DevHotkeys is off would send the next bug report chasing
        // a key that cannot fire - the same class of waste as a log line naming
        // the wrong mask in section 72.
        if (!Dev(devHotkeys))
        {
            LoggerInstance.Msg("Development keys are OFF (DevHotkeys). Live: "
                + $"{toggleKey.Value} controller aiming, keypad 3 recenter, "
                + "keypad plus/period/enter nozzle. Set DevHotkeys true for the rest.");
        }
        else
        {
            LoggerInstance.Msg("Development keys are ON (DevHotkeys): "
                + $"{uiHideKey.Value} UI, {armsKey.Value} arms, {sourceKey.Value} pose source, "
                + $"{rayKey.Value} ray override, {splashKey.Value} splash, "
                + $"{uiStereoKey.Value} stereo UI, plus the keypad trim set.");
            LoggerInstance.Msg($"Laser: {laserKey.Value} on/off, {laserDirectionKey.Value} cycles "
                + $"nozzle -> camera -> wash. Starts {(showLaser.Value ? "on" : "off")} "
                + $"in \"{laserDirection.Value}\" mode.");
        }
        // Counted, not assumed. The four arrays desynchronised once already and
        // the only symptom was a log line naming the wrong mask, which is the
        // exact mechanism behind the misdiagnoses of section 72. A mismatch is
        // now a loud line at startup instead of a silent mislabel.
        if (SkipNames.Length != SkipPresets.Length
            || SkipColors.Length != SkipPresets.Length
            || SkipColorNames.Length != SkipPresets.Length)
            LoggerInstance.Warning($"Skip table MISALIGNED: {SkipPresets.Length} presets, "
                + $"{SkipNames.Length} names, {SkipColors.Length} colours, "
                + $"{SkipColorNames.Length} colour names. Mask labels in this log are WRONG.");

        // The MASK is still reported when the keys are off - it is active state
        // and a bug report needs it. Only the KEY that cycles it is dropped.
        LoggerInstance.Msg("Wash aim skip: "
            + (Dev(devHotkeys)
                ? $"{aimSkipKey.Value} cycles {SkipPresets.Length} presets. "
                : "")
            + $"Now mask {aimSkip.Value} ({AimSkipName()}), laser {LaserColorName()}. "
            + "Every preset but the last keeps SetWashDirection suppressed, which IS the "
            + "decoupling - the last one is a control and brings the gaze bug back.");
    }

    // ALT MUST NOT BE HELD, and this is the fix for the reported Alt+F4.
    //
    // Windows closes a window on Alt+F4. Unity's Input.GetKeyDown(KeyCode.F4)
    // reports the F4 anyway, because it knows nothing about the chord - so every
    // Alt+F4 fired whatever was bound to F4, saved it, and only then did the
    // window go away. Two things sat there: SourceKey here, which flipped the
    // pistol between the aim and the grip pose, and GateKey in XR Boot, whose
    // own description ends "Expect the flat game to break".
    //
    // Checked for every keyed handler rather than only for F4. Alt+F6 is the
    // teardown, and any future key could land on another chord; the rule "a
    // plain hotkey does not fire while a modifier is held" is the one that does
    // not need revisiting.
    //
    // KeyCode.None parses cleanly and GetKeyDown never reports it, which is how
    // GateKey was already neutralised by hand in a working cfg. Setting a key to
    // "None" therefore keeps working exactly as before.
    private static bool AltHeld() =>
        Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

    private static bool KeyDown(MelonPreferences_Entry<string> entry) =>
        !AltHeld()
        && Enum.TryParse<KeyCode>(entry.Value, ignoreCase: true, out var key)
        && Input.GetKeyDown(key);

    // A DEVELOPMENT key: silent unless DevHotkeys says otherwise.
    private bool DevKeyDown(MelonPreferences_Entry<string> entry) =>
        Dev(devHotkeys) && KeyDown(entry);

    public override void OnUpdate()
    {
        // ARMED ON THE TRANSITION, not on the state: requesting it every frame
        // while XR runs would restart the hold forever and the plate would never
        // fade.
        if (showSplash.Value && XRSettings.enabled && !splashArmed)
        {
            splashArmed = true;
            splash.Request(LoggerInstance);
        }

        if (!XRSettings.enabled)
            splashArmed = false;

        if (DevKeyDown(splashKey))
        {
            if (splash.Visible)
                splash.Hide();
            else
                splash.Request(LoggerInstance);
        }

        // Checked before the toggle and only while running, so both arm variants
        // can be compared with the headset on rather than across two launches.
        if (active)
        {
            if (DevKeyDown(armsKey))
            {
                hideArms.Value = !hideArms.Value;
                ApplyArmMode();
                MelonPreferences.Save();
                LoggerInstance.Msg("Arms: " + ArmModeName());
            }

            if (DevKeyDown(sourceKey))
            {
                useAimPose.Value = !useAimPose.Value;
                ApplyPoseSource();
                MelonPreferences.Save();
                LoggerInstance.Msg("Pose source: " + SourceName());
            }

            if (DevKeyDown(rayKey))
            {
                overrideRay.Value = !overrideRay.Value;
                MelonPreferences.Save();
                LoggerInstance.Msg($"Wash ray override: {overrideRay.Value}");
            }

            if (DevKeyDown(laserKey))
            {
                showLaser.Value = !showLaser.Value;

                if (!showLaser.Value)
                {
                    washLaser.Hide();
                    laserStatus = $"laser off ({laserKey.Value})";
                }

                MelonPreferences.Save();
                LoggerInstance.Msg($"Wash laser: {(showLaser.Value ? "on" : "off")}");
            }

            // Cycled in session rather than set in the config, because the whole
            // value of the laser is holding the ideal line and the effective one
            // against the same wet patch without taking the headset off.
            if (DevKeyDown(laserDirectionKey))
            {
                laserDirection.Value = NextLaserDirection(laserDirection.Value);
                MelonPreferences.Save();
                LoggerInstance.Msg($"Laser direction: {laserDirection.Value}");
            }

            // The decoupling experiment. Cycling rather than four separate
            // config edits and four launches, because only one candidate may be
            // active at a time and the headset must not have to come off
            // between them. The laser's COLOUR follows this index, so the
            // selected candidate is readable in VR without the overlay - which
            // sticks to the face and cannot be read anyway.
            if (DevKeyDown(aimSkipKey))
            {
                var next = (SkipIndex() + 1) % SkipPresets.Length;
                aimSkip.Value = SkipPresets[next];
                GameInput.AimSkip = aimSkip.Value;
                MelonPreferences.Save();
                LoggerInstance.Msg($"Wash aim skip: mask {aimSkip.Value} ({AimSkipName()})  "
                    + $"laser {LaserColorName()}");
            }

            // BOTH STAY LIVE. Changing a nozzle and recentring are things a
            // player does, not tools for tuning this mod, so neither sits behind
            // DevHotkeys. Keypad 3 used to live INSIDE LiveTrim among the trim
            // handlers, which is why it had to be lifted out: gating LiveTrim
            // would otherwise have taken recentring away with it.
            NozzleKeys();
            RecenterKey();

            // EVERYTHING ELSE on the keypad is a tuning tool, and the whole set
            // writes to the config and saves. Section 90 recorded the incident
            // this prevents: "a too-early released Alt rotates the pistol by 5
            // degrees and saves that".
            if (Dev(devHotkeys))
                LiveTrim();
        }

        // Outside the active gate on purpose. The UI is unreadable in the
        // headset whether or not the pose is being driven, so switching it off
        // must not require the controller aiming to be on first.
        if (DevKeyDown(uiHideKey))
            gameUi.ToggleHidden(LoggerInstance);

        if (DevKeyDown(uiStereoKey))
            gameUi.ToggleStereoFix(LoggerInstance, uiDistance.Value);

        // ONE KEY. The toggle key still works, but it is no longer required to
        // get into VR.
        //
        // The player pressed F8 for XR and then F2 for the washer - two keys for
        // one intention, blind, inside a headset whose on-screen overlay is
        // suppressed. Now the second one happens by itself the moment XR comes
        // up, and F2 is only there to hand the flat game back without shutting
        // XR down.
        //
        // Deliberately one-directional: it switches ON with XR and does NOT
        // switch off when XR stops. A player who pressed F2 to get the flat game
        // back must not have it taken away again on the next frame, and
        // OnLateUpdate already returns early while XRSettings.enabled is false,
        // so an active mod with no XR writes nothing.
        var wantOn = !active && autoEnableWithXr.Value && XRSettings.enabled;

        // KeyDown, NOT DevKeyDown. F2 is the way back to the flat game and a
        // player needs it - AutoEnableWithXR hands the washer to the controller
        // by itself, and this is the only way to hand it back. It gets the Alt
        // guard like everything else.
        var toggled = KeyDown(toggleKey);

        if (!toggled && !wantOn)
            return;

        if (wantOn && !toggled)
        {
            active = true;
            LoggerInstance.Msg("Controller aiming enabled automatically: XR is running "
                + "(AutoEnableWithXR).");
        }
        else
        {
            active = !active;
        }

        if (!active)
        {
            // Nothing to restore. Both owners rewrite their transform every
            // frame, so the flat game recovers by itself on the next Update.
            GameInput.Active = false;
            GameInput.FireHeld = false;
            GameInput.RayActive = false;
            GameInput.RayReady = false;
            RestoreArms();
            DisposeActions();
            washLaser.Dispose(LoggerInstance);
            menuLaser.Dispose(LoggerInstance);

            // OnGUI is not gated on `active`, so without this the overlay keeps
            // announcing a live laser for the rest of the session after F2 off.
            laserStatus = "laser: mod off";
            status = "Off. The game has its transforms back.";
            LoggerInstance.Msg("Controller aiming off.");
            return;
        }

        anchor = null;
        positionAction = null;
        rotationAction = null;
        headRotationAction = null;
        headPositionAction = null;
        trackedAction = null;
        triggerAction = null;
        moveAction = null;
        turnAction = null;
        offHandTrigger = null;
        offHandPosition = null;
        offHandRotation = null;
        publishedOffHandRotation = Quaternion.identity;
        publishedWasherHandRotation = Quaternion.identity;
        washerHandWorldPublished = false;
        handAssets.Reset();
        vrHands.Reset();
        refillArmed = true;

        // Re-captured per activation like every other action, because the player
        // is rebuilt per job and a stale InputAction reads nothing.
        rightPrimary = null;
        rightSecondary = null;
        rightSqueeze = null;
        leftPrimary = null;
        leftSecondary = null;
        leftMenu = null;
        leftSqueeze = null;
        leftStickClick = null;
        rightStickClick = null;

        jumpButton.Reset();
        stanceButton.Reset();
        sprayLatchButton.Reset();
        interactButton.Reset();
        taskButton.Reset();
        menuButton.Reset();
        dirtButton.Reset();
        groupButton.Reset();
        tabPrevButton.Reset();
        tabNextButton.Reset();
        menuSubmitButton.Reset();
        menuAcceptButton.Reset();
        extensionButton.Reset();

        // Cleared on the way in, so a latch never survives a toggle-off. A gun
        // that sprays the moment the mod is switched back on would be a nasty
        // surprise.
        GameInput.FireLatched = false;
        sprintHeld = false;

        // Same reasoning as the latch above: OnLateUpdate returns early while
        // inactive, so nothing would keep writing the frozen pose and nothing
        // could release it. All four fields that carry the state, cleared
        // together - a half-cleared teardown is how a stale freeze comes back.
        calibrating = false;
        calibArmRequested = false;
        calibSolveRequested = false;
        calibHeldLast = false;

        // Same reasoning again: OnLateUpdate returns early while inactive, so a
        // zone left standing would be stale the next time it runs. Every field
        // that carries the state, cleared together.
        rightZoneGrip.Reset();
        washerGrip.Reset();
        inShoulderZone = false;
        inHipZone = false;
        inWasherZone = false;
        torsoYawValid = false;
        loggedZonePoses = false;
        zoneSuppressUntil = 0f;

        // ALLE Felder des Waechters, zusammen: ein gesetztes nearGestureZone
        // ohne die Abstaende, die es begruenden, ist genau die halb geraeumte
        // Lage, an der dieses Projekt schon gezahlt hat.
        nearGestureZone = false;
        zoneGuardUntil = 0f;
        zoneShoulderDistance = 0f;
        zoneHipDistance = 0f;
        latchClearedFrame = -1;

        // Dropped too, so the reach is re-read and re-applied on the way back in
        // rather than trusted from a player that no longer exists.
        reachSelector = IntPtr.Zero;
        interactables.Clear();
        aimTargetPointer = IntPtr.Zero;
        nextInteractableScan = 0f;

        bodyYaw = 0f;
        snapArmed = true;
        headBaseCaptured = false;
        headPositionEverWritten = false;
        recenterHeadRequested = false;

        // Die Ruhelage gehoert zum Level: nach einem Auftragswechsel ist
        // headTurn eine andere Instanz, und ihre Ruhelage ist neu zu messen.
        // Ein stehengelassener Wert waere genau die Falle aus
        // unity-null-vs-pattern-null, nur mit einem Vector statt mit einer
        // Referenz.
        headTurnRestCaptured = false;
        lastHeadPoseValid = false;
        // ZUERST die Materialien zuruecknehmen, DANN den Zustand vergessen:
        // ResetDepth braucht den aufgeloesten Wurzel-Canvas noch, und Reset
        // gibt ihn her. Die andere Reihenfolge haette einen ZTest hinterlassen,
        // den niemand mehr zuruecknehmen kann - Unitys Standard-UI-Material
        // ueberlebt den Auftragswechsel.
        // BEIDE Felder auf dasselbe Nichts: ein hoverData, das auf ein
        // zerstoertes EventSystem zeigt, und ein Modul aus dem alten Auftrag
        // sind genau die Kombination, die "is null" nicht sieht.
        cursorModule = null;

        // ZUSAMMEN MIT DEM MODUL, weil beide auf demselben GameObject sitzen:
        // stirbt eines, ist das andere auch tot. Ein Feld stehen zu lassen,
        // waehrend das Geschwisterfeld geraeumt wird, ist genau die Falle aus
        // unity-null-vs-pattern-null.
        gameEventSystem = null;
        hoverData = null;
        gameHoverTarget = IntPtr.Zero;
        loggedGameHover = false;

        gameUi.ResetDepth(LoggerInstance);
        gameUi.Reset();
        gunRender.Reset();
        playerInput = null;
        characterController = null;
        loggedBlockedStance = -1;
        loggedNoController = false;
        interaction = null;
        nextInteractionSearch = 0f;
        carrying = false;
        heldItemState = "";
        raySpawn = null;
        equipment = null;
        nextRaySearch = 0f;
        washProbe.Reset();
        sprayHaptics.Reset();
        headDeviceId = 0UL;
        armsPivot = null;
        armMeshes = null;
        nextArmRefresh = 0f;
        hiddenArmObjects.Clear();
        hiddenArmIds.Clear();
        loggedArmCount = -1;
        armReturnedCount = -1;
        gripRotation = null;
        gripPosition = null;
        aimRotation = null;
        aimPosition = null;
        assembly = null;
        assemblyRest = null;
        baseCaptured = false;
        anchorPositionWritten = false;
        nextPoseReport = 0f;
        warnedUntracked = false;
        loggedRecapture = false;
        loggedWaiting = false;
        quietRebind = false;
        nextRecapture = 0f;
        poseStale = false;
        node = Enum.TryParse<XRNode>(hand.Value, ignoreCase: true, out var parsed) ? parsed : XRNode.RightHand;

        GameInput.Active = true;
        LoggerInstance.Msg($"Controller aiming on, reading {node}.");

        // EIN PULS BEIM EINSCHALTEN, und er ist ein kostenloser
        // Funktionsnachweis: ohne ihn waere die erste Frage nach einer
        // ausgebliebenen Vibration immer "liegt es an der Geste oder an der
        // Haptik". Beide Haende, weil hier keine Hand handelt.
        //
        // NUR ANGEMELDET, nicht gesendet: XRBoots Setup laeuft gemessen 26 ms
        // spaeter, und ein Puls vor der Action ist keiner.
        hapticGreetPending = true;
        hapticGreetUntil = Time.unscaledTime + 10f;
        nextHapticGreet = 0f;

        // Drei Sekunden, weil die Ladebilder und die Kameras des Auftrags dann
        // stehen. Sofort waere die Haelfte noch nicht da.
        surfaceProbeAt = Time.unscaledTime + 3f;
        surfaceProbeDone = false;


        ProbeNativeXR();
        ListInputDevices();
        ScanXrDevices();

        // Armed even when the first capture succeeds, so a controller that wakes
        // up later still gets picked up.
        nextRecapture = Time.unscaledTime + 1f;

        if (!CreateActions())
            status = "No controller bindings. See the log for the native XR probe.";
    }

    private float nextRecapture;
    private bool loggedRecapture;
    private bool poseStale;
    private bool loggedWaiting;
    private bool quietRebind;

    // RE-BINDS the controller actions, and the first version of this got both
    // halves wrong.
    //
    // WRONG ONE: it called ScanXrDevices(). That walks the XR ulong device path
    // and is what finds the HEAD rotation. The hand actions are captured in
    // ListControls, which is called from ListInputDevices() - the Input System
    // path, where the touch_controller devices live. So the retry re-ran the
    // wrong enumeration and "bound on re-scan" never once appeared in the log.
    //
    // WRONG TWO: it did not clear the action fields. CaptureFromChild assigns
    // through "??=", so it only ever fills a field that is null. After a level
    // load the fields hold stale actions - not null - and nothing is rebound.
    // That is precisely why F2 fixed it: the activation path sets every action
    // field to null first, and only then enumerates.
    //
    // Both symptoms were the same defect seen from two sides: "controllers dead
    // after an automatic boot" and "controllers gone after loading a save".
    // Is there a hand device at all? Asked SILENTLY, and asked FIRST.
    //
    // The measured sequence after loading a save: the runtime REMOVES the
    // controller devices and takes about five seconds to register them again.
    // The device list during that window holds only keyboard, mouse,
    // touchscreen, two HID pads and HeadTrackingOpenXR - touch_controllerLeft
    // and touch_controllerRight are simply gone.
    //
    // The first version of ForceRebind disposed the old actions before finding
    // that out, so a window where the ROTATION was still delivering
    // ("position False, rotation True") was turned into one where nothing
    // delivered. Destroying something that works in order to attempt a rebuild
    // that cannot succeed is strictly worse than waiting.
    //
    // Same characteristic bits ListControls uses - 256 left, 512 right - so this
    // agrees with the code that does the actual binding instead of guessing at
    // layout names, which differ per runtime.
    // InputDeviceCharacteristics, read off the devices themselves: a
    // touch_controllerLeft reports 356 and a Right 612, so bit 256 is Left and
    // 512 is Right. The FIELD names in this class say "left" and "right" but
    // mean off-hand and reading hand, because ListInputDevices assigns them by
    // "isRight == (node == RightHand)" rather than by physical side.
    private const int LeftCharacteristic = 256;
    private const int RightCharacteristic = 512;

    private int ReadingHandBit()
        => node == XRNode.RightHand ? RightCharacteristic : LeftCharacteristic;

    private int OffHandBit()
        => node == XRNode.RightHand ? LeftCharacteristic : RightCharacteristic;

    // ASKS FOR ONE SPECIFIC HAND, and that is the fix for "the left stick
    // sometimes does nothing until F2".
    //
    // The old test was "Characteristic(device, 256) || Characteristic(device,
    // 512)" - any hand at all. So when the right controller dozed off and only
    // the left was registered, the pre-check passed, ForceRebind disposed all
    // fourteen actions, re-acquired the left ones, failed to find the reading
    // hand, reported failure, and the next tick did it again. Measured: twenty
    // discarded rebinds in five seconds, each one throwing away a left stick
    // that had just been bound and enabled.
    //
    // The comment on the waiting branch said the right thing all along -
    // "waiting is the correct behaviour here, and it keeps whatever is still
    // delivering alive". It just could not tell which hand it was waiting for.
    private bool DevicePresent(int characteristic)
    {
        for (var id = 1; id <= 64; id++)
        {
            try
            {
                var device = InputSystem.GetDeviceById(id);

                if (device is null)
                    continue;

                if (Characteristic(device, characteristic))
                    return true;
            }
            catch
            {
                // An unreadable device is not the device we are looking for.
            }
        }

        return false;
    }

    // PER HAND, not all of it.
    //
    // Cleared FIRST, because "??=" in CaptureFromChild will not overwrite a
    // stale action - but only the group whose device is actually registered, so
    // a rebind triggered by one hand can no longer destroy the other one's
    // working bindings.
    private void ForceRebind(string reason, bool readingHand, bool offHand)
    {
        var which = readingHand && offHand ? "both hands"
            : readingHand ? "reading hand" : "off hand";

        LoggerInstance.Msg($"Re-binding controller actions ({reason}, {which}).");

        if (readingHand)
        {
            // Disabling before dropping the reference keeps an orphaned enabled
            // action from lingering in the Input System.
            DisposeActions();

            trackedAction = null;
            triggerAction = null;
            turnAction = null;
            rightPrimary = null;
            rightSecondary = null;
            rightSqueeze = null;
            rightStickClick = null;
        }

        if (offHand)
        {
            moveAction = null;
            offHandTrigger = null;
            offHandPosition = null;
            offHandRotation = null;
            leftPrimary = null;
            leftSecondary = null;
            leftMenu = null;
            leftSqueeze = null;
            leftStickClick = null;
        }

        try
        {
            // The Input System path for the hands, the XR path for the head.
            // Both log their full device inventory, which is wanted once and
            // noise on every retry - five failed rebinds printed five complete
            // lists in the last run.
            ListInputDevices();

            if (!quietRebind)
                ScanXrDevices();

            quietRebind = true;
            ApplyPoseSource();
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  re-binding threw {exception.GetType().Name}: "
                + exception.Message);
        }

        var bound = positionAction is not null && rotationAction is not null;
        var offBound = moveAction is not null;

        if (readingHand)
        {
            LoggerInstance.Msg(bound
                ? $"  re-bind OK. Reading {node} from {SourceName()}."
                : "  re-bind did not find the reading hand. Retrying.");

            // Only the reading hand's outcome may clear this flag; an off-hand
            // rebind says nothing about whether the pose is delivering.
            poseStale = !bound;
        }
        else
        {
            LoggerInstance.Msg(offBound
                ? "  re-bind OK. Off-hand stick and buttons bound."
                : "  re-bind did not find the off hand. Retrying.");
        }

        // Re-armed on success, so the NEXT outage reports itself instead of
        // recovering silently. A recovery that leaves no trace is how a
        // recurring fault stays invisible.
        if (readingHand ? bound : offBound)
        {
            loggedRecapture = false;
            loggedWaiting = false;
        }
    }

    // Two conditions, because there are two ways to lose the controllers.
    //
    // Unbound actions cover the automatic boot: XRSettings.enabled turns true
    // inside display.Start() while the session is still coming up, so the first
    // enumeration runs before any hand device is registered.
    //
    // A pose that READS NULL covers the level load: the actions are still there,
    // they simply no longer resolve to a device. Nothing but reading them can
    // tell the difference, which is why the flag is set at the read site.
    private void RetryCapture()
    {
        // TWO INDEPENDENT REASONS, each with its own device to wait for.
        //
        // The old version watched only the reading hand's pose. That was enough
        // while every rebind re-acquired everything, because the off hand came
        // along for the ride - but now that a rebind touches one group at a
        // time, an off hand that wakes up later needs a reason of its own or it
        // would never be bound at all.
        var poseMissing = positionAction is null || rotationAction is null || poseStale;
        var offHandMissing = moveAction is null;

        if (!poseMissing && !offHandMissing)
            return;

        // A quarter of a second, not a whole one. The devices come back on the
        // runtime's schedule - measured at about five seconds after a save
        // load - and the only part of the delay this code controls is how
        // promptly it notices. One second of polling added up to a second of
        // dead controllers for no reason.
        if (Time.unscaledTime < nextRecapture)
            return;

        nextRecapture = Time.unscaledTime + 0.25f;

        // NOTHING IS TOUCHED for a hand that is not registered. Waiting is the
        // correct behaviour, and it keeps whatever is still delivering alive.
        var doReadingHand = poseMissing && DevicePresent(ReadingHandBit());
        var doOffHand = offHandMissing && DevicePresent(OffHandBit());

        if (!doReadingHand && !doOffHand)
        {
            if (!loggedWaiting)
            {
                loggedWaiting = true;

                var missing = poseMissing && offHandMissing ? "Neither controller is"
                    : poseMissing ? $"The {node} controller is not"
                    : "The off-hand controller is not";

                LoggerInstance.Msg($"{missing} registered - waiting. The runtime removes the "
                    + "controllers across a level load, and they doze off on their own; they "
                    + "re-register a few seconds later. Existing actions are left alone until "
                    + "then, so the other hand keeps working.");
            }

            return;
        }

        loggedWaiting = false;

        if (!loggedRecapture)
        {
            loggedRecapture = true;
            LoggerInstance.Msg("Controller actions are not delivering, and the device IS "
                + "present - re-binding.");
        }

        var why = poseMissing && positionAction is not null ? "pose reads null" : "not bound";

        ForceRebind(why, doReadingHand, doOffHand);
    }

    // Unity order is Update, then animation and Animation Rigging, then
    // LateUpdate. Both known writers of these transforms live in Update, and the
    // rigging constraints run before LateUpdate too, so this is the last word.
    public override void OnLateUpdate()
    {
        // BEFORE the active gate and before Resolve, both on purpose. The
        // plate's entire job is the window in which no player exists yet, and it
        // belongs to XR rather than to controller aiming - a user who turned
        // AutoEnableWithXR off still has a black screen to cover.
        //
        // In LateUpdate rather than Update because it is re-seated in front of
        // the camera every frame, and the camera has already moved by now.
        if (XRSettings.enabled)
        {
            splashStatus = splash.Tick(LoggerInstance, splashSeconds.Value,
                splashFadeSeconds.Value, splashDistance.Value, splashWidth.Value);
        }
        else if (splash.Visible)
        {
            splash.Hide();
        }

        if (!active)
            return;

        SampleCamera();

        // REMOVED, and worth recording why rather than just deleting.
        //
        // This block ran every frame and fed liveCamera and liveParameter, which
        // NOTHING reads - the on-screen readout they were written for is gone.
        // Each frame it marshalled a Matrix4x4 out of the interop layer, called
        // RenderParameterView (a stub that returns identity ever since section
        // 46 closed that option), and built two strings out of six
        // ToString("0.##") calls. About eight allocations a frame, so roughly
        // seven hundred a second at 90 Hz, for two values never looked at.
        //
        // Dev instrumentation that outlived its question. The fields are gone
        // with it.

        // Both early returns below skip DriveRay, and therefore skip the only
        // place that hides the laser. Left alone, the line stays enabled and
        // frozen where the gun last pointed - and a frozen line that still
        // reads "on" is exactly the trap section 58 fell into: indistinguishable
        // from a live one that happens to agree. The instrument must go dark the
        // moment it stops being driven.
        // BEFORE the Resolve gate, not after. The first version sat below it and
        // therefore did not run until a player existed - measured at six seconds
        // after activation, which is six seconds of dead controllers for no
        // reason. Binding an input device has nothing to do with whether a player
        // has spawned.
        RetryCapture();

        if (!Resolve())
        {
            washLaser.Hide();
            menuLaser.Hide();
            laserStatus = "laser: player not resolved";
            return;
        }

        if (!XRSettings.enabled)
        {
            washLaser.Hide();
            menuLaser.Hide();
            laserStatus = "laser: XR not running";
            status = "XR is not running. Start it with XR Boot first.";
            return;
        }

        // DETERMINED ONCE PER FRAME, before any reader.
        //
        // The menu has to actually TAKE the inputs, which the first version did
        // not do: it assumed the game would ignore MovementRaw while
        // AllowsPlayerMovement was false. It does not - the avatar kept walking
        // through the main menu while the stick was also moving the selection.
        // And X fired twice, once as a world interaction and once as a menu
        // submit.
        //
        // Read here rather than inside each consumer so that every one of them
        // sees the same answer in the same frame; MenuModeActive also logs its
        // transitions, and three callers would log three times.
        menuMode = uiNavigation.Value && MenuModeActive();

        // EIN MENUE BRINGT NEUE GRAPHICS MIT, und die kennen den ZTest noch
        // nicht. Beide Flanken, nicht nur die steigende: beim Schliessen
        // bleiben Elemente stehen, die beim Oeffnen entstanden sind, und ein
        // Durchlauf je Menuewechsel ist billiger als einer je Frame.
        if (menuMode != uiDepthMenuLast)
        {
            uiDepthMenuLast = menuMode;
            gameUi.TouchDepth();
        }

        // Before DriveHead, so the yaw it composes is this frame's.
        ReadTurn();
        ReadOffHandTrigger();
        // BEFORE DriveButtons, and that order is the point: the gestures borrow
        // the two grips, and the borrowed actions are evaluated inside
        // DriveButtons. The zone state and the suppression have to stand before
        // it runs.
        DriveBodyZones();
        DriveButtons();
        DriveNozzleScheme();
        ReportHeldItem();
        // AFTER ReportHeldItem, because that method owns the search for the
        // interaction manager and this one only reads the handle it resolved.
        DriveInteractionReach();
        ReportInteractionReach();
        ReportInteractionAim();
        ReportUiSelection();
        DriveMenuNavigation();
        ReportCursorFollowUp();
        DriveMenuTabs();
        // Bit 131072. Written from OnLateUpdate like the other clamps, so it is
        // the last word before rendering.
        ClampReticle((aimSkip.Value & 131072) != 0);
        ReportReticleSeries();
        DriveLookState();
        DriveHead();
        ReadTrigger();
        DriveMovement();
        DriveRay();
        // NACH DriveRay, und das ist der ganze Punkt. Hier stand der Bericht
        // dreizehn Aufrufe davor und hat damit einen Zwischenstand der
        // Duesenrotation gemessen statt den, mit dem das Spiel arbeitet -
        // dieselbe Falle, in der die Zielsuche selbst gesessen hat. Ein
        // Bericht gehoert hinter den letzten Schreiber seiner Groesse.
        ReportAimChain();
        // NACH DriveRay UND NACH ReportHeldItem, aus zwei getrennten Gruenden:
        // der veroeffentlichte Strahl ist erst hier der dieses Frames, und
        // carrying setzt ReportHeldItem. Ein Bericht gehoert hinter den
        // letzten Schreiber jeder Groesse, die er nennt.
        DriveHapticGreeting();
        ReportSurfaces();
        DriveSprayHaptics();
        ReportCarryProbe();
        // AUS DEMSELBEN GRUND HIER und nicht in ReadOffHandTrigger: die
        // Weltposition der Hand entsteht in ApplyPoseSource, und die
        // Kamerapose dieses Frames steht erst nach DriveHead. Der Trigger
        // hinterlaesst darum nur eine Anforderung, wie die Kalibriergeste es
        // auch tut.
        ReportInteractProbe();

        // AFTER DriveRay, and this position in the frame IS the fix for the
        // head coupling reported from the headset.
        //
        // It used to run beside DriveMenuNavigation, which sits before
        // DriveLookState, DriveHead and DriveRay - so it built its ray and its
        // screen projection from the PREVIOUS frame's head and gun pose.
        // Pitching the head up left the stale camera lower, so the beam drifted
        // DOWN; pitching down drifted it up. Reported in exactly those words,
        // and the same staleness put the screen-space aim off the tiles.
        //
        // The wash laser never had this defect because it is drawn from INSIDE
        // DriveRay, after the pose write. Same point in the frame, same
        // correctness - which is what "take the method from the laser" means
        // here. MuzzlePoint was only half of it; the other half is WHEN.
        DriveMenuPointer();
        // AFTER DriveMenuPointer, and for the same reason DriveMenuPointer sits
        // after DriveRay: it acts on the selection and the hit point of THIS
        // frame. Reading them before the pointer has written them would be the
        // staleness bug of section 90 all over again, one caller later.
        DriveMenuRightStick();
        // EnsureStereo when automatic, Reapply otherwise. Both are idempotent,
        // so this runs per frame; EnsureStereo also re-asserts the content scale
        // whenever the configured value changes.
        if (autoStereoUi.Value)
            gameUi.EnsureStereo(LoggerInstance, uiDistance.Value, uiScale.Value,
                uiAlwaysOnTop.Value, uiDepthRefresh.Value);
        else
            gameUi.Reapply(LoggerInstance, uiDistance.Value);

        if (positionAction is null || rotationAction is null)
            return;

        // Third pose source in three versions, and the two failures are worth
        // recording because they were not obvious:
        //
        // 0.1.0 used InputTracking.GetLocalPosition. That is the LEGACY VR input
        // path and reports an identity pose under the subsystem-based XR SDK.
        // Wrong but harmless.
        //
        // 0.2.0 used InputDevices.GetDeviceAtXRNode. That HARD CRASHED the
        // process - no managed exception, nothing a try/catch could see, the log
        // simply ended mid-line. The method returns an InputDevice struct by
        // value, and that marshalling is broken in this Cpp2IL and Il2CppInterop
        // combination, same family as the Span problem in section 36.
        //
        // So this version avoids UnityEngine.XR entirely and goes through
        // Unity's Input System, which is ordinary managed C# with no native
        // struct-returning bindings in the path. ReadValueAsObject is used
        // rather than the generic ReadValue for the same reason: no generic
        // instantiation across interop.
        var positionValue = positionAction.ReadValueAsObject();
        var rotationValue = rotationAction.ReadValueAsObject();

        if (positionValue is null || rotationValue is null)
        {
            // The trigger for the level-load case. RetryCapture cannot see this
            // from the outside: the actions are non-null and look healthy.
            poseStale = true;

            if (!warnedUntracked)
            {
                warnedUntracked = true;

                // Reported together, because the pair is the diagnosis. A value
                // for isTracked next to a null pose means the device is being
                // read and only the pose is missing; two nulls mean the device is
                // not being read at all.
                var tracked = "no isTracked action";
                try
                {
                    var trackedValue = trackedAction?.ReadValueAsObject();
                    tracked = trackedValue is null ? "isTracked NULL" : $"isTracked {trackedValue}";
                }
                catch (Exception exception)
                {
                    tracked = $"isTracked threw {exception.GetType().Name}";
                }

                LoggerInstance.Warning($"{node} pose reads null. position {positionValue is not null}, " +
                    $"rotation {rotationValue is not null}, {tracked}");
            }

            status = $"{node} has no value. Move the controller.";
            return;
        }

        // Delivering again, so the retry stands down.
        poseStale = false;
        warnedUntracked = false;

        var position = positionValue.Unbox<Vector3>();
        var rotation = rotationValue.Unbox<Quaternion>();

        // Written as a LOCAL pose without any space conversion, and that is the
        // hypothesis under test from section 44. Because the head pose lives in
        // the view matrix and the camera transform stays put, PlayerCamera is
        // effectively the tracking space origin - and EquipmentAnchor is its
        // child. If that holds, the tracking space pose already IS the local
        // pose and no conversion is needed.
        //
        // If the washer turns with the head instead of staying put in the room,
        // the hypothesis is wrong and a conversion through the camera matrix has
        // to go here.
        var offsetRotation = Quaternion.Euler(pitchOffset.Value, yawOffset.Value, rollOffset.Value);
        var local = rotation * offsetRotation;

        statusPrefix = worldSpace.Value ? "world" : "local";

        // Read back BEFORE overwriting: if the anchor no longer holds what was
        // written last frame, the game owns this transform and the missing 6DOF
        // has nothing to do with the pose value.
        if (anchor is null || anchor == null)
            return;

        var target = UseAssembly() && assembly is not null && assembly != null
            ? assembly
            : anchor!;
        var reverted = anchorPositionWritten
            && (target.localPosition - wroteAnchorPosition).sqrMagnitude > 0.000001f;
        var foundInstead = target.localPosition;

        if (recenterRequested)
        {
            baseCaptured = false;
            recenterRequested = false;
            anchorPositionWritten = false;
        }

        if (!baseCaptured)
        {
            anchorBase = anchor.localPosition;
            assemblyBase = assembly is null ? Vector3.zero : assembly.localPosition;
            if (assemblyRest.HasValue)
                assemblyBase = assemblyRest.Value;
            else
                assemblyRest = assemblyBase;
            poseBase = position;
            poseMin = position;
            poseMax = position;
            baseCaptured = true;
            LoggerInstance.Msg($"Pose reference captured. anchor rest {Vector(anchorBase)}, "
                + $"pose {Vector(poseBase)}");
        }

        poseMin = Vector3.Min(poseMin, position);
        poseMax = Vector3.Max(poseMax, position);
        var delta = position - poseBase;

        var offset = new Vector3(offsetX.Value, offsetY.Value, offsetZ.Value);
        var onAssembly = UseAssembly();

        // World space: the pose is placed relative to the HEAD and then mapped
        // into the world by the BODY YAW, so nothing the parent chain does can
        // reach it. No recentering needed - the washer lands where the hand is.
        var camera = Camera.main;
        if (worldSpace.Value && assembly is not null && camera is not null
            && TryReadHeadPose(out var hmdRotation, out var hmdPosition))
        {
            var camT = camera.transform;
            var toWorld = camT.rotation * Quaternion.Inverse(hmdRotation);

            // UNCHANGED, and deliberately. raySpawn.forward hangs off this
            // rotation through the locator chain, and raySpawn.forward IS the
            // effective wash direction that section 72 decoupled from the gaze
            // after six failed attempts. Everything 0.51.0 changes is POSITION.
            // If the effect zone ever follows the head again, this line is not
            // the suspect - the aim-skip mask or the RaycastUpdate postfix is.
            //
            // EINMAL PROBEWEISE AUF EIN REINES YAW UMGESTELLT UND WIEDER
            // ZURUECKGENOMMEN, und die Ruecknahme ist der Eintrag. Die
            // Begruendung war eine Messung, die gun pitch - ctrl pitch ==
            // -hmd pitch fand - abgegriffen aber in ReportAimChain, das VOR
            // DriveRay laeuft und damit die Rotation liest, die das Spiel
            // auf RaySpawnPoint hinterlassen hat, nicht die von DriveRay auf
            // Identitaet gezwungene. Die Zahl sagte also nichts ueber diese
            // Zeile. Die 6DOF-Handsteuerung ist hier richtig und bleibt
            // unangetastet; der Fehler lag darin, WANN die Zielsuche liest -
            // siehe AimForward.
            var gunRotation = toWorld * local;

            // The entire grip fix is WHICH ROTATION each offset gets.
            //
            // handWorld is where the tracked point actually is, mapped
            // through the camera, with the pre-existing trim inside it and
            // rotated by toWorld alone.
            //
            // grip is rotated by gunRotation instead - the full washer
            // rotation, trim included - because it is a constant in the
            // gun's own frame. With the offset inside handWorld the pivot
            // stays at a head-fixed point and the gun swings AROUND the
            // hand, which is exactly the reported symptom.
            //
            // Reduces EXACTLY to the previous expression at grip zero, which
            // is the default, so the restructuring on its own changes
            // nothing measurable.
            //
            // LIFTED OUT of the position branch, unchanged otherwise. The
            // calibration has to see these two even on a frame where the freeze
            // is writing instead of them, and handRotWorld - the hand's world
            // rotation WITHOUT the trim - is what the rotation solves against.
            var handWorld = camT.position
                + toWorld * (position - hmdPosition + offset);
            var grip = new Vector3(gripX.Value, gripY.Value, gripZ.Value);
            var handRotWorld = toWorld * rotation;

            // DIE POSE DER PISTOLENHAND, hier veroeffentlicht und nirgends
            // sonst. toWorld gilt nur in diesem Block; woanders gerechnet waere
            // es ein Fremdwert, und genau diese Falle hat die
            // Off-Hand-Rotation schon einmal gestellt (Abschnitt 102).
            //
            // Es ist bewusst handWorld und nicht die Position der gezeichneten
            // Pistole: die Hand gehoert an den Controller. Die Pistole wird per
            // Shader verzerrt dargestellt, ein normales Mesh an ihrem
            // physischen Ort war unsichtbar.
            publishedWasherHandWorld = handWorld;
            publishedWasherHandRotation = handRotWorld;
            washerHandWorldPublished = true;

            // DIE LINKE HAND, mit demselben toWorld und derselben Kopfpose wie
            // die rechte, also im selben Block gerechnet. OHNE den Trim-Offset:
            // der gehoert der Pistole, nicht der freien Hand.
            if (offHandPosition is not null
                && TryReadTrackedPoint(offHandPosition, out var offTracked))
            {
                publishedOffHandWorld = camT.position
                    + toWorld * (offTracked - hmdPosition);

                // DIE ROTATION IM SELBEN BLOCK, mit demselben toWorld - genau
                // wie handRotWorld ein paar Zeilen darueber. Woanders gerechnet
                // waere sie ein Fremdwert: toWorld gilt nur hier.
                if (TryReadRotation(offHandRotation, out var offRotation))
                    publishedOffHandRotation = toWorld * offRotation;

                offHandWorldPublished = true;
            }
            else
            {
                offHandWorldPublished = false;
            }

            // THE CALIBRATION GESTURE, resolved HERE rather than where the
            // buttons are read, so every number in the solve comes from THIS
            // frame: the same toWorld, the same head pose and the same
            // controller pose that produced the value being solved against.
            // DriveButtons runs earlier in the frame and only leaves a request.
            if (calibArmRequested)
            {
                calibArmRequested = false;

                if (!drivePosition.Value)
                {
                    LoggerInstance.Msg("calibrate: ignored, position driving is off "
                        + "(DrivePosition) - there is nothing to place.");
                }
                else
                {
                    calibRot = gunRotation;
                    calibPos = handWorld + (gunRotation * grip);
                    calibrating = true;
                    LoggerInstance.Msg("calibrate: holding the washer still. Move your "
                        + "washer hand to where it should sit, then let go.");
                }
            }

            // SNAPSHOT BEFORE THE SOLVE CLEARS IT, so the release frame still
            // writes the FROZEN pose. Writing the freshly solved pose here
            // instead would use the gunRotation computed at the top of this
            // method, which was built from the OLD offsets - a visible one-frame
            // jump. The next frame recomputes from the new offsets and should
            // land exactly on the frozen pose, which makes that frame its own
            // check on the solve.
            var freezeThisFrame = calibrating;

            if (calibSolveRequested)
            {
                calibSolveRequested = false;

                if (calibrating)
                {
                    SolveCalibration(handRotWorld, handWorld);
                    calibrating = false;
                }
            }

            if (freezeThisFrame)
            {
                assembly.rotation = calibRot;

                if (drivePosition.Value)
                {
                    wroteAnchorPosition = calibPos;
                    assembly.position = calibPos;
                    anchorPositionWritten = false;
                }
            }
            else
            {
                assembly.rotation = gunRotation;

                if (drivePosition.Value)
                {
                    wroteAnchorPosition = handWorld + (gunRotation * grip);
                    assembly.position = wroteAnchorPosition;
                    anchorPositionWritten = false;
                }
            }

            // Logged HERE as well, and this was a real blind spot: the
            // per-second pose line at the end of this method sits past this
            // return, so in the default world-space configuration the gun's
            // position was never recorded at all. The gun hanging in front of
            // the body instead of in the right hand could not be diagnosed from
            // the log for exactly that reason - the trim offset that caused it
            // was invisible.
            if (Dev(verboseDiagnostics) && Time.unscaledTime >= nextPoseReport)
            {
                nextPoseReport = Time.unscaledTime + 1f;
                LoggerInstance.Msg($"pose world  hand {Vector(position)}  "
                    + $"head {Vector(hmdPosition)}  "
                    + $"hand-head {Vector(position - hmdPosition)}  "
                    + $"offset {Vector(offset)}  "
                    + $"grip {Vector(new Vector3(gripX.Value, gripY.Value, gripZ.Value))}  "
                    + $"src {PositionSourceName()}  "
                    + $"wrote {Vector(wroteAnchorPosition)}  "
                    // THE INVARIANT, and it tests the complaint itself rather
                    // than a theory about it.
                    //
                    // toWorld maps tracking space to world space. If the head
                    // driving is self-consistent it can only be a pure YAW, and
                    // that yaw must equal bodyYaw - the artificial stick turn -
                    // because nothing else distinguishes the two spaces. It must
                    // therefore be CONSTANT while the stick is still, no matter
                    // where the head looks.
                    //
                    // If it instead picks up a pitch or roll term that moves
                    // with the head, then the 0.45 m lever arm (hand - head) is
                    // being rotated by a wandering rotation, and the washer
                    // swings while the hand is still. That is exactly the
                    // reported symptom, and no defect anywhere in the transform
                    // chain would be needed to produce it.
                    //
                    // Why this was invisible until now: errUp measures the job
                    // direction against raySpawn.forward, and both sit BELOW
                    // assembly.rotation. A toWorld error rotates the gun and the
                    // laser together and cannot show up in that number. The aim
                    // being correct never validated toWorld.
                    //
                    // camPos is logged beside it because the second invariant is
                    // a length: |wrote - camPos| must equal |hand - head|
                    // exactly at grip zero, since a rotation preserves length.
                    // A non-zero residual localises the fault to the position
                    // expression instead of the rotation.
                    // The candidate for the reticle tilting about z. The mod
                    // writes assembly.rotation including the controller's ROLL,
                    // and the crosshair plausibly derives its orientation from
                    // the wash direction - so rolling the wrist would roll the
                    // reticle. That may be correct behaviour that merely looks
                    // wrong, or it may want stabilising; either way the number
                    // has to be on record beside the report.
                    + $"gunRoll {SignedAngle(assembly.eulerAngles.z).ToString("0.#", Invariant)}  "
                    + $"toWorld {Vector(toWorld.eulerAngles)}  "
                    + $"bodyYaw {bodyYaw.ToString("0.#", Invariant)}  "
                    + $"camPos {Vector(camT.position)}  "
                    + $"lever {(position - hmdPosition).magnitude.ToString("0.###", Invariant)}  "
                    + $"gunToCam {(wroteAnchorPosition - camT.position).magnitude.ToString("0.###", Invariant)}  "
                    + $"{headPositionStatus}  {turnStatus}");
            }

            return;
        }

        if (drivePosition.Value)
        {
            if (onAssembly && assembly is not null)
            {
                wroteAnchorPosition = assemblyBase + delta + offset;
                assembly.localPosition = wroteAnchorPosition;
                anchorPositionWritten = true;
            }
            else
            {
                wroteAnchorPosition = anchorBase + delta + offset;
                anchor.localPosition = wroteAnchorPosition;
                anchorPositionWritten = true;
            }
        }

        anchor.localRotation = local;

        // Driven in both arm states now. The three-way mode gated this line, and
        // two of its three states came back reported as "control off" - which the
        // anchor write above should have made impossible. Rather than reason about
        // it a fourth time, the gate is gone and the readback below will say who
        // owns what.
        if (driveArms.Value && armsPivot is not null)
            armsPivot.localRotation = local;

        // Once a second, never per frame: a pose that never varies would otherwise
        // bury the log, which is the mistake section 45 made.
        if (Dev(verboseDiagnostics) && Time.unscaledTime >= nextPoseReport)
        {
            nextPoseReport = Time.unscaledTime + 1f;
            var span = poseMax - poseMin;
            LoggerInstance.Msg($"pose {Vector(position)}  head {HeadPositionText()}  "
                + $"span {Vector(span)}  on {TargetName()}  "
                + $"delta {Vector(delta)}  {rayStatus}  "
                + $"wrote {Vector(wroteAnchorPosition)}"
                + $"{(reverted ? $"  REVERTED, found {Vector(foundInstead)}" : "  held")}");
        }

        status = $"{node} from {SourceName()}  {statusPrefix}  span {Vector(poseMax - poseMin)}  "
            + $"delta {Vector(delta)}{(drivePosition.Value ? "" : " (pos off)")}"
            + $"{(reverted ? "  REVERTED" : "")}";
    }

    public override void OnGUI()
    {
        // Not drawn while XR is running. OnGUI has no stereo awareness at all,
        // so this box is one of the elements the user sees doubled and glued to
        // the face - and it is the one element here that is ours to remove.
        // Everything it shows also goes to the log, which is the channel this
        // project relies on anyway.
        if (XRSettings.enabled)
            return;

        // FUER SPIELER UNSICHTBAR - Abschnitt 109. Der Kommentar darueber sagt
        // schon, warum das hier kostenlos ist: alles, was diese Box zeigt,
        // steht ohnehin im Log, und das ist der Kanal, auf den sich dieses
        // Projekt ohnehin stuetzt.
        if (!devMode.Value)
            return;

        GUI.Box(new Rect(8f, 184f, 700f, 144f), GUIContent.none);
        GUI.Label(new Rect(16f, 188f, 604f, 20f),
            $"Wet Reality Pose   {toggleKey.Value} toggle   {(active ? "ON" : "off")}   "
            + $"arms {ArmModeName()} ({armsKey.Value})   source {SourceName()} ({sourceKey.Value})   "
            + $"pos {(drivePosition.Value ? "on" : "off")} (num 0)");
        GUI.Label(new Rect(16f, 208f, 684f, 20f), $"{headStatus}");
        GUI.Label(new Rect(16f, 228f, 684f, 20f),
            $"trim  pitch {pitchOffset.Value:0.#}  yaw {yawOffset.Value:0.#}  roll {rollOffset.Value:0.#}"
            + $"   offset {Vector(new Vector3(offsetX.Value, offsetY.Value, offsetZ.Value))}"
            + $"   grip {Vector(new Vector3(gripX.Value, gripY.Value, gripZ.Value))} [{PositionSourceName()}]"
            + $"   ray {Vector(new Vector3(rayOriginX.Value, rayOriginY.Value, rayOriginZ.Value))}"
            + $"{((aimSkip.Value & 8192) != 0 ? "" : " (bit 8192 clear)")}");
        GUI.Label(new Rect(16f, 248f, 684f, 20f),
            $"fire {(GameInput.FireHeld ? "HELD" : "-")}  {moveStatus}  {rayStatus}  "
            + $"patch {(GameInput.Installed ? "on" : "OFF")}  reads {GameInput.FireReads}   "
            + "num 8/2 4/6 7/9 trim (shift=pos, ctrl=grip), 5 reset, 0 pos (ctrl=src), 1 target, 3 recenter");
        GUI.Label(new Rect(16f, 268f, 684f, 20f),
            $"laser {laserDirection.Value}  {laserStatus}  {turnStatus}  {buttonStatus}  {headPositionStatus}  {gameUi.Status}  {gunRender.SwapStatus}  {gunRender.FovStatus}");
        GUI.Label(new Rect(16f, 288f, 684f, 36f), status);
    }

    // Resolved lazily and re-resolved whenever it goes stale, because the player
    // is spawned per job and destroyed between them.
    //
    // Camera.main is PlayerCamera in this game, confirmed by the hierarchy dump.
    // Going through it matters: there are TWO transforms named EquipmentAnchor,
    // one under PlayerCamera and one under Third Person Visuals, and the second
    // is what other players see. A plain name search would hit either.
    private bool Resolve()
    {
        // UNITY-NULL, not just pattern-null - and this single line was the cause
        // of "the controllers are gone after loading a save".
        //
        // "is not null" is a pattern match and sees only a null POINTER. A Unity
        // object that has been DESTROYED is not a null pointer, so after the
        // player is rebuilt for a new level this test passed, Resolve returned
        // immediately, and headTurn was never re-fetched. DriveHead then wrote
        // localRotation to a dead transform, which throws - 1318 times in one
        // run, once per frame.
        //
        // MelonLoader catches that per invocation, so nothing crashed. What it
        // did instead was worse to diagnose: OnLateUpdate aborted at DriveHead
        // every single frame, so everything after it never ran - the pose read,
        // the staleness flag, the movement write. The input devices were fine
        // and came back on their own; three versions were spent rebinding them
        // because the symptom pointed there.
        //
        // The same trap, in the same shape, was fixed in WashProbe earlier in
        // this session. It was still sitting here.
        var live = anchor is not null && anchor != null
            && armsPivot is not null && armsPivot != null
            && headTurn is not null && headTurn != null;

        if (live)
            return true;

        // Dropped explicitly before re-resolving. Leaving a destroyed reference
        // in place would let a later guard elsewhere believe it is usable.
        //
        // ALL OF THEM, and that is the fix for "the left stick does nothing
        // after loading a level, until F2".
        //
        // 0.95.0 fixed the Unity-null test above but cleared only the
        // transforms. playerInput and equipment stayed, and they are acquired
        // through "if (x is null)" and "x ??= ..." respectively - both of which
        // see a null POINTER and never a destroyed object. So after the player
        // was rebuilt for a new level, MovementRaw was written into the OLD
        // BaseInput every frame: no exception, no log line, and nothing moved.
        // Aiming kept working the whole time because the gun transform IS
        // re-resolved here, which is exactly why the symptom looked like an
        // input-binding problem and not like a stale reference.
        //
        // F2 worked because Reset() clears these two. This makes the level load
        // do what F2 did.
        anchor = null;
        armsPivot = null;
        headTurn = null;
        assembly = null;
        armMeshes = null;
        nextArmRefresh = 0f;
        hiddenArmObjects.Clear();
        hiddenArmIds.Clear();
        loggedArmCount = -1;
        armReturnedCount = -1;
        playerInput = null;
        equipment = null;

        // Sonst bliebe nach einem Levelwechsel eine zerstoerte Referenz stehen,
        // und "is null" sieht die nicht.
        characterController = null;
        loggedBlockedStance = -1;
        loggedNoController = false;

        var camera = Camera.main;
        if (camera is null)
        {
            status = "No main camera. Load a job first.";
            return false;
        }

        anchor = camera.transform.Find("EquipmentAnchor");
        armsPivot = camera.transform.Find("Rig_PlayerArmsPivot");

        assembly = anchor?.Find("PowerWasher_Assembly");

        // Der gepinnte ArmGeo-Pfad ist ENTFALLEN. Die Renderer werden unter
        // armsPivot nachgelesen, siehe RefreshArmMeshes.
        armMeshes = null;
        nextArmRefresh = 0f;

        // HeadTurn is the camera's parent and carries the game's yaw. Taken from
        // the hierarchy rather than searched by name, because there is exactly
        // one and it is where PlayerCameraController.m_horizontalLook points -
        // verified in section 44.
        headTurn = camera.transform.parent;

        if (anchor is null)
        {
            status = "EquipmentAnchor not found under the main camera.";
            return false;
        }

        if (armsPivot is null && driveArms.Value)
            LoggerInstance.Warning("Rig_PlayerArmsPivot not found. Driving the anchor only; the arms will not follow.");

        // VOR der Meldung, nicht danach. 1.22.0 hat "arm meshes 0" gemeldet,
        // weil die Zeile formatiert wurde, bevor RefreshArmMeshes lief - ein
        // Bericht vor dem Schreiber liest einen Fremdwert.
        RefreshArmMeshes();

        LoggerInstance.Msg($"Resolved anchor under \"{camera.name}\", arms pivot {(armsPivot is null ? "missing" : "found")}, " +
            $"arm meshes {(armMeshes is null ? 0 : armMeshes.Length)}, "
            + $"assembly {(assembly is null ? "MISSING" : "found")}.");
        ApplyArmMode();
        LogSkinnedBones();
        return true;
    }

    // Every device the Input System adds, removes or reconfigures, with its
    // layout. If an XR device appears here under a layout other than
    // XRController, the binding path simply has the wrong name and that is a
    // one-line fix. If nothing appears at all, the devices never reach the Input
    // System and the problem is a layer deeper.
    // Head look, the thing that decides whether this feels like VR at all.
    //
    // The split follows what section 35 measured of the game's own look system:
    // HeadTurn carries yaw only, PlayerCamera carries pitch only, and in both
    // measurements the other axes stayed exactly zero. Writing the HMD rotation
    // the same way keeps the mod inside the structure the game already uses,
    // instead of inventing a second one.
    //
    // Rotation only for now. The HMD position is relative to the tracking origin
    // and the camera already sits at a local (0, 1.62, 0) standing height;
    // combining those needs care and is worthless until looking around works.
    private void DriveHead()
    {
        // Unity-null here as well, as defence in depth. Resolve now re-fetches a
        // destroyed transform, but a write to one throws rather than failing
        // quietly, and a throw here takes the rest of OnLateUpdate with it.
        if (headTurn is null || headTurn == null || anchor is null || anchor == null)
            return;

        // The control object itself, captured while enumerating the device.
        //
        // No binding path is involved, and that is the point. Five guessed paths
        // failed earlier because they all named <XRHMD>, a layout this device
        // does not have - its generated layout is XRInputV1::OpenXR::HeadTrackingOpenXR.
        // Holding the InputControl sidesteps the naming question entirely.
        //
        // Reading it goes through ReadValueAsObject: a class in, a boxed value
        // out, then unboxed locally. The typed alternatives are generic, and
        // generics across interop are the family that crashed twice today. The
        // ulong feature API is out too - TryGetFeatureValue_Quaternionf threw
        // MissingMethodException because its output parameter is a struct byref,
        // while the bool variant worked precisely because bool is primitive.
        if (headRotationAction is null)
        {
            headStatus = "head: no rotation control";
            return;
        }

        Quaternion rotation;

        try
        {
            var value = headRotationAction.ReadValueAsObject();
            if (value is null)
            {
                headStatus = $"head: {headFeature} returned null";
                return;
            }

            rotation = value.Unbox<Quaternion>();
        }
        catch (Exception exception)
        {
            headRotationAction = null;
            headStatus = $"head: read threw {exception.GetType().Name}, disabled";
            return;
        }

        var euler = rotation.eulerAngles;

        // FESTGEHALTEN FUER DEN KETTENBERICHT, und, falls er die Doppelzaehlung
        // bestaetigt, fuer den Fix selbst. Die Assembly haengt unter
        // anchor.parent, das weiter unten die volle HMD-Neigung bekommt,
        // waehrend ihre eigene lokale Rotation die rohe Controller-Pose ist.
        // Herausrechnen laesst sich der Kopf nur, wenn der Pose-Schreiber ihn
        // kennt - und der laeuft in einer anderen Methode.
        headRotation = rotation;
        headEuler = euler;

        // The split mirrors what section 35 measured of the game's own look
        // system: HeadTurn carries yaw only, PlayerCamera pitch only, and the
        // other axes stayed exactly zero in every measurement. Staying inside
        // that structure beats inventing a second one beside it.
        var yawOnly = Quaternion.Euler(0f, euler.y, 0f);

        // HeadTurn carries the artificial body yaw AND the head yaw. They are
        // rotations about the same axis, so they commute and add, and folding
        // them into this one write keeps HeadTurn at a single writer per frame.
        headTurn.localRotation = Quaternion.Euler(0f, bodyYaw + euler.y, 0f);
        // The camera takes exactly what HeadTurn's yaw did not. Cancelling the
        // yaw QUATERNION rather than re-composing Euler components is what fixes
        // the residual drift of 0.27.0 - Euler(0,y,0) * Euler(x,0,z) is not the
        // pose it was taken from once the head is turned and tilted at once.
        //
        // Strictly LOCAL, unlike 0.28.0. Cancelling the parent's WORLD rotation
        // there discarded the player character's facing and left view and
        // movement diverging by the root yaw.
        DriveHeadPosition();

        // Cancels the HMD yaw ONLY, deliberately not bodyYaw. Cancelling the
        // body yaw here too would subtract it again one level down and nothing
        // would turn - the same shape as section 57 regression 1. The product
        // HeadTurn * PlayerCamera then comes out as bodyYaw * full HMD rotation.
        anchor.parent.localRotation = Quaternion.Inverse(yawOnly) * rotation;

        headStatus = $"head {headFeature} yaw {euler.y.ToString("0.#", Invariant)} " +
            $"pitch {euler.x.ToString("0.#", Invariant)}";
    }

    // Enumerates a device's controls with their real names and paths.
    //
    // InputControlPath.TryFindControls returns an Il2CppReferenceArray of
    // InputControl, which is an array of CLASS references - the safe shape.
    // InputControl.children returns ReadOnlyArray<InputControl>, a generic
    // struct by value, which is the shape that crashed the process twice today.
    // The distinction matters more than the convenience.
    //
    // "**" matches at any depth, which is wanted here: a pose control has its
    // position and rotation as children, and those child names are exactly what
    // a binding path needs.
    private void ListControls(UnityEngine.InputSystem.InputDevice device)
    {
        // The single most informative line available, and the reason for this
        // whole version.
        //
        // Controls come either from a registered layout or, when no layout
        // matches, from the features inside the device descriptor - see
        // OpenXRDevice.cs:22 in the package, XRDeviceDescriptor.FromJson of
        // description.capabilities. No layout was ever registered here, so this
        // JSON is what the Input System had to work with.
        //
        // A feature list with poses means the device HAS features and only the
        // layout was missing. An empty one means the provider hands out none,
        // and then no layout would help either. That is the fork the whole plan
        // hangs on, and it is a string on a class, so reading it is harmless.
        try
        {
            var capabilities = device.description.capabilities ?? "";
            LoggerInstance.Msg($"        capabilities, {capabilities.Length} chars:");
            LoggerInstance.Msg($"          {(capabilities.Length > 2000 ? capabilities[..2000] + " ...TRUNCATED" : capabilities)}");
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"        capabilities unreadable: {exception.GetType().Name}: {exception.Message}");
        }

        // Probed by name before the wildcard, and that order is deliberate.
        //
        // The capabilities JSON proves the device carries DeviceRotation,
        // CenterEyeRotation and the rest as real features. Yet "**" returned a
        // single control, the device itself - which now looks like my wildcard
        // was simply wrong rather than the device being empty.
        //
        // TryGetChildControl returns InputControl, a CLASS, so this is safe. And
        // a control that answers here hands over its own path, which is exactly
        // what an InputAction needs - no more guessing binding paths.
        // Which device this is decides what gets captured from it. The
        // characteristics live in the capabilities JSON as a bit field; the head
        // reported 33, meaning HeadMounted plus TrackedDevice. A right-hand
        // controller would report HeldInHand, TrackedDevice, Controller and
        // Right, so bit 512 is the one that matters.
        var isHead = Characteristic(device, 1);
        var isRight = Characteristic(device, 512);
        var isLeft = Characteristic(device, 256);
        LoggerInstance.Msg($"        head {isHead}, right {isRight}, left {isLeft}");

        // Hand controls, captured the same way the head worked out: read the
        // child control, take ITS path. This replaces the <XRController>{RightHand}
        // guesswork that never matched anything, for the simple reason that no
        // controller device existed at the time.
        //
        // Candidate ORDER is the whole fix here, and it cost a measurement to
        // find. Both spellings exist on this device, and only one of them is
        // ever written to:
        //
        //   devicepose/rotation   built from the device descriptor's feature
        //                         list, which reads "devicepose/rotation" -
        //                         a slash in a feature name becomes a control
        //                         HIERARCHY, not a flattened name. The package
        //                         says so itself at OpenXRInput.cs:394,
        //                         "/EyeTrackingOpenXR/pose/isTracked --> action
        //                         is pose".
        //
        //   deviceRotation        INHERITED from the XRController base layout,
        //                         picked because the descriptor's characteristics
        //                         say Controller|HeldInHand. It exists, it is a
        //                         Quaternion, and nothing ever fills it.
        //
        // Every earlier version probed deviceRotation first, bound that dead
        // control, and read null forever - which is exactly what sections 50 and
        // 51 chased through the XR layer, the descriptors and the provider
        // bridge. All three were fine. See section 52.
        //
        // The head escapes this entirely because its descriptor features are
        // flat - "DeviceRotation", "CenterEyeRotation" - so its generated
        // controls and the inherited XRHMD ones coincide. That is also what
        // kVirtualControlMap at OpenXRInput.cs:122 is for: mapping a flat
        // control name back onto the action it came from.
        if (!isHead && (isRight || isLeft))
        {
            var wanted = isRight == (node == XRNode.RightHand);
            LoggerInstance.Msg($"        {(wanted ? "WANTED hand" : "other hand")}, reading {node}");

            // Movement lives on the OTHER hand's stick, matching the game's own
            // gamepad layout: left stick walks, right hand aims.
            if (!wanted && moveAction is null)
                moveAction = CaptureFromChild(device, "movement stick",
                    new[] { "thumbstick", "joystick", "touchpad" });

            // The LEFT trigger, for nozzle rotation and detergent refill.
            //
            // The flat game puts that pair on leftShoulder, and Touch has no
            // shoulder, so the left trigger is the nearest free left-hand
            // equivalent - and it is the most-pressed of the must-haves, so it
            // earns a trigger rather than a face button. Same capture pattern as
            // the movement stick: taken from the hand that is NOT holding the
            // washer, so the right trigger stays purely the spray.
            if (!wanted && offHandTrigger is null)
                offHandTrigger = CaptureFromChild(device, "off-hand trigger",
                    new[] { "trigger", "triggerButton" });

            // DIE MENUETASTE HAENGT AN DER PHYSISCH LINKEN HAND, nicht an der
            // Off-Hand - Abschnitt 108, und im Linkshaenderbetrieb war das ein
            // echter Defekt.
            //
            // Alle anderen "left*"-Bindungen sind Off-Hand-Bindungen und
            // tauschen mit der Haendigkeit mit; das ist richtig, denn X, Y,
            // Griff und Stick gibt es an beiden Controllern. /input/menu/click
            // gibt es NICHT: XRInput haelt fest, dass die Taste nur links
            // existiert, weil das Gegenstueck /input/system/click dem Runtime
            // gehoert. An die Off-Hand gekoppelt hatte ein Linkshaender also
            // gar keine Menuetaste - genau so gemeldet.
            if (!isRight)
                leftMenu ??= CaptureFromChild(device, "left menu",
                    new[] { "menu_button", "menuButton", "menu" });

            if (!wanted)
            {
                leftPrimary ??= CaptureFromChild(device, "left X",
                    new[] { "primary_button", "primaryButton" });
                leftSecondary ??= CaptureFromChild(device, "left Y",
                    new[] { "secondary_button", "secondaryButton" });
                leftSqueeze ??= CaptureFromChild(device, "left squeeze",
                    new[] { "squeeze", "grip" });
                leftStickClick ??= CaptureFromChild(device, "left stick click",
                    new[] { "thumbstick_click", "thumbstickClicked", "thumbstickClick" });

                // THE OFF HAND'S POSITION, and it has never been bound before.
                //
                // Only the washer hand gets a pose below, under if (wanted) -
                // which is all this mod needed until the body-zone gestures. The
                // gesture that has the left hand reach for the washer needs to
                // know where the left hand IS, and this is the whole of it: one
                // capture in the loop that already enumerates this device. No
                // second tracking layer, no new OpenXR path.
                //
                // THE CANDIDATE ORDER IS NOT NEGOTIABLE, for the reason spelled
                // out above the washer hand's own capture: devicePosition exists
                // as an inherited XRController control and is NEVER written,
                // while devicepose/position is the one built from the device
                // descriptor's feature list. Probing the inherited name first is
                // exactly what sections 50 to 52 chased through the XR layer for
                // two sessions before section 52 found it.
                //
                // Rotation is deliberately NOT captured: the washer-zone test
                // only needs the off hand's POINT, and an unused binding would
                // be a reader's puzzle.
                offHandPosition ??= CaptureFromChild(device, "off-hand position",
                    new[] { "devicepose/position", "devicePosition" });

                // Dieselbe Form wie gripRotation am Pistolenarm. In diesem Lauf
                // nur erhoben und gemeldet - eingehaengt wird im naechsten.
                offHandRotation ??= CaptureFromChild(device, "off-hand rotation",
                    new[] { "devicepose/rotation", "deviceRotation" });
            }
            else
            {
                rightPrimary ??= CaptureFromChild(device, "right A",
                    new[] { "primary_button", "primaryButton" });
                rightSecondary ??= CaptureFromChild(device, "right B",
                    new[] { "secondary_button", "secondaryButton" });
                rightSqueeze ??= CaptureFromChild(device, "right squeeze",
                    new[] { "squeeze", "grip" });
                rightStickClick ??= CaptureFromChild(device, "right stick click",
                    new[] { "thumbstick_click", "thumbstickClicked", "thumbstickClick" });
            }

            if (wanted)
            {
                gripRotation = CaptureFromChild(device, "grip rotation",
                    new[] { "devicepose/rotation", "deviceRotation" });
                gripPosition = CaptureFromChild(device, "grip position",
                    new[] { "devicepose/position", "devicePosition" });
                aimRotation = CaptureFromChild(device, "aim rotation",
                    new[] { "pointer/rotation", "pointerRotation" });
                aimPosition = CaptureFromChild(device, "aim position",
                    new[] { "pointer/position", "pointerPosition" });
                ApplyPoseSource();

                // The second half of the discriminator. isTracked is a button
                // control, so it reads through the very same InputAction and
                // ReadValueAsObject path as the pose does. If this one delivers a
                // value while the pose stays null, the device is being updated
                // and only the pose is missing. If it is null too, nothing about
                // this device is being read at all - and then the problem is that
                // the input subsystem is not being pumped, not the binding.
                // primary_button first: under simple_controller it is the one bound
                // to /input/select/click, which is the trigger on Touch hardware.
                // The dedicated trigger action has no component to bind on that
                // profile and reports inactive.
                // "trigger" FIRST from 0.26.0 on. With the Touch profile bound,
                // primary_button is the A button, not the trigger - it only stood
                // in for it while simple_controller mapped it to select/click.
                triggerAction = CaptureFromChild(device, "trigger button",
                    new[] { "trigger", "primary_button", "triggerButton" });

                trackedAction = CaptureFromChild(device, "hand isTracked",
                    new[] { "devicepose/isTracked", "pointer/isTracked", "isTracked" });

                // The RIGHT stick, same hand as the pose. Design document
                // section 13: the HMD takes over what the right stick did in the
                // flat game, so its X axis is free for comfort turning. The
                // OpenXR action is already registered for both hands and XR
                // Boot's own report logs it ACTIVE on the right device - this is
                // only the Input System half. Resolved off the control rather
                // than from a typed path, like every other action here.
                turnAction = CaptureFromChild(device, "turn stick",
                    new[] { "thumbstick", "joystick", "touchpad" });
            }
        }

        // Ordered by preference. centerEyeRotation is the one Unity's own
        // TrackedPoseDriver binds for head tracking, per the package docs
        // (Documentation~/input.md:159-163), and deviceRotation is documented
        // there as identical for the HMD. Whichever answers first is kept.
        if (!isHead)
            return;

        // Captured for measurement only. A one-to-one mapping needs the hand
        // relative to the HEAD, because the tracking origin has no relation to
        // EquipmentAnchor's parent space - which is exactly why 0.23.0 works on a
        // delta rather than on an absolute position.
        foreach (var candidate in new[] { "centerEyePosition", "devicePosition" })
        {
            try
            {
                var child = ResolveControl(device, candidate);
                if (child is null || headPositionAction is not null)
                    continue;

                headPositionAction = new InputAction("WetRealityHeadPosition",
                    InputActionType.Value, child.path, null, null, null);
                headPositionAction.Enable();

                LoggerInstance.Msg($"        POSITION  \"{candidate}\"  ->  {child.path}   [{child.layout}]");
            }
            catch (Exception exception)
            {
                LoggerInstance.Msg($"        head position \"{candidate}\" threw {exception.GetType().Name}");
            }
        }

        foreach (var candidate in new[] { "centerEyeRotation", "deviceRotation" })
        {
            try
            {
                var child = ResolveControl(device, candidate);
                if (child is null)
                    continue;

                LoggerInstance.Msg($"        ROTATION  \"{candidate}\"  ->  {child.path}   [{child.layout}]");

                if (headRotationAction is not null)
                    continue;

                // The control hands over its own path, so the binding is read
                // rather than guessed. That distinction is the whole fix: the
                // five paths tried earlier all named <XRHMD>, a layout this
                // device does not have - its generated layout is
                // XRInputV1::OpenXR::HeadTrackingOpenXR.
                //
                // An InputAction from a path plus ReadValueAsObject is a route
                // that already works in this build. AddBinding(action, control)
                // would be more direct but returns a struct by value, and that
                // is the shape to avoid here.
                headRotationAction = new InputAction("WetRealityHead", InputActionType.Value,
                    child.path, null, null, null);
                headRotationAction.Enable();
                headFeature = candidate;

                LoggerInstance.Msg($"        bound head rotation  ->  " +
                    $"{headRotationAction.activeControl?.path ?? "NO CONTROL"}");
            }
            catch (Exception exception)
            {
                LoggerInstance.Msg($"        child \"{candidate}\" threw {exception.GetType().Name}");
            }
        }

        try
        {
            var controls = InputControlPath.TryFindControls(device, "*", 0);
            if (controls is null || controls.Length == 0)
            {
                LoggerInstance.Warning("        no direct children via wildcard.");
                return;
            }

            LoggerInstance.Msg($"        {controls.Length} control(s):");
            for (var index = 0; index < controls.Length && index < 40; index++)
            {
                var control = controls[index];
                if (control is null)
                    continue;

                LoggerInstance.Msg($"          {control.path}   [{control.layout}]");
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"        controls unreadable: {exception.GetType().Name}: {exception.Message}");
        }
    }

    // Names a reported profile id by converting each known profile string and
    // comparing ids. Avoids needing PathToString, and keeps every value a
    // primitive.
    private static readonly string[] KnownProfiles =
    {
        "/interaction_profiles/oculus/touch_controller",
        "/interaction_profiles/meta/touch_controller_plus",
        "/interaction_profiles/facebook/touch_controller_pro",
        "/interaction_profiles/khr/simple_controller",
        "/interaction_profiles/valve/index_controller",
        "/interaction_profiles/htc/vive_controller",
        "/interaction_profiles/microsoft/motion_controller",
        "/interaction_profiles/ext/hand_interaction_ext",
    };

    private static string ProfileName(ulong id)
    {
        foreach (var candidate in KnownProfiles)
        {
            if (NativeXR.StringToPath(candidate, out var candidateId) && candidateId == id)
                return candidate;
        }

        return "UNKNOWN profile";
    }

    // Reads one bit out of the characteristics field in the capabilities JSON.
    //
    // Parsed by string rather than through XRDeviceDescriptor.FromJson, because
    // that helper hands back a struct and structs crossing interop are the
    // family to avoid here. A bit test on one integer does not justify the risk.
    private bool Characteristic(UnityEngine.InputSystem.InputDevice device, int bit)
    {
        try
        {
            var json = device.description.capabilities ?? "";
            const string key = "\"characteristics\":";
            var start = json.IndexOf(key, StringComparison.Ordinal);
            if (start < 0)
                return false;

            start += key.Length;
            var end = start;
            while (end < json.Length && char.IsDigit(json[end]))
                end++;

            return int.TryParse(json[start..end], out var value) && (value & bit) != 0;
        }
        catch (Exception exception)
        {
            LoggerInstance.Msg($"        characteristics unparsable: {exception.GetType().Name}");
            return false;
        }
    }

    // Resolves a control by name or by a slash-separated path, walking the
    // hierarchy one segment at a time.
    //
    // Whether TryGetChildControl accepts a path with slashes was never measured -
    // "devicepose/isTracked" sat second in the old candidate list and the first
    // entry always matched before it was reached. Walking the segments ourselves
    // makes the question moot, and costs a loop.
    //
    // Every step goes through TryGetChildControl, which returns a class, so this
    // stays on the safe side of the interop rules from section 46.
    private static UnityEngine.InputSystem.InputControl? ResolveControl(
        UnityEngine.InputSystem.InputDevice device, string path)
    {
        if (!path.Contains('/'))
            return device.TryGetChildControl(path);

        UnityEngine.InputSystem.InputControl? current = device;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0)
                continue;

            current = current?.TryGetChildControl(segment);
            if (current is null)
                return null;
        }

        return current;
    }

    // Finds the first named child control on a device and binds an action to the
    // path that control reports. The generalisation of what finally worked for
    // the head: never name a layout, never invent a path.
    private InputAction? CaptureFromChild(UnityEngine.InputSystem.InputDevice device,
        string label, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                var child = device.TryGetChildControl(candidate);
                if (child is null)
                    continue;

                var action = new InputAction($"WetReality_{label}", InputActionType.Value,
                    child.path, null, null, null);
                action.Enable();

                LoggerInstance.Msg($"        {label} <- \"{candidate}\"  path {child.path}  [{child.layout}]");
                return action;
            }
            catch (Exception exception)
            {
                LoggerInstance.Msg($"        {label} \"{candidate}\" threw {exception.GetType().Name}");
            }
        }

        LoggerInstance.Warning($"        {label}: none of {string.Join(", ", candidates)} exist on this device.");
        return null;
    }

    // The safe read path, and the point of this version.
    //
    // UnityEngine.XR.InputDevices carries a complete static API that takes the
    // device as a plain ulong: primitives and strings in, result through a byref
    // parameter, bool or primitive out. No struct returned by value, no List, no
    // Span - which is exactly the shape that survives in this build. It bypasses
    // the Input System, layouts, and the whole family of calls that hard-crashed
    // the process twice today.
    //
    // InputDevice itself is not needed at all: the struct holds nothing but
    // m_DeviceId and m_Initialized.
    private void ScanXrDevices()
    {
        LoggerInstance.Msg("  XR devices via the ulong path:");
        var found = 0;

        for (var id = 1UL; id <= 64UL; id++)
        {
            bool valid;

            try
            {
                valid = InputDevices.IsDeviceValid(id);
            }
            catch (Exception exception)
            {
                LoggerInstance.Msg($"    id {id,-3} IsDeviceValid threw {exception.GetType().Name}");
                continue;
            }

            if (!valid)
                continue;

            found++;

            try
            {
                LoggerInstance.Msg($"    id {id,-3} \"{InputDevices.GetDeviceName(id)}\"  " +
                    $"characteristics {InputDevices.GetDeviceCharacteristics(id)}");
            }
            catch (Exception exception)
            {
                LoggerInstance.Msg($"    id {id,-3} valid, name unreadable, {exception.GetType().Name}");
            }

            ProbeFeatures(id);
        }

        if (found == 0)
            LoggerInstance.Warning("    none valid in 1..64. The XR layer reports no devices.");
    }

    // Candidate feature names rather than enumeration.
    //
    // TryGetFeatureUsages takes a List<InputFeatureUsage> and therefore belongs
    // to the dangerous family. A bool return per candidate name is cheap and
    // safe, so the names get knocked on one at a time.
    //
    // The first group is Unity's standard XR feature set. The second is the
    // action names this project registered itself in XR Boot 0.10.0 - action
    // names become feature names after sanitising, see OpenXRInput.cs:292.
    private static readonly string[] RotationFeatures =
    {
        "DeviceRotation", "CenterEyeRotation",
    };

    private static readonly string[] PositionFeatures =
    {
        "DevicePosition", "CenterEyePosition",
    };

    // The bool variant is the only one of this family that works in this build -
    // its output parameter is primitive, while the Quaternion and Vector3
    // variants throw MissingMethodException on a struct byref. That makes
    // isTracked the one thing readable here, and it is exactly the discriminator
    // the controller problem needs:
    //
    //   true  -> the runtime tracks the hand pose, data exists, and the fault is
    //            in the Input System read path
    //   false -> no pose data arrives at all, and the fault is upstream
    //
    // Feature names come from the device's own capabilities JSON, which after
    // the rename lists devicepose/isTracked and pointer/isTracked verbatim.
    private static readonly string[] BoolFeatures =
    {
        "IsTracked", "UserPresence",
        "devicepose/isTracked", "pointer/isTracked",
    };

    private void ProbeFeatures(ulong id)
    {
        // Two alternative readers for the same value, tried because the obvious
        // one is unavailable.
        //
        // Established by measurement: the OpenXR side is complete. devicepose
        // and pointer report ACTIVE for both hands, so the runtime has resolved
        // the bindings and is delivering poses, and the XR layer confirms it with
        // IsTracked true. What fails is the Input System, which never pulls any
        // value out of our hand devices - not even a button.
        //
        // So the pose has to come from the XR layer instead, and there the only
        // blocker was TryGetFeatureValue_Quaternionf throwing
        // MissingMethodException on every device, head included. That is almost
        // certainly IL2CPP stripping rather than marshalling: PWS2 never calls
        // that overload, so it was removed from the build.
        //
        // These two are different methods and may have survived:
        //   AtTime - same shape plus a timestamp, a separate entry point
        //   Custom - takes an Il2CppStructArray of bytes, a reference array, so
        //            no struct crosses the boundary at all; the four floats of a
        //            quaternion would have to be decoded by hand
        foreach (var feature in new[] { "devicepose/rotation", "DeviceRotation" })
        {
            Step($"AtTime_Quaternionf {feature}");

            try
            {
                var value = Quaternion.identity;
                if (InputDevices.TryGetFeatureValueAtTime_Quaternionf(id, feature, DateTime.Now.Ticks, out value))
                {
                    var euler = value.eulerAngles;
                    LoggerInstance.Msg($"        HIT AtTime  {feature,-20} euler ({euler.x.ToString("0.#", Invariant)}, " +
                        $"{euler.y.ToString("0.#", Invariant)}, {euler.z.ToString("0.#", Invariant)})");
                }
                else
                {
                    LoggerInstance.Msg($"        AtTime {feature}: returned false");
                }
            }
            catch (Exception exception)
            {
                LoggerInstance.Msg($"        AtTime {feature} threw {exception.GetType().Name}");
            }
        }

        foreach (var feature in new[] { "devicepose/rotation", "devicepose/position" })
        {
            Step($"Custom {feature}");

            try
            {
                var buffer = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(16);
                if (InputDevices.TryGetFeatureValue_Custom(id, feature, buffer))
                {
                    LoggerInstance.Msg($"        HIT Custom  {feature,-20} " +
                        $"{BitConverter.ToSingle(buffer, 0).ToString("0.###", Invariant)}, " +
                        $"{BitConverter.ToSingle(buffer, 4).ToString("0.###", Invariant)}, " +
                        $"{BitConverter.ToSingle(buffer, 8).ToString("0.###", Invariant)}, " +
                        $"{BitConverter.ToSingle(buffer, 12).ToString("0.###", Invariant)}");
                }
                else
                {
                    LoggerInstance.Msg($"        Custom {feature}: returned false");
                }
            }
            catch (Exception exception)
            {
                LoggerInstance.Msg($"        Custom {feature} threw {exception.GetType().Name}");
            }
        }

        foreach (var feature in RotationFeatures)
        {
            // Step before the call, not after. A hard crash leaves no stack
            // trace, so the last log line has to name the call that caused it -
            // that is how every crash today was pinned down.
            Step($"Quaternionf {feature}");

            try
            {
                var value = Quaternion.identity;
                if (InputDevices.TryGetFeatureValue_Quaternionf(id, feature, out value))
                {
                    var euler = value.eulerAngles;
                    LoggerInstance.Msg($"        HIT  {feature,-18} euler ({euler.x.ToString("0.#", Invariant)}, " +
                        $"{euler.y.ToString("0.#", Invariant)}, {euler.z.ToString("0.#", Invariant)})");

                    // Remembered so DriveHead can use it without searching again.
                    if (headDeviceId == 0UL)
                    {
                        headDeviceId = id;
                        headFeature = feature;
                    }
                }
            }
            catch (Exception exception)
            {
                LoggerInstance.Msg($"        {feature} threw {exception.GetType().Name}");
            }
        }

        foreach (var feature in PositionFeatures)
        {
            Step($"Vector3f {feature}");

            try
            {
                var value = Vector3.zero;
                if (InputDevices.TryGetFeatureValue_Vector3f(id, feature, out value))
                    LoggerInstance.Msg($"        HIT  {feature,-18} {Vector(value)}");
            }
            catch (Exception exception)
            {
                LoggerInstance.Msg($"        {feature} threw {exception.GetType().Name}");
            }
        }

        foreach (var feature in BoolFeatures)
        {
            Step($"bool {feature}");

            try
            {
                var value = false;
                if (InputDevices.TryGetFeatureValue_bool(id, feature, out value))
                    LoggerInstance.Msg($"        HIT  {feature,-18} {value}");
            }
            catch (Exception exception)
            {
                LoggerInstance.Msg($"        {feature} threw {exception.GetType().Name}");
            }
        }
    }

    // Lists every device the Input System currently knows, with its layout.
    //
    // The input layer from XR Boot 0.10.0 now attaches its action sets
    // successfully, yet the bindings still report NO CONTROL. So the question is
    // whether the Input System ever learns about the devices, and under which
    // layout name. If an XR device shows up as something other than
    // XRController, the binding path is simply misspelled and that is a one-line
    // fix. If nothing XR-shaped appears, the devices never cross over and the
    // problem is a layer deeper.
    //
    // Done by scanning ids rather than reading InputSystem.devices, which hands
    // back a ReadOnlyArray<T>. Generic structs returned by value across interop
    // are what hard-crashed the process twice today, sections 45 and 46.
    // GetDeviceById returns InputDevice, a class, so nothing risky crosses the
    // boundary. onDeviceChange would need an interop delegate conversion, which
    // is more machinery for the same answer.
    //
    // Fully qualified: InputDevice exists in both UnityEngine.InputSystem and
    // UnityEngine.XR, and both namespaces are in use here.
    private void ListInputDevices()
    {
        LoggerInstance.Msg("  Input System devices:");
        var found = 0;

        for (var id = 1; id <= 64; id++)
        {
            UnityEngine.InputSystem.InputDevice? device;

            try
            {
                device = InputSystem.GetDeviceById(id);
            }
            catch (Exception exception)
            {
                LoggerInstance.Msg($"    id {id,-3} unreadable, {exception.GetType().Name}");
                continue;
            }

            if (device is null)
                continue;

            found++;
            try
            {
                LoggerInstance.Msg($"    id {id,-3} \"{device.name}\"  layout \"{device.layout}\"  added {device.added}");

                // Only the XR devices get their controls listed. All five
                // guessed head paths failed, so the names have to be read rather
                // than invented - which is the same lesson as the P/Invoke
                // signatures in section 47.
                if (device.layout.Contains("XRInputV1", StringComparison.Ordinal))
                    ListControls(device);
            }
            catch (Exception exception)
            {
                LoggerInstance.Msg($"    id {id,-3} present, details unreadable, {exception.GetType().Name}");
            }
        }

        if (found == 0)
            LoggerInstance.Warning("    none. The Input System knows no devices at all, not even keyboard or mouse.");
    }

    // Input System actions bound to the controller, created fresh on every
    // activation so a changed Hand setting takes effect without a restart.
    //
    // The binding path is resolved by the Input System against whatever XR
    // devices it currently knows. activeControl is therefore the single most
    // useful diagnostic: if it is null, the binding matched no device, and the
    // problem is the device list rather than the pose.

    private bool CreateActions()
    {
        // The head action is not created here. It is built in ListControls from
        // the path the control itself reports, which runs earlier in the same
        // keypress. Guessing paths here produced five misses in a row.
        LoggerInstance.Msg(headRotationAction is null
            ? "  HEAD ROTATION UNAVAILABLE. No rotation control was found on any XR device."
            : $"  head rotation bound to \"{headFeature}\". Head look will be driven.");

        // The hand actions are not created here either, for the same reason.
        //
        // Until now this method built them from <XRController>{RightHand}/... and
        // reported NO CONTROL every single time. The path was never the problem:
        // both device lists, the Input System one and the XR-layer one, contained
        // nothing but the HMD. A binding cannot match a device that does not
        // exist.
        //
        // They are captured in ListControls now, from a real hand device if one
        // shows up. Whether one shows up is the open question, and the most
        // likely reason it did not is simply that the controllers were asleep -
        // that was never verified in any test.
        var bound = positionAction is not null && rotationAction is not null;

        LoggerInstance.Msg(bound
            ? $"  hand pose bound for {node}. The washer will follow."
            : $"  HAND POSE UNAVAILABLE for {node}. No controller device with pose controls was found.");

        return bound;
    }

    // Work package A from design document section 45: find out why head motion
    // does not let you look around, even though the image is stereo.
    //
    // The head pose, if it reaches Unity at all, is inside the stereo view
    // matrices. Unlike the input APIs these are safe across interop: every
    // matrix getter has an _Injected variant taking ref Matrix4x4, so nothing is
    // returned as a struct by value and nothing goes through Span marshalling.
    //
    // Two readings decide the question:
    //
    //   projectionMatrixMode   Explicit means the game sets the projection
    //                          itself and thereby overwrites what the display
    //                          subsystem provides per eye.
    //   the view matrix        Its third row is the camera forward direction. If
    //                          that changes while the head turns, the pose does
    //                          reach Unity and something downstream discards it.
    //                          If it stays put, the pose never arrives and what
    //                          moves on screen is VDXR reprojecting a static
    //                          image.
    private void ProbeCamera(Camera camera)
    {
        try
        {
            LoggerInstance.Msg($"  camera \"{camera.name}\", stereoEnabled {camera.stereoEnabled}, " +
                $"projectionMatrixMode {camera.projectionMatrixMode}");
            LoggerInstance.Msg($"  fov {camera.fieldOfView.ToString("0.##", Invariant)}, " +
                $"stereoTargetEye {camera.stereoTargetEye}, stereoSeparation {camera.stereoSeparation.ToString("0.###", Invariant)}");

            LogForward("worldToCamera ", camera.worldToCameraMatrix);
            LogForward("stereo view L ", camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left));
            LogForward("stereo view R ", camera.GetStereoViewMatrix(Camera.StereoscopicEye.Right));

            probeUntil = Time.realtimeSinceStartup + 6f;
            nextProbe = 0f;
            LoggerInstance.Msg("  sampling the view matrix for 6 s. TURN YOUR HEAD now.");
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  camera probe failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    // Row two of a view matrix is the camera forward in world space. Read from
    // the struct fields directly rather than through Matrix4x4 helpers, so no
    // il2cpp call happens on a value that is already in managed hands.
    private void LogForward(string label, Matrix4x4 matrix)
    {
        LoggerInstance.Msg($"    {label} forward ({matrix.m20.ToString("0.###", Invariant)}, " +
            $"{matrix.m21.ToString("0.###", Invariant)}, {matrix.m22.ToString("0.###", Invariant)})" +
            $"  translation ({matrix.m03.ToString("0.###", Invariant)}, " +
            $"{matrix.m13.ToString("0.###", Invariant)}, {matrix.m23.ToString("0.###", Invariant)})");
    }

    private void SampleCamera()
    {
        if (probeUntil < 0f || Time.realtimeSinceStartup < nextProbe)
            return;

        if (Time.realtimeSinceStartup > probeUntil)
        {
            probeUntil = -1f;
            LoggerInstance.Msg("  sampling done.");
            return;
        }

        nextProbe = Time.realtimeSinceStartup + 1f;

        var camera = Camera.main;
        if (camera is null)
            return;

        LogForward("camera stereo L", camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left));
        LogForward("renderparam  L ", RenderParameterView(camera));
    }

    // Option O3 from design document section 46, the reconnaissance step.
    //
    // O2 is closed: reading the display subsystem's render passes crashes the
    // process, at the wrapper and at the _Injected static alike. This path uses
    // no il2cpp interop at all, only P/Invoke over primitives, so it cannot fail
    // that way.
    //
    // What this run has to settle is a single question. xrLocateSpace needs an
    // XrTime in the session's time domain, and there is no way to invent one.
    // On Windows the only practical source is
    // XR_KHR_win32_convert_performance_counter. If the plugin enabled it, O3
    // works and the head pose is three calls away. If not, O3 needs a different
    // time source and O1 becomes the shorter road after all.
    private void ProbeNativeXR()
    {
        LoggerInstance.Msg("  native OpenXR probe:");

        try
        {
            // Each call on its own line and logged immediately, so a crash names
            // the exact import that caused it rather than the whole block.
            Step("GetRuntimeName");
            LoggerInstance.Msg(NativeXR.GetRuntimeName(out var namePointer)
                ? $"    runtime       \"{NativeXR.ReadAnsi(namePointer)}\""
                : "    runtime       NOT AVAILABLE");

            Step("GetRuntimeVersion");
            LoggerInstance.Msg(NativeXR.GetRuntimeVersion(out var major, out var minor, out var patch)
                ? $"    version       {major}.{minor}.{patch}"
                : "    version       NOT AVAILABLE");

            Step("IsSessionFocused");
            LoggerInstance.Msg($"    sessionFocused {NativeXR.IsSessionFocused()}");

            Step("GetSession");

            LoggerInstance.Msg(NativeXR.GetSession(out var session)
                ? $"    XrSession     0x{session:x}"
                : "    XrSession     NOT AVAILABLE");

            Step("GetAppSpace");
            LoggerInstance.Msg(NativeXR.GetAppSpace(out var appSpace)
                ? $"    XrSpace app   0x{appSpace:x}"
                : "    XrSpace app   NOT AVAILABLE");

            Step("GetProcAddressPtr");
            var procAddress = NativeXR.GetProcAddressPtr(true);
            LoggerInstance.Msg($"    xrGetInstanceProcAddr 0x{procAddress.ToInt64():x}");

            // Asked here rather than at boot, and that is the whole point of
            // repeating it. XR Boot asks in the same frame as AttachActionSets
            // and gets NONE for both hands, which says nothing: the profile only
            // becomes current once xrSyncActions has run and the runtime has
            // announced an InteractionProfileChanged event. F2 is seconds later.
            //
            // If a profile is bound here, the earlier NONE was a timing artefact
            // and the hand devices should exist. If it is still NONE, the runtime
            // genuinely offers no controller to this application, and the search
            // moves outside the mod - to what Virtual Desktop hands over.
            Step("GetCurrentInteractionProfile");
            foreach (var hand in new[] { "/user/hand/left", "/user/hand/right" })
            {
                if (!NativeXR.StringToPath(hand, out var handPath))
                {
                    LoggerInstance.Warning($"    profile {hand}: StringToPath refused");
                    continue;
                }

                if (!NativeXR.GetCurrentInteractionProfile(handPath, out var current) || current == 0UL)
                {
                    LoggerInstance.Warning($"    profile {hand}: STILL NONE bound");
                    continue;
                }

                LoggerInstance.Msg($"    profile {hand}: 0x{current:x}  {ProfileName(current)}");
            }

            Step("IsExtensionEnabled");

            // The deciding line. The others are context.
            foreach (var extension in new[]
            {
                "XR_KHR_win32_convert_performance_counter",
                "XR_KHR_composition_layer_depth",
                "XR_EXT_hand_tracking",
                "XR_KHR_visibility_mask",
            })
            {
                LoggerInstance.Msg($"    {(NativeXR.IsExtensionEnabled(extension) ? "ENABLED " : "disabled")}  {extension}");
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Error($"    native probe failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    // Option O2 from design document section 46, now closed. Kept because the
    // camera readings themselves stay useful, but the render parameter call is
    // disabled: it crashes the process, see section 46.
    //
    // Section 46 measured that Camera.GetStereoViewMatrix carries no head
    // rotation: both eye matrices equal the plain camera matrix apart from a
    // fixed 62 mm IPD shift. The open question is whether the DISPLAY SUBSYSTEM
    // knows better - its render parameters come from xrLocateViews, and Unity
    // might simply not be applying them to the camera.
    //
    // If the matrix returned here reacts to head motion, the pose is available
    // and the head problem is solved without rebuilding the input layer. If it
    // is identical to the camera matrix, Unity copies one into the other and O2
    // is dead, leaving O1 and O3.
    //
    // Safe across interop, and that was checked before writing it: every call
    // in this path takes its result as a ref parameter - GetRenderPass(int, ref
    // XRRenderPass) and GetRenderParameter(Camera, int, ref XRRenderParameter).
    // Nothing returns a struct by value, which is what killed version 0.2.0.
    private Matrix4x4 RenderParameterView(Camera camera)
    {
        // Option O2, closed. The body is gone on purpose rather than left
        // unreachable: dead code that warns on every build is worse than a
        // reference to where the finding is written down.
        //
        // What was tried and what happened is in design document section 46.
        // Short version: reading the display subsystem render passes kills the
        // process inside Internal_TryGetRenderPass, at the instance wrapper and
        // at the _Injected static alike, pinned down by step logging both times.
        // The head pose came from the Input System in the end - see ListControls.
        return Matrix4x4.identity;
    }

    // Found through SubsystemManager's backing list rather than through XR Boot,
    // so this mod stays independent of it. The list is a plain static property,
    // which avoids the generic enumeration that interop handles badly.
    private XRDisplaySubsystem? DisplaySubsystem()
    {
        if (displaySubsystem is not null)
            return displaySubsystem;

        try
        {
            var subsystems = SubsystemManager.s_IntegratedSubsystems;
            if (subsystems is null)
                return null;

            for (var index = 0; index < subsystems.Count; index++)
            {
                var candidate = subsystems[index]?.TryCast<XRDisplaySubsystem>();
                if (candidate is null)
                    continue;

                displaySubsystem = candidate;
                LoggerInstance.Msg($"  found XRDisplaySubsystem, running {candidate.running}, " +
                    $"{candidate.GetRenderPassCount()} render pass(es)");
                return displaySubsystem;
            }

            LoggerInstance.Warning("  no XRDisplaySubsystem among the integrated subsystems.");
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  subsystem lookup failed: {exception.GetType().Name}: {exception.Message}");
        }

        return null;
    }

    // A hard crash leaves no stack trace and no exception, so the only way to
    // learn where it happened is to have said so beforehand. Logged once per
    // step name, because this runs every frame.
    private void Step(string name)
    {
        if (!reachedSteps.Add(name))
            return;

        LoggerInstance.Msg($"  step  {name}");
    }

    private readonly HashSet<string> reachedSteps = new(StringComparer.Ordinal);

    private static string Forward(Matrix4x4 matrix) =>
        $"({matrix.m20.ToString("0.##", Invariant)}, {matrix.m21.ToString("0.##", Invariant)}, {matrix.m22.ToString("0.##", Invariant)})";

    private void DisposeActions()
    {
        try
        {
            positionAction?.Disable();
            rotationAction?.Disable();
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  disabling actions threw: {exception.GetType().Name}");
        }

        positionAction = null;
        rotationAction = null;
        gripRotation = null;
        gripPosition = null;
        aimRotation = null;
        aimPosition = null;
    }

    // Unity reports eulers as 0..360; a roll of -3 degrees reads as 357 and looks
    // like a large value. Wrapped so the log shows the small signed number the
    // eye expects.
    private static float SignedAngle(float degrees) =>
        degrees > 180f ? degrees - 360f : degrees;

    private static string Vector(Vector3 value) =>
        $"({value.x.ToString("0.###", Invariant)}, {value.y.ToString("0.###", Invariant)}, {value.z.ToString("0.###", Invariant)})";

    private string ArmModeName() => HideBodyGeometry ? "hidden" : "visible";

    // DIE VORGABEHAENDE DES SPIELS SIND IMMER AUS. Ausdrueckliche Anweisung:
    // sichtbar sind nur die gefundenen VR-Haende, nie das Armrig und nie die
    // spiel-eigene Hand unter der Pistole.
    //
    // Deshalb ist das hier KEIN Tor mehr, an dem eine Checkbox haengt. Waere es
    // eines, koennte ein Spieler das Rig zurueckholen - ein Mesh ueber beide
    // Arme, am Kopf verankert, das in VR nicht taugt (Abschnitt 53).
    //
    // HideArms bleibt als Entwicklerausstieg, aber nur unter DevMode: dieser
    // Build liefert DevMode = false aus, ein Spieler kommt also nicht daran.
    // Zum Nachsehen im Rig bleibt der Weg damit offen.
    private bool HideBodyGeometry => !(devMode.Value && !hideArms.Value);

    // Die Renderer unterhalb des Armpivots, auf 0,5 s nachgelesen. Ohne
    // Zeitgeber waere dies wieder eine einmalige Referenz, und ein Outfit-Wechsel
    // mitten im Level wuerde sie still entwerten.
    private bool RefreshArmMeshes()
    {
        if (armsPivot is null || armsPivot == null)
        {
            armMeshes = null;
            return false;
        }

        if (armMeshes is not null && Time.unscaledTime < nextArmRefresh)
            return false;

        nextArmRefresh = Time.unscaledTime + 0.5f;
        armMeshes = armsPivot.GetComponentsInChildren<Renderer>(true);
        return true;
    }

    // Hiding is what VR mods do with a first-person arm rig they cannot
    // retarget. Reversible, and reversed on the way out.
    //
    // DAS GAMEOBJECT, NICHT DER RENDERER, und das ist die Ruecknahme einer
    // Aenderung, die in 1.22.0 ungefragt mitgenommen wurde.
    //
    // Begruendet war sie damit, dass das Rig Animation und IK traegt und ein
    // deaktiviertes GameObject mehr mitnimmt als die Sichtbarkeit. Das war eine
    // Vermutung, und der Lauf hat sie widerlegt: HideArms stand auf true, drei
    // Renderer wurden geschrieben, keine Ausnahme - und die Arme blieben
    // sichtbar. Also schreibt etwas enabled wieder auf true. Ein deaktiviertes
    // GameObject ist dagegen immun, und genau deshalb hat die Vorfassung
    // funktioniert.
    //
    // Geschrieben wird auf JEDEN gefundenen Renderer, nicht nur auf ArmGeo.
    // ArmGeo deckt im Hauptspiel schon alle drei, weil fps_arms und fps_gloves
    // seine Kinder sind - aber ein DLC, das Geometrie NEBEN ArmGeo haengt, waere
    // damit nicht erfasst.
    //
    // Die Erkennung darunter bleibt unangetastet. Sie ist belegt: im DLC fand
    // sie CHAR_FPS_HUT_Visual, wo der gepinnte Pfad aus Abschnitt 113 ins Leere
    // lief.
    private void ApplyArmMode()
    {
        try
        {
            var refreshed = RefreshArmMeshes();

            if (!HideBodyGeometry)
            {
                RestoreArms();
                return;
            }

            if (armMeshes is null)
                return;

            for (var index = 0; index < armMeshes.Length; index++)
            {
                var renderer = armMeshes[index];

                if (renderer is null || renderer == null)
                    continue;

                var target = renderer.gameObject;

                // activeSelf, nicht activeInHierarchy: nach dem Abschalten von
                // ArmGeo sind seine Kinder inaktiv-durch-Eltern, tragen aber
                // weiter activeSelf true - und genau die sollen auch einzeln
                // abgeschaltet werden.
                if (target is null || target == null || !target.activeSelf)
                    continue;

                target.SetActive(false);

                var id = target.GetInstanceID();

                if (hiddenArmIds.Contains(id))
                    continue;

                hiddenArmIds.Add(id);
                hiddenArmObjects.Add(target);
            }

            if (refreshed)
                ReportArmReturns();

            // EINMAL PRO BESTAND, nicht pro Frame. Beim naechsten DLC steht hier
            // ohne Ratespiel, wie viel Koerpergeometrie am Rig hing und wo - die
            // Lehre aus Abschnitt 112: eine Zahl kann nicht sagen, WELCHE.
            if (hiddenArmObjects.Count == loggedArmCount)
                return;

            loggedArmCount = hiddenArmObjects.Count;
            LoggerInstance.Msg($"  arm geometry: {hiddenArmObjects.Count} node(s) deactivated"
                + $" under {(armsPivot is null ? "?" : armsPivot.name)}"
                + $"   ({(armMeshes is null ? 0 : armMeshes.Length)} renderer(s) under the pivot)");

            for (var index = 0; index < hiddenArmObjects.Count && index < 5; index++)
            {
                var target = hiddenArmObjects[index];

                if (target is null || target == null)
                    continue;

                LoggerInstance.Msg($"    arm[{index}] {PathOf(target.transform)}");
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning("  arm mode threw "
                + $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    // WER HAELT DEN ZUSTAND. Gelesen wird genau das, was geschrieben wurde -
    // activeSelf der selbst abgeschalteten Knoten. Kommt einer wieder, steht es
    // als Warnung im Log statt im Kopfhoerer des Testers.
    //
    // Die Zeile ist in BEIDE Richtungen eine Messung: bleibt alles abgeschaltet
    // und es sind trotzdem Arme zu sehen, dann ist die sichtbare Geometrie NICHT
    // dieser Bestand - und das waere ein anderer und wichtigerer Befund als der
    // vermutete Halter.
    private void ReportArmReturns()
    {
        var returned = 0;
        var first = "";

        for (var index = 0; index < hiddenArmObjects.Count; index++)
        {
            var target = hiddenArmObjects[index];

            if (target is null || target == null || !target.activeSelf)
                continue;

            returned++;

            if (first.Length == 0)
                first = PathOf(target.transform);
        }

        if (returned == armReturnedCount)
            return;

        armReturnedCount = returned;

        if (returned == 0)
        {
            LoggerInstance.Msg("  arm geometry: hold verified, every node stayed off");
            return;
        }

        LoggerInstance.Warning($"  arm geometry: {returned} node(s) CAME BACK"
            + $" - something re-activates them. first {first}");
    }

    private void RestoreArms()
    {
        for (var index = 0; index < hiddenArmObjects.Count; index++)
        {
            var target = hiddenArmObjects[index];

            if (target is null || target == null)
                continue;

            target.SetActive(true);
        }

        hiddenArmObjects.Clear();
        hiddenArmIds.Clear();
        loggedArmCount = -1;
        armReturnedCount = -1;
    }

    // TEIL C: die Werte, die entscheiden, ob Haende an die Controller gehen
    // koennen. NUR GELESEN, keine Verhaltensaenderung.
    //
    // Der Grund, warum das gemessen und nicht geschlossen wird: ein
    // SkinnedMeshRenderer folgt seinen KNOCHEN, nicht seinem Elternknoten. Eine
    // Kopie an einen Controller zu haengen bewegt nichts, solange die Knochen im
    // Armrig am Kopf haengen. Und debug_hand, der einzige freistehende
    // Einzelhand-Knoten, hat KEINE Kindknoten und liest in jedem Log "?" als
    // Shader - beides Zeichen, dass er nichts zeichnet. Belegt ist keines davon.
    private void LogSkinnedBones()
    {
        try
        {
            LogSkinnedUnder("arms pivot", armsPivot);
            LogSkinnedUnder("assembly", assembly);
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning("  bones probe threw "
                + $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private void LogSkinnedUnder(string label, Transform? root)
    {
        if (root is null || root == null)
            return;

        var all = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);

        if (all is null || all.Length == 0)
        {
            LoggerInstance.Msg($"  bones under {label}: no skinned renderer(s)");
            return;
        }

        LoggerInstance.Msg($"  bones under {label}: {all.Length} skinned renderer(s)");

        for (var index = 0; index < all.Length && index < 6; index++)
        {
            var skin = all[index];

            if (skin is null || skin == null)
                continue;

            var bones = skin.bones;
            var boneCount = bones is null ? 0 : bones.Length;
            var materials = skin.sharedMaterials;
            var materialCount = materials is null ? 0 : materials.Length;
            var rootBone = skin.rootBone;

            // DER MATERIALNAME, nicht nur die Platzzahl. Der Szenen-Instanz von
            // debug_hand fehlt das Material - liegt hier ein brauchbares, ist
            // der Weg ueber die vorhandene Hand in der Szene offen, ohne
            // Addressables.
            var material = "none";

            if (materials is not null && materialCount > 0
                && materials[0] is not null && materials[0] != null)
                material = materials[0].name + " / "
                    + (materials[0].shader is null || materials[0].shader == null
                        ? "null shader"
                        : materials[0].shader.name);

            LoggerInstance.Msg($"    {skin.name}   bones {boneCount}"
                + $"   materials {materialCount} \"{material}\""
                + $"   rootBone {(rootBone is null || rootBone == null ? "NONE" : PathOf(rootBone))}");

            for (var bone = 0; bone < boneCount && bone < 2; bone++)
            {
                var node = bones![bone];

                if (node is null || node == null)
                    continue;

                LoggerInstance.Msg($"      bone[{bone}] {PathOf(node)}");
            }

            var parent = skin.transform.parent;
            var animator = parent is null || parent == null
                ? null
                : parent.GetComponent<Animator>();

            if (animator is null || animator == null)
                continue;

            var controller = animator.runtimeAnimatorController;

            LoggerInstance.Msg($"      parent animator on {parent!.name}:"
                + $" enabled {animator.enabled}   runtime controller "
                + $"{(controller is null || controller == null ? "NONE" : controller.name)}");
        }
    }

    // IsPressed is a parameterless bool, which is the safest call shape this
    // build offers. ReadValue<T> is generic and ReadValueAsObject needs an
    // unbox whose target type would have to be assumed - neither is worth it
    // for a button.
    private void ReadTrigger()
    {
        if (triggerAction is null)
        {
            GameInput.FireHeld = false;
            return;
        }

        try
        {
            var held = triggerAction.IsPressed();

            if (held != GameInput.FireHeld)
                LoggerInstance.Msg($"trigger {(held ? "down" : "up")}");

            GameInput.FireHeld = held;
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  trigger read threw {exception.GetType().Name}; giving up on it.");
            triggerAction = null;
            GameInput.FireHeld = false;
        }
    }

    // The trigger as it is RIGHT NOW, for the spray latch lockout. Separate from
    // ReadTrigger because DriveButtons runs first and GameInput.FireHeld is
    // therefore a frame behind at that point.
    private bool TriggerHeldNow()
    {
        if (triggerAction is null)
            return false;

        try
        {
            return triggerAction.IsPressed();
        }
        catch
        {
            return false;
        }
    }

    // Vector2 in through a setter, and the previous frame's value read back out.
    // One measurement, two distinguishable failures: a stick that reads nothing
    // leaves the value at zero, a game that owns the property shows a mismatch.
    private void DriveMovement()
    {
        if (moveAction is null)
            return;

        // Unity-null as well as pattern-null: a destroyed BaseInput is not a
        // null pointer, and writing MovementRaw into one is silent.
        if (playerInput is null || playerInput == null)
        {
            playerInput = UnityEngine.Object.FindObjectOfType<Il2CppFuturLab.PW2.BaseInput>();

            if (playerInput is null || playerInput == null)
            {
                playerInput = null;
                moveStatus = "no BaseInput in the scene";
                return;
            }

            LoggerInstance.Msg($"Resolved player input: {playerInput.GetType().Name}");
        }

        // Same standing-down as the rotation mode below, for the same reason:
        // the game does NOT ignore MovementRaw while a menu is open, so the
        // avatar walked through the main menu on the navigation stick.
        if (menuMode)
        {
            if (wroteMovement != Vector2.zero)
            {
                wroteMovement = Vector2.zero;
                playerInput.MovementRaw = Vector2.zero;
            }

            moveStatus = "move: menu open";
            return;
        }

        // The rotation mode owns the left stick while it is active, and the
        // movement write has to stand down rather than fight it. Zero is sent
        // once on the way in, so the player stops instead of coasting on the
        // last value the game received.
        if (DriveItemRotation())
        {
            if (wroteMovement != Vector2.zero)
            {
                wroteMovement = Vector2.zero;
                playerInput.MovementRaw = Vector2.zero;
            }

            moveStatus = "move: rotating carried item";
            return;
        }

        try
        {
            var raw = moveAction.ReadValueAsObject();
            var stick = raw is null ? Vector2.zero : raw.Unbox<Vector2>();

            var heldBefore = playerInput.MovementRaw;
            var kept = (heldBefore - wroteMovement).sqrMagnitude < 0.0001f;

            // DIE RUECKFALLEBENE, bei 1.0 vollstaendig aus - dann wird die
            // Haltung hier nicht einmal gelesen. Siehe CrouchedMoveCap.
            var cap = crouchedMoveCap.Value;
            var capped = cap < 1f && RunBlockedByStance(stick.magnitude);

            if (capped)
                stick *= cap < 0f ? 0f : cap;

            playerInput.MovementRaw = stick;
            wroteMovement = stick;

            moveStatus = $"stick ({stick.x:0.##}, {stick.y:0.##})  "
                + $"{(capped ? "capped  " : "")}"
                + $"{(wroteMovement == Vector2.zero ? "" : kept ? "held" : "REVERTED")}";
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  movement threw {exception.GetType().Name}; giving up on it.");
            moveAction = null;
            moveStatus = "movement failed";
        }
    }

    // The head pose, both halves, as plain values. Captured since 0.24.0 and
    // unused until now.
    private bool TryReadHeadPose(out Quaternion rotation, out Vector3 position)
    {
        rotation = Quaternion.identity;
        position = Vector3.zero;

        try
        {
            var rotationValue = headRotationAction?.ReadValueAsObject();
            var positionValue = headPositionAction?.ReadValueAsObject();

            if (rotationValue is null || positionValue is null)
                return false;

            rotation = rotationValue.Unbox<Quaternion>();
            position = positionValue.Unbox<Vector3>();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Depth-first by name. childCount and GetChild(int) are an int and a class,
    // so this stays inside the safe call shapes from section 46.
    // Every match, not the first one. FindDeep's first-hit rule is a measurement
    // hazard here: the assembly carries one Locator_Nozzle_* per nozzle type and
    // each has its own NozzleAnchor(Clone)/RaySpawnPoint, INACTIVE ones included.
    // Picking an inactive sibling produced two runs whose numbers contradicted
    // each other - nozzle-versus-camera came out at a median of 0 deg in one and
    // 16 deg in the next, with 175 deg excursions that no physical nozzle makes.
    // Section 35 hit the same class of defect with two objects named
    // EquipmentAnchor.
    private static void CollectDeep(Transform root, string name, List<Transform> into)
    {
        for (var i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);

            if (string.Equals(child.name, name, StringComparison.Ordinal))
                into.Add(child);

            CollectDeep(child, name, into);
        }
    }

    private static string PathOf(Transform node)
    {
        var path = node.name;
        var walk = node.parent;

        // Four levels is enough to tell the nozzle locators apart and keeps the
        // line readable; the full chain from the scene root is not the question.
        for (var depth = 0; depth < 4 && walk is not null; depth++)
        {
            path = walk.name + "/" + path;
            walk = walk.parent;
        }

        return path;
    }

    private static Transform? FindDeep(Transform root, string name)
    {
        for (var i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);

            if (string.Equals(child.name, name, StringComparison.Ordinal))
                return child;

            var found = FindDeep(child, name);
            if (found is not null)
                return found;
        }

        return null;
    }

    // The point of the whole exercise: the jet as the pistol's extension.
    //
    // Its origin was always right - RaySpawnPoint sits at the muzzle and the VFX
    // is parented there. Only the direction came from the camera, which is how a
    // flat game aims. EquipmentManager.WashRay is the networked source of truth
    // and it has a setter, so it is written rather than patched. If the game
    // turns out to own it, the escalation is a postfix on get_WashRay - kept as
    // plan B because Ray leaving interop by value is the risky shape.
    private void DriveRay()
    {
        GameInput.RayActive = overrideRay.Value;

        // No early return when the override is off. The nozzle still has to be
        // located and the probe still has to run - measuring is the entire point
        // of this version, and the write below is gated on its own.
        if (assembly is null)
        {
            washLaser.Hide();
            laserStatus = "laser: no assembly";
            return;
        }

        try
        {
            // Re-searched on a timer rather than cached: switching the nozzle
            // destroys the old NozzleAnchor clone and its RaySpawnPoint with it.
            if (Time.unscaledTime >= nextRaySearch)
            {
                nextRaySearch = Time.unscaledTime + 0.5f;
                raySpawn = ResolveRaySpawn();
                // NOT "??=": that assigns only over a null pointer, so a
                // destroyed EquipmentManager would never be replaced.
                if (equipment is null || equipment == null)
                    equipment = UnityEngine.Object.FindObjectOfType<Il2CppFuturLab.PW2.EquipmentManager>();
            }

            if (raySpawn is null)
            {
                rayStatus = "ray: no RaySpawnPoint";
                washLaser.Hide();
                laserStatus = "laser: no nozzle";
                return;
            }

            // Ahead of the EquipmentManager gate on purpose: the laser needs the
            // nozzle and WashEquipment, not EquipmentManager, and a missing one
            // of those must not silently take the instrument away.
            //
            // Placed here rather than after the pose write at the end of
            // OnLateUpdate, which means the line trails the gun by one frame -
            // about 1 degree during a 90 deg/s sweep. Accepted deliberately: it
            // reads the SAME transform snapshot the probe logs a few lines down,
            // so the picture and the numbers cannot disagree, and the delicate
            // world-space write block stays untouched.
            washProbe.Sample();

            // Bit 16. Not a Harmony patch but a component toggle, because
            // PositionToFOV is a MonoBehaviour with its own LateUpdate - a bool
            // on a class reference is the safest shape available.
            washProbe.ApplyPositionToFov(LoggerInstance, (aimSkip.Value & 16) != 0);

            // Bit 2048. The shader FOV lock on the washer materials.
            gunRender.Apply(LoggerInstance, (aimSkip.Value & 2048) != 0, assembly);

            // Beside the lock write and from the same method, so both act on the
            // same resolved assembler in the same frame.
            // Die eigene Hand des Mods mitgeben, sonst blendet BodyMeshes sie
            // als Koerpergeometrie aus - im 1.29.0-Lauf genau so passiert.
            gunRender.ApplyVisualSwap(LoggerInstance, showDrivenWasher.Value,
                HideBodyGeometry, assembly, vrHands.WasherHandRoot);

            // Neben dem Waschertausch und aus demselben Grund: ein Outfit- oder
            // Ausruestungswechsel kann die Armgeometrie mitten im Level
            // austauschen, und ein einmaliges Schreiben ueberlebte das nicht.
            // Derselbe 0,5-s-Zeitgeber, und ohne Bestandsaenderung schreibt die
            // Schleife nichts.
            ApplyArmMode();

            // Jeder Aufruf treibt HOECHSTENS EINEN Schritt voran - anfragen oder
            // pollen -, damit ein Ladevorgang keinen Frame blockiert. Nach dem
            // letzten Schluessel kehrt die Methode sofort zurueck.
            if (probeHandAssets.Value)
                handAssets.Probe(LoggerInstance);

            // DIE HAENDE. Nur bei ausgeblendetem Armrig, sonst stuenden beide
            // gleichzeitig im Bild. Der Anker kommt namensfrei aus GunRender.
            var wantHands = showVrHands.Value;

            vrHands.Apply(LoggerInstance, wantHands, gunRender.BodyAnchor,
                node != XRNode.LeftHand);

            // publishedOffHandWorld und die Rotation entstehen im Pose-Block mit
            // demselben toWorld wie die Pistolenhand; hier wird nur gesetzt.
            // SYMMETRISCH ZUR OFF-HAND: Weltpose vom Controller, Trimm darauf.
            if (wantHands && washerHandWorldPublished)
                vrHands.DriveWasherHand(publishedWasherHandWorld,
                    publishedWasherHandRotation,
                    new Vector3(washerHandPosX.Value, washerHandPosY.Value,
                        washerHandPosZ.Value),
                    new Vector3(washerHandRotX.Value, washerHandRotY.Value,
                        washerHandRotZ.Value));

            if (wantHands && offHandWorldPublished)
                vrHands.DriveOffHand(publishedOffHandWorld, publishedOffHandRotation,
                    new Vector3(offHandPosX.Value, offHandPosY.Value, offHandPosZ.Value),
                    new Vector3(offHandRotX.Value, offHandRotY.Value, offHandRotZ.Value));

            // Bit 16384. The one candidate the millimetre-level exoneration of
            // the transform leaves standing: a vertex shader keyed to the field
            // of view, whose lock global reads a degenerate 1.
            gunRender.ApplyFovGlobals(LoggerInstance,
                (aimSkip.Value & 16384) != 0, (aimSkip.Value & 32768) != 0, Camera.main);

            // Computed once and handed to everything below, so the line that is
            // drawn, the origin the patches get and the distance the probe logs
            // are all the same point. Reading it three times would let them
            // disagree by a frame of PositionToFOV jitter, and the whole reason
            // the laser lives in this method is that picture and numbers must not
            // be able to diverge.
            var muzzle = MuzzlePoint(raySpawn);

            // Bit 128. The most direct lever in the whole system, and the last
            // one to be found because the hierarchy dumper hides the node.
            //
            // RaySpawnPoint.localRotation IS the entire discrepancy between gun
            // and nozzle. Every other transform in the chain -
            // PowerWasher_Assembly, Locator_Gun, Nozzle Locators Root,
            // Locator_Nozzle_*, NozzleAnchor(Clone) - reads localEuler (0,0,0) in
            // all five dumps. RaySpawnPoint never appears as its own entry
            // because single-child chains are collapsed, so its rotation was the
            // one value never looked at. The aim-test files recorded it all
            // along: localPos always (0,0,0), localEuler swinging by up to 16.1
            // deg with aim.
            //
            // Forced to identity rather than patched, because the carrier is a
            // transform and this mod writes transforms successfully everywhere
            // else. Identity means the nozzle points exactly where its parent
            // locator points, which is the gun. The readback below says whether
            // the write survives - the game rebuilds this in
            // PlayerCameraPreRender, a render callback after LateUpdate, so
            // losing the race is the expected failure and has to be visible
            // rather than guessed at.
            if ((aimSkip.Value & 128) != 0)
            {
                raySpawn.localRotation = Quaternion.identity;
                spawnRotationForced = true;
            }

            // Bit 65536. The anchor's LATERAL displacement, clamped away on the
            // game's own transform instead of only in this mod's arithmetic.
            //
            // Measured mechanism, not inferred. At startup the inventory reads
            //
            //     target[0] NozzleAnchor(Clone)  local (-0.07, -0.02, 0.06)
            //     positionToFOV: DISABLED, ResetTransform called
            //     fovFudge 0.073 m
            //
            // and 0.073 is exactly |(-0.07, -0.02)|. So ResetTransform restores
            // the offset PositionToFOV had RECORDED - and it recorded it while
            // already displaced. Disabling the component stops further writing
            // but cannot undo what was baked in. After a nozzle change the clone
            // is rebuilt from the prefab and fovFudge reads 0.
            //
            // That is precisely the reported symptom: "at start the jet is
            // slightly offset from the laser, switching nozzle centres it
            // immediately". MuzzlePoint already strips this laterally, which is
            // why the LASER and the effect zone were always right - but the
            // game's jet VFX hangs UNDER this anchor and nothing corrected it.
            //
            // Only x and y go. z is the forward offset the nozzle authored and it
            // differs per nozzle - the locators sit at 0.12, 0.2, 0.4, 0.65 and
            // 1.0 - so zeroing it would break every nozzle but one. The
            // third-person anchor, which carries no PositionToFOV, reads a clean
            // (0, 0, 0.04) in all five dumps, so lateral zero IS the authored
            // state.
            //
            // Written BEFORE MuzzlePoint below, so the laser, the jet and the
            // effect origin all derive from the same corrected transform in the
            // same frame. MuzzlePoint's own correction then becomes a no-op and
            // fovFudge logs 0 - which is the readback that says this clamp is
            // live.
            if ((aimSkip.Value & 65536) != 0)
            {
                var nozzleAnchor = raySpawn.parent;

                if (nozzleAnchor is not null && nozzleAnchor != null)
                {
                    var lp = nozzleAnchor.localPosition;

                    if (lp.x != 0f || lp.y != 0f)
                        nozzleAnchor.localPosition = new Vector3(0f, 0f, lp.z);
                }
            }

            // Bit 8192, the ray-origin calibration. Clamped per frame exactly
            // like the rotation above, and for the same reason: the game rebuilds
            // this node in PlayerCameraPreRender, so a one-shot write loses the
            // race while a per-frame one does not.
            //
            // Written BEFORE MuzzlePoint is computed, so the laser, the jet and
            // the effect origin all come from the same corrected transform in the
            // same frame. Writing it after would put the laser one frame ahead of
            // the VFX and make the two disagree while both were "correct".
            if ((aimSkip.Value & 8192) != 0)
            {
                raySpawn.localPosition = new Vector3(
                    rayOriginX.Value, rayOriginY.Value, rayOriginZ.Value);
                spawnPositionForced = true;
            }
            else if (spawnPositionForced)
            {
                // Nothing restores the game's own value, but it rewrites this
                // node every frame anyway, so one frame of the old offset is all
                // that switching the bit off costs.
                spawnPositionForced = false;
            }
            else if (spawnRotationForced)
            {
                // Left alone again once the bit clears. Nothing restores the
                // game's own value for us, but it rewrites it every frame, so
                // one frame of identity is all this costs.
                spawnRotationForced = false;
            }

            UpdateLaser(muzzle);

            // Reported BEFORE the EquipmentManager gate. The probe block IS the
            // deliverable of this version and needs only WashEquipment; gating
            // it behind a different component meant that a null EquipmentManager
            // produced a drawn laser and zero measurements, with nothing in the
            // log to say why the run came back empty.
            // The look-state line rides along on the laser string, because that
            // is the one channel that provably reaches the log in the default
            // world-space configuration. The overlay is unreadable in the
            // headset and the per-second pose line sits past an early return.
            // The single biggest block: about thirty lines a second, plus the
            // renderer and raycaster walks that produce them.
            if (Dev(verboseDiagnostics))
                washProbe.Report(LoggerInstance, raySpawn, muzzle, assembly,
                    $"{laserStatus}   {lookStateStatus}");

            if (equipment is null || equipment == null)
            {
                rayStatus = "ray: no EquipmentManager";
                return;
            }

            // Handed to the patches rather than written here. A write from
            // LateUpdate cannot survive: WashEquipment rebuilds its raycasts in
            // PlayerCameraPreRender, which the render callback fires later in the
            // frame. The patches act at the moment of use instead.
            // Origin corrected, direction untouched. NozzleAnchor's localEuler is
            // (-0, 0, 0) in all five dumps and raycaster[0].RaySpawnPointRotation
            // is (0, 0, 0), so the fudge is a pure translation - the beam was
            // never rotated, only moved. Whatever is wrong with the DIRECTION is
            // the FOV reprojection, a different finding with a different fix.
            // Published for the RaycastUpdate postfix, which needs them at the
            // moment the game has just filled the nozzle instances - a value it
            // can read without reaching back into the scene from inside a detour.
            GameInput.NozzleForward = raySpawn.forward;
            GameInput.NozzleOrigin = muzzle;
            GameInput.NozzleReady = true;

            GameInput.RayOrigin = muzzle;
            GameInput.RayDirection = raySpawn.forward;
            GameInput.RayReady = true;

            if (overrideRay.Value)
                equipment.WashRay = new Ray(muzzle, raySpawn.forward);

            // FESTGEHALTEN FUER DIE ZIELSUCHE, an genau der Stelle, an der
            // auch das Spiel seinen Strahl bekommt: nach dem Identity-Write
            // auf raySpawn.localRotation, nach der Anker-Klemmung und nach
            // MuzzlePoint. Alles, was diese Datei an der Richtung korrigiert,
            // ist hier drin.
            publishedAimOrigin = muzzle;
            publishedAimForward = raySpawn.forward;
            aimPublished = true;

            // FUER DEN TRAGE-HALTER, aus derselben Quelle und im selben Atemzug.
            //
            // CarryAimReady traegt die Bedingung mit: nur waehrend getragen
            // wird, und carrying ist in diesem Frame von ReportHeldItem
            // gesetzt, das in OnLateUpdate vor DriveRay laeuft. Aussehalb des
            // Tragens taucht der Tausch also gar nicht auf.
            GameInput.CarryFollowsHand = carryFollowsHand.Value;
            GameInput.CarrySwapOrigin = carrySwapOrigin.Value;
            GameInput.CarryAimForward = raySpawn.forward;
            GameInput.CarryAimOrigin = muzzle;
            GameInput.CarryAimReady = carrying;

            // The number the real fix will need, obtained without patching
            // anything: how far the nozzle and the gaze actually diverge.
            var gaze = Camera.main;
            var apart = gaze is null
                ? -1f
                : Vector3.Angle(raySpawn.forward, gaze.transform.forward);

            rayStatus = $"nozzle {Vector(raySpawn.forward)}  "
                + $"apart {apart.ToString("0.#", Invariant)} deg  "
                + $"get {GameInput.RayReads}  set {GameInput.RayWrites}  "
                + $"game {Vector(GameInput.LastGameDirection)}";
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  wash ray threw {exception.GetType().Name}; leaving it to the game.");
            overrideRay.Value = false;
            rayStatus = "ray: failed";
        }
    }

    private readonly List<Transform> rayCandidates = new();
    private string raySpawnPath = "";
    private bool spawnRotationForced;
    private bool spawnPositionForced;

    // Picks the ACTIVE RaySpawnPoint and says out loud how many it had to choose
    // between. Logged only when the choice changes, so a nozzle swap shows up as
    // one line rather than two per second.
    private Transform? ResolveRaySpawn()
    {
        if (assembly is null)
            return null;

        rayCandidates.Clear();
        CollectDeep(assembly, "RaySpawnPoint", rayCandidates);

        Transform? picked = null;
        var live = 0;

        foreach (var candidate in rayCandidates)
        {
            if (!candidate.gameObject.activeInHierarchy)
                continue;

            live++;
            picked ??= candidate;
        }

        // Falls back to an inactive one rather than to nothing: no nozzle at all
        // is a different and louder failure, and the path in the log will say
        // which case this is.
        if (picked is null && rayCandidates.Count > 0)
            picked = rayCandidates[0];

        if (picked is null)
        {
            if (raySpawnPath.Length > 0)
            {
                raySpawnPath = "";
                LoggerInstance.Msg("  raySpawn: none under the assembly");
            }

            return null;
        }

        var path = PathOf(picked);

        if (!string.Equals(path, raySpawnPath, StringComparison.Ordinal))
        {
            raySpawnPath = path;
            LoggerInstance.Msg($"  raySpawn: {rayCandidates.Count} candidate(s), {live} active"
                + $"  using {path}  active {picked.gameObject.activeInHierarchy}");
        }

        return picked;
    }

    // 6DOF head tracking: standing up out of a seated position has to change the
    // eye height, and leaning has to move the viewpoint.
    //
    // Written on HeadTurn.localPosition, NOT on the camera's. HeadTurn's own
    // rotation carries bodyYaw + head yaw, so a constant offset written one level
    // below would swing around with every head turn. HeadTurn's PARENT is the
    // player root, which does not rotate with the head, so an offset there is
    // stable - it only needs the artificial body yaw applied, so that leaning
    // left still goes left after a stick turn.
    //
    // The delta is taken from a reference captured on the first frame, for the
    // reason section 54 established for the gun: the tracking origin sits
    // wherever the runtime put it, so only the CHANGE is meaningful. Num3
    // recentres both.
    private void DriveHeadPosition()
    {
        if (headTurn is null || !driveHeadPosition.Value)
            return;

        if (!TryReadHeadPose(out _, out var hmdPosition))
        {
            // DEN VORFRAME ENTWERTEN. Sonst vergleicht der erste Frame nach
            // einem Leseausfall gegen eine alte Pose und meldet einen Sprung,
            // den es nie gab. Das ist ein Teilschutz, keine Loesung: ein
            // Tracking-Verlust, bei dem die Laufzeitumgebung weiter die letzte
            // Pose liefert, kommt hier nicht an - dafuer steht der Zaehler in
            // der Statuszeile.
            lastHeadPoseValid = false;
            headPositionStatus = "head pos: no pose";
            return;
        }

        try
        {
            // DER SPRUNG, DEN KEIN KOPF MACHT - Abschnitt 128.
            //
            // Vor der Erfassung, damit die Erkennung sie im selben Frame
            // ausloesen kann. Gegen den VORFRAME gemessen, nicht gegen die
            // Basis: die Basis darf sich beim Aufstehen langsam weit
            // entfernen, das ist ja der Sinn der 6DOF-Hoehe.
            var jumpLimit = headRecenterJump.Value;

            if (jumpLimit > 0f && lastHeadPoseValid && headBaseCaptured)
            {
                var moved = (hmdPosition - lastHeadPose).magnitude;

                if (moved > jumpLimit)
                {
                    recenterHeadRequested = true;
                    headRecenters++;

                    // GEDECKELT. Eine Zeile pro Erkennung ist die Auskunft,
                    // ob es ausgeloest hat; hundert Zeilen waeren nur
                    // Rauschen, und der Zaehler traegt die Menge.
                    if (headRecenters <= 8)
                        LoggerInstance.Msg("head recenter: the tracking origin moved "
                            + $"{moved:0.###} m in one frame, past {jumpLimit:0.##} m. "
                            + "No head does that, so this is the runtime recentring - "
                            + "re-capturing the eye-height reference.");
                }
            }

            lastHeadPose = hmdPosition;
            lastHeadPoseValid = true;

            // DIE UNBERUEHRTE RUHELAGE, einmal pro Level und VOR dem ersten
            // Schreibvorgang. Ab dem ersten Schreiben steht in
            // headTurn.localPosition Ruhelage PLUS Versatz.
            if (!headTurnRestCaptured)
            {
                headTurnRestCaptured = true;
                headTurnRest = headTurn.localPosition;
            }

            if (!headBaseCaptured || recenterHeadRequested)
            {
                headBaseCaptured = true;
                recenterHeadRequested = false;
                headPoseBase = hmdPosition;

                // AUF DIE RUHELAGE, NICHT AUF DEN GESCHRIEBENEN WERT - und
                // hier lag der zweite Fehler. Die Vorfassung las
                // headTurn.localPosition, also Ruhelage plus alten Versatz.
                // Der neue Versatz wird 0, die Summe bleibt gleich, der
                // Blickpunkt bewegt sich nicht: Num3 hat nie auf neutrale
                // Augenhoehe zurueckgesetzt, sondern die aktuelle Hoehe zur
                // neuen Neutrale erklaert. Genau die gemeldete Beschwerde.
                headTurnBase = headTurnRest;

                LoggerInstance.Msg($"head position reference: hmd {Vector(headPoseBase)}  "
                    + $"headTurn {Vector(headTurnBase)}  (the game's own rest pose)");
            }

            // Read back BEFORE writing, so a game that owns this transform is
            // reported rather than silently fought - the mistake that cost a run
            // on EquipmentAnchor in 0.22.0.
            var found = headTurn.localPosition;
            var kept = (found - wroteHeadPosition).sqrMagnitude < 0.000001f;

            var offset = Quaternion.Euler(0f, bodyYaw, 0f) * (hmdPosition - headPoseBase);

            wroteHeadPosition = headTurnBase + offset;
            headTurn.localPosition = wroteHeadPosition;

            headPositionStatus = $"head pos {Vector(offset)}"
                + (headRecenters == 0 ? "" : $"  recenters {headRecenters}")
                + (headPositionEverWritten && !kept ? "  REVERTED" : "");
            headPositionEverWritten = true;
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  head position threw {exception.GetType().Name}; "
                + "leaving the head where the game puts it.");
            driveHeadPosition.Value = false;
            headPositionStatus = "head pos: failed";
        }
    }

    // Integrates the artificial body yaw. Deliberately writes NO transform: the
    // write belongs to DriveHead, which owns HeadTurn.
    private void ReadTurn()
    {
        if (turnAction is null)
        {
            turnStatus = "turn: no stick";
            return;
        }

        // A menu owns the sticks. Turning the body while reading a menu is
        // disorienting, and a nozzle change triggered from a menu is invisible.
        if (menuMode)
        {
            turnStatus = "turn: menu open";
            snapArmed = true;
            nozzleArmed = true;
            return;
        }

        try
        {
            var raw = turnAction.ReadValueAsObject();
            var stick = raw is null ? Vector2.zero : raw.Unbox<Vector2>();
            var x = stick.x;

            // NOZZLE CYCLING ON THE SAME STICK, on Y, and this is the first
            // gameplay action reachable with the headset on.
            //
            // The flat game binds next and previous nozzle to scroll up/down and
            // dpad right/left - it is an AXIS, not two buttons. Right stick Y is
            // the one free axis, it sits on the washer hand where nozzle changes
            // belong, and it costs no buttons at all.
            //
            // The dominance test is not optional. X on this same stick is the
            // smooth turn, so without it a diagonal flick would turn AND change
            // the nozzle. Edge-triggered and re-armed inside the deadzone, the
            // same shape snapArmed already uses for snap turn.
            var y = stick.y;

            if (Mathf.Abs(y) > turnDeadzone.Value && Mathf.Abs(y) > Mathf.Abs(x) * 1.5f)
            {
                if (nozzleArmed && playerInput is not null && playerInput != null)
                {
                    nozzleArmed = false;
                    var direction = y > 0f ? 1 : -1;

                    // Logged with the configuration on BOTH sides, because the
                    // open question is which nozzles the stick can actually
                    // reach. R3 cycles the CATEGORY and was measured stepping
                    // 0 -> 2 -> 0, skipping group 1 - the IsOwned filter leaving
                    // out an unowned category. Whether the turbo nozzle is in
                    // that skipped group, or sits inside group 0 where the stick
                    // should already find it, or belongs to a different WASHER
                    // altogether (InvokeSwitchGun / SwitchWasherGroup), is
                    // undecided. Printing group and name per step enumerates the
                    // reachable set instead of guessing at it.
                    var before = DescribeConfiguration();
                    playerInput.InvokeSwitchNozzle(direction);
                    LoggerInstance.Msg($"nozzle: {(direction > 0 ? "next" : "previous")} "
                        + $"(right stick Y)  before {before}  after {DescribeConfiguration()}");
                }
            }
            // RE-ARMED ON THE WHOLE STICK, not on y alone - section 75 point 5.
            //
            // The old condition re-armed as soon as |y| fell below the deadzone.
            // During a sustained turn x stays large while y wanders freely about
            // zero, so every crossing of the y deadzone re-armed and the next
            // small upward push fired again. The stick never had to return to
            // centre, which is why turning occasionally changed the nozzle by
            // itself.
            //
            // Requiring BOTH axes inside the deadzone means a nozzle change is
            // only possible from the centre position - a deliberate flick - and
            // a turn in progress cannot produce one at all. One more condition,
            // no cost, and it removes the cause rather than moving the
            // threshold.
            else if (Mathf.Abs(y) < turnDeadzone.Value && Mathf.Abs(x) < turnDeadzone.Value)
            {
                nozzleArmed = true;
            }

            if (Mathf.Abs(x) < turnDeadzone.Value)
            {
                snapArmed = true;
                turnStatus = $"turn idle  yaw {bodyYaw.ToString("0.#", Invariant)}";
                return;
            }

            if (snapTurn.Value)
            {
                if (snapArmed)
                {
                    bodyYaw += Mathf.Sign(x) * snapAngle.Value;
                    snapArmed = false;
                    LoggerInstance.Msg($"snap turn {(x < 0f ? "left" : "right")}"
                        + $" -> yaw {bodyYaw.ToString("0.#", Invariant)}");
                }
            }
            else
            {
                // Rescaled past the deadzone, so the first usable degree of
                // travel is not a jump from zero to deadzone times speed.
                var span = Mathf.Max(0.0001f, 1f - turnDeadzone.Value);
                var scaled = Mathf.Clamp01((Mathf.Abs(x) - turnDeadzone.Value) / span);

                // Unscaled, so comfort turning does not freeze with the game's
                // time scale.
                bodyYaw += Mathf.Sign(x) * scaled * turnSpeed.Value * Time.unscaledDeltaTime;
            }

            bodyYaw = Mathf.Repeat(bodyYaw, 360f);

            turnStatus = $"turn {(snapTurn.Value ? "snap" : "smooth")} {x:0.##}  "
                + $"yaw {bodyYaw.ToString("0.#", Invariant)}";
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  turn read threw {exception.GetType().Name}; giving up on it.");
            turnAction = null;
            turnStatus = "turn failed";
        }
    }

    // Nozzle control through the game's OWN input surface, which is the project
    // rule from CLAUDE.md: drive what the game already reads.
    //
    // BaseInput carries public Invoke* helpers - the game's own raise-the-event
    // methods - so no il2cpp delegate has to be built and no WasherInputHandler
    // has to be found (it is not even a MonoBehaviour, so FindObjectOfType could
    // not reach it). Every parameter is an int or nothing at all, which keeps
    // this clear of the struct shapes that crashed the process in sections 45
    // and 46. No new csproj reference either.
    //
    // The int is a DIRECTION, +1 or -1, not an index: the game's action map
    // binds NextNozzle and PreviousNozzle to the same one-int handler, and the
    // shipped XR layer names its equivalent CycleNozzle.
    //
    // InvokeRotateNozzle takes no parameter at all - it is the horizontal and
    // vertical fan toggle, which is a different axis from nozzle WIDTH (the
    // spray angle, a separate plus-minus control that has no Invoke helper and
    // would need the handler).
    // Both halves, on one press, because that is what the flat game does.
    //
    // The extracted InputActionAsset shows <Keyboard>/r bound to TWO actions -
    // RotateNozzle AND RecallSoap - with identical binding lists. One press
    // raises both and the inapplicable half does nothing. This mod only ever
    // called InvokeRotateNozzle, which is exactly why refilling detergent has
    // been missing entirely rather than merely unbound.
    // The rest of the basic controls, all through the game's own Invoke helpers.
    //
    // Two of the calls below are the game's own overloads rather than
    // inventions. Tab is bound to BOTH TaskList_Toggle and FurnitureInventory,
    // and FurnitureInventory declares interactions 'Hold,Press' - so tap for the
    // task list and hold for the inventory on one control IS the shipped design.
    // Same for stance: BaseInput ships the pair InvokeCrouchPressed and
    // InvokeCrouchLongPressed precisely so a caller can split them.
    //
    // The spray latch is deliberately NOT a game call. The mod already owns the
    // Fire getter, so a continuous-spray toggle is one bool here - and the
    // game-side routes (StaticWashing, FireOverride, m_toggleFire) are tangled
    // with the proximity state machine and two player settings. In VR a latch is
    // the single most valuable ergonomic on the list: holding a trigger for
    // minutes is the worst thing carried over from the desk game.
    // LIVE GRIP CALIBRATION, the UEVR-style gesture: hold the chord, the washer
    // stops following the hand and stands still in the room, move the controller
    // to where the washer should sit in your hand, let go, and the difference
    // becomes the saved offsets.
    //
    // IT INVERTS CLEANLY, and that is not luck - it is what the grip fix of
    // section 51 bought. The pose is composed as
    //
    //     gunRotation = toWorld * controllerRotation * offsetRotation
    //     handWorld   = camT.position + toWorld * (position - hmd + offset)
    //     gunPosition = handWorld + gunRotation * grip
    //
    // offsetRotation sits on the RIGHT of the controller rotation, so it is a
    // rotation in the washer's own frame, and grip is turned by the full
    // gunRotation, so it is a constant in that same frame. Those two are exactly
    // what "how does it sit in my hand" means, each appears once, and each
    // appears on the right - so both solve directly:
    //
    //     offsetRotation = Inverse(toWorld * controllerRotation) * frozenRot
    //     grip           = Inverse(frozenRot) * (frozenPos - handWorld)
    //
    // Had the offsets been applied in world or head space this would not invert,
    // and the comment at the position write says why they are not: "the entire
    // grip fix is WHICH ROTATION each offset gets".
    //
    // ONE FRAME, ONE SNAPSHOT. The chord is read in DriveButtons, which runs
    // earlier in the frame than the pose write, so the release only sets a
    // REQUEST. The solve then happens inside the pose write, from that method's
    // own toWorld, hmd pose and controller pose - the same frame and the same
    // values that produced the number being solved against. Carrying the hand
    // pose over from the button read would pair two frames, which is the mistake
    // that has cost this project three misdiagnoses.
    //
    // PositionOffset stays a KNOWN, not a second unknown: one equation, one
    // unknown. The calibration solves grip and leaves the position trim alone.
    private bool calibrating;
    private bool calibArmRequested;
    private bool calibSolveRequested;
    private bool calibHeldLast;
    private float calibLockoutUntil;
    private Quaternion calibRot = Quaternion.identity;
    private Vector3 calibPos;

    // True while the chord or the key is held, and for a short tail afterwards.
    //
    // The tail is not cosmetic: the task-list button carries a 0.4 s hold half,
    // so ITS tap lands on RELEASE - which is the same instant the chord ends.
    // Without the tail, letting go of the calibration would open the task list
    // every single time.
    private bool CalibrateSuppressed() => calibrating || Time.unscaledTime < calibLockoutUntil;

    private void ReadCalibrateHold()
    {
        // THREE BUTTONS ON THE OFF HAND, asked for in exactly that shape: two
        // would be easy to hit while washing, and a mis-hit here would silently
        // recalibrate the grip. The washer hand stays completely free, which it
        // has to be - it is the hand doing the aligning.
        var chord = ButtonEdge.ReadAxis(leftSqueeze) > 0.6f
            && ButtonEdge.IsDown(leftPrimary)
            && ButtonEdge.IsDown(leftSecondary);

        // GetKey, not GetKeyDown: this is a hold, not a press. Alt-guarded like
        // every other key in this mod.
        var keyHeld = !AltHeld()
            && Enum.TryParse<KeyCode>(calibrateKey.Value, ignoreCase: true, out var key)
            && Input.GetKey(key);

        var held = chord || keyHeld;

        if (held)
            calibLockoutUntil = Time.unscaledTime + 0.3f;

        if (held && !calibHeldLast)
        {
            // REFUSED IN LOCAL SPACE, with a reason. The solve inverts the
            // WORLD-space composition; the local branch composes differently, so
            // running it there would write a confidently wrong number. Saying so
            // on the press edge costs one line per gesture and nothing per frame.
            if (!worldSpace.Value)
            {
                LoggerInstance.Msg("calibrate: ignored, WorldSpace is off - the solve "
                    + "only inverts the world-space pose. Turn WorldSpace on to calibrate.");
            }
            else
            {
                calibArmRequested = true;
            }
        }
        else if (!held && calibHeldLast)
        {
            // BOTH requests dropped on release, not just the solve. An arm
            // request the pose write never reached - no assembly, no head pose,
            // position driving off - would otherwise survive and freeze the
            // washer the next time that branch ran, long after the gesture.
            calibArmRequested = false;

            if (calibrating)
                calibSolveRequested = true;
        }

        calibHeldLast = held;
    }

    // Called from the pose write, with that frame's own values.
    //
    // handRotWorld is the hand's rotation in world space WITHOUT the trim -
    // toWorld * controllerRotation - and handWorld is the point the washer hangs
    // off. Both are computed a few lines above the call.
    private void SolveCalibration(Quaternion handRotWorld, Vector3 handWorld)
    {
        var wantedRotation = Quaternion.Inverse(handRotWorld) * calibRot;
        var wantedGrip = Quaternion.Inverse(calibRot) * (calibPos - handWorld);

        // eulerAngles returns 0..360 and the configurator's sliders run
        // -180..180, so 350 has to come out as -10. Without this the sliders
        // would show a value they cannot represent and snap to their end stop
        // the first time the window opened.
        var pitch = Wrap180(wantedRotation.eulerAngles.x);
        var yaw = Wrap180(wantedRotation.eulerAngles.y);
        var roll = Wrap180(wantedRotation.eulerAngles.z);

        var oldGrip = new Vector3(gripX.Value, gripY.Value, gripZ.Value);
        var oldRotation = Quaternion.Euler(pitchOffset.Value, yawOffset.Value, rollOffset.Value);
        var moved = (wantedGrip - oldGrip).magnitude;
        var turned = Quaternion.Angle(oldRotation, wantedRotation);

        // A TAP IS A NO-OP BY CONSTRUCTION, and this makes that explicit. If the
        // hand never moved between the freeze and the release, the solve returns
        // the values already in force - so an accidental brush of the chord
        // cannot move the grip. Reported as a worry, and it is worth saying in
        // the log rather than leaving it to be trusted.
        if (moved < 0.005f && turned < 1f)
        {
            LoggerInstance.Msg("calibrate: released without moving - nothing changed "
                + $"(moved {moved * 100f:0.#} cm, turned {turned:0.#} deg)");
            return;
        }

        // CLAMPED to the configurator's own slider range. A solve that lands
        // outside it would be unreachable and unreadable in that window, and a
        // value the UI cannot show is a value nobody can undo.
        var clamped = new Vector3(
            Mathf.Clamp(wantedGrip.x, -0.25f, 0.25f),
            Mathf.Clamp(wantedGrip.y, -0.25f, 0.25f),
            Mathf.Clamp(wantedGrip.z, -0.25f, 0.25f));

        if ((clamped - wantedGrip).magnitude > 0.0005f)
        {
            LoggerInstance.Warning($"  calibrate: grip {Vector(wantedGrip)} is outside "
                + "+-25 cm and was clamped. The washer will not sit exactly where it was "
                + "left - move your hand closer to the washer and calibrate again.");
        }

        gripX.Value = clamped.x;
        gripY.Value = clamped.y;
        gripZ.Value = clamped.z;
        pitchOffset.Value = pitch;
        yawOffset.Value = yaw;
        rollOffset.Value = roll;
        MelonPreferences.Save();

        // THE SOURCE IS RECORDED, because the numbers only mean anything with it.
        // PositionSource and UseAimPose decide what the controller pose even IS,
        // so a calibration taken on the grip pose does not transfer to the aim
        // pose - and the next reader of this log needs to know which one it was.
        LoggerInstance.Msg($"calibrate: grip {Vector(clamped)}   "
            + $"rotation pitch {pitch:0.#} yaw {yaw:0.#} roll {roll:0.#}   "
            + $"moved {moved * 100f:0.#} cm, turned {turned:0.#} deg   "
            + $"src {PositionSourceName()}, aimPose {useAimPose.Value}   saved");
    }

    // eulerAngles hands back 0..360, so one subtraction is all it takes.
    private static float Wrap180(float degrees) =>
        degrees > 180f ? degrees - 360f : degrees;

    // BODY-ZONE GESTURES: a spatial condition on top of a grip press.
    //
    // VR-EINGABE -> VORHANDENER PWS2-PFAD -> VORHANDENE SPIELLOGIK, the same
    // architecture the spray already uses. Nothing here implements tool logic:
    // each gesture raises the identical Invoke helper the stick click raises, so
    // there is exactly one place in this mod that knows how a washer, a nozzle
    // category or an extension is switched, and it is not here.
    //
    // ALL THREE TARGETS TAKE A DIRECTION, NOT A STATE. InvokeSwitchGun(int),
    // InvokeSwitchNozzleGroup(int) and InvokeSwitchExtension(int) all raise an
    // Action<int>, and +1 means "next" - the game does the wrapping. So three or
    // more unlocked tools work by construction, and nothing in here may assume a
    // two-state toggle, because there is none.
    //
    // WHY THIS RUNS BEFORE DriveButtons, and it is the same lesson as
    // DriveMenuPointer in section 90: the gestures share their grips with the
    // dirt highlight, the spray latch and the carried-item rotation, and those
    // are evaluated INSIDE DriveButtons. The zone state has to stand before that
    // method runs, or the suppression arrives a frame late and the borrowed
    // action fires anyway.
    // ONE edge for the right grip, not one per zone: the shoulder and the hip
    // gesture share that grip, so two ButtonEdge instances would poll the same
    // control and only one of their taps could ever be read. Which of the two
    // zones the press belongs to is decided below, from the zone state.
    private readonly ButtonEdge rightZoneGrip = new();
    private readonly ButtonEdge washerGrip = new();
    private bool inShoulderZone;
    private bool inHipZone;
    private bool inWasherZone;
    private float nextShoulderGesture;
    private float nextHipGesture;
    private float nextWasherGesture;
    private float zoneSuppressUntil;

    // ====================================================================
    // DER WAECHTER FUER DAS EINRASTEN - Abschnitt 111.
    //
    // Gefuellt jeden Frame in DriveBodyZones, gelesen in DriveButtons, das
    // unmittelbar danach laeuft: EINE Momentaufnahme pro Frame statt zweier
    // Abgriffe an verschiedenen Stellen. Zwei Abgriffe haben in diesem
    // Projekt schon Diagnosen gekostet.
    //
    // Die beiden Abstaende laufen mit, nicht nur das bool: die Logzeile eines
    // geschluckten Druckes muss die Zahl nennen, sonst ist der naechste Lauf
    // wieder eine Vermutung statt einer Messung.
    private bool nearGestureZone;
    private float zoneGuardUntil;
    private float zoneShoulderDistance;
    private float zoneHipDistance;

    // Der verbrauchte Druck, als FRAMENUMMER statt als Frist.
    //
    // Nicht zoneSuppressUntil, und der Unterschied ist kein Geschmack: das
    // sperrt auch die Schmutz-Hervorhebung am linken Griff und traegt die
    // Meldung "grip taken by a body-zone gesture", die hier falsch waere.
    //
    // Und keine Zeitfrist: der Nachlauf von zoneSuppressUntil existiert fuer
    // Verbraucher mit Halte-Haelfte, deren Tipp beim LOSLASSEN landet. Die
    // Rastung hat holdSeconds 0, ihr Tap steht genau einen Frame, und
    // DriveBodyZones und DriveButtons laufen im selben OnLateUpdate. Eine
    // Frist koennte hier also nichts einfangen, was die Framenummer nicht
    // einfaengt - sie wuerde nur ein bewusstes Wieder-Einrasten innerhalb
    // ihrer Laufzeit verschlucken und damit eine kleine Unbedienbarkeit dort
    // einbauen, wo gerade eine grosse ausgebaut wird.
    private int latchClearedFrame = -1;
    private float nextZoneDebug;
    private float torsoYaw;
    private bool torsoYawValid;
    private bool loggedZonePoses;

    // True while a gesture has just taken a grip press, plus a short tail.
    //
    // The tail matters for the same reason it did for the calibration chord: a
    // consumer with a hold half lands its tap on RELEASE, which is the instant
    // the gesture ends.
    private bool ZoneGestureSuppressed() => Time.unscaledTime < zoneSuppressUntil;

    // True, solange der Griffdruck dieses Frames schon das Ausrasten bezahlt
    // hat. Ohne das saehe DriveButtons denselben Tap im selben Frame erneut
    // und wuerde sofort neu einrasten - der Spieler haette den Strahl nie
    // ausgeschaltet.
    private bool LatchJustCleared() => Time.frameCount == latchClearedFrame;

    // DER WAECHTER, und er sperrt AUSSCHLIESSLICH das Einrasten.
    //
    // Zwei Gruende, weil die Messung zwei zeigt: die Hand liegt zu nah an
    // einer Gestenzone - gemessen 4 bis 36 mm ausserhalb der 0,20-m-Kugel -
    // oder sie hat die Zone gerade verlassen, gemessen 0,07 bis 0,47 s vorher.
    // Ein Radius allein erwischt den zweiten Fall nicht, denn der Griffdruck
    // zieht die Hand im selben Zug aus der Kugel.
    private bool LatchGuarded() => sprayLatchZoneGuard.Value
        && (nearGestureZone || Time.unscaledTime < zoneGuardUntil);

    // Die Zahlen hinter dem Urteil, fuer die Logzeile. Ein geschluckter Druck
    // ohne den Abstand daneben waere genau die Zeile, die den naechsten Lauf
    // nicht entscheidet.
    private string LatchGuardReason() =>
        $"hip {zoneHipDistance:0.###}/"
        + $"{hipZoneRadius.Value + sprayLatchZoneMargin.Value:0.###}"
        + $"   shoulder {zoneShoulderDistance:0.###}/"
        + $"{shoulderZoneRadius.Value + sprayLatchZoneMargin.Value:0.###}"
        + $"   graceLeft {Mathf.Max(0f, zoneGuardUntil - Time.unscaledTime):0.##} s";

    private void DriveBodyZones()
    {
        if (!gestureZones.Value || playerInput is null || playerInput == null)
            return;

        // A menu owns the grips - they step the tabs there. Same gate the rest of
        // the button handling uses.
        if (menuMode)
        {
            inShoulderZone = false;
            inHipZone = false;
            inWasherZone = false;

            // MIT GERAEUMT. Dieser Zweig kehrt vor der Messung um, also bliebe
            // nearGestureZone auf dem Wert von vor dem Menue stehen und wuerde
            // das Einrasten nach dem Schliessen grundlos sperren - ein Flag
            // ohne seinen Zustand, die Falle, die dieses Projekt kennt.
            nearGestureZone = false;
            return;
        }

        try
        {
            if (!TryReadHeadPose(out var hmdRotation, out var hmdPosition))
                return;

            if (!TryReadTrackedPoint(positionAction, out var handPosition))
                return;

            // REPORTED ONCE, because the off-hand binding is new and its absence
            // decides whether gesture three exists at all. A silent missing pose
            // would look like a broken gesture.
            if (!loggedZonePoses)
            {
                loggedZonePoses = true;
                LoggerInstance.Msg("zones: head ok, washer hand ok, off-hand position "
                    + $"{(offHandPosition is null ? "NOT BOUND - the washer gesture is off" : "ok")}"
                    + $", off-hand rotation {(offHandRotation is null ? "NOT BOUND" : "bound")}");
            }

            // THE TORSO YAW, and the fallback is the interesting half.
            //
            // Projecting the head's forward axis onto the horizontal plane gives
            // the direction the body faces. When the player looks nearly
            // straight up or down that projection collapses - and looking up is
            // exactly what someone does while reaching over their shoulder. The
            // LAST GOOD yaw is kept instead of computing a degenerate new one.
            //
            // eulerAngles.y would be the wrong tool here: it flips in the same
            // attitude, and the zone would swing behind the player's back.
            //
            // The artificial body yaw from the stick does NOT enter, and that is
            // correct rather than an omission: both points are read in the same
            // tracking space, so the stick turn cancels out. The zone stays
            // "behind my real right shoulder", which is what the body feels.
            var forward = hmdRotation * Vector3.forward;
            var flat = new Vector3(forward.x, 0f, forward.z);

            if (flat.sqrMagnitude > 0.01f)
            {
                torsoYaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
                torsoYawValid = true;
            }
            else if (!torsoYawValid)
            {
                // Never had a good one - the very first frames, head straight up.
                return;
            }

            var toTorso = Quaternion.Inverse(Quaternion.Euler(0f, torsoYaw, 0f));
            var handLocal = toTorso * (handPosition - hmdPosition);

            // GESPIEGELT FUER LINKSHAENDER - Abschnitt 108.
            //
            // Die X-Werte sind Meter RECHTS vom Kopf und beschreiben eine
            // Pistole in der rechten Hand. Fuehrt die linke sie, liegen
            // Schulter- und Hueftzone auf der falschen Seite und die Gesten
            // sind unerreichbar - gemeldet, und im Rechtshaenderbetrieb nicht
            // zu sehen.
            //
            // Ein Vorzeichen statt zweier Wertepaare: die Preference bleibt
            // eine Preference. Die Waschzone braucht das nicht, sie haengt an
            // der Pistolenhand selbst.
            var side = node == XRNode.LeftHand ? -1f : 1f;

            var shoulderCentre = new Vector3(shoulderZoneX.Value * side,
                shoulderZoneY.Value, shoulderZoneZ.Value);
            var hipCentre = new Vector3(hipZoneX.Value * side,
                hipZoneY.Value, hipZoneZ.Value);

            var shoulderDistance = (handLocal - shoulderCentre).magnitude;
            var hipDistance = (handLocal - hipCentre).magnitude;

            // DIE VORIGE LAGE, gesichert VOR den beiden Aufrufen: UpdateZone
            // schreibt den Zustand per ref, danach ist sie nicht mehr lesbar.
            var wasShoulderZone = inShoulderZone;
            var wasHipZone = inHipZone;

            UpdateZone("shoulderR", shoulderDistance <= shoulderZoneRadius.Value,
                ref inShoulderZone, handLocal);
            UpdateZone("hipR", hipDistance <= hipZoneRadius.Value,
                ref inHipZone, handLocal);

            // DER WAECHTER-ZUSTAND DIESES FRAMES, gesetzt bevor irgendein Tap
            // ausgewertet wird.
            //
            // Der Rand gilt NUR hier und laesst die Gestenzonen selbst
            // unberuehrt: eine Geste soll nicht schwerer werden, nur weil das
            // Einrasten vorsichtiger wird.
            zoneShoulderDistance = shoulderDistance;
            zoneHipDistance = hipDistance;
            nearGestureZone =
                shoulderDistance <= shoulderZoneRadius.Value + sprayLatchZoneMargin.Value
                || hipDistance <= hipZoneRadius.Value + sprayLatchZoneMargin.Value;

            // NUR SCHULTER UND HUEFTE. Die Waschzone haengt am Griff der
            // freien Hand und hat mit der Rastung nichts zu tun; ihre
            // Austrittsflanke hier mitzunehmen wuerde das Einrasten sperren,
            // wann immer die zweite Hand die Pistole verlaesst.
            if ((wasShoulderZone && !inShoulderZone) || (wasHipZone && !inHipZone))
                zoneGuardUntil = Time.unscaledTime + sprayLatchZoneGrace.Value;

            // THE WASHER ZONE, against the washer hand rather than the body.
            var washerLocal = Vector3.zero;
            var washerInside = false;

            if (offHandPosition is not null
                && TryReadTrackedPoint(offHandPosition, out var offHand)
                && TryReadRotation(rotationAction, out var handRotation))
            {
                var centre = handPosition
                    + (handRotation * new Vector3(0f, 0f, washerZoneForward.Value));
                washerLocal = offHand - centre;
                washerInside = washerLocal.magnitude <= washerZoneRadius.Value;
            }

            UpdateZone("washer", washerInside, ref inWasherZone, washerLocal);

            // POLLED EVERY FRAME regardless of the zone, so the edge state stays
            // honest. Feeding a poll false while the grip is held turns RELEASING
            // it into a fresh down edge - the defect section 93 recorded for the
            // spray latch. The zone is tested at the moment of the tap instead.
            rightZoneGrip.Poll(ButtonEdge.ReadAxis(rightSqueeze) > 0.6f, 0f);
            washerGrip.Poll(ButtonEdge.ReadAxis(leftSqueeze) > 0.6f, 0f);

            // ONE PRESS, ONE ACTION. The nearer centre wins. Raising two tool
            // changes from a single grip would be the worse failure, and the log
            // names which one was picked and why.
            //
            // A BELT SINCE ABSCHNITT 102, NOT THE MECHANISM. At the old radius of
            // 0.30 the two spheres overlapped by 0.15 m and this branch decided
            // real cases; at 0.15 the geometry no longer overlaps, so inHipZone
            // and inShoulderZone should never both be true. It stays because a
            // radius is a preference and someone will raise it again.
            // AUSRASTEN GEWINNT IMMER, und das ist die Haelfte der Behebung,
            // die den festsitzenden Strahl unmoeglich macht.
            //
            // Liegt die Rastung, raeumt dieser Druck sie, und die Geste feuert
            // NICHT. Gemessen war genau das Gegenteil: 58 Griffdruecke gingen
            // an Gesten, waehrend die Rastung lag, und der Spieler sah nur die
            // Duesengruppe umklappen, waehrend der Strahl weiterlief - ab
            // 17:38:07 zehnmal in Folge.
            //
            // DIE GESTE ZU VERLIEREN IST DER PREIS UND IST RICHTIG SO. Ein
            // Druck, der beides tut, waere nicht vorhersagbar; die Regel
            // lautet jetzt schlicht: laeuft der Dauerstrahl, hoert der Griff
            // ihn auf, sonst macht er die Geste.
            if (rightZoneGrip.Tap && GameInput.FireLatched
                && sprayLatchGripClears.Value)
            {
                GameInput.FireLatched = false;
                latchClearedFrame = Time.frameCount;

                if (sprayLatchBuzz.Value)
                    Buzz(WasherHandRight, "spray latch off");

                LoggerInstance.Msg("continuous spray off (grip clears the latch, "
                    + $"gesture skipped)   {LatchGuardReason()}");
            }
            else if (rightZoneGrip.Tap && (inShoulderZone || inHipZone))
            {
                var shoulderWins = inShoulderZone
                    && (!inHipZone || shoulderDistance <= hipDistance);

                if (shoulderWins && Time.unscaledTime >= nextShoulderGesture)
                {
                    nextShoulderGesture = Time.unscaledTime + gestureCooldown.Value;
                    FireGesture("shoulder", handLocal, () => playerInput.InvokeSwitchGun(1),
                        inHipZone ? $"hip also in range at {hipDistance:0.##} m" : "");
                }
                else if (!shoulderWins && Time.unscaledTime >= nextHipGesture)
                {
                    nextHipGesture = Time.unscaledTime + gestureCooldown.Value;
                    FireGesture("hip", handLocal,
                        () => playerInput.InvokeSwitchNozzleGroup(1),
                        inShoulderZone
                            ? $"shoulder also in range at {shoulderDistance:0.##} m"
                            : "");
                }
            }

            // CARRYING WINS on the left grip: while something is held, that grip
            // is the rotation modifier, and taking it away would make the
            // existing controls worse - which this task must not do.
            //
            // carrying is a frame behind here, because ReportHeldItem sets it
            // later in the frame than this method runs. That is deliberate and
            // harmless: picking an item up and reaching for the washer within
            // one frame is not a thing a hand does.
            if (washerGrip.Tap && inWasherZone && !carrying
                && Time.unscaledTime >= nextWasherGesture)
            {
                nextWasherGesture = Time.unscaledTime + gestureCooldown.Value;
                FireGesture("washer", washerLocal,
                    () => playerInput.InvokeSwitchExtension(1), "");
            }

            if (Dev(zoneDebug) && Time.unscaledTime >= nextZoneDebug)
            {
                nextZoneDebug = Time.unscaledTime + 0.5f;
                LoggerInstance.Msg($"zone hand: local {Vector(handLocal)}"
                    + $"   shoulder {shoulderDistance:0.##}/{shoulderZoneRadius.Value:0.##}"
                    + $" {(inShoulderZone ? "IN" : "out")}"
                    + $"   hip {hipDistance:0.##}/{hipZoneRadius.Value:0.##}"
                    + $" {(inHipZone ? "IN" : "out")}"
                    + $"   washer {washerLocal.magnitude:0.##}/{washerZoneRadius.Value:0.##}"
                    + $" {(inWasherZone ? "IN" : "out")}"
                    + $"   torsoYaw {torsoYaw:0.#}");
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  body zones threw {exception.GetType().Name}: "
                + exception.Message);
        }
    }

    // Enter and exit only, so a zone the hand is resting in costs nothing. The
    // local coordinates ride along on the transition, which is what makes the
    // radii adjustable without the per-frame line.
    private void UpdateZone(string name, bool inside, ref bool state, Vector3 local)
    {
        if (inside == state)
            return;

        state = inside;
        LoggerInstance.Msg($"zone: {name} {(inside ? "ENTER" : "EXIT")}   "
            + $"local {Vector(local)}");
    }

    // The configuration BEFORE and AFTER, exactly as the three stick-click
    // handlers report it. That makes one run answer both questions at once: was
    // the gesture recognised, and did the PWS2 call change anything. A gesture
    // that fires while the configuration stands still is a fact about the game's
    // route, not about the gesture.
    // Ein Puls, und die Preference-Pruefung sitzt HIER statt an sechs
    // Aufrufstellen. Haptics.Pulse selbst haelt die Bruecke und die Zaehler.
    // WELCHE SEITE DIE PISTOLENHAND IST - Abschnitt 110.
    //
    // node ist die Wahl aus der Preference Hand. Alles Haptische haengt an
    // dieser einen Frage, und keine Aufrufstelle traegt mehr eine Konstante:
    // die Tabelle aus Abschnitt 102 hatte die Seiten eines Rechtshaenders
    // eingetragen, wo die ROLLE gemeint war.
    private bool WasherHandRight => node == XRNode.RightHand;

    private bool Buzz(bool right, string why)
        => Buzz(right, hapticAmplitude.Value, hapticSeconds.Value, why);

    // DIE STAERKE ALS PARAMETER, damit der zweite Puls schwaecher sein kann -
    // und zwar auf DEMSELBEN Weg.
    //
    // Ein eigener Pfad direkt an Haptics.Pulse waere der Fehler: die
    // Pistolenhand muss durch die Warteschlange, sonst ueberschreibt die
    // naechste Nachsendung des Dauerpulses den Echopuls nach Millisekunden.
    // Beim Umschalten der Verlaengerung kann der Strahl gerade laufen, also
    // ist das nicht der Ausnahmefall, sondern der Normalfall.
    //
    // GEKLAMMERT WIRD HIER, nicht beim Aufrufer: Haptics.Pulse klammert nicht,
    // und ein Faktor ist eine cfg-Zahl, die auch 2 sein kann.
    private bool Buzz(bool right, float amplitude, float seconds, string why)
    {
        if (!haptics.Value)
            return false;

        amplitude = Mathf.Clamp01(amplitude);

        // Die zwei Schalter bei jedem Puls durchgereicht, nicht einmal beim
        // Start: so wirkt eine Aenderung in der cfg beim naechsten Puls und
        // nicht erst beim naechsten Spielstart. Zwei Feldschreibungen kosten
        // nichts, und Pulse ist ein Ereignis, kein Frame-Pfad.
        Haptics.Configure(LoggerInstance, hapticFrequency.Value, hapticUnfiltered.Value);

        // DIE PISTOLENHAND GEHT DURCH DIE WARTESCHLANGE, sobald die
        // Strahl-Haptik an ist - und das ist kein Umweg, sondern die
        // Bedingung dafuer, dass ein Gesten-Puls waehrend des Spruehens
        // ueberhaupt zu fuehlen ist. Direkt gesendet wuerde ihn die naechste
        // Nachsendung des Dauerpulses nach Millisekunden ueberschreiben.
        //
        // Die freie Hand bleibt direkt: dort laeuft kein Dauerpuls, also gibt
        // es nichts zu ueberlagern.
        if (right == WasherHandRight && sprayHapticsOn.Value)
        {
            sprayHaptics.Queue(amplitude, seconds);

            // "Eingereiht" ist nicht "gesendet". Der Begruessungspuls fragt
            // nach dem Senden, und die Antwort darf ihn nicht vorzeitig
            // beruhigen.
            //
            // BEANTWORTET WIRD SIE VON DER FREIEN HAND, nicht "von links" -
            // seit Abschnitt 110 haengt diese Weiche an der Rolle. Der
            // Begruessungspuls ruft beide Seiten, und genau die eine, die
            // nicht die Pistolenhand ist, nimmt den direkten Weg und liefert
            // ein echtes Ergebnis. Das gilt in beiden Haendigkeiten.
            return false;
        }

        return Haptics.Pulse(LoggerInstance, right, amplitude, seconds, why);
    }

    // Die zwei Ausgaenge der Strahl-Haptik. Sie liegen hier und nicht in
    // SprayHaptics, damit dort keine Preference und keine Bruecke vorkommt.
    private void SendSprayPulse(float amplitude, float seconds)
    {
        if (!haptics.Value)
            return;

        Haptics.Configure(LoggerInstance, hapticFrequency.Value, hapticUnfiltered.Value);
        Haptics.Pulse(LoggerInstance, WasherHandRight, amplitude, seconds, "spray");
    }

    private void StopSprayPulse() => Haptics.Stop(LoggerInstance, WasherHandRight);

    // TRIFFT DER STRAHL ETWAS? Ein eigener Strahl, und zwar entlang DERSELBEN
    // Richtung, die das Spiel zum Waschen bekommt - publishedAimForward, von
    // DriveRay festgehalten. Eine zweite Richtung aus einer zweiten Quelle
    // waere eine neue Fehlerquelle statt einer Messung.
    //
    // Die Ueberladung ohne RaycastHit ist der ganze Trick: zwei Vector3
    // hinein, eine Reichweite, eine Ebenenmaske, ein bool heraus. Die
    // Struct-Sperre dieses Projekts gilt fuer Bounds, Ray und RaycastHit -
    // nicht fuer einen Vector3 als Argument, was seit Lauf 0 belegt ist.
    //
    // Trigger werden ignoriert. Sonst zaehlte jede Interaktionszone als
    // Oberflaeche, und die sind gross - der Wickeltisch aus Lauf 2 misst allein
    // 45 cm in x.
    private bool ProbeSprayContact()
    {
        if (!aimPublished)
            return false;

        try
        {
            var direction = publishedAimForward;

            if (direction.sqrMagnitude < 0.0001f)
                return false;

            var origin = publishedAimOrigin + (direction * hapticContactSkip.Value);
            var range = Mathf.Max(0.1f, hapticContactRange.Value);

            return Physics.Raycast(origin, direction, range, hapticContactMask.Value,
                QueryTriggerInteraction.Ignore);
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  contact ray threw {exception.GetType().Name}: "
                + exception.Message);

            return false;
        }
    }

    // EINMAL PRO FRAME, hinter DriveRay und damit hinter washProbe.Sample:
    // IsWashing und die Trefferdistanz kommen aus DEMSELBEN Block wie die
    // Waschrichtung. Ein zweiter Abgriff waere ein anderer Zwischenstand.
    //
    // menuMode nimmt die Vibration mit: ein brummender Controller im Menue
    // waere ein Defekt, und IsWashing kann dort noch einen Frame nachhaengen.
    private void DriveSprayHaptics()
    {
        if (sprayHapticSettings is null)
            return;

        sprayHaptics.Drive(LoggerInstance, sprayHapticSettings, equipment,
            washProbe.IsWashing, washProbe.CrosshairHitDistance,
            washProbe.TurboRotation,
            GameInput.Active && haptics.Value && !menuMode);
    }

    // JEDE AKTIVE KAMERA UND JEDER CANVAS, einmal.
    //
    // Die Spalten sind die, aus denen sich ein Bild-in-Bild erklaeren laesst:
    //
    //   depth        die Reihenfolge, in der Kameras ins selbe Ziel zeichnen
    //   clearFlags   wer den Puffer loescht - und wer das eben NICHT tut, denn
    //                ein nicht geloeschtes Fenster behaelt den Vorframe
    //   targetTexture  eine Kamera, die in eine Textur zeichnet, die jemand
    //                  anders als Bild zeigt: genau die Form einer
    //                  Verschachtelung
    //   rect         ein Viewport kleiner als das Fenster ist ein Bild IM Bild,
    //                wortwoertlich
    //   stereoTargetEye  wer ueberhaupt in den Augenpuffer geht
    //
    // Fuer Canvas dasselbe in seiner Sprache: renderMode, worldCamera,
    // sortingOrder und die Pixelgroesse.
    private void ReportSurfaces()
    {
        if (surfaceProbeDone || !Dev(surfaceProbe)
            || Time.unscaledTime < surfaceProbeAt)
            return;

        surfaceProbeDone = true;

        try
        {
            var cameras = Resources.FindObjectsOfTypeAll(
                Il2CppInterop.Runtime.Il2CppType.Of<Camera>());

            var listed = 0;

            for (var index = 0; index < cameras.Length; index++)
            {
                var camera = cameras[index]?.TryCast<Camera>();

                if (camera is null || camera == null)
                    continue;

                // NUR die, die wirklich zeichnen. Eine deaktivierte Kamera
                // erklaert kein Bild auf dem Schirm, und die Szene hat drei
                // davon - Third Person und TimeLapse sind aus.
                if (!camera.enabled || !camera.gameObject.activeInHierarchy)
                    continue;

                listed++;

                var target = camera.targetTexture;
                var targetText = target is null || target == null
                    ? "none (the window)"
                    : $"\"{target.name}\" {target.width}x{target.height}";

                LoggerInstance.Msg($"  surface camera [{listed}] \"{camera.name}\""
                    + $"   depth {camera.depth:0.#}"
                    + $"   clear {camera.clearFlags}"
                    + $"   target {targetText}"
                    + $"   rect {camera.rect.x:0.##},{camera.rect.y:0.##} "
                    + $"{camera.rect.width:0.##}x{camera.rect.height:0.##}"
                    + $"   eye {camera.stereoTargetEye}"
                    + $"   mask 0x{camera.cullingMask:x8}"
                    + $"   fov {camera.fieldOfView:0.#}");
            }

            LoggerInstance.Msg($"  surface: {listed} active camera(s) of {cameras.Length}");

            var canvases = Resources.FindObjectsOfTypeAll(
                Il2CppInterop.Runtime.Il2CppType.Of<Canvas>());

            var shown = 0;

            for (var index = 0; index < canvases.Length; index++)
            {
                var canvas = canvases[index]?.TryCast<Canvas>();

                if (canvas is null || canvas == null)
                    continue;

                if (!canvas.enabled || !canvas.gameObject.activeInHierarchy)
                    continue;

                // NUR WURZEL-CANVASSE. Die Szene hat 49, und die
                // verschachtelten erben Modus und Kamera vom Wurzelknoten -
                // sie alle zu listen waere eine Seite Log fuer dieselbe
                // Auskunft.
                if (!canvas.isRootCanvas)
                    continue;

                shown++;

                var worldCamera = canvas.worldCamera;
                var rect = canvas.pixelRect;

                LoggerInstance.Msg($"  surface canvas [{shown}] \"{canvas.name}\""
                    + $"   mode {canvas.renderMode}"
                    + $"   camera {(worldCamera is null || worldCamera == null ? "none" : $"\"{worldCamera.name}\"")}"
                    + $"   plane {canvas.planeDistance:0.##}"
                    + $"   sorting {canvas.sortingLayerName}/{canvas.sortingOrder}"
                    + $"   pixels {rect.width:0}x{rect.height:0}"
                    + $"   layer {canvas.gameObject.layer}");
            }

            LoggerInstance.Msg($"  surface: {shown} active root canvas(es) of {canvases.Length}"
                + $"   screen {Screen.width}x{Screen.height}"
                + $"   eyeTexture {UnityEngine.XR.XRSettings.eyeTextureWidth}"
                + $"x{UnityEngine.XR.XRSettings.eyeTextureHeight}"
                + $"   mirror {UnityEngine.XR.XRSettings.gameViewRenderMode}");
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  surface probe threw {exception.GetType().Name}: "
                + exception.Message);
        }
    }

    // DER BEGRUESSUNGSPULS, UND WARUM ER WARTET.
    //
    // Er lief in Lauf 3 ganze 26 ms vor XRBoots Setup: die Vibrate-Action
    // existierte noch nicht, der Puls wurde abgelehnt, und im Log stand
    // "action NOT ready" - eine eigene Ursache, die nichts mit dem stummen
    // Controller zu tun hat. Ohne diese Trennung waere sie in ihn
    // hineingerechnet worden.
    //
    // Zehn Sekunden Geduld, halbsekuendlich, dann eine Zeile und Ruhe. Ein
    // Nachweis, der endlos auf sich warten laesst, ist selbst ein Defekt.
    private void DriveHapticGreeting()
    {
        if (!hapticGreetPending || Time.unscaledTime < nextHapticGreet)
            return;

        nextHapticGreet = Time.unscaledTime + 0.5f;

        if (Time.unscaledTime > hapticGreetUntil)
        {
            hapticGreetPending = false;
            LoggerInstance.Msg("haptics: no pulse within ten seconds of switching on. "
                + "See XR Boot's \"haptic bind:\" lines for the bound source per device.");
            return;
        }

        var left = Buzz(false, "driver on");
        var right = Buzz(true, "driver on");

        if (!left && !right)
            return;

        hapticGreetPending = false;
        LoggerInstance.Msg("haptics: greeting pulse sent to both hands.");
    }

    private void FireGesture(string name, Vector3 local, Action raise, string note)
    {
        zoneSuppressUntil = Time.unscaledTime + 0.3f;

        var before = DescribeConfiguration();
        raise();

        // DIE HANDELNDE HAND, ueber ihre ROLLE statt ueber eine Seite.
        //
        // Schulter und Huefte sind Gesten der PISTOLENHAND, die
        // Verlaengerungs-Geste fuehrt die FREIE Hand an die Pistole. Bis
        // Abschnitt 110 stand hier "rechts" beziehungsweise "links" - richtig
        // fuer einen Rechtshaender und vertauscht fuer jeden anderen, genau so
        // gemeldet.
        var washerHandGesture = !string.Equals(name, "washer", StringComparison.Ordinal);

        Buzz(washerHandGesture == WasherHandRight, $"gesture {name}");

        // DIE MITHALTENDE HAND, und nur bei der zweihaendigen Geste.
        //
        // washerHandGesture false heisst: die FREIE Hand hat gegriffen, und
        // zwar an die Pistole. Beide Haende sind dann an einer Sache, also
        // pulsen beide. Die Seite des Echos kommt aus der Rolle - eine
        // Konstante stuende hier fuer Linkshaender verkehrt, genau der Fehler
        // aus Abschnitt 110.
        var echo = !washerHandGesture && gestureEchoFactor.Value > 0f;

        if (echo)
            Buzz(WasherHandRight, hapticAmplitude.Value * gestureEchoFactor.Value,
                hapticSeconds.Value, $"gesture {name} echo");

        // "hands both" STEHT IN DERSELBEN ZEILE wie die Geste, nicht in einer
        // eigenen. Ein zweiter Satz waere eine zweite Momentaufnahme, und die
        // Paarung zweier Logzeilen hat in diesem Projekt schon Diagnosen
        // gekostet. So beweist ein Lauf beides auf einmal: die Geste sass, und
        // der zweite Puls ging mit.
        LoggerInstance.Msg($"gesture {name}: fired   local {Vector(local)}   "
            + $"before {before}   after {DescribeConfiguration()}"
            + $"   hands {(echo ? "both" : "acting only")}"
            + (string.IsNullOrEmpty(note) ? "" : $"   ({note})"));
    }

    // Both tracked-point reads go through ReadValueAsObject and Unbox, the shape
    // the whole mod uses: no generic instantiation across the interop boundary,
    // and no UnityEngine.XR call that returns a struct by value. The reasons are
    // at ReadPose and cost sections 36 and 45 to learn.
    private static bool TryReadTrackedPoint(InputAction? action, out Vector3 point)
    {
        point = Vector3.zero;

        if (action is null)
            return false;

        try
        {
            var raw = action.ReadValueAsObject();

            if (raw is null)
                return false;

            point = raw.Unbox<Vector3>();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadRotation(InputAction? action, out Quaternion rotation)
    {
        rotation = Quaternion.identity;

        if (action is null)
            return false;

        try
        {
            var raw = action.ReadValueAsObject();

            if (raw is null)
                return false;

            rotation = raw.Unbox<Quaternion>();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void DriveButtons()
    {
        if (playerInput is null || playerInput == null)
            return;

        try
        {
            // WHILE A MENU IS OPEN only the menu button survives, as Cancel.
            //
            // Everything else here would fire into a world the player cannot see:
            // X is a world interaction, Y would toggle the task list behind the
            // menu, and the spray latch would leave the washer running. The
            // edges are still polled so no press is held across the transition
            // and fires on the way out.
            //
            // A and the right trigger are the exception, and they are read in
            // the navigation code rather than here - that is where accept lives.
            if (menuMode)
            {
                jumpButton.Poll(false, 0f);
                stanceButton.Poll(false, 0.4f);
                // X USED TO CONFIRM HERE and was therefore polled live. Accept
                // moved to A, so X has no menu role left and is suppressed like
                // the rest: a press meant for a menu must not reach the world
                // interaction on the way out.
                interactButton.Poll(false, 0f);
                // Still false for the GAMEPLAY edges - dirt highlight and the
                // spray latch must not fire from a menu - while DriveMenuTabs
                // reads the same two grips through its own edge detectors.
                dirtButton.Poll(false, 0f);
                sprayLatchButton.Poll(false, 0f);
                menuButton.Poll(ButtonEdge.IsDown(leftMenu), 0f);

                // Y STAYS LIVE, and disabling it was the defect.
                //
                // The timestamps say why: "task list toggled" at 17:19:27.129 is
                // followed by "menu mode: ON" at 17:19:27.160. Opening the task
                // list sets AllowsPlayerMovement false, so the list IS the menu
                // mode - and switching Y off inside that mode meant the button
                // that opened the list could no longer close it, while the menu
                // button still could. "The menu takes every input" was too
                // coarse: the button that opens a thing has to keep closing it.
                //
                // Hold threshold zero here, unlike the gameplay path: the
                // furniture inventory on the hold half is meaningless with a menu
                // already open, and a tap landing instantly reads better for a
                // close.
                taskButton.Poll(ButtonEdge.IsDown(leftSecondary), 0f);

                if (taskButton.Tap && !TrySubmitCloseButton())
                    playerInput.InvokeToggleTaskList();

                if (menuButton.Tap)
                    PressMenuButton("menu");

                // A menu takes the sticks and the buttons, so the chord can
                // no longer be released - and a frozen washer with no way to
                // unfreeze it would look like the mod had died. Dropped rather
                // than solved: a calibration nobody finished should not write.
                if (calibrating)
                {
                    calibrating = false;
                    calibArmRequested = false;
                    calibSolveRequested = false;
                    calibHeldLast = false;
                    LoggerInstance.Msg("calibrate: dropped, a menu took the input");
                }

                buttonStatus = "buttons: menu open";
                return;
            }

            // Hold thresholds of zero mean "no hold half", so the tap fires on
            // press and feels instant. Jump and Interact must never wait.
            jumpButton.Poll(ButtonEdge.IsDown(rightPrimary), 0f);
            interactButton.Poll(ButtonEdge.IsDown(leftPrimary), 0f);
            menuButton.Poll(ButtonEdge.IsDown(leftMenu), 0f);
            dirtButton.Poll(ButtonEdge.ReadAxis(leftSqueeze) > 0.6f, 0f);
            sprayLatchButton.Poll(ButtonEdge.ReadAxis(rightSqueeze) > 0.6f, 0f);

            // These two carry a hold half, so their taps land on release.
            stanceButton.Poll(ButtonEdge.IsDown(rightSecondary), 0.4f);
            taskButton.Poll(ButtonEdge.IsDown(leftSecondary), 0.4f);

            // AFTER the polls, so the edges stay consistent, and BEFORE the
            // consumers below, which check CalibrateSuppressed. The three
            // buttons of the chord all have their own jobs - dirt highlight,
            // interact, task list - and every one of them has to stay quiet
            // while the chord is being held, or calibrating would also throw
            // the player into the task list.
            ReadCalibrateHold();

            // Jump takes a bool, and the two events Jump and JumpReleased plus
            // one helper leave only one sensible reading.
            if (jumpButton.Pressed)
                playerInput.InvokeJump(true);

            if (jumpButton.Released)
                playerInput.InvokeJump(false);

            if (stanceButton.Tap)
            {
                playerInput.InvokeCrouchPressed();
                LoggerInstance.Msg("stance: pressed");
            }

            if (stanceButton.Hold)
            {
                playerInput.InvokeCrouchLongPressed();
                LoggerInstance.Msg("stance: long pressed");
            }

            if (interactButton.Tap && !CalibrateSuppressed())
            {
                // A TOGGLE NOW, driven by the state machine rather than by one
                // fixed verb.
                //
                // The old code always sent PickUp, on the assumption that the
                // game resolves the verb per target object. Section 74 recorded
                // that as an unconfirmed hypothesis; this session broke it -
                // objects could be picked up and never put down, because the
                // second press simply repeated PickUp.
                //
                // carrying comes from PlayerInteractionManager's own state
                // label, measured as HoldingItemInteractionState while a step
                // ladder was held.
                var before = InteractionText();

                if (carrying)
                {
                    PlaceCarried();

                    LoggerInstance.Msg($"interact: place/{placeVerb.Value}"
                        + $"  before {before}  after {InteractionText()}");
                }
                else if (!TryAimPickup(before))
                {
                    // DIE PISTOLE ZEIGT INS LEERE, also bleibt das Blickziel des
                    // Spiels zustaendig - dieser Pfad ist unveraendert, und dass
                    // er unberuehrt bleibt, war die Abnahmebedingung.
                    if (UsesManagerRoute())
                    {
                        // Both halves through the manager on this route, because that
                        // is what one keyboard key doing both implies. Falls back to
                        // the BaseInput event if the manager has not resolved yet.
                        if (interaction is not null && interaction != null)
                            interaction.OnInteractInput(Il2CppFuturLab.PW2.ItemInteraction.PickUp);
                        else
                            playerInput.InvokeItemInteraction(
                                Il2CppFuturLab.PW2.ItemInteraction.PickUp);
                    }
                    else
                    {
                        playerInput.InvokeItemInteraction(
                            Il2CppFuturLab.PW2.ItemInteraction.PickUp);
                    }

                    LoggerInstance.Msg($"interact: pickup/{placeVerb.Value}"
                        + $"  before {before}  after {InteractionText()}");
                }
            }

            if (taskButton.Tap && !CalibrateSuppressed())
            {
                // Y CLOSES IT TOO, which is what the user asked for and the more
                // understandable behaviour: the button that opened a thing should
                // close it. Same chain as the menu button - the interactable
                // context button is the measured close target - with the toggle
                // as the fallback when nothing is open.
                if (TrySubmitCloseButton())
                {
                    LoggerInstance.Msg("task list: closed via the context button (Y)");
                }
                else
                {
                    playerInput.InvokeToggleTaskList();
                    LoggerInstance.Msg("task list toggled");
                }

            }

            if (taskButton.Hold && !CalibrateSuppressed())
            {
                playerInput.InvokeToggleFurnitureInventory();
                LoggerInstance.Msg("furniture inventory toggled");
            }

            // Suppressed while carrying: the left grip is the rotation modifier
            // then, and a dirt highlight on every release of it would fire
            // constantly while placing something.
            // The washer gesture sits on this same left grip, so it is suppressed
            // here for the same reason the calibration chord is.
            if (dirtButton.Tap && !carrying && !CalibrateSuppressed()
                && !ZoneGestureSuppressed())
            {
                playerInput.InvokeHighlightDirt();
                LoggerInstance.Msg("dirt highlight");
            }

            // THE GRIP IS IGNORED WHILE THE TRIGGER IS HELD - section 91 B.
            //
            // Reported: spraying with the right trigger held often sticks, as if
            // the continuous mode had been switched on with the grip, and then
            // the jet cannot be turned off. Section 91 named the mechanism: whoever
            // pulls the trigger hard tenses the whole hand and squeezes the grip
            // with it, crossing the 0.6 threshold.
            //
            // THE TAP IS IGNORED, the Poll is NOT suppressed, and the difference
            // matters. Feeding the poll false while the trigger is held would
            // make RELEASING the trigger with the grip still squeezed look like a
            // fresh down edge - and latch for certain, which is the opposite of
            // the fix. Ignoring the tap swallows a grip press that began during a
            // trigger pull, and a real latch then needs a real release and a
            // fresh squeeze of the grip.
            //
            // READ LIVE, not from GameInput.FireHeld: DriveButtons runs before
            // ReadTrigger in OnLateUpdate, so that field still carries the
            // previous frame.
            var triggerHeld = sprayLatchLockout.Value && TriggerHeldNow();

            // AND NOT WHEN A BODY-ZONE GESTURE JUST TOOK THIS PRESS. The
            // shoulder and hip gestures sit on this same right grip, so without
            // this every tool change would also flip the continuous spray.
            // DIE KETTE IST AUSGESCHRIEBEN, statt das Umschalten als
            // Auffangzweig stehen zu lassen. Vier Gruende, einen Druck nicht
            // aufs Einrasten zu geben, und jeder nennt sich im Log - so sagt
            // ein Lauf, WELCHER Grund gegriffen hat, statt nur dass nichts
            // passierte.
            if (sprayLatchButton.Tap && LatchJustCleared())
            {
                // ABSICHTLICH LEER. Der Druck hat in DriveBodyZones das
                // Ausrasten bezahlt und ist dort protokolliert. Ohne diesen
                // Zweig faende der Zweig unten denselben Tap im selben Frame
                // und wuerde sofort neu einrasten.
            }
            else if (sprayLatchButton.Tap && GameInput.FireLatched)
            {
                // DIE ZUSICHERUNG, und sie steht bewusst VOR allen drei
                // Schluck-Zweigen: liegt die Rastung, raeumt jeder Griffdruck
                // sie. Ohne Ausnahme.
                //
                // Erreichbar wird dieser Zweig, wo DriveBodyZones nicht
                // geraeumt hat - bei abgeschalteten Gestenzonen, bei einem
                // Pose-Lesefehler in diesem Frame, oder wenn
                // SprayLatchGripClears aus ist. Fruehere Fassungen liessen
                // den Druck dann an "grip taken by a body-zone gesture" oder
                // an "trigger is held" verfallen, und der Strahl lief weiter.
                //
                // DER TRIGGER DARF HIER NICHT SPERREN. Die Sperre aus
                // Abschnitt 91 B verhindert das versehentliche EINrasten
                // waehrend eines Triggerzugs; sie spiegelbildlich auch aufs
                // AUSrasten zu legen, macht den Strahl unabschaltbar genau in
                // dem Moment, in dem man sprueht - dieselbe Fehlerklasse, die
                // dieser Abschnitt behebt.
                GameInput.FireLatched = false;

                if (sprayLatchBuzz.Value)
                    Buzz(WasherHandRight, "spray latch off");

                LoggerInstance.Msg("continuous spray off");
            }
            else if (sprayLatchButton.Tap && ZoneGestureSuppressed())
            {
                LoggerInstance.Msg("continuous spray: grip taken by a body-zone gesture");
            }
            else if (sprayLatchButton.Tap && triggerHeld)
            {
                // LOGGED, because a swallowed press is exactly the evidence
                // section 91 asked for: it says the grip really was crossing the
                // threshold during a trigger pull, which is the half of the
                // question the absence of "continuous spray ON" cannot answer.
                //
                // Abschnitt 111 hat sie gezaehlt: ZWEIMAL in einer Sitzung,
                // gegen 58 Zonen-Kollisionen. Die Sperre arbeitet, sie war nur
                // nie die Ursache.
                LoggerInstance.Msg("continuous spray: grip ignored, trigger is held");
            }
            else if (sprayLatchButton.Tap && !GameInput.FireLatched && LatchGuarded())
            {
                // DER FEHLGRIFF AN DER HUEFTE, und ab hier wird er verschluckt
                // statt zum Dauerstrahl.
                //
                // Die Bedingung traegt !FireLatched, und das ist die
                // Sicherheitseigenschaft dieser Zeile: der Waechter kann nur
                // das EINRASTEN sperren. Ein Waechter, der auch das Ausrasten
                // sperrt, waere genau der Fehler, der hier behoben wird.
                LoggerInstance.Msg("continuous spray: grip ignored, hand is at a "
                    + $"gesture zone   {LatchGuardReason()}");
            }
            else if (sprayLatchButton.Tap)
            {
                // NUR NOCH EINRASTEN. Jedes Ausrasten faengt der Zweig
                // oben ab, also waere ein Umschalten hier eine Zeile, die
                // etwas behauptet, was sie nicht mehr tut.
                GameInput.FireLatched = true;

                // DER PULS, damit ein stilles Einrasten nicht wieder moeglich
                // ist. "SPRAY LATCHED" steht nur im DevMode-Overlay und war im
                // Headset nie zu sehen - der Zustand war fuer den Spieler
                // unsichtbar, und das ist die dritte Haelfte des Fehlers.
                if (sprayLatchBuzz.Value)
                    Buzz(WasherHandRight, "spray latch on");

                LoggerInstance.Msg("continuous spray ON");
            }

            // CANCEL FIRST, menu toggle second. ESC does exactly this in the
            // flat game: it closes whatever is open, and opens the pause menu
            // only when nothing is. Cancel is attempted and reports whether it
            // landed, so one button covers both without a mode.
            // Ordered cheapest-and-safest first, and every step says in the log
            // whether it fired. The inventory runs on every press regardless, so
            // one run records what the task list actually puts on screen.
            // Close button first, because it is the measured target; Cancel
            // second, now that the EventSystem does sometimes hold a selection
            // again; the pause-menu toggle last. Each step logs whether it
            // fired, so the chain is readable from the log rather than inferred.
            if (menuButton.Tap)
                PressMenuButton("world");

            DriveSprint();

            buttonStatus = $"buttons ok{(GameInput.FireLatched ? "  SPRAY LATCHED" : "")}"
                + $"{(sprintHeld ? "  sprint" : "")}"
                // A constant, not an interpolation: section 87 lists the
                // per-frame status strings as the open performance item, and
                // this one adds no formatting work.
                + (calibrating ? "  CALIBRATING - move the hand, then let go" : "");
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  buttons threw {exception.GetType().Name}: {exception.Message}");
            buttonStatus = "buttons: failed";
        }
    }

    // Derived from stick deflection rather than a button, because the flat
    // game's Sprint sits on leftStickPress and /input/thumbstick/click is NOT
    // registered - adding it would need a suggest pass, which xrAttachActionSets
    // has already closed, so it would need a game restart. Full deflection is
    // free and reads naturally.
    //
    // Sprint is a plain settable bool on BaseInput and is written from the
    // game's own press and release handlers only, so a direct write should hold.
    // If it turns out to revert, the answer is a getter patch, the shape that
    // already carries Fire.
    // THE NOZZLE SCHEME, on the two stick clicks.
    //
    //   right stick Y        step within the current nozzle CATEGORY
    //   R3, right click      cycle the category: standard, turbo, soap, back
    //   L3, left click       cycle the available EXTENSIONS
    //
    // Nothing here holds a list of nozzles, categories or extensions, and that
    // is deliberate: the game's own cyclers carry the availability and unlock
    // logic. ConfigurationManager exposes SwitchToNextAvailableNozzleInGroup,
    // SwitchToNextAvailableNozzleGroup and SwitchToNextAvailableExtension, all
    // filtered through IsOwned(GameContent), and the data model behind them -
    // WasherConfiguration.CompatibleNozzleGroups as Dictionary<int,
    // List<NozzleData>>, NozzleGroupIds, CurrentNozzleGroup, CompatibleExtensions,
    // and NozzleTypeData.NozzleGroup as the category id of a single nozzle - was
    // read out of the assemblies rather than guessed.
    //
    // Driven through BaseInput's Invoke helpers rather than those cyclers
    // directly. EquipmentManager is a NetworkBehaviour with RotateNozzleRpc and
    // UpdateWashRayRpc, so equipment changes are plausibly replicated, and
    // calling ConfigurationManager straight would skip both the network and the
    // respawn. The Invoke route is also the project rule from CLAUDE.md: drive
    // what the game already reads.
    //
    // THE INT IS UNCONFIRMED for these two, and that is the measurement this run
    // carries. For InvokeSwitchNozzle it is field knowledge - plus one and minus
    // one step through the nozzles. But ConfigurationManager also holds
    // m_playerIndex and its Init takes an int player index, so a same-shaped int
    // is NOT automatically a direction. LogConfiguration prints the category id
    // and the nozzle name on either side of every call, so one run says whether
    // +1 advances the category, does nothing, or addresses player one.
    private void DriveNozzleScheme()
    {
        if (playerInput is null || menuMode)
            return;

        try
        {
            // R3 CARRIES A HOLD HALF NOW, which is what makes the washer
            // reachable without a new button.
            //
            // Measured reason. The right stick reaches nozzles 0, 15, 25 and 40,
            // all in group 0, and R3 reaches groups 0 and 2 only - group 1 never
            // appears, because IsOwned leaves out what is not owned. So the
            // turbo nozzle is not in the offered set at all, and the remaining
            // possibility is that it belongs to a different POWER WASHER rather
            // than to a nozzle group. InvokeSwitchGun is the call for that.
            //
            // The cost of a hold half is that the TAP now lands on release,
            // about 400 ms later. Acceptable here and nowhere near the spray
            // trigger: a nozzle category arriving on release reads as a
            // deliberate press, whereas a delayed jump or interact reads as a
            // swallowed input - which is why those two still have no threshold.
            groupButton.Poll(ButtonEdge.IsDown(rightStickClick), 0.4f);
            extensionButton.Poll(ButtonEdge.IsDown(leftStickClick), 0f);

            if (groupButton.Tap)
            {
                var before = DescribeConfiguration();
                playerInput.InvokeSwitchNozzleGroup(1);
                LoggerInstance.Msg($"nozzle group: R3 tap  before {before}  after {DescribeConfiguration()}");
            }

            // THE WASHER, on the hold - and the route is CONFIRMED, which this
            // comment used to leave open.
            //
            // It read "if nothing changes, InvokeSwitchGun is not the route",
            // written while that was still a hypothesis. It is the route: the
            // hold cycles Normal, Turbo and whatever further washers have been
            // unlocked. The int is a DIRECTION like every other Invoke helper
            // here - InvokeSwitchGun raises Action<int>, +1 is "next" and the
            // game wraps - so this is not a two-state toggle and nothing may
            // treat it as one.
            //
            // Left standing, the old wording sent a later reader looking for a
            // different lever that does not exist.
            if (groupButton.Hold)
            {
                var before = DescribeConfiguration();
                playerInput.InvokeSwitchGun(1);
                LoggerInstance.Msg($"washer: R3 hold  before {before}  after {DescribeConfiguration()}");
            }

            if (extensionButton.Pressed)
            {
                var before = DescribeConfiguration();
                playerInput.InvokeSwitchExtension(1);
                LoggerInstance.Msg($"extension: L3  before {before}  after {DescribeConfiguration()}");
            }
        }
        catch (Exception exception)
        {
            // Dropped rather than retried, like NozzleKeys: the player is
            // rebuilt per job and playerInput re-resolves on its own.
            playerInput = null;
            LoggerInstance.Warning($"  nozzle scheme threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    // The category id and the nozzle name, and nothing that crosses a list.
    //
    // NozzleGroupIds is a List<int> and CompatibleNozzleGroups a
    // Dictionary<int, List<NozzleData>>, both Il2CppSystem collections - and
    // section 36 records the BlittableListWrapper marshalling layer in this
    // build as defective. Neither is touched. NozzleData and NozzleTypeData are
    // plain class references, NozzleGroup is an int and ShortName a string, so
    // every read here is a safe shape.
    private string DescribeConfiguration()
    {
        try
        {
            var manager = equipment is null || equipment == null
                ? null
                : UnityEngine.Object.FindObjectOfType<Il2CppFuturLab.PW2.EquipmentManager>();

            var configuration = manager?.ConfigurationManager?.CurrentConfiguration;

            if (configuration is null)
                return "no configuration";

            var nozzle = configuration.Nozzle;
            var extension = configuration.Extension;

            var group = nozzle?.NozzleType is null ? -1 : nozzle.NozzleType.NozzleGroup;
            var nozzleName = nozzle?.NozzleType?.ShortName ?? "none";
            // Via ExtensionType, the same shape the nozzle uses via NozzleType.
            // ShortName is inherited from BaseEquipmentTypeData and is a plain
            // string; ExtensionName is a LocalizedString and is deliberately not
            // touched, because a localisation object across this interop is an
            // untested shape and a diagnostic is never worth a crash.
            var extensionName = extension?.ExtensionType?.ShortName ?? "none";

            // The WASHER is logged too, and it is the field that decides the
            // turbo question.
            //
            // Measured: the right stick reaches nozzles 0, 15, 25 and 40, all in
            // group 0, plus Soap in group 2. R3 reaches groups 0 and 2 only -
            // group 1 never appears, because IsOwned leaves out what is not
            // owned. So the control scheme is not the limit; the offered set is.
            //
            // That leaves two possibilities, and only this column separates
            // them: turbo sits in the unowned group 1, or turbo is a different
            // PowerWasher altogether - in which case the control needed is
            // InvokeSwitchGun rather than InvokeSwitchNozzleGroup.
            // The Unity object name. PowerWasherData has no ShortName, and its
            // Directory is STATIC - both probed and both wrong. The asset name
            // is a plain string on a ScriptableObject, the same shape already
            // relied on for renderer, material and shader names.
            var gun = configuration.PressureGun;
            var washer = gun is null || gun == null ? "none" : gun.name;

            return $"[washer {washer}  group {group}  nozzle {nozzleName}  ext {extensionName}]";
        }
        catch (Exception exception)
        {
            return $"read threw {exception.GetType().Name}";
        }
    }

    private string heldItemState = "";
    private Il2CppFuturLab.PW2.PlayerInteractionManager? interaction;
    private float nextInteractionSearch;

    // Set every frame from the state machine's own label, which the run of
    // 0.66.0 measured as "HoldingItemInteractionState" while a step ladder was
    // carried. The name is the reliable indicator: the ladder does not land in
    // m_physicsItemHolder, so Item read "none" the whole time it was held.
    private bool carrying;

    // THE CARRY STATE, for two of the reported problems at once.
    //
    // "Objects can be picked up but not put down with the same key" is the
    // hypothesis section 74 flagged as unconfirmed: InvokeItemInteraction is
    // called with a FIXED PickUp verb, on the assumption that the game resolves
    // the verb per target object. If it does not, a second press repeats PickUp
    // and nothing is placed - which is exactly what was reported.
    //
    // ItemInteraction turns out to hold None, PickUp, Use, Rotate and Remove,
    // and BaseInput has InvokeCancelInteraction(ItemInteraction) beside
    // InvokeItemInteraction. So the fix is a verb chosen from the state rather
    // than a constant - but it needs to know whether something is held, and this
    // is the read that settles whether that is knowable.
    //
    // PlayerInteractionState is a CLASS, not a struct - probed, not assumed - so
    // reading it crosses no struct boundary. Its Item is a MovableItemBase, also
    // a class. Logged only on CHANGE, so carrying something for a minute costs
    // one line rather than sixty.
    // HOW FAR THE GAME REACHES FOR A PICKUP, raised for VR standing height.
    //
    // Reported: a low step ladder gives no interaction prompt standing, none
    // crouching, and only works after lying down with two presses of B.
    //
    // A HYPOTHESIS THAT DIED BEFORE IT COST ANYTHING, and it is worth recording
    // because it would have led into DriveHead: that the camera transform does
    // not pitch in this mod, so the probe ray stays level and never reaches the
    // floor. It does pitch - DriveHead's own comment says "HeadTurn carries yaw
    // only, PlayerCamera carries PITCH only". Looking down works.
    //
    // WHAT IS LEFT fits every detail including "crouching is not enough":
    // PlayerCameraInteractionSelector raycasts from the camera up to
    // m_pickupDistance, and in VR the camera sits at the player's REAL height
    // because DriveHeadPosition writes the HMD position onto HeadTurn. The
    // straight line from eye height to something lying on the floor is longer
    // than the flat game is built for. Lying down shortens that line a lot,
    // crouching only a little.
    //
    // ONLY RAISED, never lowered, and the log carries BOTH numbers - so this
    // change is also the measurement that can refute it: if the game's own value
    // is already above the minimum, the distance was never the cause and the
    // suspicion moves on to m_pickupItemMask or the ladder's collider.
    //
    // RE-APPLIED PER SELECTOR INSTANCE, not once per session: the player is
    // rebuilt per job. Compared by native Pointer, because Il2CppInterop hands
    // out a fresh managed wrapper on every access and ReferenceEquals between
    // two reads of one object is therefore always false - the defect that cost
    // section 90 its menu navigation.
    private IntPtr reachSelector;

    private void DriveInteractionReach()
    {
        try
        {
            var manager = interaction;

            if (manager is null || manager == null)
                return;

            var selector = manager.InteractionSelector
                ?.TryCast<Il2CppFuturLab.PW2.PlayerCameraInteractionSelector>();

            if (selector is null || selector == null)
                return;

            if (selector.Pointer == reachSelector)
                return;

            reachSelector = selector.Pointer;

            var game = selector.m_pickupDistance;
            var wanted = pickupDistanceMin.Value;
            var camera = Camera.main;
            var height = camera is null || camera == null
                ? 0f
                : camera.transform.position.y;

            if (game >= wanted)
            {
                LoggerInstance.Msg($"interaction: pickupDistance game {game:0.##} m"
                    + $" -> left alone, already above {wanted:0.##}"
                    + $"   camera height {height:0.##} m");
                return;
            }

            selector.m_pickupDistance = wanted;

            LoggerInstance.Msg($"interaction: pickupDistance game {game:0.##} m"
                + $" -> raised to {selector.m_pickupDistance:0.##} m (PickupDistanceMin)"
                + $"   camera height {height:0.##} m");
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  interaction reach threw "
                + $"{exception.GetType().Name}: {exception.Message}");
            reachSelector = IntPtr.Zero;
        }
    }

    // WHY A LOW OBJECT IS NOT FOUND - and the two obvious answers are already
    // dead, which is why this is a measurement and not a third fix.
    //
    //   DISTANCE: measured game 1000 m. The reach was never the limit, and
    //            PickupDistanceMin correctly left it alone.
    //   PITCH:   DriveHead writes the full HMD rotation minus yaw onto the
    //            camera - Quaternion.Inverse(yawOnly) * rotation - with no clamp
    //            anywhere. Looking down really does pitch the camera.
    //
    // WHAT IS LEFT, and this line is built to separate them:
    //   the camera pitch, so "is the camera even looking down" stops being an
    //      assumption;
    //   the camera's world height, since only the stance changed the outcome;
    //   the game's own interaction state, so a target that IS found but not
    //      offered looks different from one that is never found;
    //   m_pickupItemMask, the only piece of the selector's geometry left.
    //
    // The mask is read in its own try/catch: LayerMask is a struct returned by
    // value, and while a four-byte struct is not the family that killed the
    // process in section 36, this project does not find out the hard way.
    private float nextReachReport;

    private void ReportInteractionReach()
    {
        if (!Dev(zoneDebug) || Time.unscaledTime < nextReachReport)
            return;

        nextReachReport = Time.unscaledTime + 0.5f;

        try
        {
            var manager = interaction;

            if (manager is null || manager == null)
                return;

            var selector = manager.InteractionSelector
                ?.TryCast<Il2CppFuturLab.PW2.PlayerCameraInteractionSelector>();

            var camera = Camera.main;

            if (camera is null || camera == null || selector is null || selector == null)
                return;

            var t = camera.transform;
            // Signed pitch: eulerAngles gives 0..360, and "looking down" has to
            // come out negative rather than as 315.
            var pitch = t.eulerAngles.x;

            if (pitch > 180f)
                pitch -= 360f;

            var mask = "unread";

            try
            {
                mask = selector.m_pickupItemMask.value.ToString();
            }
            catch (Exception maskException)
            {
                mask = maskException.GetType().Name;
            }

            LoggerInstance.Msg($"reach: pitch {pitch:0.#} deg"
                + $"   eye {Vector(t.position)}"
                + $"   forward {Vector(t.forward)}"
                + $"   reach {selector.m_pickupDistance:0.##} m"
                + $"   pickupMask {mask}"
                + $"   state {manager.InteractionState}");
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  reach report threw "
                + $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    // ZIELEN MIT DER PISTOLE STATT MIT DEM KOPF.
    //
    // Gemessen, 130 von 130 Zeilen: Camera.main.transform.forward hat y == 0.
    // Der Kamera-Transform ist dauerhaft waagerecht, weil die Kopfpose in der
    // View-Matrix lebt (Abschnitt 44) und nicht auf diesem Knoten. Der Strahl
    // von PlayerCameraInteractionSelector laeuft damit immer horizontal, und ein
    // Gegenstand am Boden wird nur getroffen, wenn das Auge tief genug sitzt -
    // weshalb allein das Liegen half. Die Reichweite ist unbeteiligt, das Spiel
    // greift bis 1000 m.
    //
    // Der Strahl selbst ist nativ. Also wird er nicht umgelenkt, sondern die Mod
    // bestimmt das Ziel und NENNT es dem Spiel: VR-Eingabe -> vorhandener
    // PWS2-Pfad -> vorhandene Spiellogik, dasselbe Prinzip wie Spruehen, Gesten
    // und Menue.
    //
    // DER WEG DAHIN WAR FALSCH GEWAEHLT, und drei Befunde sagen warum.
    //
    // ERSTENS IST SetTargetItem NATIV PRIVATE. Der Name des Interop-Feldes sagt
    // es woertlich: NativeMethodInfoPtr_SetTargetItem_Private_Void_
    // PlayerInteractableBase_0. Die Notiz "ist public" im Designdokument stammt
    // von der managed Signatur, die Il2CppInterop unabhaengig von der nativen
    // Sichtbarkeit oeffentlich stellt. Die Methode ist die interne Senke des
    // Managers, nicht seine Schnittstelle.
    //
    // ZWEITENS SCHREIBT DER MANAGER SIE SICH SELBST ZURUECK.
    // PlayerInteractionManager hat Update, FixedUpdate UND LateUpdate und holt
    // sich sein Ziel ueber das private IsLookingAtItem aus
    // PlayerCameraInteractionSelector.GetTargetItem - dem waagerechten Strahl.
    //
    // DRITTENS STAND DIE REIHENFOLGE DAGEGEN: DriveButtons laeuft in
    // OnLateUpdate sieben Anweisungen VOR dieser Messung. Im Frame des
    // X-Drucks wirkte also bestenfalls das Ziel des vorigen Frames.
    //
    // DESHALB SCHREIBT DIESE MESSUNG NICHTS MEHR, und sie heisst darum Report
    // und nicht Drive - die Trennung dieser Datei: Drive schreibt, Report misst.
    // Das Ziel wird jetzt im Moment des X-Drucks bestimmt und in einem Zug mit
    // dem Verb uebergeben, siehe TryAimPickup. Aufgenommen wird weiterhin von
    // PWS2 selbst.
    //
    // DIE REGEL BLEIBT: DIE PISTOLE GEWINNT, WENN SIE ETWAS FINDET, sonst wird
    // nichts angefasst. Zeigt die Pistole ins Leere, behaelt das Spiel sein
    // Blickziel - der heute funktionierende Fall bleibt unberuehrt, und das war
    // die Abnahmebedingung.
    //
    // OFFEN UND BENANNT: Hinweis und Highlight des Spiels haengen an
    // m_targetItem und bleiben damit am Blickziel. Die Aufnahme klappt, sieht
    // aber unbeschriftet aus. RefreshTargetAndUpdatePrompts ist public und der
    // naechste Posten - nicht in diesem Lauf, das waere eine zweite
    // Verhaltensaenderung.
    //
    // CanInteract wird GEFRAGT statt nachgebaut. Was aufnehmbar ist, weiss das
    // Spiel; diese Mod duplizert keine Werkzeuglogik.
    private readonly List<Il2CppFuturLab.PW2.PlayerInteractableBase> interactables = new();
    private float nextInteractableScan;
    private IntPtr aimTargetPointer;
    private float nextAimLog;

    // DIE ZIELSUCHE, ohne jede Nebenwirkung auf das Spiel: sie scannt nicht,
    // loggt nicht und schreibt nicht. Das entscheidet der Aufrufer - die
    // Messung liest die getaktete Liste, der X-Druck scannt frisch.
    //
    // Die out-Parameter sind MOD-EIGEN und queren die Interop-Grenze nicht. Die
    // Struct-Regel dieses Projekts gilt fuer Spielmethoden wie IsLookingAtItem,
    // nicht fuer eigenen Code; hier sorgen sie dafuer, dass Ziel, Abstand,
    // Entfernung und Verb AUS EINEM BLOCK kommen und in EINER Logzeile stehen
    // koennen.
    // DAS TOR EINES EINZELNEN KANDIDATEN, und es ist bewusst GETEILT: die
    // Entscheidung in FindAimTarget und der Bericht in ReportAimMiss rechnen
    // damit dieselbe Geometrie. Ein auf zweitem Weg gerechneter Bericht waere
    // eine neue Fehlerquelle statt einer Diagnose - dieselbe Begruendung, aus
    // der ReportPointerMiss dasselbe TryProjectRect benutzt wie der Zeiger.
    //
    // Ein leerer Rueckgabewert heisst bestanden.
    //
    // CanInteract gehoert NICHT hierher. Es wird heute nur dem Geometrie-Sieger
    // gestellt, und das pro Kandidat zu tun waere eine Verhaltensaenderung: ein
    // naeher liegender Kandidat, den das Spiel ablehnt, wuerde einen weiter
    // entfernten nicht mehr blockieren. Dieser Lauf misst, er aendert nichts.
    private string AimGate(Il2CppFuturLab.PW2.PlayerInteractableBase item,
        Vector3 origin, Vector3 forward, out float perp, out float ahead)
    {
        var toItem = item.transform.position - origin;
        ahead = Vector3.Dot(toItem, forward);

        // Senkrechter Abstand zur Strahllinie. Kein Raycast, also kein
        // RaycastHit - das ist die Struct-Familie, die dieses Projekt meidet,
        // und fuer "worauf zeige ich" reicht die Geometrie.
        //
        // IMMER gerechnet, auch hinter der Muendung, damit der Bericht fuer
        // jeden Kandidaten eine Zahl hat. Die Tore darunter stehen in der alten
        // Reihenfolge, also entscheidet sich nichts anders als vorher.
        perp = (toItem - (forward * ahead)).magnitude;

        if (ahead <= 0.1f)
            return "hinter der Muendung";

        if (ahead > aimInteractionRange.Value)
            return "zu weit";

        if (perp > aimInteractionRadius.Value)
            return "ausserhalb des Kegels";

        return string.Empty;
    }

    private Il2CppFuturLab.PW2.PlayerInteractableBase? FindAimTarget(
        out float perp, out float ahead, out Il2CppFuturLab.PW2.ItemInteraction kind,
        out string reason)
    {
        perp = 0f;
        ahead = 0f;
        kind = Il2CppFuturLab.PW2.ItemInteraction.None;
        reason = string.Empty;

        var manager = interaction;

        if (manager is null || manager == null
            || raySpawn is null || raySpawn == null)
        {
            reason = "kein Manager oder kein RaySpawn";
            return null;
        }

        var selector = manager.InteractionSelector
            ?.TryCast<Il2CppFuturLab.PW2.PlayerCameraInteractionSelector>();
        var character = selector?.m_playerCharacter;

        if (character is null || character == null)
        {
            reason = "kein Selektor oder kein PlayerCharacter";
            return null;
        }

        if (interactables.Count == 0)
        {
            reason = "keine Kandidaten";
            return null;
        }

        // DERSELBE STRAHL, DEN DAS SPIEL ZUM WASCHEN BEKOMMT, und zwar als
        // WERT statt als eigener Abgriff am Transform.
        //
        // Das ist die Lehre aus vier Messlaeufen. Die Pose-Kette hat Schreiber
        // in OnUpdate (ApplyPoseSource) UND an mehreren Stellen in
        // OnLateUpdate: DriveHead schreibt anchor.parent, DriveRay zwingt
        // raySpawn.localRotation auf Identitaet und klemmt den Anker. Wer die
        // Richtung an einer eigenen Stelle im Frame selbst abgreift, erwischt
        // je nach Zeilennummer einen anderen Zwischenstand - genau daran sind
        // drei Diagnosen gescheitert, und zwei davon waren Messungen dieser
        // Mod ueber sich selbst.
        //
        // DriveRay haelt darum am Ende seiner Arbeit fest, was es dem Spiel
        // gibt. Diese Richtung ist als richtig GEMELDET - die 6DOF-Steuerung
        // der Pistole funktioniert, das Waschen trifft - und sie ist damit die
        // einzige Richtung im Frame, die nicht erst bewiesen werden muss.
        //
        // EINEN FRAME ALT, und das ist der Preis: DriveRay laeuft nach
        // DriveButtons. Bei 90 Grad pro Sekunde sind das etwa ein Grad, und
        // dieselbe Toleranz hat der Wasch-Laser seit 0.51.0 ausdruecklich
        // akzeptiert. Gegen einen Kegel von 0,5 m auf 2 m - etwa 14 Grad -
        // faellt das nicht ins Gewicht.
        //
        // Vor dem ersten DriveRay-Durchlauf greift die alte Rechnung, damit
        // ein frueher Druck nicht ins Leere laeuft.
        var origin = aimPublished ? publishedAimOrigin : MuzzlePoint(raySpawn);
        var forward = aimPublished ? publishedAimForward : AimForward(raySpawn);

        Il2CppFuturLab.PW2.PlayerInteractableBase? best = null;
        var bestPerp = float.MaxValue;
        var bestAhead = 0f;

        for (var index = 0; index < interactables.Count; index++)
        {
            var item = interactables[index];

            if (item is null || item == null)
                continue;

            if (AimGate(item, origin, forward, out var itemPerp, out var itemAhead).Length != 0)
                continue;

            // Nur der Naechste an der Strahllinie gewinnt. Das ist AUSWAHL und
            // kein Tor, und steht darum hier statt in AimGate - der Bericht
            // will jeden Kandidaten sehen, nicht nur den besseren.
            if (itemPerp >= bestPerp)
                continue;

            bestPerp = itemPerp;
            bestAhead = itemAhead;
            best = item;
        }

        if (best is null || best == null)
        {
            reason = "nichts im Kegel";
            return null;
        }

        var interactionKind = best.PrimaryInteraction;

        try
        {
            if (!best.CanInteract(character, interactionKind, bestAhead))
            {
                reason = "CanInteract false";
                return null;
            }
        }
        catch (Exception askException)
        {
            LoggerInstance.Warning($"  CanInteract threw {askException.GetType().Name}");
            reason = $"CanInteract warf {askException.GetType().Name}";
            return null;
        }

        perp = bestPerp;
        ahead = bestAhead;
        kind = interactionKind;

        return best;
    }

    // DIE KETTE, GEMESSEN STATT HERGELEITET.
    //
    // DIE BEHAUPTUNG: die Gun-Weltrichtung ist bodyYaw * hmdRotation *
    // controllerRotation * offset, weil die rohe Controller-Rotation als LOKALE
    // Rotation unter einem Knoten landet, der die Kopfneigung schon traegt.
    // Trifft das zu, dann gilt bei JEDER Kopfhaltung
    //
    //     gun pitch - ctrl pitch == hmd pitch + 1,75 Grad (getrimmter Offset)
    //
    // Die Spalte diff nennt die linke Seite, hmd die rechte. Laufen die beiden
    // zusammen, ist der Kopf doppelt gezaehlt und der Fix ist ein Faktor.
    // Bleibt diff konstant, waehrend hmd wandert, ist die Herleitung falsch und
    // der Fehler sitzt woanders - dann sind raySpawns Elternkette und die
    // Rueckleseprobe von Bit 128 die naechsten beiden Stellen.
    //
    // AUSGELOEST VON DER KOPFBEWEGUNG, nicht von der Uhr: eine Zeile je zwei
    // Grad Neigungsaenderung, hoechstens vier pro Sekunde. Schlichtes Umsehen
    // liefert damit die Messreihe, ohne dass irgendetwas getroffen werden muss,
    // und ein ruhig gehaltener Kopf flutet das Log nicht.
    private void ReportAimChain()
    {
        if (!Dev(aimChainReport) || Time.unscaledTime < nextChainReport)
            return;

        if (raySpawn is null || raySpawn == null || anchor is null || anchor == null)
            return;

        // eulerAngles liefert 0..360. Fuer eine Neigung, die nach oben UND
        // unten geht, ist das unbrauchbar - 358 und 2 liegen vier Grad
        // auseinander, nicht 356. Wrap180 ist die vorhandene Umrechnung.
        var headPitch = Wrap180(headEuler.x);

        if (Math.Abs(headPitch - chainLastHeadPitch) < 2f)
            return;

        chainLastHeadPitch = headPitch;
        nextChainReport = Time.unscaledTime + 0.25f;

        try
        {
            // Aus forward.y statt aus eulerAngles: die Richtung ist die Groesse,
            // um die es geht, und asin davon ist eindeutig - eine
            // Euler-Zerlegung ist es bei gekipptem Roll nicht.
            var forward = raySpawn.forward;
            var gunPitch = -Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;

            var hasHand = TryReadRotation(rotationAction, out var handRotation);
            var handForward = handRotation * Vector3.forward;
            var ctrlPitch = hasHand
                ? -Mathf.Asin(Mathf.Clamp(handForward.y, -1f, 1f)) * Mathf.Rad2Deg
                : 0f;

            var parent = anchor.parent;
            var parentPitch = parent is null || parent == null
                ? 0f
                : Wrap180(parent.localRotation.eulerAngles.x);

            // DREI SPALTEN, DIE DIE ANNAHME VON AimForward PRUEFEN, statt sie
            // zu glauben. spawnLocal ist die Rotation, die das Spiel auf
            // RaySpawnPoint hinterlassen hat - der Stoerterm, den die
            // Zielsuche bisher mitgelesen hat. locFwd und asmFwd muessen
            // einander gleichen, sonst traegt die Locator-Kette doch eine
            // Rotation und AimForward greift eine Ebene zu hoch.
            var spawnLocalPitch = Wrap180(raySpawn.localRotation.eulerAngles.x);
            var locFwd = AimForward(raySpawn);
            var locPitch = -Mathf.Asin(Mathf.Clamp(locFwd.y, -1f, 1f)) * Mathf.Rad2Deg;
            var asmPitch = assembly is null || assembly == null
                ? 999f
                : -Mathf.Asin(Mathf.Clamp(assembly.forward.y, -1f, 1f)) * Mathf.Rad2Deg;

            // Beide Vorzeichen zeigen nach UNTEN: Unity-Euler x positiv neigt
            // die Nase nach unten, und -asin(forward.y) ebenso. Die Spalten
            // duerfen also verglichen werden.
            LoggerInstance.Msg($"aim chain: hmd pitch {headPitch.ToString("0.#", Invariant)}"
                + $"   parent pitch {parentPitch.ToString("0.#", Invariant)}"
                + $"   ctrl pitch {(hasHand ? ctrlPitch.ToString("0.#", Invariant) : "-")}"
                + $"   gun pitch {gunPitch.ToString("0.#", Invariant)}"
                + $"   diff {(hasHand ? (gunPitch - ctrlPitch).ToString("0.#", Invariant) : "-")}"
                + $"   spawnLocal {spawnLocalPitch.ToString("0.#", Invariant)}"
                + $"   locFwd {locPitch.ToString("0.#", Invariant)}"
                + $"   asmFwd {asmPitch.ToString("0.#", Invariant)}"
                + $"   gunFwd {Vector(forward)}"
                + $"   ctrlFwd {(hasHand ? Vector(handForward) : "unbound")}");
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  aim chain threw "
                + $"{exception.GetType().Name}: {exception.Message}");
        }
    }


    // WARUM NICHTS ANGEZIELT WAR, im Moment des Drucks und pro Kandidat.
    //
    // Der vorige Lauf ist daran verlorengegangen, dass 24 X-Druecke stumm in
    // den Blickziel-Pfad gefallen sind: "interact: aim pickup" kam null Mal
    // vor, und warum, stand nirgends. Diese Luecke schliesst dieser Block.
    //
    // JEDE SPALTE BEANTWORTET EINE HYPOTHESE, damit ein Lauf genuegt:
    //
    //   offset      zerlegt statt als Betrag. Ein grosser y-Anteil bei kleinem
    //               x und z heisst: die Wurzel des Objekts sitzt am Boden und
    //               gezielt wird auf den Koerper. Ein Skalar mittelt die
    //               Richtung weg - dieselbe Lehre wie errRight/errUp/camPitch.
    //   Pfad + ptr  mehrere Kandidaten gleichen Namens mit verschiedenen
    //               Pointern sind Doppelgaenger aus FindObjectsOfTypeAll. Wo
    //               mehrere Objekte denselben Namen tragen, ist der Name kein
    //               Bezeichner - die Regel aus Abschnitt 97.
    //   origin/fwd  wandern sie zwischen zwei Druecken einer Serie, rauscht die
    //               Pose und nicht die Geometrie.
    //   gate + can  ein leeres Tor bei canInteract FALSE heisst: die Absage ist
    //               spielseitig. Bisher unsichtbar, weil nur Erfolge geloggt
    //               wurden.
    //   ahead/dist  ahead ist die Projektion auf den Strahl, dist die echte
    //               Entfernung. CanInteract bekommt heute ahead, also den
    //               kleineren Wert. Erst messen, dann aendern.
    private void ReportAimMiss(string reason)
    {
        if (!Dev(aimMissReport))
            return;

        try
        {
            if (raySpawn is null || raySpawn == null)
            {
                LoggerInstance.Msg($"aim miss: {reason}   kein raySpawn");
                return;
            }

            // AUS DERSELBEN QUELLE WIE DIE ENTSCHEIDUNG, also dieselben Zahlen
            // - kein zweiter Rechenweg. Das gilt jetzt auch fuer den
            // Zeitpunkt: beide nehmen den von DriveRay festgehaltenen Strahl.
            var origin = aimPublished ? publishedAimOrigin : MuzzlePoint(raySpawn);
            var forward = aimPublished ? publishedAimForward : AimForward(raySpawn);

            // DIE QUELLEN DER RICHTUNG. Inzwischen ist gemessen, welche von
            // ihnen stimmte: keine. Der Fehler lag nicht in der Richtung,
            // sondern im Zeitpunkt - AimForward oben nimmt jetzt denselben
            // Knoten, den DriveRay auch dem Spiel gibt.
            //
            // Die Spalten bleiben als Gegenprobe stehen. ctrl ist die
            // Controller-Pose im Tracking-Raum, also um die Recenter-Yaw
            // gedreht und darum nur im Pitch vergleichbar - das hat hier
            // einmal eine Fehldeutung getragen. gaze laeuft als Pruefung des
            // MESSGERAETS mit: fuer sie ist gemessen, dass y == 0 ist
            // (Abschnitt 97), sie MUSS also daneben liegen. Eine Referenz,
            // deren Ergebnis man kennt, prueft das Messgeraet selbst.
            var hasHand = TryReadRotation(rotationAction, out var handRotation);
            var handForward = hasHand ? handRotation * Vector3.forward : Vector3.zero;
            var gaze = Camera.main;

            var selector = interaction?.InteractionSelector
                ?.TryCast<Il2CppFuturLab.PW2.PlayerCameraInteractionSelector>();
            var character = selector?.m_playerCharacter;
            var known = character is not null && character != null;

            LoggerInstance.Msg($"aim miss: {reason}"
                + $"   origin {Vector(origin)}   forward {Vector(forward)}"
                + $"   candidates {interactables.Count}"
                + $"   ctrl {(hasHand ? Vector(handForward) : "unbound")}"
                + $"   gaze {(gaze is null ? "none" : Vector(gaze.transform.forward))}"
                + $"   radius {aimInteractionRadius.Value:0.##} m"
                + $"   range {aimInteractionRange.Value:0.##} m"
                + $"   reach {(selector is null || selector == null ? -1f : selector.m_pickupDistance):0.#} m"
                + $"   character {(known ? "ok" : "none")}");

            // RANGFOLGE NACH DER ECHTEN ENTFERNUNG, nicht nach perp: hinter der
            // Muendung ist perp bedeutungslos, und die Frage lautet "was liegt
            // in meiner Naehe und warum hat nichts gepasst".
            //
            // Insertion-Sort ueber Indizes, kein Sort mit Vergleichsdelegate -
            // dieselbe Begruendung wie bei ScanMenuButtons: nichts Aufrufbares
            // ueber die Interop-Grenze reichen.
            var order = new List<int>();
            var dists = new List<float>();

            for (var index = 0; index < interactables.Count; index++)
            {
                var item = interactables[index];

                if (item is null || item == null)
                    continue;

                var dist = (item.transform.position - origin).magnitude;
                var at = order.Count;

                while (at > 0 && dists[at - 1] > dist)
                    at--;

                order.Insert(at, index);
                dists.Insert(at, dist);

                if (order.Count > 3)
                {
                    order.RemoveAt(3);
                    dists.RemoveAt(3);
                }
            }

            for (var rank = 0; rank < order.Count; rank++)
            {
                var item = interactables[order[rank]];
                var gate = AimGate(item, origin, forward, out var perp, out var ahead);
                var offset = item.transform.position - (origin + (forward * ahead));
                var kind = item.PrimaryInteraction;
                var can = "nicht gefragt";

                // NUR DEN GEOMETRISCH BESTANDENEN, damit der Bericht das Spiel
                // nicht oefter befragt als die Entscheidung es tut.
                if (gate.Length == 0 && known)
                {
                    try
                    {
                        can = item.CanInteract(character!, kind, ahead) ? "True" : "FALSE";
                    }
                    catch (Exception askException)
                    {
                        can = $"warf {askException.GetType().Name}";
                    }
                }

                LoggerInstance.Msg($"  [{rank + 1}] {PathOf(item.transform)}"
                    + $"   ptr 0x{item.Pointer.ToString("X")}"
                    + $"   pos {Vector(item.transform.position)}");
                LoggerInstance.Msg($"      ahead {ahead:0.##} m   dist {dists[rank]:0.##} m"
                    + $"   perp {perp:0.##} m   offset {Vector(offset)}"
                    + $"   kind {kind}   canInteract {can}"
                    + $"   gate {(gate.Length == 0 ? "PASSIERT" : gate)}");

                // DIE ENTSCHEIDENDE ZEILE. need ist die Richtung, die auf
                // diesen Gegenstand zeigt; danach steht fuer jede Quelle der
                // Winkel dagegen. Was hier nahe null liest, ist die Richtung,
                // die der Strahl haette haben muessen.
                var toItem = item.transform.position - origin;
                var need = toItem.sqrMagnitude > 0.000001f
                    ? toItem.normalized
                    : Vector3.zero;

                LoggerInstance.Msg($"      need {Vector(need)}   ang"
                    + $"   fwd {Vector3.Angle(forward, need).ToString("0.#", Invariant)}"
                    + $"   up {Vector3.Angle(raySpawn.up, need).ToString("0.#", Invariant)}"
                    + $"   -fwd {Vector3.Angle(-forward, need).ToString("0.#", Invariant)}"
                    + $"   right {Vector3.Angle(raySpawn.right, need).ToString("0.#", Invariant)}"
                    + $"   ctrl {(hasHand ? Vector3.Angle(handForward, need).ToString("0.#", Invariant) : "-")}"
                    + $"   gaze {(gaze is null ? "-" : Vector3.Angle(gaze.transform.forward, need).ToString("0.#", Invariant))}");
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  aim miss report threw "
                + $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    // DAS ZIEL IM MOMENT DES X-DRUCKS, in einem Zug mit dem Verb.
    //
    // SetTargetAndInteractStateImmediate ist nativ PUBLIC, anders als
    // SetTargetItem, und sie ist IMMEDIATE: der Aufruf braucht keine
    // Lebensdauer ueber den Frame hinaus, und deshalb kann ihn der Selektor
    // nicht ueberschreiben. Genau das war der Defekt.
    //
    // FRISCH GESCANNT statt aus der getakteten Liste gelesen: auf einem
    // Tastendruck kostet der Sweep nichts, was zaehlt - dieselbe Begruendung,
    // die FindCarriedItem schon traegt - und die halbe Sekunde Veraltung faellt
    // genau im entscheidenden Moment weg.
    //
    // DAS VERB IST DAS FREIGEGEBENE. FindAimTarget fragt CanInteract zu
    // PrimaryInteraction; ein anderes Verb zu senden als das gepruefte waere
    // eine stille Fehlzuendung, die im Log nicht zu sehen ist. Bei einem Hocker
    // ist das PickUp, bei einem Ventil Use.
    //
    // Gibt true zurueck, wenn die Pistole ein Ziel hatte und der Aufruf
    // gelaufen ist; dann hat diese Methode auch geloggt, weil nur hier Ziel,
    // Verb und Geometrie aus einem Block stammen. Bei false bleibt der
    // Blickziel-Pfad des Spiels zustaendig.
    private bool TryAimPickup(string before)
    {
        if (!aimInteraction.Value)
        {
            // Kein Kandidaten-Dump fuer eine abgeschaltete Funktion, aber auch
            // nicht stumm: "es passiert nichts" soll nie unerklaert bleiben.
            if (Dev(aimMissReport))
                LoggerInstance.Msg("aim miss: AimInteraction ist aus");

            return false;
        }

        var manager = interaction;

        if (manager is null || manager == null)
        {
            ReportAimMiss("kein PlayerInteractionManager");
            return false;
        }

        // GESETZT DIREKT NACH DEM AUFRUF und im catch zurueckgegeben. Die
        // Reihenfolge ist der Unterschied zwischen zwei Faellen: wirft der
        // Aufruf selbst, ist nichts geschehen und der Blickziel-Pfad SOLL noch
        // feuern; wirft erst das Loggen dahinter, darf er es NICHT.
        var fired = false;

        try
        {
            nextInteractableScan = Time.unscaledTime + 0.5f;
            ScanInteractables();

            var target = FindAimTarget(out var perp, out var ahead, out var kind,
                out var reason);

            if (target is null || target == null)
            {
                ReportAimMiss(reason);
                return false;
            }

            if (kind == Il2CppFuturLab.PW2.ItemInteraction.None)
            {
                ReportAimMiss("Verb None");
                return false;
            }

            var name = target.name;

            manager.SetTargetAndInteractStateImmediate(target, kind);
            fired = true;

            // BEFORE UND AFTER SIND HIER NICHT DAS URTEIL.
            // HoldingItemInteractionState.PickUpItem gibt einen Task zurueck,
            // die Aufnahme laeuft also asynchron weiter - derselbe Stolperstein,
            // den der Kommentar bei InteractionText schon festhaelt. Das Urteil
            // liefert die Wechsel-Meldung aus ReportHeldItem.
            LoggerInstance.Msg($"interact: aim pickup \"{name}\""
                + $"  verb {kind}  perp {perp:0.##} m  ahead {ahead:0.##} m"
                + $"  before {before}  after {InteractionText()}");

            return true;
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  aim pickup threw "
                + $"{exception.GetType().Name}: {exception.Message}");

            return fired;
        }
    }

    private void ReportInteractionAim()
    {
        if (!aimInteraction.Value || menuMode)
            return;

        try
        {
            // DERSELBE KURZSCHLUSS WIE VORHER, und er hat einen Zweck: ohne
            // aufgeloesten Manager waere der Sweep unten reine Arbeit ohne
            // Aussage.
            if (interaction is null || interaction == null
                || raySpawn is null || raySpawn == null)
                return;

            if (Time.unscaledTime >= nextInteractableScan)
            {
                nextInteractableScan = Time.unscaledTime + 0.5f;
                ScanInteractables();
            }

            var best = FindAimTarget(out var perp, out var ahead, out var kind, out _);

            if (best is null || best == null)
            {
                // Nichts angezielt: das Blickziel des Spiels bleibt stehen.
                if (aimTargetPointer != IntPtr.Zero)
                {
                    aimTargetPointer = IntPtr.Zero;
                    LoggerInstance.Msg("aim target: none");
                }

                return;
            }

            // AUF WECHSEL, nicht pro Frame - verglichen ueber den nativen
            // Pointer, weil Il2CppInterop bei jedem Zugriff einen frischen
            // Wrapper herausgibt.
            if (best.Pointer != aimTargetPointer || Time.unscaledTime >= nextAimLog)
            {
                aimTargetPointer = best.Pointer;
                nextAimLog = Time.unscaledTime + 2f;

                LoggerInstance.Msg($"aim target: {best.name}"
                    + $"   perp {perp:0.##} m   ahead {ahead:0.##} m"
                    + $"   kind {kind}   canInteract True");
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  interaction aim threw "
                + $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private void ScanInteractables()
    {
        interactables.Clear();

        try
        {
            var found = Resources.FindObjectsOfTypeAll(
                Il2CppInterop.Runtime.Il2CppType
                    .Of<Il2CppFuturLab.PW2.PlayerInteractableBase>());

            for (var index = 0; index < found.Length; index++)
            {
                var item = found[index]
                    ?.TryCast<Il2CppFuturLab.PW2.PlayerInteractableBase>();

                if (item is null || item == null)
                    continue;

                if (!item.gameObject.activeInHierarchy)
                    continue;

                interactables.Add(item);
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  interactable scan threw "
                + $"{exception.GetType().Name}: {exception.Message}");
            interactables.Clear();
        }
    }

    private float nextCarryProbe;
    private long carryDirSeen;
    private long carryPosSeen;
    private long carryCamSeen;
    private bool carryProbeArmed;

    // WAS DER TRAGE-HALTER LIEST, in einem Block pro Momentaufnahme.
    //
    // Die drei Zaehlerpaare sind die Messung: "in" steigt nur, wenn der Wert
    // INNERHALB von Move() gelesen wurde. Genannt werden Zuwachs und Summe,
    // weil die Summe allein nicht sagt, ob im letzten Fenster gelesen wurde -
    // und ein Zuwachs allein nicht, ob ueberhaupt je.
    //
    // camFwd und handFwd stehen daneben, damit der Winkel zwischen Kopf und
    // Hand mit im Block steht: nur so ist spaeter zu sehen, ob die
    // Item-Position dem einen oder dem anderen gefolgt ist. Beziehungszahlen
    // nie ueber zwei Logzeilen paaren.
    private void ReportCarryProbe()
    {
        // NICHT an CarryProbe allein: diese Preference schaltet die drei
        // Getter-ZAEHLER, und die sind beantwortet und aus. Der Block ist
        // jetzt der Wirkungsnachweis des Kamera-Tauschs - ohne ihn waere
        // "es fuehlt sich nicht anders an" nicht von "der Tausch lief nie"
        // zu unterscheiden. Eine Zeile pro Sekunde, nur waehrend getragen
        // wird.
        if (!Dev(carryProbe))
            return;

        try
        {
            // EINMAL, sobald die Klammer erstmals zugeschlagen hat. Ohne diese
            // Zeile waere "kein Block" nicht von "Move() wird nie gerufen" zu
            // unterscheiden - zwei verschiedene Befunde.
            if (!carryProbeArmed
                && (GameInput.HolderMoves > 0 || GameInput.HolderMovesPhysical > 0
                    || GameInput.HolderMovesBase > 0))
            {
                carryProbeArmed = true;
                LoggerInstance.Msg("carry probe: erstes Move() gesehen, Halter "
                    + $"{GameInput.HolderKind}   probe "
                    + $"{(GameInput.CarryProbeInstalled ? "vollstaendig" : "UNVOLLSTAENDIG")}");
            }

            if (!carrying || Time.unscaledTime < nextCarryProbe)
                return;

            nextCarryProbe = Time.unscaledTime + 1f;

            var dirIn = GameInput.TargetDirIn;
            var posIn = GameInput.TargetPosIn;
            var camIn = GameInput.CamTransformIn;

            var dirStep = dirIn - carryDirSeen;
            var posStep = posIn - carryPosSeen;
            var camStep = camIn - carryCamSeen;

            carryDirSeen = dirIn;
            carryPosSeen = posIn;
            carryCamSeen = camIn;

            var gaze = Camera.main;
            var camForward = gaze is null || gaze == null
                ? Vector3.zero
                : gaze.transform.forward;
            var handForward = aimPublished ? publishedAimForward : Vector3.zero;

            var angle = camForward == Vector3.zero || handForward == Vector3.zero
                ? -1f
                : Vector3.Angle(camForward, handForward);

            var delta = (GameInput.ItemAfterMove - GameInput.ItemBeforeMove).magnitude;

            LoggerInstance.Msg($"carry probe: halter {GameInput.HolderKind}"
                + $"   moves raycast {GameInput.HolderMoves} physical {GameInput.HolderMovesPhysical} base {GameInput.HolderMovesBase}"
                + $"   item {(GameInput.ItemSeen ? "ok" : "NONE")}"
                + $"   before {Vector(GameInput.ItemBeforeMove)}"
                + $"   after {Vector(GameInput.ItemAfterMove)}"
                + $"   delta {delta.ToString("0.###", Invariant)} m");

            LoggerInstance.Msg($"      targetDir in +{dirStep} ({dirIn}) out {GameInput.TargetDirOut}"
                + $"   targetPos in +{posStep} ({posIn}) out {GameInput.TargetPosOut}"
                + $"   camTransform in +{camStep} ({camIn}) out {GameInput.CamTransformOut}");

            LoggerInstance.Msg($"      camFwd {Vector(camForward)}"
                + $"   handFwd {(aimPublished ? Vector(handForward) : "unpublished")}"
                + $"   angle {(angle < 0f ? "-" : angle.ToString("0.#", Invariant))}"
                + $"   lastTargetDir {Vector(GameInput.LastTargetDirection)}"
                + $"   lastTargetPos {Vector(GameInput.LastTargetPosition)}");

            // DIE ZEILE, DIE DEN STRAHL DES SPIELS NENNT - und sie steht hier,
            // damit sie auch dann etwas sagt, wenn der Tausch nichts bewirkt.
            //
            // toItem ist die Richtung von der Kamera zum abgelegten Objekt,
            // waagerecht genommen, weil die Bodenklemme die Hoehe ohnehin
            // bestimmt. Liest angle(cam) nahe null, hing das Objekt am
            // Kopfstrahl; liest angle(hand) nahe null, haengt es jetzt an der
            // Hand. Beides gross heisst: der Halter rechnet aus einer dritten
            // Quelle, und dann ist der naechste Hebel das Nachsetzen im
            // Postfix statt der Tausch davor.
            var toItem = GameInput.ItemAfterMove - GameInput.CamPosAtMove;
            var flatItem = new Vector3(toItem.x, 0f, toItem.z);
            var flatCam = new Vector3(GameInput.CamFwdAtMove.x, 0f, GameInput.CamFwdAtMove.z);
            var flatHand = new Vector3(handForward.x, 0f, handForward.z);

            LoggerInstance.Msg($"      swaps {GameInput.CarrySwaps}"
                + $"   skips {GameInput.CarrySwapSkips}"
                + $"   camPos {Vector(GameInput.CamPosAtMove)}"
                + $"   camFwdAtMove {Vector(GameInput.CamFwdAtMove)}"
                + $"   itemDist {flatItem.magnitude.ToString("0.##", Invariant)} m"
                + $"   angle(cam) {(flatItem.sqrMagnitude < 0.0001f || flatCam.sqrMagnitude < 0.0001f ? "-" : Vector3.Angle(flatItem, flatCam).ToString("0.#", Invariant))}"
                + $"   angle(hand) {(flatItem.sqrMagnitude < 0.0001f || flatHand.sqrMagnitude < 0.0001f ? "-" : Vector3.Angle(flatItem, flatHand).ToString("0.#", Invariant))}");
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  carry probe threw {exception.GetType().Name}: "
                + exception.Message);
        }
    }

    // PHYSISCHES GREIFEN: DIE HAND UEBERSCHNEIDET DAS OBJEKT.
    //
    // Kein Zielen, kein Kegel, kein Blickziel - bei einem Objekt am Boden ist
    // das der ehrlichere Weg, und Abschnitt 101 hat es so vorgeschlagen.
    //
    // DER BEZUGSPUNKT IST DER COLLIDER, und das ist der einzige Grund, warum
    // diese Methode ueberhaupt gebaut werden konnte. transform.position dieser
    // Objekte liegt konstant im Levelursprung (Abschnitt 101, 82 Messungen),
    // Kind-Renderer gibt es keine (Lauf 0), und renderer.bounds ist
    // projektweit gesperrt. Uebrig bleibt ClosestPoint - gemessen ueber 21
    // Druecke, ohne Wurf, mit brauchbaren Punkten.
    //
    // ES IST DIE STANDZONE DES SPIELS, NICHT DAS MESH. Der Wickeltisch liest
    // 0 m, waehrend die Hand auf Brusthoehe mit 45 cm Spielraum in ihm steht;
    // die Klobrille verlangt echtes Hinunterfassen. Das ist eine bewusste
    // Entscheidung und kein Versehen: InteractZoneRadius auf 0 verlangt die
    // Hand IM Collider und verschaerft es ohne Build. Der ehrliche Mesh-Weg
    // waere InteractableAnimationEvent.m_animator, aber m_animEvents ist eine
    // List eines Il2CppSystem.ValueType - die Familie, die diesen Prozess
    // zweimal ohne managed Exception beendet hat. Das braucht seinen eigenen
    // Messlauf.
    //
    // NUR Use, NICHT PickUp. Aufnehmen bleibt bei X; ein Trigger, der auch
    // Gegenstaende aufnimmt, waere eine zweite Verhaltensaenderung in einem
    // Lauf - und der Wunsch nannte ausdruecklich die animierten Objekte.
    //
    // DAS VERB IST DAS FREIGEGEBENE, wie bei TryAimPickup: CanInteract wird zu
    // PrimaryInteraction gefragt und genau dieses Verb gesendet. Ein anderes
    // waere eine stille Fehlzuendung.
    //
    // Gibt true zurueck, wenn gegriffen wurde; dann ist der Druck verbraucht.
    private bool TryGrabInteract()
    {
        if (!interactGrab.Value)
            return false;

        // EIN BLOCK PRO DRUCK, AUCH BEIM FEHLSCHLAG. Ein stummer Druck ist die
        // Luecke, die Abschnitt 99 einen ganzen Lauf gekostet hat: 24 X-Druecke
        // fielen dort stumm in den Blickziel-Pfad, und warum, stand nirgends.
        var reason = "";

        try
        {
            if (menuMode)
            {
                reason = "Menue offen";
            }
            else if (carrying)
            {
                // Haende voll. Der Trigger behaelt dann seine alte Aufgabe,
                // statt ein zweites Objekt greifen zu wollen.
                reason = "es wird getragen";
            }
            else if (!offHandWorldPublished)
            {
                reason = "keine Weltposition der linken Hand";
            }
            else if (interaction is null || interaction == null)
            {
                reason = "kein PlayerInteractionManager";
            }
            else
            {
                var character = interaction.InteractionSelector
                    ?.TryCast<Il2CppFuturLab.PW2.PlayerCameraInteractionSelector>()
                    ?.m_playerCharacter;

                if (character is null || character == null)
                {
                    reason = "kein PlayerCharacter";
                }
                else
                {
                    // FRISCH GESCANNT, dieselbe Begruendung wie bei
                    // TryAimPickup: auf einem Tastendruck kostet der Sweep
                    // nichts, was zaehlt, und die halbe Sekunde Veraltung
                    // faellt genau im entscheidenden Moment weg.
                    nextInteractableScan = Time.unscaledTime + 0.5f;
                    ScanInteractables();

                    var hand = publishedOffHandWorld;

                    Il2CppFuturLab.PW2.PlayerInteractableBase? best = null;
                    var bestDistance = float.MaxValue;
                    var secondName = "-";
                    var secondDistance = float.MaxValue;
                    var considered = 0;

                    for (var index = 0; index < interactables.Count; index++)
                    {
                        var item = interactables[index];

                        if (item is null || item == null)
                            continue;

                        if (item.PrimaryInteraction != Il2CppFuturLab.PW2.ItemInteraction.Use)
                            continue;

                        considered++;

                        var distance = NearestColliderDistance(item.transform, hand);

                        if (distance < 0f)
                            continue;

                        if (distance < bestDistance)
                        {
                            secondName = best is null || best == null ? "-" : best.name;
                            secondDistance = bestDistance;
                            best = item;
                            bestDistance = distance;
                        }
                        else if (distance < secondDistance)
                        {
                            secondName = item.name;
                            secondDistance = distance;
                        }
                    }

                    if (best is null || best == null)
                    {
                        reason = $"kein Use-Objekt mit Collider ({considered} geprueft)";
                    }
                    else if (bestDistance > interactZoneRadius.Value)
                    {
                        reason = $"naechstes \"{best.name}\" {bestDistance:0.##} m > "
                            + $"{interactZoneRadius.Value:0.##} m";
                    }
                    else
                    {
                        var kind = best.PrimaryInteraction;
                        var name = best.name;
                        var can = false;

                        try
                        {
                            can = best.CanInteract(character, kind, bestDistance);
                        }
                        catch (Exception askException)
                        {
                            reason = $"CanInteract warf {askException.GetType().Name}";
                        }

                        if (reason.Length == 0 && !can)
                            reason = $"CanInteract false fuer \"{name}\"";

                        if (reason.Length == 0)
                        {
                            interaction.SetTargetAndInteractStateImmediate(best, kind);

                            // Die FREIE Hand hat gegriffen - sie fuehrt den
                            // linken Trigger -, also pulst sie. Welche Seite
                            // das ist, entscheidet die Haendigkeit.
                            Buzz(!WasherHandRight, "grab");

                            LoggerInstance.Msg($"grab: \"{name}\" verb {kind}"
                                + $"   dist {bestDistance:0.##} m"
                                + $"   radius {interactZoneRadius.Value:0.##} m"
                                + $"   hand {Vector(hand)}"
                                + $"   zweiter {secondName}"
                                + $" {(secondDistance >= float.MaxValue ? "-" : secondDistance.ToString("0.##", Invariant))}"
                                + $"   state {InteractionText()}");

                            return true;
                        }
                    }
                }
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  grab threw {exception.GetType().Name}: "
                + exception.Message);

            // Der Druck gilt als NICHT verbraucht: ein Wurf hier soll dem
            // Trigger nicht seine alte Aufgabe nehmen.
            return false;
        }

        LoggerInstance.Msg($"grab: nichts gegriffen - {reason}");
        return false;
    }

    // Der kleinste Abstand der Hand zur Collider-Oberflaeche unter diesem
    // Knoten, oder -1, wenn es keinen lesbaren Collider gibt.
    //
    // Der engste try/catch, den diese Datei hat, und er sitzt bewusst um den
    // EINZELNEN Aufruf: ein Vector3 als Argument in eine Spielmethode ist in
    // diesem Projekt neu - belegt in Lauf 0, aber nur fuer diese beiden
    // Objekte. Ein Wurf darf einen Kandidaten kosten, nicht die Geste.
    private float NearestColliderDistance(Transform root, Vector3 hand)
    {
        var nearest = -1f;

        try
        {
            var colliders = root.GetComponentsInChildren<Collider>();

            if (colliders is null)
                return -1f;

            for (var index = 0; index < colliders.Length; index++)
            {
                var collider = colliders[index];

                if (collider is null || collider == null)
                    continue;

                try
                {
                    var distance = (collider.ClosestPoint(hand) - hand).magnitude;

                    if (nearest < 0f || distance < nearest)
                        nearest = distance;
                }
                catch
                {
                    // Dieser Collider antwortet nicht. Der naechste vielleicht.
                }
            }
        }
        catch
        {
            return -1f;
        }

        return nearest;
    }

    private bool interactProbeRequested;

    // WO DIE ANIMIERTEN INTERAKTIONSOBJEKTE WIRKLICH SITZEN.
    //
    // Abschnitt 101: Int_ToiletSeat und Int_ChangingTable melden beide
    // (0.04, 0.01, -0.04), also den Levelursprung, ueber 82 Messungen und drei
    // Spielsitzungen. Sie haengen unter einem gemeinsamen Knoten
    // InteractableAnimation, dessen Transform im Ursprung sitzt, waehrend das
    // sichtbare Teil per Animation woanders liegt. Fuer die Zielsuche sind sie
    // damit unerreichbar, und eine Greif-Geste gegen transform.position liefe
    // in dieselbe Wand.
    //
    // DREI SPALTEN, DREI FRAGEN, damit ein Lauf genuegt:
    //
    //   renderer   Name und Weltposition jedes Kind-Renderers, plus Abstand
    //              zur linken Hand. GetComponentsInChildren<Renderer> gibt ein
    //              Klassen-Array, und .transform.position ist ein
    //              Vector3-Getter auf einer Klassenreferenz - die Form, die
    //              diese Mod ueberall benutzt. KEIN renderer.bounds: 24 Byte
    //              per Wert, projektweit gesperrt, hat diesen Prozess zweimal
    //              ohne managed Exception beendet.
    //   collider   ClosestPoint(handWorld) und dessen Abstand. Das ist die
    //              ehrliche Mesh-Naehe und damit der Bezugspunkt, den ein
    //              physisches Greifen braeuchte. In EIGENEM try/catch, weil
    //              ein Vector3 als ARGUMENT in eine Spielmethode in diesem
    //              Projekt noch nicht erprobt ist - schlaegt es fehl, bleibt
    //              der Renderer-Pivot.
    //   pos        transform.position, als Gegenprobe auf den Befund aus 101.
    //
    // Gelistet werden ALLE Kandidaten unter einem InteractableAnimation-Knoten
    // - also Klobrille und Wickeltisch nebeneinander. Einer von beiden
    // funktioniert heute ueber das Blickziel des Spiels, und eine Referenz,
    // deren Ergebnis man kennt, prueft das Messgeraet selbst.
    private void ReportInteractProbe()
    {
        if (!interactProbeRequested)
            return;

        interactProbeRequested = false;

        if (!Dev(interactProbe))
            return;

        try
        {
            // FRISCH GESCANNT wie bei TryAimPickup: auf einem Tastendruck
            // kostet der Sweep nichts, was zaehlt, und die halbe Sekunde
            // Veraltung faellt genau im entscheidenden Moment weg.
            nextInteractableScan = Time.unscaledTime + 0.5f;
            ScanInteractables();

            var hand = publishedOffHandWorld;

            LoggerInstance.Msg($"interact probe: linke Hand "
                + $"{(offHandWorldPublished ? Vector(hand) : "NICHT VEROEFFENTLICHT")}"
                + $"   Kandidaten {interactables.Count}"
                + $"   getragen {(carrying ? "ja" : "nein")}");

            if (!offHandWorldPublished)
            {
                // Ohne Handposition ist jeder Abstand unten eine Zahl gegen
                // (0,0,0) und damit eine Luege. Lieber kein Block als ein
                // falscher.
                LoggerInstance.Msg("      keine Weltposition der linken Hand - "
                    + "WorldSpace aus, oder die Off-Hand-Pose ist nicht gebunden.");
                return;
            }

            var listed = 0;

            for (var index = 0; index < interactables.Count; index++)
            {
                var item = interactables[index];

                if (item is null || item == null)
                    continue;

                var itemTransform = item.transform;
                var parent = itemTransform.parent;
                var parentName = parent is null || parent == null ? "" : parent.name;

                // DER KNOTEN IST DAS AUSWAHLKRITERIUM, nicht der Name des
                // Objekts: "Int_" ist eine Namenskonvention und damit eine
                // Vermutung, InteractableAnimation ist der gemessene Befund
                // aus Abschnitt 101.
                if (parentName.IndexOf("InteractableAnimation",
                        StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                listed++;

                LoggerInstance.Msg($"  [{listed}] {PathOf(itemTransform)}"
                    + $"   ptr 0x{item.Pointer.ToString("X")}"
                    + $"   pos {Vector(itemTransform.position)}"
                    + $"   dist(pos) {(itemTransform.position - hand).magnitude:0.##} m"
                    + $"   kind {item.PrimaryInteraction}");

                ReportProbeRenderers(itemTransform, hand);
                ReportProbeColliders(itemTransform, hand);
            }

            if (listed == 0)
            {
                LoggerInstance.Msg("      kein Kandidat unter einem "
                    + "InteractableAnimation-Knoten. Entweder steht keiner in diesem "
                    + "Auftrag, oder der Knotenname ist ein anderer - dann ist die "
                    + "AUSWAHL schuld und nicht die Geometrie.");
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  interact probe threw {exception.GetType().Name}: "
                + exception.Message);
        }
    }

    // Die drei naechsten Kind-Renderer, nach Abstand zur Hand. Drei, weil ein
    // Objekt ein Dutzend Renderer haben kann und der naechste die Antwort ist -
    // die beiden dahinter sagen, wie weit die Pivots streuen.
    private void ReportProbeRenderers(Transform root, Vector3 hand)
    {
        try
        {
            var renderers = root.GetComponentsInChildren<Renderer>();

            if (renderers is null || renderers.Length == 0)
            {
                LoggerInstance.Msg("      renderer: KEINE - dann ist das sichtbare Teil "
                    + "kein Kind dieses Knotens.");
                return;
            }

            var best = -1;
            var bestDistance = float.MaxValue;
            var second = -1;
            var secondDistance = float.MaxValue;

            for (var index = 0; index < renderers.Length; index++)
            {
                var renderer = renderers[index];

                if (renderer is null || renderer == null)
                    continue;

                var distance = (renderer.transform.position - hand).magnitude;

                if (distance < bestDistance)
                {
                    second = best;
                    secondDistance = bestDistance;
                    best = index;
                    bestDistance = distance;
                }
                else if (distance < secondDistance)
                {
                    second = index;
                    secondDistance = distance;
                }
            }

            if (best < 0)
            {
                LoggerInstance.Msg($"      renderer: {renderers.Length} gefunden, "
                    + "aber jeder einzelne las null.");
                return;
            }

            var nearest = renderers[best];

            LoggerInstance.Msg($"      renderer {renderers.Length}"
                + $"   naechster \"{nearest.name}\" {Vector(nearest.transform.position)}"
                + $"   dist {bestDistance:0.##} m"
                + (second < 0
                    ? "   zweiter -"
                    : $"   zweiter \"{renderers[second].name}\" {secondDistance:0.##} m"));
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"      renderer read threw {exception.GetType().Name}: "
                + exception.Message);
        }
    }

    // ClosestPoint ist die Frage, die dieser Block stellt: liefert das Spiel
    // eine Oberflaeche, ist der Greifradius gegen sie zu pruefen und nicht
    // gegen einen Pivot. Der try/catch ist ENG um den Aufruf, damit ein
    // Fehlschlag genau dieser Zeile zugeordnet werden kann - ein Vector3 als
    // Argument in eine Spielmethode ist hier neu.
    private void ReportProbeColliders(Transform root, Vector3 hand)
    {
        try
        {
            var colliders = root.GetComponentsInChildren<Collider>();

            if (colliders is null || colliders.Length == 0)
            {
                LoggerInstance.Msg("      collider: KEINE - physisches Greifen muesste "
                    + "dann gegen die Renderer-Pivots gehen.");
                return;
            }

            var best = -1;
            var bestDistance = float.MaxValue;
            var closest = Vector3.zero;

            for (var index = 0; index < colliders.Length; index++)
            {
                var collider = colliders[index];

                if (collider is null || collider == null)
                    continue;

                try
                {
                    var point = collider.ClosestPoint(hand);
                    var distance = (point - hand).magnitude;

                    if (distance >= bestDistance)
                        continue;

                    bestDistance = distance;
                    closest = point;
                    best = index;
                }
                catch (Exception closestException)
                {
                    LoggerInstance.Warning("      ClosestPoint warf "
                        + $"{closestException.GetType().Name} - der Renderer-Pivot bleibt "
                        + "der Bezugspunkt.");
                    return;
                }
            }

            if (best < 0)
            {
                LoggerInstance.Msg($"      collider {colliders.Length}, keiner lesbar.");
                return;
            }

            LoggerInstance.Msg($"      collider {colliders.Length}"
                + $"   naechster \"{colliders[best].name}\""
                + $"   punkt {Vector(closest)}   dist {bestDistance:0.##} m"
                + $"   trigger {colliders[best].isTrigger}");
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"      collider read threw {exception.GetType().Name}: "
                + exception.Message);
        }
    }

    private void ReportHeldItem()
    {
        try
        {
            // Cached with a timed re-resolve. This runs every frame and
            // FindObjectOfType walks the scene; performance is a deferred topic
            // in this project, which is a reason not to add cost to it.
            if (interaction is null || interaction == null)
            {
                if (Time.unscaledTime < nextInteractionSearch)
                    return;

                nextInteractionSearch = Time.unscaledTime + 0.5f;
                interaction = UnityEngine.Object
                    .FindObjectOfType<Il2CppFuturLab.PW2.PlayerInteractionManager>();
            }

            var manager = interaction;

            // PlayerInteractionState carries no Item - probed, corrected. What it
            // does carry is a string Name, which is the state machine's own label
            // and therefore the most direct statement of "is the player carrying
            // something" available. IsInventoryBlocked sits beside it.
            //
            // The held object itself comes from the holder: PlayerItemHolderBase
            // has an Item of type MovableItemBase, both classes. Three holders
            // exist on the manager - physics, ladder and abseiling - and the
            // physics one is what a ladder or a prop goes into.
            var state = manager?.InteractionState;
            var holder = manager?.m_physicsItemHolder;
            var item = holder?.Item;

            var stateName = state is null ? "no state" : state.Name;

            // A substring test, not an equality test. The label measured is
            // HoldingItemInteractionState, but placement and abseiling states
            // plausibly share the prefix, and a missed carry state costs a
            // broken control while a false positive only costs a modifier.
            carrying = stateName.IndexOf("Holding", StringComparison.OrdinalIgnoreCase) >= 0;

            // AM ZUSTANDSWECHSEL DES SPIELS, NICHT AM TASTENDRUCK - und das ist
            // der ganze Punkt dieser Stelle.
            //
            // X nimmt auf zwei Wegen auf: ueber die Zielsuche der Pistole, die
            // ihr Ziel kennt, und ueber das Blickziel des Spiels, bei dem diese
            // Mod NICHT weiss, ob etwas aufgenommen wurde. Ein Puls am
            // Tastendruck wuerde auf dem zweiten Weg auch dann vibrieren, wenn
            // gar nichts passiert ist - eine Rueckmeldung, die luegt, ist
            // schlechter als keine. Der Traegezustand kippt nur, wenn das Spiel
            // wirklich zugegriffen hat.
            //
            // Und er liefert beide vom Nutzer gewuenschten Pulse aus einer
            // Quelle: einen beim Aufnehmen, einen zweiten beim Ablegen mit X.
            //
            // AN DER FREIEN HAND, weil X dort sitzt: leftPrimary wird von
            // der Off-Hand geholt und tauscht damit mit der Haendigkeit.
            // "Links" war die Seite eines Rechtshaenders.
            if (carrying != hapticCarrying)
            {
                hapticCarrying = carrying;
                Buzz(!WasherHandRight, carrying ? "pickup" : "place");
            }
            var itemName = item is null || item == null ? "none" : item.name;
            var now = $"{stateName}/{itemName}";

            if (string.Equals(now, heldItemState, StringComparison.Ordinal))
                return;

            heldItemState = now;
            LoggerInstance.Msg($"interaction: state {stateName}   held {itemName}   "
                + $"inventoryBlocked {(state is null ? "?" : state.IsInventoryBlocked.ToString())}   "
                + $"rotationSpeed {(manager is null ? -1f : manager.ItemRotationSpeed):0.###}   "
                + $"target {(manager?.m_targetItem is null ? "none" : manager.m_targetItem.name)}");
        }
        catch (Exception exception)
        {
            // Once, then silent: a per-frame read that throws would otherwise
            // drown the log it exists to inform.
            if (heldItemState != "threw")
            {
                heldItemState = "threw";
                LoggerInstance.Warning($"  held item read threw {exception.GetType().Name}: "
                    + exception.Message);
            }
        }
    }

    // FIVE candidates now, and "manager" leads because it is the one the flat
    // game demonstrably uses.
    //
    // The decisive report: keyboard E toggles pick-up and place with the SAME
    // key, while the mod's three BaseInput verbs all failed. E is the interact
    // action, so the game routes one action to both halves - and it does not go
    // through the BaseInput event this mod was raising. Section 73 already named
    // the alternative in writing: "der sichere Weg zum selben Ziel ist immer der
    // Invoke-Helfer ODER PlayerInteractionManager.OnInteractInput(ItemInteraction)".
    // That call is public, void and takes an int-based enum, so it is a safe
    // shape.
    //
    // The other four stay for comparison, since one run can walk the whole list
    // with Ctrl+Num1 and a run costs a close, a deploy and a relaunch.
    // EIGHT candidates, because the obvious one is now measured and does NOT
    // place.
    //
    // OnInteractInput(PickUp) picks up and, pressed again while carrying, does
    // nothing at all - the log shows the call firing four times with the state
    // unchanged. The reason is visible in the same lines: "target
    // PickableStepLadder" stays set while the item is held, so a PickUp verb
    // sees a target and tries to pick up what is already in hand.
    //
    // So the verb has to differ while carrying, and the manager route gets the
    // same three verbs the BaseInput route had, plus the two state calls
    // SetInteractStateImmediate and ClearInteractions. Keyboard E toggles both
    // halves with one action, so ONE of these is what it reaches - and a single
    // run with Ctrl+Num1 can walk the whole list rather than costing eight.
    // "clear" LEADS, because it is the measured answer.
    //
    // Walked through all eight in one run. Seven left the state untouched;
    // ClearInteractions moved it every time:
    //
    //   place/manager-remove   HoldingItemInteractionState -> HoldingItemInteractionState
    //   place/manager-use      HoldingItemInteractionState -> HoldingItemInteractionState
    //   place/manager-pickup   HoldingItemInteractionState -> HoldingItemInteractionState
    //   place/setstate-remove  HoldingItemInteractionState -> HoldingItemInteractionState
    //   place/clear            HoldingItemInteractionState -> no state      (4 of 4)
    //
    // So the item is released by CLEARING the interaction, not by naming a second
    // verb - which is why the verb search could never have succeeded. Three
    // guesses were spent on the shape of the question before it was simply
    // enumerated; the cycle was worth more than any of the reasoning.
    //
    // The rest are kept behind Ctrl+Num1. They cost nothing and they are the
    // record of what does not work.
    // "request-place" first, so the cycle starts at the correct one.
    private static readonly string[] PlaceVerbs =
    {
        // The two that go through the game's own placement. The rest were the
        // candidates tried in section 79 and are kept so a failure can be
        // compared against them in one run instead of one build each.
        "request-place",
        "set-placed",
        "clear",
        "manager-remove",
        "manager-use",
        "manager-pickup",
        "setstate-remove",
        "cancel",
        "remove",
        "use",
    };

    internal static string NextPlaceVerb(string current)
    {
        for (var index = 0; index < PlaceVerbs.Length; index++)
        {
            if (string.Equals(PlaceVerbs[index], current, StringComparison.OrdinalIgnoreCase))
                return PlaceVerbs[(index + 1) % PlaceVerbs.Length];
        }

        return PlaceVerbs[0];
    }

    // The three candidate ways to say "put it down". None is derivable from a
    // signature, so the choice is a config value and the log records what was
    // sent beside what the state machine did with it.
    // The pick-up half goes through the manager for every manager-* verb, since
    // that half is measured working there.
    private bool UsesManagerRoute() =>
        placeVerb.Value.StartsWith("manager", StringComparison.OrdinalIgnoreCase)
        || placeVerb.Value.StartsWith("setstate", StringComparison.OrdinalIgnoreCase);

    // Finds the item currently in the player's hands.
    //
    // ON THE ITEM, not on the holder, and that is why the earlier attempt came
    // up empty. Section 79 read all three PlayerItemHolderBase components -
    // physics, ladder and abseiling - while a step ladder was demonstrably in
    // hand, and all three reported nothing. The note there said "the carried
    // item is tracked somewhere else entirely"; this is that somewhere.
    // MovableItemBase carries PlayerHolding, so the link points from the item
    // back to the player and a search over items finds it immediately.
    //
    // Only ever called on a button press, so the FindObjectsOfTypeAll sweep
    // costs nothing that matters.
    private Il2CppFuturLab.PW2.MovableItemBase? FindCarriedItem()
    {
        try
        {
            var all = Resources.FindObjectsOfTypeAll(
                Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.PW2.MovableItemBase>());

            for (var index = 0; index < all.Length; index++)
            {
                var item = all[index]?.TryCast<Il2CppFuturLab.PW2.MovableItemBase>();

                if (item is null || item == null)
                    continue;

                var holder = item.GetPlayerHolding();

                if (holder is not null && holder != null)
                    return item;
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  carried item search threw {exception.GetType().Name}: "
                + exception.Message);
        }

        return null;
    }

    private void PlaceCarried()
    {
        var verb = placeVerb.Value;

        // THE GAME'S OWN PLACE-DOWN, and the reason the old default was wrong.
        //
        // "clear" called PlayerInteractionManager.ClearInteractions(), which
        // means ABORT every interaction - not COMPLETE the placement. The item
        // left the hands, so it looked right, but the state machine never
        // reached Placed and SetPlaced never ran. On a ladder that is visible:
        // climbing goes through LadderClimbTrigger, whose size comes from
        // MovableItemBase.PlacedTriggerSize, so a ladder put down this way could
        // no longer be climbed - and a restart did not help, because the state
        // sits on the item instance in the scene.
        //
        // Reported as "pick the ladder up with X and put it down, and I cannot
        // climb it any more", with the decisive detail that the keyboard's E does
        // NOT break it. That ruled out a game bug and pointed straight here.
        //
        // RequestPlaceAsync, not RequestPlace: the latter returns a ValueTask,
        // and a struct returned by value across the interop boundary is the
        // family that hard-crashed this process twice (section 72). Async
        // returns a Task, which is a reference type. SetPlaced is the blunter
        // alternative - it applies the effect without asking whether the spot is
        // valid - and is kept as a second verb rather than the default.
        if (string.Equals(verb, "request-place", StringComparison.OrdinalIgnoreCase)
            || string.Equals(verb, "set-placed", StringComparison.OrdinalIgnoreCase))
        {
            var item = FindCarriedItem();
            var holder = item?.GetPlayerHolding();

            if (item is not null && item != null && holder is not null && holder != null)
            {
                var name = item.name;

                try
                {
                    if (string.Equals(verb, "set-placed", StringComparison.OrdinalIgnoreCase))
                    {
                        item.SetPlaced(holder);
                        LoggerInstance.Msg($"place: SetPlaced on \"{name}\".");
                    }
                    else
                    {
                        // The Task is deliberately dropped. It completes on the
                        // game's own schedule and nothing here waits on it; the
                        // outcome shows up as a state change either way.
                        var pending = item.RequestPlaceAsync(holder);
                        LoggerInstance.Msg($"place: RequestPlaceAsync on \"{name}\""
                            + $"  valid {item.IsValidPlacement()}"
                            + $"  task {(pending is null ? "null" : "started")}.");
                    }

                    return;
                }
                catch (Exception exception)
                {
                    LoggerInstance.Warning($"  place: {verb} threw "
                        + $"{exception.GetType().Name}: {exception.Message}");
                }
            }
            else
            {
                LoggerInstance.Msg("place: no carried item found - falling back to clear.");
            }

            // Falls through to ClearInteractions rather than doing nothing: a
            // place-down that refuses to let go is worse than one that skips a
            // state change.
            interaction?.ClearInteractions();
            return;
        }

        if (string.Equals(verb, "manager-remove", StringComparison.OrdinalIgnoreCase))
        {
            interaction?.OnInteractInput(Il2CppFuturLab.PW2.ItemInteraction.Remove);
            return;
        }

        if (string.Equals(verb, "manager-use", StringComparison.OrdinalIgnoreCase))
        {
            interaction?.OnInteractInput(Il2CppFuturLab.PW2.ItemInteraction.Use);
            return;
        }

        if (string.Equals(verb, "manager-pickup", StringComparison.OrdinalIgnoreCase))
        {
            interaction?.OnInteractInput(Il2CppFuturLab.PW2.ItemInteraction.PickUp);
            return;
        }

        if (string.Equals(verb, "setstate-remove", StringComparison.OrdinalIgnoreCase))
        {
            interaction?.SetInteractStateImmediate(Il2CppFuturLab.PW2.ItemInteraction.Remove);
            return;
        }

        if (string.Equals(verb, "clear", StringComparison.OrdinalIgnoreCase))
        {
            interaction?.ClearInteractions();
            return;
        }

        if (playerInput is null)
            return;

        if (string.Equals(verb, "remove", StringComparison.OrdinalIgnoreCase))
            playerInput.InvokeItemInteraction(Il2CppFuturLab.PW2.ItemInteraction.Remove);
        else if (string.Equals(verb, "use", StringComparison.OrdinalIgnoreCase))
            playerInput.InvokeItemInteraction(Il2CppFuturLab.PW2.ItemInteraction.Use);
        else
            playerInput.InvokeCancelInteraction(Il2CppFuturLab.PW2.ItemInteraction.PickUp);
    }

    private string InteractionText()
    {
        try
        {
            var state = interaction?.InteractionState;

            if (state is null)
                return "[no state]";

            // IsPlacementValid comes along because the place call FIRES and the
            // state does not change: "interact: place/manager before
            // [HoldingItemInteractionState] after [HoldingItemInteractionState]".
            // If the game refuses the placement because the spot is invalid, that
            // is game behaviour and not a mod defect - but nothing said so out
            // loud, which is how a working feature looks broken.
            // The holder columns are GONE, and their absence is a finding.
            //
            // All three were read - physics, ladder and abseiling - and all three
            // reported "empty" while a step ladder was demonstrably in hand. The
            // carried item is tracked somewhere else entirely, so
            // IsPlacementValid was never going to answer anything and the column
            // only suggested a cause that was not there. The state machine's own
            // label is the one reliable indicator, and it is enough.

            return $"[{state.Name}]";
        }
        catch
        {
            return "[read threw]";
        }
    }

    // ROTATING A CARRIED OBJECT, as a MODE rather than on new buttons.
    //
    // Both sticks were already spoken for - left moves, right turns and changes
    // the nozzle - so the request was explicitly for a mode: while an object is
    // carried, hold the left grip and the left stick rotates it instead of
    // walking. That is the same held-modifier pattern section 73 designed for
    // the left grip in the first place.
    //
    // Movement suppression is not optional. Without it the player would walk
    // while rotating, which is the exact failure section 73 called out for the
    // grip-quadrant scheme.
    //
    // Returns true when the mode consumed the stick, so DriveMovement can stand
    // down - one writer per input, the discipline that section 57 regression 1
    // was about.
    private bool DriveItemRotation()
    {
        if (playerInput is null || !carrying)
            return false;

        if (ButtonEdge.ReadAxis(leftSqueeze) <= 0.6f)
            return false;

        // The calibration chord holds this same grip, so without this a carried
        // item would spin while the washer was being aligned. The washer-zone
        // gesture is on it too - though that one already refuses to fire while
        // carrying, so this is the belt to its braces.
        if (CalibrateSuppressed() || ZoneGestureSuppressed())
            return false;

        try
        {
            var raw = moveAction?.ReadValueAsObject();
            var stick = raw is null ? Vector2.zero : raw.Unbox<Vector2>();

            // Rescaled past the deadzone so the first usable degree of travel is
            // not a jump, the same shape ReadTurn uses for the body yaw.
            var x = stick.x;

            if (Mathf.Abs(x) < turnDeadzone.Value)
                return true;

            var span = Mathf.Max(0.0001f, 1f - turnDeadzone.Value);
            var scaled = Mathf.Clamp01((Mathf.Abs(x) - turnDeadzone.Value) / span);
            var amount = Mathf.Sign(x) * scaled * itemRotateSpeed.Value * Time.unscaledDeltaTime;

            playerInput.InvokeItemRotated(amount);

            if (Dev(verboseDiagnostics) && Time.unscaledTime >= nextRotateReport)
            {
                nextRotateReport = Time.unscaledTime + 1f;
                LoggerInstance.Msg($"item rotate: sent {amount:0.###} "
                    + $"(stick {x:0.##}, speed {itemRotateSpeed.Value:0.#} deg/s)  "
                    + $"{InteractionText()}");
            }

            return true;
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  item rotation threw {exception.GetType().Name}: "
                + exception.Message);
            return false;
        }
    }

    private float nextRotateReport;

    // DIE HALTUNG, AUS DER KETTE GEHOLT.
    //
    // GetComponentInParent vom aufgeloesten Anker aus: EquipmentAnchor ->
    // PlayerCamera -> HeadTurn -> Spielerwurzel. Damit ist es der Controller
    // DIESES Spielers und nicht der eines Mitspielers.
    //
    // Liest die Haltung nicht, bleibt alles wie vorher - false heisst "nicht
    // gesperrt". Eine geratene Haltung waere schlimmer als keine.
    private bool RunBlockedByStance(float magnitude)
    {
        try
        {
            // Unity-null UND Muster-null: eine zerstoerte Komponente ist kein
            // Nullzeiger, und "is null" sieht sie nicht.
            if (characterController is null || characterController == null)
            {
                characterController = anchor is null || anchor == null
                    ? null
                    : anchor.GetComponentInParent<Il2CppFuturLab.PW2.BaseCharacterController>();

                if (characterController is null || characterController == null)
                {
                    characterController = null;

                    if (!loggedNoController)
                    {
                        loggedNoController = true;
                        LoggerInstance.Msg("stance: no BaseCharacterController above"
                            + " the anchor - run stays as it was.");
                    }

                    return false;
                }

                loggedNoController = false;
            }

            var stance = characterController.CharacterStance;

            if (stance == Il2CppFuturLab.PW2.CharacterStance.Standing)
            {
                loggedBlockedStance = -1;
                return false;
            }

            // EINMAL PRO HALTUNG. ChangeStanceMode steht mit dabei, weil an ihm
            // haengt, ob die Haltungstaste einmal oder zweimal zu druecken ist -
            // er ist nur lesbar, nativ ist sein Setter Protected.
            if ((int)stance != loggedBlockedStance)
            {
                loggedBlockedStance = (int)stance;
                LoggerInstance.Msg($"stance: {stance} - run blocked at "
                    + $"|stick| {magnitude:0.##}   stanceMode "
                    + $"{(playerInput is null || playerInput == null ? "?" : playerInput.ChangeStanceMode.ToString())}"
                    + $"   cap {crouchedMoveCap.Value:0.##}");
            }

            return true;
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning("  stance read threw "
                + $"{exception.GetType().Name}: {exception.Message}");
            characterController = null;
            return false;
        }
    }

    private void DriveSprint()
    {
        if (moveAction is null || playerInput is null)
            return;

        try
        {
            var raw = moveAction.ReadValueAsObject();
            var stick = raw is null ? Vector2.zero : raw.Unbox<Vector2>();
            var wanted = stick.magnitude > 0.9f;

            // KEIN RENNEN IM HOCKEN UND IM LIEGEN, und der Eingriff gehoert
            // genau hierher.
            //
            // DIESE Zeile darueber macht aus Stickstaerke ein Rennen - nicht
            // das Spiel. Gemeldet war "zu weit ausgeschlagen und der Charakter
            // steht auf", und weil die Unterbodenreinigung in PWS2 haeufig
            // gehockt oder liegend gemacht wird, kostet das jedes Mal einen
            // Haltungswechsel.
            //
            // Nur der AUSBRUCH ins Rennen wird gesperrt. Die Stickstaerke geht
            // unveraendert weiter, gehocktes Gehen behaelt sein volles Tempo,
            // und die Haltungstaste bleibt die einzige Stelle, die die Haltung
            // aendert.
            //
            // Geprueft wird nur, wenn ein Rennen ueberhaupt gewollt ist - also
            // jenseits von 0.9 Auslenkung. Im Gehen und im Stillstand kostet
            // das keinen Lesevorgang.
            if (wanted && RunBlockedByStance(stick.magnitude))
                wanted = false;

            if (wanted == sprintHeld)
                return;

            sprintHeld = wanted;
            playerInput.Sprint = wanted;
        }
        catch
        {
            // Movement already reports its own failures; this must not add a
            // second warning per frame for the same cause.
            sprintHeld = false;
        }
    }

    // PwsPlayerInput.ToggleGameMenu is one level closer to the real Escape
    // binding than BaseInput.InvokePaused - it is exactly what
    // OnGameMenuTogglePressed calls - and it takes no parameter, so there is no
    // meaning to guess. InvokePaused is the fallback if the cast comes up dead.
    //
    // The game DEBOUNCES pause through m_timeBeforePauseIsActiveAgain, so a
    // press that seems ignored right after closing the menu is expected
    // behaviour and not a broken call.
    private string selectionState = "";

    // THE UI ROUTE, and it is NOT the virtual gamepad section 73 planned.
    //
    // That plan was InputSystem.AddDevice<Gamepad>() plus InputState.Change. The
    // device half is safe - AddDevice(string, string, string) is non-generic -
    // but every state-feeding route is a GENERIC WITH A STRUCT parameter:
    // InputState.Change(InputControl, M0, ...), QueueStateEvent(InputDevice, M0,
    // double), QueueDeltaStateEvent(InputControl, M0, double). That is precisely
    // the family that hard-killed this process twice, with no managed exception
    // and a log that simply ended mid-line. Section 73 designed the plan without
    // checking the shapes.
    //
    // Il2CppInterop offers something far better, and probing found it:
    // ExecuteEvents carries NON-GENERIC overloads per interface -
    // Execute(ICancelHandler, BaseEventData), Execute(ISubmitHandler, ...),
    // Execute(IMoveHandler, ...). An interface reference plus a class. No struct
    // crosses the boundary, no generic is instantiated, and no synthetic device
    // is added - so PwsPlayerInput.TrySetControlScheme and
    // InputModuleManager.OnInputDeviceChanged are never provoked and the game
    // cannot flip to gamepad symbology.
    //
    // Cancel is the whole blocker: ESC closes both the task list and the main
    // menu, and ESC is the UI map's Cancel. This is that call, reached without a
    // device.
    private bool TryUiCancel()
    {
        try
        {
            var system = UnityEngine.EventSystems.EventSystem.current;

            if (system is null || system == null)
                return false;

            var selected = system.currentSelectedGameObject;

            if (selected is null || selected == null)
                return false;

            // GetComponent by Il2CppType rather than by generic type argument.
            // An interface as a generic parameter across this interop is
            // untested, and the typed route costs nothing here.
            var component = selected.GetComponent(
                Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.EventSystems.ICancelHandler>());

            var handler = component?.TryCast<UnityEngine.EventSystems.ICancelHandler>();

            if (handler is null)
            {
                LoggerInstance.Msg($"ui cancel: \"{selected.name}\" is selected but carries "
                    + "no ICancelHandler");
                return false;
            }

            UnityEngine.EventSystems.ExecuteEvents.Execute(handler,
                new UnityEngine.EventSystems.BaseEventData(system));

            LoggerInstance.Msg($"ui cancel: sent to \"{selected.name}\"");
            return true;
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  ui cancel threw {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    // What the EventSystem has selected, logged on change.
    //
    // This is the gate every further UI control depends on. If a selection
    // exists during normal gameplay, then Submit and navigation cannot simply be
    // bound to gameplay buttons - they would be hijacked. If a selection appears
    // only while a menu is open, the gate is sound and the rest follows cheaply.
    private void ReportUiSelection()
    {
        try
        {
            // DAS SPIEL, nicht current: diese Zeile stand drei Laeufe lang
            // dauerhaft auf "none", weil sie das EventSystem von UnityExplorer
            // gelesen hat. Ein Messgeraet, das die falsche Groesse liest,
            // luegt ueberzeugend.
            var system = GameEventSystem();
            var selected = system is null || system == null
                ? null
                : system.currentSelectedGameObject;

            var now = selected is null || selected == null ? "none" : selected.name;

            if (string.Equals(now, selectionState, StringComparison.Ordinal))
                return;

            selectionState = now;

            var module = system is null || system == null
                ? "no system"
                : system.currentInputModule is null ? "none" : system.currentInputModule.name;

            LoggerInstance.Msg($"ui selection: {now}   module {module}");
        }
        catch (Exception exception)
        {
            if (selectionState != "threw")
            {
                selectionState = "threw";
                LoggerInstance.Warning($"  ui selection read threw {exception.GetType().Name}");
            }
        }
    }

    // THE GAME'S OWN UI SURFACE, and it is what finally makes menus reachable.
    //
    // Three routes were examined before this one, in order of how much was
    // assumed and how little was checked:
    //
    //   virtual gamepad     section 73's plan. AddDevice(string,string,string) is
    //                       safe, but EVERY state-feeding route is a generic with
    //                       a STRUCT parameter - InputState.Change(InputControl,
    //                       M0, ...), QueueStateEvent(InputDevice, M0, double),
    //                       QueueDeltaStateEvent. That is the family that killed
    //                       this process twice with no managed exception.
    //   ExecuteEvents       safe shapes, non-generic per interface - but measured
    //                       "ui selection: none": the EventSystem never holds a
    //                       selected object, so Cancel has no target.
    //   FuturButton         void Submit(), void ForceSelect(), string Caption,
    //                       bool IsInteractable. Plus FuturPopupManager
    //                       .DismissAll(), void and parameterless.
    //
    // The third needs no device, no selection and no synthetic input, so the game
    // cannot flip to gamepad symbology either - the price the user had agreed to
    // pay simply does not arise.
    //
    // This is the INVENTORY step. Before anything is driven, the log has to say
    // what buttons are on screen and what they are called, because "close the
    // task list" has to map to something real rather than to a guess.
    private void ReportUiButtons(string reason)
    {
        try
        {
            var found = Resources.FindObjectsOfTypeAll(
                Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.UIStateMonoBehaviour>());

            var listed = 0;

            for (var index = 0; index < found.Length && listed < 12; index++)
            {
                var button = found[index]?.TryCast<Il2CppFuturLab.FuturButton>();

                if (button is null || button == null)
                    continue;

                // Only what is actually on screen. FindObjectsOfTypeAll returns
                // every prefab and every cached screen too, and a list of forty
                // inactive buttons says nothing about what the player sees.
                // Active AND interactable only. The first run printed twelve
                // task list entries per press, and a task caption says nothing
                // about what can be pressed.
                if (!button.gameObject.activeInHierarchy || !button.IsInteractable)
                    continue;

                listed++;

                var caption = "?";
                try { caption = button.HasCaption ? button.Caption : "(no caption)"; }
                catch { caption = "(caption threw)"; }

                LoggerInstance.Msg($"    ui button [{listed}] \"{caption}\"   "
                    + $"interactable {button.IsInteractable}   node {button.name}");
            }

            LoggerInstance.Msg($"  ui buttons ({reason}): {listed} active of {found.Length} total");
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  ui button inventory threw {exception.GetType().Name}: "
                + exception.Message);
        }
    }

    // THE CLOSE BUTTON, submitted directly.
    //
    // The inventory found it on the first run: with the task list open, an
    // ACTIVE and INTERACTABLE FuturButton sits on a node called
    // ContextButton_Clickable(Clone), captioned "Schliessen". A moment later,
    // with the list closed, the same nodes read interactable False. So
    // "is there an interactable context button on screen" is exactly the
    // question "is there something to close".
    //
    // Matched on the NODE NAME, never on the caption: the caption is localised -
    // it read "Schliessen" on a German system - and a mod that only closes menus
    // in one language is broken by construction.
    //
    // FuturButton.Submit() is virtual, public, void and parameterless. No
    // selection, no device, no synthetic input.
    //
    // DismissAll was tried here first and caused a REGRESSION: it returned true
    // whenever a popup MANAGER existed rather than when something was actually
    // dismissed, so the chain short-circuited and the pause menu stopped opening
    // altogether. A handler that reports success for doing nothing is worse than
    // no handler.
    // WELCHER SCHRITT HAT DEN DRUCK VERBRAUCHT - Abschnitt 103.
    //
    // Gemeldet: das Hauptmenue laesst sich mit dem Menue-Knopf nicht mehr
    // schliessen. Aus dem Log war das NICHT zuzuordnen, und der Grund ist die
    // Form der Kette selbst:
    //
    //     if (menuButton.Tap && !TrySubmitCloseButton() && !TryUiCancel())
    //         ToggleGameMenu();
    //
    // Verbraucht der erste Schritt den Druck, schweigt die Kette - geloggt wird
    // nur der Fehlschlag. Und die Zeilen, die da waren, widersprechen sich:
    // "Button_Close is selected" heisst, das Menue war offen, waehrend menuMode
    // aus war und der Druck darum den Gameplay-Zweig nahm.
    //
    // Diese Methode aendert die Reihenfolge NICHT. Sie ist dieselbe Kette mit
    // einer Zeile pro Druck: welcher Zweig, welcher Schritt, was war ausgewaehlt,
    // und wie liest der Zustand danach. Ein stummer Druck ist die Luecke, die
    // Abschnitt 99 einen ganzen Lauf gekostet hat.
    // Was nach dem letzten Kontextknopf-Submit auf dem Schirm stand. Bleibt es
    // beim naechsten Druck gleich, war das Submit folgenlos.
    private string menuPressSignature = "";
    private bool menuPressUsedContext;

    private void PressMenuButton(string branch)
    {
        // ALLE SPALTEN VOR DER TAT GELESEN, und das war im ersten Anlauf
        // falsch: menuMode und allowsMovement standen nach dem Umschalten in
        // der Zeile, selected davor. Die Zeile las sich dann als Widerspruch -
        // "menuMode off" neben "allowsMovement False" -, obwohl beide stimmten,
        // nur zu verschiedenen Zeitpunkten. Ein Block, eine Momentaufnahme.
        var before = InteractionText();
        var selected = SelectedName();
        var menuModeBefore = menuMode ? "ON" : "off";
        var allowsBefore = AllowsMovementText();
        var step = "toggle";

        // DER ZWEITE DRUCK SCHALTET UM, und das ist die Behebung des
        // Lobby-Falls.
        //
        // Gemessen: im Level UND in der Lobby findet die Suche denselben Knopf,
        // "Schliessen" auf ContextButton_Clickable(Clone). Im Level schliesst
        // er, in der Lobby bleibt sein Submit folgenlos - und verbraucht dabei
        // den Druck, sodass ToggleGameMenu nie erreicht wird. Der Kandidat ist
        // also nicht der falsche; seine WIRKUNG fehlt.
        //
        // Ein Namensfilter waere hier geraten. Die Folge dagegen ist messbar:
        // hat der letzte Druck den Kontextknopf benutzt und steht danach
        // dasselbe auf dem Schirm wie jetzt, dann hat er nichts bewirkt, und
        // dieser Druck ueberspringt den Schritt. Im Level, wo das Submit
        // wirkt, aendert sich die Signatur - und dort bleibt alles, wie es ist.
        var signature = MenuSignature();
        var skipContext = menuPressUsedContext
            && string.Equals(signature, menuPressSignature, StringComparison.Ordinal);

        if (!skipContext && TrySubmitCloseButton())
        {
            step = "context button";
            menuPressUsedContext = true;
        }
        else if (TryUiCancel())
        {
            step = skipContext ? "ui cancel (context had no effect)" : "ui cancel";
            menuPressUsedContext = false;
        }
        else
        {
            ToggleGameMenu();
            step = skipContext ? "toggle (context had no effect)" : "toggle";
            menuPressUsedContext = false;
        }

        // NACH der Tat gemerkt, denn verglichen wird beim naechsten Druck
        // gegen den Zustand, den dieser hinterlassen hat.
        menuPressSignature = MenuSignature();

        LoggerInstance.Msg($"menu press [{branch}]: {step}"
            + $"   menuMode {menuModeBefore}"
            + $"   selected {selected}"
            + $"   allowsMovement {allowsBefore} -> {AllowsMovementText()}"
            + $"   state {before} -> {InteractionText()}");
    }

    // Woran ein folgenloses Submit zu erkennen ist: Menuelage, Auswahl und
    // Interaktionszustand in einer Zeichenkette. Drei billige Lesungen, und
    // sie aendern sich alle, wenn ein Menue wirklich zugeht.
    private string MenuSignature() =>
        $"{menuMode}|{SelectedName()}|{AllowsMovementText()}|{InteractionText()}";

    // Der Name des ausgewaehlten UI-Objekts, oder warum keiner. Er ist die
    // Spalte, an der sich "das Menue war offen" von "das Menue war zu"
    // unterscheiden laesst.
    private string SelectedName()
    {
        try
        {
            var system = GameEventSystem();

            if (system is null || system == null)
                return "no EventSystem";

            var selected = system.currentSelectedGameObject;

            return selected is null || selected == null ? "nothing" : $"\"{selected.name}\"";
        }
        catch (Exception exception)
        {
            return $"threw {exception.GetType().Name}";
        }
    }

    // AllowsPlayerMovement ist die Groesse, aus der menuMode entsteht. Sie hier
    // mitzulesen trennt "der Mod hat sich geirrt" von "das Spiel meldet es so".
    //
    // DERSELBE PFAD WIE IN MenuModeActive, und das ist keine Bequemlichkeit:
    // PwsPlayerInput.CurrentState war geraten und existiert nicht - der Build
    // hat es abgewiesen. Die Groesse haengt am SCREEN MANAGER, nicht am Input,
    // und zwei Wege zu derselben Zahl waeren ausserdem zwei Antworten.
    private string AllowsMovementText()
    {
        try
        {
            var state = Il2CppFuturLab.PW2.PwsScreenManager.Instance?
                .MainViewport?.CurrentState;

            return state is null || state == null
                ? "no state"
                : state.AllowsPlayerMovement.ToString();
        }
        catch (Exception exception)
        {
            return $"threw {exception.GetType().Name}";
        }
    }

    private bool TrySubmitCloseButton()
    {
        try
        {
            var found = Resources.FindObjectsOfTypeAll(
                Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.UIStateMonoBehaviour>());

            for (var index = 0; index < found.Length; index++)
            {
                var button = found[index]?.TryCast<Il2CppFuturLab.FuturButton>();

                if (button is null || button == null)
                    continue;

                if (!button.gameObject.activeInHierarchy || !button.IsInteractable)
                    continue;

                if (button.name.IndexOf("ContextButton", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var caption = "?";
                try { caption = button.HasCaption ? button.Caption : "(none)"; } catch { }

                button.Submit();
                LoggerInstance.Msg($"ui: submitted context button \"{caption}\" on {button.name}");
                return true;
            }

            return false;
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  close button submit threw {exception.GetType().Name}: "
                + exception.Message);
            return false;
        }
    }

    // THE RETICLE, measured against the reproduction rather than guessed at.
    //
    // The report is specific and that is what makes it usable: opening the task
    // list with Y and closing it again leaves the crosshair completely rotated.
    // A reproducible trigger was the one thing missing - gunRoll had been logged
    // for two runs and only showed that the wrist rolls between -50.8 and +82.6
    // degrees, which is consistent with the reticle following the wash direction
    // and therefore with CORRECT behaviour that merely looks wrong.
    //
    // The structure says something different, though. IReticleWidgetView carries
    // NozzleRotationChanged as an Action<bool> - the nozzle fan flip, horizontal
    // against vertical - not a continuous angle. So a "completely rotated"
    // crosshair may be that boolean state flipping rather than an angle drifting.
    //
    // Logged on both sides of the transition, and by SUBSTRING because the node
    // name is unknown. Walking only the UI subtree keeps it cheap; a scene-wide
    // Transform search would return thousands.
    private void ReportReticle(string when)
    {
        try
        {
            var root = gameUi.Root;

            if (root is null)
            {
                LoggerInstance.Msg($"  reticle ({when}): no ui root");
                return;
            }

            reticleNodes.Clear();
            CollectBySubstring(root, "retic", reticleNodes);

            if (reticleNodes.Count == 0)
            {
                LoggerInstance.Msg($"  reticle ({when}): no node matching \"retic\" under the ui root");
                return;
            }

            for (var index = 0; index < reticleNodes.Count && index < 6; index++)
            {
                var node = reticleNodes[index];

                LoggerInstance.Msg($"  reticle ({when}) {node.name}   "
                    + $"active {node.gameObject.activeInHierarchy}   "
                    + $"localEuler {Vector(node.localEulerAngles)}   "
                    + $"worldEuler {Vector(node.eulerAngles)}   "
                    + $"scale {Vector(node.localScale)}");
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  reticle read threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    private readonly List<Transform> reticleNodes = new();
    private float nextReticleReport;
    private readonly List<Transform> reticleClampNodes = new();
    private float nextReticleClampSearch;
    private bool loggedReticleClamp;

    // THE RETICLE FIX, and the carrier was found by searching for ROTATION
    // instead of for a name.
    //
    // Measured over one run:
    //
    //   …/Up_Down_Sides/Reticle_Up     localEuler (0, 0, -90)      47 samples
    //   …/Up_Down_Sides/Reticle_Down   localEuler (-0, 0, 90)      47 samples
    //   ReticleWidget/WasherReticles   localEuler (0.5, 86.8, 1.6)
    //   ReticleWidget/WasherReticles   localEuler (4.8, 155.9, -2.1)
    //
    // The two arrows at exactly plus and minus ninety are design and are left
    // alone. WasherReticles is the defect: a large, wandering YAW between 55 and
    // 156 degrees. A yaw, not a roll - which is why three runs of roll
    // correlation found nothing. hmdRoll stays within +-10, gunRoll within +-37,
    // camRoll is exactly 0 in every sample, and none of them reaches 156.
    //
    // The game gives this node a WORLD-referenced orientation. The canvas faces
    // the camera, so the LOCAL yaw is the difference between that world
    // orientation and the view - which is precisely the report: "it always aligns
    // to the same world direction, not to where the avatar is looking", and it
    // looks correct only when the player turns to face that direction. In the
    // flat game camera yaw and body yaw coincide and it can never show; in VR
    // bodyYaw and the decoupled gun separate them.
    //
    // Clamped to identity per frame, the same shape that already carries
    // RaySpawnPoint.localRotation (bit 128) and the nozzle anchor's lateral
    // offset (bit 65536), both verified to survive the game's own rewrites. The
    // children keep their own rotations, so the arrows stay intact.
    private void ClampReticle(bool enable)
    {
        if (!enable)
            return;

        try
        {
            var root = gameUi.Root;

            if (root is null)
                return;

            // Re-searched on a timer: the widget is rebuilt with the washer, so a
            // cached transform would go stale on an equipment change.
            if (reticleClampNodes.Count == 0 || Time.unscaledTime >= nextReticleClampSearch)
            {
                nextReticleClampSearch = Time.unscaledTime + 0.5f;
                reticleClampNodes.Clear();
                CollectBySubstring(root, "WasherReticles", reticleClampNodes);
            }

            var clamped = 0;

            for (var index = 0; index < reticleClampNodes.Count; index++)
            {
                var node = reticleClampNodes[index];

                if (node is null || node == null)
                    continue;

                if (node.localRotation != Quaternion.identity)
                    node.localRotation = Quaternion.identity;

                clamped++;
            }

            if (!loggedReticleClamp && clamped > 0)
            {
                loggedReticleClamp = true;
                LoggerInstance.Msg($"  reticle clamp: {clamped} WasherReticles node(s) held at identity");
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  reticle clamp threw {exception.GetType().Name}: {exception.Message}");
            reticleClampNodes.Clear();
        }
    }

    // A TIME SERIES, because the before/after pair could not see this.
    //
    // Two methodological misses in a row, opposite in direction. The gun-to-muzzle
    // distance was once paired across two log lines up to a second apart, which
    // invented a variation that was not there. This was the mirror image: the
    // reticle was sampled 6 ms either side of the button press, so an effect that
    // lands a frame or more later - when the game re-lays out its viewports - fell
    // outside the window entirely.
    //
    // What a freeze actually looks like is a DELTA THAT GROWS. A ScreenSpaceCamera
    // canvas is oriented to face its camera every frame, so the angle between the
    // canvas forward and the camera forward is near-constant while it is healthy
    // and opens up the moment it stops tracking. That cannot be read from one
    // pair; it needs the same quantity once a second.
    //
    // The lapse hypothesis is already dead: "conversion lapsed" appeared ZERO
    // times, so renderMode and worldCamera both survive. The remaining candidate
    // is that the ACTIVE reticle moves to a different canvas - one that was never
    // converted - which is why the node count and the owning canvas are logged
    // too rather than just the angle.
    private void ReportReticleSeries()
    {
        // Before the timer, not after: this one recurses through the reticle
        // subtree via ReportTurnedDescendants, which is the expensive half.
        if (!Dev(verboseDiagnostics))
            return;

        if (Time.unscaledTime < nextReticleReport)
            return;

        nextReticleReport = Time.unscaledTime + 1f;

        try
        {
            var root = gameUi.Root;
            var camera = Camera.main;

            if (root is null || camera is null || camera == null)
                return;

            var canvas = root.GetComponent<Canvas>();
            var delta = Vector3.Angle(root.forward, camera.transform.forward);

            reticleNodes.Clear();
            CollectBySubstring(root, "retic", reticleNodes);

            var active = 0;
            for (var index = 0; index < reticleNodes.Count; index++)
            {
                if (reticleNodes[index].gameObject.activeInHierarchy)
                    active++;
            }

            // Scene-wide count beside the root-scoped one. If the root-scoped
            // count drops while the scene-wide count holds, the active reticle
            // has moved to a canvas this mod never converted - and that canvas
            // would be world-fixed by construction.
            var everywhere = 0;
            try
            {
                var all = Resources.FindObjectsOfTypeAll(
                    Il2CppInterop.Runtime.Il2CppType.Of<Transform>());

                for (var index = 0; index < all.Length; index++)
                {
                    var node = all[index]?.TryCast<Transform>();

                    if (node is null || node == null)
                        continue;

                    if (node.name.IndexOf("Reticle", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    if (node.gameObject.activeInHierarchy)
                        everywhere++;
                }
            }
            catch
            {
                everywhere = -1;
            }

            // THE ROTATED NODE, found by its ROTATION rather than by its name.
            //
            // This is the gap that cost three runs. The search only ever examined
            // nodes whose NAME contains "retic", and all of those read
            // localEuler (0, 0, 0) - so the conclusion "nothing in the chain is
            // rotated" was drawn from a sample that could not contain the answer.
            // The rotated node may sit deeper and be called Image, Dot or
            // Crosshair.
            //
            // So: walk the whole subtree under every reticle node and report
            // anything actually turned. 0.5 degrees of threshold keeps layout
            // jitter out without hiding a real tilt.
            var turned = 0;

            for (var index = 0; index < reticleNodes.Count; index++)
            {
                turned += ReportTurnedDescendants(reticleNodes[index], reticleNodes[index].name, 0);
            }

            // THE HMD ROLL, for the correlation the data now points at. The game
            // camera reads z = 0.00 in every single sample - DriveHead writes yaw
            // to HeadTurn and pitch to PlayerCamera per section 35 and discards
            // roll entirely - so if a reticle follows the real world up while the
            // view cannot roll, it appears turned exactly as reported, and looks
            // right only when the head tilt happens to match.
            var hmdRoll = float.NaN;
            if (TryReadHeadPose(out var hmdRotation, out _))
                hmdRoll = SignedAngle(hmdRotation.eulerAngles.z);

            LoggerInstance.Msg($"  reticle turned nodes {turned}   "
                + $"hmdRoll {(float.IsNaN(hmdRoll) ? "n/a" : hmdRoll.ToString("0.#", Invariant))}   "
                + $"camRoll {SignedAngle(camera.transform.eulerAngles.z).ToString("0.#", Invariant)}   "
                + $"gunRoll {(assembly is null ? "n/a" : SignedAngle(assembly.eulerAngles.z).ToString("0.#", Invariant))}");

            LoggerInstance.Msg($"  reticle series: canvasVsCamera {delta:0.#} deg   "
                + $"mode {(canvas is null ? "no canvas" : canvas.renderMode.ToString())}   "
                + $"worldCam {(canvas?.worldCamera is null ? "NULL" : "set")}   "
                + $"activeUnderRoot {active}/{reticleNodes.Count}   activeInScene {everywhere}   "
                + $"canvasEuler {Vector(root.eulerAngles)}   camEuler {Vector(camera.transform.eulerAngles)}");
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  reticle series threw {exception.GetType().Name}: {exception.Message}");
            nextReticleReport = Time.unscaledTime + 10f;
        }
    }

    // Recursive, depth-capped, and it reports the PATH rather than just the name,
    // because a node called "Image" is meaningless without its parent.
    private int ReportTurnedDescendants(Transform node, string path, int depth)
    {
        if (depth > 6)
            return 0;

        var found = 0;

        for (var index = 0; index < node.childCount; index++)
        {
            var child = node.GetChild(index);

            if (child is null || child == null)
                continue;

            var childPath = path + "/" + child.name;
            var euler = child.localEulerAngles;

            var x = SignedAngle(euler.x);
            var y = SignedAngle(euler.y);
            var z = SignedAngle(euler.z);

            if (Mathf.Abs(x) > 0.5f || Mathf.Abs(y) > 0.5f || Mathf.Abs(z) > 0.5f)
            {
                found++;

                if (found <= 6)
                    LoggerInstance.Msg($"    TURNED {childPath}   "
                        + $"localEuler ({x:0.#}, {y:0.#}, {z:0.#})   "
                        + $"active {child.gameObject.activeInHierarchy}");
            }

            found += ReportTurnedDescendants(child, childPath, depth + 1);
        }

        return found;
    }

    private static void CollectBySubstring(Transform node, string needle, List<Transform> into)
    {
        if (node.name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
            into.Add(node);

        for (var index = 0; index < node.childCount; index++)
            CollectBySubstring(node.GetChild(index), needle, into);
    }

    // THE COMMON BASE, not FuturButton.
    //
    // Reported: in the main menu certain tiles are never reached in any
    // direction. They were never in the list. Only three types derive from
    // UIStateMonoBehaviour - FuturButton, FuturToggle and TabButton - and the
    // scan collected the first only, so every tile that happens to be a toggle
    // was invisible to navigation.
    //
    // The base carries everything needed: bool IsSelected, UIState
    // CurrentUIState and a virtual ForceSelect(). Activation still differs per
    // type and is handled by cast at the point of use.
    private readonly List<Il2CppFuturLab.UIStateMonoBehaviour> menuButtons = new();
    private float nextMenuScan;

    // Nur fuer die Scan-Zeile: welche Kandidaten als "offscreen" herausfielen.
    // Drei genuegen, um ein Popup zu erkennen - siehe Abschnitt 107.
    private readonly List<string> offscreenNames = new();

    // Die gesperrten Zeilen, mit Namen. Dasselbe Mittel wie bei den
    // offscreen-Zeilen und aus demselben Grund: eine Zahl sagt nicht, WELCHE.
    private readonly List<string> lockedNames = new();
    private int menuIndex = -1;

    // WELCHE KACHEL GEWAEHLT IST, am nativen Pointer festgehalten - nicht als
    // Index.
    //
    // Gemessen im Hauptmenue-Tab: die Kandidatenliste wechselt im SELBEN
    // Bildschirm ihre Laenge, 12 / 19 / 13. Die Sichtbarkeitsfilter des Scans
    // nehmen dort Elemente zeitweise heraus, und damit verliert ein INDEX
    // seinen Bezug - der Log zeigt "menu select [8/12]" und kurz darauf
    // "[8/19]" mit derselben Beschriftung. Jedes dieser Select startet den
    // Uebergang der Kachel von vorn, und genau das ist das Blinken, das in
    // allen anderen Tabs fehlt.
    //
    // Der Index bleibt, weil die Stick-Navigation auf ihm laeuft. Diese Zahl
    // beantwortet nur die Frage "ist das ueberhaupt eine ANDERE Kachel" - und
    // beantwortet sie so, wie es diese Datei ueberall tut: am Pointer, weil
    // Il2CppInterop bei jedem Zugriff einen frischen Wrapper herausgibt.
    private IntPtr menuSelectedPointer;
    private bool menuNavArmed = true;
    private string menuModeState = "";
    private int loggedMenuCount = -1;
    private bool menuMode;

    // The game's OWN idea of what the controller cursor is pointing at.
    //
    // cursorActive read True in the pause menu, so the game expects a CURSOR
    // rather than a selection - and PwsGameStateBase.CursorScreenTarget is a
    // FuturButton, i.e. the button under that cursor. If it is ever non-null,
    // driving the menu needs no ordering and no ForceSelect at all: read the
    // target, Submit it. If it stays null, nothing moves the cursor and the
    // ordering approach is the right one after all.
    private string CursorTargetText()
    {
        try
        {
            var state = Il2CppFuturLab.PW2.PwsScreenManager.Instance?
                .MainViewport?.CurrentState;

            var target = state?.CursorScreenTarget;

            return target is null || target == null ? "none" : CaptionOf(target);
        }
        catch (Exception exception)
        {
            return "threw " + exception.GetType().Name;
        }
    }

    // PHASE 0 OF THE MENU POINTER, corrected after the first run measured
    // nothing at all.
    //
    // WHAT THE FIRST VERSION GOT WRONG, written down because it must not come
    // back: it took the FIRST ControllerCursorInputModule the scan returned.
    // That was a disabled, out-of-hierarchy Viewport1 module. Writing
    // m_screenPos into it and reading it back one frame later reported
    // HELD True - and the same value was still sitting there 73 seconds, several
    // screen changes and a stretch of gameplay later, because nothing on a
    // disabled component ever runs. All four candidates read enabled False and
    // screenPos (0,0,0), which is the signature of a bad SELECTION, not of an
    // absent feature.
    //
    // WHAT THAT RUN DID ESTABLISH, from the assembly probe that followed it: the
    // game ships the entire mechanism. InputModuleManager holds THREE modules -
    // mouse and keyboard, controller CURSOR, controller NAVIGATION - and chooses
    // between them from two bools, m_isCurrentlyGamepad and
    // m_useCursorWithController. Both have public setters, and
    // ForceUpdateModuleInUse() is public, void and parameterless. PWS2 therefore
    // already implements the pointer this project wants, switched off because no
    // gamepad is registered - which section 81 chose deliberately, to stop the
    // game flipping to gamepad symbology. That cost is now accepted: in a VR
    // build with motion controllers, gamepad glyphs are the more accurate
    // prompt.
    //
    // TWO KEYS, so a single run separates the two questions:
    //
    //   alt+Keypad7   report only. The write test now runs ONLY when an enabled
    //                 module exists, and says so plainly when none does.
    //   alt+Keypad9   flip both bools, call ForceUpdateModuleInUse(), report
    //                 which module woke up. A TOGGLE: pressing it again restores
    //                 the saved values, so a bad outcome costs no restart.
    private bool cursorProbePending;
    private Vector3 cursorProbeWrote;
    private int cursorProbeFrame;
    private int cursorProbeStage;
    private string cursorProbeWhen = "";

    private bool cursorForced;
    private bool savedUseCursor;
    private bool savedIsGamepad;
    private bool savedValues;

    // PICKED, never taken blind - this is the correction the first probe needed.
    //
    // Order of preference: active and enabled, then merely in the hierarchy,
    // then nothing at all. The CHOICE is returned so it can be logged, because
    // "which of the four" is exactly the question the first run left unanswerable
    // after the fact.
    private Il2CppFuturLab.PW2.ControllerCursorInputModule? PickCursorModule(out string how)
    {
        how = "none";

        Il2CppFuturLab.PW2.ControllerCursorInputModule? enabled = null;
        Il2CppFuturLab.PW2.ControllerCursorInputModule? inTree = null;

        try
        {
            var found = Resources.FindObjectsOfTypeAll(
                Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.PW2.ControllerCursorInputModule>());

            for (var index = 0; index < found.Length; index++)
            {
                var candidate = found[index]?.TryCast<Il2CppFuturLab.PW2.ControllerCursorInputModule>();

                if (candidate is null || candidate == null)
                    continue;

                if (candidate.isActiveAndEnabled)
                {
                    if (enabled is null)
                    {
                        enabled = candidate;
                        how = $"[{index}] active and enabled";
                    }

                    continue;
                }

                if (inTree is null && candidate.gameObject.activeInHierarchy)
                {
                    inTree = candidate;
                    how = $"[{index}] in hierarchy but NOT enabled";
                }
            }
        }
        catch (Exception exception)
        {
            how = $"threw {exception.GetType().Name}";
            return null;
        }

        return enabled ?? inTree;
    }

    // WHO DECIDES. The manager is the thing that enables one module and disables
    // the others, so its two bools explain the whole observation from run one.
    private void ReportInputModuleManager()
    {
        try
        {
            var found = Resources.FindObjectsOfTypeAll(
                Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.PW2.InputModuleManager>());

            LoggerInstance.Msg($"  input module manager: {found.Length} instance(s)");

            for (var index = 0; index < found.Length; index++)
            {
                var manager = found[index]?.TryCast<Il2CppFuturLab.PW2.InputModuleManager>();

                if (manager is null || manager == null)
                    continue;

                try
                {
                    LoggerInstance.Msg($"    [{index}] node {manager.gameObject.name}"
                        + $"   inHierarchy {manager.gameObject.activeInHierarchy}"
                        + $"   isCurrentlyGamepad {manager.m_isCurrentlyGamepad}"
                        + $"   useCursorWithController {manager.m_useCursorWithController}"
                        + $"   gamepadPlayers {manager.m_gamepadPlayers}");

                    Describe("mouse+kb  ", manager.m_mouseAndKeyboardInputModule);
                    Describe("controller", manager.m_controllerInputModule);
                    Describe("navigation", manager.m_controllerNavigationInputModule);
                }
                catch (Exception exception)
                {
                    LoggerInstance.Warning($"    [{index}] manager read threw "
                        + $"{exception.GetType().Name}: {exception.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  input module manager scan threw "
                + $"{exception.GetType().Name}: {exception.Message}");
        }

        void Describe(string label, UnityEngine.EventSystems.BaseInputModule? module)
        {
            if (module is null || module == null)
            {
                LoggerInstance.Msg($"         {label}: MISSING");
                return;
            }

            var typeName = "?";

            try
            {
                typeName = ((Il2CppSystem.Object)module).GetType().Name;
            }
            catch
            {
                typeName = "(type read threw)";
            }

            LoggerInstance.Msg($"         {label}: {typeName}"
                + $"   node {module.gameObject.name}"
                + $"   enabled {module.enabled}"
                + $"   activeAndEnabled {module.isActiveAndEnabled}");
        }
    }

    private void ReportCursorModule(string when)
    {
        LoggerInstance.Msg($"cursor probe [{when}]");

        ReportInputModuleManager();

        var module = PickCursorModule(out var how);

        LoggerInstance.Msg($"  cursor module chosen: {how}");

        // The cursor objects, separately from the modules: the module does the
        // hit testing, these carry the visible graphic and the press state.
        try
        {
            var found = Resources.FindObjectsOfTypeAll(
                Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.PW2.ControllerCursor>());

            LoggerInstance.Msg($"  controller cursor: {found.Length} instance(s)");

            for (var index = 0; index < found.Length; index++)
            {
                var cursor = found[index]?.TryCast<Il2CppFuturLab.PW2.ControllerCursor>();

                if (cursor is null || cursor == null)
                    continue;

                try
                {
                    // m_viewportRect is deliberately NOT read: Rect comes across
                    // as a struct by value, the family this project does not
                    // touch without a reason.
                    LoggerInstance.Msg($"    [{index}] node {cursor.gameObject.name}"
                        + $"   inHierarchy {cursor.gameObject.activeInHierarchy}"
                        + $"   enabled {cursor.enabled}"
                        + $"   visible {cursor.Visible}"
                        + $"   hovering {cursor.Hovering}"
                        + $"   pressed {cursor.m_pressed}"
                        + $"   position {Vector(cursor.Position)}");
                }
                catch (Exception exception)
                {
                    LoggerInstance.Warning($"    [{index}] cursor read threw "
                        + $"{exception.GetType().Name}: {exception.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  controller cursor scan threw "
                + $"{exception.GetType().Name}: {exception.Message}");
        }

        // The game's own view, in the SAME block, because these readings are
        // only meaningful against each other: a null CursorScreenTarget while
        // the state says the cursor is active is the whole reason for this probe.
        try
        {
            var state = Il2CppFuturLab.PW2.PwsScreenManager.Instance?
                .MainViewport?.CurrentState;

            if (state is null || state == null)
            {
                LoggerInstance.Msg("  game state: none");
            }
            else
            {
                var stateName = "?";

                try
                {
                    stateName = ((Il2CppSystem.Object)state).GetType().Name;
                }
                catch
                {
                    stateName = "(type read threw)";
                }

                LoggerInstance.Msg($"  game state: {stateName}"
                    + $"   allowsMovement {state.AllowsPlayerMovement}"
                    + $"   cursorActive {state.IsControllerCursorActive}"
                    + $"   cursorTarget {CursorTargetText()}");
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  game state read threw "
                + $"{exception.GetType().Name}: {exception.Message}");
        }

        // THE WRITE TEST, and it now REFUSES to run on a disabled module. That
        // refusal is the entire lesson of run one: a write into something that
        // never ticks is retained forever and proves nothing.
        if (module is null || module == null)
        {
            LoggerInstance.Msg("  write test: SKIPPED, no cursor module found at all");
            cursorProbePending = false;
            return;
        }

        if (!module.isActiveAndEnabled)
        {
            LoggerInstance.Msg("  write test: SKIPPED, the chosen module is not enabled - "
                + "a write here would be retained by a dead component and mean nothing. "
                + "Press alt+Keypad9 to ask the game to enable it.");
            cursorProbePending = false;
            return;
        }

        try
        {
            var before = module.m_screenPos;
            var target = new Vector3(
                (Screen.width * 0.25f) + 7f, (Screen.height * 0.75f) + 3f, 0f);

            module.m_screenPos = target;

            LoggerInstance.Msg($"  write test: before {Vector(before)}   wrote {Vector(target)}"
                + $"   read back {Vector(module.m_screenPos)}");

            cursorProbeWrote = target;
            cursorProbeWhen = when;
            cursorProbeFrame = Time.frameCount;
            cursorProbeStage = 0;
            cursorProbePending = true;
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  write test threw "
                + $"{exception.GetType().Name}: {exception.Message}");
            cursorProbePending = false;
        }
    }

    // ASK THE GAME TO ENABLE ITS OWN CURSOR, and a toggle rather than a one-way
    // switch: if this breaks the menus it has to be undoable without a restart.
    //
    // Both bools are set, because the manager almost certainly tests
    // m_isCurrentlyGamepad first to choose controller over mouse, and only then
    // m_useCursorWithController to choose cursor over navigation. Setting one
    // without the other would leave the question half answered.
    //
    // ForceUpdateModuleInUse() rather than writing module.enabled directly: the
    // manager owns which module is enabled, and forcing the flag behind its back
    // would be overwritten the next time it refreshes. Public, void and
    // parameterless, so the shape costs nothing.
    private void ToggleNativeCursor()
    {
        try
        {
            var found = Resources.FindObjectsOfTypeAll(
                Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.PW2.InputModuleManager>());

            if (found.Length == 0)
            {
                LoggerInstance.Warning("native cursor: no InputModuleManager found");
                return;
            }

            var restoring = cursorForced;

            LoggerInstance.Msg($"native cursor: {(restoring ? "RESTORING" : "FORCING")}"
                + $" across {found.Length} manager instance(s)");

            for (var index = 0; index < found.Length; index++)
            {
                var manager = found[index]?.TryCast<Il2CppFuturLab.PW2.InputModuleManager>();

                if (manager is null || manager == null)
                    continue;

                try
                {
                    // Every instance's originals are LOGGED even though only the
                    // first is saved, so a difference between them cannot be
                    // lost silently.
                    LoggerInstance.Msg($"    [{index}] was"
                        + $"   isCurrentlyGamepad {manager.m_isCurrentlyGamepad}"
                        + $"   useCursorWithController {manager.m_useCursorWithController}");

                    if (!restoring && !savedValues)
                    {
                        savedValues = true;
                        savedIsGamepad = manager.m_isCurrentlyGamepad;
                        savedUseCursor = manager.m_useCursorWithController;
                    }

                    manager.m_isCurrentlyGamepad = restoring ? savedIsGamepad : true;
                    manager.m_useCursorWithController = restoring ? savedUseCursor : true;

                    manager.ForceUpdateModuleInUse();
                    manager.UpdateControllerCursorVisibility();
                }
                catch (Exception exception)
                {
                    LoggerInstance.Warning($"    [{index}] force threw "
                        + $"{exception.GetType().Name}: {exception.Message}");
                }
            }

            cursorForced = !restoring;

            // The report AFTER the change, so the effect is visible in the same
            // press rather than needing a second key.
            LoggerInstance.Msg("native cursor: state after the change");
            ReportInputModuleManager();

            var module = PickCursorModule(out var how);
            LoggerInstance.Msg($"  cursor module chosen: {how}"
                + $"   enabled {(module is null || module == null ? "-" : module.isActiveAndEnabled.ToString())}");
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"native cursor toggle threw "
                + $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    // THE CROSS-FRAME HALF of the write test, in TWO stages, and the staging is
    // the fix for run one's weakest point.
    //
    // Run one asked "is the value still there next frame" and got yes - from a
    // dead component, where it stayed for 73 seconds. Retention proves nothing
    // on its own. What a LIVE module looks like is the opposite: Process() runs
    // every frame, so the interesting evidence is whether the game ACTS on the
    // position - a hover, or a non-null CursorScreenTarget - not whether the
    // number survives.
    //
    // So stage one reads one frame later, stage two half a second later, and both
    // carry hovering and cursorTarget alongside. The written value is repeated on
    // every line so each reads on its own; pairing log lines by eye is how this
    // project produced three misdiagnoses.
    private void ReportCursorFollowUp()
    {
        if (!cursorProbePending)
            return;

        var elapsed = Time.frameCount - cursorProbeFrame;

        if (cursorProbeStage == 0 && elapsed < 1)
            return;

        if (cursorProbeStage == 1 && elapsed < 30)
            return;

        cursorProbeStage++;

        if (cursorProbeStage > 2)
        {
            cursorProbePending = false;
            return;
        }

        try
        {
            var module = PickCursorModule(out _);

            if (module is null || module == null)
            {
                LoggerInstance.Msg($"cursor probe [{cursorProbeWhen}] stage {cursorProbeStage}: "
                    + "module gone before the re-read");
                cursorProbePending = false;
                return;
            }

            var now = module.m_screenPos;
            var held = (now - cursorProbeWrote).sqrMagnitude < 0.25f;

            var hovering = "?";

            try
            {
                var cursors = Resources.FindObjectsOfTypeAll(
                    Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.PW2.ControllerCursor>());

                for (var index = 0; index < cursors.Length; index++)
                {
                    var cursor = cursors[index]?.TryCast<Il2CppFuturLab.PW2.ControllerCursor>();

                    if (cursor is null || cursor == null || !cursor.gameObject.activeInHierarchy)
                        continue;

                    hovering = $"{cursor.Hovering} at {Vector(cursor.Position)}";
                    break;
                }
            }
            catch
            {
                hovering = "(read threw)";
            }

            LoggerInstance.Msg($"cursor probe [{cursorProbeWhen}] stage {cursorProbeStage}"
                + $" after {elapsed} frame(s): wrote {Vector(cursorProbeWrote)}"
                + $"   now {Vector(now)}   HELD {held}"
                + $"   hovering {hovering}   cursorTarget {CursorTargetText()}");

            if (cursorProbeStage >= 2)
                cursorProbePending = false;
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  cursor follow-up threw "
                + $"{exception.GetType().Name}: {exception.Message}");
            cursorProbePending = false;
        }
    }

    // MENU NAVIGATION, on the game's own UI surface.
    //
    // The tools were established by the close-button work: FuturButton carries
    // ForceSelect(), ForceDeselect() and Submit(), all virtual, public, void and
    // parameterless, plus Caption as a string and IsInteractable as a bool. No
    // device is synthesised, so section 73's virtual gamepad - whose every
    // state-feeding route is a generic with a STRUCT parameter - is not needed,
    // and the game cannot flip to gamepad symbology.
    //
    // THE GATE IS THE HARD PART, not the navigation. Binding the left stick to
    // menu movement is worthless if it also hijacks walking, and "are there
    // active buttons on screen" is not a safe test - the task list alone put
    // twelve interactable buttons up. So the gate is the game's own authority:
    // PwsGameStateBase.AllowsPlayerMovement, a bool on a class reference. While
    // the game says the player may not move, a menu owns the input; while it
    // says they may, this code does nothing at all.
    private bool MenuModeActive()
    {
        try
        {
            var state = Il2CppFuturLab.PW2.PwsScreenManager.Instance?
                .MainViewport?.CurrentState;

            if (state is null || state == null)
                return false;

            var blocked = !state.AllowsPlayerMovement;
            // THE RUNTIME type, via Il2CppSystem.Object.GetType(). The managed
            // GetType() returns the INTEROP WRAPPER type, so it read
            // "PwsGameStateBase" for every screen and said nothing at all -
            // which is exactly how a diagnostic wastes a run.
            var stateName = "?";
            try
            {
                stateName = ((Il2CppSystem.Object)state).GetType().Name;
            }
            catch
            {
                stateName = "(type read threw)";
            }
            var now = stateName + "/" + blocked;

            if (!string.Equals(now, menuModeState, StringComparison.Ordinal))
            {
                menuModeState = now;
                LoggerInstance.Msg($"menu mode: {(blocked ? "ON" : "off")}   "
                    + $"state {stateName}   allowsMovement {state.AllowsPlayerMovement}   "
                    + $"cursorActive {state.IsControllerCursorActive}");

                // Dropped on every transition, so a stale index cannot select a
                // button on a screen that has since been replaced.
                menuButtons.Clear();
                menuIndex = -1;
                menuSelectedPointer = IntPtr.Zero;
                nextMenuScan = 0f;
                loggedMenuCount = -1;
                tabNodes.Clear();
                tabIndex = -1;
                loggedTabs = false;
                nextTabScan = 0f;
            }

            return blocked;
        }
        catch
        {
            return false;
        }
    }

    // Sorted by SCREEN POSITION, top to bottom then left to right, because
    // FuturButton carries no index and the order a scene search returns is
    // arbitrary. Without a stable order, "next" would jump around the screen.
    // Inside the viewport and in front of the camera. z <= 0 means behind, which
    // WorldToScreenPoint happily reports with plausible-looking x and y.
    //
    // THE RECTANGLE, NOT THE PIVOT - and this is the same defect section 90
    // fixed inside PointerSelect and left standing here, one filter upstream.
    //
    // node.position is the node's PIVOT. On a small tab that is nearly its
    // centre; on a large tile it sits wherever that tile's anchoring puts it,
    // which can be an edge or outside the viewport altogether. A tile in that
    // position was dropped from menuButtons ENTIRELY, so no amount of pointing
    // at it could ever highlight it - reported as one particular tile, the one
    // that leads to the level pack selection, never responding.
    //
    // Section 90 already wrote down why the pivot is the wrong point; it just
    // drew the conclusion in one of the two places that needed it.
    //
    // GetWorldCorners takes an Il2CppStructArray, a class reference to a native
    // array - the shape ScrollRect uses for its own m_Corners, and the same one
    // Splash.cs and the touch probe already pass across this boundary. Not
    // RectTransform.rect, which returns a struct by value and this project
    // leaves alone.
    //
    // The buffer is allocated ONCE, and LAZILY - not in a field initialiser.
    //
    // A field initialiser runs in the mod's CONSTRUCTOR, which MelonLoader calls
    // before OnInitializeMelon and therefore before the IL2CPP domain is
    // guaranteed to be attached. Allocating a native array there is exactly the
    // kind of load-time throw that takes the whole mod down before it logs a
    // line. Both existing uses in this project - the touch probe and
    // Splash.cs - build theirs inside a method for the same reason, and this one
    // now matches them. First menu scan, one allocation, never again.
    private Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>? cornerBuffer;

    // THE RECTANGLE IN SCREEN PIXELS, in one place.
    //
    // Lifted out of OnScreen because the pointer's miss report needs the exact
    // same four numbers - those are what RectangleContainsScreenPoint decides
    // against, so a report computed any other way could disagree with the
    // decision it is supposed to explain.
    //
    // A corner behind the camera projects to a mirrored point and must not widen
    // the box; one corner in front is enough to call the element reachable.
    private bool TryProjectRect(RectTransform rect, Camera camera,
        out float minX, out float maxX, out float minY, out float maxY)
    {
        minX = float.MaxValue;
        maxX = float.MinValue;
        minY = float.MaxValue;
        maxY = float.MinValue;

        cornerBuffer ??= new Il2CppInterop.Runtime.InteropTypes
            .Arrays.Il2CppStructArray<Vector3>(4);

        rect.GetWorldCorners(cornerBuffer);

        var anyInFront = false;

        for (var index = 0; index < 4; index++)
        {
            var corner = camera.WorldToScreenPoint(cornerBuffer[index]);

            if (corner.z <= 0f)
                continue;

            anyInFront = true;
            minX = Mathf.Min(minX, corner.x);
            maxX = Mathf.Max(maxX, corner.x);
            minY = Mathf.Min(minY, corner.y);
            maxY = Mathf.Max(maxY, corner.y);
        }

        return anyInFront;
    }

    private bool OnScreen(Transform node)
    {
        try
        {
            var camera = Camera.main;

            if (camera is null || camera == null)
                return true;

            var rect = menuRectCandidates.Value ? node.TryCast<RectTransform>() : null;

            if (rect is not null && rect != null)
            {
                // Projected in ONE place now, shared with the pointer's miss
                // report - see TryProjectRect.
                if (!TryProjectRect(rect, camera,
                        out var minX, out var maxX, out var minY, out var maxY))
                    return false;

                // OVERLAP, not containment: a tile that runs off the edge of the
                // screen is still partly visible and still pointable, and the
                // shop lays out exactly such tiles.
                return maxX >= 0f && minX <= Screen.width
                    && maxY >= 0f && minY <= Screen.height;
            }

            var point = camera.WorldToScreenPoint(node.position);

            if (point.z <= 0f)
                return false;

            return point.x >= 0f && point.x <= Screen.width
                && point.y >= 0f && point.y <= Screen.height;
        }
        catch
        {
            // A filter that throws must not hide a button that is really there.
            return true;
        }
    }

    // Two independent ways of being invisible while staying active, selectable
    // and inside the screen.
    //
    // FIRST, CanvasGroup alpha. The product of every alpha up the chain: a group
    // at zero hides its whole subtree while leaving activeInHierarchy true.
    //
    // SECOND, CLIPPING - and this is the one the news carousel needed.
    //
    // The main menu carries a news panel that cycles five slides. All five sit
    // side by side inside a masked container, so exactly one is visible and the
    // other four are active, interactable, inside the screen bounds and at alpha
    // one. Every existing filter passed them, and navigating left out of the
    // panel took five presses - one per slide, as reported.
    //
    // Unity answers this directly: a RectMask2D sets CanvasRenderer.cull on
    // whatever it clips away. It is a bool on a class reference, so it costs
    // nothing and needs no rect arithmetic - which matters, because
    // RectTransform.rect returns a struct by value and this project does not
    // reach for those shapes without a reason.
    //
    // Checked on the node and on its immediate children, because a FuturButton
    // often carries no CanvasRenderer itself while its image child does.
    private static bool Visible(Transform node)
    {
        try
        {
            var alpha = 1f;

            for (var walk = node; walk is not null && walk != null; walk = walk.parent)
            {
                var group = walk.GetComponent<CanvasGroup>();

                if (group is not null && group != null)
                {
                    alpha *= group.alpha;

                    if (alpha < 0.1f)
                        return false;
                }
            }

            if (Culled(node))
                return false;

            for (var index = 0; index < node.childCount; index++)
            {
                var child = node.GetChild(index);

                if (child is null || child == null)
                    continue;

                // A single drawn child is enough to call the node visible, so
                // only a node whose renderers are ALL culled is excluded.
                if (!Culled(child))
                    return true;
            }

            return true;
        }
        catch
        {
            return true;
        }
    }

    // True only when a CanvasRenderer exists AND reports itself culled. A node
    // without one says nothing either way, and must not be treated as hidden.
    private static bool Culled(Transform node)
    {
        try
        {
            var renderer = node.GetComponent<CanvasRenderer>();

            return renderer is not null && renderer != null && renderer.cull;
        }
        catch
        {
            return false;
        }
    }

    // UIStateMonoBehaviour.IsSelected, NOT the EventSystem.
    //
    // Seeding from EventSystem.currentSelectedGameObject did not fix the double
    // highlight - reported again after 0.88.0: "in every tab an element is
    // highlighted that you did not choose". The game's highlight is a UI STATE on
    // the element itself, and the base class says so directly with a bool. The
    // EventSystem selection was simply the wrong indicator.
    private int IndexOfGameSelection()
    {
        try
        {
            for (var index = 0; index < menuButtons.Count; index++)
            {
                if (!menuButtons[index].IsSelected)
                    continue;

                LoggerInstance.Msg("menu seed: adopting the game's own highlight "
                    + $"[{index + 1}/{menuButtons.Count}] {CaptionOf(menuButtons[index])}");
                return index;
            }

            return -1;
        }
        catch
        {
            return -1;
        }
    }

    private void ScanMenuButtons()
    {
        // THROTTLED IN BOTH CASES. The old condition was
        //
        //     if (menuButtons.Count > 0 && Time.unscaledTime < nextMenuScan)
        //
        // so with nothing found yet the guard was false and the scan ran EVERY
        // FRAME - and this scan goes through Resources.FindObjectsOfTypeAll,
        // which walks every loaded object of the type. The intent was "look
        // again quickly while we have nothing", and that is kept as 10 Hz
        // instead of once per frame.
        if (Time.unscaledTime < nextMenuScan)
            return;

        nextMenuScan = Time.unscaledTime + (menuButtons.Count > 0 ? 0.25f : 0.1f);

        var previous = menuIndex >= 0 && menuIndex < menuButtons.Count
            ? menuButtons[menuIndex]
            : null;

        menuButtons.Clear();
        offscreenNames.Clear();
        lockedNames.Clear();

        var droppedOffscreen = 0;
        var droppedFaded = 0;
        var droppedLocked = 0;

        try
        {
            var found = Resources.FindObjectsOfTypeAll(
                Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.UIStateMonoBehaviour>());

            for (var index = 0; index < found.Length; index++)
            {
                var button = found[index]?.TryCast<Il2CppFuturLab.UIStateMonoBehaviour>();

                if (button is null || button == null)
                    continue;

                if (!button.gameObject.activeInHierarchy)
                    continue;

                // COUNTED, and separately from the inactive ones, because the
                // SettingUIElement branch in Interactable is NEW: those rows used
                // to fall through to a blanket true. If IsInteractable turns out
                // to read false on rows that are actually usable, this number
                // says so on the first run instead of looking like the settings
                // screen got worse for no reason.
                //
                // The inactive ones are not counted: the shop list culls dozens
                // of items by SetActive and that figure would say nothing.
                if (!Interactable(button))
                {
                    droppedLocked++;

                    // DIE ERSTEN DREI MIT NAMEN. Gemeldet wurde, dass die Tabs
                    // zeitweise nur noch ueber die Griff-Tasten zu wechseln
                    // waren. Der Griff-Weg ist DriveMenuTabs und geht an dieser
                    // Liste vorbei, der Zeiger waehlt AUS ihr - ein
                    // TabToggle_* in dieser Namensliste beweist die Ursache in
                    // einer Zeile, und sein Fehlen darin schliesst sie aus.
                    if (lockedNames.Count < 3)
                        lockedNames.Add(button.name);

                    continue;
                }

                // activeInHierarchy and IsInteractable are NOT enough, and the
                // gap was reported as "dead elements that are not shown in the UI
                // at all but still get selected, so you have to push the stick
                // several times to skip past them".
                //
                // Two further tests, both cheap and both about whether a human
                // can see the thing: is it inside the screen, and is it faded
                // out. A cached screen that is still active projects outside the
                // viewport or behind the camera; a panel on its way out sits at
                // CanvasGroup alpha zero while remaining active and interactable.
                // COUNTED, not just skipped. A tile that is permanently absent
                // from the list looks identical to a tile whose rectangle the
                // pointer keeps missing, and those have different causes. The
                // two counters separate them without a new log line per frame -
                // they ride along on the existing scan line, which only prints
                // when the count changes.
                if (!OnScreen(button.transform))
                {
                    droppedOffscreen++;

                    // DIE ERSTEN DREI MIT NAMEN, und der Grund steht in
                    // Abschnitt 107: nach Levelabschluss meldete der Scan "0
                    // interactable button(s), dropped offscreen 9" - die
                    // Knoepfe des Popups waren da und fielen hier heraus. Eine
                    // Zahl sagt nicht, WELCHE, und der vorhandene Bericht
                    // dafuer haengt hinter DevMode.
                    if (offscreenNames.Count < 3)
                        offscreenNames.Add(button.name);

                    continue;
                }

                if (!Visible(button.transform))
                {
                    droppedFaded++;
                    continue;
                }

                menuButtons.Add(button);
            }

            // Insertion sort on the world position. The lists are short - a dozen
            // or so - and this avoids handing a comparison delegate across
            // interop.
            var sortCamera = Camera.main;

            if (sortCamera is not null && sortCamera != null)
            {
                for (var i = 1; i < menuButtons.Count; i++)
                {
                    var candidate = menuButtons[i];
                    var key = ScreenOf(sortCamera, candidate.transform);
                    var j = i - 1;

                    while (j >= 0 && Precedes(key, ScreenOf(sortCamera, menuButtons[j].transform)))
                    {
                        menuButtons[j + 1] = menuButtons[j];
                        j--;
                    }

                    menuButtons[j + 1] = candidate;
                }
            }

            // A selection that still exists keeps its place across a rescan, so a
            // refresh does not throw the cursor back to the top of the screen
            // mid-navigation.
            menuIndex = -1;
            menuSelectedPointer = IntPtr.Zero;

            // COMPARED BY NATIVE POINTER, not by ReferenceEquals - and this was
            // the defect that made navigation useless.
            //
            // Il2CppInterop hands out a FRESH managed wrapper on every access to
            // the same native object, so ReferenceEquals between two reads of one
            // button is always false. The previous selection was therefore never
            // recognised after a rescan, menuIndex fell back to -1 every quarter
            // second, and each step started from scratch: the log shows the
            // selection alternating between [1/5] and [5/5] and never reaching
            // 2, 3 or 4.
            if (previous is not null && previous != null)
            {
                var wanted = previous.Pointer;

                for (var index = 0; index < menuButtons.Count; index++)
                {
                    if (menuButtons[index].Pointer == wanted)
                    {
                        menuIndex = index;
                        break;
                    }
                }
            }

            // LOGGED ON CHANGE, and its absence is why the last run could not be
            // read. DriveMenuNavigation returns early on an empty list, so "no
            // menu select lines" was indistinguishable between "found no
            // buttons", "the stick never left the deadzone" and "moveAction is
            // null". One count separates all three.
            // SEEDED FROM THE GAME'S OWN SELECTION when this mod has none.
            //
            // Reported: after a tab change something is already highlighted, the
            // stick does not move that highlight, it stays lit while the cursor
            // visibly walks past other elements, and it only becomes live once
            // the walk happens to land on it. The cause is two independent
            // cursors - the game highlights its own initial element while this
            // code starts counting from index 0 and knows nothing about it.
            //
            // EventSystem.currentSelectedGameObject is that element. It read
            // "none" during gameplay but carries a real object inside menus,
            // TabToggle_Home and QuitGameImageButton among them. Matching it into
            // the list makes the two cursors one.
            if (menuIndex < 0)
                menuIndex = IndexOfGameSelection();

            if (menuButtons.Count != loggedMenuCount)
            {
                loggedMenuCount = menuButtons.Count;

                var first = menuButtons.Count > 0 ? CaptionOf(menuButtons[0]) : "-";
                var last = menuButtons.Count > 0
                    ? CaptionOf(menuButtons[menuButtons.Count - 1])
                    : "-";

                LoggerInstance.Msg($"menu scan: {menuButtons.Count} interactable button(s)"
                    + $"   first {first}   last {last}"
                    + $"   dropped offscreen {droppedOffscreen}"
                    + $"{(offscreenNames.Count == 0 ? "" : " [" + string.Join(", ", offscreenNames) + "]")}"
                    + $", faded {droppedFaded},"
                    + $" locked {droppedLocked}"
                    + $"{(lockedNames.Count == 0 ? "" : " [" + string.Join(", ", lockedNames) + "]")}"
                    + $"   rectCandidates {menuRectCandidates.Value}"
                    + $"   moveAction {(moveAction is null ? "NULL" : "ok")}"
                    + $"   cursorTarget {CursorTargetText()}"
                    + $"   selectionReasserts {selectionReasserts}"
                    + $" ({ReassertRate():0.#}/s)"
                    // NUR WENN DER PATCH STEHT. Vier Zahlen, die alle 0
                    // lesen, weil der Detour nicht installiert ist, sind keine
                    // Messung - sie sind Rauschen in einer ohnehin langen
                    // Zeile. Steht der Patch, sagen sie alles; steht er nicht,
                    // sagt das die Installationszeile.
                    + $"{(GameInput.SelectionGuardInstalled ? $"   selectCalls {GameInput.SelectCallsOne}/{GameInput.SelectCallsTwo}   selectBlocked {GameInput.SelectBlocked}   selectPassed {GameInput.SelectPassed}" : "")}"
                    + $"{(GameInput.SelectGuardThrew == 0 ? "" : $"   guardThrew {GameInput.SelectGuardThrew}")}");
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  menu scan threw {exception.GetType().Name}: {exception.Message}");
            menuButtons.Clear();
            menuIndex = -1;
            menuSelectedPointer = IntPtr.Zero;
        }
    }

    // SCREEN coordinates, not world coordinates - and this was a real design
    // error, not a polarity slip.
    //
    // The first version sorted by world position.y and position.x. World y is
    // roughly "up" whatever the player faces, so vertical lists happened to work;
    // world x is only "screen right" when the player faces one particular world
    // direction, because the UI canvas is rotated to face the camera every frame.
    // Navigation therefore felt wrong in a way that depended on where the player
    // was standing, which is exactly how the user described it - "not intuitive
    // in any tab" and not reproducible per tab.
    //
    // WorldToScreenPoint returns a Vector3 by value, the same shape as
    // transform.position and used throughout this mod.
    private static Vector2 ScreenOf(Camera camera, Transform node)
    {
        try
        {
            var point = camera.WorldToScreenPoint(node.position);
            return new Vector2(point.x, point.y);
        }
        catch
        {
            return Vector2.zero;
        }
    }

    // Higher on screen first, ties broken left to right. The tolerance is now in
    // PIXELS rather than metres, so it has to be much larger than the old 0.001.
    private static bool Precedes(Vector2 a, Vector2 b) =>
        Mathf.Abs(a.y - b.y) > 4f ? a.y > b.y : a.x < b.x;

    // THE MENU POINTER, and the route is A' - not the one the plan named.
    //
    // WHY NOT m_screenPos, measured on a genuinely LIVE module after
    // alt+Keypad9 woke it: wrote (647, 1083, 0), one frame later
    // (-2.828, 5.444, -0.2), thirty frames later (-2.838, 5.45, -0.211).
    // Process() recomputes it every frame, so writing it is pointless. The run
    // before had claimed the opposite, but it had written into a DISABLED
    // module, where the value simply lay untouched for 73 seconds and several
    // screen changes. Retention is not evidence.
    //
    // WHAT THAT MEASUREMENT HANDED OVER INSTEAD. The live cursor's own Position
    // reads (-2.827, 5.444, -0.198) - IDENTICAL to what m_screenPos carries, in
    // WORLD coordinates, on a canvas whose renderMode is WorldSpace. The field
    // named "screenPos" holds a world point here, derived from the cursor
    // transform. The transform is therefore upstream, and a transform write from
    // LateUpdate is the most-proven operation in this whole mod: the same timing
    // that takes EquipmentAnchor off its two Update writers without a Harmony
    // patch.
    //
    // AND THE RAY COSTS NOTHING. raySpawn is the nozzle - already driven by the
    // controller, already world space, already the origin UpdateLaser draws
    // from. The pointing ray IS the nozzle ray: no pose read, no offsets, no
    // space conversion, and nothing new to get wrong.
    //
    // Gated twice: on cursorForced, so nothing here runs until the native cursor
    // was switched on deliberately, and on menuMode, so it can never reach the
    // game behind a menu.
    //
    // No per-frame status string. Section 87 lists the eight existing ones as the
    // last open performance item; a ninth built every frame would add to exactly
    // that. What this needs to say, it says on a throttle.
    private readonly WashLaser menuLaser = new();
    private static readonly Color PointerColor = new(0.2f, 0.85f, 1f, 1f);
    private Vector3 pointerWrote;
    private bool pointerWroteValid;
    private float nextPointerLog;
    private string pointerVerdict = "";
    private Vector3 pointerLastPick;

    // Der letzte Bildschirmpunkt des Zeigers, fuer die Zeige-Ereignisse. Ein
    // eigenes Feld, weil Select() den Punkt nicht bekommt und ein zweiter
    // Rechenweg dorthin eine zweite Antwort waere.
    private Vector2 pointerCursor;

    // The ONE cursor that can be hit-tested: the one in the hierarchy. The other
    // three instances are split-screen provisioning and sit outside it. Taking
    // the first of four was precisely the defect that made the first probe run
    // meaningless, so it is not repeated here.
    private Il2CppFuturLab.PW2.ControllerCursor? LiveControllerCursor()
    {
        try
        {
            var found = Resources.FindObjectsOfTypeAll(
                Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.PW2.ControllerCursor>());

            for (var index = 0; index < found.Length; index++)
            {
                var cursor = found[index]?.TryCast<Il2CppFuturLab.PW2.ControllerCursor>();

                if (cursor is null || cursor == null)
                    continue;

                if (cursor.gameObject.activeInHierarchy)
                    return cursor;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    // The hit point, mapped into the SCREEN coordinates the menu scan already
    // sorts by. WorldToScreenPoint is the same call ScreenOf uses, so the
    // pointer and the candidate list share one space by construction rather
    // than by conversion.
    //
    // A RECTANGLE TEST, and this comment used to say the opposite.
    //
    // It described the nearest-centre-inside-a-radius version and named
    // GetWorldCorners as a possible next step. That step was taken in section
    // 92: the test is RectangleContainsScreenPoint, and nearest centre only
    // breaks ties among the rectangles that CONTAIN the point. Left standing,
    // the old wording describes code that is no longer here.
    private void PointerSelect(Vector3 hit)
    {
        if (menuButtons.Count == 0)
            return;

        var camera = Camera.main;

        if (camera is null || camera == null)
            return;

        // NOTHING IS SELECTED WHILE THE LIST IS MOVING, and this is a safety
        // property rather than a nicety.
        //
        // Scrolling moves the CONTENT under a cursor that is standing still, so
        // menuIndex goes on pointing at a row that has already slid away - and a
        // standing highlight plus a trigger pull activates whatever was last
        // lit. That is the failure section 90 traced to "Career Mode submitted
        // seven seconds after opening the Shop tab", and scrolling would have
        // reintroduced it by a different route.
        //
        // So the highlight is dropped for as long as the list is moving plus a
        // short tail. When the stick centres, the fix at the top of this method
        // re-resolves on the very next frame with the hand held still, which is
        // exactly what that fix was for.
        if (Time.unscaledTime < scrollHoldUntil)
        {
            DropHighlight("list is scrolling");
            return;
        }

        // Only take the selection over once the hand has actually MOVED, two
        // centimetres at the panel. Without this the pointer would re-assert
        // every LateUpdate and instantly undo a stick selection - the two
        // cursors fighting each other from section 81, in a new costume.
        //
        // AND ONLY WHILE A SELECTION ACTUALLY STANDS, which is the second half
        // and was missing. The guard is there to protect an EXISTING selection
        // from being overwritten; with nothing selected there is nothing to
        // protect, and returning early just leaves the pointer mute.
        //
        // menuIndex falls to -1 at four places, and at three of them the hand
        // is by definition still: Activate (after every single click), StepTab
        // (after every tab change) and the menuMode transition in
        // MenuModeActive (after every screen change). So the reported symptom
        // was reproducible in one move - open the Shop tab, hold the hand
        // steady, point at a tile: no highlight, and a dead trigger, until the
        // hand happened to travel two centimetres. "Sometimes I cannot click a
        // tile, and it is not highlighted either although the pointer is on
        // it", exactly.
        //
        // ONE SNAPSHOT of the node, and both null shapes: a destroyed entry is
        // not `is null`, so the Unity comparison has to run too.
        var held = menuIndex >= 0 && menuIndex < menuButtons.Count
            ? menuButtons[menuIndex]
            : null;
        var settled = held is not null && held != null;

        if (settled && (hit - pointerLastPick).sqrMagnitude < 0.0004f)
        {
            // DIE RUHENDE HAND, UND HIER LAG DER FEHLER.
            //
            // Dieses Tor schuetzt eine bestehende Auswahl davor, jeden Frame
            // ueberschrieben zu werden - richtig so, Abschnitt 92. Nur kehrt
            // es damit genau in dem Fall um, in dem das Spiel die Auswahl von
            // selbst fallen laesst: Zeiger liegt still auf der Kachel, das
            // Spiel nimmt IsSelected weg, und niemand setzt es zurueck. Genau
            // das war die Meldung "leuchtet einmal auf und wird wieder dunkel,
            // auch wenn der Strahl noch auf der Kachel ist".
            //
            // Mein erster Versuch stand HINTER diesem return und lief deshalb
            // nur, waehrend die Hand wanderte - dort erzeugte er das Blinken
            // und half beim Ruhen nicht. Eine Zeile zu spaet, drei Laeufe
            // gekostet.
            HoldHighlight(menuIndex);

            // NACHGESETZT, SOLANGE DIE HAND RUHT - und das ist der Kern dieser
            // Fassung.
            //
            // Gemessen: das Spiel holt die Auswahl ~70 ms nach jedem Wechsel
            // auf das Standardelement der Seite zurueck. Ein Schreiben NUR bei
            // Zielwechsel verliert dieses Rennen jedes Mal, und die Vorschau
            // zeigt danach wieder das erste Item.
            //
            // MoveEventSelection vergleicht selbst am Pointer, ob das
            // EventSystem unser Ziel schon haelt. Der Aufruf kostet damit einen
            // Zeigervergleich und schreibt nur, wenn das Spiel die Auswahl
            // gestohlen hat.
            MoveEventSelection(held!);
            return;
        }

        pointerLastPick = hit;

        var point = camera.WorldToScreenPoint(hit);
        var cursor = new Vector2(point.x, point.y);

        var best = -1;
        var bestCost = float.MaxValue;

        for (var index = 0; index < menuButtons.Count; index++)
        {
            var node = menuButtons[index];

            if (node is null || node == null)
                continue;

            var rect = node.transform.TryCast<RectTransform>();

            if (rect is null || rect == null)
                continue;

            // THE REAL RECTANGLE, not the distance to a pivot. Reported from
            // the headset in a shape that named the cause: "the small tabs I
            // can hit very well, the big tiles are offset, and some work
            // better than others". Nearest-CENTRE used
            // node.transform.position, which is the node's PIVOT - on a small
            // tab that is nearly its centre, on a large tile it can sit at an
            // edge, and where exactly depends on that tile's anchoring. Hence
            // an offset that differs per tile.
            //
            // RectangleContainsScreenPoint is what a mouse does. Static, returns
            // bool, takes a class reference plus a Vector2 and a Camera - the
            // same by-value parameter shape as camera.WorldToScreenPoint(Vector3),
            // which this mod relies on everywhere. Not the struct-parameter
            // generic family, and not RectTransform.rect, which returns a struct
            // by value and this project leaves alone.
            if (!RectTransformUtility.RectangleContainsScreenPoint(rect, cursor, camera))
                continue;

            // SEVERAL can contain the point at once - a tile inside a panel
            // inside a tab page. Nearest centre among the CONTAINING ones is
            // the smallest sensible tiebreak and needs no size.
            var cost = (ScreenOf(camera, node.transform) - cursor).sqrMagnitude;

            if (cost < bestCost)
            {
                bestCost = cost;
                best = index;
            }
        }

        // NO HIT MEANS NO SELECTION, and the 260-pixel fallback is gone on
        // purpose. It is what let the ray reach a node that was not under the
        // cursor at all: "Career Mode" was submitted seven seconds after the
        // Shop tab had been opened, and the settings screen keeps leftovers
        // from other tabs in the candidate list ("first Career Mode last 1").
        // Pointing at nothing must select nothing.
        pointerCursor = cursor;

        if (best < 0)
        {
            // AND IT HAS TO LOOK LIKE NOTHING TOO. Reported as "the last tile
            // stays highlighted, the highlight only changes once a new one is
            // hit" - which is not cosmetic. A stale highlight plus a trigger
            // pull activates whatever was last lit, and that is exactly how
            // "Career Mode" got submitted seven seconds after the Shop tab had
            // been opened.
            //
            // DriveMenuNavigation submits menuIndex, so clearing it to -1 also
            // makes the trigger a no-op while the ray points at nothing. That
            // is the actual safety property; the dark tile is just how it looks.
            ReportPointerMiss(camera, cursor);
            DropHighlight("off target");
            return;
        }

        if (best != menuIndex)
            Select(best);
    }

    // DER ZEIGER STEHT AUF DERSELBEN KACHEL - und genau hier geschah bisher
    // nichts.
    //
    // Select() wird nur bei Wechsel gerufen und loggt dabei; ein Aufruf alle
    // 150 ms wuerde das Log fluten und die Auswahl jedes Mal neu ausrufen.
    // Darum ForceSelect direkt, ohne Umweg ueber Select, und nur wenn das Spiel
    // die Auswahl wirklich verloren hat.
    // NACHSETZUNGEN PRO SEKUNDE SEIT DER LETZTEN SCANZEILE.
    //
    // Die Zahl ist der eigentliche Messwert dieses Laufs: 20 bis 30 war der
    // Kampf, und nahe 0 heisst, dass er aufgehoert hat. Ausgerechnet wird sie
    // hier und nicht beim Lesen des Logs - die Paarung zweier Logzeilen zu
    // einer Verhaeltniszahl hat in diesem Projekt schon Diagnosen gekostet.
    private float ReassertRate()
    {
        var now = Time.unscaledTime;
        var elapsed = Mathf.Max(0.001f, now - lastReassertTime);
        var rate = (selectionReasserts - lastReassertMark) / elapsed;

        lastReassertMark = selectionReasserts;
        lastReassertTime = now;

        return rate;
    }

    private void HoldHighlight(int index)
    {
        if (!menuHighlightHold.Value || Time.unscaledTime < nextHighlightHold)
            return;

        if (index < 0 || index >= menuButtons.Count)
            return;

        nextHighlightHold = Time.unscaledTime
            + Mathf.Max(0.05f, menuHighlightHoldSeconds.Value);

        var node = menuButtons[index];

        if (node is null || node == null)
            return;

        bool selected;

        try
        {
            selected = node.IsSelected;
        }
        catch (Exception exception)
        {
            // Ein nicht lesbares IsSelected nimmt diesem Lauf die Aussage, aber
            // nicht das Menue. Einmal sagen und Ruhe.
            if (!loggedHoldHeld)
            {
                loggedHoldHeld = true;
                LoggerInstance.Warning($"  menu hold: IsSelected threw "
                    + $"{exception.GetType().Name} - no verdict from this run");
            }

            return;
        }

        if (selected)
        {
            // DIE ZWEITE HAELFTE DER GABELUNG, und sie ist die wichtigere:
            // haelt der Zustand, waehrend die Kachel dunkel ist, dann ist
            // Selected nicht die Groesse, an der das BILD haengt. Die Klasse
            // fuehrt dafuer eigene Ereignisse - Highlighted und UnHighlighted -
            // und der naechste Hebel waere ein Pointer-Enter statt eines
            // Select. Ohne diese Zeile wuerde ein erfolgloses Erneuern wie ein
            // erfolgloses Feature aussehen.
            if (!loggedHoldHeld)
            {
                loggedHoldHeld = true;
                LoggerInstance.Msg("menu hold: the game still reports IsSelected TRUE while "
                    + $"the pointer rests on {CaptionOf(node)}. If the tile still looks dark, "
                    + "the state holds and the DRAWING follows something else - Highlighted, "
                    + "not Selected.");
            }

            return;
        }

        node.ForceSelect();
        highlightReasserts++;

        if (!loggedHoldRevert)
        {
            loggedHoldRevert = true;
            LoggerInstance.Msg("menu hold: IsSelected went FALSE while the pointer rested on "
                + $"{CaptionOf(node)} - the game's own machine drops it. Re-asserting every "
                + $"{Mathf.Max(0.05f, menuHighlightHoldSeconds.Value):0.##} s from now on.");
        }
    }

    // WHY THE POINTER MISSED - the three nearest candidates with the exact
    // numbers RectangleContainsScreenPoint decides against.
    //
    // Deliberately measured through TryProjectRect, the same projection OnScreen
    // uses, so the report cannot disagree with the decision it explains. A
    // report computed a second way would be a new source of error rather than a
    // diagnosis.
    //
    // Ranked by the distance from the cursor to the rectangle's CENTRE, because
    // "which element did I mean" is a question about where things are, not about
    // where the list happens to have sorted them.
    //
    // Reads the three cases apart:
    //   rectangles far above or below the cursor  the entries are parked or
    //                                            reparented, not where drawn
    //   rectangles right but contains false       camera or projection
    //   another candidate covering the cursor     ordering or a panel on top
    private float nextMissReport;

    private void ReportPointerMiss(Camera camera, Vector2 cursor)
    {
        if (!Dev(menuMissReport) || Time.unscaledTime < nextMissReport)
            return;

        nextMissReport = Time.unscaledTime + 2f;

        try
        {
            var bestOne = -1;
            var bestTwo = -1;
            var bestThree = -1;
            var costOne = float.MaxValue;
            var costTwo = float.MaxValue;
            var costThree = float.MaxValue;

            // Three passes of insertion rather than a sort: the list is a couple
            // of dozen long and this hands no comparison delegate across interop
            // - the same reasoning as the insertion sort in ScanMenuButtons.
            for (var index = 0; index < menuButtons.Count; index++)
            {
                var node = menuButtons[index];
                var rect = node is null || node == null
                    ? null
                    : node.transform.TryCast<RectTransform>();

                if (rect is null || rect == null)
                    continue;

                if (!TryProjectRect(rect, camera,
                        out var minX, out var maxX, out var minY, out var maxY))
                    continue;

                var centre = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
                var cost = (centre - cursor).sqrMagnitude;

                if (cost < costOne)
                {
                    costThree = costTwo; bestThree = bestTwo;
                    costTwo = costOne; bestTwo = bestOne;
                    costOne = cost; bestOne = index;
                }
                else if (cost < costTwo)
                {
                    costThree = costTwo; bestThree = bestTwo;
                    costTwo = cost; bestTwo = index;
                }
                else if (cost < costThree)
                {
                    costThree = cost; bestThree = index;
                }
            }

            LoggerInstance.Msg($"menu pointer miss: cursor ({cursor.x:0}, {cursor.y:0})"
                + $"   screen {Screen.width}x{Screen.height}"
                + $"   candidates {menuButtons.Count}");

            ReportMissCandidate(camera, cursor, bestOne);
            ReportMissCandidate(camera, cursor, bestTwo);
            ReportMissCandidate(camera, cursor, bestThree);
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  pointer miss report threw "
                + $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private void ReportMissCandidate(Camera camera, Vector2 cursor, int index)
    {
        if (index < 0 || index >= menuButtons.Count)
            return;

        var node = menuButtons[index];

        if (node is null || node == null)
            return;

        var rect = node.transform.TryCast<RectTransform>();

        if (rect is null || rect == null)
            return;

        if (!TryProjectRect(rect, camera,
                out var minX, out var maxX, out var minY, out var maxY))
            return;

        var contains = RectTransformUtility.RectangleContainsScreenPoint(rect, cursor, camera);

        LoggerInstance.Msg($"    cand {CaptionOf(node)}   "
            + $"x {minX:0}..{maxX:0}   y {minY:0}..{maxY:0}   "
            + $"contains {contains}   name {node.name}");
    }

    // Shared by the two places that must make "nothing is under the pointer"
    // look like nothing: pointing past every element, and a list in motion.
    //
    // menuIndex going to -1 is the actual safety property - DriveMenuNavigation
    // submits whatever menuIndex points at, so a trigger pull is a no-op while
    // it is -1. The dark tile is just how that looks.
    //
    // Idempotent, and silent once it has fired: it is called every frame while a
    // list scrolls, and a log line per frame is the section 87 item.
    private float scrollHoldUntil;

    private void DropHighlight(string reason)
    {
        if (menuIndex < 0)
            return;

        if (menuIndex < menuButtons.Count)
        {
            var stale = menuButtons[menuIndex];

            if (stale is not null && stale != null)
            {
                stale.ForceDeselect();
                SendHover(stale, false);

                // BEIDE Felder auf dasselbe Nichts - ein gemerkter Pointer ohne
                // Auswahl waere die Falle, die dieses Projekt schon kennt.
                menuSelectedPointer = IntPtr.Zero;

                // Null heisst "auf nichts mehr": die Kette bekommt ihr Exit,
                // sonst bleibt die letzte Kachel im Hover stehen - genau der
                // stehende Rest, den Abschnitt 92 fuer die Auswahl beseitigt
                // hat.
                MoveGameHover(null);
                LoggerInstance.Msg($"menu pointer: {reason}, dropped "
                    + CaptionOf(stale));
            }
        }

        menuIndex = -1;
        menuSelectedPointer = IntPtr.Zero;
    }

    private void DriveMenuPointer()
    {
        // AUTOMATIC, so nobody has to find alt+Keypad9.
        //
        // Forced ONCE per session, on the first menu, and NOT restored on the way
        // out. That is exactly the state the field run confirmed: switched on by
        // hand and left standing. Flipping the game's input device back on every
        // menu transition would provoke OnInputDeviceChanged over and over, and
        // nothing about that has been measured - so it is not done on a hunch.
        //
        // menuMode already carries the UiNavigation gate (see OnLateUpdate), so
        // turning that off disables this with it. One switch for menu input.
        if (menuMode && menuPointer.Value && !cursorForced)
            ToggleNativeCursor();

        if (!cursorForced || !menuMode)
        {
            // Hide() is a bool write on a LineRenderer and safe to repeat, but
            // the flag keeps it from being called every frame for the whole
            // session while no menu is open.
            if (pointerWroteValid)
            {
                pointerWroteValid = false;
                menuLaser.Hide();
            }

            return;
        }

        var cursor = LiveControllerCursor();

        if (cursor is null || cursor == null)
        {
            menuLaser.Hide();
            pointerWroteValid = false;
            return;
        }

        var rect = cursor.m_transform;
        var canvas = cursor.m_cursorCanvas;

        if (rect is null || rect == null || canvas is null || canvas == null)
        {
            menuLaser.Hide();
            pointerWroteValid = false;
            return;
        }

        // READ BACK FIRST, before this frame overwrites it. That makes the drive
        // loop its own instrument: if what was written last frame is gone, the
        // cursor's own Update owns this transform and LateUpdate did not win -
        // the single question this route stands or falls on, answered without
        // spending a separate probe run on it.
        if (pointerWroteValid)
        {
            var found = rect.position;

            // TWO CENTIMETRES, and the first threshold was LYING. At one it
            // flapped between "holds" and "OVERWRITTEN" on drifts of five to
            // ten millimetres - 82 verdicts against 158 in one run - because
            // the cursor canvas moves with the head between this write and the
            // next frame's read. That is not the game fighting for the
            // transform.
            //
            // The drift is now REPORTED instead of being reduced to a bool, so
            // the next reader judges the number rather than trusting the word.
            var drift = (found - pointerWrote).magnitude;
            var held = drift < 0.02f;
            var verdict = held ? "holds" : "OVERWRITTEN";

            // On CHANGE of the verdict, and otherwise at most every two seconds.
            // A per-frame line would bury the rest of the log, and a verdict that
            // flips is the interesting event.
            if (!string.Equals(verdict, pointerVerdict, StringComparison.Ordinal)
                || Time.unscaledTime >= nextPointerLog)
            {
                pointerVerdict = verdict;
                nextPointerLog = Time.unscaledTime + 2f;

                LoggerInstance.Msg($"menu pointer: write {verdict}"
                    + $"   drift {drift:0.###} m"
                    + $"   wrote {Vector(pointerWrote)}   found {Vector(found)}"
                    + $"   hovering {cursor.Hovering}"
                    + $"   cursorTarget {CursorTargetText()}");
            }
        }

        if (raySpawn is null || raySpawn == null)
        {
            menuLaser.Hide();
            pointerWroteValid = false;
            return;
        }

        // MuzzlePoint, NOT raySpawn.position - and the difference is the
        // offset reported from the headset: the beam started up and to the
        // side of the barrel instead of at it.
        //
        // PositionToFOV rewrites NozzleAnchor(Clone).localPosition every
        // LateUpdate to hold the game's own VFX at a fixed SCREEN position,
        // which displaces the spawn point by 8.1 cm flat and 11.6 cm at the
        // field of view XR raises it to. MuzzlePoint removes the x and y of
        // that fudge as a delta and keeps z, because z is the forward offset
        // the nozzle itself authored and it differs per nozzle.
        //
        // The wash laser has drawn from this corrected point since section 77.
        // Taking raySpawn.position here re-introduced a solved bug, which is
        // what the user recognised on sight.
        var origin = MuzzlePoint(raySpawn);
        var forward = raySpawn.forward;

        // Against the CURSOR's own canvas plane, not the UI root's. The cursor
        // lives in this canvas and the module derives its position from it, so
        // this is the surface the cursor may legitimately be placed on. Whether
        // that plane coincides with the UI the player sees is a question for the
        // log, not an assumption to build on.
        var planeNormal = canvas.transform.forward;
        var denominator = Vector3.Dot(forward, planeNormal);

        if (Mathf.Abs(denominator) < 1e-5f)
        {
            menuLaser.Hide();
            pointerWroteValid = false;
            return;
        }

        var distance =
            Vector3.Dot(canvas.transform.position - origin, planeNormal) / denominator;

        // Behind the hand, or close enough to sit inside the nozzle: neither is a
        // place to put a cursor, and a negative distance would draw the beam
        // backwards.
        if (distance <= 0.05f)
        {
            menuLaser.Hide();
            pointerWroteValid = false;
            return;
        }

        var hit = origin + (forward * distance);

        try
        {
            rect.position = hit;
            pointerWrote = hit;
            pointerWroteValid = true;
        }
        catch (Exception exception)
        {
            pointerWroteValid = false;
            menuLaser.Hide();
            LoggerInstance.Warning($"  menu pointer write threw "
                + $"{exception.GetType().Name}: {exception.Message}");
            return;
        }

        // Drawn to the hit point rather than a fixed length, so the beam ENDS on
        // the surface being pointed at. A line that overshoots the panel reads as
        // if it missed.
        menuLaser.Draw(LoggerInstance, origin, forward, "menu", distance,
            laserWidth.Value, PointerColor, laserAlwaysOnTop.Value);

        // SELECTION, and it deliberately does NOT come from the game's module.
        //
        // Measured across 240 log lines from one run: hovering False and
        // cursorTarget none in every single one, while the cursor sat exactly
        // where it had been put. The module DRAWS from the transform but does
        // not hit-test from it - its Process() resolves a move out of
        // CursorInput, and no stick is feeding that.
        //
        // So the native cursor is the VISUAL, and the selection runs through
        // the pipeline that is already proven: the same menuButtons scan, the
        // same five visibility filters, the same Select() and ForceSelect().
        //
        // Which also means the trigger needs no new code. DriveMenuNavigation
        // already submits whatever menuIndex points at, on A and on the right
        // trigger both.
        PointerSelect(hit);
    }

    // THE RIGHT STICK, which a menu leaves completely free.
    //
    // ReadTurn returns immediately while menuMode is on - "a menu owns the
    // sticks", and both axes go with it: x is the smooth turn, y cycles the
    // nozzle, and neither happens in a menu. So there is a whole two-axis input
    // sitting unused on the hand that is already doing the pointing, and it
    // costs no button and no rebinding to use it.
    //
    // That is what made scrolling and value editing possible without touching
    // the left stick, which keeps menu navigation. No input conflict to resolve.
    //
    // ONE READ, then the dominant axis decides - the same rule ReadTurn and
    // DriveMenuNavigation already use, so a diagonal push scrolls or adjusts,
    // never both.
    private readonly List<UnityEngine.UI.ScrollRect> scrollRects = new();
    private float nextScrollScan;
    private int loggedScrollCount = -1;

    // Der Kandidatenbericht laeuft nur bei WECHSEL des Ziels - siehe
    // ScrollUnderPointer. Der Pointer, weil Il2CppInterop bei jedem Zugriff
    // einen frischen Wrapper herausgibt.
    private IntPtr loggedScrollTarget = new(-1);
    private readonly List<string> scrollCandidates = new();
    private UnityEngine.UI.ScrollRect? scrollTarget;
    private float scrollStart;
    private float scrollAim;
    private float nextScrollLog;
    private bool menuAdjustArmed = true;
    private bool adjustActive;
    private IntPtr adjustPointer;
    private float adjustStart;
    private float adjustAim;
    private float nextAdjustLog;
    private string adjustCaption = "";

    private void DriveMenuRightStick()
    {
        if (!menuMode)
        {
            EndScroll();
            EndAdjust();
            menuAdjustArmed = true;
            return;
        }

        var stick = MenuRightStick();
        var deadzone = turnDeadzone.Value;

        // Re-armed only when the WHOLE stick is centred - section 75 point 5,
        // where re-arming on one axis let a sustained deflection fire over and
        // over. The dropdown stepper depends on this.
        if (Mathf.Abs(stick.x) < deadzone && Mathf.Abs(stick.y) < deadzone)
        {
            EndScroll();
            EndAdjust();
            menuAdjustArmed = true;
            return;
        }

        if (Mathf.Abs(stick.y) >= Mathf.Abs(stick.x))
        {
            EndAdjust();
            DriveMenuScroll(stick.y);
            return;
        }

        EndScroll();
        DriveMenuAdjust(stick.x);
    }

    private Vector2 MenuRightStick()
    {
        if (turnAction is null)
            return Vector2.zero;

        try
        {
            var raw = turnAction.ReadValueAsObject();
            return raw is null ? Vector2.zero : raw.Unbox<Vector2>();
        }
        catch
        {
            return Vector2.zero;
        }
    }

    // SCROLLING, and the shop item list is the reason.
    //
    // Reported: behind each of the five shop tiles is a list of items, and the
    // ones further down cannot be reached. They are not missing from the scan by
    // mistake - ScrollableCulling.CullByActivation switches them off, so
    // activeInHierarchy is false and the first filter drops them correctly. The
    // items therefore need no code of their own: scroll the list, the game
    // activates them, and the next ScanMenuButtons picks them up. Which is why
    // this part is a float write and nothing more.
    //
    // AN AIM OF ITS OWN, not read-modify-write. SmoothScrollRect overrides
    // SetNormalizedPosition and SetContentAnchoredPosition, so it runs its own
    // smoothing; reading the position back each frame and adding to it would
    // then crawl or stall, because the value read is still on its way to the one
    // written. The aim is seeded once per gesture and advanced by the stick.
    private void DriveMenuScroll(float y)
    {
        if (!menuScroll.Value || !pointerWroteValid)
        {
            EndScroll();
            return;
        }

        var camera = Camera.main;

        if (camera is null || camera == null)
            return;

        ScanScrollRects();

        var target = ScrollUnderPointer(camera);

        if (target is null || target == null)
        {
            // Pointing at no list is not an error - the stick simply does
            // nothing, with no side effect anywhere else.
            EndScroll();
            return;
        }

        // COMPARED BY NATIVE POINTER. Il2CppInterop hands out a fresh managed
        // wrapper on every access to the same native object, so ReferenceEquals
        // between two reads of one ScrollRect is always false - the defect that
        // made menu navigation useless in section 90.
        var standing = scrollTarget;

        if (standing is null || standing == null || standing.Pointer != target.Pointer)
        {
            EndScroll();
            scrollTarget = target;
            scrollStart = target.verticalNormalizedPosition;
            scrollAim = scrollStart;
            nextScrollLog = 0f;
        }

        // Screen up is towards 1: verticalNormalizedPosition is 0 at the bottom
        // of the content and 1 at the top.
        scrollAim = Mathf.Clamp01(
            scrollAim + (y * menuScrollSpeed.Value * Time.unscaledDeltaTime));

        var before = target.verticalNormalizedPosition;
        target.verticalNormalizedPosition = scrollAim;

        // The tail that keeps the highlight off while the content is still
        // settling. SmoothScrollRect keeps moving for a moment after the last
        // write, so releasing the stick and re-resolving in the same frame would
        // pick the row that is on its way out.
        scrollHoldUntil = Time.unscaledTime + 0.15f;

        if (Time.unscaledTime >= nextScrollLog)
        {
            nextScrollLog = Time.unscaledTime + 0.5f;

            // REPORTED AS NUMBERS, not reduced to a bool. Section 90's pointer
            // verdict flapped between "holds" and "OVERWRITTEN" on a threshold
            // that was lying, and the lesson recorded there was to let the next
            // reader judge the figures. With smoothing in the way a read-back
            // that differs is EXPECTED; the failure is "did not move at all
            // over the whole gesture", and EndScroll is where that shows.
            LoggerInstance.Msg($"menu scroll: {ScrollPath(target)}"
                + $"   aim {scrollAim:0.###}   was {before:0.###}"
                + $"   now {target.verticalNormalizedPosition:0.###}");
        }
    }

    // Logs the whole gesture, which is the measurement that actually answers
    // "does scrolling work": start against final, on one line, from one object.
    private void EndScroll()
    {
        var target = scrollTarget;

        if (target is not null && target != null)
        {
            var final = target.verticalNormalizedPosition;

            LoggerInstance.Msg($"menu scroll: {ScrollPath(target)} done"
                + $"   start {scrollStart:0.###}   aim {scrollAim:0.###}"
                + $"   final {final:0.###}   moved {Mathf.Abs(final - scrollStart):0.###}");
        }

        // EVERY field that refers to the object, cleared in the same place. A
        // half-cleared teardown is how a destroyed object survives a null check.
        scrollTarget = null;
        scrollStart = 0f;
        scrollAim = 0f;
        nextScrollLog = 0f;
    }

    private void ScanScrollRects()
    {
        if (Time.unscaledTime < nextScrollScan)
            return;

        nextScrollScan = Time.unscaledTime + 0.25f;
        scrollRects.Clear();

        try
        {
            // ScrollRect catches SmoothScrollRect with it - FindObjectsOfTypeAll
            // returns derived types, and SmoothScrollRect is the game's subclass.
            var found = Resources.FindObjectsOfTypeAll(
                Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.UI.ScrollRect>());

            for (var index = 0; index < found.Length; index++)
            {
                var scroller = found[index]?.TryCast<UnityEngine.UI.ScrollRect>();

                if (scroller is null || scroller == null)
                    continue;

                if (!scroller.gameObject.activeInHierarchy || !scroller.vertical)
                    continue;

                scrollRects.Add(scroller);
            }

            if (scrollRects.Count != loggedScrollCount)
            {
                loggedScrollCount = scrollRects.Count;
                LoggerInstance.Msg($"menu scroll scan: {scrollRects.Count} vertical list(s)");
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  scroll scan threw {exception.GetType().Name}: "
                + exception.Message);
            scrollRects.Clear();
        }
    }

    // WHICH LIST THE STICK SCROLLS - and the rule "innermost under the cursor"
    // was not enough.
    //
    // Reported twice: the item lists under Anpassung do not scroll. The log
    // looked like they did, "menu scroll: Content done ... moved 0.184", and that
    // reading was wrong. THREE vertical lists live on that screen and all of
    // them are called Content, so a moved scroller named Content was never
    // evidence about WHICH one moved. It moved the wrong one.
    //
    // The consequence reached further than the scrolling: the entries are culled
    // by ScrollableUICullController until they come into view, so a list that
    // never scrolls never activates its entries, and CustomisationItemButton
    // appeared ZERO times in the candidate scan of that run - against 19 times
    // in a run where something did scroll. "Cannot scroll" and "cannot select"
    // were one defect, not two.
    //
    // THE NEW RULE IS A DEFINITION, not a guess: a ScrollRect whose content fits
    // inside its viewport CANNOT scroll, so it must not be chosen. Content and
    // viewport are both RectTransforms, and TryProjectRect already yields their
    // height in pixels - the same projection the pointer and OnScreen use.
    //
    // Among the ones that CAN scroll, innermost still wins, so a list inside a
    // page still beats the page.
    private UnityEngine.UI.ScrollRect? ScrollUnderPointer(Camera camera)
    {
        var point = camera.WorldToScreenPoint(pointerWrote);
        var cursor = new Vector2(point.x, point.y);

        UnityEngine.UI.ScrollRect? best = null;
        var bestDepth = -1;

        scrollCandidates.Clear();

        for (var index = 0; index < scrollRects.Count; index++)
        {
            var scroller = scrollRects[index];

            if (scroller is null || scroller == null)
                continue;

            var rect = scroller.viewport;

            if (rect is null || rect == null)
                rect = scroller.transform.TryCast<RectTransform>();

            if (rect is null || rect == null)
                continue;

            if (!RectTransformUtility.RectangleContainsScreenPoint(rect, cursor, camera))
                continue;

            // DER FILTER, DER HIER GEFEHLT HAT - Abschnitt 109, und er ist
            // derselbe, den der Knopf-Scan seit Langem benutzt.
            //
            // Gemessen: unter Einstellungen liegen VIER Listen uebereinander,
            // alle mit dem Pfad ScrollView_Settings/Viewport/Content und alle
            // mit Tiefe 14 - die vier Unter-Tabs. Weder Name noch Tiefe
            // unterscheidet sie, also gewann die erste, die
            // FindObjectsOfTypeAll lieferte, und das war nicht die sichtbare.
            // Der Kommentar unter dieser Methode beschreibt denselben Fall
            // schon zweimal; was fehlte, war das Unterscheidungsmerkmal.
            //
            // activeInHierarchy trennt sie nicht: die Tabs bleiben aktiv und
            // werden ueber CanvasGroup.alpha ausgeblendet. Genau das prueft
            // Visible - Alpha-Kette bis zur Wurzel plus Culling. "Der Name ist
            // kein Bezeichner" aus Abschnitt 97, hier fuer Pfad UND Tiefe.
            if (!Visible(scroller.transform))
            {
                scrollCandidates.Add($"{ScrollPath(scroller)} SKIPPED-faded");
                continue;
            }

            if (!CanScroll(scroller, rect, camera, out var contentHeight, out var viewHeight))
            {
                // GESAMMELT, NICHT GELOGGT: die Zeile unten schreibt einmal pro
                // Zielwechsel, und ausgerechnet die ausgeschiedenen Kandidaten
                // sind die Auskunft, die fehlt - "nothing to scroll" an einer
                // Liste, die der Spieler scrollen will, ist der Befund.
                scrollCandidates.Add($"{ScrollPath(scroller)} content {contentHeight:0}"
                    + $" view {viewHeight:0} SKIPPED-nothing-to-scroll");

                continue;
            }

            var depth = Depth(rect.transform);

            scrollCandidates.Add($"{ScrollPath(scroller)} content {contentHeight:0}"
                + $" view {viewHeight:0} depth {depth}");

            if (depth > bestDepth)
            {
                bestDepth = depth;
                best = scroller;
            }
        }

        // EINE ZEILE PRO ZIELWECHSEL, und sie entscheidet den naechsten Schritt:
        // steht die sichtbare Liste als SKIPPED-nothing-to-scroll darin, liegt
        // es an CanScroll und damit an der Projektion; steht sie mit kleinerer
        // Tiefe darin, liegt es an der Tiefenregel; steht sie gar nicht darin,
        // enthaelt ihr Sichtfenster den Zeiger nicht.
        var chosen = best is null || best == null ? IntPtr.Zero : best.Pointer;

        if (chosen != loggedScrollTarget)
        {
            loggedScrollTarget = chosen;

            LoggerInstance.Msg($"menu scroll target: "
                + $"{(best is null || best == null ? "none" : ScrollPath(best))}"
                + $"   of {scrollRects.Count} list(s), {scrollCandidates.Count} under the pointer"
                + (scrollCandidates.Count == 0
                    ? ""
                    : "   [" + string.Join("; ", scrollCandidates) + "]"));
        }

        return best;
    }

    // Content taller than the viewport, in screen pixels. Measured through the
    // same projection as everything else so the answer cannot disagree with what
    // the pointer sees. A missing content or a degenerate projection counts as
    // "cannot scroll" rather than as an error - the caller simply looks on.
    private bool CanScroll(UnityEngine.UI.ScrollRect scroller, RectTransform view,
        Camera camera, out float contentHeight, out float viewHeight)
    {
        contentHeight = 0f;
        viewHeight = 0f;

        var content = scroller.content;

        if (content is null || content == null)
            return false;

        if (!TryProjectRect(content, camera, out _, out _, out var contentMinY, out var contentMaxY))
            return false;

        if (!TryProjectRect(view, camera, out _, out _, out var viewMinY, out var viewMaxY))
            return false;

        contentHeight = contentMaxY - contentMinY;
        viewHeight = viewMaxY - viewMinY;

        // Twenty pixels of slack, so a list that happens to fill its viewport
        // exactly is not treated as scrollable and then written to pointlessly.
        return contentHeight > viewHeight + 20f;
    }

    // Name plus two parents. The leaf name alone is what made the log unreadable
    // here: three different lists, all called Content.
    private static string ScrollPath(UnityEngine.UI.ScrollRect scroller)
    {
        try
        {
            var node = scroller.transform;
            var name = node.name;
            var parent = node.parent;

            if (parent is null || parent == null)
                return name;

            var grand = parent.parent;

            return grand is null || grand == null
                ? parent.name + "/" + name
                : grand.name + "/" + parent.name + "/" + name;
        }
        catch
        {
            return "?";
        }
    }
    private static int Depth(Transform node)
    {
        var depth = 0;

        for (var walk = node; walk is not null && walk != null; walk = walk.parent)
            depth++;

        return depth;
    }

    // VALUE EDITING on the right stick's x axis, for the two types where a
    // submit means nothing.
    //
    // Slider CONTINUOUS, dropdown EDGE-TRIGGERED. A held stick should slide a
    // value smoothly and must not race through a list of options.
    //
    // AND THE SLIDER KEEPS ITS OWN AIM, for the same reason the scroll does but
    // by a different mechanism: Unity's Slider rounds on set when wholeNumbers
    // is on, so a per-frame increment smaller than one would round straight back
    // to where it started and the value would never move at all. The aim
    // accumulates the fractional steps, and the rounding then happens where it
    // belongs - on the way in to the control.
    private void DriveMenuAdjust(float x)
    {
        if (!menuSettingControls.Value)
            return;

        // ONE snapshot of the node, and both null shapes.
        var node = menuIndex >= 0 && menuIndex < menuButtons.Count
            ? menuButtons[menuIndex]
            : null;

        if (node is null || node == null)
            return;

        try
        {
            var slider = node.TryCast<Il2CppFuturLab.PW2.UI.SettingSlider>();

            if (slider is not null && slider != null)
            {
                AdjustSlider(slider, CaptionOf(node), node.Pointer, x);
                return;
            }

            var dropdown = node.TryCast<Il2CppFuturLab.PW2.UI.SettingDropdown>();

            if (dropdown is not null && dropdown != null)
            {
                if (!menuAdjustArmed)
                    return;

                menuAdjustArmed = false;
                StepDropdown(dropdown, CaptionOf(node), x > 0f ? 1 : -1);
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  menu adjust threw {exception.GetType().Name}: "
                + exception.Message);
        }
    }

    private void AdjustSlider(Il2CppFuturLab.PW2.UI.SettingSlider slider,
        string caption, IntPtr pointer, float x)
    {
        var unity = slider.m_slider;

        if (unity is null || unity == null)
            return;

        var min = unity.minValue;
        var max = unity.maxValue;
        var span = max - min;

        if (span <= 0f)
            return;

        // Seeded once per gesture, and re-seeded when the pointer moves to a
        // different row mid-deflection.
        if (!adjustActive || adjustPointer != pointer)
        {
            EndAdjust();
            adjustActive = true;
            adjustPointer = pointer;
            adjustStart = unity.value;
            adjustAim = adjustStart;
            adjustCaption = caption;
            nextAdjustLog = 0f;
        }

        adjustAim = Mathf.Clamp(
            adjustAim + (x * span * menuAdjustSpeed.Value * Time.unscaledDeltaTime),
            min, max);

        // WRITES Slider.value, not SettingSlider.SetValue(float). SetValue sits
        // beside SetMinValue, SetMaxValue and UseWholeNumbers - the group that
        // configures the control. Slider.value fires onValueChanged, which is
        // what OnSliderValueChanged is wired to, which is what reaches the
        // SettingsManager.
        unity.value = adjustAim;

        if (Time.unscaledTime >= nextAdjustLog)
        {
            nextAdjustLog = Time.unscaledTime + 0.5f;
            LoggerInstance.Msg($"menu adjust: {caption} (slider)"
                + $"   aim {adjustAim:0.###}   value {unity.value:0.###}"
                + $"   range {min:0.###}..{max:0.###}");
        }
    }

    private void EndAdjust()
    {
        if (!adjustActive)
            return;

        LoggerInstance.Msg($"menu adjust: {adjustCaption} done"
            + $"   start {adjustStart:0.###}   final {adjustAim:0.###}");

        adjustActive = false;
        adjustPointer = IntPtr.Zero;
        adjustStart = 0f;
        adjustAim = 0f;
        adjustCaption = "";
        nextAdjustLog = 0f;
    }

    private void DriveMenuNavigation()
    {
        if (!menuMode)
            return;

        // POLLED BEFORE the no-buttons return, deliberately.
        //
        // X used to be polled in DriveButtons, which runs on every menu frame,
        // so a press already held when the menu opened had its down edge
        // consumed harmlessly. Polling A only after this return would hand that
        // press to the first frame with buttons instead - and A is jump, so
        // jumping into a menu would confirm whatever happened to be selected.
        menuAcceptButton.Poll(ButtonEdge.IsDown(rightPrimary), 0f);

        ScanMenuButtons();

        if (menuButtons.Count == 0)
        {
            // DER AUSWEG, und er ist der Grund fuer diesen Abschnitt.
            //
            // Gemeldet: nach Levelabschluss ein Popup mit Story-Text und
            // Knoepfen, nicht bedienbar. Der Log sagt warum - "0 interactable
            // button(s), dropped offscreen 9": die Knoepfe sind da, unsere
            // Projektion setzt sie ausserhalb des Schirms, und bei leerer
            // Liste kehrte diese Methode hier um. Damit tat A nichts und der
            // Zeiger nichts.
            //
            // Das Spiel waehlt in solchen Popups selbst einen Knopf vor -
            // gemessen in Abschnitt 106. Also wird der abgeschickt, wenn wir
            // nichts haben. Der Druck ist schon gepollt, die Kante also
            // vorhanden.
            if (menuAcceptButton.Tap)
                SubmitGameSelection();

            return;
        }

        try
        {
            var raw = moveAction?.ReadValueAsObject();
            var stick = raw is null ? Vector2.zero : raw.Unbox<Vector2>();

            // The dominant axis decides which of the four directions was meant,
            // so a diagonal push does one thing rather than both.
            var vertical = Mathf.Abs(stick.y) >= Mathf.Abs(stick.x);
            var magnitude = vertical ? Mathf.Abs(stick.y) : Mathf.Abs(stick.x);

            // Edge-triggered, and re-armed only when the WHOLE stick is centred -
            // the lesson from section 75 point 5, where re-arming on one axis let
            // a sustained deflection fire over and over.
            if (Mathf.Abs(stick.x) < turnDeadzone.Value && Mathf.Abs(stick.y) < turnDeadzone.Value)
                menuNavArmed = true;
            else if (menuNavArmed && magnitude > turnDeadzone.Value)
            {
                menuNavArmed = false;

                if (vertical)
                    MoveSelection(0, stick.y > 0f ? 1 : -1);
                else
                    MoveSelection(stick.x > 0f ? 1 : -1, 0);
            }

            // ACCEPT ON A AND ON THE RIGHT TRIGGER, both on the washer hand.
            //
            // It was X before, on the other hand, on the grounds that X doubles
            // as the world interaction and is therefore free while a menu owns
            // the input. Correct, and beside the point: in a menu the hand
            // reaches for A, because that is where every gamepad puts accept.
            // Reported as "I keep intuitively pressing the right trigger or A,
            // and not X".
            //
            // Both buttons are free here for the same reason: jumping and
            // spraying are suppressed while a menu is open, so neither press can
            // reach the world behind it.
            // The trigger keeps its original position, after the return. It
            // carries the same latent exposure - a trigger held as a menu opens -
            // but it has worked that way for several versions and spraying while
            // a menu appears is far rarer than jumping into one. Left alone
            // rather than changed on a hunch.
            menuSubmitButton.Poll(ButtonEdge.ReadAxis(triggerAction) > 0.6f, 0f);

            // NOT WHILE THE LIST IS MOVING. PointerSelect already drops the
            // highlight for the duration, but ScanMenuButtons re-seeds menuIndex
            // from the game's own IsSelected whenever this mod has none - so the
            // selection can come back underneath us mid-scroll. The submit is
            // the place where that would actually cost something, so the guard
            // belongs here too rather than only where the highlight is drawn.
            if ((menuAcceptButton.Tap || menuSubmitButton.Tap)
                && Time.unscaledTime < scrollHoldUntil)
            {
                LoggerInstance.Msg("menu submit: ignored, the list is scrolling");
            }
            else if ((menuAcceptButton.Tap || menuSubmitButton.Tap)
                && menuIndex >= 0 && menuIndex < menuButtons.Count)
            {
                var button = menuButtons[menuIndex];
                var caption = CaptionOf(button);

                Activate(button, caption);

                // Dropped: a submit almost always changes the screen.
                menuButtons.Clear();
                menuIndex = -1;
                menuSelectedPointer = IntPtr.Zero;
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  menu navigation threw {exception.GetType().Name}: "
                + exception.Message);
            menuButtons.Clear();
            menuIndex = -1;
            menuSelectedPointer = IntPtr.Zero;
        }
    }

    // TWO-DIMENSIONAL navigation, because the menus are not all lists.
    //
    // The first version stepped +-1 through one linear order. That works for a
    // column of buttons and is WRONG for a grid by construction: a step of one
    // follows the sort order, which runs along a row, so pushing the stick up
    // moved sideways. The user hit exactly that on a tab of tiles laid out as a
    // grid - "up and down did not change rows, it changed columns, the opposite
    // of what you expect".
    //
    // So a direction now picks the nearest candidate IN that direction, scored
    // on screen coordinates: distance along the axis of travel plus a penalty on
    // the perpendicular offset. The penalty is what keeps "up" inside its own
    // column rather than drifting to a nearer tile one column over, and three is
    // high enough to matter without forbidding a diagonal neighbour when a row
    // is ragged.
    //
    // No wrap-around at the edges. A grid that jumps from the top row to the
    // bottom of another column reads as a glitch, and stopping is what every
    // console menu does.
    private void MoveSelection(int dx, int dy)
    {
        if (menuButtons.Count == 0)
            return;

        var camera = Camera.main;

        if (camera is null || camera == null)
            return;

        // Nothing selected yet: start at the first in reading order, whichever
        // direction was pushed.
        if (menuIndex < 0 || menuIndex >= menuButtons.Count)
        {
            Select(0);
            return;
        }

        var from = ScreenOf(camera, menuButtons[menuIndex].transform);

        var best = -1;
        var bestCost = float.MaxValue;

        for (var index = 0; index < menuButtons.Count; index++)
        {
            if (index == menuIndex)
                continue;

            var to = ScreenOf(camera, menuButtons[index].transform);
            var delta = to - from;

            // Screen y grows upward, so a "down" request looks for a negative
            // delta.
            var along = dx != 0 ? delta.x * dx : delta.y * dy;
            var across = dx != 0 ? delta.y : delta.x;

            // Eight pixels of slack, so a button a hair off-axis still counts as
            // lying in the requested direction.
            if (along <= 8f)
                continue;

            // A CONE, then the nearest inside it - replacing a cost of
            // "along + 3 * across".
            //
            // That weighting made a distant button lying exactly on the axis beat
            // a near one slightly off it, which is precisely the reported
            // symptom: "sometimes it skips elements that are actually closer". It
            // is a reasonable rule for a regular grid and a bad one for the
            // irregular layouts these tabs actually use.
            //
            // Inside a 60 degree half-angle the choice is plain Euclidean
            // distance, which is what the eye expects. The cone is what keeps
            // "up" from selecting something far off to the side.
            if (Mathf.Abs(across) > along * 1.73f)
                continue;

            var cost = delta.magnitude;

            if (cost < bestCost)
            {
                bestCost = cost;
                best = index;
            }
        }

        if (best >= 0)
            Select(best);
    }

    // Activation differs per type: Submit on a button, isOn on a toggle. The
    // base has no activate, only ForceSelect, so a type with neither is reported
    // rather than silently ignored.
    private void Activate(Il2CppFuturLab.UIStateMonoBehaviour node, string caption)
    {
        try
        {
            var button = node.TryCast<Il2CppFuturLab.FuturButton>();

            if (button is not null)
            {
                button.Submit();
                LoggerInstance.Msg($"menu submit: {caption} (button)");
                return;
            }

            var toggle = node.TryCast<Il2CppFuturLab.FuturToggle>();
            var unityToggle = toggle?.Toggle;

            if (unityToggle is not null && unityToggle != null)
            {
                // A TOGGLE IN A GROUP IS A RADIO BUTTON, and flipping one is how
                // the main menu could be wedged.
                //
                // Reported: the menu hangs, the news carousel on Home stops, an
                // hourglass appears, and from then on the menu cannot be
                // operated - only closed, and then not reopened. Clicking the
                // tab that is ALREADY ACTIVE used to run isOn = !isOn on it,
                // which turns that tab OFF. A tab group with nothing on has no
                // page, which is exactly a Home tab with no news and a spinner.
                //
                // StepTab has always had this right - "isOn is a plain bool
                // property and firing its callback is the point" - and writes
                // true. This path flipped. Same control, two rules, and only one
                // of them was correct.
                //
                // group is the test rather than the node's name: a grouped
                // toggle cannot legitimately be switched off by a click, and an
                // ungrouped one - a settings checkbox - must be.
                var group = unityToggle.group;

                if (group is not null && group != null)
                {
                    var wasOn = unityToggle.isOn;
                    unityToggle.isOn = true;

                    // LOGGED WITH THE GROUP STATE, because this is also the
                    // measurement that confirms or kills the diagnosis above.
                    // AnyTogglesOn false at any point would mean a group really
                    // can end up with nothing selected.
                    LoggerInstance.Msg($"menu submit: {caption} (grouped toggle -> true, "
                        + $"was {wasOn}, group anyOn {group.AnyTogglesOn()}, "
                        + $"allowSwitchOff {group.allowSwitchOff})");
                    return;
                }

                unityToggle.isOn = !unityToggle.isOn;
                LoggerInstance.Msg($"menu submit: {caption} (toggle -> {unityToggle.isOn})");
                return;
            }

            // THE SETTINGS SUB-TABS - Gameplay, Video, Audio, Controls.
            //
            // Reported as dead, and the log had been saying so on every attempt:
            // nine "SettingsNavButton(Clone) - NEITHER button nor toggle" lines
            // across two sessions. They were in the candidate list the whole
            // time, they were highlighted, they were submitted - Activate simply
            // had no branch for their type.
            //
            // TabButton is the fourth and last type deriving from
            // UIStateMonoBehaviour, and it was the one with no activation path:
            // TabMechanism knew about it for LOGGING, and nothing acted on it.
            //
            // Submit(), not SetTabActive(). SetTabActive sits beside
            // SetTabInactive and LinkPage - the group that puts the control into
            // a state - and would light the tab without telling the TabManager
            // to switch pages. Submit() is virtual, void, parameterless and
            // overridden, the same shape already proven on FuturButton.
            var tab = node.TryCast<Il2CppFuturLab.TabButton>();

            if (tab is not null && tab != null)
            {
                var wasActive = tab.m_isTabActive;
                tab.Submit();
                LoggerInstance.Msg($"menu submit: {caption} (tab button, "
                    + $"was active {wasActive})");
                return;
            }

            // THE SETTINGS SCREEN, and it was never as far away as section 90
            // assumed. SettingUIElement derives from UIStateMonoBehaviour, so
            // every one of these rows was ALREADY in menuButtons and already
            // being highlighted - the only thing missing was a way to activate
            // one, and the line below said so every time: "NEITHER button nor
            // toggle".
            //
            // Four subtypes, and for each one the call chosen is the one that
            // fires the GAME'S OWN callback chain, not the one that only moves
            // the visual. Where a type offers both - SetValue next to
            // SetValueWithoutNotify next to InitializeDropdown - that pair is
            // the initialisation group, and writing the underlying Unity
            // control's value instead is what reaches the SettingsManager.
            if (menuSettingControls.Value && ActivateSetting(node, caption))
                return;

            LoggerInstance.Msg($"menu submit: {caption} - NEITHER button nor toggle, "
                + "cannot be activated");
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  menu activate threw {exception.GetType().Name}: "
                + exception.Message);
        }
    }

    // Returns false when the node is not a settings row at all, so Activate can
    // fall through to its "neither button nor toggle" report and keep saying
    // something useful about a type nobody has looked at yet.
    private bool ActivateSetting(Il2CppFuturLab.UIStateMonoBehaviour node, string caption)
    {
        var setting = node.TryCast<Il2CppFuturLab.PW2.UI.SettingUIElement>();

        if (setting is null || setting == null)
            return false;

        // THE CHECKBOX. m_toggle.isOn is a plain bool on the Unity control and
        // firing its callback is the whole point - the same shape the FuturToggle
        // branch above and StepTab already use, so it is the proven one here.
        //
        // NOT Submit(). SettingToggle does override OnSubmit, and that would be
        // the game's own route, but then the new state can only be learned by
        // reading back - and a read-back cannot tell "it did nothing" from "it
        // has not happened yet". Writing the bool is deterministic.
        var settingToggle = node.TryCast<Il2CppFuturLab.PW2.UI.SettingToggle>();

        if (settingToggle is not null && settingToggle != null)
        {
            var unity = settingToggle.m_toggle;

            if (unity is not null && unity != null)
            {
                unity.isOn = !unity.isOn;
                LoggerInstance.Msg($"menu setting: {caption} (checkbox -> {unity.isOn})");
                return true;
            }

            LoggerInstance.Msg($"menu setting: {caption} (checkbox with no m_toggle)");
            return true;
        }

        // THE DROPDOWN, one step forward per click, and the list never opens.
        // Opening it means driving a runtime-cloned template in its own canvas
        // behind its own blocker; stepping the value needs none of that and is
        // the same gesture as the right stick.
        var settingDropdown = node.TryCast<Il2CppFuturLab.PW2.UI.SettingDropdown>();

        if (settingDropdown is not null && settingDropdown != null)
            return StepDropdown(settingDropdown, caption, 1);

        // THE SLIDER. A submit on a slider means nothing, so it says where the
        // value lives instead of silently doing nothing.
        var settingSlider = node.TryCast<Il2CppFuturLab.PW2.UI.SettingSlider>();

        if (settingSlider is not null && settingSlider != null)
        {
            LoggerInstance.Msg($"menu setting: {caption} (slider - right stick "
                + "left/right changes it)");
            return true;
        }

        // THE BUTTON, including the kind that raises a popup. Submit() is the
        // inherited virtual/void/parameterless form and SettingButton overrides
        // OnSubmit, so this is the game's own route. The popup's own buttons are
        // FuturPopupButton : FuturButton and run through the existing pipeline
        // from there, with no further work.
        var settingButton = node.TryCast<Il2CppFuturLab.PW2.UI.SettingButton>();

        if (settingButton is not null && settingButton != null)
        {
            setting.Submit();
            LoggerInstance.Msg($"menu setting: {caption} (button, "
                + $"popup {settingButton.m_usePopup})");
            return true;
        }

        // A SettingUIElement of some subtype nobody has met yet. Submit is
        // declared on the base, so this is still a safe call, and the log names
        // the case rather than hiding it.
        setting.Submit();
        LoggerInstance.Msg($"menu setting: {caption} (base Submit, type {setting.Type})");
        return true;
    }

    // WRITES m_dropdown.value, not SettingDropdown.SetValue(int).
    //
    // SetValue sits beside SetValueWithoutNotify, RefreshDropdownOptions and
    // InitializeDropdown - the initialisation group, which is about putting the
    // control into a state rather than reporting a user choice. Writing the
    // TMP_Dropdown's own value fires onValueChanged, which is what
    // OnDropdownValueChanged is wired to, which is what reaches the
    // SettingsManager. The visual and the saved setting stay in step because the
    // game does the stepping.
    private bool StepDropdown(Il2CppFuturLab.PW2.UI.SettingDropdown dropdown,
        string caption, int direction)
    {
        var unity = dropdown.m_dropdown;

        if (unity is null || unity == null)
        {
            LoggerInstance.Msg($"menu setting: {caption} (dropdown with no m_dropdown)");
            return true;
        }

        var options = unity.options;
        var count = options is null || options == null ? 0 : options.Count;

        if (count <= 1)
        {
            LoggerInstance.Msg($"menu setting: {caption} (dropdown, {count} option(s) "
                + "- nothing to step)");
            return true;
        }

        var from = unity.value;
        // Wraps in both directions. C# % keeps the sign of the dividend, so the
        // double modulo is what makes -1 land on the last option.
        var to = (((from + direction) % count) + count) % count;

        unity.value = to;

        LoggerInstance.Msg($"menu setting: {caption} (dropdown {from} -> {to} of {count})");
        return true;
    }

    // DEN VORGEWAEHLTEN KNOPF DES SPIELS ABSCHICKEN.
    //
    // Nur fuer den Fall, dass unsere Kandidatenliste leer ist: dann ist dies
    // die einzige Bedienung, die es gibt. Es ersetzt die Liste nicht und
    // konkurriert nicht mit ihr - wo sie etwas hat, laeuft alles wie bisher.
    //
    // FuturButton.Submit ist dieselbe Methode, die TrySubmitCloseButton und
    // Activate schon benutzen. Ein UIStateMonoBehaviour ohne FuturButton -
    // ein Toggle etwa - wird genannt und nicht geraten.
    private void SubmitGameSelection()
    {
        try
        {
            var system = UnityEngine.EventSystems.EventSystem.current;

            if (system is null || system == null)
            {
                LoggerInstance.Msg("menu fallback: no EventSystem, nothing to submit");
                return;
            }

            var selected = system.currentSelectedGameObject;

            if (selected is null || selected == null)
            {
                LoggerInstance.Msg("menu fallback: the game has nothing selected either - "
                    + "no way in from here");
                return;
            }

            var button = selected.GetComponent<Il2CppFuturLab.FuturButton>();

            if (button is null || button == null)
            {
                LoggerInstance.Msg($"menu fallback: \"{selected.name}\" is selected but "
                    + "carries no FuturButton");
                return;
            }

            var caption = "?";

            try
            {
                caption = button.HasCaption ? button.Caption : "(none)";
            }
            catch
            {
                // Die Beschriftung ist fuer den Log, nicht fuer die Wirkung.
            }

            button.Submit();
            LoggerInstance.Msg($"menu fallback: submitted the game's own selection "
                + $"\"{caption}\" on {selected.name} - our candidate list was empty");
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  menu fallback threw {exception.GetType().Name}: "
                + exception.Message);
        }
    }

    private void Select(int index)
    {
        if (index < 0 || index >= menuButtons.Count)
            return;

        var previous = menuIndex;

        menuIndex = index;

        if (previous >= 0 && previous < menuButtons.Count && previous != menuIndex)
        {
            var old = menuButtons[previous];

            if (old is not null && old != null)
            {
                old.ForceDeselect();
                SendHover(old, false);
            }
        }

        var target = menuButtons[menuIndex];

        if (target is null || target == null)
            return;

        // DIESELBE KACHEL, NUR AN ANDERER STELLE DER LISTE: dann ist nichts zu
        // tun. Ohne diese Zeile feuert ForceSelect nach jedem Rescan erneut,
        // der Uebergang startet von vorn, und die Kachel blinkt - waehrend sie
        // in Tabs mit stabiler Liste ruhig leuchtet. Der Index hat sich
        // geaendert, das Ziel nicht.
        if (menuSelectedPointer == target.Pointer)
            return;

        menuSelectedPointer = target.Pointer;

        target.ForceSelect();

        // NACH ForceSelect, nicht statt: das Select traegt weiter die
        // Aktivierung ueber den Trigger, das Zeigen traegt das Bild. Die zwei
        // sind im Spiel zwei Dinge, und genau das war der Fehlschluss des
        // ersten Anlaufs.
        SendHover(target, true);
        MoveEventSelection(target);
        MoveGameHover(target.gameObject);
        LoggerInstance.Msg($"menu select [{menuIndex + 1}/{menuButtons.Count}] "
            + CaptionOf(target));
    }

    // DEM EINGABEMODUL DES SPIELS SAGEN, WO GEZEIGT WIRD - Abschnitt 106.
    //
    // HandlePointerExitAndEnter ist nativ PROTECTED und von Il2CppInterop
    // oeffentlich gestellt. Aufrufbar ist sie damit; was die Regel aus
    // Abschnitt 98 betrifft - interop-public ist nicht nativ-public -, ist die
    // Erwartung an ihre STABILITAET, nicht ihre Erreichbarkeit. Sie ist Unitys
    // Code und nicht der des Spiels, also so stabil wie das UI-Paket selbst.
    //
    // Der Aufruf ersetzt kein ForceSelect: die Auswahl traegt weiter die
    // Aktivierung ueber den Trigger, das Hover traegt das Bild. Genau diese
    // Trennung hat in Abschnitt 105 drei Anlaeufe gekostet.
    private void MoveGameHover(GameObject? target)
    {
        if (!menuGameHover.Value || gameHoverDropped)
            return;

        try
        {
            var system = UnityEngine.EventSystems.EventSystem.current;

            if (system is null || system == null)
                return;

            if (!EnsureCursorModule())
                return;

            // EINE Instanz, und sie wird nur gebaut, nicht ersetzt: die
            // Hover-Kette lebt in ihr. Ein frisches Objekt pro Aufruf haette
            // keine Vorgeschichte, und die alte Kachel bekaeme nie ihr Exit.
            if (hoverData is null)
                hoverData = new UnityEngine.EventSystems.PointerEventData(system);

            hoverData.position = pointerCursor;

            var pointer = target is null || target == null ? IntPtr.Zero : target.Pointer;

            // NUR BEI WECHSEL. HandlePointerExitAndEnter ist bei gleichem Ziel
            // ein Nullaufruf - aber ihn pro Frame zu machen hiesse, die
            // Lehre aus 105 zu wiederholen, wo haeufiger schlimmer war.
            if (pointer == gameHoverTarget)
                return;

            gameHoverTarget = pointer;

            cursorModule!.HandlePointerExitAndEnter(hoverData, target);

            if (!loggedGameHover)
            {
                loggedGameHover = true;

                var entered = hoverData.pointerEnter;

                LoggerInstance.Msg($"menu game hover: module \"{cursorModule.name}\""
                    + $"   target {(target is null || target == null ? "none" : $"\"{target.name}\"")}"
                    + $"   pointerEnter after the call "
                    + $"{(entered is null || entered == null ? "nothing" : $"\"{entered.name}\"")}"
                    + $"   at {pointerCursor.x:0},{pointerCursor.y:0}");
            }
        }
        catch (Exception exception)
        {
            // Ein Feld, keine Preference - ein Fehlschlag darf die Einstellung
            // des Nutzers nicht umschreiben. Die Lehre aus 105.
            gameHoverDropped = true;
            LoggerInstance.Warning($"  menu game hover threw {exception.GetType().Name}: "
                + exception.Message + " - game hover switched off for this session");
        }
    }

    // DAS EVENTSYSTEM DES SPIELS - und NICHT EventSystem.current.
    //
    // Der Zensus hat fuenf gezaehlt, und current gehoert UnityExplorer:
    //
    //     [1] MultiplayerEventSystem  "Viewport0"  root UIRoot(Clone)  active
    //     [2] EventSystem             "UniverseLibCanvas"     IS current
    //
    // Drei Laeufe lang hat die Mod in [2] geschrieben. Die Rueckleseprobe hat
    // es dann in einem Satz gesagt: "read back as NOTHING right after the
    // write". Das Spiel liest [1], also muss dort geschrieben werden.
    //
    // GEANKERT AUF EINE SPIELKOMPONENTE, nicht auf einen Objektnamen: das
    // Cursor-Modul des Spiels sitzt auf demselben GameObject wie sein
    // EventSystem, und EnsureCursorModule findet es ueber seinen TYP und nimmt
    // den ersten AKTIVEN. Ein Test auf "Viewport0" waere ein Namenstest und
    // damit die Bruchstelle aus pinned-path-is-a-name-test.
    private UnityEngine.EventSystems.EventSystem? GameEventSystem()
    {
        if (gameEventSystem is not null && gameEventSystem != null)
            return gameEventSystem;

        if (EnsureCursorModule() && cursorModule is not null && cursorModule != null)
        {
            var onModule = cursorModule
                .GetComponent<UnityEngine.EventSystems.EventSystem>();

            if (onModule is not null && onModule != null)
            {
                gameEventSystem = onModule;

                if (!loggedGameEventSystem)
                {
                    loggedGameEventSystem = true;

                    var current = UnityEngine.EventSystems.EventSystem.current;
                    var isCurrent = current is not null && current != null
                        && current.Pointer == onModule.Pointer;

                    LoggerInstance.Msg($"menu selection: addressing "
                        + $"{GameInput.NativeTypeOf(onModule)} on "
                        + $"\"{onModule.gameObject.name}\""
                        + $"   {(isCurrent ? "it IS EventSystem.current" : "NOT EventSystem.current - that one belongs to UnityExplorer")}");
                }

                return gameEventSystem;
            }
        }

        // DER RUECKFALL, benannt statt still. Er ist nicht schlechter als der
        // Zustand vor dieser Fassung - aber er ist auch nicht besser, und das
        // muss im Log stehen, sonst sucht der naechste Lauf an der falschen
        // Stelle.
        if (!loggedEventSystemFallback)
        {
            loggedEventSystemFallback = true;
            LoggerInstance.Warning("  menu selection: no EventSystem on the cursor "
                + "module's object - falling back to EventSystem.current, which "
                + "measured as UnityExplorer's. Selection writes will not land.");
        }

        return UnityEngine.EventSystems.EventSystem.current;
    }

    // Das Modul, getaktet gesucht und beide null-Formen geprueft: nach einem
    // Auftragswechsel ist die alte Instanz zerstoert und "is null" sieht das
    // nicht.
    private bool EnsureCursorModule()
    {
        if (cursorModule is not null && cursorModule != null)
            return true;

        if (Time.unscaledTime < nextCursorModuleSearch)
            return false;

        nextCursorModuleSearch = Time.unscaledTime + 0.5f;

        try
        {
            var found = Resources.FindObjectsOfTypeAll(
                Il2CppInterop.Runtime.Il2CppType
                    .Of<Il2CppFuturLab.PW2.ControllerCursorInputModule>());

            for (var index = 0; index < found.Length; index++)
            {
                var module = found[index]
                    ?.TryCast<Il2CppFuturLab.PW2.ControllerCursorInputModule>();

                if (module is null || module == null)
                    continue;

                // DER ERSTE AKTIVE, nicht der erste ueberhaupt: die anderen
                // Instanzen sind Split-Screen-Vorhaltung und liegen ausserhalb
                // der Hierarchie. Genau dieser Griff hat die erste
                // Cursor-Sondierung in Abschnitt 91 bedeutungslos gemacht.
                if (!module.gameObject.activeInHierarchy)
                    continue;

                cursorModule = module;
                return true;
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  cursor module search threw "
                + $"{exception.GetType().Name}: {exception.Message}");
        }

        return false;
    }

    // DIE AUSWAHL DES EVENTSYSTEMS AUF DIESE KACHEL, und die erste Zeile im Log
    // loest eine Zweideutigkeit auf, die der vorige Lauf hinterlassen hat.
    //
    // Dort stand "ZUR BASIS ... eventSystem holds ImageButton_HomeBase" - und
    // ob das ZWEI Objekte sind oder EIN Objekt unter zwei Namen (Beschriftung
    // gegen Objektname), entscheidet ueber zwei voellig verschiedene
    // Folgeschritte. Darum vergleicht diese Methode die Objekte selbst und
    // nennt beide Namen.
    //
    // Halten beide dasselbe Objekt, ist SetSelectedGameObject wirkungslos und
    // das Leuchten haengt an etwas Drittem - dann ist dieser Weg beantwortet.
    // WIE VIELE EVENTSYSTEME, UND WELCHES IST AKTUELL.
    //
    // Der Zensus aus Abschnitt 34 hat "EventSystem x1, MultiplayerEventSystem
    // x2" gezaehlt, dazu kommt das eigene von UniverseLib. Vier Kandidaten
    // fuer eine Auswahl, und bisher ist unbelegt, in welches die Mod
    // schreibt.
    //
    // Resources.FindObjectsOfTypeAll statt FindObjectsOfType, weil das
    // inaktive ueberspringt - dieselbe Form wie GameUi.Resolve. Der Typname
    // kommt ueber den ZEIGER, damit kein Member auf einem Wrapper angefasst
    // wird.
    private void ReportEventSystems()
    {
        if (loggedEventSystems)
            return;

        loggedEventSystems = true;

        try
        {
            var all = Resources.FindObjectsOfTypeAll(
                Il2CppInterop.Runtime.Il2CppType
                    .Of<UnityEngine.EventSystems.EventSystem>());

            var current = UnityEngine.EventSystems.EventSystem.current;

            LoggerInstance.Msg($"event systems: {all.Length} found");

            for (var index = 0; index < all.Length; index++)
            {
                var system = all[index]?
                    .TryCast<UnityEngine.EventSystems.EventSystem>();

                if (system is null || system == null)
                    continue;

                var root = system.transform.root;
                var module = system.currentInputModule;

                LoggerInstance.Msg($"  eventSystem[{index}] "
                    + $"{GameInput.NativeTypeOf(system)}"
                    + $"   object \"{system.gameObject.name}\""
                    + $"   root \"{(root is null || root == null ? "-" : root.name)}\""
                    + $"   enabled {system.enabled}"
                    + $"   active {system.gameObject.activeInHierarchy}"
                    + $"   module "
                    + $"{(module is null || module == null ? "none" : module.name)}"
                    + $"   {(current is not null && current != null && current.Pointer == system.Pointer ? "IS EventSystem.current" : "not current")}");
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  event system census threw "
                + $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private void MoveEventSelection(Il2CppFuturLab.UIStateMonoBehaviour node)
    {
        if (!menuSetEventSelection.Value || selectionRouteDropped)
            return;

        try
        {
            var system = GameEventSystem();

            if (system is null || system == null)
                return;

            var target = node.gameObject;

            if (target is null || target == null)
                return;

            var held = system.currentSelectedGameObject;

            // Am POINTER verglichen, nicht am Namen: Il2CppInterop gibt bei
            // jedem Zugriff einen frischen Wrapper heraus, und zwei Wrapper auf
            // dasselbe Objekt sind nicht dasselbe Objekt fuer ==.
            var same = held is not null && held != null
                && held.Pointer == target.Pointer;

            // EINMAL PRO VERDIKT, nicht einmal pro Sitzung. Traefe der Zeiger
            // beim ersten Wechsel zufaellig das Element, das das Spiel schon
            // ausgewaehlt hat, stuende dort "SAME OBJECT" - und der
            // interessante Fall waere fuer den Rest der Sitzung stumm.
            if (same ? !loggedSelectionSame : !loggedSelectionDiffer)
            {
                if (same)
                    loggedSelectionSame = true;
                else
                    loggedSelectionDiffer = true;

                LoggerInstance.Msg($"menu selection route: node \"{target.name}\""
                    + $"   caption {CaptionOf(node)}"
                    + $"   eventSystem held "
                    + $"{(held is null || held == null ? "nothing" : $"\"{held.name}\"")}"
                    + $"   {(same ? "SAME OBJECT - moving it changes nothing, the lit look hangs on something else" : "different objects - moving the selection is the lever")}");
            }

            if (same)
                return;

            // EINMAL, UND ZWAR HIER: der erste Schreibvorgang ist der Moment,
            // in dem die Frage "ein EventSystem oder mehrere, und wessen"
            // ueberhaupt zaehlt. Der vorhandene Tracer kann sie nicht
            // beantworten - er meldet den Modulnamen nur, wenn sich der
            // AUSWAHLNAME aendert, und stand deshalb den ganzen Lauf auf einer
            // einzigen Probe aus Sekunde 18.
            ReportEventSystems();

            // DIE RUECKLESEPROBE PRO ZIELWECHSEL NEU ARMIEREN, nicht einmal
            // pro Sitzung. Sonst sagt sie etwas ueber die erste Kachel und
            // schweigt danach - und ein Umschwung mitten in der Sitzung waere
            // unsichtbar. Gedeckelt, damit ein Schwenk durch eine lange Liste
            // das Log nicht flutet.
            if (lastAssertedTarget != target.Pointer && readBackLines < 12)
            {
                lastAssertedTarget = target.Pointer;
                loggedReadBackTook = false;
                loggedReadBackNull = false;
                loggedReadBackOther = false;
            }

            // DAS FENSTER VOR DEM SCHREIBEN ARMIEREN. Das Spiel reagiert auf
            // unseren Schreibvorgang, teils noch in ihm - waere der Waechter
            // danach scharf, ginge genau die erste Leerung durch.
            GameInput.KeepSelectionTarget = target.Pointer;
            GameInput.KeepSelectionUntil =
                Time.unscaledTime + GameInput.KeepSelectionWindow;

            GameInput.ModWriting = true;

            try
            {
                system.SetSelectedGameObject(target);
            }
            finally
            {
                // IMMER ZURUECKSETZEN, auch bei einem Wurf: eine haengende
                // Marke wuerde jede spaetere Logzeile falsch beschriften, und
                // eine falsche Beschriftung ist schlimmer als keine.
                GameInput.ModWriting = false;
            }

            selectionReasserts++;

            // DIE RUECKLESEPROBE, einmal je Verdikt.
            //
            // Sie trennt zwei Ursachen, die im Log bisher gleich aussahen:
            // "der Schreibvorgang wirkt und wird spaeter geleert" gegen "der
            // Schreibvorgang kommt nie an". Ohne sie waere der naechste Lauf
            // wieder eine Vermutung.
            var after = system.currentSelectedGameObject;
            var took = after is not null && after != null
                && after.Pointer == target.Pointer;

            if (took && !loggedReadBackTook)
            {
                loggedReadBackTook = true;
                readBackLines++;
                LoggerInstance.Msg($"menu selection: read back as \"{target.name}\" "
                    + "right after the write - the write lands, so anything that "
                    + "empties it arrives later.");
            }
            else if (!took && (after is null || after == null) && !loggedReadBackNull)
            {
                loggedReadBackNull = true;
                readBackLines++;
                LoggerInstance.Warning("  menu selection: read back as NOTHING right "
                    + "after the write - the write never lands. The selection guard "
                    + "cannot help here; see the eventSystem census above.");
            }
            else if (!took && after is not null && after != null && !loggedReadBackOther)
            {
                loggedReadBackOther = true;
                readBackLines++;
                LoggerInstance.Msg($"menu selection: read back as \"{after.name}\" "
                    + $"instead of \"{target.name}\" - something redirected the write.");
            }

            // WER SIE GESTOHLEN HAT, einmal mit Namen. Eine Zahl sagt nicht,
            // WELCHES Element das Spiel bevorzugt - und genau der Name
            // (Button_Close, Button_Back) war der Schluessel zur Diagnose.
            if (!loggedSelectionThief && held is not null && held != null)
            {
                loggedSelectionThief = true;
                LoggerInstance.Msg($"menu selection: the game held \"{held.name}\" while "
                    + $"the pointer rested on {CaptionOf(node)} - re-asserting. "
                    + "This is the page default pulling the selection back.");
            }
        }
        catch (Exception exception)
        {
            // Wie beim Zeigen: ein Feld, keine Preference. Ein Fehlschlag darf
            // die Einstellung des Nutzers nicht umschreiben - und eine
            // Selbstzuweisung auf .Value, wie sie hier zuerst stand, war
            // schlicht Unsinn: sie tut nichts und schreibt die cfg an.
            selectionRouteDropped = true;
            LoggerInstance.Warning($"  menu selection route threw "
                + $"{exception.GetType().Name}: {exception.Message}"
                + " - selection moves switched off for this session");
        }
    }

    // POINTER-ENTER UND POINTER-EXIT, in der Form, die diese Datei schon
    // traegt: GetComponent ueber Il2CppType, TryCast auf die Schnittstelle,
    // und die NICHT-GENERISCHE Ueberladung von ExecuteEvents. Interface plus
    // Klasse, kein Struct und kein Delegate ueber die Interop-Grenze - siehe
    // TryUiCancel, wo derselbe Weg fuer ICancelHandler gemessen laeuft.
    //
    // POINTEREVENTDATA, UND DAS WAR EIN FEHLER IM ERSTEN ANLAUF. Die
    // nicht-generische Ueberladung DEKLARIERT BaseEventData, der Handler
    // verlangt aber zur Laufzeit das Genaue:
    //
    //     menu hover threw Il2CppException: Invalid type:
    //     UnityEngine.EventSystems.BaseEventData passed to event expecting
    //     UnityEngine.EventSystems.PointerEventData
    //
    // Der deklarierte Parametertyp ist also nicht der verlangte. Fuer
    // ICancelHandler in TryUiCancel genuegt BaseEventData - dort ist es das
    // richtige -, und daraus hatte ich geschlossen, es genuege hier auch.
    // PointerEventData ist eine KLASSE und erbt von BaseEventData, damit bleibt
    // die Form dieselbe: Interface plus Klasse, kein Struct.
    //
    // Die Position wird mitgegeben, weil ein Handler, der sie liest, sonst auf
    // (0,0) zeigt - und das ist im Zweifel eine andere Kachel.
    private void SendHover(Il2CppFuturLab.UIStateMonoBehaviour node, bool entering)
    {
        if (!menuHoverEvents.Value || hoverDropped)
            return;

        try
        {
            var system = UnityEngine.EventSystems.EventSystem.current;

            if (system is null || system == null)
                return;

            var type = entering
                ? Il2CppInterop.Runtime.Il2CppType
                    .Of<UnityEngine.EventSystems.IPointerEnterHandler>()
                : Il2CppInterop.Runtime.Il2CppType
                    .Of<UnityEngine.EventSystems.IPointerExitHandler>();

            var component = node.gameObject.GetComponent(type);

            // DIE EINE ZEILE, DIE DEN WEG ENTSCHEIDET. Tragen die Kacheln diese
            // Schnittstellen nicht, ist Zeigen kein Hebel - und das soll im Log
            // stehen, nicht im Headset erraten werden. Nur beim ersten Enter,
            // damit es keine Zeile pro Kachel gibt.
            if (entering && !loggedHoverProbe)
            {
                loggedHoverProbe = true;
                // WER DAUERHAFT LEUCHTET, mit in derselben Zeile. Gemeldet
                // ist, dass in jedem Tab EIN Element leuchtet, auch wenn man
                // ueber andere fahrt - das ist die Auswahl des EventSystems,
                // und wenn sie es ist, dann ist SetSelectedGameObject der
                // naechste Hebel und nicht ForceSelect.
                var held = system.currentSelectedGameObject;

                LoggerInstance.Msg($"menu hover: {CaptionOf(node)} "
                    + $"{(component is null || component == null ? "carries NO IPointerEnterHandler - pointing is not a lever here" : "carries IPointerEnterHandler, sending enter")}"
                    + $"   eventSystem holds "
                    + $"{(held is null || held == null ? "nothing" : $"\"{held.name}\"")}");
            }

            if (component is null || component == null)
                return;

            var data = new UnityEngine.EventSystems.PointerEventData(system)
            {
                position = pointerCursor,
            };

            if (entering)
            {
                var handler = component
                    .TryCast<UnityEngine.EventSystems.IPointerEnterHandler>();

                if (handler is not null)
                    UnityEngine.EventSystems.ExecuteEvents.Execute(handler, data);
            }
            else
            {
                var handler = component
                    .TryCast<UnityEngine.EventSystems.IPointerExitHandler>();

                if (handler is not null)
                    UnityEngine.EventSystems.ExecuteEvents.Execute(handler, data);
            }
        }
        catch (Exception exception)
        {
            // EIN FELD, KEINE PREFERENCE - und das ist die Korrektur eines
            // eigenen Fehlers. Der erste Anlauf schrieb menuHoverEvents.Value
            // auf false, MelonPreferences hat das beim Spielende in die cfg
            // gespeichert, und damit haette der naechste Lauf mit
            // abgeschalteter Funktion begonnen, ohne dass es jemand wollte.
            // Ein Fehlschlag darf die Einstellung des Nutzers nicht umschreiben.
            hoverDropped = true;
            LoggerInstance.Warning($"  menu hover threw {exception.GetType().Name}: "
                + exception.Message + " - hover events switched off for this session");
        }
    }

    // IsInteractable is declared on the concrete types rather than on the base,
    // so it is reached by cast. A node that is neither a button nor a toggle -
    // a TabButton, say - counts as interactable, since the tab bar is driven
    // separately and excluding it here would only hide it from the log.
    private static bool Interactable(Il2CppFuturLab.UIStateMonoBehaviour node)
    {
        try
        {
            var button = node.TryCast<Il2CppFuturLab.FuturButton>();

            if (button is not null)
                return button.IsInteractable;

            var toggle = node.TryCast<Il2CppFuturLab.FuturToggle>();

            if (toggle is not null)
                return toggle.IsInteractable;

            // IsInteractable is ABSTRACT on SettingUIElement and implemented by
            // all four subtypes, so one cast to the base covers them. Without
            // this a greyed-out settings row fell through to the blanket true
            // below and was selectable - which is the "dead elements you have to
            // step past" complaint, one screen further on.
            var setting = node.TryCast<Il2CppFuturLab.PW2.UI.SettingUIElement>();

            if (setting is not null)
                return setting.IsInteractable;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string CaptionOf(Il2CppFuturLab.UIStateMonoBehaviour node)
    {
        try
        {
            var button = node.TryCast<Il2CppFuturLab.FuturButton>();

            if (button is not null && button.HasCaption)
                return button.Caption;

            // A settings row carries no Caption, so without this the log showed
            // object names for the whole settings screen. LocalizationKey is a
            // plain string and reads like the row does.
            var setting = node.TryCast<Il2CppFuturLab.PW2.UI.SettingUIElement>();

            if (setting is not null)
            {
                var key = setting.LocalizationKey;

                if (!string.IsNullOrEmpty(key))
                    return key;
            }

            // A sub-tab carries its label in a TMP text, and without this every
            // one of the five logged as "SettingsNavButton(Clone)" - which is
            // how nine failed activations said nothing about WHICH tab.
            var tabButton = node.TryCast<Il2CppFuturLab.TabButton>();

            if (tabButton is not null)
            {
                var label = tabButton.Text;

                if (label is not null && label != null && !string.IsNullOrEmpty(label.text))
                    return label.text;
            }

            return node.name;
        }
        catch
        {
            return "?";
        }
    }

    private readonly List<Transform> tabNodes = new();
    private float nextTabScan;
    private int tabIndex = -1;
    private bool loggedTabs;
    private readonly ButtonEdge tabPrevButton = new();
    private readonly ButtonEdge tabNextButton = new();
    private readonly ButtonEdge menuSubmitButton = new();
    private readonly ButtonEdge menuAcceptButton = new();

    // MENU TABS ON THE GRIPS, which is where the user asked for them and where
    // both grips are already free: dirt highlight and the spray latch are
    // switched off while a menu owns the input.
    //
    // NOT through TabManager.ChangeTab(bool), which looks like exactly the right
    // call - void, one bool, almost certainly a direction. TabManager is GENERIC
    // in this build (the probe reports it as a TypeSpecification), so finding an
    // instance at runtime needs the concrete type argument, and that is not
    // known. Guessing a generic instantiation across this interop is the family
    // that killed the process twice.
    //
    // The tabs themselves are reachable without it. They are named TabToggle_*
    // - TabToggle_Home appeared in the EventSystem selection log - and they
    // carry either a FuturButton, whose Submit() is already proven, or a
    // FuturToggle, whose Toggle.isOn is a plain bool. Both are safe shapes.
    //
    // Ordered LEFT TO RIGHT, unlike the menu buttons: a tab bar is horizontal,
    // and ordering it top-to-bottom would make "next" jump unpredictably.
    private void DriveMenuTabs()
    {
        if (!menuMode)
            return;

        try
        {
            if (tabNodes.Count == 0 || Time.unscaledTime >= nextTabScan)
            {
                nextTabScan = Time.unscaledTime + 0.5f;
                ScanTabs();
            }

            if (tabNodes.Count == 0)
                return;

            tabPrevButton.Poll(ButtonEdge.ReadAxis(leftSqueeze) > 0.6f, 0f);
            tabNextButton.Poll(ButtonEdge.ReadAxis(rightSqueeze) > 0.6f, 0f);

            // Left grip steps LEFT, right grip steps RIGHT, against a bar that
            // is now sorted by SCREEN x. Reported inverted in 0.85.0, where the
            // sort was by world x and therefore pointed wherever the player
            // happened to face.
            if (tabPrevButton.Tap)
                StepTab(-1);

            if (tabNextButton.Tap)
                StepTab(1);
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  menu tabs threw {exception.GetType().Name}: {exception.Message}");
            tabNodes.Clear();
        }
    }

    private void ScanTabs()
    {
        tabNodes.Clear();

        var root = gameUi.Root;

        if (root is null)
            return;

        CollectBySubstring(root, "TabToggle", tabNodes);

        // Only what is on screen, and sorted by x so the order matches the bar.
        for (var index = tabNodes.Count - 1; index >= 0; index--)
        {
            if (!tabNodes[index].gameObject.activeInHierarchy)
                tabNodes.RemoveAt(index);
        }

        var tabCamera = Camera.main;

        if (tabCamera is not null && tabCamera != null)
        {
            for (var i = 1; i < tabNodes.Count; i++)
            {
                var candidate = tabNodes[i];
                var key = ScreenOf(tabCamera, candidate).x;
                var j = i - 1;

                while (j >= 0 && ScreenOf(tabCamera, tabNodes[j]).x > key)
                {
                    tabNodes[j + 1] = tabNodes[j];
                    j--;
                }

                tabNodes[j + 1] = candidate;
            }
        }

        if (!loggedTabs && tabNodes.Count > 0)
        {
            loggedTabs = true;

            for (var index = 0; index < tabNodes.Count && index < 8; index++)
                LoggerInstance.Msg($"    tab [{index + 1}] {tabNodes[index].name}   "
                    + $"x {tabNodes[index].position.x:0.##}   {TabMechanism(tabNodes[index])}");

            LoggerInstance.Msg($"  menu tabs: {tabNodes.Count} found, left grip back, right grip forward");
        }
    }

    // Says which of the two activation routes a tab node offers, so the log
    // explains a tab that does not respond instead of leaving it a mystery.
    private static string TabMechanism(Transform node)
    {
        try
        {
            if (node.GetComponent<Il2CppFuturLab.FuturButton>() is not null)
                return "FuturButton";

            if (node.GetComponent<Il2CppFuturLab.FuturToggle>() is not null)
                return "FuturToggle";

            if (node.GetComponent<Il2CppFuturLab.TabButton>() is not null)
                return "TabButton";

            return "NEITHER - cannot be activated";
        }
        catch
        {
            return "component read threw";
        }
    }

    private void StepTab(int direction)
    {
        if (tabNodes.Count == 0)
            return;

        tabIndex = tabIndex < 0
            ? (direction > 0 ? 0 : tabNodes.Count - 1)
            : (tabIndex + direction + tabNodes.Count) % tabNodes.Count;

        var node = tabNodes[tabIndex];

        if (node is null || node == null)
            return;

        try
        {
            var button = node.GetComponent<Il2CppFuturLab.FuturButton>();

            if (button is not null && button != null)
            {
                button.Submit();
                LoggerInstance.Msg($"menu tab [{tabIndex + 1}/{tabNodes.Count}] "
                    + $"{node.name} via Submit");

                // The button list belongs to the old page.
                menuButtons.Clear();
                menuIndex = -1;
                menuSelectedPointer = IntPtr.Zero;
                return;
            }

            var toggle = node.GetComponent<Il2CppFuturLab.FuturToggle>();
            var unityToggle = toggle?.Toggle;

            if (unityToggle is not null && unityToggle != null)
            {
                // isOn is a plain bool property and firing its callback is the
                // point - SetIsOnWithoutNotify would change the visual and not
                // the page.
                unityToggle.isOn = true;
                LoggerInstance.Msg($"menu tab [{tabIndex + 1}/{tabNodes.Count}] "
                    + $"{node.name} via Toggle.isOn");

                menuButtons.Clear();
                menuIndex = -1;
                menuSelectedPointer = IntPtr.Zero;
                return;
            }

            LoggerInstance.Msg($"menu tab [{tabIndex + 1}/{tabNodes.Count}] {node.name} "
                + "has neither FuturButton nor FuturToggle - not activated");
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  tab step threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    private void ToggleGameMenu()
    {
        if (playerInput is null)
            return;

        try
        {
            var pws = playerInput.TryCast<Il2CppFuturLab.PW2.PwsPlayerInput>();

            if (pws is not null)
            {
                pws.ToggleGameMenu();
                LoggerInstance.Msg("game menu toggled (PwsPlayerInput.ToggleGameMenu)");
                return;
            }

            playerInput.InvokePaused();
            LoggerInstance.Msg("game menu: fell back to InvokePaused");
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  game menu threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    // Edge-triggered off the left trigger's analog value, with the same
    // arm-and-re-arm shape the nozzle cycle and the snap turn use. No tap/hold
    // timing: this is the most-pressed action of the set and must feel instant.
    private void ReadOffHandTrigger()
    {
        if (offHandTrigger is null)
            return;

        try
        {
            var raw = offHandTrigger.ReadValueAsObject();
            var value = raw is null ? 0f : raw.Unbox<float>();

            if (value > 0.6f)
            {
                if (refillArmed)
                {
                    refillArmed = false;

                    // DER MESSBLOCK BLEIBT EINE ANFORDERUNG. Er laeuft
                    // spaeter im Frame, wenn Kopf- und Handpose dieses Frames
                    // stehen; hier waere er einen Frame alt. InteractProbe ist
                    // aus, das ist der Diagnosepfad.
                    interactProbeRequested = true;

                    // DIE VORRANGFOLGE, und sie steht genau hier statt in zwei
                    // Methoden verteilt: greift die Hand ein Objekt, ist der
                    // Druck verbraucht; sonst macht der Trigger, was er immer
                    // gemacht hat. SOFORT und nicht ueber ein Flag wie der
                    // Messblock - der Griff braucht nur die Handposition, und
                    // die steht aus ApplyPoseSource fuer diesen Frame schon
                    // bereit. Die meistgedrueckte Aktion des Sets bleibt
                    // damit sofortig.
                    if (!TryGrabInteract())
                        RotateOrRefill();
                }
            }
            else if (value < 0.3f)
            {
                refillArmed = true;
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  off-hand trigger threw {exception.GetType().Name}; "
                + "giving up on it.");
            offHandTrigger = null;
        }
    }

    private void RotateOrRefill()
    {
        if (playerInput is null)
            return;

        playerInput.InvokeRotateNozzle();
        playerInput.InvokeRecallSoap();
        LoggerInstance.Msg("nozzle: rotate + soap recall (the game picks by context)");
    }

    // RECENTRE, and it is deliberately not a development key: in VR the gun and
    // the head drift out of place for ordinary reasons and putting them back is
    // part of playing. Lifted out of LiveTrim so that gating the trim set does
    // not take it away.
    //
    // Keeps its original guard. Ctrl and Alt still suppress it, so the modifier
    // combinations on the same cross cannot recentre as a side effect - and it
    // sets no "changed" flag, so unlike the trim keys it writes nothing to the
    // config.
    private void RecenterKey()
    {
        if (AltHeld() || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
            return;

        if (Input.GetKeyDown(KeyCode.Keypad3))
        {
            recenterRequested = true;
            recenterHeadRequested = true;
            LoggerInstance.Msg("Recenter requested (gun and head position).");
        }
    }

    private void NozzleKeys()
    {
        if (playerInput is null)
            return;

        try
        {
            if (Input.GetKeyDown(KeyCode.KeypadPlus))
            {
                playerInput.InvokeSwitchNozzle(1);
                LoggerInstance.Msg("nozzle: next");
            }

            if (Input.GetKeyDown(KeyCode.KeypadPeriod))
            {
                playerInput.InvokeSwitchNozzle(-1);
                LoggerInstance.Msg("nozzle: previous");
            }

            if (Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                RotateOrRefill();
            }
        }
        catch (Exception exception)
        {
            // Dropped rather than retried: the player is rebuilt per job, and
            // DriveMovement re-resolves playerInput on its own next frame.
            playerInput = null;
            LoggerInstance.Warning($"  nozzle input threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    // PositionToFOV's lateral fudge, removed - and only that.
    //
    // The component holds the beam origin at a fixed SCREEN position as the field
    // of view changes, by rewriting NozzleAnchor(Clone).localPosition every
    // LateUpdate. Measured, not inferred: the THIRD-person anchor - same prefab,
    // same locator, but no PositionToFOV on it - reads a clean (0, 0, 0.04) in all
    // five dumps, while the first-person one reads (0.057, -0.057, 0.041) at fov 70
    // and (0.084, -0.080, 0.039) at the 79.65 XR raises it to. That is 8.1 cm
    // sideways-and-down flat and 11.6 cm in the headset - the red line starting
    // behind and beside the barrel, exactly.
    //
    // Only x and y go. z is the forward offset the NOZZLE authored and it differs
    // per nozzle - the locators sit at 0.12, 0.2, 0.4, 0.65 and 1.0 - so a
    // hard-coded muzzle constant would be wrong the moment an extension is fitted.
    // Keeping lp.z costs the 0.6 mm by which the component also perturbs z, two
    // hundred times less than the error being removed.
    //
    // Applied as a DELTA on raySpawn.position rather than as
    // locator.TransformPoint(0, 0, lp.z). The two agree only if RaySpawnPoint
    // contributes no translation of its own, and the dumps cannot show that: they
    // print no line for RaySpawnPoint, WashVfx or VfxRoot, only that the three
    // together sum to zero. The delta form does not need the assumption.
    //
    // Nothing is written to the game. The anchor keeps its screen-space position,
    // so the game's own VFX stays where the game put it; only the numbers this mod
    // computes and the line it draws move to the real muzzle.
    // DIE RICHTUNG, DIE DAS SPIEL AUCH ZUM WASCHEN BEKOMMT - und nicht die,
    // die zum Zeitpunkt der Zielsuche auf RaySpawnPoint steht.
    //
    // GEMESSENE URSACHE VON "niedrige Gegenstaende lassen sich nicht
    // aufnehmen", und es ist eine Reihenfolge im Frame. In OnLateUpdate
    // lesen DriveButtons (ueber TryAimPickup) und ReportInteractionAim die
    // Duesenrichtung, und ERST DREIZEHN AUFRUFE SPAETER zwingt DriveRay
    // raySpawn.localRotation auf Identitaet und gibt dem Spiel mit
    // GameInput.RayDirection den korrigierten Wert. Die Zielsuche bekam
    // also die kamerabasierte Rotation, die das Spiel im Render-Callback
    // des Vorframes hineingeschrieben hatte.
    //
    // DAS ERKLAERT DEN GANZEN BEFUNDSATZ: eine Zielneigung, die dem KOPF
    // folgte statt der Hand, waehrend Waschen und Laser aus demselben
    // Transform die richtige Richtung bezogen - UpdateLaser wird aus
    // DriveRay gerufen, also nach dem Write. Der Menuezeiger liest ebenfalls
    // vorher, hat aber einen sichtbaren Cursor und wird per Auge korrigiert;
    // deshalb ist er hier nicht mit umgestellt.
    //
    // DER ELTERNKNOTEN STATT EINER UMSORTIERUNG. DriveButtons hinter
    // DriveRay zu schieben wuerde am Frame-Aufbau der ganzen Datei ruehren.
    // NozzleAnchor(Clone) traegt dagegen localEuler (0,0,0) - in allen fuenf
    // Hierarchie-Dumps - womit sein forward genau das ist, was
    // raySpawn.forward nach dem Identity-Write ist: gleicher Frame, keine
    // Verzoegerung, und unabhaengig davon, ob dieser Write das Rennen gegen
    // PlayerCameraPreRender gewinnt. Die Kettenspalten locFwd und asmFwd
    // pruefen diese Annahme im Log, statt sie zu glauben.
    //
    // Faellt der Elternknoten fuer einen Frame weg - ein Duesenwechsel
    // zerstoert den Clone - bleibt spawn.forward die Rueckfallebene, genau
    // wie bei MuzzlePoint darunter.
    private Vector3 AimForward(Transform spawn)
    {
        var anchor = spawn.parent;              // NozzleAnchor(Clone)

        return anchor is null || anchor == null ? spawn.forward : anchor.forward;
    }

    private Vector3 MuzzlePoint(Transform spawn)
    {
        var anchor = spawn.parent;              // NozzleAnchor(Clone)
        var locator = anchor?.parent;           // the active Locator_Nozzle_*

        // Either can go missing for a frame: switching the nozzle destroys the
        // anchor clone and ResolveRaySpawn only re-searches twice a second, so a
        // detached RaySpawnPoint can outlive its parents. Falling back to the
        // uncorrected point keeps the laser drawn - a line 8 cm off is still a
        // usable instrument, a line that vanishes is not.
        if (anchor is null || anchor == null || locator is null || locator == null)
            return spawn.position;

        var lp = anchor.localPosition;

        return spawn.position + locator.TransformVector(new Vector3(-lp.x, -lp.y, 0f));
    }

    private Il2CppFuturLab.PW2.PlayerCameraController? cameraController;
    private string lookStateStatus = "look: untouched";
    private bool loggedLookNodes;

    // THE GAME'S OWN LOOK ANGLES, which this mod has never written.
    //
    // This is the explanation the five previous levers were missing, and the
    // measurement points straight at it. With RaySpawnPoint verified clamped to
    // identity - spawnLocal euler (-0, 0, 0) in the log, not assumed - the
    // effective direction was still wrong by:
    //
    //     errUp -5.5 deg   camPitch  -8.2 deg
    //     errUp -22.0 deg  camPitch -22.5 deg
    //
    // A ratio of one to one. Not a reprojection, not a rotated transform: a term
    // that IS the camera pitch. And PlayerCameraController keeps the look angles
    // as plain floats, VerticalLookRotation and HorizontalLookRotation, beside
    // the transforms. The flat game moves both together - the look input updates
    // the float AND turns the transform. This mod turns the transform from the
    // HMD and never touches the float, so the float sits wherever the flat game
    // left it while the camera is pitched. Any computation built on the float is
    // then wrong by exactly the HMD pitch, which is what was measured.
    //
    // Written as a plain property setter, the shape that carried MovementRaw.
    // The readback says whether the game keeps the value, and the sign is the
    // one thing not derivable: if it is inverted, errUp DOUBLES instead of
    // vanishing, which is unmistakable rather than subtle. Bit 1024 writes the
    // negation so one run settles it either way.
    private void DriveLookState()
    {
        var writePitch = (aimSkip.Value & 512) != 0;
        var writeNegated = (aimSkip.Value & 1024) != 0;

        if (!writePitch && !writeNegated)
        {
            lookStateStatus = "look: not written";
            return;
        }

        try
        {
            if (cameraController is null || cameraController == null)
            {
                cameraController = UnityEngine.Object
                    .FindObjectOfType<Il2CppFuturLab.PW2.PlayerCameraController>();

                if (cameraController is null)
                {
                    lookStateStatus = "look: no PlayerCameraController";
                    return;
                }
            }

            var camera = Camera.main;
            if (camera is null)
                return;

            // Unity reports pitch as 0..360 with positive meaning looking DOWN.
            // Wrapped to a signed angle first, because feeding 337 into a look
            // angle would be nonsense rather than a sign error.
            var pitch = camera.transform.eulerAngles.x;
            if (pitch > 180f)
                pitch -= 360f;

            var wanted = writeNegated ? -pitch : pitch;

            var before = cameraController.VerticalLookRotation;
            cameraController.VerticalLookRotation = wanted;
            var after = cameraController.VerticalLookRotation;

            // THE TRANSFORMS BESIDE THE FLOATS, and the reason the float write
            // changed nothing.
            //
            // Measured with mask 897 live: the readback reads "was -5.3 -> wrote
            // -5.3", so VerticalLookRotation ALREADY equalled the camera pitch.
            // It was never stale, and errUp stayed locked to camPitch one to one
            // across 131 samples and both signs. So the float is not the carrier.
            //
            // But PlayerCameraController also owns m_horizontalLook and
            // m_verticalLook as TRANSFORMS. This mod writes the HMD yaw to
            // HeadTurn and the HMD pitch to PlayerCamera, chosen from the
            // section 35 measurement of the flat look system. If m_verticalLook
            // is a DIFFERENT node than the one being written, it stays level
            // while the camera pitches - and any direction built from it is off
            // by exactly the camera pitch, which is the measurement.
            //
            // Logged by NAME first. Naming the two nodes costs nothing and
            // decides whether the pitch is going to the wrong transform, which
            // is a question five levers' worth of guessing never asked.
            if (!loggedLookNodes)
            {
                loggedLookNodes = true;

                var horizontal = cameraController.m_horizontalLook;
                var vertical = cameraController.m_verticalLook;

                LoggerInstance.Msg($"  look nodes: horizontal "
                    + $"\"{(horizontal is null ? "null" : horizontal.name)}\"   "
                    + $"vertical \"{(vertical is null ? "null" : vertical.name)}\"");
                LoggerInstance.Msg($"  this mod writes yaw to \"{(headTurn is null ? "null" : headTurn.name)}\" "
                    + $"and pitch to \"{(anchor?.parent is null ? "null" : anchor.parent.name)}\"");

                if (vertical is not null)
                    LoggerInstance.Msg($"  vertical node local euler {Vector(vertical.localEulerAngles)}");
            }

            var verticalNode = cameraController.m_verticalLook;
            var nodeEuler = verticalNode is null ? Vector3.zero : verticalNode.localEulerAngles;

            lookStateStatus = $"look pitch {pitch:0.#} -> wrote {wanted:0.#}  "
                + $"was {before:0.#}  now {after:0.#}"
                + (Mathf.Abs(after - wanted) > 0.5f ? "  REVERTED" : "")
                + $"  vNode {(verticalNode is null ? "null" : verticalNode.name)} "
                + $"euler {nodeEuler.x:0.#}";
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  look state threw {exception.GetType().Name}; leaving it alone.");
            cameraController = null;
            lookStateStatus = "look: failed";
        }
    }

    private string SourceName() => useAimPose.Value ? "aim" : "grip";

    // Curated combinations, not all sixteen. The field result decides the order:
    // 1 and 2 each reduced the gaze pull on their own, so 1+2 is the first thing
    // to try and sits third in the cycle rather than last. 4 and 8 alone were
    // reported as no better than doing nothing, so they appear only inside the
    // wider combinations.
    // 5009 sits DIRECTLY AFTER 913, the working state, so one press of the skip
    // key reaches it. 5009 is 913 plus bit 4096: everything that works, plus
    // FOVCorrectPosition substituted with identity. That is this run's
    // measurement, and putting it one press away means it can be A/B'd against
    // the working state in the headset without hunting through the cycle.
    // 33681 LEADS, because it is the working state as of this measurement: 913
    // plus bit 32768, which writes the global _LockFOV to zero.
    //
    // That single write closed the muzzle offset the project chased for a whole
    // session. _LockFOV is a SWITCH, not an angle - writing the live camera
    // field of view, 79.646, was merely another non-zero "on" and proved nothing,
    // while zero turned the vertex-shader lock off and put the jet on the
    // muzzle. The shader property table settled the family: _ToggleFOV is a
    // Float whose DEFAULT is 0 and which runs at 1.
    //
    // 913 is kept directly behind it as the control, so one press restores the
    // old displaced rendering for comparison.
    private static readonly int[] SkipPresets = { 230289, 99217, 33681, 913, 17297, 9105, 2961, 5009, 897, 385, 1, 0 };

    private static readonly Color[] SkipColors =
    {
        // Aligned one-to-one with SkipPresets. The colour is the only readable
        // channel in the headset - the overlay sticks to the face - so it has to
        // say which preset is live.
        new(0.55f, 1f, 0.55f, 1f),    // mint   230289  THE WORKING STATE, + reticle clamp
        new(1f, 1f, 0.35f, 1f),       // pale    99217  control: reticle left world-aligned
        new(0.1f, 0.9f, 0.9f, 1f),    // teal    33681  control: anchor left displaced
        new(0.1f, 1f, 0.15f, 1f),     // green     913  control: shader lock back on
        new(0.6f, 0.3f, 1f, 1f),      // violet  17297  _LockFOV -> camera FOV, proved nothing
        new(1f, 0.55f, 0f, 1f),       // orange   9105  + ray-origin offset
        new(1f, 0.05f, 0.05f, 1f),    // red      2961  + shader FOV lock off
        new(1f, 0.35f, 0.9f, 1f),     // magenta  5009  + FOVCorrectPosition identity
        new(0.2f, 0.45f, 1f, 1f),     // blue      897  PositionToFOV still on
        new(0.1f, 0.95f, 0.95f, 1f),  // cyan      385  no look write
        new(1f, 0.9f, 0.1f, 1f),      // yellow      1  decoupling only
        new(1f, 1f, 1f, 1f),          // white       0  CONTROL, nothing suppressed
    };

    // EVERY preset keeps bit 1 set except the last, and that is the whole point
    // of this ordering.
    //
    // Bit 1 suppresses SetWashDirection, which is what stops the game re-aiming
    // RaySpawnPoint at the gaze. Measured: without it the nozzle sits at a median
    // 0.0 deg from the CAMERA; with it, at a constant 2.3 to 2.6 deg from the
    // GUN. So bit 1 is not one experiment among several - it is the working
    // decoupling, and switching it off to take a measurement hands the user back
    // the original bug in full. A measurement run was set to mask 0 for exactly
    // that reason and cost a session.
    //
    // Mask 0 is kept, last, as the control. White, so it cannot be mistaken for
    // one of the working states.
    private static readonly string[] SkipNames =
    {
        // Aligned one-to-one with SkipPresets above. Bit 1 decouples, 16 disables
        // PositionToFOV, 128 clamps RaySpawnPoint, 256 overwrites the job's own
        // Direction - the only lever the measurement leaves standing.
        // Aligned one-to-one with SkipPresets. The two leading presets differ
        // ONLY in the sign of the look-pitch write, because that sign is the one
        // thing the structure cannot tell us - and a wrong sign DOUBLES the
        // error instead of removing it, which is unmistakable in one sweep.
        // 913 adds bit 16, PositionToFOV off, to the working 897. Measured
        // reason: the displacement that component writes onto the ray origin
        // varies with head pitch from 0.083 m to 0.562 m, because it holds a
        // SCREEN position and the gun's screen position changes as the head
        // tilts. The visible jet hangs under that origin and is dragged with it.
        // The effect zone is unaffected either way - it comes from the job write.
        // REALIGNED. This array had SEVEN entries against six presets, so from
        // index 4 every label named the wrong mask - preset 1 was logged as
        // "513: look pitch written ALONE". A mislabelled mask in the log is the
        // precise mechanism behind the three misdiagnoses of section 72, so the
        // arrays are now counted rather than assumed: SkipPresets, SkipColors,
        // SkipColorNames and SkipNames all hold SEVEN.
        "230289: THE WORKING STATE - + WasherReticles held at identity",
        "99217: CONTROL - reticle left world-aligned, the crosshair twists",
        "33681: CONTROL - anchor left displaced, jet offset from laser until a nozzle swap",
        "913: CONTROL - shader lock back on, the displaced rendering returns",
        "17297: _LockFOV set to the camera FOV - a non-zero 'on', proved nothing",
        "9105: + RayOriginOffset clamped on RaySpawnPoint - the CALIBRATION",
        "2961: + shader FOV lock OFF - measured, reached the materials, changed nothing",
        "5009: + FOVCorrectPosition as identity - measured, changed nothing",
        "897: PositionToFOV still on too",
        "385: no look write, clamp + job write only",
        "1: baseline, decoupling only",
        "0: CONTROL, nothing suppressed",
    };

    private static readonly string[] SkipColorNames =
        { "mint", "pale yellow", "teal", "green", "violet", "orange", "red", "magenta", "blue", "cyan", "yellow", "white" };

    // Derived from the mask rather than stored separately, so a hand-edited
    // config value still lands somewhere sane instead of desynchronising the
    // colour from what is actually suppressed.
    private int SkipIndex()
    {
        for (var index = 0; index < SkipPresets.Length; index++)
        {
            if (SkipPresets[index] == aimSkip.Value)
                return index;
        }

        return 0;
    }

    private string AimSkipName() => SkipNames[SkipIndex()];

    private string LaserColorName() => SkipColorNames[SkipIndex()];

    private static string NextLaserDirection(string current) =>
        string.Equals(current, "wash", StringComparison.OrdinalIgnoreCase) ? "nozzle"
        : string.Equals(current, "nozzle", StringComparison.OrdinalIgnoreCase) ? "camera"
        : "wash";

    // The laser starts at the NOZZLE, never at the game's WashingSource. If that
    // source turns out to be the camera position, a line drawn from it would
    // begin at the eye, where an angular error is nearly unreadable; from the
    // barrel tip the same error is an angle right where the water leaves. The
    // measured WashingSource is logged beside it, so the difference between the
    // two origins is still on record.
    private void UpdateLaser(Vector3 muzzle)
    {
        if (!showLaser.Value)
        {
            washLaser.Hide();
            laserStatus = $"laser off ({laserKey.Value})";
            return;
        }

        if (raySpawn is null)
        {
            washLaser.Hide();
            laserStatus = "laser: no nozzle";
            return;
        }

        var mode = laserDirection.Value;
        Vector3? direction;

        if (string.Equals(mode, "nozzle", StringComparison.OrdinalIgnoreCase))
            direction = raySpawn.forward;
        else if (string.Equals(mode, "camera", StringComparison.OrdinalIgnoreCase))
            direction = Camera.main?.transform.forward;
        else
            direction = washProbe.WashDirection;

        laserStatus = washLaser.Draw(LoggerInstance, muzzle, direction,
            $"{mode}/{SkipIndex()}", laserLength.Value, laserWidth.Value,
            SkipColors[SkipIndex()], laserAlwaysOnTop.Value);
    }

    private string HeadPositionText()
    {
        try
        {
            var value = headPositionAction?.ReadValueAsObject();
            return value is null ? "none" : Vector(value.Unbox<Vector3>());
        }
        catch (Exception exception)
        {
            return exception.GetType().Name;
        }
    }

    // Deckelt einen Entwicklungsschalter mit DevMode. Der gespeicherte Wert
    // bleibt unberuehrt - nur seine Wirkung haengt am Hauptschalter.
    private bool Dev(MelonPreferences_Entry<bool> entry) =>
        devMode.Value && entry.Value;

    private bool UseAssembly() =>
        !string.Equals(positionTarget.Value, "anchor", StringComparison.OrdinalIgnoreCase);

    private string TargetName() => UseAssembly()
        ? (assembly is null ? "assembly MISSING" : "assembly")
        : "anchor";

    private void ApplyPoseSource()
    {
        var aim = useAimPose.Value && aimRotation is not null;

        // Rotation keeps the old rule untouched: the aim pose points down the
        // barrel by definition, which is what a washer wants.
        rotationAction = aim ? aimRotation : gripRotation;

        // Position no longer follows it. Each branch falls back to the coupled
        // choice when the action it wants was never bound, so a runtime that
        // publishes only one of the two poses degrades instead of reading null.
        var wanted = positionSource.Value;

        if (string.Equals(wanted, "grip", StringComparison.OrdinalIgnoreCase)
            && gripPosition is not null)
            positionAction = gripPosition;
        else if (string.Equals(wanted, "aim", StringComparison.OrdinalIgnoreCase)
            && aimPosition is not null)
            positionAction = aimPosition;
        else
            positionAction = aim ? aimPosition : gripPosition;
    }

    private static string NextPositionSource(string current) =>
        string.Equals(current, "grip", StringComparison.OrdinalIgnoreCase) ? "aim"
        : string.Equals(current, "aim", StringComparison.OrdinalIgnoreCase) ? "follow"
        : "grip";

    // Reports what is LIVE, not what was configured. A configured "grip" that
    // fell back to the aim action because the grip pose never bound would
    // otherwise read as a working grip pose - and this project has already paid
    // for one instrument that lied about being live (section 58, and the first
    // WashLaser).
    private string PositionSourceName()
    {
        var name = positionSource.Value;

        var live = positionAction is null ? "none"
            : ReferenceEquals(positionAction, gripPosition) ? "grip"
            : ReferenceEquals(positionAction, aimPosition) ? "aim"
            : "other";

        return string.Equals(name, live, StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{name}->{live}";
    }

    // Rotation trim on the numeric keypad, so it can be found by touch with the
    // headset on. The keypad layout is the mnemonic: 8/2 tilt, 4/6 turn, 7/9
    // roll, 5 back to zero, 0 toggles whether position is written at all. Every
    // change is saved immediately, so a value dialled in inside the headset
    // survives the next launch and can be read back out of the config file.
    private void LiveTrim()
    {
        const float degrees = 5f;
        const float metres = 0.02f;

        // Five millimetres, a quarter of the position step. The user's word for
        // this task is "perfektionieren", and aligning a grip to a palm is a
        // finer job than centring a tracking origin: 2 cm per press overshoots
        // the whole adjustment range in three keystrokes.
        const float gripMetres = 0.005f;
        var changed = false;

        // Shift turns the same keypad cross into position trim. One key set,
        // three meanings, and all reachable by touch with the headset on.
        var shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        // Ctrl is the third. The full keypad was already spoken for - 0 to 9
        // here, divide/multiply/minus on the laser and the aim mask, plus/
        // period/enter on the nozzle fallbacks - and the F-keys are gone down to
        // F12, with F7 belonging to UnityExplorer. A modifier is the only way to
        // add three axes without evicting something, and evicting a key blind
        // has already cost this project two runs.
        //
        // Checked FIRST and exclusively. Without the else-if chain, ctrl+Keypad8
        // would fall through into the rotation branch and silently trim pitch
        // while the user believed they were moving the grip.
        var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        // Alt is the UI set, checked before the others for the same reason ctrl
        // is: a modifier branch that falls through would silently trim something
        // else.
        var alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

        if (alt)
        {
            if (Input.GetKeyDown(KeyCode.Keypad8)) { uiScale.Value += 0.05f; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad2)) { uiScale.Value -= 0.05f; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad4)) { uiDistance.Value -= 0.1f; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad6)) { uiDistance.Value += 0.1f; changed = true; }

            if (Input.GetKeyDown(KeyCode.Keypad5))
            {
                uiScale.Value = 0.5f;
                uiDistance.Value = 2f;
                changed = true;
            }

            // Clamped: a zero or negative scale collapses the UI to a point that
            // cannot be dialled back out of inside the headset, and a plane
            // nearer than the near clip vanishes entirely.
            uiScale.Value = Mathf.Clamp(uiScale.Value, 0.1f, 2f);
            uiDistance.Value = Mathf.Clamp(uiDistance.Value, 0.3f, 10f);

            if (changed)
                LoggerInstance.Msg($"ui: scale {uiScale.Value:0.##}  distance {uiDistance.Value:0.##} m");

            // PHASE 0 of the menu pointer. Neither of these sets changed: they
            // write no preference, so the save below must not fire for them.
            if (Input.GetKeyDown(KeyCode.Keypad7))
                ReportCursorModule("alt+num7");

            if (Input.GetKeyDown(KeyCode.Keypad9))
                ToggleNativeCursor();
        }
        else
        // Ctrl+Shift is the fourth set, and it moves the RAY ORIGIN rather than
        // the gun. Checked before the ctrl-only branch, because ctrl is held in
        // both and a plain "if (ctrl)" first would swallow it.
        //
        // The two are opposite ends of the same gap: ctrl moves the washer to
        // the jet, ctrl+shift moves the jet to the washer. Only the second keeps
        // the grip in the hand, so it is the one to reach for - but both are
        // here because which one is correct depends on a cause that is still
        // unidentified.
        if (ctrl && shift)
        {
            if (Input.GetKeyDown(KeyCode.Keypad8)) { rayOriginY.Value += gripMetres; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad2)) { rayOriginY.Value -= gripMetres; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad4)) { rayOriginX.Value -= gripMetres; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad6)) { rayOriginX.Value += gripMetres; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad7)) { rayOriginZ.Value -= gripMetres; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad9)) { rayOriginZ.Value += gripMetres; changed = true; }

            if (Input.GetKeyDown(KeyCode.Keypad5))
            {
                rayOriginX.Value = 0f;
                rayOriginY.Value = 0f;
                rayOriginZ.Value = 0f;
                changed = true;
            }

            if (changed)
                LoggerInstance.Msg("ray origin "
                    + $"{Vector(new Vector3(rayOriginX.Value, rayOriginY.Value, rayOriginZ.Value))}"
                    + $"   bit 8192 {((aimSkip.Value & 8192) != 0 ? "SET" : "clear - no effect yet")}");
        }
        else if (ctrl)
        {
            // The gun's own frame: 8/2 raise and lower it along its own up, 4/6
            // move it across its own right, 7/9 slide it along the barrel. Same
            // keypad mnemonic as the other two sets, so the muscle memory
            // carries over.
            if (Input.GetKeyDown(KeyCode.Keypad8)) { gripY.Value += gripMetres; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad2)) { gripY.Value -= gripMetres; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad4)) { gripX.Value -= gripMetres; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad6)) { gripX.Value += gripMetres; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad7)) { gripZ.Value -= gripMetres; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad9)) { gripZ.Value += gripMetres; changed = true; }

            if (Input.GetKeyDown(KeyCode.Keypad5))
            {
                gripX.Value = 0f;
                gripY.Value = 0f;
                gripZ.Value = 0f;
                changed = true;
            }

            // The A/B switch for the finding this version is built on, in the
            // headset rather than across two launches. Whether the grip pose's
            // origin really sits at the palm in THIS runtime is a claim from the
            // OpenXR spec, not a measurement - so it has to be falsifiable
            // without taking the headset off.
            // Ctrl+Num1. Num1 alone is the world/local switch and is gated on
            // !ctrl, so this is free.
            if (Input.GetKeyDown(KeyCode.Keypad1))
            {
                placeVerb.Value = NextPlaceVerb(placeVerb.Value);
                changed = true;
                LoggerInstance.Msg($"place verb: {placeVerb.Value}");
            }

            if (Input.GetKeyDown(KeyCode.Keypad0))
            {
                positionSource.Value = NextPositionSource(positionSource.Value);
                ApplyPoseSource();

                // The reference pose is tied to the action that captured it, and
                // the two poses have different origins - that is the entire
                // point. Without a recenter the local-space path would jump by
                // the distance between them on every switch.
                recenterRequested = true;
                changed = true;
                LoggerInstance.Msg($"Position source: {PositionSourceName()}");
            }
        }
        else if (shift)
        {
            if (Input.GetKeyDown(KeyCode.Keypad8)) { offsetY.Value += metres; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad2)) { offsetY.Value -= metres; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad4)) { offsetX.Value -= metres; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad6)) { offsetX.Value += metres; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad7)) { offsetZ.Value -= metres; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad9)) { offsetZ.Value += metres; changed = true; }

            if (Input.GetKeyDown(KeyCode.Keypad5))
            {
                offsetX.Value = 0f;
                offsetY.Value = 0f;
                offsetZ.Value = 0f;
                changed = true;
            }
        }
        else
        {
            if (Input.GetKeyDown(KeyCode.Keypad8)) { pitchOffset.Value -= degrees; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad2)) { pitchOffset.Value += degrees; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad4)) { yawOffset.Value -= degrees; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad6)) { yawOffset.Value += degrees; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad7)) { rollOffset.Value -= degrees; changed = true; }
            if (Input.GetKeyDown(KeyCode.Keypad9)) { rollOffset.Value += degrees; changed = true; }

            if (Input.GetKeyDown(KeyCode.Keypad5))
            {
                pitchOffset.Value = 0f;
                yawOffset.Value = 0f;
                rollOffset.Value = 0f;
                changed = true;
            }
        }

        // All three of these are gated on !ctrl. Keypad0 in particular: ctrl+0
        // is the position-source switch above, and without the gate one press
        // would ALSO toggle position driving off - the washer would freeze and
        // the source change would look like it had broken the tracking.
        if (!ctrl && !alt && Input.GetKeyDown(KeyCode.Keypad0))
        {
            drivePosition.Value = !drivePosition.Value;

            // Switching position back on always recenters. Without this the old
            // reference is still in force and the washer jumps by however far the
            // hand has travelled since F2 - the half metre reported for 0.23.0.
            if (drivePosition.Value)
                recenterRequested = true;
            else if (assembly is not null && assemblyRest.HasValue)
                assembly.localPosition = assemblyRest.Value;

            changed = true;
        }



        // Swapping the target mid-run also restores the transform being left
        // behind, so the abandoned one does not stay frozen at the last value.
        // Was the assembly/anchor target cycle. World space always writes the
        // assembly, so the useful switch here is now world versus local.
        if (!ctrl && !alt && Input.GetKeyDown(KeyCode.Keypad1))
        {
            worldSpace.Value = !worldSpace.Value;
            anchorPositionWritten = false;
            recenterRequested = true;
            changed = true;
        }

        if (!changed)
            return;

        MelonPreferences.Save();
        LoggerInstance.Msg($"trim pitch {pitchOffset.Value}, yaw {yawOffset.Value}, "
            + $"roll {rollOffset.Value}, position {drivePosition.Value}, "
            + $"grip {Vector(new Vector3(gripX.Value, gripY.Value, gripZ.Value))}, "
            + $"src {PositionSourceName()}");
    }
}
