using System.Text.Json.Nodes;

namespace Archify.Net;

// routing.mjs. Internal architecture router shared by rendering and geometry inspection.
// Create a new router when measured boxes or connections change: port spreading
// is computed once and route results are cached for this scene.

// Default label rect of a routed relationship given the routes and label rects resolved so far (routing.mjs:36-39).
public delegate LabelRect? LabelRectResolver(JsonObject conn, List<Pt> points, List<List<Pt>> routes, List<LabelRect> labels);
// Candidate interior point families tried before the shared ones (routing.mjs:47-49).
public delegate IEnumerable<List<Pt>>? PreferredCandidatesResolver(JsonObject conn, Box from, Box to, Pt start, Pt end, string fromSide, string toSide);

// createRouter's destructured options (routing.mjs:76-90) with their defaults.
public sealed record RouterOptions(
    IReadOnlyList<Frame>? Frames = null,
    double InteriorSegmentPx = 16,
    double MicroSegmentPx = 8,
    LabelRectResolver? LabelRectFor = null,
    bool DistinctAutomaticPorts = false,
    bool PreferReadableRoutes = false,
    Func<JsonObject, string, string?>? SideFor = null,
    double? MaxPortSpacing = null,
    PreferredCandidatesResolver? PreferredCandidates = null,
    bool CompositionFloors = true,
    bool CrossingFreeGridFirst = false,
    double ComponentGapPx = 0,
    double MinimumTerminalSegmentPx = 0,
    double ReadabilityBottomMargin = 0);

// `{ d, points }` as pathFor returns it.
public sealed record RoutedPath(string D, List<Pt> Points);
public sealed record Sides(string FromSide, string ToSide);

public static class Routing
{
    public static Router CreateRouter(JsMap<string, Box> components, IReadOnlyList<JsonObject> connections, RouterOptions? options = null) =>
        new(components, connections, options ?? new RouterOptions());
}

public sealed class Router
{
    // ---- captured closure state ----------------------------------------------------------------------------------------
    readonly JsMap<string, Box> components;
    readonly IReadOnlyList<JsonObject> connections;
    readonly IReadOnlyList<Frame> frames;
    readonly double interiorSegmentPx;
    readonly double microSegmentPx;
    readonly LabelRectResolver? labelRectFor;
    readonly bool distinctAutomaticPorts;
    readonly bool preferReadableRoutes;
    readonly Func<JsonObject, string, string?>? sideFor;
    readonly PreferredCandidatesResolver? preferredCandidates;
    readonly bool compositionFloors;
    readonly bool crossingFreeGridFirst;
    readonly double minimumTerminalSegmentPx;
    readonly double readabilityBottomMargin;
    readonly List<BorderSegment> frameBorders;
    const double LABEL_CLEARANCE = 4;
    const double AUTOMATIC_PORT_CORNER_GUTTER = 16;
    static readonly string[] SIDE_ORDER = ["right", "bottom", "left", "top"];

    sealed class ResolvedRoute(JsonObject conn, List<Pt> points)
    {
        public JsonObject Conn { get; } = conn;
        public List<Pt> Points { get; set; } = points;
    }
    sealed record ReservedLabel(JsonObject Conn, LabelRect Rect);
    sealed record ConnGeometry(Box From, Box To, Pt Start, Pt End, string FromSide, string ToSide, bool Crowded);
    sealed record EndpointChoice(Pt Point, bool Spread, bool Crowded = false);
    sealed record RouteChoice(RoutedPath Routed, Sides Sides);
    sealed record IncidentEndpoint(JsonObject Conn, string Field, string SideField);

    // Labels of already-routed relationships, reserved while planning the rest.
    List<ReservedLabel> reservedLabels = [];
    bool honourReservedLabels = true;
    bool allowGridSearch = true;

    sealed class PlanningMetrics
    {
        public int RouteCount, ExplicitRouteCount, AutomaticRouteCount, GridSearchCount, GridRoutedCount, GridCandidateNodeCount,
            GridUsableNodeCount, GridEdgeCount, GridVisitedNodeCount, AvoidedSegmentCount, ConflictFallbackCount;
        public int MaximumGridSearchCount = 64;
        public int GridBudgetExhaustedCount, CrossoverRoutedCount, ReadabilityCandidateCount, ReadabilityImprovedCount,
            LabelRelocationCandidateCount, LabelRelocationImprovedCount, ReciprocalCandidateCount, ReciprocalImprovedCount;
        public List<JsonObject> GridAttempts = [];
        public JsonObject ToJson() => new()
        {
            ["routeCount"] = RouteCount, ["explicitRouteCount"] = ExplicitRouteCount, ["automaticRouteCount"] = AutomaticRouteCount,
            ["gridSearchCount"] = GridSearchCount, ["gridRoutedCount"] = GridRoutedCount, ["gridCandidateNodeCount"] = GridCandidateNodeCount,
            ["gridUsableNodeCount"] = GridUsableNodeCount, ["gridEdgeCount"] = GridEdgeCount, ["gridVisitedNodeCount"] = GridVisitedNodeCount,
            ["avoidedSegmentCount"] = AvoidedSegmentCount, ["conflictFallbackCount"] = ConflictFallbackCount,
            ["maximumGridSearchCount"] = MaximumGridSearchCount, ["gridBudgetExhaustedCount"] = GridBudgetExhaustedCount,
            ["crossoverRoutedCount"] = CrossoverRoutedCount, ["readabilityCandidateCount"] = ReadabilityCandidateCount,
            ["readabilityImprovedCount"] = ReadabilityImprovedCount, ["labelRelocationCandidateCount"] = LabelRelocationCandidateCount,
            ["labelRelocationImprovedCount"] = LabelRelocationImprovedCount, ["reciprocalCandidateCount"] = ReciprocalCandidateCount,
            ["reciprocalImprovedCount"] = ReciprocalImprovedCount,
            ["gridAttempts"] = new JsonArray([.. GridAttempts.Select(a => (JsonNode?)a.DeepClone())]),
        };
    }
    readonly PlanningMetrics planningMetrics = new();
    readonly double[] gridComponentGaps;

    readonly JsMap<string, List<Box>> neighbourRects = new();
    readonly JsMap<JsonObject, RoutedPath> pathCache = new();
    readonly JsMap<JsonObject, Sides> selectedSides = new();
    readonly JsMap<JsonObject, SpreadPorts> automaticPorts;
    readonly JsMap<string, List<IncidentEndpoint>> incidentEndpoints = new();
    bool routesPlanned;
    bool routesPlanning;

    internal Router(JsMap<string, Box> components, IReadOnlyList<JsonObject> connections, RouterOptions options)
    {
        this.components = components;
        this.connections = connections;
        frames = options.Frames ?? [];
        interiorSegmentPx = options.InteriorSegmentPx;
        microSegmentPx = options.MicroSegmentPx;
        labelRectFor = options.LabelRectFor;
        distinctAutomaticPorts = options.DistinctAutomaticPorts;
        preferReadableRoutes = options.PreferReadableRoutes;
        sideFor = options.SideFor;
        preferredCandidates = options.PreferredCandidates;
        compositionFloors = options.CompositionFloors;
        crossingFreeGridFirst = options.CrossingFreeGridFirst;
        minimumTerminalSegmentPx = options.MinimumTerminalSegmentPx;
        readabilityBottomMargin = options.ReadabilityBottomMargin;
        frameBorders = frames.SelectMany(frame => Geometry.FrameBorderSegments(frame)).ToList();
        gridComponentGaps = options.ComponentGapPx > 0 ? [options.ComponentGapPx, 0] : [0];

        // A node's neighbours laid out as one row below (or above) it read as a
        // fan-out: reach them all through the same vertical side. Center-based
        // inference alone sends the outer ones sideways around their siblings.
        foreach (var conn in connections)
        {
            var from = Component(conn.Str("from"));
            var to = Component(conn.Str("to"));
            if (from is null || to is null || ReferenceEquals(from, to)) continue;
            foreach (var (node, other) in new[] { (from, to), (to, from) })
            {
                var list = new List<Box>(neighbourRects.Get(node.Id) ?? []) { other };
                neighbourRects.Set(node.Id, list);
            }
        }

        automaticPorts = Geometry.AutomaticPortSpread(connections, components, 16, options.MaxPortSpacing ?? 14,
            InferredSide,
            // Preserve the established initial placement for ordinary markers; only
            // widen groups whose arrowheads cannot fit the legacy 14px slots.
            distinctAutomaticPorts ? (left, right) => MarkerSpacing(left, right) > 14 ? PortSpacing(left, right) : 14 : null);

        foreach (var conn in connections)
        {
            if (!components.Has(conn.Str("from") ?? "") || !components.Has(conn.Str("to") ?? "")) continue;
            foreach (var (field, sideField) in new[] { ("from", "fromSide"), ("to", "toSide") })
            {
                var entries = incidentEndpoints.Get(conn.Str(field)!) ?? [];
                entries.Add(new(conn, field, sideField));
                incidentEndpoints.Set(conn.Str(field)!, entries);
            }
        }
    }

    // ---- small helpers -------------------------------------------------------------------------------------------------
    Box? Component(string? id) => id is null ? null : components.Get(id);
    static double Coord(Pt p, int axis) => axis == 0 ? p.X : p.Y;
    static Pt WithCoord(Pt p, int axis, double value) => axis == 0 ? p with { X = value } : p with { Y = value };
    static List<Pt> Join(Pt start, IEnumerable<Pt> interior, Pt end) => [start, .. interior, end];
    static List<Pt> Interior(List<Pt> points) => points.Count <= 2 ? [] : points.GetRange(1, points.Count - 2);
    static bool Truthy(string? s) => !string.IsNullOrEmpty(s);
    static bool AuthoredSide(JsonObject conn, string field) => Truthy(conn.Str(field)) && conn.Str(field) != "auto";
    static double Span(Pt a, Pt b) => Math.Abs(b.X - a.X) + Math.Abs(b.Y - a.Y);
    static double RouteLength(List<Pt> points)
    {
        double total = 0;
        for (var index = 1; index < points.Count; index++) total += Span(points[index - 1], points[index]);
        return total;
    }
    // JS `a || b || c` over numeric comparator results: the first non-zero (non-NaN) term.
    static double FirstNonZero(double a, double b) => a != 0 && !double.IsNaN(a) ? a : b;
    static double FirstNonZero(double a, double b, double c) => FirstNonZero(a, FirstNonZero(b, c));
    static bool SameSides(Sides a, Sides b) => a.FromSide == b.FromSide && a.ToSide == b.ToSide;
    static bool SamePoints(List<Pt> a, List<Pt> b) => a.Count == b.Count && a.Zip(b).All(pair => pair.First.X == pair.Second.X && pair.First.Y == pair.Second.Y);

