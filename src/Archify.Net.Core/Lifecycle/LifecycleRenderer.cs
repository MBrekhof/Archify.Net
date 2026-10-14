using System.Text;
using System.Text.Json.Nodes;

namespace Archify.Net;

// render-lifecycle.mjs. The module body (line 25 to validateLayout at 978) becomes one Scene built per render; the module
// functions are its methods. No layout report and no quality-profile gate: upstream reads neither.
//
// Lifecycle v3 draws one shape: the main path as a left-to-right row, loops
// back to earlier phases as arcs above it, and every other state (exits,
// waiting or recovery states) in one row below the phase it branches from.
// Geometry is derived from that structure alone; there are no routing controls.
public sealed class LifecycleRenderer : IDiagramRenderer
{
    public (string Svg, JsonArray? Cards, LayoutReport? Layout) Render(RenderContext ctx)
    {
        var scene = new Scene(ctx);
        return (scene.RenderSvg(), ctx.Diagram.Arr("cards"), null);
    }

    static class Layout
    {
        public const double LabelPreferred = 12, LabelMinimum = 10, SublabelPreferred = 9, SublabelMinimum = 8, TagPreferred = 8, TagMinimum = 7, StepFont = 8;
        public const double LABEL_FONT = 10;
        public const double NOTE_FONT = 9;
        public const double STATE_H = 64;
        public const double STATE_MIN_W = 140;
        public const double STATE_MAX_W = 220;
        public const double MARGIN_X = 56;
        public const double MARGIN_TOP = 28;
        public const double PORT_PAD = 22;
        public const double PORT_STEP = 26;
        public const double CORNER = 12;
        public const double FRAME_PAD = 10;
        public const double ARC_BASE = 30;
        public const double ARC_STEP = 24;
        public const double TRACK_STEP = 18;
        public const double LABEL_GAP = 6;
        public const double CLEARANCE = 5;
        public const int SPACING_ROUNDS = 5;
    }

    static readonly IReadOnlyDictionary<string, string> TypeClass = new Dictionary<string, string>(StringComparer.Ordinal)   // lookup only
    {
        ["start"] = "c-frontend", ["active"] = "c-frontend", ["waiting"] = "c-cloud", ["decision"] = "c-database",
        ["success"] = "c-backend", ["failure"] = "c-security", ["neutral"] = "c-external", ["external"] = "c-external",
    };
    // Ordinary states carry no default corner sigil, so the ones a reader should
    // notice (waiting, decision, outcomes, external) stand out. An ordinary state
    // that ends the lifecycle is still an outcome and gets a stop sigil.
    static readonly HashSet<string> QuietTypes = new(StringComparer.Ordinal) { "start", "active", "neutral" };   // lookup only
    static readonly IReadOnlyDictionary<string, string> TextClass = new Dictionary<string, string>(StringComparer.Ordinal)   // lookup only
    {
        ["start"] = "t-frontend", ["active"] = "t-frontend", ["waiting"] = "t-cloud", ["decision"] = "t-database",
        ["success"] = "t-backend", ["failure"] = "t-security", ["neutral"] = "t-muted", ["external"] = "t-muted",
    };
    static readonly string[] LegendKinds = ["start", "active", "waiting", "decision", "success", "failure", "neutral", "external"];

    const char NUL = (char)0;

    // `{ ...state }` (render-lifecycle.mjs:59) plus the width/height/x/cx/y/cy/offset the layout writes onto it.
    sealed class State
    {
        public required JsonObject Source { get; init; }
        public required string Id { get; init; }
        public string? Type => Source.Str("type");
        public string? Label => Source.Str("label");
        public string? Sublabel => Source.Str("sublabel");
        public string? Tag => Source.Str("tag");
        public string? Step => Source.Str("step");
        public string? Icon => Source.Str("icon");
        public ResolvedBrand? Brand;
        public double Width, Height, X, Cx, Y, Cy, Offset;
        public Rect Rect => new(X, Y, Width, Height);
    }

    sealed class Transition
    {
        public required JsonObject Source { get; init; }
        public required int Index { get; init; }
        public string? Id => Source.Str("id");
        // `${transition.from}` of a missing field prints `undefined`; states.has(undefined) is false.
        public string From => Source.Str("from") ?? "undefined";
        public string To => Source.Str("to") ?? "undefined";
        public string? Label => Source.Str("label");
        public string? Note => Source.Str("note");
        public string? Variant => Source.Str("variant");
    }

    sealed class Bracket
    {
        public required string Key;
        public required List<string> Sources;
        public List<string> Targets = [];
        public List<Transition> Transitions = [];
        public (int Lo, int Hi)? Frame;
    }

    sealed class Connector
    {
        public required string Kind;
        public Transition? Transition;
        public bool Down;
        public string? UpperId, LowerId;
        public int Gap, Order;
        public Bracket? Bracket;
        public Rect Rect;
        public double? ExitX;
    }

    sealed class Port
    {
        public required object Key;   // a Connector, or the string key of a loop/arc riser
        public double OtherX;
        public int Order;
        public double X;
    }

    sealed class Arc
    {
        public required Transition Transition;
        public required string Key;
        public int Lo, Hi, Index;
        public int Level;   // 0 = unassigned (`other.level` falsy)
    }

    sealed class Run
    {
        public required Connector Connector;
        public required List<double> Tops, Bottoms;
        public double Lo, Hi;
        public bool Straight;
        public int? Track;
    }

    sealed class Route
    {
        public required List<Pt> Points;
        public int Segment;
        public required string LabelMode;
        public Bracket? Bracket;
    }

    readonly record struct Placed(double X, double Y, double Width, double Height, double Cx, double Cy)
    {
        public static implicit operator Rect(Placed p) => new(p.X, p.Y, p.Width, p.Height);
    }

    sealed class Block
    {
        public required List<State> Members;
        public double Ideal, Weight;
        public int Order;
    }

    sealed class Pool { public double Sum; public int Count; }

    readonly record struct Spacing(double SpineGap, double RowGap, double Drop, double FirstTrack);

    sealed class Geometry2
    {
        public required string Ports;
        public required JsMap<Transition, Route> Routes;
        public required List<Pt> Junctions;
        public required List<Rect> Frames;
        public double ContentBottom, ContentRight;
        public required JsMap<Transition, Placed> PlacedLabels;
        public required List<Transition> UnplacedLabels;
    }

    sealed class Scene
    {
        readonly RenderContext ctx;
        readonly JsonObject lifecycle;
        readonly JsonObject meta;
        readonly string? locale;
        readonly List<Transition> transitions;
        readonly List<string> mainPath;
        readonly List<State> authoredStates;
        readonly JsMap<string, State> states = new();
        readonly JsMap<string, int> spineIndex = new();
        readonly double smallestText;
        readonly double readableWidthBudget;
        readonly JsMap<int, Transition> spineEdges = new();
        readonly double mainPathStateWidth;
        readonly List<double> minimumSpineGaps;
        readonly JsMap<string, int> depth = new();
        readonly int rowCount;
        readonly List<List<State>> rows;
        readonly List<State> offStates;
        readonly JsMap<Transition, string> kindOf = new();
        readonly JsMap<string, Bracket> brackets = new();
        readonly JsMap<Transition, Bracket> bracketOf = new();
        readonly JsMap<string, Bracket> bracketTargets = new();
        readonly JsSet<string> outgoing = new();
        Spacing spacing = new(72, 44, 58, 34);
        readonly Geometry2 geometry;
        readonly JsSet<Transition> spacingWideners = new();
        readonly List<LegendEntry> legendEntries;
        readonly double[] viewBox;
        readonly JsMap<string, int> stateSteps = new();

        static string N(double v) => Js.Num(v);
        static bool Truthy(string? s) => !string.IsNullOrEmpty(s);
        // JS `a || b` on numbers: 0, -0 and NaN fall through.
        static double Or(double first, double second) => first == 0 || double.IsNaN(first) ? second : first;
        static double MaxOf(IEnumerable<double> values, double seed = double.NegativeInfinity)
        {
            var max = seed;
            foreach (var value in values) max = Math.Max(max, value);
            return max;
        }
        static double MinOf(IEnumerable<double> values, double seed = double.PositiveInfinity)
        {
            var min = seed;
            foreach (var value in values) min = Math.Min(min, value);
            return min;
        }
        static double Mean(IEnumerable<double> xs)
        {
            double sum = 0; var count = 0;
            foreach (var x in xs) { sum += x; count++; }
            return sum / count;
        }
        bool OnSpine(string id) => spineIndex.Has(id);
        int DepthOf(string id) => depth.Get(id);
        State Get(string id) => states.Get(id)!;
        double CxOf(string id) => Get(id).Cx;
        bool IsFinal(State state) => !outgoing.Has(state.Id);
        List<Transition> OfKind(string kind) => transitions.Where(t => kindOf.Get(t) == kind).ToList();
        static bool HasLabel(Transition t) => Truthy(t.Label) || Truthy(t.Note);
        // `transition.label || transition.note`
        static string? LabelOrNote(Transition t) => Truthy(t.Label) ? t.Label : Truthy(t.Note) ? t.Note : null;
        // sourceEvidence?.nodes?.[state.id]?.length — null until phase 4 ports repository evidence.
        bool SourceBadgeFor(string id) => ctx.SourceEvidence.Obj("nodes").Arr(id) is { Count: > 0 };

