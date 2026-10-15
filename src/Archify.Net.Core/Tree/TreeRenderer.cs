using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Archify.Net;

// render-tree.mjs. The module-level flow (line 28 to the final writeDiagram call) becomes one Scene built per render;
// `--layout-json` becomes the LayoutReport on the result and writeDiagram's HTML is returned by the facade.
public sealed class TreeRenderer : IDiagramRenderer
{
    public (string Svg, JsonArray? Cards, LayoutReport? Layout) Render(RenderContext ctx)
    {
        var scene = new Scene(ctx);
        var svg = scene.RenderSvg();
        return (svg, ctx.Diagram.Arr("cards"), new LayoutReport(scene.BuildLayoutReport()));
    }

    static class Layout
    {
        public const double Margin = 32;
        public const double Top = 64;
        public const double PadX = 14;
        public const double PadY = 12;
        public const double LabelFont = 13;
        public const double LabelLine = 17;
        public const double SublabelFont = 11;
        public const double SublabelLine = 14;
        public const double RootLabelFont = 14.5;
        public const double ToggleR = 7.5;
    }

    const double ADVANCE = NodeTextFit.WidthFactor;
    const int STACK_MIN = 3;
    const double STACK_GAP = 10;
    static readonly string[] SUBTREE_HUES = ["backend", "database", "cloud", "frontend", "messagebus", "external"];

    // `{ stack: [...] }` or `{ single: id }` (:168-186); width/rowH/spine are written later, as upstream does.
    sealed class Group
    {
        public List<string>? Stack;
        public string? Single;
        public double SpineOffset, Indent, Width, RowH, Spine;
    }

    // The `{ ...node, isRoot, labelFont, ... }` record (:226), mutated in place where upstream spreads a copy: no stale
    // copy is read after any of those spreads.
    sealed class TreeNode
    {
        public required JsonObject Source;
        public required string Id;
        public string? Parent;
        public required string Label;
        public bool IsRoot, Stacked;
        public double LabelFont, Width, Height, ContentHeight, EvidenceHeader, X, Y;
        public int Depth;
        public List<string> LabelLines = [], SublabelLines = [];
        public double? ToggleAt;
    }

    sealed record Edge(string From, string To, int Index, List<Pt> Points);

    sealed class Scene
    {
        readonly RenderContext ctx;
        readonly JsonObject tree;
        readonly JsonObject meta;
        readonly string? locale;
        readonly string direction;
        readonly bool down;
        readonly double gapX, gapY, nodeW, nodeMaxW;
        readonly List<JsonObject> nodes;
        readonly string rootId;
        readonly JsMap<string, List<string>> childrenOf = new();
        readonly JsMap<string, int> depthOf = new();
        readonly JsMap<string, Group> stackOf = new();
        readonly JsMap<string, List<Group>> groupsOf = new();
        readonly JsMap<string, TreeNode> measured = new();
        readonly double[] bandSize;
        readonly double[] bandStart;
        readonly double breadthGap, depthGap;
        readonly JsMap<string, double> span = new();
        readonly JsMap<string, TreeNode> placed = new();
        readonly List<TreeNode> all;
        readonly double[] viewBox;
        readonly List<Edge> edges;
        readonly JsMap<string, string> hueOf = new();

        static string N(double v) => Js.Num(v);

