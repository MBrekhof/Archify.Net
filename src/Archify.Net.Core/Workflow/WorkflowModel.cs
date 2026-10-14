using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Archify.Net;

// workflow-compiler.mjs:170 `automaticNodeWidths` (R8): a WeakMap keyed by the node object, rewritten by every
// canonicalReadableWorkflow. One per top-level compile, handed to the nested ones; never a field on WorkflowDoc.
internal sealed class WorkflowWidths
{
    readonly ConditionalWeakTable<JsonObject, StrongBox<double>> table = new();
    public double? Get(JsonObject node) => table.TryGetValue(node, out var width) ? width.Value : null;
    public void Set(JsonObject node, double width) => table.AddOrUpdate(node, new StrongBox<double>(width));
    public void Remove(JsonObject node) => table.Remove(node);   // `delete`
}

// :114-121. `request` is upstream's request object (kind, fromCol/toCol/minimum or minimum, edge, from, to, attemptedCandidateFamilies,
// candidateCount), read back by compileWithLayoutFeedback (:5758-5792) and feedbackFailure (:5646-5664).
internal sealed class WorkflowLayoutFeedback(JsonObject request)
    : Exception($"Workflow layout requires {WorkflowModel.Tpl(request, "kind")} feedback.")
{
    public JsonObject Request { get; } = request;
    public string? Kind => Request.Str("kind");
}

// createReadableLayout's result (:642-660); createLegacyLayout (:122-136) leaves the readable-only fields null, which is what
// upstream's `layout.laneHeights?.[i] ?? layout.laneH` style reads see.
internal sealed record ReadableLayout(string Contract, double LaneX, double LaneY, double LaneW, double LaneH, List<double>? LaneHeights,
    double LaneGap, double LaneTitleH, List<double>? GroupHeaderHeights, List<double>? GroupFooterHeights, double[] ColXs, double NodeW,
    double NodeH, double DefaultViewBoxWidth, JsSet<string>? ChannelLabelEdgeKeys, List<string>? WidthContributors, List<string>? HeightContributors);

// workflow-compiler.mjs clusters A (:80-232), B (:233-661) and C (:672-856): module constants, node helpers, the readable-v2
// rank layout, canonical order and the semantic contract. compilerFailure (:663) belongs to the compile result (Task 7).
internal static class WorkflowModel
{
    public static readonly double[] LegacyColumnCenters = [88, 220, 300, 430, 500, 625];
    public static readonly string[] ReadableCandidateCostPriority = [
        "properCrossingCount", "unrelatedBendContactCount", "sharedCorridorPx", "automaticForwardReversePx", "labelRouteClearanceDeficit",
        "laterArrivalBlockCount", "oppositeParallelDeficit", "interiorPreferred28Deficit", "excessiveVerticalTravelPx", "bendCount",
        "stretchMilli", "canvasGrowthPx", "portDisplacementMilli", "legacyCoordinateDisplacement", "stableCandidateOrdinal",
    ];
    public const int MaxReadableLayoutFeedbackRounds = 3;
    public const double GroupFrameTopInset = 8, GroupFrameBottomInset = 4, GroupLabelBaselineOffset = -2, GroupLabelMaskAscent = 10,
        GroupLabelMaskH = 14, GroupNodeInset = 4;
    public static class HardRouteRhythm { public const double Direct = 28, Endpoint = 8, Interior = 16; }

    // ---------------------------------------------------------------- JS value semantics shared by the workflow files
    // ponytail: undefined and null collapse to C# null in these helpers; ids, lanes and endpoints are schema-required strings.