        public Scene(RenderContext ctx)
        {
            this.ctx = ctx;
            lifecycle = ctx.Diagram;
            meta = ctx.Meta!;
            locale = meta.Str("locale");
            transitions = lifecycle["transitions"].AsArrayOrEmpty().Select((node, index) => new Transition { Source = (JsonObject)node!, Index = index }).ToList();
            mainPath = lifecycle["mainPath"].AsArrayOrEmpty().Select(node => BrandMarks.JsString(node)).ToList();
            authoredStates = lifecycle["states"].AsArrayOrEmpty().Select(node => (JsonObject)node!)
                .Select(state => new State { Source = state, Id = state.Str("id") ?? "undefined", Brand = ctx.Brands.BrandMarkFor(state) }).ToList();
            foreach (var state in authoredStates) states.Set(state.Id, state);
            for (var index = 0; index < mainPath.Count; index++) spineIndex.Set(mainPath[index], index);

            ValidateStructure();

            // ---------------------------------------------------------------- measure
            foreach (var state in states.Values)
            {
                state.Width = PreferredWidth(state);
                state.Height = Layout.STATE_H;
            }
            smallestText = MinOf(states.Values.SelectMany(state =>
            {
                var fitted = Fonts(state, state.Width);
                return (double[])[fitted.Label, Truthy(state.Sublabel) ? fitted.Sublabel : double.PositiveInfinity];
            }), Layout.LABEL_FONT);
            readableWidthBudget = Math.Floor(DesktopReadability.DESKTOP_READER_DIAGRAM_WIDTH * smallestText / DesktopReadability.MIN_PROJECTED_NODE_TEXT_PX);

            // ---------------------------------------------------------------- classify

            // The first forward transition between consecutive main-path states is the
            // row itself; any other main-path pair is an arc above the row.
            foreach (var transition in transitions)
            {
                if (!spineIndex.Has(transition.From) || !spineIndex.Has(transition.To)) continue;
                var from = spineIndex.Get(transition.From);
                var to = spineIndex.Get(transition.To);
                if (to == from + 1 && !spineEdges.Has(from)) spineEdges.Set(from, transition);
            }
            mainPathStateWidth = mainPath.Sum(id => Get(id).Width);
            minimumSpineGaps = mainPath.Take(Math.Max(0, mainPath.Count - 1)).Select((_, index) =>
            {
                var edge = spineEdges.Get(index);
                return (edge is not null && HasLabel(edge) ? LabelBox(edge).Width : 0) + 26;
            }).ToList();

            // Every other state sits in a row below the main path at its distance from
            // it, so each transition stays within one row or joins two adjacent rows.
            foreach (var id in mainPath) depth.Set(id, 0);
            for (var frontier = mainPath.ToList(); frontier.Count > 0;)
            {
                var next = new List<string>();
                foreach (var id in frontier)
                {
                    foreach (var transition in transitions)
                    {
                        var other = transition.From == id ? transition.To : transition.To == id ? transition.From : null;
                        if (Truthy(other) && states.Has(other!) && !depth.Has(other!))
                        {
                            depth.Set(other!, depth.Get(id) + 1);
                            next.Add(other!);
                        }
                    }
                }
                frontier = next;
            }
            foreach (var id in states.Keys.ToList()) if (!depth.Has(id)) depth.Set(id, 1);
            rowCount = (int)Math.Max(0, MaxOf(depth.Values.Select(d => (double)d)) + 1);
            rows = Enumerable.Range(0, rowCount).Select(row => row == 0
                ? mainPath.Select(Get).ToList()
                : states.Values.Where(state => DepthOf(state.Id) == row).ToList()).ToList();
            offStates = rows.Skip(1).SelectMany(row => row).ToList();

            foreach (var transition in transitions)
            {
                var (from, to) = (DepthOf(transition.From), DepthOf(transition.To));
                if (from == 0 && to == 0) kindOf.Set(transition, spineEdges.Get(spineIndex.Get(transition.From)) == transition ? "spine" : "arc");
                else kindOf.Set(transition, from == to ? "same" : "between");
            }

            // Exits that leave several main-path phases for one state are drawn once:
            // from a composite frame around consecutive phases, or from a shared bus.
            var downByTarget = new JsMap<string, List<Transition>>();
            foreach (var transition in OfKind("between"))
            {
                if (DepthOf(transition.From) != 0) continue;
                if (!downByTarget.Has(transition.To)) downByTarget.Set(transition.To, []);
                downByTarget.Get(transition.To)!.Add(transition);
            }
            // A merged exit promises one meaning. Only a target with one eligible
            // complete-signature bucket joins a bracket; all other exits stay singles.
            foreach (var (target, list) in downByTarget.Entries)
            {
                var buckets = new JsMap<string, List<Transition>>();
                foreach (var transition in list)
                {
                    var signature = ExitSignature(transition);
                    if (!buckets.Has(signature)) buckets.Set(signature, []);
                    buckets.Get(signature)!.Add(transition);
                }
                var eligible = buckets.Values.Where(bucket => DistinctOrdered(bucket.Select(transition => transition.From)).Count >= 2).ToList();
                if (eligible.Count != 1) continue;
                var group = eligible[0];
                var sources = DistinctOrdered(group.Select(transition => transition.From));
                Js.Sort(sources, (a, b) => spineIndex.Get(a) - spineIndex.Get(b));
                var key = string.Join(NUL, sources);
                if (!brackets.Has(key)) brackets.Set(key, new Bracket { Key = key, Sources = sources });
                brackets.Get(key)!.Targets.Add(target);
                brackets.Get(key)!.Transitions.AddRange(group);
            }
            foreach (var bracket in brackets.Values)
            {
                foreach (var transition in bracket.Transitions) bracketOf.Set(transition, bracket);
            }
            // Consecutive main-path phases form a composite state: one frame around them
            // and one exit per target, like a UML superstate transition. Frames never
            // partially overlap; such a bracket keeps a shared bus under its phases.
            var frameSpans = new List<(int Lo, int Hi)>();
            foreach (var bracket in brackets.Values)
            {
                var indexes = bracket.Sources.Select(id => spineIndex.Get(id)).ToList();
                var lo = indexes.Min();
                var hi = indexes.Max();
                if (hi - lo + 1 != indexes.Count || frameSpans.Any(span => span.Lo <= hi && lo <= span.Hi)) continue;
                bracket.Frame = (lo, hi);
                frameSpans.Add((lo, hi));
            }
            foreach (var bracket in brackets.Values) foreach (var target in bracket.Targets) bracketTargets.Set(target, bracket);

            foreach (var transition in transitions) outgoing.Add(transition.From);

            // ---------------------------------------------------------------- layout

            // Each round tries ports pulled toward their partners and ports spread evenly,
            // and keeps the placed layout with the fewest bends and short jogs.
            // Labels that could not be placed at tighter spacing and prompted retries.
            Geometry2? chosen = null;
            for (var round = 0; round < Layout.SPACING_ROUNDS; round += 1)
            {
                var candidates = ((string[])["partner", "even"]).Select(ports => LayoutScene(spacing, ports)).ToList();
                var placed = candidates.Where(candidate => candidate.UnplacedLabels.Count == 0).ToList();
                Js.Sort(placed, (a, b) => RouteCost(a.Routes) - RouteCost(b.Routes));
                var best = placed.Count > 0 ? placed[0] : candidates[0];
                // layout() positions the shared states, so the winner is laid out again.
                chosen = best == candidates[^1] ? best : LayoutScene(spacing, best.Ports);
                if (placed.Count > 0) break;
                if (round == 0)
                {
                    var closest = candidates.ToList();
                    Js.Sort(closest, (a, b) => a.UnplacedLabels.Count - b.UnplacedLabels.Count);
                    foreach (var transition in closest[0].UnplacedLabels) spacingWideners.Add(transition);
                }
                var nextSpineGap = spacing.SpineGap + 28;
                // The canvas includes this whole row. Widening beyond its text budget
                // cannot succeed; keep its current gaps while other spacing still grows.
                var nextMainPathWidth = Layout.MARGIN_X * 2 + mainPathStateWidth
                    + minimumSpineGaps.Sum(minimum => Math.Max(nextSpineGap, minimum));
                spacing = new(nextMainPathWidth > readableWidthBudget ? spacing.SpineGap : nextSpineGap,
                    spacing.RowGap + 28, spacing.Drop + 14, spacing.FirstTrack + 6);
            }
            geometry = chosen!;

            // ---------------------------------------------------------------- canvas
            legendEntries = LegendCatalog();
            var canvasWidth = Math.Max(640, Math.Ceiling(geometry.ContentRight + Layout.MARGIN_X));
            var legend = Legend.LegendFootprint(legendEntries, canvasWidth - 80);
            viewBox = [canvasWidth, Math.Ceiling(geometry.ContentBottom + (legendEntries.Count > 0 ? 78 + legend.ExtraHeight : 32))];
            ValidateLayout();

            for (var index = 0; index < mainPath.Count; index++) stateSteps.Set(mainPath[index], index);
            foreach (var transition in transitions)
            {
                if (!stateSteps.Has(transition.To)) stateSteps.Set(transition.To, transition.Index + 1);
            }
        }

        // [...new Set(values)]
        static List<string> DistinctOrdered(IEnumerable<string> values)
        {
            var set = new JsSet<string>();
            foreach (var value in values) set.Add(value);
            return [.. set];
        }

        // ---------------------------------------------------------------- measure

        (double Label, double Sublabel, double Tag) Fonts(State state, double width) => (
            TextFit.FittedNodeFontSize(state.Label, BrandMarks.BrandLabelFitWidth(state.Brand, width), Layout.LabelPreferred, Layout.LabelMinimum),
            TextFit.FittedNodeFontSize(state.Sublabel, width, Layout.SublabelPreferred, Layout.SublabelMinimum),
            TextFit.FittedNodeFontSize(state.Tag, width, Layout.TagPreferred, Layout.TagMinimum));

        // Wide enough for every row at its preferred size; the label also clears the
        // top-rail decorations (sigil or step, brand, source badge) that share its row.
        double PreferredWidth(State state)
        {
            static double Need(string? text, double font) => Truthy(text) ? Utils.TextUnits(text) * font * NodeTextFit.WidthFactor + NodeTextFit.HorizontalPadding + 36 : 0;
            var left = Math.Max(Utils.SemanticSigilFootprint + 2, Truthy(state.Step) ? 26 + TextFit.MinimumNodeTextWidth(state.Step, Layout.StepFont) : 0);
            var right = (state.Brand is not null ? 26 : 4) + (SourceBadgeFor(state.Id) ? Utils.SourceBadgeFootprint : 0);
            var label = TextFit.MinimumNodeTextWidth(state.Label, Layout.LabelPreferred) + left + right + 4;
            return Math.Ceiling(Math.Min(Layout.STATE_MAX_W, Math.Max(Math.Max(Layout.STATE_MIN_W, label),
                Math.Max(Need(state.Sublabel, Layout.SublabelPreferred), Need(state.Tag, Layout.TagPreferred)))));
        }

        static (double Width, double Height) LabelBox(Transition transition)
        {
            var text = transition.Label ?? "";
            var note = transition.Note ?? "";
            var width = Math.Ceiling(Math.Max(
                text.Length > 0 ? Utils.TextUnits(text) * Layout.LABEL_FONT * 0.6 : 0,
                note.Length > 0 ? Utils.TextUnits(note) * Layout.NOTE_FONT * 0.6 : 0) + 12);
            return (width, text.Length > 0 && note.Length > 0 ? 28 : 16);
        }

        string ExitSignature(Transition transition) => string.Join(NUL, (string[])[transition.Label ?? "", transition.Note ?? "", VariantOf(transition)]);

        // ---------------------------------------------------------------- layout

        static double RouteCost(JsMap<Transition, Route> routes)
        {
            double cost = 0;
            foreach (var route in routes.Values)
            {
                var points = route.Points;
                cost += Math.Max(0, points.Count - 2) * 10;
                for (var index = 1; index < points.Count; index += 1)
                {
                    var run = Math.Abs(points[index].X - points[index - 1].X);
                    if (points.Count > 2 && Math.Abs(points[index].Y - points[index - 1].Y) < 0.5) cost += run * 0.1 + (run < 30 ? 40 : 0);
                }
            }
            return cost;
        }

