using System.Globalization;
using System.Text;

namespace Archify.Net;

internal static partial class Pele
{
    // svg/builder.ts
    internal static class Builder
    {
        // Escapes text for XML and drops the control characters XML does not allow (builder.ts:8-15).
        // Deviation UPSTREAM-008 (PORT-008 Task 11): upstream drops only those controls and DEL, so U+FFFE, U+FFFF and a lone
        // surrogate half (typed, or from an entity such as `#65534;`) reach the SVG, which is then not well-formed XML. The
        // port drops them too, here, where every text and attribute value the SVG holds passes. Text is measured before it
        // is escaped, so the layout stays upstream's.
        public static string Esc(string text)
        {
            var sb = new StringBuilder(text.Length);
            for (var k = 0; k < text.Length; k++)
            {
                var c = text[k];
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case <= '\u0008' or '\u000b' or '\u000c' or >= '\u000e' and <= '\u001f' or '\u007f' or '\ufffe' or '\uffff': break;
                    case >= '\ud800' and <= '\udbff' when k + 1 < text.Length && char.IsLowSurrogate(text[k + 1]): sb.Append(c).Append(text[++k]); break;
                    case >= '\ud800' and <= '\udfff': break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        // Escapes text that is only ever read as text: entity codes are turned into their characters first.
        public static string EscText(string text) => Esc(Entities.DecodeEntities(text));

        // Drops the characters XML 1.0 forbids (UPSTREAM-008) without HTML-escaping `&`, `<`, `>` or `"`, for plain-text names.
        internal static string StripXmlInvalid(string text)
        {
            var sb = new StringBuilder(text.Length);
            for (var k = 0; k < text.Length; k++)
            {
                var c = text[k];
                switch (c)
                {
                    case <= '\u0008' or '\u000b' or '\u000c' or >= '\u000e' and <= '\u001f' or '\u007f' or '\ufffe' or '\uffff': break;
                    case >= '\ud800' and <= '\udbff' when k + 1 < text.Length && char.IsLowSurrogate(text[k + 1]): sb.Append(c).Append(text[++k]); break;
                    case >= '\ud800' and <= '\udfff': break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        // The two decimals of a coordinate, by hundredths (builder.ts:27-28).
        static readonly string[] Fraction = [.. Enumerable.Range(0, 100).Select(i =>
            i == 0 ? "" : i % 10 == 0 ? "." + (i / 10).ToString(CultureInfo.InvariantCulture) : "." + i.ToString("00", CultureInfo.InvariantCulture))];

        // builder.ts:30-44: Math.round(v * 100) half up, then integer + fraction table; String(n / 100) from 1e11 hundredths.
        public static string Num(double value)
        {
            var n = Js.Round(value * 100);
            if (n == 0) return "0";
            if (n > 0)
            {
                if (n < 1e11)
                {
                    var q = Math.Floor(n / 100);
                    return ((long)q).ToString(CultureInfo.InvariantCulture) + Fraction[(int)(n - q * 100)];
                }
            }
            else if (n > -1e11)
            {
                var m = -n;
                var q = Math.Floor(m / 100);
                return "-" + ((long)q).ToString(CultureInfo.InvariantCulture) + Fraction[(int)(m - q * 100)];
            }
            return Js.Num(n / 100);
        }

        // Writes a measured label as SVG text centered on (cx, cy) (builder.ts:48-92; no span has an icon, Label.cs).
        public static string LabelSvg(Label label, double cx, double cy, string attrs)
        {
            var lines = label.Lines;
            if (lines.Count == 0) return "";
            var lh = label.LineHeight;
            var shift = label.Size * 0.35;
            var y = cy - label.Height / 2 + lh / 2 + shift;

            if (lines.Count == 1 && lines[0].Count == 1 && lines[0][0].Style == 0)
                return $"<text{attrs} x=\"{Num(cx)}\" y=\"{Num(y)}\" text-anchor=\"middle\">{Esc(lines[0][0].Text)}</text>";

            var shared = SharedStyle(label);
            attrs = TextStyle(attrs, shared);
            var body = new StringBuilder();
            foreach (var line in lines)
            {
                for (var j = 0; j < line.Count; j++)
                {
                    var span = line[j];
                    var pos = j == 0 ? $" x=\"{Num(cx)}\" y=\"{Num(y)}\"" : "";
                    body.Append($"<tspan{pos}{SpanStyle(span.Style & ~shared)}>{Esc(span.Text)}</tspan>");
                }
                if (line.Count == 0) body.Append($"<tspan x=\"{Num(cx)}\" y=\"{Num(y)}\"> </tspan>");
                y += lh;
            }
            return $"<text{attrs} text-anchor=\"middle\" xml:space=\"preserve\">{body}</text>";
        }

        const string Bold = " font-weight=\"var(--_w)\"";
        const string Italic = " font-style=\"italic\"";

        public static string SpanStyle(int style) =>
            ((style & (int)Style.Bold) != 0 ? Bold : "") + ((style & (int)Style.Italic) != 0 ? Italic : "");

        // The emphasis every span of a label has. It is written once, on the text element.
        public static int SharedStyle(Label label)
        {
            var shared = (int)(Style.Bold | Style.Italic);
            var any = false;
            foreach (var line in label.Lines)
                foreach (var span in line)
                {
                    if (span.Text == "") continue;
                    shared &= span.Style;
                    any = true;
                }
            return any ? shared : 0;
        }

        // Adds that emphasis to a text element's attributes, unless they give a weight of their own.
        public static string TextStyle(string attrs, int shared) =>
            attrs
            + ((shared & (int)Style.Bold) != 0 && !attrs.Contains(" font-weight=\"", StringComparison.Ordinal) ? Bold : "")
            + ((shared & (int)Style.Italic) != 0 && !attrs.Contains(" font-style=\"", StringComparison.Ordinal) ? Italic : "");
    }
}
