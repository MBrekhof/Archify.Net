using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net;

// render-class.mjs. The module-level flow (line 57 to the final writeDiagram call) becomes one Scene built per render;
// `--layout-json` becomes the LayoutReport on the result and writeDiagram's HTML is returned by the facade.
public sealed class ClassRenderer : IDiagramRenderer
{
    public (string Svg, JsonArray? Cards, LayoutReport? Layout) Render(RenderContext ctx)
    {
        var scene = new Scene(ctx);
        scene.ValidateClassDiagram();
        var svg = scene.RenderSvg();
        return (svg, ctx.Diagram.Arr("cards"), new LayoutReport(scene.BuildLayoutReport()));
    }

    static class Layout
    {
        public const double Margin = 24;
        public const double LegendH = 34;
        public const double PadX = 12;
        public const double HeaderH = 44;
        public const double HeaderPlainH = 34;
        public const double NameFont = 13.5;
        public const double StereotypeFont = 9.5;
        public const double MemberFont = 11;
        public const double RowH = 20;
        public const double CompartmentPad = 5;
        public const double MinRelationshipLength = 28;
    }

    // ---- Type kinds --------------------------------------------------------------
    // KIND_TONE (:89) in source order: `[...new Set(Object.values(KIND_TONE))]` drives the hue rules.
    static readonly (string Kind, string Tone)[] KIND_TONE =
    [
        ("class", "backend"),
        ("abstract", "backend"),
        ("interface", "frontend"),
        ("enum", "messagebus"),
        ("record", "database"),
    ];

    static string? Stereotype(string? kind) => kind switch
    {
        "abstract" => "«abstract»",
        "interface" => "«interface»",
        "enum" => "«enumeration»",
        "record" => "«record»",
        _ => null,
    };

    // VISIBILITY_GLYPH[member.visibility]: an unknown visibility is `undefined` (schema-unreachable: visibility is an enum).
    static string? VisibilityGlyph(string visibility) => visibility switch
    {
        "public" => "+", "protected" => "#", "package" => "~", "private" => "-", _ => null,
    };

    static string? VisibilityClass(string glyph) => glyph switch
    {
        "+" => "public", "#" => "protected", "~" => "package", "-" => "private", _ => null,
    };

    // Member rows are set in a monospace face, whose ASCII advance is the shared
    // 0.6em estimate; wide glyphs count as two units, so the measurement never
    // under-reserves space and a member is never clipped by its own box.
    const double MEMBER_ADVANCE = Layout.MemberFont * NodeTextFit.WidthFactor;
    const double CONTINUATION_INDENT = 2;
    const double VIS_CHIP = 14;
    const double VIS_GAP = 6;
    static readonly double VIS_EXTRA = Math.Ceiling(VIS_CHIP + VIS_GAP - 2 * MEMBER_ADVANCE);
    const double PARAMETER_INDENT = 4;
    const double BADGE_SIZE = 16;
    const double BADGE_PORCH = BADGE_SIZE + 8;

    // ---- Relationship notation -----------------------------------------------------
    sealed record Notation(string Kind, bool Dashed, string? End, string? Start);
    static readonly Notation[] NOTATION =
    [
        new("dependency", true, "open", null),
        new("association", false, "open", null),
        new("inheritance", false, "triangle", null),
        new("realization", true, "triangle", null),
        new("composition", false, null, "diamond-filled"),
        new("aggregation", false, null, "diamond-hollow"),
    ];
    static Notation? NotationOf(string? kind) => NOTATION.FirstOrDefault(n => n.Kind == kind);
    static bool IsGeneralization(string? kind) => kind is "inheritance" or "realization";

    // Marker glyphs, in marker space (:295), plus `triangle-start` appended at :303.
    sealed record Glyph(string Id, double Width, double Height, double RefX, double RefY, string? Orient, string Body);
    static readonly Glyph[] GLYPH =
    [
        new("open", 12, 12, 11, 6, null, "<path d=\"M 1 1 L 11 6 L 1 11\" fill=\"none\"/>"),
        new("triangle", 16, 16, 15, 8, null, "<path d=\"M 1 1.5 L 15 8 L 1 14.5 Z\" class=\"cl-marker-hollow\"/>"),
        new("diamond-filled", 20, 12, 1, 6, null, "<path d=\"M 1 6 L 10 1 L 19 6 L 10 11 Z\" class=\"cl-marker-filled\"/>"),
        new("diamond-hollow", 20, 12, 1, 6, null, "<path d=\"M 1 6 L 10 1 L 19 6 L 10 11 Z\" class=\"cl-marker-hollow\"/>"),
        new("triangle-start", 16, 16, 15, 8, "auto-start-reverse", "<path d=\"M 1 1.5 L 15 8 L 1 14.5 Z\" class=\"cl-marker-hollow\"/>"),
    ];
    const double MARKER_HEIGHT = 16;
    const double PORT_SPACING = 26;
    const double BUS_MIN_GAP = 40;
    const double LEGEND_SWATCH = 34;
    const double LEGEND_BASELINE_GAP = 16;
    const double LEGEND_FONT_SIZE = 10;
    const double RELATIONSHIP_LABEL_FONT = 9.5;
    const double RELATIONSHIP_LABEL_ADVANCE = 0.62;
    const double MARKER_REACH = 20;
    const double MARKER_HALF = 10;

    sealed record MemberRow(JsonObject Member, string Text);
    sealed record Compartment(string Kind, List<MemberRow> Rows);
    sealed record Line(string Text, double Indent);
    sealed record WrappedRow(JsonObject Member, string Text, List<Line> Lines);
    sealed record WrappedCompartment(string Kind, List<WrappedRow> Rows);
    sealed record Route(JsonObject Relation, List<Pt> Points);

    sealed class Scene
    {
        readonly RenderContext ctx;
        readonly JsonObject cd;
        readonly JsonObject meta;
        readonly string? locale;
        readonly string? gateProfile;
        // grid (:62): origin, gapX, gapY, typeW, typeMaxW overridden by cd.layout.
        readonly double originX, originY, gapX, gapY, typeW, typeMaxW;
        readonly List<Box> measuredTypes;
        readonly Bands bands;
        readonly JsMap<string, Box> types = new();
        readonly List<JsonObject> relationships;
        readonly JsMap<string, double> typeSteps = new();
        readonly List<LegendEntry> legendEntries;
        readonly double[] viewBox;
        readonly List<JsonObject> routable;
        readonly JsMap<JsonObject, List<Pt>> busPaths = new();
        readonly JsMap<JsonObject, List<JsonObject>> busGroups = new();
        bool separateFallbackRoutes;
        Router router = null!;
        readonly JsMap<JsonObject, Pt> resolvedLabelPoints = new();
        static readonly JsonObject NoSource = new();

        static string N(double v) => Js.Num(v);
        static string S(string? s) => s ?? "undefined";
        static bool Truthy(string? s) => !string.IsNullOrEmpty(s);
        Box? TypeOf(string? id) => id is null ? null : types.Get(id);
        List<Pt> PointsFor(JsonObject relationship) => PathFor(relationship).Points;

