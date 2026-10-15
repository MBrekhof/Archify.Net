using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Archify.Net.Tests;

// Phase 5 task 2: the library seams the CLI stands on, each against tests/fixtures/cli (tools/capture-cli-fixtures.mjs).
public class CliSeamTests
{
    static JsonObject CliFixture(string rel) => JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture($"cli/{rel}.json")))!.AsObject();

    // The fixture's single input file, read from the repo source its `files` map names.
    static JsonObject Input(JsonObject fixture)
    {
        var source = fixture["files"]!.AsObject().Single().Value!.GetValue<string>();
        return DiagramLoader.Parse(Fixtures.Lf(Path.Combine(Fixtures.RepoRoot, source.Replace('/', Path.DirectorySeparatorChar))));
    }

    static DiagramType Type(JsonObject fixture) => DiagramTypes.Parse(fixture["args"]![1]!.GetValue<string>());

    // diagnostics.mjs:136-146 formatDiagnostics, what the human renderer boundary writes to stderr.
    static string Format(string error, IReadOnlyList<Diagnostic> diagnostics) => diagnostics.Count == 0 ? error : string.Join("\n",
        [error, .. diagnostics.Select(d => $"[{d.Code}] {d.Message}{(d.SupportedFixes.Count > 0 ? $" Fix: {string.Join("; ", d.SupportedFixes)}." : "")}")]);

    static string Json(IEnumerable<Diagnostic> diagnostics) => Js.Stringify(new JsonArray([.. diagnostics.Select(d => (JsonNode?)d.ToJson())]), 2);

    [Fact]
    public void Render_failure_carries_the_thrown_error_text_and_only_the_thrown_diagnostics()
    {
        var fixture = CliFixture("render/render-overflow-label-locale");
        var r = ArchifyNet.Render(Type(fixture), Input(fixture));
        // Human render: `archify: <locale warning>` (the CLI's), then formatDiagnostics(error.message, error.archifyDiagnostics).
        var stderr = fixture["stderr"]!.GetValue<string>();
        Assert.Equal(stderr[(stderr.IndexOf('\n') + 1)..], Format(r.Error!, r.ThrownDiagnostics) + "\n");
        // JSON mode keeps the recorded list, locale warning included (validate --json of the same input).
        var receipt = JsonNode.Parse(CliFixture("validate/overflow-label-locale-json")["stdout"]!.GetValue<string>())!;
        Assert.Equal(Js.Stringify(receipt["diagnostics"], 2), Json(r.Diagnostics));
        Assert.Equal(receipt["error"]!.GetValue<string>(), r.Error);

        var ok = ArchifyNet.Render(DiagramType.Architecture, DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/web-app.architecture.json"))));
        Assert.Null(ok.Error);
        Assert.Empty(ok.ThrownDiagnostics);
    }

    // `validate <type> <input> --layout-json`: the stdout is the report, success or the two report-bearing failure shapes.
    [Theory]
    [InlineData("web-app.architecture")] [InlineData("brand-aware-delivery.architecture")] [InlineData("production-deployment.architecture")]
    [InlineData("checkout-platform.base.architecture")] [InlineData("checkout-platform.head.architecture")]
    [InlineData("agent-tool-call.workflow")] [InlineData("holiday-planning.workflow")] [InlineData("incident-response.workflow")]
    [InlineData("release-delivery.workflow")]
    [InlineData("architecture-validate-failure-json")] [InlineData("workflow-unmeasured-failure-json")] [InlineData("workflow-measured-failure-json")]
    public void Layout_report_equals_validate_layout_json(string name)
    {
        var fixture = CliFixture($"layout/{name}");
        var outcome = ArchifyNet.Layout(Type(fixture), Input(fixture));
        Assert.Equal(fixture["exit"]!.GetValue<int>() == 0, outcome.Ok);
        Assert.Equal(fixture["stdout"]!.GetValue<string>(), Js.Stringify(outcome.Report, 2) + "\n");
        if (!outcome.Ok) Assert.NotNull(outcome.Error);
    }

    // A failure before validateArchitecture (here the renderer's schema gate) has no report: the CLI reports it as a renderer
    // failure (A:6906-6913), whose error and diagnostics are these.
    [Fact]
    public void Layout_failure_before_layout_has_no_report()
    {
        var fixture = CliFixture("layout/architecture-pre-layout-failure-json");
        var outcome = ArchifyNet.Layout(Type(fixture), Input(fixture));
        var receipt = JsonNode.Parse(fixture["stdout"]!.GetValue<string>())!;
        Assert.False(outcome.Ok);
        Assert.Null(outcome.Report);
        Assert.Equal(receipt["error"]!.GetValue<string>(), outcome.Error);
        Assert.Equal(Js.Stringify(receipt["diagnostics"], 2), Json(outcome.Diagnostics));
    }

    // check-render-output.mjs hashes the file bytes and scans bytes.toString('utf8'): a BOM stays in, and each invalid UTF-8 sequence
    // becomes U+FFFD. The input carries invalid bytes inside a scanned SVG attribute, so the decoded text shows in finite_svg's
    // detail; the expected report is Node's `archify check --json` on the same bytes, minus the CLI's `file` and `provenance`.
    [Fact]
    public void Check_hashes_bytes_and_decodes_like_node()
    {
        var bytes = Fixtures.Bytes(Fixtures.Fixture("cli/inputs/decode-invalid-utf8.html"));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var expected = JsonNode.Parse(CliFixture("check/decode-invalid-utf8-json")["stdout"]!.GetValue<string>())!.AsObject();
        expected.Remove("file");
        expected.Remove("provenance");
        var report = ArchifyNet.Check(bytes).Report;
        Assert.Equal(Js.Stringify(expected, 2), Js.Stringify(report, 2));
        var finite = report["checks"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == "finite_svg")!;
        Assert.Equal("[\"rect x=\\\"�(�NaN\\\"\"]", Js.Stringify(finite["details"]));
        // The string overload is the bytes overload on its UTF-8 encoding.
        var golden = Fixtures.Bytes(Fixtures.Upstream("examples/web-app-rendered.html"));
        var html = Encoding.UTF8.GetString(golden);
        Assert.Equal(Js.Stringify(ArchifyNet.Check(golden).Report, 2), Js.Stringify(ArchifyNet.Check(html).Report, 2));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(golden)), ArchifyNet.Check(html).Report["artifact"]!["sha256"]!.GetValue<string>());
    }

    [Fact]
    public void Diagnostic_json_keeps_upstream_key_order()
    {
        var plain = new Diagnostic("a/b", Severity.Warning, "m", new JsonObject { ["x"] = 1 }, new JsonObject(), ["fix"], null);
        Assert.Equal("""{"code":"a/b","severity":"warning","message":"m","subject":{"x":1},"evidence":{},"supportedFixes":["fix"]}""", Js.Stringify(plain.ToJson()));
        var suppressing = plain with { Severity = Severity.Error, Suppresses = ["c/d"] };
        Assert.Equal("""{"code":"a/b","severity":"error","message":"m","subject":{"x":1},"evidence":{},"supportedFixes":["fix"],"suppresses":["c/d"]}""", Js.Stringify(suppressing.ToJson()));
    }

    [Fact]
    public void Examples_and_recipes_are_embedded()
    {
        var examples = Directory.GetFiles(Fixtures.Upstream("examples"), "*.json");
        Assert.Equal(25, examples.Length);
        foreach (var path in examples) Assert.Equal(Fixtures.Lf(path), Resources.Example(Path.GetFileName(path)));
        Assert.Equal(Fixtures.Lf(Fixtures.Upstream("recipes/scenarios.json")), Resources.Text("recipes/scenarios.json"));
    }
}
