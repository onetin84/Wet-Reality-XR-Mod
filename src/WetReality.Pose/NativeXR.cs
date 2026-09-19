using System.Reflection;
using System.Runtime.InteropServices;

namespace WetReality;

// Option O3 from design document section 46: ask OpenXR for the head pose
// directly, bypassing Il2CppInterop completely.
//
// Why bypass it. Three managed routes to a pose have now failed, two of them by
// hard-crashing the process:
//
//   InputTracking            legacy path, returns identity      section 45
//   InputDevices             struct returned by value, CRASH    section 45
//   XRDisplaySubsystem       GetRenderPass, CRASH, and the      section 46
//                            _Injected static crashes too
//
// Everything here is plain P/Invoke over primitives and out-parameters. No
// il2cpp types, no structs crossing the interop boundary, no generics. That is
// the whole point: this path cannot fail the way the other three did.
//
// The pieces the plugin hands out are enough to talk to OpenXR ourselves:
// the session handle, the app space handle, and xrGetInstanceProcAddr.
internal static class NativeXR
{
    private const string Library = "UnityOpenXR";

    // Must be installed before the first call, exactly as in XR Boot. CoreCLR
    // does not search Data/Plugins/x86_64 for P/Invoke, so a bare
    // DllImport("UnityOpenXR") fails with ERROR_MOD_NOT_FOUND even though the
    // file is right there.
    internal static void InstallResolver(Assembly assembly, string pluginDirectory)
    {
        NativeLibrary.SetDllImportResolver(assembly, (name, _, _) =>
            string.Equals(name, Library, StringComparison.Ordinal)
                && NativeLibrary.TryLoad(Path.Combine(pluginDirectory, "UnityOpenXR.dll"), out var handle)
                    ? handle
                    : IntPtr.Zero);
    }

    // The XrSession the plugin created. A raw ulong, which is what makes this
    // safe to fetch.
    [DllImport(Library, EntryPoint = "OpenXRInputProvider_GetXRSession")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool GetSession(out ulong session);

    // The app space, meaning the tracking origin that poses are expressed
    // relative to. This is the base space for locating the head.
    [DllImport(Library, EntryPoint = "OpenXRInputProvider_GetAppSpace")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool GetAppSpace(out ulong appSpace);

    // Answers whether a given OpenXR extension is enabled on the instance.
    //
    // This decides whether O3 is possible at all. xrLocateSpace needs an XrTime
    // in the session's own time domain, and there is no way to invent one. The
    // only practical source on Windows is
    // XR_KHR_win32_convert_performance_counter, which turns a
    // QueryPerformanceCounter value into an XrTime. The Unity package never
    // calls it, so the question is whether the plugin happens to enable it
    // anyway.
    [DllImport(Library, EntryPoint = "unity_ext_IsExtensionEnabled", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool IsExtensionEnabled(string extensionName);

    // xrGetInstanceProcAddr, with which any OpenXR function can be resolved -
    // including extension functions, which the loader does not export.
    [DllImport(Library, EntryPoint = "NativeConfig_GetProcAddressPtr")]
    internal static extern IntPtr GetProcAddressPtr([MarshalAs(UnmanagedType.I1)] bool loaderDefault);

    // Reported for the record: which runtime is actually on the other end.
    // Expected to be Virtual Desktop, since that is the active OpenXR runtime
    // per HKLM\SOFTWARE\Khronos\OpenXR\1\ActiveRuntime.
    //
    // Both signatures are transcribed from the package, not inferred. Version
    // 0.7.0 declared these two as returning IntPtr directly, guessing from the
    // names, and the very first call took the process down. Every other import
    // in this file was transcribed and every other one worked. The rule is
    // simply: read the declaration, never reason about it.
    [DllImport(Library, EntryPoint = "NativeConfig_GetRuntimeName")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool GetRuntimeName(out IntPtr runtimeNamePtr);

    [DllImport(Library, EntryPoint = "NativeConfig_GetRuntimeVersion")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool GetRuntimeVersion(out ushort major, out ushort minor, out uint patch);

    [DllImport(Library, EntryPoint = "NativeConfig_IsSessionFocused")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool IsSessionFocused();

    // Which interaction profile the runtime has bound per hand.
    //
    // XR Boot asks this too, but it asks in the same frame as AttachActionSets
    // and gets NONE for both hands. That reading is worthless: a profile only
    // becomes current after xrSyncActions has run and the runtime has announced
    // an InteractionProfileChanged event. Exactly the same mistake as reading
    // XRBackend.IsEnabled 100 ms before the session was focused, section 41.
    //
    // Asking again from here is the fix, because F2 happens seconds after F8.
    //
    // Both signatures transcribed from Runtime/Features/OpenXRFeatureInternal.cs:14-20.
    [DllImport(Library, EntryPoint = "Internal_StringToPath")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool StringToPath([MarshalAs(UnmanagedType.LPStr)] string path, out ulong pathId);

    [DllImport(Library, EntryPoint = "Internal_GetCurrentInteractionProfile")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool GetCurrentInteractionProfile(ulong pathId, out ulong interactionProfile);

    internal static string ReadAnsi(IntPtr pointer) =>
        pointer == IntPtr.Zero ? "null" : Marshal.PtrToStringAnsi(pointer) ?? "unreadable";
}