        public Scene(RenderContext ctx)
        {
            this.ctx = ctx;
            cd = ctx.Diagram;
            meta = ctx.Meta!;
            locale = meta.Str("locale");
            gateProfile = ctx.Options.Quality?.Name() ?? (Truthy(meta.Str("quality_profile")) ? meta.Str("quality_profile") : null);

            // The grid is always on: a type without `pos` is placed by its row/col cell.
            var layout = cd["layout"] as JsonObject;
            var origin = layout.Arr("origin");
            originX = origin is null ? 32 : Elem(origin, 0);
            originY = origin is null ? 40 : Elem(origin, 1);
            gapX = layout.Num("gapX") ?? 128;
            gapY = layout.Num("gapY") ?? 84;
            typeW = layout.Num("typeW") ?? 180;
            typeMaxW = layout.Num("typeMaxW") ?? 300;

            measuredTypes = cd["types"].AsArrayOrEmpty().Select(node =>
            {
                var type = (JsonObject)node!;
                var width = ResolvedWidth(type);
                return new Box { Id = S(type.Str("id")), Source = type, X = double.NaN, Y = double.NaN, Width = width, Height = TypeHeight(type, width) };
            }).ToList();
            var erGrid = new ErGrid(OriginX: originX, OriginY: originY, GapX: gapX, GapY: gapY, EntityW: typeW);
            bands = ErdGrid.BandedLayout(measuredTypes.Select(t => GridEntity.From(t.Source, t.Height, t.Width)).ToList(), erGrid);
            // Types are centred in their row band, so a relationship between two types of
            // one row runs straight across instead of jogging between their centres.
            foreach (var type in measuredTypes)
            {
                var (x, top) = ErdGrid.ResolveEntityPos(GridEntity.From(type.Source, type.Height, type.Width), erGrid, bands);
                var row = type.Source.Num("row");
                var bandHeight = row is { } r && bands.Heights.Has(r) ? bands.Heights.Get(r) : type.Height;
                type.X = x;
                type.Y = PlacedByGrid(type.Source) ? top + (bandHeight - type.Height) / 2 : top;
                types.Set(type.Id, type);
            }

            // Routing runs at module load, so a type without a position is reported before
            // any geometry is computed from it.
            var unplaced = types.Values.Where(type => !double.IsFinite(type.X) || !double.IsFinite(type.Y)).ToList();
            if (unplaced.Count > 0)
            {
                throw ctx.Diagnostics.ThrowDiagnosticProblems("Class diagram placement failed",
                    unplaced.Select(type => $"Type \"{type.Id}\" needs grid row/col or an absolute pos [x,y]."),
                    "layout/constraint", new JsonObject { ["diagramType"] = "class" });
            }

            relationships = cd["relationships"].AsArrayOrEmpty().Select(node =>
            {
                var relationship = (JsonObject)node!;
                if (relationship["via"] is not JsonArray { Count: 0 }) return relationship;
                // An empty path equals an omitted one for the bus and the ordinary router alike; the author's document is kept.
                var automatic = relationship.DeepClone().AsObject();
                automatic.Remove("via");
                return automatic;
            }).ToList();

            for (var index = 0; index < relationships.Count; index++)
            {
                var relationship = relationships[index];
                if (relationship.Str("to") is { } to && !typeSteps.Has(to)) typeSteps.Set(to, index);
                if (relationship.Str("from") is { } from && !typeSteps.Has(from)) typeSteps.Set(from, index + 1);
            }
            var typeIndex = 0;
            foreach (var node in cd["types"].AsArrayOrEmpty())
            {
                if (node.Str("id") is { } id && !typeSteps.Has(id)) typeSteps.Set(id, typeIndex);
                typeIndex++;
            }

            var legendCatalog = NOTATION.Select(n => new LegendEntry(n.Kind, ctx.Locale.Translate(locale, $"legend.class.{n.Kind}"), Interactive: false)).ToList();
            legendEntries = Legend.ResolveLegend(meta.Obj("legend"), legendCatalog, relationships.Select(r => r.Str("kind")).OfType<string>())
                .Select(entry => entry with { SwatchWidth = LEGEND_SWATCH }).ToList();

            viewBox = meta["viewBox"] is JsonArray authored ? [Elem(authored, 0), Elem(authored, 1)] : AutoViewBox();

            routable = relationships.Where(RenderableRelationship).ToList();

            // ---- Hierarchy bus -------------------------------------------------------------
            // Two or more automatic generalizations of one kind into one supertype, all
            // from subtypes placed below it, draw as one tree: a single triangle and trunk
            // under the supertype, a horizontal bus, and a drop to each subtype.
            {
                var groups = new JsMap<string, List<JsonObject>>();
                foreach (var relationship in routable)
                {
                    if (!IsGeneralization(relationship.Str("kind")) || !AutoRouted(relationship)) continue;
                    var key = $"{S(relationship.Str("to"))}\0{S(relationship.Str("kind"))}";
                    if (!groups.Has(key)) groups.Set(key, []);
                    groups.Get(key)!.Add(relationship);
                }
                foreach (var members in groups.Values)
                {
                    if (members.Count < 2) continue;
                    var target = TypeOf(members[0].Str("to"))!;
                    var bottom = target.Y + target.Height;
                    var sources = members.Select(relationship => TypeOf(relationship.Str("from"))!).ToList();
                    var firstTop = double.PositiveInfinity;
                    foreach (var source in sources) firstTop = Math.Min(firstTop, source.Y);
                    if (firstTop - bottom < BUS_MIN_GAP) continue;
                    var busY = Js.Round(bottom + (firstTop - bottom) / 2);
                    var paths = members.Select((relationship, index) =>
                    {
                        var source = sources[index];
                        return Math.Abs(source.Cx - target.Cx) < 0.5
                            ? new List<Pt> { new(source.Cx, source.Y), new(target.Cx, bottom) }
                            : [new(source.Cx, source.Y), new(source.Cx, busY), new(target.Cx, busY), new(target.Cx, bottom)];
                    }).ToList();
                    // Each drop may touch only its own subtype and the supertype; a deeper
                    // subtype's drop must not run through a sibling placed above it.
                    var clear = paths.Select((points, member) =>
                    {
                        var others = types.Values.Where(type => !ReferenceEquals(type, target) && !ReferenceEquals(type, sources[member])).ToList();
                        return points.Skip(1).Select((point, index) => others
                            .All(type => !Geometry.SegmentIntersectsRect(new Segment(points[index], point), type))).All(ok => ok);
                    }).All(ok => ok);
                    if (!clear) continue;
                    for (var index = 0; index < members.Count; index++)
                    {
                        busPaths.Set(members[index], paths[index]);
                        busGroups.Set(members[index], members);
                    }
                }
            }

            for (; ; )
            {
                router = OrdinaryRouter();
                if (busPaths.Count == 0) break;
                var routes = routable.Select(relation => new Route(relation, busPaths.Get(relation) ?? router.PathFor(relation).Points)).ToList();
                var rejected = new JsSet<List<JsonObject>>();
                for (var leftIndex = 0; leftIndex < routes.Count; leftIndex += 1)
                {
                    var left = routes[leftIndex];
                    var leftGroup = busGroups.Get(left.Relation);
                    for (var rightIndex = leftIndex + 1; rightIndex < routes.Count; rightIndex += 1)
                    {
                        var right = routes[rightIndex];
                        var rightGroup = busGroups.Get(right.Relation);
                        // Only one bus group may share ports and segments; other routes keep their own relationship markers.
                        if ((leftGroup is null && rightGroup is null) || ReferenceEquals(leftGroup, rightGroup)) continue;
                        if (!BusRouteConflict(left, right)) continue;
                        if (leftGroup is not null) rejected.Add(leftGroup);
                        if (rightGroup is not null) rejected.Add(rightGroup);
                    }
                }
                if (rejected.Count == 0) break;
                // Relationships that fell back stay separate even when they share a target: no corridor without a bus.
                separateFallbackRoutes = true;
                // Re-plan after the whole group falls back so new routes do not hit the remaining buses; bounded by groups.
                foreach (var group in rejected)
                {
                    foreach (var relationship in group)
                    {
                        busPaths.Delete(relationship);
                        busGroups.Delete(relationship);
                    }
                }
            }

            // The default point picks the roomiest segment but knows nothing about other
            // routes, markers, or types near it. A colliding unpinned label goes through the
            // shared bounded placer, with every end marker reserved.
            {
                var labels = routable.Where(relationship => relationship["label"].Truthy()).Select(relationship =>
                {
                    var (lx, ly) = DefaultRelationshipLabelPoint(relationship);
                    return LabelRectAt(relationship, relationships.IndexOf(relationship), lx, ly);
                }).ToList();
                if (labels.Count > 0)
                {
                    var markers = routable.SelectMany(relationship =>
                    {
                        var points = PathFor(relationship).Points;
                        return new[]
                        {
                            MarkerBox(points[0], ConnectionEndpointSide(relationship, "source")),
                            MarkerBox(points[^1], ConnectionEndpointSide(relationship, "target")),
                        };
                    }).ToList();
                    var footprint = legendEntries.Count > 0
                        ? Layout.LegendH + Legend.LegendFootprint(legendEntries, Math.Max(1, viewBox[0] - Layout.Margin * 2)).ExtraHeight
                        : 0;
                    var placed = AutomaticLabels.PlaceAutomaticLabels(
                        labels: labels,
                        routes: routable.Select(relationship => new LabelRoute(relationships.IndexOf(relationship), PathFor(relationship).Points)).ToList(),
                        components: [.. types.Values, .. markers],
                        titles: [],
                        viewBox: viewBox,
                        placementBottom: viewBox[1] - footprint,
                        keepFallbackNearRoute: true);
                    for (var index = 0; index < placed.Count; index++)
                    {
                        var rect = placed[index];
                        if (!ReferenceEquals(rect, labels[index])) resolvedLabelPoints.Set(rect.Relation, new Pt(rect.Lx!.Value, rect.Ly!.Value));
                    }
                }
            }
        }

        // A JS array element in arithmetic: anything but a number reads as NaN (undefined, or a non-number).
        static double Elem(JsonArray a, int i) => i < a.Count && a[i] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.D() : double.NaN;

        // ---- Members -------------------------------------------------------------------
        static string VisibilityPrefix(JsonObject member) =>
            member["visibility"].Truthy() ? $"{VisibilityGlyph(S(member.Str("visibility"))) ?? "undefined"} " : "";   // template prints `undefined`

        static string AttributeText(JsonObject attribute) =>
            $"{VisibilityPrefix(attribute)}{S(attribute.Str("name"))}{(attribute["type"].Truthy() ? $": {BrandMarks.JsString(attribute["type"])}" : "")}";

