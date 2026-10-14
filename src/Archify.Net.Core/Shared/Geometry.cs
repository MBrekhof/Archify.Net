using System.Text;
using System.Text.Json.Nodes;

namespace Archify.Net;

// Geometry helpers shared by all typed renderers. Every function here is
// pure; renderers own their layout tables and pass measured rects
// ({x, y, width, height, cx, cy}) in.
//
// geometry.mjs. Upstream's `[x, y]` is Pt, `{ start, end }` is Segment (never `[p1, p2]`); authored relations stay
// JsonObject so `relation.id`/`relation.from` reads are the same lookups. Functions that call recordDiagnostic take the
// DiagnosticSink first.

public readonly record struct Pt(double X, double Y);
public readonly record struct Segment(Pt Start, Pt End);

public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public static implicit operator Rect(Box b) => new(b.X, b.Y, b.Width, b.Height);
    public static implicit operator Rect(LabelRect l) => new(l.X, l.Y, l.Width, l.Height);
}

// The measured node/boundary rect upstream builds with `{ ...c, x, y, width, height, cx, cy }`. `Source` is the authored
// node; `Brand` is filled from ctx.Brands.BrandMarkFor(Source) at measure time (upstream carries the mark through the
// object spread as a symbol property, so the port carries it explicitly); `Relation` is the payload label rects carry.
public sealed class Box
{
    public required string Id { get; init; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Cx => X + Width / 2;
    public double Cy => Y + Height / 2;
    public required JsonObject Source { get; init; }
    public ResolvedBrand? Brand { get; set; }
    public JsonObject? Relation { get; set; }
}

// A relationship label record: `{ relation, relationIndex, label, x, y, width, height, lx, ly }` (render-architecture.mjs:169-180).
// A class, not a record: the label placer compares placements by reference (`placed[index] !== before`).
public sealed class LabelRect
{
    public required JsonObject Relation { get; init; }
    public int? RelationIndex { get; set; }
    public string? Label { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    // The label anchor. Architecture's label records carry `lx`/`ly`; the sequence renderer's (render-sequence.mjs:374-387)
    // have no such keys, so they stay null and never reach evidence.labelRect.
    public double? Lx { get; set; }
    public double? Ly { get; set; }
    public LabelRect Clone() => (LabelRect)MemberwiseClone();
}

// A structural frame (boundary) whose borders a route may cross but never follow. `Id` is `boundary.id || index`
// upstream: a string or a number.
public sealed record Frame(double X, double Y, double Width, double Height, double Radius = 0, string? Kind = null,
    string? Label = null, JsonNode? Id = null, string? Shape = null, Pt? Start = null, Pt? End = null);

public readonly record struct BorderSegment(string Side, Pt Start, Pt End);

// A routed relationship as the collectors receive it: `{ relation, relationIndex?, points, sourceEndpoint?, targetEndpoint? }`.
// routing.mjs passes `{ points }` alone, so everything but the points is optional.
// `Segments` is the pre-built `routed.segments` list collectBorderRuns prefers over the points (the check script passes one).
public sealed record RoutedRelation(List<Pt>? Points, JsonObject? Relation = null, int? RelationIndex = null,
    bool SourceEndpoint = true, bool TargetEndpoint = true, List<Segment>? Segments = null);

// The same once a collector has resolved the relation and index.
public sealed record Routed(JsonObject Relation, int RelationIndex, List<Pt> Points, bool SourceEndpoint = true, bool TargetEndpoint = true);

public sealed record EndpointSideIssue(string Endpoint, string Side, int SegmentIndex, Pt Start, Pt End, string ExpectedAxis, string ExpectedDirection);
public sealed record SourceSegment(Pt Start, Pt End, int SegmentIndex);
public sealed class AnalysisSegment
{
    public Pt Start { get; init; }
    public Pt End { get; set; }
    public int SegmentIndex { get; init; }
    public List<SourceSegment> SourceSegments { get; init; } = [];
}
public sealed record CrossingHit(Pt Point, int LeftSegment, int RightSegment);
public sealed record CorridorHit(Routed Left, Routed Right, int LeftSegment, int RightSegment, double OverlapLength, Pt OverlapStart, Pt OverlapEnd);
public sealed record ArrowheadEntry(JsonObject Relation, List<Pt>? Points, bool SourceEndpoint, bool TargetEndpoint, Pt Tip, double HalfWidth);
public sealed record ArrowheadHit(ArrowheadEntry Left, ArrowheadEntry Right, double Distance, double Minimum);
public sealed record BorderRunHit(RoutedRelation Routed, Frame Frame, int FrameIndex, string Side, int SegmentIndex,
    double OverlapLength, Pt OverlapStart, Pt OverlapEnd);
public sealed record RouteBudget(int MaxBends, int RoutesOverSuggestedBends, double? MaxStretch, int RoutesOverSuggestedStretch,
    double? MinSegmentPx, double? MinInteriorSegmentPx, int ShortSegmentCount, int ShortEndpointSegmentCount,
    int ShortInteriorSegmentCount, int MicroSegmentCount);
public sealed record RhythmIssue(string Code, JsonObject? Relation, int RelationIndex, int SegmentIndex, string Position, double Length, Pt Start, Pt End);
public sealed record LabelClearanceHit(LabelRect Label, JsonObject LabelRelation, int LabelRelationIndex, JsonObject OtherRelation,
    int OtherRelationIndex, Rect Rect, double Clearance, double? IntersectionLength, int SegmentIndex, Pt Start, Pt End, double Threshold);
public sealed record CanvasOverflowHit(LabelRect Label, JsonObject Relation, int RelationIndex, Rect Rect, double[] ViewBox,
    double[] ViewBoxOrigin, List<string> Sides, JsMap<string, double> OverflowPx, double Tolerance);
public sealed class SpreadPorts { public Pt? From { get; set; } public Pt? To { get; set; } }
public sealed record SeparationOption(Pt Pos, string Relation);

public static class Geometry
{
    // ---- string helpers (every number through Js.Num) ----------------------------------------------------------------
    static string N(double v) => Js.Num(v);
    static double Round1(double v) => Js.Round(v * 10) / 10;
    // point.map((value) => Math.round(value * 10) / 10).join(', ')
    static string PtR1(Pt p) => $"{N(Round1(p.X))}, {N(Round1(p.Y))}";
    // point.map(Math.round).join(', ')
    static string PtRound(Pt p) => $"{N(Js.Round(p.X))}, {N(Js.Round(p.Y))}";
    static JsonArray PtJson(Pt p) => new(p.X, p.Y);
    static JsonArray PtsJson(IEnumerable<Pt> points) => [.. points.Select(p => (JsonNode?)PtJson(p))];
    static JsonObject RectJson(Rect r) => new() { ["x"] = r.X, ["y"] = r.Y, ["width"] = r.Width, ["height"] = r.Height };
    // JS String(value) for a JSON scalar (relation.key, frame.id).
    static string JsStr(JsonNode? n) => n switch
    {
        null => "null",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v => N(Js.ToDouble(v)),
        JsonArray => "",   // ponytail: `${[]}` joins; no caller stores arrays here
        _ => "[object Object]",
    };
    static bool Truthy(string? s) => !string.IsNullOrEmpty(s);
    static string Id(JsonObject? relation) => Truthy(relation.Str("id")) ? $" id \"{relation.Str("id")}\"" : "";
    static string S(string? s) => s ?? "undefined";   // `${relation.from}` of a missing field
    static JsonObject? AsObj(JsonNode? n) => n as JsonObject;

    // In degraded mode (no ajv) a type-wrong top-level field reaches the renderer.
    // Coerce non-arrays to [] so the module-level Maps build without throwing and
    // the friendly validator checks (which run later) report the real problem.
    public static IEnumerable<JsonNode?> AsArray(JsonNode? value) => value.AsArrayOrEmpty();

    // A computed coordinate must be a finite number; NaN/undefined would silently
    // write `<rect x="NaN">` into the output. Used by the validators as a backstop.
    public static bool IsFinitePoint(params double[] coords)
    {
        foreach (var c in coords) if (!double.IsFinite(c)) return false;
        return true;
    }
    static bool IsFinitePoint(Pt p) => double.IsFinite(p.X) && double.IsFinite(p.Y);
    static bool IsFiniteRect(Rect r) => IsFinitePoint(r.X, r.Y, r.Width, r.Height);

    // Separation the layout solver guarantees and separation this check re-derives
    // travel through different arithmetic, so a pair the solver placed at exactly
    // its minimum can land a few ulps short of this comparison. In #583 the CLI's
    // own receipt has the pair `793.6 + 160 + 8` read as 2**-43 past a5's left edge
    // at 961.5999999999999 — one ulp at that magnitude. 0.0001 is the numeric
    // tolerance the route and layout geometry in this file already compares with.
    const double NumericClearancePx = 0.0001;

    public static bool RectsOverlap(Rect a, Rect b, double gap = 0)
    {
        // Non-finite geometry means "unknown", not "overlapping". Every comparison
        // below is false for NaN, so without this guard the negation reports a
        // collision for every pair. Callers surface non-finite pos/size through their
        // own diagnostic; reporting it again as an overlap buries that message under
        // one bogus separation hint per pair.
        if (!IsFinitePoint(a.X, a.Y, a.Width, a.Height, b.X, b.Y, b.Width, b.Height)) return false;
        return !(
            a.X + a.Width + gap <= b.X + NumericClearancePx ||
            b.X + b.Width + gap <= a.X + NumericClearancePx ||
            a.Y + a.Height + gap <= b.Y + NumericClearancePx ||
            b.Y + b.Height + gap <= a.Y + NumericClearancePx
        );
    }

    readonly record struct GapBox(double X1, double Y1, double X2, double Y2);

    public static bool SegmentIntersectsRect(Segment segment, Rect rect, double gap = 0)
    {
        var box = new GapBox(rect.X - gap, rect.Y - gap, rect.X + rect.Width + gap, rect.Y + rect.Height + gap);
        var (a, b) = (segment.Start, segment.End);
        if (PointInBox(a, box) || PointInBox(b, box)) return true;
        return
            SegmentsIntersect(a, b, new(box.X1, box.Y1), new(box.X2, box.Y1)) ||
            SegmentsIntersect(a, b, new(box.X2, box.Y1), new(box.X2, box.Y2)) ||
            SegmentsIntersect(a, b, new(box.X2, box.Y2), new(box.X1, box.Y2)) ||
            SegmentsIntersect(a, b, new(box.X1, box.Y2), new(box.X1, box.Y1));
    }

    public static double? SegmentRectClearance(Segment segment, Rect rect)
    {
        var (start, end) = (segment.Start, segment.End);
        if (!IsFinitePoint(start.X, start.Y, end.X, end.Y, rect.X, rect.Y, rect.Width, rect.Height)) return null;
        if (rect.Width < 0 || rect.Height < 0) return null;
        if (SegmentIntersectsRect(segment, rect)) return 0;

        var right = rect.X + rect.Width;
        var bottom = rect.Y + rect.Height;
        var clearance = Math.Min(PointRectDistance(start, rect), PointRectDistance(end, rect));
        double[] corners = [rect.X, rect.Y, right, rect.Y, right, bottom, rect.X, bottom];
        for (var index = 0; index < 8; index += 2)
        {
            var distance = PointSegmentDistanceXY(corners[index], corners[index + 1], start, end);
            if (distance < clearance) clearance = distance;
        }
        return clearance;
    }

    // The same clearance, but the caller already knows the threshold it compares
    // against: when an axis gap alone proves the distance cannot be smaller, the
    // exact distance is never computed and Infinity is returned.
    public static double? SegmentRectClearanceWithin(Segment segment, Rect rect, double limit)
    {
        var (start, end) = (segment.Start, segment.End);
        if (!IsFinitePoint(start.X, start.Y, end.X, end.Y, rect.X, rect.Y, rect.Width, rect.Height)) return null;
        if (rect.Width < 0 || rect.Height < 0) return null;
        if (SegmentIntersectsRect(segment, rect)) return 0;
        var minX = Math.Min(start.X, end.X);
        var maxX = Math.Max(start.X, end.X);
        var minY = Math.Min(start.Y, end.Y);
        var maxY = Math.Max(start.Y, end.Y);
        var gapX = Math.Max(rect.X - maxX, minX - (rect.X + rect.Width));
        var gapY = Math.Max(rect.Y - maxY, minY - (rect.Y + rect.Height));
        if (Math.Max(gapX, gapY) > limit) return double.PositiveInfinity;
        return SegmentRectClearance(segment, rect);
    }

    public static double? SegmentRectIntersectionLength(Segment segment, Rect rect)
    {
        var (start, end) = (segment.Start, segment.End);
        if (!IsFinitePoint(start.X, start.Y, end.X, end.Y, rect.X, rect.Y, rect.Width, rect.Height)) return null;
        if (rect.Width < 0 || rect.Height < 0) return null;

        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Js.Hypot(dx, dy);
        if (length <= 0.0000001) return 0;
        (double Direction, double Distance)[] bounds =
        [
            (-dx, start.X - rect.X),
            (dx, rect.X + rect.Width - start.X),
            (-dy, start.Y - rect.Y),
            (dy, rect.Y + rect.Height - start.Y),
        ];
        double enter = 0;
        double leave = 1;
        foreach (var (direction, distance) in bounds)
        {
            if (Math.Abs(direction) <= 0.0000001)
            {
                if (distance < -0.0000001) return 0;
                continue;
            }
            var ratio = distance / direction;
            if (direction < 0) enter = Math.Max(enter, ratio);
            else leave = Math.Min(leave, ratio);
            if (enter > leave + 0.0000001) return 0;
        }
        return length * Math.Max(0, leave - enter);
    }

    const double RouteIndexCellSize = 128;
    const double RouteIndexMaxCellsPerRoute = 256;

    static (double X1, double Y1, double X2, double Y2) RouteBounds(Routed route)
    {
        var x1 = double.PositiveInfinity;
        var y1 = double.PositiveInfinity;
        var x2 = double.NegativeInfinity;
        var y2 = double.NegativeInfinity;
        foreach (var (x, y) in route.Points)
        {
            x1 = Math.Min(x1, x);
            y1 = Math.Min(y1, y);
            x2 = Math.Max(x2, x);
            y2 = Math.Max(y2, y);
        }
        return (x1, y1, x2, y2);
    }

    static bool IsSafeInteger(double v) => SpatialGrid<object>.IsSafeInteger(v);

