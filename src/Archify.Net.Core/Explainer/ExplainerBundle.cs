using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace Archify.Net;

// archify 3.2.0 explainer/revision.mjs (the authored-content identity) and explainer/page-bundle.mjs (the page's packed native
// children).
internal static class ExplainerBundle
{
    internal static string Hash(string text) => ExplainerSources.Sha256(Encoding.UTF8.GetBytes(text));   // a lone surrogate as EF BF BD

    // createHash().update(value) on a value that is not a string: Node's ERR_INVALID_ARG_TYPE TypeError (an undefined child html).
    static string HashOf(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? Hash(s)
        : throw new ArgumentException("The \"data\" argument must be of type string or an instance of Buffer, TypedArray, or DataView. Received undefined");

    /// <summary>stable (revision.mjs:10): object keys sorted by UTF-16 code units, recursively. Object.fromEntries then lists
    /// array-index keys first, ascending, as V8 orders any object.</summary>
    internal static JsonNode? Stable(JsonNode? value) => value switch
    {
        JsonArray a => new JsonArray([.. a.Select(Stable)]),
        JsonObject o => new JsonObject(o.Where(kv => ExplainerCharts.IsArrayIndex(kv.Key)).OrderBy(kv => uint.Parse(kv.Key))
            .Concat(o.Where(kv => !ExplainerCharts.IsArrayIndex(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            .Select(kv => KeyValuePair.Create(kv.Key, Stable(kv.Value)))),
        _ => value?.DeepClone(),
    };

    static void Copy(JsonObject to, JsonNode? from, params string[] keys)
    {
        if (from is not JsonObject source) return;
        foreach (var key in keys) if (source.TryGetPropertyValue(key, out var value)) to[key] = value?.DeepClone();
    }

    /// <summary>computeExplainerRevision (revision.mjs:5-20) over {document, sourceText, preparedSources, preparedDiagrams,
    /// dependencies}, a prepared bundle being one such object.</summary>
    public static string Revision(JsonObject args) =>
        Revision(args["document"], args.Str("sourceText"), args.Obj("preparedSources"), args.Obj("preparedDiagrams"), args["dependencies"]);

    internal static string Revision(JsonNode? document, string? sourceText, JsonObject? preparedSources, JsonObject? preparedDiagrams, JsonNode? dependencies)
    {
        preparedSources ??= [];
        preparedDiagrams ??= [];
        var sources = (preparedSources["sources"].Truthy() ? preparedSources["sources"] : preparedSources) as JsonObject ?? [];
        var assets = (preparedSources["assets"].Truthy() ? preparedSources["assets"] : null) as JsonObject ?? [];
        var diagrams = (preparedDiagrams["diagrams"].Truthy() ? preparedDiagrams["diagrams"] : preparedDiagrams) as JsonObject ?? [];
        var entries = dependencies.Truthy() ? dependencies.AsArrayOrEmpty()
            : preparedSources["dependencies"].AsArrayOrEmpty().Concat(preparedDiagrams["dependencies"].AsArrayOrEmpty());
        var identities = entries.Select(entry => { var identity = new JsonObject(); Copy(identity, entry, "kind", "id", "sha256", "bytes"); return Stable(identity); }).ToList();
        Js.Sort(identities, (a, b) => string.CompareOrdinal(Js.Stringify(a), Js.Stringify(b)));
        var sourceIdentities = new JsonObject();
        foreach (var (id, source) in sources)
        {
            var identity = new JsonObject();
            Copy(identity, source, "title", "url", "path", "lines", "revision");
            if (source?["text"] is JsonValue text && text.TryGetValue<string>(out var s)) identity["textSha256"] = Hash(s);
            sourceIdentities[id] = identity;
        }
        var children = new JsonObject();
        foreach (var (id, child) in diagrams)
            children[id] = new JsonObject
            {
                ["htmlSha256"] = HashOf(child?["html"]),
                ["specSha256"] = Hash(Js.Stringify(Stable(child?["spec"].Truthy() == true ? child["spec"] : new JsonObject()))),
            };
        var assetIdentities = new JsonObject();
        foreach (var (id, asset) in assets)
        {
            var identity = new JsonObject();
            Copy(identity, asset, "sha256");
            identity["dataSha256"] = asset?["dataUrl"].Truthy() == true ? HashOf(asset["dataUrl"]) : Hash("");
            Copy(identity, asset, "mime", "bytes");
            assetIdentities[id] = identity;
        }
        var resourceIdentity = new JsonObject
        {
            ["version"] = 1, ["sourceSha256"] = Hash(sourceText ?? Js.Stringify(document)), ["documentSha256"] = Hash(Js.Stringify(Stable(document))),
            ["dependencies"] = new JsonArray([.. identities]), ["sources"] = sourceIdentities, ["children"] = children, ["assets"] = assetIdentities,
        };
        return Hash(Js.Stringify(Stable(resourceIdentity)));
    }

    /// <summary>The prepared bundle's digest (prepare.mjs:14): sha256 of the bundle without its digest.</summary>
    public static string Digest(JsonObject bundle) => ExplainerPrepare.Digest(bundle);

    // swap (page-bundle.mjs:6-11): each native child's `from` string becomes `to`, converted, at the same key position.
    static JsonObject Swap(JsonObject bundle, string from, string to, Func<string, string> convert)
    {
        var copy = bundle.DeepClone().AsObject();
        if (copy["preparedDiagrams"]?["diagrams"] is not JsonObject diagrams) return copy;
        foreach (var (id, node) in diagrams.ToList())
        {
            if (node is not JsonObject child || child[from] is not JsonValue v || !v.TryGetValue<string>(out var value)) continue;
            var swapped = new JsonObject();
            foreach (var (key, entry) in child) swapped[key == from ? to : key] = key == from ? convert(value) : entry?.DeepClone();   // fromEntries: a later duplicate wins in place
            diagrams[id] = swapped;
        }
        return copy;
    }

    /// <summary>packPageBundle: each child's `html` becomes `htmlGzip`, GZip level 9 in base64.</summary>
    /// <remarks>UPSTREAM-DEVIATION gzip-stream (Decision 1): .NET's zlib writes a different, equally valid level-9 stream from
    /// Node's, so pages are compared by the inflated payload.</remarks>
    public static JsonObject Pack(JsonObject bundle) => Swap(bundle, "html", "htmlGzip", html =>
    {
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, new ZLibCompressionOptions { CompressionLevel = 9 }, leaveOpen: true)) gzip.Write(Encoding.UTF8.GetBytes(html));
        return Convert.ToBase64String(packed.ToArray());
    });

    /// <summary>unpackPageBundle: packed and older unpacked pages alike. Throws InvalidDataException on a payload that does not
    /// inflate, FormatException on one that is not base64.</summary>
    public static JsonObject Unpack(JsonObject bundle) => Swap(bundle, "htmlGzip", "html", data =>
    {
        using var gzip = new GZipStream(new MemoryStream(Convert.FromBase64String(data)), CompressionMode.Decompress);
        using var inflated = new MemoryStream();
        gzip.CopyTo(inflated);
        return Encoding.UTF8.GetString(inflated.ToArray());   // toString('utf8'): invalid bytes as U+FFFD
    });
}
