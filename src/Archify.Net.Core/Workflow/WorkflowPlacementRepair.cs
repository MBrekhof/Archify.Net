using System.Text.Json.Nodes;
using static Archify.Net.WorkflowModel;

namespace Archify.Net;

internal sealed record PlacementChange(string Op, string Path, double Value);
internal sealed record PlacementComponent(List<string> Ids, bool Cyclic);
// `{ eligible: false, reason }` or `{ eligible: true, proposed, changes, components, rails: { left, right } }`.
internal sealed record PlacementProposal(bool Eligible, string? Reason, WorkflowDoc? Proposed = null, List<PlacementChange>? Changes = null,
    List<PlacementComponent>? Components = null, bool LeftRail = false, bool RightRail = false);

// workflow-placement-repair.mjs: one optional placement proposal for a failed readable workflow. Never applied to the authored
// input; the compiler verifies the complete result.
internal static class WorkflowPlacementRepair
{
    static readonly string[] PinKeys = ["via", "channelX", "channelY", "labelAt"];

    public static PlacementProposal ProposeWorkflowPlacement(WorkflowDoc input)
    {
        if (input.SchemaVersion != 2 || input.Meta["viewBox"].Truthy()
            || input.Groups is { Count: > 0 } || input.Phases is { Count: > 0 }
            || input.MainPath is not { Count: > 0 } || input.Nodes.Count > 64
            || input.Edges.Any(e => PinKeys.Any(e.ContainsKey)))
        {
            return new(false, "Needs unpinned v2, complete mainPath, no authored regions, <=64 nodes.");
        }
        List<string> main = [.. input.MainPath.Select(IdKey)];
        var mainIndex = new JsMap<string, int>();
        for (var i = 0; i < main.Count; i++) mainIndex.Set(main[i], i);
        static string Id(JsonObject n) => IdKey(n["id"]);
        var nodes = new JsMap<string, JsonObject>();
        foreach (var n in input.Nodes) nodes.Set(Id(n), n);
        var laneIndex = new JsMap<string, int>();
        for (var i = 0; i < input.Lanes.Count; i++) laneIndex.Set(IdKey(input.Lanes[i]["id"]), i);
        if (main.Any(id => !nodes.Has(id)) || main.Distinct().Count() != main.Count)
            return new(false, "Invalid mainPath.");
        var @out = new JsMap<string, List<string>>();
        foreach (var n in input.Nodes) @out.Set(Id(n), [.. input.Edges.Where(e => IdKey(e["from"]) == Id(n)).Select(e => IdKey(e["to"]))]);
        JsSet<string> Reachable(string id)
        {
            var found = new JsSet<string>();
            var todo = new List<string>(@out.Get(id)!);
            while (todo.Count > 0)
            {
                var next = todo[^1];
                todo.RemoveAt(todo.Count - 1);
                if (found.Has(next) || !nodes.Has(next)) continue;
                found.Add(next);
                todo.AddRange(@out.Get(next)!);
            }
            return found;
        }
        var reach = new JsMap<string, JsSet<string>>();
        foreach (var n in input.Nodes) reach.Set(Id(n), Reachable(Id(n)));
        var offMain = input.Nodes.Where(n => !mainIndex.Has(Id(n))).ToList();
        var offIds = new JsSet<string>();
        foreach (var n in offMain) offIds.Add(Id(n));
        var components = new List<PlacementComponent>();
        var assigned = new JsSet<string>();
        foreach (var n in offMain)
        {
            if (assigned.Has(Id(n))) continue;
            var component = new List<string>();
            var todo = new List<string> { Id(n) };
            while (todo.Count > 0)
            {
                var id = todo[^1];
                todo.RemoveAt(todo.Count - 1);
                if (assigned.Has(id)) continue;
                assigned.Add(id);
                component.Add(id);
                foreach (var e in input.Edges)
                {
                    if (IdKey(e["from"]) == id && offIds.Has(IdKey(e["to"]))) todo.Add(IdKey(e["to"]));
                    if (IdKey(e["to"]) == id && offIds.Has(IdKey(e["from"]))) todo.Add(IdKey(e["from"]));
                }
            }
            var cyclic = component.Any(id => main.Any(m => reach.Get(id)!.Has(m) && reach.Get(m)!.Has(id)));
            components.Add(new(component, cyclic));
        }
        var leftRail = components.Any(c => c.Cyclic);
        if (!leftRail) return new(false, "No return cycle connected to the main path.");
        var rightRail = components.Any(c => !c.Cyclic && c.Ids.Count > 1);
        double start = leftRail ? 1 : 0, end = rightRail ? 4 : 5;
        var proposed = WorkflowDoc.From(input.ToJson());   // structuredClone: keeps -0, drops the Symbol
        var moved = new JsMap<string, JsonObject>();
        foreach (var n in proposed.Nodes) moved.Set(Id(n), n);
        int? LaneOf(JsonObject n) => laneIndex.Has(IdKey(n["lane"])) ? laneIndex.Get(IdKey(n["lane"])) : null;
        double Height(JsonObject n) => Or(n.Num("height"), 52);
        var col = start;
        for (var i = 0; i < main.Count; i++)
        {
            var current = moved.Get(main[i])!;
            var previous = i > 0 ? moved.Get(main[i - 1]) : null;   // `moved.get(main[-1])` is undefined
            if (previous is not null && LaneOf(current) <= LaneOf(previous)) col = Math.Min(col + 1, end);
            current["col"] = col;
        }
        foreach (var component in components)
        {
            foreach (var id in component.Ids)
            {
                var n = moved.Get(id)!;
                var parents = input.Edges.Where(e => IdKey(e["to"]) == id && mainIndex.Has(IdKey(e["from"]))).Select(e => moved.Get(IdKey(e["from"]))!).ToList();
                n["col"] = component.Cyclic ? 0 : component.Ids.Count > 1 ? 5
                    : Math.Min(end, MaxOf(parents.Select(p => p.Num("col")!.Value + 1), start));
                // Place same-lane failure branches below their main-path source row.
                if (component.Cyclic && parents.Any(p => StrictEquals(p["lane"], n["lane"])))
                {
                    var sameLane = main.Select(m => moved.Get(m)!).Where(m => StrictEquals(m["lane"], n["lane"]));
                    n["yOffset"] = MaxOf(sameLane.Select(m => Or(m.Num("yOffset"), 0) + Height(m) / 2), double.NegativeInfinity) + Height(n) / 2 + 40;
                }
            }
        }
        var groups = new JsMap<string, List<JsonObject>>();
        foreach (var n in proposed.Nodes)
        {
            var key = Tpl(n, "lane") + ":" + Tpl(n, "col");
            groups.Set(key, [.. groups.Get(key) ?? [], n]);
        }
        double Order(JsonObject n)
        {
            if (mainIndex.Has(Id(n))) return mainIndex.Get(Id(n));
            var incoming = input.Edges.Where(e => IdKey(e["to"]) == Id(n) && mainIndex.Has(IdKey(e["from"]))).Select(e => (double)mainIndex.Get(IdKey(e["from"]))).ToList();
            return incoming.Count > 0 ? incoming.Min() + 0.5 : main.Count;
        }
        foreach (var group in groups.Values)
        {
            if (group.Count < 2) continue;
            Js.Sort(group, (a, b) => Lor(Order(a) - Order(b), Js.LocaleCompare(a.Str("id") ?? "", b.Str("id") ?? "")));
            double total = 0;
            foreach (var n in group) total += Height(n);
            total += 40 * (group.Count - 1);
            var top = -total / 2;
            foreach (var n in group)
            {
                n["yOffset"] = top + Height(n) / 2;
                top += Height(n) + 40;
            }
        }
        var changes = new List<PlacementChange>();
        for (var i = 0; i < input.Nodes.Count; i++)
        {
            foreach (var field in (string[])["col", "yOffset"])
            {
                var before = input.Nodes[i];
                var after = proposed.Nodes[i];
                if (before.ContainsKey(field) == after.ContainsKey(field) && StrictEquals(before[field], after[field])) continue;
                changes.Add(new(before.ContainsKey(field) ? "replace" : "add", $"/nodes/{i}/{field}", after[field]!.D()));
            }
        }
        return new(true, null, proposed, changes, components, leftRail, rightRail);
    }
}
