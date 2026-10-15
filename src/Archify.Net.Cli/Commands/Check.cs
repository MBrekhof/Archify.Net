using System.Text.Json.Nodes;
using Archify.Net.Cli.Paths;

namespace Archify.Net.Cli.Commands;

static class CheckCommand
{
    static readonly string[] KnownOptions = ["--json", "--require-provenance"];

    // commandCheck (A:5470-5545) without the provenance gate (Task 9): the output is always JSON, `--json` changes nothing, and
    // with no delivery sidecar provenance is 'unknown'.
    public static int Run(string[] args, CliIo io)
    {
        if (args.FirstOrDefault(arg => arg.StartsWith("--", StringComparison.Ordinal) && !KnownOptions.Contains(arg)) is { } unknown)
            throw new CliFailException($"Unknown check option \"{unknown}\".");
        var positional = args.Where(arg => !KnownOptions.Contains(arg)).ToArray();
        if (positional.Length != 1 || positional[0].Length == 0) throw new CliFailException(Cli.Usage());
        if (args.Contains("--require-provenance")) throw new NotImplementedException("Task 9");
        var artifactPath = NodePath.Resolve(io.Cwd, positional[0]);

        if (Unreadable(artifactPath, out var bytes) is { } code)
        {
            // inspectArtifactDeliveryProvenance's catch (A:5356-5366), printed as provenanceFailureReceipt (A:5450-5468).
            var reason = $"The artifact is not a stable regular file (delivery-artifact-{code}).";
            var message = $"Could not read the HTML artifact: {reason}";
            var diagnostic = new Diagnostic("input/artifact-unreadable", Severity.Error, message, new JsonObject { ["artifact"] = artifactPath },
                new JsonObject { ["reason"] = reason }, ["provide an existing readable .html artifact, then rerun the checker"], null);
            io.Out(Js.Stringify(new JsonObject
            {
                ["schemaVersion"] = 1, ["ok"] = false, ["command"] = "check", ["provenance"] = "unknown",
                ["artifact"] = new JsonObject { ["path"] = artifactPath }, ["error"] = message, ["file"] = artifactPath, ["diagnostic"] = message,
                ["diagnostics"] = new JsonArray(diagnostic.ToJson()),
            }, 2));
            return 1;
        }

        // check-render-output.mjs's report gains `file` at index 1 (it is the script's second key) and the provenance status.
        var check = ArchifyNet.Check(bytes);
        check.Report.Insert(1, "file", artifactPath);
        check.Report["provenance"] = "unknown";
        io.Out(Js.Stringify(check.Report, 2));
        return check.Ok ? 0 : 1;
    }

    // captureRegularFileBinding's verdict on the realpath of the artifact (atomic-output.mjs:321-346, A:108-116), as the reason code's
    // suffix; null with the bytes when the artifact reads. A link resolves to its target; a dangling one is no regular file.
    static string? Unreadable(string path, out byte[] bytes)
    {
        bytes = [];
        var file = new FileInfo(path);
        if (!file.Exists) return Directory.Exists(path) || file.LinkTarget is not null ? "not-regular-file" : "inspection-failed";
        try { bytes = File.ReadAllBytes(path); return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return "handle-inspection-failed"; }
    }
}
