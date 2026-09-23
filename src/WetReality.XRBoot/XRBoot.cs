using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MelonLoader;
using System.IO;
using UnityEngine;
using UnityEngine.SubsystemsImplementation;
using UnityEngine.XR;

[assembly: MelonInfo(typeof(WetReality.XRBoot), "Wet Reality XR Boot", "0.24.0", "Wet Reality")]
[assembly: MelonGame("FuturLab", "PowerWash Simulator 2")]

namespace WetReality;

// Starts Unity's OpenXR provider inside a shipped PWS2 build that was never
// built with XR support.
//
// Why this can work at all, from design document section 40: Unity registers
// XR subsystems from Data/UnitySubsystems/<library>/UnitySubsystemsManifest.json
// at startup, before any melon runs, and that registration is data driven. With
// UnityOpenXR.dll and openxr_loader.dll staged into Data/Plugins/x86_64, the
// descriptors "OpenXR Display" and "OpenXR Input" are already present and
// XRSettings.supportedDevices already lists them. What is missing is the managed
// loader that creates and starts them, because Unity.XR.OpenXR is not part of
// GameAssembly.dll and cannot be added to a finished IL2CPP build.
//
// This mod is that loader, reduced to the steps that matter. The sequence is
// transcribed from OpenXRLoader.InitializeInternal and StartInternal.
//
// Skipped, because they exist only to serve Unity's editor and feature
// infrastructure which is absent here: OpenXRFeature.Initialize,
// RequestOpenXRFeatures (a no-op without features), OpenXRSettings.ApplySettings,
// analytics and the diagnostic report.
//
// NOT skipped, though 0.2.0 tried to: HookGetInstanceProcAddr. It looks like
// feature plumbing and is not. With an empty feature chain it reduces to
// fetching the plugin's xrGetInstanceProcAddr pointer and handing it straight
// back, and that handing back is what loads stage one of the loader. Without it
// the plugin cannot resolve the global, instance-less entry points and fails
// Display_Initialize on the first one. See the imports in Native.cs.
//
// Booting is deliberately bound to a key rather than done on load. A failed or
// half-finished OpenXR session can hang a frame or wedge the renderer, and that
// must never happen to someone who merely started the game.
public sealed class XRBoot : MelonMod
{
    private enum Phase
    {
        Idle,
        Initialized,
        SessionRequested,
        Running,

        // Subsystems stopped, session and instance still alive. The distinction
        // from ShutDown is the whole of the second-boot fix: ShutDown is a state
        // nothing in this process can boot out of, Paused is one it can.
        Paused,

        Failed,
        ShutDown,
    }

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // Requested at boot so the instance is created with them. The first one is
    // the whole point: it converts a QueryPerformanceCounter value into an
    // XrTime, without which xrLocateSpace cannot be called at all. VDXR
    // reported it disabled when nobody asked for it.
    private static readonly string[] RequestedExtensions =
    {
        "XR_KHR_win32_convert_performance_counter",
    };

    // Held in a static field for the lifetime of the process. The native side
    // keeps the function pointer, so letting the delegate be collected would
    // turn the next event into a call through freed memory.
    private static Native.ReceiveNativeEvent? callback;

    // Design document section 42, option X6. XRBackend.IsEnabled is computed and
    // has no setter, but a computed property is still patchable. Forcing it true
    // is the only remaining route to PWS2's own VR layer, because the gate turned
    // out to be build data rather than runtime state: XR stayed disabled with a
    // focused OpenXR session, an active device and real eye buffers.
    //
    // The flags are separate from the patches on purpose. Both patches are
    // installed at load but report the original value until a flag is set.
    //
    // Two flags rather than one, and the reason is the 17:03 crash.
    //
    // Forcing both patches from startup killed the renderer:
    // RenderPipelineAsset.InternalCreatePipeline threw an InvalidCastException
    // and the game died before the menu. The prime suspect is the broad patch,
    // not the narrow one. XRSDKConditional is a FuturLab.Conditional, and that
    // system plausibly gates graphics decisions too - forcing every conditional
    // check true can make the pipeline manager select an XR variant this build
    // does not contain, which is exactly what an InvalidCastException during
    // pipeline creation looks like.
    //
    // So they are separately controllable now. The narrow one alone is the
    // experiment worth repeating.
    private static bool forceBackendGate;
    private static bool forceConditionalGate;

    private MelonPreferences_Entry<string> bootKey = null!;
    private MelonPreferences_Entry<string> shutdownKey = null!;
    private MelonPreferences_Entry<string> gateKey = null!;
    private MelonPreferences_Entry<string> loaderName = null!;
    private MelonPreferences_Entry<bool> desktopMirror = null!;
    private MelonPreferences_Entry<string> desktopMirrorEye = null!;

    private string mirrorApplied = "";
    private float nextMirrorCheck;
    private bool loggedMirrorOriginal;
    private MelonPreferences_Entry<bool> forceBackendFromStart = null!;
    private MelonPreferences_Entry<bool> forceConditionalFromStart = null!;
    private MelonPreferences_Entry<bool> pauseOnShutdown = null!;
    private MelonPreferences_Entry<bool> swapEyes = null!;

    private Phase phase = Phase.Idle;

    private MelonPreferences_Entry<bool> autoBoot = null!;
    private MelonPreferences_Entry<bool> devMode = null!;
    private MelonPreferences_Entry<bool> devHotkeys = null!;

    // Auto-boot state. All of it is "did we already try", so none of it needs to
    // survive anything.
    private bool autoBootDone;
    private bool autoBootBlocked;
    private int frameCount;
    private float slowestRecent;
    private readonly float[] recentFrames = new float[30];
    private int recentIndex;
    private float firstUpdate = -1f;
    private float nextPreflight;
    private bool warnedPreflight;
    private Native.NativeEvent lastEvent = Native.NativeEvent.XrIdle;
    private bool sawAnyEvent;

    // Whether the second session_EndSession has already been made for the current
    // pause. See the XrStopping branch in Pump - the call is legal at exactly one
    // moment and must not be repeated after it.
    private bool answeredStopping;
    private string status = "Idle. Press the boot key to start OpenXR.";
    private string lastStep = "";

    private XRDisplaySubsystem? display;
    private XRInputSubsystem? input;
    private float gateReportDue = -1f;
    private float stateReportDue = -1f;

