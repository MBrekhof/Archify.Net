using System.Text.Json.Nodes;

namespace Archify.Net;

// route-quality.mjs: the obstacle-clearing orthogonal grid search and the authored-detour gate built on it.

// The `metrics` object shortestOrthogonalGridRoute patches with Object.assign.
public sealed class GridMetrics
{
    public string Status { get; set; } = "";
    public double MaximumGridNodes { get; set; }
    public int ObstacleCount { get; set; }
    public int AvoidedSegmentCount { get; set; }
    public int CoordinateCount { get; set; }
    public int CandidateNodeCount { get; set; }
    public int UsableNodeCount { get; set; }
    public int GraphEdgeCount { get; set; }
    public int VisitedNodeCount { get; set; }
    public int AttemptCount { get; set; }
}

public sealed record GridRoute(List<Pt> Points, double? Length, int ObstacleCount);

// The destructured options of shortestOrthogonalGridRoute (route-quality.mjs:261-280) with their defaults;
// `EndpointStubPx` null means upstream's `clearance + 2`.
public sealed record GridRouteOptions(double Clearance, double MaximumObstacleCount, double? EndpointStubPx = null,
    double MaximumGridNodes = double.PositiveInfinity, IReadOnlyList<Segment>? AvoidedSegments = null, bool AllowAvoidedCrossings = false,
    double MinimumAvoidedOverlapPx = 8, double RouteSeparationPx = 8, double MinimumSegmentPx = 8,
    IReadOnlyList<BorderSegment>? BorderSegments = null, double BendPenaltyPx = 0, Func<List<Pt>, bool>? Accept = null, GridMetrics? Metrics = null);

// DEFAULTS, overridable per call through `thresholds`.
public sealed record DetourThresholds(double Clearance = 2, double MinimumDetourRatio = 2.5, double MinimumExcessLengthPx = 200,
    double MinimumEmptyExcursionPx = 96, double MaximumObstacleCount = 80, double SharedCorridorMinimumPx = 32);

public static class RouteQuality
{
    static readonly Dictionary<string, Pt> Outward = new(StringComparer.Ordinal)   // lookup only
    {
        ["left"] = new(-1, 0),
        ["right"] = new(1, 0),
        ["top"] = new(0, -1),
        ["bottom"] = new(0, 1),
    };

    static double Rounded(double value) => Js.Round(value * 100) / 100;

    // `${point[0]}\u0000${point[1]}` — Pt equality (SameValueZero on both doubles) is the same key.
    readonly record struct State(Pt Node, char Axis);

    sealed class MinHeap
    {
        public readonly List<(State Key, double Distance)> Entries = [];

        public void Push(State key, double distance)
        {
            var entry = (key, distance);
            Entries.Add(entry);
            var index = Entries.Count - 1;
            while (index > 0)
            {
                var parent = (index - 1) / 2;
                if (Entries[parent].Distance <= distance) break;
                Entries[index] = Entries[parent];
                index = parent;
            }
            Entries[index] = entry;
        }

        public (State Key, double Distance)? Pop()
        {
            if (Entries.Count == 0) return null;
            var first = Entries[0];
            var last = Entries[^1];
            Entries.RemoveAt(Entries.Count - 1);
            if (Entries.Count == 0) return first;
            var index = 0;
            while (true)
            {
                var left = index * 2 + 1;
                var right = left + 1;
                if (left >= Entries.Count) break;
                var child = right < Entries.Count
                    && Entries[right].Distance < Entries[left].Distance ? right : left;
                if (Entries[child].Distance >= last.Distance) break;
                Entries[index] = Entries[child];
                index = child;
            }
            Entries[index] = last;
            return first;
        }
    }

    static double? OrthogonalLength(IReadOnlyList<Pt> points)
    {
        double total = 0;
        for (var index = 0; index < points.Count - 1; index += 1)
        {
            var (x1, y1) = points[index];
            var (x2, y2) = points[index + 1];
            if (x1 != x2 && y1 != y2) return null;
            total += Math.Abs(x2 - x1) + Math.Abs(y2 - y1);
        }
        return total;
    }

