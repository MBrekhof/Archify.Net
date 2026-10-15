using System.Text;
using System.Text.Json.Nodes;

namespace Archify.Net;

// render-architecture.mjs. The module-level flow (line 56 to the final writeDiagram call) becomes one Scene built per render;
// `--layout-json` becomes the LayoutReport on the result and writeDiagram's HTML is returned by the facade.
public sealed class ArchitectureRenderer : IDiagramRenderer
{
    public (string Svg, JsonArray? Cards, LayoutReport? Layout) Render(RenderContext ctx)
    {
        var scene = new Scene(ctx);
        scene.ValidateArchitecture();
        var svg = scene.RenderSvg();
        return (svg, ctx.Diagram.Arr("cards"), new LayoutReport(scene.BuildLayoutReport()));
    }

    // layoutJsonMode (:1177-1189): no renderSvg. A diagnostic rejection inside validateArchitecture still returns the partial
    // report as repair evidence; anything else, and everything thrown before (scene construction included), propagates to the
    // renderer failure boundary.
    internal static LayoutOutcome LayoutMode(RenderContext ctx)
    {
        var scene = new Scene(ctx);
        try
        {
            scene.ValidateArchitecture();
        }
        catch (ArchifyDiagnosticException error) when (error.Diagnostics.Count > 0)
        {
            // { ...buildLayoutReport(), ...rendererFailure(error), contract }: `ok` keeps its slot at index 0.
            var (message, diagnostics, _) = ctx.Diagnostics.RendererFailure(error);
            var report = scene.BuildLayoutReport();
            report["schemaVersion"] = 1;
            report["ok"] = false;
            report["source"] = "renderer";
            report["error"] = message;
            report["diagnostics"] = new JsonArray([.. diagnostics.Select(d => (JsonNode?)d.ToJson())]);
            report["contract"] = "archify-architecture-layout-v1";
            return new(false, report, message, diagnostics);
        }
        return new(true, scene.BuildLayoutReport(), null, ctx.Diagnostics.Recorded);
    }

    static class Layout
    {
        public const double DefaultW = 120;
        public const double DefaultH = 60;
        public const double Margin = 40;
        // Boundary padding — the 30/50 rule that was a hand-arithmetic footgun
        // (CHANGELOG v2.2.1): 30px on top/left/right, plus 20px extra at the bottom.
        public const double BoundaryPad = 30;
        public const double BoundaryExtraBottom = 20;
        public const double BoundaryLabelBaseline = 18;
        public const double BoundaryLabelClearance = 4;
        public const double BoundaryLabelFontPreferred = 9;
        public const double BoundaryLabelFontMinimum = 6;
        public const double BoundaryLabelMaskHeight = 16;
        public const double BoundaryLabelRailGap = 2;
        public const double BoundaryLabelFrameInset = 4;
        public const double LegendH = 28;
    }

    static class ComponentTextFit
    {
        public const double SublabelPreferred = 9;
        public const double SublabelMinimum = 6;
        public const double TagPreferred = 7;
        public const double TagMinimum = 6;
    }

    // `{ ...boundary, x, y, width, height, memberTop }` and the later `{ ...boundary, y, height, title }` spreads.
    sealed class Boundary
    {
        public required JsonObject Source { get; init; }
        public double X, Y, Width, Height, MemberTop;
        public BoundaryTitle? Title;
        public string? Label => Source.Str("label");
        public string? Kind => Source.Str("kind");
        public IEnumerable<string> Wraps => Source["wraps"].AsArrayOrEmpty().Select(id => id.Str0()).OfType<string>();
        public Boundary Clone() => (Boundary)MemberwiseClone();
        public static implicit operator Rect(Boundary b) => new(b.X, b.Y, b.Width, b.Height);
    }

    sealed class BoundaryTitle
    {
        public double X, Y, Width, Height, FontSize, MinimumFontSize, BaselineOffset, AvailableWidth, MinimumWidth;
        public static implicit operator Rect(BoundaryTitle t) => new(t.X, t.Y, t.Width, t.Height);
    }

    sealed class Scene
    {
        readonly RenderContext ctx;
        readonly JsonObject arch;
        readonly JsonObject meta;
        readonly string? locale;
        readonly GridSpec? grid;
        readonly List<LegendEntry> legendCatalog;
        readonly JsMap<string, Box> components = new();
        readonly bool enforcesBoundaryTitleComposition;
        readonly JsMap<string, double> componentSteps = new();
        readonly List<JsonObject> connections;
        readonly List<LegendEntry> architectureLegendEntries;
        // One source for connection label geometry: the rect the containment rule
        // measures is the rect the SVG mask draws, the auto canvas covers, the legend
        // avoids, and the layout report publishes.
        readonly JsMap<JsonObject, Pt> resolvedLabelPoints = new();
        readonly List<Boundary> rawBoundaries;
        readonly Router router;
        List<LabelRect> connectionLabels;
        readonly List<Rect> connectionGeometry;
        readonly string? readabilityProblem;
        readonly List<Boundary> boundaries;
        readonly List<Frame> compositionFrames;
        readonly double[] viewBox;
        List<Segment>? titleRouteSegments;
        // Ruling 1: the option replaces ARCHIFY_QUALITY_PROFILE at every shared gate that read `env || profile`.
        readonly string? gateProfile;

        static string N(double v) => Js.Num(v);
        static string S(string? s) => s ?? "undefined";
        static bool Truthy(string? s) => !string.IsNullOrEmpty(s);
        static bool Has(JsMap<string, Box> map, string? id) => id is not null && map.Has(id);
        Box? ComponentOf(string? id) => id is null ? null : components.Get(id);
        bool BothEndpoints(JsonObject conn) => Has(components, conn.Str("from")) && Has(components, conn.Str("to"));
        List<Pt> PointsFor(JsonObject conn) => router.PathFor(conn).Points;

        public Scene(RenderContext ctx)
        {
            this.ctx = ctx;
            arch = ctx.Diagram;
            meta = ctx.Meta!;
            locale = meta.Str("locale");
            gateProfile = ctx.Options.Quality?.Name() ?? meta.Str("quality_profile");
            grid = Grid.GridLayout(arch);

            legendCatalog = new[] { "frontend", "backend", "database", "cloud", "security", "messagebus", "external" }
                .Select(kind => new LegendEntry(kind, ctx.Locale.Translate(locale, $"legend.architecture.{kind}"))).ToList();

            foreach (var node in arch["components"].AsArrayOrEmpty())
            {
                var c = (JsonObject)node!;
                components.Set(c.Str("id") ?? "undefined", MeasureComponent(c));
            }
            enforcesBoundaryTitleComposition = Truthy(meta.Str("quality_profile"));
            connections = arch["connections"].AsArrayOrEmpty().Select(node => (JsonObject)node!).ToList();
            for (var index = 0; index < connections.Count; index++)
            {
                var conn = connections[index];
                if (conn.Str("from") is { } from && !componentSteps.Has(from)) componentSteps.Set(from, index);
                if (conn.Str("to") is { } to && !componentSteps.Has(to)) componentSteps.Set(to, index + 1);
            }
            var componentIndex = 0;
            foreach (var node in arch["components"].AsArrayOrEmpty())
            {
                var id = node.Str("id");
                if (id is not null && !componentSteps.Has(id)) componentSteps.Set(id, componentIndex);
                componentIndex++;
            }

            architectureLegendEntries = Legend.ResolveLegend(meta.Obj("legend"), legendCatalog,
                components.Values.Select(component => component.Source.Str("type")).OfType<string>());

            // ---- Routing state ----------------------------------------------------------
            // Initialized before the boundary-title work below: connection label rects are
            // part of the derived canvas, so the title convergence must measure the same
            // width the diagram actually renders into (a title sized for a narrower canvas
            // would fall below the desktop-readability floor once labels grow it). Routing
            // reads components, connections and the member-derived boundary frames (so an
            // automatic route never borrows a frame border as its corridor), never the
            // title-expanded frames or the viewBox.
            rawBoundaries = arch["boundaries"].AsArrayOrEmpty().Select(node => BoundaryRect((JsonObject)node!)).OfType<Boundary>().ToList();
            var routerOptions = new RouterOptions(
                DistinctAutomaticPorts: true,
                PreferReadableRoutes: true,
                // A single outward stub below the scene can remove a crossing without
                // widening the canvas or shrinking its text. Fixed canvases keep their bounds.
                ReadabilityBottomMargin: meta["viewBox"].Truthy() ? 0 : 24,
                Frames: rawBoundaries.Select(boundary => new Frame(boundary.X, boundary.Y, boundary.Width, boundary.Height,
                    Radius: boundary.Kind == "security-group" ? 8 : 12, Kind: boundary.Kind, Label: boundary.Label, Id: boundary.Source["id"]?.DeepClone())).ToList(),
                LabelRectFor: (conn, points, routes, labels) => conn["label"].Truthy() ? AutomaticLabels.ReservedLabelRect(
                    ConnectionLabelBoxAt(conn, Geometry.LabelPoint(conn, points)),
                    points,
                    components.Values.ToList(),
                    routes.Select((route, index) => new LabelRoute(index, route)).ToList(),
                    labels) : null);
            router = Routing.CreateRouter(components, connections, routerOptions);
            // The auto canvas has to cover these rects; an authored viewBox is never
            // resized to fit them — there the containment rule reports the clipping.
            connectionLabels = ConnectionLabelRects();
            // Unlabelled outer corridors are geometry too. Fitting only nodes and labels
            // can clip a valid explicit via route while every browser overflow check passes.
            connectionGeometry =
            [
                .. connectionLabels.Select(label => (Rect)label),
                .. connections.Where(BothEndpoints).SelectMany(conn => PointsFor(conn).Select(p => new Rect(p.X, p.Y, 0, 0))),
            ];

            var resolvedBoundaryTitles = ResolveBoundaryTitles();
            boundaries = resolvedBoundaryTitles.Boundaries;
            readabilityProblem = resolvedBoundaryTitles.ReadabilityProblem;
            compositionFrames = boundaries.Select((boundary, index) => new Frame(boundary.X, boundary.Y, boundary.Width, boundary.Height,
                Radius: boundary.Kind == "security-group" ? 8 : 12,
                Kind: Truthy(boundary.Kind) ? boundary.Kind : "boundary",
                Label: boundary.Label,
                Id: boundary.Source["id"].Truthy() ? boundary.Source["id"]!.DeepClone() : JsonValue.Create((double)index))).ToList();

            // ---- Auto viewBox: fit all geometry + the measured resolved legend ----------
            // Connection labels are diagram content, so an auto canvas that stopped at the
            // component/boundary bbox would clip them; the label rects join the fit here
            // and in the title convergence above, which sizes fonts for this same width.
            viewBox = meta["viewBox"].Truthy()
                ? [meta["viewBox"]![0]!.D(), meta["viewBox"]![1]!.D()]
                : AutoViewBoxFor(boundaries.Select(b => (Rect)b), connectionGeometry);

            // Fit titles and canvas from the original geometry first. Fallback labels must
            // fit inside that canvas, so moving a label cannot trigger title reflow or a
            // canvas/label feedback loop. Keep standard and every authored label control.
            if (meta.Str("quality_profile") == "showcase")
            {
                var routes = new List<LabelRoute>();
                for (var relationIndex = 0; relationIndex < connections.Count; relationIndex++)
                    if (BothEndpoints(connections[relationIndex])) routes.Add(new(relationIndex, PointsFor(connections[relationIndex])));
                connectionLabels = AutomaticLabels.PlaceAutomaticLabels(
                    keepFallbackNearRoute: true,
                    labels: connectionLabels,
                    routes: routes,
                    components: components.Values.ToList(),
                    titles: boundaries.Select(boundary => (Rect)boundary.Title!).ToList(),
                    viewBox: viewBox,
                    // Leave the resolved legend band available; moving a label must not hide
                    // an otherwise visible legend. Existing labels keep their placement.
                    placementBottom: architectureLegendEntries.Count > 0
                        ? LegendY() - 32 - Legend.LegendFootprint(architectureLegendEntries, viewBox[0] - Layout.Margin * 2).ExtraHeight
                        : viewBox[1]);
                foreach (var rect in connectionLabels) resolvedLabelPoints.Set(rect.Relation, new(rect.Lx!.Value, rect.Ly!.Value));
            }
        }