    static Func<Rect, double, List<Routed>> CreateRouteCandidateIndex(List<Routed> routes)
    {
        var cells = new Dictionary<(long X, long Y), List<int>>();   // lookup only
        var globalRouteIndexes = new List<int>();

        for (var routeIndex = 0; routeIndex < routes.Count; routeIndex++)
        {
            var bounds = RouteBounds(routes[routeIndex]);
            var x1 = Math.Floor(bounds.X1 / RouteIndexCellSize);
            var y1 = Math.Floor(bounds.Y1 / RouteIndexCellSize);
            var x2 = Math.Floor(bounds.X2 / RouteIndexCellSize);
            var y2 = Math.Floor(bounds.Y2 / RouteIndexCellSize);
            if (!(IsSafeInteger(x1) && IsSafeInteger(y1) && IsSafeInteger(x2) && IsSafeInteger(y2)))
            {
                globalRouteIndexes.Add(routeIndex);
                continue;
            }
            var cellCount = (x2 - x1 + 1) * (y2 - y1 + 1);

            // Very long routes are cheaper to test once per label than to duplicate
            // across a large part of the index.
            if (!IsSafeInteger(cellCount) || cellCount > RouteIndexMaxCellsPerRoute)
            {
                globalRouteIndexes.Add(routeIndex);
                continue;
            }
            for (var y = (long)y1; y <= (long)y2; y += 1)
            {
                for (var x = (long)x1; x <= (long)x2; x += 1)
                {
                    if (cells.TryGetValue((x, y), out var bucket)) bucket.Add(routeIndex);
                    else cells[(x, y)] = [routeIndex];
                }
            }
        }

        return (rect, threshold) =>
        {
            var x1 = Math.Floor((rect.X - threshold) / RouteIndexCellSize);
            var y1 = Math.Floor((rect.Y - threshold) / RouteIndexCellSize);
            var x2 = Math.Floor((rect.X + rect.Width + threshold) / RouteIndexCellSize);
            var y2 = Math.Floor((rect.Y + rect.Height + threshold) / RouteIndexCellSize);
            var cellCount = (x2 - x1 + 1) * (y2 - y1 + 1);

            // A huge threshold offers no selectivity and can overflow cell iteration.
            if (!(IsSafeInteger(x1) && IsSafeInteger(y1) && IsSafeInteger(x2) && IsSafeInteger(y2))
                || !IsSafeInteger(cellCount)
                || cellCount > Math.Max(64, cells.Count * 4)) return routes;

            var routeIndexes = new JsSet<int>();
            foreach (var index in globalRouteIndexes) routeIndexes.Add(index);
            for (var y = (long)y1; y <= (long)y2; y += 1)
            {
                for (var x = (long)x1; x <= (long)x2; x += 1)
                {
                    if (!cells.TryGetValue((x, y), out var bucket)) continue;
                    foreach (var routeIndex in bucket) routeIndexes.Add(routeIndex);
                }
            }
            var ordered = routeIndexes.ToList();
            Js.Sort(ordered, (left, right) => left - right);
            return ordered.Select(routeIndex => routes[routeIndex]).ToList();
        };
    }

    // entry.points || relation.routePoints
    static List<Pt>? RoutePointsOf(RoutedRelation entry, JsonObject? relation)
    {
        if (entry.Points is not null) return entry.Points;
        if (relation.Arr("routePoints") is not { } raw) return null;
        var points = new List<Pt>();
        foreach (var point in raw)
            if (point is JsonArray pair && pair.Count == 2 && pair[0] is JsonValue px && pair[1] is JsonValue py) points.Add(new(px.D(), py.D()));
        return points;
    }

    public static List<LabelClearanceHit> CollectLabelRouteClearance(IEnumerable<LabelRect>? labels, IEnumerable<RoutedRelation>? routedRelations, double threshold)
    {
        if (!double.IsFinite(threshold) || threshold < 0) return [];
        if (threshold == 0) return [];
        var routeCandidates = new List<Routed>();
        var fallbackIndex = 0;
        foreach (var entry in routedRelations ?? [])
        {
            var relation = entry.Relation ?? new JsonObject();   // `entry?.relation || entry`
            var points = NormalizeRoutePoints(RoutePointsOf(entry, relation));
            if (points.Count >= 2) routeCandidates.Add(new(relation, entry.RelationIndex ?? fallbackIndex, points));
            fallbackIndex++;
        }
        var seenRoutes = new HashSet<string>(StringComparer.Ordinal);   // lookup only
        var routes = routeCandidates.Where(route => seenRoutes.Add(RelationshipIdentity(route.Relation, route.RelationIndex))).ToList();
        var hits = new List<LabelClearanceHit>();
        var seenLabels = new HashSet<string>(StringComparer.Ordinal);   // lookup only
        var candidateRoutes = CreateRouteCandidateIndex(routes);

        fallbackIndex = -1;
        foreach (var label in labels ?? [])
        {
            fallbackIndex++;
            Rect rect = label;
            if (!IsFiniteRect(rect) || rect.Width < 0 || rect.Height < 0) continue;
            var relationIndex = label.RelationIndex ?? fallbackIndex;
            var labelIdentity = RelationshipIdentity(label.Relation, relationIndex);
            if (!seenLabels.Add(labelIdentity)) continue;
            foreach (var route in candidateRoutes(rect, threshold))
            {
                if (relationIndex == route.RelationIndex || SameRelationship(label.Relation, route.Relation)) continue;
                (double Clearance, double? IntersectionLength, int SegmentIndex, Pt Start, Pt End)? nearest = null;
                for (var segmentIndex = 0; segmentIndex < route.Points.Count - 1; segmentIndex += 1)
                {
                    var start = route.Points[segmentIndex];
                    var end = route.Points[segmentIndex + 1];
                    var clearance = SegmentRectClearance(new(start, end), rect);
                    if (clearance is null) continue;
                    if (nearest is null || clearance < nearest.Value.Clearance)
                        nearest = (clearance.Value, SegmentRectIntersectionLength(new(start, end), rect), segmentIndex, start, end);
                }
                if (nearest is null || nearest.Value.Clearance + 0.0001 >= threshold) continue;
                var n = nearest.Value;
                hits.Add(new(label, label.Relation, relationIndex, route.Relation, route.RelationIndex, rect,
                    n.Clearance, n.IntersectionLength, n.SegmentIndex, n.Start, n.End, threshold));
            }
        }
        return hits;
    }

    public static double? MinimumLabelRouteClearance(IEnumerable<LabelClearanceHit>? measurements)
    {
        var list = measurements?.ToList() ?? [];
        if (list.Count == 0) return null;
        var minimum = double.PositiveInfinity;
        foreach (var hit in list) minimum = Math.Min(minimum, hit.Clearance);
        return Round1(minimum);
    }

    static string RelationshipIdentity(JsonObject? relation, int relationIndex)
    {
        if (relation is not null && relation.ContainsKey("key")) return $"key:{JsStr(relation["key"])}";
        if (Truthy(relation.Str("id"))) return $"id:{relation.Str("from") ?? ""}\u0000{relation.Str("to") ?? ""}\u0000{relation.Str("id")}";
        return $"index:{N(relationIndex)}";
    }

    static bool SameRelationship(JsonObject? left, JsonObject? right)
    {
        if (left is null || right is null) return false;
        if (ReferenceEquals(left, right)) return true;
        if (left.ContainsKey("key") && right.ContainsKey("key")) return Js.Stringify(left["key"]) == Js.Stringify(right["key"]);
        return Truthy(left.Str("id")) && Truthy(right.Str("id")) && left.Str("id") == right.Str("id")
            && left.Str("from") == right.Str("from") && left.Str("to") == right.Str("to");
    }

    static JsonObject RelationshipSubject(string diagramType, string relationCollection, int relationIndex, JsonObject? relation)
    {
        var subject = new JsonObject { ["diagramType"] = diagramType, ["collection"] = relationCollection, ["index"] = relationIndex };
        if (Truthy(relation.Str("id"))) subject["id"] = relation.Str("id");
        if (Truthy(relation.Str("from"))) subject["from"] = relation.Str("from");
        if (Truthy(relation.Str("to"))) subject["to"] = relation.Str("to");
        return subject;
    }

    static readonly string[] AuthoredPlanKeys = ["route", "via", "channelX", "channelY", "fromSide", "toSide", "bias", "labelAt", "labelDx", "labelDy", "labelSegment"];

    // Re-planning advice only applies when the failing relation actually carries
    // authored controls; telling an author to remove controls that do not exist
    // just sends them hunting for a field that was never written.
    static string RePlanHint(IEnumerable<JsonObject?> relations, string fallback)
    {
        var controls = new JsSet<string>();
        foreach (var relation in relations)
            foreach (var key in AuthoredPlanKeys) if (relation is not null && relation.ContainsKey(key)) controls.Add(key);
        if (controls.Count == 0) return fallback;
        return $"if the authored {string.Join("/", controls)} are not required by the user, remove them so the renderer can re-plan; otherwise preserve that intent and {fallback}";
    }

    sealed record EndpointSideRule(string Axis, double SourceSign, double TargetSign, string SourceDirection, string TargetDirection);

    static readonly Dictionary<string, EndpointSideRule> EndpointSideRules = new(StringComparer.Ordinal)   // lookup only
    {
        ["left"] = new("horizontal", -1, 1, "leftward", "rightward from the left"),
        ["right"] = new("horizontal", 1, -1, "rightward", "leftward from the right"),
        ["top"] = new("vertical", -1, 1, "upward", "downward from above"),
        ["bottom"] = new("vertical", 1, -1, "downward", "upward from below"),
    };

    static EndpointSideIssue? EndpointSideIssueFor(IEnumerable<Pt>? points, string endpoint, string? side)
    {
        if (side is null || !EndpointSideRules.TryGetValue(side, out var rule)) return null;
        var normalized = NormalizeRoutePoints(points);
        if (normalized.Count < 2) return null;
        var segmentIndex = endpoint == "source" ? 0 : normalized.Count - 2;
        var start = normalized[segmentIndex];
        var end = normalized[segmentIndex + 1];
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var along = rule.Axis == "horizontal" ? dx : dy;
        var across = rule.Axis == "horizontal" ? dy : dx;
        var expectedSign = endpoint == "source" ? rule.SourceSign : rule.TargetSign;
        if (Math.Abs(across) <= 0.0001 && along * expectedSign > 0.0001) return null;
        return new(endpoint, side, segmentIndex, start, end, rule.Axis, endpoint == "source" ? rule.SourceDirection : rule.TargetDirection);
    }

    // A side is a direction contract, not just a point on a box border. This pure
    // predicate lets automatic routers prefer a dogleg whose first and final
    // segments leave/enter the chosen sides perpendicularly.
    public static bool RouteHonorsEndpointSides(IEnumerable<Pt>? points, string? fromSide, string? toSide) =>
        EndpointSideIssueFor(points, "source", fromSide) is null
        && EndpointSideIssueFor(points, "target", toSide) is null;

    // Explicit fromSide/toSide are authored geometry, so a tangent or backwards
    // endpoint segment changes their meaning. Fail this universally instead of
    // leaving a malformed arrow for visual review to discover. Named routes and
    // authored via points already carry their own geometry semantics: when they
    // omit endpoint sides, do not invent a relative-position side and then reject
    // the route for disagreeing with that invention. Pure automatic routes may
    // still be checked against renderer-inferred sides.
    public static List<string> CleanEndpointSideProblems(DiagnosticSink sink, JsonNode? relations, JsSet<string>? endpointIds,
        Func<JsonObject, List<Pt>?> pathFor, string diagramType, string relationCollection,
        Func<JsonObject, string?>? fromSideFor = null, Func<JsonObject, string?>? toSideFor = null,
        Func<JsonObject, int, bool>? shouldCheckRelation = null,
        string routeHint = "align the first/final via segment with fromSide/toSide, change the side, or remove explicit routing so auto can choose a perpendicular approach") =>
        CleanEndpointSideProblems(sink, AsArray(relations), endpointIds, pathFor, diagramType, relationCollection, fromSideFor, toSideFor, shouldCheckRelation, routeHint);

    // The list overloads: render-class.mjs:772 passes `routable`, a filtered JS array of the authored relationship objects,
    // where the other renderers pass the diagram's own array. A JsonObject cannot join a second JsonArray, so the gates
    // also take the objects as a sequence; `relationIndex` counts that sequence, as upstream's does.
    public static List<string> CleanEndpointSideProblems(DiagnosticSink sink, IEnumerable<JsonNode?>? relations, JsSet<string>? endpointIds,
        Func<JsonObject, List<Pt>?> pathFor, string diagramType, string relationCollection,
        Func<JsonObject, string?>? fromSideFor = null, Func<JsonObject, string?>? toSideFor = null,
        Func<JsonObject, int, bool>? shouldCheckRelation = null,
        string routeHint = "align the first/final via segment with fromSide/toSide, change the side, or remove explicit routing so auto can choose a perpendicular approach")
    {
        var problems = new List<string>();
        var relationIndex = -1;
        foreach (var node in relations ?? [])
        {
            relationIndex++;
            if (node is not JsonObject relation || endpointIds is null || !Has(endpointIds, relation.Str("from")) || !Has(endpointIds, relation.Str("to"))) continue;
            if (shouldCheckRelation is not null && !shouldCheckRelation(relation, relationIndex)) continue;
            var points = pathFor(relation);
            if (points is null || points.Count < 2) continue;
            var authoredFromSide = Truthy(relation.Str("fromSide")) && relation.Str("fromSide") != "auto" ? relation.Str("fromSide") : null;
            var authoredToSide = Truthy(relation.Str("toSide")) && relation.Str("toSide") != "auto" ? relation.Str("toSide") : null;
            var hasAuthoredRouteGeometry = (Truthy(relation.Str("route")) && relation.Str("route") != "auto") || relation.Arr("via") is not null;
            var inferredFromSide = !hasAuthoredRouteGeometry && fromSideFor is not null ? fromSideFor(relation) : null;
            var inferredToSide = !hasAuthoredRouteGeometry && toSideFor is not null ? toSideFor(relation) : null;
            var fromSide = authoredFromSide ?? inferredFromSide;
            var toSide = authoredToSide ?? inferredToSide;
            var checks = new List<(EndpointSideIssue Issue, string SideOrigin)>();
            if (Truthy(fromSide) && EndpointSideIssueFor(points, "source", fromSide) is { } sourceIssue) checks.Add((sourceIssue, authoredFromSide is not null ? "authored" : "inferred"));
            if (Truthy(toSide) && EndpointSideIssueFor(points, "target", toSide) is { } targetIssue) checks.Add((targetIssue, authoredToSide is not null ? "authored" : "inferred"));
            foreach (var (issue, sideOrigin) in checks)
            {
                var relationId = Id(relation);
                var authoredField = issue.Endpoint == "source" ? "fromSide" : "toSide";
                var sideField = sideOrigin == "inferred" ? $"inferred {authoredField}" : authoredField;
                var segmentRole = issue.Endpoint == "source" ? "first" : "final";
                var from = PtR1(issue.Start);
                var to = PtR1(issue.End);
                var message = $"[clean-flow/endpoint-side-direction] {diagramType} {relationCollection}[{N(relationIndex)}]{relationId} \"{S(relation.Str("from"))}\" -> \"{S(relation.Str("to"))}\" {segmentRole} segment {N(issue.SegmentIndex)} [{from}] -> [{to}] does not honor {sideField} \"{issue.Side}\" — it must run {issue.ExpectedAxis} {issue.ExpectedDirection}; {routeHint}.";
                sink.Record(new Diagnostic("clean-flow/endpoint-side-direction", Severity.Error, message,
                    RelationshipSubject(diagramType, relationCollection, relationIndex, relation),
                    new JsonObject
                    {
                        ["endpoint"] = issue.Endpoint,
                        ["authoredField"] = authoredField,
                        ["sideOrigin"] = sideOrigin,
                        ["side"] = issue.Side,
                        ["segmentIndex"] = issue.SegmentIndex,
                        ["from"] = PtJson(issue.Start),
                        ["to"] = PtJson(issue.End),
                        ["expectedAxis"] = issue.ExpectedAxis,
                        ["expectedDirection"] = issue.ExpectedDirection,
                    },
                    [routeHint], null));
                problems.Add(message);
            }
        }
        return problems;
    }