        static string Slot(string id, string side) => $"{id}|{side}";

        Geometry2 LayoutScene(Spacing spacing, string portMode)
        {
            var (spineGap, rowGap, drop, firstTrack) = spacing;
            // Main-path x positions: each gap fits its row label.
            var cursor = Layout.MARGIN_X;
            for (var index = 0; index < mainPath.Count; index++)
            {
                var state = Get(mainPath[index]);
                state.X = cursor;
                state.Cx = cursor + state.Width / 2;
                cursor += state.Width + Math.Max(spineGap, index < minimumSpineGaps.Count ? minimumSpineGaps[index] : 26);
            }
            for (var row = 1; row < rowCount; row += 1) PlaceRow(row, rowGap);

            // Shift horizontally so the leftmost element keeps the margin.
            var minX = MinOf(states.Values.Select(state => state.X));
            var dx = Layout.MARGIN_X - minX;
            foreach (var state in states.Values) { state.X += dx; state.Cx += dx; }

            var arcs = AssignArcLevels();
            var maxLevel = (int)MaxOf(arcs.Select(arc => (double)arc.Level), 0);
            var arcLabelReserve = arcs.Any(arc => HasLabel(arc.Transition)) ? 12 : 0;
            var spineTop = Layout.MARGIN_TOP + (maxLevel != 0 ? Layout.ARC_BASE + (maxLevel - 1) * Layout.ARC_STEP + arcLabelReserve : 0);

            var ports = new JsMap<string, JsMap<object, Port>>();
            Port? PortOf(string id, string side, object key) => ports.Get(Slot(id, side))?.Get(key);
            void RequestPort(string id, string side, object key, double otherX, int order)
            {
                var slot = Slot(id, side);
                if (!ports.Has(slot)) ports.Set(slot, new JsMap<object, Port>());
                if (!ports.Get(slot)!.Has(key)) ports.Get(slot)!.Set(key, new Port { Key = key, OtherX = otherX, Order = order, X = 0 });
            }

            // Connectors cross the gap below row `gap`: single transitions between two
            // rows, composite frame exits, shared buses, and loops between two
            // non-adjacent states of one row.
            var connectors = new List<Connector>();
            foreach (var transition in OfKind("between"))
            {
                if (bracketOf.Has(transition)) continue;
                var down = DepthOf(transition.From) < DepthOf(transition.To);
                var upperId = down ? transition.From : transition.To;
                var lowerId = down ? transition.To : transition.From;
                connectors.Add(new Connector { Kind = "single", Transition = transition, Down = down, UpperId = upperId, LowerId = lowerId, Gap = DepthOf(upperId), Order = transition.Index });
            }
            var frames = new List<Rect>();
            foreach (var bracket in brackets.Values)
            {
                var order = bracket.Transitions[0].Index;
                if (bracket.Frame is not { } frame)
                {
                    connectors.Add(new Connector { Kind = "bracket", Bracket = bracket, Gap = 0, Order = order });
                    continue;
                }
                var first = Get(mainPath[frame.Lo]);
                var last = Get(mainPath[frame.Hi]);
                var rect = new Rect(first.X - Layout.FRAME_PAD, spineTop - Layout.FRAME_PAD, last.X + last.Width - first.X + Layout.FRAME_PAD * 2, Layout.STATE_H + Layout.FRAME_PAD * 2);
                frames.Add(rect);
                foreach (var target in bracket.Targets) connectors.Add(new Connector { Kind = "frame", Bracket = bracket, Rect = rect, LowerId = target, Gap = 0, Order = order });
            }
            // Neighbours in one lower row connect side to side; others loop underneath.
            var rowOrder = rows.Select(list =>
            {
                var sorted = list.ToList();
                Js.Sort(sorted, (a, b) => a.X - b.X);
                return sorted.Select(state => state.Id).ToList();
            }).ToList();
            bool Adjacent(Transition transition) => transition.From != transition.To
                && Math.Abs(rowOrder[DepthOf(transition.From)].IndexOf(transition.From) - rowOrder[DepthOf(transition.From)].IndexOf(transition.To)) == 1;
            var sides = OfKind("same").Where(Adjacent).ToList();
            // Side labels ride on the line for groups of three or more. Reserve their
            // actual height before placing rows: horizontal gap growth cannot clear a
            // label trapped between two full-width parallel strokes.
            var sideGroups = new JsMap<string, List<Transition>>();
            foreach (var transition in sides)
            {
                var pair = new List<string> { transition.From, transition.To };
                Js.SortDefault(pair);
                var key = string.Join(NUL, pair);
                if (!sideGroups.Has(key)) sideGroups.Set(key, []);
                sideGroups.Get(key)!.Add(transition);
            }
            var sideLanes = new List<(List<Transition> Ordered, double Step)>();
            var rowHeights = rows.Select(_ => Layout.STATE_H).ToList();
            foreach (var group in sideGroups.Values)
            {
                bool Rightward(Transition transition) => Get(transition.From).X < Get(transition.To).X;
                var ordered = group.Where(Rightward).Concat(group.Where(transition => !Rightward(transition))).ToList();
                if (ordered.Count > 5)
                {
                    var message = $"{N(ordered.Count)} transitions between the neighbouring states \"{ordered[0].From}\" and \"{ordered[0].To}\" cannot spread apart on the shared edge; at most five stay legible.";
                    throw ctx.Diagnostics.ThrowDiagnosticProblems("Lifecycle validation failed", [message],
                        subject: new JsonObject { ["diagramType"] = "lifecycle" },
                        details: [StructureProblem("lifecycle/crowded-side-transitions", message,
                            new JsonObject { ["collection"] = "transitions", ["from"] = ordered[0].From, ["to"] = ordered[0].To },
                            ["merge the triggers into one transition label"])]);
                }
                var step = Math.Min(20, 48 / (double)(ordered.Count - 1));
                if (ordered.Count > 2)
                {
                    var heights = ordered.Select(transition => HasLabel(transition) ? LabelBox(transition).Height : 0).ToList();
                    step = Math.Max(step, Math.Max(
                        MaxOf(heights.Select(height => height != 0 ? height / 2 + Layout.CLEARANCE : 0)),
                        MaxOf(heights.Skip(1).Select((height, index) => height != 0 && heights[index] != 0 ? (height + heights[index]) / 2 + 4 : 0))));
                    var row = DepthOf(ordered[0].From);
                    // Keep every side port eight pixels inside the rounded state corners.
                    rowHeights[row] = Math.Max(rowHeights[row], step * (ordered.Count - 1) + 16);
                }
                sideLanes.Add((ordered, step));
            }
            foreach (var transition in OfKind("same").Where(transition => !Adjacent(transition)))
            {
                connectors.Add(new Connector { Kind = "loop", Transition = transition, Gap = DepthOf(transition.From), Order = transition.Index });
            }

            foreach (var connector in connectors)
            {
                if (connector.Kind == "single")
                {
                    RequestPort(connector.UpperId!, "bottom", connector, CxOf(connector.LowerId!), connector.Order);
                    RequestPort(connector.LowerId!, "top", connector, CxOf(connector.UpperId!), connector.Order);
                }
                else if (connector.Kind == "frame")
                {
                    RequestPort(connector.LowerId!, "top", connector, connector.Rect.X + connector.Rect.Width / 2, connector.Order);
                }
                else if (connector.Kind == "loop")
                {
                    var (from, to) = (connector.Transition!.From, connector.Transition.To);
                    if (from == to)
                    {
                        RequestPort(from, "bottom", $"{N(connector.Order)}:out", double.PositiveInfinity, 0);
                        RequestPort(from, "bottom", $"{N(connector.Order)}:in", double.PositiveInfinity, 1);
                    }
                    else
                    {
                        RequestPort(from, "bottom", $"{N(connector.Order)}:out", CxOf(to), connector.Order);
                        RequestPort(to, "bottom", $"{N(connector.Order)}:in", CxOf(from), connector.Order);
                    }
                }
                else
                {
                    var (sources, targets) = (connector.Bracket!.Sources, connector.Bracket.Targets);
                    var dropCenter = targets.Sum(CxOf) / targets.Count;
                    var sourceCenter = sources.Sum(CxOf) / sources.Count;
                    foreach (var id in sources) RequestPort(id, "bottom", connector, dropCenter, connector.Order);
                    foreach (var id in targets) RequestPort(id, "top", connector, sourceCenter, connector.Order);
                }
            }
            foreach (var arc in arcs)
            {
                var transition = arc.Transition;
                if (transition.From == transition.To)
                {
                    RequestPort(transition.From, "top", $"{arc.Key}:out", double.PositiveInfinity, 0);
                    RequestPort(transition.From, "top", $"{arc.Key}:in", double.PositiveInfinity, 1);
                }
                else
                {
                    RequestPort(transition.From, "top", $"{arc.Key}:out", CxOf(transition.To), 0);
                    RequestPort(transition.To, "top", $"{arc.Key}:in", CxOf(transition.From), 0);
                }
            }
            DistributePorts(ports, arcs, portMode);
            PlaceFrameExits(connectors, ports, PortOf);

            // Each gap assigns its horizontal runs to tracks, then the next row follows.
            var runs = connectors.Select(connector =>
            {
                if (connector.Kind == "loop")
                {
                    var (from, to) = (connector.Transition!.From, connector.Transition.To);
                    var tops = new List<double> { PortOf(from, "bottom", $"{N(connector.Order)}:out")!.X, PortOf(to, "bottom", $"{N(connector.Order)}:in")!.X };
                    return new Run { Connector = connector, Tops = tops, Bottoms = [], Lo = MinOf(tops), Hi = MaxOf(tops), Straight = false };
                }
                if (connector.Kind == "bracket")
                {
                    var (sources, targets) = (connector.Bracket!.Sources, connector.Bracket.Targets);
                    var tops = sources.Select(id => PortOf(id, "bottom", connector)!.X).ToList();
                    var bottoms = targets.Select(id => PortOf(id, "top", connector)!.X).ToList();
                    var xs = tops.Concat(bottoms).ToList();
                    return new Run { Connector = connector, Tops = tops, Bottoms = bottoms, Lo = MinOf(xs), Hi = MaxOf(xs), Straight = false };
                }
                var top = connector.Kind == "frame" ? connector.ExitX!.Value : PortOf(connector.UpperId!, "bottom", connector)!.X;
                var bottom = PortOf(connector.LowerId!, "top", connector)!.X;
                return new Run { Connector = connector, Tops = [top], Bottoms = [bottom], Lo = Math.Min(top, bottom), Hi = Math.Max(top, bottom), Straight = Math.Abs(top - bottom) < 0.5 };
            }).ToList();
            var rowTop = new List<double> { spineTop };
            var trackY = new List<Func<int, double>>();
            for (var row = 0; row < rowCount; row += 1)
            {
                foreach (var state in rows[row])
                {
                    state.Y = rowTop[row];
                    state.Height = rowHeights[row];
                    state.Cy = rowTop[row] + state.Height / 2;
                }
                var bottom = rowTop[row] + rowHeights[row];
                var count = AssignTracks(runs.Where(run => run.Connector.Gap == row).ToList());
                trackY.Add(track => bottom + firstTrack + track * Layout.TRACK_STEP);
                if (row + 1 < rowCount) rowTop.Add((count != 0 ? trackY[row](count - 1) : bottom + firstTrack) + drop);
            }
            double RowBottom(int row) => rowTop[row] + rowHeights[row];

            // Routes, keyed by transition.
            var routes = new JsMap<Transition, Route>();
            var junctions = new List<Pt>();
            foreach (var (index, transition) in spineEdges.Entries)
            {
                var from = Get(mainPath[index]);
                var to = Get(mainPath[index + 1]);
                routes.Set(transition, new Route { Points = [new Pt(from.X + from.Width, from.Cy), new Pt(to.X, to.Cy)], Segment = 0, LabelMode = "above" });
            }
            foreach (var arc in arcs)
            {
                var transition = arc.Transition;
                var @out = PortOf(transition.From, "top", $"{arc.Key}:out")!.X;
                var inn = PortOf(transition.To, "top", $"{arc.Key}:in")!.X;
                var y = spineTop - Layout.ARC_BASE - (arc.Level - 1) * Layout.ARC_STEP;
                routes.Set(transition, new Route { Points = [new Pt(@out, spineTop), new Pt(@out, y), new Pt(inn, y), new Pt(inn, spineTop)], Segment = 1, LabelMode = "on" });
            }
            foreach (var run in runs)
            {
                var connector = run.Connector;
                var gap = connector.Gap;
                var y = trackY[gap](run.Track ?? 0);
                var upper = RowBottom(gap);
                var lower = gap + 1 < rowTop.Count ? rowTop[gap + 1] : double.NaN;
                if (connector.Kind == "loop")
                {
                    var (a, b) = (run.Tops[0], run.Tops[1]);
                    routes.Set(connector.Transition!, new Route { Points = [new Pt(a, upper), new Pt(a, y), new Pt(b, y), new Pt(b, upper)], Segment = 1, LabelMode = "on" });
                    continue;
                }
                if (connector.Kind is "single" or "frame")
                {
                    var top = run.Tops[0];
                    var bottom = run.Bottoms[0];
                    var start = connector.Kind == "frame" ? connector.Rect.Y + connector.Rect.Height : upper;
                    List<Pt> down = run.Straight
                        ? [new Pt(top, start), new Pt(top, lower)]
                        : [new Pt(top, start), new Pt(top, y), new Pt(bottom, y), new Pt(bottom, lower)];
                    if (connector.Kind == "single")
                    {
                        var points = connector.Down ? down : Enumerable.Reverse(down).ToList();
                        routes.Set(connector.Transition!, new Route { Points = points, Segment = connector.Down ? points.Count - 2 : 0, LabelMode = "beside" });
                    }
                    else
                    {
                        var index = 0;
                        foreach (var transition in connector.Bracket!.Transitions.Where(transition => transition.To == connector.LowerId))
                        {
                            routes.Set(transition, new Route { Points = down, Segment = down.Count - 2, LabelMode = index != 0 ? "none" : "beside", Bracket = connector.Bracket });
                            index++;
                        }
                    }
                    continue;
                }
                var bracket = connector.Bracket!;
                var labelled = new JsSet<string>();
                foreach (var transition in bracket.Transitions)
                {
                    var tick = PortOf(transition.From, "bottom", connector)!.X;
                    var dropX = PortOf(transition.To, "top", connector)!.X;
                    List<Pt> points = Math.Abs(tick - dropX) < 0.5
                        ? [new Pt(tick, upper), new Pt(tick, lower)]
                        : [new Pt(tick, upper), new Pt(tick, y), new Pt(dropX, y), new Pt(dropX, lower)];
                    var owner = !labelled.Has(transition.To);
                    labelled.Add(transition.To);
                    routes.Set(transition, new Route { Points = points, Segment = points.Count - 2, LabelMode = owner ? "beside" : "none", Bracket = bracket });
                }
                foreach (var x in run.Tops.Concat(run.Bottoms))
                {
                    if (x > run.Lo + 0.5 && x < run.Hi - 0.5) junctions.Add(new Pt(x, y));
                }
            }
            // Parallel transitions between two neighbours spread symmetrically around
            // the shared edge, left-to-right ones first; a reciprocal pair lands at ±10.
            foreach (var (ordered, step) in sideLanes)
            {
                for (var index = 0; index < ordered.Count; index++)
                {
                    var transition = ordered[index];
                    var from = Get(transition.From);
                    var to = Get(transition.To);
                    var y = from.Cy + (index - (ordered.Count - 1) / 2.0) * step;
                    List<Pt> points = from.X < to.X ? [new Pt(from.X + from.Width, y), new Pt(to.X, y)] : [new Pt(from.X, y), new Pt(to.X + to.Width, y)];
                    // With three or more parallels the inner strokes leave no lane for a
                    // floating label, so their labels ride on the line like arc labels.
                    routes.Set(transition, new Route { Points = points, Segment = 0, LabelMode = ordered.Count > 2 ? "on" : "above" });
                }
            }

            var stateRight = MaxOf(states.Values.Select(state => state.X + state.Width));
            var (placedLabels, unplacedLabels) = PlaceLabels(routes, stateRight);
            var boxes = states.Values.Select(state => state.Rect).Concat(placedLabels.Values.Select(placed => (Rect)placed)).ToList();
            var contentBottom = Math.Max(
                MaxOf(boxes.Select(box => box.Y + box.Height)),
                MaxOf(routes.Values.SelectMany(route => route.Points.Select(point => point.Y))));
            var contentRight = MaxOf(boxes.Select(box => box.X + box.Width));
            return new Geometry2
            {
                Ports = portMode, Routes = routes, Junctions = junctions, Frames = frames, ContentBottom = contentBottom, ContentRight = contentRight,
                PlacedLabels = placedLabels, UnplacedLabels = unplacedLabels,
            };
        }

