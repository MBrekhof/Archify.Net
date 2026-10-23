using System.Text.RegularExpressions;

namespace Archify.Net;

internal static partial class Pele
{
    // text/label.ts Span. `style` is the Style bits as a number. No span ever carries an icon: archify passes no icon
    // resolver, so iconMarkup() is always '' (icons.ts:21-22) and the icon branches of label.ts and builder.ts are unreachable.
    internal sealed class Span(string text, int style, double width)
    {
        public string Text { get; set; } = text;
        public int Style { get; } = style;
        public double Width { get; set; } = width;
    }

    internal sealed record Label(List<List<Span>> Lines, List<double> Widths, double Width, double Height, double Size, double LineHeight);

    // text/label.ts
    internal static class Labels
    {
        public const double LineHeight = 1.5;

        // /<br\s*\/?>|\\n|\n/gi: the `i` flag folds the `n` of a literal backslash-n too.
        static readonly Regex ReBreak = new($@"<[bB][rR]{Chars.Ws}*/?>|\\[nN]|\n");
        // /<\/?([a-zA-Z][\w-]*)(?:\s[^<>]*)?\/?>/y
        static readonly Regex ReTag = new($@"\G</?([a-zA-Z][A-Za-z0-9_-]*)(?:{Chars.Ws}[^<>]*)?/?>");
        static readonly Regex ReIcon = new("(fa[bklrs]?):fa-([A-Za-z0-9_-]+)");
        static readonly Regex ReMarkup = new(@"[<\\\n*_]|fa[bklrs]?:fa-");

        static Label Empty() => new([], [], 0, 0, 0, 0);

        static string Dedent(string text)
        {
            var lines = text.Split('\n');
            var min = int.MaxValue;   // Infinity
            foreach (var line in lines)
            {
                if (Js.Trim(line) == "") continue;
                var indent = line.Length - Js.TrimStart(line).Length;
                if (indent < min) min = indent;
            }
            if (min == int.MaxValue || min == 0) return text;
            return string.Join("\n", lines.Select(line => line[Math.Min(min, line.Length - Js.TrimStart(line).Length)..]));
        }

        static void PushText(List<Span> line, string text, int style)
        {
            if (text == "") return;
            var decoded = Entities.DecodeEntities(text);
            var last = line.Count > 0 ? line[^1] : null;
            if (last is not null && last.Style == style) last.Text += decoded;
            else line.Add(new(decoded, style, 0));
        }

        // label.ts:50-73 with no resolver: an icon name is dropped with the space after it, or, at the end of the text, the
        // text before it is trimEnd-ed (review M2).
        static void PushIcons(List<Span> line, string text, int style)
        {
            if (!text.Contains(":fa-", StringComparison.Ordinal))
            {
                PushText(line, text, style);
                return;
            }
            var last = 0;
            for (var m = ReIcon.Match(text); m.Success; m = m.NextMatch())
            {
                PushText(line, text[last..m.Index], style);
                last = m.Index + m.Length;
                if (Chars.CharAt(text, last) == ' ')
                {
                    last++;
                }
                else if (last == text.Length)
                {
                    var before = line.Count > 0 ? line[^1] : null;
                    if (before is not null) before.Text = Js.TrimEnd(before.Text);
                }
            }
            PushText(line, text[last..], style);
        }

        // Splits inline HTML and, for markdown labels, emphasis markers into styled spans.
        static void Inline(List<Span> line, string source, bool markdown, int @base)
        {
            var style = @base;
            var marked = 0;
            var text = new System.Text.StringBuilder();
            var i = 0;
            var n = source.Length;
            void Flush()
            {
                PushIcons(line, text.ToString(), style);
                text.Clear();
            }
            while (i < n)
            {
                var ch = source[i];
                if (ch == '<')
                {
                    var m = ReTag.Match(source, i);
                    if (m.Success)
                    {
                        var tag = m.Groups[1].Value.ToLowerInvariant();   // ASCII only ([a-zA-Z][\w-]*)
                        var closing = Chars.CharAt(source, i + 1) == '/';
                        var bit = tag is "b" or "strong" ? (int)Style.Bold : tag is "i" or "em" ? (int)Style.Italic : 0;
                        if (bit != 0)
                        {
                            Flush();
                            style = closing ? (style & ~bit) | (@base & bit) : style | bit;
                        }
                        i += m.Length;
                        continue;
                    }
                }
                else if (markdown && ch is '*' or '_')
                {
                    var dbl = Chars.CharAt(source, i + 1) == ch;
                    var bit = dbl ? (int)Style.Bold : (int)Style.Italic;
                    var len = dbl ? 2 : 1;
                    var open = (marked & bit) == 0;
                    var prev = i > 0 ? source[i - 1] : ' ';
                    var next = i + len < n ? source[i + len] : ' ';
                    var canOpen = open && !Chars.IsSpace(next) && Js.IndexOf(source, dbl ? new string(ch, 2) : ch.ToString(), i + len) != -1;
                    var canClose = !open && !Chars.IsSpace(prev);
                    var wordy = ch == '_' && Chars.IsWord(prev) && Chars.IsWord(next);
                    if ((canOpen || canClose) && !wordy)
                    {
                        Flush();
                        marked = canOpen ? marked | bit : marked & ~bit;
                        style = canOpen ? style | bit : (style & ~bit) | (@base & bit);
                        i += len;
                        continue;
                    }
                }
                text.Append(ch);
                i++;
            }
            Flush();
        }

