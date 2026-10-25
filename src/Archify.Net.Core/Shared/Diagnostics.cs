using System.Text.Json.Nodes;

namespace Archify.Net;

public enum Severity { Error, Warning }

public sealed record Diagnostic(string Code, Severity Severity, string Message, JsonObject Subject, JsonObject Evidence,
    IReadOnlyList<string> SupportedFixes, IReadOnlyList<string>? Suppresses)
{
    public string? Path => Subject["path"]?.GetValue<string>();

    // {code, severity, message, subject, evidence, supportedFixes, [suppresses]}: the order of normalizedDiagnostic
    // (diagnostics.mjs:16-30) and the CLI's diagnostic() (A:2399-2408); `suppresses` only when present.
    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["code"] = Code, ["severity"] = Severity == Severity.Error ? "error" : "warning", ["message"] = Message,
            ["subject"] = Subject.DeepClone(), ["evidence"] = Evidence.DeepClone(), ["supportedFixes"] = new JsonArray([.. SupportedFixes.Select(f => (JsonNode?)f)]),
        };
        if (Suppresses is not null) json["suppresses"] = new JsonArray([.. Suppresses.Select(c => (JsonNode?)c)]);
        return json;
    }

    // diagnostics.mjs:11-31 plainObject + normalizedDiagnostic. JsonObject has no `undefined`, so port authors omit a key JS
    // would leave undefined; explicit nulls (upstream's `x ?? null`) are kept. Subject/Evidence become fresh objects as
    // plainObject's do, which here also keeps one diagnostic recorded and thrown from sharing a JsonNode parent.
    internal static Diagnostic Normalize(Diagnostic diagnostic) => new(
        string.IsNullOrEmpty(diagnostic.Code) ? "internal/unclassified" : diagnostic.Code,
        diagnostic.Severity == Severity.Warning ? Severity.Warning : Severity.Error,
        Js.Trim(string.IsNullOrEmpty(diagnostic.Message) ? "Archify could not classify this failure." : diagnostic.Message),
        PlainObject(diagnostic.Subject),
        PlainObject(diagnostic.Evidence),
        UniqueTrimmed(diagnostic.SupportedFixes) ?? [],
        UniqueTrimmed(diagnostic.Suppresses));

    static JsonObject PlainObject(JsonObject? value) => value?.DeepClone().AsObject() ?? new JsonObject();

    // [...new Set(values.map((v) => String(v).trim()).filter(Boolean))]
    static IReadOnlyList<string>? UniqueTrimmed(IReadOnlyList<string>? values)
    {
        if (values is null) return null;
        var set = new JsSet<string>();
        foreach (var value in values) { var trimmed = Js.Trim(value); if (trimmed.Length > 0) set.Add(trimmed); }
        return [.. set];
    }
}

// The `error.archifyDiagnostics` carrier. Library code throws it; the Archify.* facade catches it and returns diagnostics.
public sealed class ArchifyDiagnosticException(string message, IReadOnlyList<Diagnostic> diagnostics) : Exception(message)
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;
}

// diagnostics.mjs:1-132, one instance per render instead of module state. Recording is always on (upstream's
// ARCHIFY_DIAGNOSTIC_FORMAT=json mode, the path receipts come from). installRendererDiagnosticBoundary is not ported:
// the facade catches ArchifyDiagnosticException instead of a process-level handler.
public sealed class DiagnosticSink
{
    List<Diagnostic> recorded = [];
    HashSet<string> recordedKeys = new(StringComparer.Ordinal);
    Dictionary<string, Diagnostic> recordedByMessage = new(StringComparer.Ordinal);   // lookup only, never enumerated
    int recordingSuppressionDepth;

    public IReadOnlyList<Diagnostic> Recorded => recorded;

    public void Record(Diagnostic diagnostic)
    {
        if (recordingSuppressionDepth > 0) return;
        var normalized = Diagnostic.Normalize(diagnostic);
        // Identical messages can describe different subjects; only exact repeats dedupe.
        var key = $"{normalized.Code} {normalized.Message} {Js.Stringify(normalized.Subject)}";
        if (!recordedKeys.Add(key)) return;
        recorded.Add(normalized);
        recordedByMessage.TryAdd(normalized.Message, normalized);
    }

