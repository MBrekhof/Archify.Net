using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Archify.Net.Cli.Tests;

// A Node-minted CLI fixture (tools/capture-cli-fixtures.mjs): `files` maps workdir names to repo-relative sources; `setup` lists the
// steps run in the workdir before the recorded command.
public sealed record CliFixture(string Name, string[] Args, IReadOnlyDictionary<string, string> Files, int Exit, string Stdout, string Stderr,
    JsonArray? Setup = null)
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
        // JSON.parse: an escaped lone surrogate in `args` (guide/lone-surrogate) stays one.
        var json = DiagramLoader.ParseNode(ReadLf(Path.Combine(FixtureRoot, rel.Replace('/', Path.DirectorySeparatorChar) + ".json")))!.AsObject();
        return new CliFixture(rel,
            [.. json["args"]!.AsArray().Select(a => a!.GetValue<string>())],
            json["files"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()),
            (int)json["exit"]!.GetValue<double>(), json["stdout"]!.GetValue<string>(), json["stderr"]!.GetValue<string>(), json["setup"]?.AsArray());
    }

    // The capture script's applySetup, step for step: CLI runs go through Cli.Run in the workdir.
    public static void Replay(string work, JsonArray steps)
    {
        string At(string relative) => Path.Join(work, relative);
        string Text(JsonObject step, string key) => step[key]!.GetValue<string>();
        foreach (var step in steps.Select(s => s!.AsObject()))
        {
            if (step["run"] is JsonArray run)
            {
                var output = new StringWriter();
                var exit = Cli.Run([.. run.Select(a => a!.GetValue<string>())], new CliIo(output, output, work, CliServices.Default));
                Assert.True(step["exit"]!.GetValue<double>() == exit, $"setup {Js.Stringify(run)} exited {exit}: {output}");
            }
            else if (step.ContainsKey("copy")) File.Copy(At(Text(step, "copy")), At(Text(step, "to")), overwrite: true);
            else if (step.ContainsKey("delete")) File.Delete(At(Text(step, "delete")));
            else if (step.ContainsKey("append")) File.AppendAllText(At(Text(step, "append")), Text(step, "text"), new UTF8Encoding(false));
            else if (step.ContainsKey("write")) File.WriteAllText(At(Text(step, "write")), Text(step, "text"), new UTF8Encoding(false));
            else if (step.ContainsKey("set"))
            {
                var target = At(Text(step, "set"));
                var json = JsonNode.Parse(ReadLf(target))!.AsObject();
                var value = step["value"]!.DeepClone();
                json[Text(step, "key")] = value is JsonValue text && text.TryGetValue<string>(out var s) ? s.Replace("<WORK>", work) : value;
                File.WriteAllText(target, Js.Stringify(json, 2) + "\n", new UTF8Encoding(false));
            }
            else if (step.ContainsKey("mkdir")) Directory.CreateDirectory(At(Text(step, "mkdir")));
            else if (step.ContainsKey("symlink")) PathSemanticsTests.FileSymlink(At(Text(step, "symlink")), Text(step, "target"));
            else if (step.ContainsKey("replace"))
            {
                var target = At(Text(step, "replace"));
                var text = ReadLf(target);
                Assert.Equal(2, text.Split(Text(step, "from")).Length);
                File.WriteAllText(target, text.Replace(Text(step, "from"), Text(step, "to")), new UTF8Encoding(false));
            }
            else if (step.ContainsKey("denyCreate")) DenyCreate(At(Text(step, "denyCreate")));
            else throw new InvalidOperationException($"unknown setup step {Js.Stringify(step)}");
        }
    }

    // icacls <dir> /deny *S-1-1-0:(WD,AD): Everyone may not create files or subdirectories in this directory (not inherited). Owning
    // the directory is enough; no elevation.
    public static void DenyCreate(string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("denyCreate is a Windows ACL");
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    // Fixture names ("help/x") in a group, optionally only those whose file name starts with a prefix.
    public static IEnumerable<string> FixtureNames(string group, string prefix = "") =>
        Directory.GetFiles(Path.Combine(FixtureRoot, group), prefix + "*.json").Select(f => $"{group}/{Path.GetFileNameWithoutExtension(f)}").Order(StringComparer.Ordinal);

    // Runs Cli.Run in a fresh temp workdir seeded from `seedFiles` (workdir name -> repo-relative source), tokenises the output
    // like the fixtures (Q8), and deletes the workdir; `inspect(workdir)` runs just before that.
    public static (int Exit, string Stdout, string Stderr) Run(string[] args, IReadOnlyDictionary<string, string>? seedFiles = null, CliServices? services = null,
        Action<string>? inspect = null, JsonArray? setup = null)
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
            if (setup is not null) Replay(work, setup);
            // StringWriter keeps NewLine "\r\n", so a stray WriteLine shows up as a byte difference.
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exit = Cli.Run(args, new CliIo(stdout, stderr, work, services ?? CliServices.Default));
            inspect?.Invoke(work);
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
    public static void AssertFixture(CliFixture fixture, Action<string>? inspect = null)
    {
        var (exit, stdout, stderr) = Run(fixture.Args, fixture.Files, inspect: inspect, setup: fixture.Setup);
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
