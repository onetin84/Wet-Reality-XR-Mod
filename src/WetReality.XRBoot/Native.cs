using System.Runtime.InteropServices;
using System.Text;

namespace WetReality;

// P/Invoke surface of UnityOpenXR.dll, the native half of Unity's OpenXR
// provider.
//
// PWS2 ships no XR provider, so the managed orchestration layer that normally
// drives these functions - Unity.XR.OpenXR - does not exist in GameAssembly.dll
// and cannot be added to a finished IL2CPP build. It does not have to be: every
// call the managed loader makes is a plain P/Invoke into this native library,
// and a MelonLoader melon is ordinary managed code with full P/Invoke. So this
// mod takes over the loader role.
//
// Entry point names and signatures are transcribed from
// com.unity.xr.openxr@1.18.0, Runtime/OpenXRLoaderBase.cs. Their presence in
// the shipped UnityOpenXR.dll was verified before any of this was written. The
// marshalling attributes are copied deliberately rather than guessed: the bool
// return is U1 and the bool parameter is I1, which is not the default for
// either direction.
internal static class Native
{
    // Resolved from the process DLL search path, which Unity extends with
    // Data/Plugins/x86_64 - exactly where the library was staged.
    private const string Library = "UnityOpenXR";

    // Order matters and is passed by value as an int, so this must mirror
    // OpenXRFeature.NativeEvent exactly, including the gaps in meaning between
    // the setup, runtime and shutdown groups.
    internal enum NativeEvent
    {
        XrSetupConfigValues,
        XrSystemIdChanged,
        XrInstanceChanged,
        XrSessionChanged,
        XrBeginSession,

        XrSessionStateChanged,
        XrChangedSpaceApp,

        XrEndSession,
        XrDestroySession,
        XrDestroyInstance,

        XrIdle,
        XrReady,
        XrSynchronized,
        XrVisible,
        XrFocused,
        XrStopping,
        XrExiting,
        XrLossPending,
        XrInstanceLossPending,
        XrRestartRequested,
        XrRequestRestartLoop,
        XrRequestGetSystemLoop,
        XrExtensionsReady,
    }

    internal delegate void ReceiveNativeEvent(NativeEvent nativeEvent, ulong payload);