    // JS `a || b` on a number read from JSON: missing, 0 and NaN fall through.
    internal static double Or(double? first, double second) => first is { } f && f != 0 && !double.IsNaN(f) ? f : second;
    // JS `x || y` where both are numbers: the first unless it is 0 or NaN (comparator chains).
    internal static double Lor(double first, double second) => first != 0 && !double.IsNaN(first) ? first : second;
    internal static bool Truthy(double value) => value != 0 && !double.IsNaN(value);
    internal static bool IsInteger(double? value) => value is { } d && double.IsFinite(d) && d == Math.Floor(d);
    // `colXs[col]`: undefined (→ NaN in arithmetic) unless col is an index of the array.
    internal static double ColAt(IReadOnlyList<double> colXs, double? col) => IsInteger(col) && col >= 0 && col < colXs.Count ? colXs[(int)col!.Value] : double.NaN;
    // `${object.key}`: "undefined" for a missing key.
    internal static string Tpl(JsonObject? o, string key) => o is not null && o.TryGetPropertyValue(key, out var value) ? BrandMarks.JsString(value) : "undefined";
    // `a.x || a.y` on two text fields, rendered into a template.
    internal static string TplOr(JsonObject o, string first, string second) => o[first].Truthy() ? BrandMarks.JsString(o[first]) : Tpl(o, second);
    // A Map/Set key for a JSON id value: the string itself, a tagged form for anything else.
    internal static string IdKey(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : "\u0000" + Js.Stringify(value);
    // JS `===` on two JSON values.
    internal static bool StrictEquals(JsonNode? a, JsonNode? b) => (a, b) switch
    {
        (null, null) => true,
        (JsonValue x, JsonValue y) when x.TryGetValue<string>(out var p) => y.TryGetValue<string>(out var q) && p == q,
        (JsonValue x, JsonValue y) when x.TryGetValue<bool>(out var p) => y.TryGetValue<bool>(out var q) && p == q,
        (JsonValue x, JsonValue y) => x.GetValueKind() == System.Text.Json.JsonValueKind.Number && y.GetValueKind() == System.Text.Json.JsonValueKind.Number && x.D() == y.D(),
        _ => ReferenceEquals(a, b),
    };
    // `Number(value) || 0`.
    internal static double NumberOr0(JsonNode? value)
    {
        var number = value switch
        {
            null => 0,   // Number(null) and Number(undefined) || 0
            JsonValue v when v.TryGetValue<string>(out var s) => Js.ToNumber(s),
            JsonValue v when v.TryGetValue<bool>(out var b) => b ? 1 : 0,
            JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.Number => v.D(),
            _ => double.NaN,   // ponytail: objects/arrays; the schema types yOffset as a number
        };
        return Or(number, 0);
    }
    internal static double MaxOf(IEnumerable<double> values, double seed)
    {
        var max = seed;
        foreach (var value in values) max = Math.Max(max, value);
        return max;
    }
    internal static double Col(JsonObject node) => node.Num("col") ?? double.NaN;
    static IEnumerable<string> StringsOf(JsonNode? value) => value.AsArrayOrEmpty().Select(BrandMarks.JsString);

    // ---------------------------------------------------------------- A (:108-231)

    public static double HardRouteSegmentMinimum(IReadOnlyList<Pt> points, int index) => points.Count == 2 ? HardRouteRhythm.Direct
        : index == 0 || index == points.Count - 2 ? HardRouteRhythm.Endpoint : HardRouteRhythm.Interior;

    public static ReadableLayout CreateLegacyLayout() => new("fixed-v1", 40, 52, 640, 104, null, 20, 30, null, null,
        [.. LegacyColumnCenters], 92, 52, 720, null, null, null);

    public static bool HasAbsoluteWorkflowPins(WorkflowDoc w) => w.Edges.Any(edge => edge["via"] is JsonArray || edge["labelAt"] is JsonArray
        || edge.ContainsKey("channelX") || edge.ContainsKey("channelY"));

    public static bool HasVerticalStack(WorkflowDoc w)
    {
        var offsetsByLaneAndColumn = new JsMap<string, JsSet<double>>();
        foreach (var node in w.Nodes)
        {
            if (!IsInteger(node.Num("col"))) continue;
            var key = $"{Tpl(node, "lane")}\u0000{Tpl(node, "col")}";
            var offsets = offsetsByLaneAndColumn.Get(key) ?? new JsSet<double>();
            offsets.Add(NumberOr0(node["yOffset"]));   // `|| 0` never yields -0, so SameValueZero is plain equality here
            if (offsets.Count > 1) return true;
            offsetsByLaneAndColumn.Set(key, offsets);
        }
        return false;
    }

    public static bool UsesIndependentLaneMeasurement(WorkflowDoc w) =>
        HasVerticalStack(w) && !w.Meta["viewBox"].Truthy() && !HasAbsoluteWorkflowPins(w);

    // A readable-v2 node without an authored width grows to fit its label, up to 200px; a sublabel or tag that would not fit even
    // at 6px also grows it to fit at its preferred 8px or 7px. null is upstream's `null` (no growth past 92px).
    public static double? AutomaticNodeWidth(JsonObject node)
    {
        var labelWidth = Math.Max(92, Math.Ceiling((Utils.TextUnits(node.Str("label")) * 6.8 - 6) / 4) * 4);
        var fitted = labelWidth;
        foreach (var (text, size) in new[] { (node.Str("sublabel"), 8.0), (node.Str("tag"), 7.0) })
        {
            if (string.IsNullOrEmpty(text) || TextFit.MinimumNodeTextWidth(text, 6) <= TextFit.AvailableNodeTextWidth(labelWidth)) continue;
            fitted = Math.Max(fitted, Math.Ceiling((TextFit.MinimumNodeTextWidth(text, size) + labelWidth - TextFit.AvailableNodeTextWidth(labelWidth)) / 4) * 4);
        }
        return fitted > 92 ? Math.Min(200, fitted) : null;
    }

    public static double AuthoredNodeWidth(JsonObject node, WorkflowWidths widths) =>
        node.Num("width") is { } width && double.IsFinite(width) ? width : widths.Get(node) ?? 92;

    static string NodeWidthContributor(JsonObject node, WorkflowWidths widths) => $"node {Tpl(node, "id")} width {Js.Num(AuthoredNodeWidth(node, widths))}px";

    public static double AuthoredNodeHeight(JsonObject node) =>
        node.Num("height") is { } height && double.IsFinite(height) ? height : node["tag"].Truthy() ? 68 : 52;

    public static double WorkflowLabelWidth(string? label) => Math.Max(30, Utils.TextUnits(label) * 4.8 + 10);

    public static (double X, double Width, double Cx) ReadableGroupBounds(WorkflowDoc w, JsonObject group, IReadOnlyList<double> colXs, WorkflowWidths widths)
    {
        var fromCol = group.Num("fromCol");
        var toCol = group.Num("toCol");
        if (!IsInteger(fromCol) || !IsInteger(toCol) || fromCol < 0 || fromCol > toCol || toCol >= colXs.Count) return (0, 0, 0);
        var start = colXs[(int)fromCol!.Value] - 50;
        var end = colXs[(int)toCol!.Value] + 50;
        var naturalWidth = end - start;
        var minimumWidth = Utils.TextUnits(group.Str("label")) * 5.6 + 20;
        var width = Math.Max(naturalWidth, minimumWidth);
        var left = fromCol == toCol && width > naturalWidth ? start : (start + end - width) / 2;
        var right = left + width;
        foreach (var node in w.Nodes)
        {
            var col = node.Num("col");
            if (!StrictEquals(node["lane"], group["lane"]) || !IsInteger(col) || col < fromCol || col > toCol || col < 0 || col >= colXs.Count) continue;
            var halfWidth = AuthoredNodeWidth(node, widths) / 2;
            left = Math.Min(left, colXs[(int)col!.Value] - halfWidth - GroupNodeInset);
            right = Math.Max(right, colXs[(int)col.Value] + halfWidth + GroupNodeInset);
        }
        width = right - left;
        return (left, width, left + width / 2);
    }

    static bool VerticalIntervalsOverlap(JsonObject a, JsonObject b, double clearance) =>
        Math.Abs(NumberOr0(a["yOffset"]) - NumberOr0(b["yOffset"])) < AuthoredNodeHeight(a) / 2 + AuthoredNodeHeight(b) / 2 + clearance;

    // ---------------------------------------------------------------- B (:233-661)

    // `{ from, to, minimum, contributors }`; from/to stay doubles because feedback keys and node cols are filtered after the fact.
    sealed record RankConstraint(double From, double To, double Minimum, IReadOnlyList<string> Contributors);

    // `layoutFeedback = {}`: null is the default. Keys: rankGapMinimums, rankGapContributors, laneGapMin, laneGapContributors.
    public static ReadableLayout CreateReadableLayout(WorkflowDoc w, JsonObject? feedback, WorkflowWidths widths)
    {
        const int columnCount = 6;
        const double baselinePitch = 120, columnStart = 94, channelDetourBudgetPx = 4 * 28;
        const int maxLayoutIterations = 3;
        feedback ??= [];
        var constraints = new List<RankConstraint>();
        var feedbackConstraints = new List<RankConstraint>();
        var channelLabelEdgeKeys = new JsSet<string>();
        var widthContributors = new JsSet<string>();
        var heightContributors = new JsSet<string>();
        var nodes = w.Nodes;
        double nodeClearance = WorkflowMigrationGeometry.PreservesWorkflowAbsoluteGeometry(w) ? 8 : 32;
        var nodesById = new JsMap<string, JsonObject>();
        foreach (var node in nodes) nodesById.Set(IdKey(node["id"]), node);
        double W(JsonObject node) => AuthoredNodeWidth(node, widths);
        string Nwc(JsonObject node) => NodeWidthContributor(node, widths);

        for (var col = 0; col < columnCount - 1; col++) constraints.Add(new(col, col + 1, baselinePitch, []));
        // Object.entries(...).sort(): the default sort compares String([key, value]).
        var rankGapEntries = (feedback.Obj("rankGapMinimums") ?? []).Select(kv => (kv.Key, kv.Value)).ToList();
        Js.Sort(rankGapEntries, (a, b) => string.CompareOrdinal(a.Key + "," + (a.Value is null ? "" : BrandMarks.JsString(a.Value)),
            b.Key + "," + (b.Value is null ? "" : BrandMarks.JsString(b.Value))));
        foreach (var (key, value) in rankGapEntries)
        {
            var parts = key.Split(':');   // `key.split(':').map(Number)`; a missing `to` is undefined
            var from = Js.ToNumber(parts[0]);
            double? to = parts.Length > 1 ? Js.ToNumber(parts[1]) : null;
            var minimum = value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number ? v.D() : double.NaN;
            var authoredContributors = feedback.Obj("rankGapContributors")?[key];
            constraints.Add(new(from, to ?? double.NaN, minimum, authoredContributors.Truthy()
                ? [.. StringsOf(authoredContributors)]
                : [$"rank {Js.Num(from)}→{(to is { } t ? Js.Num(t) : "undefined")} route clearance"]));
        }

        for (var leftIndex = 0; leftIndex < nodes.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < nodes.Count; rightIndex++)
            {
                var leftNode = nodes[leftIndex];
                var rightNode = nodes[rightIndex];
                if (!StrictEquals(leftNode["lane"], rightNode["lane"]) || StrictEquals(leftNode["col"], rightNode["col"])) continue;
                if (!VerticalIntervalsOverlap(leftNode, rightNode, 8)) continue;
                var fromNode = Col(leftNode) < Col(rightNode) ? leftNode : rightNode;
                var toNode = ReferenceEquals(fromNode, leftNode) ? rightNode : leftNode;
                // Unpinned drafts reserve a route corridor; absolute assertions keep
                // their existing rank plan, including migration planning projections.
                constraints.Add(new(Col(fromNode), Col(toNode), W(fromNode) / 2 + nodeClearance + W(toNode) / 2, [
                    $"rank {Tpl(fromNode, "col")}→{Tpl(toNode, "col")} node width clearance",
                    Nwc(fromNode),
                    Nwc(toNode),
                ]));
            }
        }

        foreach (var edge in w.Edges)
        {
            var fromNode = nodesById.Get(IdKey(edge["from"]));
            var toNode = nodesById.Get(IdKey(edge["to"]));
            if (fromNode is null || toNode is null || !StrictEquals(fromNode["lane"], toNode["lane"]) || StrictEquals(fromNode["col"], toNode["col"])) continue;
            var route = RouteOrAuto(edge);
            if (edge["via"].Truthy() || edge.ContainsKey("channelX") || edge.ContainsKey("channelY") || (route != "auto" && route != "straight")) continue;
            if (NumberOr0(fromNode["yOffset"]) != NumberOr0(toNode["yOffset"])) continue;
            var earlier = Col(fromNode) < Col(toNode) ? fromNode : toNode;
            var later = ReferenceEquals(earlier, fromNode) ? toNode : fromNode;
            var labeled = edge["label"].Truthy() && !edge["labelAt"].Truthy();
            var labeledDirectClearance = labeled ? Math.Max(28, WorkflowLabelWidth(edge.Str("label")) + 8) : 28;
            var directLabelExpansionCost = Math.Max(0, labeledDirectClearance - 28);
            var canUseAutomaticLabelChannel = labeled && route == "auto" && !edge["fromSide"].Truthy() && !edge["toSide"].Truthy()
                && !edge.ContainsKey("channelX") && !edge.ContainsKey("channelY");
            var preferLabelChannel = canUseAutomaticLabelChannel && directLabelExpansionCost > channelDetourBudgetPx;
            if (preferLabelChannel) channelLabelEdgeKeys.Add(StableValueKey(edge));
            var rank = $"rank {Tpl(earlier, "col")}→{Tpl(later, "col")}";
            constraints.Add(new(Col(earlier), Col(later), W(earlier) / 2 + 28 + W(later) / 2, [
                $"{rank} direct clearance",
                $"{rank} node width clearance",
                Nwc(earlier),
                Nwc(later),
            ]));
            if (!preferLabelChannel && labeledDirectClearance > 28)
            {
                var labelConstraintMinimum = W(earlier) / 2 + labeledDirectClearance + W(later) / 2;
                feedbackConstraints.Add(new(Col(earlier), Col(later), labelConstraintMinimum, [
                    $"{rank} direct clearance",
                    $"edge {WorkflowEdgeName(edge)} label mask",
                    Nwc(earlier),
                    Nwc(later),
                ]));
            }
        }

        // Phases (:337-358) and groups (:360-381) reserve their label span: (label width allowance, label padding).
        void SpanConstraints(List<JsonObject>? spans, string kind, double padding, double allowance)
        {
            foreach (var span in spans ?? [])
            {
                var fromCol = span.Num("fromCol");
                var toCol = span.Num("toCol");
                if (!IsInteger(fromCol) || !IsInteger(toCol) || fromCol < 0 || fromCol > toCol || toCol >= columnCount) continue;
                var minimumWidth = Utils.TextUnits(span.Str("label")) * 5.6 + padding;
                string[] contributors = [$"{kind} {TplOr(span, "id", "label")} label span"];
                if (fromCol == toCol)
                {
                    if (toCol < columnCount - 1)
                        constraints.Add(new(toCol!.Value, toCol.Value + 1, baselinePitch + Math.Max(0, minimumWidth - allowance), contributors));
                    continue;
                }
                constraints.Add(new(fromCol!.Value, toCol!.Value, Math.Max(0, minimumWidth - allowance), contributors));
            }
        }
        SpanConstraints(w.Phases, "phase", 8, 92);
        SpanConstraints(w.Groups, "group", 20, 100);

        var activeConstraints = new List<RankConstraint>(constraints);
        double[] colXs = [];
        JsSet<string>[] colProvenance = [];
        for (var iteration = 0; iteration < maxLayoutIterations; iteration++)
        {
            colXs = [.. Enumerable.Range(0, columnCount).Select(col => columnStart + col * baselinePitch)];
            colProvenance = [.. Enumerable.Range(0, columnCount).Select(_ => new JsSet<string>())];
            var orderedConstraints = activeConstraints.Where(c => IsInteger(c.From) && IsInteger(c.To)
                && c.From >= 0 && c.From < c.To && c.To < columnCount && double.IsFinite(c.Minimum)).ToList();
            Js.Sort(orderedConstraints, (a, b) => Lor(a.To - b.To, Lor(a.From - b.From, a.Minimum - b.Minimum)));
            for (var to = 1; to < columnCount; to++)
            {
                foreach (var constraint in orderedConstraints)
                {
                    if (constraint.To != to) continue;
                    var candidate = colXs[(int)constraint.From] + constraint.Minimum;
                    var candidateProvenance = Union(colProvenance[(int)constraint.From], constraint.Contributors);
                    if (candidate > colXs[to] + 0.0001)
                    {
                        colXs[to] = candidate;
                        colProvenance[to] = candidateProvenance;
                    }
                    else if (Math.Abs(candidate - colXs[to]) <= 0.0001 && candidate > columnStart + to * baselinePitch + 0.0001)
                    {
                        foreach (var contributor in candidateProvenance) colProvenance[to].Add(contributor);
                    }
                }
            }
            if (iteration > 0 || feedbackConstraints.Count == 0) break;
            activeConstraints = [.. activeConstraints, .. feedbackConstraints];
        }

        var firstRankNodes = nodes.Where(node => node.Num("col") == 0).ToList();
        var firstExtent = MaxOf(firstRankNodes.Select(node => W(node) / 2), 46);
        const double leftInset = 8;
        var leftShift = Math.Max(0, 40 + leftInset + firstExtent - colXs[0]);
        if (Truthy(leftShift))
        {
            for (var col = 0; col < colXs.Length; col++) colXs[col] += leftShift;
            foreach (var node in firstRankNodes)
            {
                if (Math.Abs(W(node) / 2 - firstExtent) > 0.0001) continue;
                foreach (var provenance in colProvenance) provenance.Add(Nwc(node));
            }
        }

        var unpinnedTopEndpointIds = new JsSet<string>();
        foreach (var edge in w.Edges)
        {
            var preservesHorizontalPins = edge["via"] is JsonArray || edge.ContainsKey("channelX");
            if (preservesHorizontalPins) continue;
            if (edge.Str("fromSide") == "top") unpinnedTopEndpointIds.Add(IdKey(edge["from"]));
            if (edge.Str("toSide") == "top") unpinnedTopEndpointIds.Add(IdKey(edge["to"]));
        }
        var laneOrder = new JsMap<string, int>();
        for (var index = 0; index < w.Lanes.Count; index++) laneOrder.Set(IdKey(w.Lanes[index]["id"]), index);
        double laneHeaderShift = 0;
        var laneHeaderShiftContributors = new JsSet<string>();
        foreach (var nodeId in unpinnedTopEndpointIds)
        {
            var node = nodesById.Get(nodeId);
            var col = node?.Num("col");
            if (node is null || !IsInteger(col) || col < 0 || col >= columnCount) continue;
            var laneKey = IdKey(node["lane"]);
            if (!laneOrder.Has(laneKey)) continue;   // `asArray(workflow.lanes)[undefined]`
            var lanePosition = laneOrder.Get(laneKey);
            var lane = w.Lanes[lanePosition];
            var prefix = lane.Str("variant") == "exception" ? "EX" : Js.Num(lanePosition + 1).PadLeft(2, '0');
            var laneHeaderRight = 40 + 14 + Utils.TextUnits($"{prefix} / {Tpl(lane, "label")}") * 6.2;
            var requiredShift = laneHeaderRight + 2 - colXs[(int)col!.Value];
            if (requiredShift > laneHeaderShift + 0.0001)
            {
                laneHeaderShift = requiredShift;
                laneHeaderShiftContributors = [];
                laneHeaderShiftContributors.Add($"lane {Tpl(lane, "id")} label width");
            }
            else if (requiredShift > 0 && Math.Abs(requiredShift - laneHeaderShift) <= 0.0001)
            {
                laneHeaderShiftContributors.Add($"lane {Tpl(lane, "id")} label width");
            }
        }
        if (laneHeaderShift > 0)
        {
            for (var col = 0; col < colXs.Length; col++) colXs[col] += laneHeaderShift;
            foreach (var provenance in colProvenance)
                foreach (var contributor in laneHeaderShiftContributors) provenance.Add(contributor);
        }

        double measuredContentLeftShift = 0;
        foreach (var edge in w.Edges)
        {
            if (!channelLabelEdgeKeys.Has(StableValueKey(edge))) continue;
            var fromNode = nodesById.Get(IdKey(edge["from"]));
            var toNode = nodesById.Get(IdKey(edge["to"]));
            if (fromNode is null || toNode is null) continue;
            var labelCenter = (ColAt(colXs, fromNode.Num("col")) + ColAt(colXs, toNode.Num("col"))) / 2;
            var labelLeft = labelCenter - WorkflowLabelWidth(edge.Str("label")) / 2;
            measuredContentLeftShift = Math.Max(measuredContentLeftShift, 16 - labelLeft);
        }
        foreach (var phase in w.Phases ?? [])
        {
            var fromCol = phase.Num("fromCol");
            var toCol = phase.Num("toCol");
            if (!IsInteger(fromCol) || !IsInteger(toCol)) continue;
            var width = Math.Max(ColAt(colXs, toCol) - ColAt(colXs, fromCol) + 92, Utils.TextUnits(phase.Str("label")) * 5.6 + 8);
            var left = fromCol == toCol
                ? ColAt(colXs, fromCol) - 46
                : (ColAt(colXs, fromCol) + ColAt(colXs, toCol) - width) / 2;
            measuredContentLeftShift = Math.Max(measuredContentLeftShift, 16 - left);
        }
        foreach (var group in w.Groups ?? [])
        {
            if (!IsInteger(group.Num("fromCol")) || !IsInteger(group.Num("toCol"))) continue;
            var bounds = ReadableGroupBounds(w, group, colXs, widths);
            measuredContentLeftShift = Math.Max(measuredContentLeftShift, 44 - bounds.X);
        }
        if (measuredContentLeftShift > 0)
        {
            for (var col = 0; col < colXs.Length; col++) colXs[col] += measuredContentLeftShift;
        }

        var rightmost = colXs[^1] + 50;
        var rightmostContributors = Union(colProvenance[^1], []);
        foreach (var node in nodes)
        {
            var col = node.Num("col");
            if (!IsInteger(col) || col < 0 || col >= columnCount) continue;
            var nodeRight = colXs[(int)col!.Value] + W(node) / 2;
            var nodeContributors = Union(colProvenance[(int)col.Value], [Nwc(node)]);
            if (nodeRight > rightmost + 0.0001)
            {
                rightmost = nodeRight;
                rightmostContributors = nodeContributors;
            }
            else if (Math.Abs(nodeRight - rightmost) <= 0.0001)
            {
                foreach (var contributor in nodeContributors) rightmostContributors.Add(contributor);
            }
        }
        foreach (var group in w.Groups ?? [])
        {
            var fromCol = group.Num("fromCol");
            var toCol = group.Num("toCol");
            if (!IsInteger(fromCol) || !IsInteger(toCol)) continue;
            var bounds = ReadableGroupBounds(w, group, colXs, widths);
            var groupRight = bounds.X + bounds.Width;
            var groupContributors = Union(colProvenance[(int)fromCol!.Value], [
                .. colProvenance[(int)toCol!.Value],
                $"group {TplOr(group, "id", "label")} label span",
                .. nodes.Where(node => StrictEquals(node["lane"], group["lane"]) && node.Num("col") >= fromCol && node.Num("col") <= toCol).Select(Nwc),
            ]);
            if (groupRight > rightmost + 0.0001)
            {
                rightmost = groupRight;
                rightmostContributors = groupContributors;
            }
            else if (Math.Abs(groupRight - rightmost) <= 0.0001)
            {
                foreach (var contributor in groupContributors) rightmostContributors.Add(contributor);
            }
        }
        double laneLabelWidth = 0;
        JsonObject? widestLane = null;
        for (var index = 0; index < w.Lanes.Count; index++)
        {
            var width = Utils.TextUnits($"{Js.Num(index + 1).PadLeft(2, '0')} / {Tpl(w.Lanes[index], "label")}") * 6.2 + 30;
            if (width > laneLabelWidth) (laneLabelWidth, widestLane) = (width, w.Lanes[index]);
        }
        var rightmostLaneWidth = Math.Ceiling(rightmost - 40 + 8);
        var laneW = Math.Max(Math.Max(640, rightmostLaneWidth), Math.Ceiling(laneLabelWidth));
        if (laneW > 640)
        {
            if (rightmostLaneWidth == laneW)
            {
                foreach (var contributor in rightmostContributors) widthContributors.Add(contributor);
            }
            if (Math.Ceiling(laneLabelWidth) == laneW && widestLane is not null)
            {
                widthContributors.Add($"lane {TplOr(widestLane, "id", "label")} label width");
            }
        }
        // verticalExtent(laneId): `laneId !== undefined` filters by lane; the shared call passes nothing.
        (double Maximum, JsSet<string> Contributors) VerticalExtent(JsonObject? lane)
        {
            double maximum = 0;
            var contributors = new JsSet<string>();
            foreach (var node in nodes)
            {
                if (lane is not null && lane.ContainsKey("id") && !StrictEquals(node["lane"], lane["id"])) continue;
                var yOffset = NumberOr0(node["yOffset"]);
                var extent = AuthoredNodeHeight(node) / 2 + Math.Abs(yOffset);
                var contributor = $"node {Tpl(node, "id")} height {Js.Num(AuthoredNodeHeight(node))}px{(yOffset != 0 ? $" with yOffset {Js.Num(yOffset)}px" : "")}";
                if (extent > maximum + 0.0001)
                {
                    maximum = extent;
                    contributors = [];
                    contributors.Add(contributor);
                }
                else if (Math.Abs(extent - maximum) <= 0.0001)
                {
                    contributors.Add(contributor);
                }
            }
            return (maximum, contributors);
        }
        var sharedVerticalExtent = VerticalExtent(null);
        var laneH = 30 + Math.Max(74, Math.Ceiling(sharedVerticalExtent.Maximum * 2 + 8));
        var independentLaneMeasurement = UsesIndependentLaneMeasurement(w);
        var laneBaseHeights = new List<double>();
        foreach (var lane in w.Lanes)
        {
            if (!independentLaneMeasurement) { laneBaseHeights.Add(laneH); continue; }
            var ownVerticalExtent = VerticalExtent(lane);
            var height = 30 + Math.Max(74, Math.Ceiling(ownVerticalExtent.Maximum * 2 + 8));
            if (height > 104)
            {
                foreach (var contributor in ownVerticalExtent.Contributors) heightContributors.Add(contributor);
            }
            laneBaseHeights.Add(height);
        }
        if (!independentLaneMeasurement && laneH > 104)
        {
            foreach (var contributor in sharedVerticalExtent.Contributors) heightContributors.Add(contributor);
        }
        var groupsByLane = new JsMap<string, List<JsonObject>>();
        foreach (var group in w.Groups ?? [])
            groupsByLane.Set(IdKey(group["lane"]), [.. groupsByLane.Get(IdKey(group["lane"])) ?? [], group]);
        var groupHeaderHeights = new List<double>();
        var groupFooterHeights = new List<double>();
        for (var laneIndex = 0; laneIndex < w.Lanes.Count; laneIndex++)
        {
            var baseContentH = laneBaseHeights[laneIndex] - 30;
            double header = 0;
            double footer = 0;
            foreach (var group in groupsByLane.Get(IdKey(w.Lanes[laneIndex]["id"])) ?? [])
            {
                var bounds = ReadableGroupBounds(w, group, colXs, widths);
                var labelLeft = bounds.X + 10;
                var labelRight = labelLeft + Utils.TextUnits(group.Str("label")) * 5.6;
                var fromCol = group.Num("fromCol");
                var toCol = group.Num("toCol");
                foreach (var node in nodes)
                {
                    var col = node.Num("col");
                    if (!StrictEquals(node["lane"], group["lane"]) || !IsInteger(col) || col < fromCol || col > toCol || col < 0 || col >= colXs.Length) continue;
                    var halfWidth = W(node) / 2;
                    var nodeLeft = colXs[(int)col!.Value] - halfWidth;
                    var nodeRight = colXs[(int)col.Value] + halfWidth;
                    var overlapsLabel = nodeRight > labelLeft && nodeLeft < labelRight;
                    var topOffset = (baseContentH - AuthoredNodeHeight(node)) / 2 + NumberOr0(node["yOffset"]);
                    double minimumTopOffset = overlapsLabel ? 11 : 9;
                    header = Math.Max(header, Math.Ceiling(minimumTopOffset - topOffset));
                    var bottomMargin = baseContentH - GroupFrameBottomInset - topOffset - AuthoredNodeHeight(node);
                    footer = Math.Max(footer, Math.Ceiling(1 - bottomMargin));
                }
            }
            groupHeaderHeights.Add(Math.Max(0, header));
            groupFooterHeights.Add(Math.Max(0, footer));
        }
        var laneHeights = groupHeaderHeights.Select((header, index) => laneBaseHeights[index] + header + groupFooterHeights[index]).ToList();
        var laneGap = Math.Max(20, Math.Ceiling(Or(feedback.Num("laneGapMin"), 0)));
        for (var index = 0; index < groupHeaderHeights.Count; index++)
        {
            if (!Truthy(groupHeaderHeights[index])) continue;
            heightContributors.Add($"lane {TplOr(w.Lanes[index], "id", "label")} group label clearance {Js.Num(groupHeaderHeights[index])}px");
        }
        for (var index = 0; index < groupFooterHeights.Count; index++)
        {
            if (!Truthy(groupFooterHeights[index])) continue;
            heightContributors.Add($"lane {TplOr(w.Lanes[index], "id", "label")} group frame containment {Js.Num(groupFooterHeights[index])}px");
        }
        if (laneGap > 20)
        {
            foreach (var contributor in StringsOf(feedback["laneGapContributors"])) heightContributors.Add(contributor);
        }
        var requiredWidth = 40 + laneW + 16;

        return new("readable-v2", 40, 52, laneW, laneH, laneHeights, laneGap, 30, groupHeaderHeights, groupFooterHeights, colXs, 92, 52,
            requiredWidth, channelLabelEdgeKeys, SortedStable(widthContributors), SortedStable(heightContributors));
    }