    bool RouteClearsReservedLabels(JsonObject conn, List<Pt> points)
    {
        if (!honourReservedLabels) return true;
        foreach (var entry in reservedLabels)
        {
            if (ReferenceEquals(entry.Conn, conn)) continue;
            for (var index = 0; index < points.Count - 1; index += 1)
                if (Geometry.SegmentIntersectsRect(new(points[index], points[index + 1]), entry.Rect, LABEL_CLEARANCE)) return false;
        }
        return true;
    }

    // The grid search adds its own 2px component clearance; pre-expand so a
    // reserved label keeps the same 4px clearance the placement pass demands.
    List<Rect> ReservedLabelObstacles(double gridClearance)
    {
        if (!honourReservedLabels) return [];
        var grow = LABEL_CLEARANCE - gridClearance;
        return reservedLabels.Select(entry => new Rect(entry.Rect.X - grow, entry.Rect.Y - grow, entry.Rect.Width + grow * 2, entry.Rect.Height + grow * 2)).ToList();
    }

    // Automatic routes are held to the same composition floors the showcase
    // gate enforces afterwards. Accepting a route here that the gate rejects
    // only hands the author a hand-routing repair the planner could have made.
    bool RouteMeetsCompositionFloors(List<Pt> points)
    {
        if (minimumTerminalSegmentPx > 0 && points.Count > 2)
        {
            if (Span(points[0], points[1]) < minimumTerminalSegmentPx || Span(points[^2], points[^1]) < minimumTerminalSegmentPx) return false;
        }
        if (compositionFloors && Geometry.CollectRouteRhythmIssues([new RoutedRelation(points)], interiorSegmentPx, microSegmentPx).Count > 0) return false;
        return frames.Count == 0 || Geometry.CollectBorderRuns([new RoutedRelation(points)], frames).Count == 0;
    }

    // ---- Connection routing ------------------------------------------------------
    // The spaced search is a readability preference, so its route must also be
    // clean as drawn: full rhythm floors (even for a type that turns them off)
    // and terminal segments long enough for a rounded corner and an end marker.
    bool SpacedRouteIsClean(List<Pt> points, double gap)
    {
        if (gap == 0) return true;
        return Span(points[0], points[1]) >= 16 && Span(points[^2], points[^1]) >= 16
            && Geometry.CollectRouteRhythmIssues([new RoutedRelation(points)], interiorSegmentPx, microSegmentPx).Count == 0;
    }

    List<Rect> GridComponentObstacles(JsonObject conn, double gap) =>
        components.Values.Select(component => gap == 0 || component.Id == conn.Str("from") || component.Id == conn.Str("to")
            ? (Rect)component
            : new Rect(component.X - gap, component.Y - gap, component.Width + gap * 2, component.Height + gap * 2)).ToList();

    bool RouteClearsComponents(JsonObject conn, List<Pt> points, double clearance = 2)
    {
        var from = conn.Str("from");
        var to = conn.Str("to");
        foreach (var component in components.Values)
        {
            if (component.Id == from || component.Id == to) continue;
            for (var index = 0; index < points.Count - 1; index += 1)
                if (Geometry.SegmentIntersectsRect(new(points[index], points[index + 1]), component, clearance)) return false;
        }
        return RouteClearsReservedLabels(conn, points);
    }

    static bool RouteClearsEndpointComponents(List<Pt> points, Box from, Box to)
    {
        var lastSegment = points.Count - 2;
        for (var index = 0; index <= lastSegment; index += 1)
        {
            var segment = new Segment(points[index], points[index + 1]);
            if (index > 0 && Geometry.SegmentIntersectsRect(segment, from)) return false;
            if (index < lastSegment && Geometry.SegmentIntersectsRect(segment, to)) return false;
        }
        return true;
    }

    static bool RelationshipsShareEndpoint(JsonObject left, JsonObject right) =>
        left.Str("from") == right.Str("from")
        || left.Str("from") == right.Str("to")
        || left.Str("to") == right.Str("from")
        || left.Str("to") == right.Str("to");

    static double CollinearOverlapLength(Pt leftStart, Pt leftEnd, Pt rightStart, Pt rightEnd)
    {
        const double epsilon = 0.0001;
        if (Math.Abs(leftStart.X - leftEnd.X) <= epsilon
            && Math.Abs(rightStart.X - rightEnd.X) <= epsilon
            && Math.Abs(leftStart.X - rightStart.X) <= epsilon)
        {
            return Math.Max(0,
                Math.Min(Math.Max(leftStart.Y, leftEnd.Y), Math.Max(rightStart.Y, rightEnd.Y))
                    - Math.Max(Math.Min(leftStart.Y, leftEnd.Y), Math.Min(rightStart.Y, rightEnd.Y)));
        }
        if (Math.Abs(leftStart.Y - leftEnd.Y) <= epsilon
            && Math.Abs(rightStart.Y - rightEnd.Y) <= epsilon
            && Math.Abs(leftStart.Y - rightStart.Y) <= epsilon)
        {
            return Math.Max(0,
                Math.Min(Math.Max(leftStart.X, leftEnd.X), Math.Max(rightStart.X, rightEnd.X))
                    - Math.Max(Math.Min(leftStart.X, leftEnd.X), Math.Min(rightStart.X, rightEnd.X)));
        }
        return 0;
    }

    static bool OrthogonalTouchOnResolvedInterior(Pt start, Pt end, Pt resolvedStart, Pt resolvedEnd)
    {
        const double epsilon = 0.0001;
        var candidateHorizontal = Math.Abs(start.Y - end.Y) <= epsilon;
        var candidateVertical = Math.Abs(start.X - end.X) <= epsilon;
        var resolvedHorizontal = Math.Abs(resolvedStart.Y - resolvedEnd.Y) <= epsilon;
        var resolvedVertical = Math.Abs(resolvedStart.X - resolvedEnd.X) <= epsilon;
        if (candidateHorizontal && resolvedVertical)
        {
            var x = resolvedStart.X;
            var y = start.Y;
            return x >= Math.Min(start.X, end.X) - epsilon
                && x <= Math.Max(start.X, end.X) + epsilon
                && y > Math.Min(resolvedStart.Y, resolvedEnd.Y) + epsilon
                && y < Math.Max(resolvedStart.Y, resolvedEnd.Y) - epsilon;
        }
        if (candidateVertical && resolvedHorizontal)
        {
            var x = start.X;
            var y = resolvedStart.Y;
            return y >= Math.Min(start.Y, end.Y) - epsilon
                && y <= Math.Max(start.Y, end.Y) + epsilon
                && x > Math.Min(resolvedStart.X, resolvedEnd.X) + epsilon
                && x < Math.Max(resolvedStart.X, resolvedEnd.X) - epsilon;
        }
        return false;
    }

    static List<ResolvedRoute> UnrelatedResolvedRoutes(JsonObject conn, List<ResolvedRoute> resolvedRoutes) =>
        resolvedRoutes.Where(entry => !RelationshipsShareEndpoint(conn, entry.Conn)).ToList();

    static bool RouteConflictsWithResolved(JsonObject conn, List<Pt> points, List<ResolvedRoute> resolvedRoutes)
    {
        foreach (var entry in UnrelatedResolvedRoutes(conn, resolvedRoutes))
        {
            for (var left = 0; left < points.Count - 1; left += 1)
            {
                for (var right = 0; right < entry.Points.Count - 1; right += 1)
                {
                    if (Geometry.ProperSegmentIntersection(points[left], points[left + 1], entry.Points[right], entry.Points[right + 1]) is not null) return true;
                    if (OrthogonalTouchOnResolvedInterior(points[left], points[left + 1], entry.Points[right], entry.Points[right + 1])) return true;
                    if (CollinearOverlapLength(points[left], points[left + 1], entry.Points[right], entry.Points[right + 1]) >= 8) return true;
                }
            }
        }
        return false;
    }

    bool RouteOverlapsResolved(JsonObject conn, List<Pt> points, List<ResolvedRoute> resolvedRoutes)
    {
        // A common destination does not make two independently labelled routes a
        // bus. Keep their corridors distinct too; explicit routes bypass planning.
        foreach (var entry in resolvedRoutes)
        {
            if (RelationshipsShareEndpoint(conn, entry.Conn)
                && (!distinctAutomaticPorts || HasAuthoredRouteGeometry(entry.Conn) || entry.Conn["labelAt"].Truthy() || conn["labelAt"].Truthy())) continue;
            for (var left = 0; left < points.Count - 1; left += 1)
                for (var right = 0; right < entry.Points.Count - 1; right += 1)
                    if (CollinearOverlapLength(points[left], points[left + 1], entry.Points[right], entry.Points[right + 1]) >= 8) return true;
        }
        return false;
    }

    static Pt OutwardStub(Pt point, string? side, double distance = 24)
    {
        var (dx, dy) = side switch { "left" => (-1.0, 0.0), "right" => (1.0, 0.0), "top" => (0.0, -1.0), "bottom" => (0.0, 1.0), _ => (0.0, 0.0) };
        return new(point.X + dx * distance, point.Y + dy * distance);
    }

    static bool CollinearBacktrack(Pt a, Pt b, Pt c)
    {
        var first = (X: b.X - a.X, Y: b.Y - a.Y);
        var second = (X: c.X - b.X, Y: c.Y - b.Y);
        var cross = first.X * second.Y - first.Y * second.X;
        var dot = first.X * second.X + first.Y * second.Y;
        return Math.Abs(cross) <= 0.0001 && dot < -0.0001;
    }

    static List<List<Pt>> SideAwareBridgeCandidates(Pt start, Pt end, string? fromSide, string? toSide)
    {
        var startStub = OutwardStub(start, fromSide);
        var endStub = OutwardStub(end, toSide);
        var rawCandidates = new List<List<Pt>>();
        const double minimumBridge = 16;
        var verticalFrom = fromSide is "top" or "bottom";
        var verticalTo = toSide is "top" or "bottom";
        var horizontalFrom = fromSide is "left" or "right";
        var horizontalTo = toSide is "left" or "right";

        // Port spreading can leave parallel-side anchors only a few pixels apart.
        // Route through a bounded outside channel so we keep both endpoint normals
        // without introducing a tiny, noisy connector between the two stubs.
        if (verticalFrom && verticalTo && Math.Abs(start.X - end.X) < minimumBridge)
        {
            foreach (var channelX in new[] { Math.Max(start.X, end.X) + minimumBridge, Math.Min(start.X, end.X) - minimumBridge })
                rawCandidates.Add([startStub, new(channelX, startStub.Y), new(channelX, endStub.Y), endStub]);
        }
        if (horizontalFrom && horizontalTo && Math.Abs(start.Y - end.Y) < minimumBridge)
        {
            foreach (var channelY in new[] { Math.Max(start.Y, end.Y) + minimumBridge, Math.Min(start.Y, end.Y) - minimumBridge })
                rawCandidates.Add([startStub, new(startStub.X, channelY), new(endStub.X, channelY), endStub]);
        }

        rawCandidates.Add([startStub, new(endStub.X, startStub.Y), endStub]);
        rawCandidates.Add([startStub, new(startStub.X, endStub.Y), endStub]);
        var normalized = rawCandidates.Select(candidate => Geometry.NormalizeRoutePoints(Join(start, candidate, end)))
            .Where(points => points.Count >= 2)
            .Where(points => !CollinearBacktrack(points[0], points[1], points.Count > 2 ? points[2] : points[1]))
            .Where(points => !CollinearBacktrack(points.Count > 2 ? points[^3] : points[^2], points[^2], points[^1]))
            .Where(points => Geometry.RouteHonorsEndpointSides(points, fromSide, toSide))
            .ToList();
        Js.Sort(normalized, (a, b) => a.Count - b.Count);
        return normalized.Select(Interior).ToList();
    }

