namespace Archify.Net;

public enum DiagramType { Architecture, Workflow, Sequence, Dataflow, Lifecycle, Erd, Class, Tree, Timeline, Waterfall }

public static class DiagramTypes
{
    public static string Name(this DiagramType t) => t.ToString().ToLowerInvariant();
    public static DiagramType Parse(string name) => name switch
    {
        "architecture" => DiagramType.Architecture, "workflow" => DiagramType.Workflow, "sequence" => DiagramType.Sequence,
        "dataflow" => DiagramType.Dataflow, "lifecycle" => DiagramType.Lifecycle, "erd" => DiagramType.Erd,
        "class" => DiagramType.Class, "tree" => DiagramType.Tree, "timeline" => DiagramType.Timeline, "waterfall" => DiagramType.Waterfall,
        _ => throw new ArgumentException($"unknown diagram type \"{name}\"", nameof(name)),
    };
    // cli.mjs SEMANTIC_COLLECTIONS / RELATIONSHIP_COLLECTIONS
    public static string SemanticCollection(this DiagramType t) => t switch
    {
        DiagramType.Architecture => "components", DiagramType.Workflow => "nodes", DiagramType.Sequence => "participants",
        DiagramType.Dataflow => "nodes", DiagramType.Lifecycle => "states", DiagramType.Erd => "entities", DiagramType.Tree => "nodes",
        DiagramType.Class => "types", DiagramType.Timeline => "events", DiagramType.Waterfall => "spans", _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };
    public static string? RelationshipCollection(this DiagramType t) => t switch
    {
        DiagramType.Architecture => "connections", DiagramType.Workflow => "edges", DiagramType.Sequence => "messages",
        DiagramType.Dataflow => "flows", DiagramType.Lifecycle => "transitions", DiagramType.Erd => "relationships",
        DiagramType.Class => "relationships", _ => null,
    };
}