    [DllImport(Library, EntryPoint = "main_LoadOpenXRLibrary")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool LoadOpenXRLibrary(byte[] loaderPath);

    [DllImport(Library, EntryPoint = "main_UnloadOpenXRLibrary")]
    internal static extern void UnloadOpenXRLibrary();

    [DllImport(Library, EntryPoint = "NativeConfig_SetCallbacks")]
    internal static extern void SetCallbacks(ReceiveNativeEvent callback);

    [DllImport(Library, EntryPoint = "NativeConfig_SetApplicationInfo", CharSet = CharSet.Ansi)]
    internal static extern void SetApplicationInfo(
        string applicationName, string applicationVersion, uint applicationVersionHash, string engineVersion);

    [DllImport(Library, EntryPoint = "session_SetSuccessfullyInitialized")]
    internal static extern void SetSuccessfullyInitialized([MarshalAs(UnmanagedType.I1)] bool value);

    // These two are the whole of OpenXRFeature.HookGetInstanceProcAddr once the
    // feature chain is empty: fetch the plugin's default xrGetInstanceProcAddr
    // pointer, then hand it straight back.
    //
    // Version 0.2.0 skipped that method as "feature infrastructure" and paid for
    // it. The name of the setter is the giveaway - LoadStage1. Handing the
    // pointer back is what makes the plugin load stage one of the loader and
    // resolve the global, instance-less entry points. Without it the plugin
    // answers its own calls to xrEnumerateApiLayerProperties and
    // xrEnumerateInstanceExtensionProperties with XR_ERROR_FUNCTION_UNSUPPORTED,
    // and Display_Initialize dies on the first one.
    //
    // So this call is mandatory even with no features at all.
    [DllImport(Library, EntryPoint = "NativeConfig_GetProcAddressPtr")]
    internal static extern IntPtr GetProcAddressPtr([MarshalAs(UnmanagedType.I1)] bool loaderDefault);

    [DllImport(Library, EntryPoint = "NativeConfig_SetProcAddressPtrAndLoadStage1")]
    internal static extern void SetProcAddressPtrAndLoadStage1(IntPtr function);

    [DllImport(Library, EntryPoint = "session_InitializeSession")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool InitializeSession();

    [DllImport(Library, EntryPoint = "session_CreateSessionIfNeeded")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool CreateSessionIfNeeded();

    [DllImport(Library, EntryPoint = "session_BeginSession")]
    internal static extern void BeginSession();

    [DllImport(Library, EntryPoint = "session_EndSession")]
    internal static extern void EndSession();

    [DllImport(Library, EntryPoint = "session_DestroySession")]
    internal static extern void DestroySession();

    [DllImport(Library, EntryPoint = "session_RequestExitSession")]
    internal static extern void RequestExitSession();

    [DllImport(Library, EntryPoint = "messagepump_PumpMessageLoop")]
    internal static extern void PumpMessageLoop();

    [DllImport(Library, EntryPoint = "session_RequestOpenXRApiVersion")]
    internal static extern void RequestOpenXRApiVersion(uint major, uint minor, uint patch);

    // The two that open option O3, and the reason they are here at all.
    //
    // Reading the head pose from OpenXR directly needs an XrTime in the
    // session's own time domain, and the only practical source on Windows is
    // XR_KHR_win32_convert_performance_counter. The probe in
    // WetReality.Pose 0.8.0 found it DISABLED, which looked like the end of O3.
    //
    // It is not, because extensions are requested rather than assumed. Unity
    // does this in RequestOpenXRFeatures, which runs AFTER
    // session_InitializeSession - and that ordering is not a mistake: the
    // instance is created asynchronously afterwards. Our own event log proves
    // it, with XrInstanceChanged and XrExtensionsReady arriving some 90 ms after
    // InitializeSession returned. So a request placed there still reaches
    // instance creation.
    //
    // Both signatures transcribed from OpenXRLoaderBase.cs. Guessing one of
    // these cost a crash in Pose 0.7.0.
    [DllImport(Library, EntryPoint = "unity_ext_RequestEnableExtensionString", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool RequestEnableExtensionString(string extensionString);

    [DllImport(Library, EntryPoint = "unity_ext_IsExtensionEnabled", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool IsExtensionEnabled(string extensionName);

    // Which interaction profile the runtime has actually bound for a hand.
    //
    // This is the measurement that replaces a guess. Bindings were suggested for
    // /interaction_profiles/oculus/touch_controller only. If VDXR reports a
    // different profile as current for /user/hand/right, those bindings never
    // become active and no controller device is ever created - which matches the
    // observation exactly: controllers demonstrably awake, yet both device lists
    // contain nothing but the HMD.
    //
    // Profiles are compared as path ids rather than strings: StringToPath turns
    // each candidate into an id, and the ids are compared. That avoids needing
    // PathToString and keeps every value a primitive.
    //
    // Both signatures transcribed from Runtime/Features/OpenXRFeatureInternal.cs:14-20.
    [DllImport(Library, EntryPoint = "Internal_StringToPath")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool StringToPath([MarshalAs(UnmanagedType.LPStr)] string path, out ulong pathId);

    [DllImport(Library, EntryPoint = "Internal_GetCurrentInteractionProfile")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool GetCurrentInteractionProfile(ulong pathId, out ulong interactionProfile);

    // The loader path is a wide string, not an ANSI one, and the native side
    // expects the terminator inside the buffer. Unity switches to UTF32 on
    // Unix; this build is Windows only, so Unicode is correct here.
    internal static byte[] ToWideBytes(string value) => Encoding.Unicode.GetBytes(value + '\0');
}
