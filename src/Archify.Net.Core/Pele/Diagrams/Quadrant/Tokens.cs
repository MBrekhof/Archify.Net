namespace Archify.Net;

internal static partial class Pele
{
    internal static partial class Quadrant
    {
        // diagrams/quadrant/tokens.ts `const enum T`.
        internal static class T
        {
            public const int End = 0, Error = 1, Newline = 2, Title = 3, TitleValue = 4, AccTitle = 5, AccTitleValue = 6, AccDescr = 7,
                AccDescrValue = 8, AccDescrMultilineValue = 9, XAxis = 10, YAxis = 11, Delimiter = 12, Quadrant1 = 13, Quadrant2 = 14,
                Quadrant3 = 15, Quadrant4 = 16, ClassDef = 17, MdStr = 18, Str = 19, ClassName = 20, PointStart = 21, PointX = 22,
                PointY = 23, QuadrantChart = 24, Alpha = 25, UnicodeText = 26, Colon = 27, Plus = 28, Comma = 29, Eq = 30, Mult = 31,
                Brkt = 32, Underscore = 33, Dot = 34, Amp = 35, Minus = 36, Num = 37, Space = 38, Semi = 39, Punctuation = 40, Eof = 41;
        }

        // tokens.ts TOKEN_NAMES
        static readonly string[] TokenNames =
        [
            "$end", "INVALID", "NEWLINE", "title", "title_value", "acc_title", "acc_title_value", "acc_descr", "acc_descr_value",
            "acc_descr_multiline_value", "X-AXIS", "Y-AXIS", "AXIS-TEXT-DELIMITER", "QUADRANT_1", "QUADRANT_2", "QUADRANT_3",
            "QUADRANT_4", "CLASSDEF", "MD_STR", "STR", "class_name", "point_start", "point_x", "point_y", "QUADRANT", "ALPHA",
            "UNICODE_TEXT", "COLON", "PLUS", "COMMA", "EQUALS", "MULT", "BRKT", "UNDERSCORE", "DOT", "AMP", "MINUS", "NUM", "SPACE",
            "SEMI", "PUNCTUATION", "EOF",
        ];
    }
}
