using System.Text.Json.Nodes;
using static Archify.Net.WorkflowModel;

namespace Archify.Net;

// workflow-compiler.mjs cluster I2: validateReadableInputsBeforeRouting (:3038-3258), the v2-only typed input diagnostics. Every
// supported fix is verified by recompiling the candidate (AcceptsFix); `.filter(acceptsFix).slice(0, 3)` evaluates every
// candidate before slicing, so each list is materialised before it is cut.
internal sealed partial class WorkflowCompilation
{
    void ValidateReadableInputsBeforeRouting()
    {
        if (workflow.SchemaVersion != 2) return;
        Exception Fail(Diagnostic diagnostic) => ctx.Diagnostics.ThrowDiagnosticError(diagnostic.Message, [diagnostic]);
        static string UnusedId(string @base, JsSet<string> used)
        {
            for (var suffix = 2; ; suffix += 1)
            {
                var candidate = $"{@base}-{N(suffix)}";
                if (!used.Has(candidate)) return candidate;
            }
        }
        static JsSet<string> IdSet(IEnumerable<JsonObject> items)
        {
            var set = new JsSet<string>();
            foreach (var item in items) set.Add(IdKey(item["id"]));
            return set;
        }
        // Sorted by the authored element's own index (an identity lookup, :3047-3055).
        var authoredLanes = workflow.Lanes.ToList();
        Js.Sort(authoredLanes, (left, right) => sourceLanes.Get(left) - sourceLanes.Get(right));
        var authoredNodes = workflow.Nodes.ToList();
        Js.Sort(authoredNodes, (left, right) => sourceNodes.Get(left) - sourceNodes.Get(right));
        var authoredEdges = workflow.Edges.ToList();
        Js.Sort(authoredEdges, (left, right) => sourceEdges.Get(left) - sourceEdges.Get(right));

        var firstLaneIndex = new JsMap<string, int>();
        foreach (var lane in authoredLanes)
        {
            var laneIndex = sourceLanes.Get(lane);
            if (firstLaneIndex.Has(IdKey(lane["id"])))
            {
                var message = $"Workflow lane id \"{Tpl(lane, "id")}\" is duplicated.";
                var replacement = UnusedId(Tpl(lane, "id"), IdSet(workflow.Lanes));
                var canonicalLaneIndex = workflow.Lanes.FindIndex(candidate => ReferenceEquals(candidate, lane));
                List<string> supportedFixes = AcceptsFix(document => document.Lanes[canonicalLaneIndex]["id"] = replacement)
                    ? [$"rename /lanes/{N(laneIndex)}/id to verified unique id \"{replacement}\""] : [];
                throw Fail(new("workflow/duplicate-lane-id", Severity.Error, message,
                    new JsonObject { ["diagramType"] = "workflow", ["lane"] = lane["id"]?.DeepClone(), ["path"] = $"/lanes/{N(laneIndex)}/id" },
                    new JsonObject
                    {
                        ["duplicateLaneId"] = lane["id"]?.DeepClone(),
                        ["firstPath"] = $"/lanes/{N(firstLaneIndex.Get(IdKey(lane["id"])))}/id",
                        ["duplicatePath"] = $"/lanes/{N(laneIndex)}/id",
                    },
                    supportedFixes, null));
            }
            firstLaneIndex.Set(IdKey(lane["id"]), laneIndex);
        }

        var firstNodeIndex = new JsMap<string, int>();
        foreach (var node in authoredNodes)
        {
            var nodeIndex = sourceNodes.Get(node);
            if (firstNodeIndex.Has(IdKey(node["id"])))
            {
                var message = $"Workflow node id \"{Tpl(node, "id")}\" is duplicated.";
                var replacement = UnusedId(Tpl(node, "id"), IdSet(workflow.Nodes));
                var canonicalNodeIndex = workflow.Nodes.FindIndex(candidate => ReferenceEquals(candidate, node));
                List<string> supportedFixes = AcceptsFix(document => document.Nodes[canonicalNodeIndex]["id"] = replacement)
                    ? [$"rename /nodes/{N(nodeIndex)}/id to verified unique id \"{replacement}\""] : [];
                throw Fail(new("workflow/duplicate-node-id", Severity.Error, message,
                    new JsonObject { ["diagramType"] = "workflow", ["node"] = node["id"]?.DeepClone(), ["path"] = $"/nodes/{N(nodeIndex)}/id" },
                    new JsonObject
                    {
                        ["duplicateNodeId"] = node["id"]?.DeepClone(),
                        ["firstPath"] = $"/nodes/{N(firstNodeIndex.Get(IdKey(node["id"])))}/id",
                        ["duplicatePath"] = $"/nodes/{N(nodeIndex)}/id",
                    },
                    supportedFixes, null));
            }
            firstNodeIndex.Set(IdKey(node["id"]), nodeIndex);
        }

        var availableNodeIds = nodes.Keys.ToList();
        Js.Sort(availableNodeIds, StableCompare);
        foreach (var edge in authoredEdges)
        {
            var edgeIndex = sourceEdges.Get(edge);
            foreach (var (field, endpoint) in ((string, string)[])[("from", "source"), ("to", "target")])
            {
                if (Has(edge[field])) continue;
                var unknownId = edge[field];
                var message = $"Workflow edge \"{WorkflowEdgeName(edge)}\" references unknown {endpoint} \"{Tpl(edge, field)}\".";
                var verified = availableNodeIds.Where(nodeId => AcceptsFix(document => RepointReferences(document, unknownId, nodeId))).ToList();
                var supportedFixes = verified.Take(3).SelectMany(nodeId => UnknownIdReferenceFixes(unknownId, nodeId)).ToList();
                throw Fail(new("workflow/unknown-edge-endpoint", Severity.Error, message,
                    new JsonObject
                    {
                        ["diagramType"] = "workflow", ["edge"] = edge["id"]?.DeepClone(), ["path"] = $"/edges/{N(edgeIndex)}/{field}",
                        ["from"] = edge["from"]?.DeepClone(), ["to"] = edge["to"]?.DeepClone(),
                    },
                    new JsonObject
                    {
                        ["endpoint"] = endpoint,
                        ["unknownNodeId"] = unknownId?.DeepClone(),
                        ["availableNodeIds"] = new JsonArray([.. availableNodeIds.Select(nodeId => (JsonNode?)nodeId)]),
                        ["fixVerification"] = "each candidate was verified by applying every listed repoint together and recompiling",
                    },
                    supportedFixes, null));
            }
        }
        var laneIds = IdSet(workflow.Lanes);
        var availableLaneIds = new List<JsonNode?>();
        foreach (var lane in workflow.Lanes)
        {
            if (!availableLaneIds.Any(seen => StrictEquals(seen, lane["id"]))) availableLaneIds.Add(lane["id"]);
        }
        Js.Sort(availableLaneIds, StableCompare);
        var nodeSourceIndexes = new JsMap<string, int>();
        foreach (var node in authoredNodes) nodeSourceIndexes.Set(IdKey(node["id"]), sourceNodes.Get(node));

        var byLane = new JsMap<string, (JsonNode? Lane, List<Box> Nodes)>();
        foreach (var authoredNode in authoredNodes)
        {
            var nodeIndex = sourceNodes.Get(authoredNode);
            var node = NodeOf(authoredNode["id"])!;
            if (!laneIds.Has(IdKey(Lane(node))))
            {
                var message = $"Workflow node \"{node.Id}\" uses unknown lane \"{Tpl(node.Source, "lane")}\".";
                var canonicalNodeIndex = workflow.Nodes.FindIndex(candidate => StrictEquals(candidate["id"], node.Source["id"]));
                var supportedFixes = availableLaneIds.SelectMany(laneId =>
                    AcceptsFix(document => document.Nodes[canonicalNodeIndex]["lane"] = laneId?.DeepClone())
                        ? (string[])[$"set /nodes/{N(nodeIndex)}/lane to verified lane id \"{Js.PrimitiveString(laneId)}\""]
                        : []).ToList();
                throw Fail(new("workflow/unknown-node-lane", Severity.Error, message,
                    new JsonObject { ["diagramType"] = "workflow", ["node"] = node.Source["id"]?.DeepClone(), ["path"] = $"/nodes/{N(nodeIndex)}/lane" },
                    new JsonObject
                    {
                        ["unknownLaneId"] = Lane(node)?.DeepClone(),
                        ["availableLaneIds"] = new JsonArray([.. availableLaneIds.Select(laneId => laneId?.DeepClone())]),
                    },
                    supportedFixes, null));
            }
            // ponytail: dead after the schema gate (col is an integer >= 0 there, and measurement keeps it finite); ported, untested.
            if (!IsInteger(Col(node)) || Col(node) < 0 || Col(node) >= layout.ColXs.Length)
            {
                var message = $"Workflow node \"{node.Id}\" uses column {Tpl(node.Source, "col")}, but valid columns are integers 0..{N(layout.ColXs.Length - 1)}.";
                var canonicalNodeIndex = workflow.Nodes.FindIndex(candidate => StrictEquals(candidate["id"], node.Source["id"]));
                var supportedFixes = Enumerable.Range(0, layout.ColXs.Length).SelectMany(col =>
                    AcceptsFix(document => document.Nodes[canonicalNodeIndex]["col"] = col)
                        ? (string[])[$"set /nodes/{N(nodeIndex)}/col to verified column {N(col)}"]
                        : []).ToList();
                throw Fail(new("workflow/invalid-node-column", Severity.Error, message,
                    new JsonObject { ["diagramType"] = "workflow", ["node"] = node.Source["id"]?.DeepClone(), ["path"] = $"/nodes/{N(nodeIndex)}/col" },
                    new JsonObject { ["actualColumn"] = node.Source["col"]?.DeepClone(), ["minimumColumn"] = 0, ["maximumColumn"] = layout.ColXs.Length - 1 },
                    supportedFixes, null));
            }
            if (!Geometry.IsFinitePoint(node.X, node.Y, node.Cx, node.Cy))
            {
                var message = $"Workflow node \"{node.Id}\" produced non-finite coordinates.";
                // `col: authoredNode.col` is dropped by JSON when undefined; the others are `?? null`.
                var authored = new JsonObject();
                if (authoredNode["col"] is { } authoredCol) authored["col"] = authoredCol.DeepClone();
                authored["width"] = authoredNode["width"]?.DeepClone();
                authored["height"] = authoredNode["height"]?.DeepClone();
                authored["yOffset"] = authoredNode["yOffset"]?.DeepClone();
                throw Fail(new("workflow/non-finite-node-geometry", Severity.Error, message,
                    new JsonObject { ["diagramType"] = "workflow", ["node"] = node.Source["id"]?.DeepClone(), ["path"] = $"/nodes/{N(nodeIndex)}" },
                    new JsonObject
                    {
                        ["measuredRect"] = new JsonObject { ["x"] = node.X, ["y"] = node.Y, ["width"] = node.Width, ["height"] = node.Height },
                        ["authored"] = authored,
                    },
                    [], null));
            }
            var key = IdKey(Lane(node));
            byLane.Set(key, (Lane(node), [.. byLane.Get(key).Nodes ?? [], node]));
        }
        // Every overlapping pair is reported at once. Failing on the first pair
        // made an author who stacked two groups of nodes fix one, rerun, and only
        // then learn about the other; the draft already contained both.
        var overlaps = new List<Diagnostic>();
        foreach (var (lane, laneNodes) in byLane.Values)
        {
            for (var left = 0; left < laneNodes.Count; left += 1)
            {
                for (var right = left + 1; right < laneNodes.Count; right += 1)
                {
                    if (!Geometry.RectsOverlap(laneNodes[left], laneNodes[right], 8)) continue;
                    var leftNode = laneNodes[left];
                    var rightNode = laneNodes[right];
                    var rightIndex = nodeSourceIndexes.Get(IdKey(rightNode.Source["id"]));
                    var canonicalNodeIndex = workflow.Nodes.FindIndex(candidate => StrictEquals(candidate["id"], rightNode.Source["id"]));
                    var supportedFixes = Enumerable.Range(0, layout.ColXs.Length).SelectMany(col =>
                        col == Col(rightNode) ? []
                        : AcceptsFix(document => document.Nodes[canonicalNodeIndex]["col"] = col)
                            ? (string[])[$"set /nodes/{N(rightIndex)}/col to verified free column {N(col)}"]
                            : []).ToList();
                    var message = $"Workflow nodes \"{leftNode.Id}\" and \"{rightNode.Id}\" are less than 8px apart in lane \"{Js.PrimitiveString(lane)}\".";
                    static JsonObject Entry(Box node) => new()
                    {
                        ["id"] = node.Source["id"]?.DeepClone(),
                        ["rect"] = new JsonObject { ["x"] = node.X, ["y"] = node.Y, ["width"] = node.Width, ["height"] = node.Height },
                    };
                    overlaps.Add(new("workflow/node-overlap", Severity.Error, message,
                        new JsonObject { ["diagramType"] = "workflow", ["node"] = rightNode.Source["id"]?.DeepClone(), ["path"] = $"/nodes/{N(rightIndex)}" },
                        new JsonObject
                        {
                            ["lane"] = lane?.DeepClone(),
                            ["minimumClearancePx"] = 8,
                            ["nodes"] = new JsonArray(Entry(leftNode), Entry(rightNode)),
                        },
                        supportedFixes, null));
                }
            }
        }
        if (overlaps.Count > 0)
        {
            throw ctx.Diagnostics.ThrowDiagnosticError(overlaps.Count == 1
                ? overlaps[0].Message
                : $"Workflow node overlap:\n- {string.Join("\n- ", overlaps.Select(entry => entry.Message))}", overlaps);
        }
    }
}
