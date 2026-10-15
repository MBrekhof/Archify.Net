using System.Text.Json.Nodes;

namespace Archify.Net;

// dataflow-compiler.mjs. compileDataflow is the retry wrapper around one isolated `attempt` per `build(rebalancePorts)`; the
// closure state of `build` is a fresh Build per attempt. The automatic-showcase clusters (B, C, H, I, M, N, O-joint and the
// showcase branches of F/J/K) are declared in DataflowCompiler.Automatic.cs; every caller sits behind `automaticShowcase`.
internal sealed partial class DataflowCompiler(RenderContext ctx, JsonObject dataflow, string? qualityProfile)
{
    static class Layout
    {
        public const double StageY = 46, StageH = 36, StageBottomPad = 74, LeftX = 100, ColGap = 215, StageW = 168, NodeW = 112, NodeH = 58;
        public static readonly double[] RowYs = [128, 242, 356, 470, 584];
        public const double LabelH = LabelCapacity.FlowLabelHeight;
    }
    // nodeTextFit (:53-58): the minimums are the shared contextFontMinimum at every use site.
    const double SublabelPreferred = 7, TagPreferred = 7;
    // Mirrors the authored minimum in schemas/dataflow.schema.json: a repair that
    // would need a node shorter than the schema allows cannot be offered.
    const double MinimumNodeHeight = 36;

    static readonly string[] LabelPinKeys = ["labelAt", "labelDx", "labelDy", "labelSegment"];
    static readonly string[] FlowPinKeys = ["fromSide", "toSide", "channelX", "channelY", "labelAt", "labelDx", "labelDy", "labelSegment"];

    // All geometry is local to an attempt. File publication and source loading
    // stay in the CLI renderer and happen once, after a complete valid SVG exists.
    public string Compile()
    {
        try { return Attempt(false)!; }
        catch (ArchifyDiagnosticException initial)
        {
            if (!LayoutFailure(initial)) throw;
            try
            {
                var recovered = Attempt(true);
                if (recovered is not null) return recovered;
            }
            catch (ArchifyDiagnosticException error)
            {
                // A failed geometry proposal leaves the original actionable diagnosis.
                // Unexpected implementation errors still escape with their own stack.
                if (!LayoutFailure(error)) throw;
            }
            throw;   // `throw initial`: still inside its catch, so the bare rethrow is the same exception
        }
    }

    static bool LayoutFailure(ArchifyDiagnosticException error) =>
        error.Diagnostics.Count > 0 && error.Message.StartsWith("Data-flow layout validation failed:", StringComparison.Ordinal);

    // :1579-1587: the carrier's diagnostics become what this attempt recorded (rendererFailure under isolated recording).
    string? Attempt(bool rebalancePorts) => ctx.Diagnostics.WithIsolatedRecording(() =>
    {
        try { return new Build(ctx, dataflow, qualityProfile, rebalancePorts).Run(); }
        catch (ArchifyDiagnosticException error)
        {
            throw new ArchifyDiagnosticException(error.Message, ctx.Diagnostics.RendererFailure(error).diagnostics);
        }
    });

    sealed record RoutedPath(string D, List<Pt> Points);
    readonly record struct PortItem(JsonObject? Flow, bool Incoming, double Width);

    sealed partial class Build
    {
        readonly RenderContext ctx;
        readonly JsonObject dataflow;
        readonly JsonObject meta;
        readonly string? locale;
        readonly string? qualityProfile;
        readonly bool rebalancePorts;
        readonly List<JsonObject> stages;
        readonly List<JsonObject> authoredNodes;
        readonly List<JsonObject> flows;
        readonly bool automaticShowcase;
        readonly double[] viewBox;
        readonly double contextFontMinimum;
        readonly JsMap<string, Box> nodes = new();
        // node.autoHeightInset (:235, :1436), kept beside the shared Box.
        readonly JsMap<string, double> autoHeightInset = new();
        readonly JsMap<JsonObject, string> automaticSourceSides = new();
        readonly JsMap<string, int> nodeSteps = new();
        readonly JsMap<JsonObject, RoutedPath> pathCache = new();
        JsMap<JsonObject, SpreadPorts> automaticPorts = new();
        JsSet<JsonObject> resolvedCrossoverFlows = new();   // `.clear()` at :1373 → a fresh set
        List<JsonObject> routable = [];
        const double DETOUR_NODE_GAP = 12;
        const double ENDPOINT_STUB = 24;
        readonly JsMap<JsonObject, Pt> resolvedLabelPoints = new();
        readonly JsSet<JsonObject> twoLineOcclusions = new();
        List<LabelRect> initialLabelRects = [];
        List<Frame> compositionFrames = [];
        List<LabelRect> resolvedLabelRects = [];
        readonly List<LegendEntry> legendCatalog;

        static string N(double v) => Js.Num(v);
        static bool Truthy(string? s) => !string.IsNullOrEmpty(s);
        static string Pad2(int value) => Js.Num(value).PadLeft(2, '0');   // String(n).padStart(2, '0')
        static List<Segment> SegmentsOf(IReadOnlyList<Pt> points)
        {
            var segments = new List<Segment>();
            for (var index = 1; index < points.Count; index++) segments.Add(new(points[index - 1], points[index]));
            return segments;
        }
        Box? NodeOf(string? id) => id is null ? null : nodes.Get(id);
        bool Has(string? id) => id is not null && nodes.Has(id);
        // sourceEvidence?.nodes?.[node.id]?.length — null until phase 4 ports repository evidence.
        bool SourceBadgeFor(string id) => ctx.SourceEvidence.Obj("nodes").Arr(id) is { Count: > 0 };
        static double StageX(double index) => Layout.LeftX + index * Layout.ColGap;
        static double RowY(double? row) => row is { } r && r >= 0 && r < Layout.RowYs.Length && r == Math.Floor(r) ? Layout.RowYs[(int)r] : double.NaN;
        // `{ ...other, relation: { ...other.relation, labelAt } }`: a new rect whose relation is a fresh copy (R9: the original
        // flow object keeps its identity for the rect at `index`).
        static LabelRect WithRelation(LabelRect other, JsonObject relation) => new()
        {
            Relation = relation, RelationIndex = other.RelationIndex, Label = other.Label, X = other.X, Y = other.Y,
            Width = other.Width, Height = other.Height, Lx = other.Lx, Ly = other.Ly, Lines = other.Lines,
        };
        static LabelRect PinnedCopy(LabelRect other)
        {
            var relation = other.Relation.DeepClone().AsObject();
            relation["labelAt"] = new JsonArray(other.Lx ?? double.NaN, other.Ly ?? double.NaN);
            var copy = WithRelation(other, relation);
            copy.Ly = (other.Ly ?? double.NaN) - 1;
            return copy;
        }
        List<Rect> Titles() => compositionFrames.Select(frame => new Rect(frame.X, frame.Y, frame.Width, Layout.StageH)).ToList();
        List<LabelRoute> RoutesForPlacement() => flows.Select((flow, relationIndex) => (flow, relationIndex))
            .Where(e => Has(e.flow.Str("from")) && Has(e.flow.Str("to")))
            .Select(e => new LabelRoute(e.relationIndex, PathFor(e.flow).Points)).ToList();

