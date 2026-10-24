using System.Text;
using System.Text.Json.Nodes;
using Archify.Net.Cli.Paths;

namespace Archify.Net.Cli.Commands;

static class RenderCommand
{
    // commandRender (A:4233-4249): quality, repo-root, unknown option, arity, type; then render-<type>.mjs <input> [output].
    public static int Run(string[] args, CliIo io)
    {
        var (afterQuality, quality) = Args.Quality(args);
        var (rest, repoRoot) = Args.RepoRoot(afterQuality, io.Cwd);
        // A mistyped flag would otherwise be taken as the output path.
        if (rest.FirstOrDefault(arg => arg.StartsWith("--", StringComparison.Ordinal)) is { } unknown)
            throw new CliFailException($"Unknown render option \"{unknown}\".");
        if (rest.Length is < 2 or > 3 || rest[0].Length == 0 || rest[1].Length == 0) throw new CliFailException(Cli.Usage());
        var type = Args.DiagramType(rest[0]);
        var inputPath = NodePath.Resolve(io.Cwd, rest[1]);
        var output = rest.Length == 3 && rest[2].Length > 0 ? rest[2] : null;   // `...(output ? [output] : [])`
        return type == DiagramType.Explainer
            ? RenderExplainer(io, inputPath, output, quality, repoRoot)
            : RenderToFile(io, type, () => ReadDiagram(inputPath), inputPath, output, Args.RenderOptions(quality, repoRoot));
    }

    // render-explainer.mjs in human mode: the draft read as 'utf8' text (no UTF-8 check), prepared, the output resolved, the page
    // rendered for the output's directory, its frozen files re-checked, then written. Nothing is printed on success; a failure with
    // diagnostics prints formatDiagnostics, exit 1.
    static int RenderExplainer(CliIo io, string inputPath, string? requestedOutput, string? quality, string? repoRoot)
    {
        try
        {
            var bundle = ExplainerPipeline.Prepare(new UTF8Encoding(false).GetString(File.ReadAllBytes(inputPath)),
                new PrepareOptions(inputPath, RepoRoot: repoRoot, Quality: quality));
            var request = new OutputRequest(io.Cwd, requestedOutput, "explainer.html") { InputPaths = [inputPath, (string)bundle["sourcePath"]!] }
                .WithAuthored(bundle["document"]!.AsObject());
            var outPath = OutputPaths.Native.ResolveOutputPath(request).OutputPath;
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            var html = ExplainerPipeline.Render(bundle, Path.GetDirectoryName(outPath));
            ExplainerPipeline.AssertDependencies(bundle);
            WriteHtml(outPath, html, request);
            return 0;
        }
        catch (ArchifyDiagnosticException error)
        {
            io.Err(Cli.FormatDiagnostics(error.Message, error.Diagnostics));
            return 1;
        }
        catch (Exception error) when (error is not (CliFailException or CliArgumentException))
        {
            // UPSTREAM-DEVIATION render-uncaught: an error without diagnostics (Pele's front-matter error, an unreadable draft) is
            // uncaught upstream and Node prints its raw stack, exit 1. The port prints the message, exit 1 (Review Focus 1).
            io.Err(error.Message);
            return 1;
        }
    }

