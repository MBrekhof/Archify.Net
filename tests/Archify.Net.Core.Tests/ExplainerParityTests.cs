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
    static readonly string[] Groups = ["parse", "shorthand", "inline", "diff", "writing", "validate", "prepare", "layout", "pages", "cases", "showcase", "check", "patch"];
    // Rows that differ from Node by a stated deviation (docs/deviations.md), each pinned by its own fact below.
    static readonly HashSet<(string Group, string Name)> KnownDeviations =
        [("parse", "cr-only"), ("parse", "line-separator-heading"), ("writing", "rule-fold-long-s"),
         ("patch", "payload-missing"), ("patch", "payload-twice"), ("patch", "payload-invalid-json")];

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
        var actual = Actual(group, name);
        if (expected != actual) Assert.Fail(ParityTests.FirstDifference(expected, actual));
    }

    // The port's tokenised output for a seed, as Matches_node compares it.
    static string Actual(string group, string name)
    {
        var seed = Seed(group, name);
        var dir = Directory.CreateTempSubdirectory("axs-").FullName;
        var repo = seed["repo"] switch   // the runner's `repo: true` or `repo: {origin}` (README.md)
        {
            JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.True => EvidenceRepo.CreateExplainer(),
            JsonObject o => EvidenceRepo.CreateExplainer(o.Str("origin")),
            _ => null,
        };
        try
        {
            WriteFiles(dir, seed["files"] as JsonObject);
            return Tokenise(NormalizePage(Run(seed, dir, repo)), dir, repo);
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
        if (repo is not null && args.Count > 0 && args[^1] is JsonObject last) last["repoRoot"] = repo;   // the runner's `repo: true`
        JsonNode? Arg(int i) => i < args.Count ? args[i] : null;
        try
        {
            var page = seed["page"] is JsonObject pageSeed ? BuildPage(pageSeed, dir, repo) : null;
            switch (seed["call"]!.GetValue<string>())
            {
                case "checkExplainerHtml":
                    var checkOptions = page is null ? Arg(1) : Arg(0);
                    return Json(ExplainerCheck.Check(Encoding.UTF8.GetBytes(page ?? Arg(0)!.GetValue<string>()), checkOptions.Str("file") ?? "<html>", checkOptions.Bool("checkNative") ?? true));
                case "scanHtml":
                    var (elements, scripts) = ExplainerCheck.Scan(Arg(0)!.GetValue<string>());
                    return Json(new JsonObject { ["elements"] = new JsonArray([.. elements.Select(e => (JsonNode?)Scanned(e))]), ["scripts"] = new JsonArray([.. scripts.Select(s => (JsonNode?)Scanned(s))]) });
                case "isExplainerHtml":
                    return Json(ExplainerCheck.IsExplainerHtml(Arg(0)!.GetValue<string>()));
                case "preparePanelPatch":
                    var patch = Arg(0);
                    var patched = ExplainerPatch.Prepare(new(patch.Str("artifactPath")!, patch.Str("panelId")!, patch.Str("replacementPath")!, patch.Str("repoRoot"),
                        patch.Str("quality"), patch.Str("writingMode"), patch.Obj("expectedArtifact")?.DeepClone().AsObject()));
                    return seed["render"]?.GetValueKind() == System.Text.Json.JsonValueKind.True ? ExplainerPipeline.Render(patched.Bundle, dir) : Json(patched.ToJson());
                case "layoutExplainerPanels":
                    return Json(ExplainerRender.Layout(Arg(0)!.AsObject(), Arg(1) as JsonObject));
                case "renderExplainer" when seed["draft"] is not null:
                    return ExplainerPipeline.Render(PrepareDraft(seed, dir, repo), dir);
                case "renderExplainer":
                    var render = Arg(1);
                    return ExplainerRender.Render(Arg(0)!.AsObject(), new(render.Obj("preparedBundle"), render.Str("sourceText"), render.Obj("sourceMap"),
                        render.Str("sourcePath"), render.Obj("preparedSources"), render.Obj("preparedDiagrams"), render.Arr("diagnostics"), render.Str("pageDirectory")));
                case "deliver":   // the cases group: deliver explainer <case>.archify.md <case>.html --quality standard, no repository root
                    var draftFile = Path.Join(dir, seed["draft"].Str("file"));
                    File.WriteAllText(draftFile, seed["draft"].Str("text"));
                    return ExplainerPipeline.Render(ExplainerPipeline.Prepare(seed["draft"].Str("text")!, new(draftFile, Quality: "standard")), dir);
                case "resolveExplainerSources":
                    return Json(ExplainerSources.Resolve(Arg(0)!.AsObject(), Resources(Arg(1))));
                case "prepareExplainerDiagrams":
                    return Json(ExplainerDiagrams.Prepare(Arg(0)!.AsObject(), Resources(Arg(1))));
                case "validateExplainerDiagramTarget":
                    var at = Arg(2);
                    return Json(ExplainerDiagrams.ValidateTarget(Arg(0) as JsonObject, Arg(1) as JsonObject,
                        new(at.Obj("sourceMap"), at.Str("sourcePath"), at.Str("pointer") ?? "/", (int?)at.Num("line") ?? 1)));
                case "prepareExplainer":
                    return Json(ExplainerPrepare.Prepare(Arg(0)!.GetValue<string>(), Prepare(Arg(1))));
                case "readPreparedBundle":
                    return Json(ReadPrepared(seed, dir, repo));
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
        catch (JsError e) { return Json(new JsonObject { ["thrown"] = new JsonObject { ["name"] = "Error", ["message"] = e.Message } }); }
    }

    // The runner's prepareDraft: the draft written to <DIR>/<file> and prepared as render-explainer.mjs does.
    static JsonObject PrepareDraft(JsonObject seed, string dir, string? repo) => PrepareDraft(seed["draft"], dir, repo);
    static JsonObject PrepareDraft(JsonNode? draftNode, string dir, string? repo)
    {
        var draft = (JsonObject)Substitute(draftNode, dir, repo)!;
        var inputPath = Path.Join(dir, draft.Str("file") ?? "draft.archify.md");
        Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
        File.WriteAllText(inputPath, draft.Str("text"));
        var options = draft.Obj("options")?.DeepClone().AsObject() ?? [];
        if (repo is not null) options["repoRoot"] = repo;
        options["inputPath"] = inputPath;
        return ExplainerPrepare.Prepare(draft.Str("text")!, Prepare(options));
    }

    // The runner's renderDraft: a `page` {draft | fixture, resign?, tamper?, file = 'page.html', after?} rendered by the port,
    // written to <DIR>/<file>, then `after` applied.
    static string BuildPage(JsonObject page, string dir, string? repo)
    {
        var file = Path.Join(dir, page.Str("file") ?? "page.html");
        var html = page["fixture"] is JsonObject fixture ? CheckFixture(fixture) : ExplainerPipeline.Render(PrepareDraft(page["draft"], dir, repo), Path.GetDirectoryName(file));
        if (page["resign"] is { } edits) html = Resign(html, edits);
        html = Tamper(html, page["tamper"]);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, html);
        WriteFiles(dir, page.Obj("after"));
        return html;
    }
    static string PayloadJson(JsonNode? value) => Regex.Replace(Js.Stringify(value), @"[<>&  ]", m => $"\\u{(int)m.Value[0]:x4}");   // render.mjs:14
    // `resign`: replacements over the page's unpacked prepared bundle, signed, packed and embedded again in place.
    static string Resign(string html, JsonNode edits)
    {
        var open = Assert.Single(Regex.Matches(html, "<script [^>]*id=\"archify-explainer-prepared\"[^>]*>"));
        var start = open.Index + open.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        var bundle = ParseJson(Tamper(Js.Stringify(ExplainerBundle.Unpack(ParseJson(html[start..end]))), edits));
        bundle["digest"] = ExplainerPrepare.Digest(bundle);
        return html[..start] + PayloadJson(ExplainerBundle.Pack(bundle)) + html[end..];
    }
    // test/explainer-check.test.mjs fixture(), as the runner's checkFixture builds it (README.md).
    static string CheckFixture(JsonObject f)
    {
        static string Escaped(JsonNode? value) => Regex.Replace(Js.Stringify(value), "[<>&]", m => $"\\u{(int)m.Value[0]:x4}");
        var kids = new JsonObject();
        foreach (var (id, child) in f.Obj("children") ?? [])
        {
            var html = child.Str("text") ?? Fixtures.Lf(Fixtures.Upstream("examples/" + child.Str("example")));
            kids[id] = new JsonObject
            {
                ["html"] = html, ["sha256"] = child?["sha256"]?.DeepClone() ?? Sha256(html), ["bytes"] = child?["bytes"]?.DeepClone() ?? Encoding.UTF8.GetByteCount(html),
            };
        }
        var dependencies = f.Arr("dependencies") ?? [];
        var asset = f.Obj("asset");
        var blocks = new JsonArray(new JsonObject { ["id"] = "explanation", ["type"] = "prose", ["text"] = "Explanation" });
        foreach (var (id, _) in kids) blocks.Add(new JsonObject { ["id"] = id, ["type"] = "diagram" });
        var document = new JsonObject
        {
            ["schema_version"] = 1, ["diagram_type"] = "explainer", ["meta"] = new JsonObject { ["id"] = "case", ["title"] = "Case" },
            ["answer"] = new JsonObject { ["summary"] = "Answer" },
            ["panels"] = new JsonArray(new JsonObject { ["id"] = "question", ["title"] = "Why?", ["blocks"] = blocks }), ["sources"] = new JsonArray(),
        };
        var text = Js.Stringify(document);
        var manifest = new JsonObject
        {
            ["version"] = 1, ["artifactId"] = "case", ["revision"] = Sha256(text), ["sourceSha256"] = Sha256(text), ["documentSha256"] = Sha256(Js.Stringify(document)),
            ["dependencies"] = dependencies.DeepClone(),
            ["children"] = new JsonObject(kids.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)new JsonObject { ["sha256"] = kv.Value!["sha256"]!.DeepClone(), ["bytes"] = kv.Value["bytes"]!.DeepClone() }))),
            ["assets"] = asset is null ? new JsonObject() : new JsonObject { ["image"] = asset.DeepClone() }, ["sources"] = new JsonObject(),
        };
        var bundle = new JsonObject
        {
            ["schemaVersion"] = 1, ["kind"] = "archify-explainer-prepared", ["document"] = document.DeepClone(), ["sourceText"] = text,
            ["source"] = new JsonObject { ["sha256"] = Sha256(text), ["bytes"] = Encoding.UTF8.GetByteCount(text) }, ["dependencies"] = dependencies.DeepClone(),
            ["preparedSources"] = new JsonObject { ["sources"] = new JsonObject() }, ["preparedDiagrams"] = new JsonObject { ["diagrams"] = kids.DeepClone() },
            ["quality"] = f.Str("quality") ?? (kids.Count > 0 ? "showcase" : "standard"),
        };
        bundle["digest"] = Sha256(Js.Stringify(bundle));
        manifest["revision"] = ExplainerBundle.Revision(bundle);
        manifest.Remove("dependencies");
        var image = asset is null ? "" : $"<img alt=\"contained\" src=\"data:{asset.Str("mime")};base64,{dependencies[0].Str("contentBase64")}\">";
        return $"<!doctype html><html data-archify-artifact=\"explainer\" data-revision=\"{manifest.Str("revision")}\"><head><title>Case</title></head><body><main id=\"question\"><p id=\"explanation\">Explanation</p>"
            + string.Concat(kids.Select(kv => $"<div id=\"{kv.Key}\">Diagram preview</div>")) + image + "</main>"
            + $"<script id=\"archify-explainer-prepared\" type=\"application/json\">{Escaped(bundle)}</script><script id=\"explainer-manifest\" type=\"application/json\">{Escaped(manifest)}</script></body></html>";
    }
    static JsonObject Scanned(ScannedElement e)
    {
        var json = new JsonObject { ["tag"] = e.Tag, ["attrs"] = new JsonObject(e.Attrs.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))), ["line"] = e.Line, ["index"] = e.Index };
        if (e is ScannedScript s) json["text"] = s.Text;
        return json;
    }

    static InlineContext Context(JsonNode? context) => new() { PanelId = context.Str("panelId"), Footnotes = context?["footnotes"] as JsonArray };
    // prepareExplainerDiagrams' `quality = 'showcase'` default; resolveExplainerSources reads no quality.
    static ResourceOptions Resources(JsonNode? o) => new(o.Str("repoRoot"), o.Str("baseDir"), o.Obj("sourceMap"), o.Str("sourcePath"), o.Str("quality") ?? "showcase");
    static PrepareOptions Prepare(JsonNode? o) =>
        new(o.Str("inputPath")!, o.Str("baseDir"), o.Str("repoRoot"), o.Str("quality"), o.Str("writingMode") ?? "warn", o.Str("output"));

    // The runner's {from, to} replacements, each `from` occurring exactly once.
    static string Tamper(string text, JsonNode? edits)
    {
        foreach (var edit in edits.AsArrayOrEmpty())
        {
            var (from, to) = (edit.Str("from")!, edit.Str("to")!);
            Assert.Equal(2, text.Split(from).Length);
            text = text.Replace(from, to);
        }
        return text;
    }
    static JsonObject ParseJson(string text) => (JsonObject)ExplainerJson.Parse(text, out string? _)!;   // JSON.parse, V8 key order

    // The runner's two-step readPreparedBundle: prepare draft A, apply `mutate` to its bundle (the file round trip is a JSON one),
    // and read it back against draft B (README.md).
    static JsonObject ReadPrepared(JsonObject seed, string dir, string? repo)
    {
        var draft = (JsonObject)Substitute(seed["draft"], dir, repo)!;
        var inputPath = Path.Join(dir, draft.Str("file") ?? "draft.archify.md");
        File.WriteAllText(inputPath, draft.Str("text"));
        var options = draft.Obj("options")?.DeepClone().AsObject() ?? [];
        if (repo is not null) options["repoRoot"] = repo;
        options["inputPath"] = inputPath;
        var bundle = ParseJson(Js.Stringify(ExplainerPrepare.Prepare(draft.Str("text")!, Prepare(options))));
        var mutate = seed.Obj("mutate") ?? [];
        Assert.True(mutate.Count <= 1);
        if (mutate["path"] is { } path) bundle["sourcePath"] = Path.GetFullPath(Path.Join(dir, path.GetValue<string>()));
        if (mutate.ContainsKey("quality")) bundle["quality"] = mutate["quality"]?.DeepClone();
        if (mutate.ContainsKey("writingMode")) { if (mutate["writingMode"] is null) bundle.Remove("writingMode"); else bundle["writingMode"] = mutate["writingMode"]!.DeepClone(); }
        if (mutate.ContainsKey("document")) bundle["document"] = ParseJson(Tamper(Js.Stringify(bundle["document"]), mutate["document"]));
        if (mutate.ContainsKey("signed")) bundle = ParseJson(Tamper(Js.Stringify(bundle), mutate["signed"]));
        if (new[] { "path", "quality", "writingMode", "document", "signed" }.Any(mutate.ContainsKey)) bundle["digest"] = ExplainerPrepare.Digest(bundle);
        if (mutate.ContainsKey("bytes")) bundle = ParseJson(Tamper(Js.Stringify(bundle), mutate["bytes"]));
        if (mutate.ContainsKey("dependency")) WriteFiles(dir, mutate.Obj("dependency"));
        var read = (JsonObject?)Substitute(seed["read"], dir, repo) ?? [];
        var inputB = read.Str("file") is { } file ? Path.Join(dir, file) : inputPath;
        if (read.Str("file") is not null && read.Str("text") is { } text) File.WriteAllText(inputB, text);
        var o = read.Obj("options");
        return ExplainerPrepare.ReadPrepared(bundle, read.Str("text") ?? draft.Str("text")!, inputB,
            new(o.Str("expectedInputPath") ?? inputB, o.Bool("checkDependencies") ?? true, o.Str("quality"), o.Str("writingMode")));
    }

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

    // A seed's `files`: {path: {text} | {base64, zeros?} | {zeros: n} | null}, written into (null: removed from) its directory;
    // `zeros` after `base64` appends that many zero bytes.
    static void WriteFiles(string dir, JsonObject? files)
    {
        foreach (var (rel, entry) in files ?? new JsonObject())
        {
            var path = Path.Combine(dir, rel);
            if (entry is null) { File.Delete(path); continue; }
            var bytes = entry["text"] is { } text ? new UTF8Encoding(false).GetBytes(text.GetValue<string>())
                : entry["base64"] is { } base64 ? [.. Convert.FromBase64String(base64.GetValue<string>()), .. new byte[(int)(entry["zeros"]?.D() ?? 0)]]
                : new byte[(int)entry["zeros"]!.D()];
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

    // The pages committed under showcase/explainer (rendered by `archify deliver explainer <case>.archify.md <case>.html --quality standard`)
    // equal the showcase goldens, which upstream v3.2.0 minted from the same drafts (English translations of upstream's cases, TRANS-001),
    // after NormalizePage.
    [Theory]
    [InlineData("first-rental")]
    [InlineData("p99-tail-latency")]
    [InlineData("tls-first-visit")]
    [InlineData("finalize-delivery")]
    public void Showcase_pages_match_upstream(string name)
    {
        var page = Path.Combine(Fixtures.RepoRoot, "showcase", "explainer", name + ".html");
        Assert.True(File.Exists(page), page);
        var expected = Expected("showcase", name);
        var actual = NormalizePage(Fixtures.Lf(page));
        if (expected != actual) Assert.Fail(ParityTests.FirstDifference(expected, actual));
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
            var fixedText = Fixtures.Lf(file);
            Assert.NotEqual(node, fixedText);
            // Decision 3 (a page from a draft without a locale) switches the whole reader dictionary; a page fixed by
            // Decision 2 alone (an en page with a shorthand branch lane) takes the text check below.
            if (name.EndsWith(".html", StringComparison.Ordinal) && node.Contains("<html lang=\"zh-CN\"", StringComparison.Ordinal))
            {
                AssertDefaultLocaleOnly(node, fixedText);
                continue;
            }
            Assert.Equal(Masked(Decided(node)), Masked(fixedText));
        }
    }

    // Decision 3 changes no data and no markup: the revision and the prepared and manifest payloads are byte-equal, and with
    // text nodes, the reader dictionary and the lang value stripped, tags and attributes are identical. Only lang differs.
    internal static void AssertDefaultLocaleOnly(string node, string fixedText)
    {
        Assert.Contains("<html lang=\"zh-CN\"", node);
        Assert.Contains("<html lang=\"en\"", fixedText);
        Assert.DoesNotContain("lang=\"zh-CN\"", fixedText);
        static string Revision(string page) => Regex.Match(page, "<html\\b[^>]*\\bdata-revision=\"([^\"]*)\"").Groups[1].Value;
        Assert.NotEqual("", Revision(node));
        Assert.Equal(Revision(node), Revision(fixedText));
        foreach (var id in new[] { "archify-explainer-prepared", "explainer-manifest" })
            Assert.Equal(ScriptBody(node, id), ScriptBody(fixedText, id));
        // Attribute values (the toolbar's aria-labels) may differ only by the reader dictionary: its zh and en leaves zipped
        // into a table, plus render.mjs's inline ternaries, applied longest first.
        var table = new List<(string Zh, string En)>
        {
            ("跳至正文", "Skip to content"), ("主题", "Theme"), ("典型值说明", "Typical value notes"), ("上限", "Limit"), ("超限", "Over limit by"),
            ("有利", "Favorable"), ("有代价", "Tradeoff"), ("不利", "Unfavorable"), ("原行", "Old"), ("新行", "New"), ("改动", "Change"), ("代码改动", "Code changes"),
        };
        void Zip(JsonNode? zh, JsonNode? en)
        {
            if (zh is JsonObject zo && en is JsonObject eo) foreach (var (key, value) in zo) Zip(value, eo[key]);
            else if (zh is JsonArray za && en is JsonArray ea) for (var i = 0; i < Math.Min(za.Count, ea.Count); i++) Zip(za[i], ea[i]);
            else if (zh is JsonValue zv && zv.TryGetValue<string>(out var z) && en is JsonValue ev && ev.TryGetValue<string>(out var e) && z != e) table.Add((z, e));
        }
        Zip(JsonNode.Parse(ScriptBody(node, "explainer-reader-words")), JsonNode.Parse(ScriptBody(fixedText, "explainer-reader-words")));
        table.Sort((a, b) => b.Zh.Length - a.Zh.Length);
        string Translate(string tag) => tag.StartsWith("<script", StringComparison.Ordinal) || tag.StartsWith("<style", StringComparison.Ordinal)
            ? tag : table.Aggregate(tag, (t, pair) => t.Replace(pair.Zh, pair.En));
        Assert.Equal(string.Concat(Skeleton(node).Select(Translate)), string.Concat(Skeleton(fixedText)));
    }
    static string ScriptBody(string page, string id)
    {
        var match = Regex.Match(page, $"<script type=\"application/json\" id=\"{id}\">(.*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success, id);
        return match.Groups[1].Value;
    }
    // Tags (with their attributes) and the raw text of <style> and <script> stay, one token each; text nodes, the reader
    // dictionary's JSON and every lang value go.
    static IEnumerable<string> Skeleton(string page)
    {
        page = Regex.Replace(page, "(<script type=\"application/json\" id=\"explainer-reader-words\">).*?(</script>)", "$1$2", RegexOptions.Singleline);
        page = Regex.Replace(page, "\\blang=\"[^\"]*\"", "lang=\"\"");
        return Regex.Matches(page, "<script\\b[^>]*>.*?</script>|<style\\b[^>]*>.*?</style>|<[^>]*>", RegexOptions.Singleline).Select(m => m.Value);
    }

    // The page check bites: a fixed page with a changed revision, an extra attribute or changed CSS fails it.
    [Fact] public void Default_locale_page_check_rejects_other_changes()
    {
        var node = Fixtures.Lf(Fixtures.Golden("explainer/pages/no-locale.html"));
        var fixedText = Fixtures.Lf(Fixtures.Golden("explainer/pages/no-locale.fixed.html"));
        AssertDefaultLocaleOnly(node, fixedText);
        var revision = Regex.Match(fixedText, "data-revision=\"([0-9a-f]+)\"").Groups[1].Value;
        foreach (var mutated in new[]
        {
            fixedText.Replace($"data-revision=\"{revision}\"", $"data-revision=\"{revision[1..]}0\""),
            fixedText.Replace("<html lang=\"en\"", "<html lang=\"en\" data-extra=\"x\""),
            Regex.Replace(fixedText, "<style>", "<style>a{color:red}", RegexOptions.None, TimeSpan.FromSeconds(5)),
            fixedText.Replace("aria-label=\"Theme\"", "aria-label=\"Themes\""),
        })
        {
            Assert.NotEqual(fixedText, mutated);
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertDefaultLocaleOnly(node, mutated));
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

    // --- Task 4 facts ---
    const string TinyPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jZ1EAAAAASUVORK5CYII=";

    // A bundle's digest covers everything but itself, a read returns an equal copy, and a rebind signs the new draft.
    [Fact] public void Prepared_digest_is_self_consistent()
    {
        var dir = Directory.CreateTempSubdirectory("axs-").FullName;
        try
        {
            var text = "# Why? {#why}\n\nBecause.\n\n@source docs title=\"Docs\"\n\n## How? {#how}\n\n@tree id=layout\n| root | Platform |\n| api | API | root |\n\n@image id=pixel path=tiny.png alt=\"Pixel\"\n";
            File.WriteAllBytes(Path.Join(dir, "tiny.png"), Convert.FromBase64String(TinyPng));
            var input = Path.Join(dir, "draft.archify.md");
            var bundle = ExplainerPrepare.Prepare(text, new(input));
            var unsigned = bundle.DeepClone().AsObject();
            unsigned.Remove("digest");
            Assert.Equal(Sha256(Js.Stringify(unsigned)), bundle.Str("digest"));
            Assert.Equal("digest", bundle.Last().Key);
            ExplainerPrepare.CheckBundleSize(bundle);
            var read = ExplainerPrepare.ReadPrepared(bundle, text, input, new(input));
            Assert.NotSame(bundle, read);
            Assert.Equal(Js.Stringify(bundle), Js.Stringify(read));

            var edited = text.Replace("Because.", "Because of the cache.");
            Assert.Equal("explainer/prepared-mismatch", Assert.Throws<ArchifyDiagnosticException>(() => ExplainerPrepare.ReadPrepared(bundle, edited, input, new(input))).Diagnostics[0].Code);
            var rebound = ExplainerPrepare.Rebind(bundle, ExplainerPrepare.ParseValidated(edited, input, null));
            Assert.Equal(ExplainerPrepare.Digest(rebound), rebound.Str("digest"));
            Assert.NotEqual(bundle.Str("digest"), rebound.Str("digest"));
            Assert.Equal(bundle.Select(kv => kv.Key), rebound.Select(kv => kv.Key));
            Assert.Equal(edited, ExplainerPrepare.ReadPrepared(rebound, edited, input, new(input)).Str("sourceText"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // readContained and readSpec refuse a reparse point at any component below the root (Node's lstat reports a junction as a
    // link); the root itself may be one, as fs.realpathSync resolves it. A junction needs no privilege, unlike a symbolic link.
    [Fact] public void Reparse_points_below_the_root_are_refused()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("axs-").FullName;
        try
        {
            var real = Directory.CreateDirectory(Path.Join(dir, "real")).FullName;
            File.WriteAllBytes(Path.Join(real, "tiny.png"), Convert.FromBase64String(TinyPng));
            File.WriteAllText(Path.Join(real, "tree.json"), "{}");
            var linked = Path.Join(dir, "linked");
            var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", ["/c", "mklink", "/J", linked, real]) { RedirectStandardOutput = true })!;
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
            JsonObject Images(string path) => new()
            {
                ["sources"] = new JsonArray(),
                ["panels"] = new JsonArray(new JsonObject { ["id"] = "p", ["blocks"] = new JsonArray(new JsonObject { ["id"] = "a", ["type"] = "image", ["path"] = path, ["alt"] = "A" }) }),
            };
            var refused = Assert.Throws<ArchifyDiagnosticException>(() => ExplainerSources.Resolve(Images("linked/tiny.png"), new(null, dir, null, null)));
            Assert.Equal(("explainer/asset-symlink", "/panels/0/blocks/0/path"), (refused.Diagnostics[0].Code, refused.Diagnostics[0].Path));
            var diagram = new JsonObject
            {
                ["panels"] = new JsonArray(new JsonObject { ["id"] = "p", ["blocks"] = new JsonArray(new JsonObject { ["id"] = "t", ["type"] = "diagram", ["diagramType"] = "tree", ["source"] = "linked/tree.json" }) }),
            };
            Assert.Equal("explainer/diagram-source-symlink", Assert.Throws<ArchifyDiagnosticException>(() => ExplainerDiagrams.Prepare(diagram, new(null, dir, null, null))).Diagnostics[0].Code);
            var through = ExplainerSources.Resolve(Images("tiny.png"), new(null, linked, null, null));
            Assert.Equal("image/png", through["assets"]!["a"].Str("mime"));
        }
        finally
        {
            if (Directory.Exists(Path.Join(dir, "linked"))) Directory.Delete(Path.Join(dir, "linked"));   // the junction itself, not its target
            Directory.Delete(dir, recursive: true);
        }
    }

    // --- Task 5 facts ---
    static JsonNode Script(string page, string id)
    {
        var match = Regex.Match(page, $"<script type=\"application/json\" id=\"{id}\">(.*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success, id);
        return JsonNode.Parse(match.Groups[1].Value)!;   // json() escapes < > & U+2028 U+2029 as \u, which JSON reads back
    }

    // UPSTREAM-DEVIATION gzip-stream (Decision 1): each packed child is .NET's GZip stream at level 9 (XFL 02), which inflates
    // to the child HTML the manifest names. Nothing here claims the bytes differ from Node's zlib stream.
    [Fact] public void Gzip_payload_is_the_stated_deviation()
    {
        var dir = Directory.CreateTempSubdirectory("axs-").FullName;
        try
        {
            var page = ExplainerPipeline.Render(PrepareDraft(Seed("pages", "diagram-guided"), dir, null), dir);
            var children = Script(page, "explainer-manifest")["children"]!.AsObject();
            var diagrams = Script(page, "archify-explainer-prepared")["preparedDiagrams"]!["diagrams"]!.AsObject();
            Assert.Equal(children.Select(kv => kv.Key), diagrams.Select(kv => kv.Key));
            Assert.NotEmpty(diagrams);
            foreach (var (id, child) in diagrams)
            {
                Assert.False(child!.AsObject().ContainsKey("html"));
                var packed = Convert.FromBase64String(child["htmlGzip"]!.GetValue<string>());
                Assert.Equal([0x1f, 0x8b, 0x08], packed[..3]);   // GZip, deflate
                Assert.Equal(0x02, packed[8]);                    // XFL 02: maximum compression
                using var gzip = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress);
                using var inflated = new MemoryStream();
                gzip.CopyTo(inflated);
                Assert.Equal(children[id]!["sha256"]!.GetValue<string>(), Sha256(inflated.ToArray()));
                Assert.Equal(children[id]!["bytes"]!.GetValue<int>(), (int)inflated.Length);
            }
            var unpacked = ExplainerBundle.Unpack(Script(page, "archify-explainer-prepared").AsObject());
            foreach (var (id, child) in unpacked["preparedDiagrams"]!["diagrams"]!.AsObject())
                Assert.Equal(children[id]!["sha256"]!.GetValue<string>(), Sha256(child!["html"]!.GetValue<string>()));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // UPSTREAM-DEVIATION default-locale (Decision 3): a page from a draft without @meta locale is English (lang, controls, the
    // footnote heading), where upstream's render.mjs:38 defaults to zh-CN. The port equals the corrected oracle's golden;
    // Node's stays beside it.
    [Fact] public void Default_locale_is_en_the_stated_deviation()
    {
        Assert.DoesNotContain("@meta", Seed("pages", "no-locale")["draft"].Str("text"));
        var node = Fixtures.Lf(Fixtures.Golden("explainer/pages/no-locale.html"));
        foreach (var zh in new[] { "<html lang=\"zh-CN\"", "跳至正文", "典型值说明", "\"answer\":\"结论\"" }) Assert.Contains(zh, node);
        var corrected = Fixtures.Lf(Fixtures.Golden("explainer/pages/no-locale.fixed.html"));
        foreach (var en in new[] { "<html lang=\"en\"", "Skip to content", "Typical value notes", "\"answer\":\"Answer\"" }) Assert.Contains(en, corrected);
        Compare("pages", "no-locale", NormalizePage(corrected));
    }

    // The library facade prepares and renders; a draft problem, a front-matter PeleException and any other failure come back as
    // Error and diagnostics through the renderer-failure mapping.
    [Fact] public void RenderExplainer_returns_diagnostics_not_exceptions()
    {
        var dir = Directory.CreateTempSubdirectory("axs-").FullName;
        try
        {
            var input = Path.Join(dir, "draft.archify.md");
            var ok = ArchifyNet.RenderExplainer(Seed("pages", "no-locale")["draft"].Str("text")!, new(input));
            Assert.StartsWith("<!doctype html>\n<html lang=\"en\"", ok.Html);
            Assert.Equal(("", "", null, null), (ok.Svg, ok.Cards, ok.Layout, ok.Error));
            Assert.Empty(ok.ThrownDiagnostics);
            Assert.All(ok.Diagnostics, d => Assert.Equal(Severity.Warning, d.Severity));

            var invalid = ArchifyNet.RenderExplainer("# T\n\nOne.\n\nTwo.\n\n## Q\n\nText.", new(input));
            Assert.Equal(("", "Explainer draft could not be parsed."), (invalid.Html, invalid.Error));
            Assert.Equal("explainer/parse-answer", Assert.Single(invalid.Diagnostics).Code);

            var frontMatter = ArchifyNet.RenderExplainer(Seed("validate", "parsed-front-matter")["parsed"]!.GetValue<string>(), new(input));
            Assert.Equal("", frontMatter.Html);
            Assert.Equal("internal/unclassified", Assert.Single(frontMatter.Diagnostics).Code);
            Assert.StartsWith("Front matter is not valid YAML.", frontMatter.Error);

            var missing = ArchifyNet.RenderExplainer("# T\n\nA.\n\n## Q {#q}\n\n@image id=pic path=missing.png alt=\"x\"\n", new(input, QualityProfile.Showcase, WritingMode: "off", PageDirectory: dir));
            Assert.Equal("", missing.Html);
            Assert.StartsWith("explainer/asset", Assert.Single(missing.Diagnostics).Code);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Task 4 review, Minor 1: a reparse point that is neither a symbolic link nor a junction (a OneDrive Files On-Demand
    // placeholder, dedup) is read as the file it stands for, as libuv's lstat reads it; only a link target makes a link.
    [Fact] public void Only_symbolic_links_and_junctions_are_links()
    {
        Assert.False(ExplainerSources.IsLink(FileAttributes.ReparsePoint | FileAttributes.Archive | FileAttributes.Offline, () => null));
        Assert.True(ExplainerSources.IsLink(FileAttributes.ReparsePoint | FileAttributes.Directory, () => @"C:\target"));
        Assert.False(ExplainerSources.IsLink(FileAttributes.Archive, () => throw new InvalidOperationException("a plain file's target is never read")));
    }

    // --- Task 6 facts ---
    // UPSTREAM-DEVIATION patch-payload-message: patch.mjs:26 rethrows a caught error only when it has `diagnostics`, but
    // throwDiagnosticError attaches `archifyDiagnostics`, so embedded()'s own failure (no payload, two payloads, invalid JSON)
    // comes out as "Embedded native diagram payload cannot be inflated." with the panel id. The port reports embedded()'s
    // diagnostic, which names no panel.
    [Theory]
    [InlineData("payload-missing", "The artifact must contain one original draft and prepared resource payload.", "Use an artifact produced by the formal explainer CLI.")]
    [InlineData("payload-twice", "The artifact must contain one original draft and prepared resource payload.", "Use an artifact produced by the formal explainer CLI.")]
    [InlineData("payload-invalid-json", "Embedded draft payload is invalid.", "Rebuild the artifact from its original draft.")]
    public void Patch_payload_failures_keep_their_own_message(string name, string message, string example)
    {
        var node = JsonNode.Parse(Expected("patch", name))!["thrown"]!;
        Assert.Equal("Embedded native diagram payload cannot be inflated.", node.Str("message"));
        Assert.Equal("first", node["code"]![0]!["subject"].Str("panelId"));
        var port = JsonNode.Parse(Actual("patch", name))!["thrown"]!;
        Assert.Equal(message, port.Str("message"));
        var diagnostic = Assert.Single(port["code"]!.AsArray())!;
        Assert.Equal(("explainer/patch-payload", message, example), (diagnostic.Str("code"), diagnostic.Str("message"), diagnostic["evidence"].Str("example")));
        Assert.Equal("""{"input":"<DIR>/page.html","line":1,"component":"panel"}""", Js.Stringify(diagnostic["subject"]));
    }

    // check-render-output.mjs:44-48: an explainer page, by its marker or by its prepared payload alone, goes to the explainer
    // checker, and the facade returns that report without `file`. Each native child is checked in process; its receipt names
    // an unwritten <tmp>\archify-explainer-check-XXXXXX\child.html (stated deviation, as prepare's children).
    [Fact] public void Check_routes_explainer_pages_to_the_explainer_checker()
    {
        var dir = Directory.CreateTempSubdirectory("axs-").FullName;
        try
        {
            var page = BuildPage(Seed("check", "clean-native")["page"]!.AsObject(), dir, null);
            var bytes = Encoding.UTF8.GetBytes(page);
            var direct = ExplainerCheck.Check(bytes);
            Assert.Equal("<html>", direct.Str("file"));
            var child = direct["composition"]!["nativeDiagrams"]![0]!["receipt"]!.Str("file")!;
            Assert.Matches(@"^" + Regex.Escape(Path.GetTempPath()) + @"archify-explainer-check-[A-Za-z0-9]{6}\\child\.html$", child);
            Assert.False(Directory.Exists(Path.GetDirectoryName(child)));
            foreach (var html in new[] { page, page.Replace(" data-archify-artifact=\"explainer\"", "") })
            {
                var check = ArchifyNet.Check(Encoding.UTF8.GetBytes(html));
                Assert.Equal("explainer", check.Report.Str("family"));
                Assert.False(check.Report.ContainsKey("file"));
                Assert.Equal(html == page, check.Ok);
            }
            static string Stable(JsonObject report) => Regex.Replace(Js.Stringify(report), "archify-explainer-check-[A-Za-z0-9]{6}", "<tmp>");
            var facade = ArchifyNet.Check(bytes).Report;
            direct.Remove("file");
            Assert.Equal(Stable(direct), Stable(facade));
            Assert.Equal(["ok", "family", "artifact", "checks", "composition", "diagnostics"], facade.Select(kv => kv.Key));
            Assert.Null(ArchifyNet.Check(Fixtures.Bytes(Fixtures.Upstream("examples/erd-orders-rendered.html"))).Report["family"]);   // native pages unchanged
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // A matching expectedArtifact (the validated page's own identity) lets the patch through; no Node seed can name it, because
    // the port's page bytes differ from Node's by the gzip stream (Decision 1).
    [Fact] public void Patch_accepts_the_validated_artifact()
    {
        var dir = Directory.CreateTempSubdirectory("axs-").FullName;
        try
        {
            var seed = Seed("patch", "md-panel");
            WriteFiles(dir, (JsonObject)Substitute(seed["files"], dir, null)!);
            BuildPage(seed["page"]!.AsObject(), dir, null);
            var artifact = Path.Join(dir, "page.html");
            var identity = ArchifyNet.Check(File.ReadAllBytes(artifact)).Report["artifact"]!.DeepClone().AsObject();
            var result = ExplainerPipeline.Patch(new(artifact, "first", Path.Join(dir, "replacement.md"), ExpectedArtifact: identity));
            Assert.Contains("New paragraph.", result.SourceText);
            Assert.Equal(ExplainerPrepare.Digest(result.Bundle), result.Bundle.Str("digest"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Carry-forward (Task 5, concern 1): upstream's plain `new Error(...)` is the port's internal JsError; an unclassified one
    // reports errorName "Error", as upstream's error.name does.
    [Fact] public void Unclassified_JsError_is_named_Error()
    {
        var (error, diagnostics, _) = new DiagnosticSink().RendererFailure(new JsError("Bundle differs."));
        Assert.Equal("Bundle differs.", error);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(("internal/unclassified", "Error"), (diagnostic.Code, diagnostic.Evidence.Str("errorName")));
        Assert.Equal("PeleError", Assert.Single(new DiagnosticSink().RendererFailure(new PeleException("x", "syntax")).diagnostics).Evidence.Str("errorName"));
        Assert.Equal("InvalidOperationException", Assert.Single(new DiagnosticSink().RendererFailure(new InvalidOperationException("x")).diagnostics).Evidence.Str("errorName"));
        Assert.Equal("a-b", ExplainerShorthand.InferredId("A \ud800 B"));
        Assert.StartsWith("actor-", ExplainerShorthand.InferredId("\ud800"));
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
