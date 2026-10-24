using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;
namespace Archify.Net.Tests;

// PORT-009: the explainer port against goldens Node mints through upstream archify 3.2.0's explainer modules
// (tools/explainer-seeds/run.mjs, run by tools/capture-fixtures.mjs). Seeds, goldens and tokens: tools/explainer-seeds/README.md.
public partial class ExplainerParityTests
{
    // A group is compared once it is listed; each task adds its own.
    static readonly string[] Groups = ["parse", "shorthand", "inline", "diff", "writing", "validate"];
    // Rows that differ from Node by a stated deviation (docs/deviations.md), each pinned by its own fact below.
    static readonly HashSet<(string Group, string Name)> KnownDeviations =
        [("parse", "cr-only"), ("parse", "line-separator-heading"), ("writing", "rule-fold-long-s")];

    public static TheoryData<string, string> Targets()
    {
        var data = new TheoryData<string, string>();
        foreach (var group in Groups)
            foreach (var seed in SeedsOf(group))
                if (!KnownDeviations.Contains((group, seed!["name"]!.GetValue<string>()))) data.Add(group, seed["name"]!.GetValue<string>());
        return data;
    }

    [Theory, MemberData(nameof(Targets))]
    public void Matches_node(string group, string name) => Compare(group, name, Expected(group, name));

    static void Compare(string group, string name, string expected)
    {
        var seed = Seed(group, name);
        var dir = Directory.CreateTempSubdirectory("axs-").FullName;
        var repo = seed["repo"]?.GetValue<bool>() == true ? EvidenceRepo.Create(null).Root : null;
        try
        {
            WriteFiles(dir, seed["files"] as JsonObject);
            var actual = Tokenise(NormalizePage(Run(seed, dir, repo)), dir, repo);
            if (expected != actual) Assert.Fail(ParityTests.FirstDifference(expected, actual));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            if (repo is not null) EvidenceRepo.Delete(repo);
        }
    }

