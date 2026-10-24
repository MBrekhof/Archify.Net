using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net;

// parseExplainer's result. Document is null when any diagnostic is an error; a JSON-format draft's document is whatever
// JSON.parse returned (an array too), so it is a JsonNode. Source-map entries are {line, column, start, end}, with start and
// end UTF-16 offsets into SourceText.
internal sealed record ExplainerParse(JsonNode? Document, JsonObject SourceMap, string Format, string SourceText, IReadOnlyList<Diagnostic> Diagnostics)
{
    public JsonObject ToJson() => new()
    {
        ["document"] = Document?.DeepClone(), ["sourceMap"] = SourceMap.DeepClone(), ["format"] = Format, ["sourceText"] = SourceText,
        ["diagnostics"] = new JsonArray([.. Diagnostics.Select(d => (JsonNode?)d.ToJson())]),
    };
}

// archify 3.2.0 explainer/parse.mjs: the restricted Markdown author grammar, and JSON drafts with exact value locations.
// While a Markdown draft is built, a JsonObject member holding C# null is a JS `undefined` property: it is mapped in the
// source map, as Object.entries sees it, and left out of the document and of every JSON.stringify, as JS leaves it out.
internal static partial class ExplainerParser
{
    static readonly HashSet<string> Types = new(StringComparer.Ordinal)
        { "architecture", "workflow", "sequence", "dataflow", "lifecycle", "erd", "class", "tree", "timeline", "waterfall" };

    // JS regex classes: \s is JS whitespace, `.` stops at a line terminator, `$` (no m flag) is the end of input.
    const string Ws = Js.WsClass;
    static readonly string WsIn = Js.WsClass[1..^1];
    const string Nl = "[^\n\r\x2028\x2029]";
    static readonly Regex ReId = new(@"\A[A-Za-z][A-Za-z0-9_-]*\z");
    static readonly Regex ReRowLabel = new($@"\A({Nl}*?){Ws}+\{{#([^}}{WsIn}]+)\}}\z");
    static readonly Regex ReUnsupported = new($@"\A{Ws}*(?:>+|#{{3,}}{Ws}|(?:---|\*\*\*|___){Ws}*\z|```|~~~|[-*+]{Ws}+\[[ xX]\])");
    // UPSTREAM-DEVIATION (heading-terminator): upstream's `.` stops at a line terminator, so a heading line holding a lone CR
    // (classic Mac line endings) or U+2028 does not match and `match[1]` throws a TypeError out of parseExplainer. Here `.`
    // spans them: the heading takes the whole line and the draft gets the ordinary diagnostics instead of a crash.
    static readonly Regex ReHeading = new($@"\A(.*?)(?:{Ws}+\{{(?:#([^}}{WsIn}]+))?(?:{Ws}*span=([0-9]+))?\}})?\z", RegexOptions.Singleline);
    static readonly Regex ReNumber = new(@"\A[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?\z");
    static readonly Regex ReDirective = new($@"\A@([a-z][a-z-]*)(?:{Ws}+({Nl}*))?\z");
    static readonly Regex ReRowEnd = new($@"\A(?:#{{1,2}}{Ws}|@)");
    static readonly Regex ReLineNumbers = new($@"\A({Ws}*)\|\|(?={Ws})");
    static readonly Regex ReBlockStart = new($@"\A(?:#{{1,2}}{Ws}|@|\||[-*+]{Ws}|[0-9]+\.{Ws}|```|~~~)");
    static readonly Regex ReSourceCell = new(@"\Asource:[A-Za-z][A-Za-z0-9_-]*\z");
    static readonly Regex ReNodePrefix = new($@"\A(?:actor|entity){Ws}+");
    static readonly Regex ReLines = new(@"\A([0-9]+):([0-9]+)\z");
    static readonly Regex ReAssessment = new($@"\A({Nl}*?){Ws}*\{{(good|caution|bad)(?::([^}}]+))?\}}((?:{Ws}*\[[^\]]*\]\([^){WsIn}]*\))*){Ws}*\z");
    static readonly Regex ReFence = new($@"\A(```+|~~~+){Ws}*([A-Za-z0-9_+.#-]*){Ws}*({Nl}*)\z");
    static readonly Regex ReSeparator = new(@"\A:?-{3,}:?\z");
    static readonly Regex ReList = new($@"\A(?:([-*+])|([0-9]+)\.){Ws}+({Nl}+)\z");
    static readonly Regex ReIndent = new($@"\A{Ws}{{2,}}");
    static readonly Regex ReTask = new($@"\A\[[ xX]\]{Ws}+");
    static readonly Regex RePosition = new("position ([0-9]+)");
    static readonly Regex ReLine = new("line ([0-9]+)");

    static string Stable(string kind, string value) => $"{kind}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12]}";
    static string PointerKey(string value) => value.Replace("~", "~0").Replace("/", "~1");
    static List<string> Csv(string? value) => string.IsNullOrEmpty(value) ? [] : [.. value.Split(',').Select(Js.Trim).Where(part => part.Length > 0)];
    static JsonArray Strings(IEnumerable<string> values) => [.. values.Select(v => (JsonNode?)v)];

    // A copy without the undefined (C# null) members; an inline `spec` holds real JSON nulls and is copied as is.
    static JsonNode? Defined(JsonNode? value) => value switch
    {
        JsonObject o => new JsonObject(o.Where(kv => kv.Value is not null).Select(kv => KeyValuePair.Create(kv.Key, kv.Key == "spec" ? kv.Value!.DeepClone() : Defined(kv.Value)))),
        JsonArray a => new JsonArray([.. a.Select(Defined)]),
        _ => value?.DeepClone(),
    };
    static string Stringify(JsonNode? value) => Js.Stringify(Defined(value));

