using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Archify.Net;

// One element of scanHtml (check.mjs:39): its tag (lower case), attributes (lower-cased names, decoded values, a repeated
// name keeps its first position and takes the last value), 1-based line and UTF-16 offset.
internal record ScannedElement(string Tag, Dictionary<string, string> Attrs, int Line, int Index);
// A <script> element and its raw text.
internal sealed record ScannedScript(string Tag, Dictionary<string, string> Attrs, int Line, int Index, string Text) : ScannedElement(Tag, Attrs, Line, Index);

// archify 3.2.0 explainer/check.mjs: the explainer page's artifact checker. Each native child is checked by the native
// ArtifactChecker in process, where upstream runs scripts/check-render-output.mjs on a temp file.
internal static class ExplainerCheck
{
    // JS regexes by hand (no `u`, `/i` folds ASCII only): `\s` is the JS class, `\w` and `\b` are ASCII, `$` is \z.
    const string Ws = Js.WsClass;
    static readonly string WsIn = Js.WsClass[1..^1];
    const string Word = "[A-Za-z0-9_]";
    static string Ci(string literal) => ExplainerSources.Ci(literal);

    static readonly Regex Family = new($"<{Ci("html")}(?!{Word})[^>]*(?<!{Word}){Ci("data-archify-artifact")}{Ws}*={Ws}*[\"']{Ci("explainer")}[\"']");
    static readonly Regex Token = new($"<!--[\\s\\S]*?(?:-->|\\z)|<![^>]*>|<(/?)([A-Za-z][A-Za-z0-9_:-]*)(?=[{WsIn}/>])(?:[^>\"']|\"[^\"]*\"|'[^']*')*>");
    static readonly Regex Attr = new($"([^{WsIn}=<>\"'`/]+)(?:{Ws}*={Ws}*(?:\"([^\"]*)\"|'([^']*)'|([^{WsIn}>]+)))?");
    static readonly Regex Entity = new($"&(?:{Ci("amp")}|{Ci("lt")}|{Ci("gt")}|{Ci("quot")}|{Ci("apos")}|#39|#[xX][0-9A-Fa-f]+|#[0-9]+);");
    static readonly Dictionary<string, Regex> RawEnds = new[] { "script", "style", "textarea", "title" }.ToDictionary(tag => tag, tag => new Regex($"</{Ci(tag)}{Ws}*>"));
    static readonly Regex UnsafeUrl = new($"^(?:{Ci("javascript")}|{Ci("vbscript")}):");
    static readonly Regex DataImage = new($"^{Ci("data:image/")}(?:{Ci("png")}|{Ci("jpeg")}|{Ci("webp")}|{Ci("gif")});{Ci("base64")},");
    static readonly Regex Revision = new("^[0-9a-f]{40}\\z");
    static readonly Regex UnsafePayload = new(@"[<>&  ]");

    /// <summary>isExplainerHtml (check.mjs:11): the family marker on &lt;html&gt;, or the prepared bundle's payload script.</summary>
    public static bool IsExplainerHtml(string html) => Family.IsMatch(html)
        || (html.Contains("archify-explainer-prepared", StringComparison.Ordinal) && Scan(html).Scripts.Any(s => s.Attrs.GetValueOrDefault("id") == "archify-explainer-prepared"));

    // decode (check.mjs:13-18)
    static string Decode(string value) => Entity.Replace(value, m =>
    {
        var token = m.Value;
        var named = token.ToLowerInvariant() switch { "&amp;" => "&", "&lt;" => "<", "&gt;" => ">", "&quot;" => "\"", "&apos;" or "&#39;" => "'", _ => null };
        if (named is not null) return named;
        double n = 0;
        if (token[2] is 'x' or 'X') foreach (var c in token[3..^1]) n = n * 16 + Convert.ToInt32(c.ToString(), 16);
        else foreach (var c in token[2..^1]) n = n * 10 + (c - '0');
        return n > 0 && n <= 0x10ffff ? (n < 0x10000 ? ((char)n).ToString() : char.ConvertFromUtf32((int)n)) : "�";   // fromCodePoint keeps a lone surrogate
    });

