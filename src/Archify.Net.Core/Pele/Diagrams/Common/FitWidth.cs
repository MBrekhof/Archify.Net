namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/common/fit-width.ts at archify's options: no `maxWidth` is passed (charts.mjs:28), so every helper returns
    // the natural size on its first draw and the redraw loops are dead code (§2). turnToFit/tighten are not used by the six
    // chart types and are not ported.
    internal static class FitWidth
    {
        // fit-width.ts:7-24: `available === undefined` returns the first draw.
        public static T Fit<T>(double natural, Func<double, bool, T> draw) => draw(natural, false);

        // fit-width.ts:27-30: the width a title may take before it wraps.
        public const double TitleRoom = 4000;

        // fit-width.ts:34-38
        public static double CapWidth(double natural) => natural;
    }
}
