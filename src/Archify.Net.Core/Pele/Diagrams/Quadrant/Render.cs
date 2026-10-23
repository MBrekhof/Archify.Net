using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/quadrant/render.ts
    internal static partial class Quadrant
    {
        const double Gap = 4;
        const int Careful = 300;

        sealed record Dot(double Cx, double Cy, double Reach, Label Text);

        // (config.quadrantChart as Config | undefined)?.[key]: anything but an object reads as undefined.
        static JsonNode? Setting(JsonObject config, string key) => config["quadrantChart"] is JsonObject o ? o[key] : null;

        static double Length(JsonObject config, string key, double fallback, double max) =>
            Setting(config, key) is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.D() is var d && d >= 0 ? Math.Min(d, max) : fallback;

        static bool SettingIs(JsonObject config, string key, string value) =>
            Setting(config, key) is JsonValue v && v.TryGetValue<string>(out var s) && s == value;

        static string Hex(string color) => color.StartsWith('#') ? color : "#" + color;

        public static PeleRenderResult Render(QuadrantDb model, JsonObject config, PeleRenderContext context)
        {
            var size = context.Options.FontSize;
            var axisSize = Js.Round(size * 0.875);
            var pointSize = Js.Round(size * 0.75);
            var natural = Js.Round(Math.Max(100, Length(config, "chartWidth", 500, 4000)));
            var width = Js.Round(FitWidth.CapWidth(natural));
            var height = Js.Round((Math.Max(100, Length(config, "chartHeight", 500, 4000)) * width) / natural);
            var outer = Length(config, "quadrantPadding", context.Options.Padding, 200);
            var xPad = Length(config, "xAxisLabelPadding", 5, 200);
            var yPad = Length(config, "yAxisLabelPadding", 5, 200);
            var hasPoints = model.Points.Count > 0;

            Label Text(QuadrantText? text, double fontSize, double max) =>
                Labels.LayoutLabel(text?.Text, text?.Markdown == true, fontSize, Math.Max(max, 40));

            var title = Labels.LayoutLabel(model.Title, false, size, Math.Max(width - 2 * outer, 40), (int)Style.Bold);
            var titlePad = Length(config, "titlePadding", 10, 200);
            var titleSpace = title.Height > 0 ? title.Height + titlePad : 0;

            var hasX = !string.IsNullOrEmpty(model.XAxisLeft?.Text) || !string.IsNullOrEmpty(model.XAxisRight?.Text);
            var hasY = !string.IsNullOrEmpty(model.YAxisBottom?.Text) || !string.IsNullOrEmpty(model.YAxisTop?.Text);
            // Mermaid moves the x axis labels below the chart as soon as there are points.
            var xBelow = hasPoints || SettingIs(config, "xAxisPosition", "bottom");
            var yRight = SettingIs(config, "yAxisPosition", "right");

            var side = width - 2 * outer;
            var xLeft = Text(model.XAxisLeft, axisSize, model.XAxisRight is not null ? side / 2 - 16 : side);
            var xRight = Text(model.XAxisRight, axisSize, side / 2 - 16);
            var xHeight = Math.Max(xLeft.Height, xRight.Height);
            var xSpace = hasX ? xHeight + xPad : 0;
            var tall = height - 2 * outer - titleSpace - xSpace;
            var yBottom = Text(model.YAxisBottom, axisSize, model.YAxisTop is not null ? tall / 2 - 16 : tall);
            var yTop = Text(model.YAxisTop, axisSize, tall / 2 - 16);
            var yHeight = Math.Max(yBottom.Height, yTop.Height);
            var ySpace = hasY ? yHeight + yPad : 0;

            var left = outer + (yRight ? 0 : ySpace);
            var top = outer + titleSpace + (xBelow ? 0 : xSpace);
            var w = Math.Max(0, side - ySpace);
            var h = Math.Max(0, tall);
            var tileW = Math.Max(0, (w - Gap) / 2);
            var tileH = Math.Max(0, (h - Gap) / 2);

            // Boxes a point label keeps clear of: the gaps between quadrants, quadrant labels, points, and the labels placed
            // before it.
            var taken = new List<double> { left + tileW, top, left + w - tileW, top + h, left, top + tileH, left + w, top + h - tileH };
            bool IsClear(int from, double x0, double y0, double x1, double y1)
            {
                for (var k = from; k < taken.Count; k += 4)
                {
                    if (x0 < taken[k + 2] && x1 > taken[k] && y0 < taken[k + 3] && y1 > taken[k + 1]) return false;
                }
                return true;
            }

            var textTop = Length(config, "quadrantTextTopPadding", 5, 200);
            var quadrants = new StringBuilder();
            for (var k = 0; k < 4; k++)
            {
                var x = k == 0 || k == 3 ? left + w - tileW : left;
                var y = k < 2 ? top : top + h - tileH;
                var text = Text(model.Quadrants[k], size, tileW - 16);
                var cx = x + tileW / 2;
                var cy = hasPoints ? y + textTop + text.Height / 2 : y + tileH / 2;
                taken.AddRange([cx - text.Width / 2, cy - text.Height / 2, cx + text.Width / 2, cy + text.Height / 2]);
                quadrants.Append($"<g class=\"pele-quadrant\" data-id=\"{k + 1}\">")
                    .Append($"<rect x=\"{Builder.Num(x)}\" y=\"{Builder.Num(y)}\" width=\"{Builder.Num(tileW)}\" height=\"{Builder.Num(tileH)}\" rx=\"{Theme.Radius}\"/>")
                    .Append(Builder.LabelSvg(text, cx, cy, " class=\"pele-label\" fill=\"var(--_fg)\" stroke=\"none\""))
                    .Append("</g>");
            }

            var axes = "";
            if (hasX)
            {
                var y = xBelow ? top + h + xPad + xHeight / 2 : top - xPad - xHeight / 2;
                axes += Builder.LabelSvg(xLeft, xRight.Height > 0 ? left + w / 4 : left + xLeft.Width / 2, y, "")
                    + Builder.LabelSvg(xRight, left + (w * 3) / 4, y, "");
            }
            if (hasY)
            {
                var x = yRight ? left + w + yPad + yHeight / 2 : left - yPad - yHeight / 2;
                string Turned(Label text, double y) =>
                    text.Height > 0 ? $"<g transform=\"translate({Builder.Num(x)},{Builder.Num(y)}) rotate(-90)\">{Builder.LabelSvg(text, 0, 0, "")}</g>" : "";
                axes += Turned(yBottom, yTop.Height > 0 ? top + (h * 3) / 4 : top + h - yBottom.Width / 2) + Turned(yTop, top + h / 4);
            }

            var radius = Length(config, "pointRadius", 5, 200);
            var textPad = Length(config, "pointTextPadding", 5, 200);
            var fill = Theme.SeriesColor(0);
            var dots = new List<Dot>();
            var circles = new List<string>();
            foreach (var point in model.Points)
            {
                if (!(point.X >= 0 && point.X <= 1 && point.Y >= 0 && point.Y <= 1)) continue;
                var shared = model.Classes.GetValueOrDefault(point.ClassName);
                var r = Math.Min(point.Style.Radius ?? shared?.Radius ?? radius, 2000);
                var color = point.Style.Color ?? shared?.Color;
                var strokeColor = point.Style.StrokeColor ?? shared?.StrokeColor;
                var strokeWidth = point.Style.StrokeWidth ?? shared?.StrokeWidth;
                var styles = new List<string>();
                if (!string.IsNullOrEmpty(color)) styles.Add("fill:" + Hex(color));
                if (!string.IsNullOrEmpty(strokeWidth))
                {
                    if (!string.IsNullOrEmpty(strokeColor)) styles.Add("stroke:" + Hex(strokeColor));
                    styles.Add("stroke-width:" + strokeWidth);
                }
                var cx = left + point.X * w;
                var cy = top + (1 - point.Y) * h;
                // parseInt(strokeWidth): the value matched /^\d+px$/.
                dots.Add(new(cx, cy, r + (!string.IsNullOrEmpty(strokeWidth) ? Math.Min(Js.ParseInt(strokeWidth, 0), 400) / 2 : 0), Text(point.Text, pointSize, 200)));
                circles.Add(
                    $"<circle class=\"pele-point{Theme.ClassNames(point.ClassName)}\" data-id=\"{Builder.EscText(point.Text.Text)}\" cx=\"{Builder.Num(cx)}\" cy=\"{Builder.Num(cy)}\" r=\"{Builder.Num(r)}\""
                    + (!string.IsNullOrEmpty(strokeWidth) && string.IsNullOrEmpty(strokeColor) ? $" stroke=\"{fill}\"" : "")
                    + Theme.ResolveStyle(styles).Shape + "/>");
            }

            // A label goes below its point, or above, right or left of it when that is the first place that is inside the chart and
            // free. A second round lets it cross the gaps between quadrants. Large charts skip the search for a free place.
            var careful = dots.Count <= Careful;
            if (careful) foreach (var (cx, cy, reach, _) in dots) taken.AddRange([cx - reach, cy - reach, cx + reach, cy + reach]);
            var labels = new StringBuilder();
            foreach (var (cx, cy, reach, text) in dots)
            {
                var halfW = text.Width / 2;
                var halfH = text.Size * 0.6 + (text.Height - text.LineHeight) / 2;
                var away = reach + textPad;
                double[] spots = [cx, cy + away + halfH, cx, cy - away - halfH, cx + away + halfW, cy, cx - away - halfW, cy];
                var tx = cx;
                var ty = spots[1];
                var found = false;
                for (var k = 0; k < 16; k += 2)
                {
                    var spot = k % 8;
                    var x = spot < 4 && text.Width < w ? Math.Min(Math.Max(spots[spot], left + halfW), left + w - halfW) : spots[spot];
                    var y = spots[spot + 1];
                    if (x - halfW < left || x + halfW > left + w || y - halfH < top || y + halfH > top + h) continue;
                    var free = !careful || IsClear(k < 8 ? 0 : 8, x - halfW, y - halfH, x + halfW, y + halfH);
                    if (!found || free)
                    {
                        tx = x;
                        ty = y;
                        found = true;
                    }
                    if (free) break;
                }
                if (careful) taken.AddRange([tx - halfW, ty - halfH, tx + halfW, ty + halfH]);
                labels.Append(Builder.LabelSvg(text, tx, ty, " class=\"pele-label\""));
            }

            // Mermaid puts each new point under the ones before it.
            circles.Reverse();
            var content =
                Builder.LabelSvg(title, width / 2, titlePad / 2 + title.Height / 2, " class=\"pele-title\" font-weight=\"var(--_tw)\"")
                + $"<g class=\"pele-quadrants\" fill=\"var(--_s)\" stroke=\"var(--_b)\">{quadrants}</g>"
                + (axes != "" ? $"<g class=\"pele-axis-labels\" font-size=\"{Js.Num(axisSize)}\" fill=\"var(--_m)\">{axes}</g>" : "")
                + (dots.Count > 0
                    ? $"<g class=\"pele-points\" fill=\"{fill}\">{string.Concat(circles)}</g>"
                      + $"<g class=\"pele-point-labels\" font-size=\"{Js.Num(pointSize)}\">{labels}</g>"
                    : "");

            return new(Root.SvgDocument("quadrantChart", width, height, size, context, model.AccTitle, model.AccDescr, content), width, height, "quadrantChart");
        }
    }
}
