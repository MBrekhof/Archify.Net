using System.Text.Json.Nodes;

namespace Archify.Net;

// archify's render options (charts.mjs:28) as typed fields, plus the `config` record merged under each chart's own.
internal sealed record PeleOptions(double Padding, double FontSize, bool Links, bool Images, JsonObject Config);

// core.ts RenderResult as charts.mjs reads it (links are never read: `links: false`).
internal sealed record PeleRenderResult(string Svg, double Width, double Height, string Type);

// What core.ts gives a diagram's parse(): the entity-encoded source ending in "\n", the merged config, the front-matter
// title and the number of front-matter lines removed. `Map` leads `Source`'s offsets back to the fence (the appended "\n" is
// past its last character).
internal sealed record PreprocessedInput(string Source, JsonObject Config, string? Title, int LineOffset, SourceMap Map);

// core.ts:130 `{ ...options, responsive, icons }`; there is no icon resolver (Label.cs).
internal sealed record PeleRenderContext(PeleOptions Options, bool Responsive);

// core.ts Diagram: register(...) is replaced by Pele's fixed table. `Section` is the config key read for useMaxWidth
// (e.g. "xyChart"); null means the type's own id. Render is parse + render, as core.ts runs them back to back.
internal interface IPeleDiagram
{
    string Id { get; }
    string? Section { get; }
    PeleRenderResult Render(PreprocessedInput input, PeleRenderContext context);
}

// core.ts: detectType and render for the six chart types archify registers (charts.mjs:9).
internal static partial class Pele
{
    // Mermaid's default maxTextSize, and Pele's output limit (core.ts:34,37).
    const int DefaultLimit = 50000;
    const int DefaultOutputLimit = 4_000_000;

    // pie, xychart, quadrantChart, sankey, radar, venn: each lands with its own PORT-008 task (5-10).
    static readonly Dictionary<string, IPeleDiagram> Diagrams = new IPeleDiagram[] { new PieDiagram(), new XyChartDiagram(), new QuadrantChartDiagram(), new SankeyDiagram() }.ToDictionary(diagram => diagram.Id, StringComparer.Ordinal);

    public static string? DetectType(string text) => Detect.Run(Preprocess.Run(text).Text);

    // core.ts:120-138 over parseSource (:62-84). Throws PeleException. `written` leads `text` back to the fence when the caller
    // has rewritten it (archify's pie legend); an error's line is counted there.
    public static PeleRenderResult Render(string text, PeleOptions options, SourceMap? written = null)
    {
        try
        {
            if (text.Length > DefaultLimit) throw new PeleException($"Diagram source is longer than the limit of {DefaultLimit} characters.", "limit");
            var pre = Preprocess.Run(text);
            var type = Detect.Run(pre.Text) ?? throw new PeleException("No diagram type detected for the given text.", "unsupported-diagram");
            if (!Diagrams.TryGetValue(type, out var diagram)) throw new PeleException($"Diagram type \"{type}\" is not supported yet.", "unsupported-diagram");
            var config = Spread(options.Config, pre.Config);
            // None of the six types sets keepComments, so parse() gets the comment-free text (core.ts:81-82).
            var (source, origin) = Preprocess.EncodeEntities(pre.Text, pre.Origin);
            var map = written?.Then(origin) ?? new SourceMap(text, origin);
            var input = new PreprocessedInput(source + "\n", config, pre.Title, pre.LineOffset, map);
            var section = config[diagram.Section ?? type];
            var fixedWidth = section is JsonObject o && o["useMaxWidth"] is JsonValue v && v.TryGetValue<bool>(out var b) && !b;   // === false
            PeleRenderResult rendered;
            try { rendered = diagram.Render(input, new PeleRenderContext(options, !fixedWidth)); }
            catch (PeleException error) when (error.At is int at) { throw error.Located(input.Map.Line(at)); }   // SourceMap
            if (rendered.Svg.Length > DefaultOutputLimit) throw new PeleException($"Diagram output is longer than the limit of {DefaultOutputLimit} characters.", "limit");
            return rendered with { Type = type };
        }
        catch (JsRangeError)
        {   // core.ts:43-50 guarded()
            throw new PeleException("Diagram is nested too deeply.", "limit");
        }
    }

    // `{ ...extra, ...pre.config }` (core.ts:80): a shallow spread, so a chart's own `xyChart` replaces archify's whole object.
    static JsonObject Spread(JsonObject extra, JsonObject own)
    {
        var config = new JsonObject();
        foreach (var (key, value) in extra) config[key] = value?.DeepClone();
        foreach (var (key, value) in own) config[key] = value?.DeepClone();
        return config;
    }
}
