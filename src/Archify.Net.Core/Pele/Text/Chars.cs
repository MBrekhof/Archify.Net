namespace Archify.Net;

internal static partial class Pele
{
    // util/chars.ts: tests on UTF-16 code units, as the lexers read them with charCodeAt. Past the end of a string
    // charCodeAt is NaN, which is none of these: CharAt returns -1 for it.
    internal static class Chars
    {
        public static int CharAt(string s, int i) => (uint)i < (uint)s.Length ? s[i] : -1;

        // `\s` (chars.ts:5-18; the same set as Js.IsSpace).
        public static bool IsSpace(int c) => c >= 0 && Js.IsSpace(c);
        public static bool IsBlank(int c) => c is 32 or 9;
        // A line terminator: what `.` does not match.
        public static bool IsLineEnd(int c) => c is 10 or 13 or 0x2028 or 0x2029;
        public static bool IsDigit(int c) => c is >= 48 and <= 57;
        public static bool IsLetter(int c) => c is >= 65 and <= 90 or >= 97 and <= 122;
        public static bool IsHex(int c) => c is >= 48 and <= 57 or >= 65 and <= 70 or >= 97 and <= 102;
        // A character of `\w` (ASCII: no regex here has the `u` flag).
        public static bool IsWord(int c) => c is >= 48 and <= 57 or >= 65 and <= 90 or >= 97 and <= 122 or 95;

        // Regex classes for the patterns ported from Pele: JS `\s`, `\S`, `\w` and `.` (no `u` flag anywhere on the chart path).
        public const string Ws = Js.WsClass;
        public static readonly string NotWs = "[^" + Ws[1..];
        public const string Word = "[A-Za-z0-9_]";
        public const string Dot = "[^\n\r\u2028\u2029]";
    }
}