    /// <summary>parseExplainer (parse.mjs:18-224).</summary>
    internal static ExplainerParse Parse(string sourceText, string? inputPath = null, string? output = null) =>
        new Run(sourceText, inputPath, output is null ? null : output.Split('\\', '/')[^1]).Result();

    sealed class Row(List<string> cells, int line)
    {
        public List<string> Cells = cells;
        public readonly int Line = line;
    }

    sealed class Run(string text, string? inputPath, string? output)
    {
        readonly JsonObject sourceMap = new();
        readonly List<Diagnostic> diagnostics = [];
        readonly List<int> starts = [0];
        string[] lines = [];

        public ExplainerParse Result()
        {
            var format = Js.TrimStart(text).StartsWith('{') || Js.TrimStart(text).StartsWith('[') ? "json" : "markdown";
            for (var i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
            lines = [.. text.Split('\n').Select(line => line.EndsWith('\r') ? line[..^1] : line)];
            var document = format == "json" ? Json() : Markdown();
            Identities(document);
            return new(diagnostics.Any(d => d.Severity == Severity.Error) ? null : document, sourceMap, format, text, diagnostics);
        }

        int StartOf(int line) => line < starts.Count ? starts[line] : text.Length;

        JsonObject Locate(int start, int? end = null)
        {
            int lo = 0, hi = starts.Count;
            while (lo + 1 < hi) { var mid = (lo + hi) >> 1; if (starts[mid] <= start) lo = mid; else hi = mid; }
            return new JsonObject { ["line"] = lo + 1, ["column"] = start - starts[lo] + 1, ["start"] = start, ["end"] = end ?? start };
        }

        JsonObject Descriptor(int line, int? endLine = null) => Locate(StartOf(line), StartOf(endLine ?? line + 1));

        void Map(string pointer, JsonNode? value, JsonObject descriptor)
        {
            sourceMap[pointer] = descriptor.DeepClone();
            if (value is JsonArray array) for (var i = 0; i < array.Count; i++) Map($"{pointer}/{i}", array[i], descriptor);
            else if (value is JsonObject obj) foreach (var (key, entry) in obj.ToList()) Map($"{pointer}/{PointerKey(key)}", entry, descriptor);
        }

        void Fail(double line, JsonNode component, string reason, string example, string path = "/")
        {
            var subject = new JsonObject { ["line"] = line, ["column"] = 1, ["component"] = component.DeepClone(), ["path"] = path };
            if (!string.IsNullOrEmpty(inputPath)) subject["input"] = inputPath;
            diagnostics.Add(new($"explainer/parse-{Js.String(component)}", Severity.Error, reason, subject,
                new JsonObject { ["reason"] = reason, ["example"] = example }, [$"Use the documented syntax, for example: {example}"], null));
        }

        void Unsupported(string value, int line, string component)
        {
            if (ReUnsupported.IsMatch(value)) Fail(line, component, "This Markdown block is outside the restricted author grammar.", "## What happens?\nA short paragraph.");
        }

        // --- JSON drafts (parse.mjs:46-81) ---
        JsonNode? Json()
        {
            var parsed = ExplainerJson.Parse(text, out string? error);
            if (error is not null)
            {
                var position = RePosition.Match(error);
                var line = ReLine.Match(error);
                Fail(position.Success ? Locate(int.Parse(position.Groups[1].Value)).GetLine() : line.Success ? Js.ToNumber(line.Groups[1].Value) : 1,
                    "json", "Input is not valid JSON.", "{\"schema_version\":1,\"diagram_type\":\"explainer\",...}");
                return null;
            }
            // Scan the parsed JSON again for exact nested value locations and duplicate keys, which JSON.parse overwrites.
            var pos = 0;
            void Skip() { while (pos < text.Length && Js.IsSpace(text[pos])) pos++; }
            string StringEnd()
            {
                var start = pos++;
                while (pos < text.Length) { var c = text[pos++]; if (c == '\\') pos++; else if (c == '"') break; }
                return ExplainerJson.Parse(text[start..pos], out string? _)!.GetValue<string>();
            }
            char At(int i) => i < text.Length ? text[i] : '\0';
            void Scan(string pointer)
            {
                Skip();
                var start = pos;
                if (At(pos) == '{')
                {
                    pos++; Skip();
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    while (At(pos) != '}')
                    {
                        Skip();
                        var keyStart = pos;
                        var key = StringEnd();
                        if (keys.Contains(key)) Fail(Locate(keyStart).GetLine(), "json", "Duplicate JSON keys would discard authored content.", "Use each object key once.", $"{pointer}/{PointerKey(key)}");
                        keys.Add(key);
                        Skip(); pos++;
                        Scan($"{pointer}/{PointerKey(key)}");
                        Skip();
                        if (At(pos) == ',') pos++; else break;
                    }
                    pos++;
                }
                else if (At(pos) == '[')
                {
                    pos++; Skip();
                    var index = 0;
                    while (At(pos) != ']') { Scan($"{pointer}/{index++}"); Skip(); if (At(pos) == ',') pos++; else break; }
                    pos++;
                }
                else if (At(pos) == '"') StringEnd();
                else while (pos < text.Length && !(Js.IsSpace(text[pos]) || text[pos] is ',' or ']' or '}')) pos++;
                sourceMap[pointer] = Locate(start, pos);
            }
            Scan("");
            if (parsed is JsonObject root && root["meta"] is JsonObject meta && !string.IsNullOrEmpty(output)) meta["output"] = output;
            return parsed;
        }

        // --- Markdown drafts (parse.mjs:82-202) ---
        JsonObject document = null!;
        JsonObject Meta => document["meta"]!.AsObject();
        JsonObject Answer => document["answer"]!.AsObject();
        JsonObject? panel;
        int panelIndex = -1, cursor;
        bool titleSeen, answerSeen, reasonsAllowed;
        JsonArray Blocks => panel!["blocks"]!.AsArray();

        (string Title, string? Id, string? Span) Heading(string line, string prefix)
        {
            var match = ReHeading.Match(Js.Trim(line[prefix.Length..]));
            return (Js.Trim(match.Groups[1].Value), match.Groups[2].Success ? match.Groups[2].Value : null, match.Groups[3].Success ? match.Groups[3].Value : null);
        }

        void ClosePanel(int end) { if (panelIndex >= 0) sourceMap[$"/panels/{panelIndex}"]!["end"] = end; }

        void AddBlock(JsonObject block, int at, int until, List<Row>? rows = null)
        {
            var type = block["type"]!.GetValue<string>();
            if (panel is null) { Fail(at + 1, type, "Blocks must follow a question panel heading.", "## What should I know?"); return; }
            var panelId = panel.Str("id");
            // A block named after its own panel points at the same place; keep both reachable instead of failing.
            // Diagrams keep their ID because node and route links name it.
            if (block.Str("id") is { Length: > 0 } id && id == panelId && type != "diagram") block["id"] = $"{panelId}-{type}";
            if (string.IsNullOrEmpty(block.Str("id"))) block["id"] = Stable(type, $"{panelId}\0{Stringify(block)}");
            if (type == "comparison")
                foreach (var row in block["rows"]!.AsArray().Cast<JsonObject>())
                    if (string.IsNullOrEmpty(row.Str("id"))) row["id"] = Stable("row", $"{block.Str("id")}\0{row.Str("label")}");
            var pointer = $"/panels/{panelIndex}/blocks/{Blocks.Count}";
            Blocks.Add(block);
            Map(pointer, block, Descriptor(at, until));
            var property = type switch { "facts" => "entries", "comparison" => "rows", "steps" => "items", "list" => "items", _ => null };
            if (property is null) return;
            for (var index = 0; index < (rows ?? []).Count; index++)
                if (block[property] is JsonArray items && index < items.Count)
                    Map($"{pointer}/{property}/{index}", items[index], Descriptor(rows![index].Line, rows[index].Line + 1));
        }

        List<string> Cells(string raw, int line)
        {
            var trimmed = Js.Trim(raw);
            var value = trimmed.StartsWith('|') ? trimmed[1..] : trimmed;
            if (value.EndsWith('|'))
            {   // a final `\|` (odd backslashes before it) is an escaped pipe and stays
                var slashes = 0;
                while (slashes < value.Length - 1 && value[value.Length - 2 - slashes] == '\\') slashes++;
                if (slashes % 2 == 0) value = value[..^1];
            }
            var result = new List<string>();
            var cell = new StringBuilder();
            var code = false;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '\\' && i + 1 < value.Length && value[i + 1] is '|' or '\\') { cell.Append(value[++i]); continue; }
                if (c == '`') code = !code;
                if (c == '|' && !code) { result.Add(Js.Trim(cell.ToString())); cell.Clear(); }
                else cell.Append(c);
            }
            result.Add(Js.Trim(cell.ToString()));
            if (code) Fail(line + 1, "rows", "Unclosed inline code in a table row.", "| label | `literal` |");
            return result;
        }

