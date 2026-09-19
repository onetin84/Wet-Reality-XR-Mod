using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;

namespace WetReality;

// A red line from the nozzle along the direction being measured. Draws only -
// it writes nothing to the game, holds no Harmony patch and touches no game
// component.
//
// Why it exists. The wash ray has been attacked three times on inference and
// missed three times, and section 58 then drew the wrong conclusion from the
// right log: the frames it quoted came from AFTER the OpenXR shutdown, when the
// mod had stopped driving the gun. Numbers in a log can be read from the wrong
// window. A line in the picture, held against the patch that actually gets
// clean, cannot - PROVIDED the line never lies about being live. Which is the
// whole burden of this class, and the first version of it failed exactly there:
//
//   * every catch called Release() without destroying the GameObject, so a
//     throw left an ENABLED red line frozen in the DontDestroyOnLoad scene,
//     unreachable by both toggles, and the 5-second retry stacked a new one on
//     top every 5 seconds;
//   * nothing hid the line when OpenXR stopped or the job ended, so the
//     instrument built to expose the section 58 trap reproduced it.
//
// Hence: ONE teardown path (Drop), called from every catch, and a liveness flag
// the owner must refresh or the line goes away.
//
// Two build facts, settled by unpacking the shipped bundles rather than guessed.
// "Unlit/Color" and "Hidden/Internal-Colored" are NOT in this build - they
// appear only in the name registry of globalgamemanagers with zero compiled
// shader data, because a URP project strips the built-in GL line shader. And
// Shader.Find(string) goes through the _Injected / ManagedSpanWrapper route
// that is unreliable in this Cpp2IL and Il2CppInterop combination, so every
// call is wrapped and there is a second route that needs no string at all:
// eight LineRenderer components are already live in the scene, so a
// URP-compatible line material is loaded and can be copied.
//
// Depth testing is deliberately left alone rather than forced to an overlay
// queue. The line then stops where it meets a surface, and that end point is
// exactly what gets compared against the wet patch.
internal sealed class WashLaser
{
    // Ordered by how well this build guarantees them, not by how modern they
    // are. Sprites/Default leads because it is CONFIRMED present with compiled
    // data in Resources/unity_builtin_extra, which is always loaded; it is
    // unlit, multiplies vertex colour, and depth-tests. URP/Unlit is the native
    // pipeline match but ships only inside urp_assets_all.bundle, so it is
    // load-dependent and can be null in a menu.
    private static readonly string[] ShaderNames =
    {
        "Sprites/Default",
        "Universal Render Pipeline/Unlit",
        "Legacy Shaders/Particles/Alpha Blended",
        "UI/Default",
        "Legacy Shaders/Diffuse",
    };

    // DIESELBE LISTE MIT UI/Default VORN, und die Umstellung ist gemessen.
    //
    // Der erste Versuch, die Linie vor die Geometrie zu holen, schrieb den
    // ZTest erfolgreich - und die Linie verschwand weiter in der Wand:
    //
    //     wash laser material   via Sprites/Default   effective shader
    //                           "Sprites/Default"   colour set
    //     menu laser: ZTest Always on 3 name(s),
    //                 unity_GUIZTestMode reads back 8
    //
    // Die Ruecklesung sagt 8, also ist der Schreibvorgang angekommen. Nur liest
    // Sprites/Default die Eigenschaft NICHT: sein ShaderLab nennt kein
    // ZTest [...], damit bleibt es beim festen LEqual. Dieselbe Falle wie bei
    // den UI-Materialien, einen Stock tiefer - "gesetzt" ist nicht
    // "wirksam", und nur das Bild entscheidet.
    //
    // UI/Default dagegen traegt ZTest [unity_GUIZTestMode] und ist fuer diese
    // Frage KEIN Kandidat mehr, sondern belegt: die UI des Spiels benutzt genau
    // diesen Shader, und dort hat dieselbe Eigenschaft das Clipping beseitigt.
    // Er ist ebenfalls unbeleuchtet, alpha-gemischt und multipliziert die
    // Vertexfarbe - fuer eine Linie also derselbe Zweck wie Sprites/Default.
    private static readonly string[] OnTopShaderNames =
    {
        "UI/Default",
        "Sprites/Default",
        "Universal Render Pipeline/Unlit",
        "Legacy Shaders/Particles/Alpha Blended",
        "Legacy Shaders/Diffuse",
    };

