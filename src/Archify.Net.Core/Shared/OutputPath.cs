using System.Text.Json.Nodes;

namespace Archify.Net;

// output-path.mjs validateAuthoredOutputPath (:323-357), lexical subset only: no cwd, no filesystem. The resolved-extension
// (meta-resolved-extension) and symlink-aware containment (meta-outside-cwd) checks, resolveOutputPath, path-semantics and
// sidecar-path live in the CLI (Archify.Net.Cli/Paths). Lexical containment needs no code of its own: portable-path already rejects
// absolute paths (output/meta-absolute) and `.`/`..` segments (output/meta-path-syntax), so a path that passes stays inside.
public static class OutputPath
{
    public static void ValidateAuthoredOutputPath(string raw, DiagnosticSink sink) => ValidateAuthoredOutputPath(JsonValue.Create(raw), sink);

    // cli.mjs:79-80 hands over whatever meta.output holds; a non-string fails portable-path's 'type' reason.
    public static void ValidateAuthoredOutputPath(JsonNode? raw, DiagnosticSink sink)
    {
        var rawOutput = raw is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        try { PortablePath.Validate(rawOutput!, "output"); }
        catch (PortablePathException error) { throw AuthoredOutputDiagnostic(error, raw, sink); }

        if (!string.Equals(PosixExtname(rawOutput!), ".html", StringComparison.OrdinalIgnoreCase))   // only ASCII h/t/m/l fold onto "html"
            throw sink.ThrowDiagnosticError("meta.output must target an .html file.", [new(
                "output/meta-extension", Severity.Error, "meta.output must target an .html file.",
                new JsonObject { ["output"] = rawOutput, ["path"] = "/meta/output" }, new JsonObject(),
                ["change meta.output to a portable path ending in .html"], null)]);
    }

    // output-path.mjs:76-96
    static Exception AuthoredOutputDiagnostic(PortablePathException error, JsonNode? raw, DiagnosticSink sink)
    {
        var absolute = error.Reason == "absolute";
        var message = absolute
            ? "meta.output must be a relative path resolved from the current working directory."
            : "meta.output must be a portable POSIX-relative path.";
        var evidence = new JsonObject { ["reason"] = error.Reason };
        if (error.SegmentIndex is int segmentIndex) evidence["segmentIndex"] = segmentIndex;
        if (error.Segment is string segment) evidence["segment"] = segment;
        if (error.Utf8Bytes is int utf8Bytes) evidence["utf8Bytes"] = utf8Bytes;
        if (error.Utf16CodeUnits is int utf16CodeUnits) evidence["utf16CodeUnits"] = utf16CodeUnits;
        if (error.Limit is int limit) evidence["limit"] = limit;
        return sink.ThrowDiagnosticError(message, [new(
            absolute ? "output/meta-absolute" : "output/meta-path-syntax", Severity.Error, message,
            new JsonObject { ["output"] = raw?.DeepClone(), ["path"] = "/meta/output" }, evidence,
            ["set meta.output to a portable relative .html path such as reports/diagram.html"], null)]);
    }

    // path.posix.extname for a path portable-path accepted (no empty segments, so no trailing slash): ".html" → "", "..html" → ".html".
    static string PosixExtname(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? "" : name[dot..];
    }
}
