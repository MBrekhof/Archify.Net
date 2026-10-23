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
        Pele.Sankey.Parse(Pele.Sankey.PrepareTextForParsing(input.Source), db);
        return Pele.Sankey.Render(db, input.Config, context);
    }
}
