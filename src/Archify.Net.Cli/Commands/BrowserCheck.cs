using System.Text.Json.Nodes;
using Archify.Net.Cli.Browser;
using Archify.Net.Cli.Delivery;
using Archify.Net.Cli.Paths;

namespace Archify.Net.Cli.Commands;

// browser-check and visual-check: commandBrowserEvidence (A:5620-5704) over executeBrowserEvidence (A:5547-5618). visual-check is the
// same command with captures.
static class BrowserEvidenceCommand
{
    static readonly string[] KnownOptions = ["--json", "--summary", "--require-provenance"];

    public static int Run(string[] rawArgs, CliIo io, string command, bool capture)
    {
        var (args, rawOutDir) = Args.OutDir(rawArgs);
        var summary = args.Contains("--summary");
        var json = args.Contains("--json") || summary;
        void PrintJson(JsonObject receipt, bool published) =>
            io.Out(summary ? Js.Stringify(BrowserReceipt.Summarize(receipt, published)) : Js.Stringify(receipt, 2));
        void PrintFailure(JsonObject receipt, string review)
        {
            io.Err(FormatDiagnostics($"automated browser evidence failed: {receipt["error"]?.GetValue<string>()}", receipt["diagnostics"]!.AsArray()));
            io.Err($"perceptual visual review {review}");
        }
        if (args.FirstOrDefault(arg => arg.StartsWith("--", StringComparison.Ordinal) && !KnownOptions.Contains(arg)) is { } unknown)
            throw new CliFailException($"Unknown {command} option \"{unknown}\".", 1);
        var positional = args.Where(arg => !KnownOptions.Contains(arg)).ToArray();
        if (positional.Length != 1) throw new CliFailException(Cli.Usage(), 1);

        var artifactPath = NodePath.Resolve(io.Cwd, positional[0]);
        string? outDir = null;
        if (rawOutDir is not null)
        {
            try { outDir = OutputPaths.ResolveNativeOutputDirectory(rawOutDir, io.Cwd); }
            catch (ArchifyDiagnosticException error)
            {
                var failure = Failure(command, capture, artifactPath);
                failure["error"] = error.Message;
                // an OutputPathError's archifyDiagnostics, in their spread key order
                failure["diagnostics"] = new JsonArray(ArtifactFailure.SpreadJson(error.Diagnostics[0]));
                if (json) PrintJson(failure, false);
                else PrintFailure(failure, "pending");
                return 1;
            }
        }

        // ponytail: blocking on the async engine; Task.Run keeps it off any caller's synchronization context.
        var (result, inputFailure) = Task.Run(() => ExecuteAsync(artifactPath, outDir, args.Contains("--require-provenance"), command, capture,
            io.Services.BrowserInspectors)).GetAwaiter().GetResult();
        var receipt = result.Receipt;
        if (json) PrintJson(receipt, result.Published);
        else if (inputFailure) PrintFailure(receipt, capture ? "pending" : "not requested");
        else
        {
            var sidecarDirectory = outDir ?? Path.GetDirectoryName(receipt["artifact"]!["path"]!.GetValue<string>())!;
            io.Out($"automated browser evidence {receipt["status"]}: {receipt["artifact"]!["path"]}");
            io.Out($"{command} containment {receipt["containment"]!["status"]}; captures {receipt["captures"]!["status"]}; perceptual visual review {receipt["visualReview"]}");
            io.Out($"receipt {Path.Join(sidecarDirectory, receipt["sidecars"]!["receipt"]!.GetValue<string>())}");
            if (receipt["captures"]!["contactSheet"]?.GetValue<string>() is { Length: > 0 } sheet) io.Out($"contact sheet {Path.Join(sidecarDirectory, sheet)}");
            if (receipt["error"]?.GetValue<string>() is { Length: > 0 } error) io.Err(error);
        }
        return result.ExitCode;
    }

    // executeBrowserEvidence (A:5547-5618): the delivery provenance gate before any browser, then the engine with the gate's verifier;
    // an input failure the engine throws becomes the viewer/<command>-input receipt. Both refusals are persisted, exit 1. Task 12's
    // finalize runs its browser-check stage through here with requireProvenance (A:5821-5823).
    internal static async Task<(BrowserEvidenceResult Result, bool InputFailure)> ExecuteAsync(string artifactPath, string? outDir, bool requireProvenance,
        string command, bool capture, IBrowserInspectorFactory browsers)
    {
        var provenance = DeliveryStore.InspectProvenance(artifactPath, requireProvenance);
        if (!provenance.Ok)
            return (BrowserEvidence.PersistFailure(artifactPath, CheckCommand.ProvenanceFailureReceipt(command, artifactPath, provenance), outDir, command, capture), true);
        try
        {
            return (await BrowserEvidence.RunAsync(new BrowserEvidenceRequest(artifactPath, command, capture)
            {
                OutDir = outDir, DeliveryProvenance = provenance, VerifyArtifact = BrowserEvidence.DeliveryVerifier(artifactPath, provenance),
            }, browsers), false);
        }
        catch (Exception error) when (error is BrowserEvidenceException or IOException or UnauthorizedAccessException)
        {
            var failure = Failure(command, capture, artifactPath, provenance);
            failure["error"] = error.Message;
            var evidence = new JsonObject { ["reason"] = error.Message };
            if (((error as BrowserEvidenceException)?.Code ?? DeliveryStore.SystemCode(error)) is { } code) evidence["systemCode"] = code;
            failure["diagnostics"] = new JsonArray(BrowserReceipt.Failure($"viewer/{command}-input", $"{command} could not read a valid HTML input or prepare its evidence files.",
                new JsonObject { ["artifact"] = artifactPath }, evidence, ["provide an existing readable .html artifact and a writable directory for evidence files"]).ToJson());
            return (BrowserEvidence.PersistFailure(artifactPath, failure, outDir, command, capture), true);
        }
    }

    // The failure receipt's head (A:5594-5604, :5651-5658).
    static JsonObject Failure(string command, bool capture, string artifactPath, Provenance? provenance = null)
    {
        var failure = new JsonObject
        {
            ["schemaVersion"] = 1, ["ok"] = false, ["command"] = command, ["evidenceKind"] = "automated-browser", ["status"] = "fail",
            ["visualReview"] = capture ? "pending" : "not-requested",
        };
        if (provenance is not null)
        {
            failure["provenance"] = provenance.Status;
            if (provenance.ReceiptId is { } receiptId) failure["deliveryReceiptId"] = receiptId;
        }
        failure["artifact"] = new JsonObject { ["path"] = artifactPath };
        return failure;
    }

    // formatDiagnostics (A:2749-2758) over a receipt's diagnostics.
    static string FormatDiagnostics(string error, JsonArray diagnostics) =>
        diagnostics.Count == 0 ? error : string.Join("\n", [error, .. diagnostics.Select(d =>
        {
            var fixes = d!["supportedFixes"] as JsonArray ?? [];
            return $"[{d["code"]}] {d["message"]}{(fixes.Count > 0 ? $" Fix: {string.Join("; ", fixes.Select(f => f!.GetValue<string>()))}." : "")}";
        })]);
}