        // A composite exit leaves the frame directly above its target when possible,
        // clear of the inner phases' own connectors and of sibling exits.
        void PlaceFrameExits(List<Connector> connectors, JsMap<string, JsMap<object, Port>> ports, Func<string, string, object, Port?> portOf)
        {
            var frameConnectors = connectors.Where(connector => connector.Kind == "frame").ToList();
            foreach (var connector in frameConnectors)
            {
                var (rect, bracket) = (connector.Rect, connector.Bracket!);
                var inner = mainPath.Skip(bracket.Frame!.Value.Lo).Take(bracket.Frame.Value.Hi - bracket.Frame.Value.Lo + 1)
                    .SelectMany(id => (ports.Get(Slot(id, "bottom"))?.Values ?? []).Select(port => port.X)).ToList();
                // `other.rect === rect`: frame connectors of one bracket share its rect object.
                var taken = inner.Concat(frameConnectors.Where(other => other.Bracket == bracket && other.ExitX is not null).Select(other => other.ExitX!.Value)).ToList();
                var target = portOf(connector.LowerId!, "top", connector)!;
                var low = rect.X + 16;
                var high = rect.X + rect.Width - 16;
                var wanted = Math.Min(high, Math.Max(low, target.X));
                bool Free(double x) => x >= low && x <= high && taken.All(other => Math.Abs(other - x) >= 14);
                var exitX = wanted;
                for (var step = 0; step <= 40 && !Free(exitX); step += 1)
                {
                    exitX = ((double[])[wanted + step * 7, wanted - step * 7]).Cast<double?>().FirstOrDefault(x => Free(x!.Value)) ?? exitX;
                }
                connector.ExitX = exitX;
                // Snap the target's port to the exit when that keeps its spacing.
                var delta = Math.Abs(exitX - target.X);
                if (delta >= 0.5 && delta < 24)
                {
                    var state = Get(connector.LowerId!);
                    var others = ports.Get(Slot(connector.LowerId!, "top"))!.Values.Where(port => port != target).ToList();
                    if (exitX >= state.X + 12 && exitX <= state.X + state.Width - 12 && others.All(port => Math.Abs(port.X - exitX) >= 12))
                    {
                        target.X = exitX;
                    }
                    else if (delta < 16)
                    {
                        connector.ExitX = ((double[])[target.X, exitX + Math.Sign(Or(exitX - target.X, 1)) * (16 - delta)]).Cast<double?>().FirstOrDefault(x => Free(x!.Value)) ?? exitX;
                    }
                }
            }
        }

        // Each lower row starts every state under the states it connects to in the
        // row above; overlapping blocks merge and centre on their combined ideal.
        void PlaceRow(int row, double rowGap)
        {
            var members = rows[row];
            var ideal = new JsMap<string, double>();
            foreach (var state in members)
            {
                var xs = transitions.SelectMany(transition =>
                {
                    var other = transition.To == state.Id ? transition.From : transition.From == state.Id ? transition.To : null;
                    return Truthy(other) && depth.Has(other!) && DepthOf(other!) == row - 1 ? (double[])[Get(other!).Cx] : [];
                }).ToList();
                if (xs.Count > 0) ideal.Set(state.Id, xs.Sum() / xs.Count);
            }
            if (row == 1)
            {
                foreach (var bracket in brackets.Values)
                {
                    var center = bracket.Sources.Sum(id => Get(id).Cx) / bracket.Sources.Count;
                    foreach (var id in bracket.Targets) ideal.Set(id, center);
                }
            }
            var fallback = MaxOf(mainPath.Select(id => Get(id).Cx)) + 1;
            var blocks = new List<Block>();
            var grouped = new JsSet<string>();
            foreach (var state in members)
            {
                if (grouped.Has(state.Id)) continue;
                var bracket = row == 1 ? bracketTargets.Get(state.Id) : null;
                var list = bracket is not null ? bracket.Targets.Select(Get).ToList() : [state];
                foreach (var member in list) grouped.Add(member.Id);
                blocks.Add(new Block { Members = list, Ideal = ideal.Has(state.Id) ? ideal.Get(state.Id) : fallback, Weight = ideal.Has(state.Id) ? list.Count : 0.05, Order = members.IndexOf(state) });
            }
            // Neighbours joined by a labelled transition keep room for its label.
            double GapBetween(State left, State right) => MaxOf(transitions
                .Where(transition => HasLabel(transition) && ((transition.From == left.Id && transition.To == right.Id)
                    || (transition.From == right.Id && transition.To == left.Id)))
                .Select(transition => LabelBox(transition).Width + 32), rowGap);
            Js.Sort(blocks, (a, b) => Or(a.Ideal - b.Ideal, a.Order - b.Order));
            // Lay a cluster of blocks side by side and return each block's centre offset.
            (double Width, List<double> Offsets) Arrange(List<Block> list)
            {
                var offsets = new List<double>();
                double x = 0;
                State? previous = null;
                foreach (var block in list)
                {
                    var start = x;
                    foreach (var member in block.Members)
                    {
                        if (previous is not null) x += GapBetween(previous, member);
                        member.Offset = x;
                        x += member.Width;
                        previous = member;
                    }
                    offsets.Add((start + x) / 2);
                }
                return (x, offsets.Select(offset => offset - x / 2).ToList());
            }
            // A cluster's centre is the weighted mean of (ideal - offset) over its
            // blocks, which minimises their squared drift.
            (double Width, double X) Centre(List<Block> list)
            {
                var (width, offsets) = Arrange(list);
                var weight = list.Sum(block => block.Weight);
                return (width, list.Select((block, index) => block.Weight * (block.Ideal - offsets[index])).Sum() / weight);
            }
            var clusters = new List<List<Block>>();
            foreach (var block in blocks)
            {
                clusters.Add([block]);
                while (clusters.Count > 1)
                {
                    var last = clusters[^1];
                    var previous = clusters[^2];
                    var a = Centre(previous);
                    var b = Centre(last);
                    if (a.X + a.Width / 2 + GapBetween(previous[^1].Members[^1], last[0].Members[0]) <= b.X - b.Width / 2) break;
                    clusters.RemoveRange(clusters.Count - 2, 2);
                    clusters.Add([.. previous, .. last]);
                }
            }
            foreach (var cluster in clusters)
            {
                var (width, x) = Centre(cluster);
                Arrange(cluster);
                foreach (var block in cluster)
                {
                    foreach (var member in block.Members)
                    {
                        member.X = x - width / 2 + member.Offset;
                        member.Cx = member.X + member.Width / 2;
                    }
                }
            }
        }

