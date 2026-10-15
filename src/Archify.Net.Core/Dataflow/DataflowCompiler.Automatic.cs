using System.Text.Json.Nodes;

namespace Archify.Net;

// dataflow-compiler.mjs automatic-showcase clusters: B port rebalance, C auto node height, H route repair, I port reorder,
// M label/corridor co-repair, N wrap, O joint repair and the `automaticShowcase` branches of F/J/K. Every caller in
// DataflowCompiler.cs sits behind `automaticShowcase`, which no authored-viewBox input reaches.
internal sealed partial class DataflowCompiler
{
    sealed partial class Build
    {
        static readonly string[] ReorderPinKeys = ["fromSide", "toSide", "labelAt", "labelDx", "labelDy", "labelSegment"];

        // `JSON.stringify(points)` as a key or an equality test (:903, :1213): finite numbers print as their Number strings.
        static string PointsKey(IReadOnlyList<Pt> points) => string.Join(";", points.Select(p => $"{N(p.X)},{N(p.Y)}"));
        static double Ax(Pt p, int axis) => axis == 0 ? p.X : p.Y;
        static Pt With(Pt p, int axis, double value) => axis == 0 ? p with { X = value } : p with { Y = value };
        // `{ ...rect, ly: rect.ly - 1 }` / `{ ...rect, ly: rect.ly + 1 }`: new objects, same relation.
        static LabelRect Lowered(LabelRect rect) { var copy = rect.Clone(); copy.Ly = (rect.Ly ?? double.NaN) - 1; return copy; }
        static LabelRect Raised(LabelRect rect) { var copy = rect.Clone(); copy.Ly = (rect.Ly ?? double.NaN) + 1; return copy; }
        static Pt AnchorOf(LabelRect rect) => new(rect.Lx ?? double.NaN, rect.Ly ?? double.NaN);
        int FlowIndex(JsonObject flow) => flows.FindIndex(other => ReferenceEquals(other, flow));   // dataflow.flows.indexOf(flow)
        double PlacementBottom => viewBox[1] - Layout.StageBottomPad;
        List<Box> Components() => [.. nodes.Values];
        static RoutedPath Routed(List<Pt> points) => new(Geometry.PolylinePath(points), points);
        static bool SegmentIntersectsNode(IReadOnlyList<Pt> points, Box node) => SegmentsOf(points).Any(segment => Geometry.SegmentIntersectsRect(segment, node, 2));

        // :928-936, used by I and M.
        static int IntersectionCount(IReadOnlyList<Pt> points, IReadOnlyList<Pt> otherPoints) => SegmentsOf(points).Sum(a =>
            SegmentsOf(otherPoints).Count(b => Geometry.ProperSegmentIntersection(a.Start, a.End, b.Start, b.End) is not null));
        static bool FoldsBack(IReadOnlyList<Pt> points)
        {
            var segments = SegmentsOf(points);
            return segments.Where((segment, index) => segments.Skip(index + 2).Any(other => Geometry.SegmentIntersectsRect(segment, new Rect(
                Math.Min(other.Start.X, other.End.X), Math.Min(other.Start.Y, other.End.Y),
                Math.Abs(other.End.X - other.Start.X), Math.Abs(other.End.Y - other.Start.Y))))).Any();
        }

        // :1322-1329 and :1374-1378: automatic/automatic proper crossings get halos.
        void CollectCrossovers()
        {
            for (var index = 0; index < routable.Count; index++)
            {
                var left = routable[index];
                foreach (var right in routable.Skip(index + 1))
                {
                    if (!Related(left, right) && SupportsAutomaticCrossover(left) && SupportsAutomaticCrossover(right) && Crosses(left, right))
                    {
                        resolvedCrossoverFlows.Add(left);
                        resolvedCrossoverFlows.Add(right);
                    }
                }
            }
        }

        // ---------------------------------------------------------------- B (:157-199) automaticSourceSides; runs on the retry only
        void RebalancePorts()
        {
            foreach (var node in nodes.Values)
            {
                var authored = authoredNodes.FirstOrDefault(item => item.Str("id") == node.Id) ?? node.Source;   // dataflow.nodes.find(…)
                if (authored.ContainsKey("height") || authored.ContainsKey("yOffset")) continue;
                var incident = flows.Where(flow => flow.Str("from") == node.Id || flow.Str("to") == node.Id).ToList();
                if (incident.Any(HasPinnedFlowGeometry)) continue;
                var sides = new Dictionary<string, List<PortItem>>(StringComparer.Ordinal)   // lookup only
                {
                    ["left"] = [], ["right"] = [], ["top"] = [], ["bottom"] = [],
                };
                foreach (var flow in incident)
                {
                    var from = NodeOf(flow.Str("from"));
                    var to = NodeOf(flow.Str("to"));
                    if (from is null || to is null) continue;
                    var incoming = flow.Str("to") == node.Id;
                    var side = incoming ? Geometry.LegacyDefaultToSide(from, to)
                        : automaticSourceSides.Get(flow) is { Length: > 0 } chosen ? chosen : Geometry.LegacyDefaultFromSide(from, to);
                    sides[side].Add(new(flow, incoming, FlowStrokeWidth(flow)));
                }
                var choices = new List<(JsonObject Flow, string Alternate, double Distance)>();
                foreach (var side in new[] { "left", "right" })
                {
                    var items = sides[side];
                    if (!items.Any(item => item.Incoming) || PortSpan(items) <= node.Height) continue;
                    foreach (var item in items.Where(item => !item.Incoming))
                    {
                        var target = NodeOf(item.Flow!.Str("to"))!;
                        var dy = target.Cy - node.Cy;
                        if (Math.Abs(dy) < (node.Height + target.Height) / 2 + 16) continue;
                        var alternate = dy > 0 ? "bottom" : "top";
                        if (PortSpan([.. sides[alternate], item]) > node.Width) continue;
                        var toSide = Geometry.LegacyDefaultToSide(node, target);
                        if (toSide != "left" && toSide != "right") continue;
                        var start = Geometry.Anchor(node, alternate);
                        var end = Geometry.Anchor(target, toSide);
                        var points = Geometry.NormalizeRoutePoints([start, new(start.X, end.Y), end]);
                        if (!Geometry.RouteHonorsEndpointSides(points, alternate, toSide)) continue;
                        if (nodes.Values.Any(other => SegmentsOf(points).Where((segment, index) =>
                            Geometry.SegmentIntersectsRect(segment, other, 2)
                            && !((other.Id == node.Id && index == 0) || (other.Id == target.Id && index == points.Count - 2))).Any())) continue;
                        choices.Add((item.Flow!, alternate, Math.Abs(start.X - end.X) + Math.Abs(start.Y - end.Y)));
                    }
                }
                Js.Sort(choices, (a, b) => a.Distance - b.Distance);
                if (choices.Count > 0) automaticSourceSides.Set(choices[0].Flow, choices[0].Alternate);
            }
        }

