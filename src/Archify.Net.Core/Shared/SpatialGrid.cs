namespace Archify.Net;

// spatial-grid.mjs: uniform spatial grid for "which items can this box reach" queries (#8).
public readonly record struct GridBox(double MinX, double MaxX, double MinY, double MaxY);

public sealed class SpatialGrid<T> where T : class
{
    // A cell box wider than this is not worth walking: the query returns every
    // inserted item instead, which stays a superset of what the box reaches.
    const double DefaultMaxCells = 4096;

    readonly double cellSize;
    readonly double maxCells;
    readonly Dictionary<(long X, long Y), List<T>> buckets = [];   // lookup only
    readonly List<T> items = [];
    // Items whose own box cannot be walked are candidates for every query.
    readonly List<T> overflow = [];

    SpatialGrid(double cellSize, double maxCells) { this.cellSize = cellSize; this.maxCells = maxCells; }

    public static SpatialGrid<T> Create(double cellSize, double maxCells = DefaultMaxCells) => new(cellSize, maxCells);

    readonly record struct Range(double X0, double X1, double Y0, double Y1);

    Range RangeOf(GridBox box) => new(
        Math.Floor(box.MinX / cellSize), Math.Floor(box.MaxX / cellSize),
        Math.Floor(box.MinY / cellSize), Math.Floor(box.MaxY / cellSize));

    // Number.isSafeInteger: finite, integral, |v| <= 2^53 - 1.
    internal static bool IsSafeInteger(double v) => double.IsFinite(v) && v == Math.Floor(v) && Math.Abs(v) <= 9007199254740991;

    // Finite coordinates can still land outside the safe-integer range, where
    // incrementing an index no longer advances it; and a legal coordinate can
    // name more cells than the grid is worth. Both cases stay out of the buckets
    // and are answered by the item list, so they never hang or allocate.
    bool Walkable(Range range) => IsSafeInteger(range.X0) && IsSafeInteger(range.X1)
        && IsSafeInteger(range.Y0) && IsSafeInteger(range.Y1)
        && (range.X1 - range.X0 + 1) * (range.Y1 - range.Y0 + 1) <= maxCells;

    public void Insert(GridBox box, T item)
    {
        items.Add(item);
        var range = RangeOf(box);
        if (!Walkable(range))
        {
            overflow.Add(item);
            return;
        }
        for (var x = (long)range.X0; x <= (long)range.X1; x += 1)
        {
            for (var y = (long)range.Y0; y <= (long)range.Y1; y += 1)
            {
                if (!buckets.TryGetValue((x, y), out var bucket)) { bucket = []; buckets[(x, y)] = bucket; }
                bucket.Add(item);
            }
        }
    }

    public List<T> Query(GridBox box)
    {
        var range = RangeOf(box);
        if (!Walkable(range)) return [.. items];
        var seen = new HashSet<T>(ReferenceEqualityComparer.Instance);   // lookup only
        var found = new List<T>();
        for (var x = (long)range.X0; x <= (long)range.X1; x += 1)
        {
            for (var y = (long)range.Y0; y <= (long)range.Y1; y += 1)
            {
                if (!buckets.TryGetValue((x, y), out var bucket)) continue;
                foreach (var item in bucket)
                {
                    if (!seen.Add(item)) continue;
                    found.Add(item);
                }
            }
        }
        foreach (var item in overflow)
        {
            if (!seen.Add(item)) continue;
            found.Add(item);
        }
        return found;
    }
}
