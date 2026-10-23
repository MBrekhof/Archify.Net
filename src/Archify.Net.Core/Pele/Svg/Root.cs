using System.Text.RegularExpressions;

namespace Archify.Net;

internal static partial class Pele
{
    // svg/root.ts
    internal static class Root
    {
        // A rectangle's corner radius comes from a variable, written in a style rather than in `rx` (root.ts:5-21).
        const string Rounded = " rx=\"" + Theme.Radius + "\"";
        // /<rect\b[^>]*>/g: the `\b` follows a word character, so it means "no word character next".
        static readonly Regex ReRounded = new("<rect(?![A-Za-z0-9_])[^>]*>");

        static string RoundedRects(string content)
        {
            if (!content.Contains(Rounded, StringComparison.Ordinal)) return content;
            return ReRounded.Replace(content, m =>
            {
                var tag = m.Value;
                var at = tag.IndexOf(Rounded, StringComparison.Ordinal);
                if (at < 0) return tag;
                var rest = tag[..at] + tag[(at + Rounded.Length)..];
                var style = rest.IndexOf(" style=\"", StringComparison.Ordinal);
                if (style >= 0) return $"{rest[..(style + 8)]}rx:var(--_r);{rest[(style + 8)..]}";
                var end = rest.EndsWith("/>", StringComparison.Ordinal) ? rest.Length - 2 : rest.Length - 1;
                return $"{rest[..end]} style=\"rx:var(--_r)\"{rest[end..]}";
            });
        }

        // The weights of bold text, each set only in a diagram that has such text.
        static readonly (string Alias, string Name)[] Weights = [("--_tw", "title"), ("--_hw", "heading"), ("--_w", "bold")];

        // root.ts:35-67. archify passes no idPrefix (so "pele"), no fontFamily; `responsive` comes from Pele.Render.
        public static string SvgDocument(string type, double width, double height, double fontSize, PeleRenderContext context, string? accTitle, string? accDescr, string content)
        {
            const string prefix = "pele";
            var head = "";
            var aria = "";
            if (!string.IsNullOrEmpty(accTitle))
            {
                head += $"<title id=\"{prefix}-title\">{Builder.EscText(accTitle)}</title>";
                aria += $" aria-labelledby=\"{prefix}-title\"";
            }
            if (!string.IsNullOrEmpty(accDescr))
            {
                head += $"<desc id=\"{prefix}-desc\">{Builder.EscText(accDescr)}</desc>";
                aria += $" aria-describedby=\"{prefix}-desc\"";
            }
            var fit = context.Responsive ? "max-width:100%;height:auto;" : "";
            content = RoundedRects(content);
            var weights = content.Contains("var(--_r)", StringComparison.Ordinal) ? $";--_r:{Theme.Radius}" : "";
            foreach (var (alias, name) in Weights)
                if (content.Contains($"var({alias})", StringComparison.Ordinal)) weights += $";{alias}:var(--pele-{name}-weight,bold)";
            var w = Js.Num(width);
            var h = Js.Num(height);
            return $"<svg xmlns=\"http://www.w3.org/2000/svg\" class=\"pele pele-{type}\" viewBox=\"0 0 {w} {h}\" width=\"{w}\" height=\"{h}\" style=\"{fit}{Theme.RootStyle}{weights}{Builder.Esc(Theme.FontStyle(fontSize))}\" fill=\"var(--_fg)\" role=\"graphics-document document\" aria-roledescription=\"{type}\"{aria}>"
                + head + content + "</svg>";
        }
    }
}