        (JsMap<string, string> Attrs, List<string> Positional) Attributes(string raw, int line)
        {
            var attrs = new JsMap<string, string>();
            var positional = new List<string>();
            var pos = 0;
            while (pos < raw.Length)
            {
                while (pos < raw.Length && Js.IsSpace(raw[pos])) pos++;
                if (pos >= raw.Length) break;
                var end = pos;
                while (end < raw.Length && !Js.IsSpace(raw[end]) && raw[end] != '=') end++;
                if (end == pos) { Fail(line + 1, "directive", "Malformed directive arguments.", "@metric label=\"Latency\" value=12 unit=ms"); break; }
                var key = raw[pos..end];
                pos = end;
                if (pos >= raw.Length || raw[pos] != '=') { positional.Add(key); continue; }
                pos++;
                string value;
                if (pos < raw.Length && raw[pos] is '"' or '\'')
                {
                    var quote = raw[pos++];
                    var closed = false;
                    var sb = new StringBuilder();
                    while (pos < raw.Length)
                    {
                        var c = raw[pos++];
                        if (c == quote) { closed = true; break; }
                        if (c == '\\' && pos < raw.Length && (raw[pos] == quote || raw[pos] == '\\')) sb.Append(raw[pos++]); else sb.Append(c);
                    }
                    value = sb.ToString();
                    if (!closed) Fail(line + 1, "directive", "Quoted directive value is not closed.", "title=\"A short title\"");
                    if (pos < raw.Length && !Js.IsSpace(raw[pos])) Fail(line + 1, "directive", "Arguments must be separated by whitespace.", "label=\"Latency\" value=12");
                }
                else { var start = pos; while (pos < raw.Length && !Js.IsSpace(raw[pos])) pos++; value = raw[start..pos]; }
                if (attrs.Has(key)) Fail(line + 1, "directive", "Duplicate arguments would discard authored content.", "Use each key once.");
                attrs.Set(key, value);
            }
            return (attrs, positional);
        }

        void RowWidth(List<Row> rows, int min, int max, string component)
        {
            foreach (var row in rows)
                if (row.Cells.Count < min || row.Cells.Count > max)
                    Fail(row.Line + 1, component, $"Expected {(min == max ? $"{min}" : $"{min}–{max}")} cells in this directive row.",
                        $"| {string.Join(" | ", Enumerable.Range(1, min).Select(i => $"field{i}"))} |");
        }

