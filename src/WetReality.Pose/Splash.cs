using System;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace WetReality;

// The Wet Reality plate, shown in the headset while the game is still dark.
//
// WHAT THIS CAN AND CANNOT COVER. Nothing this mod draws can appear before the
// XR session presents frames, so the loader's own half minute of preparation and
// the engine's start-up stay black no matter what. What is coverable is the gap
// between "OpenXR is running" and the game's first meaningful frame - which is
// the black the player actually sits through with the headset already on.
//
// A WORLD-SPACE CANVAS, NOT A QUAD. A quad needs a material, and a material
// needs a shader, and Shader.Find is unreliable in a stripped IL2CPP build -
// WashLaser carries a fallback that borrows a material from an existing
// LineRenderer for exactly that reason. A CanvasRenderer asks Unity itself for
// the default UI material, so there is nothing to look up and nothing to fall
// back from.
//
// The texture is EMBEDDED in this assembly rather than installed as a file. One
// less thing for the installer to place, one less path to get wrong, and no
// "image missing" state to design a fallback for.
internal sealed class Splash
{
    private const string ResourceName = "WetReality.startup-logo.png";
    private const string HolderName = "WetRealitySplash";

    // Canvas units. The plate is scaled to metres by localScale, so this only
    // sets the internal resolution of the rect - big enough that the RawImage is
    // never asked to stretch a small rect over a large texture.
    private const float CanvasWidth = 1600f;

    private GameObject? holder;
    private Texture2D? texture;
    private CanvasGroup? group;
    private RectTransform? rect;

    private bool loadFailed;
    private bool loggedOnce;
    private float startedAt = -1f;
    private float aspect = 1545f / 888f;

    internal bool Visible => holder is not null && holder != null && startedAt >= 0f;

    // Asked for by the XR-running transition, and by the re-show key. Kept
    // separate from the drawing so the request can happen in OnUpdate while the
    // positioning happens in OnLateUpdate, after the camera has moved.
    internal void Request(MelonLogger.Instance log)
    {
        if (loadFailed)
            return;

        startedAt = Time.unscaledTime;
        loggedOnce = false;

        if (group is not null && group != null)
            group.alpha = 1f;

        log.Msg("Splash: requested.");
    }

    internal void Hide()
    {
        startedAt = -1f;

        if (group is not null && group != null)
            group.alpha = 0f;

        if (holder is not null && holder != null)
            holder.SetActive(false);
    }

    // Returns a one-line status for the diagnostics block. Called every frame
    // while XR runs, BEFORE the player-resolved gate: the plate's whole purpose
    // is the window in which no player exists yet.
    internal string Tick(MelonLogger.Instance log, float holdSeconds, float fadeSeconds,
        float distance, float widthMetres)
    {
        if (startedAt < 0f)
            return "splash: idle";

        if (loadFailed)
            return "splash: texture unavailable";

        var camera = Camera.main;

        // No camera yet is not a failure - it is the normal state one frame
        // after a scene swap. The plate simply waits, and its timer waits with
        // it, so a hold of two seconds means two seconds of VISIBLE plate.
        if (camera is null)
        {
            startedAt = Time.unscaledTime;
            return "splash: waiting for a camera";
        }

        if (!Ensure(log))
            return "splash: could not be built";

        var elapsed = Time.unscaledTime - startedAt;

        if (elapsed >= holdSeconds + fadeSeconds)
        {
            Hide();
            log.Msg($"Splash: done after {elapsed:0.00} s.");
            return "splash: finished";
        }

        var alpha = 1f;

        if (elapsed > holdSeconds && fadeSeconds > 0.01f)
            alpha = 1f - ((elapsed - holdSeconds) / fadeSeconds);

        if (group is not null && group != null)
            group.alpha = Mathf.Clamp01(alpha);

        // Re-seated every frame rather than parented to the camera. The camera
        // is re-created across scene loads, and a parented plate would be
        // destroyed with it in the middle of its own hold.
        var eye = camera.transform;
        holder!.transform.position = eye.position + (eye.forward * distance);
        holder.transform.rotation = eye.rotation;

        // Canvas units to metres, preserving the image's own aspect.
        var scale = widthMetres / CanvasWidth;
        holder.transform.localScale = new Vector3(scale, scale, scale);

        if (rect is not null && rect != null)
            rect.sizeDelta = new Vector2(CanvasWidth, CanvasWidth / aspect);

        if (!loggedOnce)
        {
            loggedOnce = true;
            log.Msg($"  splash: {texture!.width}x{texture.height}  aspect {aspect:0.000}"
                + $"  {widthMetres:0.00} m wide at {distance:0.00} m"
                + $"  camera \"{camera.name}\"  layer {holder.layer}"
                + $"  hold {holdSeconds:0.00} s + fade {fadeSeconds:0.00} s");
        }

        return $"splash: {Mathf.Clamp01(alpha):0.00} alpha, {elapsed:0.0} s";
    }

