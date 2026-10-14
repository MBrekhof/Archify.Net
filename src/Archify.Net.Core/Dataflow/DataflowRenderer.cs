using System.Text.Json.Nodes;

namespace Archify.Net;

// render-dataflow.mjs. No layout report: dataflow has no --layout-json.
public sealed class DataflowRenderer : IDiagramRenderer
{
    public (string Svg, JsonArray? Cards, LayoutReport? Layout) Render(RenderContext ctx)
    {
        // `process.env.ARCHIFY_QUALITY_PROFILE || dataflow.meta?.quality_profile` (R4): one resolved value for automaticShowcase and the gates.
        var qualityProfile = ctx.Options.Quality?.Name() ?? ctx.Meta.Str("quality_profile");
        string svg;
        try { svg = new DataflowCompiler(ctx, ctx.Diagram, qualityProfile).Compile(); }
        catch (ArchifyDiagnosticException error)
        {
            // :19 — the attempt's diagnostics were recorded under isolation; publish them before the facade's rendererFailure reads the sink.
            foreach (var diagnostic in error.Diagnostics) ctx.Diagnostics.Record(diagnostic);
            throw;
        }
        return (svg, ctx.Diagram.Arr("cards"), null);
    }
}