        public Scene(RenderContext ctx)
        {
            this.ctx = ctx;
            tree = ctx.Diagram;
            meta = ctx.Meta!;
            locale = meta.Str("locale");
            var layout = tree["layout"] as JsonObject;
            direction = layout.Str("direction") == "right" ? "right" : "down";
            down = direction == "down";
            gapX = layout.Num("gapX") ?? (direction == "down" ? 28 : 48);
            gapY = layout.Num("gapY") ?? (direction == "down" ? 56 : 18);
            nodeW = layout.Num("nodeW") ?? 140;
            nodeMaxW = layout.Num("nodeMaxW") ?? (direction == "down" ? 220 : 200);

            nodes = Geometry.AsArray(tree["nodes"]).Select(node => (JsonObject)node!).ToList();
            rootId = ValidateStructure();

            foreach (var node in nodes) childrenOf.Set(Id(node), []);
            foreach (var node in nodes) if (node.Str("parent") is { } parent) childrenOf.Get(parent)!.Add(Id(node));

            AssignDepth(rootId, 0);

            // Compact leaf runs (going down only), :160-189.
            foreach (var (id, children) in childrenOf.Entries)
            {
                var groups = new List<Group>();
                var run = new List<string>();
                void Flush()
                {
                    if (down && run.Count >= STACK_MIN) groups.Add(new Group { Stack = run });
                    else foreach (var child in run) groups.Add(new Group { Single = child });
                    run = [];
                }
                foreach (var child in children)
                {
                    if (childrenOf.Get(child)!.Count > 0)
                    {
                        Flush();
                        groups.Add(new Group { Single = child });
                    }
                    else run.Add(child);
                }
                Flush();
                foreach (var group in groups)
                {
                    if (group.Stack is null) continue;
                    group.SpineOffset = groups.Count == 1 ? 22 : 12;
                    group.Indent = group.SpineOffset + 16;
                }
                groupsOf.Set(id, groups);
                foreach (var group in groups) if (group.Stack is not null) foreach (var child in group.Stack) stackOf.Set(child, group);
            }

            foreach (var node in nodes) measured.Set(Id(node), Measure(node));

            // A stacked run reads as one list: one width, one row height (:230). Each group touches only its own
            // members, so walking groupsOf visits them in the same first-seen order as `new Set(stackOf.values())`.
            foreach (var groups in groupsOf.Values)
            {
                foreach (var group in groups)
                {
                    if (group.Stack is null) continue;
                    var members = group.Stack.Select(id => measured.Get(id)!).ToList();
                    group.Width = MaxOf(members.Select(node => node.Width));
                    group.RowH = MaxOf(members.Select(node => node.Height));
                    foreach (var node in members) { node.Width = group.Width; node.Height = group.RowH; }
                }
            }

            // ---- Layout (:245-311) ----
            breadthGap = down ? gapX : gapY;
            depthGap = down ? gapY : gapX;
            var maxDepth = (int)MaxOf(measured.Values.Select(node => (double)node.Depth));
            bandSize = new double[maxDepth + 1];
            foreach (var node in measured.Values) if (!node.Stacked) bandSize[node.Depth] = Math.Max(bandSize[node.Depth], DepthSize(node));
            bandStart = new double[maxDepth + 1];
            var offset = down ? Layout.Top : Layout.Margin;
            for (var depth = 0; depth < bandSize.Length; depth++)
            {
                bandStart[depth] = offset;
                offset = offset + bandSize[depth] + depthGap;
            }
            foreach (var node in measured.Values)
            {
                if (node.Stacked) continue;
                if (down) node.Height = bandSize[node.Depth];
                else node.Width = bandSize[node.Depth];
            }

            MeasureSpan(rootId);
            Place(rootId, down ? Layout.Margin : Layout.Top);

            all = nodes.Select(node => placed.Get(Id(node))!).ToList();
            viewBox = meta["viewBox"] is JsonArray authored ? [Elem(authored, 0), Elem(authored, 1)] :
            [
                Math.Max(320, Math.Ceiling(MaxOf(all.Select(node => node.X + node.Width)) + Layout.Margin + (down ? 0 : Layout.ToggleR + 4))),
                Math.Max(240, Math.Ceiling(MaxOf(all.Select(node => node.Y + node.Height)) + Layout.Margin + (down ? Layout.ToggleR + 4 : 0))),
            ];

            edges = all.Where(node => node.Parent is not null)
                .Select((node, index) => new Edge(node.Parent!, node.Id, index, EdgePoints(placed.Get(node.Parent!)!, node))).ToList();

            // ---- Canvas checks (:380-390) ----
            var problems = new List<string>();
            if (meta["viewBox"] is JsonArray authoredBox)
            {
                foreach (var node in all)
                {
                    if (node.X + node.Width + Layout.Margin / 2 > viewBox[0] || node.Y + node.Height + Layout.Margin / 2 > viewBox[1])
                    {
                        problems.Add($"Node \"{node.Id}\" lies outside the authored meta.viewBox {string.Join("x", authoredBox.Select(JoinPart))} — remove meta.viewBox or enlarge it.");
                    }
                }
            }
            if (problems.Count > 0)
            {
                throw ctx.Diagnostics.ThrowDiagnosticProblems("Tree layout validation failed", problems, "layout/constraint", new JsonObject { ["diagramType"] = "tree" });
            }

            var rootChildren = childrenOf.Get(rootId)!;
            for (var index = 0; index < rootChildren.Count; index++) Paint(rootChildren[index], SUBTREE_HUES[index % SUBTREE_HUES.Length]);
        }

        static string Id(JsonObject node) => node.Str("id")!;
        static double Elem(JsonArray a, int i) => i < a.Count && a[i] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.D() : double.NaN;
        // Array.prototype.join: null/undefined become "", numbers print as JS numbers.
        static string JoinPart(JsonNode? v) => v is null ? "" : v is JsonValue jv && jv.GetValueKind() == JsonValueKind.Number ? Js.Num(jv.D()) : v.Str0() ?? Js.Stringify(v);
        // Math.max(...xs): -Infinity for none.
        static double MaxOf(IEnumerable<double> values)
        {
            var max = double.NegativeInfinity;
            foreach (var value in values)
            {
                if (double.IsNaN(value)) return double.NaN;
                max = Math.Max(max, value);
            }
            return max;
        }

