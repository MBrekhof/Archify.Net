using System.Text.Json.Nodes;
using static Archify.Net.WorkflowModel;

namespace Archify.Net;

// workflow-compiler.mjs clusters L (canvas measurement, :5154-5311), M (SVG emit, :5313-5517) and N (receipt and driver,
// :5520-5644).
internal sealed partial class WorkflowCompilation
{
    internal sealed record ContentBounds(double Left, double Top, double Right, double Bottom, List<string> Contributors);

    // ---------------------------------------------------------------- L
    ContentBounds MeasuredContentBounds()
    {
        var left = layout.LaneX;
        double top = 27;
        var right = layout.LaneX + layout.LaneW;
        var bottom = lanes.LegendY() + 18;
        // `owners` keeps its insertion order (left, top, right, bottom) for the contributor list.
        var owners = new JsMap<string, string>();
        owners.Set("left", "workflow lanes");
        owners.Set("top", (workflow.Phases?.Count ?? 0) > 0 ? "phase header band" : "workflow top padding");
        owners.Set("right", "workflow lanes");
        owners.Set("bottom", workflowLegendEntries.Count > 0 ? "legend" : "workflow lanes and bottom padding");
        void IncludePoint(Pt point, string contributor)
        {
            if (point.X < left) { left = point.X; owners.Set("left", contributor); }
            if (point.Y < top) { top = point.Y; owners.Set("top", contributor); }
            if (point.X > right) { right = point.X; owners.Set("right", contributor); }
            if (point.Y > bottom) { bottom = point.Y; owners.Set("bottom", contributor); }
        }
        void IncludeRect(Rect rect, string contributor)
        {
            IncludePoint(new(rect.X, rect.Y), contributor);
            IncludePoint(new(rect.X + rect.Width, rect.Y + rect.Height), contributor);
        }

        foreach (var node in nodes.Values) IncludeRect(node, $"node {node.Id}");
        for (var index = 0; index < workflow.Edges.Count; index++)
        {
            var edge = workflow.Edges[index];
            if (!Has(edge.Str("from")) || !Has(edge.Str("to"))) continue;
            var routed = pathCache.Get(edge)!;
            var name = edge["id"].Truthy() ? BrandMarks.JsString(edge["id"]) : N(index);
            foreach (var point in routed.Points) IncludePoint(point, $"edge {name}");
            if (labelRectCache.Get(routed) is { } label) IncludeRect(label, $"edge {name} label mask");
        }
        foreach (var phase in workflow.Phases ?? [])
        {
            if (!ValidSpan(phase, layout.ColXs.Length)) continue;
            var span = PhaseSpan(phase);
            IncludeRect(new(span.X, 27, span.Width, 16), $"phase {Tpl(phase, "id")}");
        }
        foreach (var group in workflow.Groups ?? [])
        {
            if (!lanes.LaneIndex.Has(IdKey(group["lane"])) || !ValidSpan(group, layout.ColXs.Length)) continue;
            var span = GroupSpan(group);
            IncludeRect(new(span.X, lanes.LaneTop(group["lane"]) + layout.LaneTitleH + GroupFrameTopInset, span.Width, GroupFrameHeight(group)), $"group {Tpl(group, "id")}");
            if (V2)
            {
                var frameY = lanes.LaneTop(group["lane"]) + layout.LaneTitleH + GroupFrameTopInset;
                var labelBaseline = frameY + GroupLabelBaselineOffset;
                IncludeRect(new(span.X + 10, labelBaseline - GroupLabelMaskAscent, Utils.TextUnits(group.Str("label")) * 5.6, GroupLabelMaskH), $"group {Tpl(group, "id")} label");
            }
        }
        if (workflowLegendEntries.Count > 0)
        {
            foreach (var (kind, rect) in WorkflowLegendRects()) IncludeRect(rect, $"legend {kind}");
        }
        var contributors = new JsSet<string>();
        foreach (var owner in owners.Values) contributors.Add(owner);
        foreach (var contributor in layout.WidthContributors ?? []) contributors.Add(contributor);
        foreach (var contributor in layout.HeightContributors ?? []) contributors.Add(contributor);
        return new(left, top, right, bottom, [.. contributors]);
    }