    (Pt Start, Pt End) AlignFacingPorts(JsonObject conn, Box from, Box to, Pt start, Pt end, string fromSide, string toSide, SpreadPorts ports) =>
        Geometry.AlignAutomaticFacingPorts(conn, from, to, start, end, fromSide, toSide, ports,
            points => RouteClearsEndpointComponents(points, from, to) && RouteClearsComponents(conn, points));

    // A reciprocal pair is repaired jointly after planning, so crossings that
    // involve it are not final yet; keep the established first choice there.
    bool Reciprocal(JsonObject conn) => connections.Any(other => other.Str("from") == conn.Str("to") && other.Str("to") == conn.Str("from"));
    int SiblingCrossings(JsonObject conn, List<Pt> points, List<ResolvedRoute> resolvedRoutes)
    {
        if (Reciprocal(conn)) return 0;
        return CrossingCount(conn, points, resolvedRoutes.Where(entry => !Reciprocal(entry.Conn)).ToList());
    }

    static List<Pt> ViaPoints(JsonNode via) => via.AsArrayOrEmpty().Select(point => new Pt(point![0]!.D(), point[1]!.D())).ToList();

    List<Pt> RouteVia(JsonObject conn, Box from, Box to, Pt start, Pt end, string fromSide, string toSide, List<ResolvedRoute> resolvedRoutes)
    {
        if (conn["via"].Truthy()) return ViaPoints(conn["via"]!);
        switch (Truthy(conn.Str("route")) ? conn.Str("route") : "auto")
        {
            case "straight":
                return [];
            case "orthogonal-h":
            {
                var midX = (start.X + end.X) / 2;
                return [new(midX, start.Y), new(midX, end.Y)];
            }
            case "orthogonal-v":
            {
                var midY = (start.Y + end.Y) / 2;
                return [new(start.X, midY), new(end.X, midY)];
            }
            default:
            {
                // A caller may own the corridor for relationships that would otherwise
                // share one channel. Preferred candidates are tried before the shared
                // families, so a declared trunk or lane wins over the generic midpoint;
                // a candidate that violates the endpoint contract or an obstacle is
                // skipped and the historical order below still applies.
                if (preferredCandidates is not null)
                {
                    foreach (var candidate in preferredCandidates(conn, from, to, start, end, fromSide, toSide) ?? [])
                    {
                        var points = Join(start, candidate, end);
                        if (Geometry.RouteHonorsEndpointSides(points, fromSide, toSide)
                            && RouteClearsEndpointComponents(points, from, to)
                            && RouteClearsComponents(conn, points)
                            && RouteMeetsCompositionFloors(points)
                            && !RouteConflictsWithResolved(conn, points, resolvedRoutes)) return candidate;
                    }
                }

                // Direct line unless the anchors are clearly orthogonal-friendly.
                var deltaX = Math.Abs(start.X - end.X);
                var deltaY = Math.Abs(start.Y - end.Y);
                if (deltaX < 4 || deltaY < 4)
                {
                    var direct = new List<Pt> { start, end };
                    if (Geometry.RouteHonorsEndpointSides(direct, fromSide, toSide)
                        && RouteClearsEndpointComponents(direct, from, to)
                        && RouteClearsComponents(conn, direct)
                        && RouteMeetsCompositionFloors(direct)
                        && !RouteConflictsWithResolved(conn, direct, resolvedRoutes)) return [];
                }

                var rhythmBridge = Geometry.AutomaticPortRhythmBridge(start, end, fromSide, toSide, accept: points =>
                    RouteClearsEndpointComponents(points, from, to)
                    && RouteClearsComponents(conn, points)
                    && RouteMeetsCompositionFloors(points)
                    && !RouteConflictsWithResolved(conn, points, resolvedRoutes));
                if (rhythmBridge is not null) return Interior(rhythmBridge);

                // Automatic port spreading can leave otherwise aligned endpoints only a
                // few pixels apart. A midpoint route would split that tiny difference
                // into two unreadable endpoint stubs, so take a bounded outside channel
                // when both anchors sit on parallel component sides.
                const double minimumStub = 8;
                var fromVerticalSide = start.Y == from.Y || start.Y == from.Y + from.Height;
                var toVerticalSide = end.Y == to.Y || end.Y == to.Y + to.Height;
                if (fromVerticalSide && toVerticalSide && deltaX < minimumStub * 2)
                {
                    foreach (var channelX in new[] { Math.Max(start.X, end.X) + minimumStub * 2, Math.Min(start.X, end.X) - minimumStub * 2 })
                    {
                        var candidate = new List<Pt> { new(channelX, start.Y), new(channelX, end.Y) };
                        var points = Join(start, candidate, end);
                        if (Geometry.RouteHonorsEndpointSides(points, fromSide, toSide)
                            && RouteClearsComponents(conn, points)
                            && RouteMeetsCompositionFloors(points)
                            && !RouteConflictsWithResolved(conn, points, resolvedRoutes)) return candidate;
                    }
                }

                var fromHorizontalSide = start.X == from.X || start.X == from.X + from.Width;
                var toHorizontalSide = end.X == to.X || end.X == to.X + to.Width;
                if (fromHorizontalSide && toHorizontalSide && deltaY < minimumStub * 2)
                {
                    foreach (var channelY in new[] { Math.Max(start.Y, end.Y) + minimumStub * 2, Math.Min(start.Y, end.Y) - minimumStub * 2 })
                    {
                        var candidate = new List<Pt> { new(start.X, channelY), new(end.X, channelY) };
                        var points = Join(start, candidate, end);
                        if (Geometry.RouteHonorsEndpointSides(points, fromSide, toSide)
                            && RouteClearsComponents(conn, points)
                            && RouteMeetsCompositionFloors(points)
                            && !RouteConflictsWithResolved(conn, points, resolvedRoutes)) return candidate;
                    }
                }

                var midX = (start.X + end.X) / 2;
                var horizontalFirst = new List<Pt> { new(midX, start.Y), new(midX, end.Y) };
                var midY = (start.Y + end.Y) / 2;
                var verticalFirst = new List<Pt> { new(start.X, midY), new(end.X, midY) };
                var candidates = new[] { horizontalFirst, verticalFirst };
                var sideSafe = candidates.Where(candidate => Geometry.RouteHonorsEndpointSides(Join(start, candidate, end), fromSide, toSide)).ToList();
                var sideAware = SideAwareBridgeCandidates(start, end, fromSide, toSide);
                var nearParallelPorts =
                    (fromSide is "top" or "bottom" && toSide is "top" or "bottom" && deltaX < minimumStub * 2)
                    || (fromSide is "left" or "right" && toSide is "left" or "right" && deltaY < minimumStub * 2);
                List<List<Pt>> ordered = nearParallelPorts ? [.. sideAware, .. sideSafe] : [.. sideSafe, .. sideAware];
                List<Pt>? firstFeasible = null;
                foreach (var candidate in ordered)
                {
                    var points = Join(start, candidate, end);
                    if (!(RouteClearsEndpointComponents(points, from, to)
                        && RouteClearsComponents(conn, points)
                        && RouteMeetsCompositionFloors(points)
                        && !RouteConflictsWithResolved(conn, points, resolvedRoutes)
                        && (!distinctAutomaticPorts || (!RouteOverlapsResolved(conn, points, resolvedRoutes)
                            && SiblingCrossings(conn, points, resolvedRoutes) == 0)))) continue;
                    if (!conn["label"].Truthy() || labelRectFor is null || labelRectFor(conn, points,
                        resolvedRoutes.Select(entry => entry.Points).ToList(),
                        reservedLabels.Select(entry => entry.Rect).ToList()) is not null) return candidate;
                    firstFeasible ??= candidate;
                }
                if (firstFeasible is not null) return firstFeasible;
                // Siblings fanning out from one side all want the same midpoint
                // channel. Step outward through the corridor to a free parallel
                // channel before searching.
                if (distinctAutomaticPorts)
                {
                    static List<double> Channels(double from, double to, double mid)
                    {
                        var (low, high) = (Math.Min(from, to) + 24, Math.Max(from, to) - 24);
                        var values = new List<double>();
                        for (double offset = 16; mid - offset >= low || mid + offset <= high; offset += 16)
                            values.AddRange(new[] { mid + offset, mid - offset }.Where(value => value >= low && value <= high));
                        return values;
                    }
                    var channelCandidates = Channels(start.X, end.X, midX).Select(x => new List<Pt> { new(x, start.Y), new(x, end.Y) })
                        .Concat(Channels(start.Y, end.Y, midY).Select(y => new List<Pt> { new(start.X, y), new(end.X, y) }));
                    foreach (var candidate in channelCandidates)
                    {
                        var points = Join(start, candidate, end);
                        if (Geometry.RouteHonorsEndpointSides(points, fromSide, toSide)
                            && RouteClearsEndpointComponents(points, from, to)
                            && RouteClearsComponents(conn, points)
                            && RouteMeetsCompositionFloors(points)
                            && !RouteConflictsWithResolved(conn, points, resolvedRoutes)
                            && !RouteOverlapsResolved(conn, points, resolvedRoutes)
                            && SiblingCrossings(conn, points, resolvedRoutes) == 0) return candidate;
                    }
                }

                // Two-bend doglegs are deliberately cheap, but a real architecture can
                // place adjacent components on both of those corridors. Search a
                // bounded obstacle grid before falling back to a route that the Clean
                // Flow gate already knows violates the inferred endpoint directions.
                // This keeps ordinary multi-bend avoidance renderer-owned instead of
                // forcing the author to hand-place via points.
                // Alternative endpoint pairs are cheap probes, not another grid-search
                // budget. A legal short bridge often beats the first grid detour.
                List<Pt> Fallback() => sideSafe.Count > 0 ? sideSafe[0] : sideAware.Count > 0 ? sideAware[0] : horizontalFirst;
                if (!allowGridSearch) return Fallback();
                if (planningMetrics.GridSearchCount >= planningMetrics.MaximumGridSearchCount)
                {
                    planningMetrics.GridBudgetExhaustedCount += 1;
                    planningMetrics.ConflictFallbackCount += 1;
                    return Fallback();
                }
                // First-draft architecture graphs are not always planar at their
                // authored node positions. The renderer gives automatic crossings a
                // visible halo, so the grid owns opaque-node avoidance and rejects
                // ambiguous shared corridors without forcing the model to hand-route
                // a sprawling perimeter detour. Cheap candidates above still prefer a
                // genuinely crossing-free path whenever one is available.
                if (crossingFreeGridFirst)
                {
                    var unrelatedSegments = UnrelatedResolvedRoutes(conn, resolvedRoutes)
                        .SelectMany(entry => entry.Points.Skip(1).Select((segEnd, index) => new Segment(entry.Points[index], segEnd))).ToList();
                    foreach (var gap in unrelatedSegments.Count > 0 ? gridComponentGaps : Array.Empty<double>())
                    {
                        planningMetrics.GridSearchCount += 1;
                        var strict = RouteQuality.ShortestOrthogonalGridRoute(start, end, [start, end],
                            [.. GridComponentObstacles(conn, gap), .. ReservedLabelObstacles(2)], fromSide, toSide,
                            new GridRouteOptions(Clearance: 2, MaximumObstacleCount: 80, EndpointStubPx: 24, MaximumGridNodes: 4096,
                                AvoidedSegments: unrelatedSegments, AllowAvoidedCrossings: false, MinimumAvoidedOverlapPx: 8, RouteSeparationPx: 8,
                                MinimumSegmentPx: interiorSegmentPx, BorderSegments: frameBorders, BendPenaltyPx: 48));
                        if (strict is not null
                            && RouteClearsEndpointComponents(strict.Points, from, to)
                            && RouteClearsComponents(conn, strict.Points)
                            && !RouteConflictsWithResolved(conn, strict.Points, resolvedRoutes)
                            && !RouteOverlapsResolved(conn, strict.Points, resolvedRoutes)
                            && RouteMeetsCompositionFloors(strict.Points)
                            && SpacedRouteIsClean(strict.Points, gap))
                        {
                            planningMetrics.GridRoutedCount += 1;
                            return Interior(strict.Points);
                        }
                    }
                }
                var avoidedSegments = resolvedRoutes
                    .Where(entry => distinctAutomaticPorts && RelationshipsShareEndpoint(conn, entry.Conn)
                        && !HasAuthoredRouteGeometry(entry.Conn) && !entry.Conn["labelAt"].Truthy() && !conn["labelAt"].Truthy())
                    .SelectMany(entry => entry.Points.Skip(1).Select((segEnd, index) => new Segment(entry.Points[index], segEnd))).ToList();
                foreach (var gap in gridComponentGaps)
                {
                    var gridMetrics = new GridMetrics();
                    planningMetrics.GridSearchCount += 1;
                    planningMetrics.AvoidedSegmentCount += avoidedSegments.Count;
                    var searched = RouteQuality.ShortestOrthogonalGridRoute(start, end, [start, end],
                        [.. GridComponentObstacles(conn, gap), .. ReservedLabelObstacles(2)], fromSide, toSide,
                        new GridRouteOptions(Clearance: 2, MaximumObstacleCount: 80, EndpointStubPx: 24, MaximumGridNodes: 4096,
                            AvoidedSegments: avoidedSegments, AllowAvoidedCrossings: true, MinimumAvoidedOverlapPx: 8, RouteSeparationPx: 8,
                            MinimumSegmentPx: interiorSegmentPx, BorderSegments: frameBorders, BendPenaltyPx: 48, Metrics: gridMetrics));
                    planningMetrics.GridCandidateNodeCount += gridMetrics.CandidateNodeCount;
                    planningMetrics.GridUsableNodeCount += gridMetrics.UsableNodeCount;
                    planningMetrics.GridEdgeCount += gridMetrics.GraphEdgeCount;
                    planningMetrics.GridVisitedNodeCount += gridMetrics.VisitedNodeCount;
                    var clearsEndpoints = searched is not null && RouteClearsEndpointComponents(searched.Points, from, to);
                    var clearsComponents = searched is not null && RouteClearsComponents(conn, searched.Points);
                    var clearsRelationships = searched is not null && !RouteConflictsWithResolved(conn, searched.Points, resolvedRoutes);
                    var clearsSharedCorridors = searched is not null && !RouteOverlapsResolved(conn, searched.Points, resolvedRoutes);
                    var meetsFloors = searched is not null && RouteMeetsCompositionFloors(searched.Points) && SpacedRouteIsClean(searched.Points, gap);
                    var accepted = searched is not null && clearsEndpoints && clearsComponents && clearsSharedCorridors && meetsFloors;
                    var attempt = new JsonObject
                    {
                        ["relationship"] = Truthy(conn.Str("id")) ? conn.Str("id") : $"{conn.Str("from")}->{conn.Str("to")}",
                        ["fromSide"] = fromSide,
                        ["toSide"] = toSide,
                        ["inputAvoidedSegmentCount"] = avoidedSegments.Count,
                        ["status"] = gridMetrics.Status,
                        ["maximumGridNodes"] = gridMetrics.MaximumGridNodes,
                        ["obstacleCount"] = gridMetrics.ObstacleCount,
                        ["avoidedSegmentCount"] = gridMetrics.AvoidedSegmentCount,
                        ["coordinateCount"] = gridMetrics.CoordinateCount,
                        ["candidateNodeCount"] = gridMetrics.CandidateNodeCount,
                        ["usableNodeCount"] = gridMetrics.UsableNodeCount,
                        ["graphEdgeCount"] = gridMetrics.GraphEdgeCount,
                        ["visitedNodeCount"] = gridMetrics.VisitedNodeCount,
                        ["attemptCount"] = gridMetrics.AttemptCount,
                        ["accepted"] = accepted,
                    };
                    if (!accepted && searched is not null)
                    {
                        var rejectedBy = new JsonArray();
                        if (!clearsEndpoints) rejectedBy.Add("endpoint-components");
                        if (!clearsComponents) rejectedBy.Add("components");
                        if (!clearsSharedCorridors) rejectedBy.Add("shared-corridor");
                        if (!meetsFloors) rejectedBy.Add("composition-floors");
                        attempt["rejectedBy"] = rejectedBy;
                        attempt["candidatePoints"] = new JsonArray([.. searched.Points.Select(p => (JsonNode?)new JsonArray(p.X, p.Y))]);
                    }
                    planningMetrics.GridAttempts.Add(attempt);
                    if (accepted)
                    {
                        planningMetrics.GridRoutedCount += 1;
                        if (!clearsRelationships) planningMetrics.CrossoverRoutedCount += 1;
                        return Interior(searched!.Points);
                    }
                }

                // Both bounded doglegs are blocked. Keep the best endpoint-safe route
                // when one exists so the universal Clean Flow gate reports the actual
                // obstacle; otherwise preserve the historical deterministic fallback
                // and let the endpoint-direction gate explain the side mismatch.
                planningMetrics.ConflictFallbackCount += 1;
                return Fallback();
            }
        }
    }

