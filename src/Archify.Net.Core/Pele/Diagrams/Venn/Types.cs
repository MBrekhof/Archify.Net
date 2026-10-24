namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/venn/types.ts. VennModel is VennDb itself (index.ts hands the db to the renderer).
    internal static partial class Venn
    {
        // A set (one id) or an overlap of sets (two or more ids, sorted), with its relative area.
        internal sealed record VennSubset(List<string> Sets, double Size, string? Label);

        // A line of text shown inside the region of the given sets.
        internal sealed record VennText(List<string> Sets, string Id, string? Label);

        // Targets are the sorted ids of a set or overlap, or the id of a text.
        internal sealed record VennStyle(List<string> Targets, JsMap<string, string> Styles);
    }
}
