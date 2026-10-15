using System.Text.Json.Nodes;
using static Archify.Net.WorkflowModel;

namespace Archify.Net;

// workflow-compiler.mjs readable-v2 automatic routing: F readableAutomaticSides (:1267-1352), the H1/H3 pin predicates, H2
// validateReadablePairwisePinConflicts (:2060-2192), H4 clearStraightAutomaticPlan (:2336-2377), the H5 driver
// validateReadablePinnedGeometry (:2378-2691), J3 feasibility (:3402-3706), K1 cost and candidate set (:3750-4040), K2
// readableAutomaticVia (:4042-4203), K5 port candidates, endpoint repairs, internal detours, free space (:4470-4691) and K6
// readableAutomaticRoute (:4693-4934). The pin-conflict throwers these reach (only an authored pin — labelAt, via,
// channelX/Y, a non-auto route or side — does) are in WorkflowCompilation.Pins.cs.
internal sealed partial class WorkflowCompilation
{
    // A cost record (:3947-3966) as its 15 dimensions in READABLE_CANDIDATE_COST_PRIORITY order.
    internal sealed class Cost(double[] v)
    {
        public readonly double[] V = v;
        public double ProperCrossingCount => V[0];
        public double UnrelatedBendContactCount => V[1];
        public double SharedCorridorPx => V[2];
        public double LabelRouteClearanceDeficit => V[4];
        public double StableCandidateOrdinal => V[14];
    }

    // A candidate or plan: `{ family, via, points, ordinal, cost, fromSide, toSide }`. `With` is `{ ...plan, points, cost }`.
    internal sealed class Plan
    {
        public required List<Pt> Points { get; init; }
        public required string FromSide { get; init; }
        public required string ToSide { get; init; }
        public string? Family { get; init; }
        public List<Pt>? Via { get; init; }
        public Cost Cost { get; init; } = null!;
        public Plan With(List<Pt> points, Cost cost) => new() { Points = points, FromSide = FromSide, ToSide = ToSide, Family = Family, Via = Via, Cost = cost };
    }

    internal sealed record CandidateSet(List<Plan> RawCandidates, List<Plan> Candidates, double OutsideRight);

    static readonly string[] SideOrder = ["right", "bottom", "left", "top"];
    static double Ax(Pt p, int axis) => axis == 0 ? p.X : p.Y;
    static Pt Shift(Pt p, int axis, double offset) => axis == 0 ? p with { X = p.X + offset } : p with { Y = p.Y + offset };
    static Rect RectOf(SceneObstacle o) => new(o.X, o.Y, o.Width, o.Height);
    // `edge.fromSide && edge.fromSide !== 'auto' ? edge.fromSide : null`
    static string? AuthoredSide(JsonObject edge, string field) => edge[field].Truthy() && edge.Str(field) != "auto" ? edge.Str(field) : null;
    static bool HasAny(JsonObject edge, params string[] fields) => fields.Any(edge.ContainsKey);
    bool SharesEndpoint(JsonObject edge, JsonObject other) =>
        ((JsonNode?[])[edge["from"], edge["to"]]).Any(id => StrictEquals(id, other["from"]) || StrictEquals(id, other["to"]));
    static double StrokeWidth(JsonObject relation) => Js.Or(relation.Num("width"), relation.Str("variant") == "emphasis" ? 1.8 : 1.4);
    static JsonObject ArrowRelation(JsonObject relation)
    {
        var copy = relation.DeepClone().AsObject();
        copy["width"] = StrokeWidth(relation);
        return copy;
    }

    // ---------------------------------------------------------------- F: readableAutomaticSides (:1267-1352)
    Sides? ReadableAutomaticSides(JsonObject edge, Box from, Box to)
    {
        var automaticRoute = !edge["via"].Truthy() && !edge.ContainsKey("channelX") && !edge.ContainsKey("channelY")
            && (!edge["route"].Truthy() || edge.Str("route") == "auto");
        var authoredFrom = AuthoredSide(edge, "fromSide");
        var authoredTo = AuthoredSide(edge, "toSide");
        if (!automaticRoute || authoredFrom is not null && authoredTo is not null) return null;
        if (readableSideCache.Has(edge)) return readableSideCache.Get(edge);

        var preferred = new List<Sides>();
        if (LegacyAutomaticOneBendSides(edge, from, to) is { } legacyPreferred) preferred.Add(legacyPreferred);
        preferred.Add(new(authoredFrom ?? Geometry.LegacyDefaultFromSide(from, to), authoredTo ?? Geometry.LegacyDefaultToSide(from, to)));
        foreach (var fromSide in authoredFrom is not null ? [authoredFrom] : SideOrder)
            foreach (var toSide in authoredTo is not null ? [authoredTo] : SideOrder)
                preferred.Add(new(fromSide, toSide));

        var seen = new JsSet<string>();
        var sidePairs = new List<Sides>();
        foreach (var candidate in preferred)
        {
            if (authoredFrom is not null && candidate.FromSide != authoredFrom) continue;
            if (authoredTo is not null && candidate.ToSide != authoredTo) continue;
            var key = $"{candidate.FromSide}:{candidate.ToSide}";
            if (seen.Has(key)) continue;
            seen.Add(key);
            sidePairs.Add(candidate);
        }

        var naturalFromSide = authoredFrom ?? Geometry.LegacyDefaultFromSide(from, to);
        var naturalToSide = authoredTo ?? Geometry.LegacyDefaultToSide(from, to);
        CandidateSet PlanFor(Sides candidate, int pairOrdinal) => ReadableAutomaticCandidateSet(edge, from, to,
            Geometry.Anchor(from, candidate.FromSide), Geometry.Anchor(to, candidate.ToSide), candidate.FromSide, candidate.ToSide,
            pairOrdinal * 9, naturalFromSide, naturalToSide);

        if (sidePairs.Count > 0)
        {
            var primary = sidePairs[0];
            if (PlanFor(primary, 0).Candidates.Count > 0)
            {
                readableSideCache.Set(edge, primary);
                return primary;
            }
        }

        var candidates = new List<Plan>();
        for (var pairOrdinal = 0; pairOrdinal < sidePairs.Count; pairOrdinal++) candidates.AddRange(PlanFor(sidePairs[pairOrdinal], pairOrdinal).Candidates);
        Js.Sort(candidates, (left, right) => CompareCost(left.Cost, right.Cost));
        if (candidates.Count > 0)
        {
            var selected = new Sides(candidates[0].FromSide, candidates[0].ToSide);
            readableSideCache.Set(edge, selected);
            return selected;
        }
        readableSideCache.Set(edge, null);
        return null;
    }

    // ---------------------------------------------------------------- H1/H3 pin predicates (:1724-1737, :1872-1876, :2193-2334)
    static List<string> AuthoredRouteAssertionFields(JsonObject? edge)
    {
        var fields = new List<string>();
        if (edge is null) return fields;
        if (edge["via"] is JsonArray) fields.Add("via");
        if (edge.ContainsKey("channelX")) fields.Add("channelX");
        if (edge.ContainsKey("channelY")) fields.Add("channelY");
        if (edge["route"].Truthy() && edge.Str("route") != "auto") fields.Add("route");
        if (edge["fromSide"].Truthy() && edge.Str("fromSide") != "auto") fields.Add("fromSide");
        if (edge["toSide"].Truthy() && edge.Str("toSide") != "auto") fields.Add("toSide");
        return fields;
    }

    static bool HasAuthoredRouteAssertions(JsonObject? edge) => AuthoredRouteAssertionFields(edge).Count > 0;

    static bool HasAbsoluteRoutePins(JsonObject? edge) => edge is not null && (edge["via"] is JsonArray || edge.ContainsKey("channelX") || edge.ContainsKey("channelY"));

    static readonly Dictionary<string, string[]> ReadablePresetPinFields = new()   // lookup only
    {
        ["straight"] = [], ["drop"] = ["channelY"], ["outside-right"] = ["channelX"], ["return-left"] = ["channelX"],
        ["bottom-channel"] = ["channelY"], ["up-channel"] = ["channelY"],
    };

    static List<string> PresentChannelPins(JsonObject edge) => [.. ((string[])["channelX", "channelY"]).Where(edge.ContainsKey)];

    void ValidateReadableRouteControls(JsonObject edge)
    {
        var channelPins = PresentChannelPins(edge);
        var preset = RouteOrAuto(edge);
        if (preset == "auto") return;
        var allowedPins = ReadablePresetPinFields.GetValueOrDefault(preset) ?? [];
        var conflictingPins = channelPins.Where(field => !allowedPins.Contains(field)).ToList();
        if (conflictingPins.Count == 0) return;
        throw RouteControlsConflict(edge, preset, allowedPins, conflictingPins);
    }

    static string SegmentOrientation(Pt start, Pt end) =>
        Math.Abs(start.X - end.X) <= 0.0001 ? "vertical" : Math.Abs(start.Y - end.Y) <= 0.0001 ? "horizontal" : "diagonal";

    static List<(Pt Start, Pt End, string Orientation)> RouteSegments(IReadOnlyList<Pt> points) =>
        [.. Enumerable.Range(0, Math.Max(0, points.Count - 1)).Select(index => (points[index], points[index + 1], SegmentOrientation(points[index], points[index + 1])))];

    static bool EndpointSideIsHonored(IReadOnlyList<Pt> points, string? side, string endpoint)
    {
        if (string.IsNullOrEmpty(side) || side == "auto" || points.Count < 2) return true;
        var source = endpoint == "source";
        var from = source ? points[0] : points[^2];
        var to = source ? points[1] : points[^1];
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        if (source)
        {
            return side switch
            {
                "right" => dx > 0 && Math.Abs(dy) <= 0.0001,
                "left" => dx < 0 && Math.Abs(dy) <= 0.0001,
                "bottom" => dy > 0 && Math.Abs(dx) <= 0.0001,
                "top" => dy < 0 && Math.Abs(dx) <= 0.0001,
                _ => false,
            };
        }
        return side switch
        {
            "right" => dx < 0 && Math.Abs(dy) <= 0.0001,
            "left" => dx > 0 && Math.Abs(dy) <= 0.0001,
            "bottom" => dy < 0 && Math.Abs(dx) <= 0.0001,
            "top" => dy > 0 && Math.Abs(dx) <= 0.0001,
            _ => false,
        };
    }

    static bool CorridorTopologyMatches(IReadOnlyList<Pt> points, string axis, double coordinate)
    {
        var collapsed = Geometry.NormalizeRoutePoints(points);
        var start = collapsed[0];
        var end = collapsed[^1];
        List<Pt> via = axis == "x" ? [new(coordinate, start.Y), new(coordinate, end.Y)] : [new(start.X, coordinate), new(end.X, coordinate)];
        var expected = Geometry.JoinRoutePoints(start, via, end);
        var actualPattern = RouteSegments(collapsed).Select(segment => segment.Orientation).ToList();
        var expectedPattern = RouteSegments(expected).Select(segment => segment.Orientation).ToList();
        return actualPattern.Count == expectedPattern.Count
            && actualPattern.SequenceEqual(expectedPattern)
            && RouteContainsChannelPin(collapsed, axis == "x" ? "channelX" : "channelY", coordinate);
    }