    (ContentBounds Bounds, double[] RequiredViewBox) MeasuredPlanFootprint()
    {
        var bounds = MeasuredContentBounds();
        return (bounds, [
            Math.Max(minimumCanvasWidth, Math.Ceiling(bounds.Right + 16)),
            Math.Max(lanes.AutoHeight, Math.Ceiling(bounds.Bottom + 18)),
        ]);
    }

    void FinalizeReadableViewBox()
    {
        if (workflow.SchemaVersion != 2)
        {
            requiredViewBox = [.. viewBox];
            return;
        }
        var (bounds, required) = MeasuredPlanFootprint();
        requiredViewBox = required;
        var outsideOrigin = bounds.Left < 0 || bounds.Top < 0;
        if (outsideOrigin)
        {
            var hasAbsolutePins = HasAbsoluteWorkflowPins(workflow);
            var message = $"Workflow geometry extends above or left of the viewBox origin ({N(Js.Round(bounds.Left))}, {N(Js.Round(bounds.Top))}).";
            throw ctx.Diagnostics.ThrowDiagnosticError(message, [new Diagnostic(
                hasAbsolutePins ? "workflow/explicit-pin-conflict" : "workflow/solver-budget-exhausted", Severity.Error, message,
                new JsonObject { ["diagramType"] = "workflow", ["path"] = "/meta/viewBox" },
                ViewBoxEvidence(bounds), [], null)]);
        }
        if (!workflow.Meta["viewBox"].Truthy())
        {
            viewBox = [.. requiredViewBox];
            return;
        }
        var tooNarrow = viewBox[0] < requiredViewBox[0];
        var tooShort = viewBox[1] < requiredViewBox[1];
        if (!tooNarrow && !tooShort) return;
        var capacity = $"Workflow viewBox {N(viewBox[0])}×{N(viewBox[1])} cannot contain the readable-v2 layout; minimum {N(requiredViewBox[0])}×{N(requiredViewBox[1])}.";
        var supportedFixes = new List<string>();
        if (AcceptsFix(document => document.Meta["viewBox"] = new JsonArray(requiredViewBox[0], requiredViewBox[1])))
            supportedFixes.Add($"set meta.viewBox to at least [{N(requiredViewBox[0])}, {N(requiredViewBox[1])}]");
        if (AcceptsFix(document => document.Meta.Remove("viewBox")))
            supportedFixes.Add("omit meta.viewBox so the compiler can use its measured intrinsic canvas");
        throw ctx.Diagnostics.ThrowDiagnosticError(capacity, [new Diagnostic("workflow/viewbox-capacity", Severity.Error, capacity,
            new JsonObject { ["diagramType"] = "workflow", ["path"] = "/meta/viewBox" }, ViewBoxEvidence(bounds), supportedFixes, null)]);
    }

