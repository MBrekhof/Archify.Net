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
    public RenderOptions Options { get; } = options;
    public JsonNode? SourceEvidence { get; }   // verifyRepositoryEvidence's result; null until phase 4 ports repository evidence
}
