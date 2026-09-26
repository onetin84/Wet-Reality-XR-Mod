using MelonLoader;
using UnityEngine;

namespace WetReality;

// DIE WASHER-WAEHLSCHEIBE DES SPIELS IN VR - Abschnitt 205.
//
// Gemessen in Abschnitt 204 (Pose 1.115.0): InvokeSwitchGun, der Druck,
// wechselt nur die MARKE. Eine zweite Stufe derselben Marke (PVMedium neben
// PVLight) erreichte R3 halten nie, nur SwitchToNextAvailablePowerWasherInGroup
// und die Waehlscheibe. Die Waehlscheibe oeffnet ueber
// PwsPlayerInput.HandleEquipmentHeld(PowerWasher), ist im Headset gut
// sichtbar, InvokeNavigateSubMenu(+1/-1) blaettert die Stufe und
// HandleEquipmentReleased uebernimmt die Auswahl. Das Spiel kuemmert sich damit
// selbst um Besitz, Duesen, Verlaengerung und Koop - die Mod setzt keine
// Ausruestung.
//
// DER STICK ZIELTE NICHT, und das ist die eine Stelle, die hier neu ist.
// RadialMenuUI.UpdateInput(Vector2, RadialMenuInputMode) kennt Keyboard=0,
// ConsoleController=1, VRController=2. Ohne angemeldetes Gamepad steht
// RadialMenuState.m_inputMode auf Keyboard, und ein Stickwert von hoechstens 1
// ist dann eine Zeigerposition eine Einheit neben der Mitte: kein Segment,
// das Highlight verschwindet - genau das gemeldete Bild. Die Mod stellt den
// Modus darum auf ConsoleController, solange die Scheibe offen ist. DAS IST
// DIE UNGEMESSENE HAELFTE: ob das Spiel den Modus pro Frame zuruecksetzt,
// steht in der Sekundenzeile ("mode before write").
//
// GEMESSEN in 1.117.0 (Abschnitt 207): es setzt ihn zurueck - "mode before
// write Keyboard" in jeder Sekundenzeile, das Highlight blieb auf dem
// ausgeruesteten Washer. Seit 1.118.0 ruft die Mod darum
// RadialMenuUI.UpdateInput(stick, ConsoleController) SELBST, jedes Frame mit
// ausgelenktem Stick. Das laeuft in OnLateUpdate, also NACH dem Update des
// Spiels: das Bild dieses Frames zeigt unseren Stand, und die Uebernahme im
// selben Frame liest ihn. RadialWheelNavigateRaw bleibt auf null - den
// Tastaturweg des Spiels mit einem Stickwert zu fuettern hat in 1.115.0 das
// Highlight geloescht, und zwei Schreiber je Frame liessen Hover und
// Hover-Ton flackern.
//
// BELEGUNG seit 1.121.0 - Abschnitt 210, auf Wunsch alles am Zeiger:
//   R3 halten              oeffnet die Washer-Scheibe
//   Zeigestrahl + Washer-Trigger
//                          waehlt, was unter dem Strahl liegt: ein Segment
//                          (die Marke) oder eine Stufen-Kachel. Der Strahl
//                          allein waehlt nichts (HoverSelects).
//   linker / rechter Griff Scheibe zurueck / vor
//   R3                     uebernimmt und schliesst
// Fuer das Stickspiel bleibt alles ohne Zeiger erreichbar:
//   rechter Stick          zielt auf die Marke
//   linker Stick l/r       Stufe zurueck / vor, ein Schritt je Kippen
//   A                      uebernimmt und schliesst
// Der freie Trigger bleibt gesperrt, solange die Scheibe offen ist oder
// wechselt. Bis 1.120.1 lagen die Stufe auf den Griffen und der
// Scheibenwechsel auf den Triggern.
//
// DREI SCHEIBEN - Abschnitt 208. Das Spiel hat Washer, Duese und
// Verlaengerung als getrennte Scheiben, jede mit eigener Halte-Taste. Ein
// Trigger uebernimmt die Wahl der offenen Scheibe - so wie das Loslassen im
// Flat-Spiel - und oeffnet die naechste im Kreis Washer -> Duese ->
// Verlaengerung.
//
// GEMESSEN in 1.119.0: HandleEquipmentHeld im SELBEN Frame wie
// HandleEquipmentReleased oeffnete nichts, der Nachversuch einen Frame spaeter
// auch nicht - und danach oeffnete KEINE Scheibe mehr, auch R3 halten nicht,
// bis zum Neustart. Vermutung: das Schliessen ist ein Zustandswechsel, der
// noch lief, und der Gameplay-Zustand kam nie wieder als Empfaenger von
// OpenRadialMenu an. Seit 1.119.1 kommt die neue Scheibe darum erst, wenn
// CurrentState kein RadialMenuState mehr ist und SwitchSettleFrames vergangen
// sind, und nur EIN Aufruf. Jede Wechsel- und R3-Zeile traegt den Befund
// (Diagnose): ob OpenRadialMenu einen Empfaenger hat, BlockedInput und den
// Zustandstyp.
//
// DER ZEIGESTRAHL - Abschnitt 209. Die Scheibe waehlt ein Segment allein aus
// einer Richtung ab ihrer Mitte; genau die fuettert der Stick schon. Der Strahl
// wird darum mit der Ebene von m_entriesRoot geschnitten, der Treffer in deren
// lokale Koordinaten gebracht, und die Richtung von der Mitte dorthin geht an
// denselben UpdateInput. Mitte und Massstab kommen aus den SYMBOLEN der
// Eintraege (m_entryIconImage) - Mittelwert und mittlerer Abstand.
//
// GEMESSEN in 1.120.0: die Eintraege selbst liegen ALLE auf (0, 0), dauerhaft,
// nicht nur beim Oeffnen. Die Segmente zeichnet ein Material ueber die ganze
// Flaeche; SetPositionFromIndex setzt nur das Symbol. Liegen auch die Symbole
// auf einem Punkt, gelten Ursprung von m_entriesRoot und EntryRadius der
// Konfiguration (250). Die Stufen-Kacheln der Washer-Scheibe sind ein
// waagrechter Streifen x -288..288, y -72..72 - breiter als jeder innere Kreis.
// Darum schreibt der Strahl nichts, solange er auf einer Kachel liegt oder
// innerhalb von CentreZone der Eintragsentfernung: dort soll die Marke stehen
// bleiben, waehrend man die Stufe anzielt. EIN Schreiber je Frame: ist der
// Stick ausgelenkt, gewinnt er, sonst der Strahl.
//
// GEMESSEN in 1.120.1: die Symbole liegen auf einem Kreis mit Radius 475 um
// den Ursprung von m_entriesRoot (nicht auf EntryRadius 250 der
// Konfiguration); Washer 3 Segmente bei 90/-30/-150 Grad, Duese und
// Verlaengerung 4 bei 90/0/-90/180. Die Richtung des Strahls trifft dasselbe
// Segment wie derselbe Stickausschlag, und die Scheibe liegt genau am Ende des
// Strahls. Der Ring hatte keinen Aussenrand - r 2,54 steuerte noch -, seit
// 1.121.0 endet er bei MaxReach.
//
// DIE STUFE PER TRIGGER - Abschnitt 210. Die Kachel unter dem Strahl wird in
// m_subOptions des Karussells gesucht (Objektgleichheit, nicht die Reihenfolge
// von GetComponentsInChildren), und die Mod blaettert mit dem gemessenen
// InvokeNavigateSubMenu dorthin: ein Schritt, dann warten, bis m_selectedIndex
// sich bewegt hat, dann der naechste. UNGEMESSEN in 1.121.0, darum die Zeilen
// "tier walk": ob m_selectedIndex im selben Frame nachzieht, ob gesperrte
// Stufen uebersprungen werden ("passed over") und ob ein Schritt je Frame
// Klang und Bild sauber haelt.
// Die Scheibe bleibt nach dem Loslassen von R3 offen: R3 gedrueckt halten und
// denselben Stick kippen ist am Touch-Controller unbequem.
//
// ERKANNT WIRD DIE SCHEIBE AM ZUSTAND DES SPIELS, nicht an der eigenen
// Oeffnung: CurrentState ist dann ein RadialMenuState. Schliesst sie auf
// einem anderen Weg, faellt die Steuerung im selben Frame heraus.
// Der Strahl des Menuezeigers, wie DriveMenuPointer ihn zuletzt gerechnet
// hat. BeamLength ist die Strecke bis zur Cursor-Ebene, auf der der Strahl
// endet - neben der Strecke bis zur Scheibe zeigt sie, ob beide zusammenfallen.
internal readonly record struct WheelPointer(bool Valid, Vector3 Origin, Vector3 Forward,
    float BeamLength);

