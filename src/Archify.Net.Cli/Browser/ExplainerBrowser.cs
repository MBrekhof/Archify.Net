using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Archify.Net.Cli.Browser;

// archify 3.2.0 explainer/browser-check.mjs (BC) over the IBrowserInspector seam: the explainer's browser gate. The page expressions
// are upstream's, verbatim, with their line numbers; the two page functions are the explainer-gate resources.
internal static class ExplainerBrowser
{
    // EXPLAINER_VIEWPORTS (BC:5)
    public static readonly IReadOnlyList<(int Width, int Height)> Viewports = [(1280, 900), (1440, 900)];

    static readonly string MeasureNativePreviews = Resource("explainer-gate/measure-native-previews.js");
    static readonly string MeasurePublicHtml = Resource("explainer-gate/measure-public-html.js");

    static string Resource(string name)
    {
        using var reader = new StreamReader(typeof(ExplainerBrowser).Assembly.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }

    // `(${measurePublicHtml.toString()})(${javascript})` (BC:115)
    public static string MeasurePublicHtmlExpression(bool javascript) => $"({MeasurePublicHtml})({(javascript ? "true" : "false")})";

    // --- JS reads ---

    static JsonNode? P(JsonNode? node, string key) => node is JsonObject o && o.TryGetPropertyValue(key, out var value) ? value : null;
    static bool IsTrue(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.True;
    static int Length(JsonNode? node) => node is JsonArray a ? a.Count : 0;
    static double Num(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.D() : double.NaN;
    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    // `${node.key}`: undefined when the key is missing
    static string Tpl(JsonNode? node, string key) => node is JsonObject o && o.TryGetPropertyValue(key, out var v) ? Js.String(v) : "undefined";
    static JsonNode? Clone(JsonNode? node) => node?.DeepClone();

    // `[...new Set(list)]`
    static List<string> Distinct(IEnumerable<string> items) => [.. items.Distinct(StringComparer.Ordinal)];

    // --- keyboardTraversal (BC:67-86) ---

    public static async Task<JsonObject> KeyboardTraversalAsync(IBrowserInspector browser, int controlCount)
    {
        await browser.EvaluateAsync("(function(){document.activeElement.blur();window.scrollTo(0,0);})()");
        var reached = new List<string>();   // a Set, in insertion order
        // Radio groups intentionally expose one tab stop; Arrow keys reach siblings.
        async Task<JsonNode?> Record()
        {
            var state = await browser.EvaluateAsync("({key:document.activeElement?.dataset.qaKey,radio:document.activeElement?.type==='radio',name:document.activeElement?.name})");
            if (state is JsonObject o && o.TryGetPropertyValue("key", out var key) && Js.String(key) is var k && !reached.Contains(k)) reached.Add(k);
            return state;
        }
        var arrowGroups = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < controlCount + 3; i++)
        {
            await browser.PressAsync("Tab");
            var state = await Record();
            var name = Tpl(state, "name");
            if (IsTrue(P(state, "radio")) && arrowGroups.Add(name))
            {
                var siblings = Num(await browser.EvaluateAsync($"document.querySelectorAll('input[type=radio][name=\"{name}\"][data-qa-key]').length"));
                for (var step = 0; step < siblings; step++)
                {
                    await browser.PressAsync("ArrowRight");
                    await Record();
                }
            }
        }
        var keys = Js.Stringify(new JsonArray([.. reached.Select(k => (JsonNode?)k)]));
        var missing = await browser.EvaluateAsync($"Array.from(document.querySelectorAll('[data-qa-key]')).filter(e=>!{keys}.includes(e.dataset.qaKey)).map(e=>({{key:e.dataset.qaKey,stableComponent:e.dataset.qaPath,text:e.textContent.trim().slice(0,160),href:e.getAttribute('href')||null,tag:e.tagName,component:e.closest('[data-block-type],.panel')?.id||e.id||e.tagName.toLowerCase(),label:e.getAttribute('aria-label')||e.textContent.trim()||e.name,tabIndex:e.tabIndex,type:e.type,reason:'This active control was not reached by Tab or native radio ArrowRight navigation.'}}))");
        return new JsonObject { ["count"] = controlCount, ["reached"] = reached.Count, ["missing"] = missing, ["ok"] = reached.Count == controlCount };
    }

    // press (BC:145): the key, then one macrotask for the reader's handlers.
    static async Task Press(IBrowserInspector browser, string key)
    {
        await browser.PressAsync(key);
        await browser.EvaluateAsync("new Promise(resolve=>setTimeout(resolve,0))");
    }

    // activate (BC:146)
    static async Task<bool> Activate(IBrowserInspector browser, string selector, string key = "Enter")
    {
        var exists = await browser.EvaluateAsync($"(function(){{const e=document.querySelector({Js.Stringify(JsonValue.Create(selector))});if(!e)return false;e.scrollIntoView({{block:'center'}});e.focus();return document.activeElement===e;}})()");
        if (!exists.Truthy()) return false;
        await Press(browser, key);
        return true;
    }

    // --- inspectExplainerBrowser (BC:148-186) ---

    public static async Task<JsonObject> InspectAsync(IBrowserInspector browser, PublicHtmlOptions options)
    {
        await browser.ResetPageAsync();
        // ponytail: a fresh page per reset has fresh storage, which stands in for upstream's localStorage.clear() init script (BC:154).
        var metrics = await browser.InspectPublicHtmlAsync(options);
        var failures = new JsonArray();
        void Check(string name, bool ok, string reason, string component = "reader", JsonNode? measurement = null)
        {
            if (ok) return;
            var failure = new JsonObject { ["name"] = name, ["reason"] = reason, ["component"] = component };
            if (measurement.Truthy()) failure["measurement"] = measurement;
            failures.Add(failure);
        }
        var nativeErrors = new List<string>();
        var diagram = Text(await browser.EvaluateAsync("document.querySelector('[data-block-type=steps] [data-diagram-link]')?.dataset.diagramLink || document.querySelector('[data-diagram-link]')?.dataset.diagramLink || document.querySelector('[data-open-diagram]')?.dataset.openDiagram || null"));
        if (!string.IsNullOrEmpty(diagram))
        {
            var selector = Text(await browser.EvaluateAsync("document.querySelector('[data-block-type=steps] [data-diagram-link]')?'[data-block-type=steps] [data-diagram-link]':document.querySelector('[data-diagram-link]')?'[data-diagram-link]':'[data-open-diagram]'"))!;
            Check("diagram-keyboard", await Activate(browser, selector), "Diagram link cannot be activated by keyboard.", diagram);
            for (var attempt = 0; attempt < 30; attempt++)
            {
                if (IsTrue(await browser.EvaluateAsync("Boolean(document.querySelector('#diagram-dialog')?.dataset.nativeFocus)"))) break;
                await Task.Delay(100);
            }
            var frame = await browser.EvaluateAsync("(function(){const d=document.querySelector('#diagram-dialog'),f=d.querySelector('iframe');return {open:d.open,frame:Boolean(f),sandbox:f?.getAttribute('sandbox'),loaded:d.dataset.nativeLoaded,ready:d.dataset.nativeReady,loadingHidden:d.querySelector('.dialog-loading')?.hidden,visible:document.visibilityState,frameRect:f?{width:f.getBoundingClientRect().width,height:f.getBoundingClientRect().height}:null,error:d.querySelector('.dialog-loading')?.textContent};})()");
            Check("diagram-viewer", IsTrue(P(frame, "open")) && IsTrue(P(frame, "frame")), "Diagram activation did not open the canonical Viewer.", diagram, Clone(frame));
            var focus = await browser.EvaluateAsync($"(function(){{const link=document.querySelector({Js.Stringify(JsonValue.Create(selector))}),hash=link.dataset.diagramHash||'',edges=JSON.parse(link.dataset.diagramEdges||'[]'),bundle=JSON.parse(document.getElementById('archify-explainer-prepared').textContent),child=bundle.preparedDiagrams.diagrams[link.dataset.diagramLink||link.dataset.openDiagram],nodes=hash.startsWith('focus=')?[decodeURIComponent(hash.slice(6))]:hash.startsWith('route=')?hash.slice(6).split('~').map(decodeURIComponent):[...new Set((child.semantic?.edges||[]).filter(e=>edges.includes(e.id)).flatMap(e=>[e.from,e.to]))];let actual;try{{actual=JSON.parse(document.querySelector('#diagram-dialog').dataset.nativeFocus||'null');}}catch{{}}return {{component:link.closest('[data-block-type]')?.id||link.dataset.diagramLink,nodes,edges,actual,frame:{{ready:document.querySelector('#diagram-dialog').dataset.nativeReady,loaded:document.querySelector('#diagram-dialog').dataset.nativeLoaded,loadingHidden:document.querySelector('#diagram-dialog .dialog-loading').hidden}}}};}})()");
            var native = await browser.InspectNativeViewerAsync();
            metrics["nativeViewer"] = native;
            nativeErrors.AddRange(P(native, "errors").AsArrayOrEmpty().Select(Js.String));
            var focusNodes = P(focus, "nodes").AsArrayOrEmpty().Select(Js.String).ToList();
            var focusEdges = P(focus, "edges").AsArrayOrEmpty().Select(Js.String).ToList();
            if (focusNodes.Count > 0 || focusEdges.Count > 0)
            {
                var nativeNodes = P(native, "nodes") is JsonArray nn ? nn.Select(Js.String).ToList() : null;
                var nativeEdges = P(native, "edges") is JsonArray ne ? ne.Select(Js.String).ToList() : null;
                var measurement = (focus as JsonObject)?.DeepClone().AsObject() ?? [];
                measurement["dialog"] = Clone(frame);
                measurement["native"] = native.DeepClone();
                Check("diagram-focus", IsTrue(P(native, "available")) && Text(P(native, "ready")) == "complete" && IsTrue(P(native, "hasViewer")) && P(native, "active").Truthy()
                    && focusNodes.Count == (nativeNodes?.Count ?? -1) && focusNodes.All(id => nativeNodes?.Contains(id) == true)
                    && (focusEdges.Count == 0 || focusEdges.Count == (nativeEdges?.Count ?? -1)) && focusEdges.All(id => nativeEdges?.Contains(id) == true),
                    "The canonical Viewer did not focus the step’s exact nodes and edges.", Js.String(P(focus, "component")), measurement);
            }
            await Press(browser, "Escape");
        }
        var nativeText = await browser.EvaluateAsync($"({MeasureNativePreviews})()") as JsonArray ?? [];
        foreach (var entry in nativeText)
        {
            var component = Js.String(P(entry, "component"));
            Check("diagram-fit", Text(P(entry, "fit")) != "insufficient" && Num(P(entry, "scrollWidth")) <= Num(P(entry, "clientWidth")) + 1 && Length(P(entry, "outsidePreview")) == 0,
                "The native preview cannot fit its complete graph at the required text size.", component, Clone(entry));
            Check("diagram-text", P(entry, "minimum") is not null && Num(P(entry, "minimum")) >= 10 && (options.Width < 1440 || Num(P(entry, "ratio")) >= 0.75),
                "Native diagram text is below its viewport reading floor (10px; 75% of body at 1440).", component, Clone(entry));
        }
        metrics["nativeText"] = nativeText;
        metrics["interactionFailures"] = failures;
        // upstream's subscription on the first page's session (BC:152) sees every error of that page, the interaction phase included
        var interactionErrors = browser.PageErrors.Concat(nativeErrors).ToList();

        var noJavaScript = new List<(string Theme, JsonObject Metrics)>();
        foreach (var theme in new[] { "light", "dark" })
        {
            await browser.ResetPageAsync();
            noJavaScript.Add((theme, await browser.InspectPublicHtmlAsync(options with { Theme = theme, Javascript = false, Keyboard = false, ScreenshotPath = null })));
        }
        await browser.ResetPageAsync();
        var print = await browser.InspectPublicHtmlAsync(options with { Theme = "light", Media = "print", Keyboard = false, ScreenshotPath = null });
        static bool PanelsReadable(JsonObject entry) => Length(entry["panels"]) > 0
            && entry["panels"].AsArrayOrEmpty().All(p => IsTrue(P(p, "visible")) && Js.Trim(Text(P(p, "text")) ?? "").Length > 0);
        static bool StaticReading(JsonObject entry) => entry["staticReading"].AsArrayOrEmpty().All(p => IsTrue(P(p, "visible")) && P(p, "text").Truthy());
        metrics["noJavaScript"] = new JsonArray([.. noJavaScript.Select(state => (JsonNode)new JsonObject
        {
            ["theme"] = state.Theme,
            ["ok"] = PanelsReadable(state.Metrics) && StaticReading(state.Metrics) && Length(state.Metrics["interactiveVisible"]) == 0
                && !JsonNode.DeepEquals(noJavaScript[0].Metrics["background"], noJavaScript[1].Metrics["background"]),
            ["panels"] = Clone(state.Metrics["panels"]), ["interactiveVisible"] = Clone(state.Metrics["interactiveVisible"]), ["background"] = Clone(state.Metrics["background"]),
            ["measurementMethod"] = Clone(state.Metrics["measurementMethod"]), ["fontReadiness"] = Clone(state.Metrics["fontReadiness"]),
        })]);
        var typical = await browser.EvaluateAsync("({links:Array.from(document.querySelectorAll('.typical-ref')).map(e=>e.getAttribute('href')),notes:Array.from(document.querySelectorAll('.typical-footnotes li')).map(e=>({id:e.id,text:e.textContent,visible:getComputedStyle(e).display!=='none'&&e.getBoundingClientRect().height>0}))})");
        var notes = P(typical, "notes").AsArrayOrEmpty().ToList();
        metrics["print"] = new JsonObject
        {
            ["ok"] = PanelsReadable(print) && Length(print["interactiveVisible"]) == 0 && StaticReading(print)
                && P(typical, "links").AsArrayOrEmpty().All(link => notes.Any(note => Js.String(link) == "#" + Js.String(P(note, "id")) && IsTrue(P(note, "visible"))
                    && Js.Trim(Text(P(note, "text")) ?? "").Length > 0)),
            ["panels"] = Clone(print["panels"]), ["interactiveVisible"] = Clone(print["interactiveVisible"]), ["typical"] = typical,
            ["measurementMethod"] = Clone(print["measurementMethod"]), ["fontReadiness"] = Clone(print["fontReadiness"]),
        };
        metrics["consoleErrors"] = new JsonArray([.. Distinct([.. metrics["consoleErrors"].AsArrayOrEmpty().Select(Js.String), .. interactionErrors,
            .. noJavaScript.SelectMany(x => x.Metrics["consoleErrors"].AsArrayOrEmpty().Select(Js.String)), .. print["consoleErrors"].AsArrayOrEmpty().Select(Js.String)])
            .Select(e => (JsonNode?)e)]);
        return metrics;
    }

    // --- explainerObservation (BC:188-191) ---

    public static JsonObject Observation(int width, int height, string theme, JsonObject metrics)
    {
        var observation = metrics.DeepClone().AsObject();
        var overflowX = Num(metrics["scrollWidth"]) > Num(metrics["innerWidth"]) + 1 || Length(metrics["overflowElements"]) > 0;
        observation["width"] = width;
        observation["height"] = height;
        observation["theme"] = theme;
        observation["overflowX"] = overflowX;
        observation["overflowY"] = Num(metrics["scrollHeight"]) > Num(metrics["innerHeight"]);
        observation["verticalScrollAccepted"] = !overflowX;
        observation["ok"] = !overflowX;
        observation["readabilityOk"] = Length(metrics["smallText"]) == 0;
        observation["viewerChromeOk"] = Length(metrics["occluded"]) == 0;
        return observation;
    }

    // --- explainerObservationDiagnostics (BC:192-207) and repairExample (BC:208) ---

    public static JsonArray ObservationDiagnostics(string artifact, IReadOnlyList<JsonObject> allObservations)
    {
        var diagnostics = new JsonArray();
        var locations = new Dictionary<string, int>(StringComparer.Ordinal);
        string? sourceInput = null;
        try
        {
            var (lines, payload) = ExplainerPipeline.ScanIds(File.ReadAllText(artifact, Encoding.UTF8));
            locations = lines;
            if (payload is not null)
            {
                var bundle = JsonNode.Parse(payload);
                sourceInput = Text(P(bundle, "sourcePath"));
                var sourceMap = P(bundle, "sourceMap");
                int? Line(string pointer) => Num(P(P(sourceMap, pointer), "line")) is var line && line != 0 && !double.IsNaN(line) ? (int)line : null;
                var index = 0;
                foreach (var panel in P(P(bundle, "document"), "panels")!.AsArray())
                {
                    var panelId = Js.String(P(panel, "id"));
                    locations[panelId] = Line($"/panels/{index}") ?? locations.GetValueOrDefault(panelId, 1);
                    var j = 0;
                    foreach (var block in P(panel, "blocks")!.AsArray())
                    {
                        var blockId = Js.String(P(block, "id"));
                        locations[blockId] = Line($"/panels/{index}/blocks/{j}") ?? locations.GetValueOrDefault(blockId, 1);
                        j++;
                    }
                    index++;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or NullReferenceException) { }
        foreach (var entry in allObservations)
        {
            var failures = new List<JsonObject>();
            JsonObject Failure(string name, string reason, string component) => new() { ["name"] = name, ["reason"] = reason, ["component"] = component };
            if (IsTrue(entry["overflowX"]))
                failures.Add(Failure("horizontal-overflow", "Rendered content exceeds the horizontal viewport.",
                    P(entry["overflowElements"].AsArrayOrEmpty().FirstOrDefault(), "component") is { } owner && owner.Truthy() ? Js.String(owner) : "document"));
            foreach (var issue in entry["keyboardIssues"].AsArrayOrEmpty())
            {
                var failure = new JsonObject { ["name"] = "keyboard" };
                foreach (var (key, value) in issue!.AsObject()) failure[key] = Clone(value);
                failures.Add(failure);
            }
            if (entry["keyboardTraversal"].Truthy() && !IsTrue(P(entry["keyboardTraversal"], "ok")))
                foreach (var m in P(entry["keyboardTraversal"], "missing").AsArrayOrEmpty())
                    failures.Add(Failure("keyboard-traversal", $"{Tpl(m, "reason")} {Tpl(m, "tag")} {Tpl(m, "label")}", Js.String(P(m, "component"))));
            foreach (var reason in entry["consoleErrors"].AsArrayOrEmpty()) failures.Add(Failure("console-error", Js.String(reason), "reader"));
            foreach (var x in entry["references"].AsArrayOrEmpty()) failures.Add(Failure("reference", $"Unresolved reference {Tpl(x, "href")}.", Js.String(P(x, "component"))));
            foreach (var id in entry["duplicateIds"].AsArrayOrEmpty()) failures.Add(Failure("duplicate-id", $"Duplicate DOM ID {Js.String(id)}.", "document"));
            foreach (var x in entry["smallText"].AsArrayOrEmpty()) failures.Add(Failure("readability", $"Rendered text is {Tpl(x, "fontSize")}px, below 10px.", Js.String(P(x, "component"))));
            foreach (var x in entry["occluded"].AsArrayOrEmpty()) failures.Add(Failure("control-occluded", "Another element covers an active control.", Js.String(P(x, "component"))));
            failures.AddRange(entry["interactionFailures"].AsArrayOrEmpty().OfType<JsonObject>());
            foreach (var x in entry["noJavaScript"].AsArrayOrEmpty().Where(x => !IsTrue(P(x, "ok"))))
                failures.Add(Failure("no-javascript", $"Static reading content or inactive controls fail with JavaScript disabled ({Tpl(x, "theme")}).", "document"));
            if (!IsTrue(P(entry["print"], "ok")))
                failures.Add(Failure("print", "Print hides necessary reading content or displays inactive controls.", "document"));
            var theme = Js.String(entry["theme"]);
            if (Text(entry["resolvedTheme"]) != theme)
                failures.Add(Failure("theme", $"Requested {theme}; resolved {Tpl(entry, "resolvedTheme")}.", "theme"));
            foreach (var fail in failures)
            {
                var name = Js.String(fail["name"]);
                var reason = fail["reason"];
                var component = Js.String(fail["component"]);
                var subject = new JsonObject { ["artifact"] = artifact };
                if (!string.IsNullOrEmpty(sourceInput)) subject["input"] = sourceInput;
                subject["line"] = locations.TryGetValue(component, out var line) && line != 0 ? line : 1;
                subject["component"] = Clone(fail["component"]);
                subject["viewport"] = new JsonObject { ["width"] = Clone(entry["width"]), ["height"] = Clone(entry["height"]), ["theme"] = Clone(entry["theme"]) };
                var evidence = new JsonObject { ["reason"] = Clone(reason), ["example"] = RepairExample(name) };
                if (fail["measurement"].Truthy()) evidence["measurement"] = Clone(fail["measurement"]);
                diagnostics.Add(new JsonObject
                {
                    ["code"] = $"explainer/browser-{name}", ["severity"] = "error", ["message"] = Clone(reason), ["subject"] = subject, ["evidence"] = evidence,
                    ["supportedFixes"] = new JsonArray(RepairExample(name)),
                });
            }
        }
        return diagnostics;
    }

    static string RepairExample(string name) => name == "horizontal-overflow" ? "Use min-width:0 and bounded horizontal scrolling for tables or code."
        : name.StartsWith("keyboard", StringComparison.Ordinal) ? "Use a native button/link/input with visible focus and keyboard activation."
        : name is "no-javascript" or "print" ? "Keep canonical content visible and hide interactive controls in no-JavaScript and print styles."
        : name == "diagram-text" ? "Increase the diagram panel span until projected node text reaches 75% of body text."
        : "Repair the reported component and re-render the source.";

    // The five receipt groups of an explainer (visual-check.mjs:2689-2693).
    public static void AddReceiptGroups(JsonObject receipt, IReadOnlyList<JsonObject> all)
    {
        static JsonObject Group(bool pass, IEnumerable<JsonObject> observations) =>
            new() { ["status"] = pass ? "pass" : "fail", ["observations"] = new JsonArray([.. observations.Select(o => (JsonNode)o)]) };
        static bool CommonQa(JsonObject e) => Length(e["consoleErrors"]) == 0 && Length(e["duplicateIds"]) == 0 && Length(e["keyboardIssues"]) == 0
            && IsTrue(P(e["keyboardTraversal"], "ok"));
        static JsonObject At(JsonObject e) => new() { ["width"] = Clone(e["width"]), ["theme"] = Clone(e["theme"]) };
        receipt["commonQa"] = Group(all.All(CommonQa), all.Select(e => { var o = At(e); o["ok"] = CommonQa(e); return o; }));
        receipt["interactions"] = Group(all.All(e => Length(e["interactionFailures"]) == 0),
            all.Select(e => { var o = At(e); o["ok"] = Length(e["interactionFailures"]) == 0; o["failures"] = Clone(e["interactionFailures"]); return o; }));
        receipt["references"] = Group(all.All(e => Length(e["references"]) == 0),
            all.Select(e => { var o = At(e); o["ok"] = Length(e["references"]) == 0; o["failures"] = Clone(e["references"]); return o; }));
        static bool NoJs(JsonObject e) => e["noJavaScript"].AsArrayOrEmpty().All(n => IsTrue(P(n, "ok")));
        receipt["noJavaScript"] = Group(all.All(NoJs), all.Select(e => { var o = At(e); o["ok"] = NoJs(e); o["states"] = Clone(e["noJavaScript"]); return o; }));
        receipt["print"] = Group(all.All(e => IsTrue(P(e["print"], "ok"))), all.Select(e =>
        {
            var o = new JsonObject { ["width"] = Clone(e["width"]) };
            foreach (var (key, value) in e["print"] as JsonObject ?? []) o[key] = Clone(value);
            return o;
        }));
    }
}
