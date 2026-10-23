namespace Archify.Net;

internal static partial class Pele
{
    // diagrams/xychart/ticks.ts
    internal static partial class XyChart
    {
        // A step of 1, 2 or 5 times a power of ten that divides `span` into about `count` parts. A step below one comes back as
        // the negative of its inverse, which is a whole number, so that tick values are found by division and print without
        // rounding noise (0.3, not 0.30000000000000004).
        public static double TickStep(double span, double count)
        {
            var raw = span / Math.Max(count, 1);
            var power = Math.Floor(JsMath.Log10(raw));
            var unit = raw / JsMath.Pow(10, power);
            double factor = unit >= 7.07 ? 10 : unit >= 3.16 ? 5 : unit >= 1.41 ? 2 : 1;
            return power < 0 ? -JsMath.Pow(10, -power) / factor : factor * JsMath.Pow(10, power);
        }

        public static double TickIndex(double step, double value) => step > 0 ? value / step : value * -step;

        public static double TickValue(double step, double index) => step > 0 ? index * step : index / -step;

        // The multiples of a step from `lo` to `hi`, both included.
        public static List<double> TicksBetween(double lo, double hi, double step)
        {
            var first = Js.Round(TickIndex(step, lo));
            if (TickValue(step, first) < lo) first++;
            var last = Js.Round(TickIndex(step, hi));
            if (TickValue(step, last) > hi) last--;
            var output = new List<double>();
            for (var i = first; i <= last && output.Count < 100; i++) output.Add(TickValue(step, i));
            return output;
        }
    }
}
