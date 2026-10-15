using System.Text.Json.Nodes;
using Xunit;
namespace Archify.Net.Tests;

public class GeometryTests
{
    static readonly JsonObject Fx = (JsonObject)JsonNode.Parse(File.ReadAllText(Fixtures.Fixture("js/geometry.json")))!;
    static Rect R(JsonNode n) => new(n["x"]!.D(), n["y"]!.D(), n["width"]!.D(), n["height"]!.D());
    static Pt P(JsonNode n) => new(n[0]!.D(), n[1]!.D());
    static List<Pt> Poly(JsonNode n) => n.AsArray().Select(p => P(p!)).ToList();

    static Segment Seg(JsonNode n) => new(P(n["start"]!), P(n["end"]!));

    [Fact] public void RectsOverlap() { foreach (var c in Fx["rectsOverlap"]!.AsArray()) Assert.Equal(c![3]!.GetValue<bool>(), Geometry.RectsOverlap(R(c[0]!), R(c[1]!), c[2]!.D())); }
    [Fact] public void SegmentIntersectsRect() { foreach (var c in Fx["segmentIntersectsRect"]!.AsArray()) Assert.Equal(c![3]!.GetValue<bool>(), Geometry.SegmentIntersectsRect(Seg(c[0]!), R(c[1]!), c[2]!.D())); }
    [Fact] public void SegmentRectClearance() { foreach (var c in Fx["segmentRectClearance"]!.AsArray()) Assert.Equal(c![2]!.D(), Geometry.SegmentRectClearance(Seg(c[0]!), R(c[1]!))!.Value, 12); }   // double? per the brief; xUnit's precision overload takes double
    [Fact] public void PolylinePath_bytes() { foreach (var c in Fx["polylinePath"]!.AsArray()) Assert.Equal(c![1]!.GetValue<string>(), Geometry.PolylinePath(Poly(c[0]!))); }
    [Fact] public void RoundedPath_bytes() { foreach (var c in Fx["roundedPath"]!.AsArray()) Assert.Equal(c![2]!.GetValue<string>(), Geometry.RoundedPath(Poly(c[0]!), c[1]!.D())); }
    [Fact] public void NormalizeRoutePoints() { foreach (var c in Fx["normalizeRoutePoints"]!.AsArray()) Assert.Equal(Js.Stringify(c![1]), Js.Stringify(Geometry.NormalizeRoutePoints(Poly(c[0]!)).ToJson())); }
    [Fact] public void JoinRoutePoints_output_is_a_fixed_point_of_NormalizeRoutePoints()
    {   // makes upstream's identity memo (geometry.mjs:1327-1374) unobservable: normalising a joined route never changes it
        Pt s = new(0, 0), e = new(100, 100);
        var cases = new (Pt Start, Pt[]? Via, Pt End)[]
        {
            (s, null, e),                                              // straight, no via
            (s, [new(100, 0)], e),                                     // one bend
            (s, [new(50, 0), new(100, 0)], e),                         // collinear middle point, popped
            (s, [s], e),                                               // via duplicates the start
            (s, [e], e),                                               // via duplicates the end
            (s, [new(0, 40), new(60, 40), new(60, 100)], e),           // two-bend corridor
        };
        foreach (var (start, via, end) in cases)
        {
            var joined = Geometry.JoinRoutePoints(start, via, end);
            Assert.Equal(joined, Geometry.NormalizeRoutePoints(joined));
        }
    }
    [Fact] public void RoutePointsValue() { foreach (var c in Fx["routePointsValue"]!.AsArray()) Assert.Equal(c![1]!.GetValue<string>(), Geometry.RoutePointsValue(Poly(c[0]!))); }
    [Fact] public void LabelPoint_matches_upstream_rules()
    {   // geometry.mjs:1868-1880, ported literally; no fixture needed, the arithmetic is the spec
        var two = new List<Pt> { new(0, 0), new(100, 50) };
        Assert.Equal(new Pt(50, -10), Geometry.LabelPoint(new JsonObject(), two));
        Assert.Equal(new Pt(53, -6), Geometry.LabelPoint(new JsonObject { ["labelDx"] = 3, ["labelDy"] = 4 }, two));
        var three = new List<Pt> { new(0, 0), new(100, 0), new(100, 100) };
        Assert.Equal(new Pt(100, 40), Geometry.LabelPoint(new JsonObject(), three));                      // labelSegment defaults to 1
        Assert.Equal(new Pt(50, -10), Geometry.LabelPoint(new JsonObject { ["labelSegment"] = 0 }, three));
        Assert.Equal(new Pt(7, 8), Geometry.LabelPoint(new JsonObject { ["labelAt"] = new JsonArray(7, 8) }, three));
    }
    [Fact] public void TextFit()
    {
        foreach (var c in Fx["fittedNodeFontSize"]!.AsArray())
        {
            var t = c![0]!.GetValue<string>();
            // Namespace-qualified: this test method's name shadows the class inside GeometryTests.
            Assert.Equal(c[1]!.D(), Archify.Net.TextFit.FittedNodeFontSize(t, 120, 9, 6));
            Assert.Equal(c[2]!.D(), Archify.Net.TextFit.MinimumNodeTextWidth(t, 6));
            Assert.Equal(c[3]!.D(), Archify.Net.TextFit.AvailableNodeTextWidth(120));
        }
    }
}

// Upstream returns routes as [[x, y], ...]; the fixture stores that shape, so the test compares through it.
static class PtJson
{
    public static JsonArray ToJson(this List<Pt> points) => [.. points.Select(p => (JsonNode?)new JsonArray(p.X, p.Y))];
}
