using System.Text;

namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/venn/layout.ts
    internal static partial class Venn
    {
        internal sealed class Circle(string id, double x, double y, double r)
        {
            public string Id = id;
            public double X = x, Y = y, R = r;
        }

        // Where the content of a region goes: the point inside all of `inside` and outside every other circle that is farthest
        // from their outlines, and how far that is. A margin of 0 or less means the circles as placed leave the region empty.
        internal readonly record struct Spot(double X, double Y, double Margin);

        const double Tau = Math.PI * 2;

        // JSON.stringify of a list of ids: a key for the list.
        static string JsonKey(IEnumerable<string> ids) => "[" + string.Join(",", ids.Select(Js.Quote)) + "]";

        static List<string> Sorted(List<string> ids)
        {
            var list = new List<string>(ids);
            Js.SortDefault(list);
            return list;
        }

        // For every overlap of three or more sets, adds the pairwise overlaps it implies and the diagram does not state, a
        // quarter of the smaller set each, so that the circles are drawn overlapping. Returns the list it was given when there
        // is nothing to add.
        public static List<VennSubset> EnsurePairwiseSubsets(List<VennSubset> subsets)
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            var sizes = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var subset in subsets)
            {
                present.Add(JsonKey(Sorted(subset.Sets)));
                if (subset.Sets.Count == 1) sizes[subset.Sets[0]] = subset.Size;
            }
            var added = new List<VennSubset>();
            foreach (var subset in subsets)
            {
                if (subset.Sets.Count < 3) continue;
                var members = Sorted(subset.Sets);
                for (var i = 0; i < members.Count - 1; i++)
                {
                    for (var j = i + 1; j < members.Count; j++)
                    {
                        List<string> pair = [members[i], members[j]];
                        if (!present.Add(JsonKey(pair))) continue;
                        var size = sizes.TryGetValue(pair[0], out var a) && sizes.TryGetValue(pair[1], out var b) ? Math.Min(a, b) / 4 : 2.5;
                        added.Add(new(pair, size, ""));
                    }
                }
            }
            return added.Count > 0 ? [.. subsets, .. added] : subsets;
        }

        // The area two circles share when their centers are d apart.
        public static double LensArea(double r1, double r2, double d)
        {
            if (d >= r1 + r2) return 0;
            var small = Math.Min(r1, r2);
            if (d <= Math.Abs(r1 - r2)) return Math.PI * small * small;
            var a1 = r1 * r1 * JsMath.Acos((d * d + r1 * r1 - r2 * r2) / (2 * d * r1));
            var a2 = r2 * r2 * JsMath.Acos((d * d + r2 * r2 - r1 * r1) / (2 * d * r2));
            var a3 = 0.5 * Math.Sqrt(Math.Max((-d + r1 + r2) * (d + r1 - r2) * (d - r1 + r2) * (d + r1 + r2), 0));
            return a1 + a2 - a3;
        }

        // How far apart two circles sit for their overlap to have the given area. The area falls as the distance grows, so
        // bisection finds it.
        public static double DistanceForOverlap(double r1, double r2, double area)
        {
            var low = Math.Abs(r1 - r2);
            var high = r1 + r2;
            if (!(area > 0)) return high;
            var min = Math.Min(r1, r2);
            if (area >= Math.PI * (min * min)) return low;   // Math.PI * Math.min(r1, r2) ** 2
            for (var i = 0; i < 48; i++)
            {
                var middle = (low + high) / 2;
                if (LensArea(r1, r2, middle) > area) low = middle;
                else high = middle;
            }
            return (low + high) / 2;
        }

        // How far a point is from the nearest outline it must stay within or without. Gives up, with some value below `floor`,
        // as soon as the point cannot reach `floor`.
        static double Margin(List<Circle> circles, IReadOnlyList<int> inside, IReadOnlyList<int> outside, double x, double y, double floor)
        {
            var best = double.PositiveInfinity;
            foreach (var i in inside)
            {
                var c = circles[i];
                var dx = x - c.X;
                var dy = y - c.Y;
                var room = c.R - Math.Sqrt(dx * dx + dy * dy);
                if (room < best)
                {
                    if (room < floor) return room;
                    best = room;
                }
            }
            foreach (var i in outside)
            {
                var c = circles[i];
                var dx = x - c.X;
                var dy = y - c.Y;
                var room = Math.Sqrt(dx * dx + dy * dy) - c.R;
                if (room < best)
                {
                    if (room < floor) return room;
                    best = room;
                }
            }
            return best;
        }

        // Finds the roomiest point of a region by sampling the smallest of its circles on a grid and then walking uphill from
        // the best sample. Ties go to the point nearest the middle of the region, so a symmetric arrangement gets symmetric
        // labels.
        public static Spot FindSpot(List<Circle> circles, IReadOnlyList<int> inside, IReadOnlyList<int> outside)
        {
            double cx = 0, cy = 0;
            var smallest = circles[inside[0]];
            foreach (var i in inside)
            {
                var c = circles[i];
                cx += c.X / inside.Count;
                cy += c.Y / inside.Count;
                if (c.R < smallest.R) smallest = c;
            }
            var r = smallest.R;
            var x = cx;
            var y = cy;
            var best = Margin(circles, inside, outside, x, y, double.NegativeInfinity);
            double off = 0;
            bool Consider(double px, double py)
            {
                var m = Margin(circles, inside, outside, px, py, best - r * 1e-9);
                if (!(m > best - r * 1e-9)) return false;
                var distance = Js.Hypot(px - cx, py - cy);
                if (m > best + r * 1e-9 || distance < off - r * 1e-9)
                {
                    best = m;
                    off = distance;
                    x = px;
                    y = py;
                    return true;
                }
                return false;
            }
            const int Grid = 10;
            for (var gy = -Grid; gy <= Grid; gy++)
                for (var gx = -Grid; gx <= Grid; gx++)
                    if (gx * gx + gy * gy <= Grid * Grid) Consider(smallest.X + gx * r / Grid, smallest.Y + gy * r / Grid);
            var step = r / Grid;
            for (var round = 0; round < 40 && step > r * 1e-6; round++)
            {
                var moved = false;
                for (var k = 0; k < 8; k++)
                {
                    var angle = k * Tau / 8;
                    if (Consider(x + JsMath.Cos(angle) * step, y + JsMath.Sin(angle) * step)) moved = true;
                }
                if (!moved) step /= 2;
            }
            return new(x, y, best);
        }

        // Places one circle per set, with areas in proportion to the sizes of the sets and each pair as far apart as its
        // overlap asks: one, two and three sets exactly, more by relaxing a ring of circles toward those distances. Lengths
        // are in units of the square root of a size.
        public static List<Circle> LayoutCircles(List<VennSubset> subsets)
        {
            var sizes = new JsMap<string, double>();
            foreach (var subset in subsets)
                foreach (var id in subset.Sets)
                    if (!sizes.Has(id)) sizes.Set(id, 10);
            foreach (var subset in subsets)
                if (subset.Sets.Count == 1) sizes.Set(subset.Sets[0], subset.Size);

            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            var circles = new List<Circle>();
            double largest = 0;
            foreach (var size in sizes.Values)
                if (double.IsFinite(size) && size > largest) largest = size;
            if (largest == 0) largest = 10;
            var widest = Math.Sqrt(largest / Math.PI);
            foreach (var (id, size) in sizes.Entries)
            {
                index[id] = circles.Count;
                // A set too small to hold its name, or with no size at all, still gets a circle that shows.
                var r = double.IsFinite(size) && size > 0 ? Math.Sqrt(size / Math.PI) : 0;
                circles.Add(new(id, 0, 0, Math.Max(r, widest * 0.3)));
            }
            var n = circles.Count;
            if (n < 2) return circles;

            // The overlap each pair was given, by the indexes of its circles.
            var overlaps = new Dictionary<int, double>();
            foreach (var subset in EnsurePairwiseSubsets(subsets))
            {
                if (subset.Sets.Count != 2) continue;
                var a = index[subset.Sets[0]];
                var b = index[subset.Sets[1]];
                if (a != b) overlaps[Math.Min(a, b) * n + Math.Max(a, b)] = subset.Size;
            }
            // How far apart each overlapping pair should be. Pairs that do not overlap only have to keep a gap.
            var wanted = new Dictionary<int, double>();
            foreach (var (key, area) in overlaps) wanted[key] = DistanceForOverlap(circles[key / n].R, circles[key % n].R, area);
            var gap = widest * 0.15;
            double Distance(int a, int b) => wanted.TryGetValue(a * n + b, out var d) ? d : circles[a].R + circles[b].R + gap;

            if (n == 2)
            {
                circles[1].X = Distance(0, 1);
                return circles;
            }

            if (n == 3)
            {
                // Two sets side by side and the third below, the corners of a triangle whose sides are the three distances. An
                // overlap of all three has to exist, so the triangle shrinks until it does.
                var ab = Distance(0, 1);
                var ac = Distance(0, 2);
                var bc = Distance(1, 2);
                var triple = subsets.Any(subset => subset.Sets.Distinct(StringComparer.Ordinal).Count() == 3);
                for (var attempt = 0; attempt < 40; attempt++)
                {
                    var x = ab > 0 ? (ab * ab + ac * ac - bc * bc) / (2 * ab) : 0;
                    circles[1].X = ab;
                    circles[2].X = Math.Max(-ac, Math.Min(ac, x));
                    circles[2].Y = Math.Sqrt(Math.Max(ac * ac - x * x, 0));
                    if (!triple || FindSpot(circles, [0, 1, 2], []).Margin > widest * 0.12) break;
                    ab *= 0.94;
                    ac *= 0.94;
                    bc *= 0.94;
                }
                return circles;
            }

            double total = 0;
            foreach (var c in circles) total += 2 * c.R + gap;
            var rounds = (int)Math.Min(300, Math.Floor(3e6 / ((double)n * n)));
            if (n <= 64)
            {
                var ring = total / Tau;
                for (var i = 0; i < n; i++)
                {
                    var angle = i * Tau / n - Math.PI / 2;
                    circles[i].X = JsMath.Cos(angle) * ring;
                    circles[i].Y = JsMath.Sin(angle) * ring;
                }
            }
            else
            {
                var columns = (int)Math.Ceiling(Math.Sqrt(n));
                var cell = 2 * widest + gap;
                for (var i = 0; i < n; i++)
                {
                    circles[i].X = i % columns * cell;
                    circles[i].Y = i / columns * cell;
                }
            }
            var dx = new double[n];
            var dy = new double[n];
            var pulls = new int[n];
            for (var round = 0; round < rounds; round++)
            {
                Array.Clear(dx);
                Array.Clear(dy);
                Array.Clear(pulls);
                for (var a = 0; a < n; a++)
                {
                    for (var b = a + 1; b < n; b++)
                    {
                        var known = wanted.TryGetValue(a * n + b, out var overlap);
                        var apart = circles[a].R + circles[b].R + gap;
                        var ux = circles[b].X - circles[a].X;
                        var uy = circles[b].Y - circles[a].Y;
                        var length = Js.Hypot(ux, uy);
                        if (!known && length >= apart) continue;
                        if (length < 1e-9)
                        {
                            ux = JsMath.Cos(a + b);
                            uy = JsMath.Sin(a + b);
                            length = 1;
                        }
                        var move = (length - (known ? overlap : apart)) / 2 / length;
                        dx[a] += ux * move;
                        dy[a] += uy * move;
                        dx[b] -= ux * move;
                        dy[b] -= uy * move;
                        pulls[a]++;
                        pulls[b]++;
                    }
                }
                for (var i = 0; i < n; i++)
                {
                    if (pulls[i] == 0) continue;
                    circles[i].X += dx[i] / pulls[i];
                    circles[i].Y += dy[i] / pulls[i];
                }
            }
            return circles;
        }

        sealed record Corner(double X, double Y, Circle A, Circle B);

        // The outline of the region all the given circles share, as an SVG path, or "" if they share none. Coordinates are
        // written with Builder.Num, the only format render.ts passes.
        public static string RegionPath(List<Circle> circles)
        {
            static string N(double v) => Builder.Num(v);
            static string Whole(Circle c) =>
                $"M{N(c.X - c.R)},{N(c.Y)}A{N(c.R)},{N(c.R)} 0 1 1 {N(c.X + c.R)},{N(c.Y)}A{N(c.R)},{N(c.R)} 0 1 1 {N(c.X - c.R)},{N(c.Y)}Z";
            if (circles.Count == 1) return Whole(circles[0]);
            bool Within(double x, double y, Circle skip, Circle other)
            {
                foreach (var c in circles)
                    if (c != skip && c != other && Js.Hypot(x - c.X, y - c.Y) > c.R * (1 + 1e-9)) return false;
                return true;
            }
            // The corners of the region: where two outlines cross inside all the other circles.
            var corners = new List<Corner>();
            for (var i = 0; i < circles.Count; i++)
            {
                for (var j = i + 1; j < circles.Count; j++)
                {
                    var a = circles[i];
                    var b = circles[j];
                    var d = Js.Hypot(b.X - a.X, b.Y - a.Y);
                    if (d >= a.R + b.R || d <= Math.Abs(a.R - b.R) || d == 0) continue;
                    var along = (a.R * a.R - b.R * b.R + d * d) / (2 * d);
                    var up = Math.Sqrt(Math.Max(a.R * a.R - along * along, 0));
                    var mx = a.X + (b.X - a.X) * along / d;
                    var my = a.Y + (b.Y - a.Y) * along / d;
                    foreach (var sign in (ReadOnlySpan<int>)[1, -1])
                    {
                        var x = mx + sign * up * (b.Y - a.Y) / d;
                        var y = my - sign * up * (b.X - a.X) / d;
                        if (Within(x, y, a, b)) corners.Add(new(x, y, a, b));
                    }
                }
            }
            if (corners.Count < 2)
            {
                // No corners: either one circle lies inside all the others, or there is nothing shared.
                var smallest = circles[0];
                foreach (var c in circles) if (c.R < smallest.R) smallest = c;
                foreach (var c in circles)
                    if (c != smallest && Js.Hypot(c.X - smallest.X, c.Y - smallest.Y) + smallest.R > c.R * (1 + 1e-9)) return "";
                return Whole(smallest);
            }
            double cx = 0, cy = 0;
            foreach (var p in corners)
            {
                cx += p.X / corners.Count;
                cy += p.Y / corners.Count;
            }
            Js.Sort(corners, (p, q) => JsMath.Atan2(p.Y - cy, p.X - cx) - JsMath.Atan2(q.Y - cy, q.X - cx));
            var path = new StringBuilder();
            for (var i = 0; i < corners.Count; i++)
            {
                var from = corners[i];
                var to = corners[(i + 1) % corners.Count];
                if (i == 0) path.Append($"M{N(from.X)},{N(from.Y)}");
                // The side between two corners is an arc of a circle that passes through both; of those, the one whose arc
                // stays inside the others.
                Circle? arc = null;
                var large = 0;
                var narrowest = double.PositiveInfinity;
                foreach (var c in new[] { from.A, from.B })
                {
                    if (c != to.A && c != to.B) continue;
                    var start = JsMath.Atan2(from.Y - c.Y, from.X - c.X);
                    var sweep = JsMath.Atan2(to.Y - c.Y, to.X - c.X) - start;
                    if (sweep <= 0) sweep += Tau;
                    var mx = c.X + c.R * JsMath.Cos(start + sweep / 2);
                    var my = c.Y + c.R * JsMath.Sin(start + sweep / 2);
                    var width = Js.Hypot(mx - (from.X + to.X) / 2, my - (from.Y + to.Y) / 2);
                    if (width < narrowest)
                    {
                        narrowest = width;
                        arc = c;
                        large = sweep > Math.PI ? 1 : 0;
                    }
                }
                if (arc is null) path.Append($"L{N(to.X)},{N(to.Y)}");
                else path.Append($"A{N(arc.R)},{N(arc.R)} 0 {large} 1 {N(to.X)},{N(to.Y)}");
            }
            return path.Append('Z').ToString();
        }
    }
}