    static string? InferredSide(IReadOnlyList<Pt> points, string endpoint)
    {
        if (points.Count < 2) return null;
        var start = endpoint == "source" ? points[0] : points[^2];
        var end = endpoint == "source" ? points[1] : points[^1];
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        if (endpoint == "source")
        {
            if (dx > 0 && dy == 0) return "right";
            if (dx < 0 && dy == 0) return "left";
            if (dy > 0 && dx == 0) return "bottom";
            if (dy < 0 && dx == 0) return "top";
        }
        else
        {
            if (dx > 0 && dy == 0) return "left";
            if (dx < 0 && dy == 0) return "right";
            if (dy > 0 && dx == 0) return "top";
            if (dy < 0 && dx == 0) return "bottom";
        }
        return null;
    }

    static Pt MoveOutward(Pt point, string side, double distance)
    {
        var (dx, dy) = Outward.TryGetValue(side, out var vector) ? vector : new Pt(0, 0);
        return new(point.X + dx * distance, point.Y + dy * distance);
    }

    static Rect ExpandedRect(Rect rect, double clearance) => new(
        rect.X - clearance,
        rect.Y - clearance,
        rect.Width + clearance * 2,
        rect.Height + clearance * 2);

    sealed record Bounds(double Left, double Top, double Right, double Bottom, double Width, double Height)
    {
        public JsonObject ToJson() => new() { ["left"] = Left, ["top"] = Top, ["right"] = Right, ["bottom"] = Bottom, ["width"] = Width, ["height"] = Height };
    }

    static bool Usable(Rect rect) => Geometry.IsFinitePoint(rect.X, rect.Y, rect.Width, rect.Height) && rect.Width >= 0 && rect.Height >= 0;

    static Bounds? BoundsForRects(IEnumerable<Rect> rects)
    {
        var usable = rects.Where(Usable).ToList();
        if (usable.Count == 0) return null;
        var left = usable.Min(rect => rect.X);
        var top = usable.Min(rect => rect.Y);
        var right = usable.Max(rect => rect.X + rect.Width);
        var bottom = usable.Max(rect => rect.Y + rect.Height);
        return new(left, top, right, bottom, right - left, bottom - top);
    }

    static Bounds? BoundsForPoints(IReadOnlyList<Pt> points)
    {
        if (points.Count == 0) return null;
        var left = points.Min(p => p.X);
        var top = points.Min(p => p.Y);
        var right = points.Max(p => p.X);
        var bottom = points.Max(p => p.Y);
        return new(left, top, right, bottom, right - left, bottom - top);
    }

    sealed record Excursion(double Left, double Top, double Right, double Bottom, double Maximum)
    {
        public JsonObject ToJson() => new() { ["left"] = Left, ["top"] = Top, ["right"] = Right, ["bottom"] = Bottom, ["maximum"] = Maximum };
    }

    static Excursion? OutsideExcursion(Bounds? routeBounds, Bounds? contentBounds)
    {
        if (routeBounds is null || contentBounds is null) return null;
        var left = Math.Max(0, contentBounds.Left - routeBounds.Left);
        var top = Math.Max(0, contentBounds.Top - routeBounds.Top);
        var right = Math.Max(0, routeBounds.Right - contentBounds.Right);
        var bottom = Math.Max(0, routeBounds.Bottom - contentBounds.Bottom);
        return new(left, top, right, bottom, Math.Max(Math.Max(left, top), Math.Max(right, bottom)));
    }

    static double PointDistanceFromRect(Pt point, Rect rect)
    {
        var dx = Math.Max(Math.Max(rect.X - point.X, 0), point.X - (rect.X + rect.Width));
        var dy = Math.Max(Math.Max(rect.Y - point.Y, 0), point.Y - (rect.Y + rect.Height));
        return dx + dy;
    }

    static (double Maximum, Pt Point)? EmptyControlPointClearance(IReadOnlyList<Pt> points, IEnumerable<Rect> contentRects)
    {
        var controls = points.Skip(1).Take(Math.Max(0, points.Count - 2)).ToList();
        var rects = contentRects.Where(Usable).ToList();
        if (controls.Count == 0 || rects.Count == 0) return null;
        var distances = controls.Select(point => rects.Min(rect => PointDistanceFromRect(point, rect))).ToList();
        var maximum = distances.Max();
        return (maximum, controls[distances.IndexOf(maximum)]);
    }

    static bool PointBlocked(Pt point, List<Rect> obstacles) => obstacles.Any(rect =>
        point.X >= rect.X && point.X <= rect.X + rect.Width
            && point.Y >= rect.Y && point.Y <= rect.Y + rect.Height);

