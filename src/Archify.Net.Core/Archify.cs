using System.Text;
using System.Text.Json.Nodes;

namespace Archify.Net;

// One typed renderer: render-<type>.mjs's module body from loadDiagram to writeDiagram.
internal interface IDiagramRenderer
{
    (string Svg, JsonArray? Cards, LayoutReport? Layout) Render(RenderContext ctx);
}

internal static class Renderers
{
    public static IDiagramRenderer For(DiagramType type) => type switch
    {
        DiagramType.Architecture => new ArchitectureRenderer(),
        DiagramType.Sequence => new SequenceRenderer(),
        DiagramType.Dataflow => new DataflowRenderer(),
        DiagramType.Lifecycle => new LifecycleRenderer(),
        DiagramType.Class => new ClassRenderer(),
        DiagramType.Tree => new TreeRenderer(),
        DiagramType.Timeline => new TimelineRenderer(),
        DiagramType.Waterfall => new WaterfallRenderer(),
        DiagramType.Workflow => new WorkflowRenderer(),
        DiagramType.Erd => new ErdRenderer(),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}

// Not `Archify`: a class of that name inside namespace Archify.Net cannot be named from consumer code
// (`Archify.Render` binds to the root namespace, CS0234).
public static class ArchifyNet
{
    /// <summary>Runs the load gates and the typed renderer; problems come back as diagnostics, never as exceptions.</summary>
    /// <remarks>
    /// Known deviations from upstream (full list in README.md, "Known deviations from upstream"):
    /// Word breaks: <c>Js.WordBreaks</c> matches V8's <c>Intl.Segmenter</c> word granularity except that Han, Hiragana, Katakana, Thai, Lao,
    /// Khmer and Myanmar runs split per grapheme (ICU uses a dictionary; visible in sequence notes and wrapped automatic-showcase dataflow
    /// labels), V8 splits inside grapheme clusters the port keeps whole (a Prepend such as U+0600 before a space, a jamo next to a Hangul
    /// syllable) while the port splits across clusters V8 joins (a ZWJ before a pictograph after a letter, UAX #29 WB3c; a Format/Prepend-led
    /// cluster after a letter), and a non-POSIX host locale is assumed (under <c>LANG=C</c> V8 splits <c>api.example.com</c>
    /// at the dots). Every other text path, zh-CN included, is byte-exact.
    /// A workflow render returns the compiler receipt (upstream's <c>--layout-json</c>) as <c>Layout</c>; dataflow returns null.
    /// Time zones use the operating system's zone table, not Node's tzdata: before a zone's first transition the port shows the base
    /// offset (an <c>Asia/Shanghai</c> event at <c>0001-01-01T00:00:00Z</c> is <c>UTC+08:06</c> upstream, <c>UTC+08:00</c> here), and before
    /// the table's first adjustment rule that rule is extrapolated backwards (<c>Pacific/Chatham</c> and <c>Australia/Lord_Howe</c> in 1970
    /// read +13:45/+11:00 instead of +12:45/+10:00). UTC and fixed offsets are exact for every year.
    /// <c>Date.parse</c> outside the timeline schema's timestamp shape is NaN in the port; the schema gate rejects such strings before
    /// any renderer runs.
    /// An erd entity with neither <c>row</c>/<c>col</c> nor <c>pos</c> whose relationships carry labels fails with <c>internal/unclassified</c>
    /// as upstream does, but the message and evidence carry the .NET exception where upstream has <c>Cannot read properties of undefined (reading '0')</c>
    /// and the JS <c>TypeError</c>.
    /// Repository evidence (<c>RenderOptions.RepoRoot</c>): <c>repository-evidence/root-identity-indeterminate</c> is unreachable (the root is
    /// compared with <c>git rev-parse --show-prefix</c>; upstream reaches it only through a mocked stat); Node's spawn error strings are
    /// synthesised (<c>spawnSync git ENOENT</c>, <c>spawnSync git ENOBUFS</c>) and an errno outside the port's table prints its number;
    /// <c>root-unreadable</c> names the whole requested root where Node names the first missing path component; the root is resolved only
    /// through its final link (reached through a junctioned or symlinked parent, <c>repoRoot</c> in subjects and the paths in the
    /// "not a Git repository" and <c>root-not-top-level</c> messages differ from Node's, the verdict is the same); <c>Render</c> with
    /// <c>RepoRoot</c> runs git synchronously with no timeout, as upstream; and five non-ASCII hosts WHATWG URL accepts are rejected because
    /// <c>IdnMapping</c> enforces the hyphen, label-length and non-empty-label rules, by input: <c>https://-bücher.example/x/y</c>,
    /// <c>https://bücher-.example/x/y</c>, <c>https://ü</c> + 70 <c>a</c> + <c>.example/x/y</c> (a 71-character label),
    /// <c>https://bücher..example/o/r</c> and <c>https://xn--bcher-kva..example/o/r</c>.
    /// </remarks>
    /// <param name="afterLoadGates">Runs after the load gates and before brand marks and rendering, where cli.mjs's loadDiagram
    /// resolves the output path (cli.mjs:87-99). An <see cref="ArchifyDiagnosticException"/> it throws becomes the render failure.</param>
    public static RenderResult Render(DiagramType type, JsonNode diagram, RenderOptions? options = null, Action? afterLoadGates = null) =>
        Run(type, diagram, options, afterLoadGates, (ctx, root) =>
        {
            var (svg, cards, layout) = Renderers.For(type).Render(ctx);
            var meta = root["meta"]!.AsObject();
            var cardsHtml = Utils.RenderCards(cards);
            var html = TemplateWriter.Apply(Resources.Text("assets/template.html"), meta.Str("title")!, meta.Str("subtitle"), svg, cardsHtml,
                meta.Str("locale"), string.IsNullOrEmpty(meta.Str("visual_preset")) ? "classic" : meta.Str("visual_preset")!, ctx.SourceEvidence, ctx.Locale);
            return new RenderResult(html, svg, cardsHtml, ctx.Diagnostics.Recorded, layout, null, []);
        }, (error, diagnostics, thrown) => new RenderResult("", "", "", diagnostics, null, error, thrown));

