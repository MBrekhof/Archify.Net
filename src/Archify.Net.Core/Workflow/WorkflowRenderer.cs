using System.Text.Json.Nodes;

namespace Archify.Net;

// render-workflow.mjs. The layout report is the compiler's receipt (R5): `--layout-json` prints exactly that.
public sealed class WorkflowRenderer : IDiagramRenderer
{
    public (string Svg, JsonArray? Cards, LayoutReport? Layout) Render(RenderContext ctx)
    {
        var compiled = Compile(ctx);
        // :35-36 — a failed compile re-records its diagnostics and throws; a successful one publishes none (R11).
        if (!compiled.Ok) throw ctx.Diagnostics.ThrowDiagnosticError(string.IsNullOrEmpty(compiled.Error) ? "Workflow compilation failed." : compiled.Error, compiled.Diagnostics);
        return (compiled.Svg!, ctx.Diagram.Arr("cards"), new LayoutReport(Receipt(compiled)));
    }

    // :32-34 — layout mode prints the receipt whether or not the compile succeeded, and records nothing.
    internal static LayoutOutcome LayoutMode(RenderContext ctx)
    {
        var compiled = Compile(ctx);
        return compiled.Ok
            ? new(true, Receipt(compiled), null, ctx.Diagnostics.Recorded)
            : new(false, Receipt(compiled), string.IsNullOrEmpty(compiled.Error) ? "Workflow compilation failed." : compiled.Error, compiled.Diagnostics);
    }

    static WorkflowCompileResult Compile(RenderContext ctx)
    {
        // `process.env.ARCHIFY_QUALITY_PROFILE || workflow.meta?.quality_profile` (R4).
        var qualityProfile = ctx.Options.Quality?.Name() ?? ctx.Meta!.Str("quality_profile");
        try
        {
            return ctx.Diagnostics.WithIsolatedRecording(() =>
                WorkflowCompiler.Compile(ctx, WorkflowDoc.From(ctx.Diagram), qualityProfile, ctx.SourceEvidence as JsonObject));
        }
        catch (ArchifyDiagnosticException error)
        {
            // :21-28 — the scope has restored the caller's recording; keep a typed escape visible.
            foreach (var diagnostic in error.Diagnostics) ctx.Diagnostics.Record(diagnostic);
            throw;
        }
    }

    // Upstream's receipt holds the result's diagnostics array by reference and the repair wrappers edit it in place, so the
    // port fills `diagnostics` from the final result here. A failed measured receipt already has the key (before `ok`).
    static JsonObject Receipt(WorkflowCompileResult compiled)
    {
        var report = compiled.Receipt.DeepClone().AsObject();
        report["diagnostics"] = new JsonArray([.. compiled.Diagnostics.Select(d => (JsonNode?)d.ToJson())]);
        return report;
    }
}
