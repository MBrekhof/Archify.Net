using System.Text.Json;
using Archify.Net.Cli;
using Archify.Net.Cli.Browser;
using ModelContextProtocol.Client;
using Xunit;

namespace Archify.Net.Mcp.Tests;

public sealed class McpPipelineToolTests
{
    // Mirrors FinalizeCommandTests.NoChromium (tests/Archify.Net.Cli.Tests/FinalizeCommandTests.cs:45-55).
    private sealed class NoChromium : IBrowserInspectorFactory
    {
        public bool Resolved { get; private set; }

        public string? ResolveExecutable()
        {
            Resolved = true;
            return null;
        }

        public Task<IBrowserInspector> LaunchAsync(string executable) =>
            throw new InvalidOperationException("no browser is installed");
    }

    private static void RequireChromium() =>
        Assert.True(
            CliServices.Default.HeadlessShell() is not null,
            "Playwright Chromium (headless shell 1243) is not installed: run `archify install-browser` (PowerShell or cmd, no admin), then rerun the Browser tests.");

    [Fact]
    public async Task RenderTool_WritesHtmlAndReturnsVerbatimOutputPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "archify-mcp-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sourceSpec = Path.Combine(McpTestHost.RepoRoot, "upstream", "examples", "web-app.architecture.json");
            File.Copy(sourceSpec, Path.Combine(tempDir, "web-app.architecture.json"));

            await using var host = await McpTestHost.StartAsync(tempDir);
            var renderResult = await host.Client.CallToolAsync("archify_render", new Dictionary<string, object?>
            {
                ["type"] = "architecture",
                ["input"] = "web-app.architecture.json",
                ["output"] = "rendered.html",
            });

            Assert.NotEqual(true, renderResult.IsError);
            var expectedOutputPath = Path.Combine(tempDir, "rendered.html");
            Assert.Equal(expectedOutputPath, McpTestHost.ExtractSingleText(renderResult));
            Assert.True(File.Exists(expectedOutputPath));

            var goldenBytes = await File.ReadAllBytesAsync(Path.Combine(McpTestHost.RepoRoot, "upstream", "examples", "web-app-rendered.html"));
            var actualBytes = await File.ReadAllBytesAsync(expectedOutputPath);
            Assert.Equal(goldenBytes, actualBytes);
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
    public async Task FinalizeTool_WithoutChromium_MapsExit2SkippedToIsErrorTrue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "archify-mcp-finalize-nochrome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sourceSpec = Path.Combine(McpTestHost.RepoRoot, "upstream", "examples", "web-app.architecture.json");
            File.Copy(sourceSpec, Path.Combine(tempDir, "web-app.architecture.json"));

            var browsers = new NoChromium();
            await using var host = await McpTestHost.StartAsync(tempDir, CliServices.Default with { BrowserInspectors = browsers });

            var result = await host.Client.CallToolAsync("archify_finalize", new Dictionary<string, object?>
            {
                ["type"] = "architecture",
                ["input"] = "web-app.architecture.json",
                ["output"] = "out.html",
            });

            Assert.True(result.IsError);
            Assert.True(browsers.Resolved);

            using var doc = JsonDocument.Parse(McpTestHost.ExtractSingleText(result));
            var root = doc.RootElement;
            Assert.False(root.GetProperty("ok").GetBoolean());
            Assert.Equal("finalize", root.GetProperty("command").GetString());
            Assert.Equal("skipped", root.GetProperty("status").GetString());
            Assert.Equal("browser-check", root.GetProperty("failedStage").GetString());

