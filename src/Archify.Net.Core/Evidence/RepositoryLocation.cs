using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Archify.Net;

/// <summary>
/// <c>parseRepositoryRemote</c>'s result. <see cref="PathKind"/> is <c>identity[2]</c>, carried as its own field.
/// </summary>
internal sealed record ParsedRemote(string Identity, string Url, string? Provider, string Protocol, string Path, string Endpoint, string PathKind);

/// <summary>
/// Port of renderers/shared/repository-location.mjs: repository identity and forge links, pure (no I/O, no remote contact).
/// </summary>
/// <remarks>
/// <c>new URL</c> is replaced by <see cref="Authority"/>, a WHATWG authority parser covering only what reaches it here (the input
/// already matched <c>^(https?|ssh)://[^/]+/.+$</c> and holds no whitespace, control character, <c>\</c>, <c>?</c> or <c>#</c>).
/// System.Uri differs on IPv4 shorthand, IDNA, default ports and the ssh opaque host.
/// </remarks>
internal static class RepositoryLocation
{
    const RegexOptions Es = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
    // JS `/i` on ASCII letters is spelled as classes: .NET's case-insensitive tables also fold U+017F and U+212A.
    static readonly Regex Scp = new(@"^git@([^/:]+):([^\n\r  ]+)\z", Es);
    static readonly Regex Remote = new(@"^([hH][tT][tT][pP][sS]?|[sS][sS][hH]):\/\/([^/]+)\/([^\n\r  ]+)\z", Es);
    static readonly Regex Userinfo = new(@"^((?:[hH][tT][tT][pP][sS]?|[sS][sS][hH]):\/\/)[^/]*@", Es);
    static readonly Regex Markdown = new(@"\.(?:[mM][dD]|[mM][aA][rR][kK][dD][oO][wW][nN])\z", Es);

    static string? ProviderHost(string host) => host switch { "github.com" => "github", "gitee.com" => "gitee", "gitlab.com" => "gitlab", _ => null };
    // /[\s\\\u0000-\u001f\u007f?#]/ (:10); the segment test (:24) adds `/`.
    static bool Bad(char c) => Js.IsSpace(c) || c is '\\' or < ' ' or '\u007f' or '?' or '#';

    public static ParsedRemote? Parse(string? value, bool authored, string? provider)
    {
        if (value is null) return null;
        var raw = authored ? value : Js.Trim(value);
        if (raw.Length == 0 || raw.Any(Bad)) return null;
        var scp = Scp.Match(raw);
        var scpAbsolute = scp.Success && scp.Groups[2].Value.StartsWith('/');
        var expanded = scp.Success ? $"ssh://git@{scp.Groups[1].Value}/{(scpAbsolute ? scp.Groups[2].Value[1..] : scp.Groups[2].Value)}" : raw;
        var match = Remote.Match(expanded);
        if (!match.Success) return null;
        var rest = match.Groups[3].Value;
        var segments = (rest.EndsWith('/') ? rest[..^1] : rest).Split('/');
        if (!scp.Success)   // Git passes SCP paths literally; percent escapes are decoded only in URIs (:20)
            for (var i = 0; i < segments.Length; i++) { if (Js.DecodeUriComponent(segments[i]) is not { } d) return null; segments[i] = d; }
        if (segments.Any(p => p.Length == 0 || p is "." or ".." || p.Any(c => c == '/' || Bad(c)))) return null;
        if (Authority(match.Groups[1].Value, match.Groups[2].Value) is not { } url) return null;
        var protocol = url.Protocol;
        if (protocol == "ssh:" && (url.Username != "git" || url.Password.Length > 0)) return null;
        if (authored && protocol != "ssh:" && (url.Username.Length > 0 || url.Password.Length > 0)) return null;
        var hostname = Js.ToLowerCase(url.Hostname);
        if (hostname.Length == 0) return null;
        var forge = ProviderHost(hostname) ?? (provider == "gitlab" ? "gitlab" : null);
        var last = segments.Length - 1;
        if (forge is not null && EndsWithDotGit(segments[last], forge == "github")) segments[last] = segments[last][..^4];
        if (segments[last] is "" or "." or "..") return null;
        var repositoryPath = string.Join('/', segments);
        var port = url.Port.Length > 0 ? url.Port : protocol == "ssh:" ? "22" : protocol == "https:" ? "443" : "80";
        var endpoint = forge is not null && ((protocol == "https:" && port == "443") || (protocol == "ssh:" && port == "22")) ? "standard" : protocol + port;
        var pathKind = forge is not null ? "repository" : scp.Success && !scpAbsolute ? "relative" : "absolute";
        var identityPath = forge is "github" or "gitlab" ? Js.ToLowerCase(repositoryPath) : repositoryPath;
        var canonicalUrl = scp.Success ? $"git@{hostname}:{(scpAbsolute ? "/" : "")}{repositoryPath}"
            : $"{protocol}//{(protocol == "ssh:" ? "git@" : "")}{url.Host}/{string.Join('/', segments.Select(Js.EncodeUriComponent))}";
        var identity = "[" + string.Join(',', new[] { hostname, endpoint, pathKind, identityPath }.Select(Js.Quote)) + "]";
        return new ParsedRemote(identity, canonicalUrl, forge, protocol, repositoryPath, endpoint, pathKind);
    }
    // /\.git$/i for github, /\.git$/ otherwise (:34).
    static bool EndsWithDotGit(string s, bool ignoreCase) =>
        s.Length >= 4 && s[^4] == '.' && (ignoreCase ? (s[^3] | 0x20) == 'g' && (s[^2] | 0x20) == 'i' && (s[^1] | 0x20) == 't' : s.EndsWith(".git", StringComparison.Ordinal));

