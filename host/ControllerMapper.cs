using System.Diagnostics;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace QuestPad.Host;

internal sealed class ControllerMapper
{
    private const uint BtnA = 1u << 0;
    private const uint BtnB = 1u << 1;
    private const uint BtnX = 1u << 2;
    private const uint BtnY = 1u << 3;
    private const uint BtnLThumb = 1u << 4;
    private const uint BtnRThumb = 1u << 5;
    private const uint BtnMenuRaw = 1u << 6;

    private const double Deadzone = 0.08;
    private const float ShoulderPress = 0.62f;
    private const float ShoulderRelease = 0.45f;
    private const float DpadPress = 0.55f;
    private const float DpadRelease = 0.35f;
    private const float DpadIntent = 0.18f;
    private const float GuideTriggerThreshold = 0.85f;
    private const double GuideHoldSeconds = 0.75;
    private const double MenuHoldSeconds = 0.50;
    private const int StartPulseFrames = 3;

    private bool leftShoulder;
    private bool rightShoulder;
    private bool dpadUp;
    private bool dpadDown;
    private bool dpadLeft;
    private bool dpadRight;
    private bool menuWasDown;
    private bool menuUsed;
    private bool startHoldActive;
    private bool viewHoldActive;
    private long menuDownTicks;
    private long guideStartTicks;
    private int startPulseFrames;

    public void Reset()
    {
        leftShoulder = rightShoulder = false;
        ClearDpad();
        menuWasDown = false;
        menuUsed = false;
        startHoldActive = false;
        viewHoldActive = false;
        menuDownTicks = 0;
        guideStartTicks = 0;
        startPulseFrames = 0;
    }

    public void Apply(
        IXbox360Controller pad,
        uint buttons,
        float lx,
        float ly,
        float rx,
        float ry,
        float lt,
        float rt,
        float lg,
        float rg)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        bool menu = (buttons & BtnMenuRaw) != 0;
        bool leftThumb = (buttons & BtnLThumb) != 0;
        bool rightThumb = (buttons & BtnRThumb) != 0;

        if (menu && !menuWasDown)
        {
            menuDownTicks = nowTicks;
            startPulseFrames = 0;
            startHoldActive = false;
            menuUsed = false;
            guideStartTicks = 0;
            ClearDpad();
        }

        // View is a latched chord: Menu + R3 starts a real held Back/View button,
        // and it remains held for as long as R3 itself stays physically depressed.
        // This lets the user release Menu after initiating the chord, which is both
        // more comfortable and friendlier to games that distinguish tap vs hold.
        if (viewHoldActive && !rightThumb)
            viewHoldActive = false;

        bool view = viewHoldActive;
        bool guide = false;
        bool startHeld = false;
        float outRx = rx;
        float outRy = ry;
        float outLt = lt;
        float outRt = rt;
        bool outRightThumb = rightThumb;

        if (menu)
        {
            // Once a plain Menu hold has committed to Start/Menu, keep that mode
            // until release. This prevents a late camera movement from unexpectedly
            // turning a held Start button into the D-pad modifier layer.
            if (!startHoldActive && !menuUsed && menuDownTicks != 0 &&
                SecondsSince(menuDownTicks, nowTicks) >= MenuHoldSeconds)
            {
                startHoldActive = true;
            }

            if (startHoldActive)
            {
                startHeld = true;
            }
            else
            {
                // Ergonomic modifier layer: left thumb holds Menu while the right thumb
                // gets an entire D-pad without requiring impossible same-hand chords.
                if (Math.Abs(rx) >= DpadIntent || Math.Abs(ry) >= DpadIntent)
                    menuUsed = true;

                UpdateDpad(rx, ry);
                outRx = outRy = 0.0f; // never leak camera movement while using the D-pad layer

                // Menu + R3 -> Back/View. Activation latches until R3 is released,
                // so long-press behavior is a genuine continuous Xbox Back/View hold.
                if (rightThumb)
                {
                    viewHoldActive = true;
                    view = true;
                    outRightThumb = false;
                    menuUsed = true;
                }

                // The Meta/System button belongs to Horizon OS and is not a safe app binding.
                // Menu + both triggers held deliberately for 750 ms supplies Xbox Guide.
                bool guideChord = lt >= GuideTriggerThreshold && rt >= GuideTriggerThreshold;
                if (guideChord)
                {
                    menuUsed = true;
                    outLt = outRt = 0.0f; // don't fire/brake in-game while invoking Guide
                    if (guideStartTicks == 0)
                        guideStartTicks = nowTicks;
                    guide = SecondsSince(guideStartTicks, nowTicks) >= GuideHoldSeconds;
                }
                else
                {
                    guideStartTicks = 0;
                }
            }
        }
        else
        {
            if (menuWasDown)
            {
                // A quick, otherwise-unused Menu press remains a normal Start/Menu tap.
                // A committed long hold has already been sent continuously and must not
                // generate another pulse on release.
                if (!menuUsed && !startHoldActive)
                    startPulseFrames = StartPulseFrames;
                ClearDpad();
            }
            startHoldActive = false;
            menuDownTicks = 0;
            guideStartTicks = 0;
        }

