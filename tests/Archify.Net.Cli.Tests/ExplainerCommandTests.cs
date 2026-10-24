using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Archify.Net.Cli.Delivery;
using Xunit;

namespace Archify.Net.Cli.Tests;

// PORT-009 Task 7: validate, deliver, render, check, patch and doctor on explainer documents against the Node-minted `explainer`
// group (tools/capture-cli-fixtures.mjs): stdout, stderr and exit, the workdir's entries, every delivery sidecar, and the page a
// command writes, all under the capture's masks.
public partial class ExplainerCommandTests
{
    // Compared by their own facts below, not with Node's output.
    static readonly HashSet<string> KnownDeviations =
    [
        "explainer/render-front-matter",      // render-uncaught: Node prints a raw stack
        "explainer/patch-native-page-json",   // patch-payload-message (Task 6): upstream reports the gzip message
        "explainer/patch-json",               // patch-binding (UPSTREAM-016, #1085): upstream's page embeds the temp draft
        "explainer/patch-human",              // patch-binding
    ];

    sealed record Fixture(CliFixture Cli, List<string> Entries, Dictionary<string, string> Sidecars, string? Page);

    // The page compared is the fixed oracle's where the capture wrote one (Decisions 2 and 3), otherwise Node's.
    static Fixture Load(string name)
    {
        var json = DiagramLoader.ParseNode(CliTestHost.ReadLf(Path.Combine(CliTestHost.FixtureRoot, name.Replace('/', Path.DirectorySeparatorChar) + ".json")))!;
        return new(CliTestHost.Fixture(name), [.. json["entries"]!.AsArray().Select(e => e!.GetValue<string>())],
            json["sidecars"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()),
            (json["pageFixed"] ?? json["page"])?.GetValue<string>());
    }

    // doctor is Q3's own report; its explainer lines are compared by Doctor_reports_the_explainer_lines.
    public static TheoryData<string> Fixtures() =>
        [.. CliTestHost.FixtureNames("explainer").Where(n => !KnownDeviations.Contains(n) && n != "explainer/doctor")];

    [Theory, MemberData(nameof(Fixtures))]
    public void Explainer_commands_match_node(string name)
    {
        var fixture = Load(name);
        var (exit, stdout, stderr, entries, sidecars, page) = Run(fixture);
        Assert.Equal(UsageEdit.Apply(fixture.Cli.Stdout), stdout);
        Assert.Equal(UsageEdit.Apply(fixture.Cli.Stderr), stderr);
        Assert.Equal(fixture.Cli.Exit, exit);
        Assert.Equal(fixture.Entries, entries);
        Assert.Equal(fixture.Sidecars, sidecars);
        Assert.Equal(fixture.Page, page);
    }

    // UPSTREAM-DEVIATION render-uncaught (Review Focus 1): render explainer has no diagnostic boundary for an error without
    // diagnostics, so Node prints the PeleError's raw stack. The port prints its message; exit 1 and stdout empty either way.
    [Fact]
    public void Render_front_matter_error_is_the_stated_deviation()
    {
        var fixture = Load("explainer/render-front-matter");
        var (exit, stdout, stderr, entries, _, _) = Run(fixture);
        var nodeMessage = fixture.Cli.Stderr.Split("\r\n").Single(line => line.Contains("[PeleError]: ", StringComparison.Ordinal));
        Assert.Equal((1, ""), (exit, stdout));
        Assert.Equal(fixture.Entries, entries);
        Assert.Equal(nodeMessage[(nodeMessage.IndexOf("[PeleError]: ", StringComparison.Ordinal) + "[PeleError]: ".Length)..] + "\n", stderr);
        Assert.Equal("Front matter is not valid YAML. YAML: unexpected end of the stream within a flow collection\n", stderr);
    }

