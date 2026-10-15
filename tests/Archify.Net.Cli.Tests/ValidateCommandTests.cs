using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Archify.Net.Cli.Tests;

// Phase 5 task 6: `validate` and `inspect` against the Node-minted fixtures (stdout, stderr, exit byte-equal), writing nothing.
public class ValidateCommandTests
{
    // V8's JSON.parse text (Q8): these compare with the reason masked.
    static readonly string[] V8Text = ["validate/broken-json-json", "validate/broken-json-human", "validate/bom-json", "validate/bom-human"];

    public static TheoryData<string> Fixtures() =>
    [
        .. CliTestHost.FixtureNames("validate").Where(n => !V8Text.Contains(n)),
        .. CliTestHost.FixtureNames("layout"),
        .. CliTestHost.FixtureNames("arguments", "validate-"),
        .. CliTestHost.FixtureNames("arguments", "inspect-"),
    ];

    [Theory, MemberData(nameof(Fixtures))]
    public void Validate_and_inspect_match_node(string name)
    {
        var fixture = CliTestHost.Fixture(name);
        CliTestHost.AssertFixture(fixture, work => Assert.Equal(Sorted(fixture.Files.Keys), Entries(work)));
    }

    [Theory]
    [InlineData("validate/broken-json-json")]
    [InlineData("validate/bom-json")]
    public void Parse_failure_receipts_match_node_but_for_the_v8_text(string name)
    {
        var fixture = CliTestHost.Fixture(name);
        var (exit, stdout, stderr) = CliTestHost.Run(fixture.Args, fixture.Files);
        Assert.Equal(MaskReason(fixture.Stdout), MaskReason(stdout));
        Assert.Equal((fixture.Exit, fixture.Stderr), (exit, stderr));
    }

    [Theory]
    [InlineData("validate/broken-json-human")]
    [InlineData("validate/bom-human")]
    public void Parse_failure_text_matches_node_but_for_the_v8_text(string name)
    {
        var fixture = CliTestHost.Fixture(name);
        var (exit, stdout, stderr) = CliTestHost.Run(fixture.Args, fixture.Files);
        var shape = new Regex(@"^(?<r>[\s\S]+)\n\[input/json-parse\] Input JSON could not be parsed: \k<r> Fix: repair the JSON syntax and run validation again\.\n\z");
        Assert.Matches(shape, fixture.Stderr);
        Assert.Matches(shape, stderr);
        Assert.Equal((fixture.Exit, fixture.Stdout), (exit, stdout));
    }

    // The receipt's `error` is the parse reason; every occurrence of it (error, message, evidence.reason) becomes <REASON>.
    static string MaskReason(string receipt)
    {
        var reason = JsonNode.Parse(receipt)!["error"]!.GetValue<string>();
        Assert.NotEmpty(reason);
        return receipt.Replace(Js.Stringify(JsonValue.Create(reason))[1..^1], "<REASON>");
    }

