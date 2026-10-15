using System.Text.Json.Nodes;
using static Archify.Net.WorkflowModel;

namespace Archify.Net;

// workflow-compiler.mjs compileWorkflowInternal (:1362-5644), one instance per compile (R6). The constructor runs the closure's
// top-level statements in source order; Run() is the driver at :5609. Clusters here: G (preamble, scene state, acceptsFix),
// E (legacy column-capacity repair), F legacy (one-bend sides), J1/J2 (route primitives, caches), K3 routeVia, K4 (label
// point, edgeSides, automaticPorts) and K8 (registerRouted, pathFor, labelRectFor). Validation is in WorkflowCompilation.Validation.cs,
// canvas measurement, SVG and the receipt in WorkflowCompilation.Svg.cs, readable-v2 automatic routing (F readable, H2/H4/H5,
// J3, K1/K2/K5/K6) in WorkflowCompilation.Routing.cs, the typed input diagnostics (I2) in WorkflowCompilation.Inputs.cs, the
// pin/preset conflict paths (H1 verifiers and throwers, H5 per-edge pin checks, K3 readablePresetVia, K7 readableControlledRoute)
// in WorkflowCompilation.Pins.cs.
internal sealed partial class WorkflowCompilation
{
    // `{ d, points }` (:5094, :5124). A class, not a record: LABEL_RECT_CACHE keys on the routed object (R9).
    internal sealed class RoutedPath(string d, List<Pt> points)
    {
        public string D { get; } = d;
        public List<Pt> Points { get; } = points;
    }
    internal sealed record Sides(string FromSide, string ToSide);
    internal sealed record Span(double X, double Width, double Cx);
    // The obstacle grid's items (:1493, :5065, :5077): nodes, routes and label rects.
    internal sealed class ObstacleItem
    {
        public required string Kind { get; init; }
        public Box? Node { get; init; }
        public JsonObject? Edge { get; init; }
        public RoutedPath? Routed { get; init; }
        public LabelRect? Rect { get; init; }
        public SceneObstacle? Obstacle { get; init; }
        public int Sequence { get; init; }
        public GridBox Bounds { get; init; }
    }
    // workflowSceneLabelObstacles' entries (:1536-1572).
    internal sealed record SceneObstacle(string Kind, JsonNode? Id, double X, double Y, double Width, double Height);
    // firstRouteNodeCollision's result (:3357-3364).
    internal sealed record RouteNodeCollision(string ObstacleNode, string ObstacleRole, int SegmentIndex, Pt From, Pt To, double ClearancePx);

    const double OBSTACLE_CELL = 160;

    readonly RenderContext ctx;
    readonly WorkflowDoc qualityResolvedWorkflow;
    readonly WorkflowDoc workflow;
    readonly string? resolvedQualityProfile;
    readonly string? authoredQualityProfile;
    readonly JsonObject? sourceEvidence;
    readonly bool discoverFixes;
    readonly JsonObject layoutFeedback;
    readonly WorkflowWidths widths;
    readonly JsMap<JsonObject, int> sourceLanes = new();
    readonly JsMap<JsonObject, int> sourceNodes = new();
    readonly JsMap<JsonObject, int> sourceEdges = new();
    readonly ReadableLayout layout;
    readonly List<LegendEntry> workflowLegendEntries;
    readonly double legendPackingWidth;
    readonly double minimumCanvasWidth;
    readonly WorkflowLaneGeometry lanes;
    double[] viewBox;
    double[] requiredViewBox;
    bool measuredPlanReady;
    readonly JsMap<string, Box> nodes;
    readonly SpatialGrid<ObstacleItem> obstacleGrid = SpatialGrid<ObstacleItem>.Create(OBSTACLE_CELL);
    double rightmostNodeEdge;
    double rightmostRoutedEdge = double.NegativeInfinity;
    // Routed endpoints per node, so the port search reads only the routes that touch the node.
    readonly JsMap<string, JsMap<JsonObject, RoutedPath>> nodeRoutes = new();
    readonly WorkflowStepIndexes steps;
    readonly JsMap<JsonObject, int> edgeIndexByEdge = new();
    // Several rejected routes can propose the same document edit; its verification is cached per compile, capped at 128.
    readonly JsMap<string, bool> fixResults = new();
    readonly JsMap<JsonObject, RoutedPath> pathCache = new();
    // Stores null entries (:1350): `has` and `get` both matter.
    readonly JsMap<JsonObject, Sides?> readableSideCache = new();
    readonly List<Diagnostic> workflowDiagnostics = [];
    readonly JsMap<JsonObject, SpreadPorts> automaticPorts;
    int obstacleSequence;
    // LABEL_RECT_CACHE (:5130): keyed by the routed object, so a re-routed edge gets a fresh rectangle.
    readonly JsMap<RoutedPath, LabelRect> labelRectCache = new();