    static bool Has(JsSet<string> set, string? value) => value is not null && set.Has(value);

    // One mechanical quality gate for every renderer-owned relationship path.
    // A renderer supplies its semantic obstacle set; source/target boxes are
    // always exempt because paths are expected to terminate on their boundaries.
    // Containers, lifelines, and other intentionally pass-through geometry should
    // simply not be supplied as obstacles.
    public static List<string> CleanFlowProblems(DiagnosticSink sink, JsonNode? relations, IEnumerable<Box> obstacles,
        Func<JsonObject, List<Pt>?> pathFor, string diagramType, string relationCollection, string obstacleKind, double clearance = 2,
        string routeHint = "adjust fromSide/toSide, set route/via or channel coordinates, or move the obstacle") =>
        CleanFlowProblems(sink, AsArray(relations), obstacles, pathFor, diagramType, relationCollection, obstacleKind, clearance, routeHint);

    public static List<string> CleanFlowProblems(DiagnosticSink sink, IEnumerable<JsonNode?>? relations, IEnumerable<Box> obstacles,
        Func<JsonObject, List<Pt>?> pathFor, string diagramType, string relationCollection, string obstacleKind, double clearance = 2,
        string routeHint = "adjust fromSide/toSide, set route/via or channel coordinates, or move the obstacle")
    {
        // A relationship hidden behind an unrelated opaque node changes the
        // diagram's meaning, so this is a correctness invariant rather than an
        // opt-in composition preference. Keep it active even when the author omits
        // quality_profile; standard/showcase still control stricter visual budgets.
        var problems = new List<string>();
        var obstacleList = obstacles.ToList();
        var obstacleIds = new HashSet<string>(obstacleList.Select(obstacle => obstacle.Id), StringComparer.Ordinal);   // lookup only
        var relationIndex = -1;
        foreach (var node in relations ?? [])
        {
            relationIndex++;
            if (node is not JsonObject relation || relation.Str("from") is not { } from || relation.Str("to") is not { } to) continue;
            if (!obstacleIds.Contains(from) || !obstacleIds.Contains(to)) continue;
            var points = pathFor(relation);
            if (points is null || points.Count < 2) continue;
            if (!points.All(IsFinitePoint)) continue;

            foreach (var obstacle in obstacleList)
            {
                if (obstacle.Id == from || obstacle.Id == to) continue;
                if (!IsFinitePoint(obstacle.X, obstacle.Y, obstacle.Width, obstacle.Height)) continue;
                var hitSegment = -1;
                for (var segmentIndex = 0; segmentIndex < points.Count - 1; segmentIndex += 1)
                {
                    if (SegmentIntersectsRect(new(points[segmentIndex], points[segmentIndex + 1]), obstacle, clearance))
                    {
                        hitSegment = segmentIndex;
                        break;
                    }
                }
                if (hitSegment == -1) continue;
                var fromText = PtRound(points[hitSegment]);
                var toText = PtRound(points[hitSegment + 1]);
                var relationId = Id(relation);
                var hint = RePlanHint([relation], routeHint);
                var message = $"[clean-flow/edge-through-node] {diagramType} {relationCollection}[{N(relationIndex)}]{relationId} \"{from}\" -> \"{to}\" crosses {obstacleKind} \"{obstacle.Id}\" (unrelated to this relationship) on segment {N(hitSegment)} [{fromText}] -> [{toText}] ({N(clearance)}px clearance) — {hint}.";
                sink.Record(new Diagnostic("clean-flow/edge-through-node", Severity.Error, message,
                    RelationshipSubject(diagramType, relationCollection, relationIndex, relation),
                    new JsonObject
                    {
                        ["obstacleKind"] = obstacleKind,
                        ["obstacleId"] = obstacle.Id,
                        ["segmentIndex"] = hitSegment,
                        ["from"] = PtJson(points[hitSegment]),
                        ["to"] = PtJson(points[hitSegment + 1]),
                        ["clearancePx"] = clearance,
                    },
                    [hint], null));
                problems.Add(message);
            }
        }
        return problems;
    }

    // Build a read-only analysis copy of a polyline with straight-through
    // waypoints removed. A waypoint on a forward-collinear run is not a visual
    // endpoint, so treating it as one would hide a proper X that lands exactly on
    // that waypoint. Reversals and real bends stay split: their shared point can
    // still be an authored touch rather than a crossing. The source points are
    // retained on every merged segment so diagnostics can name the authored
    // segment that contains a hit without changing rendered/receipt geometry.
    public static List<AnalysisSegment> ForwardCollinearAnalysisSegments(IReadOnlyList<Pt>? points)
    {
        var segments = new List<AnalysisSegment>();
        var list = points ?? [];
        for (var segmentIndex = 0; segmentIndex < list.Count - 1; segmentIndex += 1)
        {
            var start = list[segmentIndex];
            var end = list[segmentIndex + 1];
            var sourceSegment = new SourceSegment(start, end, segmentIndex);
            var previous = segments.Count > 0 ? segments[^1] : null;
            if (previous is not null && SegmentsContinueForward(previous.Start, previous.End, start, end))
            {
                previous.End = end;
                previous.SourceSegments.Add(sourceSegment);
                continue;
            }
            segments.Add(new() { Start = start, End = end, SegmentIndex = segmentIndex, SourceSegments = [sourceSegment] });
        }
        return segments;
    }

    public static int SourceSegmentIndexAtPoint(AnalysisSegment? segment, Pt point)
    {
        var source = segment?.SourceSegments.FirstOrDefault(s => PointLiesOnSegment(point, s.Start, s.End));
        return source?.SegmentIndex ?? segment?.SegmentIndex ?? 0;
    }

    static List<AnalysisSegment> AuthoredAnalysisSegments(IReadOnlyList<Pt> points)
    {
        var segments = new List<AnalysisSegment>();
        for (var segmentIndex = 0; segmentIndex < points.Count - 1; segmentIndex++)
            segments.Add(new()
            {
                Start = points[segmentIndex], End = points[segmentIndex + 1], SegmentIndex = segmentIndex,
                SourceSegments = [new(points[segmentIndex], points[segmentIndex + 1], segmentIndex)],
            });
        return segments;
    }

    static bool SegmentsContinueForward(Pt firstStart, Pt firstEnd, Pt secondStart, Pt secondEnd)
    {
        if (!(IsFinitePoint(firstStart) && IsFinitePoint(firstEnd) && IsFinitePoint(secondStart) && IsFinitePoint(secondEnd))) return false;
        const double epsilon = 0.0001;
        if (Math.Abs(firstEnd.X - secondStart.X) > epsilon
            || Math.Abs(firstEnd.Y - secondStart.Y) > epsilon) return false;
        var firstVector = new Pt(firstEnd.X - firstStart.X, firstEnd.Y - firstStart.Y);
        var secondVector = new Pt(secondEnd.X - secondStart.X, secondEnd.Y - secondStart.Y);
        var firstLength = Js.Hypot(firstVector.X, firstVector.Y);
        var secondLength = Js.Hypot(secondVector.X, secondVector.Y);
        if (firstLength <= epsilon || secondLength <= epsilon) return false;
        var cross = firstVector.X * secondVector.Y - firstVector.Y * secondVector.X;
        if (Math.Abs(cross) > epsilon) return false;
        var dot = firstVector.X * secondVector.X + firstVector.Y * secondVector.Y;
        return dot > epsilon;
    }

    static bool PointLiesOnSegment(Pt point, Pt start, Pt end)
    {
        if (!(IsFinitePoint(point) && IsFinitePoint(start) && IsFinitePoint(end))) return false;
        const double epsilon = 0.0001;
        var length = Js.Hypot(end.X - start.X, end.Y - start.Y);
        if (length <= epsilon) return Js.Hypot(point.X - start.X, point.Y - start.Y) <= epsilon;
        if (Math.Abs(CrossProduct(start, end, point)) > epsilon * length) return false;
        return point.X >= Math.Min(start.X, end.X) - epsilon
            && point.X <= Math.Max(start.X, end.X) + epsilon
            && point.Y >= Math.Min(start.Y, end.Y) - epsilon
            && point.Y <= Math.Max(start.Y, end.Y) + epsilon;
    }

    // A bend meeting another route's interior can look like a branch even when
    // no pair of segment interiors forms a proper X. Keep actual route endpoints
    // out of this geometric query; callers decide which relationships are allowed
    // to share junctions. Collinear corridors remain a separate quality measure.
    public static List<Pt> PolylineBendContacts(IReadOnlyList<Pt> left, IReadOnlyList<Pt> right)
    {
        var contacts = new List<Pt>();
        static bool Near(Pt a, Pt b) => Js.Hypot(a.X - b.X, a.Y - b.Y) <= 0.0001;
        foreach (var (points, other) in new[] { (left, right), (right, left) })
        {
            if (other.Count < 2) continue;
            for (var index = 1; index < points.Count - 1; index += 1)
            {
                var point = points[index];
                if (Math.Abs(CrossProduct(points[index - 1], point, points[index + 1])) <= 0.0001
                    || Near(point, other[0]) || Near(point, other[^1])) continue;
                var onOther = false;
                for (var segment = 0; segment < other.Count - 1; segment++)
                    if (PointLiesOnSegment(point, other[segment], other[segment + 1])) { onOther = true; break; }
                if (onOther && !contacts.Any(otherPoint => Near(point, otherPoint))) contacts.Add(point);
            }
        }
        return contacts;
    }

    // Authored shared endpoints retain their junction interpretation by default.
    // Renderers can opt their automatic routes into proper interior X checks;
    // endpoint touches and collinear trunks still are not proper crossings.
    public static List<string> CleanCrossingProblems(DiagnosticSink sink, JsonNode? relations, JsSet<string> endpointIds,
        Func<JsonObject, List<Pt>?> pathFor, string diagramType, string relationCollection,
        string? profile = null, bool profileIsAuthoritative = false, bool mergeForwardCollinearWaypoints = false,
        Func<JsonObject, JsonObject, bool>? includeSharedEndpoints = null,
        Func<JsonObject, JsonObject, CrossingHit, bool>? crossingResolved = null,
        bool warnInStandard = false, Action<Diagnostic>? onDiagnostic = null,
        string routeHint = "adjust route/via or channel coordinates so the relationships use separate corridors") =>
        CleanCrossingProblems(sink, AsArray(relations), endpointIds, pathFor, diagramType, relationCollection, profile, profileIsAuthoritative,
            mergeForwardCollinearWaypoints, includeSharedEndpoints, crossingResolved, warnInStandard, onDiagnostic, routeHint);

    public static List<string> CleanCrossingProblems(DiagnosticSink sink, IEnumerable<JsonNode?>? relations, JsSet<string> endpointIds,
        Func<JsonObject, List<Pt>?> pathFor, string diagramType, string relationCollection,
        string? profile = null, bool profileIsAuthoritative = false, bool mergeForwardCollinearWaypoints = false,
        Func<JsonObject, JsonObject, bool>? includeSharedEndpoints = null,
        Func<JsonObject, JsonObject, CrossingHit, bool>? crossingResolved = null,
        bool warnInStandard = false, Action<Diagnostic>? onDiagnostic = null,
        string routeHint = "adjust route/via or channel coordinates so the relationships use separate corridors")
    {
        onDiagnostic ??= sink.Record;
        var severity = QualityProfileForGate(profile ?? "standard", profileIsAuthoritative) == "showcase" ? Severity.Error : Severity.Warning;
        if (severity == Severity.Warning && !warnInStandard) return [];
        var routed = new List<(JsonObject Relation, int Index, List<Pt> Points, List<AnalysisSegment> AnalysisSegments)>();
        var index = -1;
        foreach (var node in relations ?? [])
        {
            index++;
            if (node is not JsonObject relation || !Has(endpointIds, relation.Str("from")) || !Has(endpointIds, relation.Str("to"))) continue;
            var points = pathFor(relation);
            if (points is null || points.Count < 2) continue;
            if (!points.All(IsFinitePoint)) continue;
            routed.Add((relation, index, points, mergeForwardCollinearWaypoints ? ForwardCollinearAnalysisSegments(points) : AuthoredAnalysisSegments(points)));
        }
        var problems = new List<string>();

        for (var leftIndex = 0; leftIndex < routed.Count; leftIndex += 1)
        {
            var left = routed[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < routed.Count; rightIndex += 1)
            {
                var right = routed[rightIndex];
                if (SharesEndpoint(left.Relation, right.Relation)
                    && !(includeSharedEndpoints is not null && includeSharedEndpoints(left.Relation, right.Relation))) continue;

                CrossingHit? hit = null;
                foreach (var leftSegment in left.AnalysisSegments)
                {
                    if (hit is not null) break;
                    foreach (var rightSegment in right.AnalysisSegments)
                    {
                        var point = ProperSegmentIntersection(leftSegment.Start, leftSegment.End, rightSegment.Start, rightSegment.End);
                        if (point is { } p)
                        {
                            hit = new(p, SourceSegmentIndexAtPoint(leftSegment, p), SourceSegmentIndexAtPoint(rightSegment, p));
                            break;
                        }
                    }
                }
                if (hit is null) continue;
                if (crossingResolved is not null && crossingResolved(left.Relation, right.Relation, hit)) continue;

                string Describe(JsonObject relation, int relationIndex) =>
                    $"{relationCollection}[{N(relationIndex)}]{Id(relation)} \"{S(relation.Str("from"))}\" -> \"{S(relation.Str("to"))}\"";
                var pointText = PtR1(hit.Point);
                var hint = RePlanHint([left.Relation, right.Relation], routeHint);
                var message = $"[composition/proper-crossing] {(severity == Severity.Error ? "showcase" : "standard")} {diagramType} {Describe(left.Relation, left.Index)} crosses {Describe(right.Relation, right.Index)} at [{pointText}] (segments {N(hit.LeftSegment)} and {N(hit.RightSegment)}) — {hint}.";
                onDiagnostic(new Diagnostic("composition/proper-crossing", severity, message,
                    RelationshipSubject(diagramType, relationCollection, left.Index, left.Relation),
                    new JsonObject
                    {
                        ["otherRelationship"] = RelationshipSubject(diagramType, relationCollection, right.Index, right.Relation),
                        ["point"] = PtJson(hit.Point),
                        ["segmentIndex"] = hit.LeftSegment,
                        ["otherSegmentIndex"] = hit.RightSegment,
                    },
                    [hint], null));
                if (severity == Severity.Error) problems.Add(message);
            }
        }
        return problems;
    }