    // Prune only against the candidate segment, never the route's initial bounds:
    // a later detour can still encounter any obstacle in the complete inventory.
    // Keep epsilon-near bounds for the existing exact predicates to decide.
    static bool SegmentBlocked(Pt start, Pt end, List<Rect> obstacles)
    {
        var minX = Math.Min(start.X, end.X);
        var maxX = Math.Max(start.X, end.X);
        var minY = Math.Min(start.Y, end.Y);
        var maxY = Math.Max(start.Y, end.Y);
        return obstacles.Any(rect =>
        {
            if (maxX < Math.Min(rect.X, rect.X + rect.Width) - 0.0001
                || minX > Math.Max(rect.X, rect.X + rect.Width) + 0.0001
                || maxY < Math.Min(rect.Y, rect.Y + rect.Height) - 0.0001
                || minY > Math.Max(rect.Y, rect.Y + rect.Height) + 0.0001) return false;
            return Geometry.SegmentIntersectsRect(new(start, end), rect);
        });
    }

    static bool SegmentConflictsWithAvoided(Pt start, Pt end, List<Segment> avoidedSegments, double minimumOverlapPx, bool allowCrossings)
    {
        var minX = Math.Min(start.X, end.X);
        var maxX = Math.Max(start.X, end.X);
        var minY = Math.Min(start.Y, end.Y);
        var maxY = Math.Max(start.Y, end.Y);
        return avoidedSegments.Any(segment =>
        {
            // A zero overlap threshold also rejects disjoint segments (0 >= 0).
            if (minimumOverlapPx > 0 && (maxX < Math.Min(segment.Start.X, segment.End.X) - 0.0001
                || minX > Math.Max(segment.Start.X, segment.End.X) + 0.0001
                || maxY < Math.Min(segment.Start.Y, segment.End.Y) - 0.0001
                || minY > Math.Max(segment.Start.Y, segment.End.Y) + 0.0001)) return false;
            return (!allowCrossings && (Geometry.ProperSegmentIntersection(start, end, segment.Start, segment.End) is not null
                || OrthogonalTouchOnAvoidedInterior(start, end, segment.Start, segment.End)))
                || CollinearOverlap(start, end, segment.Start, segment.End) >= minimumOverlapPx;
        });
    }

    static bool OrthogonalTouchOnAvoidedInterior(Pt start, Pt end, Pt avoidedStart, Pt avoidedEnd)
    {
        const double epsilon = 0.0001;
        var candidateHorizontal = Math.Abs(start.Y - end.Y) <= epsilon;
        var candidateVertical = Math.Abs(start.X - end.X) <= epsilon;
        var avoidedHorizontal = Math.Abs(avoidedStart.Y - avoidedEnd.Y) <= epsilon;
        var avoidedVertical = Math.Abs(avoidedStart.X - avoidedEnd.X) <= epsilon;
        if (candidateHorizontal && avoidedVertical)
        {
            var x = avoidedStart.X;
            var y = start.Y;
            return x >= Math.Min(start.X, end.X) - epsilon
                && x <= Math.Max(start.X, end.X) + epsilon
                && y > Math.Min(avoidedStart.Y, avoidedEnd.Y) + epsilon
                && y < Math.Max(avoidedStart.Y, avoidedEnd.Y) - epsilon;
        }
        if (candidateVertical && avoidedHorizontal)
        {
            var x = start.X;
            var y = avoidedStart.Y;
            return y >= Math.Min(start.Y, end.Y) - epsilon
                && y <= Math.Max(start.Y, end.Y) + epsilon
                && x > Math.Min(avoidedStart.X, avoidedEnd.X) + epsilon
                && x < Math.Max(avoidedStart.X, avoidedEnd.X) - epsilon;
        }
        return false;
    }

    static bool PointOnSegmentInterior(Pt point, Pt start, Pt end)
    {
        const double epsilon = 0.0001;
        var cross = (end.X - start.X) * (point.Y - start.Y)
            - (end.Y - start.Y) * (point.X - start.X);
        if (Math.Abs(cross) > epsilon) return false;
        var dot = (point.X - start.X) * (point.X - end.X)
            + (point.Y - start.Y) * (point.Y - end.Y);
        return dot < -epsilon;
    }

