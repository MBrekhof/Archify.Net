using System.Text.Json;
using Archify.Net.Cli;
using Xunit;

namespace Archify.Net.Mcp.Tests;

public sealed class McpToolCatalogTests
{
    private static readonly string[] ExpectedTools =
    [
        "archify_finalize",
        "archify_guide",
        "archify_patch",
        "archify_render",
        "archify_validate",
    ];

    [Fact]
    public async Task ListToolsAsync_ExposesExactFiveToolsWithDescriptionsAndSchemas()
    {
        await using var host = await McpTestHost.StartAsync();
        var tools = await host.Client.ListToolsAsync();

        var names = tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(ExpectedTools, names);

        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), $"Tool {tool.Name} is missing a description.");
            Assert.Equal(JsonValueKind.Object, tool.JsonSchema.ValueKind);
        }
    }

    [Fact]
    public async Task GuideTool_List_ScenarioRecommendation_Language_AndInvalidLang()
    {
        await using var host = await McpTestHost.StartAsync();

        var listResult = await host.Client.CallToolAsync("archify_guide", new Dictionary<string, object?>());
        Assert.NotEqual(true, listResult.IsError);
        using var listDoc = JsonDocument.Parse(McpTestHost.ExtractSingleText(listResult));
        Assert.True(listDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("list", listDoc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("en", listDoc.RootElement.GetProperty("lang").GetString());
        Assert.True(listDoc.RootElement.GetProperty("recipes").GetArrayLength() >= 10);

        var emptyOptResult = await host.Client.CallToolAsync("archify_guide", new Dictionary<string, object?>
        {
            ["query"] = "   ",
            ["lang"] = "",
        });
        Assert.NotEqual(true, emptyOptResult.IsError);
        using var emptyOptDoc = JsonDocument.Parse(McpTestHost.ExtractSingleText(emptyOptResult));
        Assert.Equal("list", emptyOptDoc.RootElement.GetProperty("mode").GetString());

        var recResult = await host.Client.CallToolAsync("archify_guide", new Dictionary<string, object?>
        {
            ["query"] = "approval gate for leave requests",
        });
        Assert.NotEqual(true, recResult.IsError);
        using var recDoc = JsonDocument.Parse(McpTestHost.ExtractSingleText(recResult));
        Assert.True(recDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("recommendation", recDoc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("workflow", recDoc.RootElement.GetProperty("recommendation").GetProperty("type").GetString());

        var zhResult = await host.Client.CallToolAsync("archify_guide", new Dictionary<string, object?>
        {
            ["query"] = "发布流程",
            ["lang"] = "zh",
        });
        Assert.NotEqual(true, zhResult.IsError);
        using var zhDoc = JsonDocument.Parse(McpTestHost.ExtractSingleText(zhResult));
        Assert.Equal("zh", zhDoc.RootElement.GetProperty("lang").GetString());
        Assert.Equal("workflow", zhDoc.RootElement.GetProperty("recommendation").GetProperty("type").GetString());

        var badLang = await host.Client.CallToolAsync("archify_guide", new Dictionary<string, object?>
        {
            ["lang"] = "fr",
        });
        Assert.True(badLang.IsError);
        Assert.Contains("--lang must be \"en\" or \"zh\"", McpTestHost.ExtractSingleText(badLang), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateTool_Pass_LayoutJson_Fail_AndInvalidCwd()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "archify-mcp-validate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sourceSpec = Path.Combine(McpTestHost.RepoRoot, "upstream", "examples", "web-app.architecture.json");
            var specPath = Path.Combine(tempDir, "web-app.architecture.json");
            File.Copy(sourceSpec, specPath);

            var subDir = Path.Combine(tempDir, "nested");
            Directory.CreateDirectory(subDir);
            File.Copy(sourceSpec, Path.Combine(subDir, "nested-spec.json"));

            await using var host = await McpTestHost.StartAsync(tempDir);

            // Exit 0: valid spec with absent cwd and empty optional repoRoot
            var passResult = await host.Client.CallToolAsync("archify_validate", new Dictionary<string, object?>
            {
                ["type"] = "architecture",
                ["input"] = "web-app.architecture.json",
                ["quality"] = "showcase",
                ["repoRoot"] = "   ",
                ["cwd"] = "",
            });
            Assert.NotEqual(true, passResult.IsError);
            using var passDoc = JsonDocument.Parse(McpTestHost.ExtractSingleText(passResult));
            Assert.True(passDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("validate", passDoc.RootElement.GetProperty("command").GetString());
            Assert.Equal("architecture", passDoc.RootElement.GetProperty("type").GetString());
            Assert.True(passDoc.RootElement.GetProperty("candidateFrozen").GetBoolean());
            Assert.Equal(9, passDoc.RootElement.GetProperty("checks").GetArrayLength());
            Assert.Equal("pass", passDoc.RootElement.GetProperty("composition").GetProperty("status").GetString());

            // Relative cwd resolved against DefaultCwd
            var relCwdResult = await host.Client.CallToolAsync("archify_validate", new Dictionary<string, object?>
            {
                ["type"] = "architecture",
                ["input"] = "nested-spec.json",
                ["cwd"] = "nested",
            });
            Assert.NotEqual(true, relCwdResult.IsError);
            using var relCwdDoc = JsonDocument.Parse(McpTestHost.ExtractSingleText(relCwdResult));
            Assert.True(relCwdDoc.RootElement.GetProperty("ok").GetBoolean());

            // Layout JSON mode
            var layoutResult = await host.Client.CallToolAsync("archify_validate", new Dictionary<string, object?>
            {
                ["type"] = "architecture",
                ["input"] = "web-app.architecture.json",
                ["layoutJson"] = true,
            });
            Assert.NotEqual(true, layoutResult.IsError);
            using var layoutDoc = JsonDocument.Parse(McpTestHost.ExtractSingleText(layoutResult));
            Assert.True(layoutDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("architecture", layoutDoc.RootElement.GetProperty("diagram_type").GetString());
            Assert.Equal(10, layoutDoc.RootElement.GetProperty("components").GetArrayLength());

            // Exit 1: validation failure on broken JSON
            var badSpecPath = Path.Combine(tempDir, "broken.json");
            await File.WriteAllTextAsync(badSpecPath, "{ not valid json ");

            var failResult = await host.Client.CallToolAsync("archify_validate", new Dictionary<string, object?>
            {
                ["type"] = "architecture",
                ["input"] = "broken.json",
            });
            Assert.True(failResult.IsError);
            using var failDoc = JsonDocument.Parse(McpTestHost.ExtractSingleText(failResult));
            Assert.False(failDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("validate", failDoc.RootElement.GetProperty("command").GetString());
            Assert.Equal("input/json-parse", failDoc.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());

            // Exit 2: CLI argument refusal (--layout-json on sequence diagram)
            var exit2Result = await host.Client.CallToolAsync("archify_validate", new Dictionary<string, object?>
            {
                ["type"] = "sequence",
                ["input"] = "web-app.architecture.json",
                ["layoutJson"] = true,
            });
            Assert.True(exit2Result.IsError);
            using var exit2Doc = JsonDocument.Parse(McpTestHost.ExtractSingleText(exit2Result));
            Assert.False(exit2Doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("arguments", exit2Doc.RootElement.GetProperty("stage").GetString());
            Assert.Equal("cli/unsupported-option", exit2Doc.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());

            // Missing cwd directory
            var badCwdResult = await host.Client.CallToolAsync("archify_validate", new Dictionary<string, object?>
            {
                ["type"] = "architecture",
                ["input"] = "web-app.architecture.json",
                ["cwd"] = Path.Combine(tempDir, "does-not-exist"),
            });
            Assert.True(badCwdResult.IsError);
            using var cwdDoc = JsonDocument.Parse(McpTestHost.ExtractSingleText(badCwdResult));
            Assert.False(cwdDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("input/cwd-invalid", cwdDoc.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());

            // File path passed as cwd
            var fileCwdResult = await host.Client.CallToolAsync("archify_validate", new Dictionary<string, object?>
            {
                ["type"] = "architecture",
                ["input"] = "web-app.architecture.json",
                ["cwd"] = specPath,
            });
            Assert.True(fileCwdResult.IsError);
            using var fileCwdDoc = JsonDocument.Parse(McpTestHost.ExtractSingleText(fileCwdResult));
            Assert.False(fileCwdDoc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("input/cwd-invalid", fileCwdDoc.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CancellationBeforeStart_AndConcurrentCallsSerializeViaSemaphore()
    {
        // 1. Cancellation before run starts throws OperationCanceledException immediately
        using (var tools = new ArchifyTools())
        {
            using var cancelledCts = new CancellationTokenSource();
            await cancelledCts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => tools.GuideAsync(cancellationToken: cancelledCts.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => tools.ValidateAsync("architecture", "in.json", cancellationToken: cancelledCts.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => tools.RenderAsync("architecture", "in.json", cancellationToken: cancelledCts.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => tools.FinalizeAsync("architecture", "in.json", "out.html", cancellationToken: cancelledCts.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => tools.PatchAsync("a.html", "panel", "r.md", "out.html", cancellationToken: cancelledCts.Token));
        }

        // 2. Two concurrent calls serialize via the semaphore without interleaving
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = 0;
        var maxConcurrent = 0;

        var customServices = CliServices.Default with
        {
            NewGuid = () =>
            {
                var current = Interlocked.Increment(ref inFlight);
                int snapshot;
                do
                {
                    snapshot = maxConcurrent;
                    if (current <= snapshot)
                    {
                        break;
                    }
                }
                while (Interlocked.CompareExchange(ref maxConcurrent, current, snapshot) != snapshot);

                firstEntered.TrySetResult();
                releaseFirst.Task.GetAwaiter().GetResult();
                Interlocked.Decrement(ref inFlight);
                return Guid.NewGuid();
            },
        };

        var tempDir = Path.Combine(Path.GetTempPath(), "archify-mcp-concurrency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sourceSpec = Path.Combine(McpTestHost.RepoRoot, "upstream", "examples", "web-app.architecture.json");
            File.Copy(sourceSpec, Path.Combine(tempDir, "web-app.architecture.json"));

            await using var host = await McpTestHost.StartAsync(tempDir, customServices);

            // Render with empty optional parameters writes default <type>.html and returns exit 0
            var renderResult = await host.Client.CallToolAsync("archify_render", new Dictionary<string, object?>
            {
                ["type"] = "architecture",
                ["input"] = "web-app.architecture.json",
                ["output"] = "   ",
                ["quality"] = "",
                ["repoRoot"] = " ",
                ["cwd"] = "",
            });
            Assert.NotEqual(true, renderResult.IsError);
            var renderedPath = McpTestHost.ExtractSingleText(renderResult);
            Assert.True(File.Exists(renderedPath));

            // Finalize calls Services.NewGuid() inside DeliverCommand; using missing.json exercises the lock without browser overhead
            var finalizeArgs = new Dictionary<string, object?>
            {
                ["type"] = "architecture",
                ["input"] = "missing.json",
                ["output"] = "out.html",
                ["receipt"] = "",
                ["outDir"] = "   ",
                ["quality"] = "",
                ["repoRoot"] = "",
                ["candidateSha256"] = " ",
                ["cwd"] = "",
            };

            var call1 = host.Client.CallToolAsync("archify_finalize", finalizeArgs).AsTask();
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var call2 = host.Client.CallToolAsync("archify_finalize", finalizeArgs).AsTask();
            await Task.Delay(50);
            Assert.False(call2.IsCompleted);

            releaseFirst.TrySetResult();

            var results = await Task.WhenAll(call1, call2);
            Assert.All(results, r => Assert.True(r.IsError));
            Assert.Equal(1, maxConcurrent);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunStdioAsync_HandlesVersionHelpAndUnknownArgs()
    {
        await using var outWriter = new StringWriter();
        await using var errWriter = new StringWriter();

        var versionExit = await ArchifyMcpServer.RunStdioAsync(["--version"], outWriter, errWriter);
        Assert.Equal(0, versionExit);
        Assert.Contains(ArchifyMcpServer.ServerVersion, outWriter.ToString(), StringComparison.Ordinal);

        outWriter.GetStringBuilder().Clear();
        var helpExit = await ArchifyMcpServer.RunStdioAsync(["--help"], outWriter, errWriter);
        Assert.Equal(0, helpExit);
        Assert.Contains("Usage: archify-mcp", outWriter.ToString(), StringComparison.Ordinal);

        var badExit = await ArchifyMcpServer.RunStdioAsync(["--bogus"], outWriter, errWriter);
        Assert.Equal(2, badExit);
        Assert.Contains("unknown argument", errWriter.ToString(), StringComparison.Ordinal);
    }
}
