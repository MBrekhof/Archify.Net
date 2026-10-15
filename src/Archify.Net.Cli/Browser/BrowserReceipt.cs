using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Archify.Net.Cli.Delivery;
using Archify.Net.Cli.Paths;

namespace Archify.Net.Cli.Browser;

// The browser evidence receipt: baseReceipt (VC:2204-2235), observationDiagnostics (VC:2093-2202), the contact sheet (VC:1959-1986)
// and summarizeBrowserEvidence (VC:1988-2022). Receipts are JsonObjects because upstream builds them by spreading and mutating
// objects: setting an existing key keeps its place, a new key goes last, exactly as here.
internal static class BrowserReceipt
{
    public const string ContainmentPolicy = "fit-or-reader-declared-readable-vertical-scroll";

    // failureDiagnostic (VC:2033-2035)
    public static Diagnostic Failure(string code, string message, JsonObject subject, JsonObject evidence, IReadOnlyList<string> supportedFixes,
        Severity severity = Severity.Error) => new(code, severity, message, subject, evidence, supportedFixes, null);

    public static JsonArray Json(IEnumerable<Diagnostic> diagnostics) => [.. diagnostics.Select(d => (JsonNode)d.ToJson())];

    public static JsonObject ArtifactIdentity(string path, byte[] bytes) =>
        new() { ["path"] = path, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["bytes"] = bytes.Length };

    public static JsonObject Containment() => new() { ["status"] = "fail", ["policy"] = ContainmentPolicy, ["viewports"] = new JsonArray() };

    public static JsonObject Captures(bool capture) =>
        new() { ["status"] = capture ? "fail" : "not-requested", ["screenshots"] = new JsonArray(), ["contactSheet"] = null };

    public static JsonObject Base(string artifactPath, byte[] artifact, JsonObject sidecars, JsonObject chrome, Provenance? deliveryProvenance,
        string command, bool capture)
    {
        var receipt = new JsonObject
        {
            ["schemaVersion"] = 1, ["ok"] = false, ["command"] = command, ["evidenceKind"] = "automated-browser", ["status"] = "fail",
            ["visualReview"] = capture ? "pending" : "not-requested",
        };
        if (deliveryProvenance is not null)
        {
            receipt["provenance"] = deliveryProvenance.Status;
            if (!string.IsNullOrEmpty(deliveryProvenance.ReceiptId)) receipt["deliveryReceiptId"] = deliveryProvenance.ReceiptId;
        }
        receipt["artifact"] = ArtifactIdentity(artifactPath, artifact);
        receipt["state"] = new JsonObject { ["detail"] = "read", ["motion"] = "still" };
        receipt["chrome"] = chrome;
        receipt["diagnostics"] = new JsonArray();
        receipt["containment"] = Containment();
        receipt["themeStates"] = new JsonObject { ["status"] = "fail", ["viewports"] = new JsonArray() };
        receipt["readability"] = new JsonObject
        {
            ["status"] = "fail", ["minimumProjectedNodeTextPx"] = DesktopReadability.MIN_PROJECTED_NODE_TEXT_PX, ["viewports"] = new JsonArray(),
        };
        receipt["viewerChrome"] = new JsonObject { ["status"] = "fail", ["viewports"] = new JsonArray() };
        receipt["captures"] = Captures(capture);
        receipt["sidecars"] = WithFiles(sidecars);
        return receipt;
    }

    // { ...sidecars, files: [] }
    public static JsonObject WithFiles(JsonObject sidecars)
    {
        var copy = sidecars.DeepClone().AsObject();
        copy["files"] = new JsonArray();
        return copy;
    }

    static JsonObject ViewportSubject(string artifact, Observation entry) => new()
    {
        ["artifact"] = artifact, ["viewport"] = new JsonObject { ["width"] = entry.Width, ["height"] = entry.Height, ["theme"] = entry.Theme },
    };

