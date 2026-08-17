using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QuestPad.Host;

internal readonly record struct HostSnapshot(
    bool Connected,
    bool GamepadAvailable,
    bool GamepadPaused,
    double Hz,
    long Drops,
    string Thermal,
    double? QuestBatteryTempC,
    int? LeftBattery,
    int? RightBattery,
    string BatterySource,
    string OutputBackend,
    string QuestViewStatus,
    bool GyroValid,
    string GyroStatus,
    string SteeringStatus);

internal sealed class HostStatus
{
    private readonly object _gate = new();
    private HostSnapshot _value = new(
        false, false, false, 0, 0, "N/A", null, null, null, "n/a",
        "starting", "black / zero-layer", false, "off", "off");

    private int? _openXrLeftBattery;
    private int? _openXrRightBattery;
    private int? _adbLeftBattery;
    private int? _adbRightBattery;

    public HostSnapshot Snapshot()
    {
        lock (_gate) return _value;
    }

    public void SetConnection(bool connected)
    {
        lock (_gate) _value = _value with { Connected = connected, Hz = connected ? _value.Hz : 0 };
    }

    public void SetGamepadAvailable(bool available)
    {
        lock (_gate) _value = _value with { GamepadAvailable = available };
    }

    public void SetPaused(bool paused)
    {
        lock (_gate) _value = _value with { GamepadPaused = paused };
    }

    public void SetOutputBackend(string name)
    {
        lock (_gate) _value = _value with { OutputBackend = name };
    }

    public void SetQuestViewStatus(string status)
    {
        lock (_gate) _value = _value with { QuestViewStatus = status };
    }

    public void UpdateMotionStatus(bool gyroValid, string gyroStatus, string steeringStatus)
    {
        lock (_gate) _value = _value with
        {
            GyroValid = gyroValid,
            GyroStatus = gyroStatus,
            SteeringStatus = steeringStatus
        };
    }

    public void UpdateTelemetry(double hz, long drops, string thermal, int? leftBattery, int? rightBattery)
    {
        lock (_gate)
        {
            _openXrLeftBattery = leftBattery;
            _openXrRightBattery = rightBattery;
            ResolveBatteries(out int? resolvedLeft, out int? resolvedRight, out string source);
            _value = _value with
            {
                Hz = hz,
                Drops = drops,
                Thermal = thermal,
                LeftBattery = resolvedLeft,
                RightBattery = resolvedRight,
                BatterySource = source
            };
        }
    }

    public void UpdateAdbBatteries(int? leftBattery, int? rightBattery)
    {
        lock (_gate)
        {
            _adbLeftBattery = leftBattery;
            _adbRightBattery = rightBattery;
            ResolveBatteries(out int? resolvedLeft, out int? resolvedRight, out string source);
            _value = _value with
            {
                LeftBattery = resolvedLeft,
                RightBattery = resolvedRight,
                BatterySource = source
            };
        }
    }

    public void UpdateAdbBatteryTemperature(double? temperatureC)
    {
        lock (_gate) _value = _value with { QuestBatteryTempC = temperatureC };
    }

    private void ResolveBatteries(out int? left, out int? right, out string source)
    {
        left = _openXrLeftBattery ?? _adbLeftBattery;
        right = _openXrRightBattery ?? _adbRightBattery;
        bool anyOpenXr = _openXrLeftBattery.HasValue || _openXrRightBattery.HasValue;
        bool anyAdbFallback = (!_openXrLeftBattery.HasValue && _adbLeftBattery.HasValue) ||
                              (!_openXrRightBattery.HasValue && _adbRightBattery.HasValue);
        source = anyOpenXr && anyAdbFallback ? "OpenXR + ADB" :
                 anyOpenXr ? "OpenXR" :
                 anyAdbFallback ? "ADB" : "n/a";
    }
}

internal sealed class TrayStatus : IDisposable
{
    private readonly HostStatus _status;
    private readonly RuntimeSettings _settings;
    private readonly Action _calibrateSteering;
    private readonly Action _disarmSteering;
    private readonly Action<bool> _setPaused;
    private readonly Action _exit;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _disposed;
    private ApplicationContext? _context;
    private NotifyIcon? _icon;
    private ContextMenuStrip? _menu;
    private Icon? _trayIcon;

