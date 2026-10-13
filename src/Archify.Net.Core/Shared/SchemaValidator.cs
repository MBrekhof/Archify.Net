using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net;

// validator.mjs over an in-house JSON Schema 2020-12 evaluator that reproduces upstream's generated ajv validators
// (Ajv2020 { allErrors: true, strict: true }, every pattern compiled with the `u` flag) plus the synthetic `portablePath`
// keyword generate-validators.mjs:62-87 wraps around them. Keywords run in ajv's rule-group order: an up-front type error,
// then the untyped group ($ref const enum not anyOf oneOf allOf if), then the number, string, array and object groups,
// each skipped when the data is not of its type. A single declared type whose group has keywords reports its type error
// at that group's position, as ajv does.
public static class SchemaValidator
{
    sealed record Error(string Keyword, string InstancePath, JsonObject Params, string Message);

    static readonly Dictionary<string, JsonObject> SchemasById = new(StringComparer.Ordinal);   // lookups only
    static readonly Dictionary<DiagramType, string> TypeSchemaIds = [];
    static readonly Dictionary<string, Regex> Patterns = new(StringComparer.Ordinal);
    static readonly string[] NumberKeywords = ["maximum", "minimum", "exclusiveMaximum", "exclusiveMinimum", "multipleOf"];
    static readonly string[] StringKeywords = ["maxLength", "minLength", "pattern"];
    static readonly string[] ArrayKeywords = ["maxItems", "minItems", "prefixItems", "items", "contains"];
    static readonly string[] ObjectKeywords = ["maxProperties", "minProperties", "required", "propertyNames", "additionalProperties", "properties"];

