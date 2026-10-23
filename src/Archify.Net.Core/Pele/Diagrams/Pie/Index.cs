namespace Archify.Net;

// diagrams/pie/index.ts: parse ignores the config and the front-matter line offset, so an error's line counts from the
// chart's own first line.
internal sealed class PieDiagram : IPeleDiagram
{
    public string Id => "pie";
    public string? Section => null;
    public PeleRenderResult Render(PreprocessedInput input, PeleRenderContext context) =>
        Pele.Pie.Render(Pele.Pie.Build(Pele.Pie.Parse(input.Source), input.Title), input.Config, context);
}
