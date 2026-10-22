using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Archify.Net.Cli.Paths;

namespace Archify.Net.Cli.Commands;

static partial class ValidateCommand
{
    static readonly string[] KnownOptions = ["--json", "--layout-json"];

    // commandValidate (A:6824-7057). `inspect architecture …` arrives here as `[...args, '--layout-json']` named inspect (A:7094-7102).
    public static int Run(string[] args, CliIo io, string command = "validate")
    {
        var (afterQuality, quality) = Args.Quality(args);
        var (afterRepoRoot, repoRoot) = Args.RepoRoot(afterQuality, io.Cwd);
        var (options, _) = Args.ExtractExplainerWritingArgs(afterRepoRoot);
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

        var inputPath = NodePath.Resolve(io.Cwd, rest[1]);
        ArtifactFailure Failure(string error, IReadOnlyList<Diagnostic> diagnostics) =>
            new(command, Cli.InputOrRender(diagnostics), error, Cli.WithInput(diagnostics, inputPath)) { Type = typeName, Input = inputPath };

        // The in-CLI pre-gate (A:6857-6880): bytes parsed without stripping a BOM, then the schema when meta.output is undefined,
        // otherwise only the authored output path.
        byte[] specification;
        JsonObject document;
        var authoredOutput = false;
        try
        {
            document = RenderCommand.ReadDiagram(inputPath, out specification);
            JsonNode? output = null;
            authoredOutput = document["meta"] is JsonObject meta && meta.TryGetPropertyValue("output", out output);
            if (authoredOutput) OutputPaths.Native.ValidateAuthoredOutputPath(output, io.Cwd);
            else SchemaValidator.Validate(type, document, new DiagnosticSink());
        }
        catch (ArchifyDiagnosticException error)
        {
            // inputDiagnostic (A:2609-2625): `error` is Node's own message; only the diagnostic prefixes it.
            var message = error.Diagnostics is [{ Code: "input/read" or "input/json-parse" } input] ? input.Evidence["reason"]!.GetValue<string>() : error.Message;
            // An authored-output failure is an OutputPathError, reported with its spread key order (pinned by validate/bad-meta-output-json).
            return Cli.ReportArtifactFailure(io, json, Failure(message, error.Diagnostics) with { SpreadOrdered = authoredOutput ? error.Diagnostics : [] });
        }

        var renderOptions = Args.RenderOptions(quality, repoRoot);
        if (layoutJson)
        {
            // A:6882-6917: the layout report on success; on failure the renderer's own receipt when it has a contract and diagnostics.
            var layout = ArchifyNet.Layout(type, document, renderOptions);
            if (layout.Report is { } report && (layout.Ok || report["contract"].Truthy() && report["diagnostics"] is JsonArray))
            {
                io.Out(Js.Stringify(report, 2));
                return layout.Ok ? 0 : 1;
            }
            return Cli.ReportArtifactFailure(io, json, Failure(layout.Error!, layout.Diagnostics));
        }

        var result = ArchifyNet.Render(type, document, renderOptions);
        if (result.Error is not null) return Cli.ReportArtifactFailure(io, json, Failure(result.Error, result.Diagnostics));
        // specificationLocaleDiagnostics (A:1877-1887): the locale warnings, disclosed before the check and kept in the receipt.
        var localeWarnings = result.Diagnostics.Where(d => d.Code.StartsWith("i18n/", StringComparison.Ordinal)).ToList();
        foreach (var warning in localeWarnings) io.Err($"archify: {warning.Message}");
        var check = ArchifyNet.Check(new UTF8Encoding(false).GetBytes(result.Html));
        if (!check.Ok) return Cli.ReportArtifactFailure(io, json, CheckFailure(command, typeName, inputPath, check.Report));

        // engineeringProfileFromArtifact (A:4357-4360)
        var profileMatch = EngineeringProfile().Match(result.Html);
        var engineeringProfile = profileMatch.Success ? profileMatch.Groups[1].Value : null;
        var composition = check.Report["composition"]!;
        if (json)
        {
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(specification));   // artifactIdentity (A:981-983)
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
            if (localeWarnings.Count > 0) receipt["diagnostics"] = new JsonArray([.. localeWarnings.Select(d => (JsonNode)d.ToJson())]);
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

    // The check stage (A:6968-6984): the checker's report with `file` naming the input at key index 1, where the checker's own
    // `file` (the temporary artifact) sat, and its failures classified by checkerDiagnostics.
    internal static ArtifactFailure CheckFailure(string command, string type, string inputPath, JsonObject checker)
    {
        checker.Insert(1, "file", inputPath);
        return new(command, "check", "Final artifact check failed.", CheckerDiagnostics.From(checker)) { Type = type, Input = inputPath, Checker = checker };
    }

    [GeneratedRegex(@"<svg[^>]*\sdata-engineering-profile=""([^""]+)""")]
    internal static partial Regex EngineeringProfile();
}
