using System.Text.Json.Nodes;
using Archify.Net.Cli.Paths;
using Xunit;
using Scratch = Archify.Net.Cli.Tests.PathSemanticsTests.Scratch;

namespace Archify.Net.Cli.Tests;

// Phase 5 task 5: output-path.mjs (CLI half) and sidecar-path.mjs. T/native-output-path.test.mjs is table-ported with Node's
// full outcome per row (tests/fixtures/cli/paths); the render and deliver cases of T/output-path.test.mjs run on the real
// filesystem; the bounded-stem table and the NTFS sidecar names compare with values Node computed on this volume.
public class OutputPathsTests
{
    static JsonNode Table(string name) => JsonNode.Parse(CliTestHost.ReadLf(Path.Combine(CliTestHost.FixtureRoot, "paths", name)))!;

    // ---- T/native-output-path.test.mjs ----

    public static TheoryData<int, string> NativeRows()
    {
        var data = new TheoryData<int, string>();
        foreach (var (row, index) in Table("native-output-path.json").AsArray().Select((row, index) => (row, index))) data.Add(index, $"{row!["fn"]} {row["value"]}");
        return data;
    }

    [Theory, MemberData(nameof(NativeRows))]
    public void Native_output_path_row_matches_node(int index, string label)
    {
        var row = Table("native-output-path.json")[index]!.AsObject();
        var value = row["value"]!.GetValue<string>();
        var options = row["options"]!.AsObject();
        var platform = options["platform"]!.GetValue<string>();
        var fs = new ForbiddenFs();
        string Call() => row["fn"]!.GetValue<string>() switch
        {
            "validate" => OutputPaths.ValidateNativeOutputPath(value, platform, options["kind"]?.GetValue<string>() ?? "file"),
            "directory" => OutputPaths.ResolveNativeOutputDirectory(value, options["cwd"]!.GetValue<string>(), platform),
            // "IPC shares fail before output resolution touches the filesystem": any filesystem call fails the row.
            _ => new OutputPaths(new PathSemantics(fs)).ResolveOutputPath(
                new OutputRequest(CliTestHost.RepoRoot, value, options["defaultOutput"]!.GetValue<string>()) { Platform = platform }).OutputPath,
        };
        if (row["ok"]!.GetValue<bool>())
        {
            Assert.Equal(row["result"]!.GetValue<string>(), Call());
            return;
        }
        var error = Assert.Throws<ArchifyDiagnosticException>(Call);
        Assert.Equal(row["message"]!.GetValue<string>(), error.Message);
        AssertDiagnostic(row["diagnostic"]!.AsObject(), Assert.Single(error.Diagnostics), label);
        Assert.Equal(0, fs.Calls);
    }

    // The unpaired-surrogate rows cannot cross a JSON fixture intact (they read back as U+FFFD), so they are asserted here.
    [Theory]
    [InlineData("darwin")]
    [InlineData("linux")]
    [InlineData("win32")]
    public void Native_outputs_reject_unpaired_UTF16_surrogates_without_rejecting_valid_pairs(string platform)
    {
        foreach (var surrogate in new[] { "\ud800", "\udfff" })
        {
            var error = Assert.Throws<ArchifyDiagnosticException>(() => OutputPaths.ValidateNativeOutputPath($"reports/{surrogate}.html", platform));
            Assert.Equal("unpaired-surrogate", error.Diagnostics[0].Evidence["reason"]!.GetValue<string>());
        }
        Assert.Equal("reports/\ud83d\ude00.html", OutputPaths.ValidateNativeOutputPath("reports/\ud83d\ude00.html", platform));
    }

