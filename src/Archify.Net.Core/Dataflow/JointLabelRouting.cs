using System.Text.Json.Nodes;

namespace Archify.Net;

// The `metrics` out-object of repairLabelRoutes.
internal sealed class LabelRouteMetrics
{
    public int Searches { get; set; }
    public int MaximumSearches { get; set; }
    public int ChangedLabels { get; set; }
    public int ChangedRoutes { get; set; }
}

// joint-label-routing.mjs. Routes are `{ relation, relationIndex, points }` RoutedRelations; `{ ...route, points }` is `with`, and
// every route/label compare is by reference (R9): RoutedRelation is a record, so `==` and List.Contains would compare values.
internal static class JointLabelRouting
{
    static List<Segment> Segments(IReadOnlyList<Pt> points)
    {
        var segments = new List<Segment>();
        for (var index = 0; index < points.Count - 1; index++) segments.Add(new(points[index], points[index + 1]));
        return segments;
    }
    static bool Related(JsonObject a, JsonObject b) =>
        new[] { a.Str("from"), a.Str("to") }.Any(id => id == b.Str("from") || id == b.Str("to"));
    static double Length(IReadOnlyList<Pt> points) =>
        Segments(points).Aggregate(0.0, (sum, s) => sum + Math.Abs(s.Start.X - s.End.X) + Math.Abs(s.Start.Y - s.End.Y));
    // `Math.min(...)`: an empty spread is +Infinity and `null` (non-finite geometry) reads as 0.
    static double Distance(IReadOnlyList<Pt> points, Rect rect, double limit = 36)
    {
        var min = double.PositiveInfinity;
        foreach (var segment in Segments(points)) min = Math.Min(min, Geometry.SegmentRectClearanceWithin(segment, rect, limit) ?? 0);
        return min;
    }
    static bool Masks(IReadOnlyList<Pt> points, Rect rect) => Distance(points, rect, 4) + 0.0001 < 4;
    static readonly string[] PinKeys = ["fromSide", "toSide", "channelX", "channelY", "labelAt", "labelDx", "labelDy", "labelSegment"];
    static bool Pinned(JsonObject flow) => flow["via"].Truthy() || (flow["route"].Truthy() && flow.Str("route") != "auto")
        || PinKeys.Any(flow.ContainsKey);
    static int CrossingCount(IReadOnlyList<Pt> a, IReadOnlyList<Pt> b) => Segments(a).Sum(s => Segments(b)
        .Count(t => Geometry.ProperSegmentIntersection(s.Start, s.End, t.Start, t.End) is not null));
    // `JSON.stringify(points)`: number arrays print as their Number strings.
    static string PointsKey(IReadOnlyList<Pt> points) => string.Join(";", points.Select(p => $"{Js.Num(p.X)},{Js.Num(p.Y)}"));
    static Rect Inflate(Rect r, double by) => new(r.X - by, r.Y - by, r.Width + by * 2, r.Height + by * 2);

    sealed record Candidate(LabelRect Rect, List<Pt> Points, List<RoutedRelation> Blocked, double OwnDistance, double Move);

