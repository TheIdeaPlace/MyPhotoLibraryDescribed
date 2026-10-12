using PhotoFinder.Services;

namespace PhotoFinder.Tests;

/// <summary>Captions and accessibility fields in files (#31, #62), matching the Mac's FileMetadataWriter.</summary>
[Collection(SharedState.Name)]
public sealed class FileFieldsTests : IDisposable
{
    private readonly TestLibrary _lib = new();

    public void Dispose() => _lib.Dispose();

    [Fact]
    public void AltText_IsTheDescription_OrTheSentencesThatFitIn250()
    {
        Assert.Equal("A dog on a beach.", FileFields.AltText("  A dog on a beach.  "));
        Assert.Null(FileFields.ExtendedDescription("A dog on a beach."));

        var first = "A golden retriever runs along a sandy beach at sunset with a red ball in its mouth. ";
        var second = "Waves break behind it and two people walk in the distance near the dunes. ";
        var third = "The sky is orange and pink, and the low sun makes long shadows across the wet sand near the water's edge.";
        var long3 = first + second + third;
        Assert.True(long3.Length > 250);
        var alt = FileFields.AltText(long3);
        Assert.Equal((first + second).Trim(), alt);
        Assert.Equal(long3.Trim(), FileFields.ExtendedDescription(long3));

        // One sentence too long: whole words, then "…", with no comma left before it.
        var words = string.Join(", ", Enumerable.Repeat("a dog on a beach", 30));
        var cut = FileFields.AltText(words);
        Assert.True(cut.Length <= 250);
        Assert.EndsWith("…", cut);
        Assert.DoesNotContain(",…", cut);

        // Never half an emoji, and any whitespace is a word break.
        var emoji = string.Concat(Enumerable.Repeat("🐕", 200));
        var cutEmoji = FileFields.AltText(emoji);
        Assert.False(char.IsHighSurrogate(cutEmoji[^2]), "A character was split.");
        var tabs = string.Join("\t", Enumerable.Repeat("dog", 100));
        Assert.EndsWith("dog…", FileFields.AltText(tabs));

        // Full-width sentence ends, with no space after them.
        var cjk = string.Concat(Enumerable.Repeat("海辺を走る犬がいます。", 30));
        var cjkAlt = FileFields.AltText(cjk);
        Assert.EndsWith("。", cjkAlt);
        Assert.True(cjkAlt.Length <= 250);
    }

    [Fact]
    public void Replaced_ListsWhatsAlreadyThere_ButNotEmptyOrMatchingFields()
    {
        Assert.Empty(FileFields.Replaced("", "", "", "A dog."));
        Assert.Empty(FileFields.Replaced("A dog.", "A dog.", "", "A dog."));
        Assert.Equal([("caption", "Mine")], FileFields.Replaced("Mine", "", "", "A dog."));
        Assert.Equal([("caption", "Mine"), ("alt text", "Other app")], FileFields.Replaced("Mine", "Other app", "", "A dog."));
        // Another app's extended description would be cleared (or replaced): that's asked about too.
        Assert.Equal([("extended description", "A long one from IDT.")], FileFields.Replaced("", "", "A long one from IDT.", "A dog."));
    }

    [Fact]
    public void ExplorersTitle_FollowsTheCaption_OnlyWhenEmptyOrTheOldCaption()
    {
        Assert.True(FileFields.TitleKeepsInStep("", "Old"));
        Assert.True(FileFields.TitleKeepsInStep("Old", "Old"));
        Assert.False(FileFields.TitleKeepsInStep("A title someone wrote", "Old"));
    }

    private static byte[] Scan(byte[] b)
    {
        for (var i = 2; i < b.Length - 1; i++) if (b[i] == 0xFF && b[i + 1] == 0xDA) return b[i..];
        return [];
    }

