namespace PhotoFinder.Tests;

/// <summary>Tests that touch the app's static state (FileRow.SpokenColumns, Announcer.Observer) run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedState
{
    public const string Name = "Shared static state";
}

/// <summary>The writers refuse every change until the app allows them (#83). Tests of writing allow it from the start;
/// the read-only tests turn it off, and back on, inside the shared-state collection.</summary>
internal static class AllowPhotoChangesInTests
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Allow()
    {
        Services.PhotoChanges.Allowed = true;
        // Nothing a test does may land in the real data folder (the log, especially).
        Services.AppPaths.DataFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PhotoFinderTests", $"data-{Environment.ProcessId}");
    }
}
