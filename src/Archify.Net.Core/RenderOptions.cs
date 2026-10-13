namespace Archify.Net;

public enum QualityProfile { Standard, Showcase }

public sealed record RenderOptions(QualityProfile? Quality = null, string? RepoRoot = null);
