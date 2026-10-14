using System.Text.Json.Nodes;
using static Archify.Net.WorkflowModel;

namespace Archify.Net;

// workflow-compiler.mjs cluster I: validateWorkflow (:2693-3036) and the readable-v2 input gate (:3038-3258, Task 9); the
// readable pinned-geometry driver (H5, :2378-2692) is Task 10.
internal sealed partial class WorkflowCompilation
{
    // validateReadableInputsBeforeRouting: v2-only typed input diagnostics with verified fixes.
    void ValidateReadableInputsBeforeRouting()
    {
        if (workflow.SchemaVersion != 2) return;
        throw new NotImplementedException("Task 9: validateReadableInputsBeforeRouting (workflow-compiler.mjs:3038-3258)");
    }

    // validateReadablePinnedGeometry: the v2 routing driver (reserve pinned → straight → routeFirst → all edges → pin checks → pairwise).
    void ValidateReadablePinnedGeometry()
    {
        if (workflow.SchemaVersion != 2) return;
        throw new NotImplementedException("Task 10: validateReadablePinnedGeometry (workflow-compiler.mjs:2378-2692)");
    }

    void ValidateWorkflow()
    {
        // Report edges by their authored index; the canonical order is internal.
        var authoredEdges = qualityResolvedWorkflow.Edges;
        var problems = new List<string>();
        // ponytail: the nodes/edges/phases/groups/mainPath/cards array checks (:2709-2726) are the WorkflowDoc's types.
        if (workflow.SchemaVersion != 1 && workflow.SchemaVersion != 2) problems.Add("Workflow files must set \"schema_version\" to 1 or 2.");
        if (workflow.DiagramType != "workflow") problems.Add($"Unsupported diagram_type \"{workflow.DiagramType ?? "undefined"}\". Expected \"workflow\".");
        if (!workflow.Meta["title"].Truthy()) problems.Add("Workflow files must include meta.title.");
        if (workflow.Lanes.Count == 0) problems.Add("Workflow files must include at least one lane.");
        if (problems.Count > 0)
            throw ctx.Diagnostics.ThrowDiagnosticProblems("Workflow layout validation failed", problems, subject: new JsonObject { ["diagramType"] = "workflow" });

        EnforceLegacyColumnCapacity();

        var laneIds = new JsSet<string>();
        foreach (var lane in workflow.Lanes) laneIds.Add(IdKey(lane["id"]));
        if (laneIds.Count != workflow.Lanes.Count) problems.Add("Lane ids must be unique.");
        if (nodes.Count != workflow.Nodes.Count) problems.Add("Node ids must be unique.");
        var phaseIds = new JsSet<string>();
        foreach (var phase in workflow.Phases ?? []) phaseIds.Add(IdKey(phase["id"]));
        if (phaseIds.Count != (workflow.Phases?.Count ?? 0)) problems.Add("Phase ids must be unique.");
        var groupIds = new JsSet<string>();
        foreach (var group in workflow.Groups ?? []) groupIds.Add(IdKey(group["id"]));
        if (groupIds.Count != (workflow.Groups?.Count ?? 0)) problems.Add("Group ids must be unique.");

        foreach (var node in nodes.Values)
        {
            var source = node.Source;
            if (!laneIds.Has(IdKey(source["lane"])))
            {
                problems.Add($"Node \"{node.Id}\" uses unknown lane \"{Tpl(source, "lane")}\".");
                continue;
            }
            var col = source.Num("col");
            if (!IsInteger(col) || col < 0 || col >= layout.ColXs.Length)
            {
                problems.Add($"Node \"{node.Id}\" uses column {Tpl(source, "col")}, but valid columns are integers 0..{N(layout.ColXs.Length - 1)}.");
                continue;
            }
            if (!Geometry.IsFinitePoint(node.X, node.Y, node.Cx, node.Cy))
            {
                problems.Add($"Node \"{node.Id}\" produced non-finite coordinates — check col, width, height, and yOffset are numbers.");
                continue;
            }
            problems.AddRange(WorkflowScene.WorkflowNodeTextProblems(source, node.Width, node.Brand));

            var top = lanes.LaneTop(source["lane"]);
            var contentTop = top + layout.LaneTitleH + lanes.LaneGroupHeaderH(source["lane"]);
            var laneRight = layout.LaneX + layout.LaneW;
            if (node.X < layout.LaneX || node.X + node.Width > laneRight)
                problems.Add($"Node \"{node.Id}\" exceeds the horizontal bounds of lane \"{Tpl(source, "lane")}\".");
            if (node.Y < contentTop || node.Y + node.Height > top + lanes.LaneHeight(source["lane"]))
                problems.Add($"Node \"{node.Id}\" collides with the title or boundary of lane \"{Tpl(source, "lane")}\".");
        }

        var phaseRanges = new List<JsonObject>();
        foreach (var phase in workflow.Phases ?? [])
        {
            var fromCol = phase.Num("fromCol");
            var toCol = phase.Num("toCol");
            if (!IsInteger(fromCol) || !IsInteger(toCol))
            {
                problems.Add($"Phase \"{Tpl(phase, "id")}\" must use integer fromCol/toCol values.");
                continue;
            }
            if (fromCol < 0 || toCol >= layout.ColXs.Length || fromCol > toCol)
                problems.Add($"Phase \"{Tpl(phase, "id")}\" uses invalid columns {Tpl(phase, "fromCol")}..{Tpl(phase, "toCol")}; use an ordered range within 0..{N(layout.ColXs.Length - 1)}.");
            else
                phaseRanges.Add(phase);
            var estLabelW = Utils.TextUnits(phase.Str("label")) * 5.6;
            var width = PhaseSpan(phase).Width;
            if (estLabelW > width + 8)
                problems.Add($"Phase label \"{Tpl(phase, "label")}\" (~{N(Js.Round(estLabelW))}px) is wider than its {N(Js.Round(width))}px span — shorten the label or widen the phase range.");
        }
        Js.Sort(phaseRanges, (a, b) => Lor(a.Num("fromCol")!.Value - b.Num("fromCol")!.Value, a.Num("toCol")!.Value - b.Num("toCol")!.Value));
        for (var i = 0; i < phaseRanges.Count; i++)
        {
            for (var j = i + 1; j < phaseRanges.Count; j++)
            {
                var earlier = phaseRanges[i];
                var later = phaseRanges[j];
                if (later.Num("fromCol") > earlier.Num("toCol")) break;
                problems.Add($"Phase \"{Tpl(later, "id")}\" ({Tpl(later, "fromCol")}..{Tpl(later, "toCol")}) overlaps phase \"{Tpl(earlier, "id")}\" ({Tpl(earlier, "fromCol")}..{Tpl(earlier, "toCol")}) — start at col {N(earlier.Num("toCol")!.Value + 1)} or later, or end the earlier phase at col {N(later.Num("fromCol")!.Value - 1)}.");
            }
        }

        foreach (var group in workflow.Groups ?? [])
        {
            if (!laneIds.Has(IdKey(group["lane"])))
            {
                problems.Add($"Group \"{Tpl(group, "id")}\" uses unknown lane \"{Tpl(group, "lane")}\".");
                continue;
            }
            var fromCol = group.Num("fromCol");
            var toCol = group.Num("toCol");
            if (!IsInteger(fromCol) || !IsInteger(toCol))
            {
                problems.Add($"Group \"{Tpl(group, "id")}\" must use integer fromCol/toCol values.");
                continue;
            }
            if (fromCol < 0 || toCol >= layout.ColXs.Length || fromCol > toCol)
                problems.Add($"Group \"{Tpl(group, "id")}\" uses invalid columns {Tpl(group, "fromCol")}..{Tpl(group, "toCol")}; use an ordered range within 0..{N(layout.ColXs.Length - 1)}.");
            var contained = nodes.Values.Any(node => StrictEquals(Lane(node), group["lane"]) && Col(node) >= fromCol && Col(node) <= toCol);
            if (!contained)
                problems.Add($"Group \"{Tpl(group, "id")}\" does not contain any nodes — align its lane/columns with the parallel or branch work it frames.");
        }

        var byLane = new JsMap<string, (JsonNode? Lane, List<Box> Nodes)>();
        foreach (var node in nodes.Values)
        {
            var key = IdKey(Lane(node));
            byLane.Set(key, (Lane(node), [.. byLane.Get(key).Nodes ?? [], node]));
        }
        foreach (var (lane, laneNodes) in byLane.Values)
        {
            for (var i = 0; i < laneNodes.Count; i++)
            {
                for (var j = i + 1; j < laneNodes.Count; j++)
                {
                    if (Geometry.RectsOverlap(laneNodes[i], laneNodes[j], 8))
                        problems.Add($"Nodes \"{laneNodes[i].Id}\" and \"{laneNodes[j].Id}\" are less than 8px apart in lane \"{BrandMarks.JsString(lane)}\" — move one to another col, adjust yOffset, or reduce width/height.");
                }
            }
        }

        foreach (var edge in workflow.Edges)
        {
            if (!Has(edge["from"])) problems.Add($"Edge \"{TplOr(edge, "label", "from")}\" references unknown source \"{Tpl(edge, "from")}\".");
            if (!Has(edge["to"])) problems.Add($"Edge \"{TplOr(edge, "label", "to")}\" references unknown target \"{Tpl(edge, "to")}\".");
            if (Has(edge["from"]) && Has(edge["to"]))
            {
                var routed = PathFor(edge);
                if (routed.Points.Count == 2)
                {
                    var (start, end) = (routed.Points[0], routed.Points[1]);
                    var segmentLength = Js.Hypot(end.X - start.X, end.Y - start.Y);
                    if (segmentLength < 28)
                        problems.Add($"Edge \"{Tpl(edge, "from")}\" -> \"{Tpl(edge, "to")}\" is too short ({N(Js.Round(segmentLength))}px; minimum 28px) — move the nodes farther apart or use a verified orthogonal route with readable clearance.");
                }
            }
        }

        var sink = ctx.Diagnostics;
        var endpointIds = new JsSet<string>();
        foreach (var id in nodes.Keys) endpointIds.Add(id);
        var profile = workflow.Meta.Str("quality_profile");
        problems.AddRange(Geometry.CleanEndpointSideProblems(sink, authoredEdges, endpointIds, PathPoints, "workflow", "edges",
            edge => EdgeSides(edge).FromSide, edge => EdgeSides(edge).ToSide,
            routeHint: "keep automatic routing, or choose fromSide/toSide and via points whose first and final segments cross node borders perpendicularly"));
        problems.AddRange(Geometry.CleanFlowProblems(sink, authoredEdges, nodes.Values, PathPoints, "workflow", "edges", "node",
            routeHint: "adjust fromSide/toSide, set route/via or channel coordinates, or move the node to a clearer lane/column"));
        problems.AddRange(Geometry.CleanCrossingProblems(sink, authoredEdges, endpointIds, PathPoints, "workflow", "edges",
            profile: profile, profileIsAuthoritative: true,
            mergeForwardCollinearWaypoints: V2,
            includeSharedEndpoints: (_, _) => V2,
            warnInStandard: V2,
            onDiagnostic: V2 ? workflowDiagnostics.Add : null,
            routeHint: "adjust route/via, bias, or channel coordinates so the edges use separate lane corridors"));
        problems.AddRange(Geometry.CleanAmbiguousCorridorProblems(sink, authoredEdges, endpointIds, PathPoints, "workflow", "edges",
            profile: profile, profileIsAuthoritative: true,
            includeSharedEndpoints: (_, _) => V2,
            allowShortWorkflowTrunks: V2,
            onDiagnostic: workflowDiagnostics.Add,
            routeHint: "adjust route/via, bias, or channel coordinates so unrelated edges do not visually merge"));
        if (V2)
        {
            var routedRelations = workflow.Edges.Select((relation, relationIndex) =>
            {
                // `{ ...relation, width: relation.width || (emphasis ? 1.8 : 1.4) }`
                var copy = relation.DeepClone().AsObject();
                copy["width"] = Or(relation.Num("width"), relation.Str("variant") == "emphasis" ? 1.8 : 1.4);
                return new RoutedRelation(PathFor(relation).Points, copy, relationIndex);
            }).ToList();
            foreach (var hit in Geometry.CollectArrowheadCollisions(routedRelations, allowShortWorkflowTrunks: true))
            {
                var severity = resolvedQualityProfile == "showcase" ? Severity.Error : Severity.Warning;
                static string Name(ArrowheadEntry entry) => WorkflowEdgeName(entry.Relation);
                var message = $"[composition/arrowhead-collision] workflow arrows \"{Name(hit.Left)}\" and \"{Name(hit.Right)}\" overlap at their destination (clearance {N(hit.Distance)}px; minimum {N(hit.Minimum)}px).";
                workflowDiagnostics.Add(new("composition/arrowhead-collision", severity, message,
                    new JsonObject { ["diagramType"] = "workflow", ["edge"] = Name(hit.Left), ["from"] = hit.Left.Relation["from"]?.DeepClone(), ["to"] = hit.Left.Relation["to"]?.DeepClone() },
                    new JsonObject { ["otherEdge"] = Name(hit.Right), ["distancePx"] = hit.Distance, ["minimumPx"] = hit.Minimum },
                    ["use separate destination sides or ports with clearance for both arrow markers"], null));
                if (severity == Severity.Error) problems.Add(message);
            }
        }
        problems.AddRange(Geometry.CleanBorderRunProblems(sink, authoredEdges, endpointIds, WorkflowCompositionFrames(), PathPoints, "workflow", "edges",
            profile, profileIsAuthoritative: true,
            routeHint: "adjust route/via, bias, or channel coordinates so the edge crosses the lane or group perpendicularly instead of following its border"));
        problems.AddRange(Geometry.CleanRouteRhythmProblems(sink, authoredEdges, endpointIds, PathPoints, "workflow", "edges",
            profile, profileIsAuthoritative: true,
            routeHint: "adjust route/via, bias, or channel coordinates so each turn has a readable run-up"));

        if (workflow.MainPath is { } mainPath)
        {
            for (var stepIndex = 0; stepIndex < mainPath.Count; stepIndex++)
            {
                var id = mainPath[stepIndex];
                if (Has(id)) continue;
                var message = $"mainPath references unknown node \"{BrandMarks.JsString(id)}\".";
                problems.Add(message);
                var candidates = nodes.Keys.ToList();
                Js.Sort(candidates, StableCompare);
                workflowDiagnostics.Add(new("workflow/unknown-endpoint", Severity.Error, message,
                    new JsonObject { ["diagramType"] = "workflow", ["mainPath"] = id?.DeepClone(), ["path"] = $"/mainPath/{N(stepIndex)}" },
                    new JsonObject
                    {
                        ["unknownNodeId"] = id?.DeepClone(),
                        ["availableNodeIds"] = new JsonArray([.. candidates.Select(nodeId => (JsonNode?)nodeId)]),
                        ["fixVerification"] = "each candidate was verified by applying every listed repoint together and recompiling",
                    },
                    [.. candidates
                        .Where(nodeId => AcceptsFix(document => RepointReferences(document, id, nodeId)))
                        .Take(3)
                        .SelectMany(nodeId => UnknownIdReferenceFixes(id, nodeId))],
                    null));
            }
            for (var i = 0; i < mainPath.Count - 1; i++)
            {
                var fromId = mainPath[i];
                var toId = mainPath[i + 1];
                var from = NodeOf(fromId);
                var to = NodeOf(toId);
                if (from is null || to is null) continue;
                var linked = workflow.Edges.Any(edge => StrictEquals(edge["from"], fromId) && StrictEquals(edge["to"], toId));
                if (!linked)
                    problems.Add($"mainPath step \"{BrandMarks.JsString(fromId)}\" -> \"{BrandMarks.JsString(toId)}\" has no matching edge — add the edge or remove the pair from mainPath.");
                if (Col(to) < Col(from))
                    problems.Add($"mainPath step \"{BrandMarks.JsString(fromId)}\" -> \"{BrandMarks.JsString(toId)}\" moves backward from col {Tpl(from.Source, "col")} to {Tpl(to.Source, "col")} — use a return edge outside mainPath for loops.");
            }
        }

        var labelRects = new List<LabelRect>();
        for (var edgeIndex = 0; edgeIndex < workflow.Edges.Count; edgeIndex++)
        {
            if (LabelRectFor(workflow.Edges[edgeIndex], edgeIndex) is { } labelRect) labelRects.Add(labelRect);
        }
        foreach (var rect in labelRects)
        {
            foreach (var node in nodes.Values)
            {
                if (Geometry.RectsOverlap(rect, node, -2))
                    problems.Add($"Label \"{rect.Label}\" overlaps node \"{node.Id}\" — adjust labelDx/labelDy/labelSegment or set labelAt.\n{Geometry.SuggestLabelObstacleFix(rect, rect.Lx ?? double.NaN, rect.Ly ?? double.NaN, node, "node", viewBox, nodes.Values)}");
            }
        }
        for (var i = 0; i < labelRects.Count; i++)
        {
            for (var j = i + 1; j < labelRects.Count; j++)
            {
                if (Geometry.RectsOverlap(labelRects[i], labelRects[j], -2))
                    problems.Add($"Labels \"{labelRects[i].Label}\" and \"{labelRects[j].Label}\" overlap — adjust labelDx/labelDy/labelSegment or route one relationship through a separate corridor.\n{Geometry.SuggestLabelPairFix(labelRects[i], labelRects[j])}");
            }
        }
        problems.AddRange(Geometry.CleanLabelRouteClearanceProblems(sink, authoredEdges, labelRects, endpointIds, PathPoints, "workflow", "edges",
            profile, profileIsAuthoritative: true));

        if (workflow.SchemaVersion == 1)
        {
            if (viewBox[0] < layout.LaneX + layout.LaneW + 16)
                problems.Add($"viewBox width {N(viewBox[0])} clips the {N(layout.LaneW)}px lanes — set meta.viewBox[0] to at least {N(layout.LaneX + layout.LaneW + 16)}.");
            if (lanes.LegendY() + 18 > viewBox[1])
                problems.Add($"Legend exceeds viewBox height {N(viewBox[1])} — set meta.viewBox[1] to at least {N(lanes.LegendY() + 18)}.");
            // v1 only; see collectLabelCanvasOverflow in shared/geometry.mjs.
            problems.AddRange(Geometry.CleanLabelCanvasContainmentProblems(sink, labelRects, viewBox, "workflow", "edges", profile, profileIsAuthoritative: true));
        }

        measuredPlanReady = HasCompleteMeasuredPlan();
        if (problems.Count > 0)
        {
            throw ctx.Diagnostics.ThrowDiagnosticProblems("Workflow layout validation failed", problems,
                subject: new JsonObject { ["diagramType"] = "workflow" }, details: workflowDiagnostics);
        }
    }
}