    public override void OnInitializeMelon()
    {
        // Must happen before the first P/Invoke. Version 0.1.0 did not do this
        // and died immediately with DllNotFoundException on UnityOpenXR.
        // Fully qualified: MelonLoader ships a NativeLibrary of its own, so the
        // bare name is ambiguous here.
        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(
            typeof(XRBoot).Assembly, ResolveNativeLibrary);

        var settings = MelonPreferences.CreateCategory("WetRealityXRBoot");
        // AUTOMATIC BOOT, so nothing has to be pressed at all.
        //
        // The header of this file forbids booting AT LOAD and forbids a failed
        // boot taking the flat game with it. It does not forbid an automatic
        // boot as such - and section 73 designed the safe form, which this is:
        // three pre-flight checks, a maturity gate measured in FRAMES rather
        // than seconds, and a marker file so a boot that hangs the process
        // natively cannot do it twice.
        autoBoot = settings.CreateEntry("AutoBootOnStartup", true,
            description: "Bring up XR by itself once the game has settled, so no key "
                + "has to be pressed. Falls back to the boot key if any pre-flight "
                + "check fails, and skips itself if the previous attempt never finished.");

        // A NEW ENTRY, so it reaches every cfg that already exists - a changed
        // DEFAULT would not, which is why setting GateKey's default to "None"
        // would have fixed nothing for anyone already installed.
        //
        // F8 stays outside this on purpose; see DevPressed.
        // DER EINE SCHALTER, DER ALLE ENTWICKLUNGSHILFEN DECKELT.
        //
        // Eine Beta soll nichts mitschleppen, was nur zum Messen da war: die
        // Live-Logs, die Dev-Tasten und die Messberichte kosten Bild fuer
        // Bild Arbeit und fuellen das Log.
        //
        // GEDECKELT, NICHT UEBERSCHRIEBEN. Naheliegend waere, die einzelnen
        // Schalter beim Start auf false zu setzen - aber MelonPreferences
        // speichert beim Spielende zurueck, und damit waeren die
        // Entwicklungswerte dauerhaft weg. Der Deckel laesst sie stehen und
        // macht sie nur unwirksam, siehe Dev().
        devMode = settings.CreateEntry("DevMode", false,
            description: "Master switch for everything that only exists for development: "
                + "the verbose per-frame logs, the F-key and keypad hotkeys, and the miss "
                + "and chain reports. While this is false those switches have no effect, "
                + "whatever they are set to, and the mod does no measuring work. Set it "
                + "to true to develop; the individual switches then apply as before. The "
                + "MelonLoader console window is separate - hide_console under [console] "
                + "in UserData/Loader.cfg.");

        devHotkeys = settings.CreateEntry("DevHotkeys", false,
            description: "Development keys: the shutdown key and the gate key. Off by default "
                + "so they cannot be hit by accident - the shutdown key is a full teardown "
                + "and VR only comes back on a game restart. The boot key stays on either way, "
                + "because it is the fallback when the automatic boot stands down.");

        bootKey = settings.CreateEntry("BootKey", "F8", description: "Runs the OpenXR initialize and start sequence.");
        shutdownKey = settings.CreateEntry("ShutdownKey", "F6",
            description: "Stops the subsystems. Pauses by default, so the boot key resumes; "
                + "with PauseOnShutdown off it ends and destroys the session instead.");
        gateKey = settings.CreateEntry("GateKey", "F4",
            description: "Forces XRBackend.IsEnabled true and instantiates the XRInitializer prefab. Expect the flat game to break.");
        // DAS SPIEGELBILD AUF DEM BILDSCHIRM - Abschnitt 104.
        //
        // Gemeldet: auf dem Monitor erscheint das Titelbild verschachtelt, Bild
        // im Bild. Und es kostet Leistung, die im Headset niemandem nuetzt -
        // ein Spieler, der nur in VR spielt, will es abschalten koennen.
        //
        // Default AN, weil ein schwarzes Fenster beim Start wie ein Defekt
        // aussieht und die Vorschau fuer Zuschauer der normale Fall ist. Der
        // Konfigurator hat dafuer ein Haekchen.
        desktopMirror = settings.CreateEntry("DesktopMirror", true,
            description: "Show the VR view on the monitor. Costs performance that nobody in "
                + "the headset benefits from; turn it off for a solo session.");

        // Welches Auge, getrennt vom Haekchen: so verliert ein Nutzer, der hier
        // "right" oder "both" eingetragen hat, seine Wahl nicht, wenn er das
        // Spiegelbild im Werkzeug aus- und wieder einschaltet.
        //
        // LeftEye ist das billigste echte Spiegelbild - BothEyes blittet zwei
        // Puffer fuer ein Bild, das auf einem Monitor niemand stereoskopisch
        // sieht.
        desktopMirrorEye = settings.CreateEntry("DesktopMirrorEye", "left",
            description: "Which eye the monitor shows: left, right or both. Only read while "
                + "DesktopMirror is on.");

        // Abschnitt 179. Ein Diagnoseweg fuer genau einen Lauf, darum aus
        // ausgeliefert und nicht unter DevMode: er wirkt nur beim Start und
        // ist ohne Schalter nicht zu erreichen.
        swapEyes = settings.CreateEntry("SwapEyes", false,
            description: "DIAGNOSTIC. Exchanges the left and right eye views at xrLocateViews, so "
                + "the left display shows what was drawn from the right eye. Depth looks inverted - "
                + "close one eye at a time. Read at boot only. Leave off.");

        loaderName = settings.CreateEntry("LoaderName", "openxr_loader",
            description: "Loader library passed to main_LoadOpenXRLibrary. Set to the mock runtime path to test without a headset.");
        forceBackendFromStart = settings.CreateEntry("ForceBackendGateFromStart", false,
            description: "Forces XRBackend.IsEnabled true from melon load, so the game's own startup can take the XR branch. "
                + "The narrow patch. Risky, off by default.");
        forceConditionalFromStart = settings.CreateEntry("ForceConditionalGateFromStart", false,
            description: "Forces XRSDKConditional.IsEnabledImpl true from melon load. The broad patch, and the prime suspect "
                + "for the 17:03 crash in RenderPipelineAsset.InternalCreatePipeline. Leave off unless testing exactly that.");
        pauseOnShutdown = settings.CreateEntry("PauseOnShutdown", false,
            description: "Makes the shutdown key only STOP the subsystems and leave the OpenXR session and instance alive, "
                + "so the boot key resumes instead of rebooting. On by default because the full teardown cannot be booted "
                + "out of: the XrInstance is created inside the display subsystem's native Display_Initialize, which only "
                + "runs on genuine subsystem creation, and this mod deliberately never destroys the subsystems. Turn off "
                + "to get the old full-teardown behaviour back, at the price of needing a game restart afterwards.");

        LoggerInstance.Msg($"Ready. {bootKey.Value} boots OpenXR"
            + (Dev(devHotkeys)
                ? $", {shutdownKey.Value} "
                    + (pauseOnShutdown.Value ? "pauses it, and boots again to resume" : "shuts it down")
                : " if the automatic boot stands down; the shutdown and gate keys are OFF "
                    + "(DevHotkeys)")
            + ". " + (autoBoot.Value ? "Auto-boot is on." : "Nothing happens until then."));
        LoggerInstance.Msg($"XRSettings.enabled is {XRSettings.enabled}, supportedDevices \"{SupportedDevices()}\".");

        // Reported at load, so a missing staged file is obvious before anyone
        // presses a key and blames the sequence for it.
        var plugins = PluginDirectory();
        LoggerInstance.Msg($"Native plugin directory: {plugins}");
        foreach (var file in new[] { "UnityOpenXR.dll", "openxr_loader.dll" })
        {
            var full = Path.Combine(plugins, file);
            LoggerInstance.Msg($"  {(File.Exists(full) ? "present" : "MISSING")}  {file}");
        }

        InstallGatePatches();

        // The lesson from the 16:53 run: forcing the gate at runtime is too
        // late. The patch worked - IsEnabled went from False to True - and the
        // game did not care, because nothing re-queries it once the scene is up.
        // Whatever consumes the flag consumes it during startup, and by the time
        // a key can be pressed that decision is long made.
        //
        // So the only meaningful version of the experiment is to force the value
        // before the game has decided anything. That is what this does, and it
        // is off by default because a game that boots down an XR path it was
        // never built for may not reach the menu at all.
        forceBackendGate = forceBackendFromStart.Value;
        forceConditionalGate = forceConditionalFromStart.Value;

        if (forceBackendGate || forceConditionalGate)
        {
            LoggerInstance.Warning($"Forcing from start: XRBackend.IsEnabled {forceBackendGate}, "
                + $"XRSDKConditional.IsEnabledImpl {forceConditionalGate}.");
            LoggerInstance.Warning("If the game does not reach the menu, set both back to false in UserData/MelonPreferences.cfg.");
        }
    }

    // Both patches are installed dormant. Whether MelonLoader can detour an
    // il2cpp method at all is itself part of the experiment, so each one is
    // reported individually rather than assumed.
    private void InstallGatePatches()
    {
        Patch("XRBackend.get_IsEnabled",
            typeof(Il2CppFuturLab.XR.XRBackend).GetProperty("IsEnabled",
                BindingFlags.Static | BindingFlags.Public)?.GetGetMethod(),
            nameof(ForceBackend));

        // The deeper of the two. XRSDKConditional.IsEnabledImpl is where the
        // build data is most likely evaluated - it holds m_enabledXRSDKs of type
        // XRPlatform, which for this PC build is presumably None. Patching here
        // also covers Conditional.IsEnabled, which delegates to it.
        //
        // Broader than the first patch and correspondingly riskier: the same
        // conditional type may gate unrelated decisions.
        Patch("XRSDKConditional.IsEnabledImpl",
            typeof(Il2CppFuturLab.XRSDKConditional).GetMethod("IsEnabledImpl",
                BindingFlags.Instance | BindingFlags.Public),
            nameof(ForceConditional));
    }

