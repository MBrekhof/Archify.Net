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
    // the directory is enough; no elevation. icacls, not FileSystemAccessRule: when %TEMP% carries explicit ACEs (a sandbox tool
    // added some), new directories inherit a non-canonical ACL that .NET refuses to modify; icacls reorders it.
    public static void DenyCreate(string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("denyCreate is a Windows ACL");
        using var icacls = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("icacls", [directory, "/deny", "*S-1-1-0:(WD,AD)"])
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var output = icacls.StandardOutput.ReadToEnd() + icacls.StandardError.ReadToEnd();
        icacls.WaitForExit();
        if (icacls.ExitCode != 0) throw new InvalidOperationException($"icacls exited {icacls.ExitCode}: {output}");
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
public static partial class UsageEdit
{
    static readonly string[] Out = ["compare", "preview", "migrate", "brands"];

    // usage() as Node printed it: the help fixture minus console.log's "\n".
    public static readonly string Upstream = CliTestHost.Fixture("help/help").Stdout[..^1];
    public static readonly string Edited = Edit(Upstream);

    // The explainer type is not ported until PORT-009: its usage lines and its name in the type list are stripped, each strip a
    // separate step that must match exactly once.
    const string ExplainerType = ", explainer";
    const string WritingBlock = "\nExplainer writing checks (validate, deliver, finalize, patch):\n  --writing warn|strict|off (default: warn)\n";

    // The Types line of upstream's usage(), and the port's.
    static readonly string UpstreamTypes = Upstream.Split('\n').SkipWhile(l => l != "Types:").ElementAt(1)[2..];
    static readonly string EditedTypes = ReplaceOnce(UpstreamTypes, ExplainerType, "");
    static readonly string TypesHead = UpstreamTypes[..(UpstreamTypes.IndexOf(", ", StringComparison.Ordinal) + 2)];   // "architecture, "

    static string ReplaceOnce(string text, string part, string replacement)
    {
        var at = text.IndexOf(part, StringComparison.Ordinal);
        if (at < 0 || text.IndexOf(part, at + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"help/help no longer has upstream's usage() shape: {Js.Stringify(part)} is not there exactly once.");
        return text.Remove(at, part.Length).Insert(at, replacement);
    }

    static string Edit(string usage)
    {
        usage = ReplaceOnce(usage, WritingBlock, "");
        usage = ReplaceOnce(usage, ExplainerType + "\n", "\n");   // the Types line
        var lines = usage.Split('\n').ToList();
        if (lines.RemoveAll(l => l.StartsWith("  archify patch ", StringComparison.Ordinal)) != 1)
            throw new InvalidOperationException("help/help no longer has upstream's usage() shape: one patch line.");
        var removed = lines.RemoveAll(l => Out.Any(c => l.StartsWith($"  archify {c} ", StringComparison.Ordinal)));
        var doctor = lines.IndexOf("  archify doctor");
        if (removed != 5 || doctor < 0) throw new InvalidOperationException("help/help no longer has upstream's usage() shape.");
        lines.Insert(doctor + 1, "  archify install-browser");
        return string.Join("\n", lines);
    }

    static string JsonEscaped(string s) => s.Replace("\n", "\\n");

    public static string Apply(string text) => UnknownType(text.Replace(Upstream, Edited).Replace(JsonEscaped(Upstream), JsonEscaped(Edited)));

    // rendererPath's unknown-type error lists TYPES in its message, its fix and evidence.supportedTypes; every listing must be
    // upstream's (with explainer), else this edit fails loudly. A prefix is a TYPES listing only when TYPES' first entry follows it,
    // so another message's "use one of: " is left alone; an unknown-type text with no listing at all also fails.
    static string UnknownType(string text)
    {
        var listings = 0;
        foreach (var prefix in new[] { "Expected one of: ", "use one of: " })
        {
            var count = text.Split(prefix + TypesHead).Length - 1;
            if (count == 0) continue;
            if (text.Split(prefix + UpstreamTypes).Length - 1 != count)
                throw new InvalidOperationException($"an unknown-type listing after {Js.Stringify(prefix)} is not upstream's TYPES with explainer.");
            text = text.Replace(prefix + UpstreamTypes, prefix + EditedTypes);
            listings += count;
        }
        if (listings == 0 && (text.Contains("Unknown diagram type", StringComparison.Ordinal) || text.Contains("cli/unknown-diagram-type", StringComparison.Ordinal)))
            throw new InvalidOperationException("an unknown-type message no longer lists upstream's TYPES after \"Expected one of: \".");
        var arrays = text.Split("\"supportedTypes\": [").Length - 1;
        if (arrays == 0) return text;
        var edited = SupportedTypesExplainer().Replace(text, "\"waterfall\"\n");
        if (SupportedTypesExplainer().Count(text) != arrays)
            throw new InvalidOperationException("an evidence.supportedTypes array does not end in \"waterfall\", \"explainer\".");
        return edited;
    }

    [System.Text.RegularExpressions.GeneratedRegex("\"waterfall\",\n +\"explainer\"\n")]
    private static partial System.Text.RegularExpressions.Regex SupportedTypesExplainer();
}
