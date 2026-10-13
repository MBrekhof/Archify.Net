using System.Text.Json.Nodes;

namespace Archify.Net;

public sealed record RenderResult(string Html, string Svg, string Cards, IReadOnlyList<Diagnostic> Diagnostics, LayoutReport? Layout);

public sealed record CheckResult(bool Ok, JsonObject Report);
