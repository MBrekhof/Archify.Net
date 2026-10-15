using Xunit;

namespace Archify.Net.Cli.Tests;

// Phase 5 task 5: `render`, `demo` and `examples` against the Node-minted fixtures (stdout, stderr, exit byte-equal), every
// written HTML byte-equal to its golden, and failures and option guards leaving nothing behind.
public class RenderCommandTests
{
    const string Upstream = "upstream/examples/";

    // The HTML each success fixture writes (workdir-relative) and the golden ParityTests holds for that input.
    static readonly Dictionary<string, (string Output, string Golden)[]> Writes = new()
    {
        ["render/render-output-arg"] = [("out.html", Upstream + "web-app-rendered.html")],
        ["render/render-meta-output"] = [("web-app-rendered.html", Upstream + "web-app-rendered.html")],
        ["render/render-meta-output-subdir"] = [(@"examples\workflow-agent-tool-call-rendered.html", Upstream + "workflow-agent-tool-call-rendered.html")],
        ["render/render-quality-standard"] = [("standard.html", "tests/goldens/agent-tool-call.option-standard-rendered.html")],
        ["render/render-nested-output"] = [(@"nested\deeper\out.html", Upstream + "web-app-rendered.html")],
        ["render/render-locale-warning"] = [("out.html", "")],   // no golden for en-US web-app; written, not compared
        ["render/render-empty-output"] = [("web-app-rendered.html", Upstream + "web-app-rendered.html")],
        ["render/demo-default"] = [("archify-demo.html", Upstream + "web-app-rendered.html")],
        ["render/demo-empty-directory"] = [("archify-demo.html", Upstream + "web-app-rendered.html")],
        ["render/demo-directory"] = [(@"my-demo\archify-demo.html", Upstream + "web-app-rendered.html")],
    };

    public static TheoryData<string> RenderFixtures() => [.. CliTestHost.FixtureNames("render").Where(n => n != "render/examples")];

    [Theory, MemberData(nameof(RenderFixtures))]
    public void Render_and_demo_match_node(string name)
    {
        var fixture = CliTestHost.Fixture(name);
        CliTestHost.AssertFixture(fixture, work =>
        {
            var writes = Writes.GetValueOrDefault(name) ?? [];
            Assert.Equal(fixture.Exit == 0, writes.Length > 0);
            foreach (var (output, golden) in writes)
            {
                var written = Path.Join(work, output);
                Assert.True(File.Exists(written), $"{output} was not written");
                if (golden.Length > 0) AssertSameBytes(golden, written);
            }
            // Nothing else: no temp file, and a failure writes no output.
            Assert.Equal(Expected(fixture.Files.Keys.Concat(writes.Select(w => w.Output))), Entries(work));
        });
    }

    [Fact]
    public void Examples_render_the_15_goldens_into_cwd_examples()
    {
        var fixture = CliTestHost.Fixture("render/examples");
        var (exit, stdout, stderr) = CliTestHost.Run(fixture.Args, fixture.Files, inspect: work =>
        {
            var names = Directory.GetFiles(Path.Join(work, "examples")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(15, names.Count);
            foreach (var file in names) AssertSameBytes(Upstream + file, Path.Join(work, "examples", file!));
            Assert.Equal(["examples"], Directory.GetFileSystemEntries(work).Select(Path.GetFileName));
        });
        // Q3/Q8: upstream writes into the skill; the port into <cwd>/examples.
        Assert.Equal(fixture.Stdout.Replace(@"<SKILL>\examples\", @"<WORK>\examples\"), stdout);
        Assert.Equal(fixture.Stderr, stderr);
        Assert.Equal(fixture.Exit, exit);
    }

    public static TheoryData<string> OptionGuards() =>
        [.. new[] { "render-", "demo-", "examples-" }.SelectMany(prefix => CliTestHost.FixtureNames("arguments", prefix))];

    [Theory, MemberData(nameof(OptionGuards))]
    public void Option_guards_match_node_and_create_no_files(string name)
    {
        var fixture = CliTestHost.Fixture(name);
        CliTestHost.AssertFixture(fixture, work => Assert.Equal(Expected(fixture.Files.Keys), Entries(work)));
    }

    // fail(`Could not create demo directory "<dir>": <message>`, 1) (A:6173-6177); the message after the colon is the OS's (Q8).
    [Fact]
    public void Demo_reports_a_directory_it_cannot_create()
    {
        var (exit, stdout, stderr) = CliTestHost.Run(["demo", "blocker"], new Dictionary<string, string> { ["blocker"] = Upstream + "web-app.architecture.json" });
        Assert.Equal((1, ""), (exit, stdout));
        Assert.StartsWith("Could not create demo directory \"<WORK>\\blocker\": ", stderr);
    }

    // The parse failure's text is V8's upstream and .NET's here (Q8: code and subject).
    [Fact]
    public void Render_reports_unparseable_input_as_input_json_parse()
    {
        var (exit, stdout, stderr) = CliTestHost.Run(["render", "workflow", "broken.workflow.json", "out.html"],
            new Dictionary<string, string> { ["broken.workflow.json"] = "tests/fixtures/cli/inputs/broken.workflow.json" });
        Assert.Equal((1, ""), (exit, stdout));
        Assert.StartsWith("Input JSON could not be parsed: ", stderr);
        Assert.Contains("\n[input/json-parse] Input JSON could not be parsed: ", stderr);
    }

    // A second render replaces the first output in place and leaves no temp file.
    [Fact]
    public void Render_replaces_an_existing_output()
    {
        var args = new[] { "render", "architecture", "web-app.architecture.json", "out.html" };
        var seed = new Dictionary<string, string> { ["web-app.architecture.json"] = Upstream + "web-app.architecture.json", ["out.html"] = Upstream + "sequence-cache-miss-request.html" };
        var (exit, stdout, stderr) = CliTestHost.Run(args, seed, inspect: work =>
        {
            AssertSameBytes(Upstream + "web-app-rendered.html", Path.Join(work, "out.html"));
            Assert.Equal(Expected(seed.Keys), Entries(work));
        });
        Assert.Equal((0, "<WORK>\\out.html\n", ""), (exit, stdout, stderr));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("standard", QualityProfile.Standard)]
    [InlineData("showcase", QualityProfile.Showcase)]
    public void Quality_maps_onto_render_options(string? quality, QualityProfile? expected) =>
        Assert.Equal(new RenderOptions(expected, @"C:\repo"), Args.RenderOptions(quality, @"C:\repo"));

    static void AssertSameBytes(string goldenRepoRelative, string written) =>
        Assert.True(File.ReadAllBytes(Path.Join(CliTestHost.RepoRoot, goldenRepoRelative)).AsSpan().SequenceEqual(File.ReadAllBytes(written)),
            $"{written} differs from {goldenRepoRelative}");

    // Every file and directory under `work`, relative; or the set the given files imply (each file plus its parent directories).
    static List<string> Entries(string work) =>
        [.. Directory.GetFileSystemEntries(work, "*", SearchOption.AllDirectories).Select(e => Path.GetRelativePath(work, e)).Order(StringComparer.Ordinal)];

    static List<string> Expected(IEnumerable<string> files) =>
        [.. files.SelectMany(f =>
        {
            var parts = f.Split('\\', '/');
            return Enumerable.Range(1, parts.Length).Select(n => string.Join('\\', parts[..n]));
        }).Distinct().Order(StringComparer.Ordinal)];
}