    static void AssertDiagnostic(JsonObject expected, Diagnostic actual, string label)
    {
        Assert.Equal(expected["code"]!.GetValue<string>(), actual.Code);
        Assert.Equal(expected["message"]!.GetValue<string>(), actual.Message);
        Assert.Equal(Severity.Error, actual.Severity);
        Assert.Equal(Js.Stringify(expected["subject"]), Js.Stringify(actual.Subject));
        Assert.True(Js.Stringify(expected["evidence"]) == Js.Stringify(actual.Evidence), $"{label}: {Js.Stringify(expected["evidence"])} vs {Js.Stringify(actual.Evidence)}");
        Assert.Equal(expected["supportedFixes"]!.AsArray().Select(f => f!.GetValue<string>()), actual.SupportedFixes);
    }

    // Every call fails: native validation must reject before resolution starts.
    sealed class ForbiddenFs : IPathFs
    {
        public int Calls { get; private set; }
        Exception Touch() { Calls++; return new InvalidOperationException("unexpected filesystem access"); }
        public string RealPath(string path) => throw Touch();
        public FsStat Stat(string path) => throw Touch();
        public FsStat LStat(string path) => throw Touch();
        public string ReadLink(string path) => throw Touch();
        public IDisposable CreateExclusive(string path) => throw Touch();
        public void Delete(string path) => throw Touch();
    }

    // ---- node:path behaviour the port re-implements (values printed by Node v24) ----

    [Theory]
    [InlineData(".html", "")]
    [InlineData("..html", ".html")]
    [InlineData("a.", ".")]
    [InlineData(@"C:\x\a.HTML", ".HTML")]
    [InlineData("dir/a.html/", ".html")]
    [InlineData("a.b.c", ".c")]
    [InlineData("C:.html", "")]
    [InlineData("..", "")]
    [InlineData(@"C:\x\.html", "")]
    public void Extname_is_node_win32_extname(string path, string expected) => Assert.Equal(expected, NodePath.Extname(path, win32: true));

