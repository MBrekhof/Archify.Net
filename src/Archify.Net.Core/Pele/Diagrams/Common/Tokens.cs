using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Archify.Net;

// diagrams/common/tokens.ts Scanner: the end of the match at `at`, or -1.
internal delegate int Scanner(string src, int at);

// diagrams/common/tokens.ts TokenType. A pattern is a literal string (`Literal`) or a sticky regular expression (`Pattern`,
// built with Pele.Tokens.Sticky so it is anchored at the position it is tried at).
internal sealed class TokenType
{
    public required string Name { get; init; }
    public string? Literal { get; init; }
    public Regex? Pattern { get; init; }
    public bool Hidden { get; init; }
    // Start of a construct that needs a closer: when this matches but the whole pattern does not, the type is dead.
    public Regex? Opener { get; init; }
    // Every ASCII character a match of a regular expression can start with, when that is known.
    public string? First { get; init; }
    // Chevrotain's LONGER_ALT.
    public TokenType[]? Longer { get; init; }
    // Makes a matcher, once per text, for a pattern that could rescan the text.
    public Func<Scanner>? Scanner { get; init; }
    // Matches in place of the pattern, with no memory between calls.
    public Scanner? Match { get; init; }
}

internal sealed record TokenList(List<int> Kinds, List<int> Starts, List<int> Ends, string Src);

internal static partial class Pele
{
    // diagrams/common/tokens.ts: the tokenizer for the types Mermaid defines with Langium grammars, and the shared tokens.
    internal static class Tokens
    {
        // A JS sticky (`y`) pattern: \G anchors every alternative at the start position.
        public static Regex Sticky(string pattern) => new(@"\G(?:" + pattern + ")");

        sealed record Table(int[][] Starting, int[] Every, int[]?[] Longer);

        static readonly ConditionalWeakTable<TokenType[], Table> Tables = new();

        // tokens.ts:48-63, literally: a literal type starts with its first character; a pattern type with any of `first`, or
        // anywhere when `first` is not given. Characters outside ASCII try every type.
        static Table Prepare(TokenType[] types)
        {
            var every = Enumerable.Range(0, types.Length).ToArray();
            var starting = new int[128][];
            for (var c = 0; c < 128; c++)
            {
                var ch = (char)c;
                starting[c] = [.. every.Where(k =>
                {
                    var type = types[k];
                    return type.Literal is not null ? type.Literal.Length > 0 && type.Literal[0] == ch : type.First is null || type.First.Contains(ch);
                })];
            }
            return new(starting, every, [.. types.Select(type => type.Longer?.Select(alt => Array.IndexOf(types, alt)).ToArray())]);
        }

        public static TokenList Tokenize(string src, TokenType[] types, string diagram)
        {
            var kinds = new List<int>();
            var starts = new List<int>();
            var ends = new List<int>();
            var dead = new bool[types.Length];
            var table = Tables.GetValue(types, Prepare);
            var scanners = new Scanner?[types.Length];
            var n = src.Length;

            int MatchAt(int k, int p)
            {
                if (dead[k]) return -1;
                var type = types[k];
                if (type.Match is not null) return type.Match(src, p);
                if (type.Scanner is not null) return (scanners[k] ??= type.Scanner())(src, p);
                if (type.Literal is { } literal) return string.CompareOrdinal(src, p, literal, 0, literal.Length) == 0 && p + literal.Length <= n ? p + literal.Length : -1;
                var m = type.Pattern!.Match(src, p);
                if (m.Success) return m.Index + m.Length;
                if (type.Opener is not null && type.Opener.Match(src, p).Success) dead[k] = true;
                return -1;
            }

            var p = 0;
            while (p < n)
            {
                int c = src[p];
                var matched = false;
                foreach (var k in c < 128 ? table.Starting[c] : table.Every)
                {
                    var end = MatchAt(k, p);
                    if (end <= p) continue;
                    var kind = k;
                    if (table.Longer[k] is { } alts)
                    {
                        foreach (var alt in alts)
                        {
                            var altEnd = MatchAt(alt, p);
                            if (altEnd > end)
                            {
                                kind = alt;
                                end = altEnd;
                                break;
                            }
                        }
                    }
                    if (!types[kind].Hidden)
                    {
                        kinds.Add(kind);
                        starts.Add(p);
                        ends.Add(end);
                    }
                    p = end;
                    matched = true;
                    break;
                }
                if (!matched) throw Errors.SyntaxError(diagram, src, p, "", true);
            }
            kinds.Add(-1);
            starts.Add(n);
            ends.Add(n);
            return new(kinds, starts, ends, src);
        }

        const string Ws = Chars.Ws;
        static readonly string NotWs = Chars.NotWs;