        double LegendY() => viewBox[1] - 16;

        // ---- Measure components from free coordinates --------------------------------
        Box MeasureComponent(JsonObject c)
        {
            var (x, y) = Grid.ResolveComponentPos(c, grid);
            var (w, h) = c.Arr("size") is { } size ? (size[0]!.D(), size[1]!.D()) : (Layout.DefaultW, Layout.DefaultH);
            return new Box { Id = c.Str("id") ?? "undefined", X = x, Y = y, Width = w, Height = h, Source = c, Brand = ctx.Brands.BrandMarkFor(c) };
        }

        // ---- Boundaries computed from the `wraps` id list ---------------------------
        Boundary? BoundaryRect(JsonObject boundary)
        {
            var members = boundary["wraps"].AsArrayOrEmpty().Select(id => ComponentOf(id.Str0())).Where(member => member is not null).ToList();
            if (members.Count == 0) return null;
            var minX = double.PositiveInfinity;
            var minY = double.PositiveInfinity;
            var maxX = double.NegativeInfinity;
            var maxY = double.NegativeInfinity;
            foreach (var member in members)
            {
                minX = Math.Min(minX, member!.X);
                minY = Math.Min(minY, member.Y);
                maxX = Math.Max(maxX, member.X + member.Width);
                maxY = Math.Max(maxY, member.Y + member.Height);
            }
            var pad = boundary.Num("pad") ?? Layout.BoundaryPad;
            var topPad = Math.Max(Math.Max(
                pad,
                Layout.BoundaryLabelBaseline + Layout.BoundaryLabelClearance),
                // Routes must avoid the title rail that the final frame will contain.
                enforcesBoundaryTitleComposition
                    ? Layout.BoundaryLabelMaskHeight + Layout.BoundaryLabelClearance + Layout.BoundaryLabelFrameInset
                    : 0);
            return new Boundary
            {
                Source = boundary,
                X = minX - pad,
                Y = minY - topPad,
                Width = maxX - minX + pad * 2,
                Height = maxY - minY + topPad + Layout.BoundaryExtraBottom,
                MemberTop = minY,
            };
        }

        static bool RectContains(Rect outer, Rect inner)
        {
            const double epsilon = 1e-9;
            return outer.X <= inner.X + epsilon
                && outer.Y <= inner.Y + epsilon
                && outer.X + outer.Width + epsilon >= inner.X + inner.Width
                && outer.Y + outer.Height + epsilon >= inner.Y + inner.Height;
        }

        static double BoundaryLabelWidth(string? label, double fontSize) => Math.Max(30, Utils.TextUnits(label) * fontSize * 0.6 + 10);

        LabelRect? ConnectionLabelBox(JsonObject conn)
        {
            if (!conn["label"].Truthy()) return null;
            return ConnectionLabelBoxAt(conn, resolvedLabelPoints.Has(conn) ? resolvedLabelPoints.Get(conn) : Geometry.LabelPoint(conn, PointsFor(conn)));
        }

        static LabelRect ConnectionLabelBoxAt(JsonObject conn, Pt at)
        {
            var width = Math.Max(30, Utils.TextUnits(conn.Str("label")) * 4.8 + 10);
            return new LabelRect { Relation = conn, Label = conn.Str("label"), X = at.X - width / 2, Y = at.Y - 10, Width = width, Height = 14, Lx = at.X, Ly = at.Y };
        }

        List<LabelRect> ConnectionLabelRects()
        {
            var rects = new List<LabelRect>();
            for (var relationIndex = 0; relationIndex < connections.Count; relationIndex++)
            {
                var conn = connections[relationIndex];
                if (!BothEndpoints(conn)) continue;
                var box = ConnectionLabelBox(conn);
                if (box is null) continue;
                box.RelationIndex = relationIndex;
                rects.Add(box);
            }
            return rects;
        }

        double[] AutoViewBoxFor(IEnumerable<Rect> candidateBoundaries, IEnumerable<Rect>? extraRects = null)
        {
            double maxX = 0;
            double maxY = 0;
            foreach (var rects in new[] { components.Values.Select(c => (Rect)c), candidateBoundaries, extraRects ?? [] })
            {
                foreach (var rect in rects)
                {
                    maxX = Math.Max(maxX, rect.X + rect.Width);
                    maxY = Math.Max(maxY, rect.Y + rect.Height);
                }
            }
            var width = Math.Ceiling(maxX + Layout.Margin);
            var footprint = Legend.LegendFootprint(architectureLegendEntries, Math.Max(1, width - Layout.Margin * 2));
            if (footprint.MinWidth > width - Layout.Margin * 2)
            {
                width = Math.Ceiling(footprint.MinWidth + Layout.Margin * 2);
                footprint = Legend.LegendFootprint(architectureLegendEntries, width - Layout.Margin * 2);
            }
            return [width, Math.Ceiling(maxY + Layout.Margin + Layout.LegendH + footprint.ExtraHeight)];
        }

        double ResolvedViewBoxWidth(IEnumerable<Rect> candidateBoundaries)
        {
            if (meta.Arr("viewBox") is { Count: > 0 } authored && authored[0] is JsonValue first && first.GetValueKind() == System.Text.Json.JsonValueKind.Number && double.IsFinite(first.D()))
                return first.D();
            return AutoViewBoxFor(candidateBoundaries, connectionGeometry)[0];
        }

        Boundary ExpandBoundaryForReadableTitle(Boundary boundary, double minimumFontSize)
        {
            if (!enforcesBoundaryTitleComposition) return boundary;
            var requiredWidth = BoundaryLabelWidth(boundary.Label, minimumFontSize) + Layout.BoundaryLabelFrameInset * 2;
            var extra = Math.Max(0, requiredWidth - boundary.Width);
            if (extra == 0 || double.IsNaN(extra)) return boundary;
            var expanded = boundary.Clone();
            expanded.X = boundary.X - extra / 2;
            expanded.Width = boundary.Width + extra;
            return expanded;
        }

        static BoundaryTitle MeasureBoundaryTitle(Boundary boundary, double minimumFontSize)
        {
            var availableWidth = Math.Max(0, boundary.Width - Layout.BoundaryLabelFrameInset * 2);
            var units = Utils.TextUnits(boundary.Label);
            var fitted = units > 0
                ? (availableWidth - 10) / (units * 0.6)
                : Layout.BoundaryLabelFontPreferred;
            var preferredFontSize = Math.Max(Layout.BoundaryLabelFontPreferred, minimumFontSize);
            var fontSize = Math.Max(minimumFontSize, Math.Min(preferredFontSize, fitted));
            var desiredWidth = BoundaryLabelWidth(boundary.Label, fontSize);
            var height = Math.Max(Layout.BoundaryLabelMaskHeight, Math.Ceiling(fontSize + 7));
            return new BoundaryTitle
            {
                X = boundary.X + Layout.BoundaryLabelFrameInset,
                Y = boundary.MemberTop
                    - Layout.BoundaryLabelClearance
                    - height,
                Width = Math.Min(availableWidth, desiredWidth),
                Height = height,
                FontSize = fontSize,
                MinimumFontSize = minimumFontSize,
                BaselineOffset = fontSize + 4,
                AvailableWidth = availableWidth,
                MinimumWidth = BoundaryLabelWidth(boundary.Label, minimumFontSize),
            };
        }

