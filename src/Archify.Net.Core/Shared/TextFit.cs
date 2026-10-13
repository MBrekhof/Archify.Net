namespace Archify.Net;

// text-fit.mjs. Single-line node text fitting, shared by every renderer.
//
// Node text (`label`, `sublabel`, `tag`) renders as one <text> element with
// text-anchor="middle" and is never wrapped. Left unmeasured, an over-long
// value silently spills across its neighbours while validation still reports
// a clean receipt — the failure mode this module exists to close.
//
// Two halves, always used together:
//   - fittedNodeFontSize shrinks the text toward a legible minimum at render
//     time, so ordinary overruns simply get smaller instead of overlapping.
//   - minimumNodeTextWidth reports the width the text still needs once it has
//     shrunk as far as it may, so validation can reject what shrinking cannot
//     save.
//
// The geometry constants below are shared; the per-field `preferred` and
// `minimum` font sizes are not, because renderers set node text at different
// sizes (architecture sublabels are 9px, the rest are 7px).

// widthFactor: px of advance width per text unit, per px of font size.
// horizontalPadding: total px reserved inside the box so text never touches
// the border.
public static class NodeTextFit
{
    public const double WidthFactor = 0.6;
    public const double HorizontalPadding = 8;
}

public sealed record LabelRow(string Text, double Font, double Y);
public sealed record NodeLabelLayoutResult(double X, List<double> Ys, double SigilY, double SigilSize);

public static class TextFit
{
    // Largest font size at or below `preferred` that fits `text` inside `width`,
    // floored at `minimum` — below that the text is no longer legible and the
    // caller should be reporting a problem instead.
    public static double FittedNodeFontSize(string? text, double width, double preferred, double minimum)
    {
        var units = Math.Max(1, Utils.TextUnits(text));
        var available = Math.Max(1, width - NodeTextFit.HorizontalPadding);
        var fitted = Math.Min(preferred, available / (units * NodeTextFit.WidthFactor));
        return Math.Max(minimum, Math.Floor(fitted * 10) / 10);
    }

    // Width `text` occupies at its legible minimum. Compare against
    // `width - nodeTextFit.horizontalPadding` to decide whether shrink-to-fit can
    // rescue it.
    public static double MinimumNodeTextWidth(string? text, double minimum) => Utils.TextUnits(text) * minimum * NodeTextFit.WidthFactor;

    // Available text width inside a box of `width`.
    public static double AvailableNodeTextWidth(double width) => width - NodeTextFit.HorizontalPadding;

    // Adapted from Souptik Chakraborty's #220: shift only labels which reach a
    // corner icon. Unlike the original hard gate, a narrow valid node uses a
    // separate text row; its authored bounds and acceptance remain unchanged.
    public static NodeLabelLayoutResult NodeLabelLayout(double width, double height, IReadOnlyList<LabelRow> rows, string side = "left",
        bool brand = false, bool source = false, string step = "")
    {
        var x = width / 2;
        var labelWidth = MinimumNodeTextWidth(rows[0].Text, rows[0].Font);
        var stepEnd = step.Length > 0 ? (side == "left" ? 23 : 10) + MinimumNodeTextWidth(step, 8) + 3 : 0;
        var left = Math.Max(side == "left" ? Utils.SemanticSigilFootprint + 2 : 4, stepEnd);
        var right = width - (brand ? 26 : side == "right" ? Utils.SemanticSigilFootprint + 2 : 4)
            - (source ? Utils.SourceBadgeFootprint : 0);
        var originalYs = rows.Select(row => row.Y).ToList();
        if (x - labelWidth / 2 >= left && x + labelWidth / 2 <= right) return new(x, originalYs, Utils.SemanticSigilInset, Utils.SemanticSigilSize);
        if (labelWidth <= right - left)
        {
            // Round away from the icon, retaining the node centre whenever possible.
            x = Math.Min(Math.Floor((right - labelWidth / 2) * 10) / 10,
                Math.Max(x, Math.Ceiling((left + labelWidth / 2) * 10) / 10));
            return new(x, originalYs, Utils.SemanticSigilInset, Utils.SemanticSigilSize);
        }
        // Keep the existing font sizes and put the text below the decoration rail.
        // Conservative ascent/descent bounds also protect CJK and fallback fonts.
        double bottom = Math.Max(brand ? 22 : Utils.SemanticSigilFootprint, source ? 19 : 0);
        var ys = rows.Select(row =>
        {
            var y = Math.Max(row.Y, Math.Ceiling((bottom + 2 + row.Font * 1.2) * 10) / 10);
            bottom = y + row.Font * 0.3;
            return y;
        }).ToList();
        if (bottom <= height - 2) return new(x, ys, Utils.SemanticSigilInset, Utils.SemanticSigilSize);
        if (source)
        {
            // A source badge adds a second decoration on the right. On short boxes,
            // restoring the original rows would put the title back under that badge.
            // Try compact leading before giving up the dedicated text rail. Retain
            // every font size and the authored box; only this crowded fallback packs
            // the rows, with a full em above each baseline and 0.3 em below it.
            double compactBottom = Math.Max(brand ? 22 : Utils.SemanticSigilFootprint, 19) + 1;
            var compactYs = rows.Select(row =>
            {
                var y = Math.Ceiling((compactBottom + 1 + row.Font) * 10) / 10;
                compactBottom = y + row.Font * 0.3;
                return y;
            }).ToList();
            // The compact fallback may also use the otherwise reserved bottom
            // padding; the entire descent still stays inside the fixed box.
            if (compactBottom <= height - 0.5) return new(x, compactYs, Utils.SemanticSigilInset, Utils.SemanticSigilSize);
        }
        // A deliberately short fixed box may have no spare row. Preserve its text
        // and geometry, and fit only the decorative sigil in the space above it.
        return new(x, originalYs, 1, Math.Max(1, Math.Min(Utils.SemanticSigilSize, Math.Floor(rows[0].Y - rows[0].Font * 1.2 - 3))));
    }
}