    JsonObject ViewBoxEvidence(ContentBounds bounds) => new()
    {
        ["actualViewBox"] = new JsonArray([.. viewBox.Select(v => (JsonNode?)v)]),
        ["requiredViewBox"] = new JsonArray([.. requiredViewBox.Select(v => (JsonNode?)v)]),
        ["contentBounds"] = new JsonArray(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
        ["contributors"] = new JsonArray([.. bounds.Contributors.Select(c => (JsonNode?)c)]),
    };

    // ---------------------------------------------------------------- M
    string RenderLane(JsonObject lane, int index)
    {
        var y = lanes.LaneTop(lane["id"]);
        var height = lanes.LaneHeight(index);
        var exception = lane.Str("variant") == "exception"
            ? $"\n        <rect data-graph-role=\"structural-frame\" data-composition-frame-kind=\"exception-lane\" data-composition-frame-id=\"lane-{N(index)}-exception\" x=\"{N(layout.LaneX + 6)}\" y=\"{N(y + 6)}\" width=\"{N(layout.LaneW - 12)}\" height=\"{N(height - 12)}\" rx=\"8\" class=\"c-security-group\" stroke-width=\"1\"/>"
            : "";
        var labelClass = lane.Str("variant") == "exception" ? "t-security" : "t-muted";
        var prefix = lane.Str("variant") == "exception" ? "EX" : N(index + 1).PadLeft(2, '0');
        return $"        <rect data-graph-role=\"structural-frame\" data-composition-frame-kind=\"lane\" data-composition-frame-id=\"lane-{N(index)}\" x=\"{N(layout.LaneX)}\" y=\"{N(y)}\" width=\"{N(layout.LaneW)}\" height=\"{N(height)}\" rx=\"10\" class=\"c-lane\" stroke-width=\"1\"/>{exception}\n"
            + $"        <text x=\"{N(layout.LaneX + 14)}\" y=\"{N(y + 22)}\" class=\"{labelClass}\" font-size=\"10\" font-weight=\"600\">{prefix} / {Utils.Esc(lane.Str("label"))}</text>";
    }

    static (string Path, string Marker) ArrowClass(string? variant) =>
        Geometry.ArrowClassMap.TryGetValue(Truthy(variant) ? variant! : "default", out var entry) ? entry : Geometry.ArrowClassMap["default"];

    string RenderPhase(JsonObject phase)
    {
        var span = PhaseSpan(phase);
        var accent = Geometry.VariantAccent(phase.Str("variant"));
        var (lineClass, _) = ArrowClass(phase.Str("variant"));
        // A hairline spans the phase's columns; only the label is masked, so the span itself stays visible.
        var labelW = Math.Min(span.Width - 12, WorkflowLabelWidth(phase.Str("label")) + 10);
        var x1 = span.X + 8;
        var x2 = span.X + span.Width - 8;
        return $"        <path d=\"M {N(x1)} 35 H {N(x2)}\" class=\"{lineClass} phase-rule\" stroke-width=\"1\" stroke-linecap=\"round\" fill=\"none\"/>\n"
            + $"        <rect x=\"{N(span.Cx - labelW / 2)}\" y=\"27\" width=\"{N(labelW)}\" height=\"16\" rx=\"8\" class=\"c-mask phase-chip\"/>\n"
            + $"        <text x=\"{N(span.Cx)}\" y=\"38.5\" class=\"{accent}\" font-size=\"8\" font-weight=\"600\" text-anchor=\"middle\" letter-spacing=\".04em\">{Utils.Esc(phase.Str("label"))}</text>";
    }

    string RenderGroup(JsonObject group, int index)
    {
        var span = GroupSpan(group);
        var y = lanes.LaneTop(group["lane"]) + layout.LaneTitleH + GroupFrameTopInset;
        var height = GroupFrameHeight(group);
        var cls = group.Str("variant") == "security" ? "c-security-group" : "c-lane";
        var textClass = Geometry.VariantAccent(group.Str("variant"));
        var labelY = V2 ? y + GroupLabelBaselineOffset : y + 14;
        return $"        <rect data-graph-role=\"structural-frame\" data-composition-frame-kind=\"group\" data-composition-frame-id=\"group-{N(index)}\" x=\"{N(span.X)}\" y=\"{N(y)}\" width=\"{N(span.Width)}\" height=\"{N(height)}\" rx=\"9\" class=\"{cls}\" stroke-width=\"1\"/>\n"
            + $"        <text x=\"{N(span.X + 10)}\" y=\"{N(labelY)}\" class=\"{textClass}\" font-size=\"7\" font-weight=\"600\">{Utils.Esc(group.Str("label"))}</text>";
    }

    // Automatic showcase canvases stop sublabel shrinking at the desktop floor.
    // Standard and authored canvases retain the established text-fitting contract;
    // the preferred font remains the maximum supported by the fixed text rows.
    double ReadableSublabelMinimum()
    {
        var minimum = WorkflowScene.TextFitSizes.SublabelMinimum;
        if (workflow.SchemaVersion != 2 || resolvedQualityProfile != "showcase" || workflow.Meta["viewBox"].Truthy()) return minimum;
        return Math.Max(minimum, Math.Ceiling(DesktopReadability.MinimumReadableSourceTextPx(viewBox[0]) * 10) / 10);
    }

    void ValidateReadableNodeText()
    {
        var diagnostics = new List<Diagnostic>();
        var minimum = ReadableSublabelMinimum();
        foreach (var node in nodes.Values)
        {
            var sublabel = node.Source.Str("sublabel");
            if (!node.Source["sublabel"].Truthy()) continue;
            var maximumCanvasWidth = Math.Floor(DesktopReadability.DESKTOP_READER_DIAGRAM_WIDTH * WorkflowScene.TextFitSizes.SublabelPreferred / DesktopReadability.MIN_PROJECTED_NODE_TEXT_PX);
            var fontBudgetExceeded = minimum > WorkflowScene.TextFitSizes.SublabelPreferred;
            var minimumW = TextFit.MinimumNodeTextWidth(sublabel, minimum);
            if (!fontBudgetExceeded && minimumW <= TextFit.AvailableNodeTextWidth(node.Width)) continue;
            var message = fontBudgetExceeded
                ? $"Sublabel \"{sublabel}\" needs a {N(minimum)}px minimum that stays readable on this {N(viewBox[0])}px canvas, above the supported {N(WorkflowScene.TextFitSizes.SublabelPreferred)}px text row — compact the column spacing or node widths so the canvas is at most {N(maximumCanvasWidth)}px wide; preserve the sublabel's role or protocol."
                : $"Sublabel \"{sublabel}\" needs ~{N(Math.Ceiling(minimumW))}px at the {N(minimum)}px minimum that stays readable on this {N(viewBox[0])}px canvas, but node \"{node.Id}\" provides {N(TextFit.AvailableNodeTextWidth(node.Width))}px — shorten the sublabel while preserving its role or protocol.";
            var authoredNode = workflow.Nodes.First(authored => authored.Str("id") == node.Id);
            diagnostics.Add(new("workflow/sublabel-readability", Severity.Error, message,
                new JsonObject { ["diagramType"] = "workflow", ["node"] = node.Id, ["path"] = $"/nodes/{N(sourceNodes.Get(authoredNode))}/sublabel" },
                new JsonObject
                {
                    ["requiredFontPx"] = minimum, ["maximumSlotFontPx"] = WorkflowScene.TextFitSizes.SublabelPreferred, ["canvasWidthPx"] = viewBox[0],
                    ["maximumCanvasWidthPx"] = maximumCanvasWidth, ["requiredTextWidthPx"] = minimumW, ["availableTextWidthPx"] = TextFit.AvailableNodeTextWidth(node.Width),
                },
                fontBudgetExceeded
                    ? [$"compact column spacing or node widths until the implicit canvas is at most {N(maximumCanvasWidth)}px wide, preserving every node and relationship"]
                    : [$"shorten node \"{node.Id}\" sublabel while preserving its role or protocol; move supplementary facts into a card"],
                null));
        }
        if (diagnostics.Count > 0) throw ctx.Diagnostics.ThrowDiagnosticError("Workflow layout validation failed", diagnostics);
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
        var hasSub = source["sublabel"] is not null && !(source["sublabel"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0);
        var labelFontSize = TextFit.FittedNodeFontSize(label, BrandMarks.BrandLabelFitWidth(node.Brand, node.Width), WorkflowScene.TextFitSizes.LabelPreferred, WorkflowScene.TextFitSizes.LabelMinimum);
        var sublabelMinimum = Math.Min(WorkflowScene.TextFitSizes.SublabelPreferred, ReadableSublabelMinimum());
        var sublabelFontSize = TextFit.FittedNodeFontSize(sublabel, node.Width, WorkflowScene.TextFitSizes.SublabelPreferred, sublabelMinimum);
        var tagFontSize = TextFit.FittedNodeFontSize(tag, node.Width, WorkflowScene.TextFitSizes.TagPreferred, WorkflowScene.TextFitSizes.TagMinimum);
        var textRows = new List<LabelRow> { new(label, labelFontSize, 21) };
        if (hasSub) textRows.Add(new(sublabel ?? "", sublabelFontSize, 38));
        if (source["tag"].Truthy()) textRows.Add(new(tag ?? "", tagFontSize, node.Height - 12));
        var labelLayout = TextFit.NodeLabelLayout(node.Width, node.Height, textRows,
            brand: node.Brand is not null, source: sourceEvidence.Obj("nodes").Arr(node.Id) is { Count: > 0 });
        var sub = hasSub
            ? $"\n          <text data-detail=\"context\" x=\"{N(node.Cx)}\" y=\"{N(node.Y + labelLayout.Ys[1])}\" class=\"t-muted\" font-size=\"{N(sublabelFontSize)}\" text-anchor=\"middle\">{Utils.Esc(sublabel)}</text>"
            : "";
        var tagText = source["tag"].Truthy()
            ? $"\n        <text data-detail=\"fine\" x=\"{N(node.Cx)}\" y=\"{N(node.Y + labelLayout.Ys[hasSub ? 2 : 1])}\" class=\"{accent}\" font-size=\"{N(tagFontSize)}\" text-anchor=\"middle\">{Utils.Esc(tag)}</text>"
            : "";
        var brand = node.Brand is { } mark ? BrandMarks.RenderBrandMark(mark, node.X + node.Width - 22, node.Y + 6) : "";
        var passport = new JsonObject();
        if (source["type"] is { } kind) passport["kind"] = kind.DeepClone();
        if (source["sublabel"] is { } sublabelNode) passport["sublabel"] = sublabelNode.DeepClone();
        if (source["tag"] is { } tagNode) passport["tag"] = tagNode.DeepClone();
        passport["context"] = lanes.NodeContext(source);
        foreach (var (key, value) in BrandMarks.BrandMetadataFor(node.Brand) ?? []) passport[key] = value?.DeepClone();
        var locale = workflow.Meta.Str("locale");
        return $"        <g {SvgAttrs.FocusNodeAttrs(node.Id, label, passport, locale, ctx)}>\n"
            + $"          {SvgAttrs.FocusNodeTitle(label, passport)}\n"
            + $"          <rect x=\"{N(node.X)}\" y=\"{N(node.Y)}\" width=\"{N(node.Width)}\" height=\"{N(node.Height)}\" rx=\"8\" class=\"c-mask\"/>\n"
            + $"          <rect x=\"{N(node.X)}\" y=\"{N(node.Y)}\" width=\"{N(node.Width)}\" height=\"{N(node.Height)}\" rx=\"8\" class=\"{fill}\"{SvgAttrs.AnimateAttr(workflow.Meta, "node", steps.NodeStep(source))} stroke-width=\"1.5\"/>\n"
            + $"          {Utils.RenderSemanticSigil(type, node.X + 6, node.Y + labelLayout.SigilY, labelLayout.SigilSize, source.Str("icon"))}{(brand.Length > 0 ? $"\n          {brand}" : "")}\n"
            + $"          <text data-node-label=\"\"{(hasSub ? " data-detail-anchor=\"\"" : "")} x=\"{N(node.X + labelLayout.X)}\" y=\"{N(node.Y + labelLayout.Ys[0])}\" class=\"t-primary\" font-size=\"{N(labelFontSize)}\" font-weight=\"600\" text-anchor=\"middle\">{Utils.Esc(label)}</text>{sub}{tagText}\n"
            + "        </g>";
    }

