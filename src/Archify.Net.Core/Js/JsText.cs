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
    // No regional-indicator rule: Graphemes already pairs flags. Ceiling, both directions: V8 splits inside a grapheme at word level (Prepend U+0600 before a space,
    // jamo next to a syllable) that the port keeps whole; the port splits across graphemes V8 joins (WB3c: ZWJ × pictograph after a letter;
    // a Format/Prepend-led grapheme after a letter); ubrk_* from icu.dll if that matters.
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
    // PORT-009: \p{Script=Latin}, \p{L} and \p{N} as V8 (Node 24) reads them, minted by tools/capture-fixtures.mjs into
    // tests/fixtures/js/unicode-props.json and copied here as sorted inclusive [first, last] pairs; JsTextTests.Unicode_properties_match_V8
    // checks every code point. .NET Regex has no \p{Script=...}, and its categories follow the runtime's Unicode version, not V8's.
    static readonly int[] LatinRanges =
    [
        0x0041, 0x005A, 0x0061, 0x007A, 0x00AA, 0x00AA, 0x00BA, 0x00BA, 0x00C0, 0x00D6, 0x00D8, 0x00F6,
        0x00F8, 0x02B8, 0x02E0, 0x02E4, 0x1D00, 0x1D25, 0x1D2C, 0x1D5C, 0x1D62, 0x1D65, 0x1D6B, 0x1D77,
        0x1D79, 0x1DBE, 0x1E00, 0x1EFF, 0x2071, 0x2071, 0x207F, 0x207F, 0x2090, 0x209C, 0x212A, 0x212B,
        0x2132, 0x2132, 0x214E, 0x214E, 0x2160, 0x2188, 0x2C60, 0x2C7F, 0xA722, 0xA787, 0xA78B, 0xA7CD,
        0xA7D0, 0xA7D1, 0xA7D3, 0xA7D3, 0xA7D5, 0xA7DC, 0xA7F2, 0xA7FF, 0xAB30, 0xAB5A, 0xAB5C, 0xAB64,
        0xAB66, 0xAB69, 0xFB00, 0xFB06, 0xFF21, 0xFF3A, 0xFF41, 0xFF5A, 0x10780, 0x10785, 0x10787, 0x107B0,
        0x107B2, 0x107BA, 0x1DF00, 0x1DF1E, 0x1DF25, 0x1DF2A,
    ];
    static readonly int[] LetterRanges =
    [
        0x0041, 0x005A, 0x0061, 0x007A, 0x00AA, 0x00AA, 0x00B5, 0x00B5, 0x00BA, 0x00BA, 0x00C0, 0x00D6,
        0x00D8, 0x00F6, 0x00F8, 0x02C1, 0x02C6, 0x02D1, 0x02E0, 0x02E4, 0x02EC, 0x02EC, 0x02EE, 0x02EE,
        0x0370, 0x0374, 0x0376, 0x0377, 0x037A, 0x037D, 0x037F, 0x037F, 0x0386, 0x0386, 0x0388, 0x038A,
        0x038C, 0x038C, 0x038E, 0x03A1, 0x03A3, 0x03F5, 0x03F7, 0x0481, 0x048A, 0x052F, 0x0531, 0x0556,
        0x0559, 0x0559, 0x0560, 0x0588, 0x05D0, 0x05EA, 0x05EF, 0x05F2, 0x0620, 0x064A, 0x066E, 0x066F,
        0x0671, 0x06D3, 0x06D5, 0x06D5, 0x06E5, 0x06E6, 0x06EE, 0x06EF, 0x06FA, 0x06FC, 0x06FF, 0x06FF,
        0x0710, 0x0710, 0x0712, 0x072F, 0x074D, 0x07A5, 0x07B1, 0x07B1, 0x07CA, 0x07EA, 0x07F4, 0x07F5,
        0x07FA, 0x07FA, 0x0800, 0x0815, 0x081A, 0x081A, 0x0824, 0x0824, 0x0828, 0x0828, 0x0840, 0x0858,
        0x0860, 0x086A, 0x0870, 0x0887, 0x0889, 0x088E, 0x08A0, 0x08C9, 0x0904, 0x0939, 0x093D, 0x093D,
        0x0950, 0x0950, 0x0958, 0x0961, 0x0971, 0x0980, 0x0985, 0x098C, 0x098F, 0x0990, 0x0993, 0x09A8,
        0x09AA, 0x09B0, 0x09B2, 0x09B2, 0x09B6, 0x09B9, 0x09BD, 0x09BD, 0x09CE, 0x09CE, 0x09DC, 0x09DD,
        0x09DF, 0x09E1, 0x09F0, 0x09F1, 0x09FC, 0x09FC, 0x0A05, 0x0A0A, 0x0A0F, 0x0A10, 0x0A13, 0x0A28,
        0x0A2A, 0x0A30, 0x0A32, 0x0A33, 0x0A35, 0x0A36, 0x0A38, 0x0A39, 0x0A59, 0x0A5C, 0x0A5E, 0x0A5E,
        0x0A72, 0x0A74, 0x0A85, 0x0A8D, 0x0A8F, 0x0A91, 0x0A93, 0x0AA8, 0x0AAA, 0x0AB0, 0x0AB2, 0x0AB3,
        0x0AB5, 0x0AB9, 0x0ABD, 0x0ABD, 0x0AD0, 0x0AD0, 0x0AE0, 0x0AE1, 0x0AF9, 0x0AF9, 0x0B05, 0x0B0C,
        0x0B0F, 0x0B10, 0x0B13, 0x0B28, 0x0B2A, 0x0B30, 0x0B32, 0x0B33, 0x0B35, 0x0B39, 0x0B3D, 0x0B3D,
        0x0B5C, 0x0B5D, 0x0B5F, 0x0B61, 0x0B71, 0x0B71, 0x0B83, 0x0B83, 0x0B85, 0x0B8A, 0x0B8E, 0x0B90,
        0x0B92, 0x0B95, 0x0B99, 0x0B9A, 0x0B9C, 0x0B9C, 0x0B9E, 0x0B9F, 0x0BA3, 0x0BA4, 0x0BA8, 0x0BAA,
        0x0BAE, 0x0BB9, 0x0BD0, 0x0BD0, 0x0C05, 0x0C0C, 0x0C0E, 0x0C10, 0x0C12, 0x0C28, 0x0C2A, 0x0C39,
        0x0C3D, 0x0C3D, 0x0C58, 0x0C5A, 0x0C5D, 0x0C5D, 0x0C60, 0x0C61, 0x0C80, 0x0C80, 0x0C85, 0x0C8C,
        0x0C8E, 0x0C90, 0x0C92, 0x0CA8, 0x0CAA, 0x0CB3, 0x0CB5, 0x0CB9, 0x0CBD, 0x0CBD, 0x0CDD, 0x0CDE,
        0x0CE0, 0x0CE1, 0x0CF1, 0x0CF2, 0x0D04, 0x0D0C, 0x0D0E, 0x0D10, 0x0D12, 0x0D3A, 0x0D3D, 0x0D3D,
        0x0D4E, 0x0D4E, 0x0D54, 0x0D56, 0x0D5F, 0x0D61, 0x0D7A, 0x0D7F, 0x0D85, 0x0D96, 0x0D9A, 0x0DB1,
        0x0DB3, 0x0DBB, 0x0DBD, 0x0DBD, 0x0DC0, 0x0DC6, 0x0E01, 0x0E30, 0x0E32, 0x0E33, 0x0E40, 0x0E46,
        0x0E81, 0x0E82, 0x0E84, 0x0E84, 0x0E86, 0x0E8A, 0x0E8C, 0x0EA3, 0x0EA5, 0x0EA5, 0x0EA7, 0x0EB0,
        0x0EB2, 0x0EB3, 0x0EBD, 0x0EBD, 0x0EC0, 0x0EC4, 0x0EC6, 0x0EC6, 0x0EDC, 0x0EDF, 0x0F00, 0x0F00,
        0x0F40, 0x0F47, 0x0F49, 0x0F6C, 0x0F88, 0x0F8C, 0x1000, 0x102A, 0x103F, 0x103F, 0x1050, 0x1055,
        0x105A, 0x105D, 0x1061, 0x1061, 0x1065, 0x1066, 0x106E, 0x1070, 0x1075, 0x1081, 0x108E, 0x108E,
        0x10A0, 0x10C5, 0x10C7, 0x10C7, 0x10CD, 0x10CD, 0x10D0, 0x10FA, 0x10FC, 0x1248, 0x124A, 0x124D,
        0x1250, 0x1256, 0x1258, 0x1258, 0x125A, 0x125D, 0x1260, 0x1288, 0x128A, 0x128D, 0x1290, 0x12B0,
        0x12B2, 0x12B5, 0x12B8, 0x12BE, 0x12C0, 0x12C0, 0x12C2, 0x12C5, 0x12C8, 0x12D6, 0x12D8, 0x1310,
        0x1312, 0x1315, 0x1318, 0x135A, 0x1380, 0x138F, 0x13A0, 0x13F5, 0x13F8, 0x13FD, 0x1401, 0x166C,
        0x166F, 0x167F, 0x1681, 0x169A, 0x16A0, 0x16EA, 0x16F1, 0x16F8, 0x1700, 0x1711, 0x171F, 0x1731,
        0x1740, 0x1751, 0x1760, 0x176C, 0x176E, 0x1770, 0x1780, 0x17B3, 0x17D7, 0x17D7, 0x17DC, 0x17DC,
        0x1820, 0x1878, 0x1880, 0x1884, 0x1887, 0x18A8, 0x18AA, 0x18AA, 0x18B0, 0x18F5, 0x1900, 0x191E,
        0x1950, 0x196D, 0x1970, 0x1974, 0x1980, 0x19AB, 0x19B0, 0x19C9, 0x1A00, 0x1A16, 0x1A20, 0x1A54,
        0x1AA7, 0x1AA7, 0x1B05, 0x1B33, 0x1B45, 0x1B4C, 0x1B83, 0x1BA0, 0x1BAE, 0x1BAF, 0x1BBA, 0x1BE5,
        0x1C00, 0x1C23, 0x1C4D, 0x1C4F, 0x1C5A, 0x1C7D, 0x1C80, 0x1C8A, 0x1C90, 0x1CBA, 0x1CBD, 0x1CBF,
        0x1CE9, 0x1CEC, 0x1CEE, 0x1CF3, 0x1CF5, 0x1CF6, 0x1CFA, 0x1CFA, 0x1D00, 0x1DBF, 0x1E00, 0x1F15,
        0x1F18, 0x1F1D, 0x1F20, 0x1F45, 0x1F48, 0x1F4D, 0x1F50, 0x1F57, 0x1F59, 0x1F59, 0x1F5B, 0x1F5B,
        0x1F5D, 0x1F5D, 0x1F5F, 0x1F7D, 0x1F80, 0x1FB4, 0x1FB6, 0x1FBC, 0x1FBE, 0x1FBE, 0x1FC2, 0x1FC4,
        0x1FC6, 0x1FCC, 0x1FD0, 0x1FD3, 0x1FD6, 0x1FDB, 0x1FE0, 0x1FEC, 0x1FF2, 0x1FF4, 0x1FF6, 0x1FFC,
        0x2071, 0x2071, 0x207F, 0x207F, 0x2090, 0x209C, 0x2102, 0x2102, 0x2107, 0x2107, 0x210A, 0x2113,
        0x2115, 0x2115, 0x2119, 0x211D, 0x2124, 0x2124, 0x2126, 0x2126, 0x2128, 0x2128, 0x212A, 0x212D,
        0x212F, 0x2139, 0x213C, 0x213F, 0x2145, 0x2149, 0x214E, 0x214E, 0x2183, 0x2184, 0x2C00, 0x2CE4,
        0x2CEB, 0x2CEE, 0x2CF2, 0x2CF3, 0x2D00, 0x2D25, 0x2D27, 0x2D27, 0x2D2D, 0x2D2D, 0x2D30, 0x2D67,
        0x2D6F, 0x2D6F, 0x2D80, 0x2D96, 0x2DA0, 0x2DA6, 0x2DA8, 0x2DAE, 0x2DB0, 0x2DB6, 0x2DB8, 0x2DBE,
        0x2DC0, 0x2DC6, 0x2DC8, 0x2DCE, 0x2DD0, 0x2DD6, 0x2DD8, 0x2DDE, 0x2E2F, 0x2E2F, 0x3005, 0x3006,
        0x3031, 0x3035, 0x303B, 0x303C, 0x3041, 0x3096, 0x309D, 0x309F, 0x30A1, 0x30FA, 0x30FC, 0x30FF,
        0x3105, 0x312F, 0x3131, 0x318E, 0x31A0, 0x31BF, 0x31F0, 0x31FF, 0x3400, 0x4DBF, 0x4E00, 0xA48C,
        0xA4D0, 0xA4FD, 0xA500, 0xA60C, 0xA610, 0xA61F, 0xA62A, 0xA62B, 0xA640, 0xA66E, 0xA67F, 0xA69D,
        0xA6A0, 0xA6E5, 0xA717, 0xA71F, 0xA722, 0xA788, 0xA78B, 0xA7CD, 0xA7D0, 0xA7D1, 0xA7D3, 0xA7D3,
        0xA7D5, 0xA7DC, 0xA7F2, 0xA801, 0xA803, 0xA805, 0xA807, 0xA80A, 0xA80C, 0xA822, 0xA840, 0xA873,
        0xA882, 0xA8B3, 0xA8F2, 0xA8F7, 0xA8FB, 0xA8FB, 0xA8FD, 0xA8FE, 0xA90A, 0xA925, 0xA930, 0xA946,
        0xA960, 0xA97C, 0xA984, 0xA9B2, 0xA9CF, 0xA9CF, 0xA9E0, 0xA9E4, 0xA9E6, 0xA9EF, 0xA9FA, 0xA9FE,
        0xAA00, 0xAA28, 0xAA40, 0xAA42, 0xAA44, 0xAA4B, 0xAA60, 0xAA76, 0xAA7A, 0xAA7A, 0xAA7E, 0xAAAF,
        0xAAB1, 0xAAB1, 0xAAB5, 0xAAB6, 0xAAB9, 0xAABD, 0xAAC0, 0xAAC0, 0xAAC2, 0xAAC2, 0xAADB, 0xAADD,
        0xAAE0, 0xAAEA, 0xAAF2, 0xAAF4, 0xAB01, 0xAB06, 0xAB09, 0xAB0E, 0xAB11, 0xAB16, 0xAB20, 0xAB26,
        0xAB28, 0xAB2E, 0xAB30, 0xAB5A, 0xAB5C, 0xAB69, 0xAB70, 0xABE2, 0xAC00, 0xD7A3, 0xD7B0, 0xD7C6,
        0xD7CB, 0xD7FB, 0xF900, 0xFA6D, 0xFA70, 0xFAD9, 0xFB00, 0xFB06, 0xFB13, 0xFB17, 0xFB1D, 0xFB1D,
        0xFB1F, 0xFB28, 0xFB2A, 0xFB36, 0xFB38, 0xFB3C, 0xFB3E, 0xFB3E, 0xFB40, 0xFB41, 0xFB43, 0xFB44,
        0xFB46, 0xFBB1, 0xFBD3, 0xFD3D, 0xFD50, 0xFD8F, 0xFD92, 0xFDC7, 0xFDF0, 0xFDFB, 0xFE70, 0xFE74,
        0xFE76, 0xFEFC, 0xFF21, 0xFF3A, 0xFF41, 0xFF5A, 0xFF66, 0xFFBE, 0xFFC2, 0xFFC7, 0xFFCA, 0xFFCF,
        0xFFD2, 0xFFD7, 0xFFDA, 0xFFDC, 0x10000, 0x1000B, 0x1000D, 0x10026, 0x10028, 0x1003A, 0x1003C, 0x1003D,
        0x1003F, 0x1004D, 0x10050, 0x1005D, 0x10080, 0x100FA, 0x10280, 0x1029C, 0x102A0, 0x102D0, 0x10300, 0x1031F,
        0x1032D, 0x10340, 0x10342, 0x10349, 0x10350, 0x10375, 0x10380, 0x1039D, 0x103A0, 0x103C3, 0x103C8, 0x103CF,
        0x10400, 0x1049D, 0x104B0, 0x104D3, 0x104D8, 0x104FB, 0x10500, 0x10527, 0x10530, 0x10563, 0x10570, 0x1057A,
        0x1057C, 0x1058A, 0x1058C, 0x10592, 0x10594, 0x10595, 0x10597, 0x105A1, 0x105A3, 0x105B1, 0x105B3, 0x105B9,
        0x105BB, 0x105BC, 0x105C0, 0x105F3, 0x10600, 0x10736, 0x10740, 0x10755, 0x10760, 0x10767, 0x10780, 0x10785,
        0x10787, 0x107B0, 0x107B2, 0x107BA, 0x10800, 0x10805, 0x10808, 0x10808, 0x1080A, 0x10835, 0x10837, 0x10838,
        0x1083C, 0x1083C, 0x1083F, 0x10855, 0x10860, 0x10876, 0x10880, 0x1089E, 0x108E0, 0x108F2, 0x108F4, 0x108F5,
        0x10900, 0x10915, 0x10920, 0x10939, 0x10980, 0x109B7, 0x109BE, 0x109BF, 0x10A00, 0x10A00, 0x10A10, 0x10A13,
        0x10A15, 0x10A17, 0x10A19, 0x10A35, 0x10A60, 0x10A7C, 0x10A80, 0x10A9C, 0x10AC0, 0x10AC7, 0x10AC9, 0x10AE4,
        0x10B00, 0x10B35, 0x10B40, 0x10B55, 0x10B60, 0x10B72, 0x10B80, 0x10B91, 0x10C00, 0x10C48, 0x10C80, 0x10CB2,
        0x10CC0, 0x10CF2, 0x10D00, 0x10D23, 0x10D4A, 0x10D65, 0x10D6F, 0x10D85, 0x10E80, 0x10EA9, 0x10EB0, 0x10EB1,
        0x10EC2, 0x10EC4, 0x10F00, 0x10F1C, 0x10F27, 0x10F27, 0x10F30, 0x10F45, 0x10F70, 0x10F81, 0x10FB0, 0x10FC4,
        0x10FE0, 0x10FF6, 0x11003, 0x11037, 0x11071, 0x11072, 0x11075, 0x11075, 0x11083, 0x110AF, 0x110D0, 0x110E8,
        0x11103, 0x11126, 0x11144, 0x11144, 0x11147, 0x11147, 0x11150, 0x11172, 0x11176, 0x11176, 0x11183, 0x111B2,
        0x111C1, 0x111C4, 0x111DA, 0x111DA, 0x111DC, 0x111DC, 0x11200, 0x11211, 0x11213, 0x1122B, 0x1123F, 0x11240,
        0x11280, 0x11286, 0x11288, 0x11288, 0x1128A, 0x1128D, 0x1128F, 0x1129D, 0x1129F, 0x112A8, 0x112B0, 0x112DE,
        0x11305, 0x1130C, 0x1130F, 0x11310, 0x11313, 0x11328, 0x1132A, 0x11330, 0x11332, 0x11333, 0x11335, 0x11339,
        0x1133D, 0x1133D, 0x11350, 0x11350, 0x1135D, 0x11361, 0x11380, 0x11389, 0x1138B, 0x1138B, 0x1138E, 0x1138E,
        0x11390, 0x113B5, 0x113B7, 0x113B7, 0x113D1, 0x113D1, 0x113D3, 0x113D3, 0x11400, 0x11434, 0x11447, 0x1144A,
        0x1145F, 0x11461, 0x11480, 0x114AF, 0x114C4, 0x114C5, 0x114C7, 0x114C7, 0x11580, 0x115AE, 0x115D8, 0x115DB,
        0x11600, 0x1162F, 0x11644, 0x11644, 0x11680, 0x116AA, 0x116B8, 0x116B8, 0x11700, 0x1171A, 0x11740, 0x11746,
        0x11800, 0x1182B, 0x118A0, 0x118DF, 0x118FF, 0x11906, 0x11909, 0x11909, 0x1190C, 0x11913, 0x11915, 0x11916,
        0x11918, 0x1192F, 0x1193F, 0x1193F, 0x11941, 0x11941, 0x119A0, 0x119A7, 0x119AA, 0x119D0, 0x119E1, 0x119E1,
        0x119E3, 0x119E3, 0x11A00, 0x11A00, 0x11A0B, 0x11A32, 0x11A3A, 0x11A3A, 0x11A50, 0x11A50, 0x11A5C, 0x11A89,
        0x11A9D, 0x11A9D, 0x11AB0, 0x11AF8, 0x11BC0, 0x11BE0, 0x11C00, 0x11C08, 0x11C0A, 0x11C2E, 0x11C40, 0x11C40,
        0x11C72, 0x11C8F, 0x11D00, 0x11D06, 0x11D08, 0x11D09, 0x11D0B, 0x11D30, 0x11D46, 0x11D46, 0x11D60, 0x11D65,
        0x11D67, 0x11D68, 0x11D6A, 0x11D89, 0x11D98, 0x11D98, 0x11EE0, 0x11EF2, 0x11F02, 0x11F02, 0x11F04, 0x11F10,
        0x11F12, 0x11F33, 0x11FB0, 0x11FB0, 0x12000, 0x12399, 0x12480, 0x12543, 0x12F90, 0x12FF0, 0x13000, 0x1342F,
        0x13441, 0x13446, 0x13460, 0x143FA, 0x14400, 0x14646, 0x16100, 0x1611D, 0x16800, 0x16A38, 0x16A40, 0x16A5E,
        0x16A70, 0x16ABE, 0x16AD0, 0x16AED, 0x16B00, 0x16B2F, 0x16B40, 0x16B43, 0x16B63, 0x16B77, 0x16B7D, 0x16B8F,
        0x16D40, 0x16D6C, 0x16E40, 0x16E7F, 0x16F00, 0x16F4A, 0x16F50, 0x16F50, 0x16F93, 0x16F9F, 0x16FE0, 0x16FE1,
        0x16FE3, 0x16FE3, 0x17000, 0x187F7, 0x18800, 0x18CD5, 0x18CFF, 0x18D08, 0x1AFF0, 0x1AFF3, 0x1AFF5, 0x1AFFB,
        0x1AFFD, 0x1AFFE, 0x1B000, 0x1B122, 0x1B132, 0x1B132, 0x1B150, 0x1B152, 0x1B155, 0x1B155, 0x1B164, 0x1B167,
        0x1B170, 0x1B2FB, 0x1BC00, 0x1BC6A, 0x1BC70, 0x1BC7C, 0x1BC80, 0x1BC88, 0x1BC90, 0x1BC99, 0x1D400, 0x1D454,
        0x1D456, 0x1D49C, 0x1D49E, 0x1D49F, 0x1D4A2, 0x1D4A2, 0x1D4A5, 0x1D4A6, 0x1D4A9, 0x1D4AC, 0x1D4AE, 0x1D4B9,
        0x1D4BB, 0x1D4BB, 0x1D4BD, 0x1D4C3, 0x1D4C5, 0x1D505, 0x1D507, 0x1D50A, 0x1D50D, 0x1D514, 0x1D516, 0x1D51C,
        0x1D51E, 0x1D539, 0x1D53B, 0x1D53E, 0x1D540, 0x1D544, 0x1D546, 0x1D546, 0x1D54A, 0x1D550, 0x1D552, 0x1D6A5,
        0x1D6A8, 0x1D6C0, 0x1D6C2, 0x1D6DA, 0x1D6DC, 0x1D6FA, 0x1D6FC, 0x1D714, 0x1D716, 0x1D734, 0x1D736, 0x1D74E,
        0x1D750, 0x1D76E, 0x1D770, 0x1D788, 0x1D78A, 0x1D7A8, 0x1D7AA, 0x1D7C2, 0x1D7C4, 0x1D7CB, 0x1DF00, 0x1DF1E,
        0x1DF25, 0x1DF2A, 0x1E030, 0x1E06D, 0x1E100, 0x1E12C, 0x1E137, 0x1E13D, 0x1E14E, 0x1E14E, 0x1E290, 0x1E2AD,
        0x1E2C0, 0x1E2EB, 0x1E4D0, 0x1E4EB, 0x1E5D0, 0x1E5ED, 0x1E5F0, 0x1E5F0, 0x1E7E0, 0x1E7E6, 0x1E7E8, 0x1E7EB,
        0x1E7ED, 0x1E7EE, 0x1E7F0, 0x1E7FE, 0x1E800, 0x1E8C4, 0x1E900, 0x1E943, 0x1E94B, 0x1E94B, 0x1EE00, 0x1EE03,
        0x1EE05, 0x1EE1F, 0x1EE21, 0x1EE22, 0x1EE24, 0x1EE24, 0x1EE27, 0x1EE27, 0x1EE29, 0x1EE32, 0x1EE34, 0x1EE37,
        0x1EE39, 0x1EE39, 0x1EE3B, 0x1EE3B, 0x1EE42, 0x1EE42, 0x1EE47, 0x1EE47, 0x1EE49, 0x1EE49, 0x1EE4B, 0x1EE4B,
        0x1EE4D, 0x1EE4F, 0x1EE51, 0x1EE52, 0x1EE54, 0x1EE54, 0x1EE57, 0x1EE57, 0x1EE59, 0x1EE59, 0x1EE5B, 0x1EE5B,
        0x1EE5D, 0x1EE5D, 0x1EE5F, 0x1EE5F, 0x1EE61, 0x1EE62, 0x1EE64, 0x1EE64, 0x1EE67, 0x1EE6A, 0x1EE6C, 0x1EE72,
        0x1EE74, 0x1EE77, 0x1EE79, 0x1EE7C, 0x1EE7E, 0x1EE7E, 0x1EE80, 0x1EE89, 0x1EE8B, 0x1EE9B, 0x1EEA1, 0x1EEA3,
        0x1EEA5, 0x1EEA9, 0x1EEAB, 0x1EEBB, 0x20000, 0x2A6DF, 0x2A700, 0x2B739, 0x2B740, 0x2B81D, 0x2B820, 0x2CEA1,
        0x2CEB0, 0x2EBE0, 0x2EBF0, 0x2EE5D, 0x2F800, 0x2FA1D, 0x30000, 0x3134A, 0x31350, 0x323AF,
    ];
    static readonly int[] NumberRanges =
    [
        0x0030, 0x0039, 0x00B2, 0x00B3, 0x00B9, 0x00B9, 0x00BC, 0x00BE, 0x0660, 0x0669, 0x06F0, 0x06F9,
        0x07C0, 0x07C9, 0x0966, 0x096F, 0x09E6, 0x09EF, 0x09F4, 0x09F9, 0x0A66, 0x0A6F, 0x0AE6, 0x0AEF,
        0x0B66, 0x0B6F, 0x0B72, 0x0B77, 0x0BE6, 0x0BF2, 0x0C66, 0x0C6F, 0x0C78, 0x0C7E, 0x0CE6, 0x0CEF,
        0x0D58, 0x0D5E, 0x0D66, 0x0D78, 0x0DE6, 0x0DEF, 0x0E50, 0x0E59, 0x0ED0, 0x0ED9, 0x0F20, 0x0F33,
        0x1040, 0x1049, 0x1090, 0x1099, 0x1369, 0x137C, 0x16EE, 0x16F0, 0x17E0, 0x17E9, 0x17F0, 0x17F9,
        0x1810, 0x1819, 0x1946, 0x194F, 0x19D0, 0x19DA, 0x1A80, 0x1A89, 0x1A90, 0x1A99, 0x1B50, 0x1B59,
        0x1BB0, 0x1BB9, 0x1C40, 0x1C49, 0x1C50, 0x1C59, 0x2070, 0x2070, 0x2074, 0x2079, 0x2080, 0x2089,
        0x2150, 0x2182, 0x2185, 0x2189, 0x2460, 0x249B, 0x24EA, 0x24FF, 0x2776, 0x2793, 0x2CFD, 0x2CFD,
        0x3007, 0x3007, 0x3021, 0x3029, 0x3038, 0x303A, 0x3192, 0x3195, 0x3220, 0x3229, 0x3248, 0x324F,
        0x3251, 0x325F, 0x3280, 0x3289, 0x32B1, 0x32BF, 0xA620, 0xA629, 0xA6E6, 0xA6EF, 0xA830, 0xA835,
        0xA8D0, 0xA8D9, 0xA900, 0xA909, 0xA9D0, 0xA9D9, 0xA9F0, 0xA9F9, 0xAA50, 0xAA59, 0xABF0, 0xABF9,
        0xFF10, 0xFF19, 0x10107, 0x10133, 0x10140, 0x10178, 0x1018A, 0x1018B, 0x102E1, 0x102FB, 0x10320, 0x10323,
        0x10341, 0x10341, 0x1034A, 0x1034A, 0x103D1, 0x103D5, 0x104A0, 0x104A9, 0x10858, 0x1085F, 0x10879, 0x1087F,
        0x108A7, 0x108AF, 0x108FB, 0x108FF, 0x10916, 0x1091B, 0x109BC, 0x109BD, 0x109C0, 0x109CF, 0x109D2, 0x109FF,
        0x10A40, 0x10A48, 0x10A7D, 0x10A7E, 0x10A9D, 0x10A9F, 0x10AEB, 0x10AEF, 0x10B58, 0x10B5F, 0x10B78, 0x10B7F,
        0x10BA9, 0x10BAF, 0x10CFA, 0x10CFF, 0x10D30, 0x10D39, 0x10D40, 0x10D49, 0x10E60, 0x10E7E, 0x10F1D, 0x10F26,
        0x10F51, 0x10F54, 0x10FC5, 0x10FCB, 0x11052, 0x1106F, 0x110F0, 0x110F9, 0x11136, 0x1113F, 0x111D0, 0x111D9,
        0x111E1, 0x111F4, 0x112F0, 0x112F9, 0x11450, 0x11459, 0x114D0, 0x114D9, 0x11650, 0x11659, 0x116C0, 0x116C9,
        0x116D0, 0x116E3, 0x11730, 0x1173B, 0x118E0, 0x118F2, 0x11950, 0x11959, 0x11BF0, 0x11BF9, 0x11C50, 0x11C6C,
        0x11D50, 0x11D59, 0x11DA0, 0x11DA9, 0x11F50, 0x11F59, 0x11FC0, 0x11FD4, 0x12400, 0x1246E, 0x16130, 0x16139,
        0x16A60, 0x16A69, 0x16AC0, 0x16AC9, 0x16B50, 0x16B59, 0x16B5B, 0x16B61, 0x16D70, 0x16D79, 0x16E80, 0x16E96,
        0x1CCF0, 0x1CCF9, 0x1D2C0, 0x1D2D3, 0x1D2E0, 0x1D2F3, 0x1D360, 0x1D378, 0x1D7CE, 0x1D7FF, 0x1E140, 0x1E149,
        0x1E2F0, 0x1E2F9, 0x1E4F0, 0x1E4F9, 0x1E5F1, 0x1E5FA, 0x1E8C7, 0x1E8CF, 0x1E950, 0x1E959, 0x1EC71, 0x1ECAB,
        0x1ECAD, 0x1ECAF, 0x1ECB1, 0x1ECB4, 0x1ED01, 0x1ED2D, 0x1ED2F, 0x1ED3D, 0x1F100, 0x1F10C, 0x1FBF0, 0x1FBF9,
    ];
    // A code point is inside iff it is a bound or its insertion point falls after a range start (an odd index).
    static bool InSorted(int[] r, int cp) { var i = Array.BinarySearch(r, cp); return i >= 0 || (~i & 1) == 1; }
    // ponytail: V8 parity — /\p{Script=Latin}/u, /\p{L}/u, /\p{N}/u by range table.
    public static bool IsLatin(int cp) => InSorted(LatinRanges, cp);
    public static bool IsLetter(int cp) => InSorted(LetterRanges, cp);
    public static bool IsNumber(int cp) => InSorted(NumberRanges, cp);
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

    // String.prototype.indexOf / lastIndexOf: ordinal, `from` clamped to [0, length]; a lastIndexOf match starts at or before
    // `from` (.NET's LastIndexOf(value, startIndex) wants the match to END there, and both .NET forms throw past the end).
    public static int IndexOf(string s, string needle, int from) => s.IndexOf(needle, Math.Clamp(from, 0, s.Length), StringComparison.Ordinal);
    public static int LastIndexOf(string s, string needle, int from)
    {
        for (var i = Math.Min(Math.Max(from, 0), s.Length - needle.Length); i >= 0; i--)
            if (string.CompareOrdinal(s, i, needle, 0, needle.Length) == 0) return i;
        return -1;
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
    // SpecialCasing's 102 unconditional expansions (ß → SS, ligatures, Greek with ypogegrammeni), the table printed by Node v24, and
    // ı → I, which .NET's invariant casing leaves out. Checked against Node over every code point: the only differences left are 27
    // code points added in Unicode 16 (Garay U+10D70-10D85 and U+019B, U+0264, U+1C8A, U+A7CD, U+A7DB) that .NET does not map yet.
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
        ['ı'] = "I", ['ß'] = "SS", ['ŉ'] = "ʼN", ['ǰ'] = "J̌", ['ΐ'] = "Ϊ́", ['ΰ'] = "Ϋ́", ['և'] = "ԵՒ",
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
