using System.Text.Json.Nodes;

namespace Archify.Net;

// On failure Html is empty, Error is the thrown error's message, Diagnostics follow the JSON-mode rule (recorded if any, else
// thrown) and ThrownDiagnostics are what human mode prints. On success Error is null and ThrownDiagnostics empty.
public sealed record RenderResult(string Html, string Svg, string Cards, IReadOnlyList<Diagnostic> Diagnostics, LayoutReport? Layout,
    string? Error, IReadOnlyList<Diagnostic> ThrownDiagnostics);

// `validate <type> <input> --layout-json` (architecture and workflow). Report is what that prints: the layout report or
// compile receipt, also on a layout or compile failure; it is null when the render failed before layout (load gates,
// evidence, brand marks, scene construction), and Error/Diagnostics then hold the renderer failure.
public sealed record LayoutOutcome(bool Ok, JsonObject? Report, string? Error, IReadOnlyList<Diagnostic> Diagnostics);

public sealed record CheckResult(bool Ok, JsonObject Report);

// `--require-provenance` (sidecar verification) lands in phase 5; until then it must stay false.
public sealed record CheckOptions(bool RequireProvenance = false);
