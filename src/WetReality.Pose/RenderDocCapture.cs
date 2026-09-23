using System.Runtime.InteropServices;
using MelonLoader;
using UnityEngine;

namespace WetReality;

// RENDERDOC-AUFNAHME AUS DEM MOD - Abschnitt 176.
//
// Der einaeugige Bodeneffekt hat dreizehn Kandidaten verbraucht, alle
// ausgeschlossen, jeder aus einer Analogie gewonnen (Abschnitte 163 bis 170).
// Der naechste Schritt war, die Renderpaesse pro Auge mitzuzaehlen - und das
// ist genau, was RenderDoc tut: jeder Zeichenaufruf beider Augen, mit Shader,
// Texturen und Konstanten, und fuer einen Pixel die Liste der Aufrufe, die ihn
// geschrieben haben. Nachsehen statt erschliessen.
//
// WARUM DER MOD AUSLOEST UND NICHT F12. RenderDoc grenzt ein Bild an Present
// ab. OpenXR-Anwendungen praesentieren ihre Augenbilder nicht, und das
// Monitorfenster dieses Spiels zeigt ein Standbild, keinen Spiegel - ob und
// wie oft es praesentiert, ist nicht gemessen. StartFrameCapture und
// EndFrameCapture klammern stattdessen ueber Unitys eigenen Bildtakt, unab-
// haengig vom Fenster. Und niemand muss mit Headset auf dem Kopf eine Taste
// auf dem Fenster treffen: der Spieler schaut auf den Rasen und wartet.
//
// NUR WENN RENDERDOC SCHON IM PROZESS IST. GetModuleHandle, nicht
// LoadLibrary: die DLL muss vor dem Anlegen des D3D11-Geraets injiziert sein,
// also durch den Start aus RenderDoc heraus. Ohne sie ist das hier eine Zeile
// im Log und sonst nichts.
//
// Kein Struct ueber die Grenze: die API ist eine Tabelle von C-Funktions-
// zeigern, gelesen per Marshal.ReadIntPtr und aufgerufen als cdecl-Delegate.
// Die Indizes stammen aus renderdoc_app.h (RENDERDOC_API_1_7_0, dem die 1.6.0
// als typedef entspricht), gezaehlt ab null.
internal sealed class RenderDocCapture
{
    private const int ApiVersion160 = 10600;

    private const int SlotSetCaptureFilePathTemplate = 11;
    private const int SlotGetNumCaptures = 13;
    private const int SlotStartFrameCapture = 19;
    private const int SlotIsFrameCapturing = 20;
    private const int SlotEndFrameCapture = 21;
    private const int SlotSetCaptureTitle = 26;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetApiFn(int version, out IntPtr api);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DeviceWindowFn(IntPtr device, IntPtr window);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint EndFn(IntPtr device, IntPtr window);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint NoArgUintFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StringFn([MarshalAs(UnmanagedType.LPStr)] string text);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    private bool resolved;
    private DeviceWindowFn? start;
    private EndFn? end;
    private NoArgUintFn? isCapturing;
    private NoArgUintFn? numCaptures;
    private StringFn? setTitle;

    private int taken;
    private bool capturing;
    private int framesLeft;
    private float settledFor;
    private float nextAt;

