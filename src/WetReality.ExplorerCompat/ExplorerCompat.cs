using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MelonLoader;
using MelonLoader.Utils;

[assembly: MelonInfo(typeof(WetReality.ExplorerCompat), "Wet Reality Explorer Compatibility", "0.4.0", "Wet Reality")]
[assembly: MelonGame("FuturLab", "PowerWash Simulator 2")]
[assembly: MelonPriority(-1000)]

namespace WetReality;

// UniverseLib 1.6.2 cannot load its UI AssetBundle on Unity 6, which crashed
// PWS2 on the first modded start (diagnostics/2026-09-13-first-crash).
//
// UniverseLib.AssetBundle.LoadFromMemory asks for the icall
// "UnityEngine.AssetBundle::LoadFromMemory_Internal" and invokes it as
// (IntPtr binary, uint crc). Unity 6 no longer exports that function. The real
// export is LoadFromMemory_Internal_Injected(ref ManagedSpanWrapper, uint).
// MelonLoader's resolver hides the difference by registering a managed
// fallback for the missing name, which the probe below makes visible as a
// "Registered mono icall UnityEngine.AssetBundle::LoadFromMemory_Internal"
// line. UniverseLib then calls that managed thunk through a mismatched
// delegate. The resulting access violation named coreclr.dll as the faulting
// module, which fits a bad thunk call rather than a defect in .NET.
//
// UniverseLib cannot correct this itself: ICallManager.GetICallUnreliable
// builds its "_Injected" candidates with loopSig.Concat(...) but discards the
// result, so only the two literal signatures are ever tried.
//
// MelonLoader's generated interop bindings are not usable as a replacement
// either. AssetBundle survives in this build, but the generated span
// marshalling is broken: the generated LoadFromMemory throws
// ObjectCollectedException and the generated LoadFromFile throws
// MissingMethodException for ReadOnlySpan.GetPinnableReference. Version 0.3.0
// established that, so the injected export is called directly here.
public sealed class ExplorerCompat : MelonMod
{
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ManagedSpan
    {
        public byte* Begin;
        public int Length;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr LoadFromMemoryInjected(ref ManagedSpan binary, uint crc);

    private const string MemoryInjected = "UnityEngine.AssetBundle::LoadFromMemory_Internal_Injected";
    private const string FileInjected = "UnityEngine.AssetBundle::LoadFromFile_Internal_Injected";
    private const string FileTruncated = "UnityEngine.AssetBundle::LoadFromFile_Internal_Injecte";

    private static MelonLogger.Instance log = null!;

    // Held for the lifetime of the process so the thunk is never collected.
    private static LoadFromMemoryInjected? loadFromMemory;
    private static bool probed;

    public override void OnInitializeMelon()
    {
        log = LoggerInstance;

        // Unity is not touched here; this runs before UniverseLib initializes.
        var target = typeof(UniverseLib.AssetBundle).GetMethod("LoadFromMemory", new[] { typeof(byte[]), typeof(uint) })
            ?? throw new MissingMethodException("UniverseLib.AssetBundle.LoadFromMemory(byte[], uint) not found.");

        var prefix = typeof(ExplorerCompat).GetMethod(nameof(LoadPrefix), BindingFlags.NonPublic | BindingFlags.Static)!;
        HarmonyInstance.Patch(target, prefix: new HarmonyMethod(prefix));
        log.Msg("Redirected UniverseLib AssetBundle memory loading to the injected Unity 6 export.");
    }

    private static bool LoadPrefix(byte[] binary, uint crc, ref UniverseLib.AssetBundle? __result)
    {
        ArgumentNullException.ThrowIfNull(binary);

        if (!probed)
        {
            probed = true;
            try { Probe(binary.Length); }
            catch (Exception exception) { log.Warning($"Could not probe the AssetBundle icalls: {exception}"); }
        }

        __result = FromMemory(binary, crc) ?? FromFile(binary, crc);
        if (__result is null)
            log.Error("No AssetBundle loading strategy produced a bundle. The UniverseLib UI stays unstyled.");

        // The original method would now call the mismatched managed thunk.
        return false;
    }

    private static void Probe(int length)
    {
        log.Msg($"Unity {UnityEngine.Application.unityVersion} is loading a {length} byte UniverseLib UI bundle.");

        // Resolving a name MelonLoader has to fake emits its own registration
        // line, which is how a genuine export is told from a fallback.
        foreach (var signature in new[]
        {
            MemoryInjected,
            FileInjected,
            // The truncated spellings that UniverseLib.AssetBundle.LoadFromFile
            // actually passes. If these resolve to the same pointer as the full
            // name, il2cpp matches icall names by prefix and that method takes
            // its injected path rather than its crashing legacy one.
            FileTruncated,
            "UnityEngine.AssetBundle::LoadFromFile_Injecte",
        })
            log.Msg($"  icall {Describe(IL2CPP.il2cpp_resolve_icall(signature))} {signature}");
    }

    private static string Describe(IntPtr pointer) => pointer == IntPtr.Zero ? "missing" : $"present 0x{pointer:x}";

    private static unsafe UniverseLib.AssetBundle? FromMemory(byte[] binary, uint crc)
    {
        try
        {
            if (loadFromMemory is null)
            {
                var export = IL2CPP.il2cpp_resolve_icall(MemoryInjected);
                if (export == IntPtr.Zero)
                {
                    log.Warning($"{MemoryInjected} is not exported by this build.");
                    return null;
                }

                loadFromMemory = Marshal.GetDelegateForFunctionPointer<LoadFromMemoryInjected>(export);
            }

            IntPtr handle;
            fixed (byte* bytes = binary)
            {
                var span = new ManagedSpan { Begin = bytes, Length = binary.Length };
                handle = loadFromMemory(ref span, crc);
            }

            if (handle == IntPtr.Zero)
            {
                log.Warning("The injected memory loader returned no handle.");
                return null;
            }

            var bundle = Resolve(handle);
            log.Msg(bundle is null
                ? "The injected memory loader returned a handle that does not point at an AssetBundle."
                : "Loaded the UI bundle through the injected memory loader.");
            return bundle;
        }
        catch (Exception exception)
        {
            log.Warning($"The injected memory loader failed: {exception}");
            return null;
        }
    }

    // Unity hands back a scripting handle rather than an object pointer, and
    // UniverseLib itself is inconsistent about reading one: LoadFromFile calls
    // il2cpp_gchandle_get_target while LoadAsset dereferences the handle. Both
    // are tried here and the class pointer decides which was right, so a wrong
    // guess yields null instead of a corrupt wrapper.
    private static UniverseLib.AssetBundle? Resolve(IntPtr handle)
    {
        var target = IL2CPP.il2cpp_gchandle_get_target(handle);
        if (IsAssetBundle(target))
            return new UniverseLib.AssetBundle(target);

        if (target != IntPtr.Zero)
            return null;

        var dereferenced = Marshal.ReadIntPtr(handle);
        return IsAssetBundle(dereferenced) ? new UniverseLib.AssetBundle(dereferenced) : null;
    }

    private static bool IsAssetBundle(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
            return false;

        var expected = Il2CppClassPointerStore<UnityEngine.AssetBundle>.NativeClassPtr;
        return expected != IntPtr.Zero && IL2CPP.il2cpp_object_get_class(pointer) == expected;
    }

    // UniverseLib's own file loader already takes the injected path on Unity 6,
    // so it is a usable second attempt, but only once the truncated signature
    // it passes is known to resolve. Otherwise it falls through to the same
    // kind of mismatched legacy call that crashed the first modded start.
    private static UniverseLib.AssetBundle? FromFile(byte[] binary, uint crc)
    {
        var injected = IL2CPP.il2cpp_resolve_icall(FileInjected);
        var truncated = IL2CPP.il2cpp_resolve_icall(FileTruncated);
        if (injected == IntPtr.Zero || truncated != injected)
        {
            log.Warning("Skipping the file loader: UniverseLib would fall back to the legacy signature and crash.");
            return null;
        }

        var path = Path.Combine(MelonEnvironment.UserDataDirectory, "WetReality", $"universelib-ui-{binary.Length}-{crc}.bundle");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Unity keeps the file open for the lifetime of the bundle, so this
            // copy is deliberately left in place.
            File.WriteAllBytes(path, binary);

            var bundle = UniverseLib.AssetBundle.LoadFromFile(path, crc, 0UL);
            log.Msg(bundle is null
                ? $"UniverseLib's file loader returned no bundle for {path}."
                : "Loaded the UI bundle through UniverseLib's file loader.");
            return bundle;
        }
        catch (Exception exception)
        {
            log.Warning($"UniverseLib's file loader failed for {path}: {exception}");
            return null;
        }
    }
}
