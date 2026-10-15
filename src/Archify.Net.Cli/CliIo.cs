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

// The seams tests replace. ponytail: the browser inspector factory (Task 10) and the finalize stage runner (Task 12) join
// as init properties when their types exist.
public sealed record CliServices
{
    public static readonly CliServices Default = new();
    // randomUUID() for receipt ids
    public Func<Guid> NewGuid { get; init; } = Guid.NewGuid;
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;
}