        // ---------------------------------------------------------------- C (:206-240) grows crowded nodes (autoHeightInset)
        void GrowCrowdedNodes()
        {
            // `{ ...node }` snapshots: neighbours are measured against the original bounds (and upstream's stored `cy`).
            var originalBoxes = nodes.Values.Select(node => (node.Id, node.X, node.Width, node.Cy, node.Height)).ToList();
            var rowBudget = Js.MinOf(Layout.RowYs.Skip(1).Select((y, index) => y - Layout.RowYs[index]), double.PositiveInfinity) - 10;
            foreach (var authored in authoredNodes)
            {
                if (authored.ContainsKey("height") || authored.ContainsKey("yOffset")) continue;
                var node = NodeOf(authored.Str("id"))!;
                var incident = flows.Where(flow => flow.Str("from") == node.Id || flow.Str("to") == node.Id).ToList();
                if (incident.Any(HasPinnedFlowGeometry)) continue;
                var left = new List<PortItem>();
                var right = new List<PortItem>();
                List<PortItem>? SideItems(string side) => side == "left" ? left : side == "right" ? right : null;   // sides[side]?.push
                foreach (var flow in incident)
                {
                    if (!Has(flow.Str("from")) || !Has(flow.Str("to"))) continue;
                    var (fromSide, toSide) = FlowSides(flow);
                    if (flow.Str("from") == node.Id) SideItems(fromSide)?.Add(new(null, false, FlowStrokeWidth(flow)));
                    if (flow.Str("to") == node.Id) SideItems(toSide)?.Add(new(null, true, FlowStrokeWidth(flow)));
                }
                // Object.values(sides): left, then right.
                var desired = Js.MaxOf(new[] { left, right }.Where(items => items.Any(item => item.Incoming)).Select(PortSpan), node.Height);
                var neighbours = originalBoxes.Where(other => other.Id != node.Id
                    && other.X < node.X + node.Width + 10 && other.X + other.Width + 10 > node.X).ToList();
                var height = Js.MinOf(neighbours.Select(other =>
                {
                    var distance = Math.Abs(other.Cy - node.Cy);
                    // Split spare space with another growing box, and also respect a
                    // taller authored neighbour. Use original bounds for stable ordering.
                    return Math.Min(distance - 10, 2 * distance - other.Height - 20);
                }), Math.Min(Math.Min(desired, rowBudget), 2 * (node.Cy - (Layout.StageY + Layout.StageH + 22))));
                if (height > node.Height)
                {
                    var inset = (height - node.Height) / 2;
                    autoHeightInset.Set(node.Id, inset);
                    node.Y -= inset;
                    node.Height = height;
                }
            }
        }

        // ---------------------------------------------------------------- F showcase branches
        // :593-603 automaticPortRhythmBridge inside routeVia; null falls through to the preset corridors.
        List<Pt>? AutomaticPortBridgeVia(Box from, Box to, Pt start, Pt end, string fromSide, string toSide)
        {
            var bridge = Geometry.AutomaticPortRhythmBridge(start, end, fromSide, toSide,
                accept: points => !nodes.Values.Any(node => node.Id != from.Id && node.Id != to.Id && SegmentIntersectsNode(points, node)));
            return bridge?.GetRange(1, bridge.Count - 2);   // bridge.slice(1, -1)
        }

        // :624-628 vertical/vertical automatic route.
        static List<Pt> AutomaticVerticalVia(Pt start, Pt end)
        {
            if (Math.Abs(start.X - end.X) < 0.0001) return [];
            var midY = (start.Y + end.Y) / 2;
            return [new(start.X, midY), new(end.X, midY)];
        }