    // ------------------------------------------------------------------ build

    private bool Ensure(MelonLogger.Instance log)
    {
        if (holder is not null && holder != null)
        {
            if (!holder.activeSelf)
                holder.SetActive(true);

            return true;
        }

        // A destroyed holder leaves a non-null wrapper behind, so both tests are
        // needed before deciding to build a second one. The same Unity-null trap
        // that cost four diagnoses elsewhere in this mod.
        holder = null;

        if (!LoadTexture(log))
            return false;

        try
        {
            // Layer 0. PlayerCamera's cullingMask has the Default layer set, so
            // no layer juggling is needed - the same reasoning WashLaser uses.
            holder = new GameObject(HolderName);
            holder.layer = 0;

            var canvas = holder.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            // Above the game's own UI, which the mod reparents to a
            // ScreenSpaceCamera canvas at plane distance 2.
            canvas.sortingOrder = 32000;

            rect = holder.GetComponent<RectTransform>();

            if (rect is not null && rect != null)
                rect.sizeDelta = new Vector2(CanvasWidth, CanvasWidth / aspect);

            group = holder.AddComponent<CanvasGroup>();
            group.alpha = 1f;
            group.interactable = false;
            group.blocksRaycasts = false;

            var child = new GameObject("Plate");
            child.layer = 0;
            child.transform.SetParent(holder.transform, false);

            var image = child.AddComponent<RawImage>();
            image.texture = texture;
            image.raycastTarget = false;

            var childRect = child.GetComponent<RectTransform>();

            if (childRect is not null && childRect != null)
            {
                childRect.anchorMin = Vector2.zero;
                childRect.anchorMax = Vector2.one;
                childRect.offsetMin = Vector2.zero;
                childRect.offsetMax = Vector2.zero;
            }

            log.Msg("  splash: canvas built, world space, layer 0, RawImage attached.");
            return true;
        }
        catch (Exception error)
        {
            log.Warning("  splash: building the canvas threw " + error.GetType().Name
                + " - " + error.Message);
            loadFailed = true;
            return false;
        }
    }

    private bool LoadTexture(MelonLogger.Instance log)
    {
        if (texture is not null && texture != null)
            return true;

        texture = null;

        try
        {
            var assembly = Assembly.GetExecutingAssembly();

            using var stream = assembly.GetManifestResourceStream(ResourceName);

            if (stream is null)
            {
                // Named, with the alternatives, because a mismatch between the
                // csproj LogicalName and this constant is the one way this can
                // fail silently at build time.
                log.Warning($"  splash: embedded resource \"{ResourceName}\" not found. Present: "
                    + string.Join(", ", assembly.GetManifestResourceNames()));
                loadFailed = true;
                return false;
            }

            var bytes = new byte[stream.Length];
            var read = 0;

            while (read < bytes.Length)
            {
                var step = stream.Read(bytes, read, bytes.Length - read);

                if (step <= 0)
                    break;

                read += step;
            }

            // mipChain false: the plate is shown at one distance and one size,
            // so mips would only cost memory.
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);

            // Explicit Il2CppStructArray rather than relying on an implicit
            // conversion from byte[] - the interop layer takes the managed array
            // for some signatures and not others, and a wrong guess here throws
            // at runtime rather than at build time.
            var loaded = ImageConversion.LoadImage(texture, new Il2CppStructArray<byte>(bytes));

            if (!loaded || texture.width <= 2)
            {
                log.Warning($"  splash: LoadImage refused {read} byte(s); "
                    + $"texture is {texture.width}x{texture.height}.");
                loadFailed = true;
                return false;
            }

            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            aspect = (float)texture.width / texture.height;

            log.Msg($"  splash: texture loaded, {read} byte(s) -> {texture.width}x{texture.height}.");
            return true;
        }
        catch (Exception error)
        {
            log.Warning("  splash: loading the texture threw " + error.GetType().Name
                + " - " + error.Message);
            loadFailed = true;
            return false;
        }
    }

    // ---------------------------------------------------------------- teardown

    internal void Dispose(MelonLogger.Instance log)
    {
        startedAt = -1f;

        try
        {
            if (holder is not null && holder != null)
                UnityEngine.Object.Destroy(holder);

            // Destroying the holder does NOT take the texture with it: it was
            // created here, not loaded from an asset, so it is this object's to
            // release. WashLaser learned the same lesson about its material.
            if (texture is not null && texture != null)
                UnityEngine.Object.Destroy(texture);
        }
        catch (Exception error)
        {
            log.Warning("  splash: teardown threw " + error.GetType().Name);
        }

        holder = null;
        texture = null;
        group = null;
        rect = null;
    }
}
