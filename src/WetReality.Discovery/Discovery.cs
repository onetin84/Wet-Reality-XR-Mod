using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.SubsystemsImplementation;

[assembly: MelonInfo(typeof(WetReality.Discovery), "Wet Reality Discovery", "0.6.0", "Wet Reality")]
[assembly: MelonGame("FuturLab", "PowerWash Simulator 2")]

namespace WetReality;

// Answers the central reverse engineering question of the design document,
// section 17: which transform carries the washer aim offset, and where does
// PWS2 pull the camera along once the flat aim limit is reached?
//
// Take a baseline, move the washer, compare: the report lists every transform
// whose local pose changed, ordered by rotation delta. That measurement has
// already settled section 17 - see section 35 of the design document.
//
// Reports are written to UserData/WetReality. Everything is read-only; no game
// state is modified.
public sealed class Discovery : MelonMod
{
    private readonly record struct Pose(Vector3 LocalPosition, Quaternion LocalRotation, Vector3 WorldPosition);
    private readonly record struct Entry(string Path, Pose Pose);
    private readonly record struct Change(string Path, float Rotation, float Position, Pose Before, Pose After);

    // Keyed by il2cpp object pointer rather than by path. Paths are not unique
    // in practice, and keying on them silently dropped transforms: the first
    // hierarchy dump walked 6166 transforms while the snapshot retained 4220.
    // Pointers also make "appeared" and "gone" real object identity instead of
    // a name comparison.
    private sealed record Snapshot(Dictionary<IntPtr, Entry> Entries, int Visited, int DistinctPaths);

    // Below these the values are float noise rather than movement.
    private const float RotationFloor = 0.01f;
    private const float PositionFloor = 0.0005f;

    // Always reported, changed or not. "HeadTurn did not move" is itself a
    // result: it is what separates a camera that follows only at the aim limit
    // from one that follows proportionally from the start.
    private static readonly string[] Watched =
    {
        "HeadTurn[0]", "PlayerCamera[1]", "EquipmentAnchor[2]", "Rig_PlayerArmsPivot[3]",
    };

    // Component names that decide whether PWS2 builds its HUD and radial menus
    // with uGUI or with UI Toolkit. The first dump found a single Canvas, a
    // WorldSpace nameplate, and opening a radial menu changed the hierarchy not
    // at all - which fits UI Toolkit, but also fits a menu that was not open.
    // Component types are read from the il2cpp class, so detecting them needs
    // no reference to the assemblies that declare them.
    private static readonly string[] UiMarkers =
    {
        "UIDocument", "PanelSettings", "UIElements", "Canvas", "Graphic", "Image", "Text",
        "Button", "Selectable", "EventSystem", "InputModule", "Raycaster", "UIRenderer", "Panel",
    };

    // Names worth a full component listing in the hierarchy dump.
    private static readonly string[] Interesting =
    {
        "washer", "gun", "weapon", "hose", "nozzle", "spray", "jet", "water", "lance",
        "hand", "arm", "player", "pivot", "aim", "camera", "head", "rig", "body", "look",
    };

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private MelonPreferences_Category settings = null!;
    private MelonPreferences_Entry<string> baselineKey = null!;
    private MelonPreferences_Entry<string> compareKey = null!;
    private MelonPreferences_Entry<string> dumpKey = null!;
    private MelonPreferences_Entry<string> focus = null!;
    private MelonPreferences_Entry<string> dumpDelay = null!;

    private Snapshot? baseline;
    private string baselineLabel = "";
    private int reportIndex;
    private string status = "Waiting for a key.";
    private string lastError = "";
    private float dumpDue = -1f;

    public override void OnInitializeMelon()
    {
        settings = MelonPreferences.CreateCategory("WetRealityDiscovery");
        baselineKey = settings.CreateEntry("BaselineKey", "F9", description: "Records a baseline pose for every transform.");
        compareKey = settings.CreateEntry("CompareKey", "F10", description: "Compares against the baseline and writes a report.");
        dumpKey = settings.CreateEntry("DumpKey", "F11", description: "Dumps cameras, canvases and the render pipeline.");
        focus = settings.CreateEntry("FocusPath", "PlayerCharacter",
            description: "Changes whose path contains this are reported first and logged. Animated scenery otherwise dominates the ranking.");
        dumpDelay = settings.CreateEntry("DumpDelaySeconds", "3",
            description: "Delay between pressing the dump key and taking the dump, so transient UI such as a held radial menu can be opened first.");

        LoggerInstance.Msg($"Ready. {baselineKey.Value} baseline, {compareKey.Value} compare, {dumpKey.Value} hierarchy dump. " +
            $"Focus \"{focus.Value}\". Reports go to {Path.Combine(MelonEnvironment.UserDataDirectory, "WetReality")}.");
    }

