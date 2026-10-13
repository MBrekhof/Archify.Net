namespace Archify.Net;

public enum QualityProfile { Standard, Showcase }

public sealed record RenderOptions(QualityProfile? Quality = null, string? RepoRoot = null);

public static class QualityProfiles
{
    // The string upstream reads from ARCHIFY_QUALITY_PROFILE / meta.quality_profile (mirrors DiagramType.Name()).
    public static string Name(this QualityProfile p) => p.ToString().ToLowerInvariant();
}
