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

    private async Task<LibraryModel> StartAsync(bool saveInPhotos)
    {
        var store = new DescriptionStore(_lib.PathOf(@"data\Descriptions.json"));
        var model = new LibraryModel(new Settings { SaveDescriptionsInPhotos = saveInPhotos, ReadOnly = false }, _lib.PathOf(@"data\Settings.json"),
            new MetadataCache(_lib.PathOf(@"data\m.json")), () => Task.FromResult(new List<string> { _lib.Root }), () => DateTime.Now.AddYears(1))
        { Descriptions = store };
        await model.StartAsync();
        await model.DetailFillTask;
        return model;
    }

    private MediaItem Item(LibraryModel model, string name) => model.AllItems.Single(i => i.Name == name);

    private async Task<string> CaptionOf(string name) => (await MetadataReader.ReadAsync(_lib.PathOf(name))).Caption;

    [WpfFact]
    public async Task SaveInPhotos_FillsEmptyCaptionsAndAltText_AndLeavesYoursAlone()
    {
        using var model = await StartAsync(saveInPhotos: true);
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
        // In the standard caption field, with Explorer's Title (empty) kept in step, and the alt text filled too.
        var fields = await XmpFields.ReadAsync(_lib.PathOf("plain.jpg"));
        Assert.Equal("A plain test picture.", fields.Caption);
        Assert.Equal("A plain test picture.", fields.AltText);
        Assert.Equal("A plain test picture.", await MetadataReader.ReadTitleAsync(_lib.PathOf("plain.jpg")));
        // The captioned photo's alt text was empty, so it's filled, but its caption isn't touched.
        Assert.Equal("A captioned test picture.", (await XmpFields.ReadAsync(_lib.PathOf("captioned.jpg"))).AltText);
    }

    [WpfFact]
    public async Task SaveInPhotos_LeavesAnotherAppsExtendedDescription_AndKeepsAMotionPhotosVideo()
    {
        _lib.AddJpeg("idt.jpg");
        await XmpFields.WriteAsync(_lib.PathOf("idt.jpg"), null, null, "A long description another app wrote.");
        var motion = _lib.AddJpeg("motion.jpg");
        File.AppendAllText(motion, "ftypmp42 the video part");
        var motionSize = new FileInfo(motion).Length;
        using var model = await StartAsync(saveInPhotos: true);
        var long1 = "A golden retriever runs along a sandy beach at sunset with a red ball in its mouth. Waves break behind it and two people walk in the distance near the dunes. The sky is orange and pink, and the low sun makes long shadows across the wet sand near the water's edge.";
        model.Descriptions!.Put(Item(model, "idt.jpg"), "accessible", long1);
        model.Descriptions.Put(Item(model, "motion.jpg"), "accessible", "A dog.");
        await model.CaptionsWritten;
        var fields = await XmpFields.ReadAsync(_lib.PathOf("idt.jpg"));
        Assert.Equal(FileFields.AltText(long1), fields.AltText);
        Assert.Equal("A long description another app wrote.", fields.ExtendedDescription);
        // Captioned in place, its video kept, if it had room; otherwise left exactly as it was.
        var motionAfter = File.ReadAllBytes(motion);
        Assert.Equal(motionSize, motionAfter.Length);
        Assert.Equal("ftypmp42 the video part"u8.ToArray(), motionAfter[^23..]);
    }

    [WpfFact]
    public async Task SaveInPhotos_WritesAnExtendedDescription_WhenTheAltTextIsShortened()
    {
        using var model = await StartAsync(saveInPhotos: true);
        var long1 = "A golden retriever runs along a sandy beach at sunset with a red ball in its mouth. Waves break behind it and two people walk in the distance near the dunes. The sky is orange and pink, and the low sun makes long shadows across the wet sand near the water's edge.";
        model.Descriptions!.Put(Item(model, "plain.jpg"), "accessible", long1);
        await model.CaptionsWritten;
        var fields = await XmpFields.ReadAsync(_lib.PathOf("plain.jpg"));
        Assert.Equal(FileFields.AltText(long1), fields.AltText);
        Assert.Equal(long1, fields.ExtendedDescription);
    }

    [WpfFact]
    public async Task WithSaveInPhotosOff_NothingIsWritten()
    {
        using var model = await StartAsync(saveInPhotos: false);
        model.Descriptions!.Put(Item(model, "plain.jpg"), "accessible", "Not saved in the photo.");
        await model.CaptionsWritten;
        Assert.Equal("", await CaptionOf("plain.jpg"));
        Assert.Equal("", (await XmpFields.ReadAsync(_lib.PathOf("plain.jpg"))).AltText);
    }

    [Fact]
    public void TheOldThreeWaySetting_CarriesOver()
    {
        var path = _lib.PathOf(@"data\old-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "CaptionPolicy": 2 }""");
        Assert.True(Settings.Load(path).SaveDescriptionsInPhotos);
        File.WriteAllText(path, """{ "CaptionPolicy": 1 }""");
        Assert.True(Settings.Load(path).SaveDescriptionsInPhotos);
        File.WriteAllText(path, """{ "CaptionPolicy": 0 }""");
        Assert.False(Settings.Load(path).SaveDescriptionsInPhotos);
        Assert.False(new Settings().SaveDescriptionsInPhotos);
    }

    [WpfFact]
    public async Task YourOwnChange_WhileACaptionIsWrittenFromADescription_WaitsRatherThanBeingRefused()
    {
        using var model = await StartAsync(saveInPhotos: true);
        model.Descriptions!.Put(Item(model, "plain.jpg"), "accessible", "A plain test picture.");
        // At once, while that caption is being written.
        var favorite = model.ToggleFavoriteAsync([model.Rows.Single(r => r.Name == "captioned.jpg")]);
        await Task.WhenAll(favorite, model.CaptionsWritten);
        Assert.DoesNotContain("Still saving the last change", _announced);
        Assert.Contains("Added to Favorites", _announced);
        Assert.Equal("A plain test picture.", await CaptionOf("plain.jpg"));
        Assert.False(model.IsWriting);
        Assert.False(model.IsWritingCaptions);
    }

    [WpfFact]
    public async Task ACaptionYouJustSaved_IsntReplaced()
    {
        using var model = await StartAsync(saveInPhotos: true);
        var plain = model.Rows.Single(r => r.Name == "plain.jpg");
        // Your caption starts saving, then a description arrives for the same photo.
        var yours = model.SaveCaptionAsync([plain], "Mine");
        model.Descriptions!.Put(plain.Item!, "accessible", "A plain test picture.");
        await Task.WhenAll(yours, model.CaptionsWritten);
        Assert.Equal("Mine", await CaptionOf("plain.jpg"));
    }

    [WpfFact]
    public async Task UseDescriptionAsCaption_SavesIt_OrSaysItHasntBeenDescribed_AndSaveInPhotosAsksOnce()
    {
        using var model = await StartAsync(saveInPhotos: false);
        var window = new MainWindow(model) { ShowActivated = false };
        window.Show();
        try
        {
            var plain = model.Rows.Single(r => r.Name == "plain.jpg");
            await window.UseDescriptionAsCaptionAsync([plain]);
            Assert.Contains("plain.jpg hasn't been described yet. Describe it first.", _announced);
            // Described only as another type: said which.
            model.Descriptions!.Put(plain.Item!, "brief", "Short.");
            await window.UseDescriptionAsCaptionAsync([plain]);
            Assert.Contains("plain.jpg hasn't been described as Accessible yet. Describe it first.", _announced);

            model.Descriptions!.Put(plain.Item!, "accessible", "A plain test picture.");
            await window.UseDescriptionAsCaptionAsync([plain]);
            Assert.Equal("A plain test picture.", await CaptionOf("plain.jpg"));
            Assert.Contains("Caption saved", _announced);

            // The command writes the alt text too, and the caption goes in the standard field.
            Assert.Equal("A plain test picture.", (await XmpFields.ReadAsync(_lib.PathOf("plain.jpg"))).AltText);

            // Turning Save Descriptions in Photos on asks once; Cancel leaves it off.
            var asked = new List<string>();
            var original = QuestionDialog.Ask;
            try
            {
                QuestionDialog.Ask = (_, _, question, _, yes) => { asked.Add($"{question} [{yes}]"); return false; };
                window.SetSaveInPhotos(true);
                Assert.Equal(["Save descriptions in your photos? [Save in Photos]"], asked);
                Assert.False(model.Settings.SaveDescriptionsInPhotos);
                Assert.False(window.SaveInPhotosItem.IsChecked);

                QuestionDialog.Ask = (_, _, _, _, _) => true;
                window.SetSaveInPhotos(true);
                Assert.True(Settings.Load(_lib.PathOf(@"data\Settings.json")).SaveDescriptionsInPhotos);
                Assert.True(window.SaveInPhotosItem.IsChecked);
                Assert.Contains("New descriptions are saved in your photos", _announced);

                // Turning it off doesn't ask.
                QuestionDialog.Ask = (_, _, _, _, _) => throw new InvalidOperationException("Asked to turn off");
                window.SetSaveInPhotos(false);
                Assert.False(model.Settings.SaveDescriptionsInPhotos);
                Assert.Contains("Descriptions aren't saved in your photos", _announced);
            }
            finally
            {
                QuestionDialog.Ask = original;
            }
        }
        finally
        {
            window.Close();
        }
    }
}