        // :673-698 alignAutomaticFacingPorts inside pathFor.
        (Pt Start, Pt End) AlignAutomaticPorts(JsonObject flow, Box from, Box to, Pt start, Pt end, string fromSide, string toSide, SpreadPorts? ports) =>
            Geometry.AlignAutomaticFacingPorts(flow, from, to, start, end, fromSide, toSide, ports, accept: points =>
            {
                if (nodes.Values.Any(node => node.Id != from.Id && node.Id != to.Id
                    && Geometry.SegmentIntersectsRect(new(points[0], points[1]), node, 2))) return false;
                // A pinned route still occupies its endpoint. Check both ends: on a
                // short crowded side, keeping the bridge also preserves space for
                // labels between neighbouring branches.
                foreach (var (rect, side, point) in new[] { (from, fromSide, points[0]), (to, toSide, points[1]) })
                {
                    foreach (var other in flows)
                    {
                        if (ReferenceEquals(other, flow) || !Has(other.Str("from")) || !Has(other.Str("to"))) continue;
                        foreach (var field in new[] { "from", "to" })
                        {
                            var otherSides = FlowSides(other);
                            if (other.Str(field) != rect.Id || (field == "from" ? otherSides.FromSide : otherSides.ToSide) != side) continue;
                            var spread = automaticPorts.Get(other);
                            var occupied = (field == "from" ? spread?.From : spread?.To) ?? Geometry.Anchor(rect, side);
                            if (Js.Hypot(point.X - occupied.X, point.Y - occupied.Y) < 18 - 0.0001) return false;
                        }
                    }
                }
                return true;
            });

        // ---------------------------------------------------------------- H (:765-926) route repair
        sealed record Detour(JsonObject Flow, RoutedPath Routed, double Cost);