        public Build(RenderContext ctx, JsonObject dataflow, string? qualityProfile, bool rebalancePorts)
        {
            this.ctx = ctx;
            this.dataflow = dataflow;
            this.qualityProfile = qualityProfile;
            this.rebalancePorts = rebalancePorts;
            meta = ctx.Meta!;
            locale = meta.Str("locale");
            stages = dataflow["stages"].AsArrayOrEmpty().Select(node => (JsonObject)node!).ToList();
            authoredNodes = dataflow["nodes"].AsArrayOrEmpty().Select(node => (JsonObject)node!).ToList();
            flows = dataflow["flows"].AsArrayOrEmpty().Select(node => (JsonObject)node!).ToList();

            // Explicit geometry preserves established node sizing, typography, height,
            // routing and label placement. An omitted canvas width still fits its content
            // in either profile, including authored node widths.
            automaticShowcase = qualityProfile == "showcase"
                && !meta.ContainsKey("viewBox")
                && !authoredNodes.Any(node => node.ContainsKey("width"));
            var stageRight = StageX(stages.Count - 1) + Layout.StageW / 2;
            var nodeRights = authoredNodes.Select(node => StageX(node.Num("stage") ?? double.NaN) + Js.Or(node.Num("width"), Layout.NodeW) / 2)
                .Where(double.IsFinite).ToList();
            var pinnedLabelRights = flows
                .Where(flow => Truthy(flow.Str("label")) && flow["labelAt"] is JsonArray at && Geometry.IsFinitePoint([.. at.Select(J.Coord)]))
                .Select(flow => J.Coord(flow["labelAt"]![0]) + LabelCapacity.FlowLabelSize(flow).Width / 2).ToList();
            // Automatic width fits stages/nodes with 24px right pad. The old 940px floor
            // left a blank third of the canvas on 2–3 stage drafts; five default stages
            // still measure ~1068 and stay unchanged. Height starts at 720 and is refit
            // by heightForLabels for automatic showcase.
            var contentWidth = Math.Ceiling(Js.MaxOf([stageRight, .. nodeRights, .. pinnedLabelRights, 0], double.NegativeInfinity) + 24);
            viewBox = meta["viewBox"] is JsonArray authored
                ? [.. authored.Select(J.Coord)]
                : [Math.Max(480, contentWidth), 720];
            contextFontMinimum = automaticShowcase
                ? Math.Max(6, Math.Ceiling(DesktopReadability.MinimumReadableSourceTextPx(viewBox[0]) * 10) / 10)
                : 6;

            foreach (var node in authoredNodes) nodes.Set(node.Str("id") ?? "undefined", MeasureNode(node));   // new Map(entries): first position, last value

            legendCatalog =
            [
                new("emphasis", ctx.Locale.Translate(locale, "legend.dataflow.emphasis"), Interactive: false, SwatchWidth: 34, SwatchGap: 9),
                new("security", ctx.Locale.Translate(locale, "legend.dataflow.security"), Interactive: false, SwatchWidth: 34, SwatchGap: 9),
                new("dashed", ctx.Locale.Translate(locale, "legend.dataflow.dashed"), Interactive: false, SwatchWidth: 34, SwatchGap: 9),
                new("database", ctx.Locale.Translate(locale, "legend.dataflow.database")),
                new("default", ctx.Locale.Translate(locale, "legend.dataflow.default"), Interactive: false, SwatchWidth: 34, SwatchGap: 9),
            ];
        }

        Frame StageFrame(JsonObject stage, int index) => new(
            StageX(index) - Layout.StageW / 2, Layout.StageY, Layout.StageW, viewBox[1] - Layout.StageY - Layout.StageBottomPad,
            Radius: 10, Kind: "stage", Label: stage.Str("label"), Id: JsonValue.Create(index));

        Box MeasureNode(JsonObject node)
        {
            var cx = StageX(node.Num("stage") ?? double.NaN);
            // Expand only unpinned showcase nodes, within their existing stage budget.
            // Very long text still gets an actionable error instead of an unbounded
            // canvas/typography feedback loop.
            var contextWidth = Js.MaxOf(new[] { node.Str("sublabel"), node.Str("tag") }.Select(text =>
                Truthy(text) ? TextFit.MinimumNodeTextWidth(text, Math.Max(7, contextFontMinimum)) + 8 : 0), 0);
            var width = Js.Or(node.Num("width"), automaticShowcase
                ? Math.Max(Layout.NodeW, Math.Min(Math.Min(Layout.StageW, (cx - 24) * 2), Math.Min((viewBox[0] - 24 - cx) * 2, Math.Ceiling(contextWidth))))
                : Layout.NodeW);
            var height = Js.Or(node.Num("height"), Layout.NodeH);
            var y = RowY(node.Num("row")) + Js.Or(node.Num("yOffset"), 0);
            return new Box
            {
                Id = node.Str("id") ?? "undefined", Source = node, Brand = ctx.Brands.BrandMarkFor(node),
                Width = width, Height = height, X = cx - width / 2, Y = y, Cx = cx, Cy = y + height / 2,
            };
        }

        // Move one outgoing arrow off a crowded mixed side when its
        // perpendicular exit has capacity and reaches the target with a clear corner.
        static bool HasPinnedFlowGeometry(JsonObject flow) => flow["via"].Truthy() || (flow["route"].Truthy() && flow.Str("route") != "auto")
            || FlowPinKeys.Any(flow.ContainsKey);
        // Arrowheads are seven stroke-width units tall. Reserve two units of
        // clear ink within the automatic spread's existing 18px spacing limit.
        static double PortSpan(IReadOnlyList<PortItem> items) => 32 + Math.Max(0, items.Count - 1) * Math.Min(18,
            Math.Ceiling(Js.MaxOf(items.Select(item => item.Width * (item.Incoming ? 7 : 1)), 0)) + 2);