    static string N(double v) => Js.Num(v);
    static bool Truthy(string? s) => !string.IsNullOrEmpty(s);
    Box? NodeOf(string? id) => id is null ? null : nodes.Get(id);
    Box? NodeOf(JsonNode? id) => id is JsonValue v && v.TryGetValue<string>(out var s) ? nodes.Get(s) : null;
    bool Has(string? id) => id is not null && nodes.Has(id);
    bool Has(JsonNode? id) => NodeOf(id) is not null;
    static Pt PtOf(JsonNode? point) => point is JsonArray a && a.Count >= 2 ? new(Coord(a[0]), Coord(a[1])) : new(double.NaN, double.NaN);
    static double Coord(JsonNode? value) => value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number ? v.D() : double.NaN;
    static JsonArray PtJson(Pt p) => new(p.X, p.Y);
    bool V2 => workflow.SchemaVersion == 2;
    JsonNode? Lane(Box node) => node.Source["lane"];
    static double Col(Box node) => WorkflowModel.Col(node.Source);
    List<Pt> PathPoints(JsonObject edge) => PathFor(edge).Points;
    static GridBox RouteBounds(IReadOnlyList<Pt> points)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var point in points)
        {
            if (point.X < minX) minX = point.X;
            if (point.X > maxX) maxX = point.X;
            if (point.Y < minY) minY = point.Y;
            if (point.Y > maxY) maxY = point.Y;
        }
        return new(minX, maxX, minY, maxY);
    }
    static GridBox RectToBounds(Rect rect) => new(rect.X, rect.X + rect.Width, rect.Y, rect.Y + rect.Height);
    static bool BoundsOverlap(GridBox a, GridBox b, double margin) =>
        a.MinX <= b.MaxX + margin && b.MinX <= a.MaxX + margin && a.MinY <= b.MaxY + margin && b.MinY <= a.MaxY + margin;

    // ---------------------------------------------------------------- G: preamble (:1370-1418)
    // compileWorkflowInternal's input, schema and semantic gates, which return a compilerFailure before any scene state exists.
    // ponytail: the `workflow/input-contract` type check (:1370-1380) is the WorkflowDoc parameter type.
    public static WorkflowCompileResult CompileWorkflowInternal(RenderContext ctx, WorkflowCompileOptions options)
    {
        var input = options.Workflow;
        var authoredQualityProfile = input.Meta.Str("quality_profile");
        var resolvedQualityProfile = Truthy(options.QualityProfile) ? options.QualityProfile : authoredQualityProfile;
        var qualityResolvedWorkflow = input;
        if (Truthy(resolvedQualityProfile) && resolvedQualityProfile != authoredQualityProfile)
        {
            qualityResolvedWorkflow = input.Spread();
            qualityResolvedWorkflow.Meta = input.Meta.DeepClone().AsObject();
            qualityResolvedWorkflow.Meta["quality_profile"] = resolvedQualityProfile;
        }
        List<Diagnostic> inputDiagnostics = [];
        try
        {
            var json = qualityResolvedWorkflow.ToJson();
            SchemaValidator.Validate(DiagramType.Workflow, json, ctx.Diagnostics);
            DiagramLoader.ValidateCrossCollectionContracts(DiagramType.Workflow, json, ctx.Diagnostics);
        }
        catch (ArchifyDiagnosticException error)
        {
            inputDiagnostics = [.. error.Diagnostics.Select(diagnostic => diagnostic with { SupportedFixes = [] })];
        }
        catch (Exception error)
        {
            inputDiagnostics = [new("workflow/input-contract", Severity.Error, "Workflow schema validation failed unexpectedly.",
                new JsonObject { ["diagramType"] = "workflow", ["path"] = "/" },
                new JsonObject { ["reason"] = Truthy(error.Message) ? error.Message : error.ToString() }, [], null)];
        }
        if (inputDiagnostics.Count > 0)
            return WorkflowCompiler.CompilerFailure(input.SchemaVersion == 2 ? "readable-v2" : "fixed-v1", inputDiagnostics);
        var workflow = CanonicalReadableWorkflow(qualityResolvedWorkflow, options.Widths, options.WidthOverrides);
        var semanticDiagnostics = SemanticContractDiagnostics(workflow);
        if (semanticDiagnostics.Count > 0)
            return WorkflowCompiler.CompilerFailure(workflow.SchemaVersion == 2 ? "readable-v2" : "fixed-v1", semanticDiagnostics);
        return new WorkflowCompilation(ctx, options, qualityResolvedWorkflow, workflow, resolvedQualityProfile, authoredQualityProfile).Run();
    }

    // ---------------------------------------------------------------- G: scene state (:1419-1639), in source order
    WorkflowCompilation(RenderContext ctx, WorkflowCompileOptions options, WorkflowDoc qualityResolvedWorkflow, WorkflowDoc workflow,
        string? resolvedQualityProfile, string? authoredQualityProfile)
    {
        this.ctx = ctx;
        this.qualityResolvedWorkflow = qualityResolvedWorkflow;
        this.workflow = workflow;
        this.resolvedQualityProfile = resolvedQualityProfile;
        this.authoredQualityProfile = authoredQualityProfile;
        sourceEvidence = options.SourceEvidence;
        discoverFixes = options.DiscoverFixes;
        layoutFeedback = options.LayoutFeedback ?? [];
        widths = options.Widths;
        for (var index = 0; index < qualityResolvedWorkflow.Lanes.Count; index++) sourceLanes.Set(qualityResolvedWorkflow.Lanes[index], index);
        for (var index = 0; index < qualityResolvedWorkflow.Nodes.Count; index++) sourceNodes.Set(qualityResolvedWorkflow.Nodes[index], index);
        for (var index = 0; index < qualityResolvedWorkflow.Edges.Count; index++) sourceEdges.Set(qualityResolvedWorkflow.Edges[index], index);
        layout = workflow.SchemaVersion == 2 ? CreateReadableLayout(workflow, layoutFeedback, widths) : CreateLegacyLayout();

        var footprint = WorkflowScene.ResolveWorkflowLegendFootprint(ctx, workflow, layout);
        workflowLegendEntries = footprint.WorkflowLegendEntries;
        legendPackingWidth = footprint.LegendPackingWidth;
        minimumCanvasWidth = footprint.MinimumCanvasWidth;
        lanes = WorkflowScene.CreateWorkflowLaneGeometry(ctx, workflow, layout, footprint.LegendExtraHeight, minimumCanvasWidth);
        viewBox = lanes.InitialViewBox;
        requiredViewBox = [.. viewBox];
        measuredPlanReady = false;

        nodes = WorkflowScene.MeasureWorkflowNodes(ctx, workflow, layout, lanes, widths);

        // Obstacles of the routing search are queried through a uniform grid instead of scanned.
        foreach (var node in nodes.Values)
        {
            obstacleGrid.Insert(RectToBounds(node), new ObstacleItem { Kind = "node", Node = node, Bounds = RectToBounds(node) });
            rightmostNodeEdge = Math.Max(rightmostNodeEdge, node.X + node.Width);
        }

        steps = WorkflowScene.CreateWorkflowStepIndexes(workflow);
        for (var index = 0; index < workflow.Edges.Count; index++) edgeIndexByEdge.Set(workflow.Edges[index], index);

        // :4466 — runs during construction: edgeSides for every edge (for v2 that fills readableSideCache before any route exists).
        automaticPorts = Geometry.AutomaticPortSpread(workflow.Edges, nodes,
            sideFor: (edge, endpoint) => endpoint == "source" ? EdgeSides(edge).FromSide : EdgeSides(edge).ToSide);
    }

    // ---------------------------------------------------------------- G: legend rects, frames, scene obstacles (:1454-1575)
    LegendLayout WorkflowLegendLayout(IReadOnlyList<LegendObstacle>? obstacles = null) => new(
        X: 20,
        BaselineY: lanes.LegendY(),
        Width: V2 ? legendPackingWidth : viewBox[0] - 40,
        FontSize: 7,
        ItemGap: 7,
        MinTitleY: lanes.LastLaneBottom() + 8,
        Obstacles: obstacles ?? [],
        Unfit: workflow.Meta.ContainsKey("legend") ? "error" : "hide",
        DiagramType: "workflow");

    List<(string Kind, Rect Rect)> WorkflowLegendRects()
    {
        if (workflowLegendEntries.Count == 0) return [];
        var measured = Legend.MeasureLegend(ctx.Diagnostics, workflowLegendEntries, WorkflowLegendLayout());
        if (measured is null) return [];
        return [("title", new Rect(20, measured.TitleY!.Value - 10, 48, 14)),
            .. measured.Entries.Select(entry => (entry.Kind, new Rect(entry.X, entry.Baseline - 10, entry.Width, 14)))];
    }

    double GroupFrameHeight(JsonObject group) => V2
        ? lanes.LaneHeight(group["lane"]) - layout.LaneTitleH - GroupFrameTopInset - GroupFrameBottomInset
        : layout.LaneH - layout.LaneTitleH - 16;

    List<Frame> WorkflowCompositionFrames()
    {
        var frames = new List<Frame>();
        for (var index = 0; index < workflow.Lanes.Count; index++)
        {
            var lane = workflow.Lanes[index];
            var y = lanes.LaneTop(lane["id"]);
            var height = lanes.LaneHeight(index);
            frames.Add(new(layout.LaneX, y, layout.LaneW, height, Radius: 10, Kind: "lane", Label: lane.Str("label"), Id: $"lane-{N(index)}"));
            if (lane.Str("variant") == "exception")
                frames.Add(new(layout.LaneX + 6, y + 6, layout.LaneW - 12, height - 12, Radius: 8, Kind: "exception-lane",
                    Label: $"{Tpl(lane, "label")} exception", Id: $"lane-{N(index)}-exception"));
        }
        var groups = workflow.Groups ?? [];
        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            var span = GroupSpan(group);
            frames.Add(new(span.X, lanes.LaneTop(group["lane"]) + layout.LaneTitleH + GroupFrameTopInset, span.Width, GroupFrameHeight(group),
                Radius: 9, Kind: "group", Label: group.Str("label"), Id: $"group-{N(index)}"));
        }
        return frames;
    }

    static bool ValidSpan(JsonObject span, int columnCount)
    {
        var fromCol = span.Num("fromCol");
        var toCol = span.Num("toCol");
        return IsInteger(fromCol) && IsInteger(toCol) && fromCol >= 0 && toCol < columnCount && fromCol <= toCol;
    }

    List<SceneObstacle> WorkflowSceneLabelObstacles()
    {
        var obstacles = new List<SceneObstacle>();
        for (var index = 0; index < workflow.Lanes.Count; index++)
        {
            var lane = workflow.Lanes[index];
            var prefix = lane.Str("variant") == "exception" ? "EX" : N(index + 1).PadLeft(2, '0');
            var label = $"{prefix} / {Tpl(lane, "label")}";
            obstacles.Add(new("lane-header", lane["id"], layout.LaneX + 14, lanes.LaneTop(lane["id"]) + 12, Utils.TextUnits(label) * 6.2, 14));
        }
        foreach (var phase in workflow.Phases ?? [])
        {
            if (!ValidSpan(phase, layout.ColXs.Length)) continue;
            var span = PhaseSpan(phase);
            obstacles.Add(new("phase-header", phase["id"], span.X, 27, span.Width, 16));
        }
        foreach (var group in workflow.Groups ?? [])
        {
            if (!lanes.LaneIndex.Has(IdKey(group["lane"])) || !ValidSpan(group, layout.ColXs.Length)) continue;
            var span = GroupSpan(group);
            var frameY = lanes.LaneTop(group["lane"]) + layout.LaneTitleH + GroupFrameTopInset;
            var labelBaseline = frameY + GroupLabelBaselineOffset;
            obstacles.Add(new("group-label", group["id"], span.X + 10, labelBaseline - GroupLabelMaskAscent, Utils.TextUnits(group.Str("label")) * 5.6, GroupLabelMaskH));
        }
        return obstacles;
    }

    // ---------------------------------------------------------------- G: fix discovery (:1582-1628)
    bool AcceptsFix(Action<WorkflowDoc> mutator)
    {
        if (!discoverFixes) return false;
        var candidate = workflow.Clone();
        // Migration projections carry geometry provenance outside their JSON (the Symbol, :1588-1590).
        candidate.AbsoluteGeometryPolicy = workflow.AbsoluteGeometryPolicy;
        mutator(candidate);
        var key = Js.Stringify(candidate.ToJson());
        if (fixResults.Has(key)) return fixResults.Get(key);
        var accepted = ctx.Diagnostics.WithRecordingSuppressed(() => WorkflowCompiler.CompileWorkflowWithFeedback(ctx,
            new WorkflowCompileOptions(candidate, resolvedQualityProfile, null, widths, DiscoverFixes: false)).Ok);
        if (fixResults.Count < 128) fixResults.Set(key, accepted);
        return accepted;
    }

    static void RepointReferences(WorkflowDoc document, JsonNode? unknownId, string nodeId)
    {
        foreach (var entry in document.Edges)
        {
            if (StrictEquals(entry["from"], unknownId)) entry["from"] = nodeId;
            if (StrictEquals(entry["to"], unknownId)) entry["to"] = nodeId;
        }
        if (document.MainPath is { } mainPath)
            document.MainPath = new JsonArray([.. mainPath.Select(id => StrictEquals(id, unknownId) ? (JsonNode?)nodeId : id?.DeepClone())]);
    }

    // Indexes follow the emitter conventions: edges use the sourceIndexes space, mainPath the canonical workflow's.
    List<string> UnknownIdReferenceFixes(JsonNode? unknownId, string nodeId)
    {
        var fixes = new List<string>();
        for (var index = 0; index < qualityResolvedWorkflow.Edges.Count; index++)
        {
            foreach (var field in (string[])["from", "to"])
            {
                if (StrictEquals(qualityResolvedWorkflow.Edges[index][field], unknownId))
                    fixes.Add($"set /edges/{N(index)}/{field} to verified node id \"{nodeId}\"");
            }
        }
        var mainPath = workflow.MainPath.AsArrayOrEmpty().ToList();
        for (var index = 0; index < mainPath.Count; index++)
        {
            if (StrictEquals(mainPath[index], unknownId)) fixes.Add($"set /mainPath/{N(index)} to verified node id \"{nodeId}\"");
        }
        return fixes;
    }

    // ---------------------------------------------------------------- E: legacy column-capacity repair (:1064-1233)
    double? VerifiedLegacyAlternative(JsonObject edge, Box from, Box to, double requiredClearance)
    {
        var occupied = nodes.Values.Where(node => StrictEquals(Lane(node), Lane(to)) && node.Id != to.Id).ToList();
        var toCol = Col(to);
        var candidates = layout.ColXs.Select((center, col) => (Center: center, Col: (double)col)).Where(candidate => candidate.Col != toCol).ToList();
        Js.Sort(candidates, (a, b) => Lor(Math.Abs(a.Col - toCol) - Math.Abs(b.Col - toCol), a.Col - b.Col));
        foreach (var candidate in candidates)
        {
            var candidateRect = new Rect(candidate.Center - to.Width / 2, to.Y, to.Width, to.Height);
            if (occupied.Any(node => Geometry.RectsOverlap(candidateRect, node, 8))) continue;
            var centerDistance = Math.Abs(candidate.Center - from.Cx);
            var signedClearance = centerDistance - from.Width / 2 - to.Width / 2;
            if (signedClearance < requiredClearance) continue;
            if (AcceptsFix(document => document.Nodes.First(node => node.Str("id") == to.Id)["col"] = (int)candidate.Col)) return candidate.Col;
        }
        return null;
    }

    WorkflowCompileResult NestedCompile(WorkflowDoc document, string? qualityProfile) =>
        WorkflowCompiler.CompileWorkflowWithFeedback(ctx, new WorkflowCompileOptions(document, qualityProfile, null, widths, DiscoverFixes: false));

    bool ReadableMigrationProvidesCapacity(Box from, Box to, double requiredClearance)
    {
        // `{ ...workflow, schema_version: 2 }` never passed canonicalReadableWorkflow, so its nodes miss the width table (R8).
        var projected = workflow.Spread();
        projected.SchemaVersion = 2;
        var readable = CreateReadableLayout(projected, null, new WorkflowWidths());
        var centerDistance = Math.Abs(ColAt(readable.ColXs, Col(to)) - ColAt(readable.ColXs, Col(from)));
        if (centerDistance - from.Width / 2 - to.Width / 2 < requiredClearance) return false;
        if (!discoverFixes) return false;

        return ctx.Diagnostics.WithRecordingSuppressed(() =>
        {
            var migrationQualityProfile = authoredQualityProfile;
            var planned = NestedCompile(WorkflowMigrationGeometry.IntrinsicWorkflow(workflow), migrationQualityProfile);
            if (!planned.Ok) planned = NestedCompile(WorkflowMigrationGeometry.PlanningWorkflow(workflow), migrationQualityProfile);
            if (!planned.Ok || planned.Receipt["columns"] is not JsonArray columns) return false;

            WorkflowDoc candidate;
            try
            {
                candidate = WorkflowMigrationGeometry.CreateMappedWorkflowCandidate(workflow, LegacyColumnCenters, [.. columns.Select(Coord)]).Document;
            }
            catch (Exception)
            {
                return false;
            }
            var compiled = NestedCompile(candidate, migrationQualityProfile);
            var requiredViewBox = compiled.Diagnostics.Count > 0 && compiled.Diagnostics.All(diagnostic => diagnostic.Code == "workflow/viewbox-capacity")
                ? compiled.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Evidence["requiredViewBox"] is JsonArray)?.Evidence["requiredViewBox"] as JsonArray
                : null;
            if (!compiled.Ok && candidate.Meta["viewBox"] is JsonArray authored && requiredViewBox is not null)
            {
                candidate.Meta["viewBox"] = new JsonArray(Math.Max(Coord(authored[0]), Coord(requiredViewBox[0])), Math.Max(Coord(authored[1]), Coord(requiredViewBox[1])));
                compiled = NestedCompile(candidate, migrationQualityProfile);
            }
            return compiled.Ok;
        });
    }

    double[]? VerifiedReducedWidths(Box from, Box to, double requiredClearance)
    {
        var widthBudget = 2 * (Math.Abs(to.Cx - from.Cx) - requiredClearance);
        if (widthBudget < 64) return null;
        double[] widths = [from.Width, to.Width];
        var excess = widths[0] + widths[1] - widthBudget;
        foreach (var index in widths[0] >= widths[1] ? (int[])[0, 1] : [1, 0])
        {
            var reduction = Math.Min(excess, widths[index] - 32);
            widths[index] -= reduction;
            excess -= reduction;
        }
        if (excess > 0.0001) return null;
        Box[] candidates = [from, to];
        var labelsFit = candidates.Select((node, index) =>
            Utils.TextUnits(node.Source.Str("label")) * 6.8 <= widths[index] + 6
            && (!node.Source["sublabel"].Truthy() || TextFit.MinimumNodeTextWidth(node.Source.Str("sublabel"), WorkflowScene.TextFitSizes.SublabelMinimum) <= TextFit.AvailableNodeTextWidth(widths[index]))
            && (!node.Source["tag"].Truthy() || TextFit.MinimumNodeTextWidth(node.Source.Str("tag"), WorkflowScene.TextFitSizes.TagMinimum) <= TextFit.AvailableNodeTextWidth(widths[index])))
            .All(fits => fits);
        if (!labelsFit) return null;
        var serializedWidths = widths.Select(width => Math.Floor((width + 1e-9) * 100) / 100).ToArray();
        var signedClearance = Math.Abs(to.Cx - from.Cx) - serializedWidths[0] / 2 - serializedWidths[1] / 2;
        if (signedClearance + 0.0001 < requiredClearance) return null;
        var accepted = AcceptsFix(document =>
        {
            document.Nodes.First(node => node.Str("id") == from.Id)["width"] = serializedWidths[0];
            document.Nodes.First(node => node.Str("id") == to.Id)["width"] = serializedWidths[1];
        });
        return accepted ? serializedWidths : null;
    }

    void EnforceLegacyColumnCapacity()
    {
        if (workflow.SchemaVersion != 1) return;
        foreach (var edge in workflow.Edges)
        {
            var from = NodeOf(edge.Str("from"));
            var to = NodeOf(edge.Str("to"));
            if (from is null || to is null || !StrictEquals(Lane(from), Lane(to)) || StrictEquals(from.Source["col"], to.Source["col"])) continue;
            if (!VerticalIntervalsOverlap(from.Source, to.Source, 8)) continue;
            var centerDistance = Math.Abs(to.Cx - from.Cx);
            var actualSignedClearance = centerDistance - from.Width / 2 - to.Width / 2;
            var direct = !edge["via"].Truthy() && RouteOrAuto(edge) is "auto" or "straight" && Math.Abs(from.Cy - to.Cy) < 0.0001;
            double requiredDirectClearance = direct ? 28 : 8;
            if (actualSignedClearance >= requiredDirectClearance) continue;
            var alternative = VerifiedLegacyAlternative(edge, from, to, requiredDirectClearance);
            var reducedWidths = VerifiedReducedWidths(from, to, requiredDirectClearance);
            var capacity = actualSignedClearance < 0
                ? $"overlap by {N(Math.Abs(Js.Round(actualSignedClearance)))}px"
                : $"leave only {N(Js.Round(actualSignedClearance))}px of direct clearance";
            var message = $"Workflow columns {Tpl(from.Source, "col")}→{Tpl(to.Source, "col")} place nodes \"{from.Id}\" and \"{to.Id}\" so they {capacity} under the fixed-v1 layout.";
            var supportedFixes = new List<string>();
            if (ReadableMigrationProvidesCapacity(from, to, requiredDirectClearance)) supportedFixes.Add("migrate this workflow to schema_version 2");
            if (alternative is { } column) supportedFixes.Add($"move node \"{to.Id}\" to verified free column {N(column)}");
            if (reducedWidths is not null)
                supportedFixes.Add($"set node widths \"{from.Id}\"={N(Js.Round(reducedWidths[0] * 100) / 100)}px and \"{to.Id}\"={N(Js.Round(reducedWidths[1] * 100) / 100)}px");
            throw ctx.Diagnostics.ThrowDiagnosticError(message, [new Diagnostic("workflow/column-capacity", Severity.Error, message,
                new JsonObject
                {
                    ["diagramType"] = "workflow", ["edge"] = edge["id"]?.DeepClone(), ["from"] = edge["from"]?.DeepClone(), ["to"] = edge["to"]?.DeepClone(),
                    ["fromCol"] = from.Source["col"]?.DeepClone(), ["toCol"] = to.Source["col"]?.DeepClone(),
                },
                new JsonObject
                {
                    ["centerDistancePx"] = centerDistance, ["nodeWidthsPx"] = new JsonArray(from.Width, to.Width),
                    ["actualSignedClearancePx"] = actualSignedClearance, ["requiredDirectClearancePx"] = requiredDirectClearance,
                },
                supportedFixes, ["workflow/short-edge", "clean-flow/endpoint-side-direction", "workflow/label-node-overlap"])]);
        }
    }

    // `edge.route || 'auto'`
    static string RouteOrAuto(JsonObject edge) => edge["route"].Truthy() ? BrandMarks.JsString(edge["route"]) : "auto";

    // ---------------------------------------------------------------- F: automatic side selection (:1238-1359)
    Sides? LegacyAutomaticOneBendSides(JsonObject edge, Box from, Box to)
    {
        var automaticRoute = !edge["via"].Truthy() && (!edge["route"].Truthy() || edge.Str("route") == "auto");
        var automaticFrom = !edge["fromSide"].Truthy() || edge.Str("fromSide") == "auto";
        var automaticTo = !edge["toSide"].Truthy() || edge.Str("toSide") == "auto";
        if (!automaticRoute || !automaticFrom || !automaticTo || StrictEquals(Lane(from), Lane(to))) return null;
        if (from.Cx == to.Cx || from.Cy == to.Cy) return null;
        var verticalFrom = to.Cy < from.Cy ? "top" : "bottom";
        var horizontalTo = to.Cx < from.Cx ? "right" : "left";
        var horizontalFrom = to.Cx < from.Cx ? "left" : "right";
        var verticalTo = to.Cy < from.Cy ? "bottom" : "top";
        foreach (var candidate in (Sides[])[new(verticalFrom, horizontalTo), new(horizontalFrom, verticalTo)])
        {
            var start = Geometry.Anchor(from, candidate.FromSide);
            var end = Geometry.Anchor(to, candidate.ToSide);
            if (OneBendCrossLaneVia(edge, start, end, candidate.FromSide, candidate.ToSide) is not null) return candidate;
        }
        return null;
    }

    // readableAutomaticSides (:1267-1352) is in WorkflowCompilation.Routing.cs.
    Sides? AutomaticOneBendSides(JsonObject edge, Box from, Box to) =>
        V2 ? ReadableAutomaticSides(edge, from, to) : LegacyAutomaticOneBendSides(edge, from, to);

    // ---------------------------------------------------------------- J1: route primitives (:3259-3388)
    double GapYBetween(JsonNode? fromLane, JsonNode? toLane, double bias = 0.5)
    {
        var a = lanes.LaneTop(fromLane) + lanes.LaneHeight(fromLane);
        var b = lanes.LaneTop(toLane);
        return a + (b - a) * bias;
    }

    Span SpanForCols(double? fromCol, double? toCol, double pad = 46, double minimumWidth = 0)
    {
        var start = ColAt(layout.ColXs, fromCol) - pad;
        var end = ColAt(layout.ColXs, toCol) + pad;
        var width = Math.Max(end - start, minimumWidth);
        if (fromCol == toCol && width > end - start) return new(start, width, start + width / 2);
        var cx = (start + end) / 2;
        return new(cx - width / 2, width, cx);
    }

    Span PhaseSpan(JsonObject phase) => SpanForCols(phase.Num("fromCol"), phase.Num("toCol"), 46, V2 ? Utils.TextUnits(phase.Str("label")) * 5.6 + 8 : 0);

    Span GroupSpan(JsonObject group)
    {
        if (V2)
        {
            var (x, width, cx) = ReadableGroupBounds(workflow, group, layout.ColXs, widths);
            return new(x, width, cx);
        }
        return SpanForCols(group.Num("fromCol"), group.Num("toCol"), 50, 0);
    }

    static List<Pt> SameLaneAutoVia(Pt start, Pt end)
    {
        if (start.X == end.X || start.Y == end.Y) return [];
        var midX = (start.X + end.X) / 2;
        return [new(midX, start.Y), new(midX, end.Y)];
    }

    bool RouteClearsUnrelatedNodes(JsonObject edge, IReadOnlyList<Pt> points, double clearance = 2)
    {
        var endpointIds = new JsSet<string>();
        endpointIds.Add(IdKey(edge["from"]));
        endpointIds.Add(IdKey(edge["to"]));
        var routeExtent = RouteBounds(points);
        var queryBox = new GridBox(routeExtent.MinX - clearance, routeExtent.MaxX + clearance, routeExtent.MinY - clearance, routeExtent.MaxY + clearance);
        foreach (var item in obstacleGrid.Query(queryBox))
        {
            if (item.Kind != "node") continue;
            var node = item.Node!;
            if (endpointIds.Has(IdKey(node.Source["id"]))) continue;
            if (!BoundsOverlap(routeExtent, RectToBounds(node), clearance)) continue;
            for (var index = 0; index < points.Count - 1; index++)
            {
                if (Geometry.SegmentIntersectsRect(new(points[index], points[index + 1]), node, clearance)) return false;
            }
        }
        return true;
    }

    RouteNodeCollision? FirstRouteNodeCollision(JsonObject edge, IReadOnlyList<Pt> points)
    {
        var lastSegment = points.Count - 2;
        foreach (var node in nodes.Values)
        {
            var endpointRole = node.Id == edge.Str("from") ? "source-endpoint" : node.Id == edge.Str("to") ? "target-endpoint" : "unrelated";
            for (var segmentIndex = 0; segmentIndex <= lastSegment; segmentIndex++)
            {
                if (endpointRole == "source-endpoint" && segmentIndex == 0) continue;
                if (endpointRole == "target-endpoint" && segmentIndex == lastSegment) continue;
                double clearancePx = endpointRole == "unrelated" ? 2 : 0;
                var from = points[segmentIndex];
                var to = points[segmentIndex + 1];
                if (Geometry.SegmentIntersectsRect(new(from, to), node, clearancePx))
                    return new(node.Id, endpointRole, segmentIndex, from, to, clearancePx);
            }
        }
        return null;
    }

    List<Pt>? OneBendCrossLaneVia(JsonObject edge, Pt start, Pt end, string? fromSide, string? toSide)
    {
        var fromVertical = fromSide is "top" or "bottom";
        var toVertical = toSide is "top" or "bottom";
        if (fromVertical == toVertical) return null;

        var corner = fromVertical ? new Pt(start.X, end.Y) : new Pt(end.X, start.Y);
        var points = Geometry.NormalizeRoutePoints([start, corner, end]);
        if (points.Count != 3 || !Geometry.RouteHonorsEndpointSides(points, fromSide, toSide)) return null;

        var segmentsAreReadable = Enumerable.Range(0, points.Count - 1).All(index =>
            Js.Hypot(points[index + 1].X - points[index].X, points[index + 1].Y - points[index].Y) >= 8);
        if (!segmentsAreReadable || !RouteClearsUnrelatedNodes(edge, points)) return null;
        return points[1..^1];
    }

    // ---------------------------------------------------------------- K1 predicates (:3776-3791)
    bool IsReadableAutomaticRoute(JsonObject edge) => V2 && edge["via"] is not JsonArray && !edge.ContainsKey("channelX") && !edge.ContainsKey("channelY")
        && (!edge["route"].Truthy() || edge.Str("route") == "auto");

    bool IndependentAutomaticRoute(JsonObject edge) => V2 && !edge["via"].Truthy() && !edge.ContainsKey("channelX") && !edge.ContainsKey("channelY")
        && !edge["labelAt"].Truthy()
        && (!edge["route"].Truthy() || edge.Str("route") == "auto")
        && (!edge["fromSide"].Truthy() || edge.Str("fromSide") == "auto")
        && (!edge["toSide"].Truthy() || edge.Str("toSide") == "auto");

    // ---------------------------------------------------------------- K3: routeVia (:4305-4364); readablePresetVia is in .Pins.cs, K2 in .Routing.cs
    // Returns `edge.via` itself (:4315): the same authored points, never mutated.
    List<Pt> RouteVia(JsonObject edge, Box from, Box to, Pt start, Pt end, string fromSide, string toSide, bool validateReadablePreset = true)
    {
        if (edge["via"].Truthy()) return [.. edge["via"].AsArrayOrEmpty().Select(PtOf)];
        var hasCoordinatePins = edge.ContainsKey("channelX") || edge.ContainsKey("channelY");
        if (V2 && edge["route"].Truthy() && edge.Str("route") != "auto" && !hasCoordinatePins && validateReadablePreset)
            return ReadablePresetVia(edge, from, to, start, end, fromSide, toSide);
        switch (RouteOrAuto(edge))
        {
            case "straight":
                return [];
            case "drop":
            {
                var y = edge.Num("channelY") ?? GapYBetween(Lane(from), Lane(to), edge.Num("bias") ?? 0.5);
                return [new(start.X, y), new(end.X, y)];
            }
            case "outside-right":
            {
                var x = edge.Num("channelX") ?? layout.LaneX + layout.LaneW + 12;
                return [new(x, start.Y), new(x, end.Y)];
            }
            case "return-left":
            {
                var x = edge.Num("channelX") ?? Math.Min(from.X, to.X) - 28;
                return [new(x, start.Y), new(x, end.Y)];
            }
            case "bottom-channel":
            {
                var y = edge.Num("channelY") ?? Math.Max(from.Y + from.Height, to.Y + to.Height) + 32;
                return [new(start.X, y), new(end.X, y)];
            }
            case "up-channel":
            {
                var y = edge.Num("channelY") ?? Math.Min(from.Y, to.Y) - 28;
                return [new(start.X, y), new(end.X, y)];
            }
            default:
            {
                if (V2)
                {
                    var channelX = edge.Num("channelX");
                    var channelY = edge.Num("channelY");
                    if (edge.ContainsKey("channelX") && edge.ContainsKey("channelY"))
                        return [new(channelX ?? double.NaN, start.Y), new(channelX ?? double.NaN, channelY ?? double.NaN), new(end.X, channelY ?? double.NaN)];
                    if (edge.ContainsKey("channelX")) return [new(channelX ?? double.NaN, start.Y), new(channelX ?? double.NaN, end.Y)];
                    if (edge.ContainsKey("channelY")) return [new(start.X, channelY ?? double.NaN), new(end.X, channelY ?? double.NaN)];
                    return ReadableAutomaticVia(edge, from, to, start, end, fromSide, toSide);
                }
                if (StrictEquals(Lane(from), Lane(to))) return SameLaneAutoVia(start, end);
                if (OneBendCrossLaneVia(edge, start, end, fromSide, toSide) is { } oneBendVia) return oneBendVia;
                var y = GapYBetween(Lane(from), Lane(to), edge.Num("bias") ?? 0.5);
                return [new(start.X, y), new(end.X, y)];
            }
        }
    }

    // ---------------------------------------------------------------- K4: label point, edgeSides (:4366-4464)
    // labelPoint({ ...edge, labelSegment }, points) (geometry.mjs:1869-1880) as its two callers reach it: no labelAt, three or
    // more points, so only the segment branch applies. A C# Pt is a value, so the callers' `point[1] += 10` cannot alias.
    static Pt LabelPointAt(JsonObject edge, IReadOnlyList<Pt> points, int labelSegment)
    {
        var segmentIndex = Math.Min(points.Count - 2, Math.Max(0, labelSegment));
        var a = points[segmentIndex];
        var b = points[segmentIndex + 1];
        return new((a.X + b.X) / 2 + Or(edge.Num("labelDx"), 0), (a.Y + b.Y) / 2 - 10 + Or(edge.Num("labelDy"), 0));
    }

    Pt WorkflowEdgeLabelPoint(JsonObject edge, IReadOnlyList<Pt> points)
    {
        if (workflow.SchemaVersion == 1)
        {
            if (edge["labelAt"].Truthy() || IsInteger(edge.Num("labelSegment")) || points.Count != 3) return Geometry.LabelPoint(edge, points);
            var segmentLengths = new[] { 0, 1 }.Select(index => Js.Hypot(points[index + 1].X - points[index].X, points[index + 1].Y - points[index].Y)).ToArray();
            var labelSegment = segmentLengths[0] >= segmentLengths[1] ? 0 : 1;
            var point = LabelPointAt(edge, points, labelSegment);
            if (points[labelSegment].X == points[labelSegment + 1].X) point = point with { Y = point.Y + 10 };
            return point;
        }
        if (!edge["labelAt"].Truthy() && !IsInteger(edge.Num("labelSegment"))
            && !edge.ContainsKey("labelDx") && !edge.ContainsKey("labelDy")
            && points.Count == 2 && points[0].X == points[1].X)
        {
            // Center implicit v2 vertical labels in the corridor. Authored offsets
            // retain their existing source-relative anchor, including explicit zero.
            var x = points[0].X;
            var labelSide = 0;
            if (layoutFeedback["fanInLabelClearance"].Truthy() && !WorkflowMigrationGeometry.PreservesWorkflowAbsoluteGeometry(workflow))
            {
                // On a failed-route retry, leave the shared arrival side open. The
                // label stays beside its own straight edge, with an 8px mask gap;
                // normal node, route, label and frame checks still decide feasibility.
                var incoming = workflow.Edges.Where(other => !ReferenceEquals(other, edge) && StrictEquals(other["to"], edge["to"]))
                    .Select(other => NodeOf(other.Str("from"))?.Cx)
                    .Where(value => value is { } cx && double.IsFinite(cx) && Math.Abs(cx - x) > 4)
                    .Select(value => value!.Value).ToList();
                if (incoming.Count > 0 && incoming.All(value => value < x)) labelSide = 1;
                else if (incoming.Count > 0 && incoming.All(value => value > x)) labelSide = -1;
            }
            return new(x + labelSide * (WorkflowLabelWidth(edge.Str("label")) / 2 + 8), (points[0].Y + points[1].Y) / 2);
        }
        if (edge["labelAt"].Truthy() || IsInteger(edge.Num("labelSegment")) || points.Count <= 2) return Geometry.LabelPoint(edge, points);
        var segments = Enumerable.Range(0, points.Count - 1).Select(index => (
            Index: index,
            Horizontal: Math.Abs(points[index + 1].Y - points[index].Y) <= 0.0001,
            Length: Js.Hypot(points[index + 1].X - points[index].X, points[index + 1].Y - points[index].Y))).ToList();
        Js.Sort(segments, (left, right) => Lor((right.Horizontal ? 1 : 0) - (left.Horizontal ? 1 : 0), Lor(right.Length - left.Length, left.Index - right.Index)));
        Pt PointOn(int labelSegment)
        {
            var point = LabelPointAt(edge, points, labelSegment);
            if (points[labelSegment].X == points[labelSegment + 1].X) point = point with { Y = point.Y + 10 };
            return point;
        }
        // The longest horizontal run is the preferred home, but a run shorter than
        // its label pushes the label onto a node, and the planner then detours the
        // whole edge to manufacture a longer run. When the preferred run is that
        // short, use the next run that keeps the label clear of every node and
        // inside one lane, off the lane dividers, before giving up on it.
        var width = WorkflowLabelWidth(edge.Str("label"));
        bool ClearOfNodes(Pt p)
        {
            var rect = new Rect(p.X - width / 2, p.Y - 10, width, 14);
            return !nodes.Values.Any(node => Geometry.RectsOverlap(rect, node, -2));
        }
        bool InsideOneLane(Pt p) => p.X - width / 2 >= layout.LaneX + 4
            && p.X + width / 2 <= layout.LaneX + layout.LaneW - 4
            && workflow.Lanes.Select((lane, index) => (lane, index)).Any(entry =>
            {
                var top = lanes.LaneTop(entry.lane["id"]);
                return p.Y - 10 >= top + 4 && p.Y + 4 <= top + lanes.LaneHeight(entry.index) - 4;
            });
        var preferred = PointOn(segments[0].Index);
        if (!edge["label"].Truthy() || !segments[0].Horizontal || segments[0].Length >= width + 8 || ClearOfNodes(preferred)) return preferred;
        foreach (var segment in segments.Skip(1))
        {
            if (segment.Length < 28) continue;
            var point = PointOn(segment.Index);
            if (ClearOfNodes(point) && InsideOneLane(point)) return point;
        }
        return preferred;
    }

    Sides EdgeSides(JsonObject edge)
    {
        var from = NodeOf(edge.Str("from"));
        var to = NodeOf(edge.Str("to"));
        var resolved = V2 ? readableSideCache.Get(edge) : null;
        if (resolved is not null) return resolved;
        if (AutomaticOneBendSides(edge, from!, to!) is { } oneBendSides) return oneBendSides;
        if (V2 && layout.ChannelLabelEdgeKeys?.Has(StableValueKey(edge)) == true && !edge["fromSide"].Truthy() && !edge["toSide"].Truthy())
            return new("top", "top");
        return new(
            Geometry.ChosenSide(edge.Str("fromSide"), Geometry.LegacyDefaultFromSide(from!, to!)),
            Geometry.ChosenSide(edge.Str("toSide"), Geometry.LegacyDefaultToSide(from!, to!)));
    }

    // ---------------------------------------------------------------- K7 (:4936-4943) and K8 (:5061-5152)
    bool IsReadableControlledRoute(JsonObject edge) => V2 && (edge["via"] is JsonArray || edge.ContainsKey("channelX") || edge.ContainsKey("channelY")
        || (edge["route"].Truthy() && edge.Str("route") != "auto"));

    // Registering a routed path also publishes it (and its label box) to the grid.
    RoutedPath RegisterRouted(JsonObject edge, RoutedPath routed)
    {
        pathCache.Set(edge, routed);
        var sequence = obstacleSequence++;
        var routeExtent = RouteBounds(routed.Points);
        obstacleGrid.Insert(routeExtent, new ObstacleItem { Kind = "route", Edge = edge, Routed = routed, Sequence = sequence, Bounds = routeExtent });
        foreach (var point in routed.Points) rightmostRoutedEdge = Math.Max(rightmostRoutedEdge, point.X);
        foreach (var end in (string[])["from", "to"])
        {
            var nodeId = edge[end];
            if (nodeId is null) continue;
            var byEdge = nodeRoutes.Get(IdKey(nodeId));
            if (byEdge is null) { byEdge = new(); nodeRoutes.Set(IdKey(nodeId), byEdge); }
            byEdge.Set(edge, routed);
        }
        var label = edgeIndexByEdge.Has(edge) ? LabelRectFor(edge, edgeIndexByEdge.Get(edge)) : null;
        if (label is not null)
        {
            obstacleGrid.Insert(RectToBounds(label), new ObstacleItem { Kind = "label", Edge = edge, Rect = label, Sequence = sequence });
            rightmostRoutedEdge = Math.Max(rightmostRoutedEdge, label.X + label.Width);
        }
        return routed;
    }

    // pathFor(edge, automaticPlan = null): the readable branches (:5087-5114) take the controlled route (K7, .Pins.cs) and the
    // automatic plan (a reserved straight preview, or readableAutomaticRoute); the non-readable branch (:5115-5125) routes v1 edges
    // and any controlled v2 edge readableControlledRoute returns null for.
    RoutedPath PathFor(JsonObject edge, Plan? automaticPlan = null)
    {
        if (pathCache.Get(edge) is { } cached) return cached;
        var from = NodeOf(edge.Str("from"));
        var to = NodeOf(edge.Str("to"));
        if (IsReadableControlledRoute(edge) && ReadableControlledRoute(edge, from!, to!) is { } controlled)
        {
            readableSideCache.Set(edge, new(controlled.FromSide, controlled.ToSide));
            return RegisterRouted(edge, new RoutedPath(Geometry.PolylinePath(controlled.Points), controlled.Points));
        }
        var ports = automaticPorts.Get(edge);
        var (fromSide, toSide) = EdgeSides(edge);
        if (IsReadableAutomaticRoute(edge))
        {
            var planned = automaticPlan ?? ReadableAutomaticRoute(edge, from!, to!, new(fromSide, toSide), ports);
            readableSideCache.Set(edge, new(planned.FromSide, planned.ToSide));
            return RegisterRouted(edge, new RoutedPath(Geometry.PolylinePath(planned.Points), planned.Points));
        }
        var start = ports?.From ?? Geometry.Anchor(from!, fromSide);
        var end = ports?.To ?? Geometry.Anchor(to!, toSide);
        List<Pt> authoredPoints = [start, .. RouteVia(edge, from!, to!, start, end, fromSide, toSide), end];
        var hasAbsoluteRoutePins = edge["via"] is JsonArray || edge.ContainsKey("channelX") || edge.ContainsKey("channelY");
        var points = V2 && !hasAbsoluteRoutePins ? Geometry.NormalizeRoutePoints(authoredPoints) : authoredPoints;
        return RegisterRouted(edge, new RoutedPath(Geometry.PolylinePath(points), points));
    }

    LabelRect? LabelRectFor(JsonObject edge, int relationIndex)
    {
        if (!edge["label"].Truthy() || !Has(edge.Str("from")) || !Has(edge.Str("to"))) return null;
        var routed = PathFor(edge);
        if (labelRectCache.Get(routed) is { } cached) return cached;
        var (lx, ly) = WorkflowEdgeLabelPoint(edge, routed.Points);
        var width = WorkflowLabelWidth(edge.Str("label"));
        var rect = new LabelRect
        {
            Relation = edge, RelationIndex = relationIndex, Label = edge.Str("label"),
            X = lx - width / 2, Y = ly - 10, Width = width, Height = 14, Lx = lx, Ly = ly,
        };
        labelRectCache.Set(routed, rect);
        return rect;
    }
}
