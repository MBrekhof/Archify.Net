using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Archify.Net.Cli.Delivery;
using Archify.Net.Cli.Paths;

namespace Archify.Net.Cli.Commands;

static partial class DeliverCommand
{
    static readonly string[] KnownOptions = ["--json", "--open"];

    // commandDeliver (A:4362-5240) after the cut (Q5): stages input, prepare, render, check, receipt, commit, release. A failure
    // once the output is known goes through recordDeliveryFailure; one before it, or a lock failure, is reported as is.
    public static int Run(string[] args, CliIo io, DeliveryStore? store = null)
    {
        store ??= DeliveryStore.Native;
        var (afterQuality, quality) = Args.Quality(args);
        var (options, repoRoot) = Args.RepoRoot(afterQuality, io.Cwd);
        var json = options.Contains("--json");
        var open = options.Contains("--open");
        if (options.FirstOrDefault(arg => arg.StartsWith("--", StringComparison.Ordinal) && !KnownOptions.Contains(arg)) is { } unknown)
            throw new CliArgumentException($"Unknown deliver option \"{unknown}\".", "cli/unknown-option", new JsonObject { ["option"] = unknown },
                supportedFixes: ["remove the unknown option and retry"]);
        var positional = options.Where(arg => !KnownOptions.Contains(arg)).ToArray();
        if (positional.Length is < 2 or > 3 || positional[0].Length == 0 || positional[1].Length == 0)
            throw new CliArgumentException(Cli.Usage(), "cli/usage", supportedFixes: ["use: archify deliver <type> <input.json> [output.html] [options]"]);
        var typeName = positional[0];
        var type = Args.DiagramType(typeName);
        // An explicit "" reaches resolveOutputPath (A:4376: output/native-path-syntax, reason empty); the attempted-output spellings
        // read it as `requestedOutput || …`.
        var requestedOutput = positional.Length == 3 ? positional[2] : null;
        var attemptedOutput = string.IsNullOrEmpty(requestedOutput) ? null : requestedOutput;
        var receiptId = io.Services.NewGuid().ToString();
        var inputPath = NodePath.Resolve(io.Cwd, positional[1]);
        DeliveryAttempt? attempt = null;

        ArtifactFailure Failure(string stage, string? output, string error, IReadOnlyList<Diagnostic> diagnostics) =>
            new("deliver", stage, error, diagnostics) { Type = typeName, Input = inputPath, Output = output, ReceiptId = receiptId };

        // reportPreparedArtifactFailure: no record, the lock (if any) untouched.
        int ReportPrepared(ArtifactFailure failure) => Cli.ReportArtifactFailure(io, json, failure);

        // reportDeliveryFailure + writeDeliveryFailureReceipt (A:4275-4326): record the attempt, release the lock, then report.
        int ReportDelivery(ArtifactFailure failure)
        {
            var held = attempt;
            attempt = null;
            var recorded = store.RecordFailure(failure.Output, failure.Stage, inputPath, failure.Error, receiptId, held);
            IReadOnlyList<Diagnostic> extra = recorded.Diagnostic is null ? [] : [recorded.Diagnostic];
            if (recorded.LockError is not { } lockError)
                return Cli.ReportArtifactFailure(io, json, failure with { Provenance = recorded.Status, Diagnostics = [.. failure.Diagnostics, .. extra] });
            var lockDiagnostic = DeliveryStore.LockDiagnostic(failure.Output!, lockError);
            var coded = lockError is DeliveryLockException;
            return Cli.ReportArtifactFailure(io, json, failure with
            {
                Error = coded ? $"Could not safely continue delivery for \"{failure.Output}\": {lockError.Message}" : failure.Error,
                Provenance = recorded.Status != "unrecorded" ? recorded.Status : null,
                Diagnostics = coded ? [lockDiagnostic, .. failure.Diagnostics, .. extra] : [.. failure.Diagnostics, lockDiagnostic, .. extra],
            });
        }

        try
        {
            // 1. input
            byte[] specification;
            JsonObject diagram;
            try { diagram = RenderCommand.ReadDiagram(inputPath, out specification); }
            catch (ArchifyDiagnosticException error)
            {
                var reason = error.Diagnostics[0].Evidence["reason"]!.GetValue<string>();
                return ReportDelivery(Failure("input", NodePath.Resolve(io.Cwd, attemptedOutput ?? $"{typeName}.html"),
                    $"Could not read delivery input \"{inputPath}\": {reason}", error.Diagnostics));
            }

            // 2. prepare: the authored path (validated even when absent, A:4462), the output, its physical spelling.
            var request = new OutputRequest(io.Cwd, requestedOutput, $"{typeName}.html") { InputPaths = [inputPath] }.WithAuthored(diagram);
            var authoredText = request.AuthoredOutput is JsonValue authored && authored.TryGetValue<string>(out var text) ? text : null;
            string? outputPath = null;
            try
            {
                OutputPaths.Native.ValidateAuthoredOutputPath(request.AuthoredOutput, io.Cwd);
                outputPath = OutputPaths.Native.ResolveOutputPath(request).OutputPath;
                outputPath = OutputPaths.Native.CanonicalFuturePath(outputPath);
            }
            catch (Exception error)
            {
                var attempted = NodePath.Resolve(io.Cwd, attemptedOutput ?? (string.IsNullOrEmpty(authoredText) ? $"{typeName}.html" : authoredText));
                var (diagnostics, spread) = PathDiagnostics(attempted, error, request.HasAuthoredOutput);
                var failure = Failure("prepare", attempted, error.Message, diagnostics) with { SpreadOrdered = spread };
                return outputPath is null ? ReportPrepared(failure) : ReportDelivery(failure);
            }

            var outputDirectory = Path.GetDirectoryName(outputPath)!;
            try { Directory.CreateDirectory(outputDirectory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                var message = $"Could not create delivery directory \"{outputDirectory}\": {error.Message}";
                var evidence = new JsonObject();
                if (NativeFs.ErrorCode(error) is { } code) evidence["systemCode"] = code;
                evidence["reason"] = error.Message;
                return ReportDelivery(Failure("prepare", outputPath, message, [new("delivery/prepare-directory", Severity.Error, message,
                    new JsonObject { ["outputDirectory"] = outputDirectory }, evidence, ["choose a writable output directory"], null)]));
            }

            // The provenance path, once the directory exists for the namespace probe (A:4532-4565).
            try
            {
                var defaultProvenance = DeliveryStore.ProvenancePath(outputPath);
                OutputPaths.Native.ResolveOutputPath(new OutputRequest(io.Cwd, defaultProvenance, defaultProvenance)
                {
                    RequiredExtension = ".json", InputPaths = [inputPath], InputDescription = "the delivery input", OtherOutputPaths = [outputPath],
                });
            }
            catch (Exception error)
            {
                var attempted = NodePath.Resolve(io.Cwd, attemptedOutput ?? (string.IsNullOrEmpty(authoredText) ? $"{typeName}.html" : authoredText));
                var (diagnostics, spread) = PathDiagnostics(attempted, error, authoredDefined: true);
                var failure = Failure("prepare", attempted, error.Message, diagnostics) with { SpreadOrdered = spread };
                return error is SidecarNamespaceException ? ReportPrepared(failure) : ReportDelivery(failure);
            }

            // 3. the directory lock; an existing one is classified and left alone.
            try { attempt = store.Acquire(outputPath, receiptId, inputPath); }
            catch (Exception error)
            {
                return ReportPrepared(Failure("prepare", outputPath, $"Could not start delivery for \"{outputPath}\": {error.Message}",
                    [DeliveryStore.LockDiagnostic(outputPath, error)]));
            }

            // 4. the journal, before render.
            try { store.BeginJournal(attempt, inputPath); }
            catch (Exception error)
            {
                const string message = "Could not persist the delivery attempt before rendering.";
                return ReportDelivery(Failure("prepare", outputPath, message, [new("delivery/journal-write", Severity.Error, message,
                    new JsonObject { ["output"] = outputPath, ["journal"] = attempt.PendingPath }, new JsonObject { ["reason"] = error.Message },
                    ["choose a writable output directory with a journal path distinct from the input"], null)]));
            }

            // 5. render, in memory.
            var result = ArchifyNet.Render(type, diagram, Args.RenderOptions(quality, repoRoot));
            if (result.Error is not null)
                return ReportDelivery(Failure("render", outputPath, result.Error, Cli.WithInput(result.Diagnostics, inputPath)));
            var localeWarnings = result.Diagnostics.Where(d => d.Code.StartsWith("i18n/", StringComparison.Ordinal)).ToList();
            foreach (var warning in localeWarnings) io.Err($"archify: {warning.Message}");

            // 6. check, on the bytes to be committed.
            var artifact = new UTF8Encoding(false).GetBytes(result.Html);
            var check = store.Check(artifact);
            if (!check.Ok)
            {
                var checker = check.Report;
                checker.Insert(1, "file", outputPath);   // the checker's own `file` key sits at index 1 upstream
                return ReportDelivery(Failure("check", outputPath, "Final artifact check failed; the previous artifact was preserved.",
                    CheckerDiagnostics.From(checker)) with { Checker = checker });
            }

            // 7. receipt
            JsonObject? sourceEvidence;
            try { sourceEvidence = SourceEvidence(result.Html); }
            catch (Exception error) when (error is System.Text.Json.JsonException or FormatException)
            {
                var message = $"Could not read the repository evidence receipt: {error.Message}";
                return ReportDelivery(Failure("receipt", outputPath, message, [new("delivery/evidence-receipt-invalid", Severity.Error, message,
                    new JsonObject { ["output"] = outputPath }, new JsonObject { ["reason"] = error.Message }, [], null)]));
            }
            var receipt = Receipt(receiptId, typeName, inputPath, outputPath, specification, artifact, check.Report, result.Html, sourceEvidence, localeWarnings);

            // 8-9. commit: the artifact, then its provenance; the journal goes, then the backup.
            try { store.Commit(attempt, artifact, DeliveryStore.SuccessProvenance(receipt)); }
            catch (Exception error)
            {
                var message = $"Could not commit verified delivery \"{outputPath}\": {error.Message}";
                var evidence = new JsonObject();
                if (NativeFs.ErrorCode(error) is { } code) evidence["systemCode"] = code;
                evidence["reason"] = error.Message;
                foreach (var (key, value) in (error as DeliveryCommitException)?.Details ?? []) evidence[key] = value?.DeepClone();
                return ReportDelivery(Failure("commit", outputPath, message, [new("delivery/commit", Severity.Error, message,
                    new JsonObject { ["output"] = outputPath }, evidence,
                    ["choose replaceable regular-file artifact and provenance targets on the same writable filesystem"], null)]));
            }

            // 10. release
            var committed = attempt;
            attempt = null;
            try { DeliveryStore.Release(committed); }
            catch (DeliveryLockException error)
            {
                return ReportPrepared(Failure("release", outputPath, $"Delivery committed, but its lock could not be released for \"{outputPath}\": {error.Message}",
                    [DeliveryStore.LockDiagnostic(outputPath, error)]));
            }

            // D9: no launcher; upstream's fallback when open-artifact.mjs is unavailable (A:5173-5188).
            if (open)
            {
                receipt["open"] = new JsonObject { ["requested"] = true, ["status"] = "unsupported", ["target"] = outputPath, ["method"] = null };
                io.Err($"Could not open the verified artifact (unsupported). Open it manually. Target: {outputPath}");
            }

            if (json) io.Out(Js.Stringify(receipt, 2));
            else
            {
                var validation = receipt["validation"]!;
                var engineering = validation["engineeringProfile"] is { } profile ? $"; engineering {profile.GetValue<string>()}: pass" : "";
                io.Out($"delivered {typeName} {outputPath}");
                io.Out($"{Js.Num(validation["checksPassed"]!.D())}/{Js.Num(validation["checkCount"]!.D())} artifact checks; composition " +
                    $"{validation["compositionProfile"]!.GetValue<string>()}: {validation["compositionStatus"]!.GetValue<string>()}{engineering}; " +
                    $"sha256 {receipt["artifact"]!["sha256"]!.GetValue<string>()[..12]}");
            }
            return 0;
        }
        finally
        {
            // ponytail: only an unexpected exception gets here holding the lock; release it quietly and let the exception report.
            if (attempt is not null)
                try { DeliveryStore.Release(attempt); }
                catch (DeliveryLockException) { }
        }
    }

    // A prepare-stage path failure's diagnostics: an OutputPathError's own (serialised in spread order), or the output/path-resolution
    // fallback. deliver validates meta.output even when it is undefined, and JSON then drops the subject's undefined `output`.
    static (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyCollection<Diagnostic> Spread) PathDiagnostics(string attempted, Exception error, bool authoredDefined)
    {
        if (error is ArchifyDiagnosticException { Diagnostics.Count: > 0 } pathError)
        {
            List<Diagnostic> diagnostics = [.. pathError.Diagnostics.Select(d => authoredDefined || !d.Code.StartsWith("output/meta-", StringComparison.Ordinal) ? d
                : d with { Subject = WithoutOutput(d.Subject) })];
            return (diagnostics, diagnostics);
        }
        var evidence = new JsonObject();
        if (DeliveryStore.SystemCode(error) is { } code) evidence["systemCode"] = code;
        return ([new("output/path-resolution", Severity.Error, error.Message, new JsonObject { ["output"] = attempted }, evidence,
            ["choose a safe output path and retry"], null)], []);
    }

    static JsonObject WithoutOutput(JsonObject subject)
    {
        var copy = subject.DeepClone().AsObject();
        copy.Remove("output");
        return copy;
    }

    // The success receipt (A:4952-4990), without `update` (Q5).
    static JsonObject Receipt(string receiptId, string type, string input, string output, byte[] specification, byte[] artifact, JsonObject checker,
        string html, JsonObject? sourceEvidence, List<Diagnostic> localeWarnings)
    {
        var checks = checker["checks"]!.AsArray();
        var composition = checker["composition"]!;
        var summary = composition["summary"]!;
        var validation = new JsonObject
        {
            ["checksPassed"] = checks.Count(c => c!["ok"].Truthy()), ["checkCount"] = checks.Count,
            ["compositionProfile"] = composition["profile"]?.DeepClone(), ["compositionStatus"] = composition["status"]?.DeepClone(),
        };
        if (ValidateCommand.EngineeringProfile().Match(html) is { Success: true } profile) validation["engineeringProfile"] = profile.Groups[1].Value;
        validation["errors"] = summary["errors"]?.DeepClone();
        validation["warnings"] = summary["warnings"]?.DeepClone();
        if (summary["warnings"].Truthy())
            validation["compositionIssues"] = new JsonArray([.. composition["issues"]!.AsArray().Where(i => i!["severity"]?.GetValue<string>() == "warning").Select(i => i!.DeepClone())]);
        var receipt = new JsonObject
        {
            ["schemaVersion"] = 1, ["receiptId"] = receiptId, ["ok"] = true, ["command"] = "deliver", ["type"] = type, ["input"] = input, ["output"] = output,
            ["specification"] = Receipts.Identity(specification), ["artifact"] = Receipts.Identity(artifact), ["validation"] = validation,
        };
        if (sourceEvidence is not null)
        {
            var repository = sourceEvidence["repository"]!;
            var evidence = new JsonObject
            {
                ["verified"] = true, ["repository"] = repository["url"]!.DeepClone(), ["revision"] = repository["revision"]!.DeepClone(),
                ["references"] = sourceEvidence["referenceCount"]!.DeepClone(),
            };
            if (repository["linkMode"].Truthy()) evidence["linkMode"] = repository["linkMode"]!.DeepClone();
            receipt["evidence"] = evidence;
        }
        if (localeWarnings.Count > 0) receipt["diagnostics"] = new JsonArray([.. localeWarnings.Select(d => (JsonNode)d.ToJson())]);
        return receipt;
    }

    [GeneratedRegex("""<script id="archify-source-evidence-data" type="application/json">([\s\S]*?)</script>""")]
    private static partial Regex SourceEvidenceData();

    // sourceEvidenceFromArtifact (A:4346-4355)
    static JsonObject? SourceEvidence(string html)
    {
        var match = SourceEvidenceData().Match(html);
        if (!match.Success) return null;
        var evidence = DiagramLoader.ParseNode(match.Groups[1].Value) as JsonObject;
        var repository = evidence?["repository"] as JsonObject;
        var verified = evidence?["verified"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
        var referenceCount = evidence?["referenceCount"] is JsonValue count && count.GetValueKind() == System.Text.Json.JsonValueKind.Number
            && count.GetValue<double>() is var number && number == Math.Floor(number) && double.IsFinite(number);
        var url = (repository?["url"]).Truthy();
        var revision = (repository?["revision"]).Truthy();
        if (!verified || !url || !revision || !referenceCount)
            throw new FormatException("Rendered source evidence receipt is incomplete.");
        return evidence;
    }
}
