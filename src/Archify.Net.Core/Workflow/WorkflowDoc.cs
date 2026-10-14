using System.Text.Json.Nodes;

namespace Archify.Net;

// The workflow document a compile works on (R7). Upstream spreads the parsed document (`{ ...workflow, nodes: sorted }`,
// workflow-compiler.mjs:732-738) so one node object sits in two arrays; a JsonObject cannot, so the collections are lists of
// the authored references. `sourceIndexes` (:1419-1423) key on those references.
// ponytail: models the schema's root keys (additionalProperties: false); a document that fails the schema never reaches here.
internal sealed class WorkflowDoc
{
    static readonly string[] SchemaKeys = ["schema_version", "diagram_type", "meta", "lanes", "phases", "groups", "mainPath", "semanticChecks", "nodes", "edges", "cards"];

    public int SchemaVersion { get; set; }
    public string? DiagramType { get; set; }
    public JsonObject Meta { get; set; } = [];
    public List<JsonObject> Lanes { get; set; } = [];
    // null is `undefined`: canonicalReadableWorkflow keeps an absent phases/groups absent (:723-737).
    public List<JsonObject>? Phases { get; set; }
    public List<JsonObject>? Groups { get; set; }
    public JsonArray? MainPath { get; set; }
    public JsonObject? SemanticChecks { get; set; }
    public List<JsonObject> Nodes { get; set; } = [];
    public List<JsonObject> Edges { get; set; } = [];
    public JsonArray? Cards { get; set; }
    // workflow-migration-geometry.mjs's `absoluteGeometryPolicy` Symbol: survives a spread, dies in a JSON or structured clone.
    public bool? AbsoluteGeometryPolicy { get; set; }
    // The authored root key order, so ToJson() serialises as JSON.stringify of the spread document does.
    IReadOnlyList<string> keyOrder = SchemaKeys;

    public static WorkflowDoc From(JsonObject authored)
    {
        static List<JsonObject> Objects(JsonNode? n) => n is JsonArray a ? [.. a.OfType<JsonObject>()] : [];
        return new WorkflowDoc
        {
            SchemaVersion = authored.Num("schema_version") is { } v ? (int)v : 0,
            DiagramType = authored.Str("diagram_type"),
            Meta = authored.Obj("meta") ?? [],
            Lanes = Objects(authored["lanes"]),
            Phases = authored.ContainsKey("phases") ? Objects(authored["phases"]) : null,
            Groups = authored.ContainsKey("groups") ? Objects(authored["groups"]) : null,
            MainPath = authored.Arr("mainPath"),
            SemanticChecks = authored.Obj("semanticChecks"),
            Nodes = Objects(authored["nodes"]),
            Edges = Objects(authored["edges"]),
            Cards = authored.Arr("cards"),
            keyOrder = [.. authored.Select(kv => kv.Key)],
        };
    }

    // `{ ...workflow }`: a new document sharing every collection and element, Symbol included.
    public WorkflowDoc Spread() => (WorkflowDoc)MemberwiseClone();

    // cloneWorkflow (:694-697) = JSON.parse(JSON.stringify(...)): drops the Symbol, turns -0 into 0. A structuredClone
    // (workflow-placement-repair.mjs:49) is From(ToJson()) instead, which keeps -0.
    public WorkflowDoc Clone() => From(JsonNode.Parse(Js.Stringify(ToJson()))!.AsObject());

    // A detached deep copy in the authored key order.
    public JsonObject ToJson()
    {
        static JsonArray Copy(List<JsonObject> items) => new([.. items.Select(item => item.DeepClone())]);
        JsonNode? Value(string key) => key switch
        {
            "schema_version" => SchemaVersion,
            "diagram_type" => DiagramType,
            "meta" => Meta.DeepClone(),
            "lanes" => Copy(Lanes),
            "phases" => Phases is null ? null : Copy(Phases),
            "groups" => Groups is null ? null : Copy(Groups),
            "mainPath" => MainPath?.DeepClone(),
            "semanticChecks" => SemanticChecks?.DeepClone(),
            "nodes" => Copy(Nodes),
            "edges" => Copy(Edges),
            "cards" => Cards?.DeepClone(),
            _ => null,
        };
        var json = new JsonObject();
        foreach (var key in keyOrder.Concat(SchemaKeys.Except(keyOrder)))
            if (Value(key) is { } value) json[key] = value;
        return json;
    }
}
