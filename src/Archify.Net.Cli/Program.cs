using System.Text;

namespace Archify.Net.Cli;

static class Program
{
    static int Main(string[] args)
    {
        var utf8 = new UTF8Encoding(false);
        Console.OutputEncoding = utf8;
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n", AutoFlush = true };
        using var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { NewLine = "\n", AutoFlush = true };
        try
        {
            return Cli.Run(args, new CliIo(stdout, stderr, Environment.CurrentDirectory, CliServices.Default));
        }
        catch (Exception e)
        {
            // A non-argument error escapes upstream's top-level catch (A:7132): Node prints the stack and exits 1.
            stderr.Write(e.ToString().ReplaceLineEndings("\n") + "\n");
            return 1;
        }
    }
}
