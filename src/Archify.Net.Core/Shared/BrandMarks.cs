using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Archify.Net;

public sealed record BrandMark(string Id, string Title, string Category, string[] Aliases, string[] Domains, double ViewBox, string Hex,
    string Path, JsonObject Provenance);

// { ...preset, kind: 'preset', status: 'preset', sourceUrl: preset.provenance.source }
public sealed record ResolvedBrand(BrandMark Mark, string SourceUrl, string Kind = "preset", string Status = "preset");

// brand-marks.mjs: the built-in catalog, lookup and SVG. Remote capture (normalizeBrandSourceUrl, isPrivateBrandAddress,
// validateUrlShape, captureBrandReference, remoteBrand, mapConcurrent, captureTimeoutMilliseconds, ARCHIFY_BRAND_*) is not
// ported: Core never touches the network, so a digest-pinned brand object reports brand/capture-unavailable.
public static class BrandMarks
{
    static readonly List<BrandMark> Marks = [.. JsonNode.Parse(Resources.Text("generated-brand-marks.json"))!.AsArray().Select(n => new BrandMark(
        n.Str("id")!, n.Str("title")!, n.Str("category")!,
        [.. n.Arr("aliases")!.Select(a => a!.GetValue<string>())], [.. n.Arr("domains")!.Select(a => a!.GetValue<string>())],
        n.Num("viewBox")!.Value, n.Str("hex")!, n.Str("path")!, n.Obj("provenance")!))];
    static readonly Dictionary<string, BrandMark> MarkByLookup = new(StringComparer.Ordinal);   // lookup only, never enumerated
    static readonly JsMap<string, BrandMark> MarkByDomain = new();

    static BrandMarks()
    {
        foreach (var mark in Marks)
        {
            foreach (var value in (string[])[mark.Id, mark.Title, .. mark.Aliases])
                foreach (var form in LookupForms(value)) MarkByLookup.TryAdd(form, mark);
            foreach (var domain in mark.Domains) MarkByDomain.Set(domain, mark);
        }
    }

    // [raw, raw.replace(/[\s_]+/g, '-'), raw.replace(/[\s_.-]+/g, '')], deduplicated in order. toLocaleLowerCase('en-US') equals
    // toLowerCase for every code point (Node v24), hence Js.ToLowerCase here and in DomainMark.
    static List<string> LookupForms(string value)
    {
        var raw = Js.ToLowerCase(Js.Trim(value));
        if (raw.Length == 0) return [];
        var forms = new List<string> { raw };
        foreach (var form in (string[])[CollapseRuns(raw, c => Js.IsSpace(c) || c == '_', "-"), CollapseRuns(raw, c => Js.IsSpace(c) || c is '_' or '.' or '-', "")])
            if (!forms.Contains(form)) forms.Add(form);
        return forms;
    }

