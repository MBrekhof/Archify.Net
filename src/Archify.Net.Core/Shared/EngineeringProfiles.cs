using System.Text.Json.Nodes;

namespace Archify.Net;

// engineering-profiles.mjs. Runs after schema validation, so ids, wraps entries, from/to are strings where present.
public static class EngineeringProfiles
{
    const string DeploymentProfile = "deployment-ownership";
    static readonly string[] DeploymentBoundaryKinds = ["region", "security-group"];
    static readonly string[] PrivateStateTypes = ["database"];

    static JsonObject Subject(string collection, int index, JsonObject? item = null)
    {
        var subject = new JsonObject { ["diagramType"] = "architecture", ["profile"] = DeploymentProfile, ["collection"] = collection, ["index"] = index };
        if (item is not null && item.Str("id") is { Length: > 0 } id) subject["id"] = id;
        return subject;
    }

    // JSON.stringify(value) inside a template literal: a missing key prints "undefined".
    static string Show(JsonObject obj, string key) => obj.TryGetPropertyValue(key, out var value) ? Js.Stringify(value) : "undefined";

    // { ...source[key] } style copy: a missing key stays missing, an explicit null is kept.
    static void CopyIfPresent(JsonObject target, string targetKey, JsonObject source, string key)
    {
        if (source.TryGetPropertyValue(key, out var value)) target[targetKey] = value?.DeepClone();
    }

    static bool Wraps(JsonObject boundary, string? id) => id is not null && boundary["wraps"].AsArrayOrEmpty().Any(w => w is JsonValue v && v.TryGetValue<string>(out var s) && s == id);

    static List<(JsonObject boundary, int index)> Membership(List<JsonObject> boundaries, string? componentId, string kind) =>
        [.. boundaries.Select((boundary, index) => (boundary, index)).Where(b => b.boundary.Str("kind") == kind && Wraps(b.boundary, componentId))];

    static JsonArray RegionsJson(List<(JsonObject boundary, int index)> regions) => [.. regions.Select(r =>
    {
        var entry = new JsonObject { ["boundaryIndex"] = r.index };
        CopyIfPresent(entry, "label", r.boundary, "label");
        return (JsonNode)entry;
    })];

    public static IReadOnlyList<Diagnostic> DeploymentOwnershipDiagnostics(JsonObject diagram)
    {
        var components = diagram["components"].AsArrayOrEmpty().OfType<JsonObject>().ToList();
        var boundaries = diagram["boundaries"].AsArrayOrEmpty().OfType<JsonObject>().ToList();
        var connections = diagram["connections"].AsArrayOrEmpty().OfType<JsonObject>().ToList();
        var diagnostics = new List<Diagnostic>();

        foreach (var kind in DeploymentBoundaryKinds)
        {
            var count = boundaries.Count(boundary => boundary.Str("kind") == kind);
            if (count > 0) continue;
            diagnostics.Add(new("engineering/deployment-boundary-kind", Severity.Error,
                $"Deployment ownership requires at least one {kind} boundary.",
                Subject("boundaries", -1), new JsonObject { ["requiredKind"] = kind, ["found"] = count },
                [$"add one {kind} boundary with an explicit wraps list"], null));
        }

        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];
            var type = component.Str("type");
            if (type == "external") continue;
            var id = component.Str("id");
            JsonObject Evidence(string key, JsonNode value) { var e = new JsonObject(); CopyIfPresent(e, "componentType", component, "type"); e[key] = value; return e; }

            if (component.Str("tag") is not string tag || Js.Trim(tag).Length == 0)
            {
                var evidence = Evidence("ownerField", "tag");
                diagnostics.Add(new("engineering/deployment-owner-missing", Severity.Error,
                    $"Deployment component {Show(component, "id")} does not name its owner in tag.",
                    Subject("components", index, component), evidence,
                    [$"set /components/{Js.Num(index)}/tag to the responsible team or owner"], null));
            }

            var regions = Membership(boundaries, id, "region");
            if (regions.Count == 0)
                diagnostics.Add(new("engineering/deployment-region-scope", Severity.Error,
                    $"Deployment component {Show(component, "id")} is not assigned to a region boundary.",
                    Subject("components", index, component), Evidence("regionMemberships", 0),
                    ["add the component id to the real region boundary wraps list"], null));
            else if (regions.Count > 1)
                diagnostics.Add(new("engineering/deployment-region-ambiguous", Severity.Error,
                    $"Deployment component {Show(component, "id")} belongs to more than one region boundary.",
                    Subject("components", index, component), Evidence("regions", RegionsJson(regions)),
                    ["keep the component id in exactly one real region boundary wraps list"], null));

