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
// BELEGUNG, offen:
//   rechter Stick          zielt auf die Marke (Segment)
//   linker / rechter Griff Stufe zurueck / vor - wie Tab zurueck/vor im Menue
//   A, rechter Trigger, R3 uebernehmen und schliessen
// Die Scheibe bleibt nach dem Loslassen von R3 offen: R3 gedrueckt halten und
// denselben Stick kippen ist am Touch-Controller unbequem.
//
// ERKANNT WIRD DIE SCHEIBE AM ZUSTAND DES SPIELS, nicht an der eigenen
// Oeffnung: CurrentState ist dann ein RadialMenuState. Schliesst sie auf
// einem anderen Weg, faellt die Steuerung im selben Frame heraus.
internal sealed class WasherWheel
{
    private readonly ButtonEdge accept = new();
    private readonly ButtonEdge trigger = new();
    private readonly ButtonEdge stickClick = new();
    private readonly ButtonEdge previous = new();
    private readonly ButtonEdge next = new();

    private const float AimDeadzone = 0.35f;

    private Il2CppFuturLab.PW2.RadialMenuState? state;
    private Il2CppFuturLab.PW2.UI.RadialMenu.RadialMenuUI? ui;
    private int loggedSegment = int.MinValue;
    private bool wasOpen;
    private bool loggedMode;
    private float nextReport;

    internal bool Open => state is not null;

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
            NoteClosed(log);
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

        pws.HandleEquipmentHeld(Il2CppFuturLab.PW2.EquipmentType.PowerWasher);
        log.Msg($"washer wheel: R3 hold, HandleEquipmentHeld(PowerWasher)  at {before}");
        return true;
    }

    // Gibt true zurueck, wenn die Scheibe in diesem Frame uebernommen und
    // geschlossen wurde; der Aufrufer sperrt dann den Rest des R3-Drucks.
    internal bool Drive(MelonLogger.Instance log, Il2CppFuturLab.PW2.BaseInput input,
        Vector2 stick, bool acceptDown, bool triggerDown, bool stickClickDown,
        bool previousDown, bool nextDown, Func<string> describe, Action<string> buzz)
    {
        var wheel = state;

        if (wheel is null)
            return false;

        // DER ERSTE FRAME SCHLUCKT, was beim Oeffnen schon gedrueckt war: R3
        // haelt noch, und ein neuer ButtonEdge saehe darin einen Druck.
        if (!wasOpen)
        {
            wasOpen = true;
            loggedMode = false;
            nextReport = 0f;
            loggedSegment = int.MinValue;
            ui = FindUi();
            accept.Poll(acceptDown, 0f);
            trigger.Poll(triggerDown, 0f);
            stickClick.Poll(stickClickDown, 0f);
            previous.Poll(previousDown, 0f);
            next.Poll(nextDown, 0f);
            log.Msg($"washer wheel: open   type {SafeMenuType(wheel)}   {describe()}");
            buzz("washer wheel open");
            return false;
        }

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
            if (ui is not null && stick.magnitude > AimDeadzone)
                ui.UpdateInput(stick, Il2CppFuturLab.PW2.UI.RadialMenuInputMode.ConsoleController);

            var segment = ui is null ? -1 : ui.m_indexOfSegment;

            if (segment != loggedSegment)
            {
                loggedSegment = segment;
                log.Msg($"washer wheel: segment {segment} of {(ui is null ? -1 : ui.m_segmentCount)}"
                    + $"   stick ({stick.x:0.##}, {stick.y:0.##})   highlighted {Highlighted(ui)}");
            }

            if (Time.unscaledTime >= nextReport)
            {
                nextReport = Time.unscaledTime + 1f;
                log.Msg($"washer wheel: stick ({stick.x:0.##}, {stick.y:0.##})"
                    + $"   mode before write {mode}"
                    + $"   cursorActive {wheel.IsControllerCursorActive}"
                    + $"   segment {segment}   highlighted {Highlighted(ui)}");
            }
        }
        catch (Exception exception)
        {
            log.Warning($"washer wheel: steering threw {exception.GetType().Name}: {exception.Message}");
        }

        accept.Poll(acceptDown, 0f);
        trigger.Poll(triggerDown, 0f);
        stickClick.Poll(stickClickDown, 0f);
        previous.Poll(previousDown, 0f);
        next.Poll(nextDown, 0f);

        if (previous.Pressed || next.Pressed)
        {
            var direction = next.Pressed ? 1 : -1;
            input.InvokeNavigateSubMenu(direction);
            log.Msg($"washer wheel: tier {direction:+0;-0}   highlighted {Highlighted(ui)}");
        }

        if (!accept.Pressed && !trigger.Pressed && !stickClick.Pressed)
            return false;

        var how = accept.Pressed ? "A" : trigger.Pressed ? "trigger" : "R3";
        var pws = input.TryCast<Il2CppFuturLab.PW2.PwsPlayerInput>();

        if (pws is null || pws == null)
            return false;

        var before = describe();
        pws.HandleEquipmentReleased(Il2CppFuturLab.PW2.EquipmentType.PowerWasher);
        log.Msg($"washer wheel: {how} takes it   before {before}   after {describe()}");
        buzz("washer wheel take");
        return how == "R3";
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
