using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Archify.Net.Cli.Browser;
using Archify.Net.Cli.Commands;
using Xunit;
using Xunit.Abstractions;

namespace Archify.Net.Cli.Tests;

// Phase 5 task 12: `archify finalize` on the command line (A:5706-5863) against Node-minted fixtures (tools/capture-cli-fixtures.mjs):
// argument errors, runs that stop before the browser gate or skip it, the web-app run on real Chromium, and the phase exit (plan
// "Exit"): every example at both qualities, outcome for outcome. Receipts compare after the capture script's scrub (the brief): no
// startedAt, finishedAt, durationMs, stage command or stderr, no update block (D7), v4 ids as <uuid>, Chromium as <CHROME>.
public sealed partial class FinalizeCommandTests(ITestOutputHelper output)
{
    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex UuidV4();

    [GeneratedRegex(@"(\.finalize\.json|-summary\.json|\.delivery\.json|\.delivery-pending\.json|\.browser-check\.json|^\.archify-delivery-lock\.json)$")]
    private static partial Regex FinalizeSidecar();

    // The capture script's scrubFinalize.
    internal static string Scrub(string text)
    {
        var t = UuidV4().Replace(text, "<uuid>");
        if (!t.StartsWith('{')) return t;
        var value = JsonNode.Parse(t)!.AsObject();
        foreach (var key in new[] { "startedAt", "finishedAt", "durationMs", "update" }) value.Remove(key);
        foreach (var (_, stage) in value["stages"] as JsonObject ?? [])
        {
            foreach (var key in new[] { "durationMs", "command", "stderr" }) stage!.AsObject().Remove(key);
            (stage!["receipt"] as JsonObject)?.Remove("update");
        }
        return Js.Stringify(value, t.Contains("\n  ") ? 2 : 0) + "\n";
    }

    internal static string Chromium(string text) =>
        DoctorCommand.HeadlessShell() is { } chrome ? text.Replace(chrome.Replace("\\", "\\\\"), "<CHROME>").Replace(chrome, "<CHROME>") : text;

    // A browser factory with no Chromium; records whether a run asked for one.
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

    public static TheoryData<string> ArgumentFixtures() => [.. CliTestHost.FixtureNames("arguments", "finalize-")];

    [Theory, MemberData(nameof(ArgumentFixtures))]
    public void Argument_errors_match_node(string name)
    {
        var browsers = new NoChromium();
        var fixture = CliTestHost.Fixture(name);
        var (exit, stdout, stderr) = CliTestHost.Run(fixture.Args, fixture.Files, CliServices.Default with { BrowserInspectors = browsers },
            inspect: work => Assert.Equal(fixture.Files.Count, Directory.EnumerateFileSystemEntries(work).Count()));
        Assert.Equal(UsageEdit.Apply(fixture.Stdout), stdout);
        Assert.Equal(UsageEdit.Apply(fixture.Stderr), stderr);
        Assert.Equal(fixture.Exit, exit);
        Assert.False(browsers.Resolved);
    }

    public static TheoryData<string> WithoutChromiumFixtures() => [.. CliTestHost.FixtureNames("finalize")];

    // Upstream ran with ARCHIFY_CHROME at a missing file; here the factory reports no Chromium. Only a run that reached the browser gate
    // (exit 2, skipped) asked for one.
    [Theory, MemberData(nameof(WithoutChromiumFixtures))]
    public void Finalize_without_chromium_matches_node(string name)
    {
        var browsers = new NoChromium();
        var fixture = AssertMatches(name, CliServices.Default with { BrowserInspectors = browsers });
        Assert.Equal(fixture["exit"]!.GetValue<int>() == 2, browsers.Resolved);
    }

    public static TheoryData<string> ChromiumFixtures() => [.. CliTestHost.FixtureNames("finalize-chromium")];

    // The web-app run on the installed Playwright Chromium: the summary (stdout), the full receipt, its summary file, the delivery
    // provenance and the browser-check receipt.
    [Trait("Category", "Browser")]
    [Theory, MemberData(nameof(ChromiumFixtures))]
    public void Finalize_on_chromium_matches_node(string name)
    {
        RequireChromium();
        AssertMatches(name, CliServices.Default);
    }

    static void RequireChromium() => Assert.True(DoctorCommand.HeadlessShell() is not null,
        "Playwright Chromium (headless shell 1243) is not installed: run `archify install-browser` (PowerShell or cmd, no admin), then rerun the Browser tests.");