    static bool RouteMatchesPresetFamily(string? preset, IReadOnlyList<Pt> points, Box from, Box to)
    {
        var collapsed = Geometry.NormalizeRoutePoints(points);
        var segments = RouteSegments(collapsed);
        if (preset == "straight") return collapsed.Count == 2;
        if (preset == "drop")
        {
            if (StrictEquals(from.Source["lane"], to.Source["lane"])) return false;
            if (collapsed.Count == 2 && segments.Count > 0 && segments[0].Orientation == "vertical") return true;
            var upper = from.Cy <= to.Cy ? from : to;
            var lower = ReferenceEquals(upper, from) ? to : from;
            return segments.Any(segment => segment.Orientation == "horizontal"
                && segment.Start.Y >= upper.Y + upper.Height - 0.0001
                && segment.Start.Y <= lower.Y + 0.0001
                && CorridorTopologyMatches(points, "y", segment.Start.Y));
        }
        if (preset is "outside-right" or "return-left")
        {
            var boundary = preset == "outside-right" ? Math.Max(from.X + from.Width, to.X + to.Width) : Math.Min(from.X, to.X);
            return segments.Any(segment => segment.Orientation == "vertical"
                && (preset == "outside-right" ? segment.Start.X > boundary + 0.0001 : segment.Start.X < boundary - 0.0001)
                && CorridorTopologyMatches(points, "x", segment.Start.X));
        }
        if (preset is "bottom-channel" or "up-channel")
        {
            var boundary = preset == "bottom-channel" ? Math.Max(from.Y + from.Height, to.Y + to.Height) : Math.Min(from.Y, to.Y);
            return segments.Any(segment => segment.Orientation == "horizontal"
                && (preset == "bottom-channel" ? segment.Start.Y > boundary + 0.0001 : segment.Start.Y < boundary - 0.0001)
                && CorridorTopologyMatches(points, "y", segment.Start.Y));
        }
        return false;
    }

    static bool RouteContainsChannelPin(IReadOnlyList<Pt> points, string field, double value)
    {
        for (var index = 0; index < points.Count - 1; index++)
        {
            var start = points[index];
            var end = points[index + 1];
            if (field == "channelX" ? start.X == value && end.X == value && Math.Abs(end.Y - start.Y) > 0.0001
                : start.Y == value && end.Y == value && Math.Abs(end.X - start.X) > 0.0001) return true;
        }
        return false;
    }

    static Pt? ProperOrthogonalIntersection(Pt leftStart, Pt leftEnd, Pt rightStart, Pt rightEnd)
    {
        var leftOrientation = SegmentOrientation(leftStart, leftEnd);
        var rightOrientation = SegmentOrientation(rightStart, rightEnd);
        if (leftOrientation == rightOrientation || leftOrientation == "diagonal" || rightOrientation == "diagonal") return null;
        var horizontalStart = leftOrientation == "horizontal" ? leftStart : rightStart;
        var horizontalEnd = leftOrientation == "horizontal" ? leftEnd : rightEnd;
        var verticalStart = leftOrientation == "vertical" ? leftStart : rightStart;
        var verticalEnd = leftOrientation == "vertical" ? leftEnd : rightEnd;
        var point = new Pt(verticalStart.X, horizontalStart.Y);
        const double epsilon = 0.0001;
        var insideHorizontal = point.X > Math.Min(horizontalStart.X, horizontalEnd.X) + epsilon && point.X < Math.Max(horizontalStart.X, horizontalEnd.X) - epsilon;
        var insideVertical = point.Y > Math.Min(verticalStart.Y, verticalEnd.Y) + epsilon && point.Y < Math.Max(verticalStart.Y, verticalEnd.Y) - epsilon;
        return insideHorizontal && insideVertical ? point : null;
    }

    // The pin-conflict throwers (:1942-2010) are in WorkflowCompilation.Pins.cs.
    // classifyFailedAutomaticCandidatePins (:2012-2058): only reached when an edge carries labelAt or an absolute route pin.
    void ClassifyFailedAutomaticCandidatePins(JsonObject edge, List<Plan> rawCandidates)
    {
        var relationIndex = CanonicalIndex(edge);
        var priorRoutes = pathCache.Entries.Where(entry => !ReferenceEquals(entry.Key, edge))
            .Select(entry => new RoutedRelation(entry.Value.Points, entry.Key, CanonicalIndex(entry.Key))).ToList();
        if (priorRoutes.Count == 0) return;
        var priorLabels = priorRoutes.Select(route => LabelRectFor(route.Relation!, route.RelationIndex!.Value)).OfType<LabelRect>().ToList();

        foreach (var raw in rawCandidates)
        {
            var candidateRect = CandidateLabelRect(edge, raw.Points);
            if (candidateRect is { } rect)
            {
                var candidateLabel = new LabelRect
                {
                    Relation = edge, RelationIndex = relationIndex, Label = edge.Str("label"), X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height,
                };
                var priorLabel = priorLabels.FirstOrDefault(otherLabel => Geometry.RectsOverlap(candidateLabel, otherLabel, -2)
                    && (edge["labelAt"] is JsonArray || otherLabel.Relation["labelAt"] is JsonArray));
                if (priorLabel is not null) ThrowReadableLabelLabelPinConflict(candidateLabel, priorLabel);

                var labelRouteHit = Geometry.CollectLabelRouteClearance([candidateLabel], priorRoutes, 4)
                    .FirstOrDefault(hit => edge["labelAt"] is JsonArray || HasAbsoluteRoutePins(hit.OtherRelation));
                if (labelRouteHit is not null)
                {
                    var collidedRoute = priorRoutes.FirstOrDefault(route => ReferenceEquals(route.Relation, labelRouteHit.OtherRelation));
                    ThrowReadableLabelRoutePinConflict(labelRouteHit, collidedRoute?.Points);
                }
            }
            var reverseHit = Geometry.CollectLabelRouteClearance(priorLabels, [new RoutedRelation(raw.Points, edge, relationIndex)], 4)
                .FirstOrDefault(hit => hit.LabelRelation["labelAt"] is JsonArray);
            if (reverseHit is not null) ThrowReadableLabelRoutePinConflict(reverseHit, raw.Points);
        }
    }

    // ---------------------------------------------------------------- H2: validateReadablePairwisePinConflicts (:2060-2192)
    void ValidateReadablePairwisePinConflicts()
    {
        var labels = new List<LabelRect>();
        for (var relationIndex = 0; relationIndex < workflow.Edges.Count; relationIndex++)
            if (LabelRectFor(workflow.Edges[relationIndex], relationIndex) is { } label) labels.Add(label);
        var routedRelations = new List<RoutedRelation>();
        for (var relationIndex = 0; relationIndex < workflow.Edges.Count; relationIndex++)
        {
            var edge = workflow.Edges[relationIndex];
            if (Has(edge["from"]) && Has(edge["to"])) routedRelations.Add(new(PathFor(edge).Points, edge, relationIndex));
        }

        var labelRouteHit = Geometry.CollectLabelRouteClearance(labels, routedRelations, 4)
            .FirstOrDefault(hit => hit.LabelRelation["labelAt"] is JsonArray || HasAuthoredRouteAssertions(hit.OtherRelation));
        if (labelRouteHit is not null) ThrowReadableLabelRoutePinConflict(labelRouteHit);

        for (var leftIndex = 0; leftIndex < labels.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < labels.Count; rightIndex++)
            {
                if (!Geometry.RectsOverlap(labels[leftIndex], labels[rightIndex], -2)) continue;
                ThrowReadableLabelLabelPinConflict(labels[leftIndex], labels[rightIndex]);
            }
        }

        if (workflow.Meta.Str("quality_profile") != "showcase") return;
        for (var leftIndex = 0; leftIndex < routedRelations.Count; leftIndex++)
        {
            var left = routedRelations[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < routedRelations.Count; rightIndex++)
            {
                var right = routedRelations[rightIndex];
                if (!HasAuthoredRouteAssertions(left.Relation) && !HasAuthoredRouteAssertions(right.Relation)) continue;
                if (SharesEndpoint(left.Relation!, right.Relation!)) continue;
                foreach (var leftSegment in Geometry.ForwardCollinearAnalysisSegments(left.Points))
                {
                    foreach (var rightSegment in Geometry.ForwardCollinearAnalysisSegments(right.Points))
                    {
                        if (ProperOrthogonalIntersection(leftSegment.Start, leftSegment.End, rightSegment.Start, rightSegment.End) is not { } point) continue;
                        throw RouteRouteCrossingConflict(left, right, leftSegment, rightSegment, point);
                    }
                }
            }
        }

