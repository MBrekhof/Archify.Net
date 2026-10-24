using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;
namespace Archify.Net.Tests;

// repository-evidence.mjs against the fast-import fixture repo. Every scenario's payload or receipt was minted by Node against the
// same stream (tools/capture-fixtures.mjs); the C#-only facts below cover what Node's own tests check by behaviour, not by value.
public class RepositoryEvidenceTests
{
    static readonly JsonArray Scenarios = JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture("evidence/scenarios.json")))!.AsArray();
    const string GithubOrigin = "git@github.com:example/evidence-repo.git";

    static JsonNode Scenario(string name) => Scenarios.Single(s => s!["name"]!.GetValue<string>() == name)!;
    static JsonObject Diagram(string name) => DiagramLoader.Normalize(Scenario(name)["diagram"]!.DeepClone().AsObject());
    static JsonNode Expected(string file) => JsonNode.Parse(Fixtures.Lf(Fixtures.Fixture($"evidence/{file}")))!;
    static string Payload(string name) => Js.Stringify(Expected($"{name}.payload.json"));
    static IReadOnlyList<Diagnostic> Failure(Action verify, string root)
    {
        var error = Assert.Throws<ArchifyDiagnosticException>(verify);
        return [.. error.Diagnostics.Select(d => EvidenceRepo.Tokenise(d, root))];
    }

    public static IEnumerable<object[]> ScenarioNames() => Scenarios.Select(s => new object[] { s!["name"]!.GetValue<string>() });

    [Fact] public void Fixture_stream_builds_the_pinned_head_in_two_directories()
    {
        var (a, headA) = EvidenceRepo.Create(null);
        var (b, headB) = EvidenceRepo.Create(null);
        try
        {
            Assert.NotEqual(a, b);
            Assert.Equal(EvidenceRepo.PinnedHead, headA);
            Assert.Equal(EvidenceRepo.PinnedHead, headB);
        }
        finally { EvidenceRepo.Delete(a); EvidenceRepo.Delete(b); }
    }

    [Theory, MemberData(nameof(ScenarioNames))]
    public void Verify_matches_upstream(string name)
    {
        var scenario = Scenario(name);
        var (repo, _) = EvidenceRepo.Create(scenario.Str("origin"));
        string? empty = null, partial = null;
        try
        {
            var kind = scenario.Str("root")!;
            if (kind == "empty") empty = Directory.CreateTempSubdirectory("archify-evidence-").FullName;
            if (kind == "partial") partial = EvidenceRepo.MakePartialClone(repo, scenario.Str("origin")!);
            if (kind == "bad-type") EvidenceRepo.MakeBadType(repo);
            if (kind == "truncated") EvidenceRepo.MakeTruncated(repo);
            var root = kind switch
            {
                "repo" or "bad-type" or "truncated" => repo, "src" => Directory.CreateDirectory(Path.Combine(repo, "src")).FullName, "empty" => empty,
                "partial" => partial, "missing" => repo + "-missing", "" => "", _ => throw new ArgumentException(kind),
            };
            var type = DiagramTypes.Parse(scenario.Str("type")!);
            var payloadFile = Fixtures.Fixture($"evidence/{name}.payload.json");
            if (File.Exists(payloadFile))
                Assert.Equal(Payload(name), Js.Stringify(RepositoryEvidence.Verify(type, Diagram(name), root, new DiagnosticSink())));
            else
                ParityTests.AssertReceipt(Expected($"{name}.expected.json"),
                    Failure(() => RepositoryEvidence.Verify(type, Diagram(name), root, new DiagnosticSink()), partial ?? empty ?? repo));
        }
        finally { EvidenceRepo.Delete(repo); if (empty is not null) EvidenceRepo.Delete(empty); if (partial is not null) EvidenceRepo.Delete(partial); }
    }

    // The damage itself took (otherwise the scenarios above would pass for the wrong reason).
    [Fact] public void Partial_clone_holds_no_blob()
    {
        var (repo, _) = EvidenceRepo.Create(GithubOrigin);
        string? clone = null;
        try
        {
            clone = EvidenceRepo.MakePartialClone(repo, GithubOrigin);
            Assert.DoesNotContain(" blob ", EvidenceRepo.Git(clone, "cat-file", "--batch-all-objects", "--batch-check"));
        }
        finally { EvidenceRepo.Delete(repo); if (clone is not null) EvidenceRepo.Delete(clone); }
    }

    [Fact] public void Bad_type_blob_is_not_readable_by_cat_file()
    {
        var (repo, _) = EvidenceRepo.Create(GithubOrigin);
        try { EvidenceRepo.MakeBadType(repo); Assert.Throws<InvalidOperationException>(() => EvidenceRepo.Git(repo, "cat-file", "-t", EvidenceRepo.RouterBlobId)); }
        finally { EvidenceRepo.Delete(repo); }
    }

    [Fact] public void Truncated_blob_is_not_readable_by_cat_file()
    {
        var (repo, _) = EvidenceRepo.Create(GithubOrigin);
        try { EvidenceRepo.MakeTruncated(repo); Assert.Throws<InvalidOperationException>(() => EvidenceRepo.Git(repo, "cat-file", "-p", EvidenceRepo.RouterBlobId)); }
        finally { EvidenceRepo.Delete(repo); }
    }

    // Survey-test3 §4c.3 (C#-only: the result is a clean payload, so Node adds only "exit 0"): the content batch overflows maxBuffer,
    // and the per-file show fallback reads all five paths. line: 2 fails if the fallback returns empty content (SourceLineCount("") is 1).
    [Fact] public void A_content_batch_over_maxBuffer_falls_back_to_per_file_show()
    {
        var (repo, _) = EvidenceRepo.Create(GithubOrigin);
        try
        {
            EvidenceRepo.Git(repo, "read-tree", "HEAD");
            var line = Enumerable.Repeat((byte)'a', 79).Append((byte)'\n').ToArray();
            var content = Enumerable.Range(0, 14 * 1024 * 1024 / 80).SelectMany(_ => line).ToArray();
            string[] names = ["a.txt", "b.txt", "c.txt", "d.txt", "e.txt"];
            foreach (var n in names) File.WriteAllBytes(Path.Combine(repo, n), content);
            EvidenceRepo.Git(repo, ["add", .. names]);
            EvidenceRepo.Git(repo, "-c", "user.name=Archify Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "big fixture");
            var head = EvidenceRepo.Git(repo, "rev-parse", "HEAD");

            var diagram = Diagram("github-web");
            diagram["meta"]!["repository"]!["revision"] = head;
            JsonArray Cite(IEnumerable<string> paths) => new([.. paths.Select(p => (JsonNode)new JsonObject { ["path"] = p, ["line"] = 2 })]);
            diagram["components"]![0]!["sources"] = Cite(names[..3]);
            diagram["components"]![1]!["sources"] = Cite(names[3..]);
            var payload = RepositoryEvidence.Verify(DiagramType.Architecture, DiagramLoader.Normalize(diagram), repo, new DiagnosticSink());
            Assert.Equal(5, payload!["referenceCount"]!.GetValue<int>());
        }
        finally { EvidenceRepo.Delete(repo); }
    }

    [Fact] public void A_null_root_is_root_required_like_an_empty_one()
    {
        var error = Assert.Throws<ArchifyDiagnosticException>(() => RepositoryEvidence.Verify(DiagramType.Architecture, Diagram("root-required"), null, new DiagnosticSink()));
        ParityTests.AssertReceipt(Expected("root-required.expected.json"), error.Diagnostics);
    }

    // T/repository-evidence.test.mjs:93-112: an earlier citation's error is kept, the over-limit read stops the loop.
    [Fact] public void A_blob_over_16_MiB_is_ENOBUFS_and_stops_the_loop()
    {
        var (repo, _) = EvidenceRepo.Create(GithubOrigin);
        try
        {
            EvidenceRepo.Git(repo, "read-tree", "HEAD");
            File.WriteAllBytes(Path.Combine(repo, "large.txt"), Enumerable.Repeat((byte)'x', 17 * 1024 * 1024).ToArray());
            File.WriteAllBytes(Path.Combine(repo, "exact.txt"), Enumerable.Repeat((byte)'x', 16 * 1024 * 1024).ToArray());
            EvidenceRepo.Git(repo, "add", "large.txt", "exact.txt");
            EvidenceRepo.Git(repo, "-c", "user.name=Archify Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "large source");
            var head = EvidenceRepo.Git(repo, "rev-parse", "HEAD");

            var diagram = Diagram("github-web");
            diagram["meta"]!["repository"]!["revision"] = head;
            diagram["components"]![0]!["sources"] = JsonNode.Parse("""[{"path":"src/router.js","line":99},{"path":"large.txt","line":1},{"path":"src/store.js","line":99}]""");
            var diagnostics = Failure(() => RepositoryEvidence.Verify(DiagramType.Architecture, DiagramLoader.Normalize(diagram), repo, new DiagnosticSink()), repo);
            Assert.Equal(["repository-evidence/line-out-of-range", "repository-evidence/git-unavailable"], diagnostics.Select(d => d.Code));
            Assert.Equal("Could not run Git: spawnSync git ENOBUFS", diagnostics[1].Message);
            Assert.Equal("""{"reason":"spawnSync git ENOBUFS"}""", Js.Stringify(diagnostics[1].Evidence));

            // maxBuffer trips only when strictly exceeded: exactly 16 MiB of output passes, one byte less of limit does not.
            var git = new GitRunner();
            var exact = git.Run(repo, ["show", $"{head}:exact.txt"], 16 * 1024 * 1024);
            Assert.Null(exact.Error);
            Assert.Equal(16 * 1024 * 1024, exact.Stdout.Length);
            Assert.Equal("spawnSync git ENOBUFS", git.Run(repo, ["show", $"{head}:exact.txt"], 16 * 1024 * 1024 - 1).Error);
        }
        finally { EvidenceRepo.Delete(repo); }
    }

    // PERF-001: the stdin writer and stderr drain block on pipes, so they run on dedicated threads, not the pool.
    [Fact] public void Pipe_helpers_run_off_the_thread_pool()
    {
        var (repo, _) = EvidenceRepo.Create(GithubOrigin);
        var onPool = new System.Collections.Concurrent.ConcurrentBag<bool>();
        try
        {
            GitRunner.OnHelperThread = onPool.Add;
            Assert.Null(new GitRunner().Run(repo, ["rev-parse", "HEAD"], 1024).Error);
            Assert.True(onPool.Count >= 2);   // other tests may add their own helpers; every one must be off the pool
            Assert.DoesNotContain(true, onPool);
        }
        finally { GitRunner.OnHelperThread = null; EvidenceRepo.Delete(repo); }
    }

    // T/repository-evidence-replacement.test.mjs: --no-replace-objects reads the pinned commit, not its replacement.
    [Fact] public void A_replace_ref_on_the_commit_does_not_change_the_verdict()
    {
        var (repo, head) = EvidenceRepo.Create(GithubOrigin);
        try
        {
            var emptyTree = EvidenceRepo.Git(repo, Array.Empty<byte>(), "hash-object", "-t", "tree", "-w", "--stdin");
            var replacement = EvidenceRepo.Git(repo, "-c", "user.name=x", "-c", "user.email=x@example.invalid", "commit-tree", emptyTree, "-m", "replaced");
            EvidenceRepo.Git(repo, "replace", head, replacement);
            Assert.Equal("", EvidenceRepo.Git(repo, "ls-tree", head));   // the replacement is live for ordinary reads
            Assert.Equal(Payload("github-web"), Js.Stringify(RepositoryEvidence.Verify(DiagramType.Architecture, Diagram("github-web"), repo, new DiagnosticSink())));
        }
        finally { EvidenceRepo.Delete(repo); }
    }

    // Evidence reads objects only (T/repository-evidence-replacement.test.mjs:126-136).
    [Fact] public void A_dirty_work_tree_does_not_change_the_verdict()
    {
        var (repo, _) = EvidenceRepo.Create(GithubOrigin);
        try
        {
            EvidenceRepo.Git(repo, "reset", "--hard", "--quiet");
            File.WriteAllText(Path.Combine(repo, "src", "router.js"), "x\n");
            File.Delete(Path.Combine(repo, "src", "store.js"));
            Assert.NotEqual("", EvidenceRepo.Git(repo, "status", "--porcelain"));
            Assert.Equal(Payload("github-web"), Js.Stringify(RepositoryEvidence.Verify(DiagramType.Architecture, Diagram("github-web"), repo, new DiagnosticSink())));
        }
        finally { EvidenceRepo.Delete(repo); }
    }

    // T/repository-evidence.test.mjs:80-91: path-only sources verify types without reading blob contents.
    [Fact] public void Path_only_sources_never_run_cat_file_batch_or_show()
    {
        var (repo, _) = EvidenceRepo.Create(GithubOrigin);
        try
        {
            var trace = Path.Combine(repo, "git.trace");
            var git = new GitRunner();
            git.Environment["GIT_TRACE"] = trace;
            var diagram = Diagram("github-web");
            diagram["components"]![0]!["sources"] = JsonNode.Parse("""[{"path":"src/router.js"},{"path":"src/store.js"}]""");
            Assert.NotNull(RepositoryEvidence.Verify(DiagramType.Architecture, diagram, repo, new DiagnosticSink(), git));
            var log = File.ReadAllText(trace);
            Assert.Contains("cat-file --batch-check", log);
            Assert.DoesNotMatch(new Regex(@"cat-file --batch(?:\s|\z)|\bshow\s"), log);
        }
        finally { EvidenceRepo.Delete(repo); }
    }

    // A PATH without git: Node's spawnSync error text, through the load gates.
    [Fact] public void Git_absent_reports_git_unavailable_ENOENT()
    {
        var (repo, _) = EvidenceRepo.Create(GithubOrigin);
        try
        {
            var ctx = new RenderContext(DiagramType.Architecture, Diagram("github-web"), new RenderOptions(RepoRoot: repo)) { Git = MissingGit() };
            var error = Assert.Throws<ArchifyDiagnosticException>(() => RepositoryEvidence.Verify(ctx.Type, ctx.Diagram, repo, ctx.Diagnostics, ctx.Git));
            ParityTests.AssertReceipt(Expected("git-absent.expected.json"), [.. error.Diagnostics.Select(d => EvidenceRepo.Tokenise(d, repo))]);
        }
        finally { EvidenceRepo.Delete(repo); }
    }

    static GitRunner MissingGit() => new() { Executable = Path.Combine(Path.GetTempPath(), "archify-no-git", OperatingSystem.IsWindows() ? "git.exe" : "git") };

    // Answers the named commands (args joined by spaces) itself; every other command runs real git.
    sealed class ScriptedGit : GitRunner
    {
        readonly Dictionary<string, string> answers = new(StringComparer.Ordinal);
        public string this[string args] { set => answers[args] = value; }
        public override Result Run(string root, IReadOnlyList<string> args, int maxBuffer, string? input = null) =>
            answers.TryGetValue(string.Join(' ', args), out var text) ? new(null, 0, Encoding.UTF8.GetBytes(text)) : base.Run(root, args, maxBuffer, input);
    }

    // 3.2.0 dc7a5f28: gitValue drops Git's one record terminator (`replace(/\n$/, '')`), so the top-level path keeps its whitespace.
    // Upstream's test renames a directory to end in a space (POSIX only); the canned answer stands for that directory here.
    [Theory]
    [InlineData("C:/repo-with-space \n", "C:/repo-with-space ")]
    [InlineData("C:/repo\r\n", "C:/repo\r")]   // only \n is the terminator
    [InlineData("C:/repo", "C:/repo")]
    [InlineData("C:/repo\n\n", "C:/repo\n")]
    [InlineData("\n", "")]
    [InlineData("", "")]
    public void Top_level_path_loses_only_one_trailing_newline(string answer, string gitTopLevel)
    {
        var (repo, _) = EvidenceRepo.Create(GithubOrigin);
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(repo, "src")).FullName;   // a real show-prefix "src/" marks it not top level
            var git = new ScriptedGit { ["rev-parse --show-toplevel"] = answer };
            var d = Assert.Single(Failure(() => RepositoryEvidence.Verify(DiagramType.Architecture, Diagram("subdirectory-root"), root, new DiagnosticSink(), git), repo));
            Assert.Equal("repository-evidence/root-not-top-level", d.Code);
            Assert.Equal(gitTopLevel, d.Evidence["gitTopLevel"]!.GetValue<string>());   // the value that keeps its whitespace
            // normalizedDiagnostic trims the message and each fix (diagnostics.mjs:17,25), upstream as here.
            Assert.Equal(Js.Trim($"Evidence root must be the Git top-level directory: {gitTopLevel}"), d.Message);
            Assert.Equal([Js.Trim($"pass --repo-root {gitTopLevel}")], d.SupportedFixes);
        }
        finally { EvidenceRepo.Delete(repo); }
    }

    // `gitValue(...).trim()`: the origin alone stays fully trimmed (3.2.0 repository-evidence.mjs:232).
    [Theory]
    [InlineData(" https://github.com/example/x.git \n")]
    [InlineData("\thttps://github.com/example/x.git\r\n")]
    public void Origin_is_fully_trimmed(string answer)
    {
        var (repo, _) = EvidenceRepo.Create(GithubOrigin);
        try
        {
            var git = new ScriptedGit { ["remote get-url origin"] = answer };
            var d = Assert.Single(Failure(() => RepositoryEvidence.Verify(DiagramType.Architecture, Diagram("github-web"), repo, new DiagnosticSink(), git), repo));
            Assert.Equal("repository-evidence/origin-mismatch", d.Code);
            Assert.Equal("https://github.com/example/x.git", d.Evidence["localOrigin"]!.GetValue<string>());
        }
        finally { EvidenceRepo.Delete(repo); }
    }

    [Fact] public void A_padded_matching_origin_verifies()
    {
        var (repo, _) = EvidenceRepo.Create(GithubOrigin);
        try
        {
            var git = new ScriptedGit { ["remote get-url origin"] = $" {GithubOrigin} \r\n" };
            Assert.Equal(Payload("github-web"), Js.Stringify(RepositoryEvidence.Verify(DiagramType.Architecture, Diagram("github-web"), repo, new DiagnosticSink(), git)));
        }
        finally { EvidenceRepo.Delete(repo); }
    }

    // P4: a diagram without meta.repository or sources returns null before any git process; the runner here fails on first use.
    [Fact] public void Load_spawns_no_git_for_a_diagram_without_evidence()
    {
        var examples = Directory.EnumerateFiles(Fixtures.Upstream("examples"), "*.json").Where(f => !f.EndsWith(".explainer.json")).ToList();   // native types only
        Assert.NotEmpty(examples);
        foreach (var file in examples)
        {
            var parts = Path.GetFileName(file).Split('.');
            var type = DiagramTypes.Parse(parts[^2]);
            var diagram = DiagramLoader.Normalize(DiagramLoader.Parse(Fixtures.Lf(file)));
            var ctx = new RenderContext(type, diagram, new RenderOptions(RepoRoot: Path.GetTempPath())) { Git = MissingGit() };
            DiagramLoader.Load(type, diagram, ctx);
            Assert.Null(ctx.SourceEvidence);
        }
        // The same runner does fail once evidence is declared: the seam is live.
        var withEvidence = DiagramLoader.Normalize(DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/web-app.architecture.json"))));
        withEvidence["meta"]!["repository"] = Diagram("github-web")["meta"]!["repository"]!.DeepClone();
        withEvidence["components"]![0]!["sources"] = Diagram("github-web")["components"]![0]!["sources"]!.DeepClone();
        var failing = new RenderContext(DiagramType.Architecture, withEvidence, new RenderOptions(RepoRoot: Path.GetTempPath())) { Git = MissingGit() };
        var error = Assert.Throws<ArchifyDiagnosticException>(() => DiagramLoader.Load(DiagramType.Architecture, withEvidence, failing));
        Assert.Equal("repository-evidence/git-unavailable", Assert.Single(error.Diagnostics).Code);
    }

    // End to end: Render verifies through the load gates and embeds the payload (the badge goldens are a later task).
    [Fact] public void Render_embeds_the_verified_payload()
    {
        var (repo, _) = EvidenceRepo.Create(GithubOrigin);
        try
        {
            var diagram = DiagramLoader.Parse(Fixtures.Lf(Fixtures.Upstream("examples/web-app.architecture.json")));
            diagram["meta"]!["repository"] = Diagram("github-web")["meta"]!["repository"]!.DeepClone();
            diagram["components"]![0]!["sources"] = Diagram("github-web")["components"]![0]!["sources"]!.DeepClone();
            var result = ArchifyNet.Render(DiagramType.Architecture, diagram, new RenderOptions(RepoRoot: repo));
            Assert.True(result.Html.Length > 0, string.Join("\n", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
            Assert.Contains($"<script id=\"archify-source-evidence-data\" type=\"application/json\">{Payload("github-web")}</script>", result.Html);
        }
        finally { EvidenceRepo.Delete(repo); }
    }
}
