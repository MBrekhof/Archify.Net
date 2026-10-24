namespace Archify.Net;

/// <summary><see cref="ArchifyNet.RenderExplainer"/>'s options. InputPath names the draft (relative resources resolve beside
/// it); PageDirectory is where the page will live, which local paths inside it are written relative to (default: the draft's
/// directory). WritingMode is the writing check mode: warn, strict or off.</summary>
public sealed record ExplainerOptions(string InputPath, QualityProfile? Quality = null, string? RepoRoot = null, string WritingMode = "warn",
    string? PageDirectory = null);