    Sides? RowFanOutSides(Box from, Box to)
    {
        if (!preferReadableRoutes) return null;
        string? VerticalSides(Box node, Box other)
        {
            var below = other.Y >= node.Y + node.Height;
            if (!below && other.Y + other.Height > node.Y) return null;
            if (Geometry.DefaultFromSide(node, other) is "top" or "bottom") return null;
            var neighbours = neighbourRects.Get(node.Id) ?? [];
            var rowSibling = neighbours.Any(sibling => !ReferenceEquals(sibling, other)
                && Math.Abs(sibling.Cy - other.Cy) < 1
                && Geometry.DefaultFromSide(node, sibling) == (below ? "bottom" : "top"));
            // Only when a node of that row blocks a sideways route to one of the
            // neighbours on this side; otherwise side exits stay clear and keep the
            // vertical side free. Decide per side so a row never mixes both styles.
            bool Blocked(Box target)
            {
                var (gapStart, gapEnd) = target.Cx < node.Cx
                    ? (target.X + target.Width, node.X) : (node.X + node.Width, target.X);
                return components.Values.Any(rect => !ReferenceEquals(rect, node) && !ReferenceEquals(rect, target)
                    && rect.Y < target.Y + target.Height && rect.Y + rect.Height > target.Y
                    && rect.X < gapEnd && rect.X + rect.Width > gapStart);
            }
            var sameSide = neighbours.Where(sibling => Math.Abs(sibling.Cy - other.Cy) < 1
                && Geometry.DefaultFromSide(node, sibling) == Geometry.DefaultFromSide(node, other)).ToList();
            return rowSibling && sameSide.Any(Blocked) ? (below ? "bottom" : "top") : null;
        }
        static string Opposite(string side) => side == "top" ? "bottom" : "top";
        var fromSide = VerticalSides(from, to);
        if (fromSide is not null) return new(fromSide, Opposite(fromSide));
        var toSide = VerticalSides(to, from);
        return toSide is not null ? new(Opposite(toSide), toSide) : null;
    }

    static double Stroke(JsonObject relation) =>
        relation.Num("width") is { } width && width != 0 && !double.IsNaN(width) ? width : relation.Str("variant") == "emphasis" ? 1.8 : 1.5;
    static double MarkerSpacing(JsonObject left, JsonObject right) => 3.5 * (Stroke(left) + Stroke(right));
    static double PortSpacing(JsonObject left, JsonObject right) => Math.Max(14, MarkerSpacing(left, right) + 3.5);
    string? FanOutSide(JsonObject conn, string endpoint)
    {
        var from = Component(conn.Str("from"));
        var to = Component(conn.Str("to"));
        var fanOut = from is null || to is null ? null : RowFanOutSides(from, to);
        return endpoint == "source" ? fanOut?.FromSide : fanOut?.ToSide;
    }
    // A caller may own the endpoint side of a relationship; whatever it leaves
    // unresolved keeps architecture's row fan-out inference.
    string? InferredSide(JsonObject conn, string endpoint)
    {
        var owned = sideFor?.Invoke(conn, endpoint);
        return Truthy(owned) ? owned : FanOutSide(conn, endpoint);
    }

    Sides InferredConnectionSides(JsonObject conn)
    {
        var from = Component(conn.Str("from"))!;
        var to = Component(conn.Str("to"))!;
        var fromInferred = InferredSide(conn, "source");
        var toInferred = InferredSide(conn, "target");
        return new(
            Geometry.ChosenSide(conn.Str("fromSide"), Truthy(fromInferred) ? fromInferred! : Geometry.DefaultFromSide(from, to)),
            Geometry.ChosenSide(conn.Str("toSide"), Truthy(toInferred) ? toInferred! : Geometry.DefaultToSide(from, to)));
    }

