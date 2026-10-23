using System.Text.RegularExpressions;

namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/xychart/db.ts
    internal static partial class XyChart
    {
        // `Markdown` is the grammar's type 'markdown' (X3). Render reads only the text, as upstream does.
        internal sealed record XyText(string Text, bool Markdown = false);

        internal sealed record DataPoint(double Value, string Label);

        // BandAxis | LinearAxis: `Categories` is set for a band axis, `Min`/`Max` for a linear one.
        internal sealed class Axis
        {
            public bool Band { get; init; }
            public string Title { get; set; } = "";
            public List<string> Categories { get; init; } = [];
            public double Min { get; init; }
            public double Max { get; init; }
        }

        // A series: each value with its place on the x axis; a category with no value has null (undefined).
        internal sealed class Plot
        {
            public required bool Bar { get; init; }
            public required string Title { get; init; }
            public required List<(string At, double? Value)> Data { get; init; }
            public List<string>? PointLabels { get; set; }
        }

        internal sealed class XyChartDb
        {
            // /\n\s+/g with JS \s.
            static readonly Regex ReDescrIndent = new("\n" + Chars.Ws + "+");

            public string? Title { get; set; }
            public string? AccTitle { get; private set; }
            public string? AccDescr { get; private set; }
            public string? Orientation { get; private set; }
            public Axis XAxis { get; private set; } = new() { Band = true };
            public Axis YAxis { get; private set; } = new() { Min = double.PositiveInfinity, Max = double.NegativeInfinity };
            bool hasSetXAxis;
            // False while the y range comes from the data and not from a `y-axis` statement.
            public bool HasSetYAxis { get; private set; }
            public List<Plot> Plots { get; } = [];

            // Deviation X1 (PORT-008 Task 11): upstream compares case-sensitively, so `HORIZONTAL`, which the case-insensitive
            // lexer takes as the orientation, draws vertical. The token is ASCII letters only.
            public void SetOrientation(string orientation) =>
                Orientation = Js.ToLowerCase(orientation) == "horizontal" ? "horizontal" : "vertical";

            public void SetXAxisTitle(XyText title) => XAxis.Title = Js.Trim(title.Text);

            public void SetXAxisRangeData(double min, double max)
            {
                XAxis = new() { Title = XAxis.Title, Min = min, Max = max };
                hasSetXAxis = true;
            }

            public void SetXAxisBand(List<XyText> categories)
            {
                XAxis = new() { Band = true, Title = XAxis.Title, Categories = [.. categories.Select(c => Js.Trim(c.Text))] };
                hasSetXAxis = true;
            }

            public void SetYAxisTitle(XyText title) => YAxis.Title = Js.Trim(title.Text);

            public void SetYAxisRangeData(double min, double max)
            {
                YAxis = new() { Title = YAxis.Title, Min = min, Max = max };
                HasSetYAxis = true;
            }

            List<(string, double?)> PlotData(List<double> values)
            {
                if (values.Count == 0) return [];
                // The first series fixes the x range when no x-axis statement did.
                if (!hasSetXAxis) SetXAxisRangeData(1, values.Count);
                var x = XAxis;
                if (x.Band && values.Count > x.Categories.Count) values = values[..x.Categories.Count];
                if (!HasSetYAxis)
                {
                    var (min, max) = (YAxis.Min, YAxis.Max);
                    foreach (var value in values)
                    {
                        if (value < min) min = value;
                        if (value > max) max = value;
                    }
                    YAxis = new() { Title = YAxis.Title, Min = min, Max = max };
                }
                if (x.Band) return [.. x.Categories.Select((category, i) => (category, i < values.Count ? values[i] : (double?)null))];
                if (values.Count == 1) return [(Js.Num(x.Min), values[0])];
                var step = (x.Max - x.Min) / (values.Count - 1);
                return [.. values.Select((value, i) => (Js.Num(x.Min + i * step), (double?)value))];
            }

            void AddPlot(bool bar, XyText title, List<DataPoint> data)
            {
                var plot = new Plot { Bar = bar, Title = Js.Trim(title.Text), Data = PlotData([.. data.Select(d => d.Value)]) };
                // Mermaid accepts labels on the points of a bar series and ignores them.
                if (!bar && data.Any(d => Js.Trim(d.Label) != "")) plot.PointLabels = [.. data.Select(d => Js.Trim(d.Label))];
                Plots.Add(plot);
            }

            public void SetLineData(XyText title, List<DataPoint> data) => AddPlot(false, title, data);
            public void SetBarData(XyText title, List<DataPoint> data) => AddPlot(true, title, data);
            public void SetDiagramTitle(string text) => Title = text;
            public void SetAccTitle(string text) => AccTitle = Js.TrimStart(text);   // .replace(/^\s+/g, '')
            public void SetAccDescription(string text) => AccDescr = ReDescrIndent.Replace(text, "\n");
        }
    }
}