    private void Patch(string label, MethodInfo? target, string postfix)
    {
        if (target is null)
        {
            LoggerInstance.Error($"  patch  NOT FOUND  {label}");
            return;
        }

        try
        {
            HarmonyInstance.Patch(target,
                postfix: new HarmonyMethod(typeof(XRBoot).GetMethod(postfix,
                    BindingFlags.Static | BindingFlags.NonPublic)));
            LoggerInstance.Msg($"  patch  installed, dormant  {label}");
        }
        catch (Exception exception)
        {
            LoggerInstance.Error($"  patch  FAILED  {label}: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void ForceBackend(ref bool __result)
    {
        if (forceBackendGate)
            __result = true;
    }

    private static void ForceConditional(ref bool __result)
    {
        if (forceConditionalGate)
            __result = true;
    }

    // Unity loads its native plugins from Data/Plugins/x86_64 through its own
    // mechanism. CoreCLR, which is what a melon runs on, does not search that
    // directory for P/Invoke at all, so a plain DllImport("UnityOpenXR") fails
    // with ERROR_MOD_NOT_FOUND even while the file sits right there.
    //
    // Worth recording, because it was the surprise here: Unity had already
    // registered both subsystem descriptors from the manifest JSON without ever
    // loading the library. Had it loaded it, LoadLibrary would have matched the
    // module by base name and the plain import would have worked by accident.
    // Registration is metadata only; the library is loaded on first use.
    private static IntPtr ResolveNativeLibrary(string name, Assembly assembly, DllImportSearchPath? search)
    {
        if (!string.Equals(name, "UnityOpenXR", StringComparison.Ordinal))
            return IntPtr.Zero;

        var candidate = Path.Combine(PluginDirectory(), "UnityOpenXR.dll");
        return System.Runtime.InteropServices.NativeLibrary.TryLoad(candidate, out var handle)
            ? handle
            : IntPtr.Zero;
    }

    private static string PluginDirectory() => Path.Combine(Application.dataPath, "Plugins", "x86_64");

    // The loader path goes to LoadLibrary on the native side, which searches the
    // process paths rather than the calling module's directory. Unity gets away
    // with the bare name "openxr_loader" because it extends the DLL search path
    // at startup; nothing guarantees that reaches a call made from here. So a
    // configured name that is not already absolute is resolved against the
    // plugin directory, and a missing extension is filled in.
    private string LoaderPath()
    {
        var configured = loaderName.Value;

        if (!Path.IsPathRooted(configured))
            configured = Path.Combine(PluginDirectory(), configured);
        if (string.IsNullOrEmpty(Path.GetExtension(configured)))
            configured += ".dll";

        return configured;
    }

    private static string PendingPath() =>
        Path.Combine(
            MelonLoader.Utils.MelonEnvironment.UserDataDirectory,
            "WetReality", "autoboot.pending");

    // THE LINE THAT KEEPS THE PROMISE, and it is the whole reason an automatic
    // boot is defensible at all.
    //
    // Boot() returns through Fail() at five places and Pump() at a sixth, each
    // setting Phase.Failed, and OnUpdate is wrapped in try/catch - so a boot that
    // throws already leaves a fully playable flat game behind. What none of that
    // covers is a NATIVE hang: no exception, no log line, nothing to catch.
    //
    // A marker file written immediately before the attempt and deleted on
    // reaching Running covers exactly that case. If it is still there at load,
    // the previous automatic boot never finished, and this one steps aside. So
    // even a boot that freezes the process cannot do it twice, and a stranger's
    // second launch is always the playable flat game.
    private bool ClaimAutoBoot()
    {
        try
        {
            var path = PendingPath();

            if (File.Exists(path))
            {
                LoggerInstance.Warning("Auto-boot SKIPPED: " + path + " still exists, so the "
                    + "previous automatic boot never finished. Press " + bootKey.Value
                    + " to boot manually, or delete that file to re-arm it.");
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTime.Now.ToString("O"));
            return true;
        }
        catch (Exception exception)
        {
            // Unable to write the marker means unable to guarantee the promise,
            // so the automatic boot does not happen. Failing closed.
            LoggerInstance.Warning("Auto-boot SKIPPED: could not write the marker file ("
                + exception.GetType().Name + "). Booting automatically without it would "
                + "risk repeating a hang on every launch.");
            return false;
        }
    }

    private void ReleaseAutoBoot()
    {
        try
        {
            var path = PendingPath();

            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A stale marker only costs the NEXT automatic boot, which is the
            // safe direction to fail in.
        }
    }

    // Three pre-flight checks, each turning a silent native failure into a
    // sentence somebody can act on.
    private bool PreflightPasses()
    {
        // ONE. Empty supportedDevices means the three files of delivery layer B
        // are missing or misnamed - the single most likely installation error -
        // and without this check it surfaces as
        // "session_CreateSessionIfNeeded returned false".
        var devices = XRSettings.supportedDevices;
        var count = devices is null ? 0 : devices.Length;

        if (count == 0)
        {
            if (warnedPreflight) return false;
            LoggerInstance.Warning("Auto-boot: XRSettings.supportedDevices is EMPTY. "
                + "UnityOpenXR.dll, openxr_loader.dll or "
                + "Data/UnitySubsystems/UnityOpenXR/UnitySubsystemsManifest.json is missing "
                + "or misnamed. VR cannot start until that is fixed.");
            return false;
        }

        // TWO. No active runtime means no headset software is running, and the
        // registry answers that for the price of one read.
        try
        {
            var runtime = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Khronos\OpenXR\1", "ActiveRuntime", null) as string;

            if (string.IsNullOrEmpty(runtime))
            {
                if (warnedPreflight) return false;
                LoggerInstance.Warning("Auto-boot: no ActiveRuntime under "
                    + "HKLM\\SOFTWARE\\Khronos\\OpenXR\\1. Start your headset software first.");
                return false;
            }

            if (!File.Exists(runtime))
            {
                LoggerInstance.Warning("Auto-boot SKIPPED: ActiveRuntime points at \""
                    + runtime + "\", which does not exist.");
                return false;
            }

            LoggerInstance.Msg("Auto-boot pre-flight: " + count
                + " supported device(s), runtime \"" + runtime + "\"");
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning("Auto-boot SKIPPED: reading the OpenXR runtime key threw "
                + exception.GetType().Name);
            return false;
        }

        return true;
    }

    // THE MATURITY GATE, measured in FRAMES and not only in seconds.
    //
    // Frames only advance while the render loop is alive, so a frame count is a
    // direct measurement of "the renderer is healthy and not mid-load". Wall
    // clock is not: a long asset load blocks the main thread, and eight seconds
    // of a frozen game look exactly like eight seconds of a running one.
    //
    // Camera.main is deliberately NOT the gate. Pose.Resolve has a branch "No
    // main camera. Load a job first.", so it is null outside a job - and booting
    // from the main menu was measured working in section 41. Using it would
    // forbid the very case that works.
    private bool MatureEnough()
    {
        if (firstUpdate < 0f)
            firstUpdate = Time.unscaledTime;

        var delta = Time.unscaledDeltaTime;

        frameCount++;
        recentFrames[recentIndex] = delta;
        recentIndex = (recentIndex + 1) % recentFrames.Length;

        if (frameCount < recentFrames.Length * 2)
            return false;

        if (Time.unscaledTime - firstUpdate < 8f)
            return false;

        slowestRecent = 0f;

        for (var index = 0; index < recentFrames.Length; index++)
        {
            if (recentFrames[index] > slowestRecent)
                slowestRecent = recentFrames[index];
        }

        return slowestRecent < 0.1f;
    }

    private void TryAutoBoot()
    {
        if (autoBootDone || autoBootBlocked || !autoBoot.Value)
            return;

        if (phase is not Phase.Idle)
        {
            // Somebody already pressed the key, or a previous attempt is under
            // way. Either way this is no longer ours to start.
            autoBootDone = true;
            return;
        }

        if (!MatureEnough())
            return;

        // The retry timer. Without it a failing pre-flight would be re-read on
        // every single frame for a minute.
        if (Time.unscaledTime < nextPreflight)
            return;

        // A BOUNDED RETRY on the pre-flight, because failing it once is not the
        // same as failing it forever.
        //
        // The likeliest failure is simply that the headset software has not
        // finished starting - the ActiveRuntime key is absent until it has. A
        // single attempt would then stand down permanently and the player would
        // be left wondering why VR never came, having done nothing wrong.
        //
        // Bounded at 60 seconds rather than unbounded, and that limit is the
        // point: somebody who deliberately wants the flat game must not have VR
        // sprung on them ten minutes later because they happened to start their
        // headset software for something else.
        if (!PreflightPasses())
        {
            if (Time.unscaledTime - firstUpdate < 60f)
            {
                if (!warnedPreflight)
                {
                    warnedPreflight = true;
                    LoggerInstance.Msg("Auto-boot waiting: pre-flight not satisfied yet. "
                        + "Retrying for up to 60 s in case the headset software is still "
                        + "starting.");
                }

                // Not done after all - try again on a later frame.
                autoBootDone = false;
                nextPreflight = Time.unscaledTime + 5f;
                return;
            }

            autoBootBlocked = true;
            LoggerInstance.Msg("Auto-boot stood down after 60 s. " + bootKey.Value
                + " still boots manually, and the flat game is untouched.");
            return;
        }

        autoBootDone = true;

        if (!ClaimAutoBoot())
        {
            autoBootBlocked = true;
            LoggerInstance.Msg("Auto-boot stood down. " + bootKey.Value
                + " still boots manually, and the flat game is untouched.");
            return;
        }

        LoggerInstance.Msg("Auto-boot: " + frameCount + " frames, "
            + (Time.unscaledTime - firstUpdate).ToString("0.0") + " s, slowest of the last "
            + recentFrames.Length + " frames " + (slowestRecent * 1000f).ToString("0")
            + " ms. Booting.");

        Boot();
    }

    public override void OnUpdate()
    {
        try
        {
            // Two branches on the same key, and the order is the fix. From
            // Paused the boot key RESUMES; only from the three dead phases does
            // it run the full sequence again. Boot() from a paused state is what
            // F8 used to do and what could never work: Create() hands back a
            // subsystem without re-running Display_Initialize, so no XrInstance
            // is ever created and session_CreateSessionIfNeeded fails.
            TryAutoBoot();
            DriveDesktopMirror();

            if (Pressed(bootKey) && phase is Phase.Paused)
                Resume();
            else if (Pressed(bootKey) && phase is Phase.Idle or Phase.Failed or Phase.ShutDown)
                Boot();
            // BOTH BEHIND DevHotkeys. The shutdown key is the most expensive
            // accidental press in this mod: with PauseOnShutdown off it is a
            // full teardown, and the entry's own description says that cannot be
            // booted out of - VR is gone until the game is restarted. A player
            // who wants the flat game presses F2 in the Pose mod instead.
            //
            // The gate has no phase guard at all, so it sat at the END of this
            // chain catching any press of its key in any state.
            else if (DevPressed(shutdownKey) && phase is Phase.Initialized or Phase.SessionRequested or Phase.Running)
                Shutdown();
            else if (DevPressed(gateKey))
                RunGateExperiment();

            // Pumped while paused as well. The session is still begun, so the
            // runtime keeps queueing events for it, and a session whose events
            // nobody collects is how XrStopping and XrExiting go unseen. Pump's
            // own two branches cannot fire from Paused, so this only drains.
            if (phase is Phase.Initialized or Phase.SessionRequested or Phase.Running or Phase.Paused)
                Pump();

            if (stateReportDue >= 0f && Time.realtimeSinceStartup >= stateReportDue)
            {
                stateReportDue = -1f;
                ReportInteractionProfiles();
                XRInput.ReportActionState(LoggerInstance);
                XRInput.ReportDeviceIdMapping(LoggerInstance);
            }


            if (gateReportDue >= 0f && Time.realtimeSinceStartup >= gateReportDue)
            {
                gateReportDue = -1f;
                ReportGateOutcome();
            }
        }
        catch (Exception exception)
        {
            phase = Phase.Failed;
            status = $"Failed in {lastStep}: {exception.GetType().Name}. See the log.";
            LoggerInstance.Error($"XR boot failed during {lastStep}: {exception}");
        }
    }

    // Discovery draws its own box at the top left, so this one sits below it.
    public override void OnGUI()
    {
        // NOT DRAWN while XR is running, the same rule Pose already follows.
        //
        // OnGUI has no stereo awareness at all, so this box is one of the
        // elements the player sees doubled and glued to their face - reported as
        // "log messages in the top left". It stays visible BEFORE the boot and
        // after a shutdown, which is exactly when it is useful: a failed boot
        // still has to be able to say so on screen.
        if (XRSettings.enabled)
            return;

        // FUER SPIELER UNSICHTBAR - Abschnitt 109. Gemeldet als "halb
        // transparentes Konsolenfenster links oben beim Start", und genau das
        // ist es: diese Box, solange XR noch nicht laeuft.
        //
        // Der Fehlerfall bleibt sichtbar, und das ist keine Ausnahme aus
        // Bequemlichkeit: ein Boot, der scheitert, muss das sagen koennen -
        // ohne Bild im Headset und ohne Log, das ein Spieler liest, waere
        // "es passiert nichts" die einzige Auskunft.
        if (!devMode.Value && phase is not Phase.Failed)
            return;

        GUI.Box(new Rect(8f, 96f, 620f, 82f), GUIContent.none);
        GUI.Label(new Rect(16f, 100f, 604f, 20f),
            $"Wet Reality XR Boot   {bootKey.Value} boot"
                + (Dev(devHotkeys) ? $"   {shutdownKey.Value} shutdown" : "")
                + $"   phase {phase}");
        GUI.Label(new Rect(16f, 120f, 604f, 20f),
            $"XRSettings.enabled {XRSettings.enabled}, device \"{XRSettings.loadedDeviceName}\", " +
            $"last event {(sawAnyEvent ? lastEvent.ToString() : "none")}");
        GUI.Label(new Rect(16f, 140f, 604f, 36f), status);
    }

    // ALT MUST NOT BE HELD. Windows closes a window on Alt+F4, Unity reports the
    // F4 regardless, and GateKey's default is F4 - whose own description ends
    // "Expect the flat game to break". It never fired on this machine only
    // because a working cfg had GateKey set to "None" by hand; every fresh
    // installation had it live on the standard close-the-window chord.
    //
    // Alt+F6 is the teardown on the same reasoning, so the guard is on the
    // helper rather than on one key.
    // Deckelt einen Entwicklungsschalter mit DevMode. Der gespeicherte Wert
    // bleibt unberuehrt - nur seine Wirkung haengt am Hauptschalter.
    private bool Dev(MelonPreferences_Entry<bool> entry) =>
        devMode.Value && entry.Value;

    private static bool Pressed(MelonPreferences_Entry<string> entry) =>
        !(Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
        && Enum.TryParse<KeyCode>(entry.Value, ignoreCase: true, out var key)
        && Input.GetKeyDown(key);

    // A DEVELOPMENT key: silent unless DevHotkeys says otherwise.
    //
    // F8 is NOT one of these. Auto-boot handles the normal start, but it stands
    // down on a failed pre-flight check and then says so in the log - "Auto-boot
    // stood down. F8 still boots manually" - which makes F8 the only way back
    // into VR at that point. Taking it away would strand exactly the user whose
    // machine needed it.
    private bool DevPressed(MelonPreferences_Entry<string> entry) =>
        Dev(devHotkeys) && Pressed(entry);

    // ~~~~~~~~~~~~ Initialize ~~~~~~~~~~~~

    private void Boot()
    {
        LoggerInstance.Msg("=== OpenXR boot sequence ===");
        phase = Phase.Idle;
        sawAnyEvent = false;

        // Reset explicitly, because Pump gates on it. A stale XrFocused from a
        // previous session leaves phase stuck at SessionRequested forever, since
        // the branch at the end of Pump fires on exactly XrReady; a stale
        // XrReady does the opposite and starts the subsystems a frame early.
        // Unity never needs this line: its equivalent field is per loader
        // instance, and its Deinitialize pumps the message loop twice so the
        // shutdown events themselves move the state off XrReady. This mod keeps
        // one field for the whole process and skips those pumps.
        lastEvent = Native.NativeEvent.XrIdle;

        // Step order follows OpenXRLoader.InitializeInternal. It is worth noting
        // one oddity: Unity sets the application info AFTER initializing the
        // session. That looks wrong for OpenXR, where the application name
        // belongs to xrCreateInstance, but it is the order that demonstrably
        // works in shipped Unity builds, so it is reproduced rather than
        // improved. If instance creation turns out to ignore the name, moving
        // this call earlier is the first thing to try.
        Step("session_SetSuccessfullyInitialized", () => Native.SetSuccessfullyInitialized(false));

        var loader = LoaderPath();
        if (!File.Exists(loader))
        {
            Fail($"No loader library at {loader}.");
            return;
        }

        if (!Step($"main_LoadOpenXRLibrary(\"{loader}\")", () => Native.LoadOpenXRLibrary(Native.ToWideBytes(loader))))
        {
            Fail($"The OpenXR loader library at {loader} exists but was refused by the plugin.");
            return;
        }

        // Unity's equivalent is OpenXRFeature.HookGetInstanceProcAddr, which with
        // an empty feature chain reduces to exactly these two calls. It has to
        // happen before InitializeSession, in Unity's order, and it is not
        // optional: see the comment on the imports.
        var procAddress = Native.GetProcAddressPtr(true);
        LoggerInstance.Msg($"  ok       NativeConfig_GetProcAddressPtr -> 0x{procAddress.ToInt64():x}");
        if (procAddress == IntPtr.Zero)
        {
            Fail("The plugin returned a null xrGetInstanceProcAddr. The loader library did not initialize.");
            return;
        }

        // Abschnitt 179: mit SwapEyes steht hier die eigene Huelle, sonst der
        // unveraenderte Zeiger - der Normalfall bleibt Byte fuer Byte derselbe.
        var handed = swapEyes.Value ? EyeSwap.Wrap(LoggerInstance, procAddress) : procAddress;

        Step("NativeConfig_SetProcAddressPtrAndLoadStage1", () => Native.SetProcAddressPtrAndLoadStage1(handed));

        // Requested twice on purpose, before and after InitializeSession.
        //
        // 0.7.0 asked only afterwards, where Unity asks, and got REFUSED. Two
        // explanations fit: either the runtime does not support the extension,
        // or the plugin had already read the runtime's extension list during
        // InitializeSession and stopped accepting requests. This run separates
        // them - if the early attempt is accepted, it was timing.
        RequestExtensions("before InitializeSession");

        if (!Step("session_InitializeSession", Native.InitializeSession))
        {
            Fail("Session initialization was refused even with stage 1 loaded.");
            return;
        }

        // The slot where Unity calls RequestOpenXRFeatures.
        RequestExtensions("after InitializeSession");

        callback = OnNativeEvent;
        Step("NativeConfig_SetCallbacks", () => Native.SetCallbacks(callback));

        Step("NativeConfig_SetApplicationInfo", () => Native.SetApplicationInfo(
            Application.productName, Application.version, VersionHash(Application.version), Application.unityVersion));

        display = Create<XRDisplaySubsystemDescriptor, XRDisplaySubsystem>("OpenXR Display");
        if (display is null)
        {
            Fail("The display subsystem could not be created even though its descriptor is registered.");
            return;
        }

        LoggerInstance.Msg("  created  XRDisplaySubsystem");

        // Input is created but not required. Stereo rendering is the point of
        // this experiment; controller poses come later and must not be able to
        // block the display path.
        input = Create<XRInputSubsystemDescriptor, XRInputSubsystem>("OpenXR Input");
        LoggerInstance.Msg(input is null
            ? "  WARNING  XRInputSubsystem could not be created. Continuing, display only."
            : "  created  XRInputSubsystem");

        phase = Phase.Initialized;
        status = "Initialized. Pumping the message loop, waiting for XrReady.";
        LoggerInstance.Msg("Initialize finished. Waiting for the runtime to report XrReady.");
    }

    // ~~~~~~~~~~~~ Message loop and start ~~~~~~~~~~~~

    // Unity pumps this from Application.onBeforeRender. A melon cannot subscribe
    // to that as conveniently, but it does not need to: the pump is exported
    // directly as messagepump_PumpMessageLoop, and OnUpdate runs on the main
    // thread once per frame, which is what OpenXR requires.
    // DAS SPIEGELBILD, zweimal pro Sekunde geprueft und nur bei Abweichung
    // geschrieben.
    //
    // Nicht einmalig: XRSettings gehoert dem Plugin, und ein Wert, den ein
    // Ladevorgang oder ein Modewechsel zurueckstellt, waere sonst still
    // verloren. Zwei Eigenschaftslesungen alle 500 ms sind dagegen nichts -
    // und der Vergleich verhindert, dass pro Frame geschrieben wird.
    //
    // NUR WAEHREND XR LAEUFT. Vorher gehoert das Fenster dem flachen Spiel, und
    // ein gesetztes gameViewRenderMode waere ein Eingriff ohne Gegenstand.
    private void DriveDesktopMirror()
    {
        if (!XRSettings.enabled || UnityEngine.Time.unscaledTime < nextMirrorCheck)
            return;

        nextMirrorCheck = UnityEngine.Time.unscaledTime + 0.5f;

        try
        {
            // DER AUSGANGSWERT, EINMAL UND VOR DEM ERSTEN SCHREIBEN. Ohne ihn
            // waere hinterher nicht zu sagen, ob "aus" gewirkt hat oder ob es
            // schon aus war - und die Bild-im-Bild-Meldung braucht genau diese
            // Unterscheidung.
            if (!loggedMirrorOriginal)
            {
                loggedMirrorOriginal = true;
                LoggerInstance.Msg($"  mirror original: showDeviceView "
                    + $"{XRSettings.showDeviceView}   gameViewRenderMode "
                    + $"{XRSettings.gameViewRenderMode}   eyeTexture "
                    + $"{XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}");
            }

            var wanted = desktopMirror.Value
                ? (desktopMirrorEye.Value ?? "left").Trim().ToLowerInvariant()
                : "off";

            if (string.Equals(wanted, mirrorApplied, StringComparison.Ordinal))
                return;

            var mode = wanted switch
            {
                "off" => UnityEngine.XR.GameViewRenderMode.None,
                "right" => UnityEngine.XR.GameViewRenderMode.RightEye,
                "both" => UnityEngine.XR.GameViewRenderMode.BothEyes,
                _ => UnityEngine.XR.GameViewRenderMode.LeftEye,
            };

            // BEIDE SCHALTER, weil unbekannt ist, welcher in dieser
            // Unity-Fassung den Blit wirklich traegt. showDeviceView ist der
            // aeltere, gameViewRenderMode der genauere; gegeneinander koennen
            // sie nicht stehen, denn None und false wollen dasselbe.
            XRSettings.showDeviceView = wanted != "off";
            XRSettings.gameViewRenderMode = mode;

            mirrorApplied = wanted;

            LoggerInstance.Msg($"  mirror: {wanted} -> showDeviceView "
                + $"{XRSettings.showDeviceView}   gameViewRenderMode "
                + $"{XRSettings.gameViewRenderMode}");
        }
        catch (Exception exception)
        {
            // Einmal und nie wieder: ein werfender Schalter darf nicht zweimal
            // pro Sekunde eine Warnung schreiben.
            nextMirrorCheck = float.MaxValue;
            LoggerInstance.Warning($"  mirror threw {exception.GetType().Name}: "
                + exception.Message);
        }
    }

    private void Pump()
    {
        Native.PumpMessageLoop();

        // The second half of the pause, and it exists because the OpenXR state
        // machine does not let an application end a session whenever it likes.
        // xrEndSession is legal in XR_SESSION_STATE_STOPPING only; from a focused
        // session it is XR_ERROR_SESSION_NOT_STOPPING. Unity answers that by
        // calling EndSession from TWO places: once in StopInternal, which Stop
        // reaches from a possibly focused session, and again from its XrStopping
        // event case, which is the moment the call is actually legal.
        //
        // If the plugin's session_EndSession is state-aware and the first call in
        // Shutdown already took, XrStopping never arrives while paused and this
        // never fires. Which of the two happens is readable in the log, and it is
        // the one thing about the native side that no source here can answer.
        if (phase == Phase.Paused && !answeredStopping
            && lastEvent == Native.NativeEvent.XrStopping)
        {
            answeredStopping = true;
            Step("session_EndSession answering XrStopping", Native.EndSession);
            Native.PumpMessageLoop();
        }

        if (phase == Phase.Initialized)
        {
            // Idempotent by name and by contract, and Unity likewise calls it
            // repeatedly until the runtime is ready.
            // Routed through Step for one reason: Fail prints lastStep, and
            // lastStep was still holding the last subsystem-creation step from
            // Boot. So every failure here was reported as a subsystem creation
            // failure, and the log pointed three rounds of investigation at the
            // wrong call. The step that fails has to name itself.
            if (!Step("session_CreateSessionIfNeeded", Native.CreateSessionIfNeeded))
            {
                Fail("session_CreateSessionIfNeeded returned false. No usable OpenXR runtime, or the headset is not connected.");
                return;
            }

            phase = Phase.SessionRequested;
            status = "Session requested. Waiting for XrReady.";
        }

        if (phase != Phase.SessionRequested || lastEvent != Native.NativeEvent.XrReady)
            return;

        StartSubsystems();
    }

    private void StartSubsystems()
    {
        // The order is not cosmetic. Unity starts the display first so that the
        // input subsystem can reach the session object it creates.
        Step("XRDisplaySubsystem.Start", () => display!.Start());

        if (display is null || !display.running)
        {
            Fail("The display subsystem did not come up after Start.");
            return;
        }

        Step("session_BeginSession", Native.BeginSession);

        // Placed exactly where Unity calls OpenXRInput.AttachActionSets, which
        // is inside StartInternal after BeginSession. Order matters: action sets
        // can only be attached to a session that has begun.
        lastStep = "OpenXR input layer";
        if (!XRInput.Setup(LoggerInstance))
            LoggerInstance.Warning("The input layer did not come up. Poses and buttons will stay unavailable.");

        if (input is not null)
        {
            Step("XRInputSubsystem.Start", () => input.Start());

            // Stopped and restarted straight away, and the reason is a suspicion
            // worth one test rather than an established fact.
            //
            // The device definitions are registered before this Start, but the
            // subsystem itself was CREATED earlier, back in Boot. If it takes its
            // device list at creation, our hands were not there yet - which would
            // explain section 50 exactly: the XR layer reports IsTracked true for
            // both hands while the Input System reads nothing from them at all,
            // not even a button.
            //
            // A restart is the cheapest way to find out. If the hands start
            // reporting afterwards, the list is taken once and the fix is to
            // create the subsystem later. If nothing changes, the device list is
            // not the issue and this line comes back out.
            Step("XRInputSubsystem.Stop for a re-enumeration", () => input.Stop());
            Step("XRInputSubsystem.Start again", () => input.Start());
        }

        phase = Phase.Running;

        // Reaching Running is what the marker was waiting for. Deleted here and
        // nowhere else: any earlier and a hang between the two would go
        // unrecorded.
        ReleaseAutoBoot();
        status = $"Running. XRSettings.enabled {XRSettings.enabled}, device \"{XRSettings.loadedDeviceName}\".";

        LoggerInstance.Msg("=== OpenXR is running ===");
        LoggerInstance.Msg($"  XRSettings.enabled       {XRSettings.enabled}");
        LoggerInstance.Msg($"  XRSettings.loadedDevice  \"{XRSettings.loadedDeviceName}\"");
        LoggerInstance.Msg($"  XRSettings.eyeTexture    {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}");
        // renderViewportScale alongside the buffer size, for design document
        // section 91 A: a tester reports CPU and GPU maxed out where this
        // machine sits at 13 and 70 percent, and "not in flat mode" points at
        // the XR path, whose largest item is the render resolution. The buffer
        // size was already logged here, so the measurement section 91 asked for
        // is ALREADY in every tester's log; this is the one figure that was
        // missing beside it. A 4K buffer per eye explains a full GPU with no mod
        // involvement at all.
        LoggerInstance.Msg($"  renderViewportScale      {XRSettings.renderViewportScale}");
        LoggerInstance.Msg($"  stereoRenderingMode      {XRSettings.stereoRenderingMode}");
        LoggerInstance.Msg($"  display.running          {display.running}");
        LoggerInstance.Msg($"  input.running            {input?.running.ToString() ?? "no input subsystem"}");

        // Verified after the fact rather than assumed. A request is not an
        // enablement: the runtime decides.
        foreach (var extension in RequestedExtensions)
            LoggerInstance.Msg($"  extension  {(Native.IsExtensionEnabled(extension) ? "ENABLED " : "disabled")}  {extension}");

        ReportInteractionProfiles();

        // Both of these need xrSyncActions to have run at least once, so they
        // are repeated a few seconds later rather than trusted now. Asking too
        // early is the mistake that reported NONE for the interaction profile
        // twice, and IsEnabled false in section 41.
        stateReportDue = Time.realtimeSinceStartup + 5f;

        // The point of the whole exercise, from design document section 40, risk
        // R2: does the game's own gate notice? XRBackend.IsEnabled is computed
        // and read only, and XRSDKConditional suggests it asks whether an XR SDK
        // is running. This line is the test.
        try
        {
            LoggerInstance.Msg($"  XRBackend.IsEnabled      {Il2CppFuturLab.XR.XRBackend.IsEnabled}");
        }
        catch (Exception exception)
        {
            LoggerInstance.Warning($"  XRBackend.IsEnabled could not be read: {exception.GetType().Name}");
        }
    }

    private void OnNativeEvent(Native.NativeEvent nativeEvent, ulong payload)
    {
        lastEvent = nativeEvent;
        sawAnyEvent = true;

        // Every event is logged. During a first bring-up the event order is the
        // only window into what the runtime is actually doing.
        LoggerInstance.Msg($"  event    {nativeEvent}  payload {payload}");
    }

    // ~~~~~~~~~~~~ Shutdown ~~~~~~~~~~~~

    // F6 is a PAUSE by default now, because the old F6 was a one-way door.
    //
    // Pressing F8 after F6 never came back. The error blamed subsystem creation,
    // which was a lie from Fail printing a stale lastStep; the real failure was
    // session_CreateSessionIfNeeded, and the real cause is one step further
    // back. session_DestroySession destroys the session AND the instance, but
    // this method deliberately never destroys the display subsystem - see the
    // crash note on the teardown path below. The XrInstance is created inside
    // the display subsystem's native Display_Initialize, and that runs on
    // genuine subsystem CREATION only. So the second boot's Create() handed back
    // a subsystem over a plugin with no instance, no XrInstanceChanged event
    // arrived, and CreateSessionIfNeeded had nothing to build a session on.
    //
    // Stopping the subsystems already achieves everything F6 is for: rendering
    // returns to flat and the runtime releases the headset. Ending and
    // destroying the session on top bought nothing and cost the way back.
    private void Shutdown()
    {
        var pausing = pauseOnShutdown.Value;
        LoggerInstance.Msg(pausing ? "=== OpenXR pause ===" : "=== OpenXR shutdown ===");

        // Cleared first, and not for tidiness. Both timers outlive the session
        // otherwise, and every callback behind them P/Invokes - StringToPath,
        // GetCurrentInteractionProfile, GetActionIsActive, GetDeviceId. Five
        // seconds after a teardown those reach a plugin whose instance is gone.
        stateReportDue = -1f;
        gateReportDue = -1f;

        // Input first, display second: Unity's order in its own Stop, and the
        // mirror of the start order, where the display has to come up first so
        // input can reach the session object it creates.
        if (input is not null && input.running)
            Step("XRInputSubsystem.Stop", () => input.Stop());
        if (display is not null && display.running)
            Step("XRDisplaySubsystem.Stop", () => display.Stop());

        if (pausing)
        {
            // A real Unity Stop, which is two lines. OpenXRLoader.StopInternal
            // is both of them: Internal_EndSession, then a message pump.
            //
            // 0.17.0 left the session BEGUN on the theory that one which stops
            // submitting frames is acceptable. The field refuted it: the monitor
            // returned to the flat game, but the HEADSET froze on the last
            // submitted stereo frame - which is what a compositor does with a
            // live session that went quiet. It keeps reprojecting the last layer
            // it was handed, because nothing told it the application was done
            // with the display.
            //
            // The pump is not decoration. EndSession is what takes the runtime
            // off the running states and hands the display back, and the events
            // that report it - XrStopping, XrIdle, and later the XrReady a
            // resume waits for - exist only for someone who pumps.
            //
            // Still NOT called, and this is the whole second-boot fix. No
            // RequestExitSession: xrRequestExitSession commits the session to
            // STOPPING and then EXITING, and EXITING's only legal exit is
            // xrDestroySession. No DestroySession: the log shows that one call
            // firing XrDestroySession AND XrDestroyInstance, and the XrInstance
            // is created inside the display subsystem's native
            // Display_Initialize, which runs on genuine subsystem creation only.
            // So the session OBJECT, the instance, the attached action sets and
            // the device definitions all survive. Ending is reversible where
            // destroying is not.
            answeredStopping = false;
            Step("session_EndSession", Native.EndSession);
            Native.PumpMessageLoop();

            phase = Phase.Paused;
            status = "Paused, session ended. The last-event line says whether it took.";
            LoggerInstance.Msg("Paused. The session was ENDED but not destroyed; session object, "
                + "instance, attached action sets and device definitions all survive. "
                + $"{bootKey.Value} resumes.");
            return;
        }

        // Unity's order, and note it is the REVERSE of what this method had:
        // EndSession belongs to Unity's Stop and RequestExitSession to its
        // Deinitialize, in that order. This mod had them swapped.
        Step("session_EndSession", Native.EndSession);
        Step("session_RequestExitSession", Native.RequestExitSession);
        Step("session_DestroySession", Native.DestroySession);

        // The subsystems are no longer destroyed, and that is deliberate.
        //
        // Since the input layer started attaching action sets, F6 takes the
        // whole game down with it. The log ends immediately after
        // session_DestroySession, so it dies inside one of the Destroy calls
        // that used to follow. Before the input layer existed the same sequence
        // completed cleanly through main_UnloadOpenXRLibrary, which points at
        // destroying an input subsystem whose action sets are attached, or at
        // destroying subsystems after their session is already gone.
        //
        // Stop is enough for what shutdown has to achieve: rendering returns to
        // flat and the runtime releases the headset. Leaving the subsystem
        // objects alive costs nothing, because the process is going to end
        // anyway, and it turns a crash into a clean exit.
        //
        // Unloading the library is skipped for the same reason - it pulls the
        // ground out from under a plugin Unity still holds subsystem pointers
        // into.
        Step("skipping subsystem Destroy and library unload", () => { });

        display = null;
        input = null;
        phase = Phase.ShutDown;
        status = "Shut down. A real reboot needs a game restart, not the boot key.";
    }

    // The way back out of Paused, and deliberately tiny.
    //
    // No re-initialisation: instance and session were never destroyed. No
    // BeginSession: the session is still begun, and a second xrBeginSession on a
    // running session returns XR_ERROR_SESSION_RUNNING. No XRInput.Setup: the
    // action sets are still attached, and the OpenXR package's own flag agrees -
    // its actionSetsAttached is set once on start and cleared ONLY in
    // Deinitialize, never in Stop. Unity re-begins a session without
    // re-attaching action sets, so a resume that never ended one certainly must
    // not.
    //
    // THE ONE UNKNOWN, and it is load-bearing: whether XRDisplaySubsystem.Start
    // succeeds after Stop while the session is still begun and was never ended.
    // Untested. What IS tested is the INPUT subsystem, which has done a
    // Stop/Start round trip inside a live session since 0.15.0. Read that
    // evidence carefully though - the two "ok" lines it produces come from the
    // Action overload of Step, which logs ok unconditionally and therefore only
    // proves nothing threw. The real proof is downstream, in design document
    // section 51: both hands reported devicepose and pointer ACTIVE after the
    // round trip, which a stopped input subsystem cannot produce.
    //
    // The display owns the swapchains, so it is a different animal. If Start
    // returns with running false, the fallback is to make the pause a real Unity
    // Stop: add session_EndSession on the way out, and on the way back
    // CreateSessionIfNeeded, wait for XrReady, then display.Start,
    // session_BeginSession, input.Start - still without XRInput.Setup.
    private void Resume()
    {
        LoggerInstance.Msg("=== OpenXR resume ===");

        if (display is null)
        {
            Fail("Nothing to resume: the display subsystem reference is gone.");
            return;
        }

        LoggerInstance.Msg($"  last event while paused  {(sawAnyEvent ? lastEvent.ToString() : "none")}");
        LoggerInstance.Msg("  XrReady means Pump starts the subsystems this very frame. Anything "
            + "else means it waits for one, and keeps waiting rather than failing.");

        // Back into the cold start's waiting room, and nothing more. Pump runs
        // later in this same OnUpdate, so the rest happens without being asked:
        // session_CreateSessionIfNeeded, wait for XrReady, StartSubsystems. That
        // is Unity's StartInternal too, which opens with CreateSessionIfNeeded on
        // EVERY start and is re-entered from the XrReady event.
        //
        // lastEvent is deliberately NOT reset, and it is the one line on which
        // Boot and Resume disagree. Boot resets it because a stale XrFocused
        // misgates Pump. Resume must keep it, because the XrReady it waits for
        // may have ALREADY arrived: session state events fire on CHANGE, the
        // runtime is free to take an idle session back to XrReady within
        // milliseconds of the pause, and while paused nothing will ask again.
        // Resetting it would mean waiting for an event that has happened.
        phase = Phase.Initialized;
        status = "Resuming. Pumping the message loop, waiting for XrReady.";
    }

    // ~~~~~~~~~~~~ Gate experiment, design document option X6 ~~~~~~~~~~~~

    private void RunGateExperiment()
    {
        LoggerInstance.Msg("=== Gate experiment, forcing XRBackend.IsEnabled ===");
        LoggerInstance.Msg($"  before   IsEnabled {BackendGate()}");

        forceBackendGate = true;
        forceConditionalGate = true;
        var forced = BackendGate();
        LoggerInstance.Msg($"  after    IsEnabled {forced}");

        if (forced != "True")
        {
            // The patch did not take. That is a result in itself: it means
            // MelonLoader could not detour this il2cpp method, and X6 needs a
            // different lever rather than a better prompt.
            LoggerInstance.Error("  the patch did not change the value. Harmony could not detour this method.");
            status = "Gate patch had no effect. See the log.";
            return;
        }

        var prefab = FindInitializerPrefab();
        if (prefab is null)
        {
            status = "Gate forced, but no XRInitializer prefab was found.";
            return;
        }

        // Instantiating is the risky half. XRBackend.Awake enables
        // m_xrObjectsToEnable and disables m_nonXRObjectsToDisable, which can
        // pull the flat player, camera or input out from under the running game.
        LoggerInstance.Warning("  instantiating XRInitializer. The flat game may break from here on.");
        try
        {
            var instance = UnityEngine.Object.Instantiate(prefab);
            instance.name = "WetRealityXRInitializer";
            LoggerInstance.Msg($"  ok       Instantiate, activeSelf {instance.activeSelf}, " +
                $"activeInHierarchy {instance.activeInHierarchy}");

            // Version 0.4.0 stopped here and wondered why nothing happened. The
            // prefab is stored inactive, a clone of an inactive object is
            // inactive too, and Unity does not run Awake on an inactive
            // GameObject. So the whole point of instantiating - letting
            // XRBackend.Awake run - was silently skipped.
            if (!instance.activeSelf)
            {
                instance.SetActive(true);
                LoggerInstance.Msg($"  ok       SetActive(true), activeInHierarchy now {instance.activeInHierarchy}");
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Error($"  FAILED   Instantiate: {exception.GetType().Name}: {exception.Message}");
            status = "Instantiating XRInitializer threw. See the log.";
            return;
        }

        // Awake loads the XR manager through Addressables, so nothing useful can
        // be read in the same frame.
        gateReportDue = Time.realtimeSinceStartup + 5f;
        status = "Gate forced and XRInitializer instantiated. Reporting again in 5 s.";
    }

    private void ReportGateOutcome()
    {
        LoggerInstance.Msg("=== Gate experiment, 5 s later ===");
        LoggerInstance.Msg($"  IsEnabled      {BackendGate()}");
        Value("  IsRunning     ", () => Il2CppFuturLab.XR.XRBackend.IsRunning.ToString());
        Value("  IsInitialized ", () => Il2CppFuturLab.XR.XRBackend.IsInitialized.ToString());
        LoggerInstance.Msg($"  XRSettings.enabled {XRSettings.enabled}, device \"{XRSettings.loadedDeviceName}\"");

        // The real evidence. If the game took the XR branch, its own XR input
        // and visuals should now exist as live components rather than as
        // unreferenced prefabs.
        Count("FuturLab.PW2.XR.XRPlayerInputManager", Il2CppType.Of<Il2CppFuturLab.PW2.XR.XRPlayerInputManager>);
        Count("FuturLab.PW2.XR.XRPlayerInputHand", Il2CppType.Of<Il2CppFuturLab.PW2.XR.XRPlayerInputHand>);
        Count("FuturLab.PW2.CharacterVisualsXR", Il2CppType.Of<Il2CppFuturLab.PW2.CharacterVisualsXR>);
        Count("FuturLab.XR.XRInputManager", Il2CppType.Of<Il2CppFuturLab.XR.XRInputManager>);

        status = $"Gate experiment done. IsEnabled {BackendGate()}. See the log.";
    }

    private void Count(string label, Func<Il2CppSystem.Type> type)
    {
        try
        {
            var found = Resources.FindObjectsOfTypeAll(type());
            LoggerInstance.Msg($"  {found.Length,4}  {label}");
        }
        catch (Exception exception)
        {
            LoggerInstance.Msg($"     ?  {label}: unreadable, {exception.GetType().Name}");
        }
    }

    private static string BackendGate()
    {
        try
        {
            return Il2CppFuturLab.XR.XRBackend.IsEnabled.ToString();
        }
        catch (Exception exception)
        {
            return $"unreadable, {exception.GetType().Name}";
        }
    }

    // The XRInitializer carrying XRBackend is a loaded prefab, not a scene
    // object - design document section 39. FindObjectsOfTypeAll sees it because
    // that call includes assets, which is exactly why it is used here.
    private GameObject? FindInitializerPrefab()
    {
        try
        {
            var found = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Il2CppFuturLab.XR.XRBackend>());
            LoggerInstance.Msg($"  found    {found.Length} XRBackend instance(s)");

            for (var index = 0; index < found.Length; index++)
            {
                var backend = found[index].TryCast<Il2CppFuturLab.XR.XRBackend>();
                if (backend is null)
                    continue;

                var gameObject = backend.gameObject;

                // activeSelf is the meaningful one for a prefab asset.
                // activeInHierarchy is always false for something that is not in
                // a scene, so it says nothing.
                LoggerInstance.Msg($"    \"{gameObject.name}\", scene \"{gameObject.scene.name}\", " +
                    $"activeSelf {gameObject.activeSelf}, activeInHierarchy {gameObject.activeInHierarchy}");
                LoggerInstance.Msg($"    xrObjectsToEnable {Length(backend.m_xrObjectsToEnable)}, " +
                    $"nonXRObjectsToDisable {Length(backend.m_nonXRObjectsToDisable)}, " +
                    $"xrManagerToInstantiate {(backend.m_xrManagerToInstantiate is null ? "none" : "set")}");
                return gameObject;
            }
        }
        catch (Exception exception)
        {
            LoggerInstance.Error($"  FAILED   locating the prefab: {exception.GetType().Name}: {exception.Message}");
        }

        return null;
    }

    private static string Length(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GameObject>? array) =>
        array is null ? "null" : array.Length.ToString(Invariant);

    // ~~~~~~~~~~~~ Helpers ~~~~~~~~~~~~

    // Finds a registered descriptor by id and creates its subsystem.
    //
    // SubsystemManager only offers generic enumeration, which is awkward across
    // il2cpp interop. SubsystemDescriptorStore exposes the backing list as a
    // plain static property instead. Create lives on the generic descriptor
    // base, so the concrete descriptor type has to be cast to before calling it.
    private TSubsystem? Create<TDescriptor, TSubsystem>(string id)
        where TDescriptor : IntegratedSubsystemDescriptor<TSubsystem>
        where TSubsystem : IntegratedSubsystem
    {
        lastStep = $"creating subsystem \"{id}\"";

        var descriptors = SubsystemDescriptorStore.s_IntegratedDescriptors;
        if (descriptors is null)
        {
            LoggerInstance.Error("  s_IntegratedDescriptors is null. No subsystems are registered at all.");
            return null;
        }

        for (var index = 0; index < descriptors.Count; index++)
        {
            var descriptor = descriptors[index];
            if (descriptor is null || !string.Equals(descriptor.id, id, StringComparison.Ordinal))
                continue;

            var typed = descriptor.TryCast<TDescriptor>();
            if (typed is null)
            {
                LoggerInstance.Error($"  descriptor \"{id}\" is not a {typeof(TDescriptor).Name}.");
                return null;
            }

            try
            {
                return typed.Create();
            }
            catch (NullReferenceException)
            {
                // Interop's Create casts the result without a null check, so a
                // native refusal surfaces as an NRE from get_Pointer rather than
                // as a null return. Caught here so the log says what actually
                // happened; the reason itself is only ever in Player.log, under
                // "Failed to initialize subsystem".
                LoggerInstance.Error(
                    $"  the native side refused to create \"{id}\". The reason is in Player.log " +
                    "under LocalLow/FuturLab, section Unity OpenXR Diagnostic Report.");
                return null;
            }
        }

        LoggerInstance.Error($"  no registered descriptor with id \"{id}\".");
        return null;
    }

    private bool Step(string what, Func<bool> action)
    {
        lastStep = what;
        var result = action();
        LoggerInstance.Msg($"  {(result ? "ok      " : "FAILED  ")} {what}");
        return result;
    }

    private void Step(string what, Action action)
    {
        lastStep = what;
        action();
        LoggerInstance.Msg($"  ok       {what}");
    }

    private void Value(string label, Func<string> read)
    {
        try
        {
            LoggerInstance.Msg($"{label} {read()}");
        }
        catch (Exception exception)
        {
            LoggerInstance.Msg($"{label} unreadable, {exception.GetType().Name}");
        }
    }

    // Asks the runtime which interaction profile it has actually bound per hand,
    // instead of assuming it took the one we suggested.
    //
    // This is the measurement the controller problem needed. The controllers were
    // demonstrably awake - checked with pointer rays in Virtual Desktop - yet no
    // controller device appeared anywhere. If VDXR binds a profile we never
    // suggested bindings for, that is exactly the result it would produce.
    //
    // Compared as path ids: each candidate string is converted with StringToPath
    // and matched against the id the runtime reports. No PathToString needed, and
    // every value stays a primitive.
    private void ReportInteractionProfiles()
    {
        foreach (var hand in new[] { "/user/hand/left", "/user/hand/right" })
        {
            try
            {
                if (!Native.StringToPath(hand, out var handPath))
                {
                    LoggerInstance.Warning($"  profile   {hand}: StringToPath refused");
                    continue;
                }

                if (!Native.GetCurrentInteractionProfile(handPath, out var current))
                {
                    LoggerInstance.Warning($"  profile   {hand}: no current interaction profile");
                    continue;
                }

                if (current == 0UL)
                {
                    LoggerInstance.Warning($"  profile   {hand}: NONE bound. The runtime reports no controller here.");
                    continue;
                }

                LoggerInstance.Msg($"  profile   {hand}: id 0x{current:x}  {NameOfProfile(current)}");
            }
            catch (Exception exception)
            {
                LoggerInstance.Warning($"  profile   {hand}: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    // Every profile the OpenXR package knows about, so the reported id can be
    // named rather than left as a number.
    private static readonly string[] KnownProfiles =
    {
        "/interaction_profiles/oculus/touch_controller",
        "/interaction_profiles/meta/touch_controller_plus",
        "/interaction_profiles/facebook/touch_controller_pro",
        "/interaction_profiles/khr/simple_controller",
        "/interaction_profiles/valve/index_controller",
        "/interaction_profiles/htc/vive_controller",
        "/interaction_profiles/microsoft/motion_controller",
        "/interaction_profiles/hp/mixed_reality_controller",
        "/interaction_profiles/ext/hand_interaction_ext",
        "/interaction_profiles/microsoft/hand_interaction",
    };

    private string NameOfProfile(ulong id)
    {
        foreach (var candidate in KnownProfiles)
        {
            if (Native.StringToPath(candidate, out var candidateId) && candidateId == id)
                return candidate;
        }

        return "UNKNOWN, not among the profiles this package defines";
    }

    private void RequestExtensions(string when)
    {
        foreach (var extension in RequestedExtensions)
        {
            var accepted = Native.RequestEnableExtensionString(extension);
            LoggerInstance.Msg($"  {(accepted ? "ok      " : "REFUSED ")} request {extension}  ({when})");
        }
    }

    private void Fail(string reason)
    {
        phase = Phase.Failed;
        status = reason;
        LoggerInstance.Error($"XR boot aborted at {lastStep}. {reason}");
    }

    // Unity hashes the application version into a uint for the runtime: the
    // first four bytes of the MD5, byte reversed on little endian machines.
    private static uint VersionHash(string version)
    {
        var digest = MD5.HashData(Encoding.UTF8.GetBytes(version));
        if (BitConverter.IsLittleEndian)
            Array.Reverse(digest);
        return BitConverter.ToUInt32(digest, 0);
    }

    private static string SupportedDevices()
    {
        try
        {
            var devices = XRSettings.supportedDevices;
            if (devices is null || devices.Length == 0)
                return "none";

            var names = new List<string>();
            for (var index = 0; index < devices.Length; index++)
                names.Add(devices[index]);
            return string.Join(", ", names);
        }
        catch (Exception exception)
        {
            return $"unreadable, {exception.GetType().Name}";
        }
    }
}
