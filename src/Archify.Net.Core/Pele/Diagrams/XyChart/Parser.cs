namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/xychart/parser.ts
    internal static partial class XyChart
    {
        static bool[] Flags(params int[] tokens)
        {
            var output = new bool[T.Eof + 1];
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

            bool IsText(int k) => TypeAt(k) == T.Str || Is(Word, k);

            XyText Text()
            {
                if (!IsText(i)) throw Fail("'STR', 'ALPHA', 'NUM'");
                var s = texts[i++];
                if (types[i - 1] != T.Str) while (Is(Word, i)) s += texts[i++];
                return new(s);
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
                    output.Add(new(value, TypeAt(i) == T.Str ? texts[i++] : ""));
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
