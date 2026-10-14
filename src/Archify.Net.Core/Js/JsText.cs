using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Archify.Net;

// ponytail: V8 parity — Intl.Segmenter stand-ins, \p{Script=Han} tokenizers and the JS-whitespace splits the renderers use.
public static partial class Js
{
    // ponytail: V8 parity — ES WhiteSpace + LineTerminator (= IsSpace) as a regex class; replaces `\s` in every ported pattern
    // (.NET `\s` adds U+0085 and drops U+FEFF).
    public const string WsClass = "[\t\n\v\f\r \u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]";
    const RegexOptions Es = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;

    // ponytail: V8 parity — Intl.Segmenter grapheme granularity; StringInfo is UAX #29 extended clusters since .NET 5.
    public static List<string> Graphemes(string s)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add(e.GetTextElement());
        return list;
    }
    // ponytail: per-grapheme fallback, parity-exempt per spec; P/Invoke ubrk_* from icu.dll if a CJK user appears.
    // Only upstream input is a Han+Mark run (render-sequence.mjs:203-205).
    public static List<string> WordBreaks(string s) => Graphemes(s);

    static readonly int[] HanRanges =
    [
        0x2E80, 0x2E99, 0x2E9B, 0x2EF3, 0x2F00, 0x2FD5, 0x3005, 0x3005, 0x3007, 0x3007, 0x3021, 0x3029, 0x3038, 0x303B,
        0x3400, 0x4DBF, 0x4E00, 0x9FFF, 0xF900, 0xFA6D, 0xFA70, 0xFAD9, 0x16FE2, 0x16FE3, 0x16FF0, 0x16FF1,
        0x20000, 0x2A6DF, 0x2A700, 0x2B739, 0x2B740, 0x2B81D, 0x2B820, 0x2CEA1, 0x2CEB0, 0x2EBE0, 0x2EBF0, 0x2EE5D,
        0x2F800, 0x2FA1D, 0x30000, 0x3134A, 0x31350, 0x323AF,
    ];
    // ponytail: V8 parity — \p{Script=Han} by range table (equals V8's set by full enumeration); .NET Regex has no \p{Script=…}.
    public static bool IsHan(int cp)
    {
        for (var i = 0; i < HanRanges.Length; i += 2) if (cp >= HanRanges[i] && cp <= HanRanges[i + 1]) return true;
        return false;
    }
    // ponytail: V8 parity — \p{Mark} = Mn | Mc | Me.
    public static bool IsMark(int cp) => Rune.IsValid(cp) && Rune.GetUnicodeCategory(new Rune(cp))
        is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    // ponytail: V8 parity — /\p{Script=Han}/u.test(s).
    public static bool HasHan(string s) { foreach (var r in s.EnumerateRunes()) if (IsHan(r.Value)) return true; return false; }

    // Code point at i and its UTF-16 length; a lone surrogate reads as U+FFFD (neither space, Han nor mark — as in JS) but is
    // sliced from the original string, so tokens always concatenate back to s.
    static (int cp, int len) CodePointAt(string s, int i) { Rune.DecodeFromUtf16(s.AsSpan(i), out var r, out var n); return (r.Value, n); }
    // Emits maximal runs where `same(first, next)` holds, by code point.
    static List<string> Runs(string s, Func<int, int, bool> same)
    {
        var list = new List<string>();
        for (var i = 0; i < s.Length;)
        {
            var (first, n) = CodePointAt(s, i); var j = i + n;
            while (j < s.Length) { var (cp, m) = CodePointAt(s, j); if (!same(first, cp)) break; j += m; }
            list.Add(s[i..j]); i = j;
        }
        return list;
    }
    // ponytail: V8 parity — s.match(/\s+|\S+/gu) || [].
    public static List<string> SpaceTokens(string s) => Runs(s, (first, cp) => IsSpace(first) == IsSpace(cp));
    // ponytail: V8 parity — s.match(/\s+|\p{Script=Han}[\p{Script=Han}\p{Mark}]*|[^\s\p{Script=Han}]+/gu) || [].
    public static List<string> HanTokens(string s) => Runs(s, (first, cp) =>
        IsSpace(first) ? IsSpace(cp) : IsHan(first) ? IsHan(cp) || IsMark(cp) : !IsSpace(cp) && !IsHan(cp));

    // ponytail: V8 parity — String.prototype.trimStart / trimEnd (same IsSpace table as Trim; every JS space is BMP).
    public static string TrimStart(string s) { var i = 0; while (i < s.Length && IsSpace(s[i])) i++; return s[i..]; }
    public static string TrimEnd(string s) { var j = s.Length; while (j > 0 && IsSpace(s[j - 1])) j--; return s[..j]; }

    // Array.from(string): code points, a lone surrogate kept as its own element.
    public static List<string> CodePoints(string s)
    {
        var list = new List<string>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { list.Add(s.Substring(i, 2)); i++; }
            else list.Add(s[i].ToString());
        }
        return list;
    }

    // ES2023 §22.2.6.14 RegExp.prototype[@@split] for a non-unicode regex without a limit: empty matches at 0 and at the end never
    // split, captures are interleaved. One walk for every split below — Regex.Split differs on empty matches at the ends.
    static string[] JsSplit(Regex re, string s)
    {
        if (s.Length == 0) return re.Match(s).Success ? [] : [""];
        var parts = new List<string>(); int p = 0, q = 0;
        while (q < s.Length)
        {
            var m = re.Match(s, q);                   // leftmost match at ≥ q = JS's sticky retry at q, q+1, …; lookbehind sees before q
            if (!m.Success || m.Index >= s.Length) break;
            var e = m.Index + m.Length;
            if (e == p) { q = m.Index + 1; continue; }
            parts.Add(s[p..m.Index]);
            for (var g = 1; g < m.Groups.Count; g++) parts.Add(m.Groups[g].Value);   // ponytail: groups always participate in these patterns
            p = q = e;
        }
        parts.Add(s[p..]);
        return [.. parts];
    }
    static readonly Regex Spaces = new(WsClass + "+", Es), SpacesKept = new("(" + WsClass + "+)", Es);
    // ponytail: V8 parity — s.split(/\s+/) (render-tree.mjs:201).
    public static string[] SplitOnSpaces(string s) => JsSplit(Spaces, s);
    // ponytail: V8 parity — s.split(/(\s+)/) (render-timeline.mjs:282).
    public static string[] SplitKeepingSpaces(string s) => JsSplit(SpacesKept, s);

    // BMP code-unit classes, no `u` flag: .NET Regex matches UTF-16 units, so a surrogate pair falls in the "other" run as in V8.
    const string Cjk = "\u2E80-\u9FFF\uAC00-\uD7AF\uF900-\uFAFF\uFF00-\uFFEF";
    static readonly Regex CjkWrap = new("[" + Cjk + "]|[^" + WsClass[1..^1] + Cjk + "]+" + WsClass + "*|" + WsClass + "+", Es);
    // ponytail: V8 parity — s.match(/[⺀-鿿가-힯豈-﫿＀-￯]|[^\s⺀-鿿가-힯豈-﫿＀-￯]+\s*|\s+/g) || [s] (render-tree.mjs:126).
    public static List<string> CjkWrapTokens(string s)
    {
        var list = CjkWrap.Matches(s).Select(m => m.Value).ToList();
        return list.Count > 0 ? list : [s];
    }

    // Lookbehind throws under RegexOptions.ECMAScript: default options + CultureInvariant (no \s, no case, no class shorthands).
    static readonly Regex Signature = new(@"(?<=\(|, )|(?=\): )", RegexOptions.CultureInvariant),
        AfterCommaSpace = new("(?<=, )", RegexOptions.CultureInvariant),
        CommasKept = new("(," + WsClass + "*)", Es);
    // ponytail: V8 parity — s.split(/(?<=\(|, )|(?=\): )/) (render-class.mjs:176).
    public static string[] SplitSignature(string s) => JsSplit(Signature, s);
    // ponytail: V8 parity — s.split(/(?<=, )/) (render-class.mjs:181).
    public static string[] SplitAfterCommaSpace(string s) => JsSplit(AfterCommaSpace, s);
    // ponytail: V8 parity — s.split(/(,\s*)/) (render-class.mjs:884).
    public static string[] SplitKeepingCommas(string s) => JsSplit(CommasKept, s);
    // ponytail: V8 parity — /^,\s*$/.test(part) (render-class.mjs:885), as code: JS `$` has no before-final-\n match.
    public static bool IsCommaPart(string part) => part.Length > 0 && part[0] == ',' && Trim(part[1..]).Length == 0;
}
