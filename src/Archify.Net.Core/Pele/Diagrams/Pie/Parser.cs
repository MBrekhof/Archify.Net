namespace Archify.Net;

// diagrams/pie/parser.ts
internal sealed class PieAst
{
    public bool ShowData { get; set; }
    public string? Title { get; set; }
    public string? AccTitle { get; set; }
    public string? AccDescr { get; set; }
    public List<(string Label, double Value)> Sections { get; } = [];
}

internal static partial class Pele
{
    internal static partial class Pie
    {
        // parser.ts `const enum T`: the index of each type in PieTokens.
        const int TShowData = 0, TPie = 1, TColon = 2, TNumber = 3, TAccDescr = 4, TAccTitle = 5, TTitle = 6, TString = 7, TNewline = 8;

        static int Digits(string src, int from)
        {
            var i = from;
            for (var c = Chars.CharAt(src, i); Chars.IsDigit(c); c = Chars.CharAt(src, i)) i++;
            return i;
        }

        // Answers as NUMBER_PIE's pattern /(?:-?[0-9]+\.[0-9]+(?!\.))|(?:-?(0|[1-9][0-9]*)(?!\.))/y does, reading each run of
        // digits once (parser.ts:38-60).
        static Scanner NumberScanner()
        {
            var from = 0;
            var end = 0;
            var fraction = -1;
            return (src, p) =>
            {
                var at = Chars.CharAt(src, p) == 45 ? p + 1 : p;
                if (at < from || at >= end)
                {
                    from = at;
                    end = Digits(src, at);
                    fraction = -1;
                    if (end > at && Chars.CharAt(src, end) == 46)
                    {
                        var stop = Digits(src, end + 1);
                        var length = stop - end - 1;
                        if (length > 0 && Chars.CharAt(src, stop) != 46) fraction = stop;
                        else if (length > 1) fraction = stop - 1;
                    }
                }
                if (end == at) return -1;
                if (fraction >= 0) return fraction;
                if (Chars.CharAt(src, at) == 48) return Chars.CharAt(src, at + 1) == 46 ? -1 : at + 1;
                if (Chars.CharAt(src, end) != 46) return end;
                return end - 1 > at ? end - 1 : -1;
            };
        }

        public static readonly TokenType[] PieTokens =
        [
            Tokens.Keyword("showData"),
            Tokens.Keyword("pie"),
            new() { Name = ":", Literal = ":" },
            new() { Name = "NUMBER_PIE", Scanner = NumberScanner, First = "-0123456789" },
            Tokens.AccDescr,
            Tokens.AccTitle,
            Tokens.Title,
            Tokens.String,
            Tokens.Newline,
            Tokens.Whitespace,
            Tokens.Yaml,
            Tokens.Directive,
            Tokens.SingleLineComment,
        ];

        public static PieAst Parse(string src)
        {
            var r = new Reader(Tokens.Tokenize(src, PieTokens, "pie"), PieTokens, "pie");
            var ast = new PieAst();

            void EndOfLine()
            {
                if (r.Kind == -1) return;
                if (r.Kind != TNewline) r.Fail("a line break or the end of input");
                while (r.Kind == TNewline) r.I++;
            }

            while (r.Kind == TNewline) r.I++;
            r.Expect(TPie);
            ast.ShowData = r.Accept(TShowData);
            while (r.Kind != -1)
            {
                switch (r.Kind)
                {
                    case TNewline:
                        r.I++;
                        break;
                    case TAccDescr:
                        ast.AccDescr = Tokens.AccDescrValue(r.Take());
                        EndOfLine();
                        break;
                    case TAccTitle:
                        ast.AccTitle = Tokens.AccTitleValue(r.Take());
                        EndOfLine();
                        break;
                    case TTitle:
                        ast.Title = Tokens.TitleValue(r.Take());
                        EndOfLine();
                        break;
                    case TString:
                        var label = Tokens.StringValue(r.Take());
                        r.Expect(TColon);
                        var value = Js.ToNumber(r.Expect(TNumber));
                        ast.Sections.Add((label, value));
                        EndOfLine();
                        break;
                    default:
                        r.Fail("a section, a title, or a line break");
                        break;
                }
            }
            return ast;
        }
    }
}
