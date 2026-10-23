namespace Archify.Net;

// Pele 1.3.0 src/errors.ts PeleError, as far as charts.mjs reads it (`code`, `line`, `message`). Public: a front-matter error
// propagates out of ExplainerCharts.Compile (charts.mjs:25, review B1). Task 3 adds `locate` and `syntaxError`.
public sealed class PeleException(string message, string? code = "syntax", int? line = 0) : Exception(message)
{
    public string? Code { get; } = code;
    public int? Line { get; } = line;
}
