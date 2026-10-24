using Archify.Net.Cli.Commands;
using Xunit;

namespace Archify.Net.Cli.Tests;

// Phase 5 task 7: `doctor` (A:5873-6052 under Q3), `install-browser` (Q4) and the embedded viewer-gate scripts (Q9).
public class DoctorCommandTests
{
    static readonly CliServices Ready = CliServices.Default with { HasResource = _ => true, GitOnPath = () => true, HeadlessShell = () => "chrome-headless-shell.exe" };

    static readonly string[] Labels =
    [
        $".NET {Environment.Version}", "Core template", "Locale catalogs", "Scenario recipe guide", "Viewer settle script", "Viewer metrics script",
        "architecture schema and example", "workflow schema and example", "sequence schema and example", "dataflow schema and example",
        "lifecycle schema and example", "erd schema and example", "tree schema and example", "class schema and example",
        "timeline schema and example", "waterfall schema and example", "explainer schema and example", "Explainer runtime and reader assets",
        "git on PATH", "Chromium for browser gates",
    ];

    static string Lines(Func<string, string> status) => "Archify doctor\n\n" + string.Concat(Labels.Select(l => $"[{status(l)}] {l}\n"));

    [Theory]
    [InlineData("arguments/doctor-extra-positional")]
    [InlineData("arguments/doctor-unknown-option")]
    public void Doctor_arguments_match_node(string name) => CliTestHost.AssertFixture(CliTestHost.Fixture(name));

    [Fact]
    public void Ready_prints_every_check_ok()
    {
        Assert.Equal((0, Lines(_ => "ok") + "\nArchify is ready.\n", ""), CliTestHost.Run(["doctor"], services: Ready));
    }

    // The default resource lookup finds every resource doctor asks for: template, 7 locale files (manifest + 6), recipes, 2 viewer-gate
    // scripts, 11 schemas, 26 examples, and the explainer's 2 reader assets and 2 gate scripts.
    [Fact]
    public void Every_checked_resource_is_embedded()
    {
        var asked = new List<string>();
        var services = Ready with { HasResource = name => { asked.Add(name); return CliServices.Default.HasResource(name); } };
        Assert.Equal((0, Lines(_ => "ok") + "\nArchify is ready.\n", ""), CliTestHost.Run(["doctor"], services: services));
        Assert.Equal(52, asked.Distinct().Count());
        Assert.Equal(26, asked.Count(n => n.StartsWith("examples/", StringComparison.Ordinal)));
        Assert.Equal(11, asked.Count(n => n.StartsWith("schemas/", StringComparison.Ordinal)));
        Assert.Contains("viewer-gate/settle.js", asked);
        Assert.Contains("viewer-gate/metrics.js", asked);
        Assert.Contains("explainer/reader.js", asked);
        Assert.Contains("explainer-gate/measure-public-html.js", asked);
        Assert.False(CliServices.Default.HasResource("examples/nope.json"));
    }

    [Fact]
    public void Missing_resources_and_runtime_checks_are_counted_apart()
    {
        string[] absent = ["schemas/erd.schema.json", "examples/subscription-billing.erd.json", "viewer-gate/metrics.js"];
        var services = Ready with { HasResource = name => !absent.Contains(name), GitOnPath = () => false, HeadlessShell = () => null };
        string[] missing = ["erd schema and example", "Viewer metrics script", "git on PATH", "Chromium for browser gates"];
        Assert.Equal((1, Lines(l => missing.Contains(l) ? "missing" : "ok"), "\nArchify is not ready: 3 required files missing; 2 runtime checks failed.\n"),
            CliTestHost.Run(["doctor"], services: services));
    }

    [Fact]
    public void Singular_summaries()
    {
        Assert.Equal((1, Lines(l => l == "Core template" ? "missing" : "ok"), "\nArchify is not ready: 1 required file missing.\n"),
            CliTestHost.Run(["doctor"], services: Ready with { HasResource = name => name != "assets/template.html" }));
        Assert.Equal((1, Lines(l => l == "Chromium for browser gates" ? "missing" : "ok"), "\nArchify is not ready: 1 runtime check failed.\n"),
            CliTestHost.Run(["doctor"], services: Ready with { HeadlessShell = () => null }));
    }

    // Q10: the real probes. Both fail loudly, naming the fix, when git or Chromium is absent from this machine.
    [Fact]
    public void Real_probes_find_git_and_the_headless_shell()
    {
        Assert.True(DoctorCommand.GitOnPath(), "git is not on PATH; install git");
        var shell = DoctorCommand.HeadlessShell();
        Assert.True(shell is not null, "Playwright's Chromium headless shell is not installed; run `archify install-browser`");
        Assert.True(File.Exists(shell));
        Assert.Matches(@"[\\/]chromium_headless_shell-\d+[\\/][^\\/]+[\\/]chrome-headless-shell(\.exe)?$", shell);
    }

    [Fact]
    public void Install_browser_runs_playwrights_installer_in_process_and_passes_its_exit_code_through()
    {
        string[]? seen = null;
        var services = CliServices.Default with { PlaywrightMain = args => { seen = args; return 7; } };
        Assert.Equal((7, "", ""), CliTestHost.Run(["install-browser"], services: services));
        Assert.Equal(["install", "--only-shell", "chromium"], seen!);
    }

    // No Node counterpart: the guards follow doctor's (A:5874-5876).
    [Fact]
    public void Install_browser_rejects_arguments_without_installing()
    {
        var services = CliServices.Default with { PlaywrightMain = _ => throw new InvalidOperationException("must not install") };
        Assert.Equal((2, "", "Unknown install-browser option \"--force\".\n"), CliTestHost.Run(["install-browser", "--force"], services: services));
        Assert.Equal((2, "", UsageEdit.Edited + "\n"), CliTestHost.Run(["install-browser", "extra"], services: services));
    }

    // An empty PATHEXT entry adds no "" extension: an extensionless `git` file is not a command on Windows.
    [Fact]
    public void Empty_pathext_entries_are_no_extension()
    {
        var bare = Directory.CreateTempSubdirectory("archify-git-").FullName;
        var exe = Directory.CreateTempSubdirectory("archify-git-").FullName;
        try
        {
            File.WriteAllText(Path.Join(bare, "git"), "");
            File.WriteAllText(Path.Join(exe, "git.exe"), "");
            Assert.False(DoctorCommand.GitOnPath(bare, ".COM;;.EXE;"));
            Assert.True(DoctorCommand.GitOnPath($"{bare};\"{exe}\"", ".COM;;.EXE;"));
            Assert.False(DoctorCommand.GitOnPath(exe, ".COM"));
        }
        finally
        {
            Directory.Delete(bare, recursive: true);
            Directory.Delete(exe, recursive: true);
        }
    }

    // Q9: the embedded scripts are the verbatim upstream/viewer-gate copies (VC:1603-1633, VC:1635-1799).
    [Theory]
    [InlineData("settle")]
    [InlineData("metrics")]
    public void Viewer_gate_scripts_are_embedded_verbatim(string name)
    {
        var file = File.ReadAllBytes(Path.Combine(CliTestHost.RepoRoot, "upstream", "viewer-gate", name + ".js"));
        using var stream = typeof(Cli).Assembly.GetManifestResourceStream($"viewer-gate/{name}.js")!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        Assert.Equal(file, copy.ToArray());
        Assert.StartsWith("(function () {\n", System.Text.Encoding.UTF8.GetString(file));
        Assert.EndsWith("\n    })()", System.Text.Encoding.UTF8.GetString(file));
    }
}
