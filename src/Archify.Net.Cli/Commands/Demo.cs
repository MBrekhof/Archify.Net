using Archify.Net.Cli.Paths;

namespace Archify.Net.Cli.Commands;

static class DemoCommand
{
    // commandDemo (A:6163-6184): the bundled web-app example to <dir>/archify-demo.html; the option guard runs before any mkdir.
    public static int Run(string[] args, CliIo io)
    {
        if (args.FirstOrDefault(arg => arg.StartsWith("--", StringComparison.Ordinal)) is { } unknown)
            throw new CliFailException($"Unknown demo option \"{unknown}\".");
        if (args.Length > 1) throw new CliFailException(Cli.Usage());

        var outputDirectory = NodePath.Resolve(io.Cwd, args is [{ Length: > 0 } directory] ? directory : io.Cwd);
        var output = Path.Join(outputDirectory, "archify-demo.html");
        try { Directory.CreateDirectory(outputDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CliFailException($"Could not create demo directory \"{outputDirectory}\": {error.Message}", 1);
        }

        var status = RenderCommand.RenderToFile(io, DiagramType.Architecture,
            () => DiagramLoader.Parse(Resources.Example("web-app.architecture.json")), null, output, new RenderOptions());
        if (status != 0) return status;

        io.Out($"\nDemo ready: {output}");
        io.Out("Next: open the HTML in your browser, then render your own diagram:");
        io.Out("  archify render architecture <input.json> <output.html>");
        return 0;
    }
}
