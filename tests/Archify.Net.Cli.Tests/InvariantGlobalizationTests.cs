using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace Archify.Net.Cli.Tests;

// GLOB-001: in a host with System.Globalization.Invariant on, no culture lookup may throw. The CLI runs in a child process under a
// copy of its runtimeconfig with the switch on. Only success is asserted, not golden bytes: invariant mode compares ordinally, so
// LocaleCompare orderings may differ from ICU's (a stated deviation).
public class InvariantGlobalizationTests
{
    [Fact]
    public async Task Render_succeeds_in_an_invariant_globalization_host()
    {
        var dll = typeof(Cli).Assembly.Location;
        var work = Directory.CreateTempSubdirectory("archify-invariant-").FullName;
        try
        {
            var config = JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(dll, ".runtimeconfig.json")))!;
            var options = config["runtimeOptions"]!.AsObject();
            if (options["configProperties"] is not JsonObject properties) options["configProperties"] = properties = [];
            properties["System.Globalization.Invariant"] = true;
            var runtimeConfig = Path.Join(work, "invariant.runtimeconfig.json");
            File.WriteAllText(runtimeConfig, config.ToJsonString());
            var input = Path.Join(work, "web-app.architecture.json");
            File.Copy(Path.Join(CliTestHost.RepoRoot, "upstream", "examples", "web-app.architecture.json"), input);
            var output = Path.Join(work, "out.html");

            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = work };
            foreach (var arg in new[] { "exec", "--runtimeconfig", runtimeConfig, dll, "render", "architecture", input, output }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(120)).Token);
            Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}\nstdout: {await stdout}\nstderr: {await stderr}");
            Assert.True(File.Exists(output));
        }
        finally { Directory.Delete(work, recursive: true); }
    }
}
