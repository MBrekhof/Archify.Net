using System.Text.Json.Nodes;
using Xunit;

namespace Archify.Net.Tests;

// workflow-compiler.mjs clusters A-D (through a shimmed copy that exports the module-private functions),
// workflow-migration-geometry.mjs and workflow-placement-repair.mjs against Node (tools/capture-fixtures.mjs,
// fixtures/js/workflow-model.json). Every assertion compares Js.Stringify of the C# result with the fixture node.
public class WorkflowModelTests
{
    static readonly JsonNode Fx = JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture("js/workflow-model.json")))!;
    static JsonObject Row(string name) => Fx["rows"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == name)!.AsObject();
    public static IEnumerable<object[]> Rows() => Fx["rows"]!.AsArray().Select(r => new object[] { r!["name"]!.GetValue<string>() });

    static JsonObject Document(JsonObject row) => row["document"] is JsonObject document ? document.DeepClone().AsObject()
        : JsonNode.Parse(Fixtures.Lf(Path.Combine(Fixtures.RepoRoot, row["input"]!.GetValue<string>())))!.AsObject();
    static JsonObject Document(string name) => Document(Row(name));

    static string S(JsonNode? node) => Js.Stringify(node);
    static JsonArray Arr<T>(IEnumerable<T> items, Func<T, JsonNode?> map) => new([.. items.Select(map)]);
    static JsonNode? N(double? value) => value is { } d ? JsonValue.Create(d) : null;
    static JsonArray Strings(IEnumerable<string> items) => Arr(items, s => JsonValue.Create(s));

    static (WorkflowDoc Canonical, WorkflowWidths Widths) Canonical(JsonObject authored)
    {
        var widths = new WorkflowWidths();
        return (WorkflowModel.CanonicalReadableWorkflow(WorkflowDoc.From(authored), widths, null), widths);
    }

    static JsonObject LayoutJson(ReadableLayout layout)
    {
        var json = new JsonObject
        {
            ["contract"] = layout.Contract, ["laneX"] = layout.LaneX, ["laneY"] = layout.LaneY, ["laneW"] = layout.LaneW, ["laneH"] = layout.LaneH,
        };
        if (layout.LaneHeights is { } laneHeights) json["laneHeights"] = Arr(laneHeights, h => h);
        json["laneGap"] = layout.LaneGap;
        json["laneTitleH"] = layout.LaneTitleH;
        if (layout.GroupHeaderHeights is { } headers) json["groupHeaderHeights"] = Arr(headers, h => h);
        if (layout.GroupFooterHeights is { } footers) json["groupFooterHeights"] = Arr(footers, h => h);
        json["colXs"] = Arr(layout.ColXs, x => x);
        json["nodeW"] = layout.NodeW;
        json["nodeH"] = layout.NodeH;
        json["defaultViewBoxWidth"] = layout.DefaultViewBoxWidth;
        if (layout.ChannelLabelEdgeKeys is { } keys) json["channelLabelEdgeKeys"] = Strings(keys);
        if (layout.WidthContributors is { } width) json["widthContributors"] = Strings(width);
        if (layout.HeightContributors is { } height) json["heightContributors"] = Strings(height);
        return json;
    }

    // ---- cluster C: canonical order, the A helpers it feeds, the semantic contract ------------------------------------------

    [Theory, MemberData(nameof(Rows))]
    public void Canonical_matches_Node(string name)
    {
        var (c, widths) = Canonical(Document(name));
        var actual = new JsonObject
        {
            ["lanes"] = Arr(c.Lanes, l => l["id"]?.DeepClone()),
            ["nodes"] = Arr(c.Nodes, n => n["id"]?.DeepClone()),
            ["edges"] = Arr(c.Edges, e => WorkflowModel.StableValueKey(e)),
            ["phases"] = c.Phases is null ? null : Arr(c.Phases, p => p["id"]?.DeepClone()),
            ["groups"] = c.Groups is null ? null : Arr(c.Groups, g => g["id"]?.DeepClone()),
            ["widths"] = Arr(c.Nodes, n => WorkflowModel.AuthoredNodeWidth(n, widths)),
            ["automatic"] = Arr(c.Nodes, n => N(WorkflowModel.AutomaticNodeWidth(n))),
            ["absolutePins"] = WorkflowModel.HasAbsoluteWorkflowPins(c),
            ["verticalStack"] = WorkflowModel.HasVerticalStack(c),
            ["independent"] = WorkflowModel.UsesIndependentLaneMeasurement(c),
            ["preserves"] = WorkflowMigrationGeometry.PreservesWorkflowAbsoluteGeometry(c),
        };
        Assert.Equal(S(Row(name)["canonical"]), S(actual));
    }

    [Theory, MemberData(nameof(Rows))]
    public void SemanticContractDiagnostics_match_Node(string name)
    {
        var (c, _) = Canonical(Document(name));
        var actual = Arr(WorkflowModel.SemanticContractDiagnostics(c), d => new JsonArray(d.Code, d.Path));
        Assert.Equal(S(Row(name)["semantic"]), S(actual));
    }

    [Fact]
    public void CanonicalReadableWorkflow_width_overrides_match_Node()
    {
        var expected = Fx["overrides"]!;
        var overrides = new JsMap<string, double>();
        foreach (var (id, width) in expected["overrides"]!.AsObject()) overrides.Set(id, width!.D());
        var widths = new WorkflowWidths();
        var authored = JsonNode.Parse(Fixtures.Lf(Fixtures.Upstream("examples/holiday-planning.workflow.json")))!.AsObject();
        var c = WorkflowModel.CanonicalReadableWorkflow(WorkflowDoc.From(authored), widths, overrides);
        Assert.Equal(S(expected["widths"]), S(Arr(c.Nodes, n => new JsonArray(n["id"]!.DeepClone(), WorkflowModel.AuthoredNodeWidth(n, widths)))));
    }

    // ---- cluster B: readable rank layout (no feedback, and one rankGapMinimums/laneGapMin feedback object) -----------------

    [Theory, MemberData(nameof(Rows))]
    public void CreateReadableLayout_matches_Node(string name)
    {
        var (c, widths) = Canonical(Document(name));
        var expected = Row(name)["layout"]!;
        Assert.Equal(S(expected["none"]), S(LayoutJson(WorkflowModel.CreateReadableLayout(c, null, widths))));
        Assert.Equal(S(expected["feedback"]), S(LayoutJson(WorkflowModel.CreateReadableLayout(c, Fx["feedback"]!.DeepClone().AsObject(), widths))));
    }

    [Fact]
    public void CreateLegacyLayout_matches_Node() => Assert.Equal(S(Fx["legacyLayout"]), S(LayoutJson(WorkflowModel.CreateLegacyLayout())));

    // ---- cluster D: legend footprint, lane geometry, measured nodes (stored cx/cy, R2), node text problems, step indexes ----

    [Theory, MemberData(nameof(Rows))]
    public void MeasureWorkflowNodes_matches_Node(string name)
    {
        var authored = Document(name);
        var ctx = new RenderContext(DiagramType.Workflow, authored, new RenderOptions());
        ctx.Locale.Register(authored.Obj("meta").Str("locale"), authored.Obj("meta").Obj("translations"));
        var (c, widths) = Canonical(authored);
        var layout = c.SchemaVersion == 2 ? WorkflowModel.CreateReadableLayout(c, null, widths) : WorkflowModel.CreateLegacyLayout();
        var legend = WorkflowScene.ResolveWorkflowLegendFootprint(ctx, c, layout);
        var lanes = WorkflowScene.CreateWorkflowLaneGeometry(ctx, c, layout, legend.LegendExtraHeight, legend.MinimumCanvasWidth);
        var nodes = WorkflowScene.MeasureWorkflowNodes(ctx, c, layout, lanes, widths);
        var actual = new JsonObject
        {
            ["legend"] = new JsonObject
            {
                ["entries"] = Arr(legend.WorkflowLegendEntries, e => new JsonArray(e.Kind, e.Label)),
                ["present"] = Strings(legend.PresentLegendKinds),
                ["packingWidth"] = legend.LegendPackingWidth,
                ["rows"] = legend.PackedLegendFootprint.RowCount,
                ["extraHeight"] = legend.LegendExtraHeight,
                ["minimumCanvasWidth"] = legend.MinimumCanvasWidth,
            },
            ["autoHeight"] = lanes.AutoHeight,
            ["initialViewBox"] = Arr(lanes.InitialViewBox, v => v),
            ["laneTops"] = Arr(c.Lanes, l => lanes.LaneTop(l["id"])),
            ["laneHeights"] = Arr(Enumerable.Range(0, c.Lanes.Count), i => lanes.LaneHeight(i)),
            ["lastLaneBottom"] = lanes.LastLaneBottom(),
            ["legendY"] = lanes.LegendY(),
            ["nodes"] = Arr(nodes.Values, n => new JsonObject
            {
                ["id"] = n.Id, ["x"] = n.X, ["y"] = n.Y, ["width"] = n.Width, ["height"] = n.Height, ["cx"] = n.Cx, ["cy"] = n.Cy,
                ["context"] = lanes.NodeContext(n.Source),
                ["problems"] = Strings(WorkflowScene.WorkflowNodeTextProblems(n.Source, n.Width, n.Brand)),
            }),
        };
        Assert.Equal(S(Row(name)["measured"]), S(actual));
    }

    [Theory, MemberData(nameof(Rows))]
    public void CreateWorkflowStepIndexes_matches_Node(string name)
    {
        var (c, _) = Canonical(Document(name));
        var steps = WorkflowScene.CreateWorkflowStepIndexes(c);
        var actual = new JsonObject
        {
            ["edges"] = Arr(c.Edges, e => steps.EdgeSteps.Get(e)),
            ["nodes"] = Arr(c.Nodes, n => steps.NodeStep(n)),
        };
        Assert.Equal(S(Row(name)["steps"]), S(actual));
    }

    // ---- workflow-migration-geometry.mjs ------------------------------------------------------------------------------------

    public static IEnumerable<object[]> MapperRows() => Fx["mapper"]!.AsArray().Select((_, i) => new object[] { i });

    [Theory, MemberData(nameof(MapperRows))]
    public void CreateHorizontalRankMapper_matches_Node(int index)
    {
        var row = Fx["mapper"]![index]!;
        double[] Columns(string key) => [.. row[key]!.AsArray().Select(v => v!.D())];
        if (row["error"] is { } error)
        {
            var thrown = Assert.Throws<ArgumentException>(() => WorkflowMigrationGeometry.CreateHorizontalRankMapper(Columns("oldColumns"), Columns("newColumns")));
            Assert.Equal(error.GetValue<string>(), thrown.Message);
            return;
        }
        var map = WorkflowMigrationGeometry.CreateHorizontalRankMapper(Columns("oldColumns"), Columns("newColumns"));
        var xs = row["xs"]!.AsArray().Select(x => x?.D() ?? double.NaN);   // `null` stands for a non-finite x
        var actual = Arr(xs, x =>
        {
            try { return map(x); }
            catch (ArgumentException e) { return new JsonObject { ["error"] = e.Message }; }
        });
        Assert.Equal(S(row["outputs"]), S(actual));
    }

    public static IEnumerable<object[]> MigrationRows() => Fx["migration"]!.AsArray().Select(r => new object[] { r!["name"]!.GetValue<string>() });

    [Theory, MemberData(nameof(MigrationRows))]
    public void MigrationGeometry_matches_Node(string name)
    {
        var expected = Fx["migration"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == name)!;
        var doc = WorkflowDoc.From(Document(name));
        Assert.Equal(expected["preserves"]!.GetValue<bool>(), WorkflowMigrationGeometry.PreservesWorkflowAbsoluteGeometry(doc));

        var intrinsic = WorkflowMigrationGeometry.IntrinsicWorkflow(doc);
        Assert.Equal(S(expected["intrinsic"]), S(intrinsic.ToJson()));
        Assert.Equal(expected["intrinsicPolicy"]!.GetValue<bool>(), WorkflowMigrationGeometry.PreservesWorkflowAbsoluteGeometry(intrinsic));
        var planning = WorkflowMigrationGeometry.PlanningWorkflow(doc);
        Assert.Equal(S(expected["planning"]), S(planning.ToJson()));
        Assert.Equal(expected["planningPolicy"]!.GetValue<bool>(), WorkflowMigrationGeometry.PreservesWorkflowAbsoluteGeometry(planning));

        // `createReadableLayout({ ...workflow, schema_version: 2 })` (:1093): never canonicalised, so every automatic width misses.
        var readable = doc.Spread();
        readable.SchemaVersion = 2;
        var columns = WorkflowModel.CreateReadableLayout(readable, null, new WorkflowWidths()).ColXs;
        Assert.Equal(S(expected["columns"]), S(Arr(columns, x => x)));
        var (mapped, changed) = WorkflowMigrationGeometry.CreateMappedWorkflowCandidate(doc, WorkflowModel.LegacyColumnCenters, columns);
        Assert.Equal(S(expected["changedCoordinates"]), S(Arr(changed, c => new JsonObject { ["path"] = c.Path, ["from"] = c.From, ["to"] = c.To })));
        Assert.Equal(S(expected["mapped"]), S(mapped.ToJson()));
        Assert.Equal(S(Document(name)), S(doc.ToJson()));   // the caller's document is never rewritten
    }

    // ---- workflow-placement-repair.mjs --------------------------------------------------------------------------------------

    [Theory, MemberData(nameof(Rows))]
    public void ProposeWorkflowPlacement_matches_Node(string name)
    {
        var expected = Fx["placement"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == name)!["result"];
        var input = WorkflowDoc.From(Document(name));
        var result = WorkflowPlacementRepair.ProposeWorkflowPlacement(input);
        var actual = !result.Eligible
            ? new JsonObject { ["eligible"] = false, ["reason"] = result.Reason }
            : new JsonObject
            {
                ["eligible"] = true,
                ["proposed"] = result.Proposed!.ToJson(),
                ["changes"] = Arr(result.Changes!, c => new JsonObject { ["op"] = c.Op, ["path"] = c.Path, ["value"] = c.Value }),
                ["components"] = Arr(result.Components!, c => new JsonObject { ["ids"] = Strings(c.Ids), ["cyclic"] = c.Cyclic }),
                ["rails"] = new JsonObject { ["left"] = result.LeftRail, ["right"] = result.RightRail },
            };
        Assert.Equal(S(expected), S(actual));
        Assert.Equal(S(Document(name)), S(input.ToJson()));   // structuredClone: the authored input is never applied
    }
}