    // The check stage cannot be reached from a valid input (the renderer only emits passing artifacts), so a golden's report is
    // edited into a failing one and fed to the receipt builder: one artifact check and one composition error (A:6968-6984).
    [Fact]
    public void Check_stage_failure_carries_the_checker_with_the_input_as_file()
    {
        var checker = ArchifyNet.Check(File.ReadAllBytes(Path.Join(CliTestHost.RepoRoot, "upstream", "examples", "web-app-rendered.html"))).Report;
        checker["ok"] = false;
        checker["checks"]![0]!["ok"] = false;
        checker["checks"]![0]!["details"] = new JsonArray("found 2 <svg> block(s)");
        checker["composition"]!["issues"]!.AsArray().Add(new JsonObject
        {
            ["severity"] = "error", ["code"] = "composition/proper-crossing", ["relationship"] = "users-to-cdn", ["detail"] = "crossing",
        });
        var input = @"C:\work\web-app.architecture.json";

        var failure = Commands.ValidateCommand.CheckFailure("validate", "architecture", input, checker);
        var receipt = failure.ToReceipt();

        Assert.Equal(["schemaVersion", "ok", "command", "stage", "type", "input", "error", "diagnostics", "checker"], receipt.Select(p => p.Key));
        Assert.Equal(("check", "architecture", input, "Final artifact check failed."),
            (Str(receipt["stage"]), Str(receipt["type"]), Str(receipt["input"]), Str(receipt["error"])));
        // checker.file = path.resolve(input) lands at index 1, as the checker's own `file` key sits there.
        Assert.Equal(["ok", "file", "artifact", "checks", "composition"], receipt["checker"]!.AsObject().Select(p => p.Key));
        Assert.Equal(input, Str(receipt["checker"]!["file"]));
        Assert.Equal(["composition/proper-crossing", "artifact/single-svg"], receipt["diagnostics"]!.AsArray().Select(d => Str(d!["code"])));
        Assert.Equal(Js.Stringify(new JsonArray([.. CheckerDiagnostics.From(checker).Select(d => (JsonNode)d.ToJson())])), Js.Stringify(receipt["diagnostics"]));

        // Human mode: formatDiagnostics on stderr, exit 1.
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Assert.Equal(1, Cli.ReportArtifactFailure(new CliIo(stdout, stderr, @"C:\work", CliServices.Default), json: false, failure));
        Assert.Equal("", stdout.ToString());
        Assert.Equal("Final artifact check failed.\n" +
            "[composition/proper-crossing] Final artifact failed composition/proper-crossing. Fix: if authored via/route/channelX/channelY controls exist and are not required by the user, remove them to let the renderer re-plan; otherwise preserve that intent and adjust route/via or channel coordinates so unrelated relationships use separate corridors.\n" +
            "[artifact/single-svg] found 2 <svg> block(s) Fix: remove additional SVG roots so the artifact contains exactly one diagram SVG.\n", stderr.ToString());
    }

    // An implementation error reaches the receipt as internal/unclassified with the input as its subject (diagnostics.mjs:106-116).
    // Upstream's subject.input is the deleted temp snapshot the renderer read (A:6927); the port names the user's input (deviation).
    [Fact]
    public void Unclassified_render_failure_names_the_input()
    {
        var (exit, stdout, stderr) = CliTestHost.Run(["validate", "erd", "unplaced.erd.json", "--json"],
            new Dictionary<string, string> { ["unplaced.erd.json"] = "tests/fixtures/diagrams/erd-unplaced-labelled.erd.json" });
        Assert.Equal((1, ""), (exit, stderr));
        var receipt = JsonNode.Parse(stdout)!;
        Assert.Equal("render", Str(receipt["stage"]));
        var diagnostic = Assert.Single(receipt["diagnostics"]!.AsArray())!;
        Assert.Equal("internal/unclassified", Str(diagnostic["code"]));
        Assert.Equal("""{"input":"<WORK>\\unplaced.erd.json"}""", Js.Stringify(diagnostic["subject"]));
    }

    // `--repo-root` reaches nextAction after the quality (T/validate-next-action.test.mjs:13-40).
    [Fact]
    public void Next_action_carries_the_repo_root()
    {
        var (exit, stdout, _) = CliTestHost.Run(["validate", "workflow", "agent-tool-call.workflow.json", "--json", "--quality", "standard", "--repo-root", "."],
            new Dictionary<string, string> { ["agent-tool-call.workflow.json"] = "upstream/examples/agent-tool-call.workflow.json" });
        Assert.Equal(0, exit);
        var arguments = JsonNode.Parse(stdout)!["nextAction"]!["arguments"]!.AsArray().Select(Str);
        Assert.Equal(["workflow", "<WORK>\\agent-tool-call.workflow.json", "<output.html>", "--quality", "standard", "--repo-root", "<WORK>",
            "--candidate-sha256", "d67965d50872c97e4ff3476271e645bf19fab6b3ab44e111bdb44b063e3aa36f", "--json"], arguments);
    }

    static string? Str(JsonNode? node) => node?.GetValue<string>();

    static List<string> Sorted(IEnumerable<string> names) => [.. names.Order(StringComparer.Ordinal)];

    static List<string> Entries(string work) =>
        [.. Directory.GetFileSystemEntries(work, "*", SearchOption.AllDirectories).Select(e => Path.GetRelativePath(work, e)).Order(StringComparer.Ordinal)];
}
