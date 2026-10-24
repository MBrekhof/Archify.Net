using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net;

// archify 3.2.0 explainer/validate.mjs: structural and semantic author validation. Resource preparation owns byte and Git
// verification; deferred references into external native files are checked after the diagrams are prepared. The document is
// a JsonNode: a JSON-format draft can parse to an array, which the schema then rejects.
internal static class ExplainerValidator
{
    static readonly Regex Id = new(@"\A[A-Za-z][A-Za-z0-9_-]*\z");
    static readonly Regex BlockPath = new(@"^/panels/[0-9]+/blocks/[0-9]+");
    static readonly HashSet<string> ReaderIds = new(StringComparer.Ordinal) { "board", "diagram-dialog", "diagram-dialog-title", "toast" };
    static readonly Dictionary<string, string> NodeKeys = new(StringComparer.Ordinal)
    {
        ["architecture"] = "components", ["sequence"] = "participants", ["lifecycle"] = "states", ["erd"] = "entities", ["class"] = "types",
        ["timeline"] = "events", ["waterfall"] = "spans", ["workflow"] = "nodes", ["dataflow"] = "nodes", ["tree"] = "nodes",
    };
    static readonly Dictionary<string, string> EdgeKeys = new(StringComparer.Ordinal)
    {
        ["architecture"] = "connections", ["sequence"] = "messages", ["lifecycle"] = "transitions", ["erd"] = "relationships",
        ["class"] = "relationships", ["workflow"] = "edges", ["dataflow"] = "edges",
    };

    // `at` (validate.mjs:12): a JSON pointer walked with optional chaining.
    static JsonNode? At(JsonNode? document, string pointer)
    {
        var value = document;
        foreach (var raw in pointer.Split('/').Skip(1))
        {
            var key = raw.Replace("~1", "/").Replace("~0", "~");
            value = value switch
            {
                JsonObject o => o[key],
                JsonArray a => ExplainerCharts.IsArrayIndex(key) && ulong.Parse(key) < (ulong)a.Count ? a[int.Parse(key)] : null,
                _ => null,
            };
        }
        return value;
    }

    sealed record Target(string Pointer, JsonNode Component, string? PanelId);
    sealed record DiagramEntry(JsonObject Block, string Pointer, string? PanelId);

