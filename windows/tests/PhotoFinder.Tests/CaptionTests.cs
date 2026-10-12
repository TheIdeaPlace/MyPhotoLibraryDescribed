using System.Windows.Controls;
using PhotoFinder.Models;
using PhotoFinder.Services;
using PhotoFinder.ViewModels;
using PhotoFinder.Views;

namespace PhotoFinder.Tests;

/// <summary>Descriptions as captions (#30): Photo > Use Description as Caption and Save Descriptions as Captions,
/// matching the Mac. Captions are the file's Title.</summary>
[Collection(SharedState.Name)]
public sealed class CaptionTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly List<string> _announced = [];

    public CaptionTests()
    {
        Announcer.Observer = _announced.Add;
        _lib.AddJpeg("plain.jpg");
        _lib.AddJpeg("captioned.jpg", new TestLibrary.JpegOptions { Title = "My own words" });
    }

    public void Dispose()
    {
        Announcer.Observer = null;
        _lib.Dispose();
    }

    [Theory]
    [InlineData(CaptionPolicy.Never, "", false)]
    [InlineData(CaptionPolicy.WhenEmpty, "", true)]
    [InlineData(CaptionPolicy.WhenEmpty, "Mine", false)]
    [InlineData(CaptionPolicy.Always, "Mine", true)]
    [InlineData(CaptionPolicy.Always, "A dog.", false)]
    public void ThePolicy_DecidesFromTheCaptionThere(CaptionPolicy policy, string existing, bool write)
    {
        Assert.Equal(write, policy.ShouldWrite(" A dog. ", existing));
        Assert.False(CaptionPolicy.Always.ShouldWrite("   ", ""));
    }

    private async Task<LibraryModel> StartAsync(CaptionPolicy policy)
    {
        var store = new DescriptionStore(_lib.PathOf(@"data\Descriptions.json"));
        var model = new LibraryModel(new Settings { CaptionPolicy = policy }, _lib.PathOf(@"data\Settings.json"),
            new MetadataCache(_lib.PathOf(@"data\m.json")), () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1))
        { Descriptions = store };
        await model.StartAsync();
        await model.DetailFillTask;
        return model;
    }

    private MediaItem Item(LibraryModel model, string name) => model.AllItems.Single(i => i.Name == name);

    private async Task<string> CaptionOf(string name) => (await MetadataReader.ReadAsync(_lib.PathOf(name))).Caption;

    [WpfFact]
    public async Task OnlyWhenThereIsNoCaption_FillsEmptyCaptions_AndLeavesYoursAlone()
    {
        using var model = await StartAsync(CaptionPolicy.WhenEmpty);
        // As if the list hadn't read its details yet: the file decides, not the list's copy.
        Item(model, "captioned.jpg").Metadata = null;
        model.Descriptions!.Put(Item(model, "plain.jpg"), "accessible", "A plain test picture.");
        model.Descriptions.Put(Item(model, "captioned.jpg"), "accessible", "A captioned test picture.");
        await model.CaptionsWritten;
        Assert.Equal("A plain test picture.", await CaptionOf("plain.jpg"));
        Assert.Equal("My own words", await CaptionOf("captioned.jpg"));
        // Quietly: nothing said, no dialog.
        Assert.DoesNotContain(_announced, a => a.Contains("Caption"));
        // The description is kept for the file, whose size and date the caption changed.
        Assert.Equal("A plain test picture.", model.Descriptions.Get(Item(model, "plain.jpg"), "accessible"));
    }

    [WpfFact]
    public async Task Always_ReplacesCaptions_AndNever_LeavesThemAlone()
    {
        using (var model = await StartAsync(CaptionPolicy.Never))
        {
            model.Descriptions!.Put(Item(model, "plain.jpg"), "accessible", "Not saved as a caption.");
            await model.CaptionsWritten;
            Assert.Equal("", await CaptionOf("plain.jpg"));
        }
        using (var model = await StartAsync(CaptionPolicy.Always))
        {
            model.Descriptions!.Put(Item(model, "captioned.jpg"), "accessible", "Replaced by the description.");
            await model.CaptionsWritten;
            Assert.Equal("Replaced by the description.", await CaptionOf("captioned.jpg"));
        }
    }

    [WpfFact]
    public async Task UseDescriptionAsCaption_SavesIt_OrSaysItHasntBeenDescribed_AndTheSettingIsAMenuChoice()
    {
        using var model = await StartAsync(CaptionPolicy.Never);
        var window = new MainWindow(model) { ShowActivated = false };
        window.Show();
        try
        {
            var plain = model.Rows.Single(r => r.Name == "plain.jpg");
            await window.UseDescriptionAsCaptionAsync([plain]);
            Assert.Contains("plain.jpg hasn't been described yet. Describe it first.", _announced);

            model.Descriptions!.Put(plain.Item!, "accessible", "A plain test picture.");
            await window.UseDescriptionAsCaptionAsync([plain]);
            Assert.Equal("A plain test picture.", await CaptionOf("plain.jpg"));
            Assert.Contains("Caption saved", _announced);

            var menu = (MenuItem)window.FindName("CaptionPolicyMenu");
            Assert.Equal(["Never", "Only When There's No Caption", "Always"],
                menu.Items.OfType<MenuItem>().Select(m => ((string)m.Header).Replace("_", "")));
            window.SetCaptionPolicy(CaptionPolicy.WhenEmpty);
            Assert.Equal(CaptionPolicy.WhenEmpty, Settings.Load(_lib.PathOf(@"data\Settings.json")).CaptionPolicy);
        }
        finally
        {
            window.Close();
        }
    }
}
