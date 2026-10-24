using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Archify.Net.Cli.Browser;

// Q4: the browser is Playwright's pinned Chromium, the headless shell `archify install-browser` installs, with no system-Chrome
// fallback. resolveExecutable is doctor's probe; null (none installed) is upstream's "Chrome unavailable": skipped, exit 2.
internal sealed class PlaywrightInspectorFactory(Func<string?> resolveExecutable) : IBrowserInspectorFactory
{
    // upstream's startup window (VC:56)
    const float StartupTimeoutMs = 90_000;

    public string? ResolveExecutable() => resolveExecutable();

    public async Task<IBrowserInspector> LaunchAsync(string executable)
    {
        var playwright = await Playwright.CreateAsync();
        try
        {
            return new PlaywrightInspector(playwright,
                await playwright.Chromium.LaunchAsync(new() { ExecutablePath = executable, Headless = true, Timeout = StartupTimeoutMs }));
        }
        catch (Exception error)
        {
            playwright.Dispose();
            if (error is TimeoutException) throw new BrowserEvidenceException(error.Message) { Code = BrowserEvidenceException.StartupTimeout };
            throw;
        }
    }
}

// upstream's ChromeVisualBrowser.inspect (VC:1584-1817) on Playwright: a new page per viewport at device scale factor 1, the
// snapshot's file URL with ?theme=, the load event, the verbatim viewer-gate scripts (Task 7), then the viewport PNG when capturing.
sealed class PlaywrightInspector(IPlaywright playwright, IBrowser browser) : IBrowserInspector
{
    static readonly string Settle = Resource("viewer-gate/settle.js");
    static readonly string Metrics = Resource("viewer-gate/metrics.js");

