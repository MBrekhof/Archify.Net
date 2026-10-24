using System.Text;
using System.Text.Json.Nodes;
using Archify.Net.Cli.Browser;
using Archify.Net.Cli.Commands;
using Microsoft.Playwright;
using Xunit;

namespace Archify.Net.Cli.Tests;

// PORT-009 Task 8: `finalize explainer` and the explainer browser gate (explainer/browser-check.mjs) against the Node-minted
// `explainer-finalize` and `explainer-browser` groups (tools/capture-cli-fixtures.mjs). Without a browser the receipts compare under
// the finalize scrub and the explainer masks; on Chromium only verdicts compare (Decision 4).
public sealed class ExplainerBrowserTests
{
    static void RequireChromium() => Assert.True(DoctorCommand.HeadlessShell() is not null,
        "Playwright Chromium (headless shell 1243) is not installed: run `archify install-browser` (PowerShell or cmd, no admin), then rerun the Browser tests.");

    // The no-JavaScript group rests on this: upstream's inspector (CDP Runtime.evaluate) still evaluates while page scripts are disabled
    // (Emulation.setScriptExecutionDisabled); here the page's context has JavaScriptEnabled = false and the page's own script must not run.
    [Trait("Category", "Browser")]
    [Fact]
    public async Task Evaluate_runs_with_page_javascript_disabled()
    {
        RequireChromium();
        var directory = Directory.CreateTempSubdirectory("archify-nojs-").FullName;
        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { ExecutablePath = DoctorCommand.HeadlessShell(), Headless = true });
            await using var context = await browser.NewContextAsync(new() { JavaScriptEnabled = false });
            var page = await context.NewPageAsync();
            var file = Path.Join(directory, "page.html");
            File.WriteAllText(file, "<!doctype html><html><body><p id=p>static</p><script>document.body.dataset.ran='yes'</script></body></html>");
            await page.GotoAsync(new Uri(file).AbsoluteUri).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal("static|none|2", await page.EvaluateAsync<string>(
                "(function(){return document.getElementById('p').textContent+'|'+(document.body.dataset.ran||'none')+'|'+[1,2].length;})()").WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Equal("done", await page.EvaluateAsync<string>("Promise.resolve('done')").WaitAsync(TimeSpan.FromSeconds(20)));
            // ponytail: page timers never fire here (a setTimeout promise hangs), so the no-JS path waits only through microtasks and
            // synchronous polls, as upstream's does: measurePublicHtml(false) observes nothing and the fonts poll is synchronous.
            Assert.Equal("loaded", await page.EvaluateAsync<string>("(function(){void document.body.offsetHeight;return document.fonts.status;})()").WaitAsync(TimeSpan.FromSeconds(20)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // --- finalize explainer without a browser ---

    sealed class NoChromium : IBrowserInspectorFactory
    {
        public bool Resolved { get; private set; }

        public string? ResolveExecutable()
        {
            Resolved = true;
            return null;
        }

        public Task<IBrowserInspector> LaunchAsync(string executable) => throw new InvalidOperationException("no browser is installed");
    }

    public static TheoryData<string> FinalizeFixtures() => [.. CliTestHost.FixtureNames("explainer-finalize")];

    // The four gates with browser-check not run (not requested), --browser-check skipped (exit 2, the only run that asks for Chromium),
    // invalid chart front matter (finalize/runtime), and the writing modes.
    [Theory, MemberData(nameof(FinalizeFixtures))]
    public void Finalize_explainer_without_chromium_matches_node(string name)
    {
        var json = JsonNode.Parse(CliTestHost.ReadLf(Path.Combine(CliTestHost.FixtureRoot, name + ".json")))!.AsObject();
        var fixture = CliTestHost.Fixture(name);
        var browsers = new NoChromium();
        string[] entries = [];
        var sidecars = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var (exit, stdout, stderr) = CliTestHost.Run(fixture.Args, fixture.Files, CliServices.Default with { BrowserInspectors = browsers }, inspect: work =>
        {
            entries = [.. Directory.EnumerateFileSystemEntries(work, "*", SearchOption.AllDirectories).Select(e => Path.GetRelativePath(work, e)).Order(StringComparer.Ordinal)];
            foreach (var entry in entries.Where(e => IsFinalizeFile(Path.GetFileName(e))))
                sidecars[entry] = FinalizeCommandTests.Scrub(ExplainerCommandTests.Mask(CliTestHost.Tokenise(CliTestHost.ReadLf(Path.Join(work, entry)), work)));
        }, setup: fixture.Setup);
        string Expected(string text) => BrowserEvidenceTests.ChromeUnavailableRuling(UsageEdit.Apply(text), "browser-check");
        Assert.Equal(Expected(fixture.Stdout), FinalizeCommandTests.Scrub(ExplainerCommandTests.Mask(stdout)));
        Assert.Equal(Expected(fixture.Stderr), ExplainerCommandTests.Mask(stderr));
        Assert.Equal(fixture.Exit, exit);
        Assert.Equal(Js.Stringify(json["entries"]), Js.Stringify(new JsonArray([.. entries.Select(e => (JsonNode?)e)])));
        var expected = json["sidecars"]!.AsObject();
        Assert.Equal(string.Join("\n", expected.Select(kv => kv.Key)), string.Join("\n", sidecars.Keys));
        foreach (var (file, text) in expected) Assert.Equal(Expected(text!.GetValue<string>()), sidecars[file]);
        Assert.Equal(fixture.Args.Contains("--browser-check"), browsers.Resolved);
    }

    static bool IsFinalizeFile(string name) =>
        new[] { ".finalize.json", "-summary.json", ".delivery.json", ".delivery-pending.json", ".browser-check.json" }.Any(s => name.EndsWith(s, StringComparison.Ordinal))
        || name == ".archify-delivery-lock.json";

    // --- the browser gate on Chromium: verdicts only (Decision 4) ---

    static readonly string[] Groups = ["containment", "readability", "viewerChrome", "themeStates", "commonQa", "interactions", "references", "noJavaScript", "print"];

    // The capture's verdictsOf: {exit, ok, status, each group's status, the ordered diagnostic codes}.
    static JsonObject Verdicts(JsonNode receipt, int exit) => new()
    {
        ["exit"] = exit, ["ok"] = receipt["ok"]?.DeepClone(), ["status"] = receipt["status"]?.DeepClone(),
        ["groups"] = new JsonObject([.. Groups.Select(g => KeyValuePair.Create(g, receipt[g]?["status"]?.DeepClone()))]),
        ["codes"] = new JsonArray([.. receipt["diagnostics"].AsArrayOrEmpty().Select(d => d!["code"]!.DeepClone())]),
    };

    public static TheoryData<string> BrowserFixtures() => [.. CliTestHost.FixtureNames("explainer-browser")];

    // The receipt's verdicts equal Node's, or Node's without the viewer-target platform difference where the capture wrote `verdictsFixed`.
    [Trait("Category", "Browser")]
    [Theory, MemberData(nameof(BrowserFixtures))]
    public void Verdicts_match_node(string name)
    {
        RequireChromium();
        var fixture = JsonNode.Parse(CliTestHost.ReadLf(Path.Combine(CliTestHost.FixtureRoot, name + ".json")))!.AsObject();
        string[] args = [.. fixture["args"]!.AsArray().Select(a => a!.GetValue<string>())];
        var files = fixture["files"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>());
        JsonNode? browserReceipt = null;
        var finalize = args[0] == "finalize";
        var (exit, stdout, _) = CliTestHost.Run(args, files, CliServices.Default, setup: fixture["setup"]?.AsArray(), inspect: work =>
        {
            if (finalize) browserReceipt = JsonNode.Parse(File.ReadAllText(Path.Join(work, "out.browser-check.json"), Encoding.UTF8));
        });
        var receipt = JsonNode.Parse(stdout)!;
        JsonObject actual = finalize
            ? new() { ["exit"] = exit, ["gates"] = receipt["gates"]!.DeepClone(), ["browserCheck"] = receipt["browserCheck"]!.DeepClone() }
            : Verdicts(receipt, exit);
        Assert.Equal(Js.Stringify((fixture["verdictsFixed"] ?? fixture["verdicts"])!, 2), Js.Stringify(actual, 2));
        if (finalize)
            Assert.Equal(Js.Stringify((fixture["browserVerdictsFixed"] ?? fixture["browserVerdicts"])!, 2), Js.Stringify(Verdicts(browserReceipt!, exit), 2));
        if (!finalize) Assert.Equal("explainer", receipt["family"]?.GetValue<string>());
    }

    // Platform difference viewer-target (not an upstream defect): on the Playwright headless shell the sandboxed srcdoc Viewer is an
    // in-process frame with no CDP target, so upstream's target-only lookup (browser-check.mjs:131-133) never finds it and every
    // step-linked page fails explainer/browser-diagram-focus there. On upstream's default desktop Chrome (--headless=new) the Viewer is a
    // target and both agree. The port finds the frame among the page's frames. Each fixed verdict is Node's where upstream found
    // no Viewer target at any viewport, with the diagram-focus codes gone, interactions failing only for another diagram-* code, and
    // ok, status and exit following from the rest.
    [Fact]
    public void Fixed_verdicts_differ_only_by_the_viewer_target()
    {
        var fixedCount = 0;
        foreach (var name in CliTestHost.FixtureNames("explainer-browser"))
        {
            var fixture = JsonNode.Parse(CliTestHost.ReadLf(Path.Combine(CliTestHost.FixtureRoot, name + ".json")))!.AsObject();
            var finalize = fixture.ContainsKey("browserVerdicts");
            var node = fixture[finalize ? "browserVerdicts" : "verdicts"]!.AsObject();
            var fixedVerdicts = fixture[finalize ? "browserVerdictsFixed" : "verdictsFixed"];
            var focus = node["codes"]!.AsArray().Any(c => c!.GetValue<string>() == "explainer/browser-diagram-focus");
            Assert.Equal(focus, fixedVerdicts is not null);
            if (fixedVerdicts is null) continue;
            fixedCount++;
            Assert.All(fixture["viewerTargets"]!.AsArray(), target => Assert.False(target!.GetValue<bool>()));
            var codes = node["codes"]!.AsArray().Select(c => c!.GetValue<string>()).Where(c => c != "explainer/browser-diagram-focus").ToList();
            var groups = node["groups"]!.DeepClone().AsObject();
            groups["interactions"] = codes.Any(c => c.StartsWith("explainer/browser-diagram-", StringComparison.Ordinal)) ? "fail" : "pass";
            var pass = groups.All(g => g.Value!.GetValue<string>() == "pass") && codes.Count == 0;
            var expected = new JsonObject
            {
                ["exit"] = pass ? 0 : 1, ["ok"] = pass, ["status"] = pass ? "pass" : "fail", ["groups"] = groups,
                ["codes"] = new JsonArray([.. codes.Select(c => (JsonNode?)c)]),
            };
            Assert.Equal(Js.Stringify(expected), Js.Stringify(fixedVerdicts));
            if (!finalize) continue;
            var gates = fixture["verdicts"]!["gates"]!.DeepClone().AsObject();
            gates["browser-check"] = pass ? "pass" : "fail";
            Assert.Equal(Js.Stringify(new JsonObject
            {
                ["exit"] = gates.All(g => g.Value!.GetValue<string>() == "pass") ? 0 : 1, ["gates"] = gates, ["browserCheck"] = fixture["verdicts"]!["browserCheck"]!.DeepClone(),
            }), Js.Stringify(fixture["verdictsFixed"]));
        }
        Assert.Equal(4, fixedCount);
    }
}