    // [left.from, left.to].some((id) => id === right.from || id === right.to)
    static bool SharesEndpoint(JsonObject left, JsonObject right)
    {
        var (rf, rt) = (right.Str("from"), right.Str("to"));
        foreach (var id in new[] { left.Str("from"), left.Str("to") }) if (id == rf || id == rt) return true;
        return false;
    }

    // Two unrelated relationships that occupy the same visible corridor can read
    // as one authored branch or merge even when neither relationship crosses a
    // node or forms a proper X. Keep authored shared endpoints exempt by default;
    // automatic architecture routes opt in because they promise separate ports.
    // Workflow v2 opts into shared-endpoint checks with a bounded terminal-trunk
    // exception. Other callers keep their existing authored-junction contract.
    // The counterflow-only opt-in serves older workflow exports without a root
    // readable-v2 contract; full shared-endpoint checking takes precedence.
    // Relationships a renderer declares on one junction (a Lifecycle exit bracket)
    // may share that junction's horizontal bus, the first tick from a common
    // source port and, when they end at the same state, the merged drop into it;
    // other overlaps between them count.
    // Tiny overlaps below the route rhythm
    // floor are ignored to avoid turning sub-pixel rounding into a quality debt.
    public static List<CorridorHit> CollectAmbiguousCorridors(IEnumerable<RoutedRelation>? routedRelations, double minOverlapPx = 8,
        Func<JsonObject, JsonObject, bool>? includeSharedEndpoints = null, Func<JsonObject, JsonObject, bool>? includeSharedEndpointCounterflow = null,
        bool allowShortWorkflowTrunks = false)
    {
        var routed = new List<Routed>();
        var fallbackIndex = -1;
        foreach (var entry in routedRelations ?? [])
        {
            fallbackIndex++;
            var relation = entry.Relation;
            if (relation is null || relation.Str("from") is null || relation.Str("to") is null) continue;
            var points = NormalizeRoutePoints(entry.Points);
            if (points.Count < 2) continue;
            routed.Add(new(relation, entry.RelationIndex ?? fallbackIndex, points, entry.SourceEndpoint, entry.TargetEndpoint));
        }
        var hits = new List<CorridorHit>();

        for (var leftIndex = 0; leftIndex < routed.Count; leftIndex += 1)
        {
            var left = routed[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < routed.Count; rightIndex += 1)
            {
                var right = routed[rightIndex];
                var sharedBus = Truthy(left.Relation.Str("junction")) && left.Relation.Str("junction") == right.Relation.Str("junction");
                var sharedEndpoint = SharesEndpoint(left.Relation, right.Relation);
                var counterflowOnly = sharedEndpoint && !(includeSharedEndpoints is not null && includeSharedEndpoints(left.Relation, right.Relation));
                if (counterflowOnly && !(includeSharedEndpointCounterflow is not null && includeSharedEndpointCounterflow(left.Relation, right.Relation))) continue;

                CorridorHit? longest = null;
                for (var leftSegment = 0; leftSegment < left.Points.Count - 1; leftSegment += 1)
                {
                    for (var rightSegment = 0; rightSegment < right.Points.Count - 1; rightSegment += 1)
                    {
                        var overlap = CollinearAxisOverlap(
                            left.Points[leftSegment],
                            left.Points[leftSegment + 1],
                            right.Points[rightSegment],
                            right.Points[rightSegment + 1]);
                        if (overlap is not { } o || o.Length + 0.0001 < minOverlapPx) continue;
                        if (allowShortWorkflowTrunks && ShortWorkflowTrunk(left, right, leftSegment, rightSegment, o.Length)) continue;
                        // A multi-target bracket repeats its source tick for each target.
                        // Limit that exemption to the first segment at the same source port:
                        // a common source identity cannot waive an interior vertical overlap.
                        var sharedSourceTick = left.SourceEndpoint && right.SourceEndpoint
                            && left.Relation.Str("from") == right.Relation.Str("from") && leftSegment == 0 && rightSegment == 0
                            && Math.Abs(left.Points[0].X - right.Points[0].X) < 0.0001
                            && Math.Abs(left.Points[0].Y - right.Points[0].Y) < 0.0001;
                        if (sharedBus && (Math.Abs(o.Start.Y - o.End.Y) < 0.0001
                            || left.Relation.Str("to") == right.Relation.Str("to") || sharedSourceTick)) continue;
                        if (counterflowOnly)
                        {
                            var leftDelta = new Pt(left.Points[leftSegment + 1].X - left.Points[leftSegment].X, left.Points[leftSegment + 1].Y - left.Points[leftSegment].Y);
                            var rightDelta = new Pt(right.Points[rightSegment + 1].X - right.Points[rightSegment].X, right.Points[rightSegment + 1].Y - right.Points[rightSegment].Y);
                            if (leftDelta.X * rightDelta.X + leftDelta.Y * rightDelta.Y >= 0) continue;
                        }
                        if (longest is null || o.Length > longest.OverlapLength + 0.0001)
                            longest = new(left, right, leftSegment, rightSegment, o.Length, o.Start, o.End);
                    }
                }
                if (longest is not null) hits.Add(longest);
            }
        }
        return hits;
    }

    // edge.width || (variant === 'emphasis' ? 1.8 : 1.4)
    static string Variant(JsonObject edge) => Truthy(edge.Str("variant")) ? edge.Str("variant")! : "default";
    static double Or(double? value, double fallback) => value is { } v && v != 0 && !double.IsNaN(v) ? v : fallback;

    static bool ShortWorkflowTrunk(Routed left, Routed right, int leftSegment, int rightSegment, double length)
    {
        if (length > 24 + 0.0001) return false;
        var a = left.Relation; var b = right.Relation;
        static double Width(JsonObject edge) => Or(edge.Num("width"), Variant(edge) == "emphasis" ? 1.8 : 1.4);
        if (Variant(a) != Variant(b) || Width(a) != Width(b) || (a.Str("role") ?? "") != (b.Str("role") ?? "")) return false;
        static bool Same(Pt p, Pt q) => Math.Abs(p.X - q.X) < 0.0001 && Math.Abs(p.Y - q.Y) < 0.0001;
        var source = left.SourceEndpoint && right.SourceEndpoint
            && a.Str("from") == b.Str("from") && leftSegment == 0 && rightSegment == 0
            && Same(left.Points[0], right.Points[0]);
        var target = left.TargetEndpoint && right.TargetEndpoint
            && a.Str("to") == b.Str("to") && leftSegment == left.Points.Count - 2 && rightSegment == right.Points.Count - 2
            && Same(left.Points[^1], right.Points[^1]);
        if (!source && !target) return false;
        var p = left.Points[leftSegment]; var q = left.Points[leftSegment + 1];
        var r = right.Points[rightSegment]; var s = right.Points[rightSegment + 1];
        return (q.X - p.X) * (s.X - r.X) + (q.Y - p.Y) * (s.Y - r.Y) > 0;
    }

    // Bundled arrow markers are 7 stroke-widths across the direction of travel.
    // Callers select the automatic routes they own; explicit junctions are preserved,
    // and relations on one junction ending at the same state share a merged drop.
    public static List<ArrowheadHit> CollectArrowheadCollisions(IEnumerable<RoutedRelation>? routedRelations, bool allowShortWorkflowTrunks = false)
    {
        var incoming = new Dictionary<(string To, int Axis, int Direction), List<ArrowheadEntry>>();   // lookup only
        var hits = new List<ArrowheadHit>();
        foreach (var entry in routedRelations ?? [])
        {
            var points = NormalizeRoutePoints(entry.Points);
            if (points.Count < 2 || entry.Relation is not { } relation || !Truthy(relation.Str("to"))) continue;
            var tip = points[^1];
            var previous = points[^2];
            var vertical = Math.Abs(tip.X - previous.X) < 0.0001;
            var axis = vertical ? 0 : 1;
            var direction = Math.Sign(Coord(tip, 1 - axis) - Coord(previous, 1 - axis));
            var halfWidth = 3.5 * Or(relation.Num("width"), relation.Str("variant") == "emphasis" ? 1.8 : 1.5);
            var key = (relation.Str("to")!, axis, direction);
            if (!incoming.TryGetValue(key, out var siblings)) siblings = [];
            var current = new ArrowheadEntry(relation, entry.Points, entry.SourceEndpoint, entry.TargetEndpoint, tip, halfWidth);
            foreach (var sibling in siblings)
            {
                // One junction's merged drop into a state carries a single arrowhead.
                if (Truthy(sibling.Relation.Str("junction")) && sibling.Relation.Str("junction") == relation.Str("junction")
                    && sibling.Relation.Str("to") == relation.Str("to")) continue;
                if (Math.Abs(Coord(tip, 1 - axis) - Coord(sibling.Tip, 1 - axis)) > 0.0001) continue;
                var distance = Math.Abs(Coord(tip, axis) - Coord(sibling.Tip, axis));
                var minimum = halfWidth + sibling.HalfWidth;
                if (allowShortWorkflowTrunks)
                {
                    var left = new Routed(sibling.Relation, 0, NormalizeRoutePoints(sibling.Points), sibling.SourceEndpoint, sibling.TargetEndpoint);
                    var right = new Routed(relation, 0, points, entry.SourceEndpoint, entry.TargetEndpoint);
                    var overlap = CollinearAxisOverlap(left.Points[^2], left.Points[^1], points[^2], tip);
                    if (overlap is { } o && ShortWorkflowTrunk(left, right, left.Points.Count - 2, points.Count - 2, o.Length)) continue;
                }
                if (distance < minimum - 0.0001) hits.Add(new(sibling, current, distance, minimum));
            }
            siblings.Add(current);
            incoming[key] = siblings;
        }
        return hits;
    }

    static double Coord(Pt p, int axis) => axis == 0 ? p.X : p.Y;

    public static List<string> CleanAmbiguousCorridorProblems(DiagnosticSink sink, JsonNode? relations, JsSet<string>? endpointIds,
        Func<JsonObject, List<Pt>?> pathFor, string diagramType, string relationCollection,
        string? profile = null, bool profileIsAuthoritative = false,
        string routeHint = "adjust route/via or channel coordinates so the relationships use separate corridors",
        double minOverlapPx = 8, Func<JsonObject, JsonObject, bool>? includeSharedEndpoints = null,
        Func<JsonObject, JsonObject, bool>? includeSharedEndpointCounterflow = null, bool allowShortWorkflowTrunks = false,
        Action<Diagnostic>? onDiagnostic = null) =>
        CleanAmbiguousCorridorProblems(sink, AsArray(relations), endpointIds, pathFor, diagramType, relationCollection, profile, profileIsAuthoritative,
            routeHint, minOverlapPx, includeSharedEndpoints, includeSharedEndpointCounterflow, allowShortWorkflowTrunks, onDiagnostic);

    public static List<string> CleanAmbiguousCorridorProblems(DiagnosticSink sink, IEnumerable<JsonNode?>? relations, JsSet<string>? endpointIds,
        Func<JsonObject, List<Pt>?> pathFor, string diagramType, string relationCollection,
        string? profile = null, bool profileIsAuthoritative = false,
        string routeHint = "adjust route/via or channel coordinates so the relationships use separate corridors",
        double minOverlapPx = 8, Func<JsonObject, JsonObject, bool>? includeSharedEndpoints = null,
        Func<JsonObject, JsonObject, bool>? includeSharedEndpointCounterflow = null, bool allowShortWorkflowTrunks = false,
        Action<Diagnostic>? onDiagnostic = null)
    {
        onDiagnostic ??= sink.Record;
        var severity = QualityProfileForGate(profile ?? "standard", profileIsAuthoritative) == "showcase" ? Severity.Error : Severity.Warning;
        if (severity == Severity.Warning && !allowShortWorkflowTrunks) return [];
        var routedRelations = CollectEligibleRoutedRelations(relations, endpointIds, pathFor);

        var problems = new List<string>();
        foreach (var hit in CollectAmbiguousCorridors(routedRelations, minOverlapPx, includeSharedEndpoints, includeSharedEndpointCounterflow, allowShortWorkflowTrunks))
        {
            string Describe(Routed r) => $"{relationCollection}[{N(r.RelationIndex)}]{Id(r.Relation)} \"{S(r.Relation.Str("from"))}\" -> \"{S(r.Relation.Str("to"))}\"";
            var length = Round1(hit.OverlapLength);
            var from = PtR1(hit.OverlapStart);
            var to = PtR1(hit.OverlapEnd);
            var hint = RePlanHint([hit.Left.Relation, hit.Right.Relation], routeHint);
            var message = $"[composition/ambiguous-corridor] {(severity == Severity.Error ? "showcase" : "standard")} {diagramType} {Describe(hit.Left)} shares a {N(length)}px corridor with {Describe(hit.Right)} at [{from}] -> [{to}] (segments {N(hit.LeftSegment)} and {N(hit.RightSegment)}; minimum {N(minOverlapPx)}px) — {hint}.";
            onDiagnostic(new Diagnostic("composition/ambiguous-corridor", severity, message,
                RelationshipSubject(diagramType, relationCollection, hit.Left.RelationIndex, hit.Left.Relation),
                new JsonObject
                {
                    ["otherRelationship"] = RelationshipSubject(diagramType, relationCollection, hit.Right.RelationIndex, hit.Right.Relation),
                    ["overlapLengthPx"] = length,
                    ["minimumPx"] = minOverlapPx,
                    ["from"] = PtJson(hit.OverlapStart),
                    ["to"] = PtJson(hit.OverlapEnd),
                    ["segmentIndex"] = hit.LeftSegment,
                    ["otherSegmentIndex"] = hit.RightSegment,
                },
                [hint], null));
            if (severity == Severity.Error) problems.Add(message);
        }
        return problems;
    }

