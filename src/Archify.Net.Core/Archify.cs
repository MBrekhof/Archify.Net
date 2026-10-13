using System.Text.Json.Nodes;

namespace Archify.Net;

// One typed renderer: render-<type>.mjs's module body from loadDiagram to writeDiagram.
internal interface IDiagramRenderer
{
    (string Svg, JsonArray? Cards, LayoutReport? Layout) Render(RenderContext ctx);
}

internal static class Renderers
{
    public static IDiagramRenderer For(DiagramType type) => type switch
    {
        DiagramType.Architecture => new ArchitectureRenderer(),
        _ => throw new NotSupportedException($"{type.Name()} renderer lands in phase 2–4"),
    };
}

public static class Archify
{
    public static RenderResult Render(DiagramType type, JsonNode diagram, RenderOptions? options = null)
    {
        // Ruling 3: a non-object root reports input/json-parse the way DiagramLoader.Parse does, never throws.
        if (diagram is not JsonObject)
        {
            var failure = DiagramLoader.ParseFailure("the diagram root must be an object");
            return new RenderResult("", "", "", failure.Diagnostics, null);
        }
        var root = DiagramLoader.Normalize(diagram.DeepClone().AsObject());
        var ctx = new RenderContext(type, root, options ?? new RenderOptions());
        try
        {
            DiagramLoader.Load(type, root, ctx);                                   // cli.mjs loadDiagram gates
            ctx.Brands.PrepareDiagramBrandMarks(type, root, ctx.Diagnostics);      // loadDiagramWithBrandMarks
            var renderer = Renderers.For(type);                                    // phase 1: Architecture only
            var (svg, cards, layout) = renderer.Render(ctx);
            var meta = root["meta"]!.AsObject();
            var cardsHtml = Utils.RenderCards(cards);
            var html = TemplateWriter.Apply(Resources.Text("assets/template.html"), meta.Str("title")!, meta.Str("subtitle"), svg, cardsHtml,
                meta.Str("locale"), string.IsNullOrEmpty(meta.Str("visual_preset")) ? "classic" : meta.Str("visual_preset")!, ctx.SourceEvidence, ctx.Locale);
            return new RenderResult(html, svg, cardsHtml, ctx.Diagnostics.Recorded, layout);
        }
        catch (ArchifyDiagnosticException ex)
        {
            var (_, diagnostics) = ctx.Diagnostics.RendererFailure(ex);
            return new RenderResult("", "", "", diagnostics, null);
        }
    }

    // scripts/check-render-output.mjs on an artifact's HTML: the report minus `file`.
    public static CheckResult Check(string html, CheckOptions? options = null)
    {
        if (options?.RequireProvenance == true) throw new NotSupportedException("provenance sidecar checks land in phase 5");
        var report = ArtifactChecker.Check(html);
        return new CheckResult(report["ok"]!.GetValue<bool>(), report);
    }
}