    static string CollapseRuns(string value, Func<char, bool> member, string replacement)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (!member(value[i])) { sb.Append(value[i]); continue; }
            while (i + 1 < value.Length && member(value[i + 1])) i++;
            sb.Append(replacement);
        }
        return sb.ToString();
    }

    // new URL(value) restricted to http(s): a WHATWG pre-pass (strip C0/space at both ends, drop TAB/CR/LF, any run of
    // slashes or backslashes after the special scheme, `\` as `/` before the query, whitespace in the host is a failure),
    // then System.Uri. ponytail: WHATWG URL subset — full parser if a golden ever needs it. Outcomes match; the href in the
    // unpinned-url message can differ on percent-encoding (System.Uri escapes `\` in query/fragment as %5C) and IDN hosts.
    internal static Uri? AsUrl(string? value)
    {
        if (value is null) return null;
        int start = 0, end = value.Length;
        while (start < end && value[start] <= 0x20) start++;
        while (end > start && value[end - 1] <= 0x20) end--;
        var s = value[start..end].Replace("\t", "").Replace("\r", "").Replace("\n", "");
        var colon = s.IndexOf(':');
        var scheme = colon < 0 ? "" : s[..colon].ToLowerInvariant();
        if (scheme is not ("http" or "https")) return null;
        var rest = s[(colon + 1)..].TrimStart('/', '\\');
        var queryAt = rest.IndexOfAny(['?', '#']);
        if (queryAt < 0) queryAt = rest.Length;
        rest = rest[..queryAt].Replace('\\', '/') + rest[queryAt..];
        var hostEnd = rest.IndexOfAny(['/', '?', '#']);
        if (rest[..(hostEnd < 0 ? rest.Length : hostEnd)].Any(c => Js.IsSpace(c))) return null;
        return Uri.TryCreate($"{scheme}://{rest}", UriKind.Absolute, out var url) ? url : null;
    }

    static BrandMark? DomainMark(string hostname)
    {
        var host = Js.ToLowerCase(hostname);
        if (host.EndsWith('.')) host = host[..^1];
        var candidates = MarkByDomain.Entries.Where(e => host == e.Key || host.EndsWith($".{e.Key}", StringComparison.Ordinal)).ToList();
        Js.Sort(candidates, (left, right) => right.Key.Length - left.Key.Length);
        return candidates.Count > 0 ? candidates[0].Value : null;
    }

    public static BrandMark? FindBrandMark(string value)
    {
        var url = AsUrl(value);
        if (url is not null) return DomainMark(url.IdnHost);
        foreach (var form in LookupForms(value))
            if (MarkByLookup.TryGetValue(form, out var mark)) return mark;
        return null;
    }

    // ponytail: listBrandMarks(query) filtering and its `path` omission serve only the `archify brands` CLI, not ported.
    public static IReadOnlyList<BrandMark> ListBrandMarks() => Marks;

    public static IReadOnlyList<string> Suggestions(string input)
    {
        var forms = LookupForms(input);
        var needle = forms.Count > 0 ? forms[0] : "";
        var scored = Marks.Select(mark => (mark.Id, Score: LookupForms(mark.Id).Any(form => form.Contains(needle, StringComparison.Ordinal)
            || needle.Contains(form, StringComparison.Ordinal)) ? 0 : 1)).ToList();
        Js.Sort(scored, (left, right) => left.Score != right.Score ? left.Score - right.Score : Js.LocaleCompare(left.Id, right.Id));
        return [.. scored.Take(5).Select(entry => entry.Id)];
    }

    public static JsonObject? BrandMetadataFor(ResolvedBrand? mark) => mark is null ? null : new JsonObject
    {
        ["brand"] = mark.Mark.Title,
        ["brandId"] = mark.Mark.Id,
        ["brandStatus"] = mark.Status,
        ["brandSource"] = mark.SourceUrl,
    };

    public static double BrandLabelFitWidth(ResolvedBrand? mark, double width) => mark is not null ? Math.Max(1, width - 48) : width;

    // Upstream reads node.id and node.label; the caller passes them beside the resolved mark. A missing label measures as
    // '' (textUnits(undefined)) and prints as `undefined`, exactly as the template literal does (brand-marks.mjs:671-674).
    public static string? BrandTopRailProblem(ResolvedBrand? mark, string id, string? label, double width, double minimumFontSize, string subject = "Node")
    {
        if (mark is null) return null;
        var available = width - 48;
        var required = Utils.TextUnits(label) * minimumFontSize * 0.6;
        if (available >= required) return null;
        return $"{subject} \"{id}\" brand top rail leaves {Js.Num(Math.Max(0, available))}px for its label, but "
            + $"\"{label ?? "undefined"}\" needs ~{Js.Num(Math.Ceiling(required))}px at the {Js.Num(minimumFontSize)}px legible minimum — widen the node or shorten the label.";
    }

    static string MarkAttrs(ResolvedBrand mark) => string.Join(" ", ((string[])[
        $"data-brand-mark=\"{Utils.Esc(mark.Mark.Id)}\"",
        $"data-brand-title=\"{Utils.Esc(mark.Mark.Title)}\"",
        $"data-brand-status=\"{Utils.Esc(mark.Status)}\"",
        mark.SourceUrl.Length > 0 ? $"data-brand-source=\"{Utils.Esc(mark.SourceUrl)}\"" : "",
    ]).Where(s => s.Length > 0));

    // ponytail: only the preset branch. ResolvedBrand can carry nothing but a built-in mark, so upstream's remote (<image>)
    // and fallback (globe glyph) branches are unreachable; port them with remote capture.
    public static string RenderBrandMark(ResolvedBrand mark, double x, double y, double size = 16)
    {
        const int inset = 3;
        var scale = (size - inset * 2) / mark.Mark.ViewBox;
        var content = $"<path d=\"{Utils.Esc(mark.Mark.Path)}\" transform=\"translate(3 3) scale({Js.Num(scale)})\" fill=\"#{Utils.Esc(mark.Mark.Hex)}\"/>";
        var s = Js.Num(size);
        return $"<g aria-hidden=\"true\" {MarkAttrs(mark)} class=\"brand-mark\" transform=\"translate({Js.Num(x)} {Js.Num(y)})\">\n"
            + $"            <rect width=\"{s}\" height=\"{s}\" rx=\"4\" class=\"brand-mark-badge\"/>\n"
            + $"            {content}\n"
            + $"            <rect width=\"{s}\" height=\"{s}\" rx=\"4\" class=\"brand-mark-frame\"/>\n"
            + "          </g>";
    }
}

