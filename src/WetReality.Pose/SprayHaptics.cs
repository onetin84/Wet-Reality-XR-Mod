using MelonLoader;
using UnityEngine;

namespace WetReality;

// HAPTIK DES STRAHLS - Abschnitt 102, Lauf 4.
//
// Ziel des Nutzers: ohne Blick auf die Oberflaeche erfuehlen, welcher Strahl
// aktiv ist, ob Turbo laeuft, ob Seife an ist, und ob der Strahl eine
// Oberflaeche trifft.
//
// DREI GLIEDER, NICHT FUENF, und das ist eine Messung und keine Vereinfachung.
// Der Entwurf nahm Strahlart, Betriebsart und Seife als drei unabhaengige
// Achsen an. Gemessen sind sie das nicht:
//
//   nozzle 0 / 15 / 25 / 40   Gruppe 0   ShortName IST der Spruehwinkel
//   nozzle Soap               Gruppe 2   Seife ist eine DUESE
//   washer UXLight/PVLight               Turbo ist ein REINIGER
//
// Seife ist also keine Betriebsart ueber einem 0-Grad-Strahl: ist sie aktiv,
// gibt es keinen Winkelstrahl, den ein Faktor 0,65 multiplizieren koennte. Sie
// bekommt eine eigene Basis. Turbo dagegen IST orthogonal, weil er am Reiniger
// haengt und nicht an der Duese - dort gilt der Entwurf unveraendert.
//
// DER DAUERPULS ENTSTEHT DURCH NACHSENDEN. Ein neuer xrApplyHapticFeedback auf
// derselben Action ersetzt den laufenden; gesendet wird mit einer Dauer, die
// laenger ist als das Intervall, damit ein verspaeteter Frame keine Luecke
// hinterlaesst. Das Aufhoeren braucht einen eigenen Aufruf, sonst laeuft der
// letzte Impuls nach dem Spruehende aus.
//
// EIN SEGMENT-STAPEL, WEIL ES SONST ZWEI ABSENDER GAEBE. Die kurzen
// Ereignisimpulse und die Gesten-Pulse aus Lauf 3 muessen den Dauerpuls
// ueberlagern. Liefen sie daneben, wuerde die naechste Nachsendung sie nach
// Millisekunden ueberschreiben - ein Impuls, der im Code steht und in der Hand
// nicht ankommt. Alles, was die PISTOLENHAND betrifft, geht darum durch diese
// Warteschlange. Die freie Hand hat keinen Dauerpuls und braucht sie nicht.
internal sealed class SprayHaptics
{
    // Ein Abschnitt erzwungener Amplitude. Amplitude 0 heisst STILLE, und die
    // ist absichtlich moeglich: die Pause in einem Doppelimpuls muss auch
    // waehrend des Spruehens als Pause zu fuehlen sein.
    private readonly List<(float Until, float Amplitude, float Seconds)> segments = new();

    private bool washing;
    private bool washedLast;
    private float contact;
    private float nextSend;
    private bool sending;
    private float lastSentAmplitude;
    private float nextReport;

    // DIE TURBO-MESSUNG, mit einem kurzen Nachhall.
    //
    // TurboRotation dreht sich, solange der Strahl rotiert. Zwei Frames mit
    // demselben Wert - eine Pause, ein Frame ohne Fortschritt - duerfen den
    // Zustand aber nicht umwerfen, sonst flackert das Muster. Der Nachhall
    // haelt ihn eine halbe Sekunde.
    private float lastTurboRotation;
    private float turboSeenUntil;
    private bool turboLatched;

    // Der Wechsel wird am POINTER erkannt, nicht am Namen: Il2CppInterop gibt
    // bei jedem Zugriff einen frischen Wrapper heraus, und ein String pro Frame
    // waere eine Allokation pro Frame fuer eine Zahl, die sich bei einem
    // Tastendruck aendert. Dieselbe Technik wie bei der Zielverfolgung.
    private IntPtr nozzlePointer;
    private IntPtr washerPointer;
    private string jetName = "";
    private string washerName = "";
    private int nozzleGroup = -1;
    private bool sawConfiguration;

    internal string JetName => jetName;
    internal string WasherName => washerName;
    internal bool Washing => washing;