        var corridorHit = Geometry.CollectAmbiguousCorridors(routedRelations, 8)
            .FirstOrDefault(hit => HasAuthoredRouteAssertions(hit.Left.Relation) || HasAuthoredRouteAssertions(hit.Right.Relation));
        if (corridorHit is not null) throw RouteRouteCorridorConflict(corridorHit);
    }

    // ---------------------------------------------------------------- H4: clearStraightAutomaticPlan (:2336-2377)
    Plan? ClearStraightAutomaticPlan(JsonObject edge)
    {
        if (!IndependentAutomaticRoute(edge) || StrictEquals(edge["from"], edge["to"])) return null;
        // A return must not reserve the shared facing corridor before its main
        // relationship. It can still use a clear straight route in ordinary routing.
        if (edge.Str("role") != "main" && workflow.Edges.Any(other => other.Str("role") == "main"
            && StrictEquals(other["from"], edge["to"]) && StrictEquals(other["to"], edge["from"]) && !pathCache.Has(other))) return null;
        // Relative label pins are not absolute, but they still participate in
        // placed-label conflict evidence. Reserving a straight path for them first
        // can change which edge owns the corridor and scramble supportedFixes.
        if (HasAny(edge, "labelDx", "labelDy", "labelSegment")) return null;
        var from = NodeOf(edge["from"])!;
        var to = NodeOf(edge["to"])!;
        var start = Geometry.Anchor(from, Geometry.LegacyDefaultFromSide(from, to));
        var end = Geometry.Anchor(to, Geometry.LegacyDefaultToSide(from, to));
        var aligned = Math.Abs(start.X - end.X) < 0.0001 || Math.Abs(start.Y - end.Y) < 0.0001;
        if (!aligned || !RouteClearsUnrelatedNodes(edge, [start, end])) return null;
        // Facing centres are only a cheap filter. Port selection, labels and route
        // costs can still select a bent route; reserving that route first would let
        // a long branch displace a shorter one. Preview the ordinary planner and
        // reserve only its selected straight route, without registering obstacles.
        var hadSides = readableSideCache.Has(edge);
        var previousSides = readableSideCache.Get(edge);
        try
        {
            var plan = ctx.Diagnostics.WithRecordingSuppressed(() => ReadableAutomaticRoute(edge, from, to, EdgeSides(edge), automaticPorts.Get(edge)));
            return plan.Points.Count == 2 ? plan : null;
        }
        // A preview does not own layout feedback or failure diagnostics. Let the
        // ordinary routing order establish them with its complete preceding scene.
        catch (WorkflowLayoutFeedback)
        {
            return null;
        }
        catch (ArchifyDiagnosticException)
        {
            return null;
        }
        finally
        {
            if (hadSides) readableSideCache.Set(edge, previousSides);
            else readableSideCache.Delete(edge);
        }
    }

    // ---------------------------------------------------------------- H5: validateReadablePinnedGeometry (:2378-2691)
    // The routing driver: reserve pinned → reserve straight → routeFirst → all edges, then the per-edge pin checks and H2.
    void ValidateReadablePinnedGeometry()
    {
        if (workflow.SchemaVersion != 2) return;
        // Reserve absolute geometry at contested nodes before automatic routing,
        // independently of IDs. Unrelated routes retain their diagnostic ordering.
        foreach (var edge in workflow.Edges)
        {
            if (!HasAbsoluteRoutePins(edge) || !Has(edge["from"]) || !Has(edge["to"])) continue;
            if (!workflow.Edges.Any(other => !HasAbsoluteRoutePins(other)
                && ((JsonNode?[])[edge["from"], edge["to"]]).Any(id => StrictEquals(id, other["from"]) || StrictEquals(id, other["to"])))) continue;
            ValidateReadableRouteControls(edge);
            PathFor(edge);
        }
        // Reserve clear straight automatic routes before ID-ordered bending edges so
        // a later edge cannot steal a corridor that already has one good straight path.
        // Fix probes use the same reservation policy as public compilation. A
        // verified labelAt replacement retains causal label-pin evidence even when
        // deleting that pin lets automatic routing recreate its collision.
        // Skip when any edge carries a relative/absolute label pin: straight-first
        // would reorder corridors and scramble placed-label conflict evidence.
        var hasLabelPins = workflow.Edges.Any(edge => HasAny(edge, "labelAt", "labelDx", "labelDy", "labelSegment"));
        if (!hasLabelPins)
        {
            foreach (var edge in workflow.Edges)
            {
                if (!Has(edge["from"]) || !Has(edge["to"])) continue;
                var plan = ClearStraightAutomaticPlan(edge);
                if (plan is null) continue;
                ValidateReadableRouteControls(edge);
                PathFor(edge, plan);
            }
        }
        // Routing order feedback: an automatic edge that an earlier automatic edge
        // forced into a shared corridor or crossing is planned first on a retry.
        foreach (var index in layoutFeedback["routeFirst"].AsArrayOrEmpty())
        {
            // Diagnostics name source indexes; the canonical edge order differs.
            var edge = workflow.Edges.FirstOrDefault(candidate => sourceEdges.Has(candidate) && index is JsonValue v
                && v.GetValueKind() == System.Text.Json.JsonValueKind.Number && sourceEdges.Get(candidate) == v.D());
            if (edge is null || !Has(edge["from"]) || !Has(edge["to"]) || !IndependentAutomaticRoute(edge)) continue;
            PathFor(edge);
        }
        foreach (var edge in workflow.Edges)
        {
            if (!Has(edge["from"]) || !Has(edge["to"])) continue;
            ValidateReadableRouteControls(edge);
            // labelAt origin, negative route pins, the pinned-route checks and labelAt clearance (:2422-2688), in Pins.cs.
            ValidateReadableEdgePins(edge);
        }
        ValidateReadablePairwisePinConflicts();
    }

    // ---------------------------------------------------------------- J3: feasibility (:3402-3706)
    static Pt OutwardStub(Pt point, string side, double distance = 16)
    {
        var (dx, dy) = side switch { "left" => (-1, 0), "right" => (1, 0), "top" => (0, -1), "bottom" => (0, 1), _ => (0, 0) };
        return new(point.X + dx * distance, point.Y + dy * distance);
    }

    static bool OrthogonalRoute(IReadOnlyList<Pt> points)
    {
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return false;
            if (index == 0) continue;
            var previous = points[index - 1];
            var dx = Math.Abs(point.X - previous.X);
            var dy = Math.Abs(point.Y - previous.Y);
            if (dx <= 0.0001 == dy <= 0.0001) return false;
        }
        return true;
    }

    static bool RouteClearsEndpointNodes(IReadOnlyList<Pt> points, Box from, Box to)
    {
        var lastSegment = points.Count - 2;
        for (var index = 0; index <= lastSegment; index++)
        {
            var segment = new Segment(points[index], points[index + 1]);
            if (index > 0 && Geometry.SegmentIntersectsRect(segment, from)) return false;
            if (index < lastSegment && Geometry.SegmentIntersectsRect(segment, to)) return false;
        }
        return true;
    }

    static bool RouteMeetsHardRhythm(IReadOnlyList<Pt> points)
    {
        if (points.Count == 2)
            return Js.Hypot(points[1].X - points[0].X, points[1].Y - points[0].Y) + 0.0001 >= HardRouteSegmentMinimum(points, 0);
        for (var index = 0; index < points.Count - 1; index++)
        {
            var point = points[index];
            var length = Math.Abs(points[index + 1].X - point.X) + Math.Abs(points[index + 1].Y - point.Y);
            if (!(length + 0.0001 >= HardRouteSegmentMinimum(points, index))) return false;
        }
        return true;
    }

    bool RouteLabelClearsNodes(JsonObject edge, IReadOnlyList<Pt> points)
    {
        if (!edge["label"].Truthy() || edge["labelAt"].Truthy()) return true;
        var (lx, ly) = WorkflowEdgeLabelPoint(edge, points);
        var width = WorkflowLabelWidth(edge.Str("label"));
        var rect = new Rect(lx - width / 2, ly - 10, width, 14);
        // The comparison shrinks the rect by two pixels, so only nodes inside the
        // shrunk box can fail it.
        var queryBox = new GridBox(rect.X + 2, rect.X + rect.Width - 2, rect.Y + 2, rect.Y + rect.Height - 2);
        foreach (var item in obstacleGrid.Query(queryBox))
        {
            if (item.Kind != "node") continue;
            if (Geometry.RectsOverlap(rect, item.Node!, -2)) return false;
        }
        return true;
    }

    Rect? CandidateLabelRect(JsonObject edge, IReadOnlyList<Pt> points)
    {
        if (!edge["label"].Truthy()) return null;
        var (lx, ly) = WorkflowEdgeLabelPoint(edge, points);
        var width = WorkflowLabelWidth(edge.Str("label"));
        return new Rect(lx - width / 2, ly - 10, width, 14);
    }

    double LabelRouteClearanceDeficit(JsonObject edge, IReadOnlyList<Pt> points, double threshold = 8)
    {
        var candidateLabel = CandidateLabelRect(edge, points);
        var extent = RouteBounds(points);
        if (candidateLabel is { } label)
        {
            var labelExtent = RectToBounds(label);
            extent = new(Math.Min(extent.MinX, labelExtent.MinX), Math.Max(extent.MaxX, labelExtent.MaxX),
                Math.Min(extent.MinY, labelExtent.MinY), Math.Max(extent.MaxY, labelExtent.MaxY));
        }
        var queryBox = new GridBox(extent.MinX - threshold, extent.MaxX + threshold, extent.MinY - threshold, extent.MaxY + threshold);
        // Accumulate in the order the routes were registered, which is the order the
        // previous full scan used, so the floating-point sum is identical.
        var items = obstacleGrid.Query(queryBox).Where(item => item.Kind is "route" or "label").ToList();
        Js.Sort(items, (left, right) => Js.Or(left.Sequence - right.Sequence, left.Kind == right.Kind ? 0 : left.Kind == "route" ? -1 : 1));
        double deficit = 0;
        foreach (var item in items)
        {
            if (item.Kind == "route")
            {
                if (candidateLabel is not { } rect) continue;
                var otherPoints = item.Routed!.Points;
                for (var index = 0; index < otherPoints.Count - 1; index++)
                {
                    if (Geometry.SegmentRectClearance(new(otherPoints[index], otherPoints[index + 1]), rect) is { } clearance)
                        deficit += Math.Max(0, threshold - clearance);
                }
            }
            else if (candidateLabel is not null || !edge["label"].Truthy())
            {
                for (var index = 0; index < points.Count - 1; index++)
                {
                    if (Geometry.SegmentRectClearance(new(points[index], points[index + 1]), item.Rect!) is { } clearance)
                        deficit += Math.Max(0, threshold - clearance);
                }
            }
        }
        return deficit;
    }

    // `evidence` (readablePresetRejection only) receives placedLabelConflictEvidence (:3516-3545, in Pins.cs) for the first conflict.
    bool RouteClearsPlacedLabels(JsonObject edge, IReadOnlyList<Pt> points, JsonObject? evidence = null)
    {
        void Assign(JsonObject conflict)
        {
            if (evidence is null) return;
            foreach (var (key, value) in conflict.ToList()) { conflict.Remove(key); evidence[key] = value; }
        }
        var candidateLabel = CandidateLabelRect(edge, points);
        var extent = RouteBounds(points);
        double minX = extent.MinX - 4, minY = extent.MinY - 4, maxX = extent.MaxX + 4, maxY = extent.MaxY + 4;
        if (candidateLabel is { } label)
        {
            minX = Math.Min(minX, label.X - 4);
            minY = Math.Min(minY, label.Y - 4);
            maxX = Math.Max(maxX, label.X + label.Width + 4);
            maxY = Math.Max(maxY, label.Y + label.Height + 4);
        }
        foreach (var item in obstacleGrid.Query(new GridBox(minX, maxX, minY, maxY)))
        {
            // A label may sit far from the route it annotates, so both boxes are queried.
            if (item.Kind == "label")
            {
                if (candidateLabel is { } own && Geometry.RectsOverlap(own, item.Rect!, -2))
                {
                    if (evidence is not null) Assign(PlacedLabelConflictEvidence(edge, candidateLabel, item, "label-label"));
                    return false;
                }
                Rect rect = item.Rect!;
                // Either axis further than the minimum clearance means no segment can reach it.
                if (Math.Max(rect.X - extent.MaxX, extent.MinX - (rect.X + rect.Width)) >= 4
                    || Math.Max(rect.Y - extent.MaxY, extent.MinY - (rect.Y + rect.Height)) >= 4) continue;
                for (var index = 0; index < points.Count - 1; index++)
                {
                    if (Geometry.SegmentRectClearance(new(points[index], points[index + 1]), rect) is { } clearance && clearance + 0.0001 < 4)
                    {
                        if (evidence is not null) Assign(PlacedLabelConflictEvidence(edge, candidateLabel, item, "route-label", [.. points], index, clearance));
                        return false;
                    }
                }
            }
            else if (item.Kind == "route" && candidateLabel is { } own)
            {
                var otherBounds = item.Bounds;
                if (Math.Max(own.X - otherBounds.MaxX, otherBounds.MinX - (own.X + own.Width)) >= 4
                    || Math.Max(own.Y - otherBounds.MaxY, otherBounds.MinY - (own.Y + own.Height)) >= 4) continue;
                var otherPoints = item.Routed!.Points;
                for (var index = 0; index < otherPoints.Count - 1; index++)
                {
                    if (Geometry.SegmentRectClearance(new(otherPoints[index], otherPoints[index + 1]), own) is { } clearance && clearance + 0.0001 < 4)
                    {
                        if (evidence is not null) Assign(PlacedLabelConflictEvidence(edge, candidateLabel, item, "label-route", otherPoints, index, clearance));
                        return false;
                    }
                }
            }
        }
        return true;
    }

    bool RouteClearsLegend(JsonObject edge, IReadOnlyList<Pt> points)
    {
        if (workflowLegendEntries.Count == 0) return true;
        foreach (var (_, rect) in WorkflowLegendRects())
        {
            for (var index = 0; index < points.Count - 1; index++)
            {
                if (Geometry.SegmentIntersectsRect(new(points[index], points[index + 1]), rect)) return false;
            }
            if (CandidateLabelRect(edge, points) is { } label && Geometry.RectsOverlap(label, rect)) return false;
        }
        return true;
    }

    // Scene label obstacles are a pure function of the document and the layout.
    List<SceneObstacle>? cachedSceneLabelObstacles;
    List<SceneObstacle> SceneLabelObstacles()
    {
        if (cachedSceneLabelObstacles is null)
        {
            cachedSceneLabelObstacles = WorkflowSceneLabelObstacles();
            foreach (var obstacle in cachedSceneLabelObstacles)
                obstacleGrid.Insert(RectToBounds(RectOf(obstacle)), new ObstacleItem { Kind = "scene", Obstacle = obstacle });
        }
        return cachedSceneLabelObstacles;
    }

    bool RouteClearsSceneLabelObstacles(JsonObject edge, IReadOnlyList<Pt> points)
    {
        // Asking for the cached list also publishes it to the grid.
        SceneLabelObstacles();
        var label = CandidateLabelRect(edge, points);
        var extent = RouteBounds(points);
        double minX = extent.MinX, minY = extent.MinY, maxX = extent.MaxX, maxY = extent.MaxY;
        if (label is { } l)
        {
            minX = Math.Min(minX, l.X);
            minY = Math.Min(minY, l.Y);
            maxX = Math.Max(maxX, l.X + l.Width);
            maxY = Math.Max(maxY, l.Y + l.Height);
        }
        foreach (var item in obstacleGrid.Query(new GridBox(minX, maxX, minY, maxY)))
        {
            if (item.Kind != "scene") continue;
            var obstacle = RectOf(item.Obstacle!);
            for (var index = 0; index < points.Count - 1; index++)
            {
                if (Geometry.SegmentIntersectsRect(new(points[index], points[index + 1]), obstacle)) return false;
            }
            if (label is { } own && Geometry.RectsOverlap(own, obstacle)) return false;
        }
        return true;
    }

    // Composition frames are pure as well; only a frame that reaches the candidate
    // can share a border line with it.
    List<Frame>? cachedCompositionFrames;
    List<Frame> CompositionFrames() => cachedCompositionFrames ??= WorkflowCompositionFrames();

    bool RouteClearsFrameBorders(IReadOnlyList<Pt> points)
    {
        var extent = RouteBounds(points);
        var nearFrames = CompositionFrames().Where(frame => BoundsOverlap(RectToBounds(new(frame.X, frame.Y, frame.Width, frame.Height)), extent, 0)).ToList();
        if (nearFrames.Count == 0) return true;
        return Geometry.CollectBorderRuns([new RoutedRelation([.. points])], nearFrames).Count == 0;
    }

    bool RouteFitsCanvasOrigin(JsonObject edge, IReadOnlyList<Pt> points)
    {
        var coordinates = new List<Pt>(points);
        if (!edge["labelAt"].Truthy() && CandidateLabelRect(edge, points) is { } label)
        {
            coordinates.Add(new(label.X, label.Y));
            coordinates.Add(new(label.X + label.Width, label.Y + label.Height));
        }
        return coordinates.All(point => point.X >= 0 && point.Y >= 0);
    }

    // One ordered contract for acceptance and the first rejection. Cheap and
    // selective checks run before expensive clearance work. Direct calls need no
    // dispatch table, callbacks or context objects, and never collect evidence.
    string? ReadableCandidateRejection(JsonObject edge, IReadOnlyList<Pt> points, Box from, Box to, string fromSide, string toSide)
    {
        if (points.Count < 2) return "route endpoints";
        if (!OrthogonalRoute(points)) return "orthogonal non-zero segments";
        if (!RouteMeetsHardRhythm(points)) return "readable segment rhythm";
        if (!Geometry.RouteHonorsEndpointSides(points, fromSide, toSide)) return "perpendicular endpoint-side direction";
        if (!RouteClearsEndpointNodes(points, from, to)) return "node clearance";
        if (!RouteLabelClearsNodes(edge, points)) return "edge-label node clearance";
        if (!RouteClearsSceneLabelObstacles(edge, points)) return "lane/phase/group label clearance";
        if (!RouteClearsUnrelatedNodes(edge, points)) return "node clearance";
        if (!RouteClearsPlacedLabels(edge, points)) return "placed edge-label clearance";
        if (!RouteFitsCanvasOrigin(edge, points)) return "canvas origin";
        if (!RouteClearsFrameBorders(points)) return "structural-frame border clearance";
        if (!RouteClearsLegend(edge, points)) return "legend clearance";
        return null;
    }

    bool ReadableCandidateIsFeasible(JsonObject edge, IReadOnlyList<Pt> points, Box from, Box to, string fromSide, string toSide) =>
        ReadableCandidateRejection(edge, points, from, to, fromSide, toSide) is null;

    // ---------------------------------------------------------------- K1: cost and candidate set (:3750-4040)
    static List<Pt> CorridorViaY(Pt start, Pt end, string fromSide, string toSide, double y)
    {
        var startStub = OutwardStub(start, fromSide);
        var endStub = OutwardStub(end, toSide);
        return [startStub, new(startStub.X, y), new(endStub.X, y), endStub];
    }

    static List<Pt> CorridorViaX(Pt start, Pt end, string fromSide, string toSide, double x)
    {
        var startStub = OutwardStub(start, fromSide);
        var endStub = OutwardStub(end, toSide);
        return [startStub, new(x, startStub.Y), new(x, endStub.Y), endStub];
    }

    static bool ProperAxisCrossing(Pt a, Pt b, Pt c, Pt d)
    {
        var firstHorizontal = Math.Abs(a.Y - b.Y) <= 0.0001;
        var secondHorizontal = Math.Abs(c.Y - d.Y) <= 0.0001;
        if (firstHorizontal == secondHorizontal) return false;
        var (h0, h1) = firstHorizontal ? (a, b) : (c, d);
        var (v0, v1) = firstHorizontal ? (c, d) : (a, b);
        var x = v0.X;
        var y = h0.Y;
        return x > Math.Min(h0.X, h1.X) + 0.0001 && x < Math.Max(h0.X, h1.X) - 0.0001
            && y > Math.Min(v0.Y, v1.Y) + 0.0001 && y < Math.Max(v0.Y, v1.Y) - 0.0001;
    }

    (double ProperCrossingCount, double UnrelatedBendContactCount, double SharedCorridorPx, double OppositeParallelDeficit) RouteInteractionMetrics(JsonObject edge, List<Pt> points)
    {
        double properCrossingCount = 0, unrelatedBendContactCount = 0, sharedCorridorPx = 0, oppositeParallelDeficit = 0;
        var extent = RouteBounds(points);
        var nearbyRoutes = obstacleGrid.Query(new GridBox(extent.MinX - 8, extent.MaxX + 8, extent.MinY - 8, extent.MaxY + 8))
            .Where(item => item.Kind == "route").ToList();
        Js.Sort(nearbyRoutes, (left, right) => left.Sequence - right.Sequence);
        foreach (var item in nearbyRoutes)
        {
            var otherEdge = item.Edge!;
            var routed = item.Routed!;
            var sharedEndpoint = SharesEndpoint(edge, otherEdge);
            if (sharedEndpoint && !V2) continue;
            if (!sharedEndpoint && IsReadableAutomaticRoute(edge)) unrelatedBendContactCount += Geometry.PolylineBendContacts(points, routed.Points).Count;
            if (IsReadableAutomaticRoute(edge)) oppositeParallelDeficit += LongCounterflowDeficit(points, routed.Points);
            foreach (var hit in Geometry.CollectAmbiguousCorridors([new RoutedRelation(points, edge), new RoutedRelation(routed.Points, otherEdge)],
                includeSharedEndpoints: (_, _) => V2, allowShortWorkflowTrunks: V2)) sharedCorridorPx += hit.OverlapLength;
            for (var left = 0; left < points.Count - 1; left++)
            {
                for (var right = 0; right < routed.Points.Count - 1; right++)
                {
                    if (ProperAxisCrossing(points[left], points[left + 1], routed.Points[right], routed.Points[right + 1])) properCrossingCount += 1;
                }
            }
        }
        return (properCrossingCount, unrelatedBendContactCount, sharedCorridorPx, oppositeParallelDeficit);
    }

    // Prefer a clear existing track for long, close counterflow. This is only a
    // candidate preference: same-direction trunks and short endpoint runs stay
    // under the existing route contracts, and authored geometry is never edited.
    static double LongCounterflowDeficit(List<Pt> points, List<Pt> otherPoints)
    {
        var left = Geometry.NormalizeRoutePoints(points);
        var right = Geometry.NormalizeRoutePoints(otherPoints);
        double worst = 0;
        for (var a = 1; a < left.Count; a++)
        {
            for (var b = 1; b < right.Count; b++)
            {
                var horizontal = Math.Abs(left[a].Y - left[a - 1].Y) <= 0.0001 && Math.Abs(right[b].Y - right[b - 1].Y) <= 0.0001;
                var vertical = Math.Abs(left[a].X - left[a - 1].X) <= 0.0001 && Math.Abs(right[b].X - right[b - 1].X) <= 0.0001;
                if (!horizontal && !vertical) continue;
                var axis = horizontal ? 0 : 1;
                if ((Ax(left[a], axis) - Ax(left[a - 1], axis)) * (Ax(right[b], axis) - Ax(right[b - 1], axis)) >= 0) continue;
                var gap = Math.Abs(Ax(left[a], 1 - axis) - Ax(right[b], 1 - axis));
                if (gap <= 0.0001 || gap >= 8) continue;
                var overlap = Math.Min(Math.Max(Ax(left[a], axis), Ax(left[a - 1], axis)), Math.Max(Ax(right[b], axis), Ax(right[b - 1], axis)))
                    - Math.Max(Math.Min(Ax(left[a], axis), Ax(left[a - 1], axis)), Math.Min(Ax(right[b], axis), Ax(right[b - 1], axis)));
                worst = Math.Max(worst, Math.Max(0, overlap - 24) * (8 - gap));
            }
        }
        return worst;
    }

    double AutomaticForwardReversePx(JsonObject edge, List<Pt> points)
    {
        var from = NodeOf(edge["from"]);
        var to = NodeOf(edge["to"]);
        if (from is null || to is null || edge.Str("role") is "return" or "error" || Col(to) <= Col(from)) return 0;
        double total = 0;
        for (var index = 0; index < points.Count - 1; index++) total += Math.Max(0, points[index].X - points[index + 1].X);
        return total;
    }

    // Reserve only a measured, feasible horizontal arrival, not a future whole
    // route. This preference applies to automatic same-column upward rails only.
    readonly JsMap<JsonObject, (int RouteCount, List<(Pt A, Pt B)> Arrivals)> laterArrivalCache = new();
    double LaterArrivalBlockCount(JsonObject edge, List<Pt> points)
    {
        if (!IndependentAutomaticRoute(edge) || HasAny(edge, "labelDx", "labelDy", "labelSegment")) return 0;
        var from = NodeOf(edge["from"])!;
        var to = NodeOf(edge["to"])!;
        if (Col(from) != Col(to) || to.Cy >= from.Cy) return 0;
        var cached = laterArrivalCache.Has(edge) ? laterArrivalCache.Get(edge) : default;
        if (cached.Arrivals is null || cached.RouteCount != pathCache.Count)
        {
            var arrivals = new List<(Pt, Pt)>();
            var eligible = workflow.Edges.Where(incoming =>
            {
                if (ReferenceEquals(incoming, edge) || pathCache.Has(incoming) || !IndependentAutomaticRoute(incoming)
                    || HasAny(incoming, "labelDx", "labelDy", "labelSegment")) return false;
                var source = NodeOf(incoming["from"]);
                var target = NodeOf(incoming["to"]);
                return source is not null && target is not null && Col(target) == Col(from) && Col(source) != Col(target)
                    && target.Cy > to.Cy && target.Cy < from.Cy;
            }).ToList();
            // Skip crowded scenes entirely: no order-dependent first-eight choice,
            // and at most eight prospective feasibility probes for this rail/scene.
            foreach (var incoming in eligible.Count <= 8 ? eligible : [])
            {
                var source = NodeOf(incoming["from"])!;
                var target = NodeOf(incoming["to"])!;
                var fromSide = source.Cx < target.Cx ? "right" : "left";
                var toSide = source.Cx < target.Cx ? "left" : "right";
                var start = Geometry.Anchor(source, fromSide);
                var end = Geometry.Anchor(target, toSide);
                var prospective = Geometry.JoinRoutePoints(start, CorridorViaX(start, end, fromSide, toSide, (start.X + end.X) / 2), end);
                if (!ReadableCandidateIsFeasible(incoming, prospective, source, target, fromSide, toSide)) continue;
                var interaction = RouteInteractionMetrics(incoming, prospective);
                if (WorkflowModel.Truthy(interaction.ProperCrossingCount) || WorkflowModel.Truthy(interaction.UnrelatedBendContactCount) || WorkflowModel.Truthy(interaction.SharedCorridorPx)
                    || WorkflowModel.Truthy(LabelRouteClearanceDeficit(incoming, prospective))) continue;
                arrivals.Add((OutwardStub(end, toSide), end));
            }
            cached = (pathCache.Count, arrivals);
            laterArrivalCache.Set(edge, cached);
        }
        return cached.Arrivals.Count(arrival => Enumerable.Range(1, Math.Max(0, points.Count - 3))
            .Any(index => ProperAxisCrossing(points[index], points[index + 1], arrival.A, arrival.B)));
    }

    Cost ReadableCandidateCost(JsonObject edge, List<Pt> points, double ordinal, string naturalFromSide, string naturalToSide)
    {
        var interaction = RouteInteractionMetrics(edge, points);
        var segmentLengths = new List<double>();
        for (var index = 0; index < points.Count - 1; index++)
            segmentLengths.Add(Math.Abs(points[index + 1].X - points[index].X) + Math.Abs(points[index + 1].Y - points[index].Y));
        double routeLength = 0;
        foreach (var length in segmentLengths) routeLength += length;
        var directLength = Math.Abs(points[^1].X - points[0].X) + Math.Abs(points[^1].Y - points[0].Y);
        double interiorPreferred28Deficit = 0;
        for (var index = 1; index < segmentLengths.Count - 1; index++) interiorPreferred28Deficit += Math.Max(0, 28 - segmentLengths[index]);
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity, minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach (var (x, y) in points)
        {
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }
        var canvasGrowthPx = Math.Max(0, -minX) + Math.Max(0, maxX - minimumCanvasWidth) + Math.Max(0, -minY) + Math.Max(0, maxY - lanes.AutoHeight);
        var from = NodeOf(edge["from"])!;
        var to = NodeOf(edge["to"])!;
        var naturalStart = Geometry.Anchor(from, naturalFromSide);
        var naturalEnd = Geometry.Anchor(to, naturalToSide);
        var portDisplacementPx = Math.Abs(points[0].X - naturalStart.X) + Math.Abs(points[0].Y - naturalStart.Y)
            + Math.Abs(points[^1].X - naturalEnd.X) + Math.Abs(points[^1].Y - naturalEnd.Y);
        var legacyCoordinateDisplacement = Math.Abs(from.Cx - ColAt(LegacyColumnCenters, Col(from))) + Math.Abs(to.Cx - ColAt(LegacyColumnCenters, Col(to)));
        var automaticForwardReversePx = AutomaticForwardReversePx(edge, points);
        var laterArrivalBlockCount = LaterArrivalBlockCount(edge, points);
        var labelRouteClearanceDeficit = LabelRouteClearanceDeficit(edge, points);
        return new([
            interaction.ProperCrossingCount,
            interaction.UnrelatedBendContactCount,
            interaction.SharedCorridorPx,
            automaticForwardReversePx,
            labelRouteClearanceDeficit,
            laterArrivalBlockCount,
            interaction.OppositeParallelDeficit,
            interiorPreferred28Deficit,
            // Keep bend count first for ordinary routes; avoid a many-fold outer trip
            // solely to save a corner when the endpoints share a column.
            IndependentAutomaticRoute(edge) && Col(from) == Col(to) && from.Cy != to.Cy ? Math.Max(0, routeLength - 2 * Math.Abs(to.Cy - from.Cy)) : 0,
            Math.Max(0, points.Count - 2),
            Js.Round((directLength > 0 ? routeLength / directLength : 1) * 1000),
            canvasGrowthPx,
            Js.Round(portDisplacementPx * 1000),
            legacyCoordinateDisplacement,
            ordinal,
        ]);
    }

    // `(left[dimension] || 0) - (right[dimension] || 0)` over the priority order; ties are real (Js.Sort stays stable).
    static double CompareCost(Cost left, Cost right)
    {
        for (var index = 0; index < left.V.Length; index++)
        {
            var a = Js.Or(left.V[index], 0);
            var b = Js.Or(right.V[index], 0);
            if (a != b) return a - b;
        }
        return 0;
    }

    CandidateSet ReadableAutomaticCandidateSet(JsonObject edge, Box from, Box to, Pt start, Pt end, string fromSide, string toSide,
        double ordinalOffset = 0, string? naturalFromSide = null, string? naturalToSide = null)
    {
        naturalFromSide ??= fromSide;
        naturalToSide ??= toSide;
        var midX = (start.X + end.X) / 2;
        var laneGapY = StrictEquals(Lane(from), Lane(to)) ? lanes.LaneTop(Lane(from)) - 16 : GapYBetween(Lane(from), Lane(to), edge.Num("bias") ?? 0.5);
        var topY = Math.Max(8, Math.Min(lanes.LaneTop(Lane(from)), lanes.LaneTop(Lane(to))) - 16);
        var bottomY = Math.Max(lanes.LaneTop(Lane(from)) + lanes.LaneHeight(Lane(from)), lanes.LaneTop(Lane(to)) + lanes.LaneHeight(Lane(to))) + 16;
        var outsideLeft = layout.LaneX - 20;
        var outsideRight = layout.LaneX + layout.LaneW + 12;
        var families = new List<(string Family, List<Pt> Via)>
        {
            ("facing-straight", []),
            ("horizontal-then-vertical", [new(end.X, start.Y)]),
            ("vertical-then-horizontal", [new(start.X, end.Y)]),
            ("lane-gap-corridor", CorridorViaY(start, end, fromSide, toSide, laneGapY)),
            ("column-gap-corridor", CorridorViaX(start, end, fromSide, toSide, midX)),
            ("outside-left", CorridorViaX(start, end, fromSide, toSide, outsideLeft)),
            ("outside-right", CorridorViaX(start, end, fromSide, toSide, outsideRight)),
            ("top-corridor", CorridorViaY(start, end, fromSide, toSide, topY)),
            ("bottom-corridor", CorridorViaY(start, end, fromSide, toSide, bottomY)),
        };
        // Offset tracks let several routes through one gap run side by side.
        foreach (var offset in (double[])[-12, 12, -24, 24])
        {
            families.Add(("lane-gap-corridor", CorridorViaY(start, end, fromSide, toSide, laneGapY + offset)));
            families.Add(("column-gap-corridor", CorridorViaX(start, end, fromSide, toSide, midX + offset)));
        }
        // Failed-route diagnostics also inspect rejected candidates. Keep their
        // complete geometry before filtering for feasible routes.
        var rawCandidates = families.Select(candidate => new Plan
        {
            Family = candidate.Family, Via = candidate.Via, Points = Geometry.JoinRoutePoints(start, candidate.Via, end), FromSide = fromSide, ToSide = toSide,
        }).ToList();
        // `.filter(feasible).map(cost)`: every feasibility check runs before the first cost.
        var feasible = new List<(int Ordinal, Plan Raw)>();
        for (var ordinal = 0; ordinal < rawCandidates.Count; ordinal++)
        {
            if (ReadableCandidateIsFeasible(edge, rawCandidates[ordinal].Points, from, to, fromSide, toSide)) feasible.Add((ordinal, rawCandidates[ordinal]));
        }
        var candidates = feasible.Select(entry => entry.Raw.With(entry.Raw.Points,
            ReadableCandidateCost(edge, entry.Raw.Points, ordinalOffset + entry.Ordinal, naturalFromSide, naturalToSide))).ToList();
        Js.Sort(candidates, (left, right) => CompareCost(left.Cost, right.Cost));
        return new(rawCandidates, candidates, outsideRight);
    }

    // ---------------------------------------------------------------- K2: readableAutomaticVia (:4042-4203)
    List<Pt> ReadableAutomaticVia(JsonObject edge, Box from, Box to, Pt start, Pt end, string fromSide, string toSide)
    {
        var (rawCandidates, candidates, outsideRight) = ReadableAutomaticCandidateSet(edge, from, to, start, end, fromSide, toSide);
        if (candidates.Count > 0) return candidates[0].Points[1..^1];
        var outsideRightCandidate = rawCandidates.FirstOrDefault(candidate => candidate.Family == "outside-right");
        if (outsideRightCandidate is not null)
        {
            var currentPoints = Geometry.NormalizeRoutePoints([start, .. outsideRightCandidate.Via!, end]);
            var labelRect = CandidateLabelRect(edge, currentPoints);
            var outsideRightMinX = outsideRight;
            if (labelRect is { } label)
            {
                // Only obstacles whose box can reach the label rect can push the corridor.
                var labelQuery = new GridBox(label.X - 4, label.X + label.Width + 4, label.Y - 4, label.Y + label.Height + 4);
                foreach (var item in obstacleGrid.Query(labelQuery))
                {
                    if (item.Kind == "node")
                    {
                        if (!Geometry.RectsOverlap(label, item.Node!, -2)) continue;
                        var rightwardLabelDeficit = item.Node!.X + item.Node.Width - 2 - label.X;
                        if (rightwardLabelDeficit > 0) outsideRightMinX = Math.Max(outsideRightMinX, outsideRight + rightwardLabelDeficit * 2);
                    }
                    else if (item.Kind == "label")
                    {
                        if (!Geometry.RectsOverlap(label, item.Rect!, -2)) continue;
                        var rightwardLabelDeficit = item.Rect!.X + item.Rect.Width - 2 - label.X;
                        if (rightwardLabelDeficit > 0) outsideRightMinX = Math.Max(outsideRightMinX, outsideRight + rightwardLabelDeficit * 2);
                    }
                    else if (item.Kind == "route")
                    {
                        var otherPoints = item.Routed!.Points;
                        for (var index = 0; index < otherPoints.Count - 1; index++)
                        {
                            var clearance = Geometry.SegmentRectClearance(new(otherPoints[index], otherPoints[index + 1]), label);
                            if (clearance is null || clearance + 0.0001 >= 4) continue;
                            var rightwardLabelDeficit = Math.Max(otherPoints[index].X, otherPoints[index + 1].X) + 4 - label.X;
                            if (rightwardLabelDeficit > 0) outsideRightMinX = Math.Max(outsideRightMinX, outsideRight + rightwardLabelDeficit * 2);
                        }
                    }
                }
            }
            outsideRightMinX = Math.Ceiling(outsideRightMinX * 1000) / 1000;
            var rightmostPlacedX = Math.Max(outsideRight, Math.Max(rightmostNodeEdge, rightmostRoutedEdge));
            var probeGrowth = Math.Max(32, Math.Max(labelRect?.Width ?? 0, rightmostPlacedX + 16 - outsideRightMinX));
            var lastInfeasibleX = outsideRight;
            for (var probe = 0; probe < 7; probe++)
            {
                if (outsideRightMinX > outsideRight + 0.0001)
                {
                    var expandedPoints = Geometry.NormalizeRoutePoints([start, .. CorridorViaX(start, end, fromSide, toSide, outsideRightMinX), end]);
                    if (ReadableCandidateIsFeasible(edge, expandedPoints, from, to, fromSide, toSide))
                    {
                        var feasibleX = outsideRightMinX;
                        var feasiblePoints = expandedPoints;
                        var infeasibleX = lastInfeasibleX;
                        for (var refinement = 0; refinement < 53 && feasibleX - infeasibleX > 0.001; refinement++)
                        {
                            var midpointX = Math.Ceiling((infeasibleX + feasibleX) / 2 * 1000) / 1000;
                            if (midpointX >= feasibleX - 0.0001) break;
                            var midpointPoints = Geometry.NormalizeRoutePoints([start, .. CorridorViaX(start, end, fromSide, toSide, midpointX), end]);
                            if (ReadableCandidateIsFeasible(edge, midpointPoints, from, to, fromSide, toSide))
                            {
                                feasibleX = midpointX;
                                feasiblePoints = midpointPoints;
                            }
                            else
                            {
                                infeasibleX = midpointX;
                            }
                        }
                        return feasiblePoints[1..^1];
                    }
                    lastInfeasibleX = outsideRightMinX;
                }
                outsideRightMinX = Math.Ceiling((outsideRightMinX + probeGrowth) * 1000) / 1000;
                probeGrowth *= 2;
            }
        }
        var hasRelevantAbsolutePin = edge["labelAt"] is JsonArray
            || pathCache.Keys.Any(otherEdge => otherEdge["labelAt"] is JsonArray || HasAbsoluteRoutePins(otherEdge));
        if (hasRelevantAbsolutePin) ClassifyFailedAutomaticCandidatePins(edge, rawCandidates);
        var families = new JsonArray([.. rawCandidates.Select(candidate => (JsonNode?)candidate.Family)]);
        var horizontallyFacing = (fromSide == "right" && toSide == "left" && end.X > start.X)
            || (fromSide == "left" && toSide == "right" && start.X > end.X);
        if (horizontallyFacing && Col(from) != Col(to))
        {
            var fromCol = Math.Min(Col(from), Col(to));
            var toCol = Math.Max(Col(from), Col(to));
            var requiredRankGap = from.Width / 2 + 32 + to.Width / 2;
            var actualRankGap = ColAt(layout.ColXs, toCol) - ColAt(layout.ColXs, fromCol);
            if (actualRankGap + 0.0001 < requiredRankGap)
            {
                throw new WorkflowLayoutFeedback(new JsonObject
                {
                    ["kind"] = "rank-gap-minimum", ["fromCol"] = fromCol, ["toCol"] = toCol, ["minimum"] = Math.Ceiling(requiredRankGap * 1000) / 1000,
                    ["edge"] = edge["id"]?.DeepClone(), ["from"] = edge["from"]?.DeepClone(), ["to"] = edge["to"]?.DeepClone(),
                    ["attemptedCandidateFamilies"] = families, ["candidateCount"] = rawCandidates.Count,
                });
            }
        }
        if (!StrictEquals(Lane(from), Lane(to)) && layout.LaneGap < 32)
        {
            throw new WorkflowLayoutFeedback(new JsonObject
            {
                ["kind"] = "lane-gap-minimum", ["minimum"] = 32,
                ["edge"] = edge["id"]?.DeepClone(), ["from"] = edge["from"]?.DeepClone(), ["to"] = edge["to"]?.DeepClone(),
                ["attemptedCandidateFamilies"] = families, ["candidateCount"] = rawCandidates.Count,
            });
        }
        var message = $"Workflow edge \"{WorkflowEdgeName(edge)}\" has no feasible readable-v2 automatic route.";
        throw ctx.Diagnostics.ThrowDiagnosticError(message, [new Diagnostic("workflow/solver-budget-exhausted", Severity.Error, message,
            new JsonObject { ["diagramType"] = "workflow", ["edge"] = edge["id"]?.DeepClone(), ["from"] = edge["from"]?.DeepClone(), ["to"] = edge["to"]?.DeepClone() },
            new JsonObject { ["attemptedCandidateFamilies"] = families, ["candidateCount"] = rawCandidates.Count },
            [], null)]);
    }

    // ---------------------------------------------------------------- K5: ports, endpoint repairs, detours, free space (:4470-4691)
    List<Pt> AutomaticPortCandidates(JsonObject edge, Box node, string side, Pt preferred, Box counterpart)
    {
        if (!IndependentAutomaticRoute(edge)) return [preferred];
        var verticalSide = side is "left" or "right";
        var axis = verticalSide ? 1 : 0;
        var center = Geometry.Anchor(node, side);
        var occupied = new List<(double Coordinate, double Clearance)>();
        foreach (var (other, routed) in nodeRoutes.Get(node.Id)?.Entries ?? [])
        {
            foreach (var endpoint in (string[])["from", "to"])
            {
                if (!StrictEquals(other[endpoint], node.Id)) continue;
                var point = endpoint == "from" ? routed.Points[0] : routed.Points[^1];
                var ownWidth = StrokeWidth(edge);
                var otherWidth = StrokeWidth(other);
                if (Math.Abs(Ax(point, 1 - axis) - Ax(center, 1 - axis)) < 0.0001)
                    occupied.Add((Ax(point, axis), Math.Max(16, 3.5 * (ownWidth + otherWidth))));
            }
        }
        bool Clear(Pt point) => occupied.All(entry => Math.Abs(entry.Coordinate - Ax(point, axis)) >= entry.Clearance - 0.0001);
        if (Clear(preferred)) return [preferred];
        var candidates = new List<Pt>();
        var extent = verticalSide ? node.Height : node.Width;
        var direction = (verticalSide ? counterpart.Cy : counterpart.Cx) >= Ax(center, axis) ? 1 : -1;
        for (double offset = 0; offset <= extent / 2 - 16; offset += 12)
        {
            foreach (var sign in (int[])[direction, -direction])
            {
                var point = Shift(center, axis, sign * offset);
                if (Clear(point) && !candidates.Any(candidate => Ax(candidate, axis) == Ax(point, axis))) candidates.Add(point);
            }
            if (candidates.Count >= 2) return candidates[..2];
        }
        return candidates.Count > 0 ? candidates : [preferred];
    }

    List<Plan> AutomaticEndpointRepairs(JsonObject edge, Plan selected, Box from, Box to, string naturalFromSide, string naturalToSide, bool narrowSlots = false)
    {
        if (!IndependentAutomaticRoute(edge)
            || HasAny(edge, "labelDx", "labelDy", "labelSegment", "bias")
            || selected.Cost.ProperCrossingCount != 0
            || (!WorkflowModel.Truthy(selected.Cost.SharedCorridorPx) && !WorkflowModel.Truthy(selected.Cost.UnrelatedBendContactCount))
            || selected.Points.Count < 3) return [];
        List<CorridorHit> CorridorHits(List<Pt> points)
        {
            var hits = new List<CorridorHit>();
            foreach (var (relation, routed) in pathCache.Entries.ToList())
            {
                hits.AddRange(Geometry.CollectAmbiguousCorridors([new RoutedRelation(points, edge), new RoutedRelation(routed.Points, relation)],
                    includeSharedEndpoints: (_, _) => true, allowShortWorkflowTrunks: true));
            }
            return hits;
        }
        var originalHits = CorridorHits(selected.Points);
        var originalNeighbours = originalHits.Select(hit => hit.Right.Relation).ToList();
        var contacts = new List<Pt>();
        if (WorkflowModel.Truthy(selected.Cost.UnrelatedBendContactCount))
        {
            foreach (var (other, routed) in pathCache.Entries.ToList())
                if (!SharesEndpoint(edge, other)) contacts.AddRange(Geometry.PolylineBendContacts(selected.Points, routed.Points));
        }
        var ownArrow = ArrowRelation(edge);
        var incoming = (nodeRoutes.Get(IdKey(edge["to"]))?.Entries ?? []).Select(entry => new RoutedRelation(entry.Value.Points, ArrowRelation(entry.Key))).ToList();
        var repairs = new List<Plan>();
        foreach (var endpoint in (string[])["from", "to"])
        {
            var first = endpoint == "from";
            var endpointIndex = first ? 0 : selected.Points.Count - 1;
            var segmentIndex = first ? 0 : selected.Points.Count - 2;
            var corner = selected.Points[first ? 1 : selected.Points.Count - 2];
            var touchingCorner = contacts.Any(point => Js.Hypot(point.X - corner.X, point.Y - corner.Y) <= 0.0001);
            if (!touchingCorner && !originalHits.Any(hit => hit.LeftSegment == segmentIndex)) continue;
            var node = first ? from : to;
            var side = first ? selected.FromSide : selected.ToSide;
            var axis = side is "left" or "right" ? 1 : 0;
            var minimum = (axis == 1 ? node.Y : node.X) + 16;
            var maximum = (axis == 1 ? node.Y + node.Height : node.X + node.Width) - 16;
            // Keep the side, its perpendicular stub, and the existing corner gutter.
            // Small slides can separate source strokes or a source from an arrow even
            // when the normal 12px port-spread step cannot fit on a 52px node side.
            var offsets = new List<double> { -8, 8, -10, 10 };
            if (narrowSlots)
            {
                var ownFootprint = (first ? 0.5 : 3.5) * StrokeWidth(edge);
                var slots = new List<(double Start, double End)> { (minimum, maximum) };
                foreach (var (relation, routed) in nodeRoutes.Get(node.Id)?.Entries ?? [])
                {
                    foreach (var otherEndpoint in (string[])["from", "to"])
                    {
                        if (!StrictEquals(relation[otherEndpoint], node.Id)) continue;
                        var point = otherEndpoint == "from" ? routed.Points[0] : routed.Points[^1];
                        if (Math.Abs(Ax(point, 1 - axis) - Ax(selected.Points[endpointIndex], 1 - axis)) > 0.0001) continue;
                        var radius = ownFootprint + (otherEndpoint == "from" ? 0.5 : 3.5) * StrokeWidth(relation);
                        var low = Ax(point, axis) - radius;
                        var high = Ax(point, axis) + radius;
                        var next = new List<(double Start, double End)>();
                        foreach (var slot in slots)
                        {
                            if (high <= slot.Start || low >= slot.End) { next.Add(slot); continue; }
                            if (Math.Min(slot.End, low) > slot.Start) next.Add((slot.Start, Math.Min(slot.End, low)));
                            if (slot.End > Math.Max(slot.Start, high)) next.Add((Math.Max(slot.Start, high), slot.End));
                        }
                        slots = next;
                    }
                }
                var slotOffsets = slots.Select(slot => (slot.Start + slot.End) / 2 - Ax(selected.Points[endpointIndex], axis)).ToList();
                Js.Sort(slotOffsets, (a, b) => Js.Or(Math.Abs(a) - Math.Abs(b), a - b));
                offsets.AddRange(slotOffsets.Take(4));
            }
            foreach (var offset in offsets)
            {
                var points = new List<Pt>(selected.Points);
                points[endpointIndex] = Shift(points[endpointIndex], axis, offset);
                var cornerIndex = first ? 1 : points.Count - 2;
                points[cornerIndex] = Shift(points[cornerIndex], axis, offset);
                if (Ax(points[endpointIndex], axis) < minimum || Ax(points[endpointIndex], axis) > maximum) continue;
                var endpointFootprint = (first ? 0.5 : 3.5) * StrokeWidth(edge);
                var clearPort = (nodeRoutes.Get(node.Id)?.Entries ?? []).All(entry => ((string[])["from", "to"]).All(otherEndpoint =>
                {
                    if (!StrictEquals(entry.Key[otherEndpoint], node.Id)) return true;
                    var point = otherEndpoint == "from" ? entry.Value.Points[0] : entry.Value.Points[^1];
                    if (Math.Abs(Ax(point, 1 - axis) - Ax(points[endpointIndex], 1 - axis)) > 0.0001) return true;
                    var otherFootprint = (otherEndpoint == "from" ? 0.5 : 3.5) * StrokeWidth(entry.Key);
                    return Math.Abs(Ax(point, axis) - Ax(points[endpointIndex], axis)) + 0.0001 >= endpointFootprint + otherFootprint;
                }));
                if (!clearPort) continue;
                if (!ReadableCandidateIsFeasible(edge, points, from, to, selected.FromSide, selected.ToSide)) continue;
                var cost = ReadableCandidateCost(edge, points, selected.Cost.StableCandidateOrdinal, naturalFromSide, naturalToSide);
                if (cost.ProperCrossingCount != 0
                    || cost.UnrelatedBendContactCount > selected.Cost.UnrelatedBendContactCount
                    || cost.SharedCorridorPx > selected.Cost.SharedCorridorPx
                    || (cost.SharedCorridorPx == selected.Cost.SharedCorridorPx && cost.UnrelatedBendContactCount == selected.Cost.UnrelatedBendContactCount)) continue;
                if (CorridorHits(points).Any(hit => !originalNeighbours.Any(neighbour => ReferenceEquals(neighbour, hit.Right.Relation)))) continue;
                if (Geometry.CollectArrowheadCollisions([new RoutedRelation(points, ownArrow), .. incoming], allowShortWorkflowTrunks: true)
                    .Any(hit => ReferenceEquals(hit.Left.Relation, ownArrow) || ReferenceEquals(hit.Right.Relation, ownArrow))) continue;
                repairs.Add(selected.With(points, cost));
            }
        }
        return repairs;
    }

    // The fixed center/offset tracks can miss the open space beside a column.
    // Only expand a severe vertical detour: try at most 24 nearby node-side tracks,
    // preserving the existing feasibility, port and interaction checks.
    List<Plan> ReadableInternalDetourPlans(JsonObject edge, Plan selected, Box from, Box to, string naturalFromSide, string naturalToSide)
    {
        if (!IndependentAutomaticRoute(edge) || Col(from) != Col(to)) return [];
        double length = 0;
        for (var index = 1; index < selected.Points.Count; index++)
            length += Math.Abs(selected.Points[index].X - selected.Points[index - 1].X) + Math.Abs(selected.Points[index].Y - selected.Points[index - 1].Y);
        var direct = Math.Abs(to.Cx - from.Cx) + Math.Abs(to.Cy - from.Cy);
        if (length <= direct * 2 || length - direct <= 64) return [];
        var down = to.Cy > from.Cy;
        var fromSide = down ? "bottom" : "top";
        var toSide = down ? "top" : "bottom";
        var starts = AutomaticPortCandidates(edge, from, fromSide, Geometry.Anchor(from, fromSide), to);
        var ends = AutomaticPortCandidates(edge, to, toSide, Geometry.Anchor(to, toSide), from);
        var midX = (from.Cx + to.Cx) / 2;
        var lowY = Math.Min(from.Cy, to.Cy);
        var highY = Math.Max(from.Cy, to.Cy);
        var trackSet = new JsSet<double>();
        foreach (var node in nodes.Values)
        {
            if (!(node.Y <= highY && node.Y + node.Height >= lowY)) continue;
            trackSet.Add(node.X - 8);
            trackSet.Add(node.X + node.Width + 8);
        }
        var tracks = trackSet.Where(x => x >= layout.LaneX + 4 && x <= layout.LaneX + layout.LaneW - 4).ToList();
        Js.Sort(tracks, (a, b) => Js.Or(Math.Abs(a - midX) - Math.Abs(b - midX), a - b));
        if (tracks.Count > 24) tracks.RemoveRange(24, tracks.Count - 24);
        var plans = new List<Plan>();
        foreach (var start in starts)
        {
            foreach (var end in ends)
            {
                foreach (var x in tracks)
                {
                    var points = Geometry.JoinRoutePoints(start, CorridorViaX(start, end, fromSide, toSide, x), end);
                    if (!ReadableCandidateIsFeasible(edge, points, from, to, fromSide, toSide)) continue;
                    plans.Add(new Plan
                    {
                        Points = points, FromSide = fromSide, ToSide = toSide, Family = "internal-detour",
                        Cost = ReadableCandidateCost(edge, points, 10000 + plans.Count, naturalFromSide, naturalToSide),
                    });
                }
            }
        }
        return plans;
    }

    // Fixed offset tracks can miss a free corridor around several occupied rows.
    // Search only after the cheaper automatic plans still collide; pins retain
    // their existing paths. Four side pairs and explicit grid budgets bound work.
    List<Plan> ReadableFreeSpacePlans(JsonObject edge, Plan selected, Box from, Box to, string naturalFromSide, string naturalToSide)
    {
        if (!IndependentAutomaticRoute(edge) || StrictEquals(Lane(from), Lane(to))
            || HasAny(edge, "labelDx", "labelDy", "labelSegment", "bias")
            || (!WorkflowModel.Truthy(selected.Cost.SharedCorridorPx) && !WorkflowModel.Truthy(selected.Cost.ProperCrossingCount) && !WorkflowModel.Truthy(selected.Cost.UnrelatedBendContactCount))) return [];
        var obstacles = new List<Rect>();
        void Add(double x, double y, double width, double height, double padding) =>
            obstacles.Add(new(x - padding, y - padding, width + padding * 2, height + padding * 2));
        foreach (var node in nodes.Values) Add(node.X, node.Y, node.Width, node.Height, 8);
        SceneLabelObstacles();
        foreach (var item in obstacleGrid.Query(new GridBox(0, layout.LaneX + layout.LaneW + 64, 0, lanes.AutoHeight + 64)))
        {
            if (item.Kind is "label" or "scene")
            {
                var rect = item.Rect is { } labelRect ? (Rect)labelRect : RectOf(item.Obstacle!);
                Add(rect.X, rect.Y, rect.Width, rect.Height, 4);
            }
            else if (item.Kind == "route" && !ReferenceEquals(item.Edge, edge))
            {
                var routePoints = item.Routed!.Points;
                for (var index = 1; index < routePoints.Count; index++)
                {
                    var a = routePoints[index - 1];
                    var b = routePoints[index];
                    Add(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y), 8);
                }
            }
        }
        var down = to.Cy > from.Cy;
        Sides[] pairs = [new(down ? "bottom" : "top", down ? "top" : "bottom"), new("left", "left"), new("right", "right"), new(selected.FromSide, selected.ToSide)];
        var plans = new List<Plan>();
        var seen = new JsSet<string>();
        for (var ordinal = 0; ordinal < pairs.Length; ordinal++)
        {
            var (fromSide, toSide) = pairs[ordinal];
            var key = $"{fromSide}:{toSide}";
            if (seen.Has(key)) continue;
            seen.Add(key);
            var start = AutomaticPortCandidates(edge, from, fromSide, Geometry.Anchor(from, fromSide), to)[0];
            var end = AutomaticPortCandidates(edge, to, toSide, Geometry.Anchor(to, toSide), from)[0];
            var pairOrdinal = ordinal;
            var candidate = RouteQuality.ShortestOrthogonalGridRoute(start, end, selected.Points, obstacles, fromSide, toSide,
                new GridRouteOptions(Clearance: 0, MaximumObstacleCount: 200, EndpointStubPx: 16, MaximumGridNodes: 25600,
                    // Keep nearby coordinate candidates: coalescing can erase a whole gap.
                    // Workflow's feasibility check still rejects short or reversing segments.
                    MinimumSegmentPx: 0, BendPenaltyPx: 24,
                    Accept: points =>
                    {
                        if (!ReadableCandidateIsFeasible(edge, points, from, to, fromSide, toSide)) return false;
                        var cost = ReadableCandidateCost(edge, points, 20000 + pairOrdinal, naturalFromSide, naturalToSide);
                        return !WorkflowModel.Truthy(cost.ProperCrossingCount) && !WorkflowModel.Truthy(cost.UnrelatedBendContactCount)
                            && !WorkflowModel.Truthy(cost.SharedCorridorPx) && !WorkflowModel.Truthy(cost.LabelRouteClearanceDeficit);
                    }));
            if (candidate is null) continue;
            plans.Add(new Plan
            {
                Points = candidate.Points, FromSide = fromSide, ToSide = toSide, Family = "free-space",
                Cost = ReadableCandidateCost(edge, candidate.Points, 20000 + ordinal, naturalFromSide, naturalToSide),
            });
        }
        return plans;
    }

    // ---------------------------------------------------------------- K6: readableAutomaticRoute (:4693-4934)
    Plan ReadableAutomaticRoute(JsonObject edge, Box from, Box to, Sides primarySides, SpreadPorts? primaryPorts)
    {
        var authoredFrom = AuthoredSide(edge, "fromSide");
        var authoredTo = AuthoredSide(edge, "toSide");
        var sidePairs = new List<Sides> { primarySides };
        foreach (var fromSide in authoredFrom is not null ? [authoredFrom] : SideOrder)
            foreach (var toSide in authoredTo is not null ? [authoredTo] : SideOrder)
                sidePairs.Add(new(fromSide, toSide));

        var naturalFromSide = authoredFrom ?? Geometry.LegacyDefaultFromSide(from, to);
        var naturalToSide = authoredTo ?? Geometry.LegacyDefaultToSide(from, to);
        var seen = new JsSet<string>();
        var plans = new List<Plan>();
        var feedback = new List<(WorkflowLayoutFeedback Error, int PairOrdinal)>();
        Exception? firstFailure = null;
        // Spreading ports for provisional sides can offset an otherwise clear
        // facing pair. Keep the aligned route in the same feasibility/cost contest.
        // Error and return roles do not imply a detour when that corridor is clear.
        var alignedStart = AutomaticPortCandidates(edge, from, naturalFromSide, Geometry.Anchor(from, naturalFromSide), to)[0];
        var alignedEnd = AutomaticPortCandidates(edge, to, naturalToSide, Geometry.Anchor(to, naturalToSide), from)[0];
        List<Pt> alignedPoints = [alignedStart, alignedEnd];
        if (!edge["fromSide"].Truthy() && !edge["toSide"].Truthy() && !edge["labelAt"].Truthy()
            && ReadableCandidateIsFeasible(edge, alignedPoints, from, to, naturalFromSide, naturalToSide))
        {
            plans.Add(new Plan
            {
                Family = "aligned-facing", Points = alignedPoints, FromSide = naturalFromSide, ToSide = naturalToSide,
                Cost = ReadableCandidateCost(edge, alignedPoints, -1, naturalFromSide, naturalToSide),
            });
        }
        foreach (var candidateSides in sidePairs)
        {
            if (authoredFrom is not null && candidateSides.FromSide != authoredFrom) continue;
            if (authoredTo is not null && candidateSides.ToSide != authoredTo) continue;
            var key = $"{candidateSides.FromSide}:{candidateSides.ToSide}";
            if (seen.Has(key)) continue;
            var pairOrdinal = seen.Count;
            seen.Add(key);
            var primary = pairOrdinal == 0;
            var preferredStart = primaryPorts?.From is { } portFrom && primary ? portFrom : Geometry.Anchor(from, candidateSides.FromSide);
            var preferredEnd = primaryPorts?.To is { } portTo && primary ? portTo : Geometry.Anchor(to, candidateSides.ToSide);
            var starts = AutomaticPortCandidates(edge, from, candidateSides.FromSide, preferredStart, to);
            var ends = AutomaticPortCandidates(edge, to, candidateSides.ToSide, preferredEnd, from);
            var start = starts[0];
            var end = ends[0];
            var planned = ReadableAutomaticCandidateSet(edge, from, to, start, end, candidateSides.FromSide, candidateSides.ToSide,
                pairOrdinal * 9, naturalFromSide, naturalToSide);
            plans.AddRange(planned.Candidates);
            var portOrdinal = 0;
            for (var i = 0; i < starts.Count; i++)
            {
                for (var j = 0; j < ends.Count; j++)
                {
                    // `alternativeStart === start && alternativeEnd === end`: index identity (R9).
                    if (i == 0 && j == 0) continue;
                    portOrdinal += 1;
                    var alternativeStart = starts[i];
                    var alternativeEnd = ends[j];
                    var alternative = ReadableAutomaticCandidateSet(edge, from, to, alternativeStart, alternativeEnd,
                        candidateSides.FromSide, candidateSides.ToSide, pairOrdinal * 9 + portOrdinal * 144, naturalFromSide, naturalToSide);
                    plans.AddRange(alternative.Candidates);
                    if (alternative.Candidates.Count > 0) continue;
                    try
                    {
                        var via = ctx.Diagnostics.WithRecordingSuppressed(() => ReadableAutomaticVia(
                            edge, from, to, alternativeStart, alternativeEnd, candidateSides.FromSide, candidateSides.ToSide));
                        var points = Geometry.JoinRoutePoints(alternativeStart, via, alternativeEnd);
                        plans.Add(new Plan
                        {
                            Points = points, FromSide = candidateSides.FromSide, ToSide = candidateSides.ToSide,
                            Cost = ReadableCandidateCost(edge, points, pairOrdinal * 9 + portOrdinal * 144 + 6, naturalFromSide, naturalToSide),
                        });
                    }
                    // R10: every non-feedback error, a C# bug included, is a candidate failure here (:4784-4787).
                    catch (WorkflowLayoutFeedback error)
                    {
                        feedback.Add((error, pairOrdinal));
                    }
                    catch (Exception error)
                    {
                        firstFailure ??= error;
                    }
                }
            }
            if (planned.Candidates.Count > 0) continue;
            try
            {
                var expandedVia = ctx.Diagnostics.WithRecordingSuppressed(() => ReadableAutomaticVia(
                    edge, from, to, start, end, candidateSides.FromSide, candidateSides.ToSide));
                var expandedPoints = Geometry.JoinRoutePoints(start, expandedVia, end);
                var outsideRightOrdinal = planned.RawCandidates.FindIndex(candidate => candidate.Family == "outside-right");
                var ordinal = pairOrdinal * 9 + Math.Max(0, outsideRightOrdinal);
                plans.Add(new Plan
                {
                    Family = "outside-right", Points = expandedPoints, FromSide = candidateSides.FromSide, ToSide = candidateSides.ToSide,
                    Cost = ReadableCandidateCost(edge, expandedPoints, ordinal, naturalFromSide, naturalToSide),
                });
            }
            catch (WorkflowLayoutFeedback error)
            {
                feedback.Add((error, pairOrdinal));
            }
            catch (Exception error)
            {
                firstFailure ??= error;
            }
        }

        Js.Sort(plans, (left, right) => CompareCost(left.Cost, right.Cost));
        if (plans.Count > 0)
        {
            // Before paying for a long outside detour, try the existing bounded port
            // slides on up to four otherwise viable plans with a false bend junction.
            // Pins never enter this repair; every slide keeps route and label checks.
            var touchingPlans = plans.Where(plan => WorkflowModel.Truthy(plan.Cost.UnrelatedBendContactCount) && !WorkflowModel.Truthy(plan.Cost.ProperCrossingCount)).Take(4).ToList();
            foreach (var plan in touchingPlans) plans.AddRange(AutomaticEndpointRepairs(edge, plan, from, to, naturalFromSide, naturalToSide));
            Js.Sort(plans, (left, right) => CompareCost(left.Cost, right.Cost));
            plans.AddRange(ReadableInternalDetourPlans(edge, plans[0], from, to, naturalFromSide, naturalToSide));
            Js.Sort(plans, (a, b) => CompareCost(a.Cost, b.Cost));
            var endpointRepairs = AutomaticEndpointRepairs(edge, plans[0], from, to, naturalFromSide, naturalToSide);
            // `String(point)` equality: Pt value equality ("x,y").
            var repairSeeds = plans[0].Cost.SharedCorridorPx > 0
                ? plans.Where((plan, index) => plans.FindIndex(other => other.FromSide == plan.FromSide && other.ToSide == plan.ToSide
                    && other.Points[0] == plan.Points[0] && other.Points[^1] == plan.Points[^1]) == index).Take(8).ToList()
                : [];
            // A crowded fan-out can exhaust the fixed corridor coordinates even with
            // distinct ports. Repair only interior segments of this automatic route;
            // One pass over at most eight seeds and four offsets bounds the search.
            foreach (var selected in repairSeeds)
            {
                if (selected.Cost.SharedCorridorPx == 0) continue;
                var alternatives = new List<Plan>();
                for (var segment = 1; segment < selected.Points.Count - 2; segment++)
                {
                    var a = selected.Points[segment];
                    var b = selected.Points[segment + 1];
                    var axis = a.X == b.X ? 0 : 1;
                    foreach (var offset in (double[])[-16, 16, -32, 32])
                    {
                        var points = new List<Pt>(selected.Points);
                        points[segment] = Shift(points[segment], axis, offset);
                        points[segment + 1] = Shift(points[segment + 1], axis, offset);
                        if (!ReadableCandidateIsFeasible(edge, points, from, to, selected.FromSide, selected.ToSide)) continue;
                        var cost = ReadableCandidateCost(edge, points, selected.Cost.StableCandidateOrdinal, naturalFromSide, naturalToSide);
                        if (CompareCost(cost, selected.Cost) < 0) alternatives.Add(selected.With(points, cost));
                    }
                }
                Js.Sort(alternatives, (a, b) => CompareCost(a.Cost, b.Cost));
                if (alternatives.Count > 0) plans.Add(alternatives[0]);
            }
            plans.AddRange(endpointRepairs);
            Js.Sort(plans, (a, b) => CompareCost(a.Cost, b.Cost));
            // A corner repair can expose a usable port slot that the original route
            // could not reach. Try at most four measured gaps per endpoint, retaining
            // stroke/arrow footprints and every route/label feasibility check.
            if (plans[0].Cost.SharedCorridorPx > 0)
            {
                plans.AddRange(AutomaticEndpointRepairs(edge, plans[0], from, to, naturalFromSide, naturalToSide, true));
                Js.Sort(plans, (a, b) => CompareCost(a.Cost, b.Cost));
            }
            plans.AddRange(ReadableFreeSpacePlans(edge, plans[0], from, to, naturalFromSide, naturalToSide));
            Js.Sort(plans, (a, b) => CompareCost(a.Cost, b.Cost));
            var best = plans[0];
            return new Plan { Points = best.Points, FromSide = best.FromSide, ToSide = best.ToSide };
        }

        static double FeedbackPriority(WorkflowLayoutFeedback error) => error.Kind switch { "rank-gap-minimum" => 0, "lane-gap-minimum" => 1, _ => 99 };
        Js.Sort(feedback, (left, right) => Js.Or(FeedbackPriority(left.Error) - FeedbackPriority(right.Error), left.PairOrdinal - right.PairOrdinal));
        if (feedback.Count > 0) throw feedback[0].Error;
        List<string> authoredSideFields = [.. authoredFrom is not null ? ["fromSide"] : Array.Empty<string>(), .. authoredTo is not null ? ["toSide"] : Array.Empty<string>()];
        if (authoredSideFields.Count > 0) throw AuthoredSideRouteConflict(edge, from, to, primarySides, primaryPorts, authoredSideFields, firstFailure);
        if (firstFailure is not null) throw firstFailure;
        throw new InvalidOperationException("readable-v2 automatic route enumeration produced no result");
    }
}
