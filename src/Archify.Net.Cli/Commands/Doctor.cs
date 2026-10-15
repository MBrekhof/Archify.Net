using System.Text.Json.Nodes;

namespace Archify.Net.Cli.Commands;

static class DoctorCommand
{
    // Q3: upstream's per-type renderer/schema/example triple becomes the type's schema plus every embedded example of that type,
    // in TYPES order (A:12).
    static readonly (string Type, string[] Examples)[] Types =
    [
        ("architecture", ["web-app", "brand-aware-delivery", "checkout-platform.base", "checkout-platform.head", "production-deployment"]),
        ("workflow", ["agent-tool-call", "holiday-planning", "incident-response", "release-delivery"]),
        ("sequence", ["cache-miss-request", "async-job-roundtrip"]),
        ("dataflow", ["product-analytics", "event-stream"]),
        ("lifecycle", ["agent-run", "deployment-release"]),
        ("erd", ["orders", "subscription-billing"]),
        ("tree", ["payment-platform", "archify-repository"]),
        ("class", ["payments", "payment-processors"]),
        ("timeline", ["payment-incident", "archify-dev-activity"]),
        ("waterfall", ["checkout-request", "example-rebuild"]),
    ];

    static readonly string[] Locales = ["manifest", "en", "es", "ko", "zh-CN", "zh-TW"];

    // commandDoctor (A:5873-6052) under Q3: the .NET runtime, the embedded resources, git on PATH and Playwright's Chromium, in
    // upstream's line format and exit contract. A missing resource counts as a missing file, a failed probe as a failed runtime check.
    public static int Run(string[] args, CliIo io)
    {
        if (args.FirstOrDefault(arg => arg.StartsWith("--", StringComparison.Ordinal)) is { } unknown)
            throw new CliFailException($"Unknown doctor option \"{unknown}\".");
        if (args.Length > 0) throw new CliFailException(Cli.Usage());

        var services = io.Services;
        int Missing(params string[] names) => names.Count(name => !services.HasResource(name));
        // ponytail: the runtime line is always ok; a host older than net10.0 cannot load the tool to report otherwise.
        var checks = new List<(string Label, int Missing, int Failed)>
        {
            ($".NET {Environment.Version}", 0, 0),
            ("Core template", Missing("assets/template.html"), 0),
            ("Locale catalogs", Missing([.. Locales.Select(l => $"locales/{l}.json")]), 0),
            ("Scenario recipe guide", Missing("recipes/scenarios.json"), 0),
            ("Viewer settle script", Missing("viewer-gate/settle.js"), 0),
            ("Viewer metrics script", Missing("viewer-gate/metrics.js"), 0),
        };
        foreach (var (type, examples) in Types)
            checks.Add(($"{type} schema and example", Missing([$"schemas/{type}.schema.json", .. examples.Select(e => $"examples/{e}.{type}.json")]), 0));
        checks.Add(("git on PATH", 0, services.GitOnPath() ? 0 : 1));
        checks.Add(("Chromium for browser gates", 0, services.HeadlessShell() is null ? 1 : 0));

        io.Out("Archify doctor\n");
        foreach (var (label, missing, failed) in checks) io.Out($"[{(missing + failed == 0 ? "ok" : "missing")}] {label}");

        var missingFiles = checks.Sum(c => c.Missing);
        var failedRuntime = checks.Sum(c => c.Failed);
        if (missingFiles == 0 && failedRuntime == 0)
        {
            io.Out("\nArchify is ready.");
            return 0;
        }
        var problems = new List<string>();
        if (missingFiles > 0) problems.Add($"{missingFiles} required file{(missingFiles == 1 ? "" : "s")} missing");
        if (failedRuntime > 0) problems.Add($"{failedRuntime} runtime check{(failedRuntime == 1 ? "" : "s")} failed");
        io.Err($"\nArchify is not ready: {string.Join("; ", problems)}.");
        return 1;
    }

    // Core's resources plus Cli's (viewer-gate/*).
    internal static bool HasResource(string name) =>
        Resources.Exists(name) || typeof(DoctorCommand).Assembly.GetManifestResourceInfo(name) is not null;

    // A `git` executable in a PATH directory (PATHEXT extensions on Windows); nothing is run.
    internal static bool GitOnPath() => GitOnPath(Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT"));

    // An empty PATHEXT entry (";;" or a trailing ";") is no extension: an extensionless `git` file is not a Windows command.
    internal static bool GitOnPath(string? path, string? pathExt)
    {
        string[] extensions = OperatingSystem.IsWindows() ? (pathExt ?? ".EXE").Split(';', StringSplitOptions.RemoveEmptyEntries) : [""];
        return (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => extensions.Any(ext => File.Exists(Path.Join(dir.Trim('"'), "git" + ext))));
    }

    // The headless shell `install-browser` fetches (Q4): <browsers>/chromium_headless_shell-<revision>/<platform>/chrome-headless-shell,
    // the revision from the bundled driver's browsers.json. Nothing is launched.
    // ponytail: Playwright's default browsers directory only; its PLAYWRIGHT_BROWSERS_PATH override is not consulted.
    internal static string? HeadlessShell()
    {
        var browsersJson = Path.Join(AppContext.BaseDirectory, ".playwright", "package", "browsers.json");
        if (!File.Exists(browsersJson)) return null;
        var revision = JsonNode.Parse(File.ReadAllText(browsersJson))!["browsers"]!.AsArray()
            .FirstOrDefault(b => (string?)b!["name"] == "chromium-headless-shell")?["revision"]?.GetValue<string>();
        var root = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : OperatingSystem.IsMacOS() ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches")
            : Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        var install = new DirectoryInfo(Path.Join(root, "ms-playwright", $"chromium_headless_shell-{revision}"));
        if (revision is null || !install.Exists) return null;
        var executable = OperatingSystem.IsWindows() ? "chrome-headless-shell.exe" : "chrome-headless-shell";
        return install.EnumerateDirectories().Select(platform => Path.Join(platform.FullName, executable)).FirstOrDefault(File.Exists);
    }
}
