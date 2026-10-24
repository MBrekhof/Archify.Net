using System.Text.RegularExpressions;

namespace Archify.Net;

// diagrams/radar/parser.ts. An entry's axis is Langium's unresolved reference: only its text ($refText), which Mermaid reads.
internal sealed record RadarAxisAst(string Name, string? Label);
internal sealed record RadarEntryAst(string? Axis, double Value);
internal sealed record RadarCurveAst(string Name, string? Label, List<RadarEntryAst> Entries);
// OptionAst: `showLegend` holds Flag, `graticule` Text, `ticks`/`max`/`min` Number.
internal sealed record RadarOptionAst(string Name, bool Flag, double Number, string? Text);

internal sealed class RadarAst
{
    public string? Title { get; set; }
    public string? AccTitle { get; set; }
    public string? AccDescr { get; set; }
    public List<RadarAxisAst> Axes { get; } = [];
    public List<RadarCurveAst> Curves { get; } = [];
    public List<RadarOptionAst> Options { get; } = [];
}

internal static partial class Pele
{
    internal static partial class Radar
    {
        // parser.ts `const enum T`: the index of each type in RadarTokens.
        const int TRadarColon = 0, TRadar = 1, TShowLegend = 2, TGraticule = 3, TCurve = 4, TTicks = 5, TAxis = 6, TMax = 7, TMin = 8,
            TColon = 9, TComma = 10, TOpen = 11, TClose = 12, TOpenBrace = 13, TCloseBrace = 14, TGraticuleValue = 15, TBoolean = 16,
            TAccDescr = 17, TAccTitle = 18, TTitle = 19, TNumber = 20, TString = 21, TId = 22, TNewline = 23;

        static int Digits(string src, int at)
        {
            var end = at;
            for (var c = Chars.CharAt(src, end); Chars.IsDigit(c); c = Chars.CharAt(src, end)) end++;
            return end;
        }

        // The NUMBER pattern /(?:[0-9]+\.[0-9]+(?!\.))|(?:0|[1-9][0-9]*(?!\.))/y without its backtracking (parser.ts:52-71): the
        // run of digits holding `at` is read once. Upstream keeps the run in module state reset by lexRadar; here it is the
        // scanner's own, made once per text.
        static Scanner NumberScanner()
        {
            int runFrom = 0, runEnd = 0;
            return (src, at) =>
            {
                if (at < runFrom || at >= runEnd)
                {
                    runFrom = at;
                    runEnd = Digits(src, at);
                }
                var whole = runEnd;
                if (whole == at) return -1;
                var dot = Chars.CharAt(src, whole) == 46;
                if (dot)
                {
                    var fraction = Digits(src, whole + 1);
                    var length = fraction - whole - 1;
                    if (length > 0 && Chars.CharAt(src, fraction) != 46) return fraction;
                    if (length > 1) return fraction - 1;
                }
                if (Chars.CharAt(src, at) == 48) return at + 1;
                if (!dot) return whole;
                return whole - 1 > at ? whole - 1 : -1;
            };
        }

        static readonly Regex ReBlock = Tokens.Sticky($@"[\t ]*accDescr{Chars.Ws}*\{{");

        // parser.ts:73-86: an `accDescr {` with no `}` after it fails at once, rather than reading to the end of the text again
        // for each one. The last `}` is found once per text.
        static Scanner AccDescrScanner()
        {
            var lastBrace = -2;
            return (src, at) =>
            {
                var block = ReBlock.Match(src, at);
                if (block.Success)
                {
                    if (lastBrace == -2) lastBrace = src.LastIndexOf('}');
                    if (block.Index + block.Length > lastBrace) return -1;
                }
                var m = Tokens.AccDescr.Pattern!.Match(src, at);
                return m.Success ? m.Index + m.Length : -1;
            };
        }

        // /[\w]([-\w]*\w)?/y
        static readonly TokenType Id = new() { Name = "ID", Pattern = Tokens.Sticky(@"[A-Za-z0-9_](?:[-A-Za-z0-9_]*[A-Za-z0-9_])?") };
        static TokenType Word(string text) => new() { Name = text, Literal = text, Longer = [Id] };
        static TokenType Mark(string text) => new() { Name = text, Literal = text };

        static readonly TokenType RadarKeyword = Tokens.Keyword("radar-beta");

