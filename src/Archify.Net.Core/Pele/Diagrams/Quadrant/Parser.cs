namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/quadrant/parser.ts
    internal static partial class Quadrant
    {
        static bool[] Flags(params int[] tokens)
        {
            var output = new bool[T.Eof + 1];
            foreach (var t in tokens) output[t] = true;
            return output;
        }

        static readonly bool[] Word = Flags(T.Punctuation, T.Amp, T.Num, T.Alpha, T.Comma, T.Plus, T.Eq, T.Mult, T.Dot, T.Brkt, T.Underscore, T.UnicodeText);
        static readonly bool[] Id = Flags(T.Alpha, T.Num, T.Minus, T.Comma, T.Colon, T.Amp, T.Brkt, T.Mult, T.UnicodeText);
        static readonly bool[] StyleTokens = Flags(T.Alpha, T.Num, T.Colon, T.Space, T.Brkt, T.Minus);
        static readonly bool[] Eol = Flags(T.Newline, T.Semi, T.Eof);

        const string TextTokens = "'STR', 'MD_STR', 'ALPHA', 'NUM', 'UNICODE_TEXT', 'PUNCTUATION'";

        // Every read stops at END or ERROR, the last token, so no index goes past the end.
        public static void Parse(string src, QuadrantDb db)
        {
            var (types, texts, starts) = Tokenize(src);
            var i = 0;

            PeleException Fail(string expected)
            {
                var t = types[i];
                if (t == T.Error) return Errors.SyntaxError("quadrantChart", src, starts[i], "", true);
                return Errors.SyntaxError("quadrantChart", src, starts[i], $"Expecting {expected}, got '{TokenNames[t]}'");
            }

            string Expect(int type)
            {
                if (types[i] != type) throw Fail($"'{TokenNames[type]}'");
                return texts[i++];
            }

            bool IsText(int t) => t == T.Str || t == T.MdStr || Word[t];

            QuadrantText Text()
            {
                var first = types[i];
                if (!IsText(first)) throw Fail(TextTokens);
                var s = texts[i++];
                while (Word[types[i]] || types[i] == T.Space || types[i] == T.Minus) s += texts[i++];
                return new(s, first == T.MdStr);
            }

            List<string> Styles()
            {
                var output = new List<string>();
                for (; ; )
                {
                    if (!StyleTokens[types[i]]) throw Fail("'ALPHA', 'NUM', 'COLON', 'SPACE', 'BRKT', 'MINUS'");
                    var s = texts[i++];
                    while (StyleTokens[types[i]]) s += texts[i++];
                    output.Add(Js.Trim(s));
                    if (types[i] != T.Comma) return output;
                    i++;
                }
            }

            while (Eol[types[i]] || types[i] == T.Space) i++;
            Expect(T.QuadrantChart);

            while (types[i] != T.End)
            {
                while (types[i] == T.Space) i++;
                var t = types[i];
                switch (t)
                {
                    case T.ClassDef:
                    {
                        i++;
                        Expect(T.Space);
                        if (!Id[types[i]]) throw Fail("'ALPHA', 'NUM', 'UNICODE_TEXT'");
                        var id = texts[i++];
                        while (Id[types[i]]) id += texts[i++];
                        Expect(T.Space);
                        db.AddClass(id, Styles());
                        break;
                    }
                    case T.XAxis:
                    case T.YAxis:
                    {
                        i++;
                        var start = Text();
                        QuadrantText? end = null;
                        if (types[i] == T.Delimiter)
                        {
                            i++;
                            if (IsText(types[i])) end = Text();
                            else start = start with { Text = start.Text + " ⟶ " };
                        }
                        if (t == T.XAxis)
                        {
                            db.SetXAxisLeftText(start);
                            if (end is not null) db.SetXAxisRightText(end);
                        }
                        else
                        {
                            db.SetYAxisBottomText(start);
                            if (end is not null) db.SetYAxisTopText(end);
                        }
                        break;
                    }
                    case T.Quadrant1:
                    case T.Quadrant2:
                    case T.Quadrant3:
                    case T.Quadrant4:
                        i++;
                        db.SetQuadrantText(t - T.Quadrant1, Text());
                        break;
                    case T.Title:
                        i++;
                        db.SetDiagramTitle(Js.Trim(Expect(T.TitleValue)));
                        break;
                    case T.AccTitle:
                        i++;
                        db.SetAccTitle(Js.Trim(Expect(T.AccTitleValue)));
                        break;
                    case T.AccDescr:
                        i++;
                        db.SetAccDescription(Js.Trim(Expect(T.AccDescrValue)));
                        break;
                    case T.AccDescrMultilineValue:
                        db.SetAccDescription(Js.Trim(texts[i++]));
                        break;
                    default:
                        if (IsText(t))
                        {
                            var label = Text();
                            var className = types[i] == T.ClassName ? texts[i++] : "";
                            Expect(T.PointStart);
                            var x = Expect(T.PointX);
                            var y = Expect(T.PointY);
                            db.AddPoint(label, className, x, y, StyleTokens[types[i]] ? Styles() : []);
                        }
                        break;
                }
                if (!Eol[types[i]]) throw Fail("'NEWLINE', 'SEMI', 'EOF'");
                i++;
            }
        }
    }
}