internal sealed class WasherWheel
{
    private readonly ButtonEdge accept = new();
    private readonly ButtonEdge stickClick = new();
    private readonly ButtonEdge select = new();
    private readonly ButtonEdge previousWheel = new();
    private readonly ButtonEdge nextWheel = new();

    private const float AimDeadzone = 0.35f;

    // Innerer Kreis, als Anteil der Eintragsentfernung, und der Rand um jede
    // Stufen-Kachel, in Einheiten von m_entriesRoot. Abschnitt 209.
    private const float CentreZone = 0.35f;
    private const float TileMargin = 8f;

    // Aussenrand des Rings, als Anteil der Eintragsentfernung: die Symbole
    // liegen auf 1, das Segment endet sichtbar kurz dahinter. Abschnitt 210.
    private const float MaxReach = 1.6f;

    // Waehlt schon das Zielen ein Segment? Seit 1.121.0 nein - der Trigger
    // waehlt, sonst verstellte jeder Weg zur Stufen-Kachel die Marke.
    private const bool HoverSelects = false;

    // Linker Stick als Stufe: Ausschlag zum Ausloesen, Mitte zum Wiederladen.
    private const float TierFlickIn = 0.6f;
    private const float TierFlickOut = 0.3f;

    // Das Blaettern zur angezielten Stufe. Abschnitt 210.
    private const int MaxTierSteps = 8;
    private const float TierTimeout = 1f;
    private const int TierStepWaitFrames = 5;
    private const float FallbackEntryRadius = 250f;
    private const float SwitchTimeout = 1f;
    private const int SwitchSettleFrames = 2;

    private static readonly Il2CppFuturLab.PW2.EquipmentType[] Wheels =
    {
        Il2CppFuturLab.PW2.EquipmentType.PowerWasher,
        Il2CppFuturLab.PW2.EquipmentType.Nozzle,
        Il2CppFuturLab.PW2.EquipmentType.Extension,
    };

    // Die Scheibe, die ein Trigger angefordert hat, bis sie offen ist oder
    // SwitchTimeout verstrichen ist.
    private Il2CppFuturLab.PW2.EquipmentType? pendingWheel;
    private float pendingSince;
    private int pendingFrame;
    private int closedFrame;
    private bool pendingRequested;

    private Il2CppFuturLab.PW2.RadialMenuState? state;
    private Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuUI? ui;
    private int loggedSegment = int.MinValue;
    private bool wasOpen;
    private bool loggedMode;
    private float nextReport;

    // Die Geometrie fuer den Zeigestrahl, je Oeffnung neu. Abschnitt 209.
    private Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>? corners;
    private Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuEntry>? entries;
    private Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuSubOption>? tiles;
    private readonly List<Vector2> entryPoints = new();
    private RectTransform? root;
    private Vector2 centre;
    private float entryRadius;
    private float loggedRadius = -1f;
    private string geometrySource = "";
    private string loggedSource = "";
    private float beamLength = -1f;
    private bool loggedEntries;

