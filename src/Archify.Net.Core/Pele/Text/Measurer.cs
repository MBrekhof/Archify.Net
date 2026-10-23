namespace Archify.Net;

internal static partial class Pele
{
    // text/measurer.ts Style (a const enum of bits).
    [Flags]
    internal enum Style { None = 0, Bold = 1, Italic = 2, Mono = 4 }

    // text/measurer.ts metricsMeasurer and text/metrics.ts. archify passes no measurer and Node has no canvas, so
    // defaultMeasurer() is always this table (measurer.ts:113-121); the canvas measurer is not ported (§2).
    internal static class Measurer
    {
        // Advance widths of printable ASCII in thousandths of an em, two base-36 digits each (metrics.ts, from Arial).
        const string Regular =
            "7q7q9vfgfgopij5b9999atg87q997q7qfgfgfgfgfgfgfgfgfgfg7q7qg8g8g8fgs7ijijk2k2ijgzlmk27qdwijfgn5k2lmijlmk2ijgzk2ijq8ijijgz7q7q7qd1fg99fgfgdwfgfg7qfgfg6666dw66n5fgfgfgfg99dw7qfgdwk2dwdwdw9a789ag8";
        const string BoldTable =
            "7q99d6fgfgopk26m9999atg87q997q7qfgfgfgfgfgfgfgfgfgfg9999g8g8g8gzr3k2k2k2k2ijgzlmk27qfgk2gzn5k2lmijlmk2ijgzk2ijq8ijijgz997q99g8fg99fggzfggzfg99gzgz7q7qfg7qopgzgzgzgzatfg99gzfglmfgfgdwat7satg8";

        static int[] Unpack(string packed) =>
            [.. Enumerable.Range(0, 95).Select(i => (int)Js.ParseInt(packed.Substring(i * 2, 2), 36))];

        static readonly int[] RegularUnits = Unpack(Regular), BoldUnits = Unpack(BoldTable);

        static bool IsWide(int c) =>
            c is >= 0x1100 and <= 0x115f or >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3 or >= 0xf900 and <= 0xfaff
                or >= 0xfe30 and <= 0xfe4f or >= 0xff00 and <= 0xff60 or >= 0xffe0 and <= 0xffe6 or >= 0xd800 and <= 0xdbff;

        static bool IsZeroWidth(int c) =>
            c is >= 0x300 and <= 0x36f or >= 0x200b and <= 0x200f or >= 0xdc00 and <= 0xdfff or >= 0xfe00 and <= 0xfe0f or 0xfeff;

        // Per UTF-16 code unit, as charCodeAt reads the text (measurer.ts:46-61).
        public static double Width(string text, double size, Style style)
        {
            var mono = (style & Style.Mono) != 0;
            var table = (style & Style.Bold) != 0 ? BoldUnits : RegularUnits;
            var units = 0;
            foreach (int c in text)
            {
                if (c < 32) continue;
                if (c < 127) units += mono ? 600 : table[c - 32];
                else if (IsZeroWidth(c)) continue;
                else if (IsWide(c)) units += mono ? 1200 : 1000;
                else units += mono ? 600 : 580;
            }
            return units * size / 1000;
        }
    }
}
