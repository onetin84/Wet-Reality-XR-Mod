using System.Reflection;
using MelonLoader;

namespace WetReality;

// HAPTISCHES FEEDBACK - Abschnitt 102, Lauf 3.
//
// Gewuenscht war ein Puls, sobald mit einem Objekt interagiert wird, und ein
// zweiter beim Ablegen. Gepulst wird die HANDELNDE Hand: Schulter und Huefte
// rechts, Verlaengerung und animiertes Objekt links, Aufnehmen und Ablegen
// links, weil X am linken Controller sitzt.
//
// WARUM REFLEXION UND KEIN ASSEMBLY-VERWEIS. Die Action-Id und die
// Geraete-Ids entstehen in XRBoots Setup und existieren nur dort. Ein
// Compile-Verweis von Pose auf XRBoot waere eine Ladeabhaengigkeit zwischen
// zwei MelonMods, die MelonLoader in beliebiger Reihenfolge laedt; ein
// fehlendes XRBoot wuerde dann Pose mitnehmen, statt nur die Vibration
// wegzulassen. Ueber Reflexion bleibt der Ausfall lokal.
//
// EINMAL AUFGELOEST UND GECACHED, mit einer Logzeile - und die unterscheidet
// DREI Zustaende, weil sie drei verschiedene Ursachen haben: XRBoot ist nicht
// geladen, XRBoot ist geladen aber zu alt (kein XRHaptics), oder gebunden. Die
// Assets.ps1-Falle aus Abschnitt 100 war genau diese Verwechslung von "nicht
// vorhanden" und "vorhanden und leer".
internal static class Haptics
{
    private const string BridgeAssembly = "WetReality.XRBoot";
    private const string BridgeType = "WetReality.XRHaptics";

    private static Func<bool, float, float, bool>? pulse;
    private static Action<float, bool>? configure;
    private static Func<bool, bool>? stop;
    private static bool resolved;
    private static long pulses;
    private static long refused;

    internal static long Pulses => pulses;
    internal static long Refused => refused;
    internal static bool Bound => pulse is not null;

    // Ein Puls. amplitude und seconds kommen von den Preferences des
    // Aufrufers, damit ein zu langer Brummer per cfg zu kuerzen ist und nicht
    // per Build - 1 s war der Wunsch, 0,25 s der Startwert.
    //
    // Gibt true zurueck, wenn ein Impuls abgeschickt wurde. Der
    // Begruessungspuls braucht diese Auskunft, weil er sonst nicht wissen
    // kann, ob er wiederholt werden muss.
    internal static bool Pulse(MelonLogger.Instance log, bool right,
        float amplitude, float seconds, string why)
    {
        if (!resolved)
        {
            resolved = true;
            Resolve(log);
        }

        var bridge = pulse;

        if (bridge is null)
        {
            refused++;
            return false;
        }

        try
        {
            if (bridge(right, amplitude, seconds))
            {
                pulses++;
                return true;
            }

            refused++;

            // NUR DIE ERSTEN, und dann still. Ein abgelehnter Puls pro Geste
            // waere eine Logzeile pro Geste fuer eine Ursache, die sich nicht
            // aendert - der Zustandsbericht von XRBoot sagt sie ohnehin.
            if (refused <= 3)
            {
                // "haptic ACTIVE" steht hier NICHT mehr als Rat: Lauf 3b hat
                // gemessen, dass GetActionIsActive fuer einen Ausgang nichts
                // aussagt - trigger und squeeze lesen auf demselben Geraet
                // ebenfalls inactive und funktionieren. Der Bindungsbericht
                // von XR Boot ist die Stelle, die etwas sagt.
                log.Msg($"haptics: {(right ? "right" : "left")} refused ({why}). "
                    + "See XR Boot's \"haptic bind:\" lines - they name the bound "
                    + "source per device, with primary_button beside it as the control.");
            }

            return false;
        }
        catch (Exception exception)
        {
            refused++;
            pulse = null;
            log.Warning($"  haptics threw {exception.GetType().Name}: {exception.Message}"
                + " - dropped for this session.");
            return false;
        }
    }

