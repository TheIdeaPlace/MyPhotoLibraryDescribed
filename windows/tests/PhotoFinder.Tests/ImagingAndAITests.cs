using PhotoFinder.Services;

namespace PhotoFinder.Tests;

public class ImageDecodingTests
{
    [Theory]
    [InlineData(4000u, 3000u, 2048u, 2048u, 1536u)]
    [InlineData(3000u, 4000u, 2048u, 1536u, 2048u)]
    [InlineData(800u, 600u, 2048u, 800u, 600u)]
    [InlineData(0u, 0u, 2048u, 1u, 1u)]
    [InlineData(10000u, 10u, 1000u, 1000u, 1u)]
    public void FitWithin_KeepsShapeAndNeverGrows(uint w, uint h, uint max, uint ew, uint eh) =>
        Assert.Equal((ew, eh), ImageDecoding.FitWithin(w, h, max));

    [Fact]
    public async Task DecodeAsync_ScalesDown()
    {
        using var lib = new TestLibrary();
        var path = lib.AddJpeg("big.jpg", new TestLibrary.JpegOptions { Width = 400, Height = 200 });
        using var bitmap = await ImageDecoding.DecodeAsync(path, 100);
        Assert.Equal((100, 50), (bitmap.PixelWidth, bitmap.PixelHeight));
    }

    [Fact]
    public async Task DecodeAsync_TurnsSidewaysPhotosUpright()
    {
        using var lib = new TestLibrary();
        // Stored 400 × 200 with orientation 6 (rotate 90°): seen as 200 × 400.
        var path = lib.AddJpeg("sideways.jpg", new TestLibrary.JpegOptions { Width = 400, Height = 200, Orientation = 6 });
        using var full = await ImageDecoding.DecodeAsync(path, 4096);
        Assert.Equal((200, 400), (full.PixelWidth, full.PixelHeight));
        using var small = await ImageDecoding.DecodeAsync(path, 100);
        Assert.Equal((50, 100), (small.PixelWidth, small.PixelHeight));
    }

    [Fact]
    public async Task DecodeAsync_FailsForAFileThatIsntAPicture()
    {
        using var lib = new TestLibrary();
        var fake = lib.AddFile("fake.jpg");
        await Assert.ThrowsAnyAsync<Exception>(() => ImageDecoding.DecodeAsync(fake, 100));
    }

    [Fact]
    public async Task ToJpeg_MakesAJpegThatDecodesAgain()
    {
        using var lib = new TestLibrary();
        var path = lib.AddPng("a.png", 60, 40);
        using var bitmap = await ImageDecoding.DecodeAsync(path, 2048);
        var jpeg = await ImageDecoding.ToJpegAsync(bitmap);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg[..2]);
        var copy = lib.PathOf("copy.jpg");
        await File.WriteAllBytesAsync(copy, jpeg);
        using var again = await ImageDecoding.DecodeAsync(copy, 2048);
        Assert.Equal((60, 40), (again.PixelWidth, again.PixelHeight));
    }

    [StaFact]
    public async Task LoadForDisplay_GivesAPictureForAnImage_AndNullForGarbage()
    {
        using var lib = new TestLibrary();
        var image = await ImageDecoding.LoadForDisplayAsync(lib.AddJpeg("a.jpg"), false, 2400);
        Assert.NotNull(image);
        Assert.Equal(64, image!.Width, 0);
        var garbage = await ImageDecoding.LoadForDisplayAsync(lib.AddFile("fake.jpg"), false, 2400);
        Assert.Null(garbage);
    }
}

public class TextRecognitionTests
{
    [Fact]
    public async Task ReadsTextDrawnOnAPicture()
    {
        Assert.SkipUnless(TextRecognition.IsAvailable, "No OCR language is installed on this machine.");
        using var lib = new TestLibrary();
        var path = lib.AddJpeg("sign.jpg", new TestLibrary.JpegOptions { Text = "HELLO WORLD 42" });
        var lines = await TextRecognition.ReadLinesAsync(path);
        Assert.Contains("HELLO WORLD 42", string.Join(" ", lines) + " [read: " + string.Join(" / ", lines) + "]");
    }

    [Fact]
    public async Task APlainPicture_HasNoText()
    {
        Assert.SkipUnless(TextRecognition.IsAvailable, "No OCR language is installed on this machine.");
        using var lib = new TestLibrary();
        var lines = await TextRecognition.ReadLinesAsync(lib.AddJpeg("blank.jpg", new TestLibrary.JpegOptions { Width = 300, Height = 200 }));
        Assert.Empty(lines);
    }
}

public class WindowsAIProtocolTests
{
    [Fact]
    public void BuildRequest_IsOneJsonLineWithTheImageInBase64()
    {
        var line = WindowsAI.BuildRequest(7, "brief", [1, 2, 3]);
        Assert.DoesNotContain("\n", line);
        Assert.Contains("\"id\":7", line);
        Assert.Contains("\"kind\":\"brief\"", line);
        Assert.Contains("\"mime\":\"image/jpeg\"", line);
        Assert.Contains("\"image\":\"AQID\"", line);
    }

    [Fact]
    public void ParseResponse_ReturnsTheTrimmedDescription()
    {
        Assert.Equal("A dog.", WindowsAI.ParseResponse("""{"id": 3, "ok": true, "kind": "brief", "text": " A dog. ", "seconds": 2.4}""", 3));
    }

