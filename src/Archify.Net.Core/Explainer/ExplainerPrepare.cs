using System.Text;
using System.Text.Json.Nodes;

namespace Archify.Net;

// prepareExplainer's options. WritingMode is `options.writingMode || 'warn'`.
public sealed record PrepareOptions(string InputPath, string? BaseDir = null, string? RepoRoot = null, string? Quality = null,
    string WritingMode = "warn", string? Output = null);

// readPreparedBundle's options; ExpectedInputPath is the caller's input path unless it names another.
public sealed record ReadPreparedOptions(string ExpectedInputPath, bool CheckDependencies = true, string? Quality = null, string? WritingMode = null);

// archify 3.2.0 explainer/prepare.mjs: the prepared bundle, a draft frozen with its resolved sources and native diagrams, and
// the checks that bind a bundle to the draft it came from. The bundle file itself (writePreparedBundle, readPreparedBundle's
// read) is the CLI's: here a bundle is an in-memory JsonObject.
internal static class ExplainerPrepare
{
    const int MaxBundleBytes = 64 * 1024 * 1024, MaxSourceBytes = 16 * 1024 * 1024;
    static readonly HashSet<string> Skipped = new(StringComparer.Ordinal) { "spec", "source", "path", "language", "target", "changes" };

    // identity (:13): {sha256, bytes}
    internal static JsonObject Identity(byte[] bytes) => new() { ["sha256"] = ExplainerSources.Sha256(bytes), ["bytes"] = bytes.Length };
    internal static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);   // Buffer.from: a lone surrogate becomes EF BF BD

    /// <summary>digest (:14): sha256 of JSON.stringify(bundle) without its digest.</summary>
    internal static string Digest(JsonObject bundle)
    {
        var unsigned = bundle.DeepClone().AsObject();
        unsigned.Remove("digest");
        return ExplainerSources.Sha256(Utf8(Js.Stringify(unsigned)));
    }

    // fail (:15-18)
    static Exception Fail(string code, string message, string? input, string reason, string example = "Retry from the unchanged original draft and local resources.")
    {
        var subject = new JsonObject();
        if (input is not null) subject["input"] = input;
        subject["line"] = 1;
        subject["component"] = "document";
        return new ArchifyDiagnosticException(message, [Diagnostic.Normalize(new($"explainer/{code}", Severity.Error, message, subject,
            new JsonObject { ["reason"] = reason, ["example"] = example }, [example], null))]);
    }
    static Exception Throw(string message, IEnumerable<Diagnostic> diagnostics) => new ArchifyDiagnosticException(message, [.. diagnostics.Select(Diagnostic.Normalize)]);

    /// <summary>parseValidatedExplainer (:19-28): the parse, then validation; either failing throws. A front-matter
    /// PeleException from a chart fence propagates.</summary>
    public static ExplainerParse ParseValidated(string sourceText, string? inputPath, string? output, string writingMode = "warn")
    {
        if (Encoding.UTF8.GetByteCount(sourceText) > MaxSourceBytes)
            throw Fail("source-limit", "Explainer draft exceeds the 16 MiB source budget.", inputPath, "source-byte-limit",
                "Reduce the draft below 16 MiB and use relative native diagram sources for large specifications.");
        var parsed = ExplainerParser.Parse(sourceText, inputPath, output);
        if (!parsed.Document.Truthy() || parsed.Diagnostics.Any(entry => entry.Severity != Severity.Warning))   // `!parsed.document`: a `false` draft too
            throw Throw("Explainer draft could not be parsed.", parsed.Diagnostics);
        var (ok, diagnostics) = ExplainerValidator.Validate(parsed.Document!, parsed.SourceMap, inputPath, writingMode);
        if (!ok) throw Throw("Explainer draft is invalid.", diagnostics);
        return parsed with { Diagnostics = [.. parsed.Diagnostics, .. diagnostics] };
    }

    static JsonNode? Take(JsonObject from, string key)
    {
        var value = from[key];
        from.Remove(key);
        return value;
    }

    /// <summary>prepareExplainer (:29-47): the draft parsed and validated, its sources resolved and its native diagrams
    /// rendered, bound by a digest. Returns the bundle.</summary>
    public static JsonObject Prepare(string sourceText, PrepareOptions o)
    {
        var inputPath = Path.GetFullPath(o.InputPath);
        var parsed = ParseValidated(sourceText, inputPath, o.Output, o.WritingMode);
        var document = parsed.Document!.AsObject();   // validated: an object
        var baseDir = string.IsNullOrEmpty(o.BaseDir) ? Path.GetDirectoryName(inputPath)! : o.BaseDir;
        var quality = !string.IsNullOrEmpty(o.Quality) ? o.Quality : document["meta"].Str("quality_profile") is { Length: > 0 } authored ? authored : "standard";
        var common = new ResourceOptions(o.RepoRoot, baseDir, parsed.SourceMap, inputPath, quality);
        var sources = ExplainerSources.Resolve(document, common);
        var diagrams = ExplainerDiagrams.Prepare(document, common);
        ValidatePreparedTargets(document, diagrams["diagrams"]!.AsObject(), common);
        var dependencies = new JsonArray();
        foreach (var entry in sources["dependencies"]!.AsArray().Concat(diagrams["dependencies"]!.AsArray()).Select(e => e!.AsObject()))
        {
            var copy = entry.DeepClone().AsObject();
            copy["id"] = (entry["id"].Truthy() ? entry["id"] : entry["blockId"])?.DeepClone();
            if (!(entry.Str("kind") == "repository" || entry["inline"].Truthy())) copy["path"] = Path.GetFullPath(Path.Combine(baseDir, entry.Str("path")!));
            dependencies.Add(copy);
        }
        var bundle = new JsonObject { ["schemaVersion"] = 1, ["kind"] = "archify-explainer-prepared" };
        var json = parsed.ToJson();
        foreach (var key in json.Select(kv => kv.Key).ToList()) bundle[key] = Take(json, key);
        bundle["sourcePath"] = inputPath;
        bundle["baseDir"] = baseDir;
        bundle["quality"] = quality;
        bundle["writingMode"] = string.IsNullOrEmpty(o.WritingMode) ? "warn" : o.WritingMode;
        bundle["source"] = Identity(Utf8(sourceText));
        var preparedSources = new JsonObject { ["sources"] = Take(sources, "sources"), ["assets"] = Take(sources, "assets") };
        if (sources["charts"]!.AsObject().Count > 0) preparedSources["charts"] = Take(sources, "charts");
        if (sources["repository"] is not null) preparedSources["repository"] = Take(sources, "repository");
        bundle["preparedSources"] = preparedSources;
        bundle["preparedDiagrams"] = new JsonObject { ["diagrams"] = Take(diagrams, "diagrams") };
        bundle["dependencies"] = dependencies;
        bundle["digest"] = Digest(bundle);
        AssertDependencies(bundle);
        return bundle;
    }

    // An fs error's code. ponytail: the four a frozen file meets in practice; anything else reads as read-failed.
    static string ErrorCode(Exception error, string path) => error switch
    {
        _ when Directory.Exists(path) => "EISDIR",
        FileNotFoundException or DirectoryNotFoundException => "ENOENT",
        UnauthorizedAccessException => "EPERM",
        IOException => "EBUSY",
        _ => "read-failed",
    };

    /// <summary>assertPreparedDependencies (:48-66): frozen bytes match their identity, and each file a bundle was frozen from
    /// is unchanged on disk.</summary>
    public static void AssertDependencies(JsonObject bundle)
    {
        var input = bundle.Str("sourcePath");
        foreach (var dependency in bundle["dependencies"].AsArrayOrEmpty().OfType<JsonObject>())
        {
            // Repository excerpts were read from the pinned Git object, never the mutable checkout.
            if (dependency.Str("kind") == "repository" || dependency["inline"].Truthy() || dependency["retained"].Truthy())
            {
                if (dependency["contentBase64"] is JsonValue content && content.TryGetValue<string>(out var base64))
                {
                    // ponytail: Buffer.from(…, 'base64') skips characters outside the alphabet; invalid base64 here is a mismatch.
                    var bytes = new byte[base64.Length];
                    var frozen = Convert.TryFromBase64String(base64, bytes, out var length) ? bytes[..length] : null;
                    if (frozen is null || ExplainerSources.Sha256(frozen) != dependency.Str("sha256") || dependency.Num("bytes") != frozen.Length)
                        throw Fail("prepared-mismatch", "Frozen resource bytes do not match their identity.", input, "frozen-resource-byte-mismatch");
                }
                else if (dependency["retained"].Truthy()) throw Fail("prepared-invalid", "Retained resources require their original frozen bytes.", input, "missing-frozen-resource");
                continue;
            }
            var path = dependency.Str("path");
            byte[] actual;
            try { actual = path is null ? throw new ArgumentNullException(nameof(path)) : File.ReadAllBytes(path); }
            catch (Exception error)
            {
                throw Fail("dependency-changed", "A frozen explainer resource is no longer readable.", input, path is null ? "ERR_INVALID_ARG_TYPE" : ErrorCode(error, path));
            }
            if (ExplainerSources.Sha256(actual) != dependency.Str("sha256") || dependency.Num("bytes") != actual.Length)
                throw Fail("dependency-changed", "An explainer resource changed during preparation.", input, "frozen-resource-byte-mismatch");
        }
    }

    /// <summary>writePreparedBundle's resource limit (:67-73), without the file: the serialised bundle stays within 64 MiB.</summary>
    public static void CheckBundleSize(JsonObject bundle)
    {
        if (Encoding.UTF8.GetByteCount(Js.Stringify(bundle)) > MaxBundleBytes)
            throw Fail("resource-limit", "Prepared explainer exceeds the resource budget.", bundle.Str("sourcePath"), "bundle-byte-limit");
    }

    // realpathSync.native(path.resolve(value)), falling back to path.resolve: an existing file is its real path, so two
    // spellings of one file compare equal. ponytail: on Windows existing paths compare ignoring case, which is what the real
    // on-disk name gives Node; an 8.3 short name is not expanded.
    static (string Path, bool Exists) Canonical(string value)
    {
        var full = Path.GetFullPath(value);
        if (!File.Exists(full) && !Directory.Exists(full)) return (full, false);
        var info = new FileInfo(full);
        return ((info.ResolveLinkTarget(returnFinalTarget: true) ?? info).FullName, true);
    }
    static bool SamePath(string left, string right)
    {
        var (a, b) = (Canonical(left), Canonical(right));
        return string.Equals(a.Path, b.Path, a.Exists && b.Exists && OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>readPreparedBundle (:74-96) on an in-memory bundle: a deep copy, as the file's JSON round trip makes one, checked
    /// against the draft it must belong to. Returns that copy.</summary>
    public static JsonObject ReadPrepared(JsonObject bundle, string sourceText, string inputPath, ReadPreparedOptions o)
    {
        bundle = bundle.DeepClone().AsObject();
        var recorded = (bundle["inputPath"].Truthy() ? bundle["inputPath"] : bundle["sourcePath"]) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        if (recorded is null || !SamePath(recorded, o.ExpectedInputPath))
            throw Fail("prepared-mismatch", "Prepared explainer belongs to a different author input path.", inputPath, "draft-path-mismatch");
        var source = Identity(Utf8(sourceText));
        if (bundle.Str("kind") != "archify-explainer-prepared" || bundle.Num("schemaVersion") != 1 || bundle.Str("digest") != Digest(bundle)
            || source.Str("sha256") != bundle["source"].Str("sha256") || source.Num("bytes") != bundle["source"].Num("bytes") || bundle.Str("sourceText") != sourceText)
            throw Fail("prepared-mismatch", "Prepared explainer does not match the original draft bytes.", inputPath, "draft-or-bundle-digest-mismatch");
        if (!string.IsNullOrEmpty(o.Quality) && o.Quality != bundle.Str("quality"))
            throw Fail("prepared-mismatch", "Prepared explainer uses a different quality profile.", inputPath, "quality-profile-mismatch",
                "Prepare the original draft again under the requested quality profile.");
        var writingMode = bundle["writingMode"].Truthy() ? bundle.Str("writingMode") : "warn";
        if (!string.IsNullOrEmpty(o.WritingMode) && o.WritingMode != writingMode)
            throw Fail("prepared-mismatch", "Prepared explainer uses a different writing check mode.", inputPath, "writing-mode-mismatch",
                "Prepare the original draft again under the requested writing check mode.");
        var parsed = ParseValidated(sourceText, bundle.Str("sourcePath"), null, writingMode ?? "warn");
        if (Js.Stringify(parsed.Document) != Js.Stringify(bundle["document"]))
            throw Fail("prepared-mismatch", "Prepared document differs from the authored draft.", inputPath, "normalized-document-mismatch");
        if (o.CheckDependencies) AssertDependencies(bundle);
        return bundle;
    }

    /// <summary>rebindPreparedBundle (:97-101): the bundle with a new parse of its draft, signed again.</summary>
    public static JsonObject Rebind(JsonObject bundle, ExplainerParse parsed)
    {
        var next = bundle.DeepClone().AsObject();
        var json = parsed.ToJson();
        foreach (var key in json.Select(kv => kv.Key).ToList()) next[key] = Take(json, key);
        next["source"] = Identity(Utf8(parsed.SourceText));
        next["digest"] = Digest(next);
        return next;
    }

    /// <summary>validatePreparedTargets (:103-131): every node and route link in the document's text, and every step target,
    /// resolved against the native diagrams. Throws on the first that does not resolve.</summary>
    public static void ValidatePreparedTargets(JsonObject document, JsonObject diagrams, ResourceOptions o)
    {
        void Visit(JsonNode? value, string pointer, string? panelId, string? component)
        {
            switch (value)
            {
                case JsonValue v when v.TryGetValue<string>(out var text):
                    var location = o.SourceMap?[pointer] as JsonObject;
                    var line = location?["line"].Truthy() == true ? location["line"]!.D() : 1;
                    var inline = ExplainerInline.Parse(text, new InlineOptions(line, component, panelId, o.SourcePath));
                    foreach (var reference in inline.References.OfType<JsonObject>())
                    {
                        var kind = reference.Str("kind");
                        if (kind is not ("node" or "route")) continue;
                        var target = kind == "node"
                            ? new JsonObject { ["diagram"] = reference["diagram"]?.DeepClone(), ["node"] = reference["node"]?.DeepClone() }
                            : new JsonObject { ["diagram"] = reference["diagram"]?.DeepClone(), ["route"] = new JsonArray(reference["from"]?.DeepClone(), reference["to"]?.DeepClone()) };
                        var offset = (int)(reference.Num("offset") ?? 0);
                        ExplainerDiagrams.ValidateTarget(diagrams, target, new TargetOptions(o.SourceMap, o.SourcePath, pointer, (int)line + text[..offset].Count(c => c == '\n')));
                    }
                    break;
                case JsonArray array:
                    for (var index = 0; index < array.Count; index++) Visit(array[index], $"{pointer}/{index}", panelId, component);
                    break;
                case JsonObject obj:
                    foreach (var (key, entry) in obj)
                    {
                        if (Skipped.Contains(key) || (component is "diff" or "snippet" && key == "text")) continue;
                        Visit(entry, $"{pointer}/{key}", panelId, component);
                    }
                    break;
            }
        }
        Visit(document["answer"], "/answer", null, "answer");
        foreach (var (panel, index) in document["panels"].AsArrayOrEmpty().Select((p, i) => (p, i)))
        {
            var panelId = panel.Str("id");
            Visit(panel?["title"], $"/panels/{index}/title", panelId, "panel");
            foreach (var (block, blockIndex) in panel.Arr("blocks").AsArrayOrEmpty().Select((b, i) => (b, i)))
            {
                var pointer = $"/panels/{index}/blocks/{blockIndex}";
                Visit(block, pointer, panelId, block.Str("type"));
                if (block.Str("type") != "steps") continue;
                foreach (var (item, itemIndex) in block.Arr("items").AsArrayOrEmpty().Select((x, i) => (x, i)))
                {
                    if (item?["target"].Truthy() != true) continue;
                    var (own, parent) = (o.SourceMap?[$"{pointer}/items/{itemIndex}"]?["line"], o.SourceMap?[pointer]?["line"]);
                    var line = own.Truthy() ? (int)own!.D() : parent.Truthy() ? (int)parent!.D() : 1;   // a?.line || b?.line || 1
                    ExplainerDiagrams.ValidateTarget(diagrams, item!["target"] as JsonObject, new TargetOptions(o.SourceMap, o.SourcePath, $"{pointer}/items/{itemIndex}/target", line));
                }
            }
        }
    }
}
