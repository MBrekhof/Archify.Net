using System.Text.Json;
using System.Text.Json.Nodes;

namespace Archify.Net;

// Typed reads of a JsonNode property: null when the key is missing or holds another JSON type (JS `typeof` checks).
public static class J
{
    static JsonNode? Get(JsonNode? n, string key) => n is JsonObject o ? o[key] : null;

    public static string? Str(this JsonNode? n, string key) => Get(n, key) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public static double? Num(this JsonNode? n, string key) => Get(n, key) is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.D() : null;
    public static JsonArray? Arr(this JsonNode? n, string key) => Get(n, key) as JsonArray;
    public static JsonObject? Obj(this JsonNode? n, string key) => Get(n, key) as JsonObject;
    public static bool? Bool(this JsonNode? n, string key) => Get(n, key) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    // geometry.mjs asArray: Array.isArray(value) ? value : []
    public static IEnumerable<JsonNode?> AsArrayOrEmpty(this JsonNode? n) => n is JsonArray a ? a : [];

    // A JS number is a double whatever CLR type backs the node: JsonValue.Create(3).GetValue<double>() throws.
    public static double D(this JsonNode n) => Js.ToDouble((JsonValue)n);

    // JS truthiness of a property value (`relation.via`, `conn.labelAt`): missing/null, false, 0/NaN and "" are falsy;
    // every array and object — even an empty one — is truthy.
    public static bool Truthy(this JsonNode? n) => n switch
    {
        null => false,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        JsonValue v => Js.ToDouble(v) is var d && d != 0 && !double.IsNaN(d),
        _ => true,
    };
}
