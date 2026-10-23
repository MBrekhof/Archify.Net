namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/common/caseless.ts
    internal static class Caseless
    {
        // Whether `word`, written in lower case, is at `at`. Only ASCII letters match in either case, as with the `i` flag on a
        // regular expression without the `u` flag. Past the end, charCodeAt is NaN and matches nothing.
        public static bool WordAt(string src, int at, string word)
        {
            for (var k = 0; k < word.Length; k++)
            {
                var c = Chars.CharAt(src, at + k);
                int w = word[k];
                if (c != w && !(w is >= 97 and <= 122 && c == w - 32)) return false;
            }
            return true;
        }
    }
}