        public string? Run()
        {
            if (automaticShowcase && rebalancePorts) RebalancePorts();
            if (rebalancePorts && automaticSourceSides.Count == 0) return null;

            // Seven incoming arrows cannot fit on a 58px box after the corner gutters.
            // Use spare row space around its centre, so clear same-row outputs stay
            // straight. Explicit geometry and incident route/label pins stay authoritative.
            if (automaticShowcase) GrowCrowdedNodes();

            for (var index = 0; index < flows.Count; index++)
            {
                var flow = flows[index];
                if (flow.Str("from") is { } from && !nodeSteps.Has(from)) nodeSteps.Set(from, index);
                if (flow.Str("to") is { } to && !nodeSteps.Has(to)) nodeSteps.Set(to, index + 1);
            }
            for (var index = 0; index < authoredNodes.Count; index++)
            {
                var id = authoredNodes[index].Str("id");
                if (id is not null && !nodeSteps.Has(id)) nodeSteps.Set(id, index);
            }

            // The shared default of 14px packs two 10px arrowheads on top of each other.
            // Raise the ceiling so a side that has room spreads them; a short side still
            // clamps to usable/(n-1) and keeps the previous layout.
            automaticPorts = Geometry.AutomaticPortSpread(dataflow["flows"], nodes, maxSpacing: 18,
                sideFor: (flow, endpoint) => endpoint == "source" ? FlowSides(flow).FromSide : FlowSides(flow).ToSide);

            routable = flows.Where(flow => Has(flow.Str("from")) && Has(flow.Str("to"))).ToList();

            // Preset paths can cross unrelated flows. Prefer a short clear detour, but
            // give resolved crossings the same finite reading cost as Architecture rather
            // than forcing a perimeter excursion. Crossings with authored routes still
            // need a clear detour; only actual automatic/automatic crosses get halos.
            if (automaticShowcase) RepairAutomaticRoutes();

            // Route detours can reverse the direction used to sort a shared node's ports.
            // Reorder the existing slots by the first turn of the settled route, only when
            // the whole automatic group becomes shorter without adding any intersection.
            if (automaticShowcase) ReorderAutomaticPorts();

            initialLabelRects = [];
            for (var relationIndex = 0; relationIndex < flows.Count; relationIndex++)
            {
                var flow = flows[relationIndex];
                if (!Truthy(flow.Str("label")) || !Has(flow.Str("from")) || !Has(flow.Str("to"))) continue;
                var (lx, ly) = LabelPointFor(flow, PathFor(flow).Points);
                var (width, height) = LabelCapacity.FlowLabelSize(flow);
                // The shared placer uses a 10px baseline inset; Dataflow masks use 11px.
                // Adapt the baseline so it tests exactly the rectangle we later render.
                var rect = new LabelRect
                {
                    Relation = flow, RelationIndex = relationIndex, Label = flow.Str("label"),
                    X = lx - width / 2, Y = ly - 11, Width = width, Height = height, Lx = lx, Ly = ly - 1,
                };
                // Only repair the additional footprint of an unpinned second line. A
                // single-line plate may intentionally interrupt its own route, especially
                // on a vertical segment; that established placement remains authoritative.
                if (Truthy(flow.Str("classification")) && !LabelPinKeys.Any(flow.ContainsKey))
                {
                    if (ExtraLabelLineMasksRoute(rect, PathFor(flow).Points)) twoLineOcclusions.Add(flow);
                }
                initialLabelRects.Add(rect);
            }
            // Give the shared placer room for complete initial plates before its search;
            // width growth here does not recompute the measured nodes or cached routes.
            FitAutomaticWidthToLabels(initialLabelRects);
            if (automaticShowcase) viewBox[1] = HeightForLabels(initialLabelRects)
                + Js.MaxOf(initialLabelRects.Select(rect => rect.Height - Layout.LabelH), 0);
            compositionFrames = stages.Select(StageFrame).ToList();

            resolvedLabelRects = (automaticShowcase ? PlaceInitialLabels(initialLabelRects) : initialLabelRects).Select(rect =>
            {
                // Dataflow has compact 16px plates. Permit one clearance gutter beyond
                // two plate heights (36px), which covers a label immediately above a node,
                // while rejecting a collision-free island far from the labelled route.
                var ownPoints = PathFor(rect.Relation).Points;
                var nearby = SegmentsOf(ownPoints).Any(segment => (Geometry.SegmentRectClearanceWithin(segment, rect, 36) ?? 0) <= 36);
                var original = initialLabelRects.First(label => ReferenceEquals(label.Relation, rect.Relation));
                var kept = nearby ? rect : original;
                var resolved = kept.Clone();
                resolved.Ly = (kept.Ly ?? double.NaN) + 1;
                if (automaticShowcase) resolvedLabelPoints.Set(rect.Relation, new Pt(resolved.Lx ?? double.NaN, resolved.Ly ?? double.NaN));
                return resolved;
            }).ToList();

            // The ordinary shared placer allows a plate to interrupt its own connector.
            // For the second-line defect above, keep the complete plate beside the route
            // instead. This local search freezes all other labels and authored geometry,
            // and uses the same node/title/label/other-route checks as ordinary placement.
            for (var index = 0; index < resolvedLabelRects.Count; index++)
            {
                var rect = resolvedLabelRects[index];
                if (!twoLineOcclusions.Has(rect.Relation)) continue;
                var ownPoints = PathFor(rect.Relation).Points;
                if (!SegmentsOf(ownPoints).Any(segment => Geometry.SegmentIntersectsRect(segment, rect))) continue;
                var original = initialLabelRects.First(label => ReferenceEquals(label.Relation, rect.Relation));
                var extraHeight = original.Height - Layout.LabelH;
                var preferred = original.Clone();
                preferred.Y = original.Y - extraHeight;
                preferred.Ly = (original.Ly ?? double.NaN) - extraHeight;
                // `components: [...nodes.values(), ...ownRouteBands]`: the placer reads components as rects only.
                var ownRouteBands = SegmentsOf(ownPoints).Select(segment => new Box
                {
                    Id = "", Source = new JsonObject(),
                    X = Math.Min(segment.Start.X, segment.End.X) - 2, Y = Math.Min(segment.Start.Y, segment.End.Y) - 2,
                    Width = Math.Abs(segment.End.X - segment.Start.X) + 4, Height = Math.Abs(segment.End.Y - segment.Start.Y) + 4,
                });
                var labels = resolvedLabelRects.Select((other, otherIndex) => otherIndex == index ? preferred : PinnedCopy(other)).ToList();
                var routes = RoutesForPlacement();
                var titles = Titles();
                var replacement = AutomaticLabels.PlaceAutomaticLabels(labels, routes, [.. nodes.Values, .. ownRouteBands], titles, viewBox,
                    placementBottom: viewBox[1] - Layout.StageBottomPad, keepFallbackNearRoute: true, maxOwnRouteDistance: 36)[index];
                // An exhausted shared search returns its supplied preferred rect. That is
                // not proof of a safe replacement: preserve the previously accepted label
                // if the complete footprint cannot fit near its connector without collisions.
                var clear = replacement.X >= 0 && replacement.Y >= 0
                    && replacement.X + replacement.Width <= viewBox[0]
                    && replacement.Y + replacement.Height <= viewBox[1] - Layout.StageBottomPad
                    && !nodes.Values.Select(node => (Rect)node).Concat(titles).Any(obstacle => Geometry.RectsOverlap(replacement, obstacle, 2))
                    && !resolvedLabelRects.Where((_, otherIndex) => otherIndex != index).Any(other => Geometry.RectsOverlap(replacement, other, 2))
                    && routes.All(route => SegmentsOf(route.Points).All(segment => (Geometry.SegmentRectClearanceWithin(segment, replacement, 4) ?? 0) >= 4))
                    && SegmentsOf(ownPoints).Any(segment => (Geometry.SegmentRectClearanceWithin(segment, replacement, 36) ?? 0) <= 36);
                if (!clear) continue;
                var resolved = replacement.Clone();
                resolved.Ly = (replacement.Ly ?? double.NaN) + 1;
                resolvedLabelRects[index] = resolved;
                resolvedLabelPoints.Set(rect.Relation, new Pt(resolved.Lx ?? double.NaN, resolved.Ly ?? double.NaN));
            }

            // Labels may occupy the only useful part of an inter-stage corridor after
            // ordinary routing has finished. Try shifting one interior segment past the
            // actual plate, then re-place only the labels that this move obstructs. Valid
            // labels keep their positions. Endpoint geometry and authored controls remain
            // authoritative, and every accepted route clears all final foreign labels.
            if (automaticShowcase) RepairLabelsAndCorridors();

            // Only after routes settle, try two complete rows for an unresolved wide
            // label. Keep the plate immediately beside its own arrow, with every other
            // route and label fixed; an empty island above a node is not a useful slot.
            if (automaticShowcase) WrapBlockedLabels();

            // A shared placement search may retain an unpinned original plate when none
            // of its candidates fit the compact width. Include every final rendered plate
            // without moving nodes, routes or labels; an authored canvas remains fixed.
            FitAutomaticWidthToLabels(resolvedLabelRects);
            // A bounded label move may also extend below the initial route footprint.
            if (automaticShowcase)
            {
                viewBox[1] = HeightForLabels(resolvedLabelRects);
                RefitFrameHeights();
            }

            // Resolve a remaining label and its occupied corridors as one proposal.
            // Earlier valid placements and authored geometry remain authoritative.
            if (automaticShowcase) RepairJointLabelRoutes();

            ValidateDataflow();
            return RenderSvg();
        }

        // :1354, :1372: `frame.height = …` in place; Frame is a record, so the element is replaced.
        void RefitFrameHeights()
        {
            for (var index = 0; index < compositionFrames.Count; index++)
                compositionFrames[index] = compositionFrames[index] with { Height = viewBox[1] - Layout.StageY - Layout.StageBottomPad };
        }

