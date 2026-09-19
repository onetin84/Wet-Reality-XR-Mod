using UnityEngine;
using UnityEngine.InputSystem;

namespace WetReality;

// Edge and hold detection for one controller button.
//
// OpenXR delivers boolean state, not events, and Unity's Hold interaction lives
// in the GAME's action asset rather than in ours - so tap and hold have to be
// derived here. That is not a workaround: BaseInput exposes the two halves as
// DIFFERENT calls (InvokeCrouchPressed against InvokeCrouchLongPressed,
// InvokeToggleTaskList against InvokeToggleFurnitureInventory), so a caller has
// to make the distinction itself. FuturLab's own shipped VR layer agrees - its
// ButtonPressType enum is {Default, LongPress, DoublePress}.
//
// The asymmetry is deliberate and unavoidable:
//
//   Hold fires while the finger is still DOWN, on crossing the threshold. A
//   furniture inventory that only appears after you let go would be useless,
//   and the game's own Hold interaction fires at the threshold too.
//
//   Tap can only fire on RELEASE, because until the finger lifts there is no
//   way to know it was a tap.
//
// So a tap costs one release of latency. That is invisible on a stance toggle
// and intolerable on a spray trigger, which is why nothing in this project puts
// a timer anywhere near the right trigger.
internal sealed class ButtonEdge
{
    private bool down;
    private float pressedAt;
    private bool holdFired;

    // True for exactly one frame.
    internal bool Pressed { get; private set; }
    internal bool Released { get; private set; }
    internal bool Tap { get; private set; }
    internal bool Hold { get; private set; }

    internal void Reset()
    {
        down = false;
        holdFired = false;
        Pressed = false;
        Released = false;
        Tap = false;
        Hold = false;
    }

    // holdSeconds of zero disables the hold half entirely, which is what a
    // button wants when it has only one job - then Tap fires on press rather
    // than on release, because there is nothing to wait for.
    internal void Poll(bool pressed, float holdSeconds)
    {
        Pressed = false;
        Released = false;
        Tap = false;
        Hold = false;

        if (pressed && !down)
        {
            down = true;
            holdFired = false;
            pressedAt = Time.unscaledTime;
            Pressed = true;

            if (holdSeconds <= 0f)
                Tap = true;

            return;
        }

        if (pressed && down)
        {
            if (holdSeconds > 0f && !holdFired
                && Time.unscaledTime - pressedAt >= holdSeconds)
            {
                holdFired = true;
                Hold = true;
            }

            return;
        }

        if (!pressed && down)
        {
            down = false;
            Released = true;

            // Only a release that never became a hold counts as a tap.
            if (holdSeconds > 0f && !holdFired)
                Tap = true;
        }
    }

    // Buttons registered as Binary come through as a Unity Button control, and
    // IsPressed is the read this project has already proven on the trigger.
    // Axis1D controls - the grip squeeze - need a value and a threshold, so they
    // use ReadAxis below instead.
    internal static bool IsDown(InputAction? action)
    {
        if (action is null)
            return false;

        try
        {
            return action.IsPressed();
        }
        catch
        {
            return false;
        }
    }

    internal static float ReadAxis(InputAction? action)
    {
        if (action is null)
            return 0f;

        try
        {
            var raw = action.ReadValueAsObject();
            return raw is null ? 0f : raw.Unbox<float>();
        }
        catch
        {
            return 0f;
        }
    }
}
