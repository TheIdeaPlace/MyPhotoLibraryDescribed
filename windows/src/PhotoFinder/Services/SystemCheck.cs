using System.Globalization;
using Windows.Devices.Enumeration;

namespace PhotoFinder.Services;

/// <summary>
/// Help > Check System: whether this PC can do what the app is for, describing photos, and what to do if not. One
/// line per check in the Mac's layout and wording (#32): "Name: OK. detail", "Name: Needs attention. what to do", or
/// "Name: detail" for information.
/// </summary>
public sealed class SystemCheck
{
    public enum Status { Ok, Problem, Info }

    public sealed record Item(string Name, Status Status, string Detail)
    {
        public string Line => Status switch
        {
            Status.Ok => $"{Name}: OK. {Detail}",
            Status.Problem => $"{Name}: Needs attention. {Detail}",
            _ => $"{Name}: {Detail}",
        };

        // Screen readers name a list item from ToString.
        public override string ToString() => Line;
    }

    /// <summary>The facts the checks are made from, gathered separately so the wording can be tested.</summary>
    public sealed record Facts
    {
        public required string AppVersion { get; init; }
        /// <summary>Installed from PhotoFinder.appinstaller, so it updates itself.</summary>
        public bool UpdatesItself { get; init; }
        public required Version OsVersion { get; init; }
        /// <summary>The NPU's name, or null when Windows lists none.</summary>
        public string? Npu { get; init; }
        public required AIReadiness Readiness { get; init; }
        public IReadOnlyList<string> LibraryFolders { get; init; } = [];
        public IReadOnlyList<string> MissingFolders { get; init; } = [];
        public int SavedDescriptions { get; init; }
        public bool DescribeAutomatically { get; init; }
        public bool DescribeInBackground { get; init; }
    }

    /// <summary>Windows 11 24H2: the first build with Windows AI's image description.</summary>
    public const int MinimumBuild = 26100;

    public IReadOnlyList<Item> Items { get; }

    public SystemCheck(Facts facts)
    {
        var build = facts.OsVersion.Build;
        var items = new List<Item>
        {
            new(AppInfo.Name, Status.Info, $"Version {facts.AppVersion}." +
                (facts.UpdatesItself ? " Updates itself: Help > Check for Updates checks now." : " Not installed with its installer, so it doesn't update itself.")),
            build >= MinimumBuild
                ? new("Windows", Status.Ok, $"Build {build}.")
                : new("Windows", Status.Problem, $"Build {build}. Describing photos needs Windows 11 version 24H2 (build {MinimumBuild}) or later. Browsing works on this version."),
            facts.Npu is { } npu
                ? new("NPU", Status.Ok, $"{npu}.")
                : new("NPU", Status.Problem, "None found. Windows describes photos only on a Copilot+ PC, which has an NPU."),
        };

        if (build < MinimumBuild || facts.Npu is null)
        {
            // Already said above: one cause, one problem.
            items.Add(new("Windows AI", Status.Info, build < MinimumBuild ? "Not checked: it needs a newer Windows." : "Not checked: it needs an NPU."));
        }
        else
        {
            items.Add(facts.Readiness.State switch
            {
                "Ready" => new("Windows AI", Status.Ok, "Ready to describe photos, on this PC."),
                "NotReady" => new("Windows AI", Status.Problem, "Windows' image description model isn't downloaded yet. Describe Photo (Ctrl+Shift+D) downloads it."),
                "NotInstalled" => new("Windows AI", Status.Problem, $"The {AppInfo.HelperName} isn't installed. Install {AppInfo.Name} from PhotoFinder.appinstaller."),
                _ => new("Windows AI", Status.Problem, facts.Readiness.Message ?? $"Not available ({facts.Readiness.State})."),
            });
        }

        if (facts.LibraryFolders.Count == 0)
            items.Add(new("Library folders", Status.Problem, "None. Add one with File > Add Folder to Library."));
        else if (facts.MissingFolders.Count > 0)
            items.Add(new("Library folders", Status.Problem,
                $"{Folders(facts.LibraryFolders.Count)}, but {string.Join(", ", facts.MissingFolders)} can't be reached. Connect the drive, or remove the folder with File > Remove Folder from Library."));
        else
            items.Add(new("Library folders", Status.Ok, $"{Folders(facts.LibraryFolders.Count)}: {string.Join(", ", facts.LibraryFolders)}."));

        var saved = facts.SavedDescriptions;
        items.Add(new("Saved descriptions", Status.Info,
            saved == 0 ? "None yet." : saved == 1 ? "1 photo described." : $"{saved.ToString("N0", CultureInfo.CurrentCulture)} photos described."));
        items.Add(new("Describe Photos Automatically", Status.Info, facts.DescribeAutomatically ? "On." : "Off. Turn it on in the Photo menu."));
        items.Add(new("Describe Added Folders in the Background", Status.Info, facts.DescribeInBackground ? "On." : "Off."));
        Items = items;

        static string Folders(int n) => n == 1 ? "1 folder" : $"{n} folders";
    }

    public string Summary
    {
        get
        {
            var problems = Items.Count(i => i.Status == Status.Problem);
            return problems == 0 ? "Everything needed to describe photos is ready."
                : problems == 1 ? "1 thing needs attention." : $"{problems} things need attention.";
        }
    }

    /// <summary>Everything, for Copy to Clipboard and Report a Bug.</summary>
    public string Text => string.Join(Environment.NewLine, Items.Select(i => i.Line).Prepend(Summary));

    /// <summary>The NPU Windows lists (the Compute Accelerator device class), or null.</summary>
    public static async Task<string?> FindNpuAsync()
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(
                "System.Devices.ClassGuid:=\"{f01a9d53-3ff6-48d2-9f97-c8a7004be10c}\" AND System.Devices.Present:=System.StructuredQueryType.Boolean#True",
                null, DeviceInformationKind.Device);
            return devices.FirstOrDefault()?.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