    // `new Set([...a, ...b])`
    static JsSet<string> Union(IEnumerable<string> first, IEnumerable<string> second)
    {
        var set = new JsSet<string>();
        foreach (var item in first) set.Add(item);
        foreach (var item in second) set.Add(item);
        return set;
    }

    static List<string> SortedStable(IEnumerable<string> items)
    {
        var list = items.ToList();
        Js.Sort(list, StableCompare);
        return list;
    }

    // `edge.route || 'auto'`
    static string RouteOrAuto(JsonObject edge) => edge["route"].Truthy() ? BrandMarks.JsString(edge["route"]) : "auto";

    // ---------------------------------------------------------------- C (:672-856)

    public static string WorkflowEdgeName(JsonObject edge) => edge["id"].Truthy() ? BrandMarks.JsString(edge["id"]) : $"{Tpl(edge, "from")}->{Tpl(edge, "to")}";

    // `value == null ? '' : String(value)`
    static string StableText(JsonNode? value) => value is null ? "" : BrandMarks.JsString(value);
    internal static double StableCompare(string a, string b) => Math.Sign(string.CompareOrdinal(a, b));
    internal static double StableCompare(JsonNode? a, JsonNode? b) => StableCompare(StableText(a), StableText(b));

    // JSON.stringify with every object's keys in ordinal order: the last tiebreak of compareWorkflowEdges, so order-observable.
    public static string StableValueKey(JsonNode? value) => value switch
    {
        JsonArray array => $"[{string.Join(",", array.Select(StableValueKey))}]",
        JsonObject obj => $"{{{string.Join(",", SortedStable(obj.Select(kv => kv.Key)).Select(key => $"{Js.Quote(key)}:{StableValueKey(obj[key])}"))}}}",
        _ => Js.Stringify(value),
    };

