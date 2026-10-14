using System.Text.Json.Nodes;
using Xunit;

namespace Archify.Net.Tests;

// label-capacity.mjs and joint-label-routing.mjs against Node (tools/capture-fixtures.mjs). Numbers compare as Js.Num strings;
// `same` flags pin object identity (R9).
public class DataflowHelperTests
{
    static JsonNode Load(string name) => JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture($"js/{name}")))!;
    static readonly JsonNode Capacity = Load("label-capacity.json");
    static readonly JsonArray Joint = Load("joint-label-routing.json").AsArray();

    static string N(JsonNode? n) => n is null ? "undefined" : Js.Num(n.D());
    static string N(double? v) => v is { } d ? Js.Num(d) : "undefined";
    static List<Pt> Points(JsonNode? n) => [.. n!.AsArray().Select(p => new Pt(p![0]!.D(), p[1]!.D()))];
    static string PointsKey(IEnumerable<Pt> points) => string.Join(";", points.Select(p => $"{Js.Num(p.X)},{Js.Num(p.Y)}"));
    static Rect RectOf(JsonNode n) => new(n["x"]!.D(), n["y"]!.D(), n["width"]!.D(), n["height"]!.D());

    static LabelRect Label(JsonNode n, JsonObject relation) => new()
    {
        Relation = relation, RelationIndex = (int)n["relationIndex"]!.D(), Label = n["label"]?.GetValue<string>(),
        X = n["x"]!.D(), Y = n["y"]!.D(), Width = n["width"]!.D(), Height = n["height"]!.D(),
        Lx = n["lx"]?.D(), Ly = n["ly"]?.D(), Lines = n["lines"] is JsonArray lines ? [.. lines.Select(l => l!.GetValue<string>())] : null,
    };
    static string LabelKey(JsonNode n) => string.Join("|", N(n["relationIndex"]), n["label"]?.GetValue<string>() ?? "undefined",
        N(n["x"]), N(n["y"]), N(n["width"]), N(n["height"]), N(n["lx"]), N(n["ly"]),
        n["lines"] is JsonArray lines ? string.Join("\n", lines.Select(l => l!.GetValue<string>())) : "no-lines", n["relation"]?["id"]?.GetValue<string>() ?? "no-id");
    static string LabelKey(LabelRect r) => string.Join("|", N(r.RelationIndex), r.Label ?? "undefined",
        N(r.X), N(r.Y), N(r.Width), N(r.Height), N(r.Lx), N(r.Ly),
        r.Lines is { } lines ? string.Join("\n", lines) : "no-lines", r.Relation.Str("id") ?? "no-id");

    // ---- label-capacity.mjs --------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> SizeRows() => Capacity["sizes"]!.AsArray().Select((_, i) => new object[] { i });

    [Theory, MemberData(nameof(SizeRows))]
    public void FlowLabelSize_matches_Node(int row)
    {
        var r = Capacity["sizes"]![row]!;
        var flow = r["flow"]!.AsObject();
        var size = r["lines"] is JsonArray lines
            ? LabelCapacity.FlowLabelSize(flow, [.. lines.Select(l => l!.GetValue<string>())])
            : LabelCapacity.FlowLabelSize(flow);
        Assert.Equal($"{N(r["size"]!["width"])}x{N(r["size"]!["height"])}", $"{Js.Num(size.Width)}x{Js.Num(size.Height)}");
        Assert.Equal(N(Capacity["flowLabelHeight"]), Js.Num(LabelCapacity.FlowLabelHeight));
    }

    public static IEnumerable<object[]> SceneRows() => Capacity["scenes"]!.AsArray().Select(s => new object[] { s!["name"]!.GetValue<string>() });

    [Theory, MemberData(nameof(SceneRows))]
    public void WrapBlockedFlowLabels_matches_Node(string name)
    {
        var scene = Capacity["scenes"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == name)!;
        var input = scene["input"]!;
        var labels = input["labels"]!.AsArray().Select(l => Label(l!, l!["relation"]!.DeepClone().AsObject())).ToList();
        var components = input["components"]!.AsArray()
            .Select((c, i) => new Box { Id = $"c{i}", Source = [], X = c!["x"]!.D(), Y = c["y"]!.D(), Width = c["width"]!.D(), Height = c["height"]!.D() }).ToList();
        var result = LabelCapacity.WrapBlockedFlowLabels(
            labels: labels,
            routes: [.. input["routes"]!.AsArray().Select(r => new LabelRoute((int)r!["relationIndex"]!.D(), Points(r["points"])))],
            components: components,
            titles: [.. input["titles"]!.AsArray().Select(t => RectOf(t!))],
            viewBox: [.. input["viewBox"]!.AsArray().Select(v => v!.D())],
            placementBottom: input["placementBottom"]!.D());
        var expected = scene["output"]!.AsArray();
        Assert.Equal(expected.Count, result.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i]!["same"]!.GetValue<bool>(), ReferenceEquals(result[i], labels[i]));
            Assert.Equal(LabelKey(expected[i]!), LabelKey(result[i]));
            Assert.Same(labels[i].Relation, result[i].Relation);   // `{ ...label, ...shape }` keeps the relation object
        }
    }

    // ---- joint-label-routing.mjs ---------------------------------------------------------------------------------------

    static JsonObject Relation(JsonNode n)
    {
        var relation = new JsonObject();
        if (n["id"] is { } id) relation["id"] = id.DeepClone();
        relation["from"] = n["from"]!.DeepClone();
        relation["to"] = n["to"]!.DeepClone();
        foreach (var (key, value) in n["pins"]!.AsObject()) relation[key] = value?.DeepClone();
        return relation;
    }

    public static IEnumerable<object[]> JointRows() => Joint.Select(r => new object[] { r!["name"]!.GetValue<string>() });

    [Theory, MemberData(nameof(JointRows))]
    public void RepairLabelRoutes_matches_Node(string name)
    {
        var row = Joint.Single(r => r!["name"]!.GetValue<string>() == name)!;
        var input = row["input"]!;
        var labels = input["labels"]!.AsArray().Select(l => Label(l!, Relation(l!["relation"]!))).ToList();
        var routes = input["routes"]!.AsArray()
            .Select(r => new RoutedRelation(Points(r!["points"]), Relation(r["relation"]!), (int)r["relationIndex"]!.D())).ToList();
        var sides = new Dictionary<JsonObject, (string?, string?)>(ReferenceEqualityComparer.Instance);
        foreach (var route in routes)
        {
            var side = input["sides"]![Js.Num(route.RelationIndex!.Value)]!;
            sides[route.Relation!] = (side["fromSide"]?.GetValue<string>(), side["toSide"]?.GetValue<string>());
        }
        var frames = input["frames"]!.AsArray().Select(f => new Frame(f!["x"]!.D(), f["y"]!.D(), f["width"]!.D(), f["height"]!.D(),
            f["radius"]!.D(), f["kind"]?.GetValue<string>(), f["label"]?.GetValue<string>(), f["id"]?.DeepClone())).ToList();
        var metrics = new LabelRouteMetrics();

        var (outLabels, outRoutes) = JointLabelRouting.RepairLabelRoutes(
            labels: labels, routes: routes,
            nodes: [.. input["nodes"]!.AsArray().Select(n => new Box { Id = n!["id"]!.GetValue<string>(), Source = [],
                X = n["x"]!.D(), Y = n["y"]!.D(), Width = n["width"]!.D(), Height = n["height"]!.D() })],
            titles: [.. input["titles"]!.AsArray().Select(t => RectOf(t!))],
            frames: frames,
            viewBox: [.. input["viewBox"]!.AsArray().Select(v => v!.D())],
            placementBottom: input["placementBottom"]!.D(),
            sideFor: relation => sides[relation],
            metrics: metrics);

        var output = row["output"]!;
        Assert.Equal(output["labelsSame"]!.GetValue<bool>(), ReferenceEquals(outLabels, labels));
        Assert.Equal(output["routesSame"]!.GetValue<bool>(), ReferenceEquals(outRoutes, routes));
        var m = output["metrics"]!;
        Assert.Equal($"{N(m["searches"])} {N(m["maximumSearches"])} {N(m["changedLabels"])} {N(m["changedRoutes"])}",
            $"{metrics.Searches} {metrics.MaximumSearches} {metrics.ChangedLabels} {metrics.ChangedRoutes}");
        var expectedLabels = output["labels"]!.AsArray();
        Assert.Equal(expectedLabels.Count, outLabels.Count);
        for (var i = 0; i < expectedLabels.Count; i++)
        {
            Assert.Equal(expectedLabels[i]!["same"]!.GetValue<bool>(), ReferenceEquals(outLabels[i], labels[i]));
            Assert.Equal(LabelKey(expectedLabels[i]!), LabelKey(outLabels[i]));
        }
        var expectedRoutes = output["routes"]!.AsArray();
        Assert.Equal(expectedRoutes.Count, outRoutes.Count);
        for (var i = 0; i < expectedRoutes.Count; i++)
        {
            Assert.Equal(expectedRoutes[i]!["same"]!.GetValue<bool>(), ReferenceEquals(outRoutes[i], routes[i]));
            Assert.Equal(N(expectedRoutes[i]!["relationIndex"]), N(outRoutes[i].RelationIndex));
            Assert.Equal(PointsKey(Points(expectedRoutes[i]!["points"])), PointsKey(outRoutes[i].Points!));
            Assert.Same(routes[i].Relation, outRoutes[i].Relation);
        }
    }

    // dataflow-compiler.mjs:1365 adopts a proposal only when `proposal.labels !== resolvedLabelRects`.
    [Fact]
    public void RepairLabelRoutes_returns_the_input_lists_when_nothing_changes()
    {
        var relation = new JsonObject { ["from"] = "a", ["to"] = "b" };
        List<LabelRect> labels = [new() { Relation = relation, RelationIndex = 0, Label = "flow", X = 140, Y = 20, Width = 40, Height = 16, Lx = 160, Ly = 30 }];
        List<RoutedRelation> routes = [new([new(100, 60), new(220, 60)], relation, 0)];
        List<Box> nodes =
        [
            new() { Id = "a", Source = [], X = 40, Y = 40, Width = 60, Height = 40 },
            new() { Id = "b", Source = [], X = 220, Y = 40, Width = 60, Height = 40 },
        ];
        var metrics = new LabelRouteMetrics();
        var (outLabels, outRoutes) = JointLabelRouting.RepairLabelRoutes(labels, routes, nodes, titles: [], frames: [], viewBox: [400, 200],
            placementBottom: 200, sideFor: _ => ("right", "left"), metrics: metrics);
        Assert.Same(labels, outLabels);
        Assert.Same(routes, outRoutes);
        Assert.Equal(0, metrics.Searches);
    }
}