    /// <summary><c>validate &lt;type&gt; &lt;input&gt; --layout-json</c>: the same gates as <see cref="Render"/>, then the layout
    /// report without SVG or HTML. Architecture and workflow only, as upstream (A:6849-6855).</summary>
    public static LayoutOutcome Layout(DiagramType type, JsonNode diagram, RenderOptions? options = null)
    {
        if (type is not (DiagramType.Architecture or DiagramType.Workflow))
            throw new ArgumentOutOfRangeException(nameof(type), type, "--layout-json supports architecture and workflow only");
        return Run(type, diagram, options, null,
            (ctx, _) => type == DiagramType.Architecture ? ArchitectureRenderer.LayoutMode(ctx) : WorkflowRenderer.LayoutMode(ctx),
            (error, diagnostics, _) => new LayoutOutcome(false, null, error, diagnostics));
    }

    // cli.mjs loadDiagramWithBrandMarks, then `body`; anything thrown becomes `failure(error, diagnostics, thrown)` as
    // installRendererDiagnosticBoundary's rendererFailure does (an unclassified error becomes internal/unclassified).
    static T Run<T>(DiagramType type, JsonNode diagram, RenderOptions? options, Action? afterLoadGates, Func<RenderContext, JsonObject, T> body,
        Func<string, IReadOnlyList<Diagnostic>, IReadOnlyList<Diagnostic>, T> failure)
    {
        // Ruling 3: a non-object root reports input/json-parse the way DiagramLoader.Parse does, never throws.
        if (diagram is not JsonObject)
        {
            var parse = DiagramLoader.ParseFailure("the diagram root must be an object");
            return failure(parse.Message, parse.Diagnostics, parse.Diagnostics);
        }
        var root = DiagramLoader.Normalize(diagram.DeepClone().AsObject());
        var ctx = new RenderContext(type, root, options ?? new RenderOptions());
        try
        {
            DiagramLoader.Load(type, root, ctx);                                   // cli.mjs loadDiagram gates
            afterLoadGates?.Invoke();                                              // the CLI's resolveOutputPath
            ctx.Brands.PrepareDiagramBrandMarks(type, root, ctx.Diagnostics);      // loadDiagramWithBrandMarks
            return body(ctx, root);
        }
        catch (Exception ex) when (ex is not NotSupportedException)
        {
            var (error, diagnostics, thrown) = ctx.Diagnostics.RendererFailure(ex);
            return failure(error, diagnostics, thrown);
        }
    }

