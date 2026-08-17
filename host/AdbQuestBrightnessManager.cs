using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace QuestPad.Host;

/// <summary>
/// Quest's OpenXR compositor does not reliably honor a NativeActivity window
/// brightness override. This manager therefore applies Black-mode brightness at
/// Android's display/system layer through the already-selected ADB Quest target.
///
/// The user's original brightness and brightness mode are captured before the
/// first override and written next to the portable host executable. The recovery
/// file is deleted after a successful restore, but survives a host crash so the
/// next run can still restore the original values.
/// </summary>
internal sealed class AdbQuestBrightnessManager : IAsyncDisposable
{
    private const string BackupFileName = "QuestPad.brightness-backup.json";
    private const int MinimumBrightness = 1;
    private const float MinimumNormalizedBrightness = 1.0f / 255.0f;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(75);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string adb;
    private readonly string serial;
    private readonly RuntimeSettings settings;
    private readonly string backupPath;
    private readonly CancellationTokenSource cts;
    private readonly Task task;
    private BrightnessBackup? backup;
    private QuestViewMode? appliedView;
    private bool disabled;

    public AdbQuestBrightnessManager(
        string adb,
        string serial,
        RuntimeSettings settings,
        CancellationToken outer)
    {
        this.adb = adb;
        this.serial = serial;
        this.settings = settings;

        string baseDirectory = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? AppContext.BaseDirectory;
        backupPath = Path.Combine(baseDirectory, BackupFileName);

        LoadRecoveryBackup();
        cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        task = Task.Run(() => LoopAsync(cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            QuestViewMode desired = settings.Snapshot().QuestView;
            if (!disabled && appliedView != desired)
            {
                bool ok = desired == QuestViewMode.Black
                    ? EnterBlackMode()
                    : RestorePreferredBrightness();

                // One ADB attempt per view transition. If a vendor command fails,
                // do not hammer the USB/ADB path at polling frequency; the next
                // transition (and shutdown recovery) gets another attempt.
                appliedView = desired;
                if (!ok)
                    Console.Error.WriteLine("Quest brightness transition incomplete; will retry on the next view change or shutdown.");
            }

            try
            {
                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private bool EnterBlackMode()
    {
        if (backup is null && !CaptureBackup())
        {
            disabled = true;
            Console.Error.WriteLine(
                "Quest brightness control disabled: could not capture the current system brightness safely.");
            return false;
        }

        // If Android exposes a brightness mode, force manual while Black is active.
        // We restore the exact previous mode when leaving Black.
        if (backup!.BrightnessMode.HasValue)
            Run("shell", "settings", "put", "system", "screen_brightness_mode", "0");

        CommandResult setting = Run(
            "shell", "settings", "put", "system", "screen_brightness",
            MinimumBrightness.ToString(CultureInfo.InvariantCulture));

        // DisplayManager's shell command applies the value directly to the default
        // display. Keep Settings.System as the portable fallback for Quest builds
        // where this command is missing or vendor-modified.
        CommandResult display = Run(
            "shell", "cmd", "display", "set-brightness",
            MinimumNormalizedBrightness.ToString("0.########", CultureInfo.InvariantCulture));

        if (!setting.Success && !display.Success)
        {
            Console.Error.WriteLine(
                "Quest brightness control failed: neither Settings.System nor cmd display accepted the minimum brightness override.");
            return false;
        }

        Console.WriteLine(
            $"Quest brightness: Black override -> {MinimumBrightness}/255 " +
            $"(settings={(setting.Success ? "ok" : "failed")}, display={(display.Success ? "ok" : "failed")})");
        return true;
    }

    private bool CaptureBackup()
    {
        CommandResult brightnessResult = Run(
            "shell", "settings", "get", "system", "screen_brightness");
        if (!brightnessResult.Success ||
            !int.TryParse(brightnessResult.Stdout.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int brightness) ||
            brightness < 0 || brightness > 255)
        {
            Console.Error.WriteLine(
                $"Could not read Quest screen_brightness: {BestError(brightnessResult)}");
            return false;
        }

        int? mode = null;
        CommandResult modeResult = Run(
            "shell", "settings", "get", "system", "screen_brightness_mode");
        if (modeResult.Success &&
            int.TryParse(modeResult.Stdout.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedMode))
        {
            mode = parsedMode;
        }

        backup = new BrightnessBackup(serial, brightness, mode);
        if (!SaveBackup())
        {
            backup = null;
            return false;
        }

        Console.WriteLine(
            $"Quest brightness saved: {brightness}/255, mode={(mode.HasValue ? mode.Value.ToString(CultureInfo.InvariantCulture) : "unknown")}");
        return true;
    }

    private bool RestorePreferredBrightness()
    {
        if (backup is null)
        {
            appliedView = QuestViewMode.Passthrough;
            return true;
        }

        // Put the display in manual mode briefly so the saved brightness can be
        // applied deterministically, then restore the user's original mode last.
        if (backup.BrightnessMode.HasValue)
            Run("shell", "settings", "put", "system", "screen_brightness_mode", "0");

        CommandResult setting = Run(
            "shell", "settings", "put", "system", "screen_brightness",
            backup.Brightness.ToString(CultureInfo.InvariantCulture));

        float normalized = Math.Clamp(backup.Brightness / 255.0f, 0.0f, 1.0f);
        CommandResult display = Run(
            "shell", "cmd", "display", "set-brightness",
            normalized.ToString("0.########", CultureInfo.InvariantCulture));

        bool modeOk = true;
        if (backup.BrightnessMode.HasValue)
        {
            modeOk = Run(
                "shell", "settings", "put", "system", "screen_brightness_mode",
                backup.BrightnessMode.Value.ToString(CultureInfo.InvariantCulture)).Success;
        }

        if (!setting.Success || !modeOk)
        {
            Console.Error.WriteLine(
                "Quest brightness restore was incomplete; keeping the portable recovery file for the next launch.");
            return false;
        }

        Console.WriteLine(
            $"Quest brightness restored -> {backup.Brightness}/255, " +
            $"mode={(backup.BrightnessMode.HasValue ? backup.BrightnessMode.Value.ToString(CultureInfo.InvariantCulture) : "unchanged")}, " +
            $"display={(display.Success ? "ok" : "fallback-only")}");

        backup = null;
        try
        {
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not delete {BackupFileName}: {ex.Message}");
        }

        return true;
    }

    private void LoadRecoveryBackup()
    {
        if (!File.Exists(backupPath)) return;
        try
        {
            BrightnessBackup? recovered = JsonSerializer.Deserialize<BrightnessBackup>(
                File.ReadAllText(backupPath), JsonOptions);
            if (recovered is null) return;

            if (!recovered.Serial.Equals(serial, StringComparison.Ordinal))
            {
                disabled = true;
                Console.Error.WriteLine(
                    $"Quest brightness recovery belongs to ADB device '{recovered.Serial}', not '{serial}'. " +
                    "Brightness control is disabled so the previous headset's recovery data is not overwritten.");
                return;
            }

            backup = recovered;
            Console.WriteLine(
                $"Recovered pending Quest brightness backup: {backup.Brightness}/255, " +
                $"mode={(backup.BrightnessMode.HasValue ? backup.BrightnessMode.Value.ToString(CultureInfo.InvariantCulture) : "unknown")}");
        }
        catch (Exception ex)
        {
            disabled = true;
            Console.Error.WriteLine(
                $"Could not read {BackupFileName}; brightness control disabled for safety: {ex.Message}");
        }
    }

    private bool SaveBackup()
    {
        if (backup is null) return false;
        string tempPath = backupPath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(backup, JsonOptions));
            File.Move(tempPath, backupPath, true);
            return true;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            Console.Error.WriteLine($"Could not save {BackupFileName}: {ex.Message}");
            return false;
        }
    }

    private CommandResult Run(params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(adb)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(serial);
            foreach (string argument in arguments) psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi);
            if (process is null)
                return new CommandResult(false, -1, string.Empty, "failed to start adb.exe");

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(4000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new CommandResult(false, -1, string.Empty, "ADB brightness command timed out");
            }

            Task.WaitAll(stdoutTask, stderrTask);
            return new CommandResult(
                process.ExitCode == 0,
                process.ExitCode,
                stdoutTask.Result,
                stderrTask.Result);
        }
        catch (Exception ex)
        {
            return new CommandResult(false, -1, string.Empty, ex.Message);
        }
    }

    private static string BestError(CommandResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Stderr)) return result.Stderr.Trim();
        if (!string.IsNullOrWhiteSpace(result.Stdout)) return result.Stdout.Trim();
        return $"ADB exited with code {result.ExitCode}";
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        try { await task; } catch (OperationCanceledException) { }

        // Normal host shutdown must restore the user's display policy even when
        // QuestPad itself was still in Black mode.
        if (backup is not null)
            RestorePreferredBrightness();

        cts.Dispose();
    }

    private sealed record BrightnessBackup(string Serial, int Brightness, int? BrightnessMode);
    private readonly record struct CommandResult(bool Success, int ExitCode, string Stdout, string Stderr);
}
