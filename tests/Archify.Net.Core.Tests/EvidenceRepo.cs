using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Archify.Net.Tests;

// survey-evidence §5 / P8: the evidence fixture repo, built from the checked-in fast-import stream exactly as tools/capture-fixtures.mjs
// builds it. Its git children run with GIT_* scrubbed and no system or global config, so nothing on the machine changes the HEAD.
internal static class EvidenceRepo
{
    public const string PinnedHead = "77eb757055fa2c17900278114600cfe23c3957d1";

    public static (string Root, string Head) Create(string? originUrl)
    {
        var root = Directory.CreateTempSubdirectory("archify-evidence-").FullName;
        Git(root, "init", "--quiet", "--template=", "--initial-branch=main");
        Git(root, File.ReadAllBytes(Fixtures.Fixture("evidence/evidence-repo.fi")), "fast-import", "--quiet");
        if (originUrl is not null) Git(root, "remote", "add", "origin", originUrl);
        var head = Git(root, "rev-parse", "HEAD");
        if (head != PinnedHead) throw new InvalidOperationException($"evidence-repo.fi built {head}, expected {PinnedHead}");
        return (root, head);
    }

    public static string Git(string root, params string[] args) => Git(root, null, args);

    public static string Git(string root, byte[]? input, params string[] args)
    {
        var start = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in (string[])["-C", root, .. args]) start.ArgumentList.Add(arg);
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToList()) start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        Process process;
        try { process = Process.Start(start)!; }
        catch (Win32Exception error) { throw new InvalidOperationException("The evidence tests need git on PATH, and it could not be started.", error); }
        using (process)
        {
            var stdin = Task.Run(() => { if (input is not null) process.StandardInput.BaseStream.Write(input); process.StandardInput.Close(); });
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            stdin.Wait();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} exited {process.ExitCode}: {stderr.Result}");
            return stdout.Trim();
        }
    }

    // Pack files are read-only, which Directory.Delete refuses on Windows.
    public static void Delete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
    }

    // <ROOT> for the temp root in both spellings (OS separators, git's forward slashes), on every string value: as the capture does.
    static string Tokenise(string s, string root) => s.Replace(root, "<ROOT>").Replace(root.Replace('\\', '/'), "<ROOT>");

    static JsonNode? Tokenise(JsonNode? node, string root) => node switch
    {
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, Tokenise(kv.Value, root)))),
        JsonArray a => new JsonArray([.. a.Select(x => Tokenise(x, root))]),
        JsonValue v when v.TryGetValue<string>(out var s) => JsonValue.Create(Tokenise(s, root)),
        _ => node?.DeepClone(),
    };

    public static Diagnostic Tokenise(Diagnostic d, string root) => d with
    {
        Message = Tokenise(d.Message, root),
        Subject = (JsonObject)Tokenise(d.Subject, root)!,
        Evidence = (JsonObject)Tokenise(d.Evidence, root)!,
        SupportedFixes = [.. d.SupportedFixes.Select(f => Tokenise(f, root))],
    };
}
