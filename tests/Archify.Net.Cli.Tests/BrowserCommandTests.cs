using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Archify.Net.Cli.Browser;
using Archify.Net.Cli.Commands;
using Xunit;

namespace Archify.Net.Cli.Tests;

// Phase 5 task 11: `browser-check` and `visual-check` on the command line (A:5547-5704) against Node-minted fixtures
// (tools/capture-cli-fixtures.mjs): argument errors, T/cli.test.mjs:439-634, 4050 and 4225 without a browser, and receipts from the
// same Playwright Chromium build on both sides (Q7). Each run is compared on exit, stdout, stderr, the workdir afterwards and every
// evidence file: text byte for byte, a PNG by its magic and IHDR size (Q4).
public sealed partial class BrowserCommandTests
{
    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex UuidV4();

    [GeneratedRegex(@"\.(?:visual|browser)-check\.")]
    private static partial Regex EvidenceName();

    public static TheoryData<string> ArgumentFixtures() =>
        [.. CliTestHost.FixtureNames("arguments", "browser-check-"), .. CliTestHost.FixtureNames("arguments", "visual-check-")];

    [Theory, MemberData(nameof(ArgumentFixtures))]
    public void Argument_errors_match_node(string name)
    {
        var browsers = new NoChromium();
        var fixture = CliTestHost.Fixture(name);
        var (exit, stdout, stderr) = CliTestHost.Run(fixture.Args, fixture.Files, CliServices.Default with { BrowserInspectors = browsers });
        Assert.Equal(UsageEdit.Apply(fixture.Stdout), stdout);
        Assert.Equal(UsageEdit.Apply(fixture.Stderr), stderr);
        Assert.Equal(fixture.Exit, exit);
        Assert.False(browsers.Resolved);
    }

    public static TheoryData<string> EvidenceFixtures() => [.. CliTestHost.FixtureNames("evidence")];

    // Upstream ran with ARCHIFY_CHROME at a missing file; here the factory reports no Chromium. A skipped run (exit 2) asked for the
    // browser; every refusal (exit 1: provenance, input, --out-dir) answered before it, never launching one.
    [Theory, MemberData(nameof(EvidenceFixtures))]
    public void Commands_without_chromium_match_node(string name)
    {
        var browsers = new NoChromium();
        var fixture = AssertMatches(name, CliServices.Default with { BrowserInspectors = browsers });
        Assert.Equal(fixture["exit"]!.GetValue<int>() == 2, browsers.Resolved);
    }

    public static TheoryData<string> ChromiumFixtures() => [.. CliTestHost.FixtureNames("browser-check"), .. CliTestHost.FixtureNames("visual-check")];

    // One golden of each diagram type through browser-check, plus human, --summary, a delivered artifact under --require-provenance and
    // visual-check (4 PNGs and the contact sheet), on the installed Playwright Chromium (the default services).
    [Trait("Category", "Browser")]
    [Theory, MemberData(nameof(ChromiumFixtures))]
    public void Commands_on_chromium_match_node(string name)
    {
        Assert.True(DoctorCommand.HeadlessShell() is not null,
            "Playwright Chromium (headless shell 1243) is not installed: run `archify install-browser` (PowerShell or cmd, no admin), then rerun the Browser tests.");
        AssertMatches(name, CliServices.Default);
    }

