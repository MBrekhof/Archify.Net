namespace Archify.Net;

// diagrams/xychart/index.ts: the front-matter title is the model's until a `title` statement sets one; useMaxWidth is read
// from config.xyChart.
internal sealed class XyChartDiagram : IPeleDiagram
{
    public string Id => "xychart";
    public string? Section => "xyChart";
    public PeleRenderResult Render(PreprocessedInput input, PeleRenderContext context)
    {
        var db = new Pele.XyChart.XyChartDb { Title = input.Title };
        Pele.XyChart.Parse(input.Source, db);
        return Pele.XyChart.Render(db, input.Config, context);
    }
}
