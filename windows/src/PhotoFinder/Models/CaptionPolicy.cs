namespace PhotoFinder.Models;

/// <summary>Whether a new description is also written into a photo's caption (#30). Since #62 the person chooses only
/// on or off (Save Descriptions in Photos), which means WhenEmpty; the other values remain for reading old settings.</summary>
public enum CaptionPolicy { Never, WhenEmpty, Always }

public static class CaptionPolicies
{
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