    public TrayStatus(
        HostStatus status,
        RuntimeSettings settings,
        Action calibrateSteering,
        Action disarmSteering,
        Action<bool> setPaused,
        Action exit)
    {
        _status = status;
        _settings = settings;
        _calibrateSteering = calibrateSteering;
        _disarmSteering = disarmSteering;
        _setPaused = setPaused;
        _exit = exit;
        _thread = new Thread(Run) { IsBackground = true, Name = "QuestPad tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        _context = new ApplicationContext();

        var title = new ToolStripMenuItem("QuestPad") { Enabled = false };
        title.Font = new Font(title.Font, FontStyle.Bold);
        var connection = Disabled("Quest: waiting");
        var gamepad = Disabled("Gamepad: starting");
        var outputStatus = Disabled("Output: starting");
        var questViewStatus = Disabled("Quest view: black / zero-layer");
        var gyroStatus = Disabled("Gyro: off");
        var steeringStatus = Disabled("Steering: off");
        var leftBattery = Disabled("Left controller: n/a");
        var rightBattery = Disabled("Right controller: n/a");
        var batterySource = Disabled("Battery source: n/a");
        var thermal = Disabled("Thermal: n/a");
        var batteryTemp = Disabled("Quest battery temp: n/a");
        var cadence = Disabled("Input: 0.0 Hz");

        var questViewMenu = new ToolStripMenuItem("Quest view");
        var viewBlack = CheckItem("Black / zero-layer (PC-only)", () => _settings.SetQuestView(QuestViewMode.Black));
        var viewPassthrough = CheckItem("Passthrough (MR)", () => _settings.SetQuestView(QuestViewMode.Passthrough));
        questViewMenu.DropDownItems.AddRange(new ToolStripItem[] { viewBlack, viewPassthrough });

        var outputMenu = new ToolStripMenuItem("Output backend");
        var outputXbox = CheckItem("Xbox 360 / XInput", () => _settings.SetOutput(OutputMode.Xbox360));
        var outputDs4 = CheckItem("DualShock 4 / native motion", () => _settings.SetOutput(OutputMode.DualShock4));
        outputMenu.DropDownItems.AddRange(new ToolStripItem[] { outputXbox, outputDs4 });

        var gyroMenu = new ToolStripMenuItem("Gyro source (right Touch)");
        var gyroOff = CheckItem("Off", () => _settings.SetGyroSource(GyroSourceMode.Off));
        var gyroRate = CheckItem("Angular-rate only (recommended)", () => _settings.SetGyroSource(GyroSourceMode.AngularRate));
        var gyroCamera = CheckItem("Camera-assisted tracked pose (diagnostic A/B)", () => _settings.SetGyroSource(GyroSourceMode.CameraAssisted));
        gyroMenu.DropDownItems.AddRange(new ToolStripItem[] { gyroOff, gyroRate, gyroCamera });

        var gyroSmooth = new ToolStripMenuItem("Gyro smoothing");
        var gsOff = CheckItem("Off", () => _settings.SetGyroSmoothing(SmoothingLevel.Off));
        var gsLight = CheckItem("Light", () => _settings.SetGyroSmoothing(SmoothingLevel.Light));
        var gsMedium = CheckItem("Medium", () => _settings.SetGyroSmoothing(SmoothingLevel.Medium));
        var gsStrong = CheckItem("Strong", () => _settings.SetGyroSmoothing(SmoothingLevel.Strong));
        gyroSmooth.DropDownItems.AddRange(new ToolStripItem[] { gsOff, gsLight, gsMedium, gsStrong });

        var gyroStickLock = CheckItem("Lock gyro while using right stick", () =>
        {
            RuntimeSettingsSnapshot cfg = _settings.Snapshot();
            _settings.SetGyroStickLock(!cfg.GyroStickLock);
        });

        // Steering remains available only as a deliberately limited experiment. The
        // free-air/hybrid prototypes are no longer user-facing because QuestPad is not
        // trying to replace a multi-turn native HID wheel.
        var steeringMenu = new ToolStripMenuItem("Mounted steering experiment");
        var stOff = CheckItem("Off", () => _settings.SetSteering(SteeringMode.Off));
        var stMounted = CheckItem("Mounted / rigid wheel (experimental)", () => _settings.SetSteering(SteeringMode.Mounted));
        steeringMenu.DropDownItems.AddRange(new ToolStripItem[] { stOff, stMounted });

        var steeringRange = new ToolStripMenuItem("Steering range");
        var range180 = CheckItem("180° total", () => _settings.SetSteeringRange(180));
        var range240 = CheckItem("240° total", () => _settings.SetSteeringRange(240));
        var range360 = CheckItem("360° total", () => _settings.SetSteeringRange(360));
        steeringRange.DropDownItems.AddRange(new ToolStripItem[] { range180, range240, range360 });

        var steeringSmooth = new ToolStripMenuItem("Steering smoothing");
        var ssOff = CheckItem("Off", () => _settings.SetSteeringSmoothing(SmoothingLevel.Off));
        var ssLight = CheckItem("Light", () => _settings.SetSteeringSmoothing(SmoothingLevel.Light));
        var ssMedium = CheckItem("Medium", () => _settings.SetSteeringSmoothing(SmoothingLevel.Medium));
        var ssStrong = CheckItem("Strong", () => _settings.SetSteeringSmoothing(SmoothingLevel.Strong));
        steeringSmooth.DropDownItems.AddRange(new ToolStripItem[] { ssOff, ssLight, ssMedium, ssStrong });

        var gripClutch = CheckItem("Steering light-grip clutch (recommended)", () =>
        {
            RuntimeSettingsSnapshot cfg = _settings.Snapshot();
            _settings.SetSteeringGripClutch(!cfg.SteeringGripClutch);
        });
        var invertSteering = CheckItem("Invert steering direction", () =>
        {
            RuntimeSettingsSnapshot cfg = _settings.Snapshot();
            _settings.SetSteeringInverted(!cfg.SteeringInverted);
        });

        var calibrate = new ToolStripMenuItem("Center + arm steering");
        calibrate.Click += (_, _) => _calibrateSteering();
        var disarm = new ToolStripMenuItem("Disarm steering now");
        disarm.Click += (_, _) => _disarmSteering();

        var pause = new ToolStripMenuItem("Pause gamepad output") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) => _setPaused(pause.Checked);
        var exit = new ToolStripMenuItem("Exit QuestPad Host");
        exit.Click += (_, _) => _exit();

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            title, new ToolStripSeparator(), connection, gamepad, outputStatus, questViewStatus,
            gyroStatus, steeringStatus, leftBattery, rightBattery, batterySource, thermal, batteryTemp, cadence,
            new ToolStripSeparator(), questViewMenu, outputMenu, gyroMenu, gyroSmooth, gyroStickLock,
            steeringMenu, steeringRange, steeringSmooth, gripClutch, invertSteering, calibrate, disarm,
            new ToolStripSeparator(), pause, new ToolStripSeparator(), exit
        });

        _trayIcon = CreateTrayIcon();
        _icon = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "QuestPad — waiting for Quest",
            ContextMenuStrip = _menu,
            Visible = true
        };

        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, _) =>
        {
            HostSnapshot s = _status.Snapshot();
            RuntimeSettingsSnapshot cfg = _settings.Snapshot();

            connection.Text = s.Connected ? "Quest: connected" : "Quest: waiting";
            gamepad.Text = !s.GamepadAvailable ? "Gamepad: unavailable" :
                s.GamepadPaused ? "Gamepad: paused" : "Gamepad: active";
            outputStatus.Text = $"Output: {s.OutputBackend}";
            questViewStatus.Text = $"Quest view: {s.QuestViewStatus}";
            gyroStatus.Text = $"Gyro: {s.GyroStatus}";
            steeringStatus.Text = $"Steering: {s.SteeringStatus}";
            leftBattery.Text = $"Left controller: {BatteryText(s.LeftBattery)}";
            rightBattery.Text = $"Right controller: {BatteryText(s.RightBattery)}";
            batterySource.Text = $"Battery source: {s.BatterySource}";
            thermal.Text = $"Thermal: {s.Thermal}";
            batteryTemp.Text = $"Quest battery temp: {TemperatureText(s.QuestBatteryTempC)}";
            cadence.Text = $"Input: {s.Hz:F1} Hz   drops: {s.Drops}";

            viewBlack.Checked = cfg.QuestView == QuestViewMode.Black;
            viewPassthrough.Checked = cfg.QuestView == QuestViewMode.Passthrough;
            outputXbox.Checked = cfg.Output == OutputMode.Xbox360;
            outputDs4.Checked = cfg.Output == OutputMode.DualShock4;
            gyroOff.Checked = cfg.GyroSource == GyroSourceMode.Off;
            gyroRate.Checked = cfg.GyroSource == GyroSourceMode.AngularRate;
            gyroCamera.Checked = cfg.GyroSource == GyroSourceMode.CameraAssisted;
            gsOff.Checked = cfg.GyroSmoothing == SmoothingLevel.Off;
            gsLight.Checked = cfg.GyroSmoothing == SmoothingLevel.Light;
            gsMedium.Checked = cfg.GyroSmoothing == SmoothingLevel.Medium;
            gsStrong.Checked = cfg.GyroSmoothing == SmoothingLevel.Strong;
            gyroStickLock.Checked = cfg.GyroStickLock;
            stOff.Checked = cfg.Steering == SteeringMode.Off;
            stMounted.Checked = cfg.Steering == SteeringMode.Mounted;
            range180.Checked = Math.Abs(cfg.SteeringRangeDegrees - 180) < 1;
            range240.Checked = Math.Abs(cfg.SteeringRangeDegrees - 240) < 1;
            range360.Checked = Math.Abs(cfg.SteeringRangeDegrees - 360) < 1;
            ssOff.Checked = cfg.SteeringSmoothing == SmoothingLevel.Off;
            ssLight.Checked = cfg.SteeringSmoothing == SmoothingLevel.Light;
            ssMedium.Checked = cfg.SteeringSmoothing == SmoothingLevel.Medium;
            ssStrong.Checked = cfg.SteeringSmoothing == SmoothingLevel.Strong;
            gripClutch.Checked = cfg.SteeringGripClutch;
            invertSteering.Checked = cfg.SteeringInverted;

            if (pause.Checked != s.GamepadPaused) pause.Checked = s.GamepadPaused;
            pause.Enabled = s.GamepadAvailable;

            bool steeringEnabled = cfg.Steering == SteeringMode.Mounted;
            calibrate.Enabled = s.Connected && steeringEnabled;
            disarm.Enabled = steeringEnabled;
            steeringRange.Enabled = steeringEnabled;
            steeringSmooth.Enabled = steeringEnabled;
            gripClutch.Enabled = steeringEnabled;
            invertSteering.Enabled = steeringEnabled;

            string state = !s.Connected ? "waiting" : s.GamepadPaused ? "paused" : "connected";
            string text = $"QuestPad — {state} — {s.OutputBackend} — {s.QuestViewStatus} — L {BatteryText(s.LeftBattery)} R {BatteryText(s.RightBattery)}";
            _icon.Text = text.Length <= 127 ? text : text[..127];
        };
        timer.Start();
        _ready.Set();
        Application.Run(_context);
        timer.Stop();

        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
        _menu.Dispose();
        _menu = null;
        _trayIcon.Dispose();
        _trayIcon = null;
        timer.Dispose();
    }

    private static ToolStripMenuItem Disabled(string text) => new(text) { Enabled = false };

    private static ToolStripMenuItem CheckItem(string text, Action action)
    {
        var item = new ToolStripMenuItem(text) { CheckOnClick = false };
        item.Click += (_, _) => action();
        return item;
    }

    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var background = new SolidBrush(Color.FromArgb(22, 112, 220));
            g.FillEllipse(background, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 19, FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.White);
            var size = g.MeasureString("Q", font);
            g.DrawString("Q", font, text, (32 - size.Width) / 2f, (32 - size.Height) / 2f - 1f);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static string BatteryText(int? value) => value.HasValue ? $"{value.Value}%" : "n/a";
    private static string TemperatureText(double? value) => value.HasValue ? $"{value.Value:F1} °C (ADB battery)" : "n/a";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_menu is not null && _context is not null)
                _menu.BeginInvoke(new Action(() => _context.ExitThread()));
            else
                _context?.ExitThread();
        }
        catch { }
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