    // Rounded corners must leave every visible straight segment above the artifact check's 8px floor, including short stubs.
    static string RoundedWorkflowPath(IReadOnlyList<Pt> points)
    {
        double radius = 8;
        for (var index = 1; index < points.Count; index++)
        {
            var length = Js.Hypot(points[index].X - points[index - 1].X, points[index].Y - points[index - 1].Y);
            var corners = (index > 1 ? 1 : 0) + (index < points.Count - 1 ? 1 : 0);
            if (corners > 0) radius = Math.Min(radius, Math.Max(0, (length - 8) / corners));
        }
        return Geometry.RoundedPath(points, radius);
    }

    string RenderEdgePath(JsonObject edge, int index)
    {
        var (cls, marker) = ArrowClass(edge.Str("variant"));
        var routed = PathFor(edge);
        var strokeWidth = Or(edge.Num("width"), edge.Str("variant") == "emphasis" ? 1.8 : 1.4);
        var routing = IndependentAutomaticRoute(edge) ? " data-composition-routing=\"workflow-v2-auto\"" : "";
        var role = V2 ? $" data-edge-role=\"{Utils.Esc(edge["role"].Truthy() ? BrandMarks.JsString(edge["role"]) : "")}\"" : "";
        double? step = steps.EdgeSteps.Has(edge) ? steps.EdgeSteps.Get(edge) : null;
        return $"        <path {SvgAttrs.FocusEdgeAttrs(Tpl(edge, "from"), Tpl(edge, "to"), edge.Str("label"), N(index), edge.Str("id"))}{routing}{role} data-composition-points=\"{Geometry.RoutePointsValue(routed.Points)}\" d=\"{RoundedWorkflowPath(routed.Points)}\" class=\"{cls}\"{SvgAttrs.AnimateAttr(workflow.Meta, "edge", step)} stroke-width=\"{N(strokeWidth)}\" marker-end=\"url(#{marker})\"/>";
    }