    // Relationship paths may cross a structural frame, but they must not borrow a
    // frame side as a routing corridor. Rounded rectangle corners are trimmed from
    // the modeled straight sides so a short corner touch is not mistaken for a
    // border run. Any positive straight overlap beyond the numeric epsilon is a
    // hard failure in every quality profile; 16px belongs only to the separate,
    // neutral short-segment metric and is not a corridor exemption.
    public static List<BorderRunHit> CollectBorderRuns(IEnumerable<RoutedRelation>? routedRelations, IEnumerable<Frame>? frames)
    {
        var hits = new List<BorderRunHit>();
        var frameList = frames?.ToList() ?? [];
        foreach (var routed in routedRelations ?? [])
        {
            var points = routed.Points ?? [];
            var routeSegments = routed.Segments;
            if (routeSegments is null)
            {
                routeSegments = [];
                for (var index = 0; index < points.Count - 1; index++) routeSegments.Add(new(points[index], points[index + 1]));
            }
            if (routeSegments.Count == 0) continue;
            if (!routeSegments.All(segment => IsFinitePoint(segment.Start) && IsFinitePoint(segment.End))) continue;
            for (var frameIndex = 0; frameIndex < frameList.Count; frameIndex++)
            {
                var frame = frameList[frameIndex];
                foreach (var border in FrameBorderSegments(frame))
                {
                    var overlaps = new List<(Overlap Overlap, int SegmentIndex)>();
                    for (var segmentIndex = 0; segmentIndex < routeSegments.Count; segmentIndex += 1)
                    {
                        var segment = routeSegments[segmentIndex];
                        var overlap = CollinearAxisOverlap(segment.Start, segment.End, border.Start, border.End);
                        if (overlap is not { } o || o.Length <= 0.0001) continue;
                        overlaps.Add((o, segmentIndex));
                    }
                    if (overlaps.Count == 0) continue;
                    var merged = MergeBorderOverlaps(overlaps.Select(o => o.Overlap).ToList(), border);
                    var sorted = merged.ToList();
                    Js.Sort(sorted, (left, right) => right.Length - left.Length is var d && d != 0 ? d : left.Low - right.Low);
                    var longest = sorted[0];
                    var minSegment = int.MaxValue;
                    foreach (var (_, segmentIndex) in overlaps) minSegment = Math.Min(minSegment, segmentIndex);
                    double total = 0;
                    foreach (var interval in merged) total += interval.Length;
                    hits.Add(new(routed, frame, frameIndex, border.Side, minSegment, total, longest.Start, longest.End));
                }
            }
        }
        return hits;
    }

    public static List<string> CleanBorderRunProblems(DiagnosticSink sink, JsonNode? relations, JsSet<string>? endpointIds, IEnumerable<Frame>? frames,
        Func<JsonObject, List<Pt>?> pathFor, string diagramType, string relationCollection, string? profile, bool profileIsAuthoritative = false,
        string routeHint = "adjust route/via or channel coordinates so the relationship crosses the frame perpendicularly through a clear opening")
    {
        if (!Truthy(QualityProfileForGate(profile, profileIsAuthoritative))) return [];
        var routedRelations = CollectEligibleRoutedRelations(AsArray(relations), endpointIds, pathFor);
        var problems = new List<string>();
        foreach (var hit in CollectBorderRuns(routedRelations, frames))
        {
            var relation = hit.Routed.Relation ?? new JsonObject();
            var relationIndex = hit.Routed.RelationIndex ?? 0;
            var relationId = Id(relation);
            var frameKind = Truthy(hit.Frame.Kind) ? hit.Frame.Kind! : Truthy(hit.Frame.Shape) ? hit.Frame.Shape! : "frame";
            var frameIdentity = Truthy(hit.Frame.Label) ? hit.Frame.Label! : hit.Frame.Id.Truthy() ? JsStr(hit.Frame.Id) : N(hit.FrameIndex);
            var length = Round1(hit.OverlapLength);
            var from = PtR1(hit.OverlapStart);
            var to = PtR1(hit.OverlapEnd);
            var hint = RePlanHint([relation], routeHint);
            var message = $"[composition/container-border-run] {diagramType} {relationCollection}[{N(relationIndex)}]{relationId} \"{S(relation.Str("from"))}\" -> \"{S(relation.Str("to"))}\" follows {frameKind} \"{frameIdentity}\" {hit.Side} border for {N(length)}px on segment {N(hit.SegmentIndex)} [{from}] -> [{to}] — {hint}.";
            var evidence = new JsonObject { ["frameKind"] = frameKind };
            if (hit.Frame.Id is not null) evidence["frameId"] = hit.Frame.Id.DeepClone();
            if (hit.Frame.Label is not null) evidence["frameLabel"] = hit.Frame.Label;
            evidence["side"] = hit.Side;
            evidence["segmentIndex"] = hit.SegmentIndex;
            evidence["overlapLengthPx"] = length;
            evidence["from"] = PtJson(hit.OverlapStart);
            evidence["to"] = PtJson(hit.OverlapEnd);
            sink.Record(new Diagnostic("composition/container-border-run", Severity.Error, message,
                RelationshipSubject(diagramType, relationCollection, relationIndex, relation), evidence, [hint], null));
            problems.Add(message);
        }
        return problems;
    }

    public static RouteBudget RouteBudgetMetrics(IEnumerable<RoutedRelation>? routedRelations, double bendsPerRelationship = 2,
        double stretch = 1.35, double segmentPx = 16, double microSegmentPx = 8)
    {
        var maxBends = 0;
        var routesOverSuggestedBends = 0;
        double? maxStretch = null;
        var routesOverSuggestedStretch = 0;
        double? minSegmentPx = null;
        double? minInteriorSegmentPx = null;
        var shortSegmentCount = 0;
        var shortEndpointSegmentCount = 0;
        var shortInteriorSegmentCount = 0;
        var microSegmentCount = 0;

        foreach (var routed in routedRelations ?? [])
        {
            var points = NormalizeRoutePoints(routed.Points);
            if (points.Count < 2) continue;
            var bends = Math.Max(0, points.Count - 2);
            maxBends = Math.Max(maxBends, bends);
            if (bends > bendsPerRelationship) routesOverSuggestedBends += 1;

            double routeLength = 0;
            for (var index = 0; index < points.Count - 1; index += 1)
            {
                var length = Math.Abs(points[index + 1].X - points[index].X) + Math.Abs(points[index + 1].Y - points[index].Y);
                if (length <= 0.0001) continue;
                var position = SegmentPosition(index, points.Count - 1);
                routeLength += length;
                minSegmentPx = minSegmentPx is null ? length : Math.Min(minSegmentPx.Value, length);
                if (position == "interior")
                    minInteriorSegmentPx = minInteriorSegmentPx is null ? length : Math.Min(minInteriorSegmentPx.Value, length);
                if (length < segmentPx)
                {
                    shortSegmentCount += 1;
                    if (position == "interior") shortInteriorSegmentCount += 1;
                    else shortEndpointSegmentCount += 1;
                }
                if (length < microSegmentPx) microSegmentCount += 1;
            }
            var direct = Math.Abs(points[^1].X - points[0].X) + Math.Abs(points[^1].Y - points[0].Y);
            if (direct > 0.0001)
            {
                var routeStretch = routeLength / direct;
                maxStretch = maxStretch is null ? routeStretch : Math.Max(maxStretch.Value, routeStretch);
                if (routeStretch > stretch + 0.0001) routesOverSuggestedStretch += 1;
            }
        }

        return new(maxBends, routesOverSuggestedBends, maxStretch, routesOverSuggestedStretch, minSegmentPx, minInteriorSegmentPx,
            shortSegmentCount, shortEndpointSegmentCount, shortInteriorSegmentCount, microSegmentCount);
    }

    public static List<RhythmIssue> CollectRouteRhythmIssues(IEnumerable<RoutedRelation>? routedRelations, double interiorSegmentPx = 16, double microSegmentPx = 8)
    {
        var issues = new List<RhythmIssue>();
        var fallbackIndex = -1;
        foreach (var routed in routedRelations ?? [])
        {
            fallbackIndex++;
            var points = NormalizeRoutePoints(routed.Points);
            if (points.Count < 2) continue;
            for (var segmentIndex = 0; segmentIndex < points.Count - 1; segmentIndex += 1)
            {
                var start = points[segmentIndex];
                var end = points[segmentIndex + 1];
                var length = Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y);
                if (length <= 0.0001) continue;
                var position = SegmentPosition(segmentIndex, points.Count - 1);
                var code = length < microSegmentPx - 0.0001
                    ? "composition/micro-segment"
                    : position == "interior" && length < interiorSegmentPx - 0.0001
                        ? "composition/short-interior-segment"
                        : null;
                if (code is null) continue;
                issues.Add(new(code, routed.Relation, routed.RelationIndex ?? fallbackIndex, segmentIndex, position, length, start, end));
            }
        }
        return issues;
    }

    public static List<string> CleanRouteRhythmProblems(DiagnosticSink sink, JsonNode? relations, JsSet<string>? endpointIds,
        Func<JsonObject, List<Pt>?> pathFor, string diagramType, string relationCollection, string? profile, bool profileIsAuthoritative = false,
        string routeHint = "move the channel/via point to remove the cramped turn or give the route more corridor space",
        double interiorSegmentPx = 16, double microSegmentPx = 8) =>
        CleanRouteRhythmProblems(sink, AsArray(relations), endpointIds, pathFor, diagramType, relationCollection, profile, profileIsAuthoritative,
            routeHint, interiorSegmentPx, microSegmentPx);

    public static List<string> CleanRouteRhythmProblems(DiagnosticSink sink, IEnumerable<JsonNode?>? relations, JsSet<string>? endpointIds,
        Func<JsonObject, List<Pt>?> pathFor, string diagramType, string relationCollection, string? profile, bool profileIsAuthoritative = false,
        string routeHint = "move the channel/via point to remove the cramped turn or give the route more corridor space",
        double interiorSegmentPx = 16, double microSegmentPx = 8)
    {
        if (QualityProfileForGate(profile, profileIsAuthoritative) != "showcase") return [];
        var routedRelations = CollectEligibleRoutedRelations(relations, endpointIds, pathFor);
        var problems = new List<string>();
        foreach (var hit in CollectRouteRhythmIssues(routedRelations, interiorSegmentPx, microSegmentPx))
        {
            var relation = hit.Relation ?? new JsonObject();
            var relationId = Id(relation);
            var length = Round1(hit.Length);
            var from = PtR1(hit.Start);
            var to = PtR1(hit.End);
            var rule = hit.Code == "composition/micro-segment"
                ? $"is below the {N(microSegmentPx)}px micro-segment floor"
                : $"is below the {N(interiorSegmentPx)}px interior-segment floor";
            var hint = RePlanHint([relation], routeHint);
            var message = $"[{hit.Code}] showcase {diagramType} {relationCollection}[{N(hit.RelationIndex)}]{relationId} \"{S(relation.Str("from"))}\" -> \"{S(relation.Str("to"))}\" has a {N(length)}px {hit.Position} segment {N(hit.SegmentIndex)} [{from}] -> [{to}] that {rule} — {hint}.";
            sink.Record(new Diagnostic(hit.Code, Severity.Error, message,
                RelationshipSubject(diagramType, relationCollection, hit.RelationIndex, relation),
                new JsonObject
                {
                    ["segmentIndex"] = hit.SegmentIndex,
                    ["position"] = hit.Position,
                    ["lengthPx"] = length,
                    ["minimumPx"] = hit.Code == "composition/micro-segment" ? microSegmentPx : interiorSegmentPx,
                    ["from"] = PtJson(hit.Start),
                    ["to"] = PtJson(hit.End),
                },
                [hint], null));
            problems.Add(message);
        }
        return problems;
    }

    public static List<string> CleanLabelRouteClearanceProblems(DiagnosticSink sink, JsonNode? relations, IEnumerable<LabelRect>? labels, JsSet<string>? endpointIds,
        Func<JsonObject, List<Pt>?> pathFor, string diagramType, string relationCollection, string? profile, bool profileIsAuthoritative = false,
        double threshold = 4,
        string routeHint = "adjust labelAt, labelDx, labelDy, or labelSegment; otherwise adjust the other relationship route/via/channel") =>
        CleanLabelRouteClearanceProblems(sink, AsArray(relations), labels, endpointIds, pathFor, diagramType, relationCollection, profile, profileIsAuthoritative,
            threshold, routeHint);

    public static List<string> CleanLabelRouteClearanceProblems(DiagnosticSink sink, IEnumerable<JsonNode?>? relations, IEnumerable<LabelRect>? labels, JsSet<string>? endpointIds,
        Func<JsonObject, List<Pt>?> pathFor, string diagramType, string relationCollection, string? profile, bool profileIsAuthoritative = false,
        double threshold = 4,
        string routeHint = "adjust labelAt, labelDx, labelDy, or labelSegment; otherwise adjust the other relationship route/via/channel")
    {
        if (QualityProfileForGate(profile, profileIsAuthoritative) != "showcase") return [];
        var routedRelations = CollectEligibleRoutedRelations(relations, endpointIds, pathFor);
        var problems = new List<string>();
        foreach (var hit in CollectLabelRouteClearance(labels, routedRelations, threshold))
        {
            string Describe(JsonObject? relation, int relationIndex)
            {
                var relationId = Id(relation);
                var relationLabel = Truthy(relation.Str("label")) ? $" label \"{relation.Str("label")}\"" : "";
                return $"{relationCollection}[{N(relationIndex)}]{relationId} \"{S(relation.Str("from"))}\" -> \"{S(relation.Str("to"))}\"{relationLabel}";
            }
            var clearance = Round1(hit.Clearance);
            var from = PtR1(hit.Start);
            var to = PtR1(hit.End);
            var hint = RePlanHint([hit.LabelRelation, hit.OtherRelation], routeHint);
            var labelText = Truthy(hit.Label.Label) ? hit.Label.Label! : Truthy(hit.LabelRelation.Str("label")) ? hit.LabelRelation.Str("label")! : "";
            var message = $"[composition/label-route-clearance] showcase {diagramType} label \"{labelText}\" on {Describe(hit.LabelRelation, hit.LabelRelationIndex)} is {N(clearance)}px from {Describe(hit.OtherRelation, hit.OtherRelationIndex)} segment {N(hit.SegmentIndex)} [{from}] -> [{to}] (label rect {FormatRect(hit.Rect)}; minimum {N(threshold)}px) — {hint}.";
            sink.Record(new Diagnostic("composition/label-route-clearance", Severity.Error, message,
                RelationshipSubject(diagramType, relationCollection, hit.LabelRelationIndex, hit.LabelRelation),
                new JsonObject
                {
                    ["label"] = labelText,
                    ["otherRelationship"] = RelationshipSubject(diagramType, relationCollection, hit.OtherRelationIndex, hit.OtherRelation),
                    ["segmentIndex"] = hit.SegmentIndex,
                    ["clearancePx"] = clearance,
                    ["minimumPx"] = threshold,
                    ["labelRect"] = LabelRectJson(hit.Label),
                    ["from"] = PtJson(hit.Start),
                    ["to"] = PtJson(hit.End),
                },
                [hint], null));
            problems.Add(message);
        }
        return problems;
    }

    // evidence.labelRect is the whole label record upstream (`hit.rect` is the label itself); JSON.stringify drops the
    // relation object cycle-free but keeps every own property, so emit the record's scalar fields in its shape.
    static JsonObject LabelRectJson(LabelRect label)
    {
        var json = new JsonObject
        {
            ["relation"] = label.Relation.DeepClone(),
            ["relationIndex"] = label.RelationIndex,
            ["label"] = label.Label,
            ["x"] = label.X, ["y"] = label.Y, ["width"] = label.Width, ["height"] = label.Height,
        };
        if (label.Lx is { } lx) json["lx"] = lx;
        if (label.Ly is { } ly) json["ly"] = ly;
        return json;
    }