    // scripts/check-render-output.mjs on an artifact's HTML: the report minus `file`.
    public static CheckResult Check(string html, CheckOptions? options = null) => Check(new UTF8Encoding(false).GetBytes(html), options);

    // The artifact's bytes, as upstream reads the file: sha256 and length over the bytes, the scan over bytes.toString('utf8').
    public static CheckResult Check(byte[] html, CheckOptions? options = null)
    {
        if (options?.RequireProvenance == true) throw new NotSupportedException("provenance checks are in the archify CLI (check --require-provenance)");
        var report = ArtifactChecker.Check(html);
        return new CheckResult(report["ok"]!.GetValue<bool>(), report);
    }

    // `archify validate`: the load gates and renderer, then the artifact checker on what they emitted, as one diagnostic list.
    public static IReadOnlyList<Diagnostic> Validate(DiagramType type, JsonNode diagram, RenderOptions? options = null)
    {
        var render = Render(type, diagram, options);
        if (render.Html.Length == 0) return render.Diagnostics;   // both failure paths of Render emit no HTML
        var check = Check(render.Html);
        return check.Ok ? render.Diagnostics : [.. render.Diagnostics, .. CheckerDiagnostics.From(check.Report)];
    }
}

// bin/archify.mjs:2660-2747 COMPOSITION_CHECKS, CHECK_FIXES, COMPOSITION_FIXES, checkerDiagnostics, compositionFixes.
// Diagnostics are built as upstream's diagnostic() builds them: no normalizeDiagnostic, so an empty fix string survives.
public static class CheckerDiagnostics
{
    static readonly HashSet<string> COMPOSITION_CHECKS = new(StringComparer.Ordinal)
    {
        "label_route_clearance", "relationship_crossings", "relationship_corridors", "container_border_runs", "route_rhythm",
    };

    static readonly Dictionary<string, string[]> CHECK_FIXES = new(StringComparer.Ordinal)   // lookup only, never enumerated
    {
        ["single_svg"] = ["remove additional SVG roots so the artifact contains exactly one diagram SVG"],
        ["finite_svg"] = ["replace non-finite coordinates before rendering again"],
        ["orthogonal_arrows"] = ["use renderer-supported orthogonal routing controls"],
        ["legend_clearance"] = ["move the route or enlarge the viewBox so relationships do not enter the legend"],
    };

