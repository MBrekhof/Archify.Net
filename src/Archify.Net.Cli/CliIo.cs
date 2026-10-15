namespace Archify.Net.Cli;

// Q2: the in-process I/O contract. The writers carry UTF-8 without BOM (Program.Main); every line ends in "\n" on every OS,
// so commands write through Out/Err and never call WriteLine (which writes "\r\n" on Windows).
public sealed record CliIo(TextWriter Stdout, TextWriter Stderr, string Cwd, CliServices Services)
{
    // console.log(text)
    public void Out(string text) => Stdout.Write(text + "\n");
    // console.error(text)
    public void Err(string text) => Stderr.Write(text + "\n");
}

// The seams tests replace. ponytail: the finalize stage runner (Task 12) joins as an init property when its type exists.
public sealed record CliServices
{
    public static readonly CliServices Default = new();
    // randomUUID() for receipt ids
    public Func<Guid> NewGuid { get; init; } = Guid.NewGuid;
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;
    // doctor's lookups and probes (Q3)
    public Func<string, bool> HasResource { get; init; } = Commands.DoctorCommand.HasResource;
    public Func<bool> GitOnPath { get; init; } = Commands.DoctorCommand.GitOnPath;
    public Func<string?> HeadlessShell { get; init; } = Commands.DoctorCommand.HeadlessShell;
    // install-browser's in-process installer (Q4)
    public Func<string[], int> PlaywrightMain { get; init; } = Microsoft.Playwright.Program.Main;
    // browser-check / visual-check (Q4): Playwright's pinned Chromium, the headless shell doctor probes for
    public Browser.IBrowserInspectorFactory BrowserInspectors { get; init; } = new Browser.PlaywrightInspectorFactory(Commands.DoctorCommand.HeadlessShell);
}