    static bool PointOnAvoidedInterior(Pt point, List<Segment> avoidedSegments) =>
        avoidedSegments.Any(segment => PointOnSegmentInterior(point, segment.Start, segment.End));

    public static GridRoute? ShortestOrthogonalGridRoute(Pt start, Pt end, IReadOnlyList<Pt> points, IEnumerable<Rect> obstacles,
        string? fromSide, string? toSide, GridRouteOptions options)
    {
        var clearance = options.Clearance;
        var endpointStubPx = options.EndpointStubPx ?? clearance + 2;
        var maximumGridNodes = options.MaximumGridNodes;
        var routeSeparationPx = options.RouteSeparationPx;
        var minimumSegmentPx = options.MinimumSegmentPx;
        var bendPenaltyPx = options.BendPenaltyPx;
        var metrics = options.Metrics;
        if (metrics is not null)
        {
            metrics.Status = "initializing";
            metrics.MaximumGridNodes = maximumGridNodes;
            metrics.ObstacleCount = 0;
            metrics.AvoidedSegmentCount = 0;
            metrics.CoordinateCount = 0;
            metrics.CandidateNodeCount = 0;
            metrics.UsableNodeCount = 0;
            metrics.GraphEdgeCount = 0;
            metrics.VisitedNodeCount = 0;
            metrics.AttemptCount = 0;
        }
        if (fromSide is null || toSide is null || !Outward.ContainsKey(fromSide) || !Outward.ContainsKey(toSide))
        {
            if (metrics is not null) metrics.Status = "unsupported-endpoint-side";
            return null;
        }
        var startStub = MoveOutward(start, fromSide, endpointStubPx);
        var endStub = MoveOutward(end, toSide, endpointStubPx);
        // The graph may legally leave the initial endpoint bounds to find a clear
        // corridor. Keep every bounded obstacle and occupied relationship visible
        // to that search; filtering them against the initial box lets a detour walk
        // straight through geometry that only becomes relevant after it leaves the
        // box. The explicit obstacle/node budgets below keep this deterministic.
        var expanded = obstacles
            .Where(rect => Geometry.IsFinitePoint(rect.X, rect.Y, rect.Width, rect.Height))
            .Select(rect => ExpandedRect(rect, clearance)).ToList();
        var relevantAvoidedSegments = (options.AvoidedSegments ?? []).ToList();
        // Frame borders may be crossed perpendicularly but never borrowed as a
        // corridor: the composition gate rejects any collinear run along them.
        var relevantBorderSegments = (options.BorderSegments ?? []).ToList();
        if (metrics is not null)
        {
            metrics.ObstacleCount = expanded.Count;
            metrics.AvoidedSegmentCount = relevantAvoidedSegments.Count;
        }
        if (expanded.Count > options.MaximumObstacleCount)
        {
            if (metrics is not null) metrics.Status = "obstacle-budget-exceeded";
            return null;
        }

        var xs = new HashSet<double> { startStub.X, endStub.X };   // lookup only; read back sorted
        var ys = new HashSet<double> { startStub.Y, endStub.Y };
        foreach (var p in points) { xs.Add(p.X); ys.Add(p.Y); }
        foreach (var rect in expanded)
        {
            xs.Add(rect.X - 1);
            xs.Add(rect.X + rect.Width + 1);
            ys.Add(rect.Y - 1);
            ys.Add(rect.Y + rect.Height + 1);
        }
        foreach (var segment in relevantAvoidedSegments)
        {
            var (segmentStart, segmentEnd) = (segment.Start, segment.End);
            xs.Add(segmentStart.X);
            xs.Add(segmentEnd.X);
            ys.Add(segmentStart.Y);
            ys.Add(segmentEnd.Y);
            if (Math.Abs(segmentStart.X - segmentEnd.X) <= 0.0001)
            {
                xs.Add(segmentStart.X - routeSeparationPx);
                xs.Add(segmentStart.X + routeSeparationPx);
            }
            if (Math.Abs(segmentStart.Y - segmentEnd.Y) <= 0.0001)
            {
                ys.Add(segmentStart.Y - routeSeparationPx);
                ys.Add(segmentStart.Y + routeSeparationPx);
            }
        }
        void AddBorderCoordinates(double distance)
        {
            foreach (var segment in relevantBorderSegments)
            {
                if (Math.Abs(segment.Start.X - segment.End.X) <= 0.0001)
                {
                    xs.Add(segment.Start.X - distance);
                    xs.Add(segment.Start.X + distance);
                }
                if (Math.Abs(segment.Start.Y - segment.End.Y) <= 0.0001)
                {
                    ys.Add(segment.Start.Y - distance);
                    ys.Add(segment.Start.Y + distance);
                }
            }
        }
        AddBorderCoordinates(routeSeparationPx);
        // Grid lines closer than a readable segment would let the search emit a
        // micro jog between two obstacle edges; keep the endpoint stubs and coalesce
        // the rest so every turn the route can take is at least one segment long.
        List<double> Coalesce(HashSet<double> values, HashSet<double> keep, bool fillGaps)
        {
            var sorted = values.ToList();
            Js.Sort(sorted, (a, b) => a - b);
            if (!fillGaps) return sorted.Where((value, index) =>
                index == 0 || keep.Contains(value) || value - sorted[index - 1] >= minimumSegmentPx).ToList();
            // A dense chain of discarded coordinates can erase a usable corridor.
            // Compare with the last retained line while reserving readable spacing
            // around the endpoint lines that must be kept.
            var retained = new List<double>();
            foreach (var value in sorted)
            {
                if (keep.Contains(value) || ((retained.Count == 0 || value - retained[^1] >= minimumSegmentPx)
                    && keep.All(endpoint => Math.Abs(value - endpoint) >= minimumSegmentPx))) retained.Add(value);
            }
            return retained;
        }
        // Keep accepted routes stable. A failed or caller-rejected search can use
        // the denser coordinate set, within the same original node budget.
        var remainingGridNodes = maximumGridNodes;
        for (var attempt = 0; attempt < 2; attempt += 1)
        {
            // A pinned endpoint stub may land on a frame border. Nearby candidates
            // closer than the minimum segment disappear during coalescing. Recovery
            // adds a full-segment corridor without changing the attempt or node budget.
            if (attempt > 0 && routeSeparationPx < minimumSegmentPx) AddBorderCoordinates(minimumSegmentPx);
            var orderedX = Coalesce(xs, [startStub.X, endStub.X], attempt > 0);
            var orderedY = Coalesce(ys, [startStub.Y, endStub.Y], attempt > 0);
            var candidateNodeCount = orderedX.Count * orderedY.Count;
            if (metrics is not null)
            {
                metrics.CoordinateCount += orderedX.Count + orderedY.Count;
                metrics.CandidateNodeCount += candidateNodeCount;
                metrics.AttemptCount = attempt + 1;
            }
            if (candidateNodeCount > remainingGridNodes)
            {
                if (metrics is not null) metrics.Status = "node-budget-exceeded";
                return null;
            }
            remainingGridNodes -= candidateNodeCount;
            var nodes = new HashSet<Pt>();   // lookup only
            foreach (var x in orderedX)
            {
                foreach (var y in orderedY)
                {
                    var point = new Pt(x, y);
                    if (!PointBlocked(point, expanded)
                        && (options.AllowAvoidedCrossings || !PointOnAvoidedInterior(point, relevantAvoidedSegments)))
                        nodes.Add(point);
                }
            }
            if (metrics is not null) metrics.UsableNodeCount += nodes.Count;
            if (!nodes.Contains(startStub) || !nodes.Contains(endStub))
            {
                if (metrics is not null) metrics.Status = "endpoint-blocked";
                return null;
            }

            var adjacency = new Dictionary<Pt, List<(Pt Neighbor, double Weight, char Axis)>>();   // lookup only
            foreach (var node in nodes) adjacency[node] = [];
            var graphEdgeCount = 0;
            void ConnectLine(List<Pt> line, char axis)
            {
                for (var index = 0; index < line.Count - 1; index += 1)
                {
                    var left = line[index];
                    var right = line[index + 1];
                    if (SegmentBlocked(left, right, expanded)) continue;
                    if (SegmentConflictsWithAvoided(left, right, relevantAvoidedSegments, options.MinimumAvoidedOverlapPx, options.AllowAvoidedCrossings)) continue;
                    if (relevantBorderSegments.Any(segment => CollinearOverlap(left, right, segment.Start, segment.End) > 0.0001)) continue;
                    var distance = Math.Abs(right.X - left.X) + Math.Abs(right.Y - left.Y);
                    adjacency[left].Add((right, distance, axis == 'h' ? 'R' : 'D'));
                    adjacency[right].Add((left, distance, axis == 'h' ? 'L' : 'U'));
                    graphEdgeCount += 1;
                }
            }
            foreach (var y in orderedY) ConnectLine(orderedX.Select(x => new Pt(x, y)).Where(nodes.Contains).ToList(), 'h');
            foreach (var x in orderedX) ConnectLine(orderedY.Select(y => new Pt(x, y)).Where(nodes.Contains).ToList(), 'v');
            if (metrics is not null) metrics.GraphEdgeCount += graphEdgeCount;

            // The search state carries the incoming direction so a turn can cost extra
            // and a reversal is never taken: the pure shortest path hugs every obstacle
            // corner with a staircase of short jogs, while a bend-penalised one takes
            // the same corridor in a few long strokes. The first stub already leaves
            // the endpoint along its side and the last one arrives along the end side.
            static char DirectionOf(Pt v) => v.X > 0 ? 'R' : v.X < 0 ? 'L' : v.Y > 0 ? 'D' : 'U';
            static char OppositeOf(char d) => d switch { 'R' => 'L', 'L' => 'R', 'D' => 'U', _ => 'D' };
            var sourceAxis = DirectionOf(Outward[fromSide]);
            var targetAxis = OppositeOf(DirectionOf(Outward[toSide]));
            var source = startStub;
            var target = endStub;
            var sourceState = new State(source, sourceAxis);
            var distances = new Dictionary<State, double> { [sourceState] = 0 };   // lookup only
            var previous = new Dictionary<State, State>();                          // lookup only
            var queue = new MinHeap();
            queue.Push(sourceState, 0);
            var visitedNodeCount = 0;
            (State Key, double Distance)? targetState = null;
            while (queue.Entries.Count > 0)
            {
                var next = queue.Pop()!.Value;
                var current = next.Key;
                var currentDistance = next.Distance;
                if (!distances.TryGetValue(current, out var known) || currentDistance != known) continue;
                visitedNodeCount += 1;
                var (currentNode, currentAxis) = current;
                if (currentNode == target)
                {
                    // The end stub is outside the graph: never reverse back onto it.
                    if (currentAxis == OppositeOf(targetAxis)) continue;
                    // Arriving on the wrong axis costs one final turn onto the end stub.
                    var arrival = currentDistance + (currentAxis == targetAxis ? 0 : bendPenaltyPx);
                    if (targetState is null || arrival < targetState.Value.Distance) targetState = (current, arrival);
                    if (currentAxis == targetAxis || bendPenaltyPx == 0) break;
                    continue;
                }
                if (targetState is not null && currentDistance >= targetState.Value.Distance) break;
                foreach (var (neighbor, weight, axis) in adjacency.TryGetValue(currentNode, out var edges) ? edges : [])
                {
                    if (axis == OppositeOf(currentAxis)) continue;
                    var candidate = currentDistance + weight + (axis == currentAxis ? 0 : bendPenaltyPx);
                    var neighborState = new State(neighbor, axis);
                    if (candidate >= (distances.TryGetValue(neighborState, out var best) ? best : double.PositiveInfinity)) continue;
                    distances[neighborState] = candidate;
                    previous[neighborState] = current;
                    queue.Push(neighborState, candidate);
                }
            }
            if (metrics is not null) metrics.VisitedNodeCount += visitedNodeCount;
            if (targetState is null)
            {
                if (metrics is not null) metrics.Status = "no-route";
                continue;
            }
            var reversed = new List<Pt>();
            var key = targetState.Value.Key;
            while (true)
            {
                reversed.Add(key.Node);
                if (key == sourceState) break;
                if (!previous.TryGetValue(key, out key)) break;
            }
            if (reversed[^1] != source)
            {
                if (metrics is not null) metrics.Status = "broken-predecessor-chain";
                return null;
            }
            reversed.Reverse();
            var shortestPoints = Geometry.NormalizeRoutePoints([start, .. reversed, end]);
            if (options.Accept is not null && !options.Accept(shortestPoints))
            {
                if (metrics is not null) metrics.Status = "rejected";
                continue;
            }
            if (metrics is not null) metrics.Status = "routed";
            return new(shortestPoints, OrthogonalLength(shortestPoints), expanded.Count);
        }
        return null;
    }

