namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/quadrant/lexer.ts
    internal static partial class Quadrant
    {
        internal sealed record QuadrantTokens(List<int> Types, List<string> Texts, List<int> Starts);

        // lexer.ts `const enum S`
        const int SInitial = 0, STitle = 1, SAccTitle = 2, SAccDescr = 3, SAccDescrMultiline = 4, SString = 5, SMdString = 6, SClassName = 7,
            SPointStart = 8, SPointX = 9, SPointY = 10;

        // The token each state gives for the empty rest of the input, where it gives one.
        static readonly int[] AtEnd = [T.Eof, T.TitleValue, T.AccTitleValue, T.AccDescrValue, T.AccDescrMultilineValue, T.Str];

        static readonly int[] Single = BuildSingle();

        static int[] BuildSingle()
        {
            var single = new int[128];
            foreach (var (chars, type) in new[]
            {
                (":", T.Colon), ("+", T.Plus), (",", T.Comma), ("=", T.Eq), ("*", T.Mult), ("#", T.Brkt), ("_", T.Underscore), (".", T.Dot),
                ("&", T.Amp), ("-", T.Minus), (" \t\v\f", T.Space), (";", T.Semi), ("!$%'`?\\/", T.Punctuation),
            })
                foreach (var ch in chars) single[ch] = type;
            return single;
        }

        // JS indexOf(char, from): -1 past the end.
        static int Find(string s, char c, int from) => from >= s.Length ? -1 : s.IndexOf(c, from);

        public static QuadrantTokens Tokenize(string src)
        {
            var n = src.Length;
            var types = new List<int>();
            var texts = new List<string>();
            var starts = new List<int>();
            var stack = new Stack<int>();
            var state = SInitial;
            var p = 0;
            // The last run scanned of each kind, so that a long run is not scanned again from each of its characters.
            int spaceFrom = 0, spaceTo = 0, dashFrom = 0, dashTo = 0, wsFrom = 0, wsTo = 0, gapFrom = 0, gapTo = 0;

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
            int Spaces(int q)
            {
                while (Chars.CharAt(src, q) == 32) q++;
                return q;
            }
            int Run(int q)
            {
                while (q < n && Chars.IsSpace(src[q])) q++;
                return q;
            }
            // Mermaid's `(1)|(0(.\d+)?)`, where the dot is any character within a line.
            int Coordinate(int q)
            {
                var c = Chars.CharAt(src, q);
                if (c == 49) return q + 1;
                if (c != 48) return -1;
                var e = q + 1;
                if (e < n && !Chars.IsLineEnd(src[e]) && Chars.IsDigit(Chars.CharAt(src, e + 1)))
                {
                    e += 2;
                    while (Chars.IsDigit(Chars.CharAt(src, e))) e++;
                }
                return e;
            }

            while (p < n)
            {
                int c = src[p];
                var next = Chars.CharAt(src, p + 1);

                switch (state)
                {
                    case SInitial:
                        break;

                    case STitle:
                    case SAccTitle:
                    case SAccDescr:
                        Emit(AtEnd[state], p, LineEnd(p));
                        Pop();
                        continue;

                    case SAccDescrMultiline:
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

                    case SString:
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

                    case SMdString:
                        if (c == 96 && next == 34)
                        {
                            p += 2;
                            Pop();
                            continue;
                        }
                        if (c != 96 && c != 34)
                        {
                            var e = p + 1;
                            for (var d = Chars.CharAt(src, e); e < n && d != 96 && d != 34; d = Chars.CharAt(src, ++e)) { }
                            Emit(T.MdStr, p, e);
                            continue;
                        }
                        goto Done;

                    case SClassName:
                        if (Chars.IsWord(c))
                        {
                            var e = p + 1;
                            while (Chars.IsWord(Chars.CharAt(src, e))) e++;
                            Emit(T.ClassName, p, e);
                            Pop();
                            continue;
                        }
                        goto Done;

                    case SPointStart:
                    {
                        var e = Coordinate(p);
                        if (e != -1)
                        {
                            Emit(T.PointX, p, e);
                            Push(SPointX);
                            continue;
                        }
                        var k = Run(p);
                        if (Chars.CharAt(src, k) == 93)
                        {
                            p = Spaces(k + 1);
                            Pop();
                            continue;
                        }
                        goto Done;
                    }

                    case SPointX:
                    {
                        var k = Run(p);
                        if (Chars.CharAt(src, k) == 44)
                        {
                            p = Run(k + 1);
                            state = SPointY;
                            continue;
                        }
                        goto Done;
                    }

                    case SPointY:
                    {
                        var e = Coordinate(p);
                        if (e != -1)
                        {
                            Emit(T.PointY, p, e);
                            Pop();
                            continue;
                        }
                        goto Done;
                    }
                }

                if (c == 37 && next == 37)
                {
                    p = LineEnd(p + 2);
                    continue;
                }
                // Mermaid's second comment rule takes any character before `%%` with it, a line break included.
                if (c != 125 && next == 37 && Chars.CharAt(src, p + 2) == 37)
                {
                    p = LineEnd(p + 3);
                    continue;
                }
                if (c == 10 || c == 13)
                {
                    var e = p + 1;
                    for (var d = Chars.CharAt(src, e); d == 10 || d == 13; d = Chars.CharAt(src, ++e)) { }
                    Emit(T.Newline, p, e);
                    continue;
                }

                var lower = c | 32;
                if (lower == 116 && Caseless.WordAt(src, p, "title") && !Chars.IsWord(Chars.CharAt(src, p + 5)))
                {
                    Emit(T.Title, p, p + 5);
                    Push(STitle);
                    continue;
                }
                if (lower == 97 && Caseless.WordAt(src, p, "acc"))
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
                            continue;
                        }
                        if (d == 123 && descr)
                        {
                            p = Run(k + 1);
                            Push(SAccDescrMultiline);
                            continue;
                        }
                    }
                }

                // Axis, quadrant and chart keywords and the axis arrow take the spaces around them.
                var j = p;
                if (c == 32)
                {
                    if (p < spaceFrom || p >= spaceTo)
                    {
                        spaceFrom = p;
                        spaceTo = Spaces(p);
                    }
                    j = spaceTo;
                }
                {
                    var d = Chars.CharAt(src, j);
                    var dl = d | 32;
                    var type = 0;
                    var end = 0;
                    if (dl == 120 && Caseless.WordAt(src, j, "x-axis"))
                    {
                        type = T.XAxis;
                        end = j + 6;
                    }
                    else if (dl == 121 && Caseless.WordAt(src, j, "y-axis"))
                    {
                        type = T.YAxis;
                        end = j + 6;
                    }
                    else if (d == 45)
                    {
                        if (j < dashFrom || j >= dashTo)
                        {
                            dashFrom = j;
                            dashTo = j;
                            while (Chars.CharAt(src, dashTo) == 45) dashTo++;
                        }
                        if (dashTo - j >= 2 && Chars.CharAt(src, dashTo) == 62)
                        {
                            type = T.Delimiter;
                            end = dashTo + 1;
                        }
                    }
                    else if (dl == 113 && Caseless.WordAt(src, j, "quadrant"))
                    {
                        var q = Chars.CharAt(src, j + 9);
                        if (Chars.CharAt(src, j + 8) == 45 && q >= 49 && q <= 52)
                        {
                            type = T.Quadrant1 + q - 49;
                            end = j + 10;
                        }
                        else if (Caseless.WordAt(src, j + 8, "chart"))
                        {
                            type = T.QuadrantChart;
                            end = j + 13;
                        }
                    }
                    if (type != 0)
                    {
                        Emit(type, p, Spaces(end));
                        continue;
                    }
                }

                if (lower == 99 && Caseless.WordAt(src, p, "classdef") && !Chars.IsWord(Chars.CharAt(src, p + 8)))
                {
                    Emit(T.ClassDef, p, p + 8);
                    continue;
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
                if (c == 58 && next == 58 && Chars.CharAt(src, p + 2) == 58)
                {
                    p += 3;
                    Push(SClassName);
                    continue;
                }
                // A point's `: [`, with any whitespace before, between and after, line breaks included.
                if (c == 58 || Chars.IsSpace(c))
                {
                    var k = p;
                    if (c != 58)
                    {
                        if (p < wsFrom || p >= wsTo)
                        {
                            wsFrom = p;
                            wsTo = Run(p);
                        }
                        k = wsTo;
                    }
                    if (Chars.CharAt(src, k) == 58)
                    {
                        if (k + 1 < gapFrom || k + 1 >= gapTo)
                        {
                            gapFrom = k + 1;
                            gapTo = Run(k + 1);
                        }
                        if (Chars.CharAt(src, gapTo) == 91)
                        {
                            Emit(T.PointStart, p, Run(gapTo + 1));
                            Push(SPointStart);
                            continue;
                        }
                    }
                }

                if (Chars.IsLetter(c))
                {
                    var e = p + 1;
                    while (Chars.IsLetter(Chars.CharAt(src, e))) e++;
                    Emit(T.Alpha, p, e);
                    continue;
                }
                if (c >= 128)
                {
                    var e = p + 1;
                    while (Chars.CharAt(src, e) >= 128) e++;
                    Emit(T.UnicodeText, p, e);
                    continue;
                }
                if (Chars.IsDigit(c))
                {
                    var e = p + 1;
                    while (Chars.IsDigit(Chars.CharAt(src, e))) e++;
                    Emit(T.Num, p, e);
                    continue;
                }
                // c < 128 here: every character from 128 up is UNICODE_TEXT above.
                if (Single[c] != 0)
                {
                    Emit(Single[c], p, p + 1);
                    continue;
                }
                break;
            }
        Done:

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