    // Das laufende Blaettern zu einer Stufen-Kachel; tierTarget -1 = keins.
    private int tierTarget = -1;
    private int tierDirection;
    private int tierSteps;
    private int tierSegment;
    private int tierLastSelected;
    private int tierStepFrame;
    private int tierStartFrame;
    private float tierSince;
    private bool tierAwaiting;
    private bool tierFlickArmed;
    private float nextEntryScan;
    private float nextTileScan;
    private float nextGeometryLog;
    private string loggedPointer = "";
    private string pointerSummary = "pointer none";

    internal bool Open => state is not null;

    // DevMode, von Pose je Frame gesetzt: nur dann die Messzeilen -
    // Sekundenbericht, Zeiger-Zonen, Geometrie. Abschnitt 210.
    internal bool Verbose { get; set; }

    // Zwischen zwei Scheiben: die alte ist zu, die neue noch nicht offen.
    internal bool Switching => pendingWheel is not null;

    // Einmal pro Frame, direkt nach menuMode und vor jedem Leser.
    internal void Detect(MelonLogger.Instance log, bool menuMode)
    {
        state = null;

        if (!menuMode)
        {
            NoteClosed(log);
            return;
        }

        try
        {
            var current = Il2CppFuturLab.PW2.PwsScreenManager.Instance?.MainViewport?.CurrentState;
            var wheel = current is null || current == null
                ? null
                : current.TryCast<Il2CppFuturLab.PW2.RadialMenuState>();

            state = wheel is null || wheel == null ? null : wheel;
        }
        catch
        {
            state = null;
        }

        if (state is null)
        {
            NoteClosed(log);
            return;
        }

        if (pendingWheel is { } wanted && SafeMenuTypeValue(state) == wanted)
        {
            log.Msg($"washer wheel: switched to {wanted}"
                + $" after {Time.frameCount - pendingFrame} frame(s)");
            pendingWheel = null;
        }
    }

    // Jedes Frame bei geschlossener Scheibe. Wartet, bis der Zustand des
    // Spiels SwitchSettleFrames lang keine Scheibe mehr ist, und ruft dann
    // EINMAL HandleEquipmentHeld.
    internal void DrivePending(MelonLogger.Instance log, Il2CppFuturLab.PW2.BaseInput input)
    {
        if (pendingWheel is not { } wanted || Open)
            return;

        if (Time.unscaledTime - pendingSince > SwitchTimeout)
        {
            log.Msg($"washer wheel: {wanted} did not open within {SwitchTimeout:0.#} s"
                + $"   requested {pendingRequested}   {Diagnose(input)}");
            pendingWheel = null;
            return;
        }

        if (pendingRequested)
            return;

        if (closedFrame < pendingFrame)
            closedFrame = Time.frameCount;

        if (Time.frameCount - closedFrame < SwitchSettleFrames)
            return;

        var pws = input.TryCast<Il2CppFuturLab.PW2.PwsPlayerInput>();

        if (pws is null || pws == null)
            return;

        pendingRequested = true;
        var before = Diagnose(input);
        pws.HandleEquipmentHeld(wanted);
        log.Msg($"washer wheel: HandleEquipmentHeld({wanted})"
            + $" {Time.frameCount - pendingFrame} frame(s) after the switch   {before}");
    }

    // Was das Oeffnen einer Scheibe braucht, als eine Zeile.
    internal static string Diagnose(Il2CppFuturLab.PW2.BaseInput input)
    {
        string handler, blocked, current;

        try
        {
            var open = input.OpenRadialMenu;
            handler = open is null || open == null ? "none" : "set";
        }
        catch (Exception exception)
        {
            handler = $"? ({exception.GetType().Name})";
        }

        try
        {
            blocked = input.BlockedInput.ToString();
        }
        catch (Exception exception)
        {
            blocked = $"? ({exception.GetType().Name})";
        }

        try
        {
            var state = Il2CppFuturLab.PW2.PwsScreenManager.Instance?.MainViewport?.CurrentState;
            current = state is null || state == null ? "null" : state.GetIl2CppType()?.Name ?? "?";
        }
        catch (Exception exception)
        {
            current = $"? ({exception.GetType().Name})";
        }

        return $"openHandler {handler}   blocked {blocked}   state {current}";
    }

    private void NoteClosed(MelonLogger.Instance log)
    {
        if (!wasOpen)
            return;

        wasOpen = false;
        log.Msg("washer wheel: closed");
    }

    // R3 gehalten. Derselbe Aufruf wie in Abschnitt 204 gemessen.
    internal bool RequestOpen(MelonLogger.Instance log, Il2CppFuturLab.PW2.BaseInput input,
        string before)
    {
        var pws = input.TryCast<Il2CppFuturLab.PW2.PwsPlayerInput>();

        if (pws is null || pws == null)
        {
            log.Msg("washer wheel: input is not PwsPlayerInput - not opened");
            return false;
        }

        var diagnosis = Diagnose(input);
        pws.HandleEquipmentHeld(Il2CppFuturLab.PW2.EquipmentType.PowerWasher);
        log.Msg($"washer wheel: R3 hold, HandleEquipmentHeld(PowerWasher)  at {before}   {diagnosis}");
        return true;
    }

