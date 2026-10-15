using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Archify.Net;

// ponytail: V8 parity — the only file allowed to know how JavaScript formats, sorts and escapes.
public static partial class Js
{
    // Number.prototype.toString(): shortest round-trip digits, then ES2023 §6.1.6.1.20 layout.
    public static string Num(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsPositiveInfinity(v)) return "Infinity";
        if (double.IsNegativeInfinity(v)) return "-Infinity";
        if (v == 0) return "0";                                  // covers -0
        var neg = v < 0; if (neg) v = -v;
        var (digits, n) = ShortestDigits(v);                     // digits "123", n = position of decimal point (1e(n-1) ≤ v < 1e n)
        var k = digits.Length;
        string s;
        if (k <= n && n <= 21) s = digits + new string('0', n - k);
        else if (0 < n && n <= 21) s = digits[..n] + "." + digits[n..];
        else if (-6 < n && n <= 0) s = "0." + new string('0', -n) + digits;
        else { var e = n - 1; var mant = k == 1 ? digits : digits[0] + "." + digits[1..]; s = mant + "e" + (e >= 0 ? "+" : "-") + Math.Abs(e); }
        return neg ? "-" + s : s;
    }
    // "R" gives shortest round-trip in .NET Core 3.0+; parse mantissa/exponent out of its "E" form.
    static (string digits, int n) ShortestDigits(double v)
    {
        var r = v.ToString("R", CultureInfo.InvariantCulture);     // e.g. "1.5", "1E+21", "5E-324", "123456789.123"
        var ePos = r.IndexOf('E');
        var mant = ePos < 0 ? r : r[..ePos];
        var exp = ePos < 0 ? 0 : int.Parse(r[(ePos + 1)..], CultureInfo.InvariantCulture);
        var dot = mant.IndexOf('.');
        var intPart = dot < 0 ? mant : mant[..dot];
        var frac = dot < 0 ? "" : mant[(dot + 1)..];
        var digits = (intPart + frac).TrimStart('0');
        var leadingZeros = (intPart + frac).Length - digits.Length;
        var n = intPart.Length + exp - leadingZeros;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) { digits = "0"; n = 1; }
        return (digits, n);
    }

    public static double Round(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return v;
        var f = Math.Floor(v);
        var r = v - f >= 0.5 ? f + 1 : f;          // exact: 0.49999999999999994 → 0
        return r == 0 && v < 0 ? -0.0 : r;
    }

    // toFixed / toPrecision / toLocaleString work on the EXACT decimal value of the double (BigInteger ratio),
    // so 1.005.toFixed(2) === "1.00" (the double is 1.00499999…) exactly as V8 does.
    // Exact value of a finite double: v = m · 2^e → (numerator, denominator).
    static (BigInteger num, BigInteger den) Exact(double v)
    {
        var bits = BitConverter.DoubleToInt64Bits(Math.Abs(v));
        var e = (int)((bits >> 52) & 0x7FF); var m = bits & 0xFFFFFFFFFFFFFL;
        if (e == 0) e = 1; else m |= 1L << 52;
        e -= 1075;
        return e >= 0 ? (new BigInteger(m) << e, BigInteger.One) : (new BigInteger(m), BigInteger.One << -e);
    }
    // n = round(v · 10^f) with ties → larger n (ES toFixed/toPrecision: "if there are two such n, pick the larger n").
    static BigInteger ScaledHalfUp(double v, int f)
    {
        var (num, den) = Exact(v);
        if (f >= 0) num *= BigInteger.Pow(10, f); else den *= BigInteger.Pow(10, -f);
        var q = BigInteger.DivRem(num, den, out var rem);
        return rem * 2 >= den ? q + 1 : q;
    }
    public static string ToFixed(double v, int f)
    {   // ES2023 §21.1.3.3. V8: (-0.001).toFixed(2) === "-0.00" (sign kept whenever v < 0); (-0).toFixed(2) === "0.00".
        if (double.IsNaN(v)) return "NaN";
        if (Math.Abs(v) >= 1e21 || double.IsInfinity(v)) return Num(v);
        var n = ScaledHalfUp(v, f);
        var s = n.ToString(CultureInfo.InvariantCulture);
        if (f > 0) { s = s.PadLeft(f + 1, '0'); s = s[..^f] + "." + s[^f..]; }
        return v < 0 ? "-" + s : s;
    }
    public static string ToPrecision(double v, int p)
    {   // ES2023 §21.1.3.5
        if (double.IsNaN(v) || double.IsInfinity(v)) return Num(v);
        if (v == 0) return p == 1 ? "0" : "0." + new string('0', p - 1);
        var neg = v < 0; var a = Math.Abs(v);
        var e = (int)Math.Floor(Math.Log10(a));                 // may be off by one near powers of ten → correct below
        var n = ScaledHalfUp(a, p - 1 - e);
        if (n >= BigInteger.Pow(10, p)) { e++; n = ScaledHalfUp(a, p - 1 - e); }
        else if (n < BigInteger.Pow(10, p - 1)) { e--; n = ScaledHalfUp(a, p - 1 - e); }
        var digits = n.ToString(CultureInfo.InvariantCulture);
        string s;
        if (e < -6 || e >= p) s = (p == 1 ? digits : digits[0] + "." + digits[1..]) + "e" + (e >= 0 ? "+" : "-") + Math.Abs(e);
        else if (e == p - 1) s = digits;
        else if (e >= 0) s = digits[..(e + 1)] + "." + digits[(e + 1)..];
        else s = "0." + new string('0', -(e + 1)) + digits;
        return neg ? "-" + s : s;
    }
    public static string ToLocaleStringEnUs(double v, int? maximumSignificantDigits = null, int? maximumFractionDigits = null)
    {   // Intl.NumberFormat('en-US', …). The digit source is the SHORTEST round-trip decimal (ICU formats the JS Number's
        // decimal representation), NOT the exact binary value: 1.005 → "1.01", 2**60 → "1,152,921,504,606,847,000".
        // Rounding mode "halfExpand" on that decimal string; minimumFractionDigits 0; grouping ","; sign whenever
        // double.IsNegative (so -0 → "-0", -0.004 frac2 → "-0"). Only the two option shapes upstream uses are supported.
        if (double.IsNaN(v)) return "NaN"; if (double.IsInfinity(v)) return v > 0 ? "∞" : "-∞";
        var sign = double.IsNegative(v) ? "-" : "";
        if (v == 0) return sign + "0";
        var (digits, n) = ShortestDigits(Math.Abs(v));            // value = 0.digits × 10^n
        // keep = number of digits of `digits` to keep
        var keep = maximumSignificantDigits is int sig ? sig : n + (maximumFractionDigits ?? 3);
        if (keep < digits.Length)
        {
            var head = BigInteger.Parse(digits[..Math.Max(keep, 0)].PadLeft(1, '0'), CultureInfo.InvariantCulture);
            var roundUp = keep >= 0 && digits[keep] >= '5';      // halfExpand on the decimal string
            if (keep < 0) { head = 0; roundUp = false; }
            if (roundUp) head += 1;
            digits = head.ToString(CultureInfo.InvariantCulture);
            if (digits.Length > Math.Max(keep, 0)) n += 1;        // carry: 999.95 sig3 → 1000
            if (digits == "0") return sign + "0";
            digits = digits.TrimEnd('0'); if (digits.Length == 0) digits = "0";
        }
        string intPart, frac;
        if (n <= 0) { intPart = "0"; frac = new string('0', -n) + digits; }
        else if (n >= digits.Length) { intPart = digits + new string('0', n - digits.Length); frac = ""; }
        else { intPart = digits[..n]; frac = digits[n..]; }
        frac = frac.TrimEnd('0');
        return sign + Group(intPart) + (frac.Length > 0 ? "." + frac : "");
    }
    static string Group(string intDigits)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < intDigits.Length; i++) { if (i > 0 && (intDigits.Length - i) % 3 == 0) sb.Append(','); sb.Append(intDigits[i]); }
        return sb.ToString();
    }

    public static double Hypot(double a, double b)
    {   // V8 MathHypot: Infinity wins over NaN; scale by max; Kahan loop. For two arguments the loop reduces exactly to
        // sqrt(x*x + y*y) * max (verified 0/200 000 mismatches by the plan reviewer); the "sum - comp" variant does NOT.
        a = Math.Abs(a); b = Math.Abs(b);
        if (double.IsInfinity(a) || double.IsInfinity(b)) return double.PositiveInfinity;
        if (double.IsNaN(a) || double.IsNaN(b)) return double.NaN;
        var max = Math.Max(a, b); if (max == 0) return 0;
        var x = a / max; var y = b / max;
        return Math.Sqrt(x * x + y * y) * max;
    }

    public static void Sort<T>(List<T> list, Func<T, T, double> cmp)
    {   // ponytail: stable sort via OrderBy; equals V8 TimSort for every consistent comparator. Differs only when the
        // comparator returns NaN (inconsistent order) — schema-validated input gives finite keys. Port V8 TimSort
        // (binary insertion + galloping merge) if a parity golden ever shows it.
        // V8 comparator contract: the SIGN of the returned double decides; NaN counts as 0. Never truncate to int.
        var sorted = list.OrderBy(x => x, Comparer<T>.Create((p, q) => { var r = cmp(p, q); return double.IsNaN(r) ? 0 : Math.Sign(r); })).ToList();
        list.Clear(); list.AddRange(sorted);
    }
    public static void SortDefault(List<string> list) => Sort(list, (p, q) => string.CompareOrdinal(p, q));   // UTF-16 code units
    // JS Number(string): ES §7.1.4.1.1 StringToNumber. JS parseFloat: §19.2.4 — longest StrDecimalLiteral prefix.
    public static double ToNumber(string s)
    {
        var t = Trim(s);
        if (t.Length == 0) return 0;
        if (t.Length > 2 && t[0] == '0' && (t[1] | 0x20) is 'x' or 'b' or 'o')
        {
            var radix = (t[1] | 0x20) switch { 'x' => 16, 'b' => 2, _ => 8 };
            double acc = 0;
            foreach (var c in t[2..]) { var d = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'z' ? c - 'a' + 10 : c is >= 'A' and <= 'Z' ? c - 'A' + 10 : 99; if (d >= radix) return double.NaN; acc = acc * radix + d; }
            return acc;
        }
        var (value, length) = DecimalPrefix(t);
        return length == t.Length ? value : double.NaN;         // whole string must be a StrDecimalLiteral
    }
    public static double ParseFloat(string s)
    {
        var t = Trim(s);
        var (value, length) = DecimalPrefix(t);
        return length == 0 ? double.NaN : value;
    }
    // Longest prefix matching [+-]? (Infinity | digits [. digits?] | . digits) ([eE] [+-]? digits)? — returns (value, consumed chars).
    static (double value, int length) DecimalPrefix(string t)
    {
        var i = 0; var neg = false;
        if (i < t.Length && (t[i] == '+' || t[i] == '-')) { neg = t[i] == '-'; i++; }
        if (string.CompareOrdinal(t, i, "Infinity", 0, 8) == 0) return (neg ? double.NegativeInfinity : double.PositiveInfinity, i + 8);
        var start = i; var digits = 0;
        while (i < t.Length && t[i] is >= '0' and <= '9') { i++; digits++; }
        if (i < t.Length && t[i] == '.') { var j = i + 1; var frac = 0; while (j < t.Length && t[j] is >= '0' and <= '9') { j++; frac++; } if (digits + frac > 0) { i = j; digits += frac; } }
        if (digits == 0) return (double.NaN, 0);
        if (i < t.Length && (t[i] | 0x20) == 'e') { var j = i + 1; if (j < t.Length && (t[j] == '+' || t[j] == '-')) j++; var k = j; while (k < t.Length && t[k] is >= '0' and <= '9') k++; if (k > j) i = k; }
        var v = double.Parse(t[start..i], NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture);
        return (neg ? -v : v, i);
    }
    // String.prototype.localeCompare under V8's default `en`: ICU's `en` collation is CLDR root, which the invariant CompareInfo
    // also uses under ICU (sign-checked by test). With InvariantGlobalization on it compares ordinally (stated deviation, GLOB-001).
    public static int LocaleCompare(string a, string b) => CultureInfo.InvariantCulture.CompareInfo.Compare(a, b, CompareOptions.None);

    public static string Stringify(JsonNode? v, int indent = 0) { var sb = new StringBuilder(); Write(sb, v, indent, 0); return sb.ToString(); }
    static void Write(StringBuilder sb, JsonNode? v, int indent, int depth)
    {   // JSON.stringify: objects in insertion order (integer-like keys would come first in V8; the schemas forbid them),
        // numbers via Num, non-finite → "null", strings via Quote. indent > 0 → ES "gap" layout.
        switch (v)
        {
            case null: sb.Append("null"); return;
            case JsonArray arr:
                if (arr.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (indent > 0) sb.Append('\n').Append(' ', indent * (depth + 1));
                    Write(sb, arr[i], indent, depth + 1);
                }
                if (indent > 0) sb.Append('\n').Append(' ', indent * depth);
                sb.Append(']'); return;
            case JsonObject obj:
                if (obj.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{'); var first = true;
                foreach (var (key, val) in obj)
                {
                    if (!first) sb.Append(','); first = false;
                    if (indent > 0) sb.Append('\n').Append(' ', indent * (depth + 1));
                    sb.Append(Quote(key)).Append(':'); if (indent > 0) sb.Append(' ');
                    Write(sb, val, indent, depth + 1);
                }
                if (indent > 0) sb.Append('\n').Append(' ', indent * depth);
                sb.Append('}'); return;
            case JsonValue val:
                if (val.TryGetValue<string>(out var s)) { sb.Append(Quote(s)); return; }
                if (val.TryGetValue<bool>(out var b)) { sb.Append(b ? "true" : "false"); return; }
                var d = ToDouble(val);
                sb.Append(double.IsFinite(d) ? Num(d) : "null"); return;
        }
    }
    // ponytail: V8 parity — a JS number is a double, whatever CLR type backs the JsonValue (parsed JsonElement or a C# literal).
    internal static double ToDouble(JsonValue v) => v.TryGetValue<JsonElement>(out var el) ? el.GetDouble() : v.GetValue<object>() switch
    {
        double x => x, float x => x, decimal x => (double)x,
        int x => x, uint x => x, long x => x, ulong x => x, short x => x, ushort x => x, byte x => x, sbyte x => x,
        var o => throw new InvalidOperationException($"JsonValue of type {o.GetType()} is not a number."),
    };
    // ES2019 well-formed JSON.stringify: " \ and control chars escaped (short forms for \b \f \n \r \t), lone surrogates as \udxxx,
    // everything else — including < > & and non-ASCII — literal.
    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break; case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break; case '\f': sb.Append("\\f"); break; case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break; case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[++i]); }
                    else if (char.IsSurrogate(c)) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    // WhiteSpace + LineTerminator per ES: TAB VT FF SP NBSP ZWNBSP(U+FEFF) USP(Zs) LF CR LS PS. Not U+0085.
    public static bool IsSpace(int cp) => cp is 0x09 or 0x0B or 0x0C or 0x20 or 0xA0 or 0xFEFF or 0x0A or 0x0D or 0x2028 or 0x2029
        || (cp != 0x85 && cp <= 0xFFFF && CharUnicodeInfo.GetUnicodeCategory((char)cp) == UnicodeCategory.SpaceSeparator);
    public static string Trim(string s) { int i = 0, j = s.Length; while (i < j && IsSpace(s[i])) i++; while (j > i && IsSpace(s[j - 1])) j--; return s[i..j]; }
    public static string ReplaceFirst(string s, string search, Func<string, string> replacement)
    { var at = s.IndexOf(search, StringComparison.Ordinal); return at < 0 ? s : string.Concat(s.AsSpan(0, at), replacement(search), s.AsSpan(at + search.Length)); }
    public static string EscapeHtml(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
    // Graphemes / WordBreaks, Han tokenizers and the JS-whitespace splits live in JsText.cs.
}
