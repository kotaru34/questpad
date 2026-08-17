using QuestPad.Host;

const uint BtnL3 = 1u << 4;
const uint BtnR3 = 1u << 5;
const uint BtnMenu = 1u << 6;

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void CheckNear(float actual, float expected, string message)
{
    if (Math.Abs(actual - expected) > 0.001f)
        throw new InvalidOperationException($"{message}: expected {expected:F3}, got {actual:F3}");
}

// 0.4 direct layout: Touch index triggers become bumpers, grip analogs become triggers.
{
    var mapper = new ControllerMapper();
    LogicalGamepadState s = mapper.Map(
        buttons: 0,
        lx: 0, ly: 0, rx: 0, ry: 0,
        lt: 1.0f, rt: 0.70f,
        lg: 0.25f, rg: 0.75f);

    Check(s.LB, "left Touch index trigger must map to LB/L1");
    Check(s.RB, "right Touch index trigger must map to RB/R1");
    CheckNear(s.LT, 0.25f, "left Touch grip must map to analog LT/L2");
    CheckNear(s.RT, 0.75f, "right Touch grip must map to analog RT/R2");
}

// Menu+L3 is the generic View/Share modifier and suppresses the physical L3 click.
{
    var mapper = new ControllerMapper();
    LogicalGamepadState s = mapper.Map(
        BtnMenu | BtnL3,
        0, 0, 0, 0,
        0, 0, 0, 0,
        enableDs4Extras: true);

    Check(s.View, "Menu+L3 must map to View/Share");
    Check(!s.L3, "Menu+L3 must suppress L3 while the modifier is held");
    Check(!s.TouchpadClick, "Menu+L3 must not produce touchpad click");
}

// Menu+R3 is DS4 touchpad click, but Xbox keeps R3 because there is no touchpad target.
{
    var ds4Mapper = new ControllerMapper();
    LogicalGamepadState ds4 = ds4Mapper.Map(
        BtnMenu | BtnR3,
        0, 0, 0, 0,
        0, 0, 0, 0,
        enableDs4Extras: true);

    Check(ds4.TouchpadClick, "DS4 Menu+R3 must map to touchpad click");
    Check(!ds4.R3, "DS4 Menu+R3 must suppress R3 while touchpad click is held");
    Check(!ds4.View, "DS4 Menu+R3 must not map to Share/View");

    var xboxMapper = new ControllerMapper();
    LogicalGamepadState xbox = xboxMapper.Map(
        BtnMenu | BtnR3,
        0, 0, 0, 0,
        0, 0, 0, 0,
        enableDs4Extras: false);

    Check(!xbox.TouchpadClick, "Xbox mode must never emit DS4 touchpad click");
    Check(xbox.R3, "Xbox Menu+R3 should leave R3 available");
    Check(!xbox.Menu, "Xbox Menu+R3 must not generate an accidental Start/Menu tap");
}

// Regression for the 0.3.x Guide/PS failure: plain Menu may already have crossed its
// 0.50 s hold point before both logical triggers are squeezed. Guide must still take
// priority, cancel Options/Start and complete after its own 0.75 s hold.
{
    var mapper = new ControllerMapper();

    _ = mapper.Map(
        BtnMenu,
        0, 0, 0, 0,
        0, 0, 0, 0,
        enableDs4Extras: true);

    Thread.Sleep(600);

    LogicalGamepadState armed = mapper.Map(
        BtnMenu,
        0, 0, 0, 0,
        0, 0, 1.0f, 1.0f,
        enableDs4Extras: true);

    Check(!armed.Menu, "Guide intent must cancel an already-eligible plain Menu hold");
    Check(!armed.Guide, "Guide must not fire before the 0.75 s confirmation hold");
    CheckNear(armed.LT, 0.0f, "Guide chord must suppress LT while arming");
    CheckNear(armed.RT, 0.0f, "Guide chord must suppress RT while arming");

    Thread.Sleep(800);

    LogicalGamepadState guide = mapper.Map(
        BtnMenu,
        0, 0, 0, 0,
        0, 0, 1.0f, 1.0f,
        enableDs4Extras: true);

    Check(guide.Guide, "Menu+LT+RT must produce Guide/PS after 0.75 s");
    Check(!guide.Menu, "Guide/PS must not also emit Start/Options");
    CheckNear(guide.LT, 0.0f, "Guide chord must suppress LT while active");
    CheckNear(guide.RT, 0.0f, "Guide chord must suppress RT while active");
}

Console.WriteLine("Mapper smoke tests passed.");