        // ---------------------------------------------------------------- validation (:250-562)
        void ValidateDataflow()
        {
            var problems = new List<string>();
            var diagnostics = new List<Diagnostic>();
            if (nodes.Count != authoredNodes.Count) problems.Add("Node ids must be unique.");

            var stageCount = stages.Count;
            foreach (var node in nodes.Values)
            {
                var stage = node.Source.Num("stage");
                if (stage is null || stage < 0 || stage >= stageCount)
                    problems.Add($"Node \"{node.Id}\" uses invalid stage {RawValue(node.Source, "stage")} — valid stages are 0..{N(stageCount - 1)}.");
                var row = node.Source.Num("row");
                if (row is null || row < 0 || row >= Layout.RowYs.Length)
                    problems.Add($"Node \"{node.Id}\" uses invalid row {RawValue(node.Source, "row")} — valid rows are 0..{N(Layout.RowYs.Length - 1)}.");
                if (!Geometry.IsFinitePoint(node.X, node.Y, node.Cx, node.Cy))
                {
                    problems.Add($"Node \"{node.Id}\" produced non-finite coordinates — check stage, row, width, height, and yOffset are numbers.");
                    continue;
                }
                if (node.X < 24 || node.X + node.Width > viewBox[0] - 24)
                    problems.Add($"Node \"{node.Id}\" exceeds the horizontal bounds of the viewBox — reduce node.width or increase meta.viewBox[0].");
                var diagramAreaTop = Layout.StageY + Layout.StageH + 22;
                var diagramAreaBottom = viewBox[1] - Layout.StageBottomPad;
                // y is the node's top edge, so the top bound moves only with yOffset —
                // height cannot repair it. Both bounds are reported independently, because
                // a node tall enough to cross the whole area violates both at once.
                if (node.Y < diagramAreaTop)
                {
                    var message = $"Node \"{node.Id}\" starts above the readable diagram area — keep y at or above {N(diagramAreaTop)}.";
                    diagnostics.Add(new("dataflow/node-above-area", Severity.Error, message,
                        new JsonObject { ["diagramType"] = "dataflow", ["node"] = node.Id },
                        new JsonObject { ["y"] = node.Y, ["areaTop"] = diagramAreaTop, ["yOffset"] = Js.Or(node.Source.Num("yOffset"), 0) },
                        [$"raise or remove the negative yOffset on node \"{node.Id}\""], null));
                    problems.Add(message);
                }
                if (node.Y + node.Height > diagramAreaBottom)
                {
                    var nodeBottom = node.Y + node.Height;
                    var requiredViewBoxHeight = nodeBottom + Layout.StageBottomPad;
                    // Only offer a repair that can clear this bound on its own. Lowering
                    // yOffset moves the whole node, which reaches the bound only while the
                    // node is short enough to fit between them at all; reducing height moves
                    // only the bottom edge, and only reaches the bound while a node short
                    // enough to do it is still legal to author.
                    var supportedFixes = new List<string>();
                    var roomForNode = diagramAreaBottom - diagramAreaTop;
                    // The height that clears the bound at the node's current y. The room
                    // between the bounds is the cap only while the node can still move; once
                    // it sits below the area, the cap is what remains below it.
                    var heightCapAtY = Math.Min(roomForNode, diagramAreaBottom - node.Y);
                    if (node.Height <= roomForNode) supportedFixes.Add($"lower yOffset on node \"{node.Id}\"");
                    if (heightCapAtY >= MinimumNodeHeight) supportedFixes.Add($"reduce node \"{node.Id}\" height to at most {N(heightCapAtY)}");
                    // A dataflow canvas with an authored meta.viewBox gets no readable-scroll
                    // exception, so the raised canvas is offered as a standalone repair only
                    // while the delivered page still fits the target viewport — the same
                    // page-fit math the checker applies for composition/viewport-height. The
                    // raise is bounded above too: growing the height lowers the canvas ratio,
                    // and under the wide threshold the Reader can no longer narrow the page,
                    // so advice that stopped at the minimum let an author overshoot into a
                    // certain visual-check failure.
                    var raisedOverflow = DesktopReadability.PredictedFixedWidthOverflow(viewBox[0], requiredViewBoxHeight);
                    var raisedCanvasFits = raisedOverflow is null;
                    var maximumViewBoxHeight = Math.Floor(viewBox[0] / DesktopReadability.DECLARED_WIDE_READER_RATIO);
                    // The ratio boundary can land between integers; step down until the same
                    // predicate the checker uses accepts the height.
                    while (maximumViewBoxHeight > 1 && DesktopReadability.PredictedFixedWidthOverflow(viewBox[0], maximumViewBoxHeight) is not null)
                        maximumViewBoxHeight -= 1;
                    var raiseTarget = maximumViewBoxHeight > requiredViewBoxHeight
                        ? $"to at least {N(requiredViewBoxHeight)} and at most {N(maximumViewBoxHeight)}"
                        : $"to {N(requiredViewBoxHeight)}";
                    if (raisedCanvasFits) supportedFixes.Add($"raise meta.viewBox[1] {raiseTarget}, then rerun deliver and visual-check");
                    // The bound belongs to y + height, so name the y that satisfies it. The
                    // unadjusted "keep y between 104 and areaBottom" let an author follow the
                    // message exactly and still fail.
                    var extent = node.Height <= roomForNode
                        ? $"keep y within [{N(diagramAreaTop)}, {N(diagramAreaBottom - node.Height)}] so that y + height does not pass {N(diagramAreaBottom)}"
                        : heightCapAtY >= MinimumNodeHeight
                            ? $"it is taller than the area from y = {N(diagramAreaTop)} to y = {N(diagramAreaBottom)}, so no yOffset can fit it — reduce node height to at most {N(heightCapAtY)} and keep y within [{N(diagramAreaTop)}, {N(diagramAreaBottom - heightCapAtY)}] so that y + height does not pass {N(diagramAreaBottom)}"
                            : $"it is taller than the area from y = {N(diagramAreaTop)} to y = {N(diagramAreaBottom)} and sits too low for any legal height, so no single change clears this bound — reduce node height to at most {N(roomForNode)} and move it up so that y + height does not pass {N(diagramAreaBottom)}";
                    var raiseClause = raisedCanvasFits
                        ? $" Raising meta.viewBox[1] {raiseTarget} also fits it and still fits the target viewport."
                        : $" Raising meta.viewBox[1] to {N(requiredViewBoxHeight)} would fit the node, but the page then reaches at least ~{N(raisedOverflow!.PageHeightPx)}px in the 1440x900 viewport, so the raise cannot stand on its own — compact the node instead.";
                    var message = $"Node \"{node.Id}\" exceeds the readable diagram area — {extent}.{raiseClause}";
                    var evidence = new JsonObject
                    {
                        ["y"] = node.Y, ["height"] = node.Height, ["areaBottom"] = diagramAreaBottom,
                        ["requiredViewBoxHeight"] = requiredViewBoxHeight, ["requiredViewBoxFitsViewport"] = raisedCanvasFits,
                        ["maximumViewBoxHeight"] = maximumViewBoxHeight,
                    };
                    if (!raisedCanvasFits) evidence["requiredViewBoxPageHeightPx"] = raisedOverflow!.PageHeightPx;
                    evidence["viewBoxHeight"] = viewBox[1];
                    diagnostics.Add(new("dataflow/node-below-area", Severity.Error, message,
                        new JsonObject { ["diagramType"] = "dataflow", ["node"] = node.Id }, evidence, supportedFixes, null));
                    problems.Add(message);
                }
                var label = node.Source.Str("label");
                var estLabelW = Utils.TextUnits(label) * 6.2;
                if (estLabelW > node.Width + 6)
                    problems.Add($"Label \"{label ?? "undefined"}\" (~{N(Js.Round(estLabelW))}px) is wider than component \"{node.Id}\" ({N(node.Width)}px) — shorten the label or increase node.width.");
                var brandRailProblem = BrandMarks.BrandTopRailProblem(node.Brand, node.Id, label, node.Width, 8);
                if (brandRailProblem is not null) problems.Add(brandRailProblem);
                // sublabel and tag render as single unwrapped <text> elements; shrink-to-fit
                // handles the ordinary case, this rejects what it cannot rescue.
                var availableTextW = TextFit.AvailableNodeTextWidth(node.Width);
                foreach (var (field, value, minimum) in new[]
                {
                    ("Sublabel", node.Source.Str("sublabel"), contextFontMinimum),
                    ("Tag", node.Source.Str("tag"), contextFontMinimum),
                })
                {
                    if (!Truthy(value)) continue;
                    var minimumW = TextFit.MinimumNodeTextWidth(value, minimum);
                    if (minimumW > availableTextW)
                        problems.Add($"{field} \"{value}\" needs ~{N(Math.Ceiling(minimumW))}px at the {N(minimum)}px legible minimum, but node \"{node.Id}\" provides {N(availableTextW)}px — shorten the {field.ToLowerInvariant()} or increase node.width.");
                }
            }

            for (var i = 0; i < authoredNodes.Count; i++)
            {
                for (var j = i + 1; j < authoredNodes.Count; j++)
                {
                    var a = NodeOf(authoredNodes[i].Str("id"))!;
                    var b = NodeOf(authoredNodes[j].Str("id"))!;
                    if (Geometry.RectsOverlap(a, b, 10))
                        problems.Add($"Nodes \"{a.Id}\" and \"{b.Id}\" are less than 10px apart — move one to another stage/row or adjust yOffset.");
                }
            }

            var stageById = new JsMap<string, double?>();
            foreach (var node in authoredNodes) stageById.Set(node.Str("id") ?? "undefined", node.Num("stage"));
            for (var flowIndex = 0; flowIndex < flows.Count; flowIndex++)
            {
                var flow = flows[flowIndex];
                foreach (var (field, endpoint) in new[] { ("from", "source"), ("to", "target") })
                {
                    var unknownId = flow.Str(field);
                    if (Has(unknownId)) continue;
                    var problem = $"Flow \"{(Truthy(flow.Str("label")) ? flow.Str("label") : unknownId ?? "undefined")}\" references unknown {endpoint} \"{unknownId ?? "undefined"}\".";
                    var anchorId = flow.Str(field == "from" ? "to" : "from");
                    var anchorStage = anchorId is not null && stageById.Has(anchorId) ? stageById.Get(anchorId) : null;
                    double StageDelta(string id) => anchorStage is null || stageById.Get(id) is not { } stage
                        ? 0
                        : field == "to" ? stage - anchorStage.Value : anchorStage.Value - stage;
                    var candidates = stageById.Keys.Where(id => id != anchorId).ToList();
                    Js.Sort(candidates, (a, b) =>
                    {
                        var da = StageDelta(a);
                        var db = StageDelta(b);
                        var forwardDiff = (db > 0 ? 1 : 0) - (da > 0 ? 1 : 0);
                        return forwardDiff != 0 ? forwardDiff : da - db;
                    });
                    var subject = new JsonObject { ["diagramType"] = "dataflow", ["flow"] = flow["id"]?.DeepClone(), ["path"] = $"/flows/{N(flowIndex)}/{field}" };
                    if (flow["from"] is { } fromNode) subject["from"] = fromNode.DeepClone();
                    if (flow["to"] is { } toNode) subject["to"] = toNode.DeepClone();
                    var evidence = new JsonObject { ["endpoint"] = endpoint };
                    if (flow[field] is { } unknownNode) evidence["unknownNodeId"] = unknownNode.DeepClone();
                    evidence["availableNodeIds"] = new JsonArray([.. candidates.Select(id => (JsonNode?)id)]);
                    diagnostics.Add(new("dataflow/unknown-endpoint", Severity.Error, problem, subject, evidence,
                        [.. candidates.Take(3).Select(id => $"set /flows/{N(flowIndex)}/{field} to verified node id \"{id}\"")], null));
                    problems.Add(problem);
                }
                if (!Truthy(flow.Str("label"))) problems.Add($"Flow \"{flow.Str("from") ?? "undefined"}\" -> \"{flow.Str("to") ?? "undefined"}\" must include a short data label.");
                if (Has(flow.Str("from")) && Has(flow.Str("to")))
                {
                    var routed = PathFor(flow);
                    var (start, end) = (routed.Points[0], routed.Points[^1]);
                    var distance = Js.Hypot(end.X - start.X, end.Y - start.Y);
                    if (distance < 34) problems.Add($"Flow \"{flow.Str("label")}\" is too short ({N(Js.Round(distance))}px; minimum 34px) — route it through a channel or spread its nodes.");
                    if (flow["via"] is JsonArray via)
                    {
                        for (var segmentIndex = 0; segmentIndex < routed.Points.Count - 1; segmentIndex += 1)
                        {
                            var segmentStart = routed.Points[segmentIndex];
                            var segmentEnd = routed.Points[segmentIndex + 1];
                            var isDiagonal = Math.Abs(segmentStart.X - segmentEnd.X) > 0.01 && Math.Abs(segmentStart.Y - segmentEnd.Y) > 0.01;
                            if (!isDiagonal) continue;
                            var viaIndex = Math.Min(segmentIndex, via.Count - 1);
                            problems.Add($"Flow \"{flow.Str("label")}\" has a diagonal segment from ({N(segmentStart.X)}, {N(segmentStart.Y)}) to ({N(segmentEnd.X)}, {N(segmentEnd.Y)}) — align via[{N(viaIndex)}] with its adjacent point by sharing the same x or y coordinate.");
                        }
                    }
                }
            }

            var sink = ctx.Diagnostics;
            var relations = dataflow["flows"];
            var endpointIds = new JsSet<string>();
            foreach (var id in nodes.Keys) endpointIds.Add(id);
            problems.AddRange(Geometry.CleanEndpointSideProblems(sink, relations, endpointIds, PathPoints, "dataflow", "flows",
                flow => FlowSides(flow).FromSide, flow => FlowSides(flow).ToSide,
                routeHint: "keep automatic routing, or choose fromSide/toSide and via points whose first and final segments cross node borders perpendicularly"));
            problems.AddRange(Geometry.CleanFlowProblems(sink, relations, nodes.Values, PathPoints, "dataflow", "flows", "node",
                routeHint: "adjust fromSide/toSide, set route/via or channelX/channelY, or move the node to another stage/row"));
            problems.AddRange(Geometry.CleanCrossingProblems(sink, relations, endpointIds, PathPoints, "dataflow", "flows",
                profile: qualityProfile,
                crossingResolved: (left, right, _) => resolvedCrossoverFlows.Has(left) && resolvedCrossoverFlows.Has(right),
                routeHint: "adjust route/via or channelX/channelY so the flows use separate stage corridors"));
            problems.AddRange(Geometry.CleanAmbiguousCorridorProblems(sink, relations, endpointIds, PathPoints, "dataflow", "flows",
                profile: qualityProfile,
                routeHint: "adjust route/via or channelX/channelY so unrelated flows do not visually merge"));
            problems.AddRange(Geometry.CleanBorderRunProblems(sink, relations, endpointIds, compositionFrames, PathPoints, "dataflow", "flows",
                qualityProfile,
                routeHint: "adjust route/via or channelX/channelY so the flow crosses the stage perpendicularly instead of following its border"));
            problems.AddRange(Geometry.CleanRouteRhythmProblems(sink, relations, endpointIds, PathPoints, "dataflow", "flows",
                qualityProfile,
                routeHint: "adjust route/via or channelX/channelY so each turn uses a clear inter-stage corridor"));

            var labelRects = resolvedLabelRects;
            foreach (var rect in labelRects)
            {
                foreach (var node in nodes.Values)
                {
                    if (Geometry.RectsOverlap(rect, node, -2))
                        problems.Add($"Label \"{rect.Label}\" overlaps node \"{node.Id}\" — adjust labelDx/labelDy/labelSegment or set labelAt.\n{Geometry.SuggestLabelObstacleFix(rect, rect.Lx ?? double.NaN, rect.Ly ?? double.NaN, node, "node", viewBox, nodes.Values)}");
                }
            }
            for (var i = 0; i < labelRects.Count; i += 1)
            {
                for (var j = i + 1; j < labelRects.Count; j += 1)
                {
                    if (Geometry.RectsOverlap(labelRects[i], labelRects[j], -2))
                        problems.Add($"Labels \"{labelRects[i].Label}\" and \"{labelRects[j].Label}\" overlap — adjust labelDx/labelDy.\n{Geometry.SuggestLabelPairFix(labelRects[i], labelRects[j])}");
                }
            }
            problems.AddRange(Geometry.CleanLabelRouteClearanceProblems(sink, relations, labelRects, endpointIds, PathPoints, "dataflow", "flows",
                qualityProfile,
                routeHint: "adjust labelAt, labelDx, labelDy, or labelSegment; otherwise adjust the other flow route/via/channelX/channelY"));
            problems.AddRange(Geometry.CleanLabelCanvasContainmentProblems(sink, labelRects, viewBox, "dataflow", "flows", qualityProfile));

            var lastStageX = StageX(stages.Count - 1);
            if (lastStageX + Layout.StageW / 2 > viewBox[0] - 24)
                problems.Add($"Stages exceed viewBox width — set meta.viewBox[0] to at least {N(Math.Ceiling(lastStageX + Layout.StageW / 2 + 24))}.");

            if (problems.Count > 0)
            {
                throw ctx.Diagnostics.ThrowDiagnosticProblems("Data-flow layout validation failed", problems,
                    subject: new JsonObject { ["diagramType"] = "dataflow" }, details: diagnostics);
            }
        }

