using System.Text.Json.Nodes;

namespace Archify.Net;

// label-capacity.mjs.
internal static class LabelCapacity
{
    public const double FlowLabelHeight = 16;

    public static (double Width, double Height) FlowLabelSize(JsonObject flow, IReadOnlyList<string?>? lines = null)
    {
        lines ??= [flow.Str("label")];
        var classification = flow.Str("classification");
        var longestLine = (double)Utils.TextUnits(classification ?? "");   // `flow.classification || ''`
        foreach (var line in lines) longestLine = Math.Max(longestLine, Utils.TextUnits(line));
        return (
            Js.Round(Math.Max(34, longestLine * 4.9 + 12) * 10) / 10,
            FlowLabelHeight + 11 * (lines.Count - 1 + (string.IsNullOrEmpty(classification) ? 0 : 1)));
    }

    readonly record struct RouteSegment(int RelationIndex, Pt Start, Pt End)
    {
        public Segment Seg => new(Start, End);
    }

    sealed record Shape(IReadOnlyList<string> Lines, double Width, double Height);

    static readonly string[] PinKeys = ["labelAt", "labelDx", "labelDy", "labelSegment"];

    // A last capacity repair after routing: two complete text rows can fit beside
    // a short arrow where one wide row cannot. Never move a route or a valid label.
    public static List<LabelRect> WrapBlockedFlowLabels(IReadOnlyList<LabelRect> labels, IReadOnlyList<LabelRoute> routes, IReadOnlyList<Box> components,
        IReadOnlyList<Rect> titles, IReadOnlyList<double> viewBox, double placementBottom)
    {
        var placed = labels.ToList();
        var segments = new List<RouteSegment>();
        foreach (var route in routes)
            for (var index = 0; index < route.Points.Count - 1; index++) segments.Add(new(route.RelationIndex, route.Points[index], route.Points[index + 1]));
        bool Inside(Rect rect) => rect.X >= 0 && rect.Y >= 0
            && rect.X + rect.Width <= viewBox[0] && rect.Y + rect.Height <= placementBottom;
        static bool Masks(RouteSegment segment, Rect rect) => (Geometry.SegmentRectClearanceWithin(segment.Seg, rect, 4) ?? 0) + 0.0001 < 4;   // `null + 0.0001`
        var obstacles = components.Select(c => (Rect)c).Concat(titles).ToList();
        for (var index = 0; index < placed.Count; index++)
        {
            var label = placed[index];
            if (PinKeys.Any(label.Relation.ContainsKey)) continue;   // `!== undefined`: a JSON null still counts
            if (Inside(label) && !components.Any(rect => Geometry.RectsOverlap(label, rect, -2))
                && !titles.Any(rect => Geometry.RectsOverlap(label, rect))
                && !placed.Where((_, i) => i != index).Any(rect => Geometry.RectsOverlap(label, rect))
                && !segments.Any(segment => segment.RelationIndex != label.RelationIndex && Masks(segment, label))) continue;
            var text = label.Label ?? "";
            // `[...segment(text)].slice(1).map(({ index }) => …)`: each word after the first starts a candidate split.
            var shapes = new List<Shape>();
            var split = 0;
            foreach (var word in Js.WordBreaks(text))
            {
                if (split > 0)
                {
                    string[] lines = [text[..split], text[split..]];
                    var (width, height) = FlowLabelSize(label.Relation, lines);
                    if (lines.All(line => Js.Trim(line) != "") && width < label.Width) shapes.Add(new(lines, width, height));
                }
                split += word.Length;
            }
            Js.Sort(shapes, (a, b) => a.Width - b.Width);
            if (shapes.Count > 8) shapes.RemoveRange(8, shapes.Count - 8);
            var blockers = obstacles.Select(rect => (Rect: rect, Gap: 2.0))
                .Concat(placed.Where((_, i) => i != index).Select(rect => (Rect: (Rect)rect, Gap: 2.0)))
                .Concat(segments.Select(segment => (Rect: new Rect(
                    Math.Min(segment.Start.X, segment.End.X), Math.Min(segment.Start.Y, segment.End.Y),
                    Math.Abs(segment.End.X - segment.Start.X), Math.Abs(segment.End.Y - segment.Start.Y)), Gap: 4.0)))
                .ToList();
            if (Search(label, index, shapes, blockers) is { } found) placed[index] = found;
        }
        return placed;

        // The labelled `search:` loop; `break search` is a return.
        LabelRect? Search(LabelRect label, int index, List<Shape> shapes, List<(Rect Rect, double Gap)> blockers)
        {
            var lx = label.Lx ?? double.NaN;
            var probes = 0;
            var seen = new HashSet<string>();   // ponytail: membership only, never enumerated
            foreach (var shape in shapes)
            {
                foreach (var segment in segments.Where(segment => segment.RelationIndex == label.RelationIndex))
                {
                    var (a, b) = (segment.Start, segment.End);
                    if (Math.Abs(a.Y - b.Y) > 0.0001) continue;
                    double low = Math.Min(a.X, b.X) + 8, high = Math.Max(a.X, b.X) - shape.Width - 8;
                    if (high < low) continue;
                    double[] ys = [a.Y - shape.Height - 4, a.Y + 4];
                    if (label.Y > a.Y) Array.Reverse(ys);
                    foreach (var y in ys)
                    {
                        var positions = new List<double> { Math.Max(low, Math.Min(high, lx - shape.Width / 2)), low, high };
                        foreach (var (rect, gap) in blockers)
                        {
                            if (y + shape.Height + gap <= rect.Y || rect.Y + rect.Height + gap <= y) continue;
                            positions.Add(rect.X - gap - shape.Width);
                            positions.Add(rect.X + rect.Width + gap);
                        }
                        var unique = new JsSet<double>();
                        foreach (var position in positions) unique.Add(position);
                        var xs = unique.Where(x => x >= low && x <= high).ToList();
                        Js.Sort(xs, (p, q) => Math.Abs(p + shape.Width / 2 - lx) - Math.Abs(q + shape.Width / 2 - lx));
                        foreach (var x in xs)
                        {
                            if (!seen.Add($"{Js.Num(x)},{Js.Num(y)},{Js.Num(shape.Width)},{Js.Num(shape.Height)}")) continue;
                            if (probes++ >= 64) return null;
                            var rect = label.Clone();   // `{ ...label, ...shape, x, y, lx, ly }`
                            rect.Lines = shape.Lines; rect.Width = shape.Width; rect.Height = shape.Height;
                            rect.X = x; rect.Y = y; rect.Lx = x + shape.Width / 2; rect.Ly = y + 11;
                            if (!Inside(rect) || obstacles.Any(other => Geometry.RectsOverlap(rect, other, 2))
                                || placed.Where((_, i) => i != index).Any(other => Geometry.RectsOverlap(rect, other, 2))
                                || segments.Any(s => Masks(s, rect))) continue;
                            return rect;
                        }
                    }
                }
            }
            return null;
        }
    }
}