    static double CollinearOverlap(Pt leftStart, Pt leftEnd, Pt rightStart, Pt rightEnd)
    {
        if (leftStart.X == leftEnd.X && rightStart.X == rightEnd.X
            && leftStart.X == rightStart.X)
        {
            return Math.Max(0, Math.Min(Math.Max(leftStart.Y, leftEnd.Y), Math.Max(rightStart.Y, rightEnd.Y))
                - Math.Max(Math.Min(leftStart.Y, leftEnd.Y), Math.Min(rightStart.Y, rightEnd.Y)));
        }
        if (leftStart.Y == leftEnd.Y && rightStart.Y == rightEnd.Y
            && leftStart.Y == rightStart.Y)
        {
            return Math.Max(0, Math.Min(Math.Max(leftStart.X, leftEnd.X), Math.Max(rightStart.X, rightEnd.X))
                - Math.Max(Math.Min(leftStart.X, leftEnd.X), Math.Min(rightStart.X, rightEnd.X)));
        }
        return 0;
    }

    static bool SegmentOutsideContent(Pt start, Pt end, Bounds? contentBounds)
    {
        if (contentBounds is null) return false;
        var midpoint = new Pt((start.X + end.X) / 2, (start.Y + end.Y) / 2);
        return midpoint.X < contentBounds.Left || midpoint.X > contentBounds.Right
            || midpoint.Y < contentBounds.Top || midpoint.Y > contentBounds.Bottom;
    }