    public Sides ConnectionSides(JsonObject conn)
    {
        if (!routesPlanned && !routesPlanning) PlanRoutes();
        return selectedSides.Get(conn) ?? InferredConnectionSides(conn);
    }

    // The same side inference without the planning pass. A caller that must group
    // relationships before any route exists (the erd renderer assigns a shared
    // trunk per fan-in) reads its sides here: connectionSides() plans routes, and
    // planning asks the grouping what the trunks are.
    public Sides InferredSides(JsonObject conn) => InferredConnectionSides(conn);

    public string ConnectionEndpointSide(JsonObject conn, string endpoint)
    {
        var field = endpoint == "source" ? "fromSide" : "toSide";
        if (AuthoredSide(conn, field)) return conn.Str(field)!;
        var sides = ConnectionSides(conn);
        return endpoint == "source" ? sides.FromSide : sides.ToSide;
    }

    public static bool HasAuthoredRouteGeometry(JsonObject? conn) =>
        conn is not null && (
            conn["via"].Truthy()
            || (Truthy(conn.Str("route")) && conn.Str("route") != "auto")
            || conn.ContainsKey("channelX")
            || conn.ContainsKey("channelY"));

    static bool HasAuthoredLabelPlacement(JsonObject? conn) =>
        conn is not null && (conn.ContainsKey("labelAt") || conn.ContainsKey("labelDx") || conn.ContainsKey("labelDy") || conn.ContainsKey("labelSegment"));

    static Pt? PortOf(SpreadPorts? ports, string endpoint) => ports is null ? null : endpoint == "from" ? ports.From : ports.To;
    static string SideOf(Sides sides, string sideField) => sideField == "fromSide" ? sides.FromSide : sides.ToSide;

    // A route can change sides after the initial port spread. Reserve slots on
    // the final side as well: falling back to its midpoint can put an arrow
    // between two existing slots, or directly on another incoming arrow.
    EndpointChoice AutomaticEndpoint(JsonObject conn, string endpoint, Box rect, string side, string inferredSide)
    {
        var initial = side == inferredSide ? PortOf(automaticPorts.Get(conn), endpoint) : null;
        var preferred = initial ?? Geometry.Anchor(rect, side);
        if (HasAuthoredRouteGeometry(conn) || conn["labelAt"].Truthy()) return new(preferred, initial is not null);
        var axis = side is "left" or "right" ? 1 : 0;
        var occupied = new List<(double Value, double Spacing)>();
        foreach (var (other, field, sideField) in incidentEndpoints.Get(rect.Id) ?? [])
        {
            if (ReferenceEquals(other, conn) || HasAuthoredRouteGeometry(other) || other["labelAt"].Truthy()) continue;
            var routed = pathCache.Get(other);
            var sides = selectedSides.Get(other) ?? InferredConnectionSides(other);
            if (SideOf(sides, sideField) != side) continue;
            var point = routed is not null
                ? (field == "from" ? routed.Points[0] : routed.Points[^1])
                : PortOf(automaticPorts.Get(other), field) ?? Geometry.Anchor(rect, side);
            occupied.Add((Coord(point, axis), PortSpacing(conn, other)));
        }
        if (occupied.Count == 0) return new(preferred, initial is not null);
        // On an alternate side, equally close slots should approach the other
        // node rather than pass its neighbours just because a coordinate is lower.
        // Keep the established spread order on the inferred side unchanged.
        var counterpart = Component(endpoint == "from" ? conn.Str("to") : conn.Str("from"))!;
        var preferredValue = Coord(preferred, axis);
        var toward = side == inferredSide ? preferredValue : (axis == 1 ? counterpart.Cy : counterpart.Cx);
        var candidates = new List<double> { preferredValue };
        foreach (var (value, spacing) in occupied) { candidates.Add(value - spacing); candidates.Add(value + spacing); }
        Js.Sort(candidates, (a, b) => FirstNonZero(
            Math.Abs(a - preferredValue) - Math.Abs(b - preferredValue),
            Math.Abs(a - toward) - Math.Abs(b - toward),
            a - b));
        foreach (var value in candidates)
        {
            var point = WithCoord(preferred, axis, value);
            if (Geometry.PortHasCornerClearance(rect, side, point)
                && occupied.All(other => Math.Abs(value - other.Value) >= other.Spacing - 0.0001))
                return new(point, true);
        }
        return new(preferred, true, true);
    }

    ConnGeometry ConnectionGeometry(JsonObject conn, Sides? sides = null)
    {
        var from = Component(conn.Str("from"))!;
        var to = Component(conn.Str("to"))!;
        var inferred = InferredConnectionSides(conn);
        var (fromSide, toSide) = sides ?? inferred;
        var legacyPorts = fromSide == inferred.FromSide && toSide == inferred.ToSide ? automaticPorts.Get(conn) : null;
        var source = distinctAutomaticPorts
            ? AutomaticEndpoint(conn, "from", from, fromSide, inferred.FromSide)
            : new EndpointChoice(legacyPorts?.From ?? Geometry.Anchor(from, fromSide), legacyPorts?.From is not null);
        var target = distinctAutomaticPorts
            ? AutomaticEndpoint(conn, "to", to, toSide, inferred.ToSide)
            : new EndpointChoice(legacyPorts?.To ?? Geometry.Anchor(to, toSide), legacyPorts?.To is not null);
        var ports = new SpreadPorts { From = source.Spread ? source.Point : null, To = target.Spread ? target.Point : null };
        var (start, end) = AlignFacingPorts(conn, from, to, source.Point, target.Point, fromSide, toSide, ports);
        return new(from, to, start, end, fromSide, toSide, source.Crowded || target.Crowded);
    }

    RoutedPath RoutedForGeometry(JsonObject conn, List<ResolvedRoute> resolvedRoutes, ConnGeometry geometry)
    {
        var (from, to, start, end, fromSide, toSide, _) = geometry;
        var authoredPoints = Join(start, RouteVia(conn, from, to, start, end, fromSide, toSide, resolvedRoutes), end);
        // Explicit waypoints are author-owned geometry. Keep even a collinear
        // waypoint: it can intentionally split a route at a semantic touch point,
        // and preserving it is part of the backwards-compatible authoring contract.
        // Automatic routes remain normalized so the renderer does not emit noisy
        // duplicate turns or zero-length segments.
        var points = HasAuthoredRouteGeometry(conn) ? authoredPoints : Geometry.NormalizeRoutePoints(authoredPoints);
        return new(Geometry.RoundedPath(points, 8), points);
    }

    RoutedPath CachePath(JsonObject conn, RoutedPath routed, Sides sides)
    {
        pathCache.Set(conn, routed);
        selectedSides.Set(conn, new(sides.FromSide, sides.ToSide));
        return routed;
    }

    List<Sides> CandidateSidePairs(JsonObject conn)
    {
        var inferred = InferredConnectionSides(conn);
        var authoredFrom = AuthoredSide(conn, "fromSide") ? conn.Str("fromSide") : null;
        var authoredTo = AuthoredSide(conn, "toSide") ? conn.Str("toSide") : null;
        var fromOptions = authoredFrom is not null ? [authoredFrom] : new List<string> { inferred.FromSide }.Concat(SIDE_ORDER.Where(side => side != inferred.FromSide)).ToList();
        var toOptions = authoredTo is not null ? [authoredTo] : new List<string> { inferred.ToSide }.Concat(SIDE_ORDER.Where(side => side != inferred.ToSide)).ToList();
        var pairs = fromOptions.SelectMany(fromSide => toOptions.Select(toSide => (
            Sides: new Sides(fromSide, toSide),
            DeviationCount: (fromSide != inferred.FromSide ? 1 : 0) + (toSide != inferred.ToSide ? 1 : 0)))).ToList();
        Js.Sort(pairs, (left, right) =>
        {
            if (left.DeviationCount != right.DeviationCount) return left.DeviationCount - right.DeviationCount;
            var leftGeometry = ConnectionGeometry(conn, left.Sides);
            var rightGeometry = ConnectionGeometry(conn, right.Sides);
            var leftDistance = Math.Abs(leftGeometry.End.X - leftGeometry.Start.X) + Math.Abs(leftGeometry.End.Y - leftGeometry.Start.Y);
            var rightDistance = Math.Abs(rightGeometry.End.X - rightGeometry.Start.X) + Math.Abs(rightGeometry.End.Y - rightGeometry.Start.Y);
            return leftDistance - rightDistance;
        });
        return pairs.Select(pair => pair.Sides).ToList();
    }

    bool RouteIsClear(JsonObject conn, List<Pt> points, ConnGeometry geometry, List<ResolvedRoute> resolvedRoutes) =>
        !geometry.Crowded && points.Count >= 2
        && Geometry.RouteHonorsEndpointSides(points, geometry.FromSide, geometry.ToSide)
        && RouteClearsEndpointComponents(points, geometry.From, geometry.To)
        && RouteClearsComponents(conn, points)
        && RouteMeetsCompositionFloors(points)
        && !RouteOverlapsResolved(conn, points, resolvedRoutes);

    int CrossingCount(JsonObject conn, List<Pt> points, List<ResolvedRoute> resolvedRoutes)
    {
        var count = 0;
        foreach (var entry in resolvedRoutes)
        {
            if (RelationshipsShareEndpoint(conn, entry.Conn)
                && (!distinctAutomaticPorts || HasAuthoredRouteGeometry(entry.Conn) || entry.Conn["labelAt"].Truthy() || conn["labelAt"].Truthy())) continue;
            var crosses = false;
            for (var index = 1; index < points.Count && !crosses; index += 1)
            {
                for (var otherIndex = 1; otherIndex < entry.Points.Count; otherIndex += 1)
                {
                    if (Geometry.ProperSegmentIntersection(points[index - 1], points[index], entry.Points[otherIndex - 1], entry.Points[otherIndex]) is not null)
                    {
                        crosses = true;
                        break;
                    }
                }
            }
            if (crosses) count += 1;
        }
        return count;
    }

    double ReadabilityCost(JsonObject conn, List<Pt> points, List<ResolvedRoute> resolvedRoutes)
    {
        var length = RouteLength(points);
        // A crossover is a reading cost even with a halo. Keep it finite: avoiding
        // one crossing must not justify an arbitrarily long perimeter excursion.
        return length + Math.Max(0, points.Count - 2) * 48 + CrossingCount(conn, points, resolvedRoutes) * 160;
    }

