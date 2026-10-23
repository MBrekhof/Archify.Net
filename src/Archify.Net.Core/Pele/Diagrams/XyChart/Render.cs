using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/xychart/render.ts
    internal static partial class XyChart
    {
        const double Tick = 4;
        const double Swatch = 12;
        const double Row = 26;
        const double LegendGap = 24;
        const double Limit = 1e300;

        sealed record Mark(double At, Label Label);

        static JsonObject Record(JsonNode? value) => value as JsonObject ?? new JsonObject();

        static bool On(JsonObject config, string key, bool fallback = true) =>
            config[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

        static double Dimension(JsonNode? value, double fallback) =>
            value is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.D() is var d && d > 0
                ? Js.Round(Math.Min(Math.Max(d, 100), 4000))
                : fallback;

        static double? Finite(double? value) =>
            value is { } v && double.IsFinite(v) ? Math.Min(Math.Max(v, -Limit), Limit) : null;

        static double Widest(List<Mark> marks)
        {
            double width = 0;
            foreach (var mark in marks) width = Math.Max(width, mark.Label.Width);
            return width;
        }

        // Writes a label with its left edge, center or right edge at x. A plain one-line label keeps its place when the text is
        // wider or narrower than measured.
        static string Aligned(Label label, double x, double y, string anchor)
        {
            var line = label.Lines.Count == 1 ? label.Lines[0] : null;
            if (line is not null && line.Count == 1 && line[0].Style == 0)
                return $"<text x=\"{Builder.Num(x)}\" y=\"{Builder.Num(y + label.Size * 0.35)}\" text-anchor=\"{anchor}\">{Builder.Esc(line[0].Text)}</text>";
            return Builder.LabelSvg(label, x + (anchor == "start" ? label.Width / 2 : anchor == "end" ? -label.Width / 2 : 0), y, "");
        }

        public static PeleRenderResult Render(XyChartDb model, JsonObject config, PeleRenderContext context)
        {
            if (model.Plots.Count == 0) throw new PeleException("No Plot to render, please provide a plot with some data", "semantic");
            var chart = Record(config["xyChart"]);
            var size = context.Options.FontSize;
            var small = Js.Round(size * 0.875);
            var tiny = Js.Round(size * 0.75);
            var pad = context.Options.Padding;
            var natural = Dimension(chart["width"], 700);
            var width = FitWidth.CapWidth(natural);
            // A narrower chart is not as much shorter, so that the plot keeps room for its marks.
            var height = Js.Round(Dimension(chart["height"], 500) * Math.Max(0.6, width / natural));
            var horizontal = model.Orientation is { } orientation
                ? orientation == "horizontal"
                : chart["chartOrientation"] is JsonValue co && co.TryGetValue<string>(out var cs) && cs == "horizontal";
            // The value of a bar is always written past its end, which is what showDataLabelOutsideBar asks for in Mermaid.
            // Inside, whether it fits changes from bar to bar and with the width of the chart.
            var showValues = On(chart, "showDataLabel", false);
            var x = model.XAxis;
            var plots = model.Plots;
            Label Text(string? raw, double fontSize, double max) => Labels.LayoutLabel(raw, false, fontSize, max);
            var lineHeight = Text("0", small, 4000).Height;
            var tinyHeight = Text("0", tiny, 4000).Height;

            var title = Labels.LayoutLabel(On(chart, "showTitle") ? model.Title : null, false, size, width - 2 * pad, (int)Style.Bold);
            var titleSpace = title.Height > 0 ? title.Height + 12 : 0;

            var legend = new List<(int Index, Label Label)>();
            double legendWidth = 0;
            if (On(chart, "showLegend"))
            {
                for (var index = 0; index < plots.Count; index++)
                {
                    if (plots[index].Title == "") continue;
                    var label = Text(plots[index].Title, small, width * 0.3);
                    legend.Add((index, label));
                    legendWidth = Math.Max(legendWidth, label.Width + Swatch + 8);
                }
            }
            var legendSpace = legend.Count > 0 ? legendWidth + LegendGap : 0;

            // The value range: as written, or taken from the data, where bars stand on zero and the range grows to whole steps.
            var bars = plots.Count(plot => plot.Bar);
            var lo = model.YAxis.Min;
            var hi = model.YAxis.Max;
            // Deviation (PORT-008 Task 11): upstream tests `lo !== hi` before clamping, so a written range whose ends both clamp to
            // the same ±1e300 (y-axis 1e301 --> 1e302) divides by zero and draws 100 "NaN" ticks. Tested after clamping, it falls
            // back to the data as a collapsed range (5 --> 5) does; the x axis already compares its clamped ends (`ranged`).
            var exact = model.HasSetYAxis && double.IsFinite(lo) && double.IsFinite(hi) && Finite(lo) != Finite(hi);
            if (exact)
            {
                lo = Finite(lo)!.Value;
                hi = Finite(hi)!.Value;
            }
            else
            {
                lo = double.PositiveInfinity;
                hi = double.NegativeInfinity;
                foreach (var plot in plots)
                {
                    foreach (var (_, raw) in plot.Data)
                    {
                        if (Finite(raw) is not { } value) continue;
                        if (value < lo) lo = value;
                        if (value > hi) hi = value;
                    }
                }
                if (lo > hi) lo = hi = 0;
                if (bars > 0)
                {
                    lo = Math.Min(lo, 0);
                    hi = Math.Max(hi, 0);
                }
                if (lo == hi)
                {
                    if (lo == 0) hi = 1;
                    else
                    {
                        lo -= Math.Abs(lo) / 2;
                        hi += Math.Abs(hi) / 2;
                    }
                }
            }

            List<Mark> ValueMarks(double length, double spacing)
            {
                var step = TickStep(Math.Abs(hi - lo), Math.Max(2, Math.Min(10, Math.Floor(length / spacing))));
                if (!exact)
                {
                    lo = TickValue(step, Math.Floor(TickIndex(step, lo) + 1e-9));
                    hi = TickValue(step, Math.Ceiling(TickIndex(step, hi) - 1e-9));
                }
                return [.. TicksBetween(Math.Min(lo, hi), Math.Max(lo, hi), step).Select(value => new Mark((value - lo) / (hi - lo), Text(Js.Num(value), small, 4000)))];
            }

            // Places along the category axis are fractions of its length. Categories and bars sit in the middle of equal slots;
            // the points of a line on a numeric axis run from end to end.
            double count = 1;
            if (x.Band) count = Math.Max(x.Categories.Count, 1);
            else foreach (var plot in plots) count = Math.Max(count, plot.Data.Count);
            var slot = 1 / count;
            var inset = x.Band || bars > 0 ? slot / 2 : 0;
            var x0 = Finite(x.Band ? null : x.Min);
            var x1 = Finite(x.Band ? null : x.Max);
            var ranged = x0 is not null && x1 is not null && x0 != x1;
            double Place(int i, int n) =>
                x.Band ? (i + 0.5) * slot : inset + (n > 1 ? (double)i / (n - 1) : ranged ? 0 : 0.5) * (1 - 2 * inset);

            var categoryConf = Record(chart["xAxis"]);
            var valueConf = Record(chart["yAxis"]);
            var leftConf = horizontal ? categoryConf : valueConf;
            var bottomConf = horizontal ? valueConf : categoryConf;
            var leftTitle = Text(On(leftConf, "showTitle") ? (horizontal ? x.Title : model.YAxis.Title) : null, small, height * 0.8);
            var bottomTitle = Text(On(bottomConf, "showTitle") ? (horizontal ? model.YAxis.Title : x.Title) : null, small, width * 0.8);
            var leftTick = On(leftConf, "showTick") ? Tick : 0;
            var bottomTick = On(bottomConf, "showTick") ? Tick : 0;
            var leftLabels = On(leftConf, "showLabel");
            var bottomLabels = On(bottomConf, "showLabel");

            var wrapAt = horizontal ? width * 0.3 : Math.Max(48, (width - 2 * pad - 64 - legendSpace) * slot - 8);
            var categories = x.Band && On(categoryConf, "showLabel") ? x.Categories.Select(category => Text(category, small, wrapAt)).ToList() : [];
            double categoryWidth = 0;
            double categoryHeight = 0;
            foreach (var label in categories)
            {
                categoryWidth = Math.Max(categoryWidth, label.Width);
                categoryHeight = Math.Max(categoryHeight, label.Height);
            }

            // The labelled places of the category axis. Labels that would run into each other are thinned out.
            List<Mark> CategoryMarks(double length)
            {
                var marks = new List<Mark>();
                if (x.Band)
                {
                    var need = categories.Count == 0 ? 8 : horizontal ? categoryHeight : categoryWidth + 12;
                    var every = Math.Max(1, Math.Ceiling(need / (length * slot)));
                    for (double i = 0; i < x.Categories.Count; i += every)
                        marks.Add(new((i + 0.5) * slot, i < categories.Count ? categories[(int)i] : Text(null, small, 0)));
                }
                else if (ranged)
                {
                    var (a, b) = (x0!.Value, x1!.Value);
                    var step = TickStep(Math.Abs(b - a), Math.Max(2, Math.Min(10, Math.Floor(length / (horizontal ? 32 : 80)))));
                    foreach (var value in TicksBetween(Math.Min(a, b), Math.Max(a, b), step))
                        marks.Add(new(inset + ((value - a) / (b - a)) * (1 - 2 * inset), Text(Js.Num(value), small, 4000)));
                }
                else if (x0 is { } only)
                {
                    marks.Add(new(0.5, Text(Js.Num(only), small, 4000)));
                }
                return marks;
            }

            // Room past the end of the value axis for what is written beside a point or a bar.
            double pointLabelWidth = 0;
            double valueWidth = 0;
            foreach (var plot in plots)
            {
                foreach (var label in plot.PointLabels ?? []) pointLabelWidth = Math.Max(pointLabelWidth, Measurer.Width(label, tiny, Style.None));
                if (showValues && horizontal && plot.Bar)
                {
                    foreach (var (_, value) in plot.Data)
                        valueWidth = Math.Max(valueWidth, Measurer.Width(Finite(value) is { } f ? Js.Num(f) : "", tiny, Style.None));
                }
            }

            var bottomLabelHeight = !bottomLabels ? 0 : horizontal || !x.Band ? lineHeight : categoryHeight;
            var bottomSpace =
                bottomTick + (bottomLabelHeight > 0 ? bottomLabelHeight + 4 : 0) + (bottomTitle.Height > 0 ? bottomTitle.Height + 4 : 0);
            var headroom = horizontal ? 0 : pointLabelWidth > 0 || showValues ? tinyHeight + 4 : leftLabels ? lineHeight / 2 : 0;
            var top = pad + titleSpace + headroom;
            var plotHeight = Math.Max(1, height - pad - bottomSpace - top);
            var bottom = top + plotHeight;

            var leftMarks = horizontal ? CategoryMarks(plotHeight) : ValueMarks(plotHeight, 56);
            var leftLabelWidth = leftLabels ? Widest(leftMarks) : 0;
            var left = pad + (leftTitle.Height > 0 ? leftTitle.Height + 6 : 0) + (leftLabelWidth > 0 ? leftLabelWidth + 6 : 0) + leftTick;
            var beside = Math.Max(pointLabelWidth > 0 ? pointLabelWidth + 12 : 0, valueWidth > 0 ? valueWidth + 8 : 0);
            var reach = horizontal ? Math.Min(width * 0.25, beside) : 0;
            var plotWidth = Math.Max(1, width - pad - left - legendSpace - reach);
            var bottomMarks = horizontal ? ValueMarks(plotWidth, 80) : CategoryMarks(plotWidth);
            if (bottomLabels && legendSpace + reach == 0)
            {
                double over = 0;
                foreach (var mark in bottomMarks) over = Math.Max(over, mark.At * plotWidth + mark.Label.Width / 2 - plotWidth);
                plotWidth = Math.Max(1, plotWidth - over);
            }
            var right = left + plotWidth;

            double Px(double c, double v) => horizontal ? left + v * plotWidth : left + c * plotWidth;
            double Py(double c, double v) => horizontal ? top + c * plotHeight : bottom - v * plotHeight;
            double Scale(double value) => (value - lo) / (hi - lo);
            static double Clamp(double v) => Math.Min(Math.Max(v, 0), 1);

            // A line across the plot at a place on the value axis.
            string Rule(double v) =>
                horizontal ? $"M{Builder.Num(Px(0, v))},{Builder.Num(top)}V{Builder.Num(bottom)}" : $"M{Builder.Num(left)},{Builder.Num(Py(0, v))}H{Builder.Num(right)}";

            var grid = new StringBuilder();
            foreach (var mark in horizontal ? bottomMarks : leftMarks) grid.Append(Rule(mark.At));

            var axis = new StringBuilder();
            var labels = new StringBuilder();
            if (On(leftConf, "showAxisLine")) axis.Append($"M{Builder.Num(left)},{Builder.Num(top)}V{Builder.Num(bottom)}");
            if (On(bottomConf, "showAxisLine")) axis.Append($"M{Builder.Num(left)},{Builder.Num(bottom)}H{Builder.Num(right)}");
            foreach (var mark in leftMarks)
            {
                var y = horizontal ? top + mark.At * plotHeight : bottom - mark.At * plotHeight;
                if (leftTick != 0) axis.Append($"M{Builder.Num(left)},{Builder.Num(y)}h{Js.Num(-leftTick)}");
                if (leftLabels) labels.Append(Aligned(mark.Label, left - leftTick - 6, y, "end"));
            }
            foreach (var mark in bottomMarks)
            {
                var at = left + mark.At * plotWidth;
                if (bottomTick != 0) axis.Append($"M{Builder.Num(at)},{Builder.Num(bottom)}v{Js.Num(bottomTick)}");
                if (bottomLabels) labels.Append(Aligned(mark.Label, at, bottom + bottomTick + 4 + mark.Label.Height / 2, "middle"));
            }

            var span = (horizontal ? plotHeight : plotWidth) * slot;
            var group = span * 0.7;
            var thickness = group / Math.Max(bars, 1);
            var gap = bars > 1 ? Math.Min(2, thickness * 0.2) : 0;
            var @base = Clamp(Scale(0));
            // Bars that go both ways get a line to stand on.
            if (bars > 0 && @base > 0 && @base < 1) axis.Append(Rule(@base));
            // Lines are drawn over the bars wherever they were declared, as a bar would hide part of one under it. A series keeps
            // the color of its place in the source.
            var series = new StringBuilder();
            var lines = new StringBuilder();
            var outside = new StringBuilder();
            var pointLabels = new StringBuilder();
            var bar = 0;
            for (var index = 0; index < plots.Count; index++)
            {
                var plot = plots[index];
                var color = Theme.SeriesColor(index);
                var id = plot.Title != "" ? $" data-id=\"{Builder.EscText(plot.Title)}\"" : "";
                // A series declared before the categories can hold more values than there are categories.
                var n = x.Band ? (int)Math.Min(plot.Data.Count, count) : plot.Data.Count;
                if (plot.Bar)
                {
                    var offset = (bar++ - (bars - 1) / 2.0) * thickness - (thickness - gap) / 2;
                    var rects = new StringBuilder();
                    for (var i = 0; i < n; i++)
                    {
                        if (Finite(plot.Data[i].Value) is not { } value) continue;
                        var c = Place(i, n);
                        var v = Clamp(Scale(value));
                        var from = Math.Min(@base, v);
                        var to = Math.Max(@base, v);
                        var bx = horizontal ? Px(c, from) : Px(c, 0) + offset;
                        var by = horizontal ? Py(c, 0) + offset : Py(c, to);
                        var bw = horizontal ? (to - from) * plotWidth : thickness - gap;
                        var bh = horizontal ? thickness - gap : (to - from) * plotHeight;
                        rects.Append($"<rect x=\"{Builder.Num(bx)}\" y=\"{Builder.Num(by)}\" width=\"{Builder.Num(bw)}\" height=\"{Builder.Num(bh)}\"/>");
                        if (!showValues) continue;
                        var label = Js.Num(value);
                        var w = Measurer.Width(label, tiny, Style.None);
                        // Towards the end of the bar: 1 when it runs up or right from the baseline, -1 the other way.
                        var @out = v >= @base ? 1 : -1;
                        var tx = bx + bw / 2;
                        var ty = by + bh / 2;
                        var anchor = "middle";
                        if (horizontal)
                        {
                            if (bh < tiny) continue;
                            tx = (@out > 0 ? bx + bw : bx) + @out * 6;
                            anchor = @out > 0 ? "start" : "end";
                        }
                        else
                        {
                            if (w + 2 > span / bars) continue;
                            ty = (@out > 0 ? by : by + bh) - @out * (tinyHeight / 2 + 2);
                        }
                        outside.Append($"<text x=\"{Builder.Num(tx)}\" y=\"{Builder.Num(ty + tiny * 0.35)}\" text-anchor=\"{anchor}\">{label}</text>");
                    }
                    series.Append($"<g class=\"pele-series pele-bars\"{id} fill=\"{color}\">{rects}</g>");
                    continue;
                }

                // A line is cut where it leaves the value range, so it stays inside the plot.
                var d = new StringBuilder();
                var dots = new StringBuilder();
                var pen = false;
                double pc = 0;
                var pv = double.NaN;
                for (var i = 0; i < n; i++)
                {
                    if (Finite(plot.Data[i].Value) is not { } value)
                    {
                        pv = double.NaN;
                        continue;
                    }
                    var c = Place(i, n);
                    var v = Scale(value);
                    var visible = v >= 0 && v <= 1;
                    if (double.IsNaN(pv))
                    {
                        if (visible) d.Append($"M{Builder.Num(Px(c, v))},{Builder.Num(Py(c, v))}");
                        pen = visible;
                    }
                    else
                    {
                        var dv = v - pv;
                        double t0 = 0;
                        double t1 = 1;
                        if (dv != 0)
                        {
                            var a = -pv / dv;
                            var b = (1 - pv) / dv;
                            t0 = Math.Max(t0, Math.Min(a, b));
                            t1 = Math.Min(t1, Math.Max(a, b));
                        }
                        else if (!visible)
                        {
                            t0 = 2;
                        }
                        if (t0 <= t1)
                        {
                            var c0 = pc + (c - pc) * t0;
                            var v0 = pv + dv * t0;
                            var c1 = pc + (c - pc) * t1;
                            var v1 = pv + dv * t1;
                            if (!pen || t0 > 0) d.Append($"M{Builder.Num(Px(c0, v0))},{Builder.Num(Py(c0, v0))}");
                            d.Append($"L{Builder.Num(Px(c1, v1))},{Builder.Num(Py(c1, v1))}");
                        }
                        pen = visible;
                    }
                    pc = c;
                    pv = v;
                    if (!visible) continue;
                    var cx = Px(c, v);
                    var cy = Py(c, v);
                    if (span >= 12) dots.Append($"<circle cx=\"{Builder.Num(cx)}\" cy=\"{Builder.Num(cy)}\" r=\"3\"/>");
                    var label = Text(plot.PointLabels is { } pl && i < pl.Count ? pl[i] : null, tiny, 4000);
                    if (label.Height > 0)
                    {
                        var half = label.Width / 2;
                        pointLabels.Append(horizontal
                            ? Aligned(label, cx + 10, cy, "start")
                            : Aligned(label, Math.Max(Math.Min(cx, right - half), left + half), cy - 6 - label.Height / 2, "middle"));
                    }
                }
                lines.Append($"<g class=\"pele-series pele-line\"{id} fill=\"{color}\">")
                    .Append(d.Length > 0 ? $"<path d=\"{d}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/>" : "")
                    .Append(dots)
                    .Append("</g>");
            }

            var titles = new StringBuilder();
            if (leftTitle.Height > 0)
            {
                titles.Append($"<g transform=\"translate({Builder.Num(pad + leftTitle.Height / 2)},{Builder.Num(top + plotHeight / 2)}) rotate(-90)\">{Builder.LabelSvg(leftTitle, 0, 0, "")}</g>");
            }
            titles.Append(Builder.LabelSvg(bottomTitle, left + plotWidth / 2, height - pad - bottomTitle.Height / 2, ""));

            var legendOut = new StringBuilder();
            double legendHeight = 0;
            foreach (var item in legend) legendHeight += Math.Max(Row, item.Label.Height + 6);
            var ly = top + Math.Max(0, (plotHeight - legendHeight) / 2);
            var lx = right + reach + LegendGap;
            foreach (var (index, label) in legend)
            {
                var row = Math.Max(Row, label.Height + 6);
                if (ly + row > height) break;
                var y = ly + row / 2;
                var color = Theme.SeriesColor(index);
                legendOut.Append($"<g class=\"pele-legend-item\" data-id=\"{Builder.EscText(plots[index].Title)}\">")
                    .Append(plots[index].Bar
                        ? $"<rect x=\"{Builder.Num(lx)}\" y=\"{Builder.Num(y - Swatch / 2)}\" width=\"{Js.Num(Swatch)}\" height=\"{Js.Num(Swatch)}\" rx=\"2\" fill=\"{color}\"/>"
                        : $"<path d=\"M{Builder.Num(lx)},{Builder.Num(y)}h{Js.Num(Swatch)}\" stroke=\"{color}\" stroke-width=\"2\" stroke-linecap=\"round\"/>")
                    .Append(Builder.LabelSvg(label, lx + Swatch + 8 + label.Width / 2, y, ""))
                    .Append("</g>");
                ly += row;
            }

            var content =
                Builder.LabelSvg(title, width / 2, pad + title.Height / 2, " class=\"pele-title\" font-weight=\"var(--_tw)\"")
                + (grid.Length > 0 ? $"<path class=\"pele-grid\" d=\"{grid}\" stroke=\"var(--_a)\"/>" : "")
                + $"<g class=\"pele-plot\">{series}{lines}</g>"
                + (axis.Length > 0 ? $"<path class=\"pele-axis\" d=\"{axis}\" fill=\"none\" stroke=\"var(--_b)\"/>" : "")
                + (labels.Length > 0 ? $"<g class=\"pele-axis-labels\" font-size=\"{Js.Num(small)}\" fill=\"var(--_m)\">{labels}</g>" : "")
                + (titles.Length > 0 ? $"<g class=\"pele-axis-titles\" font-size=\"{Js.Num(small)}\">{titles}</g>" : "")
                + (outside.Length > 0 ? $"<g class=\"pele-data-labels\" font-size=\"{Js.Num(tiny)}\">{outside}</g>" : "")
                + (pointLabels.Length > 0 ? $"<g class=\"pele-point-labels\" font-size=\"{Js.Num(tiny)}\">{pointLabels}</g>" : "")
                + (legendOut.Length > 0 ? $"<g class=\"pele-legend\" font-size=\"{Js.Num(small)}\">{legendOut}</g>" : "");

            return new(Root.SvgDocument("xychart", width, height, size, context, model.AccTitle, model.AccDescr, content), width, height, "xychart");
        }
    }
}