        static double MeasureSpans(List<Span> line, double size)
        {
            double total = 0;
            foreach (var span in line)
            {
                span.Width = Measurer.Width(span.Text, size, (Style)span.Style);
                total += span.Width;
            }
            return total;
        }

        // span.text.match(/\s*\S+\s*|\s+/g) ?? []
        static List<string> Pieces(string s)
        {
            var pieces = new List<string>();
            for (var p = 0; p < s.Length;)
            {
                var j = p;
                while (j < s.Length && Chars.IsSpace(s[j])) j++;
                if (j < s.Length)
                {
                    while (j < s.Length && !Chars.IsSpace(s[j])) j++;
                    while (j < s.Length && Chars.IsSpace(s[j])) j++;
                }
                pieces.Add(s[p..j]);
                p = j;
            }
            return pieces;
        }

        static void Wrap(List<Span> line, double size, double max, List<List<Span>> output, List<double> widths)
        {
            var current = new List<Span>();
            double width = 0;
            void Commit()
            {
                var last = current.Count > 0 ? current[^1] : null;
                if (last is not null)
                {
                    var trimmed = Js.TrimEnd(last.Text);
                    if (trimmed != last.Text)
                    {
                        last.Text = trimmed;
                        var w = Measurer.Width(trimmed, size, (Style)last.Style);
                        width += w - last.Width;
                        last.Width = w;
                    }
                }
                output.Add(current);
                widths.Add(width);
                current = [];
                width = 0;
            }
            foreach (var span in line)
            {
                foreach (var piece in Pieces(span.Text))
                {
                    var part = piece;
                    if (width > 0 && width + Measurer.Width(Js.TrimEnd(part), size, (Style)span.Style) > max)
                    {
                        Commit();
                        part = Js.TrimStart(part);
                    }
                    var last = current.Count > 0 ? current[^1] : null;
                    if (last is not null && last.Style == span.Style)
                    {
                        last.Text += part;
                        last.Width = Measurer.Width(last.Text, size, (Style)last.Style);
                        width = 0;
                        foreach (var s in current) width += s.Width;
                    }
                    else
                    {
                        var w = Measurer.Width(part, size, (Style)span.Style);
                        current.Add(new(part, span.Style, w));
                        width += w;
                    }
                }
            }
            if (current.Count > 0 || output.Count == 0) Commit();
        }

        public static Label LayoutLabel(string? raw, bool markdown, double size, double maxWidth, int @base = 0)
        {
            if (string.IsNullOrEmpty(raw)) return Empty();
            var lineHeight = Js.Round(size * LineHeight);

            if (!ReMarkup.IsMatch(raw) && !raw.Contains('&') && !raw.Contains('ﬂ'))
            {
                var text = Js.Trim(raw);
                var width = Measurer.Width(text, size, (Style)@base);
                if (width <= maxWidth || !text.Contains(' ')) return new([[new(text, @base, width)]], [width], width, lineHeight, size, lineHeight);
                var wrapped = new List<List<Span>>();
                var wrappedWidths = new List<double>();
                Wrap([new(text, @base, width)], size, maxWidth, wrapped, wrappedWidths);
                double widest = 0;
                foreach (var w in wrappedWidths) if (w > widest) widest = w;
                return new(wrapped, wrappedWidths, widest, wrapped.Count * lineHeight, size, lineHeight);
            }

            var source = markdown ? Js.Trim(Dedent(raw.Replace("\r\n", "\n").Replace('\r', '\n'))) : Js.Trim(raw);
            var lines = new List<List<Span>>();
            var widths = new List<double>();
            double widestLine = 0;
            foreach (var part in ReBreak.Split(source))
            {
                var line = new List<Span>();
                Inline(line, Js.Trim(part), markdown, @base);
                var w = MeasureSpans(line, size);
                if (w > maxWidth)
                {
                    Wrap(line, size, maxWidth, lines, widths);
                }
                else
                {
                    lines.Add(line);
                    widths.Add(w);
                }
            }
            foreach (var w in widths) if (w > widestLine) widestLine = w;
            return new(lines, widths, widestLine, lines.Count * lineHeight, size, lineHeight);
        }
    }
}