    internal void Reset()
    {
        segments.Clear();
        washing = false;
        washedLast = false;
        contact = 0f;
        nextSend = 0f;
        sending = false;
        lastSentAmplitude = 0f;
        nozzlePointer = IntPtr.Zero;
        washerPointer = IntPtr.Zero;
        jetName = "";
        washerName = "";
        nozzleGroup = -1;
        sawConfiguration = false;
        lastTurboRotation = 0f;
        turboSeenUntil = 0f;
        turboLatched = false;
    }

    // Ein einzelner Impuls auf der Pistolenhand, durch die Warteschlange.
    //
    // nextSend wird genullt, damit er im naechsten Frame gesendet wird und
    // nicht erst beim naechsten Takt des Dauerpulses - ein Bestaetigungsimpuls,
    // der 60 ms zu spaet kommt, ist kein Bestaetigungsimpuls.
    internal void Queue(float amplitude, float seconds)
    {
        var start = Mathf.Max(LastUntil(), Time.unscaledTime);
        segments.Add((start + seconds, amplitude, seconds));
        nextSend = 0f;
    }

    internal void QueueGap(float seconds)
    {
        var start = Mathf.Max(LastUntil(), Time.unscaledTime);
        segments.Add((start + seconds, 0f, seconds));
    }

    private float LastUntil()
    {
        var last = 0f;

        for (var index = 0; index < segments.Count; index++)
        {
            if (segments[index].Until > last)
                last = segments[index].Until;
        }

        return last;
    }

    // Einmal pro Frame, hinter DriveRay: dort hat WashProbe in diesem Frame
    // schon gelesen, und die Spielwerte kommen damit aus demselben Block wie
    // die Entscheidung.
    internal void Drive(MelonLogger.Instance log, SprayHapticSettings settings,
        Il2CppFuturLab.PW2.EquipmentManager? equipment, bool isWashing,
        float hitDistance, float turboRotation, bool active)
    {
        try
        {
            if (!active || !settings.Enabled)
            {
                // Nur EINMAL anhalten, nicht pro Frame: ein Stop-Aufruf je
                // Frame waere 90 Aufrufe pro Sekunde fuer einen Zustand, der
                // sich nicht aendert.
                if (sending)
                {
                    settings.Stop();
                    sending = false;
                    lastSentAmplitude = 0f;
                }

                segments.Clear();
                washedLast = isWashing;
                return;
            }

            // ZUERST die Turbo-Messung, damit ReadConfiguration und die
            // Amplitude denselben Zustand sehen.
            if (Mathf.Abs(turboRotation - lastTurboRotation) > 0.0001f)
                turboSeenUntil = Time.unscaledTime + 0.5f;

            lastTurboRotation = turboRotation;
            turboLatched = Time.unscaledTime < turboSeenUntil;

            ReadConfiguration(log, settings, equipment);

            washing = isWashing;

            if (washing != washedLast)
            {
                washedLast = washing;

                // Anlauf- und Abschlussimpuls. Der Abschluss ist schwaecher,
                // weil er ein Ende bestaetigt und keine Aufmerksamkeit will.
                if (washing)
                    Queue(settings.StartPulse, settings.StartSeconds);
                else
                    Queue(settings.StopPulse, settings.StopSeconds);
            }

            // DER KONTAKT, zeitlich geglaettet statt geschaltet. Ein Sprung
            // von 0,78 auf 1,00 beim Ueberstreichen einer Kante waere ein
            // Klacken, und gewuenscht war ein weicher Uebergang.
            //
            // DAS SIGNAL IST JETZT EIN EIGENER STRAHL, und der vorige war
            // keiner: m_lastCrosshairHitDistance las konstant 100, auch am
            // Wandkontakt, ueber jede Stichprobe eines ganzen Laufs. Der
            // Rohwert laeuft im Bericht weiter mit - als Beleg, nicht als
            // Eingabe.
            var hit = settings.Contact();
            var target = hit ? 1f : 0f;
            var step = settings.ContactSmooth <= 0.001f
                ? 1f
                : Mathf.Clamp01(Time.unscaledDeltaTime / settings.ContactSmooth);

            contact += (target - contact) * step;

            var amplitude = SteadyAmplitude(settings);
            var interval = settings.Refresh;

            // EIN SEGMENT GEWINNT, solange es laeuft.
            var now = Time.unscaledTime;

            while (segments.Count > 0 && segments[0].Until <= now)
                segments.RemoveAt(0);

            var seconds = 0f;

            if (segments.Count > 0)
            {
                amplitude = segments[0].Amplitude;
                seconds = segments[0].Seconds;
                interval = Mathf.Max(0.01f, segments[0].Until - now);
            }
            else if (IsTurbo(settings))
            {
                // Das Muster, und es MUSS das Nachsende-Intervall bestimmen:
                // eine Amplitude, die sich alle 33 ms aendern soll, aber nur
                // alle 60 ms gesendet wird, ergibt kein Rattern, sondern
                // Zufall.
                var halfPeriod = 1f / Mathf.Max(1f, settings.TurboHz * 2f);
                var phase = Mathf.FloorToInt(now / halfPeriod) & 1;

                amplitude *= phase == 0 ? 1f : Mathf.Clamp01(settings.TurboDepth);
                interval = halfPeriod;
            }
            else if (settings.Breathe > 0.0001f)
            {
                // Die "natuerliche Schwankung": langsam und flach, ausdruecklich
                // NICHT als Pulsieren wahrnehmbar.
                amplitude *= 1f + (settings.Breathe * Mathf.Sin(now * 0.7f * 2f * Mathf.PI));
            }

            amplitude = Mathf.Clamp01(amplitude);

            if (amplitude <= 0.001f)
            {
                if (sending)
                {
                    settings.Stop();
                    sending = false;
                    lastSentAmplitude = 0f;
                }

                return;
            }

            if (now >= nextSend)
            {
                // Dauer laenger als das Intervall, damit ein verspaeteter Frame
                // keine Luecke hinterlaesst. Bei einem Segment ist die Dauer
                // seine eigene Laenge.
                var duration = seconds > 0.001f ? seconds : interval * 2.5f;

                settings.Send(amplitude, duration);
                lastSentAmplitude = amplitude;
                sending = true;
                nextSend = now + interval;
            }

            if (settings.Report && now >= nextReport)
            {
                nextReport = now + 1f;

                log.Msg($"spray haptics: jet \"{jetName}\" group {nozzleGroup}"
                    + $"   washer \"{washerName}\""
                    + $"   turbo {(IsTurbo(settings) ? "YES" : "no")}"
                    + $" (rot {turboRotation:0.##} latched {(turboLatched ? "Y" : "n")})"
                    + $"   washing {(washing ? "YES" : "no")}"
                    + $"   hitDist {hitDistance:0.##}"
                    + $"   contact {contact:0.##}"
                    + $"   amp {lastSentAmplitude:0.###}"
                    + $"   interval {interval * 1000f:0} ms"
                    + $"   segments {segments.Count}");
            }
        }
        catch (Exception exception)
        {
            log.Warning($"  spray haptics threw {exception.GetType().Name}: "
                + exception.Message);

            segments.Clear();
        }
    }

