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
                case "deliver": throw new NotImplementedException("Task 8");
                case "finalize": throw new NotImplementedException("Task 12");
                case "validate": throw new NotImplementedException("Task 6");
                case "inspect":
                    if (args.ElementAtOrDefault(0) != "architecture")
                    {
                        var subject = new JsonObject { ["command"] = "inspect" };
                        if (args.Length > 0) subject["type"] = args[0];
                        throw new CliArgumentException("inspect is currently supported for architecture diagrams only.", "cli/unsupported-option",
                            subject, supportedFixes: ["use an architecture diagram", "use validate <type> <input.json> --layout-json for workflow diagrams"]);
                    }
                    // commandValidate([...args, '--layout-json'], {command: 'inspect'})
                    throw new NotImplementedException("Task 6");
                case "check": throw new NotImplementedException("Task 6");
                case "visual-check": throw new NotImplementedException("Task 11");
                case "browser-check": throw new NotImplementedException("Task 11");
                case "guide": throw new NotImplementedException("Task 7");
                case "examples": return Commands.ExamplesCommand.Run(args, io);
                case "doctor": throw new NotImplementedException("Task 7");
                case "install-browser": throw new NotImplementedException("Task 7");
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

    // reportArtifactArgumentFailure (A:4328-4344) through reportArtifactFailure's receipt (A:4251-4266): type and input are
    // undefined, so dropped.
    internal static JsonObject ArgumentReceipt(string command, CliArgumentException error)
    {
        var subject = new JsonObject { ["command"] = command };
        foreach (var (key, value) in error.Subject) subject[key] = value?.DeepClone();
        var diagnostic = new Diagnostic(error.Code, Severity.Error, error.Message, subject, error.Evidence, error.SupportedFixes, null);
        return new JsonObject
        {
            ["schemaVersion"] = 1, ["ok"] = false, ["command"] = command, ["stage"] = "arguments", ["error"] = error.Message,
            ["diagnostics"] = new JsonArray(diagnostic.ToJson()),
        };
    }
}
