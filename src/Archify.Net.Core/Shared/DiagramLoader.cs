using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Archify.Net;

public static class DiagramLoader
{
    // ponytail: MaxDepth stays at the reader's default 64; deeper input reports input/json-parse where V8 would parse.
    // Raise MaxDepth if a real diagram ever nests that deep.
    static readonly JsonReaderOptions ReaderOptions = new() { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false };

    // JSON.parse semantics, hand-rolled over Utf8JsonReader: JsonNode.Parse throws on duplicate keys and, like JsonDocument,
    // turns lone surrogate escapes into U+FFFD. Failures carry input/json-parse as cli.mjs:66-78 does.
    public static JsonObject Parse(string json)
    {
        JsonNode? root;
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), ReaderOptions);
            reader.Read();
            root = Value(ref reader);
            reader.Read();   // throws on anything after the root value
        }
        catch (JsonException error) { throw ParseFailure(error.Message); }
        // Upstream parses any JSON value and lets schema validation reject a non-object root; this API returns the diagram object.
        return root as JsonObject ?? throw ParseFailure("the diagram root must be an object");
    }

    internal static ArchifyDiagnosticException ParseFailure(string reason)
    {
        var message = $"Input JSON could not be parsed: {reason}";
        return new ArchifyDiagnosticException(message, [Diagnostic.Normalize(new(
            "input/json-parse", Severity.Error, message,
            new JsonObject(),   // upstream's subject.input is the CLI's file path; a string input has none
            new JsonObject { ["reason"] = reason },
            ["repair the JSON syntax and run validation again"], null))]);
    }

    static JsonNode? Value(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                var obj = new JsonObject();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var key = Text(ref reader);
                    reader.Read();
                    obj[key] = Value(ref reader);   // duplicate key: last value in the first key's position, as in JS
                }
                return obj;
            case JsonTokenType.StartArray:
                var arr = new JsonArray();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) arr.Add(Value(ref reader));
                return arr;
            case JsonTokenType.String: return JsonValue.Create(Text(ref reader));
            // TryGetDouble refuses out-of-range literals; JSON.parse("1e400") is Infinity, which double.Parse also yields.
            case JsonTokenType.Number:
                return JsonValue.Create(reader.TryGetDouble(out var d) ? d : double.Parse(reader.ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture));
            case JsonTokenType.True: return JsonValue.Create(true);
            case JsonTokenType.False: return JsonValue.Create(false);
            default: return null;
        }
    }

    static string Text(ref Utf8JsonReader reader) => reader.ValueIsEscaped ? Unescape(reader.ValueSpan) : reader.GetString()!;

    // The reader has already validated the escapes. \uXXXX is appended as the raw code unit without pairing, so a lone
    // \ud800 survives the way JSON.parse keeps it.
    static string Unescape(ReadOnlySpan<byte> s)
    {
        var sb = new StringBuilder(s.Length);
        while (true)
        {
            var at = s.IndexOf((byte)'\\');
            if (at < 0) return sb.Append(Encoding.UTF8.GetString(s)).ToString();
            sb.Append(Encoding.UTF8.GetString(s[..at]));
            var c = s[at + 1];
            if (c == 'u')
            {
                sb.Append((char)int.Parse(s.Slice(at + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                s = s[(at + 6)..];
                continue;
            }
            sb.Append(c switch { (byte)'b' => '\b', (byte)'f' => '\f', (byte)'n' => '\n', (byte)'r' => '\r', (byte)'t' => '\t', _ => (char)c });
            s = s[(at + 2)..];
        }
    }

    // Parsed input is already double-backed. C#-built nodes (`new JsonArray(0, 60)`, `["labelDx"] = 3`) and JsonElement-backed
    // ones are rewritten in place so every number reads as a double. Returns the same object.
    public static JsonObject Normalize(JsonObject diagram) { Walk(diagram); return diagram; }

    static void Walk(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                    if (IsNumber(value)) obj[key] = JsonValue.Create(value!.D()); else Walk(value);
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                    if (IsNumber(arr[i])) arr[i] = JsonValue.Create(arr[i]!.D()); else Walk(arr[i]);
                break;
        }
    }

    static bool IsNumber(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number;

    // cli.mjs:79-84, the validation gates of loadDiagram in upstream's order. Throws ArchifyDiagnosticException.
    public static void Load(DiagramType type, JsonObject diagram, RenderContext ctx)
    {
        var sink = ctx.Diagnostics;
        if (diagram["meta"] is JsonObject meta && meta.TryGetPropertyValue("output", out var authoredOutput))
            OutputPath.ValidateAuthoredOutputPath(authoredOutput, sink);
        SchemaValidator.Validate(type, diagram, sink);
        ApplyLocaleTranslations(type, diagram, ctx);
        ValidateCrossCollectionContracts(type, diagram, sink);
        EngineeringProfiles.Validate(type, diagram, sink);
    }

    // cli.mjs:32-42. Upstream also prints each warning on stderr outside JSON mode; a library only records them.
    static void ApplyLocaleTranslations(DiagramType type, JsonObject diagram, RenderContext ctx)
    {
        if (diagram["meta"] is not JsonObject meta || string.IsNullOrEmpty(meta.Str("locale"))) return;   // schema: locale is a string
        ctx.Locale.Register(meta.Str("locale"), meta.Obj("translations"));
        foreach (var diagnostic in ctx.Locale.LocaleDiagnostics(type, meta)) ctx.Diagnostics.Record(diagnostic);
    }

    // cli.mjs:331-357 validateRelationshipIds via validateCrossCollectionContracts. Relationship ids are optional, but once
    // supplied they are the durable identity viewer links use, so duplicates fail.
    public static void ValidateCrossCollectionContracts(DiagramType type, JsonObject diagram, DiagnosticSink sink)
    {
        var collection = type.RelationshipCollection();
        var relationships = collection is null ? [] : diagram[collection].AsArrayOrEmpty();
        var seen = new HashSet<string>(StringComparer.Ordinal);   // keyed by JSON text: ids are schema-validated strings
        var problems = new List<string>();
        var index = 0;
        foreach (var relationship in relationships)
        {
            var id = relationship is JsonObject r && r.TryGetPropertyValue("id", out var value) ? value : null;
            if (id is not null && !(id is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0))
            {
                var key = Js.Stringify(id);
                if (!seen.Add(key)) problems.Add($"/{collection}/{Js.Num(index)}/id duplicates relationship id {key}");
            }
            index++;
        }
        if (problems.Count > 0)
            throw sink.ThrowDiagnosticProblems("Relationship identity validation failed", problems, "relationship/duplicate-id",
                new JsonObject { ["diagramType"] = type.Name(), ["collection"] = collection });
    }
}
