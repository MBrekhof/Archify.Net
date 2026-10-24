namespace Archify.Net;

// diagrams/venn/index.ts: the front-matter title is the model's until a `title` statement sets one. There is no `section`,
// so useMaxWidth is read from config.venn.
internal sealed class VennDiagram : IPeleDiagram
{
    public string Id => "venn";
    public string? Section => null;
    public PeleRenderResult Render(PreprocessedInput input, PeleRenderContext context)
    {
        var db = new Pele.Venn.VennDb();
        if (!string.IsNullOrEmpty(input.Title)) db.Title = input.Title;   // if (title)
        Pele.Venn.Parse(input.Source, db);
        return Pele.Venn.Render(db, input.Config, context);
    }
}