        // Arcs nest by span: a shorter arc stays lower; overlapping spans never share a level.
        List<Arc> AssignArcLevels()
        {
            var arcs = OfKind("arc").Select((transition, index) =>
            {
                var a = spineIndex.Get(transition.From);
                var b = spineIndex.Get(transition.To);
                return new Arc { Transition = transition, Key = $"arc{N(transition.Index)}", Lo = Math.Min(a, b), Hi = Math.Max(a, b), Index = index };
            }).ToList();
            var sorted = arcs.ToList();
            Js.Sort(sorted, (left, right) => Or(Or((left.Hi - left.Lo) - (right.Hi - right.Lo), left.Lo - right.Lo), left.Index - right.Index));
            foreach (var arc in sorted)
            {
                var blocking = sorted.Where(other => other != arc && other.Level != 0
                    && ((other.Lo < arc.Hi && arc.Lo < other.Hi) || (other.Lo == other.Hi && arc.Lo < other.Lo && other.Lo < arc.Hi)
                        || (other.Lo == arc.Lo && other.Hi == arc.Hi))).ToList();
                arc.Level = 1 + (int)MaxOf(blocking.Select(other => (double)other.Level), 0);
            }
            return arcs;
        }

        // Port order on one side avoids crossings: connections toward the left use
        // the left part of the side, connections toward the right the right part.
        // Nested arcs put the outer (higher) arc's port furthest out and spread evenly;
        // every other port moves toward its partner so connectors can drop straight.
        void DistributePorts(JsMap<string, JsMap<object, Port>> ports, List<Arc> arcs, string mode)
        {
            var arcLevel = new JsMap<string, int>();
            foreach (var arc in arcs) { arcLevel.Set($"{arc.Key}:out", arc.Level); arcLevel.Set($"{arc.Key}:in", arc.Level); }
            // Lower-row tops follow the upper ports they connect to, so place them last.
            bool LowerTop(string slot) => slot.EndsWith("|top", StringComparison.Ordinal) && !OnSpine(slot.Split('|')[0]);
            var slots = ports.Entries.ToList();
            Js.Sort(slots, (a, b) => (LowerTop(a.Key) ? 1 : 0) - (LowerTop(b.Key) ? 1 : 0));
            foreach (var (slot, entries) in slots)
            {
                var id = slot.Split('|')[0];
                var state = Get(id);
                var list = entries.Values.ToList();
                if (LowerTop(slot))
                {
                    foreach (var port in list)
                    {
                        var upper = port.Key is Connector { Kind: "single" } connector ? ports.Get(Slot(connector.UpperId!, "bottom"))?.Get(port.Key) : null;
                        if (upper is not null) port.OtherX = upper.X;
                    }
                }
                Js.Sort(list, (left, right) =>
                {
                    var leftDirection = Math.Sign(left.OtherX - state.Cx);
                    var rightDirection = Math.Sign(right.OtherX - state.Cx);
                    if (leftDirection != rightDirection) return leftDirection - rightDirection;
                    int? leftLevel = left.Key is string leftKey && arcLevel.Has(leftKey) ? arcLevel.Get(leftKey) : null;
                    int? rightLevel = right.Key is string rightKey && arcLevel.Has(rightKey) ? arcLevel.Get(rightKey) : null;
                    if (leftLevel is { } ll && rightLevel is { } rl && ll != rl)
                    {
                        // An outer arc's riser must stay outside every inner span it rises past.
                        return leftDirection < 0 ? ll - rl : rl - ll;
                    }
                    return Or(left.OtherX - right.OtherX, left.Order - right.Order);
                });
                var usable = state.Width - Layout.PORT_PAD * 2;
                var step = list.Count > 1 ? Math.Min(Layout.PORT_STEP, usable / (list.Count - 1)) : 0;
                var xs = mode == "even" || (OnSpine(id) && slot.EndsWith("|top", StringComparison.Ordinal))
                    ? list.Select((_, index) => state.Cx - (step * (list.Count - 1)) / 2 + index * step).ToList()
                    : FitPorts(list.Select(port => port.OtherX).ToList(), step, state.X + Layout.PORT_PAD, state.X + state.Width - Layout.PORT_PAD);
                for (var index = 0; index < list.Count; index++) list[index].X = Js.Round(xs[index] * 10) / 10;
            }
            SnapPorts(ports, mode);
        }

        // Ordered ports as close to their wanted x as the step and side allow: a pool
        // of adjacent violators (isotonic regression on wanted - index * step).
        static List<double> FitPorts(List<double> wanted, double step, double low, double high)
        {
            var n = wanted.Count;
            var top = high - step * (n - 1);
            double Clamp(double x) => Math.Min(Math.Max(x, low), Math.Max(low, top));
            var pools = new List<Pool>();
            for (var index = 0; index < n; index++)
            {
                var x = wanted[index];
                pools.Add(new Pool { Sum = (double.IsFinite(x) ? x : x > 0 ? high : low) - index * step, Count = 1 });
                while (pools.Count > 1 && pools[^2].Sum / pools[^2].Count > pools[^1].Sum / pools[^1].Count)
                {
                    var last = pools[^1];
                    pools.RemoveAt(pools.Count - 1);
                    pools[^1].Sum += last.Sum;
                    pools[^1].Count += last.Count;
                }
            }
            return pools.SelectMany(pool => Enumerable.Repeat(Clamp(pool.Sum / pool.Count), pool.Count)).Select((x, index) => x + index * step).ToList();
        }

        // A nearly vertical connector becomes exactly vertical when a port can move
        // without crowding its neighbours; otherwise its jog widens to a readable run.
        void SnapPorts(JsMap<string, JsMap<object, Port>> ports, string mode)
        {
            bool Fits(string id, string side, Port moving, double x)
            {
                var state = Get(id);
                return x >= state.X + Layout.PORT_PAD - 8 && x <= state.X + state.Width - Layout.PORT_PAD + 8
                    && ports.Get(Slot(id, side))!.Values.All(other => other == moving || Math.Abs(other.X - x) >= 12);
            }
            foreach (var (slot, entries) in ports.Entries)
            {
                if (!slot.EndsWith("|top", StringComparison.Ordinal)) continue;
                var lowerId = slot.Split('|')[0];
                if (OnSpine(lowerId)) continue;
                // Validate the final group, since neighboring ports may move together.
                var group = entries.Values.ToList();
                Js.Sort(group, (a, b) => a.X - b.X);
                var state = Get(lowerId);
                var targets = group.Select(port => port.Key is Connector { Kind: "single" } connector
                    ? ports.Get(Slot(connector.UpperId!, "bottom"))?.Get(port.Key)?.X : null).ToList();
                if (targets.Select((x, index) => x is { } value && double.IsFinite(value)
                    && value >= state.X + Layout.PORT_PAD - 8 && value <= state.X + state.Width - Layout.PORT_PAD + 8
                    && (index == 0 || value - targets[index - 1]!.Value >= 12)).All(ok => ok))
                {
                    for (var index = 0; index < group.Count; index++) group[index].X = targets[index]!.Value;
                    continue;
                }
                foreach (var port in entries.Values)
                {
                    var connector = port.Key as Connector;
                    if (connector?.Kind == "single")
                    {
                        var upperPort = ports.Get(Slot(connector.UpperId!, "bottom"))!.Get(connector)!;
                        var delta = Math.Abs(upperPort.X - port.X);
                        if (delta < 0.5 || (delta >= 16 && mode == "even")) continue;
                        if (Fits(lowerId, "top", port, upperPort.X)) port.X = upperPort.X;
                        else if (delta >= 16) continue;
                        else if (Fits(connector.UpperId!, "bottom", upperPort, port.X)) upperPort.X = port.X;
                        else
                        {
                            var away = Or(Math.Sign(port.X - upperPort.X), 1);
                            var pushed = ((double[])[upperPort.X + away * 16, upperPort.X - away * 16]).Cast<double?>().FirstOrDefault(x => Fits(lowerId, "top", port, x!.Value));
                            if (pushed is { } value) port.X = value;
                        }
                    }
                    else if (connector?.Kind == "bracket")
                    {
                        // A drop next to a tick would leave a micro run on the shared bus.
                        var ticks = connector.Bracket!.Sources.Select(id => ports.Get(Slot(id, "bottom"))!.Get(connector)!.X).ToList();
                        var near = ticks.Cast<double?>().FirstOrDefault(x => Math.Abs(x!.Value - port.X) >= 0.5 && Math.Abs(x.Value - port.X) < 16);
                        if (near is { } value && Fits(lowerId, "top", port, value)) port.X = value;
                    }
                }
            }
        }

