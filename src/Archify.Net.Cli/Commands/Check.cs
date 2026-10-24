using System.Text.Json.Nodes;
using Archify.Net.Cli.Delivery;
using Archify.Net.Cli.Paths;

namespace Archify.Net.Cli.Commands;

static class CheckCommand
{
    static readonly string[] KnownOptions = ["--json", "--require-provenance"];

    // commandCheck (A:5470-5545): the delivery provenance gate, the checker on the bytes the gate read, then verifyDeliveryUnchanged.
    // The output is always JSON; `--json` changes nothing.
    public static int Run(string[] args, CliIo io)
    {
        if (args.FirstOrDefault(arg => arg.StartsWith("--", StringComparison.Ordinal) && !KnownOptions.Contains(arg)) is { } unknown)
            throw new CliFailException($"Unknown check option \"{unknown}\".");
        var positional = args.Where(arg => !KnownOptions.Contains(arg)).ToArray();
        if (positional.Length != 1 || positional[0].Length == 0) throw new CliFailException(Cli.Usage());
        var artifactPath = NodePath.Resolve(io.Cwd, positional[0]);

        var provenance = DeliveryStore.InspectProvenance(artifactPath, args.Contains("--require-provenance"));
        if (!provenance.Ok) return Fail(io, artifactPath, provenance);

        // check-render-output.mjs's report (`file` at index 1), then the family against a current delivery (A:5636-5640), then the
        // provenance status.
        var (ok, report) = Cli.CheckArtifact(provenance.Artifact, artifactPath);
        if (provenance.Status == "current" && (provenance.Type == "explainer") != (report.Str("family") == "explainer"))
        {
            ok = false;
            report["ok"] = false;
            var evidence = new JsonObject { ["expectedType"] = provenance.Type };
            if (report["family"] is { } family) evidence["actualFamily"] = family.DeepClone();   // a native report has none: undefined
            report["diagnostics"] = new JsonArray([.. report["diagnostics"].AsArrayOrEmpty().Select(d => d?.DeepClone()),
                new Diagnostic("delivery/artifact-family-mismatch", Severity.Error, "The checked artifact family differs from its delivery provenance.",
                    new JsonObject { ["artifact"] = artifactPath }, evidence, ["Rebuild the artifact from its original specification."], null).ToJson()]);
        }
        var verified = DeliveryStore.VerifyUnchanged(artifactPath, provenance, report["artifact"] ?? new JsonObject());
        if (!verified.Ok) return Fail(io, artifactPath, verified);
        report["provenance"] = provenance.Status;
        if (provenance.ReceiptId is { } receiptId) report["deliveryReceiptId"] = receiptId;
        io.Out(Js.Stringify(report, 2));
        return ok ? 0 : 1;
    }

    static int Fail(CliIo io, string artifactPath, Provenance provenance)
    {
        io.Out(Js.Stringify(ProvenanceFailureReceipt("check", artifactPath, provenance), 2));
        return 1;
    }

    // provenanceFailureReceipt (A:5450-5468), shared with browser-check and visual-check.
    internal static JsonObject ProvenanceFailureReceipt(string command, string artifactPath, Provenance provenance)
    {
        var error = provenance.Diagnostics[0].Message;
        var receipt = new JsonObject { ["schemaVersion"] = 1, ["ok"] = false, ["command"] = command };
        if (command is "visual-check" or "browser-check")
        {
            receipt["evidenceKind"] = "automated-browser";
            receipt["status"] = "fail";
            receipt["visualReview"] = command == "visual-check" ? "pending" : "not-requested";
        }
        receipt["provenance"] = provenance.Status;
        if (provenance.ReceiptId is { } receiptId) receipt["deliveryReceiptId"] = receiptId;
        receipt["artifact"] = new JsonObject { ["path"] = artifactPath };
        receipt["error"] = error;
        if (command == "check")
        {
            receipt["file"] = artifactPath;
            receipt["diagnostic"] = error;
        }
        receipt["diagnostics"] = new JsonArray([.. provenance.Diagnostics.Select(d => (JsonNode)d.ToJson())]);
        return receipt;
    }
}
