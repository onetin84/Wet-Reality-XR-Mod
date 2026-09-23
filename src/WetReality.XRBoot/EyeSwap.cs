using System.Globalization;
using System.Runtime.InteropServices;
using MelonLoader;

namespace WetReality;

// DIE AUGEN VERTAUSCHT - Abschnitt 179, ein Diagnoseweg, kein Feature.
//
// Der einaeugige Bodeneffekt sitzt nur im RECHTEN Auge; flach und links sind
// richtig (Abschnitt 177). Unter MultiPass ist das rechte Auge zugleich der
// ZWEITE Durchgang derselben Kamera. Zwei Klassen passen auf dieses Bild, und
// dieser Weg trennt sie:
//
//   bleibt der Effekt im rechten DISPLAY     -> er haengt am zweiten Durchgang
//                                               (Zustand aus dem ersten, Reihen-
//                                               folge, Zielpuffer)
//   wandert er ins linke DISPLAY             -> er haengt an den Daten des
//                                               rechten Auges (Pose, Sichtfeld)
//
// WARUM HIER UND NICHT Camera.SetStereoViewMatrix. URP nimmt unter XR die
// Augenmatrizen vom Display-Subsystem, nicht von der Kamera - derselbe Grund,
// aus dem StereoSeparation "0 of 8" meldete (Abschnitt 168). Das Display-
// Subsystem bekommt sie aus xrLocateViews. Also wird dort getauscht, ueber den
// Weg, den Unitys OpenXR-Features selbst benutzen: ein eigenes
// xrGetInstanceProcAddr in der Feature-Kette, das fuer genau eine Funktion eine
// Huelle zurueckgibt. XRBoot ist diese Kette (siehe Native.cs, LoadStage1).
//
// Getauscht werden Pose und Sichtfeld, Typ und next bleiben. Unity legt die
// Bilder mit denselben Posen in xrEndFrame vor, der Kompositor zeigt also im
// linken Display das Bild, das vom rechten Auge aus gezeichnet wurde. Die
// Tiefe ist dabei verkehrt - zum Pruefen je ein Auge schliessen.
//
// XrView, x64: type u32 @0, next @8, pose @16 (Quaternion 16 + Vektor 12),
// fov @44 (16), Groesse 64. Getauscht werden die 44 Byte ab 16.
internal static class EyeSwap
{
    private const int XrSuccess = 0;
    private const int XrTypeView = 7;
    private const int ViewSize = 64;
    private const int PayloadOffset = 16;
    private const int PayloadSize = 44;
    private const int PositionXOffset = 32;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetInstanceProcAddrFn(ulong instance, IntPtr name, IntPtr function);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int LocateViewsFn(ulong session, IntPtr locateInfo, IntPtr viewState,
        uint capacity, IntPtr countOutput, IntPtr views);

    // Statisch und fuer die ganze Prozesslaufzeit gehalten: die native Seite
    // behaelt die Zeiger, ein eingesammelter Delegate waere ein Absturz im
    // naechsten Frame.
    private static GetInstanceProcAddrFn? realGetProc;
    private static GetInstanceProcAddrFn? getProcHook;
    private static LocateViewsFn? realLocate;
    private static LocateViewsFn? locateHook;
    private static MelonLogger.Instance? log;

    private static long calls;
    private static long swaps;
    private static long skipped;
    private static bool loggedFirst;
    private static bool loggedSkip;

    // Nimmt den Zeiger, den XRBoot sonst unveraendert zurueckgaebe, und gibt
    // den eigenen zurueck.
    internal static IntPtr Wrap(MelonLogger.Instance logger, IntPtr real)
    {
        log = logger;
        realGetProc = Marshal.GetDelegateForFunctionPointer<GetInstanceProcAddrFn>(real);
        getProcHook = GetProcHook;
        locateHook = LocateHook;

        var wrapped = Marshal.GetFunctionPointerForDelegate(getProcHook);
        logger.Msg($"  eye swap: ARMED - xrGetInstanceProcAddr 0x{real.ToInt64():x} wrapped as "
            + $"0x{wrapped.ToInt64():x}. Diagnostic: left and right views are exchanged.");
        return wrapped;
    }