        static string MethodText(JsonObject method) =>
            $"{VisibilityPrefix(method)}{S(method.Str("name"))}({(method["parameters"] is { } parameters ? BrandMarks.JsString(parameters) : "")}){(method["returns"].Truthy() ? $": {BrandMarks.JsString(method["returns"])}" : "")}";

        // Bold display text runs wider than the regular estimate.
        static double NameWidth(JsonObject type) => Utils.TextUnits(type.Str("label")) * Layout.NameFont * NodeTextFit.WidthFactor * 1.08;

        static List<Compartment> MemberRows(JsonObject type) => new List<Compartment>
        {
            new("attributes", type["attributes"].AsArrayOrEmpty().Select(node => { var attribute = (JsonObject)node!; return new MemberRow(attribute, AttributeText(attribute)); }).ToList()),
            new("methods", type["methods"].AsArrayOrEmpty().Select(node => { var method = (JsonObject)node!; return new MemberRow(method, MethodText(method)); }).ToList()),
        }.Where(compartment => compartment.Rows.Count > 0).ToList();

        // A member longer than its box wraps instead of being cut (:149).
        static List<Line> FillLines(IEnumerable<string> pieces, double limit, double firstIndent, double indent)
        {
            var lines = new List<Line>();
            var current = "";
            double Room() => limit - (lines.Count > 0 ? indent : firstIndent);
            void Push(string text) => lines.Add(new Line(text, lines.Count > 0 ? indent : firstIndent));
            foreach (var piece in pieces)
            {
                if (Utils.TextUnits(current + piece) <= Room())
                {
                    current += piece;
                    continue;
                }
                if (current.Length > 0) Push(Js.TrimEnd(current));
                current = Js.TrimStart(piece);
                while (Utils.TextUnits(current) > Room())
                {
                    var chars = Js.CodePoints(current);
                    var cut = 0;
                    for (var units = 0; cut < chars.Count && units + Utils.TextUnits(chars[cut]) <= Room(); cut += 1) units += Utils.TextUnits(chars[cut]);
                    var at = Math.Max(1, cut);
                    Push(string.Concat(chars.Take(at)));
                    current = string.Concat(chars.Skip(at));
                }
            }
            if (current.Length > 0) Push(Js.TrimEnd(current));
            return lines;
        }

        static List<Line> WrapMember(string text, double availablePx)
        {
            var limit = Math.Max(8, Math.Floor(availablePx / MEMBER_ADVANCE));
            if (Utils.TextUnits(text) <= limit) return [new Line(text, 0)];
            var lines = FillLines(Js.SplitSignature(text), limit, 0, CONTINUATION_INDENT);
            var open = text.IndexOf('(');
            var close = text.LastIndexOf(')');
            if (open == -1 || close < open) return lines;
            var head = text[..(open + 1)];
            var parameters = Js.SplitAfterCommaSpace(text[(open + 1)..close]);
            var tail = text[close..];
            return
            [
                .. FillLines([head], limit, 0, CONTINUATION_INDENT),
                .. FillLines(parameters, limit, PARAMETER_INDENT, PARAMETER_INDENT),
                .. FillLines([tail], limit, CONTINUATION_INDENT, CONTINUATION_INDENT),
            ];
        }

        static double HeaderHeight(JsonObject type) => Stereotype(type.Str("kind")) is not null ? Layout.HeaderH : Layout.HeaderPlainH;

        static double HeaderWidth(JsonObject type)
        {
            var stereotype = Stereotype(type.Str("kind"));
            return Math.Ceiling(Layout.PadX * 2 + BADGE_PORCH * 2 + Math.Max(
                NameWidth(type),
                stereotype is not null ? Utils.TextUnits(stereotype) * Layout.StereotypeFont * NodeTextFit.WidthFactor : 0));
        }

        // An authored width is authoritative. An omitted one grows to the widest
        // member up to `layout.typeMaxW`; past that, members wrap.
        double ResolvedWidth(JsonObject type)
        {
            if (type.Num("width") is { } authored && double.IsFinite(authored)) return authored;
            double members = 0;
            foreach (var row in MemberRows(type).SelectMany(compartment => compartment.Rows))
                members = Math.Max(members, Utils.TextUnits(row.Text) * MEMBER_ADVANCE);
            var wanted = Math.Ceiling(members + Layout.PadX * 2 + VIS_EXTRA);
            return Math.Max(HeaderWidth(type), Math.Min(typeMaxW, Math.Max(typeW, wanted)));
        }

        static List<WrappedCompartment> Compartments(JsonObject type, double width)
        {
            var available = width - Layout.PadX * 2 - VIS_EXTRA;
            return MemberRows(type).Select(compartment => new WrappedCompartment(compartment.Kind,
                compartment.Rows.Select(row => new WrappedRow(row.Member, row.Text, WrapMember(row.Text, available))).ToList())).ToList();
        }

        static double TypeHeight(JsonObject type, double width)
        {
            double sum = 0;
            foreach (var compartment in Compartments(type, width))
            {
                double lines = 0;
                foreach (var row in compartment.Rows) lines += row.Lines.Count;
                sum += lines * Layout.RowH + Layout.CompartmentPad * 2;
            }
            return HeaderHeight(type) + sum;
        }

        // ---- Layout --------------------------------------------------------------------
        static bool PlacedByGrid(JsonObject type) => type["pos"] is not JsonArray { Count: 2 };

        (double MaxX, double MaxY) ContentBounds()
        {
            double maxX = 0, maxY = 0;
            foreach (var type in types.Values)
            {
                if (!double.IsFinite(type.X) || !double.IsFinite(type.Y)) continue;
                maxX = Math.Max(maxX, type.X + type.Width);
                maxY = Math.Max(maxY, type.Y + type.Height);
            }
            return (maxX, maxY);
        }

        LegendLayout LegendLayoutFor(double width, double height, IReadOnlyList<LegendObstacle>? obstacles = null, string unfit = "error") => new(
            X: Layout.Margin,
            BaselineY: height - LEGEND_BASELINE_GAP,
            Width: width - Layout.Margin * 2,
            FontSize: LEGEND_FONT_SIZE,
            MinTitleY: ContentBounds().MaxY + 8,
            Obstacles: obstacles ?? [],
            Unfit: unfit,
            DiagramType: "class");

        double[] AutoViewBox()
        {
            var (maxX, maxY) = ContentBounds();
            var width = Math.Ceiling(maxX + Layout.Margin);
            var footprint = Legend.LegendFootprint(legendEntries, Math.Max(1, width - Layout.Margin * 2));
            if (footprint.MinWidth > width - Layout.Margin * 2)
            {
                width = Math.Ceiling(footprint.MinWidth + Layout.Margin * 2);
                footprint = Legend.LegendFootprint(legendEntries, width - Layout.Margin * 2);
            }
            var height = Math.Ceiling(maxY + Layout.Margin + (legendEntries.Count > 0 ? Layout.LegendH + footprint.ExtraHeight : 0));
            for (var attempt = 0; legendEntries.Count > 0 && attempt < 4; attempt += 1)
            {
                if (Legend.MeasureLegend(ctx.Diagnostics, legendEntries, LegendLayoutFor(width, height, unfit: "hide")) is not null) break;
                height += Layout.LegendH;
            }
            return [Math.Max(320, width), Math.Max(240, height)];
        }

        // ---- Routing -------------------------------------------------------------------
        // A generalization is drawn vertically whenever the two types are not side by
        // side: the supertype above, its subtypes below. Other relationships keep the
        // shared horizontal-first inference, and stacked types always use their facing sides.
        string? SideFor(JsonObject relationship, string endpoint)
        {
            var from = TypeOf(relationship.Str("from"));
            var to = TypeOf(relationship.Str("to"));
            if (from is null || to is null) return null;
            var overlapX = from.X < to.X + to.Width && to.X < from.X + from.Width;
            var overlapY = from.Y < to.Y + to.Height && to.Y < from.Y + from.Height;
            var vertical = overlapX || (!overlapY && IsGeneralization(relationship.Str("kind")));
            if (!vertical) return endpoint == "source" ? Geometry.LegacyDefaultFromSide(from, to) : Geometry.LegacyDefaultToSide(from, to);
            var down = to.Cy > from.Cy;
            if (endpoint == "source") return down ? "bottom" : "top";
            return down ? "top" : "bottom";
        }

        bool RenderableRelationship(JsonObject relationship) =>
            TypeOf(relationship.Str("from")) is not null && TypeOf(relationship.Str("to")) is not null && relationship.Str("from") != relationship.Str("to");

        static bool AutoRouted(JsonObject relationship) =>
            relationship["via"] is not JsonArray { Count: > 0 }
            && (!relationship["route"].Truthy() || relationship.Str("route") == "auto")
            && !relationship["fromSide"].Truthy() && !relationship["toSide"].Truthy() && !relationship["labelAt"].Truthy();

