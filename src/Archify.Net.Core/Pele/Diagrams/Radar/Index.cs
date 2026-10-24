namespace Archify.Net;

// diagrams/radar/index.ts: the front-matter title is the model's until a `title` statement sets one. There is no
// `section`, so useMaxWidth is read from config.radar.
internal sealed class RadarDiagram : IPeleDiagram
{
    public string Id => "radar";
    public string? Section => null;
    public PeleRenderResult Render(PreprocessedInput input, PeleRenderContext context) =>
        Pele.Radar.Render(Pele.Radar.Build(Pele.Radar.Parse(input.Source), input.Title), input.Config, context);
}