    [Theory]
    [InlineData(@"C:\reports\..\x.html", @"C:\x.html")]
    [InlineData(@".\reports\..\d.html", "d.html")]
    [InlineData(@"..\..\a", @"..\..\a")]
    [InlineData("//server/share", @"\\server\share\")]
    [InlineData("//server/share/a//b/../c/", @"\\server\share\a\c\")]
    [InlineData(@"\\.\NUL", @"\\.\NUL")]
    [InlineData("//?/C:/r/d.html", @"\\?\C:\r\d.html")]
    [InlineData("C:", "C:.")]
    [InlineData(@"C:a\..\..\b", @"C:..\b")]
    [InlineData("a/b/", @"a\b\")]
    [InlineData(@"C:\a\..\..\b", @"C:\b")]
    [InlineData(@"\a\.\b", @"\a\b")]
    [InlineData("", ".")]
    [InlineData(@"\\?\C:", @"\\?\C:")]
    [InlineData(@"\\.\NUL\", @"\\.\NUL\")]
    [InlineData(@"\\x\NUL", @"\\x\NUL\")]
    [InlineData(@"\\.\NUL\a", @"\\.\NUL\a")]
    [InlineData(@"\\?\UNC", @"\\?\UNC")]
    public void Normalize_is_node_win32_normalize(string path, string expected) => Assert.Equal(expected, NodePath.Normalize(path));

    [Fact]
    public void Join_is_node_win32_join() => Assert.Equal(@"C:\a\c.html", NodePath.Join(@"C:\a", ["b", "..", "c.html"]));

    // ---- sidecar-path.mjs: all four functions against Node ----

    [Fact]
    public void Bounded_sidecar_stems_match_node()
    {
        var table = Table("sidecar-stems.json");
        foreach (var row in table["stems"]!.AsArray())
        {
            var stem = row!["stem"]!.GetValue<string>();
            var suffixes = row["suffixes"]!.AsArray().Select(s => s!.GetValue<string>()).ToList();
            var options = row["options"] as JsonObject;
            var label = $"{stem} {Js.Stringify(row["options"])} {suffixes[0]}";
            Assert.True(row["needsBounding"]!.GetValue<bool>() == SidecarPath.StemNeedsBounding(stem, suffixes), label);
            Assert.True(row["isBounded"]!.GetValue<bool>() == SidecarPath.IsBoundedSidecarStem(stem), label);
            Assert.Equal(row["bounded"]!.GetValue<string>(), SidecarPath.BoundedSidecarStem(stem, suffixes,
                options?["force"]?.GetValue<bool>() ?? false, options?["hashDomain"]?.GetValue<string>()));
        }
        foreach (var row in table["components"]!.AsArray())
        {
            var (stem, force, hashDomain) = SidecarPath.StemFromComponent(row!["component"]!.GetValue<string>());
            Assert.Equal(row["stem"]!.GetValue<string>(), stem);
            var options = row["options"] as JsonObject;
            Assert.Equal(options?["force"]?.GetValue<bool>() ?? false, force);
            Assert.Equal(options?["hashDomain"]?.GetValue<string>(), hashDomain);
            Assert.Equal(row["bounded"]!.GetValue<string>(), SidecarPath.BoundedSidecarStem(stem, SidecarPath.DeliverySuffixes, force, hashDomain));
        }
    }

    // WebApp.HTML, Ünïcode Straße.html (NFC and NFD), 240×L: the delivery provenance name of a future artifact in a fresh
    // directory on this volume. NTFS folds the case, keeps ß and keeps the NFD spelling distinct; the long stem is bounded.
    [Fact]
    public void Delivery_sidecar_names_on_NTFS_match_node()
    {
        using var scratch = new Scratch();
        var directory = NativeFs.RealPath(scratch.Root);
        foreach (var row in Table("ntfs-sidecar-names.json").AsArray())
        {
            var name = row!["name"]!.GetValue<string>();
            Assert.Equal(Path.Join(directory, row["provenance"]!.GetValue<string>()), SidecarPath.DeliverySidecarPath(Path.Join(scratch.Root, name), ".delivery.json"));
        }
        Assert.Empty(Directory.GetFileSystemEntries(scratch.Root));   // the namespace probes leave nothing behind
    }

    // ---- T/output-path.test.mjs: future-path aliases, and the render and deliver cases ----

    static readonly string WorkflowFixture = Path.Join(CliTestHost.RepoRoot, "upstream", "examples", "agent-tool-call.workflow.json");

    static (int Exit, string Stdout, string Stderr) RunIn(string cwd, params string[] args)
    {
        var (stdout, stderr) = (new StringWriter(), new StringWriter());
        var exit = Cli.Run(args, new CliIo(stdout, stderr, cwd, CliServices.Default));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    static string WorkflowWithOutput(string path, string? output)
    {
        var source = JsonNode.Parse(File.ReadAllText(WorkflowFixture))!;
        if (output is not null) source["meta"]!["output"] = output;
        File.WriteAllText(path, source.ToJsonString());
        return path;
    }

    // directoryAliasesNames (:39-57)
    static bool DirectoryAliasesNames(string directory, string authoredName, string lookupName)
    {
        var authored = Path.Join(directory, authoredName);
        using (new FileStream(authored, FileMode.CreateNew)) { }
        try
        {
            var lookup = Path.Join(directory, lookupName);
            return File.Exists(lookup) && NativeFs.Identity(authored) == NativeFs.Identity(lookup);
        }
        finally { File.Delete(authored); }
    }

    [Fact]
    public void Future_path_aliases_follow_the_containing_directory_case_and_Unicode_semantics()
    {
        using var scratch = new Scratch();
        var cwd = scratch.Root;
        var caseInsensitive = DirectoryAliasesNames(cwd, "ArchifyCaseProbe", "archifycaseprobe");
        var normalizationInsensitive = DirectoryAliasesNames(cwd, "archify-norm-\u00e9-probe", "archify-norm-e\u0301-probe");
        Assert.True(caseInsensitive);   // NTFS default; the assertions below would hold either way
        Assert.Equal(caseInsensitive, OutputPaths.Native.PathsAlias(Path.Join(cwd, "Future.HTML"), Path.Join(cwd, "future.html")));
        Assert.Equal(normalizationInsensitive, OutputPaths.Native.PathsAlias(Path.Join(cwd, "Caf\u00e9.html"), Path.Join(cwd, "Cafe\u0301.html")));
    }

    [Fact]
    public void Render_reports_an_output_symlink_cycle_as_a_structured_output_diagnostic()
    {
        using var scratch = new Scratch();
        var input = WorkflowWithOutput(scratch.Path("diagram.workflow.json"), null);
        var output = scratch.Path("cycle-a.html");
        var otherLink = scratch.Path("cycle-b.html");
        PathSemanticsTests.FileSymlink(output, otherLink);
        PathSemanticsTests.FileSymlink(otherLink, output);

        var (exit, _, stderr) = RunIn(scratch.Root, "render", "workflow", input, output);
        Assert.Equal(1, exit);
        Assert.StartsWith($"Output path contains a symbolic-link cycle: \"{output}\".\n[output/symlink-cycle] ", stderr);
        var error = Assert.Throws<ArchifyDiagnosticException>(() => OutputPaths.Native.ResolveOutputPath(
            new OutputRequest(scratch.Root, output, "workflow.html") { InputPaths = [input] }));
        Assert.Equal(output, error.Diagnostics[0].Subject["output"]!.GetValue<string>());
        Assert.NotEmpty(error.Diagnostics[0].SupportedFixes);
    }

    [Fact]
    public void Render_rejects_an_output_symlink_that_aliases_its_JSON_input()
    {
        using var scratch = new Scratch();
        var input = WorkflowWithOutput(scratch.Path("diagram.workflow.json"), null);
        var source = File.ReadAllBytes(input);
        var output = scratch.Path("diagram.html");
        PathSemanticsTests.FileSymlink(output, input);

        var (exit, _, stderr) = RunIn(scratch.Root, "render", "workflow", input, output);
        Assert.Equal(1, exit);
        Assert.Contains("output must not replace an input", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(source, File.ReadAllBytes(input));
    }

    [Fact]
    public void Render_rejects_an_existing_output_hard_link_to_its_JSON_input()
    {
        using var scratch = new Scratch();
        var input = WorkflowWithOutput(scratch.Path("diagram.workflow.json"), null);
        var source = File.ReadAllBytes(input);
        var output = scratch.Path("diagram.html");
        PathSemanticsTests.HardLink(output, input);

        var (exit, _, stderr) = RunIn(scratch.Root, "render", "workflow", input, output);
        Assert.Equal(1, exit);
        Assert.Contains("output must not replace an input", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(source, File.ReadAllBytes(input));
    }

    [Fact]
    public void Render_rejects_an_absolute_meta_output_when_no_CLI_output_is_provided()
    {
        using var scratch = new Scratch();
        var output = scratch.Path("authored.html");
        var input = WorkflowWithOutput(scratch.Path("diagram.workflow.json"), output);

        var (exit, _, stderr) = RunIn(scratch.Root, "render", "workflow", input);
        Assert.Equal(1, exit);
        Assert.StartsWith("meta.output must be a relative path", stderr);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Render_rejects_a_parent_segment_in_portable_meta_output_before_filesystem_containment()
    {
        using var scratch = new Scratch();
        var cwd = scratch.Directory("work");
        var input = WorkflowWithOutput(Path.Join(cwd, "diagram.workflow.json"), "../escaped.html");

        var (exit, _, stderr) = RunIn(cwd, "render", "workflow", input);
        Assert.Equal(1, exit);
        Assert.Contains("[output/meta-path-syntax]", stderr);
        Assert.Contains("portable POSIX-relative path", stderr);
        Assert.False(File.Exists(scratch.Path("escaped.html")));
    }

    [Fact]
    public void Render_rejects_a_meta_output_that_escapes_through_a_directory_symlink()
    {
        using var scratch = new Scratch();
        var cwd = scratch.Directory("work");
        var outside = scratch.Directory("outside");
        Directory.CreateSymbolicLink(Path.Join(cwd, "linked"), outside);
        var input = WorkflowWithOutput(Path.Join(cwd, "diagram.workflow.json"), "linked/authored.html");

        var (exit, _, stderr) = RunIn(cwd, "render", "workflow", input);
        Assert.Equal(1, exit);
        Assert.StartsWith("meta.output must stay inside the current working directory.\n[output/meta-outside-cwd] ", stderr);
        Assert.False(File.Exists(Path.Join(outside, "authored.html")));
    }

    [Fact]
    public void Render_requires_a_meta_output_target_with_an_html_extension()
    {
        using var scratch = new Scratch();
        var input = WorkflowWithOutput(scratch.Path("diagram.workflow.json"), "authored.json");

        var (exit, _, stderr) = RunIn(scratch.Root, "render", "workflow", input);
        Assert.Equal(1, exit);
        Assert.StartsWith("meta.output must target an .html file.", stderr);
        Assert.False(File.Exists(scratch.Path("authored.json")));
    }

    [Fact]
    public void Render_rejects_a_meta_output_symlink_that_resolves_to_a_non_html_target()
    {
        using var scratch = new Scratch();
        var input = WorkflowWithOutput(scratch.Path("diagram.workflow.json"), "authored.html");
        var target = scratch.File("authored.json", "trusted target");
        PathSemanticsTests.FileSymlink(scratch.Path("authored.html"), target);

        var (exit, _, stderr) = RunIn(scratch.Root, "render", "workflow", input);
        Assert.Equal(1, exit);
        Assert.StartsWith("meta.output must resolve to an .html file.\n[output/meta-resolved-extension] ", stderr);
        Assert.Equal("trusted target", File.ReadAllText(target));
    }

    // Through resolveOutputPath, as deliver's prepare stage calls it (A:4462-4468); the deliver command itself is Task 8.
    [Fact]
    public void Deliver_rejects_a_future_path_alias_of_its_JSON_input_with_a_structured_diagnostic()
    {
        using var scratch = new Scratch();
        var real = scratch.Directory("real");
        var linked = scratch.Path("linked");
        Directory.CreateSymbolicLink(linked, real);
        var input = WorkflowWithOutput(Path.Join(real, "diagram.workflow.json"), null);
        var output = Path.Join(linked, "diagram.workflow.json");

        var error = Assert.Throws<ArchifyDiagnosticException>(() => OutputPaths.Native.ResolveOutputPath(
            new OutputRequest(scratch.Root, output, "workflow.html") { InputPaths = [input] }));
        Assert.Equal("output/input-alias", Assert.Single(error.Diagnostics).Code);
        Assert.Equal(output, error.Diagnostics[0].Subject["output"]!.GetValue<string>());
        Assert.Equal(input, error.Diagnostics[0].Subject["input"]!.GetValue<string>());
    }

    // writeDiagram commits to the physical target: an output that is a symbolic link keeps the link and replaces its target.
    [Fact]
    public void Render_writes_through_an_output_symlink_to_its_html_target()
    {
        using var scratch = new Scratch();
        var input = WorkflowWithOutput(scratch.Path("diagram.workflow.json"), null);
        var target = scratch.File("target.html", "old");
        var output = scratch.Path("link.html");
        PathSemanticsTests.FileSymlink(output, target);

        var (exit, stdout, stderr) = RunIn(scratch.Root, "render", "workflow", input, output);
        Assert.Equal((0, $"{output}\n", ""), (exit, stdout, stderr));
        Assert.NotNull(new FileInfo(output).LinkTarget);
        Assert.StartsWith("<!DOCTYPE html>", File.ReadAllText(target), StringComparison.OrdinalIgnoreCase);
    }
}
