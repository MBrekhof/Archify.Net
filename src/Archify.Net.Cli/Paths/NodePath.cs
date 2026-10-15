using System.Text;

namespace Archify.Net.Cli.Paths;

// node:path where .NET's Path differs: path.resolve keeps no trailing separator, path.win32.normalize keeps a leading `..`
// and never touches the drive, path.extname ignores a dotfile's leading dot.
internal static class NodePath
{
    static readonly bool Windows = OperatingSystem.IsWindows();

    // path.resolve(cwd, p) on the host. ponytail: GetFullPath also trims a component's trailing dots and spaces (Win32 rules),
    // which path.resolve keeps; native output validation rejects such spellings before any caller resolves them.
    public static string Resolve(string cwd, string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, cwd));

    // path.resolve(p) for a path that is already absolute (upstream's error subjects).
    public static string Resolve(string path) => Resolve(Directory.GetCurrentDirectory(), path);

    // path.join(base, ...segments) for an absolute base.
    public static string Join(string basePath, IEnumerable<string> segments) =>
        Normalize(string.Join(Windows ? '\\' : '/', [basePath, .. segments]), Windows);

    static bool IsSep(char c, bool win32) => c == '/' || (win32 && c == '\\');

    // path.extname (win32 when `win32`, else posix).
    public static string Extname(string path, bool? win32 = null)
    {
        var w = win32 ?? Windows;
        var start = w && path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' ? 2 : 0;
        int startDot = -1, startPart = start, end = -1, preDotState = 0;
        var matchedSlash = true;
        for (var i = path.Length - 1; i >= start; --i)
        {
            var c = path[i];
            if (IsSep(c, w))
            {
                if (!matchedSlash) { startPart = i + 1; break; }
                continue;
            }
            if (end == -1) { matchedSlash = false; end = i + 1; }
            if (c == '.')
            {
                if (startDot == -1) startDot = i;
                else if (preDotState != 1) preDotState = 1;
            }
            else if (startDot != -1) preDotState = -1;
        }
        if (startDot == -1 || end == -1 || preDotState == 0 || (preDotState == 1 && startDot == end - 1 && startDot == startPart + 1)) return "";
        return path[startDot..end];
    }

    // path.win32.normalize (Node v24), or path.posix.normalize when !win32.
    public static string Normalize(string path, bool win32 = true)
    {
        if (path.Length == 0) return ".";
        if (!win32)
        {
            var absolute = path[0] == '/';
            var tail = NormalizeString(path, !absolute, '/', false);
            if (tail.Length == 0) tail = absolute ? "" : ".";
            if (tail.Length > 0 && path[^1] == '/') tail += "/";
            return absolute ? "/" + tail : tail;
        }
        if (path.Length == 1) return IsSep(path[0], true) ? "\\" : path;
        var rootEnd = 0;
        string? device = null;
        var isAbsolute = false;
        if (IsSep(path[0], true))
        {
            isAbsolute = true;
            if (IsSep(path[1], true))
            {
                int j = 2, last = j;
                while (j < path.Length && !IsSep(path[j], true)) j++;
                if (j < path.Length && j != last)
                {
                    var firstPart = path[last..j];
                    last = j;
                    while (j < path.Length && IsSep(path[j], true)) j++;
                    if (j < path.Length && j != last)
                    {
                        last = j;
                        while (j < path.Length && !IsSep(path[j], true)) j++;
                        // A UNC root only gains a trailing separator; a `\\.\x` or `\\?\x` device root does not.
                        if (j == path.Length) return $"\\\\{firstPart}\\{path[last..]}" + (firstPart is "." or "?" ? "" : "\\");
                        if (j != last) { device = $"\\\\{firstPart}\\{path[last..j]}"; rootEnd = j; }
                    }
                }
            }
            else rootEnd = 1;
        }
        else if (char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            device = path[..2];
            rootEnd = 2;
            if (path.Length > 2 && IsSep(path[2], true)) { isAbsolute = true; rootEnd = 3; }
        }
        var rest = rootEnd < path.Length ? NormalizeString(path[rootEnd..], !isAbsolute, '\\', true) : "";
        if (rest.Length == 0 && !isAbsolute) rest = ".";
        if (rest.Length > 0 && IsSep(path[^1], true)) rest += "\\";
        // ponytail: newer Node prefixes `.\` to a relative tail that would read as a drive (`a:b`); callers here split the
        // result into components and drop `.`, so the prefix never changes what they see.
        if (device is null) return isAbsolute ? "\\" + rest : rest;
        return isAbsolute ? $"{device}\\{rest}" : device + rest;
    }

    // Node's normalizeString: resolves `.` and `..`, collapses separators; `..` above the start is kept when allowed.
    static string NormalizeString(string path, bool allowAboveRoot, char separator, bool win32)
    {
        var res = new StringBuilder();
        int lastSegmentLength = 0, lastSlash = -1, dots = 0;
        for (var i = 0; i <= path.Length; ++i)
        {
            char code;
            if (i < path.Length) code = path[i];
            else if (IsSep(path[i - 1], win32)) break;
            else code = '/';
            if (IsSep(code, win32))
            {
                if (lastSlash == i - 1 || dots == 1) { }
                else if (dots == 2)
                {
                    if (res.Length < 2 || lastSegmentLength != 2 || res[^1] != '.' || res[^2] != '.')
                    {
                        if (res.Length > 2)
                        {
                            var s = res.ToString();
                            var lastSlashIndex = s.LastIndexOf(separator);
                            if (lastSlashIndex == -1) { res.Clear(); lastSegmentLength = 0; }
                            else
                            {
                                res.Length = lastSlashIndex;
                                lastSegmentLength = res.Length - 1 - res.ToString().LastIndexOf(separator);
                            }
                            lastSlash = i;
                            dots = 0;
                            continue;
                        }
                        if (res.Length != 0)
                        {
                            res.Clear();
                            lastSegmentLength = 0;
                            lastSlash = i;
                            dots = 0;
                            continue;
                        }
                    }
                    if (allowAboveRoot)
                    {
                        res.Append(res.Length > 0 ? $"{separator}.." : "..");
                        lastSegmentLength = 2;
                    }
                }
                else
                {
                    if (res.Length > 0) res.Append(separator).Append(path, lastSlash + 1, i - lastSlash - 1);
                    else res.Append(path, lastSlash + 1, i - lastSlash - 1);
                    lastSegmentLength = i - lastSlash - 1;
                }
                lastSlash = i;
                dots = 0;
            }
            else if (code == '.' && dots != -1) ++dots;
            else dots = -1;
        }
        return res.ToString();
    }
}