        // `${node.stage}` of a value that failed `typeof === 'number'`.
        static string RawValue(JsonObject node, string key) => node.ContainsKey(key) ? Js.PrimitiveString(node[key]) : "undefined";

        // ---------------------------------------------------------------- routing (:564-717)
        bool SupportsAutomaticCrossover(JsonObject flow) => automaticShowcase && !flow["via"].Truthy()
            && (!flow["route"].Truthy() || flow.Str("route") == "auto")
            && !flow.ContainsKey("channelX") && !flow.ContainsKey("channelY");

        static readonly IReadOnlyDictionary<string, Pt> Outward = new Dictionary<string, Pt>(StringComparer.Ordinal)   // lookup only
        {
            ["left"] = new(-24, 0), ["right"] = new(24, 0), ["top"] = new(0, -24), ["bottom"] = new(0, 24),
        };

        List<Pt> RouteVia(JsonObject flow, Box from, Box to, Pt start, Pt end)
        {
            if (flow["via"].Truthy()) return flow["via"].AsArrayOrEmpty().Select(J.PtOf).ToList();
            var route = flow["route"].Truthy() ? flow.Str("route") : "auto";
            switch (route)
            {
                case "straight":
                    return [];
                case "vertical-channel":
                {
                    var x = flow.Num("channelX") ?? start.X + (end.X > start.X ? 44 : -44);
                    return [new(x, start.Y), new(x, end.Y)];
                }
                case "bottom-channel":
                {
                    var y = flow.Num("channelY") ?? Math.Max(from.Y + from.Height, to.Y + to.Height) + 26;
                    return [new(start.X, y), new(end.X, y)];
                }
                case "top-channel":
                {
                    var y = flow.Num("channelY") ?? Math.Min(from.Y, to.Y) - 24;
                    return [new(start.X, y), new(end.X, y)];
                }
                default:
                {
                    var (fromSide, toSide) = FlowSides(flow);
                    var fromVertical = fromSide is "top" or "bottom";
                    var toVertical = toSide is "top" or "bottom";
                    var acrossDelta = fromVertical && toVertical ? Math.Abs(start.X - end.X) : Math.Abs(start.Y - end.Y);
                    if (automaticShowcase && acrossDelta > 0.0001 && acrossDelta < 16)
                    {
                        // Spread ports can differ by only 7px/14px. Reuse the bounded bridge
                        // rather than emit a midpoint turn below our own rhythm floor.
                        if (AutomaticPortBridgeVia(from, to, start, end, fromSide, toSide) is { } bridge) return bridge;
                    }
                    if (fromVertical != toVertical)
                    {
                        // Perpendicular sides meet at one corner when both endpoints face it;
                        // otherwise each leaves through a short stub before the corner.
                        Pt Stub(Pt point, string side) => Outward.TryGetValue(side, out var o) ? new(point.X + o.X, point.Y + o.Y) : new(double.NaN, double.NaN);
                        var startStub = Stub(start, fromSide);
                        var endStub = Stub(end, toSide);
                        List<List<Pt>> candidates =
                        [
                            [fromVertical ? new(start.X, end.Y) : new(end.X, start.Y)],
                            [startStub, new(startStub.X, endStub.Y), endStub],
                            [startStub, new(endStub.X, startStub.Y), endStub],
                        ];
                        var found = candidates.FirstOrDefault(candidate =>
                        {
                            var points = Geometry.NormalizeRoutePoints([start, .. candidate, end]);
                            return Geometry.RouteHonorsEndpointSides(points, fromSide, toSide)
                                && !nodes.Values.Any(node => SegmentsOf(points).Where((segment, index) =>
                                    Geometry.SegmentIntersectsRect(segment, node, 2)
                                    && !((node.Id == from.Id && index == 0) || (node.Id == to.Id && index == points.Count - 2))).Any());
                        });
                        if (found is not null) return found;
                    }
                    if (automaticShowcase && fromVertical && toVertical) return AutomaticVerticalVia(start, end);
                    if (Math.Abs(start.Y - end.Y) < 4) return [];
                    var midX = start.X + (end.X - start.X) / 2;
                    // Across several stages the midpoint can fall inside a middle stage's
                    // node; then turn in the clear inter-stage gap nearest to it.
                    var gapXs = Enumerable.Range(0, Math.Max(0, stages.Count - 1)).Select(index => (StageX(index) + StageX(index + 1)) / 2)
                        .Where(x => x > Math.Min(start.X, end.X) && x < Math.Max(start.X, end.X)).ToList();
                    Js.Sort(gapXs, (left, right) => Math.Abs(left - midX) - Math.Abs(right - midX));
                    double? turnX = null;
                    foreach (var x in (double[])[midX, .. gapXs])
                    {
                        List<Pt> points = [start, new(x, start.Y), new(x, end.Y), end];
                        if (!nodes.Values.Any(node => node.Id != from.Id && node.Id != to.Id
                            && SegmentsOf(points).Any(segment => Geometry.SegmentIntersectsRect(segment, node, 2))))
                        {
                            turnX = x;
                            break;
                        }
                    }
                    var tx = turnX ?? midX;
                    return [new(tx, start.Y), new(tx, end.Y)];
                }
            }
        }

