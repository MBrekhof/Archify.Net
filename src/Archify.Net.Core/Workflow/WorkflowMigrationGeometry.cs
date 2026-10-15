using System.Text.Json.Nodes;
using static Archify.Net.WorkflowModel;

namespace Archify.Net;

internal sealed record ChangedCoordinate(string Path, double From, double To);

// workflow-migration-geometry.mjs: v1 → v2 migration support. Only preservesWorkflowAbsoluteGeometry affects a render; the rest
// runs inside the column-capacity fix discovery (workflow-compiler.mjs:1092-1147).
internal static class WorkflowMigrationGeometry
{
    const int TargetSchemaVersion = 2;

    // Planning removes absolute route assertions, but must keep the same rank policy as the document whose coordinates will be
    // mapped into that plan. An authored canvas likewise keeps its established plan: draft-only optional corridor growth must not
    // consume space that a valid fixed canvas lacks.
    public static bool PreservesWorkflowAbsoluteGeometry(WorkflowDoc w) => w.AbsoluteGeometryPolicy ?? (w.Meta["viewBox"] is JsonArray
        || w.Edges.Any(edge => edge["via"] is JsonArray || edge["labelAt"] is JsonArray || edge.ContainsKey("channelX") || edge.ContainsKey("channelY")));

    // The authored workflow as a schema-v2 document without its capacity override, so the compiler can discover the intrinsic v2
    // rank plan before deciding whether an explicit viewBox needs to grow.
    public static WorkflowDoc IntrinsicWorkflow(WorkflowDoc w)
    {
        var intrinsic = w.Clone();
        intrinsic.SchemaVersion = TargetSchemaVersion;
        intrinsic.AbsoluteGeometryPolicy = PreservesWorkflowAbsoluteGeometry(w);
        intrinsic.Meta.Remove("viewBox");   // `{ ...intrinsic.meta }` then delete: the clone's meta is already its own
        return intrinsic;
    }

    // A schema-v2 planning projection without the authored route geometry that only becomes valid once its legacy X coordinates
    // are remapped. Rank-affecting automatic and straight relationships remain.
    public static WorkflowDoc PlanningWorkflow(WorkflowDoc w)
    {
        var planned = IntrinsicWorkflow(w);
        var edges = new List<JsonObject>();
        foreach (var edge in planned.Edges)
        {
            var hasRoutedGeometry = edge["via"] is JsonArray
                || (edge["route"].Truthy() && edge.Str("route") is not ("auto" or "straight"))
                || edge.ContainsKey("channelX")
                || edge.ContainsKey("channelY");
            if (hasRoutedGeometry) continue;
            var automatic = new JsonObject();
            foreach (var property in (string[])["id", "from", "to", "variant", "role", "width"])
                if (edge.ContainsKey(property)) automatic[property] = edge[property]?.DeepClone();
            if (edge.Str("route") == "straight") automatic["route"] = "straight";
            if (!edge.ContainsKey("labelAt") && edge.ContainsKey("label")) automatic["label"] = edge["label"]?.DeepClone();
            edges.Add(automatic);
        }
        planned.Edges = edges;

        if (planned.MainPath is { } mainPath)
        {
            var projectedPairs = new JsSet<string>();
            foreach (var edge in planned.Edges) projectedPairs.Add($"{Tpl(edge, "from")}\u0000{Tpl(edge, "to")}");
            var projectionBreaksMainPath = mainPath.Select((from, index) => index < mainPath.Count - 1
                && !projectedPairs.Has($"{Js.PrimitiveString(from)}\u0000{Js.PrimitiveString(mainPath[index + 1])}")).Any(breaks => breaks);
            if (projectionBreaksMainPath) planned.MainPath = null;
        }
        return planned;
    }

    static double MappedNumber(double value) => Js.ToNumber(Js.ToFixed(value, 6));

