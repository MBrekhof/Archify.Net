using System.Text.Json.Nodes;

namespace Archify.Net;

// layout-report.mjs: serialize computed layout for dry-run / inspect (#9). The renderer assembles the report object
// (render-architecture.mjs buildLayoutReport); this class carries it and owns the four row builders.
// A key JS would leave `undefined` is omitted, as JSON.stringify would drop it.
public sealed class LayoutReport(JsonObject report)
{
    public JsonObject Report { get; } = report;

    static bool IsInteger(double? d) => d is { } v && double.IsFinite(v) && v == Math.Floor(v);
    static JsonArray RoundedPos(JsonArray pos) => [.. pos.Select(p => (JsonNode?)JsonValue.Create(Js.Round(p!.D())))];

    public static JsonObject ComponentBox(Box c)
    {
        var box = new JsonObject { ["id"] = c.Id };
        if (c.Source["type"] is { } type) box["type"] = type.DeepClone();
        if (c.Source["label"] is { } label) box["label"] = label.DeepClone();
        box["x"] = Js.Round(c.X);
        box["y"] = Js.Round(c.Y);
        box["width"] = c.Width;
        box["height"] = c.Height;
        if (IsInteger(c.Source.Num("row"))) box["row"] = c.Source.Num("row");
        if (IsInteger(c.Source.Num("col"))) box["col"] = c.Source.Num("col");
        if (c.Source.Arr("pos") is { } pos) box["pos"] = RoundedPos(pos);
        return box;
    }

    public static JsonObject EntityBox(Box e)
    {
        var box = new JsonObject { ["id"] = e.Id };
        if (e.Source["label"] is { } label) box["label"] = label.DeepClone();
        box["x"] = Js.Round(e.X);
        box["y"] = Js.Round(e.Y);
        box["width"] = e.Width;
        box["height"] = e.Height;
        box["attributes"] = new JsonArray([.. e.Source.Arr("attributes").AsArrayOrEmpty().Select(attribute => attribute?["name"]?.DeepClone())]);
        if (IsInteger(e.Source.Num("row"))) box["row"] = e.Source.Num("row");
        if (IsInteger(e.Source.Num("col"))) box["col"] = e.Source.Num("col");
        if (e.Source.Arr("pos") is { } pos) box["pos"] = RoundedPos(pos);
        return box;
    }

    // `b` is the measured boundary: its authored object (kind, label, wraps) plus the frame rect.
    public static JsonObject BoundaryBox(JsonObject boundary, Rect frame)
    {
        var box = new JsonObject();
        if (boundary["kind"] is { } kind) box["kind"] = kind.DeepClone();
        if (boundary["label"] is { } label) box["label"] = label.DeepClone();
        box["x"] = Js.Round(frame.X);
        box["y"] = Js.Round(frame.Y);
        box["width"] = Js.Round(frame.Width);
        box["height"] = Js.Round(frame.Height);
        if (boundary["wraps"] is { } wraps) box["wraps"] = wraps.DeepClone();
        return box;
    }

    public static JsonObject ConnectionPath(JsonObject conn, IReadOnlyList<Pt> points, Pt? labelAt)
    {
        var path = new JsonObject
        {
            ["from"] = conn["from"]?.DeepClone(),
            ["to"] = conn["to"]?.DeepClone(),
            ["label"] = conn["label"]?.DeepClone(),
            ["variant"] = conn["variant"]?.DeepClone() ?? "default",
            ["route"] = conn["route"]?.DeepClone() ?? "auto",
            // These points are repair inputs: rounding a fractional port makes a
            // reused waypoint diagonal relative to the actual endpoint.
            ["points"] = new JsonArray([.. points.Select(p => (JsonNode?)new JsonArray(p.X, p.Y))]),
        };
        if (labelAt is { } at) path["labelAt"] = new JsonArray(Js.Round(at.X), Js.Round(at.Y));
        return path;
    }
}
