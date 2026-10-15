using System.Text.Json;
using System.Text.Json.Nodes;

namespace Archify.Net;

// erd/grid.mjs. Banded grid placement for the ER IR, shared by the class and erd renderers. Not auto-layout: column
// widths and row band heights are measured from the boxes the author declares, so the same spec always produces the
// same coordinates. `ErdGrid`, not `Grid`: Architecture/Grid.cs owns that name.
public sealed record ErGrid(string Mode = "grid", double OriginX = 32, double OriginY = 40, double GapX = 56, double GapY = 44,
    double EntityW = 240, double HeaderH = 30, double RowH = 20);

// An entity as bandedLayout/resolveEntityPos read it. `null` plays `undefined` in the Number.isInteger/isFinite checks.
// `Width` is the measured width the caller spreads over the entity (render-class.mjs:231-233), else the authored one.
public sealed record GridEntity(string? Id, JsonArray? Pos, double? Row, double? Col, double? Width, double Height)
{
    public static GridEntity From(JsonObject source, double height, double? width = null) =>
        new(source.Str("id"), source.Arr("pos"), source.Num("row"), source.Num("col"), width ?? source.Num("width"), height);
}

public sealed record Bands(JsMap<double, double> ColumnX, JsMap<double, double> RowY, JsMap<double, double> Widths, JsMap<double, double> Heights,
    double TotalWidth, double TotalHeight);

public static class ErdGrid
{
    // grid.mjs:5-22 DEFAULT_ER_GRID — the record defaults are its values.
    public static ErGrid DefaultErGrid { get; } = new();

    // `{ ...defaults, ...raw }`: each present numeric key overrides. A JS `origin[i]` that is missing or not a number
    // reads as undefined and poisons the arithmetic, so it becomes NaN here.
    public static ErGrid Merge(ErGrid defaults, JsonObject raw)
    {
        var origin = raw.Arr("origin");
        return defaults with
        {
            Mode = raw.Str("mode") ?? defaults.Mode,
            OriginX = origin is null ? defaults.OriginX : Elem(origin, 0),
            OriginY = origin is null ? defaults.OriginY : Elem(origin, 1),
            GapX = raw.Num("gapX") ?? defaults.GapX,
            GapY = raw.Num("gapY") ?? defaults.GapY,
            EntityW = raw.Num("entityW") ?? defaults.EntityW,
            HeaderH = raw.Num("headerH") ?? defaults.HeaderH,
            RowH = raw.Num("rowH") ?? defaults.RowH,
        };
    }

    public static ErGrid? ErGridLayout(JsonObject er)
    {
        var raw = er["layout"];
        // Grid cells without a layout block mean the default grid, not absolute coordinates that were never supplied.
        if (!raw.Truthy())
        {
            var gridPlaced = er["entities"].AsArrayOrEmpty().Any(entity => IsFinite(entity.Num("row")) && IsFinite(entity.Num("col")));
            return gridPlaced ? DefaultErGrid : null;
        }
        if (raw.Str("mode") != "grid") return null;
        return Merge(DefaultErGrid, (JsonObject)raw!);
    }

    // An entity may omit `width` and inherit the grid's default; layout and placement validator resolve it the same way.
    public static double ResolvedEntityWidth(GridEntity entity, ErGrid? grid) =>
        IsFinite(entity.Width) ? entity.Width!.Value : grid?.EntityW ?? DefaultErGrid.EntityW;

    // One header band plus one band per declared column; an entity with no columns still gets a single row.
    public static double EntityHeight(JsonObject entity, ErGrid grid)
    {
        var rows = Math.Max(1, entity["attributes"] is JsonArray a ? a.Count : 0);
        return grid.HeaderH + rows * grid.RowH + 6;
    }

    // A box on a half column (a class grid's `col: 1.5`) is centred between two columns: it sizes its row but neither column.
    static bool GridColumn(double? col) => col is { } c && double.IsInteger(c * 2) && c >= 0;

