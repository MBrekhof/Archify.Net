using System.Text.Json.Nodes;

namespace Archify.Net;

// Per-render state that upstream keeps in module globals and process.env.
public sealed class RenderContext(DiagramType type, JsonObject diagram, RenderOptions options)
{
    public DiagramType Type { get; } = type;
    public JsonObject Diagram { get; } = diagram;
    public JsonObject? Meta => Diagram["meta"] as JsonObject;
    public DiagnosticSink Diagnostics { get; } = new();
    public LocaleCatalog Locale { get; } = new();
    public BrandMarkResolver Brands { get; } = new();
    public RenderOptions Options { get; } = options;
    public JsonNode? SourceEvidence { get; internal set; }   // verifyRepositoryEvidence's result, set by DiagramLoader.Load
    internal GitRunner Git { get; init; } = new();
}