    /// <summary>validateExplainer (validate.mjs:17-138). A front-matter PeleException from a chart fence propagates.</summary>
    public static (bool Ok, List<Diagnostic> Diagnostics) Validate(JsonNode document, JsonObject? sourceMap, string? inputPath, string writingMode = "warn")
    {
        sourceMap ??= [];
        var diagnostics = new List<Diagnostic>();
        var entries = new List<WritingEntry>();
        JsonObject Location(string pointer, JsonNode component, string? panelId)
        {
            var key = pointer;
            JsonNode? value = null;
            var found = false;
            while (!found)
            {
                found = sourceMap.TryGetPropertyValue(key, out value);
                if (key.Length == 0) break;
                var slash = key.LastIndexOf('/');
                key = slash < 0 ? key[..^1] : key[..slash];
            }
            var location = value as JsonObject;
            var subject = new JsonObject
            {
                ["line"] = location?["line"] is { } line && line.Truthy() ? line.DeepClone() : 1,
                ["column"] = location?["column"] is { } column && column.Truthy() ? column.DeepClone() : 1,
                ["component"] = component.DeepClone(), ["path"] = pointer,
            };
            if (!string.IsNullOrEmpty(panelId)) subject["panelId"] = panelId;
            if (!string.IsNullOrEmpty(inputPath)) subject["input"] = inputPath;
            return subject;
        }
        void Fail(string code, string pointer, JsonNode component, string reason, string example, string? panelId = null) =>
            diagnostics.Add(new($"explainer/{code}", Severity.Error, reason, Location(pointer, component, panelId),
                new JsonObject { ["reason"] = reason, ["example"] = example }, [$"Use a valid form, for example: {example}"], null));

        var errors = SchemaValidator.Errors(DiagramType.Explainer, document);
        if (errors.Count > 0)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var error in errors)
            {
                if (error.Keyword == "if") continue;
                var pointer = error.InstancePath;
                if (error.Keyword == "required") pointer += $"/{Js.String(error.Params["missingProperty"])}";
                if (error.Keyword == "additionalProperties") pointer += $"/{Js.String(error.Params["additionalProperty"])}";
                var blockPath = BlockPath.Match(pointer);
                JsonNode component = blockPath.Success ? (At(document, blockPath.Value) is JsonObject block && block["type"].Truthy() ? block["type"]!.DeepClone() : "block")
                    : pointer.StartsWith("/sources/", StringComparison.Ordinal) ? "source" : pointer.StartsWith("/panels/", StringComparison.Ordinal) ? "panel" : "document";
                var reason = $"{(pointer.Length > 0 ? pointer : "/")} {error.Message}";
                if (!seen.Add($"{error.Keyword}\0{pointer}\0{Js.Stringify(error.Params)}")) continue;
                var type = error.Params["type"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : null;
                var componentName = Js.String(component);
                var example = error.Keyword switch
                {
                    "required" => $"Add {Js.String(error.Params["missingProperty"])} to the {componentName}.",
                    "additionalProperties" => $"Remove unsupported {Js.String(error.Params["additionalProperty"])}.",
                    _ when pointer == "/meta/output" => "\"output\":\"answer.html\"",
                    "type" => type switch
                    {
                        "number" => "value=12.5", "boolean" => "multiple=true", "array" => "\"items\":[\"First item\",\"Second item\"]",
                        "object" => "\"meta\":{\"id\":\"answer-page\",\"title\":\"Answer\"}", _ => "\"text\":\"A direct explanation.\"",
                    },
                    "const" => $"Use {Js.Stringify(error.Params["allowedValue"])}.",
                    "enum" => $"Choose {string.Join(", ", error.Params["allowedValues"]!.AsArray().Select(v => Js.Stringify(v)))}.",
                    "oneOf" when component is JsonValue cv && cv.TryGetValue<string>(out var cs) && cs == "diagram" => "@diagram sequence source=request.sequence.json",
                    "minimum" or "exclusiveMinimum" or "maximum" =>
                        $"Use a finite value satisfying the stated bound, such as {Js.Num(error.Params["limit"]!.D() + (error.Keyword == "exclusiveMinimum" ? 1 : 0))}.",
                    "pattern" => pointer.EndsWith("/revision", StringComparison.Ordinal) ? "revision=0123456789abcdef0123456789abcdef01234567" : "id=request-path",
                    "minItems" => "| a | First option |\n| b | Second option |",
                    "minLength" => "text=\"A direct explanation.\"",
                    _ => componentName == "panel" && component is JsonValue pv && pv.TryGetValue<string>(out _) ? "## Why does this happen? {#reasoning}\nA direct explanation."
                        : componentName == "source" && component is JsonValue sv && sv.TryGetValue<string>(out _) ? "@source facts example title=\"Stated facts\"" : $"@{componentName} id=example",
                };
                Fail($"schema-{error.Keyword}", pointer.Length > 0 ? pointer : "/", component, reason, example);
            }
            return (false, diagnostics);
        }

        var doc = document.AsObject();
        var meta = doc["meta"]!.AsObject();
        var answer = doc["answer"]!.AsObject();
        var sources = doc["sources"]!.AsArray().Cast<JsonObject>().ToList();
        var panels = doc["panels"]!.AsArray().Cast<JsonObject>().ToList();
        var targets = new JsMap<string, Target>();
        targets.Set("answer", new("/answer", "answer", null));
        var sourceIds = new JsMap<string, Target>();
        var diagrams = new JsMap<string, DiagramEntry>();
        void Identity(string value, string pointer, string component, string? panelId, JsMap<string, Target>? registry = null)
        {
            registry ??= targets;
            if (!Id.IsMatch(value) || value.Length > 100) Fail("id", pointer, component, "IDs require an ASCII letter followed by letters, digits, hyphens or underscores.", "id=request-path", panelId);
            if (registry.Has(value)) Fail("duplicate-id", pointer, component, "This identity is already used. Use an explicit distinct ID.", "id=request-path-branch", panelId);
            registry.Set(value, new(pointer, component, panelId));
            if (registry == targets && (value.StartsWith("explainer-", StringComparison.Ordinal) || ReaderIds.Contains(value)))
                Fail("reserved-id", pointer, component, "This identity is reserved for reader controls or generated footnotes.", "id=request-path", panelId);
        }
        for (var si = 0; si < sources.Count; si++) Identity(sources[si].Str("id")!, $"/sources/{si}/id", "source", null, sourceIds);
        for (var pi = 0; pi < panels.Count; pi++)
        {
            var panel = panels[pi];
            var pp = $"/panels/{pi}";
            Identity(panel.Str("id")!, $"{pp}/id", "panel", panel.Str("id"));
            var blocks = panel["blocks"]!.AsArray().Cast<JsonObject>().ToList();
            for (var bi = 0; bi < blocks.Count; bi++)
            {
                var block = blocks[bi];
                var bp = $"{pp}/blocks/{bi}";
                var type = block.Str("type")!;
                Identity(block.Str("id")!, $"{bp}/id", type, panel.Str("id"));
                if (type == "diagram") diagrams.Set(block.Str("id")!, new(block, bp, panel.Str("id")));
                if (type is "comparison" or "steps")
                {
                    var property = block["rows"].Truthy() ? "rows" : "items";
                    var items = block[property]!.AsArray();
                    for (var ii = 0; ii < items.Count; ii++) Identity(items[ii].Str("id")!, $"{bp}/{property}/{ii}/id", type, panel.Str("id"));
                }
            }
        }
        foreach (var sourceId in sourceIds.Keys)
            if (targets.Has($"source-{sourceId}")) Fail("reserved-id", targets.Get($"source-{sourceId}")!.Pointer, "source", "This identity collides with a generated source anchor.", "id=reader-section");