    [Fact]
    public async Task AJpegWithRoom_GetsTheFieldsInPlace_KeepingItsPictureDateTitleAndRating()
    {
        var path = _lib.AddJpeg("a.jpg", new TestLibrary.JpegOptions { Title = "My title", Rating = 4, DateTaken = new DateTime(2022, 6, 1, 10, 0, 0) });
        var before = await MetadataReader.ReadAsync(path);
        var scan = Scan(File.ReadAllBytes(path));
        var modified = File.GetLastWriteTimeUtc(path);

        await XmpFields.WriteAsync(path, "A dog on a beach.", "A dog.", "");
        var fields = await XmpFields.ReadAsync(path);
        Assert.Equal(new XmpFields.Fields("A dog on a beach.", "A dog.", "", HasCaption: true), fields);
        Assert.True(scan.AsSpan().SequenceEqual(Scan(File.ReadAllBytes(path))), "The picture changed.");
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        var after = await MetadataReader.ReadAsync(path);
        Assert.Equal(before.DateTaken, after.DateTaken);
        Assert.Equal(before.Rating, after.Rating);
        Assert.Equal("My title", await MetadataReader.ReadTitleAsync(path));
        // The standard caption is read first, ahead of the Title.
        Assert.Equal("A dog on a beach.", after.Caption);
        Assert.Equal("A dog.", after.AltText);

        // Cleared: it stays cleared, rather than the Title someone wrote taking its place.
        await XmpFields.WriteAsync(path, "", null, null);
        Assert.Equal("", (await XmpFields.ReadAsync(path)).Caption);
        Assert.Equal("", (await MetadataReader.ReadAsync(path)).Caption);
        Assert.Equal("My title", await MetadataReader.ReadTitleAsync(path));
    }

    /// <summary>The JPEG without its APP segments (EXIF, XMP and their padding), so there's no room for the fields.</summary>
    private static byte[] WithoutRoom(byte[] b)
    {
        var output = new List<byte> { 0xFF, 0xD8 };
        var i = 2;
        while (b[i] == 0xFF && b[i + 1] is >= 0xE1 and <= 0xEF)
            i += 2 + ((b[i + 2] << 8) | b[i + 3]);
        output.AddRange(b[i..]);
        return output.ToArray();
    }