        Router OrdinaryRouter() => Routing.CreateRouter(types, routable.Where(relationship => !busPaths.Has(relationship)).ToList(), new RouterOptions(
            SideFor: SideFor,
            MaxPortSpacing: PORT_SPACING,
            DistinctAutomaticPorts: separateFallbackRoutes,
            // As for the ERD: re-plan a route against the complete scene when it crosses
            // or bends needlessly, and look for a detour that crosses nothing first.
            PreferReadableRoutes: true,
            CrossingFreeGridFirst: true,
            // A detour keeps a readable gap from every type it only passes.
            ComponentGapPx: 10));

        static bool BusRouteConflict(Route left, Route right)
        {
            foreach (var (leftField, leftPoint) in new[] { ("from", left.Points[0]), ("to", left.Points[^1]) })
            {
                foreach (var (rightField, rightPoint) in new[] { ("from", right.Points[0]), ("to", right.Points[^1]) })
                {
                    if (left.Relation.Str(leftField) == right.Relation.Str(rightField)
                        && Js.Hypot(leftPoint.X - rightPoint.X, leftPoint.Y - rightPoint.Y) < MARKER_HEIGHT - 0.0001) return true;
                }
            }
            if (Geometry.CollectAmbiguousCorridors([new RoutedRelation(left.Points, left.Relation), new RoutedRelation(right.Points, right.Relation)],
                includeSharedEndpoints: (_, _) => true).Count > 0) return true;
            for (var index = 0; index < left.Points.Count - 1; index++)
                for (var otherIndex = 0; otherIndex < right.Points.Count - 1; otherIndex++)
                    if (Geometry.ProperSegmentIntersection(left.Points[index], left.Points[index + 1], right.Points[otherIndex], right.Points[otherIndex + 1]) is not null) return true;
            return false;
        }

        RoutedPath PathFor(JsonObject relationship)
        {
            var points = busPaths.Get(relationship);
            return points is not null ? new RoutedPath(Geometry.RoundedPath(points, 8), points) : router.PathFor(relationship);
        }

        // A bus drop leaves the subtype's top and the trunk enters the supertype's
        // bottom; every other relationship keeps the router's resolved sides.
        string ConnectionEndpointSide(JsonObject relationship, string endpoint)
        {
            if (busPaths.Has(relationship)) return endpoint == "source" ? "top" : "bottom";
            return router.ConnectionEndpointSide(relationship, endpoint);
        }

        Sides InferredSides(JsonObject relationship)
        {
            if (busPaths.Has(relationship)) return new Sides("top", "bottom");
            return router.InferredSides(relationship);
        }

        static double RelationshipLabelWidth(JsonObject relationship) =>
            Math.Max(30, Utils.TextUnits(relationship.Str("label")) * RELATIONSHIP_LABEL_FONT * RELATIONSHIP_LABEL_ADVANCE + 10);

        // A label sits on the segment with the most room unless the author picks one:
        // above a horizontal run, beside a vertical one.
        Pt RelationshipLabelPoint(JsonObject relationship) =>
            resolvedLabelPoints.Has(relationship) ? resolvedLabelPoints.Get(relationship) : DefaultRelationshipLabelPoint(relationship);

        Pt DefaultRelationshipLabelPoint(JsonObject relationship)
        {
            if (relationship["labelAt"].Truthy() && relationship["labelAt"] is JsonArray at) return new Pt(Elem(at, 0), Elem(at, 1));
            var points = PathFor(relationship).Points;
            var width = RelationshipLabelWidth(relationship);
            bool Vertical(int index) => Math.Abs(points[index].X - points[index + 1].X) < Math.Abs(points[index].Y - points[index + 1].Y);
            double Room(int index)
            {
                var length = Js.Hypot(points[index + 1].X - points[index].X, points[index + 1].Y - points[index].Y);
                return length - (Vertical(index) ? 22 : width + 8);
            }
            int index;
            if (relationship.Num("labelSegment") is { } segment && double.IsInteger(segment))
            {
                index = (int)Math.Min(points.Count - 2, segment);
            }
            else
            {
                index = 0;
                for (var candidate = 0; candidate < points.Count - 1; candidate++) if (Room(candidate) > Room(index)) index = candidate;
            }
            var (a, b) = (points[index], points[index + 1]);
            var dx = relationship.Num("labelDx") ?? 0;
            var dy = relationship.Num("labelDy") ?? 0;
            if (Vertical(index)) return new Pt(a.X + width / 2 + 6 + dx, (a.Y + b.Y) / 2 + 4 + dy);
            return new Pt((a.X + b.X) / 2 + dx, a.Y - 6 + dy);
        }

        Rect RelationshipLabelBox(JsonObject relationship)
        {
            var (lx, ly) = RelationshipLabelPoint(relationship);
            var width = RelationshipLabelWidth(relationship);
            return new Rect(lx - width / 2, ly - 10, width, 14);
        }

        // `{ relation, relationIndex, label, ...relationshipLabelBox(relationship), lx, ly }`.
        static LabelRect LabelRectAt(JsonObject relationship, int relationIndex, double lx, double ly)
        {
            var width = RelationshipLabelWidth(relationship);
            return new LabelRect
            {
                Relation = relationship, RelationIndex = relationIndex, Label = relationship.Str("label"),
                X = lx - width / 2, Y = ly - 10, Width = width, Height = 14, Lx = lx, Ly = ly,
            };
        }

        static Box MarkerBox(Pt point, string side)
        {
            var (x, y) = point;
            var (bx, by, bw, bh) = side switch
            {
                "left" => (x - MARKER_REACH, y - MARKER_HALF, MARKER_REACH, MARKER_HALF * 2),
                "right" => (x, y - MARKER_HALF, MARKER_REACH, MARKER_HALF * 2),
                "top" => (x - MARKER_HALF, y - MARKER_REACH, MARKER_HALF * 2, MARKER_REACH),
                _ => (x - MARKER_HALF, y, MARKER_HALF * 2, MARKER_REACH),
            };
            return new Box { Id = "", Source = NoSource, X = bx, Y = by, Width = bw, Height = bh };
        }

        // ---- Validation ----------------------------------------------------------------
        static Diagnostic Detail(string code, string message, JsonObject subject, JsonObject evidence, IReadOnlyList<string> supportedFixes)
        {
            var full = new JsonObject { ["diagramType"] = "class" };
            foreach (var (key, value) in subject) full[key] = value?.DeepClone();
            return new Diagnostic(code, Severity.Error, message, full, evidence, supportedFixes, null);
        }

        List<string>? GeneralizationCycle()
        {
            var parents = new JsMap<string, List<string>>();
            foreach (var relationship in relationships)
            {
                if (relationship.Str("kind") != "inheritance" || !RenderableRelationship(relationship)) continue;
                var from = relationship.Str("from")!;
                if (!parents.Has(from)) parents.Set(from, []);
                parents.Get(from)!.Add(relationship.Str("to")!);
            }
            var state = new JsMap<string, string>();
            var stack = new List<string>();
            List<string>? Visit(string id)
            {
                if (state.Get(id) == "done") return null;
                if (state.Get(id) == "open") return [.. stack.Skip(stack.IndexOf(id)), id];
                state.Set(id, "open");
                stack.Add(id);
                foreach (var parent in parents.Get(id) ?? [])
                {
                    var cycle = Visit(parent);
                    if (cycle is not null) return cycle;
                }
                stack.RemoveAt(stack.Count - 1);
                state.Set(id, "done");
                return null;
            }
            foreach (var id in parents.Keys.ToList())
            {
                var cycle = Visit(id);
                if (cycle is not null) return cycle;
            }
            return null;
        }

