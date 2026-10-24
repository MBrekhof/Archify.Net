using System.Text.Json.Nodes;

namespace Archify.Net.Cli.Browser;

// The seam between the evidence engine and a browser (survey-browser §6): upstream's resolveChrome + browserFactory + inspect
// (VC:2380-2388), which T/visual-check.test.mjs drives through its fakeBrowser. Task 11 implements it on Playwright Chromium.
public interface IBrowserInspectorFactory
{
    // The browser executable, or null when none is installed: upstream's findChrome() → null, a skipped receipt and exit 2.
    string? ResolveExecutable();

    Task<IBrowserInspector> LaunchAsync(string executable);
}

// One launched browser. Disposing it is upstream's browser.close(); a throw there fails the run (T/visual-check.test.mjs:3209).
public interface IBrowserInspector : IAsyncDisposable
{
    // Load the snapshot at width × height with `?theme=<theme>`, settle, measure (the verbatim viewer-gate scripts) and, given a
    // screenshotPath, write the viewport PNG there as a new file.
    Task<ViewportMetrics> InspectAsync(string snapshotPath, int width, int height, string theme, string? screenshotPath);

    // The explainer gate (explainer/browser-check.mjs). A fake for the native gate need not implement these.

    // inspectPublicHtml (BC:88-127) on the current page, a new one when there is none or its JavaScript setting differs.
    Task<JsonObject> InspectPublicHtmlAsync(PublicHtmlOptions o) => throw new NotSupportedException();

    // evaluate (BC:6-10) on the current page: the value, a promise awaited, as JSON (undefined is null).
    Task<JsonNode?> EvaluateAsync(string expression) => throw new NotSupportedException();

    // One key down and up on the current page: "Tab", "ArrowRight", "Enter", "Escape" or " ".
    Task PressAsync(string key) => throw new NotSupportedException();

    // inspectNativeViewer (BC:129-143): the srcdoc Viewer frame of the current page.
    Task<JsonObject> InspectNativeViewerAsync() => throw new NotSupportedException();

    // resetPage: the current page is closed; the next inspection opens a fresh one.
    Task ResetPageAsync() => throw new NotSupportedException();

    // The console errors and uncaught exceptions of the current page since it opened (upstream's subscription on the page's session).
    IReadOnlyList<string> PageErrors => throw new NotSupportedException();
}

// inspectPublicHtml's options (BC:88); themeControl is always 'query' here.
public sealed record PublicHtmlOptions(string ArtifactPath, int Width, int Height, string Theme, bool Javascript = true, string Media = "screen",
    bool Keyboard = true, string? ScreenshotPath = null);

// An error carrying the properties upstream reads off a thrown Error: `code` (ARCHIFY_TEMP_CLEANUP, the startup timeout, or a system
// code for an input failure), `directory` (a retained temp directory), and verifyArtifact's `deliveryProvenance.status` and
// `archifyDiagnostics`.
internal sealed class BrowserEvidenceException(string message) : Exception(message)
{
    public const string TempCleanup = "ARCHIFY_TEMP_CLEANUP";
    // upstream: ERR_CHROME_CDP_TIMEOUT on Target.getTargets, the browser's startup handshake (VC:2593-2594)
    public const string StartupTimeout = "ERR_CHROME_CDP_TIMEOUT";

    public string? Code { get; init; }
    public string? Directory { get; init; }
    public string? ProvenanceStatus { get; init; }
    public IReadOnlyList<Diagnostic>? Diagnostics { get; init; }
}
