using UnityEngine;
using UnityEngine.InputSystem;

namespace WetReality;

// Edge and hold detection for one controller button.
//
// OpenXR delivers boolean state, not events, and Unity's Hold interaction lives
// in the GAME's action asset rather than in ours - so tap and hold have to be
// derived here. That is not a workaround: BaseInput exposes the two halves as
// DIFFERENT calls (InvokeCrouchPressed against InvokeCrouchLongPressed,
// InvokeToggleTaskList against InvokeToggleFurnitureInventory), so a caller has
// to make the distinction itself. FuturLab's own shipped VR layer agrees - its
// ButtonPressType enum is {Default, LongPress, DoublePress}.
//
// The asymmetry is deliberate and unavoidable:
//
//   Hold fires while the finger is still DOWN, on crossing the threshold. A
//   furniture inventory that only appears after you let go would be useless,
//   and the game's own Hold interaction fires at the threshold too.
//
//   Tap can only fire on RELEASE, because until the finger lifts there is no
//   way to know it was a tap.
//
// So a tap costs one release of latency. That is invisible on a stance toggle
// and intolerable on a spray trigger, which is why nothing in this project puts
// a timer anywhere near the right trigger.
internal sealed class ButtonEdge
{
    private bool down;
    private float pressedAt;
    private bool holdFired;

    // Was der Flankenzaehler fuer den aktuellen Zustand der Taste haelt.
    // Nur zum Berichten: bleibt eine Rastung stehen, ist die Frage, ob hier
    // noch "gedrueckt" steht, weil die Achse stumm wurde.
    internal bool Down => down;

    // True for exactly one frame.
    internal bool Pressed { get; private set; }
    internal bool Released { get; private set; }
    internal bool Tap { get; private set; }
    internal bool Hold { get; private set; }

    internal void Reset()
    {
        down = false;
        holdFired = false;
        Pressed = false;
        Released = false;
        Tap = false;
        Hold = false;
    }

    // holdSeconds of zero disables the hold half entirely, which is what a
    // button wants when it has only one job - then Tap fires on press rather
    // than on release, because there is nothing to wait for.
    //
    // MIT holdSeconds UEBER NULL feuert Hold AN DER SCHWELLE, waehrend der
    // Finger noch unten ist, und Tap erst beim Loslassen. Genau diese
    // Aufteilung traegt seit Abschnitt 149 die Immersion-Geste auf der
    // Menue-Taste: lang gedrueckt schaltet die UI, kurz gedrueckt oeffnet das
    // Menue.
    //
    // Ein Doppelklick-Fenster stand hier auch einmal. Es ist wieder heraus:
    // Virtual Desktop belegt den Doppelklick der Menue-Taste selbst, und das
    // Fenster kostete 0,3 s Verzoegerung auf JEDEN Menuedruck - ein Halten
    // braucht keines.
    internal void Poll(bool pressed, float holdSeconds)
    {
        Pressed = false;
        Released = false;
        Tap = false;
        Hold = false;

        if (pressed && !down)
        {
            down = true;
            holdFired = false;
            pressedAt = Time.unscaledTime;
            Pressed = true;

            if (holdSeconds <= 0f)
                Tap = true;

            return;
        }

        if (pressed && down)
        {
            if (holdSeconds > 0f && !holdFired
                && Time.unscaledTime - pressedAt >= holdSeconds)
            {
                holdFired = true;
                Hold = true;
            }

            return;
        }

        if (!pressed && down)
        {
            down = false;
            Released = true;

            // Only a release that never became a hold counts as a tap.
            if (holdSeconds > 0f && !holdFired)
                Tap = true;
        }
    }

    // Buttons registered as Binary come through as a Unity Button control, and
    // IsPressed is the read this project has already proven on the trigger.
    // Axis1D controls - the grip squeeze - need a value and a threshold, so they
    // use ReadAxis below instead.
    internal static bool IsDown(InputAction? action)
    {
        if (action is null)
            return false;

        try
        {
            return action.IsPressed();
        }
        catch
        {
            return false;
        }
    }

    internal static float ReadAxis(InputAction? action)
        => ReadAxis(action, out _);

    // MIT DER ZWEITEN ANTWORT, und die ist der Grund fuer die Ueberladung:
    // 0f bedeutete bisher ZWEI verschiedene Dinge - "der Griff ist offen"
    // und "diese Achse ist nicht lesbar". Fuer eine FLANKE sind beide
    // gleich, also kommt nach einem Verlust der Bindung nie wieder eine
    // Abwaertsflanke - und wer davon einen DAUERZUSTAND abhaengig macht,
    // haelt ihn dann fuer immer. Genau so blieb der Dauerstrahl stehen.
    //
    // readable wird erst nach dem vollstaendigen Lesen gesetzt: ein Wert,
    // der beim Auspacken wirft, ist nicht gelesen.
    internal static float ReadAxis(InputAction? action, out bool readable)
    {
        readable = false;

        if (action is null)
            return 0f;

        try
        {
            var raw = action.ReadValueAsObject();

            // EIN FEHLENDER WERT IST KEIN FEHLER, und diese Verwechslung hat
            // Geld gekostet. Eine Achse in Ruhe liefert null - die Action geht
            // in den Wartezustand und hat keinen betaetigten Wert -, und das
            // heisst OFFEN und nicht UNLESBAR.
            //
            // Die erste Fassung las es als unlesbar, und die Sicherung, die
            // daran haengt, raeumte darauf die Spray-Rastung: Druck rastet ein,
            // Loslassen raeumt. Gemessen 13 Mal, gemeldet als "kein Switch
            // mehr, nur Gedrueckthalten". Die Sicherung gegen einen
            // unentrinnbaren Dauerzustand hatte den Dauerzustand unmoeglich
            // gemacht.
            //
            // UNLESBAR HEISST GENAU ZWEI DINGE: es gibt kein Action-Objekt,
            // oder die Lesung wirft. Beides sind Fehler des LESEWEGS. Eine
            // stumme Action, die nicht null ist, bleibt damit ununterscheidbar
            // von einem offenen Griff - das ist eine echte Grenze dieses
            // Signals und keine, die sich wegdefinieren laesst.
            var value = raw is null ? 0f : raw.Unbox<float>();

            readable = true;
            return value;
        }
        catch
        {
            // Der Leseweg selbst ist gescheitert - hier gehoert false hin,
            // und nur hier sowie beim fehlenden Action-Objekt oben.
            readable = false;
            return 0f;
        }
    }
}