        public void ValidateClassDiagram()
        {
            var problems = new List<string>();
            var details = new List<Diagnostic>();
            void Fail(Diagnostic entry)
            {
                problems.Add(entry.Message);
                details.Add(entry);
            }

            var seen = new JsSet<string>();
            var cells = new JsMap<string, string>();
            var index = -1;
            foreach (var node in cd["types"].AsArrayOrEmpty())
            {
                index++;
                var type = (JsonObject)node!;
                var typeId = S(type.Str("id"));
                if (seen.Has(typeId)) problems.Add($"Type ids must be unique; \"{typeId}\" is declared twice.");
                seen.Add(typeId);
                if (PlacedByGrid(type))
                {
                    var row = type.Num("row");
                    var col = type.Num("col");
                    if (!(row is { } r && double.IsInteger(r)) || !(col is { } c && double.IsInteger(c * 2)))
                    {
                        problems.Add($"Type \"{typeId}\" needs grid row/col (col may be a half step, e.g. 1.5) or an absolute pos [x,y].");
                    }
                    else
                    {
                        // A half column straddles the two columns beside it.
                        var columns = new JsSet<double>();
                        columns.Add(Math.Floor(c));
                        columns.Add(Math.Ceiling(c));
                        foreach (var column in columns)
                        {
                            var key = $"{N(r)},{N(column)}";
                            if (cells.Has(key)) problems.Add($"Types \"{cells.Get(key)}\" and \"{typeId}\" share grid cell row {N(r)} col {N(column)}.");
                            else cells.Set(key, typeId);
                        }
                    }
                }
                var attributeNames = new JsSet<string>();
                foreach (var attribute in type["attributes"].AsArrayOrEmpty())
                {
                    var name = S(attribute.Str("name"));
                    if (attributeNames.Has(name)) problems.Add($"Type \"{typeId}\" declares attribute \"{name}\" twice.");
                    attributeNames.Add(name);
                }
                // Members wrap, but the title is one line: a box must be wide enough for it.
                var placed = types.Get(typeId);
                var needed = HeaderWidth(type);
                if (placed is not null && needed > placed.Width)
                {
                    Fail(Detail("class/title-text-capacity",
                        $"Type \"{typeId}\" title \"{S(type.Str("label"))}\" needs {N(needed)}px but the type is {N(placed.Width)}px wide — remove or raise the authored width, or shorten the title.",
                        new JsonObject { ["path"] = $"/types/{N(index)}/width", ["typeId"] = type["id"]?.DeepClone() },
                        new JsonObject { ["label"] = type["label"]?.DeepClone(), ["width"] = placed.Width, ["requiredWidth"] = needed },
                        ["Remove the authored width so the type sizes itself.", $"Set width to at least {N(needed)}.", "Shorten the type title."]));
                }
            }

            foreach (var type in types.Values)
            {
                foreach (var other in types.Values)
                {
                    if (string.CompareOrdinal(type.Id, other.Id) >= 0 || !Geometry.RectsOverlap(type, other, 0)) continue;
                    problems.Add($"Type \"{type.Id}\" overlaps type \"{other.Id}\".\n{Geometry.SuggestComponentSeparation(type, other)}");
                }
            }

            for (var relationIndex = 0; relationIndex < relationships.Count; relationIndex++)
            {
                var relationship = relationships[relationIndex];
                var id = relationship["id"].Truthy() ? $" id \"{BrandMarks.JsString(relationship["id"])}\"" : "";
                var from = TypeOf(relationship.Str("from"));
                var to = TypeOf(relationship.Str("to"));
                if (from is null || to is null)
                {
                    problems.Add($"Relationship {N(relationIndex)}{id} references unknown type \"{S(from is not null ? relationship.Str("to") : relationship.Str("from"))}\".");
                    continue;
                }
                if (relationship.Str("from") == relationship.Str("to"))
                {
                    problems.Add($"Relationship {N(relationIndex)}{id} connects type \"{S(relationship.Str("from"))}\" to itself — describe the self-reference as a member type or in a card.");
                    continue;
                }
                Box fromBox = from, toBox = to;
                var fromKind = fromBox.Source.Str("kind");
                var toKind = toBox.Source.Str("kind");
                JsonObject KindEvidence()
                {
                    var evidence = new JsonObject { ["from"] = relationship["from"]?.DeepClone() };
                    if (fromBox.Source["kind"] is { } fk) evidence["fromKind"] = fk.DeepClone();
                    evidence["to"] = relationship["to"]?.DeepClone();
                    if (toBox.Source["kind"] is { } tk) evidence["toKind"] = tk.DeepClone();
                    return evidence;
                }
                // The hollow triangle claims a supertype; the claim has to agree with the
                // kinds, or the diagram states an impossible hierarchy.
                if (relationship.Str("kind") == "realization" && (toKind != "interface" || fromKind == "interface"))
                {
                    Fail(Detail("class/realization-target",
                        $"Relationship {N(relationIndex)}{id} \"{S(relationship.Str("from"))}\" realizes \"{S(relationship.Str("to"))}\", but realization runs from a non-interface type to an interface (here {S(fromKind)} -> {S(toKind)}). Use inheritance for class-to-class or interface-to-interface extension.",
                        new JsonObject { ["path"] = $"/relationships/{N(relationIndex)}", ["relationshipIndex"] = relationIndex },
                        KindEvidence(),
                        ["Use kind \"inheritance\" for extension between two classes or two interfaces.", "Point the realization at the interface it implements."]));
                }
                if (relationship.Str("kind") == "inheritance" && (fromKind == "interface") != (toKind == "interface"))
                {
                    Fail(Detail("class/inheritance-kind",
                        $"Relationship {N(relationIndex)}{id} \"{S(relationship.Str("from"))}\" inherits from \"{S(relationship.Str("to"))}\" across an interface boundary ({S(fromKind)} -> {S(toKind)}). A class implementing an interface is a realization.",
                        new JsonObject { ["path"] = $"/relationships/{N(relationIndex)}", ["relationshipIndex"] = relationIndex },
                        KindEvidence(),
                        ["Use kind \"realization\" for a class implementing an interface."]));
                }
                var routed = PathFor(relationship);
                var (start, end) = (routed.Points[0], routed.Points[^1]);
                var distance = Js.Hypot(end.X - start.X, end.Y - start.Y);
                if (distance < Layout.MinRelationshipLength)
                {
                    problems.Add($"Relationship {N(relationIndex)}{id} \"{S(relationship.Str("from"))}\" -> \"{S(relationship.Str("to"))}\" is too short ({N(Js.Round(distance))}px; minimum {N(Layout.MinRelationshipLength)}px) — place the types farther apart.");
                }
            }

            var cycle = GeneralizationCycle();
            if (cycle is not null)
            {
                Fail(Detail("class/inheritance-cycle",
                    $"Inheritance forms a cycle: {string.Join(" -> ", cycle.Select(id => $"\"{id}\""))}. A type cannot be its own supertype.",
                    new JsonObject { ["path"] = "/relationships", ["typeId"] = cycle[0] },
                    new JsonObject { ["cycle"] = new JsonArray([.. cycle.Select(id => (JsonNode?)id)]) },
                    ["Remove or reverse one inheritance relationship in the cycle."]));
            }

            // Busy sides can leave less room than a marker is tall: the router spreads
            // ports by min(PORT_SPACING, available / (n - 1)).
            foreach (var (typeId, type) in types.Entries)
            {
                var sides = new JsMap<string, double>();
                // A bus enters its supertype through one shared trunk, which is one port.
                foreach (var relationship in routable.Where(candidate => !busPaths.Has(candidate)))
                {
                    var (fromSide, toSide) = InferredSides(relationship);
                    if (relationship.Str("from") == typeId) sides.Set(fromSide, sides.Get(fromSide) + 1);
                    if (relationship.Str("to") == typeId) sides.Set(toSide, sides.Get(toSide) + 1);
                }
                foreach (var (side, count) in sides.Entries)
                {
                    if (count < 2) continue;
                    var extent = side is "left" or "right" ? type.Height : type.Width;
                    var spacing = Math.Min(PORT_SPACING, Math.Max(0, extent - 32) / (count - 1));
                    if (spacing >= MARKER_HEIGHT) continue;
                    Fail(Detail("layout/marker-capacity",
                        $"Type \"{typeId}\" shares its {side} side with {N(count)} relationship ends {N(Js.Round(spacing))}px apart, under the {N(MARKER_HEIGHT)}px a relationship marker needs, so the markers overlap. Spread the relationships across sides or place the related types so they arrive from different directions.",
                        new JsonObject { ["path"] = $"/types/{typeId}" },
                        new JsonObject
                        {
                            ["type"] = typeId, ["side"] = side, ["relationshipEnds"] = count, ["spacingPx"] = Js.Round(spacing),
                            ["markerHeightPx"] = MARKER_HEIGHT, ["sideExtentPx"] = extent,
                        },
                        ["Spread the relationships across sides with room.", "Move related types so they approach from different sides."]));
                }
            }

            var sink = ctx.Diagnostics;
            var endpointIds = new JsSet<string>();
            foreach (var id in types.Keys) endpointIds.Add(id);
            problems.AddRange(Geometry.CleanEndpointSideProblems(sink, routable, endpointIds, PointsFor, "class", "relationships",
                relationship => ConnectionEndpointSide(relationship, "source"),
                relationship => ConnectionEndpointSide(relationship, "target"),
                routeHint: "keep automatic routing, or set truthful fromSide/toSide"));
            problems.AddRange(Geometry.CleanFlowProblems(sink, routable, types.Values, PointsFor, "class", "relationships", "type",
                routeHint: "adjust fromSide/toSide, set route/via, or move the type"));
            problems.AddRange(Geometry.CleanCrossingProblems(sink, routable, endpointIds, PointsFor, "class", "relationships",
                profile: gateProfile, routeHint: "move the types so unrelated relationships use separate corridors"));
            problems.AddRange(Geometry.CleanAmbiguousCorridorProblems(sink, routable, endpointIds, PointsFor, "class", "relationships",
                profile: gateProfile, routeHint: "give the relationships different sides, or move a type so their corridors differ"));
            problems.AddRange(Geometry.CleanRouteRhythmProblems(sink, routable, endpointIds, PointsFor, "class", "relationships",
                gateProfile, routeHint: "widen layout.gapX/gapY so every turn has room to read"));

            var labels = new List<LabelRect>();
            for (var relationIndex = 0; relationIndex < relationships.Count; relationIndex++)
            {
                var relationship = relationships[relationIndex];
                if (!relationship["label"].Truthy() || !RenderableRelationship(relationship)) continue;
                var (lx, ly) = RelationshipLabelPoint(relationship);
                labels.Add(LabelRectAt(relationship, relationIndex, lx, ly));
            }
            foreach (var rect in labels)
            {
                foreach (var type in types.Values)
                {
                    if (!Geometry.RectsOverlap(rect, type, -2)) continue;
                    problems.Add($"Label \"{S(rect.Label)}\" overlaps type \"{type.Id}\" — adjust labelDx/labelDy/labelSegment or set labelAt.\n{Geometry.SuggestLabelObstacleFix(rect, rect.Lx!.Value, rect.Ly!.Value, type, "type")}");
                }
            }
            problems.AddRange(Geometry.CleanLabelRouteClearanceProblems(sink, routable, labels, endpointIds, PointsFor, "class", "relationships", gateProfile));

            if (problems.Count > 0)
            {
                throw sink.ThrowDiagnosticProblems("Class diagram validation failed", problems, "layout/constraint",
                    new JsonObject { ["diagramType"] = "class" }, details);
            }
        }

        public JsonObject BuildLayoutReport() => new()
        {
            ["diagram_type"] = "class",
            ["viewBox"] = meta["viewBox"] is JsonArray authored ? authored.DeepClone() : new JsonArray(viewBox[0], viewBox[1]),
            ["types"] = new JsonArray([.. types.Values.Select(type =>
            {
                var box = new JsonObject();
                if (type.Source["id"] is { } id) box["id"] = id.DeepClone();
                if (type.Source["label"] is { } label) box["label"] = label.DeepClone();
                if (type.Source["kind"] is { } kind) box["kind"] = kind.DeepClone();
                box["x"] = Js.Round(type.X);
                box["y"] = Js.Round(type.Y);
                box["width"] = type.Width;
                box["height"] = type.Height;
                if (type.Source.Num("row") is { } row && double.IsInteger(row)) box["row"] = row;
                if (type.Source.Num("col") is { } col && double.IsInteger(col)) box["col"] = col;
                return (JsonNode?)box;
            })]),
            ["relationships"] = new JsonArray([.. routable.Select(relationship =>
            {
                Pt? labelAt = relationship["labelAt"].Truthy() && relationship["labelAt"] is JsonArray at ? new Pt(Elem(at, 0), Elem(at, 1)) : null;
                var path = LayoutReport.ConnectionPath(relationship, PathFor(relationship).Points, labelAt);
                path["kind"] = relationship["kind"]?.DeepClone();
                return (JsonNode?)path;
            })]),
        };

        // ---- Rendering -----------------------------------------------------------------
        // A member reads like a signature in an editor: the visibility glyph and every
        // type annotation are quiet, so the names carry the row.
        static string Punctuated(string? text)
        {
            var sb = new StringBuilder();
            foreach (var mark in Utils.Esc(text))
            {
                if (mark is '(' or ')' or ',') sb.Append("<tspan class=\"t-muted cl-punct\">").Append(mark).Append("</tspan>");
                else sb.Append(mark);
            }
            return sb.ToString();
        }

        static readonly Regex VisibilityGlyphPattern = new("^([+#~-]) ", RegexOptions.ECMAScript | RegexOptions.CultureInvariant);
        static readonly Regex Annotation = new(":(" + Js.WsClass + "*)([^,()]+)", RegexOptions.ECMAScript | RegexOptions.CultureInvariant);

        static string MemberSpans(string text, bool first)
        {
            var out_ = new StringBuilder();
            var rest = text;
            // A continuation line with no annotation of its own is the rest of a type
            // (`PaymentProcessor>`), so it is set like the start of that type.
            if (!first && !rest.Contains(':') && !rest.StartsWith(')')) return $"<tspan class=\"t-muted cl-type-ref\">{Utils.Esc(rest)}</tspan>";
            var glyph = first ? VisibilityGlyphPattern.Match(rest) : null;
            if (glyph is { Success: true })
            {
                out_.Append($"<tspan class=\"t-muted cl-vis cl-vis-{S(VisibilityClass(glyph.Groups[1].Value))}\">{Utils.Esc(glyph.Groups[1].Value)}</tspan> ");
                rest = rest[2..];
            }
            var last = 0;
            foreach (Match match in Annotation.Matches(rest))
            {
                out_.Append($"{Punctuated(rest[last..match.Index])}<tspan class=\"t-muted cl-punct\">:</tspan>{Utils.Esc(match.Groups[1].Value)}<tspan class=\"t-muted cl-type-ref\">{Utils.Esc(match.Groups[2].Value)}</tspan>");
                last = match.Index + match.Length;
            }
            return out_.Append(Punctuated(rest[last..])).ToString();
        }

        // The visibility glyph is set as a chip in its own column, like a key badge in an ERD.
        static string VisibilityChip(string glyph, double x, double baseline)
        {
            var kind = S(VisibilityClass(glyph));
            return $"<rect x=\"{N(x)}\" y=\"{N(baseline - 10.5)}\" width=\"{N(VIS_CHIP)}\" height=\"{N(VIS_CHIP)}\" rx=\"4\" class=\"cl-vis-chip cl-vis-{kind}\"/><text x=\"{N(x + VIS_CHIP / 2)}\" y=\"{N(baseline)}\" class=\"t-muted cl-vis cl-vis-{kind}\" font-size=\"{N(Layout.MemberFont)}\" text-anchor=\"middle\">{Utils.Esc(glyph)}</text>";
        }

        static (string Left, string? Right) MemberColumns(JsonObject member, string kind)
        {
            if (kind == "attributes") return (Punctuated(member.Str("name")), member["type"].Truthy() ? BrandMarks.JsString(member["type"]) : null);
            var parameters = member["parameters"].Truthy()
                ? string.Concat(Js.SplitKeepingCommas(BrandMarks.JsString(member["parameters"])).Select(part =>
                {
                    if (Js.IsCommaPart(part)) return "<tspan class=\"t-muted cl-punct\">,</tspan> ";
                    var pieces = part.Split(':');
                    return pieces.Length > 1
                        ? $"<tspan class=\"cl-param\">{Utils.Esc(pieces[0])}</tspan><tspan class=\"t-muted cl-punct\">:</tspan><tspan class=\"t-muted cl-type-ref\">{Utils.Esc(string.Join(':', pieces.Skip(1)))}</tspan>"
                        : $"<tspan class=\"cl-param\">{Utils.Esc(part)}</tspan>";
                }))
                : "";
            return ($"{Utils.Esc(member.Str("name"))}<tspan class=\"t-muted cl-punct\">(</tspan>{parameters}<tspan class=\"t-muted cl-punct\">)</tspan>",
                member["returns"].Truthy() ? BrandMarks.JsString(member["returns"]) : null);
        }

        static string RenderMembers(Box type)
        {
            var top = type.Y + HeaderHeight(type.Source);
            var stripe = 0;
            var blocks = new List<string>();
            foreach (var compartment in Compartments(type.Source, type.Width))
            {
                var rule = $"<line x1=\"{N(type.X)}\" y1=\"{N(top)}\" x2=\"{N(type.X + type.Width)}\" y2=\"{N(top)}\" class=\"cl-rule\" stroke-width=\"1\"/>";
                double line = 0;
                var rows = new List<string>();
                foreach (var (member, _, lines) in compartment.Rows)
                {
                    var style = string.Join("; ", new[]
                    {
                        member["static"].Truthy() ? "text-decoration: underline" : "",
                        member["abstract"].Truthy() ? "font-style: italic" : "",
                    }.Where(Truthy));
                    var styleAttr = style.Length > 0 ? $" style=\"{style}\"" : "";
                    var rowTop = top + Layout.CompartmentPad + line * Layout.RowH;
                    var zebra = stripe++ % 2 == 1
                        ? $"<rect x=\"{N(type.X + 4)}\" y=\"{N(rowTop + 1)}\" width=\"{N(type.Width - 8)}\" height=\"{N(lines.Count * Layout.RowH - 2)}\" rx=\"4\" class=\"cl-zebra\"/>"
                        : "";
                    var glyph = member["visibility"].Truthy() ? VisibilityGlyph(S(member.Str("visibility"))) ?? "" : "";   // `undefined` is falsy: no chip
                    var x0 = type.X + Layout.PadX;
                    var chip = glyph.Length > 0 ? VisibilityChip(glyph, x0, rowTop + Layout.RowH - 5) : "";
                    var textX = glyph.Length > 0 ? x0 + VIS_CHIP + VIS_GAP : x0;
                    if (lines.Count == 1)
                    {
                        var baseline = top + Layout.CompartmentPad + (line += 1) * Layout.RowH - 5;
                        var (left, right) = MemberColumns(member, compartment.Kind);
                        var typeText = right is not null
                            ? $"<text data-detail=\"context\" x=\"{N(type.X + type.Width - Layout.PadX)}\" y=\"{N(baseline)}\" class=\"t-muted cl-member cl-type-ref\" font-size=\"{N(Layout.MemberFont)}\" text-anchor=\"end\"{styleAttr}>{Utils.Esc(right)}</text>"
                            : "";
                        rows.Add($"{zebra}{chip}<text data-detail=\"context\" data-class-member=\"{compartment.Kind}\" x=\"{N(textX)}\" y=\"{N(baseline)}\" class=\"t-primary cl-member\" font-size=\"{N(Layout.MemberFont)}\"{styleAttr}>{left}</text>{typeText}");
                        continue;
                    }
                    for (var index = 0; index < lines.Count; index++)
                    {
                        var entry = lines[index];
                        var baseline = top + Layout.CompartmentPad + (line += 1) * Layout.RowH - 5;
                        var text = index > 0 || glyph.Length == 0 ? entry.Text : entry.Text.Length >= 2 ? entry.Text[2..] : "";
                        var x = index > 0 || glyph.Length == 0 ? x0 + (glyph.Length > 0 ? VIS_CHIP + VIS_GAP - 2 * MEMBER_ADVANCE : 0) + entry.Indent * MEMBER_ADVANCE : textX;
                        var continued = index > 0 ? " data-class-continuation=\"\"" : "";
                        rows.Add($"{(index > 0 ? "" : $"{zebra}{chip}")}<text data-detail=\"context\" data-class-member=\"{compartment.Kind}\"{continued} x=\"{N(x)}\" y=\"{N(baseline)}\" class=\"t-primary cl-member\" font-size=\"{N(Layout.MemberFont)}\"{styleAttr}>{MemberSpans(text, index == 0)}</text>");
                    }
                }
                top += line * Layout.RowH + Layout.CompartmentPad * 2;
                blocks.Add($"          <g data-detail=\"context\" data-class-compartment=\"{compartment.Kind}\">\n            {rule}\n            {string.Join("\n            ", rows)}\n          </g>");
            }
            return string.Join("\n", blocks);
        }

        string RenderType(Box type)
        {
            var source = type.Source;
            var kind = source.Str("kind");
            var tone = KIND_TONE.FirstOrDefault(entry => entry.Kind == kind).Tone ?? "backend";
            var header = HeaderHeight(source);
            var stereotype = Stereotype(kind);
            var kindLabel = ctx.Locale.Translate(locale, $"node.context.class.{S(kind)}");
            var passport = new JsonObject();
            if (source["kind"] is { } kindNode) passport["kind"] = kindNode.DeepClone();
            if (source["sublabel"] is { } sublabel) passport["sublabel"] = sublabel.DeepClone();
            passport["context"] = kindLabel;
            var cx = type.X + type.Width / 2;
            var nameY = stereotype is not null ? type.Y + 34 : type.Y + 22;
            var badgeX = type.X + Layout.PadX;
            var badgeY = type.Y + (stereotype is not null ? 14 : 9);
            var italic = kind == "abstract" ? " font-style=\"italic\"" : "";
            const double r = 8;
            var label = S(source.Str("label"));
            var letter = kind is { Length: > 0 } ? char.ToUpperInvariant(kind[0]).ToString() : "";
            return $"        <g {SvgAttrs.FocusNodeAttrs(type.Id, label, passport, locale, ctx)} data-class-kind=\"{Utils.Esc(kind)}\" class=\"cl-hue-{tone}\">\n"
                + $"          {SvgAttrs.FocusNodeTitle(label, passport)}\n"
                + $"          <rect x=\"{N(type.X)}\" y=\"{N(type.Y)}\" width=\"{N(type.Width)}\" height=\"{N(type.Height)}\" rx=\"{N(r)}\" class=\"c-mask\"/>\n"
                + $"          <rect x=\"{N(type.X)}\" y=\"{N(type.Y)}\" width=\"{N(type.Width)}\" height=\"{N(type.Height)}\" rx=\"{N(r)}\" class=\"c-{tone} cl-type\"{SvgAttrs.AnimateAttr(meta, "node", typeSteps.Has(type.Id) ? typeSteps.Get(type.Id) : null)} stroke-width=\"1.2\"/>\n"
                + $"          <path class=\"cl-header c-{tone}\" stroke=\"none\" d=\"M {N(type.X + r)} {N(type.Y)} H {N(type.X + type.Width - r)} A {N(r)} {N(r)} 0 0 1 {N(type.X + type.Width)} {N(type.Y + r)} V {N(type.Y + header)} H {N(type.X)} V {N(type.Y + r)} A {N(r)} {N(r)} 0 0 1 {N(type.X + r)} {N(type.Y)} Z\"/>\n"
                + $"          <path class=\"cl-cap\" d=\"M {N(type.X + 12)} {N(type.Y + 1.4)} H {N(type.X + type.Width - 12)}\" stroke-width=\"2.6\" stroke-linecap=\"round\" fill=\"none\"/>\n"
                + "          <g aria-hidden=\"true\" class=\"cl-badge\">\n"
                + $"            <rect x=\"{N(badgeX)}\" y=\"{N(badgeY)}\" width=\"{N(BADGE_SIZE)}\" height=\"{N(BADGE_SIZE)}\" rx=\"{N(BADGE_SIZE * 0.3)}\" class=\"cl-badge-chip\"/>\n"
                + $"            <text x=\"{N(badgeX + BADGE_SIZE / 2)}\" y=\"{N(badgeY + BADGE_SIZE / 2 + 3.5)}\" class=\"t-{tone} cl-badge-letter\" font-size=\"10\" font-weight=\"800\" text-anchor=\"middle\">{Utils.Esc(letter)}</text>\n"
                + "          </g>\n"
                + $"          {(stereotype is not null ? $"<text x=\"{N(cx)}\" y=\"{N(type.Y + 16)}\" class=\"t-muted cl-stereotype\" font-size=\"{N(Layout.StereotypeFont)}\" text-anchor=\"middle\">{Utils.Esc(stereotype)}</text>" : "")}\n"
                + $"          <text data-node-label=\"\" x=\"{N(cx)}\" y=\"{N(nameY)}\" class=\"t-primary\" font-size=\"{N(Layout.NameFont)}\" font-weight=\"700\" text-anchor=\"middle\"{italic}>{Utils.Esc(label)}</text>\n"
                + $"{RenderMembers(type)}\n"
                + "        </g>";
        }

        static string MarkerAttrs(string? kind, bool reversed = false)
        {
            var notation = NotationOf(kind)!;
            if (reversed) return $" marker-start=\"url(#cl-{notation.End}-start)\"";
            return $"{(notation.Start is not null ? $" marker-start=\"url(#cl-{notation.Start})\"" : "")}{(notation.End is not null ? $" marker-end=\"url(#cl-{notation.End})\"" : "")}";
        }

        static string RelationshipClass(string? kind) => $"a-default cl-relationship{(NotationOf(kind)!.Dashed ? " cl-dashed" : "")}";

        string RenderMarkerDefs()
        {
            var used = new JsSet<string>();
            foreach (var relationship in relationships)
            {
                var notation = NotationOf(relationship.Str("kind"));
                if (notation?.End is { } end) used.Add(end);
                if (notation?.Start is { } start) used.Add(start);
            }
            if (busPaths.Count > 0) used.Add("triangle-start");
            var markup = GLYPH.Where(glyph => used.Has(glyph.Id)).Select(glyph =>
                $"          <marker id=\"cl-{glyph.Id}\" markerWidth=\"{N(glyph.Width)}\" markerHeight=\"{N(glyph.Height)}\" refX=\"{N(glyph.RefX)}\" refY=\"{N(glyph.RefY)}\" orient=\"{glyph.Orient ?? "auto"}\" markerUnits=\"userSpaceOnUse\" class=\"cl-marker\" stroke-width=\"1.5\" stroke-linejoin=\"round\">\n"
                + $"            {glyph.Body}\n"
                + "          </marker>").ToList();
            return markup.Count > 0 ? $"\n          <!-- UML relationship markers -->\n{string.Join("\n", markup)}" : "";
        }

        // Bus members share their trunk and bus, so each is drawn starting at the
        // supertype: the overlapping stretches then begin at one point and their dash
        // patterns coincide instead of beating into a smeared line.
        string RenderRelationshipPath(JsonObject relationship, int index)
        {
            var routed = PathFor(relationship);
            var reversed = busPaths.Has(relationship);
            var d = reversed ? Geometry.RoundedPath(routed.Points.AsEnumerable().Reverse().ToList(), 8) : routed.D;
            var bus = reversed ? $" data-class-bus=\"{Utils.Esc(relationship.Str("to"))}\" data-motion-path=\"{Utils.Esc(routed.D)}\"" : "";
            var kind = relationship.Str("kind");
            return $"        <path {SvgAttrs.FocusEdgeAttrs(S(relationship.Str("from")), S(relationship.Str("to")), relationship.Str("label"), N(index), relationship.Str("id"))} data-class-relationship=\"{S(kind)}\"{bus} data-composition-points=\"{Geometry.RoutePointsValue(routed.Points)}\" d=\"{d}\" class=\"{RelationshipClass(kind)}\"{SvgAttrs.AnimateAttr(meta, "edge", index)} stroke-width=\"1.5\"{MarkerAttrs(kind, reversed)}/>";
        }

        string RenderRelationshipLabel(JsonObject relationship, int index)
        {
            if (!relationship["label"].Truthy()) return "";
            var (lx, ly) = RelationshipLabelPoint(relationship);
            var box = RelationshipLabelBox(relationship);
            return $"        <g data-detail=\"context\" {SvgAttrs.FocusEdgeAttrs(S(relationship.Str("from")), S(relationship.Str("to")), relationship.Str("label"), N(index), relationship.Str("id"))} data-class-relationship=\"{S(relationship.Str("kind"))}\">\n"
                + $"          <rect x=\"{N(box.X)}\" y=\"{N(box.Y)}\" width=\"{N(box.Width)}\" height=\"{N(box.Height)}\" rx=\"{N(box.Height / 2)}\" class=\"c-mask cl-label-box\"/>\n"
                + $"          <text x=\"{N(lx)}\" y=\"{N(ly)}\" class=\"t-muted\" font-size=\"{N(RELATIONSHIP_LABEL_FONT)}\" text-anchor=\"middle\">{Utils.Esc(relationship.Str("label"))}</text>\n"
                + "        </g>";
        }

        // A swatch is a short relationship drawn in the same notation: dash, line, and
        // the marker at the end it belongs to.
        static string LegendSwatch(LegendEntry entry)
        {
            var y = entry.Baseline - 4;
            var x0 = entry.X + 2;
            var x1 = entry.X + LEGEND_SWATCH - 2;
            var hit = $"<rect class=\"cl-legend-hit\" tabindex=\"0\" role=\"img\" aria-label=\"{Utils.Esc(entry.Label)}\" x=\"{N(entry.X - 6)}\" y=\"{N(entry.Baseline - 15)}\" width=\"{N(entry.Width + 12)}\" height=\"22\" rx=\"11\"/>";
            return $"{hit}<path d=\"M {N(x0)} {N(y)} L {N(x1)} {N(y)}\" class=\"{RelationshipClass(entry.Kind)}\" stroke-width=\"1.5\"{MarkerAttrs(entry.Kind)}/>";
        }

        string RenderLegend()
        {
            var obstacles = Legend.RelationshipLegendObstacles(routable,
                pointsFor: (relationship, _) => PathFor(relationship).Points,
                labelRectFor: (relationship, _) => relationship["label"].Truthy() ? (Rect?)RelationshipLabelBox(relationship) : null);
            return Legend.RenderLegend(ctx, legendEntries, LegendLayoutFor(viewBox[0], viewBox[1], obstacles), LegendSwatch, locale);
        }

        public string RenderSvg()
        {
            // Like the ERD, an automatic canvas lets the reader scroll a tall model
            // instead of shrinking member rows past legibility.
            var readerFit = meta["viewBox"].Truthy() ? "" : " data-reader-fit=\"intrinsic-height\" data-reader-min-text=\"7.5\"";
            var hueRules = new JsSet<string>();
            foreach (var (_, tone) in KIND_TONE) hueRules.Add(tone);
            var sb = new StringBuilder();
            sb.Append($"      <svg viewBox=\"0 0 {N(viewBox[0])} {N(viewBox[1])}\" {SvgAttrs.SvgRootAttrs(ctx)} data-class-ui=\"\"{readerFit}>\n");
            sb.Append(SvgAttrs.SvgAccessibleText(ctx, "class")).Append('\n');
            sb.Append(Utils.RenderDefinitions(RenderMarkerDefs())).Append('\n');
            sb.Append("        <style>\n");
            sb.Append("          svg[data-class-ui] .cl-zebra { fill: color-mix(in srgb, var(--cl-hue) 6%, transparent); }\n");
            sb.Append("          svg[data-class-ui] .cl-param { fill: color-mix(in srgb, var(--text) 72%, var(--text-muted)); }\n");
            sb.Append(string.Join("\n", hueRules.Select(tone => $"          svg[data-class-ui] .cl-hue-{tone} {{ --cl-hue: var(--{tone}-stroke); }}"))).Append('\n');
            sb.Append("          svg[data-class-ui] .cl-type { fill: var(--mask); stroke: color-mix(in srgb, var(--cl-hue) 46%, var(--lane-stroke)); }\n");
            sb.Append("          svg[data-class-ui] [data-class-kind=\"abstract\"] .cl-type { stroke-dasharray: 5 3; stroke: color-mix(in srgb, var(--cl-hue) 64%, var(--lane-stroke)); }\n");
            sb.Append("          svg[data-class-ui] .cl-header { stroke: none; }\n");
            sb.Append("          svg[data-class-ui] .cl-cap { stroke: var(--cl-hue); pointer-events: none; }\n");
            sb.Append("          svg[data-class-ui] .cl-badge-chip { fill: var(--cl-hue); }\n");
            sb.Append("          svg[data-class-ui] .cl-badge-letter { fill: var(--mask); }\n");
            sb.Append("          svg[data-class-ui] .cl-stereotype { fill: var(--text-muted); letter-spacing: .04em; }\n");
            sb.Append("          svg[data-class-ui] .cl-rule { stroke: color-mix(in srgb, var(--cl-hue) 22%, var(--lane-stroke)); }\n");
            sb.Append("          svg[data-class-ui] .cl-vis { font-weight: 700; }\n");
            sb.Append("          svg[data-class-ui] .cl-vis-chip { fill: color-mix(in srgb, var(--text) 6%, transparent); }\n");
            sb.Append("          svg[data-class-ui] .cl-type-ref { fill: var(--text-muted); }\n");
            sb.Append("          svg[data-class-ui] .cl-punct { fill: var(--text-muted); opacity: .8; }\n");
            sb.Append("          svg[data-class-ui] .cl-label-box { stroke: var(--lane-stroke); stroke-width: .8; }\n");
            sb.Append("          svg[data-class-ui] .cl-member { font-family: 'JetBrains Mono', ui-monospace, SFMono-Regular, Menlo, Consolas, 'DejaVu Sans Mono', 'Liberation Mono', 'Noto Sans Mono CJK SC', 'PingFang SC', 'Microsoft YaHei', monospace; }\n");
            sb.Append("          svg[data-class-ui] .cl-dashed { stroke-dasharray: 6 4; }\n");
            sb.Append("          svg[data-class-ui] .cl-marker { stroke: var(--arrow); }\n");
            sb.Append("          svg[data-class-ui] .cl-marker-hollow { fill: var(--mask); }\n");
            sb.Append("          svg[data-class-ui] .cl-marker-filled { fill: var(--arrow); }\n");
            sb.Append("          svg[data-class-ui] .cl-legend-hit { fill: transparent; transition: fill .15s; }\n");
            sb.Append("          svg[data-class-ui] .cl-legend-hit:focus { outline: none; }\n");
            sb.Append("          svg[data-class-ui] .cl-legend-hit:focus-visible { stroke: var(--arrow); stroke-width: 1.5; }\n");
            sb.Append("          svg[data-class-ui] [data-legend-semantic-kind]:is(:hover, :focus-within) .cl-legend-hit { fill: var(--lane-fill); }\n");
            sb.Append("          svg[data-class-ui] [data-class-relationship] { transition: opacity .15s; }\n");
            sb.Append(string.Join("\n", NOTATION.Select(n => $"          svg[data-class-ui]:has([data-legend-semantic-kind=\"{n.Kind}\"]:is(:hover, :focus-within)) [data-class-relationship]:not([data-class-relationship=\"{n.Kind}\"]) {{ opacity: .15; }}"))).Append('\n');
            sb.Append("          @media (prefers-reduced-motion: reduce) { svg[data-class-ui] * { transition: none !important; } }\n");
            sb.Append("        </style>\n");
            sb.Append('\n');
            sb.Append("        <!-- Background Grid -->\n");
            sb.Append("        <rect width=\"100%\" height=\"100%\" fill=\"url(#grid)\" />\n");
            sb.Append('\n');
            sb.Append("        <!-- Relationships (before types for correct z-order) -->\n");
            sb.Append(string.Join("\n", relationships.Select((relationship, index) => RenderableRelationship(relationship) ? RenderRelationshipPath(relationship, index) : "").Where(Truthy))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Types -->\n");
            sb.Append(string.Join("\n\n", types.Values.Select(RenderType))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Relationship labels -->\n");
            sb.Append(string.Join("\n", relationships.Select((relationship, index) => RenderableRelationship(relationship) ? RenderRelationshipLabel(relationship, index) : "").Where(Truthy))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Legend -->\n");
            sb.Append(RenderLegend()).Append('\n');
            sb.Append("      </svg>");
            return sb.ToString();
        }
    }
}