    // UPSTREAM-DEVIATION patch-payload-message (Task 6): patching a native page. Upstream's patch.mjs:26 tests `error.diagnostics`,
    // so embedded()'s own failure becomes "Embedded native diagram payload cannot be inflated."; the port keeps embedded()'s
    // diagnostic. Everything else in the receipt is Node's.
    [Fact]
    public void Patch_of_a_native_page_keeps_the_ports_payload_message()
    {
        var fixture = Load("explainer/patch-native-page-json");
        var (exit, stdout, stderr, entries, sidecars, _) = Run(fixture);
        Assert.Equal((fixture.Cli.Exit, fixture.Cli.Stderr), (exit, stderr));
        Assert.Equal(fixture.Entries, entries);
        Assert.Equal(fixture.Sidecars, sidecars);
        var node = JsonNode.Parse(fixture.Cli.Stdout)!;
        Assert.Equal("Embedded native diagram payload cannot be inflated.", node["error"]!.GetValue<string>());
        var port = JsonNode.Parse(stdout)!;
        const string message = "The artifact must contain one original draft and prepared resource payload.";
        Assert.Equal(message, port["error"]!.GetValue<string>());
        var diagnostic = Assert.Single(port["diagnostics"]!.AsArray())!;
        Assert.Equal(("explainer/patch-payload", message, "Use an artifact produced by the formal explainer CLI."),
            (diagnostic["code"]!.GetValue<string>(), diagnostic["message"]!.GetValue<string>(), diagnostic["evidence"]!["example"]!.GetValue<string>()));
        // The receipt around the diagnostic is Node's.
        foreach (var key in new[] { "error", "diagnostics" }) { node.AsObject().Remove(key); port.AsObject().Remove(key); }
        Assert.Equal(Js.Stringify(node), Js.Stringify(port));
    }

    // UPSTREAM-DEVIATION patch-binding (UPSTREAM-016, tt-a1i/archify#1085): commandPatch rebinds the bundle to its temporary
    // patched-draft.json (A:5333-5334) so the in-process deliver accepts it, and upstream's page then carries that `inputPath` and a
    // digest over it. The port's patched page is bound as a delivered page is: `sourcePath` only.
    static readonly Dictionary<string, string> PatchFiles = new()
    {
        ["author.json"] = "tests/fixtures/cli/inputs/explainer/patch-author.json",
        ["replacement.json"] = "tests/fixtures/cli/inputs/explainer/patch-replacement.json",
    };

    static JsonArray Steps(params string[][] runs) => [.. runs.Select(run => (JsonNode)new JsonObject { ["run"] = new JsonArray([.. run.Select(a => (JsonNode)a)]), ["exit"] = 0.0 })];

    static readonly string[] DeliverPage = ["deliver", "explainer", "author.json", "page.html", "--json"];

    // Node's patched rows: everything but the page is Node's; the page is Node's without its `inputPath`, the bundle digest (which
    // follows the binding, masked in Node's golden) compared by Patched_page_passes_check_and_patches_again instead.
    [Theory, InlineData("explainer/patch-json"), InlineData("explainer/patch-human")]
    public void Patched_rows_match_node_but_the_temp_binding(string name)
    {
        var fixture = Load(name);
        var (exit, stdout, stderr, entries, sidecars, page) = Run(fixture);
        Assert.Equal((fixture.Cli.Stdout, fixture.Cli.Stderr, fixture.Cli.Exit), (stdout, stderr, exit));
        Assert.Equal(fixture.Entries, entries);
        Assert.Equal(fixture.Sidecars, sidecars);
        const string binding = "\"digest\":\"<digest>\",\"inputPath\":\"<TMP>\\\\patched-draft.json\"}</script>";
        Assert.Equal(2, fixture.Page!.Split(binding).Length);
        var digest = new Regex("\"digest\":\"[0-9a-f]{64}\"}</script>");
        Assert.Single(digest.Matches(page!));
        Assert.Equal(fixture.Page.Replace(binding, "\"digest\":\"<digest>\"}</script>"), digest.Replace(page!, "\"digest\":\"<digest>\"}</script>"));
    }

    static string[] Patch(string from, string to) => ["patch", "explainer", from, "first", "replacement.json", to, "--json"];

    // The page's prepared bundle (packed, as it is embedded).
    static JsonObject PreparedOf(string html) => JsonNode.Parse(ExplainerPipeline.ScanIds(html).Prepared!)!.AsObject();

    [Fact]
    public void Patched_page_does_not_embed_the_temp_draft()
    {
        string original = "", patched = "";
        var (exit, _, _) = CliTestHost.Run(Patch("page.html", "out.html"), PatchFiles, setup: Steps(DeliverPage), inspect: work =>
        {
            original = File.ReadAllText(Path.Join(work, "page.html"));
            patched = File.ReadAllText(Path.Join(work, "out.html"));
        });
        Assert.Equal(0, exit);
        Assert.DoesNotContain("\"inputPath\"", patched, StringComparison.Ordinal);
        Assert.DoesNotContain("archify-explainer-patch-", patched, StringComparison.Ordinal);
        Assert.DoesNotContain(Temp, patched, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Temp.Replace("\\", "\\\\"), patched, StringComparison.OrdinalIgnoreCase);
        var (before, after) = (PreparedOf(original), PreparedOf(patched));
        Assert.Equal(before.Select(kv => kv.Key), after.Select(kv => kv.Key));
        Assert.Equal(before["sourcePath"]!.GetValue<string>(), after["sourcePath"]!.GetValue<string>());
    }