        (string FromSide, string ToSide) FlowSides(JsonObject flow)
        {
            var from = NodeOf(flow.Str("from"))!;
            var to = NodeOf(flow.Str("to"))!;
            return (
                Geometry.ChosenSide(flow.Str("fromSide"), automaticSourceSides.Get(flow) is { Length: > 0 } side ? side : Geometry.LegacyDefaultFromSide(from, to)),
                Geometry.ChosenSide(flow.Str("toSide"), Geometry.LegacyDefaultToSide(from, to)));
        }

        List<Pt> PathPoints(JsonObject flow) => PathFor(flow).Points;

        RoutedPath PathFor(JsonObject flow)
        {
            if (pathCache.Get(flow) is { } cached) return cached;
            var from = NodeOf(flow.Str("from"))!;
            var to = NodeOf(flow.Str("to"))!;
            var ports = automaticPorts.Get(flow);
            var (fromSide, toSide) = FlowSides(flow);
            var start = ports?.From ?? Geometry.Anchor(from, fromSide);
            var end = ports?.To ?? Geometry.Anchor(to, toSide);
            if (automaticShowcase && !flow.ContainsKey("labelDx") && !flow.ContainsKey("labelDy") && !flow.ContainsKey("labelSegment"))
                (start, end) = AlignAutomaticPorts(flow, from, to, start, end, fromSide, toSide, ports);
            // Drop consecutive duplicate points so a purely vertical (or horizontal)
            // auto-route never emits a zero-length final segment — SVG derives
            // marker-end orientation from the last segment, and a degenerate segment
            // leaves the arrowhead angle undefined (see #169).
            List<Pt> rawPoints = [start, .. RouteVia(flow, from, to, start, end), end];
            var points = new List<Pt>();
            foreach (var p in rawPoints)
            {
                if (points.Count == 0 || Math.Abs(p.X - points[^1].X) > 0.0001 || Math.Abs(p.Y - points[^1].Y) > 0.0001) points.Add(p);
            }
            // Guard against an all-degenerate route (e.g. start === end): keep both
            // endpoints so the path is still well-formed even if the marker is hidden.
            if (points.Count < 2) points.Add(end);
            var routed = new RoutedPath(Geometry.PolylinePath(points), points);
            pathCache.Set(flow, routed);
            return routed;
        }

        // ---------------------------------------------------------------- crossing helpers (:719-759), used by the automatic clusters
        static bool Related(JsonObject left, JsonObject right) =>
            new[] { left.Str("from"), left.Str("to") }.Any(id => id == right.Str("from") || id == right.Str("to"));
        bool Crosses(JsonObject left, JsonObject right) => SegmentsOf(PathFor(left).Points).Any(a => SegmentsOf(PathFor(right).Points).Any(b =>
            Geometry.ProperSegmentIntersection(a.Start, a.End, b.Start, b.End) is not null));
        List<JsonObject> Unrelated(JsonObject flow) => routable.Where(other => !ReferenceEquals(other, flow) && !Related(flow, other)).ToList();
        static double Length(IReadOnlyList<Pt> points) => SegmentsOf(points).Aggregate(0.0, (sum, s) => sum + Math.Abs(s.End.X - s.Start.X) + Math.Abs(s.End.Y - s.Start.Y));
        bool IntrudesNode(JsonObject flow, IReadOnlyList<Pt> points) => nodes.Values.Any(node => SegmentsOf(points).Where((segment, index) =>
            Geometry.SegmentIntersectsRect(segment, node, 2)
            && !((node.Id == flow.Str("from") && index == 0) || (node.Id == flow.Str("to") && index == points.Count - 2))).Any());
        bool SharesCorridor(JsonObject flow, List<Pt> points, JsSet<JsonObject>? deferred = null) => Unrelated(flow).Any(other =>
        {
            var occupied = deferred is not null && deferred.Has(other)
                ? EndpointStubs(other).Select(stub => (List<Pt>)[stub.Start, stub.End]).ToList()
                : [PathFor(other).Points];
            return occupied.Any(otherPoints => Geometry.CollectAmbiguousCorridors(
                [new RoutedRelation(points, flow), new RoutedRelation(otherPoints, other)]).Count > 0);
        });
        List<Segment> EndpointStubs(JsonObject flow)
        {
            var points = PathFor(flow).Points;
            var (fromSide, toSide) = FlowSides(flow);
            static Pt Direction(string side) => side switch { "left" => new(-1, 0), "right" => new(1, 0), "top" => new(0, -1), _ => new(0, 1) };
            return new[] { (points[0], fromSide), (points[^1], toSide) }.Select(e =>
                new Segment(e.Item1, new(e.Item1.X + Direction(e.Item2).X * ENDPOINT_STUB, e.Item1.Y + Direction(e.Item2).Y * ENDPOINT_STUB))).ToList();
        }
        List<JsonObject> CrossingFlows(JsonObject flow, IReadOnlyList<Pt> points) => Unrelated(flow).Where(other =>
            SegmentsOf(points).Any(a => SegmentsOf(PathFor(other).Points).Any(b => Geometry.ProperSegmentIntersection(a.Start, a.End, b.Start, b.End) is not null))).ToList();
        double ReadingCost(JsonObject flow, IReadOnlyList<Pt> points) =>
            Length(points) + Math.Max(0, points.Count - 2) * 48 + CrossingFlows(flow, points).Count * 160;

        // ---------------------------------------------------------------- label seed (:1008-1072)
        // The legacy two-point anchor is relative to the source's y coordinate.
        // On an automatic vertical flow that places the label inside the source.
        // Start in the inter-node corridor; explicit offsets retain their old anchor.
        Pt LabelPointFor(JsonObject flow, List<Pt> points)
        {
            if (automaticShowcase && !Truthy(flow.Str("classification")) && points.Count == 2 && points[0].X == points[1].X
                && !LabelPinKeys.Any(flow.ContainsKey) && AutomaticLabelAnchor(flow, points) is { } anchor) return anchor;
            return Geometry.LabelPoint(flow, points);
        }

        static bool ExtraLabelLineMasksRoute(LabelRect rect, List<Pt> points)
        {
            if (rect.Height <= Layout.LabelH) return false;
            var segments = SegmentsOf(points);
            var oneLine = new Rect(rect.X, rect.Y, rect.Width, Layout.LabelH);
            return segments.Any(segment => Geometry.SegmentIntersectsRect(segment, rect))
                && !segments.Any(segment => Geometry.SegmentIntersectsRect(segment, oneLine));
        }

