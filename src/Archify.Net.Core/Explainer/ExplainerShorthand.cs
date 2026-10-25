using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net;

// A shorthand row: its pipe-separated cells, already split by the parser, and its 1-based source line.
internal sealed record ShorthandRow(IReadOnlyList<string> Cells, int Line);

// compileExplainerDiagram's options. Locale is Decision 2's addition: a workflow's branch lanes read "分支 N" only on a zh
// page, "Branch N" otherwise (a null locale is the default `en`, Decision 3).
internal sealed record ShorthandOptions(string? Title, string? Id, int Line = 1, string Quality = "showcase", string? Locale = null);

// archify 3.2.0 explainer/diagrams.mjs:1-352: the author shorthand compiled into a native Archify model, and the
// diagnostics shape every diagram failure shares (subject, fail, rethrowNative).
internal static partial class ExplainerShorthand
{
    static readonly HashSet<string> Types = new(StringComparer.Ordinal)
        { "architecture", "workflow", "sequence", "dataflow", "lifecycle", "erd", "class", "tree", "timeline", "waterfall" };
    const string Ws = Js.WsClass;

    // /^[a-zA-Z][a-zA-Z0-9_-]*$/: `$` without the m flag is the end of input.
    [GeneratedRegex(@"\A[a-zA-Z][a-zA-Z0-9_-]*\z")] private static partial Regex Id();
    [GeneratedRegex($@"\A([a-zA-Z][a-zA-Z0-9_-]*){Ws}*>{Ws}*([a-zA-Z][a-zA-Z0-9_-]*)\z")] private static partial Regex Route();
    [GeneratedRegex(@"\A(one|many):(one|many)\z")] private static partial Regex Cardinality();
    [GeneratedRegex(@"\A([a-zA-Z][a-zA-Z0-9_-]*)\.([a-zA-Z][a-zA-Z0-9_]*)\z")] private static partial Regex Field();
    [GeneratedRegex("[^a-z0-9_-]+")] private static partial Regex NotSlug();
    [GeneratedRegex(@"\A-+|-+\z")] private static partial Regex EdgeDashes();
    [GeneratedRegex(@"\A/[^/]+/[0-9]+")] private static partial Regex RowPath();

    internal static bool IsId(string? value) => value is not null && Id().IsMatch(value);
    static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // The context every diagram diagnostic is built from (diagrams.mjs `context`). RowMap is the shorthand's own
    // native-pointer → author-line map; prepare's contexts have none, and a source map instead.
    internal sealed record Context(int? Line, string? Pointer, string? Id = null, string? DiagramType = null, JsonObject? Subject = null,
        Dictionary<string, int>? RowMap = null, JsonObject? SourceMap = null, string? SourcePath = null, string? PanelId = null);

    // diagrams.mjs:22-33
    internal static JsonObject SubjectOf(Context context, string pointer, JsonObject? extra = null)
    {
        JsonObject? location = null;
        for (var lookup = pointer; lookup.Length > 0 && location is null; lookup = lookup[..Math.Max(0, lookup.LastIndexOf('/'))])
            location = context.SourceMap?[lookup] as JsonObject;
        var subject = new JsonObject
        {
            ["component"] = "diagram",
            ["line"] = location?["line"].Truthy() == true ? location["line"]!.DeepClone() : JsonValue.Create(context.Line is > 0 ? context.Line.Value : 1),
            ["path"] = pointer,
        };
        if (location?["column"].Truthy() == true) subject["column"] = location["column"]!.DeepClone();
        if (!string.IsNullOrEmpty(context.SourcePath)) subject["input"] = context.SourcePath;
        if (!string.IsNullOrEmpty(context.PanelId)) subject["panelId"] = context.PanelId;
        foreach (var (key, value) in extra ?? []) if (value is not null) subject[key] = value.DeepClone();
        return subject;
    }

    static Exception Throw(string message, IEnumerable<Diagnostic> diagnostics) =>
        new ArchifyDiagnosticException(message, [.. diagnostics.Select(Diagnostic.Normalize)]);

    // diagrams.mjs:35-40
    internal static Exception Fail(Context context, string reason, string message, string? example, IReadOnlyList<string>? fixes = null, JsonNode? cause = null)
    {
        var evidence = new JsonObject { ["reason"] = reason, ["example"] = string.IsNullOrEmpty(example) ? "Use a native diagram spec with matching diagram_type." : example };
        if (cause is not null) evidence["native"] = cause;
        return Throw(message, [new Diagnostic($"explainer/{reason}", Severity.Error, message,
            SubjectOf(context, string.IsNullOrEmpty(context.Pointer) ? "/" : context.Pointer, context.Subject), evidence,
            fixes is { Count: > 0 } ? fixes : [$"Use this form: {(string.IsNullOrEmpty(example) ? "native JSON" : example)}"], null)]);
    }

