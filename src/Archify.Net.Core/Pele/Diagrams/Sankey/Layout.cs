namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/sankey/layout.ts
    internal static partial class Sankey
    {
        internal sealed record LayoutOptions(double Width, double Height, double NodeWidth, double NodePadding, double LabelHeight, string Alignment);

        // Value is the total flow through the node, in the diagram's units.
        internal sealed class PlacedNode(int layer, double value, double x)
        {
            public int Layer { get; } = layer;
            public double Value { get; } = value;
            public double X { get; } = x;
            public double Y { get; set; }
            public double Height { get; set; }
        }

        // SourceY/TargetY: vertical centres of the band where it leaves its source and where it enters its target.
        internal sealed class PlacedLink(int source, int target, double value)
        {
            public int Source { get; } = source;
            public int Target { get; } = target;
            public double Value { get; } = value;
            public double Thickness { get; set; }
            public double SourceY { get; set; }
            public double TargetY { get; set; }
        }

        const int Passes = 6;

        // JS `a || b` for a comparator's number: 0 and NaN are falsy.
        static double Or(double a, double b) => a != 0 && !double.IsNaN(a) ? a : b;

        // Most nodes have a handful of bands, and the built-in sort is slow to start on short lists.
        static void SortList(List<int> list, Func<int, int, double> compare)
        {
            if (list.Count > 8)
            {
                Js.Sort(list, compare);
                return;
            }
            for (var i = 1; i < list.Count; i++)
            {
                var item = list[i];
                var j = i - 1;
                while (j >= 0 && compare(list[j], item) > 0)
                {
                    list[j + 1] = list[j];
                    j--;
                }
                list[j + 1] = item;
            }
        }

        // Places nodes in columns by their distance from a source, sizes them by their flow, and then lets each column settle
        // next to its neighbours so that the bands cross less.
        public static (List<PlacedNode> Nodes, List<PlacedLink> Links) LayoutSankey(SankeyDb model, LayoutOptions options)
        {
            var n = model.Nodes.Count;
            var links = model.Links.Select(link => new PlacedLink(link.Source.Index, link.Target.Index,
                double.IsFinite(link.Value) && link.Value > 0 ? link.Value : 0)).ToList();
            var outgoing = new List<int>[n];
            var incoming = new List<int>[n];
            for (var i = 0; i < n; i++)
            {
                outgoing[i] = [];
                incoming[i] = [];
            }
            for (var k = 0; k < links.Count; k++)
            {
                outgoing[links[k].Source].Add(k);
                incoming[links[k].Target].Add(k);
            }

            // Longest distance from a source, in topological order; what is left over lies on a cycle.
            var depth = new int[n];
            var pending = new int[n];
            var order = new List<int>();
            for (var i = 0; i < n; i++)
            {
                pending[i] = incoming[i].Count;
                if (pending[i] == 0) order.Add(i);
            }
            for (var q = 0; q < order.Count; q++)
            {
                var i = order[q];
                foreach (var k in outgoing[i])
                {
                    var t = links[k].Target;
                    if (depth[t] < depth[i] + 1) depth[t] = depth[i] + 1;
                    if (--pending[t] == 0) order.Add(t);
                }
            }
            if (order.Count < n)
            {
                // Every node left over has a predecessor that is left over too; walking back finds the loop.
                var stuck = 0;
                while (pending[stuck] == 0) stuck++;
                var seen = new bool[n];
                while (!seen[stuck])
                {
                    seen[stuck] = true;
                    foreach (var k in incoming[stuck])
                    {
                        if (pending[links[k].Source] > 0)
                        {
                            stuck = links[k].Source;
                            break;
                        }
                    }
                }
                throw new PeleException($"Sankey diagrams cannot have circular links, and \"{model.Nodes[stuck].Id}\" is part of one.", "semantic");
            }
            var layers = 1;
            for (var i = 0; i < n; i++) if (depth[i] + 1 > layers) layers = depth[i] + 1;
            var reach = new int[n];
            for (var q = n - 1; q >= 0; q--)
            {
                var i = order[q];
                foreach (var k in outgoing[i]) if (reach[i] < reach[links[k].Target] + 1) reach[i] = reach[links[k].Target] + 1;
            }

            // Flows are scaled to the largest one, so that sums of huge values stay finite.
            double largest = 0;
            foreach (var link in links) if (link.Value > largest) largest = link.Value;
            var unit = largest > 0 ? 1 / largest : 0;

            var nodes = new List<PlacedNode>(n);
            var flow = new double[n];
            var columns = new List<int>[layers];
            for (var l = 0; l < layers; l++) columns[l] = [];
            var step = layers > 1 ? (options.Width - options.NodeWidth) / (layers - 1) : 0;
            for (var i = 0; i < n; i++)
            {
                int layer;
                if (options.Alignment == "left") layer = depth[i];
                else if (options.Alignment == "right") layer = layers - 1 - reach[i];
                else if (options.Alignment == "center")
                {
                    layer = depth[i];
                    if (incoming[i].Count == 0 && outgoing[i].Count > 0)
                    {
                        layer = layers;
                        foreach (var k in outgoing[i]) if (depth[links[k].Target] - 1 < layer) layer = depth[links[k].Target] - 1;
                    }
                }
                else layer = outgoing[i].Count > 0 ? depth[i] : layers - 1;
                double into = 0, out_ = 0, value = 0, valueOut = 0;
                foreach (var k in incoming[i])
                {
                    into += links[k].Value * unit;
                    value += links[k].Value;
                }
                foreach (var k in outgoing[i])
                {
                    out_ += links[k].Value * unit;
                    valueOut += links[k].Value;
                }
                flow[i] = Math.Max(into, out_);
                nodes.Add(new(layer, Math.Max(value, valueOut), layer * step));
                columns[layer].Add(i);
            }

            var tallest = 1;
            foreach (var column in columns) if (column.Count > tallest) tallest = column.Count;
            var gap = tallest > 1 ? Math.Min(options.NodePadding, options.Height / (tallest - 1)) : options.NodePadding;
            var scale = double.PositiveInfinity;
            foreach (var column in columns)
            {
                double sum = 0;
                foreach (var i in column) sum += flow[i];
                if (sum > 0) scale = Math.Min(scale, (options.Height - (column.Count - 1) * gap) / sum);
            }
            if (!double.IsFinite(scale) || scale < 0) scale = 0;

            // A thin node still needs room for its label. Where every column can afford it, each node claims at least a
            // label's height, and the flows are scaled down to make that fit.
            var claim = Math.Max(0, options.LabelHeight - gap);
            bool Fits(double s)
            {
                foreach (var column in columns)
                {
                    var extent = (column.Count - 1) * gap;
                    foreach (var i in column) extent += Math.Max(flow[i] * s, claim);
                    if (extent > options.Height + 1e-6) return false;
                }
                return true;
            }
            if (claim > 0 && !Fits(scale))
            {
                if (Fits(0))
                {
                    double low = 0;
                    for (var k = 0; k < 40; k++)
                    {
                        var mid = (low + scale) / 2;
                        if (Fits(mid)) low = mid;
                        else scale = mid;
                    }
                    scale = low;
                }
                else claim = 0;
            }

            // The room a node keeps clear above and below itself.
            var margin = new double[n];
            foreach (var link in links) link.Thickness = link.Value * unit * scale;
            foreach (var column in columns)
            {
                double y = 0;
                foreach (var i in column)
                {
                    nodes[i].Height = flow[i] * scale;
                    margin[i] = Math.Max(0, claim - nodes[i].Height) / 2;
                    nodes[i].Y = y + margin[i];
                    y += nodes[i].Height + 2 * margin[i] + gap;
                }
                // Spare room is shared out between the nodes of a column.
                var spare = (options.Height - y + gap) / (column.Count + 1);
                for (var k = 0; k < column.Count; k++) nodes[column[k]].Y += spare * (k + 1);
            }

            double Center(int i) => nodes[i].Y + nodes[i].Height / 2;

            // Bands leave and enter a node in the order of where they go, so they do not cross at it. These are the offsets
            // of each band's middle from the top of the node at either end.
            var sourceOffset = new double[links.Count];
            var targetOffset = new double[links.Count];
            double ByTarget(int a, int b) => Or(Center(links[a].Target) - Center(links[b].Target), a - b);
            double BySource(int a, int b) => Or(Center(links[a].Source) - Center(links[b].Source), a - b);
            double ByCenter(int a, int b) => Or(Center(a) - Center(b), a - b);
            void StackOut(int i)
            {
                double y = 0;
                SortList(outgoing[i], ByTarget);
                foreach (var k in outgoing[i])
                {
                    sourceOffset[k] = y + links[k].Thickness / 2;
                    y += links[k].Thickness;
                }
            }
            void StackIn(int i)
            {
                double y = 0;
                SortList(incoming[i], BySource);
                foreach (var k in incoming[i])
                {
                    targetOffset[k] = y + links[k].Thickness / 2;
                    y += links[k].Thickness;
                }
            }
            void StackAll()
            {
                for (var i = 0; i < n; i++)
                {
                    StackOut(i);
                    StackIn(i);
                }
            }

            // Moves each node of a column towards where its bands would run level, then restores the order and the gaps.
            // `forward` looks at the bands coming in, otherwise at those going out.
            void Settle(List<int> column, double pull, bool forward)
            {
                foreach (var i in column)
                {
                    var list = forward ? incoming[i] : outgoing[i];
                    if (list.Count == 0) continue;
                    if (forward) StackIn(i);
                    else StackOut(i);
                    double weighted = 0, weight = 0, plain = 0;
                    foreach (var k in list)
                    {
                        var level = forward
                            ? nodes[links[k].Source].Y + sourceOffset[k] - targetOffset[k]
                            : nodes[links[k].Target].Y + targetOffset[k] - sourceOffset[k];
                        weighted += level * links[k].Thickness;
                        weight += links[k].Thickness;
                        plain += Center(forward ? links[k].Source : links[k].Target) - nodes[i].Height / 2;
                    }
                    var wanted = weight > 0 ? weighted / weight : plain / list.Count;
                    nodes[i].Y += (wanted - nodes[i].Y) * pull;
                }
                SortList(column, ByCenter);
                double y = 0;
                foreach (var i in column)
                {
                    if (nodes[i].Y - margin[i] < y) nodes[i].Y = y + margin[i];
                    y = nodes[i].Y + nodes[i].Height + margin[i] + gap;
                }
                y = options.Height;
                for (var k = column.Count - 1; k >= 0; k--)
                {
                    var i = column[k];
                    if (nodes[i].Y + nodes[i].Height + margin[i] > y) nodes[i].Y = y - nodes[i].Height - margin[i];
                    y = nodes[i].Y - margin[i] - gap;
                }
            }

            StackAll();
            for (var pass = 0; pass < Passes; pass++)
            {
                var pull = JsMath.Pow(0.99, pass);
                for (var l = layers - 2; l >= 0; l--) Settle(columns[l], pull, false);
                StackAll();
                for (var l = 1; l < layers; l++) Settle(columns[l], pull, true);
                StackAll();
            }
            for (var k = 0; k < links.Count; k++)
            {
                links[k].SourceY = nodes[links[k].Source].Y + sourceOffset[k];
                links[k].TargetY = nodes[links[k].Target].Y + targetOffset[k];
            }

            return (nodes, links);
        }
    }
}