        // Routing depends on node geometry and canvas width, so its actual footprint
        // can determine height without another copy of routeVia's preset rules.
        double HeightForLabels(IReadOnlyList<LabelRect> labels)
        {
            var geometryBottom = Js.MaxOf([.. nodes.Values.Select(node => node.Y + node.Height), .. routable.SelectMany(flow => PathFor(flow).Points.Select(point => point.Y))], 0);
            var contentBottom = Js.MaxOf(labels.Select(rect => rect.Y + rect.Height), geometryBottom);
            return Math.Max(360, Math.Ceiling(contentBottom + 24 + Layout.StageBottomPad));
        }

        void FitAutomaticWidthToLabels(IReadOnlyList<LabelRect> labels)
        {
            if (meta["viewBox"].Truthy()) return;
            var labelRight = Js.MaxOf(labels.Select(rect => rect.X + rect.Width), 0);
            viewBox[0] = Math.Max(viewBox[0], Math.Ceiling(labelRight + 24));
        }

        // ---------------------------------------------------------------- render (:1382-1574)
        // Header measurement follows 276970789's #257, including the ordinal.
        // Long titles wrap at the same legible floor instead of becoming invalid input.
        static string StageHeaderText(JsonObject stage, int index) => $"{Pad2(index + 1)} / {stage.Str("label") ?? "undefined"}";

        string RenderStageHeader(JsonObject stage, int index, double cx)
        {
            var text = StageHeaderText(stage, index);
            var font = TextFit.FittedNodeFontSize(text, Layout.StageW, 9, 7);
            var available = TextFit.AvailableNodeTextWidth(Layout.StageW);
            var open = $"<text x=\"{N(cx)}\" y=\"{N(Layout.StageY + 22)}\" class=\"t-dim\" font-size=\"{N(font)}\" font-weight=\"600\" text-anchor=\"middle\">";
            if (TextFit.MinimumNodeTextWidth(text, font) <= available) return $"{open}{Utils.Esc(text)}</text>";
            var lines = new List<string>();
            var line = "";
            // Prefer word boundaries; split oversized words and CJK by grapheme, without
            // dropping whitespace or splitting a combining character/emoji sequence.
            foreach (var word in Js.SpaceTokens(text))
            {
                if (line.Length > 0 && TextFit.MinimumNodeTextWidth(line + word, font) > available) { lines.Add(line); line = ""; }
                foreach (var segment in Js.Graphemes(word))
                {
                    if (line.Length > 0 && TextFit.MinimumNodeTextWidth(line + segment, font) > available) { lines.Add(line); line = ""; }
                    line += segment;
                }
            }
            if (line.Length > 0) lines.Add(line);
            // Explicit node geometry is authoritative. Do not turn an old horizontal
            // overflow into a new collision with nodes when the header area is packed.
            var firstNodeY = Js.MinOf(nodes.Values.Where(node => node.Source.Num("stage") == index).Select(node => node.Y), viewBox[1] - Layout.StageBottomPad);
            var lastLineBottom = Layout.StageY + 22 + (lines.Count - 1) * (font + 4) + font * 0.3;
            if (lastLineBottom > firstNodeY - 4) return $"{open}{Utils.Esc(text)}</text>";
            var content = lines.Count == 1 ? Utils.Esc(text) : string.Concat(lines.Select((l, i) =>
                $"<tspan x=\"{N(cx)}\" dy=\"{N(i > 0 ? font + 4 : 0)}\">{Utils.Esc(l)}</tspan>"));
            return $"{open}{content}</text>";
        }

        string RenderStage(JsonObject stage, int index)
        {
            var frame = compositionFrames[index];
            var cx = StageX(index);
            return $"        <rect data-graph-role=\"structural-frame\" data-composition-frame-kind=\"stage\" data-composition-frame-id=\"{N(index)}\" x=\"{N(frame.X)}\" y=\"{N(frame.Y)}\" width=\"{N(frame.Width)}\" height=\"{N(frame.Height)}\" rx=\"{N(frame.Radius)}\" class=\"c-lane\" stroke-width=\"1\"/>\n"
                + $"        {RenderStageHeader(stage, index, cx)}";
        }

