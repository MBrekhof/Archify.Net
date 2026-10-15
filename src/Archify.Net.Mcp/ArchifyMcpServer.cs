using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Archify.Net.Mcp;

public static class ArchifyMcpServer
{
    public const string ServerName = "archify-mcp";
    public const string ServerVersion = "1.0.0";

    public const string Instructions =
        "Archify MCP server for recommending, validating, rendering, and finalizing interactive HTML diagrams. " +
        "Use archify_guide to recommend a diagram type for a plain-language scenario, archify_validate to validate " +
        "or inspect layout geometry, archify_render to render standalone HTML, and archify_finalize to run the full " +
        "delivery, artifact check, and headless browser verification gates.";

    public static McpServerOptions CreateOptions(ArchifyTools tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var toolCollection = new McpServerPrimitiveCollection<McpServerTool>();
        var methods = typeof(ArchifyTools).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        foreach (var method in methods)
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            {
                toolCollection.Add(McpServerTool.Create(method, tools));
            }
        }

        return new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = ServerName,
                Version = ServerVersion,
            },
            ServerInstructions = Instructions,
            ToolCollection = toolCollection,
        };
    }

    public static McpServer CreateServer(ITransport transport, ArchifyTools tools)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(tools);
        var options = CreateOptions(tools);
        return McpServer.Create(transport, options);
    }

    public static async Task<int> RunStdioAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Length == 1 && (args[0] == "--version" || args[0] == "-v"))
        {
            await stdout.WriteLineAsync(ServerVersion).ConfigureAwait(false);
            return 0;
        }

        if (args.Length == 1 && (args[0] == "--help" || args[0] == "-h"))
        {
            await stdout.WriteLineAsync("Usage: archify-mcp [--version] [--help]").ConfigureAwait(false);
            await stdout.WriteLineAsync("Runs the Archify Model Context Protocol (MCP) server over stdio.").ConfigureAwait(false);
            return 0;
        }

        if (args.Length > 0)
        {
            await stderr.WriteLineAsync($"archify-mcp: unknown argument '{args[0]}'. Expected no arguments, --version, or --help.").ConfigureAwait(false);
            return 2;
        }

        using var tools = new ArchifyTools();
        var options = CreateOptions(tools);
        await using var transport = new StdioServerTransport(options);
        await using var server = McpServer.Create(transport, options);
        await server.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
