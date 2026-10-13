namespace Archify.Net;

// node-card-style.mjs. Shared card styling for the architecture and workflow renderers. Every
// preset gets the cleaner cards and label chips; blueprint keeps its drafting
// dash patterns and square corners.
public static class NodeCardStyle
{
    static readonly string[] Tones = ["frontend", "backend", "database", "cloud", "security", "messagebus", "external"];

    public const string NODE_CARD_ATTR = "data-node-ui=\"\"";

    public static string Style()
    {
        const string all = "svg[data-node-ui]";
        var solid = $"{all}:is([data-preset=\"classic\"], [data-preset=\"signal-flow\"], [data-preset=\"editorial\"])";
        var blueprint = $"{all}[data-preset=\"blueprint\"]";
        var editorial = $"{all}[data-preset=\"editorial\"]";
        var dark = $":is([data-theme=\"dark\"] {all}, {all}[data-theme=\"dark\"])";
        const string node = "[data-node-id] > rect:not(.c-mask)";
        const string frameLabel = "[data-graph-role=\"structural-frame-label-mask\"]";
        const string edgeLabel = "g[data-edge-from] > rect.c-mask";
        return string.Join("\n",
        [
            "        <style>",
            $"          {all} {node} {{ stroke-width: 1.25px; }}",
            string.Join("\n", Tones.Select(tone => $"          {dark} {node}.c-{tone} {{ fill: color-mix(in srgb, var(--{tone}-stroke) 13%, var(--mask)); stroke: color-mix(in srgb, var(--{tone}-stroke) 72%, var(--mask)); }}")),
            $"          {solid} .c-region {{ fill: color-mix(in srgb, var(--cloud-stroke) 3%, transparent); stroke: color-mix(in srgb, var(--cloud-stroke) 42%, var(--lane-stroke)); stroke-dasharray: none; }}",
            $"          {all} .c-security-group {{ stroke: color-mix(in srgb, var(--security-stroke) 58%, var(--lane-stroke)); }}",
            $"          {solid} .c-security-group {{ stroke-dasharray: 4 3; }}",
            $"          {solid} .c-lane {{ stroke: var(--lane-stroke); stroke-dasharray: none; }}",
            $"          {all} {frameLabel} {{ rx: 5px; fill: color-mix(in srgb, var(--cloud-stroke) 10%, var(--mask)); stroke: color-mix(in srgb, var(--cloud-stroke) 38%, var(--mask)); stroke-width: .8px; }}",
            $"          {all} [data-composition-frame-kind=\"security-group\"] > {frameLabel} {{ fill: color-mix(in srgb, var(--security-stroke) 9%, var(--mask)); stroke: color-mix(in srgb, var(--security-stroke) 36%, var(--mask)); }}",
            $"          {all} .phase-rule {{ opacity: .5; stroke-dasharray: none; }}",
            $"          {all} .phase-chip {{ stroke: var(--lane-stroke); stroke-width: .75px; }}",
            $"          {all} {edgeLabel} {{ rx: 7px; stroke: var(--lane-stroke); stroke-width: .75px; }}",
            $"          {blueprint} :is({frameLabel}, {edgeLabel}, .phase-chip) {{ rx: 2px; }}",
            $"          {editorial} :is({frameLabel}, {edgeLabel}, .phase-chip) {{ rx: 3px; }}",
            "        </style>",
        ]);
    }
}
