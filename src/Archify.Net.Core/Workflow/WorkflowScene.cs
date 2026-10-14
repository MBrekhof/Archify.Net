using System.Text.Json.Nodes;
using static Archify.Net.WorkflowModel;

namespace Archify.Net;

// resolveWorkflowLegendFootprint's result (workflow-compiler.mjs:897-904).
internal sealed record WorkflowLegendFootprint(List<LegendEntry> WorkflowLegendEntries, JsSet<string> PresentLegendKinds, double LegendPackingWidth,
    LegendFootprintResult PackedLegendFootprint, double LegendExtraHeight, double MinimumCanvasWidth);

// createWorkflowLaneGeometry's result (:967-979): the canvas height, the lane lookups and the position helpers.
internal sealed class WorkflowLaneGeometry
{
    readonly RenderContext ctx;
    readonly WorkflowDoc w;
    readonly ReadableLayout layout;
    readonly double legendExtraHeight;

    public WorkflowLaneGeometry(RenderContext ctx, WorkflowDoc w, ReadableLayout layout, double legendExtraHeight, double autoHeight, double[] initialViewBox)
    {
        (this.ctx, this.w, this.layout, this.legendExtraHeight) = (ctx, w, layout, legendExtraHeight);
        AutoHeight = autoHeight;
        InitialViewBox = initialViewBox;
        LaneIndex = Lookup(w, (_, index) => index);
        LaneLabels = Lookup(w, (lane, _) => lane["label"]);
    }

    public double AutoHeight { get; }
    public double[] InitialViewBox { get; }
    public JsMap<string, int> LaneIndex { get; }
    public JsMap<string, JsonNode?> LaneLabels { get; }

    static JsMap<string, T> Lookup<T>(WorkflowDoc w, Func<JsonObject, int, T> value)
    {
        var map = new JsMap<string, T>();
        for (var index = 0; index < w.Lanes.Count; index++) map.Set(IdKey(w.Lanes[index]["id"]), value(w.Lanes[index], index));
        return map;
    }
    int? IndexOf(JsonNode? laneId) => LaneIndex.Has(IdKey(laneId)) ? LaneIndex.Get(IdKey(laneId)) : null;

    public string NodeContext(JsonObject node)
    {
        var col = node.Num("col");
        var group = (w.Groups ?? []).FirstOrDefault(candidate => StrictEquals(candidate["lane"], node["lane"])
            && col >= candidate.Num("fromCol") && col <= candidate.Num("toCol"));
        var phase = (w.Phases ?? []).FirstOrDefault(candidate => col >= candidate.Num("fromCol") && col <= candidate.Num("toCol"));
        var parts = new[] { LaneLabels.Get(IdKey(node["lane"])), group?["label"], phase?["label"] }.Where(part => part.Truthy()).Select(BrandMarks.JsString);
        var context = string.Join(" › ", parts);
        return context.Length > 0 ? context : ctx.Locale.Translate(w.Meta.Str("locale"), "node.context.workflow");
    }

    // laneHeight(idOrIndex) dispatches on `typeof`: a number is an index, anything else a lane id. An unknown id or an
    // out-of-range index reads `laneHeights[undefined]`, so the shared laneH.
    public double LaneHeight(int index) => LaneHeightAt(index);
    public double LaneHeight(JsonNode? laneId) => LaneHeightAt(IndexOf(laneId));
    double LaneHeightAt(int? index) => index is { } i && layout.LaneHeights is { } heights && i >= 0 && i < heights.Count ? heights[i] : layout.LaneH;
    public double LaneGroupHeaderH(JsonNode? laneId) => IndexOf(laneId) is { } i && layout.GroupHeaderHeights is { } heights && i < heights.Count ? heights[i] : 0;
    public double LaneGroupFooterH(JsonNode? laneId) => IndexOf(laneId) is { } i && layout.GroupFooterHeights is { } heights && i < heights.Count ? heights[i] : 0;

    // An unknown lane gives `undefined * laneGap`: NaN geometry, not an exception (:950-955).
    public double LaneTop(JsonNode? laneId)
    {
        if (IndexOf(laneId) is not { } index) return double.NaN;
        double precedingHeight = 0;
        for (var lanePosition = 0; lanePosition < Math.Min(index, w.Lanes.Count); lanePosition++) precedingHeight += LaneHeight(lanePosition);
        return layout.LaneY + precedingHeight + index * layout.LaneGap;
    }