    string RenderEdgeLabel(JsonObject edge, int index)
    {
        if (!edge["label"].Truthy()) return "";
        var routed = PathFor(edge);
        var (lx, ly) = WorkflowEdgeLabelPoint(edge, routed.Points);
        var labelW = WorkflowLabelWidth(edge.Str("label"));
        var variant = edge.Str("variant");
        var cls = !edge["variant"].Truthy() || variant == "default" ? "t-muted" : Geometry.EdgeLabelAccent(variant);
        return $"        <g data-detail=\"context\" {SvgAttrs.FocusEdgeAttrs(Tpl(edge, "from"), Tpl(edge, "to"), edge.Str("label"), N(index), edge.Str("id"))}>\n"
            + $"          <rect x=\"{N(lx - labelW / 2)}\" y=\"{N(ly - 10)}\" width=\"{N(labelW)}\" height=\"14\" rx=\"3\" class=\"c-mask\"/>\n"
            + $"          <text x=\"{N(lx)}\" y=\"{N(ly)}\" class=\"{cls}\" font-size=\"8\" text-anchor=\"middle\">{Utils.Esc(edge.Str("label"))}</text>\n"
            + "        </g>";
    }

    string RenderLegend()
    {
        var obstacles = V2
            ? Legend.RelationshipLegendObstacles(workflow.Edges, (edge, _) => PathFor(edge).Points, (edge, index) => LabelRectFor(edge, index) is { } rect ? (Rect?)(Rect)rect : null)
            : [];
        return Legend.RenderLegend(ctx, workflowLegendEntries, WorkflowLegendLayout(obstacles),
            entry => $"<rect x=\"{N(entry.X)}\" y=\"{N(entry.Baseline - 8)}\" width=\"14\" height=\"9\" rx=\"2\" class=\"{Geometry.ComponentFill.GetValueOrDefault(entry.Kind) ?? "c-external"}\" stroke-width=\"1\"/>",
            workflow.Meta.Str("locale"));
    }