    // One renderer run in human diagnostic mode (cli.mjs loadDiagram + writeDiagram, diagnostics.mjs's boundary): locale warnings
    // as `archify: <message>` on stderr, the output path on stdout, or formatDiagnostics of the thrown diagnostics and exit 1.
    // `inputPath` is null for an embedded example, which no output can alias.
    internal static int RenderToFile(CliIo io, DiagramType type, Func<JsonObject> load, string? inputPath, string? requestedOutput, RenderOptions options)
    {
        try
        {
            var diagram = load();
            var request = new OutputRequest(io.Cwd, requestedOutput, $"{type.Name()}.html") { InputPaths = inputPath is null ? [] : [inputPath] }
                .WithAuthored(diagram);
            // cli.mjs:79-80: the authored path, with its filesystem checks, before the schema gate.
            if (request.HasAuthoredOutput) OutputPaths.Native.ValidateAuthoredOutputPath(request.AuthoredOutput, io.Cwd);
            string? outPath = null;
            var result = ArchifyNet.Render(type, diagram, options, () => outPath = OutputPaths.Native.ResolveOutputPath(request).OutputPath);
            foreach (var warning in result.Diagnostics.Where(d => d.Code.StartsWith("i18n/", StringComparison.Ordinal))) io.Err($"archify: {warning.Message}");
            if (result.Error is not null) throw new ArchifyDiagnosticException(result.Error, result.ThrownDiagnostics);
            WriteHtml(outPath!, result.Html, request);
            io.Out(outPath!);
            return 0;
        }
        catch (ArchifyDiagnosticException error)
        {
            io.Err(Cli.FormatDiagnostics(error.Message, error.Diagnostics));
            return 1;
        }
    }

    // writeDiagram (cli.mjs:233-302) without atomic-output.mjs (Q5): parent directories, a temp file beside the physical target,
    // the alias recheck (:260-263), then a move onto the target. A symbolic link at the target keeps pointing at the new file.
    static void WriteHtml(string outPath, string html, OutputRequest request)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            var commitPath = OutputPaths.Native.CanonicalFuturePath(outPath);
            var temp = Path.Join(Path.GetDirectoryName(commitPath), $".archify-render-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write)) stream.Write(new UTF8Encoding(false).GetBytes(html));
                OutputPaths.Native.ResolveOutputPath(request);
                File.Move(temp, commitPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // ponytail: the reason is .NET's text where upstream has Node's (`EACCES: permission denied, open '…'`); Q8 compares code and subject.
            var message = $"Output could not be written: {error.Message}";
            throw new ArchifyDiagnosticException(message, [new("output/write", Severity.Error, message, new JsonObject { ["output"] = outPath },
                new JsonObject { ["systemCode"] = NativeFs.ErrorCode(error), ["reason"] = error.Message },
                ["choose a writable HTML file path and ensure its parent directories can be created"], null)]);
        }
    }

    // loadDiagram's read and parse (cli.mjs:52-78). The bytes decode as readFileSync(p, 'utf8') does: a BOM stays and fails the parse.
    internal static JsonObject ReadDiagram(string inputPath) => ReadDiagram(inputPath, out _);

    internal static JsonObject ReadDiagram(string inputPath, out byte[] bytes)
    {
        bytes = ReadInput(inputPath);
        try { return DiagramLoader.Parse(new UTF8Encoding(false).GetString(bytes)); }
        catch (ArchifyDiagnosticException error)
        {
            throw new ArchifyDiagnosticException(error.Message, [.. error.Diagnostics.Select(d => d with { Subject = new JsonObject { ["input"] = inputPath } })]);
        }
    }

    // readFileSync(inputPath): a failure is input/read with Node's fs text (inputDiagnostic, A:2646-2661).
    internal static byte[] ReadInput(string inputPath)
    {
        try { return File.ReadAllBytes(inputPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var (systemCode, reason) = NodeReadError(error, inputPath);
            var message = $"Input could not be read: {reason}";
            throw new ArchifyDiagnosticException(message, [new("input/read", Severity.Error, message, new JsonObject { ["input"] = inputPath },
                new JsonObject { ["systemCode"] = systemCode, ["reason"] = reason }, ["provide one readable JSON input file"], null)]);
        }
    }

    // Node's fs error text for the three read failures an author meets; any other keeps .NET's message (Q8: code and subject).
    static (string? SystemCode, string Reason) NodeReadError(Exception error, string path) =>
        Directory.Exists(path) ? ("EISDIR", "EISDIR: illegal operation on a directory, read")
        : error is FileNotFoundException or DirectoryNotFoundException ? ("ENOENT", $"ENOENT: no such file or directory, open '{path}'")
        : error is UnauthorizedAccessException ? ("EACCES", $"EACCES: permission denied, open '{path}'")
        : (NativeFs.ErrorCode(error), error.Message);
}
