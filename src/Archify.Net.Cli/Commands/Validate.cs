using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Archify.Net.Cli.Paths;

namespace Archify.Net.Cli.Commands;

static partial class ValidateCommand
{
    static readonly string[] KnownOptions = ["--json", "--layout-json"];

    // commandValidate (A:6962-7180 at v3.2.0). `inspect architecture …` arrives here as `[...args, '--layout-json']` named inspect.
    // `prepared` is upstream's ARCHIFY_EXPLAINER_PREPARED for an explainer: the bundle checked against the draft instead of a new one.
    public static int Run(string[] args, CliIo io, string command = "validate", JsonObject? prepared = null)
    {
        var (afterQuality, quality) = Args.Quality(args);
        var (afterRepoRoot, repoRoot) = Args.RepoRoot(afterQuality, io.Cwd);
        var (options, writingMode) = Args.ExtractExplainerWritingArgs(afterRepoRoot);
        if (options.FirstOrDefault(arg => arg.StartsWith("--", StringComparison.Ordinal) && !KnownOptions.Contains(arg)) is { } unknown)
            throw new CliArgumentException($"Unknown {command} option \"{unknown}\".", "cli/unknown-option", new JsonObject { ["option"] = unknown },
                supportedFixes: ["remove the unknown option and retry"]);
        var json = options.Contains("--json");
        var layoutJson = options.Contains("--layout-json");
        var rest = options.Where(arg => !KnownOptions.Contains(arg)).ToArray();
        if (rest.Length != 2 || rest[0].Length == 0 || rest[1].Length == 0)
            throw new CliArgumentException(Cli.Usage(), "cli/usage", supportedFixes: [$"use: archify {command} <type> <input.json> [options]"]);
        var typeName = rest[0];
        var type = Args.DiagramType(typeName);
        if (layoutJson && type is not (DiagramType.Architecture or DiagramType.Workflow))
            throw new CliArgumentException("--layout-json is currently supported for architecture and workflow diagrams only.", "cli/unsupported-option",
                new JsonObject { ["option"] = "--layout-json", ["type"] = typeName }, supportedFixes: ["remove --layout-json or use an architecture or workflow diagram"]);

        var explainer = type == DiagramType.Explainer;
        var inputPath = NodePath.Resolve(io.Cwd, rest[1]);
        ArtifactFailure Failure(string error, IReadOnlyList<Diagnostic> diagnostics) =>
            new(command, Cli.InputOrRender(diagnostics), error, Cli.WithInput(diagnostics, inputPath)) { Type = typeName, Input = inputPath };

        // The in-CLI pre-gate (A:7001-7024): bytes parsed without stripping a BOM (an explainer draft prepared instead), then the
        // schema when meta.output is undefined (never for an explainer), otherwise only the authored output path.
        byte[] specification = [];
        JsonObject document;
        JsonObject? bundle = null;
        var authoredOutput = false;
        try
        {
            if (explainer)
            {
                specification = RenderCommand.ReadInput(inputPath);
                bundle = ExplainerInput.Prepare(specification, inputPath, repoRoot, quality, writingMode, prepared);
                document = bundle["document"]!.AsObject();
            }
            else document = RenderCommand.ReadDiagram(inputPath, out specification);
            JsonNode? output = null;
            authoredOutput = document["meta"] is JsonObject meta && meta.TryGetPropertyValue("output", out output);
            if (authoredOutput) OutputPaths.Native.ValidateAuthoredOutputPath(output, io.Cwd);
            else if (!explainer) SchemaValidator.Validate(type, document, new DiagnosticSink());
        }
        catch (ArchifyDiagnosticException error)
        {
            // inputDiagnostic (A:2646-2661): `error` is Node's own message; only the diagnostic prefixes it.
            var message = error.Diagnostics is [{ Code: "input/read" or "input/json-parse" } input] ? input.Evidence["reason"]!.GetValue<string>() : error.Message;
            // An authored-output failure is an OutputPathError, reported with its spread key order (pinned by validate/bad-meta-output-json).
            return Cli.ReportArtifactFailure(io, json, Failure(message, error.Diagnostics) with { SpreadOrdered = authoredOutput ? error.Diagnostics : [] });
        }
        catch (Exception error) when (explainer && error is not (CliArgumentException or CliFailException))
        {
            // An explainer error without diagnostics (Pele's front-matter error, Review Focus 1): inputDiagnostic, stage input.
            return Cli.ReportArtifactFailure(io, json, Failure(error.Message, [ExplainerInput.InputDiagnostic(error, inputPath)]));
        }

        var renderOptions = Args.RenderOptions(quality, repoRoot);
        if (layoutJson)
        {
            // A:7026-7059: the layout report on success; on failure the renderer's own receipt when it has a contract and diagnostics.
            var layout = ArchifyNet.Layout(type, document, renderOptions);
            if (layout.Report is { } report && (layout.Ok || report["contract"].Truthy() && report["diagnostics"] is JsonArray))
            {
                io.Out(Js.Stringify(report, 2));
                return layout.Ok ? 0 : 1;
            }
            return Cli.ReportArtifactFailure(io, json, Failure(layout.Error!, layout.Diagnostics));
        }

        // The render into the owned temporary directory (A:7061-7093), in memory. The explainer page's directory is that directory.
        var page = Path.Join(Path.GetTempPath(), $"archify-validate-{Path.GetRandomFileName().Replace(".", "")[..6]}", $"{typeName}.html");
        string html;
        List<JsonObject> warnings;
        if (bundle is not null)
        {
            try { html = ExplainerPipeline.Render(bundle, Path.GetDirectoryName(page)); }
            catch (Exception error) when (error is not (CliArgumentException or CliFailException))
            {
                var (message, diagnostics, _) = new DiagnosticSink().RendererFailure(error);
                return Cli.ReportArtifactFailure(io, json, Failure(message, diagnostics));
            }
            warnings = ExplainerInput.Warnings(specification, bundle);
        }
        else
        {
            var result = ArchifyNet.Render(type, document, renderOptions);
            if (result.Error is not null) return Cli.ReportArtifactFailure(io, json, Failure(result.Error, result.Diagnostics));
            html = result.Html;
            // specificationLocaleDiagnostics (A:1877-1887): the locale warnings, disclosed before the check and kept in the receipt.
            warnings = [.. result.Diagnostics.Where(d => d.Code.StartsWith("i18n/", StringComparison.Ordinal)).Select(d => d.ToJson())];
        }
        foreach (var warning in warnings) io.Err($"archify: {warning.Str("message")}");
        var check = Cli.CheckArtifact(new UTF8Encoding(false).GetBytes(html), page);
        if (!check.Ok) return Cli.ReportArtifactFailure(io, json, CheckFailure(command, typeName, inputPath, check.Report));

        // engineeringProfileFromArtifact (A:4394-4397)
        var profileMatch = EngineeringProfile().Match(html);
        var engineeringProfile = profileMatch.Success ? profileMatch.Groups[1].Value : null;
        var composition = check.Report["composition"]!;
        if (json)
        {
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(specification));   // artifactIdentity
            var resolvedQuality = quality ?? (composition["profile"].Truthy() ? composition["profile"]!.GetValue<string>() : "standard");
            JsonArray arguments = [typeName, inputPath, "<output.html>", "--quality", resolvedQuality];
            if (repoRoot is not null) { arguments.Add("--repo-root"); arguments.Add(repoRoot); }
            arguments.Add("--candidate-sha256");
            arguments.Add(sha256);
            arguments.Add("--json");
            var receipt = new JsonObject
            {
                ["schemaVersion"] = 1, ["ok"] = true, ["command"] = "validate", ["type"] = typeName, ["input"] = inputPath,
                ["candidate"] = new JsonObject { ["path"] = inputPath, ["sha256"] = sha256, ["bytes"] = specification.Length },
                ["candidateFrozen"] = true,
                ["nextAction"] = new JsonObject { ["command"] = "finalize", ["arguments"] = arguments },
                ["checks"] = check.Report["checks"]!.DeepClone(),
                ["composition"] = composition.DeepClone(),
            };
            if (engineeringProfile is not null) receipt["engineeringProfile"] = engineeringProfile;
            if (warnings.Count > 0) receipt["diagnostics"] = new JsonArray([.. warnings]);
            io.Out(Js.Stringify(receipt, 2));
        }
        else
        {
            var engineering = engineeringProfile is not null ? $"; engineering {engineeringProfile}: pass" : "";
            var summary = composition["summary"]!;
            io.Out($"ok {typeName} {inputPath} ({check.Report["checks"]!.AsArray().Count} artifact checks; composition {composition["profile"]!.GetValue<string>()}: " +
                $"{Js.Num(summary["errors"]!.D())} errors, {Js.Num(summary["warnings"]!.D())} warnings{engineering})");
        }
        return 0;
    }

    // The check stage (A:7111-7132): the checker's report with `file` naming the input (in place of the checked page at key index
    // 1), and its failures classified by checkerDiagnostics.
    internal static ArtifactFailure CheckFailure(string command, string type, string inputPath, JsonObject checker)
    {
        if (checker.ContainsKey("file")) checker["file"] = inputPath;
        else checker.Insert(1, "file", inputPath);
        return new(command, "check", "Final artifact check failed.", CheckerDiagnostics.From(checker)) { Type = type, Input = inputPath, Checker = checker };
    }

    [GeneratedRegex(@"<svg[^>]*\sdata-engineering-profile=""([^""]+)""")]
    internal static partial Regex EngineeringProfile();
}
