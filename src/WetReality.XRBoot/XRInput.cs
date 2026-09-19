using System.Runtime.InteropServices;
using MelonLoader;

namespace WetReality;

// Option O1 from design document section 47: rebuild the OpenXR input layer that
// XR Boot 0.6.0 skipped.
//
// This is the piece everything else turned out to depend on. OpenXR delivers no
// input at all without action sets attached to the session - not controller
// poses, and not the devices Unity's Input System builds its XRController and
// XRHMD from. That is why every binding reported NO CONTROL and why all four
// shortcuts to a pose failed, two of them by crashing the process. See sections
// 45 to 47.
//
// Everything here is plain P/Invoke into UnityOpenXR.dll. The struct marshalling
// problems that killed earlier attempts came from Il2CppInterop, which is not
// involved on this path - these are ordinary .NET structs going to a native DLL.
//
// Every signature and struct layout below is TRANSCRIBED from
// Runtime/input/OpenXRInput.cs in com.unity.xr.openxr@1.18.0. Nothing is
// inferred from a name. Guessing two signatures cost a crash in Pose 0.7.0, and
// judging two methods by their names cost the failures in sections 41 and 45.
internal static class XRInput
{
    private const string Library = "UnityOpenXR";

    // All three accept the Oculus Touch component paths; the Meta and Facebook
    // ones are supersets. Which of them VDXR actually binds is what
    // GetCurrentInteractionProfile now reports.
    private static readonly string[] TouchCompatibleProfiles =
    {
        "/interaction_profiles/oculus/touch_controller",
        "/interaction_profiles/meta/touch_controller_plus",
        "/interaction_profiles/facebook/touch_controller_pro",
    };

    // Every profile that gets both device definitions and suggested bindings.
    // Measured: VDXR binds the last of these for Quest 3 controllers.
    private static readonly string[] AllProfiles =
    {
        "/interaction_profiles/oculus/touch_controller",
        "/interaction_profiles/meta/touch_controller_plus",
        "/interaction_profiles/facebook/touch_controller_pro",
        "/interaction_profiles/khr/simple_controller",
    };

    // The profile the runtime was measured to bind for Quest 3 over VDXR,
    // section 50. Device definitions are registered for this one only.
    internal const string DeviceProfile = "/interaction_profiles/khr/simple_controller";

    // Last path segment, used to give each device definition a distinguishable
    // product name.
    private static string ShortProfileName(string profile)
    {
        var cut = profile.LastIndexOf('/');
        return cut < 0 ? profile : profile[(cut + 1)..];
    }

    // Mandatory for every OpenXR runtime, and therefore the safety net. Its
    // component paths are a far smaller set than Touch: no trigger value and no
    // thumbstick, so only these four actions can bind here.
    private const string SimpleProfile = "/interaction_profiles/khr/simple_controller";

    private static readonly (string Name, string Suffix)[] SimpleBindings =
    {
        ("devicepose", "/input/grip/pose"),
        ("pointer", "/input/aim/pose"),
        ("primary_button", "/input/select/click"),
        ("menu_button", "/input/menu/click"),

        // DIE HAPTIK MUSS HIER STEHEN, und das ist keine Formalie: diese Liste
        // ist eine ausdrueckliche Auswahl nach Action-NAMEN, und VDXR bindet
        // fuer Quest 3 gemessen genau dieses Profil. Ein Name, der hier fehlt,
        // wird fuer das einzige tatsaechlich gebundene Profil nie
        // vorgeschlagen - die Action existierte dann und waere doch stumm.
        //
        // /output/haptic ist im KHR-Profil definiert (KHRSimpleControllerProfile.cs:161),
        // ebenso in oculus/touch und dessen beiden Obermengen.
        ("haptic", "/output/haptic"),
    };

    // Transcribed from OpenXRInteractionFeature.ActionType. Passed to
    // CreateAction as a uint.
    private enum ActionType : uint
    {
        Binary = 0,
        Axis1D = 1,
        Axis2D = 2,
        Pose = 3,
        Vibrate = 4,
    }