    /// <summary>scanHtml (check.mjs:22-51): a bounded token scanner. Quoted attributes, comments and raw-text elements are consumed
    /// whole, so markup inside a JSON payload is never an element.</summary>
    public static (List<ScannedElement> Elements, List<ScannedScript> Scripts) Scan(string html)
    {
        var elements = new List<ScannedElement>();
        var scripts = new List<ScannedScript>();
        int line = 1, lineOffset = 0, at = 0;
        Match match;
        while (at <= html.Length && (match = Token.Match(html, at)).Success)
        {
            at = match.Index + match.Length;
            if (!match.Groups[2].Success || match.Groups[1].Length > 0) continue;
            var name = match.Groups[2].Value;
            var tag = name.ToLowerInvariant();   // ASCII
            var attrs = new Dictionary<string, string>(StringComparer.Ordinal);
            var start = match.Value.IndexOf(name, StringComparison.Ordinal) + name.Length;
            var raw = match.Value[start..^1];
            foreach (Match a in Attr.Matches(raw))
                attrs[Js.ToLowerCase(a.Groups[1].Value)] = Decode(a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Success ? a.Groups[4].Value : "");
            for (var newline = html.IndexOf('\n', lineOffset); newline >= 0 && newline < match.Index; newline = html.IndexOf('\n', newline + 1)) line++;
            lineOffset = match.Index;
            elements.Add(new(tag, attrs, line, match.Index));
            if (RawEnds.TryGetValue(tag, out var end))
            {
                var closing = end.Match(html, at);
                var text = html[at..(closing.Success ? closing.Index : html.Length)];
                if (tag == "script") scripts.Add(new(tag, attrs, line, match.Index, text));
                at = closing.Success ? closing.Index + closing.Length : html.Length;
            }
        }
        return (elements, scripts);
    }

    static string Hash(string text) => ExplainerBundle.Hash(text);
    static JsonNode? P(JsonNode? node, string key) => node is JsonObject o ? o[key] : null;   // property access; undefined off a non-object
    // `${value[key]}`: a missing key is undefined.
    static string Tpl(JsonNode? node, string key) => node is JsonObject o && o.TryGetPropertyValue(key, out var v) ? Js.String(v) : "undefined";
    static bool Is(JsonNode? value, string text) => value is JsonValue v && v.TryGetValue<string>(out var s) && s == text;
    static bool IsNum(JsonNode? value, double n) => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.D() == n;
    // `===` on JSON values. ponytail: undefined and null are one (a JsonNode null); objects are never identical here.
    static bool Same(JsonNode? a, JsonNode? b) => (a, b) switch
    {
        (null, null) => true,
        (JsonValue x, JsonValue y) => x.GetValueKind() == y.GetValueKind() && (x.GetValueKind() == JsonValueKind.Number ? x.D() == y.D() : JsonNode.DeepEquals(x, y)),
        _ => false,
    };

    // Buffer.from(b64, 'base64').toString('base64') === b64 holds exactly for canonical base64, which .NET decodes strictly.
    static byte[]? Canonical(string base64)
    {
        var bytes = new byte[base64.Length];
        return Convert.TryFromBase64String(base64, bytes, out var length) && Convert.ToBase64String(bytes, 0, length) == base64 ? bytes[..length] : null;
    }