    static readonly Dictionary<string, string[]> COMPOSITION_FIXES = new(StringComparer.Ordinal)   // lookup only
    {
        ["composition/proper-crossing"] = ["if authored via/route/channelX/channelY controls exist and are not required by the user, remove them to let the renderer re-plan; otherwise preserve that intent and adjust route/via or channel coordinates so unrelated relationships use separate corridors"],
        ["composition/ambiguous-corridor"] = ["if authored via/route/channelX/channelY controls exist and are not required by the user, remove them to let the renderer re-plan; otherwise preserve that intent and adjust route/via or channel coordinates so unrelated relationships do not visually merge"],
        ["composition/container-border-run"] = ["if authored via/route/channelX/channelY controls exist and are not required by the user, remove them to let the renderer re-plan; otherwise preserve that intent and route across the frame perpendicularly through a clear opening"],
        ["composition/label-route-clearance"] = ["if authored labelAt/labelDx/labelDy/labelSegment controls exist and are not required by the user, remove them to let the renderer re-plan; otherwise preserve that intent and adjust labelAt, labelDx, labelDy, labelSegment, message y, or the other relationship route"],
        ["composition/label-canvas-containment"] = ["if authored labelAt/labelDx/labelDy/labelSegment controls exist and are not required by the user, remove them to let the renderer re-plan; otherwise preserve that intent and adjust labelAt, labelDx, labelDy, or labelSegment so the label rect stays inside the viewBox, or enlarge meta.viewBox"],
        ["composition/micro-segment"] = ["if authored via/route/channelX/channelY controls exist and are not required by the user, remove them to let the renderer re-plan; otherwise preserve that intent and move the route/channel/via point so every visible segment is at least 8px"],
        ["composition/short-interior-segment"] = ["if authored via/route/channelX/channelY controls exist and are not required by the user, remove them to let the renderer re-plan; otherwise preserve that intent and move the route/channel/via point so every interior turn has at least 16px"],
    };

    static readonly string[] PathCodes = ["artifact/svg-path-malformed", "artifact/svg-path-unsupported"];