    static bool SharesOuterCorridor(JsonObject relation, JsonNode? relations, Func<JsonObject, List<Pt>?> pathFor, List<Pt> points, Bounds? contentBounds, double minimumOverlap)
    {
        foreach (var node in Geometry.AsArray(relations))
        {
            if (node is not JsonObject other || ReferenceEquals(other, relation)) continue;
            var (from, to) = (relation.Str("from"), relation.Str("to"));
            var related = from == other.Str("from") || from == other.Str("to")
                || to == other.Str("from") || to == other.Str("to");
            if (!related) continue;
            var otherPoints = Geometry.NormalizeRoutePoints(pathFor(other) ?? []);
            for (var left = 0; left < points.Count - 1; left += 1)
            {
                if (!SegmentOutsideContent(points[left], points[left + 1], contentBounds)) continue;
                for (var right = 0; right < otherPoints.Count - 1; right += 1)
                {
                    if (CollinearOverlap(points[left], points[left + 1], otherPoints[right], otherPoints[right + 1]) >= minimumOverlap) return true;
                }
            }
        }
        return false;
    }

    static JsonObject RelationshipSubject(string diagramType, string relationCollection, int relationIndex, JsonObject relation)
    {
        var subject = new JsonObject { ["diagramType"] = diagramType, ["collection"] = relationCollection, ["index"] = relationIndex };
        if (!string.IsNullOrEmpty(relation.Str("id"))) subject["id"] = relation.Str("id");
        if (relation.Str("from") is { } from) subject["from"] = from;
        if (relation.Str("to") is { } to) subject["to"] = to;
        return subject;
    }

