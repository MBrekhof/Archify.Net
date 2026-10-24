using Xunit;

namespace Archify.Net.Cli.Tests;

// Phase 5 task 3: dispatch (A:7059-7138), usage() under Q3, the argument receipt, and the option extractors.
public class DispatchTests
{
    public static TheoryData<string> HelpFixtures() => [.. CliTestHost.FixtureNames("help")];

    [Theory]
    [MemberData(nameof(HelpFixtures))]
    public void Help_and_unknown_commands_match_node(string name) => CliTestHost.AssertFixture(CliTestHost.Fixture(name));

    // inspect's first-argument check runs in the dispatcher, before commandValidate (A:7094-7102).
    [Theory]
    [InlineData("arguments/inspect-unsupported-type")]
    [InlineData("arguments/inspect-unsupported-type-json")]
    public void Inspect_first_argument_check_matches_node(string name) => CliTestHost.AssertFixture(CliTestHost.Fixture(name));

    // Captured from Node (UP @ c21c31ff): the check is literal on args[0], so `inspect --json` reports type "--json", and a
    // non-sole --help is just an argument.
    [Fact]
    public void Inspect_checks_args0_literally()
    {
        var unsupported = CliTestHost.Fixture("arguments/inspect-unsupported-type-json");
        Assert.Equal((2, unsupported.Stdout.Replace("\"type\": \"workflow\"", "\"type\": \"--json\""), ""), CliTestHost.Run(["inspect", "--json"]));
        var human = (2, "", "inspect is currently supported for architecture diagrams only.\n");
        Assert.Equal(human, CliTestHost.Run(["inspect"]));
        Assert.Equal(human, CliTestHost.Run(["inspect", "--help", "x"]));
    }

    // Q3: install-browser has no Node counterpart; its sole --help prints the edited usage().
    [Fact]
    public void Install_browser_help_prints_the_edited_usage() =>
        Assert.Equal((0, UsageEdit.Edited + "\n", ""), CliTestHost.Run(["install-browser", "--help"]));

    // Q3: the four OUT commands answer Unknown command, exit 2, with or without --help.
    [Theory]
    [InlineData("compare")]
    [InlineData("preview")]
    [InlineData("migrate")]
    [InlineData("brands")]
    public void Out_commands_are_unknown(string command)
    {
        var expected = UsageEdit.Apply(CliTestHost.Fixture("help/unknown-command").Stderr.Replace("\"bogus\"", $"\"{command}\""));
        Assert.Equal((2, "", expected), CliTestHost.Run([command]));
        Assert.Equal((2, "", expected), CliTestHost.Run([command, "--help"]));
        Assert.Equal((2, "", expected), CliTestHost.Run([command, "architecture", "a.json", "--json"]));
    }

    [Fact]
    public void Usage_edit_is_upstreams_usage_minus_out_commands_plus_install_browser()
    {
        Assert.Equal(Cli.Usage(), UsageEdit.Edited);
        Assert.DoesNotContain("brands", UsageEdit.Edited);
        Assert.DoesNotContain("explainer", UsageEdit.Edited);   // PORT-009
        Assert.Contains("  archify doctor\n  archify install-browser\n  archify demo [output-directory]\n", UsageEdit.Edited);
    }

    // The extractor rules (A:2304-2389, A:5706-5770); each command's own fixtures pin the messages end to end.
    [Fact]
    public void Extractors_take_both_forms_and_the_last_occurrence()
    {
        Assert.Equal(("x y", "showcase"), Pair(Args.Quality(["--quality", "standard", "x", "--quality=showcase", "y"])));
        Assert.Equal(("", null), Pair(Args.Quality([])));
        Assert.Equal(("-x", "a.json"), Pair(Args.Receipt(["--receipt=a.json", "-x"])));
        Assert.Equal(("", "out"), Pair(Args.OutDir(["--out-dir", "out"])));
        var sha = new string('a', 64);
        Assert.Equal(("", sha), Pair(Args.CandidateSha256(["--candidate-sha256", sha])));
        var cwd = Path.GetTempPath();
        Assert.Equal(Path.Combine(cwd, "repo"), Args.RepoRoot(["--repo-root", "repo" + Path.DirectorySeparatorChar], cwd).RepoRoot);
        Assert.Equal(Path.GetPathRoot(cwd), Args.RepoRoot([$"--repo-root={Path.GetPathRoot(cwd)}"], cwd).RepoRoot);
    }

