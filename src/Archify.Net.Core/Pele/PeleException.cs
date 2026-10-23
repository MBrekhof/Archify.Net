namespace Archify.Net;

// Pele 1.3.0 src/errors.ts PeleError, as far as charts.mjs reads it (`code`, `line`, `message`). Public: a front-matter error
// propagates out of ExplainerCharts.Compile (charts.mjs:25, review B1).
public sealed class PeleException : Exception
{
    public PeleException(string message, string? code = "syntax", int? line = 0) : base(message)
    {
        Code = code;
        Line = line;
    }

    // An error located in a text a diagram parsed (SourceMap): `At` is the offset there, and `message` writes the message for
    // a line number. Pele.Render (or a diagram that prepares its own text, sankey) gives it the fence's line with Located.
    internal PeleException(Func<int, string> message, int? at, int line, string code) : this(message(line), code, line)
    {
        this.message = message;
        At = at;
        ParsedLine = line;
    }

    readonly Func<int, string>? message;

    public string? Code { get; }
    public int? Line { get; }
    internal int? At { get; }
    // The line as Pele counts it, in the text the diagram parsed (upstream's `line`).
    internal int? ParsedLine { get; private init; }

    // The same error on `line`, no longer waiting to be located.
    internal PeleException Located(int line) => new(message!, null, line, Code!) { ParsedLine = ParsedLine };
}

// A JS RangeError on the render path (String.fromCodePoint of a non-integer, entities.ts:34). core.ts:43-50 `guarded`
// turns every RangeError into "Diagram is nested too deeply."; Pele.Render does the same.
internal sealed class JsRangeError(string message) : Exception(message);

internal static partial class Pele
{
    // errors.ts:25-52
    internal static class Errors
    {
        public static (int Line, int Column, string Snippet) Locate(string src, int offset)
        {
            var line = 1;
            var lineStart = 0;
            for (var i = src.IndexOf('\n'); i != -1 && i < offset; i = Js.IndexOf(src, "\n", i + 1))
            {
                line++;
                lineStart = i + 1;
            }
            var lineEnd = Js.IndexOf(src, "\n", offset);
            if (lineEnd == -1) lineEnd = src.Length;
            var column = offset - lineStart + 1;
            var text = src[lineStart..lineEnd];
            return (line, column, text + "\n" + new string('-', column - 1) + "^");
        }

        // The message's first line takes the fence's line once Pele.Render locates the error (SourceMap). The snippet after it
        // stays the parsed text's line and column, as Pele prints it; charts.mjs:35 shows only the first line. No type's first
        // line shows a column.
        public static PeleException SyntaxError(string type, string src, int offset, string detail, bool lexical = false)
        {
            _ = type;   // errors.ts keeps it on the error; charts.mjs never reads it
            var loc = Locate(src, offset);
            string Message(int line)
            {
                var head = lexical ? $"Lexical error on line {line}. Unrecognized text." : $"Parse error on line {line}:";
                return lexical ? $"{head}\n{loc.Snippet}" : $"{head}\n{loc.Snippet}\n{detail}";
            }
            return new PeleException(Message, offset, loc.Line, "syntax");
        }
    }
}