    private static readonly string[] ColorProperties = { "_BaseColor", "_Color", "_TintColor" };

    private static readonly Color Red = new(1f, 0.05f, 0.05f, 1f);

    private const string HolderName = "WetReality_WashLaser";

    private GameObject? holder;
    private LineRenderer? line;
    private bool alwaysOnTop;
    private string label = "wash";
    private Material? material;
    private string materialSource = "";

    // The colour carries which decoupling candidate is active. It has to,
    // because the on-screen overlay sticks to the face in the headset and is
    // unreadable, and the spectator view freezes - so text is not a channel.
    // Re-applied only on change, since SetColor per frame is pointless work.
    private Color applied = Color.clear;

    private float nextTry;
    private int failures;
    private bool loggedFirstDraw;

    internal string Draw(MelonLogger.Instance log, Vector3 origin, Vector3? direction,
        string mode, float length, float width, Color color, bool onTop)
    {
        // VOR Ensure gesetzt, denn dort entsteht das Material und dort wird der
        // ZTest geschrieben. Als Parameter und nicht als Eigenschaft, damit
        // beide Aufrufstellen ihn sichtbar mitgeben - diese Klasse kennt keine
        // Preferences und soll keine kennen.
        alwaysOnTop = onTop;
        label = mode;

        if (direction is null)
            return Hide($"laser {mode}: no direction");

        var forward = direction.Value;

        // Guarded here as well as at the source: a zero vector would collapse
        // both endpoints onto each other and draw a dot that looks like a bug.
        if (forward.sqrMagnitude < 1e-6f)
            return Hide($"laser {mode}: zero direction");

        if (width <= 0f)
            return Hide($"laser {mode}: width is {width:0.###}");

        if (!Ensure(log))
            return $"laser {mode}: no renderer";

        var drawn = Mathf.Max(0.05f, length);

        try
        {
            var renderer = line!;
            var end = origin + forward.normalized * drawn;

            renderer.positionCount = 2;
            renderer.SetPosition(0, origin);
            renderer.SetPosition(1, end);
            renderer.startWidth = width;
            renderer.endWidth = width;

            if (color != applied)
            {
                applied = color;
                Paint(renderer, material, color);
            }

            renderer.enabled = true;

            // Logged once, from the values computed HERE rather than read back
            // from renderer.bounds. Bounds is a 24-byte struct returned by
            // value, the shape that killed this process through InputDevices,
            // and this is pure diagnostics - not worth a run. isVisible is a
            // bool, so "created but culled" is still distinguishable from
            // "created but invisible material".
            if (!loggedFirstDraw)
            {
                loggedFirstDraw = true;
                log.Msg($"  wash laser first draw   visible {renderer.isVisible}   "
                    + $"from ({origin.x:0.##}, {origin.y:0.##}, {origin.z:0.##})   "
                    + $"to ({end.x:0.##}, {end.y:0.##}, {end.z:0.##})");
            }

            return $"laser {mode} on  {width:0.###} m x {drawn:0.#} m  [{materialSource}]";
        }
        catch (Exception exception)
        {
            // DESTROYED, not merely dropped. Releasing the references while the
            // GameObject lives is what stacked one frozen red line every five
            // seconds in the first version.
            Drop(log);
            log.Warning($"  wash laser threw {exception.GetType().Name}: {exception.Message}");
            return $"laser {mode}: failed";
        }
    }

    // Must be called on every frame the laser should NOT be drawing, including
    // the frames where the owner returns early. A line left enabled after the
    // pose driving stops is indistinguishable from a live one that agrees, and
    // that is precisely how section 58 went wrong.
    internal void Hide()
    {
        try
        {
            if (line is not null && line != null)
                line.enabled = false;
        }
        catch
        {
            // No logger here, and no orphan either: the object is destroyed and
            // the retry timer is pushed out so the next Draw cannot immediately
            // build a second one beside a surviving first.
            DestroyHolder();
            nextTry = Time.unscaledTime + 5f;
        }
    }

    private string Hide(string status)
    {
        Hide();
        return status;
    }

    internal void Dispose(MelonLogger.Instance log)
    {
        if (holder is null)
            return;

        DestroyHolder();
        log.Msg("  wash laser removed");
    }