    public static double CompareWorkflowEdges(JsonObject left, JsonObject right)
    {
        foreach (var key in (string[])["id", "from", "to", "label", "route"])
        {
            var order = StableCompare(left[key], right[key]);
            if (order != 0) return order;
        }
        return StableCompare(StableValueKey(left), StableValueKey(right));
    }

    public static WorkflowDoc CanonicalReadableWorkflow(WorkflowDoc w, WorkflowWidths widths, JsMap<string, double>? widthOverrides)
    {
        if (w.SchemaVersion != 2) return w;
        foreach (var node in w.Nodes)
        {
            var width = !WorkflowMigrationGeometry.PreservesWorkflowAbsoluteGeometry(w) && !(node.Num("width") is { } authored && double.IsFinite(authored))
                ? Math.Max(Or(AutomaticNodeWidth(node), 0), Or(widthOverrides is not null && node.Str("id") is { } id && widthOverrides.Has(id) ? (double?)widthOverrides.Get(id) : null, 0))
                : 0;
            if (Truthy(width)) widths.Set(node, width);
            else widths.Remove(node);
        }
        var laneOrder = new JsMap<string, int>();
        for (var index = 0; index < w.Lanes.Count; index++) laneOrder.Set(IdKey(w.Lanes[index]["id"]), index);
        double LaneOrder(JsonObject item) => laneOrder.Has(IdKey(item["lane"])) ? laneOrder.Get(IdKey(item["lane"])) : 9007199254740991;
        double Number(JsonObject item, string key) => item.Num(key) ?? double.NaN;
        var nodes = new List<JsonObject>(w.Nodes);
        Js.Sort(nodes, (left, right) => Lor(LaneOrder(left) - LaneOrder(right), Lor(Col(left) - Col(right), StableCompare(left["id"], right["id"]))));
        var edges = new List<JsonObject>(w.Edges);
        Js.Sort(edges, CompareWorkflowEdges);
        List<JsonObject>? phases = null;
        if (w.Phases is not null)
        {
            phases = [.. w.Phases];
            Js.Sort(phases, (left, right) => Lor(Number(left, "fromCol") - Number(right, "fromCol"),
                Lor(Number(left, "toCol") - Number(right, "toCol"), StableCompare(left["id"], right["id"]))));
        }
        List<JsonObject>? groups = null;
        if (w.Groups is not null)
        {
            groups = [.. w.Groups];
            Js.Sort(groups, (left, right) => Lor(LaneOrder(left) - LaneOrder(right), Lor(Number(left, "fromCol") - Number(right, "fromCol"),
                Lor(Number(left, "toCol") - Number(right, "toCol"), StableCompare(left["id"], right["id"])))));
        }
        var canonical = w.Spread();
        canonical.Nodes = nodes;
        canonical.Edges = edges;
        canonical.Phases = phases;
        canonical.Groups = groups;
        return canonical;
    }