    RoutedPath ComputePath(JsonObject conn, List<ResolvedRoute> resolvedRoutes)
    {
        if (HasAuthoredRouteGeometry(conn))
        {
            var sides = InferredConnectionSides(conn);
            return CachePath(conn, RoutedForGeometry(conn, resolvedRoutes, ConnectionGeometry(conn, sides)), sides);
        }

        RouteChoice? firstFallback = null;
        RouteChoice? ClearRoute()
        {
            RouteChoice? firstClear = null;
            foreach (var sides in CandidateSidePairs(conn))
            {
                var geometry = ConnectionGeometry(conn, sides);
                var routed = RoutedForGeometry(conn, resolvedRoutes, geometry);
                firstFallback ??= new(routed, sides);
                if (RouteIsClear(conn, routed.Points, geometry, resolvedRoutes))
                {
                    firstClear = new(routed, sides);
                    break;
                }
            }
            return firstClear;
        }

        // Reserved labels are a preference, not an obstacle. A route that has no
        // corridor around them, or that would have to detour far around them,
        // takes the plain route instead and the label placement pass moves the
        // label; only a modest extra length is worth keeping a label in place.
        honourReservedLabels = true;
        var chosen = ClearRoute();
        if (reservedLabels.Count > 0)
        {
            honourReservedLabels = false;
            if (chosen is null) chosen = ClearRoute();
            else
            {
                var direct = Span(chosen.Routed.Points[0], chosen.Routed.Points[^1]);
                if (RouteLength(chosen.Routed.Points) > direct * 1.25 + 64)
                {
                    var plain = ClearRoute();
                    if (plain is not null && RouteLength(plain.Routed.Points) * 1.25 + 64 < RouteLength(chosen.Routed.Points)) chosen = plain;
                }
            }
            honourReservedLabels = true;
        }
        if (chosen is not null) return CachePath(conn, chosen.Routed, chosen.Sides);
        return CachePath(conn, firstFallback!.Routed, firstFallback.Sides);
    }

    List<List<Pt>> PointsOf(IEnumerable<ResolvedRoute> entries) => entries.Select(entry => entry.Points).ToList();
    static List<LabelRect> RectsOf(IEnumerable<ReservedLabel> labels) => labels.Select(label => label.Rect).ToList();

