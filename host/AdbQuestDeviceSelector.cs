using System.Diagnostics;
using System.Text;

namespace QuestPad.Host;

internal sealed record AdbQuestDevice(
    string Serial,
    string Model,
    string Manufacturer,
    bool QuestPadInstalled);

internal static class AdbQuestDeviceSelector
{
    public const string QuestPadPackage = "dev.questpad.bridge";
    public const string QuestPadActivity = "android.app.NativeActivity";

    private const string HeadtrackingFeature = "feature:android.hardware.vr.headtracking";
    private const string OculusPassthroughFeature = "feature:com.oculus.feature.PASSTHROUGH";

    public static bool TrySelectQuest(
        string adb,
        string? requestedSerial,
        out AdbQuestDevice? selected,
        out string error)
    {
        selected = null;
        error = string.Empty;

        CommandResult devicesResult = Run(adb, null, "devices", "-l");
        if (!devicesResult.Success)
        {
            error = $"Could not enumerate ADB devices: {BestError(devicesResult)}";
            return false;
        }

        List<ListedDevice> devices = ParseDevices(devicesResult.Stdout);
        if (!string.IsNullOrWhiteSpace(requestedSerial))
        {
            ListedDevice? requested = devices.FirstOrDefault(d =>
                d.Serial.Equals(requestedSerial, StringComparison.Ordinal));
            if (requested is null)
            {
                error = $"ADB device '{requestedSerial}' is not connected.\n{DescribeDevices(devices)}";
                return false;
            }
            if (!requested.State.Equals("device", StringComparison.OrdinalIgnoreCase))
            {
                error = $"ADB device '{requestedSerial}' is in state '{requested.State}', not ready.\n{DescribeDevices(devices)}";
                return false;
            }

            DeviceProbe probe = Probe(adb, requested.Serial, requested.Description);
            if (!probe.IsQuest)
            {
                error =
                    $"ADB device '{requested.Serial}' does not look like a supported Meta Quest. " +
                    $"Model='{probe.Model}', manufacturer='{probe.Manufacturer}'. " +
                    "QuestPad requires Android VR headtracking plus the Quest/Meta runtime signature.";
                return false;
            }

            selected = probe.ToSelected();
            return true;
        }

        List<ListedDevice> ready = devices
            .Where(d => d.State.Equals("device", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (ready.Count == 0)
        {
            error = "No ready ADB device was found. Connect the Quest over USB and authorize USB debugging.\n" + DescribeDevices(devices);
            return false;
        }

        List<DeviceProbe> quests = ready
            .Select(d => Probe(adb, d.Serial, d.Description))
            .Where(p => p.IsQuest)
            .ToList();

        if (quests.Count == 1)
        {
            selected = quests[0].ToSelected();
            return true;
        }

        if (quests.Count > 1)
        {
            // If several headsets are attached, an installed QuestPad APK is a useful
            // additional discriminator. Never guess if that still leaves ambiguity.
            List<DeviceProbe> installed = quests.Where(q => q.QuestPadInstalled).ToList();
            if (installed.Count == 1)
            {
                selected = installed[0].ToSelected();
                return true;
            }

            string choices = string.Join(
                Environment.NewLine,
                quests.Select(q => $"  {q.Serial}  {DisplayName(q)}  QuestPad={(q.QuestPadInstalled ? "installed" : "not installed")}"));
            error =
                "Multiple Meta Quest devices are connected, so QuestPad will not guess which one to use. " +
                "Start the host with --serial SERIAL.\n" + choices;
            return false;
        }

        error =
            "ADB devices are connected, but none passed the Meta Quest capability check. " +
            "A phone will not be used as the QuestPad transport target.\n" + DescribeDevices(devices);
        return false;
    }

    public static bool TryStartQuestPad(string adb, AdbQuestDevice device, out string error)
    {
        error = string.Empty;
        if (!device.QuestPadInstalled)
        {
            error = $"QuestPad APK ({QuestPadPackage}) is not installed on {device.Serial}. Install the matching Quest APK first.";
            return false;
        }

        string component = $"{QuestPadPackage}/{QuestPadActivity}";
        CommandResult result = Run(
            adb,
            device.Serial,
            "shell", "am", "start",
            "-a", "android.intent.action.MAIN",
            "-c", "android.intent.category.LAUNCHER",
            "-n", component);

        if (!result.Success || ContainsLaunchError(result.Stdout) || ContainsLaunchError(result.Stderr))
        {
            error = $"Could not auto-start QuestPad on {device.Serial}: {BestError(result)}";
            return false;
        }

        return true;
    }

    private static DeviceProbe Probe(string adb, string serial, string listedDescription)
    {
        string model = GetProp(adb, serial, "ro.product.model");
        string manufacturer = GetProp(adb, serial, "ro.product.manufacturer");
        string brand = GetProp(adb, serial, "ro.product.brand");
        CommandResult features = Run(adb, serial, "shell", "pm", "list", "features");
        string featureText = features.Success ? features.Stdout : string.Empty;

        bool hasHeadtracking = featureText.Contains(HeadtrackingFeature, StringComparison.OrdinalIgnoreCase);
        bool hasOculusPassthrough = featureText.Contains(OculusPassthroughFeature, StringComparison.OrdinalIgnoreCase);
        bool metaVendor = LooksMeta(manufacturer) || LooksMeta(brand) || LooksQuestDescription(listedDescription) || LooksQuestDescription(model);
        bool isQuest = hasHeadtracking && (hasOculusPassthrough || metaVendor);

        CommandResult package = Run(adb, serial, "shell", "pm", "path", QuestPadPackage);
        bool installed = package.Success && package.Stdout.Contains("package:", StringComparison.OrdinalIgnoreCase);

        return new DeviceProbe(serial, model, manufacturer, isQuest, installed);
    }

    private static string GetProp(string adb, string serial, string property)
    {
        CommandResult result = Run(adb, serial, "shell", "getprop", property);
        return result.Success ? result.Stdout.Trim() : string.Empty;
    }

    private static bool LooksMeta(string value) =>
        value.Contains("oculus", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("meta", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("facebook", StringComparison.OrdinalIgnoreCase);

    private static bool LooksQuestDescription(string value) =>
        value.Contains("quest", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("oculus", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsLaunchError(string value) =>
        value.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

    private static List<ListedDevice> ParseDevices(string text)
    {
        var result = new List<ListedDevice>();
        foreach (string raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) || line.StartsWith('*'))
                continue;

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            result.Add(new ListedDevice(parts[0], parts[1], line));
        }
        return result;
    }

    private static string DescribeDevices(IEnumerable<ListedDevice> devices)
    {
        ListedDevice[] list = devices.ToArray();
        if (list.Length == 0) return "ADB currently reports no attached devices.";
        var sb = new StringBuilder("ADB devices:");
        foreach (ListedDevice d in list)
            sb.AppendLine().Append("  ").Append(d.Serial).Append("  ").Append(d.State).Append("  ").Append(d.Description);
        return sb.ToString();
    }

    private static string DisplayName(DeviceProbe probe)
    {
        string name = string.Join(" ", new[] { probe.Manufacturer, probe.Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return string.IsNullOrWhiteSpace(name) ? "Meta Quest" : name;
    }

    private static string BestError(CommandResult result)
    {
        string stderr = result.Stderr.Trim();
        if (!string.IsNullOrWhiteSpace(stderr)) return stderr;
        string stdout = result.Stdout.Trim();
        return string.IsNullOrWhiteSpace(stdout) ? $"ADB exited with code {result.ExitCode}" : stdout;
    }

    private static CommandResult Run(string adb, string? serial, params string[] arguments)
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
            if (!string.IsNullOrWhiteSpace(serial))
            {
                psi.ArgumentList.Add("-s");
                psi.ArgumentList.Add(serial);
            }
            foreach (string arg in arguments) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null) return new CommandResult(false, -1, string.Empty, "failed to start adb.exe");

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new CommandResult(false, -1, string.Empty, "ADB command timed out");
            }

            Task.WaitAll(stdoutTask, stderrTask);
            return new CommandResult(process.ExitCode == 0, process.ExitCode, stdoutTask.Result, stderrTask.Result);
        }
        catch (Exception ex)
        {
            return new CommandResult(false, -1, string.Empty, ex.Message);
        }
    }

    private sealed record ListedDevice(string Serial, string State, string Description);

    private sealed record DeviceProbe(
        string Serial,
        string Model,
        string Manufacturer,
        bool IsQuest,
        bool QuestPadInstalled)
    {
        public AdbQuestDevice ToSelected() => new(Serial, Model, Manufacturer, QuestPadInstalled);
    }

    private readonly record struct CommandResult(bool Success, int ExitCode, string Stdout, string Stderr);
}