    // One complete label, its own bridge, and the few routes blocking its slot
    // are a single proposal. Nothing is applied until all the geometry clears.
    // Keep all endpoint coordinates and every other label fixed.
    // Returns the input lists themselves when nothing changed (dataflow-compiler.mjs:1365 tests `labels !==`).
    public static (List<LabelRect> Labels, List<RoutedRelation> Routes) RepairLabelRoutes(List<LabelRect> labels, List<RoutedRelation> routes,
        IReadOnlyList<Box> nodes, IReadOnlyList<Rect> titles, IReadOnlyList<Frame> frames, IReadOnlyList<double> viewBox, double placementBottom,
        Func<JsonObject, (string? FromSide, string? ToSide)> sideFor, int maximumSearches = 32, LabelRouteMetrics? metrics = null)
    {
        metrics ??= new();
        if (nodes.Count + titles.Count + labels.Count - 1 > 80)
        {
            (metrics.Searches, metrics.MaximumSearches, metrics.ChangedLabels, metrics.ChangedRoutes) = (0, maximumSearches, 0, 0);
            return (labels, routes);
        }
        var currentLabels = labels;
        var currentRoutes = routes;
        var searches = 0;
        var rectangles = nodes.Select(n => (Rect)n).Concat(titles).ToList();
        var borderSegments = frames.SelectMany(Geometry.FrameBorderSegments).ToList();
        bool ClearRect(LabelRect rect, int index) => rect.X >= 0 && rect.Y >= 0
            && rect.X + rect.Width <= viewBox[0] && rect.Y + rect.Height <= placementBottom
            && !rectangles.Any(node => Geometry.RectsOverlap(rect, node, 2))
            && !currentLabels.Where((_, i) => i != index).Any(other => Geometry.RectsOverlap(rect, other, 2));
        bool Intrudes(RoutedRelation route, List<Pt> points) => nodes.Any(node => Segments(points).Where((segment, index) => (
            !((node.Id == route.Relation!.Str("from") && index == 0)
              || (node.Id == route.Relation!.Str("to") && index == points.Count - 2))
            && Geometry.SegmentIntersectsRect(segment, node, 2))).Any());
        bool RouteClear(RoutedRelation route, List<Pt> points, List<LabelRect> nextLabels, List<RoutedRelation> nextRoutes, double bottom)
        {
            var (fromSide, toSide) = sideFor(route.Relation!);
            return Geometry.RouteHonorsEndpointSides(points, fromSide, toSide)
                && points.All(p => p.X >= 0 && p.Y >= 0 && p.X <= viewBox[0] && p.Y <= bottom)
                && !Intrudes(route, points)
                && Geometry.CollectRouteRhythmIssues([new RoutedRelation(points)]).Count == 0
                && !Geometry.CollectBorderRuns([new RoutedRelation(points)], frames).Any(hit => hit.Side != "bottom")
                && !nextLabels.Any(rect => rect.RelationIndex != route.RelationIndex && Masks(points, rect))
                && nextLabels.Where(rect => rect.RelationIndex == route.RelationIndex).All(rect => Distance(points, rect) <= 36)
                && nextRoutes.All(other => other.RelationIndex == route.RelationIndex
                    || !Pinned(other.Relation!) || CrossingCount(points, other.Points!) == 0)
                && nextRoutes.All(other => other.RelationIndex == route.RelationIndex
                    || Geometry.CollectAmbiguousCorridors([route with { Points = points }, other], includeSharedEndpoints: (_, _) => true).Count == 0
                    || Geometry.CollectAmbiguousCorridors([route, currentRoutes.First(r => r.RelationIndex == other.RelationIndex)],
                        includeSharedEndpoints: (_, _) => true).Count > 0);
        }
        // `for (const [labelIndex, label] of currentLabels.entries())` iterates the array bound at loop start: the input list.
        for (var labelIndex = 0; labelIndex < labels.Count; labelIndex++)
        {
            var label = labels[labelIndex];
            if (searches >= maximumSearches) break;
            var own = currentRoutes.FirstOrDefault(route => route.RelationIndex == label.RelationIndex);
            if (own is null || Pinned(own.Relation!)
                || (!currentRoutes.Any(route => !ReferenceEquals(route, own) && Masks(route.Points!, label))
                    && !nodes.Any(node => Geometry.RectsOverlap(label, node, -2)))) continue;
            var (fromSide, toSide) = sideFor(own.Relation!);
            var ownPoints = own.Points!;
            var ownOptions = new JsMap<string, List<Pt>>();
            ownOptions.Set(PointsKey(ownPoints), ownPoints);
            foreach (var endpointStubPx in new double[] { 24, 16 })
                Geometry.AutomaticPortRhythmBridge(ownPoints[0], ownPoints[^1], fromSide, toSide, endpointStubPx,
                    accept: points => { ownOptions.Set(PointsKey(points), points); return false; });
            var xs = new JsSet<double>();
            var ys = new JsSet<double>();
            xs.Add(label.X); ys.Add(label.Y);
            foreach (var points in ownOptions.Values)
                foreach (var (start, end) in Segments(points))
                {
                    foreach (var x in new[] { start.X, end.X, (start.X + end.X) / 2 }) xs.Add(x - label.Width / 2);
                    foreach (var y in new[] { start.Y, end.Y, (start.Y + end.Y) / 2 }) { ys.Add(y - label.Height - 4); ys.Add(y + 4); }
                }
            var neighbours = rectangles.Concat(currentLabels.Where((_, index) => index != labelIndex).Select(l => (Rect)l));
            foreach (var rect in neighbours)
            {
                xs.Add(rect.X - label.Width - 2); xs.Add(rect.X + rect.Width + 2);
                ys.Add(rect.Y - label.Height - 2); ys.Add(rect.Y + rect.Height + 2);
            }
            foreach (var route in currentRoutes)
                foreach (var (start, end) in Segments(route.Points!))
                {
                    if (start.X == end.X) { xs.Add(start.X - label.Width - 8); xs.Add(start.X + 8); }
                    if (start.Y == end.Y) { ys.Add(start.Y - label.Height - 8); ys.Add(start.Y + 8); }
                }
            double minX = double.PositiveInfinity, maxX = double.NegativeInfinity, minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
            foreach (var points in ownOptions.Values)
                foreach (var p in points) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
            var candidates = new List<Candidate>();
            foreach (var x in xs)
            {
                if (x + label.Width < minX - 36 || x > maxX + 36) continue;
                foreach (var y in ys)
                {
                    if (y + label.Height < minY - 36 || y > maxY + 36) continue;
                    var rect = label.Clone();   // `{ ...label, x, y, lx, ly }`
                    rect.X = x; rect.Y = y; rect.Lx = x + label.Width / 2; rect.Ly = y + ((label.Ly ?? double.NaN) - label.Y);
                    if (!ClearRect(rect, labelIndex)) continue;
                    foreach (var points in ownOptions.Values)
                    {
                        var ownDistance = Distance(points, rect);
                        if (ownDistance < 4 || ownDistance > 36) continue;
                        var nextLabels = currentLabels.Select((other, index) => index == labelIndex ? rect : other).ToList();
                        if (!RouteClear(own, points, nextLabels, currentRoutes, placementBottom)) continue;
                        var blocked = currentRoutes.Where(route => !ReferenceEquals(route, own) && Masks(route.Points!, rect)).ToList();
                        if (blocked.Count > 4 || blocked.Any(route => Pinned(route.Relation!))) continue;
                        var stableDistance = double.PositiveInfinity;
                        foreach (var route in currentRoutes)
                            if (!ReferenceEquals(route, own) && !blocked.Any(b => ReferenceEquals(b, route)))
                                stableDistance = Math.Min(stableDistance, Distance(route.Points!, rect));
                        if (ownDistance > stableDistance + 0.0001) continue;
                        candidates.Add(new(rect, points, blocked, ownDistance, Js.Hypot(x - label.X, y - label.Y)));
                    }
                }
            }
            Js.Sort(candidates, (a, b) => Js.Or(a.Blocked.Count - b.Blocked.Count, Js.Or(a.OwnDistance - b.OwnDistance, a.Move - b.Move)));
            var chosen = new List<Candidate>();
            // Avoid spending every search on virtually identical neighbouring slots.
            foreach (var c in candidates)
            {
                if (chosen.Any(other => Js.Hypot(c.Rect.X - other.Rect.X, c.Rect.Y - other.Rect.Y) < 8
                    && PointsKey(c.Points) == PointsKey(other.Points))) continue;
                chosen.Add(c); if (chosen.Count >= 16) break;
            }
            (List<LabelRect> Labels, List<RoutedRelation> Routes)? accepted = null;
            foreach (var candidate in chosen)
            {
                if (searches >= maximumSearches) break;
                var nextLabels = currentLabels.Select((other, index) => index == labelIndex ? candidate.Rect : other).ToList();
                var nextRoutes = currentRoutes.Select(route => ReferenceEquals(route, own) ? route with { Points = candidate.Points } : route).ToList();
                // Free the longest occupied corridor first. Each subsequent route sees
                // the accepted proposal; all unchanged routes remain obstacles.
                var blocked = candidate.Blocked.ToList();
                Js.Sort(blocked, (a, b) => Length(b.Points!) - Length(a.Points!));
                var failed = false;
                foreach (var old in blocked)
                {
                    if (searches >= maximumSearches) { failed = true; break; }
                    searches++;
                    var (oldFrom, oldTo) = sideFor(old.Relation!);
                    var oldPoints = old.Points!;
                    var endpoints = new[] { old.Relation!.Str("from"), old.Relation!.Str("to") };
                    var obstacles = nodes.Select(node => endpoints.Contains(node.Id) ? (Rect)node : Inflate(node, 12))
                        .Concat(titles)
                        .Concat(nextLabels.Where(rect => rect.RelationIndex != old.RelationIndex).Select(rect => Inflate(rect, 2)))
                        .ToList();
                    var found = RouteQuality.ShortestOrthogonalGridRoute(oldPoints[0], oldPoints[^1], [oldPoints[0], oldPoints[^1]], obstacles, oldFrom, oldTo,
                        new GridRouteOptions(Clearance: 2, MaximumObstacleCount: 80, EndpointStubPx: 24, MaximumGridNodes: 4096,
                            AvoidedSegments: nextRoutes.Where(other => other.RelationIndex != old.RelationIndex && !Related(old.Relation!, other.Relation!))
                                .SelectMany(other => Segments(other.Points!)).ToList(),
                            AllowAvoidedCrossings: true, MinimumAvoidedOverlapPx: 8, RouteSeparationPx: 8,
                            MinimumSegmentPx: 16, BendPenaltyPx: 48, BorderSegments: borderSegments,
                            Accept: points => RouteClear(old, points, nextLabels, nextRoutes, placementBottom + label.Height)));
                    var points = found is null ? null : Geometry.NormalizeRoutePoints(found.Points);
                    if (points is null) { failed = true; break; }
                    nextRoutes[nextRoutes.FindIndex(route => route.RelationIndex == old.RelationIndex)] = old with { Points = points };
                }
                if (failed) continue;
                var foreignDistance = double.PositiveInfinity;
                foreach (var route in nextRoutes)
                    if (route.RelationIndex != own.RelationIndex) foreignDistance = Math.Min(foreignDistance, Distance(route.Points!, candidate.Rect));
                // The original label is invalid. A collision-free repair may need a longer
                // corridor; never rank that invalid geometry above a valid proposal.
                if (candidate.OwnDistance > foreignDistance + 0.0001) continue;
                accepted = (nextLabels, nextRoutes); break;
            }
            if (accepted is { } a) { currentLabels = a.Labels; currentRoutes = a.Routes; }
        }
        metrics.Searches = searches;
        metrics.MaximumSearches = maximumSearches;
        metrics.ChangedLabels = currentLabels.Where((rect, i) => !ReferenceEquals(rect, labels[i])).Count();
        metrics.ChangedRoutes = currentRoutes.Where((route, i) => !ReferenceEquals(route, routes[i])).Count();
        return (currentLabels, currentRoutes);
    }
}