    // Gibt true zurueck, wenn die Scheibe in diesem Frame mit R3 uebernommen
    // und geschlossen wurde; der Aufrufer sperrt dann den Rest des R3-Drucks.
    // Belegung: Kopf dieser Datei, Abschnitt 210.
    internal bool Drive(MelonLogger.Instance log, Il2CppFuturLab.PW2.BaseInput input,
        Vector2 stick, float tierStickX, bool acceptDown, bool stickClickDown,
        bool previousWheelDown, bool nextWheelDown, bool selectDown,
        WheelPointer pointer, Func<string> describe, Action<string> buzz)
    {
        var wheel = state;

        if (wheel is null)
            return false;

        // DER ERSTE FRAME SCHLUCKT, was beim Oeffnen schon gedrueckt war: R3
        // haelt noch, nach einem Wechsel der Griff, und ein neuer ButtonEdge
        // saehe darin einen Druck. Ein schon gekippter linker Stick gibt erst
        // nach der Mitte eine Stufe.
        if (!wasOpen)
        {
            wasOpen = true;
            loggedMode = false;
            nextReport = 0f;
            loggedSegment = int.MinValue;
            ui = FindUi();
            ForgetGeometry();
            EndTierWalk();
            tierFlickArmed = Mathf.Abs(tierStickX) < TierFlickOut;
            accept.Poll(acceptDown, 0f);
            stickClick.Poll(stickClickDown, 0f);
            previousWheel.Poll(previousWheelDown, 0f);
            nextWheel.Poll(nextWheelDown, 0f);
            select.Poll(selectDown, 0f);
            log.Msg($"washer wheel: open   type {SafeMenuType(wheel)}   {describe()}");
            buzz("washer wheel open");
            return false;
        }

        accept.Poll(acceptDown, 0f);
        stickClick.Poll(stickClickDown, 0f);
        previousWheel.Poll(previousWheelDown, 0f);
        nextWheel.Poll(nextWheelDown, 0f);
        select.Poll(selectDown, 0f);

        try
        {
            var mode = wheel.m_inputMode;

            if (mode != Il2CppFuturLab.PW2.UI.RadialMenuInputMode.ConsoleController)
            {
                wheel.m_inputMode = Il2CppFuturLab.PW2.UI.RadialMenuInputMode.ConsoleController;

                if (!loggedMode)
                {
                    loggedMode = true;
                    log.Msg($"washer wheel: input mode {mode} -> ConsoleController");
                }
            }

            input.RadialWheelNavigateRaw = Vector2.zero;

            if (ui is null || ui == null)
                ui = FindUi();

            // Nur mit ausgelenktem Stick: in der Mitte behaelt ein Gamepad die
            // letzte Richtung, und genau so bleibt die Wahl stehen, waehrend
            // der Daumen zu A wandert.
            var stickAims = stick.magnitude > AimDeadzone;

            if (ui is not null && stickAims)
                ui.UpdateInput(stick, Il2CppFuturLab.PW2.UI.RadialMenuInputMode.ConsoleController);

            // Der Strahl nur, wenn der Stick NICHT zielt - ein Schreiber je
            // Frame. Abschnitt 209.
            if (ui is not null && !stickAims && pointer.Valid)
                Point(log, ui, pointer, select.Pressed, buzz);
            else
            {
                pointerSummary = stickAims ? "pointer yields to the stick"
                    : pointer.Valid ? "pointer (no RadialMenuUI)" : "pointer none";

                if (select.Pressed)
                    log.Msg($"washer wheel: trigger, nothing chosen   {pointerSummary}");
            }

            if (ui is not null)
                WalkTier(log, input, ui);

            var segment = ui is null ? -1 : ui.m_indexOfSegment;

            if (segment != loggedSegment)
            {
                loggedSegment = segment;
                log.Msg($"washer wheel: segment {segment} of {(ui is null ? -1 : ui.m_segmentCount)}"
                    + $"   stick ({stick.x:0.##}, {stick.y:0.##})   highlighted {Highlighted(ui)}");
            }

            if (Verbose && Time.unscaledTime >= nextReport)
            {
                nextReport = Time.unscaledTime + 1f;
                log.Msg($"washer wheel: stick ({stick.x:0.##}, {stick.y:0.##})"
                    + $"   mode before write {mode}"
                    + $"   cursorActive {wheel.IsControllerCursorActive}"
                    + $"   segment {segment}   highlighted {Highlighted(ui)}"
                    + $"   {pointerSummary}   hovered entry {HoveredEntry()}");
            }
        }
        catch (Exception exception)
        {
            log.Warning($"washer wheel: steering threw {exception.GetType().Name}: {exception.Message}");
        }

        // DIE STUFE FUER DAS STICKSPIEL - Abschnitt 210. Der linke Stick hat
        // bei offener Scheibe keine Aufgabe: das Gehen steht im Menue still,
        // und die Menuenavigation kehrt vor ihm um. Ein Schritt je Kippen,
        // geladen wird erst in der Mitte.
        if (tierFlickArmed && Mathf.Abs(tierStickX) > TierFlickIn)
        {
            tierFlickArmed = false;
            var direction = tierStickX > 0f ? 1 : -1;

            if (tierTarget >= 0 && ui is not null)
                FinishTier(log, ui, "cut short by the left stick", -1);

            input.InvokeNavigateSubMenu(direction);
            log.Msg($"washer wheel: tier {direction:+0;-0} (left stick)"
                + $"   carousel selected {(ui is null ? "-" : CarouselSelected(ui))}"
                + $"   highlighted {Highlighted(ui)}");
        }
        else if (Mathf.Abs(tierStickX) < TierFlickOut)
        {
            tierFlickArmed = true;
        }

        var pws = input.TryCast<Il2CppFuturLab.PW2.PwsPlayerInput>();

        if (pws is null || pws == null)
            return false;

        var type = SafeMenuTypeValue(wheel) ?? Il2CppFuturLab.PW2.EquipmentType.PowerWasher;

        if (previousWheel.Pressed || nextWheel.Pressed)
        {
            SwitchWheel(log, pws, type, nextWheel.Pressed ? 1 : -1, describe, buzz);
            EndTierWalk();
            return false;
        }

        if (!accept.Pressed && !stickClick.Pressed)
            return false;

        var how = accept.Pressed ? "A" : "R3";
        var walk = tierTarget >= 0
            ? $"   tier walk to {tierTarget} still running after {tierSteps} step(s)"
            : "";
        var carousel = ui is null ? "-" : CarouselSelected(ui);
        var before = describe();
        pws.HandleEquipmentReleased(type);
        EndTierWalk();
        log.Msg($"washer wheel: {how} takes {type}   carousel selected {carousel}{walk}"
            + $"   before {before}   after {describe()}");
        buzz("washer wheel take");
        return how == "R3";
    }