        void RepairAutomaticRoutes()
        {
            // The stage bottoms are fitted below the final routes and labels later.
            // Reserve the fixed tops/sides now, extending sides past the search area.
            var routingBottom = Js.MaxOf([.. nodes.Values.Select(node => node.Y + node.Height), .. routable.SelectMany(flow => PathFor(flow).Points.Select(point => point.Y))],
                viewBox[1]) + DETOUR_NODE_GAP + ENDPOINT_STUB;
            var routingFrames = stages.Select((stage, index) => StageFrame(stage, index) with { Height = routingBottom - Layout.StageY }).ToList();
            var stageBorders = routingFrames.SelectMany(frame => Geometry.FrameBorderSegments(frame).Where(border => border.Side != "bottom")).ToList();

            // Check nearby simple corridors before accepting a grid detour. Coordinate
            // thinning can discard an open two-bend corridor beside an occupied one.
            // Bound these candidates; the grid still handles routes around obstacles.
            List<List<Pt>> CompactCorridors(JsonObject flow)
            {
                var (fromSide, toSide) = FlowSides(flow);
                var axis = (fromSide, toSide) switch
                {
                    ("right", "left") or ("left", "right") => 0,
                    ("bottom", "top") or ("top", "bottom") => 1,
                    _ => -1,
                };
                if (axis < 0) return [];
                var (start, end) = (PathFor(flow).Points[0], PathFor(flow).Points[^1]);
                var low = Math.Min(Ax(start, axis), Ax(end, axis)) + ENDPOINT_STUB;
                var high = Math.Max(Ax(start, axis), Ax(end, axis)) - ENDPOINT_STUB;
                if (low > high) return [];
                var middle = (low + high) / 2;
                var coordinates = new JsSet<double>();
                coordinates.Add(low);
                coordinates.Add(middle);
                coordinates.Add(high);
                var others = routable.Where(other => !ReferenceEquals(other, flow)).ToList();
                foreach (var other in others)
                {
                    foreach (var segment in SegmentsOf(PathFor(other).Points))
                    {
                        if (Ax(segment.Start, axis) == Ax(segment.End, axis))
                        {
                            coordinates.Add(Ax(segment.Start, axis) - 8);
                            coordinates.Add(Ax(segment.Start, axis) + 8);
                        }
                    }
                }
                var values = coordinates.Where(value => value >= low && value <= high).ToList();
                Js.Sort(values, (a, b) => Math.Abs(a - middle) - Math.Abs(b - middle));
                return values.Take(16)
                    .Select(value => Geometry.NormalizeRoutePoints([start, With(start, axis, value), With(end, axis, value), end]))
                    .Where(points => Geometry.RouteHonorsEndpointSides(points, fromSide, toSide)
                        && !IntrudesNode(flow, points)
                        && Geometry.CollectRouteRhythmIssues([new RoutedRelation(points)]).Count == 0
                        && !Geometry.CollectBorderRuns([new RoutedRelation(points)], routingFrames).Any(hit => hit.Side != "bottom")
                        && CrossingFlows(flow, points).All(SupportsAutomaticCrossover)
                        && !others.Any(other => Geometry.CollectAmbiguousCorridors(
                            [new RoutedRelation(points, flow), new RoutedRelation(PathFor(other).Points, other)],
                            includeSharedEndpoints: (_, _) => true).Count > 0))
                    .ToList();
            }
            List<Pt>? GridDetour(JsonObject flow, bool allowAvoidedCrossings = false, JsSet<JsonObject>? deferred = null)
            {
                var points = PathFor(flow).Points;
                var (fromSide, toSide) = FlowSides(flow);
                var others = Unrelated(flow);
                // Keep a detour a readable gap away from the nodes it passes; only its
                // own endpoints are held to the grid's 2-unit clearance.
                var obstacles = nodes.Values.Select(node => node.Id == flow.Str("from") || node.Id == flow.Str("to") ? (Rect)node : new Rect(
                    node.X - DETOUR_NODE_GAP, node.Y - DETOUR_NODE_GAP, node.Width + DETOUR_NODE_GAP * 2, node.Height + DETOUR_NODE_GAP * 2)).ToList();
                var options = new GridRouteOptions(Clearance: 2, MaximumObstacleCount: 80, EndpointStubPx: ENDPOINT_STUB, MaximumGridNodes: 4096,
                    AvoidedSegments: others.SelectMany(other => deferred is not null && deferred.Has(other) ? EndpointStubs(other) : SegmentsOf(PathFor(other).Points)).ToList(),
                    AllowAvoidedCrossings: allowAvoidedCrossings, MinimumAvoidedOverlapPx: 8, RouteSeparationPx: 8, MinimumSegmentPx: 16, BendPenaltyPx: 48);
                List<Pt> endpoints = [points[0], points[^1]];
                var detour = RouteQuality.ShortestOrthogonalGridRoute(points[0], points[^1], endpoints, obstacles, fromSide, toSide, options);
                // Frame constraints add coordinates to the grid. Keep an already-clear
                // route stable; only retry if this route actually borrows a frame side.
                if (detour is not null && Geometry.CollectBorderRuns([new RoutedRelation(detour.Points)], routingFrames).Any(hit => hit.Side != "bottom"))
                    detour = RouteQuality.ShortestOrthogonalGridRoute(points[0], points[^1], endpoints, obstacles, fromSide, toSide, options with { BorderSegments = stageBorders });
                // The grid preserves both endpoint coordinates, even when their axes
                // are closer than its coalescing distance. Check the resulting route
                // before a cheaper micro jog can replace a readable port bridge.
                var normalized = detour is null ? null : Geometry.NormalizeRoutePoints(detour.Points);
                return normalized is not null && Geometry.CollectRouteRhythmIssues([new RoutedRelation(normalized)]).Count == 0 ? normalized : null;
            }
            Detour? DetourFor(JsonObject flow)
            {
                var points = PathFor(flow).Points;
                var normalized = GridDetour(flow);
                if (normalized is null) return null;
                // Reuse Architecture's finite reading cost: 48px per bend, 160px per
                // resolved crossing. A small clear detour still wins over a crossover.
                var currentCost = ReadingCost(flow, points);
                var detourCost = Length(normalized) + Math.Max(0, normalized.Count - 2) * 48;
                // A halo resolves an edge/edge crossing, never an edge through a node.
                // Keep the clear detour even when the obstructed preset costs less.
                var canResolve = SupportsAutomaticCrossover(flow) && CrossingFlows(flow, points).All(SupportsAutomaticCrossover)
                    && !SharesCorridor(flow, points) && !IntrudesNode(flow, points);
                if (canResolve && detourCost >= currentCost) return null;
                return new(flow, Routed(normalized), detourCost - currentCost);
            }

            // A skipped stage/row or shared corridor can obstruct a preset without a
            // proper crossing. Repair those routes first; otherwise the crossing pass either
            // misses them or treats every neighbouring edge as an impenetrable wall.
            // Both bounded searches keep node clearance and separate corridors. Only
            // automatic/automatic crosses can use the shorter, halo-resolved option.
            // Repairing a later preset can free a corridor for an earlier one. An
            // already-obstructed automatic preset is not a settled corridor: two such
            // presets can cover each other's endpoint stubs. Defer those presets while
            // repairing one route, but reserve their endpoint stubs so they can still
            // leave and enter their nodes. Plan the others around that repaired route.
            // Supplied routes and routes already clear of nodes remain obstacles.
            // Node intrusions only decrease; stop as soon as no route improves. Final
            // validation still checks every route, including any unrepaired preset.
            for (var pass = 0; pass < routable.Count; pass += 1)
            {
                var repaired = false;
                foreach (var flow in routable)
                {
                    if (!SupportsAutomaticCrossover(flow)) continue;
                    var current = PathFor(flow).Points;
                    if (!IntrudesNode(flow, current) && !SharesCorridor(flow, current)) continue;
                    var (fromSide, toSide) = FlowSides(flow);
                    List<List<Pt>> CandidatesFor(JsSet<JsonObject>? deferred) => new[] { GridDetour(flow, false, deferred), GridDetour(flow, true, deferred) }
                        .Where(points => points is not null
                            && Geometry.RouteHonorsEndpointSides(points, fromSide, toSide) && !IntrudesNode(flow, points)
                            && !SharesCorridor(flow, points, deferred) && CrossingFlows(flow, points).All(SupportsAutomaticCrossover))
                        .Select(points => points!).ToList();
                    var candidates = CandidatesFor(null);
                    if (candidates.Count == 0)
                    {
                        var deferred = new JsSet<JsonObject>();
                        foreach (var other in Unrelated(flow).Where(other => SupportsAutomaticCrossover(other) && IntrudesNode(other, PathFor(other).Points))) deferred.Add(other);
                        if (deferred.Count > 0) candidates = CandidatesFor(deferred);
                    }
                    candidates.AddRange(CompactCorridors(flow));
                    Js.Sort(candidates, (left, right) => ReadingCost(flow, left) - ReadingCost(flow, right));
                    if (candidates.Count > 0 && PointsKey(candidates[0]) != PointsKey(current))
                    {
                        pathCache.Set(flow, Routed(candidates[0]));
                        repaired = true;
                    }
                }
                if (!repaired) break;
            }
            // Resolve the most useful clear detour first. Remaining automatic crosses
            // get explicit halos; supplied geometry retains its crossing diagnostic.
            for (var pass = 0; pass < routable.Count; pass += 1)
            {
                var crossing = routable.Where(flow => SupportsAutomaticCrossover(flow) && Unrelated(flow).Any(other => Crosses(flow, other))).ToList();
                var options = crossing.Select(DetourFor).Where(option => option is not null).Select(option => option!).ToList();
                Js.Sort(options, (left, right) => left.Cost - right.Cost);
                var before = routable.Count(flow => Unrelated(flow).Any(other => Crosses(flow, other)));
                Detour? chosen = null;
                foreach (var option in options)
                {
                    var previous = pathCache.Get(option.Flow)!;
                    pathCache.Set(option.Flow, option.Routed);
                    var after = routable.Count(other => Unrelated(other).Any(next => Crosses(other, next)));
                    if (after < before) { chosen = option; break; }
                    pathCache.Set(option.Flow, previous);
                }
                if (chosen is null) break;
            }
        }