    // Review Focus 5 through the real factory: no installed headless shell is upstream's "Chrome unavailable", and the one fix is the
    // install command (Q4).
    [Theory, InlineData("browser-check", false), InlineData("visual-check", true)]
    public void Missing_chromium_is_skipped_with_exit_2_and_names_install_browser(string command, bool capture)
    {
        Assert.IsType<PlaywrightInspectorFactory>(CliServices.Default.BrowserInspectors);
        var services = CliServices.Default with { BrowserInspectors = new PlaywrightInspectorFactory(() => null) };
        var (exit, stdout, stderr) = CliTestHost.Run([command, "out.html", "--json"],
            new Dictionary<string, string> { ["out.html"] = "tests/fixtures/cli/inputs/delivered.html" }, services);
        Assert.Equal(2, exit);
        Assert.Equal("", stderr);
        var receipt = JsonNode.Parse(stdout)!;
        Assert.Equal(("skipped", "unavailable", null, capture ? "skipped" : "not-requested"),
            ((string?)receipt["status"], (string?)receipt["chrome"]!["status"], (string?)receipt["chrome"]!["executable"], (string?)receipt["captures"]!["status"]));
        Assert.Equal("Chromium is unavailable. Run archify install-browser to install it.", (string?)receipt["error"]);
        var diagnostic = Assert.Single(receipt["diagnostics"]!.AsArray())!;
        Assert.Equal(("viewer/chrome-unavailable", "warning"), ((string?)diagnostic["code"], (string?)diagnostic["severity"]));
        Assert.Equal($"[\"run archify install-browser, then rerun {command}\"]", Js.Stringify(diagnostic["supportedFixes"]));
    }

    // A:5592 catches whatever the engine throws, not only filesystem errors: a browser lookup that fails on a corrupt browsers.json
    // (JsonException) is the viewer/<command>-input receipt, persisted beside the artifact, exit 1.
    [Theory, InlineData("browser-check", false), InlineData("visual-check", true)]
    public void Any_engine_exception_is_the_input_receipt(string command, bool capture)
    {
        var services = CliServices.Default with { BrowserInspectors = new CorruptBrowsersJson() };
        string[] entries = [];
        var (exit, stdout, stderr) = CliTestHost.Run([command, "out.html", "--json"],
            new Dictionary<string, string> { ["out.html"] = "tests/fixtures/cli/inputs/delivered.html" }, services,
            inspect: work => entries = [.. Directory.GetFileSystemEntries(work).Select(Path.GetFileName).Order(StringComparer.Ordinal)!]);
        Assert.Equal((1, ""), (exit, stderr));
        var receipt = JsonNode.Parse(stdout)!;
        Assert.Equal(("fail", "corrupt browsers.json", capture ? "pending" : "not-requested"),
            ((string?)receipt["status"], (string?)receipt["error"], (string?)receipt["visualReview"]));
        var diagnostic = Assert.Single(receipt["diagnostics"]!.AsArray())!;
        Assert.Equal($"viewer/{command}-input", (string?)diagnostic["code"]);
        Assert.Equal("""{"reason":"corrupt browsers.json"}""", Js.Stringify(diagnostic["evidence"]));
        Assert.Equal(new[] { "out.html", $"out.{command}.json" }.Order(StringComparer.Ordinal), entries);
    }

    sealed class CorruptBrowsersJson : IBrowserInspectorFactory
    {
        public string? ResolveExecutable() => throw new System.Text.Json.JsonException("corrupt browsers.json");
        public Task<IBrowserInspector> LaunchAsync(string executable) => throw new InvalidOperationException("never launched");
    }

    // VC:772-788: an evidence directory where nothing can be created refuses before the browser (chrome not-checked, every gate
    // failed, nothing written), against Node with the same ACL. The OS reason is compared as <REASON> (Q8).
    [Fact]
    public void An_unwritable_evidence_directory_is_refused_before_the_browser()
    {
        var browsers = new NoChromium();
        var fixture = JsonNode.Parse(CliTestHost.ReadLf(Path.Combine(CliTestHost.FixtureRoot, "evidence-acl", "browser-check-unwritable-directory.json")))!.AsObject();
        var recorded = CliTestHost.Fixture("evidence-acl/browser-check-unwritable-directory");
        string[] entries = [];
        var (exit, stdout, stderr) = CliTestHost.Run(recorded.Args, recorded.Files, CliServices.Default with { BrowserInspectors = browsers }, setup: recorded.Setup,
            inspect: work => entries = [.. Directory.EnumerateFileSystemEntries(work, "*", SearchOption.AllDirectories).Select(e => Path.GetRelativePath(work, e)).Order(StringComparer.Ordinal)]);
        var receipt = JsonNode.Parse(stdout)!;
        var error = receipt["diagnostics"]![0]!["evidence"]!["errors"]![0]!;
        Assert.StartsWith("Access to the path", error["reason"]!.GetValue<string>());
        error["reason"] = "<REASON>";
        Assert.Equal(recorded.Stdout, Js.Stringify(receipt, 2) + "\n");
        Assert.Equal((recorded.Stderr, recorded.Exit), (stderr, exit));
        Assert.Equal(Js.Stringify(fixture["entries"]), Js.Stringify(new JsonArray([.. entries.Select(e => (JsonNode?)e)])));
        Assert.False(browsers.Resolved);
    }

