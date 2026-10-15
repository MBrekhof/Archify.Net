using System.Text;
using Archify.Net.Mcp;

Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return await ArchifyMcpServer.RunStdioAsync(args, Console.Out, Console.Error, cts.Token).ConfigureAwait(false);