            if (type is not null && PrivateStateTypes.Contains(type) && Membership(boundaries, id, "security-group").Count == 0)
                diagnostics.Add(new("engineering/deployment-private-state", Severity.Error,
                    $"Stateful component {Show(component, "id")} is not assigned to a private security-group boundary.",
                    Subject("components", index, component), Evidence("privateMemberships", 0),
                    ["add the component id to the real private security-group boundary wraps list"], null));
        }

        for (var index = 0; index < boundaries.Count; index++)
        {
            var boundary = boundaries[index];
            if (boundary.Str("kind") != "security-group") continue;
            var members = boundary["wraps"].AsArrayOrEmpty().Select(member =>
            {
                var memberId = member is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                return (json: member, regions: Membership(boundaries, memberId, "region"));
            }).ToList();
            var regionIndexes = members.SelectMany(m => m.regions.Select(r => r.index)).Distinct().Count();
            var consistent = members.Count > 0 && members.All(m => m.regions.Count == 1) && regionIndexes == 1;
            if (consistent) continue;
            var evidence = new JsonObject();
            CopyIfPresent(evidence, "boundaryKind", boundary, "kind");
            evidence["members"] = new JsonArray([.. members.Select(m => (JsonNode)new JsonObject { ["id"] = m.json?.DeepClone(), ["regions"] = RegionsJson(m.regions) })]);
            diagnostics.Add(new("engineering/deployment-private-region-consistency", Severity.Error,
                $"Private boundary {Show(boundary, "label")} must contain components from exactly one shared region.",
                Subject("boundaries", index, boundary), evidence,
                ["assign every private-boundary component to exactly one shared region boundary"], null));
        }

        for (var index = 0; index < connections.Count; index++)
        {
            var connection = connections[index];
            var (from, to) = (connection.Str("from"), connection.Str("to"));
            var crossed = boundaries.Select((boundary, boundaryIndex) => (boundary, boundaryIndex))
                .Where(b => DeploymentBoundaryKinds.Contains(b.boundary.Str("kind")) && Wraps(b.boundary, from) != Wraps(b.boundary, to)).ToList();
            if (crossed.Count == 0 || (connection.Str("label") is string label && Js.Trim(label).Length > 0)) continue;
            var evidence = new JsonObject();
            CopyIfPresent(evidence, "from", connection, "from");
            CopyIfPresent(evidence, "to", connection, "to");
            evidence["crossedBoundaries"] = new JsonArray([.. crossed.Select(c =>
            {
                var entry = new JsonObject { ["boundaryIndex"] = c.boundaryIndex };
                CopyIfPresent(entry, "kind", c.boundary, "kind");
                CopyIfPresent(entry, "label", c.boundary, "label");
                return (JsonNode)entry;
            })]);
            var name = connection.Str("id") is { Length: > 0 } connectionId ? connectionId : $"{from ?? "undefined"}->{to ?? "undefined"}";
            diagnostics.Add(new("engineering/deployment-crossing-mechanism", Severity.Error,
                $"Cross-boundary connection {Js.Quote(name)} does not name its mechanism.",
                Subject("connections", index, connection), evidence,
                [$"set /connections/{Js.Num(index)}/label to the real cross-boundary mechanism"], null));
        }

        return diagnostics;
    }

    // validateEngineeringProfile (:147)
    public static void Validate(DiagramType type, JsonObject diagram, DiagnosticSink sink)
    {
        var profile = diagram["meta"].Str("engineering_profile");
        if (string.IsNullOrEmpty(profile)) return;
        if (type != DiagramType.Architecture || profile != DeploymentProfile) return;
        var diagnostics = DeploymentOwnershipDiagnostics(diagram);
        if (diagnostics.Count == 0) return;
        throw sink.ThrowDiagnosticError(
            $"Engineering profile {Js.Quote(profile)} failed:\n{string.Join("\n", diagnostics.Select(entry => $"- {entry.Message}"))}",
            diagnostics);
    }
}
