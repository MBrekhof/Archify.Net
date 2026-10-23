namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/sankey/lexer.ts
    internal static partial class Sankey
    {
        // `const enum T`. STUCK is not a Jison token: the reference lexer would return the same empty token forever from there.
        internal static class T
        {
            public const int End = 0, Stuck = 1, Sankey = 2, Newline = 3, Comma = 4, DQuote = 5, NonEscapedText = 6, EscapedText = 7, Eof = 8;
        }

        static readonly string[] TokenNames = ["$end", "STUCK", "SANKEY", "NEWLINE", "COMMA", "DQUOTE", "NON_ESCAPED_TEXT", "ESCAPED_TEXT", "EOF"];

        // `const enum S`
        const int Initial = 0, Csv = 1, EscapedText = 2;

        // RFC 4180 TEXTDATA is printable ASCII except the double quote and the comma.
        // Deviation (PORT-008 Task 11, S3, Martin 2026-10-23): upstream 1.3.0 keeps to ASCII, so `Café,Bar,1` or a CJK name is
        // an error. The port takes Pele 1.9.0's alphabet (PR #9, its `c0`), by code point: that ASCII, U+00A0-U+D7FF,
        // U+E000-U+FFFD and U+10000 up. Controls (C0, DEL, C1), lone surrogates and U+FFFE/U+FFFF stay outside.
        static bool IsTextData(int c) =>
            c is >= 32 and <= 126 && c != 34 && c != 44 || c is >= 0xa0 and <= 0xd7ff or >= 0xe000 and <= 0xfffd or >= 0x10000;

        // String.prototype.codePointAt: a pair is one code point; a lone surrogate is its own value.
        static int CodePointAt(string s, int i) =>
            char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? char.ConvertToUtf32(s[i], s[i + 1]) : s[i];

        public static (List<int> Types, List<string> Texts, List<int> Starts) Tokenize(string src)
        {
            var n = src.Length;
            var types = new List<int>();
            var texts = new List<string>();
            var starts = new List<int>();
            var stack = new Stack<int>();
            var state = Initial;
            var p = 0;

            void Emit(int type, int s, int e)
            {
                types.Add(type);
                texts.Add(src[s..e]);
                starts.Add(s);
                p = e;
            }
            // src.slice(p, p + word.length).toLowerCase() === word, with no word character after it.
            bool Keyword(string word) =>
                Js.ToLowerCase(src[p..Math.Min(n, p + word.Length)]) == word && !Chars.IsWord(Chars.CharAt(src, p + word.Length));

            while (p < n)
            {
                int c = src[p];
                var start = p;
                var q = p;
                if (state == EscapedText)
                {
                    if (c == 34 && Chars.CharAt(src, p + 1) != 34)
                    {
                        if (stack.Count > 0) state = stack.Pop();
                        Emit(T.DQuote, p, p + 1);
                        continue;
                    }
                    while (q < n)
                    {
                        var d = CodePointAt(src, q);
                        if (IsTextData(d) || d == 44 || d == 13 || d == 10) q += d > 0xffff ? 2 : 1;
                        else if (d == 34 && Chars.CharAt(src, q + 1) == 34) q += 2;
                        else break;
                    }
                    Emit(T.EscapedText, p, q);
                }
                else
                {
                    if (state == Initial && (c | 32) == 115)
                    {
                        var length = Keyword("sankey-beta") ? 11 : Keyword("sankey") ? 6 : 0;
                        if (length > 0)
                        {
                            stack.Push(state);
                            state = Csv;
                            Emit(T.Sankey, p, p + length);
                            continue;
                        }
                    }
                    if (c == 10 || (c == 13 && Chars.CharAt(src, p + 1) == 10))
                    {
                        Emit(T.Newline, p, c == 10 ? p + 1 : p + 2);
                        continue;
                    }
                    if (c == 44)
                    {
                        Emit(T.Comma, p, p + 1);
                        continue;
                    }
                    if (c == 34)
                    {
                        stack.Push(state);
                        state = EscapedText;
                        Emit(T.DQuote, p, p + 1);
                        continue;
                    }
                    while (q < n && CodePointAt(src, q) is var d && IsTextData(d)) q += d > 0xffff ? 2 : 1;
                    Emit(T.NonEscapedText, p, q);
                }
                // A character outside the CSV alphabet matches as empty text and is never consumed.
                if (q == start)
                {
                    Emit(T.Stuck, p, p);
                    return (types, texts, starts);
                }
            }

            Emit(state == EscapedText ? T.EscapedText : T.Eof, n, n);
            Emit(T.End, n, n);
            return (types, texts, starts);
        }
    }
}