        // Greedy interval tracks for one gap, trying several orderings and keeping
        // the one with the fewest crossings.
        static int AssignTracks(List<Run> runs)
        {
            var needs = runs.Where(run => !run.Straight).ToList();
            if (needs.Count == 0) return 0;
            static double Direction(Run run) => run.Bottoms.Count > 0 ? Math.Sign(Mean(run.Bottoms) - Mean(run.Tops)) : 0;
            static double Start(Run run) => Mean(run.Tops);
            static double ByStart(Run a, Run b) => Direction(a) >= 0 ? Start(b) - Start(a) : Start(a) - Start(b);
            static double LoopsFirst(Run a, Run b) => (a.Bottoms.Count > 0 ? 1 : 0) - (b.Bottoms.Count > 0 ? 1 : 0);
            List<Run> Sorted(Func<Run, Run, double> compare)
            {
                var copy = needs.ToList();
                Js.Sort(copy, compare);
                return copy;
            }
            var orderings = new List<List<Run>>
            {
                Sorted((a, b) => Or(Direction(b) - Direction(a), ByStart(a, b))),
                Sorted((a, b) => Or(Direction(a) - Direction(b), ByStart(a, b))),
                Sorted((a, b) => (a.Hi - a.Lo) - (b.Hi - b.Lo)),
                Sorted((a, b) => Or(LoopsFirst(a, b), (a.Hi - a.Lo) - (b.Hi - b.Lo))),
                Sorted((a, b) => Or(-LoopsFirst(a, b), (a.Hi - a.Lo) - (b.Hi - b.Lo))),
            };
            (JsMap<Run, int> Assignment, int Crossings, int Count)? best = null;
            foreach (var ordering in orderings)
            {
                var tracks = new List<List<Run>>();
                var assignment = new JsMap<Run, int>();
                foreach (var run in ordering)
                {
                    var track = 0;
                    while (track < tracks.Count && tracks[track].Any(other => other.Lo - 10 < run.Hi && run.Lo < other.Hi + 10)) track += 1;
                    if (track == tracks.Count) tracks.Add([]);
                    tracks[track].Add(run);
                    assignment.Set(run, track);
                }
                var crossings = CountCrossings(runs, assignment);
                if (best is null || crossings < best.Value.Crossings || (crossings == best.Value.Crossings && tracks.Count < best.Value.Count))
                {
                    best = (assignment, crossings, tracks.Count);
                }
            }
            foreach (var run in needs) run.Track = best!.Value.Assignment.Get(run);
            return best!.Value.Count;
        }

        static int CountCrossings(List<Run> runs, JsMap<Run, int> assignment)
        {
            // Verticals run from the upper row (y 0) to a track, then on to the lower row (y 1000).
            var pieces = runs.Select(run =>
            {
                if (!assignment.Has(run)) return (Verticals: new List<(double X, double Y1, double Y2)> { (run.Tops[0], 0, 1000) }, Horizontals: new List<(double Y, double X1, double X2)>());
                var y = 10 + (double)assignment.Get(run);
                return (
                    Verticals: run.Tops.Select(x => (X: x, Y1: 0.0, Y2: y)).Concat(run.Bottoms.Select(x => (X: x, Y1: y, Y2: 1000.0))).ToList(),
                    Horizontals: new List<(double Y, double X1, double X2)> { (y, run.Lo, run.Hi) });
            }).ToList();
            var count = 0;
            for (var left = 0; left < pieces.Count; left += 1)
            {
                for (var right = 0; right < pieces.Count; right += 1)
                {
                    if (left == right) continue;
                    foreach (var (x, y1, y2) in pieces[left].Verticals)
                    {
                        foreach (var (y, x1, x2) in pieces[right].Horizontals)
                        {
                            if (x > x1 + 0.5 && x < x2 - 0.5 && y > y1 && y < y2) count += 1;
                        }
                    }
                }
            }
            return count;
        }

        // Labels sit beside the segment that enters the target (or leaves the lower
        // row), above main-path arrows, and on arc and lower-loop runs. Every
        // candidate keeps clear of other routes, states and labels.
        (JsMap<Transition, Placed> PlacedLabels, List<Transition> UnplacedLabels) PlaceLabels(JsMap<Transition, Route> routes, double contentRight)
        {
            var placedLabels = new JsMap<Transition, Placed>();
            var unplacedLabels = new List<Transition>();
            List<Segment> SegmentsOf(Transition transition)
            {
                var points = routes.Get(transition)?.Points ?? [];
                return points.Skip(1).Select((end, index) => new Segment(points[index], end)).ToList();
            }
            List<Transition> Siblings(Transition transition)
            {
                var bracket = routes.Get(transition)?.Bracket;
                return bracket is not null ? bracket.Transitions.Where(other => other.To == transition.To).ToList() : [transition];
            }
            List<Transition> Others(Transition transition)
            {
                var siblings = Siblings(transition);
                return transitions.Where(other => !siblings.Contains(other)).ToList();
            }
            foreach (var transition in transitions)
            {
                var route = routes.Get(transition);
                if (route is null || route.LabelMode == "none" || !HasLabel(transition)) continue;
                var (width, height) = LabelBox(transition);
                var (start, end) = (route.Points[route.Segment], route.Points[route.Segment + 1]);
                var candidates = new List<Placed>();
                Placed At(double cx, double cy) => new(cx - width / 2, cy - height / 2, width, height, cx, cy);
                if (route.LabelMode == "above")
                {
                    var mid = (start.X + end.X) / 2;
                    candidates.Add(At(mid, start.Y - Layout.LABEL_GAP - height / 2));
                    candidates.Add(At(mid, start.Y + Layout.LABEL_GAP + height / 2));
                }
                else if (route.LabelMode == "on")
                {
                    foreach (var fraction in (double[])[0.5, 0.35, 0.65, 0.2, 0.8])
                    {
                        candidates.Add(At(start.X + (end.X - start.X) * fraction, start.Y));
                    }
                }
                else
                {
                    // `a !== start` compares point arrays by reference: only the segment's own start index is excluded.
                    var verticals = route.Points.Skip(1).Select((point, index) => (A: route.Points[index], B: point, Index: index))
                        .Where(pair => Math.Abs(pair.A.X - pair.B.X) < 0.5).ToList();
                    var preferred = new List<(Pt A, Pt B)> { (start, end) };
                    preferred.AddRange(verticals.Where(pair => pair.Index != route.Segment).Select(pair => (pair.A, pair.B)));
                    var rowState = Get(DepthOf(transition.To) < DepthOf(transition.From) ? transition.From : transition.To);
                    foreach (var (a, b) in preferred)
                    {
                        var x = a.X;
                        var mid = (a.Y + b.Y) / 2;
                        var sides = x <= rowState.Cx ? (int[])[-1, 1] : [1, -1];
                        foreach (var side in sides)
                        {
                            foreach (var shift in (double[])[0, -10, 10]) candidates.Add(At(x + side * (Layout.LABEL_GAP + width / 2), mid + shift));
                        }
                    }
                    var horizontal = route.Points.Skip(1).Select((point, index) => (A: route.Points[index], B: point)).Cast<(Pt A, Pt B)?>()
                        .FirstOrDefault(pair => Math.Abs(pair!.Value.A.Y - pair.Value.B.Y) < 0.5 && Math.Abs(pair.Value.A.X - pair.Value.B.X) > width + 8);
                    if (horizontal is { } h) candidates.Add(At((h.A.X + h.B.X) / 2, h.A.Y));
                }
                var otherSegments = Others(transition).SelectMany(SegmentsOf).ToList();
                var ownSegments = Siblings(transition).SelectMany(SegmentsOf).ToList();
                bool Free(Placed rect) => rect.X >= 8 && rect.X + rect.Width <= contentRight + Layout.MARGIN_X - 8 && rect.Y >= 4
                    && states.Values.All(state => !Geometry.RectsOverlap(rect, state.Rect, 4))
                    && placedLabels.Values.All(other => !Geometry.RectsOverlap(rect, other, 4))
                    && otherSegments.All(segment => (Geometry.SegmentRectClearance(segment, rect) ?? double.PositiveInfinity) >= Layout.CLEARANCE)
                    && (route.LabelMode == "on" || ownSegments.All(segment => (Geometry.SegmentRectClearance(segment, rect) ?? double.PositiveInfinity) >= Layout.LABEL_GAP - 1));
                var chosen = candidates.Cast<Placed?>().FirstOrDefault(candidate => Free(candidate!.Value));
                if (chosen is { } placed) placedLabels.Set(transition, placed);
                else
                {
                    unplacedLabels.Add(transition);
                    placedLabels.Set(transition, candidates[0]);
                }
            }
            return (placedLabels, unplacedLabels);
        }

        // ---------------------------------------------------------------- validate

        static Diagnostic StructureProblem(string code, string message, JsonObject subject, IReadOnlyList<string> supportedFixes)
        {
            var fullSubject = new JsonObject { ["diagramType"] = "lifecycle" };
            foreach (var (key, value) in subject) fullSubject[key] = value?.DeepClone();
            return new Diagnostic(code, Severity.Error, message, fullSubject, new JsonObject(), supportedFixes, null);
        }

