namespace Archify.Net;

// diagrams/quadrant/index.ts: the front-matter title is the model's until a `title` statement sets one. There is no
// `section`, so useMaxWidth is read from config.quadrantChart.
internal sealed class QuadrantChartDiagram : IPeleDiagram
{
    public string Id => "quadrantChart";
    public string? Section => null;
    public PeleRenderResult Render(PreprocessedInput input, PeleRenderContext context)
    {
        var db = new Pele.Quadrant.QuadrantDb { Title = input.Title };
        Pele.Quadrant.Parse(input.Source, db);
        return Pele.Quadrant.Render(db, input.Config, context);
    }
}
