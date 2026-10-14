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
    // ponytail: V8 parity — UAX #29 subset; dictionary scripts per grapheme (stated deviation)
    // Intl.Segmenter word granularity (label-capacity.mjs:32-33) as WB3–WB13b over Graphemes, classed by first code point. Port of
    // uax29proto.mjs with three V8-measured corrections: WB4 attaches every Extend/Format-only cluster except after a Newline, WB3d and
    // the Hangul run look at the previous cluster's last code point, and only syllables form ICU's Hangul run (jamo are ALetter).
    // No regional-indicator rule: Graphemes already pairs flags. Ceiling: a grapheme that V8 splits at word level (Prepend U+0600,
    // jamo next to a syllable) or WB3c (ZWJ × pictograph after a non-pictograph) is not reproduced; ubrk_* from icu.dll if that matters.
    public static List<string> WordBreaks(string s)
    {
        var cs = new List<(string S, Wb First, Wb Last)>();
        foreach (var g in Graphemes(s))
        {
            if (cs.Count > 0 && cs[^1].First != Wb.Newline && IsExtendOrFormat(g)) { var p = cs[^1]; cs[^1] = (p.S + g, p.First, LastClass(g)); continue; }   // WB4
            cs.Add((g, WordClass(CodePointAt(g, 0).cp), LastClass(g)));
        }
        var words = new List<string>();
        var word = new StringBuilder();
        for (var k = 0; k < cs.Count; k++)
        {
            if (k > 0 && !JoinsWord(cs, k)) { words.Add(word.ToString()); word.Clear(); }
            word.Append(cs[k].S);
        }
        if (word.Length > 0) words.Add(word.ToString());
        return words;
    }

    enum Wb { Other, Newline, WSeg, Dict, SQ, DQ, MidLetter, MidNumLet, MidNum, ExtendNumLet, Numeric, HebrewLetter, Hangul, ALetter }
    const string NewlineChars = "\r\n\u000B\u000C\u0085\u2028\u2029", WSegChars = " \u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2008\u2009\u200A\u205F\u3000",
        MidLetterChars = ":\u00B7\u0387\u055F\u05F4\u2027\uFE13\uFE55\uFF1A", MidNumLetChars = ".\u2018\u2019\u2024\uFE52\uFF07\uFF0E",
        MidNumChars = ",;\u037E\u0589\u060C\u060D\u066C\u07F8\u2044\uFE10\uFE14\uFE50\uFE54\uFF0C\uFF1B",
        ExtendNumLetChars = "_\u202F\u203F\u2040\u2054\uFE33\uFE34\uFE4D\uFE4E\uFE4F\uFF3F";
    // Hiragana, Katakana (incl. U+30FC, U+FF70), Thai, Lao, Khmer, Myanmar; Han is IsHan. Only Script=Common letters inside these
    // blocks (U+FF9E/FF9F) differ from \p{Script=…}, and V8 breaks around them as it does around a dictionary grapheme.
    static readonly int[] DictRanges =
    [
        0x3041, 0x309F, 0x30A0, 0x30FF, 0x31F0, 0x31FF, 0x32D0, 0x32FE, 0x3300, 0x3357, 0xFF66, 0xFF9F, 0x0E00, 0x0E7F, 0x0E80, 0x0EFF,
        0x1780, 0x17FF, 0x19E0, 0x19FF, 0x1000, 0x109F, 0xA9E0, 0xA9FF, 0xAA60, 0xAA7F,
    ];
    static readonly int[] HebrewLetterRanges = [0x05D0, 0x05EA, 0x05EF, 0x05F2, 0xFB1D, 0xFB4F];

    static bool InRanges(int[] r, int cp)
    {
        for (var i = 0; i < r.Length; i += 2) if (cp >= r[i] && cp <= r[i + 1]) return true;
        return false;
    }
    static bool In(string chars, int cp) => cp <= 0xFFFF && chars.Contains((char)cp);
    static Wb WordClass(int cp)
    {
        if (In(NewlineChars, cp)) return Wb.Newline;
        if (In(WSegChars, cp)) return Wb.WSeg;
        if (IsHan(cp) || InRanges(DictRanges, cp)) return Wb.Dict;
        if (cp == '\'') return Wb.SQ;
        if (cp == '"') return Wb.DQ;
        if (In(MidLetterChars, cp)) return Wb.MidLetter;
        if (In(MidNumLetChars, cp)) return Wb.MidNumLet;
        if (In(MidNumChars, cp)) return Wb.MidNum;
        if (In(ExtendNumLetChars, cp)) return Wb.ExtendNumLet;
        var cat = CharUnicodeInfo.GetUnicodeCategory(cp);
        if (cat == UnicodeCategory.DecimalDigitNumber) return Wb.Numeric;
        var letter = cat is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter;
        if (letter && InRanges(HebrewLetterRanges, cp)) return Wb.HebrewLetter;
        if (cp is >= 0xAC00 and <= 0xD7A3) return Wb.Hangul;   // ICU word.txt: syllables join only syllables
        return letter || cat == UnicodeCategory.LetterNumber ? Wb.ALetter : Wb.Other;
    }
    static Wb LastClass(string g) => WordClass(CodePointAt(g, g.Length - (g.Length > 1 && char.IsSurrogatePair(g[^2], g[^1]) ? 2 : 1)).cp);
    // WB Extend/Format/ZWJ: marks, Cf except U+200B (WB Other), emoji modifiers and the halfwidth sound marks (Grapheme_Extend).
    static bool IsExtendOrFormat(string g)
    {
        foreach (var r in g.EnumerateRunes())
        {
            var cp = r.Value;
            if (cp is 0x200B) return false;
            if (cp is >= 0x1F3FB and <= 0x1F3FF or 0xFF9E or 0xFF9F) continue;
            if (CharUnicodeInfo.GetUnicodeCategory(cp) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)) return false;
        }
        return true;
    }
    static bool AH(Wb? c) => c is Wb.ALetter or Wb.HebrewLetter;
    static bool Mid(Wb? c) => c is Wb.MidLetter or Wb.MidNumLet or Wb.SQ;
    static bool MidN(Wb? c) => c is Wb.MidNum or Wb.MidNumLet or Wb.SQ;
    static bool JoinsWord(List<(string S, Wb First, Wb Last)> cs, int k)
    {
        Wb? p2 = k >= 2 ? cs[k - 2].First : null, n2 = k + 1 < cs.Count ? cs[k + 1].First : null;
        Wb p = cs[k - 1].First, n = cs[k].First;
        if (p == Wb.WSeg && n == Wb.WSeg) return cs[k - 1].Last == Wb.WSeg;                                   // WB3d (before WB4)
        if (p == Wb.Newline || n == Wb.Newline) return cs[k - 1].S == "\r" && cs[k].S == "\n";               // WB3/3a/3b
        if (AH(p) && AH(n)) return true;                                                                     // WB5
        if (p == Wb.Hangul && n == Wb.Hangul) return cs[k - 1].Last == Wb.Hangul;                             // ICU $Hangul run
        if (AH(p) && Mid(n) && AH(n2)) return true;                                                          // WB6
        if (AH(p2) && Mid(p) && AH(n)) return true;                                                          // WB7
        if (p == Wb.HebrewLetter && n == Wb.SQ) return true;                                                 // WB7a
        if (p == Wb.HebrewLetter && n == Wb.DQ && n2 == Wb.HebrewLetter) return true;                         // WB7b
        if (p2 == Wb.HebrewLetter && p == Wb.DQ && n == Wb.HebrewLetter) return true;                         // WB7c
        if ((p == Wb.Numeric || AH(p)) && (n == Wb.Numeric || AH(n)) && (p == Wb.Numeric || n == Wb.Numeric)) return true;   // WB8-10
        if (p == Wb.Numeric && MidN(n) && n2 == Wb.Numeric) return true;                                     // WB12
        if (p2 == Wb.Numeric && MidN(p) && n == Wb.Numeric) return true;                                     // WB11
        if ((AH(p) || p is Wb.Numeric or Wb.ExtendNumLet) && n == Wb.ExtendNumLet) return true;               // WB13a
        return p == Wb.ExtendNumLet && (AH(n) || n == Wb.Numeric);                                           // WB13b
    }

    static readonly int[] HanRanges =
    [
        0x2E80, 0x2E99, 0x2E9B, 0x2EF3, 0x2F00, 0x2FD5, 0x3005, 0x3005, 0x3007, 0x3007, 0x3021, 0x3029, 0x3038, 0x303B,
        0x3400, 0x4DBF, 0x4E00, 0x9FFF, 0xF900, 0xFA6D, 0xFA70, 0xFAD9, 0x16FE2, 0x16FE3, 0x16FF0, 0x16FF1,
        0x20000, 0x2A6DF, 0x2A700, 0x2B739, 0x2B740, 0x2B81D, 0x2B820, 0x2CEA1, 0x2CEB0, 0x2EBE0, 0x2EBF0, 0x2EE5D,
        0x2F800, 0x2FA1D, 0x30000, 0x3134A, 0x31350, 0x323AF,
    ];
    // ponytail: V8 parity — \p{Script=Han} by range table (equals V8's set by full enumeration); .NET Regex has no \p{Script=…}.
    public static bool IsHan(int cp) => InRanges(HanRanges, cp);
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
    // ponytail: V8 parity — s.split('-') with a one-character string separator (render-timeline.mjs:68): String.Split(char)
    // keeps empty parts and returns [""] for "", exactly as JS does.
    public static string[] SplitOnChar(string s, char separator) => s.Split(separator);

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