        void ValidateStructure()
        {
            var diagnostics = new List<Diagnostic>();
            var problems = new List<string>();
            void Add(string code, string message, JsonObject subject, IReadOnlyList<string> supportedFixes)
            {
                var diagnostic = StructureProblem(code, message, subject, supportedFixes);
                diagnostics.Add(diagnostic);
                problems.Add(diagnostic.Message);
            }
            if (states.Count != authoredStates.Count)
            {
                Add("lifecycle/duplicate-state", "State ids must be unique.", new JsonObject { ["collection"] = "states" }, ["give every state a unique id"]);
            }
            var ids = states.Keys.ToList();
            for (var index = 0; index < mainPath.Count; index++)
            {
                var id = mainPath[index];
                if (!states.Has(id))
                {
                    Add("lifecycle/unknown-main-path-state", $"mainPath[{N(index)}] \"{id}\" is not a state.", new JsonObject { ["path"] = $"/mainPath/{N(index)}" },
                        [.. ids.Take(3).Select(known => $"set /mainPath/{N(index)} to state id \"{known}\"")]);
                }
            }
            if (DistinctOrdered(mainPath).Count != mainPath.Count)
            {
                Add("lifecycle/repeated-main-path-state", "mainPath lists a state more than once; a return to an earlier phase is a transition, not a repeated step.",
                    new JsonObject { ["path"] = "/mainPath" }, ["list each main-path state once and keep the return as a transition"]);
            }
            foreach (var transition in transitions)
            {
                var index = transition.Index;
                foreach (var (field, value) in (ValueTuple<string, string>[])[("from", transition.From), ("to", transition.To)])
                {
                    if (states.Has(value)) continue;
                    var subject = new JsonObject { ["collection"] = "transitions", ["path"] = $"/transitions/{N(index)}/{field}", ["transition"] = transition.Source["id"]?.DeepClone() };
                    Add("lifecycle/unknown-endpoint", $"Transition {N(index)} references unknown {(field == "from" ? "source" : "target")} \"{value}\".",
                        subject, [.. ids.Take(3).Select(known => $"set /transitions/{N(index)}/{field} to state id \"{known}\"")]);
                }
            }
            for (var index = 0; index + 1 < mainPath.Count; index += 1)
            {
                var (from, to) = (mainPath[index], mainPath[index + 1]);
                if (!transitions.Any(transition => transition.From == from && transition.To == to))
                {
                    Add("lifecycle/main-path-gap", $"mainPath step \"{from}\" -> \"{to}\" has no transition.", new JsonObject { ["path"] = $"/mainPath/{N(index)}" },
                        [$"add a transition from \"{from}\" to \"{to}\"", "remove the state from mainPath if it is not on the happy path"]);
                }
            }
            for (var index = 0; index < authoredStates.Count; index++)
            {
                var state = authoredStates[index];
                var brandRail = BrandMarks.BrandTopRailProblem(state.Brand, state.Id, state.Label, PreferredWidth(state), Layout.LabelMinimum, "State");
                if (brandRail is not null) problems.Add(brandRail);
                foreach (var (field, name, minimum) in (ValueTuple<string, string, double>[])[("label", "Label", Layout.LabelMinimum), ("sublabel", "Sublabel", Layout.SublabelMinimum), ("tag", "Tag", Layout.TagMinimum)])
                {
                    var value = state.Source.Str(field);
                    var needed = Math.Ceiling(TextFit.MinimumNodeTextWidth(value ?? "", minimum));
                    var available = TextFit.AvailableNodeTextWidth(Layout.STATE_MAX_W) - (field == "label" && state.Brand is not null ? 26 : 0);
                    if (Truthy(value) && needed > available)
                    {
                        Add("lifecycle/state-text-too-long",
                            $"{name} \"{value}\" of state \"{state.Id}\" is wider than the widest {N(Layout.STATE_MAX_W)}px state: it needs ~{N(needed)}px at the {N(minimum)}px legible minimum, but {N(available)}px fit — shorten the {field} and move detail into a card.",
                            new JsonObject { ["collection"] = "states", ["path"] = $"/states/{N(index)}/{field}" }, [$"shorten the {field} and move detail into a card"]);
                    }
                }
            }
            if (problems.Count > 0) throw ctx.Diagnostics.ThrowDiagnosticProblems("Lifecycle validation failed", problems, subject: new JsonObject { ["diagramType"] = "lifecycle" }, details: diagnostics);
        }