    void PlanRoutes()
    {
        if (routesPlanned || routesPlanning) return;
        routesPlanning = true;
        var indexed = connections.Select((conn, index) => (Conn: conn, Index: index))
            .Where(entry => components.Has(entry.Conn.Str("from") ?? "") && components.Has(entry.Conn.Str("to") ?? "")).ToList();
        var explicitRoutes = indexed.Where(entry => HasAuthoredRouteGeometry(entry.Conn)).ToList();
        var automatic = indexed.Where(entry => !HasAuthoredRouteGeometry(entry.Conn)).ToList();
        Js.Sort(automatic, (left, right) =>
        {
            var leftFrom = Component(left.Conn.Str("from"))!;
            var leftTo = Component(left.Conn.Str("to"))!;
            var rightFrom = Component(right.Conn.Str("from"))!;
            var rightTo = Component(right.Conn.Str("to"))!;
            var leftDistance = Math.Abs(leftTo.Cx - leftFrom.Cx) + Math.Abs(leftTo.Cy - leftFrom.Cy);
            var rightDistance = Math.Abs(rightTo.Cx - rightFrom.Cx) + Math.Abs(rightTo.Cy - rightFrom.Cy);
            return FirstNonZero(leftDistance - rightDistance, left.Index - right.Index);
        });
        var resolvedRoutes = new List<ResolvedRoute>();
        reservedLabels = [];
        foreach (var (conn, _) in explicitRoutes.Concat(automatic))
        {
            var routed = ComputePath(conn, resolvedRoutes);
            resolvedRoutes.Add(new(conn, routed.Points));
            var rect = labelRectFor?.Invoke(conn, routed.Points, PointsOf(resolvedRoutes), RectsOf(reservedLabels));
            if (rect is not null) reservedLabels.Add(new(conn, rect));
        }
        // Improve against the complete set of routes, not only earlier edges.
        // A greedy side change during the initial pass can steal a later edge's
        // corridor. This bounded sweep only changes one route at a time, keeping
        // every other route and label as an obstacle/reference.
        if (preferReadableRoutes)
        {
            var scenePoints = resolvedRoutes.SelectMany(entry => entry.Points).ToList();
            foreach (var rect in components.Values.Select(c => (Rect)c).Concat(frames.Select(f => new Rect(f.X, f.Y, f.Width, f.Height))).Concat(reservedLabels.Select(entry => (Rect)entry.Rect)))
            {
                scenePoints.Add(new(rect.X, rect.Y));
                scenePoints.Add(new(rect.X + rect.Width, rect.Y + rect.Height));
            }
            // Math.min()/Math.max() of an empty list: ±Infinity; NaN propagates as Math.min/max do.
            var boundsLeft = double.PositiveInfinity;
            var boundsRight = double.NegativeInfinity;
            var boundsTop = double.PositiveInfinity;
            var boundsBottom = double.NegativeInfinity;
            foreach (var p in scenePoints)
            {
                boundsLeft = Math.Min(boundsLeft, p.X); boundsRight = Math.Max(boundsRight, p.X);
                boundsTop = Math.Min(boundsTop, p.Y); boundsBottom = Math.Max(boundsBottom, p.Y);
            }
            bool WithinScene(Pt p) => p.X >= boundsLeft && p.X <= boundsRight && p.Y >= boundsTop && p.Y <= boundsBottom;
            var jointlyImproved = new JsSet<JsonObject>();
            bool EndpointSlotsClear(List<ResolvedRoute> candidateRoutes, List<ResolvedRoute> others, Func<JsonObject, Sides?> sidesOf)
            {
                var entries = others.Concat(candidateRoutes).ToList();
                foreach (var candidate in candidateRoutes)
                {
                    var sides = sidesOf(candidate.Conn)!;
                    foreach (var (componentId, side, point) in new[]
                    {
                        (candidate.Conn.Str("from"), sides.FromSide, candidate.Points[0]),
                        (candidate.Conn.Str("to"), sides.ToSide, candidate.Points[^1]),
                    })
                    {
                        var coordinate = side is "left" or "right" ? 1 : 0;
                        foreach (var other in entries)
                        {
                            if (ReferenceEquals(other.Conn, candidate.Conn)) continue;
                            var otherSides = sidesOf(other.Conn)!;
                            foreach (var (otherId, otherSide, otherPoint) in new[]
                            {
                                (other.Conn.Str("from"), otherSides.FromSide, other.Points[0]),
                                (other.Conn.Str("to"), otherSides.ToSide, other.Points[^1]),
                            })
                            {
                                if (componentId == otherId && side == otherSide
                                    && Math.Abs(Coord(point, coordinate) - Coord(otherPoint, coordinate))
                                        < PortSpacing(candidate.Conn, other.Conn) - 0.0001) return false;
                            }
                        }
                    }
                }
                return true;
            }
            bool LabelClears(LabelRect? rect, JsonObject owner, List<ResolvedRoute> entries, List<ReservedLabel> labels)
            {
                if (rect is null) return !owner["label"].Truthy();
                if (!WithinScene(new(rect.X, rect.Y))
                    || !WithinScene(new(rect.X + rect.Width, rect.Y + rect.Height))
                    || labels.Any(other => Geometry.RectsOverlap(rect, other.Rect, 2))) return false;
                return entries.All(entry => ReferenceEquals(entry.Conn, owner) || entry.Points.Skip(1).Select((end, index) => (end, index)).All(pair =>
                    !Geometry.SegmentIntersectsRect(new(entry.Points[pair.index], pair.end), rect, LABEL_CLEARANCE)));
            }
            // A type-owned corridor (an erd bundled trunk or dedicated lane) is the
            // route its renderer draws around; the generic sweep must not trade it
            // for a shorter lane and leave the bus without its branch.
            bool FollowsPreferredCorridor(ResolvedRoute entry)
            {
                if (preferredCandidates is null) return false;
                var sides = selectedSides.Get(entry.Conn);
                if (sides is null) return false;
                var interior = Interior(entry.Points);
                return (preferredCandidates(entry.Conn, Component(entry.Conn.Str("from"))!, Component(entry.Conn.Str("to"))!,
                    entry.Points[0], entry.Points[^1], sides.FromSide, sides.ToSide) ?? []).Any(candidate => SamePoints(candidate, interior));
            }
            var typeOwned = new JsSet<JsonObject>();
            foreach (var entry in resolvedRoutes.Where(FollowsPreferredCorridor)) typeOwned.Add(entry.Conn);
            void ImproveReciprocalPairs()
            {
                // A one-route sweep cannot repair reciprocal facing edges whose initial
                // spread groups differ. Try both direct lanes together after final side
                // geometry is known, leaving every other route and authored port in place.
                var paired = new JsSet<JsonObject>();
                foreach (var first in resolvedRoutes)
                {
                    var conn = first.Conn;
                    if (paired.Has(conn) || typeOwned.Has(conn) || HasAuthoredRouteGeometry(conn) || HasAuthoredLabelPlacement(conn)
                        || AuthoredSide(conn, "fromSide")
                        || AuthoredSide(conn, "toSide")) continue;
                    var second = resolvedRoutes.FirstOrDefault(entry => !ReferenceEquals(entry, first)
                        && entry.Conn.Str("from") == conn.Str("to") && entry.Conn.Str("to") == conn.Str("from") && !typeOwned.Has(entry.Conn)
                        && !HasAuthoredRouteGeometry(entry.Conn) && !HasAuthoredLabelPlacement(entry.Conn)
                        && !AuthoredSide(entry.Conn, "fromSide")
                        && !AuthoredSide(entry.Conn, "toSide"));
                    if (second is null) continue;
                    paired.Add(conn);
                    paired.Add(second.Conn);
                    var firstSides = InferredConnectionSides(conn);
                    var secondSides = InferredConnectionSides(second.Conn);
                    var horizontal = firstSides.FromSide == "right" && firstSides.ToSide == "left"
                        && secondSides.FromSide == "left" && secondSides.ToSide == "right";
                    var horizontalReverse = firstSides.FromSide == "left" && firstSides.ToSide == "right"
                        && secondSides.FromSide == "right" && secondSides.ToSide == "left";
                    var vertical = firstSides.FromSide == "bottom" && firstSides.ToSide == "top"
                        && secondSides.FromSide == "top" && secondSides.ToSide == "bottom";
                    var verticalReverse = firstSides.FromSide == "top" && firstSides.ToSide == "bottom"
                        && secondSides.FromSide == "bottom" && secondSides.ToSide == "top";
                    if (!horizontal && !horizontalReverse && !vertical && !verticalReverse) continue;
                    if (first.Points.Count <= 2 && second.Points.Count <= 2) continue;
                    var axis = horizontal || horizontalReverse ? 1 : 0;
                    var others = resolvedRoutes.Where(entry => !ReferenceEquals(entry, first) && !ReferenceEquals(entry, second)).ToList();
                    var firstGeometry = ConnectionGeometry(conn, firstSides);
                    var secondGeometry = ConnectionGeometry(second.Conn, secondSides);
                    if (firstGeometry.Crowded || secondGeometry.Crowded) continue;
                    var previousLabels = reservedLabels;
                    var improved = false;
                    reservedLabels = reservedLabels.Where(entry => !ReferenceEquals(entry.Conn, conn) && !ReferenceEquals(entry.Conn, second.Conn)).ToList();
                    try
                    {
                        List<double> Lanes(ConnGeometry geometry)
                        {
                            var set = new JsSet<double>();
                            set.Add(Coord(geometry.Start, axis));
                            set.Add(Coord(geometry.End, axis));
                            return [.. set];
                        }
                        RoutedPath Direct(ConnGeometry geometry, double lane)
                        {
                            var start = WithCoord(geometry.Start, axis, lane);
                            var end = WithCoord(geometry.End, axis, lane);
                            return new(Geometry.RoundedPath([start, end], 8), [start, end]);
                        }
                        Sides? PairSides(JsonObject candidate) => ReferenceEquals(candidate, conn) ? firstSides
                            : ReferenceEquals(candidate, second.Conn) ? secondSides : selectedSides.Get(candidate);
                        (ResolvedRoute FirstRoute, ResolvedRoute SecondRoute, LabelRect? FirstRect, LabelRect? SecondRect)? best = null;
                        var bestCost = ReadabilityCost(conn, first.Points, [.. others, second])
                            + ReadabilityCost(second.Conn, second.Points, [.. others, first]);
                        foreach (var firstLane in Lanes(firstGeometry))
                        {
                            foreach (var secondLane in Lanes(secondGeometry))
                            {
                                planningMetrics.ReciprocalCandidateCount += 1;
                                var firstRoute = new ResolvedRoute(conn, Direct(firstGeometry, firstLane).Points);
                                var secondRoute = new ResolvedRoute(second.Conn, Direct(secondGeometry, secondLane).Points);
                                var candidates = new List<ResolvedRoute> { firstRoute, secondRoute };
                                if (!candidates.All(entry => entry.Points.All(WithinScene))) continue;
                                if (!EndpointSlotsClear(candidates, others, PairSides)) continue;
                                if (!candidates.Select((entry, index) => (entry, index)).All(pair =>
                                {
                                    var geometry = ReferenceEquals(pair.entry.Conn, conn) ? firstGeometry : secondGeometry;
                                    if (!Geometry.PortHasCornerClearance(geometry.From, geometry.FromSide, pair.entry.Points[0])
                                        || !Geometry.PortHasCornerClearance(geometry.To, geometry.ToSide, pair.entry.Points[^1])) return false;
                                    return RouteIsClear(pair.entry.Conn, pair.entry.Points, geometry, [.. others, candidates[1 - pair.index]]);
                                })) continue;
                                var firstRect = labelRectFor?.Invoke(conn, firstRoute.Points,
                                    [.. PointsOf(others), .. PointsOf(candidates)],
                                    RectsOf(reservedLabels));
                                if (!LabelClears(firstRect, conn, [.. others, secondRoute], reservedLabels)) continue;
                                var secondRect = labelRectFor?.Invoke(second.Conn, secondRoute.Points,
                                    [.. PointsOf(others), .. PointsOf(candidates)],
                                    [.. RectsOf(reservedLabels), .. (firstRect is not null ? [firstRect] : new List<LabelRect>())]);
                                if (!LabelClears(secondRect, second.Conn, [.. others, firstRoute],
                                    [.. reservedLabels, .. (firstRect is not null ? [new ReservedLabel(conn, firstRect)] : new List<ReservedLabel>())])) continue;
                                var cost = ReadabilityCost(conn, firstRoute.Points, [.. others, secondRoute])
                                    + ReadabilityCost(second.Conn, secondRoute.Points, [.. others, firstRoute]);
                                if (cost < bestCost)
                                {
                                    best = (firstRoute, secondRoute, firstRect, secondRect);
                                    bestCost = cost;
                                }
                            }
                        }
                        if (best is null) continue;
                        var chosen = best.Value;
                        CachePath(conn, new(Geometry.RoundedPath(chosen.FirstRoute.Points, 8), chosen.FirstRoute.Points), firstSides);
                        CachePath(second.Conn, new(Geometry.RoundedPath(chosen.SecondRoute.Points, 8), chosen.SecondRoute.Points), secondSides);
                        first.Points = chosen.FirstRoute.Points;
                        second.Points = chosen.SecondRoute.Points;
                        planningMetrics.ReciprocalImprovedCount += 1;
                        jointlyImproved.Add(conn);
                        jointlyImproved.Add(second.Conn);
                        improved = true;
                        reservedLabels =
                        [
                            .. reservedLabels,
                            .. (chosen.FirstRect is not null ? [new ReservedLabel(conn, chosen.FirstRect)] : new List<ReservedLabel>()),
                            .. (chosen.SecondRect is not null ? [new ReservedLabel(second.Conn, chosen.SecondRect)] : new List<ReservedLabel>()),
                        ];
                    }
                    finally
                    {
                        if (!improved) reservedLabels = previousLabels;
                    }
                }
            }
            List<ReservedLabel>? RelocatedLabelsFor(JsonObject conn, RoutedPath routed, List<ResolvedRoute> others)
            {
                // A provisional label may occupy an otherwise clear corridor. Move at
                // most one automatic label, on its existing route, and accept the pair
                // only when both labels fit without borrowing other labels' space.
                var displaced = reservedLabels.Where(label => !ReferenceEquals(label.Conn, conn) && routed.Points.Skip(1).Select((end, index) => (end, index)).Any(pair =>
                    Geometry.SegmentIntersectsRect(new(routed.Points[pair.index], pair.end), label.Rect, LABEL_CLEARANCE))).ToList();
                if (displaced.Count != 1) return null;
                var owner = displaced[0].Conn;
                if (typeOwned.Has(owner) || HasAuthoredRouteGeometry(owner) || HasAuthoredLabelPlacement(owner)) return null;
                var labels = reservedLabels.Where(label => !ReferenceEquals(label.Conn, conn) && !ReferenceEquals(label.Conn, owner)).ToList();
                var ownRoute = new ResolvedRoute(conn, routed.Points);
                var routes = new List<ResolvedRoute>(others) { ownRoute };
                var moved = routes.First(entry => ReferenceEquals(entry.Conn, owner));
                foreach (var entry in new[] { moved, ownRoute })
                {
                    var rect = labelRectFor?.Invoke(entry.Conn, entry.Points, PointsOf(routes), RectsOf(labels));
                    if (!LabelClears(rect, entry.Conn, routes, labels)
                        || (rect is not null && components.Values.Any(component => Geometry.RectsOverlap(rect, component, -2)))) return null;
                    if (rect is not null) labels.Add(new(entry.Conn, rect));
                }
                return labels;
            }
            void ImproveComplexRoutes(bool onlyCrossings = false)
            {
                foreach (var entry in resolvedRoutes)
                {
                    var conn = entry.Conn;
                    if (jointlyImproved.Has(conn) || typeOwned.Has(conn) || HasAuthoredRouteGeometry(conn) || HasAuthoredLabelPlacement(conn)) continue;
                    var others = resolvedRoutes.Where(other => !ReferenceEquals(other, entry)).ToList();
                    // Preserve uncomplicated routes and their established inferred sides.
                    // Spend the comparison budget where the complete scene has a reading
                    // cost: a crossover, more than two bends, or no room for its label.
                    var crossingsBefore = CrossingCount(conn, entry.Points, others);
                    var currentLabel = labelRectFor?.Invoke(conn, entry.Points, PointsOf(others),
                        RectsOf(reservedLabels.Where(label => !ReferenceEquals(label.Conn, conn))));
                    var labelBlocked = conn["label"].Truthy() && labelRectFor is not null && currentLabel is null;
                    if (!labelBlocked && crossingsBefore == 0 && (onlyCrossings || entry.Points.Count <= 4)) continue;
                    (RoutedPath Routed, Sides Sides, LabelRect? Rect)? best = null;
                    var bestCost = labelBlocked ? double.PositiveInfinity : ReadabilityCost(conn, entry.Points, others);
                    foreach (var sides in CandidateSidePairs(conn))
                    {
                        var geometry = ConnectionGeometry(conn, sides);
                        var routed = RoutedForGeometry(conn, others, geometry);
                        planningMetrics.ReadabilityCandidateCount += 1;
                        if (!routed.Points.All(WithinScene) || !RouteIsClear(conn, routed.Points, geometry, others)) continue;
                        if (onlyCrossings && CrossingCount(conn, routed.Points, others) >= crossingsBefore) continue;
                        var cost = ReadabilityCost(conn, routed.Points, others);
                        if (cost < bestCost)
                        {
                            var rect = labelRectFor?.Invoke(conn, routed.Points, [.. PointsOf(others), routed.Points],
                                RectsOf(reservedLabels.Where(label => !ReferenceEquals(label.Conn, conn))));
                            // Include the reserved label in this pass's bounds. In
                            // particular, a shorter route must not widen and shrink the graph.
                            if (labelRectFor is not null && !LabelClears(rect, conn, others, reservedLabels.Where(label => !ReferenceEquals(label.Conn, conn)).ToList())) continue;
                            if (onlyCrossings && (!EndpointSlotsClear([new ResolvedRoute(conn, routed.Points)], others,
                                candidate => ReferenceEquals(candidate, conn) ? sides : selectedSides.Get(candidate))
                                || (rect is not null && components.Values.Any(component => Geometry.RectsOverlap(rect, component, -2))))) continue;
                            best = (routed, sides, rect);
                            bestCost = cost;
                        }
                    }
                    if (best is null) continue;
                    var chosen = best.Value;
                    CachePath(conn, chosen.Routed, chosen.Sides);
                    entry.Points = chosen.Routed.Points;
                    planningMetrics.ReadabilityImprovedCount += 1;
                    reservedLabels = reservedLabels.Where(label => !ReferenceEquals(label.Conn, conn)).ToList();
                    if (chosen.Rect is not null) reservedLabels.Add(new(conn, chosen.Rect));
                }
            }
            allowGridSearch = false;
            try
            {
                ImproveReciprocalPairs();
                ImproveComplexRoutes();
                // A bent route between nodes that overlap across the gap separating
                // them can run straight through facing sides inside that overlap.
                // Authored sides must already face; every other route, slot and label
                // stays fixed, and the straight lane must pass the same checks.
                foreach (var entry in resolvedRoutes)
                {
                    var conn = entry.Conn;
                    if (entry.Points.Count < 4 || jointlyImproved.Has(conn) || typeOwned.Has(conn)
                        || HasAuthoredRouteGeometry(conn) || HasAuthoredLabelPlacement(conn)) continue;
                    var from = Component(conn.Str("from"))!;
                    var to = Component(conn.Str("to"))!;
                    var facing = from.X + from.Width <= to.X ? new Sides("right", "left")
                        : to.X + to.Width <= from.X ? new Sides("left", "right")
                            : from.Y + from.Height <= to.Y ? new Sides("bottom", "top")
                                : to.Y + to.Height <= from.Y ? new Sides("top", "bottom") : null;
                    if (facing is null) continue;
                    var axis = facing.FromSide is "right" or "left" ? 1 : 0;
                    var (fromOrigin, fromSize, toOrigin, toSize) = axis == 1 ? (from.Y, from.Height, to.Y, to.Height) : (from.X, from.Width, to.X, to.Width);
                    var low = Math.Max(fromOrigin, toOrigin) + AUTOMATIC_PORT_CORNER_GUTTER;
                    var high = Math.Min(fromOrigin + fromSize, toOrigin + toSize) - AUTOMATIC_PORT_CORNER_GUTTER;
                    if (high < low
                        || (AuthoredSide(conn, "fromSide") && conn.Str("fromSide") != facing.FromSide)
                        || (AuthoredSide(conn, "toSide") && conn.Str("toSide") != facing.ToSide)) continue;
                    var sides = facing;
                    var geometry = ConnectionGeometry(conn, sides);
                    var start = geometry.Start;
                    var end = geometry.End;
                    double Clamp(double value) => Math.Min(high, Math.Max(low, value));
                    var others = resolvedRoutes.Where(other => !ReferenceEquals(other, entry)).ToList();
                    var otherLabels = reservedLabels.Where(label => !ReferenceEquals(label.Conn, conn)).ToList();
                    var crossingsBefore = CrossingCount(conn, entry.Points, others);
                    Sides? SidesOf(JsonObject candidate) => ReferenceEquals(candidate, conn) ? sides : selectedSides.Get(candidate);
                    var lanes = new JsSet<double>();
                    foreach (var value in new[] { Coord(start, axis), Coord(end, axis), (Coord(start, axis) + Coord(end, axis)) / 2, (low + high) / 2 }) lanes.Add(Clamp(value));
                    foreach (var lane in lanes)
                    {
                        var points = new List<Pt> { WithCoord(start, axis, lane), WithCoord(end, axis, lane) };
                        var routed = new RoutedPath(Geometry.RoundedPath(points, 8), points);
                        if (!points.All(WithinScene)
                            || !EndpointSlotsClear([new ResolvedRoute(conn, points)], others, SidesOf)
                            || !RouteIsClear(conn, points, geometry, others)
                            || CrossingCount(conn, points, others) > crossingsBefore) continue;
                        var rect = labelRectFor?.Invoke(conn, points, [.. PointsOf(others), points], RectsOf(otherLabels));
                        if (!LabelClears(rect, conn, others, otherLabels)) continue;
                        CachePath(conn, routed, sides);
                        entry.Points = points;
                        planningMetrics.ReadabilityImprovedCount += 1;
                        reservedLabels = [.. otherLabels, .. (rect is not null ? [new ReservedLabel(conn, rect)] : new List<ReservedLabel>())];
                        break;
                    }
                }
                // Finish the established route/straightening plan first. Moving a
                // label earlier could prevent a later edge from taking its straight
                // corridor, even though the local candidate was cheaper.
                foreach (var entry in resolvedRoutes)
                {
                    var conn = entry.Conn;
                    if (jointlyImproved.Has(conn) || typeOwned.Has(conn) || HasAuthoredRouteGeometry(conn) || HasAuthoredLabelPlacement(conn)) continue;
                    var others = resolvedRoutes.Where(other => !ReferenceEquals(other, entry)).ToList();
                    if (entry.Points.Count <= 4 && CrossingCount(conn, entry.Points, others) == 0) continue;
                    (RoutedPath Routed, Sides Sides, List<ReservedLabel> Labels)? best = null;
                    var bestCost = ReadabilityCost(conn, entry.Points, others);
                    if (labelRectFor is not null && reservedLabels.Any(label => !ReferenceEquals(label.Conn, conn)))
                    {
                        // One additional side sweep, with no grid search or cascading
                        // relayout. Temporary reservations can be ignored only after a
                        // replacement placement is verified against the complete scene.
                        foreach (var sides in CandidateSidePairs(conn))
                        {
                            var geometry = ConnectionGeometry(conn, sides);
                            RoutedPath routed;
                            bool clear;
                            honourReservedLabels = false;
                            try
                            {
                                routed = RoutedForGeometry(conn, others, geometry);
                                clear = RouteIsClear(conn, routed.Points, geometry, others);
                            }
                            finally
                            {
                                honourReservedLabels = true;
                            }
                            planningMetrics.LabelRelocationCandidateCount += 1;
                            var cost = ReadabilityCost(conn, routed.Points, others);
                            if (cost >= bestCost || !clear || !routed.Points.All(WithinScene)
                                || CrossingCount(conn, routed.Points, others) > CrossingCount(conn, entry.Points, others)
                                || !EndpointSlotsClear([new ResolvedRoute(conn, routed.Points)], others,
                                    candidate => ReferenceEquals(candidate, conn) ? sides : selectedSides.Get(candidate))) continue;
                            var labels = RelocatedLabelsFor(conn, routed, others);
                            if (labels is null) continue;
                            best = (routed, sides, labels);
                            bestCost = cost;
                        }
                    }
                    if (best is null) continue;
                    var chosen = best.Value;
                    CachePath(conn, chosen.Routed, chosen.Sides);
                    entry.Points = chosen.Routed.Points;
                    reservedLabels = chosen.Labels;
                    planningMetrics.LabelRelocationImprovedCount += 1;
                }
                // A side's midpoint may be blocked even when a corner port has a
                // clear one-bend route. Probe bent automatic routes, keeping
                // all other paths/labels fixed and the existing corner inset intact.
                // Eight perpendicular side pairs, at most three slots per endpoint:
                // 72 cheap candidates per route, without another grid search.
                foreach (var entry in resolvedRoutes)
                {
                    var conn = entry.Conn;
                    if (entry.Points.Count < 4 || jointlyImproved.Has(conn) || typeOwned.Has(conn)
                        || HasAuthoredRouteGeometry(conn) || HasAuthoredLabelPlacement(conn)) continue;
                    var others = resolvedRoutes.Where(other => !ReferenceEquals(other, entry)).ToList();
                    var labels = reservedLabels.Where(label => !ReferenceEquals(label.Conn, conn)).ToList();
                    var crossingsBefore = CrossingCount(conn, entry.Points, others);
                    (RoutedPath Routed, Sides Sides, LabelRect? Rect)? best = null;
                    var bestCost = ReadabilityCost(conn, entry.Points, others);
                    static List<Pt> Slots(Box rect, string side, Pt preferred)
                    {
                        var horizontal = side is "left" or "right";
                        var axis = horizontal ? 1 : 0;
                        var origin = horizontal ? rect.Y : rect.X;
                        var size = horizontal ? rect.Height : rect.Width;
                        var inset = Math.Min(AUTOMATIC_PORT_CORNER_GUTTER, size / 2);
                        var values = new JsSet<double>();
                        values.Add(Coord(preferred, axis));
                        values.Add(origin + inset);
                        values.Add(origin + size - inset);
                        return values.Select(value => WithCoord(preferred, axis, value)).Where(point => Geometry.PortHasCornerClearance(rect, side, point)).ToList();
                    }
                    static bool Horizontal(string side) => side is "left" or "right";
                    foreach (var sides in CandidateSidePairs(conn))
                    {
                        if (Horizontal(sides.FromSide) == Horizontal(sides.ToSide)) continue;
                        var geometry = ConnectionGeometry(conn, sides);
                        foreach (var start in Slots(geometry.From, sides.FromSide, geometry.Start))
                        {
                            foreach (var end in Slots(geometry.To, sides.ToSide, geometry.End))
                            {
                                var corner = Horizontal(sides.FromSide) ? new Pt(end.X, start.Y) : new Pt(start.X, end.Y);
                                var points = Geometry.NormalizeRoutePoints([start, corner, end]);
                                var routed = new RoutedPath(Geometry.RoundedPath(points, 8), points);
                                var cost = ReadabilityCost(conn, routed.Points, others);
                                if (cost >= bestCost || points.Count != 3 || !points.All(WithinScene)
                                    || !EndpointSlotsClear([new ResolvedRoute(conn, points)], others,
                                        candidate => ReferenceEquals(candidate, conn) ? sides : selectedSides.Get(candidate))
                                    || !RouteIsClear(conn, routed.Points, geometry with { Start = start, End = end, Crowded = false }, others)
                                    || CrossingCount(conn, points, others) > crossingsBefore) continue;
                                var rect = labelRectFor?.Invoke(conn, points, [.. PointsOf(others), points], RectsOf(labels));
                                if (!LabelClears(rect, conn, others, labels)
                                    || (rect is not null && components.Values.Any(component => Geometry.RectsOverlap(rect, component, -2)))) continue;
                                best = (routed, sides, rect);
                                bestCost = cost;
                            }
                        }
                    }
                    if (best is null) continue;
                    var chosen = best.Value;
                    CachePath(conn, chosen.Routed, chosen.Sides);
                    entry.Points = chosen.Routed.Points;
                    reservedLabels = [.. labels, .. (chosen.Rect is not null ? [new ReservedLabel(conn, chosen.Rect)] : new List<ReservedLabel>())];
                    planningMetrics.ReadabilityImprovedCount += 1;
                }
                // Complete the original bounded plan first. Only a remaining crossing
                // can justify using an extra lower corridor; an already clear scene
                // must not grow merely because its labels could move into that space.
                if (readabilityBottomMargin > 0)
                {
                    boundsBottom += readabilityBottomMargin;
                    ImproveComplexRoutes(true);
                }
            }
            finally
            {
                allowGridSearch = true;
            }
        }
        planningMetrics.RouteCount = resolvedRoutes.Count;
        planningMetrics.ExplicitRouteCount = explicitRoutes.Count;
        planningMetrics.AutomaticRouteCount = automatic.Count;
        routesPlanning = false;
        routesPlanned = true;
    }

    public JsMap<JsonObject, SpreadPorts> Ports => automaticPorts;

    public RoutedPath PathFor(JsonObject conn)
    {
        PlanRoutes();
        if (pathCache.Has(conn)) return pathCache.Get(conn)!;
        return ComputePath(conn, []);
    }

    public JsonObject RoutingMetrics()
    {
        PlanRoutes();
        return planningMetrics.ToJson();
    }
}