    private static int GetProcHook(ulong instance, IntPtr name, IntPtr function)
    {
        var result = realGetProc!(instance, name, function);

        if (result != XrSuccess || name == IntPtr.Zero || function == IntPtr.Zero)
            return result;

        string? text;
        try { text = Marshal.PtrToStringAnsi(name); }
        catch { return result; }

        if (!string.Equals(text, "xrLocateViews", StringComparison.Ordinal))
            return result;

        var real = Marshal.ReadIntPtr(function);

        if (real == IntPtr.Zero)
            return result;

        realLocate = Marshal.GetDelegateForFunctionPointer<LocateViewsFn>(real);
        Marshal.WriteIntPtr(function, Marshal.GetFunctionPointerForDelegate(locateHook!));

        // DIE HUELLE IST GESETZT, aber das heisst noch nicht, dass sie
        // gerufen wird - darum zaehlt LocateHook, und die erste Tauschzeile
        // ist der eigentliche Beleg.
        log?.Msg($"  eye swap: xrLocateViews 0x{real.ToInt64():x} replaced (instance {instance})");
        return result;
    }

    private static int LocateHook(ulong session, IntPtr locateInfo, IntPtr viewState,
        uint capacity, IntPtr countOutput, IntPtr views)
    {
        var result = realLocate!(session, locateInfo, viewState, capacity, countOutput, views);
        calls++;

        if (result != XrSuccess || capacity < 2 || views == IntPtr.Zero || countOutput == IntPtr.Zero)
            return result;

        try
        {
            var count = Marshal.ReadInt32(countOutput);
            var type0 = Marshal.ReadInt32(views);
            var type1 = Marshal.ReadInt32(views + ViewSize);

            // Nur der erwartete Fall wird angefasst. Alles andere laeuft
            // unveraendert durch und wird einmal benannt.
            if (count != 2 || type0 != XrTypeView || type1 != XrTypeView)
            {
                skipped++;

                if (!loggedSkip)
                {
                    loggedSkip = true;
                    log?.Warning($"  eye swap: left alone - {count} view(s), types {type0}/{type1}, "
                        + $"expected 2 views of type {XrTypeView}");
                }

                return result;
            }

            var leftXBefore = ReadFloat(views + PositionXOffset);
            var rightXBefore = ReadFloat(views + ViewSize + PositionXOffset);

            var buffer = new byte[PayloadSize];
            var other = new byte[PayloadSize];
            Marshal.Copy(views + PayloadOffset, buffer, 0, PayloadSize);
            Marshal.Copy(views + ViewSize + PayloadOffset, other, 0, PayloadSize);
            Marshal.Copy(other, 0, views + PayloadOffset, PayloadSize);
            Marshal.Copy(buffer, 0, views + ViewSize + PayloadOffset, PayloadSize);

            swaps++;

            // ZURUECKGELESEN: die Zeile nennt x beider Augen vor und nach dem
            // Tausch. Stehen sie danach vertauscht da, hat der Tausch gewirkt -
            // und nicht nur der Aufruf stattgefunden.
            if (!loggedFirst)
            {
                loggedFirst = true;
                log?.Msg("  eye swap: first swap   x before L " + F(leftXBefore) + " R " + F(rightXBefore)
                    + "   after L " + F(ReadFloat(views + PositionXOffset))
                    + " R " + F(ReadFloat(views + ViewSize + PositionXOffset)));
            }
            else if (swaps % 5400 == 0)
            {
                log?.Msg($"  eye swap: {swaps} swap(s) in {calls} call(s), {skipped} left alone");
            }
        }
        catch (Exception exception)
        {
            skipped++;

            if (!loggedSkip)
            {
                loggedSkip = true;
                log?.Warning($"  eye swap threw {exception.GetType().Name}: {exception.Message}");
            }
        }

        return result;
    }

    private static float ReadFloat(IntPtr address) =>
        BitConverter.Int32BitsToSingle(Marshal.ReadInt32(address));

    private static string F(float value) => value.ToString("0.0000", CultureInfo.InvariantCulture);
}
