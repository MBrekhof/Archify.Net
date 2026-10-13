using System.Reflection;
using System.Text;

namespace Archify.Net;

public static class Resources
{
    static readonly Assembly Asm = typeof(Resources).Assembly;
    static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static bool Exists(string path) => Asm.GetManifestResourceInfo(path) is not null;

    public static string Text(string path)
    {
        using var s = Asm.GetManifestResourceStream(path)
            ?? throw new FileNotFoundException($"Embedded resource {path} missing");
        using var r = new StreamReader(s, Utf8NoBom, detectEncodingFromByteOrderMarks: false);
        return r.ReadToEnd();
    }
}
