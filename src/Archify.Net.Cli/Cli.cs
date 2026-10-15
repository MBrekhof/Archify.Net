using System.Text;
using System.Text.Json.Nodes;

namespace Archify.Net.Cli;

public static class Cli
{
    // COMMAND_USAGE (A:2191-2208), Q3: without compare, preview, migrate and brands; install-browser after doctor.
    static readonly (string Command, string Form)[] CommandUsage =
    [
        ("render", "<type> <input.json> [output.html] [--quality standard|showcase] [--repo-root path]"),
        ("deliver", "<type> <input.json> [output.html] [--json] [--open] [--quality standard|showcase] [--repo-root path]"),
        ("finalize", "<type> <input.json> <output.html> [--json] [--receipt path] [--out-dir <dir>] [--quality standard|showcase] [--repo-root path] [--candidate-sha256 hex]"),
        ("validate", "<type> <input.json> [--json] [--layout-json] [--quality standard|showcase] [--repo-root path]"),
        ("inspect", "<type> <input.json>"),
        ("check", "<output.html> [--json] [--require-provenance]"),
        ("browser-check", "<output.html> [--json|--summary] [--require-provenance] [--out-dir <dir>]"),
        ("visual-check", "<output.html> [--json|--summary] [--require-provenance] [--out-dir <dir>]"),
        ("guide", "[scenario or question] [--json] [--lang en|zh]"),
        ("examples", ""),
        ("doctor", ""),
        ("install-browser", ""),
        ("demo", "[output-directory]"),
    ];

    // usage() (A:2210-2219)
    internal static string Usage() =>
        "Usage:\n" + string.Join("\n", CommandUsage.Select(u => $"  archify {u.Command}{(u.Form.Length > 0 ? " " + u.Form : "")}")) +
        "\n\nTypes:\n  architecture, workflow, sequence, dataflow, lifecycle, erd, tree, class, timeline, waterfall\n";

    // The dispatch of A:7059-7138. Returns the exit code; a non-argument exception escapes (Program.Main: stack, exit 1).
    public static int Run(string[] argv, CliIo io)
    {
        // Node's process.argv: the UTF-16 command line passes through UTF-8, so a lone surrogate arrives as U+FFFD.
        argv = [.. argv.Select(arg => Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(arg)))];
        var command = argv.Length > 0 ? argv[0] : null;
        var args = argv.Skip(1).ToArray();
        // Only the sole --help form bypasses command argument parsing (A:7062-7063).
        var soleCommandHelp = args is ["--help"] && CommandUsage.Any(u => u.Command == command);
        try
        {
            switch (soleCommandHelp ? "--help" : command)
            {
                case null or "-h" or "--help" or "help":
                    io.Out(Usage());
                    return 0;
                case "render": return Commands.RenderCommand.Run(args, io);
                case "deliver": return Commands.DeliverCommand.Run(args, io);
                case "finalize": return Commands.FinalizeCommand.Run(args, io);
                case "validate": return Commands.ValidateCommand.Run(args, io);
                case "inspect":
                    if (args.ElementAtOrDefault(0) != "architecture")
                    {
                        var subject = new JsonObject { ["command"] = "inspect" };
                        if (args.Length > 0) subject["type"] = args[0];
                        throw new CliArgumentException("inspect is currently supported for architecture diagrams only.", "cli/unsupported-option",
                            subject, supportedFixes: ["use an architecture diagram", "use validate <type> <input.json> --layout-json for workflow diagrams"]);
                    }
                    return Commands.ValidateCommand.Run([.. args, "--layout-json"], io, "inspect");
                case "check": return Commands.CheckCommand.Run(args, io);
                case "visual-check": return Commands.BrowserEvidenceCommand.Run(args, io, "visual-check", capture: true);
                case "browser-check": return Commands.BrowserEvidenceCommand.Run(args, io, "browser-check", capture: false);
                case "guide": return Commands.GuideCommand.Run(args, io);
                case "examples": return Commands.ExamplesCommand.Run(args, io);
                case "doctor": return Commands.DoctorCommand.Run(args, io);
                case "install-browser": return Commands.InstallBrowserCommand.Run(args, io);
                case "demo": return Commands.DemoCommand.Run(args, io);
                default:
                    throw new CliFailException($"Unknown command \"{command}\".\n\n{Usage()}");
            }
        }
        catch (CliArgumentException error) when (command is "validate" or "deliver" or "finalize" or "inspect" && args.Contains("--json"))
        {
            io.Out(Js.Stringify(ArgumentReceipt(command, error), 2));
            return 2;
        }
        catch (CliArgumentException error)
        {
            io.Err(error.Message);
            return 2;
        }
        catch (CliFailException error)
        {
            io.Err(error.Message);
            return error.ExitCode;
        }
    }

    // formatDiagnostics (A:2749-2758, diagnostics.mjs:136-146): the human failure text.
    internal static string FormatDiagnostics(string error, IReadOnlyList<Diagnostic> diagnostics) =>
        diagnostics.Count == 0 ? error : string.Join("\n", [error, .. diagnostics.Select(d =>
            $"[{d.Code}] {d.Message}{(d.SupportedFixes.Count > 0 ? $" Fix: {string.Join("; ", d.SupportedFixes)}." : "")}")]);

    // formatDiagnostics over a receipt's diagnostics, as JSON.
    internal static string FormatDiagnostics(string error, JsonArray diagnostics) =>
        diagnostics.Count == 0 ? error : string.Join("\n", [error, .. diagnostics.Select(d =>
        {
            var fixes = d!["supportedFixes"] as JsonArray ?? [];
            return $"[{d["code"]}] {d["message"]}{(fixes.Count > 0 ? $" Fix: {string.Join("; ", fixes.Select(f => f!.GetValue<string>()))}." : "")}";
        })]);

    // reportArtifactFailure (A:4251-4273): the receipt on stdout, or formatDiagnostics on stderr; returns the exit status. The
    // `update` notice is dropped (Q5).
    internal static int ReportArtifactFailure(CliIo io, bool json, ArtifactFailure failure)
    {
        if (json) io.Out(Js.Stringify(failure.ToReceipt(), 2));
        else io.Err(FormatDiagnostics(failure.Error, failure.Diagnostics));
        return failure.Status;
    }

    // A renderer failure's diagnostics: the boundary's internal/unclassified fallback names the renderer's input
    // (diagnostics.mjs:106-116), which the library cannot know.
    internal static IReadOnlyList<Diagnostic> WithInput(IReadOnlyList<Diagnostic> diagnostics, string inputPath) =>
        [.. diagnostics.Select(d => d.Code == "internal/unclassified" && d.Subject.Count == 0 ? d with { Subject = new JsonObject { ["input"] = inputPath } } : d)];

    // stage: 'input' when any diagnostic is an input/* one, else 'render' (A:6872, :6906, :6937).
    internal static string InputOrRender(IReadOnlyList<Diagnostic> diagnostics) =>
        diagnostics.Any(d => d.Code.StartsWith("input/", StringComparison.Ordinal)) ? "input" : "render";

    // reportArtifactArgumentFailure (A:4328-4344): type and input are undefined, so dropped.
    internal static JsonObject ArgumentReceipt(string command, CliArgumentException error)
    {
        var subject = new JsonObject { ["command"] = command };
        foreach (var (key, value) in error.Subject) subject[key] = value?.DeepClone();
        var diagnostic = new Diagnostic(error.Code, Severity.Error, error.Message, subject, error.Evidence, error.SupportedFixes, null);
        return new ArtifactFailure(command, "arguments", error.Message, [diagnostic]) { Status = 2 }.ToReceipt();
    }
}

