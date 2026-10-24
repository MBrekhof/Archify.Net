using System.Text;
using System.Text.Json.Nodes;
using Archify.Net.Cli.Paths;

namespace Archify.Net.Cli;

// The explainer input stage validate and deliver share (patch reaches it through deliver).
static class ExplainerInput
{
    // prepareExplainerInput (A:2416-2426 at v3.2.0): the draft's bytes must survive a UTF-8 round trip. A prepared bundle the caller
    // hands in (upstream's ARCHIFY_EXPLAINER_PREPARED, which the CLI never reads from the environment) is checked against the draft;
    // otherwise the draft is prepared. Throws ArchifyDiagnosticException, or PeleException for invalid chart front matter.
    public static JsonObject Prepare(byte[] specification, string inputPath, string? repoRoot, string? quality, string? writingMode, JsonObject? prepared)
    {
        var text = Encoding.UTF8.GetString(specification);
        if (!Encoding.UTF8.GetBytes(text).AsSpan().SequenceEqual(specification))
        {
            const string message = "Explainer draft must contain valid UTF-8 bytes.";
            const string example = "Save the original draft as UTF-8 text.";
            throw new ArchifyDiagnosticException(message, [new("explainer/source-encoding", Severity.Error, message,
                new JsonObject { ["input"] = inputPath, ["line"] = 1, ["component"] = "document" },
                new JsonObject { ["reason"] = "invalid-utf8", ["example"] = example }, [example], null)]);
        }
        return prepared is not null
            ? ExplainerPipeline.ReadPrepared(prepared, text, inputPath, new ReadPreparedOptions(inputPath, Quality: quality, WritingMode: writingMode ?? "warn"))
            : ExplainerPipeline.Prepare(text, new PrepareOptions(inputPath, RepoRoot: repoRoot, Quality: quality, WritingMode: writingMode ?? "warn"));
    }

    // inputDiagnostic (A:2646-2661) for an error that carries no diagnostics, such as Pele's front-matter error. It is never a
    // SyntaxError here: a malformed JSON draft reports explainer/parse-*.
    public static Diagnostic InputDiagnostic(Exception error, string inputPath)
    {
        var evidence = new JsonObject();
        if (ErrorCode(error) is { } code) evidence["systemCode"] = code;
        evidence["reason"] = error.Message;
        return new("input/read", Severity.Error, $"Input could not be read: {error.Message}", new JsonObject { ["input"] = inputPath }, evidence,
            ["provide one readable JSON input file"], null);
    }

    // error.code: Pele's error code, or an fs error's.
    public static string? ErrorCode(Exception error) => error is PeleException pele ? pele.Code : NativeFs.ErrorCode(error);

    // The warnings validate and deliver disclose (A:4890, :7095): specificationLocaleDiagnostics (A:1877-1887), which reads only a
    // JSON-format draft, then the prepared bundle's own warnings as they are.
    public static List<JsonObject> Warnings(byte[] specification, JsonObject bundle)
    {
        var warnings = new List<JsonObject>();
        JsonObject? meta = null;
        try { meta = (DiagramLoader.ParseNode(Encoding.UTF8.GetString(specification)) as JsonObject)?["meta"] as JsonObject; }
        catch (System.Text.Json.JsonException) { }   // a Markdown draft
        if (meta?["locale"].Truthy() == true)
            warnings.AddRange(new LocaleCatalog().LocaleDiagnostics(DiagramType.Explainer, meta).Select(d => d.ToJson()));
        warnings.AddRange(bundle["diagnostics"].AsArrayOrEmpty().OfType<JsonObject>().Where(d => d.Str("severity") == "warning").Select(d => d.DeepClone().AsObject()));
        return warnings;
    }
}