        // ---------------------------------------------------------------- I (:941-1006) slot reorder by the first turn of the settled route
        void ReorderAutomaticPorts()
        {
            var portFrames = stages.Select(StageFrame).ToList();
            var groups = new JsMap<string, List<(JsonObject Flow, string Field, string Side)>>();
            foreach (var flow in routable)
            {
                foreach (var field in new[] { "from", "to" })
                {
                    var sides = FlowSides(flow);
                    var side = field == "from" ? sides.FromSide : sides.ToSide;
                    var key = $"{flow.Str(field)}\0{side}";
                    if (!groups.Has(key)) groups.Set(key, []);
                    groups.Get(key)!.Add((flow, field, side));
                }
            }
            bool Pinned(JsonObject flow) => !SupportsAutomaticCrossover(flow) || ReorderPinKeys.Any(flow.ContainsKey);
            static Pt? PortOf(SpreadPorts? ports, string field) => field == "from" ? ports?.From : ports?.To;
            var probes = 0;
            foreach (var group in groups.Values)
            {
                if (probes >= 16) break;
                if (group.Count < 2 || group.Count > 8
                    || group.Any(item => Pinned(item.Flow) || PortOf(automaticPorts.Get(item.Flow), item.Field) is null)) continue;
                var axis = group[0].Side is "left" or "right" ? 1 : 0;
                var currentRoutes = group.Select(item => (item.Flow, item.Field, Current: Geometry.NormalizeRoutePoints(PathFor(item.Flow).Points))).ToList();
                if (currentRoutes.Any(item => item.Current.Count < 2
                    || item.Current.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))) continue;
                var items = currentRoutes.Select(item =>
                {
                    var outward = item.Field == "from" ? item.Current : Enumerable.Reverse(item.Current).ToList();
                    var coordinate = Ax(outward[0], axis);
                    var turn = outward.Where(point => Math.Abs(Ax(point, axis) - coordinate) > 0.0001).Select(point => (double?)Ax(point, axis)).FirstOrDefault() ?? coordinate;
                    return (item.Flow, item.Field, item.Current, Outward: outward, Coordinate: coordinate, Turn: turn);
                }).ToList();
                var slots = items.Select(item => item.Coordinate).ToList();
                Js.Sort(slots, (a, b) => a - b);
                var ordered = items.ToList();
                Js.Sort(ordered, (a, b) => { var turn = a.Turn - b.Turn; return turn != 0 ? turn : a.Coordinate - b.Coordinate; });   // `a.turn - b.turn || …`
                if (ordered.Select((item, index) => item.Coordinate == slots[index]).All(same => same)) continue;
                probes++;
                var proposal = new JsMap<JsonObject, List<Pt>>();
                var straightened = false;
                for (var index = 0; index < ordered.Count; index++)
                {
                    var item = ordered[index];
                    var outward = item.Outward.ToList();
                    outward[0] = With(outward[0], axis, slots[index]);
                    if (outward.Count > 2) outward[1] = With(outward[1], axis, slots[index]);
                    var points = item.Field == "from" ? outward : Enumerable.Reverse(outward).ToList();
                    var (fromSide, toSide) = FlowSides(item.Flow);
                    List<Pt> direct = [points[0], points[^1]];
                    if (Geometry.RouteHonorsEndpointSides(direct, fromSide, toSide)
                        && (direct[0].X == direct[1].X || direct[0].Y == direct[1].Y)) points = direct;
                    points = Geometry.NormalizeRoutePoints(points);
                    if (points.Count == 2 && item.Current.Count > 2) straightened = true;
                    proposal.Set(item.Flow, points);
                }
                if (!straightened || items.Any(item =>
                {
                    var (flow, current) = (item.Flow, item.Current);
                    var points = proposal.Get(flow)!;
                    var (fromSide, toSide) = FlowSides(flow);
                    return points.Count > current.Count || Length(points) > Length(current) + 0.0001 || FoldsBack(points)
                        || !Geometry.RouteHonorsEndpointSides(points, fromSide, toSide) || IntrudesNode(flow, points)
                        || Geometry.CollectRouteRhythmIssues([new RoutedRelation(points)]).Count > 0
                        || Geometry.CollectBorderRuns([new RoutedRelation(points)], portFrames).Count > 0
                        || points.Any(p => p.X < 0 || p.Y < 0 || p.X > viewBox[0] || p.Y > PlacementBottom)
                        || routable.Any(other => !ReferenceEquals(other, flow) && (
                            IntersectionCount(points, proposal.Get(other) ?? PathFor(other).Points)
                                > IntersectionCount(current, PathFor(other).Points)
                            || Geometry.CollectAmbiguousCorridors(
                                [new RoutedRelation(points, flow), new RoutedRelation(proposal.Get(other) ?? PathFor(other).Points, other)],
                                includeSharedEndpoints: (_, _) => true).Count > 0));
                })) continue;
                foreach (var item in items)
                {
                    var points = proposal.Get(item.Flow)!;
                    pathCache.Set(item.Flow, Routed(points));
                    var ports = automaticPorts.Get(item.Flow)!;
                    if (item.Field == "from") ports.From = points[0];
                    else ports.To = points[^1];
                }
            }
        }