        // ---- Structure (:55-111) -----------------------------------------------------------
        string ValidateStructure()
        {
            var problems = new List<string>();
            var details = new List<Diagnostic>();
            void Fail(string code, string message, JsonObject subject, JsonObject evidence, string[] supportedFixes)
            {
                problems.Add(message);
                var full = new JsonObject { ["diagramType"] = "tree" };
                foreach (var (key, value) in subject) full[key] = value?.DeepClone();
                details.Add(new Diagnostic(code, Severity.Error, message, full, evidence, supportedFixes, null));
            }
            var byId = new JsMap<string, JsonObject>();
            for (var index = 0; index < nodes.Count; index++)
            {
                var node = nodes[index];
                var id = Id(node);
                if (byId.Has(id))
                {
                    Fail("tree/duplicate-id", $"Node id \"{id}\" is declared twice.", new JsonObject { ["path"] = $"/nodes/{N(index)}/id" },
                        new JsonObject { ["id"] = id }, ["Give every node a unique id."]);
                }
                byId.Set(id, node);
            }
            for (var index = 0; index < nodes.Count; index++)
            {
                var node = nodes[index];
                if (node.Str("parent") is { } parent && !byId.Has(parent))
                {
                    Fail("tree/missing-parent", $"Node \"{Id(node)}\" names parent \"{parent}\", which is not declared.",
                        new JsonObject { ["path"] = $"/nodes/{N(index)}/parent", ["nodeId"] = Id(node) }, new JsonObject { ["parent"] = parent },
                        [$"Declare a node with id \"{parent}\".", "Point parent at an existing node.", "Remove parent to make this the root."]);
                }
            }
            var roots = nodes.Where(node => node.Str("parent") is null).ToList();
            if (roots.Count != 1)
            {
                Fail("tree/root-count", roots.Count > 0
                        ? $"A tree has one root, but {Js.Num(roots.Count)} nodes have no parent: {string.Join(", ", roots.Select(node => $"\"{Id(node)}\""))}."
                        : "A tree has one root, but every node names a parent.",
                    new JsonObject { ["path"] = "/nodes" }, new JsonObject { ["roots"] = new JsonArray([.. roots.Select(node => (JsonNode?)Id(node))]) },
                    roots.Count > 0 ? ["Give all but one of these nodes a parent.", "Add a single root that the top-level nodes name as parent."] : ["Remove parent from the root node."]);
            }
            // Walk each node's parent chain; the only way a node can fail to reach the root is a cycle.
            var reported = new JsSet<string>();
            for (var index = 0; index < nodes.Count; index++)
            {
                var node = nodes[index];
                var chain = new List<string>();
                JsonObject? current = node;
                while (current is not null && current.Str("parent") is { } parent && !chain.Contains(Id(current)))
                {
                    chain.Add(Id(current));
                    current = byId.Get(parent);
                }
                if (current is not null && chain.Contains(Id(current)))
                {
                    var cycle = chain.Skip(chain.IndexOf(Id(current))).ToList();
                    var sorted = cycle.ToList();
                    Js.SortDefault(sorted);
                    var key = string.Join("|", sorted);
                    if (!reported.Has(key))
                    {
                        reported.Add(key);
                        Fail("tree/cycle", $"Parent links form a cycle: {string.Join(" -> ", cycle.Append(cycle[0]).Select(id => $"\"{id}\""))}.",
                            new JsonObject { ["path"] = $"/nodes/{N(index)}/parent", ["nodeId"] = Id(node) },
                            new JsonObject { ["cycle"] = new JsonArray([.. cycle.Select(id => (JsonNode?)id)]) },
                            ["Point one node in the cycle at a parent outside it."]);
                    }
                }
            }
            if (problems.Count > 0)
            {
                throw ctx.Diagnostics.ThrowDiagnosticProblems("Tree structure validation failed", problems, "tree/structure",
                    new JsonObject { ["diagramType"] = "tree" }, details);
            }
            return Id(roots[0]);
        }

        void AssignDepth(string id, int depth)
        {
            depthOf.Set(id, depth);
            foreach (var child in childrenOf.Get(id)!) AssignDepth(child, depth + 1);
        }

        // ---- Measurement (:121-227) --------------------------------------------------------
        static List<string> WrapText(string text, double font, double maxWidth)
        {
            var limit = Math.Max(4, Math.Floor(maxWidth / (font * ADVANCE)));
            if (Utils.TextUnits(text) <= limit) return [text];
            var tokens = Js.CjkWrapTokens(text);
            var lines = new List<string>();
            var current = "";
            foreach (var token in tokens)
            {
                if (Utils.TextUnits(Js.TrimEnd(current + token)) <= limit)
                {
                    current += token;
                    continue;
                }
                if (Js.Trim(current).Length > 0) lines.Add(Js.TrimEnd(current));
                current = Js.TrimStart(token);
                while (Utils.TextUnits(Js.TrimEnd(current)) > limit)
                {
                    var chars = Js.CodePoints(current);
                    var cut = 0;
                    for (var units = 0; cut < chars.Count && units + Utils.TextUnits(chars[cut]) <= limit; cut += 1) units += Utils.TextUnits(chars[cut]);
                    var at = Math.Max(1, cut);
                    lines.Add(string.Concat(chars.Take(at)));
                    current = string.Concat(chars.Skip(at));
                }
            }
            if (Js.Trim(current).Length > 0) lines.Add(Js.TrimEnd(current));
            return lines;
        }

        TreeNode Measure(JsonObject node)
        {
            var id = Id(node);
            var label = node.Str("label")!;
            var sublabel = node.Str("sublabel");
            var hasSublabel = !string.IsNullOrEmpty(sublabel);
            var isRoot = id == rootId;
            var labelFont = isRoot ? Layout.RootLabelFont : Layout.LabelFont;
            var labelAdvance = labelFont * ADVANCE * 1.06;
            // Text wraps between words, never inside one: the longest single word may widen a node past nodeMaxW.
            var longestWord = MaxOf(Js.SplitOnSpaces(label).Select(word => Utils.TextUnits(word) * labelAdvance));
            var floor = Math.Max(nodeW, Math.Ceiling(longestWord + Layout.PadX * 2 + 2));
            var cap = Math.Ceiling(Math.Min(nodeMaxW * 1.5, Math.Max(floor, nodeMaxW)));
            (List<string> Label, List<string> Sublabel) LinesAt(double width) => (
                WrapText(label, labelFont * 1.06, width - Layout.PadX * 2),
                hasSublabel ? WrapText(sublabel!, Layout.SublabelFont, width - Layout.PadX * 2) : []);
            var natural = Math.Ceiling(Math.Max(Utils.TextUnits(label) * labelAdvance, hasSublabel ? Utils.TextUnits(sublabel) * Layout.SublabelFont * ADVANCE : 0) + Layout.PadX * 2 + 2);
            // A stacked list shares one width anyway, so its entries are not balanced.
            var stacked = stackOf.Has(id);
            var width = Math.Min(cap, Math.Max(floor, natural));
            var target = LinesAt(width);
            for (var candidate = floor; !stacked && candidate < width; candidate += 4)
            {
                var lines = LinesAt(candidate);
                if (lines.Label.Count <= target.Label.Count && lines.Sublabel.Count <= target.Sublabel.Count)
                {
                    width = candidate;
                    break;
                }
            }
            var (labelLines, sublabelLines) = LinesAt(width);
            var contentHeight = labelLines.Count * Layout.LabelLine + (sublabelLines.Count > 0 ? 4 + sublabelLines.Count * Layout.SublabelLine : 0);
            // Verified source badges occupy the top-right 18px of a card.
            var evidenceHeader = (ctx.SourceEvidence.Obj("nodes").Arr(id)?.Count ?? 0) > 0 ? 18 : 0;
            return new TreeNode
            {
                Source = node, Id = id, Parent = node.Str("parent"), Label = label, IsRoot = isRoot, LabelFont = labelFont,
                LabelLines = labelLines, SublabelLines = sublabelLines, Width = width,
                Height = Math.Ceiling(Layout.PadY * 2 + evidenceHeader + contentHeight), ContentHeight = contentHeight,
                EvidenceHeader = evidenceHeader, Depth = depthOf.Get(id), Stacked = stacked,
            };
        }

        // ---- Layout ----------------------------------------------------------------------
        double BreadthSize(TreeNode node) => down ? node.Width : node.Height;
        double DepthSize(TreeNode node) => down ? node.Height : node.Width;

        double GroupSpan(Group group) => group.Stack is not null ? group.Indent + group.Width : span.Get(group.Single!);
        double GroupsSpan(List<Group> groups)
        {
            double sum = 0;
            for (var index = 0; index < groups.Count; index++) sum = sum + GroupSpan(groups[index]) + (index > 0 ? breadthGap : 0);
            return sum;
        }

        void MeasureSpan(string id)
        {
            foreach (var child in childrenOf.Get(id)!) MeasureSpan(child);
            span.Set(id, Math.Max(BreadthSize(measured.Get(id)!), GroupsSpan(groupsOf.Get(id)!)));
        }

        // Where a group meets its parent's crossbar: a single child at its centre, a stacked run at its spine.
        double AttachOf(Group group) => group.Stack is not null ? group.Spine : CenterOf(group.Single!);

        double CenterOf(string id)
        {
            var node = placed.Get(id)!;
            return down ? node.X + node.Width / 2 : node.Y + node.Height / 2;
        }

        void Place(string id, double start)
        {
            var node = measured.Get(id)!;
            var groups = groupsOf.Get(id)!;
            var onlyStack = groups.Count == 1 && groups[0].Stack is not null;
            var cursor = onlyStack ? start : start + (span.Get(id) - GroupsSpan(groups)) / 2;
            foreach (var group in groups)
            {
                if (group.Stack is not null)
                {
                    group.Spine = cursor + group.SpineOffset;
                    var y = bandStart[node.Depth + 1];
                    foreach (var child in group.Stack)
                    {
                        var leaf = measured.Get(child)!;
                        leaf.X = Js.Round(cursor + group.Indent);
                        leaf.Y = Js.Round(y);
                        placed.Set(child, leaf);
                        y += leaf.Height + STACK_GAP;
                    }
                }
                else Place(group.Single!, cursor);
                cursor += GroupSpan(group) + breadthGap;
            }
            var center = groups.Count > 0 ? (AttachOf(groups[0]) + AttachOf(groups[^1])) / 2 : start + span.Get(id) / 2;
            // A middle child a few px off the midpoint would put a small jog in the stem; the parent sits over it instead.
            var half = BreadthSize(node) / 2;
            foreach (var group in groups.Skip(1).Take(Math.Max(0, groups.Count - 2)))
            {
                var attach = AttachOf(group);
                if (Math.Abs(attach - center) < 24 && attach - half >= start && attach + half <= start + span.Get(id))
                {
                    center = attach;
                    break;
                }
            }
            var breadth = onlyStack ? start : center - BreadthSize(node) / 2;
            // A row going down centres each node in its band; a column going right left-aligns them.
            var depth = bandStart[node.Depth] + (down ? (bandSize[node.Depth] - DepthSize(node)) / 2 : 0);
            var (x, yy) = down ? (breadth, depth) : (depth, breadth);
            node.X = Js.Round(x);
            node.Y = Js.Round(yy);
            node.ToggleAt = onlyStack ? Js.Round(groups[0].Spine) : null;
            placed.Set(id, node);
        }

        // ---- Edges (:329-367) ---------------------------------------------------------------
        Pt EdgeStart(TreeNode parent)
        {
            if (!down) return new Pt(parent.X + parent.Width, parent.Y + parent.Height / 2);
            return new Pt(parent.ToggleAt ?? parent.X + parent.Width / 2, parent.Y + parent.Height);
        }

        List<Pt> EdgePoints(TreeNode parent, TreeNode child)
        {
            var start = EdgeStart(parent);
            if (down && child.Stacked)
            {
                var spine = stackOf.Get(child.Id)!.Spine;
                var stackEnd = new Pt(child.X, child.Y + child.Height / 2);
                if (parent.ToggleAt is not null) return [start, new(spine, stackEnd.Y), stackEnd];
                var stackMidY = bandStart[child.Depth] - depthGap / 2;
                return [start, new(start.X, stackMidY), new(spine, stackMidY), new(spine, stackEnd.Y), stackEnd];
            }
            if (down)
            {
                var downEnd = new Pt(child.X + child.Width / 2, child.Y);
                var midY = bandStart[child.Depth] - depthGap / 2;
                // Rounded parent/child centres can differ by ~1px; snap near-aligned stems to a shared x.
                if (Math.Abs(start.X - downEnd.X) < 2)
                {
                    var x = Js.Round((start.X + downEnd.X) / 2);
                    return [new(x, start.Y), new(x, downEnd.Y)];
                }
                return [start, new(start.X, midY), new(downEnd.X, midY), downEnd];
            }
            var end = new Pt(child.X, child.Y + child.Height / 2);
            var midX = bandStart[child.Depth] - depthGap / 2;
            if (Math.Abs(start.Y - end.Y) < 2)
            {
                var y = Js.Round((start.Y + end.Y) / 2);
                return [new(start.X, y), new(end.X, y)];
            }
            return [start, new(midX, start.Y), new(midX, end.Y), end];
        }

        List<string> AncestorsOf(string id)
        {
            var chain = new List<string>();
            for (var current = placed.Get(id); current?.Parent is not null; current = placed.Get(current.Parent)) chain.Add(current.Parent);
            return chain;
        }

        int DescendantCount(string id) => childrenOf.Get(id)!.Aggregate(0, (sum, child) => sum + 1 + DescendantCount(child));

        public JsonObject BuildLayoutReport() => new()
        {
            ["diagram_type"] = "tree",
            ["direction"] = direction,
            ["viewBox"] = meta["viewBox"] is JsonArray authored ? authored.DeepClone() : new JsonArray(viewBox[0], viewBox[1]),
            ["nodes"] = new JsonArray([.. all.Select(node => (JsonNode?)new JsonObject
            {
                ["id"] = node.Id,
                ["parent"] = node.Parent,
                ["depth"] = node.Depth,
                ["x"] = node.X,
                ["y"] = node.Y,
                ["width"] = node.Width,
                ["height"] = node.Height,
                ["lines"] = node.LabelLines.Count,
                ["stacked"] = node.Stacked,
            })]),
            ["edges"] = new JsonArray([.. edges.Select(edge => (JsonNode?)new JsonObject
            {
                ["from"] = edge.From,
                ["to"] = edge.To,
                ["points"] = new JsonArray([.. edge.Points.Select(p => (JsonNode?)new JsonArray(p.X, p.Y))]),
            })]),
        };

        // ---- Rendering (:403-559) --------------------------------------------------------------
        string TreeAttrs(string id)
        {
            var ancestors = AncestorsOf(id);
            return ancestors.Count > 0 ? $" data-tree-ancestors=\"{Utils.Esc(string.Join(" ", ancestors))}\"" : "";
        }

        void Paint(string current, string hue)
        {
            hueOf.Set(current, hue);
            foreach (var child in childrenOf.Get(current)!) Paint(child, hue);
        }

        string HueClass(string id) => hueOf.Has(id) ? $"tree-hue-{hueOf.Get(id)}" : "tree-hue-root";

        static string StripTrailing(string s) => Utils.TrailingBlanks.Replace(s, "");

        string RenderNode(TreeNode node)
        {
            var leaf = childrenOf.Get(node.Id)!.Count == 0;
            var tone = node.IsRoot ? "frontend" : leaf ? "external" : "backend";
            var role = node.IsRoot ? "tree-root" : leaf ? "tree-leaf" : "tree-branch";
            var context = ctx.Locale.Translate(locale, node.IsRoot ? "node.context.tree.root" : childrenOf.Get(node.Id)!.Count > 0 ? "node.context.tree.branch" : "node.context.tree.leaf");
            var passport = new JsonObject { ["kind"] = tone };
            if (node.Source["sublabel"] is { } sublabelNode) passport["sublabel"] = sublabelNode.DeepClone();
            passport["context"] = context;
            // A stacked list reads left-aligned, like a directory listing; a node in a row is centred under its link.
            var cx = node.Stacked ? node.X + Layout.PadX + 2 : node.X + node.Width / 2;
            var anchor = node.Stacked ? "start" : "middle";
            var y = node.Y + node.EvidenceHeader + (node.Height - node.EvidenceHeader - node.ContentHeight) / 2;
            var label = new StringBuilder();
            foreach (var line in node.LabelLines)
            {
                y += Layout.LabelLine;
                label.Append($"<tspan x=\"{N(cx)}\" y=\"{N(y - 4.5)}\">{Utils.Esc(line)}</tspan>");
            }
            y += node.SublabelLines.Count > 0 ? 4 : 0;
            var sublabel = new StringBuilder();
            foreach (var line in node.SublabelLines)
            {
                y += Layout.SublabelLine;
                sublabel.Append($"<tspan x=\"{N(cx)}\" y=\"{N(y - 3.5)}\">{Utils.Esc(line)}</tspan>");
            }
            // A collapsed branch shows a stacked card behind it.
            var stack = childrenOf.Get(node.Id)!.Count > 0
                ? $"<rect data-tree-stack=\"\" x=\"{N(node.X + 5)}\" y=\"{N(node.Y + 5)}\" width=\"{N(node.Width)}\" height=\"{N(node.Height)}\" rx=\"10\" class=\"tree-stack-{tone}\" stroke-width=\"1.2\"/>"
                : "";
            var accent = leaf && node.Stacked
                ? $"<path class=\"tree-accent\" d=\"M {N(node.X + 4.5)} {N(node.Y + 9)} V {N(node.Y + node.Height - 9)}\" stroke-width=\"3\" stroke-linecap=\"round\" fill=\"none\"/>"
                : "";
            var (sx, sy) = node.Stacked || !down ? (node.X, node.Y + node.Height / 2) : (node.X + node.Width / 2, node.Y);
            var socket = node.IsRoot ? "" : $"<circle cx=\"{N(sx)}\" cy=\"{N(sy)}\" r=\"2.6\" class=\"tree-socket\" stroke-width=\"1.4\"/>";
            var sublabelText = sublabel.Length > 0
                ? $"<text data-detail=\"context\" x=\"{N(cx)}\" class=\"t-muted tree-sublabel\" font-size=\"{N(Layout.SublabelFont)}\" text-anchor=\"{anchor}\">{sublabel}</text>"
                : "";
            return StripTrailing(
                $"        <g {SvgAttrs.FocusNodeAttrs(node.Id, node.Label, passport, locale, ctx)} data-tree-depth=\"{N(node.Depth)}\"{TreeAttrs(node.Id)} class=\"{HueClass(node.Id)}\">\n"
                + $"          {SvgAttrs.FocusNodeTitle(node.Label, passport)}\n"
                + $"          {stack}\n"
                + $"          <rect x=\"{N(node.X)}\" y=\"{N(node.Y)}\" width=\"{N(node.Width)}\" height=\"{N(node.Height)}\" rx=\"10\" class=\"c-mask\"/>\n"
                + $"          <rect x=\"{N(node.X)}\" y=\"{N(node.Y)}\" width=\"{N(node.Width)}\" height=\"{N(node.Height)}\" rx=\"10\" class=\"c-{tone} {role}\"{SvgAttrs.AnimateAttr(meta, "node", node.Depth)} stroke-width=\"{N(node.IsRoot ? 0 : leaf ? 1 : 1.2)}\"/>\n"
                + $"          {accent}\n"
                + $"          {socket}\n"
                + $"          <text data-node-label=\"\" x=\"{N(cx)}\" class=\"t-primary tree-label\" font-size=\"{N(node.LabelFont)}\" font-weight=\"{N(node.IsRoot ? 750 : leaf ? 560 : 700)}\" text-anchor=\"{anchor}\">{label}</text>\n"
                + $"          {sublabelText}\n"
                + "        </g>");
        }

        // The toggle is a separate control after its node, not nested inside it.
        string RenderToggle(TreeNode node)
        {
            var children = childrenOf.Get(node.Id)!;
            if (children.Count == 0) return "";
            var (cx, cy) = EdgeStart(node);
            var hidden = DescendantCount(node.Id);
            var values = new Dictionary<string, string> { ["label"] = node.Label, ["count"] = Js.Num(hidden) };   // lookup only
            var collapse = ctx.Locale.Translate(locale, "tree.toggle.collapse", values);
            var expand = ctx.Locale.Translate(locale, "tree.toggle.expand", values);
            const double r = Layout.ToggleR;
            const double arm = r * 0.42;
            var digits = Js.Num(hidden).Length;
            return StripTrailing(
                $"        <g data-tree-toggle=\"{Utils.Esc(node.Id)}\" data-tree-count=\"{N(hidden)}\"{TreeAttrs(node.Id)}{(node.Source["collapsed"].Truthy() ? " data-tree-initially-collapsed=\"\"" : "")} data-tree-label-collapse=\"{Utils.Esc(collapse)}\" data-tree-label-expand=\"{Utils.Esc(expand)}\" role=\"button\" tabindex=\"0\" aria-expanded=\"true\" aria-label=\"{Utils.Esc(collapse)}\" class=\"{HueClass(node.Id)}\">\n"
                + $"          <circle cx=\"{N(cx)}\" cy=\"{N(cy)}\" r=\"{N(r + 7)}\" class=\"tree-toggle-hit\"/>\n"
                + $"          <circle cx=\"{N(cx)}\" cy=\"{N(cy)}\" r=\"{N(r)}\" class=\"tree-toggle-disc\" stroke-width=\"1.2\"/>\n"
                + $"          <path class=\"tree-toggle-glyph\" d=\"M {N(cx - arm)} {N(cy)} H {N(cx + arm)}\" stroke-width=\"1.5\" stroke-linecap=\"round\"/>\n"
                + $"          <path class=\"tree-toggle-glyph tree-toggle-plus\" d=\"M {N(cx)} {N(cy - arm)} V {N(cy + arm)}\" stroke-width=\"1.5\" stroke-linecap=\"round\"/>\n"
                + "          <g class=\"tree-toggle-count\">\n"
                + $"            <rect x=\"{N(cx + r + 3)}\" y=\"{N(cy - 7.5)}\" width=\"{N(digits * 6.2 + 11)}\" height=\"15\" rx=\"7.5\" class=\"tree-count-pill\"/>\n"
                + $"            <text x=\"{N(cx + r + 3 + (digits * 6.2 + 11) / 2)}\" y=\"{N(cy + 3.6)}\" class=\"t-primary\" font-size=\"10\" font-weight=\"700\" text-anchor=\"middle\">{N(hidden)}</text>\n"
                + "          </g>\n"
                + "        </g>");
        }

        string RenderEdge(Edge edge)
        {
            // Links out of the root share one trunk, so they stay neutral; inside a subtree every link shares its branch's hue.
            var hued = edge.From != rootId;
            var d = Geometry.RoundedPath(edge.Points, 10);
            return $"        <path {SvgAttrs.FocusEdgeAttrs(edge.From, edge.To, null, N(edge.Index), null)}{TreeAttrs(edge.To)} data-composition-points=\"{Geometry.RoutePointsValue(edge.Points)}\" d=\"{d}\" class=\"a-default tree-edge{(hued ? $" tree-edge-hued {HueClass(edge.To)}" : " tree-edge-trunk")}\"{SvgAttrs.AnimateAttr(meta, "edge", placed.Get(edge.To)!.Depth)} stroke-width=\"{N(hued ? 1.5 : 1.8)}\"/>";
        }

        public string RenderSvg()
        {
            var readerFit = meta["viewBox"].Truthy() ? "" : " data-reader-fit=\"intrinsic-height\" data-reader-min-text=\"7.5\"";
            var expandAll = Utils.Esc(ctx.Locale.Translate(locale, "tree.expandAll"));
            var sb = new StringBuilder();
            sb.Append($"      <svg viewBox=\"0 0 {N(viewBox[0])} {N(viewBox[1])}\" {SvgAttrs.SvgRootAttrs(ctx)} data-tree-ui=\"\" data-tree-direction=\"{direction}\"{readerFit}>\n");
            sb.Append(SvgAttrs.SvgAccessibleText(ctx, "tree")).Append('\n');
            sb.Append(Utils.RenderDefinitions()).Append('\n');
            sb.Append("        <style>\n");
            sb.Append(string.Join("\n", new[] { "root" }.Concat(SUBTREE_HUES).Select(hue => hue == "root"
                ? "          svg[data-tree-ui] .tree-hue-root { --tree-hue: var(--text-muted); --tree-wash: transparent; }"
                : $"          svg[data-tree-ui] .tree-hue-{hue} {{ --tree-hue: var(--{hue}-stroke); --tree-wash: var(--{hue}-fill); }}"))).Append('\n');
            sb.Append("          svg[data-tree-ui] .tree-edge { stroke-linejoin: round; stroke-linecap: round; }\n");
            sb.Append("          svg[data-tree-ui] .tree-edge-trunk { stroke: color-mix(in srgb, var(--text) 18%, var(--arrow)); }\n");
            sb.Append("          svg[data-tree-ui] .tree-edge-hued { stroke: color-mix(in srgb, var(--tree-hue) 42%, var(--arrow)); }\n");
            sb.Append("          svg[data-tree-ui] .tree-root { fill: var(--text); stroke: none; }\n");
            sb.Append("          svg[data-tree-ui] [data-tree-depth=\"0\"] .tree-label { fill: var(--mask); }\n");
            sb.Append("          svg[data-tree-ui] [data-tree-depth=\"0\"] .tree-sublabel { fill: var(--mask); opacity: 0.68; }\n");
            sb.Append("          svg[data-tree-ui] .tree-branch { fill: var(--tree-wash); stroke: color-mix(in srgb, var(--tree-hue) 48%, var(--lane-stroke)); }\n");
            sb.Append("          svg[data-tree-ui] .tree-leaf { fill: var(--mask); stroke: color-mix(in srgb, var(--tree-hue) 22%, var(--lane-stroke)); }\n");
            sb.Append("          svg[data-tree-ui] .tree-socket { fill: var(--mask); stroke: var(--tree-hue); pointer-events: none; }\n");
            sb.Append("          svg[data-tree-ui] .tree-accent { stroke: var(--tree-hue); pointer-events: none; }\n");
            sb.Append("          svg[data-tree-ui] [data-tree-stack] { display: none; fill: var(--mask); stroke: color-mix(in srgb, var(--tree-hue) 70%, var(--lane-stroke)); }\n");
            sb.Append("          svg[data-tree-ui] [data-tree-collapsed] [data-tree-stack] { display: inline; }\n");
            sb.Append("          svg[data-tree-ui] [data-tree-hidden] { display: none; }\n");
            sb.Append("          svg[data-tree-ui] .tree-toggle-hit { fill: transparent; }\n");
            sb.Append("          svg[data-tree-ui] .tree-toggle-disc { fill: var(--mask); stroke: color-mix(in srgb, var(--tree-hue) 55%, var(--lane-stroke)); transition: stroke 0.15s ease, fill 0.15s ease; }\n");
            sb.Append("          svg[data-tree-ui] .tree-toggle-glyph { stroke: var(--text-muted); fill: none; transition: stroke 0.15s ease; }\n");
            sb.Append("          svg[data-tree-ui] .tree-toggle-count { display: none; }\n");
            sb.Append("          svg[data-tree-ui] .tree-count-pill { fill: var(--tree-hue); stroke: none; }\n");
            sb.Append("          svg[data-tree-ui] .tree-toggle-count text { fill: var(--mask); }\n");
            sb.Append("          svg[data-tree-ui] [data-tree-toggle] { cursor: pointer; outline: none; }\n");
            sb.Append("          svg[data-tree-ui] [data-tree-toggle][aria-expanded=\"true\"] .tree-toggle-plus { display: none; }\n");
            sb.Append("          svg[data-tree-ui] [data-tree-toggle][aria-expanded=\"false\"] .tree-toggle-count { display: inline; }\n");
            sb.Append("          svg[data-tree-ui] [data-tree-toggle][aria-expanded=\"false\"] .tree-toggle-disc { fill: var(--tree-hue); stroke: var(--tree-hue); }\n");
            sb.Append("          svg[data-tree-ui] [data-tree-toggle][aria-expanded=\"false\"] .tree-toggle-glyph { stroke: var(--mask); }\n");
            sb.Append("          svg[data-tree-ui] [data-tree-toggle]:hover .tree-toggle-disc,\n");
            sb.Append("          svg[data-tree-ui] [data-tree-toggle]:focus-visible .tree-toggle-disc { stroke: var(--tree-hue); stroke-width: 2; }\n");
            sb.Append("          svg[data-tree-ui] [data-tree-toggle]:hover .tree-toggle-glyph,\n");
            sb.Append("          svg[data-tree-ui] [data-tree-toggle]:focus-visible .tree-toggle-glyph { stroke: var(--text); }\n");
            sb.Append("          svg[data-tree-ui] [data-tree-expand-all] { cursor: pointer; outline: none; }\n");
            sb.Append("          svg[data-tree-ui] [data-tree-expand-all] rect { fill: var(--mask); stroke: var(--lane-stroke); }\n");
            sb.Append("          svg[data-tree-ui] [data-tree-expand-all]:is(:hover, :focus-visible) rect { stroke: var(--arrow-emphasis); stroke-width: 2; }\n");
            sb.Append("          svg[data-tree-ui]:not([data-tree-any-collapsed]) [data-tree-expand-all] { display: none; }\n");
            sb.Append("        </style>\n");
            sb.Append("\n");
            sb.Append("        <!-- Background Grid -->\n");
            sb.Append("        <rect width=\"100%\" height=\"100%\" fill=\"url(#grid)\" />\n");
            sb.Append("\n");
            sb.Append("        <!-- Containment links -->\n");
            sb.Append(string.Join("\n", edges.Select(RenderEdge))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Nodes -->\n");
            sb.Append(string.Join("\n", all.Select(RenderNode))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Branch toggles -->\n");
            sb.Append(string.Join("\n", all.Select(RenderToggle).Where(s => s.Length > 0))).Append('\n');
            sb.Append('\n');
            sb.Append("        <!-- Expand all (shown only while a branch is collapsed) -->\n");
            sb.Append($"        <g data-tree-expand-all=\"\" role=\"button\" tabindex=\"0\" aria-label=\"{expandAll}\">\n");
            sb.Append($"          <rect x=\"{N(Layout.Margin)}\" y=\"18\" width=\"{N(Math.Ceiling(Utils.TextUnits(expandAll) * 10.5 * ADVANCE + 24))}\" height=\"24\" rx=\"12\" stroke-width=\"1.2\"/>\n");
            sb.Append($"          <text x=\"{N(Layout.Margin + 12)}\" y=\"34\" class=\"t-primary\" font-size=\"10.5\" font-weight=\"650\">{expandAll}</text>\n");
            sb.Append("        </g>\n");
            sb.Append("      </svg>");
            return sb.ToString();
        }
    }
}
