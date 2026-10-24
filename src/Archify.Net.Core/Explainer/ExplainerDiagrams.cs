using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Context = Archify.Net.ExplainerShorthand.Context;

namespace Archify.Net;

// validateExplainerDiagramTarget's options: where a failing reference is reported.
internal sealed record TargetOptions(JsonObject? SourceMap = null, string? SourcePath = null, string Pointer = "/", int Line = 1);

// archify 3.2.0 explainer/diagrams.mjs:354-517: native diagram blocks frozen, validated, rendered and checked, and references
// resolved against the native SVG graph.
internal static partial class ExplainerDiagrams
{
    static readonly HashSet<string> Types = new(StringComparer.Ordinal)
        { "architecture", "workflow", "sequence", "dataflow", "lifecycle", "erd", "class", "tree", "timeline", "waterfall" };
    const int MaxSpecBytes = 4 * 1024 * 1024, MaxDiagramBytes = 32 * 1024 * 1024;

    [GeneratedRegex(@"\.[Jj][Ss][Oo][Nn]\z")] private static partial Regex JsonPath();
    [GeneratedRegex("<[a-zA-Z][^>]*>")] private static partial Regex Tag();
    [GeneratedRegex("([A-Za-z0-9_:-]+)=\"([^\"]*)\"")] private static partial Regex Attribute();
    [GeneratedRegex("&(?:quot|apos|lt|gt|amp|#[0-9]+|#x[0-9a-f]+);", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Entity();
    // /<svg\b[^>]*\bdata-quality-profile="[^"]*"[^>]*>[\s\S]*?<\/svg>/ with JS's ASCII \b.
    [GeneratedRegex(@"<svg(?![A-Za-z0-9_])[^>]*(?<![A-Za-z0-9_])data-quality-profile=""[^""]*""[^>]*>[\s\S]*?</svg>")] private static partial Regex PrimarySvg();
    [GeneratedRegex(@"(/?>)\z")] private static partial Regex TagEnd();

    static Exception Fail(Context context, string reason, string message, string example) => ExplainerShorthand.Fail(context, reason, message, example);
    static string Utf8(byte[] bytes) => new UTF8Encoding(false).GetString(bytes);   // toString('utf8')
    static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // readSpec (:354-389)
    static byte[] ReadSpec(string? baseDir, JsonNode? value, Context context)
    {
        var relative = Text(value);
        if (string.IsNullOrEmpty(baseDir) || string.IsNullOrEmpty(relative) || relative[0] is '/' or '\\'
            || relative.Any(c => c is '\\' or ':' or '?' or '#' || c <= '\u001f')
            || relative.Split('/').Any(part => part is "" or "." or ".." || Js.ToLowerCase(part) == ".git") || !JsonPath().IsMatch(relative))
            throw Fail(context, "diagram-source-path", "Diagram source must be a relative JSON file inside the input directory.", "diagrams/request.sequence.json");
        try
        {
            var ancestors = ExplainerSources.Walk(baseDir, relative,
                () => Fail(context, "diagram-source-symlink", "Diagram sources may not traverse symbolic links.", "request.sequence.json"));
            if (ancestors[^1].Info is not FileInfo || ancestors[^1].Stamp.Size > MaxSpecBytes)
                throw Fail(context, "diagram-source-size", "Diagram source must be a regular JSON file smaller than 4 MiB.", "request.sequence.json");
            var (_, bytes) = ExplainerSources.ReadUnchanged(ancestors, MaxSpecBytes);
            return bytes ?? throw Fail(context, "diagram-source-changed", "Diagram source changed while being frozen.", "Finish edits before rendering.");
        }
        catch (Exception error) when (error is not ArchifyDiagnosticException)
        {
            throw Fail(context, "diagram-source-read", "Diagram source cannot be read safely inside the input directory.", "request.sequence.json");
        }
    }

    // decode (:391-398): five named entities and numeric references; anything else stays as written.
    static string Decode(string value) => Entity().Replace(value, match =>
    {
        var entity = match.Value;
        switch (entity)
        {
            case "&quot;": return "\"";
            case "&apos;": return "'";
            case "&lt;": return "<";
            case "&gt;": return ">";
            case "&amp;": return "&";
        }
        // parseInt: `&#X41;` and an upper-case name reach the decimal branch and read as NaN.
        var digits = entity.StartsWith("&#x", StringComparison.Ordinal) ? entity[3..^1] : entity[2..^1];
        var hex = entity.StartsWith("&#x", StringComparison.Ordinal);
        if (!digits.All(hex ? char.IsAsciiHexDigit : char.IsAsciiDigit)) return entity;
        var trimmed = digits.TrimStart('0');
        var code = trimmed.Length > 8 ? double.PositiveInfinity
            : trimmed.Length == 0 ? 0 : long.Parse(trimmed, hex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture);
        if (!(code <= 0x10ffff)) return entity;
        return code <= 0xffff ? ((char)(int)code).ToString() : char.ConvertFromUtf32((int)code);   // a surrogate stays a lone unit, as in JS
    });

    // attrs (:400-402): later duplicates win.
    static Dictionary<string, string> Attrs(string tag)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Attribute().Matches(tag)) values[match.Groups[1].Value] = Decode(match.Groups[2].Value);
        return values;
    }
    static string? Truthy(Dictionary<string, string> values, string key) => values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    // semanticOf (:404-416)
    static JsonObject SemanticOf(string svg)
    {
        var nodes = new JsMap<string, JsonObject>();
        var edges = new JsMap<string, JsonObject>();
        foreach (Match match in Tag().Matches(svg))
        {
            var values = Attrs(match.Value);
            if (Truthy(values, "data-node-id") is { } nodeId)
            {
                var node = new JsonObject { ["id"] = nodeId, ["label"] = Truthy(values, "data-node-label") ?? nodeId };
                if (values.TryGetValue("id", out var svgId)) node["svgId"] = svgId;
                nodes.Set(nodeId, node);
            }
            if (Truthy(values, "data-edge-from") is { } from && Truthy(values, "data-edge-to") is { } to)
            {
                var id = Truthy(values, "data-edge-id") ?? Truthy(values, "data-edge-key") ?? $"{from}~{to}";
                var edge = new JsonObject { ["id"] = id, ["from"] = from, ["to"] = to, ["label"] = Truthy(values, "data-edge-label") ?? "" };
                if (Truthy(values, "data-edge-key") is { } key) edge["key"] = key;
                edges.Set(id, edge);
            }
        }
        return new JsonObject { ["nodes"] = new JsonArray([.. nodes.Values]), ["edges"] = new JsonArray([.. edges.Values]) };
    }