    /// <summary>
    /// Reject conspicuous authored detours without penalizing routes whose length is
    /// explained by opaque-node avoidance or a related shared outer corridor.
    /// </summary>
    public static List<string> CleanRouteDetourProblems(DiagnosticSink sink, JsonNode? relations, IEnumerable<Rect> obstacles, IEnumerable<Rect>? contentRects,
        JsSet<string>? endpointIds, Func<JsonObject, List<Pt>?> pathFor, Func<JsonObject, string?>? fromSideFor, Func<JsonObject, string?>? toSideFor,
        string diagramType, string relationCollection, string? profile, DetourThresholds? thresholds = null)
    {
        if (profile != "showcase") return [];
        var policy = thresholds ?? new DetourThresholds();
        var obstacleList = obstacles.ToList();
        var contentBounds = BoundsForRects(contentRects ?? obstacleList);
        var problems = new List<string>();
        var relationIndex = -1;
        foreach (var node in Geometry.AsArray(relations))
        {
            relationIndex++;
            if (node is not JsonObject relation || endpointIds is null
                || relation.Str("from") is not { } from || !endpointIds.Has(from) || relation.Str("to") is not { } to || !endpointIds.Has(to)) continue;
            if (relation.Arr("via") is not { Count: > 0 }) continue;
            var points = Geometry.NormalizeRoutePoints(pathFor(relation) ?? []);
            if (points.Count < 3 || !points.All(point => Geometry.IsFinitePoint(point.X, point.Y))) continue;
            var actualLength = OrthogonalLength(points);
            if (actualLength is not { } actual || !double.IsFinite(actual)) continue;
            var start = points[0];
            var end = points[^1];
            var manhattan = Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y);
            if (actual < manhattan * policy.MinimumDetourRatio
                || actual - manhattan < policy.MinimumExcessLengthPx) continue;
            var routeBounds = BoundsForPoints(points);
            var excursion = OutsideExcursion(routeBounds, contentBounds);
            var emptyClearance = EmptyControlPointClearance(points, obstacleList);
            if (Math.Max(excursion?.Maximum ?? 0, emptyClearance?.Maximum ?? 0) < policy.MinimumEmptyExcursionPx) continue;
            if (SharesOuterCorridor(relation, relations, pathFor, points, contentBounds, policy.SharedCorridorMinimumPx)) continue;

            var fromSide = fromSideFor?.Invoke(relation) is { Length: > 0 } f ? f : InferredSide(points, "source");
            var toSide = toSideFor?.Invoke(relation) is { Length: > 0 } t ? t : InferredSide(points, "target");
            var shortest = ShortestOrthogonalGridRoute(start, end, points, obstacleList, fromSide, toSide,
                new GridRouteOptions(policy.Clearance, policy.MaximumObstacleCount));
            if (shortest is null || shortest.Length is not { } shortestLength || !double.IsFinite(shortestLength) || shortestLength <= 0) continue;
            var detourRatio = actual / shortestLength;
            var excessLength = actual - shortestLength;
            if (detourRatio < policy.MinimumDetourRatio || excessLength < policy.MinimumExcessLengthPx) continue;

            var relationId = !string.IsNullOrEmpty(relation.Str("id")) ? $" id \"{relation.Str("id")}\"" : "";
            var message = $"[composition/excessive-route-detour] {diagramType} {relationCollection}[{Js.Num(relationIndex)}]{relationId} \"{from}\" -> \"{to}\" travels {Js.Num(Js.Round(actual))}px, {Js.Num(Rounded(detourRatio))}x the {Js.Num(Js.Round(shortestLength))}px shortest obstacle-clearing orthogonal route, and reaches {Js.Num(Js.Round(excursion!.Maximum))}px beyond the content bounds — remove the distant via corridor or move it close to the connected content.";
            const string supportedFix = "remove the distant via points and retry automatic routing, or keep the endpoint sides and move the via corridor near the connected nodes while preserving labels and direction";
            sink.Record(new Diagnostic("composition/excessive-route-detour", Severity.Error, message,
                RelationshipSubject(diagramType, relationCollection, relationIndex, relation),
                new JsonObject
                {
                    ["points"] = new JsonArray([.. points.Select(p => (JsonNode?)new JsonArray(p.X, p.Y))]),
                    ["actualLengthPx"] = Rounded(actual),
                    ["shortestLegalPoints"] = new JsonArray([.. shortest.Points.Select(p => (JsonNode?)new JsonArray(p.X, p.Y))]),
                    ["shortestLegalLengthPx"] = Rounded(shortestLength),
                    ["detourRatio"] = Rounded(detourRatio),
                    ["excessLengthPx"] = Rounded(excessLength),
                    ["routeBounds"] = routeBounds?.ToJson(),
                    ["contentBounds"] = contentBounds?.ToJson(),
                    ["emptyExcursionPx"] = excursion?.ToJson(),
                    ["emptyControlPointClearancePx"] = emptyClearance is { } ec ? new JsonObject { ["maximum"] = ec.Maximum, ["point"] = new JsonArray(ec.Point.X, ec.Point.Y) } : null,
                    ["obstacleCount"] = shortest.ObstacleCount,
                    ["thresholds"] = new JsonObject
                    {
                        ["minimumDetourRatio"] = policy.MinimumDetourRatio,
                        ["minimumExcessLengthPx"] = policy.MinimumExcessLengthPx,
                        ["minimumEmptyExcursionPx"] = policy.MinimumEmptyExcursionPx,
                    },
                },
                [supportedFix], null));
            problems.Add(message);
        }
        return problems;
    }
}
