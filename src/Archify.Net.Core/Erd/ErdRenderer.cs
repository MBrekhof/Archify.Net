using System.Text;
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
            : key.AsArrayOrEmpty().Select(Js.PrimitiveString);
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
                    var tag = Js.PrimitiveString(entity.Source["tag"]);
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
                    var tag = Js.PrimitiveString(entity.Source["tag"]);
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
                var tag = Js.PrimitiveString(entity.Source["tag"]);
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
            $"{S(relationship.Str("from"))}\0{S(relationship.Str("to"))}\0{(relationship["id"].Truthy() ? Js.PrimitiveString(relationship["id"]) : "")}";

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

        static (double Name, double Type) RowTextWidths(JsonNode? attribute)
        {
            var name = Utils.TextUnits(attribute.Str("name")) * NodeTextFit.WidthFactor;
            var type = attribute?["type"].Truthy() == true ? Utils.TextUnits(attribute.Str("type")) * NodeTextFit.WidthFactor : 0;
            return (name, type);
        }

        sealed record EntityHeaderFit(bool Problem, double TitleWidth, double LabelFont = 0, double LabelY = 0, double LabelX = 0);

        // The header carries one string: the table title. A domain that earns a band is named by the band, and a tagged
        // table the renderer cannot band is refused outright, so nothing depends on the header to carry a name.
        static EntityHeaderFit EntityHeader(Box entity)
        {
            const double titleInset = 30;
            var label = entity.Source.Str("label");
            var titleWidth = entity.Width - titleInset - Layout.PadX;
            var labelFont = TextFit.FittedNodeFontSize(label, titleWidth + NodeTextFit.HorizontalPadding, Layout.HeaderFont, Layout.HeaderFontMinimum);
            var labelWidth = Utils.TextUnits(label) * labelFont * NodeTextFit.WidthFactor;
            if (labelWidth > titleWidth) return new(true, titleWidth);
            // The title is centered on the table's own center line, held inside the interval between the sigil porch and
            // the right padding.
            var leftEdge = entity.X + titleInset;
            var rightEdge = leftEdge + titleWidth;
            var labelX = Js.Round(Math.Min(Math.Max(entity.X + entity.Width / 2, leftEdge + labelWidth / 2), rightEdge - labelWidth / 2) * 10) / 10;
            return new(false, titleWidth, labelFont, 19, labelX);
        }

        List<Pt> PointsFor(JsonObject relationship) => router.PathFor(relationship).Points;

        static JsonArray Ids(IEnumerable<string> ids) => new([.. ids.Select(id => (JsonNode?)id)]);

        public void ValidateEr()
        {
            var problems = new List<string>();
            // A self-relationship is reported once below; route checks skip it (index-preserving null) so they do not
            // suggest side or route repairs for a line that is never drawn.
            var routableRelationships = relationships.Select(relationship =>
                (JsonNode?)(relationship.Str("from") == relationship.Str("to") ? null : relationship)).ToList();
            var headerDetails = new List<Diagnostic>();
            var domainDetails = new List<Diagnostic>();
            var attributeDetails = new List<Diagnostic>();
            foreach (var entity in entities.Values)
            {
                var header = EntityHeader(entity);
                if (!header.Problem) continue;
                var message = $"Entity \"{entity.Id}\" title does not fit the header at a readable size — widen the entity or shorten the title.";
                problems.Add(message);
                headerDetails.Add(new Diagnostic("erd/header-text-capacity", Severity.Error, message,
                    new JsonObject { ["diagramType"] = "erd", ["entityId"] = entity.Id },
                    new JsonObject { ["label"] = entity.Source["label"]?.DeepClone(), ["availableWidth"] = header.TitleWidth, ["headerHeight"] = metrics.HeaderH },
                    ["Widen the entity.", "Shorten the title."], null));
            }
            foreach (var domain in unlabelableDomains)
            {
                // A shared cell is already reported by the grid placement check, with the pair that has to move.
                if (domain.Reason == "shared-cell") continue;
                var members = domain.Members.Select(entity => entity.Id).ToList();
                var listed = string.Join(", ", members.Select(id => $"\"{id}\""));
                var offGrid = string.Join(", ", domain.OffGrid.Select(id => $"\"{id}\""));
                var message = domain.Reason == "no-grid"
                    ? $"Domain \"{domain.Tag}\" is named but cannot be drawn: {offGrid} have no grid cells, and a band is measured from them. Give every member row/col cells, or drop the tag."
                    : $"Domain \"{domain.Tag}\" is named but cannot be drawn: {listed} do not fill a solid rectangle of grid cells (a row, a column, or a filled block — not an L or diagonal), and a band around them would enclose cells the domain does not own. Move them into one block, or split the tag.";
                problems.Add(message);
                domainDetails.Add(new Diagnostic("erd/domain-not-drawn", Severity.Error, message,
                    new JsonObject { ["diagramType"] = "erd", ["entityId"] = members[0], ["domain"] = domain.Tag },
                    new JsonObject { ["domain"] = domain.Tag, ["entities"] = Ids(members), ["reason"] = domain.Reason, ["offGrid"] = Ids(domain.OffGrid) },
                    domain.Reason == "no-grid"
                        ? ["Give every domain member row/col cells.", "Drop the tag to group nothing."]
                        : ["Give the domain members one solid rectangle of grid cells (a row, a column, or a filled block — not an L).", "Split the tag so each domain can be drawn as a band.", "Drop the tag to group nothing."],
                    null));
            }
            // The band draws the domain name, so the caption is what has to land inside the canvas.
            foreach (var (tag, members) in domainGroups)
            {
                var caption = DomainBandCaption(DomainBandBox(members));
                if (caption.X >= 4 && caption.Y >= 4 && caption.Y <= viewBox[1] - 4 && caption.X <= viewBox[0] - 4) continue;
                var listed = string.Join(", ", members.Select(entity => $"\"{entity.Id}\""));
                var message = $"Domain \"{tag}\" is named but cannot be drawn: the canvas does not reach the band caption for {listed} at ({N(caption.X)}, {N(caption.Y)}). Move the domain clear of the canvas edge, or drop the tag.";
                problems.Add(message);
                domainDetails.Add(new Diagnostic("erd/domain-not-drawn", Severity.Error, message,
                    new JsonObject { ["diagramType"] = "erd", ["entityId"] = members[0].Id, ["domain"] = tag },
                    new JsonObject
                    {
                        ["domain"] = tag, ["entities"] = Ids(members.Select(entity => entity.Id)), ["reason"] = "off-canvas",
                        ["caption"] = new JsonObject { ["x"] = caption.X, ["y"] = caption.Y },
                        ["viewBox"] = meta["viewBox"] is JsonArray authored ? authored.DeepClone() : new JsonArray(viewBox[0], viewBox[1]),
                    },
                    ["Move the domain clear of the canvas edge.", "Drop the tag to group nothing."], null));
            }
            // A band claims the box it covers; with gaps smaller than the band padding it reaches a table the domain does
            // not own.
            foreach (var (tag, members) in domainGroups)
            {
                var memberIds = new JsSet<string>();
                foreach (var entity in members) memberIds.Add(entity.Id);
                var box = DomainBandBox(members);
                var intruder = entities.Values.FirstOrDefault(entity => !memberIds.Has(entity.Id)
                    && entity.X < box.X + box.Width && entity.X + entity.Width > box.X
                    && entity.Y < box.Y + box.Height && entity.Y + entity.Height > box.Y);
                if (intruder is null) continue;
                var message = $"Domain \"{tag}\" is named but cannot be drawn: its band would cover the unrelated table \"{intruder.Id}\". Widen layout.gapX/gapY past the band padding, or split the tag.";
                problems.Add(message);
                domainDetails.Add(new Diagnostic("erd/domain-not-drawn", Severity.Error, message,
                    new JsonObject { ["diagramType"] = "erd", ["entityId"] = members[0].Id, ["domain"] = tag },
                    new JsonObject
                    {
                        ["domain"] = tag, ["entities"] = Ids(members.Select(entity => entity.Id)), ["reason"] = "band-covers-entity",
                        ["band"] = new JsonObject { ["x"] = box.X, ["y"] = box.Y, ["width"] = box.Width, ["height"] = box.Height },
                        ["coveredEntity"] = intruder.Id,
                    },
                    ["Widen layout.gapX/gapY past the band padding.", "Split the tag so each band covers only its own tables."], null));
            }
            ErdGrid.ValidateErGridPlacement(er, grid, bands, problems);

            // The authored list (duplicates included), not the deduped Map.
            var seenEntityIds = new JsSet<string>();
            var authoredEntities = er["entities"].AsArrayOrEmpty().Select(node => (JsonObject)node!).ToList();
            for (var entityIndex = 0; entityIndex < authoredEntities.Count; entityIndex++)
            {
                var entity = authoredEntities[entityIndex];
                var entityId = S(entity.Str("id"));
                if (seenEntityIds.Has(entityId)) problems.Add($"Entity ids must be unique; \"{entityId}\" is declared twice.");
                seenEntityIds.Add(entityId);

                if (!double.IsFinite(entity.Num("width") ?? double.NaN) && entity.ContainsKey("width"))
                    problems.Add($"Entity \"{entityId}\" width must be a finite number.");
                var seenColumns = new JsSet<string>();
                var attributes = entity["attributes"].AsArrayOrEmpty().ToList();
                for (var attributeIndex = 0; attributeIndex < attributes.Count; attributeIndex++)
                {
                    var attribute = attributes[attributeIndex];
                    var attributeName = S(attribute.Str("name"));
                    if (seenColumns.Has(attributeName)) problems.Add($"Entity \"{entityId}\" declares attribute \"{attributeName}\" twice.");
                    seenColumns.Add(attributeName);
                    var (nameUnits, typeUnits) = RowTextWidths(attribute);
                    // validateEr sees the authored object, so the overflow check uses the resolved width.
                    var available = EntityWidth(entity) - Layout.PadX * 2 - KeyColumnWidth(entity);
                    var needed = nameUnits * Layout.RowFont + (typeUnits != 0 ? typeUnits * Layout.TypeFont + 8 : 0);
                    if (!(needed > available)) continue;
                    var requiredEntityWidth = Math.Ceiling(needed + Layout.PadX * 2 + KeyColumnWidth(entity));
                    var fix = $"For this attribute row's text capacity, set entities[{N(entityIndex)}].width to at least {N(requiredEntityWidth)}px, preserving the attribute name, type and key roles.";
                    var message = $"Entity \"{entityId}\" attribute {N(attributeIndex)} \"{attributeName}\" needs {N(Math.Ceiling(needed))}px of text but only {N(Math.Floor(available))}px is available — {fix}";
                    problems.Add(message);
                    attributeDetails.Add(new Diagnostic("erd/attribute-text-capacity", Severity.Error, message,
                        new JsonObject
                        {
                            ["diagramType"] = "erd", ["entityId"] = entity["id"]?.DeepClone(), ["attributeIndex"] = attributeIndex,
                            ["attributeName"] = attribute?["name"]?.DeepClone(),
                        },
                        new JsonObject
                        {
                            ["entityWidth"] = EntityWidth(entity), ["requiredEntityWidth"] = requiredEntityWidth,
                            ["availableTextWidth"] = available, ["requiredTextWidth"] = needed,
                            ["paddingWidth"] = Layout.PadX * 2, ["keyColumnWidth"] = KeyColumnWidth(entity),
                            ["nameFontSize"] = Layout.RowFont, ["typeFontSize"] = Layout.TypeFont,
                        },
                        [fix], null));
                }
            }

            foreach (var entity in entities.Values)
            {
                foreach (var other in entities.Values)
                {
                    if (string.CompareOrdinal(entity.Id, other.Id) >= 0) continue;
                    if (!Geometry.RectsOverlap(entity, other, 0)) continue;
                    problems.Add($"Entity \"{entity.Id}\" overlaps entity \"{other.Id}\".\n{Geometry.SuggestComponentSeparation(entity, other)}");
                }
            }

            for (var index = 0; index < relationships.Count; index++)
            {
                var relationship = relationships[index];
                var id = relationship["id"].Truthy() ? $" id \"{Js.PrimitiveString(relationship["id"])}\"" : "";
                var from = relationship.Str("from");
                var to = relationship.Str("to");
                if (EntityOf(from) is null)
                {
                    problems.Add($"Relationship {N(index)}{id} references unknown source entity \"{S(from)}\".");
                    continue;
                }
                if (EntityOf(to) is null)
                {
                    problems.Add($"Relationship {N(index)}{id} references unknown target entity \"{S(to)}\".");
                    continue;
                }
                if (from == to)
                {
                    problems.Add($"Relationship {N(index)}{id} connects entity \"{S(from)}\" to itself — draw it between two entities or describe the self-reference in a card.");
                    continue;
                }
                var points = PointsFor(relationship);
                var (start, end) = (points[0], points[^1]);
                var distance = Js.Hypot(end.X - start.X, end.Y - start.Y);
                if (distance < Layout.MinRelationshipLength)
                {
                    problems.Add($"Relationship {N(index)}{id} \"{S(from)}\" -> \"{S(to)}\" is too short ({N(Js.Round(distance))}px; minimum {N(Layout.MinRelationshipLength)}px) — place the entities farther apart.");
                }
            }

            foreach (var entity in authoredEntities)
            {
                var attributes = entity["attributes"].AsArrayOrEmpty().ToList();
                for (var attributeIndex = 0; attributeIndex < attributes.Count; attributeIndex++)
                {
                    var attribute = attributes[attributeIndex];
                    if (attribute?["references"] is not JsonValue reference || reference.GetValueKind() != JsonValueKind.String) continue;
                    var references = reference.GetValue<string>();
                    var parts = references.Split('.');
                    var (targetId, targetColumn) = (parts[0], parts.Length > 1 ? parts[1] : null);
                    var target = entities.Get(targetId);
                    if (target is null)
                    {
                        problems.Add($"Entity \"{S(entity.Str("id"))}\" attribute {N(attributeIndex)} \"{S(attribute.Str("name"))}\" references unknown entity \"{targetId}\" in \"{references}\".");
                        continue;
                    }
                    if (!target.Source["attributes"].AsArrayOrEmpty().Any(candidate => candidate.Str("name") == targetColumn))
                    {
                        problems.Add($"Entity \"{S(entity.Str("id"))}\" attribute {N(attributeIndex)} \"{S(attribute.Str("name"))}\" references unknown attribute \"{references}\" — \"{targetId}\" has no attribute \"{S(targetColumn)}\".");
                    }
                }
            }

            // A busy fan-in can leave less room than a glyph is tall: the router spreads a side by
            // min(maxSpacing, (extent - 2 x gutter) / (n - 1)), so the renderer names it here.
            var portSpacingDetails = new List<Diagnostic>();
            foreach (var (entityId, entity) in entities.Entries)
            {
                var sides = new JsMap<string, double>();
                foreach (var relationship in relationships)
                {
                    if (!RelationshipAutoRouted(relationship)) continue;
                    var (fromSide, toSide) = router.InferredSides(relationship);
                    if (relationship.Str("from") == entityId) sides.Set(fromSide, sides.Get(fromSide) + 1);   // `|| 0`
                    if (relationship.Str("to") == entityId) sides.Set(toSide, sides.Get(toSide) + 1);
                }
                foreach (var (side, count) in sides.Entries)
                {
                    if (count < 2) continue;
                    var extent = side is "left" or "right" ? entity.Height : entity.Width;
                    var spacing = Math.Min(ERD_PORT_SPACING, Math.Max(0, extent - 32) / (count - 1));
                    if (spacing >= MARKER_HEIGHT) continue;
                    var message = $"Entity \"{entityId}\" shares its {side} side with {N(count)} relationship ends: their ports would sit {N(Js.Round(spacing))}px apart, under the {N(MARKER_HEIGHT)}px a cardinality glyph is tall, so the symbols overlap. Give the table more rows so that side is taller, spread the relationships across sides with room, or reduce the fan-in.";
                    problems.Add(message);
                    portSpacingDetails.Add(new Diagnostic("layout/marker-capacity", Severity.Error, message,
                        // The path names the entity id, not its index.
                        new JsonObject { ["diagramType"] = "erd", ["path"] = $"/entities/{entityId}" },
                        new JsonObject
                        {
                            ["entity"] = entityId, ["side"] = side, ["relationshipEnds"] = count, ["spacingPx"] = Js.Round(spacing),
                            ["markerHeightPx"] = MARKER_HEIGHT, ["sideExtentPx"] = extent,
                        },
                        [
                            $"add fields to \"{entityId}\" so its {side} side is taller",
                            "spread the relationships across the sides that have room",
                            "reduce the fan-in by modelling the shared parent instead of pointing every child at this table",
                        ], null));
                }
            }

            var sink = ctx.Diagnostics;
            JsSet<string> EndpointIds()
            {
                var ids = new JsSet<string>();
                foreach (var key in entities.Keys) ids.Add(key);
                return ids;
            }
            problems.AddRange(Geometry.CleanEndpointSideProblems(sink, routableRelationships, EndpointIds(), PointsFor, "erd", "relationships",
                relationship => router.ConnectionEndpointSide(relationship, "source"),
                relationship => router.ConnectionEndpointSide(relationship, "target"),
                routeHint: "keep automatic routing so the renderer can use a side-aware bridge, or set truthful fromSide/toSide"));
            problems.AddRange(Geometry.CleanFlowProblems(sink, routableRelationships, entities.Values, PointsFor, "erd", "relationships", "entity",
                routeHint: "adjust fromSide/toSide, set route/via, or move the entity"));
            // P1: the four profile-taking gates read gateProfile (the option, else meta.quality_profile).
            problems.AddRange(Geometry.CleanCrossingProblems(sink, routableRelationships, EndpointIds(), PointsFor, "erd", "relationships",
                profile: gateProfile,
                routeHint: "move the entities so unrelated relationships use separate corridors — put a junction beside both parents inside one solid domain rectangle"));
            problems.AddRange(Geometry.CleanAmbiguousCorridorProblems(sink, routableRelationships, EndpointIds(), PointsFor, "erd", "relationships",
                profile: gateProfile, routeHint: "give the relationships different fromSide/toSide targets, or move an entity so their corridors differ"));
            problems.AddRange(Geometry.CleanRouteRhythmProblems(sink, routableRelationships, EndpointIds(), PointsFor, "erd", "relationships",
                gateProfile, routeHint: "widen the gap between bands so every turn has room to read"));

            var labelRects = new List<LabelRect>();
            for (var index = 0; index < relationships.Count; index++)
            {
                var relationship = relationships[index];
                if (!relationship["label"].Truthy() || !RenderableRelationship(relationship)) continue;
                var (lx, ly) = ErLabelPoint(relationship);
                var box = RelationshipLabelBox(relationship);
                labelRects.Add(new LabelRect
                {
                    Relation = relationship, RelationIndex = index, Label = relationship.Str("label"),
                    X = box.X, Y = box.Y, Width = box.Width, Height = box.Height, Lx = lx, Ly = ly,
                });
            }
            foreach (var rect in labelRects)
            {
                foreach (var entity in entities.Values)
                {
                    if (!Geometry.RectsOverlap(rect, entity, -2)) continue;
                    problems.Add($"Label \"{S(rect.Label)}\" overlaps entity \"{entity.Id}\" — adjust labelDx/labelDy/labelSegment or set labelAt.\n{Geometry.SuggestLabelObstacleFix(rect, rect.Lx!.Value, rect.Ly!.Value, entity, "entity")}");
                }
            }
            problems.AddRange(Geometry.CleanLabelRouteClearanceProblems(sink, routableRelationships, labelRects, EndpointIds(), PointsFor, "erd", "relationships", gateProfile));

            if (problems.Count > 0)
            {
                throw sink.ThrowDiagnosticProblems("Entity-relationship layout validation failed", problems, "layout/constraint",
                    new JsonObject { ["diagramType"] = "erd" }, [.. headerDetails, .. domainDetails, .. portSpacingDetails, .. attributeDetails]);
            }
        }

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
        string RenderEntityColumnRows(Box entity)
        {
            var keyColumn = KeyColumnWidth(entity.Source);
            return string.Join("\n", entity.Source["attributes"].AsArrayOrEmpty().Select((attribute, index) =>
            {
                var rowTop = entity.Y + metrics.HeaderH + index * metrics.RowH;
                var baseline = rowTop + metrics.RowH - 5;
                // Each text repeats `data-detail="context"`: the Viewer's reader sizes the diagram from the texts that
                // declare the level themselves.
                var keyGlyph = RenderKeyGlyphs(attribute, entity.X + Layout.PadX, baseline);
                var nameX = entity.X + Layout.PadX + keyColumn;
                var hasType = attribute?["type"].Truthy() == true;
                var typeSpace = hasType ? Math.Min(96, Utils.TextUnits(attribute.Str("type")) * Layout.TypeFont * NodeTextFit.WidthFactor) : 0;
                var nameFontSize = TextFit.FittedNodeFontSize(attribute.Str("name"),
                    Math.Max(40, entity.X + entity.Width - Layout.PadX - typeSpace - nameX), Layout.RowFont, Layout.RowFontMinimum);
                var type = hasType
                    ? $"<text data-detail=\"context\" x=\"{N(entity.X + entity.Width - Layout.PadX)}\" y=\"{N(baseline)}\" class=\"er-type\" font-size=\"{N(Layout.TypeFont)}\" text-anchor=\"end\">{Utils.Esc(attribute.Str("type"))}</text>"
                    : "";
                var zebra = index % 2 != 0
                    ? $"<rect x=\"{N(entity.X + 4)}\" y=\"{N(rowTop + 1)}\" width=\"{N(entity.Width - 8)}\" height=\"{N(metrics.RowH - 2)}\" rx=\"4\" class=\"er-zebra\"/>"
                    : "";
                var primary = KeyRoles(attribute).Contains("pk");
                return Utils.TrailingBlanks.Replace(
                    $"          <g data-detail=\"context\" data-er-row=\"{N(index)}\">\n"
                    + $"            <line x1=\"{N(entity.X)}\" y1=\"{N(rowTop)}\" x2=\"{N(entity.X + entity.Width)}\" y2=\"{N(rowTop)}\" class=\"c-grid er-row-rule\" stroke-width=\"0.5\"/>\n"
                    + $"            {zebra}\n"
                    + $"            {keyGlyph}\n"
                    + $"            <text data-detail=\"context\" x=\"{N(nameX)}\" y=\"{N(baseline)}\" class=\"t-primary{(primary ? " er-name-pk" : "")}\" font-size=\"{N(nameFontSize)}\">{Utils.Esc(attribute.Str("name"))}</text>\n"
                    + $"            {type}\n"
                    + "          </g>", "");
            }));
        }

        string RenderEntity(Box entity)
        {
            var header = entity.Y + metrics.HeaderH;
            var fit = EntityHeader(entity);
            var passport = new JsonObject { ["kind"] = "database" };
            if (entity.Source["sublabel"] is { } sublabel) passport["sublabel"] = sublabel.DeepClone();
            if (entity.Source["tag"] is { } tagNode) passport["tag"] = tagNode.DeepClone();
            passport["context"] = ctx.Locale.Translate(locale, "node.context.erd");
            var rows = RenderEntityColumnRows(entity);
            const double r = 8;
            var label = S(entity.Source.Str("label"));
            var tag = entity.Source["tag"].Truthy() ? Js.PrimitiveString(entity.Source["tag"]) : null;
            return $"        <g {SvgAttrs.FocusNodeAttrs(entity.Id, label, passport, locale, ctx)} class=\"{HueClassOf(tag)}\">\n"
                + $"          {SvgAttrs.FocusNodeTitle(label, passport)}\n"
                + $"          <rect x=\"{N(entity.X)}\" y=\"{N(entity.Y)}\" width=\"{N(entity.Width)}\" height=\"{N(entity.Height)}\" rx=\"{N(r)}\" class=\"c-mask\"/>\n"
                + $"          <rect x=\"{N(entity.X)}\" y=\"{N(entity.Y)}\" width=\"{N(entity.Width)}\" height=\"{N(entity.Height)}\" rx=\"{N(r)}\" class=\"c-database er-table\"{SvgAttrs.AnimateAttr(meta, "node", entitySteps.Has(entity.Id) ? entitySteps.Get(entity.Id) : null)} stroke-width=\"1.2\"/>\n"
                + $"          <path class=\"er-table-header\" d=\"M {N(entity.X + r)} {N(entity.Y)} H {N(entity.X + entity.Width - r)} A {N(r)} {N(r)} 0 0 1 {N(entity.X + entity.Width)} {N(entity.Y + r)} V {N(header)} H {N(entity.X)} V {N(entity.Y + r)} A {N(r)} {N(r)} 0 0 1 {N(entity.X + r)} {N(entity.Y)} Z\"/>\n"
                + $"          <path class=\"er-table-cap\" d=\"M {N(entity.X + 12)} {N(entity.Y + 1.4)} H {N(entity.X + entity.Width - 12)}\" stroke-width=\"2.6\" stroke-linecap=\"round\" fill=\"none\"/>\n"
                + $"          {Utils.RenderSemanticSigil("database", entity.X + 8, entity.Y + 7, 14)}\n"
                + $"          <text data-node-label=\"\" x=\"{N(fit.LabelX)}\" y=\"{N(entity.Y + fit.LabelY)}\" class=\"t-primary er-title\" font-size=\"{N(fit.LabelFont)}\" font-weight=\"700\" text-anchor=\"middle\">{Utils.Esc(label)}</text>\n"
                + $"          <line x1=\"{N(entity.X)}\" y1=\"{N(header)}\" x2=\"{N(entity.X + entity.Width)}\" y2=\"{N(header)}\" class=\"c-grid er-header-rule\" stroke-width=\"1\"/>\n"
                + $"{rows}\n"
                + "        </g>";
        }

        string RenderRelationshipPath(JsonObject relationship, int index)
        {
            // `arrowClassMap[k] || arrowClassMap.default`
            var cls = (Geometry.ArrowClassMap.TryGetValue(MarkerStyleOf(relationship), out var mapped) ? mapped : Geometry.ArrowClassMap["default"]).Path;
            var routed = router.PathFor(relationship);
            var d = BundledRelationshipD(routed, RelationshipTrunkRuns(relationship));
            var strokeWidth = relationship.Num("width") is { } width && double.IsFinite(width) ? width : 1.5;
            return $"        <path {SvgAttrs.FocusEdgeAttrs(S(relationship.Str("from")), S(relationship.Str("to")), relationship.Str("label"), N(index), relationship.Str("id"))} data-er-ends=\"{string.Join(" ", RelationshipEnds(relationship))}\" data-composition-points=\"{Geometry.RoutePointsValue(routed.Points)}\" d=\"{d}\" class=\"{cls}\"{SvgAttrs.AnimateAttr(meta, "edge", index)} stroke-width=\"{N(strokeWidth)}\" marker-start=\"url(#{CardinalityMarkerId(relationship, "from")})\" marker-end=\"url(#{CardinalityMarkerId(relationship, "to")})\"/>";
        }

        string RenderRelationshipLabel(JsonObject relationship, int index)
        {
            if (!relationship["label"].Truthy()) return "";
            var (lx, ly) = ErLabelPoint(relationship);
            var box = RelationshipLabelBox(relationship);
            return $"        <g data-detail=\"context\" {SvgAttrs.FocusEdgeAttrs(S(relationship.Str("from")), S(relationship.Str("to")), relationship.Str("label"), N(index), relationship.Str("id"))} data-er-ends=\"{string.Join(" ", RelationshipEnds(relationship))}\">\n"
                + $"          <rect x=\"{N(box.X)}\" y=\"{N(box.Y)}\" width=\"{N(box.Width)}\" height=\"{N(box.Height)}\" rx=\"3\" class=\"c-mask\"/>\n"
                + $"          <text x=\"{N(lx)}\" y=\"{N(ly)}\" class=\"{Geometry.VariantAccent(relationship.Str("variant"))}\" font-size=\"{N(RELATIONSHIP_LABEL_FONT)}\" text-anchor=\"middle\">{Utils.Esc(relationship.Str("label"))}</text>\n"
                + "        </g>";
        }

        // A key swatch is the badge a field row draws; a relationship swatch is a short route meeting a table edge with
        // the marker's own glyph, ink and weight.
        static string LegendSwatch(LegendEntry entry)
        {
            var x = entry.X + entry.Lead;
            var centerY = entry.Baseline - 4;
            var hit = $"<rect class=\"er-legend-hit\" tabindex=\"0\" role=\"img\" aria-label=\"{Utils.Esc(entry.Label)}\" x=\"{N(x - 6)}\" y=\"{N(entry.Baseline - 15)}\" width=\"{N(entry.Width - entry.Lead + 12)}\" height=\"22\" rx=\"11\"/>";
            var divider = entry.Lead != 0 && entry.X > Layout.Margin
                ? $"<line x1=\"{N(entry.X - 4)}\" y1=\"{N(centerY - 8)}\" x2=\"{N(entry.X - 4)}\" y2=\"{N(centerY + 8)}\" class=\"er-legend-divider\" stroke-width=\"1\"/>"
                : "";
            if (KEY_KINDS.Contains(entry.Kind))
            {
                return $"{hit}<rect x=\"{N(x)}\" y=\"{N(entry.Baseline - 10)}\" width=\"{N(LEGEND_KEY_SWATCH)}\" height=\"13\" rx=\"3\" class=\"er-key-badge\" data-er-badge=\"{entry.Kind}\"/><text x=\"{N(x + LEGEND_KEY_SWATCH / 2)}\" y=\"{N(entry.Baseline)}\" class=\"{KeyAccent(entry.Kind)}\" text-anchor=\"middle\" font-size=\"{N(Layout.KeyFont)}\" font-weight=\"600\">{Utils.Esc(entry.Kind.ToUpperInvariant())}</text>";
            }
            var edgeX = x + LEGEND_END_SWATCH - 2;
            var glyph = CardinalityGlyphParts(entry.Kind == "optional" ? null : entry.Kind, entry.Kind == "optional");
            return $"{divider}{hit}<line x1=\"{N(x)}\" y1=\"{N(centerY)}\" x2=\"{N(edgeX)}\" y2=\"{N(centerY)}\" class=\"er-legend-route\" stroke-width=\"1.5\"/><line x1=\"{N(edgeX)}\" y1=\"{N(centerY - 7)}\" x2=\"{N(edgeX)}\" y2=\"{N(centerY + 7)}\" class=\"er-legend-edge\" stroke-width=\"2\"/><g class=\"er-legend-glyph\" transform=\"translate({N(edgeX - MARKER_TOE_X)} {N(centerY - 8)})\" style=\"stroke: var(--text-muted)\" stroke-width=\"1.75\" fill=\"none\">{string.Concat(glyph)}</g>";
        }

        // The ring a highlighted badge takes, in the ink of the role's own text (KEY_RING, :1453).
        static readonly (string Kind, string Ring)[] KEY_RING = [("pk", "var(--database-stroke)"), ("fk", "var(--messagebus-stroke)"), ("uk", "var(--text-muted)")];

        static readonly string LEGEND_HOVER_RULES = string.Join("\n", KEY_RING.Select(entry =>
                $"          svg[data-erd-ui=\"cards\"]:has([data-legend-semantic-kind=\"{entry.Kind}\"]:is(:hover, :focus-within)) [data-er-row]:not(:has([data-er-badge=\"{entry.Kind}\"])) {{ opacity: .22; }}\n"
                + $"          svg[data-erd-ui=\"cards\"]:has([data-legend-semantic-kind=\"{entry.Kind}\"]:is(:hover, :focus-within)) [data-er-row] [data-er-badge=\"{entry.Kind}\"] {{ stroke: {entry.Ring}; stroke-width: 1.25; }}")
            // Most schemas carry one and many on every route, so the matching glyph itself takes the accent ink.
            .Concat(new (string Kind, string Marker)[] { ("one", "[id^=\"er-one\"]"), ("many", "[id^=\"er-many\"]"), ("optional", "[id*=\"-optional\"]") }.Select(entry =>
                $"          svg[data-erd-ui=\"cards\"]:has([data-legend-semantic-kind=\"{entry.Kind}\"]:is(:hover, :focus-within)) [data-er-ends]:not([data-er-ends~=\"{entry.Kind}\"]) {{ opacity: .12; }}\n"
                + $"          svg[data-erd-ui=\"cards\"]:has([data-legend-semantic-kind=\"{entry.Kind}\"]:is(:hover, :focus-within)) marker{entry.Marker} {{ stroke: var(--database-stroke) !important; stroke-width: 2.25; }}")));

        string RenderLegend()
        {
            var relationshipObstacles = Legend.RelationshipLegendObstacles(relationships.Cast<JsonNode?>(),
                pointsFor: (relationship, _) => RenderableRelationship(relationship) ? PointsFor(relationship) : [],
                labelRectFor: (relationship, _) => relationship["label"].Truthy() && RenderableRelationship(relationship) ? (Rect?)RelationshipLabelBox(relationship) : null);
            return Legend.RenderLegend(ctx, legendEntries, LegendLayoutFor(viewBox[0], viewBox[1], relationshipObstacles), LegendSwatch, locale,
                labelClass: "t-primary", labelWeight: 600);
        }

        public string RenderSvg()
        {
            // An automatic canvas declares its height as intrinsic so the reader scrolls instead of shrinking the tables;
            // an authored meta.viewBox stays authoritative.
            var readerFit = meta["viewBox"].Truthy() ? "" : " data-reader-fit=\"intrinsic-height\"";
            var readerMinimumText = meta["viewBox"].Truthy() ? "" : " data-reader-min-text=\"7.5\"";
            var sb = new StringBuilder();
            sb.Append($"      <svg viewBox=\"0 0 {N(viewBox[0])} {N(viewBox[1])}\" {SvgAttrs.SvgRootAttrs(ctx)} data-erd-ui=\"cards\"{readerFit}{readerMinimumText}>\n");
            sb.Append(SvgAttrs.SvgAccessibleText(ctx, "erd")).Append('\n');
            sb.Append(Utils.RenderDefinitions(RenderCardinalityDefs())).Append('\n');
            sb.Append("        <style>\n");
            sb.Append(string.Join("\n", DOMAIN_HUES.Select(hue => $"          svg[data-erd-ui=\"cards\"] .er-hue-{hue} {{ --er-hue: var(--{hue}-stroke); --er-wash: var(--{hue}-fill); }}"))).Append('\n');
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-band { fill: color-mix(in srgb, var(--er-hue) 4%, transparent); stroke: color-mix(in srgb, var(--er-hue) 20%, transparent); stroke-dasharray: none; }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-band-pill { fill: var(--er-wash); stroke: color-mix(in srgb, var(--er-hue) 30%, transparent); stroke-width: .8; }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-band-label { fill: var(--er-hue); letter-spacing: .08em; text-transform: uppercase; }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-table { fill: var(--mask); stroke: color-mix(in srgb, var(--er-hue) 34%, var(--lane-stroke)); }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-table-header { fill: var(--er-wash); stroke: none; }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-table-cap { stroke: var(--er-hue); pointer-events: none; }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-header-rule { stroke: color-mix(in srgb, var(--er-hue) 30%, var(--lane-stroke)); }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] [class*=\"er-hue-\"] .semantic-sigil { color: var(--er-hue); }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-row-rule { stroke-opacity: 0; }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-zebra { fill: color-mix(in srgb, var(--er-hue) 6%, transparent); }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-name-pk { font-weight: 700; }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-type { fill: var(--text-muted); opacity: .86; }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-key-badge { fill: var(--database-fill); stroke: none; }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-key-badge[data-er-badge=\"fk\"] { fill: var(--messagebus-fill); }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-key-badge[data-er-badge=\"uk\"] { fill: none; stroke: var(--lane-stroke); }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-key-badge + text { text-anchor: middle; }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] [data-legend] text.t-primary { fill: var(--text); }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] [data-legend] > text.t-primary { fill: var(--text-muted); font-size: 10px; letter-spacing: .08em; text-transform: uppercase; }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-legend-hit { fill: transparent; transition: fill .15s; }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-legend-hit:focus { outline: none; }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-legend-hit:focus-visible { stroke: var(--database-stroke); stroke-width: 1.5; }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] [data-legend-semantic-kind]:is(:hover, :focus-within) .er-legend-hit { fill: var(--database-fill); }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] [data-legend-semantic-kind]:is(:hover, :focus-within) .er-legend-glyph { stroke: var(--database-stroke) !important; }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-legend-route { stroke: var(--text-muted); stroke-opacity: .55; }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-legend-edge { stroke: var(--lane-stroke); stroke-linecap: round; }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] .er-legend-divider { stroke: var(--lane-stroke); }\n");
            sb.Append("          svg[data-erd-ui=\"cards\"] [data-er-row], svg[data-erd-ui=\"cards\"] [data-er-ends] { transition: opacity .15s; }\n");
            sb.Append(LEGEND_HOVER_RULES).Append('\n');
            sb.Append("          @media (prefers-reduced-motion: reduce) { svg[data-erd-ui=\"cards\"] * { transition: none !important; } }\n");
            sb.Append("        </style>\n");
            sb.Append('\n');
            sb.Append("        <!-- Background Grid -->\n");
            sb.Append("        <rect width=\"100%\" height=\"100%\" fill=\"url(#grid)\" />\n");
            sb.Append('\n');
            sb.Append("        <!-- Domain bands (behind the routes so a channel is never washed out) -->\n");
            sb.Append(RenderDomainBands()).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Bundled relationship trunks (under the branches: a bus runs close\n");
            sb.Append("             to the shared side, so a cardinality glyph that reaches further out\n");
            sb.Append("             has to stay readable over it, and the ring's mask has to be able to\n");
            sb.Append("             punch through) -->\n");
            sb.Append(BundledTrunkPaths()).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Relationship paths (before entities for correct z-order) -->\n");
            sb.Append(string.Join("\n", relationships.Select((relationship, index) => RenderableRelationship(relationship) ? RenderRelationshipPath(relationship, index) : "").Where(Truthy))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Entities -->\n");
            sb.Append(string.Join("\n\n", entities.Values.Select(RenderEntity))).Append('\n');
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