    // Einen laufenden Dauerpuls beenden. Ohne diesen Aufruf laeuft der letzte
    // Impuls nach dem Spruehende noch seine Dauer aus - und das wuerde als
    // "die Vibration haengt" gemeldet, nicht als "der Puls war zu lang".
    internal static bool Stop(MelonLogger.Instance log, bool right)
    {
        if (!resolved)
        {
            resolved = true;
            Resolve(log);
        }

        var bridge = stop;

        if (bridge is null)
            return false;

        try
        {
            return bridge(right);
        }
        catch (Exception exception)
        {
            stop = null;
            log.Warning($"  haptics stop threw {exception.GetType().Name}: "
                + exception.Message);
            return false;
        }
    }

    // Die zwei Schalter aus Lauf 3b an die Bruecke weitergeben. Fehlt
    // Configure, ist XRBoot aelter als dieser Lauf - dann bleibt es bei den
    // Standardwerten, und das ist kein Fehler.
    internal static void Configure(MelonLogger.Instance log, float frequency,
        bool unfiltered)
    {
        if (!resolved)
        {
            resolved = true;
            Resolve(log);
        }

        try
        {
            configure?.Invoke(frequency, unfiltered);
        }
        catch (Exception exception)
        {
            configure = null;
            log.Warning($"  haptics configure threw {exception.GetType().Name}: "
                + exception.Message);
        }
    }

    private static void Resolve(MelonLogger.Instance log)
    {
        try
        {
            Assembly? found = null;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, BridgeAssembly,
                        StringComparison.OrdinalIgnoreCase))
                {
                    found = assembly;
                    break;
                }
            }

            if (found is null)
            {
                log.Msg($"haptics: {BridgeAssembly} is not loaded - no vibration. "
                    + "The pose mod works without it.");
                return;
            }

            var type = found.GetType(BridgeType);

            if (type is null)
            {
                log.Msg($"haptics: {BridgeAssembly} is loaded but carries no {BridgeType} "
                    + "- it predates the haptic action. Update XR Boot.");
                return;
            }

            var method = type.GetMethod("Pulse", BindingFlags.Static | BindingFlags.Public);

            if (method is null)
            {
                log.Msg($"haptics: {BridgeType} carries no public static Pulse - "
                    + "the bridge signature changed.");
                return;
            }

            pulse = (Func<bool, float, float, bool>)Delegate.CreateDelegate(
                typeof(Func<bool, float, float, bool>), method);

            var stopMethod = type.GetMethod("Stop",
                BindingFlags.Static | BindingFlags.Public);

            if (stopMethod is not null)
            {
                stop = (Func<bool, bool>)Delegate.CreateDelegate(
                    typeof(Func<bool, bool>), stopMethod);
            }

            var configureMethod = type.GetMethod("Configure",
                BindingFlags.Static | BindingFlags.Public);

            if (configureMethod is not null)
            {
                configure = (Action<float, bool>)Delegate.CreateDelegate(
                    typeof(Action<float, bool>), configureMethod);
            }

            // Available wird MITGELESEN statt vorausgesetzt: die Bruecke kann
            // gebunden sein, waehrend die Action nicht entstanden ist, und das
            // ist der Fall, den man sonst am Handgelenk sucht.
            var available = type.GetProperty("Available",
                BindingFlags.Static | BindingFlags.Public)?.GetValue(null);

            log.Msg($"haptics: bound to {BridgeType}.Pulse, action "
                + $"{(available is bool ready && ready ? "ready" : "NOT ready - no vibration until XR Boot creates it")}");
        }
        catch (Exception exception)
        {
            log.Warning($"  haptics resolve threw {exception.GetType().Name}: "
                + exception.Message);
        }
    }
}