        // ---------------------------------------------------------------- J/K showcase branches
        // :1012-1022 the corridor anchor for an unclassified vertical two-point flow; null falls through to legacyLabelPoint.
        Pt? AutomaticLabelAnchor(JsonObject flow, List<Pt> points)
        {
            var anchor = new Pt(points[0].X, (points[0].Y + points[1].Y) / 2 - 10);
            var (width, height) = LabelCapacity.FlowLabelSize(flow);
            var rect = new Rect(anchor.X - width / 2, anchor.Y - 11, width, height);
            // Sibling flows share a fixed source-side corridor. Keep the old search
            // anchor if a sibling occupies the midpoint; interior routes from other
            // nodes can still move during the later label/corridor repair.
            return routable.All(other => ReferenceEquals(other, flow) || other.Str("from") != flow.Str("from") || SegmentsOf(PathFor(other).Points)
                .All(segment => (Geometry.SegmentRectClearanceWithin(segment, rect, 4) ?? 0) + 0.0001 >= 4)) ? anchor : null;
        }

        // :1074-1085 placeAutomaticLabels over the initial rects.
        List<LabelRect> PlaceInitialLabels(List<LabelRect> labels) =>
            AutomaticLabels.PlaceAutomaticLabels(labels, RoutesForPlacement(), Components(), Titles(), viewBox,
                placementBottom: PlacementBottom, maxOwnRouteDistance: 36);