    public static List<Diagnostic> ObservationDiagnostics(string artifact, IReadOnlyList<Observation> allObservations,
        IReadOnlyList<Observation> readabilityObservations, string command)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var entry in allObservations)
        {
            var at = $"{entry.Width}x{entry.Height}";
            if (entry.ResolvedTheme != entry.Theme)
                diagnostics.Add(Failure("viewer/theme-state",
                    $"The rendered artifact resolved {(entry.ResolvedTheme.Length > 0 ? entry.ResolvedTheme : "no theme")} instead of the requested {entry.Theme} theme at {at}.",
                    ViewportSubject(artifact, entry), new JsonObject { ["requestedTheme"] = entry.Theme, ["resolvedTheme"] = entry.ResolvedTheme },
                    [$"restore deterministic {entry.Theme} theme resolution, then rerun {command}"]));
            if (!entry.Ok)
            {
                var authoredClipped = entry.AuthoredClipped;
                var budgetFixes = authoredClipped
                    ? ["restore the full SVG inside the diagram panel and allow normal document scrolling; remove internal scrollers and clipping without changing authored geometry"]
                    : Observation.VerticalBudgetFixes(entry);
                var evidence = new JsonObject
                {
                    ["innerWidth"] = entry.InnerWidth, ["innerHeight"] = entry.InnerHeight, ["scrollWidth"] = entry.ScrollWidth, ["scrollHeight"] = entry.ScrollHeight,
                    ["overflowX"] = entry.OverflowX, ["overflowY"] = entry.OverflowY, ["overflowDisposition"] = entry.OverflowDisposition,
                    ["readerLayout"] = entry.ReaderLayout, ["readerOverflow"] = entry.ReaderOverflow, ["readerFit"] = entry.ReaderFit,
                };
                if (authoredClipped) evidence["documentScrollUnclipped"] = false;
                if (entry.OverflowY && entry.PageComposition is not null)
                {
                    evidence["pageComposition"] = entry.PageComposition.DeepClone();
                    evidence["pageCompositionMeasurement"] = "CSS pixels at this viewport; body padding, header, diagram chrome, SVG and cards stack vertically and sum to scrollHeight";
                }
                var lanes = entry.OverflowY && entry.WorkflowLanes is not null;
                if (lanes)
                {
                    evidence["workflowLanes"] = entry.WorkflowLanes!.DeepClone();
                    evidence["measurement"] = "CSS pixels; rendered node boxes geometrically contained in each lane frame; spaces include headers and routing, not guaranteed removable space";
                }
                List<string> fixes = [.. budgetFixes];
                if (lanes)
                    fixes.AddRange([
                        "run validate workflow <source.json> --layout-json and compare the tallest rendered lane frames with source lanes, col and yOffset; frame IDs are rendered indices, not source lane IDs",
                        "where ownership and explicit geometry permit, distribute stacked steps across logical columns and meaningful lanes before increasing yOffset; preserve nodes, branches, labels and hard pins",
                        $"read references/authoring-contract.md#workflow-viewport-repair, then validate and deliver the changed source before rerunning {command} on the new artifact; this is inspection guidance, not a verified coordinate fix",
                    ]);
                else if (budgetFixes.Count == 0) fixes.Add($"contain the rendered layout within {at}, then rerun {command}");
                diagnostics.Add(Failure(authoredClipped ? "viewer/diagram-clipped" : "viewer/viewport-overflow",
                    authoredClipped
                        ? $"The authored Architecture canvas is clipped or cannot scroll in the document at {at} ({entry.Theme})."
                        : $"The rendered artifact overflows the {at} {entry.Theme} viewport.",
                    ViewportSubject(artifact, entry), evidence, fixes));
            }
            if (entry.LegendDockIntersectionArea > 0.5)
                diagnostics.Add(Failure("viewer/chrome-legend-clearance", $"The navigation Dock obscures the SVG Legend at {at} ({entry.Theme}).",
                    ViewportSubject(artifact, entry), new JsonObject { ["legendDockIntersectionArea"] = entry.LegendDockIntersectionArea },
                    [$"move the SVG Legend or Viewer Dock until legendDockIntersectionArea is 0, then rerun {command}"]));
            if (!entry.ViewerChromeStageOk)
                diagnostics.Add(Failure("viewer/chrome-stage-clearance",
                    entry.DockStageIntersectionArea > 0.5
                        ? $"Navigation Dock enters the protected SVG stage at {at} ({entry.Theme})."
                        : $"Navigation Dock clearance from the protected SVG stage is below the required gap at {at} ({entry.Theme}).",
                    ViewportSubject(artifact, entry),
                    new JsonObject
                    {
                        ["dockStageIntersectionArea"] = entry.DockStageIntersectionArea, ["dockStageGap"] = entry.DockStageGap,
                        ["requiredDockStageGap"] = entry.RequiredDockStageGap,
                    },
                    [$"adjust Viewer stage reservation or clipping until dockStageGap is at least {Js.Num(entry.RequiredDockStageGap)} and dockStageIntersectionArea is 0, then rerun {command}"]));
        }
        foreach (var entry in readabilityObservations.Where(entry => !entry.ReadabilityOk))
        {
            var kind = entry.MinimumProjectedNodeTextOwner?["kind"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            diagnostics.Add(Failure("viewer/projected-text-readability",
                $"Projected {entry.MinimumProjectedNodeTextDetail ?? "node"} text is below the readability floor at {entry.Width}x{entry.Height}.",
                ViewportSubject(artifact, entry),
                new JsonObject
                {
                    ["text"] = entry.MinimumProjectedNodeText, ["detail"] = entry.MinimumProjectedNodeTextDetail, ["owner"] = entry.MinimumProjectedNodeTextOwner?.DeepClone(),
                    ["minimumProjectedNodeTextPx"] = entry.MinimumProjectedNodeTextPx, ["minimumRequiredNodeTextPx"] = DesktopReadability.MIN_PROJECTED_NODE_TEXT_PX,
                },
                [$"increase projected {kind switch { "edge" => "relationship label", "boundary" => "boundary label", _ => "node text" }} to at least {Js.Num(DesktopReadability.MIN_PROJECTED_NODE_TEXT_PX)}px at {entry.Width}x{entry.Height}, then rerun {command}"]));
        }
        return diagnostics;
    }

    // contactSheetHtml (VC:1959-1986)
    public static string ContactSheetHtml(string artifactPath, JsonObject receipt, IReadOnlyList<(Observation Entry, string File)> screenshots)
    {
        var cards = string.Concat(screenshots.Select(s =>
            $"\n      <figure>\n        <img src=\"{Js.EscapeHtml(s.File)}\" alt=\"{Js.EscapeHtml($"{s.Entry.Theme} {s.Entry.Width} by {s.Entry.Height}")}\">"
            + $"\n        <figcaption><strong>{Js.EscapeHtml(Js.ToUpperCase(s.Entry.Theme))}</strong> · {s.Entry.Width}×{s.Entry.Height} · containment {(s.Entry.Ok ? "pass" : "fail")}"
            + $"{(s.Entry.VerticalScrollAccepted ? " · readable vertical scroll" : "")}</figcaption>\n      </figure>"));
        var name = Js.EscapeHtml(Path.GetFileName(artifactPath));
        var containment = Js.EscapeHtml(receipt["containment"]!["status"]!.GetValue<string>());
        return $$$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Archify automated browser evidence · {{{name}}}</title>
            <style>
            *{box-sizing:border-box}body{margin:0;padding:24px;background:#e9eef5;color:#172033;font:14px/1.5 ui-monospace,SFMono-Regular,Menlo,Consolas,monospace}header{max-width:1500px;margin:0 auto 18px}h1{margin:0 0 6px;font-size:20px}p{margin:0;color:#526176}.grid{max-width:1500px;margin:auto;display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:18px}figure{margin:0;padding:10px;background:white;border:1px solid #c9d4e3;border-radius:12px;box-shadow:0 10px 30px rgba(15,23,42,.08)}img{display:block;width:100%;height:auto;border:1px solid #e2e8f0}figcaption{padding:9px 4px 2px;color:#526176}@media(max-width:900px){.grid{grid-template-columns:1fr}}
            </style>
            </head>
            <body>
            <header><h1>Automated browser evidence</h1><p>{{{name}}} · visual-check containment {{{containment}}} · perceptual visual review pending</p></header>
            <main class="grid">{{{cards}}}
            </main>
            </body>
            </html>

            """.Replace("\r\n", "\n");
    }

    // summarizeBrowserEvidence (VC:1988-2022): keys upstream leaves undefined are omitted; evidence links only for a published receipt.
    public static JsonObject Summarize(JsonObject receipt, bool published)
    {
        var summary = new JsonObject();
        void Copy(string key)
        {
            if (receipt.TryGetPropertyValue(key, out var value)) summary[key] = value?.DeepClone();
        }
        foreach (var key in new[] { "schemaVersion", "command", "ok", "status", "evidenceKind", "visualReview", "artifact", "provenance", "deliveryReceiptId", "state", "chrome" })
            Copy(key);
        var checks = new JsonObject();
        foreach (var key in new[] { "containment", "readability", "viewerChrome", "themeStates", "captures" })
            if (receipt[key] is JsonObject check && check.TryGetPropertyValue("status", out var status)) checks[key] = status?.DeepClone();
        summary["checks"] = checks;
        Copy("error");
        Copy("publication");
        summary["diagnostics"] = receipt["diagnostics"]?.DeepClone() ?? new JsonArray();
        var sidecars = receipt["sidecars"] as JsonObject;
        var directory = sidecars?["directory"]?.GetValue<string>() is { Length: > 0 } dir ? dir : Path.GetDirectoryName(receipt["artifact"]!["path"]!.GetValue<string>())!;
        string? EvidencePath(JsonNode? file) => file?.GetValue<string>() is { Length: > 0 } name ? NodePath.Resolve(directory, name) : null;
        var evidence = new JsonObject();
        if (published && EvidencePath(sidecars?["receipt"]) is { } receiptPath) evidence["receipt"] = receiptPath;
        if (published && EvidencePath(receipt["captures"]?["contactSheet"]) is { } sheet) evidence["contactSheet"] = sheet;
        var screenshots = published && receipt["captures"]?["screenshots"] is JsonArray captured ? captured : [];
        evidence["screenshots"] = new JsonArray([.. screenshots.Select(entry =>
        {
            var link = new JsonObject();
            if (EvidencePath(entry!["file"]) is { } path) link["path"] = path;
            foreach (var key in new[] { "width", "height", "theme", "resolvedTheme" }) link[key] = entry[key]?.DeepClone();
            return (JsonNode)link;
        })]);
        summary["evidence"] = evidence;
        return summary;
    }
}