    public override void OnUpdate()
    {
        try
        {
            if (Pressed(baselineKey)) TakeBaseline();
            else if (Pressed(compareKey)) Compare();
            else if (Pressed(dumpKey)) ScheduleDump();
            else if (dumpDue >= 0f && Time.realtimeSinceStartup >= dumpDue)
            {
                dumpDue = -1f;
                Dump();
            }
        }
        catch (Exception exception)
        {
            // Deduplicated: OnUpdate runs every frame and an exception that
            // repeats - a keyboard read that this build refuses, say - would
            // otherwise bury the log.
            var message = exception.ToString();
            if (message != lastError)
            {
                lastError = message;
                status = $"Failed: {exception.GetType().Name}. See the log.";
                LoggerInstance.Error($"Discovery step failed: {exception}");
            }
        }
    }

    // MelonLoader forwards Unity's OnGUI to melons, so this needs no injected
    // behaviour. The MelonLoader console sits behind the game in fullscreen,
    // which makes an on-screen status the only feedback available mid-test.
    public override void OnGUI()
    {
        GUI.Box(new Rect(8f, 8f, 620f, 82f), GUIContent.none);
        GUI.Label(new Rect(16f, 12f, 604f, 20f),
            $"Wet Reality Discovery   {baselineKey.Value} baseline   {compareKey.Value} compare   {dumpKey.Value} dump");
        GUI.Label(new Rect(16f, 32f, 604f, 20f),
            baseline is null ? "No baseline yet." : $"Baseline {baselineLabel}: {baseline.Entries.Count} transforms.");
        GUI.Label(new Rect(16f, 52f, 604f, 36f), dumpDue < 0f
            ? status
            : $"Dump in {Number(dumpDue - Time.realtimeSinceStartup)} s. Open the menu you want captured now.");
    }

    private static bool Pressed(MelonPreferences_Entry<string> entry) =>
        Enum.TryParse<KeyCode>(entry.Value, ignoreCase: true, out var key) && Input.GetKeyDown(key);

    // ~~~~~~~~~~~~ Snapshot and comparison ~~~~~~~~~~~~

    private void TakeBaseline()
    {
        baseline = Capture();
        baselineLabel = DateTime.Now.ToString("HH:mm:ss", Invariant);
        status = $"Baseline taken: {baseline.Entries.Count} transforms. Move the washer, then press {compareKey.Value}.";
        LoggerInstance.Msg($"{status} Walked {baseline.Visited}, {baseline.DistinctPaths} distinct paths.");
    }

    private void Compare()
    {
        if (baseline is null)
        {
            status = $"No baseline yet. Press {baselineKey.Value} first.";
            LoggerInstance.Warning(status);
            return;
        }

        var current = Capture();
        var changes = new List<Change>();

        foreach (var (pointer, after) in current.Entries)
        {
            if (!baseline.Entries.TryGetValue(pointer, out var before))
                continue;

            var rotation = Quaternion.Angle(before.Pose.LocalRotation, after.Pose.LocalRotation);
            var position = (after.Pose.LocalPosition - before.Pose.LocalPosition).magnitude;

            if (rotation > RotationFloor || position > PositionFloor)
                changes.Add(new Change(after.Path, rotation, position, before.Pose, after.Pose));
        }

        changes.Sort((left, right) => right.Rotation != left.Rotation
            ? right.Rotation.CompareTo(left.Rotation)
            : right.Position.CompareTo(left.Position));

        var focused = changes.Where(change => change.Path.Contains(focus.Value, StringComparison.OrdinalIgnoreCase)).ToList();
        var watchLines = WatchLines(current).ToList();
        var reportPath = Write("comparison", Render(current, changes, focused, watchLines));

        status = $"{changes.Count} changed, {focused.Count} in focus. Report {Path.GetFileName(reportPath)}.";
        LoggerInstance.Msg($"{changes.Count} transforms changed, {focused.Count} matching \"{focus.Value}\". Report: {reportPath}");

        // Only watched and focused entries are logged. The global ranking is
        // dominated by animated scenery - sprinklers and cat rigs outranked the
        // entire player chain in both aim measurements.
        foreach (var line in watchLines)
            LoggerInstance.Msg($"  watch  {line}");

        foreach (var change in focused.Take(10))
            LoggerInstance.Msg($"  focus  {Number(change.Rotation)} deg  {Number(change.Position)} m  {Shorten(change.Path)}");

        if (changes.Count > focused.Count)
            LoggerInstance.Msg($"  {changes.Count - focused.Count} further changes outside the focus are in the report only.");

        // The new snapshot becomes the next baseline so the aim test can be
        // walked in stages: inside the free range, then past the limit.
        baseline = current;
        baselineLabel = DateTime.Now.ToString("HH:mm:ss", Invariant);
    }

