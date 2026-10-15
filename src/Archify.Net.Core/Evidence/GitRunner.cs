using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Archify.Net;

/// <summary>
/// Node's <c>spawnSync('git', ['--no-replace-objects', '-C', root, ...args], { maxBuffer, env, input })</c> as
/// repository-evidence.mjs:23-36 and :54-62 use it: stdout as bytes, stderr drained and discarded, no timeout.
/// </summary>
/// <remarks>
/// Spawn failures carry Node's error text, synthesised: <c>spawnSync git ENOENT</c>, <c>spawnSync git ENOBUFS</c>, or
/// <c>spawnSync git EACCES</c>. Stated deviation: any other start errno prints its numeric code where Node prints its name.
/// </remarks>
internal sealed class GitRunner
{
    /// <summary>What is started. The diagnostic text always says <c>git</c>, the file upstream passes to spawnSync; tests point
    /// this at a missing executable to stand for a PATH without git.</summary>
    internal string Executable { get; init; } = "git";

    /// <summary>Added to the inherited environment: upstream's isolation of the child (no lazy fetch, no transport), not configuration
    /// of the port. Tests add <c>GIT_TRACE</c> here.</summary>
    internal Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal) { ["GIT_NO_LAZY_FETCH"] = "1", ["GIT_ALLOW_PROTOCOL"] = "" };

    internal readonly record struct Result(string? Error, int Status, byte[] Stdout)
    {
        public string Text => new UTF8Encoding(false).GetString(Stdout);   // encoding: 'utf8' (U+FFFD on invalid bytes)
    }

    public Result Run(string root, IReadOnlyList<string> args, int maxBuffer, string? input = null)
    {
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in (string[])["--no-replace-objects", "-C", root, .. args]) start.ArgumentList.Add(arg);
        foreach (var (key, value) in Environment) start.Environment[key] = value;
        Process process;
        try { process = Process.Start(start)!; }
        catch (Win32Exception error) { return new($"spawnSync git {ErrnoName(error.NativeErrorCode)}", -1, []); }
        using (process)
        {
            // maxBuffer counts stdout and stderr together and trips only when strictly exceeded; the child is then killed, and what
            // it still writes is drained and dropped. Kill races the child's own exit: every exception that race throws is ignored.
            long buffered = 0;
            var overflow = 0;
            bool Count(int length)
            {
                if (Interlocked.Add(ref buffered, length) <= maxBuffer) return true;
                if (Interlocked.Exchange(ref overflow, 1) == 0)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception error) when (error is InvalidOperationException or Win32Exception or AggregateException) { }
                }
                return false;
            }
            // stdin runs beside the stdout read: `cat-file --batch` streams answers while it reads, so a large input would deadlock.
            var stdin = Task.Run(() =>
            {
                try
                {
                    if (input is not null) process.StandardInput.BaseStream.Write(Encoding.UTF8.GetBytes(input));
                    process.StandardInput.Close();
                }
                catch (IOException) { }   // the child exited first (EPIPE), as Node ignores
            });
            var stderr = Task.Run(() => Drain(process.StandardError.BaseStream, null, Count));
            var stdout = new MemoryStream();
            Drain(process.StandardOutput.BaseStream, stdout, Count);
            Task.WaitAll(stdin, stderr);
            process.WaitForExit();
            return overflow == 1 ? new("spawnSync git ENOBUFS", -1, []) : new(null, process.ExitCode, stdout.ToArray());
        }
    }

    static void Drain(Stream from, MemoryStream? into, Func<int, bool> withinLimit)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = from.Read(buffer)) > 0) if (withinLimit(read)) into?.Write(buffer, 0, read);
    }

    // libuv's names for the start failures a missing or unreadable git produces (ERROR_PATH_NOT_FOUND is ENOENT on Windows too).
    // ponytail: two names; add rows when another errno is ever met.
    static string ErrnoName(int code) => (OperatingSystem.IsWindows(), code) switch
    {
        (_, 2) or (true, 3) => "ENOENT",
        (true, 5) or (false, 13) => "EACCES",
        _ => code.ToString(CultureInfo.InvariantCulture),
    };
}