    static string Resource(string name)
    {
        using var reader = new StreamReader(typeof(PlaywrightInspector).Assembly.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }

    public async Task<ViewportMetrics> InspectAsync(string snapshotPath, int width, int height, string theme, string? screenshotPath)
    {
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = width, Height = height }, DeviceScaleFactor = 1 });
        try
        {
            await page.GotoAsync($"{new Uri(snapshotPath).AbsoluteUri}?theme={theme}", new() { WaitUntil = WaitUntilState.Load });
            // The settle result is dropped and the metrics cross as JSON text, as CDP's returnByValue hands them to upstream
            // (EvaluateAsync<JsonElement> would add `$id` keys).
            await page.EvaluateAsync($"Promise.resolve({Settle}).then(function () {{}})");
            var metrics = JsonSerializer.Deserialize<ViewportMetrics>(await page.EvaluateAsync<string>($"JSON.stringify({Metrics})"), JsonSerializerOptions.Web);
            if (metrics is not { ScrollWidth: { } scrollWidth, ScrollHeight: { } scrollHeight } || !double.IsFinite(scrollWidth) || !double.IsFinite(scrollHeight))
                throw new InvalidOperationException("Chrome returned incomplete containment metrics.");
            if (screenshotPath is not null)
            {
                var png = await page.ScreenshotAsync(new() { Type = ScreenshotType.Png });
                using var file = new FileStream(screenshotPath, FileMode.CreateNew);
                file.Write(png);
            }
            return metrics;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    // --- the explainer gate (explainer/browser-check.mjs) ---

    // The current page, in a context of its own: JavaScriptEnabled is a context option, and a fresh context per reset is the fresh
    // page (and storage) upstream's resetPage gives.
    IBrowserContext? context;
    IPage? page;
    bool pageJavascript;
    readonly List<string> pageErrors = [];

    IPage Page => page ?? throw new InvalidOperationException("The explainer inspector has no current page.");

    public IReadOnlyList<string> PageErrors
    {
        get { lock (pageErrors) return [.. pageErrors]; }
    }

    public async Task ResetPageAsync()
    {
        var previous = context;
        (context, page) = (null, null);
        lock (pageErrors) pageErrors.Clear();
        if (previous is not null) await previous.CloseAsync();
    }

    async Task<IPage> PageFor(bool javascript)
    {
        if (page is not null && pageJavascript == javascript) return page;
        await ResetPageAsync();
        context = await browser.NewContextAsync(new() { JavaScriptEnabled = javascript, DeviceScaleFactor = 1 });
        var created = await context.NewPageAsync();
        void Record(string text)
        {
            lock (pageErrors) pageErrors.Add(text);
        }
        // Upstream's main session does not see an out-of-process srcdoc Viewer's console (BC:93-98, :136); on the headless shell the Viewer is in process.
        created.Console += (_, message) =>
        {
            if (message.Type == "error" && !message.Location.StartsWith("about:srcdoc", StringComparison.Ordinal)) Record(message.Text);
        };
        created.PageError += (_, error) => Record(error);
        (page, pageJavascript) = (created, javascript);
        return created;
    }

    // Values cross as JSON text, as CDP's returnByValue hands them upstream; a promise is awaited.
    public async Task<JsonNode?> EvaluateAsync(string expression) =>
        await Page.EvaluateAsync<string?>($"(async()=>JSON.stringify(await ({expression})))()") is { } json ? JsonNode.Parse(json) : null;

    public Task PressAsync(string key) => Page.Keyboard.PressAsync(key == " " ? "Space" : key);

    // inspectPublicHtml (BC:88-127), themeControl 'query'.
    public async Task<JsonObject> InspectPublicHtmlAsync(PublicHtmlOptions o)
    {
        var current = await PageFor(o.Javascript);
        var errorsBefore = PageErrors.Count;
        await current.SetViewportSizeAsync(o.Width, o.Height);
        await current.EmulateMediaAsync(new()
        {
            Media = o.Media == "print" ? Media.Print : Media.Screen, ColorScheme = o.Theme == "dark" ? ColorScheme.Dark : ColorScheme.Light,
            ReducedMotion = ReducedMotion.Reduce,
        });
        var url = $"{new Uri(o.ArtifactPath).AbsoluteUri}?theme={Uri.EscapeDataString(o.Theme)}";
        await current.GotoAsync(url, new() { WaitUntil = WaitUntilState.Load });
        // Inspector evaluation works with page JavaScript disabled; font readiness is separate from reader JS (BC:109-113).
        var fontStart = System.Diagnostics.Stopwatch.StartNew();
        JsonNode? fontStatus;
        do
        {
            fontStatus = await EvaluateAsync("(function(){void document.body.offsetHeight;return document.fonts.status;})()");
            if (fontStatus?.GetValueKind() == JsonValueKind.String && fontStatus.GetValue<string>() == "loaded") break;
            await Task.Delay(50);
        } while (fontStart.ElapsedMilliseconds < 5000);
        var fontReadiness = new JsonObject { ["status"] = fontStatus, ["waitMs"] = fontStart.ElapsedMilliseconds };
        await Task.Delay(50);
        var metrics = await EvaluateAsync(ExplainerBrowser.MeasurePublicHtmlExpression(o.Javascript)) as JsonObject
            ?? throw new InvalidOperationException("Chrome returned no public HTML metrics.");
        metrics["fontReadiness"] = fontReadiness;
        metrics["themeControl"] = "query";
        metrics["navigationUrl"] = url;
        if (o.Javascript && o.Keyboard)
            metrics["keyboardTraversal"] = await ExplainerBrowser.KeyboardTraversalAsync(this, (int)(metrics["controlCount"]?.GetValue<double>() ?? 0));
        metrics["consoleErrors"] = new JsonArray([.. PageErrors.Skip(errorsBefore).Select(e => (JsonNode?)e)]);
        if (o.ScreenshotPath is not null)
        {
            await EvaluateAsync("window.scrollTo(0,0)");
            var png = await current.ScreenshotAsync(new() { Type = ScreenshotType.Png });
            if (png.Length == 0) throw new InvalidOperationException("Chrome returned an empty screenshot.");
            using var file = new FileStream(o.ScreenshotPath, FileMode.CreateNew);
            file.Write(png);
        }
        return metrics;
    }

    // inspectNativeViewer (BC:129-143): the srcdoc frame whose parent is the main frame. ponytail: no CDP target id, and no errors of
    // its own (the srcdoc console is outside the main page's as upstream's is); upstream records only those during this one evaluate.
    public async Task<JsonObject> InspectNativeViewerAsync()
    {
        var frame = Page.Frames.FirstOrDefault(f => f.ParentFrame == Page.MainFrame && f.Url.StartsWith("about:srcdoc", StringComparison.Ordinal));
        if (frame is null) return new JsonObject { ["available"] = false, ["errors"] = new JsonArray() };
        var json = await frame.EvaluateAsync<string>("JSON.stringify(({ready:document.readyState,visibility:document.visibilityState,href:location.href,active:document.querySelector('svg[data-focus-active]')?.dataset.focusActive||'',nodes:[...new Set(Array.from(document.querySelectorAll('[data-focus-selected]')).map(e=>e.dataset.nodeId).filter(Boolean))],edges:[...new Set(Array.from(document.querySelectorAll('[data-edge-id][data-focus-match]')).map(e=>e.dataset.edgeId))],hasViewer:Boolean(window.Archify?.focus)}))");
        var native = new JsonObject { ["available"] = true };
        foreach (var (key, value) in JsonNode.Parse(json)!.AsObject()) native[key] = value?.DeepClone();
        native["errors"] = new JsonArray();
        return native;
    }

    public async ValueTask DisposeAsync()
    {
        try { await browser.CloseAsync(); }
        finally { playwright.Dispose(); }
    }
}
