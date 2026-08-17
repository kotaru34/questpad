using System.Diagnostics;

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
    private const float GuideTriggerThreshold = 0.60f;
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
    private bool touchpadHoldActive;
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
        touchpadHoldActive = false;
        menuDownTicks = 0;
        guideStartTicks = 0;
        startPulseFrames = 0;
    }

    public LogicalGamepadState Map(
        uint buttons,
        float lx,
        float ly,
        float rx,
        float ry,
        float lt,
        float rt,
        float lg,
        float rg,
        bool enableDs4Extras = false)
    {
        long nowTicks = Stopwatch.GetTimestamp();
        bool menu = (buttons & BtnMenuRaw) != 0;
        bool leftThumb = (buttons & BtnLThumb) != 0;
        bool rightThumb = (buttons & BtnRThumb) != 0;

        // 0.4 physical layout:
        //   Touch index triggers -> digital bumpers (LB/RB, L1/R1)
        //   Touch grip analogs   -> analog triggers (LT/RT, L2/R2)
        // The logical layer stays backend-independent, so Xbox and DS4 receive the
        // same physical mapping automatically.
        float logicalLt = Math.Clamp(lg, 0.0f, 1.0f);
        float logicalRt = Math.Clamp(rg, 0.0f, 1.0f);

        if (menu && !menuWasDown)
        {
            menuDownTicks = nowTicks;
            startPulseFrames = 0;
            startHoldActive = false;
            menuUsed = false;
            guideStartTicks = 0;
            ClearDpad();
        }

        if (viewHoldActive && !leftThumb)
            viewHoldActive = false;
        if (!enableDs4Extras || !rightThumb)
            touchpadHoldActive = false;

        bool view = viewHoldActive;
        bool touchpad = touchpadHoldActive;
        bool guide = false;
        bool startHeld = false;
        float outRx = rx;
        float outRy = ry;
        float outLt = logicalLt;
        float outRt = logicalRt;
        bool outLeftThumb = leftThumb;
        bool outRightThumb = rightThumb;

        if (menu)
        {
            // Guide/PS has priority over the plain 0.50 s Menu hold. The old order
            // could commit Start/Options before a human had finished squeezing both
            // triggers, making Menu+LT+RT appear dead. Once both *logical* triggers
            // (the Touch grips in the 0.4 layout) are present, cancel plain Menu and
            // time the Guide chord independently.
            bool guideChord = logicalLt >= GuideTriggerThreshold && logicalRt >= GuideTriggerThreshold;
            if (guideChord)
            {
                menuUsed = true;
                startHoldActive = false;
                startHeld = false;
                ClearDpad();
                outLt = outRt = 0.0f;
                if (guideStartTicks == 0)
                    guideStartTicks = nowTicks;
                guide = SecondsSince(guideStartTicks, nowTicks) >= GuideHoldSeconds;
            }
            else
            {
                guideStartTicks = 0;

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
                    if (Math.Abs(rx) >= DpadIntent || Math.Abs(ry) >= DpadIntent)
                        menuUsed = true;

                    UpdateDpad(rx, ry);
                    outRx = outRy = 0.0f;

                    // User-tested 0.4 modifier placement:
                    //   Menu+L3 -> View/Share
                    //   Menu+R3 -> DS4 touchpad click
                    if (leftThumb)
                    {
                        viewHoldActive = true;
                        view = true;
                        outLeftThumb = false;
                        menuUsed = true;
                    }

                    if (rightThumb)
                    {
                        // Even in Xbox mode this is modifier intent, so do not emit
                        // an accidental Start/Menu pulse when Menu is released. Xbox
                        // has no touchpad, therefore R3 itself remains available there.
                        menuUsed = true;
                        if (enableDs4Extras)
                        {
                            touchpadHoldActive = true;
                            touchpad = true;
                            outRightThumb = false;
                        }
                    }
                }
            }
        }
        else
        {
            if (menuWasDown)
            {
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
            outLeftThumb = false;
        }
        if (enableDs4Extras && touchpadHoldActive)
        {
            touchpad = true;
            outRightThumb = false;
        }

        menuWasDown = menu;

        // The Touch index triggers are analog on the source side but bumpers are
        // digital on both target layouts, so use hysteresis instead of a raw cutoff.
        leftShoulder = Hysteresis(leftShoulder, lt, ShoulderPress, ShoulderRelease);
        rightShoulder = Hysteresis(rightShoulder, rt, ShoulderPress, ShoulderRelease);

        var left = Radial(lx, ly);
        var right = Radial(outRx, outRy);

        var state = new LogicalGamepadState
        {
            LX = left.x,
            LY = left.y,
            RX = right.x,
            RY = right.y,
            LT = Math.Clamp(outLt, 0.0f, 1.0f),
            RT = Math.Clamp(outRt, 0.0f, 1.0f),
            LB = leftShoulder,
            RB = rightShoulder,
            A = (buttons & BtnA) != 0,
            B = (buttons & BtnB) != 0,
            X = (buttons & BtnX) != 0,
            Y = (buttons & BtnY) != 0,
            L3 = outLeftThumb,
            R3 = outRightThumb,
            DpadUp = dpadUp,
            DpadDown = dpadDown,
            DpadLeft = dpadLeft,
            DpadRight = dpadRight,
            View = view,
            Menu = startHeld || startPulseFrames > 0,
            Guide = guide,
            TouchpadClick = touchpad
        };

        if (startPulseFrames > 0)
            startPulseFrames--;

        return state;
    }

    private void UpdateDpad(float x, float y)
    {
        dpadRight = AxisHysteresis(dpadRight, x, true);
        dpadLeft = AxisHysteresis(dpadLeft, x, false);
        dpadUp = AxisHysteresis(dpadUp, y, true);
        dpadDown = AxisHysteresis(dpadDown, y, false);

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
