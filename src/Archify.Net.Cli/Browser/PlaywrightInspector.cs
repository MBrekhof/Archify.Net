using System.Text.Json;
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

    public async ValueTask DisposeAsync()
    {
        try { await browser.CloseAsync(); }
        finally { playwright.Dispose(); }
    }
}