        static bool HorizontalOverlap(Rect left, Rect right) => left.X < right.X + right.Width && left.X + left.Width > right.X;

        // A route through a title disappears under its mask and reads as struck-through
        // text. Keep the left rail position when it is clear; otherwise slide the title
        // along its rail to the nearest clear position inside the frame.
        void SlideTitleOffRoutes(Boundary boundary, BoundaryTitle title, List<BoundaryTitle> placedTitles)
        {
            if (!enforcesBoundaryTitleComposition) return;
            var segments = titleRouteSegments ??= connections
                .Where(BothEndpoints)
                .SelectMany(conn =>
                {
                    var points = PointsFor(conn);
                    return points.Skip(1).Select((end, index) => new Segment(points[index], end));
                }).ToList();
            bool Clear(double x)
            {
                var rect = new Rect(x, title.Y, title.Width, title.Height);
                return !segments.Any(segment => Geometry.SegmentIntersectsRect(segment, rect, 2))
                    && !placedTitles.Select(t => (Rect)t).Concat(components.Values.Select(c => (Rect)c)).Concat(connectionLabels.Select(l => (Rect)l))
                        .Any(other => Geometry.RectsOverlap(rect, other));
            }
            if (Clear(title.X)) return;
            var left = boundary.X + Layout.BoundaryLabelFrameInset;
            var right = boundary.X + boundary.Width - Layout.BoundaryLabelFrameInset - title.Width;
            var band = segments.Where(segment =>
                Math.Max(segment.Start.Y, segment.End.Y) >= title.Y - 2 && Math.Min(segment.Start.Y, segment.End.Y) <= title.Y + title.Height + 2).ToList();
            var labelBlockers = connectionLabels.Where(label => label.Y < title.Y + title.Height && label.Y + label.Height > title.Y).ToList();
            var candidates = new List<double> { right };
            foreach (var segment in band)
            {
                candidates.Add(Math.Max(segment.Start.X, segment.End.X) + 8);
                candidates.Add(Math.Min(segment.Start.X, segment.End.X) - 8 - title.Width);
            }
            foreach (var label in labelBlockers)
            {
                candidates.Add(label.X + label.Width + 8);
                candidates.Add(label.X - 8 - title.Width);
            }
            candidates = candidates.Where(value => value >= left && value <= right).ToList();
            Js.Sort(candidates, (a, b) => a - b);
            foreach (var x in candidates)
            {
                if (!Clear(x)) continue;
                title.X = x;
                return;
            }
        }

        List<Boundary> LayoutBoundaryTitles(List<Boundary> raw, double minimumFontSize)
        {
            var placedTitles = new List<BoundaryTitle>();
            var measured = new JsMap<int, (Boundary Boundary, BoundaryTitle Title)>();
            var ordered = raw.Select((boundary, index) => (Boundary: boundary, Index: index)).ToList();
            Js.Sort(ordered, (left, right) =>
            {
                var areaDelta = left.Boundary.Width * left.Boundary.Height
                    - right.Boundary.Width * right.Boundary.Height;
                return areaDelta != 0 && !double.IsNaN(areaDelta) ? areaDelta : left.Index - right.Index;
            });

            foreach (var entry in ordered)
            {
                var index = entry.Index;
                var boundary = ExpandBoundaryForReadableTitle(entry.Boundary, minimumFontSize);
                var title = MeasureBoundaryTitle(boundary, minimumFontSize);
                var guard = 0;
                while (guard < raw.Count + components.Count + 1)
                {
                    guard += 1;
                    var blockers = placedTitles.Select(t => (Rect)t).Concat(components.Values.Select(c => (Rect)c))
                        .Where(candidate => HorizontalOverlap(title, candidate) && Geometry.RectsOverlap(title, candidate)).ToList();
                    if (blockers.Count == 0) break;
                    var min = double.PositiveInfinity;
                    foreach (var blocker in blockers) min = Math.Min(min, blocker.Y - Layout.BoundaryLabelRailGap - title.Height);
                    title.Y = min;
                }
                SlideTitleOffRoutes(boundary, title, placedTitles);
                placedTitles.Add(title);
                measured.Set(index, (boundary, title));
            }

            return raw.Select((_, index) =>
            {
                var (boundary, title) = measured.Get(index);
                var bottom = boundary.Y + boundary.Height;
                // Profile-less schema-v1 inputs keep their legacy boundary geometry. A
                // quality profile opts into the stricter title-composition contract and
                // may expand the frame to contain an adapted title rail.
                var y = enforcesBoundaryTitleComposition
                    ? Math.Min(boundary.Y, title.Y - Layout.BoundaryLabelFrameInset)
                    : boundary.Y;
                var result = boundary.Clone();
                result.Y = y;
                result.Height = bottom - y;
                result.Title = title;
                return result;
            }).ToList();
        }

        (List<Boundary> Boundaries, string? ReadabilityProblem) ResolveBoundaryTitles()
        {
            if (!enforcesBoundaryTitleComposition || rawBoundaries.Count == 0)
                return (LayoutBoundaryTitles(rawBoundaries, Layout.BoundaryLabelFontMinimum), null);

            const int maximumIterations = 32;
            var candidateBoundaries = rawBoundaries;
            for (var iteration = 0; iteration < maximumIterations; iteration += 1)
            {
                var budgetViewBoxWidth = ResolvedViewBoxWidth(candidateBoundaries.Select(b => (Rect)b));
                var minimumFontSize = Math.Max(
                    Layout.BoundaryLabelFontMinimum,
                    DesktopReadability.MinimumReadableSourceTextPx(budgetViewBoxWidth) + 1e-6);
                var nextBoundaries = LayoutBoundaryTitles(rawBoundaries, minimumFontSize);
                var finalViewBoxWidth = ResolvedViewBoxWidth(nextBoundaries.Select(b => (Rect)b));
                var finalMinimumFontSize = Math.Max(
                    Layout.BoundaryLabelFontMinimum,
                    DesktopReadability.MinimumReadableSourceTextPx(finalViewBoxWidth));
                if (minimumFontSize >= finalMinimumFontSize) return (nextBoundaries, null);
                candidateBoundaries = nextBoundaries;
            }

            var lastViewBoxWidth = ResolvedViewBoxWidth(candidateBoundaries.Select(b => (Rect)b));
            return (candidateBoundaries,
                $"[composition/desktop-readability] Boundary title layout did not converge after {N(maximumIterations)} iterations for the final {N(lastViewBoxWidth)}px viewBox — shorten boundary labels, provide a wider authored viewBox, or move wrapped components closer to the left edge.");
        }

        string ComponentContext(Box component)
        {
            var scopes = boundaries.Where(boundary => boundary.Wraps.Contains(component.Id)).ToList();
            Js.Sort(scopes, (a, b) => (b.Width * b.Height) - (a.Width * a.Height));
            return scopes.Count > 0 ? string.Join(" › ", scopes.Select(boundary => boundary.Label ?? "")) : ctx.Locale.Translate(locale, "node.context.architecture");
        }

        // ---- Validation: mechanical correctness, never layout taste -----------------
        static JsonObject ComponentDiagnosticBox(Box component)
        {
            var box = LayoutReport.ComponentBox(component);
            box["x"] = component.X;
            box["y"] = component.Y;
            if (component.Source.Arr("pos") is { } pos) box["pos"] = pos.DeepClone();
            return box;
        }

        static JsonObject Subject(string nodeId) => new() { ["diagramType"] = "architecture", ["nodeId"] = nodeId };

        static JsonObject ConnectionSubject(JsonObject conn, int index)
        {
            var subject = new JsonObject { ["diagramType"] = "architecture", ["collection"] = "connections", ["index"] = index };
            if (conn["id"].Truthy()) subject["id"] = conn["id"]!.DeepClone();
            subject["from"] = conn["from"]?.DeepClone();
            subject["to"] = conn["to"]?.DeepClone();
            return subject;
        }

        static JsonArray PointsJson(IEnumerable<Pt> points) => new([.. points.Select(p => (JsonNode?)new JsonArray(p.X, p.Y))]);

        static bool HasAutomaticRouteGeometry(JsonObject connection) =>
            connection["via"] is not JsonArray
            && (!connection["route"].Truthy() || connection.Str("route") == "auto")
            && !connection.ContainsKey("channelX")
            && !connection.ContainsKey("channelY");

