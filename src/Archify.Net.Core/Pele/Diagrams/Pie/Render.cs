using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/pie/render.ts
    internal static partial class Pie
    {
        const double Radius = 120;
        const double Swatch = 12;
        const double Row = 26;
        const double Gap = 32;
        const double Outside = 28;

        // (config.pie as Config | undefined)?.[key]: only an object has these keys; null stands for undefined and JSON null.
        static JsonNode? Option(JsonObject config, string key) => config["pie"] is JsonObject pie ? pie[key] : null;

        static bool IsNumber(JsonNode? node, out double value)
        {
            value = 0;
            if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return false;
            value = v.D();
            return true;
        }

        static string Point(double angle, double radius) =>
            $"{Builder.Num(JsMath.Sin(angle) * radius)},{Builder.Num(-JsMath.Cos(angle) * radius)}";

        static string Ring(double r, int sweep) =>
            $"M0,{Builder.Num(-r)}A{Builder.Num(r)},{Builder.Num(r)} 0 1 {sweep} 0,{Builder.Num(r)}A{Builder.Num(r)},{Builder.Num(r)} 0 1 {sweep} 0,{Builder.Num(-r)}Z";

        static string Slice(double start, double end, double outer, double inner)
        {
            if (end - start >= Math.PI * 2 - 1e-9) return Ring(outer, 1) + (inner > 0 ? Ring(inner, 0) : "");
            var large = end - start > Math.PI ? 1 : 0;
            if (inner <= 0) return $"M0,0L{Point(start, outer)}A{Builder.Num(outer)},{Builder.Num(outer)} 0 {large} 1 {Point(end, outer)}Z";
            return $"M{Point(start, outer)}A{Builder.Num(outer)},{Builder.Num(outer)} 0 {large} 1 {Point(end, outer)}"
                + $"L{Point(end, inner)}A{Builder.Num(inner)},{Builder.Num(inner)} 0 {large} 0 {Point(start, inner)}Z";
        }

        public static PeleRenderResult Render(PieModel model, JsonObject config, PeleRenderContext context) =>
            FitWidth.Fit(Radius * 2, (diameter, tight) => Draw(model, config, context, diameter / 2, tight));

        // `tight` is set when the chart does not fit the width it has with its legend beside it (never, for archify).
        static PeleRenderResult Draw(PieModel model, JsonObject config, PeleRenderContext context, double radius, bool tight)
        {
            var size = context.Options.FontSize;
            var small = Js.Round(size * 0.875);
            var pad = context.Options.Padding;

            var textPosition = IsNumber(Option(config, "textPosition"), out var position) && position >= 0 && position <= 1 ? position : 0.75;
            var inner = IsNumber(Option(config, "donutHole"), out var hole) && hole > 0 && hole <= 0.9 ? hole * radius : 0;
            var placed = Option(config, "legendPosition") is { } legendOption ? Js.String(legendOption) : "right";   // String(x ?? 'right')
            var legendPosition = tight && placed is "left" or "right" ? "bottom" : placed;

            var entries = model.Sections;
            double sum = 0;
            foreach (var (_, value) in entries) sum += value;

            var legend = entries.Select(entry => Labels.LayoutLabel(model.ShowData ? $"{entry.Label} [{Js.Num(entry.Value)}]" : entry.Label, false, small, 4000)).ToList();
            double legendWidth = 0;
            foreach (var label in legend) legendWidth = Math.Max(legendWidth, label.Width);
            legendWidth += legend.Count > 0 ? Swatch + 8 : 0;
            var legendHeight = legend.Count * Row;

            var title = Labels.LayoutLabel(model.Title, false, size, FitWidth.TitleRoom, (int)Style.Bold);
            var titleHeight = title.Height > 0 ? title.Height + 16 : 0;

            // A slice too thin for its percentage gets the label outside the rim, which needs a margin.
            var shares = entries.Select(entry =>
            {
                var share = sum > 0 ? entry.Value / sum : 0;
                return share * 100 >= 1 && Js.ToFixed(share * 100, 0) != "0" ? share : 0;
            }).ToList();
            var narrow = shares.Select(share =>
            {
                if (share == 0 || share >= 0.5) return false;
                var chord = 2 * radius * textPosition * JsMath.Sin(share * Math.PI);
                return chord < Measurer.Width(Js.ToFixed(share * 100, 0) + "%", small, Style.None) + 6;
            }).ToList();
            var margin = narrow.Contains(true) ? Outside : 0;

            var side = legendPosition is "left" or "right" or "center" ? legendPosition : "";
            var stacked = legendPosition is "top" or "bottom";
            var diameter = (radius + margin) * 2;
            var width = diameter;
            var height = diameter;
            if (side is "left" or "right")
            {
                width += legendWidth > 0 ? Gap + legendWidth : 0;
                height = Math.Max(height, legendHeight);
            }
            else if (stacked)
            {
                width = Math.Max(width, legendWidth);
                height += legendHeight > 0 ? 20 + legendHeight : 0;
            }
            width = Math.Max(width, title.Width);

            var cx = pad + (side == "left" ? legendWidth + Gap + diameter / 2 : side == "right" ? diameter / 2 : width / 2);
            var bodyTop = pad + titleHeight;
            var cy = bodyTop + (legendPosition == "top" ? legendHeight + 20 + diameter / 2 : stacked ? diameter / 2 : height / 2);

            var slices = new StringBuilder();
            var labels = new StringBuilder();
            var outside = new StringBuilder();
            double angle = 0;
            for (var index = 0; index < entries.Count; index++)
            {
                var share = shares[index];
                if (share == 0) continue;
                var end = angle + share * Math.PI * 2;
                slices.Append($"<path class=\"pele-slice\" data-id=\"{Builder.EscText(entries[index].Label)}\" d=\"{Slice(angle, end, radius, inner)}\" fill=\"{Theme.SeriesColor(index)}\"/>");
                var mid = (angle + end) / 2;
                var text = Js.ToFixed(share * 100, 0) + "%";
                var r = narrow[index] ? radius + Outside / 2 : radius * textPosition;
                (narrow[index] ? outside : labels).Append($"<text x=\"{Builder.Num(JsMath.Sin(mid) * r)}\" y=\"{Builder.Num(-JsMath.Cos(mid) * r + small * 0.35)}\">{text}</text>");
                angle = end;
            }
            if (slices.Length == 0) slices.Append($"<circle class=\"pele-slice\" r=\"{Js.Num(radius)}\" fill=\"none\" stroke=\"var(--_b)\"/>");

            double lx, ly;
            if (side == "right")
            {
                lx = cx + diameter / 2 + Gap;
                ly = bodyTop + (height - legendHeight) / 2;
            }
            else if (side == "left")
            {
                lx = pad;
                ly = bodyTop + (height - legendHeight) / 2;
            }
            else if (side == "center")
            {
                lx = cx - legendWidth / 2;
                ly = cy - legendHeight / 2;
            }
            else
            {
                lx = pad + (width - legendWidth) / 2;
                ly = legendPosition == "top" ? bodyTop : bodyTop + diameter + 20;
            }
            var legendOut = new StringBuilder();
            for (var index = 0; index < legend.Count; index++)
            {
                var label = legend[index];
                var y = ly + index * Row + Row / 2;
                legendOut.Append($"<g class=\"pele-legend-item\" data-id=\"{Builder.EscText(entries[index].Label)}\">")
                    .Append($"<rect x=\"{Builder.Num(lx)}\" y=\"{Builder.Num(y - Swatch / 2)}\" width=\"{Js.Num(Swatch)}\" height=\"{Js.Num(Swatch)}\" rx=\"2\" fill=\"{Theme.SeriesColor(index)}\"/>")
                    .Append(Builder.LabelSvg(label, lx + Swatch + 8 + label.Width / 2, y, ""))
                    .Append("</g>");
            }

            var totalWidth = Math.Ceiling(width + 2 * pad);
            var totalHeight = Math.Ceiling(height + titleHeight + 2 * pad);
            var transform = $"translate({Builder.Num(cx)},{Builder.Num(cy)})";
            var content =
                Builder.LabelSvg(title, totalWidth / 2, pad + title.Height / 2, " class=\"pele-title\" font-weight=\"var(--_tw)\"")
                + $"<g class=\"pele-slices\" transform=\"{transform}\" stroke=\"var(--_bg)\" stroke-width=\"1.5\" stroke-linejoin=\"round\">{slices}</g>"
                + (labels.Length > 0
                    ? $"<g class=\"pele-slice-labels\" transform=\"{transform}\" font-size=\"{Js.Num(small)}\" text-anchor=\"middle\" fill=\"var(--_bg)\">{labels}</g>"
                    : "")
                + (outside.Length > 0
                    ? $"<g class=\"pele-slice-labels\" transform=\"{transform}\" font-size=\"{Js.Num(small)}\" text-anchor=\"middle\">{outside}</g>"
                    : "")
                + (legendOut.Length > 0 ? $"<g class=\"pele-legend\" font-size=\"{Js.Num(small)}\">{legendOut}</g>" : "");

            return new(Root.SvgDocument("pie", totalWidth, totalHeight, size, context, model.AccTitle, model.AccDescr, content), totalWidth, totalHeight, "pie");
        }
    }
}