// Per-render replacement for upstream's RESOLVED_BY_NODE WeakMap and RESOLVED_MARK symbol: keyed by the authored node.
public sealed class BrandMarkResolver
{
    readonly JsMap<JsonObject, ResolvedBrand> resolvedByNode = new();

    static string? Collection(DiagramType type) => type switch
    {
        DiagramType.Architecture => "components", DiagramType.Workflow => "nodes", DiagramType.Sequence => "participants",
        DiagramType.Dataflow => "nodes", DiagramType.Lifecycle => "states", _ => null,
    };

    // Synchronous prepareDiagramBrandMarks. Upstream's capture workers run nodes in index order up to the first network
    // await, so with no remote capture every problem lands in index order.
    public void PrepareDiagramBrandMarks(DiagramType type, JsonObject diagram, DiagnosticSink sink)
    {
        var collection = Collection(type);
        var nodes = collection is not null && diagram[collection] is JsonArray array ? array : [];
        var unknown = new List<string>();
        for (var index = 0; index < nodes.Count; index++)
        {
            if (nodes[index] is not JsonObject node) continue;
            var brand = node["brand"];
            if (!brand.Truthy()) continue;
            if (brand is JsonObject or JsonArray)
            {
                // Upstream fetches the pinned URL here; Core reports instead. A missing/invalid URL keeps upstream's reason.
                var pinned = BrandMarks.AsUrl(brand.Str("url"));
                unknown.Add($"/{collection}/{Js.Num(index)}/brand could not reproduce the pinned capture: "
                    + (pinned is null ? "invalid URL" : "remote capture is not available in Archify.Net.Core"));
                continue;
            }
            var value = Js.PrimitiveString(brand);
            var preset = BrandMarks.FindBrandMark(value);
            if (preset is not null)
            {
                resolvedByNode.Set(node, new ResolvedBrand(preset, preset.Provenance.Str("source")!));
                continue;
            }
            var url = BrandMarks.AsUrl(value);
            if (url is not null)
            {
                unknown.Add($"/{collection}/{Js.Num(index)}/brand {Js.Stringify(brand)} is an unpinned URL; capture it first with `archify brands capture {url.AbsoluteUri} --json`");
                continue;
            }
            unknown.Add($"/{collection}/{Js.Num(index)}/brand {Js.Stringify(brand)} is not a built-in brand; closest IDs: {string.Join(", ", BrandMarks.Suggestions(value))}");
        }
        if (unknown.Count == 0) return;
        throw sink.ThrowDiagnosticError($"Brand mark validation failed:\n- {string.Join("\n- ", unknown)}", unknown.Select(message => new Diagnostic(
            message.Contains("is an unpinned URL", StringComparison.Ordinal) ? "brand/unpinned-url"
                : message.Contains("digest changed", StringComparison.Ordinal) ? "brand/digest-mismatch"
                : message.Contains("could not reproduce", StringComparison.Ordinal) ? "brand/capture-unavailable" : "brand/unknown",
            Severity.Error,
            message,
            new JsonObject { ["diagramType"] = type.Name(), ["collection"] = collection },
            new JsonObject(),
            message.Contains("is an unpinned URL", StringComparison.Ordinal)
                ? ["run `archify brands capture <url> --json` and author the returned digest-pinned brand object"]
                : ["choose an ID from `archify brands`", "run `archify brands capture <url> --json` for an unknown official site"],
            null)));
    }

    public ResolvedBrand? BrandMarkFor(JsonObject authoredNode) => resolvedByNode.Get(authoredNode);
}
