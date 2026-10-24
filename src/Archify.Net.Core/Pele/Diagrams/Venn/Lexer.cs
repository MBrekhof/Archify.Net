namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/venn/lexer.ts
    internal static partial class Venn
    {
        // `const enum T`
        internal static class T
        {
            public const int End = 0, Error = 1, Eof = 2, Newline = 3, Venn = 4, Title = 5, Set = 6, Union = 7, Text = 8, IndentText = 9,
                Style = 10, BracketLabel = 11, Numeric = 12, HexColor = 13, RgbColor = 14, RgbaColor = 15, Identifier = 16, String = 17,
                Comma = 18, Colon = 19;
        }

        static readonly string[] TokenNames =
        [
            "$end", "INVALID", "EOF", "NEWLINE", "VENN", "TITLE", "SET", "UNION", "TEXT", "INDENT_TEXT", "STYLE", "BRACKET_LABEL",
            "NUMERIC", "HEXCOLOR", "RGBCOLOR", "RGBACOLOR", "IDENTIFIER", "STRING", "COMMA", "COLON",
        ];

        static readonly (string Word, int Type)[] Keywords =
            [("venn-beta", T.Venn), ("set", T.Set), ("union", T.Union), ("text", T.Text), ("style", T.Style)];

        // Reads tokens one at a time, as Mermaid's Jison lexer for venn diagrams does: every rule ignores case, the first rule
        // that matches wins, and a keyword must end at a word boundary. An indented `text` line is a different token after a
        // set or union than anywhere else, and the parser is what knows which came before, so the lexer asks the db.
        internal sealed class VennLexer(string src, VennDb host)
        {
            // The text and position of the token `Next` returned last.
            public string Text = "";
            public int Start;
            int at;
            bool lineStart;
            bool ended;

            int Char(int i) => Chars.CharAt(src, i);

            bool Word(int at, string word)
            {
                for (var k = 0; k < word.Length; k++)
                {
                    var c = Char(at + k);
                    int w = word[k];
                    if (c != w && (c + 32 != w || c < 65 || c > 90)) return false;
                }
                return true;
            }

            bool Keyword(int at, string word) => Word(at, word) && !Chars.IsWord(Char(at + word.Length));

            // Deviation VN1 (PORT-008 Task 11): a keyword's word boundary (jison's `\b`) lets a `-` follow it, but IDENTIFIER
            // reads `-` as part of a name, so a name that starts with a keyword and a dash (`set-theory`, `text-books`) lexes
            // as the keyword and a stray `-`, and the chart fails. Such a keyword gives way to the name. A `-` that starts a
            // number (`-5`, `-.5`) is left to the keyword, as upstream: an indented `text-5` is a text named -5 there.
            bool DashName(int at) =>
                Char(at) == 45 && !(Chars.IsDigit(Char(at + 1)) || (Char(at + 1) == 46 && Chars.IsDigit(Char(at + 2))));

            // The end of `rgb(1, 2, 3)` or `rgba(1, 2, 3, 0.5)` whose parenthesis opens at `at`, or -1.
            int Color(int at, int parts)
            {
                var i = at + 1;
                for (var part = 0; part < parts; part++)
                {
                    while (Chars.IsSpace(Char(i))) i++;
                    var from = i;
                    while (Chars.IsDigit(Char(i)) || Char(i) == 46) i++;
                    if (i == from) return -1;
                    while (Chars.IsSpace(Char(i))) i++;
                    if (Char(i) != (part == parts - 1 ? 41 : 44)) return -1;
                    i++;
                }
                return i;
            }

            int LineEnd(int from)
            {
                var end = Js.IndexOf(src, "\n", from);
                return end == -1 ? src.Length : end;
            }

            public int Next()
            {
                var n = src.Length;
                while (true)
                {
                    var i = at;
                    Start = i;
                    Text = "";
                    if (i >= n)
                    {
                        if (ended) return T.End;
                        ended = true;
                        return T.Eof;
                    }
                    int c = src[i];
                    // A comment runs to the end of the line. Mermaid's second comment rule, `[^\}]\%\%[^\n]*`, also takes the
                    // character before it along, whatever that is. Deviation Q1 (PORT-008 Task 11): only whitespace (JS `\s`, a
                    // line break included) goes with the comment, as upstream; any other character stays and is read by the
                    // rules below (`set A%% c` keeps `A`, `[%%x]` is a label). `}` is not whitespace, so `}%%` stays an error.
                    if ((c == 37 && Char(i + 1) == 37 && Char(i + 2) != 123) || (Chars.IsSpace(c) && Char(i + 1) == 37 && Char(i + 2) == 37))
                    {
                        at = LineEnd(i + 2);
                        continue;
                    }
                    if (lineStart)
                    {
                        if (c is 32 or 9)
                        {
                            var j = i + 1;
                            while (Char(j) is 32 or 9) j++;
                            at = j;
                            if (Keyword(j, "text") && host.GetIndentMode())
                            {
                                // The word `text` itself is dropped; the indentation stands for it.
                                at = j + 4;
                                lineStart = false;
                                Text = src[i..j];
                                return T.IndentText;
                            }
                            continue;
                        }
                        if (c is not (10 or 13))
                        {
                            host.SetIndentMode(false);
                            lineStart = false;
                        }
                    }
                    if (c is 10 or 13)
                    {
                        i++;
                        while (Char(i) is 10 or 13) i++;
                        lineStart = true;
                        return Token(T.Newline, i);
                    }
                    if (c == 37 && Char(i + 1) == 37)
                    {
                        at = LineEnd(i);
                        continue;
                    }
                    if (c is 32 or 9)
                    {
                        i++;
                        while (Char(i) is 32 or 9) i++;
                        at = i;
                        continue;
                    }

                    if (((c | 32) >= 97 && (c | 32) <= 122) || c == 95)
                    {
                        // `"title"\s[^#\n;]+`. Deviation VN2 (PORT-008 Task 11): `\s` takes a line break too, so a `title` with
                        // nothing after it on its line takes the next line as its text, and the statement there is lost. Here the
                        // space after `title` is not a line break, and such a `title` is the parse error that `title` at the end of
                        // the text already is upstream.
                        if (Word(i, "title") && Chars.IsSpace(Char(i + 5)) && Char(i + 5) is not (10 or 13))
                        {
                            var end = i + 6;
                            while (end < n)
                            {
                                int ch = src[end];
                                if (ch is 35 or 10 or 59) break;
                                end++;
                            }
                            if (end > i + 6) return Token(T.Title, end);
                        }
                        foreach (var (word, type) in Keywords)
                            if (Keyword(i, word) && !DashName(i + word.Length)) return Token(type, i + word.Length);
                        if (Word(i, "rgb"))
                        {
                            var alpha = (Char(i + 3) | 32) == 97;
                            var open = i + (alpha ? 4 : 3);
                            var end = Char(open) == 40 ? Color(open, alpha ? 4 : 3) : -1;
                            if (end != -1) return Token(alpha ? T.RgbaColor : T.RgbColor, end);
                        }
                        i++;
                        while (Chars.IsWord(Char(i)) || Char(i) == 45) i++;
                        return Token(T.Identifier, i);
                    }
                    if (c == 91)
                    {
                        if (Char(i + 1) == 34)
                        {
                            var quote = Js.IndexOf(src, "\"", i + 2);
                            if (quote == -1 || Char(quote + 1) != 93) return T.Error;
                            at = quote + 2;
                            Text = src[(i + 2)..quote];
                            return T.BracketLabel;
                        }
                        var end = i + 1;
                        while (end < n && src[end] != 93 && src[end] != 34) end++;
                        if (end == i + 1 || Char(end) != 93) return T.Error;
                        at = end + 1;
                        Text = Js.Trim(src[(i + 1)..end]);
                        return T.BracketLabel;
                    }
                    if (Chars.IsDigit(c) || c is 46 or 43 or 45)
                    {
                        if (c is 43 or 45) i++;
                        var from = i;
                        while (Chars.IsDigit(Char(i))) i++;
                        if (i > from)
                        {
                            if (Char(i) == 46 && Chars.IsDigit(Char(i + 1)))
                            {
                                i += 2;
                                while (Chars.IsDigit(Char(i))) i++;
                            }
                        }
                        else if (Char(i) == 46 && Chars.IsDigit(Char(i + 1)))
                        {
                            i += 2;
                            while (Chars.IsDigit(Char(i))) i++;
                        }
                        else return T.Error;
                        return Token(T.Numeric, i);
                    }
                    if (c == 35)
                    {
                        var end = i + 1;
                        while (end < i + 9 && Chars.IsHex(Char(end))) end++;
                        return end >= i + 4 ? Token(T.HexColor, end) : T.Error;
                    }
                    if (c == 34)
                    {
                        var quote = Js.IndexOf(src, "\"", i + 1);
                        return quote == -1 ? T.Error : Token(T.String, quote + 1);
                    }
                    if (c == 44) return Token(T.Comma, i + 1);
                    if (c == 58) return Token(T.Colon, i + 1);
                    return T.Error;
                }
            }

            int Token(int type, int end)
            {
                Text = src[at..end];
                at = end;
                return type;
            }
        }
    }
}