    public double LastLaneBottom()
    {
        double total = 0;
        for (var index = 0; index < w.Lanes.Count; index++) total += LaneHeight(index);
        return layout.LaneY + total + (w.Lanes.Count - 1) * layout.LaneGap;
    }

    public double LegendY() => LastLaneBottom() + 44 + legendExtraHeight;
}

// The step indexes (:1045-1059): where each edge and node sits on the main path.
internal sealed class WorkflowStepIndexes
{
    readonly WorkflowDoc w;

    public WorkflowStepIndexes(WorkflowDoc w)
    {
        this.w = w;
        MainPathSteps = MainPath(w);
        EdgeSteps = Edges(w);
    }

    public JsMap<string, int> MainPathSteps { get; }
    public JsMap<JsonObject, int> EdgeSteps { get; }

    static JsMap<string, int> MainPath(WorkflowDoc w)
    {
        var steps = new JsMap<string, int>();
        var index = 0;
        foreach (var id in w.MainPath.AsArrayOrEmpty()) steps.Set(IdKey(id), index++);
        return steps;
    }
    static JsMap<JsonObject, int> Edges(WorkflowDoc w)
    {
        var mainPathSteps = MainPath(w);
        int? Step(JsonNode? id) => mainPathSteps.Has(IdKey(id)) ? mainPathSteps.Get(IdKey(id)) : null;
        var steps = new JsMap<JsonObject, int>();
        for (var index = 0; index < w.Edges.Count; index++)
        {
            var edge = w.Edges[index];
            var fromStep = Step(edge["from"]);
            var toStep = Step(edge["to"]);
            int? mainStep = fromStep is { } from && toStep == from + 1 ? from : null;
            steps.Set(edge, mainStep ?? (w.MainPath?.Count ?? 0) + index);
        }
        return steps;
    }

    public int NodeStep(JsonObject node) => MainPathSteps.Has(IdKey(node["id"])) ? MainPathSteps.Get(IdKey(node["id"]))
        : (w.MainPath?.Count ?? 0) + w.Nodes.FindIndex(item => StrictEquals(item["id"], node["id"]));
}

// workflow-compiler.mjs cluster D (:860-1059): the scene-setup phases lifted out of compileWorkflowInternal.
internal static class WorkflowScene
{
    // nodeTextFit (:1007-1014): font sizes for this renderer's node text; the fitting geometry is shared.
    public static class TextFitSizes
    {
        public const double LabelPreferred = 11, LabelMinimum = 9, SublabelPreferred = 8, SublabelMinimum = 6, TagPreferred = 7, TagMinimum = 6;
    }

    static readonly string[] LegendKinds = ["frontend", "backend", "security", "messagebus", "database", "cloud", "external"];

    public static WorkflowLegendFootprint ResolveWorkflowLegendFootprint(RenderContext ctx, WorkflowDoc w, ReadableLayout layout)
    {
        var locale = w.Meta.Str("locale");
        var catalog = LegendKinds.Select(kind => new LegendEntry(kind, ctx.Locale.Translate(locale, $"legend.workflow.{kind}"))).ToList();
        var presentLegendKinds = new JsSet<string>();
        foreach (var node in w.Nodes) if (node.Str("type") is { } type) presentLegendKinds.Add(type);   // a node without a type matches no kind
        var workflowLegendEntries = Legend.ResolveLegend(w.Meta.Obj("legend"), catalog, presentLegendKinds);
        const double fontSize = 7, itemGap = 7;
        var oneRowLegendFootprint = Legend.LegendFootprint(workflowLegendEntries, 9007199254740991, fontSize, itemGap);
        var minimumCanvasWidth = w.SchemaVersion == 2
            ? Math.Max(layout.DefaultViewBoxWidth, oneRowLegendFootprint.MinWidth + 40)
            : layout.DefaultViewBoxWidth;
        var legendPackingWidth = Math.Max(1, (w.SchemaVersion == 2
            ? minimumCanvasWidth
            : (w.Meta["viewBox"] is JsonArray { Count: > 0 } viewBox && viewBox[0] is { } width ? width.D() : minimumCanvasWidth)) - 40);
        var packedLegendFootprint = Legend.LegendFootprint(workflowLegendEntries, legendPackingWidth, fontSize, itemGap);
        var legendExtraHeight = w.SchemaVersion == 2 ? packedLegendFootprint.ExtraHeight : 0;
        return new(workflowLegendEntries, presentLegendKinds, legendPackingWidth, packedLegendFootprint, legendExtraHeight, minimumCanvasWidth);
    }