    // changesPreview (:418-432): explicit structural marks decorate the preview SVG only.
    static string ChangesPreview(string svg, JsonNode? changes, bool authored, JsonObject semantic, Context context)
    {
        if (authored && changes is not JsonArray)
            throw Fail(context, "diagram-change", "Structural changes must be an array of explicit target marks.", "[{target:\"api\",change:\"modified\"}]");
        var nodes = semantic["nodes"]!.AsArray().Select(node => node!.Str("id")!).ToHashSet(StringComparer.Ordinal);
        var edges = semantic["edges"]!.AsArray().Select(edge => edge!.Str("id")!).ToHashSet(StringComparer.Ordinal);
        var byTarget = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var change in changes.AsArrayOrEmpty())
        {
            var kind = change.Str("change");
            var target = change.Str("target");
            if (!change.Truthy() || kind is not ("added" or "removed" or "modified") || target is null)
                throw Fail(context, "diagram-change", "A structural change needs an explicit target and change type.", "{target:\"api\",change:\"modified\"}");
            if (!nodes.Contains(target) && !edges.Contains(target))
                throw Fail(context, "diagram-change-target", "The change target is absent from the authored native topology.", "{target:\"api\",change:\"modified\"}");
            if (!byTarget.TryAdd(target, kind))
                throw Fail(context, "diagram-change-duplicate", "A target may have only one structural change mark.", "{target:\"api\",change:\"modified\"}");
        }
        return Tag().Replace(svg, match =>
        {
            var values = Attrs(match.Value);
            var change = (values.TryGetValue("data-node-id", out var node) ? byTarget.GetValueOrDefault(node) : null)
                ?? (values.TryGetValue("data-edge-id", out var edge) ? byTarget.GetValueOrDefault(edge) : null);
            return change is null ? match.Value : TagEnd().Replace(match.Value, $" data-explainer-change=\"{change}\"$1", 1);
        });
    }

    static JsonObject Freeze(byte[] bytes) => ExplainerSources.Digest(bytes);
    static QualityProfile? Quality(string quality) => quality switch { "standard" => QualityProfile.Standard, "showcase" => QualityProfile.Showcase, _ => null };

    /// <summary>prepareExplainerDiagrams (diagrams.mjs:447-500): each diagram block frozen, schema-checked, rendered by the native
    /// renderer and checked by the artifact checker, without a per-child browser gate. Returns {diagrams, dependencies}.</summary>
    /// <remarks>Stated deviation: upstream renders and checks each child in a Node child process inside an owned temp directory
    /// (120 s timeout). Here they run in process (no timeout); the checker receipt's <c>file</c> names
    /// <c>%TEMP%\archify-explainer-diagram-&lt;8 hex&gt;\diagram.html</c>, which is never written.</remarks>
    public static JsonObject Prepare(JsonObject document, ResourceOptions o)
    {
        var diagrams = new JsonObject();
        var dependencies = new JsonArray();
        var blocks = new List<(JsonObject Block, string Pointer, string? PanelId)>();
        foreach (var (panel, panelIndex) in document["panels"].AsArrayOrEmpty().Select((p, i) => (p, i)))
            foreach (var (block, blockIndex) in panel.Arr("blocks").AsArrayOrEmpty().Select((b, i) => (b, i)))
                if (block.Str("type") == "diagram") blocks.Add((block!.AsObject(), $"/panels/{panelIndex}/blocks/{blockIndex}", panel.Str("id")));
        // Legacy flat draft shapes are accepted only as adapter inputs; canonical explainer validation remains the parent
        // pipeline's responsibility.
        foreach (var (block, blockIndex) in document["blocks"].AsArrayOrEmpty().Select((b, i) => (b, i)))
            if (block.Str("type") == "diagram") blocks.Add((block!.AsObject(), $"/blocks/{blockIndex}", null));

        foreach (var (block, pointer, panelId) in blocks)
        {
            var id = block.Str("id");
            var diagramType = block.Str("diagramType");
            var context = new Context(null, pointer, id, diagramType, SourceMap: o.SourceMap, SourcePath: o.SourcePath, PanelId: panelId);
            if (string.IsNullOrEmpty(id) || diagrams.ContainsKey(id))
                throw Fail(context, "diagram-id", "Diagram blocks need unique stable identities.", "{id:\"request\",type:\"diagram\",diagramType:\"sequence\",spec:{...}}");
            if (diagramType is null || !Types.Contains(diagramType)) throw Fail(context, "diagram-type", "Unknown native diagram type.", "sequence");
            var inline = block.ContainsKey("spec");
            if (inline == block.ContainsKey("source"))
                throw Fail(context, "diagram-input", "Provide exactly one inline spec or relative source.", "source: request.sequence.json");
            byte[] originalBytes;
            if (!inline) originalBytes = ReadSpec(o.BaseDir, block["source"], context with { Pointer = $"{pointer}/source" });
            else
            {
                originalBytes = Encoding.UTF8.GetBytes(Js.Stringify(block["spec"]));
                if (originalBytes.Length > MaxSpecBytes) throw Fail(context, "diagram-source-size", "Inline diagram JSON exceeds 4 MiB.", "Use a smaller native JSON specification.");
            }
            var original = ExplainerJson.Parse(Utf8(originalBytes), out string? syntax);
            if (syntax is not null)
                throw Fail(context, "diagram-json", "Diagram source is not valid JSON.", "{\"schema_version\":1,\"diagram_type\":\"sequence\",\"meta\":{\"title\":\"Request\"},\"participants\":[],\"messages\":[]}");
            if (original is not JsonObject spec || spec.Str("diagram_type") != diagramType)
                throw Fail(context, "diagram-type-mismatch", "Native specification diagram_type must match its explainer block.", $"diagram_type: {diagramType}");
            var dependency = new JsonObject { ["kind"] = "diagram-spec", ["id"] = id };
            if (block["source"].Truthy())
            {
                dependency["path"] = Path.GetFullPath(Path.Combine(o.BaseDir!, block.Str("source")!));
                dependency["relativePath"] = block["source"]!.DeepClone();
            }
            else dependency["inline"] = true;
            foreach (var (key, value) in Freeze(originalBytes)) dependency[key] = value!.DeepClone();
            dependencies.Add(dependency);

            var output = Path.Join(Path.GetTempPath(), $"archify-explainer-diagram-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}", "diagram.html");
            // Inline diagrams belong to the explainer's reading context. Keep the original author bytes frozen; apply inheritance
            // only to native render input. Referenced standalone specs retain their own locale defaults.
            var meta = new JsonObject();
            foreach (var (key, value) in spec.Obj("meta") ?? []) meta[key] = value?.DeepClone();
            if (inline && spec.Obj("meta")?.ContainsKey("locale") != true &&document["meta"] is JsonObject parent && parent.ContainsKey("locale"))
                meta["locale"] = parent["locale"]?.DeepClone();
            meta["output"] = "diagram.html";
            meta["quality_profile"] = o.Quality;
            var input = spec.DeepClone().AsObject();
            input["meta"] = meta;
            var type = DiagramTypes.Parse(diagramType);
            try { SchemaValidator.Validate(type, input, new DiagnosticSink()); }
            catch (ArchifyDiagnosticException error) { throw ExplainerShorthand.RethrowNative(ExplainerShorthand.NativeError(error), context, "diagram-schema"); }
            var render = ArchifyNet.Render(type, input, new RenderOptions(Quality(o.Quality), string.IsNullOrEmpty(o.RepoRoot) ? null : Path.GetFullPath(o.RepoRoot)));
            if (render.Error is not null)   // the JSON-mode renderer failure, {schemaVersion, ok, source, error, diagnostics}
                throw ExplainerShorthand.RethrowNative(new JsonObject { ["diagnostics"] = new JsonArray([.. render.Diagnostics.Select(d => (JsonNode?)d.ToJson())]) },
                    context, "diagram-render");
            var bytes = Encoding.UTF8.GetBytes(render.Html);
            var check = ArchifyNet.Check(bytes).Report;
            check.Insert(1, "file", output);   // check-render-output.mjs puts `file` second
            if (check["ok"]?.GetValue<bool>() != true) throw ExplainerShorthand.RethrowNative(check, context, "diagram-check");
            if (bytes.Length > MaxDiagramBytes) throw Fail(context, "diagram-artifact-size", "The native diagram artifact exceeds 32 MiB.", "Use a smaller native diagram.");
            var svgMatch = PrimarySvg().Match(render.Html);
            if (!svgMatch.Success) throw Fail(context, "diagram-svg", "The native renderer produced no primary semantic SVG.", "Use a supported native diagram renderer.");
            var semantic = SemanticOf(svgMatch.Value);
            var svg = ChangesPreview(svgMatch.Value, block["changes"], block.ContainsKey("changes"), semantic, context);
            // Preview styles are derived from html at render time; storing them again would double the page.
            var entry = new JsonObject
            {
                ["diagramType"] = diagramType, ["spec"] = spec.DeepClone(), ["html"] = render.Html, ["svg"] = svg,
                ["sha256"] = ExplainerSources.Sha256(bytes), ["bytes"] = bytes.Length, ["specification"] = Freeze(originalBytes),
                ["checks"] = new JsonObject { ["validate"] = new JsonObject { ["ok"] = true }, ["render"] = new JsonObject { ["ok"] = true }, ["check"] = check },
                ["semantic"] = semantic,
            };
            if (block["changes"].Truthy()) entry["changes"] = block["changes"]!.DeepClone();
            diagrams[id] = entry;
        }
        return new JsonObject { ["diagrams"] = diagrams, ["dependencies"] = dependencies };
    }

    /// <summary>validateExplainerDiagramTarget (diagrams.mjs:502-517): a reference resolved against the native SVG graph, real
    /// routes included. Throws on a failing reference.</summary>
    public static bool ValidateTarget(JsonObject? diagrams, JsonObject? target, TargetOptions o)
    {
        var context = new Context(o.Line, o.Pointer, SourceMap: o.SourceMap, SourcePath: o.SourcePath);
        var name = target is not null && target.TryGetPropertyValue("diagram", out var d) ? (d is null ? "null" : Js.String(d)) : "undefined";
        var diagram = diagrams?[name] as JsonObject;
        if (diagram is null || target is null) throw Fail(context, "diagram-reference", "The reference names an unknown diagram block.", "{diagram:\"request\",node:\"api\"}");
        var nodeList = diagram["semantic"]!["nodes"]!.AsArray();
        var nodes = nodeList.Select(node => node.Str("id")!).ToHashSet(StringComparer.Ordinal);
        var edges = diagram["semantic"]!["edges"]!.AsArray().OfType<JsonObject>().ToList();
        bool IsNode(JsonNode? id) => Text(id) is { } s && nodes.Contains(s);
        if (target.ContainsKey("node") && !IsNode(target["node"]))
            throw Fail(context, "diagram-node-reference", "The reference names a node absent from the native diagram.",
                $"{{diagram:\"{name}\",node:\"{(nodeList.Count > 0 && nodeList[0].Str("id") is { Length: > 0 } first ? first : "api")}\"}}");
        if (target.ContainsKey("edges") && !(target["edges"] is JsonArray { Count: > 0 } wanted
                && wanted.All(id => Text(id) is { } s && edges.Any(edge => edge.Str("id") == s))))
            throw Fail(context, "diagram-edge-reference", "The reference names an edge absent from the native diagram.",
                $"{{diagram:\"{name}\",edges:[\"{(edges.Count > 0 && edges[0].Str("id") is { Length: > 0 } e ? e : "api-to-db-1")}\"]}}");
        if (target.ContainsKey("route"))
        {
            if (!(target["route"] is JsonArray { Count: 2 } route && route.All(IsNode)))
                throw Fail(context, "diagram-route-reference", "A route needs two real native node identities.", $"{{diagram:\"{name}\",route:[\"api\",\"db\"]}}");
            var (from, to) = (Text(route[0])!, Text(route[1])!);
            var visited = new HashSet<string>(StringComparer.Ordinal) { from };
            var queue = new List<string> { from };
            for (var index = 0; index < queue.Count; index++)
                foreach (var edge in edges.Where(edge => edge.Str("from") == queue[index]))
                    if (visited.Add(edge.Str("to")!)) queue.Add(edge.Str("to")!);
            if (!visited.Contains(to))
                throw Fail(context, "diagram-route-reference", "No directed route connects these nodes in the native diagram.",
                    $"{{diagram:\"{name}\",route:[\"{(edges.Count > 0 && edges[0].Str("from") is { Length: > 0 } f ? f : "api")}\",\"{(edges.Count > 0 && edges[0].Str("to") is { Length: > 0 } t ? t : "db")}\"]}}");
        }
        return true;
    }
}