    public static string Redact(string? value)
    {
        var s = Userinfo.Replace(value ?? "", "$1REDACTED@", 1);   // String(value || '')
        var q = s.IndexOfAny(['?', '#']);   // /[?#].*$/s
        return q < 0 ? s : s[..q] + "?REDACTED";
    }

    // `line`/`endLine` are tested for JS truthiness (:59, :61, :65), so 0 counts as absent.
    public static string SourceHref(string? provider, string url, string revision, string path, int? line, int? endLine)
    {
        var encodedPath = string.Join('/', path.Split('/').Select(Js.EncodeUriComponent));
        var end = endLine is { } e && e != 0 && e != line ? $"-{(provider == "github" ? "L" : "")}{e}" : "";
        var fragment = line is { } l && l != 0 ? $"#L{l}{end}" : "";
        if (provider != "gitlab") return $"{url}/blob/{revision}/{encodedPath}{fragment}";
        var plain = line is not (null or 0) && Markdown.IsMatch(path) ? "?plain=1" : "";
        return $"{url}/-/blob/{revision}/{encodedPath}{plain}{fragment}";
    }

    public static string TreeHref(string? provider, string url, string revision) => $"{url}/{(provider == "gitlab" ? "-/" : "")}tree/{revision}";

    // ---- WHATWG URL, the authority part only -------------------------------------------------------------------------------------

    sealed record Url(string Protocol, string Username, string Password, string Hostname, string Port)
    {
        public string Host => Port.Length > 0 ? Hostname + ":" + Port : Hostname;
    }

