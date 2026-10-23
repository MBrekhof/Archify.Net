namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/xychart/lexer.ts
    internal static partial class XyChart
    {
        internal sealed record XyTokens(List<int> Types, List<string> Texts, List<int> Starts);

        // lexer.ts `const enum S`
        const int SInitial = 0, SAxisData = 1, SAxisBandData = 2, SData = 3, SDataInner = 4, SString = 5, SAccTitle = 6, SAccDescr = 7,
            SAccDescrMultiline = 8, SMdString = 9;

        // The token each state gives for the empty rest of the input, where it gives one (md_string gives none: its only text
        // rule cannot match an empty rest).
        static readonly int[] AtEnd = [T.Eof, T.Eof, T.Eof, T.Eof, T.Eof, T.Str, T.AccTitleValue, T.AccDescrValue, T.AccDescrMultilineValue];

        // Deviation X3 (PORT-008 Task 11): xychart.jison:51-53 mean to read a markdown string, but a quoting slip turns the
        // opening rule into one that matches a fixed run of jison source, and Pele copies that (lexer.ts BROKEN_RULE). The port
        // reads markdown strings as the quadrant grammar does (quadrant lexer.ts): `"` then a backtick opens, a run without
        // backtick or `"` is MD_STR, and a backtick then `"` closes.
        static readonly int[] Single = BuildSingle();

        static int[] BuildSingle()
        {
            var single = new int[128];
            foreach (var (ch, type) in new[]
            {
                (':', T.Colon), ('+', T.Plus), (',', T.Comma), ('=', T.Eq), ('*', T.Mult), ('#', T.Brkt), ('_', T.Underscore),
                ('.', T.Dot), ('&', T.Amp), ('-', T.Minus), (';', T.Semi),
            })
                single[ch] = type;
            return single;
        }

        // JS indexOf(char, from): -1 past the end.
        static int Find(string s, char c, int from) => from >= s.Length ? -1 : s.IndexOf(c, from);

        public static XyTokens Tokenize(string src)
        {
            var n = src.Length;
            var types = new List<int>();
            var texts = new List<string>();
            var starts = new List<int>();
            var stack = new Stack<int>();
            var state = SInitial;
            var p = 0;

            void Emit(int type, int s, int e)
            {
                types.Add(type);
                texts.Add(src[s..e]);
                starts.Add(s);
                p = e;
            }
            void Push(int s)
            {
                stack.Push(state);
                state = s;
            }
            void Pop()
            {
                if (stack.Count > 0) state = stack.Pop();
            }
            int LineEnd(int q)
            {
                var e = Find(src, '\n', q);
                return e == -1 ? n : e;
            }
            int Run(int q)
            {
                while (q < n && Chars.IsSpace(src[q])) q++;
                return q;
            }
            int Digits(int q)
            {
                while (Chars.IsDigit(Chars.CharAt(src, q))) q++;
                return q;
            }
            bool Keyword(string word) => Caseless.WordAt(src, p, word) && !Chars.IsWord(Chars.CharAt(src, p + word.Length));
            bool AfterHeader() => types.Count > 0 && types[^1] == T.XyChart;

            while (p < n)
            {
                int c = src[p];

                if (state == SString)
                {
                    if (c == 34)
                    {
                        p++;
                        Pop();
                    }
                    else
                    {
                        var e = Find(src, '"', p);
                        Emit(T.Str, p, e == -1 ? n : e);
                    }
                    continue;
                }
                if (state == SMdString)
                {
                    if (c == 96 && Chars.CharAt(src, p + 1) == 34)
                    {
                        p += 2;
                        Pop();
                        continue;
                    }
                    if (c != 96 && c != 34)
                    {
                        var e = p + 1;
                        while (e < n && src[e] != 96 && src[e] != 34) e++;
                        Emit(T.MdStr, p, e);
                        continue;
                    }
                    break;
                }
                if (state == SAccTitle || state == SAccDescr)
                {
                    Emit(AtEnd[state], p, LineEnd(p));
                    Pop();
                    continue;
                }
                if (state == SAccDescrMultiline)
                {
                    if (c == 125)
                    {
                        p++;
                        Pop();
                    }
                    else
                    {
                        var e = Find(src, '}', p);
                        Emit(T.AccDescrMultilineValue, p, e == -1 ? n : e);
                    }
                    continue;
                }

                var next = Chars.CharAt(src, p + 1);
                if (c == 37 && next == 37)
                {
                    p = LineEnd(p + 2);
                    continue;
                }
                // Mermaid's second comment rule, `[^\}]\%\%[^\n]*`, takes any character before `%%` with it. Deviation Q1
                // (PORT-008 Task 11): only whitespace (JS `\s`, a line break included) goes with the comment, as upstream; any
                // other character stays and is read by the rules below (`ab c%%x` keeps `ab c`).
                if (Chars.IsSpace(c) && next == 37 && Chars.CharAt(src, p + 2) == 37)
                {
                    p = LineEnd(p + 3);
                    continue;
                }
                if (c == 10 || c == 13)
                {
                    // Only a line break that directly follows an axis or a series ends it; one after spaces is skipped with
                    // them, and the state carries on to the next line.
                    if ((state == SAxisData || state == SData) && (c == 10 || next == 10))
                    {
                        Emit(T.Newline, p, c == 10 ? p + 1 : p + 2);
                        Pop();
                        continue;
                    }
                    var e = p + 1;
                    for (var d = Chars.CharAt(src, e); d == 10 || d == 13; d = Chars.CharAt(src, ++e)) { }
                    Emit(T.Newline, p, e);
                    continue;
                }

                var matched = false;
                switch (c | 32)
                {
                    case 116:
                        if (Keyword("title"))
                        {
                            Emit(T.Title, p, p + 5);
                            matched = true;
                        }
                        break;
                    case 97:
                        if (Caseless.WordAt(src, p, "acc"))
                        {
                            var descr = Caseless.WordAt(src, p + 3, "descr");
                            if (descr || Caseless.WordAt(src, p + 3, "title"))
                            {
                                var k = Run(p + 8);
                                var d = Chars.CharAt(src, k);
                                if (d == 58)
                                {
                                    Emit(descr ? T.AccDescr : T.AccTitle, p, Run(k + 1));
                                    Push(descr ? SAccDescr : SAccTitle);
                                    matched = true;
                                }
                                else if (d == 123 && descr)
                                {
                                    p = Run(k + 1);
                                    Push(SAccDescrMultiline);
                                    matched = true;
                                }
                            }
                        }
                        break;
                    case 120:
                        if (Keyword("xychart-beta"))
                        {
                            Emit(T.XyChart, p, p + 12);
                            matched = true;
                        }
                        else if (Keyword("xychart"))
                        {
                            Emit(T.XyChart, p, p + 7);
                            matched = true;
                        }
                        else if (Keyword("x-axis"))
                        {
                            Emit(T.XAxis, p, p + 6);
                            Push(SAxisData);
                            matched = true;
                        }
                        break;
                    case 121:
                        if (Keyword("y-axis"))
                        {
                            Emit(T.YAxis, p, p + 6);
                            Push(SAxisData);
                            matched = true;
                        }
                        break;
                    // Deviation X2 (PORT-008 Task 11): upstream's `(?:"vertical"|"horizontal")` has no word boundary and no
                    // start condition, so `title verticality` fails. The orientation is a whole word straight after the chart
                    // keyword, with only spaces between (a line break is a NEWLINE token); anywhere else it is plain text.
                    case 118:
                        if (AfterHeader() && Keyword("vertical"))
                        {
                            Emit(T.ChartOrientation, p, p + 8);
                            matched = true;
                        }
                        break;
                    case 104:
                        if (AfterHeader() && Keyword("horizontal"))
                        {
                            Emit(T.ChartOrientation, p, p + 10);
                            matched = true;
                        }
                        break;
                    case 108:
                        if (Keyword("line"))
                        {
                            Emit(T.Line, p, p + 4);
                            Push(SData);
                            matched = true;
                        }
                        break;
                    case 98:
                        if (Keyword("bar"))
                        {
                            Emit(T.Bar, p, p + 3);
                            Push(SData);
                            matched = true;
                        }
                        break;
                }
                if (matched) continue;

                if (c == 91)
                {
                    if (state == SAxisData) Push(SAxisBandData);
                    else if (state == SData) Push(SDataInner);
                    Emit(T.SquareBracesStart, p, p + 1);
                    continue;
                }
                if (c == 93)
                {
                    if (state == SDataInner || state == SAxisBandData) Pop();
                    Emit(T.SquareBracesEnd, p, p + 1);
                    continue;
                }
                if (state == SAxisData && c == 45 && next == 45 && Chars.CharAt(src, p + 2) == 62)
                {
                    Emit(T.ArrowDelimiter, p, p + 3);
                    continue;
                }
                if (state == SAxisData || state == SDataInner)
                {
                    var q = c == 43 || c == 45 ? p + 1 : p;
                    var d = Chars.CharAt(src, q);
                    var e = -1;
                    if (Chars.IsDigit(d))
                    {
                        e = Digits(q + 1);
                        if (Chars.CharAt(src, e) == 46 && Chars.IsDigit(Chars.CharAt(src, e + 1))) e = Digits(e + 2);
                    }
                    else if (d == 46 && Chars.IsDigit(Chars.CharAt(src, q + 1)))
                    {
                        e = Digits(q + 2);
                    }
                    if (e != -1)
                    {
                        Emit(T.NumberWithDecimal, p, e);
                        continue;
                    }
                }
                if (c == 34)
                {
                    if (next == 96)
                    {
                        p += 2;
                        Push(SMdString);
                    }
                    else
                    {
                        p++;
                        Push(SString);
                    }
                    continue;
                }
                if (Chars.IsLetter(c))
                {
                    var e = p + 1;
                    while (Chars.IsLetter(Chars.CharAt(src, e))) e++;
                    Emit(T.Alpha, p, e);
                    continue;
                }
                if (Chars.IsDigit(c))
                {
                    Emit(T.Num, p, Digits(p + 1));
                    continue;
                }
                if (Chars.IsSpace(c))
                {
                    p = Run(p + 1);
                    continue;
                }
                if (c < 128 && Single[c] != 0)
                {
                    Emit(Single[c], p, p + 1);
                    continue;
                }
                break;
            }

            if (p < n)
            {
                types.Add(T.Error);
                texts.Add("");
                starts.Add(p);
                return new(types, texts, starts);
            }
            // Jison tries the rules of the current state once more against the empty rest of the input.
            if (state < AtEnd.Length)
            {
                types.Add(AtEnd[state]);
                texts.Add("");
                starts.Add(n);
            }
            types.Add(T.End);
            texts.Add("");
            starts.Add(n);
            return new(types, texts, starts);
        }
    }
}
