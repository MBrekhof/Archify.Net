using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/radar/render.ts
    internal static partial class Radar
    {
        const double Swatch = 12;
        const double Row = 26;
        const double Gap = 24;

        // (config.radar as Config | undefined)?.[key]: a number at least `min`, capped at `max`; anything else is the fallback.
        static double Option(JsonObject config, string key, double fallback, double min, double max) =>
            config["radar"] is JsonObject radar && radar[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.D() is var d && d >= min
                ? Math.Min(d, max)
                : fallback;

        internal static double RelativeRadius(double value, double min, double max, double radius)
        {
            var clipped = Math.Min(Math.Max(value, min), max);
            // Deviation (PORT-008 Task 11, PORT-011): upstream multiplies first (render.ts:27), so near 1e308 the product is
            // Infinity and the point is drawn at the centre (render.ts:119). When it overflows (or max - min overflows) the port
            // divides first, halving when the span itself exceeds double.MaxValue; every other value keeps upstream's arithmetic.
            var scaled = radius * (clipped - min);
            var range = max - min;
            if (!double.IsInfinity(scaled) && !double.IsInfinity(range)) return scaled / range;
            return double.IsInfinity(range)
                ? radius * ((clipped * 0.5 - min * 0.5) / (max * 0.5 - min * 0.5))
                : radius * ((clipped - min) / range);
        }

        // A closed Catmull-Rom spline through the points, as cubic Bézier segments.
        static string ClosedRoundCurve(List<(double X, double Y)> points, double tension)
        {
            var n = points.Count;
            var d = new StringBuilder($"M{Builder.Num(points[0].X)},{Builder.Num(points[0].Y)}");
            for (var i = 0; i < n; i++)
            {
                var p0 = points[(i - 1 + n) % n];
                var p1 = points[i];
                var p2 = points[(i + 1) % n];
                var p3 = points[(i + 2) % n];
                d.Append($" C{Builder.Num(p1.X + (p2.X - p0.X) * tension)},{Builder.Num(p1.Y + (p2.Y - p0.Y) * tension)}")
                    .Append($" {Builder.Num(p2.X - (p3.X - p1.X) * tension)},{Builder.Num(p2.Y - (p3.Y - p1.Y) * tension)} {Builder.Num(p2.X)},{Builder.Num(p2.Y)}");
            }
            return d.Append(" Z").ToString();
        }

        static string Polygon(IEnumerable<(double X, double Y)> points)
        {
            var output = new StringBuilder();
            foreach (var p in points) output.Append(output.Length > 0 ? " " : "").Append(Builder.Num(p.X)).Append(',').Append(Builder.Num(p.Y));
            return output.ToString();
        }

        public static PeleRenderResult Render(RadarModel model, JsonObject config, PeleRenderContext context)
        {
            var natural = Option(config, "width", 320, 1, 4000);
            return FitWidth.Fit(natural, (width, tight) => Draw(model, config, context, width, Option(config, "height", 320, 1, 4000) * width / natural, tight));
        }

        // `tight` is set when the chart does not fit the width it has with its legend beside it (never, for archify).
        static PeleRenderResult Draw(RadarModel model, JsonObject config, PeleRenderContext context, double width, double height, bool tight)
        {
            var size = context.Options.FontSize;
            var small = Js.Round(size * 0.875);
            var pad = context.Options.Padding;

            var axisScale = Option(config, "axisScaleFactor", 1, 0, 10);
            var labelFactor = Option(config, "axisLabelFactor", 1.05, 0, 10);
            var tension = Option(config, "curveTension", 0.17, 0, 1);
            var radius = Math.Min(width, height) / 2;

            var axes = model.Axes;
            var curves = model.Curves;
            var (ticks, graticule, min) = (model.Options.Ticks, model.Options.Graticule, model.Options.Min);
            var count = axes.Count;
            var unit = new List<(double X, double Y)>(count);
            for (var i = 0; i < count; i++)
            {
                var angle = 2 * Math.PI * i / count - Math.PI / 2;
                unit.Add((JsMath.Cos(angle), JsMath.Sin(angle)));
            }

            double max;
            if (model.Options.Max is { } given)
            {
                max = given;
            }
            else
            {
                max = double.NegativeInfinity;
                foreach (var curve in curves)
                    foreach (var value in curve.Entries)
                        if (value > max) max = value;
            }

            // The chart is drawn around the origin. Margins reserve space; labels and the legend widen it.
            var x0 = -width / 2 - Option(config, "marginLeft", 16, 0, 2000);
            var x1 = width / 2 + Option(config, "marginRight", 16, 0, 2000);
            var y0 = -height / 2 - Option(config, "marginTop", 16, 0, 2000);
            var y1 = height / 2 + Option(config, "marginBottom", 16, 0, 2000);

            var grid = new StringBuilder();
            for (var i = 0; i < ticks; i++)
            {
                var r = Math.Min(radius, radius * (i + 1) / ticks);
                if (graticule == "circle") grid.Append($"<circle r=\"{Builder.Num(r)}\"/>");
                else grid.Append($"<polygon points=\"{Polygon(unit.Select(u => (u.X * r, u.Y * r)))}\"/>");
            }

            var spokes = new StringBuilder();
            var labels = new StringBuilder();
            for (var i = 0; i < count; i++)
            {
                var u = unit[i];
                var reach = radius * axisScale;
                spokes.Append($"M0,0L{Builder.Num(u.X * reach)},{Builder.Num(u.Y * reach)}");
                var label = Labels.LayoutLabel(axes[i].Label, false, small, 4000);
                var at = radius * labelFactor + 6;
                // Each label hangs off its axis on the side away from the center.
                var cx = u.X * at + (u.X > 0.01 ? label.Width / 2 : u.X < -0.01 ? -label.Width / 2 : 0);
                var cy = u.Y * at + (u.Y > 0.01 ? label.Height / 2 : u.Y < -0.01 ? -label.Height / 2 : 0);
                labels.Append($"<g class=\"pele-axis-label\" data-id=\"{Builder.EscText(axes[i].Name)}\">{Builder.LabelSvg(label, cx, cy, "")}</g>");
                x0 = Math.Min(x0, cx - label.Width / 2);
                x1 = Math.Max(x1, cx + label.Width / 2);
                y0 = Math.Min(y0, cy - label.Height / 2);
                y1 = Math.Max(y1, cy + label.Height / 2);
            }

            var shapes = new StringBuilder();
            for (var index = 0; index < curves.Count; index++)
            {
                var curve = curves[index];
                // Mermaid leaves out a curve that does not have a value for every axis.
                if (curve.Entries.Count != count) continue;
                var points = curve.Entries.Select((value, i) =>
                {
                    var r = RelativeRadius(value, min, max, radius);
                    var safe = double.IsFinite(r) ? r : 0;
                    return (unit[i].X * safe, unit[i].Y * safe);
                }).ToList();
                var color = Theme.SeriesColor(index);
                var attrs = $" class=\"pele-curve\" data-id=\"{Builder.EscText(curve.Name)}\" fill=\"{color}\" stroke=\"{color}\"";
                shapes.Append(graticule == "circle"
                    ? $"<path{attrs} d=\"{ClosedRoundCurve(points, tension)}\"/>"
                    : $"<polygon{attrs} points=\"{Polygon(points)}\"/>");
            }

            var legend = model.Options.ShowLegend
                ? curves.Select(curve => Labels.LayoutLabel(curve.Label, false, small, 4000)).ToList()
                : [];
            double legendWidth = 0;
            foreach (var label in legend) legendWidth = Math.Max(legendWidth, label.Width);
            var legendHeight = legend.Count * Row;
            var legendX = tight ? -(Swatch + 8 + legendWidth) / 2 : x1 + Gap;
            var legendTop = tight ? y1 + Gap / 2 : -legendHeight / 2;
            if (legend.Count > 0 && tight)
            {
                x0 = Math.Min(x0, legendX);
                x1 = Math.Max(x1, -legendX);
                y1 = legendTop + legendHeight;
            }
            else if (legend.Count > 0)
            {
                x1 = legendX + Swatch + 8 + legendWidth;
                y0 = Math.Min(y0, -legendHeight / 2);
                y1 = Math.Max(y1, legendHeight / 2);
            }
            var legendOut = new StringBuilder();
            for (var index = 0; index < legend.Count; index++)
            {
                var label = legend[index];
                var y = legendTop + index * Row + Row / 2;
                var color = Theme.SeriesColor(index);
                legendOut.Append($"<g class=\"pele-legend-item\" data-id=\"{Builder.EscText(curves[index].Name)}\">")
                    .Append($"<rect x=\"{Builder.Num(legendX)}\" y=\"{Builder.Num(y - Swatch / 2)}\" width=\"{Js.Num(Swatch)}\" height=\"{Js.Num(Swatch)}\" rx=\"2\" fill=\"{color}\" fill-opacity=\"0.4\" stroke=\"{color}\"/>")
                    .Append(Builder.LabelSvg(label, legendX + Swatch + 8 + label.Width / 2, y, ""))
                    .Append("</g>");
            }

            var title = Labels.LayoutLabel(model.Title, false, size, FitWidth.TitleRoom, (int)Style.Bold);
            var titleHeight = title.Height > 0 ? title.Height + 8 : 0;
            var bodyWidth = Math.Max(x1 - x0, title.Width);
            var totalWidth = Math.Ceiling(bodyWidth + 2 * pad);
            var totalHeight = Math.Ceiling(y1 - y0 + titleHeight + 2 * pad);
            var origin = $"translate({Builder.Num(pad + (bodyWidth - (x1 - x0)) / 2 - x0)},{Builder.Num(pad + titleHeight - y0)})";

            var content =
                Builder.LabelSvg(title, totalWidth / 2, pad + title.Height / 2, " class=\"pele-title\" font-weight=\"var(--_tw)\"")
                + $"<g transform=\"{origin}\">"
                + (grid.Length > 0 ? $"<g class=\"pele-graticule\" fill=\"none\" stroke=\"var(--_a)\">{grid}</g>" : "")
                + (spokes.Length > 0 ? $"<path class=\"pele-axes\" d=\"{spokes}\" fill=\"none\" stroke=\"var(--_b)\"/>" : "")
                + (shapes.Length > 0 ? $"<g class=\"pele-curves\" fill-opacity=\"0.2\" stroke-width=\"1.5\" stroke-linejoin=\"round\">{shapes}</g>" : "")
                + (labels.Length > 0 ? $"<g class=\"pele-axis-labels\" font-size=\"{Js.Num(small)}\">{labels}</g>" : "")
                + (legendOut.Length > 0 ? $"<g class=\"pele-legend\" font-size=\"{Js.Num(small)}\">{legendOut}</g>" : "")
                + "</g>";

            return new(Root.SvgDocument("radar", totalWidth, totalHeight, size, context, model.AccTitle, model.AccDescr, content), totalWidth, totalHeight, "radar");
        }
    }
}