    public static WorkflowLaneGeometry CreateWorkflowLaneGeometry(RenderContext ctx, WorkflowDoc w, ReadableLayout layout, double legendExtraHeight, double minimumCanvasWidth)
    {
        // Content is 680px wide (laneX + laneW); auto height fits the lanes plus legend.
        var laneCount = w.Lanes.Count > 0 ? w.Lanes.Count : 1;   // `workflow.lanes?.length || 1`
        double lanesHeight = 0;
        if (layout.LaneHeights is { } heights) foreach (var height in heights) lanesHeight += height;
        else lanesHeight = laneCount * layout.LaneH;
        var autoHeight = layout.LaneY + lanesHeight + (laneCount - 1) * layout.LaneGap + 124 + legendExtraHeight;
        double[] initialViewBox = w.Meta["viewBox"].Truthy() && w.Meta["viewBox"] is JsonArray viewBox
            ? [.. viewBox.Select(value => value?.D() ?? double.NaN)]
            : [minimumCanvasWidth, autoHeight];
        return new(ctx, w, layout, legendExtraHeight, autoHeight, initialViewBox);
    }

    // measureNode (:986-1004) over the nodes; a duplicate id keeps its first position and its last measurement. `cx` is the
    // column centre and `cy` the row centre, stored (R2) rather than re-derived from x/y.
    public static JsMap<string, Box> MeasureWorkflowNodes(RenderContext ctx, WorkflowDoc w, ReadableLayout layout, WorkflowLaneGeometry lanes, WorkflowWidths widths)
    {
        Box MeasureNode(JsonObject node)
        {
            var width = Or(node.Num("width"), Or(w.SchemaVersion == 2 ? widths.Get(node) : null, layout.NodeW));
            var height = Or(node.Num("height"), node["tag"].Truthy() ? 68 : layout.NodeH);
            var cx = ColAt(layout.ColXs, node.Num("col"));
            var groupHeaderH = lanes.LaneGroupHeaderH(node["lane"]);
            var contentH = lanes.LaneHeight(node["lane"]) - layout.LaneTitleH - groupHeaderH - lanes.LaneGroupFooterH(node["lane"]);
            var y = lanes.LaneTop(node["lane"]) + layout.LaneTitleH + groupHeaderH + (contentH - height) / 2 + Or(node.Num("yOffset"), 0);
            return new Box
            {
                Id = node.Str("id") ?? "undefined", Source = node, Brand = ctx.Brands.BrandMarkFor(node),
                Width = width, Height = height, X = cx - width / 2, Y = y, Cx = cx, Cy = y + height / 2,
            };
        }
        var nodes = new JsMap<string, Box>();
        foreach (var node in w.Nodes) nodes.Set(node.Str("id") ?? "undefined", MeasureNode(node));
        return nodes;
    }

    // :1021-1041. `node` is the authored node; `width` the measured (or a candidate) width; `brand` its resolved mark.
    public static List<string> WorkflowNodeTextProblems(JsonObject node, double width, ResolvedBrand? brand)
    {
        var problems = new List<string>();
        var id = Tpl(node, "id");
        var estLabelW = Utils.TextUnits(node.Str("label")) * 6.8;
        if (estLabelW > width + 6)
        {
            problems.Add($"Label \"{Tpl(node, "label")}\" (~{Js.Num(Js.Round(estLabelW))}px) is wider than component \"{id}\" ({Js.Num(width)}px) — shorten the label or increase node.width.");
        }
        if (BrandMarks.BrandTopRailProblem(brand, id, node.Str("label"), width, TextFitSizes.LabelMinimum) is { } brandRailProblem) problems.Add(brandRailProblem);
        var availableTextW = TextFit.AvailableNodeTextWidth(width);
        foreach (var (field, value, minimum) in new[] { ("Sublabel", node.Str("sublabel"), TextFitSizes.SublabelMinimum), ("Tag", node.Str("tag"), TextFitSizes.TagMinimum) })
        {
            if (string.IsNullOrEmpty(value)) continue;
            var minimumW = TextFit.MinimumNodeTextWidth(value, minimum);
            if (minimumW > availableTextW)
            {
                problems.Add($"{field} \"{value}\" needs ~{Js.Num(Math.Ceiling(minimumW))}px at the {Js.Num(minimum)}px legible minimum, but node \"{id}\" provides {Js.Num(availableTextW)}px — shorten the {field.ToLowerInvariant()} or increase node.width.");
            }
        }
        return problems;
    }

    public static WorkflowStepIndexes CreateWorkflowStepIndexes(WorkflowDoc w) => new(w);
}