    // Runs a fixture and compares exit, stdout, stderr, the workdir afterwards and every finalize/delivery/browser sidecar; returns it.
    static JsonObject AssertMatches(string name, CliServices services)
    {
        var fixture = JsonNode.Parse(CliTestHost.ReadLf(Path.Combine(CliTestHost.FixtureRoot, name + ".json")))!.AsObject();
        var recorded = CliTestHost.Fixture(name);
        string[] entries = [];
        var sidecars = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var (exit, stdout, stderr) = CliTestHost.Run(recorded.Args, recorded.Files, services, inspect: work =>
        {
            entries = [.. Directory.EnumerateFileSystemEntries(work, "*", SearchOption.AllDirectories).Select(e => Path.GetRelativePath(work, e)).Order(StringComparer.Ordinal)];
            foreach (var entry in entries.Where(e => FinalizeSidecar().IsMatch(Path.GetFileName(e)) && File.Exists(Path.Join(work, e))))
                sidecars[entry] = Scrub(Chromium(CliTestHost.Tokenise(File.ReadAllText(Path.Join(work, entry), Encoding.UTF8), work)));
        });
        var expected = fixture["sidecars"]!.AsObject();
        // Q8: a JSON parse failure's reason is V8's text upstream and .NET's here; it compares masked.
        var expectedMask = ParseReasonMask(expected.Select(kv => kv.Value!.GetValue<string>()));
        var actualMask = ParseReasonMask(sidecars.Values);
        string Expected(string text) => expectedMask(BrowserEvidenceTests.ChromeUnavailableRuling(UsageEdit.Apply(text), "browser-check"));
        Assert.Equal(Expected(recorded.Stdout), actualMask(Scrub(Chromium(stdout))));
        Assert.Equal(Expected(recorded.Stderr), actualMask(UuidV4().Replace(stderr, "<uuid>")));
        Assert.Equal(recorded.Exit, exit);
        Assert.Equal(Js.Stringify(fixture["entries"]), Js.Stringify(new JsonArray([.. entries.Select(e => (JsonNode?)e)])));
        Assert.Equal(string.Join("\n", expected.Select(kv => kv.Key)), string.Join("\n", sidecars.Keys));
        foreach (var (file, text) in expected) Assert.Equal(Expected(text!.GetValue<string>()), actualMask(sidecars[file]));
        return fixture;
    }

    // The reason of an input/json-parse diagnostic in a finalize receipt among the sidecars, masked raw and JSON-escaped.
    static Func<string, string> ParseReasonMask(IEnumerable<string> sidecars)
    {
        var reason = sidecars.Where(text => text.StartsWith('{')).Select(text => JsonNode.Parse(text)!["diagnostics"] as JsonArray)
            .SelectMany(diagnostics => diagnostics ?? []).Where(d => (string?)d!["code"] == "input/json-parse")
            .Select(d => (string?)d!["evidence"]!["reason"]).FirstOrDefault();
        if (string.IsNullOrEmpty(reason)) return text => text;
        var escaped = Js.Stringify(JsonValue.Create(reason))[1..^1];
        return text => text.Replace(escaped, "<REASON>").Replace(reason, "<REASON>");
    }

    // --- the phase exit ---

    static readonly JsonArray Outcomes = JsonNode.Parse(CliTestHost.ReadLf(Path.Combine(CliTestHost.FixtureRoot, "finalize-exit", "outcomes.json")))!.AsArray();

    public static TheoryData<string, string> ExitRuns()
    {
        var data = new TheoryData<string, string>();
        foreach (var outcome in Outcomes) data.Add(outcome!["example"]!.GetValue<string>(), outcome["quality"]!.GetValue<string>());
        return data;
    }

    // Plan "Exit" (b), and (a) as upstream itself has it: `archify finalize` on every example at --quality standard and at the default
    // showcase quality ends as upstream's finalize ended on the same Chromium build — exit code, ok, failedStage, the four gates, the full
    // receipt's diagnostic codes in order, and the delivered artifact's sha256 and size.
    [Trait("Category", "Browser")]
    [Theory, MemberData(nameof(ExitRuns))]
    public void Finalize_ends_as_upstream_on_every_example(string example, string quality)
    {
        RequireChromium();
        var expected = Outcomes.Single(o => (string?)o!["example"] == example && (string?)o["quality"] == quality)!;
        var type = example.Split('.')[^2];
        JsonNode? full = null;
        var watch = Stopwatch.StartNew();
        var (exit, stdout, _) = CliTestHost.Run(["finalize", type, example, "out.html", "--quality", quality, "--json"],
            new Dictionary<string, string> { [example] = $"upstream/examples/{example}" }, CliServices.Default,
            inspect: work => full = JsonNode.Parse(File.ReadAllText(Path.Join(work, "out.finalize.json"))));
        output.WriteLine($"{example} --quality {quality}: {watch.ElapsedMilliseconds} ms");
        var summary = JsonNode.Parse(stdout)!;
        var actual = new JsonObject
        {
            ["example"] = example, ["quality"] = quality, ["exit"] = exit, ["ok"] = summary["ok"]?.DeepClone(), ["failedStage"] = summary["failedStage"]?.DeepClone(),
            ["gates"] = summary["gates"]!.DeepClone(), ["codes"] = new JsonArray([.. full!["diagnostics"]!.AsArray().Select(d => (JsonNode?)d!["code"]!.DeepClone())]),
            ["artifact"] = new JsonObject { ["sha256"] = summary["artifact"]!["sha256"]?.DeepClone(), ["bytes"] = summary["artifact"]!["bytes"]?.DeepClone() },
        };
        Assert.Equal(Js.Stringify(expected, 2), Js.Stringify(actual, 2));
    }

