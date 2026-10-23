namespace Archify.Net;

// diagrams/sankey/index.ts: the front-matter title is the model's (the grammar has no title). There is no `section`, so
// useMaxWidth is read from config.sankey.
internal sealed class SankeyDiagram : IPeleDiagram
{
    public string Id => "sankey";
    public string? Section => null;
    public PeleRenderResult Render(PreprocessedInput input, PeleRenderContext context)
    {
        var db = new Pele.Sankey.SankeyDb { Title = string.IsNullOrEmpty(input.Title) ? null : input.Title };   // if (title)
        var (text, origin) = Pele.Sankey.PrepareWithOrigin(input.Source);
        // The parser's offsets are in the prepared text, which has lost blank lines too (SourceMap).
        try { Pele.Sankey.Parse(text, db); }
        catch (PeleException error) when (error.At is int at) { throw error.Located(input.Map.Then(origin).Line(at)); }
        return Pele.Sankey.Render(db, input.Config, context);
    }
}
