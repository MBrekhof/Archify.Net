using System.Text;
using System.Text.Json.Nodes;
using Archify.Net.Cli.Paths;

namespace Archify.Net.Cli.Commands;

static class PatchCommand
{
    // commandPatch (A:5304-5350 at v3.2.0): the original checked, one panel replaced (preparePanelPatch), the patched draft written
    // to an owned temporary directory and delivered in process with the rebound bundle, then deliver's receipt as patch's.
    public static int Run(string[] rawArgs, CliIo io)
    {
        var (afterQuality, quality) = Args.Quality(rawArgs);
        var (afterRepoRoot, repoRoot) = Args.RepoRoot(afterQuality, io.Cwd);
        var (rest, writingMode) = Args.ExtractExplainerWritingArgs(afterRepoRoot);
        var json = rest.Contains("--json");
        var positional = rest.Where(arg => arg != "--json").ToArray();
        if (positional is not ["explainer", { Length: > 0 }, { Length: > 0 }, { Length: > 0 }, { Length: > 0 }]
            || positional.Any(arg => arg.StartsWith("--", StringComparison.Ordinal)))
            throw new CliArgumentException("Use: archify patch explainer <artifact.html> <panel-id> <replacement-file> <output.html> [--json]", "cli/usage",
                supportedFixes: ["Provide an explainer artifact, stable panel id, replacement file and output HTML path."]);
        var artifact = NodePath.Resolve(io.Cwd, positional[1]);
        var panelId = positional[2];
        var replacement = NodePath.Resolve(io.Cwd, positional[3]);
        var output = positional[4];

        int Fail(string error, IReadOnlyList<Diagnostic> diagnostics, IReadOnlyCollection<Diagnostic>? spread = null) =>
            Cli.ReportArtifactFailure(io, json, new ArtifactFailure("patch", "prepare", error, diagnostics)
            {
                Type = "explainer", Input = artifact, Output = NodePath.Resolve(io.Cwd, output), SpreadOrdered = spread ?? [],
            });

        string? temporary = null;
        try
        {
            // The original through check-render-output.mjs; an unreadable file reports on stderr only, so no receipt diagnostics.
            byte[]? bytes = null;
            try { bytes = File.ReadAllBytes(artifact); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            var original = bytes is null ? null : Cli.CheckArtifact(bytes, artifact);
            if (original is not { Ok: true })
            {
                const string message = "The original explainer artifact failed validation.";
                return Fail(message, original?.Report["diagnostics"] is JsonArray reported
                    ? [.. reported.OfType<JsonObject>().Select(DiagnosticOf)]
                    : [new("explainer/patch-artifact", Severity.Error, message,
                        new JsonObject { ["input"] = artifact, ["line"] = 1, ["component"] = "document" },
                        new JsonObject { ["reason"] = "invalid-original-artifact", ["example"] = "Use an unchanged formal explainer artifact." },
                        ["Rebuild the original artifact."], null)]);
            }

            var prepared = ExplainerPipeline.Patch(new PatchRequest(artifact, panelId, replacement, repoRoot, quality, writingMode,
                original.Report["artifact"]!.DeepClone().AsObject()));
            try { OutputPaths.Native.ResolveOutputPath(new OutputRequest(io.Cwd, output, null) { InputPaths = [replacement, prepared.SourcePath] }); }
            catch (ArchifyDiagnosticException error) { return Fail(error.Message, error.Diagnostics, error.Diagnostics); }   // an OutputPathError: spread

            temporary = Directory.CreateTempSubdirectory("archify-explainer-patch-").FullName;
            var input = Path.Join(temporary, "patched-draft.json");
            using (var stream = new FileStream(input, FileMode.CreateNew, FileAccess.Write)) stream.Write(new UTF8Encoding(false).GetBytes(prepared.SourceText));
            var bundle = ExplainerPipeline.Rebind(prepared.Bundle, input);
            string[] deliverArgs =
            [
                "explainer", input, output, "--quality", Truthy(quality) ?? Truthy(bundle.Str("quality")) ?? "standard",
                "--writing", Truthy(bundle.Str("writingMode")) ?? "warn", .. repoRoot is null ? Array.Empty<string>() : ["--repo-root", repoRoot], "--json",
            ];
            var deliveryOut = new StringWriter();
            var deliveryErr = new StringWriter();
            var deliveryIo = new CliIo(deliveryOut, deliveryErr, io.Cwd, io.Services);
            int status;
            try { status = DeliverCommand.Run(deliverArgs, deliveryIo, prepared: bundle); }
            catch (CliArgumentException error)
            {
                deliveryIo.Out(Js.Stringify(Cli.ArgumentReceipt("deliver", error), 2));
                status = 2;
            }
            JsonObject result;
            try { result = DiagramLoader.ParseNode(deliveryOut.ToString())!.AsObject(); }
            catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or NullReferenceException)
            {
                throw new InvalidOperationException("Patch delivery did not return a structured receipt.");
            }
            // {...receipt, command: 'patch', panelId, original, replacement, revision, sourceSha256} (A:5341)
            result["command"] = "patch";
            result["panelId"] = panelId;
            result["original"] = new JsonObject
            {
                ["path"] = artifact, ["revision"] = prepared.PreviousRevision, ["sourceSha256"] = prepared.PreviousSourceSha256,
            };
            result["replacement"] = new JsonObject { ["path"] = replacement };
            result["revision"] = ExplainerPipeline.Revision(bundle);
            result["sourceSha256"] = bundle["source"].Str("sha256");
            if (json) io.Out(Js.Stringify(result, 2));
            else if (result["ok"].Truthy()) io.Out($"patched {panelId} {Js.PrimitiveString(result["output"])}");
            else io.Err(Cli.FormatDiagnostics(Js.PrimitiveString(result["error"]), result["diagnostics"] as JsonArray ?? []));
            io.Stderr.Write(deliveryErr.ToString());
            return status;
        }
        catch (ArchifyDiagnosticException error)
        {
            return Fail(error.Message, error.Diagnostics);
        }
        catch (Exception error) when (error is not (CliArgumentException or CliFailException))
        {
            return Fail(error.Message, [new("explainer/patch-runtime", Severity.Error, error.Message,
                new JsonObject { ["input"] = artifact, ["line"] = 1, ["component"] = "panel", ["panelId"] = panelId },
                new JsonObject { ["reason"] = ExplainerInput.ErrorCode(error) ?? "patch-failure", ["example"] = "Use a complete replacement panel with the same stable id." },
                ["Repair the replacement panel and retry."], null)]);
        }
        finally
        {
            // ponytail: a directory that cannot be removed stays in %TEMP%; upstream's owned cleanup would report it.
            if (temporary is not null)
                try { Directory.Delete(temporary, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    // `a || b` over strings
    static string? Truthy(string? value) => string.IsNullOrEmpty(value) ? null : value;

    // A checker diagnostic as diagnostic() would carry it.
    static Diagnostic DiagnosticOf(JsonObject json) => new(json.Str("code") ?? "", json.Str("severity") == "warning" ? Severity.Warning : Severity.Error,
        json.Str("message") ?? "", json.Obj("subject")?.DeepClone().AsObject() ?? [], json.Obj("evidence")?.DeepClone().AsObject() ?? [],
        [.. json["supportedFixes"].AsArrayOrEmpty().Select(Js.PrimitiveString)], null);
}