    // Uebernimmt die offene Scheibe. Die naechste oeffnet DrivePending, sobald
    // diese ganz zu ist. wasOpen faellt, damit Drive die neue Scheibe wie eine
    // frische Oeffnung behandelt: Kanten vorbelegt - der Trigger haelt noch -
    // und die UI neu gesucht.
    private void SwitchWheel(MelonLogger.Instance log, Il2CppFuturLab.PW2.PwsPlayerInput pws,
        Il2CppFuturLab.PW2.EquipmentType type, int direction, Func<string> describe,
        Action<string> buzz)
    {
        var index = Array.IndexOf(Wheels, type);
        var target = Wheels[((index < 0 ? 0 : index) + direction + Wheels.Length) % Wheels.Length];

        var before = describe();
        pws.HandleEquipmentReleased(type);

        wasOpen = false;
        ui = null;
        pendingWheel = target;
        pendingSince = Time.unscaledTime;
        pendingFrame = Time.frameCount;
        closedFrame = 0;
        pendingRequested = false;

        log.Msg($"washer wheel: grip {direction:+0;-0} takes {type}, {target} next"
            + $"   before {before}   after {describe()}   {Diagnose(pws)}");
        buzz("washer wheel switch");
    }

    // ====================================================================
    // DER ZEIGESTRAHL - Abschnitt 209.
    private void ForgetGeometry()
    {
        entries = null;
        tiles = null;
        root = null;
        entryPoints.Clear();
        entryRadius = 0f;
        loggedRadius = -1f;
        geometrySource = "";
        loggedSource = "";
        beamLength = -1f;
        loggedEntries = false;
        nextEntryScan = 0f;
        nextTileScan = 0f;
        nextGeometryLog = 0f;
        loggedPointer = "";
        pointerSummary = "pointer none";
    }

    // Zielen und, mit selectPressed, waehlen. Auf einer Kachel die Stufe, im
    // Ring das Segment, in der Mitte und ausserhalb nichts.
    private void Point(MelonLogger.Instance log, Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuUI wheelUi,
        WheelPointer pointer, bool selectPressed, Action<string> buzz)
    {
        UpdateGeometry(log, wheelUi);

        beamLength = pointer.BeamLength;
        var frame = root;

        if (frame is null || frame == null || entryRadius <= 0f)
        {
            NotePointer(log, wheelUi, "no geometry", "", -1f, -1f, Vector2.zero);
            NoteMissedSelect(log, selectPressed);
            return;
        }

        var normal = frame.forward;
        var denominator = Vector3.Dot(pointer.Forward, normal);

        if (Mathf.Abs(denominator) < 1e-5f)
        {
            NotePointer(log, wheelUi, "parallel to the wheel", "", -1f, -1f, Vector2.zero);
            NoteMissedSelect(log, selectPressed);
            return;
        }

        var distance = Vector3.Dot(frame.position - pointer.Origin, normal) / denominator;

        if (distance <= 0.05f)
        {
            NotePointer(log, wheelUi, "wheel behind the hand", "", distance, -1f, Vector2.zero);
            NoteMissedSelect(log, selectPressed);
            return;
        }

        var local = frame.InverseTransformPoint(pointer.Origin + (pointer.Forward * distance));
        var point = new Vector2(local.x, local.y);
        var offset = point - centre;
        var reach = offset.magnitude / entryRadius;

        var tile = TileUnder(wheelUi, point, out var tileIndex);

        if (tileIndex >= 0)
        {
            NotePointer(log, wheelUi, "tile", tile, distance, reach, point);

            if (selectPressed)
                StartTierWalk(log, wheelUi, tileIndex, buzz);

            return;
        }

        if (reach > MaxReach)
        {
            NotePointer(log, wheelUi, "outside", "", distance, reach, point);
            NoteMissedSelect(log, selectPressed);
            return;
        }

        if (reach < CentreZone)
        {
            NotePointer(log, wheelUi, "centre", tile, distance, reach, point);
            NoteMissedSelect(log, selectPressed);
            return;
        }

        if (HoverSelects || selectPressed)
        {
            var segmentBefore = wheelUi.m_indexOfSegment;
            wheelUi.UpdateInput(offset.normalized,
                Il2CppFuturLab.PW2.UI.RadialMenuInputMode.ConsoleController);

            if (selectPressed)
            {
                if (tierTarget >= 0)
                    FinishTier(log, wheelUi, "cut short by a segment choice", -1);

                var angle = Mathf.Atan2(offset.y, offset.x) * Mathf.Rad2Deg;
                log.Msg($"washer wheel: trigger selects segment {wheelUi.m_indexOfSegment}"
                    + $" (was {segmentBefore})   angle {angle:0}   r {reach:0.00}"
                    + $"   nearest entry {NearestEntry(offset)}   highlighted {Highlighted(wheelUi)}");
                buzz("washer wheel select");
            }
        }

        NotePointer(log, wheelUi, "ring", "", distance, reach, point);
    }