    // PORT-009 Task 1: Core knows DiagramType.Explainer, but no CLI command takes it until Task 7 (which removes this fact).
    [Theory]
    [InlineData("validate", "--json")]
    [InlineData("validate")]
    public void Explainer_is_still_an_unknown_type(params string[] command)
    {
        string[] Args(string type) => [command[0], type, "x", .. command[1..]];
        var explainer = CliTestHost.Run(Args("explainer"));
        Assert.Contains(command.Length > 1 ? "\"code\": \"cli/unknown-diagram-type\"" : "Unknown diagram type \"explainer\"", explainer.Item2 + explainer.Item3);
        var bogus = CliTestHost.Run(Args("bogus"));
        Assert.Equal((bogus.Item1, bogus.Item2.Replace("bogus", "explainer"), bogus.Item3.Replace("bogus", "explainer")), explainer);
    }

    static (string, string?) Pair((string[] Remaining, string? Value) r) => (string.Join(" ", r.Remaining), r.Value);

    // extractExplainerWritingArgs' success path (A:2397-2413): upstream's three values, both forms, the flag removed from rest.
    [Theory]
    [InlineData("warn")]
    [InlineData("strict")]
    [InlineData("off")]
    public void Writing_extractor_accepts_each_value_for_an_explainer(string mode)
    {
        Assert.Equal(("explainer x.json", mode), Pair(Args.ExtractExplainerWritingArgs(["explainer", "x.json", "--writing", mode])));
        Assert.Equal(("explainer x.json --json", mode), Pair(Args.ExtractExplainerWritingArgs(["explainer", $"--writing={mode}", "x.json", "--json"])));
    }

    [Theory]
    [InlineData("--quality")]
    [InlineData("--quality", "--json")]
    [InlineData("--quality", "")]
    [InlineData("--quality=")]
    [InlineData("--repo-root")]
    [InlineData("--repo-root=")]
    [InlineData("--receipt", "--json")]
    [InlineData("--candidate-sha256")]
    public void Missing_values_are_rejected(params string[] args)
    {
        var option = args[0].Split('=')[0];
        var e = Assert.Throws<CliArgumentException>(() =>
        {
            _ = Args.Quality(args); _ = Args.RepoRoot(args, "."); _ = Args.Receipt(args); _ = Args.CandidateSha256(args);
        });
        Assert.Equal("cli/missing-option-value", e.Code);
        Assert.Equal(option, e.Subject["option"]!.GetValue<string>());
    }

    [Fact]
    public void Bad_values_are_rejected_after_the_loop()
    {
        var quality = Assert.Throws<CliArgumentException>(() => Args.Quality(["--quality", "hero", "--quality=showcase", "--quality", "draft"]));
        Assert.Equal(("cli/invalid-option-value", "Unknown quality profile \"draft\". Expected standard or showcase."), (quality.Code, quality.Message));
        Assert.Throws<CliArgumentException>(() => Args.CandidateSha256(["--candidate-sha256", new string('A', 64)]));
        Assert.Throws<CliArgumentException>(() => Args.CandidateSha256(["--candidate-sha256", new string('a', 64) + "\n"]));
        var outDir = Assert.Throws<CliFailException>(() => Args.OutDir(["--out-dir="]));
        Assert.Equal(("--out-dir requires a directory path.", 2), (outDir.Message, outDir.ExitCode));
    }
}
