using System.Globalization;
using Microsoft.Win32;
using Windows.ApplicationModel;
using Windows.Devices.Enumeration;
using Windows.Management.Deployment;
using Windows.System.Power;

namespace PhotoFinder.Services;

/// <summary>
/// The details Check System shows about this PC and the AI it uses (#94), for bug reports and for people checking their
/// own setup: Windows' edition, the processor, memory and power, the NPU's driver, and the versions of the Windows AI
/// pieces describing depends on. All read without administrator rights. Each one is best effort: a detail that can't be
/// read is left out, never an error.
/// </summary>
public static class SystemDetails
{
    /// <summary>"Windows 11 Pro 26H2, build 26340.9616". The registry's ProductName still says "Windows 10" on Windows 11,
    /// so the name comes from the build.</summary>
    public static string? WindowsEdition()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is null) return null;
            var build = Environment.OSVersion.Version.Build;
            var ubr = key.GetValue("UBR") is int u ? $".{u}" : "";
            var product = key.GetValue("ProductName") as string ?? "Windows";
            if (build >= 22000) product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
            var display = key.GetValue("DisplayVersion") as string;
            return $"{product}{(display is { Length: > 0 } ? " " + display : "")}, build {build}{ubr}";
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The processor's name, without (R) and (TM), and its architecture.</summary>
    public static string? Processor()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var name = key?.GetValue("ProcessorNameString") as string;
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
            return name is { Length: > 0 } ? $"{SystemCheck.CleanName(name)}, {arch}" : arch;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The PC's memory, in whole GB.</summary>
    public static string? Memory()
    {
        var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return bytes > 0 ? $"{Math.Round(bytes / (1024.0 * 1024 * 1024)).ToString(CultureInfo.CurrentCulture)} GB" : null;
    }

    /// <summary>Plugged in or on battery, and whether energy saver is on, which slows describing and pauses work ahead.</summary>
    public static string? Power()
    {
        try
        {
            var battery = PowerManager.BatteryStatus;
            var source = battery == BatteryStatus.NotPresent ? "No battery"
                : PowerManager.PowerSupplyStatus == PowerSupplyStatus.NotPresent ? $"On battery, {PowerManager.RemainingChargePercent}%"
                : $"Plugged in, battery {PowerManager.RemainingChargePercent}%";
            var saver = PowerManager.EnergySaverStatus == EnergySaverStatus.On ? "energy saver on" : "energy saver off";
            return $"{source}, {saver}";
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The NPU's driver: "version 30.0.220.3000 from Qualcomm Technologies, Inc., dated 1/7/2026".</summary>
    public static async Task<string?> NpuDriverAsync()
    {
        // DEVPKEY_Device_DriverVersion, DriverDate and DriverProvider: they have no canonical names to ask for them by.
        const string version = "{a8b865dd-2e3d-4094-ad97-e593a70c75d6} 3";
        const string date = "{a8b865dd-2e3d-4094-ad97-e593a70c75d6} 2";
        const string provider = "{a8b865dd-2e3d-4094-ad97-e593a70c75d6} 9";
        try
        {
            var devices = await DeviceInformation.FindAllAsync(
                "System.Devices.ClassGuid:=\"{f01a9d53-3ff6-48d2-9f97-c8a7004be10c}\" AND System.Devices.Present:=System.StructuredQueryType.Boolean#True",
                [version, date, provider], DeviceInformationKind.Device);
            if (devices.FirstOrDefault() is not { } npu) return null;
            return DriverText(
                npu.Properties.TryGetValue(version, out var v) ? v as string : null,
                npu.Properties.TryGetValue(provider, out var p) ? p as string : null,
                npu.Properties.TryGetValue(date, out var d) && d is DateTimeOffset when ? when : null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static string? DriverText(string? version, string? provider, DateTimeOffset? date)
    {
        if (version is null) return null;
        var text = $"version {version}";
        if (provider is { Length: > 0 }) text += $" from {provider}";
        // Windows stores the date as midnight UTC; the local date would be a day early in the Americas.
        if (date is { } d) text += $", dated {d.UtcDateTime.Date.ToString("d", CultureInfo.CurrentCulture)}";
        return text;
    }

    /// <summary>Photo Finder's own package, as Windows names it, when it's installed as one.</summary>
    public static string? PackageName()
    {
        try { return Package.Current.Id.FullName; }
        catch (Exception) { return null; }
    }

    /// <summary>The Windows App Runtime the AI helper runs on, as "Microsoft.WindowsAppRuntime.1.8 8000.994.2142.0".</summary>
    public static string? AppRuntime()
    {
        try
        {
            return Package.Current.Dependencies
                .Where(d => d.Id.Name.StartsWith("Microsoft.WindowsAppRuntime", StringComparison.OrdinalIgnoreCase))
                .Select(d => $"{d.Id.Name} {Version(d.Id.Version)}")
                .FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The Windows AI pieces describing depends on, newest version of each, as Windows lists them: Windows' AI
    /// components, the workload manager and host, and the NPU's execution providers (Qualcomm's, Intel's or AMD's).</summary>
    public static IReadOnlyList<(string Name, string Version)> AIPackages()
    {
        try
        {
            var found = new PackageManager().FindPackagesForUser("")
                .Select(p => (p.Id.Name, p.Id.Version))
                .Where(p => IsAIPackage(p.Name))
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.MaxBy(p => (p.Version.Major, p.Version.Minor, p.Version.Build, p.Version.Revision)))
                .Select(p => (p.Name, Version(p.Version)));
            return Order(found);
        }
        catch (Exception)
        {
            return [];
        }
    }

    internal static bool IsAIPackage(string name) =>
        name is "MicrosoftWindows.Client.CoreAI" or "MicrosoftWindows.Client.AIX"
        || name.StartsWith("WindowsWorkload.EP.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("WindowsWorkload.Manager.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("WindowsWorkload.Host.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("WindowsWorkload.TextRecognition.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("WindowsWorkload.ImageContentModeration.", StringComparison.OrdinalIgnoreCase);

    internal static List<(string Name, string Version)> Order(IEnumerable<(string Name, string Version)> packages) =>
        packages.OrderBy(p => p.Name.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static string Version(PackageVersion v) => $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
}