        public static readonly TokenType AccDescr = new()
        {
            Name = "ACC_DESCR",
            Pattern = Sticky($@"[\t ]*accDescr(?:[\t ]*:([^\n\r]*?(?=%%)|[^\n\r]*)|{Ws}*\{{([^}}]*)\}})"),
            First = "\t a",
        };
        public static readonly TokenType AccTitle = new()
        {
            Name = "ACC_TITLE",
            Pattern = Sticky(@"[\t ]*accTitle[\t ]*:(?:[^\n\r]*?(?=%%)|[^\n\r]*)"),
            First = "\t a",
        };
        public static readonly TokenType Title = new()
        {
            Name = "TITLE",
            Pattern = Sticky(@"[\t ]*title(?:[\t ][^\n\r]*?(?=%%)|[\t ][^\n\r]*|)"),
            First = "\t t",
        };
        public static readonly TokenType String = new()
        {
            Name = "STRING",
            Pattern = Sticky($@"""([^""\\]|\\{Chars.Dot})*""|'([^'\\]|\\{Chars.Dot})*'"),
            First = "\"'",
        };
        public static readonly TokenType Newline = new() { Name = "NEWLINE", Pattern = Sticky(@"\r?\n"), First = "\r\n" };
        public static readonly TokenType Whitespace = new() { Name = "WHITESPACE", Pattern = Sticky(@"[\t ]+"), Hidden = true, First = "\t " };
        public static readonly TokenType Yaml = new()
        {
            Name = "YAML",
            Pattern = Sticky($@"---[\t ]*\r?\n(?:[\s\S]*?\r?\n)?---(?:\r?\n|(?!{NotWs}))"),
            Hidden = true,
            Opener = Sticky(@"---[\t ]*\r?\n"),
            First = "-",
        };
        public static readonly TokenType Directive = new()
        {
            Name = "DIRECTIVE",
            Pattern = Sticky($@"[\t ]*%%\{{[\s\S]*?\}}%%(?:\r?\n|(?!{NotWs}))"),
            Hidden = true,
            Opener = Sticky(@"[\t ]*%%\{"),
            First = "\t %",
        };
        public static readonly TokenType SingleLineComment = new()
        {
            Name = "SINGLE_LINE_COMMENT",
            Pattern = Sticky(@"[\t ]*%%[^\n\r]*"),
            First = "\t %",
            Hidden = true,
        };

        // Mermaid ends a keyword only at whitespace, a comment, or the end of input.
        public static TokenType Keyword(string word) => new()
        {
            Name = word,
            Pattern = Sticky(Regex.Escape(word) + $"(?:(?=%%)|(?!{NotWs}))"),
            First = word[..1],
        };

        static readonly Regex ReAccDescr = new($@"accDescr(?:[\t ]*:([^\n\r]*)|{Ws}*\{{([^}}]*)\}})");
        static readonly Regex ReAccTitle = new(@"accTitle[\t ]*:([^\n\r]*)");
        static readonly Regex ReTitle = new(@"title([\t ][^\n\r]*|)");
        static readonly Regex ReBlanks = new("[\t ]{2,}");
        // tokens.ts:192-193 /^\s*/gm and /\s+$/gm: `^`/`$` with `m` also at \r, U+2028 and U+2029 (review M6).
        static readonly Regex ReLeading = new($@"(?<=\A|[\n\r\u2028\u2029]){Ws}*");
        static readonly Regex ReTrailing = new($@"{Ws}+(?=[\n\r\u2028\u2029]|\z)");
        static readonly Regex ReBreaks = new("[\n\r]{2,}");

        static string Single(string text) => ReBlanks.Replace(Js.Trim(text), " ");

        public static string TitleValue(string text) => Single(ReTitle.Match(text).Groups[1].Value);

        public static string AccTitleValue(string text) => Single(ReAccTitle.Match(text).Groups[1].Value);

        public static string AccDescrValue(string text)
        {
            var m = ReAccDescr.Match(text);
            if (m.Groups[1].Success) return Single(m.Groups[1].Value);
            var body = ReLeading.Replace(m.Groups[2].Value, "");
            body = ReTrailing.Replace(body, "");
            body = ReBlanks.Replace(body, " ");
            return ReBreaks.Replace(body, "\n");
        }

        static readonly Dictionary<char, char> Escapes = new() { ['b'] = '\b', ['f'] = '\f', ['n'] = '\n', ['r'] = '\r', ['t'] = '\t', ['v'] = '\v', ['0'] = '\0' };

        public static string StringValue(string text)
        {
            if (!text.Contains('\\')) return text[1..^1];
            var output = new StringBuilder();
            for (var i = 1; i < text.Length - 1; i++)
            {
                var c = text[i];
                if (c == '\\')
                {
                    var next = text[++i];
                    output.Append(Escapes.TryGetValue(next, out var escaped) ? escaped : next);
                }
                else
                {
                    output.Append(c);
                }
            }
            return output.ToString();
        }
    }

    // tokens.ts:224-263: reads tokens for a hand-written parser, with Chevrotain-style messages on failure.
    internal sealed class Reader(TokenList tokens, TokenType[] types, string diagram)
    {
        public int I { get; set; }
        public TokenList Tokens { get; } = tokens;
        public List<int> Kinds { get; } = tokens.Kinds;

        public int Kind => Kinds[I];

        public string Text(int index) => Tokens.Src[Tokens.Starts[index]..Tokens.Ends[index]];
        public string Text() => Text(I);

        public string Take() => Text(I++);

        public bool Accept(int kind)
        {
            if (Kinds[I] != kind) return false;
            I++;
            return true;
        }

        public string Expect(int kind)
        {
            if (Kinds[I] != kind) Fail($"token of type '{types[kind].Name}'");
            return Take();
        }

        [DoesNotReturn]
        public void Fail(string expected) => throw Failure(expected);

        public PeleException Failure(string expected)
        {
            var found = Kind == -1 ? "end of input" : $"`{Text()}`";
            return Errors.SyntaxError(diagram, Tokens.Src, Tokens.Starts[I], $"Expecting {expected} but found {found}.");
        }
    }
}
