using System.Diagnostics;
using System.Text.RegularExpressions;

namespace QuestPad.Host;

/// <summary>
/// Best-effort fallback for Quest runtimes that do not expose controller battery
/// through XR_EXT_interaction_profile_battery_state_display yet.
///
/// Horizon OS exposes the paired Touch controller state through the shell-only
/// OVRRemoteService dumpsys service. Since QuestPad already requires an ADB link
/// for transport, the Windows host can query that service without adding any
/// privilege or background work to the Quest APK itself.
/// </summary>
internal sealed class AdbControllerBatteryPoller : IAsyncDisposable
{
    private static readonly Regex TypeRegex = new(
        @"\bType:\s*(Left|Right)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BatteryRegex = new(
        @"\bBattery:\s*(\d{1,3})\s*%",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
        bool loggedUnavailable = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await QueryAsync(ct);
                if (result.CommandSucceeded)
                {
                    _status.UpdateAdbBatteries(result.Left, result.Right);
                    if ((result.Left.HasValue || result.Right.HasValue) && !loggedUnavailable)
                    {
                        Console.WriteLine(
                            $"Controller battery fallback: ADB/OVRRemoteService " +
                            $"L={BatteryText(result.Left)} R={BatteryText(result.Right)}");
                    }
                    loggedUnavailable = result.Left.HasValue || result.Right.HasValue;
                }
                else if (!loggedUnavailable)
                {
                    Console.WriteLine(
                        "Controller battery fallback unavailable; OpenXR battery telemetry will still be used if the Quest runtime exposes it.");
                    loggedUnavailable = true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Battery telemetry must never affect input transport or XInput output.
                Console.WriteLine($"Controller battery poll failed: {ex.Message}");
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

    private async Task<QueryResult> QueryAsync(CancellationToken ct)
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
        psi.ArgumentList.Add("dumpsys");
        psi.ArgumentList.Add("OVRRemoteService");

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
        if (process.ExitCode != 0) return default;

        var (left, right) = Parse(stdout);
        return new QueryResult(true, left, right);
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

    private static string BatteryText(int? value) => value.HasValue ? $"{value.Value}%" : "n/a";

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _task; } catch (OperationCanceledException) { }
        _cts.Dispose();
    }

    private readonly record struct QueryResult(bool CommandSucceeded, int? Left, int? Right);
}
