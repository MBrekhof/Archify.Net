using System.Text.Json.Nodes;

namespace Archify.Net;

// grid.mjs. Grid placement for architecture IR (#8). Not auto-layout — fixed cell math only.
// `Raw` is upstream's `{ ...DEFAULT_GRID, ...raw }` merged object (the layout report spreads it verbatim).
public sealed class GridSpec(JsonObject raw)
{
    public JsonObject Raw { get; } = raw;
    public double OriginX => Raw.Arr("origin")![0]!.D();
    public double OriginY => Raw.Arr("origin")![1]!.D();
    public double Cols => Raw.Num("cols")!.Value;
    public double GapX => Raw.Num("gapX")!.Value;
    public double GapY => Raw.Num("gapY")!.Value;
    public double CellW => Raw.Num("cellW")!.Value;
    public double CellH => Raw.Num("cellH")!.Value;
}

public static class Grid
{
    public static JsonObject DefaultGrid() => new()
    {
        ["mode"] = "grid",
        ["origin"] = new JsonArray(40d, 80d),
        ["cols"] = 4d,
        ["gapX"] = 30d,
        ["gapY"] = 40d,
        ["cellW"] = 130d,
        ["cellH"] = 64d,
    };

    static bool IsInteger(double? d) => d is { } v && double.IsFinite(v) && v == Math.Floor(v);

    public static GridSpec? GridLayout(JsonObject arch)
    {
        var raw = arch["layout"];
        if (!raw.Truthy() || raw.Str("mode") != "grid") return null;
        var merged = DefaultGrid();
        if (raw is JsonObject obj) foreach (var (key, value) in obj) merged[key] = value?.DeepClone();
        return new GridSpec(merged);
    }

    public static Pt ResolveComponentPos(JsonObject component, GridSpec? grid)
    {
        if (component.Arr("pos") is { Count: 2 } pos) return new(pos[0]!.D(), pos[1]!.D());
        if (grid is null) return new(double.NaN, double.NaN);
        if (!IsInteger(component.Num("row")) || !IsInteger(component.Num("col"))) return new(double.NaN, double.NaN);
        var stepX = grid.CellW + grid.GapX;
        var stepY = grid.CellH + grid.GapY;
        return new(grid.OriginX + component.Num("col")!.Value * stepX, grid.OriginY + component.Num("row")!.Value * stepY);
    }

    public static void ValidateGridPlacement(JsonObject arch, GridSpec? grid, List<string> problems)
    {
        if (grid is null) return;
        if (arch.ContainsKey("layout") && arch["layout"].Str("mode") != "grid")
        {
            problems.Add("layout.mode must be \"grid\" when layout is set (free placement omits layout entirely).");
            return;
        }
        var seen = new JsMap<string, string>();
        foreach (var node in arch["components"].AsArrayOrEmpty())
        {
            var c = node as JsonObject ?? new JsonObject();
            var id = c.Str("id") ?? "undefined";
            var hasPos = c.Arr("pos") is { Count: 2 };
            var hasCell = IsInteger(c.Num("row")) && IsInteger(c.Num("col"));
            if (hasPos) continue; // pos wins; row/col are optional hints only
            if (!hasPos && !hasCell)
            {
                problems.Add($"Component \"{id}\" needs pos [x,y] or grid row/col when layout.mode is \"grid\".");
                continue;
            }
            var row = c.Num("row")!.Value;
            var col = c.Num("col")!.Value;
            if (row < 0 || col < 0)
            {
                problems.Add($"Component \"{id}\" row/col must be non-negative integers.");
                continue;
            }
            if (col >= grid.Cols)
                problems.Add($"Component \"{id}\" col {Js.Num(col)} exceeds layout.cols {Js.Num(grid.Cols)} (valid: 0..{Js.Num(grid.Cols - 1)}).");
            var key = $"{Js.Num(row)},{Js.Num(col)}";
            if (seen.Has(key)) problems.Add($"Components \"{seen.Get(key)}\" and \"{id}\" share grid cell row {Js.Num(row)} col {Js.Num(col)}.");
            else seen.Set(key, id);
        }
    }
}