    // Runs a fixture and compares everything it recorded; returns the fixture.
    static JsonObject AssertMatches(string name, CliServices services)
    {
        var fixture = JsonNode.Parse(CliTestHost.ReadLf(Path.Combine(CliTestHost.FixtureRoot, name + ".json")))!.AsObject();
        var recorded = CliTestHost.Fixture(name);
        string[] entries = [];
        var evidence = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var (exit, stdout, stderr) = CliTestHost.Run(recorded.Args, recorded.Files, services, setup: recorded.Setup, inspect: work =>
        {
            entries = [.. Directory.EnumerateFileSystemEntries(work, "*", SearchOption.AllDirectories).Select(e => Path.GetRelativePath(work, e)).Order(StringComparer.Ordinal)];
            foreach (var entry in entries.Where(e => EvidenceName().IsMatch(Path.GetFileName(e)) && File.Exists(Path.Join(work, e))))
            {
                var bytes = File.ReadAllBytes(Path.Join(work, entry));
                evidence[entry] = bytes.AsSpan().StartsWith(Png)
                    ? $"png {BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16))}x{BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20))}"
                    : Normalise(CliTestHost.Tokenise(Encoding.UTF8.GetString(bytes), work));
            }
        });
        var command = recorded.Args[0];
        string Expected(string text) => BrowserEvidenceTests.ChromeUnavailableRuling(UsageEdit.Apply(text), command);
        Assert.Equal(Expected(recorded.Stdout), Normalise(stdout));
        Assert.Equal(Expected(recorded.Stderr), Normalise(stderr));
        Assert.Equal(recorded.Exit, exit);
        Assert.Equal(Js.Stringify(fixture["entries"]), Js.Stringify(new JsonArray([.. entries.Select(e => (JsonNode?)e)])));
        var expectedEvidence = fixture["evidence"]!.AsObject();
        Assert.Equal(string.Join("\n", expectedEvidence.Select(kv => kv.Key)), string.Join("\n", evidence.Keys));
        foreach (var (file, text) in expectedEvidence) Assert.Equal(Expected(text!.GetValue<string>()), evidence[file]);
        return fixture;
    }

    static readonly byte[] Png = Convert.FromHexString("89504e470d0a1a0a");

    // The capture script's normalisation (Q8): the Chromium path is <CHROME>, v4 ids are <uuid>, and a PNG's sha256 and size in a
    // receipt's sidecars.files are <sha256> and <bytes>, since they depend on the browser build (Q4).
    static string Normalise(string text)
    {
        if (DoctorCommand.HeadlessShell() is { } chrome) text = text.Replace(chrome.Replace("\\", "\\\\"), "<CHROME>").Replace(chrome, "<CHROME>");
        text = UuidV4().Replace(text, "<uuid>");
        if (!text.StartsWith('{')) return text;
        static bool IsPng(JsonNode? file) => file!["file"]!.GetValue<string>().EndsWith(".png", StringComparison.Ordinal);
        if (JsonNode.Parse(text)!.AsObject() is not { } receipt || receipt["sidecars"]?["files"] is not JsonArray files || !files.Any(IsPng)) return text;
        foreach (var file in files.Where(IsPng))
        {
            Assert.Matches(@"\A[0-9a-f]{64}\z", file!["sha256"]!.GetValue<string>());
            Assert.True(file["bytes"]!.GetValue<double>() > 0, $"{file["file"]} has no bytes");
            (file["sha256"], file["bytes"]) = ("<sha256>", "<bytes>");
        }
        return Js.Stringify(receipt, 2) + "\n";
    }

    // A browser factory with no Chromium installed; records whether the run asked for one.
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
}
