namespace PhotoFinder.Models;

/// <summary>Photo > Save Descriptions as Captions (#30): whether a new description is also written into the photo's
/// caption, the file's Title. Off unless chosen: a caption is the person's own words. Same choices as the Mac.</summary>
public enum CaptionPolicy { Never, WhenEmpty, Always }

public static class CaptionPolicies
{
    public static readonly (CaptionPolicy Policy, string Title, string AccessKeyTitle)[] All =
    [
        (CaptionPolicy.Never, "Never", "_Never"),
        (CaptionPolicy.WhenEmpty, "Only When There's No Caption", "_Only When There's No Caption"),
        (CaptionPolicy.Always, "Always", "_Always"),
    ];

    public static string Title(this CaptionPolicy policy) => All.First(p => p.Policy == policy).Title;

    /// <summary>Whether a new description goes into a photo whose caption is <paramref name="existing"/>.</summary>
    public static bool ShouldWrite(this CaptionPolicy policy, string description, string existing)
    {
        existing = existing.Trim();
        description = description.Trim();
        if (description.Length == 0 || existing == description) return false;
        return policy switch
        {
            CaptionPolicy.WhenEmpty => existing.Length == 0,
            CaptionPolicy.Always => true,
            _ => false,
        };
    }
}