    string RenderSvg()
    {
        // A renderer-sized canvas that would overflow the desktop page at full width reads at page width with vertical scroll.
        var readerFit = V2
            && !workflow.Meta["viewBox"].Truthy()
            && ((HasVerticalStack(workflow) && (layout.LaneHeights ?? []).Any(height => height > 104))
                || DesktopReadability.PredictedFixedWidthOverflow(viewBox[0], viewBox[1], diagramType: "workflow") is not null)
            ? " data-reader-fit=\"width-first\""
            : "";
        var contract = V2 ? " data-layout-contract=\"readable-v2\"" : "";
        // svgRootAttrs(workflow.meta, resolvedQualityProfile): the facade's resolved profile; nested compiles' SVG is discarded.
        return $"      <svg viewBox=\"0 0 {N(viewBox[0])} {N(viewBox[1])}\"{readerFit}{contract} {SvgAttrs.SvgRootAttrs(ctx)} {NodeCardStyle.NODE_CARD_ATTR}>\n"
            + $"{SvgAttrs.SvgAccessibleText(ctx, "workflow")}\n"
            + $"{Utils.RenderDefinitions()}\n"
            + $"{NodeCardStyle.Style()}\n"
            + "\n"
            + "        <!-- Background Grid -->\n"
            + "        <rect width=\"100%\" height=\"100%\" fill=\"url(#grid)\" />\n"
            + "\n"
            + "        <!-- Swimlanes -->\n"
            + $"{string.Join("\n\n", workflow.Lanes.Select(RenderLane))}\n"
            + "\n"
            + "        <!-- Phase headers -->\n"
            + $"{string.Join("\n", (workflow.Phases ?? []).Select(RenderPhase))}\n"
            + "\n"
            + "        <!-- Workflow groups -->\n"
            + $"{string.Join("\n", (workflow.Groups ?? []).Select(RenderGroup))}\n"
            + "\n"
            + "        <!-- Edge paths -->\n"
            + $"{string.Join("\n", workflow.Edges.Select(RenderEdgePath))}\n"
            + "\n"
            + "        <!-- Nodes -->\n"
            + $"{string.Join("\n\n", nodes.Values.Select(RenderNode))}\n"
            + "\n"
            + "        <!-- Edge labels -->\n"
            + $"{string.Join("\n", workflow.Edges.Select(RenderEdgeLabel))}\n"
            + "\n"
            + "        <!-- Legend -->\n"
            + $"{RenderLegend()}\n"
            + "      </svg>";
    }