    public T WithRecordingSuppressed<T>(Func<T> callback)
    {
        recordingSuppressionDepth += 1;
        try { return callback(); }
        finally { recordingSuppressionDepth -= 1; }
    }

    // Speculative synchronous work needs typed message lookup without publishing
    // its discarded diagnostics into the caller's eventual renderer failure.
    public T WithIsolatedRecording<T>(Func<T> callback)
    {
        var (previousRecorded, previousKeys, previousByMessage) = (recorded, recordedKeys, recordedByMessage);
        recorded = [];
        recordedKeys = new(StringComparer.Ordinal);
        recordedByMessage = new(StringComparer.Ordinal);
        try { return callback(); }
        finally { (recorded, recordedKeys, recordedByMessage) = (previousRecorded, previousKeys, previousByMessage); }
    }

    // Returns the exception so call sites read `throw sink.ThrowDiagnosticError(...)` and flow analysis sees the throw.
    public Exception ThrowDiagnosticError(string message, IEnumerable<Diagnostic> diagnostics)
    {
        var list = diagnostics.ToList();
        foreach (var diagnostic in list) Record(diagnostic);
        return new ArchifyDiagnosticException(message, [.. list.Select(Diagnostic.Normalize)]);
    }

    public Exception ThrowDiagnosticProblems(string prefix, IEnumerable<string> problems, string code = "layout/constraint",
        JsonObject? subject = null, IEnumerable<Diagnostic>? details = null)
    {
        var messages = problems.ToList();
        // Consume matching details in emission order: repeated messages map to
        // different subjects, so a single lookup would attribute every occurrence to
        // the last detail entry.
        var queuesByMessage = new Dictionary<string, Queue<Diagnostic>>(StringComparer.Ordinal);
        foreach (var entry in details ?? [])
        {
            if (queuesByMessage.TryGetValue(entry.Message, out var queue)) queue.Enqueue(entry);
            else queuesByMessage[entry.Message] = new Queue<Diagnostic>([entry]);
        }
        var diagnostics = messages.Select(message =>
        {
            // Without a queued detail, reuse the structured diagnostic already
            // recorded under this message rather than attaching a bare fallback.
            var entry = queuesByMessage.TryGetValue(message, out var queue) && queue.Count > 0
                ? queue.Dequeue()
                : recordedByMessage.GetValueOrDefault(message);
            return Diagnostic.Normalize(entry ?? new Diagnostic(code, Severity.Error, message, subject ?? new JsonObject(), new JsonObject(), [], null));
        }).ToList();
        return ThrowDiagnosticError($"{prefix}:\n- {string.Join("\n- ", messages)}", diagnostics);
    }

    // rendererFailure without the { schemaVersion, ok, source } envelope; the facade adds it. `thrown` is what the human-mode
    // boundary prints (diagnostics.mjs:159-176): the attached diagnostics, never the recorded ones. Upstream lets an error
    // without any crash with a Node stack; the port keeps the internal/unclassified fallback there.
    public (string error, IReadOnlyList<Diagnostic> diagnostics, IReadOnlyList<Diagnostic> thrown) RendererFailure(Exception error)
    {
        IReadOnlyList<Diagnostic> attached = error is ArchifyDiagnosticException carrier ? [.. carrier.Diagnostics.Select(Diagnostic.Normalize)] : [];
        // Earlier diagnostics do not classify a later, unrelated implementation error.
        IReadOnlyList<Diagnostic> diagnostics = attached.Count > 0
            ? (recorded.Count > 0 ? recorded : attached)
            : [FallbackDiagnostic(error)];
        return (string.IsNullOrEmpty(error.Message) ? "Renderer failed without a diagnostic." : error.Message, diagnostics, attached.Count > 0 ? attached : diagnostics);
    }

    static Diagnostic FallbackDiagnostic(Exception error) => Diagnostic.Normalize(new(
        "internal/unclassified",
        Severity.Error,
        string.IsNullOrEmpty(error.Message) ? "Renderer failed without a diagnostic." : error.Message,
        new JsonObject(),   // upstream's subject.input is the CLI's argv[2] path: undefined in a library call, so omitted
        new JsonObject { ["errorName"] = error switch { JsError => "Error", PeleException => "PeleError", _ => error.GetType().Name } },   // JsError is upstream's plain `new Error(...)`, PeleException its `PeleError`
        [],
        null));
}
