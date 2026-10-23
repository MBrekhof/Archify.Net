namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/sankey/db.ts
    internal static partial class Sankey
    {
        internal sealed record SankeyNode(string Id, int Index);

        internal sealed record SankeyLink(SankeyNode Source, SankeyNode Target, double Value);

        internal sealed class SankeyDb
        {
            public List<SankeyNode> Nodes { get; } = [];
            public List<SankeyLink> Links { get; } = [];
            public string? Title { get; set; }
            // The grammar has no accTitle or accDescr; they stay undefined.
            public string? AccTitle => null;
            public string? AccDescr => null;

            readonly Dictionary<string, SankeyNode> lookup = new(StringComparer.Ordinal);

            public SankeyNode FindOrCreateNode(string id)
            {
                if (!lookup.TryGetValue(id, out var node))
                {
                    node = new(id, Nodes.Count);
                    lookup[id] = node;
                    Nodes.Add(node);
                }
                return node;
            }

            public void AddLink(SankeyNode source, SankeyNode target, double value) => Links.Add(new(source, target, value));
        }
    }
}
