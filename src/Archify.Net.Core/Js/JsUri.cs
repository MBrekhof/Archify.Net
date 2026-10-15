using System.Buffers;
using System.Globalization;
using System.Text;

namespace Archify.Net;

// ponytail: V8 parity — the URI built-ins and String.prototype.toLowerCase that repository-location.mjs relies on.
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

    // ponytail: V8 parity — toLowerCase is ICU's root-locale full lowercasing: simple mappings plus U+0130 → "i̇" (the
    // only unconditional SpecialCasing expansion) and Final_Sigma (Σ → ς at a word end). ToLowerInvariant has neither.
    // Ceiling: Cased and Case_Ignorable are approximated from general categories plus the Other_Lowercase/Other_Uppercase
    // ranges a letter can carry; a property table would make Final_Sigma exact for the rare remaining code points.
    public static string ToLowerCase(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c < 0x80) { sb.Append(char.ToLowerInvariant(c)); continue; }
            if (c == 'İ') { sb.Append("i̇"); continue; }
            if (c == 'Σ') { sb.Append(IsFinalSigma(s, i) ? 'ς' : 'σ'); continue; }
            if (Rune.TryGetRuneAt(s, i, out var r)) { sb.Append(Rune.ToLowerInvariant(r).ToString()); i += r.Utf16SequenceLength - 1; }
            else sb.Append(c);   // lone surrogate: unchanged
        }
        return sb.ToString();
    }

    // ponytail: V8 parity — toUpperCase (and toLocaleUpperCase('en-US'), identical for every code point): simple mappings plus
    // SpecialCasing's 102 unconditional expansions (ß → SS, ligatures, Greek with ypogegrammeni), the table printed by Node v24.
    // Ceiling: about 28 code points added in Unicode 16 (Garay and others) that .NET's simple case tables do not map yet.
    public static string ToUpperCase(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c < 0x80) { sb.Append(char.ToUpperInvariant(c)); continue; }
            if (UpperExpansions.TryGetValue(c, out var expansion)) { sb.Append(expansion); continue; }
            if (Rune.TryGetRuneAt(s, i, out var r)) { sb.Append(Rune.ToUpperInvariant(r).ToString()); i += r.Utf16SequenceLength - 1; }
            else sb.Append(c);   // lone surrogate: unchanged
        }
        return sb.ToString();
    }
    static readonly Dictionary<char, string> UpperExpansions = new()
    {
        ['ß'] = "SS", ['ŉ'] = "ʼN", ['ǰ'] = "J̌", ['ΐ'] = "Ϊ́", ['ΰ'] = "Ϋ́", ['և'] = "ԵՒ",
        ['ẖ'] = "H̱", ['ẗ'] = "T̈", ['ẘ'] = "W̊", ['ẙ'] = "Y̊", ['ẚ'] = "Aʾ", ['ὐ'] = "Υ̓",
        ['ὒ'] = "Υ̓̀", ['ὔ'] = "Υ̓́", ['ὖ'] = "Υ̓͂", ['ᾀ'] = "ἈΙ", ['ᾁ'] = "ἉΙ", ['ᾂ'] = "ἊΙ",
        ['ᾃ'] = "ἋΙ", ['ᾄ'] = "ἌΙ", ['ᾅ'] = "ἍΙ", ['ᾆ'] = "ἎΙ", ['ᾇ'] = "ἏΙ", ['ᾈ'] = "ἈΙ",
        ['ᾉ'] = "ἉΙ", ['ᾊ'] = "ἊΙ", ['ᾋ'] = "ἋΙ", ['ᾌ'] = "ἌΙ", ['ᾍ'] = "ἍΙ", ['ᾎ'] = "ἎΙ",
        ['ᾏ'] = "ἏΙ", ['ᾐ'] = "ἨΙ", ['ᾑ'] = "ἩΙ", ['ᾒ'] = "ἪΙ", ['ᾓ'] = "ἫΙ", ['ᾔ'] = "ἬΙ",
        ['ᾕ'] = "ἭΙ", ['ᾖ'] = "ἮΙ", ['ᾗ'] = "ἯΙ", ['ᾘ'] = "ἨΙ", ['ᾙ'] = "ἩΙ", ['ᾚ'] = "ἪΙ",
        ['ᾛ'] = "ἫΙ", ['ᾜ'] = "ἬΙ", ['ᾝ'] = "ἭΙ", ['ᾞ'] = "ἮΙ", ['ᾟ'] = "ἯΙ", ['ᾠ'] = "ὨΙ",
        ['ᾡ'] = "ὩΙ", ['ᾢ'] = "ὪΙ", ['ᾣ'] = "ὫΙ", ['ᾤ'] = "ὬΙ", ['ᾥ'] = "ὭΙ", ['ᾦ'] = "ὮΙ",
        ['ᾧ'] = "ὯΙ", ['ᾨ'] = "ὨΙ", ['ᾩ'] = "ὩΙ", ['ᾪ'] = "ὪΙ", ['ᾫ'] = "ὫΙ", ['ᾬ'] = "ὬΙ",
        ['ᾭ'] = "ὭΙ", ['ᾮ'] = "ὮΙ", ['ᾯ'] = "ὯΙ", ['ᾲ'] = "ᾺΙ", ['ᾳ'] = "ΑΙ", ['ᾴ'] = "ΆΙ",
        ['ᾶ'] = "Α͂", ['ᾷ'] = "Α͂Ι", ['ᾼ'] = "ΑΙ", ['ῂ'] = "ῊΙ", ['ῃ'] = "ΗΙ", ['ῄ'] = "ΉΙ",
        ['ῆ'] = "Η͂", ['ῇ'] = "Η͂Ι", ['ῌ'] = "ΗΙ", ['ῒ'] = "Ϊ̀", ['ΐ'] = "Ϊ́", ['ῖ'] = "Ι͂",
        ['ῗ'] = "Ϊ͂", ['ῢ'] = "Ϋ̀", ['ΰ'] = "Ϋ́", ['ῤ'] = "Ρ̓", ['ῦ'] = "Υ͂", ['ῧ'] = "Ϋ͂",
        ['ῲ'] = "ῺΙ", ['ῳ'] = "ΩΙ", ['ῴ'] = "ΏΙ", ['ῶ'] = "Ω͂", ['ῷ'] = "Ω͂Ι", ['ῼ'] = "ΩΙ",
        ['ﬀ'] = "FF", ['ﬁ'] = "FI", ['ﬂ'] = "FL", ['ﬃ'] = "FFI", ['ﬄ'] = "FFL", ['ﬅ'] = "ST",
        ['ﬆ'] = "ST", ['ﬓ'] = "ՄՆ", ['ﬔ'] = "ՄԵ", ['ﬕ'] = "ՄԻ", ['ﬖ'] = "ՎՆ", ['ﬗ'] = "ՄԽ",
    };
    static bool IsFinalSigma(string s, int at)
    {
        var before = false;
        for (var i = at; i > 0;)
        {
            var cp = char.IsLowSurrogate(s[i - 1]) && i >= 2 && char.IsHighSurrogate(s[i - 2]) ? char.ConvertToUtf32(s[i - 2], s[i - 1]) : s[i - 1];
            i -= cp > 0xFFFF ? 2 : 1;
            if (IsCaseIgnorable(cp)) continue;
            before = IsCased(cp);
            break;
        }
        if (!before) return false;
        for (var i = at + 1; i < s.Length;)
        {
            var (cp, len) = CodePointAt(s, i);
            i += len;
            if (IsCaseIgnorable(cp)) continue;
            return !IsCased(cp);
        }
        return true;
    }
    static bool IsCased(int cp) =>
        CharUnicodeInfo.GetUnicodeCategory(cp) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        || cp is 0xAA or 0xBA or >= 0x2160 and <= 0x217F or >= 0x24B6 and <= 0x24E9 or >= 0x1F130 and <= 0x1F149 or >= 0x1F150 and <= 0x1F169
            or >= 0x1F170 and <= 0x1F189;
    static bool IsCaseIgnorable(int cp) =>
        CharUnicodeInfo.GetUnicodeCategory(cp) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format
            or UnicodeCategory.ModifierLetter or UnicodeCategory.ModifierSymbol
        || cp == '\'' || In(MidLetterChars, cp) || In(MidNumLetChars, cp);
}
