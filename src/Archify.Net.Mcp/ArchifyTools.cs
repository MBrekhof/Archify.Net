using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Archify.Net.Cli;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Archify.Net.Mcp;

[McpServerToolType]
public sealed class ArchifyTools : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _defaultCwd;
    private readonly CliServices _services;

    public ArchifyTools(string? defaultCwd = null, CliServices? services = null)
    {
        _defaultCwd = string.IsNullOrWhiteSpace(defaultCwd)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(defaultCwd);
        _services = services ?? CliServices.Default;
    }

    public string DefaultCwd => _defaultCwd;

    [McpServerTool(
        Name = "archify_guide",
        Title = "Archify Scenario Guide",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false)]
    [Description("Recommend an Archify diagram type and scenario recipe for a plain-language scenario or question, or list all scenario recipes when query is omitted.")]
    public Task<CallToolResult> GuideAsync(
        [Description("Optional scenario or question in plain language (for example, 'approval gate for leave requests' or 'Show an API request with Redis cache miss'). Omit to list all scenario recipes.")]
        string? query = null,
        [Description("Optional output language: 'en' or 'zh'. When omitted, detected from query or defaults to 'en'.")]
        string? lang = null,
        CancellationToken cancellationToken = default)
    {
        var argv = new List<string> { "guide" };
        if (!string.IsNullOrWhiteSpace(query))
        {
            argv.Add(query.Trim());
        }

        argv.Add("--json");
        if (!string.IsNullOrWhiteSpace(lang))
        {
            argv.Add("--lang");
            argv.Add(lang.Trim());
        }

        return ExecuteCliAsync(argv, cwd: null, cancellationToken);
    }

    [McpServerTool(
        Name = "archify_validate",
        Title = "Validate Archify Specification",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false)]
    [Description("Validate an Archify JSON specification against schema, layout, and composition rules without writing an HTML file, or emit the resolved layout geometry report when layoutJson is true.")]
    public Task<CallToolResult> ValidateAsync(
        [Description("Diagram type: architecture, workflow, sequence, dataflow, lifecycle, erd, tree, class, timeline, or waterfall.")]
        string type,
        [Description("Path to the input JSON specification file (resolved relative to cwd when not absolute).")]
        string input,
        [Description("When true, emit the resolved layout geometry report (--layout-json, supported for architecture and workflow) instead of the validation receipt.")]
        bool layoutJson = false,
        [Description("Optional quality profile override: 'standard' or 'showcase'.")]
        string? quality = null,
        [Description("Optional repository root path (--repo-root) for verifying source references.")]
        string? repoRoot = null,
        [Description("Optional working directory used to resolve relative paths.")]
        string? cwd = null,
        CancellationToken cancellationToken = default)
    {
        var argv = new List<string> { "validate", type ?? string.Empty, input ?? string.Empty, "--json" };
        if (layoutJson)
        {
            argv.Add("--layout-json");
        }

        if (!string.IsNullOrWhiteSpace(quality))
        {
            argv.Add("--quality");
            argv.Add(quality.Trim());
        }

        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            argv.Add("--repo-root");
            argv.Add(repoRoot.Trim());
        }

        return ExecuteCliAsync(argv, cwd, cancellationToken);
    }

    [McpServerTool(
        Name = "archify_render",
        Title = "Render Archify HTML Diagram",
        ReadOnly = false,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false)]
    [Description("Render an Archify JSON specification directly to a standalone interactive HTML file without provenance sidecars or browser gates. Returns the resolved output HTML path written by the CLI.")]
    public Task<CallToolResult> RenderAsync(
        [Description("Diagram type: architecture, workflow, sequence, dataflow, lifecycle, erd, tree, class, timeline, or waterfall.")]
        string type,
        [Description("Path to the input JSON specification file (resolved relative to cwd when not absolute).")]
        string input,
        [Description("Optional output HTML file path. When omitted, uses meta.output from the JSON specification or <type>.html.")]
        string? output = null,
        [Description("Optional quality profile override: 'standard' or 'showcase'.")]
        string? quality = null,
        [Description("Optional repository root path (--repo-root) for verifying source references.")]
        string? repoRoot = null,
        [Description("Optional working directory used to resolve relative paths.")]
        string? cwd = null,
        CancellationToken cancellationToken = default)
    {
        var argv = new List<string> { "render", type ?? string.Empty, input ?? string.Empty };
        if (!string.IsNullOrWhiteSpace(output))
        {
            argv.Add(output.Trim());
        }

        if (!string.IsNullOrWhiteSpace(quality))
        {
            argv.Add("--quality");
            argv.Add(quality.Trim());
        }

        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            argv.Add("--repo-root");
            argv.Add(repoRoot.Trim());
        }

        return ExecuteCliAsync(argv, cwd, cancellationToken);
    }

    [McpServerTool(
        Name = "archify_finalize",
        Title = "Finalize Archify Diagram",
        ReadOnly = false,
        Idempotent = true,
        Destructive = false,
        OpenWorld = false)]
    [Description("Run the complete Archify delivery pipeline (validate, deliver with provenance sidecar, strict artifact check, and headless Chromium browser-check) and return the compact JSON finalize receipt.")]
    public Task<CallToolResult> FinalizeAsync(
        [Description("Diagram type: architecture, workflow, sequence, dataflow, lifecycle, erd, tree, class, timeline, or waterfall.")]
        string type,
        [Description("Path to the input JSON specification file (resolved relative to cwd when not absolute).")]
        string input,
        [Description("Required output HTML file path (resolved relative to cwd when not absolute).")]
        string output,
        [Description("Optional path for the full finalize receipt JSON (--receipt).")]
        string? receipt = null,
        [Description("Optional directory for browser-check and finalize sidecars (--out-dir).")]
        string? outDir = null,
        [Description("Optional quality profile: 'standard' or 'showcase' (defaults to 'showcase').")]
        string? quality = null,
        [Description("Optional repository root path (--repo-root) for verifying source references.")]
        string? repoRoot = null,
        [Description("Optional expected SHA-256 hex digest of the input candidate file (--candidate-sha256).")]
        string? candidateSha256 = null,
        [Description("Optional working directory used to resolve relative paths.")]
        string? cwd = null,
        CancellationToken cancellationToken = default)
    {
        var argv = new List<string> { "finalize", type ?? string.Empty, input ?? string.Empty, output ?? string.Empty, "--json" };
        if (!string.IsNullOrWhiteSpace(receipt))
        {
            argv.Add("--receipt");
            argv.Add(receipt.Trim());
        }

        if (!string.IsNullOrWhiteSpace(outDir))
        {
            argv.Add("--out-dir");
            argv.Add(outDir.Trim());
        }

        if (!string.IsNullOrWhiteSpace(quality))
        {
            argv.Add("--quality");
            argv.Add(quality.Trim());
        }

        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            argv.Add("--repo-root");
            argv.Add(repoRoot.Trim());
        }

        if (!string.IsNullOrWhiteSpace(candidateSha256))
        {
            argv.Add("--candidate-sha256");
            argv.Add(candidateSha256.Trim());
        }

        return ExecuteCliAsync(argv, cwd, cancellationToken);
    }

    internal async Task<CallToolResult> ExecuteCliAsync(
        IReadOnlyList<string> argv,
        string? cwd,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string effectiveCwd;
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            try
            {
                effectiveCwd = Path.GetFullPath(cwd.Trim(), _defaultCwd);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return CreateErrorResult(BuildCwdErrorJson(cwd, ex.Message));
            }

            if (File.Exists(effectiveCwd))
            {
                return CreateErrorResult(BuildCwdErrorJson(cwd, $"Working directory is a file, not a directory: {effectiveCwd}"));
            }

            if (!Directory.Exists(effectiveCwd))
            {
                return CreateErrorResult(BuildCwdErrorJson(cwd, $"Working directory does not exist: {effectiveCwd}"));
            }
        }
        else
        {
            effectiveCwd = _defaultCwd;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();
            int exitCode;
            await using (var stdout = new StringWriter(stdoutBuilder, CultureInfo.InvariantCulture) { NewLine = "\n" })
            await using (var stderr = new StringWriter(stderrBuilder, CultureInfo.InvariantCulture) { NewLine = "\n" })
            {
                var io = new CliIo(stdout, stderr, effectiveCwd, _services);
                var argsArray = argv.ToArray();
                exitCode = await Task.Run(() => Archify.Net.Cli.Cli.Run(argsArray, io), cancellationToken).ConfigureAwait(false);
            }

            var outText = stdoutBuilder.ToString().TrimEnd('\r', '\n');
            var errText = stderrBuilder.ToString().TrimEnd('\r', '\n');

            if (exitCode == 0)
            {
                return new CallToolResult
                {
                    IsError = false,
                    Content = [new TextContentBlock { Text = outText }],
                };
            }

            var errorPayload = !string.IsNullOrWhiteSpace(outText)
                ? outText
                : (!string.IsNullOrWhiteSpace(errText) ? errText : $"Command '{string.Join(' ', argv)}' exited with code {exitCode}.");

            return CreateErrorResult(errorPayload);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static CallToolResult CreateErrorResult(string text) =>
        new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = text }],
        };

    private static string BuildCwdErrorJson(string rawCwd, string message)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", false);
            writer.WriteString("stage", "input");
            writer.WriteString("error", message);
            writer.WriteStartArray("diagnostics");
            writer.WriteStartObject();
            writer.WriteString("code", "input/cwd-invalid");
            writer.WriteString("severity", "error");
            writer.WriteStartObject("subject");
            writer.WriteString("cwd", rawCwd);
            writer.WriteEndObject();
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