    public static IReadOnlyList<Diagnostic> From(JsonObject? checker)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var node in checker.Obj("composition").Arr("issues").AsArrayOrEmpty())
        {
            if (node is not JsonObject issue || issue.Str("severity") != "error") continue;
            var code = issue.Str("code") ?? "";
            var evidence = new JsonObject();   // const { severity, code, relationship, nodeId, ...evidence } = issue
            foreach (var (key, value) in issue)
                if (key is not ("severity" or "code" or "relationship" or "nodeId")) evidence[key] = value?.DeepClone();
            var subject = issue["relationship"].Truthy()
                ? new JsonObject { ["relationship"] = issue["relationship"]!.DeepClone() }
                : new JsonObject { ["check"] = "composition" };
            if (!issue["relationship"].Truthy() && issue["nodeId"].Truthy()) subject["nodeId"] = issue["nodeId"]!.DeepClone();
            diagnostics.Add(new(code, Severity.Error, $"Final artifact failed {code}.", subject, evidence, CompositionFixes(issue), null));
        }
        foreach (var node in checker.Arr("checks").AsArrayOrEmpty())
        {
            var name = node.Str("name") ?? "";
            if (node?["ok"].Truthy() == true || COMPOSITION_CHECKS.Contains(name)) continue;
            if (name == "svg_path_data")
            {
                var pathDiagnostics = checker.Arr("diagnostics").AsArrayOrEmpty()
                    .Where(item => item.Str("severity") == "error" && PathCodes.Contains(item.Str("code"))).ToList();
                if (pathDiagnostics.Count > 0)
                {
                    diagnostics.AddRange(pathDiagnostics.Select(item => new Diagnostic(item.Str("code")!, Severity.Error, item.Str("message") ?? "",
                        item.Obj("subject")?.DeepClone().AsObject() ?? new JsonObject(), item.Obj("evidence")?.DeepClone().AsObject() ?? new JsonObject(),
                        [.. item.Arr("supportedFixes").AsArrayOrEmpty().Select(f => Js.PrimitiveString(f))], null)));
                    continue;
                }
            }
            var details = node!["details"].Truthy() ? node["details"]!.DeepClone() : new JsonArray();
            var firstDetail = details.AsArrayOrEmpty().FirstOrDefault(d => d.Truthy());
            diagnostics.Add(new($"artifact/{name.Replace('_', '-')}", Severity.Error,
                firstDetail is not null ? Js.PrimitiveString(firstDetail) : $"Final artifact failed {name}.",
                new JsonObject { ["check"] = name }, new JsonObject { ["details"] = details },
                CHECK_FIXES.GetValueOrDefault(name) ?? [], null));
        }
        return diagnostics.Count > 0 ? diagnostics : [new("artifact/check-failed", Severity.Error,
            "Final artifact check failed without a classified diagnostic.", new JsonObject { ["check"] = "unknown" }, new JsonObject(), [], null)];
    }

    static IReadOnlyList<string> CompositionFixes(JsonObject issue)
    {
        var code = issue.Str("code");
        if (code == "composition/viewport-height")
        {
            // String(issue.detail || '').replace(/^\[[^\]]+\]\s*/, '')
            var detail = issue["detail"].Truthy() ? Js.PrimitiveString(issue["detail"]) : "";
            var close = detail.StartsWith('[') ? detail.IndexOf(']', 1) : -1;
            if (close > 1) { var at = close + 1; while (at < detail.Length && Js.IsSpace(detail[at])) at++; detail = detail[at..]; }
            return [detail];
        }
        if (code != "composition/desktop-readability") return code is not null && COMPOSITION_FIXES.TryGetValue(code, out var fixes) ? fixes : [];
        var sourceFontPx = Number(issue, "sourceFontPx");
        var actualBudgetPx = Number(issue, "availableDiagramWidth");
        var hardFloorPx = Number(issue, "minimumProjectedFontPx");
        var viewBoxWidth = Number(issue, "viewBoxWidth");
        const string preserveIntent = "Preserve the semantic text and any supplied coordinates, routes, sides, channels, and labels.";
        var readerCap = issue.Str("budgetBasis") == "recognized-declared-wide"
            ? $" The declared Reader reports a {TemplateString(issue, "budgetLimit")} limit and {Js.Num(actualBudgetPx)}px actual diagram budget; do not assume an uncapped viewport."
            : "";
        if (!(double.IsFinite(sourceFontPx) && double.IsFinite(actualBudgetPx) && double.IsFinite(hardFloorPx) && double.IsFinite(viewBoxWidth))
            || actualBudgetPx <= 0 || hardFloorPx <= 0 || viewBoxWidth <= 0)
            return [$"{preserveIntent} Repair the measured source font, desktop budget, or complete viewBox width; position-only label controls do not change projected text size."];
        if (sourceFontPx < hardFloorPx)
            return [$"{preserveIntent} The diagnosed {Js.Num(sourceFontPx)}px source text is below the {Js.Num(hardFloorPx)}px hard floor even at scale 1, so use a renderer-supported semantic text-size setting or renderer-level fix. Position-only label controls cannot repair its projection.{readerCap}"];
        var maximumViewBoxWidth = Math.Floor(sourceFontPx * actualBudgetPx / hardFloorPx);
        return [$"{preserveIntent} Compactly reflow automatic spacing and empty corridors so the complete viewBox width is at most {Js.Num(maximumViewBoxWidth)}px (current {Js.Num(viewBoxWidth)}px; {Js.Num(sourceFontPx)}px source text at {Js.Num(actualBudgetPx)}px desktop budget). If supplied geometry fixes that width, use a renderer-supported semantic text-size setting or renderer-level fix instead. Position-only label controls cannot repair its projection.{readerCap}"];
    }

    // JS Number(issue[key]). ponytail: arrays and objects read as NaN (JS coerces [5] to 5); the checker emits neither here.
    static double Number(JsonObject issue, string key) => !issue.TryGetPropertyValue(key, out var value) ? double.NaN : value switch
    {
        null => 0,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? 1 : 0,
        JsonValue v when v.TryGetValue<string>(out var s) => Js.ToNumber(s),
        JsonValue v => v.D(),
        _ => double.NaN,
    };

    // `${issue[key]}`: a missing key is undefined.
    static string TemplateString(JsonObject issue, string key) => issue.TryGetPropertyValue(key, out var value) ? Js.PrimitiveString(value) : "undefined";
}
