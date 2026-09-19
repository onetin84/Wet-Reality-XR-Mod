using MelonLoader;

namespace WetReality;

// DIE BRUECKE ZWISCHEN DEN ZWEI MODS - Abschnitt 102, Lauf 3.
//
// Pose loest die Gesten aus, XRBoot besitzt die OpenXR-Session. Die Action-Id
// und die Geraete-Ids entstehen in XRInput.Setup und existieren nur dort, also
// bleibt die OpenXR-Kenntnis hier und Pose ruft herueber.
//
// ABSICHTLICH NUR PRIMITIVE: bool, float, float, bool zurueck. Pose findet
// diesen Typ per Reflexion ueber die geladenen Assemblies und bindet ein
// Delegate darauf; eine Signatur aus Primitiven kann dabei nicht
// mis-marshallen, und es entsteht kein Verweis von Pose auf XRBoot, den
// MelonLoader zur Ladezeit aufloesen muesste. Faellt XRBoot weg, bleibt die
// Bindung einfach aus - und Pose sagt das ins Log, statt zu scheitern.
//
// DIESE KLASSE IST public, waehrend der Rest dieses Mods internal ist. Das ist
// der Grund: eine Reflexion auf einen internal-Typ braeuchte NonPublic-Flags,
// und dann waere jede Umbenennung im Inneren dieses Mods eine stille
// Bruchstelle in einem anderen.
public static class XRHaptics
{
    // Eigener Logger statt eines von XRBoot durchgereichten: die Bruecke soll
    // ohne Initialisierungsschritt benutzbar sein. Wer sie ruft, hat keinen
    // Grund zu wissen, ob XRBoot seine Instanz schon gebaut hat.
    private static readonly MelonLogger.Instance Log = new("Wet Reality Haptics");

    // True, sobald die Vibrate-Action angelegt ist. Sie sagt NICHT, dass der
    // Runtime die Bindung angenommen hat - das steht im Zustandsbericht von
    // XRInput als "haptic ACTIVE" oder "inactive", und zwei Zustaende, die
    // man verwechselt, kosten in diesem Projekt einen Lauf.
    public static bool Available => XRInput.HapticReady;

    // Gibt true zurueck, wenn ein Impuls abgeschickt wurde - nicht, dass er
    // gefuehlt wurde. Der OpenXR-Export ist void.
    public static bool Pulse(bool right, float amplitude, float seconds) =>
        XRInput.TryPulse(Log, right, amplitude, seconds);

    // Zwei Schalter fuer die Untersuchung aus Lauf 3b, beide primitiv und
    // beide von Pose aus einer Preference gesetzt: frequency 0 heisst
    // XR_FREQUENCY_UNSPECIFIED, und unfiltered schickt den Impuls mit
    // deviceId 0 statt an die Geraeteliste - der Weg, den Unity selbst nimmt,
    // wenn kein Geraet genannt ist.
    // Beendet einen laufenden Puls. Nur sinnvoll, wenn zuvor gesendet wurde -
    // vorher ist nichts aufgeloest, und dann sagt es false statt zu werfen.
    public static bool Stop(bool right) => XRInput.TryStop(Log, right);

    public static void Configure(float frequency, bool unfiltered)
    {
        XRInput.HapticFrequency = frequency;
        XRInput.HapticUnfiltered = unfiltered;
    }
}
