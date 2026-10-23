using System.Text.RegularExpressions;

namespace Archify.Net;

internal static partial class Pele
{
    internal sealed record ResolvedStyle(string Shape, string Line, string Text, string? Stroke, bool Bold, bool Italic, double? FontSize);

    // svg/theme.ts
    internal static class Theme
    {
        // Private aliases set once on the root element (theme.ts:6-9).
        public const string RootStyle =
            "--_bg:var(--pele-bg,#fff);--_fg:var(--pele-fg,#1f1f1f);--_m:var(--pele-muted,#6e6e6e);" +
            "--_l:var(--pele-line,#1f1f1f);--_s:var(--pele-surface,#f3f3f3);--_a:var(--pele-surface-alt,#e6e6e6);" +
            "--_b:var(--pele-border,#8a8a8a);--_c:var(--pele-accent,#5b7bd5);color:var(--_fg)";

        public const string FontMono = "var(--_fm)";
        public const string Radius = "var(--pele-radius,4px)";
        // The color of what a diagram marks as critical.
        public const string Critical = "var(--pele-critical,#e45756)";

        // theme.ts:27-33 with no font family: archify passes neither fontFamily nor fontFamilyMono, so families() returns the
        // generic names and RE_FAMILIES (the `u`-flag \p{L}\p{N} pattern) never runs.
        public static string FontStyle(double size) =>
            $";--_fm:var(--pele-font-mono,monospace);font:{Js.Num(size)}px sans-serif;" +
            "letter-spacing:normal;word-spacing:normal;text-transform:none;direction:ltr;" +
            "font-family:var(--pele-font,sans-serif)";

        static readonly HashSet<string> ShapeProps = new(StringComparer.Ordinal)
        {
            "fill", "fill-opacity", "stroke", "stroke-width", "stroke-dasharray", "stroke-dashoffset", "stroke-linecap",
            "stroke-linejoin", "stroke-opacity", "opacity", "rx", "ry",
        };

        static readonly HashSet<string> TextProps = new(StringComparer.Ordinal)
        {
            "color", "font-size", "font-family", "font-weight", "font-style", "text-decoration", "letter-spacing", "word-spacing", "opacity",
        };

        // /url\s*\(|expression\s*\(|javascript:|[<>{};\\]|@import|image-set|\/\*/i (ASCII folding, Detect.Ci).
        static readonly Regex ReUnsafe = new(
            $@"{Detect.Ci("url")}{Chars.Ws}*\(|{Detect.Ci("expression")}{Chars.Ws}*\(|{Detect.Ci("javascript:")}|[<>{{}};\\]|{Detect.Ci("@import")}|{Detect.Ci("image-set")}|/\*");
        static readonly Regex ReImportant = new(Detect.Ci("!important"));
        static readonly Regex RePx = new(@"\A[0-9.]+(px)?\z");

        static readonly ResolvedStyle None = new("", "", "", null, false, false, null);

        // Splits Mermaid style declarations into what applies to a shape and what applies to its text (theme.ts:85-122).
        public static ResolvedStyle ResolveStyle(IReadOnlyList<string> declarations)
        {
            if (declarations.Count == 0) return None;
            var shape = new JsMap<string, string>();
            var text = new JsMap<string, string>();
            foreach (var decl in declarations)
            {
                var at = decl.IndexOf(':');
                if (at <= 0) continue;
                var prop = Js.ToLowerCase(Js.Trim(decl[..at]));
                var value = Js.Trim(ReImportant.Replace(decl[(at + 1)..], ""));
                if (value == "" || ReUnsafe.IsMatch(value)) continue;
                if (ShapeProps.Contains(prop)) shape.Set(prop, value);
                if (TextProps.Contains(prop)) text.Set(prop == "color" ? "fill" : prop, value);
            }
            if (shape.Count == 0 && text.Count == 0) return None;
            var shapeCss = "";
            var lineCss = "";
            foreach (var (prop, value) in shape.Entries)
            {
                shapeCss += $"{prop}:{value};";
                if (prop != "fill" && prop != "fill-opacity") lineCss += $"{prop}:{value};";
            }
            var textCss = "";
            foreach (var (prop, value) in text.Entries) textCss += $"{prop}:{value};";
            var weight = text.Get("font-weight");
            var size = text.Get("font-size");
            double? px = !string.IsNullOrEmpty(size) && RePx.IsMatch(size) ? Js.ParseFloat(size) : null;
            return new(
                shapeCss != "" ? $" style=\"{Builder.Esc(shapeCss)}\"" : "",
                lineCss != "" ? $" style=\"{Builder.Esc(lineCss)}\"" : "",
                textCss != "" ? $" style=\"{Builder.Esc(textCss)}\"" : "",
                shape.Get("stroke"),
                weight is "bold" or "bolder" || (weight is not null && Js.ToNumber(weight) >= 600),
                text.Get("font-style") == "italic",
                px is { } p && p > 0 && p <= 200 ? p : null);
        }

        static readonly Regex ReClass = new("[^A-Za-z0-9_-]+");

        public static string ClassNames(string raw)
        {
            var cleaned = Js.Trim(ReClass.Replace(raw, " "));
            return cleaned == "" ? "" : " " + cleaned;
        }

        static readonly string[] Series = ["#4c78a8", "#f58518", "#54a24b", "#e45756", "#72b7b2", "#eeca3b", "#b279a2", "#9d755d"];

        // Color for the nth data series in a chart, cycling through eight tokens.
        public static string SeriesColor(int index)
        {
            var k = ((index % 8) + 8) % 8;
            return $"var(--pele-series-{k + 1},{Series[k]})";
        }
    }
}