        void Reference<T>(string? value, string pointer, string component, string? panelId, JsMap<string, T> registry, string example)
        {
            if (value is null || !registry.Has(value)) Fail("unresolved-reference", pointer, component, "The referenced identity does not exist or has the wrong component type.", example, panelId);
        }
        void References<T>(JsonArray? values, string pointer, string component, string? panelId, JsMap<string, T> registry, string example)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < (values?.Count ?? 0); i++)
            {
                var value = values![i] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                Reference(value, $"{pointer}/{i}", component, panelId, registry, example);
                if (value is not null && !seen.Add(value)) Fail("duplicate-reference", $"{pointer}/{i}", component, "Each reference may occur only once.", "Use each ID once.", panelId);
            }
        }
        // ponytail: a spec whose node or edge list is not an array reads as empty; upstream's `.map`/`.filter` throws a TypeError there.
        void DiagramTarget(JsonObject target, string pointer, string component, string? panelId)
        {
            if (target.ContainsKey("node") && target.ContainsKey("route"))
                Fail("diagram-target-exclusive", pointer, component, "A diagram target cannot select a node and a route at the same time.",
                    "{\"diagram\":\"request-path\",\"node\":\"api\"} or {\"diagram\":\"request-path\",\"route\":[\"api\",\"redis\"]}", panelId);
            var diagramId = target.Str("diagram");
            Reference(diagramId, $"{pointer}/diagram", component, panelId, diagrams, "Use the ID of a diagram block, such as request-path.");
            var diagram = diagramId is null ? null : diagrams.Get(diagramId);
            if (diagram is null || !diagram.Block["spec"].Truthy()) return;
            var spec = diagram.Block["spec"]!;
            var diagramType = diagram.Block.Str("diagramType") ?? "undefined";
            var nodes = new HashSet<string>(spec.Arr(NodeKeys.GetValueOrDefault(diagramType, "undefined"))?.Select(node => (node as JsonObject).Str("id")).OfType<string>() ?? [], StringComparer.Ordinal);
            var edges = spec.Arr(EdgeKeys.GetValueOrDefault(diagramType, "undefined"))?.OfType<JsonObject>().ToList() ?? [];
            if (target.ContainsKey("node") && !(target.Str("node") is { } node && nodes.Contains(node)))
                Fail("diagram-node", $"{pointer}/node", component, "The node does not exist in the authored native diagram.", "node=api", panelId);
            foreach (var endpoint in target.Arr("route") ?? [])
                if (!(endpoint is JsonValue ev && ev.TryGetValue<string>(out var e) && nodes.Contains(e)))
                    Fail("diagram-route", $"{pointer}/route", component, "A route endpoint does not exist in the authored native diagram.", "route=api~redis", panelId);
            if (target["edges"].Truthy())
            {
                var registry = new JsMap<string, JsonObject>();
                foreach (var edge in edges) if (edge["id"].Truthy() && edge.Str("id") is { } edgeId) registry.Set(edgeId, edge);
                References(target.Arr("edges"), $"{pointer}/edges", component, panelId, registry, "Use an edge ID from the native diagram.");
            }
        }
        string Prose(JsonArray tokens) => string.Concat(tokens.Cast<JsonObject>().Select(token =>
            token.Str("type") == "code" ? "" : token["children"] is JsonArray children ? Prose(children) : token.Str("type") == "link" ? token.Str("label") : token.Str("value") ?? ""));
        void Text(JsonNode? node, string pointer, string component, string? panelId = null)
        {
            if (node is not JsonValue v || !v.TryGetValue<string>(out var value)) return;
            var where = Location(pointer, component, panelId);
            var parsed = ExplainerInline.Parse(value, new InlineOptions(where["line"] is JsonValue lv && lv.GetValueKind() == System.Text.Json.JsonValueKind.Number ? lv.D() : null,
                component, where.Str("panelId"), inputPath));
            foreach (var entry in parsed.Diagnostics)
            {
                var subject = entry.Subject.DeepClone().AsObject();
                subject["path"] = pointer;
                diagnostics.Add(entry with { Subject = subject });
            }
            double? line = where["line"] is JsonValue wl && wl.GetValueKind() == System.Text.Json.JsonValueKind.Number ? wl.D() : null;
            if (component != "source") entries.Add(new(Prose(parsed.Tokens), line, component, string.IsNullOrEmpty(panelId) ? null : panelId, pointer, value));
            foreach (var reference in parsed.References.Cast<JsonObject>())
                switch (reference.Str("kind"))
                {
                    case "source": Reference(reference.Str("id"), pointer, component, panelId, sourceIds, "[Evidence](source:known-source)"); break;
                    case "block": Reference(reference.Str("id"), pointer, component, panelId, targets, "[Read explanation](block:known-block)"); break;
                    case "node": DiagramTarget(new JsonObject { ["diagram"] = reference.Str("diagram"), ["node"] = reference.Str("node") }, pointer, component, panelId); break;
                    case "route":
                        DiagramTarget(new JsonObject { ["diagram"] = reference.Str("diagram"), ["route"] = new JsonArray(reference.Str("from"), reference.Str("to")) }, pointer, component, panelId);
                        break;
                    case "typical" when component is not ("source" or "scope"):
                        entries.Add(new(reference.Str("text"), line, "typical", string.IsNullOrEmpty(panelId) ? null : panelId));
                        break;
                }
        }
        Text(meta["title"], "/meta/title", "title");
        Text(answer["summary"], "/answer/summary", "answer");
        var reasons = answer.Arr("reasons") ?? [];
        for (var i = 0; i < reasons.Count; i++) Text(reasons[i], $"/answer/reasons/{i}", "answer");
        Text(answer["scope"], "/answer/scope", "scope");
        for (var si = 0; si < sources.Count; si++)
        {
            var source = sources[si];
            var pointer = $"/sources/{si}";
            Text(source["title"], $"{pointer}/title", "source");
            if (source.Arr("lines") is { } lines && lines[1]!.D() < lines[0]!.D()) Fail("source-lines", $"{pointer}/lines", "source", "The repository line range must be ordered.", "lines=10:20");
            if (source.Str("url") is { Length: > 0 } url && !ExplainerInline.CredentialFreeHttpUrl(url))
                Fail("source-url", $"{pointer}/url", "source", "Source URLs must be credential-free HTTP(S) links.", "url=https://example.com/docs");
        }
        for (var pi = 0; pi < panels.Count; pi++)
        {
            var panel = panels[pi];
            var pp = $"/panels/{pi}";
            var panelId = panel.Str("id");
            Text(panel["title"], $"{pp}/title", "panel", panelId);
            var blocks = panel["blocks"]!.AsArray().Cast<JsonObject>().ToList();
            for (var bi = 0; bi < blocks.Count; bi++)
            {
                var block = blocks[bi];
                var bp = $"{pp}/blocks/{bi}";
                var component = block.Str("type")!;
                if (component == "diff")
                {
                    var where = Location($"{bp}/text", component, panelId);
                    diagnostics.AddRange(ExplainerDiff.Parse(block.Str("text")!, new(where["line"]!.D(), component, inputPath, where.Str("panelId"), where.Str("path"))).Diagnostics);
                }
                if (component == "chart" && block.ContainsKey("text") == block.ContainsKey("data"))
                    Fail("chart", bp, component, "A chart has either fence text or a data file, not both or neither.", "@chart type=bar data=latency.csv x=day y=p99", panelId);
                if (component == "chart" && block.ContainsKey("text"))
                {
                    var chart = ExplainerCharts.Compile(block.Str("text")!);
                    if (chart.Error is not null)
                    {
                        var before = diagnostics.Count;
                        Fail("chart", $"{bp}/text", component, chart.Error, chart.Example!, panelId);
                        if (chart.Line is > 0) diagnostics[before].Subject["line"] = diagnostics[before].Subject["line"]!.D() + chart.Line.Value - 1;
                    }
                }
                foreach (var key in new[] { "text", "title", "label", "note", "caption", "alt" })
                    if (component is not ("diff" or "snippet" or "chart") || key != "text") Text(block[key], $"{bp}/{key}", component, panelId);
                if (component == "list")
                {
                    var items = block["items"]!.AsArray();
                    for (var i = 0; i < items.Count; i++) Text(items[i], $"{bp}/items/{i}", component, panelId);
                }
                if (component == "facts")
                {
                    var facts = block["entries"]!.AsArray();
                    for (var i = 0; i < facts.Count; i++) { Text(facts[i]!["key"], $"{bp}/entries/{i}/key", component, panelId); Text(facts[i]!["value"], $"{bp}/entries/{i}/value", component, panelId); }
                }
                if (component == "comparison")
                {
                    var columns = block["columns"]!.AsArray();
                    for (var i = 0; i < columns.Count; i++) Text(columns[i], $"{bp}/columns/{i}", component, panelId);
                    Text(block["labelColumn"], $"{bp}/labelColumn", component, panelId);
                    var rows = block["rows"]!.AsArray();
                    for (var i = 0; i < rows.Count; i++)
                    {
                        var rp = $"{bp}/rows/{i}";
                        Text(rows[i]!["label"], $"{rp}/label", component, panelId);
                        var cells = rows[i]!["cells"]!.AsArray();
                        if (cells.Count != columns.Count) Fail("table-width", $"{rp}/cells", component, "Each row must have one cell for every comparison column.", "| Criterion | Option A | Option B |", panelId);
                        for (var j = 0; j < cells.Count; j++)
                        {
                            Text(cells[j]!["text"], $"{rp}/cells/{j}/text", component, panelId);
                            Text(cells[j]!["verdict"], $"{rp}/cells/{j}/verdict", component, panelId);
                        }
                    }
                }
                if (component == "steps")
                {
                    var items = block["items"]!.AsArray();
                    for (var i = 0; i < items.Count; i++)
                    {
                        Text(items[i]!["text"], $"{bp}/items/{i}/text", component, panelId);
                        Text(items[i]!["detail"], $"{bp}/items/{i}/detail", component, panelId);
                        if (items[i]!["target"] is JsonObject target) DiagramTarget(target, $"{bp}/items/{i}/target", component, panelId);
                    }
                }
                if (component == "code")
                {
                    var source = block.Str("source");
                    Reference(source, $"{bp}/source", component, panelId, sourceIds, "source=repository-snippet");
                    if (source is not null && sourceIds.Has(source) && !sources.First(s => s.Str("id") == source).ContainsKey("path"))
                        Fail("code-source-kind", $"{bp}/source", component, "Code blocks require a repository source whose commit and lines can be checked.", "@source snippet title=\"Code\" path=src/router.js lines=1:10", panelId);
                }
                if (component == "diagram" && block["nodeSources"] is JsonObject nodeSources)
                    foreach (var (node, source) in nodeSources)
                        Reference(source is JsonValue nv && nv.TryGetValue<string>(out var ns) ? ns : null, $"{bp}/nodeSources/{node}", component, panelId, sourceIds, $"| {node} | Label | backend | source:router |");
                if (component == "diagram" && block["spec"] is JsonObject spec)
                {
                    var diagramType = block.Str("diagramType")!;
                    if (!(spec["diagram_type"] is JsonValue dt && dt.TryGetValue<string>(out var declared) && declared == diagramType))
                        Fail("diagram-type", $"{bp}/spec/diagram_type", component, "The native specification must match the declared diagram type.", "@sequence source=request.sequence.json", panelId);
                    try { SchemaValidator.Validate(DiagramTypes.Parse(diagramType), spec, new DiagnosticSink()); }
                    catch (ArchifyDiagnosticException error)
                    {
                        foreach (var entry in error.Diagnostics)
                        {
                            var path = entry.Subject.Str("path");
                            var evidence = entry.Evidence.DeepClone().AsObject();
                            evidence["reason"] = entry.Message;
                            evidence["example"] = $"Use valid native {diagramType} JSON.";
                            diagnostics.Add(entry with { Code = $"explainer/native-{entry.Code}", Subject = Location($"{bp}/spec{(path == "/" ? "" : path ?? "")}", component, panelId), Evidence = evidence });
                        }
                    }
                }
            }
        }
        diagnostics.AddRange(ExplainerWriting.Lint(entries, writingMode, meta.Str("locale") is { Length: > 0 } locale ? locale : "auto").Select(entry =>
        {
            if (string.IsNullOrEmpty(inputPath)) return entry;
            var subject = entry.Subject.DeepClone().AsObject();
            subject["input"] = inputPath;
            return entry with { Subject = subject };
        }));
        return (!diagnostics.Any(entry => entry.Severity == Severity.Error), diagnostics);
    }
}
