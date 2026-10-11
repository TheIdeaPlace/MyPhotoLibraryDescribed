namespace PhotoFinder.Tests;

/// <summary>Tests that touch the app's static state (FileRow.SpokenColumns, Announcer.Observer) run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedState
{
    public const string Name = "Shared static state";
}