    // The port of seed["call"], compared after NormalizePage as the golden is: a page as is, any other result through Json, a
    // thrown ArchifyDiagnosticException through Thrown. Each task adds the arms of the calls it ports.
    static string Run(JsonObject seed, string dir, string? repo)
    {
        var args = (JsonArray)Substitute(seed["args"] ?? new JsonArray(), dir, repo)!;
        JsonNode? Arg(int i) => i < args.Count ? args[i] : null;
        try
        {
            switch (seed["call"]!.GetValue<string>())
            {
                case "parseExplainer":
                    return Json(ExplainerParser.Parse(Arg(0)!.GetValue<string>(), Arg(1).Str("inputPath"), Arg(1).Str("output")).ToJson());
                case "compileExplainerDiagram":
                    var options = Arg(2);
                    return Json(ExplainerShorthand.Compile(Arg(0)?.GetValue<string>(),
                        [.. Arg(1)!.AsArray().Select(row => new ShorthandRow([.. row!["cells"]!.AsArray().Select(c => c!.GetValue<string>())], (int)row["line"]!.D()))],
                        new(options.Str("title"), options.Str("id"), (int?)options.Num("line") ?? 1, options.Str("quality") ?? "showcase", options.Str("locale"))));
                case "parseInline":
                    return Json(ExplainerInline.Parse(Arg(0)!.GetValue<string>(),
                        new(Arg(1)?["line"] is JsonValue line && line.GetValueKind() == System.Text.Json.JsonValueKind.Number ? line.D() : null,
                            Arg(1).Str("component"), Arg(1).Str("panelId"), Arg(1).Str("input"))).ToJson());
                case "renderInline":
                    if (seed["texts"] is JsonArray texts)
                    {   // the runner's `texts` shape: one call per text with one shared context
                        var shared = Context(Arg(0));
                        var html = new JsonArray([.. texts.Select(text => (JsonNode?)ExplainerInline.Render(text!.GetValue<string>(), shared))]);
                        var result = new JsonObject { ["html"] = html };
                        if (shared.Footnotes is not null) result["footnotes"] = shared.Footnotes.DeepClone();
                        result["citations"] = new JsonArray([.. shared.Citations.Entries.Select(kv => (JsonNode?)new JsonArray(kv.Key, kv.Value))]);
                        return Json(result);
                    }
                    return Json(ExplainerInline.Render(Arg(0)!.GetValue<string>(), Context(Arg(1))));
                case "parseExplainerDiff":
                    var diff = Arg(1);
                    return Json(ExplainerDiff.Parse(Arg(0)!.GetValue<string>(),
                        new(diff.Num("line") ?? 1, diff.Str("component"), diff.Str("input"), diff.Str("panelId"), diff.Str("path"))).ToJson());
                case "lintExplainerWriting":
                    var lint = Arg(1);
                    var entries = Arg(0)!.AsArray().Select(e => new WritingEntry(e.Str("text"), e.Num("line"), e.Str("component"), e.Str("panelId"), e.Str("pointer"), e.Str("raw"))).ToList();
                    return Json(new JsonArray([.. ExplainerWriting.Lint(entries, lint.Str("mode") ?? "warn", lint.Str("locale") ?? "auto", lint.Obj("sentenceLimits"))
                        .Select(d => (JsonNode?)d.ToJson())]));
                case "validateExplainer":
                    JsonNode document;
                    JsonObject? sourceMap;
                    JsonNode? validate;
                    if (seed["parsed"] is JsonValue parsed)
                    {   // the runner's `parsed` shape: the draft's document and source map, args[0] adding options
                        var draft = ExplainerParser.Parse(parsed.GetValue<string>(), Arg(0).Str("inputPath"));
                        (document, sourceMap, validate) = (draft.Document!, draft.SourceMap, Arg(0));
                    }
                    else (document, sourceMap, validate) = (Arg(0)!, Arg(1).Obj("sourceMap"), Arg(1));
                    var (ok, diagnostics) = ExplainerValidator.Validate(document, sourceMap, validate.Str("inputPath"), validate.Str("writingMode") ?? "warn");
                    return Json(new JsonObject { ["ok"] = ok, ["diagnostics"] = new JsonArray([.. diagnostics.Select(d => (JsonNode?)d.ToJson())]) });
                case var call: throw new NotSupportedException($"{call} is not ported yet ({dir}, {repo})");
            }
        }
        catch (ArchifyDiagnosticException e) { return Thrown(e); }
        // Upstream's TypeErrors are ArgumentExceptions; a front-matter PeleError crosses validateExplainer on purpose.
        catch (ArgumentException e) { return Json(new JsonObject { ["thrown"] = new JsonObject { ["name"] = "TypeError", ["message"] = e.Message } }); }
        catch (PeleException e) { return Json(new JsonObject { ["thrown"] = new JsonObject { ["name"] = "PeleError", ["message"] = e.Message, ["code"] = e.Code } }); }
    }

    static InlineContext Context(JsonNode? context) => new() { PanelId = context.Str("panelId"), Footnotes = context?["footnotes"] as JsonArray };

    // JSON.stringify(result, null, 2) + "\n" over the capture's JSON-level tokens.
    internal static string Json(JsonNode? result) => Js.Stringify(TokeniseValue(result), 2) + "\n";
    // The capture's thrown row: {"thrown": {name, message, code}}, code being the error's archifyDiagnostics.
    internal static string Thrown(ArchifyDiagnosticException e) => Json(new JsonObject
    {
        ["thrown"] = new JsonObject { ["name"] = "Error", ["message"] = e.Message, ["code"] = new JsonArray([.. e.Diagnostics.Select(d => (JsonNode?)d.ToJson())]) },
    });

