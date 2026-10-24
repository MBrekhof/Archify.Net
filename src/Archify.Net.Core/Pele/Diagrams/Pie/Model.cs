using System.Text.RegularExpressions;

namespace Archify.Net;

// diagrams/pie/model.ts PieModel. Sections keep their first value per label, in insertion order (a JS Map).
internal sealed record PieModel(string? Title, string? AccTitle, string? AccDescr, bool ShowData, List<(string Label, double Value)> Sections);

internal static partial class Pele
{
    internal static partial class Pie
    {
        // /\n\s+/g with JS \s.
        static readonly Regex ReDescrIndent = new("\n" + Chars.Ws + "+");

        public static PieModel Build(PieAst ast, string? title)
        {
            var sections = new List<(string, double)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (label, value) in ast.Sections)
            {
                // Deviation PORT-011 (PORT-008 Task 11): upstream writes the label as parsed (model.ts:18), so a Mermaid code shows as
                // its placeholder (`#35;x` as `ﬂ°°35¶ßx`) and an HTML entity as typed. The port names the slice as the chart draws
                // it, as radar (RA2) and venn (VN3) do.
                if (value < 0)
                {
                    throw new PeleException(
                        $"\"{Entities.AsDrawn(label)}\" has invalid value: {Js.Num(value)}. Negative values are not allowed in pie charts. All slice values must be >= 0.",
                        "semantic");
                }
                if (seen.Add(label)) sections.Add((label, value));
            }
            return new(
                string.IsNullOrEmpty(ast.Title) ? title : ast.Title,
                string.IsNullOrEmpty(ast.AccTitle) ? null : Js.TrimStart(ast.AccTitle),   // .replace(/^\s+/g, '')
                string.IsNullOrEmpty(ast.AccDescr) ? null : ReDescrIndent.Replace(ast.AccDescr, "\n"),
                ast.ShowData,
                sections);
        }
    }
}