    // ---------------------------------------------------------------- N
    bool HasCompleteMeasuredPlan()
    {
        if (nodes.Count != workflow.Nodes.Count || pathCache.Count != workflow.Edges.Count
            || !layout.ColXs.All(double.IsFinite) || !viewBox.All(double.IsFinite)) return false;
        foreach (var node in nodes.Values)
        {
            if (!Geometry.IsFinitePoint(node.X, node.Y, node.Width, node.Height) || node.Width <= 0 || node.Height <= 0) return false;
        }
        foreach (var edge in workflow.Edges)
        {
            if (!Has(edge.Str("from")) || !Has(edge.Str("to"))) return false;
            var routed = pathCache.Get(edge);
            if (routed is null || routed.Points.Count < 2 || !routed.Points.All(point => Geometry.IsFinitePoint(point.X, point.Y))) return false;
            if (!edge["label"].Truthy()) continue;
            var rect = labelRectCache.Get(routed);
            if (rect is null || !Geometry.IsFinitePoint(rect.X, rect.Y, rect.Width, rect.Height, rect.Lx ?? double.NaN, rect.Ly ?? double.NaN)
                || rect.Width <= 0 || rect.Height <= 0) return false;
        }
        return true;
    }

    // The receipt minus its `diagnostics` key (WorkflowCompileResult.Diagnostics carries that).
    JsonObject MeasuredPlanReceipt(double[] requiredFrame)
    {
        var receiptNodes = new JsonArray();
        foreach (var node in nodes.Values)
        {
            var entry = new JsonObject();
            if (node.Source["id"] is { } id) entry["id"] = id.DeepClone();
            if (node.Source["lane"] is { } lane) entry["lane"] = lane.DeepClone();
            if (node.Source["col"] is { } col) entry["col"] = col.DeepClone();
            entry["x"] = node.X;
            entry["y"] = node.Y;
            entry["width"] = node.Width;
            entry["height"] = node.Height;
            receiptNodes.Add(entry);
        }
        var receiptEdges = new JsonArray();
        foreach (var edge in workflow.Edges)
        {
            var entry = new JsonObject { ["id"] = edge["id"]?.DeepClone() };
            if (edge["from"] is { } from) entry["from"] = from.DeepClone();
            if (edge["to"] is { } to) entry["to"] = to.DeepClone();
            entry["points"] = new JsonArray([.. pathCache.Get(edge)!.Points.Select(point => (JsonNode?)PtJson(point))]);
            receiptEdges.Add(entry);
        }
        var labels = new JsonArray();
        foreach (var edge in workflow.Edges)
        {
            if (!edge["label"].Truthy() || !Has(edge.Str("from")) || !Has(edge.Str("to"))) continue;
            var rect = labelRectCache.Get(pathCache.Get(edge)!)!;
            labels.Add(new JsonObject
            {
                ["edge"] = edge["id"]?.DeepClone(), ["label"] = edge["label"]!.DeepClone(), ["x"] = rect.Lx, ["y"] = rect.Ly,
                ["width"] = WorkflowLabelWidth(edge.Str("label")), ["height"] = 14,
            });
        }
        return new JsonObject
        {
            ["contract"] = layout.Contract,
            ["viewBox"] = new JsonArray([.. viewBox.Select(v => (JsonNode?)v)]),
            ["requiredViewBox"] = new JsonArray([.. requiredFrame.Select(v => (JsonNode?)v)]),
            ["columns"] = new JsonArray([.. layout.ColXs.Select(v => (JsonNode?)v)]),
            ["nodes"] = receiptNodes,
            ["edges"] = receiptEdges,
            ["labels"] = labels,
        };
    }