        JsonNode? Boolean(string? value, int at, string component)
        {
            if (value is null) return null;
            if (value is not ("true" or "false")) Fail(at + 1, component, "Boolean arguments require true or false.", "multiple=true");
            return value == "true";
        }

        JsonNode? Number(string? value, int at, string component)
        {
            if (value is null) return null;
            if (!ReNumber.IsMatch(value) || !double.IsFinite(Js.ToNumber(value))) Fail(at + 1, component, "Numeric arguments require a finite number.", "value=12.5");
            return Js.ToNumber(value);
        }

        static IEnumerable<(string Key, string Value)> Select(JsMap<string, string> attrs, params string[] keys) =>
            keys.Where(attrs.Has).Select(key => (key, attrs.Get(key)!));
        static void Assign(JsonObject target, IEnumerable<(string Key, string Value)> entries) { foreach (var (key, value) in entries) target[key] = value; }

        static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
        {
            ["source"] = ["title", "url", "path", "lines", "revision"], ["meta"] = ["locale", "quality_profile", "style", "repo_url", "repo_revision", "repo_provider", "repo_link_mode"],
            ["answer"] = ["scope"], ["prose"] = ["id", "text"], ["list"] = ["id", "ordered"], ["callout"] = ["id", "level", "title", "text"], ["facts"] = ["id"],
            ["metric"] = ["id", "label", "value", "limit", "unit", "note", "typical"], ["steps"] = ["id"], ["diagram"] = ["id", "source", "spec", "title", "changes"],
            ["code"] = ["id", "source", "language"], ["diff"] = ["id", "caption"], ["image"] = ["id", "path", "alt", "caption"], ["comparison"] = ["id", "columns", "label"],
            ["chart"] = ["id", "title", "type", "data", "x", "y", "unit"],
        };