        string RenderNode(Box node)
        {
            var source = node.Source;
            var type = source.Str("type") ?? "";
            var fill = Geometry.ComponentFill.GetValueOrDefault(type) ?? "c-external";
            var accent = Geometry.ComponentText.GetValueOrDefault(type) ?? "t-muted";
            var sublabel = source.Str("sublabel");
            var tag = source.Str("tag");
            var label = source.Str("label") ?? "";
            var hasSub = Truthy(sublabel);
            var labelFontSize = TextFit.FittedNodeFontSize(label, BrandMarks.BrandLabelFitWidth(node.Brand, node.Width), 10, 8);
            var sublabelFontSize = TextFit.FittedNodeFontSize(sublabel, node.Width, Math.Max(SublabelPreferred, contextFontMinimum), contextFontMinimum);
            var tagFontSize = TextFit.FittedNodeFontSize(tag, node.Width, Math.Max(TagPreferred, contextFontMinimum), contextFontMinimum);
            var inset = autoHeightInset.Get(node.Id);   // `node.autoHeightInset || 0`
            var textRows = new List<LabelRow> { new(label, labelFontSize, 21 + inset) };
            if (hasSub) textRows.Add(new(sublabel!, sublabelFontSize, 37 + inset));
            if (Truthy(tag)) textRows.Add(new(tag!, tagFontSize, node.Height - 11 - inset));
            var labelLayout = TextFit.NodeLabelLayout(node.Width, node.Height, textRows, "left",
                brand: node.Brand is not null, source: SourceBadgeFor(node.Id));
            var sub = hasSub
                ? $"\n          <text data-detail=\"context\" x=\"{N(node.Cx)}\" y=\"{N(node.Y + labelLayout.Ys[1])}\" class=\"t-muted\" font-size=\"{N(sublabelFontSize)}\" text-anchor=\"middle\">{Utils.Esc(sublabel)}</text>"
                : "";
            var tagText = Truthy(tag)
                ? $"\n        <text data-detail=\"fine\" x=\"{N(node.Cx)}\" y=\"{N(node.Y + labelLayout.Ys[hasSub ? 2 : 1])}\" class=\"{accent}\" font-size=\"{N(tagFontSize)}\" text-anchor=\"middle\">{Utils.Esc(tag)}</text>"
                : "";
            var stageIndex = source.Num("stage");
            var stage = stageIndex is { } s && s >= 0 && s < stages.Count && s == Math.Floor(s) ? stages[(int)s] : null;
            var context = stage is not null
                ? $"{Pad2((int)stageIndex!.Value + 1)} / {stage.Str("label") ?? "undefined"}"
                : ctx.Locale.Translate(locale, "node.context.dataflow");
            var brand = node.Brand is { } mark ? BrandMarks.RenderBrandMark(mark, node.X + node.Width - 22, node.Y + 6) : "";
            var passport = new JsonObject { ["kind"] = type.Length > 0 ? type : null };
            if (sublabel is not null) passport["sublabel"] = sublabel;
            if (tag is not null) passport["tag"] = tag;
            passport["context"] = context;
            foreach (var (key, value) in BrandMarks.BrandMetadataFor(node.Brand) ?? new JsonObject()) passport[key] = value?.DeepClone();
            var step = nodeSteps.Has(node.Id) ? nodeSteps.Get(node.Id) : (double?)null;
            return $"        <g {SvgAttrs.FocusNodeAttrs(node.Id, label, passport, locale, ctx)}>\n"
                + $"          {SvgAttrs.FocusNodeTitle(label, passport)}\n"
                + $"          <rect x=\"{N(node.X)}\" y=\"{N(node.Y)}\" width=\"{N(node.Width)}\" height=\"{N(node.Height)}\" rx=\"6\" class=\"c-mask\"/>\n"
                + $"          <rect x=\"{N(node.X)}\" y=\"{N(node.Y)}\" width=\"{N(node.Width)}\" height=\"{N(node.Height)}\" rx=\"6\" class=\"{fill}\"{SvgAttrs.AnimateAttr(meta, "node", step)} stroke-width=\"1.5\"/>\n"
                + $"          {Utils.RenderSemanticSigil(type, node.X + 6, node.Y + labelLayout.SigilY, labelLayout.SigilSize, source.Str("icon"))}{(brand.Length > 0 ? $"\n          {brand}" : "")}\n"
                + $"          <text data-node-label=\"\"{(hasSub ? " data-detail-anchor=\"\"" : "")} x=\"{N(node.X + labelLayout.X)}\" y=\"{N(node.Y + labelLayout.Ys[0])}\" class=\"t-primary\" font-size=\"{N(labelFontSize)}\" font-weight=\"600\" text-anchor=\"middle\">{Utils.Esc(label)}</text>{sub}{tagText}\n"
                + "        </g>";
        }

        static double FlowStrokeWidth(JsonObject flow) => Js.Or(flow.Num("width"), flow.Str("variant") == "emphasis" ? 1.8 : 1.4);

        string RenderFlowPath(JsonObject flow, int index)
        {
            var variant = Truthy(flow.Str("variant")) ? flow.Str("variant")! : "default";
            var (cls, marker) = Geometry.ArrowClassMap.TryGetValue(variant, out var entry) ? entry : Geometry.ArrowClassMap["default"];
            var routed = PathFor(flow);
            var strokeWidth = FlowStrokeWidth(flow);
            var automatic = resolvedCrossoverFlows.Has(flow);
            var edge = $"        <path {SvgAttrs.FocusEdgeAttrs(flow.Str("from") ?? "undefined", flow.Str("to") ?? "undefined", flow.Str("label"), N(index), flow.Str("id"))} data-composition-points=\"{Geometry.RoutePointsValue(routed.Points)}\"{(automatic ? " data-composition-crossover=\"halo\"" : "")}{Geometry.AuthoredStraightRouteAttrs(flow, routed.Points)} d=\"{routed.D}\" class=\"{cls}\"{SvgAttrs.AnimateAttr(meta, "edge", index)} stroke-width=\"{N(strokeWidth)}\" marker-end=\"url(#{marker})\"/>";
            if (!automatic) return edge;
            return $"        <g data-graph-role=\"automatic-crossover\" style=\"--step:{N(index)}\">\n"
                + $"          <path data-graph-role=\"automatic-crossover-underlay\" d=\"{routed.D}\" fill=\"none\" stroke=\"var(--mask)\" stroke-width=\"{N(strokeWidth + 4)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\" pointer-events=\"none\"/>\n"
                + $"{edge}\n"
                + "        </g>";
        }

        string RenderFlowLabel(JsonObject flow, int index)
        {
            var routed = PathFor(flow);
            var (lx, ly) = resolvedLabelPoints.Has(flow) ? resolvedLabelPoints.Get(flow) : LabelPointFor(flow, routed.Points);
            var rect = resolvedLabelRects.FirstOrDefault(r => ReferenceEquals(r.Relation, flow));
            var (labelW, labelH) = rect is not null ? (rect.Width, rect.Height) : LabelCapacity.FlowLabelSize(flow);
            var lines = rect?.Lines;
            var text = lines is not null
                ? string.Concat(lines.Select((line, i) => $"<tspan x=\"{N(lx)}\" dy=\"{(i > 0 ? "11" : "0")}\">{Utils.Esc(line)}</tspan>"))
                : Utils.Esc(flow.Str("label"));
            var classification = Truthy(flow.Str("classification"))
                ? $"\n        <text data-detail=\"fine\" x=\"{N(lx)}\" y=\"{N(ly + 11 * (lines is { Count: > 0 } ? lines.Count : 1))}\" class=\"t-dim\" font-size=\"7\" text-anchor=\"middle\">{Utils.Esc(flow.Str("classification"))}</text>"
                : "";
            return $"        <g data-detail=\"context\" {SvgAttrs.FocusEdgeAttrs(flow.Str("from") ?? "undefined", flow.Str("to") ?? "undefined", flow.Str("label"), N(index), flow.Str("id"))}>\n"
                + $"          <rect x=\"{N(lx - labelW / 2)}\" y=\"{N(ly - 11)}\" width=\"{N(labelW)}\" height=\"{N(labelH)}\" rx=\"4\" class=\"c-mask\"/>\n"
                + $"          <text x=\"{N(lx)}\" y=\"{N(ly)}\" class=\"{Geometry.EdgeLabelAccent(flow.Str("variant"))}\" font-size=\"8\" text-anchor=\"middle\">{text}</text>{classification}\n"
                + "        </g>";
        }

        // LEGEND_CATALOG's className/marker/strokeWidth (:1497-1501), beside the shared LegendEntry.
        static readonly IReadOnlyDictionary<string, (string ClassName, string Marker, double? StrokeWidth)> Swatches =
            new Dictionary<string, (string, string, double?)>(StringComparer.Ordinal)   // lookup only
            {
                ["emphasis"] = ("a-emphasis", "arrowhead-emphasis", 1.8),
                ["security"] = ("a-security", "arrowhead-security", null),
                ["dashed"] = ("a-dashed", "arrowhead-dashed", null),
                ["default"] = ("a-default", "arrowhead", null),
            };

        List<LegendEntry> LegendEntries()
        {
            var presentKinds = new JsSet<string>();
            foreach (var flow in flows) presentKinds.Add(Truthy(flow.Str("variant")) ? flow.Str("variant")! : "default");
            if (nodes.Values.Any(node => node.Source.Str("type") == "database")) presentKinds.Add("database");
            return Legend.ResolveLegend(meta.Obj("legend"), legendCatalog, presentKinds);
        }

        static string RenderSwatch(LegendEntry entry)
        {
            if (entry.Kind == "database")
                return $"<rect x=\"{N(entry.X)}\" y=\"{N(entry.Baseline - 8)}\" width=\"14\" height=\"9\" rx=\"2\" class=\"c-database\" stroke-width=\"1\"/>";
            var (className, marker, strokeWidth) = Swatches[entry.Kind];
            return $"<path d=\"M {N(entry.X)} {N(entry.Baseline - 3)} L {N(entry.X + 34)} {N(entry.Baseline - 3)}\" class=\"{className}\" stroke-width=\"{N(Js.Or(strokeWidth, 1.4))}\" marker-end=\"url(#{marker})\"/>";
        }

        string RenderLegend() => Legend.RenderLegend(ctx, LegendEntries(), new LegendLayout(
            X: 40,
            BaselineY: viewBox[1] - 36,
            Width: viewBox[0] - 80,
            MinTitleY: viewBox[1] - 66,
            Unfit: meta.ContainsKey("legend") ? "error" : "hide",
            DiagramType: "dataflow"), RenderSwatch, locale);

        void EnsureAutoLegendFits()
        {
            // Narrow auto canvases can wrap the legend onto extra rows. Stage frames
            // fill the canvas height, so growing height alone does not free a legend
            // band — widen just enough for a one-row pack instead.
            if (meta["viewBox"].Truthy()) return;
            var entries = LegendEntries();
            if (entries.Count == 0) return;
            if (Legend.LegendFootprint(entries, Math.Max(1, viewBox[0] - 80)).ExtraHeight <= 0) return;
            var width = viewBox[0];
            while (width < 1600 && Legend.LegendFootprint(entries, Math.Max(1, width - 80)).ExtraHeight > 0) width += 40;
            viewBox[0] = width;
        }

        string RenderSvg()
        {
            EnsureAutoLegendFits();
            // Same default-canvas contract as lifecycle: 940x720 is below the 1.55 wide
            // ratio, so without intrinsic-height the desktop Reader can neither narrow
            // nor scroll it and every default dataflow fails the browser gate.
            var readerFit = meta["viewBox"].Truthy() ? "" : " data-reader-fit=\"intrinsic-height\"";
            return $"      <svg viewBox=\"0 0 {N(viewBox[0])} {N(viewBox[1])}\"{readerFit} {SvgAttrs.SvgRootAttrs(ctx)} data-motion-choreography=\"stream\">\n"
                + $"{SvgAttrs.SvgAccessibleText(ctx, "dataflow")}\n"
                + $"{Utils.RenderDefinitions()}\n"
                + "\n"
                + "        <!-- Background Grid -->\n"
                + "        <rect width=\"100%\" height=\"100%\" fill=\"url(#grid)\" />\n"
                + "\n"
                + "        <!-- Data Stages -->\n"
                + $"{string.Join("\n\n", stages.Select(RenderStage))}\n"
                + "\n"
                + "        <!-- Flow paths -->\n"
                + $"{string.Join("\n", flows.Select(RenderFlowPath))}\n"
                + "\n"
                + "        <!-- Nodes -->\n"
                + $"{string.Join("\n\n", nodes.Values.Select(RenderNode))}\n"
                + "\n"
                + "        <!-- Flow labels -->\n"
                + $"{string.Join("\n", flows.Select(RenderFlowLabel))}\n"
                + "\n"
                + "        <!-- Legend -->\n"
                + $"{RenderLegend()}\n"
                + "      </svg>";
        }
    }
}