    // How far [start, start + size) leaves [0, extent), per end of one axis. This
    // is the single definition of "inside the canvas" that both the containment
    // rule and the repair hints measure against; an unknown extent bounds nothing.
    static (double Start, double End) AxisCanvasOverflow(double start, double size, double extent, double origin = 0)
    {
        if (!double.IsFinite(extent)) return (0, 0);
        return (origin - start, start + size - (origin + extent));
    }

    // THE CANVAS CONTAINMENT RATIONALE (referenced from the other call sites).
    //
    // The SVG canvas clips whatever leaves the viewBox, so an edge label that
    // overhangs an edge ships as truncated text while every post-render check on
    // the emitted markup still passes: clipped text is still well-formed markup.
    // Renderers with a fixed canvas already bound their nodes, lanes, and legends
    // against it; edge label rects were the exception.
    //
    // Not applied to readable-v2 workflows: that compiler grows its canvas around
    // pinned label rects and rejects an authored viewBox that cannot hold the
    // result, so the only gap growth cannot close is the origin its own rule
    // already guards. Architecture's auto canvas grows the same way, which leaves
    // this rule reporting authored viewBoxes and the origin side.
    //
    // Showcase only: a standard document authored before the rule exists may
    // overhang by a few pixels, and failing it there would break compatibility
    // instead of repairing a diagram.
    public static List<CanvasOverflowHit> CollectLabelCanvasOverflow(IEnumerable<LabelRect>? labels, IReadOnlyList<double>? viewBox, double tolerance = 0.5)
    {
        // Renderers author origin-zero canvases (meta.viewBox is [width, height] by
        // schema), but `check` re-measures foreign artifacts whose SVG viewBox may
        // carry a legal non-zero min-x/min-y; those pass all four numbers.
        var box = viewBox ?? [];
        var (originX, originY, canvasWidth, canvasHeight) = box.Count == 4
            ? (box[0], box[1], box[2], box[3])
            : (0, 0, box.Count > 0 ? box[0] : double.NaN, box.Count > 1 ? box[1] : double.NaN);
        if (!IsFinitePoint(originX, originY, canvasWidth, canvasHeight)) return [];
        var hits = new List<CanvasOverflowHit>();
        var fallbackIndex = -1;
        foreach (var label in labels ?? [])
        {
            fallbackIndex++;
            Rect rect = label;
            if (!IsFiniteRect(rect)) continue;
            // `check` parses foreign markup, so a malformed negative-size rect is
            // skipped like the other label collectors do — its flipped interval would
            // otherwise read as contained.
            if (rect.Width < 0 || rect.Height < 0) continue;
            var horizontal = AxisCanvasOverflow(rect.X, rect.Width, canvasWidth, originX);
            var vertical = AxisCanvasOverflow(rect.Y, rect.Height, canvasHeight, originY);
            (string Side, double Overflow)[] overflow = [("left", horizontal.Start), ("right", horizontal.End), ("top", vertical.Start), ("bottom", vertical.End)];
            var sides = overflow.Where(o => o.Overflow > tolerance).Select(o => o.Side).ToList();
            if (sides.Count == 0) continue;
            var overflowPx = new JsMap<string, double>();
            foreach (var (side, value) in overflow) if (value > tolerance) overflowPx.Set(side, Round1(value));
            hits.Add(new(label, label.Relation, label.RelationIndex ?? fallbackIndex, rect, [canvasWidth, canvasHeight], [originX, originY], sides, overflowPx, tolerance));
        }
        return hits;
    }

    public static string DescribeLabelCanvasOverflow(CanvasOverflowHit hit) =>
        string.Join(" and ", hit.Sides.Select(side => $"{side} edge by {N(hit.OverflowPx.Get(side))}px"));

    public static List<string> CleanLabelCanvasContainmentProblems(DiagnosticSink sink, IEnumerable<LabelRect>? labels, IReadOnlyList<double>? viewBox,
        string diagramType, string relationCollection, string? profile, bool profileIsAuthoritative = false,
        string routeHint = "adjust labelAt, labelDx, labelDy, or labelSegment; otherwise enlarge meta.viewBox",
        // Label widths are estimated from the text, not measured, so sub-pixel
        // overhang is rounding noise rather than a visible truncation.
        double tolerance = 0.5)
    {
        if (QualityProfileForGate(profile, profileIsAuthoritative) != "showcase") return [];
        var problems = new List<string>();
        foreach (var hit in CollectLabelCanvasOverflow(labels, viewBox, tolerance))
        {
            var relation = hit.Relation;
            var relationId = Id(relation);
            var labelText = Truthy(hit.Label.Label) ? hit.Label.Label! : Truthy(relation.Str("label")) ? relation.Str("label")! : "";
            var hint = RePlanHint([relation], routeHint);
            var message = $"[composition/label-canvas-containment] showcase {diagramType} label \"{labelText}\" on {relationCollection}[{N(hit.RelationIndex)}]{relationId} \"{S(relation.Str("from"))}\" -> \"{S(relation.Str("to"))}\" extends past the {DescribeLabelCanvasOverflow(hit)} (label rect {FormatRect(hit.Rect)}; viewBox {N(hit.ViewBox[0])}x{N(hit.ViewBox[1])}) — {hint}.";
            var overflowPx = new JsonObject();
            foreach (var (side, value) in hit.OverflowPx.Entries) overflowPx[side] = value;
            sink.Record(new Diagnostic("composition/label-canvas-containment", Severity.Error, message,
                RelationshipSubject(diagramType, relationCollection, hit.RelationIndex, relation),
                new JsonObject
                {
                    ["label"] = labelText,
                    ["labelRect"] = RectJson(hit.Rect),
                    ["viewBox"] = new JsonArray(hit.ViewBox[0], hit.ViewBox[1]),
                    ["overflowPx"] = overflowPx,
                    ["tolerancePx"] = hit.Tolerance,
                },
                [hint], null));
            problems.Add(message);
        }
        return problems;
    }

    // Upstream: `profileIsAuthoritative ? profile : process.env.ARCHIFY_QUALITY_PROFILE || profile`. The port has no
    // environment reads; the layout gates read the authored profile only (Task 9 brief, S5), so both branches are `profile`.
    static string? QualityProfileForGate(string? profile, bool profileIsAuthoritative) => profile;

    static List<RoutedRelation> CollectEligibleRoutedRelations(IEnumerable<JsonNode?>? relations, JsSet<string>? endpointIds, Func<JsonObject, List<Pt>?> pathFor)
    {
        var routed = new List<RoutedRelation>();
        var relationIndex = -1;
        foreach (var node in relations ?? [])
        {
            relationIndex++;
            if (node is not JsonObject relation || relation.Str("from") is not { } from || relation.Str("to") is not { } to) continue;
            if (endpointIds is not null && (!endpointIds.Has(from) || !endpointIds.Has(to))) continue;
            routed.Add(new(pathFor(relation), relation, relationIndex));
        }
        return routed;
    }

    static string SegmentPosition(int index, int segmentCount)
    {
        if (index == 0) return "source-stub";
        if (index == segmentCount - 1) return "target-stub";
        return "interior";
    }

    // Upstream memoizes normalization per input array (a WeakMap keyed by identity) and freezes the result; the port
    // recomputes — same points every time, no identity to key on.
    // Assembling a route from an anchor, corridor points and an anchor is the same
    // normalization without the intermediate array.
    public static List<Pt> JoinRoutePoints(Pt start, IEnumerable<Pt>? via, Pt end)
    {
        var normalized = new List<Pt>();
        void Accept(Pt point)
        {
            if (!IsFinitePoint(point)) return;
            if (normalized.Count > 0)
            {
                var previous = normalized[^1];
                if (Math.Abs(point.X - previous.X) <= 0.0001 && Math.Abs(point.Y - previous.Y) <= 0.0001) return;
            }
            while (normalized.Count >= 2 && CollinearForward(normalized[^2], normalized[^1], point)) normalized.RemoveAt(normalized.Count - 1);
            normalized.Add(point);
        }
        Accept(start);
        foreach (var point in via ?? []) Accept(point);
        Accept(end);
        return normalized;
    }

    public static List<Pt> NormalizeRoutePoints(IEnumerable<Pt>? points)
    {
        var normalized = new List<Pt>();
        foreach (var point in points ?? [])
        {
            if (!IsFinitePoint(point)) continue;
            if (normalized.Count > 0)
            {
                var previous = normalized[^1];
                if (Math.Abs(point.X - previous.X) <= 0.0001 && Math.Abs(point.Y - previous.Y) <= 0.0001) continue;
            }
            while (normalized.Count >= 2 && CollinearForward(normalized[^2], normalized[^1], point)) normalized.RemoveAt(normalized.Count - 1);
            normalized.Add(point);
        }
        return normalized;
    }

    static double PointRectDistance(Pt point, Rect rect)
    {
        var dx = Math.Max(Math.Max(rect.X - point.X, 0), point.X - (rect.X + rect.Width));
        var dy = Math.Max(Math.Max(rect.Y - point.Y, 0), point.Y - (rect.Y + rect.Height));
        return Js.Hypot(dx, dy);
    }

    static double PointSegmentDistanceXY(double px, double py, Pt start, Pt end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0.0000001) return Js.Hypot(px - start.X, py - start.Y);
        var t = Math.Max(0, Math.Min(1, ((px - start.X) * dx + (py - start.Y) * dy) / lengthSquared));
        return Js.Hypot(px - (start.X + t * dx), py - (start.Y + t * dy));
    }

    static bool CollinearForward(Pt a, Pt b, Pt c)
    {
        if (Math.Abs(CrossProduct(a, b, c)) > 0.0001) return false;
        return (b.X - a.X) * (c.X - b.X) + (b.Y - a.Y) * (c.Y - b.Y) >= -0.0001;
    }

    public static List<BorderSegment> FrameBorderSegments(Frame? frame)
    {
        if (frame is null) return [];
        if (frame.Shape == "line")
        {
            // ponytail: upstream also reads `{x1, y1, x2, y2}`; the port's Frame carries Start/End only.
            if (frame.Start is not { } start || frame.End is not { } end) return [];
            return IsFinitePoint(start) && IsFinitePoint(end) ? [new("line", start, end)] : [];
        }
        if (!IsFinitePoint(frame.X, frame.Y, frame.Width, frame.Height) || frame.Width <= 0 || frame.Height <= 0) return [];
        var radius = Math.Max(0, Math.Min(Math.Min(Or(frame.Radius, 0), frame.Width / 2), frame.Height / 2));
        var left = frame.X;
        var right = frame.X + frame.Width;
        var top = frame.Y;
        var bottom = frame.Y + frame.Height;
        BorderSegment[] sides =
        [
            new("top", new(left + radius, top), new(right - radius, top)),
            new("right", new(right, top + radius), new(right, bottom - radius)),
            new("bottom", new(right - radius, bottom), new(left + radius, bottom)),
            new("left", new(left, bottom - radius), new(left, top + radius)),
        ];
        return sides.Where(s => Js.Hypot(s.End.X - s.Start.X, s.End.Y - s.Start.Y) > 0.0001).ToList();
    }

    readonly record struct Overlap(double Length, Pt Start, Pt End);
    readonly record struct MergedOverlap(double Low, double High, double Length, Pt Start, Pt End);

    static List<MergedOverlap> MergeBorderOverlaps(List<Overlap> overlaps, BorderSegment border)
    {
        var horizontal = Math.Abs(border.Start.Y - border.End.Y) <= 0.0001;
        var axis = horizontal ? 0 : 1;
        var fixedCoord = horizontal ? border.Start.Y : border.Start.X;
        var sorted = overlaps.Select(overlap => (Low: Math.Min(Coord(overlap.Start, axis), Coord(overlap.End, axis)),
            High: Math.Max(Coord(overlap.Start, axis), Coord(overlap.End, axis)))).ToList();
        Js.Sort(sorted, (left, right) => left.Low - right.Low is var d && d != 0 ? d : left.High - right.High);
        var merged = new List<(double Low, double High)>();
        foreach (var interval in sorted)
        {
            if (merged.Count > 0 && interval.Low <= merged[^1].High + 0.0001) merged[^1] = (merged[^1].Low, Math.Max(merged[^1].High, interval.High));
            else merged.Add(interval);
        }
        return merged.Select(interval => new MergedOverlap(interval.Low, interval.High, interval.High - interval.Low,
            horizontal ? new(interval.Low, fixedCoord) : new(fixedCoord, interval.Low),
            horizontal ? new(interval.High, fixedCoord) : new(fixedCoord, interval.High))).ToList();
    }

    static Overlap? CollinearAxisOverlap(Pt a, Pt b, Pt c, Pt d)
    {
        const double epsilon = 0.0001;
        var horizontal = Math.Abs(a.Y - b.Y) <= epsilon
            && Math.Abs(c.Y - d.Y) <= epsilon
            && Math.Abs(a.Y - c.Y) <= epsilon;
        var vertical = Math.Abs(a.X - b.X) <= epsilon
            && Math.Abs(c.X - d.X) <= epsilon
            && Math.Abs(a.X - c.X) <= epsilon;
        if (!horizontal && !vertical) return null;
        var axis = horizontal ? 0 : 1;
        var low = Math.Max(Math.Min(Coord(a, axis), Coord(b, axis)), Math.Min(Coord(c, axis), Coord(d, axis)));
        var high = Math.Min(Math.Max(Coord(a, axis), Coord(b, axis)), Math.Max(Coord(c, axis), Coord(d, axis)));
        if (high - low <= epsilon) return null;
        var fixedCoord = horizontal ? a.Y : a.X;
        return new(high - low, horizontal ? new(low, fixedCoord) : new(fixedCoord, low), horizontal ? new(high, fixedCoord) : new(fixedCoord, high));
    }

    public static Pt? ProperSegmentIntersection(Pt a, Pt b, Pt c, Pt d)
    {
        var abC = CrossProduct(a, b, c);
        var abD = CrossProduct(a, b, d);
        var cdA = CrossProduct(c, d, a);
        var cdB = CrossProduct(c, d, b);
        const double epsilon = 0.0001;
        static bool Opposite(double left, double right) => (left > epsilon && right < -epsilon) || (left < -epsilon && right > epsilon);
        if (!Opposite(abC, abD) || !Opposite(cdA, cdB)) return null;

        var denominator = (a.X - b.X) * (c.Y - d.Y) - (a.Y - b.Y) * (c.X - d.X);
        if (Math.Abs(denominator) < epsilon) return null;
        var ab = a.X * b.Y - a.Y * b.X;
        var cd = c.X * d.Y - c.Y * d.X;
        return new(
            (ab * (c.X - d.X) - (a.X - b.X) * cd) / denominator,
            (ab * (c.Y - d.Y) - (a.Y - b.Y) * cd) / denominator);
    }

