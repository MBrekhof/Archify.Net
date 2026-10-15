using System.Text.Json;
using System.Text.Json.Nodes;

namespace Archify.Net;

// render-erd.mjs. The module-level flow (line 57 to the final writeDiagram call) becomes one Scene built per render;
// `--layout-json` becomes the LayoutReport on the result and writeDiagram's HTML is returned by the facade.
public sealed class ErdRenderer : IDiagramRenderer
{
    public (string Svg, JsonArray? Cards, LayoutReport? Layout) Render(RenderContext ctx)
    {
        var scene = new Scene(ctx);
        scene.ValidateEr();
        var svg = scene.RenderSvg();
        return (svg, ctx.Diagram.Arr("cards"), new LayoutReport(scene.BuildLayoutReport()));
    }

    // The `--layout-json` branch (:1553-1557): validation, then the report, and no SVG.
    internal static JsonObject BuildLayoutReport(RenderContext ctx)
    {
        var scene = new Scene(ctx);
        scene.ValidateEr();
        return scene.BuildLayoutReport();
    }

    // `layout` (:69-82); the report prints it in this key order.
    static class Layout
    {
        public const double Margin = 24;
        public const double LegendH = 34;
        public const double PadX = 12;
        public const double KeyWidth = 30;
        public const double KeyFont = 9;
        public const double HeaderFont = 13;
        public const double HeaderFontMinimum = 10;
        public const double RowFont = 11;
        public const double RowFontMinimum = 9;
        public const double TypeFont = 10.5;
        public const double TypeFontMinimum = 9;
        public const double MinRelationshipLength = 24;

        public static JsonObject ToJson() => new()
        {
            ["margin"] = Margin, ["legendH"] = LegendH, ["padX"] = PadX, ["keyWidth"] = KeyWidth, ["keyFont"] = KeyFont,
            ["headerFont"] = HeaderFont, ["headerFontMinimum"] = HeaderFontMinimum, ["rowFont"] = RowFont, ["rowFontMinimum"] = RowFontMinimum,
            ["typeFont"] = TypeFont, ["typeFontMinimum"] = TypeFontMinimum, ["minRelationshipLength"] = MinRelationshipLength,
        };
    }

    // ---- Key badges (:84-129) ------------------------------------------------------
    // KEY_ACCENT[role] || 't-muted'.
    static string? KeyAccent(string role) => role switch { "pk" => "t-database", "fk" => "t-messagebus", "uk" => "t-muted", _ => null };
    const double KEY_BADGE_W = 22;
    const double KEY_BADGE_GAP = 3;
    const double KEY_BADGE_INSET = 2;
    const double KEY_ROLE_GAP = 5;

    // A role or a list of roles; a role named twice still draws once (`[...new Set(roles)]`).
    static List<string> KeyRoles(JsonNode? attribute)
    {
        var key = attribute is JsonObject a ? a["key"] : null;
        IEnumerable<string> roles = key is JsonValue v && v.GetValueKind() == JsonValueKind.String
            ? [v.GetValue<string>()]
            : key.AsArrayOrEmpty().Select(BrandMarks.JsString);
        var unique = new JsSet<string>();
        foreach (var role in roles) unique.Add(role);
        return [.. unique];
    }

    static double KeyGlyphWidth(List<string> roles) =>
        roles.Count == 0 ? 0 : roles.Count * KEY_BADGE_W + (roles.Count - 1) * KEY_BADGE_GAP - KEY_BADGE_INSET;

    static double KeyColumnWidth(JsonObject entity)
    {
        double widest = 0;
        foreach (var attribute in entity["attributes"].AsArrayOrEmpty()) widest = Math.Max(widest, KeyGlyphWidth(KeyRoles(attribute)));
        return Math.Max(Layout.KeyWidth, widest != 0 ? widest + KEY_ROLE_GAP : 0);
    }

    static string RenderKeyBadge(string role, double x, double baseline) =>
        $"<rect x=\"{N(x)}\" y=\"{N(baseline - 10)}\" width=\"{N(KEY_BADGE_W)}\" height=\"13\" rx=\"3\" class=\"er-key-badge\" data-er-badge=\"{Utils.Esc(role)}\"/><text data-detail=\"context\" x=\"{N(x + KEY_BADGE_W / 2)}\" y=\"{N(baseline)}\" class=\"{KeyAccent(role) ?? "t-muted"}\" font-size=\"{N(Layout.KeyFont)}\" font-weight=\"600\">{Utils.Esc(role.ToUpperInvariant())}</text>";

    static string RenderKeyGlyphs(JsonNode? attribute, double x0, double baseline) =>
        string.Concat(KeyRoles(attribute).Select((role, index) => RenderKeyBadge(role, x0 - KEY_BADGE_INSET + index * (KEY_BADGE_W + KEY_BADGE_GAP), baseline)));

    // ---- Relationship labels (:154-169) --------------------------------------------
    const double RELATIONSHIP_LABEL_FONT = 9;
    const double RELATIONSHIP_LABEL_ADVANCE = 0.62;
    static double RelationshipLabelWidth(string? label) => Math.Max(30, Utils.TextUnits(label) * RELATIONSHIP_LABEL_FONT * RELATIONSHIP_LABEL_ADVANCE + 10);
    const double LABEL_GLYPH_CLEARANCE = 24 + 4;

    // ---- Domain bands (:211-307) ---------------------------------------------------
    const double DOMAIN_PAD_X = 12;
    const double DOMAIN_PAD_TOP = 18;
    const double DOMAIN_PAD_BOTTOM = 8;
    static readonly string[] DOMAIN_HUES = ["database", "frontend", "backend", "cloud", "messagebus", "security"];

    sealed record DomainGroup(string Tag, List<Box> Members);
    sealed record UnlabelableDomain(string Tag, List<Box> Members, string Reason, List<string> OffGrid);

    // ---- Cardinality (:378-384) ----------------------------------------------------
    const double MARKER_GLYPH_WIDTH = 24;
    const double MARKER_TOE_X = 22;
    const double MARKER_APEX_X = 8;
    const double MARKER_CIRCLE_X = 3.5;
    const double MARKER_HEIGHT = 14;

    // ---- Legend (:436-466) ---------------------------------------------------------
    static readonly string[] LEGEND_KINDS = ["pk", "fk", "uk", "one", "many", "optional"];
    static readonly HashSet<string> KEY_KINDS = new(StringComparer.Ordinal) { "pk", "fk", "uk" };   // lookup only
    const double LEGEND_BASELINE_GAP = 16;
    const double LEGEND_FONT_SIZE = 10;
    const double LEGEND_KEY_SWATCH = 22;
    const double LEGEND_END_SWATCH = 30;
    const double LEGEND_GROUP_LEAD = 10;

