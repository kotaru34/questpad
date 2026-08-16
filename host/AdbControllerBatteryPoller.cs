using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace QuestPad.Host;

/// <summary>
/// Slow, best-effort ADB telemetry poller. Controller battery data comes from
/// Horizon's shell-side OVRRemoteService when OpenXR battery data is unavailable.
/// The same 10-second loop also samples Android's battery temperature so motion
/// A/B tests have a more sensitive heat trend than the coarse thermal-status enum.
/// None of this work runs on the real-time controller transport thread.
/// </summary>
internal sealed class AdbControllerBatteryPoller : IAsyncDisposable
{
    private static readonly Regex TypeRegex = new(
        @"\bType:\s*(Left|Right)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BatteryRegex = new(
        @"\bBattery:\s*(\d{1,3})\s*%",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DeviceTemperatureRegex = new(
        @"(?im)^\s*temperature:\s*(-?\d+)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly string _adb;
    private readonly string? _serial;
    private readonly HostStatus _status;
    private readonly CancellationTokenSource _cts;
    private readonly Task _task;

    public AdbControllerBatteryPoller(
        string adb,
        string? serial,
        HostStatus status,
        CancellationToken outer)
    {
        _adb = adb;
        _serial = serial;
        _status = status;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _task = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        bool controllerAvailabilityLogged = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                QueryResult result = await QueryControllerBatteriesAsync(ct);
                if (result.CommandSucceeded)
                {
                    _status.UpdateAdbBatteries(result.Left, result.Right);
                    if ((result.Left.HasValue || result.Right.HasValue) && !controllerAvailabilityLogged)
                    {
                        Console.WriteLine(
                            $"Controller battery fallback: ADB/OVRRemoteService " +
                            $"L={BatteryText(result.Left)} R={BatteryText(result.Right)}");
                        controllerAvailabilityLogged = true;
                    }
                }
                else if (!controllerAvailabilityLogged)
                {
                    Console.WriteLine(
                        "Controller battery fallback unavailable; OpenXR battery telemetry will still be used if the Quest runtime exposes it.");
                    controllerAvailabilityLogged = true;
                }

                double? temperatureC = await QueryDeviceBatteryTemperatureAsync(ct);
                _status.UpdateAdbBatteryTemperature(temperatureC);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Slow telemetry must never affect input transport or controller output.
                Console.WriteLine($"ADB telemetry poll failed: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<QueryResult> QueryControllerBatteriesAsync(CancellationToken ct)
    {
        CommandResult command = await RunShellAsync(ct, "dumpsys", "OVRRemoteService");
        if (!command.Success) return default;
        var (left, right) = Parse(command.Stdout);
        return new QueryResult(true, left, right);
    }

    private async Task<double?> QueryDeviceBatteryTemperatureAsync(CancellationToken ct)
    {
        CommandResult command = await RunShellAsync(ct, "dumpsys", "battery");
        if (!command.Success) return null;
        return ParseDeviceBatteryTemperature(command.Stdout);
    }

    private async Task<CommandResult> RunShellAsync(CancellationToken ct, params string[] shellArguments)
    {
        var psi = new ProcessStartInfo(_adb)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(_serial))
        {
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(_serial);
        }

        psi.ArgumentList.Add("shell");
        foreach (string argument in shellArguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi);
        if (process is null) return default;

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return default;
        }

        string stdout = await stdoutTask;
        _ = await stderrTask;
        return new CommandResult(process.ExitCode == 0, stdout);
    }

    internal static (int? Left, int? Right) Parse(string text)
    {
        int? left = null;
        int? right = null;

        foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            Match type = TypeRegex.Match(line);
            Match battery = BatteryRegex.Match(line);
            if (!type.Success || !battery.Success) continue;
            if (!int.TryParse(battery.Groups[1].Value, out int percentage)) continue;

            percentage = Math.Clamp(percentage, 0, 100);
            if (type.Groups[1].Value.Equals("Left", StringComparison.OrdinalIgnoreCase))
                left = percentage;
            else if (type.Groups[1].Value.Equals("Right", StringComparison.OrdinalIgnoreCase))
                right = percentage;
        }

        return (left, right);
    }

    internal static double? ParseDeviceBatteryTemperature(string text)
    {
        Match match = DeviceTemperatureRegex.Match(text);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tenthsC))
            return null;

        // Android dumpsys battery reports the standard battery temperature property in
        // tenths of a degree Celsius. Reject obviously nonsensical values rather than
        // presenting them as precise thermal telemetry.
        double c = tenthsC / 10.0;
        return c is >= -20.0 and <= 100.0 ? c : null;
    }

    private static string BatteryText(int? value) => value.HasValue ? $"{value.Value}%" : "n/a";

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _task; } catch (OperationCanceledException) { }
        _cts.Dispose();
    }

    private readonly record struct QueryResult(bool CommandSucceeded, int? Left, int? Right);
    private readonly record struct CommandResult(bool Success, string Stdout);
}
