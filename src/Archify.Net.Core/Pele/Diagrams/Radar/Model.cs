using System.Text.RegularExpressions;

namespace Archify.Net;

// diagrams/radar/model.ts
internal sealed record RadarAxis(string Name, string Label);
internal sealed record RadarCurve(string Name, string Label, List<double> Entries);
internal sealed record RadarOptions(bool ShowLegend, double Ticks, double? Max, double Min, string Graticule);
internal sealed record RadarModel(string? Title, string? AccTitle, string? AccDescr, List<RadarAxis> Axes, List<RadarCurve> Curves, RadarOptions Options);

internal static partial class Pele
{
    internal static partial class Radar
    {
        const double MaxTicks = 32;

        // /\n\s+/g with JS \s.
        static readonly Regex ReDescrIndent = new("\n" + Chars.Ws + "+");

        static PeleException Invalid(string message) => new(message, "semantic");

        public static RadarModel Build(RadarAst ast, string? title)
        {
            var axes = ast.Axes.Select(axis => new RadarAxis(axis.Name, axis.Label ?? axis.Name)).ToList();

            var curves = ast.Curves.Select(curve =>
            {
                List<double> entries;
                if (curve.Entries[0].Axis is null)
                {
                    entries = [.. curve.Entries.Select(entry => entry.Value)];
                }
                else
                {
                    if (axes.Count == 0) throw Invalid("Axes must be populated before curves for reference entries");
                    // A JS Map: the first value for each axis name.
                    var byAxis = new Dictionary<string, double>(StringComparer.Ordinal);
                    foreach (var entry in curve.Entries)
                        if (entry.Axis is not null) byAxis.TryAdd(entry.Axis, entry.Value);
                    // Deviation RA2 (PORT-008 Task 11): upstream writes the label as parsed, so a Mermaid code shows as its internal
                    // placeholder (`#35;x` as `ﬂ°°35¶ßx`) and an HTML entity as typed, while the chart draws the decoded label.
                    // The port names the axis as the chart draws it.
                    entries = [.. axes.Select(axis => byAxis.TryGetValue(axis.Name, out var value) ? value : throw Invalid("Missing entry for axis " + Entities.DecodeEntities(axis.Label)))];
                }
                return new RadarCurve(curve.Name, curve.Label ?? curve.Name, entries);
            }).ToList();

            bool showLegend = true;
            double ticks = 5, min = 0;
            double? max = null;
            var graticule = "circle";
            foreach (var option in ast.Options)
            {
                if (option.Name == "showLegend") showLegend = option.Flag;
                else if (option.Name == "graticule") graticule = option.Text!;
                else if (option.Name == "ticks") ticks = option.Number;
                else if (option.Name == "max") max = option.Number;
                else min = option.Number;
            }
            if (ticks > MaxTicks) ticks = MaxTicks;

            return new(
                string.IsNullOrEmpty(ast.Title) ? title : ast.Title,   // ast.title || title
                string.IsNullOrEmpty(ast.AccTitle) ? null : Js.TrimStart(ast.AccTitle),   // .replace(/^\s+/g, '')
                string.IsNullOrEmpty(ast.AccDescr) ? null : ReDescrIndent.Replace(ast.AccDescr, "\n"),
                axes,
                curves,
                new(showLegend, ticks, max, min, graticule));
        }
    }
}