    // A deterministic piecewise-linear mapping between corresponding legacy and readable rank centres. Coordinates outside the
    // rank span are extrapolated from the nearest segment, so authored outside corridors keep their relative offset.
    // Upstream throws TypeError; the only caller catches everything (workflow-compiler.mjs:1115-1123).
    public static Func<double, double> CreateHorizontalRankMapper(IReadOnlyList<double> oldColumns, IReadOnlyList<double> newColumns)
    {
        if (oldColumns.Count != newColumns.Count || oldColumns.Count < 2 || !oldColumns.All(double.IsFinite) || !newColumns.All(double.IsFinite))
            throw new ArgumentException("Horizontal rank mapping requires matching finite column arrays.");
        for (var index = 1; index < oldColumns.Count; index++)
        {
            if (oldColumns[index] <= oldColumns[index - 1] || newColumns[index] <= newColumns[index - 1])
                throw new ArgumentException("Horizontal rank mapping requires strictly increasing columns.");
        }
        return x =>
        {
            if (!double.IsFinite(x)) throw new ArgumentException("Horizontal rank mapping requires a finite x coordinate.");
            var segment = oldColumns.Count - 2;
            if (x <= oldColumns[0])
            {
                segment = 0;
            }
            else
            {
                for (var index = 0; index < oldColumns.Count - 1; index++)
                {
                    if (x <= oldColumns[index + 1])
                    {
                        segment = index;
                        break;
                    }
                }
            }
            var oldSpan = oldColumns[segment + 1] - oldColumns[segment];
            var newSpan = newColumns[segment + 1] - newColumns[segment];
            var ratio = (x - oldColumns[segment]) / oldSpan;
            return MappedNumber(newColumns[segment] + ratio * newSpan);
        };
    }

    // Applies one horizontal mapping to every schema-v1 absolute X pin of `w`, which the caller owns (it is rewritten), and
    // reports each changed coordinate in document order.
    public static List<ChangedCoordinate> MapExplicitCoordinates(WorkflowDoc w, Func<double, double> mapX)
    {
        var changedCoordinates = new List<ChangedCoordinate>();
        void Record(string path, Func<double> read, Action<double> write)   // record(path, owner, property)
        {
            var from = read();
            var to = mapX(from);
            write(to);
            if (to != from) changedCoordinates.Add(new(path, from, to));
        }
        static bool FiniteNumber(JsonNode? value) => value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number && double.IsFinite(v.D());

        for (var edgeIndex = 0; edgeIndex < w.Edges.Count; edgeIndex++)
        {
            var edge = w.Edges[edgeIndex];
            if (edge["via"] is JsonArray via)
            {
                for (var pointIndex = 0; pointIndex < via.Count; pointIndex++)
                {
                    if (via[pointIndex] is JsonArray point && point.Count > 0 && FiniteNumber(point[0]))
                        Record($"/edges/{edgeIndex}/via/{pointIndex}/0", () => point[0]!.D(), to => point[0] = to);
                }
            }
            if (edge["labelAt"] is JsonArray labelAt && labelAt.Count > 0 && FiniteNumber(labelAt[0]))
                Record($"/edges/{edgeIndex}/labelAt/0", () => labelAt[0]!.D(), to => labelAt[0] = to);
            if (FiniteNumber(edge["channelX"]))
                Record($"/edges/{edgeIndex}/channelX", () => edge["channelX"]!.D(), to => edge["channelX"] = to);
        }
        return changedCoordinates;
    }

    // An independently owned schema-v2 candidate with every authored absolute X pin mapped to the readable rank plan.
    public static (WorkflowDoc Document, List<ChangedCoordinate> ChangedCoordinates) CreateMappedWorkflowCandidate(WorkflowDoc w,
        IReadOnlyList<double> oldColumns, IReadOnlyList<double> newColumns)
    {
        var document = w.Clone();
        document.SchemaVersion = TargetSchemaVersion;
        var mapX = CreateHorizontalRankMapper(oldColumns, newColumns);
        return (document, MapExplicitCoordinates(document, mapX));
    }
}