    // ---- Routing, lanes and trunks (:533-642) --------------------------------------
    const double LANE_STEP = 12;
    const double ERD_PORT_SPACING = 24;
    static readonly double[] TRUNK_OFFSETS = [16, 22, 12, 28];
    const double TRUNK_SNAP = LANE_STEP;
    const double TRUNK_BRIDGE = ERD_PORT_SPACING + 8;
    const double TRUNK_CAP = 10;
    const double TRUNK_LEG = 12;
    const double GLYPH_HALF_HEIGHT = 10;

    // `{ role, entity, side, styleKey, members }` (:691-697); axis and coordinate are written by the trunk pass (:713, :747),
    // and a coordinate left undefined stays null.
    sealed class TrunkGroup(string role, Box entity, string side, string styleKey, List<JsonObject> members)
    {
        public string Role { get; } = role;
        public Box Entity { get; } = entity;
        public string Side { get; } = side;
        public string StyleKey { get; } = styleKey;
        public List<JsonObject> Members { get; } = members;
        public int Axis { get; set; }
        public double? Coordinate { get; set; }
    }
    sealed record ChosenLine(int Axis, double Coordinate, string StyleKey);
    sealed class TrunkRun { public int StartIndex, EndIndex; public double Low, High; }
    sealed class Span(double low, double high, string styleKey)
    {
        public double Low { get; } = low;
        public double High { get; set; } = high;
        public string StyleKey { get; } = styleKey;
        public Span Copy() => new(Low, High, StyleKey);
    }

    static string N(double v) => Js.Num(v);
    static string S(string? s) => s ?? "undefined";
    static bool Truthy(string? s) => !string.IsNullOrEmpty(s);
    static bool IsInteger(double? v) => v is { } d && double.IsInteger(d);
    static double Coord(Pt p, int axis) => axis == 0 ? p.X : p.Y;
    // A JS array element in arithmetic: anything but a number reads as NaN (undefined, or a non-number).
    static double Elem(JsonArray a, int i) => i < a.Count && a[i] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.D() : double.NaN;

    static string CardinalityOf(JsonObject relationship, string endpoint)
    {
        var value = endpoint == "from" ? relationship.Str("fromCardinality") : relationship.Str("toCardinality");
        if (value is "one" or "many") return value;
        return endpoint == "from" ? "many" : "one";
    }

    static bool OptionalOf(JsonObject relationship, string endpoint) =>
        relationship.Bool(endpoint == "from" ? "fromOptional" : "toOptional") == true;

    // `identifying: false` owns the dash; otherwise `variant ?? 'default'` (an empty string stays empty).
    static string MarkerStyleOf(JsonObject relationship)
    {
        if (relationship.Bool("identifying") == false) return "dashed";
        return relationship.Str("variant") ?? "default";
    }

    static string CardinalityMarkerId(JsonObject relationship, string endpoint) =>
        $"er-{CardinalityOf(relationship, endpoint)}{(OptionalOf(relationship, endpoint) ? "-optional" : "")}-{(endpoint == "from" ? "start" : "end")}";

    static List<string> CardinalityGlyphParts(string? cardinality, bool optional, bool mirror = false)
    {
        double Flip(double x) => mirror ? MARKER_TOE_X - x : x;
        var parts = new List<string>();
        if (cardinality == "many")
            parts.Add($"<path d=\"M {N(Flip(MARKER_APEX_X))} 8 L {N(Flip(MARKER_TOE_X))} 1 M {N(Flip(MARKER_APEX_X))} 8 L {N(Flip(MARKER_TOE_X))} 8 M {N(Flip(MARKER_APEX_X))} 8 L {N(Flip(MARKER_TOE_X))} 15\"/>");
        else if (cardinality == "one")
            parts.Add($"<path d=\"M {N(Flip(17))} 1 L {N(Flip(17))} 15\"/>");
        if (optional)
        {
            parts.Add($"<circle cx=\"{N(Flip(MARKER_CIRCLE_X))}\" cy=\"8\" r=\"2.6\" class=\"c-mask\"/>");
            parts.Add($"<circle cx=\"{N(Flip(MARKER_CIRCLE_X))}\" cy=\"8\" r=\"2.6\"/>");
        }
        return parts;
    }

    static string CardinalityMarkerMarkup(string id, string cardinality, bool optional, bool mirror)
    {
        var refX = mirror ? 0 : MARKER_TOE_X;
        return $"          <marker id=\"{Utils.Esc(id)}\" markerWidth=\"{N(MARKER_GLYPH_WIDTH)}\" markerHeight=\"18\" refX=\"{N(refX)}\" refY=\"8\" orient=\"auto\" markerUnits=\"userSpaceOnUse\" class=\"a-default\" style=\"stroke: var(--text-muted)\" stroke-width=\"1.75\">\n"
            + $"            {string.Join("\n            ", CardinalityGlyphParts(cardinality, optional, mirror))}\n"
            + "          </marker>";
    }

    static bool PlacedByGrid(Box entity) =>
        IsInteger(entity.Source.Num("row")) && IsInteger(entity.Source.Num("col")) && entity.Source["pos"] is not JsonArray { Count: 2 };

    static string CellKey(Box entity) => $"{N(entity.Source.Num("row") ?? double.NaN)}:{N(entity.Source.Num("col") ?? double.NaN)}";

    // Every cell of the members' bounding box holds one of them (:219-226).
    static bool GridRunIsContiguous(List<Box> members)
    {
        var cells = new JsSet<string>();
        foreach (var entity in members) cells.Add(CellKey(entity));
        if (cells.Count != members.Count) return false;
        double minCol = double.PositiveInfinity, maxCol = double.NegativeInfinity, minRow = double.PositiveInfinity, maxRow = double.NegativeInfinity;
        foreach (var entity in members)
        {
            var col = entity.Source.Num("col")!.Value;
            var row = entity.Source.Num("row")!.Value;
            minCol = Math.Min(minCol, col); maxCol = Math.Max(maxCol, col);
            minRow = Math.Min(minRow, row); maxRow = Math.Max(maxRow, row);
        }
        return cells.Count == (maxCol - minCol + 1) * (maxRow - minRow + 1);
    }