    // Ein Trigger ohne Ziel - die Zone steht schon in pointerSummary.
    private void NoteMissedSelect(MelonLogger.Instance log, bool selectPressed)
    {
        if (selectPressed)
            log.Msg($"washer wheel: trigger, nothing chosen   {pointerSummary}");
    }

    // ====================================================================
    // DIE STUFE PER TRIGGER - Abschnitt 210.
    private void StartTierWalk(MelonLogger.Instance log, Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuUI wheelUi,
        int tileIndex, Action<string> buzz)
    {
        var carousel = Carousel(wheelUi);
        var tile = tiles is null || tileIndex >= tiles.Length ? null : tiles[tileIndex];

        if (carousel is null || tile is null || tile == null)
        {
            log.Msg($"washer wheel: trigger on tier tile {tileIndex}, but no carousel");
            return;
        }

        var options = carousel.m_subOptions;
        var count = options is null || options == null ? 0 : options.Count;
        var target = -1;

        for (var index = 0; index < count; index++)
        {
            var option = options![index];

            if (option is not null && option != null && option.Pointer == tile.Pointer)
            {
                target = index;
                break;
            }
        }

        var selected = carousel.m_selectedIndex;

        if (target < 0)
        {
            log.Msg($"washer wheel: trigger on tier tile {tileIndex} {tile.name},"
                + $" not among {count} sub option(s)   carousel selected {CarouselSelected(wheelUi)}");
            return;
        }

        if (tierTarget >= 0)
            FinishTier(log, wheelUi, "replaced by another tile", selected);

        buzz("washer wheel select");

        if (target == selected)
        {
            log.Msg($"washer wheel: trigger on tier tile {tileIndex} {tile.name}"
                + $" = sub option {target} of {count}, already selected");
            return;
        }

        tierTarget = target;
        tierDirection = target > selected ? 1 : -1;
        tierSteps = 0;
        tierAwaiting = false;
        tierSegment = wheelUi.m_indexOfSegment;
        tierLastSelected = selected;
        tierSince = Time.unscaledTime;
        tierStartFrame = Time.frameCount;

        log.Msg($"washer wheel: trigger on tier tile {tileIndex} {tile.name}"
            + $" = sub option {target} of {count}, from {selected}, stepping {tierDirection:+0;-0}");
    }

    // Jedes Frame: ein Schritt, dann warten, bis m_selectedIndex ihn zeigt.
    // So zaehlt ein Index, der einen Frame nachhinkt, keinen Schritt doppelt.
    private void WalkTier(MelonLogger.Instance log, Il2CppFuturLab.PW2.BaseInput input,
        Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuUI wheelUi)
    {
        if (tierTarget < 0)
            return;

        var carousel = Carousel(wheelUi);

        if (carousel is null)
        {
            FinishTier(log, wheelUi, "lost the carousel", -1);
            return;
        }

        var selected = carousel.m_selectedIndex;

        if (wheelUi.m_indexOfSegment != tierSegment)
        {
            FinishTier(log, wheelUi, "cut short, the segment changed", selected);
            return;
        }

        if (tierAwaiting)
        {
            if (selected == tierLastSelected)
            {
                if (Time.frameCount - tierStepFrame > TierStepWaitFrames)
                    FinishTier(log, wheelUi, "stalled, the step did not move the selection", selected);

                return;
            }

            tierAwaiting = false;
        }

        if (selected == tierTarget)
        {
            FinishTier(log, wheelUi, "reached", selected);
            return;
        }

        if (Math.Sign(tierTarget - selected) != tierDirection)
        {
            FinishTier(log, wheelUi, "passed over - a locked tier?", selected);
            return;
        }

        if (tierSteps >= MaxTierSteps || Time.unscaledTime - tierSince > TierTimeout)
        {
            FinishTier(log, wheelUi, "gave up", selected);
            return;
        }

        input.InvokeNavigateSubMenu(tierDirection);
        tierSteps++;
        tierAwaiting = true;
        tierStepFrame = Time.frameCount;
        tierLastSelected = selected;
    }

    private void FinishTier(MelonLogger.Instance log, Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuUI wheelUi,
        string outcome, int selected)
    {
        log.Msg($"washer wheel: tier walk to {tierTarget} {outcome}"
            + (selected < 0 ? "" : $"   at {selected}")
            + $"   {tierSteps} step(s), {Time.frameCount - tierStartFrame} frame(s)"
            + $"   carousel selected {CarouselSelected(wheelUi)}   highlighted {Highlighted(wheelUi)}");
        EndTierWalk();
    }

    private void EndTierWalk()
    {
        tierTarget = -1;
        tierSteps = 0;
        tierAwaiting = false;
    }

    private static Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuSubOptionManager? Carousel(
        Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuUI wheelUi)
    {
        var content = wheelUi.m_radialContentController;
        var carousel = content is null || content == null ? null : content.m_carousel;
        return carousel is null || carousel == null ? null : carousel;
    }

    // Eine Zeile bei jedem Wechsel von Zone, Segment oder Stufen-Kachel.
    private void NotePointer(MelonLogger.Instance log, Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuUI wheelUi,
        string zone, string tile, float distance, float reach, Vector2 point)
    {
        var segment = wheelUi.m_indexOfSegment;
        pointerSummary = reach < 0f ? $"pointer {zone}" : $"pointer {zone} r {reach:0.00}";
        var key = $"{zone}|{tile}|{segment}";

        if (!Verbose || key == loggedPointer)
            return;

        loggedPointer = key;
        var offset = point - centre;
        var angle = Mathf.Atan2(offset.y, offset.x) * Mathf.Rad2Deg;

        log.Msg($"washer wheel pointer: {zone}"
            + (tile.Length == 0 ? "" : $"   tier tile {tile}")
            + $"   segment {segment}"
            + $"   local ({point.x:0}, {point.y:0})   r {reach:0.00}   angle {angle:0}"
            + $"   nearest entry {NearestEntry(offset)}"
            + $"   wheel {distance:0.00} m   beam {beamLength:0.00} m"
            + $"   carousel selected {CarouselSelected(wheelUi)}"
            + $"   highlighted {Highlighted(wheelUi)}");
    }

