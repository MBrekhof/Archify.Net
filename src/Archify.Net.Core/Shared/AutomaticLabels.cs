namespace Archify.Net;

// automatic-labels.mjs.
public sealed record LabelRoute(int RelationIndex, IReadOnlyList<Pt> Points);

public static class AutomaticLabels
{
    // A route segment indexed in the spatial grid; a class so the grid's `seen` set keys by identity like a JS Set.
    sealed class RouteSegment(int relationIndex, Pt start, Pt end)
    {
        public int RelationIndex { get; } = relationIndex;
        public Pt Start { get; } = start;
        public Pt End { get; } = end;
        public Segment Seg => new(Start, End);
    }

    static double Get(Rect rect, string key) => key switch { "x" => rect.X, "y" => rect.Y, "width" => rect.Width, _ => rect.Height };

    // A bounded fallback for an unpinned label whose usual position collides.
    // It never routes an edge, moves a node, expands the canvas, or rewrites input.
    public static List<LabelRect> PlaceAutomaticLabels(IReadOnlyList<LabelRect> labels, IReadOnlyList<LabelRoute> routes, IReadOnlyList<Box> components,
        IReadOnlyList<Rect> titles, IReadOnlyList<double> viewBox, double? placementBottom = null, bool fallbackRing = true,
        bool keepFallbackNearRoute = false, bool gridSweep = false, double maxOwnRouteDistance = double.PositiveInfinity)
    {
        var bottom = placementBottom ?? viewBox[1];
        var placed = labels.ToList();
        var obstacles = components.Select(c => (Rect)c).Concat(titles).ToList();
        var segments = new List<RouteSegment>();
        foreach (var route in routes)
        {
            var normalized = Geometry.NormalizeRoutePoints(route.Points);
            for (var index = 1; index < normalized.Count; index++) segments.Add(new(route.RelationIndex, normalized[index - 1], normalized[index]));
        }
        bool Inside(Rect rect) =>
            rect.X >= 0 && rect.Y >= 0
            && rect.X + rect.Width <= viewBox[0] && rect.Y + rect.Height <= viewBox[1];
        // The mask test asked every segment about every candidate position. Segments
        // go into a uniform grid once, and a candidate only asks the cells it covers.
        const double SegmentCell = 120;
        var segmentGrid = SpatialGrid<RouteSegment>.Create(SegmentCell);
        foreach (var segment in segments)
        {
            var (sx, sy) = segment.Start;
            var (ex, ey) = segment.End;
            segmentGrid.Insert(new(Math.Min(sx, ex), Math.Max(sx, ex), Math.Min(sy, ey), Math.Max(sy, ey)), segment);
        }
        List<RouteSegment> SegmentsNear(Rect rect, double margin) => segmentGrid.Query(new(
            rect.X - margin, rect.X + rect.Width + margin,
            rect.Y - margin, rect.Y + rect.Height + margin));
        bool MasksRoute(LabelRect rect)
        {
            foreach (var segment in SegmentsNear(rect, 4))
            {
                if (segment.RelationIndex == rect.RelationIndex) continue;
                if ((Geometry.SegmentRectClearanceWithin(segment.Seg, rect, 4) ?? 0) + 0.0001 < 4) return true;   // `null + 0.0001`
            }
            return false;
        }
        bool OverlapsLabel(Rect rect, int index, double gap = 0) => placed.Where((_, otherIndex) => otherIndex != index).Any(other => Geometry.RectsOverlap(rect, other, gap));
        bool Clear(LabelRect rect, int index) =>
            Inside(rect) && rect.Y + rect.Height <= bottom
            && !obstacles.Any(obstacle => Geometry.RectsOverlap(rect, obstacle, 2))
            && !OverlapsLabel(rect, index, 2) && !MasksRoute(rect)
            && (!double.IsFinite(maxOwnRouteDistance) || segments.Any(segment =>
                segment.RelationIndex == rect.RelationIndex
                && (Geometry.SegmentRectClearanceWithin(segment.Seg, rect, maxOwnRouteDistance) ?? 0) <= maxOwnRouteDistance));
        static LabelRect RectAt(LabelRect label, double lx, double ly)
        {
            var copy = label.Clone();
            copy.Lx = lx; copy.Ly = ly; copy.X = lx - label.Width / 2; copy.Y = ly - 10;
            return copy;
        }
        // Fixed fractions can miss a narrow free slot. Try the ends of the free
        // intervals beside an orthogonal segment, using obstacle edges rather
        // than a finer sampling grid. The ordinary clearance test still decides
        // whether a candidate is usable; this search never changes geometry.
        List<LabelRect> SlidingCandidates(LabelRect label, int index, RouteSegment segment)
        {
            var (a, b) = (segment.Start, segment.End);
            var horizontal = Math.Abs(a.Y - b.Y) < 0.0001;
            if (!horizontal && Math.Abs(a.X - b.X) >= 0.0001) return [];
            var axis = horizontal ? "x" : "y";
            var size = horizontal ? "width" : "height";
            var across = horizontal ? "y" : "x";
            var crossSize = horizontal ? "height" : "width";
            Rect labelRect = label;
            var low = Math.Min(horizontal ? a.X : a.Y, horizontal ? b.X : b.Y) + 8;
            var high = Math.Max(horizontal ? a.X : a.Y, horizontal ? b.X : b.Y) - Get(labelRect, size) - 8;
            if (high < low) return [];
            var offsets = horizontal
                ? new[] { a.Y - 20, a.Y + 10, a.Y - 28, a.Y + 18 }
                : [a.X - label.Width - 6, a.X + 6, a.X - label.Width - 14, a.X + 14];
            var results = new List<LabelRect>();
            foreach (var offset in offsets)
            {
                // { ...label, [axis]: low, [size]: high - low + label[size], [across]: offset }
                var band = horizontal
                    ? new Rect(low, offset, high - low + label.Width, label.Height)
                    : new Rect(offset, low, label.Width, high - low + label.Height);
                var blockers = new List<(Rect Rect, double Gap)>();
                foreach (var rect in obstacles) blockers.Add((rect, 2));
                for (var otherIndex = 0; otherIndex < placed.Count; otherIndex++) if (otherIndex != index) blockers.Add((placed[otherIndex], 2));
                foreach (var other in SegmentsNear(band, 4))
                {
                    if (other.RelationIndex == label.RelationIndex) continue;
                    blockers.Add((new Rect(
                        Math.Min(other.Start.X, other.End.X), Math.Min(other.Start.Y, other.End.Y),
                        Math.Abs(other.End.X - other.Start.X), Math.Abs(other.End.Y - other.Start.Y)), 4));
                }
                var positions = new JsSet<double>();
                positions.Add(Math.Max(low, Math.Min(high, Get(labelRect, axis))));
                positions.Add(low);
                positions.Add(high);
                foreach (var (rect, gap) in blockers)
                {
                    if (offset + Get(labelRect, crossSize) + gap <= Get(rect, across)
                        || Get(rect, across) + Get(rect, crossSize) + gap <= offset) continue;
                    positions.Add(Get(rect, axis) - gap - Get(labelRect, size));
                    positions.Add(Get(rect, axis) + Get(rect, size) + gap);
                }
                var ordered = positions.Where(position => position >= low && position <= high).ToList();
                Js.Sort(ordered, (left, right) => Math.Abs(left - Get(labelRect, axis)) - Math.Abs(right - Get(labelRect, axis)));
                foreach (var position in ordered)
                {
                    // { ...label, [axis]: position, [across]: offset } → rectAt(label, rect.x + width / 2, rect.y + 10)
                    var (rx, ry) = horizontal ? (position, offset) : (offset, position);
                    results.Add(RectAt(label, rx + label.Width / 2, ry + 10));
                }
            }
            return results;
        }
        // An opted-in grid layout runs parallel lines through shared gaps. Rank
        // every position along all of the label's own segments: beside the line
        // first, then centred on its own line (the plate interrupts only that
        // line), then stepping outward past neighbouring parallels.
        List<Pt> GridCandidates(LabelRect label)
        {
            double[] fractions = [0.5, 0.25, 0.75, 0.375, 0.625, 0.125, 0.875];
            var ranked = new List<(double Tier, double Lx, double Ly)>();
            // A close parallel of another relationship on one side (a reciprocal
            // pair) makes a label on that side read as the neighbour's: prefer the
            // far side.
            bool ParallelOnLowSide(Pt a, Pt b, int axis)
            {
                var across = 1 - axis;
                var (low, high) = (Math.Min(Coord(a, axis), Coord(b, axis)), Math.Max(Coord(a, axis), Coord(b, axis)));
                double? nearest = null;
                foreach (var other in segments)
                {
                    if (other.RelationIndex == label.RelationIndex) continue;
                    if (Math.Abs(Coord(other.Start, across) - Coord(other.End, across)) > 0.0001) continue;
                    var distance = Coord(other.Start, across) - Coord(a, across);
                    if (Math.Abs(distance) < 0.0001 || Math.Abs(distance) > 36) continue;
                    var overlap = Math.Min(high, Math.Max(Coord(other.Start, axis), Coord(other.End, axis)))
                        - Math.Max(low, Math.Min(Coord(other.Start, axis), Coord(other.End, axis)));
                    if (overlap <= 0) continue;
                    if (nearest is null || Math.Abs(distance) < Math.Abs(nearest.Value)) nearest = distance;
                }
                return nearest is not null && nearest < 0;
            }
            foreach (var segment in segments.Where(segment => segment.RelationIndex == label.RelationIndex))
            {
                var (a, b) = (segment.Start, segment.End);
                if (Math.Abs(a.Y - b.Y) < 0.0001 && Math.Abs(a.X - b.X) >= label.Width + 16)
                {
                    var (above, below) = ParallelOnLowSide(a, b, 0) ? (1.0, 0.0) : (0.0, 1.0);
                    foreach (var fraction in fractions)
                    {
                        var x = a.X + (b.X - a.X) * fraction;
                        if (Math.Min(Math.Abs(x - a.X), Math.Abs(x - b.X)) < label.Width / 2 + 6) continue;
                        ranked.Add((above, x, a.Y - 10)); ranked.Add((below, x, a.Y + 20)); ranked.Add((2, x, a.Y + 3)); ranked.Add((3, x, a.Y - 18)); ranked.Add((3, x, a.Y + 28));
                    }
                }
                else if (Math.Abs(a.X - b.X) < 0.0001 && Math.Abs(a.Y - b.Y) >= label.Height + 16)
                {
                    var leftFirst = !ParallelOnLowSide(a, b, 1);
                    foreach (var fraction in fractions)
                    {
                        var y = a.Y + (b.Y - a.Y) * fraction;
                        if (Math.Min(Math.Abs(y - a.Y), Math.Abs(y - b.Y)) < label.Height / 2 + 6) continue;
                        ranked.Add((2, a.X, y + 3));
                        double[] offsets = [6, 14, 22, 30, 38, 46, 54];
                        for (var step = 0; step < offsets.Length; step++)
                        {
                            var offset = offsets[step];
                            var tier = step == 0 ? 0 : step == 1 ? 1 : 2 + step;
                            ranked.Add((tier + (leftFirst ? 0 : 0.5), a.X - label.Width / 2 - offset, y + 3));
                            ranked.Add((tier + (leftFirst ? 0.5 : 0), a.X + label.Width / 2 + offset, y + 3));
                        }
                    }
                }
            }
            var withOrder = ranked.Select((entry, order) => (entry.Tier, entry.Lx, entry.Ly, Order: (double)order)).ToList();
            Js.Sort(withOrder, (left, right) => left.Tier - right.Tier is var d && d != 0 ? d : left.Order - right.Order);
            return withOrder.Select(e => new Pt(e.Lx, e.Ly)).ToList();
        }

        static bool Pinned(LabelRect label) => new[] { "labelAt", "labelDx", "labelDy", "labelSegment" }.Any(key => label.Relation.ContainsKey(key));
        bool NeedsPlacement(LabelRect label, int index) => !Inside(label)
            || components.Any(component => Geometry.RectsOverlap(label, component, -2))
            || titles.Any(title => Geometry.RectsOverlap(label, title))
            || OverlapsLabel(label, index) || MasksRoute(label);
        void PlaceLabel(int index, bool rerank)
        {
            var label = placed[index];
            // Match actual defect thresholds before searching; a valid placement is
            // not a reason to restyle the diagram. New placements leave extra space.
            // The grid ranking already starts from the preferred position, so it
            // also re-ranks labels whose default spot is merely valid.
            if (!rerank && !NeedsPlacement(label, index)) return;
            if (rerank)
            {
                // Callers measure the plate one pixel higher than rectAt; test with a
                // pixel of slack so the chosen position also passes their clearance.
                var replacement = GridCandidates(label).Select(p => RectAt(label, p.X, p.Y))
                    .FirstOrDefault(rect =>
                    {
                        var slack = rect.Clone();
                        slack.X = rect.X - 1; slack.Y = rect.Y - 1; slack.Width = rect.Width + 2; slack.Height = rect.Height + 2;
                        return Clear(slack, index);
                    });
                if (replacement is not null)
                {
                    placed[index] = replacement;
                    return;
                }
            }
            foreach (var segment in segments.Where(segment => segment.RelationIndex == label.RelationIndex))
            {
                var (a, b) = (segment.Start, segment.End);
                var candidates = new List<Pt>();
                // These candidates sit beside the line. They need the same 2px node
                // clearance checked by clear(), not 8px reserved at each endpoint.
                // Extra endpoint padding can hide a valid outer slot for a reciprocal
                // pair and make the planner send its other arrow around both nodes.
                if (Math.Abs(a.Y - b.Y) < 0.0001 && Math.Abs(a.X - b.X) >= label.Width + 4)
                {
                    foreach (var fraction in new[] { 0.5, 0.25, 0.75, 0.125, 0.875 })
                    {
                        var x = a.X + (b.X - a.X) * fraction;
                        if (Math.Min(Math.Abs(x - a.X), Math.Abs(x - b.X)) < 8) continue;
                        candidates.Add(new(x, a.Y - 10));
                        candidates.Add(new(x, a.Y + 20));
                        candidates.Add(new(x, a.Y - 18));
                        candidates.Add(new(x, a.Y + 28));
                    }
                }
                else if (Math.Abs(a.X - b.X) < 0.0001 && Math.Abs(a.Y - b.Y) >= label.Height + 16)
                {
                    foreach (var fraction in new[] { 0.5, 0.25, 0.75, 0.125, 0.875 })
                    {
                        var y = a.Y + (b.Y - a.Y) * fraction;
                        if (Math.Min(Math.Abs(y - a.Y), Math.Abs(y - b.Y)) < 8) continue;
                        candidates.Add(new(a.X - label.Width / 2 - 6, y + 3));
                        candidates.Add(new(a.X + label.Width / 2 + 6, y + 3));
                        candidates.Add(new(a.X - label.Width / 2 - 14, y + 3));
                        candidates.Add(new(a.X + label.Width / 2 + 14, y + 3));
                    }
                }
                var replacement = candidates.Select(p => RectAt(label, p.X, p.Y)).FirstOrDefault(rect => Clear(rect, index));
                if (replacement is not null)
                {
                    placed[index] = replacement;
                    break;
                }
            }
            if (!ReferenceEquals(placed[index], label)) return;
            var sliding = segments.Where(segment => segment.RelationIndex == label.RelationIndex)
                .SelectMany(segment => SlidingCandidates(label, index, segment)).FirstOrDefault(rect => Clear(rect, index));
            if (sliding is not null)
            {
                placed[index] = sliding;
                return;
            }
            if (!fallbackRing) return;

            // Dense but valid topologies can leave every point directly beside the
            // relationship occupied by another route. Search a small deterministic
            // ring around the current anchor and the relationship's segment centres.
            // A collision-free island above a node is not a readable edge label.
            // Architecture opts into keeping the mask within two label heights of
            // its own route; shared callers retain their existing policy. If no nearby
            // slot fits, retain the collision so validation can request more space.
            var ownSegments = segments.Where(segment => segment.RelationIndex == label.RelationIndex).ToList();
            var baseAnchors = new List<Pt> { new(label.Lx, label.Ly) };
            baseAnchors.AddRange(ownSegments.Select(segment => new Pt(
                (segment.Start.X + segment.End.X) / 2,
                (segment.Start.Y + segment.End.Y) / 2)));
            var horizontalStep = label.Width / 2 + 12;
            Pt[] ringOffsets =
            [
                new(0, -28), new(0, 38),
                new(-horizontalStep, -28), new(horizontalStep, -28),
                new(-horizontalStep, 38), new(horizontalStep, 38),
                new(-(label.Width + 20), -52), new(label.Width + 20, -52),
                new(-(label.Width + 20), 62), new(label.Width + 20, 62),
                new(-(label.Width + 20), -76), new(label.Width + 20, -76),
                new(-(label.Width + 20), 86), new(label.Width + 20, 86),
            ];
            var fallback = baseAnchors.SelectMany(anchor => ringOffsets.Select(d => RectAt(label, anchor.X + d.X, anchor.Y + d.Y)))
                .FirstOrDefault(rect => Clear(rect, index) && (!keepFallbackNearRoute || ownSegments.Any(segment =>
                    (Geometry.SegmentRectClearanceWithin(segment.Seg, rect, label.Height * 2) ?? 0) <= label.Height * 2)));
            if (fallback is not null) placed[index] = fallback;
        }
        var pending = Enumerable.Range(0, placed.Count).Where(index => !Pinned(placed[index])).ToList();
        // A later label can vacate the only slot available to an earlier one.
        // Retry only unresolved labels after a move; accepted placements clear all
        // current labels and fixed geometry, so each success retires one label.
        // A stalled pass ends the search, with at most one pass per input label.
        for (var pass = 0; pass < labels.Count && pending.Count > 0; pass++)
        {
            var moved = false;
            foreach (var index in pending)
            {
                var before = placed[index];
                PlaceLabel(index, gridSweep && pass == 0);
                moved |= !ReferenceEquals(placed[index], before);
            }
            if (!moved) break;
            pending = pending.Where(index => NeedsPlacement(placed[index], index)).ToList();
        }
        return placed;
    }

    static double Coord(Pt p, int axis) => axis == 0 ? p.X : p.Y;

    // The rect a single unpinned label would occupy given only its own route and
    // the nodes: what the planner reserves before the remaining routes are laid.
    // Only a placement beside the route itself is worth reserving; a label that
    // would already need the fallback ring is left to the final placement pass.
    public static LabelRect? ReservedLabelRect(LabelRect label, IReadOnlyList<Pt> points, IReadOnlyList<Box> components, IReadOnlyList<LabelRoute>? routes = null,
        IReadOnlyList<LabelRect>? labels = null, IReadOnlyList<double>? viewBox = null, double placementBottom = double.PositiveInfinity)
    {
        var own = label.Clone(); own.RelationIndex = -1;
        var others = (labels ?? []).Select(other => { var copy = other.Clone(); copy.RelationIndex = -2; return copy; });
        var rect = PlaceAutomaticLabels(
            [own, .. others],
            [new LabelRoute(-1, points), .. routes ?? []],
            components,
            [],
            viewBox ?? [double.PositiveInfinity, double.PositiveInfinity],
            placementBottom,
            fallbackRing: false)[0];
        return components.Any(component => Geometry.RectsOverlap(rect, component, -2)) ? null : rect;
    }
}