    private string Render(Snapshot current, List<Change> changes, List<Change> focused, List<string> watchLines)
    {
        var report = new StringBuilder();
        report.AppendLine($"Wet Reality transform comparison, baseline {baselineLabel} to {DateTime.Now.ToString("HH:mm:ss", Invariant)}");
        report.AppendLine($"Unity {Application.unityVersion}, {baseline!.Entries.Count} transforms in the baseline, {current.Entries.Count} now.");
        report.AppendLine($"Walked {current.Visited} transforms, {current.DistinctPaths} distinct paths." +
            (current.DistinctPaths == current.Visited ? "" : " Paths are not unique, which is why entries are keyed by object pointer."));
        report.AppendLine($"{changes.Count} changed their local pose, {focused.Count} of them matching the focus \"{focus.Value}\".");
        report.AppendLine($"Appeared since the baseline: {current.Entries.Keys.Except(baseline.Entries.Keys).Count()}");
        report.AppendLine($"Gone since the baseline: {baseline.Entries.Keys.Except(current.Entries.Keys).Count()}");
        report.AppendLine($"Noise floor {Number(RotationFloor)} deg and {Number(PositionFloor)} m. Angles in degrees, distances in metres.");
        report.AppendLine();

        report.AppendLine("WATCHED, reported whether they changed or not");
        foreach (var line in watchLines)
            report.AppendLine($"  {line}");
        report.AppendLine();

        report.AppendLine($"FOCUS \"{focus.Value}\", ordered by rotation delta");
        report.AppendLine();
        foreach (var change in focused)
            Append(report, change);

        report.AppendLine();
        report.AppendLine("ALL CHANGES, ordered by rotation delta");
        report.AppendLine();
        foreach (var change in changes)
            Append(report, change);

        return report.ToString();
    }

    private static void Append(StringBuilder report, Change change)
    {
        report.AppendLine(change.Path);
        report.AppendLine($"    rotated {Number(change.Rotation)} deg, moved {Number(change.Position)}");
        report.AppendLine($"    local euler {Vector(change.Before.LocalRotation.eulerAngles)} -> {Vector(change.After.LocalRotation.eulerAngles)}");
        report.AppendLine($"    local pos   {Vector(change.Before.LocalPosition)} -> {Vector(change.After.LocalPosition)}");
        report.AppendLine($"    world pos   {Vector(change.Before.WorldPosition)} -> {Vector(change.After.WorldPosition)}");
    }

    private IEnumerable<string> WatchLines(Snapshot current)
    {
        foreach (var token in Watched)
        {
            var match = current.Entries.FirstOrDefault(pair => pair.Value.Path.EndsWith(token, StringComparison.Ordinal));
            if (match.Value.Path is null)
            {
                yield return $"{token}: not present";
                continue;
            }

            var after = match.Value.Pose;
            if (!baseline!.Entries.TryGetValue(match.Key, out var before))
            {
                yield return $"{token}: new since the baseline, local euler {Vector(after.LocalRotation.eulerAngles)}";
                continue;
            }

            var rotation = Quaternion.Angle(before.Pose.LocalRotation, after.LocalRotation);
            var position = (after.LocalPosition - before.Pose.LocalPosition).magnitude;

            yield return rotation <= RotationFloor && position <= PositionFloor
                ? $"{token}: UNCHANGED, local euler {Vector(after.LocalRotation.eulerAngles)}"
                : $"{token}: {Number(rotation)} deg, {Number(position)} m, local euler " +
                  $"{Vector(before.Pose.LocalRotation.eulerAngles)} -> {Vector(after.LocalRotation.eulerAngles)}";
        }
    }