    // Einmal pro Frame aus OnLateUpdate, nach der Menuebestimmung. count 0
    // heisst aus und kostet nichts, nicht einmal die Modulsuche.
    //
    // Gezaehlt wird nur Zeit in der WELT, nicht im Menue: gesucht ist der
    // Rasen, und eine Aufnahme des Pausenmenues waere ein verbrauchter Platz.
    internal void Tick(MelonLogger.Instance log, int count, float interval,
        float settle, int frames, bool inMenu)
    {
        if (count <= 0)
            return;

        if (!resolved)
        {
            resolved = true;
            Resolve(log, count, interval, settle, frames);
        }

        if (start is null || end is null)
            return;

        // Die offene Aufnahme wird IMMER beendet, auch wenn inzwischen ein
        // Menue aufging - eine nie beendete Aufnahme schreibt keine Datei und
        // haelt RenderDoc im Aufnahmemodus.
        if (capturing)
        {
            if (--framesLeft > 0)
                return;

            capturing = false;

            uint ok;
            try { ok = end(IntPtr.Zero, IntPtr.Zero); }
            catch (Exception exception)
            {
                log.Warning($"renderdoc: EndFrameCapture threw {exception.GetType().Name}: "
                    + exception.Message + " - captures off");
                start = null;
                end = null;
                return;
            }

            log.Msg($"renderdoc: capture {taken} of {count} ended   EndFrameCapture {ok}"
                + $"   {(ok == 1 ? "written" : "NOT WRITTEN")}"
                + $"   captures in this session {NumCaptures()}");
            nextAt = Time.unscaledTime + interval;
            return;
        }

        if (taken >= count)
            return;

        if (inMenu)
        {
            settledFor = 0f;
            return;
        }

        // Erst nach einer ruhigen Strecke in der Welt, damit die erste
        // Aufnahme nicht den Frame nach dem Laden oder nach einem Menue trifft.
        settledFor += Time.unscaledDeltaTime;

        if (settledFor < settle || Time.unscaledTime < nextAt)
            return;

        taken++;

        try
        {
            setTitle?.Invoke($"Wet Reality capture {taken} of {count}, frame {Time.frameCount}");
            start(IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception exception)
        {
            log.Warning($"renderdoc: StartFrameCapture threw {exception.GetType().Name}: "
                + exception.Message + " - captures off");
            start = null;
            end = null;
            return;
        }

        // ZURUECKGELESEN, nicht angenommen: StartFrameCapture gibt nichts
        // zurueck, IsFrameCapturing sagt, ob die Aufnahme wirklich laeuft.
        // Mit NULL, NULL waehlt RenderDoc das "aktive" Geraet - bringt VDXR
        // ein eigenes mit, kann das das falsche sein, und genau das wuerde
        // diese Zeile zeigen.
        uint running = 0;
        try { running = isCapturing?.Invoke() ?? 0; } catch { }

        capturing = true;
        framesLeft = Math.Max(1, frames);

        log.Msg($"renderdoc: capture {taken} of {count} started   frame {Time.frameCount}"
            + $"   bracketing {framesLeft} frame(s)   IsFrameCapturing {running}"
            + $"{(running == 1 ? "" : "   NOT CAPTURING - wrong or no active device")}");
    }

    private void Resolve(MelonLogger.Instance log, int count, float interval,
        float settle, int frames)
    {
        try
        {
            var module = GetModuleHandleW("renderdoc.dll");

            if (module == IntPtr.Zero)
            {
                log.Msg("renderdoc: renderdoc.dll is NOT in the process - start the game "
                    + "from RenderDoc (Launch Application) for captures. Nothing else happens.");
                return;
            }

            var getApiAddress = GetProcAddress(module, "RENDERDOC_GetAPI");

            if (getApiAddress == IntPtr.Zero)
            {
                log.Warning("renderdoc: renderdoc.dll is loaded but exports no RENDERDOC_GetAPI");
                return;
            }

            var getApi = Marshal.GetDelegateForFunctionPointer<GetApiFn>(getApiAddress);

            if (getApi(ApiVersion160, out var api) != 1 || api == IntPtr.Zero)
            {
                log.Warning("renderdoc: RENDERDOC_GetAPI refused version 1.6.0 - RenderDoc "
                    + "older than 1.6? Captures off.");
                return;
            }

            start = Slot<DeviceWindowFn>(api, SlotStartFrameCapture);
            end = Slot<EndFn>(api, SlotEndFrameCapture);
            isCapturing = Slot<NoArgUintFn>(api, SlotIsFrameCapturing);
            numCaptures = Slot<NoArgUintFn>(api, SlotGetNumCaptures);
            setTitle = Slot<StringFn>(api, SlotSetCaptureTitle);

            // Ein fester Ort statt RenderDocs Temp-Ordner, damit die Dateien
            // ohne Suchen zu finden sind. Die Vorlage ist ein Praefix; RenderDoc
            // haengt Datum, Uhrzeit und "_frameN.rdc" an.
            var folder = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory,
                "RenderDoc");
            Directory.CreateDirectory(folder);
            Slot<StringFn>(api, SlotSetCaptureFilePathTemplate)(Path.Combine(folder, "wetreality"));

            log.Msg($"renderdoc: API 1.6.0 bound   {count} capture(s), first after {settle:0.#} s "
                + $"in the world, then every {interval:0.#} s, {Math.Max(1, frames)} frame(s) each"
                + $"   files under {folder}");
        }
        catch (Exception exception)
        {
            log.Warning($"renderdoc: binding threw {exception.GetType().Name}: {exception.Message}");
            start = null;
            end = null;
        }
    }

    private uint NumCaptures()
    {
        try { return numCaptures?.Invoke() ?? 0; }
        catch { return 0; }
    }

    private static T Slot<T>(IntPtr api, int index) where T : Delegate
    {
        var pointer = Marshal.ReadIntPtr(api, index * IntPtr.Size);

        if (pointer == IntPtr.Zero)
            throw new InvalidOperationException($"slot {index} is empty");

        return Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }
}