        public void ValidateArchitecture()
        {
            var problems = new List<string>();
            var diagnostics = new List<Diagnostic>();
            if (readabilityProblem is not null) problems.Add(readabilityProblem);
            var requiresNestedBoundaryMembership = meta.Str("engineering_profile") == "deployment-ownership";
            if (components.Count != arch["components"].AsArrayOrEmpty().Count()) problems.Add("Component ids must be unique.");
            if (grid is not null)
            {
                Grid.ValidateGridPlacement(arch, grid, problems);
            }
            else
            {
                foreach (var node in arch["components"].AsArrayOrEmpty())
                {
                    if (node.Arr("pos") is not { Count: 2 })
                        problems.Add($"Component \"{S(node.Str("id"))}\" must include pos [x, y] when layout.mode is omitted (free placement).");
                }
            }

            foreach (var c in components.Values)
            {
                var label = c.Source.Str("label");
                if (!Geometry.IsFinitePoint(c.X, c.Y, c.Width, c.Height))
                {
                    var message = $"Component \"{c.Id}\" has non-finite pos/size — pos and size must be [number, number].";
                    diagnostics.Add(new("architecture/non-finite-component-geometry", Severity.Error, message,
                        Subject(c.Id),
                        new JsonObject { ["pos"] = c.Source["pos"]?.DeepClone(), ["size"] = new JsonArray(c.Width, c.Height) },
                        [$"replace component \"{c.Id}\" pos and size with finite [number, number] values"], null));
                    problems.Add(message);
                    continue;
                }
                if (c.Width <= 0 || c.Height <= 0)
                {
                    var message = $"Component \"{c.Id}\" has invalid size {N(c.Width)}x{N(c.Height)} — width and height must be greater than 0.";
                    diagnostics.Add(new("architecture/invalid-component-size", Severity.Error, message,
                        Subject(c.Id),
                        new JsonObject { ["width"] = c.Width, ["height"] = c.Height },
                        [$"set component \"{c.Id}\" width and height to values greater than 0"], null));
                    problems.Add(message);
                    continue;
                }
                if (c.X < 0 || c.Y < 0 || c.X + c.Width > viewBox[0] || c.Y + c.Height > viewBox[1])
                {
                    var message = $"Component \"{c.Id}\" falls outside the viewBox {N(viewBox[0])}x{N(viewBox[1])} — adjust pos/size or set a larger meta.viewBox.";
                    var overflow = (
                        Left: Math.Max(0, -c.X),
                        Top: Math.Max(0, -c.Y),
                        Right: Math.Max(0, c.X + c.Width - viewBox[0]),
                        Bottom: Math.Max(0, c.Y + c.Height - viewBox[1]));
                    var widerThanCanvas = overflow.Left > 0 && overflow.Right > 0;
                    var tallerThanCanvas = overflow.Top > 0 && overflow.Bottom > 0;
                    var supportedFixes = new List<string>();
                    // Move by exactly the overflow: it lands the edge flush (x + (-x) is exact
                    // in IEEE 754), while a rounded-up move pushes the opposite edge past a
                    // tight canvas and a rounded-down one leaves the overflow in place. When
                    // the component is larger than the canvas, the move must shrink it too.
                    if (overflow.Left > 0)
                    {
                        var shrink = Math.Max(0, c.Width - viewBox[0]);
                        supportedFixes.Add(shrink > 0
                            ? $"move component \"{c.Id}\" right by {N(overflow.Left)}px and shrink its width by at least {N(shrink)}px"
                            : $"move component \"{c.Id}\" right by {N(overflow.Left)}px");
                    }
                    if (overflow.Top > 0)
                    {
                        var shrink = Math.Max(0, c.Height - viewBox[1]);
                        supportedFixes.Add(shrink > 0
                            ? $"move component \"{c.Id}\" down by {N(overflow.Top)}px and shrink its height by at least {N(shrink)}px"
                            : $"move component \"{c.Id}\" down by {N(overflow.Top)}px");
                    }
                    if ((overflow.Right > 0 && !widerThanCanvas) || (overflow.Bottom > 0 && !tallerThanCanvas))
                    {
                        var coveredWidth = overflow.Right > 0 && !widerThanCanvas ? Math.Ceiling(c.X + c.Width) : viewBox[0];
                        var coveredHeight = overflow.Bottom > 0 && !tallerThanCanvas ? Math.Ceiling(c.Y + c.Height) : viewBox[1];
                        supportedFixes.Add($"set meta.viewBox to at least [{N(Math.Max(viewBox[0], coveredWidth))}, {N(Math.Max(viewBox[1], coveredHeight))}]");
                    }
                    diagnostics.Add(new("layout/component-out-of-bounds", Severity.Error, message,
                        Subject(c.Id),
                        new JsonObject
                        {
                            ["bounds"] = ComponentDiagnosticBox(c),
                            ["viewBox"] = new JsonArray(viewBox[0], viewBox[1]),
                            ["overflow"] = new JsonObject { ["left"] = overflow.Left, ["top"] = overflow.Top, ["right"] = overflow.Right, ["bottom"] = overflow.Bottom },
                        },
                        supportedFixes, null));
                    problems.Add(message);
                }
                var estLabelW = Utils.TextUnits(label) * 6.6;
                if (estLabelW > c.Width + 8)
                {
                    var message = $"Label \"{S(label)}\" (~{N(Js.Round(estLabelW))}px) is wider than component \"{c.Id}\" ({N(c.Width)}px) — shorten the label or widen size.";
                    diagnostics.Add(new("architecture/component-label-overflow", Severity.Error, message,
                        Subject(c.Id),
                        new JsonObject { ["text"] = c.Source["label"]?.DeepClone(), ["estimatedWidthPx"] = Js.Round(estLabelW), ["availableWidthPx"] = c.Width },
                        [
                            $"shorten component \"{c.Id}\" label \"{S(label)}\" while preserving its meaning",
                            $"or widen component \"{c.Id}\" to at least {N(Math.Ceiling(estLabelW - 8))}px",
                        ], null));
                    problems.Add(message);
                }
                var brandRailProblem = BrandMarks.BrandTopRailProblem(c.Brand, c.Id, label, c.Width, 8, "Component");
                if (brandRailProblem is not null) problems.Add(brandRailProblem);
                // sublabel and tag render as single unwrapped <text> elements; shrink-to-fit
                // handles the ordinary case, this rejects what it cannot rescue.
                var availableTextW = TextFit.AvailableNodeTextWidth(c.Width);
                foreach (var (field, value, minimum) in new[]
                {
                    ("Sublabel", c.Source["sublabel"], ComponentTextFit.SublabelMinimum),
                    ("Tag", c.Source["tag"], ComponentTextFit.TagMinimum),
                })
                {
                    if (!value.Truthy()) continue;
                    var text = value.Str0() ?? Js.PrimitiveString(value);
                    var minimumW = TextFit.MinimumNodeTextWidth(text, minimum);
                    if (minimumW > availableTextW)
                    {
                        var fieldKey = field.ToLowerInvariant();
                        var message = $"{field} \"{text}\" needs ~{N(Math.Ceiling(minimumW))}px at the {N(minimum)}px legible minimum, but component \"{c.Id}\" provides {N(availableTextW)}px — shorten the {fieldKey} or widen size.";
                        diagnostics.Add(new($"architecture/component-{fieldKey}-overflow", Severity.Error, message,
                            Subject(c.Id),
                            new JsonObject { ["text"] = value!.DeepClone(), ["minimumFontPx"] = minimum, ["requiredWidthPx"] = Math.Ceiling(minimumW), ["availableWidthPx"] = availableTextW },
                            [
                                $"shorten component \"{c.Id}\" {fieldKey} \"{text}\" while preserving its meaning",
                                $"or widen component \"{c.Id}\" so the {fieldKey} keeps at least {N(Math.Ceiling(minimumW))}px of text width at the {N(minimum)}px legible minimum",
                            ], null));
                        problems.Add(message);
                    }
                }
            }

            // Component overlap — the highest-traffic hand-placement failure mode.
            var list = components.Values.ToList();
            for (var i = 0; i < list.Count; i += 1)
            {
                for (var j = i + 1; j < list.Count; j += 1)
                {
                    if (Geometry.RectsOverlap(list[i], list[j], 8))
                    {
                        var separation = Geometry.ComponentSeparationOptions(list[i], list[j], 8);
                        var message = $"Components \"{list[i].Id}\" and \"{list[j].Id}\" are less than 8px apart — move one or shrink its size.\n{Geometry.SuggestComponentSeparation(list[i], list[j], 8)}";
                        diagnostics.Add(new("layout/component-overlap", Severity.Error, message,
                            Subject(list[i].Id),
                            new JsonObject
                            {
                                ["otherId"] = list[j].Id,
                                ["minimumGapPx"] = 8,
                                ["boxes"] = new JsonArray(ComponentDiagnosticBox(list[i]), ComponentDiagnosticBox(list[j])),
                            },
                            [
                                $"move component \"{list[j].Id}\" pos to [{N(separation[0].Pos.X)}, {N(separation[0].Pos.Y)}] ({separation[0].Relation})",
                                $"or move component \"{list[j].Id}\" pos to [{N(separation[1].Pos.X)}, {N(separation[1].Pos.Y)}] ({separation[1].Relation})",
                                $"or shrink \"{list[i].Id}\" or \"{list[j].Id}\" while keeping every label and sublabel readable",
                            ], null));
                        problems.Add(message);
                    }
                }
            }

            // Boundaries: every wrapped id must exist; the computed box must stay in view.
            foreach (var node in arch["boundaries"].AsArrayOrEmpty())
            {
                foreach (var id in node?["wraps"].AsArrayOrEmpty() ?? [])
                {
                    var wrapped = id.Str0();
                    if (!Has(components, wrapped)) problems.Add($"Boundary \"{S(node.Str("label"))}\" wraps unknown component \"{(wrapped ?? Js.PrimitiveString(id))}\".");
                }
            }
            var viewBoxRect = new Rect(0, 0, viewBox[0], viewBox[1]);
            foreach (var boundary in boundaries)
            {
                if (!enforcesBoundaryTitleComposition) continue;
                var title = boundary.Title!;
                if (title.MinimumWidth > title.AvailableWidth)
                {
                    problems.Add(
                        $"Boundary label \"{S(boundary.Label)}\" needs ~{N(Math.Ceiling(title.MinimumWidth))}px to fit at the "
                        + $"{N(Js.ToNumber(Js.ToFixed(title.MinimumFontSize, 2)))}px desktop-readable source minimum, but its frame provides {N(Math.Floor(title.AvailableWidth))}px — "
                        + "shorten the boundary label, increase pad, or widen the wrapped component layout.");
                }
                if (!RectContains(boundary, title))
                    problems.Add($"Boundary label \"{S(boundary.Label)}\" extends outside its final frame — shorten the label or increase boundary pad.");
                if (!RectContains(viewBoxRect, title))
                    problems.Add($"Boundary label \"{S(boundary.Label)}\" extends outside the viewBox — move wrapped components away from the canvas edge, shorten the label, or increase the viewBox.");
                foreach (var component in components.Values)
                {
                    if (!Geometry.RectsOverlap(title, component)) continue;
                    problems.Add($"Boundary label \"{S(boundary.Label)}\" overlaps component \"{component.Id}\" — move the component, increase boundary title space, or shorten the label.");
                }
            }
            for (var leftIndex = 0; leftIndex < boundaries.Count; leftIndex += 1)
            {
                var left = boundaries[leftIndex];
                var leftMembers = new JsSet<string>();
                foreach (var id in left.Wraps) leftMembers.Add(id);
                for (var rightIndex = leftIndex + 1; rightIndex < boundaries.Count; rightIndex += 1)
                {
                    var right = boundaries[rightIndex];
                    if (enforcesBoundaryTitleComposition && Geometry.RectsOverlap(left.Title!, right.Title!))
                        problems.Add($"Boundary labels \"{S(left.Label)}\" and \"{S(right.Label)}\" overlap — shorten a label or increase boundary title space.");
                    // Ordinary architecture boundaries are sets, not an implied ownership
                    // tree: orthogonal scopes such as runtime and compliance may share some
                    // components while each contains others. The opt-in deployment profile
                    // does promise hierarchical region/private-scope membership, so only it
                    // receives the stricter membership-to-frame containment contract.
                    if (!requiresNestedBoundaryMembership) continue;
                    var rightMembers = new JsSet<string>();
                    foreach (var id in right.Wraps) rightMembers.Add(id);
                    var shared = leftMembers.Where(rightMembers.Has).ToList();
                    var leftNested = leftMembers.All(rightMembers.Has);
                    var rightNested = rightMembers.All(leftMembers.Has);
                    if (shared.Count > 0 && !leftNested && !rightNested)
                    {
                        var leftOnly = leftMembers.Where(id => !rightMembers.Has(id));
                        var rightOnly = rightMembers.Where(id => !leftMembers.Has(id));
                        problems.Add(
                            $"Boundary \"{S(left.Label)}\" crosses boundary \"{S(right.Label)}\" because their memberships partially overlap "
                            + $"(shared: {string.Join(", ", shared.Select(id => $"\"{id}\""))}; "
                            + $"only in \"{S(left.Label)}\": {string.Join(", ", leftOnly.Select(id => $"\"{id}\""))}; "
                            + $"only in \"{S(right.Label)}\": {string.Join(", ", rightOnly.Select(id => $"\"{id}\""))}) — "
                            + "keep one boundary fully nested by removing outside members, or split the boundary.");
                        continue;
                    }

                    if (!Geometry.RectsOverlap(left, right)) continue;
                    var leftContainsRight = RectContains(left, right);
                    var rightContainsLeft = RectContains(right, left);
                    if (!leftContainsRight && !rightContainsLeft)
                    {
                        problems.Add(
                            $"Boundary \"{S(left.Label)}\" and boundary \"{S(right.Label)}\" final frames partially overlap — "
                            + "adjust wraps, pad, or component positions so the frames are disjoint or one fully contains the other.");
                        continue;
                    }

                    if (shared.Count == 0)
                    {
                        problems.Add(
                            $"Boundary \"{S(left.Label)}\" and boundary \"{S(right.Label)}\" final frames overlap even though their memberships are disjoint — "
                            + "adjust pad or component positions so the frames are disjoint, or make wraps express the intended nesting.");
                        continue;
                    }

                    var containmentMatchesMembership = (leftNested && rightContainsLeft)
                        || (rightNested && leftContainsRight);
                    if (!containmentMatchesMembership)
                    {
                        problems.Add(
                            $"Boundary \"{S(left.Label)}\" and boundary \"{S(right.Label)}\" final frame containment contradicts their wraps membership — "
                            + "reduce the inner boundary pad, move its components, or correct wraps so geometry and nesting agree.");
                    }
                }
            }
            foreach (var b in boundaries)
            {
                if (b.X < 0 || b.Y < 0 || b.X + b.Width > viewBox[0] || b.Y + b.Height > viewBox[1])
                {
                    var overflow = new[]
                    {
                        ("left", Math.Max(0, -b.X)),
                        ("top", Math.Max(0, -b.Y)),
                        ("right", Math.Max(0, b.X + b.Width - viewBox[0])),
                        ("bottom", Math.Max(0, b.Y + b.Height - viewBox[1])),
                    };
                    var (overflowLeft, overflowTop, overflowRight, overflowBottom) = (overflow[0].Item2, overflow[1].Item2, overflow[2].Item2, overflow[3].Item2);
                    var sides = string.Join(", ", overflow.Where(entry => entry.Item2 > 0).Select(entry => $"{entry.Item1} by {N(Math.Ceiling(entry.Item2))}px"));
                    var supportedFixes = new List<string>();
                    if (overflowLeft != 0 || overflowTop != 0)
                        supportedFixes.Add($"move the wrapped components right by at least {N(Math.Ceiling(overflowLeft))}px and down by at least {N(Math.Ceiling(overflowTop))}px, then revalidate connected routes and the opposite canvas sides; enlarging meta.viewBox cannot fix left/top overflow");
                    if (overflowRight != 0 || overflowBottom != 0)
                        supportedFixes.Add($"increase meta.viewBox to at least [{N(Math.Ceiling(Math.Max(viewBox[0], b.X + b.Width)))}, {N(Math.Ceiling(Math.Max(viewBox[1], b.Y + b.Height)))}] for right/bottom overflow, or move the wrapped components inward; revalidate desktop readability");
                    var message = $"Boundary \"{S(b.Label)}\" extends outside the viewBox ({sides}) — preserve wraps membership and repair the measured canvas side.";
                    diagnostics.Add(new("layout/boundary-out-of-bounds", Severity.Error, message,
                        new JsonObject { ["diagramType"] = "architecture", ["boundary"] = BoundarySubject(b) },
                        new JsonObject
                        {
                            ["bounds"] = new JsonObject { ["x"] = b.X, ["y"] = b.Y, ["width"] = b.Width, ["height"] = b.Height },
                            ["viewBox"] = new JsonArray(viewBox[0], viewBox[1]),
                            ["overflow"] = new JsonObject { ["left"] = overflowLeft, ["top"] = overflowTop, ["right"] = overflowRight, ["bottom"] = overflowBottom },
                            ["members"] = new JsonArray([.. b.Source["wraps"].AsArrayOrEmpty().Select(id => ComponentOf(id.Str0())).Where(m => m is not null).Select(m => (JsonNode?)LayoutReport.ComponentBox(m!))]),
                        },
                        supportedFixes, null));
                    problems.Add(message);
                }
            }

            // A boundary frame is drawn as the padded box around its members, so a
            // component that is not in `wraps` but sits wholly inside that box reads as
            // a member. Report it instead of letting the picture claim false ownership.
            // A box the frame border visibly cuts through does not claim membership,
            // and earlier valid inputs depend on that, so only full enclosure fails.
            // This new composition gate applies to showcase; standard keeps its existing
            // acceptance contract while authors can opt into the stricter check.
            if (meta.Str("quality_profile") == "showcase")
            {
                foreach (var b in boundaries)
                {
                    var members = new JsSet<string>();
                    foreach (var id in b.Wraps) members.Add(id);
                    foreach (var component in components.Values)
                    {
                        if (members.Has(component.Id) || !RectContains(b, component)) continue;
                        var message = $"Component \"{component.Id}\" is not wrapped by boundary \"{S(b.Label)}\" but sits inside its frame — "
                            + "move it outside the frame, place the wrapped members in one compact axis-aligned cluster (no empty bay for a non-member), or add it to wraps only when it truly belongs there.";
                        problems.Add(message);
                        diagnostics.Add(new("layout/boundary-encloses-non-member", Severity.Error, message,
                            new JsonObject { ["diagramType"] = "architecture", ["boundary"] = BoundarySubject(b), ["component"] = component.Id },
                            new JsonObject
                            {
                                ["bounds"] = new JsonObject { ["x"] = b.X, ["y"] = b.Y, ["width"] = b.Width, ["height"] = b.Height },
                                ["component"] = LayoutReport.ComponentBox(component),
                            },
                            [
                                $"move \"{component.Id}\" outside the boundary frame",
                                "reposition the wrapped members into one compact axis-aligned cluster (no empty bay) that leaves non-members outside",
                            ], null));
                    }
                }
            }

            var connectionList = connections;
            var knownComponentIds = components.Keys.ToList();
            Js.SortDefault(knownComponentIds);
            foreach (var conn in connectionList)
            {
                var connIndex = connectionList.IndexOf(conn);
                foreach (var (field, endpoint) in new[] { ("from", "source"), ("to", "target") })
                {
                    if (Has(components, conn.Str(field))) continue;
                    var fieldValue = conn[field] is { } raw ? Js.PrimitiveString(raw) : "undefined";
                    var named = conn["label"].Truthy() ? conn.Str("label")! : fieldValue;
                    var message = $"Connection \"{named}\" references unknown {endpoint} \"{fieldValue}\".";
                    var otherId = conn.Str(field == "from" ? "to" : "from");
                    var neighbors = new JsSet<string>();
                    foreach (var edge in connectionList)
                    {
                        if (ReferenceEquals(edge, conn)) continue;
                        if (otherId is not null && edge.Str("from") == otherId && edge.Str("to") is { } edgeTo) neighbors.Add(edgeTo);
                        if (otherId is not null && edge.Str("to") == otherId && edge.Str("from") is { } edgeFrom) neighbors.Add(edgeFrom);
                    }
                    var candidates = knownComponentIds.Where(id => id != otherId).ToList();
                    Js.Sort(candidates, (a, b) =>
                    {
                        var byNeighbour = (neighbors.Has(b) ? 1 : 0) - (neighbors.Has(a) ? 1 : 0);
                        return byNeighbour != 0 ? byNeighbour : Js.LocaleCompare(a, b);
                    });
                    var subject = new JsonObject
                    {
                        ["diagramType"] = "architecture",
                        ["connection"] = conn["id"]?.DeepClone(),
                        ["path"] = $"/connections/{N(connIndex)}/{field}",
                    };
                    if (conn["from"] is { } fromNode) subject["from"] = fromNode.DeepClone();
                    if (conn["to"] is { } toNode) subject["to"] = toNode.DeepClone();
                    diagnostics.Add(new("architecture/unknown-endpoint", Severity.Error, message,
                        subject,
                        new JsonObject
                        {
                            ["endpoint"] = endpoint,
                            ["unknownNodeId"] = conn[field]?.DeepClone(),
                            ["availableNodeIds"] = new JsonArray([.. knownComponentIds.Select(id => (JsonNode?)id)]),
                        },
                        [.. candidates.Take(3).Select(id => $"set /connections/{N(connIndex)}/{field} to verified node id \"{id}\"")], null));
                    problems.Add(message);
                    problems.Add($"Connection \"{named}\" endpoint \"{fieldValue}\" does not name a declared component.");
                }
                if (BothEndpoints(conn))
                {
                    var routed = router.PathFor(conn);
                    var outsidePoints = routed.Points.Where(p => p.X < 0 || p.Y < 0 || p.X > viewBox[0] || p.Y > viewBox[1]).ToList();
                    if (meta.Str("quality_profile") == "showcase" && outsidePoints.Count > 0)
                    {
                        var message = $"Connection \"{(conn["id"].Truthy() ? conn.Str("id") : $"{conn.Str("from")}->{conn.Str("to")}")}\" extends outside the viewBox — move the measured outside route points inward or enlarge an authored viewBox for right/bottom overflow.";
                        diagnostics.Add(new("layout/route-out-of-bounds", Severity.Error, message,
                            ConnectionSubject(conn, connections.IndexOf(conn)),
                            new JsonObject { ["viewBox"] = new JsonArray(viewBox[0], viewBox[1]), ["outsidePoints"] = PointsJson(outsidePoints), ["points"] = PointsJson(routed.Points) },
                            ["move negative route coordinates inside the canvas; for right/bottom overflow, enlarge meta.viewBox or reroute inward; preserve endpoints, direction and labels, then revalidate"], null));
                        problems.Add(message);
                    }
                    var (start, end) = (routed.Points[0], routed.Points[^1]);
                    var distance = Js.Hypot(end.X - start.X, end.Y - start.Y);
                    if (distance < 24 && conn.Str("from") == conn.Str("to"))
                    {
                        // "Move the components apart" cannot be executed for a self-loop; the
                        // ports sit on one component and the sides decide how far apart.
                        var name = conn["id"].Truthy() ? conn.Str("id") : conn["label"].Truthy() ? conn.Str("label") : conn.Str("from");
                        var message = $"Self-loop \"{name}\" on component \"{conn.Str("from")}\" has its two ports only {N(Js.Round(distance))}px apart (minimum 24px) — remove fromSide/toSide so the renderer can choose the loop's sides, or set fromSide and toSide to different sides.";
                        diagnostics.Add(new("layout/self-loop-ports", Severity.Error, message,
                            ConnectionSubject(conn, connections.IndexOf(conn)),
                            new JsonObject
                            {
                                ["distancePx"] = Js.Round(distance), ["minimumPx"] = 24,
                                ["fromSide"] = router.ConnectionEndpointSide(conn, "source"), ["toSide"] = router.ConnectionEndpointSide(conn, "target"),
                                ["points"] = PointsJson(routed.Points),
                            },
                            ["remove fromSide/toSide from the self-loop", "set fromSide and toSide to different sides of the component"], null));
                        problems.Add(message);
                    }
                    else if (distance < 24)
                    {
                        var name = conn["label"].Truthy() ? conn.Str("label") : $"{conn.Str("from")}->{conn.Str("to")}";
                        problems.Add($"Connection \"{name}\" is too short ({N(Js.Round(distance))}px; minimum 24px) — place its components farther apart.");
                    }
                }
            }

            var sink = ctx.Diagnostics;
            var relations = arch["connections"];
            var endpointIds = new JsSet<string>();
            foreach (var id in components.Keys) endpointIds.Add(id);
            string FromSideFor(JsonObject conn) => router.ConnectionEndpointSide(conn, "source");
            string ToSideFor(JsonObject conn) => router.ConnectionEndpointSide(conn, "target");
            problems.AddRange(Geometry.CleanEndpointSideProblems(sink, relations, endpointIds, PointsFor, "architecture", "connections",
                FromSideFor, ToSideFor,
                routeHint: "keep automatic routing so the renderer can use a side-aware bridge, or set truthful fromSide/toSide with perpendicular via segments"));
            problems.AddRange(Geometry.CleanFlowProblems(sink, relations, components.Values, PointsFor, "architecture", "connections", "component",
                routeHint: "adjust fromSide/toSide, set route/via, or move the component"));
            problems.AddRange(Geometry.CleanCrossingProblems(sink, relations, endpointIds, PointsFor, "architecture", "connections",
                profile: gateProfile,
                // Automatic architecture routes render with an opaque crossover halo.
                // That makes a proper X visually unambiguous while explicit authored
                // crossings remain a blocking composition error.
                crossingResolved: (left, right, _) => HasAutomaticRouteGeometry(left) && HasAutomaticRouteGeometry(right),
                routeHint: "adjust route/via or fromSide/toSide so the connections use separate corridors"));
            problems.AddRange(Geometry.CleanAmbiguousCorridorProblems(sink, relations, endpointIds, PointsFor, "architecture", "connections",
                profile: gateProfile,
                includeSharedEndpoints: (left, right) => HasAutomaticRouteGeometry(left) && !left["labelAt"].Truthy()
                    && HasAutomaticRouteGeometry(right) && !right["labelAt"].Truthy(),
                routeHint: "adjust route/via or fromSide/toSide so distinct connections do not visually merge"));
            if (gateProfile == "showcase")
            {
                var collisions = Geometry.CollectArrowheadCollisions(connections
                    .Where(conn => BothEndpoints(conn) && HasAutomaticRouteGeometry(conn) && !conn["labelAt"].Truthy())
                    .Select(relation => new RoutedRelation(PointsFor(relation), relation)));
                foreach (var hit in collisions)
                {
                    var left = hit.Left.Relation;
                    var right = hit.Right.Relation;
                    var message = $"[composition/arrowhead-collision] automatic connections \"{(left["id"].Truthy() ? left.Str("id") : left.Str("from"))}\" and \"{(right["id"].Truthy() ? right.Str("id") : right.Str("from"))}\" into \"{left.Str("to")}\" have arrowheads {N(hit.Distance)}px apart (minimum {N(hit.Minimum)}px) — enlarge or reposition the destination, or choose separate toSide ports.";
                    problems.Add(message);
                    var subject = new JsonObject { ["diagramType"] = "architecture", ["collection"] = "connections" };
                    if (left["id"] is { } leftId) subject["id"] = leftId.DeepClone();
                    subject["from"] = left["from"]?.DeepClone();
                    subject["to"] = left["to"]?.DeepClone();
                    var evidence = new JsonObject();
                    if (right["id"] is { } rightId) evidence["otherId"] = rightId.DeepClone();
                    evidence["distancePx"] = hit.Distance;
                    evidence["minimumPx"] = hit.Minimum;
                    evidence["endpoints"] = new JsonArray(new JsonArray(hit.Left.Tip.X, hit.Left.Tip.Y), new JsonArray(hit.Right.Tip.X, hit.Right.Tip.Y));
                    diagnostics.Add(new("composition/arrowhead-collision", Severity.Error, message, subject, evidence,
                        ["enlarge or reposition the destination", "choose separate toSide ports"], null));
                }
            }
            problems.AddRange(Geometry.CleanBorderRunProblems(sink, relations, endpointIds, compositionFrames, PointsFor, "architecture", "connections",
                gateProfile,
                routeHint: "adjust route/via or fromSide/toSide so the connection crosses the boundary perpendicularly instead of following its border"));
            problems.AddRange(Geometry.CleanRouteRhythmProblems(sink, relations, endpointIds, PointsFor, "architecture", "connections",
                gateProfile,
                routeHint: "move route/via points into a wider corridor or move the component so every turn has room to read"));
            // route-quality.mjs:591 reads `profile` directly (no ARCHIFY_QUALITY_PROFILE fallback): meta only.
            problems.AddRange(RouteQuality.CleanRouteDetourProblems(sink, relations, components.Values.Select(c => (Rect)c),
                [.. components.Values.Select(c => (Rect)c), .. boundaries.Select(b => (Rect)b)],
                endpointIds, PointsFor, FromSideFor, ToSideFor, "architecture", "connections",
                meta.Str("quality_profile")));
            // Connection labels must not land on top of components.
            var labelRects = connectionLabels;
            foreach (var rect in labelRects)
            {
                var blockedComponents = components.Values.Where(c => Geometry.RectsOverlap(rect, c, -2)).ToList();
                var labelPinned = new[] { "labelAt", "labelDx", "labelDy", "labelSegment" }.Any(key => rect.Relation.ContainsKey(key));
                var points = PointsFor(rect.Relation);
                double? shortHorizontalGap = points.Count == 2 && Math.Abs(points[0].Y - points[1].Y) < 0.0001
                    ? Math.Abs(points[1].X - points[0].X) : null;
                var requiredGap = Math.Ceiling(rect.Width + 16);
                if (meta.Str("quality_profile") == "showcase" && !labelPinned && blockedComponents.Count > 0
                    && shortHorizontalGap is { } gap && gap < requiredGap)
                {
                    var message = $"Label \"{S(rect.Label)}\" has only {N(Js.Round(gap))}px between \"{S(rect.Relation.Str("from"))}\" and \"{S(rect.Relation.Str("to"))}\"; it needs at least {N(requiredGap)}px to stay beside its route — increase that clear gap or place the connected nodes on another readable row, preserving the label.";
                    problems.Add(message);
                    var subject = new JsonObject { ["diagramType"] = "architecture", ["collection"] = "connections" };
                    if (rect.Relation["id"] is { } relationId) subject["id"] = relationId.DeepClone();
                    subject["from"] = rect.Relation["from"]?.DeepClone();
                    subject["to"] = rect.Relation["to"]?.DeepClone();
                    diagnostics.Add(new("composition/label-gap", Severity.Error, message, subject,
                        new JsonObject
                        {
                            ["clearGapPx"] = gap, ["minimumGapPx"] = requiredGap, ["labelWidthPx"] = rect.Width,
                            ["obstacles"] = new JsonArray([.. blockedComponents.Select(c => (JsonNode?)c.Id)]),
                        },
                        [$"increase the clear gap between the connected nodes to at least {N(requiredGap)}px", "reposition the connected nodes together while preserving the full relationship label"], null));
                }
                else
                {
                    foreach (var c in blockedComponents)
                        problems.Add($"Label \"{S(rect.Label)}\" overlaps component \"{c.Id}\" — adjust labelDx/labelDy/labelSegment or set labelAt.\n{Geometry.SuggestLabelObstacleFix(rect, rect.Lx!.Value, rect.Ly!.Value, c, "component", viewBox, components.Values)}");
                }
                if (enforcesBoundaryTitleComposition)
                {
                    foreach (var boundary in boundaries)
                    {
                        if (!Geometry.RectsOverlap(boundary.Title!, rect)) continue;
                        problems.Add(
                            $"Boundary label \"{S(boundary.Label)}\" overlaps connection label \"{S(rect.Label)}\" — move the boundary title rail by adjusting wrapped component positions, or move the connection label with labelAt/labelDx/labelDy/labelSegment.");
                    }
                }
            }
            problems.AddRange(Geometry.CleanLabelRouteClearanceProblems(sink, relations, labelRects, endpointIds, PointsFor, "architecture", "connections",
                gateProfile));
            // See collectLabelCanvasOverflow in shared/geometry.mjs. An auto canvas now
            // covers these rects, so this reports authored viewBoxes and the origin side,
            // which growth cannot reach.
            problems.AddRange(Geometry.CleanLabelCanvasContainmentProblems(sink, labelRects, viewBox, "architecture", "connections",
                gateProfile));

            if (problems.Count > 0)
            {
                throw sink.ThrowDiagnosticProblems("Architecture layout validation failed", problems,
                    subject: new JsonObject { ["diagramType"] = "architecture" },
                    details: diagnostics);
            }
        }

        static JsonObject BoundarySubject(Boundary b)
        {
            var subject = new JsonObject();
            if (b.Source["kind"] is { } kind) subject["kind"] = kind.DeepClone();
            if (b.Source["label"] is { } label) subject["label"] = label.DeepClone();
            if (b.Source["wraps"] is { } wraps) subject["wraps"] = wraps.DeepClone();
            return subject;
        }

        public JsonObject BuildLayoutReport()
        {
            var labels = new JsonArray([.. connectionLabels.Select(rect => (JsonNode?)new JsonObject
            {
                ["text"] = rect.Label,
                ["x"] = Js.Round(rect.X),
                ["y"] = Js.Round(rect.Y),
                ["width"] = Js.Round(rect.Width),
                ["height"] = 14,
                ["labelAt"] = new JsonArray(Js.Round(rect.Lx!.Value), Js.Round(rect.Ly!.Value)),
            })]);
            JsonObject layout;
            if (grid is not null)
            {
                layout = new JsonObject { ["mode"] = "grid" };
                foreach (var (key, value) in grid.Raw) layout[key] = value?.DeepClone();
            }
            else layout = new JsonObject { ["mode"] = "free" };
            return new JsonObject
            {
                ["ok"] = true,
                ["diagram_type"] = "architecture",
                ["layout"] = layout,
                ["viewBox"] = new JsonArray(viewBox[0], viewBox[1]),
                ["components"] = new JsonArray([.. components.Values.Select(c => (JsonNode?)LayoutReport.ComponentBox(c))]),
                ["boundaries"] = new JsonArray([.. boundaries.Select(b => (JsonNode?)LayoutReport.BoundaryBox(b.Source, b))]),
                ["connections"] = new JsonArray([.. connections.Where(BothEndpoints).Select(conn =>
                {
                    var routed = router.PathFor(conn);
                    Pt? labelAt = conn["label"].Truthy() ? (resolvedLabelPoints.Has(conn) ? resolvedLabelPoints.Get(conn) : Geometry.LabelPoint(conn, routed.Points)) : null;
                    return (JsonNode?)LayoutReport.ConnectionPath(conn, routed.Points, labelAt);
                })]),
                ["labels"] = labels,
            };
        }

        // ---- Rendering ---------------------------------------------------------------
        static string RenderBoundaryFrame(Boundary b, int index)
        {
            var cls = b.Kind == "security-group" ? "c-security-group" : "c-region";
            var rx = b.Kind == "security-group" ? 8 : 12;
            return $"        <rect data-graph-role=\"structural-frame\" data-composition-frame-kind=\"{Utils.Esc(Truthy(b.Kind) ? b.Kind : "boundary")}\" data-composition-frame-id=\"{N(index)}\" data-composition-frame-label=\"{Utils.Esc(b.Label)}\" x=\"{N(b.X)}\" y=\"{N(b.Y)}\" width=\"{N(b.Width)}\" height=\"{N(b.Height)}\" rx=\"{N(rx)}\" class=\"{cls}\" stroke-width=\"1\"/>";
        }

        static string RenderBoundaryLabel(Boundary b, int index)
        {
            var labelCls = b.Kind == "security-group" ? "t-security" : "t-cloud";
            var title = b.Title!;
            return $"        <g data-graph-role=\"structural-frame-label\" data-composition-frame-id=\"{N(index)}\" data-composition-frame-kind=\"{Utils.Esc(Truthy(b.Kind) ? b.Kind : "boundary")}\" data-composition-frame-label=\"{Utils.Esc(b.Label)}\">\n"
                + $"          <rect data-graph-role=\"structural-frame-label-mask\" x=\"{N(title.X)}\" y=\"{N(title.Y)}\" width=\"{N(title.Width)}\" height=\"{N(title.Height)}\" rx=\"3\" class=\"c-mask\"/>\n"
                + $"          <text data-boundary-label=\"\" x=\"{N(title.X + 4)}\" y=\"{N(title.Y + title.BaselineOffset)}\" class=\"{labelCls}\" font-size=\"{N(title.FontSize)}\" font-weight=\"600\">{Utils.Esc(b.Label)}</text>\n"
                + "        </g>";
        }

        static double StrokeWidth(JsonObject conn) =>
            conn.Num("width") is { } width && width != 0 && !double.IsNaN(width) ? width : conn.Str("variant") == "emphasis" ? 1.8 : 1.5;

        string RenderConnectionPath(JsonObject conn, int index)
        {
            var (cls, marker) = Geometry.ArrowClassMap.TryGetValue(Truthy(conn.Str("variant")) ? conn.Str("variant")! : "default", out var arrow) ? arrow : Geometry.ArrowClassMap["default"];
            var routed = router.PathFor(conn);
            var strokeWidth = StrokeWidth(conn);
            var automaticRoute = HasAutomaticRouteGeometry(conn);
            var underlay = automaticRoute
                ? $"          <path data-graph-role=\"automatic-crossover-underlay\" d=\"{routed.D}\" fill=\"none\" stroke=\"var(--mask)\" stroke-width=\"{N(strokeWidth + 4)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\" pointer-events=\"none\"/>\n"
                : "";
            var crossover = automaticRoute
                ? $" data-composition-crossover=\"halo\"{(conn["labelAt"].Truthy() ? "" : " data-composition-independent=\"true\"")}" : "";
            var edge = $"        <path {SvgAttrs.FocusEdgeAttrs(S(conn.Str("from")), S(conn.Str("to")), conn.Str("label"), N(index), conn.Str("id"))} data-composition-points=\"{Geometry.RoutePointsValue(routed.Points)}\"{crossover}{Geometry.AuthoredStraightRouteAttrs(conn, routed.Points)} d=\"{routed.D}\" class=\"{cls}\"{SvgAttrs.AnimateAttr(meta, "edge", index)} stroke-width=\"{N(strokeWidth)}\" marker-end=\"url(#{marker})\"/>";
            if (!automaticRoute) return edge;
            // The wrapper is presentation-only: viewer state remains on the one semantic
            // edge, while CSS can keep its preceding mask underlay at the same opacity.
            return $"        <g data-graph-role=\"automatic-crossover\" style=\"--step:{N(index)}\">\n{underlay}{"          " + edge[8..]}\n        </g>";
        }

        string RenderConnectionLabel(JsonObject conn, int index)
        {
            var box = ConnectionLabelBox(conn);
            if (box is null) return "";
            return $"        <g data-detail=\"context\" {SvgAttrs.FocusEdgeAttrs(S(conn.Str("from")), S(conn.Str("to")), conn.Str("label"), N(index), conn.Str("id"))}>\n"
                + $"          <rect x=\"{N(box.X)}\" y=\"{N(box.Y)}\" width=\"{N(box.Width)}\" height=\"{N(box.Height)}\" rx=\"3\" class=\"c-mask\"/>\n"
                + $"          <text x=\"{N(box.Lx!.Value)}\" y=\"{N(box.Ly!.Value)}\" class=\"{Geometry.EdgeLabelAccent(conn.Str("variant"))}\" font-size=\"8\" text-anchor=\"middle\">{Utils.Esc(conn.Str("label"))}</text>\n"
                + "        </g>";
        }

        string RenderComponent(Box c)
        {
            var type = c.Source.Str("type") ?? "";
            var fill = Geometry.ComponentFill.TryGetValue(type, out var f) ? f : "c-external";
            var accent = Geometry.ComponentText.TryGetValue(type, out var a) ? a : "t-muted";
            var cx = c.Cx;
            var sublabel = c.Source.Str("sublabel");
            var tag = c.Source.Str("tag");
            var label = c.Source.Str("label") ?? "";
            var hasSub = sublabel is not null && sublabel != "";
            var labelY = hasSub ? c.Y + c.Height / 2 - 2 : c.Y + c.Height / 2 + 4;
            var sub = hasSub
                ? $"\n        <text data-detail=\"context\" x=\"{N(cx)}\" y=\"{N(c.Y + c.Height / 2 + 14)}\" class=\"t-muted\" font-size=\"{N(TextFit.FittedNodeFontSize(sublabel, c.Width, ComponentTextFit.SublabelPreferred, ComponentTextFit.SublabelMinimum))}\" text-anchor=\"middle\">{Utils.Esc(sublabel)}</text>"
                : "";
            var tagText = Truthy(tag)
                ? $"\n        <text data-detail=\"fine\" x=\"{N(cx)}\" y=\"{N(c.Y + c.Height - 8)}\" class=\"{accent}\" font-size=\"{N(TextFit.FittedNodeFontSize(tag, c.Width, ComponentTextFit.TagPreferred, ComponentTextFit.TagMinimum))}\" text-anchor=\"middle\">{Utils.Esc(tag)}</text>"
                : "";
            var brand = c.Brand is { } mark ? BrandMarks.RenderBrandMark(mark, c.X + c.Width - 22, c.Y + 6) : "";
            var labelFontSize = TextFit.FittedNodeFontSize(label, BrandMarks.BrandLabelFitWidth(c.Brand, c.Width), 11, 8);
            var passport = new JsonObject();
            if (c.Source["type"] is { } kind) passport["kind"] = kind.DeepClone();
            if (c.Source["sublabel"] is { } sublabelNode) passport["sublabel"] = sublabelNode.DeepClone();
            if (c.Source["tag"] is { } tagNode) passport["tag"] = tagNode.DeepClone();
            passport["context"] = ComponentContext(c);
            // Ruling 4: BrandMetadataFor returns null for upstream's `{}`; the brand keys spread last either way.
            if (BrandMarks.BrandMetadataFor(c.Brand) is { } brandMetadata)
                foreach (var (key, value) in brandMetadata) passport[key] = value?.DeepClone();
            return $"        <g {SvgAttrs.FocusNodeAttrs(c.Id, label, passport, locale, ctx)}>\n"
                + $"          {SvgAttrs.FocusNodeTitle(label, passport)}\n"
                + $"          <rect x=\"{N(c.X)}\" y=\"{N(c.Y)}\" width=\"{N(c.Width)}\" height=\"{N(c.Height)}\" rx=\"8\" class=\"c-mask\"/>\n"
                + $"          <rect x=\"{N(c.X)}\" y=\"{N(c.Y)}\" width=\"{N(c.Width)}\" height=\"{N(c.Height)}\" rx=\"8\" class=\"{fill}\"{SvgAttrs.AnimateAttr(meta, "node", componentSteps.Has(c.Id) ? componentSteps.Get(c.Id) : null)} stroke-width=\"1.5\"/>\n"
                + $"          {Utils.RenderSemanticSigil(type, c.X + 6, c.Y + 6, icon: c.Source.Str("icon"))}{(brand.Length > 0 ? $"\n          {brand}" : "")}\n"
                + $"          <text data-node-label=\"\"{(hasSub ? " data-detail-anchor=\"\"" : "")} x=\"{N(cx)}\" y=\"{N(labelY)}\" class=\"t-primary\" font-size=\"{N(labelFontSize)}\" font-weight=\"600\" text-anchor=\"middle\">{Utils.Esc(label)}</text>{sub}{tagText}\n"
                + "        </g>";
        }

        string RenderLegend()
        {
            var entries = architectureLegendEntries;
            var relationshipObstacles = Legend.RelationshipLegendObstacles(arch["connections"],
                pointsFor: (connection, _) => PointsFor(connection),
                labelRectFor: (connection, _) => ConnectionLabelBox(connection) is { } box ? (Rect?)box : null);
            double contentBottom = 0;
            foreach (var component in components.Values) contentBottom = Math.Max(contentBottom, component.Y + component.Height);
            foreach (var boundary in boundaries) contentBottom = Math.Max(contentBottom, boundary.Y + boundary.Height);
            return Legend.RenderLegend(ctx, entries,
                new LegendLayout(
                    X: Layout.Margin,
                    BaselineY: LegendY(),
                    Width: viewBox[0] - Layout.Margin * 2,
                    MinTitleY: contentBottom + 8,
                    Obstacles: relationshipObstacles,
                    Unfit: meta.ContainsKey("legend") ? "error" : "hide",
                    DiagramType: "architecture"),
                renderSwatch: entry => $"<rect x=\"{N(entry.X)}\" y=\"{N(entry.Baseline - 9)}\" width=\"16\" height=\"10\" rx=\"2.5\" class=\"{(Geometry.ComponentFill.TryGetValue(entry.Kind, out var swatch) ? swatch : "c-external")}\" stroke-width=\"1\"/>",
                locale: locale);
        }

        public string RenderSvg()
        {
            // An automatic architecture canvas is compiler-measured geometry. Let the
            // Reader spend the real desktop height budget on it, including when an
            // outer route makes the canvas taller than the ordinary wide-diagram
            // threshold. Authored viewBoxes keep their geometry and existing Reader width policy;
            // their declared height may use readable document scrolling without reflow.
            var authoredViewBox = meta["viewBox"].Truthy();
            var readerFit = authoredViewBox
                ? " data-diagram-type=\"architecture\" data-reader-fit=\"authored-height\""
                : " data-reader-fit=\"intrinsic-height\"";
            // A complete repository architecture is allowed to use normal page scroll;
            // keep its common-desktop text at a comfortable reading size instead of
            // shrinking a semantically rich graph to the universal emergency floor.
            var readerMinimumText = authoredViewBox ? "" : " data-reader-min-text=\"7.5\"";
            var readerPrimaryText = authoredViewBox ? "" : " data-reader-primary-text=\"14\"";
            var sb = new StringBuilder();
            sb.Append($"      <svg viewBox=\"0 0 {N(viewBox[0])} {N(viewBox[1])}\" {SvgAttrs.SvgRootAttrs(ctx)} {NodeCardStyle.NODE_CARD_ATTR}{readerFit}{readerMinimumText}{readerPrimaryText}>\n");
            sb.Append(SvgAttrs.SvgAccessibleText(ctx, "architecture")).Append('\n');
            sb.Append(Utils.RenderDefinitions()).Append('\n');
            sb.Append(NodeCardStyle.Style()).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Background Grid -->\n");
            sb.Append("        <rect width=\"100%\" height=\"100%\" fill=\"url(#grid)\" />\n");
            sb.Append('\n');
            sb.Append("        <!-- Boundaries (behind everything) -->\n");
            sb.Append(string.Join("\n\n", boundaries.Select(RenderBoundaryFrame))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Connection paths (before components for correct z-order) -->\n");
            sb.Append(string.Join("\n", connections.Select(RenderConnectionPath))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Components -->\n");
            sb.Append(string.Join("\n\n", components.Values.Select(RenderComponent))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Connection labels -->\n");
            sb.Append(string.Join("\n", connections.Select(RenderConnectionLabel))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Boundary labels (foreground masks keep routes out of titles) -->\n");
            sb.Append(string.Join("\n\n", boundaries.Select(RenderBoundaryLabel))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Legend -->\n");
            sb.Append(RenderLegend()).Append('\n');
            sb.Append("      </svg>");
            return sb.ToString();
        }
    }
}
