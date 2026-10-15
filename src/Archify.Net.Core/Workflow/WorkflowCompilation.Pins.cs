using System.Text.Json.Nodes;
using static Archify.Net.WorkflowModel;

namespace Archify.Net;

// workflow-compiler.mjs pin and preset conflicts: H1 fix verifiers and conflict throwers (:1641-2010, the pure predicates are in
// WorkflowCompilation.Routing.cs), the per-edge pin checks of H5 validateReadablePinnedGeometry (:2422-2688), J3
// placedLabelConflictEvidence / readablePresetRejection (:3516-3545, :3708-3748), K3 readablePresetVia (:4205-4303) and K7
// readableControlledRoute (:4945-5057). Every thrower raises `workflow/explicit-pin-conflict` (or `route-preset-conflict`)
// with fixes verified by acceptsFix recompiles; `subject.path` exists only when exactly one conflicting pin remains (:1849-1855).
internal sealed partial class WorkflowCompilation
{
    // `{ edge, edgeIndex, field }`: edgeIndex is the canonical index the fix mutators address.
    internal sealed record PinRef(JsonObject Edge, int EdgeIndex, string Field);
    internal sealed record PinRepair(List<PinRef> RemovalSet, string Message);
    internal sealed record PinAlternatives(List<List<PinRef>> RemovalSets, List<PinRef> ConflictingRefs, List<JsonObject> ConflictingPins,
        List<PinRepair> Repairs, List<string> SupportedFixes);