        void Directive(int at)
        {
            var match = ReDirective.Match(Js.Trim(lines[at]));
            if (!match.Success) { Fail(at + 1, "directive", "Malformed type directive.", "@facts\n| key | value |"); cursor++; return; }
            var type = match.Groups[1].Value;
            var (a, positional) = Attributes(match.Groups[2].Success ? match.Groups[2].Value : "", at);
            if (Types.Contains(type)) { positional.Insert(0, type); type = "diagram"; }
            cursor = at + 1;
            var rows = new List<Row>();
            string? literal = null;
            int? literalStart = null;
            if (type == "diff" && cursor < lines.Length && Js.Trim(lines[cursor]) == "```diff")
            {
                cursor++;
                var begin = cursor;
                literalStart = begin;
                while (cursor < lines.Length && Js.Trim(lines[cursor]) != "```") cursor++;
                if (cursor == lines.Length) Fail(at + 1, "diff", "The diff fence is not closed.", "@diff\n```diff\n-old\n+new\n```");
                literal = TrimFinalNewline(text[StartOf(begin)..StartOf(cursor)]);
                if (cursor < lines.Length) cursor++;
            }
            else
            {
                // A directive row never starts with an empty cell; "|| id |" is a line-number prefix copied from a file viewer.
                while (cursor < lines.Length && Js.Trim(lines[cursor]).Length > 0 && !ReRowEnd.IsMatch(Js.Trim(lines[cursor])))
                {
                    rows.Add(new(Cells(ReLineNumbers.Replace(lines[cursor], "$1|", 1), cursor), cursor));
                    cursor++;
                }
                // A callout with no rows takes the plain paragraph that follows it as its text.
                if (type == "callout" && rows.Count == 0 && !a.Has("text"))
                {
                    var next = cursor;
                    while (next < lines.Length && Js.Trim(lines[next]).Length == 0) next++;
                    var paragraph = new List<Row>();
                    while (next < lines.Length && Js.Trim(lines[next]).Length > 0 && !ReBlockStart.IsMatch(Js.Trim(lines[next]))) { paragraph.Add(new([Js.Trim(lines[next])], next)); next++; }
                    if (paragraph.Count > 0) { rows.AddRange(paragraph); cursor = next; }
                }
            }
            if (!Allowed.TryGetValue(type, out var allowed)) { Fail(at + 1, "directive", $"Unsupported component @{type}.", "@facts\n| key | value |"); return; }
            foreach (var key in a.Keys) if (!allowed.Contains(key)) Fail(at + 1, type, $"Unsupported argument {key}.", $"@{type} {string.Join(" ", allowed.Select(k => $"{k}=..."))}");
            if (type is not ("source" or "diagram") && positional.Count > 0) Fail(at + 1, type, "This directive accepts key=value arguments only.", $"@{type} id=stable-id");
            if (type is "meta" or "answer" && rows.Count > 0) Fail(rows[0].Line + 1, type, "This directive accepts header arguments only.", "@meta locale=en quality_profile=showcase");
            if (type == "source")
            {
                if (positional.Count != 1) Fail(at + 1, type, "Source declarations require an ID.", "@source docs title=\"Documentation\" url=https://example.com/docs");
                var source = new JsonObject { ["id"] = positional.Count > 0 ? positional[0] : null };
                Assign(source, Select(a, "title", "url", "path", "revision"));
                if (a.Get("lines") is { } range)
                {
                    var found = ReLines.Match(range);
                    if (!found.Success) Fail(at + 1, type, "Repository lines require start:end.", "lines=10:20");
                    source["lines"] = found.Success ? new JsonArray(Js.ToNumber(found.Groups[1].Value), Js.ToNumber(found.Groups[2].Value)) : new JsonArray();
                }
                if (rows.Count > 0) Fail(at + 1, type, "Source declarations do not accept rows.", "@source demo title=\"Timing model\"");
                var sources = document["sources"]!.AsArray();
                var pointer = $"/sources/{sources.Count}";
                sources.Add(source);
                Map(pointer, source, Descriptor(at, cursor));
                return;
            }
            if (type == "meta")
            {
                Assign(Meta, Select(a, "locale", "quality_profile", "style"));
                if (!string.IsNullOrEmpty(a.Get("repo_url")) || !string.IsNullOrEmpty(a.Get("repo_revision")))
                {
                    var repository = new JsonObject { ["url"] = a.Get("repo_url"), ["revision"] = a.Get("repo_revision") };
                    if (!string.IsNullOrEmpty(a.Get("repo_provider"))) repository["provider"] = a.Get("repo_provider");
                    if (!string.IsNullOrEmpty(a.Get("repo_link_mode"))) repository["link_mode"] = a.Get("repo_link_mode");
                    Meta["repository"] = repository;
                }
                foreach (var key in new[] { "locale", "quality_profile", "style", "repository" })
                    if (Meta[key] is not null) Map($"/meta/{key}", Meta[key], Descriptor(at, cursor));
                return;
            }
            if (type == "answer")
            {
                if (a.Get("scope") is { } scope) { Answer["scope"] = scope; sourceMap["/answer/scope"] = Descriptor(at, cursor); }
                return;
            }
            var block = new JsonObject { ["type"] = type };
            if (!string.IsNullOrEmpty(a.Get("id"))) block["id"] = a.Get("id");
            string? Cell(Row row, int index) => index < row.Cells.Count ? row.Cells[index] : null;
            string Joined() => string.Join("\n", rows.Select(r => r.Cells[0]));
            switch (type)
            {
                case "prose":
                    RowWidth(rows, 1, 1, type);
                    block["text"] = a.Get("text") ?? Joined();
                    break;
                case "list":
                    RowWidth(rows, 1, 1, type);
                    block["items"] = Strings(rows.Select(r => r.Cells[0]));
                    if (a.Has("ordered")) block["ordered"] = Boolean(a.Get("ordered"), at, type);
                    break;
                case "callout":
                    RowWidth(rows, 1, 1, type);
                    block["level"] = !string.IsNullOrEmpty(a.Get("level")) ? a.Get("level") : "note";
                    Assign(block, Select(a, "title"));
                    block["text"] = a.Get("text") ?? Joined();
                    break;
                case "facts":
                    RowWidth(rows, 2, 2, type);
                    block["entries"] = new JsonArray([.. rows.Select(r => (JsonNode?)new JsonObject { ["key"] = r.Cells[0], ["value"] = Cell(r, 1) })]);
                    break;
                case "metric":
                    Assign(block, Select(a, "label", "unit", "note"));
                    foreach (var key in new[] { "value", "limit" }) if (a.Has(key)) block[key] = Number(a.Get(key), at, type);
                    if (a.Has("typical")) block["typical"] = Boolean(a.Get("typical"), at, type);
                    break;
                case "steps":
                    RowWidth(rows, 2, 7, type);
                    block["items"] = new JsonArray([.. rows.Select(r =>
                    {
                        var (id, stepText, detail, diagram, node, route, edges) = (Cell(r, 0), Cell(r, 1), Cell(r, 2), Cell(r, 3), Cell(r, 4), Cell(r, 5), Cell(r, 6));
                        if (node is not null && node.Contains('~') && string.IsNullOrEmpty(route)) { route = node; node = ""; }
                        JsonObject? target = null;
                        if (!string.IsNullOrEmpty(diagram))
                        {
                            target = new JsonObject { ["diagram"] = diagram };
                            if (!string.IsNullOrEmpty(node)) target["node"] = node;
                            if (!string.IsNullOrEmpty(route)) target["route"] = Strings(route.Split('~'));
                            if (!string.IsNullOrEmpty(edges)) target["edges"] = Strings(Csv(edges));
                        }
                        if (string.IsNullOrEmpty(diagram) && (!string.IsNullOrEmpty(node) || !string.IsNullOrEmpty(route) || !string.IsNullOrEmpty(edges)))
                            Fail(r.Line + 1, type, "A step node, route or edge reference needs its diagram ID.", "| step | Read cache | | cache-flow | redis | | |");
                        var item = new JsonObject { ["id"] = id, ["text"] = stepText };
                        if (!string.IsNullOrEmpty(detail)) item["detail"] = detail;
                        if (target is not null) item["target"] = target;
                        return (JsonNode?)item;
                    })]);
                    break;
                case "diagram":
                    Diagram(block, a, positional, rows, at);
                    break;
                case "code":
                    Assign(block, Select(a, "source", "language"));
                    break;
                case "diff":
                    block["text"] = literal ?? string.Join("\n", rows.Select(r => string.Join(" | ", r.Cells)));
                    Assign(block, Select(a, "caption"));
                    break;
                case "image":
                    Assign(block, Select(a, "path", "alt", "caption"));
                    break;
                case "chart":
                    if (string.IsNullOrEmpty(a.Get("data")) || string.IsNullOrEmpty(a.Get("x")) || string.IsNullOrEmpty(a.Get("y")))
                        Fail(at + 1, type, "A data chart names its file, label column and value columns.", "@chart type=bar data=latency.csv x=day y=p50,p99");
                    if (a.Get("type") is { } kind && kind is not ("bar" or "line" or "pie")) Fail(at + 1, type, "A data chart is a bar, line or pie chart.", "@chart type=line data=latency.csv x=day y=p99");
                    Assign(block, Select(a, "title"));
                    var data = new JsonObject
                    {
                        ["path"] = a.Get("data"), ["kind"] = !string.IsNullOrEmpty(a.Get("type")) ? a.Get("type") : "bar", ["x"] = a.Get("x"), ["y"] = Strings(Csv(a.Get("y"))),
                    };
                    if (!string.IsNullOrEmpty(a.Get("unit"))) data["unit"] = a.Get("unit");
                    block["data"] = data;
                    break;
                case "comparison":
                    RowWidth(rows, 2, 500, type);
                    if (!string.IsNullOrEmpty(a.Get("label"))) block["labelColumn"] = a.Get("label");
                    block["columns"] = Strings(Csv(a.Get("columns")));
                    block["rows"] = ComparisonRows(rows);
                    break;
            }
            if (type is "metric" or "code" or "image" or "chart" && rows.Count > 0)
                Fail(at + 1, type, "This component accepts header arguments only.", $"@{type} {string.Join(" ", allowed.Where(k => k != "id").Select(k => $"{k}=..."))}");
            if (type != "diff") foreach (var row in rows) foreach (var cell in row.Cells) Unsupported(cell, row.Line + 1, type);
            AddBlock(block, at, cursor, rows);
            if (type == "diff" && panel is not null)
            {
                var pointer = $"/panels/{panelIndex}/blocks/{Blocks.Count - 1}/text";
                sourceMap[pointer] = literalStart is { } first ? Locate(StartOf(first), StartOf(first) + literal!.Length)
                    : Descriptor(rows.Count > 0 ? rows[0].Line : at, cursor);
            }
        }