    // UnityEngine.XR.InputDeviceCharacteristics, as flags. Spelled out rather
    // than referenced, to keep this file free of UnityEngine types.
    [Flags]
    private enum Characteristics : uint
    {
        HeadMounted = 1,
        HeldInHand = 4,
        TrackedDevice = 32,
        Controller = 64,
        Left = 256,
        Right = 512,
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct SerializedGuid
    {
        [FieldOffset(0)] public ulong Low;
        [FieldOffset(8)] public ulong High;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct SerializedBinding
    {
        public ulong ActionId;
        public string Path;
    }

    [DllImport(Library, EntryPoint = "OpenXRInputProvider_RegisterDeviceDefinition", CharSet = CharSet.Ansi)]
    private static extern ulong RegisterDeviceDefinition(string userPath, string interactionProfile,
        [MarshalAs(UnmanagedType.I1)] bool isAdditive, uint characteristics,
        string name, string manufacturer, string serialNumber);

    [DllImport(Library, EntryPoint = "OpenXRInputProvider_CreateActionSet", CharSet = CharSet.Ansi)]
    private static extern ulong CreateActionSet(string name, string localizedName, SerializedGuid guid);

    [DllImport(Library, EntryPoint = "OpenXRInputProvider_CreateAction", CharSet = CharSet.Ansi)]
    private static extern ulong CreateAction(ulong actionSetId, string name, string localizedName,
        uint actionType, SerializedGuid guid, string[] userPaths, uint userPathCount,
        [MarshalAs(UnmanagedType.I1)] bool isAdditive, string[] usages, uint usageCount);

    [DllImport(Library, EntryPoint = "OpenXRInputProvider_SuggestBindings", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SuggestBindings(string interactionProfile,
        SerializedBinding[] serializedBindings, uint serializedBindingCount);

    [DllImport(Library, EntryPoint = "OpenXRInputProvider_AttachActionSets")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool AttachActionSets();

    // Whether the runtime considers an action active for a device, which is only
    // true once xrSyncActions has run with that action set attached and the
    // binding actually resolved to a physical control.
    //
    // This is the measurement that has not been taken yet, and it splits the
    // remaining question cleanly. The XR layer reports IsTracked true for both
    // hands while the Input System reads nothing from them, section 50. If the
    // actions are active, the runtime is delivering and the fault is entirely on
    // the Input System side. If they are inactive, the bindings never resolved
    // and nothing downstream could work.
    //
    // Note the parameter is a uint, not the ulong used everywhere else - that is
    // how the package declares it, so it is transcribed rather than tidied.
    // Runtime/input/OpenXRInput.cs:831-833.
    [DllImport(Library, EntryPoint = "OpenXRInputProvider_GetActionIsActive", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetActionIsActive(uint deviceId, string name);

    // The bridge lookup, and the last unused export.
    //
    // The package calls it exactly as the Input System side would, at
    // OpenXRInput.cs:762: Internal_GetDeviceId(inputDevice.characteristics,
    // inputDevice.name). So it answers precisely the question left open by
    // section 51 - given an Input System device, which provider device does the
    // native side think it is?
    //
    // A returned 0 means the provider has no mapping for that device, which
    // would explain the whole symptom: controls exist, descriptors match, and no
    // state ever arrives because nothing on the native side knows where to put
    // it.
    //
    // Signature transcribed from Runtime/input/OpenXRInput.cs:856-857. Note the
    // uint return and the characteristics-first parameter order.
    [DllImport(Library, EntryPoint = "OpenXRInputProvider_GetDeviceId", CharSet = CharSet.Ansi)]
    private static extern uint GetDeviceId(uint characteristics, string name);

    // Transkribiert aus OpenXRInput.cs:793-794, Cdecl inklusive - die beiden
    // Haptik-Exporte des Pakets deklarieren eine Aufrufkonvention, waehrend
    // die Importe darueber keine nennen. Genau so wird es uebernommen: eine
    // Signatur wird gelesen, nicht erschlossen. Version 0.7.0 hat zwei
    // Signaturen aus Namen geraten und den Prozess mit dem ersten Aufruf
    // abgeschossen.
    [DllImport(Library, EntryPoint = "OpenXRInputProvider_SendHapticImpulse",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern void SendHapticImpulse(uint deviceId, ulong actionId,
        float amplitude, float frequency, float duration);

    // DIE GEBUNDENE QUELLE EINER ACTION, transkribiert aus
    // OpenXRInput.cs:811-813 samt out IntPtr - die managed Fassung daneben
    // macht daraus einen string, und genau das tut ReadSourceName unten.
    //
    // Das ist das Instrument, das GetActionIsActive fuer einen Ausgang nicht
    // sein kann: es fragt nicht nach einem Zustand, sondern nach der Bindung.
    [DllImport(Library, EntryPoint = "OpenXRInputProvider_TryGetInputSourceName",
        CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool TryGetInputSourceNamePtr(uint deviceId, ulong actionId,
        uint index, uint flags, out IntPtr outName);

    // XrResult.cs: ein Impuls ohne Fokus gibt SessionNotFocused, gilt als
    // Erfolg, und der Controller bekommt nichts. Ohne diese Spalte waere ein
    // stummer Controller von einem nicht fokussierten Fenster nicht zu
    // unterscheiden.
    [DllImport(Library, EntryPoint = "NativeConfig_IsSessionFocused")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool IsSessionFocused();

    // DAS AUFHOEREN, transkribiert aus OpenXRInput.cs:799-800 - Cdecl wie beim
    // Senden. Ein Dauerpuls entsteht durch Nachsenden, also braucht sein Ende
    // einen eigenen Aufruf; sonst laeuft der letzte Impuls nach dem
    // Spruehende noch seine Dauer aus, und genau das wuerde als "die Vibration
    // haengt" gemeldet.
    [DllImport(Library, EntryPoint = "OpenXRInputProvider_StopHaptics",
        CallingConvention = CallingConvention.Cdecl)]
    private static extern void StopHapticsNative(uint deviceId, ulong actionId);

    internal static bool TryStop(MelonLogger.Instance log, bool right)
    {
        if (hapticAction == 0 || !hapticResolved)
            return false;

        try
        {
            var devices = right ? hapticRightIds : hapticLeftIds;

            for (var index = 0; index < devices.Length; index++)
                StopHapticsNative(devices[index].Device, devices[index].Handle);

            return true;
        }
        catch (Exception exception)
        {
            log.Warning($"  haptic stop threw {exception.GetType().Name}: "
                + exception.Message);
            return false;
        }
    }

    // DER HANDLE PRO GERAET, und das ist die Waehrung, in der die
    // Haptik-Aufrufe rechnen.
    //
    // Transkribiert aus OpenXRInput.cs:805-809. Die erste nennt KEINE
    // CharSet-Angabe und die zweite Ansi - genau so uebernommen, denn eine
    // Signatur wird gelesen, nicht vereinheitlicht.
    //
    // Unity ruft die erste ueber den CONTROL-Namen ("haptic") und die zweite
    // ueber den USAGE-Namen ("Haptic"). Welche von beiden dieser Provider
    // beantwortet, sagt der Bericht unten - beide werden gefragt.
    [DllImport(Library, EntryPoint = "OpenXRInputProvider_GetActionIdByControl")]
    private static extern ulong GetActionIdByControl(uint deviceId, string name);

    [DllImport(Library, EntryPoint = "OpenXRInputProvider_GetActionIdByUsageName",
        CharSet = CharSet.Ansi)]
    private static extern ulong GetActionIdByUsage(uint deviceId, string usageName);

    private static ulong ResolveHandle(uint device, string control, string usage)
    {
        try
        {
            var byControl = GetActionIdByControl(device, control);

            if (byControl != 0)
                return byControl;
        }
        catch
        {
            // Der zweite Weg darf es trotzdem versuchen.
        }

        try
        {
            return GetActionIdByUsage(device, usage);
        }
        catch
        {
            return 0;
        }
    }

    // UserPath | InteractionProfile | Component, aus der Flags-Aufzaehlung des
    // Pakets (OpenXRInput.cs:57-79).
    private const uint SourceNameAll = 7;

    private static string ReadSourceName(uint device, ulong action)
    {
        try
        {
            if (!TryGetInputSourceNamePtr(device, action, 0, SourceNameAll, out var pointer))
                return "no source";

            return pointer == IntPtr.Zero
                ? "null"
                : Marshal.PtrToStringAnsi(pointer) ?? "unreadable";
        }
        catch (Exception exception)
        {
            return $"threw {exception.GetType().Name}";
        }
    }

    internal static void ReportDeviceIdMapping(MelonLogger.Instance log)
    {
        log.Msg("  provider device id per Input System device:");

        var right = (uint)(Characteristics.HeldInHand | Characteristics.TrackedDevice
            | Characteristics.Controller | Characteristics.Right);
        var left = (uint)(Characteristics.HeldInHand | Characteristics.TrackedDevice
            | Characteristics.Controller | Characteristics.Left);
        var head = (uint)(Characteristics.HeadMounted | Characteristics.TrackedDevice);

        // The head is included as the control case: it is the one device the
        // Input System demonstrably reads, so whatever it reports here is what a
        // working mapping looks like.
        foreach (var (characteristics, name) in new[]
        {
            (head, "Head Tracking - OpenXR"),
            (left, $"{ShortProfileName(DeviceProfile)} Left"),
            (right, $"{ShortProfileName(DeviceProfile)} Right"),
        })
        {
            try
            {
                var id = GetDeviceId(characteristics, name);
                log.Msg($"    \"{name}\"  characteristics {characteristics}  ->  " +
                    $"{(id == 0 ? "NO MAPPING" : "provider id " + id)}");
            }
            catch (Exception exception)
            {
                log.Msg($"    \"{name}\" threw {exception.GetType().Name}");
            }
        }
    }

    // Reported some seconds after the boot, because an action cannot be active
    // before the first xrSyncActions. Asking too early is the mistake that made
    // the interaction profile read NONE, twice.
    internal static void ReportActionState(MelonLogger.Instance log)
    {
        log.Msg("  action state per hand device:");

        foreach (var (deviceId, label) in HandDevices)
        {
            // All NINE names, not four. The old list checked only the first
            // four, which is why section 74 could not decide whether the pause
            // menu never bound or the runtime swallowed the button - and why
            // squeeze, primary_button and secondary_button had to be proven
            // active from event counters instead of from this line.
            //
            // thumbstick_click matters most here: it is new, and this line is
            // the one place that says whether the runtime accepted it. If it
            // reads inactive on both hands, R3 and L3 are dead and the nozzle
            // scheme needs a different home.
            foreach (var name in new[]
                     {
                         "devicepose", "pointer", "trigger", "thumbstick",
                         "thumbstick_click", "squeeze", "primary_button",
                         "secondary_button", "menu_button",

                         // DER FALSIFIER FUER DIE VIBRATION. Liest sie fuer
                         // beide Haende inactive, hat der Runtime die Bindung
                         // nicht angenommen, und dann vibriert kein Aufruf der
                         // Welt - der Aufrufpfad ist dann nicht die Ursache
                         // und muss nicht gesucht werden.
                         "haptic",
                     })
            {
                try
                {
                    log.Msg($"    device {deviceId,-3} {label,-24} {name,-12} " +
                        $"{(GetActionIsActive(deviceId, name) ? "ACTIVE" : "inactive")}");
                }
                catch (Exception exception)
                {
                    log.Msg($"    device {deviceId,-3} {name,-12} threw {exception.GetType().Name}");
                }
            }
        }

        if (HandDevices.Count == 0)
            log.Warning("    no hand device ids were recorded at setup.");
    }

    // Filled during Setup so the state report can address the devices later.
    private static readonly List<(uint Id, string Label)> HandDevices = new();

    // ========================================================================
    // DER HAPTIK-AUSGANG - Abschnitt 102, Lauf 3.
    //
    // Die Action-Id kommt aus CreateAction, die Geraete-Ids aus
    // RegisterDeviceDefinition. Beides liegt in Setup ohnehin vor, also bleibt
    // die OpenXR-Kenntnis hier und Pose ruft ueber eine Bruecke aus reinen
    // Primitiven - kein Interop-Typ und kein Struct zwischen den zwei Mods.
    //
    // WARUM PRO HAND AUFGELOEST UND GECACHED: Setup registriert acht
    // Geraetedefinitionen, vier Profile mal zwei Haende, und nur das eine
    // Profil, das der Runtime bindet, hat ein lebendes Geraet.
    // GetActionIsActive sagt, welches - und das ist dieselbe Frage, die der
    // Zustandsbericht stellt, also dieselbe Antwortquelle statt einer zweiten.
    private static ulong hapticAction;
    private static ulong controlAction;

    private static ulong ControlActionId() => controlAction;
    // Geraet UND Handle als Paar. Getrennte Listen waeren zwei Zahlen, die man
    // ueber einen Index paart - die Art von Kopplung, die dieses Projekt schon
    // einmal eine Fehldiagnose gekostet hat.
    private static (uint Device, ulong Handle)[] hapticLeftIds =
        Array.Empty<(uint, ulong)>();

    private static (uint Device, ulong Handle)[] hapticRightIds =
        Array.Empty<(uint, ulong)>();

    private static bool hapticResolved;

    // Von Pose gesetzt, damit ein Fehlschlag per cfg zu untersuchen ist statt
    // per Build. 0 Hz heisst XR_FREQUENCY_UNSPECIFIED und ist, was die
    // bequeme Fassung des Pakets sendet; unfiltered schickt den Impuls mit
    // deviceId 0, was Unity selbst tut, wenn kein Geraet genannt ist.
    internal static float HapticFrequency;
    internal static bool HapticUnfiltered;

    internal static bool HapticReady => hapticAction != 0;

    // Gibt true zurueck, wenn ein Impuls abgeschickt wurde. Das ist NICHT
    // dasselbe wie "es hat vibriert": der Export ist void, und ob der Runtime
    // den Impuls ausfuehrt, sagt nur die Hand. Der Unterschied steht hier, weil
    // ein Aufrufer sonst aus true auf Wirkung schliesst.
    internal static bool TryPulse(MelonLogger.Instance log, bool right,
        float amplitude, float seconds)
    {
        if (hapticAction == 0)
            return false;

        try
        {
            if (!hapticResolved)
            {
                hapticResolved = true;
                ResolveHapticDevices(log);
            }

            var devices = right ? hapticRightIds : hapticLeftIds;

            if (devices.Length == 0 && !HapticUnfiltered)
                return false;

            // amplitude geklemmt und duration nicht negativ, wie es die
            // managed Fassung des Pakets vor dem Aufruf auch tut
            // (OpenXRInput.cs:459-460).
            var strength = amplitude < 0f ? 0f : amplitude > 1f ? 1f : amplitude;
            var duration = seconds < 0f ? 0f : seconds;

            if (HapticUnfiltered)
            {
                // deviceId 0: kein Geraetefilter. Unity ruft genau so, wenn
                // SendHapticImpulse ohne InputDevice benutzt wird. Der Handle
                // ist dann der aus CreateAction, weil ohne Geraet keiner pro
                // Geraet aufgeloest werden kann.
                SendHapticImpulse(0, hapticAction, strength, HapticFrequency, duration);
                return true;
            }

            // MEHRERE GERAETE: ein Impuls an ein nicht gebundenes Geraet ist
            // ein Nulleingriff, ein fehlender Impuls an das gebundene kostet
            // einen Lauf. Jedes Paar bringt seinen EIGENEN Handle mit.
            for (var index = 0; index < devices.Length; index++)
            {
                SendHapticImpulse(devices[index].Device, devices[index].Handle,
                    strength, HapticFrequency, duration);
            }

            return true;
        }
        catch (Exception exception)
        {
            log.Warning($"  haptic pulse threw {exception.GetType().Name}: "
                + exception.Message);

            // Einmal und nie wieder: ein werfender Ausgang darf nicht bei jeder
            // Geste eine Warnung schreiben.
            hapticAction = 0;
            return false;
        }
    }

    // Das aktive Geraet pro Hand, und der Log nennt es. Findet sich keins
    // aktives, wird das ERSTE registrierte je Hand genommen und das gesagt -
    // "nichts gefunden" und "gefunden, aber stumm" sind zwei Befunde, und nur
    // der zweite laesst sich am Handgelenk pruefen.
    private static void ResolveHapticDevices(MelonLogger.Instance log)
    {
        var left = new List<(uint, ulong)>();
        var right = new List<(uint, ulong)>();
        var resolvedHandles = 0;

        foreach (var (id, label) in HandDevices)
        {
            var isRight = label.EndsWith("Right", StringComparison.Ordinal);

            // DER HANDLE PRO GERAET. Faellt er auf 0, wird die Id aus
            // CreateAction genommen - sie ist erwiesen die falsche Waehrung,
            // aber ein Aufruf mit ihr ist immer noch besser als keiner, und
            // der Bericht sagt, welcher Fall vorliegt.
            var handle = ResolveHandle(id, "haptic", "Haptic");

            if (handle != 0)
                resolvedHandles++;

            (isRight ? right : left).Add((id, handle == 0 ? hapticAction : handle));

            // Die Kontrolle, mit DERSELBEN Auflösung: primary_button
            // funktioniert nachweislich, also MUSS sie hier einen Handle und
            // eine Quelle nennen. Tut sie es nicht, taugt das Instrument
            // nicht, und die Aussage ueber haptic ist wertlos - das ist der
            // Grund, warum diese Spalte ueberhaupt mitlaeuft.
            var controlHandle = ResolveHandle(id, "primary_button", "PrimaryButton");

            log.Msg($"    haptic bind: device {id,-3} {label,-28}"
                + $"  haptic handle {handle}"
                + $" source \"{(handle == 0 ? "-" : ReadSourceName(id, handle))}\""
                + $"   control(primary_button) handle {controlHandle}"
                + $" source \"{(controlHandle == 0 ? "-" : ReadSourceName(id, controlHandle))}\"");
        }

        hapticLeftIds = left.ToArray();
        hapticRightIds = right.ToArray();

        log.Msg($"  haptic: createAction id {hapticAction}"
            + $"   per-device handles {resolvedHandles} of {HandDevices.Count}"
            + $"   left {hapticLeftIds.Length} device(s)"
            + $"   right {hapticRightIds.Length} device(s)"
            + $"   route {(HapticUnfiltered ? "unfiltered (deviceId 0)" : "per device")}"
            + $"   frequency {HapticFrequency.ToString("0.#")} Hz");

        if (resolvedHandles == 0)
        {
            log.Warning("  haptic: NO per-device handle resolved. That is the currency the "
                + "impulse is paid in - see OpenXRInput.cs:455. With the action-set id "
                + "instead, the call is silent.");
        }

        bool focused;

        try
        {
            focused = IsSessionFocused();
        }
        catch (Exception exception)
        {
            log.Msg($"  haptic: focus read threw {exception.GetType().Name}");
            focused = true;
        }

        if (!focused)
        {
            log.Warning("  haptic: the session is NOT focused. XrResult documents that an "
                + "impulse then returns SessionNotFocused, counts as success, and never "
                + "reaches the controller. Nothing on this side can fix that.");
        }
        else
        {
            log.Msg("  haptic: session focused, so an impulse is allowed to arrive.");
        }

        if (hapticLeftIds.Length == 0 && hapticRightIds.Length == 0)
        {
            log.Warning("  haptic: no hand devices at all were recorded at setup - "
                + "nothing to pulse.");
        }

        // GetActionIsActive steht NICHT mehr in dieser Methode. Sie hat in
        // Lauf 3b fuer trigger und squeeze inactive gemeldet, waehrend beide
        // funktionierten - ein Messwert, der nichts unterscheidet, gehoert
        // nicht in einen Bericht. Im Zustandsbericht bleibt die Zeile stehen,
        // dort ist sie fuer Eingaben belegt.
    }

    // One action, its OpenXR type, the suffix appended to each user path to form
    // the full binding path, and the Input System usages it is tagged with.
    // Localized is the human-readable label, which has no format restrictions.
    // Left and Right are separate because Touch component paths are NOT
    // symmetric. An empty string means the action has no valid path on that
    // hand and is skipped rather than suggested - a single unsupported path
    // makes the runtime reject every binding for the profile.
    private readonly record struct Action(string Name, string Localized, ActionType Type,
        string Left, string Right, string[] Usages)
    {
        internal Action(string name, string localized, ActionType type,
            string suffix, string[] usages)
            : this(name, localized, type, suffix, suffix, usages)
        {
        }
    }

    // A deliberately small set: the poses first, because those are what the
    // project is blocked on, plus trigger and thumbstick so the MVP input from
    // section 12 has something to map onto. Grip pose is included alongside aim
    // pose because the two differ by a wrist offset and it is not yet decided
    // which one the washer should follow.
    // Action NAMES are OpenXR path components and the spec restricts them to
    // lower-case ASCII, digits, dash, underscore and period. Version 0.9.0 used
    // camelCase and CreateAction returned 0 on the very first one, "gripPose".
    // The localized name beside it has no such restriction.
    // The two pose names are NOT free choices, and that cost a full test round.
    //
    // Version 0.12.0 named them grip_pose and aim_pose. The devices appeared and
    // the bindings resolved, yet every read returned null. The reason is in
    // OpenXRInput.cs:112-130: Unity synthesises the standard controls
    // devicePosition, deviceRotation, pointerPosition and pointerRotation as
    // VIRTUAL controls, and looks up their backing feature through a fixed map:
    //
    //   ["deviceposition"] = "devicepose"      ["pointerposition"] = "pointer"
    //   ["devicerotation"] = "devicepose"      ["pointerrotation"] = "pointer"
    //
    // So the controls existed in the generated layout while the features behind
    // them, named grip_pose/position and aim_pose/rotation, did not match. The
    // names have to be exactly "devicepose" and "pointer", lower case, as the
    // package's own KHRSimpleControllerProfile.cs:266-294 spells them.
    //
    // The usages matter for the same reason: "Device" and "Pointer" are what
    // produce the DevicePosition and DeviceRotation usage hints. Tagging every
    // action with LeftHand and RightHand, as 0.12.0 did, produced
    // LeftHandPosition hints instead and no standard control could bind.
    private static readonly string[] DeviceUsage = { "Device" };
    private static readonly string[] PointerUsage = { "Pointer" };
    private static readonly string[] HandUsages = { "LeftHand", "RightHand" };

    // Transkribiert aus KHRSimpleControllerProfile.cs:308. Nicht HandUsages:
    // die Vibrate-Action tragt die Verwendung "Haptic", und daran erkennt der
    // Provider den Ausgang.
    private static readonly string[] HapticUsage = { "Haptic" };

    private static readonly Action[] Actions =
    {
        new("devicepose", "Device Pose", ActionType.Pose, "/input/grip/pose", DeviceUsage),
        new("pointer", "Pointer Pose", ActionType.Pose, "/input/aim/pose", PointerUsage),
        new("trigger", "Trigger", ActionType.Axis1D, "/input/trigger/value", HandUsages),
        new("squeeze", "Squeeze", ActionType.Axis1D, "/input/squeeze/value", HandUsages),
        new("thumbstick", "Thumbstick", ActionType.Axis2D, "/input/thumbstick", HandUsages),
        // X and Y on the left hand, A and B on the right. This asymmetry is the
        // whole of the bug fixed in 0.16.0.
        new("primary_button", "Primary Button", ActionType.Binary,
            "/input/x/click", "/input/a/click", HandUsages),
        new("secondary_button", "Secondary Button", ActionType.Binary,
            "/input/y/click", "/input/b/click", HandUsages),

        // Left hand only. The right controller's counterpart is
        // /input/system/click, which the runtime reserves for itself.
        new("menu_button", "Menu Button", ActionType.Binary,
            "/input/menu/click", "", HandUsages),

        // THE ONE NEW ACTION the whole nozzle scheme needs, and the only one
        // this project has added since 0.16.0.
        //
        // The path is SYMMETRIC - the same component on both hands - and valid
        // on oculus/touch_controller as well as both Touch supersets, so it
        // cannot trigger the atomic rejection of section 56, where one invalid
        // path in a suggestion set discards the ENTIRE set for that profile.
        //
        // khr/simple_controller has no thumbstick at all, and that is handled
        // structurally rather than by luck: SimpleBindings above is an explicit
        // list keyed by action NAME, so a name that is not in it is simply never
        // suggested for that profile. Nothing to guard.
        //
        // COST: a game restart, unavoidably. actionSetsAttached does setup once
        // per session, xrAttachSessionActionSets refuses a second attach, and
        // attaching closes xrSuggestInteractionProfileBindings - so an action
        // added mid-session would leave every binding orphaned.
        new("thumbstick_click", "Thumbstick Click", ActionType.Binary,
            "/input/thumbstick/click", HandUsages),

        // DIE ZWEITE ACTION, DIE DIESES PROJEKT HINZUFUEGT, und die erste, die
        // HINAUS geht statt herein - Abschnitt 102, Lauf 3.
        //
        // Der Pfad ist SYMMETRISCH und auf allen vier Profilen gueltig, kann
        // also die atomare Ablehnung aus Abschnitt 56 nicht ausloesen, bei der
        // ein einziger unguelter Pfad den GANZEN Vorschlag fuer ein Profil
        // verwirft.
        //
        // KOSTEN: ein Spielneustart, unvermeidlich und derselbe Grund wie bei
        // thumbstick_click darueber. actionSetsAttached macht das Setup einmal
        // pro Session, xrAttachSessionActionSets verweigert einen zweiten
        // Anhang, und der Anhang schliesst xrSuggestInteractionProfileBindings.
        new("haptic", "Haptic Output", ActionType.Vibrate,
            "/output/haptic", HapticUsage),
    };

    private static readonly string[] Hands = { "/user/hand/left", "/user/hand/right" };

    // Names must be OpenXR-legal: lowercase, no spaces. Derived from a counter
    // rather than random, because Math.Random is unavailable in this codebase and
    // stable names make two runs comparable in the log.
    private static ulong guidCounter;

    // Set once the action sets are attached, and never cleared.
    //
    // This flag is what makes a resume possible. Since XR Boot 0.18.0 the pause
    // ENDS the session and the resume comes back through Pump and
    // StartSubsystems, which puts Setup on the path a SECOND time - and almost
    // nothing in it survives being run twice. xrAttachSessionActionSets is once
    // per session: a second attach returns XR_ERROR_ACTIONSETS_ALREADY_ATTACHED.
    // Worse, attachment also CLOSES xrSuggestInteractionProfileBindings, so all
    // four SuggestBindings would be refused and the action set and eight actions
    // created alongside them would be orphans attached to nothing. HandDevices
    // would collect a second copy of every hand as well.
    //
    // And nothing in Setup NEEDS re-running after an EndSession/BeginSession
    // cycle: action sets belong to the session OBJECT, not to its begun state,
    // and the object survives the pause by design. Unity's own flag of this name
    // is set on start and cleared only in Deinitialize, never in Stop - it
    // re-begins sessions without re-attaching, and so does this.
    //
    // THE FALSIFIER, one line in the log: if an XrSessionChanged event appears
    // during a resume, then session_CreateSessionIfNeeded built a NEW session,
    // that session has no action sets attached, and this guard is wrong rather
    // than right. The fix in that case is to clear the flag on that event.
    private static bool actionSetsAttached;

    internal static bool Setup(MelonLogger.Instance log)
    {
        if (actionSetsAttached)
        {
            log.Msg("=== OpenXR input layer: attached already, skipped ===");
            log.Msg("  Device definitions, action set, actions and bindings all survive a pause. "
                + "A second attach would be refused and would take the bindings down with it.");
            return true;
        }

        log.Msg("=== OpenXR input layer ===");

        try
        {
            // The device definitions are what the Input System later turns into
            // XRController devices. Without them a binding like
            // <XRController>{RightHand}/devicePosition matches nothing, which is
            // exactly the NO CONTROL from section 45.
            // One device definition per hand AND PER PROFILE.
            //
            // This is the fix for the controllers, and the reason is measured
            // rather than guessed. Version 0.11.0 registered both hands with
            // oculus/touch_controller only, and no controller device ever
            // appeared. Asking the runtime later - once xrSyncActions had run -
            // showed why:
            //
            //   profile /user/hand/right: 0x17  /interaction_profiles/khr/simple_controller
            //
            // VDXR binds the KHR simple controller for Quest 3, not Oculus
            // Touch. A device definition names the profile it belongs to, so a
            // definition claiming Touch can never activate while Simple is the
            // bound profile. The package does the same thing implicitly: each
            // interaction feature registers its own devices with its own
            // profile, see OpenXRInput.cs:251-266 driven by
            // actionMap.desiredInteractionProfile.
            //
            // So all four profiles get definitions. Whichever one the runtime
            // picks now has a matching device waiting.
            // ONE definition per hand, not eight.
            //
            // Version 0.13.0 registered all four profiles for both hands, which
            // made the devices appear at last. But eight competing definitions on
            // two user paths is a shape Unity never produces: there, each enabled
            // interaction feature contributes exactly one device per hand, and a
            // project enables one or two features. Whether the provider maps
            // eight rival definitions sensibly is not something the package ever
            // has to answer.
            //
            // So this narrows to the profile the runtime actually binds, measured
            // in section 50 as khr/simple_controller. Bindings are still suggested
            // for all four, which was measured to be accepted and costs nothing.
            // All four again, not just DeviceProfile. The provider only creates a
            // device for the profile the runtime actually binds, so registering
            // the others costs nothing and registering too few would leave no
            // controller at all the moment Touch takes over from simple_controller.
            foreach (var profile in AllProfiles)
            {
                foreach (var hand in Hands)
                {
                    var isRight = hand.EndsWith("right", StringComparison.Ordinal);
                    var characteristics = Characteristics.HeldInHand | Characteristics.TrackedDevice
                        | Characteristics.Controller | (isRight ? Characteristics.Right : Characteristics.Left);

                    // The name becomes the Input System product name, so it has
                    // to differ per profile or the devices are indistinguishable.
                    var label = $"{ShortProfileName(profile)} {(isRight ? "Right" : "Left")}";

                    var device = RegisterDeviceDefinition(hand, profile, false, (uint)characteristics,
                        label, "OpenXR", string.Empty);

                    log.Msg($"  {(device == 0 ? "refused " : "ok      ")} device {label,-28} id {device}");

                    if (device != 0)
                        HandDevices.Add(((uint)device, label));
                }
            }

            // The head is registered but not actually needed, and that is worth
            // recording: the working head device is the plugin's own
            // "Head Tracking - OpenXR", complete with pose features, not this
            // one. Kept because it costs nothing and documents the asymmetry -
            // the head pose comes from the display subsystem, the hand poses
            // have to come through action bindings.
            var hmd = RegisterDeviceDefinition("/user/head", AllProfiles[0], false,
                (uint)(Characteristics.HeadMounted | Characteristics.TrackedDevice),
                "Head Mounted Display", "OpenXR", string.Empty);
            log.Msg($"  {(hmd == 0 ? "refused " : "ok      ")} device /user/head  id {hmd}");

            var actionSet = CreateActionSet("wetreality", "Wet Reality", NextGuid());
            log.Msg($"  {(actionSet == 0 ? "FAILED  " : "ok      ")} action set  id {actionSet}");
            if (actionSet == 0)
                return false;

            var bindings = new List<SerializedBinding>();
            var actionIds = new Dictionary<string, ulong>(StringComparer.Ordinal);

            foreach (var action in Actions)
            {
                // Usages are what the Input System matches {LeftHand} and
                // {RightHand} against in a binding path.
                var id = CreateAction(actionSet, action.Name, action.Localized, (uint)action.Type,
                    NextGuid(), Hands, (uint)Hands.Length, false,
                    action.Usages, (uint)action.Usages.Length);

                // One bad action no longer aborts the rest. 0.9.0 returned on the
                // first failure and hid whether the others would have worked,
                // which turned a one-line naming mistake into a blind run.
                if (id == 0)
                {
                    log.Error($"  FAILED   action {action.Name,-16} {action.Type}");
                    continue;
                }

                actionIds[action.Name] = id;

                // FESTGEHALTEN, weil der Puls sie spaeter braucht und sie hier
                // entsteht. Ein zweiter Weg zu derselben Id - etwa
                // GetActionIdByControl mit einem geratenen Namen - waere eine
                // Erschliessung, wo ein Rueckgabewert vorliegt.
                if (string.Equals(action.Name, "haptic", StringComparison.Ordinal))
                    hapticAction = id;

                // Die Kontrolle fuer den Bindungsbericht: eine Eingabe, die auf
                // demselben Profil gemessen ACTIVE liest.
                if (string.Equals(action.Name, "primary_button", StringComparison.Ordinal))
                    controlAction = id;

                foreach (var hand in Hands)
                {
                    var suffix = hand.EndsWith("right", StringComparison.Ordinal)
                        ? action.Right
                        : action.Left;

                    if (suffix.Length == 0)
                        continue;

                    bindings.Add(new SerializedBinding { ActionId = id, Path = hand + suffix });
                }

                log.Msg($"  ok       action {action.Name,-16} {action.Type,-7} id {id}");
            }

            // Suggested for several profiles, not one.
            //
            // Version 0.10.0 suggested bindings for oculus/touch_controller
            // alone. The result was that no controller device ever appeared,
            // while the controllers were provably awake - checked with pointer
            // rays in Virtual Desktop before the test. The obvious suspect is
            // that VDXR binds a different profile, in which case bindings for a
            // profile it never selects are simply ignored.
            //
            // The Meta and Facebook Touch profiles accept the same component
            // paths as the Oculus one, being supersets of it, so the same
            // binding list is reused. A request for a profile whose extension is
            // not enabled fails, which is why each one is reported separately
            // and a failure does not abort the rest.
            var anySuggested = false;
            foreach (var profile in TouchCompatibleProfiles)
            {
                var ok = SuggestBindings(profile, bindings.ToArray(), (uint)bindings.Count);
                anySuggested |= ok;
                log.Msg($"  {(ok ? "ok      " : "refused ")} suggest {bindings.Count} binding(s) for {profile}");
            }

            // The KHR simple controller is the one profile every OpenXR runtime
            // must support, so it is the safety net. Its component paths are a
            // much smaller set - no trigger value, no thumbstick - and using the
            // Touch paths here would just be refused.
            var simple = new List<SerializedBinding>();
            foreach (var (name, suffix) in SimpleBindings)
            {
                if (!actionIds.TryGetValue(name, out var id))
                    continue;

                foreach (var hand in Hands)
                    simple.Add(new SerializedBinding { ActionId = id, Path = hand + suffix });
            }

            var simpleOk = SuggestBindings(SimpleProfile, simple.ToArray(), (uint)simple.Count);
            anySuggested |= simpleOk;
            log.Msg($"  {(simpleOk ? "ok      " : "refused ")} suggest {simple.Count} binding(s) for {SimpleProfile}");

            if (!anySuggested)
            {
                log.Error("  every profile refused its bindings. Nothing can bind.");
                return false;
            }

            // The call without which none of the above has any effect.
            var attached = AttachActionSets();
            log.Msg($"  {(attached ? "ok      " : "FAILED  ")} attach action sets");

            // Recorded only on success, so a failed attach can be retried by a
            // resume instead of being permanently skipped.
            actionSetsAttached = attached;
            return attached;
        }
        catch (Exception exception)
        {
            log.Error($"  input layer failed: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private static SerializedGuid NextGuid()
    {
        guidCounter++;
        return new SerializedGuid { Low = 0x57657452_65616C69UL, High = guidCounter };
    }
}
