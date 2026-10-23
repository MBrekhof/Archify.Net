using System.Text.RegularExpressions;

namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/quadrant/db.ts
    internal static partial class Quadrant
    {
        internal sealed record QuadrantText(string Text, bool Markdown);

        // PointStyle: each field is set only when a style names it (`undefined` is null).
        internal sealed class PointStyle
        {
            public double? Radius { get; set; }
            public string? Color { get; set; }
            public string? StrokeColor { get; set; }
            public string? StrokeWidth { get; set; }
        }

        internal sealed record QuadrantPoint(double X, double Y, QuadrantText Text, string ClassName, PointStyle Style);

        // /^\d+$/, /^#?([\dA-Fa-f]{6}|[\dA-Fa-f]{3})$/ and /^\d+px$/: JS `\d` is ASCII and `$` without `m` is the very end.
        static readonly Regex ReNumber = new(@"\A[0-9]+\z");
        static readonly Regex ReHex = new(@"\A#?(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{3})\z");
        static readonly Regex RePixels = new(@"\A[0-9]+px\z");

        static PeleException Invalid(string style, string value, string kind) =>
            new($"value for {style} {value} is invalid, please use a valid {kind}", "semantic");

        // The first two parts of Mermaid's `style.trim().split(/\s*:\s*/)`, found in one pass.
        static (string Key, string Value) KeyValue(string style)
        {
            var s = Js.Trim(style);
            var colon = s.IndexOf(':');
            if (colon == -1) return (s, "undefined");
            var rest = Js.TrimStart(s[(colon + 1)..]);
            var next = rest.IndexOf(':');
            return (Js.TrimEnd(s[..colon]), next == -1 ? rest : Js.TrimEnd(rest[..next]));
        }

        public static PointStyle ParseStyles(List<string> styles)
        {
            var output = new PointStyle();
            foreach (var style in styles)
            {
                var (key, value) = KeyValue(style);
                if (key == "radius")
                {
                    if (!ReNumber.IsMatch(value)) throw Invalid(key, value, "number");
                    output.Radius = Js.ParseInt(value, 0);   // parseInt(value)
                }
                else if (key == "color")
                {
                    if (!ReHex.IsMatch(value)) throw Invalid(key, value, "hex code");
                    output.Color = value;
                }
                else if (key == "stroke-color")
                {
                    if (!ReHex.IsMatch(value)) throw Invalid(key, value, "hex code");
                    output.StrokeColor = value;
                }
                else if (key == "stroke-width")
                {
                    if (!RePixels.IsMatch(value)) throw Invalid(key, value, "number of pixels (eg. 10px)");
                    output.StrokeWidth = value;
                }
                else
                {
                    throw new PeleException($"style named {key} is not supported.", "semantic");
                }
            }
            return output;
        }

        static QuadrantText Trimmed(QuadrantText label) => label with { Text = Js.Trim(label.Text) };

        internal sealed class QuadrantDb
        {
            // /\n\s+/g with JS \s.
            static readonly Regex ReDescrIndent = new("\n" + Chars.Ws + "+");

            public string? Title { get; set; }
            public string? AccTitle { get; private set; }
            public string? AccDescr { get; private set; }
            // Quadrants 1 to 4: top right, top left, bottom left, bottom right.
            public QuadrantText?[] Quadrants { get; } = new QuadrantText?[4];
            public QuadrantText? XAxisLeft { get; private set; }
            public QuadrantText? XAxisRight { get; private set; }
            public QuadrantText? YAxisBottom { get; private set; }
            public QuadrantText? YAxisTop { get; private set; }
            public List<QuadrantPoint> Points { get; } = [];
            public Dictionary<string, PointStyle> Classes { get; } = new(StringComparer.Ordinal);

            public void SetQuadrantText(int index, QuadrantText label) => Quadrants[index] = Trimmed(label);
            public void SetXAxisLeftText(QuadrantText label) => XAxisLeft = Trimmed(label);
            public void SetXAxisRightText(QuadrantText label) => XAxisRight = Trimmed(label);
            public void SetYAxisBottomText(QuadrantText label) => YAxisBottom = Trimmed(label);
            public void SetYAxisTopText(QuadrantText label) => YAxisTop = Trimmed(label);

            public void AddPoint(QuadrantText label, string className, string x, string y, List<string> styles) =>
                Points.Add(new(Js.ToNumber(x), Js.ToNumber(y), Trimmed(label), className, ParseStyles(styles)));

            public void AddClass(string className, List<string> styles) => Classes[className] = ParseStyles(styles);
            public void SetDiagramTitle(string text) => Title = text;
            public void SetAccTitle(string text) => AccTitle = Js.TrimStart(text);   // .replace(/^\s+/g, '')
            public void SetAccDescription(string text) => AccDescr = ReDescrIndent.Replace(text, "\n");
        }
    }
}
