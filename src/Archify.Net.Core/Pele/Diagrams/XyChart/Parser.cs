namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/xychart/parser.ts
    internal static partial class XyChart
    {
        static bool[] Flags(params int[] tokens)
        {
            var output = new bool[T.MdStr + 1];
            foreach (var t in tokens) output[t] = true;
            return output;
        }

        static readonly bool[] Word = Flags(T.Amp, T.Num, T.Alpha, T.Plus, T.Eq, T.Mult, T.Dot, T.Brkt, T.Minus, T.Underscore);
        static readonly bool[] Eol = Flags(T.Newline, T.Semi, T.Eof);

        public static void Parse(string src, XyChartDb db)
        {
            var (types, texts, starts) = Tokenize(src);
            var i = 0;
            // types[i] past the end is undefined, which no flag or type matches.
            int TypeAt(int k) => k < types.Count ? types[k] : -1;
            bool Is(bool[] flags, int k) => TypeAt(k) is >= 0 and var t && flags[t];

            PeleException Fail(string expected)
            {
                var t = types[i];
                if (t == T.Error) return Errors.SyntaxError("xychart", src, starts[i], "", true);
                return Errors.SyntaxError("xychart", src, starts[i], $"Expecting {expected}, got '{TokenNames[t]}'");
            }

            string Expect(int type)
            {
                if (types[i] != type) throw Fail($"'{TokenNames[type]}'");
                return texts[i++];
            }

            bool IsText(int k) => TypeAt(k) is T.Str or T.MdStr || Is(Word, k);

            XyText Text()
            {
                if (!IsText(i)) throw Fail("'STR', 'ALPHA', 'NUM'");
                if (types[i] is T.Str or T.MdStr) return new(texts[i], types[i++] == T.MdStr);
                // Deviation X4 (PORT-008 Task 11): the grammar's alphaNum joins its tokens with '' (xychart.jison:171), so
                // `title Sales Report` reads `SalesReport`. The port joins them with the whitespace between them: a gap on one
                // line keeps its whitespace characters as written (spaces, tabs, NBSP), and a gap that holds a line break
                // (the lexer's whitespace skip takes one after a space) is one space (Martin, 2026-10-23). Non-whitespace in a
                // gap that gave no token (`""`, an empty markdown string, `accDescr{}`) is dropped, as upstream drops it, while
                // any whitespace inside `accDescr{}` stays part of the gap. The db trims the ends.
                var text = new System.Text.StringBuilder(texts[i]);
                var end = starts[i] + texts[i].Length;
                for (i++; Is(Word, i); i++)
                {
                    var spaces = text.Length;
                    var lineBreak = false;
                    for (var k = end; k < starts[i]; k++)
                    {
                        if (src[k] is '\n' or '\r') lineBreak = true;
                        else if (Chars.IsSpace(src[k])) text.Append(src[k]);
                    }
                    if (lineBreak)
                    {
                        text.Length = spaces;
                        text.Append(' ');
                    }
                    text.Append(texts[i]);
                    end = starts[i] + texts[i].Length;
                }
                return new(text.ToString());
            }

            (double, double) Range()
            {
                var min = Js.ToNumber(Expect(T.NumberWithDecimal));
                Expect(T.ArrowDelimiter);
                return (min, Js.ToNumber(Expect(T.NumberWithDecimal)));
            }

            List<DataPoint> Points()
            {
                var output = new List<DataPoint>();
                Expect(T.SquareBracesStart);
                for (; ; )
                {
                    var value = Js.ToNumber(Expect(T.NumberWithDecimal));
                    // A markdown label is taken as its text (X3); the grammar's dataPoint names only STR.
                    output.Add(new(value, TypeAt(i) is T.Str or T.MdStr ? texts[i++] : ""));
                    if (TypeAt(i) != T.Comma) break;
                    i++;
                }
                Expect(T.SquareBracesEnd);
                return output;
            }

            for (; ; )
            {
                if (Is(Eol, i)) i++;
                else if (TypeAt(i) != T.XyChart) break;
                else if (TypeAt(++i) == T.ChartOrientation) db.SetOrientation(texts[i++]);
            }

            while (types[i] != T.End)
            {
                var t = types[i++];
                switch (t)
                {
                    case T.Title:
                        db.SetDiagramTitle(Js.Trim(Text().Text));
                        break;
                    case T.XAxis:
                    case T.YAxis:
                    {
                        var title = IsText(i) ? Text() : null;
                        if (t == T.XAxis && TypeAt(i) == T.SquareBracesStart)
                        {
                            var categories = new List<XyText>();
                            do
                            {
                                i++;
                                categories.Add(Text());
                            } while (TypeAt(i) == T.Comma);
                            Expect(T.SquareBracesEnd);
                            db.SetXAxisBand(categories);
                        }
                        else if (TypeAt(i) == T.NumberWithDecimal || title is null)
                        {
                            var (min, max) = Range();
                            if (t == T.XAxis) db.SetXAxisRangeData(min, max);
                            else db.SetYAxisRangeData(min, max);
                        }
                        if (t == T.XAxis) db.SetXAxisTitle(title ?? new(""));
                        else db.SetYAxisTitle(title ?? new(""));
                        break;
                    }
                    case T.Line:
                    case T.Bar:
                    {
                        var title = TypeAt(i) == T.SquareBracesStart ? new XyText("") : Text();
                        var data = Points();
                        if (t == T.Line) db.SetLineData(title, data);
                        else db.SetBarData(title, data);
                        break;
                    }
                    case T.AccTitle:
                        db.SetAccTitle(Js.Trim(Expect(T.AccTitleValue)));
                        break;
                    case T.AccDescr:
                        db.SetAccDescription(Js.Trim(Expect(T.AccDescrValue)));
                        break;
                    case T.AccDescrMultilineValue:
                        db.SetAccDescription(Js.Trim(texts[i - 1]));
                        break;
                    default:
                        i--;
                        throw Fail("'title', 'X_AXIS', 'Y_AXIS', 'LINE', 'BAR', 'acc_title', 'acc_descr', 'acc_descr_multiline_value'");
                }
                while (Is(Eol, i)) i++;
            }
        }
    }
}