        // ---------------------------------------------------------------- M (:1159-1330) label/corridor co-repair
        void RepairLabelsAndCorridors()
        {
            var titles = Titles();
            const double labelGap = 4;
            // Bound the complete candidate checks, including rejected geometry, across
            // the diagram. Each accepted move clears one route's label collisions.
            var attempts = 0;
            bool NearOwnRoute(LabelRect rect, IReadOnlyList<Pt> points) => SegmentsOf(points)
                .Any(segment => (Geometry.SegmentRectClearanceWithin(segment, rect, 36) ?? 0) <= 36);
            bool MasksLabel(IReadOnlyList<Pt> points, LabelRect rect) => SegmentsOf(points)
                .Any(segment => (Geometry.SegmentRectClearanceWithin(segment, rect, labelGap) ?? 0) + 0.0001 < labelGap);
            bool OverlapsNode(LabelRect rect) => nodes.Values.Any(node => Geometry.RectsOverlap(rect, node, -2));
            // A label left inside a node does not mask the route occupying its usable
            // slot. Probe a nearby node-clear slot with the other labels fixed. These
            // are search targets only: accept a corridor move after placing every label
            // against the real routes, including resolving the original node overlap.
            var slotProbes = 0;
            var blockedLabelSlots = new List<LabelRect>();
            for (var index = 0; index < resolvedLabelRects.Count; index++)
            {
                var rect = resolvedLabelRects[index];
                if (slotProbes >= 16 || LabelPinKeys.Any(rect.Relation.ContainsKey) || !OverlapsNode(rect)) continue;
                slotProbes++;
                var labels = resolvedLabelRects.Select((other, otherIndex) => otherIndex == index ? Lowered(other) : PinnedCopy(other)).ToList();
                var probe = AutomaticLabels.PlaceAutomaticLabels(labels, [new LabelRoute(rect.RelationIndex!.Value, PathFor(rect.Relation).Points)],
                    Components(), titles, viewBox, placementBottom: PlacementBottom, maxOwnRouteDistance: 36)[index];
                if (!ReferenceEquals(probe, labels[index])) blockedLabelSlots.Add(probe);
            }
            foreach (var flow in routable)
            {
                if (attempts >= 16) break;
                if (!SupportsAutomaticCrossover(flow) || LabelPinKeys.Any(flow.ContainsKey)) continue;
                var current = Geometry.NormalizeRoutePoints(PathFor(flow).Points);
                var pendingSlots = blockedLabelSlots.Where(slot => OverlapsNode(
                    resolvedLabelRects.First(rect => ReferenceEquals(rect.Relation, slot.Relation)))).ToList();
                var blocked = resolvedLabelRects.Concat(pendingSlots)
                    .Where(rect => !ReferenceEquals(rect.Relation, flow) && MasksLabel(current, rect)).ToList();
                if (blocked.Count == 0) continue;
                var candidates = new JsMap<string, List<Pt>>();
                foreach (var rect in blocked)
                {
                    for (var index = 1; index < current.Count - 2; index++)
                    {
                        var (start, end) = (current[index], current[index + 1]);
                        if ((Geometry.SegmentRectClearanceWithin(new(start, end), rect, labelGap) ?? 0) + 0.0001 >= labelGap) continue;
                        var axis = start.X == end.X ? 0 : start.Y == end.Y ? 1 : -1;
                        if (axis == -1) continue;
                        var (low, size) = axis == 0 ? (rect.X, rect.Width) : (rect.Y, rect.Height);
                        foreach (var coordinate in new[] { low - labelGap, low + size + labelGap })
                        {
                            var points = current.ToList();
                            points[index] = With(points[index], axis, coordinate);
                            points[index + 1] = With(points[index + 1], axis, coordinate);
                            // Do not reverse either neighbouring segment into a doubled-back
                            // path. A zero-length neighbour can be removed by normalization.
                            if (new[] { index - 1, index + 1 }.Any(k =>
                                (current[k + 1].X - current[k].X) * (points[k + 1].X - points[k].X)
                                + (current[k + 1].Y - current[k].Y) * (points[k + 1].Y - points[k].Y) < 0)) continue;
                            var normalized = Geometry.NormalizeRoutePoints(points);
                            candidates.Set(PointsKey(normalized), normalized);   // first key position wins
                        }
                    }
                }
                var (fromSide, toSide) = FlowSides(flow);
                var oldCrossings = Unrelated(flow).Select(other => (Other: other, Count: IntersectionCount(current, PathFor(other).Points))).ToList();
                var oldCost = ReadingCost(flow, current);
                var ranked = candidates.Values.Select(points => (Points: points, Cost: ReadingCost(flow, points))).ToList();
                Js.Sort(ranked, (a, b) => a.Cost - b.Cost);
                foreach (var (points, cost) in ranked)
                {
                    if (attempts >= 16) break;
                    attempts++;
                    // At most two bend-costs of extra distance; never add bends, crossings,
                    // self-overlaps, shared corridors or a new route through a node.
                    if (points.Count > current.Count || cost > oldCost + 96 || FoldsBack(points)
                        || !Geometry.RouteHonorsEndpointSides(points, fromSide, toSide)
                        || points.Any(p => p.X < 0 || p.Y < 0 || p.X > viewBox[0] || p.Y > PlacementBottom)
                        || IntrudesNode(flow, points) || SharesCorridor(flow, points)
                        || Geometry.CollectRouteRhythmIssues([new RoutedRelation(points)]).Count > 0
                        || Geometry.CollectBorderRuns([new RoutedRelation(points)], compositionFrames).Count > 0
                        || oldCrossings.Any(entry =>
                        {
                            var nextCount = IntersectionCount(points, PathFor(entry.Other).Points);
                            return nextCount > entry.Count || (nextCount > 0 && !SupportsAutomaticCrossover(entry.Other));
                        })) continue;
                    var labels = resolvedLabelRects.Select(Lowered).ToList();
                    var routes = flows.Select((other, relationIndex) => (other, relationIndex))
                        .Where(e => Has(e.other.Str("from")) && Has(e.other.Str("to")))
                        .Select(e => new LabelRoute(e.relationIndex, ReferenceEquals(e.other, flow) ? points : PathFor(e.other).Points)).ToList();
                    var placed = AutomaticLabels.PlaceAutomaticLabels(labels, routes, Components(), titles, viewBox,
                        placementBottom: PlacementBottom, maxOwnRouteDistance: 36);
                    if (placed.Any(rect => !ReferenceEquals(rect.Relation, flow) && MasksLabel(points, rect))) continue;
                    if (blocked.Any(slot => pendingSlots.Any(pending => ReferenceEquals(pending, slot)) && OverlapsNode(
                        placed.First(rect => ReferenceEquals(rect.Relation, slot.Relation))))) continue;
                    if (placed.Where((rect, index) =>
                    {
                        var oldPoints = PathFor(rect.Relation).Points;
                        var ownPoints = ReferenceEquals(rect.Relation, flow) ? points : oldPoints;
                        return ((ReferenceEquals(rect.Relation, flow) || !ReferenceEquals(rect, labels[index])) && !NearOwnRoute(rect, ownPoints))
                            || (ExtraLabelLineMasksRoute(rect, ownPoints) && !ExtraLabelLineMasksRoute(labels[index], oldPoints));
                    }).Any()) continue;
                    pathCache.Set(flow, Routed(points));
                    for (var index = 0; index < placed.Count; index++)
                    {
                        var resolved = Raised(placed[index]);
                        resolvedLabelRects[index] = resolved;
                        resolvedLabelPoints.Set(placed[index].Relation, AnchorOf(resolved));
                    }
                    break;
                }
            }
            // Once labels and routes are settled, align only the free endpoint of a
            // short port bridge. Keep other routes and labels fixed so a local straight
            // line cannot trigger a longer detour or hide a neighbouring label.
            var alignmentProbes = 0;
            foreach (var flow in routable)
            {
                if (alignmentProbes >= 16) break;
                if (!SupportsAutomaticCrossover(flow) || LabelPinKeys.Any(flow.ContainsKey)) continue;
                var current = Geometry.NormalizeRoutePoints(PathFor(flow).Points);
                if (current.Count <= 2) continue;
                var index = resolvedLabelRects.FindIndex(rect => ReferenceEquals(rect.Relation, flow));
                if (index < 0) continue;
                var from = NodeOf(flow.Str("from"))!;
                var to = NodeOf(flow.Str("to"))!;
                var (fromSide, toSide) = FlowSides(flow);
                LabelRect? placedLabel = null;
                var aligned = Geometry.AlignAutomaticFacingPorts(flow, from, to, current[0], current[^1], fromSide, toSide, automaticPorts.Get(flow), accept: points =>
                {
                    if (alignmentProbes >= 16 || IntrudesNode(flow, points)
                        || Unrelated(flow).Any(other => IntersectionCount(points, PathFor(other).Points) > IntersectionCount(current, PathFor(other).Points))
                        || resolvedLabelRects.Any(rect => !ReferenceEquals(rect.Relation, flow) && MasksLabel(points, rect))) return false;
                    foreach (var (node, side, point, original) in new[] { (from, fromSide, points[0], current[0]), (to, toSide, points[1], current[^1]) })
                    {
                        if (Js.Hypot(point.X - original.X, point.Y - original.Y) < 0.0001) continue;
                        foreach (var other in routable)
                        {
                            foreach (var field in new[] { "from", "to" })
                            {
                                var otherSides = FlowSides(other);
                                if (ReferenceEquals(other, flow) || other.Str(field) != node.Id
                                    || (field == "from" ? otherSides.FromSide : otherSides.ToSide) != side) continue;
                                var occupied = field == "from" ? PathFor(other).Points[0] : PathFor(other).Points[^1];
                                if (Js.Hypot(point.X - occupied.X, point.Y - occupied.Y) < 18 - 0.0001) return false;
                            }
                        }
                    }
                    if (routable.Any(other => !ReferenceEquals(other, flow) && Geometry.CollectAmbiguousCorridors(
                        [new RoutedRelation(points, flow), new RoutedRelation(PathFor(other).Points, other)],
                        includeSharedEndpoints: (_, _) => true).Count > 0)) return false;
                    alignmentProbes++;
                    var (lx, ly) = LabelPointFor(flow, points);
                    var rect = resolvedLabelRects[index];
                    var labels = resolvedLabelRects.Select((other, i) =>
                    {
                        if (i != index) return PinnedCopy(other);
                        var own = other.Clone();
                        own.X = lx - other.Width / 2;
                        own.Y = ly - 11;
                        own.Lx = lx;
                        own.Ly = ly - 1;
                        return own;
                    }).ToList();
                    var routes = routable.Select(other => new LabelRoute(FlowIndex(other), ReferenceEquals(other, flow) ? points : PathFor(other).Points)).ToList();
                    var candidate = AutomaticLabels.PlaceAutomaticLabels(labels, routes, Components(), titles, viewBox,
                        placementBottom: PlacementBottom, maxOwnRouteDistance: 36)[index];
                    if (candidate.X < 0 || candidate.Y < 0 || candidate.X + candidate.Width > viewBox[0]
                        || candidate.Y + candidate.Height > PlacementBottom
                        || nodes.Values.Select(node => (Rect)node).Concat(titles).Any(obstacle => Geometry.RectsOverlap(candidate, obstacle, 2))
                        || resolvedLabelRects.Where((_, i) => i != index).Any(other => Geometry.RectsOverlap(candidate, other, 2))
                        || routes.Any(route => route.RelationIndex != rect.RelationIndex && MasksLabel(route.Points, candidate))
                        || !NearOwnRoute(candidate, points)
                        || (ExtraLabelLineMasksRoute(candidate, points) && !ExtraLabelLineMasksRoute(rect, current))) return false;
                    placedLabel = Raised(candidate);
                    return true;
                });
                if (placedLabel is null) continue;
                List<Pt> aligned2 = [aligned.Start, aligned.End];
                pathCache.Set(flow, Routed(aligned2));
                resolvedLabelRects[index] = placedLabel;
                resolvedLabelPoints.Set(flow, AnchorOf(placedLabel));
            }
            CollectCrossovers();
        }

