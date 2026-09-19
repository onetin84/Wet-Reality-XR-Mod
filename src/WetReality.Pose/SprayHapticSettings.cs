using MelonLoader;
using UnityEngine;

namespace WetReality;

// ALLE ZAHLEN DER STRAHL-HAPTIK AN EINER STELLE - Abschnitt 102, Lauf 4.
//
// Der Nutzer hat ausdruecklich "zentral konfigurierbar" verlangt, damit die
// Werte im Spiel getestet und angepasst werden koennen. Genau das ist hier der
// Zweck: SprayHaptics rechnet, diese Klasse haelt die Regler, und jeder
// einzelne kommt aus MelonPreferences. Eine Zahl, die nur im Code steht, waere
// ein Testlauf pro Korrektur - und ein Lauf kostet einen Neustart.
//
// AUCH DIE AUSGABE UND DER KONTAKTTEST HAENGEN HIER, als Delegates. Damit
// weiss SprayHaptics nichts von der Bruecke zu XR Boot, nichts von
// Preferences und nichts von Physics; es bekommt Zahlen und drei Aufrufe.
internal sealed class SprayHapticSettings
{
    private readonly MelonPreferences_Entry<bool> enabled;
    private readonly MelonPreferences_Entry<float> intensity;
    private readonly MelonPreferences_Entry<float> jet0;
    private readonly MelonPreferences_Entry<float> jet15;
    private readonly MelonPreferences_Entry<float> jet25;
    private readonly MelonPreferences_Entry<float> jet40;
    private readonly MelonPreferences_Entry<float> jetSoap;
    private readonly MelonPreferences_Entry<float> jetDefault;
    private readonly MelonPreferences_Entry<float> turboFactor;
    private readonly MelonPreferences_Entry<float> turboHz;
    private readonly MelonPreferences_Entry<float> turboDepth;
    private readonly MelonPreferences_Entry<string> turboWasher;
    private readonly MelonPreferences_Entry<bool> turboAuto;
    private readonly MelonPreferences_Entry<float> contactFactor;
    private readonly MelonPreferences_Entry<float> contactSmooth;
    private readonly MelonPreferences_Entry<float> refresh;
    private readonly MelonPreferences_Entry<float> breathe;
    private readonly Func<bool> report;
    private readonly Action<float, float> send;
    private readonly Action stop;
    private readonly Func<bool> contactProbe;

    internal SprayHapticSettings(
        MelonPreferences_Entry<bool> enabled,
        MelonPreferences_Entry<float> intensity,
        MelonPreferences_Entry<float> jet0,
        MelonPreferences_Entry<float> jet15,
        MelonPreferences_Entry<float> jet25,
        MelonPreferences_Entry<float> jet40,
        MelonPreferences_Entry<float> jetSoap,
        MelonPreferences_Entry<float> jetDefault,
        MelonPreferences_Entry<float> turboFactor,
        MelonPreferences_Entry<float> turboHz,
        MelonPreferences_Entry<float> turboDepth,
        MelonPreferences_Entry<string> turboWasher,
        MelonPreferences_Entry<bool> turboAuto,
        MelonPreferences_Entry<float> contactFactor,
        MelonPreferences_Entry<float> contactSmooth,
        MelonPreferences_Entry<float> refresh,
        MelonPreferences_Entry<float> breathe,
        Func<bool> report,
        Action<float, float> send,
        Action stop,
        Func<bool> contactProbe)
    {
        this.enabled = enabled;
        this.intensity = intensity;
        this.jet0 = jet0;
        this.jet15 = jet15;
        this.jet25 = jet25;
        this.jet40 = jet40;
        this.jetSoap = jetSoap;
        this.jetDefault = jetDefault;
        this.turboFactor = turboFactor;
        this.turboHz = turboHz;
        this.turboDepth = turboDepth;
        this.turboWasher = turboWasher;
        this.turboAuto = turboAuto;
        this.contactFactor = contactFactor;
        this.contactSmooth = contactSmooth;
        this.refresh = refresh;
        this.breathe = breathe;
        this.report = report;
        this.send = send;
        this.stop = stop;
        this.contactProbe = contactProbe;
    }

