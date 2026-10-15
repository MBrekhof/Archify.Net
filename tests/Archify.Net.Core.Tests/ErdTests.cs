using System.Text.Json.Nodes;
using Xunit;

namespace Archify.Net.Tests;

public class ErdTests
{
    // Every `tests/fixtures/erd/<name>.layout.json` (Task 1's capture); orders and subscription-billing read upstream's examples.
    public static TheoryData<string> LayoutReports()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(Fixtures.Fixture("erd"), "*.layout.json").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileName(path)[..^".layout.json".Length]);
        return data;
    }

    static string InputPath(string name) => name is "orders" or "subscription-billing"
        ? Fixtures.Upstream($"examples/{name}.erd.json")
        : Fixtures.Fixture($"diagrams/{name}.erd.json");

    // The facade's pipeline up to the renderer (ArchifyNet.Render): normalise, load gates, brand marks, then the scene.
    static JsonObject LayoutReportOf(string inputPath)
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(inputPath));
        var root = DiagramLoader.Normalize(d.DeepClone().AsObject());
        var ctx = new RenderContext(DiagramType.Erd, root, new RenderOptions());
        DiagramLoader.Load(DiagramType.Erd, root, ctx);
        ctx.Brands.PrepareDiagramBrandMarks(DiagramType.Erd, root, ctx.Diagnostics);
        return ErdRenderer.BuildLayoutReport(ctx);
    }

    // `node render-erd.mjs <in> <out> --layout-json` stdout: the same keys in the same order, the same numbers.
    [Theory]
    [MemberData(nameof(LayoutReports))]
    public void Erd_layout_report_equals_upstream_layout_json(string name)
    {
        Assert.Equal(Fixtures.Lf(Fixtures.Fixture($"erd/{name}.layout.json")), Js.Stringify(LayoutReportOf(InputPath(name)), 2) + "\n");
    }
}
