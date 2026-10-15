using System.IO.Pipelines;
using Archify.Net.Cli;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Archify.Net.Mcp.Tests;

internal sealed class McpTestHost : IAsyncDisposable
{
    // tests/Archify.Net.Mcp.Tests/bin/<cfg>/net10.0 -> repo root is 5 levels up
    public static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private readonly Pipe _clientToServer;
    private readonly Pipe _serverToClient;
    private readonly CancellationTokenSource _cts;
    private readonly ArchifyTools _tools;
    private readonly StreamServerTransport _serverTransport;
    private readonly McpServer _server;
    private readonly Task _serverRunTask;

    private McpTestHost(
        ArchifyTools tools,
        StreamServerTransport serverTransport,
        McpServer server,
        Task serverRunTask,
        McpClient client,
        Pipe clientToServer,
        Pipe serverToClient,
        CancellationTokenSource cts)
    {
        _tools = tools;
        _serverTransport = serverTransport;
        _server = server;
        _serverRunTask = serverRunTask;
        Client = client;
        _clientToServer = clientToServer;
        _serverToClient = serverToClient;
        _cts = cts;
    }

    public McpClient Client { get; }

    public ArchifyTools Tools => _tools;

    public static async Task<McpTestHost> StartAsync(string? defaultCwd = null, CliServices? services = null, CancellationToken cancellationToken = default)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var tools = new ArchifyTools(defaultCwd, services);
        var serverTransport = new StreamServerTransport(
            inputStream: clientToServer.Reader.AsStream(),
            outputStream: serverToClient.Writer.AsStream(),
            serverName: ArchifyMcpServer.ServerName);
        var server = ArchifyMcpServer.CreateServer(serverTransport, tools);
        var serverRunTask = server.RunAsync(cts.Token);

        var clientTransport = new StreamClientTransport(
            serverInput: clientToServer.Writer.AsStream(),
            serverOutput: serverToClient.Reader.AsStream());
        var client = await McpClient.CreateAsync(clientTransport, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new McpTestHost(tools, serverTransport, server, serverRunTask, client, clientToServer, serverToClient, cts);
    }

    public static string ExtractSingleText(CallToolResult result)
    {
        Assert.NotNull(result.Content);
        var block = Assert.Single(result.Content);
        var textBlock = Assert.IsType<TextContentBlock>(block);
        Assert.NotNull(textBlock.Text);
        return textBlock.Text;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await Client.DisposeAsync().ConfigureAwait(false);
        await _clientToServer.Writer.CompleteAsync().ConfigureAwait(false);
        await _serverToClient.Writer.CompleteAsync().ConfigureAwait(false);
        try
        {
            await _serverRunTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        await _server.DisposeAsync().ConfigureAwait(false);
        await _serverTransport.DisposeAsync().ConfigureAwait(false);
        _tools.Dispose();
        _cts.Dispose();
    }
}