    // Authority state, host state and port state for `<scheme>://<authority>/`. Username and password are kept raw: the caller only
    // compares the username with "git" and tests both for emptiness, and percent-encoding preserves both answers.
    static Url? Authority(string scheme, string authority)
    {
        var protocol = scheme.ToLowerInvariant() + ":";   // the regex admits only ASCII letters here
        var special = protocol != "ssh:";
        string username = "", password = "", hostPort = authority;
        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            var userinfo = authority[..at];
            hostPort = authority[(at + 1)..];
            if (hostPort.Length == 0) return null;
            var colon = userinfo.IndexOf(':');
            (username, password) = colon < 0 ? (userinfo, "") : (userinfo[..colon], userinfo[(colon + 1)..]);
        }
        var split = -1;
        for (int i = 0, brackets = 0; i < hostPort.Length && split < 0; i++)
            if (hostPort[i] == '[') brackets = 1;
            else if (hostPort[i] == ']') brackets = 0;
            else if (hostPort[i] == ':' && brackets == 0) split = i;
        var hostText = split < 0 ? hostPort : hostPort[..split];
        if (hostText.Length == 0 && (split >= 0 || special)) return null;
        if (ParseHost(hostText, special) is not { } host) return null;
        var port = "";
        if (split >= 0)
        {
            var digits = hostPort[(split + 1)..];
            if (!digits.All(char.IsAsciiDigit)) return null;
            if (digits.Length > 0)
            {
                var trimmed = digits.TrimStart('0');
                if (trimmed.Length > 5) return null;
                var n = trimmed.Length > 0 ? int.Parse(trimmed, CultureInfo.InvariantCulture) : 0;
                if (n > 65535) return null;
                port = (protocol, n) is ("https:", 443) or ("http:", 80) ? "" : n.ToString(CultureInfo.InvariantCulture);
            }
        }
        return new Url(protocol, username, password, host, port);
    }

    const string ForbiddenHost = "\0\t\n\r #/:<>?@[\\]^|";

    static string? ParseHost(string input, bool special)
    {
        if (input.StartsWith('[')) return input.EndsWith(']') && Ipv6(input[1..^1]) is { } v6 ? "[" + v6 + "]" : null;
        if (!special)
        {   // opaque host: refuse forbidden host code points, then UTF-8 percent-encode the C0 control set (C0 and > U+007E)
            if (input.Any(c => ForbiddenHost.Contains(c))) return null;
            var sb = new StringBuilder();
            foreach (var b in Encoding.UTF8.GetBytes(input))
                if (b is < 0x20 or > 0x7E) sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture)); else sb.Append((char)b);
            return sb.ToString();
        }
        var domain = PercentDecode(input);
        var ascii = DomainToAscii(domain);
        if (ascii is null || ascii.Length == 0 || ascii.Any(c => ForbiddenHost.Contains(c) || c is < ' ' or '%' or '\u007f')) return null;
        return EndsInNumber(ascii) ? Ipv4(ascii) : ascii;
    }

    static string PercentDecode(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var outBytes = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            int hi, lo;
            if (bytes[i] == '%' && i + 2 < bytes.Length && (hi = HexValue(bytes[i + 1])) >= 0 && (lo = HexValue(bytes[i + 2])) >= 0) { outBytes.Add((byte)(hi * 16 + lo)); i += 2; }
            else outBytes.Add(bytes[i]);
        }
        return Encoding.UTF8.GetString(outBytes.ToArray());   // UTF-8 decode without BOM, U+FFFD on invalid sequences
    }
    static int HexValue(byte c) => c is >= (byte)'0' and <= (byte)'9' ? c - '0' : (c | 0x20) is >= 'a' and <= 'f' ? (c | 0x20) - 'a' + 10 : -1;

    // UTS #46 ToASCII as WHATWG calls it (beStrict false). An ASCII domain with no `xn--` label is just ASCII-lowercased (the spec's
    // own shortcut); anything else goes through IdnMapping (UTS #46, nontransitional).
    static readonly IdnMapping Idn = new();
    static string? DomainToAscii(string domain)
    {
        if (domain.All(char.IsAscii) && !domain.Split('.').Any(l => l.StartsWith("xn--", StringComparison.OrdinalIgnoreCase)))
            return domain.ToLowerInvariant();
        try { return Idn.GetAscii(domain); } catch (ArgumentException) { return null; }
    }

    static bool EndsInNumber(string input)
    {
        var parts = input.Split('.').ToList();
        if (parts[^1].Length == 0) { if (parts.Count == 1) return false; parts.RemoveAt(parts.Count - 1); }
        var last = parts[^1];
        return (last.Length > 0 && last.All(char.IsAsciiDigit)) || Ipv4Number(last) is not null;
    }
    static ulong? Ipv4Number(string s)
    {
        if (s.Length == 0) return null;
        var radix = 10;
        if (s.Length >= 2 && s[0] == '0' && s[1] is 'x' or 'X') { s = s[2..]; radix = 16; }
        else if (s.Length >= 2 && s[0] == '0') { s = s[1..]; radix = 8; }
        ulong v = 0;
        foreach (var c in s)
        {
            var d = HexValue(c < 0x80 ? (byte)c : (byte)0xFF);
            if (d < 0 || d >= radix) return null;
            v = v > uint.MaxValue ? v : v * (ulong)radix + (ulong)d;   // saturates past 2^32: every such value fails the range checks
        }
        return v;
    }
    static string? Ipv4(string input)
    {
        var parts = input.Split('.').ToList();
        if (parts[^1].Length == 0 && parts.Count > 1) parts.RemoveAt(parts.Count - 1);
        if (parts.Count > 4) return null;
        var numbers = new List<ulong>();
        foreach (var p in parts) { if (Ipv4Number(p) is not { } n) return null; numbers.Add(n); }
        for (var i = 0; i < numbers.Count - 1; i++) if (numbers[i] > 255) return null;
        if (numbers[^1] >= 1UL << (8 * (5 - numbers.Count))) return null;
        var ipv4 = numbers[^1];
        for (var i = 0; i < numbers.Count - 1; i++) ipv4 += numbers[i] << (8 * (3 - i));
        return $"{ipv4 >> 24}.{(ipv4 >> 16) & 255}.{(ipv4 >> 8) & 255}.{ipv4 & 255}";
    }

    // ponytail: IPAddress.TryParse stands in for the WHATWG IPv6 parser (scope ids refused, as WHATWG has none); the serializer is
    // WHATWG's (lowercase hex, first longest run of two or more zero pieces compressed, embedded IPv4 printed as hex).
    static string? Ipv6(string inner)
    {
        if (inner.Contains('%') || !IPAddress.TryParse(inner, out var a) || a.AddressFamily != AddressFamily.InterNetworkV6) return null;
        var b = a.GetAddressBytes();
        var pieces = Enumerable.Range(0, 8).Select(i => b[2 * i] << 8 | b[2 * i + 1]).ToArray();
        int compress = -1, best = 1;
        for (var i = 0; i < 8;)
        {
            var j = i;
            while (j < 8 && pieces[j] == 0) j++;
            if (j - i > best) { best = j - i; compress = i; }
            i = j == i ? i + 1 : j;
        }
        var sb = new StringBuilder();
        for (var i = 0; i < 8; i++)
        {
            if (i == compress) { sb.Append(i == 0 ? "::" : ":"); i += best - 1; continue; }
            sb.Append(pieces[i].ToString("x", CultureInfo.InvariantCulture));
            if (i < 7) sb.Append(':');
        }
        return sb.ToString();
    }
}