    // `workflow.edges.indexOf(edge)`: identity on the canonical list (R9).
    int CanonicalIndex(JsonObject? edge) => edge is null ? -1 : workflow.Edges.FindIndex(candidate => ReferenceEquals(candidate, edge));
    static JsonArray PointsJson(IEnumerable<Pt> points) => new([.. points.Select(point => (JsonNode)PtJson(point))]);
    static JsonObject RectJson(Rect rect) => new() { ["x"] = rect.X, ["y"] = rect.Y, ["width"] = rect.Width, ["height"] = rect.Height };
    static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(value => (JsonNode)value)]);

    // ---------------------------------------------------------------- H1 fix verifiers (:1641-1845)
    string? VerifiedEdgeFix(JsonObject edge, string message, Action<JsonObject, WorkflowDoc> mutator)
    {
        var edgeIndex = CanonicalIndex(edge);
        if (edgeIndex < 0) return null;
        var accepted = AcceptsFix(document => mutator(document.Edges[edgeIndex], document));
        return accepted ? message : null;
    }

    string? VerifiedAutomaticRouteFix(JsonObject edge, bool clearSides = false)
    {
        var edgeName = WorkflowEdgeName(edge);
        return VerifiedEdgeFix(edge,
            clearSides
                ? $"remove explicit route geometry and endpoint sides from edge \"{edgeName}\" so readable-v2 can use its verified automatic candidate"
                : $"remove explicit route geometry from edge \"{edgeName}\" so readable-v2 can use its verified automatic candidate",
            (candidate, _) =>
            {
                candidate.Remove("via");
                candidate.Remove("channelX");
                candidate.Remove("channelY");
                candidate.Remove("route");
                if (clearSides)
                {
                    candidate.Remove("fromSide");
                    candidate.Remove("toSide");
                }
            });
    }

    JsonObject AuthoredPinEvidence(JsonObject edge, string field)
    {
        var evidence = new JsonObject { ["edge"] = WorkflowEdgeName(edge), ["field"] = field };
        if (sourceEdges.Has(edge)) evidence["path"] = $"/edges/{N(sourceEdges.Get(edge))}/{field}";
        // An absent field is `undefined`, which JSON drops.
        if (edge.TryGetPropertyValue(field, out var value)) evidence["value"] = value?.DeepClone();
        return evidence;
    }

    static List<List<T>> Combinations<T>(IReadOnlyList<T> values, int size, int start = 0, List<T>? prefix = null, List<List<T>>? output = null)
    {
        prefix ??= [];
        output ??= [];
        if (prefix.Count == size)
        {
            output.Add([.. prefix]);
            return output;
        }
        for (var index = start; index <= values.Count - (size - prefix.Count); index++)
        {
            prefix.Add(values[index]);
            Combinations(values, size, index + 1, prefix, output);
            prefix.RemoveAt(prefix.Count - 1);
        }
        return output;
    }

    (List<List<string>> RemovalSets, List<string> SupportedFixes) VerifiedPinRemovalAlternatives(JsonObject edge, IEnumerable<string> fields, string reason)
    {
        if (!discoverFixes) return ([], []);
        var edgeIndex = CanonicalIndex(edge);
        if (edgeIndex < 0) return ([], []);
        var uniqueFields = new JsSet<string>();
        foreach (var field in fields) if (edge.ContainsKey(field)) uniqueFields.Add(field);
        List<string> unique = [.. uniqueFields];
        for (var size = 1; size <= unique.Count; size++)
        {
            var removalSets = Combinations(unique, size).Where(fieldSet => AcceptsFix(document =>
            {
                foreach (var field in fieldSet) document.Edges[edgeIndex].Remove(field);
            })).ToList();
            if (removalSets.Count == 0) continue;
            var edgeName = WorkflowEdgeName(edge);
            return (removalSets, [.. removalSets.Select(fieldSet => $"remove {string.Join(" and ", fieldSet)} from edge \"{edgeName}\" {reason}")]);
        }
        return ([], []);
    }

    List<JsonObject> ConflictPinsFromRemovalSets(JsonObject edge, List<List<string>> removalSets, IEnumerable<string> fallbackFields)
    {
        var fields = new JsSet<string>();
        foreach (var field in removalSets.Count > 0 ? removalSets.SelectMany(set => set) : fallbackFields) fields.Add(field);
        return [.. fields.Select(field => AuthoredPinEvidence(edge, field))];
    }

    PinAlternatives VerifiedPinReferenceAlternatives(IEnumerable<PinRef> candidateRefs, string reason)
    {
        var seenRefs = new JsSet<string>();
        var refs = candidateRefs.Where(reference =>
        {
            if (reference.EdgeIndex < 0 || !reference.Edge.ContainsKey(reference.Field)) return false;
            var key = $"{N(reference.EdgeIndex)}:{reference.Field}";
            if (seenRefs.Has(key)) return false;
            seenRefs.Add(key);
            return true;
        }).ToList();
        List<JsonObject> FallbackPins() => [.. refs.Select(reference => AuthoredPinEvidence(reference.Edge, reference.Field))];
        if (!discoverFixes) return new([], refs, FallbackPins(), [], []);

        for (var size = 1; size <= refs.Count; size++)
        {
            var removalSets = Combinations(refs, size).Where(removalSet => AcceptsFix(document =>
            {
                foreach (var reference in removalSet) document.Edges[reference.EdgeIndex].Remove(reference.Field);
            })).ToList();
            if (removalSets.Count == 0) continue;
            var conflictingRefs = new List<PinRef>();
            var conflictingPins = new List<JsonObject>();
            var seenPins = new JsSet<string>();
            foreach (var removalSet in removalSets)
            {
                foreach (var reference in removalSet)
                {
                    var key = $"{N(CanonicalIndex(reference.Edge))}:{reference.Field}";
                    if (seenPins.Has(key)) continue;
                    seenPins.Add(key);
                    conflictingRefs.Add(new(reference.Edge, CanonicalIndex(reference.Edge), reference.Field));
                    conflictingPins.Add(AuthoredPinEvidence(reference.Edge, reference.Field));
                }
            }
            var repairs = removalSets.Select(removalSet =>
            {
                var grouped = new List<(JsonObject Edge, List<string> Fields)>();
                foreach (var reference in removalSet)
                {
                    var group = grouped.FindIndex(entry => ReferenceEquals(entry.Edge, reference.Edge));
                    if (group < 0)
                    {
                        grouped.Add((reference.Edge, []));
                        group = grouped.Count - 1;
                    }
                    grouped[group].Fields.Add(reference.Field);
                }
                var removals = grouped.Select(entry => $"remove {string.Join(" and ", entry.Fields)} from edge \"{WorkflowEdgeName(entry.Edge)}\"");
                return new PinRepair(removalSet, $"{string.Join(" and ", removals)} {reason}");
            }).ToList();
            return new(removalSets, conflictingRefs, conflictingPins, repairs, [.. repairs.Select(repair => repair.Message)]);
        }
        return new([], refs, FallbackPins(), [], []);
    }

    PinAlternatives VerifiedRoutePairPinAlternatives(JsonObject leftEdge, JsonObject rightEdge, string reason) =>
        VerifiedPinReferenceAlternatives(((JsonObject[])[leftEdge, rightEdge]).SelectMany(edge =>
        {
            var edgeIndex = CanonicalIndex(edge);
            return AuthoredRouteAssertionFields(edge).Select(field => new PinRef(edge, edgeIndex, field));
        }).ToList(), reason);

    PinAlternatives VerifiedLabelRoutePinAlternatives(JsonObject labelEdge, JsonObject routeEdge)
    {
        var refs = new List<PinRef>();
        var labelEdgeIndex = CanonicalIndex(labelEdge);
        if (labelEdge["labelAt"] is JsonArray) refs.Add(new(labelEdge, labelEdgeIndex, "labelAt"));
        var routeEdgeIndex = CanonicalIndex(routeEdge);
        foreach (var field in AuthoredRouteAssertionFields(routeEdge)) refs.Add(new(routeEdge, routeEdgeIndex, field));
        return VerifiedPinReferenceAlternatives(refs, labelEdge["labelAt"] is JsonArray
            ? "so readable-v2 can replan the remaining authored label-route pins"
            : "so readable-v2 can replan the remaining authored route assertions");
    }

    PinAlternatives VerifiedLabelPairPinAlternatives(JsonObject leftEdge, JsonObject rightEdge) => VerifiedPinReferenceAlternatives(
        ((JsonObject[])[leftEdge, rightEdge]).Where(edge => edge["labelAt"] is JsonArray).Select(edge => new PinRef(edge, CanonicalIndex(edge), "labelAt")).ToList(),
        "so readable-v2 can replan the remaining authored label pins");

    List<string> VerifiedRepairsWithLabelNudges(PinAlternatives alternatives) => [.. alternatives.Repairs.SelectMany(repair =>
    {
        if (repair.RemovalSet.Count != 1 || repair.RemovalSet[0].Field != "labelAt") return (List<string>)[repair.Message];
        var nudges = VerifiedLabelAtAlternatives(repair.RemovalSet[0].Edge);
        return nudges.Count > 0 ? nudges : [repair.Message];
    })];

    // throwExplicitPinConflict (:1847-1870); returns the exception so call sites read `throw ExplicitPinConflict(...)`.
    Exception ExplicitPinConflict(JsonObject edge, string invariant, JsonObject evidence, IEnumerable<string?> supportedFixes)
    {
        var message = $"Workflow edge \"{WorkflowEdgeName(edge)}\" has explicit geometry that violates {invariant}.";
        var conflictingPins = evidence["conflictingPins"].AsArrayOrEmpty().ToList();
        var onlyPin = conflictingPins.FirstOrDefault() as JsonObject;
        string? pinPath = null;
        if (conflictingPins.Count == 1 && sourceEdges.Has(edge) && onlyPin?["field"].Truthy() == true)
            pinPath = onlyPin["path"].Truthy() ? onlyPin.Str("path") : $"/edges/{N(sourceEdges.Get(edge))}/{Js.PrimitiveString(onlyPin["field"])}";
        var subject = new JsonObject
        {
            ["diagramType"] = "workflow", ["edge"] = edge["id"]?.DeepClone(), ["from"] = edge["from"]?.DeepClone(), ["to"] = edge["to"]?.DeepClone(),
        };
        if (pinPath is not null) subject["path"] = pinPath;
        var fullEvidence = new JsonObject { ["invariant"] = invariant };
        foreach (var (key, value) in evidence.ToList())
        {
            evidence.Remove(key);
            fullEvidence[key] = value;
        }
        return ctx.Diagnostics.ThrowDiagnosticError(message, [new Diagnostic("workflow/explicit-pin-conflict", Severity.Error, message, subject, fullEvidence,
            [.. supportedFixes.Where(fix => !string.IsNullOrEmpty(fix)).Select(fix => fix!)], null)]);
    }

    static List<string> PresentRouteGeometryFields(JsonObject edge)
    {
        var fields = new List<string>();
        if (edge["via"] is JsonArray) fields.Add("via");
        if (edge.ContainsKey("channelX")) fields.Add("channelX");
        if (edge.ContainsKey("channelY")) fields.Add("channelY");
        return fields;
    }

    PinAlternatives VerifiedRouteGeometryPinAlternatives(JsonObject edge, string reason = "so readable-v2 can replan the remaining explicit route assertions")
    {
        var edgeIndex = CanonicalIndex(edge);
        return VerifiedPinReferenceAlternatives([.. AuthoredRouteAssertionFields(edge).Select(field => new PinRef(edge, edgeIndex, field))], reason);
    }

    List<string> VerifiedLabelAtAlternatives(JsonObject edge)
    {
        if (edge["labelAt"] is not JsonArray labelAt) return [];
        var x = J.Coord(labelAt.Count > 0 ? labelAt[0] : null);
        var y = J.Coord(labelAt.Count > 1 ? labelAt[1] : null);
        (double Dx, double Dy)[] offsets = [(0, 24), (0, -24), (24, 0), (-24, 0), (0, 48), (0, -48), (48, 0), (-48, 0)];
        return [.. offsets.Select(offset =>
        {
            var next = (X: x + offset.Dx, Y: y + offset.Dy);
            return VerifiedEdgeFix(edge, $"set labelAt on edge \"{WorkflowEdgeName(edge)}\" to [{N(next.X)}, {N(next.Y)}]",
                (candidate, _) => candidate["labelAt"] = new JsonArray(next.X, next.Y));
        }).OfType<string>()];
    }

    string? VerifiedLabelAtNudge(JsonObject edge)
    {
        var alternatives = VerifiedLabelAtAlternatives(edge);
        if (alternatives.Count > 0) return alternatives[0];
        return VerifiedEdgeFix(edge, $"remove labelAt from edge \"{WorkflowEdgeName(edge)}\" so readable-v2 can use verified automatic label placement",
            (candidate, _) => candidate.Remove("labelAt"));
    }

    // ---------------------------------------------------------------- H1 conflict throwers (:1942-2010)
    // Each returns false when no authored pin is involved; otherwise it throws.
    bool ThrowReadableLabelRoutePinConflict(LabelClearanceHit hit, List<Pt>? routePoints = null)
    {
        var labelEdge = hit.LabelRelation;
        var routeEdge = hit.OtherRelation;
        var labelPinned = labelEdge["labelAt"] is JsonArray;
        var routePinned = HasAuthoredRouteAssertions(routeEdge);
        if (!labelPinned && !routePinned) return false;
        var alternatives = VerifiedLabelRoutePinAlternatives(labelEdge, routeEdge);
        var labelNudges = labelPinned && discoverFixes ? VerifiedLabelAtAlternatives(labelEdge) : [];
        // A passing replacement proves the label pin is causal even if its removal
        // recreates the collision under the ordinary automatic reservation policy.
        var conflictingRefs = labelNudges.Count > 0
            && !alternatives.ConflictingRefs.Any(reference => ReferenceEquals(reference.Edge, labelEdge) && reference.Field == "labelAt")
            ? [new PinRef(labelEdge, CanonicalIndex(labelEdge), "labelAt"), .. alternatives.ConflictingRefs]
            : alternatives.ConflictingRefs;
        List<string> supportedFixes = [
            .. labelNudges,
            .. alternatives.Repairs.Where(repair => labelNudges.Count == 0 || repair.RemovalSet.Count != 1 || repair.RemovalSet[0].Field != "labelAt")
                .Select(repair => repair.Message),
        ];
        var actualRoutePoints = routePoints ?? pathCache.Get(routeEdge)?.Points ?? PathFor(routeEdge).Points;
        var diagnosticEdge = conflictingRefs.Count > 0 ? conflictingRefs[0].Edge : labelPinned ? labelEdge : routeEdge;
        var evidence = new JsonObject { ["conflictingPins"] = new JsonArray([.. conflictingRefs.Select(reference => (JsonNode)AuthoredPinEvidence(reference.Edge, reference.Field))]) };
        if (labelPinned) evidence["labelAt"] = labelEdge["labelAt"]!.DeepClone();
        evidence["labelRect"] = RectJson(hit.Rect);
        evidence["collidedRoute"] = new JsonObject
        {
            ["edge"] = WorkflowEdgeName(routeEdge), ["from"] = routeEdge["from"]?.DeepClone(), ["to"] = routeEdge["to"]?.DeepClone(),
            ["points"] = PointsJson(actualRoutePoints),
        };
        evidence["routeSegmentIndex"] = hit.SegmentIndex;
        evidence["routeSegment"] = new JsonObject { ["from"] = PtJson(hit.Start), ["to"] = PtJson(hit.End) };
        evidence["clearancePx"] = Js.Round(hit.Clearance * 10) / 10;
        evidence["minimumPx"] = hit.Threshold;
        throw ExplicitPinConflict(diagnosticEdge, "explicit label-route clearance", evidence, supportedFixes);
    }

    bool ThrowReadableLabelLabelPinConflict(LabelRect left, LabelRect right)
    {
        var leftEdge = left.Relation;
        var rightEdge = right.Relation;
        var pinnedEdges = ((JsonObject[])[leftEdge, rightEdge]).Where(edge => edge["labelAt"] is JsonArray).ToList();
        if (pinnedEdges.Count == 0) return false;
        var alternatives = VerifiedLabelPairPinAlternatives(leftEdge, rightEdge);
        var diagnosticEdge = alternatives.ConflictingRefs.Count > 0 ? alternatives.ConflictingRefs[0].Edge : pinnedEdges[0];
        throw ExplicitPinConflict(diagnosticEdge, "explicit label-label clearance", new JsonObject
        {
            ["conflictingPins"] = new JsonArray([.. alternatives.ConflictingPins]),
            ["labelRects"] = new JsonArray([.. ((LabelRect[])[left, right]).Select(rect => (JsonNode)new JsonObject
            {
                ["edge"] = rect.Relation["id"]?.DeepClone(), ["x"] = rect.X, ["y"] = rect.Y, ["width"] = rect.Width, ["height"] = rect.Height,
            })]),
            ["minimumGapPx"] = -2,
        }, VerifiedRepairsWithLabelNudges(alternatives));
    }

    // H2's route-route throws (:2111-2140, :2153-2190).
    Exception RouteRouteCrossingConflict(RoutedRelation left, RoutedRelation right, AnalysisSegment leftSegment, AnalysisSegment rightSegment, Pt point)
    {
        var leftSourceIndex = Geometry.SourceSegmentIndexAtPoint(leftSegment, point);
        var rightSourceIndex = Geometry.SourceSegmentIndexAtPoint(rightSegment, point);
        var alternatives = VerifiedRoutePairPinAlternatives(left.Relation!, right.Relation!, "so readable-v2 can replan the remaining authored route assertions");
        var diagnosticEdge = HasAuthoredRouteAssertions(left.Relation) ? left.Relation! : right.Relation!;
        JsonObject SegmentJson(JsonObject relation, Pt from, Pt to) => new() { ["edge"] = relation["id"]?.DeepClone(), ["from"] = PtJson(from), ["to"] = PtJson(to) };
        return ExplicitPinConflict(diagnosticEdge, "explicit route-route crossing", new JsonObject
        {
            ["conflictingPins"] = new JsonArray([.. alternatives.ConflictingPins]),
            ["point"] = PtJson(point),
            ["segmentIndex"] = leftSourceIndex,
            ["otherSegmentIndex"] = rightSourceIndex,
            ["routeSegments"] = new JsonArray(SegmentJson(left.Relation!, leftSegment.Start, leftSegment.End), SegmentJson(right.Relation!, rightSegment.Start, rightSegment.End)),
            ["sourceRouteSegments"] = new JsonArray(
                SegmentJson(left.Relation!, left.Points![leftSourceIndex], left.Points[leftSourceIndex + 1]),
                SegmentJson(right.Relation!, right.Points![rightSourceIndex], right.Points[rightSourceIndex + 1])),
        }, alternatives.SupportedFixes);
    }

    Exception RouteRouteCorridorConflict(CorridorHit hit)
    {
        var alternatives = VerifiedRoutePairPinAlternatives(hit.Left.Relation, hit.Right.Relation, "so readable-v2 can replan the remaining authored route assertions");
        var diagnosticEdge = HasAuthoredRouteAssertions(hit.Left.Relation) ? hit.Left.Relation : hit.Right.Relation;
        JsonObject SegmentJson(Routed routed, int index) => new()
        {
            ["edge"] = routed.Relation["id"]?.DeepClone(), ["from"] = PtJson(routed.Points[index]), ["to"] = PtJson(routed.Points[index + 1]),
        };
        return ExplicitPinConflict(diagnosticEdge, "explicit route-route corridor clearance", new JsonObject
        {
            ["conflictingPins"] = new JsonArray([.. alternatives.ConflictingPins]),
            ["segmentIndex"] = hit.LeftSegment,
            ["otherSegmentIndex"] = hit.RightSegment,
            ["routeSegments"] = new JsonArray(SegmentJson(hit.Left, hit.LeftSegment), SegmentJson(hit.Right, hit.RightSegment)),
            ["overlapStart"] = PtJson(hit.OverlapStart),
            ["overlapEnd"] = PtJson(hit.OverlapEnd),
            ["overlapLengthPx"] = hit.OverlapLength,
            ["minimumClearancePx"] = 8,
        }, alternatives.SupportedFixes);
    }

    // validateReadableRouteControls' conflict (:2213-2222).
    Exception RouteControlsConflict(JsonObject edge, string preset, string[] allowedPins, List<string> conflictingPins)
    {
        var edgeIndex = CanonicalIndex(edge);
        var alternatives = VerifiedPinReferenceAlternatives(
            [new PinRef(edge, edgeIndex, "route"), .. conflictingPins.Select(field => new PinRef(edge, edgeIndex, field))],
            "and keep the remaining verified route assertions");
        return ExplicitPinConflict(edge, "route preset compatibility", new JsonObject
        {
            ["route"] = preset, ["allowedPins"] = Strings(allowedPins), ["conflictingPins"] = new JsonArray([.. alternatives.ConflictingPins]),
        }, alternatives.SupportedFixes);
    }

    // readableAutomaticRoute's authored-side failure (:4899-4931).
    Exception AuthoredSideRouteConflict(JsonObject edge, Box from, Box to, Sides primarySides, SpreadPorts? primaryPorts, List<string> authoredSideFields,
        Exception? firstFailure)
    {
        var alternatives = VerifiedPinRemovalAlternatives(edge, authoredSideFields, "so readable-v2 can replan the remaining endpoint-side pins");
        var sourceAnchor = primaryPorts?.From ?? Geometry.Anchor(from, primarySides.FromSide);
        var targetAnchor = primaryPorts?.To ?? Geometry.Anchor(to, primarySides.ToSide);
        var attemptedEvidence = (firstFailure as ArchifyDiagnosticException)?.Diagnostics.FirstOrDefault()?.Evidence ?? new JsonObject();
        var evidence = new JsonObject
        {
            ["conflictingPins"] = new JsonArray([.. ConflictPinsFromRemovalSets(edge, alternatives.RemovalSets, authoredSideFields)]),
            ["actualCoordinates"] = new JsonObject { ["sourceAnchor"] = PtJson(sourceAnchor), ["targetAnchor"] = PtJson(targetAnchor) },
            ["fromSide"] = primarySides.FromSide,
            ["toSide"] = primarySides.ToSide,
        };
        if (attemptedEvidence["attemptedCandidateFamilies"].Truthy()) evidence["attemptedCandidateFamilies"] = attemptedEvidence["attemptedCandidateFamilies"]!.DeepClone();
        if (attemptedEvidence.ContainsKey("candidateCount")) evidence["candidateCount"] = attemptedEvidence["candidateCount"]?.DeepClone();
        return ExplicitPinConflict(edge, "readable route feasibility with authored endpoint sides", evidence, alternatives.SupportedFixes);
    }

    // ---------------------------------------------------------------- H5 per-edge pin checks (:2422-2688)
    void ValidateReadableEdgePins(JsonObject edge)
    {
        var edgeName = WorkflowEdgeName(edge);
        // `/edges/${edgeIndex}`: an edge missing from sourceIndexes prints `undefined`.
        var edgeIndex = sourceEdges.Has(edge) ? N(sourceEdges.Get(edge)) : "undefined";
        if (edge["labelAt"] is JsonArray labelAt)
        {
            var rect = LabelRectFor(edge, CanonicalIndex(edge));
            if (rect is not null && (rect.X < 0 || rect.Y < 0))
            {
                throw ExplicitPinConflict(edge, "viewBox-origin containment", new JsonObject
                {
                    ["conflictingPins"] = new JsonArray(new JsonObject
                    {
                        ["edge"] = edgeName, ["field"] = "labelAt", ["path"] = $"/edges/{edgeIndex}/labelAt", ["value"] = labelAt.DeepClone(),
                    }),
                    ["offendingRect"] = RectJson(rect),
                    ["minimumCoordinate"] = 0,
                }, [VerifiedLabelAtNudge(edge)]);
            }
        }
        var via = edge["via"].AsArrayOrEmpty().ToList();
        var negativeViaIndex = via.FindIndex(point => J.PtOf(point) is var p && (p.X < 0 || p.Y < 0));
        JsonObject? negativeRoutePin = negativeViaIndex >= 0
            ? new() { ["field"] = "via", ["path"] = $"/edges/{edgeIndex}/via/{N(negativeViaIndex)}", ["value"] = via[negativeViaIndex]?.DeepClone() }
            : edge.Num("channelX") < 0
                ? new() { ["field"] = "channelX", ["path"] = $"/edges/{edgeIndex}/channelX", ["value"] = edge["channelX"]!.DeepClone() }
                : edge.Num("channelY") < 0
                    ? new() { ["field"] = "channelY", ["path"] = $"/edges/{edgeIndex}/channelY", ["value"] = edge["channelY"]!.DeepClone() }
                    : null;
        if (negativeRoutePin is not null)
        {
            var pin = new JsonObject { ["edge"] = edgeName };
            foreach (var (key, value) in negativeRoutePin.ToList()) { negativeRoutePin.Remove(key); pin[key] = value; }
            throw ExplicitPinConflict(edge, "viewBox-origin containment", new JsonObject { ["conflictingPins"] = new JsonArray(pin), ["minimumCoordinate"] = 0 },
                [VerifiedAutomaticRouteFix(edge), VerifiedAutomaticRouteFix(edge, clearSides: true)]);
        }
        var hasPinnedRoute = edge["via"] is JsonArray || edge.ContainsKey("channelX") || edge.ContainsKey("channelY");
        var points = PathFor(edge).Points;
        if (hasPinnedRoute) ValidatePinnedRoute(edge, points);

        if (edge["labelAt"].Truthy())
        {
            var rect = LabelRectFor(edge, CanonicalIndex(edge));
            JsonObject LabelEvidence(string key, JsonNode obstacle) => new()
            {
                ["conflictingPins"] = new JsonArray(AuthoredPinEvidence(edge, "labelAt")),
                ["labelAt"] = edge["labelAt"]!.DeepClone(),
                ["labelRect"] = RectJson(rect!),
                [key] = obstacle,
            };
            var obstacle = rect is null ? null : nodes.Values.FirstOrDefault(node => Geometry.RectsOverlap(rect, node, -2));
            if (obstacle is not null)
            {
                throw ExplicitPinConflict(edge, "edge-label node clearance", LabelEvidence("obstacleNode", obstacle.Id),
                    [VerifiedEdgeFix(edge, "remove labelAt so readable-v2 can use its verified automatic label placement", (candidate, _) => candidate.Remove("labelAt"))]);
            }
            var legendObstacle = rect is null ? null : WorkflowLegendRects().Select(legend => ((string, Rect)?)legend).FirstOrDefault(legend => Geometry.RectsOverlap(rect, legend!.Value.Item2));
            if (legendObstacle is { } legend)
            {
                throw ExplicitPinConflict(edge, "edge-label legend clearance", LabelEvidence("legendObstacle", LegendRectJson(legend)),
                    [VerifiedEdgeFix(edge, "remove labelAt so readable-v2 can use its verified automatic label placement", (candidate, _) => candidate.Remove("labelAt"))]);
            }
            var compositionObstacle = rect is null ? null : WorkflowSceneLabelObstacles().FirstOrDefault(candidate => Geometry.RectsOverlap(rect, RectOf(candidate)));
            if (compositionObstacle is not null)
            {
                throw ExplicitPinConflict(edge, "edge-label lane/phase/group clearance", LabelEvidence("compositionObstacle", SceneObstacleJson(compositionObstacle)),
                    [VerifiedEdgeFix(edge, $"remove labelAt from edge \"{WorkflowEdgeName(edge)}\" so readable-v2 can use its verified automatic label placement",
                        (candidate, _) => candidate.Remove("labelAt"))]);
            }
        }
    }

    static JsonObject LegendRectJson((string Kind, Rect Rect) legend) => new()
    {
        ["kind"] = legend.Kind, ["x"] = legend.Rect.X, ["y"] = legend.Rect.Y, ["width"] = legend.Rect.Width, ["height"] = legend.Rect.Height,
    };

    static JsonObject SceneObstacleJson(SceneObstacle obstacle) => new()
    {
        ["kind"] = obstacle.Kind, ["id"] = obstacle.Id?.DeepClone(), ["x"] = obstacle.X, ["y"] = obstacle.Y, ["width"] = obstacle.Width, ["height"] = obstacle.Height,
    };

    static JsonObject WithCollision(JsonObject target, RouteNodeCollision? collision)
    {
        if (collision is null) return target;
        target["obstacleNode"] = collision.ObstacleNode;
        target["obstacleRole"] = collision.ObstacleRole;
        target["segmentIndex"] = collision.SegmentIndex;
        target["from"] = PtJson(collision.From);
        target["to"] = PtJson(collision.To);
        target["clearancePx"] = collision.ClearancePx;
        return target;
    }

    // The `hasPinnedRoute` block (:2464-2640).
    void ValidatePinnedRoute(JsonObject edge, List<Pt> points)
    {
        var invalidPointIndex = points.FindIndex(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y));
        if (invalidPointIndex != -1)
        {
            var alternatives = VerifiedRouteGeometryPinAlternatives(edge);
            throw ExplicitPinConflict(edge, "finite route coordinates", new JsonObject
            {
                ["conflictingPins"] = new JsonArray([.. alternatives.ConflictingPins]), ["pointIndex"] = invalidPointIndex, ["point"] = PtJson(points[invalidPointIndex]),
            }, alternatives.SupportedFixes);
        }
        for (var segmentIndex = 0; segmentIndex < points.Count - 1; segmentIndex++)
        {
            var start = points[segmentIndex];
            var end = points[segmentIndex + 1];
            var dx = Math.Abs(end.X - start.X);
            var dy = Math.Abs(end.Y - start.Y);
            if (dx <= 0.0001 && dy <= 0.0001)
            {
                var viaCount = edge["via"] is JsonArray viaArray ? viaArray.Count : 0;
                var duplicateFix = viaCount > 0
                    ? VerifiedEdgeFix(edge, $"remove duplicate via[{N(Math.Min(segmentIndex, viaCount - 1))}] and keep the remaining authored pins unchanged",
                        (candidate, _) =>
                        {
                            var candidateVia = (JsonArray)candidate["via"]!;
                            candidateVia.RemoveAt(Math.Min(segmentIndex, candidateVia.Count - 1));
                        })
                    : VerifiedAutomaticRouteFix(edge);
                var alternatives = edge["via"] is JsonArray ? null : VerifiedRouteGeometryPinAlternatives(edge);
                throw ExplicitPinConflict(edge, "non-zero route segments", new JsonObject
                {
                    ["conflictingPins"] = new JsonArray([.. alternatives?.ConflictingPins ?? [AuthoredPinEvidence(edge, "via")]]),
                    ["segmentIndex"] = segmentIndex, ["from"] = PtJson(start), ["to"] = PtJson(end),
                }, alternatives is null ? [duplicateFix] : (IEnumerable<string?>)alternatives.SupportedFixes);
            }
            if (dx > 0.0001 && dy > 0.0001)
            {
                var alternatives = VerifiedRouteGeometryPinAlternatives(edge);
                throw ExplicitPinConflict(edge, "orthogonal route segments", new JsonObject
                {
                    ["conflictingPins"] = new JsonArray([.. alternatives.ConflictingPins]),
                    ["segmentIndex"] = segmentIndex, ["from"] = PtJson(start), ["to"] = PtJson(end),
                }, alternatives.SupportedFixes);
            }
            var endpoint = segmentIndex == 0 || segmentIndex == points.Count - 2;
            double minimumPx = points.Count == 2 ? 28 : endpoint ? 8 : 16;
            var lengthPx = dx + dy;
            if (lengthPx + 0.0001 < minimumPx)
            {
                var alternatives = VerifiedRouteGeometryPinAlternatives(edge);
                throw ExplicitPinConflict(edge, endpoint ? "8px endpoint stub clearance" : "16px interior turn clearance", new JsonObject
                {
                    ["conflictingPins"] = new JsonArray([.. alternatives.ConflictingPins]),
                    ["segmentIndex"] = segmentIndex,
                    ["position"] = segmentIndex == 0 ? "source-stub" : segmentIndex == points.Count - 2 ? "target-stub" : "interior",
                    ["from"] = PtJson(start), ["to"] = PtJson(end), ["lengthPx"] = lengthPx, ["minimumPx"] = minimumPx,
                }, alternatives.SupportedFixes);
            }
        }
        var (fromSide, toSide) = EdgeSides(edge);
        if (edge["via"] is JsonArray)
        {
            var missingChannelPins = PresentChannelPins(edge).Where(field => !RouteContainsChannelPin(points, field, edge.Num(field) ?? double.NaN)).ToList();
            if (missingChannelPins.Count > 0)
            {
                List<string> candidateFields = ["via", .. missingChannelPins];
                var alternatives = VerifiedPinRemovalAlternatives(edge, candidateFields, "and replan the remaining explicit route assertions");
                throw ExplicitPinConflict(edge, "channel pin preservation", new JsonObject
                {
                    ["route"] = RouteOrAuto(edge),
                    ["conflictingPins"] = new JsonArray([.. ConflictPinsFromRemovalSets(edge, alternatives.RemovalSets, candidateFields)]),
                    ["points"] = PointsJson(points),
                }, alternatives.SupportedFixes);
            }
        }
        if (edge["route"].Truthy() && edge.Str("route") != "auto" && !RouteMatchesPresetFamily(edge.Str("route"), points, NodeOf(edge["from"])!, NodeOf(edge["to"])!))
        {
            var authoredEdgeIndex = CanonicalIndex(edge);
            var alternatives = VerifiedPinReferenceAlternatives(
                [new PinRef(edge, authoredEdgeIndex, "route"), .. PresentRouteGeometryFields(edge).Select(field => new PinRef(edge, authoredEdgeIndex, field))],
                "and keep the remaining verified route assertions");
            throw ExplicitPinConflict(edge, "route preset compatibility", new JsonObject
            {
                ["route"] = edge["route"]!.DeepClone(), ["conflictingPins"] = new JsonArray([.. alternatives.ConflictingPins]), ["points"] = PointsJson(points),
            }, alternatives.SupportedFixes);
        }
        if (!Geometry.RouteHonorsEndpointSides(points, fromSide, toSide))
        {
            var candidateFields = new List<string>();
            if (edge["fromSide"].Truthy() && edge.Str("fromSide") != "auto" && !EndpointSideIsHonored(points, fromSide, "source")) candidateFields.Add("fromSide");
            if (edge["toSide"].Truthy() && edge.Str("toSide") != "auto" && !EndpointSideIsHonored(points, toSide, "target")) candidateFields.Add("toSide");
            candidateFields.AddRange(PresentRouteGeometryFields(edge));
            var alternatives = VerifiedPinRemovalAlternatives(edge, candidateFields, "and replan the remaining explicit pins");
            throw ExplicitPinConflict(edge, "perpendicular endpoint-side direction", new JsonObject
            {
                ["conflictingPins"] = new JsonArray([.. ConflictPinsFromRemovalSets(edge, alternatives.RemovalSets, candidateFields)]),
                ["points"] = PointsJson(points), ["fromSide"] = fromSide, ["toSide"] = toSide,
            }, alternatives.SupportedFixes);
        }
        if (FirstRouteNodeCollision(edge, points) is { } nodeCollision)
        {
            var alternatives = VerifiedRouteGeometryPinAlternatives(edge);
            throw ExplicitPinConflict(edge, "node clearance",
                WithCollision(new JsonObject { ["conflictingPins"] = new JsonArray([.. alternatives.ConflictingPins]) }, nodeCollision), alternatives.SupportedFixes);
        }
        bool Crosses(Rect rect) => Enumerable.Range(0, Math.Max(0, points.Count - 1)).Any(index => Geometry.SegmentIntersectsRect(new(points[index], points[index + 1]), rect));
        var legendObstacle = WorkflowLegendRects().Select(legend => ((string, Rect)?)legend).FirstOrDefault(legend => Crosses(legend!.Value.Item2));
        if (legendObstacle is { } legend)
        {
            var alternatives = VerifiedRouteGeometryPinAlternatives(edge);
            throw ExplicitPinConflict(edge, "legend clearance", new JsonObject
            {
                ["conflictingPins"] = new JsonArray([.. alternatives.ConflictingPins]), ["points"] = PointsJson(points), ["legendObstacle"] = LegendRectJson(legend),
            }, alternatives.SupportedFixes);
        }
        var compositionObstacle = WorkflowSceneLabelObstacles().FirstOrDefault(obstacle => Crosses(RectOf(obstacle)));
        if (compositionObstacle is not null)
        {
            var alternatives = VerifiedRouteGeometryPinAlternatives(edge);
            throw ExplicitPinConflict(edge, "lane/phase/group label clearance", new JsonObject
            {
                ["conflictingPins"] = new JsonArray([.. alternatives.ConflictingPins]), ["points"] = PointsJson(points),
                ["compositionObstacle"] = SceneObstacleJson(compositionObstacle),
            }, alternatives.SupportedFixes);
        }
        var frameRun = Geometry.CollectBorderRuns([new RoutedRelation(points)], WorkflowCompositionFrames()).FirstOrDefault();
        if (frameRun is not null)
        {
            var alternatives = VerifiedRouteGeometryPinAlternatives(edge);
            throw ExplicitPinConflict(edge, "structural-frame border clearance", new JsonObject
            {
                ["conflictingPins"] = new JsonArray([.. alternatives.ConflictingPins]), ["points"] = PointsJson(points),
                ["frame"] = frameRun.Frame.Id?.DeepClone() ?? frameRun.FrameIndex, ["side"] = frameRun.Side, ["overlapLengthPx"] = frameRun.OverlapLength,
            }, alternatives.SupportedFixes);
        }
    }

    // ---------------------------------------------------------------- J3 preset rejection evidence (:3516-3545, :3708-3748)
    JsonObject PlacedLabelConflictEvidence(JsonObject edge, Rect? candidateLabel, ObstacleItem item, string conflictType, List<Pt>? points = null,
        int? segmentIndex = null, double clearance = 0)
    {
        JsonObject EdgeEvidence(JsonObject relation) => new()
        {
            ["edge"] = relation["id"]?.DeepClone(), ["from"] = relation["from"]?.DeepClone(), ["to"] = relation["to"]?.DeepClone(),
            ["path"] = $"/edges/{(sourceEdges.Has(relation) ? N(sourceEdges.Get(relation)) : "undefined")}",
        };
        JsonObject LabelEvidence(JsonObject relation, Rect rect)
        {
            var evidence = new JsonObject();
            if (relation.TryGetPropertyValue("label", out var label)) evidence["label"] = label?.DeepClone();
            evidence["rect"] = RectJson(rect);
            return evidence;
        }
        var result = new JsonObject { ["conflictType"] = conflictType, ["candidateEdge"] = EdgeEvidence(edge), ["otherEdge"] = EdgeEvidence(item.Edge!) };
        if (candidateLabel is { } own) result["candidateLabel"] = LabelEvidence(edge, own);
        if (item.Kind == "label") result["otherLabel"] = LabelEvidence(item.Edge!, item.Rect!);
        if (segmentIndex is { } index)
        {
            result["segment"] = new JsonObject
            {
                ["edge"] = conflictType == "route-label" ? "candidate" : "other", ["index"] = index, ["from"] = PtJson(points![index]), ["to"] = PtJson(points[index + 1]),
            };
            result["measuredClearancePx"] = clearance;
            result["requiredClearancePx"] = 4;
        }
        else
        {
            var label = candidateLabel!.Value;
            Rect other = item.Rect!;
            result["rectangleMarginPx"] = -2;
            result["overlapPx"] = new JsonObject
            {
                ["x"] = Math.Min(label.X + label.Width, other.X + other.Width) - Math.Max(label.X, other.X),
                ["y"] = Math.Min(label.Y + label.Height, other.Y + other.Height) - Math.Max(label.Y, other.Y),
            };
        }
        return result;
    }

    // Inspect only a rejected preset. Keep successful routing and automatic
    // candidate enumeration on the short-circuit feasibility path.
    JsonObject ReadablePresetRejection(JsonObject edge, List<Pt> points, Box from, Box to, string fromSide, string toSide)
    {
        var invariant = ReadableCandidateRejection(edge, points, from, to, fromSide, toSide)
            ?? (!RouteMatchesPresetFamily(edge.Str("route"), points, from, to) ? "route preset compatibility" : null);
        JsonObject Rejection(string? value) => value is null ? [] : new() { ["invariant"] = value };
        if (invariant == "edge-label node clearance")
        {
            // A label move cannot repair a preset whose stroke also crosses a node.
            // Keep the fast feasibility order, but report the obstructed route first.
            if (FirstRouteNodeCollision(edge, points) is { } collision) return WithCollision(Rejection("node clearance"), collision);
        }
        if (invariant == "placed edge-label clearance")
        {
            var evidence = new JsonObject();
            RouteClearsPlacedLabels(edge, points, evidence);
            var result = Rejection(invariant);
            foreach (var (key, value) in evidence.ToList()) { evidence.Remove(key); result[key] = value; }
            return result;
        }
        if (invariant == "node clearance") return WithCollision(Rejection(invariant), FirstRouteNodeCollision(edge, points));
        if (invariant == "readable segment rhythm")
        {
            var segmentIndex = Enumerable.Range(0, Math.Max(0, points.Count - 1)).Select(index => (int?)index).FirstOrDefault(index =>
                Js.Hypot(points[index!.Value + 1].X - points[index.Value].X, points[index.Value + 1].Y - points[index.Value].Y) + 0.0001 < HardRouteSegmentMinimum(points, index.Value)) ?? -1;
            var result = Rejection(invariant);
            result["segmentIndex"] = segmentIndex;
            result["actualSegmentPx"] = Js.Hypot(points[segmentIndex + 1].X - points[segmentIndex].X, points[segmentIndex + 1].Y - points[segmentIndex].Y);
            result["requiredSegmentPx"] = HardRouteSegmentMinimum(points, segmentIndex);
            return result;
        }
        if (invariant == "edge-label node clearance")
        {
            var labelRect = CandidateLabelRect(edge, points)!.Value;
            var obstacle = nodes.Values.FirstOrDefault(node => Geometry.RectsOverlap(labelRect, node, -2));
            var result = Rejection(invariant);
            result["labelRect"] = RectJson(labelRect);
            if (obstacle is not null) result["obstacleNode"] = obstacle.Id;
            return result;
        }
        return Rejection(invariant);
    }

    // ---------------------------------------------------------------- K3: readablePresetVia (:4205-4303)
    List<Pt> ReadablePresetVia(JsonObject edge, Box from, Box to, Pt start, Pt end, string fromSide, string toSide)
    {
        var preset = edge.Str("route");
        List<Pt> via;
        switch (preset)
        {
            case "straight":
                via = [];
                break;
            case "drop":
            {
                var y = GapYBetween(Lane(from), Lane(to), edge.Num("bias") ?? 0.5);
                via = [new(start.X, y), new(end.X, y)];
                break;
            }
            case "outside-right":
            {
                var x = layout.LaneX + layout.LaneW + 12;
                via = [new(x, start.Y), new(x, end.Y)];
                break;
            }
            case "return-left":
            {
                var x = Math.Min(from.X, to.X) - 28;
                via = [new(x, start.Y), new(x, end.Y)];
                break;
            }
            case "bottom-channel":
            {
                var y = Math.Max(from.Y + from.Height, to.Y + to.Height) + 32;
                via = [new(start.X, y), new(end.X, y)];
                break;
            }
            case "up-channel":
            {
                var y = Math.Min(from.Y, to.Y) - 28;
                via = [new(start.X, y), new(end.X, y)];
                break;
            }
            default:
                return ReadableAutomaticVia(edge, from, to, start, end, fromSide, toSide);
        }
        var points = Geometry.JoinRoutePoints(start, via, end);
        // This success branch (workflow-compiler.mjs:4241-4244) is unreachable: argued and probed (38 fallback calls), not exhaustively proven.
        // Only two callers exist. routeVia (:4322, guarded by `if (edge.via)` at :4315) is dead for v2: a non-auto preset edge without pins
        // is a controlled route, so readableControlledRoute returns a plan and :5117 is never reached. The fallback (:5038-5056, call :5040-5048)
        // runs only when no candidate passed the filters (:5002 feasible, :5003-5008 preset family, :5009-5011 channel pins) and recomputes
        // the failed candidate's points: joinRoutePoints applies the same accept rule as normalizeRoutePoints (geometry.mjs:1332-1349 vs :1351-1371).
        if (ReadableCandidateIsFeasible(edge, points, from, to, fromSide, toSide) && RouteMatchesPresetFamily(preset, points, from, to)) return points[1..^1];
        var rejection = ReadablePresetRejection(edge, points, from, to, fromSide, toSide);
        var obstacle = rejection["obstacleNode"].Truthy()
            ? $": {(rejection.ContainsKey("segmentIndex") ? $"segment {Tpl(rejection, "segmentIndex")}" : "its label")} intersects node \"{Tpl(rejection, "obstacleNode")}\""
            : "";
        var message = $"Workflow edge \"{WorkflowEdgeName(edge)}\" cannot satisfy route preset \"{preset}\": candidate violates {Tpl(rejection, "invariant")}{obstacle}.";
        var edgeIndex = CanonicalIndex(edge);
        var edgeName = WorkflowEdgeName(edge);
        var supportedFixes = new List<string>();
        if (discoverFixes && rejection.Str("invariant") == "edge-label node clearance" && edge["label"].Truthy()
            && ((string[])["labelAt", "labelDx", "labelDy", "labelSegment"]).All(field => !edge.ContainsKey(field)))
        {
            for (var segment = 0; segment < Math.Min(points.Count - 1, 3); segment++)
            {
                var labelSegment = segment;
                var fix = VerifiedEdgeFix(edge, $"set labelSegment on edge \"{edgeName}\" to {N(labelSegment)}", (candidate, _) => candidate["labelSegment"] = labelSegment);
                if (fix is not null)
                {
                    supportedFixes.Add(fix);
                    break;
                }
            }
        }
        foreach (var candidatePreset in (string[])["straight", "drop", "outside-right", "return-left", "bottom-channel", "up-channel"])
        {
            if (candidatePreset == preset) continue;
            if (AcceptsFix(document => document.Edges[edgeIndex]["route"] = candidatePreset))
                supportedFixes.Add($"set edge \"{edgeName}\" route to verified preset \"{candidatePreset}\"");
        }
        if (AcceptsFix(document => document.Edges[edgeIndex].Remove("route")))
            supportedFixes.Add($"remove route from edge \"{edgeName}\" so readable-v2 can use its verified automatic candidate");
        var evidence = new JsonObject();
        foreach (var (key, value) in rejection.ToList()) { rejection.Remove(key); evidence[key] = value; }
        evidence["attemptedCandidateFamily"] = preset;
        evidence["points"] = PointsJson(points);
        evidence["fromSide"] = fromSide;
        evidence["toSide"] = toSide;
        evidence["requiredEndpointStubPx"] = HardRouteRhythm.Endpoint;
        evidence["requiredInteriorSegmentPx"] = HardRouteRhythm.Interior;
        evidence["requiredDirectClearancePx"] = HardRouteRhythm.Direct;
        throw ctx.Diagnostics.ThrowDiagnosticError(message, [new Diagnostic("workflow/route-preset-conflict", Severity.Error, message,
            new JsonObject
            {
                ["diagramType"] = "workflow", ["edge"] = edge["id"]?.DeepClone(), ["from"] = edge["from"]?.DeepClone(), ["to"] = edge["to"]?.DeepClone(), ["route"] = preset,
            },
            evidence, supportedFixes, null)]);
    }

    // ---------------------------------------------------------------- K7: readableControlledRoute (:4945-5057)
    Plan? ReadableControlledRoute(JsonObject edge, Box from, Box to)
    {
        var authoredFrom = AuthoredSide(edge, "fromSide");
        var authoredTo = AuthoredSide(edge, "toSide");
        var naturalFromSide = authoredFrom ?? Geometry.LegacyDefaultFromSide(from, to);
        var naturalToSide = authoredTo ?? Geometry.LegacyDefaultToSide(from, to);
        var preferredPairs = new List<Sides> { new(naturalFromSide, naturalToSide) };
        foreach (var fromSide in authoredFrom is not null ? [authoredFrom] : SideOrder)
            foreach (var toSide in authoredTo is not null ? [authoredTo] : SideOrder)
                preferredPairs.Add(new(fromSide, toSide));

        var seen = new JsSet<string>();
        var sidePairs = preferredPairs.Where(pair =>
        {
            if (authoredFrom is not null && pair.FromSide != authoredFrom) return false;
            if (authoredTo is not null && pair.ToSide != authoredTo) return false;
            var key = $"{pair.FromSide}:{pair.ToSide}";
            if (seen.Has(key)) return false;
            seen.Add(key);
            return true;
        }).ToList();
        var hasAbsoluteRoutePins = edge["via"] is JsonArray || edge.ContainsKey("channelX") || edge.ContainsKey("channelY");
        var candidates = new List<Plan>();
        var diagnosticCandidates = new List<Plan>();
        var materializedCandidates = new List<Plan>();
        for (var ordinal = 0; ordinal < sidePairs.Count; ordinal++)
        {
            var (fromSide, toSide) = sidePairs[ordinal];
            var start = Geometry.Anchor(from, fromSide);
            var end = Geometry.Anchor(to, toSide);
            var via = RouteVia(edge, from, to, start, end, fromSide, toSide, validateReadablePreset: false);
            List<Pt> authoredPoints = [start, .. via, end];
            var points = hasAbsoluteRoutePins ? authoredPoints : Geometry.NormalizeRoutePoints(authoredPoints);
            var materialized = new Plan { Points = points, FromSide = fromSide, ToSide = toSide };
            materializedCandidates.Add(materialized);
            if (points.Count >= 2 && points.All(point => double.IsFinite(point.X) && double.IsFinite(point.Y))
                && Geometry.RouteHonorsEndpointSides(points, fromSide, toSide)) diagnosticCandidates.Add(materialized);
            if (!ReadableCandidateIsFeasible(edge, points, from, to, fromSide, toSide)) continue;
            if (edge["route"].Truthy() && edge.Str("route") != "auto" && !RouteMatchesPresetFamily(edge.Str("route"), points, from, to)) continue;
            if (PresentChannelPins(edge).Any(field => !RouteContainsChannelPin(points, field, edge.Num(field) ?? double.NaN))) continue;
            candidates.Add(materialized.With(points, ReadableCandidateCost(edge, points, ordinal, naturalFromSide, naturalToSide)));
        }
        Js.Sort(candidates, (left, right) => CompareCost(left.Cost, right.Cost));
        if (candidates.Count > 0) return candidates[0];

        // Absolute geometry is authoritative even when it is invalid. Preserve the
        // best endpoint-side inference so validation can diagnose the authored
        // segment or preset that actually failed instead of silently falling back to
        // default sides and changing the route's meaning.
        if (hasAbsoluteRoutePins) return diagnosticCandidates.FirstOrDefault() ?? materializedCandidates.FirstOrDefault();

        // Preset-only routes retain their dedicated typed conflict (and verified
        // alternative search) when exhaustive side inference found no valid plan.
        var fallback = materializedCandidates.FirstOrDefault();
        if (fallback is null) return null;
        // The success branch of ReadablePresetVia is unreachable here: this fallback recomputes geometry that already failed (Task 10 report), so no fixture exists for it (argument at the success branch, ReadablePresetVia, TEST-004).
        var fallbackVia = ReadablePresetVia(edge, from, to, fallback.Points[0], fallback.Points[^1], fallback.FromSide, fallback.ToSide);
        return new Plan { Points = Geometry.NormalizeRoutePoints([fallback.Points[0], .. fallbackVia, fallback.Points[^1]]), FromSide = fallback.FromSide, ToSide = fallback.ToSide };
    }
}
