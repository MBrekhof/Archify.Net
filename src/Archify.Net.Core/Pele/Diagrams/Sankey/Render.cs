using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/sankey/render.ts
    internal static partial class Sankey
    {
        const double LabelGap = 6;
        const double ValueGap = 15;

        static double NumberOption(JsonObject conf, string key, double fallback, double min, double max) =>
            conf[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.D() is var d && d >= min ? Math.Min(d, max) : fallback;

        // A string, or a finite number as String() writes it; anything else is ''.
        static string TextOption(JsonObject conf, string key) => conf[key] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v when v.GetValueKind() == JsonValueKind.Number && double.IsFinite(v.D()) => Js.Num(v.D()),
            _ => "",
        };

        static string? StringOption(JsonObject conf, string key) => conf[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        // Two decimals, as Mermaid shows values; numbers too large for that are left as they are.
        static double Rounded(double value) => Math.Abs(value) < 1e15 ? Js.Round(value * 100) / 100 : value;

        // A colour from the configuration, as an inline style that overrides the series colour.
        static string FillStyle(JsonNode? color) => color is JsonValue v && v.TryGetValue<string>(out var s) ? FillStyle(s) : "";
        static string FillStyle(string color) => Theme.ResolveStyle([$"fill:{color}"]).Shape;

        public static PeleRenderResult Render(SankeyDb model, JsonObject config, PeleRenderContext context)
        {
            var conf = config["sankey"] as JsonObject ?? [];
            return FitWidth.Fit(NumberOption(conf, "width", 600, 1, 10000), (chartWidth, _) => Draw(model, conf, context, chartWidth));
        }

        static PeleRenderResult Draw(SankeyDb model, JsonObject conf, PeleRenderContext context, double chartWidth)
        {
            var size = context.Options.FontSize;
            var small = Js.Round(size * 0.875);
            var pad = context.Options.Padding;

            var chartHeight = NumberOption(conf, "height", 400, 1, 10000);
            var nodeWidth = Math.Min(NumberOption(conf, "nodeWidth", 10, 1, 200), chartWidth);
            var showValues = !(conf["showValues"] is JsonValue sv && sv.GetValueKind() == JsonValueKind.False);
            var prefix = TextOption(conf, "prefix");
            var suffix = TextOption(conf, "suffix");
            var outlined = StringOption(conf, "labelStyle") == "outlined";
            var alignment = StringOption(conf, "nodeAlignment") is "left" or "right" or "center" ? StringOption(conf, "nodeAlignment")! : "justify";
            var linkColor = StringOption(conf, "linkColor") ?? "gradient";

            var (nodes, links) = LayoutSankey(model, new(chartWidth, chartHeight, nodeWidth,
                NumberOption(conf, "nodePadding", 12, 0, 200) + (showValues ? ValueGap : 0), small * 1.15, alignment));

            var custom = new Dictionary<string, string>(StringComparer.Ordinal);
            if (conf["nodeColors"] is JsonObject nodeColors)
            {
                foreach (var (id, color) in nodeColors)
                {
                    var style = FillStyle(color);
                    if (style != "") custom[id] = style;
                }
            }
            string NodeStyle(int i) => custom.GetValueOrDefault(model.Nodes[i].Id) ?? "";

            // Labels sit beside the bars, on the side that faces the middle of the chart.
            var central = 0;
            double most = 0;
            foreach (var node in nodes)
            {
                if (node.Value > most)
                {
                    most = node.Value;
                    central = node.Layer;
                }
            }
            double minX = 0, maxX = chartWidth, minY = 0, maxY = chartHeight;
            var labelsOut = new StringBuilder();
            var nodesOut = new StringBuilder();
            // Deviation (PORT-008 Task 11): upstream writes a label's name, prefix and suffix with esc(), so an entity such as
            // `&amp;` or `&euro;` shows as typed, while the band's <title>, the data-id and every other Pele label decode it. The
            // port decodes them in the label too, and measures the decoded text.
            var shownPrefix = Entities.DecodeEntities(prefix);
            var shownSuffix = Entities.DecodeEntities(suffix);
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                var id = model.Nodes[i].Id;
                var height = Math.Max(node.Height, 1);
                nodesOut.Append($"<rect class=\"pele-sankey-node\" data-id=\"{Builder.EscText(id)}\" x=\"{Builder.Num(node.X)}\" y=\"{Builder.Num(node.Y)}\" width=\"{Builder.Num(nodeWidth)}\" height=\"{Builder.Num(height)}\" rx=\"{Builder.Num(Math.Min(2, height / 2))}\" fill=\"{Theme.SeriesColor(i)}\"{NodeStyle(i)}/>");

                var shownId = Entities.DecodeEntities(id);
                var value = showValues ? $"{shownPrefix}{Js.Num(Rounded(node.Value))}{shownSuffix}" : "";
                var after = outlined ? node.Layer >= central : node.X < chartWidth / 2;
                var textWidth = Measurer.Width(value != "" ? $"{shownId} {value}" : shownId, small, Style.None);
                var x = after ? node.X + nodeWidth + LabelGap : node.X - LabelGap;
                var y = node.Y + node.Height / 2;
                minX = Math.Min(minX, after ? x : x - textWidth);
                maxX = Math.Max(maxX, after ? x + textWidth : x);
                minY = Math.Min(minY, y - small * 0.75);
                maxY = Math.Max(maxY, y + small * 0.75);
                var position = $" x=\"{Builder.Num(x)}\" y=\"{Builder.Num(y + small * 0.35)}\" text-anchor=\"{(after ? "start" : "end")}\"";
                var text = Builder.Esc(shownId) + (value != "" ? $" <tspan class=\"pele-sankey-value\" fill=\"var(--_m)\">{Builder.Esc(value)}</tspan>" : "");
                labelsOut.Append($"<g class=\"pele-label\" data-id=\"{Builder.EscText(id)}\">");
                if (outlined)
                    labelsOut.Append($"<text{position} fill=\"none\" stroke=\"var(--_bg)\" stroke-width=\"3\" stroke-linejoin=\"round\">{Builder.Esc(shownId)}{(value != "" ? " " + Builder.Esc(value) : "")}</text>");
                labelsOut.Append($"<text{position}>{text}</text></g>");
            }

            var fixedStyle = linkColor is "gradient" or "source" or "target" ? "" : FillStyle(linkColor);
            var linksOut = new StringBuilder();
            foreach (var link in links)
            {
                // Without definitions there are no gradients, so a gradient link takes its source's colour.
                var end = linkColor == "target" ? link.Target : link.Source;
                var half = Math.Max(link.Thickness, 1) / 2;
                var x0 = nodes[link.Source].X + nodeWidth;
                var x1 = nodes[link.Target].X;
                var mid = Builder.Num((x0 + x1) / 2);
                var from = Builder.EscText(model.Nodes[link.Source].Id);
                var to = Builder.EscText(model.Nodes[link.Target].Id);
                linksOut.Append($"<path class=\"pele-sankey-link\" data-id=\"{from}-&gt;{to}\" d=\"M{Builder.Num(x0)},{Builder.Num(link.SourceY - half)}C{mid},{Builder.Num(link.SourceY - half)} {mid},{Builder.Num(link.TargetY - half)} {Builder.Num(x1)},{Builder.Num(link.TargetY - half)}L{Builder.Num(x1)},{Builder.Num(link.TargetY + half)}C{mid},{Builder.Num(link.TargetY + half)} {mid},{Builder.Num(link.SourceY + half)} {Builder.Num(x0)},{Builder.Num(link.SourceY + half)}Z\" fill=\"{Theme.SeriesColor(end)}\"{(fixedStyle != "" ? fixedStyle : NodeStyle(end))}>")
                    .Append($"<title>{from} → {to}: {Builder.EscText(prefix)}{Js.Num(Rounded(link.Value))}{Builder.EscText(suffix)}</title></path>");
            }

            var title = Labels.LayoutLabel(model.Title, false, size, FitWidth.TitleRoom, (int)Style.Bold);
            var titleHeight = title.Height > 0 ? title.Height + 12 : 0;
            var inner = Math.Max(maxX - minX, title.Width);
            var ox = pad - minX + (inner - (maxX - minX)) / 2;
            var oy = pad + titleHeight - minY;
            var width = Math.Ceiling(inner + 2 * pad);
            var totalHeight = Math.Ceiling(maxY - minY + titleHeight + 2 * pad);

            var content =
                Builder.LabelSvg(title, width / 2, pad + title.Height / 2, " class=\"pele-title\" font-weight=\"var(--_tw)\"")
                + $"<g transform=\"translate({Builder.Num(ox)},{Builder.Num(oy)})\">"
                + $"<g class=\"pele-sankey-links\" fill-opacity=\"0.4\">{linksOut}</g>"
                + $"<g class=\"pele-sankey-nodes\">{nodesOut}</g>"
                + $"<g class=\"pele-labels\" font-size=\"{Js.Num(small)}\">{labelsOut}</g>"
                + "</g>";

            return new(Root.SvgDocument("sankey", width, totalHeight, size, context, model.AccTitle, model.AccDescr, content), width, totalHeight, "sankey");
        }
    }
}