    /// <summary>checkExplainerHtml (check.mjs:53-166) over the artifact's bytes, read as check-render-output.mjs reads a file
    /// (invalid UTF-8 as U+FFFD); the artifact identity is over the decoded text's UTF-8, as upstream's Buffer.from(html).</summary>
    public static JsonObject Check(byte[] html, string file = "<html>", bool checkNative = true)
    {
        var text = Encoding.UTF8.GetString(html);
        var bytes = Encoding.UTF8.GetBytes(text);
        var (elements, scripts) = Scan(text);
        var diagnostics = new JsonArray();
        var checks = new JsonArray();
        void Issue(string code, string reason, string example, ScannedElement? element = null, string component = "document")
        {
            var subject = new JsonObject { ["input"] = file, ["line"] = element?.Line ?? 1, ["component"] = component };
            if (element?.Attrs.GetValueOrDefault("id") is { Length: > 0 } elementId) subject["id"] = elementId;
            diagnostics.Add(new JsonObject
            {
                ["code"] = $"explainer/{code}", ["severity"] = "error", ["message"] = reason, ["subject"] = subject,
                ["evidence"] = new JsonObject { ["reason"] = reason, ["example"] = example }, ["supportedFixes"] = new JsonArray(example),
            });
        }
        void Add(string name, int before, params string[] details) =>
            checks.Add(new JsonObject { ["name"] = name, ["ok"] = diagnostics.Count == before, ["details"] = new JsonArray([.. details.Select(d => (JsonNode?)d)]) });

        var before = diagnostics.Count;
        var htmlElement = elements.FirstOrDefault(e => e.Tag == "html");
        if (htmlElement?.Attrs.GetValueOrDefault("data-archify-artifact") != "explainer")
            Issue("artifact-family", "The document is missing its explainer family marker.", "<html data-archify-artifact=\"explainer\">");
        var ids = new Dictionary<string, ScannedElement>(StringComparer.Ordinal);
        foreach (var e in elements)
        {
            if (e.Attrs.GetValueOrDefault("id") is { Length: > 0 } id)
            {
                if (ids.ContainsKey(id)) Issue("duplicate-id", $"Duplicate DOM ID: {id}.", "Give each panel, block, source and SVG preview a unique stable ID.", e);
                ids[id] = e;
            }
            if (e.Attrs.Keys.Any(key => key.StartsWith("on", StringComparison.Ordinal))) Issue("inline-handler", "Inline event attributes are not permitted.", "Use the packaged reader event handlers.", e);
            if (e.Tag == "script" && e.Attrs.GetValueOrDefault("src") is { Length: > 0 }) Issue("external-runtime", "The explainer runtime must be self-contained.", "Embed the packaged reader script.", e);
            if (e.Tag is "iframe" or "object" or "embed") Issue("external-frame", "Frames must be created from immutable native payloads on demand.", "Keep a static native SVG preview and the frozen child HTML payload.", e);
            foreach (var attr in new[] { "href", "src", "xlink:href" })
                if (e.Attrs.GetValueOrDefault(attr) is { Length: > 0 } value && UnsafeUrl.IsMatch(value))
                    Issue("unsafe-url", "An executable URL is not allowed.", "Use an https URL or a stable #anchor.", e);
            if (e.Tag == "img" && !DataImage.IsMatch(e.Attrs.GetValueOrDefault("src") ?? ""))
                Issue("external-image", "Images must contain their frozen local bytes.", "Use a prepared local image with a data:image URL.", e, "image");
        }
        foreach (var e in elements)
            if (e.Attrs.GetValueOrDefault("href") is { Length: > 1 } href && href[0] == '#' && !ids.ContainsKey(Js.DecodeUriComponent(href[1..]) ?? href[1..]))
                Issue("unresolved-reference", $"Unresolved anchor: {href}.", "Link to an existing panel, block or source ID.", e, "reference");
        Add("explainer_structure", before, $"{ids.Count} unique DOM IDs");

        JsonNode? Payload(string id)
        {
            var found = scripts.Where(s => s.Attrs.GetValueOrDefault("id") == id).ToList();
            if (found.Count != 1 || found[0].Attrs.GetValueOrDefault("type") != "application/json")
            {
                Issue("payload", $"Expected one application/json payload: {id}.", $"<script type=\"application/json\" id=\"{id}\">…</script>", found.FirstOrDefault());
                return null;
            }
            if (UnsafePayload.IsMatch(found[0].Text)) Issue("payload-escaping", $"Unsafe raw characters in {id}.", "Escape <, >, &, U+2028 and U+2029 as JSON Unicode escapes.", found[0]);
            var parsed = ExplainerJson.Parse(found[0].Text, out string? error);
            if (error is null) return parsed;
            Issue("payload-json", $"Invalid JSON payload: {id}.", "Re-render the validated source instead of editing embedded payloads.", found[0]);
            return null;
        }
        before = diagnostics.Count;
        var bundle = Payload("archify-explainer-prepared");
        try { if (bundle is JsonObject packed) bundle = ExplainerBundle.Unpack(packed); }
        catch (Exception)
        {
            Issue("native-hash", "A compressed native diagram payload cannot be inflated.", "Re-render the native diagram and freeze its original HTML bytes.");
            bundle = null;
        }
        var document = P(bundle, "document");
        var manifest = Payload("explainer-manifest");
        var children = P(P(bundle, "preparedDiagrams"), "diagrams");
        if (bundle.Truthy() && (!Is(P(bundle, "kind"), "archify-explainer-prepared") || !IsNum(P(bundle, "schemaVersion"), 1)
            || !Is(P(bundle, "digest"), ExplainerPrepare.Digest((JsonObject)bundle!))))
            Issue("bundle-hash", "The canonical prepared bundle does not match its digest.", "Re-prepare and render the original draft with immutable resources.");
        var sourceText = P(bundle, "sourceText") is JsonValue sv && sv.TryGetValue<string>(out var st) ? st : null;
        if (bundle.Truthy() && sourceText is not null)
        {
            var parsed = ExplainerParser.Parse(sourceText, P(bundle, "sourcePath") is JsonValue pv && pv.TryGetValue<string>(out var sp) ? sp : null,
                P(P(document, "meta"), "output") is JsonValue ov && ov.TryGetValue<string>(out var output) ? output : null);
            if (!parsed.Document.Truthy() || parsed.Diagnostics.Any(d => d.Severity != Severity.Warning) || Js.Stringify(parsed.Document) != Js.Stringify(document))
                Issue("source-document-binding", "The canonical document differs from its embedded authored draft.", "Re-parse the embedded draft and render its normalized document.");
            if (!Is(P(P(bundle, "source"), "sha256"), Hash(sourceText)) || !IsNum(P(P(bundle, "source"), "bytes"), Encoding.UTF8.GetByteCount(sourceText)))
                Issue("bundle-source-hash", "The prepared source bytes do not match their identity.", "Freeze the exact original UTF-8 draft bytes.");
        }
        if (document.Truthy() && manifest.Truthy() && bundle.Truthy())
        {
            if (!Is(P(document, "diagram_type"), "explainer") || !IsNum(P(document, "schema_version"), 1) || P(document, "panels") is not JsonArray || P(document, "sources") is not JsonArray)
                Issue("canonical-document", "The canonical explainer envelope is invalid.", "Use schema_version:1, diagram_type:\"explainer\", panels:[] and sources:[].");
            if (sourceText is null || !Is(P(manifest, "sourceSha256"), Hash(sourceText)))
                Issue("source-hash", "The embedded source does not match its immutable source hash.", "Re-render the original draft and retain its exact SHA-256 source hash.");
            try
            {
                if (!Is(P(manifest, "revision"), ExplainerBundle.Revision((JsonObject)bundle!)))
                    Issue("content-revision", "The content revision does not bind the draft and frozen resources.", "Recompute the revision from the canonical draft, source locations, child diagrams and contained assets.");
            }
            catch (Exception)
            {
                Issue("content-revision", "The content revision cannot be computed from invalid frozen resources.", "Re-prepare the original draft and every declared resource.");
            }
            if (!Is(P(manifest, "documentSha256"), Hash(Js.Stringify(document))))
                Issue("document-hash", "The canonical document does not match its manifest hash.", "Re-render after changing the canonical document.");
            var revision = htmlElement?.Attrs.GetValueOrDefault("data-revision");
            if (!Same(P(manifest, "artifactId"), P(P(document, "meta"), "id")) || !Same(revision is null ? null : JsonValue.Create(revision), P(manifest, "revision")))
                Issue("artifact-identity", "Artifact ID or revision disagrees with the manifest.", "Use the renderer-generated identity and revision.");
            foreach (var panel in P(document, "panels").AsArrayOrEmpty())
                foreach (var item in new[] { panel }.Concat(P(panel, "blocks").AsArrayOrEmpty()))
                    if (!(P(item, "id") is JsonValue iv && iv.TryGetValue<string>(out var itemId) && ids.ContainsKey(itemId)))
                        Issue("canonical-id", $"Canonical ID is absent from the rendered document: {Tpl(item, "id")}.", "Render every authored panel and block with its stable ID.");
            foreach (var source in P(document, "sources").AsArrayOrEmpty())
                if (!ids.ContainsKey($"source-{Tpl(source, "id")}"))
                    Issue("source-target", $"Source target is missing: {Tpl(source, "id")}.", "Render every declared source in the source list.");
        }
        Add("explainer_payload_identity", before);

        before = diagnostics.Count;
        var dependencies = P(bundle, "dependencies");
        var entries = dependencies.AsArrayOrEmpty().ToList();
        if (P(manifest, "dependencies").Truthy())
        {
            var compact = new JsonArray([.. entries.Select(entry => (JsonNode?)new JsonObject((entry as JsonObject ?? []).Where(kv => kv.Key != "contentBase64")
                .Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone()))))]);
            if (Js.Stringify(P(manifest, "dependencies")) != Js.Stringify(compact))
                Issue("manifest-dependencies", "The compact manifest disagrees with the canonical dependency identities.", "Generate dependency metadata from the canonical prepared bundle.");
        }
        if (dependencies is not JsonArray) Issue("dependency-manifest", "The frozen dependency manifest is missing.", "Store dependencies:[] even when no external resources are used.");
        var dependencyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in entries)
        {
            var key = $"{Tpl(dependency, "kind")}:{Tpl(dependency, "id")}";
            if (!dependencyIds.Add(key)) Issue("dependency-duplicate", $"Duplicate dependency: {key}.", "Freeze each resource once.");
            var frozen = P(dependency, "contentBase64") is JsonValue cv && cv.TryGetValue<string>(out var content) ? Canonical(content) : null;
            var size = P(dependency, "bytes");
            if (frozen is null || !(size is JsonValue nv && nv.GetValueKind() == JsonValueKind.Number && Math.Abs(nv.D()) <= 9007199254740991 && double.IsInteger(nv.D()))
                || nv.D() != frozen.Length || !Is(P(dependency, "sha256"), ExplainerSources.Sha256(frozen)))
                Issue("dependency-hash", $"Frozen dependency identity is corrupt: {key}.", "Regenerate the dependency bytes, byte count and SHA-256 from the source.");
        }
        foreach (var (id, asset) in P(manifest, "assets") as JsonObject ?? [])
        {
            var dependency = entries.FirstOrDefault(d => Is(P(d, "kind"), "image") && Is(P(d, "id"), id));
            if (dependency is null || !Same(P(dependency, "sha256"), P(asset, "sha256")) || !Same(P(dependency, "bytes"), P(asset, "bytes")))
                Issue("asset-binding", $"Image asset is not bound to its frozen dependency: {id}.", "Freeze the image before rendering.");
            var src = $"data:{Tpl(asset, "mime")};base64,{Tpl(dependency, "contentBase64")}";
            if (!elements.Any(e => e.Tag == "img" && e.Attrs.GetValueOrDefault("src") == src))
                Issue("asset-content", $"Rendered image bytes differ from the frozen asset: {id}.", "Render the frozen data URL without replacing its bytes.");
        }
        foreach (var (id, source) in P(P(bundle, "preparedSources"), "sources") as JsonObject ?? [])
        {
            if (!(P(source, "text") is JsonValue tv && tv.TryGetValue<string>(out _))) continue;
            var dependency = entries.FirstOrDefault(d => Is(P(d, "kind"), "repository") && Is(P(d, "id"), id));
            if (dependency is null || !Same(P(dependency, "sha256"), P(source, "sha256")) || !Same(P(dependency, "bytes"), P(source, "bytes"))
                || !Revision.IsMatch(P(source, "revision").Truthy() ? Js.String(P(source, "revision")) : "") || !Same(P(dependency, "revision"), P(source, "revision")))
                Issue("source-binding", $"Code excerpt is missing pinned frozen bytes: {id}.", "Resolve the repository source against a full Git commit before rendering.");
        }
        Add("explainer_dependencies", before, $"{(dependencies is JsonArray all ? all.Count : 0)} frozen dependencies");

        before = diagnostics.Count;
        var receipts = new JsonArray();
        var details = new List<string>();
        var expected = P(document, "panels").AsArrayOrEmpty().SelectMany(p => P(p, "blocks").AsArrayOrEmpty())
            .Where(b => Is(P(b, "type"), "diagram")).Select(b => Tpl(b, "id")).ToList();
        if (children is not JsonObject childMap || expected.Count != childMap.Count || expected.Any(id => !childMap[id].Truthy()))
            Issue("native-count", "Native diagram payloads do not match canonical diagram blocks.", "Embed one native payload per diagram block; zero diagrams is valid.");
        var profile = P(bundle, "quality").Truthy() ? P(bundle, "quality")!.DeepClone()
            : P(P(document, "meta"), "quality_profile").Truthy() ? P(P(document, "meta"), "quality_profile")!.DeepClone() : "standard";
        foreach (var (id, child) in children as JsonObject ?? [])
        {
            var binding = P(P(manifest, "children"), id);
            var childHtml = P(child, "html") is JsonValue hv && hv.TryGetValue<string>(out var h) ? h : null;
            if (childHtml is null || !Is(P(child, "sha256"), Hash(childHtml)) || !IsNum(P(child, "bytes"), Encoding.UTF8.GetByteCount(childHtml))
                || !Same(P(binding, "sha256"), P(child, "sha256")) || !Same(P(binding, "bytes"), P(child, "bytes")))
            {
                Issue("native-hash", $"Native diagram payload is corrupt: {id}.", "Re-render the native diagram and freeze its original HTML bytes.", ids.GetValueOrDefault(id), "diagram");
                continue;
            }
            if (IsExplainerHtml(childHtml))
            {
                Issue("native-family", $"Nested explainers are not native diagrams: {id}.", "Use an existing Archify native diagram type.", ids.GetValueOrDefault(id), "diagram");
                continue;
            }
            if (!checkNative) continue;
            // UPSTREAM-DEVIATION in-process-check (stated): check.mjs writes the child to <tmp>/archify-explainer-check-XXXXXX/child.html
            // and runs check-render-output.mjs on it; the port checks the bytes in process and names that path unwritten.
            var directory = Path.Join(Path.GetTempPath(), "archify-explainer-check-" + RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 6));
            var receipt = ArtifactChecker.Check(Encoding.UTF8.GetBytes(childHtml));
            receipt.Insert(1, "file", Path.Join(directory, "child.html"));
            receipts.Add(new JsonObject { ["id"] = id, ["receipt"] = receipt });
            var ok = receipt["ok"].Truthy();
            details.Add($"{id}: {(ok ? "pass" : "fail")}");
            if (!ok || !Same(P(P(receipt, "artifact"), "sha256"), P(child, "sha256")) || !Same(P(P(receipt, "composition"), "profile"), profile))
                Issue("native-check", $"Native diagram failed its existing composition checks: {id}.", "Fix the nested diagram using the native checker diagnostics.", ids.GetValueOrDefault(id), "diagram");
        }
        Add("explainer_native_diagrams", before, [.. details]);
        var errors = diagnostics.Count;
        return new JsonObject
        {
            ["ok"] = errors == 0, ["file"] = file, ["family"] = "explainer",
            ["artifact"] = new JsonObject { ["sha256"] = ExplainerSources.Sha256(bytes), ["bytes"] = bytes.Length },
            ["checks"] = checks,
            ["composition"] = new JsonObject
            {
                ["schemaVersion"] = 1, ["profile"] = profile, ["status"] = errors > 0 ? "fail" : "pass",
                ["summary"] = new JsonObject { ["errors"] = errors, ["warnings"] = 0 }, ["issues"] = diagnostics.DeepClone(), ["nativeDiagrams"] = receipts,
            },
            ["diagnostics"] = diagnostics,
        };
    }
}