    static double CrossProduct(Pt a, Pt b, Pt c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    static bool PointInBox(Pt point, GapBox box) => point.X >= box.X1 && point.X <= box.X2 && point.Y >= box.Y1 && point.Y <= box.Y2;

    static bool SegmentsIntersect(Pt a, Pt b, Pt c, Pt d)
    {
        var o1 = Orientation(a, b, c);
        var o2 = Orientation(a, b, d);
        var o3 = Orientation(c, d, a);
        var o4 = Orientation(c, d, b);

        if (o1 == 0 && OnSegment(a, c, b)) return true;
        if (o2 == 0 && OnSegment(a, d, b)) return true;
        if (o3 == 0 && OnSegment(c, a, d)) return true;
        if (o4 == 0 && OnSegment(c, b, d)) return true;

        return o1 != o2 && o3 != o4;
    }

    static int Orientation(Pt a, Pt b, Pt c)
    {
        var value = (b.Y - a.Y) * (c.X - b.X) - (b.X - a.X) * (c.Y - b.Y);
        if (Math.Abs(value) < 0.0001) return 0;
        return value > 0 ? 1 : 2;
    }

    static bool OnSegment(Pt a, Pt b, Pt c) =>
        b.X <= Math.Max(a.X, c.X) &&
        b.X >= Math.Min(a.X, c.X) &&
        b.Y <= Math.Max(a.Y, c.Y) &&
        b.Y >= Math.Min(a.Y, c.Y);

    public static Pt Anchor(Box rect, string? side) => side switch
    {
        "left" => new(rect.X, rect.Cy),
        "right" => new(rect.X + rect.Width, rect.Cy),
        "top" => new(rect.Cx, rect.Y),
        "bottom" => new(rect.Cx, rect.Y + rect.Height),
        _ => new(rect.X + rect.Width, rect.Cy),
    };

    public static bool PortHasCornerClearance(Rect rect, string? side, Pt point)
    {
        if (side is "left" or "right")
        {
            var inset = Math.Min(16, rect.Height / 2);
            return point.Y >= rect.Y + inset && point.Y <= rect.Y + rect.Height - inset;
        }
        if (side is "top" or "bottom")
        {
            var inset = Math.Min(16, rect.Width / 2);
            return point.X >= rect.X + inset && point.X <= rect.X + rect.Width - inset;
        }
        return false;
    }

    // Reuse the same bounded alignment for facing automatic ports; callers own
    // obstacle and occupied-port checks for their renderer. Spread slots stay fixed.
    public static (Pt Start, Pt End) AlignAutomaticFacingPorts(JsonObject conn, Box from, Box to, Pt start, Pt end, string? fromSide, string? toSide,
        SpreadPorts? ports, Func<List<Pt>, bool>? accept = null)
    {
        var hasExplicitGeometry =
            conn["via"].Truthy()
            || (Truthy(conn.Str("route")) && conn.Str("route") != "auto")
            || conn.ContainsKey("channelX")
            || conn.ContainsKey("channelY")
            || conn["labelAt"].Truthy();
        var horizontallyFacing =
            (fromSide == "right" && toSide == "left")
            || (fromSide == "left" && toSide == "right");
        var verticallyFacing =
            (fromSide == "bottom" && toSide == "top")
            || (fromSide == "top" && toSide == "bottom");
        if (hasExplicitGeometry || (!horizontallyFacing && !verticallyFacing)) return (start, end);

        var fromSpread = ports?.From is not null;
        var toSpread = ports?.To is not null;
        if (fromSpread && toSpread) return (start, end);
        var hasExplicitSides =
            (Truthy(conn.Str("fromSide")) && conn.Str("fromSide") != "auto")
            || (Truthy(conn.Str("toSide")) && conn.Str("toSide") != "auto");
        if (!fromSpread && !toSpread && hasExplicitSides) return (start, end);

        var alignmentDelta = horizontallyFacing
            ? Math.Abs(start.Y - end.Y)
            : Math.Abs(start.X - end.X);
        if (alignmentDelta >= 16) return (start, end);

        // Keep the shared endpoint's distinct spread slot and move only the
        // relationship's unshared endpoint onto that axis. With no spread endpoint,
        // retain the existing least-movement choice between the two facing sides.
        // If both endpoints are shared, preserve the outside bridge so no competing
        // port is silently collapsed.
        var alignEndToStart = horizontallyFacing
            ? (Start: start, End: new Pt(end.X, start.Y))
            : (Start: start, End: new Pt(start.X, end.Y));
        var alignStartToEnd = horizontallyFacing
            ? (Start: new Pt(start.X, end.Y), End: end)
            : (Start: new Pt(end.X, start.Y), End: end);
        var candidates = fromSpread
            ? [alignEndToStart]
            : toSpread
                ? [alignStartToEnd]
                : new[] { alignEndToStart, alignStartToEnd };
        foreach (var candidate in candidates)
        {
            var points = new List<Pt> { candidate.Start, candidate.End };
            if (PortHasCornerClearance(from, fromSide, candidate.Start)
                && PortHasCornerClearance(to, toSide, candidate.End)
                && RouteHonorsEndpointSides(points, fromSide, toSide)
                && (accept is null || accept(points)))
                return candidate;
        }
        return (start, end);
    }

    static readonly Dictionary<string, Pt> PortOutwardVector = new(StringComparer.Ordinal)   // lookup only
    {
        ["left"] = new(-1, 0),
        ["right"] = new(1, 0),
        ["top"] = new(0, -1),
        ["bottom"] = new(0, 1),
    };

    // Automatic port spreading can put otherwise parallel anchors only a few
    // pixels apart. A conventional midpoint dogleg then violates the renderer's
    // own 8px/16px route-rhythm floors. Return a full outside-channel route when
    // that happens, or null when the normal automatic route remains appropriate.
    public static List<Pt>? AutomaticPortRhythmBridge(Pt start, Pt end, string? fromSide, string? toSide,
        double endpointStubPx = 24, double interiorSegmentPx = 16, Func<List<Pt>, bool>? accept = null)
    {
        if (!IsFinitePoint(start) || !IsFinitePoint(end)) return null;
        if (fromSide is null || toSide is null
            || !PortOutwardVector.TryGetValue(fromSide, out var fromVector) || !PortOutwardVector.TryGetValue(toSide, out var toVector)) return null;

        double? opposedFacingGap =
            (fromSide == "right" && toSide == "left" && end.X > start.X)
            || (fromSide == "left" && toSide == "right" && start.X > end.X)
                ? Math.Abs(end.X - start.X)
                : (fromSide == "bottom" && toSide == "top" && end.Y > start.Y)
                    || (fromSide == "top" && toSide == "bottom" && start.Y > end.Y)
                    ? Math.Abs(end.Y - start.Y)
                    : null;
        double? boundedStubPx = opposedFacingGap is { } gap
            && gap - endpointStubPx * 2 < interiorSegmentPx
            ? Math.Max(8, Math.Min(endpointStubPx, (gap - interiorSegmentPx) / 2))
            : null;
        var stubDistances = new List<double> { endpointStubPx };
        if (boundedStubPx is { } bounded && bounded >= 8 && bounded != endpointStubPx) stubDistances.Add(bounded);
        var candidates = new List<List<Pt>>();
        var verticalSides = fromSide is "top" or "bottom" && toSide is "top" or "bottom";
        var horizontalSides = fromSide is "left" or "right" && toSide is "left" or "right";

        foreach (var stubPx in stubDistances)
        {
            var startStub = new Pt(start.X + fromVector.X * stubPx, start.Y + fromVector.Y * stubPx);
            var endStub = new Pt(end.X + toVector.X * stubPx, end.Y + toVector.Y * stubPx);
            if (verticalSides && Math.Abs(start.X - end.X) < interiorSegmentPx)
            {
                foreach (var channelX in new[] { Math.Max(start.X, end.X) + interiorSegmentPx, Math.Min(start.X, end.X) - interiorSegmentPx })
                    candidates.Add([start, startStub, new(channelX, startStub.Y), new(channelX, endStub.Y), endStub, end]);
            }
            if (horizontalSides && Math.Abs(start.Y - end.Y) < interiorSegmentPx)
            {
                foreach (var channelY in new[] { Math.Max(start.Y, end.Y) + interiorSegmentPx, Math.Min(start.Y, end.Y) - interiorSegmentPx })
                    candidates.Add([start, startStub, new(startStub.X, channelY), new(endStub.X, channelY), endStub, end]);
            }
        }

        foreach (var candidate in candidates)
        {
            var points = NormalizeRoutePoints(candidate);
            if (RouteHonorsEndpointSides(points, fromSide, toSide)
                && CollectRouteRhythmIssues([new RoutedRelation(points)], interiorSegmentPx).Count == 0
                && (accept is null || accept(points)))
                return points;
        }
        return null;
    }

    sealed record PortItem(JsonObject Relation, string Endpoint, Box Rect, string Side, Box Counterpart);

    // Keep conservative auto-routed fan-out/fan-in relationships visually
    // distinct without changing authored route controls. The returned map only
    // contains endpoints that belong to a shared automatic midpoint anchor.
    public static JsMap<JsonObject, SpreadPorts> AutomaticPortSpread(JsonNode? relations, JsMap<string, Box> boxes, double gutter = 16, double maxSpacing = 14,
        Func<JsonObject, string, string?>? sideFor = null, Func<JsonObject, JsonObject, double>? spacingFor = null) =>
        AutomaticPortSpread(AsArray(relations), boxes, gutter, maxSpacing, sideFor, spacingFor);

    // The router holds its connections as a list of already-parented JsonObjects, which cannot be re-wrapped in a JsonArray.
    public static JsMap<JsonObject, SpreadPorts> AutomaticPortSpread(IEnumerable<JsonNode?> relations, JsMap<string, Box> boxes, double gutter = 16, double maxSpacing = 14,
        Func<JsonObject, string, string?>? sideFor = null, Func<JsonObject, JsonObject, double>? spacingFor = null)
    {
        var groups = new JsMap<string, List<PortItem>>();
        var spread = new JsMap<JsonObject, SpreadPorts>();

        void Add(JsonObject relation, string endpoint, Box rect, string side, Box counterpart)
        {
            var key = $"{rect.Id}\u0000{side}";
            var items = groups.Get(key) ?? [];
            items.Add(new(relation, endpoint, rect, side, counterpart));
            groups.Set(key, items);
        }

        foreach (var node in relations)
        {
            if (node is not JsonObject relation || (Truthy(relation.Str("route")) && relation.Str("route") != "auto")) continue;
            if (relation["via"].Truthy() || relation.ContainsKey("channelX") || relation.ContainsKey("channelY") || relation["labelAt"].Truthy()) continue;
            var from = relation.Str("from") is { } fromId ? boxes.Get(fromId) : null;
            var to = relation.Str("to") is { } toId ? boxes.Get(toId) : null;
            if (from is null || to is null) continue;
            var fromSide = ChosenSide(relation.Str("fromSide"), Truthy(sideFor?.Invoke(relation, "source")) ? sideFor!(relation, "source")! : DefaultFromSide(from, to));
            var toSide = ChosenSide(relation.Str("toSide"), Truthy(sideFor?.Invoke(relation, "target")) ? sideFor!(relation, "target")! : DefaultToSide(from, to));
            Add(relation, "from", from, fromSide, to);
            Add(relation, "to", to, toSide, from);
        }

        foreach (var items in groups.Values)
        {
            if (items.Count < 2) continue;
            var verticalSide = items[0].Side is "left" or "right";
            Js.Sort(items, (a, b) =>
            {
                var aCoordinate = verticalSide ? a.Counterpart.Cy : a.Counterpart.Cx;
                var bCoordinate = verticalSide ? b.Counterpart.Cy : b.Counterpart.Cx;
                if (aCoordinate != bCoordinate) return aCoordinate - bCoordinate;
                var aKey = $"{a.Relation.Str("id") ?? ""}\u0000{a.Relation.Str("from")}\u0000{a.Relation.Str("to")}\u0000{a.Relation.Str("label") ?? ""}";
                var bKey = $"{b.Relation.Str("id") ?? ""}\u0000{b.Relation.Str("from")}\u0000{b.Relation.Str("to")}\u0000{b.Relation.Str("label") ?? ""}";
                return string.CompareOrdinal(aKey, bKey) is var c && c < 0 ? -1 : c > 0 ? 1 : 0;
            });

            var extent = verticalSide ? items[0].Rect.Height : items[0].Rect.Width;
            var usable = Math.Max(0, extent - gutter * 2);
            var spacing = Math.Min(maxSpacing, usable / (items.Count - 1));
            if (!(spacing > 0)) continue;

            // Width-aware callers reserve the whole group together. Moving a single
            // port around the legacy 14px slots can wrongly report a full side while
            // its still-unrouted neighbours could have fitted farther apart.
            List<double>? offsets = null;
            if (spacingFor is not null)
            {
                var gaps = new List<double>();
                for (var index = 1; index < items.Count; index++) gaps.Add(Math.Max(maxSpacing, spacingFor(items[index - 1].Relation, items[index].Relation)));
                double span = 0;
                foreach (var gap in gaps) span += gap;
                if (span <= usable && gaps.Any(gap => gap > maxSpacing))
                {
                    var offset = -span / 2;
                    offsets = [offset];
                    foreach (var gap in gaps) offsets.Add(offset += gap);
                }
            }

            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                var offset = offsets is not null && index < offsets.Count ? offsets[index] : (index - (items.Count - 1) / 2.0) * spacing;
                var point = Anchor(item.Rect, item.Side);
                point = verticalSide ? point with { Y = point.Y + offset } : point with { X = point.X + offset };
                var endpoints = spread.Get(item.Relation) ?? new SpreadPorts();
                if (item.Endpoint == "from") endpoints.From = point; else endpoints.To = point;
                spread.Set(item.Relation, endpoints);
            }
        }

        return spread;
    }

    // A centre delta on the horizontal axis does not by itself mean the route
    // leaves sideways: a hub above an offset spoke has both a horizontal and a
    // larger vertical delta, and the router draws the vertical dogleg. Compare the
    // deltas so the inferred side matches the axis the route actually uses. Equal
    // deltas select horizontal sides; dx === 0 preserves the previous vertical
    // result, while a vertical-dominant nonzero dx selects vertical sides.
    public static string DefaultFromSide(Box from, Box to)
    {
        var dx = to.Cx - from.Cx;
        var dy = to.Cy - from.Cy;
        if (dx != 0 && Math.Abs(dx) >= Math.Abs(dy)) return dx < 0 ? "left" : "right";
        return dy > 0 ? "bottom" : "top";
    }