    [Fact]
    public void ParseResponse_ThrowsTheHelpersCodeAndMessage()
    {
        var ex = Assert.Throws<DescriptionException>(() =>
            WindowsAI.ParseResponse("""{"id": 3, "ok": false, "code": "content_filtered", "message": "Windows' content filter declined to describe this picture."}""", 3));
        Assert.Equal("content_filtered", ex.Code);
        Assert.Equal("Windows' content filter declined to describe this picture.", ex.Message);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"id": 9, "ok": true, "text": "for someone else"}""")]
    public void ParseResponse_RejectsAnswersThatCantBeTrusted(string line)
    {
        var ex = Assert.Throws<DescriptionException>(() => WindowsAI.ParseResponse(line, 3));
        Assert.Equal("bad_response", ex.Code);
    }

    [Fact]
    public void ParseResponse_AcceptsIdZero_WhichTheHelperUsesWhenItCouldntReadTheId()
    {
        var ex = Assert.Throws<DescriptionException>(() => WindowsAI.ParseResponse("""{"id": 0, "ok": false, "code": "too_large", "message": "Too big."}""", 3));
        Assert.Equal("too_large", ex.Code);
    }

    [Fact]
    public void ParseReadiness_ReadsTheHelpersCheck()
    {
        var ready = WindowsAI.ParseReadiness("""{"protocol":1,"version":"0.1.0","identity":true,"state":"Ready","kinds":["accessible"]}""");
        Assert.True(ready.Usable);
        Assert.Null(ready.Message);

        var notReady = WindowsAI.ParseReadiness("""{"protocol":1,"identity":true,"state":"NotReady"}""");
        Assert.True(notReady.Usable);

        var unsupported = WindowsAI.ParseReadiness("""{"protocol":1,"identity":true,"state":"NotSupportedOnCurrentSystem"}""");
        Assert.False(unsupported.Usable);
        Assert.Contains("Copilot+ PC", unsupported.Message);

        var noIdentity = WindowsAI.ParseReadiness("""{"protocol":1,"identity":false,"state":"NoIdentity","message":"Not installed as a package."}""");
        Assert.False(noIdentity.Usable);
        Assert.Equal("Not installed as a package.", noIdentity.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("{\"state\": 5}")]
    public void ParseReadiness_TreatsUnreadableAnswersAsUnavailable(string line)
    {
        var r = WindowsAI.ParseReadiness(line);
        Assert.False(r.Usable);
        Assert.False(string.IsNullOrEmpty(r.Message));
    }

    [Fact]
    public async Task Retries_TrySmallerSizes_OnlyAfterInternalErrors()
    {
        var sizes = new List<uint>();
        var calls = 0;
        var text = await WindowsAI.WithRetriesAsync(
            size => { sizes.Add(size); return Task.FromResult(new byte[] { 1 }); },
            _ => ++calls < 3 ? throw new DescriptionException("internal_error", "boom") : Task.FromResult("Worked small"));
        Assert.Equal("Worked small", text);
        Assert.Equal([2048u, 1024u, 512u], sizes);
    }

    [Fact]
    public async Task Retries_GiveUpWithTheLastInternalError()
    {
        var ex = await Assert.ThrowsAsync<DescriptionException>(() => WindowsAI.WithRetriesAsync<string>(
            _ => Task.FromResult(new byte[] { 1 }),
            _ => throw new DescriptionException("internal_error", "still broken")));
        Assert.Equal("still broken", ex.Message);
    }

    [Fact]
    public async Task Retries_DontRetryOtherFailures()
    {
        var calls = 0;
        await Assert.ThrowsAsync<DescriptionException>(() => WindowsAI.WithRetriesAsync<string>(
            _ => Task.FromResult(new byte[] { 1 }),
            _ => { calls++; throw new DescriptionException("content_filtered", "no"); }));
        Assert.Equal(1, calls);
    }
}

/// <summary>The real thing: only runs where the My Photo Library Described AI helper is installed and Windows' model is usable
/// (a Copilot+ PC with the helper registered). Everywhere else it reports itself skipped.</summary>
public class WindowsAIIntegrationTests
{
    [Fact]
    public async Task DescribesARealPhoto()
    {
        Assert.SkipUnless(WindowsAI.IsInstalled, "The My Photo Library Described AI helper isn't installed here.");
        var readiness = await WindowsAI.CheckAsync(TestContext.Current.CancellationToken);
        Assert.SkipUnless(readiness.State == "Ready", $"Windows' model isn't ready here: {readiness.State} {readiness.Message}");

        using var lib = new TestLibrary();
        var path = lib.AddJpeg("sign.jpg", new TestLibrary.JpegOptions { Text = "STOP" });
        using var ai = new WindowsAI();
        var text = await ai.DescribeAsync(async size =>
        {
            using var bitmap = await ImageDecoding.DecodeAsync(path, size);
            return await ImageDecoding.ToJpegAsync(bitmap);
        }, "brief", TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(text));

        // The same helper process answers a second request.
        var again = await ai.DescribeAsync(async size =>
        {
            using var bitmap = await ImageDecoding.DecodeAsync(path, size);
            return await ImageDecoding.ToJpegAsync(bitmap);
        }, "accessible", TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(again));
    }
}
