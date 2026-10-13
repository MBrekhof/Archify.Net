namespace Archify.Net.Tests;

public static class Fixtures
{
    // tests/Archify.Net.Core.Tests/bin/<cfg>/net10.0 → repo root is 5 levels up
    public static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    public static string Upstream(string rel) => Path.Combine(RepoRoot, "upstream", rel.Replace('/', Path.DirectorySeparatorChar));
    public static string Golden(string name) => Path.Combine(RepoRoot, "tests", "goldens", name);
    public static string Fixture(string rel) => Path.Combine(RepoRoot, "tests", "fixtures", rel.Replace('/', Path.DirectorySeparatorChar));
    public static byte[] Bytes(string path) => File.ReadAllBytes(path);
    public static string Lf(string path) => File.ReadAllText(path, new System.Text.UTF8Encoding(false));
}