    // --- seeds and goldens ---
    static string SeedFile(string group) => Path.Combine(Fixtures.RepoRoot, "tools", "explainer-seeds", $"{group}.json");
    static JsonArray SeedsOf(string group) => (JsonArray)DiagramLoader.ParseNode(Fixtures.Lf(SeedFile(group)))!;
    static JsonObject Seed(string group, string name) => SeedsOf(group).Cast<JsonObject>().Single(seed => seed["name"]!.GetValue<string>() == name);

    // The corrected oracle's golden when the capture wrote one (Decisions 2 and 3), otherwise Node's.
    static string Expected(string group, string name)
    {
        foreach (var file in new[] { $"{name}.fixed.json", $"{name}.json", $"{name}.fixed.html", $"{name}.html" })
        {
            var path = Fixtures.Golden($"explainer/{group}/{file}");
            if (File.Exists(path)) return file.EndsWith(".html") ? NormalizePage(Fixtures.Lf(path)) : Fixtures.Lf(path);
        }
        throw new FileNotFoundException($"no golden for explainer/{group}/{name}");
    }

    // A seed's `files`: {path: {text} | {base64} | {zeros: n}}, written into its directory.
    static void WriteFiles(string dir, JsonObject? files)
    {
        foreach (var (rel, entry) in files ?? new JsonObject())
        {
            var bytes = entry!["text"] is { } text ? new UTF8Encoding(false).GetBytes(text.GetValue<string>())
                : entry["base64"] is { } base64 ? Convert.FromBase64String(base64.GetValue<string>())
                : new byte[entry["zeros"]!.GetValue<int>()];
            var path = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
    }

    // `<DIR>` and `<REPO>` in every string of a seed's arguments.
    internal static JsonNode? Substitute(JsonNode? value, string dir, string? repo) => value switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s.Replace("<DIR>", dir).Replace("<REPO>", repo ?? "<REPO>"),
        JsonArray a => new JsonArray([.. a.Select(x => Substitute(x, dir, repo))]),
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, Substitute(kv.Value, dir, repo)))),
        _ => value?.DeepClone(),
    };

    // --- the capture's tokens (tools/explainer-seeds/run.mjs tokeniseValue and tokeniseText) ---
    [GeneratedRegex("\"htmlGzip\":\"([A-Za-z0-9+/=]*)\"")] private static partial Regex PackedChild();
    [GeneratedRegex("^[A-Za-z0-9+/=]*$")] private static partial Regex Base64();

    static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));   // a lone surrogate encodes as EF BF BD, as Node's does
    static string Inflated(string base64)
    {
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(base64)), CompressionMode.Decompress);
        using var inflated = new MemoryStream();
        gzip.CopyTo(inflated);
        return "gz-sha256:" + Sha256(inflated.ToArray());
    }

    // Each "htmlGzip":"<base64>" becomes "htmlGzip":"gz-sha256:<hex of the inflated bytes>" (Decision 1: the port's gzip stream
    // differs from Node's); a no-op on an already tokenised golden.
    public static string NormalizePage(string html) => PackedChild().Replace(html, m => $"\"htmlGzip\":\"{Inflated(m.Groups[1].Value)}\"");

    // In every `diagrams` map, each native child's `html` becomes "sha256:<hex>" (its `bytes` stays): the native suites pin it.
    public static JsonNode TokeniseChildren(JsonNode result)
    {
        var copy = result.DeepClone();
        Walk(copy, null);
        return copy;
        static void Walk(JsonNode? node, string? key)
        {
            if (node is JsonArray a) foreach (var item in a) Walk(item, null);
            if (node is not JsonObject o) return;
            foreach (var (k, v) in o.ToList()) Walk(v, k);
            if (key != "diagrams") return;
            foreach (var (_, child) in o)
                if (child is JsonObject c && c["html"] is JsonValue h && h.TryGetValue<string>(out var html)) c["html"] = "sha256:" + Sha256(html);
        }
    }

    // The JSON-level tokens: a prepared bundle's digest (asserted first: sha256(Js.Stringify(bundle minus digest))), artifact
    // identities, packed children, then TokeniseChildren.
    internal static JsonNode? TokeniseValue(JsonNode? value) => value is null ? null : TokeniseChildren(Tokens(value, null)!);
    static JsonNode? Tokens(JsonNode? value, string? key)
    {
        switch (value)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                return key == "htmlGzip" && Base64().IsMatch(s) ? Inflated(s) : NormalizePage(s);
            case JsonArray a:
                return new JsonArray([.. a.Select(x => Tokens(x, null))]);
            case JsonObject o:
                if (key is "artifact" or "expectedArtifact" && o["sha256"]?.GetValueKind() == System.Text.Json.JsonValueKind.String
                    && o["bytes"]?.GetValueKind() == System.Text.Json.JsonValueKind.Number) return "<artifact>";
                var bundle = o["kind"] is JsonValue kind && kind.TryGetValue<string>(out var k) && k == "archify-explainer-prepared"
                    && o["digest"] is JsonValue d && d.TryGetValue<string>(out _);
                if (bundle)
                {
                    var unsigned = o.DeepClone().AsObject();
                    unsigned.Remove("digest");
                    Assert.Equal(Sha256(Js.Stringify(unsigned)), o["digest"]!.GetValue<string>());
                }
                var result = new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, Tokens(kv.Value, kv.Key))));
                if (bundle) result["digest"] = "<digest>";
                return result;
            default:
                return value?.DeepClone();
        }
    }

    // The text-level tokens: <DIR> and <REPO> (raw, JSON-escaped and with forward slashes), each archify-explainer-* temp directory
    // as <TMP>; any other temp path left over fails, as in the capture.
    internal static string Tokenise(string text, string dir, string? repo)
    {
        static string[] Spellings(string p) => [.. new[] { p.Replace("\\", "\\\\"), p, p.Replace('\\', '/') }.Distinct()];
        foreach (var (path, token) in new[] { (dir, "<DIR>"), (repo, "<REPO>") })
            if (path is not null) foreach (var s in Spellings(path)) text = text.Replace(s, token);
        var temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        foreach (var s in Spellings(temp)) text = Regex.Replace(text, Regex.Escape(s) + @"(?:\\\\|\\|/)archify-explainer-[\w-]+", "<TMP>");
        foreach (var s in Spellings(temp)) Assert.False(text.Contains(s, StringComparison.Ordinal), $"untokenised temp path in output:\n{text[..Math.Min(text.Length, 2000)]}");
        return text;
    }

    // --- Task 2 facts ---
    // Decision 2: a shorthand workflow's branch lanes read "分支 N" on a zh page only. The zh row equals Node; the en and
    // default rows equal the corrected oracle's goldens, whose Node goldens carry upstream's "分支 1".
    [Fact] public void Branch_lane_label_is_the_stated_deviation()
    {
        Assert.False(File.Exists(Fixtures.Golden("explainer/shorthand/workflow-branch-zh.fixed.json")));
        var zh = Fixtures.Lf(Fixtures.Golden("explainer/shorthand/workflow-branch-zh.json"));
        Assert.Contains("\"label\": \"分支 1\"", zh);
        Compare("shorthand", "workflow-branch-zh", zh);
        foreach (var name in new[] { "workflow-branch-en", "workflow-branch-default" })
        {
            var corrected = Fixtures.Lf(Fixtures.Golden($"explainer/shorthand/{name}.fixed.json"));
            Assert.Contains("\"label\": \"分支 1\"", Fixtures.Lf(Fixtures.Golden($"explainer/shorthand/{name}.json")));
            Assert.Contains("\"label\": \"Branch 1\"", corrected);
            Compare("shorthand", name, corrected);
        }
    }

    // Every .fixed golden is its Node golden with only Decisions 2 and 3 applied (分支 N → Branch N, lang="zh-CN" → lang="en"),
    // the stable IDs and hashes that cover the changed text aside, and its seed never asks for a zh locale.
    [Fact] public void Fixed_goldens_differ_only_by_the_decided_fixes()
    {
        static string Decided(string node) => Regex.Replace(node, "分支 ([0-9]+)", "Branch $1").Replace("lang=\"zh-CN\"", "lang=\"en\"");
        static string Masked(string text) => Regex.Replace(text, "[0-9a-f]{12,}", "<hex>");
        var files = Directory.GetFiles(Fixtures.Golden("explainer"), "*.fixed.*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var seed = Js.Stringify(Seed(Path.GetFileName(Path.GetDirectoryName(file)!), name[..name.IndexOf(".fixed.", StringComparison.Ordinal)]));
            Assert.DoesNotMatch("locale=zh|\"locale\":\"zh", seed);
            var node = Fixtures.Lf(file.Replace(".fixed.", "."));
            Assert.NotEqual(node, Fixtures.Lf(file));
            Assert.Equal(Masked(Decided(node)), Masked(Fixtures.Lf(file)));
        }
    }

    // UPSTREAM-DEVIATION heading-terminator (ExplainerParser.ReHeading): upstream's heading regex stops at a line terminator,
    // so a heading line holding a lone CR (a CR-only draft) or U+2028 throws a TypeError out of parseExplainer. The port reads
    // the whole line as the heading and reports the draft's ordinary diagnostics.
    [Fact] public void Heading_with_a_line_terminator_is_a_diagnostic_not_a_crash()
    {
        foreach (var name in new[] { "cr-only", "line-separator-heading" })
            Assert.Equal("""{"thrown":{"name":"TypeError","message":"Cannot read properties of null (reading '1')"}}""", Js.Stringify(JsonNode.Parse(Expected("parse", name))));
        var crOnly = ExplainerParser.Parse(Seed("parse", "cr-only")["args"]![0]!.GetValue<string>());
        Assert.Null(crOnly.Document);
        Assert.Equal(["explainer/parse-answer", "explainer/parse-panel"], crOnly.Diagnostics.Select(d => d.Code));
        var separator = ExplainerParser.Parse(Seed("parse", "line-separator-heading")["args"]![0]!.GetValue<string>());
        Assert.Empty(separator.Diagnostics);
        Assert.Equal("Q" + (char)0x2028 + "more", separator.Document!["panels"]![0]!["title"]!.GetValue<string>());
    }

    // --- Task 3 facts ---
    // Parked (contrived): the writing rules' `/iu` folds U+017F (long s) to s, so V8 matches "ſeamless" and "utiliſe"; .NET's
    // IgnoreCase | CultureInvariant does not. U+212A (Kelvin sign) folds to k in both.
    [Fact] public void Writing_rules_do_not_fold_long_s()
    {
        var node = Fixtures.Lf(Fixtures.Golden("explainer/writing/rule-fold-long-s.json"));
        foreach (var matched in new[] { "ſeamless", "maKe a decision", "utiliſe" }) Assert.Contains($"\"matched\": \"{matched}\"", node);
        var text = Seed("writing", "rule-fold-long-s")["args"]![0]![0]!["text"]!.GetValue<string>();
        Assert.Equal(["maKe a decision"],ExplainerWriting.Lint([new(text, 1, "prose")]).Select(d => d.Evidence.Str("matched")));
    }

    // --- Task 1 facts ---
    [Theory]
    [InlineData("explainer/reader.js")]
    [InlineData("explainer/reader.css")]
    public void Reader_assets_are_embedded_verbatim(string name)
    {
        using var stream = typeof(Resources).Assembly.GetManifestResourceStream(name)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        Assert.Equal(Fixtures.Bytes(Fixtures.Upstream(name)), copy.ToArray());
    }

    [Fact] public void Explainer_is_a_type_the_native_facade_refuses()
    {
        Assert.Equal(DiagramType.Explainer, DiagramTypes.Parse("explainer"));
        Assert.Equal("explainer", DiagramType.Explainer.Name());
        Assert.Throws<ArgumentOutOfRangeException>(() => DiagramType.Explainer.SemanticCollection());
        Assert.Null(DiagramType.Explainer.RelationshipCollection());
        var document = new JsonObject { ["schema_version"] = 1, ["diagram_type"] = "explainer" };
        Assert.Equal("use ArchifyNet.RenderExplainer", Assert.Throws<NotSupportedException>(() => ArchifyNet.Render(DiagramType.Explainer, document)).Message);
        Assert.Equal("use ArchifyNet.RenderExplainer", Assert.Throws<NotSupportedException>(() => ArchifyNet.Layout(DiagramType.Explainer, document)).Message);
        Assert.Equal("use ArchifyNet.RenderExplainer", Assert.Throws<NotSupportedException>(() => ArchifyNet.Validate(DiagramType.Explainer, document)).Message);
    }

    [Fact] public void NormalizePage_hashes_the_inflated_payload_once()
    {
        var bytes = Encoding.UTF8.GetBytes("<html>child</html>");
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(bytes);
        var page = $"<script>{{\"a\":{{\"htmlGzip\":\"{Convert.ToBase64String(packed.ToArray())}\",\"bytes\":18}}}}</script>";
        var expected = $"<script>{{\"a\":{{\"htmlGzip\":\"gz-sha256:{Sha256(bytes)}\",\"bytes\":18}}}}</script>";
        Assert.Equal(expected, NormalizePage(page));
        Assert.Equal(expected, NormalizePage(expected));   // a tokenised golden is left alone
    }

    [Fact] public void TokeniseChildren_hashes_each_child_html_only()
    {
        var result = JsonNode.Parse("""{"preparedDiagrams":{"diagrams":{"a":{"html":"<p>é</p>","bytes":10}}},"html":"kept","diagrams":{"b":{"svg":"<svg/>"}}}""")!;
        Assert.Equal("""{"preparedDiagrams":{"diagrams":{"a":{"html":"sha256:HEX","bytes":10}}},"html":"kept","diagrams":{"b":{"svg":"<svg/>"}}}""".Replace("HEX", Sha256("<p>é</p>")),
            Js.Stringify(TokeniseChildren(result)));
        Assert.Contains("\"html\":\"<p>é</p>\"", Js.Stringify(result));   // the input is not changed
    }

    [Fact] public void Tokens_cover_digest_artifact_and_paths()
    {
        var bundle = new JsonObject { ["kind"] = "archify-explainer-prepared", ["sourcePath"] = "x" };
        bundle["digest"] = Sha256(Js.Stringify(bundle));
        var report = new JsonObject { ["artifact"] = new JsonObject { ["sha256"] = "ab", ["bytes"] = 2 }, ["bundle"] = bundle };
        Assert.Equal("""{"artifact":"<artifact>","bundle":{"kind":"archify-explainer-prepared","sourcePath":"x","digest":"<digest>"}}""", Js.Stringify(TokeniseValue(report)));
        bundle["digest"] = "0";
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => TokeniseValue(report));   // the port's own digest must be right

        var dir = Path.Combine(Path.GetTempPath(), "axs-seed"); var temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        var text = Js.Stringify(new JsonObject { ["a"] = dir + "\\draft.md", ["b"] = temp + "\\archify-explainer-check-AbC123\\child.html" });
        Assert.Equal("""{"a":"<DIR>\\draft.md","b":"<TMP>\\child.html"}""", Tokenise(text, dir, null));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Tokenise(Js.Stringify(new JsonObject { ["c"] = temp + "\\other" }), dir, null));
        Assert.Equal("""["D/a.md",{"k":"R \\ R"},3]""", Js.Stringify(Substitute(JsonNode.Parse("""["<DIR>/a.md",{"k":"<REPO> \\ <REPO>"},3]"""), "D", "R")));
    }
}