            var gates = root.GetProperty("gates");
            Assert.Equal("pass", gates.GetProperty("validate").GetString());
            Assert.Equal("pass", gates.GetProperty("deliver").GetString());
            Assert.Equal("pass", gates.GetProperty("check").GetString());
            Assert.Equal("skipped", gates.GetProperty("browser-check").GetString());
            Assert.Equal("viewer/chrome-unavailable", root.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Trait("Category", "Browser")]
    [Fact]
    public async Task FinalizeTool_OnChromium_PassesAllGates()
    {
        RequireChromium();

        var tempDir = Path.Combine(Path.GetTempPath(), "archify-mcp-finalize-chrome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sourceSpec = Path.Combine(McpTestHost.RepoRoot, "upstream", "examples", "web-app.architecture.json");
            File.Copy(sourceSpec, Path.Combine(tempDir, "web-app.architecture.json"));

            await using var host = await McpTestHost.StartAsync(tempDir, CliServices.Default);

            var result = await host.Client.CallToolAsync("archify_finalize", new Dictionary<string, object?>
            {
                ["type"] = "architecture",
                ["input"] = "web-app.architecture.json",
                ["output"] = "out.html",
                ["quality"] = "showcase",
            });

            Assert.NotEqual(true, result.IsError);
            using var doc = JsonDocument.Parse(McpTestHost.ExtractSingleText(result));
            var root = doc.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Equal("finalize", root.GetProperty("command").GetString());
            Assert.Equal("pass", root.GetProperty("status").GetString());
            Assert.Equal("not-requested", root.GetProperty("visualReview").GetString());

            var gates = root.GetProperty("gates");
            Assert.Equal("pass", gates.GetProperty("validate").GetString());
            Assert.Equal("pass", gates.GetProperty("deliver").GetString());
            Assert.Equal("pass", gates.GetProperty("check").GetString());
            Assert.Equal("pass", gates.GetProperty("browser-check").GetString());

            Assert.True(File.Exists(Path.Combine(tempDir, "out.html")));
            Assert.True(File.Exists(Path.Combine(tempDir, "out.delivery.json")));
            Assert.True(File.Exists(Path.Combine(tempDir, "out.finalize.json")));
            Assert.True(File.Exists(Path.Combine(tempDir, "out.finalize-summary.json")));
            Assert.True(File.Exists(Path.Combine(tempDir, "out.browser-check.json")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static string NewDirWithDraft(string prefix, string draft = "first-rental")
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(McpTestHost.RepoRoot, "showcase", "explainer", draft + ".archify.md"), Path.Combine(dir, "answer.archify.md"));
        return dir;
    }

    [Fact]
    public async Task ExplainerTools_RenderValidateFinalizeAndPatch()
    {
        var tempDir = NewDirWithDraft("archify-mcp-explainer-");
        try
        {
            await using var host = await McpTestHost.StartAsync(tempDir);

            var validate = await host.Client.CallToolAsync("archify_validate", new Dictionary<string, object?>
            {
                ["type"] = "explainer",
                ["input"] = "answer.archify.md",
                ["quality"] = "standard",
                ["writing"] = "strict",
            });
            Assert.NotEqual(true, validate.IsError);
            using (var doc = JsonDocument.Parse(McpTestHost.ExtractSingleText(validate)))
            {
                Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal("explainer", doc.RootElement.GetProperty("type").GetString());
            }

            var render = await host.Client.CallToolAsync("archify_render", new Dictionary<string, object?>
            {
                ["type"] = "explainer",
                ["input"] = "answer.archify.md",
                ["output"] = "rendered.html",
            });
            Assert.NotEqual(true, render.IsError);
            Assert.True(File.Exists(Path.Combine(tempDir, "rendered.html")));

            // An explainer runs its browser check only on request: without browserCheck no Chromium is needed.
            var finalize = await host.Client.CallToolAsync("archify_finalize", new Dictionary<string, object?>
            {
                ["type"] = "explainer",
                ["input"] = "answer.archify.md",
                ["output"] = "answer.html",
                ["quality"] = "standard",
                ["writing"] = "warn",
            });
            Assert.NotEqual(true, finalize.IsError);
            using (var doc = JsonDocument.Parse(McpTestHost.ExtractSingleText(finalize)))
            {
                var root = doc.RootElement;
                Assert.True(root.GetProperty("ok").GetBoolean());
                Assert.Equal("explainer", root.GetProperty("type").GetString());
                Assert.Equal("pass", root.GetProperty("gates").GetProperty("deliver").GetString());
                Assert.Equal("not-run", root.GetProperty("gates").GetProperty("browser-check").GetString());
                Assert.False(root.GetProperty("browserCheck").GetProperty("requested").GetBoolean());
            }

            await File.WriteAllTextAsync(Path.Combine(tempDir, "replacement.md"), "## 合同里必须写清哪些条款？ {#clauses}\n\n只看三条：租金、押金、租期。\n");
            var patch = await host.Client.CallToolAsync("archify_patch", new Dictionary<string, object?>
            {
                ["artifact"] = "answer.html",
                ["panelId"] = "clauses",
                ["replacement"] = "replacement.md",
                ["output"] = "patched.html",
                ["quality"] = "standard",
                ["repoRoot"] = " ",
                ["writing"] = "",
                ["cwd"] = "",
            });
            Assert.NotEqual(true, patch.IsError);
            using (var doc = JsonDocument.Parse(McpTestHost.ExtractSingleText(patch)))
            {
                Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal("patch", doc.RootElement.GetProperty("command").GetString());
            }

            Assert.Contains("只看三条", await File.ReadAllTextAsync(Path.Combine(tempDir, "patched.html")), StringComparison.Ordinal);

            // A panel id the page does not carry is a refusal, not an exception.
            var missing = await host.Client.CallToolAsync("archify_patch", new Dictionary<string, object?>
            {
                ["artifact"] = "answer.html",
                ["panelId"] = "nope",
                ["replacement"] = "replacement.md",
                ["output"] = "patched-2.html",
            });
            Assert.True(missing.IsError);
            using var missingDoc = JsonDocument.Parse(McpTestHost.ExtractSingleText(missing));
            Assert.False(missingDoc.RootElement.GetProperty("ok").GetBoolean());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // The tool descriptions say `writing` goes with type 'explainer' only: the CLI refuses it for the other types.
    [Fact]
    public async Task WritingParameter_IsRefusedForNativeTypes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "archify-mcp-writing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            File.Copy(Path.Combine(McpTestHost.RepoRoot, "upstream", "examples", "web-app.architecture.json"), Path.Combine(tempDir, "web-app.architecture.json"));
            await using var host = await McpTestHost.StartAsync(tempDir);
            var result = await host.Client.CallToolAsync("archify_validate", new Dictionary<string, object?>
            {
                ["type"] = "architecture",
                ["input"] = "web-app.architecture.json",
                ["writing"] = "strict",
            });
            Assert.True(result.IsError);
            using var doc = JsonDocument.Parse(McpTestHost.ExtractSingleText(result));
            Assert.Equal("cli/unsupported-option", doc.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Trait("Category", "Browser")]
    [Fact]
    public async Task FinalizeTool_ExplainerWithBrowserCheck_RunsTheBrowserGate()
    {
        RequireChromium();

        // tls-first-visit passes the browser gate; first-rental fails explainer/browser-readability on its workflow text.
        var tempDir = NewDirWithDraft("archify-mcp-explainer-chrome-", "tls-first-visit");
        try
        {
            await using var host = await McpTestHost.StartAsync(tempDir);
            var result = await host.Client.CallToolAsync("archify_finalize", new Dictionary<string, object?>
            {
                ["type"] = "explainer",
                ["input"] = "answer.archify.md",
                ["output"] = "answer.html",
                ["quality"] = "standard",
                ["browserCheck"] = true,
            });

            Assert.NotEqual(true, result.IsError);
            using var doc = JsonDocument.Parse(McpTestHost.ExtractSingleText(result));
            var root = doc.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Equal("pass", root.GetProperty("gates").GetProperty("browser-check").GetString());
            Assert.True(root.GetProperty("browserCheck").GetProperty("requested").GetBoolean());
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
    public async Task StdioSubprocess_EndToEndSmokeTest()
    {
        var mcpDllPath = typeof(ArchifyMcpServer).Assembly.Location;
        Assert.True(File.Exists(mcpDllPath), $"Expected built MCP assembly at {mcpDllPath}");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = ["exec", mcpDllPath],
            Name = "archify-mcp-stdio-test",
        });

        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();
        Assert.Equal(5, tools.Count);

        var guide = await client.CallToolAsync("archify_guide", new Dictionary<string, object?>
        {
            ["query"] = "Show an API request with Redis cache miss",
        });
        Assert.NotEqual(true, guide.IsError);
        using var doc = JsonDocument.Parse(McpTestHost.ExtractSingleText(guide));
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("sequence", doc.RootElement.GetProperty("recommendation").GetProperty("type").GetString());
    }

    [Trait("Category", "Browser")]
    [Fact]
    public async Task StdioSubprocess_FinalizeOnChromium_KeepsProtocolClean()
    {
        RequireChromium();

        var mcpDllPath = typeof(ArchifyMcpServer).Assembly.Location;
        Assert.True(File.Exists(mcpDllPath), $"Expected built MCP assembly at {mcpDllPath}");

        var tempDir = Path.Combine(Path.GetTempPath(), "archify-mcp-stdio-finalize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sourceSpec = Path.Combine(McpTestHost.RepoRoot, "upstream", "examples", "web-app.architecture.json");
            File.Copy(sourceSpec, Path.Combine(tempDir, "web-app.architecture.json"));

            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = "dotnet",
                Arguments = ["exec", mcpDllPath],
                Name = "archify-mcp-stdio-finalize-test",
            });

            await using var client = await McpClient.CreateAsync(transport);

            var finalizeResult = await client.CallToolAsync("archify_finalize", new Dictionary<string, object?>
            {
                ["type"] = "architecture",
                ["input"] = "web-app.architecture.json",
                ["output"] = "out.html",
                ["cwd"] = tempDir,
            });

            Assert.NotEqual(true, finalizeResult.IsError);
            using var doc = JsonDocument.Parse(McpTestHost.ExtractSingleText(finalizeResult));
            var root = doc.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Equal("finalize", root.GetProperty("command").GetString());
            Assert.Equal("pass", root.GetProperty("status").GetString());
            Assert.True(File.Exists(Path.Combine(tempDir, "out.html")));

            var toolsAfter = await client.ListToolsAsync();
            Assert.Equal(5, toolsAfter.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