    [Fact]
    public void Patching_twice_gives_identical_pages()
    {
        byte[] first = [], second = [];
        var (exit, _, _) = CliTestHost.Run(Patch("page.html", "second.html"), PatchFiles, setup: Steps(DeliverPage, Patch("page.html", "first.html")), inspect: work =>
        {
            first = File.ReadAllBytes(Path.Join(work, "first.html"));
            second = File.ReadAllBytes(Path.Join(work, "second.html"));
        });
        Assert.Equal(0, exit);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Patched_page_passes_check_and_patches_again()
    {
        var checks = new List<(int Exit, string Output)>();
        var (exit, stdout, _) = CliTestHost.Run(Patch("out.html", "again.html"), PatchFiles, setup: Steps(DeliverPage, Patch("page.html", "out.html")), inspect: work =>
        {
            foreach (var args in new[] { new[] { "check", "out.html", "--json", "--require-provenance" }, ["check", "again.html", "--json", "--require-provenance"] })
            {
                var output = new StringWriter();
                checks.Add((Cli.Run(args, new CliIo(output, output, work, CliServices.Default)), output.ToString()));
            }
            Assert.DoesNotContain("\"inputPath\"", File.ReadAllText(Path.Join(work, "again.html")), StringComparison.Ordinal);
        });
        Assert.True(exit == 0, stdout);
        Assert.All(checks, check => Assert.True(check.Exit == 0, check.Output));
    }

    // doctor: upstream's explainer lines (A:6151, :6168-6170) and the port's Q3 forms of them.
    [Fact]
    public void Doctor_reports_the_explainer_lines()
    {
        var node = Load("explainer/doctor").Cli.Stdout;
        Assert.Contains("[ok] explainer renderer, schema, and example\n[ok] Explainer runtime and reader assets\n", node);
        // git and Chromium injected as present (the real resource lookup stays), so the fact holds on a machine without either
        var (exit, stdout, _) = CliTestHost.Run(["doctor"], services: CliServices.Default with { GitOnPath = () => true, HeadlessShell = () => "x" });
        Assert.Equal(0, exit);
        Assert.Contains("[ok] explainer schema and example\n[ok] Explainer runtime and reader assets\n", stdout);
    }

    // assertPreparedDependencies before the commit (A:5120-5123): a frozen file that changes after the check fails the commit, and
    // nothing is published.
    [Fact]
    public void A_dependency_changed_before_the_commit_is_refused()
    {
        var work = Directory.CreateTempSubdirectory("archify-explainer-").FullName;
        try
        {
            File.Copy(Path.Join(CliTestHost.RepoRoot, "tests", "fixtures", "cli", "inputs", "explainer", "image-draft.json"), Path.Join(work, "draft.json"));
            var asset = Path.Join(work, "asset.png");
            File.Copy(Path.Join(CliTestHost.RepoRoot, "tests", "fixtures", "cli", "inputs", "explainer", "asset.png"), asset);
            var store = new DeliveryStore
            {
                Check = (bytes, file) =>
                {
                    File.AppendAllText(asset, "mutation");
                    return Cli.CheckArtifact(bytes, file);
                },
            };
            var stdout = new StringWriter();
            var exit = Commands.DeliverCommand.Run(["explainer", "draft.json", "result.html", "--json"], new CliIo(stdout, new StringWriter(), work, CliServices.Default), store);
            var receipt = JsonNode.Parse(stdout.ToString())!;
            Assert.Equal((1, "commit", "explainer/dependency-changed"),
                (exit, receipt["stage"]!.GetValue<string>(), receipt["diagnostics"]![0]!["code"]!.GetValue<string>()));
            Assert.StartsWith($"Could not commit verified delivery \"{Path.Join(work, "result.html")}\": ", receipt["error"]!.GetValue<string>());
            Assert.False(File.Exists(Path.Join(work, "result.html")));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // One fixture run in a fresh workdir: the tokenised, masked output, the entries, the masked sidecars and the portable page.
    static (int Exit, string Stdout, string Stderr, List<string> Entries, Dictionary<string, string> Sidecars, string? Page) Run(Fixture fixture)
    {
        List<string> entries = [];
        Dictionary<string, string> sidecars = [];
        string? page = null;
        var pageFile = fixture.Page is null ? null : PageFile(fixture.Cli.Args);
        var (exit, stdout, stderr) = CliTestHost.Run(fixture.Cli.Args, fixture.Cli.Files, setup: fixture.Cli.Setup, inspect: work =>
        {
            entries = [.. Directory.GetFileSystemEntries(work, "*", SearchOption.AllDirectories).Select(e => Path.GetRelativePath(work, e)).Order(StringComparer.Ordinal)];
            sidecars = entries.Where(e => IsSidecar(Path.GetFileName(e))).ToDictionary(e => e, e => Mask(CliTestHost.Tokenise(CliTestHost.ReadLf(Path.Join(work, e)), work)));
            if (pageFile is not null) page = PortablePage(CliTestHost.Tokenise(CliTestHost.ReadLf(Path.Join(work, pageFile)), work));
        });
        return (exit, Mask(stdout), Mask(stderr), entries, sidecars, page);
    }

    // The page a command writes: render's and deliver's output argument, patch's last positional.
    static string PageFile(string[] args) => args[0] == "patch" ? args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).Last() : args[3];

    static bool IsSidecar(string name) =>
        name.EndsWith(".delivery.json", StringComparison.Ordinal) || name.EndsWith(".delivery-pending.json", StringComparison.Ordinal)
        || name == ".archify-delivery-lock.json";

    // The capture's masks: v4 ids, `archify-explainer-*` temp directories (raw and JSON-escaped), the human sha256 prefix, and in a
    // JSON text every artifact identity, explainer.preparedSha256 and the dropped `update`, re-serialised in its own layout.
    static readonly string Temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
    static readonly Regex[] TempDirectories =
    [
        new(Regex.Escape(Temp.Replace("\\", "\\\\")) + @"\\\\archify-explainer-[a-z]+-[A-Za-z0-9._]+"),
        new(Regex.Escape(Temp) + @"\\archify-explainer-[a-z]+-[A-Za-z0-9._]+"),
    ];

    internal static string Mask(string text)
    {
        var masked = UuidV4().Replace(text, "<uuid>");
        foreach (var directory in TempDirectories) masked = directory.Replace(masked, "<TMP>");
        masked = HumanSha256().Replace(masked, "; sha256 <artifact>");
        if (!masked.StartsWith('{')) return masked;
        var value = MaskValue(DiagramLoader.ParseNode(masked))!.AsObject();
        value.Remove("update");
        return Js.Stringify(value, masked.Contains("\n  ", StringComparison.Ordinal) ? 2 : 0) + "\n";
    }

    static JsonNode? MaskValue(JsonNode? value) => value switch
    {
        JsonArray array => new JsonArray([.. array.Select(MaskValue)]),
        JsonObject obj => new JsonObject(obj.Select(kv => KeyValuePair.Create(kv.Key,
            kv.Key == "preparedSha256" && kv.Value is JsonValue v && v.TryGetValue<string>(out _) ? JsonValue.Create("<sha256>")
            : kv.Key == "artifact" && kv.Value.Str("sha256") is not null && kv.Value.Num("bytes") is not null ? JsonValue.Create("<artifact>")
            : MaskValue(kv.Value)))),
        _ => value?.DeepClone(),
    };

    // NormalizePage (Decision 1). No path is masked: the port's patched page carries none (UPSTREAM-016).
    static string PortablePage(string html) => PackedChild().Replace(html, m =>
    {
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(m.Groups[1].Value)), CompressionMode.Decompress);
        using var inflated = new MemoryStream();
        gzip.CopyTo(inflated);
        return $"\"htmlGzip\":\"gz-sha256:{Convert.ToHexStringLower(SHA256.HashData(inflated.ToArray()))}\"";
    });

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex UuidV4();

    [GeneratedRegex(@"; sha256 [0-9a-f]{12}$", RegexOptions.Multiline)]
    private static partial Regex HumanSha256();

    [GeneratedRegex("\"htmlGzip\":\"([A-Za-z0-9+/=]*)\"")]
    private static partial Regex PackedChild();
}