    // The golden of each example is the one ParityTests.Targets() maps it to; finalize at showcase delivers it byte for byte for every
    // example authored at showcase (the two checkout-platform examples carry no quality profile, so neither quality reproduces theirs).
    static readonly Dictionary<string, string> Goldens = new()
    {
        ["web-app.architecture.json"] = "upstream/examples/web-app-rendered.html",
        ["production-deployment.architecture.json"] = "tests/goldens/production-deployment-rendered.html",
        ["brand-aware-delivery.architecture.json"] = "tests/goldens/brand-aware-delivery-rendered.html",
        ["checkout-platform.base.architecture.json"] = "tests/goldens/checkout-platform.base-rendered.html",
        ["checkout-platform.head.architecture.json"] = "tests/goldens/checkout-platform.head-rendered.html",
        ["cache-miss-request.sequence.json"] = "upstream/examples/sequence-cache-miss-request.html",
        ["async-job-roundtrip.sequence.json"] = "tests/goldens/async-job-roundtrip-rendered.html",
        ["agent-run.lifecycle.json"] = "upstream/examples/lifecycle-agent-run.html",
        ["deployment-release.lifecycle.json"] = "tests/goldens/deployment-release-rendered.html",
        ["payments.class.json"] = "upstream/examples/class-payments-rendered.html",
        ["payment-processors.class.json"] = "upstream/examples/class-payment-processors-rendered.html",
        ["payment-platform.tree.json"] = "upstream/examples/tree-payment-platform-rendered.html",
        ["archify-repository.tree.json"] = "upstream/examples/tree-archify-repository-rendered.html",
        ["payment-incident.timeline.json"] = "upstream/examples/timeline-payment-incident-rendered.html",
        ["archify-dev-activity.timeline.json"] = "upstream/examples/timeline-archify-dev-activity-rendered.html",
        ["checkout-request.waterfall.json"] = "upstream/examples/waterfall-checkout-request-rendered.html",
        ["example-rebuild.waterfall.json"] = "upstream/examples/waterfall-example-rebuild-rendered.html",
        ["product-analytics.dataflow.json"] = "upstream/examples/dataflow-product-analytics.html",
        ["event-stream.dataflow.json"] = "tests/goldens/event-stream-rendered.html",
        ["incident-response.workflow.json"] = "tests/goldens/incident-response-rendered.html",
        ["release-delivery.workflow.json"] = "tests/goldens/release-delivery-rendered.html",
        ["holiday-planning.workflow.json"] = "tests/goldens/holiday-planning-rendered.html",
        ["agent-tool-call.workflow.json"] = "upstream/examples/workflow-agent-tool-call-rendered.html",
        ["orders.erd.json"] = "upstream/examples/erd-orders-rendered.html",
        ["subscription-billing.erd.json"] = "upstream/examples/subscription-billing-rendered.html",
    };

    [Fact]
    public void Showcase_finalize_delivers_each_examples_parity_golden()
    {
        Assert.Equal(25, Outcomes.Count / 2);
        foreach (var outcome in Outcomes.Where(o => (string?)o!["quality"] == "showcase"))
        {
            var example = outcome!["example"]!.GetValue<string>();
            var spec = JsonNode.Parse(File.ReadAllText(Path.Join(CliTestHost.RepoRoot, "upstream", "examples", example)))!;
            var golden = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Join(CliTestHost.RepoRoot, Goldens[example]))));
            Assert.Equal((string?)spec["meta"]!["quality_profile"] == "showcase", golden == (string?)outcome["artifact"]!["sha256"]);
        }
    }
}