        if (viewHoldActive)
        {
            view = true;
            outRightThumb = false;
        }

        menuWasDown = menu;

        // Touch grips are analog, but Xbox shoulders are digital. Hysteresis avoids
        // chatter around the threshold while keeping a natural squeeze gesture.
        leftShoulder = Hysteresis(leftShoulder, lg, ShoulderPress, ShoulderRelease);
        rightShoulder = Hysteresis(rightShoulder, rg, ShoulderPress, ShoulderRelease);

        var left = Radial(lx, ly);
        var right = Radial(outRx, outRy);

        pad.ResetReport();
        pad.SetAxisValue(Xbox360Axis.LeftThumbX, ToShort(left.x));
        pad.SetAxisValue(Xbox360Axis.LeftThumbY, ToShort(left.y));
        pad.SetAxisValue(Xbox360Axis.RightThumbX, ToShort(right.x));
        pad.SetAxisValue(Xbox360Axis.RightThumbY, ToShort(right.y));
        pad.SetSliderValue(Xbox360Slider.LeftTrigger, ToByte(outLt));
        pad.SetSliderValue(Xbox360Slider.RightTrigger, ToByte(outRt));

        Set(pad, Xbox360Button.LeftShoulder, leftShoulder);
        Set(pad, Xbox360Button.RightShoulder, rightShoulder);
        Set(pad, Xbox360Button.A, (buttons & BtnA) != 0);
        Set(pad, Xbox360Button.B, (buttons & BtnB) != 0);
        Set(pad, Xbox360Button.X, (buttons & BtnX) != 0);
        Set(pad, Xbox360Button.Y, (buttons & BtnY) != 0);
        Set(pad, Xbox360Button.LeftThumb, leftThumb);
        Set(pad, Xbox360Button.RightThumb, outRightThumb);
        Set(pad, Xbox360Button.Up, dpadUp);
        Set(pad, Xbox360Button.Down, dpadDown);
        Set(pad, Xbox360Button.Left, dpadLeft);
        Set(pad, Xbox360Button.Right, dpadRight);
        Set(pad, Xbox360Button.Back, view);
        Set(pad, Xbox360Button.Start, startHeld || startPulseFrames > 0);
        Set(pad, Xbox360Button.Guide, guide);
        pad.SubmitReport();

        if (startPulseFrames > 0)
            startPulseFrames--;
    }

    private void UpdateDpad(float x, float y)
    {
        dpadRight = AxisHysteresis(dpadRight, x, true);
        dpadLeft = AxisHysteresis(dpadLeft, x, false);
        dpadUp = AxisHysteresis(dpadUp, y, true);
        dpadDown = AxisHysteresis(dpadDown, y, false);

        // Diagonals are valid; opposite directions are not.
        if (dpadLeft && dpadRight)
        {
            if (x >= 0) dpadLeft = false;
            else dpadRight = false;
        }
        if (dpadUp && dpadDown)
        {
            if (y >= 0) dpadDown = false;
            else dpadUp = false;
        }
    }

    private static bool AxisHysteresis(bool current, float value, bool positive)
    {
        float signed = positive ? value : -value;
        return current ? signed > DpadRelease : signed >= DpadPress;
    }

    private static bool Hysteresis(bool current, float value, float press, float release) =>
        current ? value > release : value >= press;

    private void ClearDpad() => dpadUp = dpadDown = dpadLeft = dpadRight = false;

    private static void Set(IXbox360Controller pad, Xbox360Button button, bool pressed) =>
        pad.SetButtonState(button, pressed);

    private static short ToShort(float value)
    {
        double x = Math.Clamp(value, -1.0f, 1.0f);
        if (x <= -1.0) return short.MinValue;
        return (short)Math.Round(x * short.MaxValue);
    }

    private static byte ToByte(float value) =>
        (byte)Math.Clamp(Math.Round(Math.Clamp(value, 0.0f, 1.0f) * 255.0), 0, 255);

    private static (float x, float y) Radial(float x, float y)
    {
        double magnitude = Math.Sqrt(x * x + y * y);
        if (magnitude <= Deadzone) return (0, 0);
        double scaled = Math.Min(1.0, (magnitude - Deadzone) / (1.0 - Deadzone));
        double k = scaled / magnitude;
        return ((float)(x * k), (float)(y * k));
    }

    private static double SecondsSince(long oldTicks, long newTicks) =>
        (newTicks - oldTicks) / (double)Stopwatch.Frequency;
}
