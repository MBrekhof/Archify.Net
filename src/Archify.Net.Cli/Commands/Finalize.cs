using System.Text.Json.Nodes;
using Archify.Net.Cli.Delivery;
using Archify.Net.Cli.Finalize;
using Archify.Net.Cli.Paths;

namespace Archify.Net.Cli.Commands;

static class FinalizeCommand
{
    static readonly string[] KnownOptions = ["--json"];

    // commandFinalize (A:5772-5863): runFinalize with the CLI's delivery paths and provenance inspection, the compact summary on one
    // line with --json, else four lines and the warnings. A run that cannot start answers with a "could not start" receipt, exit 1.
    public static int Run(string[] rawArgs, CliIo io)
    {
        var (afterQuality, quality) = Args.Quality(rawArgs);
        var (afterRepoRoot, repoRoot) = Args.RepoRoot(afterQuality, io.Cwd);
        var (afterOutDir, outDir) = Args.OutDir(afterRepoRoot);
        var (afterReceipt, receiptPath) = Args.Receipt(afterOutDir);
        var (args, candidateSha256) = Args.CandidateSha256(afterReceipt);
        var json = args.Contains("--json");
        if (args.FirstOrDefault(arg => arg.StartsWith("--", StringComparison.Ordinal) && !KnownOptions.Contains(arg)) is { } unknown)
            throw new CliArgumentException($"Unknown finalize option \"{unknown}\".", "cli/unknown-option", new JsonObject { ["option"] = unknown },
                supportedFixes: ["remove the unknown option and retry"]);
        var positional = args.Where(arg => !KnownOptions.Contains(arg)).ToArray();
        if (positional.Length != 3 || positional.Any(arg => arg.Length == 0))
            throw new CliArgumentException(Cli.Usage(), "cli/usage", supportedFixes: ["use: archify finalize <type> <input.json> <output.html> [options]"]);
        var (type, input, output) = (positional[0], positional[1], positional[2]);
        Args.DiagramType(type);
        quality ??= "showcase";

        FinalizeResult result;
        try
        {
            var request = new FinalizeRequest(type, input, output, io.Cwd)
            {
                Quality = quality, RepoRoot = repoRoot, CandidateSha256 = candidateSha256, OutDir = outDir, ReceiptPath = receiptPath,
                DeliveryPaths = FinalizeGate.CliDeliveryPaths, InspectDelivery = artifact => DeliveryStore.InspectProvenance(artifact, true),
            };
            // ponytail: blocking on the async run; Task.Run keeps it off any caller's synchronization context.
            result = Task.Run(() => FinalizeGate.RunAsync(request, io.Services)).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            // upstream catches every error here (A:5826): an OutputPathError's diagnostics as spread, a finalize code, else finalize/runtime
            var diagnostics = error switch
            {
                ArchifyDiagnosticException diagnosed => new JsonArray([.. diagnosed.Diagnostics.Select(d => (JsonNode)ArtifactFailure.SpreadJson(d))]),
                FinalizeException coded => new JsonArray(new JsonObject
                {
                    ["code"] = coded.Code, ["severity"] = "error", ["message"] = coded.Message, ["evidence"] = coded.Evidence.DeepClone(),
                }),
                _ => new JsonArray(new JsonObject { ["code"] = "finalize/runtime", ["severity"] = "error", ["message"] = error.Message }),
            };
            var failure = new JsonObject
            {
                ["schemaVersion"] = 1, ["ok"] = false, ["command"] = "finalize", ["status"] = "fail", ["type"] = type, ["quality"] = quality,
                ["specification"] = new JsonObject { ["path"] = NodePath.Resolve(io.Cwd, input) },
                ["artifact"] = new JsonObject { ["path"] = NodePath.Resolve(io.Cwd, output) },
                ["gates"] = new JsonObject(FinalizeGate.Stages.Select(stage => KeyValuePair.Create(stage, (JsonNode?)"not-run"))),
                ["diagnostics"] = diagnostics, ["visualReview"] = "not-requested",
            };
            if (json) io.Out(Js.Stringify(failure));
            else io.Err(Cli.FormatDiagnostics("finalize could not start", diagnostics));
            return 1;
        }

        var summary = result.Summary;
        if (json) io.Out(Js.Stringify(summary));
        else
        {
            io.Out($"finalize {summary["status"]}: {summary["artifact"]!["path"]}");
            io.Out($"gates {string.Join(" ", summary["gates"]!.AsObject().Select(gate => $"{gate.Key}:{gate.Value}"))}");
            io.Out($"receipt {summary["evidence"]!["receipt"]}");
            io.Out($"perceptual visual review {summary["visualReview"]}");
            foreach (var entry in summary["diagnostics"]!.AsArray().Where(entry => (string?)entry!["severity"] == "warning"))
                io.Err($"warning [{entry!["code"]}] {entry["message"]}");
        }
        return result.ExitCode;
    }
}