        // ---------------------------------------------------------------- N (:1335-1345) wrapBlockedFlowLabels
        void WrapBlockedLabels()
        {
            var wrapped = LabelCapacity.WrapBlockedFlowLabels(resolvedLabelRects,
                routable.Select(flow => new LabelRoute(FlowIndex(flow), PathFor(flow).Points)).ToList(),
                Components(), Titles(), viewBox, PlacementBottom);
            for (var index = 0; index < wrapped.Count; index++)
            {
                var rect = wrapped[index];
                if (ReferenceEquals(rect, resolvedLabelRects[index])) continue;
                resolvedLabelRects[index] = rect;
                resolvedLabelPoints.Set(rect.Relation, AnchorOf(rect));
            }
        }

        // ---------------------------------------------------------------- O (:1359-1380) repairLabelRoutes, height refit, crossover recompute
        void RepairJointLabelRoutes()
        {
            var (labels, routes) = JointLabelRouting.RepairLabelRoutes(resolvedLabelRects,
                routable.Select(flow => new RoutedRelation(PathFor(flow).Points, flow, FlowIndex(flow))).ToList(),
                Components(), Titles(), compositionFrames, viewBox, PlacementBottom,
                sideFor: flow => { var sides = FlowSides(flow); return (sides.FromSide, sides.ToSide); });
            if (ReferenceEquals(labels, resolvedLabelRects)) return;   // `proposal.labels !== resolvedLabelRects`: list identity
            foreach (var route in routes) pathCache.Set(route.Relation!, Routed(route.Points!));
            for (var index = 0; index < labels.Count; index++)
            {
                var rect = labels[index];
                resolvedLabelRects[index] = rect;
                resolvedLabelPoints.Set(rect.Relation, AnchorOf(rect));
            }
            viewBox[1] = HeightForLabels(resolvedLabelRects);
            RefitFrameHeights();
            resolvedCrossoverFlows = new();
            CollectCrossovers();
        }
    }
}