// reportArtifactFailure's arguments (A:4251). A key left unset is JS undefined and drops out of the receipt; `Output`, once set,
// is present even when null (`output === undefined ? {} : { output }`).
internal sealed record ArtifactFailure(string Command, string Stage, string Error, IReadOnlyList<Diagnostic> Diagnostics)
{
    public string? Type { get; init; }
    public string? Input { get; init; }
    public bool HasOutput { get; private init; }
    public string? Output { get; init { field = value; HasOutput = true; } }
    public string? ReceiptId { get; init; }
    public string? Provenance { get; init; }
    public JsonObject? Checker { get; init; }
    public int Status { get; init; } = 1;
    // Diagnostics (by reference) that arrive as an OutputPathError's archifyDiagnostics: output-path.mjs:359-370 spreads them over
    // {severity, subject, evidence, supportedFixes}, so their keys serialise in that order, then code and message.
    public IReadOnlyCollection<Diagnostic> SpreadOrdered { get; init; } = [];

    public JsonObject ToReceipt()
    {
        var receipt = new JsonObject { ["schemaVersion"] = 1, ["ok"] = false, ["command"] = Command };
        if (!string.IsNullOrEmpty(ReceiptId)) receipt["receiptId"] = ReceiptId;
        receipt["stage"] = Stage;
        if (Type is not null) receipt["type"] = Type;
        if (Input is not null) receipt["input"] = Input;
        if (HasOutput) receipt["output"] = Output;
        receipt["error"] = Error;
        receipt["diagnostics"] = new JsonArray([.. Diagnostics.Select(d => (JsonNode)(SpreadOrdered.Any(s => ReferenceEquals(s, d)) ? SpreadJson(d) : d.ToJson()))]);
        if (!string.IsNullOrEmpty(Provenance)) receipt["provenance"] = Provenance;
        if (Checker is not null) receipt["checker"] = Checker.DeepClone();
        return receipt;
    }

    internal static JsonObject SpreadJson(Diagnostic diagnostic)
    {
        var json = diagnostic.ToJson();
        var spread = new JsonObject();
        foreach (var key in new[] { "severity", "subject", "evidence", "supportedFixes", "code", "message" })
        {
            var value = json[key];
            json.Remove(key);
            spread[key] = value;
        }
        return spread;
    }
}