    private static Snapshot Capture()
    {
        var entries = new Dictionary<IntPtr, Entry>(capacity: 8192);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var visited = 0;

        foreach (var root in Roots())
        {
            Walk(root, transform =>
            {
                visited++;
                var path = PathOf(transform);
                paths.Add(path);
                entries[transform.Pointer] = new Entry(path,
                    new Pose(transform.localPosition, transform.localRotation, transform.position));
            });
        }

        return new Snapshot(entries, visited, paths.Count);
    }

    // ~~~~~~~~~~~~ Hierarchy dump ~~~~~~~~~~~~

    // A radial menu that stays open only while its button is held cannot be
    // captured by a key pressed at the same time, so the dump is deferred and
    // the on-screen status counts down.
    private void ScheduleDump()
    {
        var delay = float.TryParse(dumpDelay.Value, NumberStyles.Float, Invariant, out var parsed) ? Math.Max(0f, parsed) : 3f;
        if (delay <= 0f)
        {
            Dump();
            return;
        }

        dumpDue = Time.realtimeSinceStartup + delay;
        status = $"Dump scheduled in {Number(delay)} s.";
        LoggerInstance.Msg($"Dump scheduled in {Number(delay)} s. Open the menu that should be captured.");
    }

    private void Dump()
    {
        var report = new StringBuilder();
        report.AppendLine($"Wet Reality hierarchy dump, {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Invariant)}");
        report.AppendLine($"Unity {Application.unityVersion}");
        report.AppendLine($"Graphics: {SystemInfo.graphicsDeviceType} {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceVersion})");
        report.AppendLine($"Screen: {Screen.width}x{Screen.height}, fullscreen {Screen.fullScreenMode}");
        report.AppendLine($"Render pipeline: {PipelineName()}");
        report.AppendLine();

        report.AppendLine("SCENES");
        for (var index = 0; index < SceneManager.sceneCount; index++)
        {
            var scene = SceneManager.GetSceneAt(index);
            report.AppendLine($"  {scene.name}  loaded {scene.isLoaded}  path {scene.path}");
        }
        report.AppendLine();

        var cameras = new List<Transform>();
        var canvases = new List<Transform>();
        var matches = new List<Transform>();
        var total = 0;

        var cameraType = Il2CppType.Of<Camera>();
        var canvasType = Il2CppType.Of<Canvas>();

        var census = new Dictionary<string, int>(StringComparer.Ordinal);
        var uiHits = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var visited = new HashSet<IntPtr>();

        foreach (var root in Roots())
        {
            Walk(root, transform =>
            {
                total++;
                visited.Add(transform.Pointer);
                if (transform.GetComponent(cameraType) is not null) cameras.Add(transform);
                if (transform.GetComponent(canvasType) is not null) canvases.Add(transform);
                else if (Interesting.Any(word => transform.name.Contains(word, StringComparison.OrdinalIgnoreCase)))
                    matches.Add(transform);

                foreach (var component in Components(transform))
                {
                    census[component] = census.GetValueOrDefault(component) + 1;
                    if (!UiMarkers.Any(marker => component.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    if (!uiHits.TryGetValue(component, out var paths))
                        uiHits[component] = paths = new List<string>();
                    if (paths.Count < 10)
                        paths.Add(PathOf(transform));
                }
            });
        }

        report.AppendLine($"CAMERAS ({cameras.Count} found, Camera.main is {(Camera.main is null ? "null" : PathOf(Camera.main.transform))})");
        foreach (var transform in cameras)
        {
            var camera = transform.GetComponent(cameraType)!.TryCast<Camera>()!;
            report.AppendLine($"  {PathOf(transform)}");
            report.AppendLine($"    active {transform.gameObject.activeInHierarchy}, enabled {camera.enabled}, depth {Number(camera.depth)}");
            report.AppendLine($"    fov {Number(camera.fieldOfView)}, near {Number(camera.nearClipPlane)}, far {Number(camera.farClipPlane)}, orthographic {camera.orthographic}");
            report.AppendLine($"    clear {camera.clearFlags}, cullingMask 0x{camera.cullingMask:x8}, targetTexture {(camera.targetTexture is null ? "none" : camera.targetTexture.name)}");
            report.AppendLine($"    stereoTargetEye {camera.stereoTargetEye}, HDR {camera.allowHDR}, MSAA {camera.allowMSAA}, dynamicResolution {camera.allowDynamicResolution}");
            report.AppendLine("    ancestor chain, nearest first:");
            foreach (var level in Ancestors(transform))
            {
                report.AppendLine($"      {level.name}  localPos {Vector(level.localPosition)}  localEuler {Vector(level.localEulerAngles)}");
                report.AppendLine($"        components: {string.Join(", ", Components(level))}");
            }
            report.AppendLine();
        }

        report.AppendLine($"CANVASES ({canvases.Count})");
        foreach (var transform in canvases)
        {
            var canvas = transform.GetComponent(canvasType)!.TryCast<Canvas>()!;
            report.AppendLine($"  {PathOf(transform)}");
            report.AppendLine($"    renderMode {canvas.renderMode}, sortingOrder {canvas.sortingOrder}, planeDistance {Number(canvas.planeDistance)}");
            report.AppendLine($"    worldCamera {(canvas.worldCamera is null ? "none" : PathOf(canvas.worldCamera.transform))}, active {transform.gameObject.activeInHierarchy}");
        }
        report.AppendLine();

        report.AppendLine($"NAME MATCHES ({matches.Count}) for: {string.Join(", ", Interesting)}");
        foreach (var transform in matches)
        {
            report.AppendLine($"  {PathOf(transform)}");
            report.AppendLine($"    localPos {Vector(transform.localPosition)}  localEuler {Vector(transform.localEulerAngles)}  worldPos {Vector(transform.position)}");
            report.AppendLine($"    components: {string.Join(", ", Components(transform))}");
        }

        report.AppendLine();
        report.AppendLine($"UI COMPONENTS ({uiHits.Count} distinct types). Settles uGUI against UI Toolkit.");
        foreach (var (component, paths) in uiHits.OrderByDescending(hit => census[hit.Key]).ThenBy(hit => hit.Key, StringComparer.Ordinal))
        {
            report.AppendLine($"  {component} x{census[component]}");
            foreach (var location in paths)
                report.AppendLine($"      {location}");
            if (census[component] > paths.Count)
                report.AppendLine($"      ... and {census[component] - paths.Count} more");
        }
        report.AppendLine();

        report.AppendLine($"COMPONENT CENSUS ({census.Count} distinct types, {census.Values.Sum()} components)");
        foreach (var (component, count) in census.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal))
            report.AppendLine($"  {count,6}  {component}");

        report.AppendLine();
        AppendCoverage(report, visited);

        report.AppendLine();
        AppendSubsystems(report);

        report.AppendLine();
        AppendXrState(report, census);

        report.AppendLine();
        report.AppendLine($"{total} transforms walked.");

        var path = Write("hierarchy", report.ToString());
        status = $"Dumped {total} transforms, {cameras.Count} cameras, {canvases.Count} canvases, " +
            $"{matches.Count} name matches. Report {Path.GetFileName(path)}.";
        LoggerInstance.Msg($"{status} Full path: {path}");
    }

    // Turns silent incompleteness into a reported number.
    //
    // Resources.FindObjectsOfTypeAll sees every loaded Transform: inactive ones,
    // objects in scenes SceneManager does not list, and objects belonging to no
    // scene at all because they came out of an asset bundle. A nonzero miss count
    // is therefore normal and not a defect. What matters is the breakdown by
    // scene - if DontDestroyOnLoad shows up here with a large count, the walk is
    // broken again, and no absence in this report may be trusted.
    private static void AppendCoverage(StringBuilder report, HashSet<IntPtr> visited)
    {
        report.AppendLine("WALK COVERAGE");

        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Transform>());
            var missedByScene = new Dictionary<string, int>(StringComparer.Ordinal);
            var samples = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var missed = 0;

            for (var index = 0; index < all.Length; index++)
            {
                var transform = all[index].TryCast<Transform>();
                if (transform is null || visited.Contains(transform.Pointer))
                    continue;

                missed++;
                var scene = transform.gameObject.scene;
                var key = string.IsNullOrEmpty(scene.name) ? "(no scene, asset or prefab)" : scene.name;
                missedByScene[key] = missedByScene.GetValueOrDefault(key) + 1;

                // Only roots are sampled. Listing missed children would bury the
                // one thing worth reading, which is which hierarchies were skipped.
                if (transform.parent is null)
                {
                    if (!samples.TryGetValue(key, out var roots))
                        samples[key] = roots = new List<string>();
                    if (roots.Count < 12)
                        roots.Add(transform.name);
                }
            }

            report.AppendLine($"  Transforms loaded {all.Length}, visited by the walk {visited.Count}, missed {missed}.");

            foreach (var (scene, count) in missedByScene.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal))
            {
                report.AppendLine($"    {count,6}  missed in {scene}");
                if (samples.TryGetValue(scene, out var roots))
                    foreach (var name in roots)
                        report.AppendLine($"            root: {name}");
            }
        }
        catch (Exception exception)
        {
            report.AppendLine($"  Could not be determined: {exception.GetType().Name}: {exception.Message}");
        }
    }

    // Reads both sides of the XR question in one place.
    //
    // Unity side: whether any XR provider is loaded and running at all. The build
    // ships no provider - no UnityOpenXR.dll, no XR Management - so enabled is
    // expected to be false, and this section exists to prove it rather than
    // assume it.
    //
    // Game side: PWS2 carries FuturLab's complete XR layer, gated behind the
    // static XRBackend.IsEnabled, which has a getter and no setter and no
    // backing field, so it is computed. XRSDKConditional offers OpenXRRunning
    // and WarnIfXRNotInitialized, which suggests the gate is the running XR
    // runtime rather than a build flag. If Unity ever reports enabled true while
    // IsEnabled stays false, that hypothesis is dead and the gate is build data.
    private static void AppendXrState(StringBuilder report, Dictionary<string, int> census)
    {
        report.AppendLine("XR STATE");

        report.AppendLine("  Unity, UnityEngine.XR.XRSettings:");
        Value(report, "    enabled", () => UnityEngine.XR.XRSettings.enabled.ToString());
        Value(report, "    isDeviceActive", () => UnityEngine.XR.XRSettings.isDeviceActive.ToString());
        Value(report, "    loadedDeviceName", () => $"\"{UnityEngine.XR.XRSettings.loadedDeviceName}\"");
        Value(report, "    stereoRenderingMode", () => UnityEngine.XR.XRSettings.stereoRenderingMode.ToString());
        Value(report, "    eyeTexture", () => $"{UnityEngine.XR.XRSettings.eyeTextureWidth}x{UnityEngine.XR.XRSettings.eyeTextureHeight}");
        Value(report, "    supportedDevices", () =>
        {
            var devices = UnityEngine.XR.XRSettings.supportedDevices;
            if (devices is null || devices.Length == 0)
                return "none";

            var names = new List<string>();
            for (var index = 0; index < devices.Length; index++)
                names.Add(devices[index]);
            return string.Join(", ", names);
        });

        report.AppendLine("  Game, FuturLab.XR.XRBackend:");
        Value(report, "    IsEnabled", () => Il2CppFuturLab.XR.XRBackend.IsEnabled.ToString());
        Value(report, "    IsRunning", () => Il2CppFuturLab.XR.XRBackend.IsRunning.ToString());
        Value(report, "    IsInitialized", () => Il2CppFuturLab.XR.XRBackend.IsInitialized.ToString());
        AppendInstances(report, "XRBackend", () => Il2CppType.Of<Il2CppFuturLab.XR.XRBackend>());

        var xrTypes = census
            .Where(entry => NamesXr(entry.Key))
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .ToList();

        report.AppendLine($"  XR-named components in the walked hierarchy: {xrTypes.Count} distinct types.");
        foreach (var (component, count) in xrTypes)
            report.AppendLine($"    {count,6}  {component}");
    }

    // Two attempts at this filter were wrong before this one, in both
    // directions. A case insensitive substring match on "xr" hits VfxRoot; a
    // case sensitive one still hits VFXRenderer. So the test is structural: a
    // namespace segment that IS "XR", as in FuturLab.XR.XRBackend, or a type
    // name that starts with it, as in FuturLab.PW2.XRHapticController.
    private static bool NamesXr(string fullName)
    {
        var segments = fullName.Split('.');
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (string.Equals(segments[index], "XR", StringComparison.Ordinal))
                return true;
        }

        return segments[^1].StartsWith("XR", StringComparison.Ordinal);
    }

    // The go/no-go gate for the whole OpenXR provider approach.
    //
    // Unity reads Data/UnitySubsystems/<library>/UnitySubsystemsManifest.json at
    // startup and registers the declared descriptors long before any melon
    // runs. So if "OpenXR Display" and "OpenXR Input" show up here, the native
    // plugin was found and loaded, and the remaining work is to replicate the
    // managed loader sequence by P/Invoke. If they do not show up, no amount of
    // mod code will help and the approach is dead.
    //
    // SubsystemManager offers only generic enumeration, which is painful across
    // il2cpp interop. SubsystemDescriptorStore exposes the backing lists as
    // plain static properties instead, which needs no generic instantiation.
    private static void AppendSubsystems(StringBuilder report)
    {
        report.AppendLine("SUBSYSTEM DESCRIPTORS");

        try
        {
            var integrated = SubsystemDescriptorStore.s_IntegratedDescriptors;
            report.AppendLine($"  integrated: {(integrated is null ? "null" : integrated.Count.ToString(Invariant))}");
            if (integrated is not null)
            {
                for (var index = 0; index < integrated.Count; index++)
                {
                    var descriptor = integrated[index];
                    report.AppendLine(descriptor is null
                        ? "      <null>"
                        : $"      id \"{descriptor.id}\"   [{TypeNameOf(descriptor)}]");
                }
            }
        }
        catch (Exception exception)
        {
            report.AppendLine($"  integrated: unreadable, {exception.GetType().Name}: {exception.Message}");
        }

        try
        {
            var standalone = SubsystemDescriptorStore.s_StandaloneDescriptors;
            report.AppendLine($"  standalone: {(standalone is null ? "null" : standalone.Count.ToString(Invariant))}");
            if (standalone is not null)
            {
                for (var index = 0; index < standalone.Count; index++)
                {
                    var descriptor = standalone[index];
                    report.AppendLine(descriptor is null
                        ? "      <null>"
                        : $"      id \"{descriptor.id}\"   [{TypeNameOf(descriptor)}]");
                }
            }
        }
        catch (Exception exception)
        {
            report.AppendLine($"  standalone: unreadable, {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void Value(StringBuilder report, string label, Func<string> read)
    {
        try
        {
            report.AppendLine($"{label}: {read()}");
        }
        catch (Exception exception)
        {
            // A single unreadable property must not cost the whole section. The
            // Span marshalling layer of this Cpp2IL and Il2CppInterop combination
            // is known broken, so some Unity 6 bindings throw on access.
            report.AppendLine($"{label}: unreadable, {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void AppendInstances(StringBuilder report, string label, Func<Il2CppSystem.Type> type)
    {
        try
        {
            var found = Resources.FindObjectsOfTypeAll(type());
            report.AppendLine($"    {label} instances: {found.Length}");
            for (var index = 0; index < found.Length && index < 12; index++)
            {
                var component = found[index].TryCast<Component>();
                report.AppendLine($"      {(component is null ? found[index].name : PathOf(component.transform))}");
            }
        }
        catch (Exception exception)
        {
            report.AppendLine($"    {label} instances: unreadable, {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string PipelineName()
    {
        try
        {
            var current = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            var fallback = UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline;
            return $"current {(current is null ? "none, built-in" : TypeNameOf(current))}, default {(fallback is null ? "none" : TypeNameOf(fallback))}";
        }
        catch (Exception exception)
        {
            return $"could not be read: {exception.GetType().Name}";
        }
    }

    // ~~~~~~~~~~~~ Traversal helpers ~~~~~~~~~~~~

    // Scene roots, the DontDestroyOnLoad scene, and the roots of every enabled
    // camera.
    //
    // The DontDestroyOnLoad part matters more than it looks. SceneManager does
    // not enumerate that scene, and version 0.4.0 claimed to cover it through
    // the camera roots alone - which only works for hierarchies that happen to
    // contain a camera. PWS2 parks its HUD and menu system there without one,
    // so the 12:29 dump reported no UI at all and no EventSystem anywhere,
    // which reads as "the game has no uGUI" when it actually means "the walker
    // never went there". Hence the probe object below: marking a throwaway
    // GameObject DontDestroyOnLoad puts it in that scene, and the scene handle
    // then hands over its roots like any other.
    private static IEnumerable<Transform> Roots()
    {
        var seen = new HashSet<IntPtr>();

        for (var index = 0; index < SceneManager.sceneCount; index++)
        {
            var scene = SceneManager.GetSceneAt(index);
            if (!scene.isLoaded)
                continue;

            foreach (var transform in RootsOf(scene, seen))
                yield return transform;
        }

        foreach (var transform in PersistentRoots(seen))
            yield return transform;

        var cameras = Camera.allCameras;
        for (var index = 0; index < cameras.Length; index++)
        {
            var root = cameras[index].transform.root;
            if (seen.Add(root.Pointer))
                yield return root;
        }
    }

    private static IEnumerable<Transform> RootsOf(Scene scene, HashSet<IntPtr> seen)
    {
        var roots = scene.GetRootGameObjects();
        for (var index = 0; index < roots.Length; index++)
        {
            var transform = roots[index].transform;
            if (seen.Add(transform.Pointer))
                yield return transform;
        }
    }

    private static IEnumerable<Transform> PersistentRoots(HashSet<IntPtr> seen)
    {
        GameObject? probe = null;
        Scene scene;

        try
        {
            probe = new GameObject("WetRealityPersistentSceneProbe");
            UnityEngine.Object.DontDestroyOnLoad(probe);
            scene = probe.scene;
        }
        catch (Exception)
        {
            // Never let a diagnostic detail cost us the whole dump.
            if (probe is not null)
                UnityEngine.Object.Destroy(probe);
            yield break;
        }

        // The probe itself is a root of that scene, so it is filtered out here
        // rather than showing up in the report as a transform of the game.
        seen.Add(probe.transform.Pointer);

        try
        {
            foreach (var transform in RootsOf(scene, seen))
                yield return transform;
        }
        finally
        {
            UnityEngine.Object.Destroy(probe);
        }
    }

    private static void Walk(Transform transform, Action<Transform> visit)
    {
        visit(transform);
        for (var index = 0; index < transform.childCount; index++)
            Walk(transform.GetChild(index), visit);
    }

    private static IEnumerable<Transform> Ancestors(Transform transform)
    {
        for (var current = transform; current is not null; current = current.parent)
            yield return current;
    }

    // The sibling index keeps same-named siblings apart. It is not a unique key
    // on its own, which is why snapshots key on the object pointer instead.
    private static string PathOf(Transform transform)
    {
        var parts = new List<string>();
        for (var current = transform; current is not null; current = current.parent)
            parts.Add($"{current.name}[{current.GetSiblingIndex()}]");

        parts.Reverse();
        return string.Join("/", parts);
    }

    // Keeps log lines readable: the washer chain paths run past 200 characters.
    private static string Shorten(string path)
    {
        var parts = path.Split('/');
        return parts.Length <= 4 ? path : $".../{string.Join("/", parts.Skip(parts.Length - 3))}";
    }

    // Reading the il2cpp class directly, because components coming out of an
    // array are wrapped as their declared type and would all report
    // "UnityEngine.Component" via GetType.
    private static IEnumerable<string> Components(Transform transform)
    {
        var components = transform.GetComponents(Il2CppType.Of<Component>());
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            yield return component is null ? "<null>" : TypeNameOf(component);
        }
    }

    private static string TypeNameOf(Il2CppObjectBase instance)
    {
        var klass = IL2CPP.il2cpp_object_get_class(instance.Pointer);
        if (klass == IntPtr.Zero)
            return "<unknown>";

        var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) ?? "<unnamed>";
        var space = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass));
        return string.IsNullOrEmpty(space) ? name : $"{space}.{name}";
    }

    // ~~~~~~~~~~~~ Output ~~~~~~~~~~~~

    private string Write(string kind, string content)
    {
        var directory = Path.Combine(MelonEnvironment.UserDataDirectory, "WetReality");
        Directory.CreateDirectory(directory);

        // Timestamped, because the counter restarts at 01 every launch and was
        // therefore overwriting earlier reports. One 11:44 hierarchy dump was
        // lost that way before this was noticed. The index is kept so several
        // reports taken within the same second still get distinct names.
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", Invariant);
        var path = Path.Combine(directory, $"{kind}-{stamp}-{++reportIndex:00}.txt");
        File.WriteAllText(path, content);
        return path;
    }

    private static string Number(float value) => value.ToString("0.####", Invariant);

    private static string Vector(Vector3 value) =>
        $"({Number(value.x)}, {Number(value.y)}, {Number(value.z)})";
}
