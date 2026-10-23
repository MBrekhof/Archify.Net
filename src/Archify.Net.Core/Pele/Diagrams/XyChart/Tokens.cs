namespace Archify.Net;

internal static partial class Pele
{
    internal static partial class XyChart
    {
        // diagrams/xychart/tokens.ts `const enum T`.
        internal static class T
        {
            public const int End = 0, Error = 1, Newline = 2, Title = 3, AccTitle = 4, AccTitleValue = 5, AccDescr = 6, AccDescrValue = 7,
                AccDescrMultilineValue = 8, XyChart = 9, ChartOrientation = 10, XAxis = 11, YAxis = 12, ArrowDelimiter = 13, Line = 14,
                Bar = 15, NumberWithDecimal = 16, SquareBracesStart = 17, SquareBracesEnd = 18, Str = 19, Alpha = 20, Colon = 21,
                Plus = 22, Comma = 23, Eq = 24, Mult = 25, Brkt = 26, Underscore = 27, Dot = 28, Amp = 29, Minus = 30, Num = 31,
                Semi = 32, Eof = 33;
        }

        // tokens.ts TOKEN_NAMES
        static readonly string[] TokenNames =
        [
            "$end", "INVALID", "NEWLINE", "title", "acc_title", "acc_title_value", "acc_descr", "acc_descr_value",
            "acc_descr_multiline_value", "XYCHART", "CHART_ORIENTATION", "X_AXIS", "Y_AXIS", "ARROW_DELIMITER", "LINE", "BAR",
            "NUMBER_WITH_DECIMAL", "SQUARE_BRACES_START", "SQUARE_BRACES_END", "STR", "ALPHA", "COLON", "PLUS", "COMMA", "EQUALS",
            "MULT", "BRKT", "UNDERSCORE", "DOT", "AMP", "MINUS", "NUM", "SEMI", "EOF",
        ];
    }
}
