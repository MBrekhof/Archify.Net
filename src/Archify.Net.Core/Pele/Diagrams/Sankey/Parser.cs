using System.Text;

namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/sankey/parser.ts
    internal static partial class Sankey
    {
        // JS \s without the line breaks \n and \r (Mermaid's [^\S\n\r]).
        static bool IsBlank(int c) => c is not (10 or 13) && Js.IsSpace(c);

        // Mermaid's sankeyUtils: strip blanks at both ends of the text, then collapse runs of line breaks so that no row is
        // empty, then trim.
        // Deviation (PORT-008 Task 11): Mermaid's first step, commented "remove all trailing spaces for each row", has no `m`
        // flag, so it strips only the ends of the whole text, which the final trim does anyway. A blank row, a header
        // `sankey ` with a trailing space, a tab-indented row or a quoted field with blanks at its row's edge is then a parse
        // error. The port strips blanks at the start and end of every row, where rows end at a line break outside a quoted
        // field (quotes as the lexer reads them: `"` opens, `""` inside is a quote, a lone `"` closes). Text inside quotes is
        // kept as written, and so is a run of blanks next to a comma, which is a whole field (`a,b,   ` reads as Node reads
        // it, an empty value). Each run is held until what follows it is known. One pass, with the collapse of line breaks
        // done as it goes.
        public static string PrepareTextForParsing(string text)
        {
            var start = 0;
            var end = text.Length;
            while (start < end && IsBlank(text[start])) start++;
            while (end > start && IsBlank(text[end - 1])) end--;

            var output = new StringBuilder(end - start);
            var quoted = false;
            var rowStart = true;   // outside quotes, no character of the row kept yet
            var blanks = -1;       // where a run of blanks outside quotes began
            var lastBreak = false;
            var last = '\0';       // the last character kept
            void Put(char c)
            {
                var isBreak = c is '\n' or '\r';
                if (isBreak && lastBreak) return;   // .replace(/[\n\r]+/g, '\n')
                last = isBreak ? '\n' : c;
                output.Append(last);
                lastBreak = isBreak;
            }
            void Flush(int to)
            {
                for (var k = blanks; k < to; k++) Put(text[k]);
                blanks = -1;
            }
            for (var i = start; i < end; i++)
            {
                var c = text[i];
                if (quoted)
                {
                    Put(c);
                    if (c == '"')
                    {
                        if (i + 1 < end && text[i + 1] == '"') Put(text[++i]);
                        else quoted = false;
                    }
                    continue;
                }
                if (IsBlank(c))
                {
                    if (blanks < 0) blanks = i;
                    continue;
                }
                if (c is '\n' or '\r')
                {
                    // A row's trailing run goes, unless it is the last field.
                    if (blanks >= 0 && !rowStart && last == ',') Flush(i);
                    blanks = -1;
                    rowStart = true;
                    Put(c);
                    continue;
                }
                // A row's leading run goes, unless it is the first field; a run inside a row stays.
                if (blanks >= 0 && (!rowStart || c == ',')) Flush(i);
                blanks = -1;
                rowStart = false;
                Put(c);
                if (c == '"') quoted = true;
            }
            return Js.Trim(output.ToString());
        }

        public static void Parse(string src, SankeyDb db)
        {
            var (types, texts, starts) = Tokenize(src);
            var i = 0;

            // STUCK always follows the empty token it stopped at, so i - 1 is a token.
            PeleException Fail(string expected)
            {
                var t = types[i];
                var got = t == T.Stuck ? TokenNames[types[i - 1]] : TokenNames[t];
                return Errors.SyntaxError("sankey", src, starts[i], $"Expecting {expected}, got '{got}'");
            }

            void Expect(int type, string also = "")
            {
                if (types[i] != type) throw Fail($"'{TokenNames[type]}'{also}");
                i++;
            }

            // Every read stops at END or STUCK, the last token, so no index goes past the end.
            string Field()
            {
                if (types[i] == T.NonEscapedText) return texts[i++];
                if (types[i] != T.DQuote) throw Fail("'DQUOTE', 'NON_ESCAPED_TEXT'");
                i++;
                if (types[i] != T.EscapedText) throw Fail("'ESCAPED_TEXT'");
                var text = texts[i++];
                Expect(T.DQuote);
                return text;
            }

            string Name() => Js.Trim(Field()).Replace("\"\"", "\"", StringComparison.Ordinal);

            Expect(T.Sankey);
            Expect(T.Newline);
            while (true)
            {
                var from = Name();
                Expect(T.Comma, ", 'NEWLINE', 'EOF'");
                var to = Name();
                Expect(T.Comma, ", 'NEWLINE', 'EOF'");
                var amount = Field();
                var source = db.FindOrCreateNode(from);
                var target = db.FindOrCreateNode(to);
                db.AddLink(source, target, Js.ParseFloat(Js.Trim(amount)));
                if (types[i] != T.Newline) break;
                i++;
            }
            if (types[i] == T.Eof) i++;
            if (types[i] != T.End) throw Fail("'NEWLINE', 'EOF'");
        }
    }
}
