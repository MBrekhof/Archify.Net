using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Archify.Net.Cli.Tests;

// A Node-minted CLI fixture (tools/capture-cli-fixtures.mjs): `files` maps workdir names to repo-relative sources.
public sealed record CliFixture(string Name, string[] Args, IReadOnlyDictionary<string, string> Files, int Exit, string Stdout, string Stderr)
{
    public override string ToString() => Name;
}

public static class CliTestHost
{
    // tests/Archify.Net.Cli.Tests/bin/<cfg>/net10.0 -> repo root is 5 levels up
    public static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    public static readonly string FixtureRoot = Path.Combine(RepoRoot, "tests", "fixtures", "cli");

    public static string ReadLf(string path) => File.ReadAllText(path, new UTF8Encoding(false));

    // "help/help" -> tests/fixtures/cli/help/help.json
    public static CliFixture Fixture(string rel)
    {
        var json = JsonNode.Parse(ReadLf(Path.Combine(FixtureRoot, rel.Replace('/', Path.DirectorySeparatorChar) + ".json")))!.AsObject();
        return new CliFixture(rel,
            [.. json["args"]!.AsArray().Select(a => a!.GetValue<string>())],
            json["files"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()),
            json["exit"]!.GetValue<int>(), json["stdout"]!.GetValue<string>(), json["stderr"]!.GetValue<string>());
    }

    // Fixture names ("help/x") in a group, optionally only those whose file name starts with a prefix.
    public static IEnumerable<string> FixtureNames(string group, string prefix = "") =>
        Directory.GetFiles(Path.Combine(FixtureRoot, group), prefix + "*.json").Select(f => $"{group}/{Path.GetFileNameWithoutExtension(f)}").Order(StringComparer.Ordinal);

    // Runs Cli.Run in a fresh temp workdir seeded from `seedFiles` (workdir name -> repo-relative source), tokenises the output
    // like the fixtures (Q8), and deletes the workdir.
    public static (int Exit, string Stdout, string Stderr) Run(string[] args, IReadOnlyDictionary<string, string>? seedFiles = null, CliServices? services = null)
    {
        var work = Directory.CreateTempSubdirectory("archify-cli-").FullName;
        try
        {
            foreach (var (name, source) in seedFiles ?? new Dictionary<string, string>())
            {
                var target = Path.Combine(work, name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(RepoRoot, source.Replace('/', Path.DirectorySeparatorChar)), target);
            }
            // StringWriter keeps NewLine "\r\n", so a stray WriteLine shows up as a byte difference.
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = Cli.Run(args, new CliIo(stdout, stderr, work, services ?? CliServices.Default));
            return (exit, Tokenise(stdout.ToString(), work), Tokenise(stderr.ToString(), work));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // Q8: the workdir JSON-escaped, then raw, becomes <WORK>.
    public static string Tokenise(string text, string work) => text.Replace(work.Replace("\\", "\\\\"), "<WORK>").Replace(work, "<WORK>");

    // Runs a fixture and asserts exit, stdout and stderr byte-equal after UsageEdit (Q3).
    public static void AssertFixture(CliFixture fixture)
    {
        var (exit, stdout, stderr) = Run(fixture.Args, fixture.Files);
        Assert.Equal(UsageEdit.Apply(fixture.Stdout), stdout);
        Assert.Equal(UsageEdit.Apply(fixture.Stderr), stderr);
        Assert.Equal(fixture.Exit, exit);
    }
}

// Q3: upstream's usage() -> the port's, in raw text and in JSON-escaped text (cli/usage receipts). Applied to every fixture.
public static class UsageEdit
{
    static readonly string[] Out = ["compare", "preview", "migrate", "brands"];

    // usage() as Node printed it: the help fixture minus console.log's "\n".
    public static readonly string Upstream = CliTestHost.Fixture("help/help").Stdout[..^1];
    public static readonly string Edited = Edit(Upstream);

    static string Edit(string usage)
    {
        var lines = usage.Split('\n').ToList();
        var removed = lines.RemoveAll(l => Out.Any(c => l.StartsWith($"  archify {c} ", StringComparison.Ordinal)));
        var doctor = lines.IndexOf("  archify doctor");
        if (removed != 5 || doctor < 0) throw new InvalidOperationException("help/help no longer has upstream's usage() shape.");
        lines.Insert(doctor + 1, "  archify install-browser");
        return string.Join("\n", lines);
    }

    static string JsonEscaped(string s) => s.Replace("\n", "\\n");

    public static string Apply(string text) => text.Replace(Upstream, Edited).Replace(JsonEscaped(Upstream), JsonEscaped(Edited));
}