    // `columnGaps` raises the gap after a column to a measured floor; gapX stays the minimum everywhere.
    public static Bands BandedLayout(IReadOnlyList<GridEntity> entities, ErGrid grid, JsMap<double, double>? columnGaps = null)
    {
        var widths = new JsMap<double, double>();
        var heights = new JsMap<double, double>();
        var maxCol = -1d;
        foreach (var entity in entities)
        {
            if (!IsInteger(entity.Row) || !GridColumn(entity.Col)) continue;
            var col = entity.Col!.Value; var row = entity.Row!.Value;
            maxCol = Math.Max(maxCol, Math.Ceiling(col));
            if (double.IsInteger(col)) widths.Set(col, Math.Max(Js.Or(widths.Get(col), 0), ResolvedEntityWidth(entity, grid)));
            heights.Set(row, Math.Max(Js.Or(heights.Get(row), 0), entity.Height));
        }

        var columnX = new JsMap<double, double>();
        var rowY = new JsMap<double, double>();
        var x = grid.OriginX;
        for (var col = 0d; col <= maxCol; col += 1)
        {
            columnX.Set(col, x);
            x += Js.Or(widths.Get(col), 0) + Math.Max(grid.GapX, columnGaps is null ? 0 : Js.Or(columnGaps.Get(col), 0));
        }
        // A half-column box may be wider than its empty neighbouring bands. Keep every grid box inside the left margin
        // without changing integer-only grids.
        var leftInset = 0d;
        foreach (var entity in entities)
        {
            if (!IsInteger(entity.Row) || !GridColumn(entity.Col) || IsInteger(entity.Col)) continue;
            double Centre(double col) => Val(columnX, col) + Js.Or(widths.Get(col), 0) / 2;
            var midpoint = (Centre(Math.Floor(entity.Col!.Value)) + Centre(Math.Ceiling(entity.Col.Value))) / 2;
            leftInset = Math.Max(leftInset, grid.OriginX - midpoint + ResolvedEntityWidth(entity, grid) / 2);
        }
        if (leftInset > 0)
        {
            foreach (var (col, column) in columnX.Entries.ToList()) columnX.Set(col, column + leftInset);
            x += leftInset;
        }
        var y = grid.OriginY;
        var maxRow = heights.Keys.Aggregate(-1d, Math.Max);   // Math.max(-1, ...heights.keys()): -1 when empty
        for (var row = 0d; row <= maxRow; row += 1)
        {
            rowY.Set(row, y);
            y += Js.Or(heights.Get(row), 0) + grid.GapY;
        }

        return new Bands(columnX, rowY, widths, heights, x, y);
    }

    static readonly Pt NaNPt = new(double.NaN, double.NaN);

    public static Pt ResolveEntityPos(GridEntity entity, ErGrid? grid, Bands? bands)
    {
        if (entity.Pos is { Count: 2 } pos) return new Pt(Elem(pos, 0), Elem(pos, 1));
        if (grid is null || bands is null) return NaNPt;
        if (!IsInteger(entity.Row) || !GridColumn(entity.Col)) return NaNPt;
        var width = ResolvedEntityWidth(entity, grid);
        var col = entity.Col!.Value;
        var rowY = Val(bands.RowY, entity.Row!.Value);
        if (!double.IsInteger(col))
        {
            double Centre(double c) => Val(bands.ColumnX, c) + Js.Or(bands.Widths.Get(c), 0) / 2;
            var (left, right) = (Math.Floor(col), Math.Ceiling(col));
            if (!double.IsFinite(Val(bands.ColumnX, left)) || !double.IsFinite(Val(bands.ColumnX, right)) || !double.IsFinite(rowY)) return NaNPt;
            return new Pt((Centre(left) + Centre(right)) / 2 - width / 2, rowY);
        }
        var columnX = Val(bands.ColumnX, col);
        if (!double.IsFinite(columnX) || !double.IsFinite(rowY)) return NaNPt;
        // Centre each box in its column so the gaps on both sides stay equal; the router reads those gaps as corridors.
        var columnWidth = Js.Or(bands.Widths.Get(col), width);
        return new Pt(columnX + (columnWidth - width) / 2, rowY);
    }

    public static void ValidateErGridPlacement(JsonObject er, ErGrid? grid, Bands? bands, List<string> problems)
    {
        var seen = new JsMap<string, string>();
        foreach (var node in er["entities"].AsArrayOrEmpty())
        {
            var entity = (JsonObject)node!;
            var id = entity.ContainsKey("id") ? Js.PrimitiveString(entity["id"]) : "undefined";   // `${entity.id}`
            var hasPos = entity["pos"] is JsonArray { Count: 2 };
            if (!hasPos && grid is null)
            {
                problems.Add($"Entity \"{id}\" needs pos [x,y] or a grid layout with row/col.");
                continue;
            }
            if (hasPos) continue;
            var row = entity.Num("row"); var col = entity.Num("col");
            if (!IsInteger(row) || !IsInteger(col))
            {
                problems.Add($"Entity \"{id}\" needs pos [x,y] or grid row/col when layout.mode is \"grid\".");
                continue;
            }
            if (row < 0 || col < 0)
            {
                problems.Add($"Entity \"{id}\" row/col must be non-negative integers.");
                continue;
            }
            var (r, c) = (Js.Num(row!.Value), Js.Num(col!.Value));
            var key = $"{r},{c}";
            if (seen.Has(key)) problems.Add($"Entities \"{seen.Get(key)}\" and \"{id}\" share grid cell row {r} col {c}.");
            else seen.Set(key, id);
            var p = ResolveEntityPos(GridEntity.From(entity, 0), grid, bands);
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y))
                problems.Add($"Entity \"{id}\" could not be placed from row {r} col {c}.");
        }
    }

    static bool IsInteger(double? v) => v is { } d && double.IsInteger(d);
    static bool IsFinite(double? v) => v is { } d && double.IsFinite(d);
    // JS `map.get(k)` in arithmetic: a missing key is undefined, i.e. NaN.
    static double Val(JsMap<double, double> m, double k) => m.Has(k) ? m.Get(k) : double.NaN;
    // A JS array element in arithmetic: anything but a number reads as NaN here (undefined, or a non-number that the
    // isFinite guards reject).
    static double Elem(JsonArray a, int i) => i < a.Count && a[i] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.D() : double.NaN;
}
