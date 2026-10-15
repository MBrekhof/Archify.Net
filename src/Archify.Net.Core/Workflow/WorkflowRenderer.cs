using System.Text.Json.Nodes;

namespace Archify.Net;

// render-workflow.mjs. The layout report is the compiler's receipt (R5): `--layout-json` prints exactly that.
public sealed class WorkflowRenderer : IDiagramRenderer
{
    public (string Svg, JsonArray? Cards, LayoutReport? Layout) Render(RenderContext ctx)
    {
        var meta = ctx.Meta!;
        // `process.env.ARCHIFY_QUALITY_PROFILE || workflow.meta?.quality_profile` (R4).
        var qualityProfile = ctx.Options.Quality?.Name() ?? meta.Str("quality_profile");
        WorkflowCompileResult compiled;
        try
        {
            compiled = ctx.Diagnostics.WithIsolatedRecording(() =>
                WorkflowCompiler.Compile(ctx, WorkflowDoc.From(ctx.Diagram), qualityProfile, ctx.SourceEvidence as JsonObject));
        }
        catch (ArchifyDiagnosticException error)
        {
            // :21-28 — the scope has restored the caller's recording; keep a typed escape visible.
            foreach (var diagnostic in error.Diagnostics) ctx.Diagnostics.Record(diagnostic);
            throw;
        }
        // :35-36 — a failed compile re-records its diagnostics and throws; a successful one publishes none (R11).
        if (!compiled.Ok) throw ctx.Diagnostics.ThrowDiagnosticError(string.IsNullOrEmpty(compiled.Error) ? "Workflow compilation failed." : compiled.Error, compiled.Diagnostics);
        var report = compiled.Receipt.DeepClone().AsObject();
        report["diagnostics"] = new JsonArray([.. compiled.Diagnostics.Select(d => (JsonNode?)new JsonObject
        {
            ["code"] = d.Code, ["severity"] = d.Severity == Severity.Error ? "error" : "warning", ["message"] = d.Message,
            ["subject"] = d.Subject.DeepClone(), ["evidence"] = d.Evidence.DeepClone(), ["supportedFixes"] = new JsonArray([.. d.SupportedFixes.Select(f => (JsonNode?)f)]),
        })]);
        return (compiled.Svg!, ctx.Diagram.Arr("cards"), new LayoutReport(report));
    }
}