    // Basis x Turbo x Kontakt x Intensitaet. Ohne Muster und ohne Schwankung -
    // die kommen beim Aufrufer dazu, damit ein Segment sie nicht mitbekommt.
    private float SteadyAmplitude(SprayHapticSettings settings)
    {
        if (!washing)
            return 0f;

        var basis = settings.JetBase(jetName, nozzleGroup);

        if (IsTurbo(settings))
            basis *= settings.TurboFactor;

        var contactFactor = settings.ContactFactor
            + ((1f - settings.ContactFactor) * Mathf.Clamp01(contact));

        return basis * contactFactor * settings.Intensity;
    }

    // Turbo am REINIGERNAMEN, ueber einen konfigurierten Namensteil.
    //
    // Gemessen sind zwei Reiniger, CPW_PW2_GD_UXLight und CPW_PW2_GD_PVLight,
    // beide der Klasse Light - nichts an den Daten sagt, welcher der Turbo ist.
    // Der Log nennt in jedem Block den aktuellen Namen, also genuegt eine
    // cfg-Zeile statt eines Messlaufs. Leerer Namensteil heisst: kein Turbo,
    // und dann traegt jeder Reiniger die Duesenbasis.
    private bool IsTurbo(SprayHapticSettings settings)
    {
        var needle = settings.TurboWasher;

        if (needle.Length != 0
            && washerName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            // Der Name UEBERSTEUERT, damit ein Reiniger, dessen Duese nicht
            // rotiert, trotzdem als Turbo gelten kann.
            return true;
        }

        return settings.TurboAuto && turboLatched;
    }

