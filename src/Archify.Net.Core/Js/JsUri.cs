using System.Buffers;
using System.Globalization;
using System.Text;

namespace Archify.Net;

// ponytail: V8 parity — the URI built-ins that repository-location.mjs relies on.
public static partial class Js
{
    const string HexDigits = "0123456789ABCDEF";
    static int HexValue(char c) => c is >= '0' and <= '9' ? c - '0' : (c | 0x20) is >= 'a' and <= 'f' ? (c | 0x20) - 'a' + 10 : -1;

    // ponytail: V8 parity — encodeURIComponent: keeps A-Za-z0-9 -_.!~*'() and %-encodes UTF-8 with uppercase hex
    // (Uri.EscapeDataString escapes !*'()). A lone surrogate throws, as JS throws URIError.
    public static string EncodeUriComponent(string s)
    {
        var sb = new StringBuilder(s.Length);
        Span<byte> utf8 = stackalloc byte[4];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsAsciiLetterOrDigit(c) || "-_.!~*'()".Contains(c)) { sb.Append(c); continue; }
            Rune r;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) r = new Rune(c, s[++i]);
            else if (char.IsSurrogate(c)) throw new ArgumentException("URI malformed", nameof(s));
            else r = new Rune(c);
            var n = r.EncodeToUtf8(utf8);
            foreach (var b in utf8[..n]) sb.Append('%').Append(HexDigits[b >> 4]).Append(HexDigits[b & 15]);
        }
        return sb.ToString();
    }

    // ponytail: V8 parity — decodeURIComponent (ES Decode, empty reserved set): null where JS throws URIError, i.e. on a `%` not
    // followed by two hex digits or on octets that are not one well-formed UTF-8 sequence (overlong, surrogate, > U+10FFFF).
    // Uri.UnescapeDataString never throws, so it cannot stand in.
    public static string? DecodeUriComponent(string s)
    {
        if (!s.Contains('%')) return s;
        var sb = new StringBuilder(s.Length);
        Span<byte> octets = stackalloc byte[4];
        for (var k = 0; k < s.Length; k++)
        {
            if (s[k] != '%') { sb.Append(s[k]); continue; }
            if (!Octet(s, k, out var b)) return null;
            k += 2;
            if (b < 0x80) { sb.Append((char)b); continue; }
            var n = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 0;
            if (n == 0 || b >= 0xF8) return null;
            octets[0] = (byte)b;
            for (var j = 1; j < n; j++)
            {
                if (k + 1 >= s.Length || s[k + 1] != '%' || !Octet(s, k + 1, out var c) || (c & 0xC0) != 0x80) return null;
                octets[j] = (byte)c;
                k += 3;
            }
            if (Rune.DecodeFromUtf8(octets[..n], out var rune, out var used) != OperationStatus.Done || used != n) return null;
            sb.Append(rune.ToString());
        }
        return sb.ToString();
    }
    static bool Octet(string s, int k, out int b)
    {
        b = 0;
        if (k + 2 >= s.Length) return false;
        int hi = HexValue(s[k + 1]), lo = HexValue(s[k + 2]);
        if (hi < 0 || lo < 0) return false;
        b = hi * 16 + lo;
        return true;
    }
}
