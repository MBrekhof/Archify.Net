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

    // Erd failure receipts (Task 1's capture, from the orders example with one fault each): validateEr's typed codes matched to
    // their problems by message, the untyped layout/constraint problems, the shared gates and the legend thrown from renderSvg —
    // messages, subjects, evidence, fixes, order. erd-domain-scattered is the card's erd/domain-not-drawn code test.
    [Theory]
    [InlineData("erd-domain-scattered")]
    [InlineData("erd-domain-pos")]
    [InlineData("erd-domain-off-canvas")]
    [InlineData("erd-domain-band-covers")]
    [InlineData("erd-header-capacity")]
    [InlineData("erd-attribute-capacity")]
    [InlineData("erd-marker-capacity")]
    [InlineData("erd-duplicate-entity")]
    [InlineData("erd-self-loop")]
    [InlineData("erd-legend-overflow")]
    [InlineData("erd-unplaced-unlabelled")]   // P2: NaN geometry without a crash, pinned in full
    // TEST-003: the validateEr branches (survey-test3 §2-§3); erd-width-infinite pins the schema gate, the renderer check is dead upstream.
    [InlineData("erd-duplicate-attribute")]
    [InlineData("erd-unknown-source")]
    [InlineData("erd-unknown-target")]
    [InlineData("erd-ref-unknown-entity")]
    [InlineData("erd-ref-unknown-attribute")]
    [InlineData("erd-too-short")]
    [InlineData("erd-entity-overlap")]
    [InlineData("erd-label-over-entity")]
    [InlineData("erd-width-infinite")]
    public void Erd_fault_diagnostics_match_upstream_receipt(string name)
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.erd.json")));
        var expected = JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture($"diagrams/{name}.erd.expected.json")))!;
        ParityTests.AssertReceipt(expected, ArchifyNet.Render(DiagramType.Erd, d));
    }

    // P2: upstream crashes with a JS TypeError inside labelPoint; the port reports the same code, but its evidence names the
    // .NET exception type (a stated deviation), so only the code is asserted.
    [Fact] public void Erd_unplaced_labelled_entity_is_internal_unclassified()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/erd-unplaced-labelled.erd.json")));
        var r = ArchifyNet.Render(DiagramType.Erd, d);
        Assert.Equal("", r.Html);
        Assert.Equal(["internal/unclassified"], r.Diagnostics.Select(x => x.Code));
    }

    // Review Focus 5 (P1): the Showcase option pins gateProfile for the four profile-taking gates, as ARCHIFY_QUALITY_PROFILE does.
    [Fact] public void Erd_quality_option_reaches_the_gates_like_the_env_var()
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/orders.crossing-standard.erd.json")));
        Assert.Equal(Fixtures.Lf(Fixtures.Golden("orders.crossing-standard-rendered.html")), ArchifyNet.Render(DiagramType.Erd, d).Html);
        var r = ArchifyNet.Render(DiagramType.Erd, d, new RenderOptions(Quality: QualityProfile.Showcase));
        Assert.Equal("", r.Html);
        ParityTests.AssertReceipt(JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture("diagrams/erd-crossing-showcase.erd.expected.json")))!, r);
    }
}