        // Deviation RA1 (PORT-008 Task 11): upstream's GRATICULE, BOOLEAN and TITLE end without a word boundary, so a name that
        // starts with `circle`, `polygon`, `true`, `false` or `title` (`circles`, `trueNorth`, `titles`) lexes as that token and
        // a name after it, and the chart fails. The empty form of TITLE (`title` with nothing after it) does not match where ID
        // would read more than `title`. GRATICULE and BOOLEAN keep upstream's tokens, as an option value glued to the next
        // statement (`graticule polygonaxis a`) draws upstream; Parse reads them as part of a name only where it reads a name.
        public static readonly TokenType[] RadarTokens =
        [
            Mark("radar-beta:"),
            new() { Name = RadarKeyword.Name, Pattern = RadarKeyword.Pattern, First = RadarKeyword.First, Longer = [Id] },
            Word("showLegend"),
            Word("graticule"),
            Word("curve"),
            Word("ticks"),
            Word("axis"),
            Word("max"),
            Word("min"),
            Mark(":"),
            Mark(","),
            Mark("["),
            Mark("]"),
            Mark("{"),
            Mark("}"),
            new() { Name = "GRATICULE", Pattern = Tokens.Sticky("circle|polygon") },
            new() { Name = "BOOLEAN", Pattern = Tokens.Sticky("true|false") },
            new() { Name = Tokens.AccDescr.Name, Pattern = Tokens.AccDescr.Pattern, First = Tokens.AccDescr.First, Scanner = AccDescrScanner },
            Tokens.AccTitle,
            new()
            {
                Name = "TITLE",
                Pattern = Tokens.Sticky(@"[\t ]*title(?:[\t ][^\n\r]*?(?=%%)|[\t ][^\n\r]*|(?![-A-Za-z0-9_]*[A-Za-z0-9_]))"),
                First = Tokens.Title.First,
            },
            new() { Name = "NUMBER", Scanner = NumberScanner },
            Tokens.String,
            Id,
            Tokens.Newline,
            Tokens.Whitespace,
            Tokens.Yaml,
            Tokens.Directive,
            Tokens.SingleLineComment,
        ];

        public static RadarAst Parse(string src)
        {
            var r = new Reader(Tokens.Tokenize(src, RadarTokens, "radar"), RadarTokens, "radar");
            var ast = new RadarAst();

            void Newlines()
            {
                while (r.Kind == TNewline) r.I++;
            }

            void EndOfLine()
            {
                if (r.Kind == -1) return;
                if (r.Kind != TNewline) r.Fail("a line break or the end of input");
                Newlines();
            }

            string? Label()
            {
                if (!r.Accept(TOpen)) return null;
                var text = Tokens.StringValue(r.Expect(TString));
                r.Expect(TClose);
                return text;
            }

            // RA1: where upstream reads a name (an axis, a curve, an entry's axis), a GRATICULE or BOOLEAN token that ID would read
            // further is the start of the name, and the tokens ID covers are taken with it. The index of the token after the
            // name, or -1: not such a token, or ID ends inside a token (`circle5.3`), where the read fails as upstream's does.
            int NameEnd()
            {
                if (r.Kind != TGraticuleValue && r.Kind != TBoolean) return -1;
                var t = r.Tokens;
                var m = Id.Pattern!.Match(t.Src, t.Starts[r.I]);
                var end = m.Index + m.Length;
                if (end <= t.Ends[r.I]) return -1;
                var k = r.I;
                while (t.Ends[k] < end)
                {
                    if (t.Kinds[k + 1] == -1 || t.Starts[k + 1] != t.Ends[k]) return -1;
                    k++;
                }
                return t.Ends[k] == end ? k + 1 : -1;
            }

            bool AtName() => r.Kind == TId || NameEnd() >= 0;

            string Name()
            {
                var next = NameEnd();
                if (next < 0) return r.Expect(TId);
                var text = r.Tokens.Src[r.Tokens.Starts[r.I]..r.Tokens.Ends[next - 1]];
                r.I = next;
                return text;
            }

            RadarEntryAst Entry(bool detailed)
            {
                if (!detailed) return new(null, Js.ToNumber(r.Expect(TNumber)));
                var axis = Name();
                r.Accept(TColon);
                return new(axis, Js.ToNumber(r.Expect(TNumber)));
            }

            RadarCurveAst Curve()
            {
                var name = Name();
                var text = Label();
                var node = new RadarCurveAst(name, text, []);
                r.Expect(TOpenBrace);
                Newlines();
                if (r.Kind != TNumber && !AtName()) r.Fail("a number or an axis name");
                var detailed = AtName();
                node.Entries.Add(Entry(detailed));
                while (r.Accept(TComma))
                {
                    Newlines();
                    node.Entries.Add(Entry(detailed));
                }
                Newlines();
                r.Expect(TCloseBrace);
                return node;
            }

            RadarAxisAst Axis()
            {
                var name = Name();
                return new(name, Label());
            }

            RadarOptionAst Option()
            {
                var kind = r.Kind;
                var name = r.Take();
                if (kind == TShowLegend) return new(name, r.Expect(TBoolean) == "true", 0, null);
                if (kind == TGraticule) return new(name, false, 0, r.Expect(TGraticuleValue));
                return new(name, false, Js.ToNumber(r.Expect(TNumber)), null);
            }

            bool IsOption() => r.Kind is TShowLegend or TTicks or TMax or TMin or TGraticule;

            Newlines();
            if (!r.Accept(TRadarColon))
            {
                r.Expect(TRadar);
                r.Accept(TColon);
            }
            Newlines();
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
                    case TAxis:
                        r.I++;
                        do ast.Axes.Add(Axis());
                        while (r.Accept(TComma));
                        break;
                    case TCurve:
                        r.I++;
                        do ast.Curves.Add(Curve());
                        while (r.Accept(TComma));
                        break;
                    default:
                        if (!IsOption()) r.Fail("an axis, a curve, an option, a title, or a line break");
                        ast.Options.Add(Option());
                        while (r.Accept(TComma))
                        {
                            if (!IsOption()) r.Fail("an option");
                            ast.Options.Add(Option());
                        }
                        break;
                }
            }
            return ast;
        }
    }
}