    // Mitte und Massstab aus den Symbolen der Eintraege, jedes Frame - sie
    // koennen beim Oeffnen noch hereinfahren. Die Liste selbst wird nur
    // gesucht, solange sie unvollstaendig ist.
    private void UpdateGeometry(MelonLogger.Instance log, Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuUI wheelUi)
    {
        if (root is null || root == null)
        {
            var entriesRoot = wheelUi.m_entriesRoot;
            root = entriesRoot is null || entriesRoot == null
                ? wheelUi.transform.TryCast<RectTransform>()
                : entriesRoot;
        }

        var frame = root;

        if (frame is null || frame == null)
            return;

        var segments = wheelUi.m_segmentCount;

        if ((entries is null || entries.Length < segments) && Time.unscaledTime >= nextEntryScan)
        {
            nextEntryScan = Time.unscaledTime + 0.5f;
            entries = wheelUi.GetComponentsInChildren<Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuEntry>();
        }

        entryPoints.Clear();

        if (entries is not null)
        {
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];

                if (entry is null || entry == null)
                    continue;

                var local = frame.InverseTransformPoint(EntryAnchor(entry).position);
                entryPoints.Add(new Vector2(local.x, local.y));
            }
        }

        var sum = Vector2.zero;

        foreach (var point in entryPoints)
            sum += point;

        // Ab drei Eintraegen ist ihr Mittelwert die Mitte des Kreises; darunter
        // gilt der Ursprung von m_entriesRoot.
        centre = entryPoints.Count >= 3 ? sum / entryPoints.Count : Vector2.zero;

        var spread = 0f;

        foreach (var point in entryPoints)
            spread += (point - centre).magnitude;

        entryRadius = entryPoints.Count == 0 ? 0f : spread / entryPoints.Count;
        geometrySource = "icons";

        // Liegen die Symbole auf einem Punkt, traegt nur noch die
        // Konfiguration. Gemessen in 1.120.0 fuer die Eintraege selbst.
        if (entryRadius < 1f)
        {
            centre = Vector2.zero;
            entryRadius = ConfigEntryRadius(wheelUi);
            geometrySource = "config";
        }

        var changed = loggedRadius < 0f || geometrySource != loggedSource
            || Mathf.Abs(entryRadius - loggedRadius) > Mathf.Max(1f, loggedRadius * 0.05f);

        if (!Verbose || !changed || Time.unscaledTime < nextGeometryLog)
            return;

        nextGeometryLog = Time.unscaledTime + 0.5f;
        loggedRadius = entryRadius;
        loggedSource = geometrySource;
        LogGeometry(log, wheelUi, frame, segments);
    }

    // Das Symbol des Eintrags - SetPositionFromIndex setzt es auf den Kreis -,
    // sonst der Eintrag selbst.
    private static Transform EntryAnchor(Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuEntry entry)
    {
        var icon = entry.m_entryIconImage;
        return icon is null || icon == null ? entry.transform : icon.transform;
    }

    private static float ConfigEntryRadius(Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuUI wheelUi)
    {
        try
        {
            var settings = wheelUi.m_config;

            if (settings is not null && settings != null && settings.EntryRadius > 1f)
                return settings.EntryRadius;
        }
        catch
        {
        }

        return FallbackEntryRadius;
    }

    private void LogGeometry(MelonLogger.Instance log, Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuUI wheelUi,
        RectTransform frame, int segments)
    {
        var config = "?";

        try
        {
            var settings = wheelUi.m_config;

            if (settings is not null && settings != null)
                config = $"center {settings.CenterRadius:0.#} inner {settings.InnerRadius:0.#}"
                    + $" entry {settings.EntryRadius:0.#} rotation {settings.RotationOffset:0.#}"
                    + $" segments {settings.SegmentCount}";
        }
        catch (Exception exception)
        {
            config = $"? ({exception.GetType().Name})";
        }

        log.Msg($"washer wheel geometry: segments {segments}   entries {entryPoints.Count}"
            + $"   centre ({centre.x:0}, {centre.y:0})   entry radius {entryRadius:0} from {geometrySource}"
            + $"   root {frame.name} scale {frame.lossyScale.x:0.#####}"
            + $"   config {config}");

        // Die Einzelposten nur beim ersten Mal je Oeffnung; danach meldet die
        // Zeile nur noch, dass der Radius sich bewegt hat.
        if (loggedEntries)
            return;

        loggedEntries = true;

        for (var index = 0; index < entryPoints.Count; index++)
        {
            var offset = entryPoints[index] - centre;
            log.Msg($"    entry {index} ({entryPoints[index].x:0}, {entryPoints[index].y:0})"
                + $"   angle {Mathf.Atan2(offset.y, offset.x) * Mathf.Rad2Deg:0}"
                + $"   distance {offset.magnitude:0}   name {EntryName(index)}");
        }

        RefreshTiles(wheelUi, true);

        if (tiles is null)
        {
            log.Msg("    tier tiles: no carousel");
            return;
        }

        log.Msg($"    tier tiles {tiles.Length}   carousel selected {CarouselSelected(wheelUi)}");

        for (var index = 0; index < tiles.Length; index++)
        {
            var host = TileRect(tiles[index]);

            if (host is null || !LocalBox(frame, host, out var min, out var max))
            {
                log.Msg($"    tier tile {index}: no rect");
                continue;
            }

            log.Msg($"    tier tile {index}   x {min.x:0}..{max.x:0}   y {min.y:0}..{max.y:0}"
                + $"   r {((min + max) * 0.5f - centre).magnitude / Mathf.Max(1f, entryRadius):0.00}"
                + $"   name {tiles[index].name}");
        }
    }

    // Die Stufen wechseln mit der Marke, darum hoechstens alle 0,5 s neu.
    private void RefreshTiles(Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuUI wheelUi, bool now)
    {
        if (!now && tiles is not null && Time.unscaledTime < nextTileScan)
            return;

        nextTileScan = Time.unscaledTime + 0.5f;
        tiles = null;

        var content = wheelUi.m_radialContentController;
        var carousel = content is null || content == null ? null : content.m_carousel;

        if (carousel is null || carousel == null)
            return;

        tiles = carousel.GetComponentsInChildren<Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuSubOption>();
    }

    private string TileUnder(Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuUI wheelUi, Vector2 point,
        out int tileIndex)
    {
        tileIndex = -1;
        RefreshTiles(wheelUi, false);

        var frame = root;

        if (tiles is null || frame is null || frame == null)
            return "none (no carousel)";

        for (var index = 0; index < tiles.Length; index++)
        {
            var host = TileRect(tiles[index]);

            if (host is null || !LocalBox(frame, host, out var min, out var max))
                continue;

            if (point.x >= min.x - TileMargin && point.x <= max.x + TileMargin
                && point.y >= min.y - TileMargin && point.y <= max.y + TileMargin)
            {
                tileIndex = index;
                return $"{index} of {tiles.Length} {tiles[index].name}";
            }
        }

        return $"none of {tiles.Length}";
    }

    // Der Knopf der Kachel, wenn sie einen hat - er ist die Flaeche, die ein
    // Klick trifft.
    private static RectTransform? TileRect(Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuSubOption? tile)
    {
        if (tile is null || tile == null)
            return null;

        var button = tile.m_futurButton;
        var host = button is null || button == null ? tile.transform : button.transform;
        var rect = host.TryCast<RectTransform>();
        return rect is null || rect == null ? null : rect;
    }

    // Die vier Ecken in den lokalen Koordinaten von m_entriesRoot.
    private bool LocalBox(RectTransform frame, RectTransform rect, out Vector2 min, out Vector2 max)
    {
        min = new Vector2(float.MaxValue, float.MaxValue);
        max = new Vector2(float.MinValue, float.MinValue);

        corners ??= new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        rect.GetWorldCorners(corners);

        for (var index = 0; index < 4; index++)
        {
            var local = frame.InverseTransformPoint(corners[index]);
            min = Vector2.Min(min, new Vector2(local.x, local.y));
            max = Vector2.Max(max, new Vector2(local.x, local.y));
        }

        return max.x > min.x && max.y > min.y;
    }

    private string NearestEntry(Vector2 offset)
    {
        if (entryPoints.Count == 0 || offset.sqrMagnitude < 1e-6f)
            return "-";

        var best = -1;
        var bestDot = float.MinValue;
        var direction = offset.normalized;

        for (var index = 0; index < entryPoints.Count; index++)
        {
            var dot = Vector2.Dot(direction, (entryPoints[index] - centre).normalized);

            if (dot > bestDot)
            {
                bestDot = dot;
                best = index;
            }
        }

        return best.ToString();
    }

    private string HoveredEntry()
    {
        try
        {
            if (entries is null)
                return "-";

            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];

                if (entry is not null && entry != null && entry.IsHoverActive)
                    return index.ToString();
            }

            return "none";
        }
        catch (Exception exception)
        {
            return $"? ({exception.GetType().Name})";
        }
    }

    private string EntryName(int index)
    {
        try
        {
            var entry = entries is null || index >= entries.Length ? null : entries[index];

            if (entry is null || entry == null)
                return "?";

            // CategoryData ist eine Struktur mit Referenzen - Abschnitt 36, nicht
            // ueber die Grenze. Der Name reicht fuer den Abgleich.
            return entry.name;
        }
        catch (Exception exception)
        {
            return $"? ({exception.GetType().Name})";
        }
    }

    private static string CarouselSelected(Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuUI wheelUi)
    {
        try
        {
            var content = wheelUi.m_radialContentController;
            var carousel = content is null || content == null ? null : content.m_carousel;

            return carousel is null || carousel == null
                ? "-"
                : $"{carousel.m_selectedIndex} confirmed {carousel.m_confirmedIndex}";
        }
        catch (Exception exception)
        {
            return $"? ({exception.GetType().Name})";
        }
    }

    private static Il2CppFuturLab.PW2.EquipmentType? SafeMenuTypeValue(
        Il2CppFuturLab.PW2.RadialMenuState wheel)
    {
        try
        {
            return wheel.MenuType;
        }
        catch
        {
            return null;
        }
    }

    private static string SafeMenuType(Il2CppFuturLab.PW2.RadialMenuState wheel)
    {
        try
        {
            return wheel.MenuType.ToString();
        }
        catch
        {
            return "?";
        }
    }

    // Einmal je Oeffnung gesucht, nicht je Frame.
    private static Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuUI? FindUi()
    {
        try
        {
            var found = UnityEngine.Object.FindObjectOfType<Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuUI>();
            return found is null || found == null ? null : found;
        }
        catch
        {
            return null;
        }
    }

    // Was die Scheibe gerade hervorhebt, als Asset-Name - die Form, die
    // DescribeConfiguration fuer den Washer schon benutzt.
    private static string Highlighted(Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuUI? ui)
    {
        try
        {
            if (ui is null || ui == null)
                return "(no RadialMenuUI)";

            var selection = ui.CurrentSelection;
            return selection is null || selection == null ? "none" : selection.name;
        }
        catch (Exception exception)
        {
            return $"? ({exception.GetType().Name})";
        }
    }
}
