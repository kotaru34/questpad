using System.Drawing;
using System.Windows.Forms;

namespace QuestPad.Host;

internal readonly record struct HostSnapshot(
    bool Connected,
    bool GamepadAvailable,
    bool GamepadPaused,
    double Hz,
    long Drops,
    string Thermal,
    int? LeftBattery,
    int? RightBattery);

internal sealed class HostStatus
{
    private readonly object _gate = new();
    private HostSnapshot _value = new(false, false, false, 0, 0, "N/A", null, null);

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

    public void UpdateTelemetry(double hz, long drops, string thermal, int? leftBattery, int? rightBattery)
    {
        lock (_gate)
            _value = _value with
            {
                Hz = hz,
                Drops = drops,
                Thermal = thermal,
                LeftBattery = leftBattery,
                RightBattery = rightBattery
            };
    }
}

internal sealed class TrayStatus : IDisposable
{
    private readonly HostStatus _status;
    private readonly Action<bool> _setPaused;
    private readonly Action _exit;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _disposed;
    private ApplicationContext? _context;
    private NotifyIcon? _icon;

    public TrayStatus(HostStatus status, Action<bool> setPaused, Action exit)
    {
        _status = status;
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

        var connection = new ToolStripMenuItem("Quest: waiting") { Enabled = false };
        var leftBattery = new ToolStripMenuItem("Left controller: n/a") { Enabled = false };
        var rightBattery = new ToolStripMenuItem("Right controller: n/a") { Enabled = false };
        var thermal = new ToolStripMenuItem("Thermal: n/a") { Enabled = false };
        var cadence = new ToolStripMenuItem("Input: 0.0 Hz") { Enabled = false };
        var pause = new ToolStripMenuItem("Pause gamepad output") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) => _setPaused(pause.Checked);
        var exit = new ToolStripMenuItem("Exit QuestPad Host");
        exit.Click += (_, _) => _exit();

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            connection, leftBattery, rightBattery, thermal, cadence,
            new ToolStripSeparator(), pause, new ToolStripSeparator(), exit
        });

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "QuestPad — waiting for Quest",
            ContextMenuStrip = menu,
            Visible = true
        };

        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (_, _) =>
        {
            var s = _status.Snapshot();
            connection.Text = s.Connected ? "Quest: connected" : "Quest: waiting";
            leftBattery.Text = $"Left controller: {BatteryText(s.LeftBattery)}";
            rightBattery.Text = $"Right controller: {BatteryText(s.RightBattery)}";
            thermal.Text = $"Thermal: {s.Thermal}";
            cadence.Text = $"Input: {s.Hz:F1} Hz   drops: {s.Drops}";
            if (pause.Checked != s.GamepadPaused) pause.Checked = s.GamepadPaused;
            pause.Enabled = s.GamepadAvailable;

            string state = s.Connected ? "connected" : "waiting";
            string bat = $"L {BatteryText(s.LeftBattery)} R {BatteryText(s.RightBattery)}";
            string text = $"QuestPad — {state} — {bat}";
            _icon.Text = text.Length <= 127 ? text : text[..127];
        };
        timer.Start();
        _ready.Set();
        Application.Run(_context);
        timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        menu.Dispose();
        timer.Dispose();
    }

    private static string BatteryText(int? value) => value.HasValue ? $"{value.Value}%" : "n/a";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_context is not null)
        {
            try { _context.ExitThread(); } catch { }
        }
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
