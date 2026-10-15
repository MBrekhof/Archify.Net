namespace Archify.Net.Cli.Commands;

static class InstallBrowserCommand
{
    // Q4: Playwright's installer in-process, headless shell only; its exit code passes through. No Node counterpart, so the
    // argument guards are doctor's (A:5874-5876).
    public static int Run(string[] args, CliIo io)
    {
        if (args.FirstOrDefault(arg => arg.StartsWith("--", StringComparison.Ordinal)) is { } unknown)
            throw new CliFailException($"Unknown install-browser option \"{unknown}\".");
        if (args.Length > 0) throw new CliFailException(Cli.Usage());
        return io.Services.PlaywrightMain(["install", "--only-shell", "chromium"]);
    }
}