        void Diagram(JsonObject block, JsMap<string, string> a, List<string> positional, List<Row> rows, int at)
        {
            const string type = "diagram";
            if (positional.Count != 1) Fail(at + 1, type, "A diagram requires exactly one native type.", "@diagram sequence source=cache.sequence.json");
            var diagramType = positional.Count > 0 ? positional[0] : null;
            block["diagramType"] = diagramType;
            if (a.Get("source") is { } source) block["source"] = source;
            if (a.Get("spec") is { } spec)
            {
                var parsed = ExplainerJson.Parse(spec, out string? error);
                if (error is null) block["spec"] = parsed;
                else Fail(at + 1, type, "Inline diagram spec must be valid JSON.", "@diagram sequence spec='{\"schema_version\":1,...}'");
            }
            if (rows.Count > 0 && (a.Has("spec") || a.Has("source"))) Fail(at + 1, type, "Choose shorthand rows, spec or source, not several.", "@diagram sequence source=cache.sequence.json");
            // A "source:<id>" cell ties that row's node to a declared source; the reader shows it on hover.
            foreach (var row in rows)
            {
                var index = row.Cells.FindIndex(ReSourceCell.IsMatch);
                if (index < 0) continue;
                var node = Js.Trim(ReNodePrefix.Replace(row.Cells[0], "", 1));
                if (index > 0 && !row.Cells[0].Contains('>'))
                {
                    if (block["nodeSources"] is not JsonObject nodeSources) block["nodeSources"] = nodeSources = [];
                    nodeSources[node] = row.Cells[index][7..];
                }
                row.Cells = [.. row.Cells.Where((_, i) => i != index)];
            }
            if (rows.Count > 0 && !a.Has("spec") && !a.Has("source"))
            {
                try
                {
                    var id = !string.IsNullOrEmpty(a.Get("id")) ? a.Get("id")
                        : Stable("diagram", $"{panel?.Str("id") ?? "undefined"}\0{Js.Stringify(new JsonArray([.. rows.Select(r => (JsonNode?)Strings(r.Cells))]))}");
                    var title = new[] { a.Get("title"), panel?.Str("title"), Meta.Str("title") }.FirstOrDefault(t => !string.IsNullOrEmpty(t));
                    block["spec"] = ExplainerShorthand.Compile(diagramType, [.. rows.Select(r => new ShorthandRow(r.Cells, r.Line + 1))],
                        // Decision 2: the page locale reaches the shorthand (the fixed oracle's parse.mjs:208 edit).
                        new(title, id, at + 1, Meta.Str("quality_profile") ?? "showcase", Meta.Str("locale")));
                }
                catch (ArchifyDiagnosticException error)
                {
                    if (error.Diagnostics.Count > 0) diagnostics.AddRange(error.Diagnostics);
                    else Fail(at + 1, type, "The diagram shorthand could not compile.", "@diagram sequence source=cache.sequence.json");
                }
            }
            if (a.Get("changes") is { } changes)
                block["changes"] = new JsonArray([.. Csv(changes).Select(value =>
                {
                    var parts = value.Split(':');
                    return (JsonNode?)new JsonObject { ["target"] = parts[0], ["change"] = parts.Length > 1 ? parts[1] : null };
                })]);
        }

        static string TrimFinalNewline(string value) =>
            value.EndsWith("\r\n", StringComparison.Ordinal) ? value[..^2] : value.EndsWith('\n') ? value[..^1] : value;

        // Citations may follow the assessment; they stay with the cell text. A cell holding only a badge ({bad:Drop}) shows
        // the badge; its label doubles as the cell text.
        static JsonObject Assessment(string text)
        {
            var match = ReAssessment.Match(text);
            if (!match.Success) return new JsonObject { ["text"] = text };
            var verdict = match.Groups[3].Success ? match.Groups[3].Value : null;
            var cell = $"{Js.Trim(match.Groups[1].Value)}{Js.Trim(match.Groups[4].Value)}";
            var result = new JsonObject { ["text"] = cell.Length > 0 ? cell : verdict is null ? "" : Js.Trim(verdict), ["assessment"] = match.Groups[2].Value };
            if (verdict is not null) result["verdict"] = Js.Trim(verdict);
            return result;
        }

        static JsonObject RowLabel(string value)
        {
            var match = ReRowLabel.Match(value);
            return match.Success ? new JsonObject { ["label"] = Js.Trim(match.Groups[1].Value), ["id"] = match.Groups[2].Value } : new JsonObject { ["label"] = value };
        }

