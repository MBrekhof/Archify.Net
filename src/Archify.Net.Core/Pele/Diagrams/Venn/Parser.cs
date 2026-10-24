namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/venn/parser.ts
    internal static partial class Venn
    {
        const string Statement = "'EOF', 'NEWLINE', 'TITLE', 'SET', 'UNION', 'TEXT', 'INDENT_TEXT', 'STYLE'";
        const string Value = "'NUMERIC', 'IDENTIFIER', 'STRING', 'HEXCOLOR', 'RGBCOLOR', 'RGBACOLOR'";

        // Ports Mermaid's venn grammar. A token is read only when the grammar has to look at one to decide, because the lexer
        // asks the model whether an indented `text` continues a set, and the statements are what tell the model so.
        public static void Parse(string src, VennDb db)
        {
            var lexer = new VennLexer(src, db);
            var type = -1;

            int Peek() => type == -1 ? (type = lexer.Next()) : type;

            PeleException Fail(string expected)
            {
                if (Peek() == T.Error) return Errors.SyntaxError("venn", src, lexer.Start, "", true);
                return Errors.SyntaxError("venn", src, lexer.Start, $"Expecting {expected}, got '{TokenNames[type]}'");
            }

            string Take()
            {
                type = -1;
                return lexer.Text;
            }

            string Expect(int wanted)
            {
                if (Peek() != wanted) throw Fail($"'{TokenNames[wanted]}'");
                return Take();
            }

            string Identifier()
            {
                if (Peek() != T.Identifier && type != T.String) throw Fail("'IDENTIFIER', 'STRING'");
                return Take();
            }

            List<string> Identifiers()
            {
                var list = new List<string> { Identifier() };
                while (Peek() == T.Comma)
                {
                    Take();
                    list.Add(Identifier());
                }
                return list;
            }

            void Subset(List<string> ids, bool union)
            {
                var label = Peek() == T.BracketLabel ? Take() : null;
                double? size = null;
                if (Peek() == T.Colon)
                {
                    Take();
                    size = Js.ParseFloat(Expect(T.Numeric));
                }
                if (union)
                {
                    if (ids.Count < 2) throw new PeleException("union requires multiple identifiers", "semantic");
                    db.ValidateUnionIdentifiers(ids);
                }
                db.AddSubsetData(ids, label, size);
                db.SetIndentMode(true);
            }

            // The id of a text, and the label in brackets that an id which is not a number may carry.
            void Text(Func<List<string>> sets)
            {
                var kind = Peek();
                if (kind != T.Identifier && kind != T.String && kind != T.Numeric) throw Fail("'NUMERIC', 'IDENTIFIER', 'STRING'");
                var id = Take();
                var label = kind != T.Numeric && Peek() == T.BracketLabel ? Take() : null;
                db.AddTextData(sets(), id, label);
            }

            void Style()
            {
                var ids = Identifiers();
                var styles = new List<(string, string)>();
                while (true)
                {
                    var key = Expect(T.Identifier);
                    Expect(T.Colon);
                    string value;
                    if (Peek() == T.String) value = Take();
                    else
                    {
                        var parts = new List<string>();
                        while (Peek() == T.Identifier || (type >= T.Numeric && type <= T.RgbaColor)) parts.Add(Take());
                        if (parts.Count == 0) throw Fail(Value);
                        value = string.Join(" ", parts);
                    }
                    styles.Add((key, value));
                    if (Peek() != T.Comma) break;
                    Take();
                }
                db.AddStyleData(ids, styles);
            }

            while (Peek() == T.Newline) Take();
            Expect(T.Venn);
            while (true)
            {
                var statement = Peek();
                if (statement == T.Eof) return;
                if (statement < T.Newline || statement > T.Style || statement == T.Venn) throw Fail(Statement);
                Take();
                if (statement == T.Title) db.SetDiagramTitle(lexer.Text[6..]);
                else if (statement == T.Set) Subset([Identifier()], false);
                else if (statement == T.Union) Subset(Identifiers(), true);
                else if (statement == T.Text)
                {
                    var ids = Identifiers();
                    Text(() => ids);
                }
                else if (statement == T.IndentText)
                {
                    Text(() => db.GetCurrentSets() ?? throw new PeleException("text requires set", "semantic"));
                }
                else if (statement == T.Style) Style();
            }
        }
    }
}