    internal bool Enabled => enabled.Value;
    internal float Intensity => Mathf.Max(0f, intensity.Value);
    internal float TurboFactor => turboFactor.Value;
    internal float TurboHz => turboHz.Value;
    internal float TurboDepth => turboDepth.Value;
    internal string TurboWasher => turboWasher.Value ?? "";
    internal bool TurboAuto => turboAuto.Value;
    internal float ContactFactor => Mathf.Clamp01(contactFactor.Value);
    internal float ContactSmooth => contactSmooth.Value;
    internal float Breathe => breathe.Value;
    // Kommt als Funktion herein, damit der DevMode-Deckel des Aufrufers
    // gilt: eine Diagnose, die im Beta mitlaeuft, zahlt Frames fuer eine
    // Frage, die beantwortet ist.
    internal bool Report => report();

    // Untergrenze 10 ms: ein Intervall von 0 waere ein Aufruf pro Frame mit
    // einer Dauer von 0 und damit nichts als Last.
    internal float Refresh => Mathf.Max(0.01f, refresh.Value);

    // DIE EREIGNISIMPULSE sind mit Absicht KEINE Preferences.
    //
    // Sie sind Formen, nicht Geschmack: ein Doppelimpuls mit 30 ms und 60 ms
    // Pause ist als Doppelimpuls erkennbar, mit 300 ms waere er zwei
    // Ereignisse. Ihre STAERKE haengt an der Hauptintensitaet, also traegt der
    // eine Regler, den der Nutzer im Werkzeug bekommt, auch sie mit.
    internal float StartPulse => Mathf.Clamp01(0.50f * Intensity);
    internal float StartSeconds => 0.07f;
    internal float StopPulse => Mathf.Clamp01(0.30f * Intensity);
    internal float StopSeconds => 0.05f;
    internal float JetPulse => Mathf.Clamp01(0.45f * Intensity);
    internal float JetSeconds => 0.04f;
    internal float SwitchPulse => Mathf.Clamp01(0.45f * Intensity);
    internal float SwitchSeconds => 0.03f;
    internal float SwitchGap => 0.06f;
    internal float SoapPulse => Mathf.Clamp01(0.30f * Intensity);
    internal float SoapSeconds => 0.05f;
    internal float SoapGap => 0.08f;

    internal void Send(float amplitude, float seconds) => send(amplitude, seconds);

    internal void Stop() => stop();

    // Trifft der Strahl etwas? Der Aufrufer schiesst den Strahl; hier steht nur
    // die Frage.
    internal bool Contact() => contactProbe();

    internal bool IsSoap(string jet) =>
        jet.Length != 0
        && jet.IndexOf("soap", StringComparison.OrdinalIgnoreCase) >= 0;

    // DIE BASIS NACH DEM NAMEN, und der Name ist der Spruehwinkel.
    //
    // Gemessen: ShortName liest "0", "15", "25", "40" und "Soap". Die Zahlen
    // des Nutzers haengen genau daran - je konzentrierter, desto kraeftiger.
    //
    // Gruppe 2 zaehlt ebenfalls als Seife, weil die Messung Soap dort gefunden
    // hat: waere der Name in einer anderen Sprache oder Fassung anders
    // geschrieben, traegt die Gruppe weiter. Ein unbekannter Name faellt auf
    // die Standardbasis - eine kuenftige Duese fuehlt sich dann mittelmaessig
    // an statt gar nicht.
    internal float JetBase(string jet, int group)
    {
        if (IsSoap(jet) || group == 2)
            return jetSoap.Value;

        if (string.Equals(jet, "0", StringComparison.Ordinal))
            return jet0.Value;

        if (string.Equals(jet, "15", StringComparison.Ordinal))
            return jet15.Value;

        if (string.Equals(jet, "25", StringComparison.Ordinal))
            return jet25.Value;

        if (string.Equals(jet, "40", StringComparison.Ordinal))
            return jet40.Value;

        return jetDefault.Value;
    }
}