    private static async Task<byte[]> PixelsAsync(string path)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        var pixels = await decoder.GetPixelDataAsync();
        return pixels.DetachPixelData();
    }

    [Fact]
    public async Task AJpegWithoutRoom_IsRewrittenBesideItAndSwappedIn_WithTheSamePixelsAndDate()
    {
        var path = _lib.AddJpeg("tight.jpg");
        File.WriteAllBytes(path, WithoutRoom(File.ReadAllBytes(path)));
        File.SetLastWriteTimeUtc(path, new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc));
        var size = new FileInfo(path).Length;
        var pixels = await PixelsAsync(path);

        await XmpFields.WriteAsync(path, "A dog.", "A dog.", "");
        Assert.True(new FileInfo(path).Length > size, "It wasn't rewritten with room.");
        Assert.Equal("A dog.", (await XmpFields.ReadAsync(path)).Caption);
        Assert.Equal(pixels, await PixelsAsync(path));
        Assert.Equal(new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc), File.GetLastWriteTimeUtc(path));
        // Nothing left beside it.
        Assert.Equal(["tight.jpg"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ATiff_GetsTheFields_KeepingItsPixels()
    {
        var path = _lib.PathOf("a.tif");
        TestLibrary.RunSta(() =>
        {
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(8, 6, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null,
                Enumerable.Range(0, 8 * 6 * 3).Select(i => (byte)i).ToArray(), 8 * 3);
            var encoder = new System.Windows.Media.Imaging.TiffBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        });
        var pixels = await PixelsAsync(path);
        await XmpFields.WriteAsync(path, "A gradient.", "A gradient.", "");
        Assert.Equal("A gradient.", (await XmpFields.ReadAsync(path)).Caption);
        Assert.Equal(pixels, await PixelsAsync(path));
    }

    // MARK: JPEGs changed segment by segment (#86)

    private static readonly byte[] XmpSignature = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();

    /// <summary>The header segments (marker, offset, length including the marker), up to the picture's start.</summary>
    private static List<(byte Marker, int Offset, int Length)> Segments(byte[] b)
    {
        var list = new List<(byte, int, int)>();
        var i = 2;
        while (b[i] == 0xFF && b[i + 1] is not (0xDA or 0xD9))
        {
            var length = 2 + ((b[i + 2] << 8) | b[i + 3]);
            list.Add((b[i + 1], i, length));
            i += length;
        }
        return list;
    }

    private static bool IsXmp(byte[] b, (byte Marker, int Offset, int Length) s) =>
        s.Marker == 0xE1 && b.AsSpan(s.Offset + 4).StartsWith(XmpSignature);

    /// <summary>The file with its XMP segment taken out: everything Photo Finder must leave as it was.</summary>
    private static byte[] WithoutXmp(byte[] b)
    {
        var output = new List<byte>();
        var at = 0;
        foreach (var s in Segments(b).Where(s => IsXmp(b, s)))
        {
            output.AddRange(b[at..s.Offset]);
            at = s.Offset + s.Length;
        }
        output.AddRange(b[at..]);
        return output.ToArray();
    }

    /// <summary>A segment inserted after the start-of-image marker.</summary>
    private static byte[] WithSegment(byte[] jpeg, byte marker, byte[] payload)
    {
        var length = payload.Length + 2;
        return [0xFF, 0xD8, 0xFF, marker, (byte)(length >> 8), (byte)length, .. payload, .. jpeg[2..]];
    }

    private static readonly byte[] Video = "ftypmp42 the video part"u8.ToArray();

    [Fact]
    public async Task ACaptionChangesOnlyTheXmp_KeepingExifCommentsThePictureAndAMotionPhotosVideo()
    {
        var path = _lib.AddJpeg("motion.jpg", new TestLibrary.JpegOptions { DateTaken = new DateTime(2022, 6, 1) });
        var original = WithSegment(File.ReadAllBytes(path), 0xFE, "A comment from the camera"u8.ToArray());
        File.WriteAllBytes(path, [.. original, .. Video]);
        var before = File.ReadAllBytes(path);

        Assert.Equal(1, (await MetadataWriter.SetFieldsAsync([MetadataWriter.FieldChanges.Replacing(ItemFor(path), "A dog on a beach.")])).Succeeded);
        var after = File.ReadAllBytes(path);
        Assert.Equal(WithoutXmp(before), WithoutXmp(after));
        Assert.Equal(Video, after[^Video.Length..]);
        var fields = await XmpFields.ReadAsync(path);
        Assert.Equal(("A dog on a beach.", "A dog on a beach."), (fields.Caption, fields.AltText));
        // Explorer's Title (empty) follows, from the XMP.
        Assert.Equal("A dog on a beach.", await MetadataReader.ReadTitleAsync(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));

        // A second change fits in the room left the first time: the file keeps its size.
        var size = after.Length;
        await XmpFields.WriteAsync(path, "A dog.", null, null);
        Assert.Equal(size, new FileInfo(path).Length);
        Assert.Equal(WithoutXmp(before), WithoutXmp(File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task ARating_GoesInTheXmp_AndExifsOwnRatingIsPatchedInPlace_SoExplorerReadsIt()
    {
        var path = _lib.AddJpeg("rated.jpg", new TestLibrary.JpegOptions { Rating = 2 });
        File.WriteAllBytes(path, [.. File.ReadAllBytes(path), .. Video]);
        var original = File.ReadAllBytes(path);
        var exif = Segments(original).Where(s => s.Marker == 0xE1 && !IsXmp(original, s)).Select(s => original[s.Offset..(s.Offset + s.Length)]).ToList();

        Assert.Equal(1, (await MetadataWriter.SetRatingAsync([ItemFor(path)], 5)).Succeeded);
        Assert.Equal(5, (await MetadataReader.ReadAsync(path)).Rating);
        var after = File.ReadAllBytes(path);
        var exifAfter = Segments(after).Where(s => s.Marker == 0xE1 && !IsXmp(after, s)).Select(s => after[s.Offset..(s.Offset + s.Length)]).ToList();
        Assert.Equal(exif.Select(e => e.Length), exifAfter.Select(e => e.Length));
        // Only the two rating values (two bytes each) may differ.
        Assert.InRange(exif.Zip(exifAfter).Sum(p => p.First.Zip(p.Second).Count(b => b.First != b.Second)), 0, 4);
        Assert.Equal(Video, after[^Video.Length..]);

        Assert.Equal(1, (await MetadataWriter.SetRatingAsync([ItemFor(path)], 0)).Succeeded);
        Assert.Equal(0, (await MetadataReader.ReadAsync(path)).Rating);
    }

    [Fact]
    public async Task AMultiPictureFile_IsChangedOnlyWhenItKeepsItsSize()
    {
        // An MPF index (Ultra HDR, MPO) records the main picture's size, so the file can't grow.
        byte[] mpf = [(byte)'M', (byte)'P', (byte)'F', 0, 0x49, 0x49, 0x2A, 0x00];
        var path = _lib.AddJpeg("hdr.jpg");
        File.WriteAllBytes(path, [.. WithSegment(WithoutRoom(File.ReadAllBytes(path)), 0xE2, mpf), .. "the gain map"u8.ToArray()]);
        var before = File.ReadAllBytes(path);
        var refused = await MetadataWriter.SetCaptionAsync([ItemFor(path)], "A dog.");
        Assert.Equal(0, refused.Succeeded);
        Assert.Contains("second picture", refused.FailureSummary);
        Assert.Equal(before, File.ReadAllBytes(path));

        // With an XMP that has room (written before the index was added), a change that fits is made.
        var roomy = _lib.AddJpeg("roomy.jpg");
        File.WriteAllBytes(roomy, WithoutRoom(File.ReadAllBytes(roomy)));
        await XmpFields.WriteAsync(roomy, "First.", null, null);
        // The read-back's decoder keeps the file mapped until it's collected.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        File.WriteAllBytes(roomy, [.. WithSegment(File.ReadAllBytes(roomy), 0xE2, mpf), .. "the gain map"u8.ToArray()]);
        var size = new FileInfo(roomy).Length;
        await XmpFields.WriteAsync(roomy, "Second.", null, null);
        Assert.Equal(size, new FileInfo(roomy).Length);
        Assert.Equal("Second.", (await XmpFields.ReadAsync(roomy)).Caption);
        Assert.EndsWith("the gain map", File.ReadAllText(roomy));
    }

    [Fact]
    public void OtherAppsXmp_IsKept_AndOtherLanguagesToo()
    {
        var xmp = """
            <?xpacket begin="﻿" id="W5M0MpCehiHzreSzNTczkc9d"?><x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description rdf:about="" xmlns:GCamera="http://ns.google.com/photos/1.0/camera/" xmlns:dc="http://purl.org/dc/elements/1.1/" GCamera:MotionPhoto="1" GCamera:MicroVideoOffset="23"><dc:description><rdf:Alt><rdf:li xml:lang="x-default">Old</rdf:li><rdf:li xml:lang="fr">Ancien</rdf:li></rdf:Alt></dc:description></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end="w"?>
            """;
        var plain = WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("a.jpg")));
        var jpeg = WithSegment(plain, 0xE1, [.. XmpSignature, .. System.Text.Encoding.UTF8.GetBytes(xmp)]);
        var changed = JpegMetadata.Apply(jpeg, new JpegMetadata.Changes { Caption = "New", AltText = "New" });
        var s = Segments(changed).Single(x => IsXmp(changed, x));
        var text = System.Text.Encoding.UTF8.GetString(changed, s.Offset + 4 + XmpSignature.Length, s.Length - 4 - XmpSignature.Length);
        Assert.Contains("GCamera:MotionPhoto=\"1\"", text);
        Assert.Contains("GCamera:MicroVideoOffset=\"23\"", text);
        Assert.Contains(">Ancien<", text);
        Assert.Contains(">New<", text);
        Assert.DoesNotContain(">Old<", text);
        Assert.Equal(plain[2..], changed[(s.Offset + s.Length)..]);
    }

    [Fact]
    public void XmpItCantRead_OrExtendedXmp_IsLeftAlone()
    {
        var plain = WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("a.jpg")));
        var broken = WithSegment(plain, 0xE1, [.. XmpSignature, .. "<x:xmpmeta not xml"u8.ToArray()]);
        Assert.Throws<MetadataRefusedException>(() => JpegMetadata.Apply(broken, new JpegMetadata.Changes { Caption = "A" }));
        var extended = WithSegment(plain, 0xE1, [.. "http://ns.adobe.com/xmp/extension/\0"u8.ToArray(), 1, 2, 3]);
        Assert.Throws<MetadataRefusedException>(() => JpegMetadata.Apply(extended, new JpegMetadata.Changes { Caption = "A" }));
        Assert.Throws<MetadataRefusedException>(() => JpegMetadata.Apply("not a jpeg"u8.ToArray(), new JpegMetadata.Changes { Caption = "A" }));
    }

    [Fact]
    public async Task AnAccentedTitleInExif_IsEmptiedWhenTheCaptionReplacesIt()
    {
        var path = _lib.AddJpeg("cafe.jpg");
        // Windows' own handler writes the Title into EXIF (ImageDescription as UTF-8, and XPTitle).
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        await file.Properties.SavePropertiesAsync([new KeyValuePair<string, object?>("System.Title", "Café au lait")]);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Equal(1, (await MetadataWriter.SetCaptionAsync([ItemFor(path)], "Un café.")).Succeeded);
        Assert.Equal("Un café.", await MetadataReader.ReadTitleAsync(path));
        var check = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        var subject = await check.Properties.RetrievePropertiesAsync(["System.Subject"]);
        Assert.NotEqual("Café au lait", subject.TryGetValue("System.Subject", out var value) ? value as string : null);
        // Nowhere in the file any more: not in EXIF's ImageDescription, nor its XPTitle (UTF-16), nor the XMP.
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.AsSpan().IndexOf("Café au lait"u8) < 0, "The old title is still in the file as UTF-8.");
        Assert.True(bytes.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes("Café au lait")) < 0, "The old title is still in XPTitle.");
    }

    [Fact]
    public async Task ARefusal_SaysWhy()
    {
        var path = _lib.AddJpeg("ext.jpg");
        File.WriteAllBytes(path, WithSegment(WithoutRoom(File.ReadAllBytes(path)), 0xE1, [.. "http://ns.adobe.com/xmp/extension/\0"u8.ToArray(), 1, 2, 3]));
        var result = await MetadataWriter.SetCaptionAsync([ItemFor(path)], "A dog.");
        Assert.Equal(0, result.Succeeded);
        Assert.Equal("ext.jpg: Its metadata is spread over several parts (Extended XMP), which isn't changed. It was left as it is.", result.FailureSummary);
    }

    [Fact]
    public void XmpThatIsntUtf8_IsLeftAlone_RatherThanChanged()
    {
        var plain = WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("a.jpg")));
        byte[] latin = [.. "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:my=\"urn:my\" my:Name=\"Caf"u8.ToArray(), 0xE9, .. "\"/></rdf:RDF></x:xmpmeta>"u8.ToArray()];
        var jpeg = WithSegment(plain, 0xE1, [.. XmpSignature, .. latin]);
        Assert.Throws<MetadataRefusedException>(() => JpegMetadata.Apply(jpeg, new JpegMetadata.Changes { Caption = "A" }));
    }

    [Fact]
    public void ACaptionInOneLanguage_IsReplacedThere_AndLineBreaksStayEscaped()
    {
        var xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:my=\"urn:my\"><my:Place>Kona&#xD;&#xA;HI</my:Place><dc:description><rdf:Alt><rdf:li xml:lang=\"en-US\">Old</rdf:li></rdf:Alt></dc:description></rdf:Description></rdf:RDF></x:xmpmeta>";
        var plain = WithoutRoom(File.ReadAllBytes(_lib.AddJpeg("a.jpg")));
        var jpeg = WithSegment(plain, 0xE1, [.. XmpSignature, .. System.Text.Encoding.UTF8.GetBytes(xmp)]);
        var changed = JpegMetadata.Apply(jpeg, new JpegMetadata.Changes { Caption = "New" });
        var seg = Segments(changed).Single(x => IsXmp(changed, x));
        var text = System.Text.Encoding.UTF8.GetString(changed, seg.Offset + 4 + XmpSignature.Length, seg.Length - 4 - XmpSignature.Length);
        Assert.DoesNotContain(">Old<", text);
        Assert.Contains("xml:lang=\"en-US\">New<", text);
        Assert.Contains("Kona&#xD;", text);
    }

    [Fact]
    public async Task APhotoChangedWhileWaitingToSwapIn_IsntReplacedWithTheOldCopy()
    {
        var path = _lib.AddJpeg("busy.jpg");
        // OneDrive has the photo open (so the swap waits), then writes a newer version.
        var held = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var oneDrive = Task.Run(async () =>
        {
            await Task.Delay(250);
            held.Write(" newer"u8);
            held.Dispose();
        });
        await Assert.ThrowsAsync<IOException>(() => XmpFields.WriteAsync(path, "A dog.", null, null));
        await oneDrive;
        Assert.EndsWith(" newer", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task APhotosCreationTime_SurvivesASave()
    {
        var path = _lib.AddJpeg("dated.jpg");
        var created = new DateTime(2019, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, created);
        Assert.Equal(1, (await MetadataWriter.SetRatingAsync([ItemFor(path)], 4)).Succeeded);
        Assert.Equal(created, File.GetCreationTimeUtc(path));
    }

    [Fact]
    public async Task APngCalledJpg_IsLeftAlone_Saying_Why()
    {
        var path = _lib.AddPng("fake.png");
        var renamed = Path.ChangeExtension(path, ".jpg");
        File.Move(path, renamed);
        var before = File.ReadAllBytes(renamed);
        var result = await MetadataWriter.SetFieldsAsync([MetadataWriter.FieldChanges.Replacing(ItemFor(renamed), "A dog.")]);
        Assert.Equal(0, result.Succeeded);
        Assert.Contains("isn't the kind of file", result.FailureSummary);
        Assert.Equal(before, File.ReadAllBytes(renamed));
    }

    [Fact]
    public async Task ACaptionClearedInTheFile_DoesntMakeSomeonesTitleTheOldCaption()
    {
        var path = _lib.AddJpeg("titled.jpg", new TestLibrary.JpegOptions { Title = "My title" });
        await XmpFields.WriteAsync(path, "", null, null);
        await MetadataWriter.SetCaptionAsync([ItemFor(path)], "New");
        Assert.Equal("New", (await XmpFields.ReadAsync(path)).Caption);
        Assert.Equal("My title", await MetadataReader.ReadTitleAsync(path));
    }

    [Fact]
    public async Task ALongDescription_GoesInAsShortAltTextAndAnExtendedDescription_ClearedWhenNoLongerNeeded()
    {
        var path = _lib.AddJpeg("long.jpg");
        var first = "A golden retriever runs along a sandy beach at sunset with a red ball in its mouth. ";
        var rest = "Waves break behind it and two people walk in the distance near the dunes. The sky is orange and pink, and the low sun makes long shadows across the wet sand near the water's edge.";
        var description = first + rest;
        Assert.Equal(1, (await MetadataWriter.SetFieldsAsync([MetadataWriter.FieldChanges.Replacing(ItemFor(path), description)])).Succeeded);
        var fields = await XmpFields.ReadAsync(path);
        Assert.Equal(FileFields.AltText(description), fields.AltText);
        Assert.True(fields.AltText.Length <= 250);
        Assert.Equal(description.Trim(), fields.ExtendedDescription);

        await MetadataWriter.SetFieldsAsync([MetadataWriter.FieldChanges.Replacing(ItemFor(path), "A dog.")]);
        fields = await XmpFields.ReadAsync(path);
        Assert.Equal("A dog.", fields.AltText);
        Assert.Equal("", fields.ExtendedDescription);
    }

    [Fact]
    public async Task ACaptionKeptTheOldWay_InTheTitle_IsFollowedByTheTitleWhenReplaced()
    {
        // Photo Finder before #62, and Explorer, kept the caption in the Title (EXIF and XP title too).
        var path = _lib.AddJpeg("old.jpg", new TestLibrary.JpegOptions { Title = "The old caption" });
        Assert.Equal(1, (await MetadataWriter.SetCaptionAsync([ItemFor(path)], "The new caption")).Succeeded);
        Assert.Equal("The new caption", (await XmpFields.ReadAsync(path)).Caption);
        Assert.Equal("The new caption", await MetadataReader.ReadTitleAsync(path));
    }

    [Fact]
    public async Task FieldsThatCantBeReadNow_AreAnError_WhenDecidingWhatToWrite()
    {
        var path = _lib.AddJpeg("locked.jpg");
        await XmpFields.WriteAsync(path, null, "Another app's alt text.", null);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAnyAsync<Exception>(() => MetadataReader.ReadAsync(path, forWriting: true));
        Assert.Equal("Another app's alt text.", (await MetadataReader.ReadAsync(path, forWriting: true)).AltText);
    }


    [Fact]
    public async Task EditingACaption_KeepsExplorersTitleInStep_UnlessSomeoneWroteADifferentTitle()
    {
        var plain = _lib.AddJpeg("plain.jpg");
        Assert.Equal(1, (await MetadataWriter.SetCaptionAsync([ItemFor(plain)], "First")).Succeeded);
        Assert.Equal("First", (await XmpFields.ReadAsync(plain)).Caption);
        Assert.Equal("First", await MetadataReader.ReadTitleAsync(plain));
        await MetadataWriter.SetCaptionAsync([ItemFor(plain)], "Second");
        Assert.Equal("Second", await MetadataReader.ReadTitleAsync(plain));

        var titled = _lib.AddJpeg("titled.jpg", new TestLibrary.JpegOptions { Title = "A title of my own" });
        await XmpFields.WriteAsync(titled, "An old caption", null, null);
        await MetadataWriter.SetCaptionAsync([ItemFor(titled)], "A new caption");
        Assert.Equal("A new caption", (await XmpFields.ReadAsync(titled)).Caption);
        Assert.Equal("A title of my own", await MetadataReader.ReadTitleAsync(titled));
    }

    [Fact]
    public void OnlyJpegAndTiff_HoldTheFields()
    {
        Assert.True(XmpFields.CanWrite(@"C:\a.JPG"));
        Assert.True(XmpFields.CanWrite(@"C:\a.tiff"));
        Assert.False(XmpFields.CanWrite(@"C:\a.png"));
        Assert.False(XmpFields.CanWrite(@"C:\a.heic"));
        Assert.False(XmpFields.CanWrite(@"C:\a.mp4"));
    }

    private static Models.MediaItem ItemFor(string path)
    {
        var info = new FileInfo(path);
        return new Models.MediaItem(info.FullName, info.Length, info.CreationTime, info.LastWriteTime);
    }
}