    public static List<Diagnostic> SemanticContractDiagnostics(WorkflowDoc w)
    {
        var checks = w.SemanticChecks;
        if (checks is null) return [];

        var nodeIds = new JsSet<string>();
        var nodeIdValues = new List<JsonNode?>();   // the Set's members as JSON values, for evidence
        foreach (var node in w.Nodes)
        {
            if (nodeIds.Has(IdKey(node["id"]))) continue;
            nodeIds.Add(IdKey(node["id"]));
            nodeIdValues.Add(node["id"]);
        }
        var incoming = new JsMap<string, int>();
        var outgoing = new JsMap<string, int>();
        var adjacency = new JsMap<string, JsSet<string>>();
        foreach (var id in nodeIds) { incoming.Set(id, 0); outgoing.Set(id, 0); adjacency.Set(id, []); }
        foreach (var edge in w.Edges)
        {
            var from = IdKey(edge["from"]);
            var to = IdKey(edge["to"]);
            if (!nodeIds.Has(from) || !nodeIds.Has(to)) continue;
            outgoing.Set(from, outgoing.Get(from) + 1);
            incoming.Set(to, incoming.Get(to) + 1);
            adjacency.Get(from)!.Add(to);
        }

        var diagnostics = new List<Diagnostic>();
        void Add(string code, string message, JsonObject subject, JsonObject evidence, string[] supportedFixes)
        {
            var full = new JsonObject { ["diagramType"] = "workflow" };
            foreach (var (key, value) in subject) full[key] = value?.DeepClone();
            diagnostics.Add(new(code, Severity.Error, message, full, evidence, supportedFixes, null));
        }
        static JsonArray Values(IEnumerable<JsonNode?> values) => new([.. values.Select(value => value?.DeepClone())]);
        var referencedNodes = new List<(JsonNode? Id, string Path)>();
        foreach (var (id, index) in checks["allowedRoots"].AsArrayOrEmpty().Select((id, index) => (id, index)))
            referencedNodes.Add((id, $"/semanticChecks/allowedRoots/{index}"));
        foreach (var (id, index) in checks["allowedTerminals"].AsArrayOrEmpty().Select((id, index) => (id, index)))
            referencedNodes.Add((id, $"/semanticChecks/allowedTerminals/{index}"));
        foreach (var collection in (string[])["requiredEdges", "requiredPaths"])
        {
            foreach (var (relation, index) in checks[collection].AsArrayOrEmpty().Select((relation, index) => (relation, index)))
            {
                referencedNodes.Add((relation?["from"], $"/semanticChecks/{collection}/{index}/from"));
                referencedNodes.Add((relation?["to"], $"/semanticChecks/{collection}/{index}/to"));
            }
        }
        foreach (var (id, path) in referencedNodes)
        {
            if (nodeIds.Has(IdKey(id))) continue;
            var text = id is null ? "undefined" : BrandMarks.JsString(id);
            var subject = new JsonObject();
            if (id is not null) subject["node"] = id.DeepClone();
            subject["path"] = path;
            Add("workflow/semantic-node-reference", $"Workflow semantic contract references unknown node \"{text}\" at {path}.", subject,
                new JsonObject { ["knownNodes"] = Values(nodeIdValues) },
                [$"replace \"{text}\" with an existing node id", "add the missing node before compiling"]);
        }
        if (diagnostics.Count > 0) return diagnostics;

        void UnexpectedEnds(string key, JsMap<string, int> counts, string code, string described, string countKey, Func<string, string[]> fixes)
        {
            if (!checks.ContainsKey(key)) return;
            var allowed = new JsSet<string>();
            var allowedValues = new List<JsonNode?>();
            foreach (var value in checks[key].AsArrayOrEmpty())
            {
                if (allowed.Has(IdKey(value))) continue;
                allowed.Add(IdKey(value));
                allowedValues.Add(value);
            }
            foreach (var (node, count) in counts.Entries)
            {
                if (count > 0 || allowed.Has(node)) continue;
                Add(code, $"Workflow node \"{node}\" has {described} and is not declared in semanticChecks.{key}.",
                    new JsonObject { ["node"] = node, ["path"] = $"/semanticChecks/{key}" },
                    new JsonObject { [countKey] = 0, [key] = Values(allowedValues) }, fixes(node));
            }
        }
        UnexpectedEnds("allowedRoots", incoming, "workflow/unexpected-root", "no incoming edge", "incomingEdges",
            node => [$"add the missing incoming edge to \"{node}\"", $"declare \"{node}\" in semanticChecks.allowedRoots if it is an intentional source"]);
        UnexpectedEnds("allowedTerminals", outgoing, "workflow/unexpected-terminal", "no outgoing edge", "outgoingEdges",
            node => [$"add the missing outgoing edge from \"{node}\"", $"declare \"{node}\" in semanticChecks.allowedTerminals if it is an intentional sink"]);

        var authoredEdges = new JsSet<string>();
        foreach (var edge in w.Edges) authoredEdges.Add($"{Tpl(edge, "from")}\u0000{Tpl(edge, "to")}");
        foreach (var (relationNode, index) in checks["requiredEdges"].AsArrayOrEmpty().Select((relation, index) => (relation, index)))
        {
            var relation = relationNode as JsonObject;
            var (from, to) = (Tpl(relation, "from"), Tpl(relation, "to"));
            if (authoredEdges.Has($"{from}\u0000{to}")) continue;
            Add("workflow/required-edge", $"Workflow semantic contract requires edge \"{from}\" -> \"{to}\", but no authored edge matches it.",
                new JsonObject { ["from"] = relation?["from"]?.DeepClone(), ["to"] = relation?["to"]?.DeepClone(), ["path"] = $"/semanticChecks/requiredEdges/{index}" },
                new JsonObject { ["authoredEdgeCount"] = w.Edges.Count },
                [$"add an edge from \"{from}\" to \"{to}\" without deleting the semantic requirement"]);
        }

        bool Reachable(string from, string to)
        {
            var visited = new JsSet<string>();
            visited.Add(from);
            var pending = new Queue<string>([from]);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                if (current == to) return true;
                foreach (var next in adjacency.Get(current) ?? [])
                {
                    if (visited.Has(next)) continue;
                    visited.Add(next);
                    pending.Enqueue(next);
                }
            }
            return false;
        }

        foreach (var (relationNode, index) in checks["requiredPaths"].AsArrayOrEmpty().Select((relation, index) => (relation, index)))
        {
            var relation = relationNode as JsonObject;
            var (fromKey, toKey) = (IdKey(relation?["from"]), IdKey(relation?["to"]));
            if (Reachable(fromKey, toKey)) continue;
            var (from, to) = (Tpl(relation, "from"), Tpl(relation, "to"));
            var reachableNodes = new JsSet<string>();
            reachableNodes.Add(fromKey);
            foreach (var next in adjacency.Get(fromKey) ?? []) reachableNodes.Add(next);
            Add("workflow/required-path", $"Workflow semantic contract requires a directed path from \"{from}\" to \"{to}\", but none exists.",
                new JsonObject { ["from"] = relation?["from"]?.DeepClone(), ["to"] = relation?["to"]?.DeepClone(), ["path"] = $"/semanticChecks/requiredPaths/{index}" },
                new JsonObject { ["reachableNodes"] = new JsonArray([.. reachableNodes.Select(key => (JsonNode?)key)]) },
                [$"restore a directed path from \"{from}\" to \"{to}\" without weakening the semantic requirement"]);
        }

        return diagnostics;
    }
}