        static JsonArray ComparisonRows(IEnumerable<Row> rows) => [.. rows.Select(r =>
        {
            var row = RowLabel(r.Cells[0]);
            row["cells"] = new JsonArray([.. r.Cells.Skip(1).Select(c => (JsonNode?)Assessment(c))]);
            return (JsonNode?)row;
        })];

        JsonObject Markdown()
        {
            document = new JsonObject
            {
                ["schema_version"] = 1, ["diagram_type"] = "explainer", ["meta"] = new JsonObject(), ["answer"] = new JsonObject { ["summary"] = "" },
                ["panels"] = new JsonArray(), ["sources"] = new JsonArray(),
            };
            while (cursor < lines.Length)
            {
                var at = cursor;
                var trimmed = Js.Trim(lines[at]);
                if (trimmed.Length == 0) { cursor++; continue; }
                if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                {
                    if (titleSeen || panel is not null) Fail(at + 1, "heading", "Use one document title before all panels.", "# What should we choose? {#choice}");
                    var h = Heading(trimmed, "# ");
                    Meta["title"] = h.Title;
                    Meta["id"] = !string.IsNullOrEmpty(h.Id) ? h.Id : Stable("explainer", h.Title);
                    titleSeen = true;
                    Map("/meta", Meta, Descriptor(at));
                    cursor++;
                    continue;
                }
                if (trimmed.StartsWith("## ", StringComparison.Ordinal))
                {
                    ClosePanel(StartOf(at));
                    var h = Heading(trimmed, "## ");
                    panel = new JsonObject { ["id"] = !string.IsNullOrEmpty(h.Id) ? h.Id : Stable("panel", h.Title), ["title"] = h.Title };
                    if (h.Span is not null) panel["span"] = Js.ToNumber(h.Span);
                    panel["blocks"] = new JsonArray();
                    var panels = document["panels"]!.AsArray();
                    panelIndex = panels.Count;
                    panels.Add(panel);
                    Map($"/panels/{panelIndex}", panel, Descriptor(at, lines.Length));
                    cursor++;
                    continue;
                }
                if (trimmed.StartsWith('@')) { reasonsAllowed = false; Directive(at); continue; }
                var fence = ReFence.Match(trimmed);
                if (fence.Success)
                {
                    reasonsAllowed = false;
                    var (fa, fp) = Attributes(fence.Groups[3].Value, at);
                    if (fp.Count > 0 || fa.Keys.Any(key => key != "title")) Fail(at + 1, "snippet", "A code fence accepts a language and an optional title only.", "```bash title=\"Run the tests\"");
                    cursor = at + 1;
                    var begin = cursor;
                    while (cursor < lines.Length && Js.Trim(lines[cursor]) != fence.Groups[1].Value) cursor++;
                    if (cursor == lines.Length) Fail(at + 1, "snippet", "The code fence is not closed.", "```bash\nnpm test\n```");
                    var from = StartOf(begin);
                    var literal = TrimFinalNewline(text[from..StartOf(cursor)]);
                    if (cursor < lines.Length) cursor++;
                    if (Js.Trim(literal).Length == 0) { Fail(at + 1, "snippet", "A code fence needs content.", "```bash\nnpm test\n```"); continue; }
                    var language = fence.Groups[2].Value;
                    var title = fa.Get("title");
                    var block = language == "diff" ? new JsonObject { ["type"] = "diff", ["text"] = literal }
                        : language == "mermaid" ? new JsonObject { ["type"] = "chart" } : new JsonObject { ["type"] = "snippet" };
                    if (language == "diff") { if (!string.IsNullOrEmpty(title)) block["caption"] = title; }
                    else
                    {
                        if (language != "mermaid" && language.Length > 0) block["language"] = language;
                        if (!string.IsNullOrEmpty(title)) block["title"] = title;
                        block["text"] = literal;
                    }
                    AddBlock(block, at, cursor);
                    if (panel is not null) sourceMap[$"/panels/{panelIndex}/blocks/{Blocks.Count - 1}/text"] = Locate(from, from + literal.Length);
                    continue;
                }
                if (trimmed.StartsWith('|'))
                {
                    var rows = new List<Row>();
                    while (cursor < lines.Length && Js.Trim(lines[cursor]).StartsWith('|')) { rows.Add(new(Cells(lines[cursor], cursor), cursor)); cursor++; }
                    if (rows.Count < 3 || !rows[1].Cells.All(ReSeparator.IsMatch))
                        Fail(at + 1, "comparison", "A Markdown table needs a header, separator and data rows.", "| Criterion | A | B |\n| --- | --- | --- |\n| Cost | Low {good} | High {bad} |");
                    var columns = rows.Count > 0 ? rows[0].Cells.Skip(1).ToList() : [];
                    var data = rows.Skip(2).ToList();
                    foreach (var r in data) if (r.Cells.Count != columns.Count + 1) Fail(r.Line + 1, "comparison", "All table rows must match the header width.", "| Cost | Low {good} | High {bad} |");
                    var table = new JsonObject { ["type"] = "comparison" };
                    if (rows.Count > 0 && rows[0].Cells[0].Length > 0) table["labelColumn"] = rows[0].Cells[0];
                    table["columns"] = Strings(columns);
                    table["rows"] = ComparisonRows(data);
                    AddBlock(table, at, cursor, data);
                    continue;
                }
                var list = ReList.Match(trimmed);
                if (list.Success)
                {
                    var rows = new List<Row>();
                    var ordered = list.Groups[2].Success;
                    while (cursor < lines.Length)
                    {
                        var found = ReList.Match(Js.Trim(lines[cursor]));
                        if (!found.Success || found.Groups[2].Success != ordered) break;
                        if (ReIndent.IsMatch(lines[cursor])) Fail(cursor + 1, "list", "Nested lists are unsupported.", "- One flat item");
                        Unsupported(found.Groups[3].Value, cursor + 1, "list");
                        rows.Add(new([found.Groups[3].Value], cursor));
                        cursor++;
                    }
                    if (panel is null)
                    {
                        if (!reasonsAllowed || ordered || Answer["reasons"] is not null) Fail(at + 1, "answer", "Use one flat unordered reason list after the conclusion.", "A conclusion.\n\n- First reason\n- Second reason");
                        var reasons = Strings(rows.Select(r => r.Cells[0]));
                        Answer["reasons"] = reasons;
                        Map("/answer/reasons", reasons, Descriptor(at, cursor));
                        for (var i = 0; i < rows.Count; i++) Map($"/answer/reasons/{i}", rows[i].Cells[0], Descriptor(rows[i].Line));
                    }
                    else
                    {
                        var tasks = rows.Count(r => ReTask.IsMatch(r.Cells[0]));
                        if (tasks > 0 && (ordered || tasks < rows.Count)) Fail(at + 1, "list", "Write every checklist item as \"- [ ] item\" in one unordered list.", "- [ ] Check the deposit terms\n- [ ] Photograph the meter readings");
                        var block = new JsonObject { ["type"] = "list", ["ordered"] = ordered };
                        if (tasks > 0) block["checklist"] = true;
                        block["items"] = Strings(rows.Select(r => tasks > 0 ? ReTask.Replace(r.Cells[0], "", 1) : r.Cells[0]));
                        AddBlock(block, at, cursor, rows);
                    }
                    continue;
                }
                var chunks = new List<string>();
                while (cursor < lines.Length && Js.Trim(lines[cursor]).Length > 0 && !ReBlockStart.IsMatch(Js.Trim(lines[cursor])))
                {
                    Unsupported(lines[cursor], cursor + 1, "prose");
                    chunks.Add(Js.Trim(lines[cursor]));
                    cursor++;
                }
                if (cursor == at) { Unsupported(trimmed, at + 1, "prose"); cursor++; continue; }
                var paragraph = string.Join("\n", chunks);
                if (panel is null)
                {
                    if (!titleSeen) Fail(at + 1, "answer", "The document title must precede the short answer.", "# A title\n\nA direct answer.");
                    if (answerSeen) Fail(at + 1, "answer", "The short answer is one paragraph before the panels.", "A direct answer in one paragraph.\n\n## Why?");
                    Answer["summary"] = paragraph;
                    answerSeen = true;
                    reasonsAllowed = true;
                    Map("/answer", Answer, Descriptor(at, cursor));
                }
                else AddBlock(new JsonObject { ["type"] = "prose", ["text"] = paragraph }, at, cursor);
            }
            ClosePanel(text.Length);
            if (!titleSeen) Fail(1, "title", "A document requires one # title.", "# What should we choose?");
            if (!answerSeen) Fail(1, "answer", "A short answer is required before question panels.", "# A title\n\nA direct answer.\n\n## Why?");
            if (document["panels"]!.AsArray().Count == 0) Fail(1, "panel", "At least one question panel is required.", "## Why does this happen?");
            if (!string.IsNullOrEmpty(output)) Meta["output"] = output;
            return Defined(document)!.AsObject();
        }