    static SchemaValidator()
    {
        Load("common");
        foreach (var type in Enum.GetValues<DiagramType>()) TypeSchemaIds[type] = Load(type.Name());

        static string Load(string name)
        {
            var schema = JsonNode.Parse(Resources.Text($"schemas/{name}.schema.json"))!.AsObject();
            var id = schema["$id"]!.GetValue<string>();
            SchemasById[id] = schema;
            CollectPatterns(schema);
            return id;
        }
        static void CollectPatterns(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj)
                        if (key == "pattern" && value is JsonValue v && v.TryGetValue<string>(out var source)) Patterns.TryAdd(source, JsRegex.Translate(source));
                        else CollectPatterns(value);
                    break;
                case JsonArray arr: foreach (var item in arr) CollectPatterns(item); break;
            }
        }
    }

    // validator.mjs validateSchema (:38-83)
    public static void Validate(DiagramType type, JsonObject data, DiagnosticSink sink)
    {
        var errors = new List<Error>();
        var id = TypeSchemaIds[type];
        if (Evaluate(SchemasById[id], data, "", id, errors))
        {
            if (data["meta"] is JsonObject meta && meta["output"] is JsonValue output && output.TryGetValue<string>(out var outputPath))
            {
                try { PortablePath.Validate(outputPath, "output"); }
                catch (PortablePathException error)
                {
                    errors.Add(new("portablePath", "/meta/output", new JsonObject { ["reason"] = error.Reason },
                        "must satisfy the portable output path contract"));
                }
            }
            if (errors.Count == 0) return;
        }

        var diagramType = type.Name();
        var diagnostics = errors.Select(error =>
        {
            var (path, identity) = AnnotatedPath(error.InstancePath, data);
            var subject = new JsonObject { ["diagramType"] = diagramType, ["path"] = path };
            if (identity is not null) subject["identity"] = identity;
            // ajv only attaches error.schema with { verbose: true }, so upstream's `expected` is undefined and drops out.
            var evidence = new JsonObject { ["keyword"] = error.Keyword };
            foreach (var (key, value) in error.Params) evidence[key] = value?.DeepClone();
            return new Diagnostic($"schema/{error.Keyword}", Severity.Error, $"{AnnotatePath(error.InstancePath, data)} {error.Message}",
                subject, evidence, SupportedFixes(error, path), null);
        }).ToList();
        throw sink.ThrowDiagnosticError($"{diagramType} schema validation failed:\n{FormatErrors(errors, data)}", diagnostics);
    }

    static IReadOnlyList<string> SupportedFixes(Error error, string path)
    {
        string Param(string key) => Js.Stringify(error.Params[key]);
        string Plain(string key) => error.Params[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : Js.Stringify(error.Params[key]);
        return error.Keyword switch
        {
            "additionalProperties" => [$"remove unsupported property {Param("additionalProperty")}"],
            "required" => [$"add required property {Param("missingProperty")}"],
            "type" => [$"use {Param("type")} at {path}"],
            "enum" => ["choose one of the allowed values"],
            "pattern" => [$"match the required pattern {Param("pattern")}"],
            "minimum" => [$"use a value {Plain("comparison")} {Plain("limit")}"],
            "maximum" => [$"use a value {Plain("comparison")} {Plain("limit")}"],
            "minItems" => [$"provide at least {Plain("limit")} item(s)"],
            "maxItems" => [$"provide at most {Plain("limit")} item(s)"],
            "minLength" => [$"provide at least {Plain("limit")} character(s)"],
            "maxLength" => [$"provide at most {Plain("limit")} character(s)"],
            _ => [],
        };
    }

    // "/nodes/3/label" reads much better as "/nodes/3 (id: "router") /label" for the
    // LLM fixing the JSON; resolve the nearest enclosing element's id or label. (validator.mjs:4-19, verbatim: no ~1 unescaping)
    static (string path, string? identity) AnnotatedPath(string instancePath, JsonNode data)
    {
        if (instancePath.Length == 0) return ("/", null);
        JsonNode? node = data;
        string? hint = null;
        foreach (var seg in instancePath.Split('/').Skip(1))
        {
            if (node is not (JsonObject or JsonArray)) break;
            var numeric = seg.Length > 0 && seg.All(char.IsAsciiDigit);
            node = node switch
            {
                JsonArray arr => numeric && Js.ToNumber(seg) < arr.Count ? arr[(int)Js.ToNumber(seg)] : null,   // "length" & co. are never objects
                JsonObject obj => obj[numeric ? Js.Num(Js.ToNumber(seg)) : seg],
                _ => null,
            };
            if (node is JsonObject element && (element["id"] ?? element["label"]) is { } tag) hint = JsString(tag);
        }
        return (instancePath, hint);
    }

    static string AnnotatePath(string instancePath, JsonNode data)
    {
        var (path, identity) = AnnotatedPath(instancePath, data);
        return identity is not null ? $"{path} (id/label: {Js.Quote(identity)})" : path;
    }

    static string FormatErrors(List<Error> errors, JsonNode data) => string.Join("\n", errors.Select(e =>
        $"  {AnnotatePath(e.InstancePath, data)} {e.Message}{(e.Params.Count > 0 ? " " + Js.Stringify(e.Params) : "")}"));

    // String(value) for a JSON value.
    static string JsString(JsonNode node) => node switch
    {
        JsonObject => "[object Object]",
        JsonArray arr => string.Join(",", arr.Select(item => item is null ? "" : JsString(item))),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => Js.Num(v.D()),
        _ => node.GetValueKind() == JsonValueKind.True ? "true" : "false",
    };

    // ---- evaluator ----

    static bool Evaluate(JsonNode schemaNode, JsonNode? data, string path, string docId, List<Error>? errors)
    {
        if (schemaNode is JsonValue boolean)
        {
            if (boolean.GetValue<bool>()) return true;
            errors?.Add(new("false schema", path, [], "boolean schema is false"));
            return false;
        }
        var schema = schemaNode.AsObject();
        var valid = true;
        void Fail(string keyword, JsonObject parameters, string message) { valid = false; errors?.Add(new(keyword, path, parameters, message)); }
        bool Sub(JsonNode sub, JsonNode? subData, string subPath, List<Error>? into) => Evaluate(sub, subData, subPath, docId, into);

        // type: up front, or deferred to its group's else-branch (ajv coerceAndCheckDataType / schemaKeywords)
        var typeNode = schema["type"];
        List<string> types = typeNode switch { null => [], JsonArray a => [.. a.Select(t => t!.GetValue<string>())], _ => [typeNode.GetValue<string>()] };
        var typeOk = types.Count == 0 || types.Any(t => TypeMatches(t, data));
        string? groupType = types.Count == 1 && GroupKeywords(types[0]) is { } own && own.Any(schema.ContainsKey) ? types[0] : null;
        void TypeError() => Fail("type", new JsonObject { ["type"] = typeNode!.DeepClone() }, $"must be {(typeNode is JsonArray ? string.Join(",", types) : types[0])}");
        if (!typeOk && groupType is null) TypeError();

        // untyped group
        if (schema["$ref"] is JsonNode reference)
        {
            var (target, targetDoc) = Resolve(reference.GetValue<string>(), docId);
            valid &= Evaluate(target, data, path, targetDoc, errors);
        }
        if (schema.TryGetPropertyValue("const", out var constant) && !JsonEqual(data, constant))
            Fail("const", new JsonObject { ["allowedValue"] = constant?.DeepClone() }, "must be equal to constant");
        if (schema["enum"] is JsonArray allowed && !allowed.Any(v => JsonEqual(data, v)))
            Fail("enum", new JsonObject { ["allowedValues"] = allowed.DeepClone() }, "must be equal to one of the allowed values");
        if (schema["not"] is JsonNode not && Sub(not, data, path, null))
            Fail("not", [], "must NOT be valid");
        if (schema["anyOf"] is JsonArray anyOf)
        {
            var scratch = errors is null ? null : new List<Error>();
            if (!anyOf.Any(branch => Sub(branch!, data, path, scratch)))   // Any stops at the first passing branch, as ajv does
            {
                errors?.AddRange(scratch!);
                Fail("anyOf", [], "must match a schema in anyOf");
            }
        }
        if (schema["oneOf"] is JsonArray oneOf)
        {
            var scratch = errors is null ? null : new List<Error>();
            int? first = null; JsonNode? passingSchemas = null;
            for (var i = 0; i < oneOf.Count; i++)
            {
                if (!Sub(oneOf[i]!, data, path, scratch)) continue;
                if (first is int f) { passingSchemas = new JsonArray(f, i); break; }   // a second pass stops ajv's branch chain
                first = i;
            }
            if (first is null || passingSchemas is not null)
            {
                errors?.AddRange(scratch!);
                Fail("oneOf", new JsonObject { ["passingSchemas"] = passingSchemas }, "must match exactly one schema in oneOf");
            }
        }
        if (schema["allOf"] is JsonArray allOf)
            foreach (var branch in allOf) valid &= Sub(branch!, data, path, errors);
        if (schema["if"] is JsonNode condition && (schema.ContainsKey("then") || schema.ContainsKey("else")))
        {
            var clause = Sub(condition, data, path, null) ? "then" : "else";
            if (schema[clause] is JsonNode branch && !Sub(branch, data, path, errors))
                Fail("if", new JsonObject { ["failingKeyword"] = clause }, $"must match \"{clause}\" schema");
        }

        // number group
        if (NumberKeywords.Any(schema.ContainsKey))
        {
            if (TypeMatches("number", data))
            {
                var d = data!.D();
                void Limit(string keyword, string comparison, Func<double, double, bool> fails)
                {
                    if (schema[keyword] is not JsonNode limit || !fails(d, limit.D())) return;
                    Fail(keyword, new JsonObject { ["comparison"] = comparison, ["limit"] = limit.D() }, $"must be {comparison} {Js.Num(limit.D())}");
                }
                Limit("maximum", "<=", (x, l) => x > l);
                Limit("minimum", ">=", (x, l) => x < l);
                Limit("exclusiveMaximum", "<", (x, l) => x >= l);
                Limit("exclusiveMinimum", ">", (x, l) => x <= l);
                if (schema["multipleOf"] is JsonNode multipleOfNode)
                {
                    var multipleOf = multipleOfNode.D();
                    var res = d / multipleOf;
                    // ajv: `m === 0 || res !== parseInt(res)`, and parseInt reads String(res), so 1e21 fails as V8 does.
                    if (multipleOf == 0 || res != ParseIntOfNumber(res))
                        Fail("multipleOf", new JsonObject { ["multipleOf"] = multipleOf }, $"must be multiple of {Js.Num(multipleOf)}");
                }
            }
            else if (groupType == "number") TypeError();
        }

        // string group
        if (StringKeywords.Any(schema.ContainsKey))
        {
            if (data is JsonValue sv && sv.TryGetValue<string>(out var s))
            {
                var length = Ucs2Length(s);
                if (schema["maxLength"] is JsonNode max && length > max.D())
                    Fail("maxLength", new JsonObject { ["limit"] = max.D() }, $"must NOT have more than {Js.Num(max.D())} characters");
                if (schema["minLength"] is JsonNode min && length < min.D())
                    Fail("minLength", new JsonObject { ["limit"] = min.D() }, $"must NOT have fewer than {Js.Num(min.D())} characters");
                if (schema["pattern"] is JsonNode patternNode && !Patterns[patternNode.GetValue<string>()].IsMatch(s))
                    Fail("pattern", new JsonObject { ["pattern"] = patternNode.GetValue<string>() }, $"must match pattern \"{patternNode.GetValue<string>()}\"");
            }
            else if (groupType == "string") TypeError();
        }

        // array group
        if (ArrayKeywords.Any(schema.ContainsKey))
        {
            if (data is JsonArray arr)
            {
                if (schema["maxItems"] is JsonNode max && arr.Count > max.D())
                    Fail("maxItems", new JsonObject { ["limit"] = max.D() }, $"must NOT have more than {Js.Num(max.D())} items");
                if (schema["minItems"] is JsonNode min && arr.Count < min.D())
                    Fail("minItems", new JsonObject { ["limit"] = min.D() }, $"must NOT have fewer than {Js.Num(min.D())} items");
                var prefixLength = 0;
                if (schema["prefixItems"] is JsonArray prefixItems)
                {
                    prefixLength = prefixItems.Count;
                    for (var i = 0; i < Math.Min(arr.Count, prefixLength); i++) valid &= Sub(prefixItems[i]!, arr[i], $"{path}/{Js.Num(i)}", errors);
                }
                if (schema["items"] is JsonNode items)
                {
                    if (items is JsonValue itemsFlag && !itemsFlag.GetValue<bool>())
                    {
                        if (arr.Count > prefixLength)
                            Fail("items", new JsonObject { ["limit"] = prefixLength }, $"must NOT have more than {Js.Num(prefixLength)} items");
                    }
                    else for (var i = prefixLength; i < arr.Count; i++) valid &= Sub(items, arr[i], $"{path}/{Js.Num(i)}", errors);
                }
                if (schema["contains"] is JsonNode contains)
                {   // minContains 1: stop at the first match; on failure ajv keeps every item's errors and adds its own
                    var scratch = errors is null ? null : new List<Error>();
                    var found = false;
                    for (var i = 0; i < arr.Count && !found; i++) found = Sub(contains, arr[i], $"{path}/{Js.Num(i)}", scratch);
                    if (!found)
                    {
                        errors?.AddRange(scratch!);
                        Fail("contains", new JsonObject { ["minContains"] = 1 }, "must contain at least 1 valid item(s)");
                    }
                }
            }
            else if (groupType == "array") TypeError();
        }

        // object group
        if (ObjectKeywords.Any(schema.ContainsKey))
        {
            if (data is JsonObject obj)
            {
                // ponytail: JsonObject order = insertion order; for-in would list integer-like keys first. Affects error order only.
                if (schema["maxProperties"] is JsonNode max && obj.Count > max.D())
                    Fail("maxProperties", new JsonObject { ["limit"] = max.D() }, $"must NOT have more than {Js.Num(max.D())} properties");
                if (schema["minProperties"] is JsonNode min && obj.Count < min.D())
                    Fail("minProperties", new JsonObject { ["limit"] = min.D() }, $"must NOT have fewer than {Js.Num(min.D())} properties");
                if (schema["required"] is JsonArray required)
                    foreach (var name in required.Select(r => r!.GetValue<string>()))
                        if (!obj.ContainsKey(name)) Fail("required", new JsonObject { ["missingProperty"] = name }, $"must have required property '{name}'");
                if (schema["propertyNames"] is JsonNode propertyNames)
                    foreach (var (key, _) in obj)
                        if (!Sub(propertyNames, JsonValue.Create(key), path, errors))   // the key's own errors sit at the object's path
                            Fail("propertyNames", new JsonObject { ["propertyName"] = key }, "property name must be valid");
                var properties = schema["properties"] as JsonObject;
                if (schema["additionalProperties"] is JsonNode additional && !(additional is JsonValue on && on.GetValue<bool>()))
                    foreach (var (key, value) in obj)
                    {
                        if (properties?.ContainsKey(key) == true) continue;
                        if (additional is JsonValue) Fail("additionalProperties", new JsonObject { ["additionalProperty"] = key }, "must NOT have additional properties");
                        else valid &= Sub(additional, value, $"{path}/{EscapePointer(key)}", errors);
                    }
                if (properties is not null)
                    foreach (var (key, sub) in properties)
                        if (obj.TryGetPropertyValue(key, out var value)) valid &= Sub(sub!, value, $"{path}/{EscapePointer(key)}", errors);
            }
            else if (groupType == "object") TypeError();
        }

        return valid;
    }

    static string[]? GroupKeywords(string type) => type switch
    {
        "number" => NumberKeywords, "string" => StringKeywords, "array" => ArrayKeywords, "object" => ObjectKeywords, _ => null,
    };

    // ajv checkDataType with strictNumbers (strict: true): numbers must be finite.
    static bool TypeMatches(string type, JsonNode? data) => type switch
    {
        "null" => data is null,
        "object" => data is JsonObject,
        "array" => data is JsonArray,
        "string" => data is JsonValue v && v.GetValueKind() == JsonValueKind.String,
        "boolean" => data is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => data is JsonValue v && v.GetValueKind() == JsonValueKind.Number && double.IsFinite(v.D()),
        "integer" => data is JsonValue v && v.GetValueKind() == JsonValueKind.Number && double.IsFinite(v.D()) && v.D() == Math.Floor(v.D()),
        _ => false,
    };

    // fast-deep-equal over JSON values; numbers compare as doubles, so a C# 1 equals a parsed 1.0.
    static bool JsonEqual(JsonNode? a, JsonNode? b) => (a, b) switch
    {
        (null, null) => true,
        (JsonArray x, JsonArray y) => x.Count == y.Count && x.Zip(y).All(p => JsonEqual(p.First, p.Second)),
        (JsonObject x, JsonObject y) => x.Count == y.Count && x.All(p => y.TryGetPropertyValue(p.Key, out var other) && JsonEqual(p.Value, other)),
        (JsonValue x, JsonValue y) => (x.GetValueKind(), y.GetValueKind()) switch
        {
            (JsonValueKind.String, JsonValueKind.String) => x.GetValue<string>() == y.GetValue<string>(),
            (JsonValueKind.Number, JsonValueKind.Number) => x.D() == y.D(),
            var (p, q) => p == q && p is JsonValueKind.True or JsonValueKind.False,
        },
        _ => false,
    };

    // ajv ucs2length: code points, a high surrogate followed by a low one counting once.
    static int Ucs2Length(string s)
    {
        var length = 0;
        for (var i = 0; i < s.Length; i++, length++)
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
        return length;
    }

    // parseInt(res): String(res), then the longest [+-]?digits prefix (NaN when there is none).
    static double ParseIntOfNumber(double res)
    {
        var s = Js.Num(res);
        var end = s.Length > 0 && s[0] == '-' ? 1 : 0;
        var start = end;
        while (end < s.Length && char.IsAsciiDigit(s[end])) end++;
        return end == start ? double.NaN : Js.ToNumber(s[..end]);
    }

    static string EscapePointer(string key) => key.Replace("~", "~0").Replace("/", "~1");

    // "common.schema.json#/$defs/id" against the referring document's $id; "#/$defs/x" stays in that document.
    static (JsonNode schema, string docId) Resolve(string reference, string docId)
    {
        var hash = reference.IndexOf('#');
        var uri = hash < 0 ? reference : reference[..hash];
        var targetDoc = uri.Length == 0 ? docId : new Uri(new Uri(docId), uri).AbsoluteUri;
        JsonNode node = SchemasById[targetDoc];
        if (hash >= 0)
            foreach (var token in reference[(hash + 1)..].Split('/').Skip(1))
            {
                var name = Uri.UnescapeDataString(token).Replace("~1", "/").Replace("~0", "~");
                node = node is JsonArray arr ? arr[(int)Js.ToNumber(name)]! : node[name]!;
            }
        return (node, targetDoc);
    }
}