    static string SafeId(string? value, Context context) => IsId(value) ? value!
        : throw Fail(context, "diagram-id", "Diagram identities must use the native identifier syntax.", "api | API | backend");

    // NFKC, then JS toLowerCase (İ becomes i̇), then the slug; a label with no usable slug gets actor-<hash>.
    // A lone surrogate is replaced with U+FFFD before Normalize so .NET does not throw ArgumentException; NotSlug maps both to `-`.
    internal static string InferredId(string label)
    {
        string normalized;
        try { normalized = label.Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException) { normalized = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(label)).Normalize(NormalizationForm.FormKC); }
        var slug = EdgeDashes().Replace(NotSlug().Replace(Js.ToLowerCase(normalized), "-"), "");
        return IsId(slug) ? slug : $"actor-{Hash(label)[..12]}";
    }

    static void DuplicateCheck(IEnumerable<JsonObject> items, Context context, string kind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var id = item["id"]!.GetValue<string>();
            if (!seen.Add(id)) throw Fail(context, "diagram-id-duplicate", $"Duplicate {kind} identity {id}.", "Use a unique id for every declaration.");
        }
    }

    // diagrams.mjs:65-75. Estimated native workflow canvas width: the widest node per column plus the column gaps, which widen
    // for edge labels. 1100 keeps a margin under the native gate's 1240px.
    const double ReadableWorkflowWidth = 1100;
    internal static double WorkflowWidth(IReadOnlyList<JsonObject> nodes, IReadOnlyList<JsonObject> edges)
    {
        static int Col(JsonObject node) => (int)node["col"]!.D();
        var columns = nodes.Count == 0 ? 0 : nodes.Max(Col) + 1;
        var widest = Enumerable.Range(0, columns).Select(col => Js.MaxOf(nodes.Where(n => Col(n) == col).Select(n => n["width"]?.D() ?? 0), 92)).ToList();
        var colOf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes) colOf[node["id"]!.GetValue<string>()] = Col(node);
        int? ColOf(JsonObject edge, string key) => colOf.TryGetValue(edge[key]!.GetValue<string>(), out var col) ? col : null;
        var gaps = Enumerable.Range(0, Math.Max(0, columns - 1)).Select(col => Js.MaxOf(edges.Where(e => ColOf(e, "from") == col && ColOf(e, "to") == col + 1)
            .Select(e => (e.Str("label") is { Length: > 0 } label ? Utils.TextUnits(label) * 5.4 : 0) + 16), 34)).ToList();
        return widest.Aggregate(0.0, (sum, width) => sum + width) + gaps.Aggregate(0.0, (sum, gap) => sum + gap) + 80;
    }

    // diagrams.mjs:79-104. Native grid/lane hints only: rank by longest path from the sources; a cyclic remainder stays a
    // deterministic separate row in column 0. Returns copies with `row` and `col` appended.
    internal static List<JsonObject> RankedPlacement(IReadOnlyList<JsonObject> nodes, IReadOnlyList<JsonObject> edges)
    {
        static string S(JsonObject o, string key) => o[key]!.GetValue<string>();
        var ids = nodes.Select(n => S(n, "id")).ToHashSet(StringComparer.Ordinal);
        var incoming = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in nodes) incoming[S(node, "id")] = 0;
        foreach (var edge in edges) if (ids.Contains(S(edge, "from")) && ids.Contains(S(edge, "to")) && S(edge, "from") != S(edge, "to")) incoming[S(edge, "to")]++;
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = nodes.Where(n => incoming[S(n, "id")] == 0).Select(n => S(n, "id")).ToList();
        for (var cursor = 0; cursor < queue.Count; cursor++)
        {
            var id = queue[cursor];
            rank.TryAdd(id, 0);
            foreach (var edge in edges.Where(e => S(e, "from") == id && S(e, "to") != id))
            {
                var to = S(edge, "to");
                if (!ids.Contains(to)) continue;
                incoming[to]--;
                rank[to] = Math.Max(rank.GetValueOrDefault(to), rank[id] + 1);
                if (incoming[to] == 0) queue.Add(to);
            }
        }
        var nextRow = new Dictionary<int, int>();
        return [.. nodes.Select(node =>
        {
            var id = S(node, "id");
            var col = rank.TryGetValue(id, out var r) && incoming[id] == 0 ? r : 0;
            var row = nextRow.GetValueOrDefault(col);
            nextRow[col] = row + 1;
            return With(node, ("row", row), ("col", col));
        })];
    }

    // {...node, key: value, ...}: a copy, each key replaced in place or appended.
    static JsonObject With(JsonObject node, params (string Key, JsonNode? Value)[] entries)
    {
        var copy = node.DeepClone().AsObject();
        foreach (var (key, value) in entries) copy[key] = value;
        return copy;
    }
    // ({key, ...node}) => node: a copy without these keys.
    static JsonObject Without(JsonObject node, params string[] keys)
    {
        var copy = node.DeepClone().AsObject();
        foreach (var key in keys) copy.Remove(key);
        return copy;
    }

    sealed record Entry(List<string> Parts, Context Context);

    /// <summary>compileExplainerDiagram (diagrams.mjs:108-334).</summary>
    /// <exception cref="ArchifyDiagnosticException">Any shorthand or native schema failure.</exception>
    internal static JsonObject Compile(string? diagramType, IReadOnlyList<ShorthandRow> rows, ShorthandOptions options)
    {
        var (title, id, line, quality, locale) = options;
        var subject = new JsonObject { ["diagram"] = id, ["diagramType"] = diagramType };
        var rowMap = new Dictionary<string, int>(StringComparer.Ordinal);
        var context = new Context(line, "/", id, diagramType, subject, rowMap);
        if (diagramType is null || !Types.Contains(diagramType)) throw Fail(context, "diagram-type", "Unknown native diagram type.", "sequence");
        if (rows.Count == 0) throw Fail(context, "diagram-rows", "A shorthand diagram needs at least one row.", "Client | API | 请求记录");
        var entries = rows.Select(row =>
        {
            var ctx = context with { Line = row.Line };
            if (row.Cells.Count == 0) throw Fail(ctx, "diagram-row", "Each diagram row needs text cells.", "Client | API | 请求记录");
            return new Entry([.. row.Cells.Select(Js.Trim)], ctx);
        }).ToList();
        var spec = new JsonObject
        {
            ["schema_version"] = diagramType == "workflow" ? 2 : 1, ["diagram_type"] = diagramType,
            ["meta"] = new JsonObject
            {
                ["title"] = !string.IsNullOrEmpty(title) ? title : !string.IsNullOrEmpty(id) ? id : diagramType,
                ["output"] = $"{(!string.IsNullOrEmpty(id) && IsId(id) ? id : "diagram")}.html", ["quality_profile"] = quality,
            },
        };
        void Expect(Entry entry, int min, int max, string example)
        {
            if (entry.Parts.Count < min || entry.Parts.Count > max || entry.Parts.Take(min).Any(part => part.Length == 0))
                throw Fail(entry.Context, "diagram-row", $"Expected {(min == max ? $"{min}" : $"{min}–{max}")} pipe-separated fields.", example);
        }
        var edgeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        string EdgeId(string from, string to)
        {
            var key = $"{from}-to-{to}";
            var count = edgeCounts.GetValueOrDefault(key) + 1;
            edgeCounts[key] = count;
            return $"{key}-{count}";
        }
        string? Part(Entry entry, int index) => index < entry.Parts.Count ? entry.Parts[index] : null;

        switch (diagramType)
        {
            case "sequence": Sequence(); break;
            case "architecture" or "workflow": Graph(); break;
            case "tree": Tree(); break;
            case "timeline": Timeline(); break;
            case "erd": Erd(); break;
            default:
                throw Fail(context, "diagram-shorthand-unsupported", $"The {diagramType} type uses native JSON instead of shorthand rows.",
                    $"Use spec with diagram_type: {diagramType}, or source: diagram.{diagramType}.json.");
        }
        try { SchemaValidator.Validate(DiagramTypes.Parse(diagramType), spec, new DiagnosticSink()); }
        catch (ArchifyDiagnosticException error) { throw RethrowNative(NativeError(error), context, "diagram-schema"); }
        return spec;

        void Sequence()
        {
            var participants = new JsonArray();
            var messages = new JsonArray();
            spec["participants"] = participants;
            spec["messages"] = messages;
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            var byId = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries.Where(e => e.Parts[0].StartsWith("actor ", StringComparison.Ordinal)))
            {
                Expect(entry, 2, 3, "actor api | API | backend");
                var actor = new JsonObject
                {
                    ["id"] = SafeId(Js.Trim(entry.Parts[0][6..]), entry.Context), ["label"] = entry.Parts[1],
                    ["type"] = Part(entry, 2) is { Length: > 0 } type ? type : "backend",
                };
                rowMap[$"/participants/{participants.Count}"] = entry.Context.Line!.Value;
                participants.Add(actor);
                var actorId = actor["id"]!.GetValue<string>();
                byId.Add(actorId);
                aliases[entry.Parts[1]] = actorId;
                aliases[actorId] = actorId;
            }
            string ActorId(string label, Context ctx)
            {
                if (aliases.TryGetValue(label, out var known)) return known;
                var inferred = InferredId(label);
                if (byId.Contains(inferred)) throw Fail(ctx, "diagram-actor-collision", "Actor labels resolve to the same id; declare explicit actor identities.", "actor client | Client | frontend");
                participants.Add(new JsonObject { ["id"] = inferred, ["label"] = label, ["type"] = "external" });
                byId.Add(inferred);
                aliases[label] = inferred;
                return inferred;
            }
            // "phase | label" rows open a labelled band over the messages that follow, up to the next phase.
            var phases = new List<(string Label, int First, Context Context)>();
            foreach (var entry in entries.Where(e => !e.Parts[0].StartsWith("actor ", StringComparison.Ordinal)))
            {
                if (entry.Parts[0] == "phase") { Expect(entry, 2, 2, "phase | TCP handshake"); phases.Add((entry.Parts[1], messages.Count, entry.Context)); continue; }
                Expect(entry, 3, 4, "Client | API | 请求记录 | return");
                var from = ActorId(entry.Parts[0], entry.Context);
                var to = ActorId(entry.Parts[1], entry.Context);
                rowMap[$"/messages/{messages.Count}"] = entry.Context.Line!.Value;
                var message = new JsonObject { ["id"] = EdgeId(from, to), ["from"] = from, ["to"] = to, ["label"] = entry.Parts[2], ["y"] = 160 + messages.Count * 48 };
                if (Part(entry, 3) is { Length: > 0 } variant) message["variant"] = variant;
                messages.Add(message);
            }
            for (var index = 0; index < phases.Count; index++)
            {
                var phase = phases[index];
                var last = (index + 1 < phases.Count ? phases[index + 1].First : messages.Count) - 1;
                if (last < phase.First) throw Fail(phase.Context, "diagram-row", "A phase needs at least one message after it.", "phase | TCP handshake\n| browser | server | SYN |");
                if (spec["segments"] is not JsonArray segments) spec["segments"] = segments = [];
                segments.Add(new JsonObject { ["from"] = messages[phase.First]!["y"]!.D() - 22, ["to"] = messages[last]!["y"]!.D() + 16, ["label"] = phase.Label });
            }
            DuplicateCheck(participants.Cast<JsonObject>(), context, "actor");
            if (messages.Count == 0) throw Fail(context, "diagram-rows", "Sequence shorthand needs a message.", "client | api | 请求记录");
        }

        void Graph()
        {
            var workflow = diagramType == "workflow";
            var nodes = new List<JsonObject>();
            var edges = new List<JsonObject>();
            var lanes = new JsMap<string, JsonObject>();
            var nodeLanes = new JsMap<string, (string Lane, Context Context)>();
            foreach (var entry in entries)
            {
                var route = Route().Match(entry.Parts[0]);
                if (workflow && entry.Parts[0].StartsWith("lane ", StringComparison.Ordinal))
                {
                    Expect(entry, 2, 2, "lane locate | Locate the cause");
                    var laneId = SafeId(Js.Trim(entry.Parts[0][5..]), entry.Context);
                    if (lanes.Has(laneId)) throw Fail(entry.Context, "diagram-id-duplicate", "Workflow lane ids must be unique.", "lane locate | Locate the cause");
                    lanes.Set(laneId, new JsonObject { ["id"] = laneId, ["label"] = entry.Parts[1] });
                }
                else if (route.Success)
                {
                    if (entry.Parts.Count > 3) throw Fail(entry.Context, "diagram-row", "An edge supports a label and optional native variant.", "api > redis | 查询 | dashed");
                    rowMap[$"/{(workflow ? "edges" : "connections")}/{edges.Count}"] = entry.Context.Line!.Value;
                    var (from, to) = (route.Groups[1].Value, route.Groups[2].Value);
                    var edge = new JsonObject { ["id"] = EdgeId(from, to), ["from"] = from, ["to"] = to };
                    if (Part(entry, 1) is { Length: > 0 } label) edge["label"] = label;
                    if (Part(entry, 2) is { Length: > 0 } variant) edge["variant"] = variant;
                    edges.Add(edge);
                }
                else
                {
                    if (workflow) Expect(entry, 2, 4, "trace | Compare slow traces | backend | locate");
                    else Expect(entry, 2, 3, "api | API | backend");
                    rowMap[$"/{(workflow ? "nodes" : "components")}/{nodes.Count}"] = entry.Context.Line!.Value;
                    var label = entry.Parts[1];
                    var node = new JsonObject
                    {
                        ["id"] = SafeId(entry.Parts[0], entry.Context), ["label"] = label, ["type"] = Part(entry, 2) is { Length: > 0 } type ? type : "backend",
                    };
                    // Wide enough that the label stays centred between the corner icon and the opposite edge.
                    var labelWidth = Math.Ceiling(Math.Max(Utils.TextUnits(label) * 6.8 + 12,
                        TextFit.MinimumNodeTextWidth(label, 11) + 2 * (Utils.SemanticSigilFootprint + 2)));
                    if (workflow && labelWidth > 92) node["width"] = labelWidth;
                    // A question is a decision point: the native decision mark tells it apart from a step.
                    if (workflow && label.Length > 0 && label[^1] is '?' or '？') node["icon"] = "decision";
                    if (Part(entry, 3) is { Length: > 0 } lane) nodeLanes.Set(node["id"]!.GetValue<string>(), (SafeId(lane, entry.Context), entry.Context));
                    nodes.Add(node);
                }
            }
            DuplicateCheck(nodes, context, "node");
            var placed = RankedPlacement(nodes, edges);
            JsonArray Array(IEnumerable<JsonObject> items) => [.. items];
            if (!workflow) Architecture(placed, edges);
            else if (lanes.Count > 0) Lanes(placed, edges, lanes, nodeLanes);
            else Rows(placed, edges);

            void Architecture(List<JsonObject> placed, List<JsonObject> edges)
            {
                static int Of(JsonObject node, string key) => (int)node[key]!.D();
                var cols = Math.Max(1, placed.Count == 0 ? 0 : placed.Max(n => Of(n, "col") + 1));
                if (cols > 12) throw Fail(context, "diagram-shorthand-limit", "This architecture needs more than twelve native grid columns.", "Use native JSON with explicit component positions.");
                static double FitWidth(JsonObject node) => Math.Ceiling(Utils.TextUnits(node.Str("label")) * 6.6 + 16);
                // Order each column by where its upstream nodes sit, then centre it against the tallest column.
                var height = Enumerable.Range(0, cols).Max(col => placed.Count(n => Of(n, "col") == col));
                var rowOf = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var col = 0; col < cols; col++)
                {
                    var column = placed.Where(n => Of(n, "col") == col).ToList();
                    double Upstream(JsonObject node)
                    {
                        var upstream = edges.Where(e => e.Str("to") == node.Str("id") && rowOf.ContainsKey(e.Str("from")!)).Select(e => (double)rowOf[e.Str("from")!]).ToList();
                        return upstream.Count > 0 ? upstream.Aggregate(0.0, (sum, row) => sum + row) / upstream.Count : Of(node, "row");
                    }
                    Js.Sort(column, (a, b) => Upstream(a) - Upstream(b) is var d && d != 0 && !double.IsNaN(d) ? d : Of(a, "row") - Of(b, "row"));
                    for (var i = 0; i < column.Count; i++) rowOf[column[i].Str("id")!] = (height - column.Count) / 2 + i;
                }
                spec["layout"] = new JsonObject
                {
                    ["mode"] = "grid", ["cols"] = cols, ["cellW"] = Js.MaxOf(placed.Select(FitWidth), 176), ["cellH"] = 76, ["gapX"] = 100, ["gapY"] = 110,
                };
                spec["components"] = Array(placed.Select(node =>
                {
                    var component = With(node, ("row", rowOf[node.Str("id")!]));
                    if (FitWidth(node) > 120) component["size"] = new JsonArray(FitWidth(node), 60);
                    return component;
                }));
                spec["connections"] = Array(edges);
            }

            void Lanes(List<JsonObject> placed, List<JsonObject> edges, JsMap<string, JsonObject> lanes, JsMap<string, (string Lane, Context Context)> nodeLanes)
            {
                foreach (var (nodeId, (lane, ctx)) in nodeLanes.Entries)
                    if (!lanes.Has(lane)) throw Fail(ctx, "diagram-lane", $"Node {nodeId} names an undeclared lane.", "lane locate | Locate the cause");
                var order = lanes.Keys.ToList();
                var first = order[0];
                static int Of(JsonObject node, string key) => (int)node[key]!.D();
                var sorted = placed.ToList();
                Js.Sort(sorted, (a, b) => Of(a, "col") - Of(b, "col") is var d and not 0 ? d : Of(a, "row") - Of(b, "row"));
                var ranked = sorted.Select(n => With(Without(n, "row", "col"), ("lane", nodeLanes.Has(n.Str("id")!) ? nodeLanes.Get(n.Str("id")!).Lane : first))).ToList();
                // Prefer, in order: one row with phase labels above it; phase rows that step down so each hand-off edge drops
                // straight; phase rows that each restart at the left. Take the first within the readable width.
                int Rank(JsonObject node) => order.IndexOf(node.Str("lane")!);
                var row = ranked.ToList();
                Js.Sort(row, (a, b) => Rank(a) - Rank(b));
                var used = lanes.Values.Where(lane => ranked.Any(n => n.Str("lane") == lane.Str("id"))).ToList();
                var sizes = used.Select(lane => ranked.Count(n => n.Str("lane") == lane.Str("id"))).ToList();
                List<JsonObject> Wrapped(bool stair)
                {
                    var nextCol = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (var i = 0; i < used.Count; i++) nextCol[used[i].Str("id")!] = stair ? sizes.Take(i).Sum(size => size - 1) : 0;
                    return [.. ranked.Select(node => { var col = nextCol[node.Str("lane")!]; nextCol[node.Str("lane")!] = col + 1; return With(node, ("col", col)); })];
                }
                var single = row.Select((node, col) => With(Without(node, "lane"), ("lane", "flow"), ("col", col))).ToList();
                // Grouping by phase keeps the flow order only when each phase is one stretch of the flow.
                var inOrder = ranked.Select((node, i) => i == 0 || Rank(node) >= Rank(ranked[i - 1])).All(ok => ok);
                var candidates = new List<(List<JsonObject> Nodes, bool Single)>();
                if (inOrder && ranked.Count <= 6 && placed.All(n => Of(n, "row") == 0)) candidates.Add((single, true));
                if (inOrder && sizes.Sum() - (sizes.Count - 1) <= 6) candidates.Add((Wrapped(true), false));
                candidates.Add((Wrapped(false), false));
                var chosen = candidates.FirstOrDefault(c => WorkflowWidth(c.Nodes, edges) <= ReadableWorkflowWidth, candidates[^1]);
                spec["nodes"] = Array(chosen.Nodes);
                if (chosen.Single)
                {
                    spec["lanes"] = new JsonArray(new JsonObject { ["id"] = "flow", ["label"] = spec["meta"]!["title"]!.DeepClone() });
                    spec["phases"] = Array(order.Select(laneId => (laneId, cols: row.Select((node, col) => (node, col)).Where(x => x.node.Str("lane") == laneId).Select(x => x.col).ToList()))
                        .Where(phase => phase.cols.Count > 0)
                        .Select(phase => new JsonObject { ["id"] = phase.laneId, ["label"] = lanes.Get(phase.laneId)!["label"]!.DeepClone(), ["fromCol"] = phase.cols[0], ["toCol"] = phase.cols[^1] }));
                }
                else spec["lanes"] = Array(used.Select(lane => lane.DeepClone().AsObject()));
                if (chosen.Nodes.Any(n => Of(n, "col") > 5)) throw Fail(context, "diagram-shorthand-limit", "A workflow lane holds at most six nodes.", "Split the process into another lane.");
                spec["edges"] = Array(edges);
            }

            void Rows(List<JsonObject> placed, List<JsonObject> edges)
            {
                static int Of(JsonObject node, string key) => (int)node[key]!.D();
                // A process too wide for one readable row wraps by columns into stacked bands, keeping each band's branch
                // rows, and steps each band down under the end of the previous one where six columns allow.
                bool Fits(List<JsonObject> laid) => laid.All(n => Of(n, "col") <= 5) && WorkflowWidth(laid, edges) <= ReadableWorkflowWidth;
                List<JsonObject>? wrapped = null;
                if (!Fits(placed))
                {
                    var branchRows = placed.Max(n => Of(n, "row")) + 1;
                    for (var size = 5; size >= 2 && wrapped is null; size--)
                        foreach (var stair in new[] { true, false })
                        {
                            var laid = placed.Select(node =>
                            {
                                var band = Of(node, "col") / size;
                                return With(node, ("row", band * branchRows + Of(node, "row")), ("col", (stair ? band * (size - 1) : 0) + Of(node, "col") - band * size));
                            }).ToList();
                            var usedRows = laid.Select(n => Of(n, "row")).Distinct().Order().ToList();
                            var compact = laid.Select(node => With(node, ("row", usedRows.IndexOf(Of(node, "row"))))).ToList();
                            if (Fits(compact)) { wrapped = compact; break; }
                        }
                }
                var laidOut = wrapped ?? placed;
                if (laidOut.Any(n => Of(n, "col") > 5)) throw Fail(context, "diagram-shorthand-limit", "Native workflow columns are limited to 0–5.", "Declare phase lanes with \"lane <id> | label\" rows and assign nodes to them.");
                var metaTitle = spec["meta"]!["title"]!.GetValue<string>();
                // Decision 2: upstream labels every branch lane "分支 N" (diagrams.mjs:270); only a zh page keeps that.
                var zh = (locale ?? "en").StartsWith("zh", StringComparison.Ordinal);
                spec["lanes"] = Array(Enumerable.Range(0, Math.Max(1, laidOut.Count == 0 ? 0 : laidOut.Max(n => Of(n, "row") + 1))).Select(row => new JsonObject
                {
                    ["id"] = $"lane-{row}", ["label"] = wrapped is not null ? $"{metaTitle} · {row + 1}" : row > 0 ? (zh ? $"分支 {row}" : $"Branch {row}") : metaTitle,
                }));
                spec["nodes"] = Array(laidOut.Select(node => With(Without(node, "row"), ("lane", $"lane-{Of(node, "row")}"))));
                spec["edges"] = Array(edges);
            }
        }

        void Tree()
        {
            var nodes = entries.Select((entry, index) =>
            {
                Expect(entry, 2, 3, "api | API 服务 | root");
                rowMap[$"/nodes/{index}"] = entry.Context.Line!.Value;
                var node = new JsonObject { ["id"] = SafeId(entry.Parts[0], entry.Context), ["label"] = entry.Parts[1] };
                if (Part(entry, 2) is { Length: > 0 } parent && parent != "-") node["parent"] = SafeId(parent, entry.Context);
                return node;
            }).ToList();
            spec["nodes"] = new JsonArray([.. nodes]);
            DuplicateCheck(nodes, context, "node");
        }

        void Timeline()
        {
            spec["meta"]!["evidence"] = "illustrative";
            var lanes = new JsMap<string, JsonObject>();
            var events = new JsonArray();
            spec["events"] = events;
            foreach (var entry in entries.Where(e => e.Parts[0].StartsWith("lane ", StringComparison.Ordinal)))
            {
                Expect(entry, 2, 2, "lane release | 发布");
                var laneId = SafeId(Js.Trim(entry.Parts[0][5..]), entry.Context);
                if (lanes.Has(laneId)) throw Fail(entry.Context, "diagram-id-duplicate", "Timeline lane ids must be unique.", "lane release | 发布");
                lanes.Set(laneId, new JsonObject { ["id"] = laneId, ["label"] = entry.Parts[1] });
            }
            foreach (var entry in entries.Where(e => !e.Parts[0].StartsWith("lane ", StringComparison.Ordinal)))
            {
                Expect(entry, 3, 5, "deploy | 2026-10-01T10:00:00+08:00 | 发布 v2 | release | change");
                var lane = Part(entry, 3) is { Length: > 0 } authored ? authored : "events";
                SafeId(lane, entry.Context);
                if (!lanes.Has(lane)) lanes.Set(lane, new JsonObject { ["id"] = lane, ["label"] = lane });
                rowMap[$"/events/{events.Count}"] = entry.Context.Line!.Value;
                var item = new JsonObject { ["id"] = SafeId(entry.Parts[0], entry.Context), ["at"] = entry.Parts[1], ["title"] = entry.Parts[2], ["lane"] = lane };
                if (Part(entry, 4) is { Length: > 0 } kind) item["kind"] = kind;
                events.Add(item);
            }
            spec["lanes"] = new JsonArray([.. lanes.Values]);
            DuplicateCheck(events.Cast<JsonObject>(), context, "event");
        }

        void Erd()
        {
            var entities = new JsMap<string, JsonObject>();
            var relationships = new JsonArray();
            spec["relationships"] = relationships;
            foreach (var entry in entries.Where(e => e.Parts[0].StartsWith("entity ", StringComparison.Ordinal)))
            {
                Expect(entry, 2, 2, "entity user | 用户");
                var entityId = SafeId(Js.Trim(entry.Parts[0][7..]), entry.Context);
                if (entities.Has(entityId)) throw Fail(entry.Context, "diagram-id-duplicate", "Entity ids must be unique.", "entity user | 用户");
                entities.Set(entityId, new JsonObject { ["id"] = entityId, ["label"] = entry.Parts[1], ["row"] = 0, ["col"] = entities.Count, ["attributes"] = new JsonArray() });
            }
            foreach (var entry in entries.Where(e => !e.Parts[0].StartsWith("entity ", StringComparison.Ordinal)))
            {
                var route = Route().Match(entry.Parts[0]);
                if (route.Success)
                {
                    Expect(entry, 2, 3, "order > user | many:one | 下单者");
                    var cardinality = Cardinality().Match(entry.Parts[1]);
                    if (!cardinality.Success) throw Fail(entry.Context, "diagram-cardinality", "Relationship cardinality must be explicit.", "order > user | many:one | 下单者");
                    var (from, to) = (route.Groups[1].Value, route.Groups[2].Value);
                    var relationship = new JsonObject
                    {
                        ["id"] = EdgeId(from, to), ["from"] = from, ["to"] = to,
                        ["fromCardinality"] = cardinality.Groups[1].Value, ["toCardinality"] = cardinality.Groups[2].Value,
                    };
                    if (Part(entry, 2) is { Length: > 0 } label) relationship["label"] = label;
                    relationships.Add(relationship);
                }
                else
                {
                    Expect(entry, 2, 4, "order.user_id | bigint | fk | user.id");
                    var field = Field().Match(entry.Parts[0]);
                    if (!field.Success || !entities.Has(field.Groups[1].Value)) throw Fail(entry.Context, "diagram-field", "A field must name an explicitly declared entity.", "entity user | 用户\nuser.id | bigint | pk");
                    var attribute = new JsonObject { ["name"] = field.Groups[2].Value, ["type"] = entry.Parts[1] };
                    var roles = Part(entry, 2)?.Split(',').Select(Js.Trim).Where(role => role.Length > 0).ToList();
                    if (roles is { Count: > 0 }) attribute["key"] = roles.Count == 1 ? roles[0] : new JsonArray([.. roles.Select(role => (JsonNode?)role)]);
                    if (Part(entry, 3) is { Length: > 0 } references) attribute["references"] = references;
                    entities.Get(field.Groups[1].Value)!["attributes"]!.AsArray().Add(attribute);
                }
            }
            spec["layout"] = new JsonObject { ["mode"] = "grid", ["gapX"] = 96, ["gapY"] = 64 };
            spec["entities"] = new JsonArray([.. entities.Values]);
        }
    }

    // The JS view of a thrown diagnostic error: {message, archifyDiagnostics}.
    internal static JsonObject NativeError(ArchifyDiagnosticException error) => new()
    {
        ["message"] = error.Message, ["archifyDiagnostics"] = new JsonArray([.. error.Diagnostics.Select(d => (JsonNode?)d.ToJson())]),
    };

    // diagrams.mjs:337-352. `error` is the JS view of what failed: a thrown error ({message, archifyDiagnostics}), a native
    // process's JSON output ({diagnostics}) or a checker receipt ({checks, composition}).
    internal static Exception RethrowNative(JsonObject error, Context context, string reason)
    {
        var details = (error["archifyDiagnostics"] as JsonArray ?? error["diagnostics"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        if (details.Count == 0 && error["checks"] is JsonArray checks)
        {
            details = [.. checks.OfType<JsonObject>().Where(check => !check["ok"].Truthy()).SelectMany(check =>
                (check["details"] is JsonArray { Count: > 0 } d ? d.Select(Js.String) : [$"Native check {Js.String(check["name"])} failed."])
                .Select(message => new JsonObject
                {
                    ["code"] = $"check/{Js.String(check["name"])}", ["severity"] = "error", ["message"] = message, ["evidence"] = check.DeepClone(),
                }))];
            if (details.Count == 0)
            {
                details = [.. (error["composition"]?["issues"] as JsonArray ?? []).OfType<JsonObject>().Where(issue => issue.Str("severity") == "error").Select(issue =>
                {
                    var copy = issue.DeepClone().AsObject();
                    copy["message"] = issue.Str("code") == "composition/desktop-readability" && issue["projectedFontPx"].Truthy()
                        ? $"Text \"{Js.String(issue["text"])}\" on {(issue["owner"]?["kind"].Truthy() == true ? Js.String(issue["owner"]!["kind"]) : "element")} {(issue["owner"]?["id"].Truthy() == true ? Js.String(issue["owner"]!["id"]) : "")} would show at {Js.ToFixed(issue["projectedFontPx"]!.D(), 1)}px, below {Js.String(issue["minimumProjectedFontPx"])}px: the diagram is {Js.Num(Js.Round(issue["viewBoxWidth"]?.D() ?? double.NaN))}px wide and shrinks to {Js.Num(Js.Round((issue["scale"]?.D() ?? double.NaN) * 100))}% on the page. Make it narrower: at most four or five nodes per row, shorter node labels, or phases."
                        : issue["detail"].Truthy() ? Js.String(issue["detail"]) : $"Native quality check {Js.String(issue["code"])} failed.";
                    return copy;
                })];
            }
        }
        if (details.Count > 0)
        {
            return Throw("Native diagram validation failed.", details.Select(entry =>
            {
                var path = entry["subject"]?["path"] is JsonValue p && p.TryGetValue<string>(out var s) ? s : null;
                var row = path is null ? null : RowPath().Match(path) is { Success: true } m && context.RowMap?.TryGetValue(m.Value, out var line) == true ? line : (int?)null;
                var pointer = !string.IsNullOrEmpty(context.Pointer) && !string.IsNullOrEmpty(path) && context.RowMap is null ? $"{context.Pointer}/spec{path}"
                    : string.IsNullOrEmpty(context.Pointer) ? "/" : context.Pointer;
                var fixes = entry["supportedFixes"] is JsonArray { Count: > 0 } f ? f.Select(Js.String).ToList() : ["Repair the native diagnostic and render again."];
                return new Diagnostic($"explainer/{reason}", entry["severity"].Truthy() && entry.Str("severity") == "warning" ? Severity.Warning : Severity.Error,
                    entry.Str("message") ?? "",
                    SubjectOf(context with { Line = row is > 0 ? row : context.Line }, pointer, new JsonObject { ["diagramType"] = context.DiagramType, ["diagram"] = context.Id }),
                    new JsonObject { ["reason"] = reason, ["example"] = $"Use a valid native {(string.IsNullOrEmpty(context.DiagramType) ? "diagram" : context.DiagramType)} JSON specification.", ["native"] = entry.DeepClone() },
                    fixes, null);
            }));
        }
        var text = error["message"].Truthy() ? Js.String(error["message"]) : "[object Object]";
        return Fail(context, reason, "The native diagram toolchain could not complete this diagram.",
            $"Use a valid native {(string.IsNullOrEmpty(context.DiagramType) ? "diagram" : context.DiagramType)} JSON specification.", [],
            new JsonObject { ["message"] = text.Length > 1000 ? text[..1000] : text });
    }
}
