using System.Text.RegularExpressions;

namespace PhotoFinder.Services;

/// <summary>
/// What a description puts in a file's fields (#31, #62), the same as the Mac's FileMetadataWriter.swift: the caption is
/// XMP's dc:description, Alt Text (Accessibility) is a short version, and Extended Description (Accessibility) is the
/// whole description when the alt text had to be shortened.
/// </summary>
public static partial class FileFields
{
    /// <summary>IPTC's guidance for Alt Text (Accessibility): 250 characters or fewer.</summary>
    public const int AltTextLimit = 250;

    /// <summary>The description when it fits in 250 characters, otherwise the whole sentences from its start that fit.
    /// When even the first sentence is longer, it's cut at a word boundary and ends with "…".</summary>
    public static string AltText(string description)
    {
        var text = description.Trim();
        if (text.Length <= AltTextLimit) return text;
        var fitting = "";
        foreach (Match end in SentenceEnd().Matches(text))
        {
            var candidate = text[..(end.Index + end.Length)].Trim();
            if (candidate.Length <= AltTextLimit) fitting = candidate;
            else break;
        }
        if (fitting.Length > 0) return fitting;

        // One long first sentence: as many whole words as fit with the "…".
        var limit = AltTextLimit - 1;
        var cut = text[..limit];
        if (!char.IsWhiteSpace(text[limit]) && cut.LastIndexOf(' ') is var space and > 0) cut = cut[..space];
        // No comma or dash left hanging before the "…".
        cut = cut.TrimEnd(' ', '\t', '\r', '\n', ',', ';', ':', '-', '–', '—');
        return cut + "…";
    }

    /// <summary>The whole description, only when the alt text had to be shortened; otherwise null.</summary>
    public static string? ExtendedDescription(string description)
    {
        var text = description.Trim();
        return AltText(text) == text ? null : text;
    }

    /// <summary>The fields holding something else now (a caption, and alt text), each with what it holds, for "Replace
    /// the caption and alt text of name?". Empty fields and fields already holding what would be written aren't counted.</summary>
    public static List<(string Field, string Current)> Replaced(string caption, string altText, string description)
    {
        var fields = new List<(string, string)>();
        caption = caption.Trim();
        altText = altText.Trim();
        description = description.Trim();
        if (caption.Length > 0 && caption != description) fields.Add(("caption", caption));
        if (altText.Length > 0 && altText != AltText(description)) fields.Add(("alt text", altText));
        return fields;
    }

    /// <summary>Whether Explorer's Title follows the caption: when it's empty, or the same as the caption being replaced,
    /// so it was the caption all along. A title someone wrote is left alone.</summary>
    public static bool TitleKeepsInStep(string title, string oldCaption) => title.Trim().Length == 0 || title.Trim() == oldCaption.Trim();

    // A sentence ends at ., ! or ? (with any closing quotes or brackets) followed by space or the end.
    [GeneratedRegex(@"[.!?]+[""'”’)\]]*(?=\s|$)")]
    private static partial Regex SentenceEnd();
}
