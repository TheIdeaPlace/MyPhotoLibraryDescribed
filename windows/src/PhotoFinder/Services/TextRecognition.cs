using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PhotoFinder.Services;

/// <summary>Reads the text in a photo with Windows' built-in OCR, which every Windows 10 and 11 PC has
/// (in the languages installed for the person), and which needs no package identity or NPU.</summary>
public static class TextRecognition
{
    public static bool IsAvailable => OcrEngine.AvailableRecognizerLanguages.Count > 0;

    /// <summary>The lines of text found, top to bottom.</summary>
    public static async Task<IReadOnlyList<string>> ReadLinesAsync(string path)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("Windows has no text recognition for your languages. Add a language with its Optical character recognition feature in Settings, Time & language.");
        using var bitmap = await ImageDecoding.DecodeAsync(path, Math.Min(OcrEngine.MaxImageDimension, 4096u));
        var result = await engine.RecognizeAsync(bitmap);
        return result.Lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0).ToList();
    }
}