        void ValidateLayout()
        {
            var problems = new List<string>();
            var diagnostics = new List<Diagnostic>();
            // The common width factor cancels when comparing the two text lines.
            static string WidthDriver(Transition transition)
            {
                var label = Utils.TextUnits(transition.Label ?? "") * Layout.LABEL_FONT;
                var note = Utils.TextUnits(transition.Note ?? "") * Layout.NOTE_FONT;
                return label == note ? "label+note" : label > note ? "label" : "note";
            }
            const string preserveConditions = "preserve every condition; shorten wording only, or move complete detail to a card explicitly associated with the same transition; shorter wording may not resolve other layout constraints";
            if (viewBox[0] > readableWidthBudget)
            {
                // A labelled main-path step widens its gap past the default spacing.
                var wideStepLabels = mainPath.Take(Math.Max(0, mainPath.Count - 1)).SelectMany((_, index) =>
                {
                    var edge = spineEdges.Get(index);
                    var excessPx = edge is not null && HasLabel(edge) ? Js.Round(LabelBox(edge).Width + 26 - spacing.SpineGap) : 0;
                    return excessPx > 0 ? (List<(string From, string To, string? Label, double ExcessPx, string WidthDriver)>)[(edge!.From, edge.To, LabelOrNote(edge), excessPx, WidthDriver(edge))] : [];
                }).ToList();
                Js.Sort(wideStepLabels, (a, b) => b.ExcessPx - a.ExcessPx);
                var widenerLabels = spacingWideners.Select(transition => $"\"{LabelOrNote(transition)}\"").ToList();
                var labelFixes = new List<string>();
                if (widenerLabels.Count > 0) labelFixes.Add($"relieve the routes crowding {string.Join(", ", widenerLabels)}: those label/note boxes found no clear spot at the base spacing, so lower-row and route spacing grew; main-path gap growth is bounded by the text readability budget. Only equivalent exits with identical labels, notes and variants can share an arrow; keep distinct notes separate, or move a secondary transition elsewhere");
                if (wideStepLabels.Count > 0) labelFixes.Add($"review the width-driving main-path transition text: {string.Join(", ", wideStepLabels.Select(step => $"\"{step.From}\" -> \"{step.To}\" {Js.ReplaceFirst(step.WidthDriver, "+", _ => " and ")} (+{N(step.ExcessPx)}px)"))}; {preserveConditions}");
                var message = $"The lifecycle is {N(viewBox[0])}px wide; at {N(smallestText)}px text it stays readable on a desktop only up to {N(readableWidthBudget)}px.{(wideStepLabels.Count > 0 ? " Transition labels or notes widen their main-path gaps." : "")}";
                diagnostics.Add(new("lifecycle/too-wide", Severity.Error, message,
                    new JsonObject { ["diagramType"] = "lifecycle", ["path"] = "/mainPath" },
                    new JsonObject
                    {
                        ["viewBoxWidth"] = viewBox[0], ["budgetPx"] = readableWidthBudget, ["mainPathStates"] = mainPath.Count, ["lowerRowStates"] = offStates.Count,
                        ["wideStepLabels"] = new JsonArray([.. wideStepLabels.Select(step => (JsonNode)new JsonObject
                        {
                            ["from"] = step.From, ["to"] = step.To, ["label"] = step.Label, ["excessPx"] = step.ExcessPx, ["widthDriver"] = step.WidthDriver,
                        })]),
                        ["spacingWideners"] = new JsonArray([.. spacingWideners.Select(transition => (JsonNode)new JsonObject
                        {
                            ["from"] = transition.From, ["to"] = transition.To, ["label"] = LabelOrNote(transition), ["widthDriver"] = WidthDriver(transition),
                        })]),
                    },
                    [.. labelFixes, "shorten state labels and sublabels", "move secondary phases off mainPath or merge adjacent phases", "split the lifecycle into two diagrams"], null));
                problems.Add(message);
            }
            foreach (var transition in geometry.UnplacedLabels)
            {
                var message = $"Transition \"{transition.From}\" -> \"{transition.To}\" label \"{LabelOrNote(transition)}\" cannot be placed clear of nearby routes.{(Truthy(transition.Note) ? " Its note contributes to the label box; the box size and nearby geometry both affect placement." : "")}";
                diagnostics.Add(new("lifecycle/label-unplaced", Severity.Error, message,
                    new JsonObject { ["diagramType"] = "lifecycle", ["collection"] = "transitions", ["index"] = transition.Index, ["from"] = transition.From, ["to"] = transition.To },
                    new JsonObject { ["widthDriver"] = WidthDriver(transition) },
                    [$"review the transition {(Truthy(transition.Note) ? Truthy(transition.Label) ? "label and note" : "note" : "label")} wording; {preserveConditions}", "only equivalent exits with identical labels, notes and variants can share one arrow; keep distinct notes on separate exits without removing them to force sharing"], null));
                problems.Add(message);
            }
            if (problems.Count > 0) throw ctx.Diagnostics.ThrowDiagnosticProblems("Lifecycle layout validation failed", problems, subject: new JsonObject { ["diagramType"] = "lifecycle" }, details: diagnostics);
        }

        // ---------------------------------------------------------------- canvas

        List<LegendEntry> LegendCatalog()
        {
            var catalog = LegendKinds.Select(kind => new LegendEntry(kind, ctx.Locale.Translate(locale, $"legend.lifecycle.{kind}"), SwatchWidth: kind == "start" ? 26 : null)).ToList();
            var presentKinds = new JsSet<string>();
            foreach (var state in states.Values) if (state.Type is { } type) presentKinds.Add(type);
            presentKinds.Add("start");
            var entries = Legend.ResolveLegend(meta.Obj("legend"), catalog, presentKinds);
            if (entries.Count == 0 || !states.Values.Any(IsFinal)) return entries;
            return [.. entries, new LegendEntry("final", ctx.Locale.Translate(locale, "legend.lifecycle.final"), Present: true, Interactive: false, SwatchWidth: 16)];
        }

        // ---------------------------------------------------------------- render

        string RenderState(State state)
        {
            var fill = state.Type is { } fillType && TypeClass.TryGetValue(fillType, out var f) ? f : TypeClass["neutral"];
            var accent = state.Type is { } accentType && TextClass.TryGetValue(accentType, out var a) ? a : "t-muted";
            var hasSub = state.Sublabel is not null && state.Sublabel != "";
            var (labelFont, sublabelFont, tagFont) = Fonts(state, state.Width);
            // Baselines keep each row's full glyph box (ascent and descent) separate.
            var (labelY, sublabelY, tagY) = hasSub && Truthy(state.Tag) ? (23.0, 40.0, 55.0) : hasSub ? (27, 45, 0) : Truthy(state.Tag) ? (29, 0, 50) : (37, 0, 0);
            var textOffset = (state.Height - Layout.STATE_H) / 2;
            var textRows = new List<LabelRow> { new(state.Label ?? "", labelFont, labelY + textOffset) };
            if (hasSub) textRows.Add(new(state.Sublabel!, sublabelFont, sublabelY + textOffset));
            if (Truthy(state.Tag)) textRows.Add(new(state.Tag!, tagFont, tagY + textOffset));
            var labelLayout = TextFit.NodeLabelLayout(state.Width, state.Height, textRows, "left",
                brand: state.Brand is not null, source: SourceBadgeFor(state.Id), step: state.Step ?? "");
            var sub = hasSub
                ? $"\n          <text data-detail=\"context\" x=\"{N(state.Cx)}\" y=\"{N(state.Y + labelLayout.Ys[1])}\" class=\"t-muted\" font-size=\"{N(sublabelFont)}\" text-anchor=\"middle\">{Utils.Esc(state.Sublabel)}</text>"
                : "";
            var tag = Truthy(state.Tag)
                ? $"\n          <text data-detail=\"fine\" x=\"{N(state.Cx)}\" y=\"{N(state.Y + labelLayout.Ys[hasSub ? 2 : 1])}\" class=\"{accent}\" font-size=\"{N(tagFont)}\" text-anchor=\"middle\">{Utils.Esc(state.Tag)}</text>"
                : "";
            var icon = state.Icon ?? (state.Type is { } quietType && QuietTypes.Contains(quietType) ? (IsFinal(state) ? "stop" : "none") : null);
            var step = Truthy(state.Step)
                ? $"\n          <text data-detail=\"fine\" x=\"{N(state.X + 23)}\" y=\"{N(state.Y + 14)}\" class=\"{accent}\" font-size=\"{N(Layout.StepFont)}\" font-weight=\"700\">{Utils.Esc(state.Step)}</text>"
                : "";
            var brand = state.Brand is { } mark ? BrandMarks.RenderBrandMark(mark, state.X + state.Width - 22, state.Y + 6) : "";
            var initial = state.Id == (mainPath.Count > 0 ? mainPath[0] : null)
                ? $"\n          <g aria-hidden=\"true\" data-lifecycle-initial-marker=\"\">{InitialMarkerShape(state.X - 26, state.X - 1, state.Cy)}</g>"
                : "";
            var finalBorder = IsFinal(state)
                ? $"\n          <rect x=\"{N(state.X + 3)}\" y=\"{N(state.Y + 3)}\" width=\"{N(state.Width - 6)}\" height=\"{N(state.Height - 6)}\" rx=\"5\" class=\"{fill}\" style=\"fill: none\" stroke-width=\"1\"/>"
                : "";
            var passport = new JsonObject();
            if (state.Source["type"] is { } kind) passport["kind"] = kind.DeepClone();
            if (state.Source["sublabel"] is { } sublabelNode) passport["sublabel"] = sublabelNode.DeepClone();
            if (state.Source["tag"] is { } tagNode) passport["tag"] = tagNode.DeepClone();
            passport["context"] = ctx.Locale.Translate(locale, "node.context.lifecycle");
            if (BrandMarks.BrandMetadataFor(state.Brand) is { } brandMetadata)
                foreach (var (key, value) in brandMetadata) passport[key] = value?.DeepClone();
            var label = state.Label ?? "";
            return $"        <g {SvgAttrs.FocusNodeAttrs(state.Id, label, passport, locale, ctx)}>\n"
                + $"          {SvgAttrs.FocusNodeTitle(label, passport)}\n"
                + $"          <rect x=\"{N(state.X)}\" y=\"{N(state.Y)}\" width=\"{N(state.Width)}\" height=\"{N(state.Height)}\" rx=\"8\" class=\"c-mask\"/>\n"
                + $"          <rect x=\"{N(state.X)}\" y=\"{N(state.Y)}\" width=\"{N(state.Width)}\" height=\"{N(state.Height)}\" rx=\"8\" class=\"{fill}\"{SvgAttrs.AnimateAttr(meta, "node", stateSteps.Has(state.Id) ? stateSteps.Get(state.Id) : null)} stroke-width=\"1.5\"/>{finalBorder}{initial}\n"
                + $"          {Utils.RenderSemanticSigil(state.Type ?? "", state.X + 6, state.Y + labelLayout.SigilY, labelLayout.SigilSize, icon)}{(brand.Length > 0 ? $"\n          {brand}" : "")}{step}\n"
                + $"          <text data-node-label=\"\"{(hasSub ? " data-detail-anchor=\"\"" : "")} x=\"{N(state.X + labelLayout.X)}\" y=\"{N(state.Y + labelLayout.Ys[0])}\" class=\"t-primary\" font-size=\"{N(labelFont)}\" font-weight=\"600\" text-anchor=\"middle\">{Utils.Esc(state.Label)}</text>{sub}{tag}\n"
                + "        </g>";
        }

        string VariantOf(Transition transition)
        {
            if (Truthy(transition.Variant)) return transition.Variant!;
            return kindOf.Get(transition) == "spine" ? "emphasis" : "default";
        }

        string RenderTransition(Transition transition, int index)
        {
            var route = geometry.Routes.Get(transition)!;
            var variant = VariantOf(transition);
            var (cls, marker) = Geometry.ArrowClassMap.TryGetValue(variant, out var arrow) ? arrow : Geometry.ArrowClassMap["default"];
            var strokeWidth = variant == "emphasis" ? 1.8 : 1.2;
            var d = Geometry.RoundedPath(route.Points, Layout.CORNER);
            var junction = route.Bracket is not null ? $" data-composition-junction=\"{Utils.Esc(route.Bracket.Key.Replace(NUL, '+'))}\"" : "";
            var edge = $"          <path {SvgAttrs.FocusEdgeAttrs(transition.From, transition.To, LabelOrNote(transition), N(index), transition.Id)} data-composition-points=\"{Geometry.RoutePointsValue(route.Points)}\" data-composition-crossover=\"halo\" data-composition-independent=\"true\"{junction} d=\"{d}\" class=\"{cls}\"{SvgAttrs.AnimateAttr(meta, "edge", index)} stroke-width=\"{N(strokeWidth)}\" marker-end=\"url(#{marker})\"/>";
            var underlay = $"          <path data-graph-role=\"automatic-crossover-underlay\" d=\"{d}\" fill=\"none\" stroke=\"var(--mask)\" stroke-width=\"{N(strokeWidth + 4)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\" pointer-events=\"none\"/>";
            return $"        <g data-graph-role=\"automatic-crossover\" style=\"--step:{N(index)}\">\n{underlay}\n{edge}\n        </g>";
        }

        string RenderLabel(Transition transition, int index)
        {
            if (!geometry.PlacedLabels.Has(transition)) return "";
            var rect = geometry.PlacedLabels.Get(transition);
            var variant = VariantOf(transition);
            var accent = variant == "default" ? "t-muted" : Geometry.EdgeLabelAccent(variant);
            var baseline = rect.Y + (Truthy(transition.Label) ? 11.5 : 11);
            var label = Truthy(transition.Label)
                ? $"\n          <text x=\"{N(rect.Cx)}\" y=\"{N(baseline)}\" class=\"{accent}\" font-size=\"{N(Layout.LABEL_FONT)}\" text-anchor=\"middle\">{Utils.Esc(transition.Label)}</text>"
                : "";
            var note = Truthy(transition.Note)
                ? $"\n          <text data-detail=\"fine\" x=\"{N(rect.Cx)}\" y=\"{N(baseline + (Truthy(transition.Label) ? 12 : 0))}\" class=\"t-muted\" font-size=\"{N(Layout.NOTE_FONT)}\" text-anchor=\"middle\">{Utils.Esc(transition.Note)}</text>"
                : "";
            return $"        <g data-detail=\"{(Truthy(transition.Label) ? "context" : "fine")}\" {SvgAttrs.FocusEdgeAttrs(transition.From, transition.To, LabelOrNote(transition), N(index), transition.Id)}>\n"
                + $"          <rect x=\"{N(rect.X)}\" y=\"{N(rect.Y)}\" width=\"{N(rect.Width)}\" height=\"{N(rect.Height)}\" rx=\"4\" class=\"c-mask\"/>{label}{note}\n"
                + "        </g>";
        }

        static string InitialMarkerShape(double dotX, double tipX, double y) =>
            $"<circle cx=\"{N(dotX)}\" cy=\"{N(y)}\" r=\"4.5\" style=\"fill: var(--arrow-emphasis)\"/><path d=\"M {N(dotX + 4.5)} {N(y)} L {N(tipX - 6)} {N(y)}\" class=\"a-emphasis\" stroke-width=\"1.4\"/><path d=\"M {N(tipX - 7)} {N(y - 3.5)} L {N(tipX)} {N(y)} L {N(tipX - 7)} {N(y + 3.5)} Z\" style=\"fill: var(--arrow-emphasis)\"/>";

        static string RenderSwatch(LegendEntry entry)
        {
            if (entry.Kind == "start") return InitialMarkerShape(entry.X + 4.5, entry.X + 24, entry.Baseline - 3.5);
            if (entry.Kind == "final")
            {
                return $"<rect x=\"{N(entry.X)}\" y=\"{N(entry.Baseline - 8)}\" width=\"14\" height=\"9\" rx=\"2\" class=\"c-external\" stroke-width=\"1\"/><rect x=\"{N(entry.X + 2.5)}\" y=\"{N(entry.Baseline - 5.5)}\" width=\"9\" height=\"4\" rx=\"1\" class=\"c-external\" style=\"fill: none\" stroke-width=\"0.8\"/>";
            }
            return $"<rect x=\"{N(entry.X)}\" y=\"{N(entry.Baseline - 8)}\" width=\"14\" height=\"9\" rx=\"2\" class=\"{(TypeClass.TryGetValue(entry.Kind, out var swatch) ? swatch : "c-external")}\" stroke-width=\"1\"/>";
        }

        string RenderLegend() => Legend.RenderLegend(ctx, legendEntries, new LegendLayout(
            X: 40,
            BaselineY: viewBox[1] - 32,
            Width: viewBox[0] - 80,
            MinTitleY: geometry.ContentBottom + 12,
            Unfit: !meta.ContainsKey("legend") ? "hide" : "error",
            DiagramType: "lifecycle"), RenderSwatch, locale);

        public string RenderSvg()
        {
            var junctions = string.Join("\n", geometry.Junctions.Select(point => $"        <circle cx=\"{N(point.X)}\" cy=\"{N(point.Y)}\" r=\"2.6\" data-lifecycle-junction=\"\" style=\"fill: var(--arrow)\" aria-hidden=\"true\"/>"));
            var frames = string.Join("\n", geometry.Frames.Select(rect => $"        <rect x=\"{N(rect.X)}\" y=\"{N(rect.Y)}\" width=\"{N(rect.Width)}\" height=\"{N(rect.Height)}\" rx=\"14\" data-lifecycle-frame=\"\" fill=\"none\" stroke=\"var(--arrow)\" stroke-opacity=\"0.55\" stroke-width=\"1\" stroke-dasharray=\"1.5 3.5\" stroke-linecap=\"round\" aria-hidden=\"true\"/>"));
            var sb = new StringBuilder();
            sb.Append($"      <svg viewBox=\"0 0 {N(viewBox[0])} {N(viewBox[1])}\" data-reader-fit=\"intrinsic-height\" data-lifecycle-layout=\"main-path-v3\" {SvgAttrs.SvgRootAttrs(ctx)} data-motion-choreography=\"walk\">\n");
            sb.Append(SvgAttrs.SvgAccessibleText(ctx, "lifecycle")).Append('\n');
            sb.Append(Utils.RenderDefinitions()).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Background Grid -->\n");
            sb.Append("        <rect width=\"100%\" height=\"100%\" fill=\"url(#grid)\" />\n");
            sb.Append('\n');
            sb.Append("        <!-- Composite phases -->\n");
            sb.Append(frames).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Transition paths -->\n");
            sb.Append(string.Join("\n", transitions.Select(RenderTransition))).Append('\n');
            sb.Append(junctions).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- States -->\n");
            sb.Append(string.Join("\n\n", states.Values.Select(RenderState))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Transition labels -->\n");
            sb.Append(string.Join("\n", transitions.Select(RenderLabel).Where(label => label.Length > 0))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Legend -->\n");
            sb.Append(RenderLegend()).Append('\n');
            sb.Append("      </svg>");
            return sb.ToString();
        }
    }
}