    public static string DefaultToSide(Box from, Box to)
    {
        var dx = to.Cx - from.Cx;
        var dy = to.Cy - from.Cy;
        if (dx != 0 && Math.Abs(dx) >= Math.Abs(dy)) return dx < 0 ? "right" : "left";
        return dy > 0 ? "top" : "bottom";
    }

    // Non-architecture renderers retain their established horizontal-first
    // endpoint contract. Architecture opts into dominant-axis inference above so
    // a vertical hub-and-spoke route can use perpendicular automatic ports.
    public static string LegacyDefaultFromSide(Box from, Box to)
    {
        if (to.Cx < from.Cx) return "left";
        if (to.Cx > from.Cx) return "right";
        if (to.Cy > from.Cy) return "bottom";
        return "top";
    }

    public static string LegacyDefaultToSide(Box from, Box to)
    {
        if (to.Cx < from.Cx) return "right";
        if (to.Cx > from.Cx) return "left";
        if (to.Cy > from.Cy) return "top";
        return "bottom";
    }

    public static string ChosenSide(string? side, string fallback) => Truthy(side) && side != "auto" ? side! : fallback;

    public static string PolylinePath(IReadOnlyList<Pt> points)
    {
        var sb = new StringBuilder();
        for (var index = 0; index < points.Count; index++)
        {
            if (index > 0) sb.Append(' ');
            sb.Append(index == 0 ? 'M' : 'L').Append(' ').Append(N(points[index].X)).Append(' ').Append(N(points[index].Y));
        }
        return sb.ToString();
    }

    public static string RoutePointsValue(IEnumerable<Pt>? points) =>
        string.Join(";", (points ?? []).Where(IsFinitePoint).Select(p => $"{N(p.X)},{N(p.Y)}"));

    // Only a direct, explicitly authored diagonal needs an artifact-check exception.
    // Nonempty via takes precedence; empty via adds no intermediate geometry.
    public static string AuthoredStraightRouteAttrs(JsonObject relation, IReadOnlyList<Pt> points)
    {
        if (relation.Str("route") != "straight" || (relation.Arr("via")?.Count ?? 0) > 0 || points.Count != 2) return "";
        var (start, end) = (points[0], points[1]);
        return Math.Abs(start.X - end.X) > 0.01 && Math.Abs(start.Y - end.Y) > 0.01
            ? " data-composition-route=\"straight\""
            : "";
    }

    public static string RoundedPath(IReadOnlyList<Pt> points, double radius)
    {
        if (points.Count < 3 || radius <= 0) return PolylinePath(points);

        var commands = new List<string> { $"M {N(points[0].X)} {N(points[0].Y)}" };
        for (var i = 1; i < points.Count - 1; i += 1)
        {
            var (px, py) = points[i - 1];
            var (cx, cy) = points[i];
            var (nx, ny) = points[i + 1];
            var prevLen = Js.Hypot(cx - px, cy - py);
            var nextLen = Js.Hypot(nx - cx, ny - cy);
            var r = Math.Min(Math.Min(radius, prevLen / 2), nextLen / 2);
            if (r < 1)
            {
                commands.Add($"L {N(cx)} {N(cy)}");
                continue;
            }
            var before = new Pt(cx - ((cx - px) / prevLen) * r, cy - ((cy - py) / prevLen) * r);
            var after = new Pt(cx + ((nx - cx) / nextLen) * r, cy + ((ny - cy) / nextLen) * r);
            commands.Add($"L {N(before.X)} {N(before.Y)}");
            commands.Add($"Q {N(cx)} {N(cy)} {N(after.X)} {N(after.Y)}");
        }
        var (endX, endY) = points[^1];
        commands.Add($"L {N(endX)} {N(endY)}");
        return string.Join(" ", commands);
    }

    // Shared by edges/flows/transitions: all carry the same optional
    // labelAt/labelDx/labelDy/labelSegment knobs.
    public static Pt LabelPoint(JsonObject item, IReadOnlyList<Pt> points)
    {
        if (item["labelAt"].Truthy() && item["labelAt"] is JsonArray at) return new(at[0]!.D(), at[1]!.D());
        var labelDx = item.Num("labelDx") ?? 0;
        var labelDy = item.Num("labelDy") ?? 0;
        if (points.Count == 2)
            return new(
                (points[0].X + points[1].X) / 2 + labelDx,
                points[0].Y - 10 + labelDy);
        var segmentIndex = (int)Math.Min(points.Count - 2, Math.Max(0, item.Num("labelSegment") ?? 1));
        var a = points[segmentIndex];
        var b = points[segmentIndex + 1];
        return new((a.X + b.X) / 2 + labelDx, (a.Y + b.Y) / 2 - 10 + labelDy);
    }

    public static readonly IReadOnlyDictionary<string, string> ComponentFill = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["frontend"] = "c-frontend",
        ["backend"] = "c-backend",
        ["database"] = "c-database",
        ["cloud"] = "c-cloud",
        ["security"] = "c-security",
        ["messagebus"] = "c-messagebus",
        ["external"] = "c-external",
    };

    public static readonly IReadOnlyDictionary<string, string> ComponentText = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["frontend"] = "t-frontend",
        ["backend"] = "t-backend",
        ["database"] = "t-database",
        ["cloud"] = "t-cloud",
        ["security"] = "t-security",
        ["messagebus"] = "t-messagebus",
        ["external"] = "t-external",
    };

    public static readonly IReadOnlyDictionary<string, (string Path, string Marker)> ArrowClassMap = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["default"] = ("a-default", "arrowhead"),
        ["emphasis"] = ("a-emphasis", "arrowhead-emphasis"),
        ["security"] = ("a-security", "arrowhead-security"),
        ["dashed"] = ("a-dashed", "arrowhead-dashed"),
    };

    // Structural phase/group accents retain their existing semantic colors.
    public static string VariantAccent(string? variant) =>
        variant == "security" ? "t-security"
        : variant == "emphasis" ? "t-backend"
            : variant == "dashed" ? "t-messagebus" : "t-muted";

    // Relationship labels use the same theme token as their path. Keep this map
    // edge-specific: node-kind text colors only coincide with some path colors in
    // the classic preset and must not define the relationship's visual meaning.
    public static string EdgeLabelAccent(string? variant) =>
        variant == "security"
            ? "t-edge-security"
            : variant == "emphasis"
                ? "t-edge-emphasis"
                : variant == "dashed"
                    ? "t-edge-dashed"
                    : "t-edge-default";

    public static string FormatRect(Rect r) => $"[{N(Js.Round(r.X))}, {N(Js.Round(r.Y))}, {N(Js.Round(r.Width))}, {N(Js.Round(r.Height))}]";

    static string FormatValue(double n) => N(Js.Round(n));

    // A label anchor is the text origin, so keeping a suggestion on the canvas
    // means moving the whole rect: offset is `rect - anchor` on that axis.
    static bool AnchorFitsCanvas(double anchorValue, double offset, double size, double extent)
    {
        var overflow = AxisCanvasOverflow(anchorValue + offset, size, extent);
        return overflow.Start <= 0 && overflow.End <= 0;
    }

    // Suggestions are emitted as integers, so the real-valued bounds are tightened
    // inward with ceil/floor first: every integer between them keeps the whole rect
    // on the canvas, which a bound like 616.7 would not. Returns null when the rect
    // is wider than the canvas and no anchor can contain it.
    static double? ClampAnchorToCanvas(double anchorValue, double offset, double size, double extent)
    {
        var rounded = Js.Round(anchorValue);
        if (!double.IsFinite(extent)) return rounded;
        var min = Math.Ceiling(-offset);
        var max = Math.Floor(extent - size - offset);
        return max < min ? null : Math.Min(Math.Max(rounded, min), max);
    }

    /// <summary>
    /// Actionable hint when an edge label rect hits a node/component box (#7).
    /// Every suggested value is a replacement for the authored field, not an
    /// increment, and has to survive being applied to the document:
    /// - the absolute form is nudged along x so the rect stays on the canvas, and a
    ///   vertical placement that cannot fit is dropped rather than clamped, since
    ///   clamping it back would push the label onto the obstacle it must clear;
    /// - the relative form is offered only when labelDx/labelDy actually move this
    ///   label (labelPoint returns an authored labelAt as-is) and only when the
    ///   replacements, computed from the document's own labelDx/labelDy, land the
    ///   rect inside the canvas.
    /// The authored values therefore come from `labelRect.relation`, the authored
    /// relationship the renderer already attaches to every label record.
    /// `obstacles` is every box the callers' own overlap loop tests (defaults to
    /// the named obstacle alone): a placement that merely traded the named obstacle
    /// for its neighbor would fail the same rule again when applied.
    /// </summary>
    public static string SuggestLabelObstacleFix(LabelRect labelRect, double lx, double ly, Box obstacle, string obstacleKind = "component",
        IReadOnlyList<double>? viewBox = null, IEnumerable<Box>? obstacles = null)
    {
        // Renderer canvases are origin-zero [width, height] by schema, but read a
        // 4-number [min-x, min-y, width, height] the same way the containment
        // collector does — degraded no-ajv runs must not clamp against the origin
        // pair as if it were the canvas size.
        var box = viewBox ?? [];
        var (canvasWidth, canvasHeight) = box.Count == 4 ? (box[2], box[3]) : (box.Count > 0 ? box[0] : double.NaN, box.Count > 1 ? box[1] : double.NaN);
        var xOffset = labelRect.X - lx;
        var yOffset = labelRect.Y - ly;
        var anchorX = ClampAnchorToCanvas(lx, xOffset, labelRect.Width, canvasWidth);
        // The placement anchors are derived from the label's own rect, not from the
        // obstacle alone: an obstacle-only "above" anchor assumes the 14px
        // single-line rect and lands a 27px two-line rect (dataflow classification,
        // lifecycle note) back on the obstacle it must clear. The overlap filter
        // uses the callers' own detection call, so a surviving hint cannot re-raise
        // the problem it repairs.
        var blockers = obstacles is not null ? obstacles.ToList() : [obstacle];
        var placements = new List<(string Name, double Y)>
        {
            ("below", Js.Round(obstacle.Y + obstacle.Height + 4 - yOffset)),
            ("above", Js.Round(obstacle.Y - 4 - labelRect.Height - yOffset)),
        }.Where(p => anchorX is { } ax
            && AnchorFitsCanvas(p.Y, yOffset, labelRect.Height, canvasHeight)
            && blockers.All(blocker => !RectsOverlap(
                new Rect(ax + xOffset, p.Y + yOffset, labelRect.Width, labelRect.Height),
                blocker,
                -2))).ToList();
        var lines = new List<string>
        {
            $"  label rect: {FormatRect(labelRect)}",
            $"  {obstacleKind} \"{obstacle.Id}\" rect: {FormatRect(obstacle)}",
        };
        if (placements.Count == 0)
        {
            // Only the vertical slots beside the named obstacle were tried, so an
            // unclaimed spot elsewhere may still exist — never assert that none does.
            lines.Add(anchorX is null
                ? $"  Suggested fix: the {N(Js.Round(labelRect.Width))}px label rect cannot fit the {N(canvasWidth)}x{N(canvasHeight)} viewBox at any anchor — shorten the label or enlarge meta.viewBox"
                : $"  Suggested fix: no placement directly above or below \"{obstacle.Id}\" stays clear inside the {N(canvasWidth)}x{N(canvasHeight)} viewBox — move the label to an open area with labelAt, shorten the label, move the {obstacleKind}, or enlarge meta.viewBox");
            return string.Join("\n", lines);
        }
        var authored = labelRect.Relation;
        var authoredDx = authored.Num("labelDx") is { } adx && double.IsFinite(adx) ? adx : 0;
        var authoredDy = authored.Num("labelDy") is { } ady && double.IsFinite(ady) ? ady : 0;
        string? RelativeHint(double y)
        {
            if (authored.Arr("labelAt") is not null) return null;
            // Both replacements are measured from the automatic label point that the
            // authored offsets are applied to, and against the unrounded anchor: a
            // rounded base would shift the applied label by up to a pixel.
            var dx = Js.Round(authoredDx + anchorX!.Value - lx);
            var dy = Js.Round(authoredDy + y - ly);
            // labelDx is authored as a number, so an authored fraction must not read as
            // a needed nudge once the replacement is rounded to an integer.
            var movesX = Math.Abs(dx - authoredDx) >= 0.5;
            var landsAtX = lx - authoredDx + (movesX ? dx : authoredDx);
            var landsAtY = ly - authoredDy + dy;
            if (!AnchorFitsCanvas(landsAtX, xOffset, labelRect.Width, canvasWidth)) return null;
            if (!AnchorFitsCanvas(landsAtY, yOffset, labelRect.Height, canvasHeight)) return null;
            return movesX
                ? $"set labelDx {FormatValue(dx)} with labelDy {FormatValue(dy)}"
                : $"set labelDy {FormatValue(dy)}";
        }
        var hint = string.Join("; or ", placements.Select(p =>
        {
            var relative = RelativeHint(p.Y);
            return $"set labelAt [{N(anchorX!.Value)}, {N(p.Y)}]{(relative is not null ? $" or {relative}" : "")} ({p.Name})";
        }));
        lines.Add($"  Suggested fix: {hint}");
        return string.Join("\n", lines);
    }

    /// <summary>Hint when two edge labels collide.</summary>
    public static string SuggestLabelPairFix(LabelRect a, LabelRect b) => string.Join("\n",
    [
        $"  \"{S(a.Label)}\" {FormatRect(a)}; \"{S(b.Label)}\" {FormatRect(b)}",
        "  Suggested fix: adjust labelDx/labelDy/labelSegment, or route one relationship through a separate corridor",
    ]);

    /// <summary>
    /// The two placements that separate b from a, in the order the prose hint
    /// offers them. Shared so the text hint and its structured repair cannot
    /// offer different coordinates.
    /// </summary>
    public static List<SeparationOption> ComponentSeparationOptions(Box a, Box b, double minGap = 8) =>
        // Ceil, not round: on fractional geometry a rounded right/below coordinate
        // can land a fraction of a pixel short of minGap, and the advised position
        // would never clear the diagnostic that offered it.
        [
            new(new(Math.Ceiling(a.X + a.Width + minGap), Js.Round(b.Y)), $"right of \"{a.Id}\""),
            new(new(Js.Round(b.X), Math.Ceiling(a.Y + a.Height + minGap)), "below"),
        ];

    /// <summary>Hint when two components/nodes are too close.</summary>
    public static string SuggestComponentSeparation(Box a, Box b, double minGap = 8)
    {
        var options = ComponentSeparationOptions(a, b, minGap);
        var (right, below) = (options[0], options[1]);
        return string.Join("\n",
        [
            $"  \"{a.Id}\" {FormatRect(a)}; \"{b.Id}\" {FormatRect(b)}",
            $"  Suggested fix: move \"{b.Id}\" pos to [{N(right.Pos.X)}, {N(right.Pos.Y)}] ({right.Relation}) or [{N(below.Pos.X)}, {N(below.Pos.Y)}] ({below.Relation})",
        ]);
    }
}