    private void Drop(MelonLogger.Instance log)
    {
        DestroyHolder();

        failures++;

        // Backs off geometrically. A permanent failure otherwise logs a handful
        // of warnings every five seconds for the whole session and buries the
        // measurement this run exists to produce - the same drowning that
        // section 45's per-frame pose logging caused.
        var wait = Mathf.Min(5f * failures, 60f);
        nextTry = Time.unscaledTime + wait;

        if (failures == 3)
            log.Warning($"  wash laser: {failures} failures, backing off to {wait:0} s between attempts");
    }

    // The single teardown. Destroys the material too: it is not owned by the
    // GameObject, so Destroy(holder) alone leaks one Material per cycle.
    private void DestroyHolder()
    {
        var doomedHolder = holder;
        var doomedMaterial = material;

        holder = null;
        line = null;
        material = null;
        materialSource = "";
        loggedFirstDraw = false;
        applied = Color.clear;

        try
        {
            if (doomedMaterial is not null)
                UnityEngine.Object.Destroy(doomedMaterial);

            if (doomedHolder is not null)
                UnityEngine.Object.Destroy(doomedHolder);
        }
        catch
        {
            // Already gone, which is the desired end state anyway.
        }
    }

    private bool Ensure(MelonLogger.Instance log)
    {
        // "is null" is a pattern match and deliberately bypasses op_Equality, so
        // it sees only a null pointer, never a DESTROYED Unity object. The
        // second half is the Unity-semantics check, and it is what catches the
        // renderer that died with the player between two jobs.
        if (line is not null && line != null)
            return true;

        if (line is not null)
            DestroyHolder();

        if (Time.unscaledTime < nextTry)
            return false;

        nextTry = Time.unscaledTime + 5f;

        try
        {
            // Left on layer 0. PlayerCamera's cullingMask is 0x002ae717, whose
            // low byte 0x17 is bits 0, 1, 2 and 4 - so the Default layer is
            // rendered and no layer juggling is needed.
            holder = new GameObject(HolderName);

            // Kept across scene loads so the instrument does not vanish
            // mid-measurement. Same shape as Discovery's scene probe, the one
            // precedent in this project that is known to work.
            UnityEngine.Object.DontDestroyOnLoad(holder);

            line = holder.AddComponent<LineRenderer>();

            material = BuildMaterial(log);
            if (material is null)
            {
                Drop(log);
                return false;
            }

            line.material = material;

            // DIE LINIE VOR DIE GEOMETRIE - Abschnitt 104, und es ist ein
            // Versaeumnis von Abschnitt 103: dort ist die UI nach vorn geholt
            // worden, die Linie, mit der man sie bedient, nicht. Sie clippte
            // dann durch nahe Objekte, und das macht ein Menue schwerer
            // bedienbar als ein verdecktes Panel.
            //
            // DAS RISIKO DER UI-MATERIALIEN BESTEHT HIER NICHT, und darum wird
            // es genannt: beide Konstruktionswege - FromShader und
            // FromExistingLine - enden auf new Material(...), die Linie hat
            // also ihr EIGENES Material. Ein ZTest darauf beruehrt keine Linie
            // des Spiels. Bei den UI-Materialien war das anders, dort ist
            // Unitys Standard-Material eines fuer hunderte Graphics.
            if (alwaysOnTop)
            {
                var written = UiDepth.ForceAlways(material);

                log.Msg($"  {label} laser: ZTest Always on {written} name(s), "
                    + $"{UiDepth.ZTestProperties[0]} reads back "
                    + $"{UiDepth.ReadBack(material)}");
            }

            // Set as well as the material colour, for the shader families that
            // do read vertex colours - Sprites/Default and the particle shaders
            // multiply by it. Harmless for URP/Unlit, which ignores it.
            line.startColor = Red;
            line.endColor = Red;

            line.useWorldSpace = true;
            line.positionCount = 2;
            line.numCapVertices = 0;
            line.numCornerVertices = 0;

            // Correct under MultiPass stereo, which this session runs: the line
            // is drawn once per eye and faces each eye's matrix on its own.
            line.alignment = LineAlignment.View;
            line.textureMode = LineTextureMode.Stretch;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            line.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            line.enabled = false;

            failures = 0;
            return true;
        }
        catch (Exception exception)
        {
            // Same asymmetry as Draw's catch had: releasing without destroying
            // leaves a half-built holder behind that Dispose can never reach.
            Drop(log);
            log.Warning($"  creating the wash laser threw {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private Material? BuildMaterial(MelonLogger.Instance log)
    {
        var built = FromShader(log) ?? FromExistingLine(log);

        if (built is null)
        {
            // Once, not per attempt: with the backoff in Drop this would
            // otherwise be the loudest thing in the log.
            if (failures == 0)
                log.Warning("  wash laser: no usable material - neither Shader.Find nor an "
                    + "existing LineRenderer produced one. The line cannot be drawn.");

            return null;
        }

        var painted = PaintMaterial(built, Red);

        // The EFFECTIVE shader name, not the requested one. If a shader failed
        // to compile in this build Unity silently substitutes the error shader,
        // and the only visible symptom is a magenta line.
        var effective = "unknown";
        try
        {
            effective = built.shader?.name ?? "null";
        }
        catch
        {
            // Reading the name is a diagnostic, never a reason to fail.
        }

        log.Msg($"  wash laser material   via {materialSource}   effective shader \"{effective}\"   "
            + $"colour {(painted ? "set" : "NOT FOUND - line may be magenta or white")}");

        return built;
    }

    // The property name differs per shader family - _BaseColor on URP, _Color on
    // the sprite and legacy shaders, _TintColor on the particle ones - so every
    // candidate is tried behind HasProperty rather than assumed.
    private static bool PaintMaterial(Material target, Color color)
    {
        var painted = false;

        foreach (var property in ColorProperties)
        {
            if (!target.HasProperty(property))
                continue;

            target.SetColor(property, color);
            painted = true;
        }

        return painted;
    }

    private static void Paint(LineRenderer renderer, Material? target, Color color)
    {
        if (target is not null)
            PaintMaterial(target, color);

        // Also on the vertex colours, for the families that read them -
        // Sprites/Default, which this build resolves to, multiplies by them.
        renderer.startColor = color;
        renderer.endColor = color;
    }

    private Material? FromShader(MelonLogger.Instance log)
    {
        // Die Reihenfolge haengt am Schalter, nicht am Zufall: soll die Linie
        // vorn liegen, muss ein Shader her, der den ZTest ueberhaupt liest.
        foreach (var name in alwaysOnTop ? OnTopShaderNames : ShaderNames)
        {
            try
            {
                var shader = Shader.Find(name);
                if (shader is null || shader == null)
                    continue;

                materialSource = name;
                return new Material(shader);
            }
            catch (Exception exception)
            {
                // Only while it is still news. Five names times one warning
                // every retry is how a diagnostic drowns its own measurement.
                if (failures == 0)
                    log.Warning($"  Shader.Find(\"{name}\") threw {exception.GetType().Name}");
            }
        }

        return null;
    }

    // No string crosses the boundary here, which is the point: eight
    // LineRenderer components are live in this scene, so a material URP already
    // draws lines with is loaded, and copying it needs only class references -
    // the safe interop shape per section 46.
    private Material? FromExistingLine(MelonLogger.Instance log)
    {
        try
        {
            var found = Resources.FindObjectsOfTypeAll(Il2CppType.Of<LineRenderer>());

            for (var index = 0; index < found.Length; index++)
            {
                // Guarded per element rather than around the loop: one null or
                // uncastable entry must skip, not abandon the whole route.
                LineRenderer? candidate;
                Material? source;

                try
                {
                    candidate = found[index]?.TryCast<LineRenderer>();

                    // FindObjectsOfTypeAll also returns OUR line - AddComponent
                    // runs before this - and the DontDestroyOnLoad scene is
                    // included. Copying our own default material would look
                    // like a successful borrow and cost a run to notice.
                    if (candidate is null || candidate.gameObject.name == HolderName)
                        continue;

                    source = candidate.sharedMaterial;
                }
                catch
                {
                    continue;
                }

                if (source is null || source == null)
                    continue;

                materialSource = $"copy of {candidate.name}";
                return new Material(source);
            }

            if (failures == 0)
                log.Warning($"  wash laser: {found.Length} LineRenderer(s) found, none usable as a material source");
        }
        catch (Exception exception)
        {
            if (failures == 0)
                log.Warning($"  borrowing a line material threw {exception.GetType().Name}: {exception.Message}");
        }

        return null;
    }
}
