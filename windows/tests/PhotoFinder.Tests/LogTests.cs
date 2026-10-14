using PhotoFinder.Services;

namespace PhotoFinder.Tests;

/// <summary>The diagnostic log: one file a day in the data folder, lines that can be read, and two weeks kept.</summary>
[Collection(SharedState.Name)]
public sealed class LogTests
{
    [Fact]
    public void FileExplorer_IsGivenThePackagesCopy_OfAFolderWindowsKeptInsideThePackage()
    {
        var root = Path.Combine(Path.GetTempPath(), "pf-explorer-" + Guid.NewGuid().ToString("N"));
        var local = Path.Combine(root, "Local");
        var package = Path.Combine(root, "Packages", "App_x", "LocalCache", "Local");
        var logs = Path.Combine(local, "My Photo Library Described", "Logs");
        try
        {
            // Not installed as a package, or nothing kept inside it: the usual path.
            Assert.Equal(logs, AppPaths.ForExplorer(logs, local, null));
            Assert.Equal(logs, AppPaths.ForExplorer(logs, local, package));
            // Windows kept the folder inside the package, where Explorer can see it.
            var moved = Path.Combine(package, "My Photo Library Described", "Logs");
            Directory.CreateDirectory(moved);
            Assert.Equal(moved, AppPaths.ForExplorer(logs, local + Path.DirectorySeparatorChar, package));
            // A folder outside %LOCALAPPDATA% is left alone.
            var elsewhere = Path.Combine(root, "Elsewhere");
            Assert.Equal(elsewhere, AppPaths.ForExplorer(elsewhere, local, package));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Lines_GoInTodaysFile_OnOneLineEach_InTheTestsOwnDataFolder()
    {
        Assert.StartsWith(Path.GetTempPath(), Log.Folder);
        Log.Write("test", "First line\nwith a break");
        Log.Error("test", "Something failed", new InvalidOperationException("Bad thing"));
        var lines = File.ReadAllLines(Log.TodaysFile);
        Assert.Contains(lines, l => l.EndsWith("[test] First line with a break"));
        Assert.Contains(lines, l => l.Contains("[test] Something failed: InvalidOperationException 0x") && l.Contains("Bad thing"));
    }

    [Fact]
    public void TheUserFolder_IsWrittenAsUserProfile_AndCrashesKeepTheInnerException()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Log.Write("test", $@"Couldn't read {profile}\Pictures\a.jpg");
        Log.Crash("test", "Crashed", new AggregateException(new InvalidOperationException("The inner one")));
        var text = File.ReadAllText(Log.TodaysFile);
        Assert.Contains(@"Couldn't read %USERPROFILE%\Pictures\a.jpg", text);
        Assert.DoesNotContain(profile + @"\Pictures", text);
        Assert.Contains("The inner one", text);
    }

    [Fact]
    public void FilesOlderThanTwoWeeks_AreRemoved()
    {
        Directory.CreateDirectory(Log.Folder);
        var old = Path.Combine(Log.Folder, "PhotoFinder-2000-01-01.log");
        var recent = Path.Combine(Log.Folder, $"PhotoFinder-{DateTime.Now.AddDays(-3):yyyy-MM-dd}.log");
        File.WriteAllText(old, "old");
        File.WriteAllText(recent, "recent");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-(Log.DaysKept + 2)));
        File.SetLastWriteTime(recent, DateTime.Now.AddDays(-3));
        Log.RemoveOld();
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }
}