    // Duese und Reiniger, gelesen ueber gecachte Handles und verglichen am
    // Pointer. Die Strings entstehen nur beim WECHSEL.
    private void ReadConfiguration(MelonLogger.Instance log,
        SprayHapticSettings settings, Il2CppFuturLab.PW2.EquipmentManager? equipment)
    {
        if (equipment is null || equipment == null)
            return;

        var configuration = equipment.ConfigurationManager?.CurrentConfiguration;

        if (configuration is null)
            return;

        var nozzle = configuration.Nozzle;
        var type = nozzle?.NozzleType;
        // PressureGun, nicht PowerWasher: so heisst das Feld auf
        // WasherConfiguration, und DescribeConfiguration liest es schon so.
        var washer = configuration.PressureGun;

        var nozzleNow = type is null || type == null ? IntPtr.Zero : type.Pointer;
        var washerNow = washer is null || washer == null ? IntPtr.Zero : washer.Pointer;

        if (nozzleNow == nozzlePointer && washerNow == washerPointer)
            return;

        var jetBefore = jetName;
        var washerBefore = washerName;
        var firstRead = !sawConfiguration;

        nozzlePointer = nozzleNow;
        washerPointer = washerNow;
        sawConfiguration = true;

        jetName = type is null || type == null ? "" : type.ShortName ?? "";
        nozzleGroup = type is null || type == null ? -1 : type.NozzleGroup;
        washerName = washer is null || washer == null ? "" : washer.name ?? "";

        // DIE ZAHLEN DES SPIELS, mitgeloggt und NICHT benutzt.
        //
        // Wucht und Strahlbreite stehen als Daten bereit: PlayerPushPower,
        // ObjectPushPower, Range am Reiniger, ReticleMin/MaxSizeModifier und
        // HasMultipleNozzles an der Duese. Traegt eine davon, kann die
        // Namenstabelle spaeter durch eine Rechnung ersetzt werden - und die
        // Messung dafuer faellt hier kostenlos an, statt einen eigenen Lauf zu
        // kosten. Nur beim Wechsel, also nie in einem Frame-Pfad.
        try
        {
            var cleaning = washer?.CleaningPower;

            log.Msg($"washer config: jet \"{jetName}\" group {nozzleGroup}"
                + $"   washer \"{washerName}\""
                + $"   class {(washer is null ? "-" : washer.Class.ToString())}"
                + $"   range {(cleaning is null ? -1f : cleaning.Range):0.##}"
                + $"   pushPlayer {(cleaning is null ? -1f : cleaning.PlayerPushPower):0.##}"
                + $"   pushObject {(cleaning is null ? -1f : cleaning.ObjectPushPower):0.##}"
                + $"   reticle {(type is null ? -1f : type.ReticleMinSizeModifier):0.##}"
                + $" to {(type is null ? -1f : type.ReticleMaxSizeModifier):0.##}"
                + $"   multiNozzle {(type is null ? "?" : type.HasMultipleNozzles.ToString())}"
                + $"   turboNeedle \"{settings.TurboWasher}\""
                + $"   -> base {settings.JetBase(jetName, nozzleGroup):0.###}");
        }
        catch (Exception exception)
        {
            log.Warning($"  washer config read threw {exception.GetType().Name}: "
                + exception.Message);
        }

        // Der erste Lesevorgang ist kein WECHSEL. Ohne diese Unterscheidung
        // pulst jeder Auftragsstart einen Bestaetigungsimpuls fuer etwas, das
        // der Spieler nicht getan hat.
        if (firstRead)
            return;

        var soapBefore = settings.IsSoap(jetBefore);
        var soapNow = settings.IsSoap(jetName);

        if (soapBefore != soapNow)
        {
            // Weicher Doppelimpuls.
            Queue(settings.SoapPulse, settings.SoapSeconds);
            QueueGap(settings.SoapGap);
            Queue(settings.SoapPulse, settings.SoapSeconds);
        }
        else if (!string.Equals(washerBefore, washerName, StringComparison.Ordinal))
        {
            // Reinigerwechsel, also normal/turbo: kurzer Doppelimpuls.
            Queue(settings.SwitchPulse, settings.SwitchSeconds);
            QueueGap(settings.SwitchGap);
            Queue(settings.SwitchPulse, settings.SwitchSeconds);
        }
        else if (!string.Equals(jetBefore, jetName, StringComparison.Ordinal))
        {
            // Strahlwechsel: ein kurzer, praeziser Impuls.
            Queue(settings.JetPulse, settings.JetSeconds);
        }
    }
}