    static Rect DomainBandBox(List<Box> members)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var entity in members)
        {
            minX = Math.Min(minX, entity.X);
            minY = Math.Min(minY, entity.Y);
            maxX = Math.Max(maxX, entity.X + entity.Width);
            maxY = Math.Max(maxY, entity.Y + entity.Height);
        }
        return new Rect(minX - DOMAIN_PAD_X, minY - DOMAIN_PAD_TOP, maxX - minX + DOMAIN_PAD_X * 2, maxY - minY + DOMAIN_PAD_TOP + DOMAIN_PAD_BOTTOM);
    }

    static Pt DomainBandCaption(Rect box) => new(box.X + 10, box.Y + 13);

    static bool IsHorizontalSide(string? side) => side is "left" or "right";

    static Pt SideAnchor(Box entity, string side) => side switch
    {
        "left" => new(entity.X, entity.Cy),
        "right" => new(entity.X + entity.Width, entity.Cy),
        "top" => new(entity.Cx, entity.Y),
        _ => new(entity.Cx, entity.Y + entity.Height),
    };

    static readonly JsonObject NoSource = new();

    static Box CardinalityGlyphBox(Pt point, string side)
    {
        var (x, y) = point;
        const double reach = MARKER_GLYPH_WIDTH;
        var (bx, by, bw, bh) = side switch
        {
            "left" => (x - reach, y - GLYPH_HALF_HEIGHT, reach, GLYPH_HALF_HEIGHT * 2),
            "right" => (x, y - GLYPH_HALF_HEIGHT, reach, GLYPH_HALF_HEIGHT * 2),
            "top" => (x - GLYPH_HALF_HEIGHT, y - reach, GLYPH_HALF_HEIGHT * 2, reach),
            _ => (x - GLYPH_HALF_HEIGHT, y, GLYPH_HALF_HEIGHT * 2, reach),
        };
        return new Box { Id = "", Source = NoSource, X = bx, Y = by, Width = bw, Height = bh };
    }

    // `channelFallback` (:1002-1011): its low/high are never read.
    static (int Axis, Func<double, List<Pt>> ToPoints)? ChannelFallback(Pt start, Pt end, string fromSide)
    {
        var horizontalPorts = fromSide is "left" or "right";
        var verticalPorts = fromSide is "top" or "bottom";
        if (horizontalPorts == verticalPorts) return null;
        return (horizontalPorts ? 0 : 1, channel => horizontalPorts
            ? [new(channel, start.Y), new(channel, end.Y)]
            : [new(start.X, channel), new(end.X, channel)]);
    }

    sealed class Scene
    {
        readonly RenderContext ctx;
        readonly JsonObject er;
        readonly JsonObject meta;
        readonly string? locale;
        readonly string? gateProfile;
        readonly ErGrid? grid;
        readonly ErGrid metrics;
        readonly Bands? bands;
        readonly JsMap<string, Box> entities = new();
        readonly List<JsonObject> relationships;
        readonly List<DomainGroup> domainGroups = [];
        readonly List<UnlabelableDomain> unlabelableDomains = [];
        readonly JsMap<string, string> domainHue = new();
        readonly JsMap<string, double> entitySteps = new();
        readonly List<LegendEntry> legendEntries;
        readonly double[] viewBox;
        readonly Router router;
        // Read lazily by preferredCandidates during route planning (:565), so they are fields, never locals.
        readonly JsMap<JsonObject, double> laneOffsets = new();
        readonly List<TrunkGroup> trunkGroups = [];
        readonly JsMap<JsonObject, TrunkGroup> trunkAssignments = new();
        readonly JsMap<JsonObject, List<TrunkRun>> trunkRunCache = new();
        readonly JsMap<JsonObject, Pt> resolvedLabelPoints = new();

        Box? EntityOf(string? id) => id is null ? null : entities.Get(id);
        double EntityWidth(JsonObject entity) => ErdGrid.ResolvedEntityWidth(GridEntity.From(entity, 0), grid);

        public Scene(RenderContext ctx)
        {
            this.ctx = ctx;
            er = ctx.Diagram;
            meta = ctx.Meta!;
            locale = meta.Str("locale");
            gateProfile = ctx.Options.Quality?.Name() ?? (Truthy(meta.Str("quality_profile")) ? meta.Str("quality_profile") : null);

            grid = ErdGrid.ErGridLayout(er);
            metrics = grid ?? ErdGrid.DefaultErGrid;

            // ---- Measure entities from the banded grid (:140-198) ----
            var measured = er["entities"].AsArrayOrEmpty().Select(node =>
            {
                var entity = (JsonObject)node!;
                return GridEntity.From(entity, ErdGrid.EntityHeight(entity, grid ?? ErdGrid.DefaultErGrid), EntityWidth(entity));
            }).ToList();
            var sources = er["entities"].AsArrayOrEmpty().Select(node => (JsonObject)node!).ToList();
            bands = grid is not null ? ErdGrid.BandedLayout(measured, grid, LabelColumnGaps()) : null;
            // A duplicate id keeps its first position with the last value, as the Map constructor does.
            for (var index = 0; index < measured.Count; index++)
            {
                var entity = measured[index];
                var (x, y) = ErdGrid.ResolveEntityPos(entity, grid, bands);
                entities.Set(S(entity.Id), new Box { Id = S(entity.Id), Source = sources[index], X = x, Y = y, Width = entity.Width!.Value, Height = entity.Height });
            }

            relationships = er["relationships"].AsArrayOrEmpty().Select(node => (JsonObject)node!).ToList();

            // ---- Domain bands (:237-307) ----
            {
                var byTag = new JsMap<string, List<Box>>();
                foreach (var entity in entities.Values)
                {
                    if (!entity.Source["tag"].Truthy()) continue;
                    var tag = BrandMarks.JsString(entity.Source["tag"]);
                    if (!byTag.Has(tag)) byTag.Set(tag, []);
                    byTag.Get(tag)!.Add(entity);
                }
                foreach (var (tag, members) in byTag.Entries)
                {
                    if (!members.All(PlacedByGrid)) continue;
                    if (!GridRunIsContiguous(members)) continue;
                    domainGroups.Add(new DomainGroup(tag, members));
                }
            }
            {
                var banded = new JsSet<string>();
                foreach (var group in domainGroups) banded.Add(group.Tag);
                var byTag = new JsMap<string, List<Box>>();
                foreach (var entity in entities.Values)
                {
                    if (!entity.Source["tag"].Truthy()) continue;
                    var tag = BrandMarks.JsString(entity.Source["tag"]);
                    if (banded.Has(tag)) continue;
                    if (!byTag.Has(tag)) byTag.Set(tag, []);
                    byTag.Get(tag)!.Add(entity);
                }
                foreach (var (tag, members) in byTag.Entries)
                {
                    var gridPlaced = members.All(PlacedByGrid);
                    var cells = new JsSet<string>();
                    foreach (var entity in members) cells.Add(CellKey(entity));
                    var sharedCell = gridPlaced && cells.Count != members.Count;
                    unlabelableDomains.Add(new UnlabelableDomain(tag, members, sharedCell ? "shared-cell" : gridPlaced ? "scattered" : "no-grid",
                        members.Where(entity => !PlacedByGrid(entity)).Select(entity => entity.Id).ToList()));
                }
            }
            foreach (var entity in entities.Values)
            {
                if (!entity.Source["tag"].Truthy()) continue;
                var tag = BrandMarks.JsString(entity.Source["tag"]);
                if (!domainHue.Has(tag)) domainHue.Set(tag, DOMAIN_HUES[domainHue.Count % DOMAIN_HUES.Length]);
            }

            for (var index = 0; index < relationships.Count; index++)
            {
                var relationship = relationships[index];
                if (relationship.Str("from") is { } from && !entitySteps.Has(from)) entitySteps.Set(from, index);
                if (relationship.Str("to") is { } to && !entitySteps.Has(to)) entitySteps.Set(to, index + 1);
            }
            for (var index = 0; index < sources.Count; index++)
            {
                if (sources[index].Str("id") is { } id && !entitySteps.Has(id)) entitySteps.Set(id, index);
            }

            // ---- Legend (:436-480) ----
            var catalog = LEGEND_KINDS.Select(kind => new LegendEntry(kind, ctx.Locale.Translate(locale, $"legend.erd.{kind}"), Interactive: false)).ToList();
            var presentKinds = new JsSet<string>();
            foreach (var entity in entities.Values)
                foreach (var attribute in entity.Source["attributes"].AsArrayOrEmpty())
                    foreach (var role in KeyRoles(attribute)) presentKinds.Add(role);
            foreach (var relationship in relationships)
                foreach (var kind in RelationshipEnds(relationship)) presentKinds.Add(kind);
            var resolved = Legend.ResolveLegend(meta.Obj("legend"), catalog, presentKinds);
            legendEntries = resolved.Select((entry, index) =>
            {
                var isKey = KEY_KINDS.Contains(entry.Kind);
                var lead = !isKey && index > 0 && KEY_KINDS.Contains(resolved[index - 1].Kind) ? LEGEND_GROUP_LEAD : 0;
                return entry with
                {
                    Lead = lead,
                    SwatchWidth = (isKey ? LEGEND_KEY_SWATCH : LEGEND_END_SWATCH) + lead,
                    TrailingWidth = Math.Ceiling(Utils.TextUnits(entry.Label) * 2 * 0.62),
                };
            }).ToList();

            viewBox = meta["viewBox"] is JsonArray authored ? [Elem(authored, 0), Elem(authored, 1)] : AutoViewBox();

            // ---- Routing (:552-589) ----
            router = Routing.CreateRouter(entities, relationships, new RouterOptions(
                // Horizontal-first, like every other non-architecture type.
                SideFor: SideFor,
                PreferredCandidates: (conn, _, _, start, end, fromSide, toSide) =>
                    [.. TrunkCandidates(conn, start, end, fromSide, toSide), .. LaneCandidates(conn, start, end, fromSide)],
                // A bundled trunk is drawn as one bus path, so the router's rhythm floors do not describe what this renderer draws.
                CompositionFloors: false,
                MaxPortSpacing: ERD_PORT_SPACING,
                PreferReadableRoutes: true,
                CrossingFreeGridFirst: true,
                ComponentGapPx: 10,
                // A 6-unit corner radius plus the 8-unit micro-segment floor.
                MinimumTerminalSegmentPx: 14));

            // ---- Lanes (:605-626) ----
            {
                var groups = new JsMap<string, List<JsonObject>>();
                foreach (var relationship in relationships)
                {
                    var key = CorridorKey(relationship);
                    if (key is null) continue;
                    var list = groups.Get(key) ?? [];
                    list.Add(relationship);
                    groups.Set(key, list);
                }
                foreach (var list in groups.Values)
                {
                    if (list.Count < 2) continue;
                    Js.Sort(list, (left, right) =>
                    {
                        var compared = string.CompareOrdinal(LaneSortKey(left), LaneSortKey(right));
                        return compared < 0 ? -1 : compared > 0 ? 1 : 0;
                    });
                    for (var index = 0; index < list.Count; index++) laneOffsets.Set(list[index], index - (list.Count - 1) / 2.0);
                }
            }

            // ---- Bundled fan-in / fan-out trunks (:662-756) ----
            {
                var fanIn = new JsMap<string, List<(int Index, JsonObject Relationship)>>();
                var fanOut = new JsMap<string, List<(int Index, JsonObject Relationship)>>();
                var resolvedSides = new JsMap<JsonObject, Sides>();
                static void Push(JsMap<string, List<(int, JsonObject)>> map, string key, (int, JsonObject) entry)
                {
                    var list = map.Get(key) ?? [];
                    list.Add(entry);
                    map.Set(key, list);
                }
                for (var index = 0; index < relationships.Count; index++)
                {
                    var relationship = relationships[index];
                    if (!RelationshipAutoRouted(relationship)) continue;
                    var sides = router.InferredSides(relationship);
                    resolvedSides.Set(relationship, sides);
                    var styleKey = MarkerStyleOf(relationship);
                    Push(fanIn, $"{S(relationship.Str("to"))}\0{sides.ToSide}\0{styleKey}", (index, relationship));
                    Push(fanOut, $"{S(relationship.Str("from"))}\0{sides.FromSide}\0{styleKey}", (index, relationship));
                }
                var taken = new JsSet<JsonObject>();
                void BuildGroups(JsMap<string, List<(int Index, JsonObject Relationship)>> map, string role)
                {
                    var grouped = map.Values.Where(list => list.Count >= 2).ToList();
                    Js.Sort(grouped, (left, right) => left[0].Index - right[0].Index);
                    foreach (var list in grouped)
                    {
                        var members = list.Where(entry => !taken.Has(entry.Relationship)).ToList();
                        if (members.Count < 2) continue;
                        var head = members[0].Relationship;
                        var sides = resolvedSides.Get(head)!;
                        var group = new TrunkGroup(role, EntityOf(role == "in" ? head.Str("to") : head.Str("from"))!,
                            role == "in" ? sides.ToSide : sides.FromSide, MarkerStyleOf(head), members.Select(entry => entry.Relationship).ToList());
                        foreach (var entry in members)
                        {
                            taken.Add(entry.Relationship);
                            trunkAssignments.Set(entry.Relationship, group);
                        }
                        trunkGroups.Add(group);
                    }
                }
                BuildGroups(fanIn, "in");
                BuildGroups(fanOut, "out");

                // One trunk coordinate per group, just outside the shared side; a coordinate close to an already chosen
                // trunk snaps onto it.
                var chosenLines = new List<ChosenLine>();
                foreach (var group in trunkGroups)
                {
                    group.Axis = IsHorizontalSide(group.Side) ? 0 : 1;
                    var portAxis = group.Axis == 0 ? 1 : 0;
                    double lowest = double.PositiveInfinity, highest = double.NegativeInfinity;
                    foreach (var relationship in group.Members)
                    {
                        var spread = router.Ports.Get(relationship);
                        var port = group.Role == "in" ? spread?.To : spread?.From;
                        var coordinate = Coord(port ?? SideAnchor(group.Entity, group.Side), portAxis);
                        lowest = Math.Min(lowest, coordinate);
                        highest = Math.Max(highest, coordinate);
                    }
                    var low = lowest - TRUNK_CAP;
                    var high = highest + TRUNK_CAP;
                    bool ClearsSpan(double coordinate)
                    {
                        var segment = group.Axis == 0
                            ? new Segment(new(coordinate, low), new(coordinate, high))
                            : new Segment(new(low, coordinate), new(high, coordinate));
                        return entities.Values.All(entity => !Geometry.SegmentIntersectsRect(segment, entity));
                    }
                    var @base = IsHorizontalSide(group.Side)
                        ? (group.Side == "left" ? group.Entity.X : group.Entity.X + group.Entity.Width)
                        : (group.Side == "top" ? group.Entity.Y : group.Entity.Y + group.Entity.Height);
                    var outward = group.Side is "left" or "top" ? -1 : 1;
                    foreach (var offset in TRUNK_OFFSETS)
                    {
                        var candidate = @base + outward * offset;
                        if (!ClearsSpan(candidate)) continue;
                        var near = chosenLines.FirstOrDefault(line => line.Axis == group.Axis
                            && line.StyleKey == group.StyleKey
                            && Math.Abs(line.Coordinate - candidate) < TRUNK_SNAP);
                        if (near is not null && !ClearsSpan(near.Coordinate)) continue;
                        var clashing = chosenLines.Any(line => line.Axis == group.Axis
                            && line.StyleKey != group.StyleKey
                            && Math.Abs(line.Coordinate - candidate) < TRUNK_SNAP);
                        if (clashing) continue;
                        group.Coordinate = near is not null ? near.Coordinate : candidate;
                        break;
                    }
                    if (group.Coordinate is null)
                    {
                        // No re-add: the members stay in `taken`, so they join no later group.
                        foreach (var relationship in group.Members) trunkAssignments.Delete(relationship);
                        continue;
                    }
                    chosenLines.Add(new ChosenLine(group.Axis, group.Coordinate.Value, group.StyleKey));
                }
            }

            // ---- Automatic label placement (:946-1000); route planning first runs here ----
            {
                var placeable = relationships.Where(RenderableRelationship).ToList();
                var labels = placeable.Where(relationship => relationship["label"].Truthy()).Select(relationship =>
                {
                    var (lx, ly) = DefaultErLabelPoint(relationship);
                    var width = RelationshipLabelWidth(relationship.Str("label"));
                    return new LabelRect
                    {
                        Relation = relationship, RelationIndex = relationships.IndexOf(relationship), Label = relationship.Str("label"),
                        X = lx - width / 2, Y = ly - 10, Width = width, Height = 14, Lx = lx, Ly = ly,
                    };
                }).ToList();
                if (labels.Count > 0)
                {
                    var routes = new List<LabelRoute>();
                    var glyphs = new List<Box>();
                    foreach (var relationship in placeable)
                    {
                        var relationIndex = relationships.IndexOf(relationship);
                        var points = router.PathFor(relationship).Points;
                        glyphs.Add(CardinalityGlyphBox(points[0], router.ConnectionEndpointSide(relationship, "source")));
                        glyphs.Add(CardinalityGlyphBox(points[^1], router.ConnectionEndpointSide(relationship, "target")));
                        var runs = RelationshipTrunkRuns(relationship);
                        if (runs.Count == 0)
                        {
                            routes.Add(new LabelRoute(relationIndex, points));
                            continue;
                        }
                        var inRun = new JsSet<int>();
                        foreach (var run in runs)
                            for (var step = 0; step < run.EndIndex - run.StartIndex; step++) inRun.Add(run.StartIndex + step);
                        var piece = new List<Pt> { points[0] };
                        for (var index = 0; index < points.Count - 1; index += 1)
                        {
                            if (inRun.Has(index))
                            {
                                if (piece.Count > 1) routes.Add(new LabelRoute(relationIndex, piece));
                                routes.Add(new LabelRoute(-1 - routes.Count, [points[index], points[index + 1]]));
                                piece = [points[index + 1]];
                            }
                            else
                            {
                                piece.Add(points[index + 1]);
                            }
                        }
                        if (piece.Count > 1) routes.Add(new LabelRoute(relationIndex, piece));
                    }
                    var titles = domainGroups.Select(group =>
                    {
                        var caption = DomainBandCaption(DomainBandBox(group.Members));
                        return new Rect(caption.X - 2, caption.Y - 10, Utils.TextUnits(group.Tag) * 9 * 0.65 + 4, 13);
                    }).ToList();
                    var footprint = Legend.LegendFootprint(legendEntries, Math.Max(1, viewBox[0] - Layout.Margin * 2));
                    var placed = AutomaticLabels.PlaceAutomaticLabels(
                        labels: labels,
                        routes: routes,
                        components: [.. entities.Values, .. glyphs],
                        titles: titles,
                        viewBox: viewBox,
                        // Subtracted even when the legend is empty (class does not).
                        placementBottom: viewBox[1] - Layout.LegendH - footprint.ExtraHeight,
                        keepFallbackNearRoute: true);
                    for (var index = 0; index < placed.Count; index++)
                    {
                        var rect = placed[index];
                        if (!ReferenceEquals(rect, labels[index])) resolvedLabelPoints.Set(rect.Relation, new Pt(rect.Lx!.Value, rect.Ly!.Value));
                    }
                }
            }
        }

        // ---- Measure (:170-191) --------------------------------------------------------
        // Each gap between two neighbouring columns widens just enough for its widest label to clear both glyphs, when
        // the grid owns every coordinate.
        JsMap<double, double>? LabelColumnGaps()
        {
            if (grid is null || meta["viewBox"] is JsonArray) return null;
            var rawEntities = er["entities"].AsArrayOrEmpty().Select(node => (JsonObject)node!).ToList();
            var rawRelationships = er["relationships"].AsArrayOrEmpty().Select(node => (JsonObject)node!).ToList();
            var absolute = rawEntities.Any(entity => entity["pos"] is JsonArray)
                || rawRelationships.Any(relationship => relationship["via"] is JsonArray { Count: > 0 } || relationship["labelAt"].Truthy()
                    || (relationship["route"].Truthy() && relationship.Str("route") != "auto")
                    // ponytail: schema-unreachable (no channelX/channelY on a relationship); kept as the `!== undefined` read.
                    || relationship.ContainsKey("channelX") || relationship.ContainsKey("channelY"));
            if (absolute) return null;
            var byId = new JsMap<string, JsonObject>();
            foreach (var entity in rawEntities) byId.Set(S(entity.Str("id")), entity);
            var gaps = new JsMap<double, double>();
            foreach (var relationship in rawRelationships)
            {
                if (!relationship["label"].Truthy()) continue;
                var from = relationship.Str("from") is { } fromId ? byId.Get(fromId) : null;
                var to = relationship.Str("to") is { } toId ? byId.Get(toId) : null;
                var fromCol = from?.Num("col");
                var toCol = to?.Num("col");
                if (!IsInteger(fromCol) || !IsInteger(toCol) || Math.Abs(fromCol!.Value - toCol!.Value) != 1) continue;
                var gap = Math.Min(fromCol.Value, toCol.Value);
                var demand = Math.Ceiling(RelationshipLabelWidth(relationship.Str("label")) + LABEL_GLYPH_CLEARANCE * 2);
                var current = gaps.Get(gap);   // `gaps.get(gap) || 0`: a missing key reads 0
                gaps.Set(gap, Math.Max(current, demand));
            }
            return gaps.Count > 0 ? gaps : null;
        }

        // ---- Domain bands (:309-322) ---------------------------------------------------
        string HueClassOf(string? tag) => $"er-hue-{(Truthy(tag) && domainHue.Has(tag!) ? domainHue.Get(tag!) : "database")}";

        string RenderDomainBands()
        {
            if (domainGroups.Count == 0) return "";
            return string.Join("\n", domainGroups.Select(group =>
            {
                var box = DomainBandBox(group.Members);
                var caption = DomainBandCaption(box);
                var pillWidth = Math.Ceiling(Utils.TextUnits(group.Tag) * (9 * NodeTextFit.WidthFactor + 0.8) + 14);
                return $"        <g data-domain-band=\"{Utils.Esc(group.Tag)}\" class=\"{HueClassOf(group.Tag)}\">\n"
                    + $"          <rect x=\"{N(box.X)}\" y=\"{N(box.Y)}\" width=\"{N(box.Width)}\" height=\"{N(box.Height)}\" rx=\"12\" class=\"c-lane er-band\" stroke-width=\"1\"/>\n"
                    + $"          <rect x=\"{N(caption.X - 2)}\" y=\"{N(box.Y - 7)}\" width=\"{N(pillWidth)}\" height=\"15\" rx=\"7.5\" class=\"c-mask\"/>\n"
                    + $"          <rect x=\"{N(caption.X - 2)}\" y=\"{N(box.Y - 7)}\" width=\"{N(pillWidth)}\" height=\"15\" rx=\"7.5\" class=\"er-band-pill\"/>\n"
                    + $"          <text data-domain-label=\"\" x=\"{N(caption.X + 5)}\" y=\"{N(box.Y + 3.6)}\" class=\"t-muted er-band-label\" font-size=\"9\" font-weight=\"700\">{Utils.Esc(group.Tag)}</text>\n"
                    + "        </g>";
            }));
        }

        // ---- Cardinality (:412-430) ----------------------------------------------------
        string RenderCardinalityDefs()
        {
            static string Flag(bool optional) => optional ? "true" : "false";
            var used = new JsSet<string>();
            foreach (var relationship in relationships)
            {
                used.Add($"{CardinalityOf(relationship, "from")}|{Flag(OptionalOf(relationship, "from"))}|start");
                used.Add($"{CardinalityOf(relationship, "to")}|{Flag(OptionalOf(relationship, "to"))}|end");
            }
            var markup = new List<string>();
            foreach (var cardinality in new[] { "one", "many" })
                foreach (var optional in new[] { false, true })
                    foreach (var end in new[] { "start", "end" })
                    {
                        if (!used.Has($"{cardinality}|{Flag(optional)}|{end}")) continue;
                        markup.Add(CardinalityMarkerMarkup($"er-{cardinality}{(optional ? "-optional" : "")}-{end}", cardinality, optional, end == "start"));
                    }
            if (markup.Count == 0) return "";
            return $"\n          <!-- Cardinality symbols -->\n{string.Join("\n", markup)}";
        }

        // ---- Legend (:442-520) ---------------------------------------------------------
        static List<string> RelationshipEnds(JsonObject relationship)
        {
            var ends = new JsSet<string>();
            ends.Add(CardinalityOf(relationship, "from"));
            ends.Add(CardinalityOf(relationship, "to"));
            if (OptionalOf(relationship, "from") || OptionalOf(relationship, "to")) ends.Add("optional");
            return [.. ends];
        }

        LegendLayout LegendLayoutFor(double width, double height, IReadOnlyList<LegendObstacle>? obstacles = null, string unfit = "error")
        {
            double contentBottom = 0;   // Math.max(0, ...): NaN propagates
            foreach (var entity in entities.Values) contentBottom = Math.Max(contentBottom, entity.Y + entity.Height);
            return new(
                X: Layout.Margin,
                BaselineY: height - LEGEND_BASELINE_GAP,
                Width: width - Layout.Margin * 2,
                FontSize: LEGEND_FONT_SIZE,
                MinTitleY: contentBottom + 8,
                Obstacles: obstacles ?? [],
                Unfit: unfit,
                DiagramType: "erd");
        }

        double[] AutoViewBox()
        {
            double maxX = 0, maxY = 0;   // Math.max(0, ...): NaN propagates
            foreach (var entity in entities.Values)
            {
                maxX = Math.Max(maxX, entity.X + entity.Width);
                maxY = Math.Max(maxY, entity.Y + entity.Height);
            }
            var width = Math.Ceiling(maxX + Layout.Margin);
            var footprint = Legend.LegendFootprint(legendEntries, Math.Max(1, width - Layout.Margin * 2));
            if (footprint.MinWidth > width - Layout.Margin * 2)
            {
                width = Math.Ceiling(footprint.MinWidth + Layout.Margin * 2);
                footprint = Legend.LegendFootprint(legendEntries, width - Layout.Margin * 2);
            }
            var height = Math.Ceiling(maxY + Layout.Margin + Layout.LegendH + footprint.ExtraHeight);
            for (var attempt = 0; attempt < 4; attempt += 1)
            {
                if (Legend.MeasureLegend(ctx.Diagnostics, legendEntries, LegendLayoutFor(width, height, unfit: "hide")) is not null) break;
                height += Layout.LegendH;
            }
            return [width, height];
        }

        // ---- Routing (:535-603) --------------------------------------------------------
        Rect RelationshipLabelBox(JsonObject relationship)
        {
            var (lx, ly) = ErLabelPoint(relationship);
            var width = RelationshipLabelWidth(relationship.Str("label"));
            return new Rect(lx - width / 2, ly - 10, width, 14);
        }

        // `entity.x || 0`: NaN and zero read as 0.
        static double OrZero(double v) => v == 0 || double.IsNaN(v) ? 0 : v;

        static string BandKey(Box entity) =>
            IsInteger(entity.Source.Num("row")) && IsInteger(entity.Source.Num("col"))
                ? $"i{N(entity.Source.Num("row")!.Value)},{N(entity.Source.Num("col")!.Value)}"
                : $"p{N(Js.Round(OrZero(entity.X)))},{N(Js.Round(OrZero(entity.Y)))}";

        string? SideFor(JsonObject relationship, string endpoint)
        {
            var from = EntityOf(relationship.Str("from"));
            var to = EntityOf(relationship.Str("to"));
            if (from is null || to is null) return null;
            return endpoint == "source" ? Geometry.LegacyDefaultFromSide(from, to) : Geometry.LegacyDefaultToSide(from, to);
        }

        string? CorridorKey(JsonObject relationship)
        {
            var from = EntityOf(relationship.Str("from"));
            var to = EntityOf(relationship.Str("to"));
            if (from is null || to is null) return null;
            var (fromSide, _) = router.InferredSides(relationship);
            var horizontalPorts = fromSide is "left" or "right";
            var verticalPorts = fromSide is "top" or "bottom";
            if (horizontalPorts == verticalPorts) return null;
            var ordered = new List<Box> { from, to };
            Js.Sort(ordered, horizontalPorts ? (left, right) => left.Cx - right.Cx : (left, right) => left.Cy - right.Cy);
            return $"{(horizontalPorts ? "h" : "v")}:{BandKey(ordered[0])}|{BandKey(ordered[1])}";
        }

        // `${from}\0${to}\0${id || ''}`.
        static string LaneSortKey(JsonObject relationship) =>
            $"{S(relationship.Str("from"))}\0{S(relationship.Str("to"))}\0{(relationship["id"].Truthy() ? BrandMarks.JsString(relationship["id"]) : "")}";

        // ---- Trunks (:646-909) ---------------------------------------------------------
        bool RelationshipAutoRouted(JsonObject relationship) =>
            RenderableRelationship(relationship)
            // `!relationship.via`: an empty `via: []` is authored here (labelColumnGaps reads `via?.length` instead).
            && !relationship["via"].Truthy()
            && (!relationship["route"].Truthy() || relationship.Str("route") == "auto")
            && !relationship.ContainsKey("channelX")
            && !relationship.ContainsKey("channelY")
            && !relationship["labelAt"].Truthy();

        List<List<Pt>> TrunkCandidates(JsonObject conn, Pt start, Pt end, string fromSide, string toSide) =>
            TrunkCandidateShape(conn, start, end, fromSide, toSide) is { } candidate ? [candidate] : [];

        List<Pt>? TrunkCandidateShape(JsonObject conn, Pt start, Pt end, string fromSide, string toSide)
        {
            var group = trunkAssignments.Get(conn);
            if (group is null) return null;
            var axis = group.Axis;
            var coordinate = group.Coordinate ?? double.NaN;
            static double SideOffset(string side) => side is "right" or "bottom" ? 1 : -1;
            var startHorizontal = IsHorizontalSide(fromSide);
            var endHorizontal = IsHorizontalSide(toSide);
            if (axis == 0)
            {
                if (startHorizontal && endHorizontal) return [new(coordinate, start.Y), new(coordinate, end.Y)];
                if (startHorizontal)
                {
                    var approachY = end.Y + SideOffset(toSide) * TRUNK_LEG;
                    return [new(coordinate, start.Y), new(coordinate, approachY), new(end.X, approachY)];
                }
                if (endHorizontal)
                {
                    var exitY = start.Y + SideOffset(fromSide) * TRUNK_LEG;
                    return [new(start.X, exitY), new(coordinate, exitY), new(coordinate, end.Y)];
                }
                return null;
            }
            if (!startHorizontal && !endHorizontal) return [new(start.X, coordinate), new(end.X, coordinate)];
            if (!startHorizontal)
            {
                var approachX = end.X + SideOffset(toSide) * TRUNK_LEG;
                return [new(start.X, coordinate), new(approachX, coordinate), new(approachX, end.Y)];
            }
            if (!endHorizontal)
            {
                var exitX = start.X + SideOffset(fromSide) * TRUNK_LEG;
                return [new(exitX, start.Y), new(exitX, coordinate), new(end.X, coordinate)];
            }
            return null;
        }

        // The rendered `d` skips each trunk stretch while the logical points keep it.
        List<TrunkRun> RelationshipTrunkRuns(JsonObject relationship)
        {
            var group = trunkAssignments.Get(relationship);
            if (group is null) return [];
            if (trunkRunCache.Has(relationship)) return trunkRunCache.Get(relationship)!;
            var axis = group.Axis;
            var coordinate = group.Coordinate ?? double.NaN;
            // The readability sweep may move a member to another table side after the groups were formed.
            if (router.ConnectionEndpointSide(relationship, group.Role == "in" ? "target" : "source") != group.Side)
            {
                trunkRunCache.Set(relationship, []);
                return [];
            }
            var points = router.PathFor(relationship).Points;
            var runs = new List<TrunkRun>();
            for (var index = 0; index < points.Count - 1; index += 1)
            {
                var (a, b) = (points[index], points[index + 1]);
                if (Math.Abs(Coord(a, axis) - coordinate) > 0.5 || Math.Abs(Coord(b, axis) - coordinate) > 0.5) continue;
                var low = Math.Min(Coord(a, 1 - axis), Coord(b, 1 - axis));
                var high = Math.Max(Coord(a, 1 - axis), Coord(b, 1 - axis));
                if (high - low < 1) continue;
                var last = runs.Count > 0 ? runs[^1] : null;
                if (last is not null && last.EndIndex == index)
                {
                    // Literal: the extended run takes this segment's high and never lowers its low.
                    last.EndIndex = index + 1;
                    last.High = high;
                }
                else
                {
                    runs.Add(new TrunkRun { StartIndex = index, EndIndex = index + 1, Low = low, High = high });
                }
            }
            trunkRunCache.Set(relationship, runs);
            return runs;
        }

        static string BundledRelationshipD(RoutedPath routed, List<TrunkRun> runs)
        {
            if (runs.Count == 0) return routed.D;
            var inRun = new JsSet<int>();
            foreach (var run in runs)
                for (var index = run.StartIndex; index < run.EndIndex; index += 1) inRun.Add(index);
            var pieces = new List<List<Pt>?>();
            List<Pt>? current = [routed.Points[0]];
            for (var index = 0; index < routed.Points.Count - 1; index += 1)
            {
                if (inRun.Has(index))
                {
                    // Literal: consecutive in-run indices push a null piece, which the filter below dereferences (:841-851).
                    pieces.Add(current);
                    current = null;
                    continue;
                }
                current ??= [routed.Points[index]];
                current.Add(routed.Points[index + 1]);
            }
            if (current is not null) pieces.Add(current);
            return string.Join(" ", pieces.Where(piece => piece!.Count >= 2).Select(piece => Geometry.RoundedPath(piece!, 8)));
        }

        string BundledTrunkPaths()
        {
            var clusters = new JsMap<string, List<Span>>();
            foreach (var relationship in relationships)
            {
                var runs = RelationshipTrunkRuns(relationship);
                if (runs.Count == 0) continue;
                var group = trunkAssignments.Get(relationship)!;
                var styleKey = MarkerStyleOf(relationship);
                var key = $"{N(group.Axis)}:{N(group.Coordinate ?? double.NaN)}:{styleKey}";
                var list = clusters.Get(key) ?? [];
                foreach (var run in runs) list.Add(new Span(run.Low, run.High, styleKey));
                clusters.Set(key, list);
            }
            var paths = new List<string>();
            foreach (var (key, runs) in clusters.Entries)
            {
                var parts = key.Split(':');
                var axis = Js.ToNumber(parts[0]);
                var coordinate = Js.ToNumber(parts[1]);
                Js.Sort(runs, (left, right) => left.Low - right.Low);
                var merged = new List<Span>();
                foreach (var run in runs)
                {
                    var last = merged.Count > 0 ? merged[^1] : null;
                    if (last is not null && run.Low <= last.High + 0.5) last.High = Math.Max(last.High, run.High);
                    else merged.Add(run.Copy());
                }
                var segments = new List<Span>();
                foreach (var segment in merged)
                {
                    var last = segments.Count > 0 ? segments[^1] : null;
                    var gap = last is not null ? segment.Low - last.High : double.PositiveInfinity;
                    if (last is not null && gap > 0.5 && gap <= TRUNK_BRIDGE)
                    {
                        var probe = axis == 0
                            ? new Segment(new(coordinate, last.High), new(coordinate, segment.Low))
                            : new Segment(new(last.High, coordinate), new(segment.Low, coordinate));
                        if (!entities.Values.Any(entity => Geometry.SegmentIntersectsRect(probe, entity)))
                        {
                            last.High = segment.High;   // literal: the bridge takes this segment's high
                            continue;
                        }
                    }
                    segments.Add(segment.Copy());
                }
                foreach (var segment in segments)
                {
                    var cls = (Geometry.ArrowClassMap.TryGetValue(segment.StyleKey, out var mapped) ? mapped : Geometry.ArrowClassMap["default"]).Path;
                    List<Pt> points = axis == 0
                        ? [new(coordinate, segment.Low), new(coordinate, segment.High)]
                        : [new(segment.Low, coordinate), new(segment.High, coordinate)];
                    paths.Add($"        <path data-er-trunk=\"\" data-composition-points=\"{Geometry.RoutePointsValue(points)}\" d=\"{Geometry.RoundedPath(points, 8)}\" class=\"{cls}\" stroke-width=\"1.5\"/>");
                }
            }
            return string.Join("\n", paths);
        }

        // ---- Labels (:913-1020) --------------------------------------------------------
        // Bundled labels default to the source-side stub so they never sit on the shared trunk.
        Pt DefaultErLabelPoint(JsonObject relationship)
        {
            var points = router.PathFor(relationship).Points;
            if (trunkAssignments.Get(relationship) is null) return Geometry.LabelPoint(relationship, points);
            return Geometry.LabelPoint(relationship, points, relationship.Num("labelSegment") ?? 0);
        }

        Pt ErLabelPoint(JsonObject relationship) =>
            resolvedLabelPoints.Has(relationship) ? resolvedLabelPoints.Get(relationship) : DefaultErLabelPoint(relationship);

        List<List<Pt>> LaneCandidates(JsonObject conn, Pt start, Pt end, string fromSide)
        {
            if (!laneOffsets.Has(conn)) return [];
            var lane = laneOffsets.Get(conn);
            var channel = ChannelFallback(start, end, fromSide);
            if (channel is not { } found) return [];
            var middle = (Coord(start, found.Axis) + Coord(end, found.Axis)) / 2;
            return [found.ToPoints(middle + lane * LANE_STEP)];
        }

        // ---- Validation (:1023-1335) ---------------------------------------------------
        bool RenderableRelationship(JsonObject relationship) =>
            EntityOf(relationship.Str("from")) is not null && EntityOf(relationship.Str("to")) is not null;

        // Task 3 ports validateEr (:1052-1335).
        public void ValidateEr() { }

        // ---- Report (:1337-1351) -------------------------------------------------------
        public JsonObject BuildLayoutReport() => new()
        {
            ["diagram_type"] = "erd",
            ["viewBox"] = meta["viewBox"] is JsonArray authored ? authored.DeepClone() : new JsonArray(viewBox[0], viewBox[1]),
            ["layout"] = Layout.ToJson(),
            ["entities"] = new JsonArray([.. entities.Values.Select(entity => (JsonNode?)LayoutReport.EntityBox(entity))]),
            ["relationships"] = new JsonArray([.. relationships.Where(RenderableRelationship).Select(relationship =>
            {
                // `relationship.labelAt || resolvedLabelPoints.get(relationship)`: an authored point is always truthy.
                Pt? labelAt = relationship["labelAt"].Truthy() && relationship["labelAt"] is JsonArray at ? new Pt(Elem(at, 0), Elem(at, 1))
                    : resolvedLabelPoints.Has(relationship) ? resolvedLabelPoints.Get(relationship) : null;
                var path = LayoutReport.ConnectionPath(relationship, router.PathFor(relationship).Points, labelAt);
                path["fromCardinality"] = CardinalityOf(relationship, "from");
                path["toCardinality"] = CardinalityOf(relationship, "to");
                return (JsonNode?)path;
            })]),
        };

        // ---- Rendering (:1353-1551) ----------------------------------------------------
        public string RenderSvg() => throw new NotImplementedException("Task 3");
    }
}