        // --- one ID namespace for the document, panels, blocks, diagrams and steps (parse.mjs:211-223) ---
        void Identities(JsonNode? document)
        {
            var identities = new Dictionary<string, (JsonNode Component, double Line)>(StringComparer.Ordinal);
            void Identity(JsonNode? value, string pointer, JsonNode component)
            {
                if (value is not JsonValue v || !v.TryGetValue<string>(out var id)) return;
                var line = new[] { sourceMap[pointer]?["line"], pointer.EndsWith("/id", StringComparison.Ordinal) ? sourceMap[pointer[..^3]]?["line"] : sourceMap[pointer]?["line"] }
                    .FirstOrDefault(l => l.Truthy()) is { } found ? found.D() : 1;
                if (!ReId.IsMatch(id) || id.Length > 100)
                    Fail(line, component, "IDs must begin with an ASCII letter and contain letters, numbers, hyphens or underscores.", "id=cache-flow", pointer);
                if (identities.TryGetValue(id, out var previous))
                    Fail(line, component, $"Duplicate ID \"{id}\": the {Js.String(previous.Component)} on line {Js.Num(previous.Line)} already uses it. Panels, blocks, diagrams and steps share one ID namespace.",
                        Js.String(component) == "panel" && component.GetValueKind() == System.Text.Json.JsonValueKind.String ? $"## Another question? {{#{id}-2}}" : $"omit id=, or use id={id}-{Js.String(component)}", pointer);
                identities[id] = (component, line);
            }
            if (document is not JsonObject root) return;
            Identity((root["meta"] as JsonObject)?["id"], "/meta/id", "document");
            if (root["panels"] is not JsonArray panels) return;
            for (var pi = 0; pi < panels.Count; pi++)
            {
                var p = panels[pi] as JsonObject;
                Identity(p?["id"], $"/panels/{pi}/id", "panel");
                if (p?["blocks"] is not JsonArray blocks) continue;
                for (var bi = 0; bi < blocks.Count; bi++)
                {
                    var b = blocks[bi] as JsonObject;
                    Identity(b?["id"], $"/panels/{pi}/blocks/{bi}/id", b?["type"].Truthy() == true ? b["type"]!.DeepClone() : "block");
                }
            }
        }
    }

    static double GetLine(this JsonObject location) => location["line"]!.D();
}
