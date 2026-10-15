using System.Text.Json.Nodes;
using Xunit;

namespace Archify.Net.Tests;

// compileWorkflow(...).receipt for each manifest row (captured by tools/capture-fixtures.mjs into fixtures/workflow/<name>.receipt.json):
// contract, viewBox, requiredViewBox, columns, measured nodes, routed edge points and label anchors, plus the diagnostic codes.
public class WorkflowReceiptTests
{
    static JsonObject WithoutDiagnostics(JsonObject receipt) { var r = (JsonObject)receipt.DeepClone(); r.Remove("diagnostics"); return r; }

    internal static void AssertReceiptEquals(string name, WorkflowCompileResult result)
    {
        var node = JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture($"workflow/{name}.receipt.json")))!.AsObject();
        Assert.Equal(Js.Stringify(WithoutDiagnostics(node), 2), Js.Stringify(WithoutDiagnostics(result.Receipt), 2));
        var codes = node["diagnostics"]!.AsArray().Select(d => d!["code"]!.GetValue<string>()).ToList();
        Assert.Equal(codes, result.Diagnostics.Select(d => d.Code).ToList());
    }

    // Mirrors ArchifyNet.Render (Archify.cs:46-55): normalise, load gates, brand preparation, then the compiler — exactly what
    // render-workflow.mjs hands compileWorkflow after loadDiagramWithBrandMarks.
    internal static WorkflowCompileResult CompileFixture(string inputPath, RenderOptions? options = null)
    {
        var d = DiagramLoader.Parse(Fixtures.Lf(inputPath));
        var root = DiagramLoader.Normalize(d.DeepClone().AsObject());
        var ctx = new RenderContext(DiagramType.Workflow, root, options ?? new RenderOptions());
        DiagramLoader.Load(DiagramType.Workflow, root, ctx);
        ctx.Brands.PrepareDiagramBrandMarks(DiagramType.Workflow, root, ctx.Diagnostics);
        var profile = ctx.Options.Quality?.Name() ?? root["meta"]!["quality_profile"]?.GetValue<string>();
        return WorkflowCompiler.Compile(ctx, WorkflowDoc.From(root), profile, null);
    }

    // Phase 3, task 7: the schema-v1 rows of tests/fixtures/diagrams/phase3-manifest.json.
    [Theory]
    [InlineData("incident-response", "upstream:examples/incident-response.workflow.json")]
    [InlineData("release-delivery", "upstream:examples/release-delivery.workflow.json")]
    [InlineData("border-clearance", "fixture:diagrams/border-clearance.workflow.json")]
    [InlineData("v1-700x400", "fixture:diagrams/v1-700x400.workflow.json")]
    [InlineData("v1-explicit-coordinates", "fixture:diagrams/v1-explicit-coordinates.workflow.json")]
    [InlineData("v1-baseline.agent-tool-call", "fixture:diagrams/v1-baseline.agent-tool-call.workflow.json")]
    // Phase 3, task 8: the readable-v2 automatic rows.
    [InlineData("holiday-planning", "upstream:examples/holiday-planning.workflow.json")]
    [InlineData("dense-automatic", "fixture:diagrams/dense-automatic.workflow.json")]
    [InlineData("fanin-label-recovery", "fixture:diagrams/fanin-label-recovery.workflow.json")]
    [InlineData("free-space", "fixture:diagrams/free-space.workflow.json")]
    [InlineData("return-arrival", "fixture:diagrams/return-arrival.workflow.json")]
    [InlineData("routing-clarity", "fixture:diagrams/routing-clarity.workflow.json")]
    [InlineData("first-draft.agent-tool-call", "fixture:diagrams/first-draft.agent-tool-call.workflow.json")]
    [InlineData("first-draft.automatic-fanout", "fixture:diagrams/first-draft.automatic-fanout.workflow.json")]
    [InlineData("first-draft.automatic-port-slot", "fixture:diagrams/first-draft.automatic-port-slot.workflow.json")]
    [InlineData("first-draft.customer-refund", "fixture:diagrams/first-draft.customer-refund.workflow.json")]
    [InlineData("first-draft.incident-response", "fixture:diagrams/first-draft.incident-response.workflow.json")]
    [InlineData("viewport.order-overflow", "fixture:diagrams/viewport.order-overflow.workflow.json")]
    [InlineData("viewport.order-reflow", "fixture:diagrams/viewport.order-reflow.workflow.json")]
    [InlineData("viewport.three-lane-remainder", "fixture:diagrams/viewport.three-lane-remainder.workflow.json")]
    // Phase 3, task 10: pins, presets and controlled routes.
    [InlineData("agent-tool-call", "upstream:examples/agent-tool-call.workflow.json")]
    [InlineData("route-text-recovery", "fixture:diagrams/route-text-recovery.workflow.json")]
    [InlineData("shared-trunks", "fixture:diagrams/shared-trunks.workflow.json")]
    public void Workflow_receipt_equals_upstream_layout_json(string name, string input)
    {
        var path = input.StartsWith("upstream:") ? Fixtures.Upstream(input["upstream:".Length..]) : Fixtures.Fixture(input["fixture:".Length..]);
        var result = CompileFixture(path);
        Assert.True(result.Ok, result.Error);
        AssertReceiptEquals(name, result);
        // R5: the facade's layout report is the receipt itself, diagnostics included — what `--layout-json` prints (render-workflow.mjs:33).
        var r = ArchifyNet.Render(DiagramType.Workflow, DiagramLoader.Parse(Fixtures.Lf(path)));
        Assert.Equal(Fixtures.Lf(Fixtures.Fixture($"workflow/{name}.receipt.json")), Js.Stringify(r.Layout!.Report, 2) + "\n");
    }
}