    Diagnostic? AbsoluteGeometrySizingDiagnostic()
    {
        if (workflow.SchemaVersion != 2 || !WorkflowMigrationGeometry.PreservesWorkflowAbsoluteGeometry(workflow)) return null;
        var affectedNodes = new JsonArray();
        foreach (var authored in workflow.Nodes)
        {
            if (authored.Num("width") is { } authoredWidth && double.IsFinite(authoredWidth)) continue;
            var node = NodeOf(authored.Str("id"));
            var automaticWidth = AutomaticNodeWidth(authored);
            if (node is null || !WorkflowModel.Truthy(Or(automaticWidth, 0)) || automaticWidth <= node.Width) continue;
            var problems = WorkflowScene.WorkflowNodeTextProblems(node.Source, node.Width, node.Brand);
            // Report only intrinsic text failures that automatic sizing would resolve.
            // Final-canvas readability still belongs to its later measured check.
            if (problems.Count == 0 || WorkflowScene.WorkflowNodeTextProblems(node.Source, automaticWidth!.Value, node.Brand).Count > 0) continue;
            affectedNodes.Add(new JsonObject
            {
                ["node"] = node.Id, ["path"] = $"/nodes/{N(sourceNodes.Get(authored))}/width", ["currentWidthPx"] = node.Width,
                ["automaticWidthPx"] = automaticWidth.Value, ["problems"] = new JsonArray([.. problems.Select(p => (JsonNode?)p)]),
            });
        }
        if (affectedNodes.Count == 0) return null;
        var absoluteConstraints = new JsonArray();
        if (workflow.Meta["viewBox"].Truthy())
            absoluteConstraints.Add(new JsonObject { ["path"] = "/meta/viewBox", ["value"] = workflow.Meta["viewBox"]!.DeepClone() });
        foreach (var edge in workflow.Edges)
        {
            foreach (var field in (string[])["via", "labelAt", "channelX", "channelY"])
            {
                if (!edge.ContainsKey(field)) continue;
                absoluteConstraints.Add(new JsonObject { ["path"] = $"/edges/{N(sourceEdges.Get(edge))}/{field}", ["value"] = edge[field]?.DeepClone() });
            }
        }
        var message = $"Preserving absolute workflow geometry disables automatic node widening: {N(affectedNodes.Count)} node(s) with omitted widths also have text-fit problems. Explicit widths can preserve their full text, but changing widths moves endpoints, so route coordinates must be remeasured. The reported automatic widths resolve these intrinsic text checks only; they are not a verified whole-diagram repair.";
        return new("workflow/absolute-geometry-sizing", Severity.Warning, message, new JsonObject { ["diagramType"] = "workflow" },
            new JsonObject { ["absoluteConstraints"] = absoluteConstraints, ["affectedNodes"] = affectedNodes }, [], null);
    }

    static readonly string[] RouteFailureCodes = ["workflow/explicit-pin-conflict", "workflow/route-preset-conflict", "workflow/solver-budget-exhausted"];

    // The driver (:5608-5643). Diagnostic failures become a result; anything else (WorkflowLayoutFeedback included) propagates.
    WorkflowCompileResult Run()
    {
        try
        {
            ValidateReadableInputsBeforeRouting();
            ValidateReadablePinnedGeometry();
            ValidateWorkflow();
            FinalizeReadableViewBox();
            ValidateReadableNodeText();
            // A rendering failure is not a measured validation rejection.
            measuredPlanReady = false;
            var svg = RenderSvg();
            var receipt = MeasuredPlanReceipt(requiredViewBox);
            return new(true, svg, receipt, null, workflowDiagnostics);
        }
        catch (ArchifyDiagnosticException error)
        {
            var diagnostics = error.Diagnostics.ToList();
            // A route failure can hide the sizing side effect of a newly added pin.
            // Supplement that failure without altering acceptance, geometry or fixes.
            var sizing = !measuredPlanReady && diagnostics.Any(diagnostic => RouteFailureCodes.Contains(diagnostic.Code)) ? AbsoluteGeometrySizingDiagnostic() : null;
            if (sizing is not null) diagnostics.Add(sizing);
            var failure = WorkflowCompiler.CompilerFailure(layout.Contract, diagnostics, sizing is not null ? $"{error.Message}\n{sizing.Message}" : error.Message);
            if (measuredPlanReady)
            {
                try
                {
                    var (_, required) = MeasuredPlanFootprint();
                    if (required.All(double.IsFinite))
                    {
                        var receipt = MeasuredPlanReceipt(required);
                        receipt["ok"] = false;
                        failure = failure with { Receipt = receipt };
                    }
                }
                catch (Exception)
                {
                    // Supplemental evidence must never replace the causal diagnostics.
                }
            }
            return failure;
        }
    }
}
