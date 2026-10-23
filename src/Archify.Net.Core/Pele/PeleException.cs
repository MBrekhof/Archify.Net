namespace Archify.Net;

// Pele 1.3.0 src/errors.ts PeleError, as far as charts.mjs reads it (`code`, `line`, `message`). Public: a front-matter error
// propagates out of ExplainerCharts.Compile (charts.mjs:25, review B1).
public sealed class PeleException(string message, string? code = "syntax", int? line = 0) : Exception(message)
{
    public string? Code { get; } = code;
    public int? Line { get; } = line;
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

        public static PeleException SyntaxError(string type, string src, int offset, string detail, bool lexical = false)
        {
            _ = type;   // errors.ts keeps it on the error; charts.mjs never reads it
            var loc = Locate(src, offset);
            var head = lexical ? $"Lexical error on line {loc.Line}. Unrecognized text." : $"Parse error on line {loc.Line}:";
            var message = lexical ? $"{head}\n{loc.Snippet}" : $"{head}\n{loc.Snippet}\n{detail}";
            return new PeleException(message, "syntax", loc.Line);
        }
    }
}
